using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;

namespace HolyLogger
{
    // The parsed outcome of a single QRZ Logbook API call. RESULT=OK -> Ok = true; RESULT=FAIL ->
    // Ok = false with Reason set (e.g. "auth", "subscription"). NetworkError = true means the request
    // never reached QRZ (offline / timeout), so the caller should leave the QSO pending and retry.
    public class QrzLogbookResult
    {
        public bool Ok;                 // RESULT=OK
        public bool NetworkError;       // request failed to complete (offline / timeout)
        public string Reason;           // REASON=... on failure (lower-case as QRZ sends it)
        public string LogId;            // LOGID=... returned after a successful INSERT
        public string Count;            // COUNT=... total QSOs in the online logbook (STATUS)
        public string BookId;           // BOOKID=... active logbook id (STATUS)
        public string RawBody;          // the unparsed response, for diagnostics

        // True when QRZ gave a definitive, non-transient rejection (bad key, no subscription, bad
        // record). Such a QSO must NOT be retried forever, so the caller marks it rejected (status 2).
        public bool IsPermanentFailure
        {
            get
            {
                if (Ok || NetworkError) return false;
                // Any explicit RESULT=FAIL from QRZ is a permanent rejection of THIS request; only a
                // network error (handled above) is transient.
                return true;
            }
        }
    }

    // Thin client for the QRZ.com Logbook API v3.0 (https://logbook.qrz.com/api). Used both by the
    // settings panel (ACTION=STATUS, key validation) and the real-time push on save (ACTION=INSERT).
    public static class QrzLogbookService
    {
        public const string Endpoint = "https://logbook.qrz.com/api";
        public const string ApiDocsUrl = "https://www.qrz.com/docs/logbook30/api";

        // One long-lived client. QRZ blocks requests with a missing/default User-Agent, so a distinct
        // product name identifying this program is set once here (spec section 2, "Mandatory Header").
        private static readonly HttpClient _http = CreateClient();

        private static HttpClient CreateClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
            string ver;
            try { ver = Assembly.GetExecutingAssembly().GetName().Version.ToString(); }
            catch { ver = "1.0"; }
            c.DefaultRequestHeaders.UserAgent.ParseAdd("HolyLogger/" + ver);
            return c;
        }

        // Validates an API key (ACTION=STATUS). On success Count/BookId are filled in.
        public static Task<QrzLogbookResult> TestKeyAsync(string apiKey)
        {
            return SendAsync(apiKey, "STATUS", null);
        }

        // Pushes one ADIF record (ACTION=INSERT). On success LogId carries the QRZ transaction id.
        public static Task<QrzLogbookResult> InsertAsync(string apiKey, string adif)
        {
            return SendAsync(apiKey, "INSERT", adif);
        }

        private static async Task<QrzLogbookResult> SendAsync(string apiKey, string action, string adif)
        {
            var result = new QrzLogbookResult();

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                result.Ok = false;
                result.Reason = "auth";
                return result;
            }

            // x-www-form-urlencoded body: KEY, ACTION, and (for INSERT) ADIF. FormUrlEncodedContent
            // sets the Content-Type header and URL-encodes every value for us.
            var fields = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("KEY", apiKey.Trim()),
                new KeyValuePair<string, string>("ACTION", action)
            };
            if (!string.IsNullOrEmpty(adif))
                fields.Add(new KeyValuePair<string, string>("ADIF", adif));

            string body;
            try
            {
                using (var content = new FormUrlEncodedContent(fields))
                using (HttpResponseMessage resp = await _http.PostAsync(Endpoint, content))
                {
                    body = await resp.Content.ReadAsStringAsync();
                }
            }
            catch
            {
                result.NetworkError = true;   // offline / timeout -> caller keeps the QSO pending
                return result;
            }

            result.RawBody = body;
            Parse(body, result);
            return result;
        }

        // QRZ replies as a urlencoded key=value string joined by '&', e.g.
        //   "RESULT=OK&LOGID=123456&COUNT=42&BOOKID=98765"
        //   "RESULT=FAIL&REASON=invalid+api+key&EXTENDED="
        private static void Parse(string body, QrzLogbookResult result)
        {
            if (string.IsNullOrWhiteSpace(body)) { result.Ok = false; result.Reason = "empty"; return; }

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string pair in body.Replace("\r", "").Replace("\n", "&").Split('&'))
            {
                if (pair.Length == 0) continue;
                int eq = pair.IndexOf('=');
                string key = eq >= 0 ? pair.Substring(0, eq) : pair;
                string val = eq >= 0 ? pair.Substring(eq + 1) : "";
                try { val = Uri.UnescapeDataString(val.Replace('+', ' ')); } catch { }
                map[key.Trim()] = val.Trim();
            }

            string resultVal;
            map.TryGetValue("RESULT", out resultVal);
            result.Ok = string.Equals(resultVal, "OK", StringComparison.OrdinalIgnoreCase);

            string tmp;
            if (map.TryGetValue("REASON", out tmp)) result.Reason = tmp;
            if (map.TryGetValue("LOGID", out tmp)) result.LogId = tmp;
            if (map.TryGetValue("COUNT", out tmp)) result.Count = tmp;
            if (map.TryGetValue("BOOKID", out tmp)) result.BookId = tmp;
        }
    }
}
