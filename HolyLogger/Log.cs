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

        // Where the log is being written. A message that tells the operator "the details are in the
        // log" and leaves them to find it has told them nothing, so the path is available to be
        // printed - and clicked. Null when logging to disk could not be set up.
        public static string FilePath
        {
            get
            {
                lock (_sync)
                {
                    if (_path == null) _path = ResolvePath();
                    return _path.Length == 0 ? null : _path;
                }
            }
        }

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

        // HOW FAR INTO THE START WE ARE, in seconds from the moment Windows launched the program -
        // not from some point inside it. "It takes 23 seconds to come up" cannot be answered by
        // reading code; it is answered by a handful of these, one per step, and then reading the log.
        public static string SinceLaunch()
        {
            try
            {
                var started = System.Diagnostics.Process.GetCurrentProcess().StartTime;
                return "+" + (DateTime.Now - started).TotalSeconds.ToString("0.0") + "s";
            }
            catch (Exception) { return "+?s"; }
        }


        // ONE STEP OF THE START, and how long the step before it took.
        //
        // "It takes 23 seconds to come up" is not answerable by reading code - the start is a hundred
        // calls and any of them could be the one. A line per step, each carrying the milliseconds since
        // the last, turns that into a list to read: the big number names the culprit.
        private static DateTime _lastStep = DateTime.MinValue;

        public static void Step(string what)
        {
            try
            {
                DateTime now = DateTime.Now;
                string delta = _lastStep == DateTime.MinValue
                    ? ""
                    : "  (" + (now - _lastStep).TotalMilliseconds.ToString("0") + " ms)";
                _lastStep = now;
                Warn("STARTUP " + SinceLaunch() + "  " + what + delta);
            }
            catch (Exception) { }
        }

    }

    // ── FREEZE WATCH ──────────────────────────────────────────────────────────────────────────────
    //
    // 2026-11-24: users said HolyLogger "freezes from time to time, the spinner turns and you have to
    // wait", and the log could not say when, for how long, or what was running - nothing wrote it down.
    // This watch writes it down, all the time, not only during the start:
    //
    //  - FREEZE: a background thread asks the window's thread a tiny question four times a second, at
    //    Input priority - the same place in the queue a mouse click waits. No answer for 2 s = the
    //    window is frozen for the operator: a line when it starts, every 10 s while it lasts, and when
    //    it ends with how long it was. At 3 s (and again at 15 s) the window's thread is stopped for an
    //    instant and its call stack is read - that names the code that holds it.
    //  - PAUSE: the watch's own thread did not run either. A full memory cleanup (gen 2 collection)
    //    stops EVERY thread, so this is how a cleanup pause shows; the cleanup count says if it was one.
    //  - BUSY: HolyLogger's own busy signs - the busy screen, the round progress spinner, the wait
    //    cursor - each get a line when they go on and off, so "the spinner turned" can be told apart
    //    from "Windows showed its busy cursor over a window that did not answer".
    //
    // Every line also says the memory, what the window's thread was running (the dispatcher work
    // item's name) and which background jobs were busy (Job below - the Google Drive sync uses it).
    // The watch costs one tiny work item every 250 ms; it writes only when something happens.
    public static class FreezeWatch
    {
        private const int TickMs = 250;
        private const long FreezeMs = 2000;
        private const long FirstStackMs = 3000;
        private const long SecondStackMs = 15000;
        private const long StillFrozenEveryMs = 10000;
        private const long PauseMs = 2000;
        private const int MaxStacksPerRun = 10;

        private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
        private static System.Windows.Threading.Dispatcher _ui;
        private static System.Threading.Thread _watch;

        // Longs are not read or written in one step in a 32-bit program, so these go through Interlocked.
        private static long _pingSentMs = -1;    // when the waiting question was posted; -1 = none waiting
        private static long _answeredMs = -1;    // when the window's thread last answered

        // What the window's thread is running right now (set by the dispatcher hooks on that thread).
        private static volatile System.Windows.Threading.DispatcherOperation _runningOp;
        private static readonly System.Collections.Generic.Stack<System.Windows.Threading.DispatcherOperation> _opStack =
            new System.Collections.Generic.Stack<System.Windows.Threading.DispatcherOperation>();

        // HolyLogger's own busy signs. The texts are read by the watch thread (a reference is read in one
        // step; a moment-old text is fine); the times only on the window's. Not volatile: passed by ref.
        private static string _busyScreen, _spinner;
        private static volatile bool _waitCursorOn;
        private static long _busyScreenSinceMs = -1, _spinnerSinceMs = -1, _waitCursorSinceMs = -1;

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, string> _jobs =
            new System.Collections.Concurrent.ConcurrentDictionary<long, string>();
        private static long _jobSeq;
        private static int _stacksTaken;

        private static readonly System.Reflection.FieldInfo OpMethod =
            typeof(System.Windows.Threading.DispatcherOperation).GetField("_method",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        // Called once, on the window's thread, when the main window is up.
        public static void Start(System.Windows.Threading.Dispatcher ui)
        {
            try
            {
                if (_watch != null || ui == null) return;
                _ui = ui;

                var hooks = ui.Hooks;
                hooks.OperationStarted += (s, e) => { _opStack.Push(e.Operation); _runningOp = e.Operation; };
                System.Windows.Threading.DispatcherHookEventHandler ended = (s, e) =>
                {
                    // nested work (a dialog shown from inside other work) ends first, so a stack
                    while (_opStack.Count > 0 && _opStack.Pop() != e.Operation) { }
                    _runningOp = _opStack.Count > 0 ? _opStack.Peek() : null;
                };
                hooks.OperationCompleted += ended;
                hooks.OperationAborted += ended;

                _watch = new System.Threading.Thread(Watch)
                {
                    IsBackground = true,
                    Name = "HolyLogger freeze watch",
                    Priority = System.Threading.ThreadPriority.AboveNormal,
                };
                _watch.Start();
                Log.Warn("FREEZE WATCH started (the window is asked every " + TickMs + " ms; no answer for "
                         + (FreezeMs / 1000) + " s is written here as a FREEZE)");
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // A background job, named for the freeze lines while it runs:  using (FreezeWatch.Job("...")) { ... }
        public static IDisposable Job(string what)
        {
            long id = System.Threading.Interlocked.Increment(ref _jobSeq);
            _jobs[id] = what + " (since " + DateTime.Now.ToString("HH:mm:ss") + ")";
            return new JobEnd(id);
        }

        private sealed class JobEnd : IDisposable
        {
            private readonly long _id;
            public JobEnd(long id) { _id = id; }
            public void Dispose() { string gone; _jobs.TryRemove(_id, out gone); }
        }

        // The main window's busy screen (the "Loading log..." cover). Window's thread only.
        public static void BusyScreen(bool on, string what)
        {
            try { Sign(on, what, "the busy screen", ref _busyScreen, ref _busyScreenSinceMs); }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // The main window's round progress spinner (imports, uploads, Remove Duplicates...). Window's thread only.
        public static void Spinner(bool on, string what)
        {
            try { Sign(on, what, "the progress spinner", ref _spinner, ref _spinnerSinceMs); }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        private static void Sign(bool on, string what, string name, ref string text, ref long sinceMs)
        {
            long now = Clock.ElapsedMilliseconds;
            if (on && sinceMs < 0)
            {
                sinceMs = now;
                text = string.IsNullOrWhiteSpace(what) ? "(no text)" : what.Trim();
                Log.Warn("BUSY  " + name + " ON: \"" + text + "\"" + Context(-1));
            }
            else if (!on && sinceMs >= 0)
            {
                Log.Warn("BUSY  " + name + " OFF after " + Sec(now - sinceMs) + " s: \"" + text + "\"");
                sinceMs = -1;
                text = null;
            }
        }

        // The answer to the watch's question - runs on the window's thread. Also looks at the wait
        // cursor, which is set in some seventy places, so it is watched here rather than at each one.
        private static void Answer()
        {
            long now = Clock.ElapsedMilliseconds;
            System.Threading.Interlocked.Exchange(ref _answeredMs, now);
            System.Threading.Interlocked.Exchange(ref _pingSentMs, -1);
            try
            {
                var c = System.Windows.Input.Mouse.OverrideCursor;
                bool wait = c != null && (c == System.Windows.Input.Cursors.Wait || c == System.Windows.Input.Cursors.AppStarting);
                if (wait && _waitCursorSinceMs < 0)
                {
                    _waitCursorSinceMs = now;
                    _waitCursorOn = true;
                }
                else if (!wait && _waitCursorSinceMs >= 0)
                {
                    long ms = now - _waitCursorSinceMs;
                    _waitCursorSinceMs = -1;
                    _waitCursorOn = false;
                    if (ms >= 1000) Log.Warn("BUSY  the wait cursor was on for " + Sec(ms) + " s");
                }
            }
            catch (Exception) { }
        }

        private static void Watch()
        {
            long lastLoop = Clock.ElapsedMilliseconds;
            int lastGen2 = GC.CollectionCount(2);
            bool frozen = false;
            long frozenFrom = 0, nextStack = 0, nextStill = 0;
            int gen2AtFreeze = 0;

            while (true)
            {
                try
                {
                    System.Threading.Thread.Sleep(TickMs);
                    var ui = _ui;
                    if (ui == null || ui.HasShutdownStarted) return;

                    long now = Clock.ElapsedMilliseconds;
                    int gen2 = GC.CollectionCount(2);

                    // THE WATCH ITSELF STOOD STILL: every thread did, not only the window's.
                    long gap = now - lastLoop - TickMs;
                    if (gap >= PauseMs)
                    {
                        int cleanups = gen2 - lastGen2;
                        Log.Warn("PAUSE  the whole program stood still for " + Sec(gap) + " s  | full memory cleanups"
                                 + " during it: " + cleanups
                                 + (cleanups == 0 && gap > 120000 ? " (none - the PC was probably asleep)" : "")
                                 + Context(-1));
                        // a question that waited through it must not count that time as a window freeze
                        if (!frozen)
                        {
                            long waiting = System.Threading.Interlocked.Read(ref _pingSentMs);
                            if (waiting >= 0) System.Threading.Interlocked.CompareExchange(ref _pingSentMs, now, waiting);
                        }
                    }
                    lastLoop = now;
                    lastGen2 = gen2;

                    long sent = System.Threading.Interlocked.Read(ref _pingSentMs);
                    if (sent < 0)
                    {
                        // answered
                        if (frozen)
                        {
                            frozen = false;
                            long answered = System.Threading.Interlocked.Read(ref _answeredMs);
                            Log.Warn("FREEZE ended - the window answered again after " + Sec(answered - frozenFrom) + " s"
                                     + Context(gen2AtFreeze));
                        }
                        System.Threading.Interlocked.Exchange(ref _pingSentMs, now);
                        ui.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(Answer));
                        continue;
                    }

                    long waited = now - sent;
                    if (!frozen && waited >= FreezeMs)
                    {
                        frozen = true;
                        frozenFrom = sent;
                        gen2AtFreeze = gen2;
                        nextStack = sent + FirstStackMs;
                        nextStill = now + StillFrozenEveryMs;
                        Log.Warn("FREEZE started - the window has not answered for " + Sec(waited) + " s" + Context(-1));
                    }
                    if (!frozen) continue;

                    if (nextStack > 0 && now >= nextStack && _stacksTaken < MaxStacksPerRun)
                    {
                        bool first = nextStack == frozenFrom + FirstStackMs;
                        nextStack = first ? frozenFrom + SecondStackMs : 0;
                        _stacksTaken++;
                        Log.Warn("FREEZE  " + Sec(now - frozenFrom) + " s in, the window's thread is in: " + WindowThreadStack(ui.Thread));
                    }
                    if (now >= nextStill)
                    {
                        nextStill = now + StillFrozenEveryMs;
                        Log.Warn("FREEZE still on - " + Sec(now - frozenFrom) + " s so far" + Context(gen2AtFreeze));
                    }
                }
                catch (Exception swallowed)
                {
                    Log.Swallow(swallowed);
                    try { System.Threading.Thread.Sleep(5000); } catch (Exception) { }
                }
            }
        }

        // WHAT HOLDS THE WINDOW. The window's thread is stopped for an instant and its stack read on a
        // helper thread. It is started again within 2 s whatever happens - a helper that got stuck reading
        // the stack can cost those 2 s of an already frozen window, never more. (Thread.Suspend is marked
        // old because it is dangerous as a way of controlling a program; for a stack read it is what the
        // runtime offers, and the resume is guaranteed below.)
        private static string WindowThreadStack(System.Threading.Thread target)
        {
            if (target == null) return "(no thread)";
            string result = "(the stack could not be read)";
            bool suspended = false;
#pragma warning disable 618
            try
            {
                target.Suspend();
                suspended = true;
                string read = null;
                var reader = new System.Threading.Thread(() =>
                {
                    try { read = Frames(new System.Diagnostics.StackTrace(target, false), 30); }
                    catch (Exception ex) { read = "(the stack could not be read: " + ex.GetType().Name + ")"; }
                }) { IsBackground = true, Name = "HolyLogger freeze watch stack" };
                reader.Start();
                if (reader.Join(2000) && read != null) result = read;
                else result = "(reading the stack took over 2 s - given up)";
            }
            catch (Exception ex) { result = "(the stack could not be read: " + ex.GetType().Name + ": " + ex.Message + ")"; }
            finally
            {
                if (suspended) { try { target.Resume(); } catch (Exception) { } }
            }
#pragma warning restore 618
            return result;
        }

        // ownOnly: leave out .NET's and SQLite's own frames, so the list is HolyLogger's code.
        internal static string Frames(System.Diagnostics.StackTrace st, int max, bool ownOnly = false)
        {
            var parts = new System.Collections.Generic.List<string>();
            foreach (var f in st.GetFrames() ?? new System.Diagnostics.StackFrame[0])
            {
                var m = f.GetMethod();
                if (m == null) continue;
                if (ownOnly)
                {
                    string ns = m.DeclaringType != null ? m.DeclaringType.Namespace ?? "" : "";
                    if (ns == "System" || ns.StartsWith("System.") || ns.StartsWith("MS.")
                        || m.DeclaringType == typeof(BigChangeWatch)) continue;
                }
                parts.Add((m.DeclaringType != null ? m.DeclaringType.Name + "." : "") + m.Name);
                if (parts.Count >= max) { parts.Add("..."); break; }
            }
            return parts.Count == 0 ? "(empty)" : string.Join(" < ", parts);
        }

        private static string Context(int gen2From)
        {
            var sb = new System.Text.StringBuilder();
            try
            {
                long privateBytes = 0;
                using (var p = System.Diagnostics.Process.GetCurrentProcess()) privateBytes = p.PrivateMemorySize64;
                sb.Append("  | memory ").Append(GC.GetTotalMemory(false) / (1024 * 1024)).Append(" MB in objects, ")
                  .Append(privateBytes / (1024 * 1024)).Append(" MB in all");
                if (gen2From >= 0) sb.Append(" | full memory cleanups since it started: ").Append(GC.CollectionCount(2) - gen2From);
                sb.Append(" | window's work: ").Append(NameOf(_runningOp));
                string busy = _busyScreen, spin = _spinner;
                if (busy != null) sb.Append(" | busy screen up: \"").Append(busy).Append('"');
                if (spin != null) sb.Append(" | progress spinner on: \"").Append(spin).Append('"');
                if (_waitCursorOn) sb.Append(" | wait cursor on");
                var jobs = new System.Collections.Generic.List<string>(_jobs.Values);
                sb.Append(" | background jobs: ").Append(jobs.Count == 0 ? "none" : string.Join("; ", jobs));
                if (System.Diagnostics.Debugger.IsAttached) sb.Append(" | debugger attached");
            }
            catch (Exception) { }
            return sb.ToString();
        }

        private static string NameOf(System.Windows.Threading.DispatcherOperation op)
        {
            try
            {
                if (op == null) return "nothing (idle)";
                var d = OpMethod?.GetValue(op) as Delegate;
                if (d == null) return "unknown";
                var m = d.Method;
                string owner = d.Target != null ? d.Target.GetType().Name : m.DeclaringType?.Name;
                return owner + "." + m.Name + " [" + op.Priority + "]";
            }
            catch (Exception) { return "unknown"; }
        }

        private static string Sec(long ms)
        {
            return (ms / 1000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    // ── BIG CHANGE WATCH ──────────────────────────────────────────────────────────────────────────
    //
    // 2026-11-24: a user's sync diary grew by 36,000 and then 32,000 lines in one minute - every QSO
    // of the log written again - and nothing in the log said what did it. Every change to a QSO, log or
    // ARRL row goes through the one database connection, and SQLite tells it about each changed row;
    // when 1,000 of them come within seconds, the code that is making them (its call stack) is written
    // once. Counting costs nothing worth measuring; the line is written once per burst.
    public static class BigChangeWatch
    {
        private const int Threshold = 1000;
        private const long QuietMs = 5000;   // this long with no change ends a burst

        private static readonly object Gate = new object();
        private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
        private static long _lastMs = -QuietMs;
        private static int _count;

        public static void Attach(System.Data.SQLite.SQLiteConnection con)
        {
            try { if (con != null) con.Update += OnRowChanged; }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        private static void OnRowChanged(object sender, System.Data.SQLite.UpdateEventArgs e)
        {
            try
            {
                string table = e.Table;
                if (table != "qso" && table != "logs" && table != "arrl_granted") return;
                bool tell;
                lock (Gate)
                {
                    long now = Clock.ElapsedMilliseconds;
                    if (now - _lastMs > QuietMs) _count = 0;
                    _lastMs = now;
                    tell = ++_count == Threshold;
                }
                if (!tell) return;
                var t = System.Threading.Thread.CurrentThread;
                bool window = System.Windows.Application.Current != null
                              && System.Windows.Application.Current.Dispatcher.Thread == t;
                Log.Warn("BIG CHANGE  " + Threshold + " rows of '" + table + "' changed within seconds (" + e.Event + "), on "
                         + (window ? "the window's thread" : "a background thread")
                         + ", made by: " + FreezeWatch.Frames(new System.Diagnostics.StackTrace(1, false), 25, true));
            }
            catch (Exception) { }
        }
    }
}
