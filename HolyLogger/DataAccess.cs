using HolyParser;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data;
using System.Data.SQLite;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace HolyLogger
{
    public class DataAccess
    {
        // Private static instance variable to hold the single instance of the class.
        private static DataAccess instance;
        private SQLiteConnection con = null;
        string dbPath = "";

        // Serializes every access to the single SQLite connection. The connection is not safe for
        // concurrent use, and it is touched from the UI thread, the UDP logging threads and the ADIF
        // import worker. Every public method takes this lock; it is re-entrant, so a public method
        // that calls another (e.g. Insert -> GetTopQSOs) is fine. The lock is only ever taken inside
        // DataAccess, so it cannot deadlock against outer locks held by callers.
        private readonly object _dbLock = new object();
        private static readonly object _instanceLock = new object();

        public bool SchemaHasChanged { get; set; }

        // The log currently loaded in the log table. New QSOs are stored under this log.
        // NO LOG IS OPEN - a real state, not an error: the operator may delete or close every log they
        // have, and the program then holds nothing open rather than adopting a log they did not choose.
        //
        // It is -1 and not 0 because the two must not be confused at startup. 0 is what the saved
        // setting reads when it was NEVER set - an upgrade from a version before this - and that user
        // must go on getting their first log opened for them. -1 can only have been written by closing
        // a log deliberately, and that choice is kept across a restart.
        public const long NoLogId = -1;

        public long ActiveLogId { get; set; }

        // Everything that needs a log asks this first. A query scoped to a log id that matches no row
        // returns nothing, which is the truthful answer to "what is in the log" when there is no log.
        public bool HasActiveLog => ActiveLogId > 0;

        // OPENING THE DATABASE IS ALLOWED TO FAIL FOR A MOMENT, AND ONLY FOR A MOMENT.
        //
        // Straight after an installation the very first launch met "attempt to write a readonly
        // database", HolyLogger showed it and SHUT ITSELF DOWN; starting it again worked. The
        // installer ships a logDB.db into the same folder the live database lives in, so for a second
        // or two after it finishes the file is still the installer's - being replaced, or held, or not
        // yet given the operator's own permissions. Trying once and giving up turned a two-second
        // condition into a program that appeared broken on the day it was installed.
        //
        // So: try, wait, try again - five times across about four seconds. A fault that is real (a
        // missing folder, a corrupt file, a genuinely read-only disk) still fails, only four seconds
        // later, which nobody minds. Each attempt is recorded, so a log from an operator this happens
        // to shows how long it took rather than nothing at all.
        //
        // The waits are deliberately not equal: the common case clears almost at once, and a first
        // wait of a quarter second keeps that case fast.
        private static readonly int[] OpenRetryWaitsMs = { 250, 500, 1000, 2000 };

        private void OpenWithRetry()
        {
            Exception last = null;

            for (int attempt = 0; attempt <= OpenRetryWaitsMs.Length; attempt++)
            {
                try
                {
                    // AND IF IT IS SIMPLY MARKED READ-ONLY, UNMARK IT. An installer can leave the
                    // attribute set on a file it laid down, and no amount of waiting clears that.
                    // HolyLogger's own log database is never meant to be read-only, so there is
                    // nothing to weigh up: it is put right and recorded.
                    try
                    {
                        if (File.Exists(dbPath))
                        {
                            var attrs = File.GetAttributes(dbPath);
                            if ((attrs & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                            {
                                File.SetAttributes(dbPath, attrs & ~FileAttributes.ReadOnly);
                                Log.Warn("The log database was marked read-only; the mark has been removed.");
                            }
                        }
                    }
                    catch (Exception swallowed) { Log.Swallow(swallowed); }

                    if (con != null)
                    {
                        try { con.Dispose(); } catch (Exception swallowed) { Log.Swallow(swallowed); }
                        con = null;
                    }

                    var opening = new SQLiteConnection(@"DataSource = " + dbPath + @";Version=3");
                    opening.Open();

                    // OPENING IS NOT THE TEST. SQLite opens a file it cannot write to perfectly
                    // happily and only complains at the first write - which would be some later
                    // innocent-looking operation, long past the point where retrying is possible.
                    // So the write is done here, deliberately, while there is still something to do
                    // about it: a transaction that is opened and immediately rolled back changes
                    // nothing and proves the file and its folder will take a write.
                    using (var probe = opening.BeginTransaction())
                    {
                        using (var cmd = new SQLiteCommand(
                            "CREATE TABLE IF NOT EXISTS holy_write_probe (x INTEGER)", opening, probe))
                            cmd.ExecuteNonQuery();
                        probe.Rollback();
                    }

                    con = opening;
                    if (attempt > 0)
                        Log.Warn("The log database opened on attempt " + (attempt + 1)
                                 + " - it was busy or read-only until then.");
                    return;
                }
                catch (Exception ex)
                {
                    last = ex;
                    if (attempt == OpenRetryWaitsMs.Length) break;

                    Log.Warn("The log database could not be opened (attempt " + (attempt + 1) + " of "
                             + (OpenRetryWaitsMs.Length + 1) + "): " + ex.Message + " - waiting "
                             + OpenRetryWaitsMs[attempt] + " ms.");
                    System.Threading.Thread.Sleep(OpenRetryWaitsMs[attempt]);
                }
            }

            throw last ?? new Exception("the database could not be opened");
        }

        private DataAccess()
        {
            try
            {

                //string executable = System.Reflection.Assembly.GetExecutingAssembly().Location;
                //string path = (System.IO.Path.GetDirectoryName(executable));
                //AppDomain.CurrentDomain.SetData("DataDirectory", path);

                System.Reflection.Assembly assembly = System.Reflection.Assembly.GetExecutingAssembly();
                System.Diagnostics.FileVersionInfo fvi = System.Diagnostics.FileVersionInfo.GetVersionInfo(assembly.Location);
                string company = fvi.CompanyName;
                string product = fvi.ProductName;
                string ApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

                dbPath = Path.Combine(ApplicationData, company, product, "logDB.db");

                SchemaHasChanged = false;

                Directory.CreateDirectory(Path.GetDirectoryName(dbPath));

                // Daily rotating backup, taken before the database is opened or touched in any
                // way, so even a backup of an already-corrupted-by-us session is impossible.
                BackupDatabaseDaily();

                OpenWithRetry();
                BackupBeforeLogsMigration();   // one-time safety copy before the logs-schema upgrade
                UpdateSchema();

            }
            catch (Exception e)
            {
                // RECORDED. This is the one failure that stops the program dead - MainWindow shows it
                // and shuts down - and it was the only one that never reached the log, so the file an
                // operator is asked to send said nothing about the thing he was complaining of.
                Log.Warn("Could not open the log database at " + dbPath + " - " + e.GetType().Name
                         + ": " + e.Message);
                throw new Exception("Failed to connect to DB: " + e.Message);
            }
            
        }

        // "HAS ANYTHING THAT MOVES A CALLSIGN BEEN WRITTEN?"
        //
        // Counted so that a screen holding a lookup built from the log - the cluster's set of worked
        // callsigns, the duplicate check's per-station index - can tell in one comparison whether its
        // answer is still good, without walking 28,454 QSOs to find out.
        //
        // Bumped by the writes that can add a QSO, remove one, move one between logs, or change the
        // callsigns on one. Deliberately NOT by the confirmation marks, the upload-queue flags or the
        // entity-code backfill: none of those can change who was worked, so making the caches rebuild
        // after a LoTW check would be work for nothing.
        //
        // Every edit in the program funnels through Update() - the main form, the Log Workshop grid, the
        // QSO editor, the Log Fixer - which is what makes this a reliable signal rather than a hopeful
        // one. It is the reason the caches those callers refused to keep are safe to keep now.
        private static long _contentVersion;

        public static long ContentVersion
        {
            get { return System.Threading.Interlocked.Read(ref _contentVersion); }
        }

        private static void BumpContentVersion()
        {
            System.Threading.Interlocked.Increment(ref _contentVersion);
        }

        // PER-LOG STATE. Everything a confirmation service remembers about a log - what it last reported,
        // which countries it confirmed, how far the incremental download has got - belongs to THAT log
        // and nothing else. It used to live in the application settings, one copy shared by every log,
        // so opening a second log showed the first one's figures as if they were its own: a brand new
        // log claimed 5,936 confirmations at LoTW before it had ever been checked.
        //
        // A key/value table rather than columns, because each service keeps a different handful of
        // values and they change as services are added. An ABSENT key is the honest answer "this log has
        // never been checked", which the caller can show as such instead of borrowing someone else's
        // number.
        public string GetLogState(long logId, string key)
        {
            if (logId <= 0 || string.IsNullOrEmpty(key)) return string.Empty;
            lock (_dbLock)
            {
                if (con == null || con.State != System.Data.ConnectionState.Open) return string.Empty;
                try
                {
                    using (var cmd = new SQLiteCommand("SELECT value FROM log_state WHERE log_id = @l AND key = @k", con))
                    {
                        cmd.Parameters.AddWithValue("@l", logId);
                        cmd.Parameters.AddWithValue("@k", key);
                        object v = cmd.ExecuteScalar();
                        return v == null || v == DBNull.Value ? string.Empty : v.ToString();
                    }
                }
                catch (Exception ex) { Log.Swallow(ex); return string.Empty; }
            }
        }

        public void SetLogState(long logId, string key, string value)
        {
            if (logId <= 0 || string.IsNullOrEmpty(key)) return;
            lock (_dbLock)
            {
                if (con == null || con.State != System.Data.ConnectionState.Open) return;
                try
                {
                    using (var cmd = new SQLiteCommand(
                        "INSERT OR REPLACE INTO log_state (log_id, key, value) VALUES (@l, @k, @v)", con))
                    {
                        cmd.Parameters.AddWithValue("@l", logId);
                        cmd.Parameters.AddWithValue("@k", key);
                        cmd.Parameters.AddWithValue("@v", (object)value ?? string.Empty);
                        cmd.ExecuteNonQuery();
                    }
                }
                catch (Exception ex) { Log.Swallow(ex); }
            }
        }

        // True when this log has never been checked by that service - so the window can say so rather
        // than showing a zero that looks like a real answer.
        public bool HasLogState(long logId, string key)
        {
            return !string.IsNullOrEmpty(GetLogState(logId, key));
        }

        // The folder holding logDB.db (and the Backups subfolder).
        public string DataFolder => Path.GetDirectoryName(dbPath);

        // The daily-backups folder (with HOW TO RESTORE.txt) -- for Help > Open Backups Folder.
        public string BackupsFolder => Path.Combine(DataFolder, "Backups");

        // WHERE EVERY REPORT GOES. The import report, the rejected-records ADIF, the LoTW upload log,
        // the unmatched-confirmations list - all of them used to be dropped on the operator's DESKTOP,
        // which is somebody's own space and not the program's to litter. One folder of the program's
        // own, beside the database and the backups, so a report can always be found and the desktop is
        // left alone.
        //
        // Static and independent of any open database, because a report can be written when no log is
        // open at all. Falls back to the desktop only if the folder cannot be made, which is better
        // than losing the report entirely.
        public static string ReportsFolder
        {
            get
            {
                try
                {
                    string dir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "4Z1KD", "HolyLogger", "Reports");
                    Directory.CreateDirectory(dir);
                    return dir;
                }
                catch (Exception swallowed)
                {
                    Log.Swallow(swallowed);
                    return Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                }
            }
        }

        // The live database file itself (logDB.db), for the in-app Restore feature.
        public string DbPath => dbPath;

        // WHERE A SAFETY COPY GOES, decided in ONE place. A safety copy is the whole database, taken
        // the moment before something rewrites it - the Log Fixer applying corrections, or a Restore
        // replacing the file. They used to be written by appending to the database's own path, which
        // landed them beside logDB.db while the daily backups went into Backups: two folders, for no
        // reason beyond how the code happened to be written, and an operator told "a copy was saved"
        // had to be told which of the two to look in.
        //
        // `kind` becomes part of the name - "fix", "restore" - so the Backups window can say what each
        // copy was taken before without being taught about every caller.
        //
        // Returns null when there is no database to copy, which the callers already treat as "no copy
        // was made" and ask the operator whether to go on regardless.
        public string SafetyCopyPath(string kind)
        {
            try
            {
                if (string.IsNullOrEmpty(dbPath) || !File.Exists(dbPath)) return null;
                string dir = BackupsFolder;
                Directory.CreateDirectory(dir);
                // One fewer, to leave room for the copy about to be made.
                PruneSafetyCopies(dir, SafetyCopiesToKeep - 1);
                return Path.Combine(dir, "logDB.db.pre-" + kind + "-"
                                         + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".bak");
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); return null; }
        }

        // How many safety copies to keep. Unlike the daily backups, these are made on DEMAND - every
        // press of Fix in the Log Fixer makes another - and nothing was ever deleting them: eight
        // copies of a 131 MB database had quietly taken a gigabyte of the operator's disk in one
        // afternoon's testing. Four is enough to undo a run of corrections and think better of it a
        // few steps later, which is what these exist for.
        private const int SafetyCopiesToKeep = 4;

        // Runs in two places, which is why `keep` is a parameter rather than the constant: BEFORE a new
        // copy is written (keeping one fewer, so the count afterwards is exactly the limit), and at
        // STARTUP (keeping the full number). Startup matters because copies are only ever made on
        // demand - an operator who fixes a log once and never again would otherwise keep whatever was
        // lying about for ever, including the ones just moved in from the old location.
        //
        // Both folders are swept, for the copies older versions left beside the database in case the
        // move at startup could not shift one.
        private void PruneSafetyCopies(string backupsDir, int keep)
        {
            try
            {
                var all = new List<string>();
                foreach (string dir in new[] { backupsDir, DataFolder })
                {
                    if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                    if (all.Count > 0 && string.Equals(dir, backupsDir, StringComparison.OrdinalIgnoreCase)) continue;
                    all.AddRange(Directory.GetFiles(dir, "logDB.db.pre-*.bak"));
                }

                // Newest first by the timestamp in the name, which sorts chronologically as text.
                foreach (string f in all.OrderByDescending(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
                                        .Skip(Math.Max(0, keep)))
                {
                    try { File.Delete(f); } catch (Exception ex) { Log.Swallow(ex); }
                }
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // Outcome of RestoreFromBackup: Ok + where the operator's pre-restore log was safely kept, or a
        // human-readable Error and nothing was changed (the original database is exactly as it was).
        public class RestoreResult
        {
            public bool Ok;
            public string SafetyCopyPath;
            public string Error;
        }

        // Swaps in a chosen backup as the live database, in-app - replacing the manual "close the app,
        // rename logDB.db by hand, copy the backup over, rename it back" procedure the operator used to
        // have to get exactly right themselves. safetyCopyFileName is the EXACT name shown to the operator
        // in the confirmation dialog before this runs, so what they were told matches what actually
        // happens - it is not generated fresh in here.
        //
        // The current database is NEVER deleted - only renamed aside - so a failed or regretted restore
        // is always recoverable. If copying the backup into place fails partway (disk full, permissions),
        // the original file is automatically moved back before returning, so the operator is never left
        // with no database at all.
        //
        // Does not reopen a connection or reset the singleton: every window and cached QSO list already
        // in this process still refers to the OLD database's data, so the only safe way forward is for the
        // caller to restart the application. Called with the same lock every other DB access uses, so it
        // cannot race a concurrent read/write.
        public RestoreResult RestoreFromBackup(string backupFilePath, string safetyCopyFileName)
        {
            var result = new RestoreResult();
            lock (_dbLock)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(backupFilePath) || !File.Exists(backupFilePath))
                    {
                        result.Error = "That backup file no longer exists.";
                        return result;
                    }

                    // The file cannot be moved or replaced while SQLite still has it open.
                    try { con?.Close(); con?.Dispose(); } catch (Exception ex) { Log.Swallow(ex); }
                    con = null;

                    // A FULL PATH IS HONOURED. Safety copies now go to the Backups folder alongside the
                    // daily ones (see SafetyCopyPath), so the caller passes where it wants this one to
                    // land. A bare filename still means "beside the database", which is what every
                    // older caller meant.
                    string safetyCopyPath = Path.IsPathRooted(safetyCopyFileName)
                        ? safetyCopyFileName
                        : Path.Combine(Path.GetDirectoryName(dbPath), safetyCopyFileName);
                    try { Directory.CreateDirectory(Path.GetDirectoryName(safetyCopyPath)); }
                    catch (Exception ex) { Log.Swallow(ex); }

                    if (File.Exists(dbPath))
                        File.Move(dbPath, safetyCopyPath);   // the undo path - this file is never deleted

                    try
                    {
                        File.Copy(backupFilePath, dbPath);
                    }
                    catch (Exception copyEx)
                    {
                        // Roll back automatically: the operator asked for a restore, not to be left with
                        // nothing if the copy itself fails.
                        Log.Warn("Restore FAILED while copying the backup over the database: " +
                                 copyEx.GetType().Name + ": " + copyEx.Message);
                        try
                        {
                            if (File.Exists(safetyCopyPath) && !File.Exists(dbPath))
                                File.Move(safetyCopyPath, dbPath);
                        }
                        catch (Exception rollbackEx) { Log.Swallow(rollbackEx); }
                        result.Error = "Could not copy the backup into place: " + copyEx.Message;
                        return result;
                    }

                    result.Ok = true;
                    result.SafetyCopyPath = safetyCopyPath;
                    return result;
                }
                catch (Exception ex)
                {
                    Log.Warn("Restore FAILED: " + ex.GetType().Name + ": " + ex.Message);
                    result.Error = ex.Message;
                    return result;
                }
            }
        }

        // How many daily backups to keep in the Backups folder; older ones are pruned.
        private const int DailyBackupsToKeep = 12;

        // Copies logDB.db to Backups\logDB-yyyy-MM-dd.db once per calendar day (extra app starts
        // on the same day are no-ops), then prunes to the newest DailyBackupsToKeep copies.
        // Runs BEFORE the SQLite connection opens, so the copied file is never mid-write.
        // A backup failure must never block startup: everything is swallowed (but logged).
        private void BackupDatabaseDaily()
        {
            try
            {
                if (!File.Exists(dbPath)) return;   // first run: nothing to back up yet

                string backupDir = Path.Combine(Path.GetDirectoryName(dbPath), "Backups");
                Directory.CreateDirectory(backupDir);

                // Safety copies used to be written beside the database; they belong in here with
                // everything else. Moved rather than explained: a line in the Backups window saying
                // "some older copies are still over there" is a note about the program's own history,
                // which is nothing an operator should have to read. Same volume, so this is a rename
                // and costs nothing however large the files are.
                foreach (string stray in Directory.GetFiles(Path.GetDirectoryName(dbPath), "logDB.db.pre-*.bak"))
                {
                    try
                    {
                        string moved = Path.Combine(backupDir, Path.GetFileName(stray));
                        if (!File.Exists(moved)) File.Move(stray, moved);
                        else File.Delete(stray);          // already there: the stray is the duplicate
                    }
                    catch (Exception ex) { Log.Swallow(ex); }
                }

                string todays = Path.Combine(backupDir,
                    "logDB-" + DateTime.Now.ToString("yyyy-MM-dd") + ".db");
                if (!File.Exists(todays))
                    File.Copy(dbPath, todays);

                // The safety copies get the same treatment as the daily ones, at the same moment. They
                // are only ever created on demand, so without this a folder full of them would simply
                // sit there until the operator happened to run the Log Fixer again.
                PruneSafetyCopies(backupDir, SafetyCopiesToKeep);

                // Prune: the date-stamped names sort chronologically, so ordering by name
                // descending puts the newest first.
                var old = Directory.GetFiles(backupDir, "logDB-????-??-??.db")
                                   .OrderByDescending(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                                   .Skip(DailyBackupsToKeep);
                foreach (string f in old)
                {
                    try { File.Delete(f); } catch (Exception ex) { Log.Swallow(ex); }
                }

                // Self-documenting folder: the restore instructions live right next to the backups,
                // so they are exactly where the user is looking when disaster strikes. Rewritten on
                // every startup so they always match the current app version's behavior.
                WriteRestoreInstructions(backupDir);

                // Also mirror today's backup to the optional user-configured extra folder (e.g. a
                // cloud or external-drive folder) for an off-machine copy. Runs in the background so
                // a slow/offline destination never delays startup.
                if (File.Exists(todays))
                    CopyDailyBackupToExtraFolder(todays);
            }
            catch (Exception ex)
            {
                Log.Swallow(ex);
            }
        }

        // Copies today's daily backup to Settings.ExtraBackupFolder (if the user set one) and prunes
        // that folder to the same retention. Backups are static files (never opened live by SQLite),
        // so a cloud-synced or network destination is safe here — unlike the live database. Runs on a
        // background thread and swallows every error: this is a bonus copy that must never block or
        // crash startup, even if the destination is offline, unplugged, or read-only.
        private void CopyDailyBackupToExtraFolder(string dailyBackupFile)
        {
            string extra = Properties.Settings.Default.ExtraBackupFolder;
            if (string.IsNullOrWhiteSpace(extra)) return;

            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    Directory.CreateDirectory(extra);

                    string dest = Path.Combine(extra, Path.GetFileName(dailyBackupFile));
                    if (!File.Exists(dest))
                        File.Copy(dailyBackupFile, dest);

                    var old = Directory.GetFiles(extra, "logDB-????-??-??.db")
                                       .OrderByDescending(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                                       .Skip(DailyBackupsToKeep);
                    foreach (string f in old)
                    {
                        try { File.Delete(f); } catch (Exception ex) { Log.Swallow(ex); }
                    }
                }
                catch (Exception ex) { Log.Swallow(ex); }
            });
        }

        private static void WriteRestoreInstructions(string backupDir)
        {
            try
            {
                File.WriteAllText(Path.Combine(backupDir, "HOW TO RESTORE.txt"), GetRestoreInstructions(backupDir));
            }
            catch (Exception ex)
            {
                Log.Swallow(ex);
            }
        }

        // The restore instructions, written to HOW TO RESTORE.txt in the backups folder - a manual
        // fallback for the one case the in-app Restore button can't cover: HolyLogger failing to launch
        // at all. No longer shown in-app (the Backups & Restore window now restores with a button instead
        // of asking the operator to rename/copy files by hand); kept here only for that fallback case.
        public static string GetRestoreInstructions(string backupsFolder)
        {
            string backups = backupsFolder;   // ...\Backups
            string logFolder = Path.GetDirectoryName(backupsFolder.TrimEnd('\\', '/')) ?? backupsFolder;   // parent = folder holding logDB.db

            string step4 =
                "4. Open your backups folder:" + Environment.NewLine +
                "      " + backups + Environment.NewLine +
                "   and pick the backup with the most recent date from BEFORE the problem" + Environment.NewLine +
                "   happened, e.g.  logDB-2026-07-03.db" + Environment.NewLine;

            return
"HOW TO RESTORE YOUR LOG FROM A BACKUP" + Environment.NewLine +
"=====================================" + Environment.NewLine +
Environment.NewLine +
"Your daily backups are saved in this folder (one file per day, the last" + Environment.NewLine +
DailyBackupsToKeep + " days are kept):" + Environment.NewLine +
"   " + backups + Environment.NewLine +
Environment.NewLine +
"Your live log database (the file  logDB.db ) is in this folder:" + Environment.NewLine +
"   " + logFolder + Environment.NewLine +
Environment.NewLine +
"If your log is damaged or QSOs were lost by mistake, do this:" + Environment.NewLine +
Environment.NewLine +
"1. Close HolyLogger completely." + Environment.NewLine +
"2. Open your log-database folder (copy-paste this path into the address bar" + Environment.NewLine +
"   of File Explorer):" + Environment.NewLine +
"      " + logFolder + Environment.NewLine +
"3. Protect the damaged file first: rename  logDB.db  to  logDB.damaged" + Environment.NewLine +
"   (right-click -> Rename). Do NOT delete it - it may still be useful." + Environment.NewLine +
step4 +
"5. COPY that backup file into your log-database folder (from step 2), and" + Environment.NewLine +
"   rename the copy to exactly:  logDB.db" + Environment.NewLine +
"6. Start HolyLogger. Your log is back to how it was on that day." + Environment.NewLine +
Environment.NewLine +
"QSOs made after the backup date are not in the backup - re-enter them or" + Environment.NewLine +
"re-import them from an ADIF export if you have one." + Environment.NewLine;
        }

        // Public static method to get the single instance of the class.
        public static DataAccess GetInstance()
        {
            // Double-checked locking so the singleton is created exactly once even if two threads
            // race here at startup.
            if (instance == null)
            {
                lock (_instanceLock)
                {
                    if (instance == null)
                        instance = new DataAccess();
                }
            }
            return instance;
        }

        public void Close()
        {
            lock (_dbLock)
            {
                con.Close();
                con.Dispose();
                instance = null;
            }
        }

        public QSO Insert(QSO qso)
        {
            lock (_dbLock)
            {
            if (con != null && con.State == System.Data.ConnectionState.Open)
            {
                SQLiteCommand insertSQL = new SQLiteCommand("INSERT INTO qso (my_callsign,operator,my_square,my_locator,dx_locator,frequency,band,dx_callsign,rst_rcvd,rst_sent,date,time,mode,submode,exchange,comment,name,country,continent,cq_zone,itu_zone,state,qth,dxcc,prop_mode,sat_name,soapbox,iota,sota_ref,pota_ref,wwff_ref,sig,sig_info,log_id) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?," + ActiveLogId + ")", con);
                insertSQL.Parameters.Add(new SQLiteParameter("my_callsign", qso.MyCall));
                insertSQL.Parameters.Add(new SQLiteParameter("operator", qso.Operator));
                insertSQL.Parameters.Add(new SQLiteParameter("my_square", qso.STX));
                insertSQL.Parameters.Add(new SQLiteParameter("my_locator", qso.MyLocator));
                insertSQL.Parameters.Add(new SQLiteParameter("dx_locator", qso.DXLocator));
                insertSQL.Parameters.Add(new SQLiteParameter("frequency", qso.Freq));
                insertSQL.Parameters.Add(new SQLiteParameter("band", qso.Band));
                insertSQL.Parameters.Add(new SQLiteParameter("dx_callsign", qso.DXCall));
                insertSQL.Parameters.Add(new SQLiteParameter("rst_rcvd", qso.RST_RCVD));
                insertSQL.Parameters.Add(new SQLiteParameter("rst_sent", qso.RST_SENT));
                insertSQL.Parameters.Add(new SQLiteParameter("date", qso.Date));
                insertSQL.Parameters.Add(new SQLiteParameter("time", qso.Time));
                insertSQL.Parameters.Add(new SQLiteParameter("mode", qso.Mode));
                insertSQL.Parameters.Add(new SQLiteParameter("submode", qso.SUBMode));
                insertSQL.Parameters.Add(new SQLiteParameter("exchange", qso.SRX));
                insertSQL.Parameters.Add(new SQLiteParameter("comment", qso.Comment));
                insertSQL.Parameters.Add(new SQLiteParameter("name", qso.Name));
                insertSQL.Parameters.Add(new SQLiteParameter("country", qso.Country));
                insertSQL.Parameters.Add(new SQLiteParameter("continent", qso.Continent));
                insertSQL.Parameters.Add(new SQLiteParameter("cq_zone", qso.CQZone));
                insertSQL.Parameters.Add(new SQLiteParameter("itu_zone", qso.ITUZone));
                insertSQL.Parameters.Add(new SQLiteParameter("state", qso.State));
                insertSQL.Parameters.Add(new SQLiteParameter("qth", qso.Qth));

                insertSQL.Parameters.Add(new SQLiteParameter("dxcc", qso.DxccCode > 0 ? (object)qso.DxccCode : DBNull.Value));
                insertSQL.Parameters.Add(new SQLiteParameter("prop_mode", qso.PROP_MODE));
                insertSQL.Parameters.Add(new SQLiteParameter("sat_name", qso.SAT_NAME));
                insertSQL.Parameters.Add(new SQLiteParameter("soapbox", qso.SOAPBOX));
                AddActivityParams(insertSQL, qso, "");
                try
                {
                    insertSQL.ExecuteNonQuery();
                    ObservableCollection<QSO> top1 = GetTopQSOs(1);
                    return top1.FirstOrDefault();
                }
                catch (Exception ex)
                {
                    throw new Exception(ex.Message);
                }
            }
            return null;
            }
        }
        // Inserts a COPY of an existing QSO into another log, tagged with source_qso_id. Never triggers
        // further copying. Returns the copy's Id. Caller holds _dbLock.
        //
        // A copy is NEVER uploaded on its own: the ORIGINAL is the single upload vehicle for the contact
        // (the copy is a record-only mirror, e.g. so contest QSOs also count for awards in the main log).
        // So every service is marked "already handled" (status 1 -- the same convention imported rows use)
        // and the copy stays out of every upload queue. Otherwise a live QSO (source pending, 0) would put
        // the SAME contact in the queue twice, once per log.
        private long InsertQsoCopy(QSO qso, long targetLogId, long sourceQsoId)
        {
            const int es = 1, qs = 1, ls = 1, cs = 1;
            using (var ins = new SQLiteCommand(
                "INSERT INTO qso (my_callsign,operator,my_square,my_locator,dx_locator,frequency,band,dx_callsign,rst_rcvd,rst_sent,date,time,mode,submode,exchange,comment,name,country,continent,cq_zone,itu_zone,state,qth,dxcc,prop_mode,sat_name,soapbox,iota,sota_ref,pota_ref,wwff_ref,sig,sig_info," + CarriedColumns + ",eqsl_status,qrz_status,lotw_status,clublog_status,log_id,source_qso_id) " +
                "VALUES (@my_callsign,@operator,@my_square,@my_locator,@dx_locator,@frequency,@band,@dx_callsign,@rst_rcvd,@rst_sent,@date,@time,@mode,@submode,@exchange,@comment,@name,@country,@continent,@cq_zone,@itu_zone,@state,@qth,@dxcc,@prop_mode,@sat_name,@soapbox,@iota,@sota_ref,@pota_ref,@wwff_ref,@sig,@sig_info," + CarriedValues + ",@es,@qs,@ls,@cs,@log_id,@src)", con))
            {
                ins.Parameters.Add(new SQLiteParameter("@my_callsign", qso.MyCall));
                ins.Parameters.Add(new SQLiteParameter("@operator", qso.Operator));
                ins.Parameters.Add(new SQLiteParameter("@my_square", qso.STX));
                ins.Parameters.Add(new SQLiteParameter("@my_locator", qso.MyLocator));
                ins.Parameters.Add(new SQLiteParameter("@dx_locator", qso.DXLocator));
                ins.Parameters.Add(new SQLiteParameter("@frequency", qso.Freq));
                ins.Parameters.Add(new SQLiteParameter("@band", qso.Band));
                ins.Parameters.Add(new SQLiteParameter("@dx_callsign", qso.DXCall));
                ins.Parameters.Add(new SQLiteParameter("@rst_rcvd", qso.RST_RCVD));
                ins.Parameters.Add(new SQLiteParameter("@rst_sent", qso.RST_SENT));
                ins.Parameters.Add(new SQLiteParameter("@date", qso.Date));
                ins.Parameters.Add(new SQLiteParameter("@time", qso.Time));
                ins.Parameters.Add(new SQLiteParameter("@mode", qso.Mode));
                ins.Parameters.Add(new SQLiteParameter("@submode", qso.SUBMode));
                ins.Parameters.Add(new SQLiteParameter("@exchange", qso.SRX));
                ins.Parameters.Add(new SQLiteParameter("@comment", qso.Comment));
                ins.Parameters.Add(new SQLiteParameter("@name", qso.Name));
                ins.Parameters.Add(new SQLiteParameter("@country", qso.Country));
                ins.Parameters.Add(new SQLiteParameter("@continent", qso.Continent));
                ins.Parameters.Add(new SQLiteParameter("@cq_zone", qso.CQZone));
                ins.Parameters.Add(new SQLiteParameter("@itu_zone", qso.ITUZone));
                // A copy is the same contact, so it carries the same STATE. It did not: the column was
                // missing here while every other writer had it, so a mirrored QSO arrived in the target
                // log with its state blank and the operator had no way to tell where it went.
                ins.Parameters.Add(new SQLiteParameter("@state", qso.State));
                ins.Parameters.Add(new SQLiteParameter("@qth", qso.Qth));

                ins.Parameters.Add(new SQLiteParameter("@dxcc", qso.DxccCode > 0 ? (object)qso.DxccCode : DBNull.Value));
                ins.Parameters.Add(new SQLiteParameter("@prop_mode", qso.PROP_MODE));
                ins.Parameters.Add(new SQLiteParameter("@sat_name", qso.SAT_NAME));
                ins.Parameters.Add(new SQLiteParameter("@soapbox", qso.SOAPBOX));
                AddActivityParams(ins, qso, "@");
                // A copy is a mirror of the contact, so it carries the same imported fields as its original.
                AddCarriedParams(ins, qso);
                ins.Parameters.Add(new SQLiteParameter("@es", es));
                ins.Parameters.Add(new SQLiteParameter("@qs", qs));
                ins.Parameters.Add(new SQLiteParameter("@ls", ls));
                ins.Parameters.Add(new SQLiteParameter("@cs", cs));
                ins.Parameters.Add(new SQLiteParameter("@log_id", targetLogId));
                ins.Parameters.Add(new SQLiteParameter("@src", sourceQsoId));
                ins.ExecuteNonQuery();
            }
            using (var idcmd = new SQLiteCommand("SELECT last_insert_rowid()", con))
                return Convert.ToInt64(idcmd.ExecuteScalar());
        }

        // Called right after a QSO is logged. If the source log has a copy-target, and the QSO's station
        // callsign AND operator both match the target log's identity, and the target doesn't already have
        // this contact, inserts a linked copy into the target log. Returns the copy Id, or 0 if nothing
        // was copied. Best-effort: never throws, so copying can't break logging.
        public long CopyQsoToTargetIfConfigured(QSO qso, long sourceLogId)
        {
            BumpContentVersion();   // this write can change who is in the log or what they are called
            try
            {
                lock (_dbLock)
                {
                    if (con == null || con.State != ConnectionState.Open || qso == null || qso.id <= 0) return 0;

                    long targetId = 0;
                    using (var tc = new SQLiteCommand("SELECT copy_target_log_id FROM logs WHERE Id = ?", con))
                    {
                        tc.Parameters.Add(new SQLiteParameter(null, sourceLogId));
                        var o = tc.ExecuteScalar();
                        if (o != null && o != DBNull.Value) targetId = Convert.ToInt64(o);
                    }
                    if (targetId <= 0 || targetId == sourceLogId) return 0;

                    // Never copy INTO a contest log — a contest log's QSOs must come only from contest
                    // operation (with its dupe-check / serial / Cabrillo logic), never as passive copies.
                    using (var ec = new SQLiteCommand("SELECT event_type FROM logs WHERE Id = ?", con))
                    {
                        ec.Parameters.Add(new SQLiteParameter(null, targetId));
                        var eo = ec.ExecuteScalar();
                        if (eo != null && eo != DBNull.Value && !string.IsNullOrWhiteSpace(eo.ToString())) return 0;
                    }

                    string tcall = string.Empty, toper = string.Empty;
                    using (var ic = new SQLiteCommand("SELECT log_callsign, log_operator FROM logs WHERE Id = ?", con))
                    {
                        ic.Parameters.Add(new SQLiteParameter(null, targetId));
                        using (var rdr = ic.ExecuteReader())
                            if (rdr.Read())
                            {
                                tcall = rdr["log_callsign"] == DBNull.Value ? string.Empty : rdr["log_callsign"].ToString();
                                toper = rdr["log_operator"] == DBNull.Value ? string.Empty : rdr["log_operator"].ToString();
                            }
                    }
                    // Identity filter: BOTH station callsign AND operator must match the target log's identity.
                    // Callsigns match per CallsignIdentity: stroke suffixes (/M, /2, ...) don't matter.
                    if (string.IsNullOrWhiteSpace(tcall) || string.IsNullOrWhiteSpace(toper)) return 0;
                    if (!CallsignIdentity.Same(qso.MyCall, tcall)) return 0;
                    if (!string.Equals((qso.Operator ?? string.Empty).Trim(), toper.Trim(), StringComparison.OrdinalIgnoreCase)) return 0;

                    // Duplicate check in the target log (same worked call + band + mode + date + time).
                    using (var dc = new SQLiteCommand("SELECT count(*) FROM qso WHERE log_id = @lid AND dx_callsign = @c AND band = @b AND mode = @m AND date = @d AND time = @t", con))
                    {
                        dc.Parameters.Add(new SQLiteParameter("@lid", targetId));
                        dc.Parameters.Add(new SQLiteParameter("@c", (object)(qso.DXCall ?? string.Empty)));
                        dc.Parameters.Add(new SQLiteParameter("@b", (object)(qso.Band ?? string.Empty)));
                        dc.Parameters.Add(new SQLiteParameter("@m", (object)(qso.Mode ?? string.Empty)));
                        dc.Parameters.Add(new SQLiteParameter("@d", (object)(qso.Date ?? string.Empty)));
                        dc.Parameters.Add(new SQLiteParameter("@t", (object)(qso.Time ?? string.Empty)));
                        if (Convert.ToInt32(dc.ExecuteScalar()) > 0) return 0;
                    }

                    return InsertQsoCopy(qso, targetId, qso.id);
                }
            }
            // A FAILURE HERE IS INVISIBLE UNLESS IT IS WRITTEN DOWN. Every "return 0" above is a
            // legitimate answer meaning "no copy was due" - no target log, wrong identity, already
            // there - and the caller cannot tell those apart from a fault. So a QSO that should have
            // been copied into the second log and was not leaves no mark anywhere: not in the target
            // log, not on screen, nowhere. The line names the callsign so the QSO can be found and
            // copied by hand.
            catch (Exception ex)
            {
                Log.Warn("The copy of " + (qso != null ? (qso.DXCall ?? "?") : "?") +
                         " into the copy-target log of log " + sourceLogId + " FAILED: " +
                         ex.GetType().Name + ": " + ex.Message);
                return 0;
            }
        }

        public bool Insert(IEnumerable<QSO> qsos)
        {
            BumpContentVersion();   // this write can change who is in the log or what they are called
            lock (_dbLock)
            {
            if (con != null && con.State == System.Data.ConnectionState.Open)
            {
                SQLiteTransaction T = con.BeginTransaction();
                foreach (var qso in qsos)
                {
                    SQLiteCommand insertSQL = new SQLiteCommand("INSERT INTO qso (my_callsign,operator,my_square,my_locator,dx_locator,frequency,band,dx_callsign,rst_rcvd,rst_sent,date,time,mode,submode,exchange,comment,name,country,continent,cq_zone,itu_zone,prop_mode,sat_name,soapbox,iota,sota_ref,pota_ref,wwff_ref,sig,sig_info,eqsl_status,qrz_status,lotw_status,clublog_status,log_id) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,1,1,1,1," + ActiveLogId + ")", con);
                    insertSQL.Transaction = T;
                    insertSQL.Parameters.Add(new SQLiteParameter("my_callsign", qso.MyCall));
                    insertSQL.Parameters.Add(new SQLiteParameter("operator", qso.Operator));
                    insertSQL.Parameters.Add(new SQLiteParameter("my_square", qso.STX));
                    insertSQL.Parameters.Add(new SQLiteParameter("my_locator", qso.MyLocator));
                    insertSQL.Parameters.Add(new SQLiteParameter("dx_locator", qso.DXLocator));
                    insertSQL.Parameters.Add(new SQLiteParameter("frequency", qso.Freq));
                    insertSQL.Parameters.Add(new SQLiteParameter("band", qso.Band));
                    insertSQL.Parameters.Add(new SQLiteParameter("dx_callsign", qso.DXCall));
                    insertSQL.Parameters.Add(new SQLiteParameter("rst_rcvd", qso.RST_RCVD));
                    insertSQL.Parameters.Add(new SQLiteParameter("rst_sent", qso.RST_SENT));
                    insertSQL.Parameters.Add(new SQLiteParameter("date", qso.Date));
                    insertSQL.Parameters.Add(new SQLiteParameter("time", qso.Time));
                    insertSQL.Parameters.Add(new SQLiteParameter("mode", qso.Mode));
                    insertSQL.Parameters.Add(new SQLiteParameter("submode", qso.SUBMode));
                    insertSQL.Parameters.Add(new SQLiteParameter("exchange", qso.SRX));
                    insertSQL.Parameters.Add(new SQLiteParameter("comment", qso.Comment));
                    insertSQL.Parameters.Add(new SQLiteParameter("name", qso.Name));
                    insertSQL.Parameters.Add(new SQLiteParameter("country", qso.Country));
                    insertSQL.Parameters.Add(new SQLiteParameter("continent", qso.Continent));
                    insertSQL.Parameters.Add(new SQLiteParameter("cq_zone", qso.CQZone));
                    insertSQL.Parameters.Add(new SQLiteParameter("itu_zone", qso.ITUZone));
                    insertSQL.Parameters.Add(new SQLiteParameter("prop_mode", qso.PROP_MODE));
                    insertSQL.Parameters.Add(new SQLiteParameter("sat_name", qso.SAT_NAME));
                    insertSQL.Parameters.Add(new SQLiteParameter("soapbox", qso.SOAPBOX));
                    AddActivityParams(insertSQL, qso, "");
                }
                try
                {
                    T.Commit();
                    return true;
                }
                catch (Exception e)
                {
                    // The whole batch is rolled back and the caller is told "false" - which is all the
                    // caller can say to the operator. WHY it failed only exists here, so write it down.
                    Log.Warn("Insert of a batch of QSOs FAILED and was rolled back: " +
                             e.GetType().Name + ": " + e.Message);
                    T.Rollback();
                    return false;
                }
            }
            return false;
            }
        }

        // failed, when supplied, receives every QSO the database refused ALONG WITH what it said. The
        // count on its own told the operator that contacts had been lost without telling them which,
        // and a number is not something anyone can act on.
        public int InsertBatch(IEnumerable<QSO> qsos, Action<int> progressCallback = null,
                               List<KeyValuePair<QSO, string>> failed = null)
        {
            BumpContentVersion();   // this write can change who is in the log or what they are called
            lock (_dbLock)
            {
            if (con == null || con.State != System.Data.ConnectionState.Open)
                throw new InvalidOperationException("Database connection is not open.");

            int faultyQso = 0;
            int processedQso = 0;

            using (SQLiteTransaction transaction = con.BeginTransaction())
            using (SQLiteCommand insertSQL = new SQLiteCommand("INSERT INTO qso (my_callsign,operator,my_square,my_locator,dx_locator,frequency,band,dx_callsign,rst_rcvd,rst_sent,date,time,mode,submode,exchange,comment,name,country,continent,prop_mode,sat_name,soapbox,cq_zone,itu_zone,eqsl_status,qrz_status,lotw_status,clublog_status,lotw_qsl_rcvd,lotw_qsl_rdate,lotw_deleted_entity,qrz_qsl_rcvd,qrz_qsl_rdate,qrz_deleted_entity,eqsl_qsl_rcvd,eqsl_qsl_rdate,eqsl_deleted_entity,clublog_qsl_rcvd,clublog_qsl_rdate,clublog_deleted_entity,paper_qsl_rcvd,state,iota,sota_ref,pota_ref,wwff_ref,sig,sig_info,credit_granted,cnty,qsl_via,qsl_rdate,qsl_sent,contest_id,time_off,date_off,extra_adif,qth,dxcc,log_id) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,1,1,?,1,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?," + ActiveLogId + ")", con, transaction))
            {
                insertSQL.Parameters.Add(new SQLiteParameter("my_callsign"));
                insertSQL.Parameters.Add(new SQLiteParameter("operator"));
                insertSQL.Parameters.Add(new SQLiteParameter("my_square"));
                insertSQL.Parameters.Add(new SQLiteParameter("my_locator"));
                insertSQL.Parameters.Add(new SQLiteParameter("dx_locator"));
                insertSQL.Parameters.Add(new SQLiteParameter("frequency"));
                insertSQL.Parameters.Add(new SQLiteParameter("band"));
                insertSQL.Parameters.Add(new SQLiteParameter("dx_callsign"));
                insertSQL.Parameters.Add(new SQLiteParameter("rst_rcvd"));
                insertSQL.Parameters.Add(new SQLiteParameter("rst_sent"));
                insertSQL.Parameters.Add(new SQLiteParameter("date"));
                insertSQL.Parameters.Add(new SQLiteParameter("time"));
                insertSQL.Parameters.Add(new SQLiteParameter("mode"));
                insertSQL.Parameters.Add(new SQLiteParameter("submode"));
                insertSQL.Parameters.Add(new SQLiteParameter("exchange"));
                insertSQL.Parameters.Add(new SQLiteParameter("comment"));
                insertSQL.Parameters.Add(new SQLiteParameter("name"));
                insertSQL.Parameters.Add(new SQLiteParameter("country"));
                insertSQL.Parameters.Add(new SQLiteParameter("continent"));
                insertSQL.Parameters.Add(new SQLiteParameter("prop_mode"));
                insertSQL.Parameters.Add(new SQLiteParameter("sat_name"));
                insertSQL.Parameters.Add(new SQLiteParameter("soapbox"));
                insertSQL.Parameters.Add(new SQLiteParameter("cq_zone"));
                insertSQL.Parameters.Add(new SQLiteParameter("itu_zone"));
                insertSQL.Parameters.Add(new SQLiteParameter("lotw_status"));
                // Confirmation status, so an ADIF exported by HolyLogger keeps every tick on re-import
                // (even onto another computer). Positional parameters - keep this order matching the SQL.
                insertSQL.Parameters.Add(new SQLiteParameter("lotw_qsl_rcvd"));
                insertSQL.Parameters.Add(new SQLiteParameter("lotw_qsl_rdate"));
                insertSQL.Parameters.Add(new SQLiteParameter("lotw_deleted_entity"));
                insertSQL.Parameters.Add(new SQLiteParameter("qrz_qsl_rcvd"));
                insertSQL.Parameters.Add(new SQLiteParameter("qrz_qsl_rdate"));
                insertSQL.Parameters.Add(new SQLiteParameter("qrz_deleted_entity"));
                insertSQL.Parameters.Add(new SQLiteParameter("eqsl_qsl_rcvd"));
                insertSQL.Parameters.Add(new SQLiteParameter("eqsl_qsl_rdate"));
                insertSQL.Parameters.Add(new SQLiteParameter("eqsl_deleted_entity"));
                insertSQL.Parameters.Add(new SQLiteParameter("clublog_qsl_rcvd"));
                insertSQL.Parameters.Add(new SQLiteParameter("clublog_qsl_rdate"));
                insertSQL.Parameters.Add(new SQLiteParameter("clublog_deleted_entity"));
                insertSQL.Parameters.Add(new SQLiteParameter("paper_qsl_rcvd"));
                insertSQL.Parameters.Add(new SQLiteParameter("state"));
                // Activity references carried by the imported file - positional, so they stay last.
                insertSQL.Parameters.Add(new SQLiteParameter("iota"));
                insertSQL.Parameters.Add(new SQLiteParameter("sota_ref"));
                insertSQL.Parameters.Add(new SQLiteParameter("pota_ref"));
                insertSQL.Parameters.Add(new SQLiteParameter("wwff_ref"));
                insertSQL.Parameters.Add(new SQLiteParameter("sig"));
                insertSQL.Parameters.Add(new SQLiteParameter("sig_info"));
                // The award / QSL record carried in from the file - positional, so this order matches the
                // column list above exactly.
                insertSQL.Parameters.Add(new SQLiteParameter("credit_granted"));
                insertSQL.Parameters.Add(new SQLiteParameter("cnty"));
                insertSQL.Parameters.Add(new SQLiteParameter("qsl_via"));
                insertSQL.Parameters.Add(new SQLiteParameter("qsl_rdate"));
                insertSQL.Parameters.Add(new SQLiteParameter("qsl_sent"));
                insertSQL.Parameters.Add(new SQLiteParameter("contest_id"));
                insertSQL.Parameters.Add(new SQLiteParameter("time_off"));
                insertSQL.Parameters.Add(new SQLiteParameter("date_off"));
                // Every field of the imported record HolyLogger has no column for, kept verbatim so an
                // operator's log is never quietly stripped by passing through here. Last, positionally.
                insertSQL.Parameters.Add(new SQLiteParameter("extra_adif"));
                // ADIF QTH. Appended after extra_adif rather than next to state, because these parameters
                // are POSITIONAL: a new one in the middle would shift every index below it.
                insertSQL.Parameters.Add(new SQLiteParameter("qth"));

                insertSQL.Parameters.Add(new SQLiteParameter("dxcc"));

                foreach (var qso in qsos)
                {
                    insertSQL.Parameters[0].Value = (object)qso.MyCall ?? DBNull.Value;
                    insertSQL.Parameters[1].Value = (object)qso.Operator ?? DBNull.Value;
                    insertSQL.Parameters[2].Value = (object)qso.STX ?? DBNull.Value;
                    insertSQL.Parameters[3].Value = (object)qso.MyLocator ?? DBNull.Value;
                    insertSQL.Parameters[4].Value = (object)qso.DXLocator ?? DBNull.Value;
                    insertSQL.Parameters[5].Value = (object)qso.Freq ?? DBNull.Value;
                    insertSQL.Parameters[6].Value = (object)qso.Band ?? DBNull.Value;
                    insertSQL.Parameters[7].Value = (object)qso.DXCall ?? DBNull.Value;
                    insertSQL.Parameters[8].Value = (object)qso.RST_RCVD ?? DBNull.Value;
                    insertSQL.Parameters[9].Value = (object)qso.RST_SENT ?? DBNull.Value;
                    insertSQL.Parameters[10].Value = (object)qso.Date ?? DBNull.Value;
                    insertSQL.Parameters[11].Value = (object)qso.Time ?? DBNull.Value;
                    insertSQL.Parameters[12].Value = (object)qso.Mode ?? DBNull.Value;
                    insertSQL.Parameters[13].Value = (object)qso.SUBMode ?? DBNull.Value;
                    insertSQL.Parameters[14].Value = (object)qso.SRX ?? DBNull.Value;
                    insertSQL.Parameters[15].Value = (object)qso.Comment ?? DBNull.Value;
                    insertSQL.Parameters[16].Value = (object)qso.Name ?? DBNull.Value;
                    insertSQL.Parameters[17].Value = (object)qso.Country ?? DBNull.Value;
                    insertSQL.Parameters[18].Value = (object)qso.Continent ?? DBNull.Value;
                    insertSQL.Parameters[19].Value = (object)qso.PROP_MODE ?? DBNull.Value;
                    insertSQL.Parameters[20].Value = (object)qso.SAT_NAME ?? DBNull.Value;
                    insertSQL.Parameters[21].Value = (object)qso.SOAPBOX ?? DBNull.Value;
                    insertSQL.Parameters[22].Value = (object)qso.CQZone ?? DBNull.Value;
                    insertSQL.Parameters[23].Value = (object)qso.ITUZone ?? DBNull.Value;
                    insertSQL.Parameters[24].Value = qso.LotwStatus;
                    insertSQL.Parameters[25].Value = qso.LotwQslRcvd;
                    insertSQL.Parameters[26].Value = (object)qso.LotwQslRDate ?? DBNull.Value;
                    insertSQL.Parameters[27].Value = qso.LotwDeletedEntity;
                    insertSQL.Parameters[28].Value = qso.QrzQslRcvd;
                    insertSQL.Parameters[29].Value = (object)qso.QrzQslRDate ?? DBNull.Value;
                    insertSQL.Parameters[30].Value = qso.QrzDeletedEntity;
                    insertSQL.Parameters[31].Value = qso.EqslQslRcvd;
                    insertSQL.Parameters[32].Value = (object)qso.EqslQslRDate ?? DBNull.Value;
                    insertSQL.Parameters[33].Value = qso.EqslDeletedEntity;
                    insertSQL.Parameters[34].Value = qso.ClublogQslRcvd;
                    insertSQL.Parameters[35].Value = (object)qso.ClublogQslRDate ?? DBNull.Value;
                    insertSQL.Parameters[36].Value = qso.ClublogDeletedEntity;
                    insertSQL.Parameters[37].Value = qso.PaperQslRcvd;
                    insertSQL.Parameters[38].Value = (object)qso.State ?? DBNull.Value;
                    insertSQL.Parameters[39].Value = Blank(qso.Iota);
                    insertSQL.Parameters[40].Value = Blank(qso.SotaRef);
                    insertSQL.Parameters[41].Value = Blank(qso.PotaRef);
                    insertSQL.Parameters[42].Value = Blank(qso.WwffRef);
                    insertSQL.Parameters[43].Value = Blank(qso.Sig);
                    insertSQL.Parameters[44].Value = Blank(qso.SigInfo);
                    insertSQL.Parameters[45].Value = Blank(qso.CreditGranted);
                    insertSQL.Parameters[46].Value = Blank(qso.Cnty);
                    insertSQL.Parameters[47].Value = Blank(qso.QslVia);
                    insertSQL.Parameters[48].Value = Blank(qso.QslRDate);
                    insertSQL.Parameters[49].Value = Blank(qso.QslSent);
                    insertSQL.Parameters[50].Value = Blank(qso.ContestId);
                    insertSQL.Parameters[51].Value = Blank(qso.TimeOff);
                    insertSQL.Parameters[52].Value = Blank(qso.DateOff);
                    insertSQL.Parameters[53].Value = Blank(qso.ExtraAdif);
                    insertSQL.Parameters[54].Value = Blank(qso.Qth);

                    insertSQL.Parameters[55].Value = qso.DxccCode > 0 ? (object)qso.DxccCode : DBNull.Value;

                    try
                    {
                        insertSQL.ExecuteNonQuery();
                    }
                    catch (Exception ex)
                    {
                        faultyQso++;
                        if (failed != null) failed.Add(new KeyValuePair<QSO, string>(qso, ex.Message));
                        // Debug.WriteLine reaches a debugger and nobody else - in a shipped build it goes
                        // nowhere. A QSO that did not make it into the log is exactly the thing that must
                        // still be answerable a week later, so it goes in the file, named.
                        Log.Warn("QSO " + (qso != null ? (qso.DXCall ?? "?") : "?") + " on " +
                                 (qso != null ? (qso.Date ?? "?") : "?") + " could not be inserted: " + ex.Message);
                    }

                    processedQso++;
                    progressCallback?.Invoke(processedQso);
                }

                try
                {
                    transaction.Commit();
                }
                catch
                {
                    transaction.Rollback();
                    throw;
                }
            }

            return faultyQso;
            }
        }
        // MANY WRITES AS ONE COMMIT. SQLite gives every statement its own transaction unless told
        // otherwise, and a commit means waiting for the disk - which is invisible for one QSO and
        // ruinous for thousands. The Log Fixer correcting 4,376 country spellings did 4,376 commits
        // and appeared to hang; inside one transaction the same work is a single commit at the end.
        //
        // The lock is the same one every writer here takes, and Monitor is re-entrant, so the Update
        // calls inside the action nest quietly. SQLite's transactions belong to the CONNECTION, so
        // those calls join this one without having to be told about it.
        //
        // If the action throws, nothing is committed: the log is left exactly as it was.
        public void RunInTransaction(Action work)
        {
            if (work == null) return;
            lock (_dbLock)
            {
                if (con == null || con.State != System.Data.ConnectionState.Open) { work(); return; }
                using (var tx = con.BeginTransaction())
                {
                    work();
                    tx.Commit();
                }
            }
        }

        public void Update(QSO qso)
        {
            BumpContentVersion();   // this write can change who is in the log or what they are called
            lock (_dbLock)
            {
            if (con != null && con.State == System.Data.ConnectionState.Open)
            {
                // Copy-to-log: an edit must keep the linked partner (copy or original) identical. Collect
                // this QSO plus any linked row (parent original + child copies) and apply the same DATA
                // update to each. We never touch their log_id / source_qso_id / upload-status here, so a
                // one-shot loop is safe and cannot ping-pong.
                var ids = new List<long> { qso.id };
                try
                {
                    using (var pc = new SQLiteCommand("SELECT source_qso_id FROM qso WHERE Id = ?", con))
                    {
                        pc.Parameters.Add(new SQLiteParameter(null, (long)qso.id));
                        var o = pc.ExecuteScalar();
                        if (o != null && o != DBNull.Value) { long pid = Convert.ToInt64(o); if (pid > 0 && !ids.Contains(pid)) ids.Add(pid); }
                    }
                    using (var cc = new SQLiteCommand("SELECT Id FROM qso WHERE source_qso_id = ?", con))
                    {
                        cc.Parameters.Add(new SQLiteParameter(null, (long)qso.id));
                        using (var rdr = cc.ExecuteReader())
                            while (rdr.Read()) { long cid = Convert.ToInt64(rdr["Id"]); if (!ids.Contains(cid)) ids.Add(cid); }
                    }
                }
                // Best-effort: at minimum the QSO itself is updated below. Written down all the same,
                // because the consequence of failing here is the one thing an operator would never
                // suspect - the edit lands on this QSO and its copy in the other log keeps the old text.
                catch (Exception ex) { Log.Swallow(ex); }

                const string sql = "UPDATE qso SET my_callsign = @my_callsign ,operator = @operator ,my_square = @my_square,my_locator = @my_locator,dx_locator = @dx_locator,frequency = @frequency,band = @band,dx_callsign = @dx_callsign,rst_rcvd = @rst_rcvd,rst_sent = @rst_sent,date = @date,time = @time,mode = @mode,submode = @submode,exchange = @exchange,comment = @comment,name = @name,country = @country,continent = @continent,cq_zone = @cq_zone,itu_zone = @itu_zone,state = @state,qth = @qth,dxcc = @dxcc,prop_mode = @prop_mode,sat_name = @sat_name, soapbox = @soapbox,iota = @iota,sota_ref = @sota_ref,pota_ref = @pota_ref,wwff_ref = @wwff_ref,sig = @sig,sig_info = @sig_info WHERE id = @id";
                try
                {
                    foreach (var uid in ids)
                    {
                        SQLiteCommand insertSQL = new SQLiteCommand(sql, con);
                        insertSQL.Parameters.Add(new SQLiteParameter("@my_callsign", qso.MyCall));
                        insertSQL.Parameters.Add(new SQLiteParameter("@operator", qso.Operator));
                        insertSQL.Parameters.Add(new SQLiteParameter("@my_square", qso.STX));
                        insertSQL.Parameters.Add(new SQLiteParameter("@my_locator", qso.MyLocator));
                        insertSQL.Parameters.Add(new SQLiteParameter("@dx_locator", qso.DXLocator));
                        insertSQL.Parameters.Add(new SQLiteParameter("@frequency", qso.Freq));
                        insertSQL.Parameters.Add(new SQLiteParameter("@band", qso.Band));
                        insertSQL.Parameters.Add(new SQLiteParameter("@dx_callsign", qso.DXCall));
                        insertSQL.Parameters.Add(new SQLiteParameter("@rst_rcvd", qso.RST_RCVD));
                        insertSQL.Parameters.Add(new SQLiteParameter("@rst_sent", qso.RST_SENT));
                        insertSQL.Parameters.Add(new SQLiteParameter("@date", qso.Date));
                        insertSQL.Parameters.Add(new SQLiteParameter("@time", qso.Time));
                        insertSQL.Parameters.Add(new SQLiteParameter("@mode", qso.Mode));
                        insertSQL.Parameters.Add(new SQLiteParameter("@submode", qso.SUBMode));
                        insertSQL.Parameters.Add(new SQLiteParameter("@exchange", qso.SRX));
                        insertSQL.Parameters.Add(new SQLiteParameter("@comment", qso.Comment));
                        insertSQL.Parameters.Add(new SQLiteParameter("@name", qso.Name));
                        insertSQL.Parameters.Add(new SQLiteParameter("@country", qso.Country));
                        insertSQL.Parameters.Add(new SQLiteParameter("@continent", qso.Continent));
                        insertSQL.Parameters.Add(new SQLiteParameter("@cq_zone", qso.CQZone));
                        insertSQL.Parameters.Add(new SQLiteParameter("@itu_zone", qso.ITUZone));
                        insertSQL.Parameters.Add(new SQLiteParameter("@state", qso.State));
                        insertSQL.Parameters.Add(new SQLiteParameter("@qth", qso.Qth));

                        insertSQL.Parameters.Add(new SQLiteParameter("@dxcc", qso.DxccCode > 0 ? (object)qso.DxccCode : DBNull.Value));
                        insertSQL.Parameters.Add(new SQLiteParameter("@prop_mode", qso.PROP_MODE));
                        insertSQL.Parameters.Add(new SQLiteParameter("@sat_name", qso.SAT_NAME));
                        insertSQL.Parameters.Add(new SQLiteParameter("@soapbox", qso.SOAPBOX));
                        AddActivityParams(insertSQL, qso, "@");
                        insertSQL.Parameters.Add(new SQLiteParameter("@id", uid));
                        insertSQL.ExecuteNonQuery();
                    }
                }
                catch (Exception ex)
                {
                    throw new Exception(ex.Message);
                }
            }
            }
        }
        public void Delete(int Id)
        {
            BumpContentVersion();   // this write can change who is in the log or what they are called
            lock (_dbLock)
            {
            if (con != null && con.State == System.Data.ConnectionState.Open)
            {
                // Copy-to-log: a single-QSO delete removes the contact EVERYWHERE. Follow the copy link
                // in BOTH directions — this QSO's copy (child: source_qso_id = Id), and, if this is itself
                // a copy, its original (parent: Id = this row's source_qso_id) plus that original's other
                // copies. Whole-log delete uses a different path (bulk by log_id) and does NOT cascade.
                var ids = new List<long> { Id };
                try
                {
                    long parent = 0;
                    using (var pc = new SQLiteCommand("SELECT source_qso_id FROM qso WHERE Id = ?", con))
                    {
                        pc.Parameters.Add(new SQLiteParameter(null, (long)Id));
                        var o = pc.ExecuteScalar();
                        if (o != null && o != DBNull.Value) parent = Convert.ToInt64(o);
                    }
                    if (parent > 0 && !ids.Contains(parent)) ids.Add(parent);
                    using (var cc = new SQLiteCommand("SELECT Id FROM qso WHERE source_qso_id = ?", con))
                    {
                        cc.Parameters.Add(new SQLiteParameter(null, (long)Id));
                        using (var rdr = cc.ExecuteReader())
                            while (rdr.Read()) { long cid = Convert.ToInt64(rdr["Id"]); if (!ids.Contains(cid)) ids.Add(cid); }
                    }
                    if (parent > 0)
                        using (var sc = new SQLiteCommand("SELECT Id FROM qso WHERE source_qso_id = ?", con))
                        {
                            sc.Parameters.Add(new SQLiteParameter(null, parent));
                            using (var rdr = sc.ExecuteReader())
                                while (rdr.Read()) { long sid = Convert.ToInt64(rdr["Id"]); if (!ids.Contains(sid)) ids.Add(sid); }
                        }
                }
                // Best-effort link lookup; the QSO itself is still deleted below. Written down because
                // the failure leaves the copy in the other log behind, alone, looking deliberate.
                catch (Exception ex) { Log.Swallow(ex); }

                try
                {
                    foreach (var delId in ids)
                        using (var del = new SQLiteCommand("DELETE FROM qso WHERE Id = ?", con))
                        {
                            del.Parameters.Add(new SQLiteParameter(null, delId));
                            del.ExecuteNonQuery();
                        }
                }
                catch (Exception ex)
                {
                    throw new Exception(ex.Message);
                }
            }
            }
        }
        public void DeleteAll()
        {
            BumpContentVersion();   // this write can change who is in the log or what they are called
            lock (_dbLock)
            {
            if (con != null && con.State == System.Data.ConnectionState.Open)
            {
                SQLiteCommand deleteSQL = new SQLiteCommand("DELETE FROM qso", con);
                try
                {
                    deleteSQL.ExecuteNonQuery();
                }
                catch (Exception ex)
                {
                    throw new Exception(ex.Message);
                }
            }
            }
        }

        // Deletes only the QSOs of one log (used by "Import ADIF -> Replace", which replaces just the
        // active log, not every log in the database).
        public void DeleteQSOsForLog(long logId)
        {
            BumpContentVersion();   // this write can change who is in the log or what they are called
            lock (_dbLock)
                using (var cmd = new SQLiteCommand("DELETE FROM qso WHERE log_id = ?", con))
                {
                    cmd.Parameters.Add(new SQLiteParameter(null, logId));
                    cmd.ExecuteNonQuery();
                }
        }
        // The six activity-program columns AND the carried ADIF text, read in one place. Every QSO
        // reader in this file calls this rather than repeating the lines, so a seventh program field
        // can never be added to some readers and forgotten in others.
        //
        // Guarded by HasColumn because a database that has not been through the migration yet - an old
        // backup opened by the Restore button, for instance - has no such columns, and rdr["iota"]
        // would throw rather than return null.
        private static void ReadActivityFields(SQLiteDataReader rdr, QSO q)
        {
            // Each field is found by its column NUMBER, looked up once here instead of the reader
            // finding the name's position again for every call. Nineteen fields, on every QSO of every
            // query in this file, so the saving is nineteen name searches per row.
            //
            // "as string" is kept exactly as it was: it yields null for a NULL column (DBNull is not a
            // string), and null is what "this QSO has no reference" means everywhere downstream. That
            // is NOT the same as the empty string TextAt gives the plain columns, and the difference
            // matters - an empty SIG would be exported as a field with no value.
            int o;
            if ((o = Ordinal(rdr, "iota")) >= 0) q.Iota = rdr.GetValue(o) as string;
            if ((o = Ordinal(rdr, "sota_ref")) >= 0) q.SotaRef = rdr.GetValue(o) as string;
            if ((o = Ordinal(rdr, "pota_ref")) >= 0) q.PotaRef = rdr.GetValue(o) as string;
            if ((o = Ordinal(rdr, "wwff_ref")) >= 0) q.WwffRef = rdr.GetValue(o) as string;
            if ((o = Ordinal(rdr, "sig")) >= 0) q.Sig = rdr.GetValue(o) as string;
            if ((o = Ordinal(rdr, "sig_info")) >= 0) q.SigInfo = rdr.GetValue(o) as string;
            // The award / QSL record and the carried remainder. Read alongside the activity references for
            // the same reason: this is the ONE place every reader goes through, and a field that is
            // written but not read back is a field silently lost on the next export.
            if ((o = Ordinal(rdr, "credit_granted")) >= 0) q.CreditGranted = rdr.GetValue(o) as string;
            if ((o = Ordinal(rdr, "cnty")) >= 0) q.Cnty = rdr.GetValue(o) as string;
            if ((o = Ordinal(rdr, "qsl_via")) >= 0) q.QslVia = rdr.GetValue(o) as string;
            if ((o = Ordinal(rdr, "qsl_rdate")) >= 0) q.QslRDate = rdr.GetValue(o) as string;
            if ((o = Ordinal(rdr, "qsl_sent")) >= 0) q.QslSent = rdr.GetValue(o) as string;
            if ((o = Ordinal(rdr, "contest_id")) >= 0) q.ContestId = rdr.GetValue(o) as string;
            if ((o = Ordinal(rdr, "time_off")) >= 0) q.TimeOff = rdr.GetValue(o) as string;
            if ((o = Ordinal(rdr, "date_off")) >= 0) q.DateOff = rdr.GetValue(o) as string;
            if ((o = Ordinal(rdr, "extra_adif")) >= 0) q.ExtraAdif = rdr.GetValue(o) as string;
            // ADIF QTH (the worked station's town). Read here, in the one place every reader in this file
            // goes through, rather than repeated per query the way state is - a field that is written but
            // read back by only some of the readers is a field that vanishes from whichever screen uses
            // the other query.
            if ((o = Ordinal(rdr, "qth")) >= 0) q.Qth = rdr.GetValue(o) as string;
            if ((o = Ordinal(rdr, "dxcc")) >= 0 && !rdr.IsDBNull(o))
            {
                int code;
                if (int.TryParse(Convert.ToString(rdr.GetValue(o)), out code)) q.DxccCode = code;
            }
        }

        // The write half of ReadActivityFields: binds the same six fields in the same fixed order.
        // An empty box is stored as NULL rather than "", so "has no reference" is one value in the
        // database and not two.
        private static void AddActivityParams(SQLiteCommand cmd, QSO qso, string prefix)
        {
            cmd.Parameters.Add(new SQLiteParameter(prefix + "iota", Blank(qso.Iota)));
            cmd.Parameters.Add(new SQLiteParameter(prefix + "sota_ref", Blank(qso.SotaRef)));
            cmd.Parameters.Add(new SQLiteParameter(prefix + "pota_ref", Blank(qso.PotaRef)));
            cmd.Parameters.Add(new SQLiteParameter(prefix + "wwff_ref", Blank(qso.WwffRef)));
            cmd.Parameters.Add(new SQLiteParameter(prefix + "sig", Blank(qso.Sig)));
            cmd.Parameters.Add(new SQLiteParameter(prefix + "sig_info", Blank(qso.SigInfo)));
        }

        // ── the fields a QSO CARRIES from the file it was imported from ───
        //
        // The award / QSL record plus the raw remainder. Kept as one named group with one binder, so the
        // statements that write a whole QSO (undo-delete, copy-to-log) cannot list a different set of
        // columns from the values they bind - the failure mode that quietly empties a field.
        private const string CarriedColumns = "credit_granted,cnty,qsl_via,qsl_rdate,qsl_sent,contest_id,time_off,date_off,extra_adif";
        // NB @qslrd, not @qrd: @qrd is already the QRZ confirmation date in the undo-delete statement, and
        // two parameters of one name would silently bind the same value to both columns.
        private const string CarriedValues = "@cg,@cnty,@qvia,@qslrd,@qsent,@cid,@toff,@doff,@extra";

        private static void AddCarriedParams(SQLiteCommand cmd, QSO qso)
        {
            cmd.Parameters.Add(new SQLiteParameter("@cg", Blank(qso.CreditGranted)));
            cmd.Parameters.Add(new SQLiteParameter("@cnty", Blank(qso.Cnty)));
            cmd.Parameters.Add(new SQLiteParameter("@qvia", Blank(qso.QslVia)));
            cmd.Parameters.Add(new SQLiteParameter("@qslrd", Blank(qso.QslRDate)));
            cmd.Parameters.Add(new SQLiteParameter("@qsent", Blank(qso.QslSent)));
            cmd.Parameters.Add(new SQLiteParameter("@cid", Blank(qso.ContestId)));
            cmd.Parameters.Add(new SQLiteParameter("@toff", Blank(qso.TimeOff)));
            cmd.Parameters.Add(new SQLiteParameter("@doff", Blank(qso.DateOff)));
            cmd.Parameters.Add(new SQLiteParameter("@extra", Blank(qso.ExtraAdif)));
        }

        // ── FILLING IN THE ENTITY NUMBER FOR QSOs LOGGED BEFORE THERE WAS A COLUMN ────────────────
        //
        // Every QSO in every log, once. A column that is filled for new contacts and empty for the
        // 28,000 already in the log is worse than no column at all: anything reading it would have to
        // ask "is this a QSO with no entity, or a QSO from before we stored one?", and there would be no
        // way to tell. So the answer is worked out for the old ones too, from the callsign and the QSO's
        // own date - the same question the statistics ask every time they open.
        //
        // Only ever writes a row whose dxcc is NULL, so it can be interrupted, run again, and never
        // touches a number that is already there - including one the operator has corrected by hand.
        // Returns how many rows it filled.
        public int BackfillEntityCodes(Func<string, string, int> codeForCall, Action<int, int> progress = null)
        {
            if (codeForCall == null) return 0;
            lock (_dbLock)
            {
                if (con == null || con.State != ConnectionState.Open) return 0;

                var rows = new List<KeyValuePair<long, KeyValuePair<string, string>>>();
                try
                {
                    using (var cmd = new SQLiteCommand(
                        "SELECT Id, dx_callsign, date FROM qso WHERE dxcc IS NULL", con))
                    using (var rdr = cmd.ExecuteReader())
                        while (rdr.Read())
                        {
                            string call = rdr["dx_callsign"] as string;
                            if (string.IsNullOrWhiteSpace(call)) continue;
                            rows.Add(new KeyValuePair<long, KeyValuePair<string, string>>(
                                Convert.ToInt64(rdr["Id"]),
                                new KeyValuePair<string, string>(call, rdr["date"] as string)));
                        }
                }
                catch (Exception swallowed) { Log.Swallow(swallowed); return 0; }

                if (rows.Count == 0) return 0;

                int filled = 0, done = 0;
                try
                {
                    using (var tx = con.BeginTransaction())
                    using (var upd = new SQLiteCommand("UPDATE qso SET dxcc = @c WHERE Id = @id", con, tx))
                    {
                        upd.Parameters.Add(new SQLiteParameter("@c"));
                        upd.Parameters.Add(new SQLiteParameter("@id"));
                        foreach (var row in rows)
                        {
                            done++;
                            int code = 0;
                            try { code = codeForCall(row.Value.Key, row.Value.Value); }
                            catch (Exception swallowed) { Log.Swallow(swallowed); }

                            // 0 is written as 0, not left NULL: "this contact belongs to no entity" is an
                            // answer, and leaving it blank would make the pass do the same work for ever.
                            upd.Parameters[0].Value = code;
                            upd.Parameters[1].Value = row.Key;
                            try { if (upd.ExecuteNonQuery() > 0) filled++; }
                            catch (Exception swallowed) { Log.Swallow(swallowed); }

                            if (progress != null && (done % 500 == 0 || done == rows.Count))
                                progress(done, rows.Count);
                        }
                        tx.Commit();
                    }
                }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
                return filled;
            }
        }

        private static object Blank(string s)
        {
            return string.IsNullOrWhiteSpace(s) ? (object)DBNull.Value : s.Trim();
        }

        // "Does this reader have that column?" - asked 18 times for EVERY QSO read (see
        // ReadActivityFields), because an old database that has not been through the migration has no
        // such column and rdr["iota"] would throw rather than return null.
        //
        // It used to answer by scanning all ~60 column names with a case-insensitive compare, every
        // time. That is 18 x 60 string comparisons per row: about 30 MILLION of them for a 28,454-QSO
        // log, and it is where opening that log spent its ELEVEN SECONDS - measured with the timing this
        // path now writes to the log, 10.9 s of an 11.2 s switch, while the same query in raw SQL takes
        // under a second.
        //
        // The names are the same for the whole reader, so they are collected once per reader and then
        // answered from a table. Held in a [ThreadStatic] pair rather than a dictionary keyed by reader:
        // readers are used and disposed one at a time on the thread doing the reading, so this both
        // holds nothing after the read and needs no locking.
        //
        // What is collected is the column's NUMBER, not merely its name, because reading a field by
        // name - rdr["dx_callsign"] - makes the reader find that name's position again on every single
        // call, the same linear scan this was built to end. With the number in hand the field is taken
        // straight out of the row.
        [ThreadStatic] private static SQLiteDataReader _columnsFor;
        [ThreadStatic] private static Dictionary<string, int> _columnOrdinals;

        private static void EnsureColumnMap(SQLiteDataReader rdr)
        {
            if (ReferenceEquals(_columnsFor, rdr)) return;
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < rdr.FieldCount; i++) map[rdr.GetName(i)] = i;
            _columnOrdinals = map;
            _columnsFor = rdr;
        }

        private static bool HasColumn(SQLiteDataReader rdr, string name)
        {
            EnsureColumnMap(rdr);
            return _columnOrdinals.ContainsKey(name);
        }

        // The column's position in this reader, or -1 if the database has no such column (an old file
        // that predates it - the Restore button can open one). Callers must check for -1 exactly as
        // they used to check HasColumn.
        private static int Ordinal(SQLiteDataReader rdr, string name)
        {
            EnsureColumnMap(rdr);
            int i;
            return _columnOrdinals.TryGetValue(name, out i) ? i : -1;
        }

        // The text of a column that may be absent or NULL. Empty string for NULL, which is what
        // rdr[name].ToString() gave before - DBNull's own ToString() is "" - so nothing downstream
        // sees a value it did not see before.
        private static string TextAt(SQLiteDataReader rdr, int ordinal)
        {
            if (ordinal < 0 || rdr.IsDBNull(ordinal)) return string.Empty;
            return rdr.GetValue(ordinal).ToString();
        }

        // EVERY COLUMN EXCEPT THE CARRIED ADIF TEXT.
        //
        // extra_adif holds, for each imported QSO, the ADIF fields HolyLogger has no column of its own
        // for - kept word for word so an export gives them back. Measured on this operator's database it
        // is 58 MB of the 62 MB in one log: 93% of everything a log read fetches, for something no screen
        // ever shows. Only the ADIF export reads it, and it fetches it for itself (FillCarriedAdif).
        //
        // Left out by NAME rather than by listing the wanted columns, so a column added to the table in
        // future is read without anyone having to remember this list. ReadActivityFields asks HasColumn
        // before touching it, so a reader without the column simply leaves QSO.ExtraAdif null - and
        // Update never writes that column, so a QSO saved without it in hand keeps its text.
        private string ColumnsExceptCarriedAdif()
        {
            if (_columnsNoAdif != null) return _columnsNoAdif;

            var names = new List<string>();
            using (var cmd = new SQLiteCommand("PRAGMA table_info(qso)", con))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                {
                    string name = r["name"].ToString();
                    if (!string.Equals(name, "extra_adif", StringComparison.OrdinalIgnoreCase))
                        names.Add("\"" + name + "\"");
                }

            _columnsNoAdif = names.Count == 0 ? "*" : string.Join(",", names.ToArray());
            return _columnsNoAdif;
        }
        private string _columnsNoAdif;

        // Fills in the carried ADIF text for QSOs that are about to be exported - the one place that
        // needs it. One query for the lot, matched back by Id.
        public void FillCarriedAdif(IEnumerable<QSO> qsos)
        {
            if (qsos == null) return;
            var byId = new Dictionary<long, QSO>();
            foreach (QSO q in qsos)
                if (q != null && q.id > 0 && string.IsNullOrEmpty(q.ExtraAdif)) byId[q.id] = q;
            if (byId.Count == 0) return;

            lock (_dbLock)
            {
                if (con == null || con.State != System.Data.ConnectionState.Open) return;
                using (var cmd = new SQLiteCommand(
                    "SELECT Id, extra_adif FROM qso WHERE extra_adif IS NOT NULL AND extra_adif <> ''", con))
                using (var rdr = cmd.ExecuteReader())
                    while (rdr.Read())
                    {
                        long id = Convert.ToInt64(rdr[0]);
                        QSO q;
                        if (byId.TryGetValue(id, out q)) q.ExtraAdif = rdr[1] as string;
                    }
            }
        }

        // Loads only the QSOs stored under one log (what the log table shows for the active log).
        public ObservableCollection<QSO> GetQSOsForLog(long logId, Action<int> progressCallback = null)
        {
            lock (_dbLock)
            {
                ObservableCollection<QSO> qso_list = new ObservableCollection<QSO>();
                int totalCount;
                using (var c = new SQLiteCommand("SELECT count(*) FROM qso WHERE log_id = ?", con))
                {
                    c.Parameters.Add(new SQLiteParameter(null, logId));
                    totalCount = Convert.ToInt32(c.ExecuteScalar());
                }
                int processedCount = 0;
                int lastReportedProgress = -1;
                using (SQLiteCommand cmd = new SQLiteCommand(
                    "SELECT " + ColumnsExceptCarriedAdif() + " FROM qso WHERE log_id = ? ORDER BY date DESC, time DESC", con))
                {
                    cmd.Parameters.Add(new SQLiteParameter(null, logId));
                    using (SQLiteDataReader rdr = cmd.ExecuteReader())
                    {
                        // THE COLUMN NUMBERS, FOUND ONCE FOR THE WHOLE QUERY.
                        //
                        // Every field used to be fetched by name - rdr["dx_callsign"] - and each of those
                        // makes the reader search its ~60 column names for that one. Forty-odd fields per
                        // QSO, 28,454 QSOs in this operator's log: over a million searches to hand over
                        // the same columns in the same order every time. A reader's columns cannot change
                        // while it is open, so their positions are taken here and used for every row.
                        //
                        // Ordinal() answers -1 for a column the database does not have, so an old file
                        // opened by Restore reads what it has and leaves the rest alone, instead of
                        // throwing the way rdr["state"] would.
                        int cId = Ordinal(rdr, "Id"), cComment = Ordinal(rdr, "comment"),
                            cDxCall = Ordinal(rdr, "dx_callsign"), cMode = Ordinal(rdr, "mode"),
                            cSubmode = Ordinal(rdr, "submode"), cExchange = Ordinal(rdr, "exchange"),
                            cFreq = Ordinal(rdr, "frequency"), cBand = Ordinal(rdr, "band"),
                            cMyCall = Ordinal(rdr, "my_callsign"), cOperator = Ordinal(rdr, "operator"),
                            cMySquare = Ordinal(rdr, "my_square"), cMyLocator = Ordinal(rdr, "my_locator"),
                            cDxLocator = Ordinal(rdr, "dx_locator"), cRstRcvd = Ordinal(rdr, "rst_rcvd"),
                            cRstSent = Ordinal(rdr, "rst_sent"), cName = Ordinal(rdr, "name"),
                            cCountry = Ordinal(rdr, "country"), cContinent = Ordinal(rdr, "continent"),
                            cCqZone = Ordinal(rdr, "cq_zone"), cItuZone = Ordinal(rdr, "itu_zone"),
                            cState = Ordinal(rdr, "state"), cTime = Ordinal(rdr, "time"),
                            cDate = Ordinal(rdr, "date"), cPropMode = Ordinal(rdr, "prop_mode"),
                            cSatName = Ordinal(rdr, "sat_name"), cSoapbox = Ordinal(rdr, "soapbox"),
                            cEqslStatus = Ordinal(rdr, "eqsl_status"), cLotwStatus = Ordinal(rdr, "lotw_status"),
                            cQrzStatus = Ordinal(rdr, "qrz_status"), cClublogStatus = Ordinal(rdr, "clublog_status"),
                            cLotwRcvd = Ordinal(rdr, "lotw_qsl_rcvd"), cLotwRDate = Ordinal(rdr, "lotw_qsl_rdate"),
                            cLotwDeleted = Ordinal(rdr, "lotw_deleted_entity"),
                            cQrzRcvd = Ordinal(rdr, "qrz_qsl_rcvd"), cQrzRDate = Ordinal(rdr, "qrz_qsl_rdate"),
                            cQrzDeleted = Ordinal(rdr, "qrz_deleted_entity"),
                            cEqslRcvd = Ordinal(rdr, "eqsl_qsl_rcvd"), cEqslRDate = Ordinal(rdr, "eqsl_qsl_rdate"),
                            cEqslDeleted = Ordinal(rdr, "eqsl_deleted_entity"),
                            cClublogRcvd = Ordinal(rdr, "clublog_qsl_rcvd"), cClublogRDate = Ordinal(rdr, "clublog_qsl_rdate"),
                            cClublogDeleted = Ordinal(rdr, "clublog_deleted_entity"),
                            cPaperRcvd = Ordinal(rdr, "paper_qsl_rcvd");

                        while (rdr.Read())
                        {
                            QSO q = new QSO();
                            if (cId >= 0) q.id = Convert.ToInt32(rdr.GetValue(cId));
                            q.Comment = TextAt(rdr, cComment);
                            q.DXCall = TextAt(rdr, cDxCall);
                            q.Mode = TextAt(rdr, cMode);
                            q.SUBMode = TextAt(rdr, cSubmode);
                            q.SRX = TextAt(rdr, cExchange);
                            q.Freq = TextAt(rdr, cFreq);
                            q.Band = TextAt(rdr, cBand);
                            q.MyCall = TextAt(rdr, cMyCall);
                            q.Operator = TextAt(rdr, cOperator);
                            q.STX = TextAt(rdr, cMySquare);
                            q.MyLocator = TextAt(rdr, cMyLocator);
                            q.DXLocator = TextAt(rdr, cDxLocator);
                            q.RST_RCVD = TextAt(rdr, cRstRcvd);
                            q.RST_SENT = TextAt(rdr, cRstSent);
                            q.Name = TextAt(rdr, cName);
                            q.Country = TextAt(rdr, cCountry);
                            q.Continent = TextAt(rdr, cContinent);
                            q.CQZone = TextAt(rdr, cCqZone);
                            q.ITUZone = TextAt(rdr, cItuZone);
                            q.State = TextAt(rdr, cState);
                            q.Time = TextAt(rdr, cTime);
                            q.Date = TextAt(rdr, cDate);
                            q.PROP_MODE = TextAt(rdr, cPropMode);
                            q.SAT_NAME = TextAt(rdr, cSatName);
                            q.SOAPBOX = TextAt(rdr, cSoapbox);
                            ReadActivityFields(rdr, q);
                            if (cEqslStatus >= 0 && !rdr.IsDBNull(cEqslStatus)) q.EqslStatus = Convert.ToInt32(rdr.GetValue(cEqslStatus));
                            if (cLotwStatus >= 0 && !rdr.IsDBNull(cLotwStatus)) q.LotwStatus = Convert.ToInt32(rdr.GetValue(cLotwStatus));
                            if (cQrzStatus >= 0 && !rdr.IsDBNull(cQrzStatus)) q.QrzStatus = Convert.ToInt32(rdr.GetValue(cQrzStatus));
                            if (cLotwRcvd >= 0 && !rdr.IsDBNull(cLotwRcvd)) q.LotwQslRcvd = Convert.ToInt32(rdr.GetValue(cLotwRcvd));
                            if (cLotwRDate >= 0 && !rdr.IsDBNull(cLotwRDate)) q.LotwQslRDate = rdr.GetValue(cLotwRDate).ToString();
                            if (cLotwDeleted >= 0 && !rdr.IsDBNull(cLotwDeleted)) q.LotwDeletedEntity = Convert.ToInt32(rdr.GetValue(cLotwDeleted));
                            if (cQrzRcvd >= 0 && !rdr.IsDBNull(cQrzRcvd)) q.QrzQslRcvd = Convert.ToInt32(rdr.GetValue(cQrzRcvd));
                            if (cQrzRDate >= 0 && !rdr.IsDBNull(cQrzRDate)) q.QrzQslRDate = rdr.GetValue(cQrzRDate).ToString();
                            if (cQrzDeleted >= 0 && !rdr.IsDBNull(cQrzDeleted)) q.QrzDeletedEntity = Convert.ToInt32(rdr.GetValue(cQrzDeleted));
                            if (cEqslRcvd >= 0 && !rdr.IsDBNull(cEqslRcvd)) q.EqslQslRcvd = Convert.ToInt32(rdr.GetValue(cEqslRcvd));
                            if (cEqslRDate >= 0 && !rdr.IsDBNull(cEqslRDate)) q.EqslQslRDate = rdr.GetValue(cEqslRDate).ToString();
                            if (cEqslDeleted >= 0 && !rdr.IsDBNull(cEqslDeleted)) q.EqslDeletedEntity = Convert.ToInt32(rdr.GetValue(cEqslDeleted));
                            if (cClublogRcvd >= 0 && !rdr.IsDBNull(cClublogRcvd)) q.ClublogQslRcvd = Convert.ToInt32(rdr.GetValue(cClublogRcvd));
                            if (cClublogRDate >= 0 && !rdr.IsDBNull(cClublogRDate)) q.ClublogQslRDate = rdr.GetValue(cClublogRDate).ToString();
                            if (cClublogDeleted >= 0 && !rdr.IsDBNull(cClublogDeleted)) q.ClublogDeletedEntity = Convert.ToInt32(rdr.GetValue(cClublogDeleted));
                            if (cPaperRcvd >= 0 && !rdr.IsDBNull(cPaperRcvd)) q.PaperQslRcvd = Convert.ToInt32(rdr.GetValue(cPaperRcvd));
                            if (cClublogStatus >= 0 && !rdr.IsDBNull(cClublogStatus)) q.ClublogStatus = Convert.ToInt32(rdr.GetValue(cClublogStatus));
                            q.StandartizeQSO();
                            qso_list.Add(q);

                            processedCount++;
                            if (totalCount > 0)
                            {
                                int progress = (int)Math.Floor((double)processedCount * 100 / totalCount);
                                if (progress > lastReportedProgress)
                                {
                                    lastReportedProgress = progress;
                                    progressCallback?.Invoke(progress);
                                }
                            }
                        }
                    }
                }
                return qso_list;
            }
        }
        // Returns QSOs in a specific log whose worked-callsign CONTAINS the given fragment (case-
        // insensitive), newest first, capped. Used to show "also worked in the copy-target log" rows in
        // the live callsign filter — reference only, so a light cap keeps it snappy.
        public List<QSO> GetQsosWithCallsignInLog(long logId, string dxCallsignFragment)
        {
            var list = new List<QSO>();
            if (string.IsNullOrWhiteSpace(dxCallsignFragment)) return list;
            lock (_dbLock)
            {
                if (con == null || con.State != ConnectionState.Open) return list;
                using (var cmd = new SQLiteCommand(
                    "SELECT * FROM qso WHERE log_id = @lid AND dx_callsign LIKE @c COLLATE NOCASE ORDER BY date DESC, time DESC LIMIT 1000", con))
                {
                    cmd.Parameters.Add(new SQLiteParameter("@lid", logId));
                    cmd.Parameters.Add(new SQLiteParameter("@c", "%" + dxCallsignFragment.Trim() + "%"));
                    using (var rdr = cmd.ExecuteReader())
                        while (rdr.Read())
                        {
                            QSO q = new QSO();
                            if (rdr["Id"] != null) q.id = int.Parse(rdr["Id"].ToString());
                            if (rdr["comment"] != null) q.Comment = rdr["comment"].ToString();
                            if (rdr["dx_callsign"] != null) q.DXCall = rdr["dx_callsign"].ToString();
                            if (rdr["mode"] != null) q.Mode = rdr["mode"].ToString();
                            if (rdr["submode"] != null) q.SUBMode = rdr["submode"].ToString();
                            if (rdr["exchange"] != null) q.SRX = rdr["exchange"].ToString();
                            if (rdr["frequency"] != null) q.Freq = rdr["frequency"].ToString();
                            if (rdr["band"] != null) q.Band = rdr["band"].ToString();
                            if (rdr["my_callsign"] != null) q.MyCall = rdr["my_callsign"].ToString();
                            if (rdr["operator"] != null) q.Operator = rdr["operator"].ToString();
                            if (rdr["my_square"] != null) q.STX = rdr["my_square"].ToString();
                            if (rdr["my_locator"] != null) q.MyLocator = rdr["my_locator"].ToString();
                            if (rdr["dx_locator"] != null) q.DXLocator = rdr["dx_locator"].ToString();
                            if (rdr["rst_rcvd"] != null) q.RST_RCVD = rdr["rst_rcvd"].ToString();
                            if (rdr["rst_sent"] != null) q.RST_SENT = rdr["rst_sent"].ToString();
                            if (rdr["name"] != null) q.Name = rdr["name"].ToString();
                            if (rdr["country"] != null) q.Country = rdr["country"].ToString();
                            if (rdr["continent"] != null) q.Continent = rdr["continent"].ToString();
                            if (rdr["cq_zone"] != null) q.CQZone = rdr["cq_zone"].ToString();
                            if (rdr["itu_zone"] != null) q.ITUZone = rdr["itu_zone"].ToString();
                        if (rdr["state"] != null) q.State = rdr["state"].ToString();
                    if (rdr["state"] != null) q.State = rdr["state"].ToString();
                            if (rdr["time"] != null) q.Time = rdr["time"].ToString();
                            if (rdr["date"] != null) q.Date = rdr["date"].ToString();
                            if (rdr["prop_mode"] != null) q.PROP_MODE = rdr["prop_mode"].ToString();
                            if (rdr["sat_name"] != null) q.SAT_NAME = rdr["sat_name"].ToString();
                            if (rdr["soapbox"] != null) q.SOAPBOX = rdr["soapbox"].ToString();
                            ReadActivityFields(rdr, q);
                            q.StandartizeQSO();
                            list.Add(q);
                        }
                }
            }
            return list;
        }

        public ObservableCollection<QSO> GetTopQSOs(int i)
        {
            lock (_dbLock)
            {
            CultureInfo enUS = new CultureInfo("en-US");
            ObservableCollection<QSO> qso_list = new ObservableCollection<QSO>();
            string stm = "SELECT * FROM qso ORDER BY Id DESC LIMIT " + i;
            using (SQLiteCommand cmd = new SQLiteCommand(stm, con))
            {
                using (SQLiteDataReader rdr = cmd.ExecuteReader())
                {
                    while (rdr.Read())
                    {
                        QSO q = new QSO();
                        if (rdr["Id"] != null) q.id = int.Parse(rdr["Id"].ToString());
                        if (rdr["comment"] != null) q.Comment = (string)rdr["comment"];
                        if (rdr["dx_callsign"] != null) q.DXCall = (string)rdr["dx_callsign"];
                        if (rdr["mode"] != null) q.Mode = (string)rdr["mode"];
                        if (rdr["submode"] != null) q.SUBMode = rdr["submode"].ToString();
                        if (rdr["exchange"] != null) q.SRX = (string)rdr["exchange"];
                        if (rdr["frequency"] != null) q.Freq = (string)rdr["frequency"];
                        if (rdr["band"] != null) q.Band = (string)rdr["band"];
                        if (rdr["my_callsign"] != null) q.MyCall = (string)rdr["my_callsign"];
                        if (rdr["operator"] != null) q.Operator = rdr["operator"].ToString();
                        if (rdr["my_square"] != null) q.STX = (string)rdr["my_square"];
                        if (rdr["my_locator"] != null) q.MyLocator = rdr["my_locator"].ToString();
                        if (rdr["dx_locator"] != null) q.DXLocator = rdr["dx_locator"].ToString();
                        if (rdr["rst_rcvd"] != null) q.RST_RCVD = (string)rdr["rst_rcvd"];
                        if (rdr["rst_sent"] != null) q.RST_SENT = (string)rdr["rst_sent"];
                        if (rdr["name"] != null) q.Name = (string)rdr["name"];
                        if (rdr["country"] != null) q.Country = rdr["country"].ToString();
                        if (rdr["continent"] != null) q.Continent = rdr["continent"].ToString();
                    if (rdr["cq_zone"] != null) q.CQZone = rdr["cq_zone"].ToString();
                    if (rdr["itu_zone"] != null) q.ITUZone = rdr["itu_zone"].ToString();
                    if (rdr["state"] != null) q.State = rdr["state"].ToString();
                        if (rdr["cq_zone"] != null) q.CQZone = rdr["cq_zone"].ToString();
                        if (rdr["itu_zone"] != null) q.ITUZone = rdr["itu_zone"].ToString();
                        if (rdr["state"] != null) q.State = rdr["state"].ToString();
                    if (rdr["state"] != null) q.State = rdr["state"].ToString();
                        if (rdr["time"] != null) q.Time = (string)rdr["time"];
                        if (rdr["date"] != null) q.Date = (string)rdr["date"];
                        if (rdr["prop_mode"] != null) q.PROP_MODE = rdr["prop_mode"].ToString();
                        if (rdr["sat_name"] != null) q.SAT_NAME = rdr["sat_name"].ToString();
                        if (rdr["soapbox"] != null) q.SOAPBOX = rdr["soapbox"].ToString();
                        ReadActivityFields(rdr, q);
                        if (rdr["eqsl_status"] != null && rdr["eqsl_status"] != DBNull.Value) q.EqslStatus = Convert.ToInt32(rdr["eqsl_status"]);
                        if (rdr["lotw_status"] != null && rdr["lotw_status"] != DBNull.Value) q.LotwStatus = Convert.ToInt32(rdr["lotw_status"]);
                        if (rdr["qrz_status"] != null && rdr["qrz_status"] != DBNull.Value) q.QrzStatus = Convert.ToInt32(rdr["qrz_status"]);
                        if (rdr["lotw_qsl_rcvd"] != null && rdr["lotw_qsl_rcvd"] != DBNull.Value) q.LotwQslRcvd = Convert.ToInt32(rdr["lotw_qsl_rcvd"]);
                        if (rdr["lotw_qsl_rdate"] != null && rdr["lotw_qsl_rdate"] != DBNull.Value) q.LotwQslRDate = rdr["lotw_qsl_rdate"].ToString();
                        if (rdr["lotw_deleted_entity"] != null && rdr["lotw_deleted_entity"] != DBNull.Value) q.LotwDeletedEntity = Convert.ToInt32(rdr["lotw_deleted_entity"]);
                        if (rdr["qrz_qsl_rcvd"] != null && rdr["qrz_qsl_rcvd"] != DBNull.Value) q.QrzQslRcvd = Convert.ToInt32(rdr["qrz_qsl_rcvd"]);
                        if (rdr["qrz_qsl_rdate"] != null && rdr["qrz_qsl_rdate"] != DBNull.Value) q.QrzQslRDate = rdr["qrz_qsl_rdate"].ToString();
                        if (rdr["qrz_deleted_entity"] != null && rdr["qrz_deleted_entity"] != DBNull.Value) q.QrzDeletedEntity = Convert.ToInt32(rdr["qrz_deleted_entity"]);
                        if (rdr["eqsl_qsl_rcvd"] != null && rdr["eqsl_qsl_rcvd"] != DBNull.Value) q.EqslQslRcvd = Convert.ToInt32(rdr["eqsl_qsl_rcvd"]);
                        if (rdr["eqsl_qsl_rdate"] != null && rdr["eqsl_qsl_rdate"] != DBNull.Value) q.EqslQslRDate = rdr["eqsl_qsl_rdate"].ToString();
                        if (rdr["eqsl_deleted_entity"] != null && rdr["eqsl_deleted_entity"] != DBNull.Value) q.EqslDeletedEntity = Convert.ToInt32(rdr["eqsl_deleted_entity"]);
                        if (rdr["clublog_qsl_rcvd"] != null && rdr["clublog_qsl_rcvd"] != DBNull.Value) q.ClublogQslRcvd = Convert.ToInt32(rdr["clublog_qsl_rcvd"]);
                        if (rdr["clublog_qsl_rdate"] != null && rdr["clublog_qsl_rdate"] != DBNull.Value) q.ClublogQslRDate = rdr["clublog_qsl_rdate"].ToString();
                        if (rdr["clublog_deleted_entity"] != null && rdr["clublog_deleted_entity"] != DBNull.Value) q.ClublogDeletedEntity = Convert.ToInt32(rdr["clublog_deleted_entity"]);
                        if (rdr["paper_qsl_rcvd"] != null && rdr["paper_qsl_rcvd"] != DBNull.Value) q.PaperQslRcvd = Convert.ToInt32(rdr["paper_qsl_rcvd"]);
                        if (rdr["clublog_status"] != null && rdr["clublog_status"] != DBNull.Value) q.ClublogStatus = Convert.ToInt32(rdr["clublog_status"]);
                        q.StandartizeQSO();
                        qso_list.Add(q);
                    }
                }
            }
            return qso_list;
            }
        }
        public int GetQsoCount()
        {
            lock (_dbLock)
            {
            string stm = "SELECT count(Id) FROM qso";
            using (SQLiteCommand cmd = new SQLiteCommand(stm, con))
            {
                cmd.CommandType = CommandType.Text;
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
            }
        }
        public int GetGridCount()
        {
            lock (_dbLock)
            {
            string stm = "SELECT count(distinct exchange) FROM qso where dx_callsign like '4X%' or dx_callsign like '4Z%'";
            using (SQLiteCommand cmd = new SQLiteCommand(stm, con))
            {
                cmd.CommandType = CommandType.Text;
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
            }
        }
        public int GetDXCCCount()
        {
            lock (_dbLock)
            {
            string stm = "SELECT count(distinct country) FROM qso";
            using (SQLiteCommand cmd = new SQLiteCommand(stm, con))
            {
                cmd.CommandType = CommandType.Text;
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
            }
        }

        // Per-log versions of the status-bar counts (the counts follow the active log).
        public int GetQsoCountForLog(long logId)
        {
            lock (_dbLock)
                using (var cmd = new SQLiteCommand("SELECT count(Id) FROM qso WHERE log_id = ?", con))
                { cmd.Parameters.Add(new SQLiteParameter(null, logId)); return Convert.ToInt32(cmd.ExecuteScalar()); }
        }
        public int GetGridCountForLog(long logId)
        {
            lock (_dbLock)
                using (var cmd = new SQLiteCommand("SELECT count(distinct exchange) FROM qso WHERE (dx_callsign like '4X%' or dx_callsign like '4Z%') AND log_id = ?", con))
                { cmd.Parameters.Add(new SQLiteParameter(null, logId)); return Convert.ToInt32(cmd.ExecuteScalar()); }
        }
        public int GetDXCCCountForLog(long logId)
        {
            lock (_dbLock)
                using (var cmd = new SQLiteCommand("SELECT count(distinct country) FROM qso WHERE log_id = ?", con))
                { cmd.Parameters.Add(new SQLiteParameter(null, logId)); return Convert.ToInt32(cmd.ExecuteScalar()); }
        }

        public ObservableCollection<RadioEvent> GetRadioEvents()
        {
            lock (_dbLock)
            {
            CultureInfo enUS = new CultureInfo("en-US");
            ObservableCollection<RadioEvent> radioEvent_list = new ObservableCollection<RadioEvent>();
            string stm = "SELECT * FROM radio_events ORDER BY Id ASC";
            using (SQLiteCommand cmd = new SQLiteCommand(stm, con))
            {
                using (SQLiteDataReader rdr = cmd.ExecuteReader())
                {
                    while (rdr.Read())
                    {
                        RadioEvent q = new RadioEvent();
                        if (rdr["Id"] != null) q.id = int.Parse(rdr["Id"].ToString());
                        if (rdr["name"] != null) q.Name = rdr["name"].ToString();
                        if (rdr["description"] != null) q.Description = rdr["description"].ToString();
                        if (rdr["is_categories"] != null) q.IsCategories = int.Parse(rdr["is_categories"].ToString()) == 1;
                        radioEvent_list.Add(q);
                    }
                }
            }
            return radioEvent_list;
            }
        }

        public ObservableCollection<GenericItem> GetTableData(string tableName, int eventId=1)
        {
            lock (_dbLock)
            {
            CultureInfo enUS = new CultureInfo("en-US");
            ObservableCollection<GenericItem> category_list = new ObservableCollection<GenericItem>();
            string stm = "SELECT * FROM " + tableName + " WHERE event_id = @eventId ORDER BY Id ASC";
            using (SQLiteCommand cmd = new SQLiteCommand(stm, con))
            {
                cmd.Parameters.Add(new SQLiteParameter("@eventId", eventId));
                using (SQLiteDataReader rdr = cmd.ExecuteReader())
                {
                    while (rdr.Read())
                    {
                        GenericItem q = new GenericItem();
                        if (rdr["Id"] != null) q.id = int.Parse(rdr["Id"].ToString());
                        if (rdr["name"] != null) q.Name = rdr["name"].ToString();
                        if (rdr["description"] != null) q.Description = rdr["description"].ToString();
                        if (rdr["event_id"] != null) q.EventId = int.Parse(rdr["event_id"].ToString());
                        category_list.Add(q);
                    }
                }
            }
            return category_list;
            }
        }

        // What each confirmation service remembers about each log. Keyed on the pair, so a log that has
        // never been checked simply has no rows and reports nothing rather than another log's figures.
        private void EnsureLogStateTable()
        {
            try
            {
                using (var cmd = new SQLiteCommand(
                    "CREATE TABLE IF NOT EXISTS [log_state] (" +
                    "[log_id] INTEGER NOT NULL, " +
                    "[key] nvarchar(60) NOT NULL COLLATE NOCASE, " +
                    "[value] TEXT NULL, " +
                    "PRIMARY KEY ([log_id], [key]))", con))
                    cmd.ExecuteNonQuery();
            }
            catch (Exception ex) { Log.Swallow(ex); }
        }

        private void AddColToTable(string tableName, string colName, string definition)
        {

            string stm = $"SELECT count(*) FROM pragma_table_info('{tableName}') WHERE name = '{colName}'";
            SQLiteCommand cmd = new SQLiteCommand(stm, con);
            try
            {
                int colCount = Convert.ToInt32(cmd.ExecuteScalar());
                if (colCount == 0)
                {
                    stm = $"ALTER TABLE {tableName} ADD COLUMN [" + colName + "] " + definition;
                    cmd = new SQLiteCommand(stm, con);
                    try
                    {
                        cmd.ExecuteNonQuery();
                        SchemaHasChanged = true;
                    }
                    catch (Exception ex)
                    {
                        throw new Exception(ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                throw new Exception(ex.Message);
            }

        }

        // True if the given column already exists on the table.
        private bool ColumnExists(string table, string col)
        {
            using (var cmd = new SQLiteCommand($"SELECT count(*) FROM pragma_table_info('{table}') WHERE name = '{col}'", con))
                return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
        }

        // The first time a build with the multi-log feature runs on an existing database (qso table
        // present but not yet migrated to logs), copy the whole DB file to a timestamped backup so the
        // user can be restored if anything ever looked wrong. Best-effort: never blocks startup.
        private void BackupBeforeLogsMigration()
        {
            try
            {
                if (!TableExists("qso")) return;          // brand-new DB: nothing to back up
                if (ColumnExists("qso", "log_id")) return; // already migrated in a previous run
                string backupPath = dbPath + ".pre-logs-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".bak";
                if (File.Exists(backupPath)) return;
                con.Close();
                try { File.Copy(dbPath, backupPath, false); } catch (System.Exception swallowed) { Log.Swallow(swallowed); }
                con.Open();
            }
            catch (Exception ex)
            {
                Log.Warn("The one-time backup taken before the multi-log migration FAILED: " +
                         ex.GetType().Name + ": " + ex.Message);
                if (con.State != System.Data.ConnectionState.Open)
                {
                    try { con.Open(); } catch (System.Exception swallowed) { Log.Swallow(swallowed); }
                }
            }
        }

        // Creates the logs table (name is unique, case-insensitive) if it does not exist yet.
        private void EnsureLogsTable()
        {
            using (var cmd = new SQLiteCommand(
                "CREATE TABLE IF NOT EXISTS [logs] (" +
                "[Id] INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL, " +
                "[name] nvarchar(100) NOT NULL COLLATE NOCASE, " +
                "[event_type] nvarchar(100) NULL COLLATE NOCASE, " +
                "[created_utc] nvarchar(40) NULL);", con))
                cmd.ExecuteNonQuery();
            using (var idx = new SQLiteCommand(
                "CREATE UNIQUE INDEX IF NOT EXISTS idx_logs_name ON logs(name COLLATE NOCASE);", con))
                idx.ExecuteNonQuery();
        }

        // ---- Logs API ------------------------------------------------------------------------

        public int GetLogCount()
        {
            lock (_dbLock)
                using (var cmd = new SQLiteCommand("SELECT count(*) FROM logs", con))
                    return Convert.ToInt32(cmd.ExecuteScalar());
        }

        public bool LogNameExists(string name)
        {
            lock (_dbLock)
                using (var cmd = new SQLiteCommand("SELECT count(*) FROM logs WHERE name = ? COLLATE NOCASE", con))
                {
                    cmd.Parameters.Add(new SQLiteParameter(null, name ?? string.Empty));
                    return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
                }
        }

        // True if the name is free to use. excludeId lets a log keep its own name during a rename.
        public bool LogNameAvailable(string name, long excludeId = 0)
        {
            lock (_dbLock)
                using (var cmd = new SQLiteCommand("SELECT count(*) FROM logs WHERE name = ? COLLATE NOCASE AND Id <> ?", con))
                {
                    cmd.Parameters.Add(new SQLiteParameter(null, name ?? string.Empty));
                    cmd.Parameters.Add(new SQLiteParameter(null, excludeId));
                    return Convert.ToInt32(cmd.ExecuteScalar()) == 0;
                }
        }

        // Inserts a new log and returns its Id. event_type is the contest name, or "" for a normal log.
        public long CreateLog(string name, string eventType)
        {
            lock (_dbLock)
            {
                using (var cmd = new SQLiteCommand("INSERT INTO logs (name, event_type, created_utc) VALUES (?,?,?)", con))
                {
                    cmd.Parameters.Add(new SQLiteParameter(null, name));
                    cmd.Parameters.Add(new SQLiteParameter(null, eventType ?? string.Empty));
                    cmd.Parameters.Add(new SQLiteParameter(null, DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss")));
                    cmd.ExecuteNonQuery();
                }
                using (var idcmd = new SQLiteCommand("SELECT last_insert_rowid()", con))
                    return Convert.ToInt64(idcmd.ExecuteScalar());
            }
        }

        // Full create: also stores the log's identity (station callsign + operator) and an optional
        // copy-target (another log this one's new QSOs are mirrored into). The identity callsign is
        // stored in its base form (4Z5SL/M -> 4Z5SL): stroke variants are one identity.
        public long CreateLog(string name, string eventType, string callsign, string opr, long? copyTargetLogId)
        {
            lock (_dbLock)
            {
                using (var cmd = new SQLiteCommand("INSERT INTO logs (name, event_type, created_utc, log_callsign, log_operator, copy_target_log_id) VALUES (?,?,?,?,?,?)", con))
                {
                    cmd.Parameters.Add(new SQLiteParameter(null, name));
                    cmd.Parameters.Add(new SQLiteParameter(null, eventType ?? string.Empty));
                    cmd.Parameters.Add(new SQLiteParameter(null, DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss")));
                    cmd.Parameters.Add(new SQLiteParameter(null, (object)CallsignIdentity.Base(callsign)));
                    cmd.Parameters.Add(new SQLiteParameter(null, (object)(opr ?? string.Empty)));
                    cmd.Parameters.Add(new SQLiteParameter(null, copyTargetLogId.HasValue ? (object)copyTargetLogId.Value : DBNull.Value));
                    cmd.ExecuteNonQuery();
                }
                using (var idcmd = new SQLiteCommand("SELECT last_insert_rowid()", con))
                    return Convert.ToInt64(idcmd.ExecuteScalar());
            }
        }

        // Sets a log's copy-target only (Log Manager "Copy settings"). Pass null to stop copying. The
        // log's identity is never touched here — identity is set once and then frozen (see SetLogIdentity).
        public void SetCopyTarget(long id, long? copyTargetLogId)
        {
            lock (_dbLock)
                using (var cmd = new SQLiteCommand("UPDATE logs SET copy_target_log_id = ? WHERE Id = ?", con))
                {
                    cmd.Parameters.Add(new SQLiteParameter(null, copyTargetLogId.HasValue ? (object)copyTargetLogId.Value : DBNull.Value));
                    cmd.Parameters.Add(new SQLiteParameter(null, id));
                    cmd.ExecuteNonQuery();
                }
        }

        // True when a log has BOTH a station callsign and an operator identity.
        public bool LogHasIdentity(long logId)
        {
            GetLogIdentity(logId, out string c, out string o);
            return !string.IsNullOrWhiteSpace(c) && !string.IsNullOrWhiteSpace(o);
        }

        // Sets a log's identity ONCE. A log's identity is permanent: if it already has one this is a no-op.
        // The identity callsign is stored in its base form (4Z5SL/M -> 4Z5SL): stroke variants are one identity.
        public void SetLogIdentity(long logId, string callsign, string opr)
        {
            lock (_dbLock)
            {
                using (var chk = new SQLiteCommand("SELECT log_callsign, log_operator FROM logs WHERE Id = ?", con))
                {
                    chk.Parameters.Add(new SQLiteParameter(null, logId));
                    using (var rdr = chk.ExecuteReader())
                        if (rdr.Read())
                        {
                            string c = rdr["log_callsign"] == DBNull.Value ? string.Empty : rdr["log_callsign"].ToString();
                            string o = rdr["log_operator"] == DBNull.Value ? string.Empty : rdr["log_operator"].ToString();
                            if (!string.IsNullOrWhiteSpace(c) && !string.IsNullOrWhiteSpace(o)) return;   // already set -> frozen
                        }
                }
                using (var cmd = new SQLiteCommand("UPDATE logs SET log_callsign = ?, log_operator = ? WHERE Id = ?", con))
                {
                    cmd.Parameters.Add(new SQLiteParameter(null, (object)CallsignIdentity.Base(callsign)));
                    cmd.Parameters.Add(new SQLiteParameter(null, (object)((opr ?? string.Empty).Trim())));
                    cmd.Parameters.Add(new SQLiteParameter(null, logId));
                    cmd.ExecuteNonQuery();
                }
            }
        }

        // The distinct (station callsign, operator) pairs actually used in a log, most-frequent first —
        // to pre-fill / offer choices when assigning that log's identity.
        public List<LogIdentityCandidate> GetStationIdentitiesInLog(long logId)
        {
            var list = new List<LogIdentityCandidate>();
            lock (_dbLock)
            {
                if (con == null || con.State != ConnectionState.Open) return list;
                using (var cmd = new SQLiteCommand(
                    "SELECT my_callsign, operator, count(*) AS cnt FROM qso WHERE log_id = @lid GROUP BY my_callsign, operator ORDER BY cnt DESC", con))
                {
                    cmd.Parameters.Add(new SQLiteParameter("@lid", logId));
                    using (var rdr = cmd.ExecuteReader())
                        while (rdr.Read())
                        {
                            string c = rdr["my_callsign"] == DBNull.Value ? string.Empty : rdr["my_callsign"].ToString();
                            string o = rdr["operator"] == DBNull.Value ? string.Empty : rdr["operator"].ToString();
                            if (string.IsNullOrWhiteSpace(c) && string.IsNullOrWhiteSpace(o)) continue;
                            list.Add(new LogIdentityCandidate
                            {
                                Callsign = c.Trim(),
                                Operator = o.Trim(),
                                Count = rdr["cnt"] == DBNull.Value ? 0 : Convert.ToInt32(rdr["cnt"])
                            });
                        }
                }
            }

            // Collapse stroke variants into one identity (4Z5SL + 4Z5SL/M -> 4Z5SL): the candidate
            // offered/stored is the base form, with the variants' counts combined.
            var merged = new List<LogIdentityCandidate>();
            foreach (var cand in list)
            {
                string baseCall = CallsignIdentity.Base(cand.Callsign);
                var hit = merged.FirstOrDefault(m =>
                    string.Equals(m.Callsign, baseCall, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(m.Operator, cand.Operator, StringComparison.OrdinalIgnoreCase));
                if (hit == null)
                    merged.Add(new LogIdentityCandidate { Callsign = baseCall, Operator = cand.Operator, Count = cand.Count });
                else
                    hit.Count += cand.Count;
            }
            return merged.OrderByDescending(m => m.Count).ToList();
        }

        // A log's copy-target, or null if it doesn't copy. Used on QSO insert and for the live indicator.
        public long? GetCopyTargetLogId(long logId)
        {
            lock (_dbLock)
                using (var cmd = new SQLiteCommand("SELECT copy_target_log_id FROM logs WHERE Id = ?", con))
                {
                    cmd.Parameters.Add(new SQLiteParameter(null, logId));
                    var o = cmd.ExecuteScalar();
                    return o == null || o == DBNull.Value ? (long?)null : Convert.ToInt64(o);
                }
        }

        // A log's (station callsign, operator) identity. Empty strings if unset.
        public void GetLogIdentity(long logId, out string callsign, out string opr)
        {
            callsign = string.Empty; opr = string.Empty;
            lock (_dbLock)
                using (var cmd = new SQLiteCommand("SELECT log_callsign, log_operator FROM logs WHERE Id = ?", con))
                {
                    cmd.Parameters.Add(new SQLiteParameter(null, logId));
                    using (var rdr = cmd.ExecuteReader())
                        if (rdr.Read())
                        {
                            callsign = rdr["log_callsign"] == DBNull.Value ? string.Empty : rdr["log_callsign"].ToString();
                            opr = rdr["log_operator"] == DBNull.Value ? string.Empty : rdr["log_operator"].ToString();
                        }
                }
        }

        // Turns off copying for every log that was copying INTO targetId (used when that log is deleted).
        // Returns how many logs were affected so the caller can tell the user.
        public int ClearCopyTargetsPointingTo(long targetId)
        {
            lock (_dbLock)
                using (var cmd = new SQLiteCommand("UPDATE logs SET copy_target_log_id = NULL WHERE copy_target_log_id = ?", con))
                {
                    cmd.Parameters.Add(new SQLiteParameter(null, targetId));
                    return cmd.ExecuteNonQuery();
                }
        }

        // Renames a log. Returns false (no change) if the new name is already used by another log.
        public bool RenameLog(long id, string newName)
        {
            lock (_dbLock)
            {
                using (var chk = new SQLiteCommand("SELECT count(*) FROM logs WHERE name = ? COLLATE NOCASE AND Id <> ?", con))
                {
                    chk.Parameters.Add(new SQLiteParameter(null, newName ?? string.Empty));
                    chk.Parameters.Add(new SQLiteParameter(null, id));
                    if (Convert.ToInt32(chk.ExecuteScalar()) > 0) return false;
                }
                using (var cmd = new SQLiteCommand("UPDATE logs SET name = ? WHERE Id = ?", con))
                {
                    cmd.Parameters.Add(new SQLiteParameter(null, newName));
                    cmd.Parameters.Add(new SQLiteParameter(null, id));
                    cmd.ExecuteNonQuery();
                }
                return true;
            }
        }

        // Deletes a log AND all QSOs stored under it. Caller must confirm with the user first.
        // NOTE: copies this log made into OTHER logs are intentionally NOT deleted (they survive) —
        // whole-log delete is archiving, not per-QSO correction. Any OTHER log that was copying INTO this
        // one has its copy-target cleared so it doesn't dangle.
        public void DeleteLog(long id)
        {
            lock (_dbLock)
            {
                using (var cc = new SQLiteCommand("UPDATE logs SET copy_target_log_id = NULL WHERE copy_target_log_id = ?", con))
                {
                    cc.Parameters.Add(new SQLiteParameter(null, id));
                    cc.ExecuteNonQuery();
                }
                using (var dq = new SQLiteCommand("DELETE FROM qso WHERE log_id = ?", con))
                {
                    dq.Parameters.Add(new SQLiteParameter(null, id));
                    dq.ExecuteNonQuery();
                }
                using (var dl = new SQLiteCommand("DELETE FROM logs WHERE Id = ?", con))
                {
                    dl.Parameters.Add(new SQLiteParameter(null, id));
                    dl.ExecuteNonQuery();
                }
            }
        }

        public string GetLogName(long id)
        {
            lock (_dbLock)
                using (var cmd = new SQLiteCommand("SELECT name FROM logs WHERE Id = ?", con))
                {
                    cmd.Parameters.Add(new SQLiteParameter(null, id));
                    var o = cmd.ExecuteScalar();
                    return o == null || o == DBNull.Value ? null : o.ToString();
                }
        }

        public string GetLogEventType(long id)
        {
            lock (_dbLock)
                using (var cmd = new SQLiteCommand("SELECT event_type FROM logs WHERE Id = ?", con))
                {
                    cmd.Parameters.Add(new SQLiteParameter(null, id));
                    var o = cmd.ExecuteScalar();
                    return o == null || o == DBNull.Value ? string.Empty : o.ToString();
                }
        }

        // Assigns every QSO that has no log yet to the given log (used once during first-run migration).
        public int AssignUnassignedToLog(long logId)
        {
            BumpContentVersion();   // this write can change who is in the log or what they are called
            lock (_dbLock)
                using (var cmd = new SQLiteCommand("UPDATE qso SET log_id = ? WHERE log_id IS NULL", con))
                {
                    cmd.Parameters.Add(new SQLiteParameter(null, logId));
                    return cmd.ExecuteNonQuery();
                }
        }

        public int CountUnassignedQSOs()
        {
            lock (_dbLock)
                using (var cmd = new SQLiteCommand("SELECT count(*) FROM qso WHERE log_id IS NULL", con))
                    return Convert.ToInt32(cmd.ExecuteScalar());
        }

        // Returns all logs with computed stats (QSO count, first/last QSO date) for the View Logs window.
        public List<LogInfo> GetLogs()
        {
            var list = new List<LogInfo>();
            lock (_dbLock)
            {
                string stm =
                    "SELECT l.Id, l.name, l.event_type, l.created_utc, l.copy_target_log_id, l.log_callsign, l.log_operator, " +
                    "(SELECT count(*) FROM qso q WHERE q.log_id = l.Id) AS qso_count, " +
                    "(SELECT min(q.date) FROM qso q WHERE q.log_id = l.Id) AS start_date, " +
                    "(SELECT max(q.date) FROM qso q WHERE q.log_id = l.Id) AS end_date " +
                    "FROM logs l ORDER BY l.Id ASC";
                using (var cmd = new SQLiteCommand(stm, con))
                using (var rdr = cmd.ExecuteReader())
                    while (rdr.Read())
                        list.Add(new LogInfo
                        {
                            Id = Convert.ToInt64(rdr["Id"]),
                            Name = rdr["name"]?.ToString() ?? string.Empty,
                            EventType = rdr["event_type"] == DBNull.Value ? string.Empty : rdr["event_type"].ToString(),
                            QsoCount = rdr["qso_count"] == DBNull.Value ? 0 : Convert.ToInt32(rdr["qso_count"]),
                            StartDate = rdr["start_date"] == DBNull.Value ? string.Empty : rdr["start_date"].ToString(),
                            EndDate = rdr["end_date"] == DBNull.Value ? string.Empty : rdr["end_date"].ToString(),
                            CopyTargetLogId = rdr["copy_target_log_id"] == DBNull.Value ? (long?)null : Convert.ToInt64(rdr["copy_target_log_id"]),
                            Callsign = rdr["log_callsign"] == DBNull.Value ? string.Empty : rdr["log_callsign"].ToString(),
                            Operator = rdr["log_operator"] == DBNull.Value ? string.Empty : rdr["log_operator"].ToString(),
                        });
            }
            return list;
        }

        // Adds the eqsl_status column to an existing qso table the first time the user runs a build
        // that has the eQSL queue feature. Existing rows are back-filled to 1 ("already handled") so
        // that upgrading does NOT suddenly queue the user's entire historical log for eQSL upload.
        // Only QSOs logged after the upgrade (inserted with the default 0) become pending.
        private void AddEqslStatusColumn()
        {
            string check = "SELECT count(*) FROM pragma_table_info('qso') WHERE name = 'eqsl_status'";
            using (var cmd = new SQLiteCommand(check, con))
            {
                int colCount = Convert.ToInt32(cmd.ExecuteScalar());
                if (colCount == 0)
                {
                    using (var alter = new SQLiteCommand("ALTER TABLE qso ADD COLUMN [eqsl_status] INTEGER NOT NULL DEFAULT 0", con))
                        alter.ExecuteNonQuery();
                    using (var backfill = new SQLiteCommand("UPDATE qso SET eqsl_status = 1", con))
                        backfill.ExecuteNonQuery();
                    SchemaHasChanged = true;
                }
            }
        }

        // Adds the qrz_status and qrz_logid columns to an existing qso table the first time the user
        // runs a build that has the QRZ Logbook real-time push feature. Existing rows are back-filled
        // to qrz_status = 1 ("already handled") so upgrading does NOT suddenly queue the user's whole
        // historical log for upload to QRZ. Only QSOs logged after the upgrade (inserted with the
        // default 0) become pending.
        private void AddQrzColumns()
        {
            string check = "SELECT count(*) FROM pragma_table_info('qso') WHERE name = 'qrz_status'";
            using (var cmd = new SQLiteCommand(check, con))
            {
                int colCount = Convert.ToInt32(cmd.ExecuteScalar());
                if (colCount == 0)
                {
                    using (var alter = new SQLiteCommand("ALTER TABLE qso ADD COLUMN [qrz_status] INTEGER NOT NULL DEFAULT 0", con))
                        alter.ExecuteNonQuery();
                    using (var alter2 = new SQLiteCommand("ALTER TABLE qso ADD COLUMN [qrz_logid] nvarchar(50) NULL", con))
                        alter2.ExecuteNonQuery();
                    using (var backfill = new SQLiteCommand("UPDATE qso SET qrz_status = 1", con))
                        backfill.ExecuteNonQuery();
                    SchemaHasChanged = true;
                }
            }
        }

        // Adds the clublog_status column to an existing qso table the first time the user runs a build
        // with the Club Log queue feature. Existing rows are back-filled to clublog_status = 1
        // ("already handled") so upgrading does NOT suddenly queue the whole historical log; only QSOs
        // logged after the upgrade (inserted with the default 0) become pending. The backlog can still
        // be pushed in bulk via Tools -> Club Log (Upload Full Log), which Club Log de-duplicates.
        private void AddClublogColumn()
        {
            string check = "SELECT count(*) FROM pragma_table_info('qso') WHERE name = 'clublog_status'";
            using (var cmd = new SQLiteCommand(check, con))
            {
                int colCount = Convert.ToInt32(cmd.ExecuteScalar());
                if (colCount == 0)
                {
                    using (var alter = new SQLiteCommand("ALTER TABLE qso ADD COLUMN [clublog_status] INTEGER NOT NULL DEFAULT 0", con))
                        alter.ExecuteNonQuery();
                    using (var backfill = new SQLiteCommand("UPDATE qso SET clublog_status = 1", con))
                        backfill.ExecuteNonQuery();
                    SchemaHasChanged = true;
                }
            }
        }

        // Returns the QSOs still waiting to be uploaded to QRZ Logbook (status 0), oldest first so they
        // are pushed in the order they were logged. Unlike eQSL there is no per-callsign opt-in table:
        // the single account API key plus the feature toggle govern whether these are actually sent.
        public List<QSO> GetPendingQrzQsos()
        {
            lock (_dbLock)
            {
            var list = new List<QSO>();
            if (con == null || con.State != ConnectionState.Open) return list;
            string stm = "SELECT *, (SELECT name FROM logs WHERE logs.Id = qso.log_id) AS log_name FROM qso WHERE qrz_status = 0 ORDER BY date ASC, time ASC, Id ASC";
            using (SQLiteCommand cmd = new SQLiteCommand(stm, con))
            using (SQLiteDataReader rdr = cmd.ExecuteReader())
            {
                while (rdr.Read())
                {
                    QSO q = new QSO();
                    if (rdr["Id"] != null) q.id = int.Parse(rdr["Id"].ToString());
                    if (rdr["comment"] != null) q.Comment = rdr["comment"].ToString();
                    if (rdr["dx_callsign"] != null) q.DXCall = rdr["dx_callsign"].ToString();
                    if (rdr["mode"] != null) q.Mode = rdr["mode"].ToString();
                    if (rdr["submode"] != null) q.SUBMode = rdr["submode"].ToString();
                    if (rdr["exchange"] != null) q.SRX = rdr["exchange"].ToString();
                    if (rdr["frequency"] != null) q.Freq = rdr["frequency"].ToString();
                    if (rdr["band"] != null) q.Band = rdr["band"].ToString();
                    if (rdr["my_callsign"] != null) q.MyCall = rdr["my_callsign"].ToString();
                    if (rdr["operator"] != null) q.Operator = rdr["operator"].ToString();
                    if (rdr["my_square"] != null) q.STX = rdr["my_square"].ToString();
                    if (rdr["my_locator"] != null) q.MyLocator = rdr["my_locator"].ToString();
                    if (rdr["dx_locator"] != null) q.DXLocator = rdr["dx_locator"].ToString();
                    if (rdr["rst_rcvd"] != null) q.RST_RCVD = rdr["rst_rcvd"].ToString();
                    if (rdr["rst_sent"] != null) q.RST_SENT = rdr["rst_sent"].ToString();
                    if (rdr["name"] != null) q.Name = rdr["name"].ToString();
                    if (rdr["country"] != null) q.Country = rdr["country"].ToString();
                    if (rdr["continent"] != null) q.Continent = rdr["continent"].ToString();
                    if (rdr["cq_zone"] != null) q.CQZone = rdr["cq_zone"].ToString();
                    if (rdr["itu_zone"] != null) q.ITUZone = rdr["itu_zone"].ToString();
                    if (rdr["state"] != null) q.State = rdr["state"].ToString();
                    if (rdr["time"] != null) q.Time = rdr["time"].ToString();
                    if (rdr["date"] != null) q.Date = rdr["date"].ToString();
                    if (rdr["prop_mode"] != null) q.PROP_MODE = rdr["prop_mode"].ToString();
                    if (rdr["sat_name"] != null) q.SAT_NAME = rdr["sat_name"].ToString();
                    if (rdr["soapbox"] != null) q.SOAPBOX = rdr["soapbox"].ToString();
                    ReadActivityFields(rdr, q);
                    if (rdr["log_name"] != DBNull.Value) q.LogName = rdr["log_name"].ToString();
                    q.QrzStatus = 0;
                    list.Add(q);
                }
            }
            return list;
            }
        }

        // Number of QSOs still waiting to be uploaded to QRZ Logbook.
        public int GetPendingQrzCount()
        {
            lock (_dbLock)
            {
            if (con == null || con.State != ConnectionState.Open) return 0;
            using (SQLiteCommand cmd = new SQLiteCommand("SELECT count(Id) FROM qso WHERE qrz_status = 0", con))
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
        }

        // Updates the QRZ Logbook upload state of a single QSO (0 pending, 1 uploaded, 2 rejected) and,
        // on success, stores the LOGID transaction id QRZ returned next to the record.
        public void SetQrzStatus(int id, int status, string logId = null)
        {
            lock (_dbLock)
            {
            if (con == null || con.State != ConnectionState.Open) return;
            using (SQLiteCommand cmd = new SQLiteCommand("UPDATE qso SET qrz_status = @s, qrz_logid = @logid WHERE Id = @id", con))
            {
                cmd.Parameters.Add(new SQLiteParameter("@s", status));
                cmd.Parameters.Add(new SQLiteParameter("@logid", (object)logId ?? DBNull.Value));
                cmd.Parameters.Add(new SQLiteParameter("@id", id));
                cmd.ExecuteNonQuery();
            }
            }
        }

        // Returns the QSOs still waiting to be uploaded to Club Log (status 0), oldest first so they
        // are sent in the order they were logged. Like QRZ there is no per-callsign opt-in table: the
        // single account (e-mail + password) plus the feature toggle govern whether these are sent.
        public List<QSO> GetPendingClublogQsos()
        {
            lock (_dbLock)
            {
            var list = new List<QSO>();
            if (con == null || con.State != ConnectionState.Open) return list;
            string stm = "SELECT *, (SELECT name FROM logs WHERE logs.Id = qso.log_id) AS log_name FROM qso WHERE clublog_status = 0 ORDER BY date ASC, time ASC, Id ASC";
            using (SQLiteCommand cmd = new SQLiteCommand(stm, con))
            using (SQLiteDataReader rdr = cmd.ExecuteReader())
            {
                while (rdr.Read())
                {
                    QSO q = new QSO();
                    if (rdr["Id"] != null) q.id = int.Parse(rdr["Id"].ToString());
                    if (rdr["comment"] != null) q.Comment = rdr["comment"].ToString();
                    if (rdr["dx_callsign"] != null) q.DXCall = rdr["dx_callsign"].ToString();
                    if (rdr["mode"] != null) q.Mode = rdr["mode"].ToString();
                    if (rdr["submode"] != null) q.SUBMode = rdr["submode"].ToString();
                    if (rdr["exchange"] != null) q.SRX = rdr["exchange"].ToString();
                    if (rdr["frequency"] != null) q.Freq = rdr["frequency"].ToString();
                    if (rdr["band"] != null) q.Band = rdr["band"].ToString();
                    if (rdr["my_callsign"] != null) q.MyCall = rdr["my_callsign"].ToString();
                    if (rdr["operator"] != null) q.Operator = rdr["operator"].ToString();
                    if (rdr["my_square"] != null) q.STX = rdr["my_square"].ToString();
                    if (rdr["my_locator"] != null) q.MyLocator = rdr["my_locator"].ToString();
                    if (rdr["dx_locator"] != null) q.DXLocator = rdr["dx_locator"].ToString();
                    if (rdr["rst_rcvd"] != null) q.RST_RCVD = rdr["rst_rcvd"].ToString();
                    if (rdr["rst_sent"] != null) q.RST_SENT = rdr["rst_sent"].ToString();
                    if (rdr["name"] != null) q.Name = rdr["name"].ToString();
                    if (rdr["country"] != null) q.Country = rdr["country"].ToString();
                    if (rdr["continent"] != null) q.Continent = rdr["continent"].ToString();
                    if (rdr["cq_zone"] != null) q.CQZone = rdr["cq_zone"].ToString();
                    if (rdr["itu_zone"] != null) q.ITUZone = rdr["itu_zone"].ToString();
                    if (rdr["state"] != null) q.State = rdr["state"].ToString();
                    if (rdr["time"] != null) q.Time = rdr["time"].ToString();
                    if (rdr["date"] != null) q.Date = rdr["date"].ToString();
                    if (rdr["prop_mode"] != null) q.PROP_MODE = rdr["prop_mode"].ToString();
                    if (rdr["sat_name"] != null) q.SAT_NAME = rdr["sat_name"].ToString();
                    if (rdr["soapbox"] != null) q.SOAPBOX = rdr["soapbox"].ToString();
                    ReadActivityFields(rdr, q);
                    if (rdr["log_name"] != DBNull.Value) q.LogName = rdr["log_name"].ToString();
                    q.ClublogStatus = 0;
                    list.Add(q);
                }
            }
            return list;
            }
        }

        // Number of QSOs still waiting to be uploaded to Club Log.
        public int GetPendingClublogCount()
        {
            lock (_dbLock)
            {
            if (con == null || con.State != ConnectionState.Open) return 0;
            using (SQLiteCommand cmd = new SQLiteCommand("SELECT count(Id) FROM qso WHERE clublog_status = 0", con))
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
        }

        // Updates the Club Log upload state of a single QSO (0 pending, 1 uploaded, 2 rejected).
        public void SetClublogStatus(int id, int status)
        {
            lock (_dbLock)
            {
            if (con == null || con.State != ConnectionState.Open) return;
            using (SQLiteCommand cmd = new SQLiteCommand("UPDATE qso SET clublog_status = @s WHERE Id = @id", con))
            {
                cmd.Parameters.Add(new SQLiteParameter("@s", status));
                cmd.Parameters.Add(new SQLiteParameter("@id", id));
                cmd.ExecuteNonQuery();
            }
            }
        }

        // Returns the QSOs still waiting to be uploaded to eQSL (status 0), oldest first so they
        // are sent in the order they were logged.
        public List<QSO> GetPendingEqslQsos()
        {
            lock (_dbLock)
            {
            var list = new List<QSO>();
            if (con == null || con.State != ConnectionState.Open) return list;
            // Not-yet-sent QSOs whose station callsign is in the eQSL accounts table (the opt-in list).
            // QSOs under a callsign that isn't in the table are intentionally left out (the user chose
            // not to upload them).
            string stm = "SELECT *, (SELECT name FROM logs WHERE logs.Id = qso.log_id) AS log_name FROM qso WHERE eqsl_status = 0 AND my_callsign IN (SELECT callsign FROM eqsl_accounts) ORDER BY date ASC, time ASC, Id ASC";
            using (SQLiteCommand cmd = new SQLiteCommand(stm, con))
            using (SQLiteDataReader rdr = cmd.ExecuteReader())
            {
                while (rdr.Read())
                {
                    QSO q = new QSO();
                    if (rdr["Id"] != null) q.id = int.Parse(rdr["Id"].ToString());
                    if (rdr["comment"] != null) q.Comment = rdr["comment"].ToString();
                    if (rdr["dx_callsign"] != null) q.DXCall = rdr["dx_callsign"].ToString();
                    if (rdr["mode"] != null) q.Mode = rdr["mode"].ToString();
                    if (rdr["submode"] != null) q.SUBMode = rdr["submode"].ToString();
                    if (rdr["exchange"] != null) q.SRX = rdr["exchange"].ToString();
                    if (rdr["frequency"] != null) q.Freq = rdr["frequency"].ToString();
                    if (rdr["band"] != null) q.Band = rdr["band"].ToString();
                    if (rdr["my_callsign"] != null) q.MyCall = rdr["my_callsign"].ToString();
                    if (rdr["operator"] != null) q.Operator = rdr["operator"].ToString();
                    if (rdr["my_square"] != null) q.STX = rdr["my_square"].ToString();
                    if (rdr["my_locator"] != null) q.MyLocator = rdr["my_locator"].ToString();
                    if (rdr["dx_locator"] != null) q.DXLocator = rdr["dx_locator"].ToString();
                    if (rdr["rst_rcvd"] != null) q.RST_RCVD = rdr["rst_rcvd"].ToString();
                    if (rdr["rst_sent"] != null) q.RST_SENT = rdr["rst_sent"].ToString();
                    if (rdr["name"] != null) q.Name = rdr["name"].ToString();
                    if (rdr["country"] != null) q.Country = rdr["country"].ToString();
                    if (rdr["continent"] != null) q.Continent = rdr["continent"].ToString();
                    if (rdr["cq_zone"] != null) q.CQZone = rdr["cq_zone"].ToString();
                    if (rdr["itu_zone"] != null) q.ITUZone = rdr["itu_zone"].ToString();
                    if (rdr["state"] != null) q.State = rdr["state"].ToString();
                    if (rdr["time"] != null) q.Time = rdr["time"].ToString();
                    if (rdr["date"] != null) q.Date = rdr["date"].ToString();
                    if (rdr["prop_mode"] != null) q.PROP_MODE = rdr["prop_mode"].ToString();
                    if (rdr["sat_name"] != null) q.SAT_NAME = rdr["sat_name"].ToString();
                    if (rdr["soapbox"] != null) q.SOAPBOX = rdr["soapbox"].ToString();
                    ReadActivityFields(rdr, q);
                    if (rdr["log_name"] != DBNull.Value) q.LogName = rdr["log_name"].ToString();
                    q.EqslStatus = 0;
                    list.Add(q);
                }
            }
            return list;
            }
        }

        // Number of QSOs still waiting to be uploaded to eQSL.
        public int GetPendingEqslCount()
        {
            lock (_dbLock)
            {
            if (con == null || con.State != ConnectionState.Open) return 0;
            using (SQLiteCommand cmd = new SQLiteCommand("SELECT count(Id) FROM qso WHERE eqsl_status = 0 AND my_callsign IN (SELECT callsign FROM eqsl_accounts)", con))
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
        }

        // Updates the eQSL upload state of a single QSO (0 pending, 1 sent, 2 rejected).
        public void SetEqslStatus(int id, int status)
        {
            lock (_dbLock)
            {
            if (con == null || con.State != ConnectionState.Open) return;
            using (SQLiteCommand cmd = new SQLiteCommand("UPDATE qso SET eqsl_status = @s WHERE Id = @id", con))
            {
                cmd.Parameters.Add(new SQLiteParameter("@s", status));
                cmd.Parameters.Add(new SQLiteParameter("@id", id));
                cmd.ExecuteNonQuery();
            }
            }
        }

        // ---- eQSL accounts (one per station callsign) -------------------------------------------

        // True if a station callsign appears in the eQSL accounts table. The table is the user's
        // explicit opt-in list: a callsign that is NOT in the table means "do not upload my QSOs
        // under this callsign to eQSL" (so no "!" badge, no upload).
        public bool IsCallsignInEqslTable(string callsign)
        {
            lock (_dbLock)
            {
            if (string.IsNullOrWhiteSpace(callsign) || con == null || con.State != ConnectionState.Open) return false;
            using (var cmd = new SQLiteCommand("SELECT count(*) FROM eqsl_accounts WHERE callsign = @c COLLATE NOCASE", con))
            {
                cmd.Parameters.Add(new SQLiteParameter("@c", callsign.Trim()));
                return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
            }
            }
        }

        // Creates the index that backs the eQSL queue lookups. The badge/queue queries filter on
        // eqsl_status (and my_callsign), so without this they scan the whole qso table on every
        // refresh. Idempotent (IF NOT EXISTS), so it is effectively a one-time cost.
        private void EnsureEqslIndexes()
        {
            try
            {
                using (var cmd = new SQLiteCommand("CREATE INDEX IF NOT EXISTS idx_qso_eqsl_status ON qso(eqsl_status, my_callsign)", con))
                    cmd.ExecuteNonQuery();
            }
            catch (Exception ex) { Log.Swallow(ex); }   // an index is an optimization only; never block startup on it
        }

        // Index that backs the QRZ Logbook pending-queue lookups (filter on qrz_status). Idempotent.
        private void EnsureQrzIndexes()
        {
            try
            {
                using (var cmd = new SQLiteCommand("CREATE INDEX IF NOT EXISTS idx_qso_qrz_status ON qso(qrz_status)", con))
                    cmd.ExecuteNonQuery();
            }
            catch (Exception ex) { Log.Swallow(ex); }   // an index is an optimization only; never block startup on it
        }

        // Index that backs the Club Log pending-queue lookups (filter on clublog_status). Idempotent.
        private void EnsureClublogIndex()
        {
            try
            {
                using (var cmd = new SQLiteCommand("CREATE INDEX IF NOT EXISTS idx_qso_clublog_status ON qso(clublog_status)", con))
                    cmd.ExecuteNonQuery();
            }
            catch (Exception ex) { Log.Swallow(ex); }   // an index is an optimization only; never block startup on it
        }

        // Index that backs every per-log query (load, counts, dup checks, copy-dedupe — the qso table
        // is always filtered by log_id). Without it each of those scans the whole table, which is felt
        // on every log switch once the table holds thousands of QSOs. Idempotent.
        private void EnsureLogIdIndex()
        {
            try
            {
                using (var cmd = new SQLiteCommand("CREATE INDEX IF NOT EXISTS idx_qso_log_id ON qso(log_id)", con))
                    cmd.ExecuteNonQuery();

                // THE COPY-TO-LOG LINK, and the reason the Log Fixer appeared to hang. Update() asks
                // "which rows are copies of this one?" for EVERY QSO it writes, and without an index
                // that question is a full scan of the qso table. Measured on this operator's 37,837-QSO
                // database: 100 of those lookups took 13 seconds, so fixing 3,292 QSOs spent about
                // SEVEN MINUTES in that one query before a single row was written. With the index the
                // same 3,292 lookups take 0.72 seconds, and building it costs 165 ms, once.
                using (var cmd = new SQLiteCommand(
                    "CREATE INDEX IF NOT EXISTS idx_qso_source_qso_id ON qso(source_qso_id)", con))
                    cmd.ExecuteNonQuery();

                // ── THE THREE QUESTIONS THIS PROGRAM ASKS MOST ────────────────────────────────
                //
                // Measured on this operator's 37,984-QSO database, before and after, with
                // EXPLAIN QUERY PLAN read for each:
                //
                //   open the log (28,454 QSOs)   1043 ms -> 444 ms   the sort disappears
                //   every QSO with one callsign    96 ms ->   0 ms   was a full table scan
                //   count confirmed by a service   95 ms ->   2 ms   was a full table scan
                //
                // Building all of them costs 925 ms, once. A thousand inserts inside a transaction
                // still take 17 ms with them in place, so the write side is not measurably worse.
                //
                // 1. The log is ALWAYS read as "this log, newest first". log_id alone finds the rows
                //    but leaves SQLite to sort them - "USE TEMP B-TREE FOR ORDER BY", the whole sort
                //    built in memory on every log open. With the date and time in the index the rows
                //    come out already in order.
                using (var cmd = new SQLiteCommand(
                    "CREATE INDEX IF NOT EXISTS idx_qso_log_date ON qso(log_id, date DESC, time DESC)", con))
                    cmd.ExecuteNonQuery();

                // 2. "What else have I worked this station on?" - the dup check, the unmatched-
                //    confirmations report, the callsign history. COLLATE NOCASE must be on the INDEX
                //    too: the queries compare that way, and an index with a different collation is
                //    simply not used.
                using (var cmd = new SQLiteCommand(
                    "CREATE INDEX IF NOT EXISTS idx_qso_dx_callsign ON qso(dx_callsign COLLATE NOCASE)", con))
                    cmd.ExecuteNonQuery();

                // 3. "How many are confirmed?" - asked once per service every time Statistics opens or
                //    a check finishes. PARTIAL indexes (WHERE ... = 1): they hold only the confirmed
                //    rows, so five of them together are far smaller than one ordinary index, and each
                //    is used only by the query whose WHERE clause matches its own - which is exactly
                //    the query it exists for.
                foreach (string service in new[] { "lotw", "qrz", "eqsl", "clublog", "paper" })
                    using (var cmd = new SQLiteCommand(
                        "CREATE INDEX IF NOT EXISTS idx_qso_" + service + "_rcvd ON qso(log_id) "
                        + "WHERE " + service + "_qsl_rcvd = 1", con))
                        cmd.ExecuteNonQuery();
            }
            catch (Exception ex) { Log.Swallow(ex); }   // an index is an optimization only; never block startup on it
        }

        // Adds the lotw_status column the first time the user runs a build that has the LoTW upload
        // feature. Existing rows are back-filled to 1 ("already handled") so upgrading does NOT
        // suddenly queue the user's whole historical log for upload to LoTW.
        private void AddLotwColumns()
        {
            string check = "SELECT count(*) FROM pragma_table_info('qso') WHERE name = 'lotw_status'";
            using (var cmd = new SQLiteCommand(check, con))
            {
                int colCount = Convert.ToInt32(cmd.ExecuteScalar());
                if (colCount == 0)
                {
                    using (var alter = new SQLiteCommand("ALTER TABLE qso ADD COLUMN [lotw_status] INTEGER NOT NULL DEFAULT 0", con))
                        alter.ExecuteNonQuery();
                    using (var backfill = new SQLiteCommand("UPDATE qso SET lotw_status = 1", con))
                        backfill.ExecuteNonQuery();
                    SchemaHasChanged = true;
                }
            }
        }

        // LoTW CONFIRMATION columns, separate from lotw_status.
        //
        // lotw_status says whether WE uploaded the QSO; these say whether the other station confirmed
        // it. A QSO can sit uploaded for years and never be confirmed, so the two cannot share a field.
        // New rows default to 0 = unconfirmed, and stay that way until a LoTW download says otherwise -
        // no back-fill, because "we don't know yet" is the honest starting state here.
        private void AddLotwConfirmationColumns()
        {
            try
            {
                using (var cmd = new SQLiteCommand(
                    "SELECT count(*) FROM pragma_table_info('qso') WHERE name = 'lotw_qsl_rcvd'", con))
                {
                    if (Convert.ToInt32(cmd.ExecuteScalar()) == 0)
                    {
                        using (var alter = new SQLiteCommand(
                            "ALTER TABLE qso ADD COLUMN [lotw_qsl_rcvd] INTEGER NOT NULL DEFAULT 0", con))
                            alter.ExecuteNonQuery();
                        SchemaHasChanged = true;
                    }
                }
                using (var cmd = new SQLiteCommand(
                    "SELECT count(*) FROM pragma_table_info('qso') WHERE name = 'lotw_qsl_rdate'", con))
                {
                    if (Convert.ToInt32(cmd.ExecuteScalar()) == 0)
                    {
                        using (var alter = new SQLiteCommand(
                            "ALTER TABLE qso ADD COLUMN [lotw_qsl_rdate] nvarchar(20) NULL", con))
                            alter.ExecuteNonQuery();
                        SchemaHasChanged = true;
                    }
                }
                using (var cmd = new SQLiteCommand(
                    "SELECT count(*) FROM pragma_table_info('qso') WHERE name = 'lotw_deleted_entity'", con))
                {
                    if (Convert.ToInt32(cmd.ExecuteScalar()) == 0)
                    {
                        using (var alter = new SQLiteCommand(
                            "ALTER TABLE qso ADD COLUMN [lotw_deleted_entity] INTEGER NOT NULL DEFAULT 0", con))
                            alter.ExecuteNonQuery();
                        SchemaHasChanged = true;
                    }
                }
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // The QRZ.com confirmation columns, parallel to the LoTW ones. Kept separate because QRZ's
        // "confirmed" is a different, broader set than LoTW's, so a QSO can carry one tick and not the
        // other. Idempotent - each ALTER runs only if the column is missing.
        private void AddQrzConfirmationColumns()
        {
            try
            {
                using (var cmd = new SQLiteCommand(
                    "SELECT count(*) FROM pragma_table_info('qso') WHERE name = 'qrz_qsl_rcvd'", con))
                {
                    if (Convert.ToInt32(cmd.ExecuteScalar()) == 0)
                    {
                        using (var alter = new SQLiteCommand(
                            "ALTER TABLE qso ADD COLUMN [qrz_qsl_rcvd] INTEGER NOT NULL DEFAULT 0", con))
                            alter.ExecuteNonQuery();
                        SchemaHasChanged = true;
                    }
                }
                using (var cmd = new SQLiteCommand(
                    "SELECT count(*) FROM pragma_table_info('qso') WHERE name = 'qrz_qsl_rdate'", con))
                {
                    if (Convert.ToInt32(cmd.ExecuteScalar()) == 0)
                    {
                        using (var alter = new SQLiteCommand(
                            "ALTER TABLE qso ADD COLUMN [qrz_qsl_rdate] nvarchar(20) NULL", con))
                            alter.ExecuteNonQuery();
                        SchemaHasChanged = true;
                    }
                }
                using (var cmd = new SQLiteCommand(
                    "SELECT count(*) FROM pragma_table_info('qso') WHERE name = 'qrz_deleted_entity'", con))
                {
                    if (Convert.ToInt32(cmd.ExecuteScalar()) == 0)
                    {
                        using (var alter = new SQLiteCommand(
                            "ALTER TABLE qso ADD COLUMN [qrz_deleted_entity] INTEGER NOT NULL DEFAULT 0", con))
                            alter.ExecuteNonQuery();
                        SchemaHasChanged = true;
                    }
                }
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // The eQSL confirmation columns, parallel to the LoTW/QRZ ones. eQSL is a third, independent
        // confirmation universe, so a QSO can carry any combination of the three ticks. Idempotent.
        private void AddEqslConfirmationColumns()
        {
            try
            {
                using (var cmd = new SQLiteCommand(
                    "SELECT count(*) FROM pragma_table_info('qso') WHERE name = 'eqsl_qsl_rcvd'", con))
                {
                    if (Convert.ToInt32(cmd.ExecuteScalar()) == 0)
                    {
                        using (var alter = new SQLiteCommand(
                            "ALTER TABLE qso ADD COLUMN [eqsl_qsl_rcvd] INTEGER NOT NULL DEFAULT 0", con))
                            alter.ExecuteNonQuery();
                        SchemaHasChanged = true;
                    }
                }
                using (var cmd = new SQLiteCommand(
                    "SELECT count(*) FROM pragma_table_info('qso') WHERE name = 'eqsl_qsl_rdate'", con))
                {
                    if (Convert.ToInt32(cmd.ExecuteScalar()) == 0)
                    {
                        using (var alter = new SQLiteCommand(
                            "ALTER TABLE qso ADD COLUMN [eqsl_qsl_rdate] nvarchar(20) NULL", con))
                            alter.ExecuteNonQuery();
                        SchemaHasChanged = true;
                    }
                }
                using (var cmd = new SQLiteCommand(
                    "SELECT count(*) FROM pragma_table_info('qso') WHERE name = 'eqsl_deleted_entity'", con))
                {
                    if (Convert.ToInt32(cmd.ExecuteScalar()) == 0)
                    {
                        using (var alter = new SQLiteCommand(
                            "ALTER TABLE qso ADD COLUMN [eqsl_deleted_entity] INTEGER NOT NULL DEFAULT 0", con))
                            alter.ExecuteNonQuery();
                        SchemaHasChanged = true;
                    }
                }
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // The Club Log confirmation columns, parallel to the LoTW/QRZ/eQSL ones. Club Log is a fourth,
        // independent confirmation universe (getadif.php QSL_RCVD = Y/V), so a QSO can carry any
        // combination of the four ticks. Idempotent.
        private void AddClublogConfirmationColumns()
        {
            try
            {
                using (var cmd = new SQLiteCommand(
                    "SELECT count(*) FROM pragma_table_info('qso') WHERE name = 'clublog_qsl_rcvd'", con))
                {
                    if (Convert.ToInt32(cmd.ExecuteScalar()) == 0)
                    {
                        using (var alter = new SQLiteCommand(
                            "ALTER TABLE qso ADD COLUMN [clublog_qsl_rcvd] INTEGER NOT NULL DEFAULT 0", con))
                            alter.ExecuteNonQuery();
                        SchemaHasChanged = true;
                    }
                }
                using (var cmd = new SQLiteCommand(
                    "SELECT count(*) FROM pragma_table_info('qso') WHERE name = 'clublog_qsl_rdate'", con))
                {
                    if (Convert.ToInt32(cmd.ExecuteScalar()) == 0)
                    {
                        using (var alter = new SQLiteCommand(
                            "ALTER TABLE qso ADD COLUMN [clublog_qsl_rdate] nvarchar(20) NULL", con))
                            alter.ExecuteNonQuery();
                        SchemaHasChanged = true;
                    }
                }
                using (var cmd = new SQLiteCommand(
                    "SELECT count(*) FROM pragma_table_info('qso') WHERE name = 'clublog_deleted_entity'", con))
                {
                    if (Convert.ToInt32(cmd.ExecuteScalar()) == 0)
                    {
                        using (var alter = new SQLiteCommand(
                            "ALTER TABLE qso ADD COLUMN [clublog_deleted_entity] INTEGER NOT NULL DEFAULT 0", con))
                            alter.ExecuteNonQuery();
                        SchemaHasChanged = true;
                    }
                }
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // The Paper QSL confirmation column - a single manual flag (1 = the operator received this QSO's
        // paper card by post). No date / deleted-entity columns: it is hand-marked, not downloaded, so
        // there is no service-supplied confirmation date or DXCC code to store. Idempotent.
        private void AddPaperConfirmationColumn()
        {
            try
            {
                using (var cmd = new SQLiteCommand(
                    "SELECT count(*) FROM pragma_table_info('qso') WHERE name = 'paper_qsl_rcvd'", con))
                {
                    if (Convert.ToInt32(cmd.ExecuteScalar()) == 0)
                    {
                        using (var alter = new SQLiteCommand(
                            "ALTER TABLE qso ADD COLUMN [paper_qsl_rcvd] INTEGER NOT NULL DEFAULT 0", con))
                            alter.ExecuteNonQuery();
                        SchemaHasChanged = true;
                    }
                }
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // ── importing a file the log already holds ────────────────────────
        //
        // Splits a freshly parsed ADIF against the QSOs a log already has: records that match a QSO in the
        // log COMPLETE it (filling only what is empty), and the ones that match nothing are handed back to
        // be inserted as new QSOs.
        //
        // This is what makes re-importing the same file safe, and it is the whole answer to a problem
        // HolyLogger created for itself. Until 8.8.4 the importer kept about a third of an ADIF file and
        // dropped the rest - on a real 28,366-QSO Log4OM export, 64% of the bytes, including the ARRL award
        // credits and the counties a USA-CA chase is built on. Those QSOs are already in people's logs, and
        // the only copy of the missing fields is the operator's original file. Asking them to import it
        // again used to mean doubling their log; now it simply completes it.
        //
        // It also protects the operator who imported once and has been logging here ever since: their newer
        // QSOs are not in the file, so nothing in this pass can touch them.
        //
        // Matching is on the four things that identify a contact - callsign, date, band, mode - with the
        // time used only to choose between several contacts with the same station on the same band and mode
        // that day. Measured against a real 28,366-QSO log: 28,197 records identify a QSO outright, 169
        // share a key with another, and none of those were inseparable by time.
        // WHAT A MERGE DID TO ONE QSO THE LOG ALREADY HELD.
        //
        // The completion pass writes into empty columns of contacts that are already stored, and
        // afterwards those contacts look exactly as though they always held the values. Nothing else in
        // the program can tell that a QSL date, a county or a contest name arrived from a file half an
        // hour ago - which makes a bad file quietly editing good QSOs impossible to notice, let alone
        // undo. So the merge says what it touched, field by field.
        public class MergeNote
        {
            public string Call { get; set; }
            public string Date { get; set; }
            public string Time { get; set; }
            public string Band { get; set; }
            public string Mode { get; set; }
            // field name -> what was put in it. Empty for a record that matched two QSOs, where nothing
            // was written at all.
            public List<KeyValuePair<string, string>> Fields = new List<KeyValuePair<string, string>>();
        }

        // Above this many notes the merge stops collecting them. A re-import of a large log can complete
        // tens of thousands of QSOs, and a list of those is held in memory in a 32-bit process that has
        // run out of room on a big import before now. The COUNTS are never capped.
        private const int MaxMergeNotes = 20000;

        public List<QSO> CompleteExistingQsos(List<QSO> parsed, long logId, out int completed, out int ambiguous,
                                              Action<int> progress = null,
                                              List<MergeNote> filledNotes = null,
                                              List<MergeNote> ambiguousNotes = null)
        {
            completed = 0; ambiguous = 0;
            var unmatched = new List<QSO>();
            if (parsed == null || parsed.Count == 0) return unmatched;

            ObservableCollection<QSO> existing = GetQSOsForLog(logId);
            if (existing == null || existing.Count == 0)
            {
                // Nothing to match against - an empty or brand-new log. Every record is new.
                unmatched.AddRange(parsed);
                return unmatched;
            }

            // call|date|band|mode -> the QSOs in the log that match it.
            var index = new Dictionary<string, List<QSO>>(StringComparer.OrdinalIgnoreCase);
            foreach (QSO q in existing)
            {
                if (q == null) continue;
                string k = MatchKey(q);
                if (k == null) continue;
                List<QSO> bucket;
                if (!index.TryGetValue(k, out bucket)) index[k] = bucket = new List<QSO>();
                bucket.Add(q);
            }

            int done = 0;
            lock (_dbLock)
            {
                if (con == null || con.State != System.Data.ConnectionState.Open)
                {
                    unmatched.AddRange(parsed);
                    return unmatched;
                }

                // Only ever writes into a column that is currently EMPTY, so anything the operator has
                // typed, corrected or downloaded since the first import survives untouched.
                const string sql =
                    "UPDATE qso SET " +
                    "extra_adif     = CASE WHEN extra_adif     IS NULL OR extra_adif     = '' THEN @extra ELSE extra_adif     END, " +
                    "state          = CASE WHEN state          IS NULL OR state          = '' THEN @state ELSE state          END, " +
                    "iota           = CASE WHEN iota           IS NULL OR iota           = '' THEN @iota  ELSE iota           END, " +
                    "sota_ref       = CASE WHEN sota_ref       IS NULL OR sota_ref       = '' THEN @sota  ELSE sota_ref       END, " +
                    "pota_ref       = CASE WHEN pota_ref       IS NULL OR pota_ref       = '' THEN @pota  ELSE pota_ref       END, " +
                    "wwff_ref       = CASE WHEN wwff_ref       IS NULL OR wwff_ref       = '' THEN @wwff  ELSE wwff_ref       END, " +
                    "sig            = CASE WHEN sig            IS NULL OR sig            = '' THEN @sig   ELSE sig            END, " +
                    "sig_info       = CASE WHEN sig_info       IS NULL OR sig_info       = '' THEN @siginfo ELSE sig_info     END, " +
                    "credit_granted = CASE WHEN credit_granted IS NULL OR credit_granted = '' THEN @cg    ELSE credit_granted END, " +
                    "cnty           = CASE WHEN cnty           IS NULL OR cnty           = '' THEN @cnty  ELSE cnty           END, " +
                    "qsl_via        = CASE WHEN qsl_via        IS NULL OR qsl_via        = '' THEN @qvia  ELSE qsl_via        END, " +
                    "qsl_rdate      = CASE WHEN qsl_rdate      IS NULL OR qsl_rdate      = '' THEN @qslrd ELSE qsl_rdate      END, " +
                    "qsl_sent       = CASE WHEN qsl_sent       IS NULL OR qsl_sent       = '' THEN @qsent ELSE qsl_sent       END, " +
                    "contest_id     = CASE WHEN contest_id     IS NULL OR contest_id     = '' THEN @cid   ELSE contest_id     END, " +
                    "time_off       = CASE WHEN time_off       IS NULL OR time_off       = '' THEN @toff  ELSE time_off       END, " +
                    "date_off       = CASE WHEN date_off       IS NULL OR date_off       = '' THEN @doff  ELSE date_off       END, " +
                    "qth            = CASE WHEN qth            IS NULL OR qth            = '' THEN @qth   ELSE qth            END " +
                    "WHERE Id = @id";

                using (SQLiteTransaction tx = con.BeginTransaction())
                using (var cmd = new SQLiteCommand(sql, con, tx))
                {
                    foreach (string p in new[] { "@extra", "@state", "@iota", "@sota", "@pota", "@wwff", "@sig",
                                                 "@siginfo", "@cg", "@cnty", "@qvia", "@qslrd", "@qsent", "@cid",
                                                 "@toff", "@doff", "@qth", "@id" })
                        cmd.Parameters.Add(new SQLiteParameter(p));

                    foreach (QSO p in parsed)
                    {
                        done++;
                        if (progress != null && (done % 500 == 0 || done == parsed.Count)) progress(done);

                        string k = p == null ? null : MatchKey(p);
                        List<QSO> bucket;
                        if (k == null || !index.TryGetValue(k, out bucket) || bucket.Count == 0)
                        {
                            if (p != null) unmatched.Add(p);   // a QSO this log does not have yet
                            continue;
                        }

                        // ONE FOR ONE. The matched QSO is taken OUT of the bucket, so a second record
                        // that looks the same cannot pair with it as well - it finds the bucket empty and
                        // is added, as a second contact should be. Without this, one QSO in the log
                        // absorbed any number of records from the file and the extras vanished: the whole
                        // reason a 17,430-record file merged into a log and added nothing at all.
                        //
                        // With the minute now in the key a bucket rarely holds more than one, but it
                        // still can - the same station, same minute, twice - and the rule has to hold
                        // there too.
                        QSO target = bucket[0];
                        if (bucket.Count > 1)
                        {
                            // Several records for that minute: the one logged nearest the same second is
                            // the one this record belongs to. If two are equally close there is no honest
                            // way to choose - and since the log clearly HAS this contact, the record is
                            // skipped rather than added as a duplicate.
                            bool tie;
                            target = ClosestByTime(bucket, p.Time, out tie);
                            if (tie || target == null)
                            {
                                ambiguous++;
                                if (ambiguousNotes != null && ambiguousNotes.Count < MaxMergeNotes)
                                    ambiguousNotes.Add(NoteFor(p));
                                continue;
                            }
                        }
                        bucket.Remove(target);

                        // A record that has nothing to give still counts as "already in this log" - it
                        // simply needs no write. Skipping those keeps a re-import of a plain file (a
                        // WSJT-X log, say, whose records hold nothing beyond what we already model) from
                        // paying for tens of thousands of pointless UPDATEs and the page writes behind
                        // them.
                        if (!CarriesAnything(p)) { completed++; continue; }

                        cmd.Parameters[0].Value = Blank(p.ExtraAdif);
                        cmd.Parameters[1].Value = Blank(p.State);
                        cmd.Parameters[2].Value = Blank(p.Iota);
                        cmd.Parameters[3].Value = Blank(p.SotaRef);
                        cmd.Parameters[4].Value = Blank(p.PotaRef);
                        cmd.Parameters[5].Value = Blank(p.WwffRef);
                        cmd.Parameters[6].Value = Blank(p.Sig);
                        cmd.Parameters[7].Value = Blank(p.SigInfo);
                        cmd.Parameters[8].Value = Blank(p.CreditGranted);
                        cmd.Parameters[9].Value = Blank(p.Cnty);
                        cmd.Parameters[10].Value = Blank(p.QslVia);
                        cmd.Parameters[11].Value = Blank(p.QslRDate);
                        cmd.Parameters[12].Value = Blank(p.QslSent);
                        cmd.Parameters[13].Value = Blank(p.ContestId);
                        cmd.Parameters[14].Value = Blank(p.TimeOff);
                        cmd.Parameters[15].Value = Blank(p.DateOff);
                        cmd.Parameters[16].Value = Blank(p.Qth);
                        cmd.Parameters[17].Value = target.id;

                        // WORKED OUT BEFORE THE WRITE, because afterwards there is no way to tell what
                        // was empty. The same test the SQL uses - empty here, something in the record -
                        // so the list says exactly what the UPDATE is about to put in.
                        MergeNote note = null;
                        if (filledNotes != null && filledNotes.Count < MaxMergeNotes)
                        {
                            note = NoteFor(p);
                            AddFill(note, "STATE", target.State, p.State);
                            AddFill(note, "IOTA", target.Iota, p.Iota);
                            AddFill(note, "SOTA_REF", target.SotaRef, p.SotaRef);
                            AddFill(note, "POTA_REF", target.PotaRef, p.PotaRef);
                            AddFill(note, "WWFF_REF", target.WwffRef, p.WwffRef);
                            AddFill(note, "SIG", target.Sig, p.Sig);
                            AddFill(note, "SIG_INFO", target.SigInfo, p.SigInfo);
                            AddFill(note, "CREDIT_GRANTED", target.CreditGranted, p.CreditGranted);
                            AddFill(note, "CNTY", target.Cnty, p.Cnty);
                            AddFill(note, "QSL_VIA", target.QslVia, p.QslVia);
                            AddFill(note, "QSLRDATE", target.QslRDate, p.QslRDate);
                            AddFill(note, "QSL_SENT", target.QslSent, p.QslSent);
                            AddFill(note, "CONTEST_ID", target.ContestId, p.ContestId);
                            AddFill(note, "TIME_OFF", target.TimeOff, p.TimeOff);
                            AddFill(note, "DATE_OFF", target.DateOff, p.DateOff);
                            AddFill(note, "QTH", target.Qth, p.Qth);
                            // Not printed value-by-value: it is every ADIF field this program has no
                            // column of its own for, and on a Log4OM record that is hundreds of bytes.
                            if (IsEmpty(target.ExtraAdif) && !IsEmpty(p.ExtraAdif))
                                note.Fields.Add(new KeyValuePair<string, string>(
                                    "other ADIF fields", "kept from the file"));
                        }

                        try
                        {
                            if (cmd.ExecuteNonQuery() > 0)
                            {
                                completed++;
                                if (note != null && note.Fields.Count > 0) filledNotes.Add(note);
                            }
                        }
                        catch (Exception swallowed) { Log.Swallow(swallowed); }
                    }

                    tx.Commit();
                }
            }
            return unmatched;
        }

        private static bool IsEmpty(string s) { return string.IsNullOrWhiteSpace(s); }

        // Which QSO a note is about, in the same five things every other report names it by.
        private static MergeNote NoteFor(QSO p)
        {
            return new MergeNote
            {
                Call = (p.DXCall ?? string.Empty).Trim(),
                Date = (p.Date ?? string.Empty).Trim(),
                Time = (p.Time ?? string.Empty).Trim(),
                Band = (p.Band ?? string.Empty).Trim(),
                Mode = (p.Mode ?? string.Empty).Trim(),
            };
        }

        // Records one field the merge is about to fill - empty in the log, present in the file. Anything
        // the log already holds is left alone and is not a change, so it is not listed.
        private static void AddFill(MergeNote note, string field, string current, string incoming)
        {
            if (note == null) return;
            if (!IsEmpty(current)) return;
            if (IsEmpty(incoming)) return;
            note.Fields.Add(new KeyValuePair<string, string>(field, incoming.Trim()));
        }

        // Whether a parsed record holds anything the completion pass could actually write.
        private static bool CarriesAnything(QSO p)
        {
            return !string.IsNullOrWhiteSpace(p.ExtraAdif)
                || !string.IsNullOrWhiteSpace(p.State) || !string.IsNullOrWhiteSpace(p.Iota)
                || !string.IsNullOrWhiteSpace(p.SotaRef) || !string.IsNullOrWhiteSpace(p.PotaRef)
                || !string.IsNullOrWhiteSpace(p.WwffRef) || !string.IsNullOrWhiteSpace(p.Sig)
                || !string.IsNullOrWhiteSpace(p.SigInfo) || !string.IsNullOrWhiteSpace(p.CreditGranted)
                || !string.IsNullOrWhiteSpace(p.Cnty) || !string.IsNullOrWhiteSpace(p.QslVia)
                || !string.IsNullOrWhiteSpace(p.QslRDate) || !string.IsNullOrWhiteSpace(p.QslSent)
                || !string.IsNullOrWhiteSpace(p.ContestId) || !string.IsNullOrWhiteSpace(p.TimeOff)
                || !string.IsNullOrWhiteSpace(p.DateOff) || !string.IsNullOrWhiteSpace(p.Qth);
        }

        // WHAT MAKES TWO RECORDS THE SAME CONTACT, for the whole program: callsign, date, band, mode and
        // the MINUTE. Null when the record is too incomplete to identify, which is safer than matching it
        // to the wrong QSO.
        //
        // The minute used to be missing here, and that cost QSOs. A station worked twice on the same day,
        // same band and same mode - an hour apart, or five - was one key, so on an import every record
        // after the first was counted as "already in this log" and thrown away. Measured on one
        // operator's file: 17,430 records, 16,192 keys without the minute; 984 of the collapsed records
        // were contacts at genuinely different times, 88 of them more than an hour apart.
        //
        // To the minute rather than the second, and rather than a tolerance in either direction, because
        // Tools > Remove Duplicates has always judged it that way and one definition serving both is
        // worth more than a cleverer one used in only half the program. Where the minute errs it errs
        // by KEEPING data: two records at 17:24:59 and 17:25:16 are different keys, so the second is
        // added rather than silently dropped, and losing a contact is the failure that matters.
        //
        // NOT the frequency, the station callsign or the operator - deliberately. A file exported by
        // another program rounds the frequency and often carries no operator at all, so demanding those
        // would make every re-import look new and DOUBLE the log, which is a worse fault than the one
        // this fixes.
        internal static string MatchKey(QSO q)
        {
            if (q == null) return null;
            string call = (q.DXCall ?? string.Empty).Trim();
            string date = (q.Date ?? string.Empty).Trim();
            string band = (q.Band ?? string.Empty).Trim();
            string mode = (q.Mode ?? string.Empty).Trim();
            if (call.Length == 0 || date.Length == 0) return null;

            // The date sometimes arrives as "yyyyMMdd HHmmss"; only the day identifies the contact.
            int space = date.IndexOf(' ');
            if (space > 0) date = date.Substring(0, space);

            // "HHmmss" or "HHmm" -> "HHmm". A record with no readable time keeps an empty slot, so it can
            // still only ever match another record that has none either.
            string time = (q.Time ?? string.Empty).Trim();
            if (time.Length > 4) time = time.Substring(0, 4);

            return call.ToUpperInvariant() + "|" + date + "|" + band.ToUpperInvariant() + "|"
                   + mode.ToUpperInvariant() + "|" + time;
        }

        // The QSO in the bucket logged closest to that time. tie = two are equally close, so the caller
        // leaves them alone rather than guessing.
        private static QSO ClosestByTime(List<QSO> bucket, string time, out bool tie)
        {
            tie = false;
            int want = TimeToSeconds(time);
            if (want < 0) { tie = true; return null; }

            QSO best = null;
            int bestGap = int.MaxValue;
            foreach (QSO q in bucket)
            {
                int t = TimeToSeconds(q.Time);
                if (t < 0) continue;
                int gap = Math.Abs(t - want);
                if (gap < bestGap) { bestGap = gap; best = q; tie = false; }
                else if (gap == bestGap) tie = true;
            }
            return best;
        }

        // "HHmmss" or "HHmm" -> seconds past midnight; -1 when it is neither.
        private static int TimeToSeconds(string time)
        {
            string t = (time ?? string.Empty).Trim();
            int space = t.IndexOf(' ');
            if (space >= 0) t = t.Substring(space + 1);
            t = t.Replace(":", "");
            if (t.Length < 4) return -1;
            int hh, mm, ss = 0;
            if (!int.TryParse(t.Substring(0, 2), out hh)) return -1;
            if (!int.TryParse(t.Substring(2, 2), out mm)) return -1;
            if (t.Length >= 6) int.TryParse(t.Substring(4, 2), out ss);
            return hh * 3600 + mm * 60 + ss;
        }

        // Sets (or clears) the manual Paper QSL flag on one QSO and persists it immediately - called when
        // the operator ticks/unticks the Paper QSL checkbox in the log grid.
        public void SetPaperQslConfirmed(long qsoId, bool confirmed)
        {
            lock (_dbLock)
            {
                if (con == null || con.State != System.Data.ConnectionState.Open) return;
                using (var cmd = new SQLiteCommand("UPDATE qso SET paper_qsl_rcvd = @v WHERE Id = @id", con))
                {
                    cmd.Parameters.Add(new SQLiteParameter("@v", confirmed ? 1 : 0));
                    cmd.Parameters.Add(new SQLiteParameter("@id", qsoId));
                    cmd.ExecuteNonQuery();
                }
            }
        }

        // The log a QSO belongs to (captured just before a delete so an undo can restore it to the SAME
        // log - the Search window can be searching a log that is not the active one). -1 if not found.
        public long GetQsoLogId(int id)
        {
            lock (_dbLock)
            {
                if (con == null || con.State != System.Data.ConnectionState.Open) return -1;
                using (var cmd = new SQLiteCommand("SELECT log_id FROM qso WHERE Id = @id", con))
                {
                    cmd.Parameters.Add(new SQLiteParameter("@id", id));
                    var o = cmd.ExecuteScalar();
                    return (o == null || o == DBNull.Value) ? -1 : Convert.ToInt64(o);
                }
            }
        }

        // Re-inserts a previously-deleted QSO into the given log, restoring its data, confirmation flags and
        // per-service upload status. Returns the new row Id (the primary key changes on re-insert). Used by
        // the Search window's Undo-delete.
        public int RestoreQso(QSO qso, long logId)
        {
            BumpContentVersion();   // this write can change who is in the log or what they are called
            if (qso == null) return 0;
            lock (_dbLock)
            {
                if (con == null || con.State != System.Data.ConnectionState.Open) return 0;
                const string sql = "INSERT INTO qso (my_callsign,operator,my_square,my_locator,dx_locator,frequency,band,dx_callsign,rst_rcvd,rst_sent,date,time,mode,submode,exchange,comment,name,country,continent,cq_zone,itu_zone,state,qth,dxcc,prop_mode,sat_name,soapbox,eqsl_status,qrz_status,lotw_status,clublog_status,lotw_qsl_rcvd,lotw_qsl_rdate,lotw_deleted_entity,qrz_qsl_rcvd,qrz_qsl_rdate,qrz_deleted_entity,eqsl_qsl_rcvd,eqsl_qsl_rdate,eqsl_deleted_entity,clublog_qsl_rcvd,clublog_qsl_rdate,clublog_deleted_entity,paper_qsl_rcvd,iota,sota_ref,pota_ref,wwff_ref,sig,sig_info," + CarriedColumns + ",log_id) " +
                    "VALUES (@my,@op,@mysq,@myloc,@dxloc,@freq,@band,@dx,@rr,@rs,@date,@time,@mode,@sub,@exch,@com,@name,@country,@cont,@cqz,@ituz,@state,@qth,@dxcc,@prop,@sat,@soap,@es,@qs,@ls,@cs,@lr,@lrd,@lde,@qr,@qrd,@qde,@er,@erd,@ede,@cr,@crd,@cde,@paper,@iota,@sota_ref,@pota_ref,@wwff_ref,@sig,@sig_info," + CarriedValues + ",@log)";
                using (var cmd = new SQLiteCommand(sql, con))
                {
                    cmd.Parameters.AddWithValue("@my", (object)qso.MyCall ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@op", (object)qso.Operator ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@mysq", (object)qso.STX ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@myloc", (object)qso.MyLocator ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@dxloc", (object)qso.DXLocator ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@freq", (object)qso.Freq ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@band", (object)qso.Band ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@dx", (object)qso.DXCall ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@rr", (object)qso.RST_RCVD ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@rs", (object)qso.RST_SENT ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@date", (object)qso.Date ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@time", (object)qso.Time ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@mode", (object)qso.Mode ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@sub", (object)qso.SUBMode ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@exch", (object)qso.SRX ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@com", (object)qso.Comment ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@name", (object)qso.Name ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@country", (object)qso.Country ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@cont", (object)qso.Continent ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@cqz", (object)qso.CQZone ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@ituz", (object)qso.ITUZone ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@state", (object)qso.State ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@qth", (object)qso.Qth ?? DBNull.Value);

                    cmd.Parameters.AddWithValue("@dxcc", qso.DxccCode > 0 ? (object)qso.DxccCode : DBNull.Value);
                    cmd.Parameters.AddWithValue("@prop", (object)qso.PROP_MODE ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@sat", (object)qso.SAT_NAME ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@soap", (object)qso.SOAPBOX ?? DBNull.Value);
                    AddActivityParams(cmd, qso, "@");
                    // Undoing a delete must give back the QSO the operator had, not a stripped copy of it.
                    AddCarriedParams(cmd, qso);
                    cmd.Parameters.AddWithValue("@es", qso.EqslStatus);
                    cmd.Parameters.AddWithValue("@qs", qso.QrzStatus);
                    cmd.Parameters.AddWithValue("@ls", qso.LotwStatus);
                    cmd.Parameters.AddWithValue("@cs", qso.ClublogStatus);
                    cmd.Parameters.AddWithValue("@lr", qso.LotwQslRcvd);
                    cmd.Parameters.AddWithValue("@lrd", (object)qso.LotwQslRDate ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@lde", qso.LotwDeletedEntity);
                    cmd.Parameters.AddWithValue("@qr", qso.QrzQslRcvd);
                    cmd.Parameters.AddWithValue("@qrd", (object)qso.QrzQslRDate ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@qde", qso.QrzDeletedEntity);
                    cmd.Parameters.AddWithValue("@er", qso.EqslQslRcvd);
                    cmd.Parameters.AddWithValue("@erd", (object)qso.EqslQslRDate ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@ede", qso.EqslDeletedEntity);
                    cmd.Parameters.AddWithValue("@cr", qso.ClublogQslRcvd);
                    cmd.Parameters.AddWithValue("@crd", (object)qso.ClublogQslRDate ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@cde", qso.ClublogDeletedEntity);
                    cmd.Parameters.AddWithValue("@paper", qso.PaperQslRcvd);
                    cmd.Parameters.AddWithValue("@log", logId);
                    cmd.ExecuteNonQuery();
                }
                using (var idc = new SQLiteCommand("SELECT last_insert_rowid()", con))
                    return Convert.ToInt32(idc.ExecuteScalar());
            }
        }

        // Persists the five per-source confirmation flags for one QSO - used by the QSO editor's
        // "Confirmed" checkboxes. Kept separate from Update() (which writes only the QSO's data fields), so
        // a normal edit never disturbs confirmation state unless the operator changed it here.
        public void UpdateConfirmations(QSO qso)
        {
            if (qso == null) return;
            lock (_dbLock)
            {
                if (con == null || con.State != System.Data.ConnectionState.Open) return;
                using (var cmd = new SQLiteCommand(
                    "UPDATE qso SET lotw_qsl_rcvd=@l, qrz_qsl_rcvd=@q, eqsl_qsl_rcvd=@e, clublog_qsl_rcvd=@c, paper_qsl_rcvd=@p WHERE Id=@id", con))
                {
                    cmd.Parameters.Add(new SQLiteParameter("@l", qso.LotwQslRcvd));
                    cmd.Parameters.Add(new SQLiteParameter("@q", qso.QrzQslRcvd));
                    cmd.Parameters.Add(new SQLiteParameter("@e", qso.EqslQslRcvd));
                    cmd.Parameters.Add(new SQLiteParameter("@c", qso.ClublogQslRcvd));
                    cmd.Parameters.Add(new SQLiteParameter("@p", qso.PaperQslRcvd));
                    cmd.Parameters.Add(new SQLiteParameter("@id", (long)qso.id));
                    cmd.ExecuteNonQuery();
                }
            }
        }

        // One confirmation as LoTW - or QRZ, or eQSL - reported it. A service-neutral carrier: each source
        // gives the same handful of fields and is matched to the log the same way, so the same type feeds
        // MarkLotwConfirmed / MarkQrzConfirmed / MarkEqslConfirmed.
        public class LotwConfirmation
        {
            public string Call { get; set; }
            public string Band { get; set; }
            public string Mode { get; set; }
            public string QsoDate { get; set; }        // yyyyMMdd
            public string StationCallsign { get; set; } // ours, when the report carries it
            public string QslRDate { get; set; }        // yyyyMMdd, when the report carries it
            public int DxccCode { get; set; }           // LoTW's worked-entity code (date-correct)

            // KEPT FOR THE QSOS THAT ARE NOT IN THE LOG AT ALL. Matching never needed these - LoTW is
            // matched on call, band, mode and date - but a confirmation for a contact the log has lost
            // can be turned back INTO that contact, and then the time and the grid are worth having.
            // LoTW holds nothing else of a QSO: no RST, no name, no QTH, no comment.
            public string TimeOn { get; set; }          // HHMM or HHMMSS, as the report writes it
            public string Grid { get; set; }            // the worked station's square
            public string Country { get; set; }         // LoTW's own name for the entity
            public string Continent { get; set; }
            public string CqZone { get; set; }
            public string ItuZone { get; set; }

            // eQSL SENDS THESE AND LOTW DOES NOT. An eQSL card carries the two signal reports, and the
            // submode and propagation mode when they are not blank - so a contact restored from eQSL
            // comes back with more of itself than one restored from LoTW. Empty for LoTW, which has
            // never held them, and empty is the honest answer: see ToQso, where nothing is invented.
            public string RstSent { get; set; }
            public string RstRcvd { get; set; }
            public string SubMode { get; set; }
            public string PropMode { get; set; }
        }

        // Marks the QSOs that LoTW says are confirmed. Returns how many rows changed.
        //
        // Matched on the same four things LoTW itself matches a contact on - worked callsign, band,
        // mode and date - plus OUR station callsign when the report names it, which matters to anyone
        // who operates under more than one call. Time is deliberately not compared: LoTW records the
        // other station's logged time, which routinely differs from ours by a minute or two.
        public int MarkLotwConfirmed(IList<LotwConfirmation> confirmations)
            => MarkLotwConfirmed(confirmations, out _);

        // The PSK family: LoTW reports the exact sub-mode, the log almost always stores plain "PSK".
        // Measured from a real 5,935-confirmation download, these were the ONLY digital modes where a
        // confirmation matched a QSO on call+band+date but was rejected on mode. Kept narrow on purpose
        // - broadening further (e.g. folding all data modes together) would risk a wrong match.
        private static readonly string[] PskFamily =
            { "PSK", "PSK31", "PSK63", "PSK125", "BPSK", "BPSK31", "BPSK63", "QPSK", "QPSK31", "QPSK63", "DATA" };

        private static readonly string PskFamilyInList =
            string.Join(",", PskFamily.Select(m => "'" + m + "'"));

        private static bool IsPskFamily(string mode)
        {
            string m = (mode ?? string.Empty).Trim();
            foreach (string f in PskFamily)
                if (string.Equals(m, f, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public int MarkLotwConfirmed(IList<LotwConfirmation> confirmations, out List<LotwConfirmation> unmatched)
            => MarkLotwConfirmed(confirmations, false, null, out unmatched);

        public int MarkLotwConfirmed(IList<LotwConfirmation> confirmations, bool fullReset, out List<LotwConfirmation> unmatched)
            => MarkLotwConfirmed(confirmations, fullReset, null, out unmatched);

        // unmatched receives every confirmation that found no QSO, for diagnosis.
        //
        // fullReset clears every existing confirmation mark before re-applying. Passed on a FULL
        // download, which is authoritative: it both rebuilds cleanly and undoes any earlier bad marks
        // (an older build, before station-callsign scoping, ticked QSOs in the wrong operator's log).
        //
        // onProgress, when given, is called with the running count as confirmations are processed - this
        // is the slow part on a big download, so the caller can keep a counter moving. UI-agnostic: it
        // is just an int, and the caller marshals it to the screen.
        public int MarkLotwConfirmed(IList<LotwConfirmation> confirmations, bool fullReset,
                                     Action<int> onProgress, out List<LotwConfirmation> unmatched)
            => MarkConfirmedCore("lotw_qsl_rcvd", "lotw_qsl_rdate", "lotw_deleted_entity",
                                 confirmations, fullReset, onProgress, System.Threading.CancellationToken.None, out unmatched);

        public int MarkLotwConfirmed(IList<LotwConfirmation> confirmations, bool fullReset,
                                     Action<int> onProgress, System.Threading.CancellationToken ct, out List<LotwConfirmation> unmatched)
            => MarkConfirmedCore("lotw_qsl_rcvd", "lotw_qsl_rdate", "lotw_deleted_entity",
                                 confirmations, fullReset, onProgress, ct, out unmatched);

        // QRZ counterparts of the MarkLotwConfirmed overloads. Identical matching (QRZ FETCH gives the
        // same call/band/mode/date/station fields LoTW does), only the target columns differ.
        public int MarkQrzConfirmed(IList<LotwConfirmation> confirmations, out List<LotwConfirmation> unmatched)
            => MarkQrzConfirmed(confirmations, false, null, out unmatched);

        public int MarkQrzConfirmed(IList<LotwConfirmation> confirmations, bool fullReset, out List<LotwConfirmation> unmatched)
            => MarkQrzConfirmed(confirmations, fullReset, null, out unmatched);

        public int MarkQrzConfirmed(IList<LotwConfirmation> confirmations, bool fullReset,
                                    Action<int> onProgress, out List<LotwConfirmation> unmatched)
            => MarkConfirmedCore("qrz_qsl_rcvd", "qrz_qsl_rdate", "qrz_deleted_entity",
                                 confirmations, fullReset, onProgress, System.Threading.CancellationToken.None, out unmatched);

        public int MarkQrzConfirmed(IList<LotwConfirmation> confirmations, bool fullReset,
                                    Action<int> onProgress, System.Threading.CancellationToken ct, out List<LotwConfirmation> unmatched)
            => MarkConfirmedCore("qrz_qsl_rcvd", "qrz_qsl_rdate", "qrz_deleted_entity",
                                 confirmations, fullReset, onProgress, ct, out unmatched);

        // eQSL counterparts. Same matching engine, eQSL columns.
        public int MarkEqslConfirmed(IList<LotwConfirmation> confirmations, out List<LotwConfirmation> unmatched)
            => MarkEqslConfirmed(confirmations, false, null, out unmatched);

        public int MarkEqslConfirmed(IList<LotwConfirmation> confirmations, bool fullReset, out List<LotwConfirmation> unmatched)
            => MarkEqslConfirmed(confirmations, fullReset, null, out unmatched);

        public int MarkEqslConfirmed(IList<LotwConfirmation> confirmations, bool fullReset,
                                     Action<int> onProgress, out List<LotwConfirmation> unmatched)
            => MarkConfirmedCore("eqsl_qsl_rcvd", "eqsl_qsl_rdate", "eqsl_deleted_entity",
                                 confirmations, fullReset, onProgress, System.Threading.CancellationToken.None, out unmatched);

        public int MarkEqslConfirmed(IList<LotwConfirmation> confirmations, bool fullReset,
                                     Action<int> onProgress, System.Threading.CancellationToken ct, out List<LotwConfirmation> unmatched)
            => MarkConfirmedCore("eqsl_qsl_rcvd", "eqsl_qsl_rdate", "eqsl_deleted_entity",
                                 confirmations, fullReset, onProgress, ct, out unmatched);

        // Club Log counterparts. Same matching engine, Club Log columns.
        public int MarkClublogConfirmed(IList<LotwConfirmation> confirmations, out List<LotwConfirmation> unmatched)
            => MarkClublogConfirmed(confirmations, false, null, out unmatched);

        public int MarkClublogConfirmed(IList<LotwConfirmation> confirmations, bool fullReset, out List<LotwConfirmation> unmatched)
            => MarkClublogConfirmed(confirmations, fullReset, null, out unmatched);

        public int MarkClublogConfirmed(IList<LotwConfirmation> confirmations, bool fullReset,
                                        Action<int> onProgress, out List<LotwConfirmation> unmatched)
            => MarkConfirmedCore("clublog_qsl_rcvd", "clublog_qsl_rdate", "clublog_deleted_entity",
                                 confirmations, fullReset, onProgress, System.Threading.CancellationToken.None, out unmatched);

        public int MarkClublogConfirmed(IList<LotwConfirmation> confirmations, bool fullReset,
                                        Action<int> onProgress, System.Threading.CancellationToken ct, out List<LotwConfirmation> unmatched)
            => MarkConfirmedCore("clublog_qsl_rcvd", "clublog_qsl_rdate", "clublog_deleted_entity",
                                 confirmations, fullReset, onProgress, ct, out unmatched);

        // Shared engine behind MarkLotwConfirmed / MarkQrzConfirmed. The three column names name the
        // rcvd flag, the confirmation-date, and the deleted-entity flag to write; they are internal
        // constants (never user input), so interpolating them into the SQL is safe.
        private int MarkConfirmedCore(string rcvdCol, string rdateCol, string deletedCol,
                                      IList<LotwConfirmation> confirmations, bool fullReset,
                                      Action<int> onProgress, System.Threading.CancellationToken ct,
                                      out List<LotwConfirmation> unmatched)
        {
            unmatched = new List<LotwConfirmation>();
            if (confirmations == null || confirmations.Count == 0) return 0;

            lock (_dbLock)
            {
                if (con == null || con.State != System.Data.ConnectionState.Open) return 0;

                // EVERY statement below is confined to the log being checked.
                //
                // A download is now asked for ONE log's station callsign, so it can only speak for that
                // log. The clear-and-rebuild used to wipe the flag across the whole table: a full check
                // on one log erased every other log's confirmations and could not put them back, because
                // the confirmations that would have restored them were never downloaded. A 1,771-QSO
                // special-event log lost all 809 of its LoTW ticks that way, silently.
                string logScope = " AND log_id = " + ActiveLogId + " ";

                int changed = 0;
                using (var tx = con.BeginTransaction())
                {
                    if (fullReset)
                        using (var clear = new SQLiteCommand(
                            $"UPDATE qso SET {rcvdCol} = 0, {rdateCol} = NULL, {deletedCol} = 0 " +
                            $"WHERE {rcvdCol} = 1" + logScope, con, tx))
                            clear.ExecuteNonQuery();

                    // The station callsign is REQUIRED to match. A confirmation whose station is empty
                    // matches nothing here (my_callsign is never blank), which is the safe direction -
                    // better to leave a QSO unticked than to tick a QSO some OTHER operator made that
                    // merely shares the call+band+mode+date. That is what stops confirmations leaking
                    // across logs.
                    //
                    // But it matches by IDENTITY, not letter for letter: 4Z5SL and 4Z5SL/6 are one
                    // station, and the awards treat them as one, so a confirmation reported under either
                    // has to find the QSO logged under the other. Compared as "the base callsign, or the
                    // base callsign followed by a stroke", which is precisely the set of spellings
                    // CallsignIdentity.Base collapses to that base - a leading stroke (4X/OK1DL) is a
                    // DIFFERENT station and is left out, because Base keeps it.
                    using (var exact = new SQLiteCommand(
                        $"UPDATE qso SET {rcvdCol} = 1, {rdateCol} = @rdate, {deletedCol} = @deleted " +
                        "WHERE dx_callsign = @call COLLATE NOCASE " +
                        "  AND band  = @band COLLATE NOCASE " +
                        "  AND mode  = @mode COLLATE NOCASE " +
                        "  AND date  = @date " +
                        "  AND (my_callsign = @mycall COLLATE NOCASE " +
                        "       OR my_callsign LIKE @mycallStroke COLLATE NOCASE)" + logScope, con, tx))
                    // Fallback for the digital sub-modes. The report gives the exact sub-mode (PSK31,
                    // PSK63, DATA...) while the log stores the family (PSK), so the exact query above
                    // misses them. This one drops the mode test to "the log's mode is in the SAME
                    // family", and is tried ONLY when the reported mode has a known family and the exact
                    // match already failed - so an ordinary SSB/CW/FT8 QSO is never broadened.
                    using (var family = new SQLiteCommand(
                        $"UPDATE qso SET {rcvdCol} = 1, {rdateCol} = @rdate, {deletedCol} = @deleted " +
                        "WHERE dx_callsign = @call COLLATE NOCASE " +
                        "  AND band  = @band COLLATE NOCASE " +
                        "  AND date  = @date " +
                        "  AND (my_callsign = @mycall COLLATE NOCASE " +
                        "       OR my_callsign LIKE @mycallStroke COLLATE NOCASE) " +
                        "  AND UPPER(TRIM(mode)) IN (" + PskFamilyInList + ")" + logScope, con, tx))
                    {
                        foreach (var cmd in new[] { exact, family })
                        {
                            cmd.Parameters.Add(new SQLiteParameter("@rdate"));
                            cmd.Parameters.Add(new SQLiteParameter("@call"));
                            cmd.Parameters.Add(new SQLiteParameter("@band"));
                            cmd.Parameters.Add(new SQLiteParameter("@date"));
                            cmd.Parameters.Add(new SQLiteParameter("@mycall"));
                            cmd.Parameters.Add(new SQLiteParameter("@mycallStroke"));
                            cmd.Parameters.Add(new SQLiteParameter("@deleted"));
                        }
                        exact.Parameters.Add(new SQLiteParameter("@mode"));

                        int processed = 0;
                        foreach (var c in confirmations)
                        {
                            // Stop requested: throw so the using(tx) below disposes WITHOUT committing -
                            // the fullReset clear and any marks so far roll back, leaving the DB unchanged.
                            ct.ThrowIfCancellationRequested();

                            if (string.IsNullOrWhiteSpace(c?.Call) || string.IsNullOrWhiteSpace(c.QsoDate)) continue;
                            // Cannot attribute a confirmation with no station callsign to any operator,
                            // so it is left unmatched rather than guessed at across logs.
                            if (string.IsNullOrWhiteSpace(c.StationCallsign)) { unmatched.Add(c); continue; }

                            int deletedFlag = DXCCManager.DeletedEntities.IsDeleted(c.DxccCode) ? 1 : 0;
                            exact.Parameters["@deleted"].Value = deletedFlag;
                            family.Parameters["@deleted"].Value = deletedFlag;

                            string rdate  = c.QslRDate ?? string.Empty;
                            string call   = c.Call.Trim();
                            string band   = (c.Band ?? string.Empty).Trim();
                            string date   = c.QsoDate.Trim();
                            // The station's IDENTITY, so a confirmation reported under 4Z5SL finds the
                            // QSO logged as 4Z5SL/6 and the other way round. Base() drops a trailing
                            // stroke modifier and keeps a leading one, so 4X/OK1DL stays its own station.
                            string mycall = CallsignIdentity.Base((c.StationCallsign ?? string.Empty).Trim());
                            string mycallStroke = mycall + "/%";

                            exact.Parameters["@rdate"].Value = rdate;
                            exact.Parameters["@call"].Value = call;
                            exact.Parameters["@band"].Value = band;
                            exact.Parameters["@date"].Value = date;
                            exact.Parameters["@mycall"].Value = mycall;
                            exact.Parameters["@mycallStroke"].Value = mycallStroke;
                            exact.Parameters["@mode"].Value = (c.Mode ?? string.Empty).Trim();

                            int rows = exact.ExecuteNonQuery();

                            // Only fall back when the exact match failed AND the LoTW mode is a digital
                            // sub-mode we widen to its family. Everything else is a genuine non-match.
                            if (rows == 0 && IsPskFamily(c.Mode))
                            {
                                family.Parameters["@rdate"].Value = rdate;
                                family.Parameters["@call"].Value = call;
                                family.Parameters["@band"].Value = band;
                                family.Parameters["@date"].Value = date;
                                family.Parameters["@mycall"].Value = mycall;
                                family.Parameters["@mycallStroke"].Value = mycallStroke;
                                rows = family.ExecuteNonQuery();
                            }

                            if (rows > 0) changed += rows; else unmatched.Add(c);

                            if (onProgress != null && (++processed % 200) == 0) onProgress(processed);
                        }
                    }
                    tx.Commit();
                }
                return changed;
            }
        }

        // Every QSO logged with this callsign, as "band mode date mycall" lines. Used by the unmatched
        // report to show what the log actually holds beside what LoTW sent, which is what reveals
        // whether the mismatch is the band, the mode, the date or the station callsign.
        public List<string> DescribeQsosForCallsign(string dxCallsign, int limit = 6)
        {
            var found = new List<string>();
            if (string.IsNullOrWhiteSpace(dxCallsign)) return found;

            lock (_dbLock)
            {
                if (con == null || con.State != System.Data.ConnectionState.Open) return found;
                using (var cmd = new SQLiteCommand(
                    "SELECT band, mode, date, my_callsign FROM qso " +
                    "WHERE dx_callsign = @call COLLATE NOCASE ORDER BY date LIMIT @lim", con))
                {
                    cmd.Parameters.Add(new SQLiteParameter("@call", dxCallsign.Trim()));
                    cmd.Parameters.Add(new SQLiteParameter("@lim", limit));
                    using (var rdr = cmd.ExecuteReader())
                        while (rdr.Read())
                            found.Add($"{rdr["band"],-6} {rdr["mode"],-6} {rdr["date"],-10} {rdr["my_callsign"]}");
                }
            }
            return found;
        }


        // EVERY CALLSIGN THIS LOG HAS WORKED **AND THE DAY IT WORKED IT**, in one query.
        //
        // The callsign alone was too blunt a test. Sorting the unmatched confirmations by callsign only,
        // a card from 2014 for a station also worked in 2021 was called an "almost match" and sent to
        // the Log Fixer as a disagreement about a QSO - when the log holds no contact with that station
        // on that day at all. The operator checked the list and found every one of them years apart.
        //
        // So the pile a card falls into is decided by callsign AND date:
        //   the log HAS that station that day - band, mode or time disagree, a question for the Fixer;
        //   it does NOT                      - the contact is absent, and can be put back.
        //
        // The date is the one field both services agree on to the day. Time is deliberately left out:
        // each side records its own operator's clock, and they routinely differ by a minute or two.
        //
        // Keyed "BASECALL|yyyyMMdd" with the identity base form, so a QSO logged as DL1ABC/P answers for
        // a card from DL1ABC - the same rule the callsign-only set used.
        public HashSet<string> WorkedCallsignDatesInLog(long logId)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            lock (_dbLock)
            {
                if (con == null || con.State != System.Data.ConnectionState.Open) return set;
                using (var cmd = new SQLiteCommand(
                    "SELECT DISTINCT dx_callsign, date FROM qso WHERE log_id = @lid AND dx_callsign IS NOT NULL", con))
                {
                    cmd.Parameters.Add(new SQLiteParameter("@lid", logId));
                    using (var rdr = cmd.ExecuteReader())
                        while (rdr.Read())
                        {
                            string call = rdr[0] as string;
                            if (string.IsNullOrWhiteSpace(call)) continue;
                            string date = rdr.IsDBNull(1) ? string.Empty : (rdr.GetValue(1) ?? string.Empty).ToString().Trim();
                            set.Add(CallsignIdentity.Base(call.Trim()) + "|" + date);
                        }
                }
            }
            return set;
        }

        // The key WorkedCallsignDatesInLog stores, built from a confirmation. One place, so the two
        // sides of the comparison can never drift apart.
        public static string CallDateKey(string callsign, string qsoDate)
        {
            return CallsignIdentity.Base((callsign ?? string.Empty).Trim())
                 + "|" + (qsoDate ?? string.Empty).Trim();
        }

        // How many QSOs are currently marked confirmed by LoTW.
        public int GetLotwConfirmedCount()
        {
            lock (_dbLock)
            {
                if (con == null || con.State != System.Data.ConnectionState.Open) return 0;
                using (var cmd = new SQLiteCommand("SELECT COUNT(*) FROM qso WHERE lotw_qsl_rcvd = 1", con))
                    return Convert.ToInt32(cmd.ExecuteScalar());
            }
        }

        // Confirmed-QSO count per log, most first, so a completion message can SHOW that the marking
        // reached every log (not only the active one) - a LoTW confirmation belongs to a contact, so a
        // matching QSO in any log is marked.
        public List<KeyValuePair<string, int>> GetLotwConfirmedCountsByLog()
        {
            var rows = new List<KeyValuePair<string, int>>();
            lock (_dbLock)
            {
                if (con == null || con.State != System.Data.ConnectionState.Open) return rows;
                using (var cmd = new SQLiteCommand(
                    "SELECT l.name, COUNT(q.Id) AS n " +
                    "FROM qso q JOIN logs l ON l.Id = q.log_id " +
                    "WHERE q.lotw_qsl_rcvd = 1 GROUP BY l.name HAVING n > 0 ORDER BY n DESC", con))
                using (var rdr = cmd.ExecuteReader())
                    while (rdr.Read())
                        rows.Add(new KeyValuePair<string, int>(
                            rdr["name"]?.ToString() ?? "(unnamed)", Convert.ToInt32(rdr["n"])));
            }
            return rows;
        }

        // How many QSOs in ONE log are marked confirmed by LoTW. The confirmation marks span the whole
        // database (a LoTW confirmation belongs to a contact, not a log), so a per-log figure is what
        // answers "how many in the log I'm looking at".
        public int GetLotwConfirmedCount(long logId)
        {
            lock (_dbLock)
            {
                if (con == null || con.State != System.Data.ConnectionState.Open) return 0;
                using (var cmd = new SQLiteCommand(
                    "SELECT COUNT(*) FROM qso WHERE lotw_qsl_rcvd = 1 AND log_id = @log", con))
                {
                    cmd.Parameters.Add(new SQLiteParameter("@log", logId));
                    return Convert.ToInt32(cmd.ExecuteScalar());
                }
            }
        }

        // QRZ counterparts of the three LoTW count helpers above.
        public int GetQrzConfirmedCount()
        {
            lock (_dbLock)
            {
                if (con == null || con.State != System.Data.ConnectionState.Open) return 0;
                using (var cmd = new SQLiteCommand("SELECT COUNT(*) FROM qso WHERE qrz_qsl_rcvd = 1", con))
                    return Convert.ToInt32(cmd.ExecuteScalar());
            }
        }

        public List<KeyValuePair<string, int>> GetQrzConfirmedCountsByLog()
        {
            var rows = new List<KeyValuePair<string, int>>();
            lock (_dbLock)
            {
                if (con == null || con.State != System.Data.ConnectionState.Open) return rows;
                using (var cmd = new SQLiteCommand(
                    "SELECT l.name, COUNT(q.Id) AS n " +
                    "FROM qso q JOIN logs l ON l.Id = q.log_id " +
                    "WHERE q.qrz_qsl_rcvd = 1 GROUP BY l.name HAVING n > 0 ORDER BY n DESC", con))
                using (var rdr = cmd.ExecuteReader())
                    while (rdr.Read())
                        rows.Add(new KeyValuePair<string, int>(
                            rdr["name"]?.ToString() ?? "(unnamed)", Convert.ToInt32(rdr["n"])));
            }
            return rows;
        }

        public int GetQrzConfirmedCount(long logId)
        {
            lock (_dbLock)
            {
                if (con == null || con.State != System.Data.ConnectionState.Open) return 0;
                using (var cmd = new SQLiteCommand(
                    "SELECT COUNT(*) FROM qso WHERE qrz_qsl_rcvd = 1 AND log_id = @log", con))
                {
                    cmd.Parameters.Add(new SQLiteParameter("@log", logId));
                    return Convert.ToInt32(cmd.ExecuteScalar());
                }
            }
        }

        // eQSL counterparts of the three count helpers.
        public int GetEqslConfirmedCount()
        {
            lock (_dbLock)
            {
                if (con == null || con.State != System.Data.ConnectionState.Open) return 0;
                using (var cmd = new SQLiteCommand("SELECT COUNT(*) FROM qso WHERE eqsl_qsl_rcvd = 1", con))
                    return Convert.ToInt32(cmd.ExecuteScalar());
            }
        }

        public List<KeyValuePair<string, int>> GetEqslConfirmedCountsByLog()
        {
            var rows = new List<KeyValuePair<string, int>>();
            lock (_dbLock)
            {
                if (con == null || con.State != System.Data.ConnectionState.Open) return rows;
                using (var cmd = new SQLiteCommand(
                    "SELECT l.name, COUNT(q.Id) AS n " +
                    "FROM qso q JOIN logs l ON l.Id = q.log_id " +
                    "WHERE q.eqsl_qsl_rcvd = 1 GROUP BY l.name HAVING n > 0 ORDER BY n DESC", con))
                using (var rdr = cmd.ExecuteReader())
                    while (rdr.Read())
                        rows.Add(new KeyValuePair<string, int>(
                            rdr["name"]?.ToString() ?? "(unnamed)", Convert.ToInt32(rdr["n"])));
            }
            return rows;
        }

        public int GetEqslConfirmedCount(long logId)
        {
            lock (_dbLock)
            {
                if (con == null || con.State != System.Data.ConnectionState.Open) return 0;
                using (var cmd = new SQLiteCommand(
                    "SELECT COUNT(*) FROM qso WHERE eqsl_qsl_rcvd = 1 AND log_id = @log", con))
                {
                    cmd.Parameters.Add(new SQLiteParameter("@log", logId));
                    return Convert.ToInt32(cmd.ExecuteScalar());
                }
            }
        }

        // Club Log counterparts of the three count helpers.
        public int GetClublogConfirmedCount()
        {
            lock (_dbLock)
            {
                if (con == null || con.State != System.Data.ConnectionState.Open) return 0;
                using (var cmd = new SQLiteCommand("SELECT COUNT(*) FROM qso WHERE clublog_qsl_rcvd = 1", con))
                    return Convert.ToInt32(cmd.ExecuteScalar());
            }
        }

        public List<KeyValuePair<string, int>> GetClublogConfirmedCountsByLog()
        {
            var rows = new List<KeyValuePair<string, int>>();
            lock (_dbLock)
            {
                if (con == null || con.State != System.Data.ConnectionState.Open) return rows;
                using (var cmd = new SQLiteCommand(
                    "SELECT l.name, COUNT(q.Id) AS n " +
                    "FROM qso q JOIN logs l ON l.Id = q.log_id " +
                    "WHERE q.clublog_qsl_rcvd = 1 GROUP BY l.name HAVING n > 0 ORDER BY n DESC", con))
                using (var rdr = cmd.ExecuteReader())
                    while (rdr.Read())
                        rows.Add(new KeyValuePair<string, int>(
                            rdr["name"]?.ToString() ?? "(unnamed)", Convert.ToInt32(rdr["n"])));
            }
            return rows;
        }

        public int GetClublogConfirmedCount(long logId)
        {
            lock (_dbLock)
            {
                if (con == null || con.State != System.Data.ConnectionState.Open) return 0;
                using (var cmd = new SQLiteCommand(
                    "SELECT COUNT(*) FROM qso WHERE clublog_qsl_rcvd = 1 AND log_id = @log", con))
                {
                    cmd.Parameters.Add(new SQLiteParameter("@log", logId));
                    return Convert.ToInt32(cmd.ExecuteScalar());
                }
            }
        }

        // Index that backs the LoTW pending-queue lookups (filter on lotw_status). Idempotent.
        private void EnsureLotwIndex()
        {
            try
            {
                using (var cmd = new SQLiteCommand("CREATE INDEX IF NOT EXISTS idx_qso_lotw_status ON qso(lotw_status)", con))
                    cmd.ExecuteNonQuery();
            }
            catch (Exception ex) { Log.Swallow(ex); }   // optimization only
        }

        // Returns the QSOs still waiting to be uploaded to LoTW (status 0), oldest first.
        public List<QSO> GetPendingLotwQsos()
        {
            lock (_dbLock)
            {
            var list = new List<QSO>();
            if (con == null || con.State != ConnectionState.Open) return list;
            string stm = "SELECT *, (SELECT name FROM logs WHERE logs.Id = qso.log_id) AS log_name FROM qso WHERE lotw_status = 0 ORDER BY date ASC, time ASC, Id ASC";
            using (SQLiteCommand cmd = new SQLiteCommand(stm, con))
            using (SQLiteDataReader rdr = cmd.ExecuteReader())
            {
                while (rdr.Read())
                {
                    QSO q = new QSO();
                    if (rdr["Id"] != null) q.id = int.Parse(rdr["Id"].ToString());
                    if (rdr["comment"] != null) q.Comment = rdr["comment"].ToString();
                    if (rdr["dx_callsign"] != null) q.DXCall = rdr["dx_callsign"].ToString();
                    if (rdr["mode"] != null) q.Mode = rdr["mode"].ToString();
                    if (rdr["submode"] != null) q.SUBMode = rdr["submode"].ToString();
                    if (rdr["exchange"] != null) q.SRX = rdr["exchange"].ToString();
                    if (rdr["frequency"] != null) q.Freq = rdr["frequency"].ToString();
                    if (rdr["band"] != null) q.Band = rdr["band"].ToString();
                    if (rdr["my_callsign"] != null) q.MyCall = rdr["my_callsign"].ToString();
                    if (rdr["operator"] != null) q.Operator = rdr["operator"].ToString();
                    if (rdr["my_square"] != null) q.STX = rdr["my_square"].ToString();
                    if (rdr["my_locator"] != null) q.MyLocator = rdr["my_locator"].ToString();
                    if (rdr["dx_locator"] != null) q.DXLocator = rdr["dx_locator"].ToString();
                    if (rdr["rst_rcvd"] != null) q.RST_RCVD = rdr["rst_rcvd"].ToString();
                    if (rdr["rst_sent"] != null) q.RST_SENT = rdr["rst_sent"].ToString();
                    if (rdr["name"] != null) q.Name = rdr["name"].ToString();
                    if (rdr["country"] != null) q.Country = rdr["country"].ToString();
                    if (rdr["continent"] != null) q.Continent = rdr["continent"].ToString();
                    if (rdr["cq_zone"] != null) q.CQZone = rdr["cq_zone"].ToString();
                    if (rdr["itu_zone"] != null) q.ITUZone = rdr["itu_zone"].ToString();
                    if (rdr["state"] != null) q.State = rdr["state"].ToString();
                    if (rdr["time"] != null) q.Time = rdr["time"].ToString();
                    if (rdr["date"] != null) q.Date = rdr["date"].ToString();
                    if (rdr["prop_mode"] != null) q.PROP_MODE = rdr["prop_mode"].ToString();
                    if (rdr["sat_name"] != null) q.SAT_NAME = rdr["sat_name"].ToString();
                    if (rdr["soapbox"] != null) q.SOAPBOX = rdr["soapbox"].ToString();
                    ReadActivityFields(rdr, q);
                    if (rdr["log_name"] != DBNull.Value) q.LogName = rdr["log_name"].ToString();
                    q.LotwStatus = 0;
                    list.Add(q);
                }
            }
            return list;
            }
        }

        // Number of QSOs with date >= fromDate (yyyyMMdd). Used to preview queue size before reset.
        public int GetQsoCountFromDate(string fromDate)
        {
            lock (_dbLock)
            {
            if (con == null || con.State != ConnectionState.Open) return 0;
            using (var cmd = new SQLiteCommand("SELECT count(Id) FROM qso WHERE date >= @d", con))
            {
                cmd.Parameters.Add(new SQLiteParameter("@d", fromDate));
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
            }
        }

        // Number of QSOs still waiting to be uploaded to LoTW.
        public int GetPendingLotwCount()
        {
            lock (_dbLock)
            {
            if (con == null || con.State != ConnectionState.Open) return 0;
            using (SQLiteCommand cmd = new SQLiteCommand("SELECT count(Id) FROM qso WHERE lotw_status = 0", con))
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
        }

        // Updates the LoTW upload state of a single QSO (0 pending, 1 uploaded, 2 rejected).
        public void SetLotwStatus(int id, int status)
        {
            lock (_dbLock)
            {
            if (con == null || con.State != ConnectionState.Open) return;
            using (SQLiteCommand cmd = new SQLiteCommand("UPDATE qso SET lotw_status = @s WHERE Id = @id", con))
            {
                cmd.Parameters.Add(new SQLiteParameter("@s", status));
                cmd.Parameters.Add(new SQLiteParameter("@id", id));
                cmd.ExecuteNonQuery();
            }
            }
        }

        // Resets lotw_status to 0 (pending) for all QSOs on or after the given date string
        // (format "YYYY-MM-DD"). Returns the number of rows affected.
        public int ResetLotwStatusFromDate(string fromDate)
        {
            lock (_dbLock)
            {
            if (con == null || con.State != ConnectionState.Open) return 0;
            using (SQLiteCommand cmd = new SQLiteCommand("UPDATE qso SET lotw_status = 0 WHERE date >= @d", con))
            {
                cmd.Parameters.Add(new SQLiteParameter("@d", fromDate));
                return cmd.ExecuteNonQuery();
            }
            }
        }

        // Dismisses all pending QSOs from the LoTW upload queue (lotw_status 0→2). Uses status 2
        // ("dismissed / will not upload") rather than 1 ("confirmed sent") so cleared QSOs are never
        // falsely counted as having been uploaded. Returns the number dismissed.
        public int ClearLotwQueue()
        {
            lock (_dbLock)
            {
            if (con == null || con.State != ConnectionState.Open) return 0;
            using (SQLiteCommand cmd = new SQLiteCommand("UPDATE qso SET lotw_status = 2 WHERE lotw_status = 0", con))
                return cmd.ExecuteNonQuery();
            }
        }

        // Removes all pending eQSL QSOs from the upload queue (only those whose callsign has an eQSL
        // account configured, matching the count shown in the Tools menu). Uses status 2 ("dismissed")
        // rather than 1 ("confirmed sent"). Returns how many were cleared.
        public int ClearEqslQueue()
        {
            lock (_dbLock)
            {
            if (con == null || con.State != ConnectionState.Open) return 0;
            using (SQLiteCommand cmd = new SQLiteCommand(
                "UPDATE qso SET eqsl_status = 2 WHERE eqsl_status = 0 AND my_callsign IN (SELECT callsign FROM eqsl_accounts)", con))
                return cmd.ExecuteNonQuery();
            }
        }

        // Removes all pending QRZ Logbook QSOs from the upload queue. Uses status 2 ("dismissed")
        // rather than 1 ("confirmed sent"). Returns how many were cleared.
        public int ClearQrzQueue()
        {
            lock (_dbLock)
            {
            if (con == null || con.State != ConnectionState.Open) return 0;
            using (SQLiteCommand cmd = new SQLiteCommand("UPDATE qso SET qrz_status = 2 WHERE qrz_status = 0", con))
                return cmd.ExecuteNonQuery();
            }
        }

        // Removes all pending Club Log QSOs from the upload queue. Uses status 2 ("dismissed") rather
        // than 1 ("confirmed sent"). Returns how many were cleared.
        public int ClearClublogQueue()
        {
            lock (_dbLock)
            {
            if (con == null || con.State != ConnectionState.Open) return 0;
            using (SQLiteCommand cmd = new SQLiteCommand("UPDATE qso SET clublog_status = 2 WHERE clublog_status = 0", con))
                return cmd.ExecuteNonQuery();
            }
        }

        // Returns QSOs dismissed from the Club Log queue (clublog_status = 2) — cleared or rejected,
        // not uploaded. Mirrors GetDismissedQrzQsos.
        public List<QSO> GetDismissedClublogQsos()
        {
            lock (_dbLock)
            {
            var list = new List<QSO>();
            if (con == null || con.State != ConnectionState.Open) return list;
            string stm = "SELECT Id, date, time, dx_callsign, band, mode, frequency, (SELECT name FROM logs WHERE logs.Id = qso.log_id) AS log_name FROM qso WHERE clublog_status = 2 ORDER BY date DESC, time DESC, Id DESC";
            using (var cmd = new SQLiteCommand(stm, con))
            using (var rdr = cmd.ExecuteReader())
            {
                while (rdr.Read())
                {
                    var q = new QSO();
                    q.id = Convert.ToInt32(rdr["Id"]);
                    q.Date = rdr["date"]?.ToString() ?? string.Empty;
                    q.Time = rdr["time"]?.ToString() ?? string.Empty;
                    q.DXCall = rdr["dx_callsign"]?.ToString() ?? string.Empty;
                    q.Band = rdr["band"]?.ToString() ?? string.Empty;
                    q.Mode = rdr["mode"]?.ToString() ?? string.Empty;
                    q.Freq = rdr["frequency"]?.ToString() ?? string.Empty;
                    q.LogName = rdr["log_name"] == DBNull.Value ? string.Empty : rdr["log_name"].ToString();
                    q.ClublogStatus = 2;
                    list.Add(q);
                }
            }
            return list;
            }
        }

        // Puts every dismissed Club Log QSO (clublog_status = 2) back into the pending queue (0).
        public int RequeueAllClublogDismissed()
        {
            lock (_dbLock)
            {
            if (con == null || con.State != ConnectionState.Open) return 0;
            using (var cmd = new SQLiteCommand("UPDATE qso SET clublog_status = 0 WHERE clublog_status = 2", con))
                return cmd.ExecuteNonQuery();
            }
        }

        // Returns QSOs dismissed from the LoTW queue (lotw_status = 2) — sent to the queue at some
        // point but explicitly cleared without being uploaded.
        public List<QSO> GetDismissedLotwQsos()
        {
            lock (_dbLock)
            {
            var list = new List<QSO>();
            if (con == null || con.State != ConnectionState.Open) return list;
            string stm = "SELECT Id, date, time, dx_callsign, band, mode, frequency, (SELECT name FROM logs WHERE logs.Id = qso.log_id) AS log_name FROM qso WHERE lotw_status = 2 ORDER BY date DESC, time DESC, Id DESC";
            using (var cmd = new SQLiteCommand(stm, con))
            using (var rdr = cmd.ExecuteReader())
            {
                while (rdr.Read())
                {
                    var q = new QSO();
                    q.id = Convert.ToInt32(rdr["Id"]);
                    q.Date = rdr["date"]?.ToString() ?? string.Empty;
                    q.Time = rdr["time"]?.ToString() ?? string.Empty;
                    q.DXCall = rdr["dx_callsign"]?.ToString() ?? string.Empty;
                    q.Band = rdr["band"]?.ToString() ?? string.Empty;
                    q.Mode = rdr["mode"]?.ToString() ?? string.Empty;
                    q.Freq = rdr["frequency"]?.ToString() ?? string.Empty;
                    q.LogName = rdr["log_name"] == DBNull.Value ? string.Empty : rdr["log_name"].ToString();
                    q.LotwStatus = 2;
                    list.Add(q);
                }
            }
            return list;
            }
        }

        public int RequeueAllLotwDismissed()
        {
            lock (_dbLock)
            {
            if (con == null || con.State != ConnectionState.Open) return 0;
            using (var cmd = new SQLiteCommand("UPDATE qso SET lotw_status = 0 WHERE lotw_status = 2", con))
                return cmd.ExecuteNonQuery();
            }
        }

        public List<QSO> GetDismissedEqslQsos()
        {
            lock (_dbLock)
            {
            var list = new List<QSO>();
            if (con == null || con.State != ConnectionState.Open) return list;
            string stm = "SELECT Id, date, time, dx_callsign, band, mode, frequency, (SELECT name FROM logs WHERE logs.Id = qso.log_id) AS log_name FROM qso WHERE eqsl_status = 2 AND my_callsign IN (SELECT callsign FROM eqsl_accounts) ORDER BY date DESC, time DESC, Id DESC";
            using (var cmd = new SQLiteCommand(stm, con))
            using (var rdr = cmd.ExecuteReader())
            {
                while (rdr.Read())
                {
                    var q = new QSO();
                    q.id = Convert.ToInt32(rdr["Id"]);
                    q.Date = rdr["date"]?.ToString() ?? string.Empty;
                    q.Time = rdr["time"]?.ToString() ?? string.Empty;
                    q.DXCall = rdr["dx_callsign"]?.ToString() ?? string.Empty;
                    q.Band = rdr["band"]?.ToString() ?? string.Empty;
                    q.Mode = rdr["mode"]?.ToString() ?? string.Empty;
                    q.Freq = rdr["frequency"]?.ToString() ?? string.Empty;
                    q.LogName = rdr["log_name"] == DBNull.Value ? string.Empty : rdr["log_name"].ToString();
                    q.EqslStatus = 2;
                    list.Add(q);
                }
            }
            return list;
            }
        }

        public int RequeueAllEqslDismissed()
        {
            lock (_dbLock)
            {
            if (con == null || con.State != ConnectionState.Open) return 0;
            using (var cmd = new SQLiteCommand("UPDATE qso SET eqsl_status = 0 WHERE eqsl_status = 2", con))
                return cmd.ExecuteNonQuery();
            }
        }

        public List<QSO> GetDismissedQrzQsos()
        {
            lock (_dbLock)
            {
            var list = new List<QSO>();
            if (con == null || con.State != ConnectionState.Open) return list;
            string stm = "SELECT Id, date, time, dx_callsign, band, mode, frequency, (SELECT name FROM logs WHERE logs.Id = qso.log_id) AS log_name FROM qso WHERE qrz_status = 2 ORDER BY date DESC, time DESC, Id DESC";
            using (var cmd = new SQLiteCommand(stm, con))
            using (var rdr = cmd.ExecuteReader())
            {
                while (rdr.Read())
                {
                    var q = new QSO();
                    q.id = Convert.ToInt32(rdr["Id"]);
                    q.Date = rdr["date"]?.ToString() ?? string.Empty;
                    q.Time = rdr["time"]?.ToString() ?? string.Empty;
                    q.DXCall = rdr["dx_callsign"]?.ToString() ?? string.Empty;
                    q.Band = rdr["band"]?.ToString() ?? string.Empty;
                    q.Mode = rdr["mode"]?.ToString() ?? string.Empty;
                    q.Freq = rdr["frequency"]?.ToString() ?? string.Empty;
                    q.LogName = rdr["log_name"] == DBNull.Value ? string.Empty : rdr["log_name"].ToString();
                    q.QrzStatus = 2;
                    list.Add(q);
                }
            }
            return list;
            }
        }

        public int RequeueAllQrzDismissed()
        {
            lock (_dbLock)
            {
            if (con == null || con.State != ConnectionState.Open) return 0;
            using (var cmd = new SQLiteCommand("UPDATE qso SET qrz_status = 0 WHERE qrz_status = 2", con))
                return cmd.ExecuteNonQuery();
            }
        }

        // Creates the eqsl_accounts table the first time. The table is managed entirely by hand in
        // Options -> eQSL Service; nothing is ever added automatically.
        private void EnsureEqslAccountsTable()
        {
            if (TableExists("eqsl_accounts")) return;

            string sql = @"
            CREATE TABLE [eqsl_accounts] (
                [Id] INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL
            , [callsign] nvarchar(100) NOT NULL UNIQUE COLLATE NOCASE
            , [username] nvarchar(100) NULL COLLATE NOCASE
            , [password] nvarchar(255) NULL
            );";
            using (var cmd = new SQLiteCommand(sql, con))
                cmd.ExecuteNonQuery();
        }

        // TRY AGAIN: the stations the operator meant to come back to. Filled from the cluster's
        // right-click menu, worked off in the Try Again window. Its own table, not part of a log,
        // because it is not log data - it is a note to self, and it outlives the log that was open
        // when the spot was seen. CREATE TABLE IF NOT EXISTS, never DROP: the list the operator
        // built up must survive every future schema change.
        private void EnsureTryAgainTable()
        {
            try
            {
                using (var cmd = new SQLiteCommand(
                    "CREATE TABLE IF NOT EXISTS [try_again] (" +
                    "[Id] INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL, " +
                    "[dx_callsign] nvarchar(50) NOT NULL COLLATE NOCASE, " +
                    // The stroke-suffix-free identity (4Z5SL/M -> 4Z5SL), stored rather than computed
                    // so "he is in the log now, drop him from the list" is one DELETE instead of a
                    // read-every-row-and-compare.
                    "[call_base] nvarchar(50) NOT NULL COLLATE NOCASE, " +
                    "[freq_text] nvarchar(30) NULL, " +
                    "[mode] nvarchar(20) NULL, " +
                    // The band NAME as the cluster itself worked it out ("20M", "40M"). Stored rather
                    // than derived back out of the frequency, so the colour this row is painted is the
                    // colour that very spot wore in the cluster - no second opinion, no rounding at a
                    // band edge, and no question of whether the frequency was written in kHz or MHz.
                    "[band] nvarchar(20) NULL, " +
                    "[added_utc] nvarchar(20) NULL)", con))
                    cmd.ExecuteNonQuery();

                // For a list created before the band was kept. Those rows simply have no band and are
                // painted in the ordinary text colour, which is what GetBandBrush does with a blank.
                AddColToTable("try_again", "band", "nvarchar(20) NULL");
            }
            catch (Exception ex) { Log.Swallow(ex); }
        }

        // Every station waiting to be tried, NEWEST FIRST - the last spot sent over is the one the
        // operator is most likely to want, so it is the one at the top.
        // added_utc is stored as "yyyyMMdd HHmmss", which sorts correctly as plain text. Id breaks the
        // tie for two entries added inside the same second.
        public List<TryAgainEntry> GetTryAgainList()
        {
            lock (_dbLock)
            {
            var list = new List<TryAgainEntry>();
            if (con == null || con.State != ConnectionState.Open) return list;
            try
            {
                using (var cmd = new SQLiteCommand(
                    "SELECT Id, dx_callsign, freq_text, mode, band, added_utc FROM try_again " +
                    "ORDER BY added_utc DESC, Id DESC", con))
                using (var rdr = cmd.ExecuteReader())
                {
                    while (rdr.Read())
                    {
                        list.Add(new TryAgainEntry
                        {
                            Id = rdr["Id"] == DBNull.Value ? 0 : Convert.ToInt64(rdr["Id"]),
                            DXCallsign = rdr["dx_callsign"] == DBNull.Value ? string.Empty : rdr["dx_callsign"].ToString(),
                            FreqText = rdr["freq_text"] == DBNull.Value ? string.Empty : rdr["freq_text"].ToString(),
                            Mode = rdr["mode"] == DBNull.Value ? string.Empty : rdr["mode"].ToString(),
                            Band = rdr["band"] == DBNull.Value ? string.Empty : rdr["band"].ToString(),
                            AddedUtc = rdr["added_utc"] == DBNull.Value ? string.Empty : rdr["added_utc"].ToString()
                        });
                    }
                }
            }
            catch (Exception ex) { Log.Swallow(ex); }
            return list;
            }
        }

        // Adds a station to the Try Again list. The SAME station on the SAME frequency in the SAME
        // mode is one entry however many times the operator sends it over - a spot re-posted every
        // few minutes would otherwise fill the list with copies of one station. The same station on
        // another band IS a separate entry: it is a different thing to try.
        // Returns false when the entry was already there.
        public bool AddTryAgain(string callsign, string freqText, string mode, string band)
        {
            lock (_dbLock)
            {
            string call = (callsign ?? string.Empty).Trim().ToUpperInvariant();
            if (call.Length == 0 || con == null || con.State != ConnectionState.Open) return false;

            string freq = (freqText ?? string.Empty).Trim();
            string md = (mode ?? string.Empty).Trim().ToUpperInvariant();
            try
            {
                using (var dup = new SQLiteCommand(
                    "SELECT count(*) FROM try_again WHERE dx_callsign = @c COLLATE NOCASE " +
                    "AND IFNULL(freq_text,'') = @f AND IFNULL(mode,'') = @m COLLATE NOCASE", con))
                {
                    dup.Parameters.Add(new SQLiteParameter("@c", call));
                    dup.Parameters.Add(new SQLiteParameter("@f", freq));
                    dup.Parameters.Add(new SQLiteParameter("@m", md));
                    if (Convert.ToInt32(dup.ExecuteScalar()) > 0) return false;
                }

                using (var cmd = new SQLiteCommand(
                    "INSERT INTO try_again (dx_callsign, call_base, freq_text, mode, band, added_utc) " +
                    "VALUES (@c, @b, @f, @m, @bd, @t)", con))
                {
                    cmd.Parameters.Add(new SQLiteParameter("@c", call));
                    cmd.Parameters.Add(new SQLiteParameter("@b", CallsignIdentity.Base(call).ToUpperInvariant()));
                    cmd.Parameters.Add(new SQLiteParameter("@f", freq));
                    cmd.Parameters.Add(new SQLiteParameter("@m", md));
                    cmd.Parameters.Add(new SQLiteParameter("@bd", (band ?? string.Empty).Trim()));
                    cmd.Parameters.Add(new SQLiteParameter("@t", DateTime.UtcNow.ToString("yyyyMMdd HHmmss")));
                    cmd.ExecuteNonQuery();
                }
                return true;
            }
            catch (Exception ex) { Log.Swallow(ex); return false; }
            }
        }

        // Removes one entry, by the row the operator right-clicked.
        public void RemoveTryAgain(long id)
        {
            lock (_dbLock)
            {
            if (id <= 0 || con == null || con.State != ConnectionState.Open) return;
            try
            {
                using (var cmd = new SQLiteCommand("DELETE FROM try_again WHERE Id = @id", con))
                {
                    cmd.Parameters.Add(new SQLiteParameter("@id", id));
                    cmd.ExecuteNonQuery();
                }
            }
            catch (Exception ex) { Log.Swallow(ex); }
            }
        }

        // Drops a station from the list because he is now in the log. Matched on the stroke-free
        // identity, so logging 4Z5SL/M clears an entry that says 4Z5SL. EVERY entry for that station
        // goes, on whatever band or mode - the operator asked for the row to leave when the callsign
        // is worked, not when that exact frequency is worked.
        // Returns how many entries were removed, so the caller knows whether to refresh the window.
        public int RemoveTryAgainForCallsign(string callsign)
        {
            lock (_dbLock)
            {
            string b = CallsignIdentity.Base((callsign ?? string.Empty).Trim()).ToUpperInvariant();
            if (b.Length == 0 || con == null || con.State != ConnectionState.Open) return 0;
            try
            {
                using (var cmd = new SQLiteCommand("DELETE FROM try_again WHERE call_base = @b COLLATE NOCASE", con))
                {
                    cmd.Parameters.Add(new SQLiteParameter("@b", b));
                    return cmd.ExecuteNonQuery();
                }
            }
            catch (Exception ex) { Log.Swallow(ex); return 0; }
            }
        }

        // How many stations are waiting. The main window's Try Again button is hidden while this is 0.
        public int GetTryAgainCount()
        {
            lock (_dbLock)
            {
            if (con == null || con.State != ConnectionState.Open) return 0;
            try
            {
                using (var cmd = new SQLiteCommand("SELECT count(Id) FROM try_again", con))
                    return Convert.ToInt32(cmd.ExecuteScalar());
            }
            catch (Exception ex) { Log.Swallow(ex); return 0; }
            }
        }

        private static EqslAccount ReadEqslAccount(SQLiteDataReader rdr)
        {
            return new EqslAccount
            {
                Id = rdr["Id"] == DBNull.Value ? 0 : Convert.ToInt32(rdr["Id"]),
                Callsign = rdr["callsign"] == DBNull.Value ? string.Empty : rdr["callsign"].ToString(),
                Username = rdr["username"] == DBNull.Value ? string.Empty : rdr["username"].ToString(),
                Password = rdr["password"] == DBNull.Value ? string.Empty : rdr["password"].ToString()
            };
        }

        // Returns all eQSL accounts (one row per station callsign), callsign ascending.
        public List<EqslAccount> GetEqslAccounts()
        {
            lock (_dbLock)
            {
            var list = new List<EqslAccount>();
            if (con == null || con.State != ConnectionState.Open) return list;
            using (var cmd = new SQLiteCommand("SELECT Id, callsign, username, password FROM eqsl_accounts ORDER BY callsign ASC", con))
            using (var rdr = cmd.ExecuteReader())
            {
                while (rdr.Read())
                    list.Add(ReadEqslAccount(rdr));
            }
            return list;
            }
        }

        // Returns the eQSL account for a station callsign, or null if there is no row for it.
        public EqslAccount GetEqslAccount(string callsign)
        {
            lock (_dbLock)
            {
            if (string.IsNullOrWhiteSpace(callsign) || con == null || con.State != ConnectionState.Open) return null;
            using (var cmd = new SQLiteCommand("SELECT Id, callsign, username, password FROM eqsl_accounts WHERE callsign = @c COLLATE NOCASE LIMIT 1", con))
            {
                cmd.Parameters.Add(new SQLiteParameter("@c", callsign.Trim()));
                using (var rdr = cmd.ExecuteReader())
                {
                    if (rdr.Read()) return ReadEqslAccount(rdr);
                }
            }
            return null;
            }
        }

        // Inserts (Id == 0) or updates (by Id) an eQSL account row. The row is keyed by its Id so the
        // callsign itself can be edited. Returns false (with an error message) if the callsign is
        // blank or already used by a different row. On a successful insert, account.Id is filled in.
        public bool SaveEqslAccount(EqslAccount account, out string error)
        {
            lock (_dbLock)
            {
            error = null;
            if (account == null) { error = "No account."; return false; }
            if (con == null || con.State != ConnectionState.Open) { error = "Database not available."; return false; }

            string callsign = (account.Callsign ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(callsign)) { error = "Station callsign cannot be empty."; return false; }

            // Reject a callsign already used by another row.
            using (var dup = new SQLiteCommand("SELECT count(*) FROM eqsl_accounts WHERE callsign = @c COLLATE NOCASE AND Id <> @id", con))
            {
                dup.Parameters.Add(new SQLiteParameter("@c", callsign));
                dup.Parameters.Add(new SQLiteParameter("@id", account.Id));
                if (Convert.ToInt32(dup.ExecuteScalar()) > 0)
                {
                    error = "The callsign " + callsign + " is already in the table.";
                    return false;
                }
            }

            if (account.Id == 0)
            {
                using (var cmd = new SQLiteCommand("INSERT INTO eqsl_accounts (callsign, username, password) VALUES (@c,@u,@p)", con))
                {
                    cmd.Parameters.Add(new SQLiteParameter("@c", callsign));
                    cmd.Parameters.Add(new SQLiteParameter("@u", (object)(account.Username ?? string.Empty)));
                    cmd.Parameters.Add(new SQLiteParameter("@p", (object)(account.Password ?? string.Empty)));
                    cmd.ExecuteNonQuery();
                }
                using (var idCmd = new SQLiteCommand("SELECT last_insert_rowid()", con))
                    account.Id = Convert.ToInt32(idCmd.ExecuteScalar());
            }
            else
            {
                using (var cmd = new SQLiteCommand("UPDATE eqsl_accounts SET callsign = @c, username = @u, password = @p WHERE Id = @id", con))
                {
                    cmd.Parameters.Add(new SQLiteParameter("@c", callsign));
                    cmd.Parameters.Add(new SQLiteParameter("@u", (object)(account.Username ?? string.Empty)));
                    cmd.Parameters.Add(new SQLiteParameter("@p", (object)(account.Password ?? string.Empty)));
                    cmd.Parameters.Add(new SQLiteParameter("@id", account.Id));
                    cmd.ExecuteNonQuery();
                }
            }
            account.Callsign = callsign;
            return true;
            }
        }

        // Removes an eQSL account row by its Id (used by the "Remove" button).
        public void DeleteEqslAccount(int id)
        {
            lock (_dbLock)
            {
            if (id <= 0 || con == null || con.State != ConnectionState.Open) return;
            using (var cmd = new SQLiteCommand("DELETE FROM eqsl_accounts WHERE Id = @id", con))
            {
                cmd.Parameters.Add(new SQLiteParameter("@id", id));
                cmd.ExecuteNonQuery();
            }
            }
        }

        // Function to check if a table exists in the database
        bool TableExists(string tableName)
        {
            using (var command = new SQLiteCommand($"SELECT name FROM sqlite_master WHERE type='table' AND name='{tableName}'", con))
            {
                using (var reader = command.ExecuteReader())
                {
                    return reader.HasRows;
                }
            }
        }

        // Bands where a stored number of 1000 or more can only be kHz, never MHz. 13CM is deliberately
        // absent: 2400 is a plausible reading BOTH as kHz and as MHz (2.4 GHz), so those rows are left
        // alone rather than silently reinterpreted. Same for anything above 70CM.
        private static readonly HashSet<string> UnambiguousKhzBands =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "160M", "80M", "60M", "40M", "30M", "20M", "17M", "15M", "12M", "10M", "6M", "4M", "2M", "70CM" };

        // One QSO whose frequency looks like kHz, and what it would become in MHz.
        public class FreqFix
        {
            public long Id { get; set; }
            public string Callsign { get; set; }
            public string Date { get; set; }
            public string Time { get; set; }
            public string Band { get; set; }
            public string OldFreq { get; set; }
            public string NewFreq { get; set; }
        }

        // Finds QSOs whose frequency was stored in kHz ("14200") and should read MHz ("14.200000").
        // Finds only - nothing is written. The caller shows the operator what would change and applies
        // it with ApplyFrequencyFixes only if they agree.
        //
        // convertBandToFreq used to hand back kHz while the rest of the program speaks MHz, so every
        // QSO logged with a band but no frequency got a value a thousand times too high. It shows in
        // the log grid next to proper MHz values, and went out in ADIF uploads as a frequency thousands
        // of MHz outside its own band.
        //
        // Deliberately timid, because this is the operator's logged data:
        //   * the QSO must already carry a band, and that band must be one where kHz is unambiguous;
        //   * dividing by 1000 must land the QSO inside that same band.
        // Anything else - a band-less row, an odd value, a microwave contact - is never offered.
        public List<FreqFix> FindKhzFrequencyFixes()
        {
            var fixes = new List<FreqFix>();
            try
            {
                using (var read = new SQLiteCommand(
                    "SELECT Id, dx_callsign, date, time, band, frequency FROM qso " +
                    "WHERE CAST(frequency AS REAL) >= 1000 ORDER BY date, time", con))
                using (var rdr = read.ExecuteReader())
                {
                    while (rdr.Read())
                    {
                        string band = rdr["band"]?.ToString()?.Trim();
                        string freq = rdr["frequency"]?.ToString()?.Trim();
                        if (string.IsNullOrWhiteSpace(band) || string.IsNullOrWhiteSpace(freq)) continue;
                        if (!UnambiguousKhzBands.Contains(band)) continue;

                        string mhz = HolyParser.HolyLogParser.NormalizeFreqToMhz(freq);
                        if (string.IsNullOrWhiteSpace(mhz)) continue;

                        // The rewritten value has to agree with the band the operator logged.
                        if (!string.Equals(HolyParser.HolyLogParser.convertFreqToBand(mhz), band,
                                           StringComparison.OrdinalIgnoreCase)) continue;

                        // Stored with six decimals ("14.200000") to match how frequencies already look
                        // in the log, so a repaired row is indistinguishable from an untouched one in
                        // the grid. The trimmed form the normaliser returns is only for ADIF output.
                        double value;
                        if (!double.TryParse(mhz, System.Globalization.NumberStyles.Float,
                                             System.Globalization.CultureInfo.InvariantCulture, out value))
                            continue;

                        fixes.Add(new FreqFix
                        {
                            Id = Convert.ToInt64(rdr["Id"]),
                            Callsign = rdr["dx_callsign"]?.ToString() ?? string.Empty,
                            Date = rdr["date"]?.ToString() ?? string.Empty,
                            Time = rdr["time"]?.ToString() ?? string.Empty,
                            Band = band,
                            OldFreq = freq,
                            NewFreq = value.ToString("0.000000",
                                                     System.Globalization.CultureInfo.InvariantCulture)
                        });
                    }
                }
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
            return fixes;
        }

        // Writes the frequencies found by FindKhzFrequencyFixes. Returns how many rows were changed.
        public int ApplyFrequencyFixes(IList<FreqFix> fixes)
        {
            if (fixes == null || fixes.Count == 0) return 0;
            try
            {
                using (var tx = con.BeginTransaction())
                {
                    using (var upd = new SQLiteCommand("UPDATE qso SET frequency = @f WHERE Id = @id", con, tx))
                    {
                        upd.Parameters.Add(new SQLiteParameter("@f"));
                        upd.Parameters.Add(new SQLiteParameter("@id"));
                        foreach (var fix in fixes)
                        {
                            upd.Parameters["@f"].Value = fix.NewFreq;
                            upd.Parameters["@id"].Value = fix.Id;
                            upd.ExecuteNonQuery();
                        }
                    }
                    tx.Commit();
                }
                Log.Warn($"Frequency repair: {fixes.Count} QSO(s) stored in kHz were rewritten as MHz.");
                return fixes.Count;
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); return 0; }
        }

        private void UpdateSchema()
        {
            string createTable_qso = @"
            CREATE TABLE [qso] (
                [Id] INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL
            , [my_callsign] nvarchar(100) NOT NULL COLLATE NOCASE
            , [operator] nvarchar(100) NULL COLLATE NOCASE
            , [my_square] nvarchar(100) NULL COLLATE NOCASE
            , [my_locator] nvarchar(100) NULL COLLATE NOCASE
            , [dx_locator] nvarchar(100) NULL COLLATE NOCASE
            , [frequency] nvarchar(100) NULL COLLATE NOCASE
            , [band] nvarchar(100) NOT NULL COLLATE NOCASE
            , [dx_callsign] nvarchar(100) NOT NULL COLLATE NOCASE
            , [rst_rcvd] nvarchar(100) NULL COLLATE NOCASE
            , [rst_sent] nvarchar(100) NULL COLLATE NOCASE
            , [date] nvarchar(100) NOT NULL COLLATE NOCASE
            , [time] nvarchar(100) NOT NULL COLLATE NOCASE
            , [mode] nvarchar(100) NOT NULL COLLATE NOCASE
            , [submode] nvarchar(100) NULL COLLATE NOCASE
            , [exchange] nvarchar(100) NULL COLLATE NOCASE
            , [comment] nvarchar(500) NULL COLLATE NOCASE
            , [name] nvarchar(500) NULL COLLATE NOCASE
            , [country] nvarchar(100) NULL COLLATE NOCASE
            , [continent] nvarchar(100) NULL COLLATE NOCASE
            , [cq_zone] nvarchar(10) NULL COLLATE NOCASE
            , [itu_zone] nvarchar(10) NULL COLLATE NOCASE
            , [state] nvarchar(20) NULL COLLATE NOCASE
            , [qth] nvarchar(100) NULL COLLATE NOCASE
            , [dxcc] INTEGER NULL
            , [prop_mode] nvarchar(100) NULL COLLATE NOCASE
            , [sat_name] nvarchar(100) NULL COLLATE NOCASE
            , [soapbox] nvarchar(100) NULL COLLATE NOCASE
            , [iota] nvarchar(20) NULL COLLATE NOCASE
            , [sota_ref] nvarchar(30) NULL COLLATE NOCASE
            , [pota_ref] nvarchar(100) NULL COLLATE NOCASE
            , [wwff_ref] nvarchar(30) NULL COLLATE NOCASE
            , [sig] nvarchar(50) NULL COLLATE NOCASE
            , [sig_info] nvarchar(100) NULL COLLATE NOCASE
            , [credit_granted] nvarchar(200) NULL COLLATE NOCASE
            , [cnty] nvarchar(100) NULL COLLATE NOCASE
            , [qsl_via] nvarchar(100) NULL COLLATE NOCASE
            , [qsl_rdate] nvarchar(20) NULL COLLATE NOCASE
            , [qsl_sent] nvarchar(10) NULL COLLATE NOCASE
            , [contest_id] nvarchar(50) NULL COLLATE NOCASE
            , [time_off] nvarchar(20) NULL COLLATE NOCASE
            , [date_off] nvarchar(20) NULL COLLATE NOCASE
            , [extra_adif] text NULL
            , [eqsl_status] INTEGER NOT NULL DEFAULT 0
            , [qrz_status] INTEGER NOT NULL DEFAULT 0
            , [qrz_logid] nvarchar(50) NULL
            , [lotw_status] INTEGER NOT NULL DEFAULT 0
            , [clublog_status] INTEGER NOT NULL DEFAULT 0
            );";

            string createTable_categories = @"
            DROP TABLE IF EXISTS[categories];
            CREATE TABLE [categories] (
                [Id] INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL
            , [name] nvarchar(100) NOT NULL COLLATE NOCASE
            , [description] nvarchar(100) NOT NULL COLLATE NOCASE
            , [event_id] bigint NOT NULL
            );
            INSERT INTO [categories] ([Id],[name],[description],[event_id]) VALUES (
            1,'','NONE',1);
            INSERT INTO [categories] ([Id],[name],[description],[event_id]) VALUES (
            2,'POR','Portable (1 Square)',1);
            INSERT INTO [categories] ([Id],[name],[description],[event_id]) VALUES (
            3,'M5','Mobile 5 (5 Squares)',1);
            INSERT INTO [categories] ([Id],[name],[description],[event_id]) VALUES (
            4,'M10','Mobile 10 (10 Squares)',1);
            INSERT INTO [categories] ([Id],[name],[description],[event_id]) VALUES (
            5,'YN','YN (Under 20 / License < 3 Years)',1);";

            string createTable_radio_events = @"
            DROP TABLE IF EXISTS[radio_events];
            CREATE TABLE [radio_events] (
                [Id] INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL
            , [name] nvarchar(100) NOT NULL COLLATE NOCASE
            , [description] nvarchar(100) NOT NULL COLLATE NOCASE
            , [is_categories] INTEGER NOT NULL COLLATE NOCASE
            );
            INSERT INTO [radio_events] ([Id],[name],[description],[is_categories]) VALUES (
            1,'holyland','Holyland Contest',1);
            INSERT INTO [radio_events] ([Id],[name],[description],[is_categories]) VALUES (
            2,'sukot','Sukot',1);
            INSERT INTO [radio_events] ([Id],[name],[description],[is_categories]) VALUES (
            3,'iarc','IARC Event',1);";

            string createTable_bands = @"DROP TABLE IF EXISTS[bands];
            CREATE TABLE [bands] (
                [Id] INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL
            , [name] nvarchar(100) NOT NULL COLLATE NOCASE
            , [description] nvarchar(100) NOT NULL COLLATE NOCASE
            , [event_id] bigint NOT NULL
            );
            INSERT INTO [bands] ([Id],[name],[description],[event_id]) VALUES (
            1,'ALL','ALL',1);
            INSERT INTO [bands] ([Id],[name],[description],[event_id]) VALUES (
            2,'10','10M',1);
            INSERT INTO [bands] ([Id],[name],[description],[event_id]) VALUES (
            3,'15','15M',1);
            INSERT INTO [bands] ([Id],[name],[description],[event_id]) VALUES (
            4,'20','20M',1);
            INSERT INTO [bands] ([Id],[name],[description],[event_id]) VALUES (
            5,'40','40M',1);
            INSERT INTO [bands] ([Id],[name],[description],[event_id]) VALUES (
            6,'80','80M',1);";

            string createTable_operators = @"DROP TABLE IF EXISTS[operators];
            CREATE TABLE [operators] (
                [Id] INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL
            , [name] nvarchar(100) NOT NULL COLLATE NOCASE
            , [description] nvarchar(100) NOT NULL COLLATE NOCASE
            , [event_id] bigint NOT NULL
            );
            INSERT INTO [operators] ([Id],[name],[description],[event_id]) VALUES (
            1,'SINGLE-OP','SINGLE-OP',1);
            INSERT INTO [operators] ([Id],[name],[description],[event_id]) VALUES (
            2,'MULTI-OP','MULTI-OP',1);
            INSERT INTO [operators] ([Id],[name],[description],[event_id]) VALUES (
            3,'CHECKLOG','CHECKLOG',1);
            INSERT INTO [operators] ([Id],[name],[description],[event_id]) VALUES (
            4,'SWL','SWL',1);";

            string createTable_power = @"DROP TABLE IF EXISTS[power];
            CREATE TABLE [power] (
                [Id] INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL
            , [name] nvarchar(100) NOT NULL COLLATE NOCASE
            , [description] nvarchar(100) NOT NULL COLLATE NOCASE
            , [event_id] bigint NOT NULL
            );
            INSERT INTO [power] ([Id],[name],[description],[event_id]) VALUES (
            1,'HIGH','High (>100W)',1);
            INSERT INTO [power] ([Id],[name],[description],[event_id]) VALUES (
            2,'LOW','Low (<100W)',1);
            INSERT INTO [power] ([Id],[name],[description],[event_id]) VALUES (
            3,'QRP','QRP(<10W)',1);";

            string createTable_modes = @"DROP TABLE IF EXISTS[modes];
            CREATE TABLE [modes] (
                [Id] INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL
            , [name] nvarchar(100) NOT NULL COLLATE NOCASE
            , [description] nvarchar(100) NOT NULL COLLATE NOCASE
            , [event_id] bigint NOT NULL
            );
            INSERT INTO [modes] ([Id],[name],[description],[event_id]) VALUES (
            1,'MIX','MIX',1);
            INSERT INTO [modes] ([Id],[name],[description],[event_id]) VALUES (
            2,'SSB','SSB',1);
            INSERT INTO [modes] ([Id],[name],[description],[event_id]) VALUES (
            3,'CW','CW',1);
            INSERT INTO [modes] ([Id],[name],[description],[event_id]) VALUES (
            4,'VHF/UHF','VHF/UHF',2);
            INSERT INTO [modes] ([Id],[name],[description],[event_id]) VALUES (
            5,'VHF','VHF',2);
            INSERT INTO [modes] ([Id],[name],[description],[event_id]) VALUES (
            6,'UHF','UHF',2);
            INSERT INTO [modes] ([Id],[name],[description],[event_id]) VALUES (
            7,'MIX','MIX',3);";

            if (!TableExists("qso"))
            {
                using (var command = new SQLiteCommand(createTable_qso, con))
                {
                    command.ExecuteNonQuery();
                }
            }
            else
            {
                AddColToTable("qso", "my_callsign", "nvarchar(100) NOT NULL");
                AddColToTable("qso", "operator", "nvarchar(100) NULL");
                AddColToTable("qso", "my_square", "nvarchar(100) NULL");
                AddColToTable("qso", "my_locator", "nvarchar(100) NULL");
                AddColToTable("qso", "dx_locator", "nvarchar(100) NULL");
                AddColToTable("qso", "dx_callsign", "nvarchar(100) NOT NULL");
                AddColToTable("qso", "prop_mode", "nvarchar(100) NULL");
                AddColToTable("qso", "sat_name", "nvarchar(100) NULL");
                AddColToTable("qso", "soapbox", "nvarchar(100) NULL");
                AddColToTable("qso", "cq_zone", "nvarchar(10) NULL");
                AddColToTable("qso", "itu_zone", "nvarchar(10) NULL");
            }
            AddColToTable("qso", "state", "nvarchar(20) NULL");   // ADIF STATE (worked station's subdivision)
            AddColToTable("qso", "qth", "nvarchar(100) NULL");    // ADIF QTH (the worked station's town)
            // THE ADIF DXCC ENTITY NUMBER. The country's identity, as opposed to its name - fixed,
            // unique, never reused, and what an award is counted on. The log held only the name.
            AddColToTable("qso", "dxcc", "INTEGER NULL");
            // Activity-program references. POTA is the wide one on purpose: ADIF allows a comma-
            // separated LIST there, because a contact can be inside two overlapping parks at once.
            AddColToTable("qso", "iota", "nvarchar(20) NULL");
            AddColToTable("qso", "sota_ref", "nvarchar(30) NULL");
            AddColToTable("qso", "pota_ref", "nvarchar(100) NULL");
            AddColToTable("qso", "wwff_ref", "nvarchar(30) NULL");
            AddColToTable("qso", "sig", "nvarchar(50) NULL");
            AddColToTable("qso", "sig_info", "nvarchar(100) NULL");
            // The operator's AWARD and QSL record, imported from whatever program they used before.
            // credit_granted is the ARRL's own list of awards granted for a QSO, and is the only thing in
            // a log that can answer "what have I actually been credited with" - a different question from
            // "what is confirmed". cnty is what USA-CA counts. The rest complete the QSL story: who the
            // card goes via, when it arrived, whether one was sent, and which contest the QSO belongs to.
            AddColToTable("qso", "credit_granted", "nvarchar(200) NULL");
            AddColToTable("qso", "cnty", "nvarchar(100) NULL");
            AddColToTable("qso", "qsl_via", "nvarchar(100) NULL");
            AddColToTable("qso", "qsl_rdate", "nvarchar(20) NULL");
            AddColToTable("qso", "qsl_sent", "nvarchar(10) NULL");
            AddColToTable("qso", "contest_id", "nvarchar(50) NULL");
            AddColToTable("qso", "time_off", "nvarchar(20) NULL");
            AddColToTable("qso", "date_off", "nvarchar(20) NULL");
            // LOSSLESS IMPORT: every ADIF field of an imported record that HolyLogger has no column of
            // its own for, kept verbatim and written back out on export. Untyped text with no length
            // limit on purpose - it holds whatever the operator's previous program wrote, which is not
            // ours to truncate. Measured on a real 28,366-QSO Log4OM log: ~900 bytes per QSO.
            AddColToTable("qso", "extra_adif", "text NULL");
            AddEqslStatusColumn();
            AddQrzColumns();
            AddLotwColumns();
            AddLotwConfirmationColumns();
            AddQrzConfirmationColumns();
            AddEqslConfirmationColumns();
            AddClublogConfirmationColumns();
            AddPaperConfirmationColumn();
            AddClublogColumn();
            AddColToTable("qso", "log_id", "INTEGER NULL");  // each QSO belongs to a named Log
            EnsureLogsTable();
            EnsureLogStateTable();
            // Real-time copy-to-log feature: a log may copy its new QSOs into another log.
            AddColToTable("logs", "copy_target_log_id", "INTEGER NULL");   // where this log's new QSOs are copied (NULL = off)
            AddColToTable("logs", "log_callsign", "nvarchar(50) NULL");    // this log's station-callsign identity
            AddColToTable("logs", "log_operator", "nvarchar(50) NULL");    // this log's operator identity
            AddColToTable("qso", "source_qso_id", "INTEGER NULL");         // on a copied QSO: Id of the original it came from
            // A contest log must never be a copy TARGET; clear any setting that points at one.
            try
            {
                using (var fixCopy = new SQLiteCommand(
                    "UPDATE logs SET copy_target_log_id = NULL WHERE copy_target_log_id IN (SELECT Id FROM logs WHERE event_type IS NOT NULL AND event_type <> '')", con))
                    fixCopy.ExecuteNonQuery();
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
            // A copy-target on a log that has no identity is meaningless (you can't even log into an
            // identity-less log). Clear those so the Log Manager never shows "copies to X" without an identity.
            try
            {
                using (var fixNoId = new SQLiteCommand(
                    "UPDATE logs SET copy_target_log_id = NULL WHERE copy_target_log_id IS NOT NULL AND " +
                    "(log_callsign IS NULL OR trim(log_callsign) = '' OR log_operator IS NULL OR trim(log_operator) = '')", con))
                    fixNoId.ExecuteNonQuery();
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
            // A copied QSO (source_qso_id set) must never be uploaded on its own -- the original handles it.
            // Earlier builds inherited the source's pending status, so copies of live QSOs sat in the upload
            // queues alongside their originals (the SAME contact queued twice). Mark every still-pending copy
            // "already handled" (1) so each contact is offered to each service exactly once (from the original).
            try
            {
                using (var fixCopies = new SQLiteCommand(
                    "UPDATE qso SET eqsl_status = 1, qrz_status = 1, lotw_status = 1, clublog_status = 1 " +
                    "WHERE source_qso_id IS NOT NULL AND " +
                    "(eqsl_status = 0 OR qrz_status = 0 OR lotw_status = 0 OR clublog_status = 0)", con))
                    fixCopies.ExecuteNonQuery();
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
            EnsureEqslAccountsTable();
            EnsureTryAgainTable();
            EnsureEqslIndexes();
            EnsureQrzIndexes();
            EnsureLotwIndex();
            EnsureClublogIndex();
            EnsureLogIdIndex();
            using (var command = new SQLiteCommand(createTable_categories, con))
            {
                command.ExecuteNonQuery();
            }
            using (var command = new SQLiteCommand(createTable_radio_events, con))
            {
                command.ExecuteNonQuery();
            }
            using (var command = new SQLiteCommand(createTable_bands, con))
            {
                command.ExecuteNonQuery();
            }
            using (var command = new SQLiteCommand(createTable_operators, con))
            {
                command.ExecuteNonQuery();
            }
            using (var command = new SQLiteCommand(createTable_power, con))
            {
                command.ExecuteNonQuery();
            }
            using (var command = new SQLiteCommand(createTable_modes, con))
            {
                command.ExecuteNonQuery();
            }
        }
    }

    // One eQSL account, keyed by the station callsign it is used for. The username is the eQSL
    // login (normally the callsign itself, but kept separate so it can differ).
    public class EqslAccount
    {
        public int Id { get; set; }   // 0 = not yet saved (a new row)
        public string Callsign { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
    }

    // A named Log and its computed stats, for the View Logs window.
    public class LogInfo
    {
        public long Id { get; set; }
        public string Name { get; set; }
        public string EventType { get; set; }   // contest name, or "" for a normal day-by-day log
        public int QsoCount { get; set; }
        public string StartDate { get; set; }   // first QSO date in the log (min)
        public string EndDate { get; set; }     // last QSO date in the log (max)
        public long? CopyTargetLogId { get; set; }  // this log copies its new QSOs into this log (null = off)
        public string Callsign { get; set; }        // this log's station-callsign identity
        public string Operator { get; set; }        // this log's operator identity
    }

    // A (station callsign, operator) pair found in a log's QSOs, with how many use it — used to pre-fill
    // and offer choices when assigning a log's permanent identity.
    public class LogIdentityCandidate
    {
        public string Callsign { get; set; }
        public string Operator { get; set; }
        public int Count { get; set; }
        public string Display => Callsign + " / " + Operator + "   (" + Count.ToString("N0") + " QSOs)";
    }

    // Station-callsign comparison for log identity. A stroke ADDED AFTER the call does not change the
    // station's identity: 4Z5SL, 4Z5SL/M, 4Z5SL/2 and 4Z5SL/P are all the same station. A stroke BEFORE
    // the call is a different station: 4X/OK1DL is OK1DL operating from Israel, not the same station
    // callsign as OK1DL at home — so a leading country prefix stays part of the identity.
    // (Not the same as Services.getBareCallsign, which strips the leading prefix too.)
    // One station on the Try Again list: who, where, and in what mode. A plain record - the window
    // shows it, the Try button hands it to the radio, and that is all it has to do. It is NOT a QSO
    // and is deliberately not one: nothing here has been worked yet.
    public class TryAgainEntry : System.ComponentModel.INotifyPropertyChanged
    {
        public long Id { get; set; }
        public string DXCallsign { get; set; }
        // Kept exactly as the cluster spotted it, so the Try button tunes to the same place the spot
        // said. TuneToClusterSpot already accepts either kHz or MHz and works out which.
        public string FreqText { get; set; }
        public string Mode { get; set; }
        // The band name the cluster gave this spot ("20M"). Kept only so the frequency can be painted
        // the band's colour, exactly as the cluster paints it.
        public string Band { get; set; }
        // Stored as "yyyyMMdd HHmmss" UTC - sortable as plain text, which is what the list is ordered on.
        public string AddedUtc { get; set; }

        // The country's flag and its name, for the callsign column. NOT columns in the try_again table
        // and deliberately so: they are worked out from the callsign each time the list is read, so a
        // later country-file update corrects an entry that was already sitting on the list, and a
        // database written on one machine does not carry another machine's image paths around with it.
        // Filled in by the Try Again window - resolving a callsign to a country is not the database's job.
        public string FlagPath { get; set; }
        public string Country { get; set; }

        // The band's colour, for the frequency text. Filled in by the window from the same
        // MainWindow.GetBandBrush the cluster's own frequency column uses, so the two agree; a brush is
        // a screen thing and has no business being worked out down here.
        public System.Windows.Media.Brush FreqBrush { get; set; }

        // THE ROW THE RIGHT-CLICK MENU IS ABOUT, and the only thing that paints a row in the Try Again
        // window. It is deliberately NOT the DataGrid's own selection: the grid selects on a left click
        // too, and a left click must leave the list looking exactly as it found it. Trying to undo the
        // grid's selection instead of replacing it meant the row was painted and then unpainted a moment
        // later, which is worse than either.
        private bool _isMarked;
        public bool IsMarked
        {
            get { return _isMarked; }
            set
            {
                if (_isMarked == value) return;
                _isMarked = value;
                var handler = PropertyChanged;
                if (handler != null) handler(this, new System.ComponentModel.PropertyChangedEventArgs("IsMarked"));
            }
        }

        // HOW LONG HE HAS BEEN WAITING, so nobody has to work it out. This replaced a column that
        // printed the clock time the station was copied in: reading "22:14" and subtracting it from the
        // wall clock is exactly the arithmetic the operator should not be doing.
        //
        // Rounded down, deliberately. "6 min" turning into "7 min" a moment early would be wrong in the
        // one direction that matters here: a spot is never fresher than it is said to be.
        //
        // ONE UNIT ONLY - minutes - and everything past the hour is "60+". Hours and days were spelled
        // out at first ("1 h 05", "2 days") and that was more precision than the answer is worth: past
        // an hour the station has gone, and how long ago he went changes nothing about what to do next.
        // A single unit also means no two rows have to be compared across units to see which is fresher.
        // "now" is the one exception, for under a minute, because "0 min" reads like a fault.
        // SPLIT IN TWO so the column can print the number in bold and the unit beside it in plain text.
        // The number is the thing being read - "39" - and "min" is the same three letters on every row,
        // which is exactly the sort of text that should stay out of the way of what changes.
        public string AgoNumber
        {
            get
            {
                TimeSpan since;
                if (!TryElapsed(out since)) return string.Empty;
                if (since.TotalMinutes < 1) return "now";
                if (since.TotalMinutes < 60) return ((int)since.TotalMinutes).ToString(CultureInfo.InvariantCulture);
                return "60+";
            }
        }

        // Blank for "now" and for "60+": neither is a count of anything, so neither takes a unit.
        public string AgoUnit
        {
            get
            {
                TimeSpan since;
                if (!TryElapsed(out since)) return string.Empty;
                return (since.TotalMinutes >= 1 && since.TotalMinutes < 60) ? " min" : string.Empty;
            }
        }

        private bool TryElapsed(out TimeSpan since)
        {
            since = TimeSpan.Zero;
            DateTime added;
            if (!DateTime.TryParseExact((AddedUtc ?? string.Empty).Trim(), "yyyyMMdd HHmmss",
                                        CultureInfo.InvariantCulture,
                                        System.Globalization.DateTimeStyles.None, out added))
                return false;

            since = DateTime.UtcNow - added;
            if (since < TimeSpan.Zero) since = TimeSpan.Zero;   // a clock that went backwards
            return true;
        }

        // Time passes whether or not anything in the database changes, so the window's clock tick calls
        // this on every row to make the Ago column re-read itself. Nothing else about the row moves.
        public void NotifyAgoChanged()
        {
            var handler = PropertyChanged;
            if (handler == null) return;
            handler(this, new System.ComponentModel.PropertyChangedEventArgs("AgoNumber"));
            handler(this, new System.ComponentModel.PropertyChangedEventArgs("AgoUnit"));
        }

        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
    }

    public static class CallsignIdentity
    {
        // A complete amateur callsign (prefix letters/digits + digit + suffix ending in a letter),
        // as opposed to a stroke modifier (M, P, QRP, 2, ...) or a bare country prefix (4X, OK, ...).
        static readonly System.Text.RegularExpressions.Regex FullCall =
            new System.Text.RegularExpressions.Regex("^[A-Za-z0-9]{1,3}[0-9][A-Za-z0-9]{0,3}[A-Za-z]$",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        // The identity base form: everything up to and including the first full-callsign segment;
        // whatever follows is a stroke modifier and is dropped. "4Z5SL/M" -> "4Z5SL",
        // "4X/OK1DL/P" -> "4X/OK1DL", "OK1DL" -> "OK1DL". Shapes with no recognizable full
        // callsign compare as typed.
        public static string Base(string callsign)
        {
            string s = (callsign ?? string.Empty).Trim();
            if (s.IndexOf('/') < 0) return s;
            string[] parts = s.Split('/');
            for (int i = 0; i < parts.Length; i++)
                if (FullCall.IsMatch(parts[i]))
                    return string.Join("/", parts, 0, i + 1);
            return s;
        }

        // True when two station callsigns are the same log identity.
        public static bool Same(string a, string b)
            => string.Equals(Base(a), Base(b), StringComparison.OrdinalIgnoreCase);

        // True when a string is shaped like an amateur callsign. Used where something that MAY be a
        // callsign has to be told apart from something that is not - a LoTW website username, for
        // instance, which the operator is free to choose and which is often not a callsign at all.
        public static bool LooksLikeCallsign(string s)
        {
            string b = Base(s);
            if (b.Length == 0) return false;
            foreach (string part in b.Split('/'))
                if (FullCall.IsMatch(part)) return true;
            return false;
        }

        // Splits a callsign into its prefix and suffix halves, cutting at the LAST digit of the base
        // callsign - the digit belongs to the prefix, being part of what identifies the country/area:
        //
        //   4Z5SL      -> "4Z5"     + "SL"
        //   4Z5SL/M    -> "4Z5"     + "SL/M"      a trailing stroke stays with the suffix
        //   4X/OK1DL   -> "4X/OK1"  + "DL"        a leading stroke stays with the prefix
        //   4X/OK1DL/P -> "4X/OK1"  + "DL/P"
        //
        // A shape with no recognisable callsign in it (a bare country prefix like "4X", or junk) is
        // returned entirely as the prefix, so nothing is invented for it.
        public static void Split(string callsign, out string prefix, out string suffix)
        {
            prefix = (callsign ?? string.Empty).Trim();
            suffix = string.Empty;
            if (prefix.Length == 0) return;

            string[] parts = prefix.Split('/');
            int baseIndex = -1;
            for (int i = 0; i < parts.Length; i++)
                if (FullCall.IsMatch(parts[i])) { baseIndex = i; break; }

            // Nothing matched the ordinary callsign shape. That is mostly special-event calls, which
            // are longer than FullCall allows (LZ1771SDG, DL40RRDXA, OE2008OHO). Falling back to the
            // last segment applies the same last-digit rule by a looser route, so those still split
            // into a usable prefix and suffix instead of landing whole in the prefix box. A segment
            // with no digit at all still drops out below and is searched exactly as typed.
            bool loose = baseIndex < 0;
            if (loose) baseIndex = parts.Length - 1;

            string lead = baseIndex > 0 ? string.Join("/", parts, 0, baseIndex) + "/" : string.Empty;
            string baseCall = parts[baseIndex];
            string tail = baseIndex < parts.Length - 1
                ? "/" + string.Join("/", parts, baseIndex + 1, parts.Length - baseIndex - 1)
                : string.Empty;

            int lastDigit = -1;
            for (int i = 0; i < baseCall.Length; i++)
                if (char.IsDigit(baseCall[i])) lastDigit = i;
            if (lastDigit < 0) return;

            // On the loose path only, insist there is a letter BEFORE the digit. Without this a bare
            // country prefix such as "4X" - which someone may well type or log - would be torn into
            // "4" + "X". FullCall matches have already proved their shape and need no such check.
            if (loose)
            {
                bool letterBefore = false;
                for (int i = 0; i < lastDigit; i++)
                    if (char.IsLetter(baseCall[i])) { letterBefore = true; break; }
                if (!letterBefore) return;
            }

            prefix = lead + baseCall.Substring(0, lastDigit + 1);
            suffix = baseCall.Substring(lastDigit + 1) + tail;
        }
    }
}




