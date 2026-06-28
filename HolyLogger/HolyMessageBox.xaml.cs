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

            ContentRendered += (s, e) =>
            {
                try
                {
                    Activate();
                    IntPtr h = new WindowInteropHelper(this).Handle;
                    if (h != IntPtr.Zero) SetForegroundWindow(h);
                    IInputElement btn = OkBtn.Visibility == Visibility.Visible ? (IInputElement)OkBtn : YesBtn;
                    Keyboard.Focus(btn);
                    DiagLog($"opened title='{Title}' IsActive={IsActive} focus={Keyboard.FocusedElement?.GetType().Name ?? "null"}");
                }
                catch (Exception ex) { DiagLog("open-error " + ex.Message); }
            };
        }

        private void OnDialogPreviewKeyDown(object sender, KeyEventArgs e)
        {
            DiagLog($"PreviewKeyDown {e.Key} IsActive={IsActive} focus={Keyboard.FocusedElement?.GetType().Name ?? "null"}");
            if (e.Key == Key.Escape) { Confirmed = false; e.Handled = true; Close(); }
        }

        // Esc closes the dialog (also wired as Window.KeyDown in XAML). For confirm dialogs Esc = No.
        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            DiagLog($"KeyDown {e.Key}");
            if (e.Key == Key.Escape)
            {
                Confirmed = false;
                Close();
            }
        }

        // TEMPORARY diagnostics — confirms whether key events actually reach this dialog at startup.
        private static void DiagLog(string msg)
        {
            try { System.IO.File.AppendAllText(@"C:\temp\holymsg_keylog.txt",
                DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg + Environment.NewLine); }
            catch { }
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
