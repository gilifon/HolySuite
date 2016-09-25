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
using Microsoft.Win32;
using System.Collections.Specialized;

namespace HolyLogger
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window, INotifyPropertyChanged
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

       

        DataAccess dal = new DataAccess();
        public ObservableCollection<QSO> Qsos;

        private string _NumOfQSOs;
        public string NumOfQSOs
        {
            get { return _NumOfQSOs; }
            set
            {
                _NumOfQSOs = value;
                OnPropertyChanged("NumOfQSOs");
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
            Qsos.CollectionChanged += Qsos_CollectionChanged;
            DataContext = Qsos;
            
            UpdateNumOfQSOs();            
        }

        void Qsos_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action == NotifyCollectionChangedAction.Remove)
            {
                foreach (QSO qso in e.OldItems)
                {
                    dal.Delete(qso.id);    
                }
                UpdateNumOfQSOs();  
            }
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
            if (!Validate()) return;
            QSO qso = new QSO();
            qso.comment = TB_Comment.Text;
            qso.dx_callsign = TB_DXCallsign.Text;
            qso.exchange = TB_Exchange.Text;
            qso.frequency = TB_Frequency.Text;
            qso.my_callsign = TB_MyCallsign.Text;
            qso.my_square = TB_MyGrid.Text;
            qso.rst_rcvd = TB_RSTRcvd.Text;
            qso.rst_sent = TB_RSTSent.Text;
            qso.timestamp = QSOTimeStamp.Value.Value;
            QSO q = dal.Insert(qso);
            Qsos.Insert(0, q);
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
            TB_Comment.Text = string.Empty;
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

        private void ExpotMenuItem_Click(object sender, RoutedEventArgs e)
        {
            string adif = GenerateAdif(dal.GetAllQSOs());
            // Displays a SaveFileDialog so the user can save the Image
            // assigned to Button2.
            SaveFileDialog saveFileDialog1 = new SaveFileDialog();
            saveFileDialog1.Filter = "ADIF File|*.adi";
            saveFileDialog1.Title = "Export ADIF";
            saveFileDialog1.ShowDialog();

            // If the file name is not an empty string open it for saving.
            try
            {
                if (saveFileDialog1.FileName != "")
                {
                    // Saves the Image via a FileStream created by the OpenFile method.
                    System.IO.FileStream fs = (System.IO.FileStream)saveFileDialog1.OpenFile();
                    StreamWriter sw = new StreamWriter(fs);
                    sw.Write(adif);
                    sw.Close();
                    fs.Close();
                }
                MessageBox.Show("File created successfully!");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Export failed: " + ex.Message);
            }
            
        }

        private string GenerateAdif(IList<QSO> qso_list)  
        {
            StringBuilder adif = new StringBuilder(200);
            adif.AppendLine("<ADIF_VERS:3>2.2 ");
            adif.AppendLine("<PROGRAMID:14>HolylandLogger ");
            adif.AppendLine("<PROGRAMVERSION:15>Version 1.0.0.0 ");
            adif.AppendLine("<EOH>");
            adif.AppendLine();

            foreach (QSO qso in qso_list)
            {
                string date = qso.timestamp.ToString("yyyyMMdd");
                string time = qso.timestamp.ToString("HHmmss");

                adif.AppendFormat("<call:{0}>{1} ", qso.dx_callsign.Length, qso.dx_callsign);
                adif.AppendFormat("<srx_string:{0}>{1} ", qso.exchange.Length, qso.exchange);
                adif.AppendFormat("<freq:{0}>{1} ", qso.frequency.Length, qso.frequency);
                adif.AppendFormat("<station_callsign:{0}>{1} ", qso.my_callsign.Length, qso.my_callsign);
                adif.AppendFormat("<operator:{0}>{1} ", qso.my_callsign.Length, qso.my_callsign);
                adif.AppendFormat("<stx_string :{0}>{1} ", qso.my_square.Length, qso.my_square);
                adif.AppendFormat("<rst_rcvd:{0}>{1} ", qso.rst_rcvd.Length, qso.rst_rcvd);
                adif.AppendFormat("<rst_sent:{0}>{1} ", qso.rst_sent.Length, qso.rst_sent);
                adif.AppendFormat("<qso_date:{0}>{1} ", date.Length, date);
                adif.AppendFormat("<time_on:{0}>{1} ", time.Length, time);
                adif.AppendFormat("<time_off:{0}>{1} ", time.Length, time);
                adif.AppendFormat("<comment:{0}>{1} ", qso.comment.Length, qso.comment);
                adif.AppendLine("<EOR>");
            }

            return adif.ToString();
        }

        private void QSODataGrid_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete)
            {
                MessageBoxResult messageBoxResult = System.Windows.MessageBox.Show("Are you sure?", "Delete Confirmation", System.Windows.MessageBoxButton.YesNo);
                if (messageBoxResult == MessageBoxResult.Yes)
                {
                    
                }
                else
                {
                    e.Handled = true;
                }
            }
        }

        private void MenuItem_Click(object sender, RoutedEventArgs e)
        {
            AboutWindow about = new AboutWindow();
            about.Show();
        }

        private bool Validate()
        {
            bool allOK = true;
            if (string.IsNullOrWhiteSpace(TB_DXCallsign.Text))
            {
                allOK = false;
                TB_DXCallsign.BorderBrush = System.Windows.Media.Brushes.Red;
            }
            else
            {
                TB_DXCallsign.BorderBrush = System.Windows.Media.Brushes.LightGray;
            }


            if (string.IsNullOrWhiteSpace(TB_Exchange.Text))
            {
                allOK = false;
                TB_Exchange.BorderBrush = System.Windows.Media.Brushes.Red;
            }
            else
            {
                TB_Exchange.BorderBrush = System.Windows.Media.Brushes.LightGray;
            }

            if (string.IsNullOrWhiteSpace(TB_Frequency.Text))
            {
                allOK = false;
                TB_Frequency.BorderBrush = System.Windows.Media.Brushes.Red;
            }
            else
            {
                TB_Frequency.BorderBrush = System.Windows.Media.Brushes.LightGray;
            }
            
            if (string.IsNullOrWhiteSpace(TB_MyCallsign.Text))
            {
                allOK = false;
                TB_MyCallsign.BorderBrush = System.Windows.Media.Brushes.Red;
            }
            else
            {
                TB_MyCallsign.BorderBrush = System.Windows.Media.Brushes.LightGray;
            }

            if (string.IsNullOrWhiteSpace(TB_MyGrid.Text))
            {
                allOK = false;
                TB_MyGrid.BorderBrush = System.Windows.Media.Brushes.Red;
            }
            else
            {
                TB_MyGrid.BorderBrush = System.Windows.Media.Brushes.LightGray;
            }

            if (string.IsNullOrWhiteSpace(TB_RSTRcvd.Text))
            {
                allOK = false;
                TB_RSTRcvd.BorderBrush = System.Windows.Media.Brushes.Red;
            }
            else
            {
                TB_RSTRcvd.BorderBrush = System.Windows.Media.Brushes.LightGray;
            }

            if (string.IsNullOrWhiteSpace(TB_RSTSent.Text))
            {
                allOK = false;
                TB_RSTSent.BorderBrush = System.Windows.Media.Brushes.Red;
            }
            else
            {
                TB_RSTSent.BorderBrush = System.Windows.Media.Brushes.LightGray;
            }

            if (string.IsNullOrWhiteSpace(QSOTimeStamp.Text))
            {
                allOK = false;
                QSOTimeStamp.BorderBrush = System.Windows.Media.Brushes.Red;
            }
            else
            {
                QSOTimeStamp.BorderBrush = System.Windows.Media.Brushes.LightGray;
            }

            return allOK;
        }

       
    }
}
