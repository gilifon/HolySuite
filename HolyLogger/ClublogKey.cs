using System;
using System.IO;
using System.Reflection;

namespace HolyLogger
{
    // Where Club Log's application API key comes from.
    //
    // Club Log asks that the key not be published in source code, and HolySuite's repository is public,
    // so the key is looked for outside the source in this order:
    //   1. clublog.key beside logDB.db     - a key the user supplied himself; his wins over ours
    //   2. an embedded resource            - the application key, compiled in from a clublog.key that
    //                                        sits next to the solution and is excluded by .gitignore,
    //                                        so a build has the key while the repository never does
    //   3. ClublogService.ApiKey           - the original hard-coded key. It is already public in the
    //                                        git history, so it is kept as a fallback purely to avoid
    //                                        breaking uploads for existing installations; delete it
    //                                        (and this step) once Club Log issues a replacement.
    public static class ClublogKey
    {
        public const string FileName = "clublog.key";

        // The user's own key file, in the same folder as the log database.
        public static string UserKeyPath
        {
            get
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                var fvi = System.Diagnostics.FileVersionInfo.GetVersionInfo(asm.Location);
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return Path.Combine(appData, fvi.CompanyName, fvi.ProductName, FileName);
            }
        }

        // The key in force, or "" when Club Log features must stay switched off.
        public static string Current()
        {
            try
            {
                string path = UserKeyPath;
                if (File.Exists(path))
                {
                    string own = File.ReadAllText(path).Trim();
                    if (own.Length > 0) return own;
                }
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }

            string embedded = Embedded();
            if (embedded.Length > 0) return embedded;

            return (ClublogService.ApiKey ?? string.Empty).Trim();
        }

        public static bool Available => Current().Length > 0;

        // True when the key came from a file rather than from the source, i.e. when the exposed
        // hard-coded key is no longer what the program actually uses.
        public static bool FromFile()
        {
            try
            {
                if (File.Exists(UserKeyPath) && File.ReadAllText(UserKeyPath).Trim().Length > 0) return true;
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
            return Embedded().Length > 0;
        }

        private static string Embedded()
        {
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                foreach (string name in asm.GetManifestResourceNames())
                {
                    if (!name.EndsWith(FileName, StringComparison.OrdinalIgnoreCase)) continue;
                    using (Stream s = asm.GetManifestResourceStream(name))
                    {
                        if (s == null) continue;
                        using (var reader = new StreamReader(s))
                            return reader.ReadToEnd().Trim();
                    }
                }
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
            return string.Empty;
        }
    }
}
