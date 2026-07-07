using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace HolyLogger
{
    // Central theme switch. Applying a scheme turns the corresponding ThemePalette column into frozen
    // brushes set DIRECTLY on Application.Resources, so every DynamicResource in the live UI (and
    // every ThemeManager.Brush() call in code) re-resolves to the new values. Anything painted in
    // code subscribes to ThemeChanged and re-paints. The choice is persisted in Settings.
    public static class ThemeManager
    {
        // The scheme currently in effect. Schemes are edited in place, so this is exactly what the
        // user selected -- there is no separate "Custom" scheme layered on top of a base anymore.
        public static ColorScheme CurrentScheme { get; private set; } = ThemePalette.FindScheme("light");

        // The selected scheme's id ("light"/"dark"/"myscheme"). Kept as a distinct property because
        // callers (menu checkmarks, the editor) key off it.
        public static string CurrentSchemeId { get; private set; } = "light";

        // The current scheme's user edits (token -> hex), layered on top of the factory palette
        // column. Empty for an untouched scheme.
        private static System.Collections.Generic.Dictionary<string, string> _overrides
            = new System.Collections.Generic.Dictionary<string, string>();

        // True when the ACTIVE SCHEME declares dark chrome -- drives the DWM title-bar color and
        // any icon/asset choices. A property of the scheme, not of which scheme is selected, so
        // any number of dark-ish schemes work without touching this.
        public static bool IsDark => CurrentScheme.IsDarkChrome;

        // The hex currently in effect for a token: the user's edit for the active scheme if it has
        // one, otherwise the scheme's factory palette value. This is what the Customize Colors
        // dialog shows in its swatches.
        public static string CurrentHex(string token)
        {
            if (_overrides != null && _overrides.TryGetValue(token, out string edited)) return edited;
            return ThemePalette.Tokens.TryGetValue(token, out string[] row) ? row[CurrentScheme.Column] : "#FF6600";
        }

        // Raised after the palette changes, so code-painted areas can re-run their coloring.
        //
        // LEAK WARNING: this is a STATIC event, so it pins every subscriber for the lifetime of the
        // process. MainWindow may subscribe without unsubscribing (it lives until exit), but any
        // transient window or dialog that subscribes here MUST unsubscribe in its Closed handler,
        // or the whole window (and everything it references) stays in memory forever.
        public static event Action ThemeChanged;

        public static SolidColorBrush Brush(string key)
        {
            var app = Application.Current;
            if (app != null && app.TryFindResource(key) is SolidColorBrush b) return b;
            return Brushes.Transparent as SolidColorBrush;
        }

        public static Color Color(string key)
        {
            var b = Brush(key);
            return b != null ? b.Color : Colors.Transparent;
        }

        public static void Apply(string schemeId)
        {
            CurrentScheme = ThemePalette.FindScheme(schemeId);
            CurrentSchemeId = CurrentScheme.Id;
            // Layer this scheme's saved user edits over its factory palette column.
            _overrides = SchemeOverrides.For(CurrentSchemeId);

            var res = Application.Current.Resources;
            foreach (var kv in ThemePalette.Tokens)
            {
                string hex = CurrentHex(kv.Key);
                var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
                brush.Freeze();
                res[kv.Key] = brush;   // set/replace directly => DynamicResource re-resolves live
            }

            // The default WPF ComboBox template paints its dropdown popup with the system window
            // brushes via DynamicResource. Point those keys at the menu tokens so every combo
            // popup surface follows the theme — otherwise the popup stays white behind
            // theme-colored item text (white-on-white list in dark mode).
            res[SystemColors.WindowBrushKey]      = res["MenuBg"];
            res[SystemColors.WindowFrameBrushKey] = res["MenuBorder"];

            try
            {
                Properties.Settings.Default.ColorSchemeId = CurrentSchemeId;
                // Kept in sync for backward compatibility (older builds sharing this user.config).
                Properties.Settings.Default.DarkMode = CurrentScheme.IsDarkChrome;
                Properties.Settings.Default.Save();
            }
            catch (System.Exception swallowed) { Log.Swallow(swallowed); }

            try { ThemeChanged?.Invoke(); } catch (System.Exception swallowed) { Log.Swallow(swallowed); }

            // Re-paint the native title bar of every window already open (the DynamicResource
            // brushes above only reach the WPF-drawn client area). Newly opened windows are caught
            // by the app-wide SourceInitialized handler registered in App.xaml.cs.
            try { foreach (Window w in Application.Current.Windows.OfType<Window>().ToList()) ApplyWindowChrome(w); }
            catch (System.Exception swallowed) { Log.Swallow(swallowed); }
        }

        // Convenience overload kept for the old Light/Dark toggle call sites.
        public static void Apply(bool dark) => Apply(dark ? "dark" : "light");

        // Sets one token in the active scheme and applies it immediately -- the programmatic twin of
        // the Customize Colors dialog, used by in-place shortcuts like right-clicking the contest
        // exchange frames. The edit is saved to whichever scheme is selected and remembered for it.
        public static void SetOverride(string token, string hex)
        {
            _overrides[token] = hex;
            SchemeOverrides.Save(CurrentSchemeId, _overrides);
            Apply(CurrentSchemeId);
        }

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_FRAMECHANGED = 0x0020;

        // Windows 10 2004+ uses attribute 20; older 1809/1903/1909 builds use 19. Try the current
        // one first and fall back so the dark title bar works across supported Windows 10/11 builds.
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;

        // Windows 11 rounded window corners. Custom-chrome windows (WindowStyle=None) don't get the
        // OS default rounding, so request it explicitly; the OS clips the window (content included)
        // to the rounded outline, so nothing pokes out the corners. Ignored on Windows 10.
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWCP_ROUND = 2;

        // Windows 11 (build 22000+) only: paints the caption itself in an arbitrary color, instead
        // of the plain dark/light the IMMERSIVE_DARK_MODE flag above gives you. Windows 10 rejects
        // this attribute (DwmSetWindowAttribute returns a failure HRESULT), which we just ignore --
        // the window keeps whatever IMMERSIVE_DARK_MODE already gave it.
        private const int DWMWA_CAPTION_COLOR = 35;

        // DWM wants a COLORREF (0x00BBGGRR), not the ARGB order WPF's Color uses.
        private static int ToColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16);

        // Makes a window's OS-drawn title bar/border follow the current theme. Without this, the
        // native chrome stays light even when our own DynamicResource-themed content goes dark,
        // producing a jarring light-strip-over-dark-body look (see HolyMessageBox, ViewLogsWindow,
        // and every other dialog). Safe to call before or after the window is shown; no-ops if the
        // handle isn't ready yet or the OS is too old to support it.
        public static void ApplyWindowChrome(Window w)
        {
            if (w == null) return;
            try
            {
                IntPtr hwnd = new WindowInteropHelper(w).Handle;
                if (hwnd == IntPtr.Zero) return;
                int useDark = IsDark ? 1 : 0;
                int hr = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int));
                if (hr != 0)
                    DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref useDark, sizeof(int));

                // Rounded corners on Windows 11 (no-op / harmless on Windows 10).
                int round = DWMWCP_ROUND;
                DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));

                // Paint the title bar itself with the scheme's TitleBarBg token (Windows 11 only;
                // silently ignored on Windows 10, leaving the plain dark/light chrome above).
                int caption = ToColorRef((Color)ColorConverter.ConvertFromString(CurrentHex("TitleBarBg")));
                DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));

                // DWM caches the non-client frame and does not repaint it just because the attribute
                // changed -- without this, the title bar only picks up the new color after the next
                // resize/move, so it still shows light the first time a dialog opens. Forcing a
                // frame-changed, no-op resize/move makes it repaint immediately.
                SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
            }
            catch { /* pre-1809 Windows, or DWM unavailable: keep default chrome */ }
        }

        public static void ApplyFromSettings()
        {
            string id = null;
            try
            {
                id = Properties.Settings.Default.ColorSchemeId;
                // One-time migration from the pre-scheme Light/Dark boolean.
                if (string.IsNullOrEmpty(id))
                    id = Properties.Settings.Default.DarkMode ? "dark" : "light";

                id = MigrateLegacySchemeId(id);
                id = MigrateLegacyItemColors(id);
            }
            catch (System.Exception swallowed) { Log.Swallow(swallowed); }
            Apply(id ?? "light");
        }

        // Maps ids saved by earlier builds onto the current scheme set: the removed single "Custom"
        // scheme resolves to the base it was derived from (its stored edits are folded onto that base
        // by SchemeOverrides' own format migration), and the renamed "midnight" scheme becomes
        // "myscheme".
        private static string MigrateLegacySchemeId(string schemeId)
        {
            if (schemeId == "custom")
            {
                string baseId = null;
                try { baseId = Properties.Settings.Default.CustomSchemeBaseId; }
                catch (System.Exception swallowed) { Log.Swallow(swallowed); }
                return string.IsNullOrWhiteSpace(baseId) ? "light" : baseId;
            }
            if (schemeId == "midnight") return "myscheme";
            return schemeId;
        }

        // One-time migration of the pre-palette per-item color settings (main form background,
        // table header row, contest exchange frames -- formerly edited in Options > User Interface)
        // into the target scheme's overrides, so nobody's chosen colors are lost by the
        // consolidation. Afterward the legacy settings are reset to their defaults so this never
        // re-triggers. Returns the (unchanged) scheme id to apply.
        private static string MigrateLegacyItemColors(string schemeId)
        {
            var s = Properties.Settings.Default;
            var legacy = new (string Token, string Value, string Default)[]
            {
                ("FormBg",       s.MainFormBackgroundColor,       "#BDDFFF"),
                ("LogHeaderBg",  s.QsoTableHeaderBackgroundColor, "#DEB887"),
                ("ContestRxBg",  s.ContestExchangeColor,          "#FFF6C8"),
                ("ContestTxBg",  s.ContestSendColor,              "#E1F5EE"),
            };

            var found = new System.Collections.Generic.Dictionary<string, string>();
            foreach (var item in legacy)
                if (!string.IsNullOrWhiteSpace(item.Value)
                    && !string.Equals(item.Value.Trim(), item.Default, StringComparison.OrdinalIgnoreCase))
                    found[item.Token] = item.Value.Trim();

            if (found.Count == 0) return schemeId;

            var overrides = SchemeOverrides.For(schemeId);
            foreach (var kv in found)
                if (!overrides.ContainsKey(kv.Key))   // never overwrite an explicit Customize Colors choice
                    overrides[kv.Key] = kv.Value;
            SchemeOverrides.Save(schemeId, overrides);

            s.MainFormBackgroundColor = "#BDDFFF";
            s.QsoTableHeaderBackgroundColor = "#DEB887";
            s.ContestExchangeColor = "#FFF6C8";
            s.ContestSendColor = "#E1F5EE";
            try { s.Save(); } catch (System.Exception swallowed) { Log.Swallow(swallowed); }

            Log.Warn("Migrated legacy item colors into the " + schemeId + " scheme: " + string.Join(", ", found.Keys));
            return schemeId;
        }

        public static void Toggle() => Apply(IsDark ? "light" : "dark");
    }
}
