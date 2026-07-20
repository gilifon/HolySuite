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

        // Restores the saved placement now and saves it again when the window closes.
        public static void Attach(Window window, string key)
        {
            if (window == null || string.IsNullOrWhiteSpace(key)) return;
            Restore(window, key);
            window.Closing += (s, e) => Save(window, key);
        }

        private static void Restore(Window window, string key)
        {
            try
            {
                var all = Load();
                if (!all.TryGetValue(key, out Box b) || b == null) return;

                // Size first, so the on-screen test below uses the real window size. Anything below the
                // window's own minimum is ignored, which also self-heals a stale too-small saved value.
                if (b.W > 0 && b.W >= window.MinWidth) window.Width = b.W;
                if (b.H > 0 && b.H >= window.MinHeight) window.Height = b.H;

                // Only take the position if it still lands on a visible monitor: a spot saved on a screen
                // that has since been unplugged would otherwise open the window where it can't be reached.
                if (IsPositionOnScreen(b.L, b.T))
                {
                    window.WindowStartupLocation = WindowStartupLocation.Manual;
                    window.Left = b.L;
                    window.Top = b.T;
                }
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

        // True when a window at (left, top) would still be reachable on some monitor of the current
        // virtual desktop. Requires the title bar to be grabbable rather than the whole window to fit.
        private static bool IsPositionOnScreen(double left, double top)
        {
            if (double.IsNaN(left) || double.IsNaN(top) ||
                double.IsInfinity(left) || double.IsInfinity(top))
                return false;

            double vsLeft = SystemParameters.VirtualScreenLeft;
            double vsTop = SystemParameters.VirtualScreenTop;
            double vsRight = vsLeft + SystemParameters.VirtualScreenWidth;
            double vsBottom = vsTop + SystemParameters.VirtualScreenHeight;

            return left >= vsLeft - 10 && top >= vsTop - 10 &&
                   left <= vsRight - 100 && top <= vsBottom - 60;
        }
    }
}
