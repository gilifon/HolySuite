using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace HolyLogger
{
    /// <summary>
    /// Reads the RTTY being received. Built like the CW decoder window, so the two read as a pair:
    /// our own bar with the window's name, its keys and a level lamp; under it the waterfall; under
    /// that the decoded text, with callsigns picked out, checked on QRZ and ready to double-click
    /// into the DX Callsign box (the same text box the CW decoder uses - see CwDecodedText).
    ///
    /// THE WATERFALL RUNS TOP TO BOTTOM (his design, 2026-10-22), as RTTY programs show it: the
    /// newest sound along the top edge, 0 to 3000 Hz across - a radio's 2400 Hz SSB filter puts its
    /// audio at about 300-2700 Hz, so every station that can be heard is on it. A station is a pair of
    /// bright stripes 170 Hz apart. Clicking between the two stripes of a station tunes the decoder to
    /// it; two thin lines show the two tones it is listening to, and they follow the station as the
    /// fine tuning pulls in (see RttyDecoder).
    ///
    /// LIKE THE CW DECODER THERE IS NO LISTEN BUTTON: open, it is listening, on the input chosen for
    /// the decoders in Options > General.
    /// </summary>
    public class RttyDecodeWindow : Window
    {
        /// <summary>A callsign double-clicked in the text: the main window puts it in the DX Callsign box.</summary>
        public event Action<string> CallsignChosen;

        /// <summary>Set by the main window: sends the text by FSK; null when it is on its way, else why not.</summary>
        public Func<string, string> SendText;

        /// <summary>Set by the main window: stop sending now and back to receive (Esc).</summary>
        public Action StopSending;

        // The shift and speed of nearly all amateur RTTY. Not offered for change until somebody needs it.
        const double ShiftHz = 170, BaudRate = 45.45;

        // A pale amber bar: the CW pair wear pale cyan, and this one must not be taken for either of them.
        static readonly Brush BarBrush = Frozen(Color.FromRgb(0xFF, 0xE0, 0x9A));
        static readonly Brush GroupEdgeBrush = Frozen(Color.FromRgb(0x4A, 0x36, 0x06));
        static readonly Brush ReadoutBrush = Frozen(Color.FromRgb(0x0D, 0x47, 0xA1));
        static readonly Brush RevOnBrush = Frozen(Color.FromRgb(0xE6, 0x51, 0x00));
        static readonly Brush ToneLineBrush = Frozen(Color.FromRgb(0x40, 0xE0, 0x40));
        static readonly Brush QuietBrush = Frozen(Color.FromRgb(0x9E, 0x9E, 0x9E));
        static readonly Brush GoodBrush = Frozen(Color.FromRgb(0x2E, 0xA8, 0x4D));
        static readonly Brush LoudBrush = Frozen(Color.FromRgb(0xE8, 0x8A, 0x00));
        static readonly Brush TooLoudBrush = Frozen(Color.FromRgb(0xCC, 0x33, 0x33));
        static readonly Brush PaperInk = Frozen(Color.FromRgb(0x1E, 0x2A, 0x34));

        static Brush Frozen(Color colour)
        {
            var brush = new SolidColorBrush(colour);
            brush.Freeze();
            return brush;
        }

        readonly WaveInRecorder _recorder = new WaveInRecorder();
        readonly DispatcherTimer _screenTimer;
        readonly RttyWaterfall _waterfall = new RttyWaterfall();

        RttyDecoder _decoder;
        CwDecodedText _text;
        TextBlock _readout, _dial;
        Ellipse _lamp;
        Button _revBtn;
        System.Windows.Controls.Primitives.ToggleButton _snapSwitch;
        Canvas _overlay;
        Rectangle _lowLine, _highLine;
        Image _picture;
        double _shownLevel;

        // The tone pair he chose, as the lower tone in Hz (mark, or space when reversed).
        double _lowHz = 2125;
        bool _reverse;
        bool _snap = true;

        // A retune asked for on the window's thread, carried out on the capture thread between two
        // blocks of sound, so the decoder is never changed while it is working.
        volatile bool _retune;

        readonly StringBuilder _pending = new StringBuilder();
        readonly object _pendingGate = new object();

        public RttyDecodeWindow()
        {
            Title = "RTTY Decoder";
            Width = 780;
            try
            {
                double saved = Properties.Settings.Default.RttyWaterfallHeight;
                if (saved >= LowestWaterfall && saved <= RttyWaterfall.Rows) _waterfallHeight = saved;
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
            Height = 32 + RulerHeight + _waterfallHeight + 2 + SplitterHeight + 200 + SendRowHeight;
            // NO NARROWER THAN THE WHOLE BAR (his request, 2026-10-22): name, dial, lamp, tones, Snap, Rev,
            // Clear and the close cross. With the tones centred on the window, each side needs room for the
            // wider of the two outer groups: measured with the widest dial, 432,100.000 kHz, 2 x 257 + 141 =
            // 654, plus 6 for the window's edge; a little over that so the groups never touch.
            MinWidth = 700;
            // The bar, the ruler and the lowest the waterfall goes, plus a framed paper with one line of text.
            MinHeight = 32 + RulerHeight + LowestWaterfall + 2 + SplitterHeight + 52 + SendRowHeight;
            ResizeMode = ResizeMode.CanResize;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            WindowStyle = WindowStyle.None;
            SetResourceReference(BackgroundProperty, "WindowBg");
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

            try
            {
                var settings = Properties.Settings.Default;
                if (settings.RttyLowToneHz >= 300 && settings.RttyLowToneHz <= 2800) _lowHz = settings.RttyLowToneHz;
                _reverse = settings.RttyReverse;
                _snap = settings.RttySnap;
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }

            BuildContent();
            PaintRev();

            // THE ARROW KEYS NUDGE THE LINES (his request, 2026-10-22): the mouse gets them near a
            // station, the keys put them exactly on it - 1 Hz a press, 10 Hz with Shift, from where the
            // fine tuning has them now. Taken before the text box can use them to move its caret.
            PreviewKeyDown += (s, e) =>
            {
                // ESC STOPS SENDING, wherever the focus is - the one key that must always work.
                if (e.Key == Key.Escape && _transmitting)
                {
                    var stop = StopSending;
                    if (stop != null) stop();
                    e.Handled = true;
                    return;
                }
                if (e.Key != Key.Left && e.Key != Key.Right) return;
                // In the send line the arrows move the caret, as in any text box.
                if (Keyboard.FocusedElement is TextBox) return;
                if ((Keyboard.Modifiers & ~ModifierKeys.Shift) != 0) return;
                double step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 10 : 1;
                MoveLinesTo(ListeningLowHz() + (e.Key == Key.Right ? step : -step));
                Remember();
                e.Handled = true;
            };

            _recorder.Failed += OnRecorderFailed;
            _recorder.Samples += OnSamples;
            CwDecodeWindow.InputDeviceChanged += OnInputDeviceChanged;

            _screenTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(40) };
            _screenTimer.Tick += ScreenTimer_Tick;

            WindowBounds.Attach(this, "RttyDecode");
            Loaded += (s, e) => StartListening();
            Closing += OnClosing;
        }

        // ---- what is on screen ----

        const int RulerHeight = 24;

        // THE WATERFALL'S HEIGHT IS HIS (his request, 2026-10-22): the bar under it is dragged up and
        // down, and the height is kept for next time. Taller shows more of the past, not bigger lines:
        // the picture is always kept RttyWaterfall.Rows lines deep, newest at the top, and as much of
        // it is shown as there is room for.
        const int LowestWaterfall = 60;
        const int SplitterHeight = 7;
        double _waterfallHeight = 160;
        RowDefinition _waterfallRow;

        void BuildContent()
        {
            var bar = BuildTitleBar();

            // The frequency ruler, then the waterfall, as one framed picture.
            var ruler = BuildRuler();

            _picture = new Image
            {
                Source = _waterfall.Bitmap,
                Stretch = Stretch.Fill,
                Height = RttyWaterfall.Rows,
                VerticalAlignment = VerticalAlignment.Top,
                Cursor = Cursors.Cross,
                ToolTip = "Click between the two stripes of a station to read it, or drag the green lines onto them"
            };
            RenderOptions.SetBitmapScalingMode(_picture, BitmapScalingMode.LowQuality);
            _picture.MouseLeftButtonDown += OnWaterfallClick;
            _picture.MouseMove += OnWaterfallMove;
            _picture.MouseLeftButtonUp += OnWaterfallRelease;
            _picture.LostMouseCapture += (s, e) => Remember();

            _lowLine = ToneLine();
            _highLine = ToneLine();
            _overlay = new Canvas { IsHitTestVisible = false, ClipToBounds = true };
            _overlay.Children.Add(_lowLine);
            _overlay.Children.Add(_highLine);
            _overlay.SizeChanged += (s, e) => PlaceToneLines();

            var picture = new Grid { Background = Brushes.Black, ClipToBounds = true };
            picture.Children.Add(_picture);
            picture.Children.Add(_overlay);

            var stack = new DockPanel();
            DockPanel.SetDock(ruler, Dock.Top);
            stack.Children.Add(ruler);
            stack.Children.Add(picture);

            var waterfall = new Border
            {
                Child = stack,
                BorderThickness = new Thickness(1),
                BorderBrush = GroupEdgeBrush,
                Background = Brushes.Black,
                ClipToBounds = true
            };

            _text = MakeTextBox();
            var paper = new Border { BorderThickness = new Thickness(1), Margin = new Thickness(8, 2, 8, 4), Child = _text.Box };
            var sendRow = BuildSendRow();
            paper.SetResourceReference(Border.BorderBrushProperty, "MutedTextBrush");
            paper.Background = Brushes.White;

            // The bar between the waterfall and the text, to pull up and down - a grip in the middle
            // so it is plainly something to take hold of.
            var splitter = new GridSplitter
            {
                Height = SplitterHeight,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                ResizeDirection = GridResizeDirection.Rows,
                ResizeBehavior = GridResizeBehavior.PreviousAndNext,
                Background = Brushes.Transparent,
                Cursor = Cursors.SizeNS,
                ToolTip = "Drag to make the waterfall taller or shorter"
            };
            var grip = new FrameworkElementFactory(typeof(Border));
            grip.SetValue(Border.WidthProperty, 40.0);
            grip.SetValue(Border.HeightProperty, 3.0);
            grip.SetValue(Border.CornerRadiusProperty, new CornerRadius(1.5));
            grip.SetValue(Border.BackgroundProperty, GroupEdgeBrush);
            var gripRoom = new FrameworkElementFactory(typeof(Grid));
            gripRoom.SetValue(Panel.BackgroundProperty, Brushes.Transparent);
            gripRoom.AppendChild(grip);
            splitter.Template = new ControlTemplate(typeof(GridSplitter)) { VisualTree = gripRoom };
            splitter.DragCompleted += (s, e) => RememberWaterfallHeight();

            var body = new Grid();
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _waterfallRow = new RowDefinition
            {
                Height = new GridLength(RulerHeight + _waterfallHeight + 2),
                MinHeight = RulerHeight + LowestWaterfall + 2,
                MaxHeight = RulerHeight + RttyWaterfall.Rows + 2
            };
            body.RowDefinitions.Add(_waterfallRow);
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 52 });
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(bar, 0);
            Grid.SetRow(waterfall, 1);
            Grid.SetRow(splitter, 2);
            Grid.SetRow(paper, 3);
            Grid.SetRow(sendRow, 4);
            body.Children.Add(bar);
            body.Children.Add(waterfall);
            body.Children.Add(splitter);
            body.Children.Add(paper);
            body.Children.Add(sendRow);
            Content = body;
        }

        void RememberWaterfallHeight()
        {
            try
            {
                double h = _waterfallRow.ActualHeight - RulerHeight - 2;
                _waterfallHeight = Math.Max(LowestWaterfall, Math.Min(RttyWaterfall.Rows, Math.Round(h)));
                Properties.Settings.Default.RttyWaterfallHeight = _waterfallHeight;
                SettingsFlush.RequestSave();
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        static Rectangle ToneLine()
        {
            return new Rectangle { Width = 2, Fill = ToneLineBrush, Opacity = 0.85, Height = RttyWaterfall.Rows };
        }

        // 0 to 3000 Hz, a figure every 500 and a tick every 100, white on black like the picture under it.
        UIElement BuildRuler()
        {
            var canvas = new Canvas { Height = RulerHeight, Background = Brushes.Black, ClipToBounds = true };
            canvas.SizeChanged += (s, e) =>
            {
                canvas.Children.Clear();
                double w = canvas.ActualWidth;
                for (int hz = 0; hz <= RttyWaterfall.TopHz; hz += 100)
                {
                    double x = hz / RttyWaterfall.TopHz * w;
                    bool major = hz % 500 == 0;
                    var tick = new Rectangle { Width = 1, Height = major ? 7 : 4, Fill = Brushes.Gray };
                    Canvas.SetLeft(tick, x);
                    Canvas.SetTop(tick, RulerHeight - tick.Height);
                    canvas.Children.Add(tick);
                    if (!major || hz == 0 || hz == (int)RttyWaterfall.TopHz) continue;
                    var label = new TextBlock { Text = hz.ToString(), FontSize = 16, Foreground = Brushes.White };
                    label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    Canvas.SetLeft(label, x - label.DesiredSize.Width / 2);
                    Canvas.SetTop(label, -2);
                    canvas.Children.Add(label);
                }
            };
            return canvas;
        }

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
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = Brushes.White,
                Foreground = PaperInk
            };
            var decoded = new CwDecodedText(box) { LineEndsAsSent = true };
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

        Border BuildTitleBar()
        {
            var closeBtn = new Button
            {
                Content = "",   // the Segoe MDL2 cross, as on the CW decoder
                Width = 32,
                Style = Application.Current.Resources["CaptionCloseButtonStyle"] as Style,
                Foreground = Brushes.Black,
                ToolTip = "Close"
            };
            System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(closeBtn, true);
            closeBtn.Click += (s, e) => Close();

            var clearBtn = BarButton("Clear", "Empty the text");
            clearBtn.Click += (s, e) => ClearText();

            // SNAP ON OR OFF (his request, 2026-10-22): on, the lines jump onto the nearest station when the
            // mouse lets go of them; off, they stay exactly where they were put - for a weak station right
            // beside a loud one, which snapping would pull the lines away from. The word, then the same
            // on/off switch as everywhere else in the program (ToggleSwitchStyle, as the map's Auto Zoom).
            const string snapTip = "On: the lines jump onto the nearest station when you let go of them. Off: they stay exactly where you put them.";
            _snapSwitch = new System.Windows.Controls.Primitives.ToggleButton
            {
                IsChecked = _snap,
                VerticalAlignment = VerticalAlignment.Center,
                Style = Application.Current.Resources["ToggleSwitchStyle"] as Style,
                ToolTip = snapTip
            };
            System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(_snapSwitch, true);
            _snapSwitch.Click += (s, e) => { _snap = _snapSwitch.IsChecked == true; Remember(); };
            var snapLabel = new TextBlock
            {
                Text = "Snap",
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.Black,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
                ToolTip = snapTip
            };
            System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(snapLabel, true);
            var snapPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            snapPanel.Children.Add(snapLabel);
            snapPanel.Children.Add(_snapSwitch);

            _revBtn = BarButton("Rev", "Swap the two tones - for a station sending upside down, or the radio on USB instead of LSB");
            _revBtn.Click += (s, e) => { _reverse = !_reverse; PaintRev(); Remember(); _retune = true; };

            var keys = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            keys.Children.Add(_revBtn);
            keys.Children.Add(clearBtn);

            _readout = new TextBlock
            {
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Foreground = ReadoutBrush,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Left,
                ToolTip = "The two tones being read, as the fine tuning follows the station"
            };
            System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(_readout, true);

            _lamp = new Ellipse
            {
                Width = 12,
                Height = 12,
                Fill = QuietBrush,
                Stroke = GroupEdgeBrush,
                StrokeThickness = 1,
                VerticalAlignment = VerticalAlignment.Center,
                // 2 down: the tones are digits only, which sit in the lower part of their line, so centring
                // the dot on the line left it above the middle of the figures (his eye, 2026-10-22).
                Margin = new Thickness(0, 2, 6, 0),
                ToolTip = "The radio's sound level. Grey: no sound arriving. Green: a good level. Orange: loud. Red: too loud."
            };
            // The bar is the window's caption, where the mouse belongs to Windows (dragging the window) -
            // so without this the lamp never saw the mouse and its tooltip never showed (his question,
            // 2026-10-22: "what does the green dot mean?"). The same for the dial and the tones.
            System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(_lamp, true);

            // THE BAR (his layout, 2026-10-22): the name and the radio's dial at the left; the lamp and the
            // tones in the MIDDLE OF THE WINDOW, whatever its width; Snap and the keys always against the
            // close cross at the right. The middle is laid over the two sides rather than between them, so
            // it is centred on the window and not on whatever room the sides happen to leave.
            var middle = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            middle.Children.Add(_lamp);
            middle.Children.Add(_readout);
            var right = new StackPanel { Orientation = Orientation.Horizontal };
            DockPanel.SetDock(right, Dock.Right);
            right.Children.Add(snapPanel);
            var keysFrame = GroupFrame(keys);
            // Nearer the cross than the frame's usual 12 (his eye, 2026-10-22: "a little to the right").
            ((Border)keysFrame).Margin = new Thickness(0, 0, 4, 0);
            right.Children.Add(keysFrame);
            right.Children.Add(closeBtn);

            var titleText = new TextBlock
            {
                Text = "RTTY Decoder",
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.Black,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0),
                ToolTip = "What the other station is sending. It listens whenever this window is open."
            };
            DockPanel.SetDock(titleText, Dock.Left);


            // THE RADIO'S DIAL (his request, 2026-10-22), told by the main window as the radio reports it.
            _dial = new TextBlock
            {
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.Black,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(14, 0, 0, 0),
                ToolTip = "The radio's frequency"
            };
            System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(_dial, true);
            DockPanel.SetDock(_dial, Dock.Left);

            var sides = new DockPanel { LastChildFill = false };
            sides.Children.Add(right);
            sides.Children.Add(titleText);
            sides.Children.Add(_dial);

            var bar = new Grid();
            bar.Children.Add(sides);
            bar.Children.Add(middle);
            return new Border { Height = 32, Child = bar, Background = BarBrush };
        }

        static Button BarButton(string text, string tip)
        {
            var button = new Button
            {
                Content = text,
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.Black,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(7, 0, 7, 0),
                Margin = new Thickness(2, 0, 0, 0),
                Height = 24,
                Cursor = Cursors.Hand,
                ToolTip = tip
            };
            System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(button, true);
            return button;
        }

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

        void PaintRev()
        {
            if (_revBtn == null) return;
            _revBtn.Background = _reverse ? RevOnBrush : Brushes.Transparent;
            _revBtn.Foreground = _reverse ? Brushes.White : Brushes.Black;
        }

        /// <summary>The radio's dial in kHz, from the main window; 0 = no radio, and nothing is shown.</summary>
        public void ShowRadioKhz(double khz)
        {
            if (_dial == null) return;
            _dial.Text = khz > 0 ? khz.ToString("N3", System.Globalization.CultureInfo.InvariantCulture) + " kHz" : string.Empty;
        }

        // ---- tuning ----

        // TUNING BY MOUSE. A click between a station's two stripes puts the lines on it (the lower tone
        // half the shift below the click). And the lines can be DRAGGED (his request, 2026-10-22): press
        // on either line and it follows the mouse, its partner with it; press anywhere else and the
        // pair centres on the mouse and follows it the same way. Saved when the button is let go.
        //
        // How far from the press point the lower tone sits, in Hz, while dragging.
        double _dragFromLowHz;
        const double GrabPixels = 8;

        double MouseHz(MouseEventArgs e)
        {
            double w = _picture.ActualWidth;
            return w > 0 ? e.GetPosition(_picture).X / w * RttyWaterfall.TopHz : 0;
        }

        // Which line, if any, is under the mouse: -1 the lower, +1 the upper, 0 neither.
        int LineUnder(MouseEventArgs e)
        {
            double w = _picture.ActualWidth;
            if (w <= 0) return 0;
            double x = e.GetPosition(_picture).X, low = ListeningLowHz();
            if (Math.Abs(x - low / RttyWaterfall.TopHz * w) <= GrabPixels) return -1;
            if (Math.Abs(x - (low + ShiftHz) / RttyWaterfall.TopHz * w) <= GrabPixels) return 1;
            return 0;
        }

        void OnWaterfallClick(object sender, MouseButtonEventArgs e)
        {
            double hz = MouseHz(e);
            int line = LineUnder(e);
            _dragFromLowHz = line == -1 ? 0 : line == 1 ? -ShiftHz : -ShiftHz / 2;
            MoveLinesTo(hz + _dragFromLowHz);
            _picture.CaptureMouse();
            e.Handled = true;
        }

        void OnWaterfallMove(object sender, MouseEventArgs e)
        {
            if (_picture.IsMouseCaptured)
            {
                MoveLinesTo(MouseHz(e) + _dragFromLowHz);
                return;
            }
            // Over a line, the cursor says it can be pulled sideways.
            _picture.Cursor = LineUnder(e) != 0 ? Cursors.SizeWE : Cursors.Cross;
        }

        void OnWaterfallRelease(object sender, MouseButtonEventArgs e)
        {
            if (!_picture.IsMouseCaptured) return;
            _picture.ReleaseMouseCapture();
            // SNAP (his question, 2026-10-22): let go near a station and the lines land on its two
            // stripes, found within 60 Hz of where they were dropped.
            double snapped = _snap ? _waterfall.FindStation(_lowHz, ShiftHz, 60) : double.NaN;
            if (!double.IsNaN(snapped)) MoveLinesTo(snapped);
            Remember();
        }

        void MoveLinesTo(double lowHz)
        {
            _lowHz = Math.Max(300, Math.Min(RttyWaterfall.TopHz - ShiftHz, Math.Round(lowHz)));
            _retune = true;
            PlaceToneLines();
        }

        void Remember()
        {
            try
            {
                var settings = Properties.Settings.Default;
                settings.RttyLowToneHz = _lowHz;
                settings.RttyReverse = _reverse;
                settings.RttySnap = _snap;
                SettingsFlush.RequestSave();
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // Where the decoder is listening now: what he chose plus what the fine tuning has added.
        double ListeningLowHz()
        {
            if (_retune || _picture.IsMouseCaptured) return _lowHz;
            var d = _decoder;
            return d != null ? d.MarkHz + d.TuningOffsetHz : _lowHz;
        }

        void PlaceToneLines()
        {
            double w = _overlay.ActualWidth;
            if (w <= 0) return;
            double low = ListeningLowHz();
            Canvas.SetLeft(_lowLine, low / RttyWaterfall.TopHz * w - 1);
            Canvas.SetLeft(_highLine, (low + ShiftHz) / RttyWaterfall.TopHz * w - 1);
        }

        // ---- listening ----

        static string ChosenDevice()
        {
            try { return Properties.Settings.Default.CwDecodeInputDevice ?? string.Empty; }
            catch (Exception swallowed) { Log.Swallow(swallowed); return string.Empty; }
        }

        void OnInputDeviceChanged()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_recorder.IsRunning) return;
                StopListening();
                StartListening();
            }));
        }

        void StartListening()
        {
            if (_recorder.IsRunning) return;
            string error;
            if (!_recorder.Start(ChosenDevice(), out error))
            {
                ShowTrouble(error + "  Choose the device in Tools > Options > General.");
                return;
            }
            var decoder = new RttyDecoder(_recorder.ActualSampleRate);
            decoder.Reverse = _reverse;
            decoder.Tune(_lowHz, ShiftHz, BaudRate);
            decoder.CharacterDecoded += OnCharacter;
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
            if (decoder != null) decoder.CharacterDecoded -= OnCharacter;
            FlushPending();
            _shownLevel = 0;
            if (_lamp != null) _lamp.Fill = QuietBrush;
        }

        // On the capture thread: retune if asked, decode, and hand the sound to the waterfall.
        void OnSamples(short[] samples, int count)
        {
            var decoder = _decoder;
            if (decoder == null) return;
            if (_retune)
            {
                _retune = false;
                decoder.Reverse = _reverse;
                decoder.Tune(_lowHz, ShiftHz, BaudRate);
            }
            // NOT WHILE WE SEND: what comes back then is our own signal, already written in red.
            if (!_transmitting)
            {
                try { decoder.Process(samples, count); }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
            }
            try { _waterfall.Feed(samples, count, _recorder.ActualSampleRate); }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        void OnCharacter(char c)
        {
            lock (_pendingGate) _pending.Append(c);
        }

        void ScreenTimer_Tick(object sender, EventArgs e)
        {
            FlushPending();
            _waterfall.Paint();
            PlaceToneLines();
            UpdateLamp();
            double low = ListeningLowHz();
            _readout.Text = Math.Round(low).ToString("N0") + " / " + Math.Round(low + ShiftHz).ToString("N0") + " Hz";
            _readout.Foreground = _transmitting ? RevOnBrush : ReadoutBrush;
        }

        // Received and sent characters wait in one queue, in the order they happened; a sent one is
        // marked by SentMark in front of it.
        const char SentMark = '\uE000';

        void FlushPending()
        {
            string text;
            lock (_pendingGate) { text = _pending.ToString(); _pending.Clear(); }
            if (text.Length == 0 || _text == null) return;
            var received = new StringBuilder();
            var sent = new StringBuilder();
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == SentMark && i + 1 < text.Length)
                {
                    if (received.Length > 0) { _text.Append(received.ToString()); received.Clear(); }
                    sent.Append(text[++i]);
                }
                else
                {
                    if (sent.Length > 0) { _text.AppendSent(sent.ToString()); sent.Clear(); }
                    received.Append(text[i]);
                }
            }
            if (received.Length > 0) _text.Append(received.ToString());
            if (sent.Length > 0) _text.AppendSent(sent.ToString());
        }

        /// <summary>A character we have just sent - any thread. Written in red into the running text.</summary>
        public void ShowSent(char c)
        {
            lock (_pendingGate) { _pending.Append(SentMark); _pending.Append(c); }
        }

        volatile bool _transmitting;

        /// <summary>The main window: sending started or ended. The tones turn orange while we transmit.</summary>
        public void ShowTransmitting(bool on)
        {
            _transmitting = on;
            if (_sendBtn != null) _sendBtn.Content = on ? "Stop" : "Send";
        }

        // ---- the send line ----

        const int SendRowHeight = 40;
        TextBox _sendBox;
        Button _sendBtn;

        // A line to type in and a Send key: Enter sends the line, Esc stops. What goes out appears in red
        // in the text above as it is sent.
        UIElement BuildSendRow()
        {
            _sendBox = new TextBox
            {
                FontSize = 18,
                FontFamily = new FontFamily("Consolas"),
                VerticalContentAlignment = VerticalAlignment.Center,
                Height = 32,
                CharacterCasing = CharacterCasing.Upper,
                ToolTip = "Type what to send. Enter sends it, Esc stops."
            };
            _sendBox.KeyDown += (s, e) =>
            {
                if (e.Key != Key.Enter) return;
                SendTyped();
                e.Handled = true;
            };
            _sendBtn = new Button
            {
                Content = "Send",
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Width = 80,
                Height = 32,
                Margin = new Thickness(6, 0, 0, 0),
                ToolTip = "Send the line (Enter). While sending: stop (Esc)."
            };
            _sendBtn.Click += (s, e) =>
            {
                if (_transmitting) { var stop = StopSending; if (stop != null) stop(); }
                else SendTyped();
            };
            var row = new DockPanel { Margin = new Thickness(8, 0, 8, 8), Height = 32 };
            DockPanel.SetDock(_sendBtn, Dock.Right);
            row.Children.Add(_sendBtn);
            row.Children.Add(_sendBox);
            return row;
        }

        void SendTyped()
        {
            string line = _sendBox.Text;
            if (string.IsNullOrWhiteSpace(line)) return;
            var send = SendText;
            string why = send == null ? "Sending is not available." : send(line);
            if (why != null) { ShowTrouble(why); return; }
            _sendBox.Clear();
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

        void ClearText()
        {
            lock (_pendingGate) _pending.Clear();
            _text.Clear();
        }

        void ShowTrouble(string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            _text.Append("\n" + message + "\n");
        }

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
            CwDecodeWindow.InputDeviceChanged -= OnInputDeviceChanged;
            StopListening();
            try { _recorder.Dispose(); }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }
    }

    /// <summary>
    /// The RTTY waterfall: 0 to 3000 Hz across, time down the picture, the newest line at the top.
    /// The lines are worked out on the capture thread (one FFT every 40 ms) and only painted on the
    /// window's thread, so a busy window can delay the picture but never the decoding.
    /// </summary>
    sealed class RttyWaterfall
    {
        public const int Bins = 600;            // across: 5 Hz a pixel
        public const int Rows = 320;            // down: 12.8 seconds at 25 lines a second - the tallest it can be dragged
        public const double TopHz = 3000;
        const int LinesPerSecond = 25;

        public readonly WriteableBitmap Bitmap = new WriteableBitmap(Bins, Rows, 96, 96, PixelFormats.Bgr32, null);
        readonly int[] _pixels = new int[Bins * Rows];
        readonly Queue<int[]> _lines = new Queue<int[]>();
        static readonly int[] Palette = MakePalette();

        // capture thread
        short[] _buf = new short[0];
        int _have, _rate;
        double[] _re, _im, _shape;
        double _noise = double.NaN;

        // ---- snapping onto a station ----
        //
        // The power in every bin up to TopHz, averaged over about half a second - long enough that a
        // station's mark and space have both been sent several times, so both of its stripes are there.
        double[] _average;
        double _averageBinHz;
        readonly object _averageGate = new object();

        void KeepAverage(double binHz)
        {
            int bins = Math.Min(_re.Length / 2, (int)(TopHz / binHz) + 2);
            lock (_averageGate)
            {
                if (_average == null || _average.Length != bins || _averageBinHz != binHz) { _average = new double[bins]; _averageBinHz = binHz; }
                for (int b = 0; b < bins; b++)
                    _average[b] += 0.08 * (_re[b] * _re[b] + _im[b] * _im[b] - _average[b]);
            }
        }

        /// <summary>
        /// The lower tone of the station nearest lowGuessHz: the place within reachHz where the two
        /// tones shiftHz apart are BOTH strongest (their powers multiplied, so one loud stripe alone
        /// does not win). NaN when nothing there stands at least 6 dB over the band's middle level -
        /// no station to snap to, and the lines stay where he put them.
        /// </summary>
        public double FindStation(double lowGuessHz, double shiftHz, double reachHz)
        {
            double[] avg; double binHz;
            lock (_averageGate)
            {
                if (_average == null) return double.NaN;
                avg = (double[])_average.Clone();
                binHz = _averageBinHz;
            }
            Func<double, double> at = hz =>
            {
                double b = hz / binHz; int b0 = (int)b;
                if (b0 < 0 || b0 + 1 >= avg.Length) return 0;
                return avg[b0] + (avg[b0 + 1] - avg[b0]) * (b - b0);
            };
            double best = 0, bestHz = double.NaN;
            for (double f = lowGuessHz - reachHz; f <= lowGuessHz + reachHz; f += binHz / 2)
            {
                if (f < 100 || f + shiftHz > TopHz) continue;
                double score = Math.Sqrt(at(f) * at(f + shiftHz));
                if (score > best) { best = score; bestHz = f; }
            }
            int lo = (int)(300 / binHz), hi = Math.Min(avg.Length, (int)(2700 / binHz));
            if (hi - lo < 10 || double.IsNaN(bestHz)) return double.NaN;
            var band = new double[hi - lo];
            Array.Copy(avg, lo, band, 0, band.Length);
            Array.Sort(band);
            double middle = band[band.Length / 2];
            return best >= 4 * middle ? bestHz : double.NaN;
        }

        public RttyWaterfall()
        {
            for (int i = 0; i < _pixels.Length; i++) _pixels[i] = Palette[0];
            Bitmap.WritePixels(new Int32Rect(0, 0, Bins, Rows), _pixels, Bins * 4, 0);
        }

        /// <summary>Sound, on the capture thread.</summary>
        public void Feed(short[] samples, int count, int rate)
        {
            if (rate < 1000) return;
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
            int nfft = 1;
            while (nfft < rate / 4) nfft <<= 1;   // about a quarter second: a few Hz a bin
            _buf = new short[nfft];
            _have = 0;
            _re = new double[nfft];
            _im = new double[nfft];
            _shape = new double[nfft];
            for (int i = 0; i < nfft; i++)
            {
                // Blackman-Harris: a strong station does not bleed sideways across the picture.
                double a = 2 * Math.PI * i / (nfft - 1);
                _shape[i] = 0.35875 - 0.48829 * Math.Cos(a) + 0.14128 * Math.Cos(2 * a) - 0.01168 * Math.Cos(3 * a);
            }
            _noise = double.NaN;
        }

        void MakeLine()
        {
            int n = _buf.Length;
            for (int i = 0; i < n; i++) { _re[i] = _buf[i] * _shape[i]; _im[i] = 0; }
            Fft(_re, _im);
            double binHz = (double)_rate / n;
            KeepAverage(binHz);
            var db = new double[Bins];
            for (int x = 0; x < Bins; x++)
            {
                // The strongest FFT bin under this pixel, so a narrow stripe is never lost between pixels.
                int b0 = (int)(x * TopHz / Bins / binHz), b1 = Math.Max(b0, (int)((x + 1) * TopHz / Bins / binHz) - 1);
                double best = 0;
                for (int b = b0; b <= b1 && b < n / 2; b++) best = Math.Max(best, _re[b] * _re[b] + _im[b] * _im[b]);
                db[x] = 10 * Math.Log10(best + 1);
            }
            // ONE QUIET LEVEL FOR THE WHOLE BAND, NOT ONE PER FREQUENCY. The CW strip gives each note its
            // own, and that is right for CW, which is off half the time; an RTTY station never stops, so
            // a level of its own learned it as the noise and faded it out (seen on the bench, 2026-10-22).
            // The band's noise is the middle of this line's levels across the passband - stations take
            // a few narrow stripes, the noise all the rest - smoothed a little from line to line.
            int lo = (int)(300 / TopHz * Bins), hi = (int)(2700 / TopHz * Bins);
            var middle = new double[hi - lo];
            Array.Copy(db, lo, middle, 0, middle.Length);
            Array.Sort(middle);
            double noise = middle[middle.Length / 2];
            _noise = double.IsNaN(_noise) ? noise : _noise + 0.2 * (noise - _noise);
            var line = new int[Bins];
            for (int x = 0; x < Bins; x++)
            {
                double v = (db[x] - _noise - 3) / 30;
                line[x] = Palette[(int)(Math.Max(0, Math.Min(1, v)) * 255)];
            }
            lock (_lines)
            {
                _lines.Enqueue(line);
                while (_lines.Count > Rows) _lines.Dequeue();
            }
        }

        /// <summary>On the window's thread: the new lines in at the top, the rest moved down.</summary>
        public void Paint()
        {
            int[][] fresh;
            lock (_lines) { fresh = _lines.ToArray(); _lines.Clear(); }
            if (fresh.Length == 0) return;
            int k = Math.Min(fresh.Length, Rows);
            Array.Copy(_pixels, 0, _pixels, k * Bins, (Rows - k) * Bins);
            for (int i = 0; i < k; i++)
                Array.Copy(fresh[fresh.Length - 1 - i], 0, _pixels, i * Bins, Bins);
            Bitmap.WritePixels(new Int32Rect(0, 0, Bins, Rows), _pixels, Bins * 4, 0);
        }

        // Black for silence, dark blue for noise, yellow for a station, white for the loudest - the
        // same colours as the CW strip.
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
    }
}
