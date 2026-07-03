using System.Collections.Generic;

namespace HolyLogger
{
    // Named theme modes. To add a future scheme (e.g. HighContrast, Sepia): add an enum value here
    // AND one extra hex per row in Tokens below (in the same column order). Nothing else changes -
    // the UI only ever references role tokens, never raw colors.
    public enum ThemeMode { Light = 0, Dark = 1 }

    // SINGLE SOURCE OF TRUTH for every themeable color. Each token lists one hex per mode, in the
    // order of the ThemeMode enum (Light, Dark, ...). ThemeManager turns the active column into
    // frozen brushes on Application.Resources, so XAML (DynamicResource "Token") and C#
    // (ThemeManager.Brush("Token")) read the exact same values.
    //
    // To theme a new surface: map it to an existing role token below, don't invent a new hex.
    public static class ThemePalette
    {
        //                                              Light        Dark
        public static readonly Dictionary<string, string[]> Tokens = new Dictionary<string, string[]>
        {
            // ---- Surfaces: neutral near-black ramp in dark mode -----------------------------------
            { "WindowBg",         new[] { "#F0F0F0", "#121316" } }, // window / app background
            { "FormBg",           new[] { "#BDDFFF", "#17181C" } }, // main entry form area
            { "PanelBg",          new[] { "#FFFFFF", "#17181C" } }, // generic panels
            { "MenuBg",           new[] { "#F0F0F0", "#262A31" } }, // menus / dropdowns (elevated surface, lighter than the near-black base so popups read as a floating list)
            { "ControlBg",        new[] { "#FFFFFF", "#202227" } }, // text inputs
            { "EditFieldBg",      new[] { "#FFFF00", "#4A3D12" } }, // QSO fields while editing
            { "ButtonBg",         new[] { "#E1E1E1", "#2A2E35" } }, // default push buttons
            { "ButtonHoverBg",    new[] { "#D0D0D0", "#363C45" } }, // default push buttons (hover)
            { "GridRowBg",        new[] { "#FFFFFF", "#121316" } }, // table rows
            { "GridAltRowBg",     new[] { "#F5F5F5", "#17191D" } }, // alternating rows
            { "GridHeaderBg",     new[] { "#E6E6E6", "#23262C" } }, // column headers
            { "GridLine",         new[] { "#CCCCCC", "#363B42" } }, // table gridlines

            // ---- Text -----------------------------------------------------------------------------
            { "TextBrush",        new[] { "#000000", "#E8EAED" } }, // primary text
            { "MutedTextBrush",   new[] { "#666666", "#9BA1AC" } }, // secondary / disabled text
            { "SelectionText",    new[] { "#FFFFFF", "#FFFFFF" } }, // text on selected/accent surface
            { "KHzText",          new[] { "#000000", "#FFD400" } }, // "kHz" unit label: black on light, yellow on dark

            // ---- Lines ----------------------------------------------------------------------------
            { "ThemeBorderBrush", new[] { "#AAAAAA", "#2C2F35" } }, // borders / dividers
            { "MenuBorder",       new[] { "#9AA0A6", "#4A515C" } }, // popup/menu frame — clearly visible against MenuBg

            // ---- Accent + stateful highlights -----------------------------------------------------
            { "AccentBrush",      new[] { "#1565C0", "#3B82F6" } }, // primary accent (blue)
            { "Danger",           new[] { "#BB3300", "#F0846E" } }, // errors / missing / warnings (red)
            { "SelectionBg",      new[] { "#3399FF", "#2C4C7A" } }, // selected row/cell, menu hover
            { "RowHoverBg",       new[] { "#90CAF9", "#26456B" } }, // cluster: map-hovered spot
            { "RowOnFreqBg",      new[] { "#90EE90", "#1E4A2A" } }, // cluster: on-frequency spot
            { "FilterRowBg",      new[] { "#C8F0D0", "#1E4030" } }, // QSO grid: filtered-match row
            { "FilterRowAltBg",   new[] { "#A8D8B4", "#183328" } }, // QSO grid: filtered-match alt row
        };
    }
}
