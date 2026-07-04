using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace HolyLogger
{
    // Central theme switch. Applying a mode turns the corresponding ThemePalette column into frozen
    // brushes set DIRECTLY on Application.Resources, so every DynamicResource in the live UI (and
    // every ThemeManager.Brush() call in code) re-resolves to the new values. Anything painted in
    // code subscribes to ThemeChanged and re-paints. The choice is persisted in Settings.
    public static class ThemeManager
    {
        public static ThemeMode CurrentMode { get; private set; } = ThemeMode.Light;

        // Convenience for the current Light/Dark toggle UI.
        public static bool IsDark => CurrentMode == ThemeMode.Dark;

        // Raised after the palette changes, so code-painted areas can re-run their coloring.
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

        public static void Apply(ThemeMode mode)
        {
            CurrentMode = mode;

            var res = Application.Current.Resources;
            foreach (var kv in ThemePalette.Tokens)
            {
                string hex = kv.Value[(int)mode];
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
                Properties.Settings.Default.DarkMode = (mode == ThemeMode.Dark);
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

        // Convenience overload for the Light/Dark checkbox toggle.
        public static void Apply(bool dark) => Apply(dark ? ThemeMode.Dark : ThemeMode.Light);

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
            bool dark = false;
            try { dark = Properties.Settings.Default.DarkMode; } catch (System.Exception swallowed) { Log.Swallow(swallowed); }
            Apply(dark ? ThemeMode.Dark : ThemeMode.Light);
        }

        public static void Toggle() => Apply(IsDark ? ThemeMode.Light : ThemeMode.Dark);
    }
}
