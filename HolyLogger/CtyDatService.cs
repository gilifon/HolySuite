using System;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using DXCCManager;

namespace HolyLogger
{
    // Keeps the cty.dat entity database current. The resolver normally reads cty.dat from the
    // copy embedded in DXCCManager; this service instead keeps an *updatable* copy next to the
    // log database (in LocalAppData) and, in the background, downloads a newer release from
    // country-files.com (AD1C) when one is available. A downloaded update is written to disk and
    // takes effect on the next launch, so a running session is never disturbed mid-resolve.
    public static class CtyDatService
    {
        // AD1C's "Big CTY" cty.dat. Same file the program was built against.
        private const string DownloadUrl = "https://www.country-files.com/bigcty/cty.dat";

        // How long with no successful refresh before we consider the data stale and warn the user.
        private const int OverdueDays = 45;

        public enum CtyUpdateStatus
        {
            Unknown,    // not checked yet this session
            UpToDate,   // contacted the site; already current
            Updated,    // downloaded a newer file
            NoNetwork,  // could not reach the internet at all (transient — don't alarm)
            LinkFailed  // online, but the URL did not return a valid cty.dat (likely moved/removed)
        }

        // Result of the most recent update check this session (in-memory).
        public static CtyUpdateStatus LastStatus { get; private set; } = CtyUpdateStatus.Unknown;
        public static string LastUpdatedVersion { get; private set; } = "";

        // The download address, shown in the warning so the user knows what to look for.
        public static string SourceUrl => DownloadUrl;

        private static string _localPath;

        // Full path to the updatable cty.dat, in the same folder as logDB.db.
        public static string LocalPath
        {
            get
            {
                if (_localPath == null)
                {
                    Assembly asm = Assembly.GetExecutingAssembly();
                    var fvi = System.Diagnostics.FileVersionInfo.GetVersionInfo(asm.Location);
                    string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    _localPath = Path.Combine(appData, fvi.CompanyName, fvi.ProductName, "cty.dat");
                }
                return _localPath;
            }
        }

        // Points the resolver at the updatable file and, on first run, seeds it from the embedded
        // copy so there is always a real file on disk to compare against and update. Call this once
        // at startup BEFORE the first EntityResolver is created.
        public static void Initialize()
        {
            try
            {
                EntityResolver.DataFilePath = LocalPath;
                if (!File.Exists(LocalPath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(LocalPath));
                    string embedded = EntityResolver.GetEmbeddedCtyDat();
                    if (!string.IsNullOrEmpty(embedded))
                        File.WriteAllText(LocalPath, embedded);
                }
            }
            catch
            {
                // If anything goes wrong, the resolver simply falls back to the embedded copy.
            }
        }

        // The release date (yyyy-MM-dd) of the cty.dat currently on disk, or "".
        public static string LocalVersion()
        {
            try
            {
                if (File.Exists(LocalPath))
                    return EntityResolver.ParseVersion(File.ReadAllText(LocalPath));
            }
            catch { }
            return "";
        }

        // Downloads the latest cty.dat and, if it is newer and valid, replaces the local file.
        // Records the outcome in LastStatus and (on any successful contact) stamps the last-OK
        // time so staleness can be detected. networkAvailable lets us tell a transient offline
        // state apart from a broken/moved download link. Safe to fire-and-forget.
        public static async Task<CtyUpdateStatus> CheckForUpdateAsync(HttpClient http, bool networkAvailable)
        {
            try
            {
                string remote = await http.GetStringAsync(DownloadUrl).ConfigureAwait(false);

                // Validate the payload: it must be sizeable, carry a VER marker, and have many
                // entity records. If the URL now returns something else (a moved/parked page, an
                // error body), treat it as a broken link rather than corrupting the good file.
                if (string.IsNullOrEmpty(remote) || remote.Length < 50000
                    || EntityResolver.ParseVersion(remote).Length == 0
                    || CountPrimaryRecords(remote) < 300)
                {
                    LastStatus = CtyUpdateStatus.LinkFailed;
                    return LastStatus;
                }

                string remoteVer = EntityResolver.ParseVersion(remote);
                string localVer = LocalVersion();

                // yyyy-MM-dd compares chronologically as plain strings.
                if (string.Compare(remoteVer, localVer, StringComparison.Ordinal) > 0)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(LocalPath));
                    string tmp = LocalPath + ".new";
                    File.WriteAllText(tmp, remote);
                    if (File.Exists(LocalPath)) File.Delete(LocalPath);
                    File.Move(tmp, LocalPath);
                    LastUpdatedVersion = remoteVer;
                    LastStatus = CtyUpdateStatus.Updated;
                }
                else
                {
                    LastStatus = CtyUpdateStatus.UpToDate;
                }

                RecordSuccessfulCheck();
                return LastStatus;
            }
            catch
            {
                // No HTTP response at all. If the machine has network, the site/URL is the problem
                // (likely moved); otherwise it's just an offline moment and we stay quiet.
                LastStatus = networkAvailable ? CtyUpdateStatus.LinkFailed : CtyUpdateStatus.NoNetwork;
                return LastStatus;
            }
        }

        private static string LastOkPath => LocalPath + ".checked";

        private static void RecordSuccessfulCheck()
        {
            try { File.WriteAllText(LastOkPath, DateTime.UtcNow.ToString("o")); } catch { }
        }

        // When the country file was last confirmed current (downloaded or verified up-to-date).
        // Falls back to the file's own release date if we've never recorded a successful check.
        public static DateTime LastSuccessfulCheckUtc()
        {
            try
            {
                if (File.Exists(LastOkPath) && DateTime.TryParse(File.ReadAllText(LastOkPath), null,
                        System.Globalization.DateTimeStyles.RoundtripKind, out DateTime dt))
                    return dt.ToUniversalTime();
            }
            catch { }
            try
            {
                string v = LocalVersion();
                if (DateTime.TryParse(v, out DateTime vd)) return DateTime.SpecifyKind(vd, DateTimeKind.Utc);
            }
            catch { }
            return DateTime.MinValue;
        }

        public static bool IsUpdateOverdue()
        {
            return (DateTime.UtcNow - LastSuccessfulCheckUtc()).TotalDays > OverdueDays;
        }

        // A user-facing warning when the country file is stale, or null when all is well. The
        // wording differs for a confirmed broken link vs. merely being offline for a while.
        public static string UpdateWarning()
        {
            if (!IsUpdateOverdue()) return null;
            if (LastStatus == CtyUpdateStatus.LinkFailed)
                return "⚠ The DXCC country file could not be refreshed — AD1C's download address may have "
                     + "changed. Country data may be out of date. Please check country-files.com for the "
                     + "current cty.dat link.\n(" + DownloadUrl + ")";
            return "⚠ The DXCC country file has not been refreshed in over " + OverdueDays + " days. Connect "
                 + "to the internet to update. If it keeps failing while online, AD1C may have moved the "
                 + "download — check country-files.com.";
        }

        private static int CountPrimaryRecords(string text)
        {
            int count = 0;
            foreach (string line in text.Replace("\r\n", "\n").Split('\n'))
            {
                if (line.Length > 0 && !char.IsWhiteSpace(line[0]) && line.TrimEnd().EndsWith(":"))
                    count++;
            }
            return count;
        }
    }
}
