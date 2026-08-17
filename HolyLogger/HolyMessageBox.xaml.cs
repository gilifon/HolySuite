using System;
using System.Runtime.InteropServices;
using System.Windows;
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
            if (owner != null) Owner = owner;
            if (width > 0) Width = width;
            ApplyType(type);

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

        private void OkBtn_Click(object sender, RoutedEventArgs e) => Close();
        private void YesBtn_Click(object sender, RoutedEventArgs e) { Confirmed = true; Close(); }
        private void NoBtn_Click(object sender, RoutedEventArgs e) { Confirmed = false; Close(); }

        // ── Static helpers ────────────────────────────────────────────────

        public static void Show(string message, string title = "HolyLogger",
            HolyMsgType type = HolyMsgType.Info, Window owner = null, double width = 0)
        {
            new HolyMessageBox(message, title, type, owner, confirm: false, width).ShowDialog();
        }

        // width, like Show's: a question carrying a list - the changes in a new version, say - reads
        // as a wall of text at the default 460.
        public static bool ShowConfirm(string message, string title = "HolyLogger",
            HolyMsgType type = HolyMsgType.Warning, Window owner = null, double width = 0)
        {
            var dlg = new HolyMessageBox(message, title, type, owner, confirm: true, width);
            dlg.ShowDialog();
            return dlg.Confirmed;
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
                                         Action<string> onLink, double width = 0, string footer = null)
        {
            var dlg = new HolyMessageBox(message, title, type, owner, confirm: false, width);

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
