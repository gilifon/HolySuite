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
            get
            {
                lock (SharedLock)
                {
                    if (_shared != null) return _shared;

                    // TIMED, AND IT SAYS WHO ASKED. This is built on FIRST USE, and building it parses
                    // cty.dat and Club Log's 9.6 MB file - tens of megabytes of objects in a 32-bit
                    // program. Something freezes the window for six seconds a few seconds after it
                    // opens, and this is the best candidate: nobody knows when "first use" happens,
                    // because it is whoever asks first.
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    _shared = Create();
                    sw.Stop();

                    try
                    {
                        var by = new System.Diagnostics.StackTrace(1, false);
                        string caller = by.FrameCount > 0 ? by.GetFrame(0).GetMethod().Name : "?";
                        string caller2 = by.FrameCount > 1 ? by.GetFrame(1).GetMethod().Name : "?";
                        System.Diagnostics.Trace.WriteLine("STARTUP  country databases built in "
                            + sw.ElapsedMilliseconds + " ms, first asked for by " + caller + " <- " + caller2);
                        WhenBuilt = "built in " + sw.ElapsedMilliseconds + " ms, first asked for by "
                                    + caller + " <- " + caller2;
                    }
                    catch { }

                    return _shared;
                }
            }
        }

        // What the timing above found, for whoever wants to write it to the program's log - this
        // project has no logger of its own.
        public static string WhenBuilt { get; private set; }

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
        // WHAT cty.dat KNOWS ABOUT A COUNTRY AS A WHOLE, found by its ARRL entity number instead of by a
        // callsign - continent, the entity's own CQ and ITU zones, and the middle of it as a grid square.
        //
        // Everything else here starts from a callsign, because a callsign is what an operator has. This
        // is for the one case where he has not got one he trusts: he NAMES the country himself, and the
        // fields that hang off a country then have to follow the country he named rather than the one
        // the callsign suggested.
        //
        // ZONES ARE THE ENTITY'S, NOT THE STATION'S. A country wide enough to span zones - the United
        // States, Russia, Australia - has one default here and stations all over it. So this answers
        // "what does this country default to", which is the only honest answer to a question asked with
        // no callsign in it, and never "which zone is that station in".
        //
        // Null when the number is unknown or cty.dat cannot name it, so a caller can leave its fields
        // alone rather than write a guess into them.
        public DXCC EntityDetails(int dxccCode)
        {
            if (dxccCode <= 0 || cty == null) return null;
            try
            {
                string ctyEntity;
                if (!ctyEntityByCode.TryGetValue(dxccCode, out ctyEntity)) return null;
                DXCC found = cty.GetDXCCbyEntityCode(ctyEntity);
                return found != null && found.Name != "Unknown" ? found : null;
            }
            catch { return null; }
        }

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
            return QsoDate(adifDate, null);
        }

        // THE HOUR MATTERS, and leaving it out got a country wrong.
        //
        // Club Log's records carry a time, not just a day. NH8S from Swains Island opens at
        // 2012-09-07T04:00Z; a QSO logged that same day at 18:27 is inside it. Asked with the date
        // alone the program asked about 00:00 - four hours BEFORE the operation began - so Club Log
        // answered "not in effect", the program fell back to the NH8 prefix and recommended American
        // Samoa for a contact that was plainly Swains Island. Every DXpedition that starts or ends
        // part-way through a day has the same trap on its first and last day.
        //
        // So the QSO's own time is used when the log has one. ADIF writes it as HHMMSS or HHMM;
        // anything else, and a missing time, falls back to midnight - which is what this did before
        // and is still the right answer when there is nothing better.
        public static DateTime QsoDate(string adifDate, string adifTime)
        {
            DateTime when;
            if (!string.IsNullOrWhiteSpace(adifDate) &&
                DateTime.TryParseExact(adifDate.Trim(), "yyyyMMdd", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out when))
            {
                return when.Add(TimeOfDay(adifTime));
            }
            return DateTime.UtcNow;
        }

        private static TimeSpan TimeOfDay(string adifTime)
        {
            string t = (adifTime ?? string.Empty).Trim();
            if (t.Length == 0) return TimeSpan.Zero;

            // Some logs write 18:27:09; the digits are what matter.
            t = t.Replace(":", string.Empty);

            DateTime parsed;
            if (t.Length == 6 && DateTime.TryParseExact(t, "HHmmss", CultureInfo.InvariantCulture,
                                                        DateTimeStyles.None, out parsed))
                return parsed.TimeOfDay;
            if (t.Length == 4 && DateTime.TryParseExact(t, "HHmm", CultureInfo.InvariantCulture,
                                                        DateTimeStyles.None, out parsed))
                return parsed.TimeOfDay;

            return TimeSpan.Zero;
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
                    if (historic != null && EntityHadCeasedBy(historic.DxccCode, whenUtc))
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

            // ...AND NOT WHEN cty.dat IS DESCRIBING SOMETHING THAT HAD NOT HAPPENED YET.
            //
            // cty.dat's exception list has NO DATES. Once a callsign is written into it the entry is
            // true for ever, in BOTH directions - which is right for a permanent assignment and wrong
            // for a special-event call that gets reissued. K4A is the case: cty.dat files =K4A under
            // Puerto Rico, and Club Log holds three K4A records - August 2014, and twice in 2026. A
            // QSO with K4A on 18 February 2008 was a US special event station, which is what the log
            // said and what QRZ says; the rule below saw three matched characters against Club Log's
            // one-letter "K" and answered Puerto Rico for a contact made six years before the Puerto
            // Rican operation existed.
            //
            // The test is that EVERY Club Log record for the callsign starts LATER than the QSO, and
            // it is what keeps R1FJ working: Club Log's R1FJ record ran until January 2010, so it is
            // in the past, cty.dat is the fresher witness and still wins. Only a cty.dat entry that
            // describes the future is set aside.
            int callLength = (callsign ?? string.Empty).Trim().Length;
            bool ctyMatchedWholeCall = fromCty != null && callLength > 0
                                       && fromCty.MatchedLength >= callLength;
            if (ctyMatchedWholeCall && !cl.ExactCall
                && clubLog.CallsignRecordsAllStartAfter(callsign, whenUtc))
                return Combine(cl, fromCty);

            // ...AND NOT WHEN CLUB LOG'S SHORT MATCH IS A CLOSED CHAPTER OF HISTORY.
            //
            // A two-letter match is treated as a weak fallback because it usually is - a whole call
            // block that says nothing about the station. But when the record that answered has an END
            // DATE that has already passed, it is not a fallback at all: it is Club Log saying "in
            // those years, these letters were that country", which is exactly the question being
            // asked. UB4WZA in April 1992 is the case: Club Log holds UB = Ukraine until 31 December
            // 1994, while cty.dat matches the longer UB4W and reports European Russia, because that is
            // what UB4W is TODAY. The contact was made in the Ukraine of 1992.
            //
            // Still-open records (no end date) are left as fallbacks, which is what keeps R1FJ right:
            // Club Log's "R1" is current, not historical, so cty.dat's Franz Josef Land wins there.
            bool clubIsHistoryOfThatEra = cl.End < DateTime.UtcNow;

            if (!cl.ExactCall && cl.MatchedLength <= BlockFallback
                && fromCty != null && fromCty.MatchedLength > cl.MatchedLength
                && fromCty.Name != "Unknown"
                && !clubIsHistoryOfThatEra
                && !CtyIsDescribingTheFuture(callsign, whenUtc, cl.MatchedLength))
                return WithEntityNumber(fromCty);   // cty.dat's country, Club Log's number for it

            return Combine(cl, fromCty);
        }

        // A LONGER MATCH IS NOT A BETTER ONE WHEN IT BELONGS TO A LATER YEAR.
        //
        // cty.dat carries no dates: a prefix written into it is written for ever, and reading it back
        // for an old QSO answers with today's map. CQ14EEN, worked in June 2004, is the case. cty.dat
        // matches CQ1 and says Azores; Club Log matches CQ and says Portugal - two characters, so the
        // rule above would hand it to cty.dat on length alone. But Club Log also knows WHY it stopped
        // at two: its CQ1 = Azores record does not begin until 1 June 2009, five years after the
        // contact. In 2004 CQ1 was not the Azores at all, and CQ14EEN was a Portuguese special-event
        // callsign for the European football championship.
        //
        // So before length decides, Club Log is asked whether the longer prefix cty.dat leaned on was
        // in effect that day. A record that had not STARTED yet means cty.dat is describing the
        // future, and its longer match is worth nothing here.
        //
        // Only the not-yet-started direction counts. A record that has EXPIRED leaves cty.dat as the
        // fresher witness, which is what keeps R1FJ right: Club Log's R1FJ record ran out in January
        // 2010, so for a QSO today cty.dat's Franz Josef Land still wins.
        private bool CtyIsDescribingTheFuture(string callsign, DateTime whenUtc, int clubMatchedLength)
        {
            try
            {
                ClubLogData.NearMiss near =
                    clubLog.LongerRecordNotInEffect(callsign, whenUtc, clubMatchedLength);
                return near != null && near.Start > whenUtc;
            }
            catch (Exception)
            {
                // An explanation that cannot be fetched must not change the answer.
                return false;
            }
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

        // ── WHY THIS COUNTRY, IN WORDS THE OPERATOR CAN JUDGE ───────────────────────────────────────
        //
        // Resolve gives one answer and keeps its reasoning to itself, which is fine until the answer
        // looks wrong. Then the only useful thing is what each database said ON ITS OWN and how much
        // of the callsign each of them actually recognised: "Club Log only matched CQ, cty.dat matched
        // CQ1" settles in one line what a country name on its own cannot.
        //
        // Worked out on demand for one callsign, never for a whole log - it costs two lookups.
        public class Explanation
        {
            public string Callsign;
            public DateTime WhenUtc;

            public string CtyName;      public int CtyCode;   public string CtyMatched;
            public string ClubName;     public int ClubCode;  public string ClubMatched;
            public bool ClubExactCall;  public bool ClubHistoric;

            // A longer prefix Club Log HOLDS but which did not apply on the QSO's date - the reason
            // its answer is shorter than cty.dat's, and the one fact that turns "Club Log matched CQ"
            // from an admission of ignorance into an explanation.
            public string ClubNearKey, ClubNearName;
            public int ClubNearCode;
            public DateTime ClubNearFrom, ClubNearTo;

            public string FinalName;    public int FinalCode;

            public bool Agree { get { return CtyCode > 0 && ClubCode > 0 && CtyCode == ClubCode; } }

            // THE VERDICT, ON ITS OWN LINE. What each database matched belongs UNDER it, one per line,
            // not run together behind it: the reader is comparing two things and a paragraph makes him
            // do that comparison inside his head.
            public string Headline
            {
                get
                {
                    if (ClubCode <= 0 && CtyCode <= 0) return "Neither cty.dat nor Club Log knows this callsign.";
                    if (ClubCode <= 0) return "Club Log has nothing for this callsign. cty.dat answered alone.";
                    if (CtyCode <= 0) return "cty.dat has nothing for this callsign. Club Log answered alone.";
                    return Agree ? "cty.dat and Club Log agree." : "cty.dat and Club Log disagree.";
                }
            }

            // One line each, in the order they are argued about. The matched letters are wrapped in **
            // so a caller that renders bold lifts them out; PlainDetails strips them for a text file.
            public List<string> Details
            {
                get
                {
                    // THE LETTERS COME FIRST ON EACH LINE, so they start at the same place and can be
                    // read one under the other. "Club Log matched UH" over "cty.dat matched UH8" put
                    // them at different distances from the margin - the dialog's font is proportional,
                    // so no amount of padding could ever have lined them up.
                    var lines = new List<string>();
                    if (!string.IsNullOrEmpty(ClubMatched))
                        lines.Add("**" + ClubMatched + "** is what Club Log matched");
                    if (!string.IsNullOrEmpty(CtyMatched))
                        lines.Add("**" + CtyMatched + "** is what cty.dat matched");
                    lines.AddRange(ExtraNotes);
                    return lines;
                }
            }

            // The part of Details that is NOT about the matched letters. A caller that already prints
            // "cty.dat matched RY0U which is Asiatic Russia (15)" on its own line has said that much
            // already, and repeating it four lines further down is noise.
            public List<string> ExtraNotes
            {
                get
                {
                    var lines = new List<string>();

                    if (!string.IsNullOrEmpty(ClubNearKey))
                    {
                        // THE HOUR IS SHOWN WHEN THERE IS ONE. Club Log's windows open and close at a
                        // time of day - Swains Island 2012 began at 04:00 UTC - and printing only the
                        // date made the note contradict itself: "not in effect" beside a range whose
                        // first day IS the QSO's day. With the time there, the reason is on the line.
                        string when;
                        if (ClubNearFrom > DateTime.MinValue && ClubNearTo < DateTime.MaxValue)
                            when = "only between " + Moment(ClubNearFrom) + " and " + Moment(ClubNearTo);
                        else if (ClubNearFrom > DateTime.MinValue)
                            when = "only from " + Moment(ClubNearFrom);
                        else
                            when = "only until " + Moment(ClubNearTo);

                        lines.Add("Club Log knows **" + ClubNearKey + "** = "
                                  + (ClubNearCode > 0 ? ClubNearName + " (" + ClubNearCode + ")" : ClubNearName)
                                  + ", but " + when);
                    }

                    if (ClubExactCall) lines.Add("Club Log has an entry for this exact callsign");
                    if (ClubHistoric) lines.Add("No Club Log record covers this date");
                    return lines;
                }
            }

            // Everything on one line, for a report where a row is a row.
            public string Sentence
            {
                get
                {
                    var d = Details;
                    return d.Count == 0 ? Headline : Headline + " " + string.Join("; ", d.ToArray()) + ".";
                }
            }

            // The same, with the emphasis markers taken out, for anywhere that shows plain text.
            public string PlainSentence { get { return (Sentence ?? "").Replace("**", ""); } }

            // A date, and the time of day with it when it is not plain midnight.
            private static string Moment(DateTime t)
            {
                return t.TimeOfDay == TimeSpan.Zero
                    ? t.ToString("dd-MM-yyyy")
                    : t.ToString("dd-MM-yyyy HH:mm") + " UTC";
            }

            // "Portugal (272)", and the number is never left off - it is the part that counts.
            private static string Named(int code, string name)
            {
                string n = string.IsNullOrWhiteSpace(name) ? "(no name)" : name.Trim();
                return code > 0 ? n + " (" + code + ")" : n;
            }

            // "cty.dat matched CQ8 which is Azores (149)", and the honest shorter forms: a stroke
            // callsign, where the matched letters are not at the front and cannot be pointed at, and a
            // database with nothing to say at all.
            private static string Says(string who, string matched, int code, string name)
            {
                bool knows = code > 0 || !string.IsNullOrEmpty(name);
                if (!knows) return who + " has nothing for this callsign";
                if (string.IsNullOrEmpty(matched)) return who + " says " + Named(code, name);
                return who + " matched **" + matched + "** which is " + Named(code, name);
            }

            // THE SAME ANSWER IN ITS SEPARATE PIECES, for a report that lays them out in a table rather
            // than as a paragraph. Report() below still composes the whole thing for the "?" box; these
            // are the identical strings, so the two cannot come to word it differently - which is the
            // whole reason the wording lives in this class and not in its callers.
            //
            // Plain text: the ** emphasis markers are for a screen that renders them, and a text file
            // shows them as two asterisks.
            public string CtySays { get { return Says("cty.dat", CtyMatched, CtyCode, CtyName).Replace("**", ""); } }
            public string ClubSays { get { return Says("Club Log", ClubMatched, ClubCode, ClubName).Replace("**", ""); } }
            public string Recommends { get { return "HolyLogger recommends: " + Named(FinalCode, FinalName); } }
            public string PlainHeadline { get { return (Headline ?? "").Replace("**", ""); } }

            // The last word: whether this one can be accepted without thinking about it.
            public string Closing
            {
                get
                {
                    return Agree
                        ? "Both agree, so this proposal is a safe one to accept."
                        : "Please consider and decide.";
                }
            }

            public List<string> PlainExtraNotes
            {
                get
                {
                    var lines = new List<string>();
                    foreach (string s in ExtraNotes) lines.Add((s ?? "").Replace("**", ""));
                    return lines;
                }
            }

            // THE WHOLE ANSWER, WORDED ONCE. Both the Log Fixer's "?" box and any report written about
            // a log say this - from here, so the two can never come to word it differently and an
            // operator comparing the paper with the screen never has to wonder whether they mean the
            // same thing. Emphasis is marked with ** for callers that render it; PlainReport strips it.
            public string Report(string logCountryName, int logCountryCode, string dateText)
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("**" + Callsign + "**"
                              + (string.IsNullOrWhiteSpace(dateText) ? "" : "   worked " + dateText));
                sb.AppendLine();
                sb.AppendLine("Your log says:   " + Named(logCountryCode, logCountryName));
                sb.AppendLine();
                sb.AppendLine(Says("cty.dat", CtyMatched, CtyCode, CtyName));
                sb.AppendLine(Says("Club Log", ClubMatched, ClubCode, ClubName));
                sb.AppendLine();
                sb.AppendLine("HolyLogger recommends: " + Named(FinalCode, FinalName));
                sb.AppendLine();
                sb.AppendLine(Headline);
                foreach (string line in ExtraNotes) sb.AppendLine(line);
                sb.AppendLine();
                sb.AppendLine(Agree
                    ? "Both agree, so this proposal is a safe one to accept."
                    : "Please consider and decide.");
                return sb.ToString();
            }

            public string PlainReport(string logCountryName, int logCountryCode, string dateText)
            {
                return Report(logCountryName, logCountryCode, dateText).Replace("**", "");
            }
        }

        public Explanation Explain(string callsign, DateTime whenUtc)
        {
            var e = new Explanation { Callsign = (callsign ?? string.Empty).Trim(), WhenUtc = whenUtc };
            if (e.Callsign.Length == 0) return e;

            // A stroke callsign is looked up by the part that says where the station IS, which is not
            // always at the front - so the number of matched characters cannot be turned into a piece
            // of the callsign the operator would recognise. Then the names are given without it rather
            // than pointing at the wrong letters.
            bool plainCall = e.Callsign.IndexOf('/') < 0;
            string upper = e.Callsign.ToUpperInvariant();

            DXCC c = null;
            try { c = cty.GetDXCC(e.Callsign); } catch (Exception) { c = null; }
            if (c != null && c.Name != "Unknown")
            {
                e.CtyName = c.Name;
                try { e.CtyCode = EntityCodeForCountry(c.Name, whenUtc); } catch (Exception) { e.CtyCode = 0; }
                if (plainCall && c.MatchedLength > 0 && c.MatchedLength <= upper.Length)
                    e.CtyMatched = upper.Substring(0, c.MatchedLength);
            }

            if (clubLog != null)
            {
                ClubLogMatch cl = null;
                try { cl = clubLog.Resolve(e.Callsign, whenUtc); } catch (Exception) { cl = null; }
                if (cl != null)
                {
                    e.ClubName = TitleCase(cl.EntityName);
                    e.ClubCode = cl.DxccCode;
                    e.ClubExactCall = cl.ExactCall;
                    e.ClubHistoric = cl.Historic;
                    if (plainCall && cl.MatchedLength > 0 && cl.MatchedLength <= upper.Length)
                        e.ClubMatched = upper.Substring(0, cl.MatchedLength);
                }

                // Why its answer is shorter than cty.dat's, when it is.
                try
                {
                    ClubLogData.NearMiss near = clubLog.LongerRecordNotInEffect(
                        e.Callsign, whenUtc, cl == null ? 0 : cl.MatchedLength);
                    if (near != null)
                    {
                        e.ClubNearKey = near.Key;
                        e.ClubNearName = TitleCase(near.EntityName);
                        e.ClubNearCode = near.DxccCode;
                        e.ClubNearFrom = near.Start;
                        e.ClubNearTo = near.End;
                    }
                }
                catch (Exception ex)
                {
                    // The near-miss is an EXPLANATION, not an answer: it says why Club Log's record
                    // is shorter than cty.dat's when a report is being written. Losing it must not
                    // cost the resolution itself, so it is swallowed - but it is written down, since
                    // a blank half of a country report is otherwise a mystery.
                    System.Diagnostics.Trace.WriteLine("CountryLookup near-miss failed: "
                                                       + ex.GetType().Name + ": " + ex.Message);
                }
            }

            DXCC final = null;
            try { final = Resolve(e.Callsign, whenUtc); } catch (Exception) { final = null; }
            if (final != null) { e.FinalName = final.Name; e.FinalCode = final.DxccCode; }

            return e;
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

        // THE OPERATOR'S OWN COUNTRY SETTLES A STROKE, asked in ONE place because two places ask it.
        //
        // A callsign with a stroke names two entities, one per side, and which one is meant is a
        // convention rather than anything in the callsign: T9/VE6PR is a Canadian operator working from
        // Bosnia. This resolver picks a side and is sometimes wrong. When the entity already recorded
        // against the QSO is ONE OF THOSE TWO, it came from the person who was there, and disagreeing
        // with it is not a finding - it is this program preferring its own guess to a fact.
        //
        // It lives here rather than in either caller because it HAS two callers and they had drifted
        // apart: the Log Fixer applied the rule and the ADIF import did not, so the same T9/VE6PR was
        // silently accepted by one and proposed for correction by the other, in two reports the operator
        // was reading side by side. One rule, one place, no second opinion.
        //
        // Only ever a reason to say NOTHING. An entity that is neither side of the stroke is still a
        // finding, and a callsign with no stroke has no second candidate, so nothing is weakened.
        public bool StrokeSettledByLog(string callsign, int recordedEntityCode, DateTime whenUtc)
        {
            if (recordedEntityCode <= 0) return false;
            if (string.IsNullOrWhiteSpace(callsign)) return false;
            if (callsign.IndexOf('/') < 0) return false;

            try
            {
                HashSet<int> sides = CandidateEntityCodes(callsign, whenUtc);
                return sides != null && sides.Contains(recordedEntityCode);
            }
            catch { return false; }
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

        // The day an entity ceased to exist, or DateTime.MaxValue while it still does. Public because
        // Verify prints the date in its report: "no longer existed on this date" is an accusation, and
        // the operator is entitled to see the date it rests on.
        public DateTime EntityEndUtc(int dxccCode)
        {
            return clubLog == null || dxccCode <= 0 ? DateTime.MaxValue : clubLog.EntityEndUtc(dxccCode);
        }

        // True when this entity was already gone on the day asked about. Only deleted entities carry an
        // end date, so an entity that still exists always answers false and the caller is unaffected.
        public bool EntityHadCeasedBy(int dxccCode, DateTime whenUtc)
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
