using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
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
    // For now the diary only grows here, in <data folder>\Sync\<this computer's id>.diary. Nothing is
    // sent anywhere yet: this step is about the diary missing nothing.
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

        public static void Start()
        {
            _timer = new Timer(_ => FlushSafe(), null, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1));
        }

        public static void Stop()
        {
            try { _timer?.Dispose(); } catch (Exception swallowed) { Log.Swallow(swallowed); }
            _timer = null;
            FlushSafe();
        }

        private static void FlushSafe()
        {
            try { Flush(); }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
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
}
