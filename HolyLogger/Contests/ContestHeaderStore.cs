using System.Collections.Generic;
using HolyParser;
using Newtonsoft.Json;

namespace HolyLogger.Contests
{
    // Persists the Cabrillo header values. Personal fields (scope Personal) are shared across every
    // contest; the four that already have canonical Settings (callsign, name, e-mail, grid) live there
    // so they stay in sync with the rest of the app, and the remaining personal fields plus every
    // per-contest field live in a JSON blob (Settings.CabrilloHeaderStore) — the per-contest ones keyed
    // by contest id. Everything merges into one tag -> value map for the info window and the export.
    public static class ContestHeaderStore
    {
        private class StoreModel
        {
            [JsonProperty("personal")] public Dictionary<string, string> Personal { get; set; } = new Dictionary<string, string>();
            [JsonProperty("byContest")] public Dictionary<string, Dictionary<string, string>> ByContest { get; set; } = new Dictionary<string, Dictionary<string, string>>();
        }

        private static StoreModel ReadModel()
        {
            try
            {
                string json = Properties.Settings.Default.CabrilloHeaderStore;
                if (!string.IsNullOrWhiteSpace(json))
                    return JsonConvert.DeserializeObject<StoreModel>(json) ?? new StoreModel();
            }
            catch { /* corrupt blob -> start fresh */ }
            return new StoreModel();
        }

        // Merged current values for a contest: JSON personal, then the canonical Settings personal
        // fields (which win), then this contest's saved per-contest values.
        public static Dictionary<string, string> Load(string contestId)
        {
            var model = ReadModel();
            var result = new Dictionary<string, string>();

            foreach (var kv in model.Personal) result[kv.Key] = kv.Value;

            // The station callsign is owned by the main-window "Station callsign" box (my_callsign);
            // it is read-only everywhere else and is never written back by these forms.
            result["CALLSIGN"]     = Properties.Settings.Default.my_callsign ?? string.Empty;
            result["NAME"]         = Properties.Settings.Default.PersonalInfoName ?? string.Empty;
            result["EMAIL"]        = Properties.Settings.Default.PersonalInfoEmail ?? string.Empty;
            result["GRID-LOCATOR"] = Properties.Settings.Default.my_locator ?? string.Empty;

            if (!string.IsNullOrEmpty(contestId) && model.ByContest.TryGetValue(contestId, out var cvals))
                foreach (var kv in cvals) result[kv.Key] = kv.Value;

            return result;
        }

        // Saves the values: canonical personal -> Settings, other personal -> JSON personal bucket,
        // per-contest -> JSON byContest[contestId]. Persists Settings once.
        public static void Save(string contestId, IDictionary<string, string> values)
        {
            if (values == null) return;
            var model = ReadModel();

            foreach (var field in CabrilloHeader.Catalog)
            {
                if (field.ReadOnly) continue;   // e.g. CALLSIGN — owned elsewhere, never written here

                values.TryGetValue(field.Tag, out var v);
                v = (v ?? string.Empty).Trim();

                if (field.Scope == CabrilloFieldScope.Personal)
                {
                    switch (field.Tag)
                    {
                        case "NAME":         Properties.Settings.Default.PersonalInfoName = v; break;
                        case "EMAIL":        Properties.Settings.Default.PersonalInfoEmail = v; break;
                        case "GRID-LOCATOR": Properties.Settings.Default.my_locator = v; break;
                        default:             model.Personal[field.Tag] = v; break;
                    }
                }
                else if (!string.IsNullOrEmpty(contestId))
                {
                    if (!model.ByContest.TryGetValue(contestId, out var cvals))
                    {
                        cvals = new Dictionary<string, string>();
                        model.ByContest[contestId] = cvals;
                    }
                    cvals[field.Tag] = v;
                }
            }

            Properties.Settings.Default.CabrilloHeaderStore = JsonConvert.SerializeObject(model);
            Properties.Settings.Default.Save();
        }

        // True when every field this contest requires has a value.
        public static bool IsComplete(Contest contest, IDictionary<string, string> values)
        {
            foreach (var tag in CabrilloHeader.RequiredFor(contest))
                if (!values.TryGetValue(tag, out var v) || string.IsNullOrWhiteSpace(v))
                    return false;
            return true;
        }

        // Copies the collected values into a Contester for the Cabrillo generator.
        public static void PopulateContester(Contester c, IDictionary<string, string> v)
        {
            string G(string tag) { v.TryGetValue(tag, out var s); return string.IsNullOrWhiteSpace(s) ? null : s.Trim(); }

            c.Callsign             = G("CALLSIGN");
            c.Name                 = G("NAME");
            c.Email                = G("EMAIL");
            c.Grid                 = G("GRID-LOCATOR");
            c.Address              = G("ADDRESS");
            c.City                 = G("ADDRESS-CITY");
            c.StateProvince        = G("ADDRESS-STATE-PROVINCE");
            c.PostalCode           = G("ADDRESS-POSTALCODE");
            c.Country              = G("ADDRESS-COUNTRY");
            c.Club                 = G("CLUB");
            c.Category_Operator    = G("CATEGORY-OPERATOR");
            c.Category_Assisted    = G("CATEGORY-ASSISTED");
            c.Category_Band        = G("CATEGORY-BAND");
            c.Category_Mode        = G("CATEGORY-MODE");
            c.Category_Power       = G("CATEGORY-POWER");
            c.Category_Transmitter = G("CATEGORY-TRANSMITTER");
            c.Category_Station     = G("CATEGORY-STATION");
            c.Category_Time        = G("CATEGORY-TIME");
            c.Category_Overlay     = G("CATEGORY-OVERLAY");
            c.Location             = G("LOCATION");
            c.Score                = G("CLAIMED-SCORE");
            c.Operators            = G("OPERATORS");
            c.Certificate          = G("CERTIFICATE");
            c.Soapbox              = G("SOAPBOX");
        }
    }
}
