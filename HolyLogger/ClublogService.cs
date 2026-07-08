using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;

namespace HolyLogger
{
    // The outcome of one Club Log real-time upload. Ok = the record was accepted (HTTP 2xx).
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

        // Club Log's API key is per-APPLICATION, not per-user: Club Log issues one key for HolyLogger
        // to the software author (email support@clublog.org with a short description of the use). Every
        // user then authenticates with their own Club Log e-mail + password; they do NOT enter a key.
        //   >>> Paste HolyLogger's Club Log API key here once Club Log issues it. <<<
        // Until then Club Log upload stays disabled (HasApiKey is false) so nothing hammers the API.
        private const string Placeholder = "PUT-HOLYLOGGER-CLUBLOG-API-KEY-HERE";
        public const string ApiKey = Placeholder;

        // True once the real application key has been pasted in above.
        public static bool HasApiKey =>
            !string.IsNullOrWhiteSpace(ApiKey) && ApiKey != Placeholder;

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
                    result.Ok = resp.IsSuccessStatusCode;
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
