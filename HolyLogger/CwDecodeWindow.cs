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
    /// BUILT AS THE KEYER'S OPPOSITE NUMBER. It wears the keyer's pale cyan
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

        /// <summary>Set by the main window: moves the radio so a note heard offHz too high comes down by
        /// offHz. Returns null when done, else a short reason. Null when there is no radio to move.</summary>
        public Func<double, string> ShiftRadio;
        /// <summary>Set by the main window: the last move went the wrong way on this radio.</summary>
        public Action WentWrongWay;

        /// <summary>Set by the main window: open Options on the page with the decoder's settings.</summary>
        public Action OpenSettings;

        /// <summary>The CW pitch was changed in Options: the waterfall's mark moves to it.</summary>
        public static event Action PitchChanged;
        public const int LowestPitchHz = 300, HighestPitchHz = 1000;     // the waterfall's own range

        internal static void RaisePitchChanged()
        {
            var handler = PitchChanged;
            if (handler == null) return;
            try { handler(); }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

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
        const bool RunNetwork = false;
        static bool _netTried;
        CwNeuralDecoder _neural;

        // THE NEW DECODER, ON TEST (see CwElementDecoder): judges whole dits and dahs. Fed always,
        // like the others, so switching to it is instant. In Both it is the one under the plain
        // decoder - comparing those two on the air is what it is here for.
        CwElementDecoder _element;

        // OUR OWN NETWORK, ON TEST (see CwDeepDecoder): reads 15 s at a time, prints a few seconds behind the sound.
        // Only there when Data\CwDeep.bin is; in Both it gets a third box under Plain and New.
        CwDeepDecoder _deep;
        CwDecodedText _deepText;
        Border _deepFrame;
        TextBlock _deepLabel;
        RowDefinition _deepRow;
        Button _deepBtn;
        readonly StringBuilder _pendingDeep = new StringBuilder();

        // BOTH SHOWS PLAIN AND NET ON ONE TIME LINE (see CwTimeLine): each letter in the column for when
        // it was sent. Plain's printed letters get their times from its LetterTimed, matched in order -
        // a timed letter that was never printed (held back as noise, then dropped) is skipped over.
        CwTimeLine _timeLine;

        // MIX (see CwMixText): Plain at once, then each letter confirmed (bold) or corrected by the network.
        CwMixText _mix;
        Border _mixFrame;
        Button _mixBtn;
        readonly System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<double, double>> _pendingCovered = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<double, double>>();
        Border _mainFrame, _timePlainFrame, _timeNetFrame;
        int _plainRate;
        readonly System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, double>> _pendingPlainTimed = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, double>>();
        readonly System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, double>> _pendingNetTimed = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, double>>();

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
        public enum Reader { Plain, Network, Both, New, Deep, Mix }

        // The lamp falls back gently instead of snapping to grey between characters. Without it it
        // flickers on CW - which is silence half the time - and cannot be read at all.
        double _shownLevel;

        public CwDecodeWindow()
        {
            Title = "CW Decoder";
            Width = 740 + SpectrumPanelWidth;
            Height = 300 + WaterfallRowHeight;

            // WIDE ENOUGH FOR THE BAR, SHORT ENOUGH FOR ONE LINE. The floor on the height is the bar
            // plus a single row of text: an operator who wants a one-line ticker along the bottom of
            // his screen must be able to have one, and any taller floor than this would refuse him.
            // The width floor is the whole bar - title, lamp, speed, the four reader keys, Tune, Clear
            // and the close cross. Measured 2026-10-17 (the Bar harness) with the widest speed,
            // (see NormalMinWidth: the bar with no reader buttons since 2026-10-21; developer mode widens it)
            MinWidth = NormalMinWidth;

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
            // A blue edge all round (2026-10-17, his request): with no OS frame the window melted into
            // the main window behind it.
            BorderBrush = Frozen(Color.FromRgb(0x0D, 0x47, 0xA1));
            BorderThickness = new Thickness(3);
            System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome
            {
                CaptionHeight = 32,
                CornerRadius = new CornerRadius(0),
                GlassFrameThickness = new Thickness(0),
                ResizeBorderThickness = new Thickness(6),
                UseAeroCaptionButtons = false
            });

            if (RunNetwork) LoadNetworkOnce();
            BuildContent();
            PaintWhich();
            ApplyShowLayout();

            _recorder.Failed += OnRecorderFailed;
            _recorder.Samples += OnSamples;
            InputDeviceChanged += OnInputDeviceChanged;
            PitchChanged += OnPitchChanged;

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

            // The waterfall runs on its own thread - see CwWaterfallStrip. It is let go while the window is
            // still whole (Closing), not after (Closed): see CwWaterfallStrip.Close.
            Closing += (s, e) => { if (!e.Cancel && _strip != null) _strip.Close(); };
            Closed += (s, e) => { if (_strip != null) _strip.Close(); };
            Closing += OnClosing;
            PreviewKeyDown += (s, e) =>
            {
                var mods = System.Windows.Input.Keyboard.Modifiers;
                if (e.Key == System.Windows.Input.Key.D && mods == (System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift))
                {
                    ToggleDeveloperMode();
                    e.Handled = true;
                }
            };
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
            _mainFrame = frame;

            _timeLine = new CwTimeLine();
            _timePlainFrame = Paper(_timeLine.PlainBox);
            _timePlainFrame.Visibility = Visibility.Collapsed;
            _timeNetFrame = Paper(_timeLine.NetBox);
            _timeNetFrame.Visibility = Visibility.Collapsed;

            _mix = new CwMixText();
            _mix.CallsignChosen += call =>
            {
                var handler = CallsignChosen;
                if (handler == null) return;
                try { handler(call); }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
            };
            _mixFrame = Paper(_mix.Box);
            ApplyMixHide();
            _mixFrame.Visibility = Visibility.Collapsed;

            // The second reader's box, shown only in Both.
            _networkText = MakeTextBox();
            _networkFrame = Paper(_networkText.Box);
            _networkFrame.Visibility = Visibility.Collapsed;

            _plainLabel = Label("Plain");
            _networkLabel = Label("New (test)");
            _plainLabel.Visibility = Visibility.Collapsed;

            _deepText = MakeTextBox();
            _deepFrame = Paper(_deepText.Box);
            _deepFrame.Visibility = Visibility.Collapsed;
            _deepLabel = Label("Net (test) - a few seconds behind");
            _deepLabel.Visibility = Visibility.Collapsed;

            var titleBar = BuildTitleBar();
            DockPanel.SetDock(titleBar, Dock.Top);

            // A grid rather than a stack: in Both the two boxes share the room equally however the
            // window is dragged, which is what makes them comparable at a glance.
            var boxes = new Grid();
            boxes.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // the waterfall
            boxes.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            boxes.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            boxes.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _networkRow = new RowDefinition { Height = new GridLength(1, GridUnitType.Star) };
            boxes.RowDefinitions.Add(_networkRow);
            boxes.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _deepRow = new RowDefinition { Height = new GridLength(0) };
            boxes.RowDefinitions.Add(_deepRow);

            // THE WATERFALL ON TOP, THE TEXT UNDER IT (his placing, 2026-10-03; it sat between the two
            // boxes before): the sound first, then what was read from it.
            var waterfall = BuildWaterfall();

            Grid.SetRow(waterfall, 0);
            Grid.SetRow(_plainLabel, 1);
            Grid.SetRow(frame, 2);
            Grid.SetRow(_networkLabel, 3);
            Grid.SetRow(_networkFrame, 4);
            Grid.SetRow(_deepLabel, 5);
            Grid.SetRow(_deepFrame, 6);
            Grid.SetRow(_timePlainFrame, 2);
            Grid.SetRow(_timeNetFrame, 6);
            Grid.SetRow(_mixFrame, 2);

            boxes.Children.Add(_plainLabel);
            boxes.Children.Add(frame);
            boxes.Children.Add(waterfall);
            boxes.Children.Add(_networkLabel);
            boxes.Children.Add(_networkFrame);
            boxes.Children.Add(_deepLabel);
            boxes.Children.Add(_deepFrame);
            boxes.Children.Add(_timePlainFrame);
            boxes.Children.Add(_timeNetFrame);
            boxes.Children.Add(_mixFrame);

            // THE FILTER WATERFALL DOWN THE RIGHT SIDE, the whole height under the bar (his design, 2026-11-22).
            var spectrum = BuildSpectrum();
            DockPanel.SetDock(spectrum, Dock.Right);

            var body = new DockPanel();
            body.Children.Add(titleBar);
            body.Children.Add(spectrum);
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
            CwStripes.Apply(box.Document);
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
        static Border Paper(Control box)
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

            // NO TUNE KEY ANY MORE (2026-11-22, his decision). It moved the station being read to his pitch; a click on
            // the filter waterfall does that for the station HE chooses, and Auto does it for one near his pitch. Its
            // check that the radio really moved the right way went with them (CheckTheMove).

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
            _networkBtn = RunNetwork ? BarButton("Net", "The neural network. Better on some signals, worse on others.") : null;
            // NEW IS NO LONGER OFFERED (2026-10-17, his decision): our network reads better, so the choice is
            // Plain, Net, or the two at once. CwElementDecoder stays in the source; it is just not run.
            // PLAIN OR HYBRID ONLY (2026-10-20, his decision): the network alone prints seconds behind the
            // sound, and an operator waiting for text stops trusting it; Both was a tool for comparing the
            // two readers, not for operating. So the bar offers Plain and Hybrid (what was called Mix:
            // Plain at once, corrected by the network). Net and Both stay in the code for testing - a saved
            // choice of either opens in Hybrid (see Showing).
            //
            // HYBRID ONLY (2026-10-21, his decision): Hybrid is better than Plain on everything measured and
            // still shows Plain's letters at once, so there is no choice to make and none on the bar - the
            // decoder always runs Hybrid, or Plain if the network file is missing or will not load. The
            // buttons are kept for DEVELOPER MODE: Ctrl+Shift+D in this window shows Plain, Net, Both and
            // Hybrid, for trying out an idea; pressed again they go. Not remembered - every start is Hybrid.
            _mixBtn = CwDeepDecoder.Available ? BarButton("Hybrid", "Plain at once; a few seconds later each letter the network agrees with turns bold, and one it reads differently is corrected (also bold).") : null;
            _deepBtn = CwDeepDecoder.Available ? BarButton("Net", "The network alone (developer mode).") : null;
            _bothBtn = CwDeepDecoder.Available ? BarButton("Both", "Plain and the network one above the other (developer mode).") : null;
            _plainBtn.Click += (s, e) => ShowWhich(Reader.Plain);
            if (_networkBtn != null) _networkBtn.Click += (s, e) => ShowWhich(Reader.Network);
            if (_mixBtn != null) _mixBtn.Click += (s, e) => ShowWhich(Reader.Mix);
            if (_deepBtn != null) _deepBtn.Click += (s, e) => ShowWhich(Reader.Deep);
            if (_bothBtn != null) _bothBtn.Click += (s, e) => ShowWhich(Reader.Both);

            var whichPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            whichPanel.Children.Add(_plainBtn);
            if (_networkBtn != null) whichPanel.Children.Add(_networkBtn);
            if (_deepBtn != null) whichPanel.Children.Add(_deepBtn);
            if (_bothBtn != null) whichPanel.Children.Add(_bothBtn);
            if (_mixBtn != null) whichPanel.Children.Add(_mixBtn);

            var right = new StackPanel { Orientation = Orientation.Horizontal };
            DockPanel.SetDock(right, Dock.Right);
            right.Children.Add(_lamp);
            right.Children.Add(_speedText);
            _whichFrame = GroupFrame(whichPanel);
            _whichFrame.Visibility = _devMode ? Visibility.Visible : Visibility.Collapsed;
            right.Children.Add(_whichFrame);
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

            // THE GEAR (his request, 2026-10-20): the decoder's settings - pitch, hiding the grey letters,
            // the input device - are in Options > General, and Tools > Options > General was too far away.
            // It opens Options already on that page. The same gear as the cluster's and the radio panel's:
            // the Segoe MDL2 settings glyph in a framed 26x26 square, the same hover and pressed blues.
            var gearBtn = new Button
            {
                Content = "\uE713",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 16,
                Width = 26,
                Height = 26,
                Padding = new Thickness(0),
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brushes.Black,
                Background = Brushes.White,
                BorderBrush = GroupEdgeBrush,
                BorderThickness = new Thickness(1),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = "CW decoder settings",
                Style = GearStyle(),
                UseLayoutRounding = true,
                SnapsToDevicePixels = true
            };
            System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(gearBtn, true);
            gearBtn.Click += (s, e) => { var open = OpenSettings; if (open != null) open(); };
            DockPanel.SetDock(gearBtn, Dock.Left);

            // AUTO (his request, 2026-11-22): after the gear, the same on/off switch as RTTY's Snap. On, a station heard
            // steadily near his pitch is moved onto it by itself - see AutoSnap. Off unless he turns it on: it moves
            // the radio, and so where he transmits.
            const string autoTip = "On: a station heard steadily within 50 Hz of your pitch is moved onto it by itself (the red line in the waterfall).\nOff: only a click on the waterfall moves the radio.";
            try { _autoSnap = Properties.Settings.Default.CwAutoSnap; }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
            var autoSwitch = new System.Windows.Controls.Primitives.ToggleButton
            {
                IsChecked = _autoSnap,
                VerticalAlignment = VerticalAlignment.Center,
                Style = Application.Current.Resources["ToggleSwitchStyle"] as Style,
                ToolTip = autoTip
            };
            System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(autoSwitch, true);
            autoSwitch.Click += (s, e) =>
            {
                _autoSnap = autoSwitch.IsChecked == true;
                _autoSince = DateTime.MinValue;
                try
                {
                    Properties.Settings.Default.CwAutoSnap = _autoSnap;
                    SettingsFlush.RequestSave();
                }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
            };
            var autoLabel = new TextBlock
            {
                Text = "Auto tune",   // his name for it, 2026-11-22
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.Black,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
                ToolTip = autoTip
            };
            System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(autoLabel, true);
            var autoPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            autoPanel.Children.Add(autoLabel);
            autoPanel.Children.Add(autoSwitch);
            DockPanel.SetDock(autoPanel, Dock.Left);

            var bar = new DockPanel { LastChildFill = false };
            bar.Children.Add(right);
            bar.Children.Add(icon);
            bar.Children.Add(titleText);
            bar.Children.Add(gearBtn);
            bar.Children.Add(autoPanel);

            return new Border { Height = 32, Child = bar, Background = CwKeyBrush };
        }

        internal static Style GearStyle()
        {
            var outer = new FrameworkElementFactory(typeof(Border));
            outer.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            outer.SetValue(Border.PaddingProperty, new Thickness(1));
            var face = new FrameworkElementFactory(typeof(Border));
            face.Name = "face";
            face.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            face.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            face.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
            var text = new FrameworkElementFactory(typeof(ContentPresenter));
            text.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            text.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            face.AppendChild(text);
            outer.AppendChild(face);
            var template = new ControlTemplate(typeof(Button)) { VisualTree = outer };
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty, Frozen(Color.FromRgb(0xA8, 0xD8, 0xFF)), "face"));
            template.Triggers.Add(hover);
            var pressed = new Trigger { Property = System.Windows.Controls.Primitives.ButtonBase.IsPressedProperty, Value = true };
            pressed.Setters.Add(new Setter(Border.BackgroundProperty, Frozen(Color.FromRgb(0x7D, 0xC0, 0xF5)), "face"));
            template.Triggers.Add(pressed);
            var style = new Style(typeof(Button));
            style.Setters.Add(new Setter(Control.TemplateProperty, template));
            return style;
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
            if (which == Reader.Network && (!RunNetwork || !SharedNet.Loaded)) return;
            if ((which == Reader.Deep || which == Reader.Mix) && !CwDeepDecoder.Available) return;

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
            PaintChoice(_deepBtn, which == Reader.Deep);
            PaintChoice(_mixBtn, which == Reader.Mix);

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

            // New's box is never shown now (see the bar buttons); in Both the Net box is the second one.
            _networkFrame.Visibility = Visibility.Collapsed;
            _networkLabel.Visibility = Visibility.Collapsed;
            _plainLabel.Visibility = both ? Visibility.Visible : Visibility.Collapsed;

            _networkRow.Height = new GridLength(0);
            bool deepBox = both && CwDeepDecoder.Available;
            _deepLabel.Visibility = deepBox ? Visibility.Visible : Visibility.Collapsed;
            _deepRow.Height = deepBox ? new GridLength(1, GridUnitType.Star) : new GridLength(0);

            // In Both the two time-lined boxes stand where the ordinary ones are (which go on being
            // written, unseen, so Plain or Net alone is up to date the moment it is chosen).
            bool mix = Showing == Reader.Mix;
            _mainFrame.Visibility = deepBox || mix ? Visibility.Collapsed : Visibility.Visible;
            _mixFrame.Visibility = mix ? Visibility.Visible : Visibility.Collapsed;
            if (mix) _mix.Show();
            _deepFrame.Visibility = Visibility.Collapsed;
            _timePlainFrame.Visibility = deepBox ? Visibility.Visible : Visibility.Collapsed;
            _timeNetFrame.Visibility = deepBox ? Visibility.Visible : Visibility.Collapsed;
            if (deepBox) _timeLine.Show();
            SizeLetterLines(false);     // only Plain puts letters under the waterfall now
        }

        static void PaintChoice(Button button, bool chosen)
        {
            if (button == null) return;
            button.Background = chosen ? Brushes.White : Brushes.Transparent;
            button.Opacity = chosen ? 1.0 : 0.65;
        }

        // Developer mode (Ctrl+Shift+D): the reader buttons are on the bar and the choice is the saved one.
        static bool _devMode;
        // Measured 2026-10-21 (Bar harness, widest speed "38 WPM   1000 Hz"): the bar needs 503 px with no reader
        // buttons and 739 with the four developer ones; plus the 3 px blue edge each side and a little air.
        // Since the filter waterfall (2026-11-22): its panel plus room for about 25 letters of text beside it.
        // Auto (2026-11-22) took the bar to 566 px (harness): the developer buttons add 236 to that.
        const double NormalMinWidth = 600, DevMinWidth = 820;
        UIElement _whichFrame;

        void ToggleDeveloperMode()
        {
            _devMode = !_devMode;
            if (_whichFrame != null) _whichFrame.Visibility = _devMode ? Visibility.Visible : Visibility.Collapsed;
            MinWidth = _devMode ? DevMinWidth : NormalMinWidth;
            FlushPendingText();
            PaintWhich();
            ApplyShowLayout();
        }

        static Reader Showing
        {
            get
            {
                try
                {
                    if (!_devMode) return CwDeepDecoder.Available ? Reader.Mix : Reader.Plain;
                    var which = (Reader)Properties.Settings.Default.CwDecodeShow;
                    if (which == Reader.Network && (!RunNetwork || !SharedNet.Loaded)) return Reader.Plain;
                    if ((which == Reader.Deep || which == Reader.Mix || which == Reader.Both) && !CwDeepDecoder.Available) return Reader.Plain;
                    if (which == Reader.New) return Reader.Plain;          // no longer offered
                    if (which < Reader.Plain || which > Reader.Mix) return Reader.Plain;
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
            decoder.WordGapTimed += OnPlainWordGap;
            _plainRate = _recorder.ActualSampleRate;
            _readerSeconds = 0;      // the new reader's clock starts here, like Plain's letter times
            _decoder = decoder;

            // NET IS NO LONGER RUN (2026-10-03, his decision). Verified correct, but trained only on clean
            // generated CW it read real signals no better than Plain, and it cost the computer all the
            // time it ran in the background. The code stays; RunNetwork brings it back.
            if (RunNetwork && SharedNet.Loaded)
            {
                var neural = new CwNeuralDecoder(_recorder.ActualSampleRate, SharedNet);
                neural.Reset();
                neural.Text += OnNetworkText;
                _neural = neural;
            }

            _wfBase = _strip != null ? _strip.Written : 0;   // the readers' sample count starts here

            if (CwDeepDecoder.Available)
            {
                var deep = new CwDeepDecoder(_recorder.ActualSampleRate);
                try { deep.PitchHz = Properties.Settings.Default.CwPitchHz; }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
                deep.Text += OnDeepText;
                deep.LetterTimed += OnDeepLetterTimed;
                deep.Covered += OnDeepCovered;
                _deep = deep;
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
            if (decoder != null) { decoder.Text -= OnPlainText; decoder.LetterTimed -= OnPlainLetterTimed; decoder.WordGapTimed -= OnPlainWordGap; }

            var neural = _neural;
            _neural = null;
            if (neural != null) neural.Text -= OnNetworkText;

            var element = _element;
            _element = null;
            if (element != null) { element.Text -= OnElementText; element.LetterTimed -= OnElementLetterTimed; element.Dispose(); }

            var deep = _deep;
            _deep = null;
            if (deep != null) { deep.Text -= OnDeepText; deep.LetterTimed -= OnDeepLetterTimed; deep.Covered -= OnDeepCovered; deep.Dispose(); }

            FlushPendingText();

            _shownLevel = 0;
            if (_lamp != null) _lamp.Fill = QuietBrush;
            if (_speedText != null) _speedText.Text = string.Empty;
        }

        void ClearEverything()
        {
            lock (_pendingGate) { _pendingPlain.Clear(); _pendingNetwork.Clear(); _pendingElement.Clear(); _pendingDeep.Clear(); _pendingPlainTimed.Clear(); _pendingNetTimed.Clear(); _pendingCovered.Clear(); _pendingSent.Clear(); }
            _timeLine.Clear();
            _timeLine.Show();
            _mix.Clear();
            _text.Clear();
            if (_networkText != null) _networkText.Clear();
            if (_deepText != null) _deepText.Clear();
            var deepReader = _deep;
            if (deepReader != null) deepReader.Reset();

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
        // IT CANNOT SLOW THE DECODERS, AND NOTHING CAN SLOW IT: the capture thread only copies the samples
        // into a ring, and the drawing - one small FFT per 10 ms of sound - runs on the strip's own thread
        // (CwWaterfallStrip, at the end of this file).
        const int LetterRowHeight = 22;                // one line of letters under the strip
        const int WaterfallRowHeight = 64 + 2 * LetterRowHeight;   // the strip, its two letter lines, margins
        CwWaterfallStrip _strip;                     // the strip itself, drawn on its own thread - see CwWaterfallStrip

        UIElement BuildWaterfall()
        {
            _strip = new CwWaterfallStrip(KeyAt, TxLettersBetween);
            _strip.Element.Height = CwWaterfallStrip.Rows + LetterRowHeight;

            // HIS PITCH MARKED (his request, 2026-10-17; the pitch is set in Options > General, 700 Hz unless
            // changed): a short bright line at each edge of the strip, laid over the picture (not drawn into
            // it, so it does not slide away with the sound).
            var picture = new Grid();
            picture.Children.Add(_strip.Element);
            foreach (var side in new[] { HorizontalAlignment.Left, HorizontalAlignment.Right })
            {
                var mark = new Rectangle
                {
                    Width = 22,
                    Height = 3,
                    Fill = MarkBrush,
                    HorizontalAlignment = side,
                    VerticalAlignment = VerticalAlignment.Top,
                    IsHitTestVisible = false
                };
                _pitchMarks.Add(mark);
                picture.Children.Add(mark);
            }
            PlacePitchMarks();

            int inner = CwWaterfallStrip.Rows + 2 * LetterRowHeight + 2;
            _wfBorder = new Border
            {
                Child = picture,
                Height = inner,
                Margin = new Thickness(0, (WaterfallRowHeight - inner) / 2, 0, (WaterfallRowHeight - inner) / 2),
                BorderThickness = new Thickness(1),
                BorderBrush = GroupEdgeBrush,
                Background = Brushes.Black,   // so the mouse finds it for the tooltip: the strip's own drawing cannot be hit
                ClipToBounds = true,
            };
            PlacePitchMarks();
            return _wfBorder;
        }

        Border _wfBorder;
        readonly System.Collections.Generic.List<Rectangle> _pitchMarks = new System.Collections.Generic.List<Rectangle>();

        void PlacePitchMarks()
        {
            int hz = 700;
            try { hz = Properties.Settings.Default.CwPitchHz; }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
            hz = Math.Max(LowestPitchHz, Math.Min(HighestPitchHz, hz));
            if (_strip != null) _strip.PitchHz = hz;
            double y = (CwWaterfallStrip.HighHz - hz) / (CwWaterfallStrip.HighHz - CwWaterfallStrip.LowHz) * (CwWaterfallStrip.Rows - 1) + 0.5;
            foreach (Rectangle mark in _pitchMarks) mark.Margin = new Thickness(0, y - 1.5, 0, 0);
            if (_wfBorder != null) _wfBorder.ToolTip = WaterfallTip(hz);
        }

        static string WaterfallTip(int pitchHz)
        {
            return "What the decoders hear: time runs right to left, the note up (300 to 1000 Hz). The dotted green line is the note being read; the short red lines at the edges mark your pitch, " + pitchHz + " Hz.\nUnder it, the letters Plain made of it.";
        }

        void OnPitchChanged()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                PlacePitchMarks();
                ApplyMixHide();
            }));
        }

        // Options > General > CW Decoder: in Mix, hide the letters the network did not confirm.
        void ApplyMixHide()
        {
            bool hide = false;
            // HybridHideUnconfirmed, ticked unless he unticks it (2026-10-20, his choice after 8.9.22). A new
            // name rather than a new default on MixHideUnconfirmed: 8.9.22 had already saved "false" for its
            // users, and an upgrade keeps saved values, so a changed default would never have reached them.
            try { hide = Properties.Settings.Default.HybridHideUnconfirmed; }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
            if (_mix != null) _mix.HideUnconfirmed = hide;
        }
        static readonly Brush MarkBrush = Frozen(Color.FromRgb(0xFF, 0x50, 0x50));

        // ONE LINE OF LETTERS UNLESS BOTH ARE ON SHOW (2026-10-03, his request): with one reader the
        // second line was always empty and only took room from the text above.
        void SizeLetterLines(bool both)
        {
            if (_strip == null || _wfBorder == null) return;
            int lines = both ? 2 : 1;
            _strip.Element.Height = CwWaterfallStrip.Rows + lines * LetterRowHeight;
            _wfBorder.Height = CwWaterfallStrip.Rows + lines * LetterRowHeight + 2;
        }

        // ---- the filter waterfall (his design, 2026-11-22; see CwFilterSpectrum) ----
        //
        // HOW WIDE: what the radio says its filter is (the main window asks an IC-7610 every second - RadioFilter),
        // else what the sound itself shows (CwFilterSpectrum.MeasuredFilter). WHERE: the filter sits around the
        // radio's own CW pitch (RadioPitch), else around the middle of what was measured, else around his pitch.
        // The ruler is the radio's frequency: the dial is what a station heard in the middle of the filter is on.
        const int SpectrumPanelWidth = CwFilterSpectrum.Columns + 2;   // the picture plus its frame
        const double SpectrumRulerHeight = 30;

        /// <summary>Set by the main window: the radio's dial in Hz, 0 when there is no radio.</summary>
        public Func<double> DialHz;
        /// <summary>Set by the main window: +1 when the radio hears CW on the upper side, -1 on the lower, 0 not known.</summary>
        public Func<int> CwSide;

        CwFilterSpectrum _spectrum;
        Grid _specPicture;
        Canvas _specRuler;
        string _specSay = string.Empty;   // the filter width, "BW 400", drawn in the ruler row
        int _spectrumTicks;
        int _radioFilterHz, _radioPitchHz;
        DateTime _radioFilterAt = DateTime.MinValue, _radioPitchAt = DateTime.MinValue;
        // What is on show: the middle note and the width (sound Hz), the side, and the dial the ruler is drawn for.
        double _specCentreHz = 700, _specWidthHz = 600, _specDial;
        int _specSide;
        // The notes at the two edges of the picture. Centred on the middle of the filter - except that the sound has
        // nothing under 0 Hz: a 3 kHz filter around a 600 Hz pitch is shown from 0 Hz up, so it is not centred then.
        double _specLowHz = 400, _specHighHz = 1000;
        Rectangle _specCentreLine;
        // HIS PITCH (Options > General), where the red line is and where a clicked station goes (his request,
        // 2026-11-22) - the same pitch as the red marks on the strip and the Tune key.
        int _specPitchHz = 700;

        /// <summary>From the main window: the radio's filter width now, in Hz.</summary>
        public void RadioFilter(int widthHz) { _radioFilterHz = widthHz; _radioFilterAt = DateTime.UtcNow; }

        /// <summary>From the main window: the radio's own CW pitch in Hz - the middle of its CW filter.</summary>
        public void RadioPitch(int hz) { _radioPitchHz = hz; _radioPitchAt = DateTime.UtcNow; }

        readonly Image[] _specRing = new Image[2];
        TimeSpan _specLastFrame = TimeSpan.Zero;

        void OnSpectrumFrame(object sender, EventArgs e)
        {
            if (_spectrum == null) return;
            var args = e as System.Windows.Media.RenderingEventArgs;
            TimeSpan now = args != null ? args.RenderingTime : TimeSpan.Zero;
            if (now == _specLastFrame) return;          // WPF can call twice for one frame
            double seconds = _specLastFrame == TimeSpan.Zero ? 0 : (now - _specLastFrame).TotalSeconds;
            _specLastFrame = now;
            if (_spectrum.Paint(seconds)) PlaceSpectrumRing();
        }

        // The newest row at the top, the copy below it carrying on with the older rows.
        void PlaceSpectrumRing()
        {
            if (_spectrum == null || _specRing[0] == null) return;
            int head = _spectrum.Head;
            Canvas.SetTop(_specRing[0], -head);
            Canvas.SetTop(_specRing[1], CwFilterSpectrum.Rows - head);
        }

        UIElement BuildSpectrum()
        {
            _spectrum = new CwFilterSpectrum();

            // The ring picture shown twice, one copy above the other, moved so the newest row is at the top
            // (see CwFilterSpectrum.Paint and PlaceSpectrumRing).
            var ring = new Canvas { IsHitTestVisible = false, ClipToBounds = true };
            foreach (int copy in new[] { 0, 1 })
            {
                var image = new Image
                {
                    Source = _spectrum.Bitmap,
                    Stretch = Stretch.Fill,
                    Height = CwFilterSpectrum.Rows,
                    IsHitTestVisible = false
                };
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.LowQuality);
                _specRing[copy] = image;
                ring.Children.Add(image);
            }

            // His pitch: where a clicked station is put (placed by PlaceSpectrumCentre).
            var centre = new Rectangle { Width = 3, Fill = MarkBrush, Opacity = 0.8, IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Left };
            _specCentreLine = centre;

            _specPicture = new Grid
            {
                Background = Brushes.Black,
                ClipToBounds = true,
                // THE MOUSE BECOMES A SHORT UPRIGHT LINE over the picture (his request, 2026-11-22): laid over a
                // station's stripe, then clicked, and the station snaps to the red line. No tooltip here - it would
                // pop up over the very place he is aiming at; the explanation is on the row above.
                Cursor = System.Windows.Input.Cursors.None
            };
            var hover = new Rectangle
            {
                Width = 2,
                Height = 30,
                Fill = Brushes.White,
                IsHitTestVisible = false,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Visibility = Visibility.Collapsed
            };
            _specPicture.MouseMove += (s, e) =>
            {
                var at = e.GetPosition(_specPicture);
                hover.Margin = new Thickness(at.X - hover.Width / 2, at.Y - hover.Height / 2, 0, 0);
                hover.Visibility = Visibility.Visible;
            };
            _specPicture.MouseLeave += (s, e) => hover.Visibility = Visibility.Collapsed;
            _specPicture.Children.Add(ring);
            _specPicture.Children.Add(centre);
            _specPicture.Children.Add(hover);
            _specPicture.MouseLeftButtonDown += OnSpectrumClick;
            _specPicture.SizeChanged += (s, e) =>
            {
                foreach (var image in _specRing) image.Width = _specPicture.ActualWidth;
                PlaceSpectrumCentre();
                PlaceSpectrumRing();
            };

            // Every frame the screen draws, while the window is open: the lines let out on a steady clock.
            System.Windows.Media.CompositionTarget.Rendering += OnSpectrumFrame;
            Closed += (s, e) => System.Windows.Media.CompositionTarget.Rendering -= OnSpectrumFrame;

            _specRuler = new Canvas { Height = SpectrumRulerHeight, Background = Brushes.Black, ClipToBounds = true };
            _specRuler.SizeChanged += (s, e) => DrawSpectrumRuler();

            var stack = new DockPanel();
            DockPanel.SetDock(_specRuler, Dock.Top);
            stack.Children.Add(_specRuler);
            stack.Children.Add(_specPicture);

            UpdateSpectrumSpan();
            return new Border
            {
                Child = stack,
                Width = SpectrumPanelWidth,
                // Clear of the bar (his screen, 2026-11-22: the ruler sat tight against it), level with the strip on
                // the left, which starts the same 3 px under the bar (see BuildWaterfall's margin).
                Margin = new Thickness(0, (WaterfallRowHeight - (CwWaterfallStrip.Rows + 2 * LetterRowHeight + 2)) / 2, 0, 0),
                BorderThickness = new Thickness(1),
                BorderBrush = GroupEdgeBrush,
                Background = Brushes.Black,
                ClipToBounds = true
            };
        }

        // Four times a second: the filter's width and middle, the side and the dial - the picture and the ruler follow.
        void UpdateSpectrumSpan()
        {
            if (_spectrum == null) return;
            DateTime now = DateTime.UtcNow;
            int pitch = 700;
            try { pitch = Properties.Settings.Default.CwPitchHz; }
            catch (Exception swallowed) { Log.Swallow(swallowed); }

            double mLow, mHigh;
            bool measured = _spectrum.MeasuredFilter(out mLow, out mHigh);
            bool fromRadio = _radioFilterHz > 0 && now - _radioFilterAt < TimeSpan.FromSeconds(5);
            bool radioPitch = _radioPitchHz > 0 && now - _radioPitchAt < TimeSpan.FromSeconds(15);

            double width, centre;
            string say, tip;
            // "BW 400", as the IC-7610 writes its filter width; "~" when it is only measured from the sound.
            if (fromRadio)
            {
                width = _radioFilterHz;
                say = "BW " + width.ToString("0");
                tip = "Filter width " + width.ToString("0") + " Hz, read from the radio.";
            }
            else if (measured)
            {
                width = Math.Max(50, Math.Round((mHigh - mLow) / 50) * 50);   // steadied: the measure wanders a little
                say = "BW ~" + width.ToString("0");
                tip = "Filter width about " + width.ToString("0") + " Hz, measured from the sound.";
            }
            else
            {
                width = 600;
                say = "BW ?";
                tip = "Filter width not known yet. It is measured from the sound once there is some.";
            }
            if (radioPitch) centre = _radioPitchHz;
            else if (measured) centre = Math.Round((mLow + mHigh) / 2 / 10) * 10;
            else centre = pitch;

            int side = 0;
            double dial = 0;
            try
            {
                if (CwSide != null) side = CwSide();
                if (DialHz != null && side != 0) dial = DialHz();
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }

            // A measured middle that moved only a little is left where it is: every move clears the picture.
            if (!radioPitch && Math.Abs(centre - _specCentreHz) < Math.Max(15, 0.05 * width)) centre = _specCentreHz;

            bool changed = Math.Abs(centre - _specCentreHz) >= 1 || Math.Abs(width - _specWidthHz) >= 1 || side != _specSide;
            bool dialMoved = Math.Abs(dial - _specDial) >= 1;
            if (pitch != _specPitchHz) { _specPitchHz = pitch; changed = true; }
            _specCentreHz = centre; _specWidthHz = width; _specSide = side; _specDial = dial;
            _specLowHz = Math.Max(0, centre - width / 2);
            _specHighHz = centre + width / 2;
            _spectrum.SetSpan(_specLowHz, _specHighHz, side < 0);
            if (say != _specSay) { _specSay = say; changed = true; }
            _specRuler.ToolTip = "The frequency of a station on the red line (your pitch).\n" + tip
                               + "\nBelow: the filter, radio frequency across, time down. Put the white line on a station and click: it moves to the red line.";
            if (changed || dialMoved) { DrawSpectrumRuler(); PlaceSpectrumCentre(); }
        }

        // Where a note is across the picture, 0 at the left edge and 1 at the right (radio frequency rises to the right).
        double SpectrumAcross(double noteHz)
        {
            double span = _specHighHz - _specLowHz;
            if (span <= 0) return 0.5;
            double a = (noteHz - _specLowHz) / span;
            return _specSide < 0 ? 1 - a : a;
        }

        // And back: the note at a point across the picture.
        double SpectrumNoteAt(double across)
        {
            double span = _specHighHz - _specLowHz;
            return _specLowHz + (_specSide < 0 ? 1 - across : across) * span;
        }

        // The red line: his pitch, where a clicked station goes. Not drawn when his pitch is outside the filter.
        void PlaceSpectrumCentre()
        {
            if (_specCentreLine == null || _specPicture == null) return;
            double across = SpectrumAcross(_specPitchHz);
            _specCentreLine.Visibility = across >= 0 && across <= 1 ? Visibility.Visible : Visibility.Collapsed;
            _specCentreLine.Margin = new Thickness(across * _specPicture.ActualWidth - _specCentreLine.Width / 2, 0, 0, 0);
        }

        static readonly Brush BwInk = Frozen(Color.FromRgb(0x9E, 0xC9, 0xFF));

        // ONE NUMBER, OVER THE RED LINE (his choice, 2026-11-22). Round figures every so often came and went as the dial
        // moved and changed with the filter width. Now the one figure is the frequency of a station sitting on his pitch
        // - in radio kHz, 10 Hz steps - always in the same place; with no radio, the pitch itself in Hz.
        void DrawSpectrumRuler()
        {
            if (_specRuler == null) return;
            _specRuler.Children.Clear();
            double w = _specRuler.ActualWidth;
            if (w <= 0 || _specHighHz <= _specLowHz) return;
            double across = SpectrumAcross(_specPitchHz);
            bool onShow = across >= 0 && across <= 1;     // his pitch is outside the filter: no red line, no figure
            double x = Math.Max(0, Math.Min(1, across)) * w;

            // THE FILTER WIDTH IN THE SAME ROW (his request, 2026-11-22), on whichever side of the figure has more
            // room - in muted blue, so it is not read as a frequency.
            var bw = new TextBlock { Text = _specSay, FontSize = 16, Foreground = BwInk };
            bw.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(bw, x > w / 2 ? 4 : w - bw.DesiredSize.Width - 4);
            Canvas.SetTop(bw, 3);
            _specRuler.Children.Add(bw);
            if (!onShow) return;

            // The dial is what a station heard in the middle of the filter is on; a note `off` Hz from the middle is
            // `off` Hz from the dial on the upper side and the other way on the lower.
            string text = _specDial > 0
                ? ((_specDial + (_specPitchHz - _specCentreHz) * (_specSide < 0 ? -1 : 1)) / 1000)
                    .ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
                : _specPitchHz.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " Hz";

            var tick = new Rectangle { Width = 3, Height = 5, Fill = MarkBrush };   // as wide as the red line under it
            Canvas.SetLeft(tick, x - 1.5);
            Canvas.SetTop(tick, SpectrumRulerHeight - 5);
            _specRuler.Children.Add(tick);

            var label = new TextBlock { Text = text, FontSize = 16, Foreground = Brushes.White };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            // Over the line; near an edge, pushed in so it is never cut.
            double lx = Math.Max(0, Math.Min(w - label.DesiredSize.Width, x - label.DesiredSize.Width / 2));
            Canvas.SetLeft(label, lx);
            Canvas.SetTop(label, 3);   // off the top edge
            _specRuler.Children.Add(label);
        }

        // A click on the picture: the station nearest it goes to his pitch (the red line).
        void OnSpectrumClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            e.Handled = true;
            double w = _specPicture.ActualWidth;
            if (w <= 0 || _spectrum == null) return;
            if (ShiftRadio == null) { SayTune("No radio"); return; }
            double note = SpectrumNoteAt(e.GetPosition(_specPicture).X / w);
            double found = _spectrum.FindStation(note, Math.Max(15, _specWidthHz * 0.06));
            if (!double.IsNaN(found)) note = found;
            double off = note - _specPitchHz;
            if (Math.Abs(off) < 5) { SayTune("On the station"); return; }
            string why = ShiftRadio(off);
            SayTune(why ?? "Moved " + Math.Round(Math.Abs(off)).ToString("N0") + " Hz");
            if (why != null) return;
            _autoHoldUntil = DateTime.UtcNow.AddSeconds(3);   // his choice stands; Auto waits
            CheckTheMove(off, _specPitchHz);
        }

        // ---- AUTO: snapping onto a station near his pitch by itself (his request, 2026-11-22) ----
        //
        // Four times a second (from UpdateSpectrumSpan). A station counts when it stands 6 dB over the filter's
        // noise within AutoReachHz of his pitch (the strongest, if there are two) and has stayed within 10 Hz of
        // where it was for AutoSteadySeconds - a burst of noise or a station passing through does not move the radio.
        // Then the radio is moved once, as if clicked, when the station is more than AutoMinHz off.
        //
        // NOT WHILE WE TRANSMIT, nor for AutoAfterTxSeconds after: the sound then is our own, or just ending.
        // NO CHASING: after each move Auto waits AutoHoldSeconds. In a QSO the two stations are often a little apart;
        // without the wait it would jump from one to the other every over, and with it his transmit frequency.
        // A click holds it off for 3 s too, so it does not undo what he just chose.
        const double AutoReachHz = 50, AutoMinHz = 10, AutoSteadySeconds = 1.0, AutoAfterTxSeconds = 3, AutoHoldSeconds = 10;
        bool _autoSnap;
        double _autoCandidateHz = double.NaN;
        DateTime _autoSince = DateTime.MinValue, _autoHoldUntil = DateTime.MinValue;

        void AutoSnap()
        {
            if (!_autoSnap || _spectrum == null || ShiftRadio == null) return;
            DateTime now = DateTime.UtcNow;
            // _txLastMs is kept by the capture thread (TransmittingNow, every block): the last moment we sent.
            bool sending = KeyerTransmitting || RadioTransmitting
                        || _txClock.Elapsed.TotalMilliseconds - _txLastMs < AutoAfterTxSeconds * 1000;
            if (sending || now < _autoHoldUntil) { _autoCandidateHz = double.NaN; return; }

            double found = _spectrum.FindStation(_specPitchHz, AutoReachHz);
            if (double.IsNaN(found)) { _autoCandidateHz = double.NaN; return; }
            if (double.IsNaN(_autoCandidateHz) || Math.Abs(found - _autoCandidateHz) > 10)
            {
                _autoCandidateHz = found;      // a new one: it has to stay put for a while first
                _autoSince = now;
                return;
            }
            _autoCandidateHz = found;
            if ((now - _autoSince).TotalSeconds < AutoSteadySeconds) return;

            double off = found - _specPitchHz;
            if (Math.Abs(off) <= AutoMinHz) return;   // already on it
            string why = ShiftRadio(off);
            _autoCandidateHz = double.NaN;
            _autoHoldUntil = now.AddSeconds(why == null ? AutoHoldSeconds : 5);
            SayTune(why ?? "Auto " + Math.Round(Math.Abs(off)).ToString("N0") + " Hz");
            if (why == null) CheckTheMove(off, _specPitchHz);
        }

        long _wfBase;                                  // waterfall sample count when the readers were made

        // EVERY LETTER PLAIN SPELLS OUT, THE MOMENT IT DOES (his rule, 2026-10-21): written under the middle of
        // its marks on the waterfall, and at the same moment into Hybrid's text - not first held back by Plain
        // (until it is sure this is Morse, and a lone E or T until a word follows), as it used to be, which left
        // letters under the waterfall that the text never showed. Noise read as letters is the network's to
        // judge: where it hears nothing, the letter goes grey (hidden unless he chose to see them).
        void OnPlainLetterTimed(string letter, long start, long end)
        {
            if (_strip != null) _strip.AddLetter(letter, _wfBase + (start + end) / 2);
            if (_plainRate <= 0) return;
            lock (_pendingGate) _pendingPlainTimed.Add(new System.Collections.Generic.KeyValuePair<string, double>(letter, end / (double)_plainRate));
        }

        // Plain's word gap, the moment it is heard (see CwDecoder.WordGapTimed).
        void OnPlainWordGap()
        {
            lock (_pendingGate) _pendingPlainTimed.Add(new System.Collections.Generic.KeyValuePair<string, double>(" ", double.NaN));
        }

        // On our network's own thread.
        void OnDeepLetterTimed(string letter, double seconds)
        {
            lock (_pendingGate) _pendingNetTimed.Add(new System.Collections.Generic.KeyValuePair<string, double>(letter, seconds));
        }

        void OnDeepCovered(double from, double to)
        {
            lock (_pendingGate) _pendingCovered.Add(new System.Collections.Generic.KeyValuePair<double, double>(from, to));
        }

        void OnElementLetterTimed(string letter, long start, long end)
        {
            // (New's letters are no longer written under the strip: only Plain's line is shown there.)
        }

        // WHILE WE TRANSMIT (his request, 2026-10-21). The radio's sound then is our own keying - on his
        // IC-7610 every dit and dah came through as a bright bar across the whole strip, and the readers
        // printed it as letters ("DU EATE I EGLE T E"). So while we transmit the readers are given silence
        // (the same length, so every reader's clock keeps step with the sound), and the waterfall draws our
        // keying as clean cyan marks at his pitch - see DrawWaterfall. Set by the main window: by the keyer
        // when HolyLogger keys through a COM port, from the radio's TX state otherwise. TxHangMs more after
        // the end, for the sound still on its way from the sound card and the radio's break-in delay.
        public static volatile bool KeyerTransmitting, RadioTransmitting;
        const double TxHangMs = 400;
        static readonly System.Diagnostics.Stopwatch _txClock = System.Diagnostics.Stopwatch.StartNew();

        /// <summary>Now, on the clock our key edges and the CAT layout are stamped with (KeyAt reads it). The
        /// waterfall times its sound on this same clock - on a clock of its own its key look-ups all missed and
        /// no cyan mark was ever drawn (his report, 2026-10-21).</summary>
        internal static double TxClockMs { get { return _txClock.Elapsed.TotalMilliseconds; } }

        // OUR KEYER'S OWN EDGES (COM port). Keyed on its KEY jack, his IC-7610 sends nothing of it down the USB
        // sound - the strip stayed empty - so the marks are drawn from the moments the keyer pressed and released
        // the line, laid on the sound by the time each block of it arrived.
        static readonly System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<double, bool>> _keyEdges = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<double, bool>>();

        /// <summary>Our keyer pressed (true) or released the key, now. From the keying thread.</summary>
        public static void KeyEdge(bool down)
        {
            KeyEdgeAt(_txClock.Elapsed.TotalMilliseconds, down);
        }

        // At a given moment - for CAT, moments still to come (see CatCwSent). Kept in time order.
        static void KeyEdgeAt(double ms, bool down)
        {
            lock (_keyEdges)
            {
                int at = _keyEdges.Count;
                while (at > 0 && _keyEdges[at - 1].Key > ms) at--;
                _keyEdges.Insert(at, new System.Collections.Generic.KeyValuePair<double, bool>(ms, down));
                if (_keyEdges.Count > 4000) _keyEdges.RemoveRange(0, 1000);
            }
        }

        // 0 = no keyer edges near this moment (tell key-down from the sound), 1 = key up, 2 = key down.
        static byte KeyAt(double ms)
        {
            lock (_keyEdges)
            {
                for (int i = _keyEdges.Count - 1; i >= 0; i--)
                    if (_keyEdges[i].Key <= ms)
                        return ms - _keyEdges[i].Key > 10000 ? (byte)0 : _keyEdges[i].Value ? (byte)2 : (byte)1;
            }
            return 0;
        }
        double _txLastMs = double.NegativeInfinity;
        short[] _silence = new short[0];

        // CW HANDED TO THE RADIO BY CAT. The radio's own TX report comes through OmniRig's polling, late and with
        // holes - on his screen the first elements and some in the middle were still drawn as big white bars and
        // read as letters. So from the moment HolyLogger hands the radio a text, it counts as transmitting for as
        // long as that text takes at the speed (worked out from its dits, dahs and gaps), plus CatStartMs for the
        // radio to begin; a text handed over while one is still going is added on after it.
        //
        // THE MARKS TOO ARE WORKED OUT, NOT HEARD. His transmit log (2026-10-21) showed the radio's USB sound
        // silent for the whole of a CAT message - level 3 against about 1000 receiving - so there is nothing in
        // the sound to draw. The dits and dahs are laid out from the text and the speed instead, as key edges
        // (see KeyEdge) from CatStartMs after the hand-over: the log had the sound go quiet 0.1 s after it.
        const double CatStartMs = 150;
        static double _catUntilMs = double.NegativeInfinity;
        static readonly object _catGate = new object();

        // OUR OWN LETTERS UNDER OUR CYAN MARKS (his idea, 2026-10-21: "print letters below the cyan signs as we do
        // for receiving"): each letter we send, at the middle of its marks on the transmit clock - from the COM
        // keyer as it finishes each one (LetterSent), or laid out from the CAT text at the speed (CatCwSent).
        static readonly System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<double, string>> _txLetters =
            new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<double, string>>();
        static readonly System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<double, string>> _sentForRows =
            new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<double, string>>();
        readonly System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, double>> _pendingSent =
            new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, double>>();
        double _readerSeconds;     // how much sound the readers have been given - their clock, as Plain's letter times

        /// <summary>A letter we send, at `midMs` on the transmit clock (TxClockMs) - the middle of its marks.</summary>
        public static void TxLetter(string letter, double midMs)
        {
            // OUR SENT TEXT IN THE ROWS TOO (his request, 2026-11-23): the same letters as under the cyan marks, and
            // the word gaps between them, in their own colour. Queued here; OnSamples hands each one on when its
            // moment has come (a CAT text is laid out ahead), stamped with the readers' clock.
            lock (_sentForRows) _sentForRows.Add(new System.Collections.Generic.KeyValuePair<double, string>(midMs, letter));
            if (letter == " ") return;    // a word gap: nothing to draw under the marks
            lock (_txLetters)
            {
                int at = _txLetters.Count;
                while (at > 0 && _txLetters[at - 1].Key > midMs) at--;
                _txLetters.Insert(at, new System.Collections.Generic.KeyValuePair<double, string>(midMs, letter));
                double old = TxClockMs - 60000;
                int drop = 0;
                while (drop < _txLetters.Count && _txLetters[drop].Key < old) drop++;
                if (drop > 0) _txLetters.RemoveRange(0, drop);
            }
        }

        // The letters we sent between two moments, for the waterfall strip.
        static void TxLettersBetween(System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<double, string>> into, double fromMs, double toMs)
        {
            into.Clear();
            lock (_txLetters)
                foreach (var l in _txLetters)
                    if (l.Key >= fromMs && l.Key <= toMs) into.Add(l);
        }

        /// <summary>A CW text has just been handed to the radio over CAT, to be sent at about wpm.</summary>
        public static void CatCwSent(string text, double wpm)
        {
            if (string.IsNullOrEmpty(text)) return;
            double unitMs = 1200.0 / (wpm >= 5 ? wpm : 20);
            double now = _txClock.Elapsed.TotalMilliseconds;
            lock (_catGate)
            {
                double at = Math.Max(now + CatStartMs, _catUntilMs);
                foreach (char c in text.ToUpperInvariant())
                {
                    string pattern;
                    if (c == ' ')
                    {
                        TxLetter(" ", at);    // for the rows only (see TxLetter)
                        at += (CwSendMonitorWindow.WordGapUnits - CwSendMonitorWindow.LetterGapUnits) * unitMs;
                    }
                    else if (CwSendMonitorWindow.TryGetMorse(c, out pattern))
                    {
                        double letterStart = at;
                        foreach (char e in pattern)
                        {
                            KeyEdgeAt(at, true);
                            at += (e == '-' ? 3 : 1) * unitMs;
                            KeyEdgeAt(at, false);
                            at += unitMs;
                        }
                        TxLetter(c.ToString(), (letterStart + at - unitMs) / 2);
                        at += (CwSendMonitorWindow.LetterGapUnits - 1) * unitMs;
                    }
                }
                _catUntilMs = at;
            }
        }

        /// <summary>The CAT sending was stopped (Esc): the sound is the band's again.</summary>
        public static void CatCwStopped()
        {
            double now = _txClock.Elapsed.TotalMilliseconds;
            lock (_catGate) _catUntilMs = double.NegativeInfinity;
            lock (_keyEdges)
            {
                _keyEdges.RemoveAll(e => e.Key > now);   // the rest of the text will not be sent
                _keyEdges.Add(new System.Collections.Generic.KeyValuePair<double, bool>(now, false));
            }
            lock (_txLetters) _txLetters.RemoveAll(e => e.Key > now);
        }

        bool TransmittingNow()
        {
            double now = _txClock.Elapsed.TotalMilliseconds;
            double catUntil;
            lock (_catGate) catUntil = _catUntilMs;
            if (KeyerTransmitting || now < catUntil) _ourSendLastMs = now;
            if (KeyerTransmitting || RadioTransmitting || now < catUntil) _txLastMs = now;
            return now - _txLastMs <= TxHangMs;
        }

        // OUR OWN HAND KEYING, READ FROM THE SIDE TONE (his request, 2026-11-13: to see how well his straight-key
        // sending reads). HolyLogger is told nothing of his key, but with MENU > SET > Connectors > USB AF/IF Output >
        // AF Beep/Speech... Output ON his IC-7610 sends its side tone down the USB sound: one clean note (645 Hz),
        // dits and dahs at their true lengths, silence between - Plain read his CQs from a recording of it. Set to
        // OFF (the default) the sound is silent while transmitting, as before. So while the radio transmits and
        // HolyLogger is not the one sending, once a block holds one clear note the rest of that transmission goes to
        // the readers and the waterfall like received sound. Sound loud across the whole band (the wide bars of
        // 2026-10-21) never holds one clear note, and is still blanked. One block at a time was not enough: on his
        // recording 8 of 258 blocks with sound (a key click, the tail of a dit) fell short and would have cut letters.
        //
        // ONLY THE RADIO'S OWN TX REPORT SAYS IT IS HIS SENDING (his screen, 2026-11-16). For a while (2026-11-13) dead
        // silence between the elements counted too - the radio sends rms 2 there while he transmits, and his
        // receiving never went under 20 - so his first element would not wait for OmniRig's late report. But with the
        // Windows recording level turned down to 13, a station he was RECEIVING had gaps that quiet, and it was drawn
        // as his own sending, in cyan. So the report alone decides, and the first element, drawn as sound before the
        // report came, is redrawn as his when it does (see CwWaterfallStrip.Feed, RedrawMs).
        const double SideToneOverMedianDb = 20;   // his side tone: median 37 dB over the band; received sound 11-23
        double _ourSendLastMs = double.NegativeInfinity, _sideToneHz;
        bool _sideTone;

        bool HolyLoggerSending()
        {
            return _txClock.Elapsed.TotalMilliseconds - _ourSendLastMs <= TxHangMs;
        }

        // One clear note between 300 and 1000 Hz (10 Hz steps, Goertzel), standing SideToneOverMedianDb over the
        // middle of the band. Silence is not a note. hz = that note.
        static bool OneClearNote(short[] samples, int count, int rate, out double hz)
        {
            hz = 0;
            if (count <= 0 || rate <= 0) return false;
            double sum = 0;
            for (int i = 0; i < count; i++) sum += (double)samples[i] * samples[i];
            if (Math.Sqrt(sum / count) < 50) return false;
            var power = new double[71];
            double peak = 0;
            for (int k = 0; k < power.Length; k++)
            {
                double c = 2 * Math.Cos(2 * Math.PI * (300 + 10 * k) / rate), s1 = 0, s2 = 0;
                for (int i = 0; i < count; i++) { double s0 = samples[i] + c * s1 - s2; s2 = s1; s1 = s0; }
                power[k] = s1 * s1 + s2 * s2 - c * s1 * s2;
                if (power[k] > peak) { peak = power[k]; hz = 300 + 10 * k; }
            }
            Array.Sort(power);
            return 10 * Math.Log10((peak + 1) / (power[power.Length / 2] + 1)) >= SideToneOverMedianDb;
        }

        // On the capture thread. Hand the block straight to the decoder - it is a few thousand
        // multiplications, far less than the 100 ms of audio it represents.
        void OnSamples(short[] samples, int count)
        {
            var decoder = _decoder;
            if (decoder == null) return;

            // A copy for the waterfall, drawn on the screen timer - nothing is worked out here.
            bool tx = TransmittingNow(), sideTone = false;
            if (HolyLoggerSending() || !tx) _sideTone = false;
            else
            {
                double hz;
                if (!_sideTone && OneClearNote(samples, count, _recorder.ActualSampleRate, out hz)) { _sideTone = true; _sideToneHz = hz; }
                sideTone = _sideTone;   // his own hand keying: the readers get it like received sound
            }
            if (_strip != null) _strip.Feed(samples, count, _recorder.ActualSampleRate, tx, sideTone, _sideToneHz);
            // The filter waterfall shows the band only: nothing is drawn while we transmit.
            if (_spectrum != null && (!tx || sideTone)) _spectrum.Feed(samples, count, _recorder.ActualSampleRate);
            if (tx && !sideTone)
            {
                if (_silence.Length < count) _silence = new short[count];
                samples = _silence;
            }

            try { decoder.Process(samples, count); }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
            if (_strip != null) _strip.NoteHz = decoder.SignalPresent ? decoder.ToneHz : 0;   // the dotted line on the strip

            // Our sent letters whose moment has come go to the rows, on the readers' clock (see TxLetter).
            int rate = _recorder.ActualSampleRate;
            if (rate > 0) _readerSeconds += count / (double)rate;
            double nowTx = TxClockMs;
            lock (_sentForRows)
            {
                int due = 0;
                while (due < _sentForRows.Count && _sentForRows[due].Key <= nowTx) due++;
                if (due > 0)
                {
                    lock (_pendingGate)
                        for (int i = 0; i < due; i++)
                            _pendingSent.Add(new System.Collections.Generic.KeyValuePair<string, double>(_sentForRows[i].Value, _readerSeconds));
                    _sentForRows.RemoveRange(0, due);
                }
            }

            // Only copied here; the reading itself runs on the new decoder's own thread.
            var element = _element;
            if (element != null)
            {
                try { element.Process(samples, count); }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
            }

            var deep = _deep;
            if (deep != null)
            {
                try
                {
                    deep.Process(samples, count);
                    // which station to read: the one Plain follows (see CwDeepDecoder - a "CWD2" network)
                    deep.Note(decoder.SignalPresent ? decoder.ToneHz : 0);
                }
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
            if (showing == Reader.Network || showing == Reader.New || showing == Reader.Deep) return;
            lock (_pendingGate) _pendingPlain.Append(text);
        }

        void OnNetworkText(string text)
        {
            if (Showing != Reader.Network) return;
            lock (_pendingGate) _pendingNetwork.Append(text);
        }

        // On our network's own thread.
        void OnDeepText(string text)
        {
            var showing = Showing;
            if (showing != Reader.Deep && showing != Reader.Both) return;
            lock (_pendingGate) _pendingDeep.Append(text);
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
            // The filter and the dial, 4 times a second; the picture itself moves on every frame (OnSpectrumFrame).
            if (_spectrum != null && ++_spectrumTicks % 5 == 0) { UpdateSpectrumSpan(); AutoSnap(); }
        }

        void FlushPendingText()
        {
            string plain, network, element, deepText;
            System.Collections.Generic.KeyValuePair<string, double>[] plainTimed, netTimed, sent;
            System.Collections.Generic.KeyValuePair<double, double>[] covered;
            lock (_pendingGate)
            {
                covered = _pendingCovered.ToArray();
                _pendingCovered.Clear();
                plainTimed = _pendingPlainTimed.ToArray();
                netTimed = _pendingNetTimed.ToArray();
                _pendingPlainTimed.Clear();
                _pendingNetTimed.Clear();
                sent = _pendingSent.ToArray();
                _pendingSent.Clear();
                plain = _pendingPlain.ToString();
                network = _pendingNetwork.ToString();
                element = _pendingElement.ToString();
                deepText = _pendingDeep.ToString();
                _pendingPlain.Clear();
                _pendingNetwork.Clear();
                _pendingElement.Clear();
                _pendingDeep.Clear();
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
                if (_deepText != null) { _deepText.ToneHz = note; _deepText.Wpm = speed; }
            }

            // The time line is kept whichever reader is on show, and drawn only in Both.
            double wpm = heard != null && heard.Wpm >= 5 ? heard.Wpm : 20;
            double noteNow = heard != null && heard.SignalPresent ? heard.ToneHz : 0;
            double speedNow = heard != null && heard.SignalPresent ? heard.Wpm : 0;
            double netEarly = 0.8 * 1.2 / wpm;       // the network says a letter 0.8 dit after it ends
            foreach (var e in plainTimed)
            {
                if (e.Key != " ") _timeLine.AddPlain(e.Key, e.Value, wpm);
                _mix.AddPlain(e.Key, e.Value, noteNow, speedNow);
            }
            foreach (var e in netTimed)
            {
                _timeLine.AddNet(e.Key, e.Value - netEarly, wpm);
                _mix.AddNet(e.Key, e.Key == " " ? e.Value : e.Value - netEarly);   // a gap is timed at its middle
            }
            foreach (var c in covered) _mix.Covered(c.Key - netEarly, c.Value - netEarly);
            foreach (var s in sent) _mix.AddSent(s.Key, s.Value);      // our own sending, in its own colour

            // With one reader on show it writes into the top box; in Both the plain decoder has the
            // top box and the new one the box under it.
            var showing = Showing;
            if (showing == Reader.Network) AppendTo(_text, network);
            else if (showing == Reader.New) AppendTo(_text, element);
            else if (showing == Reader.Deep) AppendTo(_text, deepText);
            else
            {
                AppendTo(_text, plain);
                if (showing == Reader.Both) { AppendTo(_networkText, element); AppendTo(_deepText, deepText); }
            }
            if (showing == Reader.Both && CwDeepDecoder.Available) _timeLine.Show();
            if (showing == Reader.Mix) _mix.Show();
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

        // A word from the Tune key, shown where the speed is for a few seconds.
        string _tuneSay;
        DateTime _tuneSayUntil;
        DispatcherTimer _tuneCheck;

        void SayTune(string text)
        {
            _tuneSay = text;
            _tuneSayUntil = DateTime.UtcNow.AddSeconds(3);
            UpdateSpeed();
        }

        // DID THE STATION COME ONTO THE PITCH? (Was the Tune key's own check; the key went on 2026-11-22, his decision,
        // and the check came here, for the click and Auto.) Two seconds after a move of `off` Hz the waterfall is looked
        // at again: a station on his pitch - it came our way. None there, but one at pitch + 2 x off - the radio went the
        // other way: this radio tunes the other way round, which is put right at once and remembered (WentWrongWay),
        // so every later move, and the waterfall's left and right, are right. Neither - the station stopped, or it is
        // not clear: left alone.
        void CheckTheMove(double off, int pitch)
        {
            if (_tuneCheck != null) _tuneCheck.Stop();
            _tuneCheck = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _tuneCheck.Tick += (s, e) =>
            {
                _tuneCheck.Stop();
                if (_spectrum == null || ShiftRadio == null) return;
                double reach = Math.Max(15, Math.Abs(off) * 0.5);
                if (!double.IsNaN(_spectrum.FindStation(pitch, reach))) return;              // it came our way
                double away = _spectrum.FindStation(pitch + 2 * off, reach);
                if (double.IsNaN(away)) return;                                              // not clear - leave it
                if (WentWrongWay != null) WentWrongWay();
                string again = ShiftRadio(away - pitch);
                SayTune(again ?? "Moved " + Math.Round(Math.Abs(away - pitch)).ToString("N0") + " Hz");
                _autoHoldUntil = DateTime.UtcNow.AddSeconds(AutoHoldSeconds);
            };
            _tuneCheck.Start();
        }

        void UpdateSpeed()
        {
            if (_tuneSay != null && DateTime.UtcNow < _tuneSayUntil) { _speedText.Text = _tuneSay; return; }
            _tuneSay = null;
            var decoder = _decoder;
            // No speed while it is only crackles (his report, 2026-11-23: "50 WPM" on an empty band). Noise reads as
            // the fastest Morse there is; above 28 WPM the speed is shown only once real Morse has been proved -
            // the same line the letters use (CwDecoder.MostWpmShown).
            if (decoder == null || !decoder.SignalPresent
                || (decoder.Wpm > CwDecoder.MostWpmShown && !decoder.ProvedMorse))
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
            PitchChanged -= OnPitchChanged;
            StopListening();
            try { _recorder.Dispose(); }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
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
        readonly Action<System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<double, string>>, double, double> _txLetters;
        readonly System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<double, string>> _txShown =
            new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<double, string>>();
        readonly System.Collections.Generic.Dictionary<string, FormattedText> _txShapes = new System.Collections.Generic.Dictionary<string, FormattedText>();
        static readonly Brush TxLetterInk = MakeTxLetterInk();

        static Brush MakeTxLetterInk()
        {
            var b = new SolidColorBrush(Color.FromRgb(0x00, 0xE0, 0xFF));   // the cyan of the marks (TxInk)
            b.Freeze();
            return b;
        }
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

        // REDRAWN AS HIS SENDING (see Feed): the last KeptColumns columns' levels and where each started, so those
        // made before his sending was recognised can be drawn again as his marks. _redrawFrom (under _gate): -1, or
        // the sample from which they are to be.
        // Long enough for the radio's TX report, which now alone says it is his sending: OmniRig asks every 500 ms
        // (his setting, and the most Options advises), so the report can come that long after his first element.
        const double RedrawMs = 1200;
        const int KeptColumns = 128;                        // 1.28 s, a little more than RedrawMs
        readonly double[][] _keptDb = new double[KeptColumns][];
        readonly long[] _keptRead = new long[KeptColumns];
        readonly double[][] _keptFloor = new double[KeptColumns][];   // each row's quiet level before that column
        volatile float _sideToneHz;                                   // his side tone's note (Feed)
        long _redrawFrom = -1;
        bool _fedSideTone;
        double _shownAt = double.NaN;                       // the column at the right edge, fractional
        double _lastFrameMs;
        readonly System.Collections.Generic.List<Letter> _letters = new System.Collections.Generic.List<Letter>();
        double _width, _height;                             // the element's size, from the window's thread
        static readonly int[] Palette = MakePalette();
        static readonly Typeface LetterFace = new Typeface(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

        public FrameworkElement Element { get; private set; }

        public CwWaterfallStrip(Func<double, byte> keyAt,
            Action<System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<double, string>>, double, double> txLetters)
        {
            _keyAt = keyAt;
            _txLetters = txLetters;
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
        //
        // AND UNHOOKED FROM THE WINDOW FIRST (the same error again, closing the whole program: the decoder then
        // goes down as part of the main window closing, and WPF tore the window down with the detach still
        // pending). So the window's own thread takes the strip's picture out of the window first, while both
        // ends are alive; only then is the drawing let go on the strip's thread.
        public void Close()
        {
            var d = _dispatcher;
            if (d == null || _closed) return;
            _closed = true;
            try { ((HostElement)Element).Unhook(); }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
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

        /// <summary>A block of sound, on the capture thread; tx = we were transmitting; sideTone = and the sound is
        /// his own hand keying's side tone (see CwDecodeWindow.OneClearNote).</summary>
        public void Feed(short[] samples, int count, int rate, bool tx, bool sideTone, double sideToneHz)
        {
            if (sideTone) _sideToneHz = (float)sideToneHz;
            // On the window's transmit clock: the moments looked up in _keyAt are stamped on it.
            double endMs = CwDecodeWindow.TxClockMs;           // the block's last sample has just arrived
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
                // His sending has just been recognised: the sound before it, back RedrawMs, was his first element
                // (the radio's TX report comes late) - marked so, and the columns already made from it redrawn.
                if (sideTone && !_fedSideTone && rate > 0)
                {
                    long from = Math.Max(0, _written - (long)(rate * RedrawMs / 1000));
                    for (long k = from; k < _written; k += 1 << SlotShift)
                    {
                        int slot = (int)((k >> SlotShift) & (_tx.Length - 1));
                        _tx[slot] = true;
                        _key[slot] = KeySideTone;
                    }
                    _redrawFrom = from;
                }
                _fedSideTone = sideTone;
                for (int i = 0; i < count; i++)
                {
                    if (i == 0 || (_written & ((1 << SlotShift) - 1)) == 0)
                    {
                        int slot = (int)((_written >> SlotShift) & (_tx.Length - 1));
                        _tx[slot] = tx || sideTone;
                        _key[slot] = sideTone ? KeySideTone : tx && rate > 0 ? _keyAt(_soundStartMs + _written * 1000.0 / rate) : (byte)0;
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
                double now = CwDecodeWindow.TxClockMs;
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
                long redraw;
                lock (_gate) { redraw = _redrawFrom; _redrawFrom = -1; }
                if (redraw >= 0) RedrawAsSideTone(redraw);
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

                Draw(width, height, hop, window, rate, soundStart);
                LogFrame(now);
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        void Draw(double width, double height, int hop, int window, int rate, double soundStart)
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

                // Our own letters, in cyan, from their moments on the transmit clock.
                Func<double, double> msAt = column => soundStart + (column * hop + window / 2.0) * 1000.0 / rate;
                if (_txLetters != null)
                {
                    _txLetters(_txShown, msAt(leftColumn - 2), msAt(_shownAt + 2));
                    foreach (var l in _txShown)
                    {
                        double column = ((l.Key - soundStart) * rate / 1000.0 - window / 2.0) / hop;
                        double x = (column - leftColumn) * perColumn - 5;
                        FormattedText shape;
                        if (!_txShapes.TryGetValue(l.Value, out shape))
                            _txShapes[l.Value] = shape = new FormattedText(l.Value, System.Globalization.CultureInfo.InvariantCulture,
                                FlowDirection.LeftToRight, LetterFace, 16, TxLetterInk, 1.0);
                        dc.DrawText(shape, new Point(x, Rows + 1));
                    }
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

            int kept = (int)(_columnsMade % KeptColumns);
            if (_keptDb[kept] == null) _keptDb[kept] = new double[Rows];
            Array.Copy(db, _keptDb[kept], Rows);
            _keptRead[kept] = _read - hop + window / 2;         // the middle of its sound

            // EACH NOTE ITS OWN QUIET LEVEL. Each row's level drops at once to anything quieter and creeps up
            // slowly, so it settles on that note's noise, and only a station stands out above it.
            if (_rowFloor == null) _rowFloor = (double[])db.Clone();
            if (_keptFloor[kept] == null) _keptFloor[kept] = new double[Rows];
            Array.Copy(_rowFloor, _keptFloor[kept], Rows);
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

        // The columns already made from the sample `from` on, drawn again as his side tone (see Feed).
        void RedrawAsSideTone(long from)
        {
            if (_rowFloor == null || _column == null) return;
            bool first = true;
            for (long c = Math.Max(0, _columnsMade - KeptColumns); c < _columnsMade; c++)
            {
                int kept = (int)(c % KeptColumns);
                if (_keptDb[kept] == null || _keptRead[kept] < from) continue;
                // The quiet levels as they were before his sending: the silence between his first elements had
                // pulled them down to nothing, and the band came back after it white (his screen, 2026-11-13).
                if (first && _keptFloor[kept] != null) Array.Copy(_keptFloor[kept], _rowFloor, Rows);
                first = false;
                TxColumn(_keptDb[kept], KeySideTone);
                int x = (int)(c % RingColumns);
                _bitmap.WritePixels(new Int32Rect(x, 0, 1, Rows), _column, 4, 0);
                _bitmap.WritePixels(new Int32Rect(x + RingColumns, 0, 1, Rows), _column, 4, 0);
            }
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

        // HIS SIDE TONE (key 3, see CwDecodeWindow.OneClearNote). Drawn as sound, its 644 Hz note at 5000 against
        // about 1000 receiving lit the whole strip white (his screen, 2026-11-13): the gaps between his elements are
        // silent, so every row's quiet level fell to nothing. So it gets our clean marks too, key down while his
        // note's rows stand TxKeyDownDb over their receiving level - on his recording dits 10 columns, dahs 24.
        // Only his note's rows: with any row, the band in the 0.6 s redrawn before his first element, and after
        // his last, drew one long mark (his screen, 2026-11-13).
        const byte KeySideTone = 3;

        bool SideToneDown(double[] db)
        {
            int row = (int)Math.Round((HighHz - _sideToneHz) / (HighHz - LowHz) * (Rows - 1));
            for (int y = Math.Max(0, row - 2); y <= Math.Min(Rows - 1, row + 2); y++)
                if (db[y] - _rowFloor[y] >= TxKeyDownDb) return true;
            return false;
        }

        void TxColumn(double[] db, byte key)
        {
            bool down = key == KeySideTone ? SideToneDown(db) : key != 0 ? key == 2 : WholeStripLoud(db);
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
            bool _hooked = true;
            public HostElement(HostVisual visual) { _visual = visual; AddVisualChild(visual); }
            public void Unhook() { if (!_hooked) return; _hooked = false; RemoveVisualChild(_visual); }
            protected override int VisualChildrenCount { get { return _hooked ? 1 : 0; } }
            protected override Visual GetVisualChild(int index) { return _visual; }
        }
    }

    // THE FILTER WATERFALL (his design, 2026-11-22, after MixW's picture). Down the right side of the CW window, the
    // whole height. Across: exactly what the radio's filter lets through - 400 Hz wide on a 400 Hz filter, 3 kHz on a
    // 3 kHz one - laid out as radio frequency, low on the left. Down: time, the newest at the top. A station inside
    // the filter is a bright stripe, and a click on it moves the radio so it sits on his pitch (the red line).
    //
    // Fed on the capture thread (an FFT every 40 ms, nothing beside the decoders), painted on the window's thread
    // from the lines that piled up, like the RTTY waterfall.
    sealed class CwFilterSpectrum
    {
        public const int Columns = 280;          // across: one per pixel of the panel
        public const int Rows = 900;             // down: 18 s at 50 lines a second - taller than any window
        const int LinesPerSecond = 50;           // one pixel every 20 ms: a glide, not steps
        const double MeasureTopHz = 4000;        // the sound is looked at up to here when measuring the filter

        public readonly WriteableBitmap Bitmap = new WriteableBitmap(Columns, Rows, 96, 96, PixelFormats.Bgr32, null);
        readonly int[] _pixels = new int[Columns * Rows];
        readonly System.Collections.Generic.Queue<int[]> _lines = new System.Collections.Generic.Queue<int[]>();
        static readonly int[] Palette = MakePalette();

        // MIXW'S COLOURS (his request, 2026-11-22, from his MixW screenshot): dark blue where there is nothing, the
        // band's noise speckled blue and green, a station yellow going to orange and red as it gets louder.
        // The level is set so the noise lands in the blue-green and a station 20-30 dB over it in orange-red
        // (see NoiseAt and DbAcross).
        const double NoiseAt = 0.32, DbAcross = 44;

        static int[] MakePalette()
        {
            var stops = new[]
            {
                new double[] { 0.00,   0,   0,  40 },   // nothing: dark navy
                new double[] { 0.20,   0,  40, 160 },   // blue
                new double[] { 0.35,   0, 150, 120 },   // the noise: blue-green
                new double[] { 0.50,  60, 200,  40 },   // green
                new double[] { 0.65, 230, 220,   0 },   // a station: yellow
                new double[] { 0.80, 255, 120,   0 },   // orange
                new double[] { 1.00, 230,   0,   0 },   // loud: red
            };
            var p = new int[256];
            for (int i = 0; i < 256; i++)
            {
                double t = i / 255.0;
                int k = 0;
                while (k < stops.Length - 2 && t > stops[k + 1][0]) k++;
                double w = (t - stops[k][0]) / (stops[k + 1][0] - stops[k][0]);
                int r = (int)(stops[k][1] + (stops[k + 1][1] - stops[k][1]) * w);
                int g = (int)(stops[k][2] + (stops[k + 1][2] - stops[k][2]) * w);
                int b = (int)(stops[k][3] + (stops[k + 1][3] - stops[k][3]) * w);
                p[i] = (r << 16) | (g << 8) | b;
            }
            return p;
        }

        // What is shown, as the NOTE heard (sound Hz), and which way round: set on the window's thread.
        // Flipped on the lower side (CW-R): there a higher note is a LOWER radio frequency, and the picture always
        // runs low to high radio frequency, left to right, like the radio's own scope.
        readonly object _spanGate = new object();
        double _lowHz = 400, _highHz = 1000;
        bool _flip;
        volatile bool _clear;

        // capture thread
        short[] _buf = new short[0];
        int _have, _rate, _lineCount;
        double[] _re, _im, _shape, _power;
        double _noise = double.NaN;

        // Power per FFT bin up to MeasureTopHz: about the last half second (to find the station clicked) and about
        // the last four (to measure the filter from the sound). Kept on the capture thread, read under the lock.
        readonly object _averageGate = new object();
        double[] _recent, _slow;
        double _binHz;

        // The filter as measured from the sound, in sound Hz; NaN until there is one.
        double _measuredLow = double.NaN, _measuredHigh = double.NaN;

        public CwFilterSpectrum()
        {
            for (int i = 0; i < _pixels.Length; i++) _pixels[i] = Palette[0];
            Bitmap.WritePixels(new Int32Rect(0, 0, Columns, Rows), _pixels, Columns * 4, 0);
        }

        /// <summary>On the window's thread: show lowHz..highHz of the sound. A new span starts a clean picture -
        /// lines drawn to the old one would put every station in the wrong place.</summary>
        public void SetSpan(double lowHz, double highHz, bool flip)
        {
            if (!(highHz > lowHz)) return;
            lock (_spanGate)
            {
                if (Math.Abs(lowHz - _lowHz) < 1 && Math.Abs(highHz - _highHz) < 1 && flip == _flip) return;
                _lowHz = lowHz; _highHz = highHz; _flip = flip;
            }
            _clear = true;
        }

        /// <summary>The filter's edges measured from the sound, in sound Hz. False until it could be measured.</summary>
        public bool MeasuredFilter(out double lowHz, out double highHz)
        {
            lock (_averageGate) { lowHz = _measuredLow; highHz = _measuredHigh; }
            return !double.IsNaN(lowHz) && !double.IsNaN(highHz);
        }

        /// <summary>Sound, on the capture thread.</summary>
        public void Feed(short[] samples, int count, int rate)
        {
            if (rate < 4000) return;
            if (rate != _rate) Setup(rate);
            int hop = rate / LinesPerSecond;
            for (int i = 0; i < count; i++)
            {
                _buf[_have++] = samples[i];
                if (_have == _buf.Length)
                {
                    MakeLine();
                    Array.Copy(_buf, hop, _buf, 0, _buf.Length - hop);
                    _have -= hop;
                }
            }
        }

        void Setup(int rate)
        {
            _rate = rate;
            // About a sixth of a second: 5.9 Hz a bin at 48 kHz, fine enough for a 300 Hz filter, short enough
            // that a dit still shows as a dash rather than a smear.
            int nfft = 1;
            while (nfft < rate / 8) nfft <<= 1;
            _buf = new short[nfft];
            _have = 0;
            _re = new double[nfft];
            _im = new double[nfft];
            _shape = new double[nfft];
            for (int i = 0; i < nfft; i++)
            {
                // Blackman-Harris, as in the RTTY waterfall: a loud station does not bleed sideways.
                double a = 2 * Math.PI * i / (nfft - 1);
                _shape[i] = 0.35875 - 0.48829 * Math.Cos(a) + 0.14128 * Math.Cos(2 * a) - 0.01168 * Math.Cos(3 * a);
            }
            _binHz = (double)rate / nfft;
            int top = Math.Min(nfft / 2, (int)(MeasureTopHz / _binHz) + 2);
            _power = new double[top];
            lock (_averageGate) { _recent = new double[top]; _slow = new double[top]; _measuredLow = _measuredHigh = double.NaN; }
            _noise = double.NaN;
            _lineCount = 0;
        }

        void MakeLine()
        {
            int n = _buf.Length;
            for (int i = 0; i < n; i++) { _re[i] = _buf[i] * _shape[i]; _im[i] = 0; }
            RttyWaterfall.Fft(_re, _im);
            int top = _power.Length;
            for (int b = 0; b < top; b++) _power[b] = _re[b] * _re[b] + _im[b] * _im[b];

            lock (_averageGate)
            {
                for (int b = 0; b < top; b++)
                {
                    // at 50 lines a second: about half a second, and about four
                    _recent[b] += 0.04 * (_power[b] - _recent[b]);
                    _slow[b] += 0.005 * (_power[b] - _slow[b]);
                }
            }
            if (++_lineCount % LinesPerSecond == 0) MeasureFilter();

            double low, high; bool flip;
            lock (_spanGate) { low = _lowHz; high = _highHz; flip = _flip; }
            double perColumn = (high - low) / Columns;
            var db = new double[Columns];
            for (int x = 0; x < Columns; x++)
            {
                // Column x, left to right in radio frequency; the note under it runs the other way when flipped.
                double from = flip ? high - (x + 1) * perColumn : low + x * perColumn;
                double p;
                if (perColumn >= _binHz)
                {
                    // Wide filter: the strongest bin under the column, so a narrow stripe is never lost.
                    int b0 = Clamp((int)Math.Round(from / _binHz), top), b1 = Clamp((int)Math.Round((from + perColumn) / _binHz) - 1, top);
                    p = 0;
                    for (int b = b0; b <= Math.Max(b0, b1); b++) p = Math.Max(p, _power[b]);
                }
                else
                {
                    // Narrow filter: more columns than bins, so between two bins the level is drawn in between.
                    double at = (from + perColumn / 2) / _binHz;
                    int b0 = Clamp((int)at, top), b1 = Clamp(b0 + 1, top);
                    double w = Math.Max(0, Math.Min(1, at - b0));
                    p = _power[b0] + (_power[b1] - _power[b0]) * w;
                }
                db[x] = 10 * Math.Log10(p + 1);
            }

            // One quiet level for the whole line: the middle of it, smoothed a little from line to line. Inside the
            // filter the noise is most of the picture and a station a narrow stripe, so the middle is the noise.
            var sorted = (double[])db.Clone();
            Array.Sort(sorted);
            double noise = sorted[sorted.Length / 2];
            _noise = double.IsNaN(_noise) ? noise : _noise + 0.2 * (noise - _noise);
            var line = new int[Columns];
            for (int x = 0; x < Columns; x++)
            {
                double v = NoiseAt + (db[x] - _noise) / DbAcross;
                line[x] = Palette[(int)(Math.Max(0, Math.Min(1, v)) * 255)];
            }
            lock (_lines)
            {
                _lines.Enqueue(line);
                while (_lines.Count > Rows) _lines.Dequeue();
            }
        }

        static int Clamp(int b, int top) { return b < 0 ? 0 : b >= top ? top - 1 : b; }

        // THE FILTER FROM THE SOUND, for a radio that cannot be asked: inside the filter there is the band's noise,
        // outside it next to nothing. So: the quietest tenth of 100-4000 Hz is "outside"; the longest stretch more
        // than 10 dB over that is the filter; and its edges are where the sound falls 6 dB under the middle of that
        // stretch - the same 6 dB edges the makers quote a filter's width by.
        void MeasureFilter()
        {
            double[] slow;
            lock (_averageGate) slow = (double[])_slow.Clone();
            int first = Math.Max(1, (int)(100 / _binHz)), last = slow.Length - 1;
            if (last - first < 20) return;
            var db = new double[slow.Length];
            for (int b = 0; b < slow.Length; b++) db[b] = 10 * Math.Log10(slow[b] + 1);
            var part = new double[last - first + 1];
            Array.Copy(db, first, part, 0, part.Length);
            Array.Sort(part);
            double outside = part[part.Length / 10];

            int runFrom, runTo;
            if (!LongestRunOver(db, first, last, outside + 10, out runFrom, out runTo)) { Forget(); return; }
            // The first stretch takes in the filter's skirts too, so its middle is too low and its 6 dB edges too
            // wide. Tightened again from the new middle until the edges stop moving (measured 2026-11-22 on a made-up
            // 400 Hz filter: once gave 700 Hz, tightened gives the 400).
            double middle = double.NaN;
            for (int round = 0; round < 8; round++)
            {
                var inside = new double[runTo - runFrom + 1];
                Array.Copy(db, runFrom, inside, 0, inside.Length);
                Array.Sort(inside);
                middle = inside[inside.Length / 2];
                if (middle - outside < 15) { Forget(); return; }      // no clear filter shape: noise all across, or silence
                int from, to;
                if (!LongestRunOver(db, first, last, middle - 6, out from, out to)) { Forget(); return; }
                bool same = from == runFrom && to == runTo;
                runFrom = from; runTo = to;
                if (same) break;
            }

            double lowHz = (runFrom - 0.5) * _binHz, highHz = (runTo + 0.5) * _binHz;
            if (highHz - lowHz < 40 || highHz - lowHz > 3800) { Forget(); return; }
            lock (_averageGate) { _measuredLow = lowHz; _measuredHigh = highHz; }
        }

        void Forget() { lock (_averageGate) { _measuredLow = _measuredHigh = double.NaN; } }

        // The longest run of bins over the level, a gap of up to 3 bins bridged (a hole in the noise is not an edge).
        static bool LongestRunOver(double[] db, int first, int last, double level, out int from, out int to)
        {
            from = to = -1;
            int start = -1, end = -1, gap = 0;
            for (int b = first; b <= last + 1; b++)
            {
                bool over = b <= last && db[b] > level;
                if (over)
                {
                    if (start < 0) start = b;
                    end = b; gap = 0;
                }
                else if (start >= 0 && (++gap > 3 || b > last))
                {
                    if (to < 0 || end - start > to - from) { from = start; to = end; }
                    start = -1; gap = 0;
                }
            }
            return from >= 0;
        }

        /// <summary>The note of the station nearest noteHz (within reachHz), standing at least 6 dB over the
        /// filter's noise in the last half second; NaN when there is none - then the click itself is used.</summary>
        public double FindStation(double noteHz, double reachHz)
        {
            double[] recent; double binHz; double low, high;
            lock (_averageGate) { if (_recent == null) return double.NaN; recent = (double[])_recent.Clone(); binHz = _binHz; }
            lock (_spanGate) { low = _lowHz; high = _highHz; }
            int top = recent.Length;
            int s0 = Clamp((int)(low / binHz), top), s1 = Clamp((int)(high / binHz), top);
            if (s1 - s0 < 2) return double.NaN;
            var span = new double[s1 - s0 + 1];
            Array.Copy(recent, s0, span, 0, span.Length);
            Array.Sort(span);
            double floor = span[span.Length / 2];

            int b0 = Clamp((int)((noteHz - reachHz) / binHz), top), b1 = Clamp((int)((noteHz + reachHz) / binHz), top);
            int best = -1;
            for (int b = b0; b <= b1; b++) if (best < 0 || recent[b] > recent[best]) best = b;
            if (best <= 0 || best >= top - 1 || recent[best] < 4 * floor) return double.NaN;
            // A real peak, not the side of a station just outside the reach: the strongest bin at the edge of the
            // reach with a stronger one beyond it is that other station's slope (found 2026-11-22 when the wrong-way
            // check took the side of a station 40 Hz off for one on the pitch).
            if ((best == b0 && recent[best - 1] > recent[best]) || (best == b1 && recent[best + 1] > recent[best])) return double.NaN;
            // The top of the peak between the bins, from the bin and its two neighbours.
            double l = Math.Log(recent[best - 1] + 1), c = Math.Log(recent[best] + 1), r = Math.Log(recent[best + 1] + 1);
            double d = l - 2 * c + r;
            double shift = d < 0 ? Math.Max(-0.5, Math.Min(0.5, 0.5 * (l - r) / d)) : 0;
            return (best + shift) * binHz;
        }

        // SMOOTH (his report, 2026-11-22: "not smooth enough"). The sound comes in 100 ms blocks, so the lines are
        // made 5 at a time, and they used to go on screen as they came - a jump of 5, then nothing. Now they are let
        // out one at a time on a steady clock, on every frame the screen draws (see the window's OnSpectrumFrame),
        // kept about LagLines behind the sound so there is always one ready; the clock runs up to a fifth faster or
        // slower to hold that distance, so it never stops and never jumps.
        //
        // AND THE PICTURE IS NEVER COPIED. It is a ring: each new line is written over the oldest row (Head), and
        // the window shows the bitmap twice, one above the other, moved so Head is at the top. One row is written
        // per line instead of the whole 280 x 900 picture.
        const double LagLines = LinesPerSecond * 0.15;
        double _credit;

        /// <summary>The row the newest line is in; the window puts this row at the top.</summary>
        public int Head { get; private set; }

        /// <summary>On the window's thread, every frame: lets out the lines due in `seconds`. True when the picture moved.</summary>
        public bool Paint(double seconds)
        {
            if (_clear)
            {
                // A new span: the lines made to the old one are thrown away with the old picture.
                _clear = false;
                lock (_lines) _lines.Clear();
                for (int i = 0; i < _pixels.Length; i++) _pixels[i] = Palette[0];
                Bitmap.WritePixels(new Int32Rect(0, 0, Columns, Rows), _pixels, Columns * 4, 0);
                _credit = 0;
                return true;
            }

            int waiting;
            lock (_lines)
            {
                // Far behind (the window was busy, or just opened): skip to the newest, keep the usual distance.
                while (_lines.Count > LinesPerSecond + LagLines) _lines.Dequeue();
                waiting = _lines.Count;
            }
            double ease = Math.Max(0.8, Math.Min(1.2, 1 + 0.2 * (waiting - LagLines) / LagLines));
            _credit += Math.Max(0, Math.Min(0.1, seconds)) * LinesPerSecond * ease;
            int due = (int)_credit;
            if (due == 0) return false;
            _credit -= due;
            if (waiting == 0) { _credit = Math.Min(_credit, 1); return false; }

            bool moved = false;
            for (int i = 0; i < due; i++)
            {
                int[] line;
                lock (_lines) { if (_lines.Count == 0) break; line = _lines.Dequeue(); }
                Head = (Head - 1 + Rows) % Rows;
                Bitmap.WritePixels(new Int32Rect(0, Head, Columns, 1), line, Columns * 4, 0);
                moved = true;
            }
            return moved;
        }
    }
}
