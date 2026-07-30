using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using DXCCManager;

namespace HolyLogger
{
    // Keeps Club Log's date-aware prefix and exception database (cty.xml) on disk and current, the same
    // way CtyDatService does for cty.dat. Club Log can say which entity a callsign belonged to ON THE
    // DAY it was worked, which cty.dat cannot - see ClubLogData and CountryLookup.
    //
    // Nothing here is required for the logger to work: with no key, no network, or a refused download,
    // CountryLookup falls back to cty.dat alone.
    public static class ClublogCtyService
    {
        // The whole database, gzipped, updated daily. The key is appended at call time.
        private const string DownloadUrl = "https://cdn.clublog.org/cty.php?api=";

        // A bare "YYYY-MM-DD HH:MM:SS" (UTC) of Club Log's last change. Cheap to poll, so the 10 MB
        // download only happens when something actually changed.
        private const string LastChangeUrl = "https://clublog.org/cty_last_change.php";

        public enum ClublogCtyStatus
        {
            Unknown,    // not checked yet this session
            NoKey,      // no API key available - the feature is simply off
            UpToDate,   // asked Club Log; our copy is already current
            Updated,    // downloaded a newer file
            NoNetwork,  // no internet at all (transient - stay quiet)
            LinkFailed  // online, but nothing usable came back (bad key, or the endpoint moved)
        }

        public static ClublogCtyStatus LastStatus { get; private set; } = ClublogCtyStatus.Unknown;

        private static string _folder;

        // The folder that already holds logDB.db and cty.dat.
        private static string DataFolder
        {
            get
            {
                if (_folder == null)
                {
                    Assembly asm = Assembly.GetExecutingAssembly();
                    var fvi = System.Diagnostics.FileVersionInfo.GetVersionInfo(asm.Location);
                    string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    _folder = Path.Combine(appData, fvi.CompanyName, fvi.ProductName);
                }
                return _folder;
            }
        }

        public static string LocalPath => Path.Combine(DataFolder, "clublog_cty.xml");

        // Remembers which version we hold, so staleness is decided without parsing 10 MB of XML.
        private static string StampPath => LocalPath + ".stamp";

        // Points ClubLogData at our copy. Call once at startup, before the first CountryLookup is
        // built. Harmless when the file does not exist yet - the lookup then uses cty.dat only.
        public static void Initialize()
        {
            try { ClubLogData.DataFilePath = LocalPath; }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // The release date of the copy on disk, or "" when we have none. For the About/status text.
        public static string LocalVersion()
        {
            try
            {
                if (!File.Exists(LocalPath)) return "";
                string stamp = ReadStamp();
                if (stamp.Length > 0) return stamp;
                ClubLogData data = ClubLogData.Load(LocalPath);
                return data != null && data.FileDateUtc != DateTime.MinValue
                    ? data.FileDateUtc.ToString("yyyy-MM-dd")
                    : "";
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); return ""; }
        }

        // Downloads the database when Club Log has changed it since our copy, or when we have no copy.
        // Safe to fire-and-forget at startup: the running session is never disturbed (a new file is
        // read on the next launch) and every failure path leaves the existing good file alone.
        public static async Task<ClublogCtyStatus> CheckForUpdateAsync(HttpClient http, bool networkAvailable)
        {
            string key = ClublogKey.Current();
            if (key.Length == 0)
            {
                LastStatus = ClublogCtyStatus.NoKey;
                return LastStatus;
            }

            try
            {
                string remoteStamp = (await http.GetStringAsync(LastChangeUrl).ConfigureAwait(false) ?? "").Trim();

                if (File.Exists(LocalPath) && remoteStamp.Length > 0 && remoteStamp == ReadStamp())
                {
                    LastStatus = ClublogCtyStatus.UpToDate;
                    return LastStatus;
                }

                byte[] payload = await http.GetByteArrayAsync(DownloadUrl + key).ConfigureAwait(false);
                string xml = Decompress(payload);

                // A refused key or a moved endpoint answers with a short error page, not a database.
                if (xml == null || xml.Length < 200000
                    || xml.IndexOf("<clublog", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    LastStatus = ClublogCtyStatus.LinkFailed;
                    return LastStatus;
                }

                Directory.CreateDirectory(DataFolder);
                string tmp = LocalPath + ".new";
                File.WriteAllText(tmp, xml);

                // Parse before committing: a truncated download must never replace a working file.
                if (ClubLogData.Load(tmp) == null)
                {
                    try { File.Delete(tmp); } catch (Exception swallowed) { Log.Swallow(swallowed); }
                    LastStatus = ClublogCtyStatus.LinkFailed;
                    return LastStatus;
                }

                if (File.Exists(LocalPath)) File.Delete(LocalPath);
                File.Move(tmp, LocalPath);
                WriteStamp(remoteStamp);

                LastStatus = ClublogCtyStatus.Updated;
                return LastStatus;
            }
            catch
            {
                // No answer at all. If the machine has network the endpoint or the key is the problem;
                // otherwise it is just an offline moment and we stay quiet.
                LastStatus = networkAvailable ? ClublogCtyStatus.LinkFailed : ClublogCtyStatus.NoNetwork;
                return LastStatus;
            }
        }

        // Club Log serves the file gzipped. Depending on the handler, HttpClient may already have
        // unwrapped it, so gzip is tried only when the bytes actually start with the gzip marker.
        private static string Decompress(byte[] payload)
        {
            if (payload == null || payload.Length == 0) return null;

            if (payload.Length > 2 && payload[0] == 0x1F && payload[1] == 0x8B)
            {
                try
                {
                    using (var input = new MemoryStream(payload))
                    using (var gz = new GZipStream(input, CompressionMode.Decompress))
                    using (var reader = new StreamReader(gz))
                        return reader.ReadToEnd();
                }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
            }

            return Encoding.UTF8.GetString(payload);
        }

        private static string ReadStamp()
        {
            try
            {
                if (File.Exists(StampPath)) return File.ReadAllText(StampPath).Trim();
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
            return string.Empty;
        }

        private static void WriteStamp(string stamp)
        {
            try { File.WriteAllText(StampPath, stamp ?? string.Empty); }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }
    }
}
