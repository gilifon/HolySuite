using System;
using System.Collections.Generic;
using System.IO;
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
using System.Data.SQLite;
using Xceed.Wpf.Controls;
using System.Collections.ObjectModel;
using System.ComponentModel;

namespace HolyLogger
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        protected void RaisePropertyChanged(string propertyName)
        {
            var handler = PropertyChanged;
            if (handler != null)
            {
                handler(this, new PropertyChangedEventArgs(propertyName));
            }
        }

        DataAccess dal = new DataAccess();
        public ObservableCollection<QSO> Qsos;

        private string _NumOfQSOs;
        public string NumOfQSOs
        {
            get { return _NumOfQSOs; }
            set
            {
                _NumOfQSOs = value;
                RaisePropertyChanged("NumOfQSOs");
            }
        }

        public MainWindow()
        {
            InitializeComponent();
            TB_MyCallsign.Focus();

            Left = (System.Windows.SystemParameters.PrimaryScreenWidth - Width) / 2;
            Top = (System.Windows.SystemParameters.PrimaryScreenHeight - Height) / 2;

            QSOTimeStamp.Value = DateTime.UtcNow;
            Qsos = dal.GetAllQSOs();
            DataContext = Qsos;
            
            UpdateNumOfQSOs();            
        }

        private void Lock_Btn_MouseUp(object sender, MouseButtonEventArgs e)
        {
            TB_MyCallsign.IsEnabled = !(TB_MyCallsign.IsEnabled);
            TB_MyGrid.IsEnabled = !(TB_MyGrid.IsEnabled);
            TB_Frequency.IsEnabled = !(TB_Frequency.IsEnabled);
            if (TB_MyGrid.IsEnabled) ((Image)sender).Opacity = 1;
            else ((Image)sender).Opacity = 0.5;
        }

        private void RefreshDateTime_Btn_MouseUp(object sender, MouseButtonEventArgs e)
        {
            QSOTimeStamp.Value = DateTime.UtcNow;
        }

        private void AddBtn_Click(object sender, RoutedEventArgs e)
        {
            QSO qso = new QSO();
            qso.comment = "";
            qso.dx_callsign = TB_DXCallsign.Text;
            qso.exchange = TB_Exchange.Text;
            qso.frequency = TB_Frequency.Text;
            qso.my_callsign = TB_MyCallsign.Text;
            qso.my_square = TB_MyGrid.Text;
            qso.rst_rcvd = TB_RSTRcvd.Text;
            qso.rst_sent = TB_RSTSent.Text;
            qso.timestamp = QSOTimeStamp.Value.Value;
            dal.Insert(qso);
            Qsos.Insert(0, qso);
            ClearBtn_Click(null, null);
            UpdateNumOfQSOs();
        }

        private void QRZBtn_Click(object sender, MouseButtonEventArgs e)
        {
            string url = "http://www.qrz.com";
            if (!string.IsNullOrWhiteSpace(TB_DXCallsign.Text))
                url += "/db/" + TB_DXCallsign.Text;

            System.Diagnostics.Process.Start(@"chrome.exe", url);
        }

        private void ClearBtn_Click(object sender, RoutedEventArgs e)
        {
            //TB_Frequency.Text = string.Empty;
            TB_DXCallsign.Text = string.Empty;
            TB_Exchange.Text = string.Empty;
            TB_RSTSent.Text = "59";
            TB_RSTRcvd.Text = "59";
            RefreshDateTime_Btn_MouseUp(null, null);
            TB_DXCallsign.Focus();
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F1)
            {
                AddBtn_Click(null, null);
            }
            else if (e.Key == Key.F5)
            {
                ClearBtn_Click(null, null);
            }
        }

        private void RST_GotFocus(object sender, RoutedEventArgs e)
        {
            if (((TextBox)sender).Text.Length > 0)
            {
                ((TextBox)sender).CaretIndex = 1;
                ((TextBox)sender).SelectionLength = 1;
            }
        }

        private void UpdateNumOfQSOs()
        {
            NumOfQSOs = dal.GetQsoCount().ToString();
        }

        private void ExitMenuItem_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }

    }
}
