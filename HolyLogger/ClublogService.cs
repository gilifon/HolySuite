using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;

namespace HolyLogger
{
    // The outcome of one Club Log real-time upload. Ok = the record was accepted (HTTP 2xx AND the
    // reply body is a genuine acceptance, not a fake-200 HTML/error page -- see IsRealtimeAccepted).
    // NetworkError = the request never completed (offline / timeout) so the caller may retry later.
    // Otherwise it was a definitive rejection (bad credentials / bad record); StatusCode and Message
    // carry Club Log's response for diagnostics.
    public class ClublogResult
    {
        public bool Ok;
        public bool NetworkError;
        public int StatusCode;
        public string Message;
    }

    // Thin client for Club Log's real-time upload API (https://clublog.org/realtime.php). One QSO per
    // call: the account is authenticated by email + password + the Club Log API key, and the station
    // callsign is taken from the QSO (a single Club Log account can hold several of your callsigns).
    // Club Log answers HTTP 200 when the record is accepted.
    public static class ClublogService
    {
        public const string Endpoint = "https://clublog.org/realtime.php";
        // Used only to verify a user's account login without creating a QSO (see TestCredentialsAsync).
        public const string GetAdifEndpoint = "https://clublog.org/getadif.php";
        // Bulk upload of a whole log as one ADIF file (see UploadFullLogAsync).
        public const string PutLogsEndpoint = "https://clublog.org/putlogs.php";

        // Club Log's API key is per-APPLICATION, not per-user: Club Log issues one key for HolyLogger
        // to the software author. Every user then authenticates with their own Club Log e-mail +
        // password; they do NOT enter a key. Hardcoded here so any build of the source has Club Log
        // enabled — note this makes the key visible in the (public) source repo and in the binary.
        public const string ApiKey = "d7864de21d8d17e9930bbfca7d437cfe863c1930";

        public static bool HasApiKey => !string.IsNullOrWhiteSpace(ApiKey);

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

        // Uploads a single ADIF record for the given station callsign, authenticating with the user's
        // Club Log e-mail + password and HolyLogger's embedded application API key. Never throws.
        public static async Task<ClublogResult> UploadAsync(string email, string password, string callsign, string adif)
        {
            var result = new ClublogResult();

            if (!HasApiKey || string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password)
                || string.IsNullOrWhiteSpace(callsign) || string.IsNullOrWhiteSpace(adif))
            {
                result.Ok = false;
                result.Message = "missing application key, credentials, or record";
                return result;
            }

            // x-www-form-urlencoded body; FormUrlEncodedContent sets the Content-Type and encodes
            // every value. Field names are Club Log's realtime.php parameters.
            var fields = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("email", email.Trim()),
                new KeyValuePair<string, string>("password", password),
                new KeyValuePair<string, string>("callsign", callsign.Trim().ToUpperInvariant()),
                new KeyValuePair<string, string>("api", ApiKey.Trim()),
                new KeyValuePair<string, string>("adif", adif)
            };

            try
            {
                using (var content = new FormUrlEncodedContent(fields))
                using (HttpResponseMessage resp = await _http.PostAsync(Endpoint, content))
                {
                    result.StatusCode = (int)resp.StatusCode;
                    try { result.Message = await resp.Content.ReadAsStringAsync(); }
                    catch (Exception swallowed) { Log.Swallow(swallowed); }
                    // Club Log signals acceptance with HTTP 200 and rejection with an error status, so
                    // the code is the primary signal. But a 200 can be FAKE -- a captive-portal / proxy
                    // login page, or Club Log's own "requires a valid API key" page -- both HTTP 200 with
                    // an HTML body that is NOT a real acceptance. Only trust a 200 whose body is not one
                    // of those pages, so a QSO is never falsely confirmed (and instead stays queued).
                    result.Ok = resp.IsSuccessStatusCode && IsRealtimeAccepted(result.Message);
                }
            }
            catch
            {
                result.NetworkError = true;   // offline / timeout -> caller may retry later
            }
            return result;
        }

        // Guards realtime.php's HTTP-200 accept signal against FAKE successes. A genuine acceptance is
        // a short plain-text reply (often empty); it is never a full HTML document. A captive-portal or
        // proxy login page, or Club Log's own "requires a valid API key" page, all come back as HTTP 200
        // with an HTML body -- those must NOT count as accepted. Deliberately narrow: it only rejects
        // clearly-HTML/known-error bodies, so a real success (empty or short text) is never mistaken for
        // a failure (which would strand the QSO in the queue forever).
        private static bool IsRealtimeAccepted(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return true;   // empty 200 body = Club Log's normal accept
            string trimmed = body.TrimStart();
            if (trimmed.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("<html", StringComparison.OrdinalIgnoreCase))
                return false;   // an HTML page is never a realtime.php acceptance
            if (body.IndexOf("requires a valid API key", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;   // Club Log's missing/invalid application-key page (served as 200)
            return true;        // 200 with a non-HTML, non-error body -> genuine acceptance
        }

        // Bulk-uploads a whole log as ONE ADIF file (Club Log's putlogs.php). Club Log MERGES and
        // de-duplicates, so re-running is safe. clear is always 0 here — we never flush the account's
        // existing log. This is for the whole-log backlog; per-QSO live sends use UploadAsync instead
        // (Club Log explicitly asks callers not to abuse putlogs.php with tiny repeated uploads).
        // Authenticated by the user's e-mail + password + HolyLogger's application API key. Never throws.
        public static async Task<ClublogResult> UploadFullLogAsync(string email, string password, string callsign, string adif)
        {
            var result = new ClublogResult();

            if (!HasApiKey || string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password)
                || string.IsNullOrWhiteSpace(callsign) || string.IsNullOrWhiteSpace(adif))
            {
                result.Ok = false;
                result.Message = "missing application key, credentials, or log";
                return result;
            }

            try
            {
                using (var form = new MultipartFormDataContent())
                {
                    form.Add(new StringContent(email.Trim()), "email");
                    form.Add(new StringContent(password), "password");
                    form.Add(new StringContent(callsign.Trim().ToUpperInvariant()), "callsign");
                    form.Add(new StringContent(ApiKey.Trim()), "api");
                    form.Add(new StringContent("0"), "clear");   // 0 = merge; never flush the account's log

                    var file = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(adif));
                    file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                    form.Add(file, "file", "holylogger.adi");

                    using (HttpResponseMessage resp = await _http.PostAsync(PutLogsEndpoint, form))
                    {
                        result.StatusCode = (int)resp.StatusCode;
                        try { result.Message = await resp.Content.ReadAsStringAsync(); }
                        catch (Exception swallowed) { Log.Swallow(swallowed); }
                        result.Ok = resp.IsSuccessStatusCode;
                    }
                }
            }
            catch
            {
                result.NetworkError = true;   // offline / timeout -> caller may retry later
            }
            return result;
        }

        // Verifies a user's Club Log account login WITHOUT creating a QSO, so the operator can confirm
        // their e-mail / password (and that the callsign belongs to the account) before relying on
        // real-time upload. It posts to getadif.php with a far-future date filter so Club Log returns
        // an (almost) empty log; a valid login yields HTTP 2xx, a bad login yields 403. Like every
        // Club Log API, getadif.php requires HolyLogger's application API key IN ADDITION to the
        // user's e-mail + password + call -- without a valid key Club Log returns its
        // "requires a valid API key" page. Never throws.
        public static async Task<ClublogResult> TestCredentialsAsync(string email, string password, string callsign)
        {
            var result = new ClublogResult();

            if (!HasApiKey || string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password) || string.IsNullOrWhiteSpace(callsign))
            {
                result.Ok = false;
                result.Message = "missing application key, e-mail, password, or callsign";
                return result;
            }

            var fields = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("email", email.Trim()),
                new KeyValuePair<string, string>("password", password),
                new KeyValuePair<string, string>("call", callsign.Trim().ToUpperInvariant()),
                new KeyValuePair<string, string>("api", ApiKey.Trim()),
                // Far-future, internally-consistent date filter -> the response carries essentially no
                // QSOs, so this is a lightweight login check rather than a full log download.
                new KeyValuePair<string, string>("startyear", "2099"),
                new KeyValuePair<string, string>("startmonth", "12"),
                new KeyValuePair<string, string>("startday", "31")
            };

            try
            {
                using (var content = new FormUrlEncodedContent(fields))
                using (HttpResponseMessage resp = await _http.PostAsync(GetAdifEndpoint, content))
                {
                    result.StatusCode = (int)resp.StatusCode;
                    try { result.Message = await resp.Content.ReadAsStringAsync(); }
                    catch (Exception swallowed) { Log.Swallow(swallowed); }
                    result.Ok = resp.IsSuccessStatusCode;
                }
            }
            catch
            {
                result.NetworkError = true;
            }
            return result;
        }
    }
}
