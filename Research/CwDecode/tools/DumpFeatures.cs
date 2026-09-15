using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace HolyLogger
{
    static class Log { public static void Swallow(Exception e) { } }

    // WHAT THE CTC NETWORK HEARS - one recipe, used for training AND for the real recordings.
    //
    // The network before this one was undone twice by being shown audio measured one way while it
    // learned and another way in service (a Hann taper where scipy uses a Tukey). So the features
    // are computed in exactly one place, this one, from the plain decoder's own readings via its
    // Reading event, and the same code will run inside the program. Python only ever trains on
    // what this writes.
    //
    // THE RECIPE, and it must not drift without retraining:
    //   1. Every 5 ms the decoder names the note (its best frequency) and measures every frequency.
    //   2. Take the loudness - the square root of the strength - at the note and the two frequencies
    //      either side, 25 Hz apart: five numbers. The neighbours are what let a network tell a
    //      station from a station next door, which one number never could.
    //   3. Average two readings into one frame: a frame every 10 ms.
    //   4. Take the logarithm, and subtract the NOISE FLOOR - the quietest fifth of the note's own
    //      loudness over the last three seconds. So 0 is the band, 1 is ten times louder than the
    //      band, 2 is a hundred times.
    //
    // STEP 4 IS THE LESSON OF THE LAST NETWORK. Its front end divided by the loudest thing heard
    // recently, so during a quiet stretch plain band noise was blown up to full scale and the
    // network called it a signal. Here the band sits at zero however long it is quiet, and only a
    // real signal stands above it.
    //
    // Output: a binary file of examples - name, text (empty for a real recording), frame count,
    // five features per frame as float32. Python reads it with struct.
    static class DumpFeatures
    {
        public const int Bins = 5;                 // the note and two either side
        const int ReadingsPerFrame = 2;            // 5 ms readings -> 10 ms frames
        const int FloorFrames = 300;               // three seconds
        const double FloorShare = 0.2;             // the quietest fifth

        static void Main(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("DumpFeatures out.bin index.txt wavfolder     (training: name<TAB>text per line)");
                Console.WriteLine("DumpFeatures out.bin --wav a.wav b.wav ...   (real recordings, no text)");
                return;
            }

            var jobs = new List<string[]>();   // path, text, marks
            if (args[1] == "--wav")
            {
                for (int i = 2; i < args.Length; i++) jobs.Add(new[] { args[i], string.Empty, string.Empty });
            }
            else
            {
                string folder = args[2];
                foreach (string line in File.ReadAllLines(args[1]))
                {
                    string[] bits = line.Split('\t');
                    if (bits.Length < 2) continue;
                    jobs.Add(new[] { Path.Combine(folder, bits[0] + ".wav"), bits[1], bits.Length > 6 ? bits[6] : string.Empty });
                }
            }

            int done = 0;
            using (var f = File.Create(args[0]))
            using (var w = new BinaryWriter(f))
            {
                // 02: each example now carries a label for every frame after its features - see Labels.
                w.Write(Encoding.ASCII.GetBytes("CWFEAT02"));
                w.Write(jobs.Count);
                w.Write(Bins);

                foreach (var job in jobs)
                {
                    float[] frames = Features(job[0]);
                    int count = frames.Length / Bins;
                    byte[] name = Encoding.UTF8.GetBytes(Path.GetFileNameWithoutExtension(job[0]));
                    byte[] text = Encoding.UTF8.GetBytes(job[1] ?? string.Empty);
                    w.Write(name.Length); w.Write(name);
                    w.Write(text.Length); w.Write(text);
                    w.Write(count);
                    foreach (float v in frames) w.Write(v);

                    byte[] labels = Labels(job[2], count);
                    w.Write(labels.Length);
                    w.Write(labels);
                    done++;
                }
            }
            Console.WriteLine("wrote features for " + done + " recordings");
        }

        // The answer for every frame, from the generator's marks: "nothing" everywhere except where a
        // letter or a word gap was named. Empty for a real recording, which has no marks.
        //
        // MUST MATCH TrainCtc.py's ALPHABET: class 0 is nothing, class i+1 is Alphabet[i], and "_"
        // in a mark is the space.
        //
        // A SAMPLE BECOMES A FRAME at (sample - 100) / 80. The decoder takes a reading every 40
        // samples once its 160-sample window is full, and two readings make a frame, so frame j is
        // centred about 100 + 80j samples in. The labels sit a good fraction of a dit past the letter,
        // so being a frame out either way changes nothing - but it is written down, not assumed, and
        // LookAtLabels shows it against the audio.
        const string Alphabet = " ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789/=?.,";

        static byte[] Labels(string marks, int frames)
        {
            if (string.IsNullOrWhiteSpace(marks)) return new byte[0];
            var labels = new byte[frames];

            foreach (string mark in marks.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int at = mark.IndexOf('@'), plus = mark.IndexOf('+');
                if (at <= 0 || plus <= at) continue;
                string what = mark.Substring(0, at);
                int start = int.Parse(mark.Substring(at + 1, plus - at - 1), System.Globalization.CultureInfo.InvariantCulture);
                int length = int.Parse(mark.Substring(plus + 1), System.Globalization.CultureInfo.InvariantCulture);

                int cls = what == "_" ? 1 : Alphabet.IndexOf(what[0]) + 1;
                if (cls <= 0) continue;

                int from = (int)Math.Round((start - 100) / 80.0);
                int to = (int)Math.Round((start + length - 100) / 80.0);
                if (to <= from) to = from + 1;
                for (int j = Math.Max(0, from); j < Math.Min(frames, to); j++) labels[j] = (byte)cls;
            }
            return labels;
        }

        public static float[] Features(string path)
        {
            int rate;
            short[] audio = ReadWav(path, out rate);

            var decoder = new CwDecoder(rate);
            var frames = new List<float>();

            var sum = new double[Bins];
            int readings = 0;
            var floorWindow = new double[FloorFrames];
            var sorted = new double[FloorFrames];
            int floorCount = 0, floorNext = 0;

            decoder.Reading += (best, power) =>
            {
                for (int k = 0; k < Bins; k++)
                {
                    int b = best + k - Bins / 2;
                    if (b < 0) b = 0;
                    if (b >= power.Length) b = power.Length - 1;
                    sum[k] += Math.Sqrt(Math.Max(0, power[b]));
                }
                if (++readings < ReadingsPerFrame) return;

                var logs = new double[Bins];
                for (int k = 0; k < Bins; k++)
                {
                    logs[k] = Math.Log10(sum[k] / readings + 1e-9);
                    sum[k] = 0;
                }
                readings = 0;

                floorWindow[floorNext] = logs[Bins / 2];
                floorNext = (floorNext + 1) % FloorFrames;
                if (floorCount < FloorFrames) floorCount++;
                Array.Copy(floorWindow, sorted, floorCount);
                Array.Sort(sorted, 0, floorCount);
                double floor = sorted[(int)(floorCount * FloorShare)];

                for (int k = 0; k < Bins; k++)
                {
                    double v = logs[k] - floor;
                    if (v < -1) v = -1;
                    if (v > 3) v = 3;
                    frames.Add((float)v);
                }
            };

            int block = rate / 10;
            var buf = new short[block];
            for (int i = 0; i < audio.Length; i += block)
            {
                int n = Math.Min(block, audio.Length - i);
                Array.Copy(audio, i, buf, 0, n);
                decoder.Process(buf, n);
            }
            return frames.ToArray();
        }

        static short[] ReadWav(string path, out int rate)
        {
            using (var f = File.OpenRead(path))
            using (var r = new BinaryReader(f))
            {
                r.ReadBytes(4); r.ReadInt32(); r.ReadBytes(4);
                rate = 8000; short channels = 1, bits = 16; short[] data = null;
                while (f.Position < f.Length - 8)
                {
                    string id = new string(r.ReadChars(4));
                    int size = r.ReadInt32();
                    long next = f.Position + size + (size & 1);
                    if (id == "fmt ")
                    {
                        r.ReadInt16(); channels = r.ReadInt16(); rate = r.ReadInt32();
                        r.ReadInt32(); r.ReadInt16(); bits = r.ReadInt16();
                    }
                    else if (id == "data")
                    {
                        var bytes = r.ReadBytes(size);
                        int count = size / (bits / 8) / channels;
                        data = new short[count];
                        for (int i = 0; i < count; i++)
                            data[i] = BitConverter.ToInt16(bytes, i * (bits / 8) * channels);
                    }
                    f.Position = next;
                }
                return data ?? new short[0];
            }
        }
    }
}
