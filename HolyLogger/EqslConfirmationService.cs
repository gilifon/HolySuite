using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace HolyLogger
{
    // Downloads the RECEIVED confirmations (the "In Box / Archive") for one eQSL account and parses them
    // into confirmations ready for DataAccess.MarkEqslConfirmed. eQSL's download is TWO steps: a request
    // to DownloadInBox.cfm returns an HTML page linking to a freshly generated .adi under /downloadedfiles,
    // which is then fetched. The In Box has no <STATION_CALLSIGN> per record (the whole file is "Received
    // eQSLs for <account>"), so the account's own callsign is stamped onto every confirmation for scoping,
    // and no <DXCC> is present, so DxccCode is left 0 (the caller resolves the entity from the callsign).
    public static class EqslConfirmationService
    {
        public const string InboxUrl = "https://www.eQSL.cc/qslcard/DownloadInBox.cfm";
        public const string FilesBase = "https://www.eQSL.cc/downloadedfiles/";

        public class EqslFetchResult
        {
            public bool Ok;
            public bool NetworkError;
            public string Reason;
            public int Count;
            public List<DataAccess.LotwConfirmation> Confirmations = new List<DataAccess.LotwConfirmation>();
        }

        private static readonly HttpClient _http = CreateClient();

        private static HttpClient CreateClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
            string ver;
            try { ver = Assembly.GetExecutingAssembly().GetName().Version.ToString(); }
            catch { ver = "1.0"; }
            c.DefaultRequestHeaders.UserAgent.ParseAdd("HolyLogger/" + ver);
            return c;
        }

        // Downloads and parses one eQSL account's In Box. stationCallsign is stamped on every confirmation
        // (used to scope the match to this operator's QSOs).
        //
        // rcvdSince (yyyyMMddHHmm, or null/empty for the whole In Box) becomes eQSL's RcvdSince
        // parameter - "everything that was entered into the database on or after this date/time" -
        // which is what makes a quick repeat check possible. Note it filters on when the CARD ARRIVED,
        // not on the QSO's date, so the marker the caller stores has to be the moment of the last
        // check, not the date of any QSO.
        public static async Task<EqslFetchResult> FetchInboxAsync(string username, string password, string stationCallsign,
                                                                  string rcvdSince = null,
                                                                  System.Threading.CancellationToken ct = default(System.Threading.CancellationToken))
        {
            var result = new EqslFetchResult();
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            {
                result.Reason = "auth";
                return result;
            }

            // Step 1: ask eQSL to generate the In Box file; the response is an HTML page with the link.
            string html;
            try
            {
                string url = InboxUrl
                    + "?UserName=" + Uri.EscapeDataString(username.Trim())
                    + "&Password=" + Uri.EscapeDataString(password)
                    + (string.IsNullOrWhiteSpace(rcvdSince) ? "" : "&RcvdSince=" + Uri.EscapeDataString(rcvdSince.Trim()));
                using (var resp = await _http.GetAsync(url, ct))
                    html = await resp.Content.ReadAsStringAsync();
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                result.NetworkError = true;
                return result;
            }

            // eQSL returns the error text in the page for a bad login.
            if (html.IndexOf("No such", StringComparison.OrdinalIgnoreCase) >= 0
                || html.IndexOf("Bad password", StringComparison.OrdinalIgnoreCase) >= 0
                || html.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0
                    && html.IndexOf("downloadedfiles", StringComparison.OrdinalIgnoreCase) < 0)
            {
                result.Reason = "eQSL rejected the user name / password";
                return result;
            }

            // Step 2: pull the generated .adi filename out of the page and download it.
            var m = Regex.Match(html, @"downloadedfiles/([A-Za-z0-9_.\-]+\.adi)", RegexOptions.IgnoreCase);
            if (!m.Success)
            {
                result.Reason = "eQSL did not return a download link";
                return result;
            }

            string adif;
            try
            {
                using (var resp = await _http.GetAsync(FilesBase + m.Groups[1].Value, ct))
                    adif = await resp.Content.ReadAsStringAsync();
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                result.NetworkError = true;
                return result;
            }

            result.Confirmations = ParseInbox(adif, (stationCallsign ?? string.Empty).Trim());
            result.Count = result.Confirmations.Count;
            result.Ok = true;
            return result;
        }

        // Parses eQSL's In Box ADIF (plain length-prefixed <field:len[:type]>value, records ended by
        // <eor>). Only records eQSL flags received (EQSL_QSL_RCVD = Y) are kept.
        private static List<DataAccess.LotwConfirmation> ParseInbox(string adif, string stationCallsign)
        {
            var list = new List<DataAccess.LotwConfirmation>();
            if (string.IsNullOrEmpty(adif)) return list;

            // Skip the header (everything up to <eoh>), if present.
            int eoh = adif.IndexOf("<eoh>", StringComparison.OrdinalIgnoreCase);
            int i = eoh >= 0 ? eoh + 5 : 0;
            int n = adif.Length;

            var cur = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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
                    Add(list, cur, stationCallsign);
                    cur.Clear();
                    continue;
                }

                string[] parts = tag.Split(':');       // name : len [ : type ]
                if (parts.Length < 2) continue;
                string name = parts[0].Trim();
                int len;
                if (!int.TryParse(parts[1].Trim(), out len) || len < 0) continue;
                if (i + len > n) len = n - i;
                string val = adif.Substring(i, len);
                i += len;
                if (name.Length > 0) cur[name] = val;
            }
            Add(list, cur, stationCallsign);   // trailing record, if any
            return list;
        }

        private static string F(Dictionary<string, string> f, string name)
        {
            string v;
            return f.TryGetValue(name, out v) ? (v ?? string.Empty).Trim() : string.Empty;
        }

        private static void Add(List<DataAccess.LotwConfirmation> list, Dictionary<string, string> f, string stationCallsign)
        {
            string call = F(f, "call");
            if (string.IsNullOrWhiteSpace(call)) return;

            string rcvd = F(f, "eqsl_qsl_rcvd");
            if (!rcvd.StartsWith("Y", StringComparison.OrdinalIgnoreCase)) return;   // only received cards

            list.Add(new DataAccess.LotwConfirmation
            {
                Call = call,
                Band = F(f, "band"),
                Mode = F(f, "mode"),
                QsoDate = F(f, "qso_date"),
                QslRDate = F(f, "eqsl_qslrdate"),
                StationCallsign = stationCallsign,   // the In Box belongs to this account's callsign
                DxccCode = 0                          // eQSL sends no <DXCC>; entity resolved by the caller
            });
        }
    }
}
