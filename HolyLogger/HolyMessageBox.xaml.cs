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

        private HolyMessageBox(string message, string title, HolyMsgType type, Window owner, bool confirm, double width = 0)
        {
            InitializeComponent();
            Title = title;
            MessageText.Text = message;
            if (owner != null) Owner = owner;
            if (width > 0) Width = width;
            ApplyType(type);

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
                catch { }
            };

            // If the dialog opens while another window (e.g. the embedded WebBrowser map) still holds
            // the Win32 keyboard focus, simply becoming the foreground window is NOT enough — physical
            // keystrokes go to GetFocus(), which stays null/elsewhere, so Esc is silently dropped even
            // though the dialog looks active. Re-assert real keyboard focus whenever we are activated.
            Activated += (s, e) => { try { ForceKeyboardFocus(); } catch { } };
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

        private void OkBtn_Click(object sender, RoutedEventArgs e) => Close();
        private void YesBtn_Click(object sender, RoutedEventArgs e) { Confirmed = true; Close(); }
        private void NoBtn_Click(object sender, RoutedEventArgs e) { Confirmed = false; Close(); }

        // ── Static helpers ────────────────────────────────────────────────

        public static void Show(string message, string title = "HolyLogger",
            HolyMsgType type = HolyMsgType.Info, Window owner = null, double width = 0)
        {
            new HolyMessageBox(message, title, type, owner, confirm: false, width).ShowDialog();
        }

        public static bool ShowConfirm(string message, string title = "HolyLogger",
            HolyMsgType type = HolyMsgType.Warning, Window owner = null)
        {
            var dlg = new HolyMessageBox(message, title, type, owner, confirm: true);
            dlg.ShowDialog();
            return dlg.Confirmed;
        }

        public static void ShowSuccess(string message, string title = "HolyLogger", Window owner = null)
            => Show(message, title, HolyMsgType.Success, owner);

        public static void ShowError(string message, string title = "HolyLogger", Window owner = null)
            => Show(message, title, HolyMsgType.Error, owner);

        public static void ShowWarning(string message, string title = "HolyLogger", Window owner = null, double width = 0)
            => Show(message, title, HolyMsgType.Warning, owner, width);
    }
}
