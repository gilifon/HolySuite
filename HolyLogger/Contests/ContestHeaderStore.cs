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

        // THE OWNER'S OWN CALLSIGN, GRID LOCATOR AND HOLYLAND SQUARE. Options > Personal Info keeps
        // them in the JSON personal bucket, apart from the main window's Station callsign, locator and
        // Holyland boxes (my_callsign / my_locator / my_square). Those boxes are what the station is
        // NOW - loading a club log (4Z1ZV) changes the callsign, a portable day changes the locator and
        // square - and the owner's saved details must not follow. The only link is one way: a
        // main-window box that is EMPTY starts from the saved value (FillEmptyMainWindowFromPersonal).
        // Everything exported (Cabrillo, the QSOs) takes the main window's values, never these.
        public const string CallsignTag = "CALLSIGN";
        public const string GridTag = "GRID-LOCATOR";
        public const string HolylandSquareTag = "HOLYLAND-SQUARE";   // not a Cabrillo tag; never exported

        // Before this split the Personal Info grid WAS my_locator and its callsign WAS my_callsign. Once,
        // the saved values are taken from the main window, so nothing the owner typed is lost. The
        // callsign is taken from the Operator box first: that is the person, while the station callsign
        // may be a club log's that happens to be loaded.
        public static void SeedPersonalFromMainWindowOnce()
        {
            var s = Properties.Settings.Default;
            var model = ReadModel();
            bool changed = false;
            if (!model.Personal.ContainsKey(CallsignTag))
            {
                string call = (s.Operator ?? string.Empty).Trim();
                if (call.Length == 0) call = (s.my_callsign ?? string.Empty).Trim();
                model.Personal[CallsignTag] = call.ToUpperInvariant();
                changed = true;
            }
            if (!model.Personal.ContainsKey(GridTag))
            {
                model.Personal[GridTag] = (s.my_locator ?? string.Empty).Trim();
                changed = true;
            }
            if (!model.Personal.ContainsKey(HolylandSquareTag))
            {
                model.Personal[HolylandSquareTag] = (s.my_square ?? string.Empty).Trim().ToUpperInvariant();
                changed = true;
            }
            if (!changed) return;
            s.CabrilloHeaderStore = JsonConvert.SerializeObject(model);
            try { s.Save(); }
            catch (System.Exception swallowed) { Log.Swallow(swallowed); }
        }

        // The owner's saved callsign (empty when none was typed).
        public static string OwnerCallsign()
        {
            string v;
            return ReadModel().Personal.TryGetValue(CallsignTag, out v) ? (v ?? string.Empty).Trim() : string.Empty;
        }

        // An empty main-window callsign / locator / Holyland box starts from the owner's saved value. A
        // box that holds anything is left alone - the operator put it there.
        public static void FillEmptyMainWindowFromPersonal()
        {
            var s = Properties.Settings.Default;
            var model = ReadModel();
            string v;
            if (string.IsNullOrWhiteSpace(s.my_callsign)
                && model.Personal.TryGetValue(CallsignTag, out v) && !string.IsNullOrWhiteSpace(v))
                s.my_callsign = v.Trim().ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(s.my_locator)
                && model.Personal.TryGetValue(GridTag, out v) && !string.IsNullOrWhiteSpace(v))
                s.my_locator = v.Trim();
            if (string.IsNullOrWhiteSpace(s.my_square)
                && model.Personal.TryGetValue(HolylandSquareTag, out v) && !string.IsNullOrWhiteSpace(v))
                s.my_square = v.Trim().ToUpperInvariant();
        }

        // Merged current values for a contest: JSON personal, then the canonical Settings personal
        // fields (which win), then this contest's saved per-contest values. personalInfoPage = the
        // Options > Personal Info page, which shows the owner's saved grid and Holyland square; every
        // other caller (the contest windows, Cabrillo export) gets the main window's grid.
        public static Dictionary<string, string> Load(string contestId, bool personalInfoPage = false)
        {
            var model = ReadModel();
            var result = new Dictionary<string, string>();

            foreach (var kv in model.Personal) result[kv.Key] = kv.Value;

            result["NAME"]         = Properties.Settings.Default.PersonalInfoName ?? string.Empty;
            result["EMAIL"]        = Properties.Settings.Default.PersonalInfoEmail ?? string.Empty;
            if (!personalInfoPage)
            {
                // The station callsign of an export is the main-window "Station callsign" box
                // (my_callsign); it is read-only in the contest windows and never written back by them.
                result[CallsignTag] = Properties.Settings.Default.my_callsign ?? string.Empty;
                result[GridTag] = Properties.Settings.Default.my_locator ?? string.Empty;
                result.Remove(HolylandSquareTag);
            }

            if (!string.IsNullOrEmpty(contestId) && model.ByContest.TryGetValue(contestId, out var cvals))
                foreach (var kv in cvals) result[kv.Key] = kv.Value;

            return result;
        }

        // Saves the values: canonical personal -> Settings, other personal -> JSON personal bucket,
        // per-contest -> JSON byContest[contestId]. Persists Settings once.
        // personalInfoPage: see Load - the grid (and the Holyland square) go to the owner's saved values,
        // not to the main window's boxes; an empty main-window box then starts from them.
        public static void Save(string contestId, IDictionary<string, string> values, bool personalInfoPage = false)
        {
            if (values == null) return;
            var model = ReadModel();

            if (personalInfoPage && values.TryGetValue(HolylandSquareTag, out var square))
                model.Personal[HolylandSquareTag] = (square ?? string.Empty).Trim().ToUpperInvariant();
            // The catalog marks CALLSIGN read-only (for the contest windows), so the loop below skips it.
            if (personalInfoPage && values.TryGetValue(CallsignTag, out var ownerCall))
                model.Personal[CallsignTag] = (ownerCall ?? string.Empty).Trim().ToUpperInvariant();

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
                        case GridTag:
                            if (personalInfoPage) model.Personal[GridTag] = v;
                            else Properties.Settings.Default.my_locator = v;
                            break;
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
            if (personalInfoPage) FillEmptyMainWindowFromPersonal();
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
