using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace HolyLogger
{
    // THE UDP PORTS TABLE.
    //
    // Replaces the two fixed "Enable UDP Client" / "N1MM+ UDP Client" ports that used to sit on the
    // Options > General page. Any number of lines now, each with a name of the operator's choosing and
    // a port; a ticked line is a port the program listens on. Contacts arriving on any of them are
    // stored in the log - what format they are in is read from the datagram, not from the name.
    //
    // Save writes the table and opens/closes the sockets to match. Cancel leaves everything as it was.
    public partial class UdpPortsWindow : Window
    {
        private readonly ObservableCollection<UdpPortEntry> _rows = new ObservableCollection<UdpPortEntry>();

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

            // Deleting a row (the Delete key) must never leave the table without a blank row to type in.
            // Deferred for the same reentrancy reason as Row_PropertyChanged below.
            _rows.CollectionChanged += (s, e) =>
            {
                if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Remove)
                    Dispatcher.BeginInvoke(new Action(EnsureTrailingEmptyRow),
                        System.Windows.Threading.DispatcherPriority.Background);
            };
            PortsGrid.ItemsSource = _rows;

            // Same header look as every other table in the program.
            PortsGrid.ColumnHeaderStyle = MainWindow.BuildLogTableHeaderStyle();
        }

        // Digits only in the Port cell.
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

        private void Hook(UdpPortEntry row)
        {
            if (row == null) return;
            row.PropertyChanged -= Row_PropertyChanged;   // never double-subscribe
            row.PropertyChanged += Row_PropertyChanged;
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

        // The ? button. Says what a program can send and what HolyLogger does with it - including
        // HolyCluster, which is a line in this table like any other program now.
        private void BtnWhat_Click(object sender, RoutedEventArgs e)
        {
            // No hand-made line breaks inside a paragraph: the box wraps to the width it is given, and
            // a break of ours would then wrap a second time. Measured at 700 with the msgsize harness -
            // 700 x 506, nothing scrolls, so every word is on screen.
            HolyMessageBox.Show(
                "HolyLogger listens on every port you tick here.\n\n"
                + "You do not have to say what a program sends. HolyLogger reads what arrives:\n\n"
                + "•  A contact — from WSJT-X, JTDX, MSHV, N1MM+, or any program that sends it as ADIF. It is stored in your log.\n\n"
                + "•  N1MM+'s radio — its frequency and mode go into the entry boxes.\n\n"
                + "•  A selected spot — from HolyCluster (it usually sends on port 2237). The callsign goes into the DX Callsign field, and the frequency too when CAT is not connected.\n\n"
                + "Give each program its own line, with the port that program sends on. A contact you already have is not stored twice, whichever port it came in on.",
                "UDP Ports Manager", HolyMsgType.Info, this, 700);
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

        private void BtnCancel_Click(object sender, RoutedEventArgs e) => Close();

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            // Commit whatever cell is still being typed in, so a port just entered is included.
            PortsGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Cell, true);
            PortsGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, true);

            var problems = new List<string>();
            var seen = new Dictionary<int, string>();
            int rowNum = 0;
            foreach (var row in _rows)
            {
                rowNum++;
                if (!row.IsFilled) continue;   // the blank row at the bottom

                string who = string.IsNullOrWhiteSpace(row.Name)
                    ? "Line " + rowNum
                    : "Line " + rowNum + " (" + row.Name.Trim() + ")";

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
                if (seen.TryGetValue(row.PortNumber, out other))
                    problems.Add("• " + who + " — port " + row.PortNumber + " is already used by " + other);
                else
                    seen[row.PortNumber] = who;
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
            DialogResult = true;
            Close();
        }
    }
}
