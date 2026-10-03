using System;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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

        // THE NEW DECODER, ON TEST (see CwElementDecoder): judges whole dits and dahs. Fed always,
        // like the others, so switching to it is instant. In Both it is the one under the plain
        // decoder - comparing those two on the air is what it is here for.
        CwElementDecoder _element;

        CwDecodedText _text;
        TextBlock _speedText;
        Ellipse _lamp;
        Button _plainBtn, _networkBtn, _bothBtn, _newBtn;
        CwDecodedText _networkText;
        Border _networkFrame;
        TextBlock _plainLabel, _networkLabel;
        RowDefinition _networkRow;

        // Letters arrive on the capture thread, one or two at a time, and are collected here for the
        // screen timer to put on show in one go. A Dispatcher call per letter would be a thousand
        // hops a minute for something the eye cannot see happening that fast anyway.
        readonly StringBuilder _pendingPlain = new StringBuilder();
        readonly StringBuilder _pendingNetwork = new StringBuilder();
        readonly StringBuilder _pendingElement = new StringBuilder();
        readonly object _pendingGate = new object();

        /// <summary>Which reader's letters are shown.</summary>
        public enum Reader { Plain, Network, Both, New }

        // The lamp falls back gently instead of snapping to grey between characters. Without it it
        // flickers on CW - which is silence half the time - and cannot be read at all.
        double _shownLevel;

        public CwDecodeWindow()
        {
            Title = "CW Decoder";
            Width = 660;
            Height = 300 + WaterfallRowHeight;

            // WIDE ENOUGH FOR THE BAR, SHORT ENOUGH FOR ONE LINE. The floor on the height is the bar
            // plus a single row of text: an operator who wants a one-line ticker along the bottom of
            // his screen must be able to have one, and any taller floor than this would refuse him.
            // The width floor is the whole bar - title, lamp, speed, the four reader keys, Clear and
            // the close cross - measured at 618 px with the speed showing; at the old 380 the Clear key
            // and the close cross were pushed out of the window.
            MinWidth = 630;

            // The bar, plus the framed paper with one row of text in it. Anything taller than this
            // would refuse an operator who wants a one-line ticker along the edge of his screen.
            // The waterfall strip (see BuildWaterfall) is added on top of that floor, so the text row is
            // never squeezed out by it.
            MinHeight = 88 + WaterfallRowHeight;

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

            // PUT BACK WHERE HE LEFT IT - by the same helper as every other HolyLogger window
            // (2026-10-03). Its own restore set the corner at SourceInitialized and a top on his second
            // monitor (-217) did not hold: the window came up at top 26 and then saved that, so it never
            // found its way back; and a window left open when HolyLogger closed was never saved at all.
            // WindowBounds sets the corner before the window is shown, saves at closing AND at program
            // exit (SaveAllOpen), and keeps a restored window reachable. The place this window kept in
            // its own four settings is taken over once, the first time.
            try
            {
                var old = Properties.Settings.Default;
                if (!WindowBounds.HasSaved("CwDecode") && old.CwDecodeWindowWidth >= MinWidth && old.CwDecodeWindowHeight >= MinHeight
                    && !double.IsNaN(old.CwDecodeWindowLeft) && !double.IsNaN(old.CwDecodeWindowTop))
                    WindowBounds.SaveRect("CwDecode", new Rect(old.CwDecodeWindowLeft, old.CwDecodeWindowTop, old.CwDecodeWindowWidth, old.CwDecodeWindowHeight));
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
            WindowBounds.Attach(this, "CwDecode");

            Loaded += (s, e) => StartListening();

            // The waterfall moves with every screen refresh - see WaterfallFrame.
            Loaded += (s, e) => CompositionTarget.Rendering += WaterfallFrame;
            Closed += (s, e) => CompositionTarget.Rendering -= WaterfallFrame;
            Closing += OnClosing;
        }

        // ---- what is on screen ----

        void BuildContent()
        {
            _text = MakeTextBox();

            // WHAT THE WINDOW WORKS OUT, THE DECODER IS TOLD. The K, the BK, the prosign and the
            // pause-then-somebody-else are all judged where the text is laid out, because that is
            // where whole words can be seen. Until now the decoder never heard the answer and went
            // on using the old operator's speed on the new one - see ForgetTheOperator.
            _text.TurnChanged += () =>
            {
                var decoder = _decoder;
                if (decoder != null) decoder.ForgetTheOperator();
            };

            var frame = Paper(_text.Box);

            // The second reader's box, shown only in Both.
            _networkText = MakeTextBox();
            _networkFrame = Paper(_networkText.Box);
            _networkFrame.Visibility = Visibility.Collapsed;

            _plainLabel = Label("Plain");
            _networkLabel = Label("New (test)");
            _plainLabel.Visibility = Visibility.Collapsed;

            var titleBar = BuildTitleBar();
            DockPanel.SetDock(titleBar, Dock.Top);

            // A grid rather than a stack: in Both the two boxes share the room equally however the
            // window is dragged, which is what makes them comparable at a glance.
            var boxes = new Grid();
            boxes.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            boxes.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            boxes.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // the waterfall
            boxes.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _networkRow = new RowDefinition { Height = new GridLength(1, GridUnitType.Star) };
            boxes.RowDefinitions.Add(_networkRow);

            // THE WATERFALL BETWEEN THE TWO BOXES (his placing): under the top box always, so with one
            // reader it sits under its text, and in Both it divides Plain from New.
            var waterfall = BuildWaterfall();

            Grid.SetRow(_plainLabel, 0);
            Grid.SetRow(frame, 1);
            Grid.SetRow(waterfall, 2);
            Grid.SetRow(_networkLabel, 3);
            Grid.SetRow(_networkFrame, 4);

            boxes.Children.Add(_plainLabel);
            boxes.Children.Add(frame);
            boxes.Children.Add(waterfall);
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
            _newBtn = BarButton("New", "The new decoder, on test. Better on weak signals, still worse on some strong fast ones.");
            _bothBtn = BarButton("Both", "Plain and New at once, one under the other, reading the same signal - the only fair way to see which suits your station.");
            _plainBtn.Click += (s, e) => ShowWhich(Reader.Plain);
            _networkBtn.Click += (s, e) => ShowWhich(Reader.Network);
            _newBtn.Click += (s, e) => ShowWhich(Reader.New);
            _bothBtn.Click += (s, e) => ShowWhich(Reader.Both);

            var whichPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            whichPanel.Children.Add(_plainBtn);
            whichPanel.Children.Add(_networkBtn);
            whichPanel.Children.Add(_newBtn);
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
            if (which == Reader.Network && !SharedNet.Loaded) return;

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
            PaintChoice(_newBtn, which == Reader.New);

            if (!SharedNet.Loaded && _networkBtn != null)
            {
                _networkBtn.IsEnabled = false;
                _networkBtn.Opacity = 0.45;
                _networkBtn.ToolTip = "The network file is missing, so the network cannot run.";
            }
        }

        // In Both, the second reader gets a box of its own under the first, each named. One box with
        // the two run together would be unreadable - and reading them side by side is the whole
        // point of Both: it is how the operator finds out which he trusts on HIS signals.
        //
        // WITH ONE READER, THE ONE BOX TAKES THE WHOLE WINDOW. Hiding the second box is not enough
        // and this was got wrong at first: a collapsed thing in a row set to share the space still
        // has its share of the space kept for it, so the text sat in the top half of the window with
        // an empty half under it. The row itself has to be closed, not just the box in it.
        void ApplyShowLayout()
        {
            bool both = Showing == Reader.Both;

            _networkFrame.Visibility = both ? Visibility.Visible : Visibility.Collapsed;
            _networkLabel.Visibility = both ? Visibility.Visible : Visibility.Collapsed;
            _plainLabel.Visibility = both ? Visibility.Visible : Visibility.Collapsed;

            _networkRow.Height = both ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
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
                    if (which == Reader.Network && !SharedNet.Loaded) return Reader.Plain;
                    if (which < Reader.Plain || which > Reader.New) return Reader.Plain;
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
            decoder.LetterTimed += OnPlainLetterTimed;
            _decoder = decoder;

            if (SharedNet.Loaded)
            {
                var neural = new CwNeuralDecoder(_recorder.ActualSampleRate, SharedNet);
                neural.Reset();
                neural.Text += OnNetworkText;
                _neural = neural;
            }

            var element = new CwElementDecoder(_recorder.ActualSampleRate);
            element.Text += OnElementText;
            lock (_wfGate) _wfBase = _wfWritten;       // its sample count starts here
            element.LetterTimed += OnElementLetterTimed;
            _element = element;

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
            if (decoder != null) { decoder.Text -= OnPlainText; decoder.LetterTimed -= OnPlainLetterTimed; }

            var neural = _neural;
            _neural = null;
            if (neural != null) neural.Text -= OnNetworkText;

            var element = _element;
            _element = null;
            if (element != null) { element.Text -= OnElementText; element.LetterTimed -= OnElementLetterTimed; element.Dispose(); }

            FlushPendingText();

            _shownLevel = 0;
            if (_lamp != null) _lamp.Fill = QuietBrush;
            if (_speedText != null) _speedText.Text = string.Empty;
        }

        void ClearEverything()
        {
            lock (_pendingGate) { _pendingPlain.Clear(); _pendingNetwork.Clear(); _pendingElement.Clear(); }
            _text.Clear();
            if (_networkText != null) _networkText.Clear();

            var decoder = _decoder;
            if (decoder != null) decoder.Reset();

            var neural = _neural;
            if (neural != null) neural.Reset();

            var element = _element;
            if (element != null) element.Reset();
        }

        void ShowTrouble(string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            _text.Append("\r\n" + message + "\r\n");
        }

        // ---- the waterfall ----
        //
        // WHAT THE DECODERS HEAR, FOR THE EYE (his request). A strip across the window: time runs from
        // right to left - the newest sound at the right edge, the last six seconds across - and the
        // note up the strip, 300 Hz at the bottom to 1000 Hz at the top, so every dit and dah is a
        // short bright dash at its note and the station's letters can be read off it by eye. A dotted
        // green line marks the note the plain decoder is listening to.
        //
        // IT CANNOT SLOW THE DECODERS: the capture thread only copies the samples into a ring, and
        // the drawing - one small FFT per 10 ms of sound - runs on the screen timer.
        const int LetterRowHeight = 22;                // one line of letters under the strip
        const int WaterfallRowHeight = 64 + 2 * LetterRowHeight;   // the strip, its two letter lines, margins
        const int WfColumns = 600, WfRows = 56;        // six seconds at one column per 10 ms
        const double WfLowHz = 300, WfHighHz = 1000;
        readonly short[] _wfRing = new short[1 << 15];
        long _wfWritten, _wfRead;
        readonly object _wfGate = new object();
        WriteableBitmap _wfBitmap;
        int[] _wfPixels;
        double[] _wfRowFloor;
        static readonly int[] WfPalette = MakeWaterfallPalette();

        UIElement BuildWaterfall()
        {
            _wfBitmap = new WriteableBitmap(WfColumns, WfRows, 96, 96, PixelFormats.Bgr32, null);
            _wfPixels = new int[WfColumns * WfRows];
            for (int i = 0; i < _wfPixels.Length; i++) _wfPixels[i] = WfPalette[0];
            _wfBitmap.WritePixels(new Int32Rect(0, 0, WfColumns, WfRows), _wfPixels, WfColumns * 4, 0);

            var image = new Image { Source = _wfBitmap, Stretch = Stretch.Fill, Height = WfRows };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.LowQuality);
            _wfImage = image;

            // THE LETTERS UNDER THEIR OWN DITS AND DAHS (his idea): Plain's line, then New's, moving left
            // with the strip, so a missed or wrong letter can be seen against the marks it came from.
            _wfLetterCanvas = new Canvas
            {
                Height = 2 * LetterRowHeight,
                Background = new SolidColorBrush(Color.FromRgb(0, 0, 0)),
                ClipToBounds = true
            };
            var stack = new StackPanel();
            stack.Children.Add(image);
            stack.Children.Add(_wfLetterCanvas);

            int inner = WfRows + 2 * LetterRowHeight + 2;
            return new Border
            {
                Child = stack,
                Height = inner,
                Margin = new Thickness(0, (WaterfallRowHeight - inner) / 2, 0, (WaterfallRowHeight - inner) / 2),
                BorderThickness = new Thickness(1),
                BorderBrush = GroupEdgeBrush,
                ClipToBounds = true,          // the picture slides left between columns - see WaterfallFrame
                ToolTip = "What the decoders hear: time runs right to left, the note up (300 to 1000 Hz). The dotted green line is the note being read.\nUnder it, the letters each reader made of it: Plain in white, New in yellow."
            };
        }

        Image _wfImage;
        double _wfShift;                                // how far the picture has slid past the last column, in pixels
        Canvas _wfLetterCanvas;
        long _wfBase;                                   // waterfall sample count when the readers were made

        sealed class WfLetter { public string Text; public long Sample; public int Row; public TextBlock Block; }
        readonly System.Collections.Generic.List<WfLetter> _wfLetters = new System.Collections.Generic.List<WfLetter>();
        readonly System.Collections.Generic.List<WfLetter> _wfLetterQueue = new System.Collections.Generic.List<WfLetter>();

        // Each reader says where each letter began and ended: it is written under the middle of its marks,
        // so the two lines sit one over the other and a difference is seen at once.
        void OnPlainLetterTimed(string letter, long start, long end)
        {
            lock (_wfLetterQueue) _wfLetterQueue.Add(new WfLetter { Text = letter, Sample = _wfBase + (start + end) / 2, Row = 0 });
        }

        void OnElementLetterTimed(string letter, long start, long end)
        {
            lock (_wfLetterQueue) _wfLetterQueue.Add(new WfLetter { Text = letter, Sample = _wfBase + (start + end) / 2, Row = 1 });
        }

        void MoveWaterfallLetters(int rate)
        {
            if (_wfLetterCanvas == null || _wfImage == null) return;
            lock (_wfLetterQueue)
            {
                foreach (WfLetter l in _wfLetterQueue)
                {
                    l.Block = new TextBlock
                    {
                        Text = l.Text,
                        FontSize = 16,
                        FontWeight = FontWeights.Bold,
                        FontFamily = new FontFamily("Consolas"),
                        Foreground = l.Row == 0 ? Brushes.White : new SolidColorBrush(Color.FromRgb(255, 210, 0))
                    };
                    Canvas.SetTop(l.Block, l.Row * LetterRowHeight + 1);
                    _wfLetterCanvas.Children.Add(l.Block);
                    _wfLetters.Add(l);
                }
                _wfLetterQueue.Clear();
            }

            int hop = rate / 100, window = rate * 40 / 1000;
            double width = _wfImage.ActualWidth;
            long rightCentre = _wfRead - hop + window / 2;     // the sound under the newest column
            for (int i = _wfLetters.Count - 1; i >= 0; i--)
            {
                WfLetter l = _wfLetters[i];
                double column = WfColumns - 1 - (rightCentre - l.Sample) / (double)hop;
                double x = column * width / WfColumns - 5 - _wfShift;
                if (x < -20)
                {
                    _wfLetterCanvas.Children.Remove(l.Block);
                    _wfLetters.RemoveAt(i);
                    continue;
                }
                l.Block.Visibility = x > width ? Visibility.Hidden : Visibility.Visible;   // not drawn yet
                Canvas.SetLeft(l.Block, x);
            }
        }

        void FeedWaterfall(short[] samples, int count)
        {
            lock (_wfGate)
                for (int i = 0; i < count; i++) _wfRing[(int)(_wfWritten++ & (_wfRing.Length - 1))] = samples[i];
        }

        // SMOOTH, NOT IN JUMPS. The sound arrives in blocks of a tenth of a second, and drawn as it came
        // the strip jumped ten columns at a time - "aggressive steps", he said. So the strip is moved
        // at the pace of the clock, one column per 10 ms, on every screen refresh, running WfBehindMs
        // behind the sound so the next column is always there. If the sound card's clock and this one
        // drift apart, the strip catches up (or waits) a little at a time rather than jumping.
        const double WfBehindMs = 200;
        readonly System.Diagnostics.Stopwatch _wfClock = new System.Diagnostics.Stopwatch();
        long _wfShown;                                  // columns drawn
        long _wfOffset;                                 // columns the clock is moved by, to follow the sound card

        // TEMPORARY - TO FIND THE JUMPS HE SEES. Outside HolyLogger the strip moved within a pixel of
        // where it should on 1,168 frames of 1,169, so what jumps in HolyLogger must be its frames: this
        // writes every frame that came late, or took long, to %TEMP%\HolyLogger_cw_flow.tsv. Remove
        // once the cause is found.
        readonly System.Diagnostics.Stopwatch _flowClock = System.Diagnostics.Stopwatch.StartNew();
        readonly StringBuilder _flowLog = new StringBuilder();
        double _flowLast, _flowSaved;
        int _flowFrames, _flowLate;

        // TEMPORARY, WITH THE FLOW LOG: every piece of work on the window's thread that takes over 40 ms
        // is written to the same file ("slow" lines), named by the method that ran - for a timer, the
        // Tick handlers - so the jumps that remain with Live Scale off can be put to their cause.
        bool _probeOn;
        readonly System.Collections.Generic.Dictionary<DispatcherOperation, long> _probeStart = new System.Collections.Generic.Dictionary<DispatcherOperation, long>();

        void StartProbe()
        {
            _probeOn = true;
            var hooks = Dispatcher.Hooks;
            hooks.OperationStarted += (s, e) => _probeStart[e.Operation] = System.Diagnostics.Stopwatch.GetTimestamp();
            hooks.OperationAborted += (s, e) => _probeStart.Remove(e.Operation);
            hooks.OperationCompleted += (s, e) =>
            {
                long t0;
                if (!_probeStart.TryGetValue(e.Operation, out t0)) return;
                _probeStart.Remove(e.Operation);
                double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                if (ms > 40)
                    _flowLog.AppendFormat(System.Globalization.CultureInfo.InvariantCulture, "slow\t{0:F0}\t{1:F1}\t{2}\n",
                        _flowClock.Elapsed.TotalMilliseconds, ms, DescribeOperation(e.Operation));
            };
        }

        // Everything in every open window that has an animation running on it right now, by window,
        // type and name - an animation that never stops makes the one drawing engine all HolyLogger's
        // windows share redraw on every frame.
        static string AnimatingNow()
        {
            var found = new System.Collections.Generic.List<string>();
            try
            {
                foreach (Window w in Application.Current.Windows)
                {
                    if (!w.IsVisible) continue;
                    var stack = new System.Collections.Generic.Stack<DependencyObject>();
                    stack.Push(w);
                    while (stack.Count > 0 && found.Count < 12)
                    {
                        DependencyObject d = stack.Pop();
                        var ui = d as UIElement;
                        if (ui != null)
                        {
                            var rt = ui.RenderTransform as System.Windows.Media.Animation.Animatable;
                            bool animated = ui.HasAnimatedProperties || (rt != null && rt.HasAnimatedProperties)
                                || (ui.Effect != null && ui.Effect.HasAnimatedProperties);
                            if (animated)
                            {
                                var fe = ui as FrameworkElement;
                                found.Add(w.GetType().Name + "/" + ui.GetType().Name + (fe != null && !string.IsNullOrEmpty(fe.Name) ? ":" + fe.Name : "")
                                          + (ui.IsVisible ? "" : "(hidden)"));
                            }
                        }
                        if (d is Visual || d is System.Windows.Media.Media3D.Visual3D)
                            for (int i = VisualTreeHelper.GetChildrenCount(d) - 1; i >= 0; i--) stack.Push(VisualTreeHelper.GetChild(d, i));
                    }
                }
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
            return found.Count == 0 ? "nothing" : string.Join(", ", found);
        }

        static string DescribeOperation(DispatcherOperation op)
        {
            try
            {
                var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                var d = typeof(DispatcherOperation).GetField("_method", flags)?.GetValue(op) as Delegate;
                if (d == null) return op.Priority.ToString();
                string s = (d.Method.DeclaringType != null ? d.Method.DeclaringType.Name : "?") + "." + d.Method.Name;
                if (d.Target is DispatcherTimer timer)
                {
                    var tick = typeof(DispatcherTimer).GetField("Tick", flags)?.GetValue(timer) as Delegate;
                    if (tick != null)
                        foreach (Delegate h in tick.GetInvocationList())
                            s += " tick:" + (h.Method.DeclaringType != null ? h.Method.DeclaringType.Name : "?") + "." + h.Method.Name;
                }
                return s + " (" + op.Priority + ")";
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); return op.Priority.ToString(); }
        }

        void WaterfallFrame(object sender, EventArgs e)
        {
            if (!_probeOn) StartProbe();
            double flowStart = _flowClock.Elapsed.TotalMilliseconds;
            long flowShown = _wfShown, flowOffset = _wfOffset;
            WaterfallFrameInner();
            double flowEnd = _flowClock.Elapsed.TotalMilliseconds;
            try
            {
                var re = e as RenderingEventArgs;
                double gap = _flowLast > 0 ? flowStart - _flowLast : 0;
                _flowLast = flowStart;
                _flowFrames++;
                if (gap > 25 || flowEnd - flowStart > 8 || _wfOffset != flowOffset)
                {
                    _flowLate++;
                    _flowLog.AppendFormat(System.Globalization.CultureInfo.InvariantCulture,
                        "{0:F0}\t{1:F1}\t{2:F1}\t{3}\t{4}\t{5:F0}\n", flowStart, gap, flowEnd - flowStart,
                        _wfShown - flowShown, _wfOffset, re != null ? re.RenderingTime.TotalMilliseconds : 0);
                }
                if (flowStart - _flowSaved > 10000)
                {
                    _flowSaved = flowStart;
                    _flowLog.AppendFormat("# {0:F0} ms: {1} frames, {2} late or long\n", flowStart, _flowFrames, _flowLate);
                    _flowLog.AppendFormat("# tier {0}, windows {1}, animating: {2}\n", RenderCapability.Tier >> 16,
                        Application.Current.Windows.Count, AnimatingNow());
                    System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "HolyLogger_cw_flow.tsv"), _flowLog.ToString());
                    _flowLog.Clear();
                    _flowFrames = 0; _flowLate = 0;
                }
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        void WaterfallFrameInner()
        {
            try
            {
                int rate = _recorder.ActualSampleRate;
                if (rate <= 0) return;
                long written; lock (_wfGate) written = _wfWritten;
                if (written == 0) return;
                if (!_wfClock.IsRunning) { _wfClock.Start(); _wfShown = 0; _wfOffset = 0; }

                int hop = rate / 100, window = rate * 40 / 1000;
                long ready = _wfShown + Math.Max(0, (written - _wfRead - window) / hop);
                long due = (long)((_wfClock.Elapsed.TotalMilliseconds - WfBehindMs) / 10) + _wfOffset;
                if (ready - due > 30) _wfOffset++;                        // sound ahead: one column extra now and then
                else if (due - ready > 30) _wfOffset -= due - ready - 10;  // sound late: wait for it without a jump later
                due = (long)((_wfClock.Elapsed.TotalMilliseconds - WfBehindMs) / 10) + _wfOffset;

                long take = Math.Min(due, ready) - _wfShown;
                if (take > 0) _wfShown += DrawWaterfall((int)Math.Min(take, 6));

                // BETWEEN COLUMNS, A SLIDE. Whole columns at the screen's 60 a second came one, two, one,
                // two - small jumps he could see ("not smooth enough"). The picture is also moved left
                // by the part of a column that is due by now, so it glides; the next column then lands
                // exactly where the slide has got to.
                double exact = (_wfClock.Elapsed.TotalMilliseconds - WfBehindMs) / 10 + _wfOffset;
                double part = Math.Max(0, Math.Min(1, exact - _wfShown));
                _wfShift = _wfImage != null ? part * _wfImage.ActualWidth / WfColumns : 0;
                if (_wfImage != null)
                {
                    if (!(_wfImage.RenderTransform is TranslateTransform)) _wfImage.RenderTransform = new TranslateTransform();
                    ((TranslateTransform)_wfImage.RenderTransform).X = -_wfShift;
                }
                MoveWaterfallLetters(rate);
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        int DrawWaterfall(int columns)
        {
            int rate = _recorder.ActualSampleRate;
            if (_wfBitmap == null || rate <= 0) return 0;
            int window = rate * 40 / 1000, hop = rate / 100;
            int nfft = 1; while (nfft < window * 3 / 2) nfft <<= 1;

            double note = 0;
            var heard = _decoder;
            if (heard != null && heard.SignalPresent) note = heard.ToneHz;

            var re = new double[nfft]; var im = new double[nfft];
            var column = new int[WfRows];
            int drawn = 0;
            while (true)
            {
                lock (_wfGate)
                {
                    // Fallen far behind (the window was hidden, the machine busy): skip to the present.
                    if (_wfWritten - _wfRead > _wfRing.Length / 2) _wfRead = _wfWritten - window - hop * 20;
                    if (_wfWritten - _wfRead < window) break;
                    for (int i = 0; i < nfft; i++)
                    {
                        // Blackman-Harris, not Hann: a strong station leaked into every row of the strip
                        // through a Hann window's sidelobes, 31 dB down; these are 92 dB down.
                        double a = 2 * Math.PI * i / (window - 1);
                        double shape = 0.35875 - 0.48829 * Math.Cos(a) + 0.14128 * Math.Cos(2 * a) - 0.01168 * Math.Cos(3 * a);
                        re[i] = i < window ? _wfRing[(int)((_wfRead + i) & (_wfRing.Length - 1))] * shape : 0;
                        im[i] = 0;
                    }
                    _wfRead += hop;
                }

                WaterfallFft(re, im);
                double binHz = (double)rate / nfft;
                var db = new double[WfRows];
                for (int y = 0; y < WfRows; y++)
                {
                    double f = WfHighHz - (WfHighHz - WfLowHz) * y / (WfRows - 1);
                    double b = f / binHz; int b0 = (int)b; double w = b - b0;
                    double m0 = Math.Sqrt(re[b0] * re[b0] + im[b0] * im[b0]);
                    double m1 = Math.Sqrt(re[b0 + 1] * re[b0 + 1] + im[b0 + 1] * im[b0 + 1]);
                    db[y] = 20 * Math.Log10(m0 + (m1 - m0) * w + 1);
                }

                // EACH NOTE ITS OWN QUIET LEVEL. One level for the whole strip made the noise inside the
                // radio's CW filter bright yellow and the station hardly brighter (his screenshot): the
                // dark stretch outside the filter pulled the level down. Each row's level drops at once
                // to anything quieter and creeps up slowly, so it settles on that note's noise, and only
                // a station stands out above it.
                if (_wfRowFloor == null) _wfRowFloor = (double[])db.Clone();
                for (int y = 0; y < WfRows; y++)
                    _wfRowFloor[y] += (db[y] < _wfRowFloor[y] ? 0.05 : 0.002) * (db[y] - _wfRowFloor[y]);

                int noteRow = note > 0 ? (int)Math.Round((WfHighHz - note) / (WfHighHz - WfLowHz) * (WfRows - 1)) : -1;
                bool dot = (_wfRead / hop) % 3 == 0;
                for (int y = 0; y < WfRows; y++)
                {
                    double v = (db[y] - _wfRowFloor[y] - 6) / 30;   // tried on his YT3T recording, strong and weak
                    int shade = (int)(Math.Max(0, Math.Min(1, v)) * 255);
                    column[y] = y == noteRow && dot ? 0x40E040 : WfPalette[shade];
                }

                // Everything one column to the left, the new column at the right edge.
                for (int y = 0; y < WfRows; y++)
                {
                    Array.Copy(_wfPixels, y * WfColumns + 1, _wfPixels, y * WfColumns, WfColumns - 1);
                    _wfPixels[y * WfColumns + WfColumns - 1] = column[y];
                }
                if (++drawn >= columns) break;
            }
            if (drawn > 0) _wfBitmap.WritePixels(new Int32Rect(0, 0, WfColumns, WfRows), _wfPixels, WfColumns * 4, 0);
            return drawn;
        }

        // Black for silence, dark blue for noise, bright yellow for a station, white for the loudest -
        // the noise kept dim so the dits and dahs are what the eye finds.
        static int[] MakeWaterfallPalette()
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

        static void WaterfallFft(double[] re, double[] im)
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

        // On the capture thread. Hand the block straight to the decoder - it is a few thousand
        // multiplications, far less than the 100 ms of audio it represents.
        void OnSamples(short[] samples, int count)
        {
            var decoder = _decoder;
            if (decoder == null) return;

            try { decoder.Process(samples, count); }
            catch (Exception swallowed) { Log.Swallow(swallowed); }

            // A copy for the waterfall, drawn on the screen timer - nothing is worked out here.
            FeedWaterfall(samples, count);

            // Only copied here; the reading itself runs on the new decoder's own thread.
            var element = _element;
            if (element != null)
            {
                try { element.Process(samples, count); }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
            }

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
            var showing = Showing;
            if (showing == Reader.Network || showing == Reader.New) return;
            lock (_pendingGate) _pendingPlain.Append(text);
        }

        void OnNetworkText(string text)
        {
            if (Showing != Reader.Network) return;
            lock (_pendingGate) _pendingNetwork.Append(text);
        }

        // On the new decoder's own thread.
        void OnElementText(string text)
        {
            var showing = Showing;
            if (showing != Reader.New && showing != Reader.Both) return;
            lock (_pendingGate) _pendingElement.Append(text);
        }

        void ScreenTimer_Tick(object sender, EventArgs e)
        {

            FlushPendingText();
            UpdateLamp();
            UpdateSpeed();
        }

        void FlushPendingText()
        {
            string plain, network, element;
            lock (_pendingGate)
            {
                plain = _pendingPlain.ToString();
                network = _pendingNetwork.ToString();
                element = _pendingElement.ToString();
                _pendingPlain.Clear();
                _pendingNetwork.Clear();
                _pendingElement.Clear();
            }

            // The readers are told the note and the speed before they are given the letters: it is
            // how they tell a new station answering from the same one carrying on - see CwDecodedText.
            var heard = _decoder;
            if (heard != null)
            {
                double note = heard.SignalPresent ? heard.ToneHz : 0;
                double speed = heard.SignalPresent ? heard.Wpm : 0;
                _text.ToneHz = note; _text.Wpm = speed;
                if (_networkText != null) { _networkText.ToneHz = note; _networkText.Wpm = speed; }
            }

            // With one reader on show it writes into the top box; in Both the plain decoder has the
            // top box and the new one the box under it.
            var showing = Showing;
            if (showing == Reader.Network) AppendTo(_text, network);
            else if (showing == Reader.New) AppendTo(_text, element);
            else
            {
                AppendTo(_text, plain);
                if (showing == Reader.Both) AppendTo(_networkText, element);
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
            _recorder.Failed -= OnRecorderFailed;
            _recorder.Samples -= OnSamples;
            InputDeviceChanged -= OnInputDeviceChanged;
            StopListening();
            try { _recorder.Dispose(); }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }
    }
}
