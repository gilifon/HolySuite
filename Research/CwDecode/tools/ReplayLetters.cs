using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace HolyLogger
{
    static class Log { public static void Swallow(Exception e) { } }

    // THE DECODE WINDOW'S LETTERS, FROM A RECORDING - the first half of the replay tool (ReplayStrip.py
    // draws the pictures). Both readers are fed the recording 5 ms at a time, as live, and every letter
    // they print is written with where it belongs in the recording:
    //   New   - the middle of its own first and last mark, exactly (CwElementDecoder.LetterTimed)
    //   Plain - the same, from CwDecoder.LetterTimed (where its marks were when it spelled the letter)
    // Both decoders give the same answer every time on the same sound, so this shows exactly what the
    // window showed while he listened - and can be stepped through back and forth.
    //
    //   ReplayLetters.exe <recording.wav> <letters.tsv>
    static class ReplayLetters
    {
        static void Main(string[] args)
        {
            int rate;
            short[] audio = ReadWav(args[0], out rate);
            var plain = new CwDecoder(rate);
            var element = new CwElementDecoder(rate, false);
            var lines = new StringBuilder("reader\tletter\tsample\tstart\tend\n");
            var plainText = new StringBuilder();
            var newText = new StringBuilder();
            long fed = 0;

            plain.Text += s => plainText.Append(s);
            plain.LetterTimed += (letter, start, end) =>
                lines.AppendFormat(CultureInfo.InvariantCulture, "Plain\t{0}\t{1}\t{2}\t{3}\n", letter, (start + end) / 2, start, end);
            element.Text += s => newText.Append(s);
            element.LetterTimed += (letter, start, end) =>
                lines.AppendFormat(CultureInfo.InvariantCulture, "New\t{0}\t{1}\t{2}\t{3}\n", letter, (start + end) / 2, start, end);

            int block = Math.Max(1, rate / 200);       // 5 ms, so Plain's moment is known to 5 ms
            var buf = new short[block];
            for (int i = 0; i < audio.Length; i += block)
            {
                int n = Math.Min(block, audio.Length - i);
                Array.Copy(audio, i, buf, 0, n);
                fed = i + n;
                plain.Process(buf, n);
                element.Process(buf, n);
            }
            element.Finish();

            File.WriteAllText(args[1], lines.ToString());
            Console.WriteLine("PLAIN: " + plainText.ToString().Trim());
            Console.WriteLine("NEW:   " + newText.ToString().Trim());
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
                        for (int i = 0; i < count; i++) data[i] = BitConverter.ToInt16(bytes, i * (bits / 8) * channels);
                    }
                    f.Position = next;
                }
                return data ?? new short[0];
            }
        }
    }
}
