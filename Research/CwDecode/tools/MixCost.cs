using System;
using System.Diagnostics;

namespace HolyLogger
{
    // HOW MUCH SCREEN-THREAD TIME HYBRID TAKES AS ITS TEXT GROWS (2026-10-21, the waterfall got jumpier the longer
    // it ran). Plain's letters go in as words, the network confirms them in 3 s stretches as in the decode window,
    // and the time per word is printed for every 100 words.   MixCost.exe <words>
    static class MixCost
    {
        [STAThread]
        static int Main(string[] a)
        {
            int words = int.Parse(a[0]);
            string[] pool = { "CQ", "DE", "4Z5SL", "5NN", "TU", "UR", "RST", "599", "NAME", "DAN", "QTH", "ISRAEL", "QSB", "73", "K", "DL1ABC", "TNX", "FER", "QSO" };
            var mix = new CwMixText();
            double t = 1, covered = 0;
            var clock = new Stopwatch();
            int n = 0;
            for (int w = 0; w < words; w++)
            {
                string word = pool[w % pool.Length];
                clock.Start();
                foreach (char c in word)
                {
                    mix.AddPlain(c.ToString(), t, 700, 20);
                    mix.AddNet(c.ToString(), t);
                    t += 0.3;
                }
                mix.AddPlain(" ", double.NaN, 700, 20);
                t += 0.4;
                if (t - covered > 3) { mix.Covered(covered, t - 0.5); covered = t - 0.5; }
                clock.Stop();
                if (++n % 100 == 0)
                {
                    Console.WriteLine(string.Format("words {0,5}: {1:F2} ms a word", n, clock.Elapsed.TotalMilliseconds / 100));
                    clock.Reset();
                }
            }
            return 0;
        }
    }
}
