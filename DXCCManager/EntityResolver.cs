using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace DXCCManager
{
    // Resolves a callsign to its DXCC entity using AD1C's cty.dat (country-files.com),
    // the de-facto standard prefix/entity database used by contest loggers.
    //
    // cty.dat replaces the old hand-maintained prefix table. Each entity record is a
    // primary line of 8 colon-separated fields:
    //   Name : CQzone : ITUzone : Continent : Lat : Lon : GMToffset : PrimaryPrefix :
    // followed by indented alias lines (comma-separated, terminated by ';'). An alias
    // token starting with '=' is an EXACT full-callsign match (e.g. =VP6D for Ducie I.);
    // every other token is a prefix. Tokens may carry bracketed overrides
    // ((cq) [itu] <lat/lon> {grid} ~tz~) which are irrelevant to entity identity and stripped.
    //
    // Matching rule (standard cty.dat semantics): an exact-callsign match wins; otherwise
    // the LONGEST matching prefix wins. This is what makes VP6D resolve to Ducie while
    // VP6A resolves to Pitcairn — the bug the old "first match wins" table had. The primary
    // prefix counts as an alias of its own entity, ranked below every listed alias
    // (see primaryPrefixFallbacks).
    public class EntityResolver
    {
        private class CtyEntity
        {
            public string Name;
            public string Continent;
            public string PrimaryPrefix; // unique per entity; used as the DXCC.Entity key
            public double Lat;
            public double Lon; // standard convention: East-positive (cty.dat stores West-positive)
            public int CqZone;  // entity-default CQ zone
            public int ItuZone; // entity-default ITU zone
        }

        // A resolved match: the entity plus the EFFECTIVE zones for the matched prefix/callsign.
        // cty.dat lets a prefix override the entity-default zones with (cq) and [itu] annotations
        // (e.g. K0(4)[7] in the USA), so big multi-zone countries resolve to the right zone.
        private class CtyMatch
        {
            public CtyEntity Entity;
            public int Cq;
            public int Itu;
        }

        // Exact full-callsign matches (from "=CALL" aliases). Shared with the prefix table below, and
        // it MUST be: left out of the sharing it was silently empty on every resolver after the first,
        // and 521 callsigns in this operator's log changed their answer - 3D2C fell from Conway Reef
        // back to plain Fiji, 2O12W from Wales to Unknown. That is what the check is for.
        private Dictionary<string, CtyMatch> exactCalls;

        // Prefix -> match. Resolution picks the longest matching prefix.
        //
        // NOT readonly any more, and not built here: both of these come from the shared parse below
        // when one is already in hand. Everything that WRITES to them runs inside LoadCtyDat; from the
        // moment a resolver is constructed they are only ever read, which is what makes sharing them
        // safe rather than merely cheap.
        private Dictionary<string, CtyMatch> prefixMap;

        private int maxPrefixLength = 1;
        private List<CtyEntity> allEntities;

        // Entities whose primary prefix still has to be registered as a prefix. The format spec says
        // the alias lines carry "alias DXCC prefixes (including the primary one)", but 16 of ~340
        // records don't repeat it: Franz Josef Land labels itself R1FJ yet lists only RI1F, so the
        // call R1FJ fell all the way through to European Russia's one-letter alias R. These are
        // applied after the whole file is read, and only where no alias claimed the same key, so an
        // explicit alias always outranks the primary prefix no matter where the records happen to
        // sit in the file.
        private readonly List<CtyEntity> primaryPrefixFallbacks = new List<CtyEntity>(360);

        // When set to an existing file, the resolver loads cty.dat from there instead of the
        // copy embedded in this assembly. The app points this at an updatable AppData file so a
        // newer cty.dat (downloaded from country-files.com) takes effect without a rebuild.
        public static string DataFilePath { get; set; }

        // The cty.dat release date parsed from its "VERyyyymmdd" marker, formatted yyyy-MM-dd
        // (empty if the file has no marker). Lets the UI show how current the entity data is.
        public string Version { get; private set; } = "";

        // Number of DXCC entities currently loaded (WAE-only entries excluded).
        public int EntityCount => allEntities.Count;

        // Strips cty.dat per-alias override annotations, leaving the bare prefix/callsign.
        private static readonly Regex OverrideAnnotations =
            new Regex(@"\([^)]*\)|\[[^\]]*\]|<[^>]*>|\{[^}]*\}|~[^~]*~", RegexOptions.Compiled);

        // cty.dat IS READ ONCE, NOT ONCE PER RESOLVER.
        //
        // Reading it costs 31.9 MB of garbage and 84 ms - ninety times the size of the 350 KB file,
        // because the parse copies the whole text twice, splits it into an array of lines, and runs a
        // regular expression over every alias. That would be a fair price to pay once. It was being
        // paid SIX times in one run of HolyLogger: CountryLookup builds one, the main window builds one
        // of its own, the QSO editor, the Log Workshop and the Statistics window each hold a static
        // one, and the ADIF parser builds a fresh one per import. Two of those land during the start,
        // where about two seconds of the window not answering is the collector at work.
        //
        // The file cannot change while the program runs - and if it does (a newer cty.dat downloaded
        // from country-files.com), the key below carries its path, size and date, so the next resolver
        // built parses the new file instead of handing back the old one.
        private sealed class Parsed
        {
            public Dictionary<string, CtyMatch> ExactCalls;
            public Dictionary<string, CtyMatch> PrefixMap;
            public List<CtyEntity> AllEntities;
            public int MaxPrefixLength;
            public string Version;
        }

        private static Parsed _shared;
        private static string _sharedKey;
        private static readonly object SharedLock = new object();

        public EntityResolver()
        {
            string key = CurrentFileKey();

            // Held for the whole parse, not just the lookup: two threads arriving together would
            // otherwise both read the file and one of the two parses would be thrown away.
            lock (SharedLock)
            {
                if (_shared != null && string.Equals(_sharedKey, key, StringComparison.Ordinal))
                {
                    exactCalls = _shared.ExactCalls;
                    prefixMap = _shared.PrefixMap;
                    allEntities = _shared.AllEntities;
                    maxPrefixLength = _shared.MaxPrefixLength;
                    Version = _shared.Version;
                    return;
                }

                exactCalls = new Dictionary<string, CtyMatch>(2000, StringComparer.OrdinalIgnoreCase);
                prefixMap = new Dictionary<string, CtyMatch>(4000, StringComparer.OrdinalIgnoreCase);
                allEntities = new List<CtyEntity>(360);
                LoadCtyDat();

                _shared = new Parsed
                {
                    ExactCalls = exactCalls,
                    PrefixMap = prefixMap,
                    AllEntities = allEntities,
                    MaxPrefixLength = maxPrefixLength,
                    Version = Version
                };
                _sharedKey = key;
            }
        }

        // Which file the parse in hand was made from: its path, its size and when it was last written.
        // A cty.dat replaced under the program answers to a different key and is read afresh.
        private static string CurrentFileKey()
        {
            try
            {
                if (!string.IsNullOrEmpty(DataFilePath) && File.Exists(DataFilePath))
                {
                    var f = new FileInfo(DataFilePath);
                    return f.FullName + "|" + f.Length + "|" + f.LastWriteTimeUtc.Ticks;
                }
            }
            catch { }
            return "embedded";
        }

        private void LoadCtyDat()
        {
            // Prefer the external (updatable) file when present; otherwise the embedded default.
            string text = null;
            try
            {
                if (!string.IsNullOrEmpty(DataFilePath) && File.Exists(DataFilePath))
                    text = File.ReadAllText(DataFilePath);
            }
            catch { text = null; }
            if (string.IsNullOrEmpty(text)) text = ReadEmbeddedCtyDat();
            if (string.IsNullOrEmpty(text)) return;

            Version = ParseVersion(text);

            // A record is a primary line (no leading whitespace, ends with ':') followed by
            // alias lines until one ends with ';'. Walk the file accumulating each record.
            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            int i = 0;
            while (i < lines.Length)
            {
                string line = lines[i];
                if (line.Length == 0 || char.IsWhiteSpace(line[0]))
                {
                    i++;
                    continue;
                }

                string primary = line.TrimEnd();
                if (!primary.EndsWith(":"))
                {
                    i++;
                    continue;
                }

                CtyEntity entity = ParsePrimaryLine(primary);
                i++;

                // Gather alias text across the following indented lines, up to the ';' terminator.
                var aliasBuilder = new StringBuilder();
                while (i < lines.Length)
                {
                    string aliasLine = lines[i];
                    aliasBuilder.Append(aliasLine.Trim());
                    i++;
                    if (aliasLine.Contains(";")) break;
                }

                if (entity != null)
                {
                    allEntities.Add(entity);
                    RegisterAliases(entity, aliasBuilder.ToString());
                }
            }

            foreach (CtyEntity e in primaryPrefixFallbacks)
            {
                if (!prefixMap.ContainsKey(e.PrimaryPrefix))
                    AddPrefix(e.PrimaryPrefix, e, e.CqZone, e.ItuZone);
            }
        }

        // cty.dat encodes its release as a "VERyyyymmdd" token (smuggled in as a fake callsign).
        // Returns it as yyyy-MM-dd, or "" if absent/malformed.
        public static string ParseVersion(string ctyText)
        {
            if (string.IsNullOrEmpty(ctyText)) return "";
            Match m = Regex.Match(ctyText, @"VER(\d{4})(\d{2})(\d{2})");
            return m.Success ? $"{m.Groups[1].Value}-{m.Groups[2].Value}-{m.Groups[3].Value}" : "";
        }

        // The cty.dat content embedded in this assembly — used by the app to seed/restore the
        // external updatable copy.
        public static string GetEmbeddedCtyDat()
        {
            return ReadEmbeddedCtyDat();
        }

        private static string ReadEmbeddedCtyDat()
        {
            Assembly asm = Assembly.GetExecutingAssembly();
            // Resource name is RootNamespace + filename.
            string resourceName = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("cty.dat", StringComparison.OrdinalIgnoreCase));
            if (resourceName == null) return string.Empty;
            using (Stream s = asm.GetManifestResourceStream(resourceName))
            {
                if (s == null) return string.Empty;
                using (var reader = new StreamReader(s))
                    return reader.ReadToEnd();
            }
        }

        private CtyEntity ParsePrimaryLine(string primary)
        {
            // Split into the 8 fields; the trailing ':' yields a final empty element.
            string[] f = primary.Split(':');
            if (f.Length < 8) return null;

            double lat = ParseDouble(f[4]);
            double ctyLon = ParseDouble(f[5]); // cty.dat is West-positive
            string prefix = f[7].Trim();
            // A leading '*' marks a WAE-only entity that is NOT a separate DXCC entity
            // (e.g. Sicily, Shetland Is., Bear I., European Turkey). This is a DXCC-based
            // logger, so we skip these entirely; their callsigns then fall through to the
            // real DXCC parent via normal prefix matching (IT9 -> Italy, GM/s -> Scotland).
            if (prefix.StartsWith("*")) return null;

            string name = f[0].Trim();
            // With the WAE "European Turkey" split removed above, cty.dat's remaining Turkey
            // entity is named "Asiatic Turkey" — misleading for the whole country, so use the
            // standard DXCC name "Turkey".
            if (name == "Asiatic Turkey") name = "Turkey";

            return new CtyEntity
            {
                Name = name,
                Continent = f[3].Trim(),
                PrimaryPrefix = prefix,
                Lat = lat,
                Lon = -ctyLon,
                CqZone = ParseInt(f[1]),   // field 2: default CQ zone
                ItuZone = ParseInt(f[2])   // field 3: default ITU zone
            };
        }

        private void RegisterAliases(CtyEntity entity, string aliasText)
        {
            // Some primary prefixes are labels rather than prefixes (VP8/h, 3Y/b); no callsign can
            // match those, so only the plain ones are worth keeping as a fallback.
            if (!string.IsNullOrEmpty(entity.PrimaryPrefix) && entity.PrimaryPrefix.IndexOf('/') < 0)
                primaryPrefixFallbacks.Add(entity);

            aliasText = aliasText.TrimEnd(';');
            if (aliasText.Length == 0)
            {
                // No alias list: fall back to the primary prefix with the entity-default zones.
                AddPrefix(entity.PrimaryPrefix, entity, entity.CqZone, entity.ItuZone);
                return;
            }

            foreach (string raw in aliasText.Split(','))
            {
                string trimmed = raw.Trim();
                if (trimmed.Length == 0) continue;

                // Per-prefix zone overrides: (cq) and [itu]; default to the entity zones.
                int cq = entity.CqZone, itu = entity.ItuZone;
                Match cqm = Regex.Match(trimmed, @"\((\d+)\)");
                if (cqm.Success) int.TryParse(cqm.Groups[1].Value, out cq);
                Match itm = Regex.Match(trimmed, @"\[(\d+)\]");
                if (itm.Success) int.TryParse(itm.Groups[1].Value, out itu);

                string token = OverrideAnnotations.Replace(trimmed, string.Empty).Trim();
                if (token.Length == 0) continue;

                if (token[0] == '=')
                {
                    string call = token.Substring(1).Trim();
                    if (call.Length > 0)
                        exactCalls[call] = new CtyMatch { Entity = entity, Cq = cq, Itu = itu };
                }
                else
                {
                    AddPrefix(token, entity, cq, itu);
                }
            }
        }

        private void AddPrefix(string prefix, CtyEntity entity, int cq, int itu)
        {
            if (string.IsNullOrEmpty(prefix)) return;
            prefixMap[prefix] = new CtyMatch { Entity = entity, Cq = cq, Itu = itu };
            if (prefix.Length > maxPrefixLength) maxPrefixLength = prefix.Length;
        }

        private CtyMatch Resolve(string callsign)
        {
            return Resolve(callsign, out int ignored);
        }

        // matchedLength reports how many characters of the callsign the answer rests on, so a caller
        // holding a second opinion can tell a precise match from a one-letter fallback.
        private CtyMatch Resolve(string callsign, out int matchedLength)
        {
            matchedLength = 0;
            if (string.IsNullOrWhiteSpace(callsign)) return null;
            string call = callsign.Trim().ToUpperInvariant();

            // 1) Exact full-callsign match wins.
            if (exactCalls.TryGetValue(call, out CtyMatch exact))
            {
                matchedLength = call.Length;
                return exact;
            }

            // 2) A callsign with a stroke: match on the part that says WHERE THE STATION IS, which is
            //    not always the part at the front. See OperatingPart.
            string operating = OperatingPart(call);
            if (!string.Equals(operating, call, StringComparison.Ordinal))
            {
                if (exactCalls.TryGetValue(operating, out CtyMatch exactPart))
                {
                    matchedLength = operating.Length;
                    return exactPart;
                }
                CtyMatch fromPart = LongestPrefix(operating, out matchedLength);
                if (fromPart != null) return fromPart;

                // That side means nothing to cty.dat, so try the other one before giving up.
                string other = OtherPart(call);
                if (!string.IsNullOrEmpty(other))
                {
                    if (exactCalls.TryGetValue(other, out CtyMatch exactOther))
                    {
                        matchedLength = other.Length;
                        return exactOther;
                    }
                    CtyMatch fromOther = LongestPrefix(other, out matchedLength);
                    if (fromOther != null) return fromOther;
                }
                matchedLength = 0;      // neither side is known; fall back to the whole call
            }

            // 3) Otherwise the longest matching prefix of the callsign as written.
            return LongestPrefix(call, out matchedLength);
        }

        private CtyMatch LongestPrefix(string call, out int matchedLength)
        {
            matchedLength = 0;
            if (string.IsNullOrEmpty(call)) return null;
            int len = Math.Min(call.Length, maxPrefixLength);
            for (int l = len; l >= 1; l--)
            {
                string candidate = call.Substring(0, l);
                if (prefixMap.TryGetValue(candidate, out CtyMatch byPrefix))
                {
                    matchedLength = l;
                    return byPrefix;
                }
            }
            return null;
        }

        // WHICH SIDE OF THE STROKE SAYS WHERE THE STATION IS.
        //
        // A travelling operator adds a stroke and the prefix of the place they are in. Most of the world
        // writes the place first - KP4/W1ABC - and the United States traditionally writes the home call
        // first and the place after: W1AW/KP4. Both mean the same contact, in Puerto Rico, and the ARRL
        // counts both as Puerto Rico; that is the entire point of signing it.
        //
        // Matching only ever looked at the front of the string, so KP4/W1ABC was right and W1AW/KP4 was
        // read as plain W1AW - the United States. Every W1AW Centennial operation, every /KH6, /KL7,
        // /HR9 in a log came out as the operator's home country.
        //
        // The rule: throw away the endings that name no place, and of what is left the SHORTER part is
        // the location - a place prefix is short (KP4, HR9, W4, 9A) and a callsign is not. A tie keeps
        // the first, which is the older convention.
        internal static string OperatingPart(string call)
        {
            if (string.IsNullOrEmpty(call) || call.IndexOf('/') < 0) return call;

            // A PLACELESS ENDING IS AN ENDING, and only an ending. The list below holds single letters
            // that mean something about the station - M for mobile, P for portable - and several of
            // them are also perfectly good country prefixes at the FRONT of a callsign. M is the plainest
            // case: ON4CJK/M is a Belgian station, mobile, in Belgium, while M/ON4CJK is that same
            // operator transmitting from ENGLAND, whose prefix is M. Testing every part against the list
            // threw the England away and answered Belgium - the operator's home country, which is the
            // one thing the stroke exists to say they are not in.
            var parts = new List<string>();
            string[] raw = call.Split('/');
            for (int i = 0; i < raw.Length; i++)
            {
                string p = raw[i];
                if (p.Length == 0) continue;
                if (i > 0 && IsPlacelessSuffix(p)) continue;
                parts.Add(p);
            }

            if (parts.Count == 0) return call;
            if (parts.Count == 1) return parts[0];

            string first = parts[0], last = parts[parts.Count - 1];
            return last.Length < first.Length ? last : first;
        }

        // The side of the stroke that OperatingPart did NOT choose, for a caller whose first choice
        // meant nothing to either database. "P/ON4CJK" is the case: P is not a country prefix, and
        // being the shorter part it is chosen, so the callsign would resolve to nothing at all -
        // where before the placeless-suffix fix it at least came back Belgium. Trying the other side
        // when the first says nothing keeps that answer without weakening the rule.
        internal static string OtherPart(string call)
        {
            if (string.IsNullOrEmpty(call) || call.IndexOf('/') < 0) return null;

            var parts = new List<string>();
            string[] raw = call.Split('/');
            for (int i = 0; i < raw.Length; i++)
            {
                string p = raw[i];
                if (p.Length == 0) continue;
                if (i > 0 && IsPlacelessSuffix(p)) continue;
                parts.Add(p);
            }
            if (parts.Count < 2) return null;

            string chosen = OperatingPart(call);
            string first = parts[0], last = parts[parts.Count - 1];
            return string.Equals(chosen, first, StringComparison.Ordinal) ? last : first;
        }

        // The everyday endings that say something about the STATION, never about the country: mobile,
        // portable, maritime and aeronautical mobile, low power, a lighthouse, an alternate operator -
        // and a bare digit, which in the USA moves the station to another call area of the SAME country.
        private static bool IsPlacelessSuffix(string part)
        {
            switch (part)
            {
                case "M": case "P": case "MM": case "AM": case "QRP":
                case "A": case "B": case "J": case "N": case "R": case "LH":
                    return true;
            }

            // "/P" with a call-area digit stuck to it - IZ5TJD/P7 - is still portable. It has to be said
            // out loud because P5-P9 is North Korea's ITU block, so both databases read P7 as a country:
            // cty.dat lists "P5,P6,P7,P8,P9" and Club Log carries a prefix record for each, dated from
            // 1978. Neither is evidence of a station. Every DPRK operation Club Log knows is a P5 and is
            // written prefix-FIRST - P5/OH2AM, P5/4L4FN, P5/3Z9DX, P51BH - and P6 to P9 have never
            // appeared at all, so nothing that ends in one of them was ever on the air from Korea.
            // P0-P4 are deliberately NOT included: P2 (Papua New Guinea), P3 (Cyprus) and P4 (Aruba) are
            // prefixes people really do operate under.
            if (part.Length == 2 && part[0] == 'P' && part[1] >= '5' && part[1] <= '9') return true;

            foreach (char c in part) if (c < '0' || c > '9') return false;
            return true;      // all digits - a call-area change, not a country
        }

        public DXCC GetDXCC(string callsign)
        {
            CtyMatch m = Resolve(callsign, out int matchedLength);
            if (m != null)
            {
                CtyEntity e = m.Entity;
                return new DXCC
                {
                    Name = e.Name,
                    Continent = e.Continent,
                    Entity = e.PrimaryPrefix,
                    Prefixes = e.PrimaryPrefix,
                    Locator = LatLonToGrid(e.Lat, e.Lon),
                    CqZone = m.Cq,
                    ItuZone = m.Itu,
                    MatchedLength = matchedLength
                };
            }

            string up = (callsign ?? string.Empty).ToUpperInvariant();
            return new DXCC
            {
                Continent = "XX",
                Entity = "-1",
                Name = "Unknown",
                Prefixes = up.Length >= 2 ? up.Substring(0, 2) : up
            };
        }

        public DXCC GetDXCCbyEntityCode(string entityCode)
        {
            CtyEntity e = allEntities.FirstOrDefault(x =>
                string.Equals(x.PrimaryPrefix, entityCode, StringComparison.OrdinalIgnoreCase));
            if (e != null)
            {
                return new DXCC
                {
                    Name = e.Name,
                    Continent = e.Continent,
                    Entity = e.PrimaryPrefix,
                    Prefixes = e.PrimaryPrefix,
                    Locator = LatLonToGrid(e.Lat, e.Lon),
                    CqZone = e.CqZone,
                    ItuZone = e.ItuZone
                };
            }
            return new DXCC { Continent = "XX", Entity = "-1", Name = "Unknown", Prefixes = "" };
        }

        public string GetContinent(string callsign)
        {
            CtyMatch m = Resolve(callsign);
            return m != null ? m.Entity.Continent : "XX";
        }

        public string GetLocator(string callsign)
        {
            CtyMatch m = Resolve(callsign);
            return m != null ? LatLonToGrid(m.Entity.Lat, m.Entity.Lon) : "";
        }

        private static int ParseInt(string s)
        {
            int.TryParse((s ?? string.Empty).Trim(), out int v);
            return v;
        }

        public IReadOnlyList<string> GetAllEntityNames()
        {
            return allEntities
                .Select(d => d.Name)
                .Where(n => !string.IsNullOrEmpty(n) && n != "Unknown")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static double ParseDouble(string s)
        {
            double.TryParse((s ?? string.Empty).Trim(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out double v);
            return v;
        }

        // Coarse Maidenhead grid (6 chars) for the entity's reference coordinate. Used only as a
        // country-level map/azimuth fallback when no station grid is known.
        private static string LatLonToGrid(double lat, double lon)
        {
            // Clamp into valid range to avoid edge overflow.
            lon = Math.Max(-180, Math.Min(179.999, lon));
            lat = Math.Max(-90, Math.Min(89.999, lat));

            double adjLon = lon + 180.0;
            double adjLat = lat + 90.0;

            char f1 = (char)('A' + (int)(adjLon / 20));
            char f2 = (char)('A' + (int)(adjLat / 10));
            int sq1 = (int)((adjLon % 20) / 2);
            int sq2 = (int)(adjLat % 10);
            char s1 = (char)('a' + (int)(((adjLon % 2) / 2.0) * 24));
            char s2 = (char)('a' + (int)(((adjLat % 1) / 1.0) * 24));

            return string.Concat(f1, f2, (char)('0' + sq1), (char)('0' + sq2), s1, s2);
        }
    }
}
