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
        private class BackupItem
        {
            public string Path;
            public string FileName;
            public DateTime? Date;
            public long SizeBytes;
            public string DisplayLabel => (Date.HasValue ? Date.Value.ToString("dd-MM-yyyy") : FileName)
                                         + "   (" + FormatSize(SizeBytes) + ")";
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

            LB_Backups.ItemsSource = items.OrderByDescending(b => b.FileName, StringComparer.OrdinalIgnoreCase).ToList();
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

            string safetyName = "logDB.db.pre-restore-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".bak";
            string dbFolder = DataAccess.GetInstance()?.DataFolder;
            string safetyPath = string.IsNullOrEmpty(dbFolder) ? safetyName : Path.Combine(dbFolder, safetyName);

            bool confirmed = HolyMessageBox.ShowConfirm(
                $"Replace your current log with the backup from {(_selectedBackup.Date?.ToString("dd-MM-yyyy") ?? _selectedBackup.FileName)}?\n\n" +
                $"That backup has {_selectedBackupQsoCount:N0} QSO(s), logged {_selectedBackupDateRange}.\n\n" +
                "Your CURRENT log will be kept, not deleted, saved as:\n" + safetyPath + "\n\n" +
                "HolyLogger will restart automatically once the restore is done.",
                "Restore from backup", HolyMsgType.Warning, this);
            if (!confirmed) return;

            var dal = DataAccess.GetInstance();
            var result = dal?.RestoreFromBackup(_selectedBackup.Path, safetyName);

            if (result == null || !result.Ok)
            {
                HolyMessageBox.ShowError(
                    "The restore could not be completed, and your log was not changed:\n" + (result?.Error ?? "unknown error"),
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
                    HolyMessageBox.ShowError("That folder can't be written to, so it wasn't set:\n" + path, "Extra backup folder", owner);
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
