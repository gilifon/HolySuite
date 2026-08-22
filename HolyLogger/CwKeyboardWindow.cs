using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace HolyLogger
{
    /// <summary>
    /// THE CW KEYBOARD: what is typed here goes out of the radio as it is typed.
    ///
    /// Its own window, and not the main one, for the same reason every contest logger does it that
    /// way: the entry form already owns every keystroke, and a keyboard that stole them would break
    /// logging.
    ///
    /// TWO ROWS, AND THE TEXT FALLS FROM ONE TO THE OTHER. The top row is what has NOT gone out yet;
    /// the row below is what HAS. A character is not copied down, it MOVES down, the moment it is
    /// handed to the radio - so the top row is always exactly the backlog, and the row below is the
    /// record of what was sent. That is also why nothing needs to defend the sent text from Backspace
    /// any more: it is not in the typing row to be deleted.
    ///
    /// HOW THE TEXT GETS OUT. The radio is keyed by CAT, and a CAT command carries a handful of
    /// characters at a time (Icom takes thirty, the KY radios about two dozen). So the typing is not
    /// sent letter by letter - that would flood the radio with commands - but in short chunks, PACED:
    /// after each chunk the window works out how long the radio will be busy keying it (the same
    /// PARIS timing the sending monitor uses, at the same self-learned speed) and holds the next one
    /// back until the radio is nearly done. The radio therefore always has a little in hand and never
    /// a backlog, which is what makes Escape stop the sending NOW instead of a paragraph from now.
    ///
    /// ONE TRANSMISSION, ONE LINE. What has gone does not run on for ever in a single blue line: a
    /// few seconds after the radio falls silent that line is finished with, and the next thing sent
    /// starts a new line at the top - which pushes the finished one, and everything under it, down.
    /// So the rows below the typing row read like the last few things said, newest first. How long
    /// the silence has to be before a line is finished is set at the gear; nought never finishes one,
    /// and everything stays on the single line it used to.
    ///
    /// Escape stops the radio and throws away the backlog - what has already gone stays in the row
    /// below, because it has gone. Enter does NOT close the window: the operator carries on typing
    /// the next thing. The window closes on the X, on Ctrl+K, or when the radio leaves CW.
    ///
    /// N1MM closes its window on Enter and opens a fresh empty one next time, so it needs no history
    /// and nothing to tidy. This one stays open, so the sent text has somewhere to go.
    /// </summary>
    public class CwKeyboardWindow : Window
    {
        // Hands one chunk of text to the radio. True when the CAT command went out.
        private readonly Func<string, bool> _sendChunk;

        // Aborts whatever the radio is keying.
        private readonly Action _stopSending;

        // The keying speed as the program currently understands it - it is learned from the canned
        // messages, so it improves as the operator uses them.
        private readonly Func<double> _currentWpm;

        // How much a single CAT command may carry. The radio's own buffer, not a guess.
        private readonly int _maxChunk;

        // The backlog being typed, and below it the record of what has gone.
        private readonly TextBox _box;
        private readonly TextBox _history;

        private readonly DispatcherTimer _pump;

        // When the radio is expected to finish what it has been given. The next chunk goes out
        // shortly before this, so the keying never stops in the middle of a word.
        private DateTime _radioBusyUntil = DateTime.MinValue;

        // How much work to keep in the radio's hands. Long enough to cover the gap between two
        // pumps, short enough that Escape still means "now".
        private static readonly TimeSpan Lead = TimeSpan.FromSeconds(0.6);

        // The line being added to now - what the radio is sending, or has just sent - and the finished
        // ones under it, newest first. They are held as text rather than read back off the screen so
        // that the row count can change without the record changing with it.
        private string _openLine = string.Empty;
        private readonly List<string> _finishedLines = new List<string>();

        // Further back than any row count can show, so raising the rows at the gear reveals lines that
        // were already there instead of empty space. A session that runs all weekend still stops here.
        private const int MaxFinishedLines = 60;

        // The blue of a character that has left. Fixed rather than themed: it has to mean the same
        // thing in every colour scheme, and it is legible on all of them.
        private static readonly Brush SentBrush = MakeSentBrush();

        private static Brush MakeSentBrush()
        {
            var brush = new SolidColorBrush(Color.FromRgb(0x1E, 0x90, 0xFF));
            brush.Freeze();
            return brush;
        }

        public CwKeyboardWindow(Func<string, bool> sendChunk, Action stopSending, Func<double> currentWpm, int maxChunk)
        {
            _sendChunk = sendChunk;
            _stopSending = stopSending;
            _currentWpm = currentWpm;
            _maxChunk = maxChunk < 4 ? 4 : maxChunk;

            Title = "CW Keyboard";

            // The window is as tall as its two rows, whatever the operator has set the history to -
            // change the number of history rows at the gear and the window grows or shrinks with it.
            SizeToContent = SizeToContent.Height;

            // THE WIDTH IS THE OPERATOR'S, the height is not. Dragging the sides makes the line longer
            // or shorter and WindowBounds remembers it; the height is worked out from the rows.
            Width = 560;
            MinWidth = 300;
            ResizeMode = ResizeMode.CanResize;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            // THE OS CAPTION IS REPLACED BY OUR OWN, only so the gear can sit in it - Windows will not
            // let anything of ours into its title bar. Same WindowChrome setup as the Cluster window:
            // CaptionHeight matches the bar built below, so dragging the bar still moves the window.
            WindowStyle = WindowStyle.None;
            SetResourceReference(BackgroundProperty, "WindowBg");
            System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome
            {
                CaptionHeight = 32,
                CornerRadius = new CornerRadius(0),
                GlassFrameThickness = new Thickness(0),
                ResizeBorderThickness = new Thickness(6),
                UseAeroCaptionButtons = false
            });

            var typeface = new FontFamily("Consolas");

            _box = new TextBox
            {
                FontSize = 20,
                FontFamily = typeface,
                CharacterCasing = CharacterCasing.Upper,
                Margin = new Thickness(10, 10, 10, 0),
                Padding = new Thickness(4, 2, 4, 2),
                AcceptsReturn = false,
                TextWrapping = TextWrapping.NoWrap
            };
            _box.PreviewKeyDown += Box_PreviewKeyDown;
            _box.PreviewTextInput += Box_PreviewTextInput;

            // WHAT HAS GONE, in blue and a size smaller. Read-only rather than merely disabled, so the
            // operator can still select a line of it and copy it out; never reached by Tab, because the
            // only thing in this window worth having the focus is the row being typed into.
            _history = new TextBox
            {
                FontSize = 16,
                FontFamily = typeface,
                Foreground = SentBrush,
                IsReadOnly = true,
                IsTabStop = false,

                // Real lines, so one transmission sits on one row and the rest are under it. Without
                // AcceptsReturn a read-only box shows the whole record run together on a single line.
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
                Margin = new Thickness(10, 6, 10, 10),
                Padding = new Thickness(4, 2, 4, 2)
            };
            ApplyHistoryRows();

            var body = new DockPanel { LastChildFill = false };

            var titleBar = BuildTitleBar();
            DockPanel.SetDock(titleBar, Dock.Top);
            DockPanel.SetDock(_box, Dock.Top);
            DockPanel.SetDock(_history, Dock.Top);

            body.Children.Add(titleBar);
            body.Children.Add(_box);
            body.Children.Add(_history);

            // WindowStyle.None takes the OS frame with it, so this border IS the window's visible edge.
            var frame = new Border { BorderThickness = new Thickness(1), Child = body };
            frame.SetResourceReference(Border.BorderBrushProperty, "TextBrush");

            Content = frame;
            Loaded += (s, e) => _box.Focus();

            _pump = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _pump.Tick += Pump_Tick;
            _pump.Start();

            // Where the operator put it is where it comes back. WindowBounds is the one that knows how
            // to bring a corner saved on a second screen back onto a screen that is still there.
            WindowBounds.Attach(this, "CwKeyboard");
        }

        // Only the characters a keyer can actually send. Anything else would be dropped by the radio
        // anyway, and dropping it here keeps the screen honest about what is going out.
        private static bool IsSendable(char c)
        {
            if (c == ' ') return true;
            if (c >= 'A' && c <= 'Z') return true;
            if (c >= 'a' && c <= 'z') return true;
            if (c >= '0' && c <= '9') return true;
            return ".,?/@=+-".IndexOf(c) >= 0;
        }

        private void Box_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            foreach (char c in e.Text)
            {
                if (!IsSendable(c)) { e.Handled = true; return; }
            }
        }

        private void Box_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // Ctrl+K again puts the window away - the key that opened it. The X in the corner does the
            // same thing; this is only so the operator's hands need not leave the keyboard.
            if (e.Key == Key.K && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                Close();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Escape)
            {
                StopEverything();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Enter)
            {
                // Swallowed on purpose. There is nothing for it to do - what is typed is already on
                // its way out - and it must NOT close the window: the operator keeps typing.
                e.Handled = true;
            }
        }

        private void Pump_Tick(object sender, EventArgs e)
        {
            string text = _box.Text ?? string.Empty;

            if (text.Length == 0)
            {
                FinishLineIfSilent();
                return;
            }

            // The radio still has enough to be going on with.
            if (DateTime.UtcNow + Lead < _radioBusyUntil) return;

            int take = Math.Min(_maxChunk, text.Length);

            // CUT AT A SPACE, not at the twelfth character. If the radio ever runs dry at a chunk
            // boundary the gap is heard, and a gap in the middle of a word breaks the word in two.
            // At a space the gap is expected anyway, so it costs nothing. Only when the chunk is
            // full and more text follows - a short tail being typed is sent as it stands.
            if (take == _maxChunk && take < text.Length)
            {
                int lastSpace = text.LastIndexOf(' ', take - 1, take);
                if (lastSpace > 0) take = lastSpace + 1;
            }

            string chunk = text.Substring(0, take);

            bool ok;
            try { ok = _sendChunk != null && _sendChunk(chunk); }
            catch (Exception swallowed) { Log.Swallow(swallowed); ok = false; }

            // A command that did not go out is not moved down: the same characters are offered again
            // on the next tick rather than being silently lost.
            if (!ok) return;

            MoveToHistory(chunk);

            double wpm = 20.0;
            try { if (_currentWpm != null) wpm = _currentWpm(); }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
            if (wpm < 5) wpm = 5;

            double seconds = CwSendMonitorWindow.ComputeTotalUnits(chunk) * 1.2 / wpm;
            DateTime from = _radioBusyUntil > DateTime.UtcNow ? _radioBusyUntil : DateTime.UtcNow;
            _radioBusyUntil = from.AddSeconds(seconds);
        }

        // The chunk leaves the typing row and joins the record below it. The caret comes back the same
        // number of characters, so text taken from in front of it does not move the cursor out from
        // under the operator's fingers mid-word.
        private void MoveToHistory(string chunk)
        {
            int caret = _box.CaretIndex;
            _box.Text = (_box.Text ?? string.Empty).Substring(chunk.Length);
            _box.CaretIndex = Math.Max(0, caret - chunk.Length);

            _openLine += chunk;
            RenderHistory();
        }

        // The radio has stopped and stayed stopped for as long as the operator asked for. The line
        // being added to is finished with: it drops into the list of finished ones at the top, which
        // pushes everything already there one row further down, and the next thing sent starts fresh.
        private void FinishLineIfSilent()
        {
            if (_openLine.Length == 0) return;

            int seconds = BreakSeconds();
            if (seconds <= 0) return;                       // nought: the line runs on for ever

            // _radioBusyUntil is when the radio is expected to stop keying what it was given. The
            // count starts there and not at the moment the characters were handed over, or a long
            // sentence would be broken up while it was still going out.
            if (DateTime.UtcNow < _radioBusyUntil.AddSeconds(seconds)) return;

            _finishedLines.Insert(0, _openLine);
            if (_finishedLines.Count > MaxFinishedLines) _finishedLines.RemoveRange(MaxFinishedLines, _finishedLines.Count - MaxFinishedLines);
            _openLine = string.Empty;
            RenderHistory();
        }

        // The open line on top, the finished ones under it, newest first. Redrawn whole rather than
        // appended to, because a line being finished moves every row on the screen.
        private void RenderHistory()
        {
            var text = new StringBuilder();
            text.Append(_openLine);

            foreach (string line in _finishedLines)
            {
                text.Append('\n');
                text.Append(line);
            }

            _history.Text = text.ToString();

            // Home, not End: the newest line is the top one, and that is the one to keep in view.
            _history.ScrollToHome();
        }

        // Escape: the radio stops and the backlog is thrown away. WHAT HAS ALREADY GONE STAYS in the
        // row below - it was sent, and a record that quietly loses the last thing it sent is worse
        // than no record at all.
        private void StopEverything()
        {
            try { if (_stopSending != null) _stopSending(); }
            catch (Exception swallowed) { Log.Swallow(swallowed); }

            _radioBusyUntil = DateTime.MinValue;
            _box.Text = string.Empty;
        }

        // How many rows of sent text are on show. Read from the setting every time rather than kept in
        // a field, so a change at the gear takes hold at once.
        // How long the radio must stay silent before the line being added to is finished with.
        private static int BreakSeconds()
        {
            try
            {
                int seconds = Properties.Settings.Default.CwKeyboardBreakSeconds;
                return seconds < 0 ? 0 : seconds;
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); return 3; }
        }

        private static int HistoryRows()
        {
            try
            {
                int rows = Properties.Settings.Default.CwKeyboardHistoryRows;
                if (rows < 1) rows = 1;
                if (rows > 10) rows = 10;
                return rows;
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); return 3; }
        }

        // MinLines and MaxLines together fix the row to exactly that many lines of its own font, which
        // is the one measurement that stays right when the font or the theme changes under it.
        private void ApplyHistoryRows()
        {
            int rows = HistoryRows();
            _history.MinLines = rows;
            _history.MaxLines = rows;
            _history.ScrollToHome();
        }

        // Our own caption: the title, then the gear, then the X. The gear is the way in to anything
        // this window ever needs asking about - today the history rows, tomorrow whatever else.
        private Border BuildTitleBar()
        {
            var gearBtn = new Button
            {
                Content = "",
                FontSize = 16,
                Style = Application.Current.Resources["CaptionButtonStyle"] as Style,
                ToolTip = "CW keyboard settings"
            };
            System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(gearBtn, true);
            gearBtn.Click += (s, e) => ShowSettings();

            var closeBtn = new Button
            {
                Content = "",
                Style = Application.Current.Resources["CaptionCloseButtonStyle"] as Style,
                ToolTip = "Close"
            };
            System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(closeBtn, true);
            closeBtn.Click += (s, e) => Close();

            var right = new StackPanel { Orientation = Orientation.Horizontal };
            DockPanel.SetDock(right, Dock.Right);
            right.Children.Add(gearBtn);
            right.Children.Add(closeBtn);

            var title = new TextBlock
            {
                Text = "CW Keyboard",
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0)
            };
            title.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            DockPanel.SetDock(title, Dock.Left);

            var bar = new DockPanel { LastChildFill = false };
            bar.Children.Add(right);
            bar.Children.Add(title);

            var border = new Border { Height = 32, Child = bar };
            border.SetResourceReference(Border.BackgroundProperty, "TitleBarBg");
            return border;
        }

        // WHAT THE GEAR OPENS. One question today. It is a window rather than a prompt box because
        // the next thing this keyboard needs asking about goes in here beside it, and a settings
        // window that already exists is easier to add a line to than one that has to be invented.
        private void ShowSettings()
        {
            var rowsBox = new TextBox
            {
                Text = HistoryRows().ToString(),
                FontSize = 16,
                Width = 70,
                Padding = new Thickness(4, 2, 4, 2),
                VerticalContentAlignment = VerticalAlignment.Center
            };

            var label = new TextBlock
            {
                Text = "Rows of sent text to show:",
                FontSize = 16,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");

            var hint = new TextBlock
            {
                Text = "1 to 10. The window grows and shrinks to match.",
                FontSize = 16,
                Margin = new Thickness(0, 10, 0, 0)
            };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");

            var row = new DockPanel { LastChildFill = false };
            DockPanel.SetDock(label, Dock.Left);
            DockPanel.SetDock(rowsBox, Dock.Left);
            row.Children.Add(label);
            row.Children.Add(rowsBox);

            var secondsBox = new TextBox
            {
                Text = BreakSeconds().ToString(),
                FontSize = 16,
                Width = 70,
                Padding = new Thickness(4, 2, 4, 2),
                VerticalContentAlignment = VerticalAlignment.Center
            };

            var secondsLabel = new TextBlock
            {
                Text = "Start a new line this many seconds after sending ended:",
                FontSize = 16,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            };
            secondsLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");

            var secondsHint = new TextBlock
            {
                Text = "0 never starts a new line - everything sent stays on one.",
                FontSize = 16,
                Margin = new Thickness(0, 10, 0, 0)
            };
            secondsHint.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");

            var secondsRow = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 18, 0, 0) };
            DockPanel.SetDock(secondsLabel, Dock.Left);
            DockPanel.SetDock(secondsBox, Dock.Left);
            secondsRow.Children.Add(secondsLabel);
            secondsRow.Children.Add(secondsBox);

            var okBtn = new Button { Content = "OK", FontSize = 16, Width = 90, Height = 32, IsDefault = true };
            var cancelBtn = new Button { Content = "Cancel", FontSize = 16, Width = 90, Height = 32, IsCancel = true, Margin = new Thickness(10, 0, 0, 0) };

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 18, 0, 0)
            };
            buttons.Children.Add(okBtn);
            buttons.Children.Add(cancelBtn);

            var stack = new StackPanel { Margin = new Thickness(16) };
            stack.Children.Add(row);
            stack.Children.Add(hint);
            stack.Children.Add(secondsRow);
            stack.Children.Add(secondsHint);
            stack.Children.Add(buttons);

            var dialog = new Window
            {
                Title = "CW Keyboard Settings",
                SizeToContent = SizeToContent.WidthAndHeight,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                Content = stack
            };
            dialog.SetResourceReference(BackgroundProperty, "WindowBg");

            okBtn.Click += (s, e) =>
            {
                // A number in range or nothing at all. Anything else leaves the setting as it was
                // rather than quietly picking a row count the operator did not ask for.
                if (!int.TryParse((rowsBox.Text ?? string.Empty).Trim(), out int rows) || rows < 1 || rows > 10)
                {
                    HolyMessageBox.ShowWarning("Type a whole number of rows, from 1 to 10.",
                                               "CW Keyboard Settings", dialog);
                    return;
                }

                if (!int.TryParse((secondsBox.Text ?? string.Empty).Trim(), out int seconds) || seconds < 0)
                {
                    HolyMessageBox.ShowWarning("Type a whole number of seconds, or 0 to never start a new line.",
                                               "CW Keyboard Settings", dialog);
                    return;
                }

                Properties.Settings.Default.CwKeyboardHistoryRows = rows;
                Properties.Settings.Default.CwKeyboardBreakSeconds = seconds;
                try { Properties.Settings.Default.Save(); }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
                ApplyHistoryRows();
                dialog.DialogResult = true;
            };

            dialog.ShowDialog();
            _box.Focus();
        }

        protected override void OnClosed(EventArgs e)
        {
            _pump.Stop();
            base.OnClosed(e);
        }
    }
}
