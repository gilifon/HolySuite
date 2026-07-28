using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace HolyLogger
{
    // Positions the "Received Confirmation" overlay label so it sits exactly over the green upper half of
    // the LoTW..Paper QSL header group, spanning their COMBINED width as one continuous line of text.
    //
    // WHY AN OVERLAY: each of the 5 confirmation columns draws its own green rectangle in its own header
    // (see Themes/ConfirmationColumns.xaml) - that is what makes the band look seamless without any
    // runtime code. But a DataGridColumnHeader is an opaque, separately-arranged visual: text placed in
    // ONE column's header cannot visually overflow into its neighbour, because the neighbour's own header
    // (with its own solid background) is drawn on top of that overflow. The only way to show ONE line of
    // text across several columns is a separate element layered above them, sized to their actual combined
    // on-screen bounds - which is what this class computes and keeps in sync.
    //
    // This is shared because both log tables (MainWindow's QSODataGrid and the Log Workshop's ResultsGrid)
    // need the identical calculation, just against a different DataGrid/overlay pair.
    internal static class ConfirmationStripHelper
    {
        private const string LabelText = "Received Confirmation";
        private const double EdgeInset = 2;    // keeps the first/last letter off the strip's very edges
        private const double Lift = 2;         // nudges the line up so it sits centred in the green band
        private const double Spread = 0.5;     // fraction of the leftover width used as letter spacing;
                                               // 1.0 fills the strip edge to edge, which reads too airy

        // Recomputes the overlay's position/size from the ACTUAL rendered bounds of the first and last
        // confirmation columns' headers, and only touches the overlay's layout properties if something
        // really changed - calling this from LayoutUpdated (which fires very often) must not itself cause
        // another layout pass every single time, or it would loop.
        public static void UpdatePosition(DataGrid grid, FrameworkElement overlay, ref Rect lastRect,
                                          string firstColumnSortPath, string lastColumnSortPath)
        {
            try
            {
                if (grid == null || overlay == null) return;
                if (!(overlay.Parent is FrameworkElement parent)) return;

                var firstColumn = grid.Columns.FirstOrDefault(c => c.SortMemberPath == firstColumnSortPath);
                var lastColumn = grid.Columns.FirstOrDefault(c => c.SortMemberPath == lastColumnSortPath);
                if (firstColumn == null || lastColumn == null) return;

                var presenter = FindVisualChild<DataGridColumnHeadersPresenter>(grid);
                if (presenter == null) return;

                var firstHeader = FindHeaderFor(presenter, firstColumn);
                var lastHeader = FindHeaderFor(presenter, lastColumn);
                if (firstHeader == null || lastHeader == null) return;
                if (!firstHeader.IsVisible || !lastHeader.IsVisible) return;
                if (firstHeader.ActualWidth <= 0 || lastHeader.ActualWidth <= 0) return;

                // Measured against the overlay's OWN parent, because the overlay is positioned with a Margin
                // and a Margin is relative to that parent's origin - measuring against anything else (e.g. the
                // window's outer Grid, when the overlay lives in one of its rows) offsets the label by the
                // height of everything above it. That is why the overlay and the DataGrid share one wrapper.
                Point topLeft = firstHeader.TransformToAncestor(parent).Transform(new Point(0, 0));
                Point topRightOfLast = lastHeader.TransformToAncestor(parent).Transform(new Point(lastHeader.ActualWidth, 0));

                double width = topRightOfLast.X - topLeft.X;
                double height = firstHeader.ActualHeight / 2.0;   // upper half only - matches the green band
                if (width <= 0 || height <= 0) return;

                var rect = new Rect(topLeft.X, topLeft.Y, width, height);
                if (rect == lastRect) return;   // unchanged - skip, so a stable layout does not keep re-arranging
                lastRect = rect;

                overlay.Margin = new Thickness(rect.X, rect.Y, 0, 0);
                overlay.Width = rect.Width;
                overlay.Height = rect.Height;

                SpreadLetters(overlay as Border, rect.Width - EdgeInset * 2);
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // Lays the label out as one TextBlock per character with an even gap between them, sized so the
        // whole line spans the strip end to end. WPF has no letter-spacing (tracking) property, so the
        // only way to stretch a caption across a fixed width without distorting the glyphs - which is what
        // scaling it would do - is to space the characters individually.
        private static void SpreadLetters(Border overlay, double availableWidth)
        {
            if (overlay == null || availableWidth <= 0) return;

            var panel = overlay.Child as StackPanel;
            if (panel == null)
            {
                panel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    RenderTransform = new TranslateTransform(0, -Lift)
                };
                foreach (char c in LabelText)
                {
                    // A plain space would measure as zero width here (WPF trims it), which would make the
                    // two words run together once every character is spaced apart - hence the hard space.
                    panel.Children.Add(new TextBlock { Text = c == ' ' ? "\u00A0" : c.ToString() });
                }
                overlay.Child = panel;   // font size/weight/colour are inherited from the Border
            }

            double lettersWidth = 0;
            foreach (TextBlock letter in panel.Children)
            {
                letter.Margin = new Thickness(0);   // measure the bare glyph, without last pass's gap
                letter.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                lettersWidth += letter.DesiredSize.Width;
            }

            int gaps = panel.Children.Count - 1;
            if (gaps <= 0) return;

            // The panel is centre-aligned, so a narrower spread stays centred on the strip.
            double gap = Math.Max(0, (availableWidth - lettersWidth) / gaps) * Spread;
            for (int i = 0; i < gaps; i++)
                ((TextBlock)panel.Children[i]).Margin = new Thickness(0, 0, gap, 0);
        }

        private static T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null) return null;
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T t) return t;
                var result = FindVisualChild<T>(child);
                if (result != null) return result;
            }
            return null;
        }

        private static DataGridColumnHeader FindHeaderFor(DependencyObject presenter, DataGridColumn column)
        {
            if (presenter == null) return null;
            int count = VisualTreeHelper.GetChildrenCount(presenter);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(presenter, i);
                if (child is DataGridColumnHeader h && h.Column == column) return h;
                var result = FindHeaderFor(child, column);
                if (result != null) return result;
            }
            return null;
        }
    }
}
