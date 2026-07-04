using System;
using System.Collections.Generic;

namespace HolyLogger
{
    // Persistence for the user-defined "Custom" color scheme (View > Color Scheme > Customize
    // Colors). Stored in user settings as a JSON map of token -> hex, plus the id of the built-in
    // scheme it was derived from: that base supplies the dark/light window chrome, per-token
    // "Reset" values in the editor, and a fallback for any token added to the palette after the
    // custom scheme was saved.
    internal static class CustomSchemeStore
    {
        // The scheme id under which the custom scheme appears in the menu and in ColorSchemeId.
        internal const string Id = "custom";

        internal static bool Exists
        {
            get
            {
                try { return !string.IsNullOrWhiteSpace(Properties.Settings.Default.CustomSchemeColors); }
                catch { return false; }
            }
        }

        // The built-in scheme the custom colors were derived from ("light"/"dark"/...).
        internal static string BaseId
        {
            get
            {
                try
                {
                    string id = Properties.Settings.Default.CustomSchemeBaseId;
                    return string.IsNullOrWhiteSpace(id) ? "light" : id;
                }
                catch { return "light"; }
            }
        }

        // The stored token -> hex map, or null when no custom scheme exists / it cannot be read.
        internal static Dictionary<string, string> Load()
        {
            try
            {
                string raw = Properties.Settings.Default.CustomSchemeColors;
                if (string.IsNullOrWhiteSpace(raw)) return null;
                return Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, string>>(raw);
            }
            catch (Exception ex)
            {
                Log.Swallow(ex);
                return null;
            }
        }

        internal static void Save(Dictionary<string, string> colors, string baseId)
        {
            try
            {
                Properties.Settings.Default.CustomSchemeColors = Newtonsoft.Json.JsonConvert.SerializeObject(colors);
                Properties.Settings.Default.CustomSchemeBaseId = baseId ?? "light";
                Properties.Settings.Default.Save();
            }
            catch (Exception ex) { Log.Swallow(ex); }
        }

        internal static void Delete()
        {
            try
            {
                Properties.Settings.Default.CustomSchemeColors = string.Empty;
                Properties.Settings.Default.Save();
            }
            catch (Exception ex) { Log.Swallow(ex); }
        }
    }
}
