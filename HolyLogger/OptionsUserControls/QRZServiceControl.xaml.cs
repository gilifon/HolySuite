using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace HolyLogger.OptionsUserControls
{
    /// <summary>
    /// Interaction logic for QRZServiceControl.xaml
    /// </summary>
    public partial class QRZServiceControl : UserControl
    {
        public string username { get; set; }
        public string password { get; set; }
        public bool HasChanged{ get; set; }

        // Raised after the user presses "Test Connection", carrying the result and the QRZ session
        // key obtained (empty on failure), so the main window can refresh the QRZ icon right away.
        public event Action<bool, string> ConnectionTested;

        public QRZServiceControl()
        {
            InitializeComponent();

            password = Properties.Settings.Default.qrz_password;
            username = Properties.Settings.Default.qrz_username;

            TB_Password.Password = Properties.Settings.Default.qrz_password;
            TB_UserName.Text = Properties.Settings.Default.qrz_username;

            HasChanged = false;
        }
        
        private void TestConnectionBtn_Click(object sender, RoutedEventArgs e)
        {
            Properties.Settings.Default.qrz_password = TB_Password.Password;
            Properties.Settings.Default.qrz_username = TB_UserName.Text;
            string x;
            bool ok = Helper.LoginToQRZ(out x);
            if (ok)
                HolyMessageBox.ShowSuccess("Connected to QRZ.com successfully!", "QRZ Connection", Window.GetWindow(this));
            else
                HolyMessageBox.ShowError("Connection failed. Check your username and password.", "QRZ Connection", Window.GetWindow(this));
            // Let the main window update the QRZ icon to match the test result immediately.
            ConnectionTested?.Invoke(ok, x);
        }

        private void TB_UserName_TextChanged(object sender, TextChangedEventArgs e)
        {
            Properties.Settings.Default.qrz_username = TB_UserName.Text;
            HasChanged = true;
        }

        private void TB_Password_PasswordChanged(object sender, RoutedEventArgs e)
        {
            Properties.Settings.Default.qrz_password = TB_Password.Password;
            HasChanged = true;
        }
    }
}
