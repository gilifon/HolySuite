using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace HolyLogger
{
    // A PROFILE is a complete named setup: every user setting, every window position/size, and which
    // windows are open. It is a snapshot of Properties.Settings, which is why the loose .txt settings
    // were moved into the config first - anything outside Properties.Settings would be invisible here.
    //
    // Profiles are plain JSON files, one per profile, so they can be backed up or shared between
    // machines and with other operators.
    internal static class ProfileManager
    {
        // Settings deliberately NOT part of a profile.
        private static readonly HashSet<string> Excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Bookkeeping: which profile is active, and the one-shot upgrade/migration flags. Carrying
            // these in a profile would make it fight the upgrade machinery.
            "ActiveProfile",
            "UpdateSettings",
            "LegacyFileSettingsMigrated",

            // Which log is being written to. Deliberately excluded: switching how the program LOOKS must
            // never silently switch which log new QSOs land in.
            "ActiveLogId",

            // Downloaded/derived caches and progress markers - not settings, and often large.
            "LotwConfirmedEntities", "LotwSeenKeysJson", "LotwLastNewJson", "LotwLastQsl",
            "LotwConfirmedQsoCount", "LotwLastNewQsls", "LotwLastNewCountries", "LotwLastCheckSince",
            "RecentQSOCounter",
        };

        // Window geometry: everything that describes WHERE the windows are, as opposed to how the
        // program behaves.
        //
        // It is EXCLUDED from profiles entirely (see Excluded below). Profiles are re-applied on every
        // startup, so holding geometry in them meant the profile's old positions overwrote wherever the
        // operator had actually left the windows -- the window genuinely stopped remembering its place.
        // Geometry now lives only in user.config, written when each window closes, which is the one
        // store nothing else competes with. The trade-off, deliberately taken: switching profile no
        // longer rearranges the windows.
        private static bool IsWindowLayoutSetting(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (string.Equals(name, "WindowBoundsJson", StringComparison.OrdinalIgnoreCase)) return true;
            return name.EndsWith("WindowLeft", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("WindowTop", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("WindowWidth", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("WindowHeight", StringComparison.OrdinalIgnoreCase);
        }

        public static string ProfilesFolder
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "HolyLogger", "Profiles");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        public static string ActiveProfile
        {
            get { return Properties.Settings.Default.ActiveProfile ?? string.Empty; }
        }

        // Profile names double as file names, so keep them to something a file system accepts.
        public static bool IsValidName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            return name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
        }

        public static string PathFor(string name) => Path.Combine(ProfilesFolder, name + ".json");

        public static bool Exists(string name) => IsValidName(name) && File.Exists(PathFor(name));

        public static List<string> List()
        {
            try
            {
                return Directory.GetFiles(ProfilesFolder, "*.json")
                                .Select(Path.GetFileNameWithoutExtension)
                                .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)
                                .ToList();
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
            return new List<string>();
        }

        // The current configuration as name -> value. SerializedValue is the same string form user.config
        // stores, so it round-trips every type (bool, int, double, string) without per-type handling.
        private static Dictionary<string, string> CaptureCurrent()
        {
            var values = new Dictionary<string, string>();
            foreach (System.Configuration.SettingsPropertyValue v in Properties.Settings.Default.PropertyValues)
            {
                if (Excluded.Contains(v.Name) || IsWindowLayoutSetting(v.Name)) continue;
                values[v.Name] = v.SerializedValue?.ToString() ?? string.Empty;
            }
            return values;
        }

        // Writes the CURRENT configuration to a profile file, overwriting it if it exists.
        public static bool Save(string name)
        {
            if (!IsValidName(name)) return false;
            try
            {
                File.WriteAllText(PathFor(name),
                    JsonConvert.SerializeObject(CaptureCurrent(), Formatting.Indented));
                return true;
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); return false; }
        }

        // Loads the active profile at startup, so the program always begins from what that profile
        // holds. Anything changed last session and not saved into it is deliberately NOT carried over -
        // the operator is asked about that when closing.
        //
        // If the active profile is GONE (file deleted, moved, drive unavailable) we fall back to factory
        // defaults rather than silently continuing on the leftover live config: that leftover is a stale
        // mix belonging to a profile that no longer exists, and starting from a known state is easier to
        // reason about. Returns the name of the missing profile so the caller can say what happened, or
        // null when nothing needed reporting.
        public static string ApplyActiveProfileAtStartup()
        {
            string name = ActiveProfile;
            if (string.IsNullOrWhiteSpace(name)) return null;   // no profile in use -> leave as-is

            if (!Exists(name))
            {
                RestoreFactoryDefaults();   // also clears ActiveProfile
                return name;
            }

            Apply(name);
            return null;
        }

        // True when the live configuration no longer matches the active profile file, i.e. there are
        // changes that would be lost. False when there is no active profile - then there is nothing to
        // compare against and nothing to warn about.
        public static bool CurrentDiffersFromActive()
        {
            string name = ActiveProfile;
            if (string.IsNullOrWhiteSpace(name) || !Exists(name)) return false;
            try
            {
                var saved = JsonConvert.DeserializeObject<Dictionary<string, string>>(
                    File.ReadAllText(PathFor(name)));
                if (saved == null) return false;

                foreach (var kv in CaptureCurrent())
                {
                    // Window geometry is auto-saved on exit, so it is never an "unsaved change" to ask
                    // about. Without this, moving any window would prompt on every single close.
                    if (IsWindowLayoutSetting(kv.Key)) continue;

                    saved.TryGetValue(kv.Key, out string old);
                    if (!string.Equals(old ?? string.Empty, kv.Value ?? string.Empty, StringComparison.Ordinal))
                        return true;
                }
                return false;
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); return false; }
        }

        // Loads a profile into Properties.Settings and saves. The caller restarts the app so everything
        // (window layout, colours, which windows open) is rebuilt from the new values.
        public static bool Apply(string name)
        {
            if (!Exists(name)) return false;
            try
            {
                var values = JsonConvert.DeserializeObject<Dictionary<string, string>>(
                    File.ReadAllText(PathFor(name)));
                if (values == null) return false;

                var s = Properties.Settings.Default;
                foreach (var kv in values)
                {
                    if (Excluded.Contains(kv.Key) || IsWindowLayoutSetting(kv.Key)) continue;
                    try
                    {
                        var prop = s.Properties[kv.Key];
                        if (prop == null) continue;   // setting removed/renamed since the profile was made
                        s[kv.Key] = System.ComponentModel.TypeDescriptor
                            .GetConverter(prop.PropertyType)
                            .ConvertFromInvariantString(kv.Value);
                    }
                    catch (Exception swallowed) { Log.Swallow(swallowed); }   // skip one bad value, keep going
                }

                s.ActiveProfile = name;
                s.Save();
                return true;
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); return false; }
        }

        // Puts every setting back to the default declared in Settings.Designer.cs - the escape hatch when
        // a setup has been wrecked and there is no profile to fall back to.
        //
        // The SAME list that is left out of a profile is left out here: the active log must not change
        // (that would move where QSOs are written), the upgrade/migration flags must not be rewound, and
        // resetting the LoTW caches would only force a slow re-download for no benefit.
        public static bool RestoreFactoryDefaults()
        {
            try
            {
                var s = Properties.Settings.Default;
                foreach (System.Configuration.SettingsProperty prop in s.Properties)
                {
                    if (Excluded.Contains(prop.Name) || IsWindowLayoutSetting(prop.Name)) continue;
                    try
                    {
                        object def = prop.DefaultValue;
                        s[prop.Name] = def is string text
                            ? System.ComponentModel.TypeDescriptor.GetConverter(prop.PropertyType)
                                    .ConvertFromInvariantString(text)
                            : def;
                    }
                    catch (Exception swallowed) { Log.Swallow(swallowed); }   // skip one bad setting, keep going
                }

                s.ActiveProfile = string.Empty;   // the setup no longer matches any saved profile
                s.Save();
                return true;
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); return false; }
        }

        public static bool Delete(string name)
        {
            if (!Exists(name)) return false;
            try
            {
                File.Delete(PathFor(name));
                if (string.Equals(ActiveProfile, name, StringComparison.OrdinalIgnoreCase))
                {
                    Properties.Settings.Default.ActiveProfile = string.Empty;
                    Properties.Settings.Default.Save();
                }
                return true;
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); return false; }
        }

        public static bool Rename(string oldName, string newName)
        {
            if (!Exists(oldName) || !IsValidName(newName) || Exists(newName)) return false;
            try
            {
                File.Move(PathFor(oldName), PathFor(newName));
                if (string.Equals(ActiveProfile, oldName, StringComparison.OrdinalIgnoreCase))
                {
                    Properties.Settings.Default.ActiveProfile = newName;
                    Properties.Settings.Default.Save();
                }
                return true;
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); return false; }
        }

        public static bool ImportFrom(string sourceFile, string newName)
        {
            if (!IsValidName(newName) || !File.Exists(sourceFile)) return false;
            try { File.Copy(sourceFile, PathFor(newName), true); return true; }
            catch (Exception swallowed) { Log.Swallow(swallowed); return false; }
        }

        public static bool ExportTo(string name, string targetFile)
        {
            if (!Exists(name)) return false;
            try { File.Copy(PathFor(name), targetFile, true); return true; }
            catch (Exception swallowed) { Log.Swallow(swallowed); return false; }
        }

        // Relaunch so every window is rebuilt from the newly applied settings.
        public static void RestartApplication()
        {
            try
            {
                // Hand back the single-instance mutex FIRST. The new process starts while this one is
                // still shutting down, and would otherwise hit the guard and refuse to open.
                App.ReleaseSingleInstanceMutex();

                System.Diagnostics.Process.Start(
                    System.Reflection.Assembly.GetEntryAssembly().Location);
                System.Windows.Application.Current.Shutdown();
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }
    }
}
