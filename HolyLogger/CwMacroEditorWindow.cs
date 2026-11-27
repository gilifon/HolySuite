using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace HolyLogger
{
    /// <summary>
    /// EVERY MACRO IN ONE PLACE.
    ///
    /// They could always be written one at a time, by right-clicking the button - and they still can,
    /// which is the quickest way to fix one during a contest. What that never showed was the SET: which
    /// key holds what, whether the Run and Search-and-Pounce versions of a key still say the same kind
    /// of thing, and which of the twelve are still empty. This window is the set, laid out as it is
    /// used: one row per key.
    ///
    /// TWO TABLES, BECAUSE THEY ARE TWO DIFFERENT THINGS. The twelve keyer buttons are the contest set
    /// and have two versions of every text. The four Msg buttons on the main window are for ordinary
    /// working and have one each. Putting them in one grid would have meant two empty cells on every
    /// one of those four rows and a column heading that lied about them.
    ///
    /// THE NAME IS SHARED BY BOTH BANKS, and the macros are not - see RefreshButtonFace on the keyer
    /// for why. Nothing is written until OK, so a man can look through the lot and change his mind.
    /// </summary>
    internal class CwMacroEditorWindow : Window
    {
        // TAKEN FROM THE KEYER, not written out again. Two copies of the same twelve meant that
        // changing one of them made this window throw as it opened - the arrays it fills come
        // from the keyer and are the keyer's length.
        private const int KeyerButtons = CwKeyboardWindow.ButtonCount;
        private const int MsgButtons = 4;

        private readonly TextBox[] _labels = new TextBox[KeyerButtons];
        private readonly TextBox[] _run = new TextBox[KeyerButtons];
        private readonly TextBox[] _sp = new TextBox[KeyerButtons];

        // THE OFF SET: the everyday keyer's own twelve, with names of their own. Run and S&P are the
        // contest pair and share their names; this one is what the same twelve keys hold when the bar
        // says Off, and it has nothing to do with a contest exchange.
        private readonly TextBox[] _offLabels = new TextBox[KeyerButtons];
        private readonly TextBox[] _off = new TextBox[KeyerButtons];

        private readonly TextBox[] _msgLabels = new TextBox[MsgButtons];
        private readonly TextBox[] _msgTexts = new TextBox[MsgButtons];

        // The same four keys' Answer set - see BuildMsgTable.
        private readonly TextBox[] _ansLabels = new TextBox[MsgButtons];
        private readonly TextBox[] _ansTexts = new TextBox[MsgButtons];

        private TextBox _qrlText;
        private CheckBox _qrlOn;
        private TextBox _qrlMinutes;

        // The shortcuts frame on the right, and the macro box a click on a shortcut goes into.
        private const double ShortcutsWidth = 470;
        private TextBox _lastMacroBox;
        private FrameworkElement _generalFrame;

        private readonly Func<int, string> _getMsgText;
        private readonly Action<int, string> _setMsgText;

        internal CwMacroEditorWindow(Window owner, Func<int, string> getMsgText, Action<int, string> setMsgText)
        {
            _getMsgText = getMsgText;
            _setMsgText = setMsgText;

            Title = "CW Macros";
            Owner = owner;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SizeToContent = SizeToContent.Height;
            // WIDE ENOUGH FOR THE LONGEST MACRO ANYBODY WRITES, and no wider. "CQ TEST {MYCALL}
            // {MYCALL} TEST" is thirty characters, which is what a macro box holds at this size -
            // and there are four of them now: the everyday set's one, and the contest pair's two,
            // with a name box in front of each set. A box that showed half a macro was the first
            // thing the operator said about the new layout.
            // AND NEVER NARROWER THAN WHAT IS IN IT, counted from the layout rather than measured.
            //
            // Measuring was tried and it is wrong here: with SizeToContent the wrapping heading over
            // the table reports the width of its whole sentence UNWRAPPED, so the window came up as
            // wide as two screens side by side. The sum below is the one that matters and it is short:
            // the key column, the General frame, the gap, and the Contest frame, plus the window's own
            // margins.
            //
            //   key 40 + gap 6 + (96 + 280 + 20) + gap 12 + (96 + 280 + 280 + 20) + margins 50
            //
            // ONE WIDTH, AND IT IS NOT A CHOICE. Everything across this window is fixed - the key
            // column, four macro boxes, three name boxes and two frames - so there is exactly one
            // width that shows all of it, and nothing to be gained by dragging it wider: the boxes do
            // not grow, only the empty space between them does.
            //
            // The floor and the ceiling are the same number, so a size restored from an earlier
            // session cannot bring back a window spread across two screens. The height is still his,
            // and still remembered.
            //
            // PLUS THE SHORTCUTS FRAME on the right (his request, 2026-11-27: the list the one-message editor
            // shows was missing here): ShortcutsWidth more.
            Width = 1184 + ShortcutsWidth;
            MinWidth = 1184 + ShortcutsWidth;
            MaxWidth = 1184 + ShortcutsWidth;
            SetResourceReference(BackgroundProperty, "WindowBg");
            CwKeyboardWindow.UseBigTooltips(this);

            var stack = new StackPanel { Margin = new Thickness(16) };
            stack.Children.Add(BuildKeyerTable());
            stack.Children.Add(BuildMsgTable());
            stack.Children.Add(BuildButtons());

            // THE SHORTCUTS, CLICKED IN (his request, 2026-11-27). The same list and the same click as the
            // one-message editor (MainWindow.BuildCwMacroLegend): a click puts the shortcut into the macro
            // box he was last in, at its cursor. Only a macro box counts - a NAME box is written on the
            // keycap, and a shortcut there would show as "{CALL}" on the key.
            PreviewGotKeyboardFocus += (s, e) =>
            {
                var box = e.NewFocus as TextBox;
                if (box != null && box.MaxLength == MacroLength) _lastMacroBox = box;
            };
            var legend = MainWindow.BuildCwMacroLegend(token =>
            {
                if (_lastMacroBox == null) { System.Media.SystemSounds.Beep.Play(); return; }
                MainWindow.InsertCwMacroToken(_lastMacroBox, token);
            });
            var shortcuts = new StackPanel();
            var how = new TextBlock
            {
                Text = "Click one to put it in the macro box you are in.",
                FontSize = 16,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            };
            shortcuts.Children.Add(how);
            shortcuts.Children.Add(legend);
            var shortcutsFrame = SetFrame("Shortcuts", shortcuts);
            // CYAN INSIDE (his request, 2026-11-27): "it is not an editable area" - the white boxes are what he
            // types in, this is a list he only clicks. Black writing, so it reads on cyan in a dark scheme too.
            ((Border)((Grid)shortcutsFrame).Children[0]).Background = new SolidColorBrush(Color.FromRgb(0xB2, 0xEB, 0xF2));
            shortcuts.SetValue(System.Windows.Documents.TextElement.ForegroundProperty, Brushes.Black);
            shortcutsFrame.Width = ShortcutsWidth - 16;
            shortcutsFrame.VerticalAlignment = VerticalAlignment.Top;
            shortcutsFrame.Margin = new Thickness(0, 16, 16, 0);
            // ITS TOP ON THE GENERAL AND CONTEST FRAMES' TOP (his request, 2026-11-27), not on the heading
            // above them. Taken from where the General frame really lands, so it stays right whatever the
            // heading's wrapping makes its height.
            stack.LayoutUpdated += (s, e) =>
            {
                if (_generalFrame == null || !_generalFrame.IsVisible) return;
                double top = _generalFrame.TranslatePoint(new Point(0, 0), stack).Y + stack.Margin.Top;
                if (Math.Abs(shortcutsFrame.Margin.Top - top) > 0.5) shortcutsFrame.Margin = new Thickness(0, top, 16, 0);
            };

            // DOCKED, NOT A GRID. In a two-column Grid here the tables came out 2156 wide - the wrapping
            // heading over them stretched to its whole sentence and pushed the shortcuts off the window (his
            // screen, 2026-11-27). Docked on the right, the tables keep the 1136 they always had: measured on
            // the real window in a test program (build/uiharness/MacroWin.cs).
            var both = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(shortcutsFrame, Dock.Right);
            both.Children.Add(shortcutsFrame);
            both.Children.Add(stack);

            // Grows to its content but never past the screen - the same rule the keyer's settings window
            // works by, and with twenty-eight rows in here it is not a theoretical one.
            Content = new ScrollViewer
            {
                Content = both,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = Math.Max(400, SystemParameters.WorkArea.Height - 120)
            };

            WindowBounds.Attach(this, "CwMacroEditor");

            // AND ITS SIZE IS REMEMBERED AGAIN, as it is for every other window here. It was forced
            // to a fixed width for a while, which threw away a size the operator had set himself -
            // the cure for a two-screen window I had caused, and worse than the disease. The floor
            // above is what stops that happening: too narrow is refused, too wide is his business.
        }

        private static TextBlock Heading(string text, int column, int row)
        {
            var block = new TextBlock
            {
                Text = text,
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 10, 6),
                VerticalAlignment = VerticalAlignment.Center
            };
            block.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            Grid.SetColumn(block, column);
            Grid.SetRow(block, row);

            return block;
        }

        private static TextBlock KeyName(string text, int row)
        {
            var block = new TextBlock
            {
                Text = text,
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 10, 4),
                VerticalAlignment = VerticalAlignment.Center
            };
            block.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            Grid.SetColumn(block, 0);
            Grid.SetRow(block, row);

            return block;
        }

        // AS LONG AS THE KEYCAP, AND NO LONGER, for the name columns. A macro is as long as it needs
        // to be and is trimmed with an ellipsis on the face - it is read in the tooltip. A NAME that
        // does not fit is a name he cannot read at a glance, which is the only reason it exists.
        //
        // NOT COUNTED IN CHARACTERS ANY MORE. Ten was the keyer's cap and seven the main window's,
        // and a count can only be wrong in one direction or the other: ten M's do not fit on a key
        // that holds twelve I's. Every name box is MEASURED instead - the word he is typing, in the
        // font that keycap writes in - and refused at the letter that would not fit. See
        // CwKeyboardWindow.HoldToKeycap, which the right-click editor uses for the same job.
        //
        // The keyer's keys are about 77 points of writing at sixteen point; the four on the main
        // window are 36 at eleven.

        // AND THE SAME CEILING ON EVERY MACRO, the four and the twelve alike. Fifty is what one of
        // these boxes shows without scrolling, and it is past what any radio takes in one go anyway -
        // an Icom's CW command holds thirty characters, a Kenwood's twenty-four - so a message longer
        // than this is one the keyer has to break up regardless.
        private const int MacroLength = 50;

        private static TextBox Cell(string text, int column, int row, int maxLength = 0)
        {
            var box = new TextBox
            {
                Text = text ?? string.Empty,
                FontSize = 16,
                Margin = new Thickness(0, 0, 10, 4),
                Padding = new Thickness(4, 2, 4, 2),
                MaxLength = maxLength,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(box, column);
            Grid.SetRow(box, row);

            // A MACRO CELL IS HELD TO WHAT A KEYER CAN SEND, exactly as the single-text editor holds
            // its box - typing filtered, a paste cleaned rather than refused. A name is not: it is
            // written on the keycap and never goes near the radio, so it can say whatever he likes.
            if (maxLength == MacroLength) MainWindow.CwMacroText.Guard(box);

            return box;
        }

        // The hint sits BEHIND the box and takes no mouse, so clicking where it is puts the caret in the
        // box as though it were not there. It goes when anything is typed and comes back when the box is
        // emptied again - which is how he clears a name he no longer wants.
        private static UIElement WithPlaceholder(TextBox box, string placeholder)
        {
            var hint = new TextBlock
            {
                Text = placeholder,
                FontSize = 16,
                FontFamily = new FontFamily("Consolas"),
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                IsHitTestVisible = false,
                Foreground = Brushes.Gray
            };

            TextChangedEventHandler show = (s2, e2) =>
                hint.Visibility = string.IsNullOrEmpty(box.Text) ? Visibility.Visible : Visibility.Collapsed;
            box.TextChanged += show;
            show(box, null);

            var cell = new Grid();
            Grid.SetColumn(cell, Grid.GetColumn(box));
            Grid.SetRow(cell, Grid.GetRow(box));
            Grid.SetColumn(box, 0);
            Grid.SetRow(box, 0);

            cell.Children.Add(box);
            cell.Children.Add(hint);

            return cell;
        }

        // A NAME BOX DRESSED AS THE KEY IT NAMES (his request, 2026-11-27: "all the button text boxes to look like
        // the button on the keyer"). The keycap of MsgButtonCwStyle (Themes/Controls.xaml) drawn round the box:
        // the cyan face with its thin blue edge, the darker bottom edge of a real key, rounded corners - and
        // the name in the keyer's bold Consolas, centred, so what he types looks as it will on the key. Fixed
        // colours, like the keycap, which is one colour in every scheme. `content` is the box itself, or the
        // box with its placeholder; it hands its place in the grid (and the box its margin) to the keycap.
        private static readonly Brush KeyFaceBrush = FrozenBrush(Color.FromRgb(0x7F, 0xFE, 0xFF));
        private static readonly Brush KeyEdgeBrush = FrozenBrush(Color.FromRgb(0x00, 0xCC, 0xFF));
        private static readonly Brush KeyBottomBrush = FrozenBrush(Color.FromRgb(0x00, 0x99, 0xCC));

        private static Brush FrozenBrush(Color colour)
        {
            var brush = new SolidColorBrush(colour);
            brush.Freeze();
            return brush;
        }

        private static UIElement AsKeycap(UIElement content, TextBox box)
        {
            box.Background = Brushes.Transparent;
            box.BorderThickness = new Thickness(0);
            box.Foreground = Brushes.Black;
            box.FontFamily = new FontFamily("Consolas");
            box.FontWeight = FontWeights.Bold;
            box.FontSize = 16;
            box.TextAlignment = TextAlignment.Center;
            box.Padding = new Thickness(2, 0, 2, 0);
            Thickness place = box.Margin;
            box.Margin = new Thickness(0);

            var face = new Border
            {
                CornerRadius = new CornerRadius(5),
                BorderThickness = new Thickness(1),
                BorderBrush = KeyEdgeBrush,
                Background = KeyFaceBrush,
                SnapsToDevicePixels = true
            };
            var key = new Border
            {
                CornerRadius = new CornerRadius(5),
                Background = KeyBottomBrush,
                Padding = new Thickness(0, 0, 0, 3),
                Margin = place,
                SnapsToDevicePixels = true,
                Child = face
            };
            Grid.SetColumn(key, Grid.GetColumn(content));
            Grid.SetRow(key, Grid.GetRow(content));
            Grid.SetColumn(content, 0);
            Grid.SetRow(content, 0);
            face.Child = content;
            return key;
        }

        private static TextBlock SectionTitle(string text, string note, Thickness margin, params string[] boldInNote)
        {
            var block = new TextBlock
            {
                FontSize = 16,
                TextWrapping = TextWrapping.Wrap,
                Margin = margin
            };
            block.Inlines.Add(new System.Windows.Documents.Run(text) { FontWeight = FontWeights.Bold });

            // A WORD OR TWO OF THE NOTE CAN BE BOLD AS WELL - "Run" and "S&P" where they name the two
            // frames below, so the words on the page match the words on the frames rather than being
            // buried in a sentence they are the whole point of.
            if (boldInNote != null && boldInNote.Length > 0)
                AddNoteWithBoldWords(block, "   " + note, boldInNote);
            else
                block.Inlines.Add(new System.Windows.Documents.Run("   " + note));

            block.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");

            return block;
        }

        // Splits a note at each occurrence of the given words - taken in the order they appear in the
        // text, not the order they are listed - and bolds only those. Everything else keeps the note's
        // ordinary weight.
        private static void AddNoteWithBoldWords(TextBlock block, string note, string[] boldWords)
        {
            string remaining = note;

            while (remaining.Length > 0)
            {
                int bestAt = -1;
                string bestWord = null;

                foreach (string word in boldWords)
                {
                    int at = remaining.IndexOf(word, StringComparison.Ordinal);
                    if (at < 0) continue;
                    if (bestAt < 0 || at < bestAt) { bestAt = at; bestWord = word; }
                }

                if (bestAt < 0)
                {
                    block.Inlines.Add(new System.Windows.Documents.Run(remaining));
                    return;
                }

                if (bestAt > 0)
                    block.Inlines.Add(new System.Windows.Documents.Run(remaining.Substring(0, bestAt)));

                block.Inlines.Add(new System.Windows.Documents.Run(bestWord) { FontWeight = FontWeights.Bold });
                remaining = remaining.Substring(bestAt + bestWord.Length);
            }
        }

        private UIElement BuildKeyerTable()
        {
            string[] offLabels = CwKeyboardWindow.LoadBankLabels(CwKeyboardWindow.Bank.Off);
            string[] off = CwKeyboardWindow.LoadBank(CwKeyboardWindow.Bank.Off);
            string[] labels = CwKeyboardWindow.LoadBankLabels(CwKeyboardWindow.Bank.Run);
            string[] run = CwKeyboardWindow.LoadBank(CwKeyboardWindow.Bank.Run);
            string[] sp = CwKeyboardWindow.LoadBank(CwKeyboardWindow.Bank.Sp);

            // THE KEY COLUMN STAYS WHERE IT WAS, on the left, naming the row for all three sets at
            // once - F1 holds an Off text, a Run text and an S&P text, and they are one key.
            var keys = new Grid();
            keys.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // DROPPED BY THE SAME AMOUNT THE FRAMES ARE INSET FROM THEIR OWN TOP EDGE. "Key" and "F1"
            // sat flush with the top of the row, a frame's own height above "Button text" and its
            // first data row - the caption sits there, outside the border, and the key column had
            // nothing standing in that space to match it. Nothing else here moves; only this column
            // does.
            keys.Margin = new Thickness(0, FrameContentTop, 0, 0);
            for (int r = 0; r <= KeyerButtons; r++) keys.RowDefinitions.Add(new RowDefinition { Height = new GridLength(RowHeight) });
            keys.RowDefinitions[0].Height = GridLength.Auto;
            keys.Children.Add(Heading("Key", 0, 0));

            // THE EVERYDAY SET, in the room the contest pair gave up.
            var offGrid = new Grid();
            offGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(NameBoxWidth) });
            offGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(MacroBoxWidth) });
            for (int r = 0; r <= KeyerButtons; r++) offGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(RowHeight) });
            offGrid.RowDefinitions[0].Height = GridLength.Auto;
            offGrid.Children.Add(Heading("Button text", 0, 0));
            offGrid.Children.Add(Heading("Macro", 1, 0));

            // AND THE CONTEST PAIR, RIGHT-JUSTIFIED IN ITS OWN FRAME. Three columns that belong
            // together and are used together; the frame says so without a word.
            var runGrid = new Grid();
            runGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(NameBoxWidth) });
            runGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(MacroBoxWidth) });
            runGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(MacroBoxWidth) });
            for (int r = 0; r <= KeyerButtons; r++) runGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(RowHeight) });
            runGrid.RowDefinitions[0].Height = GridLength.Auto;
            runGrid.Children.Add(Heading("Button text", 0, 0));
            runGrid.Children.Add(Heading("Run macro", 1, 0));
            runGrid.Children.Add(Heading("S&P macro", 2, 0));

            for (int i = 0; i < KeyerButtons; i++)
            {
                int row = i + 1;
                keys.Children.Add(KeyName("F" + (i + 1), row));

                _offLabels[i] = Cell(offLabels[i], 0, row);
                _off[i] = Cell(off[i], 1, row, MacroLength);
                offGrid.Children.Add(AsKeycap(_offLabels[i], _offLabels[i]));
                offGrid.Children.Add(_off[i]);

                _labels[i] = Cell(labels[i], 0, row);
                _run[i] = Cell(run[i], 1, row, MacroLength);
                _sp[i] = Cell(sp[i], 2, row, MacroLength);
                runGrid.Children.Add(AsKeycap(_labels[i], _labels[i]));
                runGrid.Children.Add(_run[i]);
                runGrid.Children.Add(_sp[i]);

                // A NAME IS ONLY WORTH WHAT THE KEYCAP CAN SHOW - measured in the font the keycap
                // writes in, so the typing stops where the key does. See the keyer's own editor,
                // which holds its one name box the same way.
                HoldNameToTheKeycap(_offLabels[i]);
                HoldNameToTheKeycap(_labels[i]);
            }

            var row3 = new Grid();
            row3.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                    // key
            row3.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
            row3.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                    // Off
            row3.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row3.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                    // Run + S&P

            // THE BUTTON THAT FILLS THE CONTEST FRAME HANGS UNDER IT, right-justified to it - that
            // frame (Run and S&P) is exactly what it fills. It sat in a row of its own above the
            // frames, which cost the window a row (his request, 2026-11-19, moved it here).
            //
            // AND IT TAKES NO ROOM OF ITS OWN: it sits at the bottom of the Contest frame's cell and is
            // drawn 4 below it, so the next section comes up beside it - its heading is on the left,
            // this button on the right. ("Shift up what can be shifted", same day.)
            row3.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var standardBtn = (FrameworkElement)BuildStandardTextsButton();
            standardBtn.HorizontalAlignment = HorizontalAlignment.Right;
            standardBtn.VerticalAlignment = VerticalAlignment.Bottom;
            var hang = new TranslateTransform();
            standardBtn.RenderTransform = hang;
            standardBtn.SizeChanged += (s, e) => hang.Y = standardBtn.ActualHeight + 4;
            Grid.SetColumn(standardBtn, 4);
            Grid.SetRow(standardBtn, 0);

            Grid.SetColumn(keys, 0);
            Grid.SetRow(keys, 0);
            row3.Children.Add(keys);

            var offFrame = SetFrame("General", offGrid);
            _generalFrame = offFrame;
            Grid.SetColumn(offFrame, 2);
            Grid.SetRow(offFrame, 0);
            row3.Children.Add(offFrame);

            var runFrame = SetFrame("Contest", runGrid);
            Grid.SetColumn(runFrame, 4);
            Grid.SetRow(runFrame, 0);
            runFrame.HorizontalAlignment = HorizontalAlignment.Right;
            row3.Children.Add(runFrame);
            row3.Children.Add(standardBtn);

            var holder = new StackPanel();
            holder.Children.Add(SectionTitle("The CW keyer's twelve buttons",
                "F1 to F12 while the keyer is open. The left pair is what they hold with the bar on Gen - "
                + "everyday working, names and texts of its own. The framed three on the right are the "
                + "contest pair, Run and S&P, which share one set of names. A name left empty shows the "
                + "macro itself.",
                new Thickness(0, 0, 0, 10),
                "Run", "S&P"));
            holder.Children.Add(row3);

            return holder;
        }

        // Every row the same height in all three grids, so the key on the left lines up with the boxes
        // beside it however long a macro is.
        private const double RowHeight = 32;

        // A name box the width of the keycap it names, and a macro box that holds a whole macro:
        // thirty characters at this size, which is "CQ TEST {MYCALL} {MYCALL} TEST" and the longest
        // any of the standard texts runs to. It was narrower and cut them off; the room comes out of
        // the gap between the two frames, which had nothing in it.
        private const double NameBoxWidth = 96;
        private const double MacroBoxWidth = 280;

        // THE FOUR MSG BUTTONS' MACROS ARE LONGER - a whole QSO line, not a contest exchange. The
        // longest standard one, "{CALL} DE {MYCALL} R TNX UR {SENTRST} {SENTRST} BK", is 391 wide at
        // 16 and was cut off in a 280 box (his screenshot, 2026-11-19). Two of these sets side by side
        // still fit inside the width the keyer table fixes for the window.
        private const double MsgMacroBoxWidth = 420;

        // THE BLUE FRAME ROUND A SET, WITH ITS NAME CUT INTO THE TOP LINE - General and Contest -
        // the way a frame has named what it holds since forms were printed on paper. The name sat
        // under the line first and read as one more heading among the column headings.
        //
        // HOW THE LINE IS BROKEN: the frame is pushed down by half the name's height and the name is
        // laid over it, painted in the window's own background so the line simply stops on one side of
        // the word and starts again on the other. The background is a resource reference, not a
        // colour, or the gap would stay light after a switch to a dark scheme.
        // WHAT STANDS BETWEEN A FRAME'S OWN TOP EDGE AND THE HEADINGS INSIDE IT: the gap left for the
        // caption to sit in (FrameMarginTop), the border stroke itself, and the padding beyond it.
        // Named rather than left as three numbers inside SetFrame, because the Key column outside the
        // frames has to drop by exactly this much to have its own "Key" and "F1" line up with
        // "Button text" and the row under it - see BuildKeyerTable.
        private const double FrameMarginTop = 10;
        private const double FrameBorderTop = 2;
        private const double FramePaddingTop = 10;
        private const double FrameContentTop = FrameMarginTop + FrameBorderTop + FramePaddingTop;

        private static FrameworkElement SetFrame(string caption, UIElement inside)
        {
            var blue = new SolidColorBrush(Color.FromRgb(0x15, 0x65, 0xC0));

            var border = new Border
            {
                BorderBrush = blue,
                BorderThickness = new Thickness(FrameBorderTop),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, FramePaddingTop, 8, 2),
                Margin = new Thickness(0, FrameMarginTop, 0, 0),
                VerticalAlignment = VerticalAlignment.Top,
                Child = inside
            };

            var name = new TextBlock
            {
                Text = caption ?? string.Empty,
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Foreground = blue,
                Margin = new Thickness(14, 0, 0, 0),
                Padding = new Thickness(6, 0, 6, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top
            };
            name.SetResourceReference(TextBlock.BackgroundProperty, "WindowBg");

            var holder = new Grid { VerticalAlignment = VerticalAlignment.Top };
            holder.Children.Add(border);
            holder.Children.Add(name);

            return holder;
        }

        // The keyer's keycaps write at sixteen point in Consolas and hold about this much; the typing
        // stops where they do - see CwKeyboardWindow.HoldToKeycap, which both editors use.
        // What the main window's four Msg keys have room for: 48 points wide, less their own border and margin.
        // 48 wide since 2026-11-18, less 1 + 1 border and 4 + 4 margin - and the names are BOLD there.
        private const double MsgKeycapRoom = 38;

        private void HoldNameToTheKeycap(TextBox box)
        {
            CwKeyboardWindow.HoldToKeycap(box, CwKeyboardWindow.KeycapRoom, "Consolas", 16, this);
        }

        // -- THE FOUR MSG BUTTONS: TWO SETS SIDE BY SIDE (his request, 2026-11-18) ---------------
        //
        // The same four keys hold one set for calling CQ and one for answering somebody else's CQ;
        // the CQ / ANS button on the main window's top bar picks which one they send. Laid out like
        // the keyer's table above: the key column on the left, then one blue frame per set. The QRL
        // question belongs to the CQ set and sits under the two frames.
        private UIElement BuildMsgTable()
        {
            string[] cqLabels = CwKeyboardWindow.ReadLabels(CwKeyboardWindow.MsgLabelsSetting, MsgButtons);
            string[] ansLabels = CwKeyboardWindow.ReadLabels(CwKeyboardWindow.AnsMsgLabelsSetting, MsgButtons);
            string[] ansTexts = CwKeyboardWindow.LoadAnsMsgTexts();

            var keys = new Grid { Margin = new Thickness(0, FrameContentTop, 0, 0) };
            keys.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            for (int r = 0; r <= MsgButtons; r++) keys.RowDefinitions.Add(new RowDefinition { Height = new GridLength(RowHeight) });
            keys.RowDefinitions[0].Height = GridLength.Auto;
            keys.Children.Add(Heading("Key", 0, 0));

            Grid cqGrid = NewMsgSetGrid();
            Grid ansGrid = NewMsgSetGrid();

            for (int i = 0; i < MsgButtons; i++)
            {
                int row = i + 1;
                keys.Children.Add(KeyName("F" + (i + 5), row));

                string text = string.Empty;
                try { if (_getMsgText != null) text = _getMsgText(i + 1) ?? string.Empty; }
                catch (Exception swallowed) { Log.Swallow(swallowed); }

                _msgLabels[i] = Cell(cqLabels[i], 0, row);
                _msgTexts[i] = Cell(text, 1, row, MacroLength);
                _ansLabels[i] = Cell(ansLabels[i], 0, row);
                _ansTexts[i] = Cell(ansTexts[i], 1, row, MacroLength);

                // The four write at eleven point in the system font - see the label TextBlock in the
                // main window's XAML - so their names are measured against that and not against the
                // keyer's larger keys.
                CwKeyboardWindow.HoldToKeycap(_msgLabels[i], MsgKeycapRoom, null, 11, this, bold: true);
                CwKeyboardWindow.HoldToKeycap(_ansLabels[i], MsgKeycapRoom, null, 11, this, bold: true);

                // WHAT THE KEY SAYS TODAY, greyed out behind an empty name box.
                cqGrid.Children.Add(AsKeycap(WithPlaceholder(_msgLabels[i], "Txt " + (i + 1)), _msgLabels[i]));
                cqGrid.Children.Add(_msgTexts[i]);
                ansGrid.Children.Add(AsKeycap(WithPlaceholder(_ansLabels[i], "Txt " + (i + 1)), _ansLabels[i]));
                ansGrid.Children.Add(_ansTexts[i]);
            }

            var sets = new Grid();
            sets.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // key
            sets.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
            sets.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // CQ
            sets.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            sets.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // Answer

            Grid.SetColumn(keys, 0);
            sets.Children.Add(keys);

            var cqFrame = SetFrame("When you call CQ", cqGrid);
            Grid.SetColumn(cqFrame, 2);
            sets.Children.Add(cqFrame);

            var ansFrame = SetFrame("When you answer a CQ", ansGrid);
            Grid.SetColumn(ansFrame, 4);
            sets.Children.Add(ansFrame);

            var holder = new StackPanel { Margin = new Thickness(0, 24, 0, 0) };
            holder.Children.Add(SectionTitle("The 4 Macro buttons on the main window",
                // "Two sets" starts the second line (his request, 2026-11-19).
                "F5 to F8 with the keyer closed.\nTwo sets: the CQ / ANS button on the top bar of the "
                + "main window picks which one they send.",
                new Thickness(0, 0, 0, 10),
                "CQ / ANS"));
            holder.Children.Add(sets);

            // THE QRL QUESTION, UNDER THE TWO SETS. It sat beside the one set before; the second set
            // took that room. It is the CQ set's question, so it follows that frame.
            var qrl = (FrameworkElement)BuildQrlSection();
            qrl.HorizontalAlignment = HorizontalAlignment.Left;
            qrl.Margin = new Thickness(0, 20, 0, 0);
            holder.Children.Add(qrl);

            return holder;
        }

        private static Grid NewMsgSetGrid()
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(NameBoxWidth) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(MsgMacroBoxWidth) });
            for (int r = 0; r <= MsgButtons; r++) grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(RowHeight) });
            grid.RowDefinitions[0].Height = GridLength.Auto;
            grid.Children.Add(Heading("Button text", 0, 0));
            grid.Children.Add(Heading("Macro", 1, 0));
            return grid;
        }


        // THE NINTH TEXT. QRL? is not on any button, but it is a text the radio sends, and a man who
        // has come here to write what he sends should not have to go back to the settings window to
        // change it. The waiting time came with it: it is the same question, and splitting a question
        // across two windows is how a man ends up hunting for the half he wants.
        //
        // A BLOCK OF ITS OWN, beside the four Macro buttons rather than inside their table. Its title
        // wraps onto two lines and the two lines that follow sit straight under it - nothing here can
        // move a row of the table beside it, and nothing there can move these.
        private UIElement BuildQrlSection()
        {
            // NO FIXED WIDTH SINCE IT MOVED UNDER THE TWO SETS (2026-11-18): it has the window's whole
            // width now, and the text box and the minutes sit on one row (his request).
            var block = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Top
            };

            block.Children.Add(SectionTitle("Asking if Frequency is free",
                "The CQ button sends this instead of CQ when the radio's frequency has moved by 150 Hz.",
                new Thickness(0, 0, 0, 10)));

            _qrlText = new TextBox
            {
                Text = CwKeyboardWindow.QrlText(),
                FontSize = 16,
                Width = 80,     // "QRL?" with room to spare; was 150 (his request, 2026-11-19)
                Padding = new Thickness(4, 2, 4, 2),
                CharacterCasing = CharacterCasing.Upper,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalContentAlignment = VerticalAlignment.Center,
                // THE SAME RED THE CQ KEY WEARS while its next press would send this (his request,
                // 2026-11-19), so the box and the key it changes are plainly one thing.
                Background = CwKeyboardWindow.QrlWarningBrush,
                Foreground = Brushes.Black,
                ToolTip = "The CQ button sends this first, to ask if the frequency is free."
            };

            // "RECOMMENDED" FIRST, THE BOX SECOND - it reads as a sentence that way round rather than
            // as a label glued to the wrong side of the thing it names.
            var note = new TextBlock
            {
                Text = "Recommended: QRL?",
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0)
            };
            note.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");

            // THE TICK FIRST: on or off, with nothing to type (his request, 2026-11-19). Off greys
            // out the rest of the row - none of it does anything then.
            _qrlOn = new CheckBox
            {
                Content = "Ask",
                FontSize = 16,
                IsChecked = CwKeyboardWindow.QrlOn,
                VerticalAlignment = VerticalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 24, 0),
                ToolTip = "Ticked: the CQ button asks if the frequency is free before calling CQ.\n"
                        + "Not ticked: it never asks - it always sends CQ."
            };

            var asking = new StackPanel { Orientation = Orientation.Horizontal };

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(_qrlOn);
            row.Children.Add(asking);
            asking.Children.Add(note);
            asking.Children.Add(_qrlText);
            block.Children.Add(row);   // the minutes join this same row below

            _qrlMinutes = new TextBox
            {
                Text = CwKeyboardWindow.QrlMinutes().ToString(CultureInfo.InvariantCulture),
                FontSize = 16,
                Width = 44,     // three digits; was 60
                Padding = new Thickness(4, 2, 4, 2),
                VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 10, 0)
            };

            var minutesRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(24, 0, 0, 0)
            };
            minutesRow.Children.Add(Words("Also ask after"));
            minutesRow.Children.Add(_qrlMinutes);
            minutesRow.Children.Add(Words("minutes on the same frequency. 0 never asks on time."));
            asking.Children.Add(minutesRow);

            asking.IsEnabled = _qrlOn.IsChecked == true;
            _qrlOn.Checked += (s, e) => asking.IsEnabled = true;
            _qrlOn.Unchecked += (s, e) => asking.IsEnabled = false;

            return block;
        }

        private static TextBlock Words(string text)
        {
            var block = new TextBlock
            {
                Text = text,
                FontSize = 16,
                VerticalAlignment = VerticalAlignment.Center
            };
            block.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");

            return block;
        }

        // THE STANDARD SET, in one press - what most CW contesters have on F1 to F8: the Run macros,
        // the Search-and-Pounce ones, and the name on each key. It used to sit in the settings window,
        // a long way from the texts it rewrites; here it fills the boxes in front of him and NOTHING IS
        // SAVED UNTIL SAVE, so Cancel is the undo it never had.
        //
        // F9 TO F12 ARE LEFT ALONE. There is no standard for them - they are where a man puts the
        // things only he sends - and writing over them would be the program having an opinion about
        // his station.
        private UIElement BuildStandardTextsButton()
        {
            var button = new Button
            {
                Content = "Use the standard contest texts",
                FontSize = 16,
                // Green, his choice (2026-11-19) - the green the Log Fixer and Duplicates use.
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                Background = new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x1B, 0x5E, 0x20)),
                BorderThickness = new Thickness(1),
                Cursor = System.Windows.Input.Cursors.Hand,
                Padding = new Thickness(12, 4, 12, 4),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
                ToolTip = "Fills F1 to F8: the Run macros, the S&P macros, and the names on the keys."
            };

            button.Click += (s, e) =>
            {
                var said = new StringBuilder();
                said.Append("This fills F1 to F8 with the standard contest set:\n\n");
                said.Append("the Run macros, the S&P macros, and the names on the keys.\n\n");
                said.Append("F9 to F12 are left alone. Whatever those eight rows hold now is replaced - ");
                said.Append("and nothing is saved until you press Save.");

                if (!HolyMessageBox.ShowConfirm(said.ToString(), "CW Macros", HolyMsgType.Warning, this, 620,
                                                "Write them", "Leave mine")) return;

                Fill(_labels, CwKeyboardWindow.StandardLabels);
                Fill(_run, CwKeyboardWindow.StandardTexts);
                Fill(_sp, CwKeyboardWindow.StandardSpTexts);
            };

            return button;
        }

        private static void Fill(TextBox[] boxes, string[] texts)
        {
            for (int i = 0; i < boxes.Length && i < texts.Length; i++) boxes[i].Text = texts[i];
        }

        private UIElement BuildButtons()
        {
            // SAVE, not OK. Nothing in this window is written until it is pressed, and "Save" says that
            // where "OK" only agrees with something.
            var ok = new Button { Content = "Save", FontSize = 16, Width = 90, Height = 32, IsDefault = true };
            var cancel = new Button
            {
                Content = "Cancel",
                FontSize = 16,
                Width = 90,
                Height = 32,
                IsCancel = true,
                Margin = new Thickness(10, 0, 0, 0)
            };

            ok.Click += (s, e) => { if (Save()) DialogResult = true; };

            var okCancel = new StackPanel { Orientation = Orientation.Horizontal };
            okCancel.Children.Add(ok);
            okCancel.Children.Add(cancel);

            // SAVE AND CANCEL, CENTRED. The standard-texts button no longer lives down here - it sits
            // above the Contest frame it fills, in BuildKeyerTable - so this row holds only the two
            // that end the window.
            var panel = new Grid { Margin = new Thickness(0, 20, 0, 0) };
            okCancel.HorizontalAlignment = HorizontalAlignment.Center;
            panel.Children.Add(okCancel);

            return panel;
        }

        private bool Save()
        {
            if (!int.TryParse((_qrlMinutes.Text ?? string.Empty).Trim(), out int minutes) || minutes < 0)
            {
                HolyMessageBox.ShowWarning("Type a whole number of minutes, or 0 to never ask on time.",
                                           "CW Macros", this);
                _qrlMinutes.Focus();
                _qrlMinutes.SelectAll();
                return false;
            }

            var labels = new string[KeyerButtons];
            var run = new string[KeyerButtons];
            var sp = new string[KeyerButtons];
            var offLabels = new string[KeyerButtons];
            var off = new string[KeyerButtons];

            for (int i = 0; i < KeyerButtons; i++)
            {
                labels[i] = (_labels[i].Text ?? string.Empty).Trim();
                run[i] = (_run[i].Text ?? string.Empty).Trim();
                sp[i] = (_sp[i].Text ?? string.Empty).Trim();
                offLabels[i] = (_offLabels[i].Text ?? string.Empty).Trim();
                off[i] = (_off[i].Text ?? string.Empty).Trim();
            }

            CwKeyboardWindow.SaveLabels(CwKeyboardWindow.KeyerLabelsSetting, labels, KeyerButtons);
            CwKeyboardWindow.SaveBank(run, CwKeyboardWindow.Bank.Run);
            CwKeyboardWindow.SaveBank(sp, CwKeyboardWindow.Bank.Sp);

            CwKeyboardWindow.SaveLabels(CwKeyboardWindow.LabelsSettingName(CwKeyboardWindow.Bank.Off),
                                        offLabels, KeyerButtons);
            CwKeyboardWindow.SaveBank(off, CwKeyboardWindow.Bank.Off);

            // THE NAMES ARE WRITTEN FIRST, and this is not tidiness. Handing a text to the main window
            // makes it redraw that button's face, and the face reads the name out of the setting - so
            // saving the names afterwards left every one of the four showing the name it had BEFORE the
            // editor was opened, which is exactly what the operator reported.
            // THE ANSWER SET FIRST: the CQ set's texts below redraw the main window's four faces, and
            // the faces may be showing this set.
            var ansLabels = new string[MsgButtons];
            var ansTexts = new string[MsgButtons];
            for (int i = 0; i < MsgButtons; i++)
            {
                ansLabels[i] = (_ansLabels[i].Text ?? string.Empty).Trim();
                ansTexts[i] = (_ansTexts[i].Text ?? string.Empty).Trim();
            }
            CwKeyboardWindow.SaveLabels(CwKeyboardWindow.AnsMsgLabelsSetting, ansLabels, MsgButtons);
            CwKeyboardWindow.SaveAnsMsgTexts(ansTexts);

            var msgLabels = new string[MsgButtons];
            for (int i = 0; i < MsgButtons; i++)
                msgLabels[i] = (_msgLabels[i].Text ?? string.Empty).Trim();

            CwKeyboardWindow.SaveLabels(CwKeyboardWindow.MsgLabelsSetting, msgLabels, MsgButtons);

            for (int i = 0; i < MsgButtons; i++)
            {
                try { if (_setMsgText != null) _setMsgText(i + 1, (_msgTexts[i].Text ?? string.Empty).Trim()); }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
            }

            try
            {
                Properties.Settings.Default.CwKeyerQrlText = (_qrlText.Text ?? string.Empty).Trim();
                Properties.Settings.Default.CwKeyerQrlOn = _qrlOn.IsChecked == true;
                Properties.Settings.Default.CwKeyerQrlMinutes = minutes;
                Properties.Settings.Default.Save();
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }

            return true;
        }
    }
}
