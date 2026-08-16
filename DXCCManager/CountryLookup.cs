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

        // The same bridge read backwards: cty.dat entity -> ARRL entity code. cty.dat carries no entity
        // numbers of its own, so whenever cty.dat's answer is the one returned - which is exactly what
        // the specificity rule below does for R1FJ and its like - this is the only way the number can be
        // filled in at all.
        private readonly Dictionary<string, int> codeByCtyEntity =
            new Dictionary<string, int>(400, StringComparer.OrdinalIgnoreCase);

        // Country NAME -> ARRL entity code, for the ADIF export. Deliberately keyed on the name rather
        // than resolved afresh from the callsign: what goes in <dxcc> has to be the number of the very
        // country written in <country> beside it, or the record contradicts itself. A QSO whose stored
        // country is wrong is a job for Verify Log, not something to paper over at export time.
        private readonly Dictionary<string, int> codeByCountryName =
            new Dictionary<string, int>(400, StringComparer.OrdinalIgnoreCase);

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

        // ── THE ENTITY LIST, BY NUMBER ────────────────────────────────────
        //
        // Every DXCC entity with its ADIF number, the name to show it under, and whether it has been
        // deleted - the three things a count of countries is made of.
        //
        // The NUMBER is the identity throughout, because a name is not one. Two databases spell the same
        // country differently ("Fed. Rep. Germany" against "Fed. Rep. of Germany"), either may re-spell
        // it, and a name that is in nobody's list reads as a deleted country - which is how "Maritime
        // Mobile", the answer meaning no country at all, came to be counted as a deleted entity worked
        // and confirmed. A number cannot do any of that: it is fixed, unique, and never reused.
        //
        // Deleted is Club Log's own <deleted> flag, not "this name is absent from the current list".
        // The name is cty.dat's wherever the two are bridged, so the page reads in the same words as the
        // log does, and Club Log's (title-cased) only for entities cty.dat cannot name at all.
        public IEnumerable<EntityRecord> AllEntities()
        {
            if (clubLog == null) yield break;
            foreach (ClubLogData.EntityInfo e in clubLog.AllEntities())
            {
                string name = null;
                string ctyEntity;
                if (ctyEntityByCode.TryGetValue(e.Code, out ctyEntity))
                {
                    DXCC named = cty.GetDXCCbyEntityCode(ctyEntity);
                    if (named != null && named.Name != "Unknown") name = named.Name;
                }
                yield return new EntityRecord
                {
                    Code = e.Code,
                    Name = string.IsNullOrEmpty(name) ? TitleCase(e.Name) : name,
                    Deleted = e.Deleted,
                };
            }
        }

        public struct EntityRecord
        {
            public int Code;
            public string Name;
            public bool Deleted;
        }

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

            // Nothing current names this callsign in either database - 4N (Serbia) and the Olympic-year
            // 2O (England) were withdrawn, so they are simply gone from both. The last country the call
            // is known to have belonged to beats "Unknown", and because this only ever fills a blank it
            // can never overrule a live assignment: a 3C1 station today still reports Equatorial Guinea,
            // not the Canada its block held until 1967.
            if (cl == null)
            {
                if (fromCty == null || fromCty.Name == "Unknown")
                {
                    ClubLogMatch historic = null;
                    try { historic = clubLog.ResolveHistoric(callsign, whenUtc); }
                    catch { historic = null; }

                    // ...but never an entity that had already CEASED TO EXIST on the day of the contact.
                    // The fallback above takes the last thing known about a callsign, which is right when
                    // the country is still there under another prefix - 4N and 2O were withdrawn, Serbia
                    // and England were not. It is wrong when the entity itself is gone: 1B was Blenheim
                    // Reef until 30 June 1975, so 1B1AB worked in 2007 came back as Blenheim Reef, an
                    // entity thirty-two years dead, and was counted as a deleted country chased and
                    // confirmed. 1B in 2007 is Northern Cyprus, which the ARRL does not recognise at all,
                    // and "no entity" is the true answer - the same answer a maritime-mobile station gets.
                    //
                    // The identical rule already guarded EntityCodeForCountry(name, date); it simply was
                    // never applied to the path that starts from a CALLSIGN.
                    if (historic != null && HasCeasedToExistBy(historic.DxccCode, whenUtc))
                        historic = null;

                    if (historic != null) return Combine(historic, fromCty);
                }
                return WithEntityNumber(fromCty);
            }

            // THE MORE SPECIFIC MATCH WINS. Club Log is consulted first, but its answer may rest on a
            // short fallback prefix while cty.dat matched far more of the callsign, and then cty.dat is
            // the better witness. R1FJ is the case that proves it: Club Log's R1FJ record expired in
            // January 2010, so for a QSO today it drops to "R1" (2 characters) and reports European
            // Russia, while cty.dat matches "R1FJ" itself and reports Franz Josef Land. A full-callsign
            // exception is never overruled - that is Club Log's hand-curated core - and neither is
            // anything cty.dat could not match at all.
            //
            // ...but only when Club Log's answer is a BARE FALLBACK. R1FJ in 2020 drops to "R" - one
            // character, a whole call block - and that is not an answer about the station. EA8AAH is
            // the case that showed the difference: Club Log matches "EA8", a real entity prefix and
            // certainly the Canary Islands, while cty.dat carries a hand-written entry for that one
            // callsign placing it in Spain. cty.dat has no dates, so such an entry stays true for ever
            // once written; Club Log is kept with dates and knew better. Letting six matched characters
            // beat three put two EA8 stations on one screen pointing opposite ways.
            //
            // Two characters is the line: a one or two letter match is a block ("R", "EA"), three is a
            // prefix somebody assigned to a place ("EA8", "KP2", "R1F").
            const int BlockFallback = 2;
            if (!cl.ExactCall && cl.MatchedLength <= BlockFallback
                && fromCty != null && fromCty.MatchedLength > cl.MatchedLength
                && fromCty.Name != "Unknown")
                return WithEntityNumber(fromCty);   // cty.dat's country, Club Log's number for it

            return Combine(cl, fromCty);
        }

        // Turns a Club Log match into the answer the rest of the program sees, saying it in cty.dat's
        // words wherever the same entity exists there.
        private DXCC Combine(ClubLogMatch cl, DXCC fromCty)
        {
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
                    // Only reached for an entity cty.dat cannot name at all - almost always a deleted one
                    // like Netherlands Antilles. Club Log shouts its names in capitals, so they are cased
                    // to sit beside the log's own wording instead of glaring out of it.
                    Name = TitleCase(cl.EntityName),
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

            // Invert it. First code wins if two ever mapped to one cty.dat entity, which would mean the
            // bridge had gone wrong rather than that the country genuinely has two numbers.
            foreach (KeyValuePair<int, string> pair in ctyEntityByCode)
            {
                if (string.IsNullOrEmpty(pair.Value)) continue;
                if (!codeByCtyEntity.ContainsKey(pair.Value)) codeByCtyEntity[pair.Value] = pair.Key;

                // ...and by the name that same entity is written under, which is what a stored QSO holds.
                DXCC named = cty.GetDXCCbyEntityCode(pair.Value);
                if (named != null && !string.IsNullOrEmpty(named.Name) && named.Name != "Unknown")
                    AddCountryName(named.Name, pair.Key);
            }

            // Now every entity Club Log lists, DELETED ONES INCLUDED, under Club Log's own wording.
            // cty.dat contains no deleted entity, so this is the only place a QSO with Czechoslovakia
            // or the Canal Zone in it can get its number - and an operator licensed for forty years has
            // plenty of those. Added second, so an entity that still exists always wins the name.
            if (clubLog != null)
            {
                foreach (KeyValuePair<int, string> entity in clubLog.AllEntityNames())
                    AddCountryName(entity.Value, entity.Key);
            }
        }

        // One country name in the lookup table, under the same flattened form the comparison above
        // uses - so "Bonaire, Curacao (Neth Antilles)" out of a log matches Club Log's capitals, and
        // "St." matches "Saint". Never overwrites: the first name registered for a wording wins, and
        // active entities are registered first.
        private void AddCountryName(string name, int code)
        {
            string key = Flatten(name);
            if (key.Length == 0 || codeByCountryName.ContainsKey(key)) return;
            codeByCountryName[key] = code;
        }

        // The ARRL entity number for a country as it is WRITTEN in a logged QSO, or 0 when no database
        // knows that wording. Used by the ADIF export, so <dxcc> and <country> can never disagree.
        // THE ENTITIES A STROKED CALLSIGN COULD NAME - one for each side of the stroke.
        //
        // M/ON4CJK could be England (the M) or Belgium (the ON4CJK), and which of the two is meant is
        // decided by a convention, not by anything in the callsign itself. This program picks a side
        // and is sometimes wrong. But the operator's own ADIF usually carries a <DXCC> for that
        // contact, written by whoever was actually there - and when that number is ONE OF THESE TWO,
        // the argument is over: the log is right and we were guessing.
        //
        // Empty for a callsign with no stroke: there is no second candidate, so there is nothing to
        // defer to, and a log that disagrees with us there is a finding like any other.
        public HashSet<int> CandidateEntityCodes(string callsign, DateTime whenUtc)
        {
            var codes = new HashSet<int>();
            if (string.IsNullOrWhiteSpace(callsign)) return codes;
            string call = callsign.Trim().ToUpperInvariant();
            if (call.IndexOf('/') < 0) return codes;

            foreach (string side in new[] { EntityResolver.OperatingPart(call), EntityResolver.OtherPart(call) })
            {
                if (string.IsNullOrEmpty(side)) continue;
                DXCC d;
                try { d = Resolve(side, whenUtc); }
                catch { continue; }
                if (d == null || !d.IsDxccEntity) continue;

                int code = d.DxccCode;
                if (code <= 0)
                {
                    try { code = EntityCodeForCountry(d.Name, whenUtc); }
                    catch { code = 0; }
                }
                if (code > 0) codes.Add(code);
            }
            return codes;
        }

        public int EntityCodeForCountry(string countryName)
        {
            string key = Flatten(countryName);
            if (key.Length == 0) return 0;
            int code;
            return codeByCountryName.TryGetValue(key, out code) ? code : 0;
        }

        // The same, but refusing an answer the QSO's own date rules out.
        //
        // Some wordings belong to an entity that no longer exists. "Germany" is the awkward one: cty.dat
        // calls the modern country "Fed. Rep. of Germany", so the bare word "Germany" - which another
        // logger's file may well carry - is Club Log's name for entity 81, deleted in 1973. Writing 81
        // into an award submission for a QSO made in 2020 would be plainly wrong, so when the only
        // match is an entity that had already ceased to exist on the day of the contact, this reports
        // nothing at all. Saying nothing is recoverable; saying the wrong number is not.
        public int EntityCodeForCountry(string countryName, DateTime whenUtc)
        {
            int code = EntityCodeForCountry(countryName);
            if (code <= 0 || clubLog == null) return code;
            if (!clubLog.IsDeletedEntity(code)) return code;
            return whenUtc <= clubLog.EntityEndUtc(code) ? code : 0;
        }

        // True when this entity was already gone on the day asked about. Only deleted entities carry an
        // end date, so an entity that still exists always answers false and the caller is unaffected.
        private bool HasCeasedToExistBy(int dxccCode, DateTime whenUtc)
        {
            if (clubLog == null || dxccCode <= 0) return false;
            if (!clubLog.IsDeletedEntity(dxccCode)) return false;
            return whenUtc > clubLog.EntityEndUtc(dxccCode);
        }

        // True when that entity is one the ARRL has deleted. The QSO still counts - it is worked
        // country number 85 for ever - but nothing new can be worked there.
        public bool IsDeletedEntityCode(int dxccCode)
        {
            return clubLog != null && dxccCode > 0 && clubLog.IsDeletedEntity(dxccCode);
        }

        // Puts the ARRL entity number on an answer that came from cty.dat, which has none of its own.
        // Only ever fills a blank: an answer that already carries a number is left exactly as it is.
        private DXCC WithEntityNumber(DXCC answer)
        {
            if (answer == null || answer.DxccCode != 0 || string.IsNullOrEmpty(answer.Entity)) return answer;
            int code;
            if (codeByCtyEntity.TryGetValue(answer.Entity, out code)) answer.DxccCode = code;
            return answer;
        }

        // Two spellings of one country. The databases differ in case, punctuation and in whether they
        // write "Saint"/"St." and "Islands"/"Is.", so those are levelled before comparing.
        private static readonly Regex NonAlphaNumeric = new Regex("[^A-Z0-9]", RegexOptions.Compiled);

        private static bool SameCountryName(string a, string b)
        {
            return Flatten(a) == Flatten(b) && Flatten(a).Length > 0;
        }

        // The same levelling, offered to callers who have to tell a real disagreement from a difference
        // in wording - an importer comparing the country a FILE carried against the one resolved from
        // the callsign, where "Germany" and "Fed. Rep. of Germany" are the same answer and must not be
        // reported as a problem.
        public static bool IsSameCountryName(string a, string b)
        {
            return SameCountryName(a, b);
        }

        // The same question asked more forgivingly, for a REPORT rather than for a lookup: is one of
        // these merely a fuller wording of the other?
        //
        // Entity numbers settle it when both sides have one, and this is only reached when they do not -
        // which happens more than it sounds. Club Log's number for the bare word "Germany" is entity 81,
        // deleted in 1973, so a modern QSO gets no number for it at all, while our own answer is "Fed.
        // Rep. of Germany". Compared as text those differ, and a Log4OM log would report every German
        // contact in it as a disagreement - thousands of lines saying nothing, burying the few that mean
        // something.
        //
        // So one name CONTAINING the other counts as agreement: Germany inside Fed. Rep. of Germany,
        // Russia inside European Russia. It is a heuristic and it is deliberately generous - the cost of
        // staying quiet about a real difference here is that one line is missing from a report, while the
        // cost of crying wolf is a report nobody reads.
        public static bool IsSameCountryWording(string a, string b)
        {
            var x = NameWords(a);
            var y = NameWords(b);
            if (x.Count == 0 || y.Count == 0) return false;

            // ONE NAME IS THE OTHER PLUS OR MINUS A WORD. Compared as whole words rather than as one
            // run of letters, because the words that differ sit in the MIDDLE and a straight "does one
            // contain the other" cannot see past them:
            //
            //   FED REP GERMANY        vs  FED REP OF GERMANY          - an "of" in the middle
            //   UK BASE AREAS CYPRUS   vs  UK SOV BASE AREAS CYPRUS    - a dropped "Sov."
            //   UNITED STATES          vs  UNITED STATES OF AMERICA
            //
            // Every one of those is the same entity written differently. Whereas ASIATIC RUSSIA and
            // EUROPEAN RUSSIA share a word and neither contains the other, which is right - they are
            // two entities.
            var small = x.Count <= y.Count ? x : y;
            var large = x.Count <= y.Count ? y : x;
            foreach (string w in small) if (!large.Contains(w)) return false;
            return true;
        }

        // A country name as its meaningful words, levelled the way the databases are matched.
        private static List<string> NameWords(string name)
        {
            var words = new List<string>();
            if (string.IsNullOrWhiteSpace(name)) return words;
            foreach (string raw in name.ToUpperInvariant().Split(new[] { ' ', '.', ',', '-', '\'', '(', ')', '&', '/' },
                                                                 StringSplitOptions.RemoveEmptyEntries))
            {
                string w = NonAlphaNumeric.Replace(raw, "");
                if (w.Length == 0) continue;
                if (w == "OF" || w == "THE" || w == "AND") continue;   // filler, never distinguishing
                words.Add(w);
            }
            return words;
        }

        // "BONAIRE, CURACAO (NETH ANTILLES)" -> "Bonaire, Curacao (Neth Antilles)". Only used for names
        // that come from Club Log verbatim; cty.dat's own wording is never touched.
        private static string TitleCase(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var sb = new System.Text.StringBuilder(s.Length);
            bool startOfWord = true;
            foreach (char c in s)
            {
                if (char.IsLetter(c))
                {
                    sb.Append(startOfWord ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c));
                    startOfWord = false;
                }
                else
                {
                    sb.Append(c);
                    // An apostrophe keeps the word going ("Cote d'Ivoire"); anything else starts a new one.
                    startOfWord = c != '\'';
                }
            }
            return sb.ToString();
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
