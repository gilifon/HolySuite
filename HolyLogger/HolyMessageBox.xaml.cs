using System.Windows;
using System.Windows.Media;

namespace HolyLogger
{
    public enum HolyMsgType { Info, Success, Warning, Error }

    public partial class HolyMessageBox : Window
    {
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
