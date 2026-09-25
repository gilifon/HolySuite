using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace HolyLogger
{
    /// <summary>
    /// A small "CW sending monitor" window. It shows the full CW text being keyed and a blinking
    /// coloured cursor that advances through the characters in sync with the radio's transmission.
    ///
    /// The IC-7300 (and CI-V radios in general) do not report keying progress, so the cursor is
    /// driven by a timing simulation based on the standard PARIS CW timing. The speed (WPM) is
    /// self-calibrated by the owner: after each transmission the real elapsed time is divided by the
    /// computed unit count to learn the radio's actual keyer speed for the next message.
    /// </summary>
    public class CwSendMonitorWindow : Window
    {
        private static readonly Dictionary<char, string> Morse = new Dictionary<char, string>
        {
            {'A',".-"},   {'B',"-..."}, {'C',"-.-."}, {'D',"-.."},  {'E',"."},    {'F',"..-."},
            {'G',"--."},  {'H',"...."}, {'I',".."},   {'J',".---"}, {'K',"-.-"},  {'L',".-.."},
            {'M',"--"},   {'N',"-."},   {'O',"---"},  {'P',".--."}, {'Q',"--.-"}, {'R',".-."},
            {'S',"..."},  {'T',"-"},    {'U',"..-"},  {'V',"...-"}, {'W',".--"},  {'X',"-..-"},
            {'Y',"-.--"}, {'Z',"--.."},
            {'0',"-----"},{'1',".----"},{'2',"..---"},{'3',"...--"},{'4',"....-"},
            {'5',"....."},{'6',"-...."},{'7',"--..."},{'8',"---.."},{'9',"----."},
            {'.',".-.-.-"},{',',"--..--"},{'?',"..--.."},{'/',"-..-."},
            {'@',".--.-."},{'=',"-...-"}, {'+',".-.-."}, {'-',"-....-"},
        };

        private static readonly Brush SentBrush = new SolidColorBrush(Color.FromRgb(0x00, 0x99, 0xCC));
        private static readonly Brush UpcomingBrush = new SolidColorBrush(Color.FromRgb(0xB5, 0xB5, 0xB5));
        private static readonly Brush CurrentForeground = new SolidColorBrush(Color.FromRgb(0x1E, 0x2A, 0x34));
        private static readonly Brush CursorBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xC9, 0x57));
        private static readonly Brush DoneBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0xA8, 0x4D));

        static CwSendMonitorWindow()
        {
            SentBrush.Freeze();
            UpcomingBrush.Freeze();
            CurrentForeground.Freeze();
            CursorBrush.Freeze();
            DoneBrush.Freeze();
        }

        private readonly string _text;
        private readonly double[] _cumulativeUnits;
        private readonly double _totalUnits;
        private readonly Border[] _cells;
        private readonly TextBlock[] _glyphs;
        private readonly TextBlock _wpmLabel;
        private readonly ProgressBar _progress;

        private readonly DispatcherTimer _advanceTimer;
        private readonly DispatcherTimer _blinkTimer;

        private double _wpm;
        private DateTime _startUtc;
        private bool _running;
        private bool _finished;
        private bool _cursorOn = true;
        private int _currentIndex;

        /// <summary>Total PARIS units for the supplied text (used by the owner for WPM calibration).</summary>
        public double TotalUnits => _totalUnits;

        public CwSendMonitorWindow(string text, double initialWpm, string title)
        {
            _text = string.IsNullOrEmpty(text) ? string.Empty : text.ToUpperInvariant();
            _wpm = initialWpm < 5 ? 5 : (initialWpm > 80 ? 80 : initialWpm);

            _cumulativeUnits = CumulativeUnits(_text);
            _totalUnits = _cumulativeUnits.Length == 0 ? 0 : _cumulativeUnits[_cumulativeUnits.Length - 1];

            Title = title;
            // Frameless, compact: no title bar/chrome, and the window shrinks to fit its content.
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            SizeToContent = SizeToContent.WidthAndHeight;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            // Never steal focus: the main window must keep keyboard focus so the F-keys keep
            // working (pressing the same key again stops the transmission and closes this window).
            ShowActivated = false;
            Focusable = false;
            IsHitTestVisible = false;
            Background = Brushes.Transparent;

            var rootBorder = new Border
            {
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x00, 0xCC, 0xFF)),
                Margin = new Thickness(10),
                MinWidth = 260,
                Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xFB, 0xFF)),
                Effect = new DropShadowEffect { BlurRadius = 8, ShadowDepth = 1, Opacity = 0.3, Color = Color.FromRgb(0x6A, 0x82, 0x96) }
            };

            var grid = new Grid { Margin = new Thickness(14) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Title row: caption + live WPM
            var titleRow = new Grid();
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var caption = new TextBlock
            {
                Text = title,
                FontSize = 16,
                Foreground = Brushes.DimGray,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(caption, 0);
            titleRow.Children.Add(caption);

            _wpmLabel = new TextBlock
            {
                Text = "~" + Math.Round(_wpm) + " WPM",
                FontSize = 16,
                Foreground = Brushes.DimGray,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(_wpmLabel, 1);
            titleRow.Children.Add(_wpmLabel);

            Grid.SetRow(titleRow, 0);
            grid.Children.Add(titleRow);

            // The message text as per-character cells
            var wrap = new WrapPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 560,
                Margin = new Thickness(0, 10, 0, 10)
            };

            _cells = new Border[_text.Length];
            _glyphs = new TextBlock[_text.Length];
            for (int i = 0; i < _text.Length; i++)
            {
                var glyph = new TextBlock
                {
                    Text = _text[i] == ' ' ? "\u00A0" : _text[i].ToString(),
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 30,
                    FontWeight = FontWeights.Bold,
                    Foreground = UpcomingBrush,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };

                var cell = new Border
                {
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(2, 0, 2, 0),
                    Margin = new Thickness(1, 0, 1, 0),
                    Background = Brushes.Transparent,
                    Child = glyph
                };

                _glyphs[i] = glyph;
                _cells[i] = cell;
                wrap.Children.Add(cell);
            }

            Grid.SetRow(wrap, 1);
            grid.Children.Add(wrap);

            _progress = new ProgressBar
            {
                Height = 6,
                Minimum = 0,
                Maximum = 100,
                Value = 0,
                Foreground = new SolidColorBrush(Color.FromRgb(0x00, 0x99, 0xCC)),
                Background = new SolidColorBrush(Color.FromRgb(0xE4, 0xE4, 0xE4)),
                BorderThickness = new Thickness(0)
            };
            Grid.SetRow(_progress, 2);
            grid.Children.Add(_progress);

            rootBorder.Child = grid;
            Content = rootBorder;

            _advanceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
            _advanceTimer.Tick += AdvanceTimer_Tick;

            _blinkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            _blinkTimer.Tick += BlinkTimer_Tick;

            _currentIndex = 0;
            RepaintAll();
        }

        /// <summary>Computes the total PARIS units for a message (static, for the owner's calibration).</summary>
        // MORSE SPACING, AND ONLY WHERE IT BELONGS. Three units between the letters of a word, seven
        // between words - and the seven REPLACES the three, it is not added to it.
        public const double LetterGapUnits = 3.0;
        public const double WordGapUnits = 7.0;

        // THE COUNT USED TO RUN ABOUT A SEVENTH TOO HIGH, and everything resting on it went wrong in
        // the same direction. Every character was charged a trailing three-unit gap, including the
        // last one where no gap follows, and a space was charged seven ON TOP of the three already
        // added by the character in front of it - ten units for a word gap that is seven.
        //
        // It mattered twice over. The keying speed is worked out as units divided by the seconds the
        // radio was on air, so an inflated count read the radio FASTER than it keys: a radio set to
        // twenty was reported at twenty-three. And the cursor that walks the message as it goes out
        // was pacing itself against a message longer than the one being sent, so it arrived at the
        // last letter after the operator had already heard it.
        public static double[] CumulativeUnits(string text)
        {
            text = text ?? string.Empty;

            var cumulative = new double[text.Length];
            double running = 0;
            bool afterLetter = false;

            for (int i = 0; i < text.Length; i++)
            {
                char c = char.ToUpperInvariant(text[i]);

                if (c == ' ')
                {
                    // A run of spaces is one word gap, not one each.
                    if (afterLetter) running += WordGapUnits;
                    afterLetter = false;
                }
                else
                {
                    if (afterLetter) running += LetterGapUnits;
                    running += ElementUnits(c);
                    afterLetter = true;
                }

                cumulative[i] = running;
            }

            return cumulative;
        }

        public static double ComputeTotalUnits(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            var cumulative = CumulativeUnits(text);
            return cumulative.Length == 0 ? 0 : cumulative[cumulative.Length - 1];
        }

        // ONE MORSE TABLE FOR THE PROGRAM. The COM-port keyer (PortCwKeyer) keys from the same table
        // this window counts from, so what goes out and what the cursor walks can never disagree.
        public static bool TryGetMorse(char c, out string pattern)
        {
            return Morse.TryGetValue(char.ToUpperInvariant(c), out pattern) && !string.IsNullOrEmpty(pattern);
        }

        // The dits and dahs of one character, with the one-unit gaps between them. NO gap after it -
        // what follows the character is the business of whatever comes next.
        private static double ElementUnits(char c)
        {
            if (!Morse.TryGetValue(char.ToUpperInvariant(c), out string pattern) || string.IsNullOrEmpty(pattern))
            {
                return 4.0;
            }

            double units = 0;
            for (int i = 0; i < pattern.Length; i++)
            {
                units += pattern[i] == '-' ? 3 : 1;
                if (i < pattern.Length - 1) units += 1; // intra-character gap
            }
            return units;
        }

        /// <summary>Called by the owner when the radio actually keys up (TX on). Starts the cursor.</summary>
        public void StartCursor()
        {
            if (_finished || _running) return;
            _running = true;
            _startUtc = DateTime.UtcNow;
            _lastTickUtc = _startUtc;
            _unitsDone = 0;
            _blinkTimer.Start();
            _advanceTimer.Start();
        }

        /// <summary>Lets the owner update the speed base mid-life (e.g. with a freshly learned WPM).</summary>
        public void UpdateWpm(double wpm)
        {
            _wpm = wpm < 5 ? 5 : (wpm > 80 ? 80 : wpm);
            _wpmLabel.Text = "~" + Math.Round(_wpm) + " WPM";
        }

        /// <summary>
        /// True once the cursor has reached the end of the text - every unit of it counted at the
        /// speed the radio reports. The owner uses it to know that a reported return to receive IS
        /// the end of the message rather than a break-in gap: those happen while units remain.
        /// </summary>
        internal bool TextDone
        {
            get { return _totalUnits > 0 && _unitsDone >= _totalUnits; }
        }

        /// <summary>Transmission ended normally: close the window immediately, no delay.</summary>
        public void Complete()
        {
            if (_finished) return;
            _finished = true;
            _advanceTimer.Stop();
            _blinkTimer.Stop();
            try { Close(); } catch (System.Exception swallowed) { Log.Swallow(swallowed); }
        }

        /// <summary>Transmission aborted early: close the window immediately, no delay.</summary>
        public void Freeze()
        {
            if (_finished) return;
            _finished = true;
            _advanceTimer.Stop();
            _blinkTimer.Stop();
            try { Close(); } catch (System.Exception swallowed) { Log.Swallow(swallowed); }
        }

        // UNITS ARE ADDED UP AS THEY GO, not worked out afresh from the start time each tick. The
        // operator can turn the radio's speed knob in the middle of a message - the keyer reads it and
        // this window is told - and dividing the WHOLE elapsed time by the NEW speed would re-price
        // everything already sent and jump the cursor. What has been keyed has been keyed; only what
        // comes after it moves at the new speed.
        private double _unitsDone;
        private DateTime _lastTickUtc = DateTime.MinValue;

        private void AdvanceTimer_Tick(object sender, EventArgs e)
        {
            if (_finished || _totalUnits <= 0) return;

            DateTime now = DateTime.UtcNow;
            DateTime since = _lastTickUtc == DateTime.MinValue ? now : _lastTickUtc;
            _lastTickUtc = now;

            double seconds = (now - since).TotalSeconds;
            if (seconds > 0) _unitsDone += seconds * _wpm / 1.2;

            double elapsedUnits = _unitsDone;

            int idx = 0;
            while (idx < _text.Length && _cumulativeUnits[idx] <= elapsedUnits)
            {
                idx++;
            }

            if (idx >= _text.Length)
            {
                // Reached the end of our estimate but the radio hasn't reported RX yet.
                // Hold on the last character until the owner calls Complete().
                idx = _text.Length - 1;
            }

            _progress.Value = Math.Min(100, elapsedUnits / _totalUnits * 100.0);

            if (idx != _currentIndex)
            {
                _currentIndex = idx;
                RepaintAll();
            }
        }

        private void BlinkTimer_Tick(object sender, EventArgs e)
        {
            _cursorOn = !_cursorOn;
            if (_currentIndex >= 0 && _currentIndex < _cells.Length)
            {
                PaintCell(_currentIndex);
            }
        }

        private void RepaintAll()
        {
            for (int i = 0; i < _cells.Length; i++)
            {
                PaintCell(i);
            }
        }

        private void PaintCell(int i)
        {
            if (i < 0 || i >= _cells.Length) return;

            if (_finished && _currentIndex >= _text.Length)
            {
                _glyphs[i].Foreground = DoneBrush;
                _cells[i].Background = Brushes.Transparent;
                return;
            }

            if (i < _currentIndex)
            {
                _glyphs[i].Foreground = SentBrush;
                _cells[i].Background = Brushes.Transparent;
            }
            else if (i == _currentIndex)
            {
                _glyphs[i].Foreground = CurrentForeground;
                _cells[i].Background = _cursorOn ? CursorBrush : Brushes.Transparent;
            }
            else
            {
                _glyphs[i].Foreground = UpcomingBrush;
                _cells[i].Background = Brushes.Transparent;
            }
        }
    }

    // -- CW KEYED FROM A COM PORT ------------------------------------------------------------------
    //
    // THE OTHER WAY TO SEND CW: not asking the radio to key a text (CAT), but keying it the way a
    // straight key does - one line of a serial port, DTR or RTS, held on for every dit and dah. A
    // cheap USB-to-serial adapter wired to the radio's KEY jack does it, and so does the radio's own
    // USB port on the Icoms that can be told to key on DTR. Chosen in Options > General, "CW via".
    //
    // HOLYLOGGER THEN MAKES THE MORSE ITSELF, so three things become ours that used to be the radio's:
    //
    //   THE SPEED. The radio's KEY SPEED knob only drives its own keyer, which is not used here.
    //   THE TIMING. Windows' ordinary timers tick every 15 ms or so, and a dit at 40 WPM is 30 ms - a
    //     timer that coarse would key like a drunk. So the keying runs on a thread of its own at the
    //     highest priority, with the system timer asked for 1 ms and the last millisecond of every
    //     wait spun rather than slept. Every edge is placed on a schedule measured from the start of
    //     the character, so small late wake-ups never add up along a message.
    //   THE SAFETY. A key left down is a transmitter left on. The line is released: on every stop, at
    //     the end of every element whatever else happens (a finally), when the port is closed, and by
    //     a watchdog that lets it go if it has somehow been down for longer than any element at any
    //     speed could last. Windows drops DTR and RTS itself when the program ends or the adapter is
    //     unplugged.
    public sealed class PortCwKeyer : IDisposable
    {
        // Longest any single element can be: a dah at 5 WPM is 720 ms. Anything down longer than
        // this is a fault, and the watchdog releases it.
        private const int LongestKeyDownMs = 3000;

        // The speeds offered. Slower than 5 WPM nobody sends; faster than 60 the timing itself gets thin.
        public const int SlowestWpm = 5;
        public const int FastestWpm = 60;

        private readonly System.IO.Ports.SerialPort _port;
        private readonly bool _useRts;
        private readonly object _gate = new object();
        private readonly Queue<char> _waiting = new Queue<char>();
        private readonly System.Threading.AutoResetEvent _wake = new System.Threading.AutoResetEvent(false);
        private readonly System.Threading.Thread _thread;
        private readonly System.Threading.Timer _watchdog;
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

        private volatile bool _closing;
        // EVERY STOP IS A NEW GENERATION. A character remembers the generation it began in and gives up
        // the moment it changes. A plain "stop asked" flag was tried first and had a trap in it: Esc
        // pressed while nothing was sending left the flag up, and the NEXT message was swallowed.
        private volatile int _stopGen;
        private volatile bool _sending;
        private volatile bool _reportedIdle = true;
        private volatile bool _down;
        private long _downSinceTicks;
        private double _wpm = 20;

        // Where the next letter may start: a letter gap after a letter, a word gap after a space.
        private double _nextStartMs;
        private bool _afterLetter;

        public string PortName { get; private set; }
        public string Line { get { return _useRts ? "RTS" : "DTR"; } }

        /// <summary>Raised on the keying thread when sending starts (true) and when it has finished (false).</summary>
        public event Action<bool> BusyChanged;

        /// <summary>
        /// Every edge as it happens, on the keying thread: down (true) or up, and the milliseconds on the
        /// keyer's own clock. For measuring the timing, and for knowing exactly what went out when.
        /// </summary>
        public event Action<bool, double> Edge;

        public double Wpm
        {
            get { lock (_gate) return _wpm; }
            set { lock (_gate) _wpm = Math.Max(SlowestWpm, Math.Min(FastestWpm, value)); }
        }

        /// <summary>True from the moment text is handed over until its last element has ended.</summary>
        public bool Busy
        {
            get { lock (_gate) return _sending || _waiting.Count > 0; }
        }

        /// <summary>True while the line is actually held on - the transmitter is keyed.</summary>
        public bool KeyDown { get { return _down; } }

        private PortCwKeyer(System.IO.Ports.SerialPort port, bool useRts, double wpm)
        {
            _port = port;
            _useRts = useRts;
            PortName = port == null ? "(no port)" : port.PortName;
            Wpm = wpm;

            _thread = new System.Threading.Thread(Run)
            {
                IsBackground = true,
                Priority = System.Threading.ThreadPriority.Highest,
                Name = "CW keying on " + PortName
            };
            _thread.Start();

            _watchdog = new System.Threading.Timer(_ => Watch(), null, 250, 250);
        }

        /// <summary>
        /// Opens the port with the keying line OFF. Null and a plain reason when it cannot be opened -
        /// another program has it, or the adapter is not plugged in.
        /// </summary>
        public static PortCwKeyer Open(string portName, bool useRts, double wpm, out string whyNot)
        {
            whyNot = null;
            System.IO.Ports.SerialPort port = null;
            try
            {
                // BOTH LINES OFF BEFORE THE PORT OPENS. .NET applies these as it opens, so the radio
                // never sees the line rise - some adapters raise DTR on open by default, and that
                // would key the transmitter the moment the option was chosen.
                port = new System.IO.Ports.SerialPort(portName)
                {
                    DtrEnable = false,
                    RtsEnable = false,
                    Handshake = System.IO.Ports.Handshake.None
                };
                port.Open();
                port.DtrEnable = false;
                port.RtsEnable = false;
                return new PortCwKeyer(port, useRts, wpm);
            }
            catch (UnauthorizedAccessException)
            {
                whyNot = portName + " is being used by another program.";
            }
            catch (System.IO.IOException)
            {
                whyNot = portName + " is not there - is the keying cable plugged in?";
            }
            catch (Exception ex)
            {
                whyNot = portName + " could not be opened: " + ex.Message;
            }

            try { if (port != null) port.Dispose(); }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
            return null;
        }

        /// <summary>
        /// A keyer with NO PORT behind it: everything runs - the thread, the timing, the edges - and no
        /// line is touched. For measuring the timing without keying a transmitter.
        /// </summary>
        public static PortCwKeyer WithoutPort(double wpm)
        {
            return new PortCwKeyer(null, false, wpm);
        }

        /// <summary>Hands text over to be keyed after whatever is already waiting. False once closed.</summary>
        public bool Send(string text)
        {
            if (_closing || string.IsNullOrEmpty(text)) return !_closing;

            bool started;
            lock (_gate)
            {
                started = !_sending && _waiting.Count == 0;
                foreach (char c in text.ToUpperInvariant())
                {
                    string pattern;
                    if (c == ' ' || CwSendMonitorWindow.TryGetMorse(c, out pattern)) _waiting.Enqueue(c);
                }
                if (_waiting.Count > 0) _sending = true;
            }
            if (started && _sending) RaiseBusy(true);
            _wake.Set();
            return true;
        }

        /// <summary>Stops NOW: the line goes off at once and everything still waiting is thrown away.</summary>
        public void Stop()
        {
            lock (_gate)
            {
                _waiting.Clear();
                _stopGen++;
            }
            Release();
            _wake.Set();
        }

        public void Dispose()
        {
            _closing = true;
            Stop();
            try { _watchdog.Dispose(); } catch (Exception swallowed) { Log.Swallow(swallowed); }
            try { _thread.Join(500); } catch (Exception swallowed) { Log.Swallow(swallowed); }
            Release();
            try { if (_port != null) { _port.Close(); _port.Dispose(); } } catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        [System.Runtime.InteropServices.DllImport("winmm.dll")]
        private static extern uint timeBeginPeriod(uint ms);

        [System.Runtime.InteropServices.DllImport("winmm.dll")]
        private static extern uint timeEndPeriod(uint ms);

        private void Run()
        {
            try { timeBeginPeriod(1); } catch (Exception swallowed) { Log.Swallow(swallowed); }
            try
            {
                while (!_closing)
                {
                    char c;
                    bool have;
                    int gen;
                    lock (_gate)
                    {
                        have = _waiting.Count > 0;
                        c = have ? _waiting.Dequeue() : ' ';
                        gen = _stopGen;
                        if (!have && _sending) _sending = false;
                    }

                    if (!have)
                    {
                        if (!_sending) RaiseBusyIfIdle();
                        _wake.WaitOne(50);
                        continue;
                    }

                    KeyCharacter(c, gen);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("CW keying on " + PortName + " stopped: " + ex.Message);
            }
            finally
            {
                Release();
                try { timeEndPeriod(1); } catch (Exception swallowed) { Log.Swallow(swallowed); }
            }
        }

        private void RaiseBusyIfIdle()
        {
            if (_reportedIdle) return;
            _reportedIdle = true;
            var handler = BusyChanged;
            if (handler != null) try { handler(false); } catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        private void RaiseBusy(bool busy)
        {
            _reportedIdle = !busy;
            var handler = BusyChanged;
            if (handler != null) try { handler(busy); } catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        private void KeyCharacter(char c, int gen)
        {
            double unit = 1200.0 / Wpm;
            double now = _clock.Elapsed.TotalMilliseconds;

            if (c == ' ')
            {
                // A run of spaces is one word gap, measured from the end of the last letter.
                if (_afterLetter) _nextStartMs += (CwSendMonitorWindow.WordGapUnits - CwSendMonitorWindow.LetterGapUnits) * unit;
                _afterLetter = false;
                return;
            }

            string pattern;
            if (!CwSendMonitorWindow.TryGetMorse(c, out pattern)) return;

            // Never before the gap is over; and after a pause in the typing, not in the past either.
            double at = Math.Max(now, _nextStartMs);

            for (int i = 0; i < pattern.Length; i++)
            {
                if (gen != _stopGen || _closing) break;
                if (!WaitUntil(at, gen)) break;

                double length = (pattern[i] == '-' ? 3 : 1) * unit;
                Press(gen);
                try
                {
                    if (!WaitUntil(at + length, gen)) break;
                }
                finally
                {
                    Release();
                }
                at += length + unit;                    // the one-unit gap inside the letter
            }

            // The letter ends where its last element ended; the next may start a letter gap later.
            _nextStartMs = at - unit + CwSendMonitorWindow.LetterGapUnits * unit;
            _afterLetter = true;
        }

        // Sleeps most of the way, then spins the last stretch: Sleep alone wakes up to a millisecond or
        // two late even with the 1 ms timer, and at 40 WPM two milliseconds is a fifteenth of a dit.
        private bool WaitUntil(double targetMs, int gen)
        {
            while (true)
            {
                if (gen != _stopGen || _closing) return false;
                double left = targetMs - _clock.Elapsed.TotalMilliseconds;
                if (left <= 0) return true;
                if (left > 3) System.Threading.Thread.Sleep((int)(left - 2));
                else System.Threading.Thread.SpinWait(200);
            }
        }

        private readonly object _lineGate = new object();

        private void Press(int gen)
        {
            lock (_lineGate)
            {
                if (gen != _stopGen || _closing) return;
                SetLine(true);
                _downSinceTicks = DateTime.UtcNow.Ticks;
                _down = true;
            }
            RaiseEdge(true);        // only reached when the line really went down
        }

        private void Release()
        {
            bool was;
            lock (_lineGate)
            {
                was = _down;
                SetLine(false);
                _down = false;
            }
            if (was) RaiseEdge(false);
        }

        private void RaiseEdge(bool down)
        {
            var handler = Edge;
            if (handler == null) return;
            try { handler(down, _clock.Elapsed.TotalMilliseconds); }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        private void SetLine(bool on)
        {
            try
            {
                if (_port == null || !_port.IsOpen) return;
                if (_useRts) _port.RtsEnable = on; else _port.DtrEnable = on;
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // THE LAST LINE OF DEFENCE. Nothing in the keying holds the line longer than one element, so
        // if it has been down for three seconds something has gone wrong - the thread stalled, the
        // machine froze for a moment - and the transmitter is let go whatever the reason.
        private void Watch()
        {
            if (!_down) return;
            if (DateTime.UtcNow.Ticks - _downSinceTicks < TimeSpan.FromMilliseconds(LongestKeyDownMs).Ticks) return;
            Log.Warn("CW keying on " + PortName + ": key held down too long - released by the watchdog.");
            Stop();
        }
    }
}
