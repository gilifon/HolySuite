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
        public int MatchedLength;    // how many characters of the callsign the match covered
        public bool Historic;        // no record covered the date; this is the last one that did
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
            // The day the entity stopped existing. Only deleted entities carry one, and it is what
            // lets a country NAME be judged against a QSO's date: "Germany" is entity 81 for a contact
            // made in 1970 and cannot possibly be for one made in 2020.
            string end = Value(e, "end");
            if (!string.IsNullOrEmpty(end)) endByCode[adif] = ParseDate(end, DateTime.MaxValue);
        }

        // DXCC code -> the day the entity ceased to exist. Absent for entities that still do.
        private readonly Dictionary<int, DateTime> endByCode = new Dictionary<int, DateTime>(100);

        // The day this entity stopped existing, or DateTime.MaxValue when it still does.
        public DateTime EntityEndUtc(int dxccCode)
        {
            DateTime end;
            return endByCode.TryGetValue(dxccCode, out end) ? end : DateTime.MaxValue;
        }

        // The active (not deleted) entities, as code/name/prefix triples — used to build the bridge
        // between Club Log's entity list and cty.dat's.
        // Every entity Club Log knows, by NUMBER, with its name and whether it has been deleted.
        //
        // The number is the identity. A name can be spelled two ways by two databases and re-spelled by
        // either of them next year; the ADIF entity number is fixed, unique, and never reused - Blenheim
        // Reef is 23 whether or not anything still calls it that, and no deleted entity shares a number
        // with a live one. Counting worked countries by number instead of by name is what this exists for.
        public IEnumerable<EntityInfo> AllEntities()
        {
            foreach (var pair in deletedByCode)
            {
                string name;
                nameByCode.TryGetValue(pair.Key, out name);
                yield return new EntityInfo
                {
                    Code = pair.Key,
                    Name = name ?? string.Empty,
                    Deleted = pair.Value,
                };
            }
        }

        public struct EntityInfo
        {
            public int Code;
            public string Name;
            public bool Deleted;
        }

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
            if (hit != null) return ToMatch(hit, true, call.Length);

            // THE PART THAT SAYS WHERE THE STATION IS, which is not always at the front: 9H5G/C6A is a
            // Maltese station in the Bahamas, VA7CD/DU7 a Canadian in the Philippines. Matching from the
            // front of the whole string answered Malta and Canada - the operator's home country, which
            // is the one thing the stroke exists to say they are NOT in. cty.dat's side of the lookup
            // learned this; this side had not, and Club Log's answer outranks cty.dat's, so it decided.
            string operating = EntityResolver.OperatingPart(call);
            if (!string.Equals(operating, call, StringComparison.Ordinal))
            {
                // ...but the operating part is a PREFIX, not the station's callsign, and the exception
                // list is a list of callsigns. Club Log registers the bare prefixes PJ2, PJ4 and PJ7 as
                // INVALID callsigns - true enough of anyone who logs "PJ4" on its own, and nothing at
                // all to do with PJ4/W9NJY. Honouring it made Club Log say "invalid operation", which
                // sends the country back to cty.dat, and cty.dat knows only today's prefixes: 24 QSOs
                // made before the 2010 split came back Bonaire / Curacao / Sint Maarten instead of the
                // Netherlands Antilles they were worked in. An exception that names no entity is
                // therefore not an answer about the operating part - fall through to the prefix table,
                // which carries the dates and gets the split right.
                hit = Pick(exceptions, operating, whenUtc);
                if (hit != null && hit.Adif != 0) return ToMatch(hit, true, operating.Length);

                ClubLogMatch fromPart = LongestPrefix(operating, whenUtc);
                if (fromPart != null) return fromPart;
            }

            // Otherwise the longest prefix that was valid on the day, same rule as cty.dat.
            return LongestPrefix(call, whenUtc);
        }

        private ClubLogMatch LongestPrefix(string call, DateTime whenUtc)
        {
            int len = Math.Min(call.Length, maxPrefixLength);
            for (int l = len; l >= 1; l--)
            {
                Record hit = Pick(prefixes, call.Substring(0, l), whenUtc);
                if (hit != null) return ToMatch(hit, false, l);
            }
            return null;
        }

        // Last resort: the last country a callsign is KNOWN to have belonged to, when nothing covers the
        // date. Prefixes are dropped from both databases once they stop being issued - 4N (Serbia) and
        // the Olympic-year 2O (England) are gone - which leaves such a call nameless even though its
        // country is perfectly well known; it simply belongs to the past.
        //
        // Two limits keep this honest. Only records that name a real entity are kept, because a lapsed
        // "invalid operation" window says nothing about later use of the callsign. And only records that
        // had already begun by the date asked about, so a 1993 QSO is never answered from a 2015 record.
        // The caller must still refuse this whenever anything current names the callsign, otherwise a
        // block that has been reassigned (3C was Canada until 1967, and is Equatorial Guinea now) would
        // be answered from its history.
        public ClubLogMatch ResolveHistoric(string callsign, DateTime whenUtc)
        {
            if (string.IsNullOrWhiteSpace(callsign)) return null;
            string call = callsign.Trim().ToUpperInvariant();

            // A full-callsign record still comes first: it is about this station, not a block.
            ClubLogMatch found = LastKnown(exceptions, call, whenUtc, true, call.Length);
            if (found != null) return found;

            // Among prefixes it is the FRESHEST record that wins, not the longest key - the longer key
            // may have lapsed decades earlier, and then the shorter one is the newer fact. 4N25K is the
            // case: "4N2" was Croatia until 1992, while "4N" was Serbia until 2013, so Serbia is what is
            // last known about the callsign. Key length only breaks ties.
            Record best = null;
            int bestLength = 0;
            int len = Math.Min(call.Length, maxPrefixLength);
            for (int l = len; l >= 1; l--)
            {
                Record candidate = LastKnownRecord(prefixes, call.Substring(0, l), whenUtc);
                if (candidate == null) continue;
                if (best == null || candidate.End > best.End)
                {
                    best = candidate;
                    bestLength = l;
                }
            }
            if (best == null) return null;

            ClubLogMatch m = ToMatch(best, false, bestLength);
            m.Historic = true;
            return m;
        }

        private ClubLogMatch LastKnown(Dictionary<string, List<Record>> from, string key, DateTime whenUtc,
                                       bool exactCall, int matchedLength)
        {
            Record best = LastKnownRecord(from, key, whenUtc);
            if (best == null) return null;
            ClubLogMatch m = ToMatch(best, exactCall, matchedLength);
            m.Historic = true;
            return m;
        }

        private static Record LastKnownRecord(Dictionary<string, List<Record>> from, string key, DateTime whenUtc)
        {
            List<Record> list;
            if (!from.TryGetValue(key, out list)) return null;

            Record best = null;
            foreach (Record r in list)
            {
                if (r.Adif <= 0) continue;          // no entity to remember
                if (r.Start > whenUtc) continue;    // did not exist yet at the date asked about
                if (best == null || r.End > best.End) best = r;
            }
            return best;
        }

        private static Record Pick(Dictionary<string, List<Record>> from, string key, DateTime whenUtc)
        {
            List<Record> list;
            if (!from.TryGetValue(key, out list)) return null;
            foreach (Record r in list)
                if (whenUtc >= r.Start && whenUtc <= r.End) return r;
            return null;
        }

        private ClubLogMatch ToMatch(Record r, bool exactCall, int matchedLength)
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
                ExactCall = exactCall,
                MatchedLength = matchedLength
            };
        }

        // EVERY entity Club Log lists, name and code, deleted ones included. The deleted entities are
        // the whole point: an operator who has been licensed for forty years has QSOs with countries
        // that stopped existing - Netherlands Antilles, Czechoslovakia, the Canal Zone - and those
        // contacts still count for awards. Their names are the only way to recognise them in a log,
        // since cty.dat carries no deleted entity at all.
        public IEnumerable<KeyValuePair<int, string>> AllEntityNames()
        {
            foreach (var pair in nameByCode) yield return pair;
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
