using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Newtonsoft.Json;

namespace HolyLogger
{
    internal class CallsignUploader
    {
        private const string EndpointUrl = "https://tools.iarc.org/holyland/server/addcallsign.php";
        private const int BatchSize = 100;

        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);
        private readonly string _pendingFilePath;
        private readonly string _serverLogPath;

        public CallsignUploader(string baseDir)
        {
            _pendingFilePath = Path.Combine(baseDir, "callsigns_new.txt");
            _serverLogPath = Path.Combine(baseDir, "callsign_server_response_log.txt");
            NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        }

        private void OnNetworkAvailabilityChanged(object sender, NetworkAvailabilityEventArgs e)
        {
            if (e.IsAvailable)
            {
                TrySendFireAndForget();
            }
        }

        /// <summary>Fire-and-forget: safe to call from non-async code.</summary>
        public void TrySendFireAndForget()
        {
            Task.Run(() => TrySendAsync());
        }

        public async Task TrySendAsync()
        {
            // Skip if already running
            if (!await _lock.WaitAsync(0)) return;
            try
            {
                if (!File.Exists(_pendingFilePath)) return;

                // Read all valid callsigns from the pending file
                var callsigns = new List<string>();
                foreach (var line in File.ReadLines(_pendingFilePath))
                {
                    string call = line.Trim().ToUpperInvariant();
                    if (!string.IsNullOrWhiteSpace(call))
                        callsigns.Add(call);
                }

                if (callsigns.Count == 0) return;

                // Make a timestamped snapshot copy — audit record of what we tried to send
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string snapshotPath = Path.Combine(
                    Path.GetDirectoryName(_pendingFilePath),
                    string.Format("new_call_{0}.txt", timestamp));
                try { File.Copy(_pendingFilePath, snapshotPath, overwrite: true); } catch { }

                // Send in batches of up to BatchSize
                bool allSucceeded = true;
                for (int i = 0; i < callsigns.Count; i += BatchSize)
                {
                    int count = Math.Min(BatchSize, callsigns.Count - i);
                    var batch = callsigns.GetRange(i, count);

                    var payload = new List<Dictionary<string, string>>(batch.Count);
                    foreach (var c in batch)
                        payload.Add(new Dictionary<string, string> { { "callsign", c } });

                    string json = JsonConvert.SerializeObject(payload);
                    var content = new StringContent(json, Encoding.UTF8, "application/json");

                    try
                    {
                        HttpResponseMessage response = await _http.PostAsync(EndpointUrl, content).ConfigureAwait(false);
                        string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        LogAndNotify(
                            string.Format(
                                "SEND OK HTTP={0} batch={1}-{2}/{3} response={4}",
                                (int)response.StatusCode,
                                i + 1,
                                i + count,
                                callsigns.Count,
                                body));
                        var result = JsonConvert.DeserializeObject<ServerResponse>(body);

                        if (result == null || !result.success)
                        {
                            LogAndNotify("SERVER CONFIRMATION FAILED (success!=true). Pending file kept.");
                            allSucceeded = false;
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        LogAndNotify("SEND ERROR " + ex.Message + ". Pending file kept.");
                        allSucceeded = false;
                        break;
                    }
                }

                // All batches confirmed by server — delete the pending file
                if (allSucceeded)
                {
                    try { File.Delete(_pendingFilePath); } catch { }
                    LogAndNotify("ALL BATCHES CONFIRMED. callsigns_new.txt deleted.");
                }
                // If not all succeeded, leave callsigns_new.txt for the next retry
            }
            finally
            {
                _lock.Release();
            }
        }

        private class ServerResponse
        {
            public bool success { get; set; }
            public int inserted { get; set; }
            public int ignored { get; set; }
        }

        private void LogAndNotify(string message)
        {
            string line = string.Format("{0} | {1}", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), message);

            try
            {
                File.AppendAllText(_serverLogPath, line + Environment.NewLine);
            }
            catch
            {
            }

            try
            {
                if (Application.Current != null)
                {
                    Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            MessageBox.Show(line, "Callsign Upload", MessageBoxButton.OK, MessageBoxImage.Information);
                        }
                        catch
                        {
                        }
                    }));
                }
            }
            catch
            {
            }
        }
    }
}
