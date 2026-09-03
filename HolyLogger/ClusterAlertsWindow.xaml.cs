using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace HolyLogger
{
    // The cluster's watch list, opened from the bell under the "Latest" button. One column, typed by
    // hand: every callsign here is one the operator does not want to miss. A spot for it rings the
    // new-country sound, is pinned to the top row of the cluster table, and is framed in yellow -
    // and it is shown even when its band or mode is filtered out, which is the whole point of a watch.
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
                        _calls.Add(new AlertCall { Call = call });
            }
            finally { _loading = false; }

            GRD_Calls.ItemsSource = _calls;
            // Covers the Delete key and the row the user removes with it; typed edits come through
            // Calls_CellEditEnding, which fires before the item is in the collection.
            _calls.CollectionChanged += (a, b) => SaveList();

            Closing += (a, b) => { SaveList(); SaveBounds_(); };
        }

        // The grid commits the cell AFTER this event, so the save is queued behind it - reading the
        // collection here would still show the pre-edit text.
        private void Calls_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            if (_loading) return;
            Dispatcher.BeginInvoke(new Action(SaveList), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void BtnRemove_Click(object sender, RoutedEventArgs e)
        {
            var row = GRD_Calls.SelectedItem as AlertCall;
            if (row != null) _calls.Remove(row);   // CollectionChanged saves
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
                string call = (row == null ? null : row.Call) ?? string.Empty;
                if (call.Length == 0) continue;
                if (!seen.Add(call)) continue;
                list.Add(call);
            }
            _main.SetClusterAlertCallsigns(list);
        }

        // One row of the grid. The setter does the tidying (trim + upper) so a callsign typed in
        // lower case, or with a stray space, is stored in the one shape the matcher expects.
        public sealed class AlertCall : INotifyPropertyChanged
        {
            private string _call = string.Empty;
            public string Call
            {
                get { return _call; }
                set
                {
                    string v = (value ?? string.Empty).Trim().ToUpperInvariant();
                    if (_call == v) return;
                    _call = v;
                    var h = PropertyChanged;
                    if (h != null) h(this, new PropertyChangedEventArgs("Call"));
                }
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
