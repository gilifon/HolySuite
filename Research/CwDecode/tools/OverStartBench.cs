using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace HolyLogger
{
    static class Log { public static void Swallow(Exception e) { } }

    // THE START-OF-OVER BENCH (2026-10-02). He saw New miss a clear CQ, and miss "TU EU4U" where Plain
    // read it: the first letters after a pause, when another station starts at another speed and note.
    // On real recordings nobody knows the sent text, so here it is generated: QSOs of two stations
    // taking turns, each with its own speed (14-36 WPM), note, strength and receiver tail (his IC-7610
    // trails ~50 ms after each mark), pauses of 0.8-4 s between overs. Every letter is known, so the
    // first letters of each over can be scored on their own.
    //
    // Scored, for Plain and New: callsigns read whole (anywhere in their over, spaces ignored), the
    // FIRST callsign of each over read whole, and wrong letters (edit distance) in each over's first
    // 3 s and in the rest.
    //
    //   OverStartBench.exe [sessions, default 12]
    static class OverStartBench
    {
        const int Rate = 8000;

        class Over { public string Text; public long Start, End; public double Note, Wpm; public List<string> Calls = new List<string>(); }

        static void Main(string[] args)
        {
            int sessions = args.Length > 0 ? int.Parse(args[0]) : 12;
            ElementSwitches(); BenchSwitches.Apply();
            if (Environment.GetEnvironmentVariable("OSB_SPEEDDEBUG") == "1") CwElementDecoder.SpeedDebug = m => Console.WriteLine(m);
            if (Environment.GetEnvironmentVariable("OSB_PATH") == "1") CwElementDecoder.PathDebug = m => Console.WriteLine(m);
            var score = new Dictionary<string, int[]>();     // reader -> calls, callsTotal, firstCalls, firstTotal, errEarly, lenEarly, errLate, lenLate
            foreach (string r in new[] { "Plain", "New" }) score[r] = new int[8];

            int only = Environment.GetEnvironmentVariable("OSB_SESSION") != null ? int.Parse(Environment.GetEnvironmentVariable("OSB_SESSION")) : -1;
            for (int s = 0; s < sessions; s++)
            {
                if (only >= 0 && s != only) continue;
                List<Over> overs;
                short[] audio = MakeSession(s, out overs);
                foreach (string reader in new[] { "Plain", "New" })
                {
                    var letters = new List<KeyValuePair<long, string>>();
                    if (reader == "Plain")
                    {
                        var d = new CwDecoder(Rate);
                        d.LetterTimed += (l, a, b) => letters.Add(new KeyValuePair<long, string>((a + b) / 2, l));
                        Feed(audio, (buf, n) => d.Process(buf, n));
                    }
                    else
                    {
                        var d = new CwElementDecoder(Rate, false);
                        d.LetterTimed += (l, a, b) => letters.Add(new KeyValuePair<long, string>((a + b) / 2, l));
                        long fed = 0; int oi = 0;
                        if (Environment.GetEnvironmentVariable("OSB_JUDGED") == "1")
                            d.Judged = (pat, end, ev, shown) => { if (shown || Environment.GetEnvironmentVariable("OSB_JUDGED_ALL") == "1") Console.WriteLine("      {0,7:F2}s {1,-8} ev {2,5:F2} {3}  steady {5:F2} beside {6:F1} fill {7:F2} plain {8}", end * 0.005, pat, ev, shown ? "shown" : "dropped", d.TimingNow, d.LastSteadiness, d.LastBeside, d.LastGapFill, d.LastJudgedPlainHeard); };
                        Feed(audio, (buf, n) =>
                        {
                            d.Process(buf, n); fed += n;
                            if (Environment.GetEnvironmentVariable("OSB_EVERY") == "1" && fed % (Rate / 2) < n)
                                Console.WriteLine("   {0,6:F1}s note {1:F0} wpm {2:F1} {3}", fed / (double)Rate, d.ToneHz, d.Wpm, d.TimingNow);
                            if (verbose && oi < overs.Count && fed >= overs[oi].Start + Rate)
                            {
                                Console.WriteLine("   over {0} (starts {6:F1}s) +1s: New note {1:F0} wpm {2:F1} | sent note {3:F0} wpm {4:F1}  {5}",
                                    oi, d.ToneHz, d.Wpm, overs[oi].Note, overs[oi].Wpm, d.TimingNow, overs[oi].Start / (double)Rate);
                                oi++;
                            }
                        });
                        d.Finish();
                    }
                    Score(overs, letters, score[reader], s, reader);
                }
            }

            foreach (var kv in score)
            {
                int[] v = kv.Value;
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "{0,-5}  calls {1}/{2}  first call {3}/{4}  wrong letters first 3 s {5}/{6} ({7:P0})  later {8}/{9} ({10:P0})",
                    kv.Key, v[0], v[1], v[2], v[3], v[4], v[5], v[4] / (double)Math.Max(1, v[5]), v[6], v[7], v[6] / (double)Math.Max(1, v[7])));
            }
        }

        static void Feed(short[] audio, Action<short[], int> process)
        {
            int block = Rate / 200;
            var buf = new short[block];
            for (int i = 0; i < audio.Length; i += block)
            {
                int n = Math.Min(block, audio.Length - i);
                Array.Copy(audio, i, buf, 0, n);
                process(buf, n);
            }
        }

        static bool verbose = Environment.GetEnvironmentVariable("OSB_VERBOSE") == "1";

        static void Score(List<Over> overs, List<KeyValuePair<long, string>> letters, int[] v, int session, string reader)
        {
            long early = 3 * Rate;
            foreach (Over o in overs)
            {
                var all = new StringBuilder(); var first = new StringBuilder(); var late = new StringBuilder();
                foreach (var l in letters)
                {
                    if (l.Key < o.Start - Rate / 4 || l.Key >= o.End + Rate / 4) continue;
                    all.Append(l.Value);
                    (l.Key < o.Start + early ? first : late).Append(l.Value);
                }
                string got = all.ToString();
                foreach (string c in o.Calls) { v[1]++; if (got.Contains(c)) v[0]++; }
                v[3]++;
                string firstWord = o.Text.Split(' ')[0];
                string firstCall = o.Calls[0];
                // the first callsign: it must be read in the over's opening letters
                int at = got.IndexOf(firstCall, StringComparison.Ordinal);
                int firstAt = o.Text.Replace(" ", "").IndexOf(firstCall, StringComparison.Ordinal);
                if (at >= 0 && at <= firstAt + 3) v[2]++;

                // the truth split the same way: letters sent in the first 3 s
                string sentEarly = SentBefore(o, o.Start + early), sentLate = o.Text.Replace(" ", "").Substring(sentEarly.Length);
                v[4] += Distance(sentEarly, first.ToString()); v[5] += sentEarly.Length;
                v[6] += Distance(sentLate, late.ToString()); v[7] += sentLate.Length;
                if (verbose) Console.WriteLine("{0,2} {1,-5} {2,-40} | {3}", session, reader, o.Text, got);
            }
        }

        // ---- the sessions ----

        static readonly Dictionary<string, long[]> letterTimes = new Dictionary<string, long[]>();

        static string SentBefore(Over o, long t)
        {
            long[] times = letterTimes[o.Start + ":" + o.Text];
            string flat = o.Text.Replace(" ", "");
            int n = 0;
            while (n < times.Length && times[n] < t) n++;
            return flat.Substring(0, n);
        }

        static short[] MakeSession(int seed, out List<Over> overs)
        {
            var rnd = new Random(1000 + seed);
            overs = new List<Over>();
            string a = Call(rnd), b = Call(rnd);
            var st = new[] { Station(rnd), Station(rnd) };
            if (Environment.GetEnvironmentVariable("OSB_TAIL0") == "1") { st[0].Tail = st[1].Tail = 0.002; }
            if (Environment.GetEnvironmentVariable("OSB_SAMEAMP") == "1") st[1].Amp = st[0].Amp;
            if (Environment.GetEnvironmentVariable("OSB_SAMESPEED") == "1") st[1].Wpm = st[0].Wpm;
            if (Environment.GetEnvironmentVariable("OSB_ONE") == "1") st[1] = st[0];
            if (Environment.GetEnvironmentVariable("OSB_TRUESPEED") == "1") CwElementDecoder.ForceWpm = st[0].Wpm;
            if (verbose) for (int q = 0; q < 2; q++)
                Console.WriteLine("  station {0}: {1:F1} WPM, {2:F0} Hz, {3:F1} dB, tail {4:F0} ms, jitter {5:P0}", q, st[q].Wpm, st[q].Note,
                    10 * Math.Log10(40 * st[q].Amp * st[q].Amp / (NoiseSigma * NoiseSigma)), st[q].Tail * 1000, st[q].Jitter);
            // a third of the sessions: the two notes the same, as when both are zero-beat
            if (seed % 3 == 0) st[1].Note = st[0].Note + (rnd.NextDouble() * 20 - 10);
            string[] texts =
            {
                "CQ CQ DE {0} {0} K",
                "{0} DE {1} {1} K",
                "{1} DE {0} TU 5NN 5NN BK",
                "{0} DE {1} R 599 599 TU {1} K",
                "{1} DE {0} TU 73 CQ DE {0} K",
                "{0} DE {1} 73 TU",
            };
            var sound = new List<double>();
            Silence(sound, 1.5 + rnd.NextDouble());
            for (int k = 0; k < texts.Length; k++)
            {
                var s = st[k % 2];
                string text = string.Format(texts[k], k % 2 == 0 ? a : b, k % 2 == 0 ? b : a);
                var o = new Over { Text = text, Start = sound.Count, Note = s.Note, Wpm = s.Wpm };
                // the calls in order of appearance
                foreach (string w in text.Split(' ')) if ((w == a || w == b) && !o.Calls.Contains(w)) o.Calls.Add(w);
                var times = new List<long>();
                Key(sound, text, s, rnd, times);
                o.End = sound.Count;
                letterTimes[o.Start + ":" + o.Text] = times.ToArray();
                overs.Add(o);
                Silence(sound, 0.8 + rnd.NextDouble() * 3.2);
            }
            Silence(sound, 2);

            // white noise, then the receiver's CW filter (500 Hz wide around 600 Hz) as two biquads
            var nr = new Random(7000 + seed);
            var x = sound.ToArray();
            for (int i = 0; i < x.Length; i++)
            {
                double u1 = 1 - nr.NextDouble(), u2 = nr.NextDouble();
                x[i] += NoiseSigma * Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
            }
            BandPass(x, 650, 500); BandPass(x, 650, 500);
            var outp = new short[x.Length];
            for (int i = 0; i < x.Length; i++) outp[i] = (short)Math.Max(-32000, Math.Min(32000, x[i] * 2));
            return outp;
        }

        const double NoiseSigma = 1000;

        class St { public double Wpm, Note, Amp, Tail, Jitter; }

        static St Station(Random rnd)
        {
            double db = 12 + rnd.NextDouble() * 18;                // in a 50 Hz band
            return new St
            {
                Wpm = 14 + rnd.NextDouble() * 22,
                Note = 480 + rnd.NextDouble() * 320,
                Amp = NoiseSigma * Math.Sqrt(Math.Pow(10, db / 10) / 40),
                Tail = 0.004 + rnd.NextDouble() * 0.012,
                Jitter = rnd.NextDouble() * 0.12,
            };
        }

        static string Call(Random rnd)
        {
            string[] pre = { "EU", "K", "W", "DL", "OK", "SP", "HA", "IK", "F", "G", "LZ", "R", "UA", "YO", "S5", "9A", "EA", "OH", "SM" };
            string p = pre[rnd.Next(pre.Length)];
            string letters = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
            var sb = new StringBuilder(p);
            sb.Append(rnd.Next(10));
            int n = 1 + rnd.Next(3);
            for (int i = 0; i < n; i++) sb.Append(letters[rnd.Next(26)]);
            return sb.ToString();
        }

        static void Silence(List<double> sound, double seconds)
        {
            int n = (int)(seconds * Rate);
            for (int i = 0; i < n; i++) sound.Add(0);
        }

        static void Key(List<double> sound, string text, St s, Random rnd, List<long> letterEnds)
        {
            double dit = 1.2 / s.Wpm;
            double phase = rnd.NextDouble() * 2 * Math.PI, step = 2 * Math.PI * s.Note / Rate;
            double env = 0;
            double rise = 1 - Math.Exp(-1 / (0.002 * Rate)), fall = 1 - Math.Exp(-1 / (s.Tail * Rate));
            Action<double, bool> emit = (units, on) =>
            {
                double sec = units * dit * (1 + (rnd.NextDouble() * 2 - 1) * s.Jitter);
                int n = (int)(sec * Rate);
                for (int i = 0; i < n; i++)
                {
                    env += ((on ? 1 : 0) - env) * (on ? rise : fall);
                    sound.Add(s.Amp * env * Math.Sin(phase));
                    phase += step;
                }
            };
            bool firstChar = true;
            foreach (char c in text)
            {
                if (c == ' ') { emit(7, false); firstChar = true; continue; }
                string pattern = null;
                foreach (var kv in CwDecoder.FromMorseTable) if (kv.Value == c.ToString()) { pattern = kv.Key; break; }
                if (pattern == null) continue;
                if (!firstChar) emit(3, false);
                firstChar = false;
                long startOf = sound.Count;
                for (int i = 0; i < pattern.Length; i++)
                {
                    if (i > 0) emit(1, false);
                    emit(pattern[i] == '-' ? 3 : 1, true);
                }
                letterEnds.Add((startOf + sound.Count) / 2);
            }
            emit(3, false);   // let the tail die
        }

        static void BandPass(double[] x, double centre, double width)
        {
            double w0 = 2 * Math.PI * centre / Rate, q = centre / width, alpha = Math.Sin(w0) / (2 * q);
            double b0 = alpha, b2 = -alpha, a0 = 1 + alpha, a1 = -2 * Math.Cos(w0), a2 = 1 - alpha;
            double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
            for (int i = 0; i < x.Length; i++)
            {
                double y = (b0 * x[i] + b2 * x2 - a1 * y1 - a2 * y2) / a0;
                x2 = x1; x1 = x[i]; y2 = y1; y1 = y; x[i] = y;
            }
        }

        static int Distance(string a, string b)
        {
            var d = new int[a.Length + 1, b.Length + 1];
            for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
            for (int j = 0; j <= b.Length; j++) d[0, j] = j;
            for (int i = 1; i <= a.Length; i++)
                for (int j = 1; j <= b.Length; j++)
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            return d[a.Length, b.Length];
        }

        static void ElementSwitches()
        {
            string v;
            if ((v = Environment.GetEnvironmentVariable("SPEED_FROM_PLAIN")) != null) CwElementDecoder.SpeedFromPlain = int.Parse(v);
            if ((v = Environment.GetEnvironmentVariable("REREAD")) != null) CwElementDecoder.ReReadOnSpeedChange = v == "1";
            if ((v = Environment.GetEnvironmentVariable("CONFIRM_SPEED")) != null) CwElementDecoder.ConfirmBigSpeedChange = v == "1";
            if ((v = Environment.GetEnvironmentVariable("SPEED_BY_PERIOD")) != null) CwElementDecoder.SpeedByPeriod = v == "1";
            if ((v = Environment.GetEnvironmentVariable("LEARN_TIMING")) != null) CwElementDecoder.LearnOperatorTiming = v == "1";
            if ((v = Environment.GetEnvironmentVariable("ADAPTIVE_LEAK")) != null) CwElementDecoder.AdaptiveLeak = v == "1";
            if ((v = Environment.GetEnvironmentVariable("TONE_LEAK")) != null) CwElementDecoder.Leak = double.Parse(v, CultureInfo.InvariantCulture);
            if ((v = Environment.GetEnvironmentVariable("LEAK_MARGIN")) != null) CwElementDecoder.LeakMargin = int.Parse(v);
            if ((v = Environment.GetEnvironmentVariable("WIDE_TIMING")) != null) CwElementDecoder.WideTiming = v == "1";
            if ((v = Environment.GetEnvironmentVariable("SPEED_FROM_EDGES")) != null) CwElementDecoder.SpeedFromEdges = v == "1";
            if ((v = Environment.GetEnvironmentVariable("STRONG_LEAK")) != null) CwElementDecoder.StrongLeak = double.Parse(v, CultureInfo.InvariantCulture);
            if ((v = Environment.GetEnvironmentVariable("STEADY_OFFSET")) != null) CwElementDecoder.SteadyAllowsOffset = v == "1";
            if ((v = Environment.GetEnvironmentVariable("FRESH_LEVELS")) != null) CwElementDecoder.FreshLevelsOnNewNote = v == "1";
            if ((v = Environment.GetEnvironmentVariable("GATE_MODE")) != null) CwElementDecoder.GateMode = int.Parse(v);
        }
    }
}
