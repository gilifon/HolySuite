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

        // Has this window ever been placed by the operator? Asked BEFORE opening a window that wants
        // its own first-time position (Try Again opens centred on the log table). Once there is a
        // saved placement, that placement wins and the first-time rule never applies again.
        public static bool HasSaved(string key)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(key)) return false;
                return Load().TryGetValue(key, out Box b) && b != null && IsRealNumber(b.L) && IsRealNumber(b.T);
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); return false; }
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

                // Size first, so the visibility net below measures the real window.
                //
                // A saved size SMALLER than the window's own minimum is raised to that minimum, not
                // thrown away. Discarding it was the old rule, and it turned a 20px correction into a
                // window that jumped to a completely different size: My Favorite Channels had 300 saved,
                // its MinWidth went to 380 so the OK button would stop being cut off, and the whole 300
                // was ignored - so the window opened at its XAML default of 540 and looked as though it
                // had forgotten everything. Clamping keeps the operator's own size wherever it is still
                // usable, which is what a remembered size is for.
                // A window whose WIDTH is content-driven is left alone, for the same reason the height
                // is below: its width is worked out from what it is showing, and a width saved from a
                // session when it was showing something else is not an improvement on that. Without
                // this, a window that had been made content-sized went on opening at whatever width it
                // happened to have before the change - so no adjustment to its layout ever reached the
                // operator, and it looked as though nothing had been done.
                bool widthIsAuto = window.SizeToContent == SizeToContent.Width
                                || window.SizeToContent == SizeToContent.WidthAndHeight;
                if (!widthIsAuto && b.W > 0) window.Width = Math.Max(b.W, window.MinWidth);

                // A window whose height is content-driven (SizeToContent="Height" / "WidthAndHeight") must
                // never have Height set explicitly - WPF throws if you try, and the whole point of that
                // setting is to always fit the CURRENT content, not a size saved from a session when the
                // content was a different height (this is what previously left a stale, oversized empty
                // area under a window's content after that content was trimmed down).
                bool heightIsAuto = window.SizeToContent == SizeToContent.Height
                                 || window.SizeToContent == SizeToContent.WidthAndHeight;
                if (!heightIsAuto && b.H > 0) window.Height = Math.Max(b.H, window.MinHeight);

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

                window.Loaded += (s, e) =>
                {
                    EnsureVisibleOnSomeScreen(window);

                    // A window that disables the rest of the program while it is up must never be the
                    // one the operator cannot find. The Log Fixer opened behind the Log Workshop on a
                    // second screen, and every other window went dead with nothing on screen to say
                    // why - the program looked hung and there was no way back without the Task Manager.
                    try { window.Activate(); } catch (Exception swallowed) { Log.Swallow(swallowed); }
                };
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        private static bool IsRealNumber(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

        // Safety net, after layout: the WHOLE window is brought inside the work area of the screen it
        // is mostly on - size first, then position.
        //
        // It used to ask only whether a grab handle's worth of title bar touched some monitor, and
        // that is not the same question. A Log Fixer restored to 2190,-27 passed it: the second screen
        // begins a little above zero, so a sliver of title bar was technically on it. In practice the
        // window sat behind the Log Workshop with its title bar level with the top edge of the screen,
        // it was MODAL so every other window went dead, and the program could not be used or escaped
        // from at all. "Technically reachable" is not the standard; "fully on a screen" is.
        //
        // Measured against the real monitor rectangles, converted into the same device-independent
        // units the window's Left/Top use, so a scaled multi-monitor desktop is judged correctly.
        // SystemParameters.WorkArea is never consulted: it answers for the primary screen only, and
        // trusting it is what stranded a window on this very desktop once before.
        private static void EnsureVisibleOnSomeScreen(Window window)
        {
            try
            {
                if (window.WindowState != WindowState.Normal) return;
                if (!IsRealNumber(window.Left) || !IsRealNumber(window.Top)) return;

                var src = PresentationSource.FromVisual(window);
                double sx = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
                double sy = src?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
                if (sx <= 0) sx = 1.0;
                if (sy <= 0) sy = 1.0;

                double w = IsRealNumber(window.Width) && window.Width > 0 ? window.Width : window.ActualWidth;
                double h = IsRealNumber(window.Height) && window.Height > 0 ? window.Height : window.ActualHeight;
                if (w <= 0 || h <= 0) return;
                var current = new Rect(window.Left, window.Top, w, h);

                // The screen this window is MOSTLY on, so a rescue never throws it onto a different
                // monitor from the one the operator put it on. Nothing overlapping at all -> primary.
                Rect best = Rect.Empty;
                double bestArea = -1;
                foreach (var screen in System.Windows.Forms.Screen.AllScreens)
                {
                    var wa = screen.WorkingArea;
                    var dips = new Rect(wa.Left / sx, wa.Top / sy, wa.Width / sx, wa.Height / sy);
                    Rect over = Rect.Intersect(dips, current);
                    double area = over.IsEmpty ? 0 : over.Width * over.Height;
                    if (area > bestArea) { bestArea = area; best = dips; }
                    if (screen.Primary && bestArea <= 0) best = dips;
                }
                if (best.IsEmpty) return;

                // A window taller or wider than its screen pushes its own title bar off the top: that
                // is how a 1010-high window landed on an 840-high screen with nothing left to grab.
                double newW = Math.Min(w, best.Width);
                double newH = Math.Min(h, best.Height);

                double newL = Math.Min(Math.Max(window.Left, best.Left), best.Right - newW);
                double newT = Math.Min(Math.Max(window.Top, best.Top), best.Bottom - newH);

                bool sizeChanged = Math.Abs(newW - w) > 0.5 || Math.Abs(newH - h) > 0.5;
                bool moved = Math.Abs(newL - window.Left) > 0.5 || Math.Abs(newT - window.Top) > 0.5;
                if (!sizeChanged && !moved) return;   // already wholly on a screen: leave it alone

                Log.Warn($"Window '{window.Title}' at {window.Left},{window.Top} {w}x{h} is not wholly on a "
                         + $"screen - moving to {newL},{newT} {newW}x{newH}.");

                // A content-sized window must never have its Height set - WPF throws, and its height is
                // the content's business anyway.
                bool heightIsAuto = window.SizeToContent == SizeToContent.Height
                                 || window.SizeToContent == SizeToContent.WidthAndHeight;
                bool widthIsAuto = window.SizeToContent == SizeToContent.Width
                                || window.SizeToContent == SizeToContent.WidthAndHeight;

                if (!widthIsAuto && sizeChanged) window.Width = Math.Max(newW, window.MinWidth);
                if (!heightIsAuto && sizeChanged) window.Height = Math.Max(newH, window.MinHeight);
                window.Left = newL;
                window.Top = newT;
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
