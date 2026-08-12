using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace HolyLogger
{
    // COPYING OUT OF THE TABLES. Every grid in this program was read-only in the strictest sense: the
    // text on screen could not be got out of it at all, so a callsign or a country had to be typed
    // again by hand to put it in an e-mail.
    //
    // Two ways, deliberately: Ctrl+C for the rows you have selected - which is the DataGrid's own
    // command, and lands in Excel as columns because it copies tab-separated - and a right-click item
    // for the ONE cell under the mouse, which is what you want when you only need a callsign.
    //
    // Cell selection (dragging a rectangle of cells, spreadsheet style) is deliberately NOT here: the
    // log table and the Workshop both build their row highlight and their tick boxes on full-row
    // selection, and changing that is a bigger job than a copy command.
    internal static class GridCopy
    {
        // Ctrl+C copies the selected rows WITH their column headings, so a block pasted into a mail or
        // a spreadsheet says what each column is. Call once when the grid is built.
        public static void Enable(DataGrid grid)
        {
            if (grid == null) return;
            grid.ClipboardCopyMode = DataGridClipboardCopyMode.IncludeHeader;

            // A TEMPLATE COLUMN COPIES NOTHING UNLESS TOLD WHAT TO COPY. DataGrid asks each column for
            // its clipboard text through ClipboardContentBinding, and a DataGridTextColumn has one for
            // free - its own Binding. A DataGridTemplateColumn does not: the template could hold
            // anything, so WPF refuses to guess and hands back an empty string. Almost every column in
            // this program is a template column, which is why Ctrl+C pasted a row of headings with
            // nothing underneath them.
            //
            // SortMemberPath is the answer already sitting there: every sortable column names the
            // property it stands for, because sorting needs exactly the same thing. So a column that
            // can be sorted can be copied, and one that cannot is left alone rather than guessed at.
            foreach (var col in grid.Columns)
            {
                if (col.ClipboardContentBinding != null) continue;

                string path = col.SortMemberPath;
                if (string.IsNullOrWhiteSpace(path)) path = PathForHeader(col.Header);
                if (string.IsNullOrWhiteSpace(path)) continue;

                try { col.ClipboardContentBinding = new System.Windows.Data.Binding(path); }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
            }
        }

        // The columns SortMemberPath does not cover. Band, Mode, Frequency, the reports and the comment
        // are not sortable in the log table, so they name no property anywhere - and they were the
        // cells that came out blank. Keyed on the heading the operator sees, which is the one thing
        // both tables have in common; a heading not listed here simply does not copy, which is better
        // than copying the wrong field.
        private static string PathForHeader(object header)
        {
            string h = (header as string ?? "").Trim();
            switch (h)
            {
                case "Frequency": return "Freq";
                case "Name": return "Name";
                case "Band": return "Band";
                case "RST-R": return "RST_RCVD";
                case "RST-S": return "RST_SENT";
                case "Mode": return "Mode";
                case "Submode": return "SUBMode";
                case "Exchange": return "SRX";
                case "Comment": return "Comment";
                case "Continent": return "Continent";
                case "State": return "State";
                case "CQ zone": return "CQZone";
                case "ITU zone": return "ITUZone";
                case "DX Locator": return "DXLocator";
                case "My callsign": return "MyCall";
                case "Operator": return "Operator";
            }
            return null;
        }

        // The cell the mouse is over, found by walking up from whatever was actually clicked - the
        // TextBlock inside the cell, usually. Returns null when the click was not on a cell at all
        // (the header, the empty space below the rows).
        public static DataGridCell CellFrom(object originalSource)
        {
            DependencyObject d = originalSource as DependencyObject;
            while (d != null && !(d is DataGridCell))
            {
                DependencyObject parent = null;
                if (d is Visual || d is System.Windows.Media.Media3D.Visual3D)
                    parent = VisualTreeHelper.GetParent(d);
                if (parent == null) parent = LogicalTreeHelper.GetParent(d);
                d = parent;
            }
            return d as DataGridCell;
        }

        // What a cell READS AS. A cell can hold a plain text block, an editable box, a tick, or - in the
        // Log Fixer - two stacked halves, so everything textual inside it is gathered in the order it
        // is drawn. A tick becomes "yes"/"" rather than being silently dropped.
        public static string TextOf(DataGridCell cell)
        {
            if (cell == null) return null;
            var parts = new List<string>();
            Collect(cell, parts);
            return string.Join("  ", parts);
        }

        private static void Collect(DependencyObject d, List<string> parts)
        {
            if (d == null) return;

            var tb = d as TextBlock;
            if (tb != null)
            {
                string t = (tb.Text ?? "").Trim();
                if (t.Length > 0) parts.Add(t);
                return;                       // a TextBlock's Runs are its own text, not extra parts
            }

            var box = d as TextBox;
            if (box != null)
            {
                string t = (box.Text ?? "").Trim();
                if (t.Length > 0) parts.Add(t);
                return;
            }

            var check = d as CheckBox;
            if (check != null)
            {
                parts.Add(check.IsChecked == true ? "yes" : "");
                return;
            }

            int n = VisualTreeHelper.GetChildrenCount(d);
            for (int i = 0; i < n; i++) Collect(VisualTreeHelper.GetChild(d, i), parts);
        }

        // Clipboard.SetText throws often enough to be worth catching - another program holding the
        // clipboard open is all it takes, and failing to copy must never take the window down.
        public static void Put(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            try { Clipboard.SetText(text); }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        public static void CopySelectedRows(DataGrid grid)
        {
            if (grid == null) return;
            try
            {
                if (grid.SelectedItems == null || grid.SelectedItems.Count == 0) return;
                ApplicationCommands.Copy.Execute(null, grid);
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // The two menu items, ready to be added to a table's existing right-click menu. `cellText` is
        // captured when the menu is built, because by the time the item is clicked the mouse has moved
        // off the cell it was over.
        public static MenuItem CopyCellItem(string cellText, Style itemStyle = null)
        {
            string shown = (cellText ?? "").Trim();
            if (shown.Length > 28) shown = shown.Substring(0, 28) + "…";

            var item = new MenuItem
            {
                Header = shown.Length == 0 ? "Copy this cell" : "Copy \"" + shown + "\"",
                IsEnabled = !string.IsNullOrWhiteSpace(cellText)
            };
            if (itemStyle != null) item.Style = itemStyle;
            item.Click += (s, e) => Put(cellText);
            return item;
        }

        public static MenuItem CopyRowsItem(DataGrid grid, Style itemStyle = null)
        {
            int n = grid == null || grid.SelectedItems == null ? 0 : grid.SelectedItems.Count;
            var item = new MenuItem
            {
                Header = n > 1 ? "Copy these " + n.ToString("N0") + " rows" : "Copy this row",
                IsEnabled = n > 0
            };
            if (itemStyle != null) item.Style = itemStyle;
            item.Click += (s, e) => CopySelectedRows(grid);
            return item;
        }
    }
}
