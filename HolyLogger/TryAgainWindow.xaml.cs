using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace HolyLogger
{
    // TRY AGAIN: the stations the operator saw on the cluster and meant to come back to.
    //
    // The window owns nothing. The list is a table in the database, read fresh on every reload, so
    // this window and the main window's button can never disagree about what is on it - and closing
    // the program does not lose it. Everything that CHANGES the list is announced through
    // ListChanged, so the button's count follows along.
    //
    // Not owned by the main window (see ShowTryAgainWindow): an owned window is pinned above its
    // owner for ever, and this is a second place to work rather than a dialog.
    public partial class TryAgainWindow : Window
    {
        private readonly DataAccess dal;

        // The rows currently on show. Kept so the clock tick can reach them without going back to the
        // database - nothing in the database has changed, only the time of day.
        private List<TryAgainEntry> _rows = new List<TryAgainEntry>();

        // Drives the "Ago" column. 20 seconds rather than a minute: the column is written in whole
        // minutes, and a once-a-minute tick that happened to land just after a row rolled over would
        // leave "6 min" on screen for the best part of two minutes.
        private System.Windows.Threading.DispatcherTimer _agoTimer;


        // Pressed Try Again on a row: the main window puts the radio there. Raised, rather than done
        // here, because tuning a radio and filling the logging form is the main window's job and it
        // already has the code for it.
        public event Action<TryAgainEntry> TryRequested;

        // The list gained or lost a row. The main window re-counts and re-labels its button.
        public event Action ListChanged;

        public TryAgainWindow(DataAccess dataAccess)
        {
            InitializeComponent();
            dal = dataAccess;

            // Remembers where the operator put it and how big he made it, on whichever screen.
            WindowBounds.Attach(this, "TryAgain");

            ReloadList();

            _agoTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(20)
            };
            _agoTimer.Tick += AgoTimer_Tick;
            _agoTimer.Start();
            // A timer left running holds the window alive and goes on waking the program up for a
            // column nobody can see.
            Closed += (s, e) =>
            {
                if (_agoTimer != null) { _agoTimer.Stop(); _agoTimer.Tick -= AgoTimer_Tick; _agoTimer = null; }
            };
        }

        // Time has passed, so every row's "Ago" is now wrong by a little. Each row is told to re-read
        // that one property; the table is NOT rebuilt, so the operator's selected row and his place in
        // the list survive the tick.
        private void AgoTimer_Tick(object sender, EventArgs e)
        {
            try
            {
                foreach (TryAgainEntry row in _rows)
                    row.NotifyAgoChanged();
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // Reads the list back from the database. Called on open, after a delete, and by the main
        // window whenever a spot is copied in or a station is logged.
        public void ReloadList()
        {
            try
            {
                List<TryAgainEntry> rows = dal != null ? dal.GetTryAgainList() : new List<TryAgainEntry>();
                foreach (TryAgainEntry row in rows)
                    FillCountry(row);
                _rows = rows;
                Grid_TryAgain.ItemsSource = rows;
                bool any = rows.Count > 0;
                Grid_TryAgain.Visibility = any ? Visibility.Visible : Visibility.Hidden;
                TB_Empty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
                Title = any ? string.Format("Try Again ({0})", rows.Count) : "Try Again";
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // THE FLAG BESIDE THE CALLSIGN. Worked out from the callsign here rather than stored with the
        // row, so the answer is always the current country file's answer - an entry that went on the
        // list before a prefix changed hands shows the right flag once the file is updated.
        //
        // Same two steps the cluster uses: the callsign gives a DXCC country, the country's name gives
        // an ISO code, and the code names an image already built into the program. A callsign nothing
        // recognises simply has no flag - the row is still perfectly usable without one.
        private static void FillCountry(TryAgainEntry row)
        {
            if (row == null) return;
            try
            {
                // THE FREQUENCY IN ITS BAND'S COLOUR, from the very method the cluster's own frequency
                // column uses - so 40m is the same red in both tables, and a colour the operator has
                // customised follows here too. A row with no band (one added before the band was kept)
                // gets the ordinary text colour back, which is what GetBandBrush does with a blank.
                row.FreqBrush = MainWindow.GetBandBrush((row.Band ?? string.Empty).Trim());
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }

            try
            {
                var dxcc = DXCCManager.CountryLookup.Shared.Resolve((row.DXCallsign ?? string.Empty).Trim());
                string name = dxcc != null ? dxcc.Name : null;
                if (string.IsNullOrWhiteSpace(name)) return;

                row.Country = name;
                string iso;
                if (MainWindow.DxccNameToIso.TryGetValue(name, out iso) && !string.IsNullOrWhiteSpace(iso))
                    row.FlagPath = "pack://application:,,,/Images/flags/" + iso + ".png";
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        private void Btn_Try_Click(object sender, RoutedEventArgs e)
        {
            var entry = (sender as FrameworkElement)?.DataContext as TryAgainEntry;
            if (entry == null) return;

            // Hand it over BEFORE closing: closing raises Closed, which lets the main window drop its
            // reference to this window, and the handler must already have run by then.
            var handler = TryRequested;
            if (handler != null) handler(entry);

            // The operator asked for the radio to be put on this station, which means he is about to
            // work him - in the main window, not here. So this window gets out of the way.
            Close();
        }

        // RIGHT-CLICK A ROW: Delete. The menu is built here rather than declared in the XAML so the
        // row it belongs to is captured directly, with no question about which row a shared menu
        // instance is currently pointing at.
        private void Grid_TryAgain_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            try
            {
                var entry = RowUnderMouse(e.OriginalSource as DependencyObject);
                if (entry == null) return;      // header, or empty space below the last row

                // Paint the row the menu is about, so there is no doubt which station is being deleted.
                // Any earlier mark goes first: only ever one row is the one in question.
                ClearMarks();
                entry.IsMarked = true;

                var menu = new ContextMenu { Style = (Style)FindResource("HolyCtxMenu") };
                var del = new MenuItem
                {
                    Header = "Delete " + (entry.DXCallsign ?? string.Empty),
                    Style = (Style)FindResource("HolyCtxItemDanger"),
                    Icon = new TextBlock
                    {
                        Text = "",
                        FontFamily = new FontFamily("Segoe MDL2 Assets"),
                        FontSize = 16,
                        Foreground = new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28)),
                        VerticalAlignment = VerticalAlignment.Center,
                        HorizontalAlignment = HorizontalAlignment.Center
                    }
                };
                del.Click += (s, args) => DeleteEntry(entry);
                menu.Items.Add(del);

                menu.PlacementTarget = Grid_TryAgain;
                menu.IsOpen = true;
                e.Handled = true;
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // THE PAINTED ROW BELONGS TO THE RIGHT-CLICK AND TO NOTHING ELSE.
        //
        // A green row here means one thing: "this is the station the menu is about to delete". If a left
        // click painted one too, a row left over from a menu the operator thought better of would look
        // exactly like a row he had just picked out to delete - and the moment before a delete is the
        // one moment that has to be unambiguous.
        //
        // The mark is a flag on the row itself, NOT the DataGrid's selection. Two attempts were made to
        // borrow the selection and both failed on the same fact - the grid decides when it selects, and
        // this window does not get to say no. Undoing the selection inside SelectionChanged did nothing
        // at all, because the grid discards a selection changed from within its own transaction; undoing
        // it afterwards worked, but the row was painted for a frame first and then unpainted, which
        // looks like a fault. Nothing is painted here that was not asked for, so there is nothing to
        // take back.
        private void ClearMarks()
        {
            foreach (TryAgainEntry row in _rows)
                row.IsMarked = false;
        }

        private void Grid_TryAgain_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            ClearMarks();
        }

        // Which row the pointer is on. The cell's DataContext is the direct answer; the walk up to the
        // row covers a click that landed on the padding between cells.
        //
        // THE WALK HAS TO CROSS OUT OF THE TEXT. A Run is a FrameworkContentElement, not a Visual, and
        // VisualTreeHelper.GetParent THROWS when handed one - so a right-click that landed on the words
        // inside a Run raised an exception the handler quietly swallowed, and no menu appeared at all.
        // It showed up on the Ago column because that is the only column built out of Runs, to print
        // the number in bold and the unit beside it in plain text. Content elements are climbed by
        // their logical parent until the walk is back among visuals.
        private TryAgainEntry RowUnderMouse(DependencyObject source)
        {
            while (source != null && !(source is DataGridRow) && !(source is DataGridCell))
            {
                if (source is Visual || source is System.Windows.Media.Media3D.Visual3D)
                {
                    source = VisualTreeHelper.GetParent(source);
                }
                else
                {
                    var content = source as FrameworkContentElement;
                    if (content == null) return null;
                    source = content.Parent;
                }
            }

            var cell = source as DataGridCell;
            if (cell != null) return cell.DataContext as TryAgainEntry;

            var row = source as DataGridRow;
            return row?.Item as TryAgainEntry;
        }

        // No confirmation and no undo: one row of a to-do list is not a QSO, and it can be put back by
        // right-clicking the spot again. A question here would be in the way every single time.
        private void DeleteEntry(TryAgainEntry entry)
        {
            if (entry == null || dal == null) return;
            try
            {
                dal.RemoveTryAgain(entry.Id);
                ReloadList();
                var handler = ListChanged;
                if (handler != null) handler();
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        private void Btn_Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
