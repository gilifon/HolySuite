using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Text;

namespace HolyLogger
{
    // WHERE DO THE NETWORK'S LETTERS GO? (2026-10-29) On the weak hand-sent fist1 recordings the network alone reads
    // about twice what Hybrid shows. Runs one WAV as tools/HybridText does and prints every network letter handed
    // to Mix, every Covered stretch, and at the end every entry Mix holds (text, time, from the network, grey).
    //   HybridDebug.exe <CwDeep.bin> <wav>
    static class HybridDebug
    {
        [STAThread]
        static int Main(string[] a)
        {
            CwBeamReading.Beam = 8; CwBeamReading.Bonus = 2;
            var net = new CwDeepNet(); string err;
            if (!net.Load(a[0], out err)) { Console.WriteLine(err); return 1; }
            byte[] raw = File.ReadAllBytes(a[1]);
            int rate = BitConverter.ToInt32(raw, 24);
            var plain = new CwDecoder(rate);
            var deep = new CwDeepDecoder(rate, net, false);
            var mix = new CwMixText();
            double note = 0, speed = 0;
            Func<double> early = () => 0.8 * 1.2 / (plain.Wpm >= 5 ? plain.Wpm : 20);
            plain.LetterTimed += (l, s0, s1) => mix.AddPlain(l, s1 / (double)rate, note, speed);
            plain.WordGapTimed += () => mix.AddPlain(" ", double.NaN, note, speed);
            deep.LetterTimed += (l, sec) => { Console.WriteLine("NET\t{0}\t{1:F2}", l, sec); mix.AddNet(l, l == " " ? sec : sec - early()); };
            deep.Covered += (f, t) => { Console.WriteLine("COV\t{0:F2}\t{1:F2}\t{2:F3}", f, t, early()); mix.Covered(f - early(), t - early()); };
            int block = rate / 10;
            var b = new short[block];
            long samples = (raw.Length - 44) / 2, silence = 18L * rate;
            for (long done = 0; done + block <= samples + silence; done += block)
            {
                for (int n = 0; n < block; n++) { long k = done + n; b[n] = k < samples ? BitConverter.ToInt16(raw, 44 + (int)(2 * k)) : (short)0; }
                plain.Process(b, block);
                note = plain.SignalPresent ? plain.ToneHz : 0;
                speed = plain.SignalPresent ? plain.Wpm : 0;
                deep.Process(b, block);
                deep.Note(note);
            }
            var list = (IList)typeof(CwMixText).GetField("_entries", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(mix);
            foreach (object e in list)
            {
                Type t = e.GetType();
                Func<string, object> f = n => t.GetField(n).GetValue(e);
                var run = (System.Windows.Documents.Run)f("Run");
                Console.WriteLine("ENT\t{0}\t{1:F2}\t{2}\t{3}\t{4}\t{5}", f("Text"), f("T"), f("FromNet"), f("Doubted"), run.Text, run.Parent != null);
            }
            return 0;
        }
    }
}
