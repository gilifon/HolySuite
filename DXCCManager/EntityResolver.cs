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
    // VP6A resolves to Pitcairn — the bug the old "first match wins" table had.
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

        // Exact full-callsign matches (from "=CALL" aliases).
        private readonly Dictionary<string, CtyMatch> exactCalls =
            new Dictionary<string, CtyMatch>(2000, StringComparer.OrdinalIgnoreCase);

        // Prefix -> match. Resolution picks the longest matching prefix.
        private readonly Dictionary<string, CtyMatch> prefixMap =
            new Dictionary<string, CtyMatch>(4000, StringComparer.OrdinalIgnoreCase);

        private int maxPrefixLength = 1;
        private readonly List<CtyEntity> allEntities = new List<CtyEntity>(360);

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

        public EntityResolver()
        {
            LoadCtyDat();
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
            if (string.IsNullOrWhiteSpace(callsign)) return null;
            string call = callsign.Trim().ToUpperInvariant();

            // 1) Exact full-callsign match wins.
            if (exactCalls.TryGetValue(call, out CtyMatch exact)) return exact;

            // 2) Otherwise the longest matching prefix wins.
            int len = Math.Min(call.Length, maxPrefixLength);
            for (int l = len; l >= 1; l--)
            {
                string candidate = call.Substring(0, l);
                if (prefixMap.TryGetValue(candidate, out CtyMatch byPrefix))
                    return byPrefix;
            }
            return null;
        }

        public DXCC GetDXCC(string callsign)
        {
            CtyMatch m = Resolve(callsign);
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
                    ItuZone = m.Itu
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
