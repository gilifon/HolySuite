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
                                data.ReadFields(r);
                                data.AddEntity();
                                break;
                            case "exception":
                                data.ReadFields(r);
                                data.AddRecord(data.exceptions);
                                break;
                            case "prefix":
                                data.ReadFields(r);
                                data.AddRecord(data.prefixes);
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

        // ONE RECORD'S FIELDS, READ STRAIGHT OFF THE READER.
        //
        // Every record used to be turned into an XElement first - a little tree of objects per record,
        // with a node for the record, a node for each of its seven fields and a text node inside each
        // of those - and then thrown away as soon as the seven values had been copied out of it. For a
        // 10 MB file that came to 25.6 MB of garbage and about 310 ms, on the thread that draws the
        // window, at the moment the window is trying to appear.
        //
        // The reader already has the values. These fields hold the current record while it is being
        // read; they belong to the load and nothing else touches them.
        private string fCall, fEntity, fAdif, fCqz, fCont, fStart, fEnd, fName, fPrefix, fDeleted;

        private void ReadFields(XmlReader r)
        {
            fCall = fEntity = fAdif = fCqz = fCont = fStart = fEnd = fName = fPrefix = fDeleted = null;

            string record = r.Name;
            if (r.IsEmptyElement) { r.Read(); return; }

            r.Read();                                   // into the record
            while (!r.EOF)
            {
                if (r.NodeType == XmlNodeType.EndElement && r.Name == record) { r.Read(); return; }

                if (r.NodeType != XmlNodeType.Element) { r.Read(); continue; }

                string field = r.Name;
                string text;
                if (r.IsEmptyElement) { text = string.Empty; r.Read(); }
                else text = r.ReadElementContentAsString();   // leaves the reader past the end tag

                switch (field)
                {
                    case "call":    fCall = text;    break;
                    case "entity":  fEntity = text;  break;
                    case "adif":    fAdif = text;    break;
                    case "cqz":     fCqz = text;     break;
                    case "cont":    fCont = text;    break;
                    case "start":   fStart = text;   break;
                    case "end":     fEnd = text;     break;
                    case "name":    fName = text;    break;
                    case "prefix":  fPrefix = text;  break;
                    case "deleted": fDeleted = text; break;
                }
            }
        }

        private static string Field(string v) { return v == null ? string.Empty : v.Trim(); }

        private void AddEntity()
        {
            int adif = ParseInt(Field(fAdif));
            if (adif <= 0) return;
            deletedByCode[adif] = string.Equals(Field(fDeleted), "true", StringComparison.OrdinalIgnoreCase);
            string name = Field(fName);
            if (!string.IsNullOrEmpty(name)) nameByCode[adif] = name;
            string prefix = Field(fPrefix);
            if (!string.IsNullOrEmpty(prefix)) prefixByCode[adif] = prefix;
            // The day the entity stopped existing. Only deleted entities carry one, and it is what
            // lets a country NAME be judged against a QSO's date: "Germany" is entity 81 for a contact
            // made in 1970 and cannot possibly be for one made in 2020.
            string end = Field(fEnd);
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

        private void AddRecord(Dictionary<string, List<Record>> into)
        {
            string call = Field(fCall);
            if (string.IsNullOrEmpty(call)) return;
            call = call.ToUpperInvariant();

            var rec = new Record
            {
                Entity = Field(fEntity),
                Adif = ParseInt(Field(fAdif)),
                CqZone = ParseInt(Field(fCqz)),
                Continent = Field(fCont),
                Start = ParseDate(Field(fStart), DateTime.MinValue),
                End = ParseDate(Field(fEnd), DateTime.MaxValue)
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

        // EVERY RECORD CLUB LOG HOLDS FOR THIS EXACT CALLSIGN BEGINS AFTER THE DATE ASKED ABOUT -
        // in other words, on that day this callsign had not yet been used for the thing Club Log
        // knows it for. False when Club Log has no record of the callsign at all.
        //
        // This exists to tell two look-alike cases apart, because cty.dat's exception list carries no
        // dates and so cannot tell them apart itself:
        //
        //   R1FJ  - Club Log's record ran until January 2010 and cty.dat still files R1FJ under Franz
        //           Josef Land. The record is in the PAST; cty.dat is the fresher fact and must win.
        //   K4A   - Club Log holds three records, August 2014 and twice in 2026. For a QSO on 18
        //           February 2008 every one of them is in the FUTURE, so cty.dat's undated =K4A entry
        //           is describing a use of the callsign that had not happened yet and cannot be an
        //           answer about 2008.
        //
        // Only "all of them start later" separates the second from the first.
        public bool CallsignRecordsAllStartAfter(string callsign, DateTime whenUtc)
        {
            if (string.IsNullOrWhiteSpace(callsign)) return false;
            string call = callsign.Trim().ToUpperInvariant();

            List<Record> list;
            if (!exceptions.TryGetValue(call, out list) || list == null || list.Count == 0)
            {
                // Same reach as Resolve: for a stroke callsign it is the operating part that is used.
                string operating = EntityResolver.OperatingPart(call);
                if (string.Equals(operating, call, StringComparison.Ordinal)) return false;
                if (!exceptions.TryGetValue(operating, out list) || list == null || list.Count == 0)
                    return false;
            }

            foreach (Record r in list)
                if (r.Start <= whenUtc) return false;

            return true;
        }

        // A LONGER RECORD CLUB LOG HOLDS THAT DID NOT APPLY ON THE DAY.
        //
        // "Club Log matched CQ" is true and unhelpful: it leaves the operator wondering whether Club
        // Log has never heard of CQ8. It has - CQ8 is Azores from 1 June 2009 - and the QSO is from
        // 1991, which is the entire explanation. Without this the answer looks like ignorance when it
        // is actually a date.
        //
        // Longer than whatever Club Log DID match, or it would report the very record that answered.
        public class NearMiss
        {
            public string Key;
            public string EntityName;
            public int DxccCode;
            public DateTime Start;
            public DateTime End;
        }

        public NearMiss LongerRecordNotInEffect(string callsign, DateTime whenUtc, int matchedLength)
        {
            if (string.IsNullOrWhiteSpace(callsign)) return null;
            string call = callsign.Trim().ToUpperInvariant();
            if (call.IndexOf('/') >= 0) return null;    // the lookup key is not the front of the call

            int longest = Math.Min(call.Length, Math.Max(maxPrefixLength, call.Length));
            for (int l = longest; l > Math.Max(matchedLength, 0); l--)
            {
                string key = call.Substring(0, l);

                // A full-callsign exception counts too - that is where a special-event call lives.
                Record found = Nearest(exceptions, key, whenUtc) ?? Nearest(prefixes, key, whenUtc);
                if (found == null) continue;

                return new NearMiss
                {
                    Key = key,
                    EntityName = found.Entity ?? string.Empty,
                    DxccCode = found.Adif,
                    Start = found.Start,
                    End = found.End
                };
            }
            return null;
        }

        // The record for this key whose window sits closest to the date - and only when NO record for
        // it covers the date, because a key that answered is not a near miss.
        private static Record Nearest(Dictionary<string, List<Record>> from, string key, DateTime whenUtc)
        {
            List<Record> list;
            if (!from.TryGetValue(key, out list) || list == null || list.Count == 0) return null;

            Record best = null;
            TimeSpan bestGap = TimeSpan.MaxValue;
            foreach (Record r in list)
            {
                if (whenUtc >= r.Start && whenUtc <= r.End) return null;   // it DID apply; not a miss
                TimeSpan gap = whenUtc < r.Start ? r.Start - whenUtc : whenUtc - r.End;
                if (best == null || gap < bestGap) { best = r; bestGap = gap; }
            }
            return best;
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
