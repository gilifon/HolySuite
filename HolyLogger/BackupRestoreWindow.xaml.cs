using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;

namespace HolyLogger
{
    // Shows the daily-backup restore instructions in-app (the same text written to
    // HOW TO RESTORE.txt), a one-click in-app Restore (pick a backup, see what's in it, confirm, and the
    // app swaps it in and restarts itself), one-click access to the Backups folder, and an optional
    // "extra backup copy folder" that is remembered in Settings.
    public partial class BackupRestoreWindow : Window
    {
        private const string NoneText = "(none — daily backups are saved only in the default Backups folder)";
        private readonly string _backupsFolder;

        // One row in the backup picker.
        private class BackupItem : System.ComponentModel.INotifyPropertyChanged
        {
            // The second line of each entry: what this backup holds, filled in by the background read
            // that runs when the window opens. "reading…" until then, so a slow disk looks like work in
            // progress rather than like an empty list.
            private string contents = "reading…";
            public string Contents
            {
                get { return contents; }
                set
                {
                    contents = value;
                    if (PropertyChanged != null)
                        PropertyChanged(this, new System.ComponentModel.PropertyChangedEventArgs("Contents"));
                }
            }
            public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;

            public string Path;
            public string FileName;
            public DateTime? Date;
            public long SizeBytes;

            // What kind of copy this is. Empty for the ordinary daily backup; the safety copies taken
            // before something rewrites the database say so, and carry a time as well as a date -
            // several can be made in one afternoon and a date alone would not tell them apart.
            public string Kind;

            public string DisplayLabel
            {
                get
                {
                    string when = Date.HasValue
                        ? Date.Value.ToString(string.IsNullOrEmpty(Kind) ? "dd-MM-yyyy" : "dd-MM-yyyy HH:mm")
                        : FileName;
                    return when
                         + (string.IsNullOrEmpty(Kind) ? "" : "   " + Kind)
                         + "   (" + FormatSize(SizeBytes) + ")";
                }
            }
        }

        // Set by the preview query after a successful read, so Restore uses the exact same numbers the
        // operator was shown rather than re-querying (and possibly getting a different answer) later.
        private BackupItem _selectedBackup;
        private bool _selectedBackupReadable;
        private int _selectedBackupQsoCount;
        private string _selectedBackupDateRange;

        public BackupRestoreWindow(string backupsFolder)
        {
            InitializeComponent();
            WindowBounds.Attach(this, "BackupRestore");   // remember position + size
            _backupsFolder = backupsFolder;
            RefreshExtraFolder();
            LoadBackupList();
        }

        private static string FormatSize(long bytes)
        {
            double mb = bytes / 1024.0 / 1024.0;
            return mb >= 1 ? mb.ToString("0.0") + " MB" : Math.Ceiling(bytes / 1024.0) + " KB";
        }

        // Reads the backup files straight from the Backups folder - the same "logDB-yyyy-MM-dd.db" naming
        // DataAccess creates and prunes them with - newest first, so the operator never has to go looking
        // in Explorer or type a filename.
        private void LoadBackupList()
        {
            var items = new List<BackupItem>();
            try
            {
                if (Directory.Exists(_backupsFolder))
                {
                    foreach (string path in Directory.GetFiles(_backupsFolder, "logDB-????-??-??.db"))
                    {
                        string name = Path.GetFileName(path);
                        DateTime? date = null;
                        string datePart = name.Length >= 18 ? name.Substring(6, 10) : null;   // logDB-yyyy-MM-dd.db
                        if (datePart != null && DateTime.TryParseExact(datePart, "yyyy-MM-dd",
                                System.Globalization.CultureInfo.InvariantCulture,
                                System.Globalization.DateTimeStyles.None, out DateTime d))
                            date = d;

                        long size = 0;
                        try { size = new FileInfo(path).Length; } catch (Exception ex) { Log.Swallow(ex); }

                        items.Add(new BackupItem { Path = path, FileName = name, Date = date, SizeBytes = size });
                    }
                }
            }
            catch (Exception ex) { Log.Swallow(ex); }

            // THE SAFETY COPIES TOO. The Log Fixer copies the whole database before it writes, and so
            // does a Restore - but those land BESIDE the database rather than in the Backups folder,
            // under a different name, so this window never showed them. An operator told "a copy was
            // saved first" came here to find it and found nothing, which made the promise look empty.
            // They are full databases like any other backup, so Restore handles them unchanged.
            try
            {
                // BOTH FOLDERS. New safety copies are written into the Backups folder with the daily
                // ones; the ones taken before that change are still sitting beside the database, and
                // an operator's old copies must not vanish from this list because the program moved
                // where it puts new ones.
                var dal = DataAccess.GetInstance();
                string dbDir = dal == null || string.IsNullOrEmpty(dal.DbPath)
                    ? null : Path.GetDirectoryName(dal.DbPath);

                var folders = new List<string> { _backupsFolder };
                if (!string.IsNullOrEmpty(dbDir) &&
                    !string.Equals(dbDir, _backupsFolder, StringComparison.OrdinalIgnoreCase))
                    folders.Add(dbDir);

                foreach (string folder in folders)
                if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
                    foreach (string path in Directory.GetFiles(folder, "logDB.db.*.bak"))
                    {
                        string name = Path.GetFileName(path);

                        // logDB.db.pre-fix-yyyyMMdd-HHmmss.bak - the kind is whatever sits between the
                        // "pre-" and the timestamp, so a new kind of safety copy needs nothing here.
                        string kind = "safety copy";
                        var m = System.Text.RegularExpressions.Regex.Match(
                            name, @"\.pre-([a-z]+)-(\d{8})-(\d{6})\.bak$",
                            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                        DateTime? when = null;
                        if (m.Success)
                        {
                            kind = "before a " + m.Groups[1].Value.ToLowerInvariant();
                            DateTime d2;
                            if (DateTime.TryParseExact(m.Groups[2].Value + m.Groups[3].Value, "yyyyMMddHHmmss",
                                    System.Globalization.CultureInfo.InvariantCulture,
                                    System.Globalization.DateTimeStyles.None, out d2))
                                when = d2;
                        }
                        if (when == null)
                            try { when = new FileInfo(path).LastWriteTime; } catch (Exception ex) { Log.Swallow(ex); }

                        long size = 0;
                        try { size = new FileInfo(path).Length; } catch (Exception ex) { Log.Swallow(ex); }

                        items.Add(new BackupItem
                        {
                            Path = path, FileName = name, Date = when, SizeBytes = size, Kind = kind
                        });
                    }
            }
            catch (Exception ex) { Log.Swallow(ex); }

            // Newest first by the moment the copy was taken, so a safety copy made this afternoon sits
            // above this morning's daily one rather than being sorted by a filename it does not share.
            var ordered = items
                .OrderByDescending(b => b.Date ?? DateTime.MinValue)
                .ThenByDescending(b => b.FileName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            LB_Backups.ItemsSource = ordered;

            // The newest backup chosen for you. It is the one an operator wants nine times in ten, and
            // an empty right-hand panel saying "pick a backup" is a step that need not exist. Selecting
            // it also fires the read that fills that panel in.
            if (ordered.Count > 0) LB_Backups.SelectedIndex = 0;

            ReadAllContents(ordered);
            ShowFolders();
        }

        // The two places backups live, named and clickable. They are two because they are made by two
        // different things: the daily backup is written into the Backups folder, while a safety copy is
        // taken beside the database itself at the moment something is about to rewrite it.
        private void ShowFolders()
        {
            if (TB_Folders == null) return;
            TB_Folders.Inlines.Clear();

            string dbDir = null;
            try
            {
                var dal = DataAccess.GetInstance();
                if (dal != null && !string.IsNullOrEmpty(dal.DbPath)) dbDir = Path.GetDirectoryName(dal.DbPath);
            }
            catch (Exception ex) { Log.Swallow(ex); }

            // ONE LINE, because there is one folder. Copies made by older versions beside the database
            // are moved in here on startup (see BackupDatabaseDaily), so there is never a second place
            // to mention - and no note about where the program used to put things.
            AddFolderLine("All of these files are kept in this folder:  ", _backupsFolder);
        }

        private void AddFolderLine(string caption, string folder)
        {
            TB_Folders.Inlines.Add(new System.Windows.Documents.Run(caption));
            if (string.IsNullOrEmpty(folder))
            {
                TB_Folders.Inlines.Add(new System.Windows.Documents.Run("(not known)"));
                return;
            }

            var link = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run(folder))
            {
                ToolTip = "Click to open this folder"
            };
            string target = folder;
            link.Click += (s, e) =>
            {
                try
                {
                    if (Directory.Exists(target)) System.Diagnostics.Process.Start("explorer.exe", "\"" + target + "\"");
                }
                catch (Exception ex) { Log.Swallow(ex); }
            };
            TB_Folders.Inlines.Add(link);
        }

        // WHAT EACH BACKUP HOLDS, WITHOUT BEING ASKED. Reading a 131 MB database is not instant and
        // there may be a dozen of them, so they are read one after another off the UI thread and each
        // line updates itself the moment its own answer arrives. The window is usable throughout; the
        // second lines simply fill in from the top down.
        //
        // One at a time rather than all at once on purpose: these are large files on the same disk, and
        // a dozen parallel reads would finish no sooner and would make the machine crawl meanwhile.
        private async void ReadAllContents(List<BackupItem> items)
        {
            if (items == null) return;
            foreach (BackupItem item in items)
            {
                string path = item.Path;
                BackupSummary summary;
                try { summary = await Task.Run(() => ReadBackupSummary(path)); }
                catch (Exception ex) { Log.Swallow(ex); item.Contents = "could not be read"; continue; }

                if (!summary.Ok) { item.Contents = "damaged — cannot be read"; continue; }

                string logs = summary.Logs == null || summary.Logs.Count == 0
                    ? ""
                    : "  ·  " + summary.Logs.Count.ToString("N0")
                      + (summary.Logs.Count == 1 ? " log" : " logs");

                item.Contents = summary.TotalCount.ToString("N0") + " QSOs" + logs;
            }
        }

        // THE WHEEL MOVES THE SELECTION, not just the view. Rolling the wheel used to slide the list
        // under a selection that stayed where it was, so the panel on the right went on describing a
        // backup that was no longer even visible - and the only way to see another one was to click it.
        //
        // Moving the selection instead means the description always belongs to the highlighted entry,
        // and - just as important - to the entry Restore would act on. Following the mouse on HOVER
        // would have done the same for the eye while leaving Restore pointed somewhere else, which is
        // not a thing to risk on a button that overwrites the database.
        private void LB_Backups_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            int count = LB_Backups.Items.Count;
            if (count == 0) return;

            int index = LB_Backups.SelectedIndex;
            if (index < 0) index = e.Delta < 0 ? -1 : count;      // nothing chosen yet: start at the end we came from

            index += e.Delta > 0 ? -1 : 1;
            if (index < 0) index = 0;
            if (index > count - 1) index = count - 1;

            if (index != LB_Backups.SelectedIndex)
            {
                LB_Backups.SelectedIndex = index;
                LB_Backups.ScrollIntoView(LB_Backups.SelectedItem);
            }
            e.Handled = true;      // the list must not also scroll away from what is now selected
        }

        // Opens the selected backup READ-ONLY (never the live connection, never a write) and reads its QSO
        // count and date range, so the operator sees exactly what they are about to bring back BEFORE
        // committing to anything. This doubles as the validity check: a file that will not even open, or
        // has no qso table, is a damaged/incomplete backup - Restore is disabled for it rather than letting
        // the operator overwrite a working log with something worse.
        private async void LB_Backups_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            _selectedBackup = LB_Backups.SelectedItem as BackupItem;
            _selectedBackupReadable = false;
            Btn_Restore.IsEnabled = false;
            TB_BackupWarning.Visibility = Visibility.Collapsed;
            TB_BackupDetails.Visibility = Visibility.Collapsed;
            TB_BackupDetailsHint.Visibility = Visibility.Visible;

            if (_selectedBackup == null) return;

            TB_BackupDetailsHint.Text = "Reading " + _selectedBackup.FileName + " …";
            string path = _selectedBackup.Path;

            var summary = await Task.Run(() => ReadBackupSummary(path));

            // The operator may have picked a different row while this was running.
            if (_selectedBackup == null || _selectedBackup.Path != path) return;

            TB_BackupDetailsHint.Visibility = Visibility.Collapsed;
            if (summary.Ok)
            {
                _selectedBackupReadable = true;
                _selectedBackupQsoCount = summary.TotalCount;
                _selectedBackupDateRange = summary.DateRange;

                var text = new System.Text.StringBuilder();
                text.Append($"{summary.TotalCount:N0} QSO(s), logged {summary.DateRange}.");
                // Per-log breakdown, so the operator sees which named logs this backup actually holds -
                // not just a flat total across all of them. Absent for a backup old enough to predate the
                // Log Manager's "logs" table; the flat total above still covers that case.
                if (summary.Logs != null && summary.Logs.Count > 0)
                {
                    text.Append(Environment.NewLine).Append("Logs in this backup:");
                    foreach (var (name, n) in summary.Logs)
                        text.Append(Environment.NewLine).Append("   • ").Append(name).Append(" — ").Append(n.ToString("N0"));
                }
                TB_BackupDetails.Text = text.ToString();
                TB_BackupDetails.Visibility = Visibility.Visible;
                Btn_Restore.IsEnabled = true;
            }
            else
            {
                TB_BackupWarning.Text = "This backup could not be read — it may be damaged.\n(" + summary.Error + ")";
                TB_BackupWarning.Visibility = Visibility.Visible;
            }
        }

        private class BackupSummary
        {
            public bool Ok;
            public int TotalCount;
            public string DateRange;
            public List<(string Name, int Count)> Logs;
            public string Error;
        }

        private static BackupSummary ReadBackupSummary(string path)
        {
            try
            {
                using (var cn = new SQLiteConnection("Data Source=" + path + ";Version=3;Read Only=True;"))
                {
                    cn.Open();

                    var result = new BackupSummary();
                    using (var cmd = new SQLiteCommand("SELECT COUNT(*), MIN(date), MAX(date) FROM qso", cn))
                    using (var rdr = cmd.ExecuteReader())
                    {
                        if (!rdr.Read()) return new BackupSummary { Ok = false, Error = "no data" };
                        result.TotalCount = rdr.IsDBNull(0) ? 0 : Convert.ToInt32(rdr.GetValue(0));
                        string min = rdr.IsDBNull(1) ? null : rdr.GetString(1);
                        string max = rdr.IsDBNull(2) ? null : rdr.GetString(2);
                        result.DateRange = (min != null && max != null) ? $"between {FormatDate(min)} and {FormatDate(max)}" : "on unknown dates";
                    }

                    // Per-log breakdown - only if this backup is new enough to have the "logs" table.
                    bool hasLogsTable;
                    using (var chk = new SQLiteCommand(
                        "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='logs'", cn))
                        hasLogsTable = Convert.ToInt32(chk.ExecuteScalar()) > 0;

                    if (hasLogsTable)
                    {
                        result.Logs = new List<(string, int)>();
                        using (var cmd = new SQLiteCommand(
                            "SELECT COALESCE(l.name, '(no log)') AS log_name, COUNT(*) AS n " +
                            "FROM qso q LEFT JOIN logs l ON l.Id = q.log_id " +
                            "GROUP BY log_name ORDER BY n DESC", cn))
                        using (var rdr = cmd.ExecuteReader())
                            while (rdr.Read())
                                result.Logs.Add((rdr.GetString(0), Convert.ToInt32(rdr.GetValue(1))));
                    }

                    result.Ok = true;
                    return result;
                }
            }
            catch (Exception ex)
            {
                return new BackupSummary { Ok = false, Error = ex.Message };
            }
        }

        private static string FormatDate(string yyyymmdd)
        {
            if (DateTime.TryParseExact(yyyymmdd, "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out DateTime d))
                return d.ToString("dd-MM-yyyy");
            return yyyymmdd;
        }

        // Restore: confirm (naming the exact safety-copy filename BEFORE anything happens), swap the
        // database in via DataAccess.RestoreFromBackup (which renames the current DB aside rather than
        // deleting it, and rolls back automatically if the copy fails), then restart the app so every
        // window/cache picks up the restored data instead of the stale one already in memory.
        private void Btn_Restore_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedBackup == null || !_selectedBackupReadable) return;

            // Into the Backups folder with everything else, so there is one place to look. Falls back
            // to a bare name beside the database only if there is no database to ask about.
            // THE NAME ONLY, so far. Nothing is deleted and nothing is written until the question
            // below has been answered YES - this used to make room for the new copy right here, which
            // meant that saying No had already cost the operator one of the copies in the list.
            string safetyPath = DataAccess.GetInstance()?.SafetyCopyName("restore")
                ?? ("logDB.db.pre-restore-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".bak");
            string safetyName = safetyPath;

            bool confirmed = HolyMessageBox.ShowConfirm(
                $"Replace your current log with the backup from {(_selectedBackup.Date?.ToString("dd-MM-yyyy") ?? _selectedBackup.FileName)}?\n\n" +
                $"That backup has {_selectedBackupQsoCount:N0} QSO(s), logged {_selectedBackupDateRange}.\n\n" +
                "Your CURRENT log will be kept, not deleted, saved as:\n" + safetyPath + "\n\n" +
                "HolyLogger will restart automatically once the restore is done.",
                "Restore from backup", HolyMsgType.Warning, this);
            if (!confirmed) return;

            var dal = DataAccess.GetInstance();

            // NOW room is made - and the copy being restored FROM is named, so that whatever else is
            // cleared away, it is not the one file this restore cannot do without. Choosing an older
            // safety copy used to delete it at this moment and then fail with "That backup file no
            // longer exists", which is what the operator saw on 2026-08-21.
            dal?.MakeRoomForSafetyCopy(_selectedBackup.Path);

            var result = dal?.RestoreFromBackup(_selectedBackup.Path, safetyName);

            if (result == null || !result.Ok)
            {
                HolyMessageBox.ShowError(
                    "The restore could not be completed, and your log was not changed:\n"
                    + (result?.Error ?? "unknown error") + "\n\n"
                    + "Close every other HolyLogger window and try the restore again.\n"
                    + "If the backup itself is at fault, pick an earlier one from the list.",
                    "Restore from backup", this);
                return;
            }

            HolyMessageBox.Show(
                "Restored. Your previous log was kept as:\n" + result.SafetyCopyPath +
                "\n\nHolyLogger will now restart.",
                "Restore from backup", HolyMsgType.Success, this);

            RestartApplication();
        }

        // Relaunches the exe and exits THIS process immediately (not the normal WPF shutdown sequence,
        // which would run every window's Closing handler - e.g. the upload-on-exit prompts - against a
        // database that no longer exists in memory correctly, since it was just swapped out from under
        // this process). If the relaunch itself cannot start, the operator is told to start it themselves
        // rather than the app just silently vanishing.
        private void RestartApplication()
        {
            try
            {
                // MUST happen before Process.Start: the app holds a single-instance mutex
                // ("HolyLoggerApplication") for the whole time it runs, and the new process starting
                // while this one still holds it hits that guard and refuses to open, reporting
                // "Holyland logger is already open." (the exact bug this fixes). Same mechanism the
                // Profile Manager's own restart uses, for the same reason.
                App.ReleaseSingleInstanceMutex();

                string exePath = Assembly.GetExecutingAssembly().Location;
                System.Diagnostics.Process.Start(exePath);
            }
            catch (Exception ex)
            {
                Log.Swallow(ex);
                HolyMessageBox.Show(
                    "HolyLogger could not restart itself. Please start it again manually.",
                    "Restore from backup", HolyMsgType.Warning, this);
            }
            finally
            {
                Environment.Exit(0);
            }
        }

        private void RefreshExtraFolder()
        {
            string path = Properties.Settings.Default.ExtraBackupFolder;
            TB_ExtraFolder.Text = string.IsNullOrWhiteSpace(path) ? NoneText : path;
        }

        private void Btn_Browse_Click(object sender, RoutedEventArgs e)
        {
            if (TryPickWritableFolder(this, Properties.Settings.Default.ExtraBackupFolder, out string chosen))
            {
                Properties.Settings.Default.ExtraBackupFolder = chosen;
                Properties.Settings.Default.Save();
                RefreshExtraFolder();
            }
        }

        // Shows a folder picker and verifies the chosen folder is writable. Returns true + the path if
        // the user picked a usable folder; false if they cancelled or it wasn't writable (an error is
        // shown in that case). Shared by this window's Browse button and the first-run offer so the
        // pick-and-validate behaviour stays identical in both places.
        public static bool TryPickWritableFolder(Window owner, string initial, out string chosen)
        {
            chosen = null;
            using (var dlg = new System.Windows.Forms.FolderBrowserDialog())
            {
                dlg.Description = "Choose a folder for extra copies of your daily backups";
                dlg.ShowNewFolderButton = true;
                if (!string.IsNullOrWhiteSpace(initial) && System.IO.Directory.Exists(initial))
                    dlg.SelectedPath = initial;

                if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                    return false;

                string path = dlg.SelectedPath;

                // Verify we can actually write there before committing, so a bad choice (read-only
                // location, etc.) is caught now rather than silently failing at backup time.
                try
                {
                    System.IO.Directory.CreateDirectory(path);
                    string probe = System.IO.Path.Combine(path, ".holylogger_write_test.tmp");
                    System.IO.File.WriteAllText(probe, "ok");
                    System.IO.File.Delete(probe);
                }
                catch (Exception ex)
                {
                    Log.Swallow(ex);
                    HolyMessageBox.ShowError(
                        "That folder can't be written to, so it wasn't set:\n" + path + "\n\n"
                        + "Pick a folder on a drive that is always connected, and one you own — "
                        + "a folder inside Documents always works.",
                        "Extra backup folder", owner);
                    return false;
                }

                chosen = path;
                return true;
            }
        }

        private void Btn_Clear_Click(object sender, RoutedEventArgs e)
        {
            Properties.Settings.Default.ExtraBackupFolder = string.Empty;
            Properties.Settings.Default.Save();
            RefreshExtraFolder();
        }

    }
}
