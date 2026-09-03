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
                // Task.Run, not just await: on .NET Framework the request's proxy is resolved on the
                // thread that STARTS it, so awaiting alone still leaves that part on the caller - which
                // for the operator photo is the UI thread, on the typing path.
                return await Task.Run(() => _imageHttpClient.GetByteArrayAsync(url)).ConfigureAwait(false);
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

        // Logs in to QRZ.com and returns the session key, or "" on failure.
        //
        // There is no synchronous version any more, deliberately. It blocked the calling thread on
        // GetResponse()/ReadToEnd(), whose default timeout is 100 seconds, and the last two callers were
        // the Test Connection buttons in Options - so a network that hangs rather than refuses froze the
        // Options window outright. Awaiting this leaves the UI thread free while the round trip is in
        // flight, and there is no blocking overload left for anyone to reach for by mistake.
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

                // Task.Run around the START. Awaiting already leaves the thread free while the round
                // trip is in flight, but the proxy is resolved BEFORE that, on whichever thread begins
                // the request - and the callers here are the Options window closing and the Test
                // Connection button, both on the window's thread.
                using (WebResponse response = await Task.Run(() => request.GetResponseAsync()).ConfigureAwait(false))
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
            string url = "https://tools.iarc.org/Holyland/Server/heartbeat.php?callsign=" + callsign
                       + "&operator=" + op_callsign + "&frequency=" + frequency + "&mode=" + mode
                       + "&machine=" + machineName + "&is_visible=" + IsVisible;

            // STARTED AWAY FROM THE WINDOW'S THREAD, and it matters more here than anywhere else.
            //
            // A timer calls this EVERY MINUTE for as long as the program is open. On .NET Framework the
            // proxy for a request is worked out on whichever thread STARTS it, so with "automatically
            // detect proxy settings" switched on - the Windows default - the window was paying that
            // price once a minute, all day. Nobody waits for the answer: it is a heartbeat.
            Task.Run(() =>
            {
                try
                {
                    WebRequest request = WebRequest.Create(url);
                    request.GetResponseAsync().ContinueWith(t =>
                    {
                        if (t.Status == TaskStatus.RanToCompletion)
                            t.Result?.Dispose();
                    });
                }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
            });
        }

        // "Is there really internet out there", answered in three seconds or not at all.
        //
        // This used to be a WebClient with no timeout, which means the .NET default of ONE HUNDRED
        // SECONDS. With no network at all that is harmless - the name lookup fails at once - but a
        // network that swallows packets instead of refusing them (hotel wifi, a captive portal, a
        // firewalled club station) left whoever asked waiting a minute and a half. Nothing this answer
        // is used for is worth that wait, so the probe is bounded, and it is async: no caller has to
        // give up a thread, least of all the UI thread.
        private static readonly HttpClient _probeHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };

        public static async Task<bool> CheckForInternetConnectionAsync()
        {
            try
            {
                // generate_204 answers with an empty 204 - the cheapest honest "yes" on the web, and
                // no body to read. Headers are enough, so the response is not waited out in full.
                using (var response = await _probeHttpClient
                           .GetAsync("http://clients3.google.com/generate_204", HttpCompletionOption.ResponseHeadersRead)
                           .ConfigureAwait(false))
                {
                    return response.IsSuccessStatusCode;
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

    // ── EIGHT WORDS TO A LINE ─────────────────────────────────────────────────────────────────
    // Wrapping at a WIDTH is not the same thing as a readable line. 460 pixels of 16-point text is
    // ten or eleven words, and a tooltip written in ten-word lines is read like a paragraph in a book
    // - which is not how anybody reads a note that popped up under the pointer. Eight words is the
    // line he asked for, and eight words is what this makes, whatever the width happens to be.
    //
    // Line breaks the text already has are kept: a tooltip written as two short paragraphs stays two
    // short paragraphs, and only the lines longer than eight words are folded.
    public sealed class ToolTipEightWordsConverter : System.Windows.Data.IValueConverter
    {
        internal const int WordsPerLine = 8;

        public object Convert(object value, System.Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            string s = value as string;
            if (string.IsNullOrEmpty(s)) return value;

            var lines = new System.Collections.Generic.List<string>();
            foreach (string line in s.Replace("\r\n", "\n").Split('\n'))
            {
                string[] words = line.Split(new[] { ' ', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
                if (words.Length == 0) { lines.Add(string.Empty); continue; }   // a blank line between paragraphs
                for (int i = 0; i < words.Length; i += WordsPerLine)
                    lines.Add(string.Join(" ", words, i, System.Math.Min(WordsPerLine, words.Length - i)));
            }
            return string.Join(System.Environment.NewLine, lines);
        }

        public object ConvertBack(object value, System.Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            return value;   // tooltips are never typed into
        }
    }

    // ── TOOLTIPS THAT WRAP ────────────────────────────────────────────────────────────────────
    // A tooltip written as a plain string is drawn by WPF as ONE LINE, however long it is: a sentence
    // of twenty words ran clean across the screen. A MaxWidth on its own does not cure that - it only
    // CUTS the line off at that width, losing the end of it.
    //
    // The cure is a wrapping TextBlock, and it belongs in the app-wide ToolTip style so every tooltip
    // in the program gets it without anybody having to remember. But a tooltip's content is not always
    // a string: a few are built from TextBlocks and StackPanels, and handing THOSE to a "Text={Binding}"
    // template would print the name of the type. So the template is chosen by what the content is -
    // text gets the wrapping one, anything else is left exactly as it was.
    public sealed class ToolTipTextTemplateSelector : System.Windows.Controls.DataTemplateSelector
    {
        public System.Windows.DataTemplate TextTemplate { get; set; }

        public override System.Windows.DataTemplate SelectTemplate(object item, System.Windows.DependencyObject container)
        {
            return item is string ? TextTemplate : null;
        }
    }
}
