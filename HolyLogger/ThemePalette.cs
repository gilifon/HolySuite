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

    // User-facing description of one color role, for the Customize Colors dialog: which token it
    // controls, the group it is shown under, and a friendly name + explanation a non-programmer
    // can act on. The catalog order is the display order.
    public class TokenInfo
    {
        public string Key { get; }
        public string Group { get; }
        public string DisplayName { get; }
        public string Description { get; }

        public TokenInfo(string key, string group, string displayName, string description)
        {
            Key = key; Group = group; DisplayName = displayName; Description = description;
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
            new ColorScheme("light",    "Light",     isDarkChrome: false, column: 0),
            new ColorScheme("dark",     "Dark",      isDarkChrome: true,  column: 1),
            new ColorScheme("myscheme", "My scheme", isDarkChrome: true,  column: 2),
        };

        // The scheme for an id, falling back to the first (light) for unknown/legacy values.
        public static ColorScheme FindScheme(string id)
            => Schemes.FirstOrDefault(s => s.Id == id) ?? Schemes[0];

        //                                              Light        Dark         My scheme
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
            { "ResetButtonBg",    new[] { "#7CD992", "#2F6B3F", "#2F6B3F" } }, // "restore defaults" button (green)
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
            // Frame around the fields you type into. Deliberately stronger than ThemeBorderBrush: a
            // text box has to be findable at a glance, and WPF's own default border is so faint that a
            // row of empty boxes reads as blank space. Light scheme goes DARKER against white; the dark
            // schemes go LIGHTER, since there "darker" would mean invisible.
            { "InputBorder",      new[] { "#7A7F87", "#5A626E", "#4E6A93" } },

            // ---- Accent + stateful highlights -----------------------------------------------------
            { "AccentBrush",      new[] { "#1565C0", "#3B82F6", "#4C8DFF" } }, // primary accent (blue)
            { "ContestNameBrush", new[] { "#1565C0", "#22D3EE", "#22D3EE" } }, // status-bar contest name: bright cyan reads far better on dark status bars than the plain accent blue
            { "Danger",           new[] { "#BB3300", "#F0846E", "#F0846E" } }, // errors / missing / warnings (red)
            { "SelectionBg",      new[] { "#3399FF", "#2C4C7A", "#2F5490" } }, // selected row/cell, menu hover
            { "RowHoverBg",       new[] { "#90CAF9", "#26456B", "#2A4A78" } }, // cluster: map-hovered spot
            { "RowOnFreqBg",      new[] { "#90EE90", "#1E4A2A", "#1E4A2A" } }, // cluster: on-frequency spot
            { "RowLotwBg",        new[] { "#FFF176", "#4A4416", "#4A4416" } }, // cluster: LoTW user spot (yellow)
            { "FilterRowBg",      new[] { "#C8F0D0", "#1E4030", "#1E4030" } }, // QSO grid: filtered-match row
            { "FilterRowAltBg",   new[] { "#A8D8B4", "#183328", "#183328" } }, // QSO grid: filtered-match alt row
            // The same pale blue a selected row has always had in the Log Workshop, so a ticked row looks
            // exactly like a selected one - and every ticked row looks that way at once. Identical in all
            // three schemes because the Workshop's selection blue is itself fixed (a ticked row keeps dark
            // text on it, which only works over a light background).
            { "RowPickedBg",      new[] { "#CFE8FF", "#CFE8FF", "#CFE8FF" } }, // Log Workshop: row ticked in the selection column
            { "WorkedElsewhereBg",    new[] { "#CFE3FF", "#183048", "#183048" } }, // QSO grid: same call found in the copy-target log (reference only)
            { "WorkedElsewhereAltBg", new[] { "#B3D2F5", "#12263A", "#12263A" } }, // QSO grid: same-call target-log alt row

            // ---- Window chrome ----------------------------------------------------------------------
            { "TitleBarBg",       new[] { "#BDDFFF", "#121316", "#0D1420" } }, // window title-bar background; drives the main + cluster custom title bars everywhere, and native dialog captions on Win11. Light default matches the GUI entry-form background (FormBg #BDDFFF).

            // ---- Designer accent surfaces (same in every scheme by design; user-overridable) -------
            { "LogHeaderBg",      new[] { "#DEB887", "#DEB887", "#DEB887" } }, // QSO log / cluster / Logs window header row (black text on it)
            { "ContestRxBg",      new[] { "#FFF6C8", "#FFF6C8", "#FFF6C8" } }, // contest mode: received-exchange frame
            { "ContestTxBg",      new[] { "#E1F5EE", "#E1F5EE", "#E1F5EE" } }, // contest mode: send-exchange band
        };

        // What each role means, in words the operator understands. Shown by the Customize Colors
        // dialog (View > Color Scheme > Customize Colors). Keep every Tokens key listed here.
        public static readonly IReadOnlyList<TokenInfo> TokenCatalog = new List<TokenInfo>
        {
            new TokenInfo("WindowBg",         "Surfaces", "Window background",        "Background of every window and dialog."),
            new TokenInfo("FormBg",           "Surfaces", "Entry form",               "Background of the QSO entry area on the main screen."),
            new TokenInfo("PanelBg",          "Surfaces", "Panels",                   "Background of lists and content panels inside windows."),
            new TokenInfo("MenuBg",           "Surfaces", "Menus and dropdowns",      "Background of menus and dropdown lists."),
            new TokenInfo("ControlBg",        "Surfaces", "Input fields",             "Background of text boxes you type into."),
            new TokenInfo("EditFieldBg",      "Surfaces", "Field being edited",       "Highlight of the QSO fields while editing an existing QSO."),
            new TokenInfo("ButtonBg",         "Surfaces", "Buttons",                  "Background of standard buttons."),
            new TokenInfo("ButtonHoverBg",    "Surfaces", "Buttons (mouse over)",     "Button background when the mouse is over it."),
            new TokenInfo("ResetButtonBg",    "Surfaces", "Restore-defaults button",  "Background of the green 'Restore factory defaults' button in the Profile Manager."),

            new TokenInfo("TextBrush",        "Text", "Main text",                    "Almost all text in the application."),
            new TokenInfo("MutedTextBrush",   "Text", "Secondary text",               "Hints, notes and less important labels."),
            new TokenInfo("SelectionText",    "Text", "Text on selection",            "Text drawn on top of a selected (highlighted) item."),
            new TokenInfo("KHzText",          "Text", "kHz label",                    "The small kHz unit next to the frequency readout."),

            new TokenInfo("GridRowBg",        "Tables", "Table rows",                 "Background of table rows (log, cluster, lists)."),
            new TokenInfo("GridAltRowBg",     "Tables", "Table rows (alternate)",     "Every second table row, for easier reading."),
            new TokenInfo("LogHeaderBg",      "Tables", "Log table headers",          "Header row of the QSO log, the cluster and the Logs window (text on it is always black)."),
            new TokenInfo("GridHeaderBg",     "Tables", "Table headers (other)",      "Headers of secondary tables and lists."),
            new TokenInfo("GridLine",         "Tables", "Table grid lines",           "The thin lines between table rows and columns."),
            new TokenInfo("SelectionBg",      "Tables", "Selected item",              "Background of the selected row, cell or menu item."),
            new TokenInfo("FilterRowBg",      "Tables", "Filter match rows",          "Log rows that match the active callsign filter."),
            new TokenInfo("FilterRowAltBg",   "Tables", "Filter match rows (alt.)",   "Every second filter-match row."),
            new TokenInfo("RowPickedBg",      "Tables", "Ticked rows (Log Workshop)", "Log Workshop rows you have ticked with the selection checkbox. All of them are highlighted, not only the last one clicked."),
            new TokenInfo("WorkedElsewhereBg",    "Tables", "Worked-before (target log)",       "Same-callsign QSOs found in the copy-target log, shown for reference."),
            new TokenInfo("WorkedElsewhereAltBg", "Tables", "Worked-before (target log, alt.)", "Every second reference row from the copy-target log."),
            new TokenInfo("RowHoverBg",       "Tables", "Cluster: map-hovered spot",  "Cluster row highlighted while hovering its dot on the map."),
            new TokenInfo("RowOnFreqBg",      "Tables", "Cluster: on-frequency spot", "Cluster row whose frequency matches your radio."),
            new TokenInfo("RowLotwBg",        "Tables", "Cluster: LoTW user spot",    "Cluster row whose DX callsign uploads to Logbook of The World (LoTW)."),

            new TokenInfo("TitleBarBg",       "Window chrome", "Title bar",              "Background of the title bar on the main window and the Cluster window. Dialog title bars also follow it on Windows 11."),

            new TokenInfo("ContestRxBg",      "Contest mode", "Received exchange frame", "The frame behind the received-exchange boxes in contest mode. Also editable by right-clicking the frame itself."),
            new TokenInfo("ContestTxBg",      "Contest mode", "Send exchange band",      "The band behind the sent-exchange boxes in contest mode. Also editable by right-clicking the band itself."),

            new TokenInfo("ThemeBorderBrush", "Borders and accents", "Borders",       "Borders and divider lines around panels and controls."),
            new TokenInfo("MenuBorder",       "Borders and accents", "Menu frame",    "The frame around popup menus and dropdown lists."),
            new TokenInfo("InputBorder",      "Borders and accents", "Input frame",   "The frame around text boxes you type into."),
            new TokenInfo("AccentBrush",      "Borders and accents", "Accent",        "Headings, links and emphasized labels (the blue)."),
            new TokenInfo("ContestNameBrush", "Borders and accents", "Contest name",  "The active contest's name in the status bar."),
            new TokenInfo("Danger",           "Borders and accents", "Errors and warnings", "Error text and warning markers (the red)."),
        };
    }
}
