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

        // Eye toggle for the QRZ lookup password: reveal the masked PasswordBox by swapping in a plain
        // TextBox (and back), keeping the two in sync so the saved setting is always current.
        private void ShowPassword_Checked(object sender, RoutedEventArgs e)
        {
            TB_PasswordVisible.Text = TB_Password.Password;
            TB_PasswordVisible.Visibility = Visibility.Visible;
            TB_Password.Visibility = Visibility.Collapsed;
        }

        private void ShowPassword_Unchecked(object sender, RoutedEventArgs e)
        {
            TB_Password.Password = TB_PasswordVisible.Text;
            TB_Password.Visibility = Visibility.Visible;
            TB_PasswordVisible.Visibility = Visibility.Collapsed;
        }

        private void TB_PasswordVisible_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_loading) return;
            Properties.Settings.Default.qrz_password = TB_PasswordVisible.Text;
            HasChanged = true;
        }

        // The password currently entered, whichever box is showing.
        private string CurrentPassword =>
            BTN_ShowPassword.IsChecked == true ? TB_PasswordVisible.Text : TB_Password.Password;

        // Awaited, not blocking. The synchronous QRZ login waits on a request whose default timeout is
        // 100 seconds, and it did that on the UI thread - so on a network that hangs rather than refuses,
        // pressing Test Connection froze the whole Options window with no sign of why. The button greys
        // itself while the round trip is in flight, the same as the eQSL and Logbook tests beside it.
        private async void TestConnectionBtn_Click(object sender, RoutedEventArgs e)
        {
            Properties.Settings.Default.qrz_password = CurrentPassword;
            Properties.Settings.Default.qrz_username = TB_UserName.Text;

            string sessionKey;
            TestConnectionBtn.IsEnabled = false;
            try { sessionKey = await Helper.LoginToQRZAsync(); }
            finally { TestConnectionBtn.IsEnabled = true; }

            bool ok = !string.IsNullOrWhiteSpace(sessionKey);
            if (ok)
                HolyMessageBox.ShowSuccess("Connected to QRZ.com successfully!", "QRZ Connection", Window.GetWindow(this));
            else
                HolyMessageBox.ShowError(
                    "Connection failed. Check your user name and password.\n\n"
                    + "An XML Subscription is also required for callsign lookups.\n"
                    + "Without one QRZ refuses the login even when the password is right.",
                    "QRZ Connection", Window.GetWindow(this));
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
                HolyMessageBox.ShowError(
                    "Could not open the browser.\n\n" + ex.Message + "\n\n"
                    + "Open qrz.com in your own browser and sign in there.",
                    "QRZ Logbook", Window.GetWindow(this));
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
                    HolyMessageBox.ShowError(
                        "Could not reach QRZ.com.\n\n"
                        + "Check your internet connection and try again.\n"
                        + "If other websites work, QRZ itself may be down — wait a few minutes.",
                        "QRZ Logbook", win);
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
                        HolyMessageBox.ShowError(
                            "An active QRZ XML Logbook Data Subscription is required.\n\n"
                            + "It is bought at qrz.com under Subscriptions. Without it QRZ refuses "
                            + "the request whatever the password or the key.",
                            "QRZ Logbook", win);
                    else
                        HolyMessageBox.ShowError("QRZ rejected the API key" +
                            (string.IsNullOrWhiteSpace(r.Reason) ? "." : ":\n" + r.Reason), "QRZ Logbook", win);
                }
            }
            catch (Exception ex)
            {
                SetValid(false);
                HolyMessageBox.ShowError(
                    "The test failed.\n\n" + ex.Message + "\n\n"
                    + "Nothing was saved.\n\n"
                    + "Paste the key again — a space or a line break copied along with it is the "
                    + "usual cause. If that is not it, check you can reach qrz.com in your browser.",
                    "QRZ Logbook", Window.GetWindow(this));
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
