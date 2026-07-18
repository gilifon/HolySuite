using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using HolyParser;
using Newtonsoft.Json;

namespace HolyLogger
{
    // A user-defined list of radio channels (name / frequency in kHz / mode). Double-clicking a channel
    // asks the main window to set the radio to it (which captures the undo state and pops the undo icon,
    // reusing the same mechanism as "Set Radio to QSO freq"). When CAT is not active a double-click
    // explains why with a message box; the list stays editable regardless. Persists as JSON in settings.
    public partial class ChannelsWindow : Window
    {
        public class RadioChannel : INotifyPropertyChanged
        {
            public string Name { get; set; } = "";

            private string _freqKhz = "";
            public string FreqKhz
            {
                get => _freqKhz;
                set
                {
                    if (_freqKhz == value) return;
                    _freqKhz = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FreqKhz)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FreqBrush)));
                }
            }

            public string Mode { get; set; } = "";

            // Freq text is colored by band, from the same band-color source as the cluster's Freq
            // column and the band checkboxes (convertFreqToBand accepts kHz directly, our unit).
            // JsonIgnore: it's derived from FreqKhz, so it must not round-trip through settings.
            [JsonIgnore]
            public Brush FreqBrush
            {
                get
                {
                    try
                    {
                        string band = HolyLogParser.convertFreqToBand((FreqKhz ?? string.Empty).Trim());
                        return string.IsNullOrEmpty(band)
                            ? ThemeManager.Brush("TextBrush")
                            : MainWindow.GetBandBrush(band);
                    }
                    catch { return ThemeManager.Brush("TextBrush"); }
                }
            }

            public event PropertyChangedEventHandler PropertyChanged;
        }

        private static readonly string[] Modes = { "USB", "LSB", "CW", "FT8", "DIGI", "RTTY", "FM", "AM" };

        private readonly MainWindow _owner;
        private readonly ObservableCollection<RadioChannel> _channels = new ObservableCollection<RadioChannel>();

        public ChannelsWindow(MainWindow owner)
        {
            InitializeComponent();
            _owner = owner;
            Owner = owner;
            ModeColumn.ItemsSource = Modes;

            foreach (var ch in LoadChannels())
                _channels.Add(ch);
            ChannelsGrid.ItemsSource = _channels;

            // Same header look as every other log-style table (QSO grid, cluster, Logs window):
            // the LogHeaderBg token from View > Color Scheme > Customize Colors, via the shared style.
            ChannelsGrid.ColumnHeaderStyle = MainWindow.BuildLogTableHeaderStyle();

            Closing += (s, e) => SaveChannels();
        }

        // Restrict the frequency cell's editor to digits and a single decimal point.
        private void FreqBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            var tb = sender as System.Windows.Controls.TextBox;
            foreach (char c in e.Text)
            {
                if (char.IsDigit(c)) continue;
                if (c == '.' && tb != null && !tb.Text.Contains(".")) continue;
                e.Handled = true;
                return;
            }
        }

        private void ChannelsGrid_PreviewMouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            // Flush any in-progress cell edit so a value just typed (e.g. "7130") is committed to the
            // bound channel before we read it -- otherwise the first double-click would see the old value.
            ChannelsGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Cell, true);
            ChannelsGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, true);

            // Ignore the "new item" placeholder row.
            var ch = ChannelsGrid.SelectedItem as RadioChannel;
            if (ch == null)
                return;

            // Reserve double-click for tuning, not for entering cell edit (edit via click / F2 instead).
            e.Handled = true;

            if (_owner == null || !_owner.IsCatLive())
            {
                HolyMessageBox.ShowWarning(
                    "Radio control (CAT) is not active, so the radio can't be tuned to this channel.\n\n" +
                    "You can still add, edit and delete channels.",
                    "CAT not active", this);
                return;
            }

            if (!double.TryParse((ch.FreqKhz ?? string.Empty).Trim(),
                                 NumberStyles.Float, CultureInfo.InvariantCulture, out double khz) || khz <= 0)
            {
                HolyMessageBox.ShowWarning("This channel has no valid frequency (kHz).", "Channels", this);
                return;
            }

            _owner.SetRadioToChannel(khz / 1000.0, ch.Mode);

            // The channel has been applied ׳’ג‚¬ג€ close the window so the action feels complete (otherwise,
            // when the channel's frequency is already the current one, nothing visibly happens).
            Close();
        }

        private void BtnDelete_Click(object sender, RoutedEventArgs e)
        {
            if (ChannelsGrid.SelectedItem is RadioChannel ch)
                _channels.Remove(ch);
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        // Custom title-bar caption buttons (the window uses WindowStyle=None, so it draws its own).
        private void TitleBar_Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

        private void TitleBar_MaxRestore_Click(object sender, RoutedEventArgs e)
        {
            if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
            else SystemCommands.MaximizeWindow(this);
        }

        private void TitleBar_Close_Click(object sender, RoutedEventArgs e) => Close();

        // Keep the maximize/restore glyph in sync with the window state.
        private void Window_StateChanged(object sender, EventArgs e)
        {
            if (TitleBar_MaxRestoreBtn == null) return;
            bool maximized = WindowState == WindowState.Maximized;
            TitleBar_MaxRestoreBtn.Content = maximized ? "" : "";
            TitleBar_MaxRestoreBtn.ToolTip = maximized ? "Restore Down" : "Maximize";
        }

        private static List<RadioChannel> LoadChannels()
        {
            try
            {
                string json = Properties.Settings.Default.ChannelsJson;
                if (string.IsNullOrWhiteSpace(json))
                    return new List<RadioChannel>();
                return JsonConvert.DeserializeObject<List<RadioChannel>>(json) ?? new List<RadioChannel>();
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); return new List<RadioChannel>(); }
        }

        private void SaveChannels()
        {
            try
            {
                // Drop wholly-empty rows (e.g. an abandoned new-row entry).
                var toSave = _channels
                    .Where(c => !(string.IsNullOrWhiteSpace(c.Name) && string.IsNullOrWhiteSpace(c.FreqKhz)))
                    .ToList();
                Properties.Settings.Default.ChannelsJson = JsonConvert.SerializeObject(toSave);
                Properties.Settings.Default.Save();
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }
    }
}
