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
            private string _name = "";
            public string Name
            {
                get => _name;
                set { if (_name != value) { _name = value; Raise(nameof(Name)); Raise(nameof(IsFilled)); } }
            }

            private string _freqKhz = "";
            public string FreqKhz
            {
                get => _freqKhz;
                set { if (_freqKhz != value) { _freqKhz = value; Raise(nameof(FreqKhz)); Raise(nameof(FreqBrush)); Raise(nameof(IsFilled)); } }
            }

            private string _mode = "";
            public string Mode
            {
                get => _mode;
                set { if (_mode != value) { _mode = value; Raise(nameof(Mode)); Raise(nameof(IsFilled)); } }
            }

            // True once the row has any content — drives the row cursor (hand on a filled row for the
            // click/double-click tune; text cursor on an empty row for typing). Not persisted.
            [JsonIgnore]
            public bool IsFilled => !string.IsNullOrWhiteSpace(Name)
                                 || !string.IsNullOrWhiteSpace(FreqKhz)
                                 || !string.IsNullOrWhiteSpace(Mode);

            private void Raise(string prop) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));

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

            // Position/size handled by the SAME shared helper every other window uses, so this
            // window cannot drift from the rest. WindowBoundsJson is excluded from profiles, so nothing
            // overwrites it at startup.
            WindowBounds.Attach(this, "Channels");


            foreach (var ch in LoadChannels())
            {
                HookChannel(ch);
                _channels.Add(ch);
            }
            EnsureTrailingEmptyRow();
            // Deleting rows (Delete key or the button) must never leave the list without a blank row.
            // Deferred for the same reentrancy reason as Channel_PropertyChanged.
            _channels.CollectionChanged += (s, e) =>
            {
                if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Remove)
                    Dispatcher.BeginInvoke(new Action(EnsureTrailingEmptyRow),
                        System.Windows.Threading.DispatcherPriority.Background);
            };
            ChannelsGrid.ItemsSource = _channels;

            // Same header look as every other log-style table (QSO grid, cluster, Logs window):
            // the LogHeaderBg token from View > Color Scheme > Customize Colors, via the shared style.
            ChannelsGrid.ColumnHeaderStyle = MainWindow.BuildLogTableHeaderStyle();

            UpdatePinButton();

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

            if (_owner == null)
                return;

            // Applying a channel needs BOTH a valid frequency and a mode. If either is missing, do
            // nothing and say which one to fill.
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
                    $"This channel can't be applied because {what}.\n\n" +
                    "Fill in the missing column, then double-click again.",
                    "My Favorite Channels", this);
                return;
            }

            // Apply the channel to the main window. SetRadioToChannel fills the Frequency and Mode
            // fields, and ALSO tunes the radio when CAT is active. With no CAT it still fills the fields
            // (it returns before the tune step), so a double-click is useful whether or not CAT is
            // connected -- which is exactly the requested behavior.
            _owner.SetRadioToChannel(khz / 1000.0, ch.Mode);

            // Applied -- close so the action feels complete (otherwise, when the channel's frequency is
            // already the current one, nothing visibly happens).
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
            {
                ch.PropertyChanged -= Channel_PropertyChanged;
                _channels.Remove(ch);
                EnsureTrailingEmptyRow();   // never leave the list without a blank row to type in
            }
        }

        // Keep exactly one empty row at the bottom at all times, so it's always obvious how to add
        // another channel. WPF's built-in "new item placeholder" isn't enough: the moment you start
        // typing in it, it BECOMES the row you're editing and no empty row is left below, which reads
        // as "there's no way to add more lines". Wholly-empty rows are dropped on save.
        private void EnsureTrailingEmptyRow()
        {
            if (_channels.Count == 0 || _channels[_channels.Count - 1].IsFilled)
            {
                var blank = new RadioChannel();
                HookChannel(blank);
                _channels.Add(blank);
            }
        }

        private void HookChannel(RadioChannel ch)
        {
            if (ch == null) return;
            ch.PropertyChanged -= Channel_PropertyChanged;   // never double-subscribe
            ch.PropertyChanged += Channel_PropertyChanged;
        }

        // A row just got content -> open a fresh blank row after it. Deferred: we're inside a property
        // notification raised during a cell commit, and mutating the bound collection right then can
        // upset the DataGrid (and ObservableCollection forbids reentrant changes).
        private void Channel_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(RadioChannel.IsFilled)) return;
            Dispatcher.BeginInvoke(new Action(EnsureTrailingEmptyRow),
                System.Windows.Threading.DispatcherPriority.Background);
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
                    "My Favorite Channels", this);
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

        // Pin: keep this window as part of the setup, so it reopens automatically on the next run at the
        // exact position and size it was left. Unpinned, it only opens when asked for from the menu.
        private void TitleBar_Pin_Click(object sender, RoutedEventArgs e)
        {
            var s = Properties.Settings.Default;
            s.ChannelsWindowPinned = !s.ChannelsWindowPinned;
            try { s.Save(); } catch (Exception swallowed) { Log.Swallow(swallowed); }
            UpdatePinButton();
        }

        // Lit accent + filled pin glyph when pinned, muted outline when not, so the state is visible
        // without hovering for the tooltip.
        // Called by the main window during shutdown: a window still open when the PROGRAM closes does
        // not get its Closing save, so the channel list would lose edits made in that last session.
        internal void PersistNow() => SaveChannels();

        // Lets the main window re-sync the icon when it unpins on a menu-open.
        internal void RefreshPinButton() => UpdatePinButton();

        private void UpdatePinButton()
        {
            if (TitleBar_PinBtn == null) return;
            bool pinned = Properties.Settings.Default.ChannelsWindowPinned;
            // Segoe MDL2 pin glyphs. Pinned shows the UPRIGHT pin (U+E840 "Pinned"); unpinned the angled
            // one (U+E718 "Pin", i.e. "click to pin"). Upright = held in place.
            TitleBar_PinBtn.Content = pinned ? "" : "";
            TitleBar_PinBtn.Foreground = pinned
                ? ThemeManager.Brush("AccentBrush")
                : ThemeManager.Brush("MutedTextBrush");
            TitleBar_PinBtn.ToolTip = pinned
                ? "Pinned: this window reopens automatically next time, in this position and size. Click to unpin."
                : "Click to pin: this window will reopen automatically next time, in this position and size.";
        }





    }
}
