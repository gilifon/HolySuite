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
using Blue.Windows;
using System.Windows.Media.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using System.Net.NetworkInformation;
using System.Windows.Media;
using System.Net.Sockets;
using System.Windows.Controls.Primitives;
using Newtonsoft.Json;

namespace HolyLogger
{
    internal struct LASTINPUTINFO
    {
        public uint cbSize;

        public uint dwTime;
    }
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

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool SetForegroundWindow(IntPtr hWnd);

        DataAccess dal;
        EntityResolver rem;

        public ObservableCollection<QSO> Qsos;
        public ObservableCollection<QSO> FilteredQsos;

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

        private string _NumOfDXCCs;
        public string NumOfDXCCs
        {
            get { return _NumOfDXCCs; }
            set
            {
                _NumOfDXCCs = value;
                OnPropertyChanged("NumOfDXCCs");
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

        private string _UploadProgress;
        public string UploadProgress
        {
            get { return _UploadProgress; }
            set
            {
                _UploadProgress = value;
                OnPropertyChanged("UploadProgress");
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

        private string _Continent;
        public string Continent
        {
            get { return _Continent; }
            set
            {
                _Continent = value;
                OnPropertyChanged("Continent");
            }
        }

        private string _Prefix;
        public string Prefix
        {
            get { return _Prefix; }
            set
            {
                _Prefix = value;
                OnPropertyChanged("Prefix");
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

        public string QRZLat { get; set; }
        public string QRZLon { get; set; }
        public string QRZGrid { get; set; }
        public double Azimuth { get; set; }

        private string _SessionKey;
        public string SessionKey
        {
            get { return _SessionKey; }
            set
            {
                _SessionKey = value;
            }
        }

        private bool isRemoteServerLiveLog = false;
        private bool isInitializeComponentsComplete = false;

        public bool isNetworkAvailable { get; set; }

        HolyLogParser _holyLogParser;
        Process QRZProcess;

        LogUploadWindow logupload = null;
        SignboardWindow signboard = null;
        TimerWindow timerscreen = null;
        MatrixWindow matrix = null;
        LogInfoWindow loginfo = null;
        AboutWindow about = null;
        OptionsWindow options = null;

        BackgroundWorker AdifHandlerWorker;
        //BackgroundWorker EntireLogQrzWorker;

        private StickyWindow _stickyWindow;
        private State state = State.New;
        private bool NotifyVersionUpToDate = false;

        QSO QsoToUpdate;
        QSO QsoPreUpdate;
        QSO LastQSO;

        DispatcherTimer UTCTimer = new DispatcherTimer();
        DispatcherTimer HeartbeatTimer = new DispatcherTimer();

        private string title = "HolyLogger   ";
        private const int SEND_CHUNK_SIZE = 50;

        BitmapImage qrz_path = new BitmapImage(new Uri("Images/qrz.png", UriKind.Relative));
        
        List<string> ImportFileQ = new List<string>();

        public static UdpClient Client;

        string MachineName = "Default";

        public MainWindow()
        {
            MachineName = Environment.MachineName;

            Qsos = new ObservableCollection<QSO>();
            rem = new EntityResolver();
            InitializeComponent();
            isInitializeComponentsComplete = true;

            if (Properties.Settings.Default.EnableUDPClient)
            {
                try
                {
                    Client = new UdpClient(Properties.Settings.Default.UDPPort);//2333 / 2237
                    Client.BeginReceive(new AsyncCallback(StartUDPClient), null);
                }
                catch
                {
                    System.Windows.Forms.MessageBox.Show("Failed to open UDP port");
                    Properties.Settings.Default.EnableUDPClient = false;
                }
            }
            isNetworkAvailable = Helper.CheckForInternetConnection();
            HeartbeatTimer.Tick += HeartbeatTimer_Tick;
            checkForAutoUpload();
            

            if (Properties.Settings.Default.ShowTitleClock)
                this.Title = title + DateTime.UtcNow.Hour.ToString("D2") + ":" + DateTime.UtcNow.Minute.ToString("D2") + ":" + DateTime.UtcNow.Second.ToString("D2") + " UTC";

            NetworkFlagItem.Visibility = Properties.Settings.Default.ShowNetworkFlag ? Visibility.Visible : Visibility.Collapsed;

            if (Properties.Settings.Default.UpdateSettings)
            {
                Properties.Settings.Default.Upgrade();
                Properties.Settings.Default.UpdateSettings = false;
                Properties.Settings.Default.Save();
            }
            if (Properties.Settings.Default.isAutoCheckUpdates && isNetworkAvailable)
            {
                NotifyVersionUpToDate = false;
                UpdatesMenuItem_Click(null, null);
            }
            this.Loaded += MainWindow_Loaded; ;
            this.PropertyChanged += MainWindow_PropertyChanged;

            ManualModeMenuItem.Header = Properties.Settings.Default.isManualMode ? "Manual Mode - On" : "Manual Mode - Off";
            L_IsManual.Text = Properties.Settings.Default.isManualMode ? "On" : "Off";

            AdifHandlerWorker = new BackgroundWorker();
            AdifHandlerWorker.WorkerReportsProgress = true;
            AdifHandlerWorker.DoWork += AdifHandlerWorker_DoWork;
            AdifHandlerWorker.ProgressChanged += AdifHandlerWorker_ProgressChanged;
            AdifHandlerWorker.RunWorkerCompleted += AdifHandlerWorker_RunWorkerCompleted;
            
            TB_Exchange.IsEnabled = Properties.Settings.Default.validation_enabled;

            TB_MyCallsign.IsEnabled = !Properties.Settings.Default.isLocked;
            TB_Operator.IsEnabled = !Properties.Settings.Default.isLocked;
            setLockBtnState();

            TB_Comment.IsEnabled = !Properties.Settings.Default.isCommentLocked;
            if (TB_Comment.IsEnabled) LockComment_Btn.Opacity = 1;
            else LockComment_Btn.Opacity = 0.5;            

            try
            {
                dal = new DataAccess();
            }
            catch (Exception e)
            {
                MessageBox.Show(e.Message);
                System.Windows.Application.Current.Shutdown();
                return;
            }

            bool item_found = false;
            foreach (ComboBoxItem item in CB_Mode.Items)
            {
                if ((string)item.Content == Properties.Settings.Default.Mode)
                {
                    CB_Mode.SelectedItem = item;
                    item_found = true;
                    break;
                }
            }
            if (!item_found)
            {
                CB_Mode.SelectedIndex = 0;
            }
            CB_Mode.Text = Properties.Settings.Default.Mode;
            
            TB_MyCallsign.Focus();

            Left = Properties.Settings.Default.MainWindowLeft < 0 ? 0 : Properties.Settings.Default.MainWindowLeft;
            Top = Properties.Settings.Default.MainWindowTop < 0 ? 0 : Properties.Settings.Default.MainWindowTop;
            Width = Properties.Settings.Default.MainWindowWidth;
            Height = Properties.Settings.Default.MainWindowHeight;
            
            //WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen;

            TP_Date.Value = DateTime.UtcNow;
            TP_Time.Value = DateTime.UtcNow;
            
            Qsos = dal.GetAllQSOs();
            Qsos.CollectionChanged += Qsos_CollectionChanged;
            DataContext = Qsos;
            LastQSO = Qsos.FirstOrDefault();

            UpdateNumOfQSOs();
            TB_Frequency_TextChanged(null, null);
            if (isNetworkAvailable) Helper.LoginToQRZ(out _SessionKey);

            if (Properties.Settings.Default.MatrixWindowIsOpen)
            {
                GenerateNewMatrixWindow();
            }
            if (Properties.Settings.Default.SignBoardWindowIsOpen)
            {
                GenerateNewSignboardWindow();
            }
            if (Properties.Settings.Default.TimerWindowIsOpen)
            {
                GenerateNewTimerWindow();
            }

            List<KeyValuePair<string, int>> gridColumnOrder = new List<KeyValuePair<string, int>>();
            gridColumnOrder.Add(new KeyValuePair<string, int>("Date", Properties.Settings.Default.Date_index));
            gridColumnOrder.Add(new KeyValuePair<string, int>("Time", Properties.Settings.Default.Time_index));
            gridColumnOrder.Add(new KeyValuePair<string, int>("Callsign", Properties.Settings.Default.Callsign_index));
            gridColumnOrder.Add(new KeyValuePair<string, int>("Name", Properties.Settings.Default.Name_index));
            gridColumnOrder.Add(new KeyValuePair<string, int>("Country", Properties.Settings.Default.Country_index));
            gridColumnOrder.Add(new KeyValuePair<string, int>("Frequency", Properties.Settings.Default.Frequency_index));
            gridColumnOrder.Add(new KeyValuePair<string, int>("Band", Properties.Settings.Default.Band_index));
            gridColumnOrder.Add(new KeyValuePair<string, int>("RST rcvd", Properties.Settings.Default.RSTrcvd_index));
            gridColumnOrder.Add(new KeyValuePair<string, int>("RST sent", Properties.Settings.Default.RSTsent_index));
            gridColumnOrder.Add(new KeyValuePair<string, int>("Mode", Properties.Settings.Default.Mode_index));
            gridColumnOrder.Add(new KeyValuePair<string, int>("Exchange", Properties.Settings.Default.Exchange_index));
            gridColumnOrder.Add(new KeyValuePair<string, int>("Comment", Properties.Settings.Default.Comment_index));
            
            foreach (var item in QSODataGrid.Columns)
            {
                item.DisplayIndex = gridColumnOrder.FirstOrDefault(p => p.Key == item.Header.ToString()).Value;
            }
            ToggleMatrixControl();
            ToggleAzimuthControl();
            NetworkFlag.Fill = isNetworkAvailable ? new SolidColorBrush(Color.FromRgb(0x00, 0xFF, 0x00)) : new SolidColorBrush(Color.FromRgb(0xFF, 0x00, 0x00));
            NetworkChange.NetworkAvailabilityChanged += NetworkChange_NetworkAvailabilityChanged;
        }

        private async void StartUDPClient(IAsyncResult res)
        {
            if (!Properties.Settings.Default.EnableUDPClient)
            {
                return;
            }
            IPEndPoint RemoteIpEndPoint = new IPEndPoint(IPAddress.Any, 0);
            byte[] received = Client.EndReceive(res, ref RemoteIpEndPoint);
            string data = Encoding.UTF8.GetString(received);

            _holyLogParser = new HolyLogParser();
            QSO qso = _holyLogParser.ParseRawQSO(data);
            qso.GenerateSoapBox();

            if (string.IsNullOrWhiteSpace(qso.Name) && isNetworkAvailable)
            {
                qso.Name = await GetQrzForCall(qso.DXCall);
            }

            this.Dispatcher.Invoke(() =>
            {
                try
                {
                    bool isValid = false;
                    qso.MyCall = string.IsNullOrWhiteSpace(qso.MyCall) ? TB_MyCallsign.Text : qso.MyCall;
                    qso.Operator = string.IsNullOrWhiteSpace(qso.Operator) ? TB_Operator.Text : qso.Operator;
                    if (Properties.Settings.Default.IsOverrideOperator)
                    {
                        qso.Operator = TB_Operator.Text;
                    }

                    qso.Comment = string.IsNullOrWhiteSpace(qso.Comment) ? TB_Comment.Text : qso.Comment;
                    qso.STX = string.IsNullOrWhiteSpace(qso.STX) ? TB_MyHolyland.Text : qso.STX;

                    lock (this)
                    {
                        if (!string.IsNullOrWhiteSpace(qso.MyCall) && !string.IsNullOrWhiteSpace(qso.Band) && !string.IsNullOrWhiteSpace(qso.Mode) && !string.IsNullOrWhiteSpace(qso.DXCall))
                        {
                            QSO q = dal.Insert(qso);
                            Qsos.Insert(0, q);
                            Properties.Settings.Default.RecentQSOCounter++;
                            isValid = true;
                        }
                    }
                    if (QSODataGrid.Items != null && QSODataGrid.Items.Count > 0)
                        QSODataGrid.ScrollIntoView(QSODataGrid.Items[0]);

                    if (isValid && Properties.Settings.Default.isAllowLiveLog && isRemoteServerLiveLog)
                    {
                        UploadProgress = "100%";
                        ToggleUploadProgress(Visibility.Visible);
                        Task<string> response = UploadLogToIARC(new Progress<int>(percent => UploadProgress = percent.ToString() + "%"), new ObservableCollection<QSO> { qso });
                    }
                    UpdateNumOfQSOs();
                    RestoreDataContext();
                }
                catch (Exception ex)
                {
                    System.Windows.Forms.MessageBox.Show("Failed to save QSO: " + ex.Message);
                }
            });
            Client.BeginReceive(new AsyncCallback(StartUDPClient), null);
        }

        private void NetworkChange_NetworkAvailabilityChanged(object sender, NetworkAvailabilityEventArgs e)
        {
            isNetworkAvailable = e.IsAvailable;
            if (isNetworkAvailable) Helper.LoginToQRZ(out _SessionKey);
            this.Dispatcher.Invoke(() =>
            {
                NetworkFlag.Fill = isNetworkAvailable ? new SolidColorBrush(Color.FromRgb(0x00, 0xFF, 0x00)) : new SolidColorBrush(Color.FromRgb(0xFF, 0x00, 0x00));
            });
        }

        private void ToggleMatrixControl()
        {
            if (Properties.Settings.Default.IsShowMatrixControl)
            {
                MatrixC.Visibility = Visibility.Visible;
                MainForm.Height = new GridLength(325);
            }
            else
            {
                MatrixC.Visibility = Visibility.Hidden;
                MainForm.Height = new GridLength(270);
            }
        }

        private void ToggleAzimuthControl()
        {
            if (Properties.Settings.Default.IsShowAzimuthControl)
            {
                AzimuthControl.Visibility = Visibility.Visible;
                this.MinWidth = 1040;
            }
            else
            {
                AzimuthControl.Visibility = Visibility.Hidden;
                this.MinWidth = 800;
            }
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            _stickyWindow = new StickyWindow(this);
            _stickyWindow.StickToScreen = true;
            _stickyWindow.StickToOther = true;
            _stickyWindow.StickOnResize = true;
            _stickyWindow.StickOnMove = true;

            RestartHeartbeatTimer();

            if (Properties.Settings.Default.ShowTitleClock)
                StartUTCTimer();
        }

        private void StartUTCTimer()
        {
            UTCTimer.Interval = new TimeSpan(0, 0, 1);
            UTCTimer.Tick += UTCTimer_Elapsed;
            UTCTimer.Start();
        }
        
        private void StopUTCTimer()
        {
            if (UTCTimer.IsEnabled)
                UTCTimer.Stop();
        }

        private void UTCTimer_Elapsed(object sender, EventArgs e)
        {
            this.Dispatcher.Invoke(() =>
            {
                this.Title = title + DateTime.UtcNow.Hour.ToString("D2") + ":" + DateTime.UtcNow.Minute.ToString("D2") + ":" + DateTime.UtcNow.Second.ToString("D2") + " UTC";
            });
            
        }

        private void RestartHeartbeatTimer()
        {
            if (HeartbeatTimer != null)
            {
                if (HeartbeatTimer.IsEnabled) 
                    HeartbeatTimer.Stop();
                HeartbeatTimer.Interval = new TimeSpan(0, 1, 0);
                HeartbeatTimer.Start();
            }
        }

        private void HeartbeatTimer_Tick(object sender, EventArgs e)
        {
            uint idle_t = Helper.GetIdleTime();
            if (isNetworkAvailable && idle_t < 1000 * 60 * 5)
            {
                Helper.SendHeartbeat(MachineName, TB_MyCallsign.Text.Trim(), TB_Operator.Text.Trim(), TB_Frequency.Text.Trim(), CB_Mode.Text.Trim()); //1000->seconds 60->minute 5->minutes
            }
        }

        private void MainWindow_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            //switch (e.PropertyName)
            //{
            //    case FLD_Mode:
            //        if (state == State.New)
            //        {
            //            if (mMode == "SSB" || mMode == "FM")
            //            {
            //                TB_RSTSent.Text = "59";
            //                TB_RSTRcvd.Text = "59";
            //            }
            //            else
            //            {
            //                TB_RSTSent.Text = "599";
            //                TB_RSTRcvd.Text = "599";
            //            }
            //        }
            //        UpdateDup();
            //        break;
            //    default:
            //        break;
            //}
        }
        
        public void Qsos_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
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
            TB_Operator.IsEnabled = !Properties.Settings.Default.isLocked;
            //TB_MyGrid.IsEnabled = !Properties.Settings.Default.isLocked;
            setLockBtnState();
        }

        private void setLockBtnState()
        {
            if (!Properties.Settings.Default.isLocked) Lock_Btn.Opacity = 1;
            else Lock_Btn.Opacity = 0.5;
        }

        private void LockComment_Btn_MouseUp(object sender, MouseButtonEventArgs e)
        {
            Properties.Settings.Default.isCommentLocked = !Properties.Settings.Default.isCommentLocked;
            TB_Comment.IsEnabled = !Properties.Settings.Default.isCommentLocked;
            if (TB_Comment.IsEnabled) ((Image)sender).Opacity = 1;
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
                qso.Mode = CB_Mode.Text;
                qso.SRX = TB_Exchange.Text;
                qso.Freq = TB_Frequency.Text;
                qso.Band = HolyLogParser.convertFreqToBand(TB_Frequency.Text);
                qso.Country = Country;
                qso.Continent = Continent;
                qso.Name = FName.Length > 25 ? FName.Substring(0,25): FName;
                qso.MyCall = TB_MyCallsign.Text;
                qso.Operator = TB_Operator.Text;
                qso.STX = TB_MyHolyland.Text;
                qso.MyLocator = TB_MyLocator.Text;
                qso.DXLocator = TB_DXLocator.Text;
                qso.RST_RCVD = TB_RSTRcvd.Text;
                qso.RST_SENT = TB_RSTSent.Text;
                DateTime date = TP_Date.Value.Value;
                qso.Date = date.Year.ToString("D4") + date.Month.ToString("D2") + date.Day.ToString("D2");
                DateTime time = TP_Time.Value.Value;
                qso.Time = time.Hour.ToString("D2") + time.Minute.ToString("D2") + time.Second.ToString("D2");
                qso.PROP_MODE = Properties.Settings.Default.IsSatelliteMode ? "SAT" : "";
                qso.SAT_NAME = "";
                qso.GenerateSoapBox();
                if (Properties.Settings.Default.IsSatelliteMode && !string.IsNullOrWhiteSpace(Properties.Settings.Default.SatelliteName))
                {
                    qso.SAT_NAME = Properties.Settings.Default.SatelliteName;
                }
                if (Properties.Settings.Default.isAllowLiveLog && isRemoteServerLiveLog)
                {
                    try
                    {
                        UploadProgress = "100%";
                        ToggleUploadProgress(Visibility.Visible);
                        Task<string> response = UploadLogToIARC(new Progress<int>(percent => UploadProgress = percent.ToString() + "%"), new ObservableCollection<QSO> { qso });
                    }
                    catch (Exception ex)
                    {
                        ToggleUploadProgress(Visibility.Hidden);
                    }
                    
                }
                try
                {
                    lock (this)
                    {
                        LastQSO = dal.Insert(qso);
                        Qsos.Insert(0, LastQSO);
                        Properties.Settings.Default.RecentQSOCounter++;
                    }                    
                    if (QSODataGrid.Items != null && QSODataGrid.Items.Count > 0)
                        QSODataGrid.ScrollIntoView(QSODataGrid.Items[0]);
                }
                catch (Exception ex)
                {
                    System.Windows.Forms.MessageBox.Show("Failed to save QSO: " + ex.Message);
                }
            }
            else if (state == State.Edit)
            {
                QsoToUpdate.Comment = TB_Comment.Text;
                QsoToUpdate.DXCall = TB_DXCallsign.Text;
                QsoToUpdate.Mode = CB_Mode.Text;
                QsoToUpdate.SRX = TB_Exchange.Text;
                QsoToUpdate.Freq = TB_Frequency.Text;
                QsoToUpdate.Band = HolyLogParser.convertFreqToBand(TB_Frequency.Text);
                QsoToUpdate.Country = Country;
                QsoToUpdate.Continent = Continent;
                QsoToUpdate.Name = TB_DX_Name.Text.Length > 25 ? TB_DX_Name.Text.Substring(0, 25) : TB_DX_Name.Text; //FName.Length > 25 ? FName.Substring(0, 25) : FName;
                QsoToUpdate.MyCall = TB_MyCallsign.Text;
                QsoToUpdate.Operator = TB_Operator.Text;
                QsoToUpdate.STX = TB_MyHolyland.Text;
                QsoToUpdate.MyLocator = TB_MyLocator.Text;
                QsoToUpdate.DXLocator = TB_DXLocator.Text;
                QsoToUpdate.RST_RCVD = TB_RSTRcvd.Text;
                QsoToUpdate.RST_SENT = TB_RSTSent.Text;
                DateTime date = TP_Date.Value.Value;
                QsoToUpdate.Date = date.Year.ToString("D4") + date.Month.ToString("D2") + date.Day.ToString("D2");
                DateTime time = TP_Time.Value.Value;
                QsoToUpdate.Time = time.Hour.ToString("D2") + time.Minute.ToString("D2") + time.Second.ToString("D2");
                QsoToUpdate.PROP_MODE = Properties.Settings.Default.IsSatelliteMode ? "SAT" : "";
                if (Properties.Settings.Default.IsSatelliteMode && !string.IsNullOrWhiteSpace(Properties.Settings.Default.SatelliteName))
                {
                    QsoToUpdate.SAT_NAME = Properties.Settings.Default.SatelliteName;
                }
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
                    q.Continent = QsoToUpdate.Continent;
                    q.Name = QsoToUpdate.Name;
                    q.MyCall = QsoToUpdate.MyCall;
                    q.STX = QsoToUpdate.STX;
                    q.RST_RCVD = QsoToUpdate.RST_RCVD;
                    q.RST_SENT = QsoToUpdate.RST_SENT;
                    q.Date = QsoToUpdate.Date;
                    q.Time = QsoToUpdate.Time;
                    q.PROP_MODE = QsoToUpdate.PROP_MODE;
                    q.SAT_NAME = QsoToUpdate.SAT_NAME;
                    QSODataGrid.Items.Refresh();
                }
                LoadPreEditUserData();
            }
            ClearBtn_Click(null, null);
            UpdateNumOfQSOs();
            ClearMatrix();
            RestoreDataContext();
        }

        private void LoadPreEditUserData()
        {
            //TB_Comment.Text = QsoPreUpdate.Comment;
            //TB_DXCallsign.Text = QsoPreUpdate.DXCall;
            //TB_Exchange.Text = QsoPreUpdate.SRX;
            TB_Frequency.Text = QsoPreUpdate.Freq;
            TB_MyCallsign.Text = QsoPreUpdate.MyCall;
            TB_Operator.Text = QsoPreUpdate.Operator;
            TB_MyHolyland.Text = QsoPreUpdate.STX;
            TB_MyLocator.Text = QsoPreUpdate.MyLocator;
            //TB_DXLocator.Text = QsoPreUpdate.DXLocator;
            //TB_RSTRcvd.Text = QsoPreUpdate.RST_RCVD;
            //TB_RSTSent.Text = QsoPreUpdate.RST_SENT;
            //TB_DX_Name.Text = QsoPreUpdate.Name;
            CB_Mode.Text = QsoPreUpdate.Mode;
        }

        private void QRZBtn_Click(object sender, MouseButtonEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(TB_DXCallsign.Text))
            {
                GetQrzData();
            }
        }

        private void ClearBtn_Click(object sender, RoutedEventArgs e)
        {
            //TB_Frequency.Text = string.Empty;
            TB_DXCallsign.Clear();
            TB_Exchange.Clear();
            TB_DXLocator.Clear();

            if (CB_Mode.Text == "SSB" || CB_Mode.Text == "FM")
            {
                TB_RSTSent.Text = "59";
                TB_RSTRcvd.Text = "59";
            }
            else
            {
                TB_RSTSent.Text = "599";
                TB_RSTRcvd.Text = "599";
            }
            if (TB_Comment.IsEnabled) TB_Comment.Clear();
            FName = string.Empty;
            Country = string.Empty;
            Continent = string.Empty;
            if (!Properties.Settings.Default.isManualMode)
                RefreshDateTime_Btn_MouseUp(null, null);
            TB_DXCallsign.Focus();
            ClearMatrix();
            if (state == State.Edit)
            {
                LoadPreEditUserData();
            }
            UpdateState(State.New);
            ShowRigParams();
            RestoreDataContext();
        }

        private void RestoreDataContext()
        {
            if (Properties.Settings.Default.IsFilterQSOs)
            {
                DataContext = Qsos;
            }
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if ((e.Key == Key.F1) || (e.Key == Key.Enter && Properties.Settings.Default.AddQSOWithEnter))
            {
                AddBtn_Click(null, null);
            }
            else if ((e.Key == Key.F2))
            {
                OptionsMenuItemMenuItem_Click(null, null);
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
            //parseAdif();
            NumOfQSOs = dal.GetQsoCount().ToString();
            NumOfGrids = dal.GetGridCount().ToString();
            NumOfDXCCs = dal.GetDXCCCount().ToString();
            Score = "0";// _holyLogParser.Result.ToString();
        }

        private void ExitMenuItem_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }
        private void OpenFolderItem_Click(object sender, RoutedEventArgs e)
        {
            Process.Start(AppDomain.CurrentDomain.BaseDirectory);
        }
        

        private void ImportAdifMenuItem_Click(object sender, RoutedEventArgs e)
        {
            CultureInfo provider = CultureInfo.InvariantCulture;
            OpenFileDialog openFileDialog = new OpenFileDialog();
            openFileDialog.Filter = "ADIF files (*.adi)|*.adi";
            

            if (openFileDialog.ShowDialog() == true)
            {
                ImportFileQ.Add(openFileDialog.FileName);
                if (!AdifHandlerWorker.IsBusy)
                    AdifHandlerWorker.RunWorkerAsync();
            }
        }
        
        private void AdifHandlerWorker_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            int faultyQso = (int)e.Result;
            ToggleUploadProgress(Visibility.Hidden);
            UpdateNumOfQSOs();

            Qsos.Clear();
            foreach (var item in dal.GetAllQSOs())
            {
                Qsos.Add(item);
            }

            if (faultyQso > 0)
            {
                System.Windows.Forms.MessageBox.Show(faultyQso + " Failed to load! check the files.");
            }
            TB_Comment.Text = "";
            UpdateNumOfQSOs();
        }

        private void AdifHandlerWorker_ProgressChanged(object sender, ProgressChangedEventArgs e)
        {
            ToggleUploadProgress(Visibility.Visible);
            UploadProgress = e.ProgressPercentage.ToString() + "%";
        }

        private void ToggleUploadProgress(Visibility visibility)
        {
            UploadProgressSpinner.Visibility = visibility;
            L_UploadProgress.Visibility = visibility;
        }
         
        private void AdifHandlerWorker_DoWork(object sender, DoWorkEventArgs e)
        {
            int faultyQSO = 0;
            foreach (var filename in ImportFileQ) //for each file in the Q
            {
                string RawAdif = File.ReadAllText(filename, Encoding.UTF8); //read it
                _holyLogParser = new HolyLogParser(RawAdif, (HolyLogParser.IsIsraeliStation(Properties.Settings.Default.my_callsign)) ? HolyLogParser.Operator.Israeli : HolyLogParser.Operator.Foreign, Properties.Settings.Default.IsParseDuplicates, Properties.Settings.Default.IsParseWARC);
                try
                {
                    _holyLogParser.Parse(); //try to parse it
                    List<QSO> rawQSOList = _holyLogParser.GetRawQSO();//get the qso list
                    int count = rawQSOList.Count;

                    int c = 1;
                    foreach (var rq in rawQSOList)
                    {
                        try
                        {
                            lock (this)
                            {
                                QSO q = dal.Insert(rq);
                            }
                            float p = (float)(c++) * 100 / count;
                            AdifHandlerWorker.ReportProgress((int)(Math.Ceiling(p)));
                        }
                        catch (Exception ex)
                        {
                            faultyQSO++;
                            //System.Windows.Forms.MessageBox.Show(ex.Message);
                        }
                    }
                }
                catch (Exception exc)
                {
                    System.Windows.Forms.MessageBox.Show(filename + " Failed to load! check the file.");
                }
            }
            e.Result = faultyQSO;
            ImportFileQ.Clear();
        }
        
        private void QSODataGrid_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                // Note that you can have more than one file.
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);

                //collect files in Queue
                foreach (var file in files)
                {
                    ImportFileQ.Add(file);
                    //HandleAdifFileImport(file);
                }
                //run async handler
                if (!AdifHandlerWorker.IsBusy)
                    AdifHandlerWorker.RunWorkerAsync();
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
        
        private async void L_SendLog(object sender, EventArgs e)
        {
            if (Qsos.Count == 0)
            {
                System.Windows.Forms.MessageBox.Show("You can not upload empty log");
                return;
            }
            LogUploadWindow w = (LogUploadWindow)sender;
            string bareCallsign = Properties.Settings.Default.PersonalInfoCallsign;
            string country = Services.getHamQth(bareCallsign).Name;

            var progressIndicator = new Progress<int>();           

            string AddParticipant_result = await AddParticipant(bareCallsign, w.selectedCategory.Operator, w.selectedCategory.Mode, w.selectedCategory.Power, Properties.Settings.Default.PersonalInfoEmail, Properties.Settings.Default.PersonalInfoName, country);
            string UploadLogToIARC_result = await UploadLogToIARC(new Progress<int>(percent => w.UploadProgress = percent), dal.GetAllQSOs());

            StringBuilder sb = new StringBuilder(200);
            sb.Append("Dear ").Append(Properties.Settings.Default.PersonalInfoName).Append(",<br>");
            sb.Append("Thank you for sending the log.<br>");
            sb.Append("73 and Best Regards.");

            string Sendemail_result = await Services.SendMail("holyland@iarc.org", Properties.Settings.Default.PersonalInfoEmail, "Your log was received", sb.ToString());
            w.Close();
            System.Windows.Forms.MessageBox.Show(UploadLogToIARC_result);
        }
        
        private async Task<string> AddParticipant(string callsign, string category_op, string category_mode, string category_power, string email, string name, string country)
        {
            Participant participant = new Participant();
            participant.Callsign = callsign;
            participant.CategoryOp = category_op;
            participant.CategoryMode = category_mode;
            participant.CategoryPower = category_power;
            participant.Email = email;
            participant.Name = name;
            participant.Country = country;
            participant.Year = DateTime.UtcNow.Year;
            participant.QSOs = Qsos.Count;
            participant.Points = Score;

           string participantJSON = JsonConvert.SerializeObject(participant);

            //************************************************** ASYNC ********************************************//
            using (var client = new HttpClient())
            {
                var values = new Dictionary<string, string>
                    {
                        { "data", participantJSON }
                    };
                var content = new FormUrlEncodedContent(values);
                try
                {
                    var response = await client.PostAsync(Properties.Settings.Default.baseURL + "/Holyland/Server/AddParticipant.php", content);
                    var responseString = await response.Content.ReadAsStringAsync();
                    return responseString;
                }
                catch (Exception)
                {
                    return "Connection with server failed! Check your internet connection";
                }
            }
        }

        private async Task<string> UploadLogToIARC(IProgress<int> progress, ObservableCollection<QSO> QSOList)
        {
            bool allSuccessfullyDone = true;
            StringBuilder errorLog = new StringBuilder();
            List<List<QSO>> ChunkedQSOs = SplitQSOList(QSOList);
            int c = 1;
            foreach (var chunk in ChunkedQSOs)
            {
                string chunkJSON = JsonConvert.SerializeObject(chunk).Replace("'", "");
                //string insert = GenerateMultipleInsert(chunk);

                using (var client = new HttpClient())
                {
                    var values = new Dictionary<string, string>
                    {
                        { "data", chunkJSON }
                    };
                    var content = new FormUrlEncodedContent(values);
                    try
                    {
                        var response = await client.PostAsync(Properties.Settings.Default.baseURL + "/Holyland/Server/AddQSO.php", content);
                        //var response = await client.PostAsync(Properties.Settings.Default.baseURL + "/Holyland/Server/AddLog.php", content);
                        var responseString = await response.Content.ReadAsStringAsync();
                        errorLog.AppendLine("Chunk #" + c + ":");
                        errorLog.AppendLine(responseString);
                        if (responseString != "Done!") allSuccessfullyDone = false;
                        progress.Report(c++ * 100 / ChunkedQSOs.Count);
                    }
                    catch (Exception)
                    {
                        return "Connection with server failed! Check your internet connection";
                    }
                }
            }
            ToggleUploadProgress(Visibility.Hidden);
            if (!allSuccessfullyDone)
            {
                using (System.IO.StreamWriter file = new System.IO.StreamWriter(AppDomain.CurrentDomain.BaseDirectory + "\\UploadReport_" + DateTime.Now.Ticks.ToString() + ".txt"))
                {
                    file.Write(errorLog.ToString());
                    file.Close();
                }
            }
            return allSuccessfullyDone ? "All Done, 73!" : "Done with some errors.\r\nPlease contact support.";// "Some of the QSOs had error";
        }

        private List<List<QSO>> SplitQSOList(ObservableCollection<QSO> QSOList)
        {
            //var QSOList = dal.GetAllQSOs();
            int numOfQSO = QSOList.Count;
            int iterations = numOfQSO / SEND_CHUNK_SIZE;
            int reminter = numOfQSO % SEND_CHUNK_SIZE;
            if (reminter > 0) iterations++;

            List<List<QSO>> SplittedQSO = new List<List<QSO>>(iterations);

            for (int i = 0; i < iterations; i++)
            {
                SplittedQSO.Add(QSOList.Skip(i * SEND_CHUNK_SIZE).Take(SEND_CHUNK_SIZE).ToList());
            }

            return SplittedQSO;
        }

        private void PostQSO(QSO qso)
        {
            //************************************************** ASYNC ********************************************//
            using (WebClient client = new WebClient())
            {
                client.UploadValuesAsync(new Uri(Properties.Settings.Default.baseURL + "/Holyland/Server/AddLog.php"), new NameValueCollection()
                    {
                        { "insertlog", GenerateMultipleInsert(new List<QSO>{qso}) }
                    });
            }
        }
       
        private string GenerateMultipleInsert(IList<QSO> qsos)
        {
            StringBuilder sb = new StringBuilder("INSERT INTO `log` ", 500);
            sb.Append("(`my_callsign`, `operator`, `my_square`, `my_locator`, `dx_locator`, `frequency`, `band`, `dx_callsign`, `rst_rcvd`, `rst_sent`, `timestamp`, `mode`, `exchange`, `comment`, `name`, `country`, `continent`, `prop_mode`, `sat_name` ) VALUES ");
            foreach (QSO qso in qsos)
            {
                sb.Append("(");
                sb.Append("'"); sb.Append(qso.MyCall.Trim().Replace("'", "\"")); sb.Append("',");
                sb.Append("'"); sb.Append(qso.Operator.Trim().Replace("'", "\"")); sb.Append("',");
                sb.Append("'"); sb.Append(qso.STX.Trim().Replace("'", "\"")); sb.Append("',");
                sb.Append("'"); sb.Append(qso.MyLocator.Trim().Replace("'", "\"")); sb.Append("',");
                sb.Append("'"); sb.Append(qso.DXLocator.Trim().Replace("'", "\"")); sb.Append("',");
                sb.Append("'"); sb.Append(qso.Freq.Trim().Replace("'", "\"")); sb.Append("',");
                sb.Append("'"); sb.Append(qso.Band.Trim().Replace("'", "\"")); sb.Append("',");
                sb.Append("'"); sb.Append(qso.DXCall.Trim().Replace("'", "\"")); sb.Append("',");
                sb.Append("'"); sb.Append(qso.RST_RCVD.Trim().Replace("'", "\"")); sb.Append("',");
                sb.Append("'"); sb.Append(qso.RST_SENT.Trim().Replace("'", "\"")); sb.Append("',");
                sb.Append("'"); sb.Append(qso.Date.Trim().Replace("'", "\"") + " " + qso.Time.Replace("'", "\"")); sb.Append("',");
                sb.Append("'"); sb.Append(qso.Mode.Trim().Replace("'", "\"")); sb.Append("',");
                sb.Append("'"); sb.Append(qso.SRX.Trim().Replace("'", "\"")); sb.Append("',");
                sb.Append("'"); sb.Append(qso.Comment.Trim().Replace("'", "\"")); sb.Append("',");
                sb.Append("'"); sb.Append(qso.Name.Trim().Replace("'", "\"")); sb.Append("',");
                sb.Append("'"); sb.Append(qso.Country.Trim().Replace("'", "\"")); sb.Append("',");
                sb.Append("'"); sb.Append(qso.Continent.Trim().Replace("'", "\"")); sb.Append("',");
                sb.Append("'"); sb.Append(qso.PROP_MODE.Trim().Replace("'", "\"")); sb.Append("',");
                sb.Append("'"); sb.Append(qso.SAT_NAME.Trim().Replace("'", "\"")); sb.Append("'),");
            }
            string result = sb.ToString().TrimEnd(',');
            result += " ON DUPLICATE KEY UPDATE my_callsign=my_callsign";
            return result;
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

        private void QSODataGrid_ColumnDisplayIndexChanged(object sender, DataGridColumnEventArgs e)
        {
            foreach (var item in QSODataGrid.Columns)
            {
                if (item.Header.ToString() == "Date")
                    Properties.Settings.Default.Date_index = item.DisplayIndex;
                else if (item.Header.ToString() == "Time")
                    Properties.Settings.Default.Time_index = item.DisplayIndex;
                else if (item.Header.ToString() == "Callsign")
                    Properties.Settings.Default.Callsign_index = item.DisplayIndex;
                else if (item.Header.ToString() == "Name")
                    Properties.Settings.Default.Name_index = item.DisplayIndex;
                else if (item.Header.ToString() == "Country")
                    Properties.Settings.Default.Country_index = item.DisplayIndex;
                else if (item.Header.ToString() == "Frequency")
                    Properties.Settings.Default.Frequency_index = item.DisplayIndex;
                else if (item.Header.ToString() == "Band")
                    Properties.Settings.Default.Band_index = item.DisplayIndex;
                else if (item.Header.ToString() == "RST rcvd")
                    Properties.Settings.Default.RSTrcvd_index = item.DisplayIndex;
                else if (item.Header.ToString() == "RST sent")
                    Properties.Settings.Default.RSTsent_index = item.DisplayIndex;
                else if (item.Header.ToString() == "Mode")
                    Properties.Settings.Default.Mode_index = item.DisplayIndex;
                else if (item.Header.ToString() == "Exchange")
                    Properties.Settings.Default.Exchange_index = item.DisplayIndex;
                else if (item.Header.ToString() == "Comment")
                    Properties.Settings.Default.Comment_index = item.DisplayIndex;
            }
        }

        private void GridRow_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (QSODataGrid.SelectedItem == null) return;
            if (string.IsNullOrWhiteSpace(TB_DXCallsign.Text) || System.Windows.Forms.MessageBox.Show("Do you want to override current QSO?", "Edit QSO", System.Windows.Forms.MessageBoxButtons.YesNo, System.Windows.Forms.MessageBoxIcon.Question) == System.Windows.Forms.DialogResult.Yes)
            {
                QsoToUpdate = QSODataGrid.SelectedItem as QSO;
                try
                {
                    if (state == State.New)
                    {
                        QsoPreUpdate = new QSO();
                        HoldPreEditUserData();
                    }                    
                    LoadQsoForUpdate();
                    ShowRigParams();
                }
                catch (Exception ex)
                {
                    System.Windows.Forms.MessageBox.Show("Error! " + ex.Message);
                }                
                UpdateMatrix();
            }
        }

        private void HoldPreEditUserData()
        {
            QsoPreUpdate.Comment = TB_Comment.Text;
            QsoPreUpdate.DXCall = TB_DXCallsign.Text;
            QsoPreUpdate.SRX = TB_Exchange.Text;
            QsoPreUpdate.Freq = TB_Frequency.Text;
            QsoPreUpdate.MyCall = TB_MyCallsign.Text;
            QsoPreUpdate.Operator = TB_Operator.Text;
            QsoPreUpdate.STX = TB_MyHolyland.Text;
            QsoPreUpdate.MyLocator = TB_MyLocator.Text;
            QsoPreUpdate.DXLocator = TB_DXLocator.Text;
            QsoPreUpdate.RST_RCVD = TB_RSTRcvd.Text;
            QsoPreUpdate.RST_SENT = TB_RSTSent.Text;
            QsoPreUpdate.Name = TB_DX_Name.Text;
            QsoPreUpdate.Mode = CB_Mode.Text;
        }

        private void LoadQsoForUpdate()
        {
            ClearBtn_Click(null, null);
            UpdateState(State.Edit);
            CB_Mode.Text = QsoToUpdate.Mode;
            TB_Comment.Text = QsoToUpdate.Comment;
            TB_DXCallsign.Text = QsoToUpdate.DXCall;
            TB_Exchange.Text = QsoToUpdate.SRX;
            TB_Frequency.Text = QsoToUpdate.Freq;
            TB_MyCallsign.Text = QsoToUpdate.MyCall;
            TB_Operator.Text = QsoToUpdate.Operator;
            TB_MyHolyland.Text = QsoToUpdate.STX;
            TB_MyLocator.Text = QsoToUpdate.MyLocator;
            TB_DXLocator.Text = QsoToUpdate.DXLocator;
            TB_RSTRcvd.Text = QsoToUpdate.RST_RCVD;
            TB_RSTSent.Text = QsoToUpdate.RST_SENT;
            TB_DX_Name.Text = QsoToUpdate.Name;
            

            try
            {
                string date = QsoToUpdate.Date.Insert(4, "/").Insert(7, "/");
                string time = QsoToUpdate.Time.Insert(2, ":").Insert(5, ":");
                if (time.Length < 7) time = time.Insert(time.Length, "00");

                TP_Date.Value = DateTime.Parse(date);
                TP_Time.Value = DateTime.Parse(time);
            }
            catch (Exception e)
            {
                TP_Date.Value = DateTime.UtcNow;
                TP_Time.Value = DateTime.UtcNow;
                throw new Exception("Failed to parse QSO date. Value set to NOW");
            }
            
        }
        
        private void UpdateState(State newState)
        {
            state = newState;
            UpdateAddBtnLabel();
        }

        private void UpdateAddBtnLabel()
        {
            if (state == State.Edit)
            {
                AddBtn.Content = "Update (F1)";
            }
            else if (state == State.New)
            {
                AddBtn.Content = "Add (F1)";
            }
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
                TB_DXCallsign.BorderBrush = System.Windows.Media.Brushes.Gray;
            }
            
            if (Properties.Settings.Default.validation_enabled)
            {
                //if (string.IsNullOrWhiteSpace(TB_Exchange.Text))
                //{
                //    allOK = false;
                //    TB_Exchange.BorderBrush = System.Windows.Media.Brushes.Red;
                //}
                //else
                //{
                //    TB_Exchange.BorderBrush = System.Windows.Media.Brushes.Gray;
                //}

                //if (!(TB_DXCallsign.Text.StartsWith("4X") || TB_DXCallsign.Text.StartsWith("4Z")))
                //{
                //    int n;
                //    if (!string.IsNullOrWhiteSpace(TB_Exchange.Text) && int.TryParse(TB_Exchange.Text, out n))
                //    {
                //        TB_Exchange.BorderBrush = System.Windows.Media.Brushes.Gray;
                //    }
                //    else
                //    {
                //        allOK = false;
                //        TB_Exchange.BorderBrush = System.Windows.Media.Brushes.Red;
                //    }
                //}


                if (string.IsNullOrWhiteSpace(TB_Frequency.Text))
                {
                    allOK = false;
                    TB_Frequency.BorderBrush = System.Windows.Media.Brushes.Red;
                    TB_Frequency.BorderThickness = new Thickness(2);
                }
                else
                {
                    TB_Frequency.BorderBrush = System.Windows.Media.Brushes.Gray;
                    TB_Frequency.BorderThickness = new Thickness(1);
                }

                if (string.IsNullOrWhiteSpace(TB_MyCallsign.Text))
                {
                    allOK = false;
                    TB_MyCallsign.BorderBrush = System.Windows.Media.Brushes.Red;
                }
                else
                {
                    TB_MyCallsign.BorderBrush = System.Windows.Media.Brushes.Gray;
                }

                //if (TB_MyCallsign.Text.StartsWith("4X") || TB_MyCallsign.Text.StartsWith("4Z"))
                //{
                //    if (string.IsNullOrWhiteSpace(TB_MyHolyland.Text))// || !HolyLogParser.validSquares.Contains(TB_MyHolyland.Text))
                //    {
                //        allOK = false;
                //        TB_MyHolyland.BorderBrush = System.Windows.Media.Brushes.Red;
                //    }
                //    else
                //    {
                //        TB_MyHolyland.BorderBrush = System.Windows.Media.Brushes.Gray;
                //    }
                //}

                if (string.IsNullOrWhiteSpace(TB_RSTRcvd.Text))
                {
                    allOK = false;
                    TB_RSTRcvd.BorderBrush = System.Windows.Media.Brushes.Red;
                }
                else
                {
                    TB_RSTRcvd.BorderBrush = System.Windows.Media.Brushes.Gray;
                }

                if (string.IsNullOrWhiteSpace(TB_RSTSent.Text))
                {
                    allOK = false;
                    TB_RSTSent.BorderBrush = System.Windows.Media.Brushes.Red;
                }
                else
                {
                    TB_RSTSent.BorderBrush = System.Windows.Media.Brushes.Gray;
                }

                if (string.IsNullOrWhiteSpace(TP_Date.Text))
                {
                    allOK = false;
                    TP_Date.BorderBrush = System.Windows.Media.Brushes.Red;
                }
                else
                {
                    TP_Date.BorderBrush = System.Windows.Media.Brushes.Gray;
                }
                if (string.IsNullOrWhiteSpace(TP_Time.Text))
                {
                    allOK = false;
                    TP_Time.BorderBrush = System.Windows.Media.Brushes.Red;
                }
                else
                {
                    TP_Time.BorderBrush = System.Windows.Media.Brushes.Gray;
                }
            }
            return allOK;
        }

        private void ClearMatrix()
        {
            MatrixC.ClearMatrix();

            if (matrix != null)
                matrix.ClearMatrix();
        }

        private void ManualModeMenuItem_Click(object sender, RoutedEventArgs e)
        {
            Properties.Settings.Default.isManualMode = !Properties.Settings.Default.isManualMode;
            ManualModeMenuItem.Header = Properties.Settings.Default.isManualMode ? "Manual Mode - On" : "Manual Mode - Off";
            L_IsManual.Text = Properties.Settings.Default.isManualMode ? "On" : "Off";
            ShowRigParams();
        }

        private void ResetRecentQSOCounterMenuItem_Click(object sender, RoutedEventArgs e)
        {
            Properties.Settings.Default.RecentQSOCounter = 0;
        }        

        private void PropertiesWindow_Closed(object sender, EventArgs e)
        {
            if (String.IsNullOrWhiteSpace(SessionKey))
                if (isNetworkAvailable) Helper.LoginToQRZ(out _SessionKey);
        }

        private void parseAdif()
        {
            try
            {
                string adif = Services.GenerateAdif(dal.GetAllQSOs());
                _holyLogParser = new HolyLogParser(adif, (HolyLogParser.IsIsraeliStation(TB_MyCallsign.Text)) ? HolyLogParser.Operator.Israeli : HolyLogParser.Operator.Foreign, Properties.Settings.Default.IsParseDuplicates, Properties.Settings.Default.IsParseWARC);
                _holyLogParser.Parse();
            }
            catch (Exception e)
            {
                System.Windows.Forms.MessageBox.Show("Parsing failed.");
            }
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            StartOmniRig();
        }

        private void Window_Closed(object sender, EventArgs e)
        {
            UTCTimer.Tick -= UTCTimer_Elapsed;
            if (OmniRigEngine != null)
            {
                OmniRigEngine.StatusChange -= OmniRigEngine_StatusChange;
                OmniRigEngine.ParamsChange -= OmniRigEngine_ParamsChange;
                Rig = null;
                OmniRigEngine = null;
            }
            Properties.Settings.Default.SignBoardWindowIsOpen = Application.Current.Windows.Cast<Window>().SingleOrDefault(w => w == signboard) != null;
            Properties.Settings.Default.MatrixWindowIsOpen = Application.Current.Windows.Cast<Window>().SingleOrDefault(w => w == matrix) != null;
            Properties.Settings.Default.TimerWindowIsOpen = Application.Current.Windows.Cast<Window>().SingleOrDefault(w => w == timerscreen) != null;
            Properties.Settings.Default.Save();
            if (dal != null) dal.Close();
        }

        private void TB_Frequency_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (TB_Band != null)
            {
                string band = HolyLogParser.convertFreqToBand(TB_Frequency.Text);
                if (!string.IsNullOrWhiteSpace(band))
                {
                    RestartHeartbeatTimer();
                    TB_Band.Text = band;
                }
                else
                {
                    TB_Band.Text = string.Empty;
                }
            }

        }

        private void TB_Frequency_KeyDown(object sender, KeyEventArgs e)
        {

            if ((e.Key >= Key.D0 && e.Key <= Key.D9) || (e.Key >= Key.NumPad0 && e.Key <= Key.NumPad9) || e.Key == Key.OemPeriod || e.Key == Key.Decimal)
            {
                if ((e.Key == Key.OemPeriod || e.Key == Key.Decimal) && ((sender as TextBox).Text.IndexOf('.') > -1))
                {
                    e.Handled = true;
                }
            }
            else
            {
                e.Handled = true;
            }
        }

        private void ClearLogMenuItem_Click(object sender, RoutedEventArgs e)
        {
            MessageBoxResult messageBoxResult = System.Windows.MessageBox.Show("Are you sure?", "Delete Confirmation", System.Windows.MessageBoxButton.YesNo);
            if (messageBoxResult == MessageBoxResult.Yes)
            {

                string adif = Services.GenerateAdif(dal.GetAllQSOs());
                try
                {
                    // Saves the Image via a FileStream created by the OpenFile method.
                    using (System.IO.StreamWriter file = new System.IO.StreamWriter(AppDomain.CurrentDomain.BaseDirectory + "\\" + DateTime.Now.Ticks.ToString() + ".adi"))
                    {
                        file.Write(adif);
                        file.Close();
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Backup failed: " + ex.Message);
                }
                finally
                {
                    Properties.Settings.Default.RecentQSOCounter = 0;
                    Qsos.Clear();
                    dal.DeleteAll();
                    ClearBtn_Click(null, null);
                    UpdateNumOfQSOs();
                }
            }
            else
            {
                e.Handled = true;
            }            
        }

        private void UploadMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (logupload != null)
            {
                var existingWindow = Application.Current.Windows.Cast<Window>().SingleOrDefault(w => w == logupload /* return "true" if 'w' is the window your are about to open */);

                if (existingWindow != null)
                {
                    existingWindow.Activate();
                }
                else
                {
                    GenerateNewLogUploasWindow();
                }
            }
            else
            {
                GenerateNewLogUploasWindow();
            }
        }

        private void GenerateNewLogUploasWindow()
        {
            logupload = new LogUploadWindow();
            logupload.Left = Properties.Settings.Default.LogUploadWindowLeft < 0 ? 0 : Properties.Settings.Default.LogUploadWindowLeft;
            logupload.Top = Properties.Settings.Default.LogUploadWindowTop < 0 ? 0 : Properties.Settings.Default.LogUploadWindowTop;
            logupload.SendLog += L_SendLog;
            logupload.Show();
        }

        private void OptionsMenuItemMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (options != null)
            {
                var existingWindow = Application.Current.Windows.Cast<Window>().SingleOrDefault(w => w == options /* return "true" if 'w' is the window your are about to open */);
                GetRigTypes();

                if (existingWindow != null)
                {
                    existingWindow.Activate();
                }
                else
                {
                    GenerateNewOptionsWindow();
                }
            }
            else
            {
                GenerateNewOptionsWindow();
            }
            options.GeneralSettingsControlControlInstance.Rig1 = Rig1;
            options.GeneralSettingsControlControlInstance.Rig2 = Rig2;
        }

        private void GenerateNewOptionsWindow()
        {
            options = new OptionsWindow();
            options.Closed += Options_Closed;
            options.Left = Properties.Settings.Default.OptionsWindowLeft < 0 ? 0 : Properties.Settings.Default.OptionsWindowLeft;
            options.Top = Properties.Settings.Default.OptionsWindowTop < 0 ? 0 : Properties.Settings.Default.OptionsWindowTop;
            options.Width = Properties.Settings.Default.OptionsWindowWidth;
            options.Height = Properties.Settings.Default.OptionsWindowHeight;
            options.Show();
        }

        private void Options_Closed(object sender, EventArgs e)
        {
            OptionsWindow optionWindow = (OptionsWindow)sender;
            if(optionWindow.QRZServiceControlInstance.HasChanged)
            {
                if (isNetworkAvailable) Helper.LoginToQRZ(out _SessionKey);
            }
            ToggleMatrixControl();
            ToggleAzimuthControl();
            if (optionWindow.GeneralSettingsControlControlInstance.HasChanged)
            {
                SelectRig();
                ShowRigParams();
            }
            if (optionWindow.UserInterfaceControlInstance.HasChanged)
            {
                if (Properties.Settings.Default.ShowTitleClock)
                {
                    StartUTCTimer();
                }
                else
                {
                    StopUTCTimer();
                    this.Title = title;
                }
            }
            if (optionWindow.SatelliteControlInstance.HasChanged)
            {
                ShowRigParams();
            }
            if (Properties.Settings.Default.EnableUDPClient)
            {
                try
                {
                    if (Client == null)
                    {
                        Client = new UdpClient(Properties.Settings.Default.UDPPort);//2333 / 2237
                        Client.BeginReceive(new AsyncCallback(StartUDPClient), null);
                    }
                }
                catch
                {
                    System.Windows.Forms.MessageBox.Show("Failed to open UDP port");
                    Properties.Settings.Default.EnableUDPClient = false;
                }
            }
            else
            {
                if (Client != null)
                {
                    Client.Close();
                    Client = null;
                }                
            }
            NetworkFlagItem.Visibility = Properties.Settings.Default.ShowNetworkFlag ? Visibility.Visible : Visibility.Collapsed;
            TB_MyCallsign.IsEnabled = !Properties.Settings.Default.isLocked;
            TB_Operator.IsEnabled = !Properties.Settings.Default.isLocked;
            setLockBtnState();
        }

        private void SignboardMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (signboard != null)
            {
                var existingWindow = Application.Current.Windows.Cast<Window>().SingleOrDefault(w => w == signboard /* return "true" if 'w' is the window your are about to open */);

                if (existingWindow != null)
                {
                    existingWindow.Activate();
                }
                else
                {
                    GenerateNewSignboardWindow();
                }
            }
            else
            {
                GenerateNewSignboardWindow();
            }

        }

        private void TimerMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (timerscreen != null)
            {
                var existingWindow = Application.Current.Windows.Cast<Window>().SingleOrDefault(w => w == timerscreen /* return "true" if 'w' is the window your are about to open */);

                if (existingWindow != null)
                {
                    existingWindow.Activate();
                }
                else
                {
                    GenerateNewTimerWindow();
                }
            }
            else
            {
                GenerateNewTimerWindow();
            }

        }

        private void GenerateNewSignboardWindow()
        {
            signboard = new SignboardWindow(TB_MyCallsign.Text, TB_MyHolyland.Text);
            signboard.Left = Properties.Settings.Default.SignBoardWindowLeft < 0 ? 0 : Properties.Settings.Default.SignBoardWindowLeft;
            signboard.Top = Properties.Settings.Default.SignBoardWindowTop < 0 ? 0 : Properties.Settings.Default.SignBoardWindowTop;
            signboard.Width = Properties.Settings.Default.SignBoardWindowWidth;
            signboard.Height = Properties.Settings.Default.SignBoardWindowHeight;
            signboard.Show();
        }

        private void GenerateNewTimerWindow()
        {
            timerscreen = new TimerWindow("kuku");
            timerscreen.Left = Properties.Settings.Default.TimerWindowLeft < 0 ? 0 : Properties.Settings.Default.TimerWindowLeft;
            timerscreen.Top = Properties.Settings.Default.TimerWindowTop < 0 ? 0 : Properties.Settings.Default.TimerWindowTop;
            timerscreen.Width = Properties.Settings.Default.TimerWindowWidth;
            timerscreen.Height = Properties.Settings.Default.TimerWindowHeight;
            timerscreen.Show();
        }

        private void MatrixMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (matrix != null)
            {
                var existingWindow = Application.Current.Windows.Cast<Window>().SingleOrDefault(w => w == matrix); /* return "true" if 'w' is the window your are about to open */

                if (existingWindow != null)
                {
                    existingWindow.Activate();
                }
                else
                {
                    GenerateNewMatrixWindow();
                }
            }
            else
            {
                GenerateNewMatrixWindow();
            }
        }

        private void LogInfoMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (loginfo != null)
            {
                var existingWindow = Application.Current.Windows.Cast<Window>().SingleOrDefault(w => w == loginfo); /* return "true" if 'w' is the window your are about to open */

                if (existingWindow != null)
                {
                    existingWindow.Activate();
                }
                else
                {
                    GenerateNewLogInfoWindow();
                }
            }
            else
            {
                GenerateNewLogInfoWindow();
            }
        }
        
        private void GenerateNewMatrixWindow()
        {
            matrix = new MatrixWindow();
            matrix.Left = Properties.Settings.Default.MatrixWindowLeft < 0 ? 0 : Properties.Settings.Default.MatrixWindowLeft;
            matrix.Top = Properties.Settings.Default.MatrixWindowTop < 0 ? 0 : Properties.Settings.Default.MatrixWindowTop;
            matrix.Show();
        }

        private void GenerateNewLogInfoWindow()
        {
            loginfo = new LogInfoWindow();
            loginfo.Left = Properties.Settings.Default.LogInfoWindowLeft < 0 ? 0 : Properties.Settings.Default.LogInfoWindowLeft;
            loginfo.Top = Properties.Settings.Default.LogInfoWindowTop < 0 ? 0 : Properties.Settings.Default.LogInfoWindowTop;

            if (_holyLogParser != null)
            {
                loginfo.CW.Value = _holyLogParser.qsoCW;
                loginfo.SSB.Value = _holyLogParser.qsoSSB;

                //loginfo.Band6.Value = p.qso6;
                loginfo.Band10.Value = _holyLogParser.qso10;
                loginfo.Band12.Value = _holyLogParser.qso12;
                loginfo.Band15.Value = _holyLogParser.qso15;
                loginfo.Band17.Value = _holyLogParser.qso17;
                loginfo.Band20.Value = _holyLogParser.qso20;
                loginfo.Band30.Value = _holyLogParser.qso30;
                loginfo.Band40.Value = _holyLogParser.qso40;
                //loginfo.Band60.Value = p.qso60;
                loginfo.Band80.Value = _holyLogParser.qso80;
                loginfo.Band160.Value = _holyLogParser.qso160;
            }
            loginfo.Show();
        }

        private void GridSquareMenuItem_Click(object sender, RoutedEventArgs e)
        {
            string url = "https://www.iarc.org/holysquare/";
            try
            {
                System.Diagnostics.Process.Start(url);
            }
            catch (Exception)
            {
                MessageBox.Show("Please install 'Chrome' and try again");
            }
        }

        private void OnTheAirMenuItem_Click(object sender, RoutedEventArgs e)
        {
            string url = "https://www.iarc.org/ontheair/";
            try
            {
                System.Diagnostics.Process.Start(url);
            }
            catch (Exception)
            {
                MessageBox.Show("Please install 'Chrome' and try again");
            }
        }

        private void AboutMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (about != null)
            {
                var existingWindow = Application.Current.Windows.Cast<Window>().SingleOrDefault(w => w == about /* return "true" if 'w' is the window your are about to open */);

                if (existingWindow != null)
                {
                    existingWindow.Activate();
                }
                else
                {
                    GenerateNewAboutWindow();
                }
            }
            else
            {
                GenerateNewAboutWindow();
            }

        }

        private void GenerateNewAboutWindow()
        {
            about = new AboutWindow();
            about.Show();
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
            Uri uri = new Uri("http://github.com/4Z1KD/HolyLogger/raw/master/HolyLogger_x86.msi");

            System.Reflection.Assembly assembly = System.Reflection.Assembly.GetExecutingAssembly();
            FileVersionInfo fvi = FileVersionInfo.GetVersionInfo(assembly.Location);
            string CurrentVersion = fvi.FileVersion;

            WebRequestHandler _webRequestHandler = new WebRequestHandler() { CachePolicy = new HttpRequestCachePolicy(HttpRequestCacheLevel.BypassCache) };

            ServicePointManager.Expect100Continue = true;
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

            using (var client = new HttpClient(_webRequestHandler))
            {
                try
                {
                    string baseRequest = "http://raw.githubusercontent.com/4Z1KD/HolyLogger/master/Version?v=" + DateTime.Now.Ticks;
                    var response = await client.GetAsync(baseRequest);
                    var responseFromServer = await response.Content.ReadAsStringAsync();

                    if (CompareVersions(CurrentVersion, responseFromServer))
                    {
                        string messageBoxText = "There is a new version. Do you want to install?";
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
                        if (NotifyVersionUpToDate)
                        {
                            System.Windows.Forms.MessageBox.Show("Your version is up-to-date");
                        }
                        else
                        {
                            NotifyVersionUpToDate = true;
                        }
                    }
                }
                catch (Exception)
                {
                    System.Windows.Forms.MessageBox.Show("Auto checking for update Failed. Please try manualy later.");
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

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Insert)
            {
                e.Handled = true;
            }
        }

        private void TB_DXCallsign_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {

            }
            else if (e.Key == Key.Space)
            {
                e.Handled = true;
            }
        }

        private void TB_MyCallsign_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (TB_MyLocator == null || TB_MyCallsign == null) return;
            RestartHeartbeatTimer();
            //TB_MyLocator.Text = rem.GetDXCC(TB_MyCallsign.Text).Locator;
            if (signboard != null)
            {
                signboard.signboardData.Callsign = TB_MyCallsign.Text;
            }
            if (TB_MyHolyland == null) return;
            UpdateMatrix();
        }
        
        private void TB_MyHolyland_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (signboard != null)
            {
                signboard.signboardData.Square = TB_MyHolyland.Text;
            }
            
        }

        private void TB_Exchange_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            //if (!char.IsDigit(e.Text, e.Text.Length - 1))
            //    e.Handled = true;
        }

        private void TB_Band_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateDup();
        }

        private void CB_Mode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                string val = CB_Mode.Text;
                if (val == "SSB" || val == "FM")
                {
                    TB_RSTSent.Text = "59";
                    TB_RSTRcvd.Text = "59";
                }
                else
                {
                    TB_RSTSent.Text = "599";
                    TB_RSTRcvd.Text = "599";
                }
                UpdateDup();
            }
            catch (Exception)
            {

                //throw;
            }
            
        }

        private void TB_DXCallsign_TextChanged(object sender, TextChangedEventArgs e)
        {
            ClearDXLocator();
            if (string.IsNullOrWhiteSpace(TB_DXCallsign.Text))
            {
                TB_DXCC.Text = "";
                TB_DX_Name.Text = "";
                ClearAzimuth();
                ClearMatrix();
                L_Duplicate.Visibility = Visibility.Hidden;
                L_Legal.Visibility = Visibility.Hidden;
                RestoreDataContext();
            }
            else
            {
                if (!Properties.Settings.Default.isManualMode && state == State.New)
                    RefreshDateTime_Btn_MouseUp(null, null);
                DXCC dXCC = rem.GetDXCC(TB_DXCallsign.Text);
                Country = dXCC.Name;
                Continent = dXCC.Continent;
                QRZGrid = dXCC.Locator;
                Prefix = TB_DXCallsign.Text.Length >= 2 ? TB_DXCallsign.Text.Substring(0, 2) : "";
                SetAzimuth();
                if (Properties.Settings.Default.UseDXCCDefaultGrid)
                    SetDXLocator(QRZGrid);
                if (state == State.New)
                    GetQrzData();
                UpdateMatrix();
                if (Properties.Settings.Default.IsFilterQSOs)
                {
                    FilteredQsos = new ObservableCollection<QSO>(Qsos.Where(p => p.DXCall.Contains(TB_DXCallsign.Text)));
                    if (LastQSO != null && Properties.Settings.Default.DisplayLastQSOinGrid) FilteredQsos.Insert(0, LastQSO);
                    DataContext = FilteredQsos;
                }
            }
        }
        
        private void TB_DXCallsign_LostFocus(object sender, RoutedEventArgs e)
        {
            TB_Exchange.Focusable = true;
        }

        private void UpdateMatrix()
        {
            if (!isInitializeComponentsComplete) return;
            ClearMatrix();

            if (Qsos == null) return;
            var qso_list = from qso in Qsos where qso.MyCall == TB_MyCallsign.Text && qso.DXCall == TB_DXCallsign.Text select qso;
            HolyLogger.Mode qsoMode;

            foreach (var item in qso_list)
            {
                try
                {
                    Enum.TryParse(item.Mode, out qsoMode);
                    MatrixC.SetMatrix(qsoMode, item.Band);
                    if (matrix != null)
                    {
                        matrix.SetMatrix(qsoMode, item.Band);
                    }
                }
                catch (Exception)
                {

                }
            }

            UpdateDup();
        }

        private void UpdateDup()
        {
            var dups = from qso in Qsos where qso.MyCall == TB_MyCallsign.Text && qso.DXCall == TB_DXCallsign.Text && qso.Band == TB_Band.Text && qso.Mode == CB_Mode.Text select qso;
            var legal = from qso in Qsos where qso.MyCall == TB_MyCallsign.Text && qso.DXCall == TB_DXCallsign.Text select qso;

            if (state == State.Edit)
                dups = dups.Where(p => p.id != QsoToUpdate.id);

            if (dups.Count() > 0)
            {
                L_Duplicate.Visibility = Visibility.Visible;
                L_Legal.Visibility = Visibility.Hidden;
                if (matrix != null)
                {
                    matrix.SetDup();
                }
            }
            else
            {
                L_Duplicate.Visibility = Visibility.Hidden;
                L_Legal.Visibility = Visibility.Hidden;
                if (legal.Count() > 0)
                {
                    L_Legal.Visibility = Visibility.Visible;
                }
                if (matrix != null)
                {
                    matrix.ClearDup();
                }
            }
        }

        private void Window_LocationChanged(object sender, EventArgs e)
        {
            if (this.Left >= 0)
                Properties.Settings.Default.MainWindowLeft = this.Left;
            if (this.Top >= 0)
                Properties.Settings.Default.MainWindowTop = this.Top;
        }

        private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            Properties.Settings.Default.MainWindowWidth = this.Width;
            Properties.Settings.Default.MainWindowHeight = this.Height;
        }

        private void SetAzimuth()
        {
            if (!string.IsNullOrWhiteSpace(TB_MyLocator.Text) && !string.IsNullOrWhiteSpace(QRZGrid))
            {
                try
                {
                    Azimuth = MaidenheadLocator.Azimuth(TB_MyLocator.Text, QRZGrid);
                    var distance = MaidenheadLocator.Distance(TB_MyLocator.Text, QRZGrid);
                    AzimuthControl.azimuthData.Azimuth = Azimuth;
                    AzimuthControl.azimuthData.Distance = distance;
                }
                catch (Exception e)
                {
                    ClearAzimuth();
                }
            }
            else
            {
                ClearAzimuth();
            }
        }
        private void SetDXLocator(string locator)
        {
            if (!string.IsNullOrWhiteSpace(locator))
            {
                TB_DXLocator.Text = locator;
            }
        }
        private void ClearDXLocator()
        {
            TB_DXLocator.Clear();            
        }



        private void ClearAzimuth()
        {
            Azimuth = 0;
            AzimuthControl.azimuthData.Azimuth = Azimuth;
            AzimuthControl.azimuthData.Distance = 0;
        }

        private async void GetQrzData()
        {
            if (string.IsNullOrWhiteSpace(SessionKey) && isNetworkAvailable)
            {
                Helper.LoginToQRZ(out _SessionKey);
            }
            if (!string.IsNullOrWhiteSpace(SessionKey) && !string.IsNullOrWhiteSpace(TB_DXCallsign.Text) && TB_DXCallsign.Text.Trim().Length >=3)
            {
                string dxcall = TB_DXCallsign.Text.Trim();
                string bare_dxcall = Services.getBareCallsign(dxcall);

                /*****************************/
                using (var client = new HttpClient())
                {
                    try
                    {
                        string baseRequest = "http://xmldata.qrz.com/xml/current/?s=";
                        var response = await client.GetAsync(baseRequest + SessionKey + ";callsign=" + bare_dxcall);
                        var responseFromServer = await response.Content.ReadAsStringAsync();
                        XDocument xDoc = XDocument.Parse(responseFromServer);
                        
                        if (!string.IsNullOrWhiteSpace(SessionKey) && !string.IsNullOrWhiteSpace(TB_DXCallsign.Text) && (dxcall == TB_DXCallsign.Text.Trim()))
                        {
                            IEnumerable<XElement> xref = xDoc.Root.Descendants(xDoc.Root.GetDefaultNamespace‌​() + "xref");
                            IEnumerable<XElement> call = xDoc.Root.Descendants(xDoc.Root.GetDefaultNamespace‌​() + "call");
                            IEnumerable<XElement> error = xDoc.Root.Descendants(xDoc.Root.GetDefaultNamespace‌​() + "Error");

                            if ((call.Count() > 0 && call.FirstOrDefault().Value == bare_dxcall) || (xref.Count() > 0 && xref.FirstOrDefault().Value == bare_dxcall))
                            {
                                IEnumerable<XElement> fname = xDoc.Root.Descendants(xDoc.Root.GetDefaultNamespace‌​() + "fname");
                                if (fname.Count() > 0)
                                    FName = fname.FirstOrDefault().Value;
                                else
                                    FName = "";

                                IEnumerable<XElement> lname = xDoc.Root.Descendants(xDoc.Root.GetDefaultNamespace‌​() + "name");
                                if (lname.Count() > 0)
                                    FName += " " + lname.FirstOrDefault().Value;

                                //****************** AZIMUTH *****************//
                                IEnumerable<XElement> lat = xDoc.Root.Descendants(xDoc.Root.GetDefaultNamespace‌​() + "lat");
                                if (lat.Count() > 0)
                                    QRZLat = lat.FirstOrDefault().Value;

                                IEnumerable<XElement> lon = xDoc.Root.Descendants(xDoc.Root.GetDefaultNamespace‌​() + "lon");
                                if (lon.Count() > 0)
                                    QRZLon = lon.FirstOrDefault().Value;

                                IEnumerable<XElement> grid = xDoc.Root.Descendants(xDoc.Root.GetDefaultNamespace‌​() + "grid");
                                if (grid.Count() > 0)
                                    QRZGrid = grid.FirstOrDefault().Value.ToUpper();

                                SetAzimuth();
                                SetDXLocator(QRZGrid);
                                //*************************************************//

                                string key = xDoc.Root.Descendants(xDoc.Root.GetDefaultNamespace‌​() + "Key").FirstOrDefault().Value;
                                if (SessionKey != key)
                                    if (isNetworkAvailable) Helper.LoginToQRZ(out _SessionKey);
                            }
                            else if (error.Count() > 0)
                            {
                                string errorCall = error.FirstOrDefault().Value.Split(':')[1].Trim();
                                if (errorCall == dxcall || errorCall == bare_dxcall)
                                {
                                    FName = "";
                                }
                            }
                        }                        
                    }
                    catch (Exception)
                    {
                        FName = "";
                    }
                }
                /*****************************/
            }
            else
            {
                FName = "";
            }
        }

        private async void EntireLogQrzServiseMenuItem_Click(object sender, RoutedEventArgs e)
        {
            ToggleUploadProgress(Visibility.Visible);
            await GetQrzForEntireLogAsync(new Progress<int>(percent => UploadProgress = percent.ToString()));
            ToggleUploadProgress(Visibility.Hidden);
        }

        private async Task<bool> GetQrzForEntireLogAsync(IProgress<int> progress)
        {
            for (int i = 0; i < Qsos.Count; i++)
            {
                progress.Report((i+1) * 100 / Qsos.Count);
                try
                {
                    QSO qso = Qsos[i];
                    if (string.IsNullOrWhiteSpace(qso.Name) && isNetworkAvailable)
                    {
                        qso.Name = await GetQrzForCall(qso.DXCall);
                        dal.Update(qso);
                        this.Dispatcher.Invoke(() =>
                        {
                            QSODataGrid.Items.Refresh();
                        });
                    }
                }
                catch (Exception ex)
                {
                    System.Windows.Forms.MessageBox.Show("Failed to execute QRZ Service: " + ex.Message);
                    break;
                }
            }
            return true;
        }
        
        private async Task<string> GetQrzForCall(string callsign)
        {
            using (var client = new HttpClient())
            {
                try
                {
                    string baseRequest = "http://xmldata.qrz.com/xml/current/?s=";
                    var response = await client.GetAsync(baseRequest + SessionKey + ";callsign=" + Services.getBareCallsign(callsign));
                    var responseFromServer = await response.Content.ReadAsStringAsync();
                    XDocument xDoc = XDocument.Parse(responseFromServer);

                    if (!string.IsNullOrWhiteSpace(SessionKey) && !string.IsNullOrWhiteSpace(callsign))
                    {
                        IEnumerable<XElement> call = xDoc.Root.Descendants(xDoc.Root.GetDefaultNamespace‌​() + "call");

                        if (call.Count() > 0)
                        {
                            string name = "";
                            IEnumerable<XElement> fname = xDoc.Root.Descendants(xDoc.Root.GetDefaultNamespace‌​() + "fname");
                            if (fname.Count() > 0)
                                name = fname.FirstOrDefault().Value;

                            IEnumerable<XElement> lname = xDoc.Root.Descendants(xDoc.Root.GetDefaultNamespace‌​() + "name");
                            if (lname.Count() > 0)
                                name += " " + lname.FirstOrDefault().Value;

                            string key = xDoc.Root.Descendants(xDoc.Root.GetDefaultNamespace‌​() + "Key").FirstOrDefault().Value;
                            if (SessionKey != key) Helper.LoginToQRZ(out _SessionKey);

                            return name;
                        }
                        else
                        {
                            return "";
                        }
                    }
                }
                catch (Exception)
                {
                    return "";
                }
                return "";
            }
        }

        private async void RemoveDuplicatesMenuItem_Click(object sender, RoutedEventArgs e)
        {
            ToggleUploadProgress(Visibility.Visible);
            await RemoveDuplicates(new Progress<int>(percent => UploadProgress = percent.ToString()));
            foreach (var item in dal.GetAllQSOs())
            {
                Qsos.Add(item);
            }
            UpdateNumOfQSOs();
            ToggleUploadProgress(Visibility.Hidden);
        }

        private Task RemoveDuplicates(IProgress<int> progress)
        {
            string adif = Services.GenerateAdif(dal.GetAllQSOs());
            _holyLogParser = new HolyLogParser(adif, (HolyLogParser.IsIsraeliStation(TB_MyCallsign.Text)) ? HolyLogParser.Operator.Israeli : HolyLogParser.Operator.Foreign, false, true);
            _holyLogParser.Parse();

            Qsos.Clear();
            dal.DeleteAll();

            List<QSO> rawQSOList = _holyLogParser.GetRawQSO();//get the qso list
            int count = rawQSOList.Count;

            int faultyQSO = 0;
            int i = 0;
            return Task.Run(() =>
            {
                
                foreach (var rq in rawQSOList)
                {
                    progress.Report((++i) * 100 / count);
                    try
                    {
                        lock (this)
                        {
                            QSO q = dal.Insert(rq);
                        }
                    }
                    catch (Exception ex)
                    {
                        faultyQSO++;
                    }
                }
                
            });
        }

        private void StatusBar_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (Rig != null && Rig.Status != OmniRig.RigStatusX.ST_ONLINE)
            {
                Properties.Settings.Default.EnableOmniRigCAT = false;
            }
            ShowRigParams();
        }

        private async void checkForAutoUpload()
        {
            if (!isNetworkAvailable) return;
            WebRequestHandler _webRequestHandler = new WebRequestHandler() { CachePolicy = new HttpRequestCachePolicy(HttpRequestCacheLevel.BypassCache) };
            using (var client = new HttpClient(_webRequestHandler))
            {
                try
                {
                    ServicePointManager.Expect100Continue = true;
                    ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                    string baseRequest = "https://raw.githubusercontent.com/4Z1KD/HolyLogger/master/LiveLog?v=" + DateTime.Now.Ticks;
                    var response = await client.GetAsync(baseRequest);
                    var responseFromServer = await response.Content.ReadAsStringAsync();
                    isRemoteServerLiveLog = responseFromServer.ToLower().Trim() == "true";
                }
                catch(Exception e)
                {
                    isRemoteServerLiveLog = false;
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


        public string Rig1 { get; set; }
        public string Rig2 { get; set; }

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
                    MessageBox.Show("OmniRig Is running");
                }
                else
                {

                    OmniRigEngine = new OmniRig.OmniRigX();
                    //OmniRigEngine = (OmniRig.OmniRigX)Activator.CreateInstance(Type.GetTypeFromProgID("OmniRig.OmniRigX"));
                    // we want OmniRig interface V.1.1 to 1.99
                    // as V2.0 will likely be incompatible  with 1.x
                    if (OmniRigEngine.InterfaceVersion < 0x101 && OmniRigEngine.InterfaceVersion > 0x299)
                    {
                        OmniRigEngine = null;
                        MessageBox.Show("OmniRig Is Not installed Or has a wrong version number");
                    }
                    GetRigTypes();
                    SubscribeToEvents();
                    SelectRig();
                    ShowRigParams();
                }
            }
            catch (Exception e)
            {
                //Mouse.OverrideCursor = null;
                //MessageBox.Show(ex.Message);
                //throw;
                Status = "Not installed";
            }
        }

        private void GetRigTypes()
        {
            if (OmniRigEngine == null) return;
            try
            {
                Rig1 = OmniRigEngine.Rig1.RigType;
                Rig2 = OmniRigEngine.Rig2.RigType;
            }
            catch
            {
                Rig1 = "";
                Rig2 = "";
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
        private void UnsubscribeFromEvents()
        {
            if (EventsSubscribed)
            {
                EventsSubscribed = false;
                OmniRigEngine.StatusChange -= OmniRigEngine_StatusChange;
                OmniRigEngine.ParamsChange -= OmniRigEngine_ParamsChange;
            }
        }
        private void SelectRig()
        {
            if (OmniRigEngine == null) return;
            if (Properties.Settings.Default.SelectedOmniRig1)
                Rig = OmniRigEngine.Rig1;
            else if (Properties.Settings.Default.SelectedOmniRig2)
                Rig = OmniRigEngine.Rig2;
        }

        //OmniRig ParamsChange events
        private void OmniRigEngine_ParamsChange(int RigNumber, int Params)
        {
            thread1 = new Thread(new ThreadStart(ShowRigParams));
            thread1.Name = "RigParams";
            thread1.Start();
        }

        //OmniRig StatusChange events
        private void OmniRigEngine_StatusChange(int RigNumber)
        {
            thread2 = new Thread(new ThreadStart(ShowRigParams));
            thread2.Name = "RigStatus";
            thread2.Start();
        }

        private void ShowRigStatus()
        {
            if (OmniRigEngine == null || Rig == null)
            {
            }
            else
            {
                this.Dispatcher.Invoke(() =>
                {
                    TB_Frequency.BorderBrush = System.Windows.Media.Brushes.Gray;
                    if (Rig == null)
                    {
                        Status = "Omni-Rig Failed";
                        return;
                    }
                    //Status = Rig.StatusStr;
                    Status = "CAT Enabled";
                    //if (Rig.Status != OmniRig.RigStatusX.ST_ONLINE && Properties.Settings.Default.EnableOmniRigCAT)
                    //{
                    //    var response = System.Windows.Forms.MessageBox.Show("Try to recover?", "CAT connection failed", System.Windows.Forms.MessageBoxButtons.YesNo);
                    //    if (response == System.Windows.Forms.DialogResult.Yes)
                    //    {
                    //        foreach (var process in Process.GetProcessesByName("OmniRig"))
                    //        {
                    //            process.Kill();
                    //        }
                    //        UnsubscribeFromEvents();
                    //        OmniRigEngine = null;
                    //        StartOmniRig();
                    //    }
                    //    else
                    //    {
                    //        Properties.Settings.Default.EnableOmniRigCAT = false;
                    //    }
                    //    Status = Rig.StatusStr;
                    //}
                    if (!Properties.Settings.Default.EnableOmniRigCAT || Rig.Status != OmniRig.RigStatusX.ST_ONLINE)//disabled or offline -> red border
                    {
                        TB_Frequency.BorderBrush = System.Windows.Media.Brushes.Red;
                        TB_Frequency.BorderThickness = new Thickness(2);
                    }
                    else // -> normal border
                    {
                        TB_Frequency.BorderBrush = System.Windows.Media.Brushes.Gray;
                        TB_Frequency.BorderThickness = new Thickness(1);
                    }
                    Status = Rig.StatusStr;
                    if (Rig.Status == OmniRig.RigStatusX.ST_ONLINE)//online
                    {
                        Status = "Cat Enabled";
                    }
                    if (!Properties.Settings.Default.EnableOmniRigCAT)//disabled
                    {
                        Status = "CAT Disabled";
                    }
                    if (state == State.Edit)
                    {
                        Status = "Edit Mode";
                    }
                });
            }
        }

        private void ShowRigParams()
        {
            ShowRigStatus();
            if (OmniRigEngine == null || Rig == null || Rig.Status != OmniRig.RigStatusX.ST_ONLINE || !Properties.Settings.Default.EnableOmniRigCAT || Properties.Settings.Default.isManualMode || state == State.Edit)
            {
                return;
            }
            try
            {
                this.Dispatcher.Invoke(() =>
                {
                    double radioRX = (double)Rig.GetRxFrequency() / 1000000;
                    double radioTX = (double)Rig.GetTxFrequency() / 1000000;
                    if (Properties.Settings.Default.IsSatelliteMode)
                        radioRX += Properties.Settings.Default.SatelliteShift;
                    RX = radioRX.ToString("###0.000000");
                    TX = radioTX.ToString("###0.000000");
                    //TB_Frequency.Text = RX;
                    Properties.Settings.Default.Frequency = RX;
                    switch (Rig.Mode)
                    {
                        case (OmniRig.RigParamX)PM_CW_L:
                            //cmbMode.Text = cmbMode.Items[1].ToString();
                            CB_Mode.Text = "CW";
                            break;
                        case (OmniRig.RigParamX)PM_CW_U:
                            //cmbMode.Text = cmbMode.Items[0].ToString();
                            CB_Mode.Text = "CW";
                            break;
                        case (OmniRig.RigParamX)PM_SSB_L:
                            //cmbMode.Text = cmbMode.Items[3].ToString();
                            CB_Mode.Text = "SSB";
                            break;
                        case (OmniRig.RigParamX)PM_SSB_U:
                            // cmbMode.Text = cmbMode.Items[2].ToString();
                            CB_Mode.Text = "SSB";
                            break;
                        case (OmniRig.RigParamX)PM_FM:
                            // cmbMode.Text = cmbMode.Items[7].ToString();
                            CB_Mode.Text = "FM";
                            break;
                        case (OmniRig.RigParamX)PM_AM:
                            // cmbMode.Text = cmbMode.Items[7].ToString();
                            CB_Mode.Text = "AM";
                            break;
                        case (OmniRig.RigParamX)PM_DIG_L:
                            // cmbMode.Text = cmbMode.Items[7].ToString();
                            CB_Mode.Text = "DIGI";
                            break;
                        case (OmniRig.RigParamX)PM_DIG_U:
                            // cmbMode.Text = cmbMode.Items[7].ToString();
                            CB_Mode.Text = "DIGI";
                            break;
                        default:
                            CB_Mode.Text = "DIGI";
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

        private void PreventSpaceInCallsign(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Space)
            {
                e.Handled = true;
            }
        }

        
    }
}