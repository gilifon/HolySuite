using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using HolyParser;

namespace HolyLogger
{
    // PUTTING BACK CONTACTS THE LOG HAS LOST.
    //
    // LoTW answers with every confirmation it holds for your callsigns. Most match a QSO; the ones that
    // do not fall into two kinds, and this window is for the first: the callsign appears NOWHERE in the
    // log, so the contact is simply not there. Nothing here can duplicate anything - that is the whole
    // reason the two kinds were separated before either was shown to anybody.
    //
    // What comes back is what LoTW keeps: when, with whom, on what band and mode, from which of your
    // callsigns, plus the entity and the square. It has never held a report, a name or a comment, so a
    // restored contact has those empty and says where it came from in its comment.
    public partial class LotwRestoreWindow : Window
    {
        public sealed class Row : INotifyPropertyChanged
        {
            public DataAccess.LotwConfirmation C;

            private bool add = true;    // ticked to begin with: they are here BECAUSE they are missing
            public bool Add
            {
                get { return add; }
                set
                {
                    if (add == value) return;
                    add = value;
                    if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs("Add"));
                    if (Changed != null) Changed();
                }
            }

            public Action Changed;
            public event PropertyChangedEventHandler PropertyChanged;

            public string Call { get { return C.Call ?? ""; } }
            public string Band { get { return (C.Band ?? "").ToUpperInvariant(); } }
            public string Mode { get { return (C.Mode ?? "").ToUpperInvariant(); } }
            public string Country { get { return C.Country ?? ""; } }
            public string Grid { get { return (C.Grid ?? "").ToUpperInvariant(); } }
            public string MyCall { get { return C.StationCallsign ?? ""; } }

            public string DateText
            {
                get
                {
                    DateTime d;
                    return DateTime.TryParseExact(C.QsoDate ?? "", "yyyyMMdd", CultureInfo.InvariantCulture,
                                                  DateTimeStyles.None, out d)
                        ? d.ToString("dd-MM-yyyy") : (C.QsoDate ?? "");
                }
            }

            public string TimeText
            {
                get
                {
                    string t = (C.TimeOn ?? "").Trim();
                    return t.Length >= 4 ? t.Substring(0, 2) + ":" + t.Substring(2, 2) : t;
                }
            }
        }

        private readonly ObservableCollection<Row> _rows = new ObservableCollection<Row>();
        private readonly List<DataAccess.LotwConfirmation> _missing;

        // How many were actually put back, so the window that opened this one can say so.
        public int Added { get; private set; }

        public LotwRestoreWindow(IEnumerable<DataAccess.LotwConfirmation> missing)
        {
            InitializeComponent();
            _missing = (missing ?? Enumerable.Empty<DataAccess.LotwConfirmation>())
                       .Where(c => c != null && !string.IsNullOrWhiteSpace(c.Call))
                       .OrderBy(c => c.QsoDate ?? "", StringComparer.Ordinal)
                       .ToList();

            Grid_Missing.ColumnHeaderStyle = MainWindow.BuildLogTableHeaderStyle();

            foreach (var c in _missing)
            {
                var r = new Row { C = c };
                r.Changed = UpdateButton;
                _rows.Add(r);
            }
            Grid_Missing.ItemsSource = _rows;

            TB_Header.Text = _rows.Count.ToString("N0")
                + (_rows.Count == 1 ? " confirmed contact is missing from this log"
                                    : " confirmed contacts are missing from this log");
            UpdateButton();
        }

        private void UpdateButton()
        {
            int n = _rows.Count(r => r.Add);
            Btn_Add.IsEnabled = n > 0;
            Btn_Add.Content = n == 0 ? "Add ticked to this log" : "Add " + n.ToString("N0") + " to this log";
        }

        private void Btn_All_Click(object sender, RoutedEventArgs e) { foreach (var r in _rows) r.Add = true; }
        private void Btn_None_Click(object sender, RoutedEventArgs e) { foreach (var r in _rows) r.Add = false; }

        private void Btn_Add_Click(object sender, RoutedEventArgs e)
        {
            List<Row> chosen = _rows.Where(r => r.Add).ToList();
            if (chosen.Count == 0) return;

            var dal = DataAccess.GetInstance();
            if (dal == null || !dal.HasActiveLog)
            {
                TB_Status.Text = "No log is open.";
                return;
            }

            // THE WHOLE DATABASE IS COPIED FIRST, exactly as the Log Fixer does before it writes. This
            // adds rows rather than changing them, which is the easier thing to undo - but "easier" is
            // not "does not need undoing", and the operator should never have to take our word for it.
            // The copy lands in the Backups folder with the others, viewable in File > Backups & Restore.
            try
            {
                string dbPath = dal.DbPath;
                string copy = dal.SafetyCopyPath("lotw-restore");
                if (!string.IsNullOrEmpty(dbPath) && copy != null && System.IO.File.Exists(dbPath))
                    System.IO.File.Copy(dbPath, copy, false);
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }

            List<QSO> qsos = chosen.Select(r => ToQso(r.C)).ToList();

            // InsertBatch RETURNS THE NUMBER THAT FAILED, not the number written - it is the import
            // path's "how many were faulty" count. Read as an inserted count it says 0 on a perfectly
            // successful run, which is how 147 restored contacts came to report "0 added" and left every
            // figure on screen untouched: nothing below here ran.
            int failedCount, inserted;
            try
            {
                failedCount = dal.InsertBatch(qsos);
                inserted = qsos.Count - failedCount;
            }
            catch (Exception ex)
            {
                Log.Swallow(ex);
                TB_Status.Text = "Could not add them: " + ex.Message;
                return;
            }

            // Mark them confirmed by the ordinary path - the same matcher the check itself uses - so a
            // restored contact carries its LoTW confirmation and its QSL-received date, and the next
            // check finds it matched instead of reporting it missing all over again.
            int marked = 0;
            try { marked = dal.MarkLotwConfirmed(chosen.Select(r => r.C).ToList()); }
            catch (Exception swallowed) { Log.Swallow(swallowed); }

            Added = inserted;
            TB_Status.Text = inserted.ToString("N0") + " added, " + marked.ToString("N0")
                             + " marked confirmed by LoTW."
                             + (failedCount > 0 ? "  " + failedCount.ToString("N0") + " could not be written." : "");

            foreach (Row r in chosen) _rows.Remove(r);
            TB_Header.Text = _rows.Count == 0
                ? "Every missing contact has been put back."
                : _rows.Count.ToString("N0") + " left";
            UpdateButton();
        }

        // A confirmation as a QSO. Only what LoTW actually sent is filled in; nothing is invented - an
        // RST of 59 that nobody logged is not a record of anything, and a made-up value is worse than an
        // empty field because it cannot be told from a real one afterwards.
        private static QSO ToQso(DataAccess.LotwConfirmation c)
        {
            return new QSO
            {
                MyCall = (c.StationCallsign ?? "").Trim().ToUpperInvariant(),
                DXCall = (c.Call ?? "").Trim().ToUpperInvariant(),
                Date = (c.QsoDate ?? "").Trim(),
                Time = (c.TimeOn ?? "").Trim(),
                Band = (c.Band ?? "").Trim().ToUpperInvariant(),
                Mode = (c.Mode ?? "").Trim().ToUpperInvariant(),
                Country = (c.Country ?? "").Trim(),
                DxccCode = c.DxccCode,
                Continent = (c.Continent ?? "").Trim().ToUpperInvariant(),
                CQZone = (c.CqZone ?? "").Trim(),
                ITUZone = (c.ItuZone ?? "").Trim(),
                DXLocator = (c.Grid ?? "").Trim().ToUpperInvariant(),
                Comment = "Restored from LoTW",
            };
        }
    }
}
