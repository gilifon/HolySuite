using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;

namespace HolyLogger.Contests
{
    // Loads the bundled contest database (contests.json, embedded) and tracks which contest, if any,
    // the operator is currently logging.
    //
    // Phase 1 scope: load + selection + active state. A contest is only OFFERED in the picker once
    // its exchange parser is finished (SupportedIds); the rest are listed but disabled. Exchange-field
    // adaptation, per-contest parsers, dupe-rule and CONTEST_ID tagging come in later phases.
    public static class ContestService
    {
        private static ContestDatabase _db;

        public static IReadOnlyList<Contest> All => Db.Contests;

        // The contest currently being logged, or null when not in a contest.
        public static Contest Active { get; private set; }

        // Every contest with an exchange definition is now selectable: the entry form renders any
        // number of received/sent fields and the variant resolver (PickVariant) chooses the right
        // asymmetric side from the callsign. Role-based contests default to the ordinary-participant
        // side for now (a role selector is the remaining piece).
        public static bool IsSupported(Contest c) =>
            c != null && c.Exchange != null && c.Exchange.Count > 0;

        private static ContestDatabase Db => _db ?? (_db = Load());

        private static ContestDatabase Load()
        {
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                string res = asm.GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith("contests.json", StringComparison.OrdinalIgnoreCase));
                if (res != null)
                    using (Stream s = asm.GetManifestResourceStream(res))
                    using (var r = new StreamReader(s))
                        return JsonConvert.DeserializeObject<ContestDatabase>(r.ReadToEnd())
                               ?? new ContestDatabase();
            }
            catch { }
            return new ContestDatabase();
        }

        public static Contest FindById(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            return Db.Contests.FirstOrDefault(c =>
                string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        public static void Activate(Contest c) => Active = c;

        public static void Deactivate() => Active = null;

        // True for an Israeli (4X/4Z) callsign — the side that drives Holyland's asymmetric exchange.
        public static bool IsIsrael(string call) => MatchesRegion("4X_4Z", call, null);

        // Callsign-prefix sets for the "home" region tokens that appear in the exchange keys
        // (received_EA / sent_non_JA / received_W_VE …). Country tokens are matched by prefix;
        // continent tokens (NA, ASIAN) are matched by the cty.dat continent passed in.
        private static readonly Dictionary<string, string[]> RegionPrefixes =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "4X_4Z", new[] { "4X", "4Z" } },
            { "EA", new[] { "EA", "EB", "EC", "ED", "EE", "EF", "EG", "EH" } },
            { "JA", new[] { "JA","JB","JC","JD","JE","JF","JG","JH","JI","JJ","JK","JL","JM","JN","JO","JP","JQ","JR","JS","7J","7K","7L","7M","7N","8J","8N" } },
            { "I", new[] { "I" } },
            { "HA", new[] { "HA", "HG" } },
            { "HB", new[] { "HB" } },
            { "LZ", new[] { "LZ" } },
            { "SP", new[] { "SP", "SN", "SO", "SQ", "SR", "3Z", "HF" } },
            { "UR", new[] { "UR","US","UT","UU","UV","UW","UX","UY","UZ","EM","EN","EO" } },
            { "YO", new[] { "YO", "YP", "YQ", "YR" } },
            { "OK_OM", new[] { "OK", "OL", "OM" } },
            { "UA", new[] { "UA","UB","UC","UD","UE","UF","UG","UH","UI","R" } },
            { "W_VE", new[] { "K","W","N","AA","AB","AC","AD","AE","AF","AG","AH","AI","AJ","AK","VE","VA","VO","VY" } },
        };

        // Tokens that name a special on-site ROLE rather than a region — these cannot be derived from
        // the callsign (the operator must declare them); we default to the ordinary-participant side.
        private static readonly HashSet<string> SpecialRoleTokens =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "activator", "island", "member", "HQ" };

        private static bool MatchesRegion(string token, string call, string continent)
        {
            if (string.Equals(token, "NA", StringComparison.OrdinalIgnoreCase))
                return string.Equals(continent, "NA", StringComparison.OrdinalIgnoreCase);
            if (string.Equals(token, "Asian", StringComparison.OrdinalIgnoreCase))
                return string.Equals(continent, "AS", StringComparison.OrdinalIgnoreCase);

            if (!RegionPrefixes.TryGetValue(token, out var prefixes)) return false;
            string c = (call ?? string.Empty).Trim().ToUpperInvariant();
            if (c.Length == 0) return false;
            foreach (string p in prefixes)
                if (c.StartsWith(p, StringComparison.Ordinal)) return true;
            return false;
        }

        // True for a "home"/region token (EA, JA, W_VE, NA…) as opposed to a complement (non_EA, DX,
        // others) or a role token (activator, island, member, HQ, chaser, hunter…).
        private static bool IsRegionToken(string token) =>
            RegionPrefixes.ContainsKey(token) ||
            string.Equals(token, "NA", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(token, "Asian", StringComparison.OrdinalIgnoreCase);

        // Picks the right variant of an asymmetric exchange for one side ("sent" or "received") given
        // the station whose identity drives it (mine for sent, the worked station's for received).
        //   • Symmetric contests just use "sent"/"received".
        //   • Region contests have a home variant (sent_EA) and an away variant (sent_non_EA / _DX /
        //     _others) — we match the callsign's prefix (or continent) to pick home vs away.
        //   • Role contests (activator/chaser, island/non_island, member/non_member, HQ/zone) can't be
        //     told from the callsign, so we default to the ordinary-participant side until the operator
        //     can choose a role (a later UI step).
        private static List<string> PickVariant(Contest c, string side, string call, string continent)
        {
            if (c?.Exchange == null) return new List<string>();
            var ex = c.Exchange;

            if (ex.TryGetValue(side, out var sym) && sym != null) return sym;   // symmetric

            string pfx = side + "_";
            var keys = ex.Keys.Where(k => k.StartsWith(pfx, StringComparison.OrdinalIgnoreCase)).ToList();
            if (keys.Count == 0) return new List<string>();

            string Token(string k) => k.Substring(pfx.Length);

            // 1) Region split: find the home token; use it if the callsign matches, else the other side.
            string homeKey = keys.FirstOrDefault(k => IsRegionToken(Token(k)));
            if (homeKey != null)
            {
                if (MatchesRegion(Token(homeKey), call, continent))
                    return ex[homeKey];
                string awayKey = keys.FirstOrDefault(k => k != homeKey) ?? homeKey;
                return ex[awayKey];
            }

            // 2) Role split (or unknown): default to the ordinary-participant variant.
            string defKey = keys.FirstOrDefault(k =>
            {
                string t = Token(k);
                return t.StartsWith("non_", StringComparison.OrdinalIgnoreCase)
                    || t.Equals("chaser", StringComparison.OrdinalIgnoreCase)
                    || t.Equals("hunter", StringComparison.OrdinalIgnoreCase)
                    || t.Equals("others", StringComparison.OrdinalIgnoreCase)
                    || t.Equals("DX", StringComparison.OrdinalIgnoreCase);
            }) ?? keys.FirstOrDefault(k => !SpecialRoleTokens.Contains(Token(k))) ?? keys[0];
            return ex[defKey];
        }

        // Fields the operator RECEIVES (logs). What you receive is exactly what the WORKED station
        // SENDS, so we resolve the SENT side from the DX callsign — this correctly handles cases the
        // JSON's receiver-indexed received_* keys cannot, e.g. a 4X station working another 4X (both
        // send their Area). Falls back to explicit received_* only if a contest defines no sent side.
        public static List<string> GetReceivedFields(Contest c, string dxCallsign, string dxContinent = null)
        {
            var bySent = PickVariant(c, "sent", dxCallsign, dxContinent);
            if (bySent != null && bySent.Count > 0) return bySent;
            return PickVariant(c, "received", dxCallsign, dxContinent);
        }

        // Fields the operator SENDS — chosen from MY callsign/continent.
        public static List<string> GetSentFields(Contest c, string myCallsign, string myContinent = null)
            => PickVariant(c, "sent", myCallsign, myContinent);
    }
}
