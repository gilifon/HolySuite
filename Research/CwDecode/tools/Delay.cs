using System;
using System.IO;
using System.Text;

namespace HolyLogger
{
    static class Log { public static void Swallow(Exception e) { } }

    // HOW LONG BEFORE THE FIRST LETTER APPEARS? The decoder holds text back until it is sure it is
    // hearing Morse - which is what keeps noise off the screen - and the operator sees that as the
    // window staying blank for a few seconds after he tunes a station in. This measures the wait.
    static class Delay
    {
        static void Main(string[] args)
        {
            int rate;
            short[] audio = ReadWav(args[0], out rate);

            var plain = new CwDecoder(rate);
            double at = 0, firstSignal = -1, firstLetter = -1;
            var got = new StringBuilder();
            plain.Text += s => { if (firstLetter < 0 && !string.IsNullOrWhiteSpace(s)) firstLetter = at; got.Append(s); };

            int block = rate / 100;
            var buf = new short[block];
            for (int i = 0; i < audio.Length; i += block)
            {
                int n = Math.Min(block, audio.Length - i);
                Array.Copy(audio, i, buf, 0, n);
                at = i / (double)rate;
                plain.Process(buf, n);
                if (firstSignal < 0 && plain.SignalPresent) firstSignal = at;
            }

            string first = got.ToString().Trim();
            if (first.Length > 28) first = first.Substring(0, 28);

            Console.WriteLine("{0,-12} signal at {1,5:F1}s   first letter at {2,5:F1}s   WAIT {3,5:F1}s   \"{4}\"",
                Path.GetFileName(args[0]), firstSignal, firstLetter,
                firstLetter < 0 ? -1 : firstLetter - firstSignal, first);
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
