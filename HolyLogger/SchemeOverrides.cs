using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace HolyLogger
{
    // Persistence for the user's color edits, stored PER SCHEME on top of the hardcoded factory
    // palette (ThemePalette.Tokens). Each built-in scheme (Light, Dark, My scheme) is edited IN
    // PLACE: the user's changes are kept here under that scheme's id and re-layered on its factory
    // colors, so the scheme keeps its name and its edits are remembered across sessions. "Reset"
    // simply drops the stored override(s), returning to the factory value(s). There is deliberately
    // no separate "Custom" scheme.
    internal static class SchemeOverrides
    {
        // schemeId -> (token -> hex). Reads the settings blob, migrating the pre-2026-07 single
        // "Custom" format on the way. Never returns null.
        internal static Dictionary<string, Dictionary<string, string>> LoadAll()
        {
            string raw;
            try { raw = Properties.Settings.Default.CustomSchemeColors; }
            catch { return new Dictionary<string, Dictionary<string, string>>(); }

            if (string.IsNullOrWhiteSpace(raw))
                return new Dictionary<string, Dictionary<string, string>>();

            // Current (nested) format: { schemeId: { token: hex } }.
            try
            {
                var nested = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, string>>>(raw);
                if (nested != null) return nested;
            }
            catch (Exception ex) { Log.Swallow(ex); }

            // Legacy flat format: a single token->hex map for the old "Custom" scheme, derived from
            // a base scheme recorded separately. Fold it onto that base scheme (edit-in-place model),
            // persist in the new nested shape so this runs only once, and return.
            try
            {
                var flat = JsonConvert.DeserializeObject<Dictionary<string, string>>(raw);
                var result = new Dictionary<string, Dictionary<string, string>>();
                if (flat != null && flat.Count > 0)
                {
                    string baseId;
                    try { baseId = Properties.Settings.Default.CustomSchemeBaseId; }
                    catch { baseId = null; }
                    if (string.IsNullOrWhiteSpace(baseId)) baseId = "light";
                    result[baseId] = flat;
                    SaveAll(result);
                }
                return result;
            }
            catch (Exception ex)
            {
                Log.Swallow(ex);
                return new Dictionary<string, Dictionary<string, string>>();
            }
        }

        // A fresh copy of one scheme's overrides (token -> hex); empty when the scheme is untouched.
        internal static Dictionary<string, string> For(string schemeId)
        {
            var all = LoadAll();
            return all.TryGetValue(schemeId, out var m) && m != null
                ? new Dictionary<string, string>(m)
                : new Dictionary<string, string>();
        }

        // Replaces the stored overrides for one scheme. An empty/null map removes the scheme's entry
        // entirely, so a fully-reset scheme leaves no trace in settings.
        internal static void Save(string schemeId, Dictionary<string, string> overrides)
        {
            var all = LoadAll();
            if (overrides == null || overrides.Count == 0) all.Remove(schemeId);
            else all[schemeId] = overrides;
            SaveAll(all);
        }

        private static void SaveAll(Dictionary<string, Dictionary<string, string>> all)
        {
            try
            {
                Properties.Settings.Default.CustomSchemeColors = JsonConvert.SerializeObject(all);
                Properties.Settings.Default.Save();
            }
            catch (Exception ex) { Log.Swallow(ex); }
        }
    }
}
