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

        // Contests whose received-exchange fields the entry form can render today, so they're
        // selectable in the picker. Grow this set as more field types / layouts are handled.
        private static readonly HashSet<string> SupportedIds =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "HOLYLAND", "CQWW_SSB", "CQWW_CW", "CQWPX_SSB", "CQWPX_CW" };

        public static IReadOnlyList<Contest> All => Db.Contests;

        // The contest currently being logged, or null when not in a contest.
        public static Contest Active { get; private set; }

        public static bool IsSupported(Contest c) =>
            c != null && SupportedIds.Contains(c.Id ?? string.Empty);

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

        // The list of fields the operator RECEIVES (logs) for the given contest, choosing the right
        // side of an asymmetric exchange from the operator's own callsign. Returns an empty list if
        // unknown. (Symmetric contests just use "received".)
        public static List<string> GetReceivedFields(Contest c, string myCallsign)
        {
            if (c?.Exchange == null) return new List<string>();

            // Symmetric: a single "received" list.
            if (c.Exchange.TryGetValue("received", out var sym) && sym != null)
                return sym;

            // Asymmetric: pick the variant for this operator's side.
            string call = (myCallsign ?? string.Empty).Trim().ToUpperInvariant();
            bool isIsrael = call.StartsWith("4X") || call.StartsWith("4Z");

            string key = null;
            if (string.Equals(c.Id, "HOLYLAND", StringComparison.OrdinalIgnoreCase))
                key = isIsrael ? "received_4X_4Z" : "received_DX";

            if (key != null && c.Exchange.TryGetValue(key, out var picked) && picked != null)
                return picked;

            // Fallback: first received_* variant present.
            foreach (var kv in c.Exchange)
                if (kv.Key.StartsWith("received", StringComparison.OrdinalIgnoreCase) && kv.Value != null)
                    return kv.Value;

            return new List<string>();
        }
    }
}
