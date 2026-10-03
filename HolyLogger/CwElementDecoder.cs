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
        internal static double LetterGate = 0.6;   // a letter is shown only if its marks stood out this much
        internal static double InWordGate = 0.1;   // ...or this much, after a shown letter in the same word
        internal static double ShortLetterGate = 1.0; // ...and at least this much for a letter of one or two elements:
                                                      // on his air sessions 900 invented words fell to 793 for 659 read to 629
                                                      // (0.8: 831 / 640; 1.5: 777 / 598); W1AW read unchanged, invented 169 to 160
        internal static double Leak = 0.01;    // a strong station leaves 1% of itself in its own gaps - at least
        internal static bool AdaptiveLeak = true;
        double _leak = Leak;
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
        double _loRe, _loIm, _hiRe, _hiIm;     // the same 5 ms at NeighbourHz below and above the note
        readonly double[] _beside = new double[Ring];   // their power, per reading

        readonly Complex[] _z = new Complex[Ring];
        readonly double[] _tonePower = new double[Ring], _noise = new double[Ring];
        readonly double[] _noiseBase = new double[Ring];   // the noise before any of the station is allowed in it
        readonly double[] _F = new double[Ring];   // summed evidence up to (not including) each reading
        long _frames;                          // readings made
        long _evidenceTo = -1;                 // _F known up to here; -1 until the levels exist
        double _levelTone, _levelNoise, _levelNoiseBase;
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

        // Every shown letter with where its first mark began and its last mark ended, counted in samples
        // from the first sample this decoder was given - so the decode window can write it under its own
        // dits and dahs on the waterfall. Raised on this decoder's own thread.
        public event Action<string, long, long> LetterTimed;

        // For the research bench only: every letter judged - its dits and dahs, where its last mark
        // ends (reading), its evidence, and whether it was shown.
        internal Action<string, long, double, bool> Judged;
        internal bool LastJudgedInWord, LastJudgedPlainHeard;
        internal string TimingNow
        {
            get
            {
                return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "dit {0:F1} fr, dah {1:F2} egap {2:F2} lgap {3:F2} wgap {4:F2}, leak {5}, tone/noise {6:F1} dB, ev to {7} search {8}..{9} shown {10}",
                    _unit, _kinds[1], _kinds[2], _kinds[3], _kinds[4], _leak,
                    _levelNoiseBase > 0 ? 10 * Math.Log10(_levelTone / _levelNoiseBase) : 0, _evidenceTo, _searchFrom, _searchTo, _shownTo);
            }
        }

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
            _plain = new CwDecoder(sampleRate);
            _plainHop = new short[_hopSamples];
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
                _plainHop[_accN] = v;
                _sound[(int)(_soundCount % _sound.Length)] = v;
                _soundCount++;

                if (_note > 0)
                {
                    long n = _sample % (_rate * 1000L);
                    double ph = -2 * Math.PI * _note * n / _rate;
                    _accRe += v * Math.Cos(ph); _accIm += v * Math.Sin(ph);
                    double pl = -2 * Math.PI * (_note - NeighbourHz) * n / _rate, phh = -2 * Math.PI * (_note + NeighbourHz) * n / _rate;
                    _loRe += v * Math.Cos(pl); _loIm += v * Math.Sin(pl);
                    _hiRe += v * Math.Cos(phh); _hiIm += v * Math.Sin(phh);
                }
                _sample++;
                if (++_accN < _hopSamples) continue;

                // The plain decoder hears the same 5 ms first, so its answer - is a station there? -
                // is known for this reading when the reading is added.
                _plain.Process(_plainHop, _hopSamples);
                _plainHearsNow = GateMode == 2 ? _plain.SignalPresent && _plain.ProvedMorse : _plain.SignalPresent;
                FollowPlainSpeed();
                FollowPlainNote();
                if (_note > 0)
                {
                    AddReading(new Complex(_accRe, _accIm));
                    _beside[(int)((_frames - 1) & (Ring - 1))] = 0.5 * (_loRe * _loRe + _loIm * _loIm + _hiRe * _hiRe + _hiIm * _hiIm);
                }
                _accRe = _accIm = 0; _accN = 0;
                _loRe = _loIm = _hiRe = _hiIm = 0;

                // Once a second: is the station we follow still the strongest? Not while the plain
                // decoder hears a station - its note is followed then (FollowPlainNote), and this 10 s
                // look, still full of the station that just stopped, would only move it back.
                if (_sample % _rate < _hopSamples && !(NoteFromPlain && _plain.SignalPresent && _note > 0)) FollowTheNote();
            }
        }

        void StartAfresh()
        {
            _frames = 0; _evidenceTo = -1; _searchFrom = -1; _searchTo = -1;
            _plain.Reset();
            _plainQuietSince = LongAgo; _plainOverStart = LongAgo; _plainWasHearing = false;
            _shownTo = 0; _spaceOwed = false; _lastShown = long.MinValue;
            _recentTone.Clear();
            _unit = 12; _kinds = new double[] { 1, 3, 1, 3, 7 }; _piece = PieceFrames; SetLengths();
            _rawUnit = 0; _pendingUnit = 0;
            SignalPresent = false;
        }

        // ---- one reading at a time ----

        // For the research bench: the sample the first reading began at, so readings can be put
        // against the recording's own clock.
        internal long FirstReadingSample = -1;

        // For the research bench: hold every letter back this long before it is final - to measure
        // what printing at once costs. 0 in HolyLogger.
        internal static double HoldBackSeconds = 0;

        // For the research bench: 0 no gate, 1 the plain decoder hears a station, 2 it has also proved
        // it is Morse; and how strong a letter must be to be shown with no gate at all.
        internal static int GateMode = 1;
        internal static double StrongLetter = 1e9;

        // THE PLAIN DECODER DECIDES WHEN SOMEONE IS SENDING. This decoder reads weak signals better but
        // made letters out of noise between stations (E I EE E, on his recording of R6ODZ and LZ5BB);
        // the plain decoder is good at exactly the question this one is bad at - is anyone there? -
        // and runs beside it on the same sound.
        readonly CwDecoder _plain;
        readonly short[] _plainHop;
        bool _plainHearsNow;
        readonly bool[] _plainHeard = new bool[Ring];
        const int GateWiden = 200;             // readings before a letter the plain decoder may have heard

        // THE PLAIN DECODER'S SPEED AT THE START OF AN OVER. This decoder finds the speed from seconds
        // of sending, so the first letters of an over were read at the last station's speed - and an
        // over starts with the callsigns: R6ODZ DE LZ5BB came out as D6 O M7E T 7BE RZ5GB where the
        // plain decoder, which re-learns the dit with every element, read it right. So when the plain
        // decoder starts hearing a station after a pause, its speed is taken until this decoder has
        // measured its own (SpeedFromPlain 1), or always (2, for the bench).
        // 0 SINCE 2026-09-30: this decoder's own speed. The start-of-over borrowing only helped while an
        // overflow (LongAgo, above) made it permanent; measured honestly, its own speed read his air
        // sessions best (643 / 776 against 629 / 796) and W1AW no worse.
        internal static int SpeedFromPlain = 0;
        // FAR IN THE PAST, NOT long.MinValue. "Now minus MinValue" overflows to a large negative number,
        // which read as "an over began a moment ago" for ever: wherever the plain decoder heard a station
        // from the first second, this decoder copied its speed permanently and never ran its own speed,
        // timing and leak measurements (found on radio.wav, where DELTA came out as DELK with the
        // textbook timing still in place). And the first station ever heard counts as the start of an over.
        const long LongAgo = -1000000000000L;
        long _plainQuietSince = LongAgo;       // reading from which the plain decoder has heard nothing
        long _plainOverStart = LongAgo;        // reading where it began hearing after a pause
        bool _plainWasHearing;

        // SpeedFromPlain 3: THE PLAIN DECODER'S SPEED WHENEVER IT IS SURE OF THE STATION, this decoder's
        // own otherwise. Measured with an overflow fixed that had, by accident, given every recording
        // where the plain decoder heard a station from the first second the plain speed for good:
        // always the plain speed read W1AW best (1150 words / 158 invented against 1116 / 205 with this
        // decoder's own) and his IC-7610 recordings best (20 against 18), but his weak air sessions
        // worst (625 / 827 against 643 / 776) - where the plain decoder barely hears, its speed is a guess.
        //
        // SpeedFromPlain 3 - the plain speed only while the plain decoder has proved Morse - was worse
        // than either (W1AW 1102 / 218). What decides it is the signal's STRENGTH: the recordings the
        // plain speed wins are 14-32 dB over the noise, the ones it loses 0-7 dB. So 4: the plain speed
        // while this station stands more than StrongDb over the noise, this decoder's own below.
        internal static double StrongDb = 10;
        bool PlainIsConfident()
        {
            if (SpeedFromPlain == 4)
                return _plain.SignalPresent && _levelNoiseBase > 0 && 10 * Math.Log10(_levelTone / _levelNoiseBase) > StrongDb;
            return _plain.SignalPresent && _plain.ProvedMorse;
        }

        void FollowPlainSpeed()
        {
            if (SpeedFromPlain == 0) return;
            bool hears = _plain.SignalPresent;
            if (hears && !_plainWasHearing && _frames - _plainQuietSince > (long)(OverGapSeconds / Hop))
                _plainOverStart = _frames;
            if (!hears && _plainWasHearing) _plainQuietSince = _frames;
            _plainWasHearing = hears;

            bool early = _frames - _plainOverStart < 4 * SpeedEvery / 2;
            bool confident = PlainIsConfident();
            if (!hears || (SpeedFromPlain == 1 && !early) || (SpeedFromPlain >= 3 && !confident && !early)
                || _frames % 20 != 0) return;
            double wpm = ForceWpm > 0 ? ForceWpm : _plain.Wpm;
            if (wpm < 5 || wpm > 60) return;
            double unit = 1200.0 / wpm / (Hop * 1000.0);
            if (Math.Abs(unit - _unit) < 0.05 * _unit) return;
            _unit = unit;
            _piece = unit < 9 ? Math.Max(2, Math.Min(4, (int)Math.Round(unit / 3))) : PieceFrames;
            SetLengths();
            Wpm = wpm;
        }

        // THE PLAIN DECODER'S NOTE. In a QSO the two stations are rarely on the same note, and this
        // decoder took its note from the last 10 s of sound - mostly the station that had just
        // stopped - so the one answering was not listened to until well into his over. The plain
        // decoder moves to a new note within half a second. When it hears a station on a note clearly
        // apart from this one's, for a few checks in a row, this decoder moves there and finds the
        // note exactly from the last two seconds.
        internal static bool NoteFromPlain = true;
        int _plainNoteAgrees;

        void FollowPlainNote()
        {
            if (!NoteFromPlain || _sample % (20 * _hopSamples) != 0) return;
            double heard = _plain.ToneHz;
            if (!_plain.SignalPresent || heard <= 0 || _note <= 0 || Math.Abs(heard - _note) < 15) { _plainNoteAgrees = 0; return; }
            if (++_plainNoteAgrees < 3) return;
            _plainNoteAgrees = 0;

            int span = (int)Math.Min(_soundCount, 2 * _rate);
            var x = new double[span];
            for (int k = 0; k < span; k++) x[k] = _sound[(int)((_soundCount - span + k) % _sound.Length)];
            _note = FindNote(x, Math.Round(heard));
            ToneHz = _note;
            if (FreshLevelsOnNewNote) { _levelsFrom = _frames; _recentTone.Clear(); }
        }

        // A NEW NOTE IS A NEW STATION: ITS LEVELS ARE ITS OWN. The levels came from the last 10 s, and
        // the loud station was held for 20 s, so after a loud station stopped and a weaker one answered
        // on another note, the weaker one was measured against the loud one - its marks looked like
        // gaps. From a change of note only the readings since are used (at least half a second's), and
        // they are re-measured every quarter second until there are two seconds of them.
        internal static bool FreshLevelsOnNewNote = true;
        long _levelsFrom = -1;

        bool PlainHeard(long from, long to)
        {
            if (GateMode == 0) return true;
            from = Math.Max(from, Math.Max(0, _frames - Ring + 1));
            for (long k = from; k < Math.Min(to, _frames); k++)
                if (_plainHeard[(int)(k & (Ring - 1))]) return true;
            return false;
        }

        void AddReading(Complex z)
        {
            if (_frames == 0) FirstReadingSample = _sample - _hopSamples;
            long f = _frames++;
            _z[(int)(f & (Ring - 1))] = z;
            _plainHeard[(int)(f & (Ring - 1))] = _plainHearsNow;

            bool freshNote = _levelsFrom >= 0 && _frames - _levelsFrom < 2 * LevelsEvery * 2;
            if (freshNote ? (_frames - _levelsFrom >= 100 && _frames % 50 == 0)
                          : (_frames % LevelsEvery == 0 && _frames >= 2 * LevelsEvery)) UpdateLevels();
            if (_evidenceTo < 0) return;

            // The piece for reading i reaches c - 1 - c/2 readings ahead of it, so reading i is judged
            // once that one has arrived. A loop, because a shorter piece after a speed change can make
            // two readings ready at once.
            while (_evidenceTo <= f - (_piece - 1 - _piece / 2))
            {
                long i = _evidenceTo;
                _tonePower[(int)(i & (Ring - 1))] = _levelTone;
                _noise[(int)(i & (Ring - 1))] = _levelNoise;
                _noiseBase[(int)(i & (Ring - 1))] = _levelNoiseBase;
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
            bool newOver = _evidenceTo - _overStart < 4 * SpeedEvery / 2
                           || (SpeedFromEdges && _levelNoiseBase > 0 && 10 * Math.Log10(_levelTone / _levelNoiseBase) > StrongDb);
            bool plainRules = SpeedFromPlain == 2 || (SpeedFromPlain != 0 && _frames - _plainOverStart < 4 * SpeedEvery / 2);
            if (!plainRules && _frames % (newOver ? SpeedEvery / 4 : SpeedEvery) == 0) UpdateSpeed();
        }

        // Noise: the quiet 30% of the last 10 s of readings (noise power is exponential, so its 30th
        // percentile is 0.357 of its mean). Tone: the loud end of 40 ms pieces, less the noise that
        // comes with them; held at half the strongest of the last 20 s through pauses; and 1% of it
        // allowed in the gaps.
        void UpdateLevels()
        {
            int n = (int)Math.Min(LevelsOver, _frames);
            if (_levelsFrom >= 0) n = (int)Math.Min(n, Math.Max(100, _frames - _levelsFrom));
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
            _levelNoiseBase = noise;
            noise = Math.Max(noise, _leak * tone);
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
            bool strongNow = _levelNoiseBase > 0 && 10 * Math.Log10(_levelTone / _levelNoiseBase) > StrongDb;
            if (SpeedFromEdges && strongNow)
            {
                double byEdges = UnitFromRisingEdges(z, amp, n);
                if (SpeedDebug != null) SpeedDebug(string.Format("    edges {0:F2} (search {1:F2})", byEdges, unit));
                // EdgesOnlyWhenSlower: the edges overrule the search only where the search has run away
                // to a slower speed - the failure they were made for - and not otherwise.
                if (byEdges > 0 && (!EdgesOnlyWhenSlower || unit > EdgesSlowerRatio * byEdges)) unit = byEdges;
            }

            // A BIG CHANGE OF SPEED MUST BE FOUND TWICE. On YT7FT's CQ (his recording missedcq_173716)
            // the speed flipped between 19.9 WPM, right, and 48 - the fastest allowed - from one search to
            // the next: short clicks in the twenty seconds searched passed for dits. The CQ began while
            // it said 48, and its dahs were cut into pieces. A speed more than a third away from the one
            // in use is taken only when the next search finds it again.
            if (ConfirmBigSpeedChange && !(SpeedFromEdges && strongNow) && _rawUnit > 0 && Math.Abs(Math.Log(unit / _rawUnit)) > Math.Log(1.35))
            {
                if (_pendingUnit > 0 && Math.Abs(Math.Log(unit / _pendingUnit)) < Math.Log(1.15)) _pendingUnit = 0;
                else { _pendingUnit = unit; unit = _rawUnit; }
            }
            else _pendingUnit = 0;
            _rawUnit = unit;
            double[] book = { 1, 3, 1, 3, 7 };
            double[] F = Evidence(z, amp, noise, PieceFrames, 0, n);
            double dit = 1;
            List<Segment> read = Decode(F, n, unit, book, LengthSpread);
            _kinds = LearnOperatorTiming ? LearnTiming(read, unit, book, out dit) : (double[])book.Clone();
            if (!LearnOperatorTiming) dit = 1;
            // While the plain decoder is sure of the station its speed rules (see PlainIsConfident);
            // this reading still teaches the operator's timing and the leak.
            double unitBefore = _unit;
            if (!(SpeedFromPlain >= 3 && PlainIsConfident())) _unit = unit * dit;
            if (AdaptiveLeak)
            {
                var noiseBase = new double[n];
                for (int k = 0; k < n; k++) noiseBase[k] = _noiseBase[(int)((to - n + k) & (Ring - 1))];
                _leak = ChooseLeak(z, amp, noiseBase, n);
                if (strongNow && StrongLeak > 0 && !StrongLeakIsAChoice) _leak = StrongLeak;
            }

            // Pieces of 4, shorter only below a 45 ms dit (measured: shorter pieces help fast sending
            // and cost everything slower).
            _piece = unit < 9 ? Math.Max(2, Math.Min(4, (int)Math.Round(unit / 3))) : PieceFrames;
            SetLengths();
            Wpm = 1200.0 / (_unit * Hop * 1000.0);

            if (ReReadOnSpeedChange && unitBefore > 0 && Math.Abs(Math.Log(_unit / unitBefore)) > Math.Log(1.15))
                ReReadSinceLastLetter();
        }

        // A NEW SPEED RE-READS WHAT IS NOT YET SHOWN. The search goes forward one reading at a time, and
        // what it worked out while the speed was wrong stayed worked out: on YT7FT's CQ the stretch read
        // at 48 WPM lost the C's first dit (it came out M N), and the C's and Q's after it ran together
        // although the speed was right again by then. So when the speed moves by more than 15%, the
        // search is run again, at the new speed, over everything since the last letter shown.
        internal static bool ReReadOnSpeedChange = true;

        void ReReadSinceLastLetter()
        {
            long to = _evidenceTo;
            long from = Math.Max(_shownTo, to - (Ring - 2048));
            if (from >= to) return;
            long overStart = _overStart;
            StartSearchAt(from);
            _overStart = overStart;
            for (long t = from + 1; t <= to; t++) SearchStep(t);
        }

        // HOW MUCH OF THE STATION IS LEFT IN ITS OWN GAPS - chosen by reading, not assumed. A fixed 1%
        // suited W3PIE's receiver; on his IC-7610 the tone trails off for 50 ms after every mark (its
        // AGC and filter) and fills the short gaps, so letters ran together (R6ODZ DE LZ5BB came out as
        // .---.--.-----. and was dropped). 10% fixed that and cost W1AW 80 words. Measuring the tone
        // left in the gaps of a reading did not work: that reading had already folded the tails into
        // the marks, so its gaps looked clean. What letters that ran together DO leave is runs of dits
        // and dahs that spell nothing. So the last stretch is read at each share, and the one whose
        // letters are most often real Morse letters wins - Morse's own alphabet, no words.
        static readonly double[] LeakChoices = { 0.01, 0.03, 0.1 };
        // 3: W1AW 1143 words read (1152 with a fixed 1%, 1116 with no margin), and his own IC-7610's
        // R6ODZ DE LZ5BB still read; at 8 W1AW was whole again and R6ODZ was lost.
        internal static int LeakMargin = 3;

        double ChooseLeak(Complex[] z, double[] amp, double[] noiseBase, int n)
        {
            double best = _leak; int bestScore = int.MinValue;
            var noise = new double[n];
            double[] choices = StrongLeakIsAChoice && StrongLeak > 0 ? new[] { 0.01, 0.03, 0.1, StrongLeak } : LeakChoices;
            foreach (double leak in choices)
            {
                for (int k = 0; k < n; k++) noise[k] = Math.Max(noiseBase[k], leak * amp[k] * amp[k]);
                double[] F = Evidence(z, amp, noise, _piece, 0, n);
                int score = 0;
                var pattern = new StringBuilder();
                foreach (Segment sg in Decode(F, n, _unit, _kinds, LengthSpread))
                {
                    if (sg.Kind <= 1) { pattern.Append(sg.Kind == 0 ? '.' : '-'); continue; }
                    if (sg.Kind < 3 || pattern.Length == 0) continue;
                    score += CwDecoder.FromMorseTable.ContainsKey(pattern.ToString()) ? 1 : -2;
                    pattern.Clear();
                }
                // A HIGHER SHARE HAS TO WIN CLEARLY. Chosen on a bare majority it cost W1AW 36 words
                // (1152 read with a fixed 1%, 1116 chosen) - a letter or two more on a strong station's
                // twenty seconds is noise, not letters running together.
                if (bestScore == int.MinValue || score > bestScore + LeakMargin) { bestScore = score; best = leak; }
            }
            return best;
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
            if (PathDebug != null && t % 200 == 0)
            {
                var sb = new StringBuilder();
                foreach (Segment sg in segs) sb.Append("._-:/"[sg.Kind]).Append(sg.End - sg.Start).Append(' ');
                PathDebug(string.Format("  path at {0} (shown {1}, from {2}, best kind {3}): {4}", t, _shownTo, _searchFrom, kk, sb));
            }

            double letterEnds = Math.Sqrt(_kinds[2] * _kinds[3]) * _unit * 1.25;
            double wordEnds = Math.Sqrt(_kinds[3] * _kinds[4]) * _unit;
            var said = new StringBuilder();
            var pattern = new StringBuilder();
            double evidence = 0; long lastMarkEnd = -1, a0OfLetter = -1;
            var marks = new List<long[]>(); var gaps = new List<long[]>();

            for (int n = 0; n < segs.Count; n++)
            {
                Segment sg = segs[n];
                long a = sg.Start + _searchFrom, b = sg.End + _searchFrom;
                if (b <= _shownTo) continue;
                if (sg.Kind <= 1)
                {
                    if (a < _shownTo) continue;            // part of a letter already judged
                    if (pattern.Length == 0) { a0OfLetter = a; marks.Clear(); gaps.Clear(); }
                    marks.Add(new[] { a, b });
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
                if (pattern.Length > 0) gaps.Add(new[] { a, Math.Min(b, a + (long)(2 * _unit)) });

                if (letterDone && pattern.Length > 0)
                {
                    string letter;
                    // INSIDE A WORD A WEAKER LETTER IS BELIEVED. The research decoder judged whole
                    // words, so strong letters carried a faded one: the E of THE on session 3 stood
                    // out for 25 ms and scored 0.14, far under the gate on its own. Once this word has
                    // a letter on screen, the same letter needs only InWordGate.
                    double gate = _spaceOwed ? InWordGate : LetterGate;
                    // A LONE E OR T, OR A LETTER OF TWO ELEMENTS, NEEDS MORE. Measured over his four air
                    // sessions, letter by letter against the sent text: 38% of the junk letters were a
                    // single dit or dah, against 11% of the real ones, and the real letters' evidence
                    // was three times the junk's (medians 2.8 and 0.8).
                    if (pattern.Length <= 2 && (!_spaceOwed || !ShortGateFirstOnly)) gate = Math.Max(gate, ShortLetterGate);
                    double steady, beside, filled;
                    MeasureLetter(marks, gaps, out steady, out beside, out filled);
                    LastSteadiness = steady; LastBeside = beside; LastGapFill = filled;
                    // ON A STRONG STATION ONLY THE CARRIER TEST. The three checks together cut the junk on
                    // his weak air sessions (776 invented words to 695) and wrecked the strong ones: W1AW
                    // fell from 1152 words read to 971, and the whole of IK1QHB's strong over vanished -
                    // a strong station's own tail sits in its gaps (his IC-7610 especially) and reads as
                    // "not quiet". Above StrongDb only a gap nearly as loud as the tone - a carrier - stops it.
                    // By the station's strength. By each letter's own loudness was tried (the station's is
                    // held for 20 s) and measured worse: air sessions 617 / 715 against 615 / 695 - the
                    // letters that passed after DD4U on his clicks recording were a real weak station at
                    // 428 Hz, not noise.
                    bool strong = _levelNoiseBase > 0 && 10 * Math.Log10(_levelTone / _levelNoiseBase) > StrongDb;
                    bool waterfallOk = strong
                        ? filled <= CarrierGapFill
                        : steady >= MinSteadiness && beside >= MinBeside && filled <= MaxGapFill;
                    bool passes = waterfallOk
                                  && evidence / pattern.Length >= gate
                                  && (evidence / pattern.Length >= StrongLetter || PlainHeard(a0OfLetter - GateWiden, t + 1));

                    // LETTERS THAT RAN TOGETHER ARE PARTED AT THEIR LONGEST GAPS. On his recording of
                    // YT7FT's CQ the C and the Q came out as one run, -.-..--.- , which spells nothing and
                    // was thrown away whole - while the plain decoder read CQ CQ CQ. Where two letters run
                    // together the gap between them is still the longest gap in the run, so a run that
                    // spells nothing is cut there, and again, until every piece is a letter. Morse's own
                    // timing only: nothing is guessed about which letters are likely.
                    List<int[]> pieces = passes ? PartIntoLetters(pattern.ToString(), marks) : null;
                    bool shown = pieces != null;
                    LastJudgedInWord = _spaceOwed;
                    LastJudgedPlainHeard = PlainHeard(a0OfLetter - GateWiden, t + 1);
                    var judged = Judged;
                    if (judged != null) judged(pattern.ToString(), lastMarkEnd, evidence / pattern.Length, shown);
                    if (shown)
                    {
                        var timed = LetterTimed;
                        foreach (int[] piece in pieces)
                        {
                            CwDecoder.FromMorseTable.TryGetValue(pattern.ToString(piece[0], piece[1] - piece[0]), out letter);
                            said.Append(letter);
                            if (timed != null)
                                timed(letter, FirstReadingSample + marks[piece[0]][0] * _hopSamples,
                                      FirstReadingSample + marks[piece[1] - 1][1] * _hopSamples);
                        }
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

        // WHAT THE EYE SEES ON A WATERFALL, asked of every letter before it is shown (his idea):
        //   STEADINESS - a real dit or dah is one steady tone, its phase moving smoothly from start to
        //     end, so its readings add up WITH their phase almost as well as their powers add: close to
        //     1. A burst of noise is as loud but its phase jumps, and adds up to little: near 1/length.
        //     The letter's weakest element counts.
        //   BESIDE - a real element is narrow: loud at its note and quiet NeighbourHz either side. A
        //     static crash is loud everywhere at once. Power at the note over power beside it.
        //   GAP FILL - the gaps between its elements, and after it, must be quiet. A carrier chopped
        //     into dahs by the search (TTTTTTTTT) has as much tone in its "gaps" as in its marks.
        internal static double NeighbourHz = 150;
        // Chosen from his four air sessions, every shown letter marked real or junk against the sent text
        // (tools/LetterJunk2.py): together these keep 95% of the real letters and 71% of the junk. In
        // whole words, weak stations only: invented 776 -> 695 for read 643 -> 615; W1AW 1152 / 158 ->
        // 1152 / 149. The carrier test (0.7) never fired on the benches - it is for the TTTTTTTTT he saw live.
        internal static double MinSteadiness = 0.4, MinBeside = 2.5, MaxGapFill = 0.4, CarrierGapFill = 0.7;
        internal static bool SteadyAllowsOffset = true;
        internal double LastSteadiness, LastBeside, LastGapFill;

        // The run of marks [0, n) as letters: itself if it spells one, else cut at its longest inner gap
        // and each side parted the same way. Null if some piece can never be a letter. Each piece is
        // {first mark, one past its last mark}.
        static List<int[]> PartIntoLetters(string pattern, List<long[]> marks)
        {
            var pieces = new List<int[]>();
            return Part(pattern, marks, 0, pattern.Length, pieces) ? pieces : null;
        }

        static bool Part(string pattern, List<long[]> marks, int from, int to, List<int[]> pieces)
        {
            if (CwDecoder.FromMorseTable.ContainsKey(pattern.Substring(from, to - from)))
            {
                pieces.Add(new[] { from, to });
                return true;
            }
            if (to - from < 2 || !PartRunsTogether) return false;
            int cut = -1; long widest = -1;
            for (int i = from; i < to - 1; i++)
            {
                long gap = marks[i + 1][0] - marks[i][1];
                if (gap > widest) { widest = gap; cut = i + 1; }
            }
            return Part(pattern, marks, from, cut, pieces) && Part(pattern, marks, cut, to, pieces);
        }

        // OFF - MEASURED AND LOST. On YT7FT's CQ the gaps inside the joined run were all much the same
        // length (his radio's tail shortens them), so the cuts fell in the wrong places (CQ became CGME),
        // and on the benches it added junk: air sessions 625 / 687 fell to 621 / 715, W1AW 1143 / 159 to
        // 1141 / 167. The cause to attack is the start-of-over speed (48 WPM against a real 19) and the
        // letter gaps the tail eats, not the run afterwards.
        internal static bool PartRunsTogether = false;

        void MeasureLetter(List<long[]> marks, List<long[]> gaps, out double steady, out double beside, out double filled)
        {
            // MEASURED OVER WHOLE ELEMENTS, phase kept, the ratio separated real letters from junk no
            // better (at 40: 96% of real kept, 84% of junk) and cost 54 real words on his air sessions.
            // Kept per 5 ms reading, as it was chosen.
            //
            // OFF-NOTE TONES ARE STEADY TOO. A station 10 Hz from the note we listen on turns its phase a
            // little every reading, and over a 165 ms dah that summed to nothing: steadiness 0.03, every
            // letter of the answering station dropped while its marks stood 18 dB clear. So the letter's
            // own turn per reading is measured first (the phase step from each reading to the next, over
            // all its marks) and taken out before the readings are added.
            double stepRe = 0, stepIm = 0;
            if (SteadyAllowsOffset)
                foreach (long[] m in marks)
                    for (long k = m[0] + 1; k < m[1]; k++)
                    {
                        Complex a = _z[(int)(k & (Ring - 1))], b = _z[(int)((k - 1) & (Ring - 1))];
                        stepRe += a.Re * b.Re + a.Im * b.Im; stepIm += a.Im * b.Re - a.Re * b.Im;
                    }
            double turn = SteadyAllowsOffset ? Math.Atan2(stepIm, stepRe) : 0;
            steady = 1; double markPower = 0, besidePower = 0; long markFrames = 0;
            foreach (long[] m in marks)
            {
                double re = 0, im = 0, power = 0;
                for (long k = m[0]; k < m[1]; k++)
                {
                    Complex z = _z[(int)(k & (Ring - 1))];
                    double c = Math.Cos(-turn * (k - m[0])), sn = Math.Sin(-turn * (k - m[0]));
                    re += z.Re * c - z.Im * sn; im += z.Re * sn + z.Im * c; power += z.Norm;
                    besidePower += _beside[(int)(k & (Ring - 1))];
                }
                long len = m[1] - m[0];
                if (len > 0 && power > 0) steady = Math.Min(steady, (re * re + im * im) / (len * power));
                markPower += power; markFrames += len;
            }
            beside = besidePower > 0 ? markPower / besidePower : 1e9;
            double gapPower = 0; long gapFrames = 0;
            foreach (long[] g in gaps)
                for (long k = g[0]; k < g[1]; k++) { gapPower += _z[(int)(k & (Ring - 1))].Norm; gapFrames++; }
            filled = markFrames > 0 && markPower > 0 && gapFrames > 0 ? (gapPower / gapFrames) / (markPower / markFrames) : 0;
            LastLoudness = markFrames > 0 ? markPower / markFrames : 0;
        }

        internal double LastLoudness;          // the letter's own power per reading, for "is it strong?"

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
                // ONLY MARKS WITH A NEIGHBOUR IN THE SAME LETTER. Short clicks, each standing alone
                // between silences, passed for dits: on YT7FT's CQ the speed flipped between 19 WPM and
                // the fastest allowed, 48, from one search to the next. A real dit or dah nearly always
                // has another mark beside it across a gap inside a letter; a click does not.
                var all = new List<double>();
                for (int i = 0; i < segs.Count; i++)
                {
                    Segment s = segs[i];
                    if (s.Kind > 1) continue;
                    all.Add(Math.Log(s.End - s.Start));
                    bool neighbour = (i > 0 && segs[i - 1].Kind == 2) || (i + 1 < segs.Count && segs[i + 1].Kind == 2);
                    if (neighbour || !SpeedFromLettersOnly) L.Add(Math.Log(s.End - s.Start));
                }
                if (L.Count < 6) L = all;
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
                if (SpeedDebug != null) SpeedDebug(string.Format("    it{0}: marks {1} (all {2}) lo {3:F1} hi {4:F1} fr; segs {5}", it, arr.Length, all.Count, Math.Exp(lo), Math.Exp(hi), DebugSegs(segs)));
                double byPeriod = SpeedByPeriod ? UnitFromPeriods(segs) : 0;
                if (SpeedDebug != null) SpeedDebug(string.Format("        by period {0:F1}", byPeriod));
                g = Math.Min(Math.Max(byPeriod > 0 ? byPeriod : Math.Exp(lo), 5.0), 35.0);
            }
            return g;
        }

        internal static Action<string> SpeedDebug;
        internal static Action<string> PathDebug;
        internal static bool SpeedByPeriod = false;
        internal static bool WideTiming = false;
        internal static bool SpeedFromEdges = false;
        internal static double StrongLeak = 0;
        internal static bool StrongLeakIsAChoice = true;      // offered to ChooseLeak, not forced
        internal static bool EdgesOnlyWhenSlower = true;
        internal static double EdgesSlowerRatio = 1.35;

        // THE SPEED OF A STRONG STATION FROM WHERE ITS MARKS BEGIN. A strong station rings on after every
        // mark (the receiver's filter and AGC - 50 ms on his IC-7610), so its marks read long and its
        // gaps short, and a speed taken from mark lengths came out far too slow (35 WPM read as 7-20).
        // A mark BEGINS sharply, though, and from one beginning to the next is always an even number of
        // dits: 2 after a dit inside a letter, 4 after a dah or a dit at a letter's end, 6, 8, 10.
        // So the dit is half the shortest common start-to-start time, refined against all of them.
        // Strong stations only: on a weak one, noise makes false beginnings.
        double UnitFromRisingEdges(Complex[] z, double[] amp, int n)
        {
            var starts = new List<int>();
            bool on = false;
            for (int k = 0; k < n; k++)
            {
                double tone = amp[k] * amp[k];
                if (tone <= 0) continue;
                double p = z[k].Norm / tone;
                if (!on && p > 0.4) { on = true; starts.Add(k); }
                else if (on && p < 0.12) on = false;
            }
            double all = UnitFromStarts(starts, 0);
            if (all <= 0 || !EdgesSinceLastPause) return all;
            // ONLY SINCE THE LAST PAUSE, when that is enough: in a QSO the twenty seconds searched hold
            // both stations, and the answer was a mixture of the two speeds - wrong for both.
            int from = 0;
            for (int i = starts.Count - 1; i > 0; i--)
                if (starts[i] - starts[i - 1] > 14 * all) { from = i; break; }
            if (from == 0) return all;
            double recent = UnitFromStarts(starts, from);
            return recent > 0 ? recent : all;
        }

        internal static bool EdgesSinceLastPause = true;

        static double UnitFromStarts(List<int> starts, int from)
        {
            var iv = new List<double>();
            for (int i = from + 1; i < starts.Count; i++)
            {
                int d = starts[i] - starts[i - 1];
                if (d >= 8 && d <= 140) iv.Add(d);         // 2 dits at 60 WPM up to 10 dits at 9 WPM... roughly
            }
            if (iv.Count < 8) return 0;
            double[] arr = iv.ToArray();
            Array.Sort(arr);
            // the shortest cluster that holds at least 15% of the intervals: two dits
            double two = 0;
            for (int i = 0; i < arr.Length; i++)
            {
                int c = 0;
                foreach (double v in arr) if (v >= arr[i] * 0.85 && v <= arr[i] * 1.25) c++;
                if (c >= Math.Max(4, arr.Length * 0.15)) { two = arr[i]; break; }
            }
            if (two <= 0) return 0;
            double u = 0;
            for (int pass = 0; pass < 3; pass++)
            {
                double sum = 0; int cnt = 0;
                foreach (double v in arr)
                {
                    double m = Math.Round(v / (pass == 0 ? two : 2 * u));
                    if (m < 1 || m > 5) continue;
                    double unitHere = v / (2 * m);
                    double baseU = pass == 0 ? two / 2 : u;
                    if (Math.Abs(unitHere / baseU - 1) > 0.2) continue;
                    sum += unitHere; cnt++;
                }
                if (cnt < 6) return 0;
                u = sum / cnt;
            }
            return Math.Min(Math.Max(u, 4.0), 35.0);
        }
        internal static double ForceWpm;      // bench only: the true speed, with SpeedFromPlain 2

        // THE SPEED FROM A MARK AND THE GAP AFTER IT TOGETHER. On a strong station every mark is read a
        // few readings long (the 20 ms pieces and the receiver's tail) and every gap as much short, so
        // the shortest marks said "slower" than the truth; read at that slower speed letters ran
        // together, which said slower still - 35 WPM fell to 7 in three steps. A mark plus the gap
        // inside the letter after it does not care where the edge between them was read: two dits for
        // a dit, four for a dah.
        static double UnitFromPeriods(List<Segment> segs)
        {
            var p = new List<double>();
            for (int i = 0; i + 1 < segs.Count; i++)
                if (segs[i].Kind <= 1 && segs[i + 1].Kind == 2)
                    p.Add(Math.Log(segs[i + 1].End - segs[i].Start));
            if (p.Count < 6) return 0;
            double[] arr = p.ToArray();
            double lo = Percentile(arr, 20), hi = Percentile(arr, 80);
            int na = 0, nb = 0;
            for (int i = 0; i < 15; i++)
            {
                double mid = (lo + hi) / 2, sa = 0, sb = 0; na = 0; nb = 0;
                foreach (double v in arr) { if (v < mid) { sa += v; na++; } else { sb += v; nb++; } }
                if (na == 0 || nb == 0) break;
                lo = sa / na; hi = sb / nb;
            }
            // Two clusters a factor of about two apart: dit periods and dah periods. One cluster only
            // (all dits or all dahs) is ambiguous - left to the marks.
            double ratio = Math.Exp(hi - lo);
            if (na == 0 || nb == 0 || ratio < 1.5 || ratio > 2.7) return 0;
            return (na * Math.Exp(lo) / 2 + nb * Math.Exp(hi) / 4) / (na + nb);
        }
        static string DebugSegs(List<Segment> segs)
        {
            var sb = new StringBuilder();
            foreach (Segment s in segs) { if (sb.Length > 400) break; sb.Append("._-:/"[s.Kind]).Append(s.End - s.Start).Append(' '); }
            return sb.ToString();
        }

        internal static bool WordGapWithoutPauses = true;
        internal static bool ConfirmBigSpeedChange = true;
        internal static bool SpeedFromLettersOnly = true;
        double _rawUnit, _pendingUnit;        // the speed search's last accepted answer, and one waiting to be confirmed
        // The short-letter gate for the FIRST letter of a word only: inside a word it dropped real letters
        // (BONNE came out BONN on radio.wav). Air sessions 615 / 695 -> 625 / 687, nothing lost elsewhere.
        internal static bool ShortGateFirstOnly = true;
        internal static bool LearnOperatorTiming = true;

        static double[] LearnTiming(List<Segment> segs, double unit, double[] book, out double dit)
        {
            var lens = new List<double>[5];
            for (int k = 0; k < 5; k++) lens[k] = new List<double>();
            foreach (Segment s in segs)
            {
                double units = (s.End - s.Start) / unit;
                // A PAUSE IS NOT A WORD GAP. Every silence counts as a word gap to the search, and the
                // pauses between overs pulled the learned word gap up to its ceiling - so a real word
                // gap looked like a letter gap and words ran together (YT3TYT3TYT3T, where the plain
                // decoder at least had YT3T YT3T). Only gaps a word gap could plausibly be are learned from.
                if (s.Kind == 4 && WordGapWithoutPauses && units > 14) continue;
                lens[s.Kind].Add(units);
            }
            dit = lens[0].Count > 10 ? Median(lens[0]) : 1.0;
            double[] lo = WideTiming ? new double[] { 1, 1.6, 0.15, 1.0, 3.0 } : new double[] { 1, 2.4, 0.6, 2.2, 5.0 };
            double[] hi = { 1, 4.5, 1.6, 5.0, 12.0 };
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
