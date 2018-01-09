using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.Win32;
using System.Collections.Specialized;
using System.Threading;
using System.Net;
using System.Xml.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using DXCCManager;
using HolyParser;
using System.Diagnostics;
using System.Net.Cache;
using System.Globalization;
using System.Drawing.Printing;
using Blue.Windows;

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
        RadioEntityResolver rem;

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

        private string _NumOfGrids;
        public string NumOfGrids
        {
            get { return _NumOfGrids; }
            set
            {
                _NumOfGrids = value;
                OnPropertyChanged("NumOfGrids");
            }
        }

        private string _IsOmniRigEnabled;
        public string IsOmniRigEnabled
        {
            get { return _IsOmniRigEnabled; }
            set
            {
                _IsOmniRigEnabled = value;
                OnPropertyChanged("IsOmniRigEnabled");
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

        private string _SessionKey;
        public string SessionKey
        {
            get { return _SessionKey; }
            set
            {
                _SessionKey = value;
            }
        }

        HolyLogParser p;
        SignboardWindow signboard = null;
        MatrixWindow matrix = null;

        BackgroundWorker EntityResolverWorker;

        private StickyWindow _stickyWindow;

        private State state = State.New;

        QSO QsoToUpdate;

        public MainWindow()
        {
            InitializeComponent();
            this.Loaded += MainWindow_Loaded; ;
            this.PropertyChanged += MainWindow_PropertyChanged;

            ManualModeMenuItem.Header = Properties.Settings.Default.isManualMode ? "Manual Mode - On" : "Manual Mode - Off";


            EntityResolverWorker = new BackgroundWorker();
            EntityResolverWorker.DoWork += EntityResolverWorker_DoWork;
            rem = new RadioEntityResolver();
            EntityResolverWorker.RunWorkerAsync();

            QRZBtn.Visibility = Properties.Settings.Default.show_qrz ? System.Windows.Visibility.Visible : System.Windows.Visibility.Hidden;
            TB_Exchange.IsEnabled = Properties.Settings.Default.validation_enabled;

            TB_MyCallsign.IsEnabled = !Properties.Settings.Default.isLocked;
            

            if (TB_MyGrid.IsEnabled) Lock_Btn.Opacity = 1;
            else Lock_Btn.Opacity = 0.5;

            if (!(TB_MyCallsign.Text.StartsWith("4X") || TB_MyCallsign.Text.StartsWith("4Z")))
            {
                TB_MyGrid.Clear();
                TB_MyGrid.IsEnabled = false;
            }
            else
            {
                TB_MyGrid.IsEnabled = true;
                TB_MyGrid.Text = Properties.Settings.Default.my_square;
            }
            TB_MyGrid.IsEnabled = !Properties.Settings.Default.isLocked;

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

            TP_Date.Value = DateTime.UtcNow;
            TP_Time.Value = DateTime.UtcNow;
            Qsos = new ObservableCollection<QSO>();
            Qsos = dal.GetAllQSOs();
            Qsos.CollectionChanged += Qsos_CollectionChanged;
            DataContext = Qsos;

            UpdateNumOfQSOs();
            TB_Frequency_TextChanged(null, null);
            Helper.LoginToQRZ(out _SessionKey);
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            _stickyWindow = new StickyWindow(this);
            _stickyWindow.StickToScreen = true;
            _stickyWindow.StickToOther = true;
            _stickyWindow.StickOnResize = true;
            _stickyWindow.StickOnMove = true;
        }

        private void MainWindow_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case FLD_Mode:
                    if (mMode == "SSB")
                    {
                        TB_RSTSent.Text = "59";
                        TB_RSTRcvd.Text = "59";
                    }
                    else
                    {
                        TB_RSTSent.Text = "599";
                        TB_RSTRcvd.Text = "599";
                    }
                    UpdateMatrixDup();
                    break;
                default:
                    break;
            }
        }

        private void EntityResolverWorker_DoWork(object sender, DoWorkEventArgs e)
        {
            rem.GetEntity("kuku");
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
            Properties.Settings.Default.isLocked = !Properties.Settings.Default.isLocked;
            TB_MyCallsign.IsEnabled = !Properties.Settings.Default.isLocked;
            TB_MyGrid.IsEnabled = !Properties.Settings.Default.isLocked;
            //TB_Frequency.IsEnabled = !(TB_Frequency.IsEnabled);
            //CB_Mode.IsEnabled = !(CB_Mode.IsEnabled);
            if (TB_MyGrid.IsEnabled) ((Image)sender).Opacity = 1;
            else ((Image)sender).Opacity = 0.5;
        }

        private void RefreshDateTime_Btn_MouseUp(object sender, MouseButtonEventArgs e)
        {
            TP_Date.Value = DateTime.UtcNow;
            TP_Time.Value = DateTime.UtcNow;
        }

        private void AddBtn_Click(object sender, RoutedEventArgs e)
        {
            if (!Validate()) return;
            if (state == State.New)
            {
                QSO qso = new QSO();
                qso.Comment = TB_Comment.Text;
                qso.DXCall = TB_DXCallsign.Text;
                qso.Mode = Mode;
                qso.SRX = TB_Exchange.Text;
                qso.Freq = TB_Frequency.Text.Replace(",", "");
                qso.Band = HolyLogParser.convertFreqToBand(TB_Frequency.Text.Replace(",", ""));
                qso.Country = Country;
                qso.Name = FName;
                qso.MyCall = TB_MyCallsign.Text;
                qso.STX = TB_MyGrid.Text.Replace("-", "");
                qso.RST_RCVD = TB_RSTRcvd.Text;
                qso.RST_SENT = TB_RSTSent.Text;
                qso.Date = TP_Date.Value.Value.ToShortDateString();
                qso.Time = TP_Time.Value.Value.ToShortTimeString();
                //if (Properties.Settings.Default.live_log) PostQSO(qso);
                QSO q = dal.Insert(qso);
                Qsos.Insert(0, q);
            }
            else if (state == State.Edit)
            {
                QsoToUpdate.Comment = TB_Comment.Text;
                QsoToUpdate.DXCall = TB_DXCallsign.Text;
                QsoToUpdate.Mode = Mode;
                QsoToUpdate.SRX = TB_Exchange.Text;
                QsoToUpdate.Freq = TB_Frequency.Text.Replace(",", "");
                QsoToUpdate.Band = HolyLogParser.convertFreqToBand(TB_Frequency.Text.Replace(",", ""));
                QsoToUpdate.Country = Country;
                QsoToUpdate.Name = FName;
                QsoToUpdate.MyCall = TB_MyCallsign.Text;
                QsoToUpdate.STX = TB_MyGrid.Text.Replace("-", "");
                QsoToUpdate.RST_RCVD = TB_RSTRcvd.Text;
                QsoToUpdate.RST_SENT = TB_RSTSent.Text;
                QsoToUpdate.Date = TP_Date.Value.Value.ToShortDateString();
                QsoToUpdate.Time = TP_Time.Value.Value.ToShortTimeString();
                dal.Update(QsoToUpdate);
                QSO q = Qsos.FirstOrDefault(p => p.id == QsoToUpdate.id);
                if (q != null)
                {
                    q.Comment = QsoToUpdate.Comment;
                    q.DXCall = QsoToUpdate.DXCall;
                    q.Mode = QsoToUpdate.Mode;
                    q.SRX = QsoToUpdate.SRX;
                    q.Freq = QsoToUpdate.Freq;
                    q.Band = QsoToUpdate.Band;
                    q.Country = QsoToUpdate.Country;
                    q.Name = QsoToUpdate.Name;
                    q.MyCall = QsoToUpdate.MyCall;
                    q.STX = QsoToUpdate.STX;
                    q.RST_RCVD = QsoToUpdate.RST_RCVD;
                    q.RST_SENT = QsoToUpdate.RST_SENT;
                    q.Date = QsoToUpdate.Date;
                    q.Time = QsoToUpdate.Time;
                    QSODataGrid.Items.Refresh();
                }
            }
            ClearBtn_Click(null, null);
            UpdateNumOfQSOs();
            ClearMatrix();
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
            TB_DXCallsign.Clear();
            TB_Exchange.Clear();

            if (mMode == "SSB")
            {
                TB_RSTSent.Text = "59";
                TB_RSTRcvd.Text = "59";
            }
            else
            {
                TB_RSTSent.Text = "599";
                TB_RSTRcvd.Text = "599";
            }

            TB_Comment.Clear();
            FName = string.Empty;
            Country = string.Empty;
            if (!Properties.Settings.Default.isManualMode)
                RefreshDateTime_Btn_MouseUp(null, null);
            TB_DXCallsign.Focus();
            ClearMatrix();
            state = State.New;
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F1)
            {
                AddBtn_Click(null, null);
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
            NumOfGrids = dal.GetGridCount().ToString();
            Score = p.Result.ToString();
        }

        private void ExitMenuItem_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }

        private void ImportAdifMenuItem_Click(object sender, RoutedEventArgs e)
        {
            CultureInfo provider = CultureInfo.InvariantCulture;
            OpenFileDialog openFileDialog = new OpenFileDialog();
            openFileDialog.Filter = "ADIF files (*.adi)|*.adi";

            if (openFileDialog.ShowDialog() == true)
            {
                string RawAdif = File.ReadAllText(openFileDialog.FileName);
                p = new HolyLogParser(RawAdif, (HolyLogParser.IsIsraeliStation(TB_MyCallsign.Text)) ? HolyLogParser.Operator.Israeli : HolyLogParser.Operator.Foreign);
                p.Parse();
                List<HolyParser.QSO> rawQSOList = p.GetRawQSO();
                foreach (var rq in rawQSOList)
                {
                    QSO q = dal.Insert(rq);
                    Qsos.Insert(0, q);
                    UpdateNumOfQSOs();
                }
            }
        }

        private void ExpotMenuItem_Click(object sender, RoutedEventArgs e)
        {
            string adif = Services.GenerateAdif(dal.GetAllQSOs());
            // Displays a SaveFileDialog so the user can save the Image
            // assigned to Button2.
            SaveFileDialog saveFileDialog1 = new SaveFileDialog();
            saveFileDialog1.Filter = "ADIF File|*.adi";
            saveFileDialog1.Title = "Export ADIF";
            saveFileDialog1.ShowDialog();

            //PrintDocument p = new PrintDocument();
            //p.PrintPage += delegate (object sender1, PrintPageEventArgs e1)
            //{
            //    e1.Graphics.DrawString(adif, new System.Drawing.Font("Times New Roman", 12), new System.Drawing.SolidBrush(System.Drawing.Color.Black), new System.Drawing.RectangleF(0, 0, p.DefaultPageSettings.PrintableArea.Width, p.DefaultPageSettings.PrintableArea.Height));
            //};
            //try
            //{
            //    p.Print();
            //}
            //catch (Exception ex)
            //{
            //    throw new Exception("Exception Occured While Printing", ex);
            //}


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

        private void ExpotCSVMenuItem_Click(object sender, RoutedEventArgs e)
        {
            string adif = Services.GenerateCSV(dal.GetAllQSOs());

            // Displays a SaveFileDialog so the user can save the Image
            // assigned to Button2.
            SaveFileDialog saveFileDialog1 = new SaveFileDialog();
            saveFileDialog1.Filter = "CSV File|*.csv";
            saveFileDialog1.Title = "Export CSV";
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

        private void UploadMenuItem_Click(object sender, RoutedEventArgs e)
        {
            LogUploadWindow l = new LogUploadWindow();
            l.SendLog += L_SendLog;
            l.Show();
        }

        private async void L_SendLog(object sender, EventArgs e)
        {
            if (Qsos.Count == 0)
            {
                System.Windows.Forms.MessageBox.Show("You can not upload empty log");
                return;
            }
            LogUploadWindow w = (LogUploadWindow)sender;
            string bareCallsign = Services.getBareCallsign(w.Callsign);
            string country = Services.getHamQth(bareCallsign);
            string AddParticipant_result = await AddParticipant(bareCallsign, w.CategoryOperator, w.CategoryMode, w.CategoryPower, w.Email, w.Handle, country);
            string UploadLogToIARC_result = await UploadLogToIARC();

            StringBuilder sb = new StringBuilder(200);
            sb.Append("Dear ").Append(w.Handle).Append(",<br><br>");
            sb.Append("Thank you for participating in the 'Holyland Contest' and for sending the log.<br>");
            sb.Append("Please be patient, we will publish the result as soon as all the logs are received.<br><br>");
            sb.Append("73 and Best Regards,<br>");
            sb.Append("Gil, 4Z1KD.<br>");
            sb.Append("Online Log Manager.<br><br><br>");
            sb.Append("http://www.iarc.org/iarc/#HolylandResults");

            string Sendemail_result = await Services.SendMail("info@iarc.org", w.Email, "Holyland Contest - your log was received", sb.ToString());
            w.Close();
            System.Windows.Forms.MessageBox.Show(UploadLogToIARC_result);
        }

        private async Task<string> AddParticipant(string callsign, string category_op, string category_mode, string category_power, string email, string name, string country)
        {
            //string delete = "DELETE FROM `log` WHERE `my_call`= '" + callsign + "';";
            string insert = "INSERT  INTO  `participants` (`callsign`,`category_op`,`category_mode`,`category_power`,`email`,`name`,`country`,`year`,`qsos`,`points`) VALUES ('" + callsign + "','" + category_op + "','" + category_mode + "','" + category_power + "','" + email + "','" + name + "','" + country + "','" + DateTime.UtcNow.Year + "','" + Qsos.Count + "','" + Score + "') ON DUPLICATE KEY UPDATE `category_op`= '" + category_op + "', `category_mode`= '" + category_mode + "',`category_power`= '" + category_power + "',`email`= '" + email + "',`name`= '" + name + "',`year`= '" + DateTime.UtcNow.Year + "',`qsos`= '" + Qsos.Count + "',`points`= '" + Score + "'";
            //************************************************** ASYNC ********************************************//
            //string deleteResponse = await ExecuteQuery(delete);
            string insertResponse = await ExecuteQuery(insert);
            return insertResponse;
        }

        private async Task<string> ExecuteQuery(string query)
        {
            using (var client = new HttpClient())
            {
                var values = new Dictionary<string, string>
                {
                    { "insertlog", query }
                };
                var content = new FormUrlEncodedContent(values);
                try
                {
                    var response = await client.PostAsync("http://www.iarc.org/Holyland2017/Server/AddLog.php", content);
                    var responseString = await response.Content.ReadAsStringAsync();
                    return responseString;
                }
                catch (Exception)
                {
                    return "Connection with server failed! Check your internet connection";
                }
            }
        }

        private async Task<string> UploadLogToIARC()
        {
            string insert = GenerateMultipleInsert(dal.GetAllQSOs());

            //************************************************** ASYNC ********************************************//
            using (var client = new HttpClient())
            {
                var values = new Dictionary<string, string>
                {
                    { "insertlog", insert }
                };
                var content = new FormUrlEncodedContent(values);
                try
                {
                    var response = await client.PostAsync("http://www.iarc.org/Holyland2017/Server/AddLog.php", content);
                    var responseString = await response.Content.ReadAsStringAsync();
                    return responseString;
                }
                catch (Exception)
                {
                    return "Connection with server failed! Check your internet connection";
                }
            }
        }

        private string GenerateMultipleInsert(IList<QSO> qsos)
        {
            StringBuilder sb = new StringBuilder("INSERT INTO `log` ", 500);
            sb.Append("(`my_call`, `my_square`, `mode`, `frequency`, `band`, `callsign`, `timestamp`, `rst_sent`, `rst_rcvd`, `exchange`, `comment`, `name`, `country`) VALUES ");
            foreach (QSO qso in qsos)
            {
                sb.Append("(");
                sb.Append("'"); sb.Append(qso.MyCall.Replace("'","\"")); sb.Append("',");
                sb.Append("'"); sb.Append(qso.STX.Replace("'", "\"")); sb.Append("',");
                sb.Append("'"); sb.Append(qso.Mode.Replace("'", "\"")); sb.Append("',");
                sb.Append("'"); sb.Append(qso.Freq.Replace("'", "\"")); sb.Append("',");
                sb.Append("'"); sb.Append(qso.Band.Replace("'", "\"")); sb.Append("',");
                sb.Append("'"); sb.Append(qso.DXCall.Replace("'", "\"")); sb.Append("',");
                sb.Append("'"); sb.Append(qso.Date.Replace("'", "\"") + " " + qso.Time.Replace("'", "\"")); sb.Append("',");
                sb.Append("'"); sb.Append(qso.RST_SENT.Replace("'", "\"")); sb.Append("',");
                sb.Append("'"); sb.Append(qso.RST_RCVD.Replace("'", "\"")); sb.Append("',");
                sb.Append("'"); sb.Append(qso.SRX.Replace("'", "\"")); sb.Append("',");
                sb.Append("'"); sb.Append(qso.Comment.Replace("'", "\"")); sb.Append("',");
                sb.Append("'"); sb.Append(qso.Name.Replace("'", "\"")); sb.Append("',");
                sb.Append("'"); sb.Append(qso.Country.Replace("'", "\"")); sb.Append("'),");
            }
            string result = sb.ToString().TrimEnd(',');
            result += " ON DUPLICATE KEY UPDATE my_call=my_call";
            return result;
        }

        //public async void UploadLog()
        //{
        //    if (Qsos.Count == 0)
        //    {
        //        System.Windows.Forms.MessageBox.Show("You can not upload empty log");
        //        return;
        //    }
        //    string result = await UploadLogToIARC();
        //    //System.Windows.Forms.MessageBox.Show("Only active during the log upload period");
        //    System.Windows.Forms.MessageBox.Show(result);
        //}

        //private void PostQSO(QSO qso)
        //{
        //    //************************************************** ASYNC ********************************************//
        //    using (WebClient client = new WebClient())
        //    {
        //        client.UploadValuesAsync(new Uri("http://www.iarc.org/xmas/Server/AddLog.php"), new NameValueCollection()
        //            {
        //                { "insertlog", GenerateInsert(qso) }
        //            });
        //    }
        //}
        //private string GenerateInsert(QSO qso)
        //{
        //    StringBuilder sb = new StringBuilder("INSERT IGNORE INTO `log` ", 500);
        //    sb.Append("(`my_call`, `my_square`, `mode`, `frequency`, `band`, `callsign`, `timestamp`, `rst_sent`, `rst_rcvd`, `exchange`, `comment`) VALUES ");
        //    sb.Append("(");
        //    sb.Append("'"); sb.Append(qso.my_call); sb.Append("',");
        //    sb.Append("'"); sb.Append(qso.my_square); sb.Append("',");
        //    sb.Append("'"); sb.Append(qso.mode); sb.Append("',");
        //    sb.Append("'"); sb.Append(qso.frequency); sb.Append("',");
        //    sb.Append("'"); sb.Append(qso.band); sb.Append("',");
        //    sb.Append("'"); sb.Append(qso.callsign); sb.Append("',");
        //    sb.Append("'"); sb.Append(qso.timestamp); sb.Append("',");
        //    sb.Append("'"); sb.Append(qso.rst_sent); sb.Append("',");
        //    sb.Append("'"); sb.Append(qso.rst_rcvd); sb.Append("',");
        //    sb.Append("'"); sb.Append(qso.exchange); sb.Append("',");
        //    sb.Append("'"); sb.Append(qso.comment); sb.Append("')");
        //    string result = sb.ToString();
        //    return result;
        //}

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

        private void GridRow_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (QSODataGrid.SelectedItem == null) return;
            if (string.IsNullOrWhiteSpace(TB_DXCallsign.Text) || System.Windows.Forms.MessageBox.Show("Do you want to override current QSO?", "Edit QSO", System.Windows.Forms.MessageBoxButtons.YesNo, System.Windows.Forms.MessageBoxIcon.Question) == System.Windows.Forms.DialogResult.Yes)
            {
                QsoToUpdate = QSODataGrid.SelectedItem as QSO;
                LoadQsoForUpdate();
            }
        }

        private void LoadQsoForUpdate()
        {
            ClearBtn_Click(null, null);
            state = State.Edit;
            TB_Comment.Text = QsoToUpdate.Comment;
            TB_DXCallsign.Text = QsoToUpdate.DXCall;
            TB_Exchange.Text = QsoToUpdate.SRX;
            Frequency = QsoToUpdate.Freq;
            TB_MyCallsign.Text = QsoToUpdate.MyCall;
            TB_MyGrid.Text = QsoToUpdate.STX;
            TB_RSTRcvd.Text = QsoToUpdate.RST_RCVD;
            TB_RSTSent.Text = QsoToUpdate.RST_SENT;
            TB_DX_Name.Text = QsoToUpdate.Name;
            Mode = QsoToUpdate.Mode;

            TP_Date.Value = DateTime.Parse(QsoToUpdate.Date);
            TP_Time.Value = DateTime.Parse(QsoToUpdate.Time);
        }

        private void AboutMenuItem_Click(object sender, RoutedEventArgs e)
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
                if (TB_DXCallsign.Text.StartsWith("4X") || TB_DXCallsign.Text.StartsWith("4Z"))
                {
                    //if (HolyLogParser.validSquares.Contains(TB_4xExchange.Text))
                    //{
                    //    TB_Exchange.BorderBrush = System.Windows.Media.Brushes.LightGray;
                    //}
                    //else
                    //{
                    //    allOK = false;
                    //    TB_4xExchange.BorderBrush = System.Windows.Media.Brushes.Red;
                    //}
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(TB_Exchange.Text) && int.Parse(TB_Exchange.Text) != 0)
                    {
                        TB_Exchange.BorderBrush = System.Windows.Media.Brushes.LightGray;
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

                if (TB_MyCallsign.Text.StartsWith("4X") || TB_MyCallsign.Text.StartsWith("4Z"))
                {
                    if (string.IsNullOrWhiteSpace(TB_MyGrid.Text))// || !HolyLogParser.validSquares.Contains(TB_MyGrid.Text))
                    {
                        allOK = false;
                        TB_MyGrid.BorderBrush = System.Windows.Media.Brushes.Red;
                    }
                    else
                    {
                        TB_MyGrid.BorderBrush = System.Windows.Media.Brushes.LightGray;
                    }
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

                if (string.IsNullOrWhiteSpace(TP_Date.Text))
                {
                    allOK = false;
                    TP_Date.BorderBrush = System.Windows.Media.Brushes.Red;
                }
                else
                {
                    TP_Date.BorderBrush = System.Windows.Media.Brushes.LightGray;
                }
                if (string.IsNullOrWhiteSpace(TP_Time.Text))
                {
                    allOK = false;
                    TP_Time.BorderBrush = System.Windows.Media.Brushes.Red;
                }
                else
                {
                    TP_Time.BorderBrush = System.Windows.Media.Brushes.LightGray;
                }
            }
            return allOK;
        }

        private void ClearMatrix()
        {
            if (matrix != null)
                matrix.ClearMatrix();
        }

        private void MyScoreMenuItem_Click(object sender, RoutedEventArgs e)
        {
            parseAdif();
            MessageBox.Show("Your score is: " + p.Result.ToString());
        }

        private void PropertiesMenuItem_Click(object sender, RoutedEventArgs e)
        {
            PropertiesWindow PropertiesWindow = new PropertiesWindow();
            PropertiesWindow.Closed += PropertiesWindow_Closed;
            PropertiesWindow.Show();
        }

        private void ManualModeMenuItem_Click(object sender, RoutedEventArgs e)
        {
            Properties.Settings.Default.isManualMode = !Properties.Settings.Default.isManualMode;
            ManualModeMenuItem.Header = Properties.Settings.Default.isManualMode ? "Manual Mode - On" : "Manual Mode - Off";
        }
        

        private void PropertiesWindow_Closed(object sender, EventArgs e)
        {
            if (String.IsNullOrWhiteSpace(SessionKey))
                Helper.LoginToQRZ(out _SessionKey);
        }

        private void parseAdif()
        {
            string adif = Services.GenerateAdif(dal.GetAllQSOs());
            p = new HolyLogParser(adif, (HolyLogParser.IsIsraeliStation(TB_MyCallsign.Text)) ? HolyLogParser.Operator.Israeli : HolyLogParser.Operator.Foreign);
            p.Parse();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            StartOmniRig();
        }

        private void Window_Closed(object sender, EventArgs e)
        {
            OmniRigEngine.StatusChange -= OmniRigEngine_StatusChange;
            OmniRigEngine.ParamsChange -= OmniRigEngine_ParamsChange;
            Rig = null;
            OmniRigEngine = null;
            Properties.Settings.Default.Save();
        }

        private void TB_Frequency_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (TB_Band != null)
            {
                //string band = HolyLogParser.convertFreqToBand(TB_Frequency.Text.Replace(",", ""));
                string band = HolyLogParser.convertFreqToBand(TB_Frequency.Text);
                if (!string.IsNullOrWhiteSpace(band))
                {
                    TB_Band.Text = band + "M";
                }
                else
                {
                    TB_Band.Text = string.Empty;
                }
            }

        }

        private void ClearLogMenuItem_Click(object sender, RoutedEventArgs e)
        {
            MessageBoxResult messageBoxResult = System.Windows.MessageBox.Show("Are you sure?", "Delete Confirmation", System.Windows.MessageBoxButton.YesNo);
            if (messageBoxResult == MessageBoxResult.Yes)
            {
                Qsos.Clear();
                dal.DeleteAll();
                ClearBtn_Click(null, null);
                UpdateNumOfQSOs();
            }
            else
            {
                e.Handled = true;
            }            
        }
        private void SignboardMenuItem_Click(object sender, RoutedEventArgs e)
        {
            signboard = new SignboardWindow(TB_MyCallsign.Text, TB_MyGrid.Text);
            signboard.Show();
        }
        private void MatrixMenuItem_Click(object sender, RoutedEventArgs e)
        {
            matrix = new MatrixWindow();
            matrix.Show();
        }
        private void OmnirigMenuItem_Click(object sender, RoutedEventArgs e)
        {
            string url = "http://www.dxatlas.com/OmniRig/";

            try
            {
                System.Diagnostics.Process.Start(url);
            }
            catch (Exception)
            {
                MessageBox.Show("Please install 'Chrome' and try again");
            }
        }

        private void HolyLoggerMenuItem_Click(object sender, RoutedEventArgs e)
        {
            string url = "https://4z1kd.github.io/HolyLogger/";

            try
            {
                System.Diagnostics.Process.Start(url);
            }
            catch (Exception)
            {
                MessageBox.Show("Please install 'Chrome' and try again");
            }
        }

        private async void UpdatesMenuItem_Click(object sender, RoutedEventArgs e)
        {
            string tempPath = Path.GetTempPath();
            string filename = tempPath + @"\HolyLogger_x86.msi";
            Uri uri = new Uri("https://github.com/4Z1KD/HolyLogger/raw/master/HolyLogger_x86.msi");

            System.Reflection.Assembly assembly = System.Reflection.Assembly.GetExecutingAssembly();
            FileVersionInfo fvi = FileVersionInfo.GetVersionInfo(assembly.Location);
            string CurrentVersion = fvi.FileVersion;

            WebRequestHandler _webRequestHandler = new WebRequestHandler() { CachePolicy = new HttpRequestCachePolicy(HttpRequestCacheLevel.NoCacheNoStore) };

            //WebClient client1 = new WebClient();
            //client1.CachePolicy = new RequestCachePolicy(RequestCacheLevel.NoCacheNoStore);
            //client1.DownloadStringCompleted += (sender1, args) => {
            //    if (!args.Cancelled && args.Error == null)
            //    {
            //        string result = args.Result; // do something fun...
            //    }
            //};
            //client1.DownloadStringAsync(new Uri("https://raw.githubusercontent.com/4Z1KD/HolyLogger/master/Version"));

            using (var client = new HttpClient(_webRequestHandler))
            {
                try
                {
                    string baseRequest = "https://raw.githubusercontent.com/4Z1KD/HolyLogger/master/Version?v=" + DateTime.Now.Ticks;
                    var response = await client.GetAsync(baseRequest);
                    var responseFromServer = await response.Content.ReadAsStringAsync();

                    if (CompareVersions(CurrentVersion, responseFromServer))
                    {
                        string messageBoxText = "Do you want to install the new version?";
                        string caption = "New updates are available";
                        MessageBoxButton button = MessageBoxButton.YesNoCancel;
                        MessageBoxImage icon = MessageBoxImage.Warning;
                        if (MessageBox.Show(messageBoxText, caption, button, icon) == MessageBoxResult.Yes)
                        {
                            //HolyLoggerMenuItem_Click(null, null);

                            try
                            {
                                if (File.Exists(filename))
                                {
                                    File.Delete(filename);
                                }
                                WebClient wc = new WebClient();
                                wc.DownloadFileAsync(uri, filename);
                                wc.DownloadFileCompleted += new AsyncCompletedEventHandler(wc_DownloadFileCompleted);
                            }
                            catch (Exception ex)
                            {
                                MessageBox.Show(ex.Message.ToString());
                            }
                        }
                    }
                    else
                    {
                        System.Windows.Forms.MessageBox.Show("Your version is up-to-date");
                    }
                }
                catch (Exception)
                {
                    System.Windows.Forms.MessageBox.Show("Server busy. Please try later.");
                }
            }
        }
        private void wc_DownloadFileCompleted(object sender, AsyncCompletedEventArgs e)
        {
            string tempPath = Path.GetTempPath();
            string filename = tempPath + @"\HolyLogger_x86.msi";

            if (e.Error == null)
            {
                Process.Start(filename);
                Environment.Exit(0);
            }
            else
            {
                MessageBox.Show("Failed to download, please check your connection", "Download failed!");
            }
        }

        private bool CompareVersions(string current, string server)
        {
            var version1 = new Version(current.Trim());
            var version2 = new Version(server.Trim());
            var result = version2.CompareTo(version1);
            return result > 0;
        }

        private void TB_DXCallsign_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                TB_Exchange.Focus();
            }
        }

        private void TB_MyCallsign_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (signboard != null)
            {
                signboard.signboardData.Callsign = TB_MyCallsign.Text;
            }
            if (TB_MyGrid == null) return;
            if (!(TB_MyCallsign.Text.StartsWith("4X") || TB_MyCallsign.Text.StartsWith("4Z")))
            {
                TB_MyGrid.Clear();
                TB_MyGrid.IsEnabled = false;
            }
            else
            {
                TB_MyGrid.IsEnabled = true;
                TB_MyGrid.Text = Properties.Settings.Default.my_square;
            }
        }

        private void ConnectToQRZ_Click(object sender, RoutedEventArgs e)
        {
            Helper.LoginToQRZ(out _SessionKey);
        }

        private void TB_MyGrid_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (signboard != null)
            {
                signboard.signboardData.Square = TB_MyGrid.Text;
            }
            
        }

        private void TB_Exchange_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            //if (!char.IsDigit(e.Text, e.Text.Length - 1))
            //    e.Handled = true;
        }

        private void TB_Band_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateMatrixDup();
        }
        private void CB_Mode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            
        }
        private void TB_DXCallsign_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(TB_DXCallsign.Text))
            {
                TB_DXCC.Text = "";
                TB_DX_Name.Text = "";
            }
        }
        private void TB_DXCallsign_LostFocus(object sender, RoutedEventArgs e)
        {
            TB_Exchange.Focusable = true;

            if (!String.IsNullOrWhiteSpace(TB_DXCallsign.Text))
            {
                if (!Properties.Settings.Default.isManualMode && state == State.New)
                    RefreshDateTime_Btn_MouseUp(null, null);
                getQrzData();
                UpdateMatrix();
            }
        }


        private void UpdateMatrix()
        {
            if (matrix != null)
            {
                var qso_list = from qso in Qsos where qso.MyCall == TB_MyCallsign.Text && qso.DXCall == TB_DXCallsign.Text select qso;
                matrix.Clear();
                HolyLogger.Mode qsoMode;
                int qsoBand;

                foreach (var item in qso_list)
                {
                    Enum.TryParse(item.Mode, out qsoMode);
                    int.TryParse(item.Band, out qsoBand);
                    matrix.SetMatrix(qsoMode, qsoBand);
                }
                UpdateMatrixDup();
            }
        }
        private void UpdateMatrixDup()
        {
            if (matrix != null)
            {
                matrix.ClearDup();
                var dups = from qso in Qsos where qso.MyCall == TB_MyCallsign.Text && qso.DXCall == TB_DXCallsign.Text && qso.Band + "M" == TB_Band.Text && qso.Mode == Mode select qso;
                if (dups.Count() > 0)
                    matrix.SetDup();
            }
        }

        private void MenuItem_Click(object sender, RoutedEventArgs e)
        {
            //System.Windows.Forms.MessageBox.Show("Test");
        }

        private async void getQrzData()
        {
            Country = rem.GetEntity(TB_DXCallsign.Text);

            if (!string.IsNullOrWhiteSpace(SessionKey) && !string.IsNullOrWhiteSpace(TB_DXCallsign.Text))
            {

                /*****************************/
                using (var client = new HttpClient())
                {
                    try
                    {
                        string baseRequest = "http://xmldata.qrz.com/xml/current/?s=";
                        var response = await client.GetAsync(baseRequest + SessionKey + ";callsign=" + TB_DXCallsign.Text);
                        var responseFromServer = await response.Content.ReadAsStringAsync();
                        XDocument xDoc = XDocument.Parse(responseFromServer);

                        //IEnumerable<XElement> country = xDoc.Root.Descendants(xDoc.Root.GetDefaultNamespace‌​() + "country");
                        //if (country.Count() > 0)
                        //    Country = country.FirstOrDefault().Value;
                        //else
                        //    Country = "";

                        IEnumerable<XElement> fname = xDoc.Root.Descendants(xDoc.Root.GetDefaultNamespace‌​() + "fname");
                        if (fname.Count() > 0)
                            FName = fname.FirstOrDefault().Value;
                        else
                            FName = "";

                        IEnumerable<XElement> lname = xDoc.Root.Descendants(xDoc.Root.GetDefaultNamespace‌​() + "name");
                        if (lname.Count() > 0)
                            FName += " " + lname.FirstOrDefault().Value;

                        string key = xDoc.Root.Descendants(xDoc.Root.GetDefaultNamespace‌​() + "Key").FirstOrDefault().Value;
                        if (SessionKey != key) Helper.LoginToQRZ(out _SessionKey);
                    }

                    catch (Exception)
                    {
                        //Country = "";
                        FName = "";
                    }

                }
                /*****************************/
            }
            else
            {
                //Country = "";
                FName = "";
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
        private string mMode = "SSB";

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
            catch (Exception)
            {
                //Mouse.OverrideCursor = null;
                //MessageBox.Show(ex.Message);
                //throw;
                Status = "Not installed";
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
                this.Dispatcher.Invoke(() =>
                {
                    Status = Rig.StatusStr;
                });
            }
        }

        private void ShowRigParams()
        {
            if (Rig == null || Rig.Status != OmniRig.RigStatusX.ST_ONLINE || Properties.Settings.Default.isManualMode)
            {
                return;
            }
            try
            {
                this.Dispatcher.Invoke(() =>
                {
                    RX = Rig.GetRxFrequency().ToString();
                    TX = Rig.GetTxFrequency().ToString();
                    Frequency = Rig.Freq.ToString();
                    if (Rig.Freq < 10000000) Frequency = Frequency.Insert(0, "0");
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
                });
            }
            catch (Exception e)
            {
                System.Windows.Forms.MessageBox.Show("Error: " + e.Message);
            }

        }













        #endregion

        
    }
}