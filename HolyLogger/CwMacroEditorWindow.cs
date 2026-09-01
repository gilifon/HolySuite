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
        private const int KeyerButtons = 12;
        private const int MsgButtons = 4;

        private readonly TextBox[] _labels = new TextBox[KeyerButtons];
        private readonly TextBox[] _run = new TextBox[KeyerButtons];
        private readonly TextBox[] _sp = new TextBox[KeyerButtons];

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
            // {MYCALL} TEST" is thirty characters; the two macro columns each hold about fifty at this
            // size. The window was a third wider than that and the empty half of every box showed it.
            Width = 860;
            MinWidth = 720;
            SetResourceReference(BackgroundProperty, "WindowBg");

            var stack = new StackPanel { Margin = new Thickness(16) };
            stack.Children.Add(BuildKeyerTable());
            stack.Children.Add(BuildMsgTable());
            stack.Children.Add(BuildQrlSection());
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

        private static TextBox Cell(string text, int column, int row)
        {
            var box = new TextBox
            {
                Text = text ?? string.Empty,
                FontSize = 16,
                Margin = new Thickness(0, 0, 10, 4),
                Padding = new Thickness(4, 2, 4, 2),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(box, column);
            Grid.SetRow(box, row);

            return box;
        }

        private static TextBlock SectionTitle(string text, string note, Thickness margin)
        {
            var block = new TextBlock
            {
                FontSize = 16,
                TextWrapping = TextWrapping.Wrap,
                Margin = margin
            };
            block.Inlines.Add(new System.Windows.Documents.Run(text) { FontWeight = FontWeights.Bold });
            block.Inlines.Add(new System.Windows.Documents.Run("   " + note));
            block.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");

            return block;
        }

        private UIElement BuildKeyerTable()
        {
            string[] labels = CwKeyboardWindow.ReadLabels(CwKeyboardWindow.KeyerLabelsSetting, KeyerButtons);
            string[] run = CwKeyboardWindow.LoadBank(false);
            string[] sp = CwKeyboardWindow.LoadBank(true);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                  // key
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });              // name
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            for (int r = 0; r <= KeyerButtons; r++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            grid.Children.Add(Heading("Key", 0, 0));
            grid.Children.Add(Heading("Button text", 1, 0));
            grid.Children.Add(Heading("Run macro", 2, 0));
            grid.Children.Add(Heading("S&P macro", 3, 0));

            for (int i = 0; i < KeyerButtons; i++)
            {
                int row = i + 1;
                grid.Children.Add(KeyName("F" + (i + 1), row));

                _labels[i] = Cell(labels[i], 1, row);
                _run[i] = Cell(run[i], 2, row);
                _sp[i] = Cell(sp[i], 3, row);

                grid.Children.Add(_labels[i]);
                grid.Children.Add(_run[i]);
                grid.Children.Add(_sp[i]);
            }

            var holder = new StackPanel();
            holder.Children.Add(SectionTitle("The CW keyer's twelve buttons",
                "F1 to F12 while the keyer is open. The button text is what is written on the keycap and is "
                + "the same in both banks; leave it empty and the macro itself shows, as it always did.",
                new Thickness(0, 0, 0, 10)));
            holder.Children.Add(grid);

            return holder;
        }

        private UIElement BuildMsgTable()
        {
            string[] labels = CwKeyboardWindow.ReadLabels(CwKeyboardWindow.MsgLabelsSetting, MsgButtons);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

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
                _msgTexts[i] = Cell(text, 2, row);

                grid.Children.Add(_msgLabels[i]);
                grid.Children.Add(_msgTexts[i]);
            }

            var holder = new StackPanel { Margin = new Thickness(0, 24, 0, 0) };
            holder.Children.Add(SectionTitle("The four Msg buttons on the main window",
                "F5 to F8 with the keyer closed. These are for ordinary working and have one text each, "
                + "not two.",
                new Thickness(0, 0, 0, 10)));
            holder.Children.Add(grid);

            return holder;
        }

        // THE NINTH TEXT. QRL? is not on any button, but it is a text the radio sends, and a man who
        // has come here to write what he sends should not have to go back to the settings window to
        // change it. The waiting time came with it: it is the same question, and splitting a question
        // across two windows is how a man ends up hunting for the half he wants.
        private UIElement BuildQrlSection()
        {
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

            var note = new TextBlock
            {
                Text = "Recommended: QRL?",
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0)
            };
            note.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(_qrlText);
            row.Children.Add(note);

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

            var holder = new StackPanel { Margin = new Thickness(0, 24, 0, 0) };
            holder.Children.Add(SectionTitle("Asking whether the frequency is free",
                "The CQ button sends this instead of calling, when the radio has moved.",
                new Thickness(0, 0, 0, 10)));
            holder.Children.Add(row);
            holder.Children.Add(minutesRow);

            return holder;
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

            // Save and Cancel in the MIDDLE, under the table they act on; the standard set stays out on
            // the left, because one is a thing done TO the table and the others end the window - and a
            // man reaching for Cancel should not find a button that rewrites eight rows under his hand.
            var panel = new Grid { Margin = new Thickness(0, 20, 0, 0) };
            var standardBtn = BuildStandardTextsButton();
            okCancel.HorizontalAlignment = HorizontalAlignment.Center;
            panel.Children.Add(standardBtn);
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

            for (int i = 0; i < KeyerButtons; i++)
            {
                labels[i] = (_labels[i].Text ?? string.Empty).Trim();
                run[i] = (_run[i].Text ?? string.Empty).Trim();
                sp[i] = (_sp[i].Text ?? string.Empty).Trim();
            }

            CwKeyboardWindow.SaveLabels(CwKeyboardWindow.KeyerLabelsSetting, labels, KeyerButtons);
            CwKeyboardWindow.SaveBank(run, false);
            CwKeyboardWindow.SaveBank(sp, true);

            var msgLabels = new string[MsgButtons];
            for (int i = 0; i < MsgButtons; i++)
            {
                msgLabels[i] = (_msgLabels[i].Text ?? string.Empty).Trim();

                try { if (_setMsgText != null) _setMsgText(i + 1, (_msgTexts[i].Text ?? string.Empty).Trim()); }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
            }

            CwKeyboardWindow.SaveLabels(CwKeyboardWindow.MsgLabelsSetting, msgLabels, MsgButtons);

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
