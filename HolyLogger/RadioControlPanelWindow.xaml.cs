using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
// For the Run elements inside the TX/RX label - Foreground is TextElement's, not TextBlock's.
using System.Windows.Documents;
using System.Windows.Input;

namespace HolyLogger
{
    /// <summary>
    /// The Radio Control Panel: a frequency box, one button per band, and five mode buttons - SSB,
    /// CW, RTTY, AM and FM.
    ///
    /// IT NEVER DECIDES WHAT IS LIT. Pressing a button asks the radio to move; what the panel shows
    /// comes back from the radio itself, through MainWindow.UpdateRadioPanel. So turning the VFO on
    /// the radio to another band, or changing its mode there, lights the matching button here just
    /// the same as pressing it would - which is the whole point of the panel.
    /// </summary>
    public partial class RadioControlPanelWindow : Window
    {
        private readonly MainWindow _main;

        private List<RadioBandPreset> _bands;
        private readonly List<ToggleButton> _bandButtons = new List<ToggleButton>();
        private ToggleButton _ssbButton;
        private ToggleButton _cwButton;
        private ToggleButton _rttyButton;
        private ToggleButton _amButton;
        private ToggleButton _fmButton;

        // Every mode button, paired with the word BandButton_Click/ModeButton_Click and OmniRig know
        // it by. One list, read by ShowRigState, so lighting a sixth mode later is one line here
        // instead of another hand-written IsChecked/IsEnabled pair.
        private List<(ToggleButton Button, string Mode)> _modeButtons;

        // Which of a band's frequencies a band button uses. It follows the radio whenever the radio
        // is on SSB, CW or RTTY; on AM or FM the last of those three is kept, so a band button still
        // has an answer even though neither AM nor FM has a frequency of its own.
        private string _mode = "SSB";

        // True while the box is showing the radio's own frequency. The first character typed wipes
        // that reading and starts a fresh number - nobody edits 14246.660 into 14250 digit by digit -
        // and until Enter is pressed, or the box is left, the radio no longer writes into it.
        private bool _boxShowsRadio = true;

        // One colour per mode, and one brush shared by every lit button (see PanelLitBrush in the
        // XAML), so the band button and the mode button always agree about which mode the panel is
        // on - pressing RTTY paints the current band's own button the same yellow as the RTTY button.
        // Only ever shows on a CHECKED button: the style's IsChecked trigger is what paints
        // PanelLitBrush onto a button's face at all, so an unchecked button stays the plain ButtonBg
        // it always wore - none of these colours is visible except on the one button (or two, band
        // and mode together) for whichever mode the radio is actually on right now.
        private static readonly System.Windows.Media.Brush SsbLit =
            new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x15, 0x65, 0xC0));
        private static readonly System.Windows.Media.Brush CwLit =
            new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD3, 0x2F, 0x2F));
        // The exact colour the Cluster's own UHF band checkbox uses (Properties.Settings.Default.
        // ClusterBandColors, "UHF": "#5ECFFF") - asked for by eye, confirmed against the saved setting.
        private static readonly System.Windows.Media.Brush AmLit =
            new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x5E, 0xCF, 0xFF));
        // Same green as the RX lamp below (RxLit) - a colour this window already uses, rather than a
        // fourth green invented for the occasion.
        private static readonly System.Windows.Media.Brush FmLit =
            new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x00, 0xA6, 0x50));

        // Lamp colours. Red is the same red as a lit CW button, green is the panel's own.
        private static readonly System.Windows.Media.Color TxLitColor =
            System.Windows.Media.Color.FromRgb(0xD3, 0x2F, 0x2F);
        private static readonly System.Windows.Media.Color RxLitColor =
            System.Windows.Media.Color.FromRgb(0x00, 0xA6, 0x50);
        private static readonly System.Windows.Media.Brush TxLit =
            new System.Windows.Media.SolidColorBrush(TxLitColor);
        private static readonly System.Windows.Media.Brush RxLit =
            new System.Windows.Media.SolidColorBrush(RxLitColor);

        // SAME COLOUR, DIFFERENT AMOUNT OF IT. The lamp is a solid fill and reaches its colour
        // exactly. The two words above it are thin strokes, and most of a stroke's pixels are edges
        // blended into whatever is behind them - so the SAME brush comes out lighter in the letters
        // than in the bar. Rendered and measured: "RX" in #00A650 averages #2CB46E, a lighter and
        // yellower green, which is why the label and the lamp read as two greens while being one.
        //
        // So the words are given a colour that AVERAGES to the lamp's rather than one that equals
        // it. Coverage came out at about 0.83 of full for this font at this size and weight; the
        // remaining 0.17 of each pixel is the background, which is why this READS the background
        // instead of assuming it. The panel follows the colour scheme, and a compensation worked out
        // against the light one would be wrong against the dark one - and wrong the other way round,
        // too light instead of too dark.
        private const double GlyphCoverage = 0.83;

        private static System.Windows.Media.Color _wordBackground;
        private static readonly System.Collections.Generic.Dictionary<System.Windows.Media.Color, System.Windows.Media.Brush> _wordBrushes =
            new System.Collections.Generic.Dictionary<System.Windows.Media.Color, System.Windows.Media.Brush>();

        private System.Windows.Media.Brush WordThatReadsAs(System.Windows.Media.Color target)
        {
            System.Windows.Media.Color background;
            try { background = ThemeManager.Brush("WindowBg").Color; }
            catch (Exception swallowed) { Log.Swallow(swallowed); return new System.Windows.Media.SolidColorBrush(target); }

            // Worked out once per colour, not on every rig poll - but thrown away the moment the
            // scheme changes, because every answer in it was computed against the old background.
            if (background != _wordBackground)
            {
                _wordBackground = background;
                _wordBrushes.Clear();
            }

            System.Windows.Media.Brush cached;
            if (_wordBrushes.TryGetValue(target, out cached)) return cached;

            var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(
                Compensate(target.R, background.R),
                Compensate(target.G, background.G),
                Compensate(target.B, background.B)));
            brush.Freeze();
            _wordBrushes[target] = brush;
            return brush;
        }

        // rendered = coverage*ink + (1-coverage)*background, solved for the ink that renders as the
        // target. Clamped, because a target far enough from the background cannot be reached at all
        // through 83% coverage - there the honest answer is "as far as this channel goes".
        private static byte Compensate(byte target, byte background)
        {
            double ink = (target - (1.0 - GlyphCoverage) * background) / GlyphCoverage;
            return (byte)Math.Max(0, Math.Min(255, Math.Round(ink)));
        }

        private RadioBandPreset _currentBand;
        private bool _rigOnline;
        private double _rigKhz;

        // True transmitting, false receiving, null when the radio does not say. Some rigs' OmniRig
        // .ini files do not read the PTT line at all, and a green RX lamp invented for those would be
        // a reading the program does not have - so both lamps stay dark instead.
        private bool? _transmitting;

        public RadioControlPanelWindow(MainWindow main)
        {
            _main = main;
            InitializeComponent();
            BuildButtons();
            ShowRigState(false, 0, null);
            RestorePosition();

            // Typing is filtered by PreviewTextInput; a paste does not go through it, so it is caught
            // here as well - otherwise a right-click Paste could drop a callsign into the box.
            DataObject.AddPastingHandler(TB_Frequency, TB_Frequency_Pasting);
        }

        // ---- where the panel sits ----------------------------------------------------------
        //
        // It comes back where it was left, INCLUDING on a second monitor - which is why the saved
        // corner is checked against every screen the machine has (System.Windows.Forms.Screen), and
        // not against SystemParameters.WorkArea: that one only ever describes the primary screen, and
        // a panel left on the second one would be dragged back onto the first every time.
        private void RestorePosition()
        {
            double left = Properties.Settings.Default.RadioPanelWindowLeft;
            double top = Properties.Settings.Default.RadioPanelWindowTop;

            if (double.IsNaN(left) || double.IsNaN(top) || (left <= 0 && top <= 0))
            {
                WindowStartupLocation = WindowStartupLocation.CenterOwner;
                return;
            }

            if (!IsOnAScreen(left, top))
            {
                // The screen it was left on is gone (a laptop undocked, a monitor unplugged). Anywhere
                // it is visible beats a window nobody can reach.
                WindowStartupLocation = WindowStartupLocation.CenterOwner;
                return;
            }

            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }

        private static bool IsOnAScreen(double left, double top)
        {
            try
            {
                // A point just inside the title bar: enough of the window must be somewhere visible
                // for it to be grabbed and moved.
                var corner = new System.Drawing.Point((int)(left + 40), (int)(top + 10));
                foreach (var screen in System.Windows.Forms.Screen.AllScreens)
                {
                    if (screen.WorkingArea.Contains(corner)) return true;
                }
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }

            return false;
        }

        protected override void OnLocationChanged(EventArgs e)
        {
            base.OnLocationChanged(e);

            if (WindowState != WindowState.Normal) return;   // a minimized window reports -32000
            if (double.IsNaN(Left) || double.IsNaN(Top)) return;

            Properties.Settings.Default.RadioPanelWindowLeft = Left;
            Properties.Settings.Default.RadioPanelWindowTop = Top;

            // Same debounce every other window uses: dragging writes per pixel, and each Save()
            // rewrites user.config. One write shortly after the drag ends.
            SettingsFlush.RequestSave();
        }

        /// <summary>Rebuild the buttons after the frequencies were edited in Options.</summary>
        public void ReloadPresets()
        {
            BuildButtons();
            ShowRigState(_rigOnline, _rigKhz, _mode);
        }

        private void BuildButtons()
        {
            _bands = RadioPanelPresets.Load();
            _bandButtons.Clear();
            ButtonGrid.Children.Clear();
            ModeRow.Children.Clear();
            RttySlot.Children.Clear();
            FmSlot.Children.Clear();

            var style = (Style)Resources["PanelToggleStyle"];

            // Every band gets a button, in order, except the last one - that band sits alone in the
            // middle of its row, its left and right cells (Columns 0 and 2) left blank on purpose.
            // SSB, AM and CW are not in this grid at all - they fill ModeRow, just below the divider.
            for (int i = 0; i < _bands.Count - 1; i++)
                ButtonGrid.Children.Add(MakeBandButton(_bands[i], style));

            ButtonGrid.Children.Add(new Border());   // blank - left of the last band

            if (_bands.Count > 0)
                ButtonGrid.Children.Add(MakeBandButton(_bands[_bands.Count - 1], style));

            ButtonGrid.Children.Add(new Border());   // blank - right of the last band

            _ssbButton = MakeModeButton("SSB", style);
            ModeRow.Children.Add(_ssbButton);

            _amButton = MakeModeButton("AM", style);
            ModeRow.Children.Add(_amButton);

            _cwButton = MakeModeButton("CW", style);
            ModeRow.Children.Add(_cwButton);

            // RTTY and FM moved out of the button grid entirely, into the row shared with TX/RX - the
            // XAML's RttySlot/FmSlot flank the centred TX/RX block, in the space that was already
            // free there. Same shared style as everything else - PanelLitTextBrush (see ShowRigState)
            // is what keeps RTTY's text readable on its yellow, not a separate style.
            _rttyButton = MakeModeButton("RTTY", style);
            RttySlot.Children.Add(_rttyButton);

            _fmButton = MakeModeButton("FM", style);
            FmSlot.Children.Add(_fmButton);

            _modeButtons = new List<(ToggleButton, string)>
            {
                (_ssbButton, "SSB"),
                (_cwButton, "CW"),
                (_rttyButton, "RTTY"),
                (_amButton, "AM"),
                (_fmButton, "FM"),
            };
        }

        private ToggleButton MakeBandButton(RadioBandPreset band, Style style)
        {
            var text = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            // LineHeight, not the font's own: at 18 and 16 point the two lines carry about 8 points
            // of air between them and the same again above and below, which is what made the button
            // tall. Pinning the line box to just over the letters closes that gap without making a
            // single letter smaller.
            text.Children.Add(new TextBlock
            {
                Text = band.Label,
                FontSize = 18,
                LineHeight = 18,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                HorizontalAlignment = HorizontalAlignment.Center
            });
            text.Children.Add(new TextBlock
            {
                Text = band.Name,
                FontSize = 12,
                FontWeight = FontWeights.Normal,
                LineHeight = 12,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                // Three points of air under the band name: pinned to its line box it sat right on
                // the button's bottom edge and read as touching the frame.
                Margin = new Thickness(0, 0, 0, 3),
                HorizontalAlignment = HorizontalAlignment.Center
            });

            var button = new ToggleButton
            {
                Style = style,
                Content = text,
                Tag = band
            };
            button.Click += BandButton_Click;
            _bandButtons.Add(button);
            return button;
        }

        private ToggleButton MakeModeButton(string mode, Style style)
        {
            var button = new ToggleButton { Style = style, Content = mode, Tag = mode };
            button.Click += ModeButton_Click;
            return button;
        }

        // ---- what the radio is doing -------------------------------------------------------

        /// <summary>
        /// Called by MainWindow every time the radio reports anything. Everything the panel shows is
        /// decided here and nowhere else.
        /// </summary>
        public void ShowRigState(bool rigOnline, double khz, string mode)
        {
            ShowRigState(rigOnline, khz, mode, _transmitting);
        }

        public void ShowRigState(bool rigOnline, double khz, string mode, bool? transmitting)
        {
            _rigOnline = rigOnline;
            _rigKhz = khz;
            _transmitting = transmitting;
            ShowTxRx(rigOnline ? transmitting : null);

            if (_modeButtons != null && _modeButtons.Any(m => string.Equals(m.Mode, mode, StringComparison.OrdinalIgnoreCase)))
            {
                _mode = mode.ToUpperInvariant();
            }

            Resources["PanelLitBrush"] = BrushForMode(_mode);
            // White reads fine on every lit colour except RTTY's yellow, where the ordinary text
            // colour is what the main GUI itself uses over that same yellow on an edited QSO field,
            // and AM's blue, asked to keep black text regardless of theme.
            Resources["PanelLitTextBrush"] = TextBrushForMode(_mode);

            _currentBand = rigOnline && khz > 0
                ? _bands.FirstOrDefault(b => b.Contains(khz))
                : null;

            foreach (var button in _bandButtons)
                button.IsChecked = rigOnline && ReferenceEquals(button.Tag, _currentBand);

            if (_modeButtons != null)
            {
                foreach (var entry in _modeButtons)
                {
                    entry.Button.IsChecked = rigOnline && string.Equals(mode, entry.Mode, StringComparison.OrdinalIgnoreCase);
                    entry.Button.IsEnabled = rigOnline;
                }
            }

            // Not every radio covers every band this panel offers. A band unchecked on the Options
            // page stays disabled and grey no matter what the radio itself is doing - it never comes
            // back with the rig, the way every other button does.
            foreach (var button in _bandButtons)
            {
                var band = button.Tag as RadioBandPreset;
                button.IsEnabled = rigOnline && (band == null || band.Enabled);
            }
            TB_Frequency.IsEnabled = rigOnline;

            // A dead box marks nothing: the band goes out with the radio.
            if (!rigOnline) { ShowWheelZone(); return; }

            // The box shows where the radio is, until it is typed in - to the same three decimals
            // as the LED on the main window, so the two never disagree about where the radio is.
            if (_boxShowsRadio && khz > 0)
                TB_Frequency.Text = khz.ToString("0.000", CultureInfo.InvariantCulture);

            // The digits may have shifted under a mouse that never moved.
            ShowWheelZone();
        }

        /// <summary>
        /// The colour every lit button on the panel wears while the radio is on this mode.
        /// </summary>
        private static System.Windows.Media.Brush BrushForMode(string mode)
        {
            if (string.Equals(mode, "CW", StringComparison.OrdinalIgnoreCase)) return CwLit;
            // The same yellow the main GUI paints a QSO field with while it is being edited
            // (EditFieldBg - see ThemePalette), so RTTY reads as "this is what changed" the same way
            // an edited field does. Read live rather than cached once: a scheme change repaints it.
            if (string.Equals(mode, "RTTY", StringComparison.OrdinalIgnoreCase)) return ThemeManager.Brush("EditFieldBg");
            if (string.Equals(mode, "AM", StringComparison.OrdinalIgnoreCase)) return AmLit;
            if (string.Equals(mode, "FM", StringComparison.OrdinalIgnoreCase)) return FmLit;
            return SsbLit;   // SSB, and anything the radio reports that the panel does not know
        }

        /// <summary>
        /// The colour every lit button's TEXT wears for this mode - the counterpart to BrushForMode,
        /// since a colour bright enough to read well itself does not always leave white legible on it.
        /// </summary>
        private System.Windows.Media.Brush TextBrushForMode(string mode)
        {
            if (string.Equals(mode, "RTTY", StringComparison.OrdinalIgnoreCase))
                return (System.Windows.Media.Brush)FindResource("TextBrush");
            if (string.Equals(mode, "AM", StringComparison.OrdinalIgnoreCase))
                return System.Windows.Media.Brushes.Black;
            return System.Windows.Media.Brushes.White;
        }

        /// <summary>
        /// The one lamp under the TX/RX label: green while the radio listens, red while it transmits,
        /// dark when there is no radio or the radio does not report which it is doing.
        /// </summary>
        private void ShowTxRx(bool? transmitting)
        {
            if (transmitting == true) TxRxLamp.Background = TxLit;
            else if (transmitting == false) TxRxLamp.Background = RxLit;
            else TxRxLamp.Background = (System.Windows.Media.Brush)FindResource("ButtonBg");

            // With the window a fixed size there is no room for a line of explanation, so the label
            // itself carries it: a dark lamp over "NO RADIO" says why nothing in the panel answers.
            //
            // The label is three runs, not one string, so that each word wears its own state's
            // colour - TX in the same red the lamp goes when transmitting, RX in the same green it
            // goes when listening. The words are coloured always, not only when that state is
            // current: they are a key to the lamp, and a key that only appears once you already
            // know what the colour means is no key at all. Reusing TxLit/RxLit rather than a second
            // pair of literals is what keeps the two agreeing after anyone retunes either.
            if (_rigOnline)
            {
                TxWord.Text = "TX";
                TxRxSlash.Text = "/";
                RxWord.Text = "RX";

                // NOT TxLit/RxLit THEMSELVES - a colour that READS as them. See WordThatReadsAs.
                TxWord.Foreground = WordThatReadsAs(TxLitColor);
                RxWord.Foreground = WordThatReadsAs(RxLitColor);

                // Only the slash follows the theme - see below for why it is a resource reference.
                TxRxSlash.SetResourceReference(Run.ForegroundProperty, "TextBrush");
            }
            else
            {
                // AND IT SAYS IT IN RED. A dark lamp is the absence of something, which is easy to
                // look straight past; the words in the theme's own red are not. Emptying the other
                // two runs rather than hiding them keeps the lamp exactly as wide as whichever
                // wording is showing, which is the whole reason the label and lamp share a stack.
                TxWord.Text = "NO RADIO";
                TxRxSlash.Text = string.Empty;
                RxWord.Text = string.Empty;

                // SetResourceReference rather than a brush taken once: the panel is open across
                // scheme changes, and a colour copied out of the palette would keep the old
                // scheme's red.
                TxWord.SetResourceReference(Run.ForegroundProperty, "Danger");
            }
        }

        // ---- the operator presses something ------------------------------------------------

        private void BandButton_Click(object sender, RoutedEventArgs e)
        {
            var button = (ToggleButton)sender;
            var band = button.Tag as RadioBandPreset;

            // The press itself must not light the button: only the radio's own answer does. Put the
            // lamp back the way the radio last left it and let the reply move it.
            button.IsChecked = ReferenceEquals(band, _currentBand);

            if (band == null) return;
            _main.TuneRadioToKhz(band.FrequencyFor(_mode), _mode);
        }

        private void ModeButton_Click(object sender, RoutedEventArgs e)
        {
            var button = (ToggleButton)sender;
            string mode = (string)button.Tag;

            button.IsChecked = _rigOnline && string.Equals(mode, _mode, StringComparison.OrdinalIgnoreCase);

            _mode = mode;

            // SSB, CW and RTTY each have their own frequency for every band (the Options page), so
            // asking for CW on 20m puts the radio on the 20m CW frequency, not on CW where the SSB
            // part of the band was - and the same now holds for RTTY. AM and FM have no frequency of
            // their own to go with a band, so pressing either just changes the mode at wherever the
            // radio already is, rather than jumping to the SSB slot for a sub-band that is usually
            // somewhere else entirely.
            bool hasOwnFrequency = string.Equals(mode, "SSB", StringComparison.OrdinalIgnoreCase)
                || string.Equals(mode, "CW", StringComparison.OrdinalIgnoreCase)
                || string.Equals(mode, "RTTY", StringComparison.OrdinalIgnoreCase);
            double khz = hasOwnFrequency && _currentBand != null ? _currentBand.FrequencyFor(mode) : _rigKhz;
            if (khz <= 0) return;

            _main.TuneRadioToKhz(khz, mode);
        }

        // The gear beside the frequency box. What a band button sends the radio to is set on the
        // panel's own page in Options, so the gear opens Options already on that page rather than
        // leaving the operator to find it in the list.
        private void PanelSettingsBtn_Click(object sender, RoutedEventArgs e)
        {
            _main.OpenOptionsOnRadioPanelPage();
        }

        // ---- only a frequency can be typed into the box ------------------------------------
        //
        // Digits and one decimal point, nothing else: no letters, no spaces, no second point. The
        // check is made on what the box WOULD hold after the keystroke, so it also refuses a point
        // pasted or typed into the middle of a number that already has one.

        private void TB_Frequency_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            StartTypingIfShowingRadio();
            e.Handled = !StaysAFrequency(e.Text);
        }

        /// <summary>
        /// The first character typed (or pasted) empties the box, so the operator types the new
        /// frequency into an empty field instead of editing the radio's reading.
        /// </summary>
        private void StartTypingIfShowingRadio()
        {
            if (!_boxShowsRadio) return;

            _boxShowsRadio = false;
            TB_Frequency.Clear();
        }

        private void TB_Frequency_Pasting(object sender, DataObjectPastingEventArgs e)
        {
            string pasted = e.DataObject.GetDataPresent(DataFormats.UnicodeText)
                ? (string)e.DataObject.GetData(DataFormats.UnicodeText)
                : null;

            StartTypingIfShowingRadio();
            if (pasted == null || !StaysAFrequency(pasted)) e.CancelCommand();
        }

        private bool StaysAFrequency(string typed)
        {
            string text = TB_Frequency.Text ?? string.Empty;
            int start = TB_Frequency.SelectionStart;
            int length = TB_Frequency.SelectionLength;

            if (start > text.Length) start = text.Length;
            if (start + length > text.Length) length = text.Length - start;

            string after = text.Substring(0, start) + typed + text.Substring(start + length);
            return System.Text.RegularExpressions.Regex.IsMatch(after, @"^[0-9]*\.?[0-9]*$");
        }

        // ---- the wheel tunes ---------------------------------------------------------------
        //
        // A notch of the wheel over the box moves the radio one kHz, up or down. What that means for
        // an odd frequency, and for a fast spin, is decided in FrequencyWheel - the LED on the main
        // window turns the wheel through the same class, so the two behave identically.
        private readonly FrequencyWheel _wheel = new FrequencyWheel();

        private void TB_Frequency_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (!_rigOnline) return;   // nothing to tune: leave the wheel to whatever else wants it

            _zonePointerX = e.GetPosition(TB_Frequency).X;

            // Inside the band and no further: the edges of whatever band the radio is in now.
            var band = RadioPanelPresets.BandFor(_rigKhz);
            double lowKhz = band != null ? band.LowKhz : 0;
            double highKhz = band != null ? band.HighKhz : 0;

            double? target = _wheel.Next(_rigKhz, e.Delta,
                                         StepUnderPointer(_zonePointerX.Value), lowKhz, highKhz);
            if (target == null) return;

            e.Handled = true;   // the radio is being tuned: the wheel belongs to us for this notch

            // The box follows the radio again, and no mode is named: turning the dial is not a
            // request to change from data to SSB.
            _boxShowsRadio = true;
            TB_Frequency.Text = target.Value.ToString("0.000", CultureInfo.InvariantCulture);
            ShowWheelZone();   // the digits just moved under the pointer; the band moves with them

            // QueueWheelTune, not TuneRadioToKhz: the wheel's path writes the frequency and nothing
            // else - no mode, no readback poll - and sends at most one command per 50 ms however fast
            // the wheel is spun.
            _main.QueueWheelTune(target.Value);
        }

        /// <summary>
        /// x of the decimal point in the box, or null when there is not one on show. This is the one
        /// line that divides the kHz digits from the fraction: the wheel's step and the band that
        /// marks it both read it here, so what is lit is exactly what would move.
        /// </summary>
        private double? DotSplitX()
        {
            try
            {
                int dot = (TB_Frequency.Text ?? string.Empty).IndexOf('.');
                if (dot < 0) return null;

                return TB_Frequency.GetRectFromCharacterIndex(dot).Right;
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }

            return null;
        }

        /// <summary>
        /// 1 kHz with the pointer over the kHz digits, 0.1 kHz once it is past the decimal point -
        /// the box is read the way the radio's own dial is: the digit you are pointing at is the one
        /// that moves.
        /// </summary>
        private double StepUnderPointer(double x)
        {
            double? split = DotSplitX();
            return split != null && x > split.Value ? 0.1 : 1.0;
        }

        // ── WHICH HALF THE WHEEL WOULD MOVE ─────────────────────────────────────
        //
        // The step depends on where the pointer is, and nothing said so: the operator had to roll the
        // wheel to find out which half he was on. So the half under the pointer wears the yellow the
        // box itself used to wear - it lights while the mouse is over the box and goes out when the
        // mouse leaves, and it is the answer to "what does one notch do from here" before the notch.

        // Where the pointer was last seen inside the box, in the box's own coordinates, or null when
        // the mouse is not over it. Kept because the digits move under a still mouse: a reading comes
        // in from the radio, the text is re-centred, and the band has to be redrawn over what is now
        // standing there.
        private double? _zonePointerX;

        private void TB_Frequency_MouseMove(object sender, MouseEventArgs e)
        {
            _zonePointerX = e.GetPosition(TB_Frequency).X;
            ShowWheelZone();
        }

        private void TB_Frequency_MouseLeave(object sender, MouseEventArgs e)
        {
            _zonePointerX = null;
            ShowWheelZone();
        }

        /// <summary>
        /// Paints the band over the half of the number a notch of the wheel would move: the kHz
        /// digits with the pointer left of the point, the fraction with it right of the point.
        /// </summary>
        private void ShowWheelZone()
        {
            if (_zonePointerX == null || !_rigOnline || !TB_Frequency.IsEnabled)
            {
                FreqZoneMark.Visibility = Visibility.Collapsed;
                return;
            }

            try
            {
                string text = TB_Frequency.Text ?? string.Empty;
                if (text.Length == 0)
                {
                    FreqZoneMark.Visibility = Visibility.Collapsed;
                    return;
                }

                // The text may have been set a moment ago, in this same call stack: without this the
                // character rectangles are still the ones of the frequency before last.
                TB_Frequency.UpdateLayout();

                Rect first = TB_Frequency.GetRectFromCharacterIndex(0);
                Rect last = TB_Frequency.GetRectFromCharacterIndex(text.Length - 1, true);
                if (first.IsEmpty || last.IsEmpty)
                {
                    FreqZoneMark.Visibility = Visibility.Collapsed;
                    return;
                }

                // No point on show means the whole box is the one step there is, so the whole number
                // lights rather than half of a division that is not there.
                double? split = DotSplitX();
                bool fraction = split != null && _zonePointerX.Value > split.Value;

                double left = fraction ? split.Value : first.Left;
                double right = split == null ? last.Right : (fraction ? last.Right : split.Value);

                if (right - left <= 0)
                {
                    FreqZoneMark.Visibility = Visibility.Collapsed;
                    return;
                }

                Canvas.SetLeft(FreqZoneMark, left);
                Canvas.SetTop(FreqZoneMark, first.Top);
                FreqZoneMark.Width = right - left;
                FreqZoneMark.Height = first.Height;
                FreqZoneMark.Visibility = Visibility.Visible;
            }
            catch (Exception swallowed)
            {
                Log.Swallow(swallowed);
                FreqZoneMark.Visibility = Visibility.Collapsed;
            }
        }

        private void TB_Frequency_KeyDown(object sender, KeyEventArgs e)
        {
            if ((e.Key == Key.Back || e.Key == Key.Delete) && _boxShowsRadio)
            {
                StartTypingIfShowingRadio();
                e.Handled = true;
                return;
            }

            if (e.Key != Key.Enter) return;
            e.Handled = true;

            string typed = (TB_Frequency.Text ?? string.Empty).Trim();
            // Nothing but digits and one point can be in the box, so the only things left that do
            // not parse are an empty box or a bare point. Neither is a frequency; neither is worth a
            // message either.
            if (!double.TryParse(typed, NumberStyles.Float, CultureInfo.InvariantCulture, out double khz) || khz <= 0)
                return;

            // Handed back to the radio: from here the box follows the reading again, which is also
            // how the operator sees that the radio actually went where it was sent.
            _boxShowsRadio = true;
            _main.TuneRadioToKhz(khz, _mode);
        }

        /// <summary>
        /// Leaving the box without pressing Enter abandons what was typed: the radio was not moved,
        /// so the box goes back to showing where the radio actually is.
        /// </summary>
        private void TB_Frequency_LostFocus(object sender, RoutedEventArgs e)
        {
            _boxShowsRadio = true;
            ShowRigState(_rigOnline, _rigKhz, _mode);
        }
    }
}
