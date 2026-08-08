using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace HolyLogger
{
    // The log manager: lists every Log with its stats, and lets the user open (activate), rename,
    // delete, or export a selected log to ADIF / Cabrillo.
    public partial class ViewLogsWindow : Window
    {
        private readonly MainWindow _main;
        private readonly DataAccess _dal;
        private readonly string _filterCallsign;   // when set, list only logs whose identity callsign matches

        // One grid row.
        public class Row
        {
            public int Num { get; set; }
            public string Name { get; set; }
            public string EventType { get; set; }
            public string StartDate { get; set; }
            public string EndDate { get; set; }
            public int QsoCount { get; set; }
            public long Id { get; set; }
            public bool IsContest { get; set; }
            public bool IsActive { get; set; }
            public string Identity { get; set; }   // "callsign / operator" for the copy filter
            public string CopiesTo { get; set; }    // target log name, or "—"
            public long? CopyTargetLogId { get; set; }
            public string Callsign { get; set; }
            public string Operator { get; set; }
        }

        public ViewLogsWindow(MainWindow main, DataAccess dal, string filterCallsign = null)
        {
            InitializeComponent();
            WindowBounds.Attach(this, "ViewLogs");   // remember position + size
            _main = main;
            _dal = dal;
            _filterCallsign = (filterCallsign ?? string.Empty).Trim();

            // Columns are Auto-width (they size to their own content), and the window is
            // SizeToContent="Width" so it grows to fit -- but it must never grow past the screen's
            // usable width (excludes the taskbar), so cap it here rather than in XAML.
            MaxWidth = SystemParameters.WorkArea.Width;

            // Same header look as the QSO log table (user's chosen color, default burlywood).
            LogsGrid.ColumnHeaderStyle = MainWindow.BuildLogTableHeaderStyle();

            // Opened because the operator changed the station callsign: show ONLY logs for that callsign
            // (logs for other callsigns are irrelevant here and would confuse) and explain the view.
            if (_filterCallsign.Length > 0)
            {
                Title = "Logs for " + _filterCallsign;
                Hint.Text = "Showing only logs for station callsign " + _filterCallsign +
                            ". Select one and Activate & Open it, or create a new log — its identity is " +
                            "set to " + _filterCallsign + " automatically.";
            }

            LoadLogs();
        }

        private void LoadLogs()
        {
            var allLogs = _dal.GetLogs();
            // Target-name lookup uses ALL logs so a filtered view still resolves a copy-target that
            // happens to belong to a different callsign.
            var nameById = allLogs.ToDictionary(l => l.Id, l => l.Name);
            var logs = allLogs.AsEnumerable();
            if (_filterCallsign.Length > 0)
                logs = logs.Where(l => CallsignIdentity.Same(l.Callsign, _filterCallsign));
            var rows = new List<Row>();
            int n = 1;
            foreach (var li in logs)
            {
                bool isContest = !string.IsNullOrEmpty(li.EventType);
                string eventDisplay = isContest
                    ? (Contests.ContestService.FindById(li.EventType)?.Name ?? li.EventType)
                    : "General";
                string copiesTo = "—";
                if (li.CopyTargetLogId.HasValue && nameById.TryGetValue(li.CopyTargetLogId.Value, out var tName))
                    copiesTo = tName;
                rows.Add(new Row
                {
                    Num = n++,
                    Name = li.Name,
                    IsActive = li.Id == _dal.ActiveLogId,
                    EventType = eventDisplay,
                    StartDate = FormatQsoDate(li.StartDate),
                    EndDate = FormatQsoDate(li.EndDate),
                    QsoCount = li.QsoCount,
                    Id = li.Id,
                    IsContest = isContest,
                    Identity = BuildIdentity(li.Callsign, li.Operator),
                    CopiesTo = copiesTo,
                    CopyTargetLogId = li.CopyTargetLogId,
                    Callsign = li.Callsign,
                    Operator = li.Operator,
                });
            }
            LogsGrid.ItemsSource = rows;

            // Pre-select the active log so Export ADIF/Cabrillo, Rename and Delete default to it
            // (and a single log always appears selected). SelectionChanged then updates Open Log.
            var preselect = rows.FirstOrDefault(r => r.Id == _dal.ActiveLogId) ?? rows.FirstOrDefault();
            if (preselect != null) LogsGrid.SelectedItem = preselect;
        }

        // QSO dates are stored as YYYYMMDD; show them as e.g. "30 Sep 2006". Empty stays empty.
        private static string FormatQsoDate(string yyyymmdd)
        {
            if (string.IsNullOrWhiteSpace(yyyymmdd)) return string.Empty;
            if (DateTime.TryParseExact(yyyymmdd.Trim(), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                return d.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);
            return yyyymmdd;   // unexpected format -> show as-is
        }

        // "callsign / operator", or "—" when the log has no identity set yet.
        private static string BuildIdentity(string call, string opr)
        {
            call = (call ?? string.Empty).Trim();
            opr = (opr ?? string.Empty).Trim();
            if (call.Length == 0 && opr.Length == 0) return "—";
            return call + " / " + opr;
        }

        private Row Selected => LogsGrid.SelectedItem as Row;

        // Gives an identity-less log its permanent identity (pre-filled from its own QSOs, or the main
        // window if empty). A log's identity is set once and can't be changed.
        private void Btn_SetIdentity_Click(object sender, RoutedEventArgs e)
        {
            if (!RequireSelection()) return;
            if (_dal.LogHasIdentity(Selected.Id))
            {
                HolyMessageBox.Show("This log's identity is already set (" + Selected.Identity + ") and is permanent.",
                    "Log identity", HolyMsgType.Info, this);
                return;
            }
            var candidates = _dal.GetStationIdentitiesInLog(Selected.Id);
            var dlg = new SetIdentityWindow(candidates, _dal.GetLogName(Selected.Id),
                                            _main.CurrentStationCallsign, _main.CurrentOperator) { Owner = this };
            if (dlg.ShowDialog() != true) return;
            _dal.SetLogIdentity(Selected.Id, dlg.Callsign, dlg.Operator);
            _main.RefreshCopyIndicator();
            LoadLogs();
        }

        // Opens the per-log Copy settings dialog: set/change/stop the copy-target and edit identity.
        private void Btn_CopySettings_Click(object sender, RoutedEventArgs e)
        {
            if (!RequireSelection()) return;
            var dlg = new CopySettingsWindow(_dal, Selected.Id, Selected.Callsign, Selected.Operator,
                                             Selected.CopyTargetLogId) { Owner = this };
            if (dlg.ShowDialog() != true) return;
            _dal.SetCopyTarget(Selected.Id, dlg.CopyTargetLogId);   // identity is permanent; only the target changes here
            // If the chosen target has no identity yet, copying can't start until that log gets one.
            if (dlg.CopyTargetLogId.HasValue && !_dal.LogHasIdentity(dlg.CopyTargetLogId.Value))
                HolyMessageBox.ShowWarning(
                    "The target log has no identity yet, so copying won't start until you open it (make it active) and set its station callsign + operator.",
                    "Copy settings", this);
            _main.RefreshCopyIndicator();   // the active log's copy state may have changed
            LoadLogs();
        }

        // "Open Log" is only meaningful for a log that is NOT already the active one.
        private void LogsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            Btn_Open.IsEnabled = Selected != null && Selected.Id != _dal.ActiveLogId;
            // "Set Identity" only applies to a log that has no identity yet — grey it out once set (permanent).
            Btn_SetIdentity.IsEnabled = Selected != null
                && (string.IsNullOrEmpty(Selected.Callsign) || string.IsNullOrEmpty(Selected.Operator));
        }

        // Searches ANY log, not just the one being logged into. The Search window works on the QSO
        // list it is handed, so the selected log's QSOs are simply loaded and passed straight to it -
        // no need to activate the log first, which would disturb where new QSOs are being written.
        //
        // Edits made in that window go to the database through DataAccess.Update, so they land in the
        // right log whichever one is active.
        // Verify the SELECTED log, like Search In Log: the log does not have to be activated, so the log
        // being worked in is left alone. Modal, because it writes corrections and the list on screen must
        // not go stale underneath the operator while he ticks rows.
        private void Btn_Verify_Click(object sender, RoutedEventArgs e)
        {
            if (!RequireSelection()) return;

            long id = Selected.Id;
            string name = Selected.Name;
            try
            {
                var qsos = _dal.GetQSOsForLog(id);
                if (qsos == null || qsos.Count == 0)
                {
                    HolyMessageBox.ShowWarning($"\"{name}\" has no QSOs to check.", "Verify Log", this);
                    return;
                }

                var verifier = new LogVerifierWindow(qsos, name) { Owner = this };
                verifier.ShowDialog();
                LoadLogs();   // a corrected callsign or band can change what this list shows
            }
            catch (Exception ex)
            {
                HolyMessageBox.ShowError("Could not check this log.\n\n" + ex.Message, "Verify Log", this);
            }
        }

        private void Btn_Search_Click(object sender, RoutedEventArgs e)
        {
            if (!RequireSelection()) return;

            long id = Selected.Id;
            string name = Selected.Name;
            try
            {
                var qsos = _dal.GetQSOsForLog(id);
                if (qsos == null || qsos.Count == 0)
                {
                    HolyMessageBox.ShowWarning($"\"{name}\" has no QSOs to search.", "Search Log", this);
                    return;
                }

                var search = new SearchWindow(qsos, name) { Owner = this };
                search.Show();
            }
            catch (Exception ex)
            {
                HolyMessageBox.ShowError("Could not open the search for this log.\n\n" + ex.Message,
                                         "Search Log", this);
            }
        }

        private bool RequireSelection()
        {
            if (Selected == null)
            {
                HolyMessageBox.ShowWarning("Please select a log first.", "Logs", this);
                return false;
            }
            return true;
        }

        private void OpenSelected()
        {
            if (!RequireSelection()) return;
            if (Selected.Id == _dal.ActiveLogId)
            {
                // Already active: just close the manager and bring main window to foreground
                Close();
                try { _main.Activate(); _main.Focus(); } catch { }
                return;
            }
            _main.SwitchActiveLog(Selected.Id);
            Close();
            try { _main.Activate(); _main.Focus(); } catch { }
        }

        private void Btn_Open_Click(object sender, RoutedEventArgs e) => OpenSelected();

        private void Btn_NewContestLog_Click(object sender, RoutedEventArgs e)
        {
            if (_main.CreateNewContestLog(this)) Close();
        }

        private void Btn_NewRegularLog_Click(object sender, RoutedEventArgs e)
        {
            if (_main.CreateNewRegularLog(this)) Close();
        }

        private void LogsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (Selected != null) OpenSelected();
        }

        private void Btn_Rename_Click(object sender, RoutedEventArgs e)
        {
            if (!RequireSelection()) return;
            long id = Selected.Id;
            string current = _dal.GetLogName(id);
            var dlg = new NewLogWindow(_dal, "Enter a new name for this log:", current, id) { Owner = this };
            if (dlg.ShowDialog() != true) return;
            _dal.RenameLog(id, dlg.LogName);
            if (id == _dal.ActiveLogId) _main.UpdateActiveLogTitle();
            LoadLogs();
        }

        private void Btn_Delete_Click(object sender, RoutedEventArgs e)
        {
            if (!RequireSelection()) return;
            long id = Selected.Id;

            if (id == _dal.ActiveLogId)
            {
                HolyMessageBox.ShowWarning("This log is currently open. Open a different log first, then delete this one.", "Delete Log", this);
                return;
            }
            if (_dal.GetLogCount() <= 1)
            {
                HolyMessageBox.ShowWarning("You cannot delete your only log.", "Delete Log", this);
                return;
            }

            // Warn if OTHER logs copy their new QSOs INTO this one — deleting it turns their copying off.
            var sources = (LogsGrid.ItemsSource as System.Collections.Generic.IEnumerable<Row>)
                          ?.Where(r => r.CopyTargetLogId == id).Select(r => r.Name).ToList()
                          ?? new System.Collections.Generic.List<string>();
            string copyNote = sources.Count > 0
                ? "\n\nNote: " + string.Join(", ", sources) + " " + (sources.Count == 1 ? "copies" : "copy") +
                  " new QSOs into this log; that copying will be turned off. QSOs already copied elsewhere are not affected."
                : string.Empty;

            if (!HolyMessageBox.ShowConfirm(
                    "Delete the log \"" + _dal.GetLogName(id) + "\" and ALL " + Selected.QsoCount.ToString("N0") +
                    " QSO(s) in it?\n\nThis permanently removes those QSOs from the database and cannot be undone." + copyNote,
                    "Delete Log", HolyMsgType.Warning, this))
                return;

            _dal.DeleteLog(id);
            _main.RefreshCopyIndicator();
            LoadLogs();
        }

        // Import runs on the main window with its own dialogs, so close the Log Manager first (it's modal),
        // then kick off the import once this window is gone. Reopen the Log Manager to see the new log.
        private void Btn_Import_Click(object sender, RoutedEventArgs e)
        {
            Close();
            _main.Dispatcher.BeginInvoke(new System.Action(() => _main.ImportAdif()),
                System.Windows.Threading.DispatcherPriority.Background);
        }

        private void Btn_Adif_Click(object sender, RoutedEventArgs e)
        {
            if (!RequireSelection()) return;
            _main.ExportQsosToAdif(_dal.GetQSOsForLog(Selected.Id), this);
        }

        private void Btn_Cabrillo_Click(object sender, RoutedEventArgs e)
        {
            if (!RequireSelection()) return;
            if (!Selected.IsContest)
            {
                HolyMessageBox.ShowWarning("Cabrillo export is for contest logs. This log is a normal (day-by-day) log — use Export ADIF instead.", "Export Cabrillo", this);
                return;
            }
            _main.ExportQsosToCabrillo(_dal.GetQSOsForLog(Selected.Id), Selected.Id, this);
        }
    }
}
