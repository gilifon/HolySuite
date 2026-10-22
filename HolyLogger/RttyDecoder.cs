using System;
using System.Collections.Generic;

namespace HolyLogger
{
    /// <summary>
    /// Turns the radio's audio into RTTY text: two-tone FSK, 5-bit Baudot, one start bit, stop bits
    /// of any length. Fed blocks of 16-bit samples at whatever rate the sound card opened; hands
    /// back each character as it is read.
    ///
    /// HOW IT READS, AND WHY THIS WAY. Every choice below was measured on the RTTY bench
    /// (Research/Rtty: 112 recordings with noise, fading, an off-tune station, a louder neighbour and
    /// long noise gaps), against MMTTY's own decoders translated line for line. On 2026-10-22 this
    /// decoder copied 1108 of 1852 callsigns there, MMTTY's best setting (FFT) 983.
    ///
    /// 1. Each tone is mixed down to zero and summed over a window as long as a bit (a matched
    ///    filter). The window is made exactly 4/shift seconds long, so the mark tone falls in a null
    ///    of the space filter and back - the two never leak into each other. There is NO limiter in
    ///    front: MMTTY's limiter lets a louder station 300 Hz away swallow the one being read (its
    ///    IIR, FIR and PLL decoders copied nothing at all on that bench).
    ///
    /// 2. Two ways of deciding mark or space are worked out side by side, and at each moment the
    ///    one that is surer wins:
    ///    - the plain one, (mark - space) / (mark + space): best in steady noise and when both tones
    ///      fade together quickly (flutter);
    ///    - the "optimal ATC" (after Kok Chen, W7AY): each tone keeps its own level and noise floor,
    ///      and the decision point sits half way up each tone's own range, so when fading takes one
    ///      tone away the other still carries the bits. Best in slow, deep fading.
    ///    Either alone lost on the bench: plain 805, ATC 771, the surer of the two 883.
    ///
    /// 3. Characters are framed from the soft decisions, not from hard bits. Every mark-to-space
    ///    edge proposes a start bit, and so does the moment the next character is due when the last
    ///    one was sent straight after another. Each proposal is slid a third of a bit either way to
    ///    where all seven bits are most clearly decided, and kept only when its start bit is space
    ///    and its stop bit is mark.
    ///
    /// 4. The squelch opens on two: noise now and then makes one frame that looks like a clear
    ///    character, hardly ever two in a row. A clear character is held until a second follows;
    ///    then both print and the squelch stays open while clear characters keep coming. Without it
    ///    noise printed about three letters a second; with it the noise gaps come out clean.
    ///
    /// 5. Fine tuning follows the station: the phase of each tone's filter turns at exactly the
    ///    rate the station is off, and the mixers are pulled after it, up to 50 Hz either way.
    /// </summary>
    public sealed class RttyDecoder
    {
        // ---- settings -------------------------------------------------------------------------

        public double MarkHz { get { return _mark; } }
        public double ShiftHz { get { return _shift; } }
        public double Baud { get { return _baud; } }

        /// <summary>Hz the fine tuning has moved away from the mark it was given.</summary>
        public double TuningOffsetHz { get { return _afcHz; } }

        /// <summary>Mark is the higher tone instead of the lower (USB with a radio sending normal RTTY).</summary>
        public bool Reverse { get; set; }

        /// <summary>Shift back to letters after a space while in figures, as nearly every station expects.</summary>
        public bool UnshiftOnSpace { get; set; }

        /// <summary>Follow the station's frequency.</summary>
        public bool FineTuning { get; set; }

        /// <summary>Furthest the fine tuning may move, Hz either way.</summary>
        public double FineTuningRange { get; set; }

        /// <summary>A character, as it is read.</summary>
        public event Action<char> CharacterDecoded;

        // BENCH SETTINGS. The defaults are the measured best; the bench sweeps them to keep checking.

        /// <summary>0 ATC only, 1 plain only, 2 the surer of the two (the default).</summary>
        public int Decision { get; set; }

        /// <summary>Every bit of a character must be at least this clear, 0 to 1.</summary>
        public double Squelch { get; set; }

        /// <summary>The same, for a character found only because it was due, with no edge seen.</summary>
        public double DueSquelch { get; set; }

        /// <summary>A character's bits must be at least this clear on average to print at all.</summary>
        public double MeanGate { get; set; }

        /// <summary>...this clear on average to open the squelch (two in a row)...</summary>
        public double OpenGate { get; set; }

        /// <summary>...and this clear to keep it open.</summary>
        public double StayGate { get; set; }

        /// <summary>The squelch shuts this many bits after the last clear character.</summary>
        public double CloseAfterBits { get; set; }

        /// <summary>Weight of the ATC's balancing term; 0.5 is the textbook optimum.</summary>
        public double AtcBalance { get; set; }

        /// <summary>Part of each bit, around its middle, that is averaged to decide it.</summary>
        public double SampleWidth { get; set; }

        /// <summary>Look for a character where it is due, not only where an edge was seen.</summary>
        public bool UseDue { get; set; }

        /// <summary>The fine tuning stands still while no station is being read.</summary>
        public bool HoldTuningOnNoise { get; set; }

        /// <summary>Bench trace: every frame that fits (start sample, clearest bit, mean bit).</summary>
        public event Action<long, double, double> FrameSeen;

        // ---- state ----------------------------------------------------------------------------

        readonly int _rate;
        double _mark = 2125, _shift = 170, _baud = 45.45;

        double _bit;          // samples per bit
        int _len;             // matched-filter length in samples
        double _markPhase, _spacePhase;
        double[] _mRe, _mIm, _sRe, _sIm;
        double _mReSum, _mImSum, _sReSum, _sImSum;
        int _w;

        double _mEnv, _sEnv, _mNoise, _sNoise;

        // Fine tuning: each filter's output a little while ago, to see how far its phase has turned.
        int _lag;
        double[] _mHistRe, _mHistIm, _sHistRe, _sHistIm;
        int _h;
        double _afcHz, _afcErrRe, _afcErrIm;

        // The soft decision for every recent sample, and its running sum so any stretch of it can be
        // averaged in one step. Indexed by absolute sample number modulo the ring length.
        const int Ring = 1 << 14;
        readonly double[] _cum = new double[Ring];
        long _n;              // samples taken so far
        double _lastV;

        long _searchFrom;                               // earliest sample a new start bit may begin at
        readonly Queue<long> _edges = new Queue<long>(); // starts proposed by mark-to-space edges
        long _due = -1;                                 // start of the character due after the last one

        // Squelch.
        bool _open;
        long _lastGood, _heldAt = -1;
        int _held;

        int _figures;         // Baudot shift: 0 letters, 1 figures

        public RttyDecoder(int sampleRate)
        {
            _rate = sampleRate;
            UnshiftOnSpace = true;
            FineTuning = true;
            FineTuningRange = 50;
            Decision = 2;
            Squelch = 0.1;
            DueSquelch = 0.3;
            MeanGate = 0.4;
            OpenGate = 0.6;
            StayGate = 0.5;
            CloseAfterBits = 30;
            AtcBalance = 0.5;
            SampleWidth = 0.25;
            UseDue = true;
            HoldTuningOnNoise = true;
            Configure();
        }

        /// <summary>Retune: the mark tone in Hz, the shift between the tones, the speed.</summary>
        public void Tune(double markHz, double shiftHz, double baud)
        {
            _mark = markHz;
            _shift = shiftHz;
            _baud = baud;
            Configure();
        }

        void Configure()
        {
            _bit = _rate / _baud;
            // Four cycles of the shift: both tones then sit on nulls of each other's filter. At 170 Hz
            // shift and 45.45 baud that is 23.5 ms against a 22 ms bit - near enough a matched filter.
            // (Three cycles, 17.6 ms, was measured too: worse everywhere but flutter.)
            int k = Math.Max(1, (int)Math.Round(_shift / _baud));
            _len = Math.Max(8, (int)Math.Round(k * _rate / _shift));
            _mRe = new double[_len]; _mIm = new double[_len];
            _sRe = new double[_len]; _sIm = new double[_len];
            _mReSum = _mImSum = _sReSum = _sImSum = 0;
            _w = 0;
            _lag = Math.Max(4, _len / 2);
            _mHistRe = new double[_lag]; _mHistIm = new double[_lag];
            _sHistRe = new double[_lag]; _sHistIm = new double[_lag];
            _h = 0;
            _afcHz = 0;
            _afcErrRe = _afcErrIm = 0;
            _mEnv = _sEnv = _mNoise = _sNoise = 0;
            _edges.Clear();
            _due = -1;
            _searchFrom = _n;
            _open = false;
            _heldAt = -1;
        }

        double MarkTone { get { return (Reverse ? _mark + _shift : _mark) + _afcHz; } }
        double SpaceTone { get { return (Reverse ? _mark : _mark + _shift) + _afcHz; } }

        // ---- the work -------------------------------------------------------------------------

        public void Process(short[] samples, int count)
        {
            for (int i = 0; i < count; i++) Step(samples[i] / 32768.0);
        }

        void Step(double x)
        {
            // 1. Matched filters: each tone mixed to zero, summed over the last _len samples.
            double mr = x * Math.Cos(_markPhase), mi = -x * Math.Sin(_markPhase);
            double sr = x * Math.Cos(_spacePhase), si = -x * Math.Sin(_spacePhase);
            _markPhase += 2 * Math.PI * MarkTone / _rate; if (_markPhase > 2 * Math.PI) _markPhase -= 2 * Math.PI;
            _spacePhase += 2 * Math.PI * SpaceTone / _rate; if (_spacePhase > 2 * Math.PI) _spacePhase -= 2 * Math.PI;

            _mReSum += mr - _mRe[_w]; _mImSum += mi - _mIm[_w];
            _sReSum += sr - _sRe[_w]; _sImSum += si - _sIm[_w];
            _mRe[_w] = mr; _mIm[_w] = mi; _sRe[_w] = sr; _sIm[_w] = si;
            if (++_w >= _len)
            {
                _w = 0;
                // Start the sums afresh once a window so rounding never piles up.
                _mReSum = _mImSum = _sReSum = _sImSum = 0;
                for (int j = 0; j < _len; j++) { _mReSum += _mRe[j]; _mImSum += _mIm[j]; _sReSum += _sRe[j]; _sImSum += _sIm[j]; }
            }
            double m = Math.Sqrt(_mReSum * _mReSum + _mImSum * _mImSum) / _len;
            double s = Math.Sqrt(_sReSum * _sReSum + _sImSum * _sImSum) / _len;

            // 2. The decision: plain and ATC, the surer one wins.
            Track(ref _mEnv, ref _mNoise, m);
            Track(ref _sEnv, ref _sNoise, s);
            double plain = (m - s) / (m + s + 1e-12);

            double mc = Math.Max(Math.Min(m, _mEnv), _mNoise) - _mNoise;
            double sc = Math.Max(Math.Min(s, _sEnv), _sNoise) - _sNoise;
            double me = _mEnv - _mNoise, se = _sEnv - _sNoise;
            double atc = mc * me - sc * se - AtcBalance * (me * me - se * se);
            double scale = 0.5 * (me * me + se * se);
            atc = scale > 1e-20 ? atc / scale : 0;
            if (atc > 1) atc = 1; else if (atc < -1) atc = -1;

            double v = Decision == 0 ? atc : Decision == 1 ? plain : Math.Abs(plain) > Math.Abs(atc) ? plain : atc;

            int at = (int)(_n & (Ring - 1));
            _cum[at] = _cum[(int)((_n - 1) & (Ring - 1))] + v;

            // 5. Fine tuning.
            // Only while a station is being read - the squelch open, or a first clear character held
            // waiting for its second. On noise alone it used to wander off (9 Hz in ten seconds of
            // noise after a station stopped), and the lines on the waterfall walked away with it.
            if (FineTuning && (!HoldTuningOnNoise || _open || (_heldAt >= 0 && _n - _heldAt < (long)(16 * _bit)))) Follow(v);
            else KeepFollowHistory();

            // 3. Framing. The filter's output crosses from mark to space about half a window after
            // the start bit really began.
            if (_lastV > 0 && v <= 0) _edges.Enqueue(_n - _len / 2);
            Frame();

            _lastV = v;
            _n++;
        }

        /// <summary>Level follows a tone up quickly and down slowly; the noise floor the other way about.</summary>
        void Track(ref double env, ref double noise, double x)
        {
            double up = 4 / _bit, down = 1 / (16 * _bit);
            env += (x > env ? up : down) * (x - env);
            double nDown = 4 / _bit, nUp = 1 / (48 * _bit);
            noise += (x < noise ? nDown : nUp) * (x - noise);
        }

        /// <summary>
        /// While one tone is clearly on, the turn of its filter's phase over _lag samples is the
        /// station's offset. Summed as vectors, so a strong moment counts for more than a faint one
        /// and noise averages away; the mixers then move a little after it.
        /// </summary>
        void Follow(double v)
        {
            double pr, pi, cr, ci;
            if (v > 0.5) { pr = _mHistRe[_h]; pi = _mHistIm[_h]; cr = _mReSum; ci = _mImSum; }
            else if (v < -0.5) { pr = _sHistRe[_h]; pi = _sHistIm[_h]; cr = _sReSum; ci = _sImSum; }
            else { pr = pi = cr = ci = 0; }
            _mHistRe[_h] = _mReSum; _mHistIm[_h] = _mImSum;
            _sHistRe[_h] = _sReSum; _sHistIm[_h] = _sImSum;
            if (++_h >= _lag) _h = 0;
            if (pr == 0 && pi == 0) return;

            // Now times the conjugate of then: its angle is how far the phase has turned.
            double dr = cr * pr + ci * pi, di = ci * pr - cr * pi;
            double a = 1 / (4 * _bit);
            _afcErrRe += a * (dr - _afcErrRe);
            _afcErrIm += a * (di - _afcErrIm);
            double errHz = Math.Atan2(_afcErrIm, _afcErrRe) * _rate / (2 * Math.PI * _lag);
            _afcHz += errHz / (8 * _bit);
            if (_afcHz > FineTuningRange) _afcHz = FineTuningRange;
            if (_afcHz < -FineTuningRange) _afcHz = -FineTuningRange;
        }

        // The history Follow compares against must stay current even while it is not following, or the
        // first comparison after a pause would measure a turn over seconds, not over _lag samples.
        void KeepFollowHistory()
        {
            _mHistRe[_h] = _mReSum; _mHistIm[_h] = _mImSum;
            _sHistRe[_h] = _sReSum; _sHistIm[_h] = _sImSum;
            if (++_h >= _lag) _h = 0;
        }

        long LastSampleNeeded(long start)
        {
            return start + (long)(_bit / 3) + Centre(6) + (long)(_bit * SampleWidth / 2) + 1;
        }

        /// <summary>Where, after the frame starts, the filter's output speaks for bit k.</summary>
        long Centre(int k)
        {
            return (long)Math.Round((k + 0.5) * _bit + _len / 2.0);
        }

        /// <summary>Average soft decision over the middle of bit k of a frame starting at start.</summary>
        double BitSoft(long start, int k)
        {
            long c = start + Centre(k);
            long half = Math.Max(1, (long)(_bit * SampleWidth / 2));
            long a = c - half, b = c + half;
            if (_n - a >= Ring - 2 || b > _n) return 0;
            return (_cum[(int)(b & (Ring - 1))] - _cum[(int)(a & (Ring - 1))]) / (b - a);
        }

        void Frame()
        {
            long early = (long)(_bit / 4);
            while (true)
            {
                bool fromEdge;
                long next;
                if (_edges.Count > 0 && (_due < 0 || _edges.Peek() <= _due)) { next = _edges.Peek(); fromEdge = true; }
                else if (_due >= 0) { next = _due; fromEdge = false; }
                else return;
                if (_n < LastSampleNeeded(next)) return;

                if (fromEdge)
                {
                    _edges.Dequeue();
                    if (next < _searchFrom - early) continue;
                    // An edge close to where the next character was due: try both, keep the clearer.
                    bool withDue = _due >= 0 && Math.Abs(next - _due) <= _bit / 2;
                    long other = withDue ? _due : next;
                    if (withDue) _due = -1;
                    TryFrame(next, other, Squelch);
                }
                else
                {
                    _due = -1;
                    if (next < _searchFrom - early) continue;
                    TryFrame(next, next, DueSquelch);
                }
            }
        }

        readonly double[] _bits = new double[7];
        readonly double[] _best = new double[7];

        void TryFrame(long seedA, long seedB, double squelch)
        {
            // Slide the frame up to a third of a bit each way, to where all seven bits are clearest.
            long reach = (long)(_bit / 3), step = Math.Max(1, (long)(_bit / 16));
            double bestScore = double.MinValue;
            long bestStart = -1;
            for (int seed = 0; seed < (seedA == seedB ? 1 : 2); seed++)
            {
                long centre = seed == 0 ? seedA : seedB;
                for (long st = centre - reach; st <= centre + reach; st += step)
                {
                    if (st < _searchFrom - (long)(_bit / 4)) continue;
                    if (_n < LastSampleNeeded(st) - (long)(_bit / 3)) continue;
                    double score = 0;
                    for (int k = 0; k < 7; k++) { _bits[k] = BitSoft(st, k); score += Math.Abs(_bits[k]); }
                    if (_bits[0] >= 0 || _bits[6] <= 0) continue;
                    if (score > bestScore) { bestScore = score; bestStart = st; Array.Copy(_bits, _best, 7); }
                }
            }
            if (bestStart < 0) return;

            double clearest = double.MaxValue;
            for (int k = 0; k < 7; k++) clearest = Math.Min(clearest, Math.Abs(_best[k]));
            double mean = bestScore / 7;
            var seen = FrameSeen;
            if (seen != null) seen(bestStart, clearest, mean);
            if (clearest < squelch || mean < MeanGate) return;

            // The next start bit can follow the stop bit at the earliest one bit after it begins; when
            // it follows at once it is due 7 to 7.5 bits on (one or one and a half stop bits).
            _searchFrom = bestStart + (long)(6.75 * _bit);
            if (UseDue) _due = bestStart + (long)(7.25 * _bit);

            int code = 0;
            for (int k = 1; k <= 5; k++) code = (code << 1) | (_best[k] > 0 ? 1 : 0);

            // 4. The squelch, opening on two.
            if (_open && bestStart - _lastGood > (long)(CloseAfterBits * _bit)) _open = false;
            if (_open)
            {
                // Only a clear character keeps it open; a doubtful one prints but does not, so when
                // the station stops the squelch shuts on the noise that follows.
                if (mean >= StayGate) _lastGood = bestStart;
                Print(code);
                return;
            }
            if (mean < OpenGate) return;
            if (_heldAt >= 0 && bestStart - _heldAt <= (long)(16 * _bit))
            {
                Print(_held);
                Print(code);
                _open = true;
                _lastGood = bestStart;
                _heldAt = -1;
                return;
            }
            _held = code;
            _heldAt = bestStart;
        }

        void Print(int code)
        {
            char c = ToAscii(code);
            if (c == '\0') return;
            var h = CharacterDecoded;
            if (h != null) h(c);
        }

        // ---- Baudot ---------------------------------------------------------------------------

        // ITA2 with the first bit on the air as the top bit of five (the order MMTTY uses).
        static readonly char[] Letters = {
            '\0', 'T', '\r', 'O', ' ', 'H', 'N', 'M',
            '\n', 'L', 'R', 'G', 'I', 'P', 'C', 'V',
            'E', 'Z', 'D', 'B', 'S', 'Y', 'F', 'X',
            'A', 'W', 'J', '\0', 'U', 'Q', 'K', '\0' };
        static readonly char[] Figures = {
            '\0', '5', '\r', '9', ' ', '#', ',', '.',
            '\n', ')', '4', '&', '8', '0', ':', ';',
            '3', '"', '$', '?', '\a', '6', '!', '/',
            '-', '2', '\'', '\0', '7', '1', '(', '\0' };

        char ToAscii(int code)
        {
            code &= 0x1F;
            if (code == 0x1B) { _figures = 1; return '\0'; }
            if (code == 0x1F) { _figures = 0; return '\0'; }
            if (_figures == 0) return Letters[code];
            char c = Figures[code];
            if (c == ' ' && UnshiftOnSpace) _figures = 0;
            if (c == '\a') c = '\0';   // the bell prints nothing
            return c;
        }
    }
}
