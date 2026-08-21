using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.Win32;
using System.Collections.Specialized;
using System.Threading;
using System.Net;
using System.Xml.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using DXCCManager;
using HolyParser;
using System.Diagnostics;
using System.Net.Cache;
using System.Globalization;
using Blue.Windows;
using System.Windows.Media.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using System.Windows.Documents;
using System.Net.NetworkInformation;
using System.Windows.Media;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Windows.Controls.Primitives;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Text.RegularExpressions;
using System.Data.SQLite;

namespace HolyLogger
{
    // Import/export: ADIF import worker, ADIF/CSV/Cabrillo export, autosave snapshots.
    // Move-only split from MainWindow.xaml.cs; no behavior change.
    public partial class MainWindow
    {

        private sealed class AdifImportResult
        {
            public int FaultyQso { get; set; }
            public int ImportedQsoCount { get; set; }
            // Records that turned out to be QSOs the log ALREADY had: they filled in that QSO's empty
            // fields instead of being added again. See DataAccess.CompleteExistingQsos.
            public int CompletedQsoCount { get; set; }
            // Matched a QSO in the log but could not be told apart from another one, so left alone.
            public int AmbiguousQsoCount { get; set; }
            public ObservableCollection<QSO> RefreshedQsos { get; set; }
            // Records the file held that could not be stored, and where the operator can read about
            // them. RejectsAdifPath is the same records as a file they can correct and import again.
            public int RejectedCount { get; set; }
            public int FilledCount { get; set; }
            // There are no country counts here any more. Whether a QSO names the right country is the
            // Log Fixer's judgement and only its judgement; this class reports what the IMPORT did.
            // Records the file(s) held, so the completion message can say what was CHECKED and not
            // leave "nothing was rejected" to be inferred from the absence of a line.
            public int RecordsRead { get; set; }
            public string ReportPath { get; set; }
            public string RejectsAdifPath { get; set; }
            // The operator pressed Stop. Everything else in here is then a PART of the file, which is
            // a different thing from the whole of it and has to be said out loud rather than left to
            // look like an import that simply found less than expected.
            public bool Stopped { get; set; }
            // Stopped while the file was still being READ, before one QSO had been written. The
            // difference decides what can be offered afterwards: nothing to clean up, or a log with
            // part of a file in it.
            public bool StoppedBeforeAnythingWasStored { get; set; }
            // What the rollback put back, when the import went into a log that already existed: QSOs
            // taken out again, and fields emptied again. UndoFailed says the log is NOT as it was.
            public int UndoneQsoCount { get; set; }
            public int UndoneFieldCount { get; set; }
            public bool UndoFailed { get; set; }
            // Replace: how many of the log's own QSOs were removed once the file was safely in, and
            // whether that step was skipped because the file brought nothing worth replacing them with.
            public int ReplacedQsoCount { get; set; }
            public bool ReplaceLeftAlone { get; set; }
            // The new log this import created, when that is what it was for - so a stopped import can
            // offer to take it away again. 0 when the import went into a log that already existed.
            public long NewLogId { get; set; }
            public string NewLogName { get; set; }
        }

        // One record that did not make it into the log, from whichever stage turned it away: the
        // parser (a field it cannot do without) or the database (the INSERT itself). Both end up in
        // the same report, because to the operator they are the same thing - a contact in their file
        // that is not in their log.
        private sealed class ImportReject
        {
            public string FileName { get; set; }
            public int Number { get; set; }
            public string Reason { get; set; }
            public string Raw { get; set; }
            public string Call { get; set; }
            public string Date { get; set; }
            public string Time { get; set; }
            public string Band { get; set; }
            public string Mode { get; set; }

            public string Describe()
            {
                string F(string v, int w)
                {
                    v = string.IsNullOrWhiteSpace(v) ? "—" : v.Trim();
                    return v.Length >= w ? v : v.PadRight(w);
                }
                return $"{F(Call, 12)} {F(Date, 10)} {F(Time, 6)} {F(Band, 6)} {F(Mode, 6)}";
            }
        }

        List<string> ImportFileQ = new List<string>();

        // Reusable ADIF export of a given QSO list (used by the File menu and by View Logs per-log).
        public void ExportQsosToAdif(System.Collections.ObjectModel.ObservableCollection<QSO> qsos, Window owner)
        {
            // A file written FOR THE OPERATOR carries everything their QSOs arrived with, including the
            // fields HolyLogger has no column for - an export they can re-import anywhere and still have
            // the log they started with. (Service uploads deliberately do not; see GenerateAdif.)
            //
            // Those carried fields are NOT loaded with the log - they are 93% of its weight and nothing
            // on screen uses them - so they are fetched here, for these QSOs, just before writing.
            try { dal?.FillCarriedAdif(qsos); }
            catch (Exception swallowed) { Log.Swallow(swallowed); }

            string adif = Services.GenerateAdif(qsos, Contests.ContestService.Active?.CabrilloName,
                                                includeImportedFields: true);
            var save = new SaveFileDialog { Filter = "ADIF File|*.adi", Title = "Export ADIF" };
            if (save.ShowDialog() != true) return;
            try
            {
                System.IO.File.WriteAllText(save.FileName, adif);
                HolyMessageBox.ShowSuccess("File created successfully!", "Export ADIF", owner);
            }
            catch (Exception ex) { HolyMessageBox.ShowError("Export failed: " + ex.Message, "Export ADIF", owner); }
        }

        public void ExportQsosToCsv(System.Collections.ObjectModel.ObservableCollection<QSO> qsos, Window owner)
        {
            string csv = Services.GenerateCSV(qsos);
            var save = new SaveFileDialog { Filter = "CSV File|*.csv", Title = "Export CSV" };
            if (save.ShowDialog() != true) return;
            try
            {
                System.IO.File.WriteAllText(save.FileName, csv);
                HolyMessageBox.ShowSuccess("File created successfully!", "Export CSV", owner);
            }
            catch (Exception ex) { HolyMessageBox.ShowError("Export failed: " + ex.Message, "Export CSV", owner); }
        }

        // Reusable Cabrillo export of one log's QSOs. The CONTEST: header MUST name the contest that
        // THIS log was collected under — its official Cabrillo name — not a global/last-used setting.
        // logId identifies the log so its stored contest (event_type) can be resolved; a regular
        // (non-contest) log or an unknown event type leaves CONTEST blank rather than claiming one.
        public void ExportQsosToCabrillo(System.Collections.ObjectModel.ObservableCollection<QSO> qsos, long logId, Window owner)
        {
            string eventType = null;
            try { eventType = dal.GetLogEventType(logId); } catch (Exception swallowed) { Log.Swallow(swallowed); }
            Contests.Contest logContest = Contests.ContestService.FindById(eventType);

            // Gather the Cabrillo header fields. For a real contest, the required fields MUST be filled
            // or the file is non-standard: if anything is missing, force the info window (no skip) and
            // abort the export if the operator cancels. For a non-contest log we don't enforce.
            // Storage key for this log's header values: the recognized contest id, else the raw log
            // event type (so a legacy / unrecognized contest still persists what the operator enters),
            // else null (in-memory only for this one export).
            string storeKey = !string.IsNullOrWhiteSpace(logContest?.Id) ? logContest.Id
                            : (!string.IsNullOrWhiteSpace(eventType) ? eventType : null);

            var values = Contests.ContestHeaderStore.Load(storeKey);

            // The station callsign is read-only in the info window (it comes from the main-window
            // Station callsign box) and is always required for a valid Cabrillo file, so it can only be
            // fixed there — check it up front before opening the window.
            values.TryGetValue("CALLSIGN", out var stationCall);
            if (string.IsNullOrWhiteSpace(stationCall))
            {
                HolyMessageBox.ShowWarning(
                    "Set your station callsign in the main window's \"Station callsign\" box before exporting a Cabrillo file.",
                    "Station callsign", owner ?? this);
                return;
            }

            // ALWAYS open the info window so the operator can review, edit and verify every header field
            // before the file is written — even when the fields are already complete, and even when the
            // log's contest can't be identified (a null contest falls back to the generic default field
            // set). Cancelling aborts the export, so no unreviewed file is ever produced.
            var info = new ContestInfoWindow(logContest, values, exportMode: true) { Owner = owner ?? this };
            info.ShowDialog();
            if (!info.Completed) return;
            Contests.ContestHeaderStore.Save(storeKey, info.Values);
            values = info.Values;   // export exactly what the operator just reviewed

            Contester c = new Contester { Contest = logContest?.CabrilloName ?? string.Empty };
            Contests.ContestHeaderStore.PopulateContester(c, values);
            string cabrillo = Services.GenerateCabrillo(qsos, c);
            var save = new SaveFileDialog { Filter = "Cabrillo File (*.cbr)|*.cbr|Cabrillo Log (*.log)|*.log", DefaultExt = "cbr", Title = "Export Cabrillo" };
            if (save.ShowDialog() != true) return;
            try
            {
                System.IO.File.WriteAllText(save.FileName, cabrillo);
                HolyMessageBox.ShowSuccess("File created successfully!", "Export Cabrillo", owner);
            }
            catch (Exception ex) { HolyMessageBox.ShowError("Export failed: " + ex.Message, "Export Cabrillo", owner); }
        }

        // ---- QRZ.com Logbook real-time upload --------------------------------------------------------

        // Serializes one QSO Plus terminates it with a single <EOR>. Reuses the app's canonical ADIF
        // generator and strips the file header (<adif_ver>...<eoh>) so only the record block remains,
        // which is what the QRZ Logbook API's ADIF parameter expects.
        private static string BuildQrzAdif(QSO qso)
        {
            string adif = Services.GenerateAdif(new System.Collections.Generic.List<QSO> { qso });
            int idx = adif.IndexOf("<eoh>", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0) adif = adif.Substring(idx + "<eoh>".Length);
            return adif.Trim();
        }
        

        // Public entry point so the Log Manager's "Import ADIF" button runs the same import flow.
        public void ImportAdif() => ImportAdifMenuItem_Click(null, null);

        // async so the two steps that take seconds - scanning the file for its callsigns and writing the
        // backup a replace makes - can run off this thread with a message standing on the screen.
        private async void ImportAdifMenuItem_Click(object sender, RoutedEventArgs e)
        {
            // Offer to save an in-progress new QSO before an import reloads the log.
            GuardUnsavedQso("import the ADIF file");

            //CultureInfo provider = CultureInfo.InvariantCulture;
            OpenFileDialog openFileDialog = new OpenFileDialog();
            openFileDialog.Filter = "ADIF files (*.adi)|*.adi";
            

            if (openFileDialog.ShowDialog() == true)
            {
                // WHO MADE THIS FILE, read before a single question is asked about it. The scan walks
                // the file line by line and keeps nothing but the callsigns, so it is cheap next to the
                // parse - and it lets the one question that can send the operator away be asked first.
                // Nothing below asks for it again; the lists are carried down.
                ShowBusyOverlay("Reading the file\u2026");
                string scanPath = openFileDialog.FileName;
                System.Collections.Generic.List<string> scannedCalls = null, scannedOps = null;
                try
                {
                    await System.Threading.Tasks.Task.Run(() =>
                        ScanAdifIdentity(scanPath, out scannedCalls, out scannedOps));
                }
                finally { HideBusyOverlay(); }
                var adifCalls = scannedCalls ?? new System.Collections.Generic.List<string>();
                var adifOps = scannedOps ?? new System.Collections.Generic.List<string>();

                // "Not my callsign - import anyway?" comes before everything else. Answering No after
                // naming a new log, or after choosing merge or replace, wastes all of that.
                if (!ApproveDifferentStationCallsign(openFileDialog.FileName, adifCalls)) return;

                // Next: does this file become its OWN new log, or get added to the log open now?
                // With NO log open there is no "log open now" to add to, so the question is not asked -
                // the file becomes its own new log, which is the only answer there is. Import is
                // deliberately NOT blocked when no log is open: bringing a file in is exactly how an
                // operator with no logs gets one.
                bool noLogOpen = dal != null && !dal.HasActiveLog;
                ImportTarget target = AskImportTarget(noLogOpen);
                if (target == ImportTarget.Cancel) return;

                _importChoice = ImportChoice.NewLog;   // corrected below if it goes into the log open now

                // THE LOG THIS IMPORT MAKES, remembered so that abandoning the import can take it away
                // again. It has to exist before the import can write into it, and the operator can
                // still back out after it exists - one more dialog stands between here and the reading.
                // Backing out used to leave the empty log behind for ever; two of them were sitting in
                // the Log Manager, named after a file that never went in.
                long createdLogId = 0;

                if (target == ImportTarget.NewLog)
                {
                    // Create a new REGULAR log for the file and make it active so the import lands in it —
                    // nothing touches the existing logs. Its identity comes from the ADIF (below).
                    string suggested = UniqueLogName(System.IO.Path.GetFileNameWithoutExtension(openFileDialog.FileName));
                    var nameDlg = new NewLogWindow(dal, "Name the new log for the imported file:", suggested) { Owner = this };
                    if (nameDlg.ShowDialog() != true) return;   // cancelled -> abort, nothing created yet
                    createdLogId = dal.CreateLog(nameDlg.LogName, string.Empty);
                    SwitchActiveLog(createdLogId);
                }
                else
                {
                    // Into the current log: offer MERGE (add) or REPLACE if the ACTIVE log has QSOs.
                    int existing = 0;
                    try { if (dal != null) existing = dal.GetQsoCountForLog(dal.ActiveLogId); }
                    catch { existing = 0; }

                    _importChoice = ImportChoice.Merge;   // an empty log open: nothing to replace

                    if (existing > 0)
                    {
                        ImportLogChoice choice = AskImportMergeOrReplace(existing);
                        if (choice == ImportLogChoice.Cancel)
                            return;
                        if (choice == ImportLogChoice.Replace && !await BackupLogForReplace())
                            return; // backup cancelled or failed -> abort; the log is left untouched
                        _importChoice = choice == ImportLogChoice.Replace ? ImportChoice.Replace
                                                                          : ImportChoice.Merge;
                    }
                }

                // Identity handling before the import runs. Scan the ADIF for the callsign(s) / operator(s)
                // it was made under.
                if (dal != null)
                {
                    string fileName = System.IO.Path.GetFileName(openFileDialog.FileName);

                    if (!dal.LogHasIdentity(dal.ActiveLogId))
                    {
                        // No identity yet -> confirm (and let the user cancel) the identity the imported
                        // log will get. Station callsign from the ADIF (not editable); operator editable.
                        var idDlg = new ImportIdentityWindow(adifCalls, adifOps, fileName) { Owner = this };
                        if (idDlg.ShowDialog() != true) { AbandonNewLog(createdLogId); return; }
                        _pendingImportCallsign = idDlg.Callsign;
                        _pendingImportOperator = idDlg.Operator;
                    }
                    else
                    {
                        // The log has a permanent identity. If the file was made under a different callsign
                        // or operator, the user must knowingly approve mixing those QSOs in (any that don't
                        // match the log's identity won't be copied to a copy-target).
                        dal.GetLogIdentity(dal.ActiveLogId, out string idCall, out string idOp);
                        bool callDiff = adifCalls.Any(c => !CallsignIdentity.Same(c, idCall));
                        bool opDiff = adifOps.Any(o => !string.Equals(o, idOp, System.StringComparison.OrdinalIgnoreCase));
                        if (callDiff || opDiff)
                        {
                            string fileId = (adifCalls.Count > 0 ? string.Join(", ", adifCalls) : "(no station callsign)")
                                          + "  /  " + (adifOps.Count > 0 ? string.Join(", ", adifOps) : "(no operator)");
                            if (!HolyMessageBox.ShowConfirm(
                                    "The file \"" + fileName + "\" was made under:\n    " + fileId + "\n\n" +
                                    "That differs from this log's permanent identity:\n    " + idCall + " / " + idOp + "\n\n" +
                                    "The QSOs will be added, but any that don't match this log's identity will NOT be copied to a copy-target. Import anyway?",
                                    "Different callsign / operator", HolyMsgType.Warning, this))
                            { AbandonNewLog(createdLogId); return; }   // declined -> abort the import
                        }
                    }
                }

                ImportFileQ.Add(openFileDialog.FileName);
                StartAdifImportWorker();
            }
        }

        // THE EMPTY LOG AN ABANDONED IMPORT LEAVES BEHIND, taken away again.
        //
        // A log created for a file that never went in is not a log: it is named after that file, holds
        // nothing, and sits in the Log Manager for ever waiting to be noticed and deleted by hand. It
        // is only ever called with a log this import made a moment ago, and only when the import is
        // being abandoned - never with a log that existed before.
        private void AbandonNewLog(long logId)
        {
            if (logId <= 0 || dal == null) return;
            try
            {
                if (logId == dal.ActiveLogId) CloseActiveLog();
                dal.DeleteLog(logId);
                RefreshCopyIndicator();
                _pendingImportCallsign = null;
                _pendingImportOperator = null;
                UpdateNumOfQSOs();
            }
            catch (Exception ex)
            {
                // Not worth a dialog: the operator has just cancelled something and an empty log is
                // harmless. It goes in the log file so it can be explained if he ever asks.
                Log.Warn("Could not remove the empty log an abandoned import created: " + ex);
            }
        }

        // Identity confirmed in the import dialog; applied to the log once the import finishes.
        private string _pendingImportCallsign;
        private string _pendingImportOperator;

        // WHICH OF THE THREE THE OPERATOR CHOSE, kept for the report. The choice was made in a dialog,
        // acted on, and forgotten - so the report could say what the import DID but never what it had
        // been asked to do, and "Added to the log: 0" reads very differently under "a new log for this
        // file" than under "replace the log with this file". A file dropped on the grid asks nothing
        // and goes into the log open now, which is Merge.
        private enum ImportChoice { NewLog, Merge, Replace }
        private ImportChoice _importChoice = ImportChoice.Merge;

        // Peeks at an ADIF file for its station callsign(s) and operator(s) so the imported log's identity
        // can be confirmed before importing. Distinct values, most-frequent first. Best-effort.
        // Reads the file ONCE, line by line, collecting both fields together.
        //
        // This runs on the UI thread before the import worker starts. It used to call ScanAdifField
        // twice, and each call did File.ReadAllText on the whole log - so a 70 MB ADIF meant two
        // 140 MB strings plus two match collections of ~28,000 Match objects each, before a single QSO
        // had been parsed. In a 32-bit process capped at 2 GB that was a large part of what made a big
        // logbook fail to import at all. Streaming keeps only one line in memory.
        //
        // Reading by line is safe here because an ADIF tag never contains a line break, and neither a
        // station callsign nor an operator ever does.
        // "THIS FILE WAS MADE UNDER A DIFFERENT CALLSIGN - IMPORT IT ANYWAY?", asked BEFORE the file is
        // read instead of after.
        //
        // It used to be asked by the import worker, from the parsed QSOs, which meant the operator sat
        // through the whole file - three minutes on a big logbook, the spinner reading "Parsing ADIF
        // 100%" - and was only then asked whether he wanted it at all. Answering No threw away every
        // second of it. Nothing needed the parsed records: the callsigns come from a scan of the file
        // that already runs before the import, so the same question with the same list can be asked at
        // once.
        //
        // Returns false when the operator says no; the caller must then leave the file alone.
        private bool ApproveDifferentStationCallsign(string filePath,
                                                     System.Collections.Generic.List<string> callsInFile)
        {
            string myCallsign = Properties.Settings.Default.my_callsign;
            if (string.IsNullOrWhiteSpace(myCallsign)) return true;
            if (callsInFile == null || callsInFile.Count == 0) return true;
            if (!callsInFile.Any(c => !CallsignIdentity.Same(c, myCallsign))) return true;

            return HolyMessageBox.ShowConfirm(
                "The ADIF file \"" + System.IO.Path.GetFileName(filePath) + "\" contains QSOs logged "
                + "under a different callsign than your current station callsign.\n\n"
                + "Callsign(s) in the file:  " + string.Join(", ", callsInFile) + "\n"
                + "Your current station callsign:  " + myCallsign.Trim() + "\n\n"
                + "Do you want to import these QSOs into your log anyway?",
                "Different callsign in ADIF file", HolyMsgType.Warning, this);
        }

        private static void ScanAdifIdentity(string filePath, out System.Collections.Generic.List<string> stationCallsigns, out System.Collections.Generic.List<string> operators)
        {
            var callCounts = new System.Collections.Generic.Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);
            var opCounts = new System.Collections.Generic.Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);
            try
            {
                var callRx = AdifFieldRegex("station_callsign");
                var opRx = AdifFieldRegex("operator");

                using (var reader = new System.IO.StreamReader(filePath, System.Text.Encoding.UTF8))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        CollectAdifFieldValues(callRx, line, callCounts);
                        CollectAdifFieldValues(opRx, line, opCounts);
                    }
                }
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }

            stationCallsigns = MostFrequentFirst(callCounts);
            operators = MostFrequentFirst(opCounts);
        }

        private static System.Text.RegularExpressions.Regex AdifFieldRegex(string field) =>
            new System.Text.RegularExpressions.Regex("<" + field + ":\\d+(?::[^>]*)?>([^<\\r\\n]*)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        private static void CollectAdifFieldValues(System.Text.RegularExpressions.Regex rx, string text,
                                                   System.Collections.Generic.Dictionary<string, int> counts)
        {
            foreach (System.Text.RegularExpressions.Match m in rx.Matches(text))
            {
                string v = m.Groups[1].Value.Trim();
                if (v.Length == 0) continue;
                counts.TryGetValue(v, out int c);
                counts[v] = c + 1;
            }
        }

        private static System.Collections.Generic.List<string> MostFrequentFirst(
            System.Collections.Generic.Dictionary<string, int> counts) =>
            counts.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).ToList();

        private enum ImportTarget { Cancel, NewLog, CurrentLog }

        // Asks whether an imported ADIF becomes its own NEW log or is added to the log open now.
        //
        // WITH NO LOG OPEN THE DIALOG STILL APPEARS, with the question it can still answer. There is
        // nothing to add the file to, so where the QSOs go is not in doubt - but "Import Duplicates"
        // is, and it decides whether a contact the file holds twice is stored once or twice. Skipping
        // the dialog altogether hid that from the one operator most likely to be affected by it: the
        // one importing a lifetime's logbook from another program on the day he installs this one.
        private ImportTarget AskImportTarget(bool newLogIsTheOnlyAnswer = false)
        {
            ImportTarget result = ImportTarget.Cancel;
            var dialog = new Window
            {
                Title = "Import ADIF",
                SizeToContent = SizeToContent.WidthAndHeight,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ShowInTaskbar = false
            };
            var root = new StackPanel { Margin = new Thickness(18, 14, 18, 16) };
            root.Children.Add(new TextBlock
            {
                Text = newLogIsTheOnlyAnswer ? "Import this file into a new log"
                                             : "Where should the imported QSOs go?",
                FontSize = 16, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 14)
            });

            string curName = null;
            try { curName = dal?.GetLogName(dal.ActiveLogId); } catch (System.Exception swallowed) { Log.Swallow(swallowed); }

            void AddOption(string label, string desc, Thickness margin)
            {
                var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 16, MaxWidth = 440, Margin = margin };
                tb.Inlines.Add(new System.Windows.Documents.Run(label) { FontWeight = FontWeights.Bold });
                tb.Inlines.Add(new System.Windows.Documents.Run(" — " + desc));
                root.Children.Add(tb);
            }
            if (newLogIsTheOnlyAnswer)
            {
                root.Children.Add(new TextBlock
                {
                    Text = "No log is open, so the file becomes a log of its own. You will be asked "
                         + "what to call it next.",
                    TextWrapping = TextWrapping.Wrap, FontSize = 16, MaxWidth = 440,
                    Margin = new Thickness(0, 0, 0, 18)
                });
            }
            else
            {
                AddOption("New log", "create a new log just for this file (recommended for a logbook from another program) — your existing logs are untouched.", new Thickness(0, 0, 0, 12));
                AddOption("Current log", "add the file's QSOs to the log open now" + (string.IsNullOrWhiteSpace(curName) ? "" : " (" + curName + ")") + ".", new Thickness(0, 0, 0, 18));
            }

            // THE ONE IMPORT SETTING THAT DECIDES HOW MANY QSOs SURVIVE, ASKED WHERE THE IMPORT IS.
            //
            // It lived in Options → Import Settings and nowhere else, so an operator who had never gone
            // looking through the options did not know it existed - and it silently drops records: with
            // it off, a file holding a contact twice puts one in the log and the second is gone. A
            // setting that throws away data has to be visible at the moment it is about to do it.
            //
            // It is the SAME setting, not a copy of it: the box opens showing what Options holds, and
            // whatever it is left at is saved back there, exactly as though it had been changed in the
            // options window. Two places to look at one switch, never two switches.
            var dupBox = new CheckBox
            {
                Content = "Import Duplicates",
                IsChecked = Properties.Settings.Default.IsParseDuplicates,
                FontSize = 16,
                Margin = new Thickness(0, 0, 0, 4)
            };
            root.Children.Add(dupBox);
            root.Children.Add(new TextBlock
            {
                Text = "Off, a contact the file holds more than once is stored only once. Two records are "
                     + "the same contact when the callsign, the date, the band, the mode and the minute "
                     + "are all the same — the same rule the Log Fixer uses. This is the same setting as "
                     + "Options → Import Settings, and what you leave it at is kept.",
                TextWrapping = TextWrapping.Wrap, FontSize = 16, MaxWidth = 440,
                Opacity = 0.75, Margin = new Thickness(24, 0, 0, 30)
            });

            var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            Button MakeButton(string text) => new Button { Content = text, MinWidth = 100, Margin = new Thickness(6, 0, 6, 0), Padding = new Thickness(12, 5, 12, 5), FontSize = 16 };
            var newBtn = MakeButton(newLogIsTheOnlyAnswer ? "Import" : "New log");
            var curBtn = MakeButton("Current log");
            if (newLogIsTheOnlyAnswer) curBtn.Visibility = Visibility.Collapsed;
            var cancelBtn = MakeButton("Cancel"); cancelBtn.IsCancel = true;
            // Saved when the import goes ahead, not when it is cancelled: a cancelled dialog is the
            // operator saying "not this", and it should leave his options as he found them.
            void KeepDuplicatesChoice()
            {
                try
                {
                    bool wanted = dupBox.IsChecked == true;
                    if (Properties.Settings.Default.IsParseDuplicates != wanted)
                    {
                        Properties.Settings.Default.IsParseDuplicates = wanted;
                        Properties.Settings.Default.Save();
                    }
                }
                catch (System.Exception swallowed) { Log.Swallow(swallowed); }
            }

            newBtn.Click += (s, e) => { KeepDuplicatesChoice(); result = ImportTarget.NewLog; dialog.Close(); };
            curBtn.Click += (s, e) => { KeepDuplicatesChoice(); result = ImportTarget.CurrentLog; dialog.Close(); };
            cancelBtn.Click += (s, e) => { result = ImportTarget.Cancel; dialog.Close(); };
            buttonRow.Children.Add(newBtn);
            buttonRow.Children.Add(curBtn);
            buttonRow.Children.Add(cancelBtn);
            root.Children.Add(buttonRow);

            dialog.Content = root;
            dialog.ShowDialog();
            return result;
        }

        private enum ImportLogChoice { Cancel, Merge, Replace }

        // Warns that a log already exists and asks the user to Merge (append) or Replace it. Built as
        // a small custom dialog because a standard MessageBox can't have "Merge"/"Replace" buttons.
        private ImportLogChoice AskImportMergeOrReplace(int existingCount)
        {
            ImportLogChoice result = ImportLogChoice.Cancel;

            var dialog = new Window
            {
                Title = "Import ADIF",
                SizeToContent = SizeToContent.WidthAndHeight,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ShowInTaskbar = false
            };

            var root = new StackPanel { Margin = new Thickness(18, 10, 18, 18) };

            var headerRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
            headerRow.Children.Add(new TextBlock
            {
                Text = "⚠",                       // warning sign
                FontSize = 26,
                Foreground = System.Windows.Media.Brushes.DarkOrange,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            });
            headerRow.Children.Add(new TextBlock
            {
                Text = "Your log already contains " + existingCount + " QSO" + (existingCount == 1 ? "" : "s") + ".",
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center
            });
            root.Children.Add(headerRow);

            // Each option is a bold label with a hanging-indented description: when the description
            // wraps, the continuation lines align under the first word of the description (i.e. under
            // "first" for Replace), not back at the left edge.
            DockPanel MakeOption(string label, string desc, Thickness margin)
            {
                var row = new DockPanel { MaxWidth = 430, Margin = margin };
                var lbl = new TextBlock { FontSize = 16, VerticalAlignment = VerticalAlignment.Top };
                lbl.Inlines.Add(new System.Windows.Documents.Run(label) { FontWeight = FontWeights.Bold });
                lbl.Inlines.Add(new System.Windows.Documents.Run(" — "));
                DockPanel.SetDock(lbl, Dock.Left);
                row.Children.Add(lbl);
                row.Children.Add(new TextBlock { Text = desc, TextWrapping = TextWrapping.Wrap, FontSize = 16 });
                return row;
            }

            root.Children.Add(MakeOption("Merge", "add the file's QSOs to your existing log.", new Thickness(0, 0, 0, 12)));
            root.Children.Add(MakeOption("Replace", "first save a backup of your current log to a file you choose, then import the file. The QSOs your log holds now are removed only once the file is safely in — if anything goes wrong, or you stop it, your log is left as it is.", new Thickness(0, 0, 0, 34)));

            var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            Button MakeButton(string text)
            {
                return new Button { Content = text, MinWidth = 90, Margin = new Thickness(6, 0, 6, 0), Padding = new Thickness(12, 5, 12, 5), FontSize = 16 };
            }
            var mergeBtn = MakeButton("Merge");
            var replaceBtn = MakeButton("Replace");
            var cancelBtn = MakeButton("Cancel");
            cancelBtn.IsCancel = true;
            mergeBtn.Click += (s, e) => { result = ImportLogChoice.Merge; dialog.Close(); };
            replaceBtn.Click += (s, e) => { result = ImportLogChoice.Replace; dialog.Close(); };
            cancelBtn.Click += (s, e) => { result = ImportLogChoice.Cancel; dialog.Close(); };
            buttonRow.Children.Add(mergeBtn);
            buttonRow.Children.Add(replaceBtn);
            buttonRow.Children.Add(cancelBtn);
            root.Children.Add(buttonRow);

            dialog.Content = root;
            dialog.ShowDialog();
            return result;
        }

        private void StartAdifImportWorker()
        {
            if (AdifHandlerWorker == null || AdifHandlerWorker.IsBusy)
                return;

            _importStopping = false;
            _importRunning = true;
            UploadProgress = "Starting import 0%";
            ToggleUploadProgress(Visibility.Visible);
            // AN IMPORT CAN BE STOPPED NOW. A 77 MB logbook is minutes of work and there was no way out
            // of it once it started - the only exit was killing the program mid-write.
            ShowStopButton(true);
            AdifHandlerWorker.RunWorkerAsync();
        }
        
        private void AdifHandlerWorker_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            // Whatever got past the per-file catch - the reload at the end of the import runs outside it,
            // and that is the very phase where a big log is most likely to run out of room. It gets the
            // same report rather than the one-line "Import failed." it used to get.
            _importRunning = false;   // nothing posted before this moment may show the spinner again

            if (e.Error != null)
            {
                ToggleUploadProgress(Visibility.Hidden);
                ShowStopButton(false);
                Log.Warn($"Import failed while {_importPhase}" + Environment.NewLine
                       + MemoryReport() + Environment.NewLine + e.Error);
                ShowImportFailure(ImportFailureText(e.Error, null, 0, null, -1));
                return;
            }

            var result = e.Result as AdifImportResult ?? new AdifImportResult();

            if (Qsos != null)
            {
                Qsos.CollectionChanged -= Qsos_CollectionChanged;
            }

            Qsos = result.RefreshedQsos ?? new ObservableCollection<QSO>();
            Qsos.CollectionChanged += Qsos_CollectionChanged;
            DataContext = Qsos;
            LastQSO = Qsos.FirstOrDefault();
            ApplyDefaultLogSort();

            // Replacing the whole collection does NOT raise CollectionChanged, so the cluster
            // colors aren't refreshed automatically. Rebuild the worked-countries cache (needed =
            // red) and re-evaluate the in-log status (worked before = blue) against the new log.
            RebuildWorkedCountriesAndRefreshCluster();
            if (clusterVisibleSpots != null)
                RefreshClusterVisibleSpots();

            ToggleUploadProgress(Visibility.Hidden);
            ShowStopButton(false);
            _importStopping = false;
            UpdateNumOfQSOs();
            UpdateLotwMenuCount();
            UpdateQrzMenuCount();

            // ONE report, whatever happened. It used to be either/or: a single record the database
            // refused replaced the whole summary with "N QSO(s) failed to import. Check the file format
            // and try again." - so an operator who imported 28,000 QSOs and lost 3 was told nothing
            // about the 28,000, and nothing about WHICH 3 either. Both halves are always said now, and
            // the ones that did not make it are named in a file on the Desktop.
            // A REPLACE THAT DID NOT REPLACE. Two ways to get here, and they are not the same thing, so
            // they do not share a sentence: the file brought nothing worth swapping the log for, or the
            // swap itself failed and the log now holds BOTH sets. The first is the safety net doing its
            // job; the second is a mess the operator has to be told about at once.
            if (!result.Stopped && result.ReplaceLeftAlone)
            {
                if (result.ImportedQsoCount > 0)
                    HolyMessageBox.ShowError(
                        "The replace could not be finished.\n\n"
                        + $"The {result.ImportedQsoCount:N0} QSO(s) from the file went in, but the QSOs "
                        + "your log already held could NOT be removed - so the log now holds BOTH.\n\n"
                        + "Use Tools \u2192 Log Workshop to sort it out, or delete this log and import "
                        + "your backup ADIF and then the new file.",
                        "Replace not finished", this, 620);
                else
                    HolyMessageBox.ShowWarning(
                        "Nothing in the file could be stored, so your log was NOT replaced.\n\n"
                        + "It still holds every QSO it had. The report says what was wrong with the file.",
                        "Log not replaced", this, 620);
            }

            if (result.Stopped)
            {
                ShowStoppedImport(result);
            }
            else if (result.ReplaceLeftAlone)
            {
                // Already spoken about, just above.
            }
            else if (result.ImportedQsoCount > 0 || result.CompletedQsoCount > 0 || result.RejectedCount > 0)
            {
                // The count of THIS log, asked of the database directly. It used to be taken from the
                // refreshed collection, which was every QSO in every log (the old GetAllQSOs had no
                // log filter; it is gone now), so a line headed "Total QSOs in log" reported the whole
                // database: importing 28,366 QSOs into a log that then held exactly 28,366 announced
                // 67,622.
                int totalQsos;
                try { totalQsos = dal.GetQsoCountForLog(dal.ActiveLogId); }
                catch (Exception swallowed)
                {
                    // Fall back to the collection just loaded for THIS log - never to a database-wide
                    // total, which would be a meaningless number under a label that says "in log".
                    Log.Swallow(swallowed);
                    totalQsos = result.RefreshedQsos != null ? result.RefreshedQsos.Count : 0;
                }

                // WHAT HAPPENED, IN NUMBERS FIRST.
                //
                // This message used to be six paragraphs of prose explaining every number as it arrived,
                // and an operator finishing a long import had to read an essay to learn that it went
                // well. Numbers now come first, each on its own short line, and the explaining is done
                // once at the end in two sentences - the rest is in the report, which is what a report
                // is for. Anything with nothing to say prints no line at all.
                bool anyRejected = result.RejectedCount > 0;

                string msg = anyRejected ? "Import finished.\n\n" : "Import completed successfully!\n\n";

                msg += $"Added to this log:  {result.ImportedQsoCount:N0}\n";
                if (result.CompletedQsoCount > 0)
                    msg += $"Already here, filled in:  {result.CompletedQsoCount:N0}\n";
                if (result.AmbiguousQsoCount > 0)
                    msg += $"Already here, too alike to match:  {result.AmbiguousQsoCount:N0}\n";
                if (anyRejected)
                    msg += $"NOT stored:  {result.RejectedCount:N0}\n";
                msg += $"Total now in this log:  {totalQsos:N0}\n";

                // The check ran and found nothing is a different statement from the check never ran, and
                // only one line separates them.
                if (result.RecordsRead > 0)
                    msg += anyRejected
                        ? $"\nAll {result.RecordsRead:N0} records in the file were checked."
                        : $"\nAll {result.RecordsRead:N0} records were checked — none was turned away.";

                // ONLY WHAT THE IMPORT DID. The country lines that used to stand here - a different
                // country, a different spelling, a contact counting towards none - were this window
                // judging the QSOs, which is the Log Fixer's job and is now done only by the Log Fixer.
                if (result.FilledCount > 0)
                    msg += $"\n\nWorth a look:\n\n• {result.FilledCount:N0} "
                         + $"record{(result.FilledCount == 1 ? "" : "s")} "
                         + "were missing a field that the rest of the record answered, so it was worked "
                         + "out for you. The report names every one.";

                // WHERE TO GO NEXT, named. "Act on them in the Log Workshop" told an operator that
                // something could be done without saying what to press, and the two windows have
                // different jobs: the Log Verifier finds, the Log Fixer puts right. Both names appear
                // here exactly as they appear on screen, so there is nothing to hunt for.
                // WHAT HAPPENS NEXT, now that it happens by itself. This used to be an instruction -
                // "select Tools → Log Workshop and press Log Verifier" - which asked the operator to go
                // and start by hand the very check the numbers above had just told him he needed. The
                // Log Fixer opens on its own when this message is closed, so the paragraph says what is
                // about to appear instead of what to press.
                // The Fixer opens whenever contacts actually arrived, so this paragraph is tied to the
                // same condition rather than to findings this window no longer works out.
                if (result.ImportedQsoCount > 0 || result.CompletedQsoCount > 0)
                    msg += "\n\nYour file was stored exactly as it is — nothing was changed.\n\n"
                         + "**The Log Fixer will open next** and check the whole log: countries, "
                         + "continents, locators and duplicates. Tick the kinds of problem you want put "
                         + "right and it corrects them for you. Nothing is written until you press Fix, "
                         + "and your database is copied first.";

                if (anyRejected)
                {
                    msg += $"\n\nThe {result.RejectedCount:N0} not stored are missing something a QSO cannot be "
                         + "stored without — a callsign, a date, a time, a band, a mode or the station callsign. "
                         + "They are saved beside the report as an ADIF you can correct and import again; nothing "
                         + "already in your log will be duplicated.";

                    if (!string.IsNullOrEmpty(result.ReportPath))
                    {
                        HolyMessageBox.ShowWithLinks(msg, "Import Complete", HolyMsgType.Warning, this,
                            FileLinks(result), OpenPath, 620, ReportsFooter);
                    }
                    else
                    {
                        HolyMessageBox.ShowWarning(msg, "Import Complete", this, 620);
                    }
                }
                else if (!string.IsNullOrEmpty(result.ReportPath))
                {
                    // Nothing rejected, but there IS a report - fields were filled in. Still success,
                    // and still reachable in one click.
                    HolyMessageBox.ShowWithLinks(msg, "Import Complete", HolyMsgType.Success, this,
                        FileLinks(result), OpenPath, 620, ReportsFooter);
                }
                else
                {
                    HolyMessageBox.ShowSuccess(msg, "Import Complete", this, 620);
                }
            }
            // Give the imported log the identity the user confirmed before the import (if it had none).
            if (!string.IsNullOrWhiteSpace(_pendingImportCallsign))
            {
                try { dal.SetLogIdentity(dal.ActiveLogId, _pendingImportCallsign, _pendingImportOperator); }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
                _pendingImportCallsign = null;
                _pendingImportOperator = null;
                RefreshCopyIndicator();
            }

            // The log's contents were swapped wholesale, so the "recent QSOs" count means nothing any
            // more - it was reset by the old clearing step, and the reset belongs with the swap.
            if (result.ReplacedQsoCount > 0)
            {
                Properties.Settings.Default.RecentQSOCounter = 0;
                try { Properties.Settings.Default.Save(); }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
                UpdateEqslQueueIndicator();
            }

            TB_Comment.Text = "";
            UpdateNumOfQSOs();

            OpenFixerAfterImport(result);
        }

        // WHAT AN OPERATOR SEES WHEN HE STOPS AN IMPORT HIMSELF.
        //
        // Not the ordinary completion message with smaller numbers in it: the numbers count a PART of
        // the file, and the two questions he has are what is in the log now and how to get rid of it.
        // Both are answered here - the second with a button rather than instructions, because the log
        // this import created is a log HE has no use for and would otherwise have to go and find in
        // Tools -> Logs and delete by hand.
        private void ShowStoppedImport(AdifImportResult result)
        {
            // A NEW LOG DOES NOT SURVIVE A STOPPED IMPORT. It was created for one purpose - to hold this
            // file - and the file did not go in, so what is left is a fragment cut off wherever the
            // operator's finger happened to land. The log did not exist before the import, so taking it
            // away IS putting things back.
            //
            // MERGE AND REPLACE ARE NOT UNDONE HERE: their rollback runs on the worker, before the log
            // is reloaded, so that the grid and the report both show the log as it ends up.
            bool hadANewLog = result.NewLogId > 0;
            bool newLogDeleted = false;
            string newLogName = result.NewLogName;

            if (hadANewLog)
            {
                try
                {
                    long id = result.NewLogId;
                    // Closed BEFORE the row goes, so the active log id never names a log that is not
                    // there - the same order the Log Manager uses, and for the same reason.
                    if (id == dal.ActiveLogId) CloseActiveLog();
                    dal.DeleteLog(id);
                    RefreshCopyIndicator();

                    // The identity confirmed for that log has nowhere to go now.
                    _pendingImportCallsign = null;
                    _pendingImportOperator = null;

                    UpdateNumOfQSOs();
                    newLogDeleted = true;
                }
                catch (Exception ex)
                {
                    Log.Warn("Could not delete the log a stopped import created: " + ex);
                }
            }

            // NOTHING IS SAID WHEN IT WENT AS PROMISED.
            //
            // The operator was told, in the dialog he had to answer Yes in, that everything would be
            // undone and the log put back as it was. It was. A message afterwards saying the same thing
            // again is one more box to close after the box he closed to get here, and it tells him
            // nothing he was not told a moment ago. The report is written either way and is in
            // File -> Open Reports Folder for anyone who wants the numbers.
            //
            // A FAILURE IS ALWAYS SAID. This is the only case where what he was promised did not happen,
            // and silence would leave him believing his log is clean when it is not.
            bool somethingIsWrong = result.UndoFailed || (hadANewLog && !newLogDeleted);
            if (!somethingIsWrong) return;

            string msg = "Import stopped.\n\n";
            if (result.UndoFailed)
            {
                msg += "WARNING: the import could NOT be undone, so part of the file is still in your log.\n\n"
                     + $"QSOs stored before you stopped:  {result.ImportedQsoCount:N0}\n\n"
                     + "The report lists what went in. Check your log before you import this file again.";
            }
            else
            {
                msg += "WARNING: the log this import created"
                     + (string.IsNullOrWhiteSpace(newLogName) ? "" : " (\"" + newLogName + "\")")
                     + " could NOT be removed. It is still there and holds part of the file.\n\n"
                     + "Delete it yourself in Tools \u2192 Logs.";
            }

            var links = FileLinks(result);
            if (links.Count > 0)
                HolyMessageBox.ShowWithLinks(msg, "Import stopped", HolyMsgType.Warning, this,
                                             links, OpenPath, 620, ReportsFooter);
            else
                HolyMessageBox.ShowWarning(msg, "Import stopped", this, 620);
        }

        // THE CHECK RUNS ITSELF WHEN AN IMPORT BRINGS QSOs IN.
        //
        // An imported log is exactly when a log is most likely to be wrong - the contacts came from
        // somebody else's program, with their spelling of countries and their idea of what a locator is
        // - and it is the one moment the operator is sitting there ready to deal with it. The import's
        // own report covers the FILE it read; this covers the LOG, which is a different question, and
        // it leaves a fixer report in the Reports folder beside the import one.
        //
        // AFTER the completion message, not before: that message says what the import did, and a window
        // opening on top of it would bury the numbers. Only when contacts actually arrived - a run that
        // added nothing has nothing new to check.
        private void OpenFixerAfterImport(AdifImportResult result)
        {
            try
            {
                if (result == null) return;
                // NOT AFTER A STOPPED IMPORT. What is in the log is a piece of a file, chosen by where
                // the operator happened to press Stop; checking it for country and locator problems
                // invites him to correct half a logbook he may be about to import properly.
                if (result.Stopped) return;
                if (result.ImportedQsoCount <= 0 && result.CompletedQsoCount <= 0) return;
                if (Qsos == null || Qsos.Count == 0) return;

                // Not owned by the main window, for the same reason the Log Workshop is not: an owned
                // window is pinned above its owner for ever, and the operator will want to look at the
                // log he has just imported while this is open.
                var verifier = new LogVerifierWindow(Qsos, SafeActiveLogName());
                verifier.Show();
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // ── What the import was doing when it went wrong ──────────────────────────────────────────
        //
        // An import that fails with "Error: Exception of type 'System.OutOfMemoryException' was thrown"
        // tells the operator nothing they can act on and tells whoever has to fix it even less: not
        // which file, not how big, not how far it got, not what else was in the way. Reconstructing one
        // real report from a photograph of that dialog took an afternoon. So the failure now carries
        // its own context - the phase it died in, the file, the log it was importing into, and the
        // memory figures - on screen and in holylogger.log.

        // The phase the ADIF worker is in, in words fit to be shown to an operator. Read by the
        // failure report and by RunWorkerCompleted, which catches whatever escapes the per-file try.
        private volatile string _importPhase = "getting ready";

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        private static string Mb(double bytes)
        {
            if (bytes >= 1024d * 1024d * 1024d) return (bytes / (1024d * 1024d * 1024d)).ToString("0.00") + " GB";
            if (bytes >= 1024d * 1024d) return (bytes / (1024d * 1024d)).ToString("0.0") + " MB";
            return (bytes / 1024d).ToString("0") + " KB";
        }

        // The numbers that actually explain an out-of-memory in this program. ADDRESS SPACE is the one
        // that matters and the one nobody thinks to look at: a 32-bit program is given a limited range
        // of memory addresses no matter how much RAM the machine has, and it is that range, not the
        // RAM, that runs out first. Printing both side by side stops the next report being "but my PC
        // has 16 GB free".
        private static string MemoryReport()
        {
            var sb = new StringBuilder();
            try
            {
                var st = new MEMORYSTATUSEX();
                st.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
                if (GlobalMemoryStatusEx(ref st))
                {
                    double usedVirtual = st.ullTotalVirtual - st.ullAvailVirtual;
                    sb.AppendLine($"    Address space this program may use:  {Mb(st.ullTotalVirtual)}"
                                + $"   ({Mb(usedVirtual)} of it in use, {Mb(st.ullAvailVirtual)} free)");
                    sb.AppendLine($"    RAM in this PC:                      {Mb(st.ullTotalPhys)}"
                                + $"   ({Mb(st.ullAvailPhys)} free)");
                }
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }

            try
            {
                using (var me = Process.GetCurrentProcess())
                    sb.AppendLine($"    HolyLogger is using:                 {Mb(me.PrivateMemorySize64)}"
                                + $"   ({Mb(GC.GetTotalMemory(false))} of that is log data)");
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }

            sb.Append($"    Program build:                       {(Environment.Is64BitProcess ? "64-bit" : "32-bit")}"
                    + $" on {(Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit")} Windows");
            return sb.ToString();
        }

        // The whole story of a failed import, in the order a reader needs it: what failed, where it
        // was, what it was working on, why, and what to do about it.
        private string ImportFailureText(Exception ex, string filename, long fileBytes, string logName, int qsosAlreadyInLog)
        {
            var sb = new StringBuilder();
            sb.AppendLine("The import could not be finished.");
            sb.AppendLine();

            sb.AppendLine("WHAT IT WAS DOING");
            if (!string.IsNullOrEmpty(filename))
            {
                sb.AppendLine($"    File:      {System.IO.Path.GetFileName(filename)}"
                            + (fileBytes > 0 ? $"   ({Mb(fileBytes)})" : ""));
                sb.AppendLine($"    Folder:    {System.IO.Path.GetDirectoryName(filename)}");
            }
            sb.AppendLine($"    Stopped:   while {_importPhase}");
            if (!string.IsNullOrEmpty(logName))
                sb.AppendLine($"    Into log:  {logName}"
                            + (qsosAlreadyInLog >= 0 ? $"   ({qsosAlreadyInLog:N0} QSOs already in it)" : ""));
            sb.AppendLine();

            if (ex is OutOfMemoryException)
            {
                sb.AppendLine("WHY IT STOPPED");
                sb.AppendLine("    HolyLogger ran out of room to hold the import.");
                sb.AppendLine();
                sb.AppendLine("    This is almost never about how much RAM the PC has. HolyLogger is a");
                sb.AppendLine("    32-bit program, so Windows allows it only a limited RANGE OF MEMORY");
                sb.AppendLine("    ADDRESSES, and that range is what filled up. An import needs the whole");
                sb.AppendLine("    file held at once, every record read out of it, and every QSO already in");
                sb.AppendLine("    the log it is importing into - all at the same moment.");
                sb.AppendLine();
                sb.AppendLine(MemoryReport());
                sb.AppendLine();
                sb.AppendLine("WHAT TO TRY, best first");
                sb.AppendLine("    1. Close the Cluster, Statistics and Map windows, restart HolyLogger,");
                sb.AppendLine("       and import before doing anything else. A freshly started program has");
                sb.AppendLine("       its memory in one unbroken piece, which is what a big file needs.");
                sb.AppendLine("    2. Split the ADIF into two or three smaller files and import them one");
                sb.AppendLine("       after the other. Nothing is lost by importing in parts - a record");
                sb.AppendLine("       already in the log fills that QSO in rather than being added twice.");
                sb.AppendLine("    3. Import into a new, empty log. Matching against a log that already");
                sb.AppendLine("       holds tens of thousands of QSOs is the most expensive part of all.");
            }
            else
            {
                sb.AppendLine("WHY IT STOPPED");
                sb.AppendLine("    " + ex.Message);
                if (ex.InnerException != null)
                    sb.AppendLine("    " + ex.InnerException.Message);
            }

            sb.AppendLine();
            sb.AppendLine("FOR WHOEVER FIXES IT");
            sb.AppendLine($"    {ex.GetType().FullName}   (HolyLogger {AppVersionText()})");
            sb.AppendLine("    The full details, with the stack trace, are in the log file below. Sending");
            sb.AppendLine("    that file is the fastest way to get this looked at.");

            return sb.ToString();
        }

        private static string AppVersionText()
        {
            try
            {
                return System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString();
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); return "version unknown"; }
        }

        // Shown with the log file as a link at the bottom, so "the details are in the log" is one
        // click rather than a hunt through AppData.
        private void ShowImportFailure(string text)
        {
            var links = new List<KeyValuePair<string, string>>();
            string logPath = Log.FilePath;
            if (!string.IsNullOrEmpty(logPath))
                links.Add(new KeyValuePair<string, string>("Log file", logPath));

            if (links.Count > 0)
                HolyMessageBox.ShowWithLinks(text, "Import Error", HolyMsgType.Error, this, links, OpenPath, 760);
            else
                HolyMessageBox.ShowError(text, "Import Error", this, 760);
        }

        private void AdifHandlerWorker_ProgressChanged(object sender, ProgressChangedEventArgs e)
        {
            // A PROGRESS REPORT THAT ARRIVES AFTER THE END IS THROWN AWAY.
            //
            // ReportProgress does not deliver the message, it POSTS it: the worker can finish, the
            // completion handler can hide the spinner, and a report posted a moment before the end then
            // arrives and shows it again - with nothing left to hide it, so it turned for ever. That is
            // what was seen after a stop: the import really had stopped, and the spinner was left
            // spinning over a program that had finished.
            if (!_importRunning) return;

            // NOTHING MOVES WHILE THE STOP QUESTION IS ON THE SCREEN. The import holds still at its next
            // checkpoint until the question is answered, so there is nothing new to report anyway - and
            // any report already posted before it stopped is dropped here rather than climbing behind
            // the very dialog asking whether to stop. Answer No and it picks up where it left off.
            if (_stopConfirmOpen) return;

            ToggleUploadProgress(Visibility.Visible);

            // ONCE HE HAS SAID STOP, THE PERCENTAGES ARE NOT SHOWN ANY MORE. The worker reports its
            // progress for a few seconds yet - it has a batch to finish, a log to put back and a grid to
            // reload - and those numbers climbing after Stop read as "it ignored me". What it is
            // actually doing is said instead, and it is the last thing this label says.
            if (_importStopping)
            {
                UploadProgress = "Stopping — putting your log back…";
                return;
            }

            UploadProgress = e.UserState as string ?? (e.ProgressPercentage.ToString() + "%");
        }

        // HAS THE OPERATOR STOPPED IT? Asked at every checkpoint: before each file, before the import
        // puts a question on the screen, before each batch is saved, and once more before anything is
        // made permanent.
        //
        // IT ASKS AND RETURNS. It used to WAIT here while the "Stop the import?" question was on the
        // screen, so that the import would pause while the operator decided. That waiting froze the
        // whole program: a worker thread that blocks on a flag the screen thread owns is one half of a
        // deadlock, and the program stopped answering the mouse with the spinner still turning - drawn
        // by Windows, over a program that was no longer running. The pause was worth nothing anyway:
        // whatever the import does in those few seconds is undone with the rest of it.
        //
        // Called on the worker thread only.
        private bool StopWasAskedFor()
        {
            // THE IMPORT HOLDS STILL WHILE THE QUESTION IS ON THE SCREEN.
            //
            // Without this it went on working behind the dialog and could FINISH before the operator
            // answered - and a finished import has nothing left to stop, so it announced its success
            // and opened the Log Fixer over the question asking whether to stop it. That is what a man
            // who pressed Stop and then read something for a minute actually got.
            //
            // BOUNDED, because this is a worker thread waiting on the screen thread and that is the
            // shape a hang has. Five minutes is far longer than anyone stares at a Yes/No box, and if
            // it is ever reached the import simply carries on rather than standing there for ever.
            var waited = System.Diagnostics.Stopwatch.StartNew();
            while (_stopConfirmOpen && waited.Elapsed.TotalMinutes < 5)
                System.Threading.Thread.Sleep(50);

            if (_stopConfirmOpen)
                Log.Warn("STOP: the question has been on screen for five minutes; the import is carrying on.");

            return AdifHandlerWorker != null && AdifHandlerWorker.CancellationPending;
        }

        private void AdifHandlerWorker_DoWork(object sender, DoWorkEventArgs e)
        {
            // Capture UI-dependent values on the calling thread before going to background work
            string overrideOperator = this.Dispatcher.Invoke(() => TB_Operator.Text);
            bool isOverride = Properties.Settings.Default.IsOverrideOperatorFromFile;
            bool isParseDuplicates = Properties.Settings.Default.IsParseDuplicates;
            bool isParseWARC = Properties.Settings.Default.IsParseWARC;
            string myCallsign = Properties.Settings.Default.my_callsign;
            List<string> files = this.Dispatcher.Invoke(() => ImportFileQ.ToList());

            int faultyQSO = 0;
            int importedQsoCount = 0;
            int completedQso = 0;    // records that completed a QSO the log already had
            int ambiguousQso = 0;    // matched, but two candidates were equally close - left alone
            var rejects = new List<ImportReject>();                    // what did not make it, and why
            var filledIn = new List<HolyLogParser.FilledField>();      // what was worked out from the record
            // WHAT THE MERGE DID TO QSOs THE LOG ALREADY HELD. Filling empty fields on stored contacts is
            // the one thing an import does that leaves no trace of its own - afterwards the QSO looks as
            // though it always held the value - so it is written down as it happens.
            var mergeFilled = new List<DataAccess.MergeNote>();
            var mergeAmbiguous = new List<DataAccess.MergeNote>();
            // Records the "Import Duplicates" option threw away before the log was ever consulted -
            // empty unless that option is off. Gathered so the report can account for them.
            var droppedDuplicates = new List<QSO>();
            // How many were dropped ALL TOLD. The list above stops collecting after 10,000 - they are
            // whole QSOs and this program has 3 GB to live in - so its Count is not the answer.
            int droppedCount = 0;

            // No list of country disagreements and no list of contacts that count towards no country:
            // both were this code judging a QSO, and the Log Fixer is the one authority on that. It opens
            // by itself when the import finishes. What is gathered here is only what the import alone
            // knows - what could not be stored, and what had to be worked out.
            // THE OPERATOR PRESSED STOP. Two facts, not one: that it was stopped, and whether it was
            // stopped early enough that the log was never touched.
            bool stopped = false;
            bool stoppedWhileReading = false;

            int recordsRead = 0;                                       // what the file(s) held, all told
            const int importBatchSize = 500;
            int lastReportedPercent = 0;
            const int readPhasePercent = 3;
            const int parsePhaseEndPercent = 78;
            const int savePhaseStartPercent = 79;
            const int savePhaseEndPercent = 95;
            const int refreshPhaseStartPercent = 96;
            const int refreshPhaseEndPercent = 100;

            // Named once, for the failure report: which log the operator is importing INTO and how much
            // it already holds is half the explanation when an import runs out of room.
            string logName = null;
            int qsosAlreadyInLog = -1;
            // WHERE THE LOG STOOD BEFORE THIS IMPORT TOUCHED IT, so that pressing Stop can put it back.
            // Taken before the first record is read, and only good for the log open now.
            DataAccess.ImportUndo undo = null;
            try
            {
                lock (_syncLock)
                {
                    logName = dal.GetLogName(dal.ActiveLogId);
                    qsosAlreadyInLog = dal.GetQsoCountForLog(dal.ActiveLogId);
                }
                undo = new DataAccess.ImportUndo
                {
                    LogId = dal.ActiveLogId,
                    HighWaterQsoId = dal.MaxQsoId(dal.ActiveLogId)
                };
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }

            foreach (var filename in files)
            {
                // Several files can be dropped at once. Stop means stop: the ones not started are not
                // started, and a stop being decided on right now is waited for rather than raced.
                if (StopWasAskedFor()) { stopped = true; break; }

                long fileBytes = 0;
                try
                {
                    _importPhase = "getting ready";
                    lastReportedPercent = 1;
                    AdifHandlerWorker.ReportProgress(lastReportedPercent, "Preparing import 1%");

                    if (!File.Exists(filename))
                    {
                        this.Dispatcher.Invoke(() =>
                            HolyMessageBox.ShowError($"File not found:\n{filename}", "Import Error", this));
                        continue;
                    }

                    try { fileBytes = new FileInfo(filename).Length; }
                    catch (Exception swallowed) { Log.Swallow(swallowed); }

                    _importPhase = "reading the file from the disk";
                    lastReportedPercent = readPhasePercent;
                    AdifHandlerWorker.ReportProgress(lastReportedPercent, "Reading file 3%");
                    string RawAdif = File.ReadAllText(filename, Encoding.UTF8);

                    if (string.IsNullOrWhiteSpace(RawAdif))
                    {
                        this.Dispatcher.Invoke(() =>
                            HolyMessageBox.ShowWarning($"File is empty:\n{filename}", "Import Error", this));
                        continue;
                    }

                    var parser = new HolyLogParser(RawAdif,
                        HolyLogParser.IsIsraeliStation(myCallsign) ? HolyLogParser.Operator.Israeli : HolyLogParser.Operator.Foreign,
                        isParseDuplicates, isParseWARC);

                    // Stop while READING costs the log nothing: not one record has been written yet.
                    // Waits while the "Stop the import?" question is on screen, so the file stops being
                    // read the moment the operator presses Stop rather than carrying on behind the
                    // question he is answering. If he says No it goes straight back to reading.
                    parser.StopRequested = StopWasAskedFor;

                    // Whose log this is, for records that name no station callsign at all. The identity
                    // just confirmed for a brand-new log comes first (it is not written to the log until
                    // the import finishes), then the log's stored identity, then the station callsign
                    // the program is set to.
                    parser.DefaultStationCall = FallbackStationCall(myCallsign);

                    _importPhase = "reading the records out of the file";
                    parser.Parse(parseProgress =>
                    {
                        // The phase carries HOW FAR, because "it died reading the file" and "it died
                        // reading the file at 96%" point at different things.
                        _importPhase = $"reading the records out of the file ({parseProgress}% of it read)";
                        int percent = readPhasePercent + (int)Math.Floor((parseProgress * (parsePhaseEndPercent - readPhasePercent)) / 100.0);
                        if (percent > lastReportedPercent)
                        {
                            lastReportedPercent = percent;
                            AdifHandlerWorker.ReportProgress(percent, $"Parsing ADIF {parseProgress}%");
                        }
                    });
                    // STOPPED WHILE READING: the file is abandoned whole. Keeping the part that had
                    // been read would put a piece of a logbook in the log - chosen by how fast the
                    // disk was, which is no way to decide what an operator's log contains. Nothing was
                    // written, so there is nothing to undo either.
                    if (parser.Stopped)
                    {
                        stopped = true;
                        stoppedWhileReading = true;
                        recordsRead += parser.RecordsRead;
                        break;
                    }

                    List<QSO> rawQSOList = parser.GetRawQSO();

                    // Everything the file held that could not become a QSO, kept with its own text so
                    // the report can name it and hand it back.
                    string shortName = System.IO.Path.GetFileName(filename);
                    foreach (var r in parser.GetRejected())
                        rejects.Add(new ImportReject
                        {
                            FileName = shortName, Number = r.Number, Reason = r.Reason, Raw = r.Raw,
                            Call = r.Call, Date = r.Date, Time = r.Time, Band = r.Band, Mode = r.Mode,
                        });
                    filledIn.AddRange(parser.GetFilled());
                    droppedDuplicates.AddRange(parser.GetDroppedDuplicates());
                    droppedCount += parser.DroppedDuplicateCount;
                    recordsRead += parser.RecordsRead;

                    RawAdif = null;   // large file string no longer needed; free it before the save phase
                    int count = rawQSOList.Count;

                    if (count == 0)
                    {
                        // "Nothing here" and "everything here was turned away" are two different
                        // things, and the operator of a file whose every record is missing a field
                        // must not be told their file is empty. The report written at the end says
                        // which records and why; this only points at it.
                        int turnedAway = parser.GetRejected().Count;
                        string why = turnedAway > 0
                            ? $"All {turnedAway:N0} of its records were turned away - the report on your Desktop says which and why."
                            : "The file may be in an unsupported format or empty.";
                        if (StopWasAskedFor()) { stopped = true; break; }
                        this.Dispatcher.Invoke(() =>
                            HolyMessageBox.ShowWarning($"No QSOs were taken from:\n{filename}\n\n{why}", "Import Warning", this));
                        continue;
                    }

                    // NO CALLSIGN QUESTION HERE ANY MORE. It was asked at this point - after the whole
                    // file had been read - and is now asked before the reading starts, where a No costs
                    // nothing. See ApproveDifferentStationCallsign.

                    foreach (var rq in rawQSOList)
                    {
                        if (isOverride)
                        {
                            rq.Operator = overrideOperator;
                        }
                    }

                    // IMPORTING A FILE THE LOG ALREADY HOLDS. Every record is first matched against the
                    // QSOs already in this log: the ones that are already here COMPLETE those QSOs (filling
                    // only empty fields), and only the genuinely new ones go on to be inserted.
                    //
                    // That is what makes "just import your file again" a safe instruction. Older versions
                    // kept about a third of an ADIF and dropped the rest, so the only copy of an operator's
                    // award credits, counties and QSL routes is the file they imported from - and asking
                    // them to import it again used to double their log. It also means someone who has been
                    // logging here since their first import keeps those newer QSOs: they are not in the
                    // file, so nothing here touches them.
                    // NOT WHEN REPLACING. Replace means the file becomes the log; the QSOs still sitting
                    // in it are the ones about to be thrown away, and matching against them would fill
                    // in QSOs that are on their way out and drop records that belong in the new log.
                    // They are only still there because the deleting now happens at the end.
                    int completedThisFile = 0, ambiguousThisFile = 0;
                    if (_importChoice != ImportChoice.Replace)
                    {
                        _importPhase = $"checking which of the file's {count:N0} QSOs this log already has";
                        AdifHandlerWorker.ReportProgress(lastReportedPercent, "Checking for QSOs already in this log");
                        lock (_syncLock)
                        {
                            rawQSOList = dal.CompleteExistingQsos(rawQSOList, dal.ActiveLogId,
                                                                  out completedThisFile, out ambiguousThisFile,
                                                                  null, mergeFilled, mergeAmbiguous, undo);
                        }
                    }
                    completedQso += completedThisFile;
                    ambiguousQso += ambiguousThisFile;
                    count = rawQSOList.Count;   // what is left is what actually gets inserted

                    for (int i = 0; i < count; i += importBatchSize)
                    {
                        // BETWEEN BATCHES, never inside one. A batch is one transaction; stopping in
                        // the middle of it would leave the question of what a half-written batch is,
                        // and 500 QSOs is a fraction of a second. What went in before the press stays
                        // in - it is stored, and the report says how much of the file made it.
                        if (StopWasAskedFor()) { stopped = true; break; }

                        _importPhase = $"saving to the log (QSO {i + 1:N0} of {count:N0})";
                        List<QSO> batch = rawQSOList.Skip(i).Take(importBatchSize).ToList();
                        int batchFaulty;
                        int batchStartIndex = i;
                        var refused = new List<KeyValuePair<QSO, string>>();
                        lock (_syncLock)
                        {
                            batchFaulty = dal.InsertBatch(batch, processedInBatch =>
                            {
                                int processedOverall = batchStartIndex + processedInBatch;
                                int savePercent = (int)Math.Ceiling((float)processedOverall * 100 / count);
                                int percent = savePhaseStartPercent + (int)Math.Floor((savePercent * (savePhaseEndPercent - savePhaseStartPercent)) / 100.0);
                                if (percent > lastReportedPercent)
                                {
                                    lastReportedPercent = percent;
                                    AdifHandlerWorker.ReportProgress(percent, $"Saving to log {savePercent}%");
                                }
                            }, refused);
                        }

                        // A QSO the database itself refused. It parsed fine, so its record is rebuilt
                        // from what was read rather than kept from the file - the operator still gets
                        // a correctable ADIF record and the reason the database gave.
                        foreach (var f in refused)
                            rejects.Add(new ImportReject
                            {
                                FileName = shortName,
                                Number   = 0,
                                Reason   = "the log would not store it: " + f.Value,
                                Raw      = SafeRecordText(f.Key),
                                Call     = f.Key?.DXCall, Date = f.Key?.Date, Time = f.Key?.Time,
                                Band     = f.Key?.Band,   Mode = f.Key?.Mode,
                            });

                        faultyQSO += batchFaulty;
                        importedQsoCount += batch.Count - batchFaulty;
                    }
                }
                catch (Exception ex)
                {
                    // Written to the log FIRST: the operator may close the dialog without reading it,
                    // and the stack trace is the half they cannot read out over the air anyway.
                    Log.Warn($"Import failed while {_importPhase} - file={filename} "
                           + $"({fileBytes:N0} bytes), log=\"{logName}\" ({qsosAlreadyInLog:N0} QSOs)"
                           + Environment.NewLine + MemoryReport()
                           + Environment.NewLine + ex);

                    string errorMsg = ImportFailureText(ex, filename, fileBytes, logName, qsosAlreadyInLog);
                    this.Dispatcher.Invoke(() => ShowImportFailure(errorMsg));
                }

                // Several files can be queued by dropping them on the grid. Stop means stop - the ones
                // not yet started are not started.
                if (stopped) break;
            }

            // ONE LAST LOOK BEFORE ANY OF IT IS MADE PERMANENT.
            //
            // The checks inside the loop are per record read and per batch saved, so a Stop pressed in
            // the last of those - or during the merge check, which runs as one uninterruptible
            // statement - was found by nothing and the import finished as though the button had never
            // been touched. It was: 28,426 QSOs went in and the operator was shown "Import completed
            // successfully" for an import he had stopped. Asked once more here, where the answer still
            // means something, because everything below undoes, replaces or reports.
            if (!stopped && StopWasAskedFor()) stopped = true;

            // ── PUTTING THE LOG BACK, when the operator said stop and meant it ──────────────────────
            //
            // A new log is not undone here: it is DELETED afterwards, whole, which undoes it better than
            // any row-by-row work could. This is for an import that went into a log that already held
            // QSOs, where the rows it added and the empty fields it filled are the only difference
            // between the log now and the log an hour ago.
            //
            // Before the reload and before the report, so both of them describe the log as it ends up
            // rather than as it was mid-import.
            int undoneQsos = 0, undoneFields = 0;
            bool undoFailed = false;
            if (stopped && _importChoice != ImportChoice.NewLog && undo != null)
            {
                _importPhase = "putting the log back as it was";
                AdifHandlerWorker.ReportProgress(lastReportedPercent, "Undoing the import…");
                try
                {
                    lock (_syncLock) { undoneQsos = dal.UndoImport(undo, out undoneFields); }
                }
                catch (Exception ex)
                {
                    // SAID OUT LOUD, never swallowed. An operator told "your log is untouched" about a
                    // log that is not would find out months later, if ever.
                    undoFailed = true;
                    Log.Warn("Could not undo a stopped import: " + ex);
                }
            }

            // ── THE SECOND HALF OF A REPLACE ────────────────────────────────────────────────────────
            //
            // The file is in. Only now do the QSOs the log started with go, in one statement, and only
            // if the file actually brought something: a replace that stored NOTHING would otherwise
            // empty a log and put nothing in its place, which is the exact disaster this reordering
            // exists to prevent. Nothing was read that could be read? Then the log is left as it is and
            // the operator still has it.
            //
            // Not after a stop, either - the rollback above has already taken the new QSOs out again,
            // and the old ones must stay where they are.
            int replacedQsos = 0;
            bool replaceLeftAlone = false;
            if (!stopped && _importChoice == ImportChoice.Replace && undo != null)
            {
                if (importedQsoCount > 0)
                {
                    _importPhase = "removing the QSOs the log held before";
                    AdifHandlerWorker.ReportProgress(lastReportedPercent, "Replacing the log…");
                    try
                    {
                        lock (_syncLock) { replacedQsos = dal.FinishReplace(undo.LogId, undo.HighWaterQsoId); }
                    }
                    catch (Exception ex)
                    {
                        // The log now holds BOTH sets. Said out loud - it is not a state to leave a
                        // man in without telling him.
                        replaceLeftAlone = true;
                        Log.Warn("Could not finish a replace (the old QSOs are still in the log): " + ex);
                    }
                }
                else
                {
                    replaceLeftAlone = true;
                    Log.Warn("Replace stored no QSOs, so the log was left exactly as it was.");
                }
            }

            if (lastReportedPercent < savePhaseEndPercent)
            {
                lastReportedPercent = savePhaseEndPercent;
                AdifHandlerWorker.ReportProgress(lastReportedPercent, $"Saving to log {savePhaseEndPercent}%");
            }

            // THE BUTTON GOES WHEN THE ANSWER WOULD BE NO. From here on the import is drawing the log on
            // the screen; there is nothing left to stop, and a Stop button that cannot stop anything is
            // a lie. It was pressed here once and the import carried on to its success message, which
            // is exactly what a button standing there for seconds after its last checkpoint invites.
            this.Dispatcher.Invoke(() => ShowStopButton(false));

            _importPhase = "reloading the log to show it on screen";
            ObservableCollection<QSO> refreshedQsos;
            lock (_syncLock)
            {
                // THE LOG THAT IS OPEN, not the whole database. This used to reload every QSO of every log
                // into the main grid, so an import left the window showing other logs' contacts mixed in
                // with this one's until the next restart or log switch - and the count in the completion
                // message came from the same collection, announcing 67,622 after an import into a log
                // holding 28,366. A total across all logs is not a number this program has any use for.
                refreshedQsos = dal.GetQSOsForLog(dal.ActiveLogId, refreshProgress =>
                {
                    int percent = refreshPhaseStartPercent + (int)Math.Floor((refreshProgress * (refreshPhaseEndPercent - refreshPhaseStartPercent)) / 100.0);
                    if (percent > lastReportedPercent)
                    {
                        lastReportedPercent = percent;
                        AdifHandlerWorker.ReportProgress(percent, $"Refreshing log table {refreshProgress}%");
                    }
                });
            }

            AdifHandlerWorker.ReportProgress(100, "Import complete 100%");

            string reportPath = null, rejectsAdifPath = null;
            // EVERY IMPORT THAT READ A RECORD LEAVES A REPORT. It used to be written only when there was
            // a PROBLEM to describe, which meant the imports that went perfectly - the great majority -
            // left no trace of themselves at all: two runs of 28,513 QSOs each produced no file, and the
            // operator had nothing to check afterwards except his own memory. What went in, what was
            // already there and what the log now holds is worth recording whether or not anything went
            // wrong; a report that appears only in trouble is one that teaches its absence means nothing.
            //
            // The only import with nothing to say is one that read no records at all - a cancelled file
            // dialog, or a file with nothing in it.
            if (recordsRead > 0 || rejects.Count > 0 || stopped)
            {
                int totalInLog = 0;
                try { totalInLog = dal.GetQsoCountForLog(dal.ActiveLogId); }
                catch (Exception swallowed) { Log.Swallow(swallowed); }

                WriteImportReport(rejects, filledIn, mergeFilled, mergeAmbiguous, droppedDuplicates, droppedCount,
                                  importedQsoCount, completedQso, ambiguousQso,
                                  recordsRead, totalInLog, files, logName,
                                  stopped, stoppedWhileReading, undoneQsos, undoneFields, undoFailed,
                                  qsosAlreadyInLog, replacedQsos,
                                  out reportPath, out rejectsAdifPath);
            }

            e.Result = new AdifImportResult
            {
                FaultyQso = faultyQSO,
                ImportedQsoCount = importedQsoCount,
                CompletedQsoCount = completedQso,
                AmbiguousQsoCount = ambiguousQso,
                RefreshedQsos = refreshedQsos,
                RejectedCount = rejects.Count,
                FilledCount = filledIn.Count,
                RecordsRead = recordsRead,
                ReportPath = reportPath,
                RejectsAdifPath = rejectsAdifPath,
                Stopped = stopped,
                StoppedBeforeAnythingWasStored = stoppedWhileReading,
                UndoneQsoCount = undoneQsos,
                UndoneFieldCount = undoneFields,
                UndoFailed = undoFailed,
                ReplacedQsoCount = replacedQsos,
                ReplaceLeftAlone = replaceLeftAlone,
                NewLogId = _importChoice == ImportChoice.NewLog && dal != null ? dal.ActiveLogId : 0,
                NewLogName = _importChoice == ImportChoice.NewLog ? logName : null,
            };
            this.Dispatcher.Invoke(() => ImportFileQ.Clear());
        }

        // The two files, each as a caption and its FULL path - shown as links, so the path can be read
        // and copied like text and opened with one click like a link.
        private static List<KeyValuePair<string, string>> FileLinks(AdifImportResult r)
        {
            var links = new List<KeyValuePair<string, string>>();
            if (!string.IsNullOrEmpty(r.ReportPath))
                links.Add(new KeyValuePair<string, string>("The report:", r.ReportPath));
            if (!string.IsNullOrEmpty(r.RejectsAdifPath))
                links.Add(new KeyValuePair<string, string>("The QSOs to correct:", r.RejectsAdifPath));
            return links;
        }

        // Opens a file in whatever the operator uses for it. If Windows has nothing associated, the
        // folder is shown instead with the file picked out, so the button always leads somewhere.
        private void OpenPath(string path)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception first)
            {
                Log.Swallow(first);
                try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + path + "\""); }
                catch (Exception swallowed)
                {
                    Log.Swallow(swallowed);
                    HolyMessageBox.ShowWarning("The report could not be opened. It is on your Desktop:\n\n" + path,
                                               "Import report", this);
                }
            }
        }

        // The callsign to give a record that names no station of its own: the identity just confirmed
        // for a new log (not stored until the import finishes), else the log's own identity, else the
        // callsign the program is set to. Empty when nothing is known - such records are then rejected
        // rather than attributed to a station that may not have made them.
        private string FallbackStationCall(string programCallsign)
        {
            if (!string.IsNullOrWhiteSpace(_pendingImportCallsign)) return _pendingImportCallsign.Trim();
            try
            {
                if (dal != null)
                {
                    dal.GetLogIdentity(dal.ActiveLogId, out string logCall, out _);
                    if (!string.IsNullOrWhiteSpace(logCall)) return logCall.Trim();
                }
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
            return (programCallsign ?? string.Empty).Trim();
        }

        // One ADIF record rebuilt from a QSO the database refused, so the rejects file holds a real
        // record for it too. Never throws: a report is not worth losing over a formatting slip.
        private static string SafeRecordText(QSO q)
        {
            if (q == null) return string.Empty;
            try { return BuildQrzAdif(q); }
            catch (Exception swallowed) { Log.Swallow(swallowed); return string.Empty; }
        }

        // The two files this import leaves on the Desktop:
        //
        //   holylogger_import_report_<when>.txt   - what happened, in words: every record that did not
        //                                           make it, named, with the reason.
        //   holylogger_rejected_qsos_<when>.adi   - those same records, verbatim, as an ADIF file the
        //                                           operator can correct in any editor and import again.
        //
        // The pair is the point: the first says what is wrong, the second is the thing to fix. Import
        // matches a re-imported record against what is already in the log, so bringing the corrected
        // file back adds the missing contacts without duplicating anything.
        // An ADIF date (yyyyMMdd) as the day the operator reads everywhere else in this program.
        // Anything that is not a date is passed through rather than guessed at.
        private static string FormatAdifDate(string adif)
        {
            string s = (adif ?? string.Empty).Trim();
            if (s.Length < 8) return s.Length == 0 ? "—" : s;
            DateTime d;
            return DateTime.TryParseExact(s.Substring(0, 8), "yyyyMMdd",
                       System.Globalization.CultureInfo.InvariantCulture,
                       System.Globalization.DateTimeStyles.None, out d)
                   ? d.ToString("dd-MM-yyyy")
                   : s;
        }

        // Centres a value in a fixed-width report column. The summary's two number columns sit under
        // headings much wider than the numbers ("NUMBER OF CASES" over a 7), and a right-justified
        // column of small numbers hugs the far edge of its heading instead of sitting beneath it.
        private static string Centred(string s, int width)
        {
            if (s == null) s = "";
            if (s.Length >= width) return s;
            int left = (width - s.Length) / 2;
            return new string(' ', left) + s + new string(' ', width - s.Length - left);
        }

        // The last line of any message that hands the operator a report: where to find it again. The
        // path above it is one click today and gone tomorrow - this says how to come back to it in a
        // week, when the message is long closed.
        private const string ReportsFooter =
            "Reports can be viewed at any time:\n**File → Open Reports Folder**";

        // How many individual rows a report section prints before it stops naming them one by one.
        //
        // A 77 MB ADIF holds a few hundred thousand contacts. A finding on even a tenth of them is tens
        // of thousands of lines - a report too big to open, built in a StringBuilder inside a 32-bit
        // process that ran out of memory on this very file once already. The COUNTS and the summary of
        // corrections stay complete and are what the operator acts on; only the naming stops.
        private const int MaxReportRows = 10000;

        // One cell of a report table: padded to its column, cut with an ellipsis when it will not fit.
        // Cutting matters more than showing every letter - one long value would otherwise push every
        // column after it out of line for that row alone, and a table that stops lining up is not one.
        // The Log Fixer's report has its own copy: these two files are read side by side and are meant
        // to look alike, but neither should have to reach into the other for a helper this small.
        private static string Pad(string text, int width)
        {
            string s = string.IsNullOrWhiteSpace(text) ? string.Empty : text.Trim();
            if (s.Length >= width) return s.Substring(0, Math.Max(1, width - 2)) + "… ";
            return s.PadRight(width);
        }

        // The rule that separates one section of a report from the next. The Log Fixer's report draws
        // the same line at the same width - the two files land in the same folder and are read one
        // after the other, and a reader should not have to notice which one they are holding.
        private const string ImportReportRule =
            "────────────────────────────────────────────────────────────────────";

        // A line of the summary: what it is, how many, and what to do about it.
        private static string SummaryRow(string label, int count, string tail)
        {
            return "  " + label.PadRight(27) + count.ToString("N0").PadLeft(9)
                 + (string.IsNullOrEmpty(tail) ? string.Empty : "   " + tail);
        }

        // "12.4 MB" / "873 KB" - the size the operator sees in Explorer, for the file the report names.
        private static string FileSizeText(string path)
        {
            try
            {
                long b = new System.IO.FileInfo(path).Length;
                if (b >= 1024L * 1024L) return (b / (1024.0 * 1024.0)).ToString("N1") + " MB";
                if (b >= 1024L) return (b / 1024.0).ToString("N0") + " KB";
                return b.ToString("N0") + " bytes";
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); return null; }
        }

        // What the operator answered when the import asked where the file should go, in his own words
        // rather than the enum's.
        private static string ChoiceSentence(ImportChoice choice)
        {
            switch (choice)
            {
                case ImportChoice.NewLog:  return "a NEW LOG of its own for this file";
                case ImportChoice.Replace: return "REPLACE the log that was open with this file";
                default:                   return "ADD this file to the log that was open";
            }
        }

        // The identity the log carries - the station callsign and operator its QSOs were made under.
        // For a brand-new log this is the one just confirmed in the import dialog, which is not written
        // to the database until the import finishes, so it is read from there first.
        private string ImportLogIdentity()
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(_pendingImportCallsign))
                    return (_pendingImportCallsign ?? string.Empty).Trim()
                         + (string.IsNullOrWhiteSpace(_pendingImportOperator)
                            ? string.Empty : "  /  " + _pendingImportOperator.Trim());

                string call, op;
                lock (_syncLock) { dal.GetLogIdentity(dal.ActiveLogId, out call, out op); }
                if (string.IsNullOrWhiteSpace(call) && string.IsNullOrWhiteSpace(op)) return null;
                return (call ?? string.Empty).Trim()
                     + (string.IsNullOrWhiteSpace(op) ? string.Empty : "  /  " + op.Trim());
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); return null; }
        }

        private void WriteImportReport(List<ImportReject> rejects, List<HolyLogParser.FilledField> filled,
                                       List<DataAccess.MergeNote> mergeFilled,
                                       List<DataAccess.MergeNote> mergeAmbiguous,
                                       List<QSO> droppedDuplicates, int droppedCount,
                                       int imported, int completed, int ambiguous,
                                       int recordsRead, int totalInLog,
                                       List<string> files, string logName,
                                       bool stopped, bool stoppedWhileReading,
                                       int undoneQsos, int undoneFields, bool undoFailed,
                                       int heldBefore, int replacedQsos,
                                       out string reportPath, out string rejectsAdifPath)
        {
            reportPath = null;
            rejectsAdifPath = null;
            try
            {
                string folder = DataAccess.ReportsFolder;
                string stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmm");
                string txt = System.IO.Path.Combine(folder, $"holylogger_import_report_{stamp}.txt");
                string adi = System.IO.Path.Combine(folder, $"holylogger_rejected_qsos_{stamp}.adi");

                ImportChoice choice = _importChoice;
                bool newLog = choice == ImportChoice.NewLog;
                int dropped = droppedCount;

                var sb = new StringBuilder();
                sb.AppendLine("HolyLogger — import report");
                sb.AppendLine(DateTime.Now.ToString("dddd d MMMM yyyy, HH:mm"));
                sb.AppendLine();

                // WHAT WAS ASKED FOR, AND OF WHAT.
                //
                // The report used to open on its numbers, and named neither the file it read nor the log
                // it wrote to nor which of the three things the operator had asked for. Read a week later
                // - or read by anyone but the person who ran it - "Added to the log: 0" cannot be judged
                // without them: it is a disaster under "replace the log", and it is exactly what
                // re-importing a file you already have is supposed to say.
                // STOPPED SAYS SO FIRST. Every number under it counts a PART of the file, and a report
                // that leads with "Added to the new log: 4,000" out of a file of 28,000 reads as a
                // failed import unless the first thing it says is that the operator stopped it.
                if (stopped)
                {
                    sb.AppendLine("YOU STOPPED THIS IMPORT.");
                    if (undoFailed)
                    {
                        sb.AppendLine("The import could NOT be undone. Part of the file is in your log.");
                        sb.AppendLine("What went in is listed below.");
                    }
                    else if (undoneQsos > 0 || undoneFields > 0)
                    {
                        sb.AppendLine("Everything it had done was put back. " + undoneQsos.ToString("N0")
                                      + " QSO(s) were taken out again and " + undoneFields.ToString("N0")
                                      + " QSO(s) had the fields this import filled emptied again.");
                        sb.AppendLine("Your log is exactly as it was before you started.");
                        sb.AppendLine("The numbers below are what the import HAD done before you stopped it.");
                        sb.AppendLine("None of it is in your log now.");
                    }
                    else if (stoppedWhileReading)
                    {
                        sb.AppendLine("It stopped while the file was still being read, so nothing was ever");
                        sb.AppendLine("stored. Your log is exactly as it was.");
                    }
                    else
                    {
                        sb.AppendLine("Nothing had been stored yet, so your log is exactly as it was.");
                    }
                    sb.AppendLine();
                }

                sb.AppendLine("What you asked for : " + ChoiceSentence(choice));
                if (files != null)
                {
                    bool first = true;
                    foreach (var f in files)
                    {
                        sb.AppendLine((first ? "File               : " : "                     ")
                                      + System.IO.Path.GetFileName(f));
                        string size = FileSizeText(f);
                        sb.AppendLine("                     " + f
                                      + (string.IsNullOrEmpty(size) ? string.Empty : "   (" + size + ")"));
                        first = false;
                    }
                }
                if (!string.IsNullOrWhiteSpace(logName))
                    sb.AppendLine((newLog ? "New log            : " : "Log                : ") + logName);
                string identity = ImportLogIdentity();
                if (!string.IsNullOrWhiteSpace(identity))
                    sb.AppendLine("Made under         : " + identity);
                sb.AppendLine();

                if (newLog)
                {
                    // EVERY RECORD THE FILE HELD, ACCOUNTED FOR.
                    //
                    // The numbers ADD UP, and that is the whole point of the block: read = added +
                    // repeated + turned away. Before, records could go missing between "read from the
                    // file" and "added to the log" with nothing to say where - the "Import Duplicates"
                    // option throws repeats away in silence, so a file of 100 could put 95 in the log
                    // and the report would print both figures without a word about the other five.
                    //
                    // The reconciliation is checked rather than trusted: if the numbers do not add up
                    // the report says so, instead of quietly presenting an arithmetic that is wrong.
                    sb.AppendLine(ImportReportRule);
                    sb.AppendLine(stopped ? "WHAT BECAME OF EVERY RECORD READ BEFORE YOU STOPPED"
                                          : "WHAT BECAME OF EVERY RECORD IN THE FILE");
                    sb.AppendLine(ImportReportRule);
                    sb.AppendLine();
                    sb.AppendLine(SummaryRow(stopped ? "Records read before Stop" : "Records read from the file",
                                             recordsRead, null));
                    sb.AppendLine("  " + new string('-', 36));
                    sb.AppendLine(SummaryRow("Added to the new log", imported, null));
                    if (dropped > 0)
                        sb.AppendLine(SummaryRow("Repeated inside the file", dropped, "not stored — listed below"));
                    if (completed > 0)
                        sb.AppendLine(SummaryRow("Already added by this import", completed, "nothing was added twice"));
                    sb.AppendLine(SummaryRow("Could not be stored", rejects.Count,
                                             rejects.Count > 0 ? "listed below, and handed back as an ADIF"
                                                               : "nothing was turned away"));
                    // A STOPPED RUN IS ALLOWED NOT TO ADD UP. Records read and then abandoned are the
                    // difference, and they are the operator's own doing - saying "please report this"
                    // about them would be crying wolf on the one line that has to keep its meaning.
                    int unaccounted = recordsRead - imported - dropped - completed - rejects.Count;
                    if (unaccounted != 0)
                        sb.AppendLine(SummaryRow(stopped ? "Read, then abandoned" : "Not accounted for", unaccounted,
                                                 stopped ? "you stopped the import before these were saved"
                                                         : "please report this - the figures above should add up"));
                    sb.AppendLine();

                    // A STOPPED NEW-LOG IMPORT KEEPS NOTHING, so "Now in this log" would name a log
                    // that is about to be deleted - the log was made for this file and goes with it.
                    // The report is written a moment before that happens, so it says what is about to
                    // be true rather than a count nobody will ever be able to check.
                    if (stopped)
                    {
                        sb.AppendLine("  Nothing was kept. The log created for this import is removed when you");
                        sb.AppendLine("  stop it, so your logs are as they were before you started. Import the");
                        sb.AppendLine("  file again when you want it - it starts from the beginning.");
                    }
                    else
                    {
                        sb.AppendLine(SummaryRow("Now in this log", totalInLog, null));
                    }
                    sb.AppendLine();
                }
                else
                {
                    // ── MERGE AND REPLACE, ACCOUNTED FOR THE SAME WAY ────────────────────────────────
                    //
                    // The same spine as the new-log summary: every record the file held, said once,
                    // ending in a total that can be checked against the log itself. What differs is
                    // where a record can END UP - a log that already holds QSOs can recognise one, and
                    // then the record adds nothing and is not lost either.
                    //
                    // The old block printed six unrelated lines that did not add up to anything and
                    // never named the log's size before the import, so "Added: 1,204" could not be
                    // placed against anything. Both ends are printed now: what the log held, and what
                    // it holds.
                    bool replacing = choice == ImportChoice.Replace;

                    sb.AppendLine(ImportReportRule);
                    sb.AppendLine(stopped ? "WHAT BECAME OF EVERY RECORD READ BEFORE YOU STOPPED"
                                          : "WHAT BECAME OF EVERY RECORD IN THE FILE");
                    sb.AppendLine(ImportReportRule);
                    sb.AppendLine();
                    sb.AppendLine(SummaryRow(stopped ? "Records read before Stop" : "Records read from the file",
                                             recordsRead, null));
                    sb.AppendLine("  " + new string('-', 36));
                    sb.AppendLine(SummaryRow("Added to the log", imported, null));

                    // Replace does not match against the log: the QSOs in it are the ones being thrown
                    // away, so these two lines are always zero there and are not printed at all.
                    if (!replacing)
                    {
                        int changed = mergeFilled == null ? 0 : mergeFilled.Count;
                        sb.AppendLine(SummaryRow("Already in your log", completed,
                                                 completed > 0 ? "nothing added twice" : null));
                        if (completed > 0)
                            sb.AppendLine(SummaryRow("   of those, filled in", changed,
                                                     changed > 0 ? "empty fields filled from the file — listed below"
                                                                 : "nothing was written; they were complete already"));
                        if (ambiguous > 0)
                            sb.AppendLine(SummaryRow("Matched two QSOs at once", ambiguous,
                                                     "left alone — listed below"));
                    }

                    if (dropped > 0)
                        sb.AppendLine(SummaryRow("Repeated inside the file", dropped, "not stored — listed below"));

                    sb.AppendLine(SummaryRow("Could not be stored", rejects.Count,
                                             rejects.Count > 0 ? "listed below, and handed back as an ADIF"
                                                               : "nothing was turned away"));

                    int unaccounted = recordsRead - imported - dropped - completed - ambiguous - rejects.Count;
                    if (unaccounted != 0)
                        sb.AppendLine(SummaryRow(stopped ? "Read, then abandoned" : "Not accounted for", unaccounted,
                                                 stopped ? "you stopped the import before these were saved"
                                                         : "please report this - the figures above should add up"));
                    sb.AppendLine();

                    // BOTH ENDS OF THE LOG. One number is a fact; two are a change, which is what an
                    // import is.
                    if (heldBefore >= 0)
                        sb.AppendLine(SummaryRow("This log held before", heldBefore, null));
                    if (replacing && replacedQsos > 0)
                        sb.AppendLine(SummaryRow("Removed by the replace", replacedQsos,
                                                 "the QSOs it held before — your backup ADIF has them"));
                    if (stopped)
                    {
                        sb.AppendLine();
                        if (undoFailed)
                            sb.AppendLine("  The import could NOT be undone, so some of the above is still in your log.");
                        else
                            sb.AppendLine("  Nothing above is in your log. It was all put back.");
                    }
                    sb.AppendLine(SummaryRow("This log holds now", totalInLog, null));
                    sb.AppendLine();
                }

                if (rejects.Count > 0)
                {
                    sb.AppendLine("────────────────────────────────────────────────────────────────────");
                    sb.AppendLine($"COULD NOT BE STORED ({rejects.Count:N0})");
                    sb.AppendLine("────────────────────────────────────────────────────────────────────");
                    sb.AppendLine();
                    sb.AppendLine("Each of these is in your file and is NOT in your log. A QSO cannot be");
                    sb.AppendLine("stored without a callsign, a date, a time, a band, a mode and the");
                    sb.AppendLine("station callsign it was made under.");
                    sb.AppendLine();
                    sb.AppendLine("The same records are saved beside this report as:");
                    sb.AppendLine("    " + System.IO.Path.GetFileName(adi));
                    sb.AppendLine("Correct them there and import that file — anything already in your log");
                    sb.AppendLine("is recognised, so nothing will be duplicated.");
                    sb.AppendLine();
                    sb.AppendLine("  " + Pad("Date", 12) + Pad("Time", 7) + Pad("Callsign", 14)
                                  + Pad("Band", 7) + Pad("Mode", 7) + Pad("Why it was not stored", 34)
                                  + "Where in the file");
                    sb.AppendLine("  " + Pad(new string('-', 10), 12) + Pad(new string('-', 5), 7)
                                  + Pad(new string('-', 12), 14) + Pad(new string('-', 5), 7)
                                  + Pad(new string('-', 5), 7) + Pad(new string('-', 32), 34)
                                  + new string('-', 30));

                    foreach (var r in rejects)
                    {
                        string where = r.Number > 0
                            ? $"{r.FileName}, record {r.Number:N0}"
                            : r.FileName;
                        sb.AppendLine(("  " + Pad(FormatAdifDate(r.Date), 12) + Pad(r.Time, 7)
                                       + Pad(string.IsNullOrWhiteSpace(r.Call) ? "—" : r.Call, 14)
                                       + Pad(r.Band, 7) + Pad(r.Mode, 7)
                                       + Pad(r.Reason, 34) + where).TrimEnd());
                    }
                    sb.AppendLine();
                }

                // WHAT THE "Import Duplicates" OPTION THREW AWAY.
                //
                // These never reached the log and never reached the rejects file either: with that option
                // off the parser drops them where it stands, so until now they were the difference between
                // two numbers in a report that never mentioned them. Named here, with the rule that
                // decided it spelled out - the rule is coarser than the one the log itself uses for a
                // duplicate, and an operator who is losing contacts to it deserves to see which.
                if (dropped > 0)
                {
                    sb.AppendLine(ImportReportRule);
                    sb.AppendLine($"REPEATED INSIDE THE FILE ({dropped:N0})");
                    sb.AppendLine(ImportReportRule);
                    sb.AppendLine();
                    sb.AppendLine("The file itself holds each of these contacts more than once, and Options →");
                    sb.AppendLine("Import → \"Import Duplicates\" is switched off, so only the first of each was");
                    sb.AppendLine("kept. Two records are the same contact when the callsign, the date, the");
                    sb.AppendLine("band, the mode and the MINUTE are all the same — the same rule the Log Fixer");
                    sb.AppendLine("and Tools → Remove Duplicates use. Two contacts with the same station at");
                    sb.AppendLine("different times of day are two contacts, and both were kept.");
                    sb.AppendLine();
                    sb.AppendLine("If you want them all, switch \"Import Duplicates\" on and import the file");
                    sb.AppendLine("again — whatever is already in the log is recognised, so nothing else will");
                    sb.AppendLine("be duplicated.");
                    sb.AppendLine();
                    sb.AppendLine("  " + Pad("Date", 12) + Pad("Time", 7) + Pad("Callsign", 14)
                                  + Pad("Band", 7) + Pad("Mode", 7) + "Station");
                    sb.AppendLine("  " + Pad(new string('-', 10), 12) + Pad(new string('-', 5), 7)
                                  + Pad(new string('-', 12), 14) + Pad(new string('-', 5), 7)
                                  + Pad(new string('-', 5), 7) + new string('-', 12));

                    int shownDupes = 0;
                    foreach (var q in droppedDuplicates)
                    {
                        if (shownDupes >= MaxReportRows)
                        {
                            sb.AppendLine($"  … and {dropped - shownDupes:N0} more, not listed one by one.");
                            break;
                        }
                        shownDupes++;
                        sb.AppendLine(("  " + Pad(FormatAdifDate(q.Date), 12) + Pad(q.Time, 7)
                                       + Pad(string.IsNullOrWhiteSpace(q.DXCall) ? "—" : q.DXCall, 14)
                                       + Pad(q.Band, 7) + Pad(q.Mode, 7) + (q.MyCall ?? string.Empty)).TrimEnd());
                    }

                    // The parser keeps at most 10,000 of them - they are whole QSOs and it has already
                    // thrown them away. The count above is the true one; this says why the list is
                    // shorter than the count, rather than leaving the two to disagree in silence.
                    if (dropped > droppedDuplicates.Count)
                        sb.AppendLine($"  … and {dropped - droppedDuplicates.Count:N0} more, not listed one by one.");

                    sb.AppendLine();
                }

                if (filled.Count > 0)
                {
                    sb.AppendLine("────────────────────────────────────────────────────────────────────");
                    sb.AppendLine($"FILLED IN FOR YOU ({filled.Count:N0})");
                    sb.AppendLine("────────────────────────────────────────────────────────────────────");
                    sb.AppendLine();
                    sb.AppendLine("These records were missing a field that the rest of the record already");
                    sb.AppendLine("answered, so it was worked out instead of turning the QSO away.");
                    sb.AppendLine();
                    // No Date or Time columns: the parser keeps neither for a filled field, and a column
                    // of nothing but dashes is worse than no column.
                    sb.AppendLine("  " + Pad("Callsign", 14) + Pad("Field", 20) + Pad("Value", 20)
                                  + "Taken from");
                    sb.AppendLine("  " + Pad(new string('-', 12), 14) + Pad(new string('-', 18), 20)
                                  + Pad(new string('-', 18), 20) + new string('-', 30));

                    foreach (var f in filled)
                        sb.AppendLine(("  " + Pad(string.IsNullOrWhiteSpace(f.Call) ? "—" : f.Call, 14)
                                       + Pad(f.Field, 20) + Pad(f.Value, 20) + f.From).TrimEnd());
                    sb.AppendLine();
                }

                // ── WHAT WAS CHANGED IN QSOs YOU ALREADY HAD ────────────────────────────────────────
                //
                // The one part of an import that alters contacts already in the log. Everything else
                // either adds a QSO or turns one away; this writes into stored ones, and afterwards they
                // look exactly as though they always held the values. Named field by field, with what
                // went in, because that is the only chance anyone has to spot a bad file writing over
                // good QSOs - and the only record of it that will ever exist.
                if (completed > 0)
                {
                    int changed = mergeFilled == null ? 0 : mergeFilled.Count;

                    sb.AppendLine("────────────────────────────────────────────────────────────────────");
                    sb.AppendLine($"ALREADY IN YOUR LOG ({completed:N0})");
                    sb.AppendLine("────────────────────────────────────────────────────────────────────");
                    sb.AppendLine();
                    sb.AppendLine("These records matched a QSO your log already held, so none of them added");
                    sb.AppendLine("a contact. Where the file held something in a field that was EMPTY here,");
                    sb.AppendLine("it was filled in; nothing that already had a value was touched.");
                    sb.AppendLine();
                    sb.AppendLine($"  Matched and changed  : {changed:N0}");
                    sb.AppendLine($"  Matched, nothing new : {completed - changed:N0}");
                    sb.AppendLine();

                    // A MERGE THAT CHANGED NOTHING STILL SAYS SO. Re-importing a file the log already
                    // holds is the commonest thing an operator does with an import, and "it added nothing
                    // and changed nothing" is the answer he is looking for - printed, not left to be
                    // inferred from a report that has no section about it.
                    if (changed == 0)
                    {
                        sb.AppendLine("  Nothing was written. Every record was already complete here.");
                        sb.AppendLine();
                    }
                    else
                    {
                        // ONE LINE PER FIELD, with the contact repeated on each. A QSO that gained three
                        // fields is three lines - which is longer, and is the shape that sorts, greps and
                        // pastes into a spreadsheet. The alternative, a contact line with its fields
                        // indented underneath, cannot do any of those.
                        sb.AppendLine("  " + Pad("Date", 12) + Pad("Time", 7) + Pad("Callsign", 14)
                                      + Pad("Band", 7) + Pad("Mode", 7) + Pad("Field", 16) + "Written");
                        sb.AppendLine("  " + Pad(new string('-', 10), 12) + Pad(new string('-', 5), 7)
                                      + Pad(new string('-', 12), 14) + Pad(new string('-', 5), 7)
                                      + Pad(new string('-', 5), 7) + Pad(new string('-', 14), 16)
                                      + new string('-', 30));

                        int shown = 0;
                        foreach (var m in mergeFilled)
                        {
                            if (shown >= MaxReportRows)
                            {
                                sb.AppendLine($"  … and {mergeFilled.Count - shown:N0} more, not listed one by one.");
                                break;
                            }
                            shown++;
                            foreach (var f in m.Fields)
                                sb.AppendLine(("  " + Pad(FormatAdifDate(m.Date), 12) + Pad(m.Time, 7)
                                               + Pad(string.IsNullOrWhiteSpace(m.Call) ? "—" : m.Call, 14)
                                               + Pad(m.Band, 7) + Pad(m.Mode, 7)
                                               + Pad(f.Key, 16) + f.Value).TrimEnd());
                        }
                        sb.AppendLine();
                    }
                }

                // MATCHED TWO CONTACTS AT ONCE, so nothing was done. The log holds this QSO more than
                // once for the same minute, and there is no honest way to choose which copy the record
                // belongs to. Naming them, not judging them: whether those copies are duplicates is the
                // Log Fixer's question and it has a kind of its own for it.
                if (mergeAmbiguous != null && mergeAmbiguous.Count > 0)
                {
                    sb.AppendLine("────────────────────────────────────────────────────────────────────");
                    sb.AppendLine($"MATCHED MORE THAN ONE QSO — LEFT ALONE ({ambiguous:N0})");
                    sb.AppendLine("────────────────────────────────────────────────────────────────────");
                    sb.AppendLine();
                    sb.AppendLine("Your log holds this contact more than once for the same minute, so there");
                    sb.AppendLine("was no way to tell which copy the record belonged to. Nothing was written");
                    sb.AppendLine("and nothing was added. The Log Fixer lists repeated contacts under");
                    sb.AppendLine("\"Duplicate contact\" if you want to deal with them.");
                    sb.AppendLine();

                    sb.AppendLine("  " + Pad("Date", 12) + Pad("Time", 7) + Pad("Callsign", 14)
                                  + Pad("Band", 7) + "Mode");
                    sb.AppendLine("  " + Pad(new string('-', 10), 12) + Pad(new string('-', 5), 7)
                                  + Pad(new string('-', 12), 14) + Pad(new string('-', 5), 7)
                                  + new string('-', 5));

                    int shown = 0;
                    foreach (var m in mergeAmbiguous)
                    {
                        if (shown >= MaxReportRows)
                        {
                            sb.AppendLine($"  … and {mergeAmbiguous.Count - shown:N0} more, not listed one by one.");
                            break;
                        }
                        shown++;
                        sb.AppendLine(("  " + Pad(FormatAdifDate(m.Date), 12) + Pad(m.Time, 7)
                                       + Pad(string.IsNullOrWhiteSpace(m.Call) ? "—" : m.Call, 14)
                                       + Pad(m.Band, 7) + m.Mode).TrimEnd());
                    }
                    sb.AppendLine();
                }

                // THE COUNTRY SECTIONS ARE GONE FROM THIS REPORT.
                //
                // They were 'HolyLogger suggests', 'same country spelled differently' and 'counts
                // towards no country' - three judgements about whether a QSO is right, made here while
                // the file was read. The Log Fixer judges the same QSOs from its own code a moment
                // later, and the two disagreed: T9/VE6PR was proposed for correction by this report and
                // accepted without comment by the Fixer, because only the Fixer knew that a stroke
                // callsign names two countries and the operator's own answer settles it.
                //
                // One question, one authority. This report now says only what the import DID - what it
                // could not store, and what it had to work out - and the Fixer, which opens by itself
                // when the import finishes, says whether the log is right.

                System.IO.File.WriteAllText(txt, sb.ToString(), Encoding.UTF8);
                reportPath = txt;
                Reports.Note(txt);

                if (rejects.Count > 0)
                {
                    var adif = new StringBuilder();
                    adif.AppendLine("HolyLogger — QSOs that could not be imported.");
                    adif.AppendLine("Each record below is preceded by the reason it was turned away.");
                    adif.AppendLine("Correct them and import this file; QSOs already in the log are recognised.");
                    adif.AppendLine("<adif_ver:5>3.1.4");
                    adif.AppendLine("<programid:10>HolyLogger");
                    adif.AppendLine("<eoh>");
                    adif.AppendLine();
                    foreach (var r in rejects)
                    {
                        // A record rebuilt from a refused QSO already ends with its own <eor>; one
                        // taken verbatim from the file does not, because the file's <EOR> is what the
                        // reader stopped at. Strip either way and write exactly one.
                        string body = (r.Raw ?? string.Empty).Trim();
                        if (body.EndsWith("<eor>", StringComparison.OrdinalIgnoreCase))
                            body = body.Substring(0, body.Length - "<eor>".Length).TrimEnd();
                        if (body.Length == 0) continue;   // nothing to correct; the report still names it

                        // The reason sits OUTSIDE the record's fields: a reader that understands ADIF
                        // takes the tags and ignores this line, and a person opening the file in an
                        // editor sees straight away what to fix.
                        adif.AppendLine($"// {r.Reason}");
                        adif.AppendLine(body);
                        adif.AppendLine("<eor>");
                        adif.AppendLine();
                    }
                    System.IO.File.WriteAllText(adi, adif.ToString(), Encoding.UTF8);
                    rejectsAdifPath = adi;
                }
            }
            catch (Exception swallowed)
            {
                // The import itself succeeded; failing to write a report must not undo it.
                Log.Swallow(swallowed);
            }
        }

        private void ExportMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (!RequireActiveLog("export")) return;
            // Export the ACTIVE log only (the "(Active Log)" menu label). Uses the same helper /
            // save dialog as the View Logs window's Export ADIF button, so behaviour is identical.
            ExportQsosToAdif(dal.GetQSOsForLog(dal.ActiveLogId), this);
        }

        private void ExportCabrilloMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (!RequireActiveLog("export")) return;
            // Export the ACTIVE log only (the "(Active Log)" menu label). Uses the same helper /
            // save dialog as the View Logs window's Export Cabrillo button.
            ExportQsosToCabrillo(dal.GetQSOsForLog(dal.ActiveLogId), dal.ActiveLogId, this);
        }

        private async Task<string> UploadCabrilloToIARC(string callsign, string op, string mode, string band, string power, string overlay, string email, string name, string country, ObservableCollection<QSO> QSOList)
        {
            try
            {
                //prepare the header data
                Contester c = new Contester();
                c.Callsign = callsign.Trim();
                c.Category_Band = band.Trim();
                c.Category_Operator = op.Trim();
                c.Category_Mode = mode.Trim();
                c.Category_Power = power.Trim();
                c.Category_Overlay = overlay.Trim();
                c.Contest = "HOLYLAND";
                c.Email = email;
                c.Grid = TB_MyLocator.Text.Trim();
                c.Name = name.Trim();
                c.Country = country.Trim();

                //generate cabrillo from the QSOs the caller handed us (the active log), NOT every log in
                //the database - the caller decides which log is being sent.
                string cabrillo = Services.GenerateCabrillo(QSOList, c);

                //set multipart
                var formData = new MultipartFormDataContent();
                var filename = callsign + "_" + DateTime.UtcNow.ToString("yyyyMMdd") + ".txt";
                formData.Add(new StringContent(cabrillo), "file", filename);

                c.filename = filename;
                c.timestamp = DateTime.UtcNow.Ticks.ToString();

                //post file
                // Started off the UI thread - on .NET Framework the proxy is resolved on the thread
                // that starts a request, and this runs from the contest-upload button.
                var response = await Task.Run(() => _sharedHttpClient.PostAsync("https://tools.iarc.org/iarc/Server/ftp.php", formData));

                if (response.IsSuccessStatusCode)
                {
                    // Create a StringContent object with your JSON data
                    //set multipart
                    formData = new MultipartFormDataContent();
                    formData.Add(new StringContent(JsonConvert.SerializeObject(c).Replace("'", "")), "info");

                    try
                    {
                        // Send a POST request to the URL with the JSON data
                        //upload_log.php
                        response = await Task.Run(() => _sharedHttpClient.PostAsync("https://tools.iarc.org/iarc/Server/upload_log.php", formData));

                        // Check if the request was successful
                        if (response.IsSuccessStatusCode)
                        {
                            // Read and display the response from the PHP file
                            string responseContent = await response.Content.ReadAsStringAsync();
                            try
                            {
                                ServerResponse serverResponse = JsonConvert.DeserializeObject<ServerResponse>(responseContent);
                                if (serverResponse.Success)
                                {
                                    return "File uploaded successfully.";
                                }
                                else
                                {
                                    return serverResponse.Msg;
                                }
                            }
                            catch
                            {
                                return "Failed to send log. Please export cabrillo and send via the website";
                            }
                        }
                        else
                        {
                            return $"Error uploading file. Status code: {response.StatusCode}";
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Swallow(ex);
                        return $"Error uploading file";
                    }
                }
                else
                {
                    return $"Error uploading file. Status code: {response.StatusCode}";
                }
            }
            catch (Exception ex)
            {
                return $"Error uploading file: {ex.Message}";
            }
        }

    }
}



