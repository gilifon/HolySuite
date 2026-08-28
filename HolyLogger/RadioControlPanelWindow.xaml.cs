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

        private RadioBandPreset _currentBand;
        private bool _rigOnline;
        private double _rigKhz;

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
            _rigOnline = rigOnline;
            _rigKhz = khz;

            if (string.Equals(mode, "SSB", StringComparison.OrdinalIgnoreCase)
                || string.Equals(mode, "CW", StringComparison.OrdinalIgnoreCase))
            {
                _mode = mode.ToUpperInvariant();
            }

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

            if (!rigOnline)
            {
                Say("Radio not connected.");
                return;
            }

            // Nothing to say while the radio is answering: the box shows the frequency and the lit
            // buttons show the band and the mode. A line repeating all three is a line in the way.
            Say(null);

            // The box shows where the radio is, until it is typed in - to the same three decimals
            // as the LED on the main window, so the two never disagree about where the radio is.
            if (_boxShowsRadio && khz > 0)
                TB_Frequency.Text = khz.ToString("0.000", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// The one line of words in the panel. It is there only when something is wrong - no radio,
        /// or something typed that is not a frequency - and takes no room at all otherwise.
        /// </summary>
        private void Say(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                TB_Status.Text = string.Empty;
                TB_Status.Visibility = Visibility.Collapsed;
                return;
            }

            TB_Status.Text = message;
            TB_Status.Visibility = Visibility.Visible;
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
            _main.TuneFromRadioPanel(band.FrequencyFor(_mode), _mode);
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

            _main.TuneFromRadioPanel(khz, mode);
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
            if (!double.TryParse(typed, NumberStyles.Float, CultureInfo.InvariantCulture, out double khz) || khz <= 0)
            {
                Say("Type kHz only, for example 14250.");
                return;
            }

            // Handed back to the radio: from here the box follows the reading again, which is also
            // how the operator sees that the radio actually went where it was sent.
            _boxShowsRadio = true;
            _main.TuneFromRadioPanel(khz, _mode);
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
