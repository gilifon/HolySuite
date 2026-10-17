using System;
using System.Collections.Generic;
using System.IO;
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
    public sealed class CwDeepDecoder : IDisposable
    {
        public const int Rate = 3200;
        const int Fft = 256, Hop = 48, Bin0 = 24;
        const double WindowSeconds = 15, StrideSeconds = 3, LagSeconds = 6;
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

        // EVERY LETTER WITH ITS TIME, in seconds from the first sound given - for the time-lined view in
        // Both. The time is where the network said it: 0.8 dit after the letter's last mark ended
        // (measured 2026-10-17, deepnet/spike_offset.py: median -0.004 s, 80% within 0.03 s).
        public event Action<string, double> LetterTimed;

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
                if (_resetAsked) { _resetAsked = false; lock (_gate) { _printedTo = -1; _nextRun = _written + (long)(WindowSeconds * Rate); } }
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
            var a = new float[n];
            lock (_gate) for (int i = 0; i < n; i++) a[i] = _ring[(int)((start + i) % _ring.Length)];

            int frames;
            float[] spec = Spectrogram(a, out frames);
            int steps;
            float[] lp = _net.Forward(spec, frames, out steps);

            long from = _printedTo < 0 ? start : _printedTo;
            long to = toTheEnd ? end : end - (long)(LagSeconds * Rate);
            if (to <= from) return;
            double stepSamples = 4.0 * Hop;
            var said = new StringBuilder();
            int prev = -1;
            for (int s = 0; s < steps; s++)
            {
                int best = 0;
                for (int c = 1; c < _net.NClass; c++) if (lp[s * _net.NClass + c] > lp[s * _net.NClass + best]) best = c;
                bool isLetter = best < Chars.Length && best != prev;
                prev = best == _net.NClass - 1 ? -1 : best;      // the blank parts two equal letters
                if (!isLetter) continue;
                long at = start + (long)(s * stepSamples);
                if (at < from || at >= to) continue;
                char ch = Chars[best];
                if (ch == ' ') { if (_lastWasSpace) continue; _lastWasSpace = true; }
                else _lastWasSpace = false;
                string letter = ch == '=' ? "<BT>" : ch == '+' ? "<AR>" : ch.ToString();
                said.Append(letter);
                var timed = LetterTimed;
                if (timed != null && ch != ' ') timed(letter, at / (double)Rate);
            }
            _printedTo = to;
            var text = Text;
            if (said.Length > 0 && text != null) text(said.ToString());
        }

        float[] Spectrogram(float[] a, out int frames)
        {
            int n = a.Length, pad = Fft / 2, bins = _net.NBins;
            frames = 1 + n / Hop;
            var spec = new float[frames * bins];
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
}
