using System;
using System.IO;

namespace HolyLogger
{
    static class Log { public static void Swallow(Exception e) { } }

    // HOW OFTEN IS A MARK OR A GAP A LENGTH NO OPERATOR CAN HAVE SENT?
    //
    // Morse is built from two mark lengths (1 and 3 dits) and three gap lengths (1, 3 and 7). Two
    // elements welded together by a threshold that never dipped make lengths that are NOT in that
    // list: dah+gap+dit is a 5-dit mark, dah+gap+dah is 7, and a gap hiding one dit inside it is
    // 3 or 5 or 9 dits long. The 5-dit ones sit in a valley where nothing real lives.
    //
    // This counts what actually arrives, in units of the dit the decoder had learned at the time,
    // so we know whether hunting for welded elements is worth anything before writing the hunt.
    static class Probe
    {
        public const double Step = 0.25;     // bucket width, in dits
        public const int Buckets = 48;       // up to 12 dits
        public static int[] MarkBuckets = new int[Buckets];
        public static int[] GapBuckets = new int[Buckets];
        public static int Marks, Gaps;

        public static void Mark(double ms, double dit)
        {
            if (dit <= 0) return;
            Marks++;
            MarkBuckets[Bucket(ms / dit)]++;
        }

        public static void Gap(double ms, double dit)
        {
            if (dit <= 0) return;
            Gaps++;
            GapBuckets[Bucket(ms / dit)]++;
        }

        static int Bucket(double dits)
        {
            int b = (int)(dits / Step);
            if (b < 0) b = 0;
            if (b >= Buckets) b = Buckets - 1;
            return b;
        }
    }

    static class Impossible
    {
        static void Main(string[] args)
        {
            foreach (string path in args)
                foreach (string file in Files(path))
                    Run(file);

            Console.WriteLine();
            Console.WriteLine("MARKS ({0})", Probe.Marks);
            Show(Probe.MarkBuckets, Probe.Marks);
            Console.WriteLine();
            Console.WriteLine("GAPS ({0})", Probe.Gaps);
            Show(Probe.GapBuckets, Probe.Gaps);
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
            var decoder = new CwDecoder(rate);
            decoder.Text += s => { };

            int block = rate / 10;
            var buf = new short[block];
            for (int i = 0; i < audio.Length; i += block)
            {
                int n = Math.Min(block, audio.Length - i);
                Array.Copy(audio, i, buf, 0, n);
                decoder.Process(buf, n);
            }
        }

        static void Show(int[] buckets, int total)
        {
            if (total == 0) return;
            for (int b = 0; b < buckets.Length; b++)
            {
                if (buckets[b] == 0) continue;
                double pct = buckets[b] * 100.0 / total;
                Console.WriteLine("{0,5:F2} - {1,5:F2} dits  {2,6}  {3,5:F2}%  {4}",
                    b * Probe.Step, (b + 1) * Probe.Step, buckets[b], pct,
                    new string('#', (int)Math.Round(pct)));
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
