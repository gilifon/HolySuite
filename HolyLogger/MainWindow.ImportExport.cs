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
            public ObservableCollection<QSO> RefreshedQsos { get; set; }
        }

        List<string> ImportFileQ = new List<string>();

        // Reusable ADIF export of a given QSO list (used by the File menu and by View Logs per-log).
        public void ExportQsosToAdif(System.Collections.ObjectModel.ObservableCollection<QSO> qsos, Window owner)
        {
            string adif = Services.GenerateAdif(qsos, Contests.ContestService.Active?.CabrilloName);
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

        // Reusable Cabrillo export of a given QSO list.
        public void ExportQsosToCabrillo(System.Collections.ObjectModel.ObservableCollection<QSO> qsos, Window owner)
        {
            Contester c = new Contester
            {
                Callsign = Properties.Settings.Default.PersonalInfoCallsign,
                Category_Mode = Properties.Settings.Default.selectedMode,
                Category_Operator = Properties.Settings.Default.selectedOperator,
                Category_Power = Properties.Settings.Default.selectedPower,
                Category_Band = Properties.Settings.Default.selectedBand,
                Category_Overlay = Properties.Settings.Default.selectedOverlay,
                Contest = Properties.Settings.Default.selectedEvent,
                Email = Properties.Settings.Default.PersonalInfoEmail,
                Grid = Properties.Settings.Default.my_locator,
                Name = Properties.Settings.Default.PersonalInfoName,
                Soapbox = "HolyLogger",
            };
            string cabrillo = Services.GenerateCabrillo(qsos, c);
            var save = new SaveFileDialog { Filter = "Text File|*.txt|Cabrillo File|*.cbr|Log File|*.log", Title = "Export Cabrillo" };
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
        

        private void ImportAdifMenuItem_Click(object sender, RoutedEventArgs e)
        {
            // Offer to save an in-progress new QSO before an import reloads the log.
            GuardUnsavedQso("import the ADIF file");

            //CultureInfo provider = CultureInfo.InvariantCulture;
            OpenFileDialog openFileDialog = new OpenFileDialog();
            openFileDialog.Filter = "ADIF files (*.adi)|*.adi";
            

            if (openFileDialog.ShowDialog() == true)
            {
                // A log already exists: let the user choose to MERGE (add to it) or REPLACE it. An
                // empty log is unambiguous, so no prompt is needed.
                int existing = 0;
                try { if (dal != null) existing = dal.GetQsoCount(); }
                catch { existing = 0; }

                if (existing > 0)
                {
                    ImportLogChoice choice = AskImportMergeOrReplace(existing);
                    if (choice == ImportLogChoice.Cancel)
                        return;
                    if (choice == ImportLogChoice.Replace && !BackupAndClearLogForReplace())
                        return; // backup cancelled or failed -> abort; the log is left untouched
                }

                ImportFileQ.Add(openFileDialog.FileName);
                StartAdifImportWorker();
            }
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
                FontSize = 15,
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
                var lbl = new TextBlock { FontSize = 14, VerticalAlignment = VerticalAlignment.Top };
                lbl.Inlines.Add(new System.Windows.Documents.Run(label) { FontWeight = FontWeights.Bold });
                lbl.Inlines.Add(new System.Windows.Documents.Run(" — "));
                DockPanel.SetDock(lbl, Dock.Left);
                row.Children.Add(lbl);
                row.Children.Add(new TextBlock { Text = desc, TextWrapping = TextWrapping.Wrap, FontSize = 14 });
                return row;
            }

            root.Children.Add(MakeOption("Merge", "add the file's QSOs to your existing log.", new Thickness(0, 0, 0, 12)));
            root.Children.Add(MakeOption("Replace", "first save a backup of your current log to a file you choose, then clear the log and import the file.", new Thickness(0, 0, 0, 34)));

            var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            Button MakeButton(string text)
            {
                return new Button { Content = text, MinWidth = 90, Margin = new Thickness(6, 0, 6, 0), Padding = new Thickness(12, 5, 12, 5), FontSize = 14 };
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
            if (e.Error != null)
            {
                ToggleUploadProgress(Visibility.Hidden);
                HolyMessageBox.ShowError($"Import failed.\n\n{e.Error.Message}", "Import Error", this);
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

            if (result.FaultyQso > 0)
            {
                HolyMessageBox.ShowWarning($"{result.FaultyQso} QSO(s) failed to import. Check the file format and try again.", "Import Complete with Errors", this);
            }
            else
            {
                if (result.ImportedQsoCount > 0)
                {
                    int totalQsos = result.RefreshedQsos != null ? result.RefreshedQsos.Count : dal.GetQsoCount();
                    HolyMessageBox.ShowSuccess($"Import completed successfully!\nImported QSOs: {result.ImportedQsoCount}\nTotal QSOs in log: {totalQsos}", "Import Complete", this);
                }
            }
            TB_Comment.Text = "";
            UpdateNumOfQSOs();
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
            const int importBatchSize = 500;
            int lastReportedPercent = 0;
            const int readPhasePercent = 3;
            const int parsePhaseEndPercent = 78;
            const int savePhaseStartPercent = 79;
            const int savePhaseEndPercent = 95;
            const int refreshPhaseStartPercent = 96;
            const int refreshPhaseEndPercent = 100;

            foreach (var filename in files)
            {
                try
                {
                    lastReportedPercent = 1;
                    AdifHandlerWorker.ReportProgress(lastReportedPercent, "Preparing import 1%");

                    if (!File.Exists(filename))
                    {
                        this.Dispatcher.Invoke(() =>
                            HolyMessageBox.ShowError($"File not found:\n{filename}", "Import Error", this));
                        continue;
                    }

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

                    parser.Parse(parseProgress =>
                    {
                        int percent = readPhasePercent + (int)Math.Floor((parseProgress * (parsePhaseEndPercent - readPhasePercent)) / 100.0);
                        if (percent > lastReportedPercent)
                        {
                            lastReportedPercent = percent;
                            AdifHandlerWorker.ReportProgress(percent, $"Parsing ADIF {parseProgress}%");
                        }
                    });
                    List<QSO> rawQSOList = parser.GetRawQSO();
                    int count = rawQSOList.Count;

                    if (count == 0)
                    {
                        this.Dispatcher.Invoke(() =>
                            HolyMessageBox.ShowWarning($"No QSOs found in file:\n{filename}\n\nThe file may be in an unsupported format or empty.", "Import Warning", this));
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

                        bool differentCall = fileCalls.Any(c => !string.Equals(c, myCallsign.Trim(), StringComparison.OrdinalIgnoreCase));
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

                    for (int i = 0; i < count; i += importBatchSize)
                    {
                        List<QSO> batch = rawQSOList.Skip(i).Take(importBatchSize).ToList();
                        int batchFaulty;
                        int batchStartIndex = i;
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
                            });
                        }

                        faultyQSO += batchFaulty;
                        importedQsoCount += batch.Count - batchFaulty;
                    }
                }
                catch (Exception ex)
                {
                    string failedFile = filename;
                    string errorMsg = $"Failed to load file:\n{failedFile}\n\nError: {ex.Message}";
                    if (ex.InnerException != null)
                    {
                        errorMsg += $"\n\nDetails: {ex.InnerException.Message}";
                    }
                    this.Dispatcher.Invoke(() =>
                        HolyMessageBox.ShowError(errorMsg, "Import Error", this));
                }
            }

            if (lastReportedPercent < savePhaseEndPercent)
            {
                lastReportedPercent = savePhaseEndPercent;
                AdifHandlerWorker.ReportProgress(lastReportedPercent, $"Saving to log {savePhaseEndPercent}%");
            }

            ObservableCollection<QSO> refreshedQsos;
            lock (_syncLock)
            {
                refreshedQsos = dal.GetAllQSOs(refreshProgress =>
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

            e.Result = new AdifImportResult
            {
                FaultyQso = faultyQSO,
                ImportedQsoCount = importedQsoCount,
                RefreshedQsos = refreshedQsos
            };
            this.Dispatcher.Invoke(() => ImportFileQ.Clear());
        }

        private void ExportMenuItem_Click(object sender, RoutedEventArgs e)
        {
            // Export the ACTIVE log only (the "(Active Log)" menu label). Uses the same helper /
            // save dialog as the View Logs window's Export ADIF button, so behaviour is identical.
            ExportQsosToAdif(dal.GetQSOsForLog(dal.ActiveLogId), this);
        }

        private void ExportCabrilloMenuItem_Click(object sender, RoutedEventArgs e)
        {
            // Export the ACTIVE log only (the "(Active Log)" menu label). Uses the same helper /
            // save dialog as the View Logs window's Export Cabrillo button.
            ExportQsosToCabrillo(dal.GetQSOsForLog(dal.ActiveLogId), this);
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
                c.Soapbox = "HolyLogger";

                //generate cabrillo
                string cabrillo = Services.GenerateCabrillo(dal.GetAllQSOs(), c);

                //set multipart
                var formData = new MultipartFormDataContent();
                var filename = callsign + "_" + DateTime.UtcNow.ToString("yyyyMMdd") + ".txt";
                formData.Add(new StringContent(cabrillo), "file", filename);

                c.filename = filename;
                c.timestamp = DateTime.UtcNow.Ticks.ToString();

                //post file
                var response = await _sharedHttpClient.PostAsync("https://tools.iarc.org/iarc/Server/ftp.php", formData);

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
                        response = await _sharedHttpClient.PostAsync("https://tools.iarc.org/iarc/Server/upload_log.php", formData);

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

        private void parseAdif()
        {
            try
            {
                string adif = Services.GenerateAdif(dal.GetAllQSOs());
                _holyLogParser = new HolyLogParser(adif, (HolyLogParser.IsIsraeliStation(TB_MyCallsign.Text)) ? HolyLogParser.Operator.Israeli : HolyLogParser.Operator.Foreign, Properties.Settings.Default.IsParseDuplicates, Properties.Settings.Default.IsParseWARC);
                _holyLogParser.Parse();
            }
            catch (Exception e)
            {
                HolyMessageBox.ShowError("Parsing failed.", "HolyLogger", this);
            }
        }

        // ── Autosave ──────────────────────────────────────────────────────────────

        private static readonly string AutosaveDir =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "autosave");

        private void SaveAutosnapshot()
        {
            try
            {
                if (dal == null) return;
                var qsos = dal.GetAllQSOs();
                if (qsos == null || qsos.Count == 0) return;

                Directory.CreateDirectory(AutosaveDir);
                string filename = Path.Combine(AutosaveDir,
                    "autosave_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".adi");
                // PROPOSED contest_id tag — autosave backup is tagged with the active contest.
                File.WriteAllText(filename, Services.GenerateAdif(qsos, Contests.ContestService.Active?.CabrilloName), Encoding.UTF8);

                PruneAutosaves(AutosaveDir);
            }
            catch (System.Exception swallowed) { Log.Swallow(swallowed); }
        }

        private static void PruneAutosaves(string dir)
        {
            try
            {
                var files = Directory.GetFiles(dir, "autosave_*.adi")
                    .Select(f => new FileInfo(f))
                    .OrderByDescending(f => f.LastWriteTime)
                    .ToList();

                if (files.Count <= 5) return;

                var cutoff = DateTime.Now.AddDays(-10);
                foreach (var f in files.Skip(5))
                {
                    if (f.LastWriteTime < cutoff)
                        try { f.Delete(); } catch (System.Exception swallowed) { Log.Swallow(swallowed); }
                }
            }
            catch (System.Exception swallowed) { Log.Swallow(swallowed); }
        }

        private void ImportAutosaveMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Import Autosaved Log",
                Filter = "ADIF files (*.adi;*.adif)|*.adi;*.adif|All files (*.*)|*.*",
                FilterIndex = 1
            };
            if (Directory.Exists(AutosaveDir))
                dlg.InitialDirectory = AutosaveDir;

            if (dlg.ShowDialog() != true) return;

            int existing = 0;
            try { if (dal != null) existing = dal.GetQsoCount(); } catch (System.Exception swallowed) { Log.Swallow(swallowed); }

            if (existing > 0)
            {
                ImportLogChoice choice = AskImportMergeOrReplace(existing);
                if (choice == ImportLogChoice.Cancel) return;

                if (choice == ImportLogChoice.Replace)
                {
                    // This is already a restore from backup, so no second backup is needed.
                    Properties.Settings.Default.RecentQSOCounter = 0;
                    Qsos.CollectionChanged -= Qsos_CollectionChanged;
                    Qsos.Clear();
                    Qsos.CollectionChanged += Qsos_CollectionChanged;
                    dal.DeleteAll();
                    ClearBtn_Click(null, null);
                    UpdateNumOfQSOs();
                    UpdateEqslQueueIndicator();
                    UpdateQrzMenuCount();
                }
            }

            ImportFileQ.Clear();
            ImportFileQ.Add(dlg.FileName);
            StartAdifImportWorker();
        }
    }
}
