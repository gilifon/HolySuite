using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace HolyLogger
{
    // THE NEW WATERFALL STRIP ON TEST (2026-10-21): a window whose own thread is kept busy (Busy ms of blocking
    // every 200 ms, as HolyLogger's shared thread is), fed keyed CW at 700 Hz in 100 ms blocks. The strip's own
    // frame log (%TEMP%\HolyLogger_cw_flow.tsv) says whether its frames stay on time anyway.
    //   StripTest.exe <seconds> <busy ms>
    static class TestClock { static readonly System.Diagnostics.Stopwatch W = System.Diagnostics.Stopwatch.StartNew(); public static double Ms { get { return W.Elapsed.TotalMilliseconds; } } }

    static class StripTest
    {
        // A recording played through Plain and the strip as the decode window does, in real time; a screen
        // photo at <photo s>, and the letters Plain gave the strip counted.  StripTest.exe <wav> <photo s>
        [STAThread]
        static int Main(string[] a)
        {
            byte[] raw = System.IO.File.ReadAllBytes(a[0]);
            int rate = BitConverter.ToInt32(raw, 24);
            double photoAt = double.Parse(a[1], System.Globalization.CultureInfo.InvariantCulture);
            var strip = new CwWaterfallStrip(ms => (byte)0);
            var win = new Window { Width = 860, Height = 200, Content = new Border { Child = strip.Element, Height = 78, VerticalAlignment = VerticalAlignment.Top } };
            win.Show();
            int letters = 0;
            var plain = new CwDecoder(rate);
            long wfBase = strip.Written;
            var text = new System.Text.StringBuilder();
            plain.LetterTimed += (l, s0, s1) => { letters++; text.Append(l); strip.AddLetter(l, wfBase + (s0 + s1) / 2); };
            var feeder = new System.Threading.Thread(() =>
            {
                int block = rate / 10;
                var b = new short[block];
                var clock = System.Diagnostics.Stopwatch.StartNew();
                int k = 0;
                for (int i = 44; i + 2 * block <= raw.Length; k++)
                {
                    for (int n = 0; n < block; n++) { b[n] = BitConverter.ToInt16(raw, i); i += 2; }
                    strip.Feed(b, block, rate, false);
                    plain.Process(b, block);
                    strip.NoteHz = plain.SignalPresent ? plain.ToneHz : 0;
                    int wait = (int)((k + 1) * 100 - clock.ElapsedMilliseconds);
                    if (wait > 0) System.Threading.Thread.Sleep(wait);
                }
            }) { IsBackground = true };
            feeder.Start();
            var shot = new DispatcherTimer { Interval = TimeSpan.FromSeconds(photoAt) };
            shot.Tick += (s, e) =>
            {
                shot.Stop();
                var p = win.PointToScreen(new Point(0, 0));
                using (var bmp = new System.Drawing.Bitmap(860, 90))
                using (var g = System.Drawing.Graphics.FromImage(bmp))
                {
                    g.CopyFromScreen((int)p.X, (int)p.Y, 0, 0, bmp.Size);
                    bmp.Save(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "strip_test.png"));
                }
                Console.WriteLine(letters + " letters given to the strip: " + text);
                strip.Close(); win.Close(); Dispatcher.CurrentDispatcher.InvokeShutdown();
            };
            shot.Start();
            Dispatcher.Run();
            return 0;
        }
    }

    // THE WATERFALL, REBUILT (2026-10-21, his request: "rethink how to make it and rewrite it").
    //
    // What was wrong. It was drawn on the one thread every HolyLogger window shares: whenever anything else was
    // busy - the main window, the radio panel, a timer, the decoded text - the strip missed its turn. His frame
    // log: about one frame in five missed, all the time. And every new column moved the whole picture a step,
    // with a slide in between trying to catch up; when frames were missed the two fell out of step and it
    // jumped back and forth.
    //
    // What it is now:
    //   ITS OWN THREAD. The strip is drawn on a thread of its own (a HostVisual in the window, its content made
    //   on that thread), so nothing else in the program can hold it up.
    //   A PICTURE THAT IS NEVER MOVED. Each new column is written once into a circular picture (twice, in fact,
    //   at i and i + RingColumns, so the six seconds on show are always one piece with no seam). Where the
    //   picture sits on the screen comes from one steady clock alone: 100 columns a second, eased by at most
    //   5% to keep a fifth of a second behind the sound. So it can only glide - a late frame moves it exactly
    //   as far as the time that passed, never back.
    //   THE LETTERS ON THE SAME THREAD AND CLOCK, drawn under their own marks.
    //
    // The capture thread hands it sound (Feed); the window hands it letters, the pitch, the note being read.
    sealed class CwWaterfallStrip
    {
        public const int Rows = 56;                         // 300 .. 1000 Hz
        public const int ShownColumns = 600;                // six seconds at one column per 10 ms
        const int RingColumns = 1024;                       // more than shown plus the lead the sound may have
        public const double LowHz = 300, HighHz = 1000;
        const double BehindColumns = 20;                    // a fifth of a second behind the sound
        const double MaxEase = 0.05;                        // the clock eased by at most 5% to keep that distance

        // ---- handed over from other threads ----
        readonly short[] _ring = new short[1 << 16];
        long _written;                                      // samples ever written
        int _rate;
        readonly object _gate = new object();
        const int SlotShift = 6;                            // our transmitting / key state, per 64 samples
        readonly bool[] _tx = new bool[(1 << 16) >> SlotShift];
        readonly byte[] _key = new byte[(1 << 16) >> SlotShift];
        double _soundStartMs = double.NaN;
        readonly Func<double, byte> _keyAt;
        static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();   // the frames' own clock

        public volatile int PitchHz = 700;
        double _noteHz;
        public double NoteHz
        {
            get { return System.Threading.Volatile.Read(ref _noteHz); }
            set { System.Threading.Volatile.Write(ref _noteHz, value); }
        }

        sealed class Letter { public string Text; public long Sample; public FormattedText Shape; }
        readonly System.Collections.Generic.List<Letter> _letterQueue = new System.Collections.Generic.List<Letter>();

        // ---- the strip's own thread ----
        readonly HostVisual _host = new HostVisual();
        readonly System.Threading.Thread _thread;
        Dispatcher _dispatcher;
        DrawingVisual _picture;
        WriteableBitmap _bitmap;
        int[] _column;
        double[] _rowFloor;
        long _read;                                         // the sample the next column starts at
        long _columnsMade;
        double _shownAt = double.NaN;                       // the column at the right edge, fractional
        double _lastFrameMs;
        readonly System.Collections.Generic.List<Letter> _letters = new System.Collections.Generic.List<Letter>();
        double _width, _height;                             // the element's size, from the window's thread
        static readonly int[] Palette = MakePalette();
        static readonly Typeface LetterFace = new Typeface(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

        public FrameworkElement Element { get; private set; }

        public CwWaterfallStrip(Func<double, byte> keyAt)
        {
            _keyAt = keyAt;
            var element = new HostElement(_host);
            element.SizeChanged += (s, e) => { lock (_gate) { _width = e.NewSize.Width; _height = e.NewSize.Height; } };
            Element = element;

            _thread = new System.Threading.Thread(Run) { IsBackground = true, Name = "CW waterfall", Priority = System.Threading.ThreadPriority.AboveNormal };
            _thread.SetApartmentState(System.Threading.ApartmentState.STA);
            _thread.Start();
        }

        /// <summary>Samples ever handed in - the waterfall's own sample count.</summary>
        public long Written { get { lock (_gate) return _written; } }

        /// <summary>Stops the strip's thread. Called as the window closes, BEFORE it is torn down.</summary>
        // DETACHED FIRST, AND WAITED FOR (his error, 2026-10-21: "Index was out of range" on closing the decoder).
        // The window being torn down let go of the HostVisual while the strip's thread still had its drawing
        // attached to it - a race inside WPF. So the strip's own thread takes its drawing off first, and the
        // window's thread waits until it has, before the window goes. Only then is the thread stopped.
        public void Close()
        {
            var d = _dispatcher;
            if (d == null || _closed) return;
            _closed = true;
            try
            {
                d.Invoke(new Action(() =>
                {
                    CompositionTarget.Rendering -= Frame;
                    if (_target != null) { _target.RootVisual = null; _target.Dispose(); _target = null; }
                }));
                d.BeginInvokeShutdown(DispatcherPriority.Normal);
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        bool _closed;
        VisualTarget _target;

        /// <summary>A block of sound, on the capture thread; tx = we were transmitting.</summary>
        public void Feed(short[] samples, int count, int rate, bool tx)
        {
            // On the window's transmit clock: the moments looked up in _keyAt are stamped on it.
            double endMs = TestClock.Ms;           // the block's last sample has just arrived
            lock (_gate)
            {
                // One steady clock for the sound (the CAT marks came out in several lengths when each block was
                // timed by its arrival): a fixed start plus the count over the rate, the start the earliest any
                // block allows, creeping later only slowly to follow the sound card's clock.
                if (rate > 0)
                {
                    double start = endMs - (_written + count) * 1000.0 / rate;
                    if (rate != _rate || double.IsNaN(_soundStartMs) || start < _soundStartMs) _soundStartMs = start;
                    else _soundStartMs += 0.002 * (start - _soundStartMs);
                    _rate = rate;
                }
                for (int i = 0; i < count; i++)
                {
                    if (i == 0 || (_written & ((1 << SlotShift) - 1)) == 0)
                    {
                        int slot = (int)((_written >> SlotShift) & (_tx.Length - 1));
                        _tx[slot] = tx;
                        _key[slot] = tx && rate > 0 ? _keyAt(_soundStartMs + _written * 1000.0 / rate) : (byte)0;
                    }
                    _ring[(int)(_written++ & (_ring.Length - 1))] = samples[i];
                }
            }
        }

        /// <summary>A letter, written under the sound around `sample` (the waterfall's own count). Any thread.</summary>
        public void AddLetter(string text, long sample)
        {
            lock (_letterQueue) _letterQueue.Add(new Letter { Text = text, Sample = sample });
        }

        // ---------------- on the strip's thread ----------------

        void Run()
        {
            try
            {
                _dispatcher = Dispatcher.CurrentDispatcher;
                _bitmap = new WriteableBitmap(2 * RingColumns, Rows, 96, 96, PixelFormats.Bgr32, null);
                var black = new int[2 * RingColumns * Rows];
                for (int i = 0; i < black.Length; i++) black[i] = Palette[0];
                _bitmap.WritePixels(new Int32Rect(0, 0, 2 * RingColumns, Rows), black, 2 * RingColumns * 4, 0);
                _column = new int[Rows];

                _picture = new DrawingVisual();
                RenderOptions.SetBitmapScalingMode(_picture, BitmapScalingMode.LowQuality);
                // Moving text: not snapped to whole pixels, or the letters hop while the picture glides.
                TextOptions.SetTextHintingMode(_picture, TextHintingMode.Animated);
                _target = new VisualTarget(_host) { RootVisual = _picture };
                CompositionTarget.Rendering += Frame;
                Dispatcher.Run();
            }
            catch (Exception ex) { Log.Swallow(ex); }
        }

        TimeSpan _lastRendering;

        void Frame(object sender, EventArgs e)
        {
            try
            {
                // WPF can ask twice for the same frame; moving the picture again for it put an uneven step in.
                var re = e as RenderingEventArgs;
                if (re != null)
                {
                    if (re.RenderingTime == _lastRendering) return;
                    _lastRendering = re.RenderingTime;
                }
                double now = TestClock.Ms;
                int rate; long written; double width, height, soundStart;
                lock (_gate) { rate = _rate; written = _written; width = _width; height = _height; soundStart = _soundStartMs; }
                if (rate <= 0 || width <= 0 || double.IsNaN(soundStart)) { _lastFrameMs = now; return; }
                int hop = rate / 100, window = rate * 40 / 1000;

                // Every column the sound now allows - a few a frame. Far behind (the window was hidden): to the present.
                if (written - _read > _ring.Length / 2)
                {
                    long columns = (written - window) / hop - (long)BehindColumns - 2;
                    _read = columns * hop;
                    _columnsMade = columns;
                    _shownAt = double.NaN;
                }
                int made = 0;
                while (written - _read >= window && made < 40) { MakeColumn(rate, hop, window); made++; }

                // THE STEADY CLOCK. Where the strip should be is worked out from the sound's own clock (see Feed:
                // a fixed start plus samples over the rate), not from how many columns are ready - those come ten
                // at a time with each block of sound, and following them made the pace wobble ten times a second.
                // The picture moves 100 columns a second, frame by frame, and is nudged towards that place by at
                // most MaxEase of its speed. Only at the start, or after falling far out, does it jump.
                double ready = _columnsMade;
                double target = ((now - soundStart) * rate / 1000.0 - window / 2.0) / hop - BehindColumns;
                double dt = Math.Min(250, Math.Max(0, now - _lastFrameMs));
                double before = _shownAt;
                if (double.IsNaN(_shownAt) || Math.Abs(target - _shownAt) > 50) _shownAt = target;
                else
                {
                    double moved = _shownAt + dt / 10.0;
                    double most = MaxEase * dt / 10.0;
                    _shownAt = moved + Math.Max(-most, Math.Min(most, (target - moved) * 0.1));
                }
                if (_shownAt > ready - 1) { _shownAt = ready - 1; _flowWaited++; }   // never a column not made yet
                if (!double.IsNaN(before)) _flowStepWorst = Math.Max(_flowStepWorst, Math.Abs((_shownAt - before) - dt / 10.0));
                _lastFrameMs = now;

                Draw(width, height, hop, window);
                LogFrame(now);
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        void Draw(double width, double height, int hop, int window)
        {
            double perColumn = width / ShownColumns;
            double leftColumn = _shownAt - ShownColumns;               // the column at the left edge
            double startInRing = leftColumn - Math.Floor(leftColumn / RingColumns) * RingColumns;
            using (DrawingContext dc = _picture.RenderOpen())
            {
                dc.PushClip(new RectangleGeometry(new Rect(0, 0, width, height)));
                dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, width, height));
                dc.DrawImage(_bitmap, new Rect(-startInRing * perColumn, 0, 2 * RingColumns * perColumn, Rows));

                // The letters, each under the middle of its own marks.
                lock (_letterQueue) { _letters.AddRange(_letterQueue); _letterQueue.Clear(); }
                for (int i = _letters.Count - 1; i >= 0; i--)
                {
                    Letter l = _letters[i];
                    double column = (l.Sample - window / 2) / (double)hop;
                    double x = (column - leftColumn) * perColumn - 5;
                    if (x < -20) { _letters.RemoveAt(i); continue; }
                    if (x > width) continue;
                    if (l.Shape == null)
                        l.Shape = new FormattedText(l.Text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                                                    LetterFace, 16, Brushes.White, 1.0);
                    dc.DrawText(l.Shape, new Point(x, Rows + 1));
                }
                dc.Pop();
            }
        }

        // One column: 40 ms of sound under a Blackman-Harris window, 300 to 1000 Hz up the strip.
        void MakeColumn(int rate, int hop, int window)
        {
            int nfft = 1; while (nfft < window * 3 / 2) nfft <<= 1;
            if (_re == null || _re.Length != nfft) { _re = new double[nfft]; _im = new double[nfft]; }
            bool tx; byte key;
            lock (_gate)
            {
                int slot = (int)(((_read + window / 2) >> SlotShift) & (_tx.Length - 1));
                tx = _tx[slot]; key = _key[slot];
                for (int i = 0; i < nfft; i++)
                {
                    // Blackman-Harris, not Hann: a strong station leaked into every row of the strip through a
                    // Hann window's sidelobes, 31 dB down; these are 92 dB down.
                    double a = 2 * Math.PI * i / (window - 1);
                    double shape = 0.35875 - 0.48829 * Math.Cos(a) + 0.14128 * Math.Cos(2 * a) - 0.01168 * Math.Cos(3 * a);
                    _re[i] = i < window ? _ring[(int)((_read + i) & (_ring.Length - 1))] * shape : 0;
                    _im[i] = 0;
                }
            }
            _read += hop;
            Fft(_re, _im);

            double binHz = (double)rate / nfft;
            var db = _db ?? (_db = new double[Rows]);
            for (int y = 0; y < Rows; y++)
            {
                double f = HighHz - (HighHz - LowHz) * y / (Rows - 1);
                double b = f / binHz; int b0 = (int)b; double w = b - b0;
                double m0 = Math.Sqrt(_re[b0] * _re[b0] + _im[b0] * _im[b0]);
                double m1 = Math.Sqrt(_re[b0 + 1] * _re[b0 + 1] + _im[b0 + 1] * _im[b0 + 1]);
                db[y] = 20 * Math.Log10(m0 + (m1 - m0) * w + 1);
            }

            // EACH NOTE ITS OWN QUIET LEVEL. Each row's level drops at once to anything quieter and creeps up
            // slowly, so it settles on that note's noise, and only a station stands out above it.
            if (_rowFloor == null) _rowFloor = (double[])db.Clone();
            // (Not "any column loud across the whole strip is ours", tried before: a received station's key clicks
            // span the strip for a moment too, and its marks got cyan edges. Our sending is known without it - from
            // the keyer, the CAT text laid out, and the radio's TX report.)
            if (tx) TxColumn(db, key);
            else
            {
                for (int y = 0; y < Rows; y++)
                    _rowFloor[y] += (db[y] < _rowFloor[y] ? 0.05 : 0.002) * (db[y] - _rowFloor[y]);
                double note = NoteHz;
                int noteRow = note > 0 ? (int)Math.Round((HighHz - note) / (HighHz - LowHz) * (Rows - 1)) : -1;
                bool dot = _columnsMade % 3 == 0;
                for (int y = 0; y < Rows; y++)
                {
                    double v = (db[y] - _rowFloor[y] - 6) / 30;   // tried on his YT3T recording, strong and weak
                    int shade = (int)(Math.Max(0, Math.Min(1, v)) * 255);
                    _column[y] = y == noteRow && dot ? 0x40E040 : Palette[shade];
                }
            }

            int x = (int)(_columnsMade % RingColumns);
            _bitmap.WritePixels(new Int32Rect(x, 0, 1, Rows), _column, 4, 0);
            _bitmap.WritePixels(new Int32Rect(x + RingColumns, 0, 1, Rows), _column, 4, 0);
            _columnsMade++;
        }

        double[] _re, _im, _db, _over;

        // OUR OWN KEYING, DRAWN CLEAN (his design): a cyan mark of fixed height at his pitch while the key is
        // down, black between. Key down from our own keyer's edges or the CAT text laid out (key 2 down, 1 up);
        // with neither (0), from the sound: half the strip or more TxKeyDownDb over its quiet level. The quiet
        // levels are left alone while we transmit, so receiving starts again on the band's own.
        const double TxKeyDownDb = 24;
        const int TxMarkRows = 5;
        const int TxInk = (0x00 << 16) | (0xE0 << 8) | 0xFF;

        bool WholeStripLoud(double[] db)
        {
            var over = _over ?? (_over = new double[Rows]);
            for (int y = 0; y < Rows; y++) over[y] = db[y] - _rowFloor[y];
            Array.Sort(over);
            return over[Rows / 2] >= TxKeyDownDb;
        }

        void TxColumn(double[] db, byte key)
        {
            bool down = key != 0 ? key == 2 : WholeStripLoud(db);
            int hz = Math.Max(CwDecodeWindow.LowestPitchHz, Math.Min(CwDecodeWindow.HighestPitchHz, PitchHz));
            int mid = (int)Math.Round((HighHz - hz) / (HighHz - LowHz) * (Rows - 1));
            for (int y = 0; y < Rows; y++)
                _column[y] = down && Math.Abs(y - mid) <= TxMarkRows / 2 ? TxInk : Palette[0];
        }

        // Black for silence, dark blue for noise, bright yellow for a station, white for the loudest - the noise
        // kept dim so the dits and dahs are what the eye finds.
        static int[] MakePalette()
        {
            var stops = new[] { new[] { 0, 0, 0 }, new[] { 10, 30, 110 }, new[] { 255, 200, 0 }, new[] { 255, 255, 255 } };
            var p = new int[256];
            for (int i = 0; i < 256; i++)
            {
                double t = i / 255.0 * (stops.Length - 1);
                int k = Math.Min(stops.Length - 2, (int)t); double w = t - k;
                int r = (int)(stops[k][0] + (stops[k + 1][0] - stops[k][0]) * w);
                int g = (int)(stops[k][1] + (stops[k + 1][1] - stops[k][1]) * w);
                int b = (int)(stops[k][2] + (stops[k + 1][2] - stops[k][2]) * w);
                p[i] = (r << 16) | (g << 8) | b;
            }
            return p;
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

        // TEMPORARY, AS BEFORE: with %TEMP%\HolyLogger_cw_flow.on present, every 10 s the strip's own frames -
        // how many, how many came over 25 ms after the last, the longest gap - go to %TEMP%\HolyLogger_cw_flow.tsv.
        static readonly bool FlowLogOn = System.IO.File.Exists(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "HolyLogger_cw_flow.on"));
        double _flowSince, _flowLast, _flowWorst, _flowStepWorst;
        int _flowFrames, _flowLate, _flowWaited;

        void LogFrame(double now)
        {
            if (!FlowLogOn) return;
            if (_flowLast > 0)
            {
                double gap = now - _flowLast;
                if (gap > 25) _flowLate++;
                if (gap > _flowWorst) _flowWorst = gap;
            }
            _flowLast = now;
            _flowFrames++;
            if (_flowSince == 0) _flowSince = now;
            if (now - _flowSince < 10000) return;
            try
            {
                System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "HolyLogger_cw_flow.tsv"),
                    string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "strip thread\t{0:F0} ms\t{1} frames\t{2} over 25 ms\tlongest {3:F0} ms\tuneven step up to {4:F2} columns\twaited for sound {5}\n",
                        now, _flowFrames, _flowLate, _flowWorst, _flowStepWorst, _flowWaited));
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
            _flowSince = now; _flowFrames = 0; _flowLate = 0; _flowWorst = 0; _flowStepWorst = 0; _flowWaited = 0;
        }

        // The window's end of it: an element that shows what the strip's thread draws.
        sealed class HostElement : FrameworkElement
        {
            readonly HostVisual _visual;
            public HostElement(HostVisual visual) { _visual = visual; AddVisualChild(visual); }
            protected override int VisualChildrenCount { get { return 1; } }
            protected override Visual GetVisualChild(int index) { return _visual; }
        }
    }
}
