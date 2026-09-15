using System;
using System.IO;
using System.Text;

namespace HolyLogger
{
    static class Log { public static void Swallow(Exception e) { } }

    // Does the decoder print while nothing is being sent?
    //
    // Feeds a recording through the plain decoder a tenth of a second at a time and prints, for
    // every tenth, how loud the audio actually is and what came out. A line with a loud reading is
    // a station; a line with a quiet reading and text on it is the fault we are looking for.
    static class Quiet
    {
        static void Main(string[] args)
        {
            int rate;
            short[] audio = ReadWav(args[0], out rate);

            var plain = new CwDecoder(rate);
            var got = new StringBuilder();
            plain.Text += s => got.Append(s);

            int block = rate / 10;
            var buf = new short[block];

            // The quietest tenth in the whole recording is the yardstick for "nothing there".
            double loudest = 0;
            for (int i = 0; i < audio.Length; i++) loudest = Math.Max(loudest, Math.Abs((double)audio[i]));

            for (int i = 0; i < audio.Length; i += block)
            {
                int n = Math.Min(block, audio.Length - i);
                Array.Copy(audio, i, buf, 0, n);

                double sum = 0;
                for (int k = 0; k < n; k++) sum += (double)buf[k] * buf[k];
                double rms = Math.Sqrt(sum / n);

                got.Clear();
                plain.Process(buf, n);

                string text = got.ToString().Replace("\r", "").Replace("\n", "");
                double seconds = i / (double)rate;

                if (text.Length > 0 || (i / block) % 10 == 0)
                    Console.WriteLine("{0,6:F1}s  rms {1,7:F0}  ({2,5:F1}% of peak)  signal={3}  {4}",
                        seconds, rms, rms * 100.0 / loudest, plain.SignalPresent ? "Y" : "n", text);
            }
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
