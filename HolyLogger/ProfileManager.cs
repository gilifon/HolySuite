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

            // "I already said not now" about the kHz frequency repair. That answer is about the LOG,
            // so switching profile must neither re-ask nor silently inherit someone else's refusal.
            "FreqRepairDeclinedCount",

            // ── WHAT THE PROGRAM WRITES ABOUT ITSELF, WHICH NOBODY CHOSE ────────────────────────
            //
            // All of these move on their own, and every one of them was making the program say "you
            // changed settings since the profile was saved" to a man who had changed nothing.
            //
            // THE RADIO'S FREQUENCY IS THE WORST OF THEM. It is written every time the VFO moves, so
            // anyone with CAT connected had a "changed" profile within seconds of switching on, and was
            // asked on every close for ever after.
            "Frequency",

            // The serial number the contest is up to - it counts itself, one per QSO.
            "ContestNextSerial",

            // What eQSL, QRZ and Club Log sent back when their confirmations were last downloaded.
            // Downloaded data, not settings. The LoTW ones were already excluded above; these three
            // were simply missed, and a confirmation check made the profile look changed.
            "EqslConfirmedEntities", "EqslConfirmedQsoCount", "EqslConfirmedDeletedCodes",
            "QrzConfirmedEntities", "QrzConfirmedQsoCount", "QrzConfirmedDeletedCodes",
            "ClublogConfirmedEntities", "ClublogConfirmedQsoCount", "ClublogConfirmedDeletedCodes",

            // Where he was last: the cluster's minutes filter, the eQSL row he last had selected, the
            // activity program he last used. Each written as he works, none of them a choice about how
            // the program behaves.
            "ClusterLastMinutesFilter", "EqslLastSelectedCallsign", "LastActivityProgram",

            // NOT EXCLUDED, deliberately: SignBoardWindowIsOpen, MatrixWindowIsOpen, TimerWindowIsOpen
            // and HasClosedClusterWindow. Those say which windows were open, and a profile is expected
            // to bring back the set of windows it was saved with.
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
            // Where the QSO editor sits, written when it closes. A window position like any other; it
            // is only spelled differently - one setting holding "left,top" instead of two.
            if (string.Equals(name, "QsoEditWindowPos", StringComparison.OrdinalIgnoreCase)) return true;
            if (IsColumnLayoutSetting(name)) return true;
            return name.EndsWith("WindowLeft", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("WindowTop", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("WindowWidth", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("WindowHeight", StringComparison.OrdinalIgnoreCase);
        }

        // ── AND WHERE THE COLUMNS SIT, WHICH IS THE SAME KIND OF THING ──────────────────────────
        //
        // "You changed settings since the profile was saved" - asked on EVERY close, of a man who had
        // changed nothing. This is why.
        //
        // The log grid's column layout is written at every close, and it holds each column's WIDTH. The
        // columns are Auto-sized, so a width is whatever the DATA measured to: log a QSO with a longer
        // callsign or a longer country name and the column comes out a few pixels wider. That number
        // went into the profile, so once the real grid differed from the saved one by a pixel it
        // differed on every close afterwards, for ever, and he was asked every time.
        //
        // A column width is not a setting anybody chose. It is layout the program maintains - the same
        // class of thing as where a window sits, which was taken out of profiles for the same reason and
        // lives in user.config instead. So these go with it.
        //
        // Matched by name rather than listed one by one: a grid added later will bring its own layout
        // setting, and it should be out of profiles from the day it appears rather than from the day
        // somebody remembers to add it here.
        private static bool IsColumnLayoutSetting(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;

            // The log grid's own columns are named ColWidthCallsign, Callsign_index and so on - a width
            // and a position per column - and the cluster's are ClusterColWidthDX and the rest. Matched
            // by shape rather than listed, so a column added later is out of profiles from the day it
            // appears rather than from the day somebody remembers to add it here.
            return name.EndsWith("ColumnLayout", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("ColumnOrder", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("ColumnWidth", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("ColumnDisplayIndex", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("_index", StringComparison.Ordinal)
                || name.IndexOf("ColWidth", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // ── WHY THE LAST ONE FAILED ─────────────────────────────────────────────────────────────
        //
        // Everything below answers true or false, and every failure used to end in the same place: the
        // reason written to the log file and thrown away here. So the Profiles window could only ever
        // say "Could not save the profile." - a sentence that tells a man his profile is not saved and
        // nothing whatever about what to do, when the answer was usually one line long and right there
        // in the exception ("access to the path is denied", "the device is not ready").
        //
        // Kept as the reason for the LAST call, read straight after it, which is how every caller here
        // uses it. It is not thread-safe and does not need to be: these are all button presses on one
        // window, one at a time.
        public static string LastError { get; private set; }

        // Records the reason and answers false, so a catch is one line and can never forget to do both.
        private static bool Failed(Exception ex)
        {
            Log.Swallow(ex);
            LastError = ex == null ? null : ex.Message;
            return false;
        }

        // For the refusals that are not exceptions - a name with a slash in it, a profile that is not
        // there any more. Same idea: say why, answer false.
        private static bool Failed(string why)
        {
            LastError = why;
            return false;
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
            LastError = null;
            if (!IsValidName(name)) return Failed("\"" + name + "\" is not a name a file can have.");
            try
            {
                File.WriteAllText(PathFor(name),
                    JsonConvert.SerializeObject(CaptureCurrent(), Formatting.Indented));
                return true;
            }
            catch (Exception ex) { return Failed(ex); }
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
            catch (Exception ex) { return Failed(ex); }
        }

        // Loads a profile into Properties.Settings and saves. The caller restarts the app so everything
        // (window layout, colours, which windows open) is rebuilt from the new values.
        public static bool Apply(string name)
        {
            LastError = null;
            if (!Exists(name)) return Failed("The profile file is not in the Profiles folder any more.");
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
            catch (Exception ex) { return Failed(ex); }
        }

        // Puts every setting back to the default declared in Settings.Designer.cs - the escape hatch when
        // a setup has been wrecked and there is no profile to fall back to.
        //
        // The SAME list that is left out of a profile is left out here: the active log must not change
        // (that would move where QSOs are written), the upgrade/migration flags must not be rewound, and
        // resetting the LoTW caches would only force a slow re-download for no benefit.
        public static bool RestoreFactoryDefaults()
        {
            LastError = null;
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
            catch (Exception ex) { return Failed(ex); }
        }

        public static bool Delete(string name)
        {
            LastError = null;
            if (!Exists(name)) return Failed("The profile file is not in the Profiles folder any more.");
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
            catch (Exception ex) { return Failed(ex); }
        }

        public static bool Rename(string oldName, string newName)
        {
            LastError = null;
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
            catch (Exception ex) { return Failed(ex); }
        }

        public static bool ImportFrom(string sourceFile, string newName)
        {
            LastError = null;
            if (!IsValidName(newName) || !File.Exists(sourceFile)) return false;
            try { File.Copy(sourceFile, PathFor(newName), true); return true; }
            catch (Exception ex) { return Failed(ex); }
        }

        public static bool ExportTo(string name, string targetFile)
        {
            LastError = null;
            if (!Exists(name)) return Failed("The profile file is not in the Profiles folder any more.");
            try { File.Copy(PathFor(name), targetFile, true); return true; }
            catch (Exception ex) { return Failed(ex); }
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
