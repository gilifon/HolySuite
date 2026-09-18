using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using HolyParser;

namespace HolyLogger
{
    // WATCHING ADIF FILES OTHER PROGRAMS WRITE.
    //
    // The list is kept in Options > General > ADIF Monitor Manager (AdifMonitorWindow). Every ticked
    // file is checked at start - which picks up the contacts made while HolyLogger was closed - and
    // then every few seconds. New contacts are added only after the operator says yes (see below).
    //
    // Each file remembers how far in it has been read (AdifMonitorEntry.Position, in bytes). A check
    // jumps straight to that byte and reads only what came after it. It stops at the last <eor> it
    // finds, so a contact the other program is still in the middle of writing is left for the next
    // check. A file that has become SHORTER than that position was replaced or emptied, and is read
    // again from the start.
    //
    // THE SAME CONTACT OVER UDP. WSJT-X and its like usually send a contact over UDP as well as writing
    // it to their file. Every contact read from the file goes through the program's one duplicate rule
    // (DataAccess.MatchKey) against the active log: one that is already there is passed over, and is
    // not counted in the question below.
    public partial class MainWindow
    {
        // Contacts are added through the same code a UDP contact takes (LogQsoFromUdp), so both paths
        // fill in, check and store a contact in the same way.
        private const int AdifMonitorIntervalSeconds = 5;

        // Read at most this much of a file per step, so a big file added for the first time is not
        // loaded into memory in one piece (the program is 32-bit).
        private const int AdifMonitorChunkBytes = 1024 * 1024;

        private DispatcherTimer _adifMonitorTimer;
        private List<AdifMonitorEntry> _adifMonitorFiles = new List<AdifMonitorEntry>();
        private int _adifMonitorBusy;   // 1 while a check is running; a tick that finds it busy does nothing

        // Re-reads the list and checks every ticked file at once. Called at startup and whenever the
        // Options window closes, since the list may have just changed there.
        internal void ApplyAdifMonitors()
        {
            try
            {
                _adifMonitorFiles = AdifMonitorStore.Load().Where(f => f.IsOn && !string.IsNullOrWhiteSpace(f.Path)).ToList();

                if (_adifMonitorFiles.Count == 0)
                {
                    StopAdifMonitors();
                    return;
                }

                if (_adifMonitorTimer == null)
                {
                    _adifMonitorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(AdifMonitorIntervalSeconds) };
                    _adifMonitorTimer.Tick += AdifMonitorTimer_Tick;
                }
                _adifMonitorTimer.Start();
                AdifMonitorTimer_Tick(null, EventArgs.Empty);
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        internal void StopAdifMonitors()
        {
            try
            {
                if (_adifMonitorTimer != null) _adifMonitorTimer.Stop();
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        private void AdifMonitorTimer_Tick(object sender, EventArgs e)
        {
            if (_isShutdownCleanupDone) return;
            if (Interlocked.CompareExchange(ref _adifMonitorBusy, 1, 0) != 0) return;

            var files = _adifMonitorFiles.ToList();
            Task.Run(async () =>
            {
                try
                {
                    foreach (var file in files)
                    {
                        if (_isShutdownCleanupDone) return;
                        await CheckAdifMonitorFile(file);
                    }
                }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
                finally { Interlocked.Exchange(ref _adifMonitorBusy, 0); }
            });
        }

        // ASKING FIRST. New contacts are never added without the operator's yes: he may have switched to
        // a different log since the other program wrote them, and they would land there without him
        // knowing. The question names how many and which log.
        //
        // No: the file is left alone for the rest of this run - not read, and its position not moved -
        // so the same contacts are offered again at the next start. Contacts that came in over UDP in
        // the meantime are in the log by then, and the duplicate rule drops them from that count.
        //
        // Yes: they are added, and later contacts from that file go into the SAME log without asking
        // again, so a running FT8 session does not stop for a question after every contact. If the
        // active log is a different one when the next contacts arrive, the question comes back.
        private readonly HashSet<string> _adifMonitorDeclined = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, long> _adifMonitorApprovedLog = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        // Runs off the window's thread. Reads what was added to one file and, once allowed, logs the new contacts.
        private async Task CheckAdifMonitorFile(AdifMonitorEntry file)
        {
            if (!File.Exists(file.Path)) return;   // not there right now (a drive not connected); try later
            if (Dispatcher.Invoke(() => _adifMonitorDeclined.Contains(file.Path))) return;

            long startPosition = file.Position;
            long position = file.Position;
            HashSet<string> keysInLog = null;       // built once, only when there is something to check
            var newRecords = new List<string>();
            // Contacts we already have that arrive carrying a comment - joined onto the stored one at
            // the end, after the new contacts are in (a repeat inside this same batch needs its first
            // copy stored before there is anything to join onto).
            var commentRepeats = new List<QSO>();

            // Read everything after the saved position first, so the question can say how many.
            while (!_isShutdownCleanupDone)
            {
                List<string> records;
                long newPosition;
                if (!ReadNewAdifRecords(file.Path, position, out records, out newPosition)) break;
                if (newPosition == position) break;   // nothing new, or only a half-written contact

                if (records.Count > 0 && keysInLog == null)
                    keysInLog = Dispatcher.Invoke(() => new HashSet<string>(
                        Qsos.Select(q => DataAccess.MatchKey(q)).Where(k => !string.IsNullOrEmpty(k)),
                        StringComparer.Ordinal));

                foreach (string record in records)
                {
                    try
                    {
                        QSO qso = new HolyLogParser().ParseRawQSO(record);
                        if (qso == null) continue;
                        // The band as LogQsoFromUdp sets it, so the key matches the one it would store.
                        if (!string.IsNullOrWhiteSpace(qso.Freq))
                            qso.Band = HolyLogParser.convertFreqToBand(qso.Freq);

                        string key = DataAccess.MatchKey(qso);
                        if (!string.IsNullOrEmpty(key) && keysInLog.Contains(key))   // already have it (e.g. came over UDP)
                        {
                            if (!string.IsNullOrWhiteSpace(qso.Comment)) commentRepeats.Add(qso);
                            continue;
                        }
                        if (!string.IsNullOrEmpty(key)) keysInLog.Add(key);   // a contact written twice in the file counts once

                        newRecords.Add(record);
                    }
                    catch (Exception swallowed) { Log.Swallow(swallowed); }
                }

                position = newPosition;
            }

            if (_isShutdownCleanupDone || Dispatcher.HasShutdownStarted) return;

            if (newRecords.Count > 0)
            {
                bool allowed = Dispatcher.Invoke(() =>
                {
                    if (dal == null || dal.ActiveLogId <= 0) return false;   // no log open: ask later
                    long logId = dal.ActiveLogId;
                    if (_adifMonitorApprovedLog.TryGetValue(file.Path, out long approved) && approved == logId) return true;

                    string logName = null;
                    try { logName = dal.GetLogName(logId); } catch (Exception swallowed) { Log.Swallow(swallowed); }
                    string count = newRecords.Count == 1 ? "1 new contact" : newRecords.Count + " new contacts";
                    bool yes = HolyMessageBox.ShowConfirm(
                        count + " in " + Path.GetFileName(file.Path) + "." + Environment.NewLine
                        + "Add them to the log " + (logName ?? "") + "?",
                        "ADIF Monitor", HolyMsgType.Info, this, yesText: "Yes", noText: "No");
                    if (yes) _adifMonitorApprovedLog[file.Path] = logId;
                    else
                    {
                        _adifMonitorDeclined.Add(file.Path);
                        _adifMonitorApprovedLog.Remove(file.Path);
                    }
                    return yes;
                });

                // Not allowed: the position stays where it was, so nothing is lost.
                if (!allowed) return;

                foreach (string record in newRecords)
                {
                    if (_isShutdownCleanupDone) return;
                    try { await LogQsoFromUdp(record); }
                    catch (Exception swallowed) { Log.Swallow(swallowed); }
                }
            }

            // The repeats' comments, joined on without asking - the same as a repeat over UDP.
            if (commentRepeats.Count > 0 && !_isShutdownCleanupDone && !Dispatcher.HasShutdownStarted)
                Dispatcher.Invoke(() =>
                {
                    foreach (QSO repeat in commentRepeats)
                    {
                        QSO stored = FindInLog(repeat);
                        if (stored != null) MergeCommentIntoLog(stored, repeat.Comment);
                    }
                });

            file.Position = position;
            if (file.Position != startPosition && !_isShutdownCleanupDone && !Dispatcher.HasShutdownStarted)
            {
                string path = file.Path;
                long saved = file.Position;
                Dispatcher.Invoke(() => AdifMonitorStore.SavePosition(path, saved));
            }
        }

        // Reads from 'position' up to the last complete record (at most one chunk). Returns the records
        // found and the byte just after the last <eor>. False when the file cannot be opened right now.
        private static bool ReadNewAdifRecords(string path, long position, out List<string> records, out long newPosition)
        {
            records = new List<string>();
            newPosition = position;
            try
            {
                // Shared read and write: the other program has this file open and keeps writing to it.
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    long length = fs.Length;
                    if (length < position) position = 0;   // replaced or emptied: start again
                    newPosition = position;
                    if (length == position) return true;

                    int toRead = (int)Math.Min(AdifMonitorChunkBytes, length - position);
                    var buffer = new byte[toRead];
                    fs.Seek(position, SeekOrigin.Begin);   // straight to where the last check stopped
                    int got = 0;
                    while (got < toRead)
                    {
                        int n = fs.Read(buffer, got, toRead - got);
                        if (n <= 0) break;
                        got += n;
                    }

                    int end = LastEorEnd(buffer, got);
                    if (end < 0)
                    {
                        // No complete record yet. Only if a whole chunk holds no <eor> at all is it
                        // stepped over, or a broken file would stop the reader for good.
                        if (got == AdifMonitorChunkBytes) newPosition = position + got;
                        return true;
                    }

                    string text = Encoding.UTF8.GetString(buffer, 0, end);
                    foreach (string piece in SplitOnEor(text))
                    {
                        string record = ExtractAdifRecord(piece + "<eor>");
                        if (record != null) records.Add(record);
                    }
                    newPosition = position + end;
                    return true;
                }
            }
            catch (Exception swallowed)
            {
                Log.Swallow(swallowed);
                return false;
            }
        }

        // The byte just after the last "<eor>" (any case) in the buffer, or -1 when there is none.
        private static int LastEorEnd(byte[] buffer, int count)
        {
            for (int i = count - 5; i >= 0; i--)
            {
                if (buffer[i] == (byte)'<'
                    && (buffer[i + 1] | 0x20) == (byte)'e'
                    && (buffer[i + 2] | 0x20) == (byte)'o'
                    && (buffer[i + 3] | 0x20) == (byte)'r'
                    && buffer[i + 4] == (byte)'>')
                    return i + 5;
            }
            return -1;
        }

        private static IEnumerable<string> SplitOnEor(string text)
        {
            int start = 0;
            while (start < text.Length)
            {
                int eor = text.IndexOf("<eor>", start, StringComparison.OrdinalIgnoreCase);
                if (eor < 0) yield break;
                yield return text.Substring(start, eor - start);
                start = eor + 5;
            }
        }
    }
}
