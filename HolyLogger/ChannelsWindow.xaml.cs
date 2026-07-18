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

            // Tuning needs BOTH a valid frequency and a mode. If either is missing, send nothing to
            // the radio and tell the user exactly what to fill in first.
            bool freqOk = double.TryParse((ch.FreqKhz ?? string.Empty).Trim(),
                                          NumberStyles.Float, CultureInfo.InvariantCulture, out double khz) && khz > 0;
            var missing = new List<string>();
            if (!freqOk) missing.Add("Frequency");
            if (string.IsNullOrWhiteSpace(ch.Mode)) missing.Add("Mode");
            if (missing.Count > 0)
            {
                string what = missing.Count == 1
                    ? $"its {missing[0]} is missing"
                    : $"its {string.Join(" and ", missing)} are missing";
                HolyMessageBox.ShowWarning(
                    $"This channel can't be sent to the radio because {what}.\n\n" +
                    "Fill in the missing column, then double-click again.",
                    "My Channels", this);
                return;
            }

            _owner.SetRadioToChannel(khz / 1000.0, ch.Mode);

            // The channel has been applied -- close the window so the action feels complete (otherwise,
            // when the channel's frequency is already the current one, nothing visibly happens).
            Close();
        }

        // When a frequency cell commits (tab/enter/click away), push the new text to the item right
        // away. That fires FreqBrush's change notification at cell-commit time, so the band color
        // appears the moment focus leaves the cell -- without waiting for the whole row to commit.
        private void ChannelsGrid_CellEditEnding(object sender, System.Windows.Controls.DataGridCellEditEndingEventArgs e)
        {
            if (e.EditAction != System.Windows.Controls.DataGridEditAction.Commit) return;
            if (e.Column != FreqColumn) return;
            if (e.Row?.Item is RadioChannel ch && e.EditingElement is System.Windows.Controls.TextBox tb)
                ch.FreqKhz = (tb.Text ?? string.Empty).Trim();
        }

        // The moment the Mode cell becomes current (e.g. tabbing out of Frequency), enter edit mode so
        // the combo editor appears; ModeCombo_Loaded then drops its list open. Deferred to Background
        // priority so it runs after the grid has finished the current-cell change.
        private void ChannelsGrid_CurrentCellChanged(object sender, EventArgs e)
        {
            if (ChannelsGrid.CurrentColumn != ModeColumn) return;
            if (!(ChannelsGrid.CurrentItem is RadioChannel)) return;
            ChannelsGrid.Dispatcher.BeginInvoke(
                new Action(() => ChannelsGrid.BeginEdit()),
                System.Windows.Threading.DispatcherPriority.Background);
        }

        // Open the mode list as soon as the editor is shown, and focus it so a click/arrow picks a mode.
        private void ModeCombo_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.ComboBox cb)
            {
                cb.Focus();
                cb.IsDropDownOpen = true;
            }
        }

        private void BtnDelete_Click(object sender, RoutedEventArgs e)
        {
            if (ChannelsGrid.SelectedItem is RadioChannel ch)
                _channels.Remove(ch);
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        // OK approves the current channels and closes (Closing saves, exactly like Close). Unlike Close,
        // it first checks that no channel is half-filled: a channel needs all three columns to be usable,
        // so a row with some (but not all) of Name / Frequency / Mode is flagged and the window stays
        // open. Wholly-empty rows are ignored -- they're dropped on save.
        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            // Commit any in-progress edit so a value just typed is included in the check.
            ChannelsGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Cell, true);
            ChannelsGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, true);

            var problems = new List<string>();
            int rowNum = 0;
            foreach (var ch in _channels)
            {
                rowNum++;
                bool anyFilled = !string.IsNullOrWhiteSpace(ch.Name)
                              || !string.IsNullOrWhiteSpace(ch.FreqKhz)
                              || !string.IsNullOrWhiteSpace(ch.Mode);
                if (!anyFilled)
                    continue;   // an empty row, not a half-filled one

                var missing = new List<string>();
                if (string.IsNullOrWhiteSpace(ch.Name)) missing.Add("Name");
                if (string.IsNullOrWhiteSpace(ch.FreqKhz)) missing.Add("Frequency");
                if (string.IsNullOrWhiteSpace(ch.Mode)) missing.Add("Mode");
                if (missing.Count == 0)
                    continue;

                // Identify the row by number, and by name when it has one, so the user never has to
                // guess which channel the message is about. Then spell out the empty columns by name.
                string who = string.IsNullOrWhiteSpace(ch.Name)
                    ? $"Row {rowNum}"
                    : $"Row {rowNum} (Name: \"{ch.Name.Trim()}\")";
                string cols = missing.Count == 1
                    ? $"the {missing[0]} column is empty"
                    : $"these columns are empty: {string.Join(", ", missing)}";
                problems.Add($"• {who} — {cols}");
            }

            if (problems.Count > 0)
            {
                HolyMessageBox.ShowWarning(
                    "A channel needs all three columns (Name, Frequency, Mode) filled in.\n\n" +
                    "Please complete or delete:\n\n" + string.Join("\n", problems),
                    "My Channels", this);
                return;   // keep the window open so the user can fix them
            }

            Close();   // all channels complete -> approve and close
        }

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
