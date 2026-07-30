using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml;
using System.Xml.Linq;

namespace DXCCManager
{
    // The result of a Club Log lookup. DxccCode 0 means "no DXCC entity at that date": Club Log uses
    // it both for maritime mobile and for operations it has judged invalid (pirates, busted calls).
    public class ClubLogMatch
    {
        public string EntityName;    // Club Log's own spelling, e.g. "UNITED STATES OF AMERICA"
        public int DxccCode;         // ARRL/ADIF entity code — the same code LoTW reports
        public int CqZone;
        public string Continent;
        public bool Invalid;         // listed as an operation that never counted for awards
        public bool DeletedEntity;   // the entity itself has been deleted from the DXCC list
        public bool ExactCall;       // matched a full-callsign exception, not merely a prefix
    }

    // A date-aware DXCC lookup built from Club Log's prefix and exception database (cty.xml, by G7VJR).
    //
    // cty.dat describes the world as it is TODAY: when a prefix stops being issued AD1C removes it, so
    // a 2007 QSO with 4N1DV or a 2012 QSO with the Olympic prefix 2O12L cannot be named at all any
    // more. Club Log instead stores every record with a validity window, so a callsign is resolved
    // against the date it was actually worked — PJ2/LY4F in 2007 was Netherlands Antilles, not today's
    // Curacao. It also carries tens of thousands of full-callsign exceptions and a list of operations
    // that were never valid, neither of which cty.dat has any equivalent for.
    //
    // This class is data only: it never downloads anything and knows nothing about API keys (see
    // ClubLogService in HolyLogger). It does not replace EntityResolver either — Club Log has no ITU
    // zone at all and spells entity names its own way, so cty.dat remains the source of both.
    public class ClubLogData
    {
        // One dated record: a prefix, or a full-callsign exception.
        private class Record
        {
            public string Entity;
            public int Adif;
            public int CqZone;
            public string Continent;
            public DateTime Start;   // MinValue when the record has no start date
            public DateTime End;     // MaxValue when the record is still current
        }

        private readonly Dictionary<string, List<Record>> exceptions =
            new Dictionary<string, List<Record>>(32000, StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, List<Record>> prefixes =
            new Dictionary<string, List<Record>>(5000, StringComparer.OrdinalIgnoreCase);

        // DXCC code -> is this entity deleted from the DXCC list. Club Log states it per entity, which
        // saves us guessing from a hand-kept table.
        private readonly Dictionary<int, bool> deletedByCode = new Dictionary<int, bool>(500);

        // DXCC code -> Club Log's entity name, for codes we meet without a name (rare).
        private readonly Dictionary<int, string> nameByCode = new Dictionary<int, string>(500);

        // DXCC code -> the prefix Club Log uses to label the entity. CountryLookup needs it to line
        // Club Log's entities up with cty.dat's.
        private readonly Dictionary<int, string> prefixByCode = new Dictionary<int, string>(500);

        private int maxPrefixLength = 1;

        // Where the downloaded copy lives. Set once at startup (see ClublogCtyService) so every
        // project can reach the same file without being handed a path, exactly as with
        // EntityResolver.DataFilePath. Empty means "we have no Club Log data" and every lookup
        // falls back to cty.dat.
        public static string DataFilePath { get; set; }

        // When Club Log generated the file (its root <clublog date="..."> attribute).
        public DateTime FileDateUtc { get; private set; }

        public int EntityCount => deletedByCode.Count;
        public int PrefixCount => prefixes.Count;
        public int ExceptionCount => exceptions.Count;

        // Reads a cty.xml. Returns null if the file is missing, unreadable or not a Club Log file, so
        // a bad download can never take the country lookup down with it.
        public static ClubLogData Load(string xmlPath)
        {
            try
            {
                if (string.IsNullOrEmpty(xmlPath) || !File.Exists(xmlPath)) return null;

                var data = new ClubLogData();
                var settings = new XmlReaderSettings
                {
                    IgnoreWhitespace = true,
                    IgnoreComments = true,
                    DtdProcessing = DtdProcessing.Prohibit
                };

                // Streamed a record at a time: the file is around 10 MB of XML and there is no reason
                // to hold a DOM of it in memory.
                using (XmlReader r = XmlReader.Create(xmlPath, settings))
                {
                    r.Read();
                    while (!r.EOF)
                    {
                        if (r.NodeType != XmlNodeType.Element)
                        {
                            if (!r.Read()) break;
                            continue;
                        }

                        // Note: each of these cases consumes the whole record element, so <prefix>
                        // inside an <entity> is never seen out here as a prefix record of its own.
                        switch (r.Name)
                        {
                            case "clublog":
                                data.FileDateUtc = ParseDate(r.GetAttribute("date"), DateTime.MinValue);
                                r.Read();
                                break;
                            case "entity":
                                data.AddEntity((XElement)XNode.ReadFrom(r));
                                break;
                            case "exception":
                                data.AddRecord((XElement)XNode.ReadFrom(r), data.exceptions);
                                break;
                            case "prefix":
                                data.AddRecord((XElement)XNode.ReadFrom(r), data.prefixes);
                                break;
                            // invalid_operations lists the very same records as the INVALID
                            // exceptions (identical record numbers), so there is nothing extra there.
                            default:
                                r.Read();
                                break;
                        }
                    }
                }

                return data.prefixes.Count > 0 ? data : null;
            }
            catch
            {
                return null;
            }
        }

        private void AddEntity(XElement e)
        {
            int adif = ParseInt(Value(e, "adif"));
            if (adif <= 0) return;
            deletedByCode[adif] = string.Equals(Value(e, "deleted"), "true", StringComparison.OrdinalIgnoreCase);
            string name = Value(e, "name");
            if (!string.IsNullOrEmpty(name)) nameByCode[adif] = name;
            string prefix = Value(e, "prefix");
            if (!string.IsNullOrEmpty(prefix)) prefixByCode[adif] = prefix;
        }

        // The active (not deleted) entities, as code/name/prefix triples — used to build the bridge
        // between Club Log's entity list and cty.dat's.
        public IEnumerable<KeyValuePair<int, string>> ActiveEntityPrefixes()
        {
            foreach (var pair in prefixByCode)
            {
                bool deleted;
                if (deletedByCode.TryGetValue(pair.Key, out deleted) && deleted) continue;
                yield return pair;
            }
        }

        private void AddRecord(XElement e, Dictionary<string, List<Record>> into)
        {
            string call = Value(e, "call");
            if (string.IsNullOrEmpty(call)) return;
            call = call.Trim().ToUpperInvariant();

            var rec = new Record
            {
                Entity = Value(e, "entity"),
                Adif = ParseInt(Value(e, "adif")),
                CqZone = ParseInt(Value(e, "cqz")),
                Continent = Value(e, "cont"),
                Start = ParseDate(Value(e, "start"), DateTime.MinValue),
                End = ParseDate(Value(e, "end"), DateTime.MaxValue)
            };

            List<Record> list;
            if (!into.TryGetValue(call, out list))
            {
                list = new List<Record>(1);
                into[call] = list;
            }
            list.Add(rec);

            if (into == prefixes && call.Length > maxPrefixLength) maxPrefixLength = call.Length;
        }

        // Resolves a callsign as it stood on a given date. Returns null when Club Log has nothing to
        // say, which is the caller's cue to fall back to cty.dat.
        public ClubLogMatch Resolve(string callsign, DateTime whenUtc)
        {
            if (string.IsNullOrWhiteSpace(callsign)) return null;
            string call = callsign.Trim().ToUpperInvariant();

            // A full-callsign exception always wins — that is the whole point of the exception list.
            Record hit = Pick(exceptions, call, whenUtc);
            if (hit != null) return ToMatch(hit, true);

            // Otherwise the longest prefix that was valid on the day, same rule as cty.dat.
            int len = Math.Min(call.Length, maxPrefixLength);
            for (int l = len; l >= 1; l--)
            {
                hit = Pick(prefixes, call.Substring(0, l), whenUtc);
                if (hit != null) return ToMatch(hit, false);
            }
            return null;
        }

        private static Record Pick(Dictionary<string, List<Record>> from, string key, DateTime whenUtc)
        {
            List<Record> list;
            if (!from.TryGetValue(key, out list)) return null;
            foreach (Record r in list)
                if (whenUtc >= r.Start && whenUtc <= r.End) return r;
            return null;
        }

        private ClubLogMatch ToMatch(Record r, bool exactCall)
        {
            bool deleted;
            return new ClubLogMatch
            {
                EntityName = r.Entity ?? string.Empty,
                DxccCode = r.Adif,
                CqZone = r.CqZone,
                Continent = r.Continent ?? string.Empty,
                // Club Log files both maritime mobile and bogus operations under DXCC code 0; only the
                // ones it names INVALID are a claim that the QSO never counted.
                Invalid = r.Adif == 0 && string.Equals(r.Entity, "INVALID", StringComparison.OrdinalIgnoreCase),
                DeletedEntity = deletedByCode.TryGetValue(r.Adif, out deleted) && deleted,
                ExactCall = exactCall
            };
        }

        // Club Log's name for a DXCC code, or "" — handy when a QSO carries only the code (from LoTW).
        public string EntityNameOf(int dxccCode)
        {
            string name;
            return nameByCode.TryGetValue(dxccCode, out name) ? name : string.Empty;
        }

        public bool IsDeletedEntity(int dxccCode)
        {
            bool deleted;
            return deletedByCode.TryGetValue(dxccCode, out deleted) && deleted;
        }

        private static string Value(XElement parent, string child)
        {
            XElement e = parent.Element(child);
            return e == null ? string.Empty : e.Value.Trim();
        }

        private static int ParseInt(string s)
        {
            int v;
            int.TryParse((s ?? string.Empty).Trim(), out v);
            return v;
        }

        // Club Log writes ISO timestamps with a UTC offset. Everything is compared in UTC so a QSO
        // logged near a record's boundary is not thrown to the wrong side of it by a local time zone.
        private static DateTime ParseDate(string s, DateTime fallback)
        {
            if (string.IsNullOrWhiteSpace(s)) return fallback;
            DateTime dt;
            if (DateTime.TryParse(s.Trim(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out dt))
                return dt;
            return fallback;
        }
    }
}
