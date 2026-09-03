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

        // CONTEST LOGS ONLY. Set when this window is opened from the Activity list's Contest line by
        // somebody who says he already has a contest log: showing him his forty general logs as well
        // makes him hunt for the one he came for. Unlike the callsign filter above, this one is NOT
        // dropped when it matches nothing - the caller has already checked there is something to show.
        private readonly bool _contestOnly;

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
            public string Identity { get; set; }      // the log's station callsign (one of them: sorting + the plain cell)
            public string IdentityTip { get; set; }   // all of them, one per line, for the tooltip
            public List<string> Callsigns { get; set; }   // current first, then the older ones
            // More than one -> the cell is a drop-down instead of a line of text, so a log with a
            // lifetime of callsigns behind it never has to widen the column to be read.
            public bool HasManyCallsigns { get; set; }
            public string CopiesTo { get; set; }    // target log name, or "—"
            public long? CopyTargetLogId { get; set; }
            public string Callsign { get; set; }
            public string Operator { get; set; }
        }

        public ViewLogsWindow(MainWindow main, DataAccess dal, string filterCallsign = null, bool contestOnly = false)
        {
            InitializeComponent();
            WindowBounds.Attach(this, "ViewLogs");   // remember position + size
            _main = main;
            _dal = dal;
            _filterCallsign = (filterCallsign ?? string.Empty).Trim();
            _contestOnly = contestOnly;

            // Columns are Auto-width (they size to their own content), and the window is
            // SizeToContent="Width" so it grows to fit -- but it must never grow past the screen's
            // usable width (excludes the taskbar), so cap it here rather than in XAML.
            MaxWidth = SystemParameters.WorkArea.Width;

            // Same header look as the QSO log table (user's chosen color, default burlywood).
            LogsGrid.ColumnHeaderStyle = MainWindow.BuildLogTableHeaderStyle();

            // Opened because the operator changed the station callsign: show ONLY logs for that callsign
            // (logs for other callsigns are irrelevant here and would confuse) and explain the view.
            if (_filterCallsign.Length > 0)
                Title = "Logs for " + _filterCallsign;
            else if (_contestOnly)
                Title = "Your contest logs";

            LoadLogs();   // sets the hint line, which depends on whether the callsign has any log
        }

        // selectId: the log to leave selected (a log just created). 0 = the active log, as before.
        private void LoadLogs(long selectId = 0)
        {
            var allLogs = _dal.GetLogs();
            // Target-name lookup uses ALL logs so a filtered view still resolves a copy-target that
            // happens to belong to a different callsign.
            var nameById = allLogs.ToDictionary(l => l.Id, l => l.Name);
            var logs = allLogs.AsEnumerable();

            // Which callsigns each log holds QSOs under - read once here, and used both to filter the
            // list and to show each log's callsigns in the Callsigns column.
            Dictionary<long, HashSet<string>> callsignsByLog;
            try { callsignsByLog = _dal.GetCallsignsByLog(); }
            catch (Exception swallowed) { Log.Swallow(swallowed); callsignsByLog = new Dictionary<long, HashSet<string>>(); }

            // Filtered to one callsign: a log qualifies if that is its current callsign OR it already
            // holds QSOs under it. A log whose callsign changed over the years must still be findable
            // under the older call - that is where those QSOs live.
            //
            // AND IF NOTHING MATCHES, THE FILTER IS DROPPED. This window is opened by the callsign guard
            // for a callsign that usually has NO log yet - that is why the guard opened it - so filtering
            // left an empty table and a man looking at a blank window, unable to see the logs he does
            // have. Every log is shown instead, and the line at the bottom says the callsign has none.
            // A log is a contest log when it names an event; everything else is a general log.
            if (_contestOnly)
                logs = logs.Where(l => !string.IsNullOrEmpty(l.EventType));

            bool filterFoundNothing = false;
            if (_filterCallsign.Length > 0)
            {
                string filterBase = CallsignIdentity.Base(_filterCallsign);
                var matching = logs.Where(l => CallsignIdentity.Same(l.Callsign, _filterCallsign) ||
                                               (callsignsByLog.TryGetValue(l.Id, out var held) && held.Contains(filterBase)))
                                   .ToList();
                if (matching.Count > 0) logs = matching;
                else filterFoundNothing = true;
            }
            UpdateFilterHint(filterFoundNothing);
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
                    Identity = BuildCallsigns(li.Callsign,
                                              callsignsByLog.TryGetValue(li.Id, out var logCalls) ? logCalls : null),
                    IdentityTip = BuildCallsignsTooltip(li.Callsign, logCalls),
                    Callsigns = CallsignList(li.Callsign, logCalls),
                    HasManyCallsigns = CallsignList(li.Callsign, logCalls).Count > 1,
                    CopiesTo = copiesTo,
                    CopyTargetLogId = li.CopyTargetLogId,
                    Callsign = li.Callsign,
                    Operator = li.Operator,
                });
            }
            LogsGrid.ItemsSource = rows;

            // Pre-select the active log so Export ADIF/Cabrillo, Rename and Delete default to it
            // (and a single log always appears selected). SelectionChanged then updates Open Log.
            // A log just created is selected instead, so it can be activated with one more press.
            var preselect = (selectId > 0 ? rows.FirstOrDefault(r => r.Id == selectId) : null)
                            ?? rows.FirstOrDefault(r => r.Id == _dal.ActiveLogId) ?? rows.FirstOrDefault();
            if (preselect != null)
            {
                LogsGrid.SelectedItem = preselect;
                LogsGrid.ScrollIntoView(preselect);
            }
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
        // The line along the bottom. Three situations, and each one says what to do next from here:
        // no filter at all, a callsign that has logs, and a callsign that has none yet.
        private void UpdateFilterHint(bool filterFoundNothing)
        {
            if (Hint == null) return;

            if (_filterCallsign.Length == 0)
            {
                Hint.Text = _contestOnly
                    ? "Only your contest logs are shown. Select one and Activate & Open it."
                    : "Select a log, then Open / Rename / Delete / Export.  Double-click to Activate and Open it.";
                return;
            }

            Hint.Text = filterFoundNothing
                ? "No log holds " + _filterCallsign + " yet, so all your logs are shown. Create a new log — it "
                  + "takes " + _filterCallsign + " as its callsign — or open one of these and use Callsigns to add "
                  + _filterCallsign + " to it."
                : "Showing only logs for station callsign " + _filterCallsign + ". Select one and Activate & Open "
                  + "it, or create a new log — it takes " + _filterCallsign + " as its callsign automatically.";
        }

        // What the Station Callsigns column shows: the log's current callsign first, then the older ones
        // its QSOs were made under. A log for one call reads as that one call, exactly as before.
        //
        // A CELL IS ONE LINE WIDE. A club station, or a log carried through three callsign changes, has
        // more names than fit, and a column that grows until the window hits the edge of the screen
        // pushes every other column out of sight. So a log with more than one callsign gets a DROP-DOWN
        // in its cell (see the column's template): closed it shows the current callsign, opened it lists
        // them all. This text is what a log with a single callsign shows, and what the column sorts on.
        private static List<string> CallsignList(string current, HashSet<string> held)
        {
            current = CallsignIdentity.Base((current ?? string.Empty).Trim());
            var all = new List<string>();
            if (current.Length > 0) all.Add(current);
            all.AddRange((held ?? new HashSet<string>())
                .Where(c => !string.Equals(c, current, StringComparison.OrdinalIgnoreCase))
                .OrderBy(c => c, StringComparer.OrdinalIgnoreCase));
            return all;
        }

        private static string BuildCallsigns(string current, HashSet<string> held)
        {
            var all = CallsignList(current, held);
            return all.Count == 0 ? "—" : all[0];
        }

        // The whole list, one per line, for the cell's tooltip - nothing is hidden, it is only folded.
        private static string BuildCallsignsTooltip(string current, HashSet<string> held)
        {
            var all = CallsignList(current, held);
            if (all.Count == 0) return "This log has no station callsign yet.";
            if (all.Count == 1) return "Station callsign: " + all[0];
            return "Station callsigns in this log (current first):" + Environment.NewLine
                   + string.Join(Environment.NewLine, all);
        }

        private Row Selected => LogsGrid.SelectedItem as Row;

        // ONE CLICK, NOT TWO, AND IT SELECTS NOTHING. Inside a DataGrid the first click on a control in
        // a cell is swallowed giving the cell focus, so the list only opened on the second press. It is
        // opened here on the first press instead - and the click is stopped from reaching the grid, so
        // looking at which callsigns a log holds does not make it the selected log. Reading is not
        // choosing: the row stays exactly as it was, and Rename / Delete / Open still point where they
        // pointed before.
        private void CallsignCombo_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var combo = sender as ComboBox;
            if (combo == null || combo.IsDropDownOpen) return;

            combo.IsDropDownOpen = true;
            e.Handled = true;
        }

        // The callsigns this log holds: which one it is for now, and the ones its QSOs were made under.
        // This is where a log gets a second callsign (an operator whose call changed keeps one log), and
        // where a log that never had one gets its first.
        private void Btn_Callsigns_Click(object sender, RoutedEventArgs e)
        {
            if (!RequireSelection()) return;
            long id = Selected.Id;
            var dlg = new LogCallsignsWindow(_dal, id, _dal.GetLogName(id) ?? Selected.Name,
                                             _main.CurrentStationCallsign) { Owner = this };
            dlg.ShowDialog();
            if (!dlg.Changed) return;

            // The active log's current callsign is what the main window shows, so put it back in step.
            if (id == _dal.ActiveLogId) _main.SyncStationCallsignFromActiveLog();
            _main.RefreshCopyIndicator();
            LoadLogs(id);
        }

        // Opens the per-log Copy settings dialog: set/change/stop the copy-target.
        private void Btn_CopySettings_Click(object sender, RoutedEventArgs e)
        {
            if (!RequireSelection()) return;
            var dlg = new CopySettingsWindow(_dal, Selected.Id, Selected.Callsign,
                                             Selected.CopyTargetLogId) { Owner = this };
            if (dlg.ShowDialog() != true) return;
            // The callsign stays as it is; only the target changes here. The target needs nothing of its
            // own - any log can receive copies - so there is nothing left to warn about.
            _dal.SetCopyTarget(Selected.Id, dlg.CopyTargetLogId);
            _main.RefreshCopyIndicator();   // the active log's copy state may have changed
            LoadLogs();
        }

        // "Open Log" is only meaningful for a log that is NOT already the active one.
        private void LogsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            Btn_Open.IsEnabled = Selected != null && Selected.Id != _dal.ActiveLogId;
            // Callsigns applies to any selected log: one that has none yet gets its first here, and one
            // that has one can be given another.
            Btn_Callsigns.IsEnabled = Selected != null;
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
                    HolyMessageBox.ShowWarning($"\"{name}\" has no QSOs to check.", "Log Fixer", this);
                    return;
                }

                var verifier = new LogVerifierWindow(qsos, name) { Owner = this };
                verifier.ShowDialog();
                LoadLogs();   // a corrected callsign or band can change what this list shows
            }
            catch (Exception ex)
            {
                HolyMessageBox.ShowError(
                    "Could not check this log.\n\n" + ex.Message + "\n\n"
                    + HolyMessageBox.WhatToDo(ex.Message, "press Log Fixer"),
                    "Log Fixer", this);
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
                HolyMessageBox.ShowError(
                    "Could not open the search for this log.\n\n" + ex.Message + "\n\n"
                    + HolyMessageBox.WhatToDo(ex.Message, "press Search In Log"),
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

            // Opening a log puts ITS callsign into the main window, so a log for another callsign than
            // the one typed there is a real change of station - asked about, never done silently. It is
            // not refused: opening a log is exactly how you move from one callsign to another, and
            // refusing would leave him unable to open his own logs until he cleared the box first.
            string typed = _main.CurrentStationCallsign;
            string logCall = (Selected.Callsign ?? string.Empty).Trim();
            if (typed.Length > 0 && logCall.Length > 0 && !_dal.LogAcceptsCallsign(Selected.Id, typed))
            {
                if (!HolyMessageBox.ShowConfirm(
                        "\"" + Selected.Name + "\" is a log for " + logCall + ", and you have " + typed +
                        " in the main window.\n\nOpening it changes your station callsign to " + logCall +
                        ". New QSOs will be logged as " + logCall + ".",
                        "Open a log for " + logCall + "?", HolyMsgType.Warning, this,
                        yesText: "Open and use " + logCall, noText: "Cancel"))
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

        // The new log is created but NOT activated, so this window stays open on the list it was
        // showing, with the new log selected and ready to be activated (or left alone).
        private void Btn_NewRegularLog_Click(object sender, RoutedEventArgs e)
        {
            long id = _main.CreateNewRegularLog(this);
            if (id > 0) LoadLogs(id);
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

            // The open log CAN be deleted, and so can the last one. Neither is a hazard once the program
            // is allowed to hold nothing open: what used to be dangerous was leaving ActiveLogId naming
            // a row that no longer exists, so new QSOs went to a log nothing could read. Deleting the
            // open log now closes it - the id becomes 0 and the window says so.
            bool deletingOpenLog = id == _dal.ActiveLogId;

            // Warn if OTHER logs copy their new QSOs INTO this one — deleting it turns their copying off.
            var sources = (LogsGrid.ItemsSource as System.Collections.Generic.IEnumerable<Row>)
                          ?.Where(r => r.CopyTargetLogId == id).Select(r => r.Name).ToList()
                          ?? new System.Collections.Generic.List<string>();
            string copyNote = sources.Count > 0
                ? "\n\nNote: " + string.Join(", ", sources) + " " + (sources.Count == 1 ? "copies" : "copy") +
                  " new QSOs into this log; that copying will be turned off. QSOs already copied elsewhere are not affected."
                : string.Empty;

            // Deleting the log that is OPEN closes it, and the operator is told so before they agree:
            // the log table empties, and nothing can be logged until a log is opened or created.
            string openNote = deletingOpenLog
                ? "\n\nThis log is the one currently open. Deleting it will CLOSE it — no log will be open, " +
                  "and you will not be able to log a QSO until you open or create one."
                : string.Empty;

            if (!HolyMessageBox.ShowConfirm(
                    "Delete the log \"" + _dal.GetLogName(id) + "\" and ALL " + Selected.QsoCount.ToString("N0") +
                    " QSO(s) in it?\n\nThis permanently removes those QSOs from the database and cannot be undone."
                    + openNote + copyNote,
                    "Delete Log", HolyMsgType.Warning, this))
                return;

            // Close it BEFORE the row goes, so ActiveLogId never names a log that is not there - that
            // gap is what would have let a QSO be written to a log nothing could read.
            if (deletingOpenLog) _main.CloseActiveLog();

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

    // ── The callsigns of one log ────────────────────────────────────────────────────────────────────
    // A log is for ONE station callsign at a time - its current one - but it keeps every callsign its
    // QSOs were made under. An operator whose call changes therefore keeps one log for his whole life
    // instead of splitting it: the old QSOs stay where they are, and typing the old call is not an
    // error, because the log genuinely holds contacts made with it.
    //
    // This window is the ONE place a callsign is deliberately added to a log, and the one place the
    // current callsign is changed. Adding only: a callsign with QSOs behind it cannot be removed
    // without the list lying about what is in the log.
    //
    // Built in code rather than XAML so no new file has to be added to the project while Visual Studio
    // has it open (CwKeyboardWindow.cs does the same).
    public class LogCallsignsWindow : Window
    {
        private readonly DataAccess _dal;
        private readonly long _logId;
        private readonly ListBox _list = new ListBox { FontSize = 16, Height = 190, Margin = new Thickness(0, 8, 0, 0) };
        private readonly TextBox _add = new TextBox
        {
            FontSize = 16,
            Width = 160,
            Padding = new Thickness(4, 3, 4, 3),
            CharacterCasing = CharacterCasing.Upper,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        private readonly Button _btnCurrent = new Button { Content = "Make current", FontSize = 16, Height = 32, Padding = new Thickness(9, 0, 9, 0), IsEnabled = false };

        // True when the log's current callsign was changed, so the caller reloads its list.
        public bool Changed { get; private set; }

        public LogCallsignsWindow(DataAccess dal, long logId, string logName, string mainWindowCallsign)
        {
            _dal = dal;
            _logId = logId;

            Title = "Callsigns — " + logName;
            Width = 520;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            SetResourceReference(BackgroundProperty, "WindowBg");

            var root = new StackPanel { Margin = new Thickness(22, 18, 22, 18) };

            root.Children.Add(new TextBlock
            {
                Text = "The station callsigns this log holds QSOs under. The current one is the log's "
                     + "callsign now: opening the log puts it into the main window's Station callsign.",
                FontSize = 16,
                TextWrapping = TextWrapping.Wrap
            });
            root.Children.Add(_list);
            _list.SelectionChanged += (s, e) =>
            {
                var sel = _list.SelectedItem as LogCallsignUse;
                _btnCurrent.IsEnabled = sel != null && !sel.IsCurrent;
            };

            var currentRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            _btnCurrent.Click += MakeCurrent_Click;
            currentRow.Children.Add(_btnCurrent);
            root.Children.Add(currentRow);

            root.Children.Add(new Border
            {
                BorderBrush = System.Windows.Media.Brushes.LightGray,
                BorderThickness = new Thickness(0, 1, 0, 0),
                Margin = new Thickness(0, 14, 0, 12)
            });

            root.Children.Add(new TextBlock
            {
                Text = "Add a callsign to this log",
                FontSize = 16,
                FontWeight = FontWeights.Bold
            });
            root.Children.Add(new TextBlock
            {
                Text = "Use this when your station callsign changes and you want to keep logging into "
                     + "this same log. The callsign you add becomes the log's current one; the earlier "
                     + "ones stay, because their QSOs are still here.",
                FontSize = 16,
                TextWrapping = TextWrapping.Wrap,
                Foreground = System.Windows.Media.Brushes.Gray,
                Margin = new Thickness(0, 4, 0, 0)
            });

            var addRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            addRow.Children.Add(_add);
            var btnAdd = new Button { Content = "Add", FontSize = 16, Height = 32, Width = 90, Margin = new Thickness(8, 0, 0, 0) };
            btnAdd.Click += Add_Click;
            addRow.Children.Add(btnAdd);
            root.Children.Add(addRow);

            // Pre-filled with what is in the main window, which is where the operator has just typed the
            // callsign he wants to use - but only when this log does not have it already.
            string typed = (mainWindowCallsign ?? string.Empty).Trim();
            if (typed.Length > 0 && !_dal.LogAcceptsCallsign(_logId, typed)) _add.Text = typed;

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 18, 0, 0)
            };
            var btnClose = new Button { Content = "Close", FontSize = 16, Height = 32, Width = 100, IsCancel = true };
            btnClose.Click += (s, e) => Close();
            buttons.Children.Add(btnClose);
            root.Children.Add(buttons);

            Content = root;
            LoadCallsigns();
        }

        private void LoadCallsigns()
        {
            try
            {
                var uses = _dal.GetCallsignsInLog(_logId);
                _list.DisplayMemberPath = "Display";
                _list.ItemsSource = uses;
                _list.SelectedItem = uses.FirstOrDefault(u => u.IsCurrent);
                _btnCurrent.IsEnabled = false;   // the current one is selected, so there is nothing to change
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        private void MakeCurrent_Click(object sender, RoutedEventArgs e)
        {
            var sel = _list.SelectedItem as LogCallsignUse;
            if (sel == null || sel.IsCurrent) return;

            if (!HolyMessageBox.ShowConfirm(
                    "This log becomes a log for \"" + sel.Callsign + "\".\n\n" +
                    "Opening it will put " + sel.Callsign + " into the main window's Station callsign, and new " +
                    "QSOs made with it belong here. Nothing already in the log changes.\n\nMake " +
                    sel.Callsign + " the current callsign?",
                    "Callsigns", HolyMsgType.Info, this))
                return;

            _dal.SetCurrentLogCallsign(_logId, sel.Callsign);
            Changed = true;
            LoadCallsigns();
        }

        private void Add_Click(object sender, RoutedEventArgs e)
        {
            string call = CallsignIdentity.Base((_add.Text ?? string.Empty).Trim());
            if (call.Length == 0)
            {
                HolyMessageBox.ShowWarning("Type the callsign to add to this log.", "Callsigns", this);
                _add.Focus();
                return;
            }

            // SHAPED LIKE A CALLSIGN? The same test the entry form and the Alerts list use, and like
            // them it only WARNS: a real callsign this program has never met is still his to type, and
            // the one thing that must not happen is the log refusing a call that is genuinely his.
            if (!CallsignIdentity.LooksLikeCallsign(call)
                && !HolyMessageBox.ShowConfirm(
                        "\"" + call + "\" is not shaped like a callsign.\n\nUse it anyway?",
                        "Callsigns", HolyMsgType.Warning, this))
            {
                _add.Focus();
                return;
            }

            // Already here: nothing to add. Make it the current one instead - that is the only thing
            // pressing Add could have meant.
            if (_dal.LogAcceptsCallsign(_logId, call))
            {
                var uses = _dal.GetCallsignsInLog(_logId);
                if (uses.Any(u => CallsignIdentity.Same(u.Callsign, call) && u.IsCurrent))
                {
                    HolyMessageBox.Show("\"" + call + "\" is already this log's current callsign.", "Callsigns", HolyMsgType.Info, this);
                    return;
                }
                if (!HolyMessageBox.ShowConfirm(
                        "This log already holds QSOs made with \"" + call + "\".\n\n" +
                        "Make it the log's current callsign?",
                        "Callsigns", HolyMsgType.Info, this))
                    return;
            }
            else if (!HolyMessageBox.ShowConfirm(
                    "From now on this log is for \"" + call + "\".\n\n" +
                    "New QSOs made with " + call + " belong here, and opening the log will put " + call +
                    " into the main window's Station callsign. The callsigns already in the log stay as " +
                    "they are — their QSOs are still here.\n\nAdd " + call + " to this log?",
                    "Callsigns", HolyMsgType.Warning, this))
                return;

            _dal.SetCurrentLogCallsign(_logId, call);
            Changed = true;
            _add.Text = string.Empty;
            LoadCallsigns();
        }
    }
}
