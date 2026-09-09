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

        private TextBox _qrlText;
        private TextBox _qrlMinutes;

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
            Width = 1184;
            MinWidth = 1184;
            MaxWidth = 1184;
            SetResourceReference(BackgroundProperty, "WindowBg");
            CwKeyboardWindow.UseBigTooltips(this);

            var stack = new StackPanel { Margin = new Thickness(16) };
            stack.Children.Add(BuildKeyerTable());
            stack.Children.Add(BuildMsgTable());
            stack.Children.Add(BuildButtons());

            // Grows to its content but never past the screen - the same rule the keyer's settings window
            // works by, and with twenty-eight rows in here it is not a theoretical one.
            Content = new ScrollViewer
            {
                Content = stack,
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
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
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
                offGrid.Children.Add(_offLabels[i]);
                offGrid.Children.Add(_off[i]);

                _labels[i] = Cell(labels[i], 0, row);
                _run[i] = Cell(run[i], 1, row, MacroLength);
                _sp[i] = Cell(sp[i], 2, row, MacroLength);
                runGrid.Children.Add(_labels[i]);
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

            // A ROW OF ITS OWN, ABOVE THE FRAMES, for the button that fills them. It used to sit at
            // the bottom of the whole window, a long way from the eight rows it rewrites; it is
            // right-justified to the Contest frame here because that frame - Run and S&P - is exactly
            // what it fills.
            row3.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            row3.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var standardBtn = (FrameworkElement)BuildStandardTextsButton();
            standardBtn.HorizontalAlignment = HorizontalAlignment.Right;
            standardBtn.Margin = new Thickness(0, 0, 0, 8);
            Grid.SetColumn(standardBtn, 4);
            Grid.SetRow(standardBtn, 0);
            row3.Children.Add(standardBtn);

            Grid.SetColumn(keys, 0);
            Grid.SetRow(keys, 1);
            row3.Children.Add(keys);

            var offFrame = SetFrame("General", offGrid);
            Grid.SetColumn(offFrame, 2);
            Grid.SetRow(offFrame, 1);
            row3.Children.Add(offFrame);

            var runFrame = SetFrame("Contest", runGrid);
            Grid.SetColumn(runFrame, 4);
            Grid.SetRow(runFrame, 1);
            runFrame.HorizontalAlignment = HorizontalAlignment.Right;
            row3.Children.Add(runFrame);

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
        // What the main window's four Msg keys have room for: 44 points wide, less their own padding.
        private const double MsgKeycapRoom = 36;

        private void HoldNameToTheKeycap(TextBox box)
        {
            CwKeyboardWindow.HoldToKeycap(box, CwKeyboardWindow.KeycapRoom, "Consolas", 16, this);
        }

        private UIElement BuildMsgTable()
        {
            string[] labels = CwKeyboardWindow.ReadLabels(CwKeyboardWindow.MsgLabelsSetting, MsgButtons);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // THE SAME NAME BOX AS THE TWELVE ABOVE, to the pixel - the keycap's own width, not a
            // guess. 150 left MyCall sitting in the left third of a box built for a much longer name.
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(NameBoxWidth) });

            // THE SAME MACRO BOX AS THE TWELVE ABOVE, to the pixel. It was a share of the window and
            // came out a different length from the boxes over it, which said the four take something
            // longer than the twelve - they do not: the same text goes to the same radio.
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(MacroBoxWidth) });

            for (int r = 0; r <= MsgButtons; r++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            grid.Children.Add(Heading("Key", 0, 0));
            grid.Children.Add(Heading("Button text", 1, 0));
            grid.Children.Add(Heading("Macro", 2, 0));

            for (int i = 0; i < MsgButtons; i++)
            {
                int row = i + 1;
                grid.Children.Add(KeyName("F" + (i + 5), row));

                string text = string.Empty;
                try { if (_getMsgText != null) text = _getMsgText(i + 1) ?? string.Empty; }
                catch (Exception swallowed) { Log.Swallow(swallowed); }

                _msgLabels[i] = Cell(labels[i], 1, row);

                // The four write at eleven point in the system font - see the label TextBlock in the
                // main window's XAML - so their names are measured against that and not against the
                // keyer's larger keys.
                CwKeyboardWindow.HoldToKeycap(_msgLabels[i], MsgKeycapRoom, null, 11, this);
                _msgTexts[i] = Cell(text, 2, row, MacroLength);

                // WHAT THE KEY SAYS TODAY, greyed out behind an empty box. These four have a name of
                // their own only if he gives them one; with none they fall back to Txt 1 to Txt 4 in CW
                // and Msg1 to Msg4 in SSB. An empty box beside a button plainly reading "Txt 1" looks
                // like the editor has lost something, and that is exactly how it was reported.
                grid.Children.Add(WithPlaceholder(_msgLabels[i], "Txt " + (i + 1)));
                grid.Children.Add(_msgTexts[i]);
            }

            // -- TWO BLOCKS, TWO GRIDS ---------------------------------------------------------
            //
            // The QRL question sits beside this table, and it USED TO SIT IN IT - same grid, another
            // column. That is what put every one of these rows in the wrong place: rows are shared
            // down a grid, so the QRL title wrapping onto two lines made the header row two lines
            // tall, and the headers, the four keys and their boxes were all shoved down by the height
            // of a sentence that has nothing to do with them.
            //
            // Side by side in a grid of their own, each block keeps its own rows. The title may wrap
            // to two lines or ten; the four macro rows never move.
            var both = new Grid();
            both.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            both.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });   // gap
            both.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(QrlWidth) });

            grid.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(grid, 0);
            both.Children.Add(grid);

            var qrl = BuildQrlSection();
            Grid.SetColumn(qrl, 2);
            both.Children.Add(qrl);

            var holder = new StackPanel { Margin = new Thickness(0, 24, 0, 0) };
            holder.Children.Add(SectionTitle("The 4 Macro buttons on the main window",
                "F5 to F8 with the keyer closed. These are for ordinary working and have one text each, "
                + "not two.",
                new Thickness(0, 0, 0, 10)));
            holder.Children.Add(both);

            return holder;
        }

        // MEASURED, NOT GUESSED: at this width the QRL sentence wraps onto exactly two lines. WPF's
        // own TextBlock was asked rather than a character count - it takes three lines under 446
        // points and sits on one above 846, so 680 is in the middle of the two-line band with room
        // on either side of it.
        private const double QrlWidth = 680;

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
            var block = new StackPanel
            {
                Width = QrlWidth,
                VerticalAlignment = VerticalAlignment.Top
            };

            block.Children.Add(SectionTitle("Asking if Frequency is free",
                "The CQ button sends this instead of CQ when the radio's frequency has moved by 150 Hz.",
                new Thickness(0, 0, 0, 10)));

            _qrlText = new TextBox
            {
                Text = CwKeyboardWindow.QrlText(),
                FontSize = 16,
                Width = 150,
                Padding = new Thickness(4, 2, 4, 2),
                CharacterCasing = CharacterCasing.Upper,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalContentAlignment = VerticalAlignment.Center
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

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(note);
            row.Children.Add(_qrlText);
            block.Children.Add(row);

            _qrlMinutes = new TextBox
            {
                Text = CwKeyboardWindow.QrlMinutes().ToString(CultureInfo.InvariantCulture),
                FontSize = 16,
                Width = 60,
                Padding = new Thickness(4, 2, 4, 2),
                VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 10, 0)
            };

            var minutesRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 10, 0, 0)
            };
            minutesRow.Children.Add(Words("Also ask after"));
            minutesRow.Children.Add(_qrlMinutes);
            minutesRow.Children.Add(Words("minutes on the same frequency. 0 never asks on time."));
            block.Children.Add(minutesRow);

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
                Properties.Settings.Default.CwKeyerQrlMinutes = minutes;
                Properties.Settings.Default.Save();
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }

            return true;
        }
    }
}
