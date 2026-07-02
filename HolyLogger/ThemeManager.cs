using System;
using System.Windows;
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

            try
            {
                Properties.Settings.Default.DarkMode = (mode == ThemeMode.Dark);
                Properties.Settings.Default.Save();
            }
            catch { }

            try { ThemeChanged?.Invoke(); } catch { }
        }

        // Convenience overload for the Light/Dark checkbox toggle.
        public static void Apply(bool dark) => Apply(dark ? ThemeMode.Dark : ThemeMode.Light);

        public static void ApplyFromSettings()
        {
            bool dark = false;
            try { dark = Properties.Settings.Default.DarkMode; } catch { }
            Apply(dark ? ThemeMode.Dark : ThemeMode.Light);
        }

        public static void Toggle() => Apply(IsDark ? ThemeMode.Light : ThemeMode.Dark);
    }
}
