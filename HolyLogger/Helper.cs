using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Xml.Linq;
using System.Device.Location;
using HolyParser;
using System.Runtime.InteropServices;

namespace HolyLogger
{
    public static class Helper
    {
        // Shared client for downloading QRZ photos off the UI thread. Reused so we don't leak
        // sockets, and given a browser User-Agent because some image CDNs reject the default one.
        private static readonly HttpClient _imageHttpClient = CreateImageHttpClient();

        private static HttpClient CreateImageHttpClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
            return client;
        }

        // Downloads image bytes without blocking the caller's thread. Returns null on any failure.
        // Decoding the bytes into a BitmapImage (from a MemoryStream) is fast and safe to do on the
        // UI thread; it is the network download that must stay off it.
        public static async Task<byte[]> DownloadImageBytesAsync(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            try
            {
                return await _imageHttpClient.GetByteArrayAsync(url).ConfigureAwait(false);
            }
            catch
            {
                return null;
            }
        }
        [DllImport("user32.dll")]
        private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        [DllImport("Kernel32.dll")]
        private static extern uint GetLastError();

        public static bool LoginToQRZ(out string SessionKey)
        {
            if (string.IsNullOrWhiteSpace(Properties.Settings.Default.qrz_username) || string.IsNullOrWhiteSpace(Properties.Settings.Default.qrz_password))
            {
                SessionKey = "";
                return false;
            }
            try
            {
                ServicePointManager.Expect100Continue = true;
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                WebRequest request = WebRequest.Create("https://xmldata.qrz.com/xml/current/?username=" + Properties.Settings.Default.qrz_username + ";password=" + Properties.Settings.Default.qrz_password);
                using (WebResponse response = request.GetResponse())
                using (Stream dataStream = response.GetResponseStream())
                using (StreamReader reader = new StreamReader(dataStream))
                {
                    string responseFromServer = reader.ReadToEnd();

                    XElement xml = XElement.Parse(responseFromServer);
                    XElement element = xml.Elements().FirstOrDefault();
                    SessionKey = element.Elements().FirstOrDefault().Value;

                    if (SessionKey.Contains("incorrect"))
                    {
                        SessionKey = "";
                        return false;
                    }
                    return true;
                }
            }
            catch (Exception ex)
            {
                //System.Windows.Forms.MessageBox.Show("Login to QRZ service failed: " + ex.Message);
                SessionKey = "";
                return false;
            }
        }

        // Async variant of LoginToQRZ. The synchronous version blocks the calling thread on
        // request.GetResponse()/ReadToEnd() (default WebRequest timeout is 100 s), which freezes
        // the UI when called from the keystroke/QRZ-lookup path. Awaiting this keeps the UI thread
        // free while the QRZ login round-trip is in flight. Returns the session key, or "" on failure.
        public static async Task<string> LoginToQRZAsync()
        {
            if (string.IsNullOrWhiteSpace(Properties.Settings.Default.qrz_username) || string.IsNullOrWhiteSpace(Properties.Settings.Default.qrz_password))
            {
                return "";
            }
            try
            {
                ServicePointManager.Expect100Continue = true;
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                WebRequest request = WebRequest.Create("https://xmldata.qrz.com/xml/current/?username=" + Properties.Settings.Default.qrz_username + ";password=" + Properties.Settings.Default.qrz_password);
                using (WebResponse response = await request.GetResponseAsync().ConfigureAwait(false))
                using (Stream dataStream = response.GetResponseStream())
                using (StreamReader reader = new StreamReader(dataStream))
                {
                    string responseFromServer = await reader.ReadToEndAsync().ConfigureAwait(false);

                    XElement xml = XElement.Parse(responseFromServer);
                    XElement element = xml.Elements().FirstOrDefault();
                    string key = element.Elements().FirstOrDefault().Value;

                    if (string.IsNullOrEmpty(key) || key.Contains("incorrect"))
                    {
                        return "";
                    }
                    return key;
                }
            }
            catch (Exception)
            {
                return "";
            }
        }

        public static void SendHeartbeat(string machineName, string callsign, string op_callsign, string frequency, string mode, bool is_visible)
        {
            ServicePointManager.Expect100Continue = true;
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            if (string.IsNullOrEmpty(callsign) || string.IsNullOrEmpty(frequency) || string.IsNullOrEmpty(mode)) return;
            string IsVisible = is_visible ? "1" : "0";
            WebRequest request = WebRequest.Create("https://tools.iarc.org/Holyland/Server/heartbeat.php?callsign=" + callsign + "&operator=" + op_callsign + "&frequency=" + frequency + "&mode=" + mode + "&machine=" + machineName + "&is_visible=" + IsVisible);
            request.GetResponseAsync().ContinueWith(t =>
            {
                if (t.Status == TaskStatus.RanToCompletion)
                    t.Result?.Dispose();
            });
        }

        public static bool CheckForInternetConnection()
        {
            try
            {
                using (var client = new WebClient())
                using (client.OpenRead("http://clients3.google.com/generate_204"))
                {
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        public static uint GetIdleTime()
        {
            LASTINPUTINFO lastInPut = new LASTINPUTINFO();
            lastInPut.cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(lastInPut);
            GetLastInputInfo(ref lastInPut);

            return (uint)Environment.TickCount - lastInPut.dwTime;
        }

        public static long GetLastInputTime()
        {
            LASTINPUTINFO lastInPut = new LASTINPUTINFO();
            lastInPut.cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(lastInPut);
            if (!GetLastInputInfo(ref lastInPut))
            {
                throw new Exception(GetLastError().ToString());
            }
            return lastInPut.dwTime;
        }

    }
}
