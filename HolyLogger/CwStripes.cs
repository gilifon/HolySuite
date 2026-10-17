using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace HolyLogger
{
    // ROWS IN TURN WHITE AND PALE BLUE, in every box of the decode window (his request, 2026-10-17 - Both had
    // them first, see CwTimeLine), so the eye keeps its place on a line.
    //
    // The text wraps, so a "row" is a line on screen, not a piece of text, and nothing in the document knows
    // where one ends. So every line is given exactly the same height (LineHeight with BlockLineHeight - a bold
    // or patched word cannot make one line taller), and the document's own background is a two-line tile of
    // that height starting at the top of the first line. The document background scrolls with the text, so
    // the stripes stay under their lines.
    public static class CwStripes
    {
        public const double LineHeight = 24;
        public static readonly Brush White = Brushes.White;
        public static readonly Brush Blue = Frozen(Color.FromRgb(0xDC, 0xEB, 0xFA));

        static Brush Frozen(Color colour)
        {
            var brush = new SolidColorBrush(colour);
            brush.Freeze();
            return brush;
        }

        public static void Apply(FlowDocument doc)
        {
            doc.LineHeight = LineHeight;
            doc.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            var group = new DrawingGroup();
            group.Children.Add(new GeometryDrawing(White, null, new RectangleGeometry(new Rect(0, 0, 10, LineHeight))));
            group.Children.Add(new GeometryDrawing(Blue, null, new RectangleGeometry(new Rect(0, LineHeight, 10, LineHeight))));
            var tile = new DrawingBrush(group)
            {
                TileMode = TileMode.Tile,
                ViewportUnits = BrushMappingMode.Absolute,
                Viewport = new Rect(0, doc.PagePadding.Top, 10, 2 * LineHeight),
                Stretch = Stretch.Fill
            };
            tile.Freeze();
            doc.Background = tile;
        }
    }
}
