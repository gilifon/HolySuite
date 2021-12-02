using HolyParser;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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

        DataAccess dal;
        public ObservableCollection<RadioEvent> RadioEvents { get; set; }
        public ObservableCollection<Category> Categories { get; set; }

        public Category selectedCategory { get; set; }

        public LogUploadWindow()
        {
            InitializeComponent();
            try
            {
                dal = new DataAccess();
            }
            catch (Exception e)
            {
                MessageBox.Show("Failed to connect to DB: " + e.Message);
                throw;
            }

            RadioEvents = dal.GetRadioEvents();
            CB_Events.SelectionChanged += CB_Events_SelectionChanged;
            CB_Events.DisplayMemberPath = "Description";
            CB_Events.SelectedValuePath = "id";
            CB_Events.ItemsSource = RadioEvents;
            CB_Events.SelectedIndex = 0;
        }

        private void CB_Events_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            Categories = dal.GetCategories((int)CB_Events.SelectedValue);
            CB_Category.SelectionChanged += CB_Category_SelectionChanged;
            CB_Category.DisplayMemberPath = "Description";
            CB_Category.SelectedValuePath = "id";
            CB_Category.ItemsSource = Categories;
            CB_Category.SelectedIndex = 0;
        }

        private void CB_Category_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            selectedCategory = (Category)CB_Category.SelectedItem;
        }

        private void SendLogBtn_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(((Category)CB_Category.SelectedItem).Description) && !string.IsNullOrWhiteSpace(Properties.Settings.Default.PersonalInfoEmail) && !string.IsNullOrWhiteSpace(Properties.Settings.Default.PersonalInfoName) && !string.IsNullOrWhiteSpace(Properties.Settings.Default.PersonalInfoCallsign))
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
