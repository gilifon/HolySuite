using System;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace HolyLogger
{
    /// <summary>
    /// Reads the CW being received and shows nothing else.
    ///
    /// BUILT AS THE KEYER'S OPPOSITE NUMBER. It opens with the keyer, wears the keyer's pale cyan
    /// bar, and carries what it needs in that bar the way the keyer carries Type/Enter and its
    /// speed - so the two windows read as one pair, the sending and the reading, rather than as two
    /// programs that happen to be open. Everything below the bar is decoded text and nothing else,
    /// and the window can be pulled down until only one line of it is left.
    ///
    /// THERE IS NO LISTEN BUTTON, because there is nothing for it to do. The window is open or it is
    /// not; open, it is listening. A button that must always be pressed after opening a window is a
    /// question the program should have answered for itself.
    ///
    /// THE METER EARNED ITS PLACE BEFORE THE DECODER DID - every "it does not decode" report of this
    /// kind starts as a level problem, and the very first silence here was a muted recording input in
    /// Windows. So the bar keeps one lamp: grey for nothing arriving, green for a good level, red for
    /// too loud. It costs no room and it answers the first question anyone will ask.
    /// </summary>
    public class CwDecodeWindow : Window
    {
        /// <summary>
        /// Raised when the recording device is changed in Options, so a window already listening
        /// moves to the new one instead of going on with the old until it is closed and reopened.
        /// </summary>
        public static event Action InputDeviceChanged;

        internal static void RaiseInputDeviceChanged()
        {
            var handler = InputDeviceChanged;
            if (handler == null) return;
            try { handler(); }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // Long enough to scroll back through a QSO, short enough that the box never becomes the
        // reason the window is slow. Trimmed from the front, so the newest text is always kept.
        const int MostCharactersKept = 20000;
        const int CharactersTrimmedAtOnce = 5000;

        // The pale cyan of the CW keycaps and of the keyer's own bar, so the pair plainly belong
        // together. Fixed in every colour scheme, like the keyer's, and the black text on it has to
        // stay readable whatever the scheme is doing.
        static readonly Brush CwKeyBrush = Frozen(Color.FromRgb(0x7F, 0xFE, 0xFF));
        static readonly Brush GroupEdgeBrush = Frozen(Color.FromRgb(0x06, 0x2A, 0x2C));
        static readonly Brush SpeedBrush = Frozen(Color.FromRgb(0x1E, 0x90, 0xFF));

        static readonly Brush QuietBrush = Frozen(Color.FromRgb(0x9E, 0x9E, 0x9E));
        static readonly Brush GoodBrush = Frozen(Color.FromRgb(0x2E, 0xA8, 0x4D));
        static readonly Brush LoudBrush = Frozen(Color.FromRgb(0xE8, 0x8A, 0x00));
        static readonly Brush TooLoudBrush = Frozen(Color.FromRgb(0xCC, 0x33, 0x33));

        static Brush Frozen(Color colour)
        {
            var brush = new SolidColorBrush(colour);
            brush.Freeze();
            return brush;
        }

        readonly WaveInRecorder _recorder = new WaveInRecorder();
        readonly DispatcherTimer _screenTimer;

        CwDecoder _decoder;

        TextBox _text;
        TextBlock _speedText;
        Ellipse _lamp;

        // Letters arrive on the capture thread, one or two at a time, and are collected here for the
        // screen timer to put on show in one go. A Dispatcher call per letter would be a thousand
        // hops a minute for something the eye cannot see happening that fast anyway.
        readonly StringBuilder _pending = new StringBuilder();
        readonly object _pendingGate = new object();

        // The lamp falls back gently instead of snapping to grey between characters. Without it it
        // flickers on CW - which is silence half the time - and cannot be read at all.
        double _shownLevel;

        public CwDecodeWindow()
        {
            Title = "CW Decode";
            Width = 620;
            Height = 300;

            // WIDE ENOUGH FOR THE BAR, SHORT ENOUGH FOR ONE LINE. The floor on the height is the bar
            // plus a single row of text: an operator who wants a one-line ticker along the bottom of
            // his screen must be able to have one, and any taller floor than this would refuse him.
            MinWidth = 380;

            // The bar, plus the framed paper with one row of text in it. Anything taller than this
            // would refuse an operator who wants a one-line ticker along the edge of his screen.
            MinHeight = 88;

            ResizeMode = ResizeMode.CanResize;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            // The OS caption is replaced by our own, only so the Clear key and the speed can sit in
            // it - Windows will not let anything of ours into its title bar. Same setup as the keyer:
            // CaptionHeight matches the bar built below, so dragging the bar still moves the window.
            WindowStyle = WindowStyle.None;
            SetResourceReference(BackgroundProperty, "WindowBg");
            System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome
            {
                CaptionHeight = 32,
                CornerRadius = new CornerRadius(0),
                GlassFrameThickness = new Thickness(0),
                ResizeBorderThickness = new Thickness(6),
                UseAeroCaptionButtons = false
            });

            BuildContent();

            _recorder.Failed += OnRecorderFailed;
            _recorder.Samples += OnSamples;
            InputDeviceChanged += OnInputDeviceChanged;

            _screenTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(50)
            };
            _screenTimer.Tick += ScreenTimer_Tick;

            RestoreWindowBounds();

            Loaded += (s, e) => StartListening();
            Closing += OnClosing;
        }

        // ---- what is on screen ----

        void BuildContent()
        {
            // The box paints itself - white in the light schemes, the scheme's own paper in the dark
            // ones - and is NOT made transparent, so the text sits on paper rather than on the
            // window's grey. The same arrangement as the keyer's two rows.
            _text = new TextBox
            {
                FontSize = 18,
                FontFamily = new FontFamily("Consolas"),
                IsReadOnly = true,
                IsTabStop = false,
                TextWrapping = TextWrapping.Wrap,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(4, 2, 4, 2),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            _text.SetResourceReference(ForegroundProperty, "TextBrush");

            // A thin dark line round the paper, exactly as the keyer frames its send row and its
            // record: it is what makes the text look like something written down rather than
            // something floating on the window.
            var frame = new Border
            {
                BorderThickness = new Thickness(1),
                Margin = new Thickness(8, 6, 8, 8),
                Child = _text
            };
            frame.SetResourceReference(Border.BorderBrushProperty, "MutedTextBrush");
            frame.SetBinding(Border.BackgroundProperty,
                new System.Windows.Data.Binding("Background") { Source = _text });

            var titleBar = BuildTitleBar();
            DockPanel.SetDock(titleBar, Dock.Top);

            var body = new DockPanel();
            body.Children.Add(titleBar);
            body.Children.Add(frame);

            Content = body;
        }

        Border BuildTitleBar()
        {
            var closeBtn = new Button
            {
                Content = "",
                Width = 32,
                Style = Application.Current.Resources["CaptionCloseButtonStyle"] as Style,
                Foreground = Brushes.Black,
                ToolTip = "Close"
            };
            System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(closeBtn, true);
            closeBtn.Click += (s, e) => Close();

            var clearBtn = new Button
            {
                Content = "Clear",
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.Black,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(7, 0, 7, 0),
                Margin = new Thickness(2, 0, 0, 0),
                Height = 24,
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = "Empty the text and start the speed and the note over"
            };
            clearBtn.Click += (s, e) => ClearEverything();

            var clearPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            clearPanel.Children.Add(clearBtn);

            _speedText = new TextBlock
            {
                Text = string.Empty,
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Foreground = SpeedBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0),
                MinWidth = 96,
                TextAlignment = TextAlignment.Right,
                ToolTip = "The speed and the note this is reading, worked out from the air"
            };

            _lamp = new Ellipse
            {
                Width = 12,
                Height = 12,
                Fill = QuietBrush,
                Stroke = GroupEdgeBrush,
                StrokeThickness = 1,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
                ToolTip = "Grey: no sound arriving. Green: a good level. Red: too loud."
            };

            var right = new StackPanel { Orientation = Orientation.Horizontal };
            DockPanel.SetDock(right, Dock.Right);
            right.Children.Add(_lamp);
            right.Children.Add(_speedText);
            right.Children.Add(GroupFrame(clearPanel));
            right.Children.Add(closeBtn);

            var titleText = new TextBlock
            {
                Text = "CW Decode",
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.Black,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(5, 0, 0, 0),
                ToolTip = "What the other station is sending. It listens whenever this window is open."
            };

            var icon = BuildEarIcon();
            DockPanel.SetDock(icon, Dock.Left);
            DockPanel.SetDock(titleText, Dock.Left);

            var bar = new DockPanel { LastChildFill = false };
            bar.Children.Add(right);
            bar.Children.Add(icon);
            bar.Children.Add(titleText);

            return new Border { Height = 32, Child = bar, Background = CwKeyBrush };
        }

        // Same frame the keyer puts round its Type/Enter pair, so a key on this bar looks like a key
        // on that one.
        static UIElement GroupFrame(UIElement inner)
        {
            var frame = new Border
            {
                CornerRadius = new CornerRadius(5),
                BorderThickness = new Thickness(1),
                BorderBrush = GroupEdgeBrush,
                Padding = new Thickness(3, 1, 3, 1),
                Margin = new Thickness(0, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = inner
            };
            System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(frame, true);
            return frame;
        }

        // An ear: three arcs growing away from a dot, the sign every radio uses for listening. Drawn
        // here like the keyer's straight key - no file, no licence, and black on the cyan bar.
        static UIElement BuildEarIcon()
        {
            var canvas = new Canvas { Width = 24, Height = 24 };
            canvas.Children.Add(new Ellipse
            {
                Width = 4,
                Height = 4,
                Fill = Brushes.Black,
                Margin = new Thickness(4, 10, 0, 0)
            });
            AddArc(canvas, "M 10,7 A 6,6 0 0 1 10,17");
            AddArc(canvas, "M 13.5,4.5 A 9.5,9.5 0 0 1 13.5,19.5");
            AddArc(canvas, "M 17,2 A 13,13 0 0 1 17,22");

            return new Viewbox
            {
                Width = 16,
                Height = 16,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = canvas
            };
        }

        static void AddArc(Canvas canvas, string data)
        {
            canvas.Children.Add(new Path
            {
                Data = Geometry.Parse(data),
                Stroke = Brushes.Black,
                StrokeThickness = 1.8,
                Fill = Brushes.Transparent
            });
        }

        // ---- where the window was left last time ----
        //
        // Same rule as the Cluster Alerts window: the saved spot is checked against the WHOLE desktop
        // - every monitor - before it is used. Checking only the main screen is how a window on the
        // second monitor gets restored somewhere the operator cannot reach it, and if the saved spot
        // is not on any screen any more the window simply opens centred instead.

        void RestoreWindowBounds()
        {
            try
            {
                var s = Properties.Settings.Default;
                if (s.CwDecodeWindowWidth >= MinWidth) Width = s.CwDecodeWindowWidth;
                if (s.CwDecodeWindowHeight >= MinHeight) Height = s.CwDecodeWindowHeight;

                if (IsPositionOnScreen(s.CwDecodeWindowLeft, s.CwDecodeWindowTop))
                {
                    WindowStartupLocation = WindowStartupLocation.Manual;
                    Left = s.CwDecodeWindowLeft;
                    Top = s.CwDecodeWindowTop;
                }
                else
                {
                    WindowStartupLocation = WindowStartupLocation.CenterOwner;
                }
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        void SaveWindowBounds()
        {
            try
            {
                var b = WindowState == WindowState.Normal
                    ? new Rect(Left, Top, Width, Height)
                    : RestoreBounds;

                var s = Properties.Settings.Default;
                if (!double.IsNaN(b.Left) && !double.IsInfinity(b.Left) &&
                    !double.IsNaN(b.Top) && !double.IsInfinity(b.Top))
                {
                    s.CwDecodeWindowLeft = b.Left;
                    s.CwDecodeWindowTop = b.Top;
                }
                if (b.Width > 0) s.CwDecodeWindowWidth = b.Width;
                if (b.Height > 0) s.CwDecodeWindowHeight = b.Height;
                s.Save();
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        static bool IsPositionOnScreen(double left, double top)
        {
            if (double.IsNaN(left) || double.IsNaN(top) ||
                double.IsInfinity(left) || double.IsInfinity(top))
                return false;

            double vsLeft = SystemParameters.VirtualScreenLeft;
            double vsTop = SystemParameters.VirtualScreenTop;
            double vsRight = vsLeft + SystemParameters.VirtualScreenWidth;
            double vsBottom = vsTop + SystemParameters.VirtualScreenHeight;

            return left >= vsLeft - 10 && top >= vsTop - 10 &&
                   left <= vsRight - 100 && top <= vsBottom - 60;
        }

        // ---- listening ----

        // Which device to listen to is set in Options (General > CW Decode) and simply read here.
        static string ChosenDevice()
        {
            try { return Properties.Settings.Default.CwDecodeInputDevice ?? string.Empty; }
            catch (Exception swallowed) { Log.Swallow(swallowed); return string.Empty; }
        }

        void OnInputDeviceChanged()
        {
            if (!_recorder.IsRunning) return;
            StopListening();
            StartListening();
        }

        /// <summary>
        /// Kept for the main window, which opens this alongside the keyer. Listening starts by
        /// itself now, so this only makes sure of it.
        /// </summary>
        public void StartListeningNow()
        {
            StartListening();
        }

        void StartListening()
        {
            if (_recorder.IsRunning) return;

            string error;
            if (!_recorder.Start(ChosenDevice(), out error))
            {
                // Nowhere else to say it now the window is only text, so it is said IN the text -
                // where he is already looking, and where it stays until he clears it.
                ShowTrouble(error + "  Choose the device in Tools > Options > General.");
                return;
            }

            var decoder = new CwDecoder(_recorder.ActualSampleRate);
            decoder.Text += OnDecodedText;
            _decoder = decoder;

            _shownLevel = 0;
            _screenTimer.Start();
        }

        void StopListening()
        {
            _screenTimer.Stop();
            try { _recorder.Stop(); }
            catch (Exception swallowed) { Log.Swallow(swallowed); }

            var decoder = _decoder;
            _decoder = null;
            if (decoder != null) decoder.Text -= OnDecodedText;

            FlushPendingText();

            _shownLevel = 0;
            if (_lamp != null) _lamp.Fill = QuietBrush;
            if (_speedText != null) _speedText.Text = string.Empty;
        }

        void ClearEverything()
        {
            lock (_pendingGate) _pending.Clear();
            _text.Clear();

            var decoder = _decoder;
            if (decoder != null) decoder.Reset();
        }

        void ShowTrouble(string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            _text.AppendText((_text.Text.Length > 0 ? "\r\n" : "") + message + "\r\n");
            _text.ScrollToEnd();
        }

        // On the capture thread. Hand the block straight to the decoder - it is a few thousand
        // multiplications, far less than the 100 ms of audio it represents.
        void OnSamples(short[] samples, int count)
        {
            var decoder = _decoder;
            if (decoder == null) return;

            try { decoder.Process(samples, count); }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // Also on the capture thread: collect, do not touch the screen.
        void OnDecodedText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            lock (_pendingGate) _pending.Append(text);
        }

        void ScreenTimer_Tick(object sender, EventArgs e)
        {
            FlushPendingText();
            UpdateLamp();
            UpdateSpeed();
        }

        void FlushPendingText()
        {
            string text;
            lock (_pendingGate)
            {
                if (_pending.Length == 0) return;
                text = _pending.ToString();
                _pending.Clear();
            }

            if (_text.Text.Length > MostCharactersKept)
                _text.Text = _text.Text.Substring(CharactersTrimmedAtOnce);

            _text.AppendText(text);
            _text.ScrollToEnd();
        }

        void UpdateLamp()
        {
            double peak = _recorder.Level;
            _shownLevel = peak > _shownLevel ? peak : _shownLevel * 0.82;

            if (_shownLevel < 0.01) _lamp.Fill = QuietBrush;
            else if (_shownLevel > 0.92) _lamp.Fill = TooLoudBrush;
            else if (_shownLevel > 0.7) _lamp.Fill = LoudBrush;
            else _lamp.Fill = GoodBrush;
        }

        void UpdateSpeed()
        {
            var decoder = _decoder;
            if (decoder == null || !decoder.SignalPresent)
            {
                _speedText.Text = string.Empty;
                return;
            }

            _speedText.Text = Math.Round(decoder.Wpm).ToString("N0") + " WPM   "
                            + Math.Round(decoder.ToneHz).ToString("N0") + " Hz";
        }

        // The recorder gave up on its own thread - the radio was switched off, or the device was
        // taken away. Back onto the UI thread before touching anything on screen.
        void OnRecorderFailed(string message)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                StopListening();
                ShowTrouble(message);
            }));
        }

        void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            SaveWindowBounds();
            _recorder.Failed -= OnRecorderFailed;
            _recorder.Samples -= OnSamples;
            InputDeviceChanged -= OnInputDeviceChanged;
            StopListening();
            try { _recorder.Dispose(); }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }
    }
}
