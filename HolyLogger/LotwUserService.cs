using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;

namespace HolyLogger
{
    // Keeps a local, in-memory set of Logbook of The World (LoTW) user callsigns so the cluster can
    // flag spotted stations that upload to LoTW. Modeled on CtyDatService: an updatable copy of the
    // ARRL "user activity" list lives in LocalAppData and is refreshed in the background at most once
    // a week (the list only changes ~weekly). Lookups are O(1) against a HashSet, so per-spot checks
    // are free — no live web query per callsign.
    public static class LotwUserService
    {
        // ARRL's official LoTW user-activity list. One line per user: "CALLSIGN,yyyy-MM-dd,HH:mm:ss".
        private const string DownloadUrl = "https://lotw.arrl.org/lotw-user-activity.csv";

        // Refresh no more often than this; the ARRL list only updates roughly weekly.
        private const int RefreshEveryDays = 7;

        private static readonly object _gate = new object();
        private static HashSet<string> _users = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static volatile bool _loaded;

        private static string _localPath;

        // Full path to the cached CSV, in the same LocalAppData folder as cty.dat / logDB.
        public static string LocalPath
        {
            get
            {
                if (_localPath == null)
                {
                    Assembly asm = Assembly.GetExecutingAssembly();
                    var fvi = System.Diagnostics.FileVersionInfo.GetVersionInfo(asm.Location);
                    string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    _localPath = Path.Combine(appData, fvi.CompanyName, fvi.ProductName, "lotw-users.csv");
                }
                return _localPath;
            }
        }

        // Loads the cached list into memory if it exists. Call once at startup (cheap; ~150k lines).
        public static void Initialize()
        {
            try
            {
                if (File.Exists(LocalPath))
                    LoadFromFile(LocalPath);
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // True when the DX callsign is a known LoTW uploader. Tries the exact spotted call first, then
        // the base call (so W1AW/P matches W1AW and 4Z5SL/M matches 4Z5SL) using the same identity rule
        // as the rest of the app. Returns false until a list has been loaded.
        public static bool IsLotwUser(string callsign)
        {
            if (!_loaded) return false;
            string call = (callsign ?? string.Empty).Trim();
            if (call.Length == 0) return false;

            lock (_gate)
            {
                if (_users.Contains(call)) return true;
                string baseCall = CallsignIdentity.Base(call);
                return !string.Equals(baseCall, call, StringComparison.OrdinalIgnoreCase)
                       && _users.Contains(baseCall);
            }
        }

        // Downloads a fresh list when the cache is missing or older than RefreshEveryDays; otherwise
        // does nothing. Safe to fire-and-forget. A successful download replaces the cache atomically
        // and reloads the in-memory set.
        public static async Task RefreshIfStaleAsync(HttpClient http)
        {
            try
            {
                if (File.Exists(LocalPath)
                    && (DateTime.UtcNow - File.GetLastWriteTimeUtc(LocalPath)).TotalDays < RefreshEveryDays)
                    return;   // still fresh

                string csv = await http.GetStringAsync(DownloadUrl).ConfigureAwait(false);

                // Guard against a moved/parked URL or an error body corrupting the good cache: the real
                // list is large and comma-delimited.
                if (string.IsNullOrEmpty(csv) || csv.Length < 100000 || csv.IndexOf(',') < 0)
                    return;

                Directory.CreateDirectory(Path.GetDirectoryName(LocalPath));
                string tmp = LocalPath + ".new";
                File.WriteAllText(tmp, csv);
                if (File.Exists(LocalPath)) File.Delete(LocalPath);
                File.Move(tmp, LocalPath);

                LoadFromFile(LocalPath);
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        private static void LoadFromFile(string path)
        {
            // Sized for the file it is about to read (235,000 lines and growing): a set left to grow
            // by itself re-buckets everything it holds a dozen times on the way up.
            var set = new HashSet<string>(300000, StringComparer.OrdinalIgnoreCase);
            foreach (string line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                int comma = line.IndexOf(',');
                string call = (comma >= 0 ? line.Substring(0, comma) : line).Trim();
                // Skip an optional header row and any non-callsign junk (a real callsign has no space).
                if (call.Length == 0 || call.IndexOf(' ') >= 0) continue;
                // NOT upper-cased: the set ignores case, so converting every one of 235,000 callsigns
                // built a second string for nothing.
                set.Add(call);
            }
            lock (_gate)
            {
                _users = set;
            }
            _loaded = set.Count > 0;
        }
    }
}
