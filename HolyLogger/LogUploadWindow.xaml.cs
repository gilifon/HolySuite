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

        private string _CategoryOperator = "SINGLE-OP";
        public string CategoryOperator
        {
            get { return _CategoryOperator; }
            set
            {
                _CategoryOperator = value;
                OnPropertyChanged("CategoryOperator");
            }
        }

        private string _CategoryMode = "SSB";
        public string CategoryMode
        {
            get { return _CategoryMode; }
            set
            {
                _CategoryMode = value;
                OnPropertyChanged("CategoryMode");
            }
        }

        private string _CategoryPower = "HIGH";
        public string CategoryPower
        {
            get { return _CategoryPower; }
            set
            {
                _CategoryPower = value;
                OnPropertyChanged("CategoryPower");
            }
        }

        private string _Email;
        public string Email
        {
            get { return _Email; }
            set
            {
                _Email = value;
                OnPropertyChanged("Email");
            }
        }

        private string _EmailConf;
        public string EmailConf
        {
            get { return _EmailConf; }
            set
            {
                _EmailConf = value;
                OnPropertyChanged("EmailConf");
            }
        }

        private string _Handle;
        public string Handle
        {
            get { return _Handle; }
            set
            {
                _Handle = value;
                OnPropertyChanged("Handle");
            }
        }

        private string _Callsign;
        public string Callsign
        {
            get { return _Callsign; }
            set
            {
                _Callsign = value;
                OnPropertyChanged("Callsign");
            }
        }

        public LogUploadWindow()
        {
            InitializeComponent();
        }

        private void SendLogBtn_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(CategoryOperator) && !string.IsNullOrWhiteSpace(CategoryMode) && !string.IsNullOrWhiteSpace(CategoryPower) && !string.IsNullOrWhiteSpace(Email) && !string.IsNullOrWhiteSpace(Handle) && !string.IsNullOrWhiteSpace(Callsign))
            {
                if (Email == EmailConf)
                {
                    if (SendLog != null)
                    {
                        SendLog(this, e);
                        Spinner.Visibility = Visibility.Visible;
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
    }
}
