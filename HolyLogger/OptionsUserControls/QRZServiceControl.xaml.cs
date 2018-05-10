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
            if (Helper.LoginToQRZ(out x))
            {
                System.Windows.Forms.MessageBox.Show("Connected!");
            }
            else
            {
                System.Windows.Forms.MessageBox.Show("Connection failed!");
            }
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
