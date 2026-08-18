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

        private void ImportAdifMenuItem_Click(object sender, RoutedEventArgs e)
        {
            // Offer to save an in-progress new QSO before an import reloads the log.
            GuardUnsavedQso("import the ADIF file");

            //CultureInfo provider = CultureInfo.InvariantCulture;
            OpenFileDialog openFileDialog = new OpenFileDialog();
            openFileDialog.Filter = "ADIF files (*.adi)|*.adi";
            

            if (openFileDialog.ShowDialog() == true)
            {
                // First: does this file become its OWN new log, or get added to the log open now?
                // With NO log open there is no "log open now" to add to, so the question is not asked -
                // the file becomes its own new log, which is the only answer there is. Import is
                // deliberately NOT blocked when no log is open: bringing a file in is exactly how an
                // operator with no logs gets one.
                ImportTarget target = dal != null && !dal.HasActiveLog ? ImportTarget.NewLog : AskImportTarget();
                if (target == ImportTarget.Cancel) return;

                if (target == ImportTarget.NewLog)
                {
                    // Create a new REGULAR log for the file and make it active so the import lands in it —
                    // nothing touches the existing logs. Its identity comes from the ADIF (below).
                    string suggested = UniqueLogName(System.IO.Path.GetFileNameWithoutExtension(openFileDialog.FileName));
                    var nameDlg = new NewLogWindow(dal, "Name the new log for the imported file:", suggested) { Owner = this };
                    if (nameDlg.ShowDialog() != true) return;   // cancelled -> abort
                    long newId = dal.CreateLog(nameDlg.LogName, string.Empty);
                    SwitchActiveLog(newId);
                }
                else
                {
                    // Into the current log: offer MERGE (add) or REPLACE if the ACTIVE log has QSOs.
                    int existing = 0;
                    try { if (dal != null) existing = dal.GetQsoCountForLog(dal.ActiveLogId); }
                    catch { existing = 0; }

                    if (existing > 0)
                    {
                        ImportLogChoice choice = AskImportMergeOrReplace(existing);
                        if (choice == ImportLogChoice.Cancel)
                            return;
                        if (choice == ImportLogChoice.Replace && !BackupAndClearLogForReplace())
                            return; // backup cancelled or failed -> abort; the log is left untouched
                    }
                }

                // Identity handling before the import runs. Scan the ADIF for the callsign(s) / operator(s)
                // it was made under.
                if (dal != null)
                {
                    ScanAdifIdentity(openFileDialog.FileName, out var adifCalls, out var adifOps);
                    string fileName = System.IO.Path.GetFileName(openFileDialog.FileName);

                    if (!dal.LogHasIdentity(dal.ActiveLogId))
                    {
                        // No identity yet -> confirm (and let the user cancel) the identity the imported
                        // log will get. Station callsign from the ADIF (not editable); operator editable.
                        var idDlg = new ImportIdentityWindow(adifCalls, adifOps, fileName) { Owner = this };
                        if (idDlg.ShowDialog() != true) return;   // cancel -> abort the whole import
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
                                return;   // user declined -> abort the import
                        }
                    }
                }

                ImportFileQ.Add(openFileDialog.FileName);
                StartAdifImportWorker();
            }
        }

        // Identity confirmed in the import dialog; applied to the log once the import finishes.
        private string _pendingImportCallsign;
        private string _pendingImportOperator;

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
        private ImportTarget AskImportTarget()
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
                Text = "Where should the imported QSOs go?",
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
            AddOption("New log", "create a new log just for this file (recommended for a logbook from another program) — your existing logs are untouched.", new Thickness(0, 0, 0, 12));
            AddOption("Current log", "add the file's QSOs to the log open now" + (string.IsNullOrWhiteSpace(curName) ? "" : " (" + curName + ")") + ".", new Thickness(0, 0, 0, 30));

            var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            Button MakeButton(string text) => new Button { Content = text, MinWidth = 100, Margin = new Thickness(6, 0, 6, 0), Padding = new Thickness(12, 5, 12, 5), FontSize = 16 };
            var newBtn = MakeButton("New log");
            var curBtn = MakeButton("Current log");
            var cancelBtn = MakeButton("Cancel"); cancelBtn.IsCancel = true;
            newBtn.Click += (s, e) => { result = ImportTarget.NewLog; dialog.Close(); };
            curBtn.Click += (s, e) => { result = ImportTarget.CurrentLog; dialog.Close(); };
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
            root.Children.Add(MakeOption("Replace", "first save a backup of your current log to a file you choose, then clear the log and import the file.", new Thickness(0, 0, 0, 34)));

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

            UploadProgress = "Starting import 0%";
            ToggleUploadProgress(Visibility.Visible);
            AdifHandlerWorker.RunWorkerAsync();
        }
        
        private void AdifHandlerWorker_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            // Whatever got past the per-file catch - the reload at the end of the import runs outside it,
            // and that is the very phase where a big log is most likely to run out of room. It gets the
            // same report rather than the one-line "Import failed." it used to get.
            if (e.Error != null)
            {
                ToggleUploadProgress(Visibility.Hidden);
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
            UpdateNumOfQSOs();
            UpdateLotwMenuCount();
            UpdateQrzMenuCount();

            // ONE report, whatever happened. It used to be either/or: a single record the database
            // refused replaced the whole summary with "N QSO(s) failed to import. Check the file format
            // and try again." - so an operator who imported 28,000 QSOs and lost 3 was told nothing
            // about the 28,000, and nothing about WHICH 3 either. Both halves are always said now, and
            // the ones that did not make it are named in a file on the Desktop.
            if (result.ImportedQsoCount > 0 || result.CompletedQsoCount > 0 || result.RejectedCount > 0)
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

            TB_Comment.Text = "";
            UpdateNumOfQSOs();

            OpenFixerAfterImport(result);
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
            ToggleUploadProgress(Visibility.Visible);
            UploadProgress = e.UserState as string ?? (e.ProgressPercentage.ToString() + "%");
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

            // No list of country disagreements and no list of contacts that count towards no country:
            // both were this code judging a QSO, and the Log Fixer is the one authority on that. It opens
            // by itself when the import finishes. What is gathered here is only what the import alone
            // knows - what could not be stored, and what had to be worked out.
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
            try
            {
                lock (_syncLock)
                {
                    logName = dal.GetLogName(dal.ActiveLogId);
                    qsosAlreadyInLog = dal.GetQsoCountForLog(dal.ActiveLogId);
                }
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }

            foreach (var filename in files)
            {
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
                        this.Dispatcher.Invoke(() =>
                            HolyMessageBox.ShowWarning($"No QSOs were taken from:\n{filename}\n\n{why}", "Import Warning", this));
                        continue;
                    }

                    // If the file's station callsign(s) differ from the current station callsign, warn
                    // the user with a clear prompt centered on the program window and let them approve
                    // or cancel importing this file.
                    if (!string.IsNullOrWhiteSpace(myCallsign))
                    {
                        List<string> fileCalls = rawQSOList
                            .Select(q => (q.MyCall ?? string.Empty).Trim())
                            .Where(s => s.Length > 0)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();

                        bool differentCall = fileCalls.Any(c => !CallsignIdentity.Same(c, myCallsign));
                        if (differentCall)
                        {
                            string fileName = System.IO.Path.GetFileName(filename);
                            string callsInFile = fileCalls.Count > 0 ? string.Join(", ", fileCalls) : "(none)";
                            bool approved = this.Dispatcher.Invoke(() =>
                                HolyMessageBox.ShowConfirm(
                                    "The ADIF file \"" + fileName + "\" contains QSOs logged under a different callsign than your current station callsign.\n\n" +
                                    "Callsign(s) in the file:  " + callsInFile + "\n" +
                                    "Your current station callsign:  " + myCallsign.Trim() + "\n\n" +
                                    "Do you want to import these QSOs into your log anyway?",
                                    "Different callsign in ADIF file", HolyMsgType.Warning, this));

                            if (!approved)
                            {
                                // User declined importing this file's QSOs.
                                continue;
                            }
                        }
                    }

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
                    _importPhase = $"checking which of the file's {count:N0} QSOs this log already has";
                    AdifHandlerWorker.ReportProgress(lastReportedPercent, "Checking for QSOs already in this log");
                    int completedThisFile = 0, ambiguousThisFile = 0;
                    lock (_syncLock)
                    {
                        rawQSOList = dal.CompleteExistingQsos(rawQSOList, dal.ActiveLogId,
                                                              out completedThisFile, out ambiguousThisFile,
                                                              null, mergeFilled, mergeAmbiguous);
                    }
                    completedQso += completedThisFile;
                    ambiguousQso += ambiguousThisFile;
                    count = rawQSOList.Count;   // what is left is what actually gets inserted

                    for (int i = 0; i < count; i += importBatchSize)
                    {
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
            }

            if (lastReportedPercent < savePhaseEndPercent)
            {
                lastReportedPercent = savePhaseEndPercent;
                AdifHandlerWorker.ReportProgress(lastReportedPercent, $"Saving to log {savePhaseEndPercent}%");
            }

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
            // ONLY WHEN THE IMPORT ITSELF HAS SOMETHING TO SAY. An import that stored everything as it
            // stood, worked nothing out and changed no existing QSO writes no file - there is nothing to
            // record. Whether the QSOs are any GOOD is a different question and a different report,
            // written by the Log Fixer.
            //
            // completedQso is in this test and was missing from it for one build - so a MERGE, which does
            // nothing but complete QSOs already stored, quietly filled fields in the operator's log and
            // wrote no report at all. That is the single case where a report matters most.
            if (rejects.Count > 0 || filledIn.Count > 0 || completedQso > 0 || ambiguousQso > 0)
                WriteImportReport(rejects, filledIn, mergeFilled, mergeAmbiguous,
                                  importedQsoCount, completedQso, ambiguousQso,
                                  out reportPath, out rejectsAdifPath);

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

        private void WriteImportReport(List<ImportReject> rejects, List<HolyLogParser.FilledField> filled,
                                       List<DataAccess.MergeNote> mergeFilled,
                                       List<DataAccess.MergeNote> mergeAmbiguous,
                                       int imported, int completed, int ambiguous,
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

                var sb = new StringBuilder();
                sb.AppendLine("HolyLogger — import report");
                sb.AppendLine(DateTime.Now.ToString("dddd d MMMM yyyy, HH:mm"));
                sb.AppendLine();
                sb.AppendLine($"Added to the log         : {imported:N0}");
                if (completed > 0) sb.AppendLine($"Already here, filled in  : {completed:N0}");
                if (ambiguous > 0) sb.AppendLine($"Already here, too alike  : {ambiguous:N0}");
                sb.AppendLine($"NOT stored               : {rejects.Count:N0}");
                sb.AppendLine();

                if (rejects.Count > 0)
                {
                    sb.AppendLine("────────────────────────────────────────────────────────────────────");
                    sb.AppendLine("QSOs THAT DID NOT MAKE IT");
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
                    sb.AppendLine("  " + "CALL".PadRight(12) + " " + "DATE".PadRight(10) + " " + "TIME".PadRight(6)
                                  + " " + "BAND".PadRight(6) + " " + "MODE".PadRight(6) + "  WHY IT WAS NOT STORED");
                    sb.AppendLine();

                    foreach (var r in rejects)
                    {
                        string where = r.Number > 0 ? $"record {r.Number:N0}" : "record";
                        sb.AppendLine($"  {r.Describe()}  {r.Reason}");
                        sb.AppendLine($"        in {r.FileName}, {where}");
                    }
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
                    foreach (var f in filled)
                        sb.AppendLine($"  {(string.IsNullOrWhiteSpace(f.Call) ? "—" : f.Call).PadRight(12)} "
                                      + $"{f.Field} = {f.Value}   (from {f.From})");
                    sb.AppendLine();
                }

                // ── WHAT WAS CHANGED IN QSOs YOU ALREADY HAD ────────────────────────────────────────
                //
                // The one part of an import that alters contacts already in the log. Everything else
                // either adds a QSO or turns one away; this writes into stored ones, and afterwards they
                // look exactly as though they always held the values. Named field by field, with what
                // went in, because that is the only chance anyone has to spot a bad file writing over
                // good QSOs - and the only record of it that will ever exist.
                if (mergeFilled != null && mergeFilled.Count > 0)
                {
                    sb.AppendLine("────────────────────────────────────────────────────────────────────");
                    sb.AppendLine($"COMPLETED FROM THE FILE ({completed:N0})");
                    sb.AppendLine("────────────────────────────────────────────────────────────────────");
                    sb.AppendLine();
                    sb.AppendLine("These QSOs were already in your log. The file held something in a field");
                    sb.AppendLine("that was EMPTY here, so it was filled in. Nothing that already had a");
                    sb.AppendLine("value was touched, and no QSO was added for these.");
                    sb.AppendLine();

                    int shown = 0;
                    foreach (var m in mergeFilled)
                    {
                        if (shown >= MaxReportRows)
                        {
                            sb.AppendLine($"  … and {mergeFilled.Count - shown:N0} more, not listed one by one.");
                            break;
                        }
                        shown++;
                        sb.AppendLine($"  {(string.IsNullOrWhiteSpace(m.Call) ? "—" : m.Call).PadRight(12)} "
                                      + $"{FormatAdifDate(m.Date).PadRight(11)}{m.Time.PadRight(7)}"
                                      + $"{m.Band.PadRight(7)}{m.Mode}");
                        foreach (var f in m.Fields)
                            sb.AppendLine($"        {f.Key} = {f.Value}");
                    }

                    if (mergeFilled.Count < completed)
                        sb.AppendLine($"  ({completed - mergeFilled.Count:N0} more were matched and needed nothing filling in.)");
                    sb.AppendLine();
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

                    int shown = 0;
                    foreach (var m in mergeAmbiguous)
                    {
                        if (shown >= MaxReportRows)
                        {
                            sb.AppendLine($"  … and {mergeAmbiguous.Count - shown:N0} more, not listed one by one.");
                            break;
                        }
                        shown++;
                        sb.AppendLine($"  {(string.IsNullOrWhiteSpace(m.Call) ? "—" : m.Call).PadRight(12)} "
                                      + $"{FormatAdifDate(m.Date).PadRight(11)}{m.Time.PadRight(7)}"
                                      + $"{m.Band.PadRight(7)}{m.Mode}");
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



