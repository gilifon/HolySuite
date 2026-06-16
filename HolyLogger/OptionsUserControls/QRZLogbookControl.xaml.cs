using System;
using System.Windows;
using System.Windows.Controls;

namespace HolyLogger.OptionsUserControls
{
    /// <summary>
    /// Interaction logic for QRZLogbookControl.xaml — configures the real-time QRZ.com Logbook upload:
    /// the Logbook API key, a "Test" button that validates it (ACTION=STATUS), and the auto-upload
    /// toggle. The toggle stays disabled (grayed out) until a key passes validation, per the spec.
    /// </summary>
    public partial class QRZLogbookControl : UserControl
    {
        private bool _loading;
        public bool HasChanged { get; set; }

        public QRZLogbookControl()
        {
            InitializeComponent();

            _loading = true;
            TB_ApiKey.Text = Properties.Settings.Default.qrz_api_key ?? string.Empty;
            CB_AutoPush.IsChecked = Properties.Settings.Default.qrz_logbook_auto_push;

            // A key that already validated on a previous run unlocks the toggle and shows the badge.
            bool valid = Properties.Settings.Default.qrz_logbook_key_valid
                         && !string.IsNullOrWhiteSpace(TB_ApiKey.Text);
            CB_AutoPush.IsEnabled = valid;
            if (valid) ShowBadge("Valid API key — QRZ Logbook upload is ready.");
            _loading = false;

            HasChanged = false;
        }

        // Editing the key invalidates the previous validation: hide the badge and re-lock the toggle
        // until the user tests the new key. The trimmed value is persisted immediately.
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

        // Opens the QRZ Logbook API documentation page in the user's default browser, where the
        // Logbook API key is found.
        private void GetKeyBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(QrzLogbookService.ApiDocsUrl);
            }
            catch (Exception ex)
            {
                System.Windows.Forms.MessageBox.Show("Could not open the browser: " + ex.Message);
            }
        }

        private async void TestBtn_Click(object sender, RoutedEventArgs e)
        {
            string key = (TB_ApiKey.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                System.Windows.Forms.MessageBox.Show("Enter your QRZ Logbook API Key first.");
                return;
            }

            Properties.Settings.Default.qrz_api_key = key;
            Properties.Settings.Default.Save();

            TestBtn.IsEnabled = false;
            try
            {
                QrzLogbookResult r = await QrzLogbookService.TestKeyAsync(key);

                if (r.NetworkError)
                {
                    SetValid(false);
                    System.Windows.Forms.MessageBox.Show("Could not reach QRZ.com. Please check your internet connection and try again.");
                    return;
                }

                if (r.Ok)
                {
                    SetValid(true);
                    string extra = string.IsNullOrEmpty(r.Count) ? string.Empty : ("  (" + r.Count + " QSOs in your logbook)");
                    ShowBadge("Valid API key — QRZ Logbook upload is ready." + extra);
                }
                else
                {
                    SetValid(false);
                    string reason = (r.Reason ?? string.Empty).ToLowerInvariant();
                    if (reason.Contains("auth") || reason.Contains("invalid") || reason.Contains("key"))
                        System.Windows.Forms.MessageBox.Show("Authentication failed. Invalid API Key.");
                    else if (reason.Contains("subscription"))
                        System.Windows.Forms.MessageBox.Show("An active QRZ XML Logbook Data Subscription is required to use automated API logging.");
                    else
                        System.Windows.Forms.MessageBox.Show("QRZ rejected the API key" +
                            (string.IsNullOrWhiteSpace(r.Reason) ? "." : (": " + r.Reason)));
                }
            }
            catch (Exception ex)
            {
                SetValid(false);
                System.Windows.Forms.MessageBox.Show("Test failed: " + ex.Message);
            }
            finally
            {
                TestBtn.IsEnabled = true;
            }
        }

        private void CB_AutoPush_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            Properties.Settings.Default.qrz_logbook_auto_push = CB_AutoPush.IsChecked == true;
            Properties.Settings.Default.Save();
            HasChanged = true;
        }

        // Records the validation outcome and unlocks/locks the auto-upload toggle accordingly.
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
    }
}
