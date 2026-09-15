using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace HolyLogger
{
    static class Log { public static void Swallow(Exception e) { } }

    // IS THE MADE-UP AUDIO AS HARD AS THE REAL THING?
    //
    // A training set that is easier than the band teaches a network to read signals nobody has. The
    // only honest check available is to run the PLAIN decoder over both and compare how well it
    // does: it is the same decoder either way, so if it finds the made-up audio much easier, the
    // audio is too kind and the whole exercise is worthless.
    //
    // Scored character by character - how many of the sent characters appear, in order, in what came
    // out. Not a word count: with the text known there is no need to fall back on whole words.
    static class ScoreTraining
    {
        static void Main(string[] args)
        {
            string folder = Path.Combine(args.Length > 0 ? args[0] : ".", "training");
            string index = Path.Combine(folder, "index.txt");
            if (!File.Exists(index)) { Console.WriteLine("no index.txt in " + folder); return; }

            double totalSent = 0, totalFound = 0;
            int easy = 0, hard = 0, hopeless = 0, n = 0;

            foreach (string line in File.ReadAllLines(index))
            {
                string[] bits = line.Split('\t');
                if (bits.Length < 5) continue;

                string wav = Path.Combine(folder, bits[0] + ".wav");
                if (!File.Exists(wav)) continue;

                string sent = Flatten(bits[1]);
                string got = Flatten(Decode(wav));

                double share = sent.Length == 0 ? 0 : InOrder(sent, got) / (double)sent.Length;
                totalSent += sent.Length;
                totalFound += InOrder(sent, got);
                n++;

                if (share >= 0.9) easy++;
                else if (share >= 0.4) hard++;
                else hopeless++;

                if (n <= 8)
                    Console.WriteLine("{0}  {1,5:P0}  {2} WPM {3} Hz strength {4}\n     sent: {5}\n     got : {6}",
                        bits[0], share, bits[2], bits[3], bits[4], bits[1], Squash(Decode(wav)));
            }

            Console.WriteLine();
            Console.WriteLine("{0} examples: {1} nearly perfect (90%+), {2} partly readable, {3} hopeless (<40%)",
                n, easy, hard, hopeless);
            Console.WriteLine("characters found in order: {0:P1}", totalSent == 0 ? 0 : totalFound / totalSent);
        }

        // How many of the sent characters turn up, in the right order, in what came out - the longest
        // run that appears in both, allowing gaps on either side.
        //
        // THE GREEDY VERSION WAS WRONG AND FLATTERED NOTHING - it under-read badly. Scanning forward
        // for each sent character in turn, one wrong letter at the front sends the scan past the
        // right answer and everything after it is scored as missing: "EA0VW DE G0FWM R R TNX FER
        // CALL UR RST 579 579" came out with a single letter wrong, T for E, and scored 31%. The E
        // was matched against the E of "DE" further along, and the whole rest of the line fell over.
        // Nothing about the decoder was being measured by that, only the shape of the scan.
        static int InOrder(string sent, string got)
        {
            if (sent.Length == 0 || got.Length == 0) return 0;

            // One row at a time: this runs over every example in the set and the full table would be
            // megabytes for a long over.
            var previous = new int[got.Length + 1];
            var current = new int[got.Length + 1];

            for (int i = 1; i <= sent.Length; i++)
            {
                for (int j = 1; j <= got.Length; j++)
                {
                    if (sent[i - 1] == got[j - 1]) current[j] = previous[j - 1] + 1;
                    else current[j] = current[j - 1] > previous[j] ? current[j - 1] : previous[j];
                }
                var swap = previous; previous = current; current = swap;
                Array.Clear(current, 0, current.Length);
            }

            return previous[got.Length];
        }

        static string Flatten(string s)
        {
            var t = new StringBuilder();
            foreach (char c in (s ?? string.Empty).ToUpperInvariant())
                if (char.IsLetterOrDigit(c)) t.Append(c);
            return t.ToString();
        }

        static string Squash(string s)
        {
            var t = new StringBuilder();
            bool space = true;
            foreach (char c in s)
            {
                bool blank = char.IsWhiteSpace(c);
                if (blank && space) continue;
                t.Append(blank ? ' ' : c);
                space = blank;
            }
            string one = t.ToString().Trim();
            return one.Length > 90 ? one.Substring(0, 90) + " ..." : one;
        }

        static string Decode(string path)
        {
            int rate;
            short[] audio = ReadWav(path, out rate);
            var plain = new CwDecoder(rate);
            var got = new StringBuilder();
            plain.Text += s => got.Append(s);
            int block = rate / 10;
            var buf = new short[block];
            for (int i = 0; i < audio.Length; i += block)
            {
                int c = Math.Min(block, audio.Length - i);
                Array.Copy(audio, i, buf, 0, c);
                plain.Process(buf, c);
            }
            return got.ToString();
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
