using System;
using System.IO;
using System.Text;

namespace HolyLogger
{
    static class Log { public static void Swallow(Exception e) { } }

    // Counts what the signal test costs: frames called empty, and half-spelled letters thrown away
    // the moment it says so. Compare the thrown-away count with the letters that reached the screen.
    static class Probe
    {
        public static long Frames, Empty, Dropped, DroppedMarks;
        static bool _was = true;

        public static void Frame(bool present, int pending)
        {
            Frames++;
            if (!present) Empty++;
            if (_was && !present && pending > 0) { Dropped++; DroppedMarks += pending; }
            _was = present;
        }

        public static void Reset() { _was = true; }
    }

    static class Dropped
    {
        static void Main(string[] args)
        {
            foreach (string path in args)
                foreach (string file in Files(path))
                    Run(file);
        }

        static string[] Files(string path)
        {
            if (Directory.Exists(path)) return Directory.GetFiles(path, "*.wav", SearchOption.AllDirectories);
            return new[] { path };
        }

        static void Run(string file)
        {
            int rate;
            short[] audio = ReadWav(file, out rate);

            long framesWas = Probe.Frames, emptyWas = Probe.Empty;
            long dropWas = Probe.Dropped, markWas = Probe.DroppedMarks;
            Probe.Reset();

            var decoder = new CwDecoder(rate);
            var got = new StringBuilder();
            decoder.Text += s => got.Append(s);

            int block = rate / 10;
            var buf = new short[block];
            for (int i = 0; i < audio.Length; i += block)
            {
                int n = Math.Min(block, audio.Length - i);
                Array.Copy(audio, i, buf, 0, n);
                decoder.Process(buf, n);
            }

            string text = got.ToString().Replace("\r", " ").Replace("\n", " ");
            int letters = 0;
            foreach (char c in text) if (!char.IsWhiteSpace(c)) letters++;

            long frames = Probe.Frames - framesWas, empty = Probe.Empty - emptyWas;
            long drops = Probe.Dropped - dropWas, marks = Probe.DroppedMarks - markWas;

            Console.WriteLine("{0,-28} empty {1,5:F1}% of the time   letters printed {2,6}   half letters binned {3,5} ({4} elements)",
                Path.GetFileName(file), frames == 0 ? 0 : empty * 100.0 / frames, letters, drops, marks);
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
                    if (id == "fmt ")
                    {
                        r.ReadInt16(); channels = r.ReadInt16(); rate = r.ReadInt32();
                        r.ReadInt32(); r.ReadInt16(); bits = r.ReadInt16();
                        if (size > 16) r.ReadBytes(size - 16);
                    }
                    else if (id == "data")
                    {
                        byte[] raw = r.ReadBytes(size);
                        int samples = raw.Length / (bits / 8) / channels;
                        data = new short[samples];
                        for (int i = 0; i < samples; i++)
                            data[i] = BitConverter.ToInt16(raw, i * channels * (bits / 8));
                    }
                    else { if (size < 0 || f.Position + size > f.Length) break; r.ReadBytes(size); }
                }
                return data ?? new short[0];
            }
        }
    }
}
