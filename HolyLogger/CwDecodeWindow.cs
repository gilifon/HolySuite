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

        /// <summary>
        /// A callsign the operator double-clicked in the decoded text. The main window puts it in
        /// the DX Callsign box, which is where {CALL} in a keyer macro reads from - so double-click,
        /// then F-key, and the answer goes out to the station just read.
        /// </summary>
        public event Action<string> CallsignChosen;

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

        // The ink on that white paper - not pure black, which glares against white at this size.
        static readonly Brush PaperInk = Frozen(Color.FromRgb(0x1E, 0x2A, 0x34));

        static Brush Frozen(Color colour)
        {
            var brush = new SolidColorBrush(colour);
            brush.Freeze();
            return brush;
        }

        readonly WaveInRecorder _recorder = new WaveInRecorder();
        readonly DispatcherTimer _screenTimer;

        CwDecoder _decoder;

        // THE NETWORK RUNS BESIDE THE PLAIN DECODER, NOT INSTEAD OF IT - and both are fed always,
        // whichever is on show. They are close enough in quality that neither can be called the
        // winner: on a minute of real French CW the plain one read "AU PLAISIR ET BOT N E SOIR" and
        // the network "AU PLAISSR ET BONNE SOIR", each right where the other was wrong. So the
        // choice belongs to the operator, on the bar, to be made while he listens - and the plain
        // one, which has been on the air longest, is what he gets until he says otherwise.
        //
        // The network also needs the plain decoder running: the note and the speed it measures are
        // what the network's front end is set up from.
        static readonly CwNeuralNet SharedNet = new CwNeuralNet();
        static bool _netTried;
        CwNeuralDecoder _neural;

        CwDecodedText _text;
        TextBlock _speedText;
        Ellipse _lamp;
        Button _plainBtn, _networkBtn, _bothBtn;
        CwDecodedText _networkText;
        Border _networkFrame;
        TextBlock _plainLabel, _networkLabel;

        // Letters arrive on the capture thread, one or two at a time, and are collected here for the
        // screen timer to put on show in one go. A Dispatcher call per letter would be a thousand
        // hops a minute for something the eye cannot see happening that fast anyway.
        readonly StringBuilder _pendingPlain = new StringBuilder();
        readonly StringBuilder _pendingNetwork = new StringBuilder();
        readonly object _pendingGate = new object();

        /// <summary>Which reader's letters are shown.</summary>
        public enum Reader { Plain, Network, Both }

        // The lamp falls back gently instead of snapping to grey between characters. Without it it
        // flickers on CW - which is silence half the time - and cannot be read at all.
        double _shownLevel;

        public CwDecodeWindow()
        {
            Title = "CW Decoder";
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

            LoadNetworkOnce();
            BuildContent();
            PaintWhich();
            ApplyShowLayout();

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
            _text = MakeTextBox();
            var frame = Paper(_text.Box);

            // The second reader's box, shown only in Both.
            _networkText = MakeTextBox();
            _networkFrame = Paper(_networkText.Box);
            _networkFrame.Visibility = Visibility.Collapsed;

            _plainLabel = Label("Plain");
            _networkLabel = Label("Network");
            _plainLabel.Visibility = Visibility.Collapsed;

            var titleBar = BuildTitleBar();
            DockPanel.SetDock(titleBar, Dock.Top);

            // A grid rather than a stack: in Both the two boxes share the room equally however the
            // window is dragged, which is what makes them comparable at a glance.
            var boxes = new Grid();
            boxes.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            boxes.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            boxes.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            boxes.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            Grid.SetRow(_plainLabel, 0);
            Grid.SetRow(frame, 1);
            Grid.SetRow(_networkLabel, 2);
            Grid.SetRow(_networkFrame, 3);

            boxes.Children.Add(_plainLabel);
            boxes.Children.Add(frame);
            boxes.Children.Add(_networkLabel);
            boxes.Children.Add(_networkFrame);

            var body = new DockPanel();
            body.Children.Add(titleBar);
            body.Children.Add(boxes);

            Content = body;
        }

        // The box paints itself - white in the light schemes, the scheme's own paper in the dark
        // ones - and is NOT made transparent, so the text sits on paper rather than on the window's
        // grey. The same arrangement as the keyer's two rows.
        CwDecodedText MakeTextBox()
        {
            var box = new RichTextBox
            {
                FontSize = 18,
                FontFamily = new FontFamily("Consolas"),
                IsReadOnly = true,
                IsTabStop = false,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            // WHITE PAPER, DARK INK, WHATEVER THE COLOUR SCHEME IS DOING. The box used to take the
            // scheme's own colours, which made it grey. Decoded text is read for minutes at a time
            // while listening, and it is read best off paper; the keyer's two rows are white for the
            // same reason. The ink is fixed to match, because white paper with the scheme's light
            // text on it would be unreadable the moment a dark scheme was chosen.
            box.Background = Brushes.White;
            box.Foreground = PaperInk;

            var decoded = new CwDecodedText(box);
            decoded.CallsignChosen += call =>
            {
                var handler = CallsignChosen;
                if (handler == null) return;
                try { handler(call); }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
            };
            return decoded;
        }

        // A thin dark line round the paper, exactly as the keyer frames its send row and its record:
        // it is what makes the text look like something written down rather than something floating
        // on the window.
        static Border Paper(RichTextBox box)
        {
            var frame = new Border
            {
                BorderThickness = new Thickness(1),
                Margin = new Thickness(8, 4, 8, 8),
                Child = box
            };
            frame.SetResourceReference(Border.BorderBrushProperty, "MutedTextBrush");
            frame.SetBinding(Border.BackgroundProperty,
                new System.Windows.Data.Binding("Background") { Source = box });
            return frame;
        }

        static TextBlock Label(string text)
        {
            var label = new TextBlock
            {
                Text = text,
                FontSize = 14,
                Margin = new Thickness(10, 2, 0, 0)
            };
            label.SetResourceReference(ForegroundProperty, "MutedTextBrush");
            return label;
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

            // WHICH READER IS ON SHOW. Two keys in the keyer's own frame, so the pair reads as one
            // question with two answers rather than as two separate switches.
            _plainBtn = BarButton("Plain", "The arithmetic decoder - no network, and the one that has been on the air longest.");
            _networkBtn = BarButton("Net", "The neural network. Better on some signals, worse on others.");
            _bothBtn = BarButton("Both", "Both at once, one under the other, reading the same signal - the only fair way to see which suits your station.");
            _plainBtn.Click += (s, e) => ShowWhich(Reader.Plain);
            _networkBtn.Click += (s, e) => ShowWhich(Reader.Network);
            _bothBtn.Click += (s, e) => ShowWhich(Reader.Both);

            var whichPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            whichPanel.Children.Add(_plainBtn);
            whichPanel.Children.Add(_networkBtn);
            whichPanel.Children.Add(_bothBtn);

            var right = new StackPanel { Orientation = Orientation.Horizontal };
            DockPanel.SetDock(right, Dock.Right);
            right.Children.Add(_lamp);
            right.Children.Add(_speedText);
            right.Children.Add(GroupFrame(whichPanel));
            right.Children.Add(GroupFrame(clearPanel));
            right.Children.Add(closeBtn);

            var titleText = new TextBlock
            {
                Text = "CW Decoder",
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.Black,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(5, 0, 0, 0),
                ToolTip = "What the other station is sending. It listens whenever this window is open."
            };

            var icon = BuildIcon();
            DockPanel.SetDock(icon, Dock.Left);
            DockPanel.SetDock(titleText, Dock.Left);

            var bar = new DockPanel { LastChildFill = false };
            bar.Children.Add(right);
            bar.Children.Add(icon);
            bar.Children.Add(titleText);

            return new Border { Height = 32, Child = bar, Background = CwKeyBrush };
        }

        // A key on the bar, cut to the keyer's pattern.
        static Button BarButton(string text, string tip)
        {
            return new Button
            {
                Content = text,
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.Black,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(7, 0, 7, 0),
                Margin = new Thickness(2, 0, 0, 0),
                Height = 24,
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = tip
            };
        }

        void ShowWhich(Reader which)
        {
            if (which != Reader.Plain && !SharedNet.Loaded) return;

            try
            {
                Properties.Settings.Default.CwDecodeShow = (int)which;
                Properties.Settings.Default.Save();
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }

            FlushPendingText();
            PaintWhich();
            ApplyShowLayout();
        }

        void PaintWhich()
        {
            var which = Showing;
            PaintChoice(_plainBtn, which == Reader.Plain);
            PaintChoice(_networkBtn, which == Reader.Network);
            PaintChoice(_bothBtn, which == Reader.Both);

            if (!SharedNet.Loaded)
            {
                foreach (var b in new[] { _networkBtn, _bothBtn })
                {
                    if (b == null) continue;
                    b.IsEnabled = false;
                    b.Opacity = 0.45;
                    b.ToolTip = "The network file is missing, so only the plain decoder can run.";
                }
            }
        }

        // In Both, the second reader gets a box of its own under the first, each named. One box with
        // the two run together would be unreadable - and reading them side by side is the whole
        // point of Both: it is how the operator finds out which he trusts on HIS signals.
        void ApplyShowLayout()
        {
            bool both = Showing == Reader.Both;
            _networkFrame.Visibility = both ? Visibility.Visible : Visibility.Collapsed;
            _plainLabel.Visibility = both ? Visibility.Visible : Visibility.Collapsed;
        }

        static void PaintChoice(Button button, bool chosen)
        {
            if (button == null) return;
            button.Background = chosen ? Brushes.White : Brushes.Transparent;
            button.Opacity = chosen ? 1.0 : 0.65;
        }

        static Reader Showing
        {
            get
            {
                try
                {
                    var which = (Reader)Properties.Settings.Default.CwDecodeShow;
                    if (which != Reader.Plain && !SharedNet.Loaded) return Reader.Plain;
                    return which;
                }
                catch (Exception swallowed) { Log.Swallow(swallowed); return Reader.Plain; }
            }
        }

        // Read once for the life of the program: 180 KB, and a second window would only read the
        // same numbers again.
        static void LoadNetworkOnce()
        {
            if (_netTried) return;
            _netTried = true;

            try
            {
                string path = System.IO.Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "Data", CwNeuralNet.WeightsFileName);
                string error;
                if (!SharedNet.Load(path, out error)) Log.Swallow(new Exception(error));
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
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

        // A SIGNAL BECOMING MORSE: the spike of a received pulse, turning into dit dit dah. The same
        // drawing as the View menu's item, so the menu entry and the window it opens are plainly the
        // same thing. Black, like the keyer's straight key, because it sits on the pale cyan bar.
        static UIElement BuildIcon()
        {
            var canvas = new Canvas { Width = 24, Height = 24 };

            canvas.Children.Add(new Path
            {
                Data = Geometry.Parse("M 1,12 L 3.5,12 L 5,5.5 L 6.5,18.5 L 8,9.5 L 9.5,12 L 11,12"),
                Stroke = Brushes.Black,
                StrokeThickness = 1.7,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Fill = Brushes.Transparent
            });

            AddDit(canvas, 12.6);
            AddDit(canvas, 16.4);

            var dah = new Rectangle
            {
                Width = 3.4,
                Height = 2.8,
                RadiusX = 1.4,
                RadiusY = 1.4,
                Fill = Brushes.Black
            };
            Canvas.SetLeft(dah, 20.2);
            Canvas.SetTop(dah, 10.6);
            canvas.Children.Add(dah);

            return new Viewbox
            {
                Width = 24,
                Height = 24,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = canvas
            };
        }

        static void AddDit(Canvas canvas, double left)
        {
            var dit = new Ellipse { Width = 2.8, Height = 2.8, Fill = Brushes.Black };
            Canvas.SetLeft(dit, left);
            Canvas.SetTop(dit, 10.6);
            canvas.Children.Add(dit);
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
            decoder.Text += OnPlainText;
            _decoder = decoder;

            if (SharedNet.Loaded)
            {
                var neural = new CwNeuralDecoder(_recorder.ActualSampleRate, SharedNet);
                neural.Reset();
                neural.Text += OnNetworkText;
                _neural = neural;
            }

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
            if (decoder != null) decoder.Text -= OnPlainText;

            var neural = _neural;
            _neural = null;
            if (neural != null) neural.Text -= OnNetworkText;

            FlushPendingText();

            _shownLevel = 0;
            if (_lamp != null) _lamp.Fill = QuietBrush;
            if (_speedText != null) _speedText.Text = string.Empty;
        }

        void ClearEverything()
        {
            lock (_pendingGate) { _pendingPlain.Clear(); _pendingNetwork.Clear(); }
            _text.Clear();
            if (_networkText != null) _networkText.Clear();

            var decoder = _decoder;
            if (decoder != null) decoder.Reset();

            var neural = _neural;
            if (neural != null) neural.Reset();
        }

        void ShowTrouble(string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            _text.Append("\r\n" + message + "\r\n");
        }

        // On the capture thread. Hand the block straight to the decoder - it is a few thousand
        // multiplications, far less than the 100 ms of audio it represents.
        void OnSamples(short[] samples, int count)
        {
            var decoder = _decoder;
            if (decoder == null) return;

            try { decoder.Process(samples, count); }
            catch (Exception swallowed) { Log.Swallow(swallowed); }

            // BOTH READERS ARE FED, WHICHEVER IS ON SHOW, so changing the choice on the bar is
            // instant and the one that was hidden is not starting from cold.
            var neural = _neural;
            if (neural == null) return;

            try
            {
                // The network is told the note and the speed the plain decoder has measured. It
                // cannot work them out for itself: the spacing of what it is fed depends on the
                // speed, which is why the two run together rather than one instead of the other.
                if (decoder.SignalPresent) neural.Configure(decoder.ToneHz, decoder.Wpm);
                neural.Process(samples, count);
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // Also on the capture thread: collect, do not touch the screen. Whichever reader is not on
        // show is still running and still being fed - its letters are simply dropped here.
        void OnPlainText(string text)
        {
            if (Showing == Reader.Network) return;
            lock (_pendingGate) _pendingPlain.Append(text);
        }

        void OnNetworkText(string text)
        {
            if (Showing == Reader.Plain) return;
            lock (_pendingGate) _pendingNetwork.Append(text);
        }

        void ScreenTimer_Tick(object sender, EventArgs e)
        {
            FlushPendingText();
            UpdateLamp();
            UpdateSpeed();
        }

        void FlushPendingText()
        {
            string plain, network;
            lock (_pendingGate)
            {
                plain = _pendingPlain.ToString();
                network = _pendingNetwork.ToString();
                _pendingPlain.Clear();
                _pendingNetwork.Clear();
            }

            // In Plain and Network the one on show writes into the top box; in Both each has its
            // own. So the top box is the plain reader's, unless only the network is on show.
            if (Showing == Reader.Network) AppendTo(_text, network);
            else
            {
                AppendTo(_text, plain);
                if (Showing == Reader.Both) AppendTo(_networkText, network);
            }
        }

                static void AppendTo(CwDecodedText box, string text)
        {
            if (box == null || string.IsNullOrEmpty(text)) return;
            box.Append(text);
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
