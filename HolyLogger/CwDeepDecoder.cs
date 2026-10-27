using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace HolyLogger
{
    // OUR NETWORK AS A LIVE READER (2026-10-16, on test - not for release). See CwDeepNet.
    //
    // The sound is brought down to 3200 a second, and every StrideSeconds the last 15 s are read as one
    // window. The network uses the seconds AFTER a letter to read it, so a letter is only printed once it
    // is LagSeconds old - the text runs that far behind the sound, on purpose: it is the price of reading
    // whole stretches instead of one element at a time. Each window prints only the stretch
    // [last printed, now - LagSeconds), so nothing is printed twice and nothing is skipped.
    //
    // The spectrogram is computed exactly as the training's (deepnet/common.py): 256-point Hann window
    // every 48 samples, reflect padding, 300-1100 Hz, each note divided by its own 25th percentile over
    // the window, then log(1 + x).
    //
    // TOLD WHICH STATION TO READ (2026-10-19, a network file "CWD2"). With three or four stations inside the
    // receiver's filter at once the network used to write a mixture of them, where Plain locks onto one. So
    // Plain's note is handed in (Note, after every block of sound) and every frame's 65 notes are taken around
    // it, the followed station always in the middle column - exactly as deepnet/common.spectrogram_centred,
    // which it was trained on. Where Plain hears nothing the last note it had is kept; before Plain has ever
    // locked on, its first note; before that, his pitch (PitchHz). On the IC-7610 bench this took the network
    // from 27 to 33 of 34 - past Plain's 32 - and it now beats Plain on all four benches.
    public sealed class CwDeepDecoder : IDisposable
    {
        public const int Rate = 3200;
        const int Fft = 256, Hop = 48, Bin0 = 24;
        const double WindowSeconds = 15, StrideSeconds = 3, LagSeconds = 3;
        // LAG 3, NOT 6 (2026-10-17, his choice after the test - own.py LIVE_LAG reads exactly as this does):
        //   6 s: IC-7610 bench 25, his own sending 928 words, W1AW 1147, the hard fist 76
        //   3 s: IC-7610 bench 27, his own sending 927 words, W1AW 1136, the hard fist 57
        // Three seconds sooner for about the same reading, except on the hardest fist.
        static readonly string Chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789,./?=+ ";

        static CwDeepNet _shared;
        static bool _tried;

        /// <summary>The network file is there and loads: Data\CwDeep.bin beside the program.</summary>
        public static bool Available
        {
            get
            {
                if (!_tried)
                {
                    _tried = true;
                    try
                    {
                        var net = new CwDeepNet();
                        string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", CwDeepNet.WeightsFileName);
                        string error;
                        if (File.Exists(path) && net.Load(path, out error)) _shared = net;
                    }
                    catch (Exception swallowed) { Log.Swallow(swallowed); }
                }
                return _shared != null;
            }
        }

        public event Action<string> Text;

        /// <summary>Where the station is expected when Plain has not yet heard one: his CW pitch.</summary>
        public double PitchHz = 700;

        // Plain's note, block by block: (samples at 3200 written when it was measured, Hz; 0 = no signal).
        readonly List<KeyValuePair<long, double>> _notes = new List<KeyValuePair<long, double>>();

        /// <summary>The note Plain follows now (0 when it hears no signal) - after each block of sound.</summary>
        public void Note(double hz)
        {
            lock (_gate)
            {
                _notes.Add(new KeyValuePair<long, double>(_written, hz));
                long keep = _written - _ring.Length;
                int drop = 0;
                while (drop < _notes.Count - 1 && _notes[drop + 1].Key < keep) drop++;
                if (drop > 0) _notes.RemoveRange(0, drop);
            }
        }

        // EVERY LETTER WITH ITS TIME, in seconds from the first sound given - for the time-lined view in
        // Both, and for Mix. A word gap comes as " ", timed at the middle of the gap. The time is where the network said it: 0.8 dit after the letter's last mark ended
        // (measured 2026-10-17, deepnet/spike_offset.py: median -0.004 s, 80% within 0.03 s).
        public event Action<string, double> LetterTimed;

        // The stretch [from, to) in seconds has now been read whole - every letter in it has been given out
        // (for Mix: a Plain letter in it that the network did not read was not confirmed).
        public event Action<double, double> Covered;

        readonly CwDeepNet _net;
        readonly Resampler _resampler;
        readonly float[] _ring = new float[Rate * 20];
        long _written;                         // samples at 3200 ever written
        long _nextRun = (long)(WindowSeconds * Rate);
        long _printedTo = -1;                  // sample time up to which letters were printed
        readonly object _gate = new object();
        readonly Thread _worker;
        readonly AutoResetEvent _wake = new AutoResetEvent(false);
        volatile bool _stopping, _resetAsked;
        bool _lastWasSpace = true;
        long _lastLetterAt = -1;               // sample time (at 3200) of the last letter printed
        const double SilenceIsAGap = 3.0;      // seconds: longer than any letter plus its gap above ~10 WPM

        public CwDeepDecoder(int inputRate) : this(inputRate, _shared) { }

        internal CwDeepDecoder(int inputRate, CwDeepNet net, bool ownThread = true)
        {
            _net = net;
            _resampler = new Resampler(inputRate, Rate);
            if (!ownThread) return;
            _worker = new Thread(WorkLoop) { IsBackground = true, Name = "CW network decoder", Priority = ThreadPriority.BelowNormal };
            _worker.Start();
        }

        public void Process(short[] samples, int count)
        {
            var outp = _resampler.Push(samples, count);
            lock (_gate)
            {
                foreach (float v in outp) _ring[(int)(_written++ % _ring.Length)] = v;
            }
            if (_worker != null) _wake.Set(); else RunDue();
        }

        // HELD WHILE HOLYLOGGER KEYS THE RADIO ITSELF (COM-port CW, 2026-10-21). The network's work makes a lot
        // of short-lived arrays, and .NET stops EVERY thread of the program to clear them away - the keying
        // thread too, so dits and gaps came out long: he heard broken CW from COM4. Measured with
        // tools/KeyTiming.cs at 25 WPM: network running, 10-20 elements off by over a quarter dit (one gap
        // 588 ms late); network held, 0 (worst 2.9 ms). It cannot hear the other station while he transmits
        // anyway; when the keying ends it reads the newest window and goes on. Plain is not held.
        static volatile bool _holdForKeying;
        public static bool HoldForKeying
        {
            get { return _holdForKeying; }
            set { _holdForKeying = value; }
        }

        public void Reset() { _resetAsked = true; _wake.Set(); }

        public void Dispose() { _stopping = true; _wake.Set(); }

        // The research harness: what is still waiting is read as if the sound had stopped.
        internal void Finish() { RunWindow(_written, true); }

        void WorkLoop()
        {
            while (!_stopping)
            {
                _wake.WaitOne(1000);
                if (_stopping) break;
                if (_resetAsked) { _resetAsked = false; lock (_gate) { _printedTo = -1; _lastLetterAt = -1; _nextRun = _written + (long)(WindowSeconds * Rate); _notes.Clear(); } }
                if (_holdForKeying) continue;
                try { RunDue(); }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
            }
        }

        void RunDue()
        {
            long now;
            lock (_gate) now = _written;
            if (now < _nextRun) return;
            // Behind (a slow machine): skip to the newest window rather than fall further behind.
            _nextRun = now + (long)(StrideSeconds * Rate);
            RunWindow(now, false);
        }

        void RunWindow(long end, bool toTheEnd)
        {
            int n = (int)(WindowSeconds * Rate);
            if (end < n) return;
            long start = end - n;
            // Kept from one reading to the next, as the network keeps its own (see CwDeepNet.Rent): each of these
            // is over 85 KB, the size .NET clears away by stopping the whole program.
            float[] a = Kept(ref _sound, n);
            lock (_gate) for (int i = 0; i < n; i++) a[i] = _ring[(int)((start + i) % _ring.Length)];

            int frames;
            float[] spec = _net.Centred ? SpectrogramCentred(a, start, out frames) : Spectrogram(a, out frames);
            int steps;
            float[] lp = _net.Forward(spec, frames, out steps);

            long from = _printedTo < 0 ? start : _printedTo;
            long to = toTheEnd ? end : end - (long)(LagSeconds * Rate);
            if (to <= from) return;
            double stepSamples = 4.0 * Hop;
            var said = new StringBuilder();
            foreach (var read in CwBeamReading.Read(lp, steps, _net.NClass, Chars))
            {
                int best = read.Key, s = read.Value;
                long at = start + (long)(s * stepSamples);
                if (at < from || at >= to) continue;
                char ch = Chars[best];
                if (ch != ' ' && !_lastWasSpace && _lastLetterAt >= 0 && at - _lastLetterAt > SilenceIsAGap * Rate)
                {
                    // A long silence between two letters is a word gap, said or not: the network never put one
                    // after the last letter of an over ("PSE KCQ CQ" on his live audio, 2026-10-27) - the letter
                    // before and the one after were never in one window together.
                    said.Append(' ');
                    var gap = LetterTimed;
                    if (gap != null) gap(" ", (at + _lastLetterAt) / 2 / (double)Rate);
                }
                if (ch == ' ') { if (_lastWasSpace) continue; _lastWasSpace = true; }
                else { _lastWasSpace = false; _lastLetterAt = at; }
                string letter = ch == '=' ? "<BT>" : ch == '+' ? "<AR>" : ch.ToString();
                said.Append(letter);
                var timed = LetterTimed;
                if (timed != null) timed(letter, at / (double)Rate);       // " " too: Mix takes its word gaps
            }
            _printedTo = to;
            var covered = Covered;
            if (covered != null) covered(from / (double)Rate, to / (double)Rate);
            var text = Text;
            if (said.Length > 0 && text != null) text(said.ToString());
        }

        float[] _sound, _spec, _full;

        // The array kept in `slot`, made (again) only when the size asked for is not the size it has.
        static float[] Kept(ref float[] slot, int n)
        {
            if (slot == null || slot.Length != n) slot = new float[n];
            return slot;
        }

        float[] Spectrogram(float[] a, out int frames)
        {
            int n = a.Length, pad = Fft / 2, bins = _net.NBins;
            frames = 1 + n / Hop;
            var spec = Kept(ref _spec, frames * bins);
            var re = new double[Fft];
            var im = new double[Fft];
            for (int f = 0; f < frames; f++)
            {
                for (int i = 0; i < Fft; i++)
                {
                    int j = f * Hop + i - pad;
                    if (j < 0) j = -j; else if (j >= n) j = 2 * (n - 1) - j;     // reflect, as numpy
                    re[i] = a[j] * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / Fft));
                    im[i] = 0;
                }
                FftInPlace(re, im);
                for (int k = 0; k < bins; k++)
                    spec[f * bins + k] = (float)Math.Sqrt(re[Bin0 + k] * re[Bin0 + k] + im[Bin0 + k] * im[Bin0 + k]);
            }
            var col = new float[frames];
            for (int k = 0; k < bins; k++)
            {
                for (int f = 0; f < frames; f++) col[f] = spec[f * bins + k];
                Array.Sort(col);
                double h = (frames - 1) * 0.25;
                int lo = (int)Math.Floor(h);
                double floor = col[lo] + (lo + 1 < frames ? (col[lo + 1] - col[lo]) * (h - lo) : 0) + 1e-9;
                for (int f = 0; f < frames; f++) spec[f * bins + k] = (float)Math.Log(1 + spec[f * bins + k] / floor);
            }
            return spec;
        }

        const int FullBins = Fft / 2 + 1;           // 0 .. 1600 Hz
        const double HzPerBin = (double)Rate / Fft; // 12.5

        float[] SpectrogramCentred(float[] a, long start, out int frames)
        {
            int n = a.Length, pad = Fft / 2, bins = _net.NBins, mid = bins / 2;
            frames = 1 + n / Hop;
            var full = Kept(ref _full, frames * FullBins);
            var re = new double[Fft];
            var im = new double[Fft];
            for (int f = 0; f < frames; f++)
            {
                for (int i = 0; i < Fft; i++)
                {
                    int j = f * Hop + i - pad;
                    if (j < 0) j = -j; else if (j >= n) j = 2 * (n - 1) - j;
                    re[i] = a[j] * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / Fft));
                    im[i] = 0;
                }
                FftInPlace(re, im);
                for (int k = 0; k < FullBins; k++)
                    full[f * FullBins + k] = (float)Math.Sqrt(re[k] * re[k] + im[k] * im[k]);
            }
            var col = new float[frames];
            for (int k = 0; k < FullBins; k++)
            {
                for (int f = 0; f < frames; f++) col[f] = full[f * FullBins + k];
                Array.Sort(col);
                double h = (frames - 1) * 0.25;
                int lo = (int)Math.Floor(h);
                double floor = col[lo] + (lo + 1 < frames ? (col[lo + 1] - col[lo]) * (h - lo) : 0) + 1e-9;
                for (int f = 0; f < frames; f++) full[f * FullBins + k] = (float)Math.Log(1 + full[f * FullBins + k] / floor);
            }

            KeyValuePair<long, double>[] notes;
            lock (_gate) notes = _notes.ToArray();
            double first = PitchHz;
            foreach (var e in notes) if (e.Value > 0) { first = e.Value; break; }

            var spec = Kept(ref _spec, frames * bins);
            int ni = 0;
            double held = first;
            for (int f = 0; f < frames; f++)
            {
                long t = start + (long)f * Hop;
                while (ni < notes.Length && notes[ni].Key <= t)
                {
                    if (notes[ni].Value > 0) held = notes[ni].Value;
                    ni++;
                }
                int c = (int)Math.Round(held / HzPerBin);
                if (c < 0) c = 0; else if (c > FullBins - 1) c = FullBins - 1;
                for (int b = 0; b < bins; b++)
                {
                    int k = Math.Abs(c + b - mid);
                    if (k > FullBins - 1) k = 2 * (FullBins - 1) - k;
                    spec[f * bins + b] = full[f * FullBins + k];
                }
            }
            return spec;
        }

        static void FftInPlace(double[] re, double[] im)
        {
            int n = re.Length;
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j) { double t = re[i]; re[i] = re[j]; re[j] = t; t = im[i]; im[i] = im[j]; im[j] = t; }
            }
            for (int len = 2; len <= n; len <<= 1)
            {
                double ang = -2 * Math.PI / len, wr = Math.Cos(ang), wi = Math.Sin(ang);
                for (int i = 0; i < n; i += len)
                {
                    double cr = 1, ci = 0;
                    for (int k = 0; k < len / 2; k++)
                    {
                        int p = i + k, q = i + k + len / 2;
                        double tr = re[q] * cr - im[q] * ci, ti = re[q] * ci + im[q] * cr;
                        re[q] = re[p] - tr; im[q] = im[p] - ti; re[p] += tr; im[p] += ti;
                        double nr = cr * wr - ci * wi; ci = cr * wi + ci * wr; cr = nr;
                    }
                }
            }
        }

        // From the sound card's rate to 3200 a second: a windowed-sinc low-pass at 1450 Hz evaluated at
        // each output instant, 12 zero crossings each side (Blackman window). Any input rate.
        sealed class Resampler
        {
            readonly double _step, _cut;
            readonly int _half;
            readonly List<float> _in = new List<float>();
            double _pos;                       // next output instant, in input samples from _in[0]

            public Resampler(int inRate, int outRate)
            {
                _step = (double)inRate / outRate;
                _cut = 1450.0 / inRate;                            // cycles per input sample
                _half = (int)Math.Ceiling(12 / (2 * _cut));
            }

            public List<float> Push(short[] samples, int count)
            {
                for (int i = 0; i < count; i++) _in.Add(samples[i] / 32768f);
                var outp = new List<float>();
                while (_pos + _half + 1 < _in.Count)
                {
                    int c = (int)Math.Floor(_pos);
                    double s = 0;
                    for (int k = c - _half + 1; k <= c + _half; k++)
                    {
                        if (k < 0) continue;
                        double u = _pos - k;
                        double sinc = Math.Abs(u) < 1e-9 ? 2 * _cut : Math.Sin(2 * Math.PI * _cut * u) / (Math.PI * u);
                        double w = 0.42 + 0.5 * Math.Cos(Math.PI * u / _half) + 0.08 * Math.Cos(2 * Math.PI * u / _half);
                        s += _in[k] * sinc * w;
                    }
                    outp.Add((float)s);
                    _pos += _step;
                }
                int drop = (int)Math.Floor(_pos) - _half - 1;
                if (drop > 4096) { _in.RemoveRange(0, drop); _pos -= drop; }
                return outp;
            }
        }
    }

    // READING THE NETWORK'S ANSWER MORE CLEVERLY (2026-10-26, research deepnet/beam.py, his "yes"). The network
    // gives, for every step, how likely each letter (and the CTC blank) is. We used to take the single most likely
    // one at every step, throwing the second choices away ("R3ZBS" when the I was a close second). Now a few
    // spellings are kept step by step (a CTC prefix beam search), and when one closes a word it earns a bonus if
    // the word is KNOWN: a CW word of a contact, a common English word, or anything shaped like a callsign.
    //
    // Measured on the four tests (network alone, words read; his three sessions / W1AW / fist):
    //     reading                     sessions   W1AW   fist   invented on his sessions
    //     single best (before)           998     1108     94       484
    //     beam 8, no bonus              1003     1112     99       509
    //     beam 8, bonus 1               1029     1124     94       525
    //     beam 8, bonus 2               1048     1130     97       562
    //     beam 8, bonus 4               1040     1133    105       705
    // (IC-7610: 33 of 34 in every row.) The English words are the 3390 of Python's own documentation the
    // research used - some are Python terms; a better English list can replace them, measured again.
    static class CwBeamReading
    {
        public static int Beam = 8;
        public static double Bonus = 2.0;
        const double Prune = -7.0;      // a letter less likely than this at a step is not tried there

        sealed class Spelling
        {
            public double Pb = double.NegativeInfinity, Pnb = double.NegativeInfinity, Bonus;
            public List<KeyValuePair<int, int>> Said;
        }

        /// <summary>lp [steps x classes] log-probabilities, the blank the last class -> each letter read: (class, step).</summary>
        public static List<KeyValuePair<int, int>> Read(float[] lp, int steps, int classes, string chars)
        {
            int blank = classes - 1;
            if (Beam <= 0)
            {
                // The old reading, kept for comparing: the single most likely class at every step.
                var said = new List<KeyValuePair<int, int>>();
                int prev = -1;
                for (int t = 0; t < steps; t++)
                {
                    int top = 0;
                    for (int c = 1; c < classes; c++) if (lp[t * classes + c] > lp[t * classes + top]) top = c;
                    if (top < chars.Length && top != prev) said.Add(new KeyValuePair<int, int>(top, t));
                    prev = top == blank ? -1 : top;
                }
                return said;
            }
            var beams = new Dictionary<string, Spelling> { { "", new Spelling { Pb = 0, Said = new List<KeyValuePair<int, int>>() } } };
            var cands = new List<int>();
            for (int t = 0; t < steps; t++)
            {
                int row = t * classes;
                cands.Clear();
                for (int k = 0; k < classes; k++) if (k == blank || lp[row + k] > Prune) cands.Add(k);
                var next = new Dictionary<string, Spelling>();
                foreach (var kv in beams)
                {
                    string prefix = kv.Key;
                    Spelling sp = kv.Value;
                    double total = Lse(sp.Pb, sp.Pnb);
                    char last = prefix.Length > 0 ? prefix[prefix.Length - 1] : '\0';
                    foreach (int k in cands)
                    {
                        double p = lp[row + k];
                        if (k == blank)
                        {
                            var e = Get(next, prefix, sp.Bonus, sp.Said);
                            e.Pb = Lse(e.Pb, total + p);
                            continue;
                        }
                        if (k >= chars.Length) continue;
                        char ch = chars[k];
                        if (ch == ' ' && (prefix.Length == 0 || last == ' '))
                        {
                            // a word gap with no word before it says nothing new
                            var e = Get(next, prefix, sp.Bonus, sp.Said);
                            e.Pnb = Lse(e.Pnb, total + p);
                            continue;
                        }
                        if (ch == last)
                        {
                            // the same letter again: a new one only after a blank, else the same one held
                            var same = Get(next, prefix, sp.Bonus, sp.Said);
                            same.Pnb = Lse(same.Pnb, sp.Pnb + p);
                            var again = Get(next, prefix + ch, sp.Bonus, Plus(sp.Said, k, t));
                            again.Pnb = Lse(again.Pnb, sp.Pb + p);
                            continue;
                        }
                        double bonus = sp.Bonus + (ch == ' ' ? WordBonus(prefix) : 0);
                        var grown = Get(next, prefix + ch, bonus, Plus(sp.Said, k, t));
                        grown.Pnb = Lse(grown.Pnb, total + p);
                    }
                }
                beams = new Dictionary<string, Spelling>();
                foreach (var kv in next.OrderByDescending(x => Lse(x.Value.Pb, x.Value.Pnb) + x.Value.Bonus).Take(Beam))
                    beams.Add(kv.Key, kv.Value);
            }
            Spelling best = null;
            double bestScore = double.NegativeInfinity;
            foreach (var kv in beams)
            {
                bool open = kv.Key.Length > 0 && kv.Key[kv.Key.Length - 1] != ' ';
                double score = Lse(kv.Value.Pb, kv.Value.Pnb) + kv.Value.Bonus + (open ? WordBonus(kv.Key) : 0);
                if (score > bestScore) { bestScore = score; best = kv.Value; }
            }
            return best != null ? best.Said : new List<KeyValuePair<int, int>>();
        }

        static Spelling Get(Dictionary<string, Spelling> next, string prefix, double bonus, List<KeyValuePair<int, int>> said)
        {
            Spelling e;
            if (!next.TryGetValue(prefix, out e)) next[prefix] = e = new Spelling { Bonus = bonus, Said = said };
            return e;
        }

        static List<KeyValuePair<int, int>> Plus(List<KeyValuePair<int, int>> said, int k, int t)
        {
            var l = new List<KeyValuePair<int, int>>(said.Count + 1);
            l.AddRange(said);
            l.Add(new KeyValuePair<int, int>(k, t));
            return l;
        }

        static double Lse(double a, double b)
        {
            if (double.IsNegativeInfinity(a)) return b;
            if (double.IsNegativeInfinity(b)) return a;
            double m = Math.Max(a, b);
            return m + Math.Log(Math.Exp(a - m) + Math.Exp(b - m));
        }

        // The word the prefix ends with (the letters since its last word gap): known, a bonus.
        static double WordBonus(string prefix)
        {
            string p = prefix.TrimEnd(' ');
            int sp = p.LastIndexOf(' ');
            string word = sp < 0 ? p : p.Substring(sp + 1);
            return Known(word) ? Bonus : 0;
        }

        static readonly System.Text.RegularExpressions.Regex CallShape = new System.Text.RegularExpressions.Regex(
            @"^(?:[A-Z0-9]{1,4}/)?(?:[A-Z]{1,2}|[0-9][A-Z]{1,2}|[A-Z][0-9])[0-9][A-Z]{1,4}(?:/[A-Z0-9]{1,3})?$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

        static bool Known(string word)
        {
            if (word.Length == 0) return false;
            if (CwWords.Contains(word) || English.Contains(word)) return true;
            if (word.Length <= 4)
            {
                bool digits = true;
                foreach (char c in word) if (c < '0' || c > '9') digits = false;
                if (digits) return true;
            }
            return CallShape.IsMatch(word);
        }

        static readonly HashSet<string> CwWords = new HashSet<string>((
            "CQ DE K KN BK AR SK AS BT TU TNX TKS THX TNK 73 88 72 UR URS RST 599 5NN 579 559 589 569 479 339 449 ES FB OM YL XYL HR HW HRD "
            + "CPY CPI NAME OP QTH RIG ANT PWR WX TEMP GM GA GE GN GD SRI AGN PSE PLS QSL QSO QRZ QRM QRN QSB QSY QRP QRO QRL QRS QRQ QRT "
            + "QRV QRX QRG QSK QSP QTR CUL GL GUD DX TEST NR CFM ABT ALL BEST DR FER FOR MNI VY VERY NICE RPT RPRT INFO LOOG LOG "
            + "WKD WRK SIGS SIG BAND COND CONDX UP DN DWN RX TX TRX KW W WATTS WATT DIPOLE YAGI VERT BEAM LP SP POTA SOTA WWFF IOTA")
            .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));

        static readonly HashSet<string> English = new HashSet<string>((
            "AB ABBREVIATED ABBREVIATIO ABC ABCMETA ABCS ABILITY ABLE ABLY ABNORMALLY ABORTED ABOUT ABOVE ABRA "
            + "ABRACADABRA ABS ABSENCE ABSENT ABSOLUTE ABSOLUTELY ABSPATH ABSTRACT ABSTRACTION ACCEPT ACCEPTABLE ACCEPTED "
            + "ACCEPTS ACCESS ACCESSED ACCESSES ACCESSIBLE ACCESSING ACCIDENTALL ACCOMPLISH ACCORDING ACCORDINGLY ACHIEVE "
            + "ACHIEVED ACQUIRE ACROSS ACT ACTION ACTIONS ACTIVE ACTUAL ACTUALLY ADAPTABILIT ADD ADDED ADDING ADDITION "
            + "ADDITIONAL ADDITIONALL ADDITIONS ADDITIVE ADDRESS ADDS ADJACENT ADMINISTRAT ADVANCED ADVANTAGE ADVISED "
            + "ADVISES ADVISORIES AENTER AEXIT AFFECT AFFECTED AFFECTING AFFECTS AFFIXING AFTER AFTERWARDS AGAIN AGAINST "
            + "AGGRESSIVE AITER AITERATOR AKA AL ALGORITHM ALGORITHMIC ALGORITHMS ALIAS ALIASES ALIASING ALIGN ALIGNED "
            + "ALIGNING ALIGNMENT ALIST ALIVE ALIZATION ALL ALLOW ALLOWABLE ALLOWED ALLOWING ALLOWS ALLY ALMOST ALONE "
            + "ALONG ALONGSIDE ALPHABETIC ALPHABETICA ALPHANUMERI ALREADY ALSO ALTERED ALTERNATE ALTERNATIVE ALTERS "
            + "ALTHOUGH ALTOGETHER ALWAYS AMAX AMAZING AMBIGUITIES AMBIGUITY AMBIGUOUS AMONG AMOUNT AN ANALYSIS ANALYZER "
            + "ANCESTOR ANCESTORS AND ANEXT ANNEX ANNOTATE ANNOTATED ANNOTATING ANNOTATION ANNOTATIONL ANNOTATIONS "
            + "ANONYMOUS ANOTHER ANSWER ANSWERS ANY ANYMORE ANYTHING ANYWHERE APART API APOSTROPHES APPEAR APPEARING "
            + "APPEARS APPEND APPENDING APPLICABLE APPLICATION APPLIED APPLIES APPLY APPROACH APPROPRIATE APPROXIMATE "
            + "APPROXIMATI AR ARABIC ARBITRARILY ARBITRARY ARCHITECTUR ARE ARG ARGCOUNT ARGS ARGUMENT ARGUMENTS ARGV ARISE "
            + "ARITHMETIC AROUND ARRAY ARRAYS ARROW ARTHUR ARTIFICIAL ARTUPLE AS ASCENDING ASCII ASIDE ASK ASKED ASPECTS "
            + "ASSEMBLED ASSERT ASSERTION ASSERTIONER ASSERTIONS ASSIGN ASSIGNABLE ASSIGNED ASSIGNING ASSIGNMENT "
            + "ASSIGNMENTS ASSIGNS ASSIST ASSOCIATE ASSOCIATED ASSUME ASSUMED ASSUMING ASSURING ASTERISK ASTERISKS "
            + "ASYMMETRY ASYNC ASYNCHRONOU ASYNCIO ASYNCTASK AT ATION ATOM ATOMIC ATTACH ATTACHED ATTACHING ATTEMPT "
            + "ATTEMPTED ATTEMPTING ATTEMPTS ATTR ATTRIBUTE ATTRIBUTEER ATTRIBUTERE ATTRIBUTES ATYPICAL AUDITING AUGASSIGN "
            + "AUGMENTED AUGOP AUGTARGET AUSTRALIANP AUTOGENERAT AUTOMATIC AUTOMATICAL AVAILABLE AVOID AVOIDED AWAIT "
            + "AWAITABLE AWAITED AWARE BACK BACKEND BACKENDS BACKGROUND BACKSLASH BACKSLASHES BACKSLASHRE BACKSPACE "
            + "BACKWARD BACKWARDS BACON BAD BAG BANANA BAR BARS BASE BASED BASEEXCEPTI BASES BASETESTCAS BASIC BASIS BAZ "
            + "BDB BDBQUIT BE BEARING BECAUSE BECOME BECOMES BEEF BEEN BEFORE BEGIN BEGINNING BEGINS BEHAVE BEHAVES "
            + "BEHAVING BEHAVIOR BEHAVIORS BEHAVIOUR BEHIND BEING BEL BELL BELONG BELONGED BELONGING BELONGS BELOW BESIDES "
            + "BEST BETWEEN BEYOND BIDIRECTION BILL BIN BINARY BIND BINDIGIT BINDING BINDINGS BINDS BININTEGER BIT BITS "
            + "BITWISE BLAISE BLANK BLOCK BLOCKED BLOCKING BLOCKINGIOE BLOCKS BLTIN BLUE BODY BOOL BOOLEAN BOOLEANS BORN "
            + "BOTH BOTTOM BOUND BOUNDARIES BOUNDLESS BOUNDS BOX BPNUMBER BR BRACE BRACES BRACKETS BREAK BREAKAGE "
            + "BREAKFAST BREAKING BREAKPOINT BREAKPOINTS BREAKS BRIEF BRING BROAD BRUCE BS BUBBLES BUCKET BUFFER "
            + "BUFFERFLAGS BUGS BUILD BUILDING BUILDS BUILT BUILTIN BUILTINS BUT BY BYPASS BYPASSED BYPASSES BYPASSING "
            + "BYTE BYTEARRAY BYTECODE BYTECODES BYTES BYTESIO CACHE CACHED CAD CALCULATE CALCULATED CALCULATING CALL "
            + "CALLABLE CALLABLES CALLBACKS CALLED CALLER CALLING CALLS CAN CANCELED CANDIDATE CANNOT CANONICAL CAPITAL "
            + "CAPITALIZE CAPITALIZED CAPITALS CAPTURE CAPWORDS CARD CARE CAREFULLY CARRIAGE CARRIED CARRY CARRYING CASE "
            + "CASED CASEFOLD CASEFOLDED CASEFOLDING CASELESS CASES CATCHING CATEGORIES CATEGORY CAUGHT CAUSE CAUSED "
            + "CAUSES CAUSING CAUTION CAVEAT CAVEATS CEASE CEDILLA CEIL CELL CELLS CELLVARS CENTER CENTERED CERTAIN CHAIN "
            + "CHAINED CHAINING CHANGE CHANGED CHANGES CHANGING CHANNEL CHAPTER CHAR CHARACTER CHARACTERS CHARS CHECK "
            + "CHECKED CHECKER CHECKERS CHECKING CHECKS CHILD CHOICE CHOSEN CHR CHUNK CHUNKS CIRCULAR CIRCUMSTANC CIRCUS "
            + "CK CL CLARITY CLASHES CLASS CLASSCELL CLASSDEF CLASSES CLASSINST CLASSMETHOD CLASSNAME CLAUSE CLAUSES "
            + "CLEANUP CLEAR CLEARED CLEARER CLEARING CLEARS CLEVER CLI CLIPPED CLOSE CLOSED CLOSELY CLOSES CLOSEST "
            + "CLOSING CLOSURE CLS CMD CMOWZ CMP CO CODE CODEBASES CODECS CODEOBJECT COEFFICIENT COERCES COERCION COLLECT "
            + "COLLECTABLE COLLECTED COLLECTION COLLECTIONS COLLECTOR COLLECTS COLON COLOR COLORIZE COLORIZED COLUMN "
            + "COLUMNS COM COMBINATION COMBINED COMBINES COMBINING COME COMES COMMA COMMAND COMMANDS COMMAS COMMENT "
            + "COMMENTS COMMON COMMONLY COMP COMPARE COMPARED COMPARES COMPARING COMPARISON COMPARISONS COMPATIBILI "
            + "COMPATIBLE COMPILE COMPILED COMPILER COMPLEMENT COMPLETE COMPLETED COMPLETEKEY COMPLETELY COMPLETENES "
            + "COMPLETION COMPLEX COMPLEXITY COMPLIANT COMPLICATE COMPLICATIN COMPONENTS COMPOSED COMPOUND COMPREHENSI "
            + "COMPRISED COMPUTE COMPUTED COMPUTERS COMPUTES COMPUTING CONCATENATE CONCATENATI CONCEPT CONCEPTUAL "
            + "CONCEPTUALL CONCISE CONCRETE CONCRETELY CONCURRENCY CONCURRENT CONCURRENTL CONDITION CONDITIONAL CONDITIONS "
            + "CONFIRM CONFIRMATIO CONFORM CONFUSION CONJUNCTION CONNECTED CONNECTOR CONSECUTIVE CONSEQUENCE CONSEQUENTL "
            + "CONSIDER CONSIDERATI CONSIDERED CONSIST CONSISTENCY CONSISTENT CONSISTENTL CONSISTING CONSISTS CONSOLE "
            + "CONSTANT CONSTANTS CONSTRAIN CONSTRAINED CONSTRAINT CONSTRAINTS CONSTRUCT CONSTRUCTED CONSTRUCTIN "
            + "CONSTRUCTIO CONSTRUCTOR CONSTRUCTS CONSTS CONSUMED CONTAIN CONTAINED CONTAINER CONTAINERS CONTAINING "
            + "CONTAINMENT CONTAINS CONTENT CONTENTS CONTEXT CONTEXTS CONTINUATIO CONTINUE CONTINUES CONTINUING "
            + "CONTRACTION CONTRAST CONTRASTS CONTROL CONTROLLED CONTROLLING CONTROLS CONVENIENCE CONVENIENT CONVENIENTL "
            + "CONVENTION CONVENTIONA CONVENTIONS CONVERSELY CONVERSION CONVERSIONS CONVERT CONVERTED CONVERTING CONVERTS "
            + "COORD COORDINATE COORDINATES COORDINATIN COPIED COPIES COPY COPYING CORE COROUTINE COROUTINES CORRECT "
            + "CORRECTLY CORRECTNESS CORRESPOND CORRESPONDE CORRESPONDI CORRESPONDS CORRUPT CORRUPTION COST COULD COUNT "
            + "COUNTER COUNTERPART COUNTING COUNTRY COURSE COVER COVERS CPYTHON CR CRASHED CREATE CREATED CREATES CREATING "
            + "CREATION CREATOR CREDITS CRITERION CTRL CTYPE CURLY CURRENT CURRENTLY CUSTOM CUSTOMIZATI CUSTOMIZE "
            + "CUSTOMIZED CUSTOMIZING CYCLE CYCLES CYCLIC CYCLICALLY DANGLING DATA DATABASE DATACLASSES DATETIME DBM DE "
            + "DEACTIVATE DEADLOCK DEALING DEALLOCATED DEBUG DEBUGGED DEBUGGER DEBUGGERS DEBUGGING DECIDE DECIMAL DECIMALS "
            + "DECINTEGER DECISION DECISIONS DECLARATION DECLARE DECLARED DECLARING DECODE DECODED DECORATED DECORATING "
            + "DECORATOR DECORATORS DECREASING DECREMENTED DECREMENTS DEDENT DEDICATED DEEMED DEEPER DEEPLY DEF DEFAULT "
            + "DEFAULTDICT DEFAULTING DEFAULTS DEFEATING DEFERRED DEFINE DEFINED DEFINES DEFINING DEFINITION DEFINITIONS "
            + "DEFPARAMETE DEGREE DEL DELATTR DELAYED DELEGATE DELEGATES DELEGATION DELETE DELETED DELETES DELETING "
            + "DELETION DELETIONS DELIMIT DELIMITED DELIMITER DELIMITERS DELITEM DELIVERED DEMAND DENIAL DENOTE DENOTED "
            + "DENOTES DENY DEPARTMENT DEPEND DEPENDENT DEPENDING DEPENDS DEPRECATED DEPRECATION DEQUE DERIVED DERIVEDCORE "
            + "DESCRIBE DESCRIBED DESCRIBES DESCRIPTION DESCRIPTOR DESCRIPTORS DESIRABLE DESIRE DESIRED DESTROYED "
            + "DESTRUCTION DESTRUCTIVE DESTRUCTOR DETAIL DETAILED DETAILS DETECT DETECTED DETECTION DETECTS DETERMINE "
            + "DETERMINED DETERMINES DETERMINING DETERMINIST DEVELOPMENT DIAGRAM DIAMOND DICT DICTIONARIE DICTIONARY "
            + "DICTVIEW DID DIDN DIFFER DIFFERENCE DIFFERENCES DIFFERENT DIFFERENTLY DIFFERS DIGIT DIGITPART DIGITS "
            + "DIGRAPHS DILIGENT DIR DIRECT DIRECTIVE DIRECTLY DIRECTORY DIRECTS DISABLE DISABLED DISABLING DISALLOWED "
            + "DISAMBIGUAT DISCARDED DISCIPLINE DISCOURAGED DISCUSSED DISGUISE DISHES DISK DISPLAY DISPLAYED DISPLAYS "
            + "DISTINCT DISTINCTION DISTINCTLY DISTINGUISH DIVISION DIVMOD DJANGO DO DOC DOCSTRING DOCSTRINGS DOCTEST "
            + "DOCUMENT DOCUMENTATI DOCUMENTED DOES DOESN DOING DON DONE DOT DOTS DOTTED DOUBLE DOUBLED DOUBLING DOWN "
            + "DROPPED DROPPING DROPS DUAL DUE DUMP DUNDER DUPLICATE DUPLICATES DURATION DURING DWARFED DXOB DYNAMIC "
            + "DYNAMICALLY EACH EAGER EAR EARLIER EASE EASIER EASILY EASY EBNF ECONOMY ED EE EED EFA EFFECT EFFECTIVELY "
            + "EFFECTS EFFICIENCY EFFICIENT EFFICIENTLY EG EGGS EIGHT EITHER ELEMENT ELEMENTS ELIF ELIGIBLE ELIMINATED "
            + "ELLIPSIS ELP ELSE ELSEWHERE EMITS EMITTED EMPHASIZE EMPTY EMULATE EMULATES EMULATING EMULATION ENABLE "
            + "ENABLED ENABLES ENCAPSULATE ENCE ENCLOSED ENCLOSING ENCODE ENCODED ENCODING ENCODINGS ENCOUNTER ENCOUNTERED "
            + "END ENDING ENDS ENDSWITH ENFORCE ENFORCED ENFORCES ENGLISH ENHANCED ENOUGH ENSURE ENSURES ENTER ENTERED "
            + "ENTERING ENTERS ENTIRE ENTRIES ENTRY ENUM ENUMERATE ENUMERATING ENUMMETA ENVIRONMENT EOF EPLACE EQ EQUAL "
            + "EQUALITY EQUALS EQUIVALENCE EQUIVALENT ERATION ERROR ERRORS ES ESCAPE ESCAPED ESCAPES ESEQ ESPECIALLY "
            + "ESTABLISHED ESTIMATED ETC ETURN EVAL EVALUATE EVALUATED EVALUATES EVALUATING EVALUATION EVALUATIONS EVEN "
            + "EVENT EVENTS EVER EVERY EVERYONE EVERYTHING EX EXACT EXACTLY EXAMINATION EXAMINED EXAMPLE EXAMPLES EXC "
            + "EXCEPT EXCEPTING EXCEPTION EXCEPTIONAL EXCEPTIONGR EXCEPTIONS EXCESS EXCLAMATION EXCLUDES EXCLUDING "
            + "EXCLUSIVE EXCLUSIVELY EXCNUMBER EXEC EXECMODEL EXECUTABLE EXECUTE EXECUTED EXECUTES EXECUTING EXECUTION "
            + "EXERCISE EXHAUSTED EXHAUSTIVE EXIST EXISTED EXISTING EXISTS EXIT EXITED EXITS EXP EXPAND EXPANDED "
            + "EXPANDTABS EXPECT EXPECTATION EXPECTED EXPECTS EXPLAINED EXPLAINS EXPLANATION EXPLICIT EXPLICITLY EXPLOIT "
            + "EXPLORED EXPONENT EXPONENTIAT EXPORTING EXPOSE EXPOSED EXPR EXPRESSED EXPRESSES EXPRESSING EXPRESSION "
            + "EXPRESSIONS EXPRLISTS EXT EXTEND EXTENDED EXTENDING EXTENDS EXTENSIBLE EXTENSION EXTENSIONS EXTERNAL EXTRA "
            + "EXTRACT EXTRACTED EXTRACTING FACILITIES FACT FACTOR FACTORIES FAIL FAILED FAILING FAILS FAILURE FAILURES "
            + "FAITHFULLY FALL FALLBACK FALLING FALLS FALSE FAQ FASHION FAST FASTER FAULT FAULTHANDLE FAVORITE FBB FDOPEN "
            + "FEATURE FEATURED FEATURES FEB FEED FEW FF FFFF FG FI FIELD FIELDS FIFTH FILE FILENAME FILES FILESYSTEM FILL "
            + "FILLCHAR FILLED FILLING FILLS FINAL FINALIZATIO FINALIZE FINALIZED FINALIZER FINALLY FIND FINDER FINDING "
            + "FINE FINISHED FINISHES FINITE FIRST FIRSTLINENO FIXED FLAG FLAGS FLAVORS FLEXIBILITY FLEXIBLE FLOAT "
            + "FLOATING FLOATNUMBER FLOATS FLOOR FLOORDIV FLOW FLY FLYING FMOD FOLDING FOLLOW FOLLOWED FOLLOWING FOLLOWS "
            + "FOO FOOTNOTES FOR FORCE FORCES FORM FORMAL FORMALIZATI FORMALLY FORMAT FORMATS FORMATSTRIN FORMATTABLE "
            + "FORMATTED FORMATTER FORMATTING FORMED FORMER FORMFEED FORMS FORMULA FORTH FORWARD FOUND FOUR FR FRACTION "
            + "FRACTIONAL FRACTIONS FRAME FRAMES FRAMEWORK FRAMEWORKS FREE FREED FREELY FREEVARS FRIENDS FROM FROMKEYS "
            + "FRONT FROZEN FROZENSET FROZENSETS FSTRING FSTRINGPREF FULL FULLY FUNC FUNCDEF FUNCNAME FUNCTION FUNCTIONALI "
            + "FUNCTIONS FUNCTOOLS FUNDAMENTAL FURTHER FURTHERMORE FUTURE GALAHAD GARBAGE GATHERED GC GE GENERAL GENERALLY "
            + "GENERATE GENERATED GENERATING GENERATION GENERATOR GENERATORS GENERIC GENERICALIA GENERICS GERMAN GET "
            + "GETATTR GETATTRIBUT GETDOC GETITEM GETS GETTEXT GETTING GG GIVE GIVEN GIVES GIVING GLOB GLOBAL GLOBALS GNU "
            + "GO GOES GOING GOT GRADE GRAINED GRAMMAR GREATER GREETING GROUP GROUPED GROUPING GROUPS GROW GS GT GUARANTEE "
            + "GUARANTEED GUARANTEES GUARD GUARDS GUIDO HAD HALF HAND HANDLE HANDLED HANDLER HANDLERS HANDLES HANDLING "
            + "HAPPEN HAPPENED HAPPENS HARD HARDER HAROLD HAS HASATTR HASH HASHABLE HASHED HASHING HASN HAUNTS HAVE HAVING "
            + "HE HEADER HEADERS HEAVILY HEL HELD HELLO HELLOWORLD HELP HELPFUL HELPS HENCE HERE HETEROGENEO HETTINGER HEX "
            + "HEXADECIMAL HEXDIGIT HEXINTEGER HH HIDDEN HIDES HIERARCHY HIGH HIGHEST HIGHLIGHT HILOSOPHER HINT HINTING "
            + "HINTS HISTORICAL HISTORY HIT HOLD HOLDS HOLY HOME HOMOGENEOUS HONORED HOOK HOOKS HORIZONTAL HOST HOSTS HOW "
            + "HOWEVER HTML HTTP HUMANS IADD IAND IB IC ICAL ICALLY ID IDEAL IDEAS IDENTICAL IDENTIFIED IDENTIFIER "
            + "IDENTIFIERS IDENTIFY IDENTIFYING IDENTITIES IDENTITY IDEOGRAPHIC IDES IDX IEEE IF IFLOORDIV IGNORE IGNORED "
            + "IGNORING IL ILLEGAL ILLUSION ILLUSTRATE ILLUSTRATED ILLUSTRATES ILLUSTRATIO ILSHIFT IMAG IMAGINARY "
            + "IMAGNUMBER IMATMUL IMMEDIATE IMMEDIATELY IMMUTABILIT IMMUTABLE IMOD IMPACTS IMPLEMENT IMPLEMENTAT "
            + "IMPLEMENTED IMPLEMENTIN IMPLEMENTS IMPLICATION IMPLICIT IMPLICITLY IMPLIED IMPLIES IMPLY IMPORT IMPORTANCE "
            + "IMPORTANT IMPORTED IMPORTERROR IMPORTING IMPORTLIB IMPORTS IMPORTWARNI IMPOSED IMPOSSIBLE IMPROPER "
            + "IMPROPERLY IMPROVE IMPROVED IMUL IN INACCESSIBL INAPPROPRIA INCARNATION INCLUDE INCLUDED INCLUDES INCLUDING "
            + "INCLUSIVE INCOMPATIBL INCOMPLETE INCORRECTLY INCREASE INCREMENTED INDENT INDENTATION INDENTED INDEPENDENT "
            + "INDEX INDEXED INDEXERROR INDEXES INDEXING INDIC INDICATE INDICATED INDICATES INDICATING INDICATOR INDICES "
            + "INDIRECT INDIRECTLY INDIVIDUAL INEQUALITY INF INFINITE INFINITY INFLUENCES INFO INFORMAL INFORMALLY "
            + "INFORMATION ING INHERIT INHERITANCE INHERITED INHERITING INHERITS INIT INITIAL INITIALISED INITIALIZAT "
            + "INITIALIZE INITIALIZED INITIALIZIN INLINE INNER INPLACE INPUT INPUTS INQUISITION INSENSITIVE INSERT "
            + "INSERTED INSERTING INSERTION INSERTS INSIDE INSIGNIFICA INSOFAR INSPECT INSPECTION INSPECTS INSPIRED INST "
            + "INSTANCE INSTANCECHE INSTANCES INSTANTIATE INSTANTIATI INSTEAD INSTRUCTION INT INTEGER INTEGERS INTEGRAL "
            + "INTELLIGENC INTENDED INTENTIONAL INTERACT INTERACTION INTERACTIVE INTERACTS INTERCHANGE INTEREST "
            + "INTERESTING INTERFACE INTERFERE INTERMEDIAT INTERNAL INTERNALLY INTERNATION INTEROPERAT INTERPOLATI "
            + "INTERPRET INTERPRETAT INTERPRETED INTERPRETER INTERPRETS INTERRUPTED INTERSECTIO INTO INTRINSIC INTRODUCE "
            + "INTRODUCED INTRODUCES INTRODUCING INTROSPECTI INTROSPECTS INTUITIVE INUE INVALID INVARIANT INVARIANTS "
            + "INVERSE INVERSION INVERT INVERTS INVESTIGATE INVOCATION INVOCATIONS INVOKE INVOKED INVOKES INVOKING INVOLVE "
            + "INVOLVED INVOLVING IO ION IONAL IONS IOR IPOW IPZ IRREFUTABLE IRSHIFT IS ISALNUM ISALPHA ISASCII ISCLASS "
            + "ISDECIMAL ISDIGIT ISIDENTIFIE ISINSTANCE ISKEYWORD ISLOWER ISN ISNUMERIC ISOLATED ISPRINTABLE ISSPACE "
            + "ISSUBCLASS ISSUE IST ISTITLE ISUB ISUPPER IT ITEM ITEMS ITER ITERABLE ITERABLES ITERATE ITERATED ITERATES "
            + "ITERATING ITERATION ITERATOR ITERATORS ITIES ITRUEDIV ITS ITSELF ITY IVE IX IXOR IZED JACK JAVA JOB JOIN "
            + "JUICE JUMP JUMPS JUST JUSTIFIED KEEP KEEPENDS KEEPING KEEPS KEPT KEY KEYBOARD KEYERROR KEYS KEYWORD "
            + "KEYWORDS KHAROSTHI KIND KINDS KL KNOW KNOWN KNOWS KWARGS KWDEFAULTS KWDS KWONLYARGCO LACK LACKS LAMBDA "
            + "LAMBDAS LANGUAGE LANGUAGES LARGE LARGER LARGEST LAST LASTI LASTLY LATER LATIN LATITUDE LATTER LAYERS LAYOUT "
            + "LAYOUTS LAZILY LAZY LC LE LEAD LEADING LEAST LEAVE LEAVES LEAVING LEFT LEGACY LEGAL LEGALLY LEN LENGTH "
            + "LENGTHS LERROR LESS LETS LETTER LETTERS LETTERSONLY LEVEL LEVELS LEXICAL LEXICALLY LEXICOGRAPH LF LIB "
            + "LIBERTY LIBRARIES LIBRARY LIE LIES LIFE LIFETIME LIFO LIKE LIKELY LIKEWISE LIMIT LIMITATION LIMITATIONS "
            + "LIMITED LIMITS LINE LINEAR LINEFEED LINENO LINES LINKED LINSPACE LIST LISTED LISTING LISTORSET LISTS "
            + "LITERAL LITERALS LIVE LJUST LL LLY LM LNOTAB LO LOAD LOADED LOADER LOADING LOADS LOCAL LOCALE LOCALECONV "
            + "LOCALLY LOCALS LOCATED LOCATING LOCATION LOCATIONS LOCK LOCKED LOCKING LOGGING LOGIC LOGICAL LONE LONG "
            + "LONGER LONGITUDE LONGLIST LONGSTRINGI LOOK LOOKAHEAD LOOKAHEADS LOOKED LOOKING LOOKS LOOKUP LOOKUPERROR "
            + "LOOKUPS LOOP LOOPING LOOPS LOSS LOSSLESSLY LOST LOW LOWER LOWERCASE LOWERCASED LOWERCASING LOWEST LSHIFT "
            + "LST LSTRIP LT LU LY MACHINE MACHINERY MADE MAGNITUDE MAIN MAINLY MAINTAIN MAINTAINS MAINTENANCE MAKE "
            + "MAKEFILE MAKES MAKETRANS MAKING MANAGEMENT MANAGER MANAGERS MANAGES MANAGING MANDATORY MANGLED MANGLING "
            + "MANNER MANUALLY MANY MAP MAPPED MAPPING MAPPINGPROX MAPPINGS MAPS MARCH MARK MARKED MARKER MARKS MASK MATCH "
            + "MATCHED MATCHES MATCHING MATH MATHEMATICA MATHEMATICS MATMUL MATRIX MATTER MAX MAXIMUM MAXSIZE MAXSPLIT MAY "
            + "MAYBE MC ME MEAN MEANING MEANINGFUL MEANINGS MEANS MECHANISM MEET MEETS MEMBER MEMBERS MEMBERSHIP MEMORY "
            + "MEMORYVIEW MENTION MENTIONED MENU MERCY MERELY MERGED MESSAGE MESSAGES META METACLASS METACLASSES METADATA "
            + "METHOD METHODS MIDDLE MIGHT MIGRATION MIN MINI MINIMUM MINUS MISC MISCTESTS MISMATCH MISSING MISSISS "
            + "MISSISSIPPI MIX MN MO MOD MODE MODEL MODELED MODELLED MODIFICATIO MODIFIED MODIFIER MODIFIERS MODIFIES "
            + "MODIFY MODIFYING MODULE MODULES MODULETYPE MODULO MONITORING MONTY MORE MORTEM MOST MOSTLY MOTIVATION MOVE "
            + "MRO MUCH MUL MULTI MULTIDIMENS MULTIPLE MULTIPLICAT MULTIPLIED MULTIPLIES MULTIPROCES MUST MUTABILITY "
            + "MUTABLE MUTABLEMAPP MUTABLESEQU MUTATE MUTATED MUTUALLY MY MYCLASS MYPY MYSUBCLASS NAL NAME NAMED "
            + "NAMEDTUPLE NAMEERROR NAMES NAMESPACE NAMESPACES NAMING NAN NANS NARROWER NATIONALITY NATURE NCE ND NDBM NDE "
            + "NDIGITS NE NEAR NEAREST NEARLY NECESSARILY NECESSARY NEED NEEDED NEEDS NEG NEGATION NEGATIVE NEITHER NERROR "
            + "NEST NESTED NESTING NEVER NEW NEWER NEWLINE NEWLINES NEWLY NEXT NFKC NG NGLY NICELY NICK NINE NL NLOCALS NO "
            + "NOBODY NODE NODEFAULT NODELIST NON NONE NONEMPTY NONLOCAL NONNEGATIVE NONPRINTABL NONSPACING NONZERO "
            + "NONZERODIGI NOR NORMAL NORMALIZATI NORMALIZE NORMALIZES NORMALLY NORMATIVE NOSIGINT NOT NOTABLY NOTATION "
            + "NOTATIONS NOTE NOTED NOTES NOTHING NOTICE NOTIMPLEMEN NOTION NOW NS NT NULL NUM NUMBER NUMBERS NUMERIC "
            + "NUMERICAL NUMERICALLY NUMPY OBEY OBJ OBJCLASS OBJECT OBJECTS OBSERVE OBSERVED OBSERVING OBTAIN OBTAINED "
            + "OCCASION OCCUR OCCURRED OCCURRENCE OCCURRENCES OCCURRING OCCURS OCERT OCT OCTAL OCTDIGIT OCTETS OCTINTEGER "
            + "ODD OF OFF OFFENDING OFFERED OFFICIAL OFFSET OFFSETS OFTEN OLD OLDER OMIT OMITTED ON ONCE ONE ONES ONGROUP "
            + "ONLY ONS ONT OOO OP OPCODE OPCODES OPEN OPENED OPENING OPERAND OPERANDS OPERATE OPERATES OPERATING "
            + "OPERATION OPERATIONS OPERATOR OPERATORS OPN OPPOSITE OPT OPTIMISATIO OPTIMIZATIO OPTIMIZED OPTION OPTIONAL "
            + "OPTIONALLY OPTIONS OR ORD ORDER ORDERED ORDERING ORDERINGS ORDINAL ORDINALS ORDINARY ORG ORGANIZATIO ORIGIN "
            + "ORIGINAL ORIGINALLY ORIGINATE OS OSERROR OTHER OTHERS OTHERWISE OUNT OUP OUT OUTCOMES OUTER OUTERMOST "
            + "OUTLINING OUTLIVE OUTPUT OUTPUTS OUTSIDE OVER OVERALL OVERALLOCAT OVERFLOW OVERFLOWERR OVERHEAD OVERLAPPING "
            + "OVERLAPS OVERLOADED OVERLOADING OVERLY OVERRIDDEN OVERRIDE OVERRIDES OVERRIDING OVERVIEW OVERWRITE "
            + "OVERWRITES OVERWRITING OVERWRITTEN OWN OWNER OWNING OWNS PACKAGE PACKAGES PACKING PADDING PAIR PAIRS "
            + "PARAGRAPH PARALLEL PARAM PARAMETER PARAMETERIZ PARAMETERS PARAMS PARAMSPEC PARENT PARENTCLASS PARENTHESES "
            + "PARENTHESIS PARENTHESIZ PARENTS PARSE PARSED PARSER PARSETUPLE PARSING PART PARTIAL PARTIALLY PARTICULAR "
            + "PARTICULARL PARTITION PARTLY PARTS PARTY PASS PASSED PASSES PASSING PAST PATH PATHNAME PATHS PATTERN "
            + "PATTERNS PAUSE PC PDB PDBRC PE PENGUIN PEP PER PERCENT PERCENTAGE PERFECTLY PERFORM PERFORMANCE PERFORMED "
            + "PERFORMING PERFORMS PERHAPS PERIOD PERMITTED PERSISTS PHASE PHILOSOPHER PHRASE PHYSICALLY PI PICK PID PIECE "
            + "PKG PLACE PLACED PLACEHOLDER PLACES PLACING PLAIN PLATFORM PLATFORMS PLAY PLAYERS PLEASE PLUS PM PNG POINT "
            + "POINTS POP POPEN POPITEM POPULATED PORTABLE POS POSITION POSITIONAL POSITIONS POSITIVE POSONLY POSONLYARGC "
            + "POSSESSIVES POSSIBLE POSSIBLY POST POSTPONE POSTPONED POTENTIAL POW POWER POWERFUL PP PPRINT PRACTICE "
            + "PRACTICES PRE PRECARIOUS PRECEDE PRECEDED PRECEDENCE PRECEDES PRECEDING PRECISE PRECISELY PRECISION "
            + "PRECOMPOSED PREDICTABLE PREFERABLY PREFIX PREFIXED PREFIXES PREPARE PREPARED PREPARING PREREQUISIT PRESENCE "
            + "PRESENT PRESENTATIO PRESENTED PRESERVE PRESERVED PRESERVES PRESERVING PRESET PRESSES PRESSING PRETEND "
            + "PRETTY PREVAILS PREVENT PREVENTS PREVIOUS PREVIOUSLY PRIMARIES PRIMARILY PRIMARY PRINT PRINTABLE PRINTED "
            + "PRINTF PRINTING PRINTS PRIOR PRIORITIES PRIORITY PRIVATE PROBLEM PROCEED PROCEEDS PROCESS PROCESSED "
            + "PROCESSES PROCESSING PROCESSOR PRODUCE PRODUCED PRODUCES PRODUCT PRODUCTION PROGRAM PROGRAMMER PROGRAMMERS "
            + "PROGRAMS PROMPT PRONE PROPAGATED PROPAGATES PROPER PROPERLY PROPERTIES PROPERTY PROPLIST PROPOSAL PROPOSED "
            + "PROTECTION PROTOCOL PROTOCOLS PROVE PROVIDE PROVIDED PROVIDES PROVIDING PROVISION PROXIES PROXY PS PSEUDO "
            + "PUBLIC PUNCTUATION PURELY PURPOSE PURPOSES PUSHED PUT PY PYARG PYC PYFILE PYTHON PYTHONHASHS PYTHONNODEB "
            + "PYTHONX QUADRATIC QUALIFIED QUALITY QUALNAME QUEST QUIT QUITE QUITTING QUOTE QUOTED QUOTES QUOTIENT QUOTING "
            + "RADD RAISE RAISED RAISES RAISING RAND RANDOM RANDOMIZATI RANGE RANGES RATE RATHER RATIONAL RATIONALE RAW "
            + "RAYMOND RB RDIVMOD RE REACH REACHABLE REACHED REACHES REACHING READ READABILITY READER READING READLINE "
            + "READRC READS REAK REAL REALITY REALIZE REALLY REASON REASONS REBIND REBOUND RECEIVED RECEIVES RECEIVING "
            + "RECENT RECIPE RECOGNISED RECOGNIZE RECOGNIZED RECOMMENDED RECORD RECREATE RECURSION RECURSIVE RECURSIVELY "
            + "RED REDEFINE REDEFINITIO REDUCE REDUCED REDUNDANT REFER REFERENCE REFERENCED REFERENCES REFERRED REFERS "
            + "REFLECT REFLECTED REFLECTION REFLECTS REFLEXIVE REFLEXIVITY REGARDED REGARDLESS REGISTER REGISTERED REGULAR "
            + "REGULARLY REINTRODUCE REJECTED RELATE RELATED RELATIONS RELATIONSHI RELATIVE RELAXED RELEASE RELEVANT "
            + "RELIABLE RELIED RELPATH RELY REMAIN REMAINDER REMAINING REMAINS REMEMBER REMIND REMOTE REMOVAL REMOVE "
            + "REMOVED REMOVEPREFI REMOVES REMOVESUFFI REMOVING RENDERS REPAIR REPEAT REPEATED REPEATEDLY REPEATS "
            + "REPETITION REPL REPLACE REPLACEABLE REPLACED REPLACEMENT REPLACES REPLACING REPORTED REPR REPRESENT "
            + "REPRESENTAT REPRESENTED REPRESENTIN REPRESENTS REQUEST REQUESTED REQUESTING REQUIRE REQUIRED REQUIREMENT "
            + "REQUIRES REQUIRING RERAISE RERAISED RESEMBLES RESERVED RESERVES RESET RESOLUTION RESOLVE RESOLVED RESOLVING "
            + "RESOURCE RESOURCES RESPECT RESPECTIVE RESPECTIVEL RESPONSIBIL RESPONSIBLE REST RESTART RESTARTING RESTORING "
            + "RESTRICT RESTRICTED RESTRICTION RESTRICTIVE RESULT RESULTING RESULTS RESUME RESUMED RESUMES RESUMING "
            + "RESURRECTED RESURRECTIO RETAIN RETAINED RETRIEVAL RETRIEVE RETRIEVED RETRIEVING RETRY RETURN RETURNED "
            + "RETURNING RETURNS RETVAL REUSE REUSED REUSING REVERSE REVERSED REVERSIBLE REVERSING REWRITTEN RF RFIND "
            + "RFLOORDIV RGS RICH RIGHT RIGHTMOST RINDEX RJUST RKL RLSHIFT RMATMUL RMOD RMUL ROLES ROOT ROR ROUGHLY ROUND "
            + "ROUNDED ROUNDING ROUNDOFF ROUNDS ROW RPARTITION RPOW RROR RRSHIFT RSHIFT RSPLIT RSTRIP RSUB RT RTRUEDIV "
            + "RULE RULES RUN RUNCALL RUNEVAL RUNNING RUNS RUNTIME RUNTIMEERRO RXOR SAFE SAFELY SAFETY SAID SALAD SALARY "
            + "SALTED SAME SAMPLE SATISFY SAUSAGE SAVED SAVES SAVING SAVINGS SAY SCANNING SCANS SCENARIO SCHEME SCIENTIFIC "
            + "SCOPE SCOPED SCOPES SCOPING SCRIPT SCRIPTS SEARCH SEARCHED SEARCHES SEARCHING SECOND SECTION SECTIONS "
            + "SECURITY SEE SEEMINGLY SELECT SELECTED SELECTS SELF SEMANTIC SEMANTICALL SEMANTICS SEMICOLON SEMICOLONS "
            + "SENSE SENSIBLE SENSITIVE SENT SENTINEL SEP SEPARATE SEPARATED SEPARATELY SEPARATING SEPARATOR SEPARATORS "
            + "SEQ SEQUENCE SEQUENCES SEQUENTIALL SERIES SERVICE SERVICES SESSION SET SETATTR SETDEFAULT SETITEM SETS "
            + "SETTING SETTRACE SEVERAL SHALL SHALLOW SHALT SHARE SHARED SHARING SHELL SHIFT SHIFTING SHIFTS SHLEX "
            + "SHORTCUT SHORTCUTS SHORTER SHORTHAND SHOULD SHOW SHOWN SHOWS SHUTDOWN SIDE SIDES SIGINT SIGN SIGNAL SIGNALS "
            + "SIGNED SIGNIFICAND SIGNIFICANT SIGNIFY SILENT SIMILAR SIMILARITY SIMILARLY SIMPLE SIMPLEEXPR SIMPLEPARAM "
            + "SIMPLEST SIMPLETYPEV SIMPLIFIED SIMPLIFY SIMPLY SIMULTANEOU SIN SINCE SING SINGLE SINGLEDISPA SINGLETON "
            + "SINGLETONS SITE SITUATIONS SIZE SIZES SJOERD SKIP SKIPPING SKIPS SLICE SLICED SLICES SLICING SLICINGS "
            + "SLIGHTLY SLOT SLOTS SLOTTED SMALL SMALLER SMALLEST SMASHING SNAKE SNIPPET SO SOCKET SOCKETS SOFT SOLE "
            + "SOLELY SOLVED SOME SOMETHING SOMETIMES SOMEWHERE SOON SOPHISTICAT SORT SORTED SORTING SORTS SOURCE SP SPACE "
            + "SPACES SPACING SPACIOUS SPAM SPAN SPANISH SPEAKING SPEC SPECIAL SPECIALATTR SPECIALISED SPECIALIZAT "
            + "SPECIALLY SPECIALNAME SPECIFIC SPECIFICALL SPECIFICATI SPECIFIED SPECIFIER SPECIFIERS SPECIFIES SPECIFY "
            + "SPECIFYING SPECS SPEED SPELLED SPELLING SPHINX SPLIT SPLITLINES SPLITS SPLITTING SQUARE SS SSIZE STABLE "
            + "STACK STACKSIZE STANDALONE STANDARD STANDS STAR STARARGS STARRED START STARTED STARTING STARTS STARTSWITH "
            + "STATE STATED STATEMENT STATEMENTS STATES STATIC STATICALLY STATICMETHO STAY STAYS STDERR STDIN STDOUT STEP "
            + "STEPPING STEPS STILL STMT STOP STOPASYNCIT STOPITERATI STOPPING STOPS STORAGE STORE STORED STORES STORING "
            + "STR STRAIGHTFOR STRASSE STREAM STREAMS STRICT STRICTLY STRIDE STRIDING STRING STRINGCHAR STRINGCONTE "
            + "STRINGESCAP STRINGIO STRINGITEM STRINGPREFI STRINGS STRIP STRIPPED STRONGLY STRUCTURAL STRUCTURE STRUCTURES "
            + "STUFF STYLE STYLES SUB SUBCLASS SUBCLASSCHE SUBCLASSED SUBCLASSES SUBCLASSING SUBGROUP SUBGROUPS SUBJECT "
            + "SUBMODULE SUBMODULES SUBPATTERN SUBPATTERNS SUBPKG SUBPROCESS SUBRANGES SUBSCRIBE SUBSCRIBED SUBSCRIBING "
            + "SUBSCRIPT SUBSCRIPTAB SUBSCRIPTED SUBSCRIPTIN SUBSCRIPTIO SUBSCRIPTS SUBSECTION SUBSECTIONS SUBSEQUENCE "
            + "SUBSEQUENT SUBSET SUBSETS SUBSTITUTED SUBSTITUTIN SUBSTRING SUBTLE SUBTRACTION SUBTYPE SUBTYPES SUCCEED "
            + "SUCCEEDED SUCCEEDING SUCCEEDS SUCCESS SUCCESSFUL SUCCESSFULL SUCCESSIVE SUCCESSIVEL SUCH SUFFICIENT SUFFIX "
            + "SUFFIXES SUITABLE SUITE SUITES SUM SUMMARIZE SUMMARIZES SUMMARIZING SUMMARY SUPER SUPERCLASS SUPERSCRIPT "
            + "SUPERSET SUPERSETS SUPPLIED SUPPLY SUPPORT SUPPORTED SUPPORTING SUPPORTS SUPPOSE SUPPRESS SUPPRESSED SURE "
            + "SURROUNDED SURROUNDING SUSPEND SUSPENDED SWAPCASE SWAPPED SWAPS SWITCH SWITCHES SYMBOL SYMBOLS SYMMETRIC "
            + "SYNCHRONIZA SYNCHRONIZE SYNONYM SYNTACTIC SYNTACTICAL SYNTAX SYNTAXERROR SYNTAXWARNI SYS SYSTEM SYSTEMEXIT "
            + "TAB TABLE TABS TABSIZE TABULATION TAILORED TAKE TAKEN TAKES TALK TARGET TARGETS TASK TB TBREAK TCH TE "
            + "TECHNICALLY TECHNIQUE TECHNIQUES TED TEDERROR TELLY TEM TEMP TEMPLATE TEMPORARILY TEMPORARY TEN TEP TERM "
            + "TERMED TERMINAL TERMINATE TERMINATED TERMINATES TERMINATION TERMS TEST TESTED TESTHOOK TESTING TESTS TEXT "
            + "TEXTIOBASE TEXTUALLY TH THAN THAT THE THEIR THEM THEMSELVES THEN THERE THEREFORE THEREOF THESE THEY THING "
            + "THINK THIRD THIS THOSE THOU THOUGH THOUSANDS THREAD THREADED THREADING THREADS THREE THROUGH THUS TIED "
            + "TIGHTER TIGHTLY TIME TIMEOUT TIMES TION TITLE TITLECASE TITLECASED TMPDIRMIXIN TO TOGETHER TOKEN "
            + "TOKENIZATIO TOKENIZING TOKENS TOLD TONS TOO TOOLS TOP TOPICS TOTAL TOTALLY TOUCH TOWARDS TP TPFLAGS TR "
            + "TRACE TRACEBACK TRACEBACKOB TRACEBACKS TRACEBACKTY TRACED TRACES TRACING TRADEOFF TRADITIONAL TRAILING "
            + "TRANSFORMAT TRANSFORMED TRANSITIVE TRANSLATE TRANSLATED TRANSLATES TRANSLATION TRAVERSE TREAT TREATED "
            + "TREATS TREE TRICKS TRIED TRIES TRIGGERED TRIGGERING TRIPLE TRIPLES TRUE TRUEDIV TRUNC TRUNCATED TRUNCATES "
            + "TRUNCATION TRUTH TRY TRYING TS TSTRING TUE TUPLE TUPLES TURN TUTORIAL TWO TXT TY TYPE TYPEALIASTY TYPED "
            + "TYPEERROR TYPES TYPESFUNCTI TYPESMAPPIN TYPESMETHOD TYPESMODULE TYPESSEQ TYPEVAR TYPEVARTUPL TYPEVARWITH "
            + "TYPICAL TYPICALLY TYPING TYPOGRAPHIC UAX UGRANGES UIT UK ULTIMATELY UMP UN UNACCEPTABL UNAFFECTED UNALIAS "
            + "UNAMBIGUOUS UNARY UNAVAILABLE UNBIND UNBOUND UNBOUNDLOCA UNCASED UNCHANGEABL UNCHANGED UNCONDITION "
            + "UNDEFINED UNDER UNDERLYING UNDERSCORE UNDERSCORES UNDERSTAND UNDERSTOOD UNDISPLAY UNDOCUMENTE UNEQUAL "
            + "UNESCAPED UNEXPECTED UNFILLED UNHANDLED UNHASHABLE UNICODE UNICODEDATA UNICODEERRO UNION UNIQUE UNIQUELY "
            + "UNIT UNITS UNIVERSAL UNLESS UNLIKE UNLIMITED UNLISTED UNLOCKING UNNAMED UNNECESSARY UNORDERED UNPACKING "
            + "UNPACKINGS UNPARENTHES UNPREDICTAB UNREACHABLE UNRECOGNIZE UNSET UNSTRUCTURE UNT UNTIL UNUSED UNUSUAL "
            + "UNWINDS UNWOUND UP UPDATE UPDATED UPDATES UPDATING UPON UPPER UPPERCASE UPPERCASED UPPERCASING US USABLE "
            + "USAGE USE USED USEFUL USER USERS USES USING USR USUAL USUALLY UTF UTIL UTILITIES UTILITY VACUUM VAL VALID "
            + "VALIDATION VALIDITY VALUE VALUEERROR VALUES VARIABLE VARIABLES VARIANT VARIANTS VARIATIONS VARIES VARIOUS "
            + "VARNAMES VARY VERBOSE VERBOSEMODU VERSA VERSION VERSIONS VERSUS VERTICAL VERY VIA VICE VIEW VIEWS VIOLATE "
            + "VIOLATIONS VIRTUAL VISIBILITY VISIBLE VS VT VULGAR WAITING WALRUS WANT WANTING WANTS WARD WARNING WAS WAY "
            + "WAYS WE WEAK WEAKREF WEIGHT WELL WERE WHAT WHATEVER WHATIS WHATS WHEN WHENEVER WHERE WHETHER WHICH WHILE "
            + "WHITESPACE WHO WHOAMI WHOLE WHOSE WHY WIDTH WILD WILDCARD WILL WINDOWS WISHES WITH WITHIN WITHOUT WON WORD "
            + "WORDS WORK WORKAROUND WORKING WORKINGS WORKS WORLD WORST WOULD WOULDN WRAP WRAPPED WRAPPER WRAPS WRITABLE "
            + "WRITE WRITES WRITING WRITTEN WRONG WS WWW XDEAD XDEADBEEF XID XMLCHARREFR XOR XXXX XXXXXXXX YET YIELD "
            + "YIELDED YIELDING YIELDS YM YOU YOUR YOURSELF YTYPE ZA ZERO ZERODIVISIO ZEROINTEGER ZEROS ZFILL ZIP ZOO ZS "
            ).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
    }
}
