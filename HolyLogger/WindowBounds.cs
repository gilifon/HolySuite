using System;
using System.Collections.Generic;
using System.Windows;
using Newtonsoft.Json;

namespace HolyLogger
{
    // Remembers a window's position and size across sessions.
    //
    // Every placement lives in ONE setting (Settings.WindowBoundsJson) keyed by a short name, instead of
    // four typed settings per window. That means a new window opts in with a single Attach() call and no
    // Settings.settings edit, and a PROFILE (which snapshots Properties.Settings) captures every window's
    // placement automatically.
    //
    // Usage, at the end of a window's constructor:  WindowBounds.Attach(this, "ViewLogs");
    internal static class WindowBounds
    {
        private class Box
        {
            public double L { get; set; }
            public double T { get; set; }
            public double W { get; set; }
            public double H { get; set; }
        }

        // Every window currently using this helper, so the placement of windows still OPEN at
        // shutdown can be saved too (see SaveAllOpen).
        private static readonly Dictionary<Window, string> _attached = new Dictionary<Window, string>();

        // Restores the saved placement now and saves it again when the window closes.
        public static void Attach(Window window, string key)
        {
            if (window == null || string.IsNullOrWhiteSpace(key)) return;
            Restore(window, key);
            _attached[window] = key;
            window.Closing += (s, e) => Save(window, key);
            window.Closed += (s, e) => _attached.Remove(window);
        }

        // Saves the placement of every attached window that is still open.
        //
        // Closing a window yourself saves it, but when the PROGRAM closes the child windows are torn
        // down without that handler doing the work - so a window left open at exit lost the position it
        // had been moved to. The main window calls this during shutdown, before settings are flushed.
        public static void SaveAllOpen()
        {
            foreach (var pair in new List<KeyValuePair<Window, string>>(_attached))
            {
                try { Save(pair.Key, pair.Value); }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
            }
        }

        private static void Restore(Window window, string key)
        {
            try
            {
                var all = Load();
                if (!all.TryGetValue(key, out Box b) || b == null) return;

                // Size first, so the visibility net below measures the real window. Anything below the
                // window's own minimum is ignored, which self-heals a stale too-small saved value.
                if (b.W > 0 && b.W >= window.MinWidth) window.Width = b.W;
                if (b.H > 0 && b.H >= window.MinHeight) window.Height = b.H;

                // Apply the saved corner whenever it is a real number - including on a second monitor.
                // This used to be gated on a test against SystemParameters.VirtualScreen*, which WPF
                // reports in device-independent units; on a scaled multi-monitor desktop that rejected
                // perfectly good positions and silently re-centred the window, which is exactly what
                // "it does not remember where I put it" looked like.
                if (IsRealNumber(b.L) && IsRealNumber(b.T))
                {
                    window.WindowStartupLocation = WindowStartupLocation.Manual;
                    window.Left = b.L;
                    window.Top = b.T;
                }

                window.Loaded += (s, e) => EnsureVisibleOnSomeScreen(window);
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        private static bool IsRealNumber(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

        // Safety net, after layout: if the restored position leaves the title bar on NO monitor (the
        // screen it was on has been unplugged), bring the window back so it can still be reached.
        // Measured against the real monitor rectangles, converted into the same device-independent units
        // the window's Left/Top use, so a scaled multi-monitor desktop is judged correctly.
        private static void EnsureVisibleOnSomeScreen(Window window)
        {
            try
            {
                if (!IsRealNumber(window.Left) || !IsRealNumber(window.Top)) return;

                var src = PresentationSource.FromVisual(window);
                double sx = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
                double sy = src?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
                if (sx <= 0) sx = 1.0;
                if (sy <= 0) sy = 1.0;

                // A grab handle's worth of title bar has to land inside some monitor.
                var handle = new Rect(window.Left, window.Top,
                                      Math.Max(80, window.ActualWidth * 0.25), 24);

                foreach (var screen in System.Windows.Forms.Screen.AllScreens)
                {
                    var wa = screen.WorkingArea;
                    var inDips = new Rect(wa.Left / sx, wa.Top / sy, wa.Width / sx, wa.Height / sy);
                    if (inDips.IntersectsWith(handle)) return;   // reachable - leave it exactly where it is
                }

                Log.Warn($"Window '{window.Title}' at {window.Left},{window.Top} is on no monitor - recentring.");
                var work = SystemParameters.WorkArea;
                window.Left = work.Left + 60;
                window.Top = work.Top + 60;
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        private static void Save(Window window, string key)
        {
            try
            {
                // RestoreBounds when maximized/minimized, so we store the real (un-maximized) placement
                // rather than 0,0 and the full screen size.
                Rect r = window.WindowState == WindowState.Normal
                    ? new Rect(window.Left, window.Top, window.Width, window.Height)
                    : window.RestoreBounds;

                if (double.IsNaN(r.Left) || double.IsInfinity(r.Left) ||
                    double.IsNaN(r.Top) || double.IsInfinity(r.Top) ||
                    r.Width <= 0 || r.Height <= 0)
                    return;

                var all = Load();
                all[key] = new Box { L = r.Left, T = r.Top, W = r.Width, H = r.Height };
                Properties.Settings.Default.WindowBoundsJson = JsonConvert.SerializeObject(all);
                Properties.Settings.Default.Save();
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        private static Dictionary<string, Box> Load()
        {
            try
            {
                string json = Properties.Settings.Default.WindowBoundsJson;
                if (!string.IsNullOrWhiteSpace(json))
                {
                    var parsed = JsonConvert.DeserializeObject<Dictionary<string, Box>>(json);
                    if (parsed != null) return parsed;
                }
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
            return new Dictionary<string, Box>(StringComparer.OrdinalIgnoreCase);
        }


    }
}
