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
    // A confirmation service answers with every card it holds for your callsigns. Most match a QSO; the
    // ones that do not fall into two kinds, and this window is for the first: the callsign appears
    // NOWHERE in the log, so the contact is simply not there. Nothing here can duplicate anything - that
    // is the whole reason the two kinds are separated before either is shown to anybody.
    //
    // Built for LoTW and now used by eQSL as well, which is why the service is a parameter and not
    // written into the text. The name stays LotwRestoreWindow only because renaming a window means
    // renaming its XAML class and its project entries for no gain to the operator.
    //
    // The two services do not keep the same things, and neither is padded out to look like the other:
    //   LoTW - when, with whom, band, mode, your callsign, the entity and the square. No report, ever.
    //   eQSL - the same, plus BOTH signal reports, and the submode and propagation mode when they are
    //          not blank. So a contact restored from eQSL comes back more complete.
    // Nothing is invented either way: an RST of 59 that nobody logged is not a record of anything.
    public partial class LotwRestoreWindow : Window
    {
        // Which service the confirmations came from. Decides the wording, the comment written on a
        // restored contact, the name of the safety copy, and which marker records the confirmation.
        public enum Source { Lotw, Eqsl }

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
            public string RstSent { get { return C.RstSent ?? ""; } }
            public string RstRcvd { get { return C.RstRcvd ?? ""; } }
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

        private readonly Source _source;

        // The service's name as the operator knows it, for every sentence and the stored comment.
        private string SourceName { get { return _source == Source.Eqsl ? "eQSL" : "LoTW"; } }

        public LotwRestoreWindow(IEnumerable<DataAccess.LotwConfirmation> missing, Source source = Source.Lotw)
        {
            InitializeComponent();
            _source = source;
            _missing = (missing ?? Enumerable.Empty<DataAccess.LotwConfirmation>())
                       .Where(c => c != null && !string.IsNullOrWhiteSpace(c.Call))
                       .OrderBy(c => c.QsoDate ?? "", StringComparer.Ordinal)
                       .ToList();

            Grid_Missing.ColumnHeaderStyle = MainWindow.BuildLogTableHeaderStyle();

            Title = "Contacts " + SourceName + " has and this log does not";

            // The reports are shown only where they exist. An empty column headed "RST Sent" reads as
            // information lost rather than information never held.
            bool hasReports = _source == Source.Eqsl;
            Col_RstSent.Visibility = hasReports ? Visibility.Visible : Visibility.Collapsed;
            Col_RstRcvd.Visibility = hasReports ? Visibility.Visible : Visibility.Collapsed;
            BuildExplanation(hasReports);

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

        // The sentence under the heading. Written here rather than in the XAML because it names the
        // service and, in its last line, says exactly what that service does NOT keep - which is
        // different for the two of them and is the operator's warning about what a restored contact
        // will be missing.
        private void BuildExplanation(bool hasReports)
        {
            TB_Explain.Inlines.Clear();
            TB_Explain.Inlines.Add(new System.Windows.Documents.Run(
                SourceName + " has these confirmed contacts and this log has no QSO with that callsign at all. "
                + "Tick the ones to put back and press "));
            // TryFindResource, not FindResource: the second one THROWS when the key is not there, and a
            // colour is not worth a window that fails to open. Falls back to the ordinary text colour.
            var danger = TryFindResource("Danger") as System.Windows.Media.Brush;
            TB_Explain.Inlines.Add(new System.Windows.Documents.Run("Add ticked to this log")
            {
                FontWeight = FontWeights.Bold,
                Foreground = danger ?? TB_Explain.Foreground
            });
            TB_Explain.Inlines.Add(new System.Windows.Documents.Run(
                ". They are added as confirmed by " + SourceName
                + ", and marked in the comment as having come from there."));
            TB_Explain.Inlines.Add(new System.Windows.Documents.LineBreak());
            TB_Explain.Inlines.Add(new System.Windows.Documents.Run(
                hasReports
                    ? SourceName + " sends both signal reports, so those come back too — but no name, no QTH "
                      + "and no comment of your own, so a restored contact has those fields empty."
                    : SourceName + " keeps only what you see here — no report, no name, no QTH — so a restored "
                      + "contact has those fields empty."));
        }

        private void UpdateButton()
        {
            int n = _rows.Count(r => r.Add);
            Btn_Add.IsEnabled = n > 0;
            Btn_Add.Content = n == 0 ? "Add ticked to this log" : "Add " + n.ToString("N0") + " to this log";
        }

        private void Btn_All_Click(object sender, RoutedEventArgs e) { foreach (var r in _rows) r.Add = true; }
        private void Btn_None_Click(object sender, RoutedEventArgs e) { foreach (var r in _rows) r.Add = false; }

        private async void Btn_Add_Click(object sender, RoutedEventArgs e)
        {
            List<Row> chosen = _rows.Where(r => r.Add).ToList();
            if (chosen.Count == 0) return;

            var dal = DataAccess.GetInstance();
            if (dal == null || !dal.HasActiveLog)
            {
                TB_Status.Text = "No log is open.";
                return;
            }

            List<QSO> qsos = chosen.Select(r => ToQso(r.C)).ToList();
            var picked = chosen.Select(r => r.C).ToList();

            // The count climbs as the rows go in - InsertBatch reports its own progress - so the wait is
            // visibly a wait for something. Progress<T> puts each report back on the UI thread itself.
            var report = new Progress<(string main, string sub)>(t =>
            {
                TB_WorkText.Text = t.main;
                TB_WorkSub.Text = t.sub;
            });
            var reporter = (IProgress<(string main, string sub)>)report;
            var insertProgress = new Progress<int>(done =>
                TB_WorkText.Text = "Adding contacts…  " + done.ToString("N0") + " of " + qsos.Count.ToString("N0"));

            // InsertBatch reports on EVERY row. Progress<T> posts to the UI thread at Normal priority,
            // which outranks Render - so forwarding all of them floods the dispatcher with work that
            // keeps jumping the queue ahead of drawing, and the number updates while the ring stops
            // turning. That is the other way a working program looks stuck. Five a second is plenty for
            // a human to read, and the last one is always sent so the count ends on the real total.
            var lastPost = System.Diagnostics.Stopwatch.StartNew();
            int total = qsos.Count;
            Action<int> throttled = done =>
            {
                if (done < total && lastPost.ElapsedMilliseconds < 200) return;
                lastPost.Restart();
                ((IProgress<int>)insertProgress).Report(done);
            };

            ShowWork(true, "Adding contacts…", "Copying the database first, so this can be undone.");
            SetBusy(true);

            int failedCount = 0, marked = 0;
            string failure = null;

            // ALL THREE STEPS ON A BACKGROUND THREAD. Copying a database this size, writing the rows and
            // then matching each one against the log is not instant, and doing it on the UI thread is
            // what made the window stop responding with no sign of whether it was working.
            await System.Threading.Tasks.Task.Run(() =>
            {
                // THE WHOLE DATABASE IS COPIED FIRST, exactly as the Log Fixer does before it writes.
                // This adds rows rather than changing them, which is the easier thing to undo - but
                // "easier" is not "does not need undoing", and the operator should never have to take
                // our word for it. The copy lands in the Backups folder with the others, viewable in
                // File > Backups & Restore.
                try
                {
                    string dbPath = dal.DbPath;
                    string copy = dal.SafetyCopyPath(SourceName.ToLowerInvariant() + "-restore");
                    if (!string.IsNullOrEmpty(dbPath) && copy != null && System.IO.File.Exists(dbPath))
                        System.IO.File.Copy(dbPath, copy, false);
                }
                catch (Exception swallowed) { Log.Swallow(swallowed); }

                // InsertBatch RETURNS THE NUMBER THAT FAILED, not the number written - it is the import
                // path's "how many were faulty" count. Read as an inserted count it says 0 on a
                // perfectly successful run, which is how 147 restored contacts came to report "0 added"
                // and left every figure on screen untouched: nothing below here ran.
                try
                {
                    failedCount = dal.InsertBatch(qsos, throttled);
                }
                catch (Exception ex)
                {
                    Log.Swallow(ex);
                    failure = ex.Message;
                    return;
                }

                // Mark them confirmed by the ordinary path - the same matcher the check itself uses - so
                // a restored contact carries its confirmation and its QSL-received date, and the next
                // check finds it matched instead of reporting it missing all over again. By the marker
                // belonging to the service the cards came from: a contact restored from eQSL is
                // confirmed at eQSL, and claiming LoTW for it would be an invented confirmation.
                reporter.Report(("Marking them confirmed by " + SourceName + "…",
                                 "Matching each restored contact to the log."));
                try
                {
                    marked = _source == Source.Eqsl
                           ? dal.MarkEqslConfirmed(picked, out _)
                           : dal.MarkLotwConfirmed(picked);
                }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
            });

            SetBusy(false);
            ShowWork(false, null, null);

            if (failure != null)
            {
                TB_Status.Text = "Could not add them: " + failure;
                return;
            }

            int inserted = qsos.Count - failedCount;

            Added = inserted;
            TB_Status.Text = inserted.ToString("N0") + " added, " + marked.ToString("N0")
                             + " marked confirmed by " + SourceName + "."
                             + (failedCount > 0 ? "  " + failedCount.ToString("N0") + " could not be written." : "");

            foreach (Row r in chosen) _rows.Remove(r);
            TB_Header.Text = _rows.Count == 0
                ? "Every missing contact has been put back."
                : _rows.Count.ToString("N0") + " left";
            UpdateButton();
        }

        // THE OVERLAY, ITS RING AND ITS CLOCK.
        //
        // The same spinner the confirmation download uses, for the same reason it was built there: a
        // turning ring on its own cannot tell "still working" from "hung", because it stops turning
        // exactly when the UI thread stalls. A clock that counts seconds can, and a count of contacts
        // written says what it is working ON.
        private System.Windows.Threading.DispatcherTimer _workClock;
        private DateTime _workStartedUtc;

        private void ShowWork(bool show, string main, string sub)
        {
            if (show)
            {
                TB_WorkText.Text = main ?? string.Empty;
                TB_WorkSub.Text = sub ?? string.Empty;
                TB_WorkElapsed.Text = "0:00";
                WorkOverlay.Visibility = Visibility.Visible;

                var spin = new System.Windows.Media.Animation.DoubleAnimation(0, 360,
                    new Duration(TimeSpan.FromSeconds(0.9)))
                {
                    RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever
                };
                SpinnerRotate.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, spin);

                _workStartedUtc = DateTime.UtcNow;
                if (_workClock == null)
                {
                    _workClock = new System.Windows.Threading.DispatcherTimer
                    {
                        Interval = TimeSpan.FromSeconds(1)
                    };
                    _workClock.Tick += (s, e) =>
                    {
                        TimeSpan t = DateTime.UtcNow - _workStartedUtc;
                        TB_WorkElapsed.Text = ((int)t.TotalMinutes) + ":" + t.Seconds.ToString("00");
                    };
                }
                _workClock.Start();
            }
            else
            {
                _workClock?.Stop();
                SpinnerRotate.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, null);
                WorkOverlay.Visibility = Visibility.Collapsed;
            }
        }

        // Nothing is clickable while the writing runs - including closing the window, which would leave
        // the insert half done with nobody to report it.
        private void SetBusy(bool busy)
        {
            Btn_Add.IsEnabled = !busy;
            Btn_All.IsEnabled = !busy;
            Btn_None.IsEnabled = !busy;
            Grid_Missing.IsEnabled = !busy;
            _busy = busy;
        }

        private bool _busy;

        protected override void OnClosing(CancelEventArgs e)
        {
            if (_busy) e.Cancel = true;   // mid-write; the overlay says so and it is a matter of seconds
            base.OnClosing(e);
        }

        // A confirmation as a QSO. Only what the service actually sent is filled in; nothing is invented
        // - an RST of 59 that nobody logged is not a record of anything, and a made-up value is worse
        // than an empty field because it cannot be told from a real one afterwards. The fields eQSL
        // sends and LoTW does not are simply empty for LoTW.
        private QSO ToQso(DataAccess.LotwConfirmation c)
        {
            return new QSO
            {
                RST_SENT = (c.RstSent ?? "").Trim(),
                RST_RCVD = (c.RstRcvd ?? "").Trim(),
                SUBMode = (c.SubMode ?? "").Trim().ToUpperInvariant(),
                PROP_MODE = (c.PropMode ?? "").Trim().ToUpperInvariant(),
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
                Comment = "Restored from " + SourceName,
            };
        }
    }
}
