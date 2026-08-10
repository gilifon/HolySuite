using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace HolyLogger
{
    // Where the columns of a log table sit and how wide they are, as ONE string that can be kept in a
    // setting: "Header=width" per column, left to right, separated by |.
    //
    //     Date=90|Time=64|Callsign=110|...
    //
    // WHY A STRING AND NOT A SETTING PER COLUMN: the main window used to keep Date_index, Time_index,
    // Callsign_index and so on - hand-written, one per column, and only for the columns somebody
    // remembered at the time. Every column added later (the confirmation block, QTH) had nothing to be
    // remembered by, so the operator could move it, close the program, and find it back where the XAML
    // put it. A layout read straight off the grid cannot leave a column out, including columns added in
    // versions after this one.
    //
    // Both log tables use this - the main window's and the Log Workshop's - so neither can drift into
    // remembering its columns differently from the other.
    internal static class GridColumnLayout
    {
        private const char Separator = '|';

        // The header text WITHOUT the sort-arrow suffix the main window appends ("Date  ▼"). That suffix
        // comes and goes as the operator sorts, so it must never be part of the key a column is found by.
        public static string BaseHeader(DataGridColumn col)
        {
            string header = col?.Header as string;
            if (string.IsNullOrEmpty(header)) return header;

            int idx = header.IndexOfAny(new[] { '▲', '▼' });
            return idx >= 0 ? header.Substring(0, idx).TrimEnd() : header;
        }

        // What a column is remembered BY. Its header text where it has one - but the Log Workshop's
        // tick-box column has a checkbox for a header, not a string, and a column left out of the layout
        // is a column the restore would shove aside while placing the others. So those fall back to their
        // position in the XAML's column list, which a drag does not change (dragging moves DisplayIndex,
        // never the Columns collection), and which stays put when a later version appends a column.
        private static string KeyOf(DataGrid grid, DataGridColumn col)
        {
            string header = BaseHeader(col);
            if (!string.IsNullOrWhiteSpace(header)
                && header.IndexOf(Separator) < 0 && header.IndexOf('=') < 0)
                return header;
            return "#" + grid.Columns.IndexOf(col).ToString(CultureInfo.InvariantCulture);
        }

        // The grid's current arrangement, ready to be stored. Empty string when there is nothing to store.
        public static string Capture(DataGrid grid)
        {
            if (grid == null) return string.Empty;

            var parts = new List<string>(grid.Columns.Count);
            foreach (var col in grid.Columns.OrderBy(c => c.DisplayIndex))
            {
                // Rounded: a fractional pixel is noise, and it would rewrite the setting on every run.
                int width = (int)Math.Round(col.ActualWidth);
                parts.Add(KeyOf(grid, col) + "=" + width.ToString(CultureInfo.InvariantCulture));
            }
            return string.Join(Separator.ToString(), parts);
        }

        // Puts a stored arrangement back on the grid. A column the layout does not mention - one added by
        // a later version - keeps the place the XAML gave it, which is the far right, and is picked up the
        // next time the layout is captured.
        public static void Apply(DataGrid grid, string layout)
        {
            if (grid == null || string.IsNullOrWhiteSpace(layout)) return;

            // Left to right, one pass: assigning DisplayIndex shifts the other columns along, and only
            // this direction is immune to that - a column placed at i can only have come from at or right
            // of i, so everything already placed stays put. (Same reason as ConfirmationColumnGroup's
            // MoveGroupTo, which this deliberately mirrors.)
            int next = 0;
            foreach (string part in layout.Split(Separator))
            {
                if (part.Length == 0) continue;
                int eq = part.LastIndexOf('=');
                string key = eq < 0 ? part : part.Substring(0, eq);
                string widthText = eq < 0 ? string.Empty : part.Substring(eq + 1);

                var col = grid.Columns.FirstOrDefault(c =>
                    string.Equals(KeyOf(grid, c), key, StringComparison.Ordinal));
                if (col == null) continue;                  // a column this version no longer has

                col.DisplayIndex = next++;

                int width;
                if (int.TryParse(widthText, NumberStyles.Integer, CultureInfo.InvariantCulture, out width)
                    && width > 0 && col.Visibility == Visibility.Visible)
                    col.Width = new DataGridLength(width);
            }

            // The five confirmation columns travel as one block. A layout that predates some of them would
            // otherwise leave the missing ones where the XAML put them and the block would arrive split.
            ConfirmationColumnGroup.Normalize(grid);
        }
    }
}
