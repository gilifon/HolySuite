using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HolyLogger
{
    // ── THE DIARY: WHAT CHANGED IN THE LOG ON THIS COMPUTER ──────────────────────────────────────────
    //
    // Step 2 of one log on several computers (2026-11-10). Each computer will keep its own copy of the
    // log and a diary of the changes made on it; the others read that diary and make the same changes,
    // and nobody ever writes anyone else's diary - which is what lets a plain shared folder (Google
    // Drive, later) carry it without two computers ever writing one file.
    //
    // The diary is kept in <data folder>\Sync\<this computer's id>.diary, and - once the operator has
    // signed in with Google (step 3, GoogleDrive below) - a copy of it goes up to his Google Drive after
    // every write.
    //
    // The changes come from sync_journal (DataAccess.EnsureSyncJournal), which triggers fill whatever
    // wrote the QSO. Every minute, and when the program closes, the notes are turned into lines and
    // forgotten. One line per changed QSO or log, a JSON object:
    //
    //   {"at":"...Z","what":"qso","op":"put","id":"<sync_id>","fields":{...the whole QSO...}}
    //   {"at":"...Z","what":"qso","op":"del","id":"<sync_id>"}
    //
    // "put" carries the WHOLE row as it is now - not just what changed - so a reader only ever has to
    // take the latest line for a QSO, and reading one line twice does no harm. The local numbers that
    // mean nothing on another computer are replaced by sync ids: log_id becomes "log", source_qso_id
    // "source", copy_target_log_id "copy_target". Empty fields are left out.
    //
    // The diary is written before the notes are forgotten, so a crash between the two can only repeat
    // a line, never lose one.
    public static class SyncDiary
    {
        private static readonly object Gate = new object();
        private static Timer _timer;
        private static int _ticks;
        private static int _pulling;

        // Every minute this computer's changes go up; at the first tick (soon after start) and then every
        // five minutes the other computers' changes come down (step 4, Pull).
        public static void Start()
        {
            _timer = new Timer(_ =>
            {
                FlushSafe();
                if (_ticks++ % 5 == 0)
                {
                    PullSafe();
                    Task.Run(() => SnapshotIfDueSafe());   // its own thread: it can take minutes
                }
            }, null, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1));
        }

        // At exit the upload gets ten seconds at most: a field laptop closed straight after a contest should
        // still send its last QSOs, but HolyLogger must never hang on the way out waiting for the internet.
        public static void Stop()
        {
            try { _timer?.Dispose(); } catch (Exception swallowed) { Log.Swallow(swallowed); }
            _timer = null;
            FlushSafe(TimeSpan.FromSeconds(10));
        }

        public static void FlushSafe(TimeSpan? uploadLimit = null)
        {
            try { Flush(); }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
            try { if (GoogleDrive.UploadDiary(DiaryPath(), uploadLimit ?? TimeSpan.FromSeconds(60))) ReportContact(null); }
            catch (Exception ex)
            {
                Log.Warn("Google Drive: the diary was not sent - " + ex.Message);
                ReportContact(ex.Message);
            }
        }

        // WHAT THE CLOUD ON THE MAIN WINDOW SHOWS. Only a real answer from Google Drive counts: a diary
        // actually sent, or the folder actually read. LastProblem is the last failure, cleared by the
        // next success; StatusChanged tells the window to look again (sign-in and sign-out too).
        public static string LastProblem { get; private set; }
        public static DateTime? LastContactUtc { get; private set; }
        public static event Action StatusChanged;

        public static void ReportContact(string problem)
        {
            LastProblem = problem;
            if (problem == null) LastContactUtc = DateTime.UtcNow;
            try { StatusChanged?.Invoke(); } catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        public static string Folder
        {
            get { return Path.Combine(DataAccess.GetInstance().DataFolder, "Sync"); }
        }

        // THIS COMPUTER'S NAME IN THE DIARIES: a random id, kept in the database together with the
        // Windows name of the computer that made it. A database restored or copied onto ANOTHER computer
        // brings the id along, and two computers writing one diary is the one thing that must not happen
        // - so a different Windows name means a different computer, and it gets an id of its own.
        public static string ComputerId()
        {
            var dal = DataAccess.GetInstance();
            string id = dal.GetDbState("sync_computer_id");
            string machine = dal.GetDbState("sync_computer_machine");
            if (string.IsNullOrEmpty(id) || !string.Equals(machine, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            {
                id = Guid.NewGuid().ToString();
                dal.SetDbState("sync_computer_id", id);
                dal.SetDbState("sync_computer_machine", Environment.MachineName);
            }
            return id;
        }

        public static string DiaryPath()
        {
            return Path.Combine(Folder, ComputerId() + ".diary");
        }

        // Turns every waiting note into diary lines. Returns how many lines were written.
        public static int Flush()
        {
            lock (Gate)
            {
                var dal = DataAccess.GetInstance();
                if (dal == null) return 0;
                int written = 0;
                while (true)
                {
                    List<DataAccess.SyncNote> notes = dal.ReadSyncJournal(2000);
                    if (notes.Count == 0) break;

                    // Only the last note for a row matters: the line carries the row as it is now.
                    var last = new Dictionary<string, DataAccess.SyncNote>();
                    foreach (var n in notes) last[n.Kind + "|" + n.RowId] = n;
                    var ordered = last.Values.OrderBy(n => n.Seq).ToList();

                    var qsoIds = ordered.Where(n => n.Kind == "qso" && n.Op == "put").Select(n => n.RowId).ToList();
                    var logIds = ordered.Where(n => n.Kind == "log" && n.Op == "put").Select(n => n.RowId).ToList();
                    var qsos = dal.ReadRowsForSync("qso", qsoIds);
                    var logs = dal.ReadRowsForSync("logs", logIds);

                    // the local numbers inside the rows, as sync ids
                    var logRefs = new HashSet<long>();
                    var qsoRefs = new HashSet<long>();
                    foreach (var r in qsos.Values)
                    {
                        if (r.TryGetValue("log_id", out object l)) logRefs.Add(Convert.ToInt64(l));
                        if (r.TryGetValue("source_qso_id", out object s)) qsoRefs.Add(Convert.ToInt64(s));
                    }
                    foreach (var r in logs.Values)
                        if (r.TryGetValue("copy_target_log_id", out object c)) logRefs.Add(Convert.ToInt64(c));
                    var logSync = dal.SyncIdsOf("logs", logRefs);
                    var qsoSync = dal.SyncIdsOf("qso", qsoRefs);

                    var sb = new StringBuilder();
                    foreach (var n in ordered)
                    {
                        var line = new JObject
                        {
                            ["at"] = n.AtUtc,
                            ["what"] = n.Kind,
                            ["op"] = n.Op,
                        };
                        if (n.Op == "del")
                        {
                            if (string.IsNullOrEmpty(n.SyncId)) continue;   // never given an id: nothing to name
                            line["id"] = n.SyncId;
                        }
                        else
                        {
                            var rows = n.Kind == "qso" ? qsos : logs;
                            if (!rows.TryGetValue(n.RowId, out var row)) continue;   // gone already; its del follows
                            if (!row.TryGetValue("sync_id", out object sid)) continue;
                            line["id"] = sid.ToString();
                            line["fields"] = Fields(row, logSync, qsoSync);
                        }
                        sb.Append(line.ToString(Formatting.None)).Append('\n');
                        written++;
                    }

                    if (sb.Length > 0) Append(sb.ToString());
                    // when each row was changed here: the latest change wins when other diaries are read
                    var times = new Dictionary<string, string>();
                    foreach (var n in ordered)
                    {
                        string sid = n.Op == "del" ? n.SyncId
                            : (n.Kind == "qso" ? qsos : logs).TryGetValue(n.RowId, out var row) && row.TryGetValue("sync_id", out object s) ? s.ToString() : null;
                        if (!string.IsNullOrEmpty(sid)) times[sid] = n.AtUtc;
                    }
                    dal.NoteSyncTimes(times);
                    dal.ForgetSyncJournal(notes[notes.Count - 1].Seq);
                }
                if (written > 0) Log.Warn("Sync diary: " + written + " line(s) written.");
                return written;
            }
        }

        private static JObject Fields(Dictionary<string, object> row, Dictionary<long, string> logSync, Dictionary<long, string> qsoSync)
        {
            var f = new JObject();
            foreach (var kv in row)
            {
                switch (kv.Key)
                {
                    case "Id":
                    case "sync_id":
                        break;
                    case "log_id":
                        if (logSync.TryGetValue(Convert.ToInt64(kv.Value), out string lg)) f["log"] = lg;
                        break;
                    case "copy_target_log_id":
                        if (logSync.TryGetValue(Convert.ToInt64(kv.Value), out string ct)) f["copy_target"] = ct;
                        break;
                    case "source_qso_id":
                        if (qsoSync.TryGetValue(Convert.ToInt64(kv.Value), out string src)) f["source"] = src;
                        break;
                    default:
                        f[kv.Key] = JToken.FromObject(kv.Value);
                        break;
                }
            }
            return f;
        }

        // ── STEP 4: READING THE OTHER COMPUTERS' DIARIES ─────────────────────────────────────────────
        //
        // Each other computer's diary in the Drive folder is read from where this computer stopped last
        // time (kept in db_state as sync_read_<file>), so only the new lines come down. Only whole lines
        // are used; a line still being written is read next time. The lines are made here by
        // DataAccess.ApplySyncLines (latest change wins, no echo), and only then is the place moved on -
        // if anything fails, the same lines are simply read again later, which does no harm.
        //
        // The network part runs outside Gate, so closing HolyLogger never waits for a download.
        public static void PullSafe()
        {
            if (Interlocked.Exchange(ref _pulling, 1) == 1) return;
            try
            {
                if (Pull()) ReportContact(null);
            }
            catch (Exception ex)
            {
                Log.Warn("Google Drive: other computers' changes were not read - " + ex.Message);
                ReportContact(ex.Message);
            }
            finally { Interlocked.Exchange(ref _pulling, 0); }
        }

        // true = Google Drive was reached and read
        private static bool Pull()
        {
            if (!GoogleDrive.Registered || !GoogleDrive.SignedIn) return false;
            var dal = DataAccess.GetInstance();
            if (dal == null) return false;
            string mine = ComputerId() + ".diary";
            bool touched = false;

            foreach (var file in GoogleDrive.ListFolder(TimeSpan.FromSeconds(60)))
            {
                if (!file.Name.EndsWith(".diary", StringComparison.OrdinalIgnoreCase) || file.Name == mine) continue;
                string readKey = "sync_read_" + file.Name;
                long.TryParse(dal.GetDbState(readKey), out long offset);
                if (file.Size < offset) offset = 0;   // shorter than what was read: that diary started again
                if (file.Size == offset) continue;

                byte[] data = GoogleDrive.Download(file.Id, offset, TimeSpan.FromSeconds(120));
                int end = Array.LastIndexOf(data, (byte)'\n');
                if (end < 0) continue;
                string machine = dal.GetDbState("sync_machine_" + file.Name);
                var lines = new List<DataAccess.SyncLine>();
                foreach (string raw in Encoding.UTF8.GetString(data, 0, end + 1).Split('\n'))
                {
                    if (raw.Trim().Length == 0) continue;
                    // DateParseHandling.None: by default Newtonsoft turns any text that looks like a date into a
                    // DateTime, and "at" came back as "11/11/2026 20:44:57" - which sorts before every real time,
                    // so a newer edit was thrown away as older (2026-11-11, the first laptop test). Every value
                    // is taken exactly as it was written.
                    JObject j;
                    try
                    {
                        using (var jr = new JsonTextReader(new StringReader(raw)) { DateParseHandling = DateParseHandling.None })
                            j = JObject.Load(jr);
                    }
                    catch (Exception) { Log.Warn("Sync: a line of " + file.Name + " could not be read; skipped."); continue; }
                    if (j["diary"] != null)   // the header: whose diary this is
                    {
                        machine = (string)j["machine"];
                        dal.SetDbState("sync_machine_" + file.Name, machine);
                        continue;
                    }
                    var line = new DataAccess.SyncLine
                    {
                        At = (string)j["at"], What = (string)j["what"], Op = (string)j["op"], Id = (string)j["id"],
                    };
                    if (j["fields"] is JObject fo)
                        line.Fields = fo.Properties().ToDictionary(p => p.Name, p => (p.Value as JValue)?.Value);
                    lines.Add(line);
                }
                if (string.IsNullOrEmpty(machine)) machine = Path.GetFileNameWithoutExtension(file.Name);

                DataAccess.SyncApplyResult r;
                lock (Gate)
                {
                    Flush();   // this computer's own latest changes first, so their times are known
                    r = dal.ApplySyncLines(lines, machine);
                }
                dal.SetDbState(readKey, (offset + end + 1).ToString());
                if (r.ActiveLogTouched) touched = true;
                Log.Warn("Sync: from " + machine + ": " + r.Applied + " change(s) made, " + r.Older
                         + " older than what is here, " + r.Skipped + " skipped.");
            }

            if (touched)
            {
                var app = System.Windows.Application.Current;
                app?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try { (app.MainWindow as MainWindow)?.ReloadActiveLogQsos(); }
                    catch (Exception swallowed) { Log.Swallow(swallowed); }
                }));
            }
            return true;
        }

        // ── STEP 5: THE WHOLE LOG IN DRIVE, FOR A NEW COMPUTER ───────────────────────────────────────
        //
        // The diaries hold only the changes made since sync began, so a brand-new computer could not
        // be given the operator's whole log from them. Once a week (when the newest copy in Drive, from
        // any of his computers, is more than seven days old) one computer puts a whole copy of the
        // database there, gzipped, as <its id>.snapshot - each computer writes only its own, like its
        // diary.
        //
        // The copy says how far it is up to date: in its db_state, where the diaries are read from next
        // (the maker's own diary at its length when the copy was taken; the others where the maker had
        // read them). A new computer puts the copy in place of its own log, restarts, and reads the
        // diaries from those places - so it misses nothing made after the copy. A change made between
        // the length being taken and the copy is in both, and is simply made twice, which does no harm.
        //
        // The copy carries no computer id, so the new computer makes its own; and sync_joined = 1.
        //
        // sync_joined in db_state: "1" = this database is the shared one (it may send copies); "keep" =
        // the operator kept this computer's own, different log (it never sends a copy, or it could
        // replace everyone's); empty = not decided yet (MainWindow.OfferDriveSnapshot asks).
        private static int _snapshotting;
        private static readonly TimeSpan SnapshotEvery = TimeSpan.FromDays(7);

        public static string JoinState
        {
            get { return DataAccess.GetInstance()?.GetDbState("sync_joined") ?? string.Empty; }
            set { DataAccess.GetInstance()?.SetDbState("sync_joined", value); }
        }

        private static string SnapshotName() { return ComputerId() + ".snapshot"; }

        // The newest whole copy another of the operator's computers put in Drive; null if none.
        public static GoogleDrive.DriveFile NewestOtherSnapshot()
        {
            string mine = SnapshotName();
            return GoogleDrive.ListFolder(TimeSpan.FromSeconds(60))
                .Where(f => f.Name.EndsWith(".snapshot", StringComparison.OrdinalIgnoreCase) && f.Name != mine && f.Size > 0)
                .OrderByDescending(f => f.ModifiedUtc ?? DateTime.MinValue)
                .FirstOrDefault();
        }

        public static void SnapshotIfDueSafe()
        {
            if (Interlocked.Exchange(ref _snapshotting, 1) == 1) return;
            try { SnapshotIfDue(); }
            catch (Exception ex) { Log.Warn("Google Drive: the whole copy of the log was not sent - " + ex.Message); }
            finally { Interlocked.Exchange(ref _snapshotting, 0); }
        }

        private static void SnapshotIfDue()
        {
            if (!GoogleDrive.Registered || !GoogleDrive.SignedIn) return;
            var dal = DataAccess.GetInstance();
            if (dal == null) return;
            string mine = SnapshotName();
            var snaps = GoogleDrive.ListFolder(TimeSpan.FromSeconds(60))
                .Where(f => f.Name.EndsWith(".snapshot", StringComparison.OrdinalIgnoreCase) && f.Size > 0).ToList();

            string join = JoinState;
            if (join.Length == 0)
            {
                // another computer's copy is there: the operator decides first (OfferDriveSnapshot)
                if (snaps.Any(f => f.Name != mine)) return;
                JoinState = join = "1";   // the first computer: the others start from this one
            }
            if (join != "1") return;

            DateTime? newest = snaps.Max(f => f.ModifiedUtc);
            if (newest.HasValue && DateTime.UtcNow - newest.Value < SnapshotEvery) return;
            MakeSnapshot(dal);
        }

        private static void MakeSnapshot(DataAccess dal)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            Directory.CreateDirectory(Folder);
            string copy = Path.Combine(Folder, "snapshot.db");
            string gz = Path.Combine(Folder, "snapshot.db.gz");
            string myDiary = ComputerId() + ".diary";
            ThreadPriority was = Thread.CurrentThread.Priority;
            try
            {
                long diaryLength;
                lock (Gate)   // no diary line is written between the length and the copy
                {
                    Flush();
                    string d = DiaryPath();
                    diaryLength = File.Exists(d) ? new FileInfo(d).Length : 0;
                    dal.CopyDatabaseTo(copy);
                }

                using (var c = new SQLiteConnection("Data Source=" + copy + ";Version=3"))
                {
                    c.Open();
                    foreach (string sql in new[]
                    {
                        "DELETE FROM sync_journal",
                        "DELETE FROM db_state WHERE key IN ('sync_computer_id', 'sync_computer_machine')",
                        "INSERT OR REPLACE INTO db_state (key, value) VALUES ('sync_joined', '1')",
                        "INSERT OR REPLACE INTO db_state (key, value) VALUES (@k, @v)",
                    })
                        using (var cmd = new SQLiteCommand(sql, c))
                        {
                            cmd.Parameters.AddWithValue("@k", "sync_read_" + myDiary);
                            cmd.Parameters.AddWithValue("@v", diaryLength.ToString());
                            cmd.ExecuteNonQuery();
                        }
                }

                Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;   // the operator is working
                using (var src = File.OpenRead(copy))
                using (var dst = File.Create(gz))
                using (var z = new GZipStream(dst, CompressionLevel.Optimal))
                    src.CopyTo(z);
                Thread.CurrentThread.Priority = was;

                long bytes = new FileInfo(gz).Length;
                GoogleDrive.UploadLarge(gz, SnapshotName(), "application/gzip", TimeSpan.FromHours(3));
                Log.Warn("Google Drive: whole copy of the log sent (" + (bytes / 1048576.0).ToString("0.0")
                         + " MB) in " + watch.Elapsed.TotalSeconds.ToString("0") + " s.");
            }
            finally
            {
                Thread.CurrentThread.Priority = was;
                try { File.Delete(copy); } catch (Exception swallowed) { Log.Swallow(swallowed); }
                try { File.Delete(gz); } catch (Exception swallowed) { Log.Swallow(swallowed); }
            }
        }

        // Fetches a whole copy and unpacks it beside the diaries; checks it is a sound HolyLogger
        // database before anyone puts it in place. Returns its path and how many QSOs it holds.
        public static string DownloadSnapshot(GoogleDrive.DriveFile file, Action<long, long> progress, out long qsos)
        {
            Directory.CreateDirectory(Folder);
            string gz = Path.Combine(Folder, "from-drive.db.gz");
            string db = Path.Combine(Folder, "from-drive.db");
            try
            {
                GoogleDrive.DownloadToFile(file.Id, gz, progress, TimeSpan.FromHours(3));
                using (var src = new GZipStream(File.OpenRead(gz), CompressionMode.Decompress))
                using (var dst = File.Create(db))
                    src.CopyTo(dst);
            }
            finally { try { File.Delete(gz); } catch (Exception swallowed) { Log.Swallow(swallowed); } }

            using (var c = new SQLiteConnection("Data Source=" + db + ";Version=3;Read Only=True"))
            {
                c.Open();
                using (var cmd = new SQLiteCommand("PRAGMA quick_check", c))
                {
                    string ok = Convert.ToString(cmd.ExecuteScalar());
                    if (ok != "ok") throw new Exception("the copy in Google Drive is damaged (" + ok + ")");
                }
                using (var cmd = new SQLiteCommand("SELECT count(*) FROM qso", c))
                    qsos = Convert.ToInt64(cmd.ExecuteScalar());
            }
            Log.Warn("Google Drive: whole copy of the log received (" + qsos + " QSOs).");
            return db;
        }

        // A new diary starts with a line saying whose it is.
        private static void Append(string lines)
        {
            string path = DiaryPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            bool isNew = !File.Exists(path);
            using (var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough))
            using (var w = new StreamWriter(fs, new UTF8Encoding(false)))
            {
                if (isNew)
                {
                    var head = new JObject
                    {
                        ["diary"] = 1,
                        ["computer"] = ComputerId(),
                        ["machine"] = Environment.MachineName,
                        ["created"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                    };
                    w.Write(head.ToString(Formatting.None) + "\n");
                }
                w.Write(lines);
            }
        }
    }

    // ── GOOGLE DRIVE: SIGNING IN, AND SENDING THIS COMPUTER'S DIARY UP ────────────────────────────────
    //
    // Step 3 of one log on several computers (2026-11-11). The operator signs in with Google once per
    // computer; from then on the diary is copied, whole, into a "HolyLogger Sync" folder in his Drive
    // whenever it has grown.
    //
    // HolyLogger talks to Google itself rather than leaving the file to the Google Drive app: at the next
    // step it must KNOW, when it starts, that it has every other computer's latest diary, and a folder the
    // Drive app may or may not have finished downloading cannot tell it that.
    //
    // THE PERMISSION IS THE SMALLEST GOOGLE HAS: drive.file - HolyLogger sees only the files it made
    // itself, never the rest of the operator's Drive. Signing in is the standard one for programs on a PC:
    // the browser opens Google's own page, the operator says Allow, and Google hands the answer back to a
    // port HolyLogger listens on at 127.0.0.1 for that one moment (with PKCE, so the answer is no use to
    // anyone else who sees it). What is kept is the refresh token, DPAPI-encrypted for this Windows user,
    // in %APPDATA%\HolyLogger\google.dat - the same protection as the service passwords in credentials.dat.
    //
    // HolyLogger's registration with Google (Cloud project "holylogger", a Desktop-app client, made
    // 2026-11-11) is NOT in the source code, which is public: the client id and secret are read from
    // google_client.json - first beside HolyLogger.exe, then in %APPDATA%\HolyLogger - as
    // {"client_id":"...","client_secret":"..."}. Without that file the Google Drive menu says so and
    // nothing else changes. How the file reaches other operators' computers is decided before release.
    //
    // No new library: plain HttpClient and the Newtonsoft.Json that HolyLogger already ships.
    public static class GoogleDrive
    {
        private const string Scope = "https://www.googleapis.com/auth/drive.file";
        public const string FolderName = "HolyLogger Sync";

        private static readonly HttpClient Http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        private static readonly object Gate = new object();

        private static string _clientId, _clientSecret;

        // True when HolyLogger's Google registration file is present (see above).
        public static bool Registered
        {
            get
            {
                if (_clientId != null) return true;
                foreach (string dir in new[]
                {
                    AppDomain.CurrentDomain.BaseDirectory,
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HolyLogger"),
                })
                {
                    try
                    {
                        string f = Path.Combine(dir, "google_client.json");
                        if (!File.Exists(f)) continue;
                        JObject j = JObject.Parse(File.ReadAllText(f));
                        string id = (string)j["client_id"], secret = (string)j["client_secret"];
                        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(secret)) continue;
                        _clientId = id; _clientSecret = secret;
                        return true;
                    }
                    catch (Exception swallowed) { Log.Swallow(swallowed); }
                }
                return false;
            }
        }

        private sealed class State
        {
            public string RefreshToken, Email, FolderId, FileId;
            public long UploadedBytes;
            public DateTime? UploadedUtc;
        }

        private static State _state;
        private static string _access;
        private static DateTime _accessUntil;

        private static string StatePath
        {
            get
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HolyLogger");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "google.dat");
            }
        }

        private static State Load()
        {
            lock (Gate)
            {
                if (_state != null) return _state;
                try
                {
                    if (File.Exists(StatePath))
                    {
                        byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(StatePath), null, DataProtectionScope.CurrentUser);
                        _state = JsonConvert.DeserializeObject<State>(Encoding.UTF8.GetString(plain));
                    }
                }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
                return _state ?? (_state = new State());
            }
        }

        private static void Save(State s)
        {
            lock (Gate)
            {
                _state = s;
                byte[] enc = ProtectedData.Protect(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(s)), null, DataProtectionScope.CurrentUser);
                File.WriteAllBytes(StatePath, enc);
            }
        }

        public static bool SignedIn { get { return !string.IsNullOrEmpty(Load().RefreshToken); } }
        public static string Email { get { return Load().Email; } }
        public static DateTime? LastUploadUtc { get { return Load().UploadedUtc; } }

        // ── signing in ──────────────────────────────────────────────────────────────────────────────
        //
        // Opens the browser at Google's sign-in page and waits (at most five minutes) for Google to send
        // the operator back. Returns the account's email address; throws with a plain reason otherwise.
        public static async Task<string> SignInAsync()
        {
            if (!Registered) throw new Exception("HolyLogger's Google registration file is missing.");
            string verifier = Base64Url(RandomBytes(32));
            string challenge;
            using (var sha = SHA256.Create())
                challenge = Base64Url(sha.ComputeHash(Encoding.ASCII.GetBytes(verifier)));
            string expectState = Base64Url(RandomBytes(16));

            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            string code;
            string redirect;
            try
            {
                redirect = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
                string url = "https://accounts.google.com/o/oauth2/v2/auth"
                    + "?client_id=" + Uri.EscapeDataString(_clientId)
                    + "&redirect_uri=" + Uri.EscapeDataString(redirect)
                    + "&response_type=code"
                    + "&scope=" + Uri.EscapeDataString(Scope)
                    + "&code_challenge=" + challenge + "&code_challenge_method=S256"
                    + "&state=" + expectState
                    + "&access_type=offline&prompt=consent";
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });

                var accept = listener.AcceptTcpClientAsync();
                if (await Task.WhenAny(accept, Task.Delay(TimeSpan.FromMinutes(5))) != accept)
                    throw new Exception("Google did not answer within five minutes.");

                using (TcpClient client = accept.Result)
                using (NetworkStream stream = client.GetStream())
                {
                    string request = ReadRequest(stream);
                    var query = ParseQuery(request);
                    if (query.TryGetValue("error", out string err))
                    {
                        Answer(stream, "HolyLogger was not allowed to use Google Drive. You can close this page.");
                        throw new Exception(err == "access_denied" ? "You did not allow HolyLogger." : "Google said: " + err);
                    }
                    if (!query.TryGetValue("code", out code) || !query.TryGetValue("state", out string gotState) || gotState != expectState)
                    {
                        Answer(stream, "Something went wrong. Please close this page and try again from HolyLogger.");
                        throw new Exception("Google's answer could not be read.");
                    }
                    Answer(stream, "HolyLogger is now signed in to Google Drive. You can close this page and go back to HolyLogger.");
                }
            }
            finally { listener.Stop(); }

            JObject tok = await PostForm("https://oauth2.googleapis.com/token", new Dictionary<string, string>
            {
                ["code"] = code,
                ["client_id"] = _clientId,
                ["client_secret"] = _clientSecret,
                ["redirect_uri"] = redirect,
                ["code_verifier"] = verifier,
                ["grant_type"] = "authorization_code",
            }, CancellationToken.None);
            string refresh = (string)tok["refresh_token"];
            if (string.IsNullOrEmpty(refresh)) throw new Exception("Google gave no lasting permission.");
            _access = (string)tok["access_token"];
            _accessUntil = DateTime.UtcNow.AddSeconds(((int?)tok["expires_in"] ?? 3600) - 120);

            // a different account (or the same one again) starts with nothing remembered about folders or files
            Save(new State { RefreshToken = refresh });
            JObject about = await Get("https://www.googleapis.com/drive/v3/about?fields=user(emailAddress)", CancellationToken.None);
            string email = (string)about["user"]?["emailAddress"];
            Save(new State { RefreshToken = refresh, Email = email });
            Log.Warn("Google Drive: signed in as " + email + ".");
            SyncDiary.ReportContact(null);
            return email;
        }

        // Signing out forgets the permission on THIS computer only. It used to tell Google to cancel it
        // too - and Google cancels HolyLogger's permission for the whole account, so signing out on the
        // home PC (2026-11-11) quietly signed the laptop out as well. Anyone who wants HolyLogger's
        // permission gone everywhere can remove it in his Google account.
        public static void SignOut()
        {
            Save(new State());
            _access = null;
            Log.Warn("Google Drive: signed out on this computer.");
            SyncDiary.ReportContact(null);
        }

        // ── sending the diary ───────────────────────────────────────────────────────────────────────
        //
        // Copies the whole diary to Drive when it has grown since the last copy; does nothing when the
        // operator is not signed in or nothing is new. The file's Drive id and the folder's are kept, so a
        // normal send is one request. Runs on the diary's own timer thread, never the window's.
        // true = it was actually sent (Google Drive was reached)
        public static bool UploadDiary(string path, TimeSpan limit)
        {
            State st = Load();
            if (string.IsNullOrEmpty(st.RefreshToken) || !Registered || !File.Exists(path)) return false;
            long length = new FileInfo(path).Length;
            if (length == st.UploadedBytes && st.FileId != null) return false;

            using (var cts = new CancellationTokenSource(limit))
            {
                CancellationToken ct = cts.Token;
                if (string.IsNullOrEmpty(st.FolderId)) st.FolderId = FindOrMakeFolder(ct);
                byte[] data = File.ReadAllBytes(path);
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    if (string.IsNullOrEmpty(st.FileId)) st.FileId = FindOrMakeFile(st.FolderId, Path.GetFileName(path), ct);
                    var req = new HttpRequestMessage(new HttpMethod("PATCH"),
                        "https://www.googleapis.com/upload/drive/v3/files/" + st.FileId + "?uploadType=media")
                    { Content = new ByteArrayContent(data) };
                    req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
                    HttpResponseMessage res = Send(req, ct);
                    if (res.StatusCode == HttpStatusCode.NotFound && attempt == 0)
                    {
                        // the file was deleted in Drive (or the folder with it): find or make them again
                        st.FileId = null;
                        st.FolderId = FindOrMakeFolder(ct);
                        continue;
                    }
                    Check(res);
                    break;
                }
            }
            st.UploadedBytes = length;
            st.UploadedUtc = DateTime.UtcNow;
            Save(st);
            Log.Warn("Google Drive: diary sent (" + length + " bytes).");
            return true;
        }

        // ── reading the folder (step 4) ─────────────────────────────────────────────────────────────
        public sealed class DriveFile
        {
            public string Id, Name;
            public long Size;
            public DateTime? ModifiedUtc;
        }

        // The files in the HolyLogger Sync folder: every computer's diary (drive.file still shows them,
        // as HolyLogger made them all, on whichever computer).
        public static List<DriveFile> ListFolder(TimeSpan limit)
        {
            var list = new List<DriveFile>();
            State st = Load();
            if (string.IsNullOrEmpty(st.RefreshToken) || !Registered) return list;
            using (var cts = new CancellationTokenSource(limit))
            {
                if (string.IsNullOrEmpty(st.FolderId)) { st.FolderId = FindOrMakeFolder(cts.Token); Save(st); }
                string q = "'" + st.FolderId + "' in parents and trashed = false";
                JObject found = Get("https://www.googleapis.com/drive/v3/files?pageSize=1000&fields=files(id,name,size,modifiedTime)&q="
                                    + Uri.EscapeDataString(q), cts.Token).GetAwaiter().GetResult();
                if (found["files"] is JArray files)
                    foreach (JToken f in files)
                    {
                        long.TryParse((string)f["size"], out long size);
                        // Newtonsoft may already have made the time a DateTime (see SyncDiary.Pull)
                        DateTime? modified = null;
                        JToken m = f["modifiedTime"];
                        if (m != null && m.Type == JTokenType.Date) modified = m.Value<DateTime>().ToUniversalTime();
                        else if (m != null && DateTime.TryParse((string)m, System.Globalization.CultureInfo.InvariantCulture,
                                     System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out DateTime p))
                            modified = p;
                        list.Add(new DriveFile { Id = (string)f["id"], Name = (string)f["name"], Size = size, ModifiedUtc = modified });
                    }
            }
            return list;
        }

        // The file from byte `offset` to its end.
        public static byte[] Download(string fileId, long offset, TimeSpan limit)
        {
            using (var cts = new CancellationTokenSource(limit))
            {
                var req = new HttpRequestMessage(HttpMethod.Get, "https://www.googleapis.com/drive/v3/files/" + fileId + "?alt=media");
                if (offset > 0) req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(offset, null);
                HttpResponseMessage res = Check(Send(req, cts.Token));
                byte[] data = res.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                // a server that ignores the range sends the whole file: skip what was already read
                if (offset > 0 && res.StatusCode == HttpStatusCode.OK)
                    data = offset >= data.Length ? new byte[0] : data.Skip((int)offset).ToArray();
                return data;
            }
        }

        // ── big files (step 5: the whole database) ─────────────────────────────────────────────────
        //
        // A plain upload is for files up to 5 MB, so the database copy goes up in Google's "resumable"
        // way: ask for an upload address, then send 8 MB at a time, each piece tried again if the line
        // drops. Its own HttpClient, which does not follow redirects: Google answers 308 for "piece
        // received, send the next", and that must reach this code, not be followed.
        private static readonly HttpClient UploadHttp =
            new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };

        public static void UploadLarge(string path, string name, string contentType, TimeSpan limit)
        {
            State st = Load();
            if (string.IsNullOrEmpty(st.RefreshToken) || !Registered) return;
            using (var cts = new CancellationTokenSource(limit))
            {
                CancellationToken ct = cts.Token;
                if (string.IsNullOrEmpty(st.FolderId)) { st.FolderId = FindOrMakeFolder(ct); Save(st); }
                string fileId = FindOrMakeFile(st.FolderId, name, ct, contentType);
                long total = new FileInfo(path).Length;

                var init = new HttpRequestMessage(new HttpMethod("PATCH"),
                    "https://www.googleapis.com/upload/drive/v3/files/" + fileId + "?uploadType=resumable")
                { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
                init.Headers.Add("X-Upload-Content-Type", contentType);
                init.Headers.Add("X-Upload-Content-Length", total.ToString());
                Uri session = Check(Send(init, ct)).Headers.Location;
                if (session == null) throw new Exception("Google Drive gave no upload address.");

                const int Piece = 8 * 1024 * 1024;   // a multiple of 256 KB, as Google asks
                var buf = new byte[Piece];
                using (var fs = File.OpenRead(path))
                {
                    long pos = 0;
                    int stuck = 0;
                    while (pos < total)
                    {
                        fs.Position = pos;
                        int n = 0, want = (int)Math.Min(Piece, total - pos);
                        while (n < want) { int r = fs.Read(buf, n, want - n); if (r <= 0) break; n += r; }

                        HttpResponseMessage res = null;
                        for (int attempt = 0; ; attempt++)
                        {
                            var put = new HttpRequestMessage(HttpMethod.Put, session) { Content = new ByteArrayContent(buf, 0, n) };
                            put.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(pos, pos + n - 1, total);
                            try { res = UploadHttp.SendAsync(put, ct).GetAwaiter().GetResult(); }
                            catch (Exception) when (attempt < 3 && !ct.IsCancellationRequested) { Thread.Sleep(3000 * (attempt + 1)); continue; }
                            int code = (int)res.StatusCode;
                            if (code == 308 || code == 200 || code == 201) break;
                            if (code >= 500 && attempt < 3) { Thread.Sleep(3000 * (attempt + 1)); continue; }
                            Check(res);
                        }

                        if ((int)res.StatusCode != 308) { pos = total; break; }
                        // "Range: bytes=0-N" = what Google has so far; carry on from there
                        long had = pos;
                        if (res.Headers.TryGetValues("Range", out IEnumerable<string> rv))
                        {
                            string r = rv.First();
                            if (long.TryParse(r.Substring(r.LastIndexOf('-') + 1), out long last)) pos = last + 1;
                        }
                        stuck = pos == had ? stuck + 1 : 0;
                        if (stuck >= 3) throw new Exception("Google Drive stopped taking the file.");
                    }
                }
            }
        }

        // The whole file, written to `path` as it arrives (never all in memory), telling `progress`
        // how far it has got: (bytes so far, bytes in all - or -1 when Google did not say).
        public static void DownloadToFile(string fileId, string path, Action<long, long> progress, TimeSpan limit)
        {
            using (var cts = new CancellationTokenSource(limit))
            {
                CancellationToken ct = cts.Token;
                var req = new HttpRequestMessage(HttpMethod.Get, "https://www.googleapis.com/drive/v3/files/" + fileId + "?alt=media");
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", AccessToken(ct));
                using (HttpResponseMessage res = Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).GetAwaiter().GetResult())
                {
                    Check(res);
                    long total = res.Content.Headers.ContentLength ?? -1;
                    using (Stream s = res.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                    using (var f = File.Create(path))
                    {
                        var buf = new byte[1024 * 1024];
                        long done = 0;
                        int n;
                        while ((n = s.Read(buf, 0, buf.Length)) > 0)
                        {
                            ct.ThrowIfCancellationRequested();
                            f.Write(buf, 0, n);
                            done += n;
                            progress?.Invoke(done, total);
                        }
                    }
                }
            }
        }

        private static string FindOrMakeFolder(CancellationToken ct)
        {
            string q = "name = '" + FolderName + "' and mimeType = 'application/vnd.google-apps.folder' and trashed = false";
            JObject found = Get("https://www.googleapis.com/drive/v3/files?orderBy=createdTime&fields=files(id)&q=" + Uri.EscapeDataString(q), ct).GetAwaiter().GetResult();
            var files = (JArray)found["files"];
            if (files != null && files.Count > 0) return (string)files[0]["id"];
            JObject made = PostJson("https://www.googleapis.com/drive/v3/files?fields=id",
                new JObject { ["name"] = FolderName, ["mimeType"] = "application/vnd.google-apps.folder" }, ct);
            return (string)made["id"];
        }

        private static string FindOrMakeFile(string folderId, string name, CancellationToken ct, string mimeType = "text/plain")
        {
            string q = "name = '" + name + "' and '" + folderId + "' in parents and trashed = false";
            JObject found = Get("https://www.googleapis.com/drive/v3/files?fields=files(id)&q=" + Uri.EscapeDataString(q), ct).GetAwaiter().GetResult();
            var files = (JArray)found["files"];
            if (files != null && files.Count > 0) return (string)files[0]["id"];
            JObject made = PostJson("https://www.googleapis.com/drive/v3/files?fields=id",
                new JObject { ["name"] = name, ["parents"] = new JArray(folderId), ["mimeType"] = mimeType }, ct);
            return (string)made["id"];
        }

        // ── the plumbing ────────────────────────────────────────────────────────────────────────────
        private static string AccessToken(CancellationToken ct)
        {
            if (_access != null && DateTime.UtcNow < _accessUntil) return _access;
            JObject tok = PostForm("https://oauth2.googleapis.com/token", new Dictionary<string, string>
            {
                ["refresh_token"] = Load().RefreshToken,
                ["client_id"] = _clientId,
                ["client_secret"] = _clientSecret,
                ["grant_type"] = "refresh_token",
            }, ct).GetAwaiter().GetResult();
            _access = (string)tok["access_token"];
            _accessUntil = DateTime.UtcNow.AddSeconds(((int?)tok["expires_in"] ?? 3600) - 120);
            return _access;
        }

        private static HttpResponseMessage Send(HttpRequestMessage req, CancellationToken ct)
        {
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", AccessToken(ct));
            return Http.SendAsync(req, ct).GetAwaiter().GetResult();
        }

        private static async Task<JObject> Get(string url, CancellationToken ct)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", AccessToken(ct));
            HttpResponseMessage res = await Http.SendAsync(req, ct).ConfigureAwait(false);
            return JObject.Parse(await Check(res).Content.ReadAsStringAsync().ConfigureAwait(false));
        }

        private static JObject PostJson(string url, JObject body, CancellationToken ct)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, url)
            { Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json") };
            HttpResponseMessage res = Send(req, ct);
            return JObject.Parse(Check(res).Content.ReadAsStringAsync().GetAwaiter().GetResult());
        }

        private static async Task<JObject> PostForm(string url, Dictionary<string, string> form, CancellationToken ct)
        {
            HttpResponseMessage res = await Http.PostAsync(url, new FormUrlEncodedContent(form), ct).ConfigureAwait(false);
            string text = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                // a refresh token Google no longer accepts (taken back in the account, or a test-mode sign-in
                // that ran out) means signed out - he is asked again instead of failing every minute
                if (text.Contains("invalid_grant")) { Save(new State()); _access = null; }
                throw new Exception("Google refused (" + (int)res.StatusCode + "): " + text);
            }
            return JObject.Parse(text);
        }

        private static HttpResponseMessage Check(HttpResponseMessage res)
        {
            if (res.IsSuccessStatusCode) return res;
            string text = res.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            throw new Exception("Google Drive answered " + (int)res.StatusCode + ": " + (text.Length > 300 ? text.Substring(0, 300) : text));
        }

        private static string ReadRequest(NetworkStream stream)
        {
            var buf = new byte[8192];
            var sb = new StringBuilder();
            stream.ReadTimeout = 10000;
            while (!sb.ToString().Contains("\r\n\r\n"))
            {
                int n = stream.Read(buf, 0, buf.Length);
                if (n <= 0) break;
                sb.Append(Encoding.ASCII.GetString(buf, 0, n));
                if (sb.Length > 65536) break;
            }
            return sb.ToString();
        }

        private static Dictionary<string, string> ParseQuery(string request)
        {
            var map = new Dictionary<string, string>();
            string first = request.Split('\n')[0];              // GET /?code=...&state=... HTTP/1.1
            int q = first.IndexOf('?');
            int sp = first.LastIndexOf(' ');
            if (q < 0 || sp <= q) return map;
            foreach (string pair in first.Substring(q + 1, sp - q - 1).Split('&'))
            {
                int eq = pair.IndexOf('=');
                if (eq > 0) map[Uri.UnescapeDataString(pair.Substring(0, eq))] = Uri.UnescapeDataString(pair.Substring(eq + 1).Replace('+', ' '));
            }
            return map;
        }

        private static void Answer(NetworkStream stream, string text)
        {
            string html = "<html><head><meta charset=\"utf-8\"><title>HolyLogger</title></head>"
                + "<body style=\"font-family:Segoe UI,Arial;font-size:20px;margin:60px\">" + WebUtility.HtmlEncode(text) + "</body></html>";
            byte[] body = Encoding.UTF8.GetBytes(html);
            byte[] head = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: "
                + body.Length + "\r\nConnection: close\r\n\r\n");
            stream.Write(head, 0, head.Length);
            stream.Write(body, 0, body.Length);
            stream.Flush();
        }

        private static byte[] RandomBytes(int n)
        {
            var b = new byte[n];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
            return b;
        }

        private static string Base64Url(byte[] b)
        {
            return Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
    }
}
