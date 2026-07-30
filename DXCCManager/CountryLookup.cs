using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace DXCCManager
{
    // The one place the rest of HolySuite asks "which country is this callsign?".
    //
    // Two databases answer, each doing what it is good at:
    //   Club Log (cty.xml)  decides WHICH ENTITY, because it knows the date: retired prefixes, ~31,000
    //                       full-callsign exceptions, and operations that never counted at all.
    //   cty.dat             supplies the WORDING and the ITU ZONE, neither of which Club Log has, and
    //                       answers on its own whenever Club Log has nothing to say.
    //
    // So a name written into the log is always cty.dat's spelling; Club Log only changes which country
    // is chosen. That matters because Club Log writes "FEDERAL REPUBLIC OF GERMANY" where the log
    // already holds "Fed. Rep. of Germany" - over a thousand QSOs would otherwise appear to change for
    // no reason at all.
    //
    // With no Club Log file present (no key, first run, download failed) every answer comes from
    // cty.dat alone, exactly as before.
    public class CountryLookup
    {
        private readonly EntityResolver cty;
        private readonly ClubLogData clubLog;

        // Club Log DXCC code -> the cty.dat entity (its primary prefix) that means the same country.
        private readonly Dictionary<int, string> ctyEntityByCode = new Dictionary<int, string>(400);

        // Entities Club Log knows and we could not line up with cty.dat. Empty today; if the ARRL adds
        // an entity and only one of the two databases has it yet, it lands here and the lookup simply
        // reports Club Log's own name for it.
        private readonly List<string> unbridged = new List<string>();

        // The handful of entities whose Club Log label cannot be matched to cty.dat automatically,
        // because the two projects label them differently (Club Log "R1F" vs cty.dat "R1FJ",
        // "FT5/W" vs "FT/w"). Keyed by ARRL entity code, which never changes. This list only ever
        // needs touching if the ARRL adds or renames an entity, not when a callsign is misreported.
        private static readonly Dictionary<int, string> KnownLabelDifferences = new Dictionary<int, string>
        {
            {   4, "3B6"   },  // Agalega & St. Brandon
            {  41, "FT/w"  },  // Crozet Island
            {  61, "R1FJ"  },  // Franz Josef Land
            { 131, "FT/x"  },  // Kerguelen Islands
            { 216, "HK0/a" },  // San Andres & Providencia
            { 238, "VP8/o" },  // South Orkney Islands
            { 241, "VP8/h" },  // South Shetland Islands
            { 246, "1A"    },  // Sov. Military Order of Malta
        };

        public CountryLookup(EntityResolver ctyDat, ClubLogData clubLogData)
        {
            if (ctyDat == null) throw new ArgumentNullException(nameof(ctyDat));
            cty = ctyDat;
            clubLog = clubLogData;
            if (clubLog != null) BuildEntityBridge();
        }

        // Loads both databases from their configured paths. Returns a lookup that works even if the
        // Club Log file is missing or unreadable.
        public static CountryLookup Create()
        {
            ClubLogData cl = null;
            try { cl = ClubLogData.Load(ClubLogData.DataFilePath); }
            catch { cl = null; }
            return new CountryLookup(new EntityResolver(), cl);
        }

        private static CountryLookup _shared;
        private static readonly object SharedLock = new object();

        // The instance the whole program shares, so no caller has to be handed the two databases.
        // Built on first use; building it parses cty.dat and (if present) Club Log's file, so the app
        // touches this once at startup rather than on the first keystroke.
        public static CountryLookup Shared
        {
            get { lock (SharedLock) { return _shared ?? (_shared = Create()); } }
        }

        // Drops the shared instance so the next caller reloads both databases - used after a refreshed
        // file has been downloaded.
        public static void Reset()
        {
            lock (SharedLock) { _shared = null; }
        }

        public bool ClubLogAvailable => clubLog != null;

        public DateTime ClubLogFileDateUtc => clubLog != null ? clubLog.FileDateUtc : DateTime.MinValue;

        // Entities Club Log has that we could not match to a cty.dat entity (diagnostic, for Options).
        public IReadOnlyList<string> UnbridgedEntities => unbridged;

        // A logged QSO's ADIF date ("yyyyMMdd") as a UTC instant, falling back to now when it cannot be
        // read. Keeps every caller from repeating the same parsing before a dated lookup.
        public static DateTime QsoDate(string adifDate)
        {
            DateTime when;
            if (!string.IsNullOrWhiteSpace(adifDate) &&
                DateTime.TryParseExact(adifDate.Trim(), "yyyyMMdd", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out when))
                return when;
            return DateTime.UtcNow;
        }

        // Today's answer - for live use (typing a call, cluster spots) where the date is now.
        public DXCC Resolve(string callsign)
        {
            return Resolve(callsign, DateTime.UtcNow);
        }

        // The answer as it stood on a given date. Pass the QSO's own date when resolving logged or
        // imported QSOs; that is the whole reason Club Log is consulted.
        public DXCC Resolve(string callsign, DateTime whenUtc)
        {
            DXCC fromCty = cty.GetDXCC(callsign);
            if (fromCty != null) fromCty.ResolvedBy = "cty.dat";

            if (clubLog == null) return fromCty;

            ClubLogMatch cl;
            try { cl = clubLog.Resolve(callsign, whenUtc); }
            catch { cl = null; }
            if (cl == null) return fromCty;

            // Club Log lists this call and date as an operation that never counted. "INVALID" is not a
            // country, so the country stays whatever cty.dat believes and only the flag is raised -
            // that way the logger can warn the operator without writing nonsense into the log.
            if (cl.Invalid)
            {
                if (fromCty == null) fromCty = new DXCC { Name = "Unknown", Entity = "-1", Continent = "XX", Prefixes = "" };
                fromCty.InvalidOperation = true;
                fromCty.DxccCode = 0;
                fromCty.ResolvedBy = "Club Log";
                return fromCty;
            }

            // Otherwise DXCC code 0 means there genuinely is no entity: a station at sea or in the air.
            // Reporting the home country instead would quietly invent a QSO that never counted.
            if (cl.DxccCode == 0)
            {
                bool maritime = cl.EntityName != null &&
                    cl.EntityName.IndexOf("MARITIME", StringComparison.OrdinalIgnoreCase) >= 0;
                return new DXCC
                {
                    Name = maritime ? "Maritime Mobile"
                                    : (string.IsNullOrEmpty(cl.EntityName) ? "Unknown" : cl.EntityName),
                    Entity = "0",
                    Continent = string.IsNullOrEmpty(cl.Continent) ? (fromCty != null ? fromCty.Continent : "XX") : cl.Continent,
                    Prefixes = "",
                    Locator = "",
                    CqZone = cl.CqZone,
                    ItuZone = 0,
                    DxccCode = 0,
                    ResolvedBy = "Club Log"
                };
            }

            // The usual case by far: both databases mean the same country, so keep cty.dat's answer
            // (its wording, its ITU zone) and merely record the entity code Club Log supplied.
            string ctyEntity;
            bool bridged = ctyEntityByCode.TryGetValue(cl.DxccCode, out ctyEntity);
            if (bridged && fromCty != null &&
                string.Equals(ctyEntity, fromCty.Entity, StringComparison.OrdinalIgnoreCase))
            {
                fromCty.DxccCode = cl.DxccCode;
                return fromCty;
            }

            // They disagree, or cty.dat had nothing. Take Club Log's country - but say it in cty.dat's
            // words whenever we can name the same entity there.
            DXCC result = bridged ? cty.GetDXCCbyEntityCode(ctyEntity) : null;
            if (result == null || result.Name == "Unknown")
            {
                result = new DXCC
                {
                    Name = cl.EntityName,
                    Entity = bridged ? ctyEntity : cl.DxccCode.ToString(),
                    Prefixes = "",
                    Locator = fromCty != null ? fromCty.Locator : "",
                    Continent = cl.Continent
                };
            }

            // cty.dat holds no ITU zone for an entity it did not match, so the QSO simply has none
            // rather than a borrowed one.
            if (!string.IsNullOrEmpty(cl.Continent)) result.Continent = cl.Continent;
            if (cl.CqZone > 0) result.CqZone = cl.CqZone;
            result.DxccCode = cl.DxccCode;
            result.InvalidOperation = cl.Invalid;
            result.ResolvedBy = "Club Log";
            return result;
        }

        // Continent only, for callers that want nothing else.
        public string GetContinent(string callsign, DateTime whenUtc)
        {
            DXCC d = Resolve(callsign, whenUtc);
            return d != null ? d.Continent : "XX";
        }

        // Lines Club Log's entity list up with cty.dat's, once, at construction. Three steps, cheapest
        // first: the two projects label most entities identically; where they don't, running Club Log's
        // label through cty.dat usually lands on the right entity, and the name comparison proves it;
        // the few that resist are listed in KnownLabelDifferences.
        private void BuildEntityBridge()
        {
            foreach (KeyValuePair<int, string> entity in clubLog.ActiveEntityPrefixes())
            {
                int code = entity.Key;
                string clubLogPrefix = entity.Value;

                string mapped;
                if (KnownLabelDifferences.TryGetValue(code, out mapped))
                {
                    ctyEntityByCode[code] = mapped;
                    continue;
                }

                // Same label in both databases?
                DXCC byLabel = cty.GetDXCCbyEntityCode(clubLogPrefix);
                if (byLabel != null && byLabel.Name != "Unknown")
                {
                    ctyEntityByCode[code] = byLabel.Entity;
                    continue;
                }

                // Otherwise resolve Club Log's label as if it were a callsign, and accept the result
                // only when both databases agree on the name - which is what makes this safe.
                DXCC byPrefix = cty.GetDXCC(clubLogPrefix);
                string clubLogName = clubLog.EntityNameOf(code);
                if (byPrefix != null && byPrefix.Name != "Unknown" &&
                    SameCountryName(byPrefix.Name, clubLogName))
                {
                    ctyEntityByCode[code] = byPrefix.Entity;
                    continue;
                }

                unbridged.Add(code + " " + clubLogPrefix + " " + clubLogName);
            }
        }

        // Two spellings of one country. The databases differ in case, punctuation and in whether they
        // write "Saint"/"St." and "Islands"/"Is.", so those are levelled before comparing.
        private static readonly Regex NonAlphaNumeric = new Regex("[^A-Z0-9]", RegexOptions.Compiled);

        private static bool SameCountryName(string a, string b)
        {
            return Flatten(a) == Flatten(b) && Flatten(a).Length > 0;
        }

        private static string Flatten(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            string up = s.ToUpperInvariant()
                         .Replace("SAINT", "ST")
                         .Replace("ISLANDS", "IS")
                         .Replace("ISLAND", "IS");
            return NonAlphaNumeric.Replace(up, string.Empty);
        }
    }
}
