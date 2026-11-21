using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace HolyLogger
{
    // WHAT HYBRID IS GIVEN, AS THE WINDOW GIVES IT SINCE 2026-10-21 (MixDump was the older feeding): Plain's letters
    // the moment it spells them (LetterTimed, raw) and its word gaps (WordGapTimed), the network's letters AND its
    // word gaps with their times (0.8 dit earlier, as the window corrects them), and each stretch it finished.
    // 18 s of silence at the end let the network finish. The network is read with CwBeamReading (BEAM, BONUS).
    // Writes "P<TAB>letter<TAB>s", "S<TAB> <TAB>NaN" (Plain's gap), "N<TAB>letter<TAB>s", "G<TAB> <TAB>s" (network gap),
    // "C<TAB>from<TAB>to".   MixDump2.exe <CwDeep.bin> <wav> <out.tsv>
    static class MixDump2
    {
        static int Main(string[] a)
        {
            string beam = Environment.GetEnvironmentVariable("BEAM"), bonus = Environment.GetEnvironmentVariable("BONUS");
            if (!string.IsNullOrEmpty(beam)) CwBeamReading.Beam = int.Parse(beam);
            if (!string.IsNullOrEmpty(bonus)) CwBeamReading.Bonus = double.Parse(bonus, CultureInfo.InvariantCulture);
            var net = new CwDeepNet(); string err;
            if (!net.Load(a[0], out err)) { Console.WriteLine(err); return 1; }
            byte[] raw = File.ReadAllBytes(a[1]);
            int rate = BitConverter.ToInt32(raw, 24);
            var plain = new CwDecoder(rate);
            var deep = new CwDeepDecoder(rate, net, false);
            var outp = new StringBuilder();
            Func<double> early = () => 0.8 * 1.2 / (plain.Wpm >= 5 ? plain.Wpm : 20);
            plain.LetterTimed += (l, s0, s1) => outp.AppendFormat(CultureInfo.InvariantCulture, "P\t{0}\t{1:F3}\n", l, s1 / (double)rate);
            plain.WordGapTimed += () => outp.Append("S\t \tNaN\n");
            deep.LetterTimed += (l, sec) =>
            {
                if (l == " ") outp.AppendFormat(CultureInfo.InvariantCulture, "G\t \t{0:F3}\n", sec);
                else outp.AppendFormat(CultureInfo.InvariantCulture, "N\t{0}\t{1:F3}\n", l, sec - early());
            };
            deep.Covered += (f, t) => outp.AppendFormat(CultureInfo.InvariantCulture, "C\t{0:F3}\t{1:F3}\n", f - early(), t - early());
            int block = rate / 10;
            var b = new short[block];
            long samples = (raw.Length - 44) / 2, silence = 18L * rate;
            for (long done = 0; done + block <= samples + silence; done += block)
            {
                for (int n = 0; n < block; n++) { long k = done + n; b[n] = k < samples ? BitConverter.ToInt16(raw, 44 + (int)(2 * k)) : (short)0; }
                plain.Process(b, block);
                deep.Process(b, block);
                deep.Note(plain.SignalPresent ? plain.ToneHz : 0);
            }
            File.WriteAllText(a[2], outp.ToString());
            return 0;
        }
    }
}
