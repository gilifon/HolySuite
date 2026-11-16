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

            // THE CW SPEED, for the same reason as the frequency above it, and reported from a real
            // evening: 8.9.3, an operator working CW who took the keyer from 18 WPM up to 26 with the
            // wheel as the other station's fist changed - and was asked, on closing, whether to save
            // the settings he had changed. He had changed his sending speed. Nothing about it is a
            // decision about how the program behaves. (The keyer stopped writing it altogether when the
            // speed became the radio knob's business alone - see CwKeyboardWindow.)
            //
            // Still remembered, exactly as before - it lives in user.config with the window positions,
            // so the keyer still opens at the speed he left it. It is only out of PROFILES.
            "CwKeyerWpm",

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

            // MORE OF THE SAME, found in a full review of every setting (2026-11-14). Each of these is
            // written by the program as he works, so each one made the profile look changed for
            // nothing - and, worse, was put back to the profile's old value on the next start.
            //
            // Mode: the main window's Mode box is bound to it, and CAT sets that box from the radio -
            // the same reason Frequency is out. Comment: bound to the Comment box, so it holds whatever
            // happened to be typed when he closed. LastMyCastleRef: the castle he last typed, like
            // LastActivityProgram. LastExportFolder: where the last export went.
            "Mode", "Comment", "LastMyCastleRef", "LastExportFolder",

            // Layout and window state, the same class as window positions: the RTTY waterfall's height
            // (dragging the divider) and whether the cluster and radio panel were minimized.
            "RttyWaterfallHeight", "ClusterWindowMinimized", "RadioPanelMinimized",

            // "The extra backup folder was already offered" - a one-time flag. In a profile, an old
            // profile or Restore Factory Defaults set it back to false and the offer came up again.
            "ExtraBackupPrompted",

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
                var values = CaptureCurrent();

                // A move kit not applied yet (an imported profile, before the start that applies it)
                // stays in the file: a save on the way out of the profile switch must not throw away
                // the eQSL accounts and the rest brought from the main computer.
                try
                {
                    if (File.Exists(PathFor(name)))
                    {
                        var old = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(PathFor(name)));
                        if (old != null && old.TryGetValue(MoveKitKey, out string kit) && !string.IsNullOrWhiteSpace(kit))
                            values[MoveKitKey] = kit;
                    }
                }
                catch (Exception swallowed) { Log.Swallow(swallowed); }

                File.WriteAllText(PathFor(name),
                    JsonConvert.SerializeObject(values, Formatting.Indented));
                return true;
            }
            catch (Exception ex) { return Failed(ex); }
        }

        // Writes ONE setting's current value into the active profile file, leaving the rest of it as it
        // was saved. For the station callsign and operator: startup reloads the profile, and when Windows
        // shuts down with HolyLogger open the "save into the profile?" question is skipped - so a callsign
        // typed in that session was lost on the next start (4Z1GY, 8.9.9). Also saves user.config, which
        // is where the value lives when no profile is in use.
        public static void SaveOneIntoActive(string settingName)
        {
            try
            {
                Properties.Settings.Default.Save();

                string name = ActiveProfile;
                if (string.IsNullOrWhiteSpace(name) || !Exists(name)) return;
                if (Excluded.Contains(settingName) || IsWindowLayoutSetting(settingName)) return;

                var value = Properties.Settings.Default.PropertyValues[settingName];
                if (value == null) return;

                var saved = JsonConvert.DeserializeObject<Dictionary<string, string>>(
                    File.ReadAllText(PathFor(name))) ?? new Dictionary<string, string>();
                string now = value.SerializedValue?.ToString() ?? string.Empty;
                if (saved.TryGetValue(settingName, out string old) && string.Equals(old, now, StringComparison.Ordinal))
                    return;

                saved[settingName] = now;
                File.WriteAllText(PathFor(name), JsonConvert.SerializeObject(saved, Formatting.Indented));
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
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

            // A profile carrying a move kit was just switched to, and the switch STARTS THIS COPY BEFORE
            // THE OLD ONE HAS FINISHED CLOSING (RestartApplication). The old copy writes user.config as
            // it goes - its own window places among it - so anything applied here could be written over
            // a moment later. Only on this once-only start: wait for the old copy to be gone, read the
            // settings again from what it left, and only then apply.
            if (HasMoveKit(name))
            {
                WaitForOtherCopyToClose();
                try { Properties.Settings.Default.Reload(); }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
            }

            Apply(name);
            ApplyMoveKitSettings(name);
            return null;
        }

        // True when the live configuration no longer matches the active profile file, i.e. there are
        // changes that would be lost. False when there is no active profile - then there is nothing to
        // compare against and nothing to warn about.
        public static bool CurrentDiffersFromActive()
        {
            return WhatDiffersFromActive().Count > 0;
        }

        // ── AND WHAT, BY NAME ───────────────────────────────────────────────────────────────────
        //
        // "You changed settings since the profile was saved" is a question a man cannot answer,
        // because it does not say what changed - so Yes and No are both a guess, and the safe guess
        // is Yes, which quietly writes whatever moved into his profile for ever.
        //
        // Every one of them, not the first: a man who spent the evening on CW and also turned the
        // cluster's Live Scale on wants to see both, or he answers about one and is surprised by the
        // other. The whole list is walked now rather than returning at the first difference.
        public static List<string> WhatDiffersFromActive()
        {
            var changed = new List<string>();

            string name = ActiveProfile;
            if (string.IsNullOrWhiteSpace(name) || !Exists(name)) return changed;
            try
            {
                var saved = JsonConvert.DeserializeObject<Dictionary<string, string>>(
                    File.ReadAllText(PathFor(name)));
                if (saved == null) return changed;

                foreach (var kv in CaptureCurrent())
                {
                    // Window geometry is auto-saved on exit, so it is never an "unsaved change" to ask
                    // about. Without this, moving any window would prompt on every single close.
                    if (IsWindowLayoutSetting(kv.Key)) continue;

                    saved.TryGetValue(kv.Key, out string old);
                    if (!string.Equals(old ?? string.Empty, kv.Value ?? string.Empty, StringComparison.Ordinal))
                    {
                        // ── AND WHICH SETTING IT WAS ────────────────────────────────────────────
                        //
                        // "You changed settings" has now been asked of more than one man who had
                        // changed nothing, and each time finding out which setting moved meant reading
                        // the source and guessing. The answer costs one line, and the next report
                        // carries it instead of a photograph of the question.
                        //
                        // THE NAME ONLY, NEVER THE VALUE. A profile holds every setting there is,
                        // and that includes LotwWebPassword, ClublogPassword and the AI API keys.
                        // This log is written to be MAILED to the developer, so a line printing what
                        // a setting changed to would post somebody's password to me the first time it
                        // was a password that had changed. The name is what identifies the culprit;
                        // the value never was.
                        Log.Warn("Profile \"" + name + "\" differs from what is running: "
                                 + kv.Key + " (value not logged)");
                        changed.Add(kv.Key);
                    }
                }
                return changed;
            }
            catch (Exception ex) { Failed(ex); return changed; }
        }

        // ── A SETTING'S NAME AS SOMETHING TO READ ───────────────────────────────────────────────
        //
        // "CwKeyerWpm" is what the file calls it, and it tells an operator nothing. The ones he is
        // likely to meet are written out; anything not on the list is split at its capitals, which
        // turns ClusterLiveScaleOn into "Cluster Live Scale On" - not English, but a great deal more
        // than the run-together form, and honest about being the setting's own name.
        //
        // A list, not a rule: only a person can say that CwKeyerWpm is "CW keyer speed". It is added
        // to when somebody meets a name that puzzles him, and no build depends on it being complete.
        private static readonly Dictionary<string, string> PlainNames =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "ClusterLiveScaleOn", "Cluster: Live Scale" },
            { "SignBoardWindowIsOpen", "Sign Board window open" },
            { "MatrixWindowIsOpen", "Matrix window open" },
            { "TimerWindowIsOpen", "Timer window open" },
            { "HasClosedClusterWindow", "Cluster window closed" },
        };

        public static string PlainName(string setting)
        {
            if (string.IsNullOrEmpty(setting)) return string.Empty;
            if (PlainNames.TryGetValue(setting, out string plain)) return plain;

            var sb = new System.Text.StringBuilder(setting.Length + 8);
            for (int i = 0; i < setting.Length; i++)
            {
                if (i > 0 && char.IsUpper(setting[i]) && !char.IsUpper(setting[i - 1])) sb.Append(' ');
                sb.Append(setting[i]);
            }
            return sb.ToString();
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

        // ── STOP USING THE PROFILE, KEEP EVERYTHING ─────────────────────────────────────────────
        //
        // Asked for 2026-11-16: the only ways out of a profile were Delete (lose the profile) and
        // Restore factory defaults (lose every setting). This one changes NO setting and keeps the
        // profile file; it only stops the program starting from the profile and asking about it on
        // close. From then on changes are simply remembered, as with no profile. No restart needed:
        // nothing about how the program looks or behaves changes.
        public static bool StopUsing()
        {
            LastError = null;
            try
            {
                Properties.Settings.Default.ActiveProfile = string.Empty;
                Properties.Settings.Default.Save();
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

        // The exported file is the profile PLUS the move kit (below), so loading it on a second computer
        // gives him the whole setup he is used to, not only the part a profile switch carries.
        public static bool ExportTo(string name, string targetFile)
        {
            LastError = null;
            if (!Exists(name)) return Failed("The profile file is not in the Profiles folder any more.");
            try
            {
                var values = JsonConvert.DeserializeObject<Dictionary<string, string>>(
                    File.ReadAllText(PathFor(name))) ?? new Dictionary<string, string>();
                values[MoveKitKey] = JsonConvert.SerializeObject(BuildMoveKit());
                File.WriteAllText(targetFile, JsonConvert.SerializeObject(values, Formatting.Indented));
                return true;
            }
            catch (Exception ex) { return Failed(ex); }
        }

        // ── THE MOVE KIT: EVERYTHING ELSE HE NEEDS ON A SECOND COMPUTER ─────────────────────────
        //
        // The operator's rule (2026-11-16): export the profile on the main computer, load it on the
        // second one, and have exactly the setup he is used to. A profile on its own does not do that,
        // for two reasons:
        //
        //  1. Some of his setup is not in Properties.Settings at all: the eQSL account table (station
        //     callsign, username, password per row) is in the log database, Contest Radio Setup is its
        //     own file, and a Club Log key of his own is a file beside the database. (LoTW, QRZ and Club
        //     Log logins ARE settings and travel in the profile as they always did.)
        //  2. Some settings are left out of a profile on purpose - window positions, column widths, the
        //     CW speed - because a profile is re-applied on EVERY start, and carrying them would put the
        //     windows back where the profile was saved each time instead of where he left them.
        //
        // So the exported FILE carries them in one extra entry, and they are applied ONCE: at the start
        // after he switches to the imported profile. Then the entry is taken out of the profile file,
        // so from that start on the second computer remembers its own windows like the first one does.
        //
        // Never carried: the log itself, which log is active, the Google Drive sign-in, downloaded
        // caches, and the bookkeeping flags - see Excluded.
        private const string MoveKitKey = "~MoveKit";

        // Settings left out of profiles that DO belong to his setup, so they travel to a new computer.
        // Window and column layout is matched by IsWindowLayoutSetting on top of these.
        private static readonly HashSet<string> CarriedOnMove = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CwKeyerWpm", "ClusterLastMinutesFilter", "LastActivityProgram", "LastMyCastleRef",
            "LastExportFolder", "RttyWaterfallHeight", "ClusterWindowMinimized", "RadioPanelMinimized",
        };

        private sealed class MoveKit
        {
            public Dictionary<string, string> Settings { get; set; }
            public List<EqslAccount> EqslAccounts { get; set; }
            public string ContestRadioCommands { get; set; }
            public string ClublogUserKey { get; set; }
        }

        private static MoveKit BuildMoveKit()
        {
            var kit = new MoveKit { Settings = new Dictionary<string, string>() };
            foreach (System.Configuration.SettingsPropertyValue v in Properties.Settings.Default.PropertyValues)
            {
                if (IsWindowLayoutSetting(v.Name) || CarriedOnMove.Contains(v.Name))
                    kit.Settings[v.Name] = v.SerializedValue?.ToString() ?? string.Empty;
            }

            try { kit.EqslAccounts = DataAccess.GetInstance().GetEqslAccounts(); }
            catch (Exception swallowed) { Log.Swallow(swallowed); }

            kit.ContestRadioCommands = Contests.ContestRadioCommands.ReadFileText();

            try
            {
                if (File.Exists(ClublogKey.UserKeyPath))
                    kit.ClublogUserKey = File.ReadAllText(ClublogKey.UserKeyPath);
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }

            return kit;
        }

        // The profile whose move kit was applied at this start, and its eQSL accounts: those go into the
        // database, which is not open yet this early, so MainWindow finishes the job (FinishMoveKit).
        private static string _moveKitProfile;
        private static List<EqslAccount> _moveKitEqsl;

        private static MoveKit ReadMoveKit(string name, out Dictionary<string, string> values)
        {
            values = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(PathFor(name)));
            if (values == null || !values.TryGetValue(MoveKitKey, out string json) || string.IsNullOrWhiteSpace(json))
                return null;
            return JsonConvert.DeserializeObject<MoveKit>(json);
        }

        private static bool HasMoveKit(string name)
        {
            try { return ReadMoveKit(name, out _) != null; }
            catch (Exception swallowed) { Log.Swallow(swallowed); return false; }
        }

        // Up to 30 seconds - an upload-on-exit can keep the old copy busy for a while. If it is still
        // there after that (a real second copy, not a restart), carry on; the single-instance guard
        // further on in startup deals with that case as it always has.
        private static void WaitForOtherCopyToClose()
        {
            try
            {
                var me = System.Diagnostics.Process.GetCurrentProcess();
                foreach (var p in System.Diagnostics.Process.GetProcessesByName(me.ProcessName))
                {
                    try { if (p.Id != me.Id) p.WaitForExit(30000); }
                    catch (Exception swallowed) { Log.Swallow(swallowed); }
                    finally { p.Dispose(); }
                }
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // At startup, straight after the profile itself: the settings and files of the move kit. Runs
        // before any window is built, so the windows open where they were on the main computer.
        private static void ApplyMoveKitSettings(string name)
        {
            try
            {
                MoveKit kit = ReadMoveKit(name, out _);
                if (kit == null) return;

                var s = Properties.Settings.Default;
                var settings = kit.Settings ?? new Dictionary<string, string>();
                DropPlacesNotOnThisComputer(settings);
                foreach (var kv in settings)
                {
                    try
                    {
                        var prop = s.Properties[kv.Key];
                        if (prop == null) continue;
                        s[kv.Key] = System.ComponentModel.TypeDescriptor
                            .GetConverter(prop.PropertyType)
                            .ConvertFromInvariantString(kv.Value);
                    }
                    catch (Exception swallowed) { Log.Swallow(swallowed); }
                }
                s.Save();

                Contests.ContestRadioCommands.WriteFileText(kit.ContestRadioCommands);

                if (!string.IsNullOrWhiteSpace(kit.ClublogUserKey))
                {
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(ClublogKey.UserKeyPath));
                        File.WriteAllText(ClublogKey.UserKeyPath, kit.ClublogUserKey);
                    }
                    catch (Exception swallowed) { Log.Swallow(swallowed); }
                }

                _moveKitProfile = name;
                _moveKitEqsl = kit.EqslAccounts ?? new List<EqslAccount>();
                Log.Warn("Profile \"" + name + "\": settings from another computer applied ("
                         + settings.Count + " settings, " + _moveKitEqsl.Count + " eQSL accounts to add)");
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // Called by MainWindow once the log database is open: the eQSL accounts, then the move kit is
        // taken out of the profile file so it is never applied again. An account already in this
        // computer's table (same station callsign) gets the main computer's username and password; one
        // that is only here is left alone.
        public static void FinishMoveKit()
        {
            string name = _moveKitProfile;
            if (string.IsNullOrEmpty(name)) return;
            _moveKitProfile = null;
            try
            {
                var da = DataAccess.GetInstance();
                foreach (EqslAccount a in _moveKitEqsl ?? new List<EqslAccount>())
                {
                    if (a == null || string.IsNullOrWhiteSpace(a.Callsign)) continue;
                    EqslAccount here = da.GetEqslAccount(a.Callsign);
                    var row = new EqslAccount
                    {
                        Id = here?.Id ?? 0,
                        Callsign = a.Callsign,
                        Username = a.Username,
                        Password = a.Password,
                    };
                    if (!da.SaveEqslAccount(row, out string error))
                        Log.Warn("eQSL account " + a.Callsign + " not added: " + error);
                }

                ReadMoveKit(name, out Dictionary<string, string> values);
                if (values != null && values.Remove(MoveKitKey))
                    File.WriteAllText(PathFor(name), JsonConvert.SerializeObject(values, Formatting.Indented));
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
            finally { _moveKitEqsl = null; }
        }

        // The second computer may have other screens. A window place from the main computer is kept
        // only when the window's title bar would land on one of THIS computer's screens; otherwise that
        // window's Left/Top are dropped (its size is kept) and it opens where it would on a fresh
        // install. Nothing is moved - a place is either taken as it was or not taken at all.
        // (Windows kept in WindowBoundsJson need none of this: WindowBounds already brings a restored
        // window wholly onto a screen.)
        private static void DropPlacesNotOnThisComputer(Dictionary<string, string> settings)
        {
            var c = System.Globalization.CultureInfo.InvariantCulture;
            foreach (string leftKey in settings.Keys.Where(k => k.EndsWith("WindowLeft", StringComparison.OrdinalIgnoreCase)).ToList())
            {
                string topKey = leftKey.Substring(0, leftKey.Length - "Left".Length) + "Top";
                if (!settings.TryGetValue(topKey, out string topText)) continue;
                if (double.TryParse(settings[leftKey], System.Globalization.NumberStyles.Float, c, out double l)
                    && double.TryParse(topText, System.Globalization.NumberStyles.Float, c, out double t)
                    && TitleBarIsOnThisComputer(l, t))
                    continue;
                settings.Remove(leftKey);
                settings.Remove(topKey);
            }

            // The QSO editor keeps its place as one "left,top" setting.
            if (settings.TryGetValue("QsoEditWindowPos", out string pos))
            {
                string[] parts = (pos ?? string.Empty).Split(',');
                bool keep = parts.Length == 2
                    && double.TryParse(parts[0], System.Globalization.NumberStyles.Float, c, out double l)
                    && double.TryParse(parts[1], System.Globalization.NumberStyles.Float, c, out double t)
                    && TitleBarIsOnThisComputer(l, t);
                if (!keep) settings.Remove("QsoEditWindowPos");
            }
        }

        // Window places are in WPF units, the screens in pixels; with no window yet, the primary
        // screen's two sizes give the scale between them.
        private static bool TitleBarIsOnThisComputer(double left, double top)
        {
            try
            {
                double scale = 1.0;
                var primary = System.Windows.Forms.Screen.PrimaryScreen;
                if (primary != null && System.Windows.SystemParameters.PrimaryScreenWidth > 0)
                    scale = primary.Bounds.Width / System.Windows.SystemParameters.PrimaryScreenWidth;
                if (scale <= 0) scale = 1.0;

                var spot = new System.Windows.Point(left + 40, top + 15);
                foreach (var screen in System.Windows.Forms.Screen.AllScreens)
                {
                    var wa = screen.WorkingArea;
                    var r = new System.Windows.Rect(wa.Left / scale, wa.Top / scale, wa.Width / scale, wa.Height / scale);
                    if (r.Contains(spot)) return true;
                }
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
            return false;
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
