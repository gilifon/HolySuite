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
                foreach (var group in Groups)
                    foreach (var name in group)
                    {
                        object v = ReadSetting(s, name);
                        if (v != null) map[name] = Convert.ToString(v, CultureInfo.InvariantCulture);
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
