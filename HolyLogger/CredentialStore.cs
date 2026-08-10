using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace HolyLogger
{
    // Keeps the online-service credentials alive across version upgrades AND reinstalls.
    //
    // WHY THIS EXISTS: settings live in a per-version, per-install-identity user.config folder, and
    // .NET's Settings.Upgrade() only bridges versions WITHIN one install identity. When the installed
    // path / deployment identity changes - this machine already carries several such identity folders -
    // Upgrade finds no previous config and every credential starts blank, forcing the operator to
    // re-enter their QRZ / eQSL / Club Log / LoTW logins. This mirror lives at a FIXED path under
    // Roaming AppData, independent of version and install identity, so those credentials can always be
    // restored. The file is DPAPI-encrypted (CurrentUser) so it is not a new plaintext password file.
    //
    // It mirrors ALL user settings now, not only the logins - the class keeps its name because the
    // credentials are why it has to be encrypted. An operator upgrading to 8.8.5 found their grid
    // square, Holyland square and operator name blank, for exactly the reason described above; nothing
    // they have typed should ever be lost that way, not just the passwords.
    public static class CredentialStore
    {
        // Credential settings grouped by service. The FIRST name in each group is the "primary": a group
        // is restored from the mirror only when its primary is blank in the live settings, so a value the
        // operator just entered is never overwritten by an older mirror. (eQSL sub-accounts live in the
        // database table eqsl_accounts, which is separate from user.config and already survives upgrades.)
        private static readonly string[][] Groups =
        {
            new[] { "qrz_api_key", "qrz_logbook_key_valid", "qrz_logbook_auto_push" },
            new[] { "qrz_username", "qrz_password" },
            new[] { "EqslUsername", "EqslPassword", "EqslQthNickname", "EqslAutoUpload" },
            new[] { "ClublogEmail", "ClublogPassword", "ClublogAutoUpload" },
            new[] { "LotwWebUser", "LotwWebPassword" },
            new[] { "LotwTqslPath", "LotwTqslPassword", "LotwStationLocation", "LotwCallsignLocations" },
            // WHO THE OPERATOR IS. Typed once and expected to stay typed - and it did not: an operator
            // who upgraded found their grid square, their Holyland square and their operator name all
            // blank again, because the install identity had changed and Upgrade() had nothing to carry.
            // Callsign is the primary: if this store already knows who the station is, the rest of the
            // identity in it is the current one and the mirror must not talk over it.
            new[] { "my_callsign", "my_locator", "my_square", "Operator", "selectedOperator" },
        };

        // Settings the general mirror must NOT carry between installs. Everything else the operator ever
        // typed or chose is fair game - the point of this file is that nothing has to be typed twice.
        private static readonly HashSet<string> NeverMirrored = new HashSet<string>
        {
            "UpdateSettings",   // the run-once flag that drives Settings.Upgrade; per-version by design
            "ActiveLogId",      // a row id in this machine's log database, not a preference
        };

        private static string FilePath
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HolyLogger");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "credentials.dat");
            }
        }

        // Writes the current value of every credential setting to the mirror. Cheap and safe to call
        // often - after the settings upgrade at startup, and whenever the Options window closes.
        public static void Backup()
        {
            try
            {
                var s = Properties.Settings.Default;
                var map = new Dictionary<string, string>();

                // EVERY setting, not just the credentials. Anything the operator typed or chose is
                // something they should never have to enter a second time because a new version landed
                // in a folder with a different name.
                foreach (System.Configuration.SettingsProperty prop in s.Properties)
                {
                    if (prop == null || NeverMirrored.Contains(prop.Name)) continue;
                    object v = ReadSetting(s, prop.Name);
                    if (v != null) map[prop.Name] = Convert.ToString(v, CultureInfo.InvariantCulture);
                }

                string json = JsonConvert.SerializeObject(map);
                byte[] enc = ProtectedData.Protect(Encoding.UTF8.GetBytes(json), null, DataProtectionScope.CurrentUser);
                File.WriteAllText(FilePath, Convert.ToBase64String(enc));
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // Fills in credentials the live settings are missing, from the mirror. Called ONCE at startup,
        // right AFTER Settings.Upgrade() - so it only supplies what the per-version upgrade could not
        // carry (e.g. after the install identity changed). A group is skipped entirely when its primary
        // credential is already present, so nothing the operator set this session is disturbed.
        public static void RestoreMissing()
        {
            try
            {
                var map = Load();
                if (map == null || map.Count == 0) return;

                var s = Properties.Settings.Default;
                bool changed = false;

                foreach (var group in Groups)
                {
                    string primary = group[0];
                    if (!IsBlank(ReadSetting(s, primary))) continue;         // operator already has it

                    string pv;
                    if (!map.TryGetValue(primary, out pv) || IsBlank(pv)) continue;   // mirror has nothing

                    foreach (var name in group)
                    {
                        string stored;
                        if (map.TryGetValue(name, out stored) && WriteSetting(s, name, stored)) changed = true;
                    }
                }

                // ...then everything else this store has never had a value for. A setting still sitting
                // at its compiled-in default was never set HERE, so filling it from the mirror takes
                // nothing away; a setting the operator has touched differs from that default and is left
                // exactly as it is. The credential pass above runs first, so anything it restored is no
                // longer at its default and this pass steps over it.
                foreach (System.Configuration.SettingsProperty prop in s.Properties)
                {
                    if (prop == null || NeverMirrored.Contains(prop.Name)) continue;

                    string stored;
                    if (!map.TryGetValue(prop.Name, out stored)) continue;

                    string live = Convert.ToString(ReadSetting(s, prop.Name), CultureInfo.InvariantCulture) ?? "";
                    string dflt = Convert.ToString(prop.DefaultValue, CultureInfo.InvariantCulture) ?? "";
                    if (!string.Equals(live, dflt, StringComparison.Ordinal)) continue;   // operator set it
                    if (string.Equals(stored, live, StringComparison.Ordinal)) continue;  // nothing to add

                    if (WriteSetting(s, prop.Name, stored)) changed = true;
                }

                if (changed) s.Save();
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        private static Dictionary<string, string> Load()
        {
            try
            {
                string path = FilePath;
                if (!File.Exists(path)) return null;
                byte[] enc = Convert.FromBase64String(File.ReadAllText(path).Trim());
                byte[] plain = ProtectedData.Unprotect(enc, null, DataProtectionScope.CurrentUser);
                return JsonConvert.DeserializeObject<Dictionary<string, string>>(Encoding.UTF8.GetString(plain));
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); return null; }   // e.g. copied from another user
        }

        private static object ReadSetting(Properties.Settings s, string name)
        {
            try { return s.Properties[name] == null ? null : s[name]; }
            catch { return null; }
        }

        private static bool WriteSetting(Properties.Settings s, string name, string value)
        {
            try
            {
                var prop = s.Properties[name];
                if (prop == null) return false;
                s[name] = prop.PropertyType == typeof(string)
                    ? (object)value
                    : TypeDescriptorConvert(prop.PropertyType, value);
                return true;
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); return false; }
        }

        private static object TypeDescriptorConvert(Type t, string value)
        {
            return System.ComponentModel.TypeDescriptor.GetConverter(t).ConvertFromInvariantString(value);
        }

        private static bool IsBlank(object v)
        {
            return v == null || string.IsNullOrWhiteSpace(Convert.ToString(v, CultureInfo.InvariantCulture));
        }
    }
}
