using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace HolyLogger.OptionsUserControls
{
    /// <summary>
    /// Options > Radio Control Panel: the two frequencies behind each of the panel's ten band
    /// buttons. The band list is fixed; only the frequencies are the operator's to change.
    /// </summary>
    public partial class RadioControlPanelControl : UserControl
    {
        private List<RadioBandPreset> _bands;
        private readonly List<TextBox> _ssbBoxes = new List<TextBox>();
        private readonly List<TextBox> _cwBoxes = new List<TextBox>();

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
            _ssbBoxes.Clear();
            _cwBoxes.Clear();

            BandGrid.Children.Clear();
            BandGrid.RowDefinitions.Clear();
            BandGrid.ColumnDefinitions.Clear();

            BandGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
            BandGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
            BandGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });

            AddHeaderRow();

            for (int i = 0; i < _bands.Count; i++)
            {
                var band = _bands[i];
                int row = i + 1;
                BandGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var label = new TextBlock
                {
                    Text = band.Label + " MHz  (" + band.Name + ")",
                    FontSize = 16,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 4, 8, 4)
                };
                Grid.SetRow(label, row);
                Grid.SetColumn(label, 0);
                BandGrid.Children.Add(label);

                var ssb = MakeBox(band.SsbKhz, row, 1);
                var cw = MakeBox(band.CwKhz, row, 2);
                _ssbBoxes.Add(ssb);
                _cwBoxes.Add(cw);
            }
        }

        private void AddHeaderRow()
        {
            BandGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var band = new TextBlock { Text = "Band", FontSize = 16, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 8, 4) };
            Grid.SetRow(band, 0);
            Grid.SetColumn(band, 0);
            BandGrid.Children.Add(band);

            var ssb = new TextBlock { Text = "SSB (kHz)", FontSize = 16, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 8, 4) };
            Grid.SetRow(ssb, 0);
            Grid.SetColumn(ssb, 1);
            BandGrid.Children.Add(ssb);

            var cw = new TextBlock { Text = "CW (kHz)", FontSize = 16, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 8, 4) };
            Grid.SetRow(cw, 0);
            Grid.SetColumn(cw, 2);
            BandGrid.Children.Add(cw);
        }

        private TextBox MakeBox(int khz, int row, int column)
        {
            var box = new TextBox
            {
                Text = khz.ToString(CultureInfo.InvariantCulture),
                FontSize = 16,
                Height = 28,
                Width = 120,
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

        /// <summary>
        /// Reads every box back into the band list and stores it. A box that does not hold a whole
        /// number of kHz is put back to the value it had, rather than saved as nothing.
        /// </summary>
        public void SaveAll()
        {
            bool changed = false;

            for (int i = 0; i < _bands.Count; i++)
            {
                changed |= ReadBox(_ssbBoxes[i], value => _bands[i].SsbKhz = value, _bands[i].SsbKhz);
                changed |= ReadBox(_cwBoxes[i], value => _bands[i].CwKhz = value, _bands[i].CwKhz);
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
