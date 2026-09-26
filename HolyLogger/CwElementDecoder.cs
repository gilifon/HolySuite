using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace HolyLogger
{
    // THE NEW DECODER (on test): it judges WHOLE dits, dahs and gaps, not 5 ms at a time.
    //
    // WHY IT EXISTS. On 25 September 2026 his radio was keyed on 10112 kHz with eight kinds of human
    // timing and heard weakly in Europe, every key-down logged. The plain decoder (CwDecoder) read 2
    // words of it: judged 5 ms at a time, tone and gaps overlapped on a third of its readings, and its
    // "is anyone there?" test never opened. Yet each whole dit or dah stood 8-13 dB clear of the gap
    // beside it. The difference is how an element is added up, and that is this decoder.
    //
    // HOW. The audio is moved down to 0 Hz at the station's note and summed every 5 ms, keeping the
    // phase. A tone keeps its phase for tens of milliseconds and noise does not, so adding 4 of those
    // readings WITH their phase is a filter only 50 Hz wide; each such 20 ms piece is then scored for
    // "tone" against "no tone", allowing the tone its own strength in every piece (fading). A search
    // over whole segments - dit, dah, and the three gaps, each of a length Morse allows at the speed
    // heard (a hidden semi-Markov model) - finds the run of elements the evidence supports best.
    // Nothing is guessed from words: no dictionary, only Morse's own timing.
    //
    // MEASURED against the plain decoder before it came here (Research/CwDecode, Python), words read /
    // invented: his three on-air sessions 749 / 662 against 531 / 704; W1AW's bulletin 1158 / 168
    // against 1009 / 327; the weak hand-sent session 22 against 2. It LOSES on his own IC-7610
    // recordings, 26 of 34 against 32 - strong, fast and crowded signals. So it is a choice on the
    // decode window's bar, not a replacement, and the plain decoder stays the default.
    //
    // LIVE, NOT A FILE. The research version read whole recordings. Here the last BufferSeconds of
    // audio are read again every StepSeconds, and a word is printed once LagSeconds of sound have
    // followed it - enough for the search to be sure where it ends. So words appear about two
    // seconds after they were sent, whole. The reading runs on its own thread: it takes a good part
    // of a second, and the capture thread must never wait for it.
    public sealed class CwElementDecoder : IDisposable
    {
        const double Hop = 0.005;              // one reading every 5 ms
        const int BufferSeconds = 24;
        const double StepSeconds = 1.0;
        const double LagSeconds = 2.0;

        // Each carried over from the research decoder, where each was measured - see the notes there
        // (ElementCoherent.py, ElementBlind.py, BlindFolder.py) for what was tried and lost.
        const int PieceFrames = 4;             // 20 ms pieces; shorter only for fast sending (below)
        const double LengthSpread = 0.30;      // how loosely a length may fit, in log units
        const int LongestGapUnits = 60;
        const double WordGate = 0.6;           // a word is shown only if its marks stood out this much
        const double Leak = 0.01;              // a strong station leaves 1% of itself in its own gaps
        const int HoldFrames = 2000;           // the station's strength remembered for 10 s
        const double HoldShare = 0.5;
        const double SwitchRatio = 1.3;        // move to another note only when it is this much stronger
        const double LowestNote = 300, HighestNote = 900;

        readonly int _rate;
        readonly int _hopSamples;
        readonly short[] _audio;
        int _fill;
        long _received;                        // samples ever received
        long _receivedAtLastRead;
        readonly object _gate = new object();

        readonly Thread _worker;               // null on the research bench - see the constructor
        readonly AutoResetEvent _wake = new AutoResetEvent(false);
        volatile bool _stopping;
        volatile bool _resetAsked;

        // Only the reading thread touches these.
        double _note;
        long _printedUpTo;                     // absolute frame: everything before it is on screen
        long _lastWordFrame = long.MinValue;

        public event Action<string> Text;

        public double ToneHz { get; private set; }
        public double Wpm { get; private set; }
        public bool SignalPresent { get; private set; }

        public CwElementDecoder(int sampleRate) : this(sampleRate, true) { }

        // onItsOwnThread false: each read happens inside Process, in step with the audio - for the
        // research bench, which must give the same answer every time it is run.
        internal CwElementDecoder(int sampleRate, bool onItsOwnThread)
        {
            _rate = sampleRate;
            _hopSamples = Math.Max(1, (int)Math.Round(Hop * sampleRate));
            _audio = new short[BufferSeconds * sampleRate];
            if (!onItsOwnThread) return;
            _worker = new Thread(ReadLoop) { IsBackground = true, Name = "CW element decoder", Priority = ThreadPriority.BelowNormal };
            _worker.Start();
        }

        public void Process(short[] samples, int count)
        {
            bool due;
            lock (_gate)
            {
                if (count >= _audio.Length)
                {
                    Array.Copy(samples, count - _audio.Length, _audio, 0, _audio.Length);
                    _fill = _audio.Length;
                }
                else
                {
                    int room = _audio.Length - _fill;
                    if (count > room)
                    {
                        int drop = count - room;
                        Array.Copy(_audio, drop, _audio, 0, _fill - drop);
                        _fill -= drop;
                    }
                    Array.Copy(samples, 0, _audio, _fill, count);
                    _fill += count;
                }
                _received += count;
                due = _received - _receivedAtLastRead >= (long)(StepSeconds * _rate);
                if (due) _receivedAtLastRead = _received;
            }
            if (!due) return;
            if (_worker != null) _wake.Set();
            else ReadNow(LagSeconds);
        }

        // The research bench's last word: everything left is read and printed, however recent.
        internal void Finish() { ReadNow(0); }

        public void Reset()
        {
            lock (_gate) { _fill = 0; }
            _resetAsked = true;
        }

        // The window has decided another station is answering. Nothing to forget here: the speed and
        // the operator's timing are measured afresh from the audio every read.
        public void ForgetTheOperator() { }

        public void Dispose()
        {
            _stopping = true;
            _wake.Set();
        }

        void ReadLoop()
        {
            while (!_stopping)
            {
                _wake.WaitOne();
                if (_stopping) break;
                ReadNow(LagSeconds);
            }
        }

        void ReadNow(double lagSeconds)
        {
            {
                if (_resetAsked)
                {
                    _resetAsked = false;
                    _note = 0; _printedUpTo = 0; _lastWordFrame = long.MinValue;
                    SignalPresent = false;
                }

                double[] x; long start;
                lock (_gate)
                {
                    // Start on a frame boundary, so frame numbers mean the same thing in every read.
                    start = _received - _fill;
                    int skip = (int)((_hopSamples - start % _hopSamples) % _hopSamples);
                    int n = _fill - skip;
                    if (n <= 0) return;
                    x = new double[n];
                    for (int i = 0; i < n; i++) x[i] = _audio[skip + i];
                    start += skip;
                }

                try { Read(x, start, lagSeconds); }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
            }
        }

        // ---- one read of the buffer ----

        void Read(double[] x, long startSample, double lagSeconds)
        {
            int T = x.Length / _hopSamples;
            if (T < 400) return;                               // two seconds at least
            long firstFrame = startSample / _hopSamples;
            long nowFrame = firstFrame + T;

            FollowTheNote(x);
            if (_note <= 0) return;
            ToneHz = _note;

            Complex[] z = MixedFrames(x, _note, startSample);
            double[] amp, noise;
            Levels(z, out amp, out noise);

            // THE SPEED, from the latest 20 s: read loosely at 20 WPM, take the dit from the marks that
            // reading drew, read again at that speed, and once more.
            int w0 = Math.Max(0, T - 4000);
            double unit = FindSpeed(z, amp, noise, w0, T);

            // Pieces of 4, shorter only below a 45 ms dit (measured: shorter pieces help fast sending
            // and cost everything slower).
            int piece = PieceFrames;
            if (unit < 9) piece = Math.Max(2, Math.Min(4, (int)Math.Round(unit / 3)));

            double[] F = Evidence(z, amp, noise, piece, 0, T);
            double[] kinds = { 1, 3, 1, 3, 7 };
            List<Segment> segs = Decode(F, T, unit, kinds, LengthSpread);

            // THE OPERATOR'S OWN TIMING, from that reading, and the audio read again with it.
            double dit;
            double[] learned = LearnTiming(segs, unit, kinds, out dit);
            segs = Decode(F, T, unit * dit, learned, LengthSpread);
            Wpm = 1200.0 / (unit * dit * Hop * 1000.0);

            PrintSettledWords(segs, F, firstFrame, nowFrame, lagSeconds);
            SignalPresent = _lastWordFrame > nowFrame - (long)(6 / Hop);
        }

        // Words that have LagSeconds of sound after them are settled: printed once, never again.
        void PrintSettledWords(List<Segment> segs, double[] F, long firstFrame, long nowFrame, double lagSeconds)
        {
            long settled = nowFrame - (long)(lagSeconds / Hop);
            var letters = new StringBuilder();
            var word = new StringBuilder();
            var pattern = new StringBuilder();
            double evidence = 0; int marks = 0; int wordStart = -1, wordEnd = -1;
            var said = new StringBuilder();

            foreach (Segment s in segs)
            {
                if (s.Kind <= 1)
                {
                    if (wordStart < 0) wordStart = s.Start;
                    wordEnd = s.End;
                    pattern.Append(s.Kind == 0 ? '.' : '-');
                    evidence += (F[s.End] - F[s.Start]) / (s.End - s.Start);
                    marks++;
                    continue;
                }
                if ((s.Kind == 3 || s.Kind == 4) && pattern.Length > 0)
                {
                    string letter;
                    if (CwDecoder.FromMorseTable.TryGetValue(pattern.ToString(), out letter)) word.Append(letter);
                    pattern.Clear();
                }
                if (s.Kind != 4) continue;

                // A word has ended, and the word gap that ends it has begun.
                if (wordStart >= 0)
                {
                    long a = firstFrame + wordStart, b = firstFrame + wordEnd;
                    if (a >= _printedUpTo && b <= settled)
                    {
                        if (word.Length > 0 && marks > 0 && evidence / marks >= WordGate)
                        {
                            said.Append(word).Append(' ');
                            _lastWordFrame = b;
                        }
                        _printedUpTo = b;       // judged, shown or not: never judged again
                    }
                }
                word.Clear(); evidence = 0; marks = 0; wordStart = -1; wordEnd = -1;
            }

            if (said.Length > 0)
            {
                var text = Text;
                if (text != null) text(said.ToString());
            }
        }

        // ---- the note ----

        // Like the plain decoder, follow the strongest note NOW, and move only when another is clearly
        // stronger - one note for a whole recording listened to the wrong one of four stations.
        void FollowTheNote(double[] x)
        {
            int n = 1; while (n < _rate / 2) n <<= 1;         // about half a second
            int span = Math.Min(x.Length, 10 * _rate);
            int from = x.Length - span;
            if (span < n) return;

            var mag = new double[n / 2 + 1];
            var re = new double[n]; var im = new double[n];
            int blocks = 0;
            for (int a = from; a + n <= x.Length; a += n / 2)
            {
                for (int i = 0; i < n; i++)
                {
                    double hann = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (n - 1));
                    re[i] = x[a + i] * hann; im[i] = 0;
                }
                Fft(re, im);
                for (int k = 0; k <= n / 2; k++) mag[k] += Math.Sqrt(re[k] * re[k] + im[k] * im[k]);
                blocks++;
            }
            if (blocks == 0) return;

            double binHz = (double)_rate / n;
            int lo = (int)Math.Ceiling(LowestNote / binHz), hi = (int)Math.Floor(HighestNote / binHz);
            int best = lo;
            for (int k = lo; k <= hi; k++) if (mag[k] > mag[best]) best = k;

            bool move = _note <= 0;
            if (!move)
            {
                int now = (int)Math.Round(_note / binHz);
                move = mag[best] > SwitchRatio * mag[Math.Max(0, Math.Min(n / 2, now))];
            }
            if (!move) return;

            var seg = new double[span];
            Array.Copy(x, from, seg, 0, span);
            _note = FindNote(seg, Math.Round(best * binHz / 25) * 25);
        }

        // The note, to a quarter of a Hz, at which 40 ms pieces add up loudest at their loudest -
        // a tone keeps its phase for that long and noise does not.
        double FindNote(double[] x, double rough)
        {
            double best = rough, bestLoud = -1;
            for (double p = rough - 12; p <= rough + 12.001; p += 1.0)
            {
                double l = Loudness(x, p);
                if (l > bestLoud) { bestLoud = l; best = p; }
            }
            double centre = best;
            for (double p = centre - 1; p <= centre + 1.001; p += 0.25)
            {
                double l = Loudness(x, p);
                if (l > bestLoud) { bestLoud = l; best = p; }
            }
            return best;
        }

        double Loudness(double[] x, double note)
        {
            Complex[] z = MixedFrames(x, note, 0);
            double[] e = PiecePower(z, 8);
            return Percentile(e, 95);
        }

        Complex[] MixedFrames(double[] x, double note, long startSample)
        {
            int T = x.Length / _hopSamples;
            var z = new Complex[T];
            double w = -2 * Math.PI * note / _rate;
            for (int f = 0; f < T; f++)
            {
                double re = 0, im = 0;
                int a = f * _hopSamples;
                for (int i = 0; i < _hopSamples; i++)
                {
                    double ph = w * ((startSample + a + i) % (long)(_rate * 1000L));
                    re += x[a + i] * Math.Cos(ph);
                    im += x[a + i] * Math.Sin(ph);
                }
                z[f] = new Complex(re, im);
            }
            return z;
        }

        // ---- the levels ----

        // Noise: the quiet 30% of the readings (noise power is exponential, so its 30th percentile
        // is 0.357 of its mean). Tone: the loud end of 40 ms pieces, less the noise that comes with
        // them. Both over +-5 s, the station held through pauses, and 1% of it allowed in its gaps.
        void Levels(Complex[] z, out double[] amp, out double[] noise)
        {
            int T = z.Length;
            var p = new double[T];
            for (int i = 0; i < T; i++) p[i] = z[i].Norm;
            noise = Local(p, 30);
            for (int i = 0; i < T; i++) noise[i] /= 0.357;

            double[] e8 = PiecePower(z, 8);
            double[] loud = Local(e8, 85);
            var tone = new double[T];
            for (int i = 0; i < T; i++) tone[i] = Math.Max((loud[i] - 8 * noise[i]) / 64, noise[i] * 0.05);

            var held = new double[T];
            for (int c = 0; c < T; c += 200)
            {
                double m = 0;
                for (int i = Math.Max(0, c - HoldFrames); i < Math.Min(T, c + HoldFrames); i++) if (tone[i] > m) m = tone[i];
                for (int i = c; i < Math.Min(T, c + 200); i++) held[i] = m;
            }
            amp = new double[T];
            for (int i = 0; i < T; i++)
            {
                tone[i] = Math.Max(tone[i], HoldShare * held[i]);
                noise[i] = Math.Max(noise[i], Leak * tone[i]);
                amp[i] = Math.Sqrt(tone[i]);
            }
        }

        static double[] Local(double[] v, double percent)
        {
            const int half = 1000, step = 200;
            var o = new double[v.Length];
            for (int c = 0; c < v.Length; c += step)
            {
                int a = Math.Max(0, c - half), b = Math.Min(v.Length, c + half);
                var s = new double[b - a];
                Array.Copy(v, a, s, 0, b - a);
                double q = Percentile(s, percent);
                for (int i = c; i < Math.Min(v.Length, c + step); i++) o[i] = q;
            }
            return o;
        }

        // |sum of c readings|^2 centred on each reading.
        static double[] PiecePower(Complex[] z, int c)
        {
            Complex[] s = PieceSums(z, c);
            var e = new double[z.Length];
            for (int i = 0; i < z.Length; i++) e[i] = s[i].Norm;
            return e;
        }

        static Complex[] PieceSums(Complex[] z, int c)
        {
            int T = z.Length;
            var C = new Complex[T + 1];
            for (int i = 0; i < T; i++) C[i + 1] = C[i] + z[i];
            var S = new Complex[T];
            int n = T - c + 1;
            for (int i = 0; i < T; i++)
            {
                int j = Math.Max(0, Math.Min(n - 1, i - c / 2));
                S[i] = C[j + c] - C[j];
            }
            return S;
        }

        // ---- the evidence and the search ----

        // For every reading, how much more likely "tone" is than "no tone", judged on the 20 ms piece
        // centred on it with the tone allowed its own strength (Rayleigh against exponential); summed,
        // so a stretch's evidence is one subtraction.
        static double[] Evidence(Complex[] z, double[] amp, double[] noise, int c, int from, int to)
        {
            Complex[] S = PieceSums(z, c);
            var F = new double[to - from + 1];
            for (int i = from; i < to; i++)
            {
                double cn = c * noise[i], cp = c * c * amp[i] * amp[i];
                double llr = Math.Log(cn / (cn + cp)) + S[i].Norm * (1 / cn - 1 / (cn + cp));
                F[i - from + 1] = F[i - from] + llr / c;
            }
            return F;
        }

        struct Segment
        {
            public int Kind, Start, End;       // 0 dit, 1 dah, 2 gap in a letter, 3 between letters, 4 between words
        }

        // The best run of whole dits, dahs and gaps over readings 0..T, each of a length Morse allows:
        // a mark follows a gap, a gap follows a mark; marks score their evidence, gaps score nothing
        // (they are what everything is measured against), and every length pays for how far it is
        // from what the speed says it should be.
        static List<Segment> Decode(double[] F, int T, double unit, double[] kinds, double spread)
        {
            const double Worst = -1e18;
            var best = new double[T + 1, 5];
            var fromStart = new int[T + 1, 5];
            var fromKind = new int[T + 1, 5];
            for (int t = 0; t <= T; t++) for (int k = 0; k < 5; k++) best[t, k] = Worst;
            best[0, 4] = 0;

            // best ending with a mark / with a gap, at each reading
            var markBest = new double[T + 1]; var markKind = new int[T + 1];
            var gapBest = new double[T + 1]; var gapKind = new int[T + 1];

            int[] lo = new int[5], hi = new int[5];
            double[][] lengthScore = new double[5][];
            for (int k = 0; k < 5; k++)
            {
                double want = kinds[k] * unit;
                lo[k] = Math.Max(1, (int)(want * Math.Exp(-3 * spread)));
                hi[k] = k != 4 ? (int)(want * Math.Exp(3 * spread)) + 1 : (int)(LongestGapUnits * unit);
                lengthScore[k] = new double[hi[k] + 1];
                for (int d = lo[k]; d <= hi[k]; d++)
                {
                    double ld = Math.Log(d / want);
                    lengthScore[k][d] = (k == 4 && ld > 0) ? 0 : -0.5 * (ld / spread) * (ld / spread);
                }
            }

            UpdateBest(best, 0, markBest, markKind, gapBest, gapKind);
            for (int t = 1; t <= T; t++)
            {
                for (int k = 0; k < 5; k++)
                {
                    bool isMark = k <= 1;
                    int top = Math.Min(hi[k], t);
                    double bestScore = Worst; int bestS = -1, bestPrev = 0;
                    for (int d = lo[k]; d <= top; d++)
                    {
                        int s = t - d;
                        double prev = isMark ? gapBest[s] : markBest[s];
                        if (prev <= Worst / 2) continue;
                        double score = prev + lengthScore[k][d] + (isMark ? F[t] - F[s] : 0);
                        if (score > bestScore) { bestScore = score; bestS = s; bestPrev = isMark ? gapKind[s] : markKind[s]; }
                    }
                    if (bestS >= 0) { best[t, k] = bestScore; fromStart[t, k] = bestS; fromKind[t, k] = bestPrev; }
                }
                UpdateBest(best, t, markBest, markKind, gapBest, gapKind);
            }

            int kk = 0;
            for (int k = 1; k < 5; k++) if (best[T, k] > best[T, kk]) kk = k;
            var segs = new List<Segment>();
            int tt = T;
            while (tt > 0 && best[tt, kk] > Worst / 2)
            {
                int s = fromStart[tt, kk];
                segs.Add(new Segment { Kind = kk, Start = s, End = tt });
                int pk = fromKind[tt, kk];
                tt = s; kk = pk;
            }
            segs.Reverse();
            return segs;
        }

        static void UpdateBest(double[,] best, int t, double[] markBest, int[] markKind, double[] gapBest, int[] gapKind)
        {
            markKind[t] = best[t, 0] >= best[t, 1] ? 0 : 1;
            markBest[t] = best[t, markKind[t]];
            int g = 2;
            if (best[t, 3] > best[t, g]) g = 3;
            if (best[t, 4] > best[t, g]) g = 4;
            gapKind[t] = g; gapBest[t] = best[t, g];
        }

        // ---- speed and timing ----

        double FindSpeed(Complex[] z, double[] amp, double[] noise, int from, int to)
        {
            int n = to - from;
            var zz = new Complex[n]; var aa = new double[n]; var nn = new double[n];
            Array.Copy(z, from, zz, 0, n); Array.Copy(amp, from, aa, 0, n); Array.Copy(noise, from, nn, 0, n);
            double[] F = Evidence(zz, aa, nn, PieceFrames, 0, n);
            double[] book = { 1, 3, 1, 3, 7 };
            double g = 12;
            for (int it = 0; it < 3; it++)
            {
                List<Segment> segs = Decode(F, n, g, book, it == 0 ? 0.5 : 0.35);
                var L = new List<double>();
                foreach (Segment s in segs) if (s.Kind <= 1) L.Add(Math.Log(s.End - s.Start));
                if (L.Count == 0) L.Add(Math.Log(g));
                double[] arr = L.ToArray();
                double lo = Percentile(arr, 20), hi = Percentile(arr, 80);
                for (int i = 0; i < 15; i++)
                {
                    double mid = (lo + hi) / 2, sa = 0, sb = 0; int na = 0, nb = 0;
                    foreach (double v in arr) { if (v < mid) { sa += v; na++; } else { sb += v; nb++; } }
                    if (na == 0 || nb == 0) break;
                    lo = sa / na; hi = sb / nb;
                }
                g = Math.Min(Math.Max(Math.Exp(lo), 5.0), 35.0);
            }
            return g;
        }

        static double[] LearnTiming(List<Segment> segs, double unit, double[] book, out double dit)
        {
            var lens = new List<double>[5];
            for (int k = 0; k < 5; k++) lens[k] = new List<double>();
            foreach (Segment s in segs) lens[s.Kind].Add((s.End - s.Start) / unit);
            dit = lens[0].Count > 10 ? Median(lens[0]) : 1.0;
            double[] lo = { 1, 2.4, 0.6, 2.2, 5.0 }, hi = { 1, 4.5, 1.6, 5.0, 12.0 };
            var learned = (double[])book.Clone();
            for (int k = 1; k < 5; k++)
                if (lens[k].Count > 10) learned[k] = Math.Min(Math.Max(Median(lens[k]) / dit, lo[k]), hi[k]);
            return learned;
        }

        // ---- small tools ----

        static double Median(List<double> v) { return Percentile(v.ToArray(), 50); }

        // As numpy's percentile: linear between the two nearest ranks.
        static double Percentile(double[] v, double percent)
        {
            if (v.Length == 0) return 0;
            var s = (double[])v.Clone();
            Array.Sort(s);
            double pos = percent / 100.0 * (s.Length - 1);
            int i = (int)Math.Floor(pos);
            if (i >= s.Length - 1) return s[s.Length - 1];
            return s[i] + (pos - i) * (s[i + 1] - s[i]);
        }

        static void Fft(double[] re, double[] im)
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
                        int a = i + k, b = i + k + len / 2;
                        double tr = re[b] * cr - im[b] * ci, ti = re[b] * ci + im[b] * cr;
                        re[b] = re[a] - tr; im[b] = im[a] - ti; re[a] += tr; im[a] += ti;
                        double nr = cr * wr - ci * wi; ci = cr * wi + ci * wr; cr = nr;
                    }
                }
            }
        }

        struct Complex
        {
            public readonly double Re, Im;
            public Complex(double re, double im) { Re = re; Im = im; }
            public double Norm { get { return Re * Re + Im * Im; } }
            public static Complex operator +(Complex a, Complex b) { return new Complex(a.Re + b.Re, a.Im + b.Im); }
            public static Complex operator -(Complex a, Complex b) { return new Complex(a.Re - b.Re, a.Im - b.Im); }
        }
    }
}
