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
        public long ActiveLogId { get; set; }

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

                con = new SQLiteConnection(@"DataSource = " + dbPath + @";Version=3");
                con.Open();
                BackupBeforeLogsMigration();   // one-time safety copy before the logs-schema upgrade
                UpdateSchema();

            }
            catch (Exception e)
            {
                throw new Exception("Failed to connect to DB: " + e.Message);
            }
            
        }

        // The folder holding logDB.db (and the Backups subfolder).
        public string DataFolder => Path.GetDirectoryName(dbPath);

        // The daily-backups folder (with HOW TO RESTORE.txt) -- for Help > Open Backups Folder.
        public string BackupsFolder => Path.Combine(DataFolder, "Backups");

        // The live database file itself (logDB.db), for the in-app Restore feature.
        public string DbPath => dbPath;

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

                    string safetyCopyPath = Path.Combine(Path.GetDirectoryName(dbPath), safetyCopyFileName);

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

                string todays = Path.Combine(backupDir,
                    "logDB-" + DateTime.Now.ToString("yyyy-MM-dd") + ".db");
                if (!File.Exists(todays))
                    File.Copy(dbPath, todays);

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
                SQLiteCommand insertSQL = new SQLiteCommand("INSERT INTO qso (my_callsign,operator,my_square,my_locator,dx_locator,frequency,band,dx_callsign,rst_rcvd,rst_sent,date,time,mode,submode,exchange,comment,name,country,continent,cq_zone,itu_zone,state,prop_mode,sat_name,soapbox,iota,sota_ref,pota_ref,wwff_ref,sig,sig_info,log_id) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?," + ActiveLogId + ")", con);
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
                "INSERT INTO qso (my_callsign,operator,my_square,my_locator,dx_locator,frequency,band,dx_callsign,rst_rcvd,rst_sent,date,time,mode,submode,exchange,comment,name,country,continent,cq_zone,itu_zone,prop_mode,sat_name,soapbox,iota,sota_ref,pota_ref,wwff_ref,sig,sig_info,eqsl_status,qrz_status,lotw_status,clublog_status,log_id,source_qso_id) " +
                "VALUES (@my_callsign,@operator,@my_square,@my_locator,@dx_locator,@frequency,@band,@dx_callsign,@rst_rcvd,@rst_sent,@date,@time,@mode,@submode,@exchange,@comment,@name,@country,@continent,@cq_zone,@itu_zone,@prop_mode,@sat_name,@soapbox,@iota,@sota_ref,@pota_ref,@wwff_ref,@sig,@sig_info,@es,@qs,@ls,@cs,@log_id,@src)", con))
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
                ins.Parameters.Add(new SQLiteParameter("@prop_mode", qso.PROP_MODE));
                ins.Parameters.Add(new SQLiteParameter("@sat_name", qso.SAT_NAME));
                ins.Parameters.Add(new SQLiteParameter("@soapbox", qso.SOAPBOX));
                AddActivityParams(ins, qso, "@");
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
            catch { return 0; }
        }

        public bool Insert(IEnumerable<QSO> qsos)
        {
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
                    T.Rollback();
                    return false;
                }
            }
            return false;
            }
        }

        public int InsertBatch(IEnumerable<QSO> qsos, Action<int> progressCallback = null)
        {
            lock (_dbLock)
            {
            if (con == null || con.State != System.Data.ConnectionState.Open)
                throw new InvalidOperationException("Database connection is not open.");

            int faultyQso = 0;
            int processedQso = 0;

            using (SQLiteTransaction transaction = con.BeginTransaction())
            using (SQLiteCommand insertSQL = new SQLiteCommand("INSERT INTO qso (my_callsign,operator,my_square,my_locator,dx_locator,frequency,band,dx_callsign,rst_rcvd,rst_sent,date,time,mode,submode,exchange,comment,name,country,continent,prop_mode,sat_name,soapbox,cq_zone,itu_zone,eqsl_status,qrz_status,lotw_status,clublog_status,lotw_qsl_rcvd,lotw_qsl_rdate,lotw_deleted_entity,qrz_qsl_rcvd,qrz_qsl_rdate,qrz_deleted_entity,eqsl_qsl_rcvd,eqsl_qsl_rdate,eqsl_deleted_entity,clublog_qsl_rcvd,clublog_qsl_rdate,clublog_deleted_entity,paper_qsl_rcvd,state,iota,sota_ref,pota_ref,wwff_ref,sig,sig_info,log_id) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,1,1,?,1,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?," + ActiveLogId + ")", con, transaction))
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

                    try
                    {
                        insertSQL.ExecuteNonQuery();
                    }
                    catch (Exception ex)
                    {
                        faultyQso++;
                        System.Diagnostics.Debug.WriteLine($"Failed to insert QSO in batch: {ex.Message}");
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
        public void Update(QSO qso)
        {
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
                catch { /* best-effort; at minimum the QSO itself is updated below */ }

                const string sql = "UPDATE qso SET my_callsign = @my_callsign ,operator = @operator ,my_square = @my_square,my_locator = @my_locator,dx_locator = @dx_locator,frequency = @frequency,band = @band,dx_callsign = @dx_callsign,rst_rcvd = @rst_rcvd,rst_sent = @rst_sent,date = @date,time = @time,mode = @mode,submode = @submode,exchange = @exchange,comment = @comment,name = @name,country = @country,continent = @continent,cq_zone = @cq_zone,itu_zone = @itu_zone,state = @state,prop_mode = @prop_mode,sat_name = @sat_name, soapbox = @soapbox,iota = @iota,sota_ref = @sota_ref,pota_ref = @pota_ref,wwff_ref = @wwff_ref,sig = @sig,sig_info = @sig_info WHERE id = @id";
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
                catch { /* best-effort link lookup; still delete the QSO itself below */ }

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
            lock (_dbLock)
                using (var cmd = new SQLiteCommand("DELETE FROM qso WHERE log_id = ?", con))
                {
                    cmd.Parameters.Add(new SQLiteParameter(null, logId));
                    cmd.ExecuteNonQuery();
                }
        }
        // The six activity-program columns, read in one place. Every QSO reader in this file calls
        // this rather than repeating six lines, so a seventh program field can never be added to
        // some readers and forgotten in others.
        //
        // Guarded by HasColumn because a database that has not been through the migration yet - an old
        // backup opened by the Restore button, for instance - has no such columns, and rdr["iota"]
        // would throw rather than return null.
        private static void ReadActivityFields(SQLiteDataReader rdr, QSO q)
        {
            if (HasColumn(rdr, "iota")) q.Iota = rdr["iota"] as string;
            if (HasColumn(rdr, "sota_ref")) q.SotaRef = rdr["sota_ref"] as string;
            if (HasColumn(rdr, "pota_ref")) q.PotaRef = rdr["pota_ref"] as string;
            if (HasColumn(rdr, "wwff_ref")) q.WwffRef = rdr["wwff_ref"] as string;
            if (HasColumn(rdr, "sig")) q.Sig = rdr["sig"] as string;
            if (HasColumn(rdr, "sig_info")) q.SigInfo = rdr["sig_info"] as string;
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

        private static object Blank(string s)
        {
            return string.IsNullOrWhiteSpace(s) ? (object)DBNull.Value : s.Trim();
        }

        private static bool HasColumn(SQLiteDataReader rdr, string name)
        {
            for (int i = 0; i < rdr.FieldCount; i++)
                if (string.Equals(rdr.GetName(i), name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public ObservableCollection<QSO> GetAllQSOs(Action<int> progressCallback = null)
        {
            lock (_dbLock)
            {
            CultureInfo enUS = new CultureInfo("en-US");
            ObservableCollection<QSO> qso_list = new ObservableCollection<QSO>();
            int totalCount = GetQsoCount();
            int processedCount = 0;
            int lastReportedProgress = -1;
            string stm = "SELECT * FROM qso ORDER BY date DESC, time DESC";
            using (SQLiteCommand cmd = new SQLiteCommand(stm, con))
            {
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
                using (SQLiteCommand cmd = new SQLiteCommand("SELECT * FROM qso WHERE log_id = ? ORDER BY date DESC, time DESC", con))
                {
                    cmd.Parameters.Add(new SQLiteParameter(null, logId));
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
                    if (rdr["state"] != null) q.State = rdr["state"].ToString();
                            if (rdr["time"] != null) q.Time = rdr["time"].ToString();
                            if (rdr["date"] != null) q.Date = rdr["date"].ToString();
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
                        if (rdr["clublog_status"] != null && rdr["clublog_status"] != DBNull.Value) q.ClublogStatus = Convert.ToInt32(rdr["clublog_status"]);
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
            catch
            {
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
            catch { /* an index is an optimization only; never block startup on it */ }
        }

        // Index that backs the QRZ Logbook pending-queue lookups (filter on qrz_status). Idempotent.
        private void EnsureQrzIndexes()
        {
            try
            {
                using (var cmd = new SQLiteCommand("CREATE INDEX IF NOT EXISTS idx_qso_qrz_status ON qso(qrz_status)", con))
                    cmd.ExecuteNonQuery();
            }
            catch { /* an index is an optimization only; never block startup on it */ }
        }

        // Index that backs the Club Log pending-queue lookups (filter on clublog_status). Idempotent.
        private void EnsureClublogIndex()
        {
            try
            {
                using (var cmd = new SQLiteCommand("CREATE INDEX IF NOT EXISTS idx_qso_clublog_status ON qso(clublog_status)", con))
                    cmd.ExecuteNonQuery();
            }
            catch { /* an index is an optimization only; never block startup on it */ }
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
            }
            catch { /* an index is an optimization only; never block startup on it */ }
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
            if (qso == null) return 0;
            lock (_dbLock)
            {
                if (con == null || con.State != System.Data.ConnectionState.Open) return 0;
                const string sql = "INSERT INTO qso (my_callsign,operator,my_square,my_locator,dx_locator,frequency,band,dx_callsign,rst_rcvd,rst_sent,date,time,mode,submode,exchange,comment,name,country,continent,cq_zone,itu_zone,state,prop_mode,sat_name,soapbox,eqsl_status,qrz_status,lotw_status,clublog_status,lotw_qsl_rcvd,lotw_qsl_rdate,lotw_deleted_entity,qrz_qsl_rcvd,qrz_qsl_rdate,qrz_deleted_entity,eqsl_qsl_rcvd,eqsl_qsl_rdate,eqsl_deleted_entity,clublog_qsl_rcvd,clublog_qsl_rdate,clublog_deleted_entity,paper_qsl_rcvd,iota,sota_ref,pota_ref,wwff_ref,sig,sig_info,log_id) " +
                    "VALUES (@my,@op,@mysq,@myloc,@dxloc,@freq,@band,@dx,@rr,@rs,@date,@time,@mode,@sub,@exch,@com,@name,@country,@cont,@cqz,@ituz,@state,@prop,@sat,@soap,@es,@qs,@ls,@cs,@lr,@lrd,@lde,@qr,@qrd,@qde,@er,@erd,@ede,@cr,@crd,@cde,@paper,@iota,@sota_ref,@pota_ref,@wwff_ref,@sig,@sig_info,@log)";
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
                    cmd.Parameters.AddWithValue("@prop", (object)qso.PROP_MODE ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@sat", (object)qso.SAT_NAME ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@soap", (object)qso.SOAPBOX ?? DBNull.Value);
                    AddActivityParams(cmd, qso, "@");
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

                int changed = 0;
                using (var tx = con.BeginTransaction())
                {
                    if (fullReset)
                        using (var clear = new SQLiteCommand(
                            $"UPDATE qso SET {rcvdCol} = 0, {rdateCol} = NULL, {deletedCol} = 0 WHERE {rcvdCol} = 1", con, tx))
                            clear.ExecuteNonQuery();

                    // The station callsign is REQUIRED to match: my_callsign = @mycall. A confirmation
                    // whose station is empty matches nothing here (my_callsign is never blank), which is
                    // the safe direction - better to leave a QSO unticked than to tick a QSO some OTHER
                    // operator made that merely shares the call+band+mode+date. Removing the old
                    // "@mycall = '' OR ..." escape is what stops confirmations leaking across logs.
                    using (var exact = new SQLiteCommand(
                        $"UPDATE qso SET {rcvdCol} = 1, {rdateCol} = @rdate, {deletedCol} = @deleted " +
                        "WHERE dx_callsign = @call COLLATE NOCASE " +
                        "  AND band  = @band COLLATE NOCASE " +
                        "  AND mode  = @mode COLLATE NOCASE " +
                        "  AND date  = @date " +
                        "  AND my_callsign = @mycall COLLATE NOCASE", con, tx))
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
                        "  AND my_callsign = @mycall COLLATE NOCASE " +
                        "  AND UPPER(TRIM(mode)) IN (" + PskFamilyInList + ")", con, tx))
                    {
                        foreach (var cmd in new[] { exact, family })
                        {
                            cmd.Parameters.Add(new SQLiteParameter("@rdate"));
                            cmd.Parameters.Add(new SQLiteParameter("@call"));
                            cmd.Parameters.Add(new SQLiteParameter("@band"));
                            cmd.Parameters.Add(new SQLiteParameter("@date"));
                            cmd.Parameters.Add(new SQLiteParameter("@mycall"));
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
                            string mycall = (c.StationCallsign ?? string.Empty).Trim();

                            exact.Parameters["@rdate"].Value = rdate;
                            exact.Parameters["@call"].Value = call;
                            exact.Parameters["@band"].Value = band;
                            exact.Parameters["@date"].Value = date;
                            exact.Parameters["@mycall"].Value = mycall;
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
            catch { /* optimization only */ }
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
            , [prop_mode] nvarchar(100) NULL COLLATE NOCASE
            , [sat_name] nvarchar(100) NULL COLLATE NOCASE
            , [soapbox] nvarchar(100) NULL COLLATE NOCASE
            , [iota] nvarchar(20) NULL COLLATE NOCASE
            , [sota_ref] nvarchar(30) NULL COLLATE NOCASE
            , [pota_ref] nvarchar(100) NULL COLLATE NOCASE
            , [wwff_ref] nvarchar(30) NULL COLLATE NOCASE
            , [sig] nvarchar(50) NULL COLLATE NOCASE
            , [sig_info] nvarchar(100) NULL COLLATE NOCASE
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
            // Activity-program references. POTA is the wide one on purpose: ADIF allows a comma-
            // separated LIST there, because a contact can be inside two overlapping parks at once.
            AddColToTable("qso", "iota", "nvarchar(20) NULL");
            AddColToTable("qso", "sota_ref", "nvarchar(30) NULL");
            AddColToTable("qso", "pota_ref", "nvarchar(100) NULL");
            AddColToTable("qso", "wwff_ref", "nvarchar(30) NULL");
            AddColToTable("qso", "sig", "nvarchar(50) NULL");
            AddColToTable("qso", "sig_info", "nvarchar(100) NULL");
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
