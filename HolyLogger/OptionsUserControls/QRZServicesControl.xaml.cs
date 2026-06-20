using System;
using System.Windows;
using System.Windows.Controls;

namespace HolyLogger.OptionsUserControls
{
    public partial class QRZServicesControl : UserControl
    {
        private bool _loading;
        public bool HasChanged { get; set; }

        // Raised after "Test Connection" so MainWindow can refresh the QRZ icon immediately.
        public event Action<bool, string> ConnectionTested;
        public event Action QrzQueueChanged;

        public QRZServicesControl()
        {
            InitializeComponent();

            _loading = true;

            // Callsign lookup credentials
            TB_UserName.Text = Properties.Settings.Default.qrz_username ?? string.Empty;
            TB_Password.Password = Properties.Settings.Default.qrz_password ?? string.Empty;

            // Logbook API key
            TB_ApiKey.Text = Properties.Settings.Default.qrz_api_key ?? string.Empty;
            CB_AutoPush.IsChecked = Properties.Settings.Default.qrz_logbook_auto_push;

            bool valid = Properties.Settings.Default.qrz_logbook_key_valid
                         && !string.IsNullOrWhiteSpace(TB_ApiKey.Text);
            CB_AutoPush.IsEnabled = valid;
            if (valid) ShowBadge("Valid API key — QRZ Logbook upload is ready.");

            CB_OnExit.SelectedIndex = Properties.Settings.Default.QrzUploadOnExitMode;

            _loading = false;
            HasChanged = false;
        }

        // ── Callsign lookup ──────────────────────────────────────────────

        private void TB_UserName_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_loading) return;
            Properties.Settings.Default.qrz_username = TB_UserName.Text;
            HasChanged = true;
        }

        private void TB_Password_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            Properties.Settings.Default.qrz_password = TB_Password.Password;
            HasChanged = true;
        }

        private void TestConnectionBtn_Click(object sender, RoutedEventArgs e)
        {
            Properties.Settings.Default.qrz_password = TB_Password.Password;
            Properties.Settings.Default.qrz_username = TB_UserName.Text;
            bool ok = Helper.LoginToQRZ(out string sessionKey);
            if (ok)
                HolyMessageBox.ShowSuccess("Connected to QRZ.com successfully!", "QRZ Connection", Window.GetWindow(this));
            else
                HolyMessageBox.ShowError("Connection failed. Check your username and password.", "QRZ Connection", Window.GetWindow(this));
            ConnectionTested?.Invoke(ok, sessionKey);
        }

        // ── Logbook ──────────────────────────────────────────────────────

        private void TB_ApiKey_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_loading) return;
            Properties.Settings.Default.qrz_api_key = (TB_ApiKey.Text ?? string.Empty).Trim();
            Properties.Settings.Default.qrz_logbook_key_valid = false;
            Properties.Settings.Default.Save();
            StatusBadge.Visibility = Visibility.Collapsed;
            CB_AutoPush.IsEnabled = false;
            HasChanged = true;
        }

        private void GetKeyBtn_Click(object sender, RoutedEventArgs e)
        {
            try { System.Diagnostics.Process.Start(QrzLogbookService.ApiDocsUrl); }
            catch (Exception ex)
            {
                HolyMessageBox.ShowError("Could not open the browser:\n" + ex.Message, "QRZ Logbook", Window.GetWindow(this));
            }
        }

        private async void TestBtn_Click(object sender, RoutedEventArgs e)
        {
            string key = (TB_ApiKey.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                HolyMessageBox.ShowWarning("Enter your QRZ Logbook API Key first.", "QRZ Logbook", Window.GetWindow(this));
                return;
            }

            Properties.Settings.Default.qrz_api_key = key;
            Properties.Settings.Default.Save();

            TestBtn.IsEnabled = false;
            try
            {
                QrzLogbookResult r = await QrzLogbookService.TestKeyAsync(key);
                var win = Window.GetWindow(this);
                if (r.NetworkError)
                {
                    SetValid(false);
                    HolyMessageBox.ShowError("Could not reach QRZ.com.\nPlease check your internet connection and try again.", "QRZ Logbook", win);
                    return;
                }
                if (r.Ok)
                {
                    SetValid(true);
                    string extra = string.IsNullOrEmpty(r.Count) ? string.Empty : $"\n{r.Count} QSOs are in your logbook.";
                    ShowBadge("Valid API key — QRZ Logbook upload is ready." + extra.Trim());
                    HolyMessageBox.ShowSuccess("API key is valid. QRZ Logbook upload is ready!" + extra, "QRZ Logbook", win);
                }
                else
                {
                    SetValid(false);
                    string reason = (r.Reason ?? string.Empty).ToLowerInvariant();
                    if (reason.Contains("auth") || reason.Contains("invalid") || reason.Contains("key"))
                        HolyMessageBox.ShowError("Authentication failed. Invalid API Key.", "QRZ Logbook", win);
                    else if (reason.Contains("subscription"))
                        HolyMessageBox.ShowError("An active QRZ XML Logbook Data Subscription is required.", "QRZ Logbook", win);
                    else
                        HolyMessageBox.ShowError("QRZ rejected the API key" +
                            (string.IsNullOrWhiteSpace(r.Reason) ? "." : ":\n" + r.Reason), "QRZ Logbook", win);
                }
            }
            catch (Exception ex)
            {
                SetValid(false);
                HolyMessageBox.ShowError("Test failed:\n" + ex.Message, "QRZ Logbook", Window.GetWindow(this));
            }
            finally { TestBtn.IsEnabled = true; }
        }

        private void CB_AutoPush_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            Properties.Settings.Default.qrz_logbook_auto_push = CB_AutoPush.IsChecked == true;
            Properties.Settings.Default.Save();
            HasChanged = true;
        }

        private void CB_OnExit_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            Properties.Settings.Default.QrzUploadOnExitMode = CB_OnExit.SelectedIndex;
            Properties.Settings.Default.Save();
        }

        private void SetValid(bool valid)
        {
            Properties.Settings.Default.qrz_logbook_key_valid = valid;
            Properties.Settings.Default.Save();
            CB_AutoPush.IsEnabled = valid;
            if (!valid) StatusBadge.Visibility = Visibility.Collapsed;
            HasChanged = true;
        }

        private void ShowBadge(string text)
        {
            StatusText.Text = text;
            StatusBadge.Visibility = Visibility.Visible;
        }

        private void ClearQueueBtn_Click(object sender, RoutedEventArgs e)
        {
            var dal = DataAccess.GetInstance();
            int pending = dal.GetPendingQrzCount();
            if (pending == 0)
            {
                HolyMessageBox.Show("The QRZ Logbook queue is already empty.", "Clear QRZ Queue",
                    HolyMsgType.Info, Window.GetWindow(this));
                return;
            }

            bool confirmed = HolyMessageBox.ShowConfirm(
                $"Remove all {pending} QSO(s) from the QRZ Logbook upload queue?\n\n" +
                "They will no longer be included in the next upload.",
                "Clear QRZ Queue", HolyMsgType.Warning, Window.GetWindow(this));
            if (!confirmed) return;

            int count = dal.ClearQrzQueue();
            HolyMessageBox.ShowSuccess($"{count} QSO(s) removed from the QRZ Logbook queue.",
                "Clear QRZ Queue", Window.GetWindow(this));
            QrzQueueChanged?.Invoke();
        }
    }
}
