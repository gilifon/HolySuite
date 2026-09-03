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
                set { if (_freqKhz != value) { _freqKhz = value; Raise(nameof(FreqKhz)); Raise(nameof(FreqBrush)); Raise(nameof(HasFrequency)); Raise(nameof(IsFilled)); } }
            }

            // EVERY ROW CARRIES THE SHAPE 0000.000, including the blank one waiting at the bottom, so
            // there is always a set of digits to click on and roll the wheel over. That means "empty"
            // can no longer be read off the text: a frequency of zero is a row that has none yet.
            [JsonIgnore]
            public bool HasFrequency => double.TryParse((FreqKhz ?? string.Empty).Trim(),
                                                        NumberStyles.Float, CultureInfo.InvariantCulture,
                                                        out double khz) && khz > 0;

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
                                 || HasFrequency
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
                // Channels saved before the column had a fixed shape (e.g. "7130") are brought up to
                // it here, so every row reads the same way the moment the window opens.
                ch.FreqKhz = FormatKhz(ch.FreqKhz);
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

        // == THE FREQUENCY CELL =====================================================================
        //
        // IT IS THE MAIN WINDOW'S MANUAL-MODE FREQUENCY BOX, IN MINIATURE. A plain cell meant typing the
        // whole number every time, which is what made it awkward, so this cell is worked the way that box
        // is worked: the number is always in the shape 0000.000, the set of digits under the pointer
        // wears the EditFieldBg yellow, a click takes that whole set so the next keystroke replaces it,
        // and a wheel notch moves it - 1 kHz over the kHz digits, 0.1 kHz over the Hz digits, the same
        // rule FrequencyWheel gives the LED and the Radio Control Panel.
        //
        // The shape the frequency is always kept in: four kHz digits at least, the point, three Hz
        // digits. A row with no frequency yet reads 0000.000 rather than being blank, so there is always
        // something to click on and roll the wheel over.
        private const string BlankKhz = "0000.000";

        // Text into the shape. Anything that will not parse, or parses to zero, is a row with no
        // frequency - and a row with no frequency still shows the shape.
        private static string FormatKhz(string text)
        {
            return double.TryParse((text ?? string.Empty).Trim(), NumberStyles.Float,
                                   CultureInfo.InvariantCulture, out double khz) && khz > 0
                ? khz.ToString("0000.000", CultureInfo.InvariantCulture)
                : BlankKhz;
        }

        // Restrict the frequency box to digits, a single decimal point, and no more than three decimals.
        // The test is made on what the box WOULD hold after the keystroke, so it also refuses a point
        // typed into the middle of a number that already has one, and a fourth Hz digit typed anywhere
        // in the fraction.
        private void FreqBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            var tb = sender as System.Windows.Controls.TextBox;
            if (tb == null) { e.Handled = true; return; }

            string text = tb.Text ?? string.Empty;
            int start = Math.Min(tb.SelectionStart, text.Length);
            int length = Math.Min(tb.SelectionLength, text.Length - start);
            string after = text.Substring(0, start) + e.Text + text.Substring(start + length);

            e.Handled = !System.Text.RegularExpressions.Regex.IsMatch(after, @"^[0-9]*\.?[0-9]{0,3}$");
        }

        private void FreqBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            var tb = sender as System.Windows.Controls.TextBox;
            if (tb == null) return;

            if (e.Key == Key.Enter)
            {
                // Enter finishes the number, without waiting for the focus to leave the cell.
                ReshapeFreqBox(tb);
                tb.SelectAll();
                e.Handled = true;
                return;
            }

            if (e.Key != Key.Back && e.Key != Key.Delete) return;

            // THE DECIMAL POINT IS PART OF THE SHAPE, NOT PART OF THE NUMBER. Delete it and the two sets
            // of digits run together, with no line for the pointer, the click or the wheel to read.
            string text = tb.Text ?? string.Empty;
            if (text.IndexOf('.') < 0) return;

            bool oneCharacter = tb.SelectionLength == 0;
            int start = tb.SelectionStart;
            int length = tb.SelectionLength;
            if (oneCharacter)
            {
                if (e.Key == Key.Back) { if (start == 0) return; start--; }
                else if (start >= text.Length) return;
                length = 1;
            }
            if (start < 0 || start > text.Length) return;
            if (start + length > text.Length) length = text.Length - start;
            if (text.Remove(start, length).IndexOf('.') >= 0) return;   // the point survives: carry on

            // Backspacing onto the point alone does nothing - it steps over it rather than swallowing the
            // number. Wiping a stretch that takes the point in means "clear this frequency", so the cell
            // goes back to the blank shape.
            if (!oneCharacter)
            {
                tb.Text = BlankKhz;
                tb.SelectAll();
            }
            e.Handled = true;
        }

        // ---- which set of digits the pointer is on -------------------------------------------------

        // Where the pointer was last seen inside a frequency box, and which box that was. Kept because
        // the digits move under a still mouse: a wheel notch rewrites the number and the band has to be
        // redrawn over what is now standing there.
        private System.Windows.Controls.TextBox _zoneBox;
        private double? _zoneX;

        // The band lives in the same cell Grid as the box, behind it.
        private static System.Windows.Shapes.Rectangle ZoneMarkOf(System.Windows.Controls.TextBox tb)
        {
            var grid = tb == null ? null : tb.Parent as System.Windows.Controls.Grid;
            return grid == null
                ? null
                : grid.Children.OfType<System.Windows.Shapes.Rectangle>().FirstOrDefault();
        }

        // True when x stands right of the decimal point - the Hz digits.
        private static bool IsFractionSide(System.Windows.Controls.TextBox tb, double x)
        {
            string text = tb == null ? string.Empty : (tb.Text ?? string.Empty);
            int dot = text.IndexOf('.');
            if (dot < 0) return false;

            try { return x > tb.GetRectFromCharacterIndex(dot).Right; }
            catch (Exception swallowed) { Log.Swallow(swallowed); return false; }
        }

        // The band under the set of digits the pointer is on. Measured from the characters as they are
        // actually drawn, the same way the main window measures its own - a proportional font puts the
        // point in a different place for every length of number.
        private void ShowFreqZone()
        {
            var tb = _zoneBox;
            var mark = ZoneMarkOf(tb);
            if (mark == null) return;

            try
            {
                string text = tb.Text ?? string.Empty;
                if (_zoneX == null || text.Length == 0)
                {
                    mark.Visibility = Visibility.Collapsed;
                    return;
                }

                // The text may have been set a moment ago, in this same call stack: without this the
                // character rectangles are still the ones of the frequency before last.
                tb.UpdateLayout();

                Rect first = tb.GetRectFromCharacterIndex(0);
                Rect last = tb.GetRectFromCharacterIndex(text.Length - 1, true);
                if (first.IsEmpty || last.IsEmpty)
                {
                    mark.Visibility = Visibility.Collapsed;
                    return;
                }

                // No point typed means one set for the whole box, so the whole number lights rather than
                // half of a division that is not there.
                int dot = text.IndexOf('.');
                double? split = dot < 0 ? (double?)null : tb.GetRectFromCharacterIndex(dot).Right;
                bool fraction = split != null && _zoneX.Value > split.Value;

                double left = fraction ? split.Value : first.Left;
                double right = split == null ? last.Right : (fraction ? last.Right : split.Value);
                if (right - left <= 0)
                {
                    mark.Visibility = Visibility.Collapsed;
                    return;
                }

                mark.Margin = new Thickness(left, 0, 0, 0);
                mark.Width = right - left;
                mark.Visibility = Visibility.Visible;
            }
            catch (Exception swallowed)
            {
                Log.Swallow(swallowed);
                mark.Visibility = Visibility.Collapsed;
            }
        }

        private static void HideFreqZone(System.Windows.Controls.TextBox tb)
        {
            var mark = ZoneMarkOf(tb);
            if (mark != null) mark.Visibility = Visibility.Collapsed;
        }

        private void FreqBox_MouseMove(object sender, MouseEventArgs e)
        {
            var tb = sender as System.Windows.Controls.TextBox;
            if (tb == null) return;

            if (!ReferenceEquals(_zoneBox, tb)) HideFreqZone(_zoneBox);   // the row the pointer just left
            _zoneBox = tb;
            _zoneX = e.GetPosition(tb).X;
            ShowFreqZone();
        }

        private void FreqBox_MouseLeave(object sender, MouseEventArgs e)
        {
            HideFreqZone(sender as System.Windows.Controls.TextBox);
            if (ReferenceEquals(_zoneBox, sender)) { _zoneBox = null; _zoneX = null; }
        }

        // The number changed under the pointer - typed, or moved by the wheel - so the band is redrawn
        // over what is standing there now.
        private void FreqBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (ReferenceEquals(_zoneBox, sender)) ShowFreqZone();
        }

        // ---- clicking, typing and rolling ----------------------------------------------------------

        // A CLICK TAKES THE WHOLE SET OF DIGITS IT LANDED ON - all of the kHz, or all of the Hz - so the
        // next keystroke replaces that set instead of being squeezed in between two of its digits.
        private void FreqBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var tb = sender as System.Windows.Controls.TextBox;
            if (tb == null || e.ClickCount != 1) return;   // a double-click belongs to the tune, not here

            double x = e.GetPosition(tb).X;

            // NOT handled, and deferred: the click still has to reach the row for the selection, and the
            // text box has to finish placing its own caret. The set is taken after both have happened.
            tb.Dispatcher.BeginInvoke(new Action(() => SelectGroupAt(tb, x)),
                                      System.Windows.Threading.DispatcherPriority.Input);
        }

        private static void SelectGroupAt(System.Windows.Controls.TextBox tb, double x)
        {
            try
            {
                string text = tb.Text ?? string.Empty;
                int dot = text.IndexOf('.');
                if (dot < 0) { tb.SelectAll(); return; }

                if (IsFractionSide(tb, x)) tb.Select(dot + 1, text.Length - dot - 1);
                else tb.Select(0, dot);
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // One notch of the wheel moves the set of digits under the pointer, exactly as it does on the
        // main window's frequency box: FrequencyWheel decides what a notch is worth and tidies the first
        // one onto a round step.
        private readonly FrequencyWheel _wheel = new FrequencyWheel();

        private void FreqBox_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            var tb = sender as System.Windows.Controls.TextBox;
            if (tb == null) return;

            double x = e.GetPosition(tb).X;
            double step = IsFractionSide(tb, x) ? 0.1 : 1.0;

            double.TryParse((tb.Text ?? string.Empty).Trim(), NumberStyles.Float,
                            CultureInfo.InvariantCulture, out double khz);

            // FrequencyWheel will not start from zero - there is no such radio frequency - so a row still
            // reading 0000.000 takes its first notch here. NO BAND EDGES ARE PASSED: a channel is a
            // number in a list, not a radio inside a band, and clamping would refuse the entry being made.
            double? target = khz > 0
                ? _wheel.Next(khz, e.Delta, step)
                : (e.Delta > 0 ? step : (double?)null);
            if (target == null) return;

            e.Handled = true;   // the number is being tuned: this notch is ours, not the grid's scroller's

            tb.Text = target.Value.ToString("0000.000", CultureInfo.InvariantCulture);

            _zoneBox = tb;
            _zoneX = x;
            ShowFreqZone();   // the digits just moved under the pointer; the band moves with them
        }

        private void FreqBox_GotFocus(object sender, RoutedEventArgs e)
        {
            // The cell holds a live text box, so a click can land inside it without the grid being told
            // which row that was. Say so here, or Delete and the double-click tune act on another row.
            var tb = sender as System.Windows.Controls.TextBox;
            if (tb != null && tb.DataContext is RadioChannel ch)
                ChannelsGrid.SelectedItem = ch;
        }

        private void FreqBox_LostFocus(object sender, RoutedEventArgs e)
        {
            ReshapeFreqBox(sender as System.Windows.Controls.TextBox);
        }

        // Put the number back in shape. The BOX's text is what is written, not the channel's: the column
        // pushes every keystroke to the channel, so writing the box writes both.
        private static void ReshapeFreqBox(System.Windows.Controls.TextBox tb)
        {
            if (tb == null) return;
            string formatted = FormatKhz(tb.Text);
            if (!string.Equals(tb.Text, formatted, StringComparison.Ordinal))
                tb.Text = formatted;
        }

        // Every channel's frequency into the shape, for the moments the boxes are not asked themselves -
        // OK and the save, either of which can happen with a half-typed number still under the caret.
        private void ReshapeAllFreqs()
        {
            foreach (var ch in _channels)
                ch.FreqKhz = FormatKhz(ch.FreqKhz);
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
            if (!freqOk) missing.Add("Frequency");   // 0000.000 is a row not yet given one
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
                var blank = new RadioChannel { FreqKhz = BlankKhz };
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

            ReshapeAllFreqs();

            var problems = new List<string>();
            int rowNum = 0;
            foreach (var ch in _channels)
            {
                rowNum++;
                bool anyFilled = !string.IsNullOrWhiteSpace(ch.Name)
                              || ch.HasFrequency
                              || !string.IsNullOrWhiteSpace(ch.Mode);
                if (!anyFilled)
                    continue;   // an empty row, not a half-filled one

                var missing = new List<string>();
                if (string.IsNullOrWhiteSpace(ch.Name)) missing.Add("Name");
                if (!ch.HasFrequency) missing.Add("Frequency");
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
                // Drop wholly-empty rows (e.g. an abandoned new-row entry). A row still reading
                // 0000.000 has no frequency, however un-blank the cell looks.
                ReshapeAllFreqs();
                var toSave = _channels
                    .Where(c => !(string.IsNullOrWhiteSpace(c.Name) && !c.HasFrequency))
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
