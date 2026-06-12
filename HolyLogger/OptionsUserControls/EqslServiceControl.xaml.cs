using System;
using System.Windows;
using System.Windows.Controls;

namespace HolyLogger.OptionsUserControls
{
    /// <summary>
    /// Interaction logic for EqslServiceControl.xaml — eQSL.cc credentials and the
    /// "auto-upload each QSO" toggle. Mirrors the QRZ service control.
    /// </summary>
    public partial class EqslServiceControl : UserControl
    {
        private bool _loading;
        public bool HasChanged { get; set; }

        public EqslServiceControl()
        {
            InitializeComponent();

            _loading = true;
            CB_AutoUpload.IsChecked = Properties.Settings.Default.EqslAutoUpload;
            TB_UserName.Text = Properties.Settings.Default.EqslUsername;
            TB_Password.Password = Properties.Settings.Default.EqslPassword;
            TB_Nickname.Text = Properties.Settings.Default.EqslQthNickname;
            _loading = false;

            HasChanged = false;
        }

        private void CB_AutoUpload_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            Properties.Settings.Default.EqslAutoUpload = CB_AutoUpload.IsChecked == true;
            Properties.Settings.Default.Save();
            HasChanged = true;
        }

        private void TB_UserName_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_loading) return;
            Properties.Settings.Default.EqslUsername = TB_UserName.Text;
            Properties.Settings.Default.Save();
            HasChanged = true;
        }

        private void TB_Password_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            Properties.Settings.Default.EqslPassword = TB_Password.Password;
            Properties.Settings.Default.Save();
            HasChanged = true;
        }

        private void TB_Nickname_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_loading) return;
            Properties.Settings.Default.EqslQthNickname = TB_Nickname.Text;
            Properties.Settings.Default.Save();
            HasChanged = true;
        }

        private async void TestConnectionBtn_Click(object sender, RoutedEventArgs e)
        {
            string user = (TB_UserName.Text ?? string.Empty).Trim();
            string pwd = TB_Password.Password ?? string.Empty;
            if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(pwd))
            {
                System.Windows.Forms.MessageBox.Show("Please enter your eQSL user name and password first.");
                return;
            }

            TestConnectionBtn.IsEnabled = false;
            try
            {
                // Authenticate with a header-only ADIF (no records) so nothing is uploaded.
                string adif = "<adif_ver:5>3.1.4<programid:10>HolyLogger<eoh>";
                string url = "https://www.eQSL.cc/qslcard/ImportADIF.cfm"
                    + "?EQSL_USER=" + Uri.EscapeDataString(user)
                    + "&EQSL_PSWD=" + Uri.EscapeDataString(pwd)
                    + "&ADIFData=" + Uri.EscapeDataString(adif);
                string resp;
                using (var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(25) })
                {
                    resp = await http.GetStringAsync(url);
                }

                if (resp != null && resp.IndexOf("No match", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    System.Windows.Forms.MessageBox.Show("eQSL rejected the user name / password.");
                }
                else if (resp != null
                         && resp.IndexOf("Error", StringComparison.OrdinalIgnoreCase) >= 0
                         && resp.IndexOf("Result", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    System.Windows.Forms.MessageBox.Show("eQSL returned: " + resp.Trim());
                }
                else
                {
                    System.Windows.Forms.MessageBox.Show("Connected to eQSL successfully!");
                }
            }
            catch (Exception ex)
            {
                System.Windows.Forms.MessageBox.Show("Connection failed: " + ex.Message);
            }
            finally
            {
                TestConnectionBtn.IsEnabled = true;
            }
        }
    }
}
