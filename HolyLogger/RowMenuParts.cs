using HolyParser;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace HolyLogger
{
    // The pieces both QSO right-click menus are built from: the one over the main window's log table and
    // the one over the Log Workshop's table.
    //
    // They are here rather than in either window because the two menus have quietly drifted apart before -
    // the same caption or the same logger list built twice ends up looking like two different programs.
    // The menus themselves stay separate (they offer genuinely different actions); only the shared parts
    // live here.
    internal static class RowMenuParts
    {
        private static readonly Brush TitleBrush = new SolidColorBrush(Color.FromRgb(0x15, 0x65, 0xC0));
        private static readonly Brush SubtitleBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
        private static readonly Brush ItemTextBrush = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A));

        // The caption at the top of a menu: what it is about to act on.
        //
        // Drawn as a caption rather than a disabled MenuItem on purpose - a menu item that cannot be
        // clicked greys itself out and reads as something broken, when this is the one line that should
        // stand out. Sizes are deliberately large: this is read at a glance, often over a row the menu
        // itself is covering.
        public static UIElement MakeMenuTitle(string title, string subtitle)
        {
            // Centred, both lines: the caption is a heading over the menu, not another item in the column
            // of choices, and centring is what separates the two at a glance.
            var panel = new StackPanel { Margin = new Thickness(14, 4, 14, 2) };
            panel.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 20,
                FontWeight = FontWeights.Bold,
                Foreground = TitleBrush,
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            if (!string.IsNullOrWhiteSpace(subtitle))
                panel.Children.Add(new TextBlock
                {
                    Text = subtitle,
                    FontSize = 16,               // read at arm's length, not squinted at
                    Margin = new Thickness(0, 2, 0, 0),
                    MaxWidth = 460,              // room for 16px text before it has to trim
                    // Centred inside the panel as well as within itself - without this the 460-wide box
                    // would sit at the left edge on a menu wider than the text.
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextAlignment = TextAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Foreground = SubtitleBrush
                });
            return panel;
        }

        // "Ask AI to check this QSO", with AI in bold - the caption for the item that sends one QSO to
        // an AI for an opinion (see AiQsoCheck). Built here because three menus carry it now: the main
        // log's, the Log Workshop's and the Verify window's, and a caption written out three times is a
        // caption that ends up worded three ways.
        //
        // A TextBlock rather than a plain string, because only part of the line is bold. Everything else
        // - size, colour, spacing - is left to the menu item's own style, so it still looks like its
        // neighbours; the exception is the bold run itself.
        public static UIElement MakeAiHeader()
        {
            var text = new TextBlock();
            text.Inlines.Add(new System.Windows.Documents.Run("Ask "));
            text.Inlines.Add(new System.Windows.Documents.Run("AI") { FontWeight = FontWeights.Bold });
            text.Inlines.Add(new System.Windows.Documents.Run(" to check this QSO"));
            return text;
        }

        // The line under the callsign: which contact with that station this is. Only the parts actually
        // filled in, so a QSO with no mode logged doesn't show a stray separator.
        //
        // Date and time go through the very converters the log tables' Date and Time columns use, so the
        // menu reads 14-03-2019 / 07:32 exactly as the row behind it does - not the stored 20190314 /
        // 073210.
        public static string QsoSubtitle(QSO qso)
        {
            if (qso == null) return null;

            var parts = new List<string>();
            string date = new QsoDateDisplayConverter().Convert(qso.Date, typeof(string), null, CultureInfo.InvariantCulture) as string;
            string time = new QsoTimeDisplayConverter().Convert(qso.Time, typeof(string), null, CultureInfo.InvariantCulture) as string;
            if (!string.IsNullOrWhiteSpace(date)) parts.Add(date);
            if (!string.IsNullOrWhiteSpace(time)) parts.Add(time);
            if (!string.IsNullOrWhiteSpace(qso.Band)) parts.Add(qso.Band);
            if (!string.IsNullOrWhiteSpace(qso.Mode)) parts.Add(qso.Mode);
            return string.Join("  •  ", parts);
        }

        // A service checkbox. Disabled (and greyed with a hint) when that service isn't set up, so you
        // can't queue to a logger you don't use. The left indent lives on the grid below, not here.
        public static CheckBox MakeServiceCheck(string name, bool configured)
        {
            return new CheckBox
            {
                Content = configured ? name : name + "   (not configured)",
                IsEnabled = configured,
                Foreground = ItemTextBrush,
                FontSize = 16,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        // The four loggers as two rows of two rather than a four-item column: they are one choice, and
        // stacking them made the menu taller than the block of rows it was opened over.
        public static UIElement MakeServiceGrid(CheckBox lotw, CheckBox qrz, CheckBox eqsl, CheckBox club)
        {
            var grid = new Grid { Margin = new Thickness(40, 2, 8, 2) };   // 40 = the menu's text indent
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition());
            grid.RowDefinitions.Add(new RowDefinition());

            // Reading order is unchanged - LoTW, QRZ across the top, eQSL, Club Log below.
            Place(grid, lotw, 0, 0);
            Place(grid, qrz,  0, 1);
            Place(grid, eqsl, 1, 0);
            Place(grid, club, 1, 1);
            return grid;
        }

        private static void Place(Grid grid, CheckBox box, int row, int col)
        {
            // Auto columns line the second one up against the widest box in the first, so the gap is the
            // only thing that has to be set by hand.
            box.Margin = new Thickness(col == 0 ? 0 : 24, 3, 0, 3);
            Grid.SetRow(box, row);
            Grid.SetColumn(box, col);
            grid.Children.Add(box);
        }

        // A way out that can be seen. Esc and a click elsewhere have always closed these menus, but
        // neither says so, and a menu that has grown buttons and checkboxes no longer looks like
        // something a stray click should dismiss.
        public static Button MakeCloseButton(ContextMenu menu)
        {
            var close = new Button
            {
                Content = "Close",
                Width = 80,
                FontSize = 16,
                Padding = new Thickness(10, 4, 10, 4),
                Cursor = Cursors.Hand
            };
            close.Click += (s, e) => { if (menu != null) menu.IsOpen = false; };
            return close;
        }
    }
}
