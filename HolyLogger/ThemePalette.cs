using System.Collections.Generic;
using System.Linq;

namespace HolyLogger
{
    // One selectable color scheme: an id (persisted in settings), the name shown in the
    // View > Color Scheme menu, whether the native window chrome (title bars) should be dark,
    // and which column of ThemePalette.Tokens holds its values.
    public class ColorScheme
    {
        public string Id { get; }
        public string DisplayName { get; }
        public bool IsDarkChrome { get; }
        public int Column { get; }

        public ColorScheme(string id, string displayName, bool isDarkChrome, int column)
        {
            Id = id; DisplayName = displayName; IsDarkChrome = isDarkChrome; Column = column;
        }
    }

    // SINGLE SOURCE OF TRUTH for every themeable color. Each token lists one hex per scheme,
    // in the column order declared in Schemes below. ThemeManager turns the active column into
    // frozen brushes on Application.Resources, so XAML (DynamicResource "Token") and C#
    // (ThemeManager.Brush("Token")) read the exact same values.
    //
    // TO ADD A NEW SCHEME: add one entry to Schemes (id, menu name, dark-chrome flag, next
    // column index) and one hex per token row below, in that column position. Nothing else in
    // the app changes -- the scheme menu, persistence and live switching all read this table.
    //
    // To theme a new surface: map it to an existing role token below, don't invent a new hex.
    public static class ThemePalette
    {
        public static readonly IReadOnlyList<ColorScheme> Schemes = new List<ColorScheme>
        {
            new ColorScheme("light",    "Light",         isDarkChrome: false, column: 0),
            new ColorScheme("dark",     "Dark",          isDarkChrome: true,  column: 1),
            new ColorScheme("midnight", "Midnight Blue", isDarkChrome: true,  column: 2),
        };

        // The scheme for an id, falling back to the first (light) for unknown/legacy values.
        public static ColorScheme FindScheme(string id)
            => Schemes.FirstOrDefault(s => s.Id == id) ?? Schemes[0];

        //                                              Light        Dark         Midnight
        public static readonly Dictionary<string, string[]> Tokens = new Dictionary<string, string[]>
        {
            // ---- Surfaces -------------------------------------------------------------------------
            { "WindowBg",         new[] { "#F0F0F0", "#121316", "#0D1420" } }, // window / app background
            { "FormBg",           new[] { "#BDDFFF", "#17181C", "#111A2B" } }, // main entry form area
            { "PanelBg",          new[] { "#FFFFFF", "#17181C", "#111A2B" } }, // generic panels
            { "MenuBg",           new[] { "#F0F0F0", "#262A31", "#1E2A40" } }, // menus / dropdowns (elevated surface, lighter than the base so popups read as a floating list)
            { "ControlBg",        new[] { "#FFFFFF", "#202227", "#16233A" } }, // text inputs
            { "EditFieldBg",      new[] { "#FFFF00", "#4A3D12", "#4A3D12" } }, // QSO fields while editing
            { "ButtonBg",         new[] { "#E1E1E1", "#2A2E35", "#1F2D47" } }, // default push buttons
            { "ButtonHoverBg",    new[] { "#D0D0D0", "#363C45", "#2A3B5C" } }, // default push buttons (hover)
            { "GridRowBg",        new[] { "#FFFFFF", "#121316", "#0D1420" } }, // table rows
            { "GridAltRowBg",     new[] { "#F5F5F5", "#17191D", "#121C2E" } }, // alternating rows
            { "GridHeaderBg",     new[] { "#E6E6E6", "#23262C", "#1B2740" } }, // column headers
            { "GridLine",         new[] { "#CCCCCC", "#363B42", "#2C3A55" } }, // table gridlines

            // ---- Text -----------------------------------------------------------------------------
            { "TextBrush",        new[] { "#000000", "#E8EAED", "#E6ECF5" } }, // primary text
            { "MutedTextBrush",   new[] { "#666666", "#9BA1AC", "#93A1B8" } }, // secondary / disabled text
            { "SelectionText",    new[] { "#FFFFFF", "#FFFFFF", "#FFFFFF" } }, // text on selected/accent surface
            { "KHzText",          new[] { "#000000", "#FFD400", "#FFD400" } }, // "kHz" unit label: black on light, yellow on dark schemes

            // ---- Lines ----------------------------------------------------------------------------
            { "ThemeBorderBrush", new[] { "#AAAAAA", "#2C2F35", "#26344E" } }, // borders / dividers
            { "MenuBorder",       new[] { "#9AA0A6", "#4A515C", "#3E5273" } }, // popup/menu frame — clearly visible against MenuBg

            // ---- Accent + stateful highlights -----------------------------------------------------
            { "AccentBrush",      new[] { "#1565C0", "#3B82F6", "#4C8DFF" } }, // primary accent (blue)
            { "ContestNameBrush", new[] { "#1565C0", "#22D3EE", "#22D3EE" } }, // status-bar contest name: bright cyan reads far better on dark status bars than the plain accent blue
            { "Danger",           new[] { "#BB3300", "#F0846E", "#F0846E" } }, // errors / missing / warnings (red)
            { "SelectionBg",      new[] { "#3399FF", "#2C4C7A", "#2F5490" } }, // selected row/cell, menu hover
            { "RowHoverBg",       new[] { "#90CAF9", "#26456B", "#2A4A78" } }, // cluster: map-hovered spot
            { "RowOnFreqBg",      new[] { "#90EE90", "#1E4A2A", "#1E4A2A" } }, // cluster: on-frequency spot
            { "FilterRowBg",      new[] { "#C8F0D0", "#1E4030", "#1E4030" } }, // QSO grid: filtered-match row
            { "FilterRowAltBg",   new[] { "#A8D8B4", "#183328", "#183328" } }, // QSO grid: filtered-match alt row
        };
    }
}
