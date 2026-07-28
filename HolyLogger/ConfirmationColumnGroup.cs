using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;

namespace HolyLogger
{
    // Keeps the five confirmation columns (LoTW / QRZ / eQSL / Club Log / Paper QSL) behaving as ONE
    // block in both log tables: the operator can drag them anywhere in the table, but they always travel
    // together, always in the same order, and no other column can end up between them.
    //
    // WHY: they are read as a set - "what has confirmed this QSO" - and the green strip with its
    // "Received Confirmation" caption is drawn across their COMBINED width (see ConfirmationStripHelper).
    // A foreign column landing in the middle splits that strip in two and puts the caption over a column
    // it does not describe.
    //
    // The order is corrected after the drop rather than during the drag, because WPF's DataGrid gives no
    // way to veto one drop position: ColumnReordering fires when the gesture starts, before a target
    // exists, and can only cancel the whole thing. So the reorder is allowed to happen and is then put
    // right - which is invisible to the operator, as it completes before the next frame is drawn.
    internal static class ConfirmationColumnGroup
    {
        // The group, in the order it must always appear in.
        private static readonly string[] GroupOrder =
        {
            "LotwStatusRank", "QrzStatusRank", "EqslStatusRank", "ClublogStatusRank", "PaperQslStatusRank"
        };

        // Guards against reacting to the DisplayIndex changes this class makes itself.
        private static bool _rearranging;

        public static void Attach(DataGrid grid)
        {
            if (grid == null) return;
            grid.ColumnReordered += (s, e) => OnColumnReordered(grid, e.Column);
        }

        // Pulls the group back together around wherever its FIRST member currently sits. Needed after a
        // saved column order is restored: the main window remembers a position for LoTW only, so the other
        // four would otherwise stay at the place the XAML gave them and the group would arrive split.
        public static void Normalize(DataGrid grid)
        {
            if (grid == null) return;
            try
            {
                var group = FindGroup(grid);
                if (group.Count < 2) return;
                MoveGroupTo(grid, group, group[0].DisplayIndex);
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        private static void OnColumnReordered(DataGrid grid, DataGridColumn moved)
        {
            if (_rearranging || grid == null || moved == null) return;
            try
            {
                _rearranging = true;

                var group = FindGroup(grid);
                if (group.Count < 2) return;

                int grabbed = group.IndexOf(moved);
                if (grabbed < 0)
                    EvictFromInsideGroup(group, moved);
                else
                    // The block is positioned so the member the operator actually grabbed lands where they
                    // dropped it - dragging by the 4th header should not jump the block three columns over.
                    MoveGroupTo(grid, group, moved.DisplayIndex - grabbed);
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
            finally { _rearranging = false; }
        }

        private static List<DataGridColumn> FindGroup(DataGrid grid)
        {
            return GroupOrder
                .Select(path => grid.Columns.FirstOrDefault(c => c.SortMemberPath == path))
                .Where(c => c != null)
                .ToList();
        }

        // Rebuilds the whole display order in one pass, assigning every column its final position from
        // left to right. That order matters: WPF shifts the other columns along on each individual
        // DisplayIndex assignment, and only a left-to-right pass is immune to it - each column placed at
        // index i can only have come from a position at or right of i, so the ones already placed
        // (0..i-1) are never disturbed.
        private static void MoveGroupTo(DataGrid grid, List<DataGridColumn> group, int blockStart)
        {
            var others = grid.Columns.Where(c => !group.Contains(c)).OrderBy(c => c.DisplayIndex).ToList();
            int insertAt = others.Count(c => c.DisplayIndex < blockStart);

            var order = new List<DataGridColumn>(grid.Columns.Count);
            order.AddRange(others.Take(insertAt));
            order.AddRange(group);
            order.AddRange(others.Skip(insertAt));

            for (int i = 0; i < order.Count; i++)
                order[i].DisplayIndex = i;
        }

        // A column dropped BETWEEN two members is pushed out to whichever side it landed nearer, so it
        // still ends up close to where the operator aimed. Taking the group's first (or last) slot also
        // closes the gap in one move: WPF shifts the displaced members along by one, which restores the
        // unbroken run without touching anything else.
        private static void EvictFromInsideGroup(List<DataGridColumn> group, DataGridColumn intruder)
        {
            int first = group.Min(c => c.DisplayIndex);
            int last = group.Max(c => c.DisplayIndex);
            int dropped = intruder.DisplayIndex;

            if (dropped <= first || dropped >= last) return;   // dropped clear of the group - allowed

            intruder.DisplayIndex = (dropped - first) <= (last - dropped) ? first : last;
        }
    }
}
