using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace HolyLogger
{
    /// <summary>
    /// The Radio Control Panel: a frequency box and twelve buttons - ten bands, SSB and CW.
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

        // Which of a band's two frequencies a band button uses. It follows the radio whenever the
        // radio is on SSB or CW; on any other mode (data, FM, AM) the last of the two is kept, so a
        // band button still has an answer.
        private string _mode = "SSB";

        // True while the box is showing the radio's own frequency. The first character typed wipes
        // that reading and starts a fresh number - nobody edits 14246.660 into 14250 digit by digit -
        // and until Enter is pressed, or the box is left, the radio no longer writes into it.
        private bool _boxShowsRadio = true;

        // Blue for SSB, red for CW. One brush, read by every lit button, so the band button and the
        // mode button always agree about which mode the panel is on.
        private static readonly System.Windows.Media.Brush SsbLit =
            new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x15, 0x65, 0xC0));
        private static readonly System.Windows.Media.Brush CwLit =
            new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD3, 0x2F, 0x2F));

        // Lamp colours. Red is the same red as a lit CW button, green is the panel's own.
        private static readonly System.Windows.Media.Brush TxLit =
            new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD3, 0x2F, 0x2F));
        private static readonly System.Windows.Media.Brush RxLit =
            new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x00, 0xA6, 0x50));

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

            var style = (Style)Resources["PanelToggleStyle"];

            // Rows 1-3 are nine bands; the fourth row is SSB, the tenth band, CW.
            for (int i = 0; i < 9 && i < _bands.Count; i++)
                ButtonGrid.Children.Add(MakeBandButton(_bands[i], style));

            _ssbButton = MakeModeButton("SSB", style);
            ButtonGrid.Children.Add(_ssbButton);

            if (_bands.Count > 9)
                ButtonGrid.Children.Add(MakeBandButton(_bands[9], style));
            else
                ButtonGrid.Children.Add(new Border());

            _cwButton = MakeModeButton("CW", style);
            ButtonGrid.Children.Add(_cwButton);
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

            if (string.Equals(mode, "SSB", StringComparison.OrdinalIgnoreCase)
                || string.Equals(mode, "CW", StringComparison.OrdinalIgnoreCase))
            {
                _mode = mode.ToUpperInvariant();
            }

            Resources["PanelLitBrush"] =
                string.Equals(_mode, "CW", StringComparison.OrdinalIgnoreCase) ? CwLit : SsbLit;

            _currentBand = rigOnline && khz > 0
                ? _bands.FirstOrDefault(b => b.Contains(khz))
                : null;

            foreach (var button in _bandButtons)
                button.IsChecked = rigOnline && ReferenceEquals(button.Tag, _currentBand);

            bool ssbLit = rigOnline && string.Equals(mode, "SSB", StringComparison.OrdinalIgnoreCase);
            bool cwLit = rigOnline && string.Equals(mode, "CW", StringComparison.OrdinalIgnoreCase);
            if (_ssbButton != null) _ssbButton.IsChecked = ssbLit;
            if (_cwButton != null) _cwButton.IsChecked = cwLit;

            foreach (var button in _bandButtons) button.IsEnabled = rigOnline;
            if (_ssbButton != null) _ssbButton.IsEnabled = rigOnline;
            if (_cwButton != null) _cwButton.IsEnabled = rigOnline;
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
            TxRxLabel.Text = _rigOnline ? "TX/RX" : "NO RADIO";

            // AND IT SAYS IT IN RED. A dark lamp is the absence of something, which is easy to look
            // straight past; the words in the theme's own red are not. TX/RX goes back to the plain
            // text colour - it is a heading over a working lamp, not a warning.
            //
            // SetResourceReference rather than a brush taken once: the panel is open across scheme
            // changes, and a colour copied out of the palette would keep the old scheme's red.
            TxRxLabel.SetResourceReference(TextBlock.ForegroundProperty, _rigOnline ? "TextBrush" : "Danger");
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

            // Mode and frequency travel together: asking for CW on 20m puts the radio on the 20m CW
            // frequency, not on CW where the SSB part of the band was.
            double khz = _currentBand != null ? _currentBand.FrequencyFor(mode) : _rigKhz;
            if (khz <= 0) return;

            _main.TuneRadioToKhz(khz, mode);
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
