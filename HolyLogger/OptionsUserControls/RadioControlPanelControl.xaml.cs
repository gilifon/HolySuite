using System;
using System.Collections.Generic;
using System.Globalization;
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

        private void BuildRows(List<RadioBandPreset> bands)
        {
            _bands = bands;
            _enabledBoxes.Clear();
            _ssbBoxes.Clear();
            _cwBoxes.Clear();
            _rttyBoxes.Clear();

            BandGrid.Children.Clear();
            BandGrid.RowDefinitions.Clear();
            BandGrid.ColumnDefinitions.Clear();

            // Select goes on the LEFT, not the right: it is about the band's identity (does this
            // radio even have it), so it belongs beside the Band column rather than after the
            // frequencies. Everything else keeps its usual place, just shifted one column right.
            BandGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
            BandGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
            BandGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
            BandGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
            BandGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });

            AddHeaderRow();

            for (int i = 0; i < _bands.Count; i++)
            {
                var band = _bands[i];
                int row = i + 1;
                BandGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

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
                Grid.SetColumn(enabled, 0);
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
                Grid.SetColumn(label, 1);
                BandGrid.Children.Add(label);

                var ssb = MakeBox(band.SsbKhz, row, 2);
                var cw = MakeBox(band.CwKhz, row, 3);
                var rtty = MakeBox(band.RttyKhz, row, 4);
                _ssbBoxes.Add(ssb);
                _cwBoxes.Add(cw);
                _rttyBoxes.Add(rtty);
            }
        }

        private void AddHeaderRow()
        {
            BandGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var select = new TextBlock { Text = "Select", FontSize = 16, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 8, 4) };
            Grid.SetRow(select, 0);
            Grid.SetColumn(select, 0);
            BandGrid.Children.Add(select);

            var band = new TextBlock { Text = "Band", FontSize = 16, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 8, 4) };
            Grid.SetRow(band, 0);
            Grid.SetColumn(band, 1);
            BandGrid.Children.Add(band);

            var ssb = new TextBlock { Text = "SSB (kHz)", FontSize = 16, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 8, 4) };
            Grid.SetRow(ssb, 0);
            Grid.SetColumn(ssb, 2);
            BandGrid.Children.Add(ssb);

            var cw = new TextBlock { Text = "CW (kHz)", FontSize = 16, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 8, 4) };
            Grid.SetRow(cw, 0);
            Grid.SetColumn(cw, 3);
            BandGrid.Children.Add(cw);

            var rtty = new TextBlock { Text = "RTTY (kHz)", FontSize = 16, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 8, 4) };
            Grid.SetRow(rtty, 0);
            Grid.SetColumn(rtty, 4);
            BandGrid.Children.Add(rtty);
        }

        private TextBox MakeBox(int khz, int row, int column)
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
                Margin = new Thickness(0, 4, 8, 4)
            };
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
        /// number of kHz is put back to the value it had, rather than saved as nothing.
        /// </summary>
        public void SaveAll()
        {
            bool changed = false;

            for (int i = 0; i < _bands.Count; i++)
            {
                bool wantEnabled = _enabledBoxes[i].IsChecked == true;
                if (wantEnabled != _bands[i].Enabled)
                {
                    _bands[i].Enabled = wantEnabled;
                    changed = true;
                }

                changed |= ReadBox(_ssbBoxes[i], value => _bands[i].SsbKhz = value, _bands[i].SsbKhz);
                changed |= ReadBox(_cwBoxes[i], value => _bands[i].CwKhz = value, _bands[i].CwKhz);
                changed |= ReadBox(_rttyBoxes[i], value => _bands[i].RttyKhz = value, _bands[i].RttyKhz);
            }

            if (!changed) return;

            RadioPanelPresets.Save(_bands);
            HasChanged = true;
        }

        private static bool ReadBox(TextBox box, Action<int> set, int current)
        {
            string typed = (box.Text ?? string.Empty).Trim();
            if (int.TryParse(typed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int khz) && khz > 0)
            {
                if (khz == current) return false;
                set(khz);
                return true;
            }

            box.Text = current.ToString(CultureInfo.InvariantCulture);
            return false;
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
