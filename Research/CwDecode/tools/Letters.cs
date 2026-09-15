using System;
using System.IO;
using System.Text;

namespace HolyLogger
{
    static class Log { public static void Swallow(Exception e) { } }

    // Every letter as it comes out, with the element lengths that made it and the dit/dah line they
    // were judged against. This is how to see WHY a letter came out wrong instead of guessing.
    static class Letters
    {
        static void Main(string[] args)
        {
            int rate;
            short[] audio = ReadWav(args[0], out rate);

            var plain = new CwDecoder(rate);
            double at = 0;

            plain.Text += s =>
            {
                if (string.IsNullOrWhiteSpace(s)) return;
                Console.WriteLine("{0,6:F1}s  {1,-6}  dit={2,4:F0} line={3,4:F0}   marks: {4}",
                    at, s, plain.LastDit, plain.LastBoundary, plain.LastMarks);
            };

            int block = rate / 100;
            var buf = new short[block];
            for (int i = 0; i < audio.Length; i += block)
            {
                int n = Math.Min(block, audio.Length - i);
                Array.Copy(audio, i, buf, 0, n);
                at = i / (double)rate;
                plain.Process(buf, n);
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
