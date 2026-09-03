using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace HolyLogger
{
    // The cluster's watch list, opened from the bell under the "Latest" button. One column, typed by
    // hand: every callsign here is one the operator does not want to miss. A spot for it rings the
    // new-country sound and is framed in purple; on a band or mode the table is not showing it is
    // pinned on its own line inside the table instead, which is the whole point of a watch.
    //
    // There is no OK/Cancel: an edit is saved the moment the cell is left, the same way the cluster's
    // other settings behave. The list lives in Settings.ClusterAlertCallsigns as one comma-separated
    // line, and MainWindow keeps the live set the spots are matched against.
    public partial class ClusterAlertsWindow : Window
    {
        private readonly MainWindow _main;
        private readonly ObservableCollection<AlertCall> _calls = new ObservableCollection<AlertCall>();
        private bool _loading;

        public ClusterAlertsWindow(MainWindow main)
        {
            InitializeComponent();
            _main = main;
            Owner = main;

            RestoreBounds_();

            _loading = true;
            try
            {
                if (_main != null)
                    foreach (string call in _main.GetClusterAlertCallsigns())
                        Add(new AlertCall { Call = call });
                EnsureTrailingEmptyLine();
            }
            finally { _loading = false; }

            GRD_Calls.ItemsSource = _calls;
            // Covers the Delete key and the row the user removes with it; typed edits come through
            // Calls_CellEditEnding, which fires before the item is in the collection. A removal can
            // also take the blank line away, so one is put back.
            _calls.CollectionChanged += (a, b) =>
            {
                SaveList();
                Dispatcher.BeginInvoke(new Action(TidyBlankLines),
                                       System.Windows.Threading.DispatcherPriority.Background);
            };

            Closing += (a, b) =>
            {
                // THE LAST CALLSIGN NEVER LEFT ITS CELL. Closing the window commits the line without a
                // CellEditEnding, so a callsign typed and then closed straight away was saved with
                // nothing said about it - which is how "TA" reached the list. Everything on the list is
                // asked about here, on the one path out that every other path ends at.
                if (!ConfirmOddCallsignsBeforeClosing()) { b.Cancel = true; return; }
                SaveList();
                SaveBounds_();
            };

            // OPEN WITH THE CURSOR ON THE EMPTY LINE. The window is opened to write a callsign into
            // it, so it is ready to be written in - no click, no hunting for the line at the bottom.
            Loaded += (a, b) => StartTypingOnTheEmptyLine();
        }

        // The list changed somewhere else while this window was open - "Copy to Alert" on a cluster
        // spot, for instance - so the grid is filled again from what was saved.
        internal void ReloadFromSettings()
        {
            _loading = true;
            try
            {
                _calls.Clear();
                if (_main != null)
                    foreach (string call in _main.GetClusterAlertCallsigns())
                        Add(new AlertCall { Call = call });
                EnsureTrailingEmptyLine();
            }
            finally { _loading = false; }
        }

        // A LINE IS ALWAYS WAITING AT THE BOTTOM, the same way the Favorite Channels window keeps one.
        // WPF's own "new item placeholder" is not enough: the moment it is typed into it BECOMES the
        // row being written, and nothing is left below it - which reads as "there is no way to add
        // another". So the window keeps the blank line itself, and opens a new one the moment the last
        // one is written in. Blank lines are dropped when the list is saved.
        private void EnsureTrailingEmptyLine()
        {
            if (_calls.Count == 0 || !IsBlank(_calls[_calls.Count - 1]))
                Add(new AlertCall());
        }

        private static bool IsBlank(AlertCall row)
        {
            return row == null || (row.Call ?? string.Empty).Trim().Length == 0;
        }

        // ONE EMPTY LINE, NEVER TWO. Typing opens a fresh line below; rubbing the callsign out again has
        // to close it, or every callsign written and deleted leaves another empty line behind.
        //
        // The line the operator is IN is the one that survives: he has just cleared it and may be about
        // to type again, so it becomes the empty line at the bottom and the spare one goes.
        private void TidyBlankLines()
        {
            if (_loading) return;

            var current = GRD_Calls.CurrentItem as AlertCall;
            bool keepCurrent = current != null && IsBlank(current) && _calls.Contains(current);

            for (int i = _calls.Count - 1; i >= 0; i--)
            {
                if (!IsBlank(_calls[i])) continue;
                if (keepCurrent && _calls[i] == current) continue;
                if (!keepCurrent && i == _calls.Count - 1) continue;   // the one waiting at the bottom
                _calls.RemoveAt(i);
            }

            EnsureTrailingEmptyLine();
        }

        // Every row is watched, so that the first letter typed into the last line opens the next one.
        private void Add(AlertCall row)
        {
            if (row == null) return;
            row.PropertyChanged -= Row_PropertyChanged;   // never twice
            row.PropertyChanged += Row_PropertyChanged;
            _calls.Add(row);
        }

        // Deferred: this arrives in the middle of a cell edit, and changing the bound collection right
        // then upsets the grid (and an ObservableCollection refuses a change inside its own event).
        private void Row_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != "Call" || _loading) return;
            Dispatcher.BeginInvoke(new Action(TidyBlankLines),
                                   System.Windows.Threading.DispatcherPriority.Background);
        }

        // The empty line the window keeps at the bottom is simply the last row.
        private void StartTypingOnTheEmptyLine()
        {
            try
            {
                if (GRD_Calls.Items.Count == 0 || GRD_Calls.Columns.Count == 0) return;

                object newLine = GRD_Calls.Items[GRD_Calls.Items.Count - 1];
                GRD_Calls.ScrollIntoView(newLine);
                GRD_Calls.CurrentCell = new DataGridCellInfo(newLine, GRD_Calls.Columns[0]);
                GRD_Calls.Focus();
                GRD_Calls.BeginEdit();
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // ONE CLICK, NOT TWO. A DataGrid takes the first click as "pick this line" and only the second
        // as "write in it", which in a window whose whole purpose is typing is one click too many.
        private void Calls_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var cell = FindParentCell(e.OriginalSource as DependencyObject);
            if (cell == null || cell.IsEditing) return;

            try
            {
                GRD_Calls.CurrentCell = new DataGridCellInfo(cell);
                // Selected as well as current: Remove works on the selection, and a line clicked into
                // is the line the operator means.
                var row = cell.DataContext as AlertCall;
                if (row != null) GRD_Calls.SelectedItem = row;
                GRD_Calls.BeginEdit();
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        private static DataGridCell FindParentCell(DependencyObject from)
        {
            while (from != null && !(from is DataGridCell))
                from = VisualTreeHelper.GetParent(from);
            return from as DataGridCell;
        }

        // The grid commits the cell AFTER this event, so the save is queued behind it - reading the
        // collection here would still show the pre-edit text.
        private void Calls_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            if (_loading) return;
            // The selection is left where it is: nothing colours it now (see the grid's CellStyle),
            // and Remove needs a line to act on.
            var edited = e.Row == null ? null : e.Row.Item as AlertCall;
            Dispatcher.BeginInvoke(new Action(() => { SaveList(); CheckTypedCallsign(edited); }),
                                   System.Windows.Threading.DispatcherPriority.Background);
        }

        // THE SAME QUESTION THE DX CALLSIGN BOX ASKS, asked here for the same reason. A watch list is
        // only as good as what is typed into it: a callsign with a typo in it never rings, and nothing
        // ever tells the operator why. The two tests are the Log Fixer's own - are the characters ones
        // a callsign can hold, and is there a letter and a digit in it.
        //
        // AND IT ONLY WARNS, like the entry form: a special-event or portable call he knows to be right
        // is kept exactly as typed. The program is not the authority on callsigns; he is.
        private void CheckTypedCallsign(AlertCall row)
        {
            if (row == null) return;

            string call = (row.Call ?? string.Empty).Trim();
            if (call.Length == 0) return;               // an empty line is simply dropped when saved

            if (CallsignIdentity.HasOnlyCallsignCharacters(call) &&
                CallsignIdentity.HasCallsignShape(call)) return;

            bool keepIt = HolyMessageBox.ShowConfirm(
                "**\"" + call + "\"** does not look like a callsign." + Environment.NewLine + Environment.NewLine
                + "Keep it on the alerts list anyway?",
                "Check the callsign", HolyMsgType.Warning, this, 0, "Keep it", "Let me fix it");

            if (keepIt)
            {
                _keptAsTyped.Add(call);   // asked and answered; the close does not ask again
                return;
            }

            // He chose to fix it: the line goes back into edit with the text selected, so the right
            // callsign can be typed straight over it.
            StartTypingOn(row);
        }

        // Callsigns he has already been asked about and kept. Asked once, not at every turn.
        private readonly HashSet<string> _keptAsTyped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Everything on the list, checked on the way out. Returns false to keep the window open.
        private bool ConfirmOddCallsignsBeforeClosing()
        {
            var odd = new List<AlertCall>();
            foreach (var row in _calls)
            {
                string call = ((row == null ? null : row.Call) ?? string.Empty).Trim();
                if (call.Length == 0) continue;
                if (_keptAsTyped.Contains(call)) continue;
                if (CallsignIdentity.HasOnlyCallsignCharacters(call) &&
                    CallsignIdentity.HasCallsignShape(call)) continue;
                odd.Add(row);
            }
            if (odd.Count == 0) return true;

            string names = string.Join(", ", odd.Select(r => "**" + (r.Call ?? string.Empty).Trim() + "**"));
            bool keep = HolyMessageBox.ShowConfirm(
                (odd.Count == 1 ? names + " does not look like a callsign." + Environment.NewLine + Environment.NewLine
                                : names + " do not look like callsigns." + Environment.NewLine + Environment.NewLine)
                + "Keep them on the alerts list anyway?",
                "Check the callsigns", HolyMsgType.Warning, this, 0, "Keep them", "Let me fix them");

            if (keep)
            {
                foreach (var row in odd) _keptAsTyped.Add((row.Call ?? string.Empty).Trim());
                return true;
            }

            // He wants to fix them: the window stays open with the first one ready to be typed over.
            StartTypingOn(odd[0]);
            return false;
        }

        // Put the caret in one line, with what is there selected.
        private void StartTypingOn(AlertCall row)
        {
            try
            {
                if (row == null || GRD_Calls.Columns.Count == 0) return;
                GRD_Calls.ScrollIntoView(row);
                GRD_Calls.SelectedItem = row;
                GRD_Calls.CurrentCell = new DataGridCellInfo(row, GRD_Calls.Columns[0]);
                GRD_Calls.Focus();
                GRD_Calls.BeginEdit();
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // REMOVE TAKES THE LINE THE OPERATOR IS IN. It used to ask only for the SELECTED row, and a
        // line being typed into is not necessarily selected - so pressing Remove with the cursor sitting
        // in a callsign did nothing at all, which reads as a broken button. The line being edited is
        // closed first (its text would otherwise be written back after the row had gone), and the
        // current line is used when nothing is selected.
        private void BtnRemove_Click(object sender, RoutedEventArgs e)
        {
            try { GRD_Calls.CommitEdit(DataGridEditingUnit.Row, true); }
            catch (Exception swallowed) { Log.Swallow(swallowed); }

            var row = (GRD_Calls.SelectedItem as AlertCall) ?? (GRD_Calls.CurrentItem as AlertCall);
            if (row != null) _calls.Remove(row);   // CollectionChanged saves, and puts a blank line back
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        // Blank lines are dropped and a callsign typed twice is kept once, so the list the cluster
        // matches against is exactly what the grid shows.
        private void SaveList()
        {
            if (_loading || _main == null) return;
            var list = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in _calls)
            {
                string call = ((row == null ? null : row.Call) ?? string.Empty).Trim();
                if (call.Length == 0) continue;
                if (!seen.Add(call)) continue;
                list.Add(call);
            }
            _main.SetClusterAlertCallsigns(list);
        }

        // One row of the grid. The setter does the tidying (trim + upper) so a callsign typed in
        // lower case, or with a stray space, is stored in the one shape the matcher expects - and it
        // asks at once what country that callsign belongs to, so the flag and the country name appear
        // as the callsign is written rather than at some later moment nobody can predict.
        public sealed class AlertCall : INotifyPropertyChanged
        {
            private string _call = string.Empty;
            public string Call
            {
                get { return _call; }
                set
                {
                    // NOT trimmed here: this setter now runs on every keystroke, and taking a space
                    // away under the caret moves it. The tidying happens where the list is read.
                    string v = (value ?? string.Empty).ToUpperInvariant();
                    if (_call == v) return;
                    _call = v;
                    LookUpCountry();
                    Raise("Call");
                }
            }

            private string _country = string.Empty;
            public string Country
            {
                get { return _country; }
                private set { _country = value; Raise("Country"); }
            }

            private string _flagPath;
            public string FlagPath
            {
                get { return _flagPath; }
                private set { _flagPath = value; Raise("FlagPath"); }
            }

            private void LookUpCountry()
            {
                string country = string.Empty, flag = null;
                string call = _call.Trim();
                if (call.Length > 0)
                    MainWindow.ResolveCallsignCountry(call, out country, out flag);
                Country = country;
                FlagPath = flag;
            }

            private void Raise(string name)
            {
                var h = PropertyChanged;
                if (h != null) h(this, new PropertyChangedEventArgs(name));
            }

            public event PropertyChangedEventHandler PropertyChanged;
        }

        // ── window position / size persistence ────────────────────────────

        private void RestoreBounds_()
        {
            var s = Properties.Settings.Default;
            if (s.ClusterAlertsWindowWidth >= MinWidth) Width = s.ClusterAlertsWindowWidth;
            if (s.ClusterAlertsWindowHeight >= MinHeight) Height = s.ClusterAlertsWindowHeight;

            if (IsPositionOnScreen(s.ClusterAlertsWindowLeft, s.ClusterAlertsWindowTop))
            {
                Left = s.ClusterAlertsWindowLeft;
                Top = s.ClusterAlertsWindowTop;
            }
            else
            {
                WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
        }

        private void SaveBounds_()
        {
            try
            {
                var b = WindowState == WindowState.Normal
                    ? new Rect(Left, Top, Width, Height)
                    : RestoreBounds;
                var s = Properties.Settings.Default;
                if (!double.IsNaN(b.Left) && !double.IsInfinity(b.Left) &&
                    !double.IsNaN(b.Top) && !double.IsInfinity(b.Top))
                {
                    s.ClusterAlertsWindowLeft = b.Left;
                    s.ClusterAlertsWindowTop = b.Top;
                }
                if (b.Width > 0) s.ClusterAlertsWindowWidth = b.Width;
                if (b.Height > 0) s.ClusterAlertsWindowHeight = b.Height;
                s.Save();
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // A saved spot on a monitor that's since been removed must not strand the window off-screen.
        private static bool IsPositionOnScreen(double left, double top)
        {
            if (double.IsNaN(left) || double.IsNaN(top) ||
                double.IsInfinity(left) || double.IsInfinity(top))
                return false;

            double vsLeft = SystemParameters.VirtualScreenLeft;
            double vsTop = SystemParameters.VirtualScreenTop;
            double vsRight = vsLeft + SystemParameters.VirtualScreenWidth;
            double vsBottom = vsTop + SystemParameters.VirtualScreenHeight;

            return left >= vsLeft - 10 && top >= vsTop - 10 &&
                   left <= vsRight - 100 && top <= vsBottom - 60;
        }
    }
}
