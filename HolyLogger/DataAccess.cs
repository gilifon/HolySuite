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
                File.WriteAllText(Path.Combine(backupDir, "HOW TO RESTORE.txt"), GetRestoreInstructions());
            }
            catch (Exception ex)
            {
                Log.Swallow(ex);
            }
        }

        // The restore instructions, shown both in HOW TO RESTORE.txt and in the in-app
        // "Backups & Restore" window, so the two can never drift apart.
        public static string GetRestoreInstructions()
        {
            return
"HOW TO RESTORE YOUR LOG FROM A BACKUP" + Environment.NewLine +
"=====================================" + Environment.NewLine +
Environment.NewLine +
"HolyLogger saves a backup copy of your entire log database here every day" + Environment.NewLine +
"(one file per day, the last " + DailyBackupsToKeep + " days are kept)." + Environment.NewLine +
Environment.NewLine +
"If your log is damaged or QSOs were lost by mistake, do this:" + Environment.NewLine +
Environment.NewLine +
"1. Close HolyLogger completely." + Environment.NewLine +
"2. Go to the folder ABOVE this one. It contains your log database, the" + Environment.NewLine +
"   file:  logDB.db" + Environment.NewLine +
"3. Protect the damaged file first: rename logDB.db to logDB.damaged" + Environment.NewLine +
"   (right-click -> Rename). Do NOT delete it - it may still be useful." + Environment.NewLine +
"4. In THIS folder, pick the backup with the most recent date from BEFORE" + Environment.NewLine +
"   the problem happened, e.g.  logDB-2026-07-03.db" + Environment.NewLine +
"5. COPY that file into the folder above, and rename the copy to exactly:" + Environment.NewLine +
"   logDB.db" + Environment.NewLine +
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
                SQLiteCommand insertSQL = new SQLiteCommand("INSERT INTO qso (my_callsign,operator,my_square,my_locator,dx_locator,frequency,band,dx_callsign,rst_rcvd,rst_sent,date,time,mode,submode,exchange,comment,name,country,continent,cq_zone,itu_zone,prop_mode,sat_name,soapbox,log_id) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?," + ActiveLogId + ")", con);
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
                "INSERT INTO qso (my_callsign,operator,my_square,my_locator,dx_locator,frequency,band,dx_callsign,rst_rcvd,rst_sent,date,time,mode,submode,exchange,comment,name,country,continent,cq_zone,itu_zone,prop_mode,sat_name,soapbox,eqsl_status,qrz_status,lotw_status,clublog_status,log_id,source_qso_id) " +
                "VALUES (@my_callsign,@operator,@my_square,@my_locator,@dx_locator,@frequency,@band,@dx_callsign,@rst_rcvd,@rst_sent,@date,@time,@mode,@submode,@exchange,@comment,@name,@country,@continent,@cq_zone,@itu_zone,@prop_mode,@sat_name,@soapbox,@es,@qs,@ls,@cs,@log_id,@src)", con))
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
                    if (string.IsNullOrWhiteSpace(tcall) || string.IsNullOrWhiteSpace(toper)) return 0;
                    if (!string.Equals((qso.MyCall ?? string.Empty).Trim(), tcall.Trim(), StringComparison.OrdinalIgnoreCase)) return 0;
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
                    SQLiteCommand insertSQL = new SQLiteCommand("INSERT INTO qso (my_callsign,operator,my_square,my_locator,dx_locator,frequency,band,dx_callsign,rst_rcvd,rst_sent,date,time,mode,submode,exchange,comment,name,country,continent,cq_zone,itu_zone,prop_mode,sat_name,soapbox,eqsl_status,qrz_status,lotw_status,clublog_status,log_id) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,1,1,1,1," + ActiveLogId + ")", con);
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
            using (SQLiteCommand insertSQL = new SQLiteCommand("INSERT INTO qso (my_callsign,operator,my_square,my_locator,dx_locator,frequency,band,dx_callsign,rst_rcvd,rst_sent,date,time,mode,submode,exchange,comment,name,country,continent,prop_mode,sat_name,soapbox,cq_zone,itu_zone,eqsl_status,qrz_status,lotw_status,clublog_status,log_id) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,1,1,?,1," + ActiveLogId + ")", con, transaction))
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

                const string sql = "UPDATE qso SET my_callsign = @my_callsign ,operator = @operator ,my_square = @my_square,my_locator = @my_locator,dx_locator = @dx_locator,frequency = @frequency,band = @band,dx_callsign = @dx_callsign,rst_rcvd = @rst_rcvd,rst_sent = @rst_sent,date = @date,time = @time,mode = @mode,submode = @submode,exchange = @exchange,comment = @comment,name = @name,country = @country,continent = @continent,cq_zone = @cq_zone,itu_zone = @itu_zone,prop_mode = @prop_mode,sat_name = @sat_name, soapbox = @soapbox WHERE id = @id";
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
                        insertSQL.Parameters.Add(new SQLiteParameter("@prop_mode", qso.PROP_MODE));
                        insertSQL.Parameters.Add(new SQLiteParameter("@sat_name", qso.SAT_NAME));
                        insertSQL.Parameters.Add(new SQLiteParameter("@soapbox", qso.SOAPBOX));
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
                        if (rdr["cq_zone"] != null) q.CQZone = rdr["cq_zone"].ToString();
                        if (rdr["itu_zone"] != null) q.ITUZone = rdr["itu_zone"].ToString();
                        if (rdr["time"] != null) q.Time = rdr["time"].ToString();
                        if (rdr["date"] != null) q.Date = rdr["date"].ToString();
                        if (rdr["prop_mode"] != null) q.PROP_MODE = rdr["prop_mode"].ToString();
                        if (rdr["sat_name"] != null) q.SAT_NAME = rdr["sat_name"].ToString();
                        if (rdr["soapbox"] != null) q.SOAPBOX = rdr["soapbox"].ToString();
                        if (rdr["eqsl_status"] != null && rdr["eqsl_status"] != DBNull.Value) q.EqslStatus = Convert.ToInt32(rdr["eqsl_status"]);
                        if (rdr["lotw_status"] != null && rdr["lotw_status"] != DBNull.Value) q.LotwStatus = Convert.ToInt32(rdr["lotw_status"]);
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
                            if (rdr["time"] != null) q.Time = rdr["time"].ToString();
                            if (rdr["date"] != null) q.Date = rdr["date"].ToString();
                            if (rdr["prop_mode"] != null) q.PROP_MODE = rdr["prop_mode"].ToString();
                            if (rdr["sat_name"] != null) q.SAT_NAME = rdr["sat_name"].ToString();
                            if (rdr["soapbox"] != null) q.SOAPBOX = rdr["soapbox"].ToString();
                            if (rdr["eqsl_status"] != null && rdr["eqsl_status"] != DBNull.Value) q.EqslStatus = Convert.ToInt32(rdr["eqsl_status"]);
                            if (rdr["lotw_status"] != null && rdr["lotw_status"] != DBNull.Value) q.LotwStatus = Convert.ToInt32(rdr["lotw_status"]);
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
                            if (rdr["time"] != null) q.Time = rdr["time"].ToString();
                            if (rdr["date"] != null) q.Date = rdr["date"].ToString();
                            if (rdr["prop_mode"] != null) q.PROP_MODE = rdr["prop_mode"].ToString();
                            if (rdr["sat_name"] != null) q.SAT_NAME = rdr["sat_name"].ToString();
                            if (rdr["soapbox"] != null) q.SOAPBOX = rdr["soapbox"].ToString();
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
                        if (rdr["cq_zone"] != null) q.CQZone = rdr["cq_zone"].ToString();
                        if (rdr["itu_zone"] != null) q.ITUZone = rdr["itu_zone"].ToString();
                        if (rdr["time"] != null) q.Time = (string)rdr["time"];
                        if (rdr["date"] != null) q.Date = (string)rdr["date"];
                        if (rdr["prop_mode"] != null) q.PROP_MODE = rdr["prop_mode"].ToString();
                        if (rdr["sat_name"] != null) q.SAT_NAME = rdr["sat_name"].ToString();
                        if (rdr["soapbox"] != null) q.SOAPBOX = rdr["soapbox"].ToString();
                        if (rdr["eqsl_status"] != null && rdr["eqsl_status"] != DBNull.Value) q.EqslStatus = Convert.ToInt32(rdr["eqsl_status"]);
                        if (rdr["lotw_status"] != null && rdr["lotw_status"] != DBNull.Value) q.LotwStatus = Convert.ToInt32(rdr["lotw_status"]);
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
        // copy-target (another log this one's new QSOs are mirrored into).
        public long CreateLog(string name, string eventType, string callsign, string opr, long? copyTargetLogId)
        {
            lock (_dbLock)
            {
                using (var cmd = new SQLiteCommand("INSERT INTO logs (name, event_type, created_utc, log_callsign, log_operator, copy_target_log_id) VALUES (?,?,?,?,?,?)", con))
                {
                    cmd.Parameters.Add(new SQLiteParameter(null, name));
                    cmd.Parameters.Add(new SQLiteParameter(null, eventType ?? string.Empty));
                    cmd.Parameters.Add(new SQLiteParameter(null, DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss")));
                    cmd.Parameters.Add(new SQLiteParameter(null, (object)(callsign ?? string.Empty)));
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
                    cmd.Parameters.Add(new SQLiteParameter(null, (object)((callsign ?? string.Empty).Trim())));
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
            return list;
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
                    if (rdr["time"] != null) q.Time = rdr["time"].ToString();
                    if (rdr["date"] != null) q.Date = rdr["date"].ToString();
                    if (rdr["prop_mode"] != null) q.PROP_MODE = rdr["prop_mode"].ToString();
                    if (rdr["sat_name"] != null) q.SAT_NAME = rdr["sat_name"].ToString();
                    if (rdr["soapbox"] != null) q.SOAPBOX = rdr["soapbox"].ToString();
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
                    if (rdr["time"] != null) q.Time = rdr["time"].ToString();
                    if (rdr["date"] != null) q.Date = rdr["date"].ToString();
                    if (rdr["prop_mode"] != null) q.PROP_MODE = rdr["prop_mode"].ToString();
                    if (rdr["sat_name"] != null) q.SAT_NAME = rdr["sat_name"].ToString();
                    if (rdr["soapbox"] != null) q.SOAPBOX = rdr["soapbox"].ToString();
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
                    if (rdr["time"] != null) q.Time = rdr["time"].ToString();
                    if (rdr["date"] != null) q.Date = rdr["date"].ToString();
                    if (rdr["prop_mode"] != null) q.PROP_MODE = rdr["prop_mode"].ToString();
                    if (rdr["sat_name"] != null) q.SAT_NAME = rdr["sat_name"].ToString();
                    if (rdr["soapbox"] != null) q.SOAPBOX = rdr["soapbox"].ToString();
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
                    if (rdr["time"] != null) q.Time = rdr["time"].ToString();
                    if (rdr["date"] != null) q.Date = rdr["date"].ToString();
                    if (rdr["prop_mode"] != null) q.PROP_MODE = rdr["prop_mode"].ToString();
                    if (rdr["sat_name"] != null) q.SAT_NAME = rdr["sat_name"].ToString();
                    if (rdr["soapbox"] != null) q.SOAPBOX = rdr["soapbox"].ToString();
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
            , [prop_mode] nvarchar(100) NULL COLLATE NOCASE
            , [sat_name] nvarchar(100) NULL COLLATE NOCASE
            , [soapbox] nvarchar(100) NULL COLLATE NOCASE
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
            AddEqslStatusColumn();
            AddQrzColumns();
            AddLotwColumns();
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
}
