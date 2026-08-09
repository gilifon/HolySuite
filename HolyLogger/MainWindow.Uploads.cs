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
    // Log-upload services glue: eQSL / LoTW (TQSL) / QRZ Logbook queues, menu counts, upload-on-exit flow.
    // Move-only split from MainWindow.xaml.cs; no behavior change.
    public partial class MainWindow
    {

        // Shared client for eQSL uploads (a single long-lived HttpClient avoids socket exhaustion).
        private static readonly System.Net.Http.HttpClient _eqslHttp =
            new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(25) };

        // Guarantees only one upload operation runs at a time, so the on-save auto-upload and the
        // manual "Send" pass can never double-send the same QSO.
        private readonly System.Threading.SemaphoreSlim _eqslPumpLock = new System.Threading.SemaphoreSlim(1, 1);

        // Builds the eQSL ImportADIF upload URL for a single QSO.
        private static string BuildEqslUrl(QSO qso, string user, string pwd, string nickname)
        {
            string adif = BuildEqslAdif(qso, nickname);
            return "https://www.eQSL.cc/qslcard/ImportADIF.cfm"
                + "?EQSL_USER=" + Uri.EscapeDataString(user)
                + "&EQSL_PSWD=" + Uri.EscapeDataString(pwd)
                + "&ADIFData=" + Uri.EscapeDataString(adif);
        }

        // Auto-uploads a single just-logged QSO to the eQSL account that belongs to the callsign it
        // was logged under. On success it is marked sent; if it can't be confirmed (offline / auth /
        // error) it stays pending and the "!" badge appears so the user can send it manually later.
        // Does nothing unless "Automatically upload each QSO" is on AND that callsign has an account
        // with credentials. The backlog is NEVER flushed here.
        private async System.Threading.Tasks.Task SendOneQsoToEqsl(QSO qso)
        {
            // This runs as fire-and-forget (_ = SendOneQsoToEqsl(...)). Any exception here (e.g. a DB
            // error from GetEqslAccount/SetEqslStatus) would otherwise be an unobserved task exception,
            // so the whole body is guarded. The QSO simply stays pending if anything goes wrong.
            try
            {
                if (qso == null || dal == null) return;
                if (!Properties.Settings.Default.EqslAutoUpload) return;

                EqslAccount acct = dal.GetEqslAccount(qso.MyCall);
                if (acct == null || string.IsNullOrWhiteSpace(acct.Username) || string.IsNullOrWhiteSpace(acct.Password))
                    return; // no eQSL account configured for this callsign -> leave it pending

                // If a send pass is already running, leave this QSO pending; it will be picked up later.
                if (!await _eqslPumpLock.WaitAsync(0)) return;
                try
                {
                    string url = BuildEqslUrl(qso, acct.Username, acct.Password, null);

                    int outcome;
                    try
                    {
                        string body = await _eqslHttp.GetStringAsync(url);
                        outcome = ClassifyEqslResponse(body);
                    }
                    catch
                    {
                        outcome = 0; // offline / timeout -> leave pending
                    }

                    if (outcome == 1) dal.SetEqslStatus(qso.id, 1);
                    // Anything short of an explicit "added" confirmation (outcome 2 rejected, or
                    // outcome 0 unknown/offline) leaves the QSO pending, so it stays in the queue and
                    // is retried. A QSO only leaves the queue when eQSL confirms it received it.

                    UpdateEqslQueueIndicator();
                }
                finally
                {
                    _eqslPumpLock.Release();
                }
            }
            catch
            {
                // Auto-upload must never crash the app; the QSO remains pending for a later retry.
            }
        }

        // Manually uploads every pending QSO that has a configured account, routing each to the eQSL
        // account of the callsign it was logged under. Marks each sent or rejected from eQSL's reply,
        // and leaves anything that can't be confirmed sent as pending so nothing is ever lost. Called
        // only from the queue window's "Send" button. Returns the number of QSOs successfully uploaded
        // in this pass. Must run on the UI thread (touches DB + UI).
        private async System.Threading.Tasks.Task<int> PumpEqslQueue(bool force = false, UploadProgressWindow progressWindow = null)
        {
            if (dal == null) return 0;
            if (!Properties.Settings.Default.UseEqslService) return 0;   // service switched off in Options

            // On a forced exit-upload, wait up to 30 s for any concurrent pump to finish rather
            // than silently skipping. For normal fire-and-forget calls, give up immediately.
            var timeout = force ? TimeSpan.FromSeconds(30) : TimeSpan.Zero;
            if (!await _eqslPumpLock.WaitAsync(timeout)) return 0;
            try
            {
                // Only QSOs whose callsign has an account come back here.
                System.Collections.Generic.List<QSO> pending = dal.GetPendingEqslQsos();
                int sentCount = 0;

                if (progressWindow != null)
                {
                    if (pending.Count > 0)
                        progressWindow.StartService("eQSL", pending.Count);
                    else
                        progressWindow.SkipService("eQSL", "nothing to upload — queue is empty");
                }

                // Load the accounts once into a callsign-keyed map (case-insensitive, matching the DB
                // NOCASE collation) instead of querying GetEqslAccount per QSO.
                var accounts = new System.Collections.Generic.Dictionary<string, EqslAccount>(StringComparer.OrdinalIgnoreCase);
                foreach (var a in dal.GetEqslAccounts())
                    if (!string.IsNullOrWhiteSpace(a.Callsign)) accounts[a.Callsign.Trim()] = a;

                foreach (var qso in pending)
                {
                    EqslAccount acct = null;
                    string myCall = (qso.MyCall ?? string.Empty).Trim();
                    if (myCall.Length > 0) accounts.TryGetValue(myCall, out acct);
                    if (acct == null || string.IsNullOrWhiteSpace(acct.Username) || string.IsNullOrWhiteSpace(acct.Password))
                        continue; // shouldn't happen (filtered), but skip defensively

                    string url = BuildEqslUrl(qso, acct.Username, acct.Password, null);
                    int outcome;
                    bool networkError = false;
                    try
                    {
                        // No ConfigureAwait(false): resume on the UI thread so DB/UI stay single-threaded.
                        string body = await _eqslHttp.GetStringAsync(url);
                        outcome = ClassifyEqslResponse(body);
                    }
                    catch
                    {
                        networkError = true; // offline / timeout
                        outcome = 0;
                    }

                    if (networkError)
                        break; // no internet -> stop; everything else stays pending for next time

                    if (outcome == 1)        // accepted by eQSL
                    {
                        dal.SetEqslStatus(qso.id, 1);
                        sentCount++;
                        progressWindow?.ReportQso(qso.DXCall, qso.Band, qso.Mode, true);
                    }
                    else if (outcome == 2)   // rejected by eQSL -> keep pending so it stays in the queue
                    {
                        // Not confirmed received -> leave the QSO pending (stays in the queue) and just
                        // report it as not-sent for this run. Only an explicit "added" clears it.
                        progressWindow?.ReportQso(qso.DXCall, qso.Band, qso.Mode, false);
                    }
                    // outcome 0 (unrecognized reply, e.g. one account's auth failed) -> leave this QSO
                    // pending and move on to the next, so one bad account can't block the others.

                    UpdateEqslQueueIndicator();
                }

                UpdateEqslQueueIndicator();
                return sentCount;
            }
            finally
            {
                _eqslPumpLock.Release();
            }
        }

        // Interprets eQSL's ImportADIF reply. Deliberately conservative: only an explicit success is
        // treated as "sent"; an explicit bad-record is treated as "rejected"; anything else (auth
        // failure, maintenance page, unrecognized text) leaves the QSO pending so it is never lost.
        // Returns 1 = sent, 2 = rejected, 0 = unknown (keep pending). May need tuning once we have a
        // real eQSL response sample to look at.
        private static int ClassifyEqslResponse(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return 0;
            string text = body.ToLowerInvariant();

            // eQSL reports e.g. "Result: 1 out of 1 records added".
            var m = System.Text.RegularExpressions.Regex.Match(text, @"(\d+)\s+out\s+of\s+(\d+)\s+record");
            if (m.Success)
            {
                int added = 0;
                int.TryParse(m.Groups[1].Value, out added);
                if (added >= 1) return 1;
                // 0 added: a duplicate already on eQSL counts as done; a real bad record is rejected.
                if (text.Contains("duplicate") || text.Contains("already")) return 1;
                if (text.Contains("bad record") || text.Contains("rejected") || text.Contains("error")) return 2;
                return 0;
            }

            if (text.Contains("bad record") || text.Contains("rejected")) return 2;
            return 0;
        }

        // Builds a one-record ADIF for eQSL by reusing the app's ADIF generator and (optionally)
        // injecting the QTH nickname tag so eQSL matches the upload to the right QTH profile.
        private static string BuildEqslAdif(QSO qso, string qthNickname)
        {
            string adif = Services.GenerateAdif(new System.Collections.Generic.List<QSO> { qso });
            if (!string.IsNullOrWhiteSpace(qthNickname))
            {
                string tag = string.Format("<app_eqsl_qth_nickname:{0}>{1}", qthNickname.Length, qthNickname);
                int idx = adif.LastIndexOf("<eor>", StringComparison.OrdinalIgnoreCase);
                if (idx >= 0) adif = adif.Insert(idx, tag);
            }
            return adif;
        }

        // Silently uploads every QSO still pending for QRZ (status 0), oldest first. Runs at startup so
        // anything that could not be pushed while offline is retried automatically. Stops on the first
        // network error (everything else stays pending); a per-record rejection is marked and skipped so
        // one bad record can't block the queue. Never throws.
        // force=true bypasses the QrzPushEnabled guard — used by the on-exit upload when the user
        // explicitly confirmed the upload even though real-time auto-push is turned off.
        private async System.Threading.Tasks.Task PumpQrzQueue(bool force = false, UploadProgressWindow progressWindow = null)
        {
            try
            {
                if (dal == null) return;
                if (!Properties.Settings.Default.UseQrzLogbook) return;   // service switched off in Options
                if (!force && !QrzPushEnabled) return;

                // When forced (exit-upload), wait up to 30 s for a concurrent pump to finish.
                // For regular fire-and-forget calls give up immediately so the caller is not blocked.
                var lockTimeout = force ? TimeSpan.FromSeconds(30) : TimeSpan.Zero;
                if (!await _qrzPumpLock.WaitAsync(lockTimeout)) return;
                try
                {
                    string key = Properties.Settings.Default.qrz_api_key.Trim();
                    System.Collections.Generic.List<QSO> pending = dal.GetPendingQrzQsos();
                    _lastQrzFailReasons.Clear();
                    _lastQrzHadNetworkError = false;

                    if (progressWindow != null)
                    {
                        if (pending.Count > 0)
                            progressWindow.StartService("QRZ Logbook", pending.Count);
                        else
                            progressWindow.SkipService("QRZ Logbook", "nothing to upload — queue is empty");
                    }

                    foreach (var qso in pending)
                    {
                        QrzLogbookResult r = await QrzLogbookService.InsertAsync(key, BuildQrzAdif(qso));

                        if (r.NetworkError)
                        {
                            _lastQrzHadNetworkError = true;
                            break;   // offline -> stop; the rest stays pending for next time
                        }

                        if (r.Ok)
                        {
                            dal.SetQrzStatus(qso.id, 1, r.LogId);
                            progressWindow?.ReportQso(qso.DXCall, qso.Band, qso.Mode, true);
                        }
                        else if (r.IsPermanentFailure)
                        {
                            // Rejected by QRZ -> keep the QSO pending (stays in the queue) and report
                            // it as not-sent for this run. Only a confirmed upload clears it. Remember
                            // WHY so the caller can tell the user (duplicate, wrong call, ...).
                            if (!string.IsNullOrWhiteSpace(r.Reason))
                                _lastQrzFailReasons.Add(r.Reason.Trim());
                            progressWindow?.ReportQso(qso.DXCall, qso.Band, qso.Mode, false);
                        }
                    }
                }
                finally
                {
                    _qrzPumpLock.Release();
                }
                UpdateQrzMenuCount();
            }
            catch
            {
                // Best effort; anything not confirmed sent simply stays pending.
            }
        }

        // Refreshes everything that reflects the eQSL queue size: the Tools-menu item (grayed when
        // empty, with the count in its header). Safe to call often.
        private void UpdateEqslQueueIndicator()
        {
            int pending = 0;
            // Counts not-yet-sent QSOs whose callsign is in the eQSL table (the opt-in list). A
            // callsign that isn't in the table is ignored.
            try { if (dal != null) pending = dal.GetPendingEqslCount(); }
            catch { pending = 0; }

            if (SendQueueToEqslMenuItem != null)
            {
                // Build the header with just the word "eQSL" in bold; always append the count
                // (including (0)) so the queue state is never ambiguous.
                var header = new System.Windows.Controls.TextBlock();
                header.Inlines.Add(new System.Windows.Documents.Run("Upload Queue to "));
                header.Inlines.Add(new System.Windows.Documents.Run("eQSL") { FontWeight = System.Windows.FontWeights.Bold });
                header.Inlines.Add(new System.Windows.Documents.Run("  (" + pending + ")"));
                SendQueueToEqslMenuItem.Header = header;
            }


            // Keep an open queue window in sync too (e.g. a QSO was deleted from the log behind it).
            if (_eqslQueueWindow != null)
                _eqslQueueWindow.RefreshList();
        }

        private async void SendQueueToEqslMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (!RequireActiveLog("upload")) return;
            int before = dal?.GetPendingEqslCount() ?? 0;
            if (before == 0)
            {
                HolyMessageBox.Show("The eQSL queue is empty. Nothing to upload.\n\n(Only QSOs whose station callsign has an eQSL account are queued.)",
                    "eQSL", HolyMsgType.Info, this);
                return;
            }

            SendQueueToEqslMenuItem.IsEnabled = false;
            this.IsEnabled = false;   // owned progress window stays live
            var progressWindow = new UploadProgressWindow { Owner = this };
            progressWindow.Show();
            try
            {
                // Progress window shows a ✓/✗ row per QSO. (eQSL has no auto-push gate; force only
                // sets the longer lock-wait so a concurrent pump doesn't make this silently skip.)
                await PumpEqslQueue(force: true, progressWindow);
                progressWindow.ShowComplete();
                UpdateEqslQueueIndicator();
                await progressWindow.WaitForOkAsync();
            }
            finally
            {
                this.IsEnabled = true;
                SendQueueToEqslMenuItem.IsEnabled = true;
            }
        }

        private void UpdateLotwMenuCount()
        {
            try
            {
                int count = dal?.GetPendingLotwQsos()?.Count ?? 0;
                var header = new System.Windows.Controls.TextBlock();
                header.Inlines.Add(new System.Windows.Documents.Run("Upload Queue to "));
                header.Inlines.Add(new System.Windows.Documents.Run("LoTW") { FontWeight = System.Windows.FontWeights.Bold });
                header.Inlines.Add(new System.Windows.Documents.Run("  (" + count + ")"));
                SendQueueToLotwMenuItem.Header = header;
            }
            catch (System.Exception swallowed) { Log.Swallow(swallowed); }
        }

        private async void UploadQueueToQrzMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (!RequireActiveLog("upload")) return;
            string key = (Properties.Settings.Default.qrz_api_key ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                HolyMessageBox.ShowWarning(
                    "QRZ Logbook API key is not configured.\nPlease enter your API key in Options → QRZ Services.",
                    "QRZ Logbook", this);
                return;
            }

            int before = dal?.GetPendingQrzCount() ?? 0;
            if (before == 0)
            {
                HolyMessageBox.Show("The QRZ Logbook queue is empty. Nothing to upload.",
                    "QRZ Logbook", HolyMsgType.Info, this);
                return;
            }

            UploadQueueToQrzMenuItem.IsEnabled = false;
            this.IsEnabled = false;   // block the main window while the upload runs (owned progress window stays live)
            var progressWindow = new UploadProgressWindow { Owner = this };
            progressWindow.Show();
            try
            {
                // force: this is an explicit "upload now" click, so run even when the auto-push
                // checkbox is off (QrzPushEnabled == false). Without force the pump silently no-ops.
                // The progress window shows a ✓/✗ row per QSO as each one is sent.
                await PumpQrzQueue(force: true, progressWindow);

                // Per-QSO rows only show ✓/✗, so add QRZ's reason as a note when something failed.
                if (_lastQrzHadNetworkError)
                    progressWindow.AddNote("Could not reach QRZ.com — check your internet connection. Unsent QSOs stay in the queue.");
                else if (_lastQrzFailReasons.Count > 0)
                    progressWindow.AddNote("QRZ reason: " + string.Join("; ", _lastQrzFailReasons) +
                        "   (\"duplicate\" = already in your logbook; \"wrong station_callsign\" = the QSO's call doesn't match this logbook's call.)");

                progressWindow.ShowComplete();
                UpdateQrzMenuCount();
                await progressWindow.WaitForOkAsync();
            }
            finally
            {
                this.IsEnabled = true;
                UploadQueueToQrzMenuItem.IsEnabled = true;
            }
        }

        private void ClearLotwQueueContextMenuItem_Click(object sender, RoutedEventArgs e)
        {
            int pending = dal?.GetPendingLotwCount() ?? 0;
            if (pending == 0)
            {
                HolyMessageBox.Show("The LoTW queue is already empty.", "Clear LoTW Queue", HolyMsgType.Info, this);
                return;
            }

            bool confirmed = HolyMessageBox.ShowConfirm(
                $"Remove all {pending:N0} QSO(s) from the LoTW upload queue?\n\nThey will no longer be included in the next upload.",
                "Clear LoTW Queue", HolyMsgType.Warning, this);
            if (!confirmed) return;

            dal.ClearLotwQueue();
            UpdateLotwMenuCount();
        }

        private void ClearEqslQueueContextMenuItem_Click(object sender, RoutedEventArgs e)
        {
            int pending = dal?.GetPendingEqslCount() ?? 0;
            if (pending == 0)
            {
                HolyMessageBox.Show("The eQSL queue is already empty.", "Clear eQSL Queue", HolyMsgType.Info, this);
                return;
            }

            bool confirmed = HolyMessageBox.ShowConfirm(
                $"Remove all {pending:N0} QSO(s) from the eQSL upload queue?\n\nThey will no longer be included in the next upload.",
                "Clear eQSL Queue", HolyMsgType.Warning, this);
            if (!confirmed) return;

            dal.ClearEqslQueue();
            UpdateEqslQueueIndicator();
        }

        private void ClearQrzQueueContextMenuItem_Click(object sender, RoutedEventArgs e)
        {
            int pending = dal?.GetPendingQrzCount() ?? 0;
            if (pending == 0)
            {
                HolyMessageBox.Show("The QRZ Logbook queue is already empty.", "Clear QRZ Queue", HolyMsgType.Info, this);
                return;
            }

            bool confirmed = HolyMessageBox.ShowConfirm(
                $"Remove all {pending:N0} QSO(s) from the QRZ Logbook upload queue?\n\nThey will no longer be included in the next upload.",
                "Clear QRZ Queue", HolyMsgType.Warning, this);
            if (!confirmed) return;

            dal.ClearQrzQueue();
            UpdateQrzMenuCount();
        }

        private async void SendQueueToLotwMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (!RequireActiveLog("upload")) return;
            string tqslPath = Properties.Settings.Default.LotwTqslPath?.Trim();
            string password = Properties.Settings.Default.LotwTqslPassword;

            if (string.IsNullOrWhiteSpace(tqslPath) || !System.IO.File.Exists(tqslPath))
            {
                HolyMessageBox.ShowWarning("TQSL executable not found.\nPlease set the correct path in Options → LoTW Upload.", "LoTW Upload", this);
                return;
            }

            var pending = dal.GetPendingLotwQsos();
            if (pending.Count == 0)
            {
                HolyMessageBox.Show("No pending QSOs to upload to LoTW.", "LoTW Upload", HolyMsgType.Info, this);
                return;
            }

            // Resolve each callsign in the queue to its TQSL station location and show the plan.
            var preview = ResolveLotwGroupsPreview(pending, out int resolvable);
            if (resolvable == 0)
            {
                HolyMessageBox.ShowWarning(
                    "None of the pending QSOs can be matched to a TQSL station location:\n\n" +
                    string.Join("\n", preview) +
                    "\n\nCreate the station location(s) in TQSL, then pick them in Options → LoTW Upload.",
                    "LoTW Upload", this);
                return;
            }

            if (!HolyMessageBox.ShowConfirm(
                    $"Upload {resolvable} pending QSO(s) to LoTW?\n\n" + string.Join("\n", preview),
                    "LoTW Upload", HolyMsgType.Warning, this))
                return;

            SendQueueToLotwMenuItem.IsEnabled = false;
            this.IsEnabled = false;   // owned progress window stays live
            var progressWindow = new UploadProgressWindow { Owner = this };
            progressWindow.Show();
            try
            {
                // LoTW signs each callsign group with TQSL as one batch, so the window shows a
                // per-service summary line rather than per-QSO rows.
                await UploadLotwQueueCoreAsync(pending, tqslPath, password, progressWindow);
                progressWindow.ShowComplete();
                await progressWindow.WaitForOkAsync();
            }
            finally
            {
                this.IsEnabled = true;
                SendQueueToLotwMenuItem.IsEnabled = true;
            }
        }

        // Core LoTW queue upload — writes the ADIF, signs+uploads via TQSL, clears the queue on
        // success and reports the result. Shared by the "Upload Queue to LoTW" menu command and the
        // upload-on-exit feature.
        private async Task UploadLotwQueueCoreAsync(List<QSO> pending, string tqslPath, string password, UploadProgressWindow progressWindow = null)
        {
            string adiPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "holylogger_lotw.adi");
            UploadProgressTitle = "LoTW Upload";
            UploadProgress = $"Preparing QSO 0 / {pending.Count:N0}";
            if (progressWindow == null) ToggleUploadProgress(Visibility.Visible);
            else progressWindow.StartService("LoTW", pending.Count);
            var lotwProgress = new Progress<string>(msg => UploadProgress = msg);

            string savedPicks = Properties.Settings.Default.LotwCallsignLocations;

            // Group the queue by station callsign. Each group is signed with the TQSL station
            // location (and therefore the certificate) that belongs to that callsign, so QSOs made
            // under a special-event call are credited to that call, not to the everyday callsign.
            var groups = pending
                .GroupBy(q => (q.MyCall ?? string.Empty).Trim(), StringComparer.OrdinalIgnoreCase)
                .ToList();

            int totalUploaded = 0;                  // QSOs accepted by LoTW (uploaded or duplicate)
            int totalSkippedNoBand = 0;
            var unresolved = new List<string>();    // callsign groups left in the queue (no location)
            var failures = new List<string>();      // callsign groups that errored at TQSL
            var reportSb = new System.Text.StringBuilder();
            reportSb.AppendLine($"LoTW upload report — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            reportSb.AppendLine();

            try
            {
                foreach (var group in groups)
                {
                    string call = group.Key;
                    string callLabel = string.IsNullOrEmpty(call) ? "(no callsign)" : call;
                    var qsos = group.ToList();

                    var choice = LotwStationResolver.Resolve(call, savedPicks);
                    if (string.IsNullOrWhiteSpace(choice.LocationName))
                    {
                        // Cannot sign this callsign — leave its QSOs in the queue and say why.
                        string reason = choice.Ambiguous
                            ? $"{callLabel}: {qsos.Count} QSO(s) — several TQSL locations, choose one in Options → LoTW"
                            : $"{callLabel}: {qsos.Count} QSO(s) — no TQSL certificate / station location";
                        unresolved.Add(reason);
                        reportSb.AppendLine($"=== {callLabel}: SKIPPED — {reason} (left in queue) ===");
                        continue;
                    }

                    string location = choice.LocationName;
                    UploadProgress = $"Preparing {callLabel}: 0 / {qsos.Count:N0}";
                    int skippedNoBand = 0;
                    await Task.Run(() => { skippedNoBand = LotwUploader.WriteAdif(qsos, adiPath, lotwProgress); });
                    totalSkippedNoBand += skippedNoBand;
                    int toSign = qsos.Count - skippedNoBand;
                    if (toSign <= 0)
                    {
                        reportSb.AppendLine($"=== {callLabel} via \"{location}\": no QSOs with band/frequency — skipped ===");
                        continue;
                    }

                    UploadProgress = $"Signing {callLabel}: 0 / {toSign:N0}";
                    LotwUploadResult result;
                    try
                    {
                        result = await LotwUploader.SignAndUploadAsync(
                            tqslPath, location, password, adiPath, lotwProgress, toSign);
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"{callLabel} → \"{location}\": {ex.Message}");
                        reportSb.AppendLine($"=== {callLabel} via \"{location}\": ERROR {ex.Message} (left in queue) ===");
                        continue;
                    }

                    reportSb.AppendLine($"=== {callLabel} via \"{location}\" — TQSL exit {result.ExitCode} ===");
                    reportSb.AppendLine(result.Detail ?? string.Empty);
                    reportSb.AppendLine();

                    if (result.ExitCode == 8)
                    {
                        // TQSL processed nothing (usually a callsign/location mismatch) — leave in queue.
                        failures.Add($"{callLabel}: TQSL processed no QSOs — check the station location matches the callsign");
                    }
                    else
                    {
                        // exit 0 = uploaded, exit 9 = already in LoTW (duplicates). Either way the
                        // QSOs are now in LoTW, so clear this group from the queue.
                        foreach (var q in qsos)
                            if (!string.IsNullOrWhiteSpace(q.Band) || !string.IsNullOrWhiteSpace(q.Freq))
                                dal.SetLotwStatus(q.id, 1);
                        totalUploaded += toSign;
                    }
                }

                if (progressWindow == null) ToggleUploadProgress(Visibility.Hidden);
                UpdateLotwMenuCount();

                // Build the user-facing summary.
                var summary = new System.Text.StringBuilder();
                summary.AppendLine(totalUploaded > 0
                    ? $"{totalUploaded:N0} QSO(s) accepted by LoTW."
                    : "No QSOs were uploaded to LoTW.");
                if (totalSkippedNoBand > 0)
                    summary.AppendLine($"{totalSkippedNoBand:N0} QSO(s) skipped — no band or frequency recorded.");
                if (unresolved.Count > 0)
                {
                    summary.AppendLine("\nLeft in the queue (no matching TQSL location):");
                    foreach (var u in unresolved) summary.AppendLine("  • " + u);
                }
                if (failures.Count > 0)
                {
                    summary.AppendLine("\nNot uploaded:");
                    foreach (var f in failures) summary.AppendLine("  • " + f);
                }

                // Save the report to the Desktop, SUMMARY FIRST.
                //
                // A big upload's per-QSO TQSL output runs to megabytes, and the file is capped from the
                // FRONT - so writing the summary last meant the raw dump ate the cap and the "left in
                // the queue" list was thrown away, the one time somebody needed to read it. Only the
                // TQSL output is truncated now; the summary is always at the top.
                string reportPath = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "lotw_last_upload.txt");
                try
                {
                    string detail = reportSb.ToString();
                    if (detail.Length > 500000) detail = detail.Substring(0, 500000) + "\r\n…(truncated)";
                    var file = new System.Text.StringBuilder();
                    file.AppendLine($"LoTW upload summary — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                    file.AppendLine(new string('=', 70));
                    file.AppendLine(summary.ToString().TrimEnd());
                    file.AppendLine();
                    file.AppendLine(new string('=', 70));
                    file.AppendLine("Full TQSL output");
                    file.AppendLine(new string('=', 70));
                    file.AppendLine();
                    file.Append(detail);
                    System.IO.File.WriteAllText(reportPath, file.ToString(), System.Text.Encoding.UTF8);
                }
                catch (System.Exception swallowed) { Log.Swallow(swallowed); }

                summary.AppendLine("\nThe full TQSL report was saved to lotw_last_upload.txt on your Desktop.");

                bool clean = failures.Count == 0 && unresolved.Count == 0;
                if (progressWindow != null)
                {
                    string line = totalUploaded > 0
                        ? $"{totalUploaded:N0} QSO(s) uploaded to LoTW"
                        : "No QSOs uploaded to LoTW";
                    int leftover = unresolved.Count + failures.Count;
                    if (leftover > 0) line += $" ({leftover} callsign group(s) left in queue)";
                    // Counts are in QSOs so the window's summary line adds up: everything that was
                    // pending and did not reach LoTW (unresolved, failed, or no band/frequency) is a
                    // miss, whatever grouping it was skipped by.
                    progressWindow.ReportBatchResult(
                        line, clean, totalUploaded, Math.Max(0, pending.Count - totalUploaded));

                    // Name the callsigns that did not go, and why, right here in the window. "8 callsign
                    // group(s) left in queue" tells the operator that something is wrong but not what to
                    // do about it - the reasons were already in hand, they were just never shown.
                    foreach (var u in unresolved) progressWindow.AddNote("• " + u);
                    foreach (var f in failures) progressWindow.AddNote("• " + f);
                    if (totalSkippedNoBand > 0)
                        progressWindow.AddNote(
                            $"• {totalSkippedNoBand:N0} QSO(s) skipped — no band or frequency recorded.");
                }
                else if (clean)
                    HolyMessageBox.ShowSuccess(summary.ToString().TrimEnd(), "LoTW Upload", this);
                else
                    HolyMessageBox.ShowWarning(summary.ToString().TrimEnd(), "LoTW Upload", this);
            }
            catch (Exception ex)
            {
                if (progressWindow == null) ToggleUploadProgress(Visibility.Hidden);
                try
                {
                    string logPath = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                        "lotw_upload_error.txt");
                    System.IO.File.WriteAllText(logPath,
                        $"LoTW upload error — {DateTime.Now:yyyy-MM-dd HH:mm:ss}\r\n" +
                        $"Message: {ex.Message}\r\n",
                        System.Text.Encoding.UTF8);
                }
                catch (System.Exception swallowed) { Log.Swallow(swallowed); }
                if (progressWindow != null)
                    progressWindow.ReportBatchResult($"Upload failed: {ex.Message}", false,
                        totalUploaded, Math.Max(0, pending.Count - totalUploaded));
                else
                    HolyMessageBox.ShowError(
                        "LoTW upload failed:\n\n" + ex.Message +
                        "\n\nDetails written to lotw_upload_error.txt on your Desktop.",
                        "LoTW Upload Failed", this);
            }
            finally
            {
                UploadProgressTitle = "";
                UploadProgress = "";
                try { if (System.IO.File.Exists(adiPath)) System.IO.File.Delete(adiPath); } catch (System.Exception swallowed) { Log.Swallow(swallowed); }
            }
        }

        // Builds a per-callsign preview of how the pending LoTW queue will be signed, and reports how
        // many QSOs can actually be resolved to a TQSL station location.
        private List<string> ResolveLotwGroupsPreview(List<QSO> pending, out int resolvableQsos)
        {
            resolvableQsos = 0;
            string savedPicks = Properties.Settings.Default.LotwCallsignLocations;
            var lines = new List<string>();
            foreach (var g in pending.GroupBy(q => (q.MyCall ?? string.Empty).Trim(), StringComparer.OrdinalIgnoreCase))
            {
                string callLabel = string.IsNullOrEmpty(g.Key) ? "(no callsign)" : g.Key;
                int n = g.Count();
                var choice = LotwStationResolver.Resolve(g.Key, savedPicks);
                if (!string.IsNullOrWhiteSpace(choice.LocationName))
                {
                    resolvableQsos += n;
                    lines.Add($"{callLabel}: {n} QSO(s) → \"{choice.LocationName}\"");
                }
                else if (choice.Ambiguous)
                    lines.Add($"{callLabel}: {n} QSO(s) → ⚠ choose a location in Options → LoTW");
                else
                    lines.Add($"{callLabel}: {n} QSO(s) → ⚠ no TQSL certificate/location (stays in queue)");
            }
            return lines;
        }

        private EqslQueueWindow _eqslQueueWindow;

        // Deletes a QSO chosen in an upload-queue window from its LOG (the queue window already confirmed
        // with the user). When the QSO is in the currently-active log, remove it through the Qsos collection
        // so the main grid and all the Qsos_CollectionChanged bookkeeping run (dal.Delete, counts,
        // worked-countries, and the copy-to-log cascade). When it belongs to a different (not-loaded) log,
        // delete it directly and refresh the queue counts/indicators. Deleting the row clears it from every
        // service's queue at once.
        private void DeleteQsoFromQueueWindow(QSO q)
        {
            if (q == null || dal == null) return;
            var inActive = Qsos?.FirstOrDefault(x => x.id == q.id);
            if (inActive != null)
            {
                Qsos.Remove(inActive);   // -> Qsos_CollectionChanged: dal.Delete + refresh everything
            }
            else
            {
                dal.Delete(q.id);        // QSO belongs to a log that isn't loaded in the grid
                UpdateNumOfQSOs();
                UpdateEqslQueueIndicator();
                UpdateLotwMenuCount();
                UpdateQrzMenuCount();
                UpdateClublogMenuCount();
            }
        }

        // Opens (or focuses) a window listing the QSOs still waiting for eQSL.
        private void ShowEqslQueueWindow()
        {
            if (dal == null) return;

            if (_eqslQueueWindow != null)
            {
                _eqslQueueWindow.Activate();
                _eqslQueueWindow.RefreshList();
                return;
            }

            _eqslQueueWindow = new EqslQueueWindow(
                () => dal.GetPendingEqslQsos(),
                () => PumpEqslQueue(),
                "eQSL",
                () => dal.GetDismissedEqslQsos(),
                () => { dal.RequeueAllEqslDismissed(); UpdateEqslQueueIndicator(); },
                q => { dal.SetEqslStatus(q.id, 2); UpdateEqslQueueIndicator(); },
                DeleteQsoFromQueueWindow)
            {
                Owner = this
            };
            _eqslQueueWindow.Closed += (s, ev) => { _eqslQueueWindow = null; UpdateEqslQueueIndicator(); };
            _eqslQueueWindow.Show();
        }

        private void ViewEqslQueueContextMenuItem_Click(object sender, RoutedEventArgs e)
        {
            ShowEqslQueueWindow();
        }

        private EqslQueueWindow _lotwQueueWindow;

        private void ShowLotwQueueWindow()
        {
            if (dal == null) return;

            if (_lotwQueueWindow != null)
            {
                _lotwQueueWindow.Activate();
                _lotwQueueWindow.RefreshList();
                return;
            }

            _lotwQueueWindow = new EqslQueueWindow(
                () => dal.GetPendingLotwQsos(),
                async () =>
                {
                    string tqslPath = Properties.Settings.Default.LotwTqslPath?.Trim();
                    string password = Properties.Settings.Default.LotwTqslPassword;
                    if (string.IsNullOrWhiteSpace(tqslPath) || !System.IO.File.Exists(tqslPath))
                        throw new Exception("TQSL not found. Please configure it in Options → LoTW Upload.");
                    var pending = dal.GetPendingLotwQsos();
                    int before = pending.Count;
                    await UploadLotwQueueCoreAsync(pending, tqslPath, password);
                    int after = dal?.GetPendingLotwCount() ?? 0;
                    UpdateLotwMenuCount();
                    return before - after;
                },
                "LoTW",
                () => dal.GetDismissedLotwQsos(),
                () => { dal.RequeueAllLotwDismissed(); UpdateLotwMenuCount(); },
                q => { dal.SetLotwStatus(q.id, 2); UpdateLotwMenuCount(); },
                DeleteQsoFromQueueWindow)
            {
                Owner = this
            };
            _lotwQueueWindow.Closed += (s, ev) => { _lotwQueueWindow = null; UpdateLotwMenuCount(); };
            _lotwQueueWindow.Show();
        }

        private void ViewLotwQueueContextMenuItem_Click(object sender, RoutedEventArgs e)
        {
            ShowLotwQueueWindow();
        }

        private EqslQueueWindow _qrzQueueWindow;

        private void ShowQrzQueueWindow()
        {
            if (dal == null) return;

            if (_qrzQueueWindow != null)
            {
                _qrzQueueWindow.Activate();
                _qrzQueueWindow.RefreshList();
                return;
            }

            _qrzQueueWindow = new EqslQueueWindow(
                () => dal.GetPendingQrzQsos(),
                async () =>
                {
                    string key = (Properties.Settings.Default.qrz_api_key ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(key))
                        throw new Exception("QRZ API key not configured. Please set it in Options → QRZ Services.");
                    int before = dal?.GetPendingQrzCount() ?? 0;
                    await PumpQrzQueue();
                    int after = dal?.GetPendingQrzCount() ?? 0;
                    UpdateQrzMenuCount();
                    return before - after;
                },
                "QRZ Logbook",
                () => dal.GetDismissedQrzQsos(),
                () => { dal.RequeueAllQrzDismissed(); UpdateQrzMenuCount(); },
                q => { dal.SetQrzStatus(q.id, 2); UpdateQrzMenuCount(); },
                DeleteQsoFromQueueWindow)
            {
                Owner = this
            };
            _qrzQueueWindow.Closed += (s, ev) => { _qrzQueueWindow = null; UpdateQrzMenuCount(); };
            _qrzQueueWindow.Show();
        }

        private void ViewQrzQueueContextMenuItem_Click(object sender, RoutedEventArgs e)
        {
            ShowQrzQueueWindow();
        }

        private EqslQueueWindow _clublogQueueWindow;

        private void ShowClublogQueueWindow()
        {
            if (dal == null) return;

            if (_clublogQueueWindow != null)
            {
                _clublogQueueWindow.Activate();
                _clublogQueueWindow.RefreshList();
                return;
            }

            _clublogQueueWindow = new EqslQueueWindow(
                () => dal.GetPendingClublogQsos(),
                async () =>
                {
                    if (!ClublogService.HasApiKey)
                        throw new Exception("Club Log upload is not active in this build (the Club Log application API key has not been configured).");
                    var s = Properties.Settings.Default;
                    if (string.IsNullOrWhiteSpace(s.ClublogEmail) || string.IsNullOrWhiteSpace(s.ClublogPassword))
                        throw new Exception("Club Log e-mail/password not configured. Please set them in Options → Club Log.");
                    int before = dal?.GetPendingClublogCount() ?? 0;
                    await PumpClublogQueue(force: true);
                    int after = dal?.GetPendingClublogCount() ?? 0;
                    UpdateClublogMenuCount();
                    return before - after;
                },
                "Club Log",
                () => dal.GetDismissedClublogQsos(),
                () => { dal.RequeueAllClublogDismissed(); UpdateClublogMenuCount(); },
                q => { dal.SetClublogStatus(q.id, 2); UpdateClublogMenuCount(); },
                DeleteQsoFromQueueWindow)
            {
                Owner = this
            };
            _clublogQueueWindow.Closed += (s, ev) => { _clublogQueueWindow = null; UpdateClublogMenuCount(); };
            _clublogQueueWindow.Show();
        }

        private void ViewClublogQueueContextMenuItem_Click(object sender, RoutedEventArgs e)
        {
            ShowClublogQueueWindow();
        }

        private void ClearClublogQueueContextMenuItem_Click(object sender, RoutedEventArgs e)
        {
            int pending = dal?.GetPendingClublogCount() ?? 0;
            if (pending == 0)
            {
                HolyMessageBox.Show("The Club Log queue is already empty.", "Clear Club Log Queue", HolyMsgType.Info, this);
                return;
            }

            bool confirmed = HolyMessageBox.ShowConfirm(
                $"Remove all {pending:N0} QSO(s) from the Club Log upload queue?\n\nThey will no longer be included in the next upload.",
                "Clear Club Log Queue", HolyMsgType.Warning, this);
            if (!confirmed) return;

            dal.ClearClublogQueue();
            UpdateClublogMenuCount();
        }

        // Uploads any confirmed services in sequence, showing per-QSO progress, then closes exactly once.
        // All confirmation dialogs were already shown in Window_Closing before this is called.
        private async void UploadAllAndCloseAsync(List<QSO> lotwPending, bool uploadEqsl, bool uploadQrz, bool uploadClublog)
        {
            this.IsEnabled = false;

            // Check connectivity before showing the window so the window never appears blank
            // while waiting for the network check to complete.
            bool online = false;
            try { online = Helper.CheckForInternetConnection(); } catch (System.Exception swallowed) { Log.Swallow(swallowed); }

            var progressWindow = new UploadProgressWindow { Owner = this };
            progressWindow.Show();

            if (lotwPending != null)
            {
                string tqslPath = Properties.Settings.Default.LotwTqslPath?.Trim();
                string password = Properties.Settings.Default.LotwTqslPassword;
                bool tqslConfigured = !string.IsNullOrWhiteSpace(tqslPath) && System.IO.File.Exists(tqslPath);
                if (!online || !tqslConfigured)
                {
                    string why = !online ? "no internet connection"
                        : "TQSL not configured (set the path in Options → LoTW)";
                    progressWindow.SkipService("LoTW", $"{lotwPending.Count:N0} QSO(s) — {why}");
                }
                else
                {
                    try { await UploadLotwQueueCoreAsync(lotwPending, tqslPath, password, progressWindow); } catch (System.Exception swallowed) { Log.Swallow(swallowed); }
                }
            }

            if (uploadEqsl)
            {
                if (!online)
                    progressWindow.SkipService("eQSL", "no internet connection — QSOs remain in queue");
                else
                    try { await PumpEqslQueue(force: true, progressWindow); } catch (System.Exception swallowed) { Log.Swallow(swallowed); }
            }

            if (uploadQrz)
            {
                bool qrzConfigured = Properties.Settings.Default.qrz_logbook_key_valid
                                     && !string.IsNullOrWhiteSpace(Properties.Settings.Default.qrz_api_key);
                if (!online || !qrzConfigured)
                {
                    string why = !online ? "no internet connection"
                        : "API key not configured (set it in Options → QRZ)";
                    progressWindow.SkipService("QRZ Logbook", $"{why} — QSOs remain in queue");
                }
                else
                    try { await PumpQrzQueue(force: true, progressWindow); } catch (System.Exception swallowed) { Log.Swallow(swallowed); }
            }

            if (uploadClublog)
            {
                bool clublogConfigured = ClublogService.HasApiKey
                                         && !string.IsNullOrWhiteSpace(Properties.Settings.Default.ClublogEmail)
                                         && !string.IsNullOrWhiteSpace(Properties.Settings.Default.ClublogPassword);
                if (!online || !clublogConfigured)
                {
                    string why = !online ? "no internet connection"
                        : "account not configured (set e-mail/password in Options → Club Log)";
                    progressWindow.SkipService("Club Log", $"{why} — QSOs remain in queue");
                }
                else
                    try { await PumpClublogQueue(force: true, progressWindow); } catch (System.Exception swallowed) { Log.Swallow(swallowed); }
            }

            progressWindow.ShowComplete();
            await progressWindow.WaitForOkAsync();

            _uploadInFlight = false;   // this is the legitimate final close; let it through
            this.Close();
        }
    }
}
