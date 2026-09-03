using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace HolyLogger
{
    public enum HolyMsgType { Info, Success, Warning, Error }

    public partial class HolyMessageBox : Window
    {
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern IntPtr SetActiveWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

        public bool Confirmed { get; private set; }

        // WHICH of up to three buttons was pressed: 1 = the Yes button, 2 = the extra button, 0 = No,
        // Escape, or the X. Confirmed stays exactly what it always was - true only for Yes - so no
        // caller that asks a plain question has to know this exists.
        public int Choice { get; private set; }

        // **BOLD** in a message becomes bold on screen. Menu paths and button names have to stand out
        // from the sentence around them - "select Tools > Log Workshop and press Log Verifier" is three
        // things to find in a program, buried in prose - and a message box that takes only a flat
        // string had no way to say so. Two asterisks either side, the convention everybody already
        // knows from chat and Markdown.
        //
        // A message with no asterisks is set as plain text exactly as before, so no existing caller
        // changes behaviour. An odd number of markers means somebody wrote a literal asterisk: the
        // trailing piece is added as plain text rather than swallowed.
        private void SetMessage(string message)
        {
            string text = message ?? string.Empty;
            if (text.IndexOf("**", StringComparison.Ordinal) < 0)
            {
                MessageText.Text = text;
                return;
            }

            MessageText.Text = string.Empty;
            MessageText.Inlines.Clear();
            AddMarkup(text);
        }

        // Appends text to whatever is already in the message, turning **...** into bold. Separate from
        // SetMessage so a caller can add a line AFTER the links it put below the message.
        private void AddMarkup(string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            int at = 0;
            bool bold = false;
            while (at < text.Length)
            {
                int mark = text.IndexOf("**", at, StringComparison.Ordinal);
                string piece = mark < 0 ? text.Substring(at) : text.Substring(at, mark - at);

                if (piece.Length > 0)
                {
                    var run = new System.Windows.Documents.Run(piece);
                    if (bold) run.FontWeight = FontWeights.Bold;
                    MessageText.Inlines.Add(run);
                }

                if (mark < 0) break;
                at = mark + 2;
                bold = !bold;
            }
        }

        private HolyMessageBox(string message, string title, HolyMsgType type, Window owner, bool confirm, double width = 0)
        {
            InitializeComponent();
            Title = title;
            SetMessage(message);
            // AN OWNER THAT HAS NOT BEEN SHOWN YET IS NOT AN OWNER. WPF throws "Cannot set Owner
            // property to a Window that has not been shown previously" - and a dialog raised from
            // inside MainWindow's constructor hits exactly that. It took HolyLogger down at startup on
            // an operator's machine: the HolyCluster UDP port was busy, the catch tried to warn him,
            // and the warning killed the program before it ever appeared. A message that cannot say
            // who owns it is still worth showing; being unable to start is not.
            if (owner != null)
            {
                try { Owner = owner; }
                catch (InvalidOperationException swallowed) { Log.Swallow(swallowed); }
            }
            if (width > 0) { Width = width; _widthWasChosen = true; }
            ApplyType(type);
            ChooseWidth();

            // EVERY WORD MUST BE READABLE, whatever the message turns out to say.
            //
            // These boxes are 460 wide and as tall as their text needs, which suited the one-line
            // messages they used to carry. They now carry the error itself and two or three lines of
            // advice under it, and a long one - a database error quoting a full path, say - made a
            // narrow ribbon of a window taller than the screen, with its own OK button below the
            // bottom edge. Sized here, once, when everything that is going into it is in.
            Loaded += (s, e) => CapHeight();

            // Remember where the user last placed any of these popups: restore that spot if it still
            // lands on a visible monitor, otherwise fall back to the XAML default (centered on owner).
            // The dialog is NoResize + SizeToContent, so only the position is persisted, not the size.
            var cfg = Properties.Settings.Default;
            if (IsPositionOnScreen(cfg.MsgBoxWindowLeft, cfg.MsgBoxWindowTop, Width, 160))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = cfg.MsgBoxWindowLeft;
                Top  = cfg.MsgBoxWindowTop;
            }

            // Save the position the moment this popup closes, so the next one opens in the same place.
            Closing += (s, e) => SavePosition();

            if (confirm)
            {
                OkBtn.Visibility = Visibility.Collapsed;
                ConfirmPanel.Visibility = Visibility.Visible;
            }

            // Esc must close the dialog even at STARTUP, when the app may not yet be the foreground
            // window. PreviewKeyDown (registered with handledEventsToo) tunnels from the window before
            // any child and fires even if a child marked the key handled — as long as keyboard focus is
            // somewhere inside this window. ContentRendered forces the window to the foreground and puts
            // focus on a button so that's guaranteed.
            AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(OnDialogPreviewKeyDown), true);

            // Raw Win32 hook: acts on Esc directly the moment WM_KEYDOWN reaches this dialog's HWND,
            // so it works even if WPF's input routing is being bypassed.
            SourceInitialized += (s, e) =>
            {
                var src = PresentationSource.FromVisual(this) as HwndSource;
                if (src != null) src.AddHook(WndHook);
            };

            ContentRendered += (s, e) =>
            {
                try
                {
                    ForceKeyboardFocus();
                    IInputElement btn = OkBtn.Visibility == Visibility.Visible ? (IInputElement)OkBtn : YesBtn;
                    Keyboard.Focus(btn);
                }
                catch (System.Exception swallowed) { Log.Swallow(swallowed); }
            };

            // If the dialog opens while another window (e.g. the embedded WebBrowser map) still holds
            // the Win32 keyboard focus, simply becoming the foreground window is NOT enough — physical
            // keystrokes go to GetFocus(), which stays null/elsewhere, so Esc is silently dropped even
            // though the dialog looks active. Re-assert real keyboard focus whenever we are activated.
            Activated += (s, e) => { try { ForceKeyboardFocus(); } catch (System.Exception swallowed) { Log.Swallow(swallowed); } };
        }

        // Force this dialog's HWND to actually own the Win32 keyboard focus. AttachThreadInput ties our
        // input queue to the current foreground thread so SetForegroundWindow/SetFocus are honoured even
        // when another window currently has focus; without this, GetFocus() can stay 0 and no key events
        // are delivered to the window.
        private void ForceKeyboardFocus()
        {
            Activate();
            IntPtr h = new WindowInteropHelper(this).Handle;
            if (h == IntPtr.Zero) return;

            IntPtr fg = GetForegroundWindow();
            uint myThread = GetCurrentThreadId();
            uint fgThread = fg != IntPtr.Zero ? GetWindowThreadProcessId(fg, out _) : myThread;

            bool attached = fgThread != myThread && AttachThreadInput(myThread, fgThread, true);
            try
            {
                SetForegroundWindow(h);
                SetActiveWindow(h);
                SetFocus(h);
            }
            finally
            {
                if (attached) AttachThreadInput(myThread, fgThread, false);
            }
        }

        // Win32 message hook on this dialog's window. WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104,
        // VK_ESCAPE = 0x1B. Closes the dialog the moment an Esc keystroke reaches this HWND.
        private IntPtr WndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == 0x0100 || msg == 0x0104)
            {
                int vk = (int)wParam;
                if (vk == 0x1B) // Esc — close directly from the raw message
                {
                    Confirmed = false;
                    handled = true;
                    Dispatcher.BeginInvoke(new Action(Close));
                }
            }
            return IntPtr.Zero;
        }

        private void OnDialogPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) { Confirmed = false; e.Handled = true; Close(); }
        }

        // Esc closes the dialog (also wired as Window.KeyDown in XAML). For confirm dialogs Esc = No.
        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                Confirmed = false;
                Close();
            }
        }

        private void ApplyType(HolyMsgType type)
        {
            switch (type)
            {
                case HolyMsgType.Success:
                    IconCircle.Fill = Brush("#34A853");
                    IconPath.Data = Geometry.Parse("M9 16.17L4.83 12l-1.42 1.41L9 19 21 7l-1.41-1.41z");
                    IconPath.Fill = Brushes.White;
                    break;
                case HolyMsgType.Warning:
                    IconCircle.Fill = Brush("#F9A825");
                    IconPath.Data = Geometry.Parse("M1 21h22L12 2 1 21zm12-3h-2v-2h2v2zm0-4h-2v-4h2v4z");
                    IconPath.Fill = Brushes.White;
                    break;
                case HolyMsgType.Error:
                    IconCircle.Fill = Brush("#D32F2F");
                    IconPath.Data = Geometry.Parse("M19 6.41L17.59 5 12 10.59 6.41 5 5 6.41 10.59 12 5 17.59 6.41 19 12 13.41 17.59 19 19 17.59 13.41 12z");
                    IconPath.Fill = Brushes.White;
                    break;
                default: // Info
                    IconCircle.Fill = Brush("#1565C0");
                    IconPath.Data = Geometry.Parse("M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm1 15h-2v-6h2v6zm0-8h-2V7h2v2z");
                    IconPath.Fill = Brushes.White;
                    IconCircle.Fill = Brushes.Transparent;
                    IconViewbox.Width = 44;
                    IconViewbox.Height = 44;
                    IconViewbox.Margin = new System.Windows.Thickness(0, 0, 0, 0);
                    break;
            }
        }

        private static SolidColorBrush Brush(string hex) =>
            new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));

        // Persist this popup's on-screen corner (shared by all HolyMessageBox popups).
        private void SavePosition()
        {
            if (double.IsNaN(Left) || double.IsNaN(Top) ||
                double.IsInfinity(Left) || double.IsInfinity(Top))
                return;
            Properties.Settings.Default.MsgBoxWindowLeft = Left;
            Properties.Settings.Default.MsgBoxWindowTop  = Top;
            try { Properties.Settings.Default.Save(); }
            catch (System.Exception swallowed) { Log.Swallow(swallowed); }
        }

        // True when a window at (left, top) would still be reachable on some monitor of the current
        // virtual desktop — so a position saved on a monitor that's since been removed/rearranged
        // doesn't strand the popup off-screen. NaN/Infinity (never-saved) returns false → use default.
        private static bool IsPositionOnScreen(double left, double top, double width, double height)
        {
            if (double.IsNaN(left) || double.IsNaN(top) ||
                double.IsInfinity(left) || double.IsInfinity(top))
                return false;

            double vsLeft   = SystemParameters.VirtualScreenLeft;
            double vsTop    = SystemParameters.VirtualScreenTop;
            double vsRight  = vsLeft + SystemParameters.VirtualScreenWidth;
            double vsBottom = vsTop  + SystemParameters.VirtualScreenHeight;

            return left >= vsLeft - 10 && top >= vsTop - 10 &&
                   left <= vsRight - 100 && top <= vsBottom - 60;
        }

        // Two columns: the label, then the count at the END of the row with every count ending on the
        // same edge. The number column is right-aligned and given a floor of its own so a run of
        // single digits does not sit hard against the words beside it.
        private static System.Windows.Controls.Grid BuildCounts(
            System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, int>> counts)
        {
            var table = new System.Windows.Controls.Grid();
            table.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });
            table.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });

            int row = 0;
            foreach (var entry in counts)
            {
                table.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });

                var label = new TextBlock
                {
                    Text = entry.Key,
                    FontSize = 18,
                    Margin = new Thickness(0, 0, 24, 3),
                };
                label.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
                System.Windows.Controls.Grid.SetRow(label, row);
                System.Windows.Controls.Grid.SetColumn(label, 0);
                table.Children.Add(label);

                var number = new TextBlock
                {
                    Text = entry.Value.ToString(System.Globalization.CultureInfo.CurrentCulture),
                    FontSize = 18,
                    FontWeight = FontWeights.Bold,
                    TextAlignment = TextAlignment.Right,
                    MinWidth = 34,
                    Margin = new Thickness(0, 0, 0, 3),
                };
                number.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
                System.Windows.Controls.Grid.SetRow(number, row);
                System.Windows.Controls.Grid.SetColumn(number, 1);
                table.Children.Add(number);

                row++;
            }

            return table;
        }

        private void OkBtn_Click(object sender, RoutedEventArgs e) => Close();
        private void YesBtn_Click(object sender, RoutedEventArgs e) { Confirmed = true; Choice = 1; Close(); }
        private void ExtraBtn_Click(object sender, RoutedEventArgs e) { Confirmed = false; Choice = 2; Close(); }
        private void NoBtn_Click(object sender, RoutedEventArgs e) { Confirmed = false; Choice = 0; Close(); }

        // ── Static helpers ────────────────────────────────────────────────

        public static void Show(string message, string title = "HolyLogger",
            HolyMsgType type = HolyMsgType.Info, Window owner = null, double width = 0)
        {
            new HolyMessageBox(message, title, type, owner, confirm: false, width).ShowDialog();
        }

        // width, like Show's: a question carrying a list - the changes in a new version, say - reads
        // as a wall of text at the default 460.
        // YES AND NO ARE THE ANSWERS TO A QUESTION; OK AND CANCEL ARE THE ANSWERS TO A STATEMENT.
        //
        // "Delete these three QSOs?" is a question and Yes/No is right. "6 QSOs will be checked by AI"
        // is not a question, and answering a statement with "Yes" reads as a mistake in the program.
        // So the two words can be named per dialog. Left unnamed they are Yes and No, which is what
        // every other confirm in the program already asks.
        //
        // yesLooksLikeButton: the Yes button PAINTED AS A BUTTON THE READER ALREADY KNOWS. A message
        // that says "press the yellow Create New Contest Log button" is asking somebody to go and find
        // a button; showing that same yellow button here, doing that same thing, is not asking anything.
        // The three colours are the button's own, passed in by the caller so this dialog does not have
        // to know about any particular window's palette.
        public static bool ShowConfirm(string message, string title = "HolyLogger",
            HolyMsgType type = HolyMsgType.Warning, Window owner = null, double width = 0,
            string yesText = null, string noText = null,
            string yesBackground = null, string yesForeground = null, string yesBorder = null)
        {
            var dlg = new HolyMessageBox(message, title, type, owner, confirm: true, width);

            if (!string.IsNullOrWhiteSpace(yesText)) dlg.YesBtn.Content = yesText;
            if (!string.IsNullOrWhiteSpace(noText)) dlg.NoBtn.Content = noText;

            PaintButton(dlg.YesBtn, yesBackground, yesForeground, yesBorder);
            dlg.FitWidthToButtons();   // the words just set may need more room than the text did

            dlg.ShowDialog();
            return dlg.Confirmed;
        }

        // A button wearing another button's colours. Named as strings ("#FFC107") so a caller can hand
        // over the exact three values that are written in its own XAML, and this dialog never has to
        // know about anybody's palette. Nothing happens when no background is named.
        private static void PaintButton(System.Windows.Controls.Button b, string background, string foreground, string border)
        {
            if (b == null || string.IsNullOrWhiteSpace(background)) return;

            var conv = new System.Windows.Media.BrushConverter();
            b.Background = (System.Windows.Media.Brush)conv.ConvertFromString(background);
            b.FontWeight = FontWeights.Bold;
            if (!string.IsNullOrWhiteSpace(foreground))
                b.Foreground = (System.Windows.Media.Brush)conv.ConvertFromString(foreground);
            if (!string.IsNullOrWhiteSpace(border))
            {
                b.BorderBrush = (System.Windows.Media.Brush)conv.ConvertFromString(border);
                b.BorderThickness = new Thickness(1.5);
            }
        }

        // THREE ANSWERS. Returns 1 for the first button, 2 for the middle one, 0 for the last one and
        // for Escape or the X - so "did they say no" is still one comparison, and the two positive
        // answers are told apart by number rather than by a second dialog.
        public static int ShowChoice(string message, string title, HolyMsgType type, Window owner,
            string yesText, string extraText, string noText,
            string yesBackground = null, string yesForeground = null, string yesBorder = null,
            string extraBackground = null, string extraForeground = null, string extraBorder = null,
            double width = 0)
        {
            var dlg = new HolyMessageBox(message, title, type, owner, confirm: true, width);

            if (!string.IsNullOrWhiteSpace(yesText)) dlg.YesBtn.Content = yesText;
            if (!string.IsNullOrWhiteSpace(noText)) dlg.NoBtn.Content = noText;
            if (!string.IsNullOrWhiteSpace(extraText))
            {
                dlg.ExtraBtn.Content = extraText;
                dlg.ExtraBtn.Visibility = Visibility.Visible;
            }

            PaintButton(dlg.YesBtn, yesBackground, yesForeground, yesBorder);
            PaintButton(dlg.ExtraBtn, extraBackground, extraForeground, extraBorder);

            // LEFT, AND LINED UP WITH THE TEXT - not centred, and not against the window's edge either.
            // Two buttons centred under a paragraph read as a pair belonging to it; three of different
            // widths centred read as a row that was dropped in and left where it fell. Putting the row
            // in the text's own column starts it on the same left edge every line above it starts on,
            // which is the line the eye is already following down the window.
            System.Windows.Controls.Grid.SetColumn(dlg.ConfirmPanel, 1);
            System.Windows.Controls.Grid.SetColumnSpan(dlg.ConfirmPanel, 1);
            dlg.ConfirmPanel.HorizontalAlignment = HorizontalAlignment.Left;

            dlg.FitWidthToButtons();
            dlg.ShowDialog();
            return dlg.Choice;
        }

        // True when the caller named a width itself. Its number is a deliberate choice about one
        // particular message and is never overruled by the general rule below.
        private bool _widthWasChosen;

        // ── A WINDOW BIG ENOUGH FOR WHAT IT HAS TO SAY ──────────────────────────────────────────
        //
        // TWO SIZES, DECIDED IN THIS ORDER.
        //
        // The WIDTH first, because the height depends on it: 460 is a comfortable measure for a
        // sentence or two and a poor one for fifteen lines, where the same text in a wider box is
        // shorter, squarer and quicker to read. Long messages are given more room before anything is
        // measured. A width the caller asked for is left exactly as asked.
        //
        // Then the HEIGHT, which is the one that can actually hide something. SizeToContent grows the
        // window until the text fits, with nothing stopping it at the edge of the monitor - and what
        // goes over that edge is the bottom of the window, which is where the OK button lives. So the
        // height is capped at what the screen can show, and the scroller in the XAML takes over from
        // there. The cap is only ever reached by a message far longer than any of ours.
        //
        // MEASURED, NOT GUESSED FROM THE CHARACTER COUNT. The text can carry line breaks, bold runs
        // and a list of file paths; WPF is asked what it actually needs.
        // The window's own furniture, in pixels, so the sums below are about the TEXT and not about
        // guessing: 24 of margin each side, the 56-wide icon column, and - for the height - the 20 of
        // top and bottom margin, the 16 spacer row and the 38 of button under it, plus the title bar.
        private const double SideFurniture = 24 + 24 + 56;
        private const double TallFurniture = 20 + 16 + 38 + 20 + 40;

        private void ChooseWidth()
        {
            try
            {
                if (MessageText == null) return;

                // ── WIDTH ────────────────────────────────────────────────────────────────────────
                //
                // 460 STAYS THE ANSWER UNTIL IT IS THE WRONG ONE. Every dialog in the program is that
                // width and has been for years; widening them all because the wording changed would be
                // a change nobody asked for. So the message is laid out at 460 first, and only a
                // message that comes out TALL is given more room - one that would otherwise be a narrow
                // ribbon of a window, which is both harder to read and the thing that runs off the
                // bottom of the screen.
                //
                // The text is measured on its own rather than by asking the TextBlock: inside a
                // scroller it has already been told how wide it may be, and it answers with that.
                if (_widthWasChosen) return;

                string plain = MessageText.Text ?? string.Empty;
                if (plain.Length == 0) return;

                double room = Math.Max(460, SystemParameters.WorkArea.Width - 160);
                foreach (double candidate in new[] { 460.0, 620.0, 760.0, 880.0 })
                {
                    if (candidate > room) break;
                    if (HeightAt(plain, candidate) <= 340 || candidate >= 880) { Width = candidate; break; }
                }
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // A WINDOW WIDE ENOUGH FOR ITS OWN BUTTONS. The width above is chosen for the TEXT, but a
        // confirm may name its buttons ("Add 4Z1KD to this log" / "Choose a log…") and two of those do
        // not fit across 460 - the reader was shown "Choose a lo". The buttons are measured for the
        // words they now carry and the window is widened to hold them, even past a width the caller
        // asked for: a choice that cannot be read is worse than a window wider than requested.
        internal void FitWidthToButtons()
        {
            try
            {
                if (YesBtn == null || NoBtn == null) return;

                var free = new Size(double.PositiveInfinity, double.PositiveInfinity);
                YesBtn.Measure(free);
                NoBtn.Measure(free);

                // The 10 between the buttons, the 24 of margin each side, and a little air. The third
                // button counts only when it is showing - and then so does the second 10px gap.
                double needed = YesBtn.DesiredSize.Width + NoBtn.DesiredSize.Width + 10 + 24 + 24 + 16;
                if (ExtraBtn != null && ExtraBtn.Visibility == Visibility.Visible)
                {
                    ExtraBtn.Measure(free);
                    needed += ExtraBtn.DesiredSize.Width + 10;
                }

                // The buttons moved into the text's column give up the 56 the icon column takes, so the
                // window has to be that much wider for the same row of words to fit.
                if (ConfirmPanel != null && System.Windows.Controls.Grid.GetColumn(ConfirmPanel) == 1)
                    needed += 56;
                if (needed <= Width) return;

                double room = Math.Max(460, SystemParameters.WorkArea.Width - 80);
                Width = Math.Min(needed, room);
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // How tall this message would be if the window were `windowWidth` across. Bold throughout, so a
        // message with bold in it is never measured short.
        private double HeightAt(string text, double windowWidth)
        {
            var drawn = new System.Windows.Media.FormattedText(
                text,
                System.Globalization.CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new System.Windows.Media.Typeface(MessageText.FontFamily, MessageText.FontStyle,
                                                  FontWeights.Bold, MessageText.FontStretch),
                MessageText.FontSize,
                System.Windows.Media.Brushes.Black,
                // Without this the text is measured as if the screen were at 100%. On a scaled
                // display the height came back wrong, so the "does it fit" test below was too.
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            drawn.MaxTextWidth = Math.Max(50, windowWidth - SideFurniture);
            return drawn.Height;
        }

        // Run when the window is up, because it needs the scroller that is in it. The width is
        // decided before that, in the constructor - it is a measurement of text and needs no window
        // at all, and doing it there was the fix for a real fault: at Loaded the assignment simply
        // did not take, and every message came out 460 wide however long its longest line was.
        private void CapHeight()
        {
            try
            {
                if (MessageScroller == null) return;

                // ── HEIGHT ───────────────────────────────────────────────────────────────────────
                //
                // THE CAP GOES ON THE SCROLLER, NOT ON THE WINDOW. A MaxHeight on the window only
                // CLIPS: the message row is sized to its content, so the scroller was handed all the
                // room it asked for, never scrolled, and the window then cut off whatever hung below
                // the limit - starting with its own OK button. Measured on a sixty-line message: the
                // text wanted 4,333 pixels, the viewport was given all 4,333, and the window was
                // chopped at 960. Capping the SCROLLER makes it scroll, and the window - which sizes
                // itself to its content - then comes out exactly as tall as the screen allows.
                double room = SystemParameters.WorkArea.Height - TallFurniture;
                if (room > 200) MessageScroller.MaxHeight = room;
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // ── WHAT TO DO ABOUT IT ─────────────────────────────────────────────────────────────────
        //
        // A message that says only what went wrong leaves the operator holding a fault and no next
        // move. These messages now end with one, and it is CHOSEN rather than offered to everybody.
        //
        // "Try again" is the honest answer to exactly one kind of failure: the log database being held
        // for a moment by something else - an upload writing a status, a backup reading the file.
        // SQLite says so in the error itself, in those words. Every other cause - a full disk, a file
        // the program may not write, a database that is damaged - is untouched by pressing the same
        // button a second time, and telling a man to press it wastes his time and leaves him no wiser.
        //
        // `pressAgain` is the button in HIS words - "press Add", "press Save" - so the sentence names
        // the thing in front of him rather than describing an action in general.
        internal static string WhatToDo(string error, string pressAgain)
        {
            string said = (error ?? string.Empty).ToLowerInvariant();
            string again = string.IsNullOrWhiteSpace(pressAgain) ? null : pressAgain.Trim();

            if (said.Contains("lock") || said.Contains("busy"))
                return "The log database is busy" + (again == null ? " — wait a moment and try once more."
                                                                   : " — wait a moment and " + again + " again.");

            return "Close HolyLogger and open it again"
                 + (again == null ? "." : ", then " + again + ".")
                 + " If it keeps happening, use Help → Support and paste the line above.";
        }

        public static void ShowSuccess(string message, string title = "HolyLogger", Window owner = null, double width = 0)
            => Show(message, title, HolyMsgType.Success, owner, width);

        public static void ShowError(string message, string title = "HolyLogger", Window owner = null, double width = 0)
            => Show(message, title, HolyMsgType.Error, owner, width);

        public static void ShowWarning(string message, string title = "HolyLogger", Window owner = null, double width = 0)
            => Show(message, title, HolyMsgType.Warning, owner, width);

        // A message with file paths written out at the end as LINKS the operator can click. A path is
        // worth printing in full - it can be read, copied, and pasted into an editor - but a path that
        // is only text makes the reader go and find the file by hand. Printed and clickable is both,
        // which is why there is no "open it" button beside OK: the path IS the button.
        //
        // links is caption -> path; onLink is given the path that was clicked, so the caller decides
        // how a file gets opened rather than this dialog deciding for it.
        public static void ShowWithLinks(string message, string title, HolyMsgType type, Window owner,
                                         System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, string>> links,
                                         Action<string> onLink, double width = 0, string footer = null,
                                         System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, int>> counts = null)
        {
            var dlg = new HolyMessageBox(message, title, type, owner, confirm: false, width);

            // A TALLY IS A TABLE, AND A TABLE CANNOT BE MADE OUT OF SPACES.
            //
            // Counts written into the message - "The AI thinks your log is correct:   4" - line up
            // only in a typewriter font, and nothing here is one. However many spaces go in front of
            // the number, each label is a different width on the screen and the numbers come out
            // scattered. So the tally is laid out as two real columns and put above the message: the
            // labels as wide as the widest of them, and the numbers ending on one edge.
            if (counts != null && counts.Count > 0)
            {
                var table = BuildCounts(counts);
                var holder = new System.Windows.Documents.InlineUIContainer(table);

                if (dlg.MessageText.Inlines.FirstInline != null)
                {
                    // TWO BREAKS: a blank line between the tally and the words under it. One left
                    // the sentence sitting against the bottom row of numbers, so the four of them
                    // read as a single block and the eye had nowhere to stop.
                    dlg.MessageText.Inlines.InsertBefore(dlg.MessageText.Inlines.FirstInline,
                                                         new System.Windows.Documents.LineBreak());
                    dlg.MessageText.Inlines.InsertBefore(dlg.MessageText.Inlines.FirstInline,
                                                         new System.Windows.Documents.LineBreak());
                    dlg.MessageText.Inlines.InsertBefore(dlg.MessageText.Inlines.FirstInline, holder);
                }
                else
                {
                    dlg.MessageText.Inlines.Add(holder);
                }
            }

            if (links != null)
                foreach (var entry in links)
                {
                    string path = entry.Value;
                    if (string.IsNullOrWhiteSpace(path)) continue;

                    dlg.MessageText.Inlines.Add(new System.Windows.Documents.LineBreak());
                    dlg.MessageText.Inlines.Add(new System.Windows.Documents.LineBreak());
                    if (!string.IsNullOrWhiteSpace(entry.Key))
                    {
                        dlg.MessageText.Inlines.Add(new System.Windows.Documents.Run(entry.Key));
                        dlg.MessageText.Inlines.Add(new System.Windows.Documents.LineBreak());
                    }

                    // A shade smaller than the message: a full path is long, and it is a reference
                    // rather than something to read at the same weight as the sentence above it.
                    var link = new System.Windows.Documents.Hyperlink(
                        new System.Windows.Documents.Run(path) { FontSize = 16 })
                    {
                        ToolTip = "Click to open this file",
                    };
                    link.Click += (s, e) =>
                    {
                        try { onLink?.Invoke(path); }
                        catch (System.Exception swallowed) { Log.Swallow(swallowed); }
                    };
                    dlg.MessageText.Inlines.Add(link);
                }

            // A closing line AFTER the links. The links are added here rather than being part of the
            // message, so anything meant to come below them cannot be written into the message string -
            // it would land above. Understands **bold** like the message does.
            if (!string.IsNullOrWhiteSpace(footer))
            {
                dlg.MessageText.Inlines.Add(new System.Windows.Documents.LineBreak());
                dlg.MessageText.Inlines.Add(new System.Windows.Documents.LineBreak());
                dlg.AddMarkup(footer);
            }

            // OK in the middle for this kind of message only. The rest of the program keeps it on the
            // right, where every dialog has always had it.
            dlg.OkBtn.HorizontalAlignment = HorizontalAlignment.Center;

            dlg.ShowDialog();
        }
    }
}
