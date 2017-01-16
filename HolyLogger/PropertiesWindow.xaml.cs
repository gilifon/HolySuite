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
using System.Windows.Shapes;

namespace HolyLogger
{
    /// <summary>
    /// Interaction logic for PropertiesWindow.xaml
    /// </summary>
    public partial class PropertiesWindow : Window
    {
        public string username { get; set; }
        public string password { get; set; }

        public PropertiesWindow()
        {
            InitializeComponent();
            password = Properties.Settings.Default.qrz_password;
            username = Properties.Settings.Default.qrz_username;

            TB_Password.Password = Properties.Settings.Default.qrz_password;
            TB_UserName.Text = Properties.Settings.Default.qrz_username;
        }

        private void CancelBtn_Click(object sender, RoutedEventArgs e)
        {
            Properties.Settings.Default.qrz_password = password;
            Properties.Settings.Default.qrz_username = username;
            this.Close();
        }

        private void SaveBtn_Click(object sender, RoutedEventArgs e)
        {
            Properties.Settings.Default.qrz_password = TB_Password.Password;
            Properties.Settings.Default.qrz_username = TB_UserName.Text;
            this.Close();
        }

        private void TestConnectionBtn_Click(object sender, RoutedEventArgs e)
        {
            Properties.Settings.Default.qrz_password = TB_Password.Password;
            Properties.Settings.Default.qrz_username = TB_UserName.Text;
            string x;
            if (Services.LoginToQRZ(out x))
            {
                System.Windows.Forms.MessageBox.Show("Connected!");
            }
            else
            {
                System.Windows.Forms.MessageBox.Show("Connection failed!");
            }
        }
    }
}
