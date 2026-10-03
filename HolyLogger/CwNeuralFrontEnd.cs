using System;
using System.Collections.Generic;

namespace HolyLogger
{
    // TURNS THE AUDIO INTO WHAT THE NETWORK WAS TAUGHT ON, and this has to be exact. A network is
    // only as good as the resemblance between what it is shown now and what it was shown while it
    // was learning; feed it the same sound measured a slightly different way and it produces
    // confident nonsense. So every step here follows MorseAngel's own preparation, which is:
    //
    //   1. A spectrogram of the audio - short overlapping slices, each tapered and Fourier'd.
    //   2. Add up three neighbouring frequencies either side of the note being read. One number
    //      per slice: how loud the note is just now. That row of numbers is "the envelope".
    //   3. Divide by a running loudest, so a strong signal and a weak one look alike, and clip at 1.
    //
    // THE SLICES ARE SPACED BY SPEED, not by time. The network was taught on an envelope with about
    // 7.69 numbers to a dit, whatever the operator's speed, so the spacing is worked out from the
    // speed being heard: at 20 WPM a dit is 480 samples and the slices step 62 samples, at 10 WPM
    // they step twice that. It is why the ordinary decoder runs first and this one second - the
    // speed it measures is what sets this up.
    //
    // ONLY THREE FREQUENCIES ARE NEEDED, so there is no Fourier transform here at all: three
    // Goertzel filters give exactly those three numbers of the transform and nothing else, which is
    // a few dozen multiplications instead of a few thousand.
    public class CwNeuralFrontEnd
    {
        // What the network was taught on: this many envelope numbers to one dit.
        public const double NumbersPerDit = 7.69;

        // Either side of the note, as MorseAngel does (nside_bins = 1, so three in all).
        const int BinsEitherSide = 1;

        // How far above the quiet a slice must stand before it is allowed to set the scale.
        const double SignalOverNoise = 8.0;

        // About three seconds of quiet held back while waiting for a first signal - enough to
        // settle the network, and bounded so a silent frequency cannot fill memory.
        const int MostHeldSlices = 400;

        // About two and a half seconds of slices, from which the scale is taken.
        const int RecentSlices = 300;

        readonly int _sampleRate;

        double[] _slice;            // the samples one slice looks at
        double[] _taper;            // the Tukey taper over the slice - see BuildTukey
        double[] _coefficients;     // one per frequency being measured
        int _sliceSamples;          // nperseg - how much sound a slice looks at
        int _hopSamples;            // how far the slices step
        int _fill;                  // how much of the slice buffer is full
        int _sinceLastSlice;

        double _loudest;            // the running loudest, which sets the scale
        double _quietest;           // and the running quietest, which says what is only noise
        readonly Queue<double> _held = new Queue<double>();
        readonly double[] _recent = new double[RecentSlices];
        readonly double[] _sorted = new double[RecentSlices];
        int _recentCount, _recentNext;
        bool _ready;

        /// <summary>The note this is measuring, in Hz. Set by Configure.</summary>
        public double ToneHz { get; private set; }

        /// <summary>The speed the spacing was worked out for. Set by Configure.</summary>
        public double Wpm { get; private set; }

        /// <summary>One envelope number, 0 to 1, every time a slice is finished.</summary>
        public event Action<float> Envelope;


        public CwNeuralFrontEnd(int sampleRate)
        {
            _sampleRate = sampleRate < 4000 ? 8000 : sampleRate;
        }

        /// <summary>
        /// Sets the note to listen to and the speed the spacing is worked out for. Cheap to call;
        /// it does nothing at all unless one of them has really changed, because rebuilding throws
        /// away the slice being gathered.
        /// </summary>
        public void Configure(double toneHz, double wpm)
        {
            if (toneHz < 100 || toneHz > 3000) return;
            if (wpm < 5 || wpm > 60) return;

            // A few Hz or a word a minute either way is not worth rebuilding for - and rebuilding on
            // every small wobble would keep the network from ever settling.
            if (_ready && Math.Abs(toneHz - ToneHz) < 20 && Math.Abs(wpm - Wpm) < Wpm * 0.12) return;

            ToneHz = toneHz;
            Wpm = wpm;

            // MorseAngel's own sizing, followed exactly.
            //   a dit lasts 1.2/wpm seconds, so this many samples:
            int samplesPerDit = (int)(1.2 / wpm * _sampleRate);
            double step = samplesPerDit / NumbersPerDit;

            //   the slice length is the power of two below half the dit
            int nfft = 1 << (int)(Math.Log(samplesPerDit, 2) - 1);
            if (nfft < 16) nfft = 16;
            if (nfft > 1024) nfft = 1024;

            int hop = (int)Math.Round(step);
            if (hop < 1) hop = 1;
            if (hop > nfft) hop = nfft;

            // scipy's rule: a long transform still only looks at 256 samples of sound.
            _sliceSamples = (nfft < 256) ? nfft : 256;
            _hopSamples = hop;

            _slice = new double[_sliceSamples];
            _taper = BuildTukey(_sliceSamples, 0.25);

            // The three frequencies: the transform's own spacing is the sample rate over nfft, and
            // the note is rounded to the nearest of them, exactly as the original rounds its bin.
            int centreBin = (int)Math.Round(toneHz / _sampleRate * nfft);
            _coefficients = new double[2 * BinsEitherSide + 1];
            for (int b = -BinsEitherSide; b <= BinsEitherSide; b++)
            {
                double frequency = (centreBin + b) * (double)_sampleRate / nfft;
                _coefficients[b + BinsEitherSide] = 2.0 * Math.Cos(2.0 * Math.PI * frequency / _sampleRate);
            }

            _fill = 0;
            _sinceLastSlice = 0;
            _loudest = 0;
            _quietest = 0;
            _held.Clear();
            _recentCount = 0; _recentNext = 0;
            _ready = true;
        }

        /// <summary>Forgets the running scale - a new station, or a long silence.</summary>
        public void Reset()
        {
            _loudest = 0;
            _quietest = 0;
            _held.Clear();
            _recentCount = 0; _recentNext = 0;
            _fill = 0;
            _sinceLastSlice = 0;
        }

        /// <summary>Feeds audio in. Raises Envelope once per slice.</summary>
        public void Process(short[] samples, int count)
        {
            if (!_ready) return;

            for (int i = 0; i < count; i++)
            {
                // The slice buffer holds the most recent _sliceSamples, oldest first.
                if (_fill < _sliceSamples)
                {
                    _slice[_fill++] = samples[i] / 32768.0;
                }
                else
                {
                    Array.Copy(_slice, 1, _slice, 0, _sliceSamples - 1);
                    _slice[_sliceSamples - 1] = samples[i] / 32768.0;
                }

                if (_fill < _sliceSamples) continue;

                if (++_sinceLastSlice < _hopSamples) continue;
                _sinceLastSlice = 0;

                RaiseEnvelope(MeasureSlice());
            }
        }

        // THE TAPER IS A TUKEY, NOT A HANN - and getting this wrong is what made the network useless
        // on real signals while it stayed perfect on made-up ones.
        //
        // MorseAngel prepares its audio with scipy's spectrogram and does not name a window, so it
        // gets scipy's default. I assumed that default was a Hann, because that is what almost every
        // other spectrogram uses. It is not: scipy's is ('tukey', 0.25) - flat across the middle
        // three quarters with a short cosine taper at each end, which is a quite different shape and
        // lets through a quite different amount of the note.
        //
        // It was found by running MorseAngel's own Python over one of the operator's recordings and
        // comparing its numbers with mine: they agreed on every size - transform 128, overlap 78,
        // step 50 - and then correlated at 0.25, which is to say not at all.
        static double[] BuildTukey(int length, double alpha)
        {
            var window = new double[length];
            if (length == 1) { window[0] = 1; return window; }

            double n = length - 1;
            double taper = alpha * n / 2.0;

            for (int i = 0; i < length; i++)
            {
                if (i < taper)
                    window[i] = 0.5 * (1 + Math.Cos(Math.PI * (2.0 * i / (alpha * n) - 1)));
                else if (i <= n - taper)
                    window[i] = 1.0;
                else
                    window[i] = 0.5 * (1 + Math.Cos(Math.PI * (2.0 * i / (alpha * n) - 2.0 / alpha + 1)));
            }

            return window;
        }

        double MeasureSlice()
        {
            // scipy takes the average out of every slice before transforming it (detrend='constant').
            // Left in, a steady offset in the sound card's audio would sit in the lowest frequency
            // and leak into everything measured.
            double mean = 0;
            for (int i = 0; i < _sliceSamples; i++) mean += _slice[i];
            mean /= _sliceSamples;

            double total = 0;
            for (int b = 0; b < _coefficients.Length; b++)
            {
                double coefficient = _coefficients[b];
                double s1 = 0, s2 = 0;
                for (int i = 0; i < _sliceSamples; i++)
                {
                    double s0 = (_slice[i] - mean) * _taper[i] + coefficient * s1 - s2;
                    s2 = s1;
                    s1 = s0;
                }
                double power = s1 * s1 + s2 * s2 - coefficient * s1 * s2;
                if (power > 0) total += power;
            }

            return total;
        }

        void RaiseEnvelope(double power)
        {
            // THE RUNNING SCALE. The network was taught on an envelope that reaches 1 on a mark and
            // sits near 0 between them, whatever the signal's actual strength - so the strength has
            // to be divided out before it ever sees it.
            //
            // The loudest heard lately sets the scale, and it leaks away slowly so that a station
            // fading, or a louder one taking the frequency, is followed within a few seconds. The
            // 1.5 is MorseAngel's: dividing by the loudest exactly would put every mark at 1 and
            // leave nothing above it, so the scale is set half as high again and the tops clip.
            // THE SCALE IS TAKEN ONLY FROM A REAL SIGNAL, and getting that wrong cost the first
            // character of every transmission. Set from whatever arrives, the scale simply follows
            // the band noise: quiet air then reads as FULL SCALE - the loudest thing heard IS the
            // noise - and the network is fed a solid mark that never ends until a station starts,
            // by which time it has no idea where it is.
            //
            // So the quietest is tracked too, and the scale is only raised by something standing
            // well clear of it. Until something does, nothing is fed at all.
            // THE SCALE COMES FROM A HIGH PLACE IN THE RECENT PAST, NOT FROM THE LOUDEST EVER HEARD.
            //
            // Taking the loudest was the fault that made the network useless on the real band while
            // it stayed perfect on made-up signals. One crash of static is louder than any CW, and
            // once it had set the scale everything the station sent divided down to nothing - the
            // envelope fed to the network was a flat row of zeros for a whole minute, and the letters
            // that came out were the network's best guess at silence.
            //
            // So the scale is the level that a fifth of the last few seconds stands above. Marks are
            // roughly a third of the time on CW, so that lands inside the marks; a crash of static is
            // one slice in hundreds and cannot move it at all; and it follows a signal fading up and
            // down within a couple of seconds.
            _recent[_recentNext] = power;
            _recentNext = (_recentNext + 1) % _recent.Length;
            if (_recentCount < _recent.Length) _recentCount++;

            Array.Copy(_recent, _sorted, _recentCount);
            Array.Sort(_sorted, 0, _recentCount);

            _quietest = _sorted[_recentCount / 4];

            // THE SCALE IS THE LOUDEST OF THE RECENT PAST, NOT A PERCENTILE OF IT.
            //
            // MorseAngel divides by max(block)/1.5, and the network was taught on what that
            // produces. I used the eightieth percentile instead, to be safe against a crash of
            // static - and that is a much smaller number than the maximum, so everything came out
            // larger and clipped at 1. The network was being shown a flat, saturated shape it had
            // never seen while learning.
            //
            // Proved by running MorseAngel's own Python over one of the operator's recordings: the
            // two transforms agree to a correlation of 1.0000 - identical - and the two ENVELOPES
            // correlated at 0.27. Everything between them was this one number.
            //
            // The static crash that drove me to a percentile is still handled, because the maximum
            // is taken over a bounded window of the last two seconds rather than over all time.
            // A crash spoils the scale for two seconds and then it recovers, which is what their
            // per-block version does too.
            _loudest = _sorted[_recentCount - 1];

            // NOTHING IS FED UNTIL THE SCALE IS KNOWN - AND THEN THE WAIT IS FED TOO.
            //
            // This took three attempts and each failure cost the first character of a transmission,
            // for a different reason each time.
            //
            // Feeding everything from the start was wrong because the scale then follows the band
            // noise, so quiet air reads as full scale and the network is handed a mark that never
            // ends. Feeding nothing until a signal arrives was wrong because an LSTM answers out of
            // what it has been fed, and one handed a mark as its very first input cannot know
            // whether that mark begins a character or sits in the middle of one. Feeding zeros in
            // the meantime was better and still not right: the scale is only settled part way INTO
            // the first mark, so the opening of that mark had already gone out as zero and the mark
            // read short - a dah arriving as a dit, C coming out as F.
            //
            // So the slices are HELD while the scale is unknown, and the moment a real signal sets
            // it they are all let go at once, measured properly. The network gets the quiet before
            // the station and the whole of its first mark. It costs nothing after that: once the
            // scale is known everything goes straight out.
            if (_loudest < _quietest * SignalOverNoise || _loudest <= 0)
            {
                _held.Enqueue(power);
                while (_held.Count > MostHeldSlices) _held.Dequeue();
                return;
            }

            double scale = _loudest / 1.5;

            while (_held.Count > 0) Send(_held.Dequeue() / scale);
            Send(power / scale);
        }

        void Send(double value)
        {
            if (value > 1) value = 1;
            if (value < 0) value = 0;

            var handler = Envelope;
            if (handler == null) return;
            try { handler((float)value); }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }
    }
}