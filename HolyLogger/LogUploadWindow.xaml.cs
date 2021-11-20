using System;
using System.Collections.Generic;
using System.ComponentModel;
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
    // A delegate type for hooking up change notifications.
    public delegate void SendLogEventHandler(object sender, EventArgs e);

    /// <summary>
    /// Interaction logic for LogUploadWindow.xaml
    /// </summary>
    public partial class LogUploadWindow : Window, INotifyPropertyChanged
    {
        #region INotifyProprtyChanged
        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string propertyName)
        {
            if (PropertyChanged != null)
            {
                PropertyChanged(this, new PropertyChangedEventArgs(propertyName));
            }
        }
        #endregion

        public event SendLogEventHandler SendLog;

        private string _Category = "SSB (Single OP, SSB Only)";
        public string Category
        {
            get { return _Category; }
            set
            {
                _Category = value;
                OnPropertyChanged("Category");
            }
        }

        public int _UploadProgress = 0;
        public int UploadProgress
        {
            get { return _UploadProgress; }
            set
            {
                _UploadProgress = value;
                OnPropertyChanged("UploadProgress");
            }
        }

        public LogUploadWindow()
        {
            InitializeComponent();
        }

        private void SendLogBtn_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(Category) && !string.IsNullOrWhiteSpace(Properties.Settings.Default.PersonalInfoEmail) && !string.IsNullOrWhiteSpace(Properties.Settings.Default.PersonalInfoName) && !string.IsNullOrWhiteSpace(Properties.Settings.Default.PersonalInfoCallsign))
            {
                if (Properties.Settings.Default.PersonalInfoEmail == Properties.Settings.Default.PersonalInfoEmailConf)
                {
                    if (SendLog != null)
                    {
                        SendLog(this, e);
                        Spinner.Visibility = Visibility.Visible;
                        L_Progress.Visibility = Visibility.Visible;
                        this.IsEnabled = false;
                    }
                }
                else
                {
                    System.Windows.Forms.MessageBox.Show("Please confirm your Email address");
                }
            }
            else
            {
                System.Windows.Forms.MessageBox.Show("Please fill in the form. All fields are required.");
            }
            
        }

        private void CancelBtn_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void Window_LocationChanged(object sender, EventArgs e)
        {
            if (this.Left >= 0)
                Properties.Settings.Default.LogUploadWindowLeft = this.Left;
            if (this.Top >= 0)
                Properties.Settings.Default.LogUploadWindowTop = this.Top;
        }
    }
}
