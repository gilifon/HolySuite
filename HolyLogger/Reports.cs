using System;
using System.IO;

namespace HolyLogger
{
    // EVERY FILE THE PROGRAM WRITES FOR THE OPERATOR TO READ, and the one announcement that it exists.
    //
    // These used to land on the desktop and, for most of them, in silence: the LoTW upload log, the
    // list of confirmations that matched nothing, the frequency-repair report. A file nobody is told
    // about is a file nobody reads, and the import report was the only one that ever said its own name.
    //
    // Written is raised on whatever thread did the writing - an upload runs on a worker - so the
    // listener marshals to the UI itself rather than this class pretending to know how.
    internal static class Reports
    {
        public static string Folder { get { return DataAccess.ReportsFolder; } }

        // path = the file just written. Never null when raised.
        public static event Action<string> Written;

        // Called by whoever wrote the file, once it is closed and complete - announcing a half-written
        // report would invite the operator to open an empty one.
        public static void Note(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            var handler = Written;
            if (handler == null) return;
            try { handler(path); }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // Writes a report and announces it in one move, so a caller cannot do the first and forget the
        // second. Returns the path, or null if it could not be written.
        public static string Write(string fileName, string contents)
        {
            try
            {
                string path = Path.Combine(Folder, fileName);
                File.WriteAllText(path, contents ?? string.Empty);
                Note(path);
                return path;
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); return null; }
        }

        // Opens a report in whatever the operator uses for that kind of file; falls back to showing it
        // in its folder when Windows has nothing associated with the extension (.adi, usually).
        public static void Open(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return;
                if (File.Exists(path)) System.Diagnostics.Process.Start("explorer.exe", "\"" + path + "\"");
                else OpenFolder();
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        public static void OpenFolder()
        {
            try
            {
                string dir = Folder;
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    System.Diagnostics.Process.Start("explorer.exe", "\"" + dir + "\"");
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }
    }
}
