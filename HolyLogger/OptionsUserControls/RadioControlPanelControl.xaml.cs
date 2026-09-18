using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace HolyLogger.OptionsUserControls
{
    /// <summary>
    /// Options > Radio Control Panel: the frequencies behind each of the panel's band buttons, and
    /// whether the button is offered at all. The band list is fixed; the frequencies and the Select
    /// checkbox are the operator's to change.
    /// </summary>
    public partial class RadioControlPanelControl : UserControl
    {
        private List<RadioBandPreset> _bands;
        private readonly List<CheckBox> _enabledBoxes = new List<CheckBox>();
        private readonly List<TextBox> _ssbBoxes = new List<TextBox>();
        private readonly List<TextBox> _cwBoxes = new List<TextBox>();
        private readonly List<TextBox> _rttyBoxes = new List<TextBox>();

        /// <summary>True once anything here was edited, so the open panel can be rebuilt.</summary>
        public bool HasChanged { get; private set; }

        public RadioControlPanelControl()
        {
            InitializeComponent();
            BuildRows(RadioPanelPresets.Load());
        }

        // Two column-groups side by side (Select/Band/SSB/CW/RTTY, a gap, then the same five again),
        // rather than one twenty-row list running off the bottom of the screen. COLS is how many grid
        // columns one group takes, including the gap after it.
        private const int Cols = 6;

        private void BuildRows(List<RadioBandPreset> bands)
        {
            _bands = bands;
            // The standard frequencies, by band, so a box holding anything else can be marked.
            var standards = new Dictionary<string, RadioBandPreset>();
            foreach (var d in RadioPanelPresets.Defaults()) standards[d.Label] = d;
            _enabledBoxes.Clear();
            _ssbBoxes.Clear();
            _cwBoxes.Clear();
            _rttyBoxes.Clear();

            BandGrid.Children.Clear();
            BandGrid.RowDefinitions.Clear();
            BandGrid.ColumnDefinitions.Clear();

            // Select goes on the LEFT of each group, not the right: it is about the band's identity
            // (does this radio even have it), so it belongs beside the Band column rather than after
            // the frequencies. Two groups, so two of each column, with a gap between them.
            for (int g = 0; g < 2; g++)
            {
                BandGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(55) });
                BandGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
                BandGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
                BandGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
                // 92, not 80: the bold "RTTY (kHz)" header is wider than the others and 80 cut off
                // its ")".
                BandGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
                if (g == 0) BandGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            }

            // Rows per group: half the bands (rounded up), plus their own header row.
            int perGroup = (_bands.Count + 1) / 2;
            for (int r = 0; r <= perGroup; r++)
                BandGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            AddHeaderRow(0);
            AddHeaderRow(1);

            for (int i = 0; i < _bands.Count; i++)
            {
                var band = _bands[i];
                int group = i < perGroup ? 0 : 1;
                int row = (i % perGroup) + 1;
                int colBase = group * Cols;

                var enabled = new CheckBox
                {
                    IsChecked = band.Enabled,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 4, 8, 4),
                    ToolTip = "Offer this band's button on the Radio Control Panel"
                };
                enabled.Checked += EnabledBox_Changed;
                enabled.Unchecked += EnabledBox_Changed;
                Grid.SetRow(enabled, row);
                Grid.SetColumn(enabled, colBase + 0);
                BandGrid.Children.Add(enabled);
                _enabledBoxes.Add(enabled);

                var label = new TextBlock
                {
                    Text = band.Label + " MHz  (" + band.Name + ")",
                    FontSize = 16,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 4, 8, 4)
                };
                Grid.SetRow(label, row);
                Grid.SetColumn(label, colBase + 1);
                BandGrid.Children.Add(label);

                RadioBandPreset standard;
                standards.TryGetValue(band.Label, out standard);

                // 30m has no SSB by international band plan - CW and digital only - so there is no
                // frequency here to edit. A dash says so plainly rather than leaving the cell looking
                // like something failed to draw.
                bool noSsb = string.Equals(band.Name, "30m", StringComparison.OrdinalIgnoreCase);
                TextBox ssb = null;
                if (noSsb)
                {
                    var dash = new TextBlock
                    {
                        Text = "—",
                        FontSize = 16,
                        HorizontalAlignment = HorizontalAlignment.Left,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(0, 4, 8, 4)
                    };
                    dash.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
                    Grid.SetRow(dash, row);
                    Grid.SetColumn(dash, colBase + 2);
                    BandGrid.Children.Add(dash);
                }
                else
                {
                    ssb = MakeBox(band.SsbKhz, standard?.SsbKhz, row, colBase + 2);
                }
                var cw = MakeBox(band.CwKhz, standard?.CwKhz, row, colBase + 3);
                var rtty = MakeBox(band.RttyKhz, standard?.RttyKhz, row, colBase + 4);
                _ssbBoxes.Add(ssb);
                _cwBoxes.Add(cw);
                _rttyBoxes.Add(rtty);
            }
        }

        private void AddHeaderRow(int group)
        {
            int colBase = group * Cols;

            var select = new TextBlock { Text = "Select", FontSize = 16, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 8, 4) };
            Grid.SetRow(select, 0);
            Grid.SetColumn(select, colBase + 0);
            BandGrid.Children.Add(select);

            var band = new TextBlock { Text = "Band", FontSize = 16, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 8, 4) };
            Grid.SetRow(band, 0);
            Grid.SetColumn(band, colBase + 1);
            BandGrid.Children.Add(band);

            var ssb = new TextBlock { Text = "SSB (kHz)", FontSize = 16, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 8, 4) };
            Grid.SetRow(ssb, 0);
            Grid.SetColumn(ssb, colBase + 2);
            BandGrid.Children.Add(ssb);

            var cw = new TextBlock { Text = "CW (kHz)", FontSize = 16, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 8, 4) };
            Grid.SetRow(cw, 0);
            Grid.SetColumn(cw, colBase + 3);
            BandGrid.Children.Add(cw);

            var rtty = new TextBlock { Text = "RTTY (kHz)", FontSize = 16, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 8, 4) };
            Grid.SetRow(rtty, 0);
            Grid.SetColumn(rtty, colBase + 4);
            BandGrid.Children.Add(rtty);
        }

        // A frequency that is not the standard one gets a light red box, so it is seen at a glance
        // which ones were changed. Checked on every keystroke, and again when a bad entry is put back.
        private static readonly System.Windows.Media.Brush ChangedBrush =
            new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xD6, 0xD6));

        private static void MarkIfNotStandard(TextBox box)
        {
            int? standard = box.Tag as int?;
            bool isStandard = standard.HasValue
                && int.TryParse((box.Text ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int khz)
                && khz == standard.Value;

            if (isStandard || !standard.HasValue) box.ClearValue(Control.BackgroundProperty);
            else box.Background = ChangedBrush;
        }

        private TextBox MakeBox(int khz, int? standardKhz, int row, int column)
        {
            var box = new TextBox
            {
                Text = khz.ToString(CultureInfo.InvariantCulture),
                FontSize = 16,
                Height = 28,
                // 70, not the original 120: five digits (the widest any of these frequencies runs to)
                // measure under 55px at this font size, and 120 left most of the box empty.
                Width = 70,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 4, 8, 4),
                Tag = standardKhz
            };
            MarkIfNotStandard(box);
            box.TextChanged += (s, e) => MarkIfNotStandard(box);
            box.LostFocus += Box_LostFocus;
            Grid.SetRow(box, row);
            Grid.SetColumn(box, column);
            BandGrid.Children.Add(box);
            return box;
        }

        private void Box_LostFocus(object sender, RoutedEventArgs e)
        {
            SaveAll();
        }

        // Unlike the frequency boxes, a checkbox has no meaningful "still typing" state to wait
        // out - the click IS the finished edit - so this saves immediately rather than on LostFocus.
        private void EnabledBox_Changed(object sender, RoutedEventArgs e)
        {
            SaveAll();
        }

        /// <summary>
        /// Reads every box back into the band list and stores it. A box that does not hold a whole
        /// number of kHz is put back to the value it had, rather than saved as nothing - and so is one
        /// outside its band's edges (the same edges that light the band button on the panel).
        /// </summary>
        public void SaveAll()
        {
            bool changed = false;
            string rejected = null;

            for (int i = 0; i < _bands.Count; i++)
            {
                bool wantEnabled = _enabledBoxes[i].IsChecked == true;
                if (wantEnabled != _bands[i].Enabled)
                {
                    _bands[i].Enabled = wantEnabled;
                    changed = true;
                }

                // No box at all for 30m (see BuildRows) - nothing to read back for it.
                if (_ssbBoxes[i] != null)
                    changed |= ReadBox(_ssbBoxes[i], _bands[i], value => _bands[i].SsbKhz = value, _bands[i].SsbKhz, ref rejected);
                changed |= ReadBox(_cwBoxes[i], _bands[i], value => _bands[i].CwKhz = value, _bands[i].CwKhz, ref rejected);
                changed |= ReadBox(_rttyBoxes[i], _bands[i], value => _bands[i].RttyKhz = value, _bands[i].RttyKhz, ref rejected);
            }

            // The message stays until the next save that rejects nothing, so it is still there to
            // read after focus has moved on to the next box.
            if (rejected != null)
            {
                TB_OutOfBand.Text = rejected;
                TB_OutOfBand.Visibility = Visibility.Visible;
            }
            else
            {
                TB_OutOfBand.Visibility = Visibility.Collapsed;
            }

            if (!changed) return;

            RadioPanelPresets.Save(_bands);
            HasChanged = true;
        }

        private static bool ReadBox(TextBox box, RadioBandPreset band, Action<int> set, int current, ref string rejected)
        {
            string typed = (box.Text ?? string.Empty).Trim();
            if (int.TryParse(typed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int khz) && khz > 0)
            {
                if (!band.Contains(khz))
                {
                    rejected = khz.ToString(CultureInfo.InvariantCulture) + " is outside the " + band.Name + " band ("
                        + band.LowKhz.ToString(CultureInfo.InvariantCulture) + " - "
                        + band.HighKhz.ToString(CultureInfo.InvariantCulture) + " kHz). Not saved.";
                    box.Text = current.ToString(CultureInfo.InvariantCulture);
                    return false;
                }
                if (khz == current) return false;
                set(khz);
                return true;
            }

            box.Text = current.ToString(CultureInfo.InvariantCulture);
            return false;
        }

        private void Btn_SpectrumWidths_Click(object sender, RoutedEventArgs e)
        {
            // The radio's name comes from the main window, the only place that knows what OmniRig is
            // running, so the table can show which row is his.
            string rig = string.Empty;
            try
            {
                var main = Application.Current?.Windows.OfType<MainWindow>().FirstOrDefault();
                if (main != null) rig = main.ConnectedRigName();
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }

            SpectrumWidthManagerWindow.Show(Window.GetWindow(this), rig);
        }

        private void Btn_RestoreDefaults_Click(object sender, RoutedEventArgs e)
        {
            var defaults = RadioPanelPresets.Defaults();
            RadioPanelPresets.Save(defaults);
            BuildRows(defaults);
            HasChanged = true;
        }
    }
}
