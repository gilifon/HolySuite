using System.Collections.Generic;
using Newtonsoft.Json;

namespace HolyLogger.Contests
{
    // Root of the bundled contest database (contests.json). Only the fields the program needs are
    // mapped; varying-type fields (multipliers, qso_points) are intentionally left unmapped so
    // Newtonsoft simply ignores them.
    public class ContestDatabase
    {
        [JsonProperty("contests")]
        public List<Contest> Contests { get; set; } = new List<Contest>();
    }

    public class Contest
    {
        [JsonProperty("id")] public string Id { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("sponsor")] public string Sponsor { get; set; }
        [JsonProperty("period")] public string Period { get; set; }
        [JsonProperty("bands")] public List<string> Bands { get; set; }
        [JsonProperty("modes")] public List<string> Modes { get; set; }
        [JsonProperty("asymmetric")] public bool Asymmetric { get; set; }

        // The exchange keyed by side: "sent"/"received" for symmetric contests, or variants like
        // "sent_DX", "sent_4X_4Z", "received_DX"… for asymmetric ones. Each maps to the ordered list
        // of field names (RST, SERIAL, HOLYLAND_AREA, …).
        [JsonProperty("exchange")] public Dictionary<string, List<string>> Exchange { get; set; }

        [JsonProperty("dupe_rule")] public string DupeRule { get; set; }
        [JsonProperty("cabrillo_name")] public string CabrilloName { get; set; }
        [JsonProperty("rules_url")] public string RulesUrl { get; set; }

        // Optional per-contest overrides for which Cabrillo header fields the operator must fill.
        // Both are absent for most contests, which then use the default required set (see
        // CabrilloHeader.RequiredFor). "cabrillo_required" adds contest-specific mandatory tags
        // (e.g. CATEGORY-OVERLAY, CATEGORY-TRANSMITTER, LOCATION); "cabrillo_optional" drops
        // default-required tags a given contest does not need.
        [JsonProperty("cabrillo_required")] public List<string> CabrilloRequired { get; set; }
        [JsonProperty("cabrillo_optional")] public List<string> CabrilloOptional { get; set; }
    }
}
