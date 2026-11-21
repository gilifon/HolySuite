using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace HolyLogger
{
    // WHAT MIX (HYBRID) IS GIVEN, for a WAV (2026-10-21): Plain and the network run exactly as the decode window
    // runs them - 0.1 s blocks, Plain's note handed to the network after each block, Plain's printed letters timed
    // from its LetterTimed in order, the network's letters 0.8 dit earlier, and each stretch the network finished.
    // Writes <out>: "P<TAB>letter<TAB>seconds", "N<TAB>letter<TAB>seconds", "C<TAB>from<TAB>to".
    // deepnet/mix_truth.py rebuilds Mix from it and checks the grey letters against what was sent.
    //
    //   MixDump.exe <CwDeep.bin> <wav> <out.tsv>
    static class MixDump
    {
        static int Main(string[] a)
        {
            var net = new CwDeepNet(); string err;
            if (!net.Load(a[0], out err)) { Console.WriteLine(err); return 1; }
            byte[] raw = File.ReadAllBytes(a[1]);
            int rate = BitConverter.ToInt32(raw, 24);
            var plain = new CwDecoder(rate);
            var deep = new CwDeepDecoder(rate, net, false);
            deep.PitchHz = 500;
            var outp = new StringBuilder();
            var times = new List<KeyValuePair<string, double>>();
            Func<double> early = () => 0.8 * 1.2 / (plain.Wpm >= 5 ? plain.Wpm : 20);
            plain.LetterTimed += (l, s0, s1) => times.Add(new KeyValuePair<string, double>(l, s1 / (double)rate));
            plain.Text += text =>
            {
                for (int i = 0; i < text.Length; i++)
                {
                    char c = text[i];
                    if (c == ' ' || c == '\r' || c == '\n') { outp.Append("S\t \t0\n"); continue; }
                    string letter = c.ToString();
                    if (c == '<') { int close = text.IndexOf('>', i); if (close > i) { letter = text.Substring(i, close - i + 1); i = close; } }
                    int at = times.FindIndex(e => e.Key == letter);
                    if (at < 0) continue;
                    double t = times[at].Value;
                    times.RemoveRange(0, at + 1);
                    outp.AppendFormat(CultureInfo.InvariantCulture, "P\t{0}\t{1:F3}\n", letter, t);
                }
            };
            deep.LetterTimed += (l, sec) => { if (l != " ") outp.AppendFormat(CultureInfo.InvariantCulture, "N\t{0}\t{1:F3}\n", l, sec - early()); };
            deep.Covered += (f, t) => outp.AppendFormat(CultureInfo.InvariantCulture, "C\t{0:F3}\t{1:F3}\n", f - early(), t - early());
            int block = rate / 10;
            var b = new short[block];
            for (int i = 44; i + 2 * block <= raw.Length; )
            {
                for (int n = 0; n < block; n++) { b[n] = BitConverter.ToInt16(raw, i); i += 2; }
                plain.Process(b, block);
                deep.Process(b, block);
                deep.Note(plain.SignalPresent ? plain.ToneHz : 0);
            }
            File.WriteAllText(a[2], outp.ToString());
            return 0;
        }
    }
}
