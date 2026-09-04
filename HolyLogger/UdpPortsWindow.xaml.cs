using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace HolyLogger
{
    // THE UDP PORTS MANAGER.
    //
    // Replaces the fixed ports that used to sit on the Options > General page ("Enable UDP Client",
    // "N1MM+ UDP Client", and the HolyCluster box). Two tables now:
    //
    //   Receive    a name and a port. What arrives is identified from the datagram itself, so the
    //              name decides nothing (MainWindow.Udp.cs).
    //   Broadcast  a name, an address, a port and a FORMAT. Nothing arrives to be examined here, so
    //              the operator says what to write and where to send it (MainWindow.UdpSend.cs).
    //
    // Save writes both tables; the sockets are opened, closed and re-read when the Options window
    // closes. Cancel leaves everything as it was.
    public partial class UdpPortsWindow : Window
    {
        private readonly ObservableCollection<UdpPortEntry> _rows = new ObservableCollection<UdpPortEntry>();
        private readonly ObservableCollection<UdpBroadcastEntry> _sendRows = new ObservableCollection<UdpBroadcastEntry>();

        public UdpPortsWindow(Window owner)
        {
            InitializeComponent();
            Owner = owner;

            foreach (var row in UdpPortStore.Load())
            {
                Hook(row);
                _rows.Add(row);
            }
            EnsureTrailingEmptyRow();

            foreach (var row in UdpBroadcastStore.Load())
            {
                HookSend(row);
                _sendRows.Add(row);
            }
            EnsureTrailingEmptySendRow();

            // Deleting a row (the Delete key) must never leave a table without a blank row to type in.
            // Deferred for the same reentrancy reason as Row_PropertyChanged below.
            _rows.CollectionChanged += (s, e) =>
            {
                if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Remove)
                    Dispatcher.BeginInvoke(new Action(EnsureTrailingEmptyRow),
                        System.Windows.Threading.DispatcherPriority.Background);
            };
            _sendRows.CollectionChanged += (s, e) =>
            {
                if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Remove)
                    Dispatcher.BeginInvoke(new Action(EnsureTrailingEmptySendRow),
                        System.Windows.Threading.DispatcherPriority.Background);
            };

            PortsGrid.ItemsSource = _rows;
            SendGrid.ItemsSource = _sendRows;

            // Same header look as every other table in the program.
            PortsGrid.ColumnHeaderStyle = MainWindow.BuildLogTableHeaderStyle();
            SendGrid.ColumnHeaderStyle = MainWindow.BuildLogTableHeaderStyle();
        }

        // Digits only in a Port cell (both tables use this).
        private void PortBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            foreach (char c in e.Text)
            {
                if (!char.IsDigit(c)) { e.Handled = true; return; }
            }
        }

        // Keep exactly one empty row at the bottom, so it is always obvious how to add another line.
        // WPF's own "new item placeholder" is not enough: the moment you type in it, it BECOMES the row
        // being edited and no empty row is left below, which reads as "there is no way to add more".
        private void EnsureTrailingEmptyRow()
        {
            if (_rows.Count == 0 || _rows[_rows.Count - 1].IsFilled)
            {
                var blank = new UdpPortEntry();
                Hook(blank);
                _rows.Add(blank);
            }
        }

        private void EnsureTrailingEmptySendRow()
        {
            if (_sendRows.Count == 0 || _sendRows[_sendRows.Count - 1].IsFilled)
            {
                var blank = new UdpBroadcastEntry();
                HookSend(blank);
                _sendRows.Add(blank);
            }
        }

        private void Hook(UdpPortEntry row)
        {
            if (row == null) return;
            row.PropertyChanged -= Row_PropertyChanged;   // never double-subscribe
            row.PropertyChanged += Row_PropertyChanged;
        }

        private void HookSend(UdpBroadcastEntry row)
        {
            if (row == null) return;
            row.PropertyChanged -= SendRow_PropertyChanged;
            row.PropertyChanged += SendRow_PropertyChanged;
        }

        // A row just got content -> open a fresh blank row after it. Deferred: we are inside a property
        // notification raised during a cell edit, and changing the bound collection right then upsets the
        // DataGrid (and ObservableCollection forbids reentrant changes).
        private void Row_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(UdpPortEntry.IsFilled)) return;
            Dispatcher.BeginInvoke(new Action(EnsureTrailingEmptyRow),
                System.Windows.Threading.DispatcherPriority.Background);
        }

        private void SendRow_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(UdpBroadcastEntry.IsFilled)) return;
            Dispatcher.BeginInvoke(new Action(EnsureTrailingEmptySendRow),
                System.Windows.Threading.DispatcherPriority.Background);
        }

        // The ? button. Says what can arrive, what can be sent, and what HolyLogger does with each.
        private void BtnWhat_Click(object sender, RoutedEventArgs e)
        {
            // No hand-made line breaks inside a paragraph: the box wraps to the width it is given, and
            // a break of ours would then wrap a second time. Measured with the msgsize harness at the
            // width below - nothing scrolls, so every word is on screen.
            HolyMessageBox.Show(
                "**RECEIVE** — you do not have to say what a program sends. HolyLogger reads what arrives on every port you tick:\n\n"
                + "•  A contact — from WSJT-X, JTDX, MSHV, N1MM+, or any program that sends it as ADIF. It is stored in your log.\n\n"
                + "•  N1MM+'s radio — its frequency and mode go into the entry boxes.\n\n"
                + "•  A selected spot — from HolyCluster (it usually sends on port 2237). The callsign goes into the DX Callsign field, and the frequency too when CAT is not connected.\n\n"
                + "A contact you already have is not stored twice, whichever port it came in on.\n\n"
                + "──────────────────────────────────────────────\n\n"
                + "**BROADCAST** — here you choose, because the other program has to be told what to expect:\n\n"
                + "•  ADIF — the contact as a plain ADIF record. Understood by the most programs.\n\n"
                + "•  WSJT-X ADIF — the same record in the WSJT-X envelope, which is what JTAlert, GridTracker and Log4OM's WSJT-X listener expect.\n\n"
                + "•  N1MM+ XML — the contact as N1MM+ announces one.\n\n"
                + "•  Radio status — not a contact: your frequency, mode and DX callsign, sent as you tune.\n\n"
                + "The first three go out the moment a contact is logged. Address 127.0.0.1 means a program on this same PC.",
                "UDP Ports Manager", HolyMsgType.Info, this, 760);
        }

        // DELETE REMOVES THE LINE.
        //
        // The DataGrid's own Delete key (CanUserDeleteRows) did nothing here, so the key is taken
        // ourselves. While a cell is actually being typed in, Delete belongs to the text box - the
        // operator is deleting a character, not the line - so the key is left alone then.
        private void PortsGrid_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Delete) return;
            if (Keyboard.FocusedElement is System.Windows.Controls.TextBox) return;

            var row = PortsGrid.SelectedItem as UdpPortEntry;
            if (row == null) return;

            e.Handled = true;
            row.PropertyChanged -= Row_PropertyChanged;
            _rows.Remove(row);
            EnsureTrailingEmptyRow();   // never leave the table without a blank row to type in
        }

        private void SendGrid_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Delete) return;
            if (Keyboard.FocusedElement is System.Windows.Controls.TextBox) return;

            var row = SendGrid.SelectedItem as UdpBroadcastEntry;
            if (row == null) return;

            e.Handled = true;
            row.PropertyChanged -= SendRow_PropertyChanged;
            _sendRows.Remove(row);
            EnsureTrailingEmptySendRow();
        }

        // LEAVING A CELL FINISHES THE EDIT.
        //
        // A cell put into edit mode stayed in it: clicking the window's own background moves no
        // keyboard focus, so the grid never heard that the operator had left, and the row sat there
        // half-open with its box and its drop-down showing. Both ways out are covered - a click
        // anywhere outside a grid, and focus genuinely moving away.
        private void Window_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var clicked = e.OriginalSource as DependencyObject;
            CommitUnlessTheEditedCellWasClicked(PortsGrid, clicked);
            CommitUnlessTheEditedCellWasClicked(SendGrid, clicked);
        }

        // LEAVING THE CELL is what ends the edit - not leaving the row, and not leaving the table.
        // Anything else clicked finishes it: another cell in the same row, the empty space under the
        // table, the headings, a button, the other table.
        private static void CommitUnlessTheEditedCellWasClicked(System.Windows.Controls.DataGrid grid, DependencyObject clicked)
        {
            if (grid == null) return;

            var cell = AncestorCell(clicked);
            if (cell != null && cell.IsEditing) return;   // the click is inside the cell being typed in

            CommitGrid(grid);
        }

        // The cell a click landed in, following a drop-down's popup back to the cell that opened it.
        private static System.Windows.Controls.DataGridCell AncestorCell(DependencyObject node)
        {
            while (node != null)
            {
                var cell = node as System.Windows.Controls.DataGridCell;
                if (cell != null) return cell;

                if (node is System.Windows.Controls.Primitives.Popup popup)
                {
                    node = popup.PlacementTarget ?? System.Windows.Media.VisualTreeHelper.GetParent(popup);
                    continue;
                }

                DependencyObject parent = null;
                if (node is System.Windows.Media.Visual || node is System.Windows.Media.Media3D.Visual3D)
                    parent = System.Windows.Media.VisualTreeHelper.GetParent(node);
                node = parent ?? System.Windows.LogicalTreeHelper.GetParent(node);
            }
            return null;
        }

        // LostKeyboardFocus BUBBLES. Clicking a cell moves focus from the grid to the editor INSIDE it,
        // and that raised this handler on the grid - which committed the edit on the very click that
        // started it, so the address could not be typed at all. Only focus leaving the grid altogether
        // counts.
        private void Grid_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            var grid = sender as System.Windows.Controls.DataGrid;
            if (grid == null) return;
            if (IsInside(e.NewFocus as DependencyObject, grid)) return;
            CommitGrid(grid);
        }

        private static void CommitGrid(System.Windows.Controls.DataGrid grid)
        {
            if (grid == null) return;
            try
            {
                grid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Cell, true);
                grid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, true);
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // Is the clicked thing part of this grid? A drop-down's list lives in a popup of its own, off
        // the window's visual tree, so the walk is followed through the popup back to what opened it.
        private static bool IsInside(DependencyObject node, DependencyObject container)
        {
            while (node != null)
            {
                if (node == container) return true;

                if (node is System.Windows.Controls.Primitives.Popup popup)
                {
                    node = popup.PlacementTarget ?? System.Windows.Media.VisualTreeHelper.GetParent(popup);
                    continue;
                }

                DependencyObject parent = null;
                if (node is System.Windows.Media.Visual || node is System.Windows.Media.Media3D.Visual3D)
                    parent = System.Windows.Media.VisualTreeHelper.GetParent(node);
                node = parent ?? System.Windows.LogicalTreeHelper.GetParent(node);
            }
            return false;
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e) => Close();

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            // Commit whatever cell is still being typed in, so a value just entered is included.
            PortsGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Cell, true);
            PortsGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, true);
            SendGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Cell, true);
            SendGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, true);

            var problems = new List<string>();
            var listening = new Dictionary<int, string>();

            int rowNum = 0;
            foreach (var row in _rows)
            {
                rowNum++;
                if (!row.IsFilled) continue;   // the blank row at the bottom

                string who = Who("Receive line", rowNum, row.Name);

                if (row.PortNumber == 0)
                {
                    problems.Add(string.IsNullOrWhiteSpace(row.Port)
                        ? "• " + who + " — no port number"
                        : "• " + who + " — " + row.Port.Trim() + " is not a port number (1 to 65535)");
                    continue;
                }

                // Two lines on one port cannot both be opened, and which one won would be a matter of
                // luck. Say so instead.
                string other;
                if (listening.TryGetValue(row.PortNumber, out other))
                    problems.Add("• " + who + " — port " + row.PortNumber + " is already used by " + other);
                else
                    listening[row.PortNumber] = who;
            }

            rowNum = 0;
            foreach (var row in _sendRows)
            {
                rowNum++;
                if (!row.IsFilled) continue;

                string who = Who("Broadcast line", rowNum, row.Name);

                if (row.PortNumber == 0)
                {
                    problems.Add(string.IsNullOrWhiteSpace(row.Port)
                        ? "• " + who + " — no port number"
                        : "• " + who + " — " + row.Port.Trim() + " is not a port number (1 to 65535)");
                }

                if (string.IsNullOrWhiteSpace(row.Format))
                    problems.Add("• " + who + " — no format chosen");

                // Sending to a port this same program is listening on, on this same PC, would have
                // HolyLogger talking to itself. Nothing terrible happens (a contact that comes back is
                // caught by the duplicate rule), but it is never what was meant.
                if (row.PortNumber != 0 && listening.ContainsKey(row.PortNumber) && IsThisPc(row.Address))
                    problems.Add("• " + who + " — port " + row.PortNumber
                                 + " is one HolyLogger is listening on. Send to the other program's port instead.");
            }

            if (problems.Count > 0)
            {
                HolyMessageBox.ShowWarning(
                    "These lines cannot be saved:\n\n" + string.Join("\n", problems) +
                    "\n\nFix them, or click a line and press Delete to remove it.",
                    "UDP Ports Manager", this);
                return;   // keep the window open so they can be fixed
            }

            UdpPortStore.Save(_rows);
            UdpBroadcastStore.Save(_sendRows);
            DialogResult = true;
            Close();
        }

        private static string Who(string what, int number, string name)
        {
            return string.IsNullOrWhiteSpace(name)
                ? what + " " + number
                : what + " " + number + " (" + name.Trim() + ")";
        }

        // An empty address means this PC, and so do the names Windows itself uses for it.
        private static bool IsThisPc(string address)
        {
            string a = (address ?? string.Empty).Trim();
            return a.Length == 0
                || a == "127.0.0.1"
                || a.Equals("localhost", StringComparison.OrdinalIgnoreCase);
        }
    }
}
