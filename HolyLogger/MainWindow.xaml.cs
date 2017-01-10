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
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net;
using System.Xml.Linq;
using System.Xml;
using System.Xml.XPath;

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



        DataAccess dal;
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

        private string _Score;
        public string Score
        {
            get { return _Score; }
            set
            {
                _Score = value;
                OnPropertyChanged("Score");
            }
        }

        private string _Version;
        public string Version
        {
            get { return _Version; }
            set
            {
                _Version = value;
                OnPropertyChanged("Version");
            }
        }

        private string _Country;
        public string Country
        {
            get { return _Country; }
            set
            {
                _Country = value;
                OnPropertyChanged("Country");
            }
        }

        private string _FName;
        public string FName
        {
            get { return _FName; }
            set
            {
                _FName = value;
                OnPropertyChanged("FName");
            }
        }

        public string SessionKey { get; set; }

        ADIFParser p;
        SignboardWindow signboard = null;

        private List<string> validSquares = new List<string>() { "H-03-AK", "H-04-AK", "H-05-AK", "H-06-AK", "J-03-AK", "J-04-AK", "J-05-AK", "J-06-AK", "J-07-AK", "K-03-AK", "K-04-AK", "K-05-AK", "K-06-AK", "L-03-AK", "L-04-AK", "L-05-AK", "M-04-AK", "B-21-AS", "C-18-AS", "C-19-AS", "C-20-AS", "C-21-AS", "D-16-AS", "D-17-AS", "D-18-AS", "D-19-AS", "D-20-AS", "D-21-AS", "E-16-AS", "E-17-AS", "E-18-AS", "E-19-AS", "E-20-AS", "E-21-AS", "F-17-AS", "F-18-AS", "F-19-AS", "F-20-AS", "F-21-AS", "G-19-AS", "G-20-AS", "G-21-AS", "A-21-AZ", "A-22-AZ", "A-23-AZ", "B-20-AZ", "B-21-AZ", "B-22-AZ", "B-23-AZ", "C-19-AZ", "C-20-AZ", "C-21-AZ", "Z-22-AZ", "Z-23-AZ", "H-18-BL", "H-19-BL", "J-18-BL", "J-19-BL", "K-17-BL", "K-18-BL", "K-19-BL", "K-20-BL", "K-21-BL", "L-17-BL", "L-18-BL", "L-19-BL", "L-20-BL", "L-21-BL", "M-17-BL", "M-18-BL", "A-22-BS", "A-23-BS", "A-24-BS", "A-25-BS", "A-26-BS", "A-27-BS", "B-21-BS", "B-22-BS", "B-23-BS", "B-24-BS", "B-25-BS", "B-26-BS", "B-27-BS", "B-28-BS", "B-29-BS", "C-21-BS", "C-22-BS", "C-23-BS", "C-24-BS", "C-25-BS", "C-26-BS", "C-27-BS", "C-28-BS", "C-29-BS", "C-30-BS", "C-31-BS", "C-32-BS", "C-33-BS", "D-20-BS", "D-21-BS", "D-22-BS", "D-23-BS", "D-24-BS", "D-25-BS", "D-26-BS", "D-27-BS", "D-28-BS", "D-29-BS", "D-30-BS", "D-31-BS", "D-32-BS", "D-33-BS", "D-34-BS", "D-35-BS", "E-21-BS", "E-22-BS", "E-23-BS", "E-24-BS", "E-25-BS", "E-26-BS", "E-27-BS", "E-28-BS", "E-29-BS", "E-30-BS", "E-31-BS", "E-32-BS", "E-33-BS", "E-34-BS", "E-35-BS", "E-36-BS", "E-37-BS", "E-38-BS", "F-21-BS", "F-22-BS", "F-23-BS", "F-24-BS", "F-25-BS", "F-26-BS", "F-27-BS", "F-28-BS", "F-29-BS", "F-30-BS", "F-31-BS", "F-32-BS", "F-33-BS", "F-34-BS", "F-35-BS", "F-36-BS", "F-37-BS", "F-38-BS", "F-39-BS", "F-40-BS", "F-41-BS", "F-42-BS", "F-43-BS", "G-22-BS", "G-23-BS", "G-24-BS", "G-25-BS", "G-26-BS", "G-27-BS", "G-28-BS", "G-29-BS", "G-30-BS", "G-31-BS", "G-32-BS", "G-33-BS", "G-34-BS", "G-35-BS", "G-36-BS", "G-37-BS", "G-38-BS", "G-39-BS", "G-40-BS", "G-41-BS", "G-42-BS", "G-43-BS", "H-22-BS", "H-23-BS", "H-24-BS", "H-25-BS", "H-26-BS", "H-27-BS", "H-28-BS", "H-29-BS", "H-30-BS", "H-31-BS", "H-32-BS", "H-33-BS", "H-34-BS", "H-35-BS", "H-36-BS", "H-37-BS", "H-38-BS", "H-39-BS", "H-40-BS", "H-41-BS", "J-22-BS", "J-23-BS", "J-24-BS", "J-25-BS", "J-26-BS", "J-27-BS", "J-28-BS", "J-29-BS", "J-30-BS", "J-31-BS", "J-32-BS", "J-33-BS", "J-34-BS", "J-35-BS", "J-36-BS", "J-37-BS", "K-21-BS", "K-22-BS", "K-23-BS", "K-24-BS", "K-25-BS", "K-26-BS", "K-27-BS", "K-28-BS", "K-29-BS", "K-30-BS", "L-20-BS", "L-21-BS", "L-22-BS", "L-23-BS", "L-24-BS", "L-25-BS", "L-26-BS", "L-27-BS", "L-28-BS", "M-25-BS", "M-26-BS", "F-21-HB", "F-22-HB", "G-19-HB", "G-20-HB", "G-21-HB", "G-22-HB", "H-18-HB", "H-19-HB", "H-20-HB", "H-21-HB", "H-22-HB", "J-19-HB", "J-20-HB", "J-21-HB", "J-22-HB", "K-19-HB", "K-20-HB", "K-21-HB", "K-22-HB", "L-21-HB", "F-09-HD", "F-10-HD", "G-06-HD", "G-07-HD", "G-08-HD", "G-09-HD", "G-10-HD", "H-07-HD", "H-08-HD", "H-09-HD", "H-10-HD", "H-11-HD", "J-09-HD", "J-10-HD", "G-06-HF", "G-07-HF", "H-05-HF", "H-06-HF", "H-07-HF", "H-08-HF", "J-05-HF", "J-06-HF", "J-07-HF", "N-01-HG", "N-03-HG", "N-04-HG", "N-05-HG", "O-00-HG", "O-01-HG", "O-02-HG", "O-03-HG", "O-04-HG", "O-05-HG", "O-06-HG", "O-07-HG", "P-00-HG", "P-01-HG", "P-02-HG", "P-03-HG", "P-04-HG", "P-05-HG", "P-06-HG", "P-07-HG", "Q-03-HG", "Q-04-HG", "Q-05-HG", "F-10-HS", "F-11-HS", "F-12-HS", "F-13-HS", "G-10-HS", "G-11-HS", "G-12-HS", "H-11-HS", "H-12-HS", "H-10-JN", "J-09-JN", "J-10-JN", "J-11-JN", "K-09-JN", "K-10-JN", "K-11-JN", "L-09-JN", "L-10-JN", "L-11-JN", "L-12-JN", "M-10-JN", "M-11-JN", "M-12-JN", "F-17-JS", "F-18-JS", "F-19-JS", "G-16-JS", "G-17-JS", "G-18-JS", "G-19-JS", "H-16-JS", "H-17-JS", "H-18-JS", "H-19-JS", "J-16-JS", "J-17-JS", "J-18-JS", "K-16-JS", "K-17-JS", "K-18-JS", "L-05-KT", "L-06-KT", "L-07-KT", "M-05-KT", "M-06-KT", "M-07-KT", "M-08-KT", "N-04-KT", "N-05-KT", "N-06-KT", "N-07-KT", "N-08-KT", "O-05-KT", "O-06-KT", "O-07-KT", "F-12-PT", "F-13-PT", "F-14-PT", "F-15-PT", "G-12-PT", "G-13-PT", "G-14-PT", "G-15-PT", "H-12-PT", "H-14-PT", "H-15-PT", "G-15-RA", "G-16-RA", "G-17-RA", "H-14-RA", "H-15-RA", "H-16-RA", "H-17-RA", "J-14-RA", "J-15-RA", "J-16-RA", "J-17-RA", "K-14-RA", "K-15-RA", "K-16-RA", "K-17-RA", "L-14-RA", "L-15-RA", "L-16-RA", "L-17-RA", "D-16-RH", "D-17-RH", "E-15-RH", "E-16-RH", "E-17-RH", "E-18-RH", "F-15-RH", "F-16-RH", "F-17-RH", "F-18-RH", "F-15-RM", "F-16-RM", "F-17-RM", "G-15-RM", "G-16-RM", "G-17-RM", "H-15-RM", "H-16-RM", "J-11-SM", "J-12-SM", "J-13-SM", "K-11-SM", "K-12-SM", "K-13-SM", "K-14-SM", "L-12-SM", "L-13-SM", "L-14-SM", "E-13-TA", "E-14-TA", "E-15-TA", "F-13-TA", "F-14-TA", "F-15-TA", "G-12-TK", "G-13-TK", "G-14-TK", "H-10-TK", "H-11-TK", "H-12-TK", "H-13-TK", "H-14-TK", "J-10-TK", "J-11-TK", "J-12-TK", "J-13-TK", "J-14-TK", "K-13-TK", "K-14-TK", "L-11-YN", "L-12-YN", "L-13-YN", "L-14-YN", "L-15-YN", "L-16-YN", "L-17-YN", "L-19-YN", "L-20-YN", "L-21-YN", "M-10-YN", "M-11-YN", "M-12-YN", "M-13-YN", "M-14-YN", "M-15-YN", "M-16-YN", "M-17-YN", "M-18-YN", "M-19-YN", "N-11-YN", "N-12-YN", "N-13-YN", "N-14-YN", "N-15-YN", "N-16-YN", "N-17-YN", "N-18-YN", "H-07-YZ", "H-08-YZ", "H-09-YZ", "J-06-YZ", "J-07-YZ", "J-08-YZ", "J-09-YZ", "K-06-YZ", "K-07-YZ", "K-08-YZ", "K-09-YZ", "L-06-YZ", "L-07-YZ", "L-08-YZ", "L-09-YZ", "L-10-YZ", "M-08-YZ", "M-09-YZ", "M-10-YZ", "M-11-YZ", "N-08-YZ", "N-09-YZ", "N-10-YZ", "N-11-YZ", "L-03-ZF", "L-04-ZF", "L-05-ZF", "M-02-ZF", "M-03-ZF", "M-04-ZF", "M-05-ZF", "N-01-ZF", "N-02-ZF", "N-03-ZF", "N-04-ZF", "N-05-ZF", "O-01-ZF", "O-02-ZF", "O-03-ZF" };

        public MainWindow()
        {
            InitializeComponent();
            Version = Assembly.GetExecutingAssembly().GetName().Version.ToString();
            QRZBtn.Visibility = Properties.Settings.Default.show_qrz ? System.Windows.Visibility.Visible : System.Windows.Visibility.Hidden;
            TB_Exchange.IsEnabled = Properties.Settings.Default.validation_enabled;

            try
            {
                dal = new DataAccess();
            }
            catch (Exception e)
            {
                MessageBox.Show("Failed to connect to DB: " + e.Message);
                throw;
            }

            TB_MyCallsign.Focus();

            Left = (System.Windows.SystemParameters.PrimaryScreenWidth - Width) / 2;
            Top = (System.Windows.SystemParameters.PrimaryScreenHeight - Height) / 2;

            QSOTimeStamp.Value = DateTime.UtcNow;
            Qsos = dal.GetAllQSOs();
            Qsos.CollectionChanged += Qsos_CollectionChanged;
            DataContext = Qsos;

            UpdateNumOfQSOs();
            TB_Frequency_TextChanged(null, null);
            LoginToQRZ();
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
            //TB_Frequency.IsEnabled = !(TB_Frequency.IsEnabled);
            //CB_Mode.IsEnabled = !(CB_Mode.IsEnabled);
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
            qso.mode = CB_Mode.Text;
            qso.exchange = TB_Exchange.Text;
            qso.frequency = TB_Frequency.Text.Replace(",","");
            qso.my_callsign = TB_MyCallsign.Text;
            qso.my_square = TB_MyGrid.Text;
            qso.rst_rcvd = TB_RSTRcvd.Text;
            qso.rst_sent = TB_RSTSent.Text;
            qso.timestamp = QSOTimeStamp.Value.Value;
            //if (Properties.Settings.Default.live_log) PostQSO(qso);
            QSO q = dal.Insert(qso);
            Qsos.Insert(0, q);
            ClearBtn_Click(null, null);
            UpdateNumOfQSOs();
        }

        private void PostQSO(QSO qso)
        {
            //************************************************** ASYNC ********************************************//
            using (WebClient client = new WebClient())
            {
                client.UploadValuesAsync(new Uri("http://www.iarc.org/xmas/Server/AddLog.php"), new NameValueCollection()
                    {
                        { "insertlog", GenerateInsert(qso) }
                    });
            }

            //************************************************** SYNC ********************************************//
            //using (WebClient client = new WebClient())
            //{
            //    byte[] response =
            //    client.UploadValues("http://iarc.org/xmas/Server/AddLog.php", new NameValueCollection()
            //        {
            //            { "insertlog", GenerateInsert(qso) }
            //        });
            //    string result = System.Text.Encoding.UTF8.GetString(response);
            //    MessageBox.Show(result);
            //}
        }
        private string GenerateInsert(QSO qso)
        {
            StringBuilder sb = new StringBuilder("INSERT IGNORE INTO `log` ", 500);
            sb.Append("(`my_call`, `my_square`, `mode`, `frequency`, `callsign`, `timestamp`, `rst_sent`, `rst_rcvd`, `exchange`, `comment`) VALUES ");
            sb.Append("(");
            sb.Append("'"); sb.Append(qso.my_callsign); sb.Append("',");
            sb.Append("'"); sb.Append(qso.my_square); sb.Append("',");
            sb.Append("'"); sb.Append(qso.mode); sb.Append("',");
            sb.Append("'"); sb.Append(qso.frequency); sb.Append("',");
            sb.Append("'"); sb.Append(qso.dx_callsign); sb.Append("',");
            sb.Append("'"); sb.Append(qso.timestamp); sb.Append("',");
            sb.Append("'"); sb.Append(qso.rst_sent); sb.Append("',");
            sb.Append("'"); sb.Append(qso.rst_rcvd); sb.Append("',");
            sb.Append("'"); sb.Append(qso.exchange); sb.Append("',");
            sb.Append("'"); sb.Append(qso.comment); sb.Append("')");
            string result = sb.ToString();
            return result;
        }

        private void QRZBtn_Click(object sender, MouseButtonEventArgs e)
        {
            string url = "http://www.qrz.com";
            if (!string.IsNullOrWhiteSpace(TB_DXCallsign.Text))
                url += "/db/" + TB_DXCallsign.Text;

            try
            {
                System.Diagnostics.Process.Start(url);
            }
            catch (Exception)
            {
                MessageBox.Show("Please install 'Chrome' and try again");
            }
            
        }

        private void ClearBtn_Click(object sender, RoutedEventArgs e)
        {
            //TB_Frequency.Text = string.Empty;
            TB_Exchange.Mask = "";
            TB_Exchange.Text = "000";
            TB_Exchange.Mask = "000";
            TB_Exchange.PromptChar = '0';

            TB_DXCallsign.Text = string.Empty;
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
            parseAdif();
            NumOfQSOs = dal.GetQsoCount().ToString();
            Score = p.Result.ToString();
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
                    MessageBox.Show("File created successfully!");
                }
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
            //adif.AppendLine("<PROGRAMVERSION:15>Version 1.0.0.0 ");
            adif.AppendFormat("<PROGRAMVERSION:{0}>{1} ", Version.Length, Version);
            adif.AppendLine();
            adif.AppendLine("<EOH>");
            adif.AppendLine();

            foreach (QSO qso in qso_list)
            {
                string date = qso.timestamp.ToString("yyyyMMdd");
                string time = qso.timestamp.ToString("HHmmss");

                adif.AppendFormat("<call:{0}>{1} ", qso.dx_callsign.Length, qso.dx_callsign);
                adif.AppendFormat("<srx_string:{0}>{1} ", qso.exchange.Length, qso.exchange);
                adif.AppendFormat("<freq:{0}>{1} ", qso.frequency.Length, qso.frequency);
                adif.AppendFormat("<mode:{0}>{1} ", qso.mode.Length, qso.mode);
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


            //if (string.IsNullOrWhiteSpace(TB_Exchange.Text) )
            //{
            //    allOK = false;
            //    TB_Exchange.BorderBrush = System.Windows.Media.Brushes.Red;
            //}
            //else
            //{
            //    TB_Exchange.BorderBrush = System.Windows.Media.Brushes.LightGray;
            //}

            if (Properties.Settings.Default.validation_enabled)
            {
                if (!string.IsNullOrWhiteSpace(TB_Exchange.Text) && TB_Exchange.Text != "000")
                {
                    if (TB_DXCallsign.Text.StartsWith("4X") || TB_DXCallsign.Text.StartsWith("4Z"))
                    {
                        if (validSquares.Contains(TB_Exchange.Text))
                        {
                            TB_Exchange.BorderBrush = System.Windows.Media.Brushes.LightGray;
                        }
                        else
                        {
                            allOK = false;
                            TB_Exchange.BorderBrush = System.Windows.Media.Brushes.Red;
                        }
                    }
                    else
                    {
                        TB_Exchange.BorderBrush = System.Windows.Media.Brushes.LightGray;
                    }
                }
                else
                {
                    allOK = false;
                    TB_Exchange.BorderBrush = System.Windows.Media.Brushes.Red;
                }
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

            if (string.IsNullOrWhiteSpace(TB_MyGrid.Text) || !validSquares.Contains(TB_MyGrid.Text))
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

        private void MyScoreMenuItem_Click(object sender, RoutedEventArgs e)
        {
            parseAdif();
            MessageBox.Show("Your score is: " + p.Result.ToString());
        }

        private void parseAdif()
        {
            string adif = GenerateAdif(dal.GetAllQSOs());
            p = new ADIFParser(adif, ADIFParser.Operator.Israeli);
            p.Parse();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            StartOmniRig();
        }

        private void Window_Closed(object sender, EventArgs e)
        {
            Properties.Settings.Default.Save();
        }

        private void TB_Frequency_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (TB_Band != null)
            {
                string band = ADIFParser.convertFreqToBand(TB_Frequency.Text.Replace(",", ""));
                if (!string.IsNullOrWhiteSpace(band))
                {
                    TB_Band.Text = ADIFParser.convertFreqToBand(TB_Frequency.Text.Replace(",", "")) + "M";
                }
                else
                {
                    TB_Band.Text = string.Empty;
                }
            }
                
        }

        private void SignboardMenuItem_Click(object sender, RoutedEventArgs e)
        {
            signboard = new SignboardWindow(TB_MyCallsign.Text, TB_MyGrid.Text);
            signboard.Show();
        }

        private void TB_MyCallsign_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (signboard != null)
            {
                signboard.signboardData.Callsign = TB_MyCallsign.Text;
            }
        }

        private void ConnectToQRZ_Click(object sender, RoutedEventArgs e)
        {
            LoginToQRZ();
        }

        private void TB_MyGrid_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (signboard != null)
            {
                signboard.signboardData.Square = TB_MyGrid.Text;
            }
        }

        private void TB_DXCallsign_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (TB_DXCallsign.Text.StartsWith("4X") || TB_DXCallsign.Text.StartsWith("4Z"))
            {
                TB_Exchange.Mask = "";
                TB_Exchange.Text = "";
                TB_Exchange.Mask = "L-00-LL";
                TB_Exchange.PromptChar = '#';
            }
            else
            {
                TB_Exchange.Mask = "";
                TB_Exchange.Text = "";
                TB_Exchange.Mask = "000";
                TB_Exchange.PromptChar = '0';
            }
        }
        private void TB_DXCallsign_LostFocus(object sender, RoutedEventArgs e)
        {
            getQrzData();
        }
        private bool LoginToQRZ()
        {
            if (string.IsNullOrWhiteSpace(Properties.Settings.Default.qrz_username) || string.IsNullOrWhiteSpace(Properties.Settings.Default.qrz_password))
            {
                SessionKey = "";
                return false;
            }
            try
            {
                WebRequest request = WebRequest.Create("http://xmldata.qrz.com/xml/current/?username=" + Properties.Settings.Default.qrz_username + ";password=" + Properties.Settings.Default.qrz_password);
                WebResponse response = request.GetResponse();
                string status = ((HttpWebResponse)response).StatusDescription;
                Stream dataStream = response.GetResponseStream();
                StreamReader reader = new StreamReader(dataStream);
                string responseFromServer = reader.ReadToEnd();

                XElement xml = XElement.Parse(responseFromServer);
                XElement element = xml.Elements().FirstOrDefault();
                SessionKey = element.Elements().FirstOrDefault().Value;

                reader.Close();
                response.Close();
                return true;
            }
            catch (Exception)
            {
                SessionKey = "";
                return false;
            }
        }

        private void getQrzData()
        {
            if (!string.IsNullOrWhiteSpace(SessionKey) && !string.IsNullOrWhiteSpace(TB_DXCallsign.Text))
            {
                try
                {
                    string baseRequest = "http://xmldata.qrz.com/xml/current/?s=";
                    WebRequest request = WebRequest.Create(baseRequest + SessionKey + ";callsign=" + TB_DXCallsign.Text);
                    WebResponse response = request.GetResponse();
                    string status = ((HttpWebResponse)response).StatusDescription;
                    Stream dataStream = response.GetResponseStream();
                    StreamReader reader = new StreamReader(dataStream);
                    string responseFromServer = reader.ReadToEnd();
                    XDocument xDoc = XDocument.Parse(responseFromServer);
                    IEnumerable<XElement> country = xDoc.Root.Descendants(xDoc.Root.GetDefaultNamespace‌​() + "country");
                    if (country.Count() > 0)
                        Country = country.FirstOrDefault().Value;
                    else
                        Country = "";

                    IEnumerable<XElement> fname = xDoc.Root.Descendants(xDoc.Root.GetDefaultNamespace‌​() + "fname");
                    if (fname.Count() > 0)
                        FName = fname.FirstOrDefault().Value;
                    else
                        FName = "";

                    IEnumerable<XElement> lname = xDoc.Root.Descendants(xDoc.Root.GetDefaultNamespace‌​() + "name");
                    if (lname.Count() > 0)
                        FName += " " + lname.FirstOrDefault().Value;
                }
                catch (Exception)
                {
                    Country = "";
                    FName = "";
                }
            }
        }




        //-------------------------------------- OmniRig Section ---------------------------------------------//
        #region OmniRig

        #region Property


        /// <summary>
        /// RX
        /// </summary>
        public const string FLD_RX = "RX";

        /// <summary>
        /// RX
        /// </summary>
        private string mRX;

        /// <summary>
        /// RX
        /// </summary>
        public string RX
        {
            get
            {
                return mRX;
            }
            set
            {
                mRX = value;
                OnPropertyChanged(FLD_RX);
            }
        }

        /// <summary>
        /// TX
        /// </summary>
        public const string FLD_TX = "TX";

        /// <summary>
        /// TX
        /// </summary>
        private string mTX;

        /// <summary>
        /// TX
        /// </summary>
        public string TX
        {
            get
            {
                return mTX;
            }
            set
            {
                mTX = value;
                OnPropertyChanged(FLD_TX);
            }
        }

        /// <summary>
        /// Frequency
        /// </summary>
        public const string FLD_Frequency = "Frequency";

        /// <summary>
        /// Frequency
        /// </summary>
        private string mFrequency = "14220000";

        /// <summary>
        /// Frequency
        /// </summary>
        public string Frequency
        {
            get
            {
                return mFrequency;
            }
            set
            {
                mFrequency = value;
                OnPropertyChanged(FLD_Frequency);
            }
        }

        /// <summary>
        /// Status
        /// </summary>
        public const string FLD_Status = "Status";

        /// <summary>
        /// Status
        /// </summary>
        private string mStatus;

        /// <summary>
        /// Status
        /// </summary>
        public string Status
        {
            get
            {
                return mStatus;
            }
            set
            {
                mStatus = value;
                OnPropertyChanged(FLD_Status);
            }
        }


        /// <summary>
        /// Mode
        /// </summary>
        public const string FLD_Mode = "Mode";

        /// <summary>
        /// Mode
        /// </summary>
        private string mMode;

        /// <summary>
        /// Mode
        /// </summary>
        public string Mode
        {
            get
            {
                return mMode;
            }
            set
            {
                mMode = value;
                OnPropertyChanged(FLD_Mode);
            }
        }

        #endregion
        #region Constants
        // Constants for enum RigParamX
        private const int PM_UNKNOWN = 0x00000001;
        private const int PM_FREQ = 0x00000002;
        private const int PM_FREQA = 0x00000004;
        private const int PM_FREQB = 0x00000008;
        private const int PM_PITCH = 0x00000010;
        private const int PM_RITOFFSET = 0x00000020;
        private const int PM_RIT0 = 0x00000040;
        private const int PM_VFOAA = 0x00000080;
        private const int PM_VFOAB = 0x00000100;
        private const int PM_VFOBA = 0x00000200;
        private const int PM_VFOBB = 0x00000400;
        private const int PM_VFOA = 0x00000800;
        private const int PM_VFOB = 0x00001000;
        private const int PM_VFOEQUAL = 0x00002000;
        private const int PM_VFOSWAP = 0x00004000;
        private const int PM_SPLITON = 0x00008000;
        private const int PM_SPLITOFF = 0x00010000;
        private const int PM_RITON = 0x00020000;
        private const int PM_RITOFF = 0x00040000;
        private const int PM_XITON = 0x00080000;
        private const int PM_XITOFF = 0x00100000;
        private const int PM_RX = 0x00200000;
        private const int PM_TX = 0x00400000;
        private const int PM_CW_U = 0x00800000;
        private const int PM_CW_L = 0x01000000;
        private const int PM_SSB_U = 0x02000000;
        private const int PM_SSB_L = 0x04000000;
        private const int PM_DIG_U = 0x08000000;
        private const int PM_DIG_L = 0x10000000;
        private const int PM_AM = 0x20000000;
        private const int PM_FM = 0x40000000;

        // Constants for enum RigStatusX
        private const int ST_NOTCONFIGURED = 0x00000000;
        private const int ST_DISABLED = 0x00000001;
        private const int ST_PORTBUSY = 0x00000002;
        private const int ST_NOTRESPONDING = 0x00000003;
        private const int ST_ONLINE = 0x00000004;

        #endregion

        /// <summary>
        /// The omni rig engine
        /// </summary>
        OmniRig.OmniRigX OmniRigEngine;
        /// <summary>
        /// The rig
        /// </summary>
        OmniRig.RigX Rig;
        /// <summary>
        /// Our rig no
        /// </summary>
        int OurRigNo;
        /// <summary>
        /// The events subscribed
        /// </summary>
        private bool EventsSubscribed = false;

        Thread thread1;
        Thread thread2;

        private void StartOmniRig()
        {
            try
            {
                if (OmniRigEngine != null)
                {
                    MessageBox.Show("OmniRig Is run");
                }
                else
                {
                    OmniRigEngine = (OmniRig.OmniRigX)Activator.CreateInstance(Type.GetTypeFromProgID("OmniRig.OmniRigX"));
                    // we want OmniRig interface V.1.1 to 1.99
                    // as V2.0 will likely be incompatible  with 1.x
                    if (OmniRigEngine.InterfaceVersion < 0x101 && OmniRigEngine.InterfaceVersion > 0x299)
                    {
                        OmniRigEngine = null;
                        MessageBox.Show("OmniRig Is Not installed Or has a wrong version number");
                    }
                    SubscribeToEvents();
                    SelectRig(1);
                }
            }
            catch (Exception ex)
            {
                //Mouse.OverrideCursor = null;
                //MessageBox.Show(ex.Message);
                //throw;
            }
        }

        private void SubscribeToEvents()
        {
            if (!EventsSubscribed)
            {
                EventsSubscribed = true;
                OmniRigEngine.StatusChange += OmniRigEngine_StatusChange;
                OmniRigEngine.ParamsChange += OmniRigEngine_ParamsChange;
            }
        }
        private void SelectRig(int NewRigNo)
        {
            if (OmniRigEngine == null)
            {
                return;
            }
            OurRigNo = NewRigNo;
            switch (NewRigNo)
            {
                case 1:
                    Rig = OmniRigEngine.Rig1;
                    break;
                case 2:
                    Rig = OmniRigEngine.Rig2;
                    break;
            }
            ShowRigStatus();
            ShowRigParams();
        }

        //OmniRig ParamsChange events
        private void OmniRigEngine_ParamsChange(int RigNumber, int Params)
        {
            if (RigNumber == OurRigNo)
            {
                thread1 = new Thread(new ThreadStart(ShowRigParams));
                thread1.Name = "RigParams";
                //Avvia il primo thread
                thread1.Start();
            }

        }


        //OmniRig StatusChange events
        private void OmniRigEngine_StatusChange(int RigNumber)
        {
            if (RigNumber == OurRigNo)
            {
                thread2 = new Thread(new ThreadStart(ShowRigStatus));
                thread2.Name = "RigStatus";
                //Avvia il secondo thread
                thread2.Start();
            }
        }

        private void ShowRigStatus()
        {
            if (Rig == null)
            {
            }
            else
            {
                Status = Rig.StatusStr;
            }
        }

        private void ShowRigParams()
        {
            if (Rig == null)
            {
                return;
            }
            RX = Rig.GetRxFrequency().ToString();
            TX = Rig.GetTxFrequency().ToString();
            Frequency = Rig.Freq.ToString();
            switch (Rig.Mode)
            {
                case (OmniRig.RigParamX)PM_CW_L:
                    //cmbMode.Text = cmbMode.Items[1].ToString();
                    Mode = "CW";
                    break;
                case (OmniRig.RigParamX)PM_CW_U:
                    //cmbMode.Text = cmbMode.Items[0].ToString();
                    Mode = "CW";
                    break;
                case (OmniRig.RigParamX)PM_SSB_L:
                    //cmbMode.Text = cmbMode.Items[3].ToString();
                    Mode = "SSB";
                    break;
                case (OmniRig.RigParamX)PM_SSB_U:
                    // cmbMode.Text = cmbMode.Items[2].ToString();
                    Mode = "SSB";
                    break;
                case (OmniRig.RigParamX)PM_FM:
                    // cmbMode.Text = cmbMode.Items[7].ToString();
                    Mode = "FM";
                    break;
                case (OmniRig.RigParamX)PM_AM:
                    // cmbMode.Text = cmbMode.Items[7].ToString();
                    Mode = "AM";
                    break;
                case (OmniRig.RigParamX)PM_DIG_L:
                    // cmbMode.Text = cmbMode.Items[7].ToString();
                    Mode = "DIGI";
                    break;
                case (OmniRig.RigParamX)PM_DIG_U:
                    // cmbMode.Text = cmbMode.Items[7].ToString();
                    Mode = "DIGI";
                    break;
                default:
                    Mode = "Other";
                    break;
            }
        }




        #endregion

        
    }
}
