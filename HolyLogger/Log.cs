using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace HolyLogger
{
    // Minimal always-safe diagnostic log. Exists so the codebase's many intentional
    // "keep running no matter what" catch blocks stop being silent: they still swallow,
    // but every swallowed exception now leaves a trace with its exact source location
    // (via caller-info attributes -- no unique message strings needed at call sites).
    //
    // Writes to %LOCALAPPDATA%\<Company>\<Product>\holylogger.log (same folder as logDB.db)
    // and to the debugger output. Never throws; if the disk write fails the app must not care.
    public static class Log
    {
        private static readonly object _sync = new object();
        private static string _path;   // resolved lazily; null until first use, "" if resolution failed

        private const long MaxBytes = 2 * 1024 * 1024;   // start fresh past 2 MB so it can't grow unbounded

        // For exceptions that are deliberately swallowed (catch-and-continue sites).
        public static void Swallow(Exception ex,
            [CallerMemberName] string member = "", [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
        {
            if (ex == null) return;
            Write("SWALLOWED", ex.GetType().Name + ": " + ex.Message, member, file, line);
        }

        // For noteworthy events / handled-but-unexpected failures where a message reads better.
        public static void Warn(string message,
            [CallerMemberName] string member = "", [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
        {
            Write("WARN", message, member, file, line);
        }

        // For unhandled-exception hooks (App.xaml.cs): full detail including stack trace.
        public static void Fatal(string source, Exception ex)
        {
            Write("UNHANDLED/" + source, ex == null ? "(no exception object)" : ex.ToString(), "", "", 0);
        }

        private static void Write(string level, string message, string member, string file, int line)
        {
            try
            {
                string site = string.IsNullOrEmpty(file)
                    ? ""
                    : "  [" + Path.GetFileName(file) + ":" + line + " " + member + "]";
                string entry = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + level + "  " + message + site;

                System.Diagnostics.Debug.WriteLine("HolyLogger " + entry);

                lock (_sync)
                {
                    if (_path == null) _path = ResolvePath();
                    if (_path.Length == 0) return;

                    var fi = new FileInfo(_path);
                    if (fi.Exists && fi.Length > MaxBytes) fi.Delete();
                    File.AppendAllText(_path, entry + Environment.NewLine);
                }
            }
            catch
            {
                // Logging must never take the app down or cascade; nothing to do.
            }
        }

        private static string ResolvePath()
        {
            try
            {
                // Same company/product-derived folder DataAccess uses for logDB.db.
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                var fvi = System.Diagnostics.FileVersionInfo.GetVersionInfo(asm.Location);
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    fvi.CompanyName, fvi.ProductName);
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "holylogger.log");
            }
            catch
            {
                return string.Empty;   // remembered: disk logging disabled, Debug output still works
            }
        }
    }
}
