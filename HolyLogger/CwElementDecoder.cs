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
    // AS IT IS SENT, LETTER BY LETTER. The first version here re-read the last 24 s every second and
    // printed a word once two seconds had followed it; he wanted it as immediate as the plain decoder
    // ("no delay!"). So the search now runs forward one 5 ms reading at a time, and a letter is printed
    // the moment the gap after it has grown too long to be a gap inside a letter - which is also when
    // the plain decoder decides. Only the slow things are still worked out from a stretch of the past:
    // the note (every second), the levels (every second) and the speed and the operator's timing
    // (every two seconds).
    public sealed class CwElementDecoder : IDisposable
    {
        const double Hop = 0.005;              // one reading every 5 ms

        // Each carried over from the research decoder, where each was measured - see the notes there
        // (ElementCoherent.py, ElementBlind.py, BlindFolder.py) for what was tried and lost.
        const int PieceFrames = 4;             // 20 ms pieces; shorter only for fast sending (below)
        const double LengthSpread = 0.30;      // how loosely a length may fit, in log units
        const int LongestGapUnits = 60;
        const double LetterGate = 0.6;         // a letter is shown only if its marks stood out this much
        const double InWordGate = 0.1;         // ...or this much, after a shown letter in the same word
        const double Leak = 0.01;              // a strong station leaves 1% of itself in its own gaps
        const int HoldFrames = 4000;           // the station's strength remembered for 20 s
        const double HoldShare = 0.5;
        const double SwitchRatio = 1.3;        // move to another note only when it is this much stronger
        const double LowestNote = 300, HighestNote = 900;

        const int Ring = 8192;                 // readings kept: 41 s, a power of two
        const int LevelsEvery = 200;           // readings between level updates (1 s)
        const int LevelsOver = 2000;           // readings the levels are taken over (10 s)
        const int SpeedEvery = 400;            // readings between speed searches (2 s)
        const int SpeedOver = 4000;            // readings the speed is found from (20 s)
        const int EmitEvery = 4;               // readings between looks for finished letters (20 ms)

        readonly int _rate;
        readonly int _hopSamples;

        // Samples waiting for the reading thread, and the last 10 s of sound for finding the note.
        readonly object _gate = new object();
        readonly List<short> _waiting = new List<short>();
        readonly short[] _sound;
        long _soundCount;

        readonly Thread _worker;               // null on the research bench - see the constructor
        readonly AutoResetEvent _wake = new AutoResetEvent(false);
        volatile bool _stopping;
        volatile bool _resetAsked;

        // ---- only the reading thread touches anything below ----

        double _note;
        long _sample;                          // samples mixed so far
        double _accRe, _accIm; int _accN;      // the 5 ms reading being summed

        readonly Complex[] _z = new Complex[Ring];
        readonly double[] _tonePower = new double[Ring], _noise = new double[Ring];
        readonly double[] _F = new double[Ring];   // summed evidence up to (not including) each reading
        long _frames;                          // readings made
        long _evidenceTo = -1;                 // _F known up to here; -1 until the levels exist
        double _levelTone, _levelNoise;
        readonly List<double> _recentTone = new List<double>();

        double _unit = 12;                     // readings per dit, the operator's own timing included
        double[] _kinds = { 1, 3, 1, 3, 7 };
        int _piece = PieceFrames;
        int[] _lo = new int[5], _hi = new int[5];
        double[][] _lengthScore = new double[5][];

        // The forward search, one column per reading.
        readonly double[] _best = new double[Ring * 5];
        readonly long[] _fromStart = new long[Ring * 5];
        readonly int[] _fromKind = new int[Ring * 5];
        readonly double[] _markBest = new double[Ring], _gapBest = new double[Ring];
        readonly int[] _markKind = new int[Ring], _gapKind = new int[Ring];
        long _searchFrom = -1;                 // where the search began, as if after a word gap
        long _searchTo = -1;

        long _shownTo;                         // the last mark of the last letter judged ends here
        bool _spaceOwed;                       // a letter has been shown since the last space
        long _lastShown = long.MinValue;
        long _overStart;                       // where the present over began (first letter after a pause)
        const double OverGapSeconds = 3.0;     // a pause this long ends an over

        public event Action<string> Text;

        // For the research bench only: every letter judged - its dits and dahs, where its last mark
        // ends (reading), its evidence, and whether it was shown.
        internal Action<string, long, double, bool> Judged;

        public double ToneHz { get; private set; }
        public double Wpm { get; private set; }
        public bool SignalPresent { get; private set; }

        public CwElementDecoder(int sampleRate) : this(sampleRate, true) { }

        // onItsOwnThread false: the work happens inside Process, in step with the audio - for the
        // research bench, which must give the same answer every time it is run.
        internal CwElementDecoder(int sampleRate, bool onItsOwnThread)
        {
            _rate = sampleRate;
            _hopSamples = Math.Max(1, (int)Math.Round(Hop * sampleRate));
            _sound = new short[10 * sampleRate];
            SetLengths();
            if (!onItsOwnThread) return;
            _worker = new Thread(WorkLoop) { IsBackground = true, Name = "CW element decoder", Priority = ThreadPriority.BelowNormal };
            _worker.Start();
        }

        public void Process(short[] samples, int count)
        {
            if (_worker == null) { Consume(samples, count); return; }
            lock (_gate) for (int i = 0; i < count; i++) _waiting.Add(samples[i]);
            _wake.Set();
        }

        // The research bench's last word: the letter still open is judged as if silence followed.
        internal void Finish() { ShowFinishedLetters(true); }

        public void Reset() { _resetAsked = true; if (_worker != null) _wake.Set(); }

        // The window has decided another station is answering: the search starts afresh, and the
        // speed with it.
        public void ForgetTheOperator() { _resetAsked = true; if (_worker != null) _wake.Set(); }

        public void Dispose()
        {
            _stopping = true;
            _wake.Set();
        }

        void WorkLoop()
        {
            while (!_stopping)
            {
                _wake.WaitOne();
                if (_stopping) break;
                short[] got;
                lock (_gate) { got = _waiting.ToArray(); _waiting.Clear(); }
                try { Consume(got, got.Length); }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
            }
        }

        void Consume(short[] samples, int count)
        {
            if (_resetAsked) { _resetAsked = false; StartAfresh(); }

            for (int i = 0; i < count; i++)
            {
                short v = samples[i];
                _sound[(int)(_soundCount % _sound.Length)] = v;
                _soundCount++;

                if (_note > 0)
                {
                    double ph = -2 * Math.PI * _note * (_sample % (_rate * 1000L)) / _rate;
                    _accRe += v * Math.Cos(ph); _accIm += v * Math.Sin(ph);
                }
                _sample++;
                if (++_accN < _hopSamples) continue;

                if (_note > 0) AddReading(new Complex(_accRe, _accIm));
                _accRe = _accIm = 0; _accN = 0;

                // Once a second: is the station we follow still the strongest?
                if (_sample % _rate < _hopSamples) FollowTheNote();
            }
        }

        void StartAfresh()
        {
            _frames = 0; _evidenceTo = -1; _searchFrom = -1; _searchTo = -1;
            _shownTo = 0; _spaceOwed = false; _lastShown = long.MinValue;
            _recentTone.Clear();
            _unit = 12; _kinds = new double[] { 1, 3, 1, 3, 7 }; _piece = PieceFrames; SetLengths();
            SignalPresent = false;
        }

        // ---- one reading at a time ----

        // For the research bench: the sample the first reading began at, so readings can be put
        // against the recording's own clock.
        internal long FirstReadingSample = -1;

        // For the research bench: hold every letter back this long before it is final - to measure
        // what printing at once costs. 0 in HolyLogger.
        internal static double HoldBackSeconds = 0;

        void AddReading(Complex z)
        {
            if (_frames == 0) FirstReadingSample = _sample - _hopSamples;
            long f = _frames++;
            _z[(int)(f & (Ring - 1))] = z;

            if (_frames % LevelsEvery == 0 && _frames >= 2 * LevelsEvery) UpdateLevels();
            if (_evidenceTo < 0) return;

            // The piece for reading i reaches c - 1 - c/2 readings ahead of it, so reading i is judged
            // once that one has arrived. A loop, because a shorter piece after a speed change can make
            // two readings ready at once.
            while (_evidenceTo <= f - (_piece - 1 - _piece / 2))
            {
                long i = _evidenceTo;
                _tonePower[(int)(i & (Ring - 1))] = _levelTone;
                _noise[(int)(i & (Ring - 1))] = _levelNoise;
                Complex s = new Complex(0, 0);
                for (long j = i - _piece / 2; j < i - _piece / 2 + _piece; j++)
                    s = s + _z[(int)(Math.Max(j, _frames - Ring) & (Ring - 1))];
                double cn = _piece * _levelNoise, cp = (double)_piece * _piece * _levelTone;
                double llr = Math.Log(cn / (cn + cp)) + s.Norm * (1 / cn - 1 / (cn + cp));
                _F[(int)((i + 1) & (Ring - 1))] = _F[(int)(i & (Ring - 1))] + llr / _piece;
                _evidenceTo = i + 1;

                SearchStep(_evidenceTo);
                if (_evidenceTo % EmitEvery == 0) ShowFinishedLetters(false);
            }
            // A NEW OVER IS MEASURED ON ITS OWN, AND OFTEN. The first letters after a pause were read
            // at the last station's speed, and the speed was then found from 20 s that were mostly
            // his: "VVV DE 4Z5SL" at the start of each part of session 3 (20, then 25, then 30 WPM)
            // came out as TVMEZ5SL and YY DE XZ5SL. For its first 4 s an over is re-measured every
            // half second, from itself alone.
            bool newOver = _evidenceTo - _overStart < 4 * SpeedEvery / 2;
            if (_frames % (newOver ? SpeedEvery / 4 : SpeedEvery) == 0) UpdateSpeed();
        }

        // Noise: the quiet 30% of the last 10 s of readings (noise power is exponential, so its 30th
        // percentile is 0.357 of its mean). Tone: the loud end of 40 ms pieces, less the noise that
        // comes with them; held at half the strongest of the last 20 s through pauses; and 1% of it
        // allowed in the gaps.
        void UpdateLevels()
        {
            int n = (int)Math.Min(LevelsOver, _frames);
            var z = LastReadings(n);
            var p = new double[n];
            for (int k = 0; k < n; k++) p[k] = z[k].Norm;
            double noise = Percentile(p, 30) / 0.357;
            double loud = Percentile(PiecePower(z, 8), 85);
            double tone = Math.Max((loud - 8 * noise) / 64, noise * 0.05);

            _recentTone.Add(tone);
            while (_recentTone.Count > HoldFrames / LevelsEvery) _recentTone.RemoveAt(0);
            double held = 0; foreach (double t in _recentTone) if (t > held) held = t;
            tone = Math.Max(tone, HoldShare * held);
            noise = Math.Max(noise, Leak * tone);
            _levelTone = tone; _levelNoise = noise;

            if (_evidenceTo < 0)
            {
                // The first time: the evidence and the search begin here, as if after a word gap.
                _evidenceTo = _frames;
                _F[(int)(_evidenceTo & (Ring - 1))] = 0;
                StartSearchAt(_evidenceTo);
            }
        }

        Complex[] LastReadings(int n)
        {
            var z = new Complex[n];
            for (int k = 0; k < n; k++) z[k] = _z[(int)((_frames - n + k) & (Ring - 1))];
            return z;
        }

        // THE SPEED, from the last 20 s: read loosely at 20 WPM, take the dit from the marks that
        // reading drew, read again at that speed, and once more. Then the operator's own timing from a
        // reading at that speed. Both then rule the forward search from here on.
        void UpdateSpeed()
        {
            long to = _evidenceTo;
            int n = (int)Math.Min(SpeedOver, Math.Min(to - _searchFrom, Ring - 64));
            if (to - _overStart >= SpeedEvery / 2) n = (int)Math.Min(n, to - _overStart + 50);
            if (n < 400) return;
            var z = new Complex[n]; var amp = new double[n]; var noise = new double[n];
            for (int k = 0; k < n; k++)
            {
                int at = (int)((to - n + k) & (Ring - 1));
                z[k] = _z[at]; amp[k] = Math.Sqrt(_tonePower[at]); noise[k] = _noise[at];
            }
            double unit = FindSpeed(z, amp, noise, 0, n);
            double[] book = { 1, 3, 1, 3, 7 };
            double[] F = Evidence(z, amp, noise, PieceFrames, 0, n);
            double dit;
            _kinds = LearnTiming(Decode(F, n, unit, book, LengthSpread), unit, book, out dit);
            _unit = unit * dit;

            // Pieces of 4, shorter only below a 45 ms dit (measured: shorter pieces help fast sending
            // and cost everything slower).
            _piece = unit < 9 ? Math.Max(2, Math.Min(4, (int)Math.Round(unit / 3))) : PieceFrames;
            SetLengths();
            Wpm = 1200.0 / (_unit * Hop * 1000.0);
        }

        void SetLengths()
        {
            for (int k = 0; k < 5; k++)
            {
                double want = _kinds[k] * _unit;
                _lo[k] = Math.Max(1, (int)(want * Math.Exp(-3 * LengthSpread)));
                _hi[k] = k != 4 ? (int)(want * Math.Exp(3 * LengthSpread)) + 1 : (int)(LongestGapUnits * _unit);
                _hi[k] = Math.Min(_hi[k], Ring - 64);
                _lengthScore[k] = new double[_hi[k] + 1];
                for (int d = _lo[k]; d <= _hi[k]; d++)
                {
                    double ld = Math.Log(d / want);
                    _lengthScore[k][d] = (k == 4 && ld > 0) ? 0 : -0.5 * (ld / LengthSpread) * (ld / LengthSpread);
                }
            }
        }

        // ---- the forward search ----

        const double Worst = -1e18;

        void StartSearchAt(long t)
        {
            _searchFrom = t; _searchTo = t;
            int c = (int)(t & (Ring - 1));
            for (int k = 0; k < 5; k++) _best[c * 5 + k] = Worst;
            _best[c * 5 + 4] = 0;
            RememberBest(t);
            _shownTo = t;
            _overStart = t;
        }

        // The best way to arrive at reading t ending with each kind of segment: a mark follows a gap, a
        // gap follows a mark; marks score their evidence, gaps score nothing, and every length pays for
        // how far it is from what the speed says it should be.
        void SearchStep(long t)
        {
            if (_searchFrom < 0 || t <= _searchTo) return;
            int c = (int)(t & (Ring - 1));
            double Ft = _F[c];
            for (int k = 0; k < 5; k++)
            {
                bool isMark = k <= 1;
                long top = Math.Min(_hi[k], t - _searchFrom);
                double bestScore = Worst; long bestS = -1; int bestPrev = 0;
                double[] ls = _lengthScore[k];
                for (int d = _lo[k]; d <= top; d++)
                {
                    long s = t - d;
                    int si = (int)(s & (Ring - 1));
                    double prev = isMark ? _gapBest[si] : _markBest[si];
                    if (prev <= Worst / 2) continue;
                    double score = prev + ls[d] + (isMark ? Ft - _F[si] : 0);
                    if (score > bestScore) { bestScore = score; bestS = s; bestPrev = isMark ? _gapKind[si] : _markKind[si]; }
                }
                _best[c * 5 + k] = bestS >= 0 ? bestScore : Worst;
                _fromStart[c * 5 + k] = bestS;
                _fromKind[c * 5 + k] = bestPrev;
            }
            RememberBest(t);
            _searchTo = t;
        }

        void RememberBest(long t)
        {
            int c = (int)(t & (Ring - 1));
            _markKind[c] = _best[c * 5] >= _best[c * 5 + 1] ? 0 : 1;
            _markBest[c] = _best[c * 5 + _markKind[c]];
            int g = 2;
            if (_best[c * 5 + 3] > _best[c * 5 + g]) g = 3;
            if (_best[c * 5 + 4] > _best[c * 5 + g]) g = 4;
            _gapKind[c] = g; _gapBest[c] = _best[c * 5 + g];
        }

        // ---- what is shown ----

        // The best path to now, traced back to the last letter shown. A letter is finished once the
        // gap after it is a gap between letters or words - or, while that gap is still growing, once it
        // is longer than any gap inside a letter can be. A space is owed once a gap is longer than the
        // middle of a letter gap and a word gap.
        void ShowFinishedLetters(bool atTheEnd)
        {
            long t = _searchTo;
            if (_searchFrom < 0 || t <= _searchFrom) return;

            int c = (int)(t & (Ring - 1));
            int kk = 0;
            for (int k = 1; k < 5; k++) if (_best[c * 5 + k] > _best[c * 5 + kk]) kk = k;
            var segs = new List<Segment>();
            long tt = t;
            while (tt > _shownTo && tt > _searchFrom && t - tt < Ring - 1024)
            {
                int ci = (int)(tt & (Ring - 1));
                long s = _fromStart[ci * 5 + kk];
                if (s < 0) break;
                segs.Add(new Segment { Kind = kk, Start = (int)(s - _searchFrom), End = (int)(tt - _searchFrom) });
                int pk = _fromKind[ci * 5 + kk];
                tt = s; kk = pk;
            }
            segs.Reverse();

            double letterEnds = Math.Sqrt(_kinds[2] * _kinds[3]) * _unit * 1.25;
            double wordEnds = Math.Sqrt(_kinds[3] * _kinds[4]) * _unit;
            var said = new StringBuilder();
            var pattern = new StringBuilder();
            double evidence = 0; long lastMarkEnd = -1, a0OfLetter = -1;

            for (int n = 0; n < segs.Count; n++)
            {
                Segment sg = segs[n];
                long a = sg.Start + _searchFrom, b = sg.End + _searchFrom;
                if (b <= _shownTo) continue;
                if (sg.Kind <= 1)
                {
                    if (a < _shownTo) continue;            // part of a letter already judged
                    if (pattern.Length == 0) a0OfLetter = a;
                    pattern.Append(sg.Kind == 0 ? '.' : '-');
                    evidence += (_F[(int)(b & (Ring - 1))] - _F[(int)(a & (Ring - 1))]) / (b - a);
                    lastMarkEnd = b;
                    continue;
                }

                bool last = n == segs.Count - 1;
                long length = b - a;
                bool letterDone = last ? (atTheEnd || length >= letterEnds) : sg.Kind >= 3;
                if (!atTheEnd && lastMarkEnd > t - (long)(HoldBackSeconds / Hop)) letterDone = false;
                bool wordDone = last ? (atTheEnd || length >= wordEnds) : sg.Kind == 4;

                if (letterDone && pattern.Length > 0)
                {
                    string letter;
                    // INSIDE A WORD A WEAKER LETTER IS BELIEVED. The research decoder judged whole
                    // words, so strong letters carried a faded one: the E of THE on session 3 stood
                    // out for 25 ms and scored 0.14, far under the gate on its own. Once this word has
                    // a letter on screen, the same letter needs only InWordGate.
                    double gate = _spaceOwed ? InWordGate : LetterGate;
                    bool shown = evidence / pattern.Length >= gate
                                 && CwDecoder.FromMorseTable.TryGetValue(pattern.ToString(), out letter);
                    var judged = Judged;
                    if (judged != null) judged(pattern.ToString(), lastMarkEnd, evidence / pattern.Length, shown);
                    if (shown && CwDecoder.FromMorseTable.TryGetValue(pattern.ToString(), out letter))
                    {
                        said.Append(letter);
                        _spaceOwed = true;
                        if (_lastShown < lastMarkEnd - (long)(OverGapSeconds / Hop)) _overStart = a0OfLetter;
                        _lastShown = lastMarkEnd;

                        // ONLY A SHOWN LETTER IS FINAL. A letter too weak to show was once marked
                        // done as well, and that lost real letters: after the H of THE the search
                        // briefly drew a false dit in the silence (evidence 0.14, not shown), a moment
                        // later moved it onto the real E - and the E, now starting inside the "done"
                        // stretch, was skipped as already judged. Left open, the search corrects it.
                        _shownTo = lastMarkEnd;
                    }
                    pattern.Clear(); evidence = 0;
                }
                if (wordDone && _spaceOwed && pattern.Length == 0)
                {
                    said.Append(' ');
                    _spaceOwed = false;
                }
            }

            SignalPresent = _lastShown > t - (long)(6 / Hop);
            if (said.Length == 0) return;
            var text = Text;
            if (text != null) text(said.ToString());
        }

        // ---- the note ----

        // Like the plain decoder, follow the strongest note NOW, and move only when another is clearly
        // stronger - one note for a whole recording listened to the wrong one of four stations.
        void FollowTheNote()
        {
            int span = (int)Math.Min(_soundCount, _sound.Length);
            var x = new double[span];
            for (int k = 0; k < span; k++) x[k] = _sound[(int)((_soundCount - span + k) % _sound.Length)];

            // NOT A FRESH START. Resetting the search on every change of note lost the first letters
            // after it - two seconds went by before the levels were known again - and in the silence
            // before a station begins the note wanders between noise peaks, restarting over and over.
            // The research decoder simply went on at the new note, and so does this.
            FollowTheNote(x);
            ToneHz = _note;
        }

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
