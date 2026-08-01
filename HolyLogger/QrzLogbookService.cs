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
                try { val = Uri.UnescapeDataString(val.Replace('+', ' ')); } catch (System.Exception swallowed) { Log.Swallow(swallowed); }
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

        // ------------------------------------------------------------------------------------------
        // FETCH: reading the confirmed QSOs back out of the logbook (the QRZ side of the confirmation
        // feature). Parallel to LoTW's confirmation download, but the transport is different - QRZ
        // returns the records as one HTML-escaped ADIF blob wrapped in a key=value envelope.
        // ------------------------------------------------------------------------------------------

        // The outcome of ACTION=FETCH. Ok mirrors RESULT=OK; Count is QRZ's own COUNT header (how many
        // records it sent); Confirmations is the parsed list, ready for DataAccess.MarkQrzConfirmed.
        public class QrzFetchResult
        {
            public bool Ok;
            public bool NetworkError;
            public string Reason;
            public int Count;
            public List<DataAccess.LotwConfirmation> Confirmations = new List<DataAccess.LotwConfirmation>();
        }

        // Downloads every CONFIRMED QSO from the logbook this key belongs to. There is no "only what is
        // new" variant - see the note on the option string below for what was tried.
        public static async Task<QrzFetchResult> FetchConfirmationsAsync(string apiKey,
                                                                         System.Threading.CancellationToken ct = default(System.Threading.CancellationToken))
        {
            var result = new QrzFetchResult();
            if (string.IsNullOrWhiteSpace(apiKey)) { result.Reason = "auth"; return result; }

            // STATUS:CONFIRMED -> only confirmed records; MAX high enough to never page.
            //
            // Separator: QRZ's prose says options are joined by "&" or ";", while its own example uses
            // commas ("BAND:80m,MODE:SSB,MAX:400"). Commas are proven for STATUS+MAX, but adding
            // MODSINCE that way came back REJECTED, so the request that carries MODSINCE uses the
            // documented semicolon instead. The plain request keeps the commas that have always worked
            // - there is no reason to disturb it.
            // ALWAYS the whole confirmed set. QRZ documents a MODSINCE option - "only return records
            // modified since this date" - which would make a quick repeat check possible, and it does
            // not work on this API. Tried against the live service in three forms, all rejected with a
            // valid key that uploads and fetches perfectly well otherwise:
            //
            //     STATUS:CONFIRMED,MAX:100000,MODSINCE:2026-07-31     (commas, as QRZ's own example)
            //     STATUS:CONFIRMED;MAX:100000;MODSINCE:2026-07-31     (semicolons, as QRZ's prose)
            //     MODSINCE:2026-07-31,MAX:100000                      (alone, no STATUS)
            //
            // So do not spend time on it again without new information from QRZ. The full set is a few
            // hundred KB and cheap enough; the caller marks with fullReset.
            string option = "STATUS:CONFIRMED,MAX:100000";

            var fields = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("KEY", apiKey.Trim()),
                new KeyValuePair<string, string>("ACTION", "FETCH"),
                new KeyValuePair<string, string>("OPTION", option)
            };

            string body;
            try
            {
                using (var content = new FormUrlEncodedContent(fields))
                using (HttpResponseMessage resp = await _http.PostAsync(Endpoint, content, ct))
                {
                    body = await resp.Content.ReadAsStringAsync();
                }
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                result.NetworkError = true;
                return result;
            }

            // The body is "COUNT=n&RESULT=OK&ADIF=<html-escaped ADIF>". The ADIF value is full of
            // &lt;/&gt; entities, so splitting the WHOLE body on '&' (as the STATUS/INSERT parser does)
            // would shred it. Instead take everything after the literal "ADIF=" as the blob, and parse
            // only the head - which has no entities - for RESULT / COUNT.
            int adifAt = body.IndexOf("ADIF=", StringComparison.OrdinalIgnoreCase);
            string head = adifAt >= 0 ? body.Substring(0, adifAt) : body;
            string adifEscaped = adifAt >= 0 ? body.Substring(adifAt + 5) : string.Empty;

            var map = ParseKeyValues(head);
            string rv;
            map.TryGetValue("RESULT", out rv);
            result.Ok = string.Equals(rv, "OK", StringComparison.OrdinalIgnoreCase);
            string reason;
            if (map.TryGetValue("REASON", out reason)) result.Reason = reason;
            string cnt;
            if (map.TryGetValue("COUNT", out cnt)) { int c; if (int.TryParse(cnt.Trim(), out c)) result.Count = c; }

            if (result.Ok && adifEscaped.Length > 0)
            {
                string adif = System.Net.WebUtility.HtmlDecode(adifEscaped);
                result.Confirmations = ParseAdifConfirmations(adif);
            }
            return result;
        }

        // Splits a "k=v&k=v" envelope (no HTML entities) into a case-insensitive map, url-decoding
        // each value. Same shape as Parse() above, factored out so FETCH can reuse it on the head only.
        private static Dictionary<string, string> ParseKeyValues(string s)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(s)) return map;
            foreach (string pair in s.Replace("\r", "").Replace("\n", "&").Split('&'))
            {
                if (pair.Length == 0) continue;
                int eq = pair.IndexOf('=');
                string key = eq >= 0 ? pair.Substring(0, eq) : pair;
                string val = eq >= 0 ? pair.Substring(eq + 1) : "";
                try { val = Uri.UnescapeDataString(val.Replace('+', ' ')); } catch (Exception swallowed) { Log.Swallow(swallowed); }
                map[key.Trim()] = val.Trim();
            }
            return map;
        }

        // Parses QRZ's ADIF blob (length-prefixed <field:len[:type]>value, records ended by <eor>) into
        // confirmations. Only the handful of fields the matcher needs are kept; a record with no <call>
        // is skipped. We FETCH with STATUS:CONFIRMED, but app_qrzlog_status is still checked so a record
        // is only taken when QRZ actually flags it confirmed (C).
        private static List<DataAccess.LotwConfirmation> ParseAdifConfirmations(string adif)
        {
            var list = new List<DataAccess.LotwConfirmation>();
            if (string.IsNullOrEmpty(adif)) return list;

            var cur = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int i = 0, n = adif.Length;
            while (i < n)
            {
                int lt = adif.IndexOf('<', i);
                if (lt < 0) break;
                int gt = adif.IndexOf('>', lt + 1);
                if (gt < 0) break;
                string tag = adif.Substring(lt + 1, gt - lt - 1);
                i = gt + 1;

                if (tag.Equals("eor", StringComparison.OrdinalIgnoreCase))
                {
                    AddConfirmation(list, cur);
                    cur.Clear();
                    continue;
                }
                if (tag.Equals("eoh", StringComparison.OrdinalIgnoreCase)) { cur.Clear(); continue; }

                // "name:len" or "name:len:type"
                string[] parts = tag.Split(':');
                if (parts.Length < 2) continue;
                string name = parts[0].Trim();
                int len;
                if (!int.TryParse(parts[1].Trim(), out len) || len < 0) continue;
                if (i + len > n) len = n - i;         // defend against a truncated stream
                string val = adif.Substring(i, len);
                i += len;
                if (name.Length > 0) cur[name] = val;
            }
            // A final record is normally closed by <eor>; add any trailing one just in case.
            AddConfirmation(list, cur);
            return list;
        }

        private static string Field(Dictionary<string, string> f, string name)
        {
            string v;
            return f.TryGetValue(name, out v) ? (v ?? string.Empty).Trim() : string.Empty;
        }

        private static void AddConfirmation(List<DataAccess.LotwConfirmation> list, Dictionary<string, string> f)
        {
            string call = Field(f, "call");
            if (string.IsNullOrWhiteSpace(call)) return;

            string status = Field(f, "app_qrzlog_status");
            if (status.Length > 0 && !status.Equals("C", StringComparison.OrdinalIgnoreCase)) return;

            var c = new DataAccess.LotwConfirmation
            {
                Call = call,
                Band = Field(f, "band"),
                Mode = Field(f, "mode"),
                QsoDate = Field(f, "qso_date"),
                StationCallsign = Field(f, "station_callsign"),
                QslRDate = Field(f, "app_qrzlog_qsldate")
            };
            int dxcc;
            if (int.TryParse(Field(f, "dxcc"), out dxcc)) c.DxccCode = dxcc;
            list.Add(c);
        }
    }
}
