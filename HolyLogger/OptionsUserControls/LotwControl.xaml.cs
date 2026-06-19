using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace HolyLogger.OptionsUserControls
{
    public partial class LotwControl : UserControl
    {
        private bool _loading;

        public DataAccess Dal { get; set; }

        public LotwControl()
        {
            InitializeComponent();

            _loading = true;
            TB_TqslPath.Text = Properties.Settings.Default.LotwTqslPath ?? string.Empty;
            TB_StationLocation.Text = Properties.Settings.Default.LotwStationLocation ?? string.Empty;
            PB_Password.Password = Properties.Settings.Default.LotwTqslPassword ?? string.Empty;
            TB_WebUser.Text = Properties.Settings.Default.LotwWebUser ?? string.Empty;
            PB_WebPassword.Password = Properties.Settings.Default.LotwWebPassword ?? string.Empty;
            DP_FromDate.SelectedDate = DateTime.Today;
            int mode = Properties.Settings.Default.LotwUploadOnExitMode;
            CB_UploadOnExit.SelectedIndex = (mode >= 0 && mode <= 2) ? mode : 0;
            _loading = false;
        }

        private void CB_UploadOnExit_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            Properties.Settings.Default.LotwUploadOnExitMode = CB_UploadOnExit.SelectedIndex;
            Properties.Settings.Default.Save();
        }

        private void TB_TqslPath_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_loading) return;
            Properties.Settings.Default.LotwTqslPath = TB_TqslPath.Text.Trim();
            Properties.Settings.Default.Save();
            StatusBadge.Visibility = Visibility.Collapsed;
        }

        private void TB_StationLocation_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_loading) return;
            Properties.Settings.Default.LotwStationLocation = TB_StationLocation.Text.Trim();
            Properties.Settings.Default.Save();
        }

        private void PB_Password_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            Properties.Settings.Default.LotwTqslPassword = PB_Password.Password;
            Properties.Settings.Default.Save();
        }

        private void TB_WebUser_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_loading) return;
            Properties.Settings.Default.LotwWebUser = TB_WebUser.Text.Trim();
            Properties.Settings.Default.Save();
        }

        private void PB_WebPassword_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            Properties.Settings.Default.LotwWebPassword = PB_WebPassword.Password;
            Properties.Settings.Default.Save();
        }

        private void BrowseBtn_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new System.Windows.Forms.OpenFileDialog
            {
                Title = "Locate TQSL executable",
                Filter = "Executables (*.exe)|*.exe|All files (*.*)|*.*",
                FileName = "tqsl.exe"
            };
            if (!string.IsNullOrWhiteSpace(TB_TqslPath.Text) && File.Exists(TB_TqslPath.Text))
                dlg.InitialDirectory = Path.GetDirectoryName(TB_TqslPath.Text);
            else
                dlg.InitialDirectory = @"C:\Program Files (x86)\Trusted QSL";

            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                TB_TqslPath.Text = dlg.FileName;
        }

        private void TestBtn_Click(object sender, RoutedEventArgs e)
        {
            string path = TB_TqslPath.Text.Trim();
            if (string.IsNullOrWhiteSpace(path))
            {
                System.Windows.Forms.MessageBox.Show("Enter the path to your TQSL executable first.");
                return;
            }
            if (!File.Exists(path))
            {
                ShowError($"File not found:\n{path}");
                return;
            }
            ShowOk($"TQSL found: {path}");
        }

        private void ResetFromDateBtn_Click(object sender, RoutedEventArgs e)
        {
            if (Dal == null)
            {
                ShowError("Database not available.");
                return;
            }
            if (DP_FromDate.SelectedDate == null)
            {
                System.Windows.Forms.MessageBox.Show("Please select a date first.");
                return;
            }

            string fromDate = DP_FromDate.SelectedDate.Value.ToString("yyyy-MM-dd");

            var confirm = System.Windows.Forms.MessageBox.Show(
                $"Mark all QSOs from {fromDate} onwards as pending for LoTW upload?\n\n" +
                "They will be included in the next upload. LoTW will skip any that are already in the system.",
                "Reset LoTW Queue",
                System.Windows.Forms.MessageBoxButtons.YesNo,
                System.Windows.Forms.MessageBoxIcon.Question);

            if (confirm != System.Windows.Forms.DialogResult.Yes) return;

            int count = Dal.ResetLotwStatusFromDate(fromDate);
            ShowOk($"{count} QSO(s) marked as pending from {fromDate} onwards.");
        }

        private void ShowOk(string text)
        {
            StatusBadge.Background = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#E6F4EA"));
            StatusBadge.BorderBrush = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#34A853"));
            StatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#1E7E34"));
            StatusText.Text = text;
            StatusBadge.Visibility = Visibility.Visible;
        }

        private void ShowError(string text)
        {
            StatusBadge.Background = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FDE8E8"));
            StatusBadge.BorderBrush = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#D32F2F"));
            StatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#B71C1C"));
            StatusText.Text = text;
            StatusBadge.Visibility = Visibility.Visible;
        }
    }
}
