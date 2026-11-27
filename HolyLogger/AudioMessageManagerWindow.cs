using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace HolyLogger
{
    // The two small pictures that say where the SSB messages come from - drawn, not loaded, so they stay
    // sharp at the keycap's size and at the window's.
    internal static class AudioSourceIcons
    {
        // A monitor on a stand: the recordings on this computer.
        public static Path Pc(double scale, Brush ink)
        {
            return new Path
            {
                Data = Geometry.Parse("M0.5,0.5 H10.5 V7.5 H0.5 Z M5.5,7.5 V9.5 M3,10 H8"),
                Stroke = ink, StrokeThickness = 1, Fill = null,
                SnapsToDevicePixels = true,
                LayoutTransform = new ScaleTransform(scale, scale),
                ToolTip = "Plays your recording from this computer",
            };
        }

        // THE KEYCAP ITSELF, as the main window draws it (his request, 2026-11-25): the purple Msg key,
        // his name for it on top - or the key when he has not named it - and underneath the key at the
        // left with the source picture at the right. Shown in the Audio Messages window so each row
        // carries the very button it belongs to. A picture only: pressing it does nothing.
        public static Button Keycap(int n, string ownName, bool usePc)
        {
            string key = "F" + (n + 4);
            string name = (ownName ?? string.Empty).Trim();
            var ink = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
            ink.Freeze();

            var label = new TextBlock { Text = name.Length > 0 ? name : key, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center };
            var keyLine = new TextBlock
            {
                Text = key, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Left,
                Foreground = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
                Visibility = name.Length > 0 ? Visibility.Visible : Visibility.Collapsed
            };
            var holder = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
                Child = usePc ? Pc(1.0, ink) : Sd(1.0, ink)
            };
            var row = new Grid { Width = 36 };
            row.Children.Add(keyLine);
            row.Children.Add(holder);

            var face = new StackPanel { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            face.Children.Add(label);
            face.Children.Add(row);

            var button = new Button
            {
                Width = 48, Height = 36, Padding = new Thickness(0), Content = face,
                Focusable = false, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center
            };
            try { button.Style = (Style)Application.Current.FindResource("MsgButtonStyle"); }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
            return button;
        }

        // A memory card, its corner cut: the radio's own memory.
        public static Path Sd(double scale, Brush ink)
        {
            return new Path
            {
                Data = Geometry.Parse("M0.5,0.5 H6 L8.5,3 V10.5 H0.5 Z M2.5,1.5 V3.5 M4.5,1.5 V3.5"),
                Stroke = ink, StrokeThickness = 1, Fill = null,
                SnapsToDevicePixels = true,
                LayoutTransform = new ScaleTransform(scale, scale),
                ToolTip = "Plays the message stored in the radio",
            };
        }
    }

    // ── THE AUDIO MESSAGES WINDOW (his design, 2026-11-25) ─────────────────────────────────────────
    //
    // Everything needed to make the four SSB messages and send them, in one place: which microphone and
    // how loud, a live level, record / listen / delete for each message, which sound card goes to the
    // radio and at what level, and the one switch that says whether the Msg buttons play these
    // recordings or the radio's own memory.
    internal sealed class AudioMessageManagerWindow : Window
    {
        const string SystemDefault = "System default";
        const string ChooseOne = "(choose)";

        readonly Func<int, string> _buttonName;
        readonly Action<int> _rename;
        readonly Func<string> _radioMemoryNote;

        readonly RadioButton _rbPc, _rbRadio;
        readonly TextBlock _radioNote;
        readonly ComboBox _cbMic, _cbSpeakers, _cbRadio, _cbPttVia, _cbPttPort, _cbPttLine;
        readonly RowDefinition _pttPortRow;
        readonly UIElement _pttPortParts;
        readonly UIElement _comPortHelp;
        readonly Border _meterTrack;
        readonly Rectangle _meterBar;
        readonly TextBlock _meterWords;
        readonly Slider _gain, _radioLevel;
        readonly TextBlock _gainText, _radioLevelText, _status;
        readonly ContentControl[] _keycaps = new ContentControl[AudioMessages.Count + 1];
        readonly TextBlock[] _lengths = new TextBlock[AudioMessages.Count + 1];
        readonly Button[] _record = new Button[AudioMessages.Count + 1];
        readonly Button[] _play = new Button[AudioMessages.Count + 1];
        readonly Button[] _delete = new Button[AudioMessages.Count + 1];
        readonly TextBox[] _files = new TextBox[AudioMessages.Count + 1];

        WaveInRecorder _mic;
        WindowsMicLevel _micLevel;
        DateTime _micLevelReadUtc;
        readonly DispatcherTimer _tick;
        double _shown, _held;
        DateTime _heldAt = DateTime.UtcNow;

        readonly object _captureGate = new object();
        List<short> _captured;
        int _recordingN;                 // 0 = not recording
        int _captureRate;
        DateTime _recordStartUtc;

        AudioClipPlayer _player;
        int _playingN;

        bool _filling;

        const double MeterWidth = 260;

        internal AudioMessageManagerWindow(Func<int, string> buttonName, Action<int> rename, Func<string> radioMemoryNote)
        {
            _buttonName = buttonName;
            _rename = rename;
            _radioMemoryNote = radioMemoryNote;

            Title = "Audio Messages";
            SizeToContent = SizeToContent.WidthAndHeight;
            ResizeMode = ResizeMode.CanMinimize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            Background = ThemeManager.Brush("WindowBg");
            Foreground = ThemeManager.Brush("TextBrush");
            FontSize = 16;

            var page = new StackPanel { Margin = new Thickness(18, 12, 18, 14), MinWidth = 640 };
            _filling = true;

            // ── WHERE THE MSG BUTTONS PLAY FROM ─────────────────────────────────────────────────────
            page.Children.Add(Heading("The Msg buttons in SSB play", 0));
            _rbPc = SourceChoice(AudioSourceIcons.Pc(1.6, ThemeManager.Brush("TextBrush")), "My recordings on this computer");
            _rbRadio = SourceChoice(AudioSourceIcons.Sd(1.6, ThemeManager.Brush("TextBrush")), "The radio's own memory");
            _rbPc.IsChecked = AudioMessages.UsePc;
            _rbRadio.IsChecked = !AudioMessages.UsePc;
            _rbPc.Checked += (s, e) => { if (!_filling) AudioMessages.UsePc = true; };
            _rbRadio.Checked += (s, e) => { if (!_filling) AudioMessages.UsePc = false; };
            page.Children.Add(_rbPc);
            page.Children.Add(_rbRadio);
            _radioNote = Muted("");
            _radioNote.Margin = new Thickness(28, 0, 0, 0);
            page.Children.Add(_radioNote);

            // ── MICROPHONE ──────────────────────────────────────────────────────────────────────────
            page.Children.Add(Heading("Microphone", 14));
            var mic = NewGrid(3);
            _cbMic = new ComboBox { Width = 300, Height = 30, VerticalContentAlignment = VerticalAlignment.Center };
            AddRow(mic, 0, Label("Device"), _cbMic, new TextBlock());

            _meterBar = new Rectangle { Height = 14, Width = 0, HorizontalAlignment = HorizontalAlignment.Left, Fill = Brushes.SeaGreen };
            _meterTrack = new Border
            {
                Width = MeterWidth, Height = 18, BorderThickness = new Thickness(1), Padding = new Thickness(1),
                BorderBrush = ThemeManager.Brush("ThemeBorderBrush"), Background = Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
                Child = _meterBar
            };
            _meterWords = new TextBlock { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Width = 300, TextWrapping = TextWrapping.Wrap };
            AddRow(mic, 1, Label("Sound now"), _meterTrack, _meterWords);

            // WINDOWS' OWN MICROPHONE LEVEL (his choice, 2026-11-25) - the same number as Microphone
            // Properties > Levels, so there is one slider for it, not two. See WindowsMicLevel.
            _gain = new Slider { Minimum = 0, Maximum = 100, Width = 260, VerticalAlignment = VerticalAlignment.Center, IsSnapToTickEnabled = true, TickFrequency = 1 };
            _gainText = new TextBlock { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Width = 300, TextWrapping = TextWrapping.Wrap };
            AddRow(mic, 2, Label("Microphone level"), _gain, _gainText);
            page.Children.Add(mic);

            // ── THE FOUR MESSAGES ───────────────────────────────────────────────────────────────────
            page.Children.Add(Heading("Messages", 14));
            var rows = new Grid();
            for (int c = 0; c < 8; c++) rows.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            for (int n = 1; n <= AudioMessages.Count; n++)
            {
                int msg = n;
                rows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                _keycaps[n] = new ContentControl { Margin = new Thickness(0, 3, 10, 3), VerticalAlignment = VerticalAlignment.Center };
                // "Rename / Button" on two lines (his request, 2026-11-26) - it renames the keycap beside
                // it. Taller than the other buttons, by the same amount above and below, so the row's
                // buttons stay centred on one line.
                var renameBtn = SmallButton("Rename");
                renameBtn.Content = new TextBlock
                {
                    Text = "Rename\nButton", TextAlignment = TextAlignment.Left,   // both lines start together (his request)
                    LineStackingStrategy = LineStackingStrategy.BlockLineHeight, LineHeight = 18
                };
                renameBtn.Height = 42;
                renameBtn.Margin = new Thickness(0, 2, 0, 2);
                renameBtn.VerticalAlignment = VerticalAlignment.Center;
                renameBtn.Click += (s, e) => { if (_rename != null) _rename(msg); ShowNames(); };
                _record[n] = SmallButton("Record");
                _record[n].Background = RecordYellow;   // his colours, 2026-11-26
                _record[n].Click += (s, e) => RecordClicked(msg);
                _play[n] = SmallButton("Play");
                _play[n].Background = PlayLightRed;
                _play[n].Foreground = KeyInk;
                _play[n].Click += (s, e) => PlayClicked(msg);
                // CLEAR, NOT DELETE (2026-11-26): the file is his now, chosen by him and kept where he
                // put it, so this only takes it off the key. The file stays on the disk.
                _delete[n] = SmallButton("Clear");
                _delete[n].Background = ClearBlue;
                _delete[n].Foreground = Brushes.White;
                _delete[n].Click += (s, e) => ClearClicked(msg);
                _lengths[n] = new TextBlock { MinWidth = 62, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };

                // THE FILE THIS KEY PLAYS, and Browse to choose another (his design, 2026-11-26).
                _files[n] = new TextBox
                {
                    Width = 250, Height = 30, IsReadOnly = true, VerticalContentAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(6, 3, 0, 3), IsTabStop = false
                };
                var browse = new Button
                {
                    Content = FolderIcon(), Width = 36, Height = 30, Margin = new Thickness(4, 3, 0, 3),
                    ToolTip = "Choose the sound file for this key"
                };
                browse.Click += (s, e) => BrowseClicked(msg);

                Put(rows, _keycaps[n], n - 1, 0);
                Put(rows, renameBtn, n - 1, 1);
                Put(rows, _record[n], n - 1, 2);
                Put(rows, _play[n], n - 1, 3);
                Put(rows, _delete[n], n - 1, 4);
                Put(rows, _lengths[n], n - 1, 5);
                Put(rows, _files[n], n - 1, 6);
                Put(rows, browse, n - 1, 7);
            }
            page.Children.Add(rows);
            _status = Muted("Play sends the sound to your speakers, so you can hear it before it goes on the air.");
            _status.Margin = new Thickness(0, 6, 0, 0);
            page.Children.Add(_status);

            // ── WHERE IT PLAYS ──────────────────────────────────────────────────────────────────────
            page.Children.Add(Heading("Playing", 14));
            var outs = NewGrid(5);
            _cbSpeakers = new ComboBox { Width = 300, Height = 30, VerticalContentAlignment = VerticalAlignment.Center };
            AddRow(outs, 0, Label("Speakers (for Play)"), _cbSpeakers, new TextBlock());
            _cbRadio = new ComboBox { Width = 300, Height = 30, VerticalContentAlignment = VerticalAlignment.Center };
            AddRow(outs, 1, Label("Sound card to the radio"), _cbRadio, new TextBlock());
            _radioLevel = new Slider { Minimum = 0, Maximum = 100, Width = 260, VerticalAlignment = VerticalAlignment.Center, IsSnapToTickEnabled = true, TickFrequency = 1 };
            _radioLevelText = new TextBlock { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            AddRow(outs, 2, Label("Level to the radio"), _radioLevel, _radioLevelText);

            // HOW THE TRANSMITTER GOES ON - CAT, a COM port line, or the radio's own VOX.
            _cbPttVia = new ComboBox { Width = 300, Height = 30, VerticalContentAlignment = VerticalAlignment.Center };
            _cbPttVia.Items.Add(new ComboBoxItem { Content = "CAT", Tag = AudioMessages.PttCat });
            _cbPttVia.Items.Add(new ComboBoxItem { Content = "COM port (RTS or DTR)", Tag = AudioMessages.PttPort });
            _cbPttVia.Items.Add(new ComboBoxItem { Content = "The radio's VOX", Tag = AudioMessages.PttVox });
            AddRow(outs, 3, Label("Transmit by"), _cbPttVia, new TextBlock());
            _cbPttPort = new ComboBox { Width = 140, Height = 30, VerticalContentAlignment = VerticalAlignment.Center };
            _cbPttLine = new ComboBox { Width = 100, Height = 30, Margin = new Thickness(10, 0, 0, 0), VerticalContentAlignment = VerticalAlignment.Center };
            _cbPttLine.Items.Add("RTS");
            _cbPttLine.Items.Add("DTR");
            var portParts = new StackPanel { Orientation = Orientation.Horizontal };
            portParts.Children.Add(_cbPttPort);
            portParts.Children.Add(_cbPttLine);
            _pttPortParts = portParts;
            var portLabel = Label("Port and line");
            AddRow(outs, 4, portLabel, portParts, new TextBlock());
            _pttPortRow = outs.RowDefinitions[4];
            page.Children.Add(outs);
            // NEW RADIOS AND OLD ONES (his request, 2026-11-25). A radio with no USB sound - an FT-897, say -
            // takes the PC's sound through an interface box into its microphone jack; its DATA jack input
            // is documented for DIG mode only, and these buttons play in SSB.
            var radioHint = Muted("Radio with USB: set it to take its transmit sound from USB (a menu setting on the radio). "
                                + "Older radio: connect this sound card to the radio's microphone jack through an interface box. "
                                + "While a message plays, HolyLogger switches the radio to transmit as chosen in Transmit by.");
            radioHint.Margin = new Thickness(0, 6, 0, 0);
            page.Children.Add(radioHint);

            // THE COM PORT, DRAWN (his request, 2026-11-25) - shown only while Transmit by is COM port.
            _comPortHelp = ComPortPicture();
            page.Children.Add(_comPortHelp);

            var close = new Button { Content = "Close", MinWidth = 110, Height = 34, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 16, 0, 0) };
            close.Click += (s, e) => Close();
            page.Children.Add(close);

            Content = page;

            FillDevices();
            var cfg = AudioMessages.Settings;
            _radioLevel.Value = Math.Max(0, Math.Min(100, Math.Round(cfg.RadioLevel * 100)));
            ShowRadioLevel();
            FillPtt();
            ShowNames();
            ShowLengths();
            ShowRadioNote();
            _filling = false;

            _cbMic.SelectionChanged += (s, e) => { if (_filling) return; SaveDevices(); StartMic(); };
            _cbSpeakers.SelectionChanged += (s, e) => { if (!_filling) SaveDevices(); };
            _cbRadio.SelectionChanged += (s, e) => { if (!_filling) SaveDevices(); };
            _cbPttVia.SelectionChanged += (s, e) => { if (!_filling) SavePtt(); };
            _cbPttPort.SelectionChanged += (s, e) => { if (!_filling) SavePtt(); };
            _cbPttLine.SelectionChanged += (s, e) => { if (!_filling) SavePtt(); };
            _gain.ValueChanged += (s, e) =>
            {
                if (_filling) return;
                if (_micLevel != null) _micLevel.Set(_gain.Value / 100.0);
                ShowGain();
            };
            _radioLevel.ValueChanged += (s, e) =>
            {
                if (_filling) return;
                AudioMessages.Settings.RadioLevel = _radioLevel.Value / 100.0;
                AudioMessages.SaveSettings();
                ShowRadioLevel();
            };
            AudioMessages.SourceChanged += OnSourceChanged;

            _tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _tick.Tick += (s, e) => Tick();

            Loaded += (s, e) => { StartMic(); _tick.Start(); };

            // A RECORDING STILL RUNNING WHEN THE WINDOW GOES gets its Save As first - while the window is
            // still there to own that dialog (in Closed it is already gone).
            Closing += (s, e) =>
            {
                if (_recordingN != 0) FinishRecording();
            };
            Closed += (s, e) =>
            {
                AudioMessages.SourceChanged -= OnSourceChanged;
                _tick.Stop();
                lock (_captureGate) { _captured = null; }
                _recordingN = 0;
                StopMic();
                if (_micLevel != null) { _micLevel.Dispose(); _micLevel = null; }
                if (_player != null) _player.Stop();
            };

            WindowBounds.Attach(this, "AudioMessages");
        }

        /// <summary>Brings the given message's row to his attention - opened from a Msg button.</summary>
        internal void PointAt(int n)
        {
            if (n < 1 || n > AudioMessages.Count) return;
            try { _record[n].Focus(); } catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // ── BUILDING BLOCKS ─────────────────────────────────────────────────────────────────────────

        static TextBlock Heading(string text, double top)
        {
            return new TextBlock { Text = text, FontWeight = FontWeights.Bold, Margin = new Thickness(0, top, 0, 6) };
        }

        static TextBlock Label(string text)
        {
            // One width for every label in the window, so the boxes of both sections line up.
            return new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0), MinWidth = 196 };
        }

        static TextBlock Muted(string text)
        {
            return new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Left, Foreground = ThemeManager.Brush("MutedTextBrush") };
        }

        static Button SmallButton(string text)
        {
            return new Button { Content = text, MinWidth = 0, Height = 30, Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(6, 3, 0, 3) };
        }

        static RadioButton SourceChoice(UIElement icon, string text)
        {
            var face = new StackPanel { Orientation = Orientation.Horizontal };
            var holder = new Border { Width = 24, Child = icon, VerticalAlignment = VerticalAlignment.Center };
            face.Children.Add(holder);
            face.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0), Foreground = ThemeManager.Brush("TextBrush") });
            return new RadioButton { Content = face, GroupName = "AudioMessageSource", VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2, 0, 2) };
        }

        static Grid NewGrid(int rows)
        {
            var g = new Grid();
            for (int c = 0; c < 3; c++) g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            for (int r = 0; r < rows; r++) g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(38) });
            return g;
        }

        static void AddRow(Grid g, int row, UIElement a, UIElement b, UIElement c)
        {
            Put(g, a, row, 0);
            Put(g, b, row, 1);
            Put(g, c, row, 2);
        }

        static void Put(Grid g, UIElement e, int row, int col)
        {
            Grid.SetRow(e, row);
            Grid.SetColumn(e, col);
            g.Children.Add(e);
        }

        // ── DEVICES ─────────────────────────────────────────────────────────────────────────────────

        void FillDevices()
        {
            var cfg = AudioMessages.Settings;

            var inputs = new List<string> { SystemDefault };
            inputs.AddRange(WaveInRecorder.GetInputDeviceNames());
            _cbMic.ItemsSource = inputs;
            _cbMic.SelectedItem = Pick(inputs, cfg.InputDevice, SystemDefault);

            var outputs = WaveOutPlayer.GetOutputDeviceNames();
            var speakers = new List<string> { SystemDefault };
            speakers.AddRange(outputs);
            _cbSpeakers.ItemsSource = speakers;
            _cbSpeakers.SelectedItem = Pick(speakers, cfg.SpeakerDevice, SystemDefault);

            // NO "SYSTEM DEFAULT" FOR THE RADIO. On most stations the default is the speakers, and a
            // message played there while the transmitter is keyed sends a dead carrier.
            var radio = new List<string> { ChooseOne };
            radio.AddRange(outputs);
            if (!string.IsNullOrEmpty(cfg.RadioDevice) && WaveInRecorder.FindDevice(radio, cfg.RadioDevice) == null)
                radio.Add(cfg.RadioDevice);                  // switched off just now: keep his choice in sight
            _cbRadio.ItemsSource = radio;
            _cbRadio.SelectedItem = Pick(radio, cfg.RadioDevice, ChooseOne);
        }

        void FillPtt()
        {
            var cfg = AudioMessages.Settings;
            string via = AudioMessages.PttVia;
            foreach (ComboBoxItem item in _cbPttVia.Items)
                if ((string)item.Tag == via) _cbPttVia.SelectedItem = item;

            var ports = new List<string>();
            try { ports.AddRange(System.IO.Ports.SerialPort.GetPortNames()); }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
            ports.Sort(ComparePorts);
            if (!string.IsNullOrEmpty(cfg.PttPort) && !ports.Contains(cfg.PttPort)) ports.Add(cfg.PttPort);   // unplugged now: keep it in sight
            _cbPttPort.ItemsSource = ports;
            _cbPttPort.SelectedItem = string.IsNullOrEmpty(cfg.PttPort) ? null : cfg.PttPort;
            _cbPttLine.SelectedItem = string.Equals(cfg.PttLine, "DTR", StringComparison.OrdinalIgnoreCase) ? "DTR" : "RTS";
            ShowPttPortRow();
        }

        // COM3 before COM10.
        static int ComparePorts(string a, string b)
        {
            int na, nb;
            bool ha = a != null && a.StartsWith("COM", StringComparison.OrdinalIgnoreCase) && int.TryParse(a.Substring(3), out na);
            bool hb = b != null && b.StartsWith("COM", StringComparison.OrdinalIgnoreCase) && int.TryParse(b.Substring(3), out nb);
            if (ha && hb) { int.TryParse(a.Substring(3), out na); int.TryParse(b.Substring(3), out nb); return na.CompareTo(nb); }
            return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        }

        void SavePtt()
        {
            var cfg = AudioMessages.Settings;
            var item = _cbPttVia.SelectedItem as ComboBoxItem;
            cfg.PttVia = item != null ? (string)item.Tag : AudioMessages.PttCat;
            cfg.PttPort = _cbPttPort.SelectedItem as string ?? string.Empty;
            cfg.PttLine = _cbPttLine.SelectedItem as string ?? "RTS";
            AudioMessages.SaveSettings();
            AudioMessages.RaiseChanged();
            ShowPttPortRow();
        }

        void ShowPttPortRow()
        {
            bool port = AudioMessages.PttVia == AudioMessages.PttPort;
            _pttPortRow.Height = new GridLength(port ? 38 : 0);
            foreach (UIElement e in ((Grid)((FrameworkElement)_pttPortParts).Parent).Children)
                if (Grid.GetRow(e) == 4) e.Visibility = port ? Visibility.Visible : Visibility.Collapsed;
            if (_comPortHelp != null) _comPortHelp.Visibility = port ? Visibility.Visible : Visibility.Collapsed;
        }

        // ── THE 9-PIN COM PORT ───────────────────────────────────────────────────────────────────
        //
        // The PC's plug (a USB-to-serial adapter has the same one), seen from the front: pins 1-5 along
        // the top from the left, 6-9 along the bottom. The three that matter here are coloured - the RS-232
        // standard places RTS on 7, DTR on 4 and ground on 5 - with the words beside it.
        static readonly Brush RtsInk = Frozen(Color.FromRgb(0xC6, 0x28, 0x28));
        static readonly Brush DtrInk = Frozen(Color.FromRgb(0x15, 0x65, 0xC0));

        UIElement ComPortPicture()
        {
            var text = ThemeManager.Brush("TextBrush");
            var canvas = new Canvas { Width = 172, Height = 74 };
            canvas.Children.Add(new Path
            {
                Data = Geometry.Parse("M8,4 H164 L148,70 H24 Z"),
                Stroke = text, StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round,
                Fill = null
            });
            for (int pin = 1; pin <= 9; pin++)
            {
                double x = pin <= 5 ? 32 + (pin - 1) * 27 : 45.5 + (pin - 6) * 27;
                double y = pin <= 5 ? 24 : 50;
                Brush fill = pin == 7 ? RtsInk : pin == 4 ? DtrInk : pin == 5 ? text : null;
                var dot = new System.Windows.Shapes.Ellipse
                {
                    Width = 12, Height = 12, Stroke = fill ?? ThemeManager.Brush("MutedTextBrush"),
                    StrokeThickness = 1.5, Fill = fill
                };
                Canvas.SetLeft(dot, x - 6);
                Canvas.SetTop(dot, y - 6);
                canvas.Children.Add(dot);
            }

            var legend = new StackPanel { Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            legend.Children.Add(LegendLine(RtsInk, "Pin 7 - RTS"));
            legend.Children.Add(LegendLine(DtrInk, "Pin 4 - DTR"));
            legend.Children.Add(LegendLine(text, "Pin 5 - ground"));

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(canvas);
            row.Children.Add(legend);

            var box = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            box.Children.Add(row);
            box.Children.Add(Muted("The PC's COM port, seen from the front. The chosen line (RTS or DTR) switches a "
                                 + "transistor in the interface box that connects the radio's PTT to ground."));
            return box;
        }

        static UIElement LegendLine(Brush ink, string words)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };
            line.Children.Add(new System.Windows.Shapes.Ellipse { Width = 12, Height = 12, Fill = ink, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            line.Children.Add(new TextBlock { Text = words, VerticalAlignment = VerticalAlignment.Center });
            return line;
        }

        static string Pick(List<string> list, string saved, string fallback)
        {
            // Also under the new number Windows may have given the card (WaveInRecorder.FindDevice).
            return WaveInRecorder.FindDevice(list, saved) ?? fallback;
        }

        void SaveDevices()
        {
            var cfg = AudioMessages.Settings;
            cfg.InputDevice = Setting(_cbMic, SystemDefault);
            cfg.SpeakerDevice = Setting(_cbSpeakers, SystemDefault);
            cfg.RadioDevice = Setting(_cbRadio, ChooseOne);
            AudioMessages.SaveSettings();
        }

        static string Setting(ComboBox combo, string none)
        {
            string s = combo.SelectedItem as string;
            return string.IsNullOrEmpty(s) || s == none ? string.Empty : s;
        }

        // ── THE MICROPHONE, LISTENED TO WHILE THE WINDOW IS OPEN ───────────────────────────────────

        void StartMic()
        {
            StopMic();
            if (_recordingN != 0) FinishRecording();
            OpenMicLevel();
            try
            {
                var rec = new WaveInRecorder();
                rec.Samples += OnSamples;
                string error;
                if (!rec.Start(AudioMessages.Settings.InputDevice, AudioMessages.VoiceRates, out error))
                {
                    rec.Samples -= OnSamples;
                    _meterWords.Text = string.IsNullOrEmpty(error) ? "This device cannot be opened." : error;
                    _meterWords.Foreground = Brushes.Firebrick;
                    return;
                }
                rec.Failed += why => Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_recordingN != 0) FinishRecording();
                    _meterWords.Text = why;
                    _meterWords.Foreground = Brushes.Firebrick;
                }));
                _mic = rec;
                _shown = _held = 0;
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        void StopMic()
        {
            var rec = _mic;
            _mic = null;
            if (rec == null) return;
            try { rec.Samples -= OnSamples; rec.Stop(); }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // On the capture thread. The block array is reused, so it is copied.
        void OnSamples(short[] block, int count)
        {
            lock (_captureGate)
            {
                if (_captured == null) return;
                for (int i = 0; i < count; i++) _captured.Add(block[i]);
            }
        }

        static readonly Brush Good = Frozen(Color.FromRgb(0x2E, 0x9E, 0x44));
        static readonly Brush Weak = Frozen(Color.FromRgb(0xE0, 0x8A, 0x00));
        static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        void Tick()
        {
            var rec = _mic;
            if (rec != null)
            {
                double peak = Math.Min(1.0, rec.Level);
                _shown = peak > _shown ? peak : _shown * 0.82;
                var now = DateTime.UtcNow;
                if (peak >= _held || (now - _heldAt).TotalSeconds > 2) { _held = peak; _heldAt = now; }

                double db = 20 * Math.Log10(Math.Max(_shown, 1e-6));
                _meterBar.Width = Math.Max(0, Math.Min(1, (db + 60) / 60)) * (MeterWidth - 4);

                string words; Brush ink;
                if (_held < 0.01) { words = "No sound from the microphone"; ink = Brushes.Firebrick; }
                else if (_held < 0.08) { words = "Too weak - raise the microphone level"; ink = Weak; }
                else if (_held > 0.97) { words = "Too loud - lower the microphone level"; ink = Brushes.Firebrick; }
                else { words = "Good"; ink = Good; }
                _meterWords.Text = words;
                _meterWords.Foreground = ink;
                _meterBar.Fill = ink;
            }

            // Moved in Windows while this window is open: the slider follows. Not while he is dragging it.
            if (_micLevel != null && !_gain.IsMouseCaptureWithin && (DateTime.UtcNow - _micLevelReadUtc).TotalMilliseconds > 500)
            {
                _micLevelReadUtc = DateTime.UtcNow;
                ShowMicLevelFromWindows();
            }

            if (_recordingN != 0)
            {
                double secs = (DateTime.UtcNow - _recordStartUtc).TotalSeconds;
                _status.Text = "Recording " + Shown(_recordingN) + "... " + secs.ToString("0.0") + " s. Press Stop when done.";
                if (secs >= AudioMessages.MaxSeconds) FinishRecording();
            }
        }

        // ── RECORD / PLAY / DELETE ──────────────────────────────────────────────────────────────────

        void RecordClicked(int n)
        {
            if (_recordingN == n) { FinishRecording(); return; }
            if (_recordingN != 0) return;
            StopPlaying();

            if (_mic == null)
            {
                HolyMessageBox.ShowWarning("The microphone cannot be opened. Choose another device above.", "Audio Messages", this);
                return;
            }
            lock (_captureGate)
            {
                _captured = new List<short>(AudioMessages.MaxSeconds * 22050);
                _captureRate = _mic.ActualSampleRate;
            }
            _recordingN = n;
            _recordStartUtc = DateTime.UtcNow;
            ShowButtons();
        }

        void FinishRecording()
        {
            int n = _recordingN;
            _recordingN = 0;
            short[] samples;
            int rate;
            lock (_captureGate)
            {
                samples = _captured != null ? _captured.ToArray() : null;
                _captured = null;
                rate = _captureRate;
            }
            ShowButtons();
            if (n == 0) return;

            if (samples == null || samples.Length < rate / 4)
            {
                _status.Text = "Nothing was recorded.";
                return;
            }
            if (AudioMessages.Settings.TrimSilence) samples = AudioMessages.TrimSilence(samples, rate);

            // SAVE AS - HE NAMES IT AND PUTS IT WHERE HE WANTS (his design, 2026-11-26). Cancel drops the
            // recording, and the key keeps the file it had.
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save the recording of " + Shown(n),
                Filter = "Sound files (*.wav)|*.wav",
                DefaultExt = ".wav",
                AddExtension = true,
                OverwritePrompt = true,
                FileName = SuggestedName(n),
            };
            string folder = StartFolder(n);
            if (folder != null) dialog.InitialDirectory = folder;

            if (dialog.ShowDialog(this) != true)
            {
                _status.Text = "The recording was not kept.";
                return;
            }

            string path = dialog.FileName;
            if (_playingN != 0) StopPlaying();      // never write over a file that is playing
            if (!AudioMessages.Save(path, samples, rate))
            {
                _status.Text = "The recording could not be saved to " + path;
                return;
            }
            RememberFolder(path);
            AudioMessages.SetFile(n, path);
            _status.Text = Shown(n) + " recorded, " + (samples.Length / (double)rate).ToString("0.0") + " s. Press Play to hear it.";
            ShowLengths();
        }

        void BrowseClicked(int n)
        {
            if (_recordingN != 0) return;
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Choose the sound file for " + Shown(n),
                Filter = "Sound files (*.wav)|*.wav",
                CheckFileExists = true,
            };
            string folder = StartFolder(n);
            if (folder != null) dialog.InitialDirectory = folder;
            if (dialog.ShowDialog(this) != true) return;

            short[] samples; int rate;
            if (!AudioMessages.Load(dialog.FileName, out samples, out rate))
            {
                HolyMessageBox.ShowWarning("This file cannot be played.\n\nChoose a WAV file in 16-bit PCM.", "Audio Messages", this);
                return;
            }
            if (_playingN == n) StopPlaying();
            RememberFolder(dialog.FileName);
            AudioMessages.SetFile(n, dialog.FileName);
            _status.Text = Shown(n) + " now plays " + System.IO.Path.GetFileName(dialog.FileName) + ".";
            ShowLengths();
        }

        void ClearClicked(int n)
        {
            if (AudioMessages.FilePath(n).Length == 0) return;
            if (_playingN == n) StopPlaying();
            AudioMessages.SetFile(n, null);
            _status.Text = Shown(n) + " has no file now. The file itself was not deleted.";
            ShowLengths();
        }

        // The name Save As offers: the key's own name, or Msg1 to Msg4.
        string SuggestedName(int n)
        {
            string name = _buttonName != null ? (_buttonName(n) ?? string.Empty).Trim() : string.Empty;
            if (name.Length == 0) name = "Msg" + n;
            foreach (char c in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name + ".wav";
        }

        // Where Save As and Browse open: the folder of this key's file, else the last one he used, else
        // the program's own AudioMessages folder.
        static string StartFolder(int n)
        {
            try
            {
                string current = AudioMessages.FilePath(n);
                if (current.Length > 0)
                {
                    string dir = System.IO.Path.GetDirectoryName(current);
                    if (System.IO.Directory.Exists(dir)) return dir;
                }
                string last = AudioMessages.Settings.LastFolder;
                if (!string.IsNullOrEmpty(last) && System.IO.Directory.Exists(last)) return last;
                System.IO.Directory.CreateDirectory(AudioMessages.Folder);
                return AudioMessages.Folder;
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); return null; }
        }

        static void RememberFolder(string path)
        {
            try
            {
                AudioMessages.Settings.LastFolder = System.IO.Path.GetDirectoryName(path) ?? string.Empty;
                AudioMessages.SaveSettings();
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        void PlayClicked(int n)
        {
            if (_playingN == n) { StopPlaying(); return; }
            StopPlaying();
            if (_recordingN != 0) return;

            short[] samples; int rate;
            if (!AudioMessages.Load(n, out samples, out rate))
            {
                _status.Text = Shown(n) + " has no recording yet.";
                return;
            }
            string error;
            uint device = WaveOutPlayer.ResolveDeviceId(AudioMessages.Settings.SpeakerDevice);
            AudioClipPlayer player = null;
            player = AudioClipPlayer.Start(samples, rate, device, 1.0, 0, () => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_player != player) return;
                _player = null;
                _playingN = 0;
                ShowButtons();
            })), out error);
            if (player == null) { _status.Text = error; return; }
            _player = player;
            _playingN = n;
            ShowButtons();
        }

        void StopPlaying()
        {
            var p = _player;
            _player = null;
            _playingN = 0;
            if (p != null) p.Stop();
            ShowButtons();
        }

        // ── SHOWING ─────────────────────────────────────────────────────────────────────────────────

        string Shown(int n)
        {
            string name = _buttonName != null ? (_buttonName(n) ?? string.Empty).Trim() : string.Empty;
            return name.Length > 0 ? "Msg " + n + " (" + name + ")" : "Msg " + n;
        }

        void ShowNames()
        {
            for (int n = 1; n <= AudioMessages.Count; n++)
            {
                string name = _buttonName != null ? (_buttonName(n) ?? string.Empty).Trim() : string.Empty;
                _keycaps[n].Content = AudioSourceIcons.Keycap(n, name, AudioMessages.UsePc);
            }
        }

        void ShowLengths()
        {
            for (int n = 1; n <= AudioMessages.Count; n++)
            {
                double secs = AudioMessages.LengthSeconds(n);
                _lengths[n].Text = secs > 0 ? secs.ToString("0.0") + " s" : string.Empty;   // no file: the empty box says it
                _lengths[n].Foreground = secs > 0 ? ThemeManager.Brush("TextBrush") : ThemeManager.Brush("MutedTextBrush");

                // The file's name in the box, the whole path when the mouse rests on it. A chosen file
                // that is no longer there is shown in red, so he sees why the key will not play.
                string path = AudioMessages.FilePath(n);
                bool there = AudioMessages.HasRecording(n);
                _files[n].Text = path.Length == 0 ? string.Empty : System.IO.Path.GetFileName(path);
                _files[n].ToolTip = path.Length == 0 ? "No file - record one, or choose one with the folder button"
                                  : there ? path : path + "\n(the file is not there any more)";
                _files[n].Foreground = path.Length > 0 && !there ? Brushes.Firebrick : ThemeManager.Brush("TextBrush");
                if (path.Length > 0 && !there) _lengths[n].Text = "missing";
            }
            ShowButtons();
        }

        void ShowButtons()
        {
            for (int n = 1; n <= AudioMessages.Count; n++)
            {
                bool has = AudioMessages.HasRecording(n);
                bool recordingThis = _recordingN == n;
                _record[n].IsEnabled = _recordingN == 0 || recordingThis;
                _record[n].Content = recordingThis ? Face(StopIcon(), "Stop", true) : Face(MicIcon(), "Record", _record[n].IsEnabled);
                _record[n].Foreground = recordingThis ? Brushes.Firebrick : KeyInk;
                _play[n].IsEnabled = has && _recordingN == 0;
                _play[n].Content = _playingN == n ? Face(StopIcon(), "Stop", true) : Face(PlayIcon(), "Play", _play[n].IsEnabled);
                _delete[n].IsEnabled = AudioMessages.FilePath(n).Length > 0 && _recordingN == 0;
            }
        }

        // ── THE PICTURES ON RECORD AND PLAY (his request, 2026-11-25) ─────────────────────────────────
        //
        // A microphone on Record, a triangle on Play, a square on either while it runs - the signs every
        // recorder carries. Drawn, so they stay sharp; faded with the button when it cannot be pressed.
        static readonly Brush MicRed = Frozen(Color.FromRgb(0xC6, 0x28, 0x28));
        static readonly Brush PlayGreen = Frozen(Color.FromRgb(0x2E, 0x7D, 0x32));
        static readonly Brush StopInk = Frozen(Color.FromRgb(0x44, 0x44, 0x44));

        // THE THREE BUTTON COLOURS (his request, 2026-11-26): Record yellow, Play light red, Clear
        // white on blue. Fixed colours, so the writing on the first two is fixed dark too - the theme's
        // text colour is light in dark mode and would vanish on yellow.
        static readonly Brush RecordYellow = Frozen(Color.FromRgb(0xFF, 0xE6, 0x5C));
        static readonly Brush PlayLightRed = Frozen(Color.FromRgb(0xFF, 0x8A, 0x8A));
        static readonly Brush ClearBlue = Frozen(Color.FromRgb(0x15, 0x65, 0xC0));
        static readonly Brush KeyInk = Frozen(Color.FromRgb(0x22, 0x22, 0x22));

        static UIElement MicIcon()
        {
            var g = new Grid { Width = 12, Height = 16 };
            g.Children.Add(new Path { Data = Geometry.Parse("M4,0.5 H8 A2,2 0 0 1 10,2.5 V7.5 A2,2 0 0 1 8,9.5 H4 A2,2 0 0 1 2,7.5 V2.5 A2,2 0 0 1 4,0.5 Z"), Fill = MicRed });
            g.Children.Add(new Path { Data = Geometry.Parse("M0.75,6.5 V7.5 A5.25,5 0 0 0 11.25,7.5 V6.5 M6,12.5 V15 M3,15.25 H9"), Stroke = MicRed, StrokeThickness = 1.5 });
            return g;
        }

        // An open folder, for Browse.
        static UIElement FolderIcon()
        {
            return new Path
            {
                Data = Geometry.Parse("M0.5,2.5 H5.5 L7,4 H14.5 V13.5 H0.5 Z M0.5,6 H14.5"),
                Stroke = Frozen(Color.FromRgb(0x8D, 0x6E, 0x00)), StrokeThickness = 1.2,
                Fill = Frozen(Color.FromRgb(0xFF, 0xD5, 0x4F)), Width = 15, Height = 14,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
        }

        static UIElement PlayIcon()
        {
            return new Path { Data = Geometry.Parse("M0,0 L11,6.5 L0,13 Z"), Fill = PlayGreen, Width = 12, Height = 13 };
        }

        static UIElement StopIcon()
        {
            return new Path { Data = Geometry.Parse("M0,0 H11 V11 H0 Z"), Fill = StopInk, Width = 11, Height = 11 };
        }

        static UIElement Face(UIElement icon, string text, bool enabled)
        {
            var face = new StackPanel { Orientation = Orientation.Horizontal };
            if (icon is FrameworkElement fe) { fe.VerticalAlignment = VerticalAlignment.Center; fe.Margin = new Thickness(0, 0, 7, 0); }
            icon.Opacity = enabled ? 1.0 : 0.35;
            face.Children.Add(icon);
            face.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
            return face;
        }

        void ShowGain()
        {
            _gainText.Text = _micLevel != null ? Math.Round(_gain.Value).ToString() : "Windows does not offer a level for this device";
        }

        void OpenMicLevel()
        {
            if (_micLevel != null) { _micLevel.Dispose(); _micLevel = null; }
            _micLevel = WindowsMicLevel.Open(AudioMessages.Settings.InputDevice);
            _gain.IsEnabled = _micLevel != null;
            ShowMicLevelFromWindows();
        }

        void ShowMicLevelFromWindows()
        {
            double v = _micLevel != null ? _micLevel.Get() : -1;
            bool was = _filling;
            _filling = true;
            if (v >= 0) _gain.Value = Math.Round(v * 100);
            _filling = was;
            ShowGain();
        }
        void ShowRadioLevel() { _radioLevelText.Text = Math.Round(_radioLevel.Value) + "%"; }

        void ShowRadioNote()
        {
            string note = _radioMemoryNote != null ? _radioMemoryNote() : null;
            _radioNote.Text = note ?? string.Empty;
            _radioNote.Visibility = string.IsNullOrEmpty(note) ? Visibility.Collapsed : Visibility.Visible;
        }

        void OnSourceChanged()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _filling = true;
                _rbPc.IsChecked = AudioMessages.UsePc;
                _rbRadio.IsChecked = !AudioMessages.UsePc;
                _filling = false;
                ShowNames();          // the keycaps' picture follows the switch
            }));
        }
    }
}
