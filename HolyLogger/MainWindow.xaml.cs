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
using System.Windows.Documents;
using System.Net.NetworkInformation;
using System.Windows.Media;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Windows.Controls.Primitives;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Text.RegularExpressions;
using System.Data.SQLite;

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
        TextBlock TB_FrequencyDisplay;

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
        private bool hasRestoredMainWindowBounds = false;

        public bool isNetworkAvailable { get; set; }

        HolyLogParser _holyLogParser;
        Process QRZProcess;

        LogUploadWindow logupload = null;
        SignboardWindow signboard = null;
        TimerWindow timerscreen = null;
        MatrixWindow matrix = null;
        Window clusterWindow = null;
        Window clusterSettingsWindow = null;
        ClientWebSocket clusterWebSocket = null;
        CancellationTokenSource clusterWebSocketCts = null;
        long clusterLastSpotTime = 0;
        static readonly string ClusterLogPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "HolyLogger", "cluster_connection.log");
        HashSet<string> clusterSpotKeys = new HashSet<string>(StringComparer.Ordinal);
        List<ClusterSpotViewItem> clusterAllSpots = new List<ClusterSpotViewItem>();
        ObservableCollection<ClusterSpotViewItem> clusterVisibleSpots = null;
        HashSet<string> clusterWorkedCountries = null;
        TextBlock clusterActiveBandIndicatorText = null;
        Button clusterBandFilterAllBtn = null;
        Button clusterBandFilterPreSelectedBtn = null;
        Button clusterBandFilterActiveBtn = null;
        StackPanel clusterShowBandsPanel = null;
        TextBlock clusterShowBandsLabelText = null;
        TextBlock clusterNewCountryLegendText = null;
        StackPanel clusterOnMyFreqLegendItem = null;
        Canvas clusterHeaderCanvas = null;
        DataGridColumn clusterDxColumn = null;
        DataGridColumn clusterSpotterColumn = null;
        DataGridColumn clusterFreqColumn = null;
        DataGridColumn clusterUtcColumn = null;
        DataGrid clusterSpotsDataGrid = null;
        ScrollViewer clusterSpotsScrollViewer = null;
        bool clusterTableMarginInitialized = false;
        StackPanel clusterLastMinutesFilterPanel = null;
        ComboBox clusterLastMinutesComboBox = null;
        int clusterLastMinutesFilterValue = 60;
        DispatcherTimer clusterSingleClickOpenQrzTimer = null;
        string clusterPendingQrzCallsign = null;
        DataGridColumn clusterLastHoverToolTipColumn = null;
        ToolTip clusterHoverToolTip = null;
        bool clusterHoverPopupEnabled = true;
        Button clusterUndoButton = null;
        TextBlock clusterUndoCountText = null;
        TextBlock clusterSpotCountText = null;
        Stack<(string FrequencyText, string ModeText, string DxCallsignText)> clusterUndoStates = new Stack<(string FrequencyText, string ModeText, string DxCallsignText)>();
        bool clusterHeaderAlignmentRefreshPending = false;
        Action _clusterWidthHandlerCleanup = null;

        // Layout constants for the cluster window floating overlay panels
        const double ClusterOffScreenPosition = -400;
        const double ClusterHeaderCanvasHeight = 92;
        const double ClusterTableTopGap = 10;
        const double ClusterShowBandsPanelWidth = 115;
        const double ClusterBaseSharedVerticalShift = -45.0;
        const double ClusterLastMinutesDropdownTop = -45.0;
        const double ClusterLastMinutesDropdownWidth = 44;

        // Extra column references needed for width persistence on close
        DataGridColumn clusterCountryColumn = null;
        DataGridColumn clusterModeColumn = null;
        DataGridColumn clusterCommentColumn = null;
        DispatcherTimer _mapUpdateDebounceTimer = null;
        bool _dxQsoInProgress = false;
        LogInfoWindow loginfo = null;
        AboutWindow about = null;
        OptionsWindow options = null;
        QRZPhotoWindow qrzPhotoWindow = null;
        double? qrzPhotoLeft = null;
        double? qrzPhotoTop = null;
        double? qrzPhotoWidth = null;
        double? qrzPhotoHeight = null;

        BackgroundWorker AdifHandlerWorker;
        //BackgroundWorker EntireLogQrzWorker;

        private StickyWindow _stickyWindow;
        private State state = State.New;
        private bool NotifyVersionUpToDate = false;

        QSO QsoToUpdate;
        QSO QsoPreUpdate;
        QSO LastQSO;

        private List<string> callsignIndex = new List<string>();
        private bool isApplyingSuggestion = false;
        private const int DefaultCallsignSuggestionRows = 20;
        private const int MinCallsignSuggestionRows = 10;
        private const int MaxCallsignSuggestionRows = 30;
        private const double CallsignSuggestionRowHeight = 22;
        private const int CallsignLookupDebounceMs = 280;
        private int maxCallsignSuggestions = DefaultCallsignSuggestionRows;
        private bool callsignSuggestionMouseControl = false;
        private HashSet<string> newCallsignsSet = new HashSet<string>(StringComparer.Ordinal);
        private CallsignUploader _callsignUploader;
        private int callsignListVersion = 0;

        DispatcherTimer UTCTimer = new DispatcherTimer();
        DispatcherTimer HeartbeatTimer = new DispatcherTimer();
        DispatcherTimer CallsignLookupDebounceTimer = new DispatcherTimer();
        DispatcherTimer VoiceMessageAvailabilityTimer = new DispatcherTimer();
        System.Windows.Forms.Timer NewDXCCTimer = new System.Windows.Forms.Timer();

        // High-Priority Stability Improvements
        private static readonly HttpClient _sharedHttpClient = new HttpClient(new WebRequestHandler { CachePolicy = new HttpRequestCachePolicy(HttpRequestCacheLevel.BypassCache) }) { Timeout = TimeSpan.FromSeconds(20) };
        private readonly object _syncLock = new object();

        private string title = "HolyLogger   V" + System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString(3) + "   ";
        private const int SEND_CHUNK_SIZE = 50;
        private const string SpotClusterHost = "dxc.ai9t.com";
        private const int SpotClusterPort = 7300;
        private const int SpotClusterConnectAttempts = 5;
        private const int SpotClusterConnectTimeoutMs = 3000;
        private const int SpotClusterReadTimeoutMs = 10000;
        private const string HolyClusterWebSocketUrl = "wss://holycluster.iarc.org/spots_ws";

        private sealed class RadioVoiceCommandProfile
        {
            public RadioVoiceCommandProfile(string message1, string message2, string message3, string message4, string stop)
            {
                MessageCommands = new[] { message1, message2, message3, message4 };
                StopCommand = stop;
            }

            public string[] MessageCommands { get; }

            public string StopCommand { get; }
        }

        private static readonly Dictionary<string, RadioVoiceCommandProfile> VoiceCommandProfiles = new Dictionary<string, RadioVoiceCommandProfile>(StringComparer.OrdinalIgnoreCase)
        {
            { "IC-7300", new RadioVoiceCommandProfile("FE FE 94 E0 28 00 01 FD", "FE FE 94 E0 28 00 02 FD", "FE FE 94 E0 28 00 03 FD", "FE FE 94 E0 28 00 04 FD", "FE FE 94 E0 28 00 00 FD") },
            { "IC-7300MK2", new RadioVoiceCommandProfile("FE FE B6 E0 28 00 01 FD", "FE FE B6 E0 28 00 02 FD", "FE FE B6 E0 28 00 03 FD", "FE FE B6 E0 28 00 04 FD", "FE FE B6 E0 28 00 00 FD") },
            { "IC-7610", new RadioVoiceCommandProfile("FE FE 98 E0 28 00 01 FD", "FE FE 98 E0 28 00 02 FD", "FE FE 98 E0 28 00 03 FD", "FE FE 98 E0 28 00 04 FD", "FE FE 98 E0 28 00 00 FD") },
            { "K3", new RadioVoiceCommandProfile("SWT11;", "SWT12;", "SWT13;", "SWT24;", "SWT27;") },
            { "FTDX10", new RadioVoiceCommandProfile("PB01;", "PB02;", "PB03;", "PB04;", "PB00;") },
            { "FTDX101D", new RadioVoiceCommandProfile("PB01;", "PB02;", "PB03;", "PB04;", "PB00;") },
            { "FTDX3000", new RadioVoiceCommandProfile("PB01;", "PB02;", "PB03;", "PB04;", "PB00;") },
            { "FT-891", new RadioVoiceCommandProfile("PB01;", "PB02;", "PB03;", "PB04;", "PB00;") },
            { "FT-891 - DATA", new RadioVoiceCommandProfile("PB01;", "PB02;", "PB03;", "PB04;", "PB00;") },
        };

        private int? pendingVoiceMessageNumber;
        private int? activeVoiceMessageNumber;
        private DateTime pendingVoiceMessageDeadlineUtc;
        private static readonly SolidColorBrush VoiceMessageDefaultBrush = new SolidColorBrush(Color.FromRgb(0xE6, 0xCC, 0xFF));
        private static readonly SolidColorBrush VoiceMessageActiveBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xC9, 0x57));

        BitmapImage qrz_path = new BitmapImage(new Uri("Images/qrz.png", UriKind.Relative));
        BitmapImage lock_path = new BitmapImage(new Uri("Images/lock.png", UriKind.Relative));
        BitmapImage unlock_path = new BitmapImage(new Uri("Images/unlock.png", UriKind.Relative));

        List<string> ImportFileQ = new List<string>();

        public static UdpClient Client;
        public static UdpClient N1MMClient;

        string MachineName = "Default";

        public MainWindow()
        {
            MachineName = Environment.MachineName;
            LoadQrzPhotoWindowBoundsFromDisk();

            Qsos = new ObservableCollection<QSO>();
            rem = new EntityResolver();
            InitializeComponent();

            TB_FrequencyDisplay = new TextBlock
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(228, 57, 0, 0),
                Width = 79,
                Height = 22,
                FontSize = 16,
                Foreground = System.Windows.Media.Brushes.Black,
                Background = System.Windows.Media.Brushes.Transparent,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
            Grid.SetRow(TB_FrequencyDisplay, 1);
            ((Grid)this.Content).Children.Add(TB_FrequencyDisplay);

            TB_Frequency.GotFocus += TB_Frequency_GotFocus;
            TB_Frequency.LostFocus += TB_Frequency_LostFocus;
            UpdateFrequencyDisplay();

            ApplyMainFormBackgroundFromSettings();
            ApplyQsoTableHeaderBackgroundFromSettings();

            isInitializeComponentsComplete = true;
            ApplyCallsignSuggestionRowsSetting();
            LoadCallsignIndex();
            FetchCallsignListUpdateInfoFireAndForget();
            LoadNewCallsignsSet();
            _callsignUploader = new CallsignUploader(AppDomain.CurrentDomain.BaseDirectory);
            _callsignUploader.TrySendFireAndForget();

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

            if (Properties.Settings.Default.EnableN1MMUDPClient)
            {
                try
                {
                    N1MMClient = new UdpClient(Properties.Settings.Default.N1MMUDPPort);//12060
                    N1MMClient.BeginReceive(new AsyncCallback(StartN1MMUDPClient), null);
                }
                catch
                {
                    System.Windows.Forms.MessageBox.Show("Failed to open N1MM+ UDP port");
                    Properties.Settings.Default.EnableN1MMUDPClient = false;
                }
            }

            isNetworkAvailable = Helper.CheckForInternetConnection();
            HeartbeatTimer.Tick += HeartbeatTimer_Tick;
            CallsignLookupDebounceTimer.Interval = TimeSpan.FromMilliseconds(CallsignLookupDebounceMs);
            CallsignLookupDebounceTimer.Tick += CallsignLookupDebounceTimer_Tick;
            VoiceMessageAvailabilityTimer.Interval = TimeSpan.FromMilliseconds(500);
            VoiceMessageAvailabilityTimer.Tick += VoiceMessageAvailabilityTimer_Tick;
            VoiceMessageAvailabilityTimer.Start();
            _mapUpdateDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            _mapUpdateDebounceTimer.Tick += (s, e) => { _mapUpdateDebounceTimer.Stop(); DoUpdateClusterSpotsOnMap(); };
            UpdateVoiceMessageAvailabilityState();
            checkForAutoUpload();
            

            if (Properties.Settings.Default.ShowTitleClock)
                this.Title = title + DateTime.UtcNow.Hour.ToString("D2") + ":" + DateTime.UtcNow.Minute.ToString("D2") + ":" + DateTime.UtcNow.Second.ToString("D2") + " UTC";

            NetworkFlagItem.Visibility = Properties.Settings.Default.ShowNetworkFlag ? Visibility.Visible : Visibility.Collapsed;
            UpdateShareIconVisibility();

            if (Properties.Settings.Default.UpdateSettings)
            {
                Properties.Settings.Default.Upgrade();
                Properties.Settings.Default.UpdateSettings = false;
                Properties.Settings.Default.Save();
            }

            NormalizeEnterKeyBehaviorSettings();

            if (Properties.Settings.Default.isAutoCheckUpdates && isNetworkAvailable)
            {
                NotifyVersionUpToDate = false;
                // Defer the update check until after the main window is initialized and shown so any dialogs are
                // owned by the main window rather than the splash window (prevents them from being closed with the splash).
                Dispatcher.BeginInvoke(new Action(() => UpdatesMenuItem_Click(null, null)), DispatcherPriority.ApplicationIdle);
            }
            this.Loaded += MainWindow_Loaded; ;
            this.PropertyChanged += MainWindow_PropertyChanged;
            Properties.Settings.Default.PropertyChanged += Settings_PropertyChanged;

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
            setLockCommentBtnState();

            try
            {
                dal = DataAccess.GetInstance();
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
            gridColumnOrder.Add(new KeyValuePair<string, int>("RST-R", Properties.Settings.Default.RSTrcvd_index));
            gridColumnOrder.Add(new KeyValuePair<string, int>("RST-S", Properties.Settings.Default.RSTsent_index));
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

            NewDXCCTimer.Interval = 2500;    // or whatever you need it to be
            NewDXCCTimer.Tick += NewDXCCTimer_Tick;
        }

        private void NormalizeEnterKeyBehaviorSettings()
        {
            bool addQsoWithEnter = Properties.Settings.Default.AddQSOWithEnter;
            bool doNothing = Properties.Settings.Default.DoNothing;

            // Keep the Enter behavior options mutually exclusive and never both unchecked.
            if (!addQsoWithEnter && !doNothing)
            {
                Properties.Settings.Default.DoNothing = true;
                Properties.Settings.Default.Save();
            }
            else if (addQsoWithEnter && doNothing)
            {
                Properties.Settings.Default.DoNothing = false;
                Properties.Settings.Default.Save();
            }
        }

        private async void StartUDPClient(IAsyncResult res)
        {
            try
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

                    lock (_syncLock)
                    {
                        if (!string.IsNullOrWhiteSpace(qso.Freq))
                        {
                            qso.Band = HolyLogParser.convertFreqToBand(qso.Freq);
                        }
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
            catch (ObjectDisposedException) { /* socket closed during shutdown ק expected */ }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("StartUDPClient error: " + ex.Message);
            }
        }

        private void StartN1MMUDPClient(IAsyncResult res)
        {
            try
            {
            if (!Properties.Settings.Default.EnableN1MMUDPClient)
            {
                return;
            }
            IPEndPoint RemoteIpEndPoint = new IPEndPoint(IPAddress.Any, 0);
            byte[] received = N1MMClient.EndReceive(res, ref RemoteIpEndPoint);
            string data = Encoding.UTF8.GetString(received);

            this.Dispatcher.Invoke(() =>
            {
                try
                {
                    Regex regex = new Regex(@"<TXFreq>(.*)?<", RegexOptions.IgnoreCase);
                    Match match = regex.Match(data);
                    if (match.Success)
                    {
                        string freq_str = Regex.Split(data, @"<TXFreq>(.*)?<", RegexOptions.IgnoreCase)[1].Trim().ToUpper();
                        double freq = 0;
                        if (double.TryParse(freq_str,out freq))
                        {
                            TB_Frequency.Text = (freq / 100).ToString("F2");
                        }
                    }

                    regex = new Regex(@"<Mode>(.*)?<", RegexOptions.IgnoreCase);
                    match = regex.Match(data);
                    if (match.Success)
                    {
                        string mode = Regex.Split(data, @"<Mode>(.*)?<", RegexOptions.IgnoreCase)[1].Trim().ToUpper();
                        if (mode == "SSB" || mode == "LSB" || mode == "USB") mode = "SSB";
                        if (mode == "RTTY" || mode == "RTTY-R" || mode == "RTTY-L" || mode == "AFSK" || mode == "AFSK-R" || mode == "AFSK-L") mode = "DIGI";
                        bool item_found = false;
                        foreach (ComboBoxItem item in CB_Mode.Items)
                        {
                            if ((string)item.Content == mode)
                            {
                                CB_Mode.Text = (string)item.Content;
                                CB_Mode.SelectedItem = item;                                
                                item_found = true;
                                break;
                            }
                        }
                        if (!item_found)
                        {
                            CB_Mode.SelectedIndex = 0;
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Windows.Forms.MessageBox.Show("Failed to save QSO: " + ex.Message);
                }
            });
            N1MMClient.BeginReceive(new AsyncCallback(StartN1MMUDPClient), null);
            }
            catch (ObjectDisposedException) { /* socket closed during shutdown ק expected */ }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("StartN1MMUDPClient error: " + ex.Message);
            }
        }

        private void NetworkChange_NetworkAvailabilityChanged(object sender, NetworkAvailabilityEventArgs e)
        {
            isNetworkAvailable = e.IsAvailable;
            if (isNetworkAvailable) Helper.LoginToQRZ(out _SessionKey);
            this.Dispatcher.Invoke(() =>
            {
                NetworkFlag.Fill = isNetworkAvailable ? new SolidColorBrush(Color.FromRgb(0x00, 0xFF, 0x00)) : new SolidColorBrush(Color.FromRgb(0xFF, 0x00, 0x00));
                UpdateShareIconVisibility();
            });
        }

        private void ToggleMatrixControl()
        {
            if (Properties.Settings.Default.IsShowMatrixControl)
            {
                MatrixC.Visibility = Visibility.Visible;
                MainForm.Height = new GridLength(325);
                MapControl.Height = 325;
            }
            else
            {
                MatrixC.Visibility = Visibility.Hidden;
                MainForm.Height = new GridLength(270);
                MapControl.Height = 270;
            }
        }

        private void ToggleAzimuthControl()
        {
            if (Properties.Settings.Default.IsShowAzimuthControl)
            {
                MapControl.Visibility = Visibility.Visible;
                this.MinWidth = 1120;
                UpdateClusterSpotsOnMap();
            }
            else
            {
                MapControl.Visibility = Visibility.Hidden;
                this.MinWidth = 800;
            }
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            ApplyClusterWindowSetting();

            _stickyWindow = new StickyWindow(this);
            _stickyWindow.StickToScreen = false;
            _stickyWindow.StickToOther = true;
            _stickyWindow.StickOnResize = true;
            _stickyWindow.StickOnMove = true;

            RestartHeartbeatTimer();

            if (Properties.Settings.Default.ShowTitleClock)
                StartUTCTimer();

            MapControl.RadiusChanged += OnMapRadiusChanged;
            MapControl.SpotTuneRequested += OnMapSpotTuneRequested;
            ShowHomeMap();
        }

        private void Window_SourceInitialized(object sender, EventArgs e)
        {
            // Restore window position and size before first show.
            if (Properties.Settings.Default.MainWindowWidth > 0)
                Width = Properties.Settings.Default.MainWindowWidth;
            if (Properties.Settings.Default.MainWindowHeight > 0)
                Height = Properties.Settings.Default.MainWindowHeight;

            Left = Properties.Settings.Default.MainWindowLeft;
            Top = Properties.Settings.Default.MainWindowTop;

            hasRestoredMainWindowBounds = true;
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
            if (isNetworkAvailable && idle_t < 1000 * 60 * 5 && Properties.Settings.Default.ShowOnTheAir)
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

                // Rebuild worked countries list after deletion
                RebuildWorkedCountriesAndRefreshCluster();
            }

            if (clusterVisibleSpots != null)
            {
                Dispatcher.BeginInvoke(new Action(RefreshClusterVisibleSpots));
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
            if (!Properties.Settings.Default.isLocked) Lock_Btn.Source = unlock_path;
            else Lock_Btn.Source = lock_path;
        }

        private void LockComment_Btn_MouseUp(object sender, MouseButtonEventArgs e)
        {
            Properties.Settings.Default.isCommentLocked = !Properties.Settings.Default.isCommentLocked;
            TB_Comment.IsEnabled = !Properties.Settings.Default.isCommentLocked;
            setLockCommentBtnState();
        }

        private void setLockCommentBtnState()
        {
            if (!Properties.Settings.Default.isCommentLocked) LockComment_Btn.Source = unlock_path;
            else LockComment_Btn.Source = lock_path;
        }

        private void RefreshDateTime_Btn_MouseUp(object sender, MouseButtonEventArgs e)
        {
            TP_Date.Value = DateTime.UtcNow;
            TP_Time.Value = DateTime.UtcNow;
        }

        private void RefreshIcon_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            TP_Date.Value = DateTime.UtcNow;
            TP_Time.Value = DateTime.UtcNow;
            e.Handled = true;
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

                    AddWorkedCountryAndRefreshCluster(qso.DXCall);
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

                // Rebuild worked countries list after edit (callsign/country may have changed)
                RebuildWorkedCountriesAndRefreshCluster();

                LoadPreEditUserData();
            }
            ShowNewDXCC();
            ClearBtn_Click(null, null);
            UpdateNumOfQSOs();
            ClearMatrix();
            RestoreDataContext();
            
        }

        private void ShowNewDXCC()
        {
            var dups = from qso in Qsos where qso.Country == TB_DXCC.Text select qso;
            if (dups.Count() == 1) //if there is only one -> it is the one we just added -> it was a new one!
            {
                NewDXCC.Visibility = Visibility.Visible;
                NewDXCCTimer.Start();
            }
        }
        private void NewDXCCTimer_Tick(object sender, EventArgs e)
        {
            NewDXCCTimer.Stop();
            NewDXCC.Visibility = Visibility.Hidden;
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
            TB_State.Text = string.Empty;
            FName = string.Empty;
            Country = string.Empty;
            UpdateCountryFlag(null);
            ClearQrzPhoto();
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
            else if (e.Key == Key.F3)
            {
                SpotButton_Click(null, null);
            }
            else if (e.Key == Key.F9 || e.Key == Key.Escape)
            {
                ClearBtn_Click(null, null);
            }
            else if (e.Key >= Key.F5 && e.Key <= Key.F8)
            {
                TriggerVoiceMessage(e.Key - Key.F4);
                e.Handled = true;
            }

        }

        private void MessageButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && int.TryParse(button.Tag?.ToString(), out int messageNumber))
            {
                TriggerVoiceMessage(messageNumber);
            }
        }

        private void SpotButton_Click(object sender, RoutedEventArgs e)
        {
            Window dialog = BuildSpotDialog();
            dialog.Owner = this;
            dialog.ShowDialog();
        }

        private Window BuildSpotDialog()
        {
            Window dialog = new Window
            {
                Title = "Spot",
                Width = 420,
                Height = 265,
                MinWidth = 420,
                MinHeight = 265,
                MaxWidth = 420,
                MaxHeight = 265,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false,
                Icon = Icon
            };

            Grid grid = new Grid { Margin = new Thickness(12) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });

            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            AddSpotDialogLabel(grid, "My Callsign", 0, new Thickness(0));
            TextBox myCallsignTextBox = AddSpotDialogTextBox(grid, TB_MyCallsign.Text, 0, true, new Thickness(0));
            myCallsignTextBox.IsTabStop = false;
            myCallsignTextBox.Focusable = false;

            string defaultSpottedCallsign = string.IsNullOrWhiteSpace(TB_DXCallsign.Text)
                ? (LastQSO != null ? LastQSO.DXCall : string.Empty)
                : TB_DXCallsign.Text;

            AddSpotDialogLabel(grid, "Spotted Callsign", 1, new Thickness(0, 8, 0, 0));
            TextBox spottedCallsignTextBox = AddSpotDialogTextBox(grid, defaultSpottedCallsign, 1, false, new Thickness(0, 8, 0, 0));

            string defaultFrequency = string.IsNullOrWhiteSpace(TB_DXCallsign.Text)
                ? (LastQSO != null ? LastQSO.Freq : string.Empty)
                : TB_Frequency.Text;

            AddSpotDialogLabel(grid, "Frequency", 2, new Thickness(0, 8, 0, 0));
            TextBox frequencyTextBox = AddSpotDialogTextBox(grid, defaultFrequency, 2, false, new Thickness(0, 8, 0, 0));

            AddSpotDialogLabel(grid, "Comment", 3, new Thickness(0, 8, 0, 0), VerticalAlignment.Top);
            TextBox commentTextBox = AddSpotDialogTextBox(grid, string.Empty, 3, false, new Thickness(0, 8, 0, 0));
            commentTextBox.MaxLength = 60;

            Button sendButton = new Button
            {
                Content = "Send",
                Width = 78,
                Height = 32,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 12, 0, 0),
                FontSize = 16,
                IsDefault = true,
                IsEnabled = false
            };

            bool isSendingSpot = false;
            Action updateSendButtonState = () =>
            {
                sendButton.IsEnabled = !isSendingSpot
                    && !string.IsNullOrWhiteSpace(spottedCallsignTextBox.Text)
                    && !string.IsNullOrWhiteSpace(frequencyTextBox.Text);
            };

            spottedCallsignTextBox.TextChanged += (s, args) => updateSendButtonState();
            frequencyTextBox.TextChanged += (s, args) => updateSendButtonState();
            updateSendButtonState();

            sendButton.Click += async (s, args) =>
            {
                if (isSendingSpot)
                {
                    return;
                }

                isSendingSpot = true;
                updateSendButtonState();
                dialog.Cursor = Cursors.Wait;

                try
                {
                    await SubmitSpotToClusterAsync(
                        myCallsignTextBox.Text,
                        spottedCallsignTextBox.Text,
                        frequencyTextBox.Text,
                        commentTextBox.Text);

                    await ShowTimedSpotSuccessWindowAsync(dialog);
                    dialog.Close();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(dialog, ex.Message, "Spot Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                finally
                {
                    isSendingSpot = false;
                    dialog.Cursor = null;
                    if (dialog.IsLoaded)
                    {
                        updateSendButtonState();
                    }
                }
            };
            Grid.SetRow(sendButton, 5);
            Grid.SetColumn(sendButton, 1);
            grid.Children.Add(sendButton);

            dialog.Content = grid;
            dialog.KeyDown += (s, args) =>
            {
                if (args.Key == Key.Escape)
                {
                    dialog.Close();
                }
            };

            dialog.Loaded += (s, args) => commentTextBox.Focus();
            return dialog;
        }

        private static void AddSpotDialogLabel(Grid grid, string content, int row, Thickness margin, VerticalAlignment verticalAlignment = VerticalAlignment.Center)
        {
            Label label = new Label
            {
                Content = content,
                VerticalAlignment = verticalAlignment,
                FontSize = 16,
                Margin = margin
            };
            Grid.SetRow(label, row);
            Grid.SetColumn(label, 0);
            grid.Children.Add(label);
        }

        private static TextBox AddSpotDialogTextBox(Grid grid, string text, int row, bool isReadOnly, Thickness margin)
        {
            TextBox textBox = new TextBox
            {
                Text = text ?? string.Empty,
                Height = 28,
                FontSize = 16,
                IsReadOnly = isReadOnly,
                Margin = margin,
                CharacterCasing = CharacterCasing.Upper,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            Grid.SetRow(textBox, row);
            Grid.SetColumn(textBox, 1);
            grid.Children.Add(textBox);
            return textBox;
        }

        private static async Task ShowTimedSpotSuccessWindowAsync(Window owner)
        {
            Window successWindow = new Window
            {
                Title = "Spot",
                Width = 300,
                Height = 120,
                MinWidth = 300,
                MinHeight = 120,
                MaxWidth = 300,
                MaxHeight = 120,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false,
                Owner = owner,
                Background = new SolidColorBrush(Color.FromRgb(0xD9, 0xF7, 0xD6)),
                Content = new Grid
                {
                    Margin = new Thickness(12),
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "Spot sent successfully.",
                            FontSize = 18,
                            FontWeight = FontWeights.SemiBold,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center,
                            TextAlignment = TextAlignment.Center,
                            Foreground = Brushes.DarkGreen
                        }
                    }
                }
            };

            TaskCompletionSource<bool> closedTcs = new TaskCompletionSource<bool>();
            successWindow.Closed += (s, e) => closedTcs.TrySetResult(true);
            successWindow.Show();

            Task delayTask = Task.Delay(2000);
            Task completedTask = await Task.WhenAny(delayTask, closedTcs.Task);

            if (completedTask == delayTask && successWindow.IsVisible)
            {
                successWindow.Close();
                await closedTcs.Task;
            }
        }

        private async Task SubmitSpotToClusterAsync(string spotterCallsign, string dxCallsign, string frequencyText, string comment)
        {
            List<string> clusterLines = new List<string>();
            string spotCommand = null;

            spotterCallsign = (spotterCallsign ?? string.Empty).Trim().ToUpperInvariant();
            dxCallsign = (dxCallsign ?? string.Empty).Trim().ToUpperInvariant();
            comment = ((comment ?? string.Empty).Trim()).Replace("\r", " ").Replace("\n", " ");

            if (string.IsNullOrWhiteSpace(spotterCallsign))
            {
                throw new InvalidOperationException("My Callsign is missing.");
            }

            if (string.IsNullOrWhiteSpace(dxCallsign))
            {
                throw new InvalidOperationException("Spotted Callsign is missing.");
            }

            double frequency;
            if (!double.TryParse(frequencyText, NumberStyles.Float, CultureInfo.InvariantCulture, out frequency)
                && !double.TryParse(frequencyText, NumberStyles.Float, CultureInfo.CurrentCulture, out frequency))
            {
                throw new InvalidOperationException("Frequency is invalid.");
            }

            if (frequency < 1000)
            {
                frequency *= 1000;
            }

            string normalizedFrequency = frequency.ToString("0.0###############", CultureInfo.InvariantCulture);

            try
            {
                using (TcpClient client = await ConnectToSpotClusterAsync())
                using (NetworkStream networkStream = client.GetStream())
                using (StreamReader reader = new StreamReader(networkStream, Encoding.UTF8, false, 1024, true))
                using (StreamWriter writer = new StreamWriter(networkStream, new UTF8Encoding(false), 1024, true))
                {
                    writer.NewLine = "\n";
                    writer.AutoFlush = true;

                    await ExpectClusterLineAsync(
                        reader,
                        line => line.IndexOf("Please enter your call:", StringComparison.OrdinalIgnoreCase) >= 0,
                        null,
                        "Initial connection to the cluster failed.",
                        clusterLines);

                    await writer.WriteLineAsync(spotterCallsign);

                    await ExpectClusterLineAsync(
                        reader,
                        line => line.IndexOf("Hello", StringComparison.OrdinalIgnoreCase) >= 0,
                        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            { "is not a valid callsign", "Login failed: invalid spotter callsign." }
                        },
                        "Login to the cluster failed.",
                        clusterLines);

                    spotCommand = string.Format(
                        CultureInfo.InvariantCulture,
                        "DX {0} {1}{2}",
                        normalizedFrequency,
                        dxCallsign,
                        string.IsNullOrWhiteSpace(comment) ? string.Empty : " " + comment);

                    await writer.WriteLineAsync(spotCommand);

                    await ExpectClusterLineAsync(
                        reader,
                        line => IsSpotConfirmationLine(line, spotterCallsign, dxCallsign, frequency),
                        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            { "command error", "The cluster rejected the spot command." },
                            { "Error - DX", "The cluster rejected the spot." },
                            { "Error - invalid frequency", "The cluster rejected the frequency." },
                            { "Error - Invalid Dx Call", "The cluster rejected the DX callsign." }
                        },
                        "The cluster did not confirm that the spot was received.",
                        clusterLines);
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(ex.Message + BuildSpotDevelopmentDetails(spotCommand, clusterLines), ex);
            }
        }

        private async Task<TcpClient> ConnectToSpotClusterAsync()
        {
            Exception lastError = null;

            for (int attempt = 0; attempt < SpotClusterConnectAttempts; attempt++)
            {
                TcpClient client = new TcpClient();

                try
                {
                    await ConnectWithTimeoutAsync(client, SpotClusterHost, SpotClusterPort, SpotClusterConnectTimeoutMs);
                    return client;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    client.Dispose();
                }
            }

            throw new InvalidOperationException(
                string.Format(CultureInfo.InvariantCulture, "Failed to connect to cluster {0}:{1}.", SpotClusterHost, SpotClusterPort),
                lastError);
        }

        private static async Task ConnectWithTimeoutAsync(TcpClient client, string host, int port, int timeoutMs)
        {
            Task connectTask = client.ConnectAsync(host, port);
            Task completedTask = await Task.WhenAny(connectTask, Task.Delay(timeoutMs));

            if (completedTask != connectTask)
            {
                throw new TimeoutException();
            }

            await connectTask;
        }

        private static async Task<string> ExpectClusterLineAsync(StreamReader reader, Func<string, bool> validLine, IDictionary<string, string> invalidLines, string timeoutMessage, IList<string> clusterLines)
        {
            DateTime deadlineUtc = DateTime.UtcNow.AddMilliseconds(SpotClusterReadTimeoutMs);

            while (true)
            {
                int remainingTimeoutMs = (int)Math.Max(1, (deadlineUtc - DateTime.UtcNow).TotalMilliseconds);
                string line;

                try
                {
                    line = await ReadLineWithTimeoutAsync(reader, remainingTimeoutMs);
                }
                catch (TimeoutException)
                {
                    throw new InvalidOperationException(timeoutMessage);
                }

                if (line == null)
                {
                    throw new InvalidOperationException("The cluster connection closed unexpectedly.");
                }

                if (clusterLines != null)
                {
                    clusterLines.Add(line.TrimEnd());
                }

                if (validLine(line))
                {
                    return line.Trim();
                }

                if (invalidLines == null)
                {
                    continue;
                }

                foreach (KeyValuePair<string, string> invalidLine in invalidLines)
                {
                    if (line.IndexOf(invalidLine.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        throw new InvalidOperationException(invalidLine.Value);
                    }
                }
            }
        }

        private static string BuildSpotDevelopmentDetails(string spotCommand, IList<string> clusterLines)
        {
            StringBuilder builder = new StringBuilder();
            builder.AppendLine();
            builder.AppendLine();
            builder.AppendLine("Development details:");
            builder.Append("Sent command: ");
            builder.AppendLine(string.IsNullOrWhiteSpace(spotCommand) ? "(not sent)" : spotCommand);
            builder.AppendLine("Cluster reply:");

            if (clusterLines == null || clusterLines.Count == 0)
            {
                builder.AppendLine("(no lines received)");
            }
            else
            {
                foreach (string line in clusterLines.Skip(Math.Max(0, clusterLines.Count - 12)))
                {
                    builder.AppendLine(line);
                }
            }

            return builder.ToString();
        }

        private static bool IsSpotConfirmationLine(string line, string spotterCallsign, string dxCallsign, double expectedFrequencyKhz)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return false;
            }

            Match match = Regex.Match(
                line,
                @"DX de\s*(?<spotter>[A-Z0-9/\-]+):\s*(?<freq>[0-9]+(?:\.[0-9]+)?)\s*(?<dx>[A-Z0-9/\-]+)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

            if (!match.Success)
            {
                return false;
            }

            if (!string.Equals(match.Groups["spotter"].Value, spotterCallsign, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!string.Equals(match.Groups["dx"].Value, dxCallsign, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            double confirmedFrequencyKhz;
            if (!double.TryParse(match.Groups["freq"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out confirmedFrequencyKhz))
            {
                return true;
            }

            return Math.Abs(confirmedFrequencyKhz - expectedFrequencyKhz) <= 1.0;
        }

        private static async Task<string> ReadLineWithTimeoutAsync(StreamReader reader, int timeoutMs)
        {
            Task<string> readTask = reader.ReadLineAsync();
            Task completedTask = await Task.WhenAny(readTask, Task.Delay(timeoutMs));

            if (completedTask != readTask)
            {
                throw new TimeoutException();
            }

            return await readTask;
        }

        private void TriggerVoiceMessage(int messageNumber)
        {
            if (messageNumber < 1 || messageNumber > 4)
            {
                return;
            }

            if (!TryGetVoiceCommandProfile(out RadioVoiceCommandProfile profile, out string rigType, out string errorMessage))
            {
                MessageBox.Show(errorMessage, "Voice Message", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            int? currentMessageNumber = activeVoiceMessageNumber ?? pendingVoiceMessageNumber;

            if (currentMessageNumber.HasValue)
            {
                if (!string.IsNullOrWhiteSpace(profile.StopCommand) && !TrySendOmniRigCustomCommand(profile.StopCommand))
                {
                    MessageBox.Show("Failed to send the stop CAT command to " + rigType + ".", "Voice Message", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                ClearVoiceMessageState();

                if (currentMessageNumber.Value == messageNumber)
                {
                    return;
                }
            }

            string command = profile.MessageCommands[messageNumber - 1];

            if (string.IsNullOrWhiteSpace(command))
            {
                MessageBox.Show("No voice-message CAT command is defined for this button.", "Voice Message", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (!TrySendOmniRigCustomCommand(command))
            {
                MessageBox.Show("Failed to send the CAT command to " + rigType + ".", "Voice Message", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            pendingVoiceMessageNumber = messageNumber;
            activeVoiceMessageNumber = null;
            pendingVoiceMessageDeadlineUtc = DateTime.UtcNow.AddSeconds(30);
        }

        private bool TryGetVoiceCommandProfile(out RadioVoiceCommandProfile profile, out string rigType, out string errorMessage)
        {
            profile = null;
            if (!TryGetVoiceMessageAvailability(out rigType, out errorMessage))
            {
                return false;
            }

            profile = VoiceCommandProfiles[rigType];
            return true;
        }



        private bool TryGetVoiceMessageAvailability(out string rigType, out string errorMessage)
        {
            rigType = NormalizeRigType(Rig != null ? Rig.RigType : null);
            errorMessage = null;

            if (!Properties.Settings.Default.EnableOmniRigCAT || OmniRigEngine == null || Rig == null)
            {
                errorMessage = "OmniRig CAT is not available.";
                return false;
            }

            if (Rig.Status != OmniRig.RigStatusX.ST_ONLINE)
            {
                errorMessage = "The radio is offline.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(rigType) || !VoiceCommandProfiles.Keys.Contains(rigType))
            {
                errorMessage = "No voice-message CAT commands are defined for this radio model.";
                return false;
            }

            if (!IsVoiceMessageModeActive())
            {
                errorMessage = "Voice-message buttons are available only in SSB mode.";
                return false;
            }

            return true;
        }

        private string NormalizeRigType(string rigType)
        {
            return string.IsNullOrWhiteSpace(rigType) ? string.Empty : rigType.Trim();
        }

        private bool IsVoiceMessageModeActive()
        {
            if (Rig == null)
            {
                return false;
            }

            return string.Equals(GetNormalizedRigMode(), "SSB", StringComparison.OrdinalIgnoreCase);
        }

        private string GetNormalizedRigMode()
        {
            if (Rig == null)
            {
                return null;
            }

            switch (Rig.Mode)
            {
                case (OmniRig.RigParamX)PM_CW_L:
                case (OmniRig.RigParamX)PM_CW_U:
                    return "CW";
                case (OmniRig.RigParamX)PM_SSB_L:
                case (OmniRig.RigParamX)PM_SSB_U:
                    return "SSB";
                case (OmniRig.RigParamX)PM_FM:
                    return "FM";
                case (OmniRig.RigParamX)PM_AM:
                    return "AM";
                case (OmniRig.RigParamX)PM_DIG_L:
                case (OmniRig.RigParamX)PM_DIG_U:
                    return "DIGI";
                default:
                    return "DIGI";
            }
        }

        private bool TrySendOmniRigCustomCommand(string command)
        {
            try
            {
                byte[] rawCommand = ParseCustomCommand(command);
                Rig.SendCustomCommand(rawCommand, 0, string.Empty);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private byte[] ParseCustomCommand(string command)
        {
            string[] parts = command.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            bool isHexCommand = parts.Length > 1 && parts.All(part => part.Length == 2 && part.All(Uri.IsHexDigit));

            if (isHexCommand)
            {
                return parts.Select(part => byte.Parse(part, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToArray();
            }

            return Encoding.ASCII.GetBytes(command);
        }

        private void UpdateVoiceMessageState()
        {
            if (Rig == null)
            {
                ClearVoiceMessageState();
                return;
            }

            bool txOn = Rig.Tx == (OmniRig.RigParamX)PM_TX;

            if (pendingVoiceMessageNumber.HasValue)
            {
                if (txOn)
                {
                    activeVoiceMessageNumber = pendingVoiceMessageNumber;
                    pendingVoiceMessageNumber = null;
                }
                else if (DateTime.UtcNow >= pendingVoiceMessageDeadlineUtc)
                {
                    pendingVoiceMessageNumber = null;
                }
            }
            else if (activeVoiceMessageNumber.HasValue && !txOn)
            {
                activeVoiceMessageNumber = null;
            }

            UpdateVoiceMessageButtonHighlight();
        }

        private void ClearVoiceMessageState()
        {
            pendingVoiceMessageNumber = null;
            activeVoiceMessageNumber = null;
            pendingVoiceMessageDeadlineUtc = DateTime.MinValue;
            UpdateVoiceMessageButtonHighlight();
        }

        private void VoiceMessageAvailabilityTimer_Tick(object sender, EventArgs e)
        {
            UpdateVoiceMessageAvailabilityState();
        }

        private void UpdateVoiceMessageAvailabilityState()
        {
            if (PlayCommandsBorder == null)
            {
                return;
            }

            bool isAvailable = TryGetVoiceMessageAvailability(out _, out string errorMessage);
            PlayCommandsBorder.IsEnabled = isAvailable;
            SetVoiceMessageButtonsEnabled(isAvailable);
            PlayCommandsBorder.ToolTip = isAvailable ? "Play radio voice messages (F5-F8)" : errorMessage;

            if (!isAvailable)
            {
                ClearVoiceMessageState();
            }
        }

        private void SetVoiceMessageButtonsEnabled(bool isEnabled)
        {
            if (Btn_Msg1 != null) Btn_Msg1.IsEnabled = isEnabled;
            if (Btn_Msg2 != null) Btn_Msg2.IsEnabled = isEnabled;
            if (Btn_Msg3 != null) Btn_Msg3.IsEnabled = isEnabled;
            if (Btn_Msg4 != null) Btn_Msg4.IsEnabled = isEnabled;
        }

        private void UpdateVoiceMessageButtonHighlight()
        {
            UpdateVoiceMessageButtonHighlight(Btn_Msg1, 1);
            UpdateVoiceMessageButtonHighlight(Btn_Msg2, 2);
            UpdateVoiceMessageButtonHighlight(Btn_Msg3, 3);
            UpdateVoiceMessageButtonHighlight(Btn_Msg4, 4);
        }

        private void UpdateVoiceMessageButtonHighlight(Button button, int messageNumber)
        {
            if (button == null)
            {
                return;
            }

            bool isActive = activeVoiceMessageNumber == messageNumber;
            button.Background = isActive ? VoiceMessageActiveBrush : VoiceMessageDefaultBrush;
        }

        private void RST_GotFocus(object sender, RoutedEventArgs e)
        {
            if (((TextBox)sender).Text.Length > 0)
            {
                ((TextBox)sender).CaretIndex = 1;
                ((TextBox)sender).SelectionLength = 1;
            }
        }

        private void TB_RSTSent_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (SMeter == null) return;
            if (TB_RSTSent.Text.Length >= 2 && int.TryParse(TB_RSTSent.Text[1].ToString(), out int s))
                SMeter.SetSValue(s);
            else
                SMeter.SetSValue(0);
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
            //CultureInfo provider = CultureInfo.InvariantCulture;
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
            // Capture UI-dependent values on the calling thread before going to background work
            string overrideOperator = this.Dispatcher.Invoke(() => TB_Operator.Text);
            bool isOverride = Properties.Settings.Default.IsOverrideOperatorFromFile;
            bool isParseDuplicates = Properties.Settings.Default.IsParseDuplicates;
            bool isParseWARC = Properties.Settings.Default.IsParseWARC;
            string myCallsign = Properties.Settings.Default.my_callsign;
            List<string> files = this.Dispatcher.Invoke(() => ImportFileQ.ToList());

            int faultyQSO = 0;
            foreach (var filename in files)
            {
                string RawAdif = File.ReadAllText(filename, Encoding.UTF8);
                var parser = new HolyLogParser(RawAdif,
                    HolyLogParser.IsIsraeliStation(myCallsign) ? HolyLogParser.Operator.Israeli : HolyLogParser.Operator.Foreign,
                    isParseDuplicates, isParseWARC);
                try
                {
                    parser.Parse();
                    List<QSO> rawQSOList = parser.GetRawQSO();
                    int count = rawQSOList.Count;
                    int c = 1;
                    foreach (var rq in rawQSOList)
                    {
                        if (isOverride)
                        {
                            rq.Operator = overrideOperator;
                        }
                        try
                        {
                            lock (this)
                            {
                                dal.Insert(rq);
                            }
                            float p = (float)(c++) * 100 / count;
                            AdifHandlerWorker.ReportProgress((int)(Math.Ceiling(p)));
                        }
                        catch (Exception)
                        {
                            faultyQSO++;
                        }
                    }
                }
                catch (Exception)
                {
                    string failedFile = filename;
                    this.Dispatcher.Invoke(() =>
                        System.Windows.Forms.MessageBox.Show(failedFile + " Failed to load! check the file."));
                }
            }
            e.Result = faultyQSO;
            this.Dispatcher.Invoke(() => ImportFileQ.Clear());
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

        private void ExportMenuItem_Click(object sender, RoutedEventArgs e)
        {
            string adif = Services.GenerateAdif(dal.GetAllQSOs());
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

        private void ExportCabrilloMenuItem_Click(object sender, RoutedEventArgs e)
        {
            Contester c = new Contester();
            c.Callsign = Properties.Settings.Default.PersonalInfoCallsign;
            c.Category_Mode = Properties.Settings.Default.selectedMode;
            c.Category_Operator = Properties.Settings.Default.selectedOperator;
            c.Category_Power = Properties.Settings.Default.selectedPower;
            c.Category_Band = Properties.Settings.Default.selectedBand;
            c.Category_Overlay = Properties.Settings.Default.selectedOverlay;
            c.Contest = Properties.Settings.Default.selectedEvent;
            c.Email = Properties.Settings.Default.PersonalInfoEmail;
            c.Grid = Properties.Settings.Default.my_locator;
            c.Name = Properties.Settings.Default.PersonalInfoName;
            c.Soapbox = "HolyLogger";

            string cabrillo = Services.GenerateCabrillo(dal.GetAllQSOs(), c);
            // Displays a SaveFileDialog so the user can save the Image
            // assigned to Button2.
            SaveFileDialog saveFileDialog1 = new SaveFileDialog();
            saveFileDialog1.Filter = "Text File|*.txt|Cabrillo File|*.cbr|Log File|*.log";
            saveFileDialog1.Title = "Export Cabrillo";
            saveFileDialog1.ShowDialog();

            // If the file name is not an empty string open it for saving.
            try
            {
                if (saveFileDialog1.FileName != "")
                {
                    // Saves the Image via a FileStream created by the OpenFile method.
                    System.IO.FileStream fs = (System.IO.FileStream)saveFileDialog1.OpenFile();
                    StreamWriter sw = new StreamWriter(fs);
                    sw.Write(cabrillo);
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
            LogUploadWindow w = (LogUploadWindow)sender;
            if (Qsos.Count == 0)
            {
                System.Windows.Forms.MessageBox.Show("You can not upload empty log");
                w.Close();
                return;
            }
            
            string bareCallsign = Properties.Settings.Default.PersonalInfoCallsign;
            string country = Services.getHamQth(bareCallsign).Name;

            var progressIndicator = new Progress<int>();           

            if (w.selectedRadioEvent.Name.ToLower() == "holyland")
            {
                string UploadCabrilloToIARC_result = await UploadCabrilloToIARC(bareCallsign, w.selectedOperator.Name, w.selectedMode.Name, w.selectedBand.Name, w.selectedPower.Name, w.selectedOverlay.Name, Properties.Settings.Default.PersonalInfoEmail, Properties.Settings.Default.PersonalInfoName, country, dal.GetAllQSOs());
                w.Close();
                System.Windows.Forms.MessageBox.Show(UploadCabrilloToIARC_result);
            }
            else
            {
                string AddParticipant_result = await AddParticipant(bareCallsign, w.selectedOperator.Name, w.selectedMode.Name, w.selectedPower.Name, Properties.Settings.Default.PersonalInfoEmail, Properties.Settings.Default.PersonalInfoName, country);
                string UploadLogToIARC_result = await UploadLogToIARC(new Progress<int>(percent => w.UploadProgress = percent), dal.GetAllQSOs());
                w.Close();
                System.Windows.Forms.MessageBox.Show(UploadLogToIARC_result);
            }
            
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
            var values = new Dictionary<string, string>
                {
                    { "data", participantJSON }
                };
            var content = new FormUrlEncodedContent(values);
            try
            {
                var response = await _sharedHttpClient.PostAsync("https://tools.iarc.org/Holyland/Server/AddParticipant.php", content);
                var responseString = await response.Content.ReadAsStringAsync();
                return responseString;
            }
            catch (Exception ex)
            {
                return ex.Message + " Connection with server failed! Check your internet connection";
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

                var values = new Dictionary<string, string>
                {
                    { "data", chunkJSON }
                };
                var content = new FormUrlEncodedContent(values);
                try
                {
                    var response = await _sharedHttpClient.PostAsync("https://tools.iarc.org/Holyland/Server/AddQSO.php", content);
                    //var response = await _sharedHttpClient.PostAsync(Properties.Settings.Default.baseURL + "/Holyland/Server/AddLog.php", content);
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
            ToggleUploadProgress(Visibility.Hidden);
            if (!allSuccessfullyDone)
            {
                using (System.IO.StreamWriter file = new System.IO.StreamWriter(AppDomain.CurrentDomain.BaseDirectory + "\\UploadReport_" + DateTime.Now.Ticks.ToString() + ".txt"))
                {
                    file.Write(errorLog.ToString());
                    file.Close();
                }
            }
            return allSuccessfullyDone ? "Log sent successfully, 73!" : "Done with some errors.\r\nPlease contact support.";// "Some of the QSOs had error";
        }

        private async Task<string> UploadCabrilloToIARC(string callsign, string op, string mode, string band, string power, string overlay, string email, string name, string country, ObservableCollection<QSO> QSOList)
        {
            try
            {
                //prepare the header data
                Contester c = new Contester();
                c.Callsign = callsign.Trim();
                c.Category_Band = band.Trim();
                c.Category_Operator = op.Trim();
                c.Category_Mode = mode.Trim();
                c.Category_Power = power.Trim();
                c.Category_Overlay = overlay.Trim();
                c.Contest = "HOLYLAND";
                c.Email = email;
                c.Grid = TB_MyLocator.Text.Trim();
                c.Name = name.Trim();
                c.Country = country.Trim();
                c.Soapbox = "HolyLogger";

                //generate cabrillo
                string cabrillo = Services.GenerateCabrillo(dal.GetAllQSOs(), c);

                //set multipart
                var formData = new MultipartFormDataContent();
                var filename = callsign + "_" + DateTime.UtcNow.ToString("yyyyMMdd") + ".txt";
                formData.Add(new StringContent(cabrillo), "file", filename);

                c.filename = filename;
                c.timestamp = DateTime.UtcNow.Ticks.ToString();

                //post file
                var response = await _sharedHttpClient.PostAsync("https://tools.iarc.org/iarc/Server/ftp.php", formData);

                if (response.IsSuccessStatusCode)
                {
                    // Create a StringContent object with your JSON data
                    //set multipart
                    formData = new MultipartFormDataContent();
                    formData.Add(new StringContent(JsonConvert.SerializeObject(c).Replace("'", "")), "info");

                    try
                    {
                        // Send a POST request to the URL with the JSON data
                        //upload_log.php
                        response = await _sharedHttpClient.PostAsync("https://tools.iarc.org/iarc/Server/upload_log.php", formData);

                        // Check if the request was successful
                        if (response.IsSuccessStatusCode)
                        {
                            // Read and display the response from the PHP file
                            string responseContent = await response.Content.ReadAsStringAsync();
                            try
                            {
                                ServerResponse serverResponse = JsonConvert.DeserializeObject<ServerResponse>(responseContent);
                                if (serverResponse.Success)
                                {
                                    return "File uploaded successfully.";
                                }
                                else
                                {
                                    return serverResponse.Msg;
                                }
                            }
                            catch
                            {
                                return "Failed to send log. Please export cabrillo and send via the website";
                            }
                        }
                        else
                        {
                            return $"Error uploading file. Status code: {response.StatusCode}";
                        }
                    }
                    catch (Exception ex)
                    {
                        return $"Error uploading file";
                    }
                }
                else
                {
                    return $"Error uploading file. Status code: {response.StatusCode}";
                }
            }
            catch (Exception ex)
            {
                return $"Error uploading file: {ex.Message}";
            }
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
            string content = GenerateMultipleInsert(new List<QSO> { qso });
            var formData = new System.Collections.Generic.Dictionary<string, string>
            {
                { "insertlog", content }
            };
            var formContent = new FormUrlEncodedContent(formData);
            _sharedHttpClient.PostAsync("https://tools.iarc.org/Holyland/Server/AddLog.php", formContent)
                             .ContinueWith(_ => { });
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
                else if (item.Header.ToString() == "RST-R")
                    Properties.Settings.Default.RSTrcvd_index = item.DisplayIndex;
                else if (item.Header.ToString() == "RST-S")
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
            if (Properties.Settings.Default.EnableOmniRigCAT)
                StartOmniRig();
            UpdateStatus();
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            // Stop all timers before shutdown to prevent pending async operations
            try { if (HeartbeatTimer != null && HeartbeatTimer.IsEnabled) HeartbeatTimer.Stop(); } catch { }
            try { if (UTCTimer != null && UTCTimer.IsEnabled) UTCTimer.Stop(); } catch { }
            try { if (CallsignLookupDebounceTimer != null && CallsignLookupDebounceTimer.IsEnabled) CallsignLookupDebounceTimer.Stop(); } catch { }
            try { if (VoiceMessageAvailabilityTimer != null && VoiceMessageAvailabilityTimer.IsEnabled) VoiceMessageAvailabilityTimer.Stop(); } catch { }
            try { if (NewDXCCTimer != null) NewDXCCTimer.Stop(); } catch { }

            // Unsubscribe from network availability events
            try { NetworkChange.NetworkAvailabilityChanged -= NetworkChange_NetworkAvailabilityChanged; } catch { }

            // Dispose CallsignUploader to unsubscribe from NetworkChange events
            try { _callsignUploader?.Dispose(); } catch { }

            // Unsubscribe from MapControl events
            try { MapControl.RadiusChanged -= OnMapRadiusChanged; } catch { }
            try { MapControl.SpotTuneRequested -= OnMapSpotTuneRequested; } catch { }
        }

        private void Window_Closed(object sender, EventArgs e)
        {
            // Unsubscribe from event handlers to prevent memory leaks
            try { this.Loaded -= MainWindow_Loaded; } catch { }
            try { this.PropertyChanged -= MainWindow_PropertyChanged; } catch { }
            try { Properties.Settings.Default.PropertyChanged -= Settings_PropertyChanged; } catch { }

            if (AdifHandlerWorker != null)
            {
                try { AdifHandlerWorker.DoWork -= AdifHandlerWorker_DoWork; } catch { }
                try { AdifHandlerWorker.ProgressChanged -= AdifHandlerWorker_ProgressChanged; } catch { }
                try { AdifHandlerWorker.RunWorkerCompleted -= AdifHandlerWorker_RunWorkerCompleted; } catch { }
            }

            UTCTimer.Tick -= UTCTimer_Elapsed;
            VoiceMessageAvailabilityTimer.Tick -= VoiceMessageAvailabilityTimer_Tick;
            if (VoiceMessageAvailabilityTimer.IsEnabled)
                VoiceMessageAvailabilityTimer.Stop();
            if (OmniRigEngine != null)
            {
                OmniRigEngine.StatusChange -= OmniRigEngine_StatusChange;
                OmniRigEngine.ParamsChange -= OmniRigEngine_ParamsChange;
                Rig = null;
                OmniRigEngine = null;
            }
            NetworkChange.NetworkAvailabilityChanged -= NetworkChange_NetworkAvailabilityChanged;
            NewDXCCTimer.Stop();
            NewDXCCTimer.Tick -= NewDXCCTimer_Tick;
            NewDXCCTimer.Dispose();
            try { Client?.Close(); } catch { }
            try { N1MMClient?.Close(); } catch { }
            Properties.Settings.Default.SignBoardWindowIsOpen = Application.Current.Windows.Cast<Window>().SingleOrDefault(w => w == signboard) != null;
            Properties.Settings.Default.MatrixWindowIsOpen = Application.Current.Windows.Cast<Window>().SingleOrDefault(w => w == matrix) != null;
            Properties.Settings.Default.TimerWindowIsOpen = Application.Current.Windows.Cast<Window>().SingleOrDefault(w => w == timerscreen) != null;
            CloseClusterWebSocket();
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

            UpdateClusterFrequencyHighlight();
            UpdateFrequencyDisplay();
        }

        private void TB_Frequency_GotFocus(object sender, RoutedEventArgs e)
        {
            if (TB_FrequencyDisplay != null)
                TB_FrequencyDisplay.Visibility = Visibility.Collapsed;
            TB_Frequency.Foreground = System.Windows.Media.Brushes.Black;
        }

        private void TB_Frequency_LostFocus(object sender, RoutedEventArgs e)
        {
            UpdateFrequencyDisplay();
        }

        private void UpdateFrequencyDisplay()
        {
            if (TB_FrequencyDisplay == null) return;

            string raw = TB_Frequency.Text.Trim();
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double mhz) || mhz <= 0)
            {
                TB_FrequencyDisplay.Visibility = Visibility.Collapsed;
                return;
            }

            // Format as MHz with two dot separators: 14.312.000
            // Convert to Hz integer, then format as ##,###,### using dots
            long hz = (long)Math.Round(mhz * 1000000.0);
            // Build groups: MHz . kHz . Hz
            long mhzPart = hz / 1000000;
            long khzPart = (hz % 1000000) / 1000;
            long hzPart = hz % 1000;
            string formatted = $"{mhzPart}.{khzPart:D3}.{hzPart:D3}";

            TB_FrequencyDisplay.Text = formatted;
            bool showOverlay = !TB_Frequency.IsFocused;
            TB_FrequencyDisplay.Visibility = showOverlay ? Visibility.Visible : Visibility.Collapsed;
            TB_Frequency.Foreground = showOverlay ? System.Windows.Media.Brushes.Transparent : System.Windows.Media.Brushes.Black;
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
            options.GeneralSettingsControlControlInstance.OmniRigEngine_Changed += GeneralSettingsControlControlInstance_OmniRigEngine_Changed;
            options.GeneralSettingsControlControlInstance.Rig1 = Rig1;
            options.GeneralSettingsControlControlInstance.Rig2 = Rig2;
        }

        private void GeneralSettingsControlControlInstance_OmniRigEngine_Changed()
        {
            if (Properties.Settings.Default.EnableOmniRigCAT)
            {
                StartOmniRig();
                options.GeneralSettingsControlControlInstance.Rig1 = Rig1;
                options.GeneralSettingsControlControlInstance.Rig2 = Rig2;
            }
            else
                StopOmniRig();

            SelectRig();
            ShowRigParams();
            UpdateVoiceMessageAvailabilityState();
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

                ApplyCallsignSuggestionRowsSetting();
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
            if (Properties.Settings.Default.EnableN1MMUDPClient)
            {
                try
                {
                    if (N1MMClient == null)
                    {
                        N1MMClient = new UdpClient(Properties.Settings.Default.N1MMUDPPort);//2333 / 2237
                        N1MMClient.BeginReceive(new AsyncCallback(StartN1MMUDPClient), null);
                    }
                }
                catch
                {
                    System.Windows.Forms.MessageBox.Show("Failed to open N1MM+ UDP port");
                    Properties.Settings.Default.EnableN1MMUDPClient = false;
                }
            }
            else
            {
                if (N1MMClient != null)
                {
                    N1MMClient.Close();
                    N1MMClient = null;
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
            string url = "https://tools.iarc.org/holysquare/";
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
            string url = "https://tools.iarc.org/ontheair/";
            try
            {
                System.Diagnostics.Process.Start(url);
            }
            catch (Exception)
            {
                MessageBox.Show("Please install 'Chrome' and try again");
            }
        }

        private void ClusterMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (clusterWindow != null)
            {
                var existingWindow = Application.Current.Windows.Cast<Window>().SingleOrDefault(w => w == clusterWindow);

                if (existingWindow != null)
                {
                    existingWindow.Activate();
                    return;
                }
            }

            GenerateNewClusterWindow();
        }

        private async void GenerateNewClusterWindow()
        {
            clusterHoverPopupEnabled = LoadClusterHoverPopupSetting();
            clusterLastMinutesFilterValue = LoadClusterLastMinutesFilterSetting();

            var undoButton = BuildClusterUndoButton();
            var settingsButton = new Button
            {
                Content = "⚙",
                Width = 28,
                Height = 28,
                FontSize = 16,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Cluster settings",
                Margin = new Thickness(0, 0, 0, 8),
                Style = MakeClusterBandFilterBtnStyle(false)
            };

            var statusText = new TextBlock
            {
                Text = string.Empty,
                FontSize = 12,
                Foreground = Brushes.DimGray,
                Margin = new Thickness(0, 0, 0, 8),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
                Visibility = Visibility.Collapsed
            };

            var spotsGrid = BuildClusterSpotsGrid();
            var headerGrid = BuildClusterHeaderPanel(undoButton, settingsButton);
            var showBandsPanel = BuildClusterBandFilterPanel();
            var lastMinutesFilterPanel = BuildClusterLastMinutesPanel();

            var headerCanvas = new Canvas { Height = ClusterHeaderCanvasHeight, IsHitTestVisible = true };
            clusterHeaderCanvas = headerCanvas;
            Panel.SetZIndex(headerCanvas, 1);

            Canvas.SetTop(showBandsPanel, 0);
            Canvas.SetLeft(showBandsPanel, ClusterOffScreenPosition);
            headerCanvas.Children.Add(showBandsPanel);

            Canvas.SetTop(lastMinutesFilterPanel, 0);
            Canvas.SetLeft(lastMinutesFilterPanel, ClusterOffScreenPosition);
            headerCanvas.Children.Add(lastMinutesFilterPanel);

            var layoutGrid = new Grid { Margin = new Thickness(12, 8, 12, 12) };
            layoutGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layoutGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layoutGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            Grid.SetRow(headerGrid, 0);
            Grid.SetRow(headerCanvas, 1);
            Grid.SetRow(spotsGrid, 2);
            layoutGrid.Children.Add(headerGrid);
            layoutGrid.Children.Add(headerCanvas);
            layoutGrid.Children.Add(spotsGrid);

            clusterWindow = new Window
            {
                Title = "Cluster",
                Width = Properties.Settings.Default.ClusterWindowWidth,
                Height = Properties.Settings.Default.ClusterWindowHeight,
                MinWidth = 200,
                MinHeight = 260,
                Left = Properties.Settings.Default.ClusterWindowLeft,
                Top = Properties.Settings.Default.ClusterWindowTop,
                Content = layoutGrid
            };
            clusterWindow.Owner = this;

            clusterUndoButton = undoButton;
            clusterUndoStates.Clear();
            UpdateClusterUndoButtonState();

            settingsButton.Click += (s, e) => OpenClusterSettingsWindow();
            undoButton.Click += ClusterUndoButton_Click;

            clusterWindow.LocationChanged += ClusterWindow_LocationChanged;
            clusterWindow.SizeChanged += ClusterWindow_SizeChanged;
            clusterWindow.Closed += ClusterWindow_Closed;

            clusterWorkedCountries = GetWorkedCountriesFromLog();
            clusterWindow.Show();

            await ConnectClusterWebSocketAsync(statusText, clusterVisibleSpots);
        }

        private void ClusterWindow_Closed(object sender, EventArgs e)
        {
            Properties.Settings.Default.ClusterColWidthDX = clusterDxColumn != null ? clusterDxColumn.ActualWidth : Properties.Settings.Default.ClusterColWidthDX;
            Properties.Settings.Default.ClusterColWidthSpotter = clusterSpotterColumn != null ? clusterSpotterColumn.ActualWidth : Properties.Settings.Default.ClusterColWidthSpotter;
            if (clusterCountryColumn != null)
            {
                SaveClusterCountryColumnWidthSetting(clusterCountryColumn.ActualWidth);
                SaveClusterCountryColumnDisplayIndexSetting(clusterCountryColumn.DisplayIndex);
            }
            Properties.Settings.Default.ClusterColWidthFreq = clusterFreqColumn != null ? clusterFreqColumn.ActualWidth : Properties.Settings.Default.ClusterColWidthFreq;
            Properties.Settings.Default.ClusterColWidthUtc = clusterUtcColumn != null ? clusterUtcColumn.ActualWidth : Properties.Settings.Default.ClusterColWidthUtc;
            Properties.Settings.Default.ClusterColWidthMode = clusterModeColumn != null ? clusterModeColumn.ActualWidth : Properties.Settings.Default.ClusterColWidthMode;
            Properties.Settings.Default.ClusterColWidthComment = clusterCommentColumn != null ? clusterCommentColumn.ActualWidth : Properties.Settings.Default.ClusterColWidthComment;
            Properties.Settings.Default.Save();

            if (clusterSettingsWindow != null)
            {
                clusterSettingsWindow.Close();
                clusterSettingsWindow = null;
            }
            CloseClusterWebSocket();
            try { _clusterWidthHandlerCleanup?.Invoke(); } catch { }
            _clusterWidthHandlerCleanup = null;
            clusterVisibleSpots = null;
            clusterWorkedCountries = null;
            clusterUndoButton = null;
            clusterUndoCountText = null;
            clusterSpotCountText = null;
            clusterActiveBandIndicatorText = null;
            clusterDxColumn = null;
            clusterSpotterColumn = null;
            clusterFreqColumn = null;
            clusterUtcColumn = null;
            clusterCountryColumn = null;
            clusterModeColumn = null;
            clusterCommentColumn = null;
            clusterSpotsScrollViewer = null;
            clusterLastMinutesFilterPanel = null;
            clusterLastMinutesComboBox = null;
            clusterBandFilterAllBtn = null;
            clusterBandFilterPreSelectedBtn = null;
            clusterBandFilterActiveBtn = null;
            clusterShowBandsPanel = null;
            if (clusterSingleClickOpenQrzTimer != null)
            {
                clusterSingleClickOpenQrzTimer.Stop();
                clusterSingleClickOpenQrzTimer.Tick -= ClusterSingleClickOpenQrzTimer_Tick;
                clusterSingleClickOpenQrzTimer = null;
            }
            clusterPendingQrzCallsign = null;
            clusterLastHoverToolTipColumn = null;
            clusterHoverToolTip = null;
            clusterUndoStates.Clear();
            clusterWindow = null;
        }

        private Button BuildClusterUndoButton()
        {
            var bitmapImage = new BitmapImage();
            bitmapImage.BeginInit();
            bitmapImage.UriSource = new Uri("pack://application:,,,/Images/UNDO_Icon.png");
            bitmapImage.DecodePixelWidth = 24;
            bitmapImage.DecodePixelHeight = 24;
            bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
            bitmapImage.EndInit();
            bitmapImage.Freeze();

            var undoIcon = new Image
            {
                Source = bitmapImage,
                Width = 24,
                Height = 24,
                Stretch = Stretch.Uniform,
                SnapsToDevicePixels = true
            };
            RenderOptions.SetBitmapScalingMode(undoIcon, BitmapScalingMode.HighQuality);

            var undoCountText = new TextBlock
            {
                Text = string.Empty,
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.Black,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0),
                TextAlignment = TextAlignment.Center,
                IsHitTestVisible = false
            };
            clusterUndoCountText = undoCountText;

            var undoContentGrid = new Grid();
            undoContentGrid.Children.Add(undoIcon);
            undoContentGrid.Children.Add(undoCountText);

            var undoButton = new Button
            {
                Width = 32,
                Height = 32,
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Undo last spot tune",
                Margin = new Thickness(0, 0, 12, 8),
                IsEnabled = false,
                Opacity = 0.35,
                Content = undoContentGrid
            };

            return undoButton;
        }

        private DataGrid BuildClusterSpotsGrid()
        {
            var spotsGrid = new DataGrid
            {
                AutoGenerateColumns = false,
                IsReadOnly = true,
                CanUserAddRows = false,
                SelectionMode = DataGridSelectionMode.Single,
                SelectionUnit = DataGridSelectionUnit.FullRow,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                RowHeaderWidth = 0,
                AlternationCount = 2,
                AlternatingRowBackground = Brushes.Gainsboro,
                FontSize = 13,
                Margin = new Thickness(0, -(ClusterHeaderCanvasHeight - ClusterTableTopGap), 0, 0),
                Opacity = 1
            };
            ToolTipService.SetInitialShowDelay(spotsGrid, 50);
            ToolTipService.SetShowDuration(spotsGrid, 3000);

            clusterHoverToolTip = new ToolTip
            {
                Background = new SolidColorBrush(Color.FromRgb(0xB7, 0xE1, 0xB0)),
                Foreground = Brushes.DarkRed,
                FontWeight = FontWeights.Bold,
                BorderBrush = Brushes.IndianRed,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(6, 2, 6, 2),
                Placement = PlacementMode.RelativePoint,
                StaysOpen = true
            };

            var hiddenRowHeaderStyle = new Style(typeof(DataGridRowHeader));
            hiddenRowHeaderStyle.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Collapsed));
            spotsGrid.RowHeaderStyle = hiddenRowHeaderStyle;

            var clusterRowStyle = new Style(typeof(DataGridRow));
            clusterRowStyle.Setters.Add(new Setter(DataGridRow.BackgroundProperty, new System.Windows.Data.Binding("RowBackground")));
            clusterRowStyle.Setters.Add(new Setter(DataGridRow.FocusVisualStyleProperty, null));
            spotsGrid.RowStyle = clusterRowStyle;

            var clusterColumnHeaderStyle = new Style(typeof(DataGridColumnHeader));
            clusterColumnHeaderStyle.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(ParseQsoTableHeaderBackgroundColor(Properties.Settings.Default.QsoTableHeaderBackgroundColor))));
            clusterColumnHeaderStyle.Setters.Add(new Setter(Control.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(0x15, 0x65, 0xC0))));
            clusterColumnHeaderStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0, 0, 1, 3)));
            clusterColumnHeaderStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(5, 3, 5, 3)));
            clusterColumnHeaderStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
            spotsGrid.ColumnHeaderStyle = clusterColumnHeaderStyle;

            // DX column
            var dxColumnTemplate = new DataTemplate();
            var dxTextBlockFactory = new FrameworkElementFactory(typeof(TextBlock));
            dxTextBlockFactory.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("DXCallsign"));
            dxTextBlockFactory.SetBinding(TextBlock.FontWeightProperty, new System.Windows.Data.Binding("DXFontWeight"));
            dxTextBlockFactory.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding("DXForeground"));
            dxTextBlockFactory.SetBinding(TextBlock.BackgroundProperty, new System.Windows.Data.Binding("DXBackground"));
            dxColumnTemplate.VisualTree = dxTextBlockFactory;
            var dxHeaderStyle = new Style(typeof(DataGridColumnHeader), clusterColumnHeaderStyle);
            dxHeaderStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
            var dxColumn = new DataGridTemplateColumn { Header = "DX", HeaderStyle = dxHeaderStyle, CellTemplate = dxColumnTemplate, SortMemberPath = "DXCallsign", Width = new DataGridLength(Math.Max(40, Properties.Settings.Default.ClusterColWidthDX)) };

            // Spotter / Country columns
            var spotterColumn = new DataGridTextColumn { Header = "Spotter", Binding = new System.Windows.Data.Binding("SpotterCallsign"), Width = new DataGridLength(Math.Max(40, Properties.Settings.Default.ClusterColWidthSpotter)) };
            var countryColumn = new DataGridTextColumn { Header = "Country", Binding = new System.Windows.Data.Binding("Country"), Width = new DataGridLength(Math.Max(40, LoadClusterCountryColumnWidthSetting())) };

            // Freq column
            var freqHeaderStyle = new Style(typeof(DataGridColumnHeader), clusterColumnHeaderStyle);
            freqHeaderStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
            freqHeaderStyle.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
            freqHeaderStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(2, 1, 2, 1)));
            var freqHeaderText = new TextBlock
            {
                TextAlignment = TextAlignment.Center,
                LineHeight = 10,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                Margin = new Thickness(0, -1, 0, -1),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            freqHeaderText.Inlines.Add(new Run("Freq") { FontSize = 12, FontWeight = FontWeights.Normal });
            freqHeaderText.Inlines.Add(new LineBreak());
            freqHeaderText.Inlines.Add(new Run("MHz") { FontSize = 8, FontWeight = FontWeights.Bold });
            var freqColumn = new DataGridTextColumn { Header = freqHeaderText, HeaderStyle = freqHeaderStyle, Binding = new System.Windows.Data.Binding("FreqDisplayText"), Width = new DataGridLength(Math.Max(40, Properties.Settings.Default.ClusterColWidthFreq)) };

            // UTC column
            var utcHeaderStyle = new Style(typeof(DataGridColumnHeader), clusterColumnHeaderStyle);
            utcHeaderStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
            var utcTextStyle = new Style(typeof(TextBlock));
            utcTextStyle.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Center));
            utcTextStyle.Setters.Add(new Setter(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center));
            var utcColumn = new DataGridTextColumn { Header = "UTC", HeaderStyle = utcHeaderStyle, ElementStyle = utcTextStyle, Binding = new System.Windows.Data.Binding("TimeUtc"), Width = new DataGridLength(ClusterLastMinutesDropdownWidth), CanUserResize = false };

            // Mode column
            var modeHeaderStyle = new Style(typeof(DataGridColumnHeader), clusterColumnHeaderStyle);
            modeHeaderStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
            var modeTemplate = new DataTemplate();
            var modeTextFactory = new FrameworkElementFactory(typeof(TextBlock));
            modeTextFactory.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Mode"));
            modeTextFactory.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding("ModeForeground"));
            modeTextFactory.SetBinding(TextBlock.FontWeightProperty, new System.Windows.Data.Binding("ModeFontWeight"));
            modeTextFactory.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Center);
            modeTextFactory.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            modeTemplate.VisualTree = modeTextFactory;
            var modeColumn = new DataGridTemplateColumn { Header = "Mode", HeaderStyle = modeHeaderStyle, CellTemplate = modeTemplate, Width = new DataGridLength(Math.Max(40, Properties.Settings.Default.ClusterColWidthMode)) };

            // Comment column
            var commentHeaderStyle = new Style(typeof(DataGridColumnHeader), clusterColumnHeaderStyle);
            commentHeaderStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
            var commentColumn = new DataGridTextColumn { Header = "Comment", HeaderStyle = commentHeaderStyle, Binding = new System.Windows.Data.Binding("Comment"), MinWidth = 60, Width = new DataGridLength(1, DataGridLengthUnitType.Star) };

            // Store references needed by other methods
            clusterDxColumn = dxColumn;
            clusterSpotterColumn = spotterColumn;
            clusterFreqColumn = freqColumn;
            clusterUtcColumn = utcColumn;
            clusterCountryColumn = countryColumn;
            clusterModeColumn = modeColumn;
            clusterCommentColumn = commentColumn;

            utcColumn.SortDirection = ListSortDirection.Descending;

            spotsGrid.Columns.Add(dxColumn);
            spotsGrid.Columns.Add(spotterColumn);
            spotsGrid.Columns.Add(countryColumn);
            spotsGrid.Columns.Add(freqColumn);
            spotsGrid.Columns.Add(utcColumn);
            spotsGrid.Columns.Add(modeColumn);
            spotsGrid.Columns.Add(commentColumn);

            int countryDisplayIndex = LoadClusterCountryColumnDisplayIndexSetting();
            if (countryDisplayIndex >= 0 && countryDisplayIndex < spotsGrid.Columns.Count)
            {
                countryColumn.DisplayIndex = countryDisplayIndex;
            }

            clusterVisibleSpots = new ObservableCollection<ClusterSpotViewItem>();
            spotsGrid.ItemsSource = clusterVisibleSpots;
            spotsGrid.PreviewMouseLeftButtonDown += ClusterSpotsGrid_MouseLeftButtonDown;
            spotsGrid.MouseMove += ClusterSpotsGrid_MouseMove;
            spotsGrid.MouseLeave += ClusterSpotsGrid_MouseLeave;
            spotsGrid.SizeChanged += (s, e) => RequestClusterHeaderAlignmentRefresh();
            spotsGrid.ColumnReordered += (s, e) => RequestClusterHeaderAlignmentRefresh();
            spotsGrid.ColumnDisplayIndexChanged += (s, e) => RequestClusterHeaderAlignmentRefresh();
            AttachClusterColumnWidthTracking(dxColumn, spotterColumn, countryColumn, freqColumn, utcColumn, modeColumn, commentColumn);
            spotsGrid.Loaded += (s, e) =>
            {
                EnsureClusterGridScrollTracking();
                RequestClusterHeaderAlignmentRefresh();
            };
            clusterSpotsDataGrid = spotsGrid;

            clusterSingleClickOpenQrzTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            clusterSingleClickOpenQrzTimer.Tick += ClusterSingleClickOpenQrzTimer_Tick;

            RefreshClusterVisibleSpots();

            return spotsGrid;
        }

        private Grid BuildClusterHeaderPanel(Button undoButton, Button settingsButton)
        {
            var legendPanel = new StackPanel
            {
                Orientation = Orientation.Vertical,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, -6, 4, 0)
            };

            legendPanel.Children.Add(BuildClusterLegendTopRow());
            legendPanel.Children.Add(BuildClusterLegendItem(new SolidColorBrush(Color.FromRgb(0x00, 0x7A, 0xCC)), "Worked Before", false, new Thickness(0, 7, 0, 0)));
            legendPanel.Children.Add(BuildClusterLegendItem(Brushes.Black, "Worked Country", false, new Thickness(0, 7, 0, 0)));

            var onMyFreqLegend = BuildClusterLegendItem(new SolidColorBrush(Color.FromRgb(0x90, 0xEE, 0x90)), "On My Radio Frequency", true, new Thickness(0, 7, 0, 0));
            onMyFreqLegend.HorizontalAlignment = HorizontalAlignment.Left;
            onMyFreqLegend.VerticalAlignment = VerticalAlignment.Top;
            clusterOnMyFreqLegendItem = onMyFreqLegend;
            legendPanel.Children.Add(onMyFreqLegend);

            var spotCountText = new TextBlock
            {
                Text = "0",
                Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x8C, 0x00)),
                FontWeight = FontWeights.Bold,
                FontSize = 16,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center
            };
            clusterSpotCountText = spotCountText;

            var actionsPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, -8, 0, 0)
            };
            actionsPanel.Children.Add(undoButton);
            actionsPanel.Children.Add(settingsButton);

            var rightColumnPanel = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top
            };
            rightColumnPanel.Children.Add(actionsPanel);

            var headerGrid = new Grid { Margin = new Thickness(0, 0, 0, 0) };
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(legendPanel, 0);
            Grid.SetColumn(rightColumnPanel, 1);
            headerGrid.Children.Add(legendPanel);
            headerGrid.Children.Add(rightColumnPanel);

            return headerGrid;
        }

        private StackPanel BuildClusterLegendTopRow()
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 1)
            };
            row.Children.Add(BuildClusterLegendItem(Brushes.Red, "New Country", false, new Thickness(0, 0, 24, 0)));
            return row;
        }

        private StackPanel BuildClusterLegendItem(Brush color, string text, bool useTextBackground = false, Thickness? itemMargin = null)
        {
            var itemPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = itemMargin ?? new Thickness(0, 0, 0, 1)
            };

            if (!useTextBackground)
            {
                itemPanel.Children.Add(new Border
                {
                    Width = 20,
                    Height = 3,
                    Background = color,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 3, 0)
                });
            }

            var itemText = new TextBlock
            {
                Text = text,
                FontSize = 12,
                Background = useTextBackground ? color : Brushes.Transparent,
                Padding = useTextBackground ? new Thickness(3, 0, 3, 0) : new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center
            };
            itemPanel.Children.Add(itemText);

            if (string.Equals(text, "New Country", StringComparison.Ordinal))
            {
                clusterNewCountryLegendText = itemText;
            }

            return itemPanel;
        }

        private Style MakeClusterBandFilterBtnStyle(bool highlighted)
        {
            Color bgTop, bgBottom, fg, border, borderBottom;
            if (highlighted)
            {
                bgTop        = Color.FromRgb(0x4A, 0xA8, 0xFF);
                bgBottom     = Color.FromRgb(0x1E, 0x70, 0xCC);
                fg           = Colors.White;
                border       = Color.FromRgb(0x18, 0x60, 0xB0);
                borderBottom = Color.FromRgb(0x0E, 0x44, 0x88);
            }
            else
            {
                bgTop        = Color.FromRgb(0xF8, 0xF8, 0xF8);
                bgBottom     = Color.FromRgb(0xD0, 0xD0, 0xD0);
                fg           = Colors.Black;
                border       = Color.FromRgb(0xAA, 0xAA, 0xAA);
                borderBottom = Color.FromRgb(0x88, 0x88, 0x88);
            }

            // Build a ControlTemplate so we can apply CornerRadius
            var template = new ControlTemplate(typeof(Button));

            // Outer border ק gives the darker "bottom edge" of the key
            var outerBorderFactory = new FrameworkElementFactory(typeof(Border));
            outerBorderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
            outerBorderFactory.SetValue(Border.BackgroundProperty, new SolidColorBrush(borderBottom));
            outerBorderFactory.SetValue(Border.PaddingProperty, new Thickness(0, 0, 0, 2)); // bottom shadow

            // Inner border ק the key face with gradient
            var innerBorderFactory = new FrameworkElementFactory(typeof(Border));
            innerBorderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
            innerBorderFactory.SetValue(Border.BorderBrushProperty, new SolidColorBrush(border));
            innerBorderFactory.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            innerBorderFactory.SetValue(Border.BackgroundProperty, new LinearGradientBrush(
                new GradientStopCollection
                {
                    new GradientStop(bgTop,    0.0),
                    new GradientStop(bgBottom, 1.0)
                },
                new Point(0, 0), new Point(0, 1)));
            innerBorderFactory.SetValue(Border.PaddingProperty, new Thickness(4, 2, 4, 2));

            // Content presenter
            var cpFactory = new FrameworkElementFactory(typeof(ContentPresenter));
            cpFactory.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cpFactory.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);

            innerBorderFactory.AppendChild(cpFactory);
            outerBorderFactory.AppendChild(innerBorderFactory);
            template.VisualTree = outerBorderFactory;

            var st = new Style(typeof(Button));
            st.Setters.Add(new Setter(Button.TemplateProperty, template));
            st.Setters.Add(new Setter(Button.FontSizeProperty, 11.0));
            st.Setters.Add(new Setter(Button.ForegroundProperty, new SolidColorBrush(fg)));
            st.Setters.Add(new Setter(Button.MarginProperty, new Thickness(0, 1, 0, 1)));
            st.Setters.Add(new Setter(Button.CursorProperty, System.Windows.Input.Cursors.Hand));
            return st;
        }

        private StackPanel BuildClusterBandFilterPanel()
        {
            string currentFilterMode = Properties.Settings.Default.ClusterBandFilterMode ?? "PreSelected";
            bool isActiveModeNow = string.Equals(currentFilterMode, "Active", StringComparison.OrdinalIgnoreCase);

            var activeBandIndicator = new TextBlock
            {
                Text = FormatClusterBandDisplay(TB_Band != null ? TB_Band.Text : string.Empty),
                Foreground = isActiveModeNow
                    ? new SolidColorBrush(Color.FromRgb(0, 190, 0))
                    : (Brush)new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
                FontWeight = FontWeights.Bold,
                FontSize = 16,
                Margin = new Thickness(6, 0, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Visible
            };
            clusterActiveBandIndicatorText = activeBandIndicator;

            var showBandsLabel = new TextBlock
            {
                Text = "Show Bands",
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, -2, 0, 2)
            };
            clusterShowBandsLabelText = showBandsLabel;

            var btnAllBands = new Button { Content = "All Bands", HorizontalAlignment = HorizontalAlignment.Stretch, Style = MakeClusterBandFilterBtnStyle(string.Equals(currentFilterMode, "All", StringComparison.OrdinalIgnoreCase)) };
            var btnPreSelected = new Button { Content = "Pre Selected", HorizontalAlignment = HorizontalAlignment.Stretch, Style = MakeClusterBandFilterBtnStyle(string.Equals(currentFilterMode, "PreSelected", StringComparison.OrdinalIgnoreCase)) };
            var btnActiveBand = new Button { Content = "Active Band", HorizontalAlignment = HorizontalAlignment.Left, Style = MakeClusterBandFilterBtnStyle(string.Equals(currentFilterMode, "Active", StringComparison.OrdinalIgnoreCase)) };

            clusterBandFilterAllBtn = btnAllBands;
            clusterBandFilterPreSelectedBtn = btnPreSelected;
            clusterBandFilterActiveBtn = btnActiveBand;

            var activeBandRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            activeBandRow.Children.Add(btnActiveBand);
            activeBandRow.Children.Add(activeBandIndicator);

            var showBandsPanel = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top
            };
            showBandsPanel.Children.Add(showBandsLabel);
            showBandsPanel.Children.Add(btnAllBands);
            showBandsPanel.Children.Add(btnPreSelected);
            showBandsPanel.Children.Add(activeBandRow);
            clusterShowBandsPanel = showBandsPanel;

            btnAllBands.Click += (s, e) => ApplyClusterBandFilterMode("All");
            btnPreSelected.Click += (s, e) => ApplyClusterBandFilterMode("PreSelected");
            btnActiveBand.Click += (s, e) => ApplyClusterBandFilterMode("Active");

            return showBandsPanel;
        }

        private void ApplyClusterBandFilterMode(string newMode)
        {
            Properties.Settings.Default.ClusterBandFilterMode = newMode;
            Properties.Settings.Default.ClusterUseActiveBand = string.Equals(newMode, "Active", StringComparison.OrdinalIgnoreCase);
            Properties.Settings.Default.Save();
            if (clusterBandFilterAllBtn != null)
                clusterBandFilterAllBtn.Style = MakeClusterBandFilterBtnStyle(string.Equals(newMode, "All", StringComparison.OrdinalIgnoreCase));
            if (clusterBandFilterPreSelectedBtn != null)
                clusterBandFilterPreSelectedBtn.Style = MakeClusterBandFilterBtnStyle(string.Equals(newMode, "PreSelected", StringComparison.OrdinalIgnoreCase));
            if (clusterBandFilterActiveBtn != null)
                clusterBandFilterActiveBtn.Style = MakeClusterBandFilterBtnStyle(string.Equals(newMode, "Active", StringComparison.OrdinalIgnoreCase));
            bool isActive = string.Equals(newMode, "Active", StringComparison.OrdinalIgnoreCase);
            if (clusterActiveBandIndicatorText != null)
            {
                clusterActiveBandIndicatorText.Foreground = isActive
                    ? new SolidColorBrush(Color.FromRgb(0, 190, 0))
                    : (Brush)new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA));
                clusterActiveBandIndicatorText.Visibility = Visibility.Visible;
            }
            RefreshClusterVisibleSpots();
        }

        private StackPanel BuildClusterLastMinutesPanel()
        {
            var lastMinutesLabel = new TextBlock
            {
                Text = "Last",
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Width = ClusterLastMinutesDropdownWidth,
                HorizontalAlignment = HorizontalAlignment.Left,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 0, 0, 1)
            };

            var lastMinutesCombo = new ComboBox
            {
                Width = ClusterLastMinutesDropdownWidth,
                Height = 22,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            };
            lastMinutesCombo.Items.Add("5");
            lastMinutesCombo.Items.Add("15");
            lastMinutesCombo.Items.Add("30");
            lastMinutesCombo.Items.Add("60");
            lastMinutesCombo.SelectedItem = clusterLastMinutesFilterValue.ToString(CultureInfo.InvariantCulture);
            clusterLastMinutesComboBox = lastMinutesCombo;

            lastMinutesCombo.SelectionChanged += (s, e) =>
            {
                int selectedMinutes;
                if (int.TryParse(lastMinutesCombo.SelectedItem as string, NumberStyles.Integer, CultureInfo.InvariantCulture, out selectedMinutes) && selectedMinutes > 0)
                {
                    clusterLastMinutesFilterValue = selectedMinutes;
                    SaveClusterLastMinutesFilterSetting(clusterLastMinutesFilterValue);
                    RefreshClusterVisibleSpots();
                }
            };

            var minutesUnitLabel = new TextBlock
            {
                Text = "min",
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0)
            };

            var lastMinutesValuePanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            };
            lastMinutesValuePanel.Children.Add(lastMinutesCombo);
            lastMinutesValuePanel.Children.Add(minutesUnitLabel);

            var spotCountBadge = new Border
            {
                Width = 34,
                Height = 34,
                CornerRadius = new CornerRadius(17),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x8C, 0x00)),
                BorderThickness = new Thickness(2),
                Background = Brushes.Transparent,
                Margin = new Thickness((ClusterLastMinutesDropdownWidth - 34) / 2, 0, 0, 2),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Child = clusterSpotCountText
            };

            var lastMinutesFilterPanel = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top
            };
            lastMinutesFilterPanel.Children.Add(spotCountBadge);
            lastMinutesFilterPanel.Children.Add(lastMinutesLabel);
            lastMinutesFilterPanel.Children.Add(lastMinutesValuePanel);
            clusterLastMinutesFilterPanel = lastMinutesFilterPanel;

            return lastMinutesFilterPanel;
        }

        private void AttachClusterColumnWidthTracking(params DataGridColumn[] columns)
        {
            var widthDescriptor = DependencyPropertyDescriptor.FromProperty(DataGridColumn.WidthProperty, typeof(DataGridColumn));
            if (widthDescriptor == null || columns == null)
            {
                return;
            }

            EventHandler handler = (s, e) => RequestClusterHeaderAlignmentRefresh();

            foreach (var column in columns)
            {
                if (column == null)
                {
                    continue;
                }

                widthDescriptor.AddValueChanged(column, handler);
            }

            // Store cleanup so we can remove handlers when the cluster window closes
            var capturedColumns = columns;
            _clusterWidthHandlerCleanup = () =>
            {
                foreach (var col in capturedColumns)
                {
                    if (col != null)
                    {
                        try { widthDescriptor.RemoveValueChanged(col, handler); } catch { }
                    }
                }
            };
        }

        private void RequestClusterHeaderAlignmentRefresh()
        {
            if (clusterHeaderAlignmentRefreshPending || clusterWindow == null || clusterSpotsDataGrid == null)
            {
                return;
            }

            clusterHeaderAlignmentRefreshPending = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                UpdateClusterActiveBandIndicatorPosition();
                clusterHeaderAlignmentRefreshPending = false;
            }), DispatcherPriority.Render);
        }

        private void UpdateClusterActiveBandIndicatorPosition()
        {
            if (clusterActiveBandIndicatorText == null)
            {
                return;
            }

            double freqStart = GetClusterColumnLeft(clusterFreqColumn);
            double utcStart = GetClusterColumnLeft(clusterUtcColumn);
            double freqWidth = GetClusterColumnWidth(clusterFreqColumn);
            double horizontalOffset = clusterSpotsScrollViewer != null ? clusterSpotsScrollViewer.HorizontalOffset : 0;

            // Move Show Bands + dropdown vertically as one unit.
            // Horizontal target: band indicator center aligned to Freq column center.
            // Vertical target inside the unit: Active Band button bottom aligned to dropdown bottom.
            if (clusterShowBandsPanel != null && clusterHeaderCanvas != null)
            {
                double panelWidth = clusterShowBandsPanel.ActualWidth > 0 ? clusterShowBandsPanel.ActualWidth : ClusterShowBandsPanelWidth;
                double freqCenter = freqStart - horizontalOffset + freqWidth / 2.0;
                double indicatorCenterOffset = panelWidth;
                if (clusterActiveBandIndicatorText != null)
                {
                    try
                    {
                        Point indicatorTopInShow = clusterActiveBandIndicatorText.TranslatePoint(new Point(0, 0), clusterShowBandsPanel);
                        indicatorCenterOffset = indicatorTopInShow.X + (clusterActiveBandIndicatorText.ActualWidth / 2.0);
                    }
                    catch
                    {
                        indicatorCenterOffset = panelWidth;
                    }
                }

                double panelLeft = freqCenter - indicatorCenterOffset;
                if (panelLeft < 0) panelLeft = 0;
                Canvas.SetLeft(clusterShowBandsPanel, panelLeft);

                double showPanelTop = 0;
                if (clusterBandFilterActiveBtn != null && clusterLastMinutesComboBox != null && clusterLastMinutesFilterPanel != null)
                {
                    try
                    {
                        Point activeBtnTopInShow = clusterBandFilterActiveBtn.TranslatePoint(new Point(0, 0), clusterShowBandsPanel);
                        double activeBtnBottomOffset = activeBtnTopInShow.Y + clusterBandFilterActiveBtn.ActualHeight;

                        Point comboTopInDrop = clusterLastMinutesComboBox.TranslatePoint(new Point(0, 0), clusterLastMinutesFilterPanel);
                        double dropdownPanelTop = 0;
                        double dropdownBottomInCanvas = dropdownPanelTop + comboTopInDrop.Y + clusterLastMinutesComboBox.ActualHeight;

                        showPanelTop = dropdownBottomInCanvas - activeBtnBottomOffset;
                    }
                    catch
                    {
                        showPanelTop = 0;
                    }
                }

                double showTop = showPanelTop + ClusterBaseSharedVerticalShift;
                double dropdownTop = ClusterLastMinutesDropdownTop;

                if (clusterBandFilterActiveBtn != null && clusterOnMyFreqLegendItem != null)
                {
                    try
                    {
                        Point activeBtnOffset = clusterBandFilterActiveBtn.TranslatePoint(new Point(0, 0), clusterShowBandsPanel);
                        double activeBtnCenterInPanel = activeBtnOffset.Y + clusterBandFilterActiveBtn.ActualHeight / 2.0;

                        Point onMyFreqInCanvas = clusterOnMyFreqLegendItem.TranslatePoint(new Point(0, 0), clusterHeaderCanvas);
                        double onMyFreqCenterInCanvas = onMyFreqInCanvas.Y + clusterOnMyFreqLegendItem.ActualHeight / 2.0;

                        double delta = onMyFreqCenterInCanvas - (showTop + activeBtnCenterInPanel);
                        showTop += delta;
                        dropdownTop += delta;
                    }
                    catch
                    {
                    }
                }

                Canvas.SetTop(clusterShowBandsPanel, showTop);

                if (clusterLastMinutesFilterPanel != null)
                {
                    Canvas.SetTop(clusterLastMinutesFilterPanel, dropdownTop);
                }
            }

            if (clusterLastMinutesFilterPanel != null && clusterHeaderCanvas != null)
            {
                Canvas.SetLeft(clusterLastMinutesFilterPanel, utcStart - horizontalOffset);
            }

            }

        private double GetClusterColumnLeft(DataGridColumn targetColumn)
        {
            if (clusterSpotsDataGrid == null || targetColumn == null)
            {
                return 0;
            }

            double left = clusterSpotsDataGrid.RowHeaderActualWidth;
            foreach (var column in clusterSpotsDataGrid.Columns.OrderBy(c => c.DisplayIndex))
            {
                if (column == targetColumn)
                {
                    return left;
                }

                left += GetClusterColumnWidth(column);
            }

            return left;
        }

        private static double GetClusterColumnWidth(DataGridColumn column)
        {
            if (column == null)
            {
                return 0;
            }

            if (column.ActualWidth > 0)
            {
                return column.ActualWidth;
            }

            return column.Width.DisplayValue > 0 ? column.Width.DisplayValue : 40;
        }

        private void EnsureClusterGridScrollTracking()
        {
            if (clusterSpotsDataGrid == null)
            {
                return;
            }

            var scrollViewer = FindVisualChild<ScrollViewer>(clusterSpotsDataGrid);
            if (scrollViewer == null || scrollViewer == clusterSpotsScrollViewer)
            {
                return;
            }

            if (clusterSpotsScrollViewer != null)
            {
                clusterSpotsScrollViewer.ScrollChanged -= ClusterSpotsScrollViewer_ScrollChanged;
            }

            clusterSpotsScrollViewer = scrollViewer;
            clusterSpotsScrollViewer.ScrollChanged += ClusterSpotsScrollViewer_ScrollChanged;
            UpdateClusterActiveBandIndicatorPosition();
        }

        private void ClusterSpotsScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            UpdateClusterActiveBandIndicatorPosition();
        }

        private static T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null)
            {
                return null;
            }

            int childrenCount = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < childrenCount; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                T typedChild = child as T;
                if (typedChild != null)
                {
                    return typedChild;
                }

                T descendant = FindVisualChild<T>(child);
                if (descendant != null)
                {
                    return descendant;
                }
            }

            return null;
        }

        private void ClusterSpotsGrid_MouseMove(object sender, MouseEventArgs e)
        {
            var dataGrid = sender as DataGrid;
            if (dataGrid == null)
            {
                return;
            }

            if (!clusterHoverPopupEnabled)
            {
                if (clusterHoverToolTip != null)
                {
                    clusterHoverToolTip.IsOpen = false;
                }
                clusterLastHoverToolTipColumn = null;
            }

            Point mousePoint = e.GetPosition(dataGrid);

            DataGridCell cell = FindVisualParent<DataGridCell>(e.OriginalSource as DependencyObject);
            if (cell == null)
            {
                dataGrid.Cursor = Cursors.Arrow;
                if (clusterHoverToolTip != null)
                {
                    clusterHoverToolTip.IsOpen = false;
                }
                clusterLastHoverToolTipColumn = null;
                return;
            }

            bool isInteractiveColumn = cell.Column == clusterDxColumn || cell.Column == clusterSpotterColumn || cell.Column == clusterFreqColumn;
            dataGrid.Cursor = isInteractiveColumn ? Cursors.Hand : Cursors.Arrow;

            if (cell.Column == clusterDxColumn || cell.Column == clusterSpotterColumn)
            {
                UpdateClusterHoverToolTip(dataGrid, cell.Column, "QRZ", mousePoint);
            }
            else if (cell.Column == clusterFreqColumn)
            {
                UpdateClusterHoverToolTip(dataGrid, cell.Column, "Set Radio", mousePoint);
            }
            else
            {
                if (clusterHoverToolTip != null)
                {
                    clusterHoverToolTip.IsOpen = false;
                }
                clusterLastHoverToolTipColumn = null;
            }
        }

        private void ClusterSpotsGrid_MouseLeave(object sender, MouseEventArgs e)
        {
            var dataGrid = sender as DataGrid;
            if (dataGrid != null)
            {
                dataGrid.Cursor = Cursors.Arrow;
                if (clusterHoverToolTip != null)
                {
                    clusterHoverToolTip.IsOpen = false;
                }
                clusterLastHoverToolTipColumn = null;
            }
        }

        private void UpdateClusterHoverToolTip(DataGrid dataGrid, DataGridColumn column, string text, Point mousePoint)
        {
            if (dataGrid == null || !clusterHoverPopupEnabled)
            {
                if (clusterHoverToolTip != null)
                {
                    clusterHoverToolTip.IsOpen = false;
                }
                return;
            }

            if (clusterLastHoverToolTipColumn != column)
            {
                clusterLastHoverToolTipColumn = column;
            }

            if (clusterHoverToolTip != null)
            {
                clusterHoverToolTip.Content = text;
                clusterHoverToolTip.PlacementTarget = dataGrid;
                clusterHoverToolTip.HorizontalOffset = mousePoint.X + 12;
                clusterHoverToolTip.VerticalOffset = mousePoint.Y + 12;
                if (!clusterHoverToolTip.IsOpen)
                {
                    clusterHoverToolTip.IsOpen = true;
                }
            }
        }

        private void ClusterSpotsGrid_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var dataGrid = sender as DataGrid;
            if (dataGrid == null)
            {
                return;
            }

            DataGridCell cell = FindVisualParent<DataGridCell>(e.OriginalSource as DependencyObject);
            if (cell == null)
            {
                return;
            }

            DataGridRow row = FindVisualParent<DataGridRow>(cell);
            var spot = (row != null ? row.Item : cell.DataContext) as ClusterSpotViewItem;
            if (spot == null)
            {
                spot = dataGrid.SelectedItem as ClusterSpotViewItem;
                if (spot == null)
                {
                    return;
                }
            }

            if (e.ClickCount >= 2)
            {
                if (clusterSingleClickOpenQrzTimer != null)
                {
                    clusterSingleClickOpenQrzTimer.Stop();
                }

                clusterPendingQrzCallsign = null;
                TuneToClusterSpot(spot);
                e.Handled = true;
                return;
            }

            if (cell.Column == clusterDxColumn || cell.Column == clusterSpotterColumn)
            {
                clusterPendingQrzCallsign = cell.Column == clusterDxColumn ? spot.DXCallsign : spot.SpotterCallsign;
                if (clusterSingleClickOpenQrzTimer != null)
                {
                    clusterSingleClickOpenQrzTimer.Stop();
                    clusterSingleClickOpenQrzTimer.Start();
                }

                e.Handled = true;
                return;
            }

            // Prevent default DataGrid row selection highlight on other columns (UTC/Mode/Comment/Freq single-click).
            e.Handled = true;
        }

        private void ClusterSingleClickOpenQrzTimer_Tick(object sender, EventArgs e)
        {
            if (clusterSingleClickOpenQrzTimer != null)
            {
                clusterSingleClickOpenQrzTimer.Stop();
            }

            string callsign = (clusterPendingQrzCallsign ?? string.Empty).Trim().ToUpperInvariant();
            clusterPendingQrzCallsign = null;
            if (string.IsNullOrWhiteSpace(callsign))
            {
                return;
            }

            string url = "https://www.qrz.com/db/" + callsign;
            try
            {
                Process.Start(url);
            }
            catch
            {
            }
        }

        private static T FindVisualParent<T>(DependencyObject child) where T : DependencyObject
        {
            while (child != null)
            {
                if (child is T parent)
                {
                    return parent;
                }

                if (child is ContentElement contentElement)
                {
                    child = ContentOperations.GetParent(contentElement)
                            ?? (contentElement as FrameworkContentElement)?.Parent;
                    continue;
                }

                if (child is Visual || child is System.Windows.Media.Media3D.Visual3D)
                {
                    child = VisualTreeHelper.GetParent(child);
                    continue;
                }

                child = LogicalTreeHelper.GetParent(child);
            }

            return null;
        }

        private void ClusterWindow_LocationChanged(object sender, EventArgs e)
        {
            if (clusterWindow == null)
            {
                return;
            }

            Properties.Settings.Default.ClusterWindowLeft = clusterWindow.Left;
            Properties.Settings.Default.ClusterWindowTop = clusterWindow.Top;
            Properties.Settings.Default.Save();
        }

        private void OpenClusterSettingsWindow()
        {
            if (clusterSettingsWindow != null)
            {
                var existing = Application.Current.Windows.Cast<Window>().SingleOrDefault(w => w == clusterSettingsWindow);
                if (existing != null)
                {
                    existing.Activate();
                    return;
                }
            }

            var enabledBands = GetEnabledClusterBands();
            var enabledModes = GetEnabledClusterModes();

            var bandsPanel = new StackPanel
            {
                Margin = new Thickness(10),
                Orientation = Orientation.Vertical
            };
            var bandCheckBoxes = new List<CheckBox>();

            foreach (string band in ClusterBandOptions)
            {
                string label = Regex.IsMatch(band, "^\\d+$") ? band + "m" : band;
                var cb = new CheckBox
                {
                    Content = label,
                    Margin = new Thickness(6, 3, 6, 3),
                    IsChecked = enabledBands.Contains(band)
                };

                cb.Checked += (s, e) =>
                {
                    enabledBands.Add(band);
                    SaveEnabledClusterBands(enabledBands);
                    RefreshClusterVisibleSpots();
                };
                cb.Unchecked += (s, e) =>
                {
                    enabledBands.Remove(band);
                    SaveEnabledClusterBands(enabledBands);
                    RefreshClusterVisibleSpots();
                };

                bandCheckBoxes.Add(cb);
                bandsPanel.Children.Add(cb);
            }

            var modesPanel = new StackPanel
            {
                Margin = new Thickness(10),
                Orientation = Orientation.Vertical
            };
            var modeCheckBoxes = new List<CheckBox>();

            foreach (string modeName in ClusterModeOptions)
            {
                var cbMode = new CheckBox
                {
                    Content = modeName,
                    Margin = new Thickness(6, 3, 6, 3),
                    IsChecked = enabledModes.Contains(modeName)
                };

                cbMode.Checked += (s, e) =>
                {
                    enabledModes.Add(modeName);
                    SaveEnabledClusterModes(enabledModes);
                    RefreshClusterVisibleSpots();
                };
                cbMode.Unchecked += (s, e) =>
                {
                    enabledModes.Remove(modeName);
                    SaveEnabledClusterModes(enabledModes);
                    RefreshClusterVisibleSpots();
                };

                modeCheckBoxes.Add(cbMode);
                modesPanel.Children.Add(cbMode);
            }

            var settingsLayout = new Grid();
            settingsLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            settingsLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            settingsLayout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            settingsLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            settingsLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            settingsLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });

            var popupToggleCheckBox = new CheckBox
            {
                Content = "Turn On PopUp",
                IsChecked = clusterHoverPopupEnabled,
                Margin = new Thickness(12, 4, 12, 4),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            };
            popupToggleCheckBox.Checked += (s, e) =>
            {
                clusterHoverPopupEnabled = true;
                SaveClusterHoverPopupSetting(clusterHoverPopupEnabled);
            };
            popupToggleCheckBox.Unchecked += (s, e) =>
            {
                clusterHoverPopupEnabled = false;
                SaveClusterHoverPopupSetting(clusterHoverPopupEnabled);
                if (clusterHoverToolTip != null)
                {
                    clusterHoverToolTip.IsOpen = false;
                }
                if (clusterSpotsDataGrid != null)
                {
                    clusterSpotsDataGrid.Cursor = Cursors.Arrow;
                }
                clusterLastHoverToolTipColumn = null;
            };

            var mapToggleCheckBox = new CheckBox
            {
                Content = "Plot spots on map",
                IsChecked = Properties.Settings.Default.ClusterMapEnabled,
                Margin = new Thickness(0, 4, 12, 4),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            };
            mapToggleCheckBox.Checked += (s, e) =>
            {
                Properties.Settings.Default.ClusterMapEnabled = true;
                Properties.Settings.Default.Save();
                UpdateClusterSpotsOnMap();
            };
            mapToggleCheckBox.Unchecked += (s, e) =>
            {
                Properties.Settings.Default.ClusterMapEnabled = false;
                Properties.Settings.Default.Save();
                if (MapControl != null)
                    MapControl.ShowClusterSpots(new System.Collections.Generic.List<HolyLogger.ToolsUserControls.ClusterSpotInfo>(),
                        0, 0, GetMapRadiusKm());
            };

            // Band colors section
            var bandColorsHeader = new TextBlock
            {
                Text = "Band colors",
                Margin = new Thickness(12, 12, 12, 0),
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Bottom
            };

            var bandColorsPanel = new StackPanel { Margin = new Thickness(8, 0, 8, 4), Orientation = Orientation.Vertical };
            var currentColors = new Dictionary<string, string>(GetBandColors(), StringComparer.OrdinalIgnoreCase);

            foreach (string band in ClusterBandOptions)
            {
                string label = Regex.IsMatch(band, "^\\d+$") ? band + "m" : band;
                string hex = currentColors.ContainsKey(band) ? currentColors[band] : "#FF6600";

                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };

                var swatch = new Border
                {
                    Width = 22, Height = 22,
                    CornerRadius = new CornerRadius(3),
                    Margin = new Thickness(0, 0, 6, 0),
                    Cursor = Cursors.Hand,
                    ToolTip = "Click to change color"
                };
                try { swatch.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
                catch { swatch.Background = new SolidColorBrush(Colors.OrangeRed); }

                var bandLabel = new TextBlock
                {
                    Text = label,
                    Width = 40,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontWeight = FontWeights.SemiBold
                };

                string capturedBand = band;
                swatch.MouseLeftButtonUp += (s, e) =>
                {
                    var dlg = new System.Windows.Forms.ColorDialog
                    {
                        FullOpen = true
                    };
                    try
                    {
                        var cur = (Color)ColorConverter.ConvertFromString(currentColors.ContainsKey(capturedBand) ? currentColors[capturedBand] : "#FF6600");
                        dlg.Color = System.Drawing.Color.FromArgb(cur.R, cur.G, cur.B);
                    }
                    catch { }

                    if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                    {
                        string newHex = string.Format("#{0:X2}{1:X2}{2:X2}", dlg.Color.R, dlg.Color.G, dlg.Color.B);
                        currentColors[capturedBand] = newHex;
                        try { swatch.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(newHex)); }
                        catch { }
                        SaveBandColors(currentColors);
                        UpdateClusterSpotsOnMap();
                    }
                };

                row.Children.Add(swatch);
                row.Children.Add(bandLabel);
                bandColorsPanel.Children.Add(row);
            }

            var bandColorsScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = bandColorsPanel,
                Margin = new Thickness(0, 0, 0, 4)
            };

            var resetColorsBtn = new Button
            {
                Content = "Reset to defaults",
                Margin = new Thickness(12, 0, 12, 6),
                Padding = new Thickness(8, 3, 8, 3),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            resetColorsBtn.Click += (s, e) =>
            {
                currentColors.Clear();
                foreach (var kv in DefaultBandColors) currentColors[kv.Key] = kv.Value;
                SaveBandColors(currentColors);
                // Rebuild the swatch visuals
                foreach (StackPanel row2 in bandColorsPanel.Children.OfType<StackPanel>())
                {
                    var sw = row2.Children.OfType<Border>().FirstOrDefault();
                    var bl = row2.Children.OfType<TextBlock>().FirstOrDefault();
                    if (sw != null && bl != null)
                    {
                        string bName = Regex.IsMatch(bl.Text, "^\\d+m$") ? bl.Text.TrimEnd('m') : bl.Text;
                        if (DefaultBandColors.TryGetValue(bName, out string defHex))
                        {
                            try { sw.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(defHex)); } catch { }
                        }
                    }
                }
                UpdateClusterSpotsOnMap();
            };

            var header = new TextBlock
            {
                Text = "Pre Selected Bands",
                Margin = new Thickness(12, 12, 12, 0),
                FontSize = 15,
                FontWeight = FontWeights.SemiBold
            };
            var bandsScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = bandsPanel,
                Margin = new Thickness(0, 2, 0, 4),
                IsEnabled = true,
                Opacity = 1.0
            };



            var modesHeader = new TextBlock
            {
                Text = "Visible modes",
                Margin = new Thickness(12, 12, 12, 0),
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Bottom
            };

            var modesScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = modesPanel,
                Margin = new Thickness(0, 0, 0, 4)
            };

            var showAllModesButton = new Button
            {
                Content = "show all modes",
                Margin = new Thickness(0, 0, 0, 6),
                Padding = new Thickness(8, 3, 8, 3),
                HorizontalAlignment = HorizontalAlignment.Left,
                MinWidth = 120
            };
            showAllModesButton.Click += (s, e) =>
            {
                enabledModes.Clear();
                foreach (string m in ClusterModeOptions)
                {
                    enabledModes.Add(m);
                }

                SaveEnabledClusterModes(enabledModes);

                foreach (var cb in modeCheckBoxes)
                {
                    cb.IsChecked = true;
                }

                RefreshClusterVisibleSpots();
            };
            modesPanel.Children.Insert(0, showAllModesButton);


            Grid.SetRow(header, 0);
            Grid.SetColumn(header, 0);

            Grid.SetRow(modesHeader, 0);
            Grid.SetColumn(modesHeader, 1);

            Grid.SetRow(bandColorsHeader, 0);
            Grid.SetColumn(bandColorsHeader, 2);



            Grid.SetRow(bandColorsScroll, 2);
            Grid.SetColumn(bandColorsScroll, 2);

            // Checkboxes docked to the bottom of the bands+modes columns (row 2, cols 0-1)
            // so they visually align with the last color row (SHF)
            var checkboxPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(6, 0, 0, 6)
            };
            checkboxPanel.Children.Add(popupToggleCheckBox);
            checkboxPanel.Children.Add(mapToggleCheckBox);

            var bandsModesBottom = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(checkboxPanel, Dock.Bottom);
            bandsModesBottom.Children.Add(checkboxPanel);

            // Inner grid to hold bands and modes side by side
            var bandsModesSplit = new Grid();
            bandsModesSplit.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bandsModesSplit.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(bandsScroll, 0);
            Grid.SetColumn(modesScroll, 1);
            bandsModesSplit.Children.Add(bandsScroll);
            bandsModesSplit.Children.Add(modesScroll);
            bandsModesBottom.Children.Add(bandsModesSplit);

            Grid.SetRow(bandsModesBottom, 1);
            Grid.SetRowSpan(bandsModesBottom, 2);
            Grid.SetColumn(bandsModesBottom, 0);
            Grid.SetColumnSpan(bandsModesBottom, 2);

            // Reset: plain Image icon + TextBlock side by side, no button frame, no template padding.
            // bandColorsPanel has Margin.Left=8, swatch has no extra left margin ? swatch left edge at 8px.
            // So resetPanel.Margin.Left = 8 aligns the icon's left edge exactly with the 160m square.
            var resetIconBmp = new BitmapImage();
            resetIconBmp.BeginInit();
            resetIconBmp.UriSource = new Uri("pack://application:,,,/Images/UNDO_Icon.png");
            resetIconBmp.DecodePixelHeight = 22;
            resetIconBmp.EndInit();
            var resetIconImg = new Image
            {
                Source = resetIconBmp,
                Width = 22,
                Height = 22,
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand,
                ToolTip = "Reset band colors to defaults"
            };
            RenderOptions.SetBitmapScalingMode(resetIconImg, BitmapScalingMode.HighQuality);
            resetIconImg.MouseLeftButtonUp += (s, e) => resetColorsBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            var resetTextBlock = new TextBlock
            {
                Text = "Reset to Default Colors",
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0),
                Cursor = Cursors.Hand
            };
            resetTextBlock.MouseLeftButtonUp += (s, e) => resetColorsBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            // Hide the original button ק it stays in the tree to keep Click logic working
            resetColorsBtn.Visibility = Visibility.Collapsed;

            var resetPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 4, 4, 4)   // matches bandColorsPanel.Margin.Left = 8
            };
            resetPanel.Children.Add(resetIconImg);
            resetPanel.Children.Add(resetTextBlock);
            resetPanel.Children.Add(resetColorsBtn);  // hidden, keeps click handler in tree

            Grid.SetRow(resetPanel, 1);
            Grid.SetColumn(resetPanel, 2);

            settingsLayout.Children.Add(header);
            settingsLayout.Children.Add(modesHeader);
            settingsLayout.Children.Add(bandColorsHeader);

            settingsLayout.Children.Add(bandsModesBottom);
            settingsLayout.Children.Add(bandColorsScroll);
            settingsLayout.Children.Add(resetPanel);

            const double clusterSettingsDefaultWidth = 560;
            const double clusterSettingsDefaultHeight = 520;
            double savedWidth = Properties.Settings.Default.ClusterSettingsWindowWidth;
            double savedHeight = Properties.Settings.Default.ClusterSettingsWindowHeight;
            double startupWidth = savedWidth > 100 ? savedWidth : clusterSettingsDefaultWidth;
            double startupHeight = savedHeight > 100 ? savedHeight : clusterSettingsDefaultHeight;

            clusterSettingsWindow = new Window
            {
                Title = "Cluster Settings",
                Width = startupWidth,
                Height = startupHeight,
                ResizeMode = ResizeMode.NoResize,
                Left = Properties.Settings.Default.ClusterSettingsWindowLeft,
                Top = Properties.Settings.Default.ClusterSettingsWindowTop,
                Content = settingsLayout
            };

            if (clusterWindow != null && clusterWindow.IsVisible)
            {
                clusterSettingsWindow.Owner = clusterWindow;
            }

            clusterSettingsWindow.LocationChanged += (s, e) =>
            {
                if (clusterSettingsWindow == null) return;
                Properties.Settings.Default.ClusterSettingsWindowLeft = clusterSettingsWindow.Left;
                Properties.Settings.Default.ClusterSettingsWindowTop = clusterSettingsWindow.Top;
            };

            clusterSettingsWindow.SizeChanged += (s, e) =>
            {
                if (clusterSettingsWindow == null) return;
                if (clusterSettingsWindow.Width > 0) Properties.Settings.Default.ClusterSettingsWindowWidth = clusterSettingsWindow.Width;
                if (clusterSettingsWindow.Height > 0) Properties.Settings.Default.ClusterSettingsWindowHeight = clusterSettingsWindow.Height;
            };

            clusterSettingsWindow.Closed += (s, e) => clusterSettingsWindow = null;
            clusterSettingsWindow.Show();
        }

        private bool LoadClusterHoverPopupSetting()
        {
            try
            {
                string path = GetClusterHoverPopupSettingPath();
                if (!File.Exists(path))
                {
                    return true;
                }

                string raw = File.ReadAllText(path).Trim();
                bool enabled;
                return bool.TryParse(raw, out enabled) ? enabled : true;
            }
            catch
            {
                return true;
            }
        }

        private void SaveClusterHoverPopupSetting(bool enabled)
        {
            try
            {
                string path = GetClusterHoverPopupSettingPath();
                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(path, enabled.ToString(CultureInfo.InvariantCulture));
            }
            catch
            {
            }
        }

        private string GetClusterHoverPopupSettingPath()
        {
            string baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HolyLogger");
            return Path.Combine(baseDir, "cluster-hover-popup-enabled.txt");
        }

        private int LoadClusterLastMinutesFilterSetting()
        {
            try
            {
                string path = GetClusterLastMinutesFilterSettingPath();
                if (!File.Exists(path))
                {
                    return 60;
                }

                int value;
                if (int.TryParse(File.ReadAllText(path).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
                    && (value == 5 || value == 15 || value == 30 || value == 60))
                {
                    return value;
                }
            }
            catch
            {
            }

            return 60;
        }

        private void SaveClusterLastMinutesFilterSetting(int minutes)
        {
            if (!(minutes == 5 || minutes == 15 || minutes == 30 || minutes == 60))
            {
                return;
            }

            try
            {
                string path = GetClusterLastMinutesFilterSettingPath();
                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(path, minutes.ToString(CultureInfo.InvariantCulture));
            }
            catch
            {
            }
        }

        private double LoadClusterCountryColumnWidthSetting()
        {
            try
            {
                string path = GetClusterCountryColumnWidthSettingPath();
                if (!File.Exists(path))
                {
                    return 100;
                }

                double value;
                if (double.TryParse(File.ReadAllText(path).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value >= 40)
                {
                    return value;
                }
            }
            catch
            {
            }

            return 100;
        }

        private void SaveClusterCountryColumnWidthSetting(double width)
        {
            if (double.IsNaN(width) || double.IsInfinity(width) || width < 40)
            {
                return;
            }

            try
            {
                string path = GetClusterCountryColumnWidthSettingPath();
                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(path, width.ToString(CultureInfo.InvariantCulture));
            }
            catch
            {
            }
        }

        private int LoadClusterCountryColumnDisplayIndexSetting()
        {
            try
            {
                string path = GetClusterCountryColumnDisplayIndexSettingPath();
                if (!File.Exists(path))
                {
                    return 2;
                }

                int value;
                if (int.TryParse(File.ReadAllText(path).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value >= 0)
                {
                    return value;
                }
            }
            catch
            {
            }

            return 2;
        }

        private void SaveClusterCountryColumnDisplayIndexSetting(int displayIndex)
        {
            if (displayIndex < 0)
            {
                return;
            }

            try
            {
                string path = GetClusterCountryColumnDisplayIndexSettingPath();
                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(path, displayIndex.ToString(CultureInfo.InvariantCulture));
            }
            catch
            {
            }
        }

        private string GetClusterCountryColumnWidthSettingPath()
        {
            string baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HolyLogger");
            return Path.Combine(baseDir, "cluster-country-col-width.txt");
        }

        private string GetClusterCountryColumnDisplayIndexSettingPath()
        {
            string baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HolyLogger");
            return Path.Combine(baseDir, "cluster-country-col-display-index.txt");
        }

        private string GetClusterLastMinutesFilterSettingPath()
        {
            string baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HolyLogger");
            return Path.Combine(baseDir, "cluster-last-minutes-filter.txt");
        }

        private void ClusterWindow_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (clusterWindow == null)
            {
                return;
            }

            if (clusterWindow.Width >= 0)
                Properties.Settings.Default.ClusterWindowWidth = clusterWindow.Width;
            if (clusterWindow.Height >= 0)
                Properties.Settings.Default.ClusterWindowHeight = clusterWindow.Height;
            Properties.Settings.Default.Save();
        }

        private static void AppendClusterLog(string message)
        {
            try
            {
                string dir = System.IO.Path.GetDirectoryName(ClusterLogPath);
                if (!System.IO.Directory.Exists(dir))
                    System.IO.Directory.CreateDirectory(dir);
                string line = string.Format("[{0:yyyy-MM-dd HH:mm:ss}] {1}{2}",
                    DateTime.Now, message, Environment.NewLine);
                System.IO.File.AppendAllText(ClusterLogPath, line, Encoding.UTF8);
            }
            catch { }
        }

        private async Task ConnectClusterWebSocketAsync(TextBlock statusText, ObservableCollection<ClusterSpotViewItem> spots)
        {
            CloseClusterWebSocket();
            clusterWebSocketCts = new CancellationTokenSource();
            CancellationToken token = clusterWebSocketCts.Token;
            int attempt = 0;

            AppendClusterLog("Cluster connection started.");

            while (!token.IsCancellationRequested)
            {
                attempt++;
                try
                {
                    DisposeClusterWebSocket();
                    clusterWebSocket = new ClientWebSocket();

                    AppendClusterLog(string.Format("Connecting to cluster (attempt {0})...", attempt));
                    await clusterWebSocket.ConnectAsync(new Uri(HolyClusterWebSocketUrl), token);
                    AppendClusterLog("Connected successfully.");
                    attempt = 0;

                    statusText.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        statusText.Text = "(connected)";
                        statusText.Foreground = new SolidColorBrush(Color.FromRgb(0, 190, 0));
                    }));

                    string initJson = clusterLastSpotTime > 0
                        ? "{\"last_time\":" + clusterLastSpotTime.ToString(CultureInfo.InvariantCulture) + "}"
                        : "{\"initial\":true}";

                    byte[] initBytes = Encoding.UTF8.GetBytes(initJson);
                    await clusterWebSocket.SendAsync(new ArraySegment<byte>(initBytes), WebSocketMessageType.Text, true, token);

                    await ReceiveClusterMessagesAsync(statusText, spots, token);

                    AppendClusterLog("WebSocket receive loop ended (connection closed by server).");
                }
                catch (OperationCanceledException)
                {
                    AppendClusterLog("Cluster connection cancelled (window closed).");
                    break;
                }
                catch (Exception ex)
                {
                    AppendClusterLog(string.Format("Disconnected with error: {0}", ex.Message));
                }

                if (token.IsCancellationRequested)
                    break;

                AppendClusterLog("Waiting 10 seconds before reconnecting...");
                statusText.Dispatcher.BeginInvoke(new Action(() =>
                {
                    statusText.Text = "(reconnecting...)";
                    statusText.Foreground = Brushes.Orange;
                }));

                try
                {
                    await Task.Delay(10000, token);
                }
                catch (OperationCanceledException)
                {
                    AppendClusterLog("Cluster connection cancelled during reconnect wait (window closed).");
                    break;
                }
            }

            statusText.Dispatcher.BeginInvoke(new Action(() =>
            {
                statusText.Text = "(disconnected)";
                statusText.Foreground = Brushes.Red;
            }));
        }

        private async Task ReceiveClusterMessagesAsync(TextBlock statusText, ObservableCollection<ClusterSpotViewItem> spots, CancellationToken cancellationToken)
        {
            byte[] buffer = new byte[8192];

            while (clusterWebSocket != null && clusterWebSocket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                WebSocketReceiveResult result;
                using (var ms = new MemoryStream())
                {
                    do
                    {
                        result = await clusterWebSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            await clusterWebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed", CancellationToken.None);
                            statusText.Dispatcher.BeginInvoke(new Action(() =>
                            {
                                statusText.Text = "(disconnected)";
                                statusText.Foreground = Brushes.Red;
                            }));
                            return;
                        }

                        ms.Write(buffer, 0, result.Count);
                    }
                    while (!result.EndOfMessage);

                    string payload = Encoding.UTF8.GetString(ms.ToArray());
                    ApplyClusterPayload(payload, spots);
                }
            }
        }

        private string ExtractClusterSpotLocator(JToken spotToken, string comment)
        {
            if (spotToken == null)
            {
                return ExtractValidMaidenheadLocator(comment);
            }

            string[] preferredFieldNames =
            {
                "locator", "dx_locator", "grid", "dx_grid", "dxlocator", "maidenhead", "dx_loc"
            };

            foreach (string fieldName in preferredFieldNames)
            {
                JToken valueToken = spotToken[fieldName];
                string locator = string.Empty;

                if (valueToken != null && valueToken.Type == JTokenType.Array)
                {
                    var arr = valueToken as JArray;
                    if (arr != null && arr.Count >= 2)
                    {
                        double lon;
                        double lat;
                        if (double.TryParse(arr[0].ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out lon)
                            && double.TryParse(arr[1].ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out lat))
                        {
                            try
                            {
                                locator = MaidenheadLocator.LatLngToLocator(lat, lon);
                            }
                            catch
                            {
                                locator = string.Empty;
                            }
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(locator))
                {
                    locator = ExtractValidMaidenheadLocator(valueToken != null ? valueToken.ToString() : string.Empty);
                }

                if (!string.IsNullOrWhiteSpace(locator))
                {
                    return locator;
                }
            }

            var spotObject = spotToken as JObject;
            if (spotObject != null)
            {
                foreach (var prop in spotObject.Properties())
                {
                    string name = prop.Name ?? string.Empty;
                    if (name.IndexOf("loc", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("grid", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        string locator = ExtractValidMaidenheadLocator(prop.Value != null ? prop.Value.ToString() : string.Empty);
                        if (!string.IsNullOrWhiteSpace(locator))
                        {
                            return locator;
                        }
                    }
                }
            }

            return ExtractValidMaidenheadLocator(comment);
        }

        private string ExtractValidMaidenheadLocator(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            var match = Regex.Match(text.ToUpperInvariant(), "\\b([A-R]{2}\\d{2}(?:[A-X]{2}(?:\\d{2})?)?)\\b");
            if (!match.Success)
            {
                return string.Empty;
            }

            string locator = match.Groups[1].Value;
            try
            {
                MaidenheadLocator.LocatorToLatLng(locator);
                return locator;
            }
            catch
            {
                return string.Empty;
            }
        }

        private void ApplyClusterPayload(string payload, ObservableCollection<ClusterSpotViewItem> spots)
        {
            try
            {
                JObject root = JObject.Parse(payload);
                JToken spotsToken;
                if (!root.TryGetValue("spots", out spotsToken) || spotsToken == null || spotsToken.Type != JTokenType.Array)
                {
                    return;
                }

                // Compute worked countries once for the whole payload instead of per-spot
                var workedCountries = GetWorkedCountriesFromLog();

                var newItems = new System.Collections.Generic.List<ClusterSpotViewItem>();

                foreach (JToken spotToken in spotsToken)
                {
                    string dx = (string)spotToken["dx_callsign"] ?? string.Empty;
                    string spotter = (string)spotToken["spotter_callsign"] ?? string.Empty;
                    long unixTime = spotToken["time"] != null ? (long)spotToken["time"] : 0;
                    string key = dx + "|" + spotter + "|" + unixTime.ToString(CultureInfo.InvariantCulture);

                    if (clusterSpotKeys.Contains(key))
                    {
                        continue;
                    }

                    clusterSpotKeys.Add(key);
                    if (unixTime > clusterLastSpotTime)
                    {
                        clusterLastSpotTime = unixTime;
                    }

                    double freq = spotToken["freq"] != null ? (double)spotToken["freq"] : 0;
                    string bandText = spotToken["band"] != null ? spotToken["band"].ToString() : string.Empty;
                    string mode = (string)spotToken["mode"] ?? string.Empty;
                    string comment = (string)spotToken["comment"] ?? string.Empty;
                    string dxLocator = ExtractClusterSpotLocator(spotToken, comment);

                    double? dxLat = null;
                    double? dxLon = null;
                    var dxLocToken = spotToken["dx_loc"];
                    if (dxLocToken != null && dxLocToken.Type == JTokenType.Array)
                    {
                        var arr = dxLocToken as Newtonsoft.Json.Linq.JArray;
                        if (arr != null && arr.Count >= 2)
                        {
                            double tmpLon, tmpLat;
                            if (double.TryParse(arr[0].ToString(), System.Globalization.NumberStyles.Float, CultureInfo.InvariantCulture, out tmpLon)
                                && double.TryParse(arr[1].ToString(), System.Globalization.NumberStyles.Float, CultureInfo.InvariantCulture, out tmpLat))
                            {
                                dxLon = tmpLon;
                                dxLat = tmpLat;
                            }
                        }
                    }

                    double? spotterLat = null;
                    double? spotterLon = null;
                    var spotterLocToken = spotToken["spotter_loc"];
                    if (spotterLocToken != null && spotterLocToken.Type == Newtonsoft.Json.Linq.JTokenType.Array)
                    {
                        var arr2 = spotterLocToken as Newtonsoft.Json.Linq.JArray;
                        if (arr2 != null && arr2.Count >= 2)
                        {
                            double tmpLon2, tmpLat2;
                            if (double.TryParse(arr2[0].ToString(), System.Globalization.NumberStyles.Float, CultureInfo.InvariantCulture, out tmpLon2)
                                && double.TryParse(arr2[1].ToString(), System.Globalization.NumberStyles.Float, CultureInfo.InvariantCulture, out tmpLat2))
                            {
                                spotterLon = tmpLon2;
                                spotterLat = tmpLat2;
                            }
                        }
                    }

                    var dxccInfo = rem.GetDXCC(dx.Trim());
                    var item = new ClusterSpotViewItem
                    {
                        UnixTime = unixTime,
                        TimeUtc = unixTime > 0
                            ? DateTimeOffset.FromUnixTimeSeconds(unixTime).UtcDateTime.ToString("HH:mm", CultureInfo.InvariantCulture)
                            : string.Empty,
                        FreqText = freq > 0 ? freq.ToString("0.0", CultureInfo.InvariantCulture) : string.Empty,
                        FreqDisplayText = freq > 0 ? ((freq >= 1000 ? (freq / 1000.0) : freq).ToString("0.000", CultureInfo.InvariantCulture)) : string.Empty,
                        BandText = bandText,
                        Mode = mode,
                        DXCallsign = dx,
                        SpotterCallsign = spotter,
                        Comment = comment,
                        Locator = dxLocator,
                        DxLat = dxLat,
                        DxLon = dxLon,
                        SpotterLat = spotterLat,
                        SpotterLon = spotterLon,
                        Country = dxccInfo != null ? dxccInfo.Name : string.Empty,
                        IsInLog = IsClusterCallsignInLog(dx),
                        IsMyCallsign = IsMyStationCallsign(dx),
                        IsNeededCountry = IsNeededCountry(dx, workedCountries),
                        SpotKey = key
                    };

                    newItems.Add(item);
                }

                if (newItems.Count == 0)
                    return;

                // Single dispatcher call for the whole batch
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    foreach (var item in newItems)
                    {
                        clusterAllSpots.Insert(0, item);
                    }
                    // Trim excess once after inserting the whole batch
                    while (clusterAllSpots.Count > 1500)
                    {
                        var evicted = clusterAllSpots[clusterAllSpots.Count - 1];
                        if (evicted.SpotKey != null)
                            clusterSpotKeys.Remove(evicted.SpotKey);
                        clusterAllSpots.RemoveAt(clusterAllSpots.Count - 1);
                    }

                    RefreshClusterVisibleSpots();
                }));
            }
            catch
            {
            }
        }

        private void DisposeClusterWebSocket()
        {
            try
            {
                if (clusterWebSocket != null)
                {
                    clusterWebSocket.Dispose();
                    clusterWebSocket = null;
                }
            }
            catch { }
        }

        private void CloseClusterWebSocket()
        {
            try
            {
                if (clusterWebSocketCts != null)
                {
                    clusterWebSocketCts.Cancel();
                    clusterWebSocketCts.Dispose();
                    clusterWebSocketCts = null;
                }
            }
            catch
            {
            }

            try
            {
                if (clusterWebSocket != null)
                {
                    clusterWebSocket.Dispose();
                    clusterWebSocket = null;
                }
            }
            catch
            {
            }
        }

        private sealed class ClusterSpotViewItem : INotifyPropertyChanged
        {
            public long UnixTime { get; set; }
            public string TimeUtc { get; set; }
            public string FreqText { get; set; }
            public string FreqDisplayText { get; set; }
            public string BandText { get; set; }
            public string Mode { get; set; }
            public string DXCallsign { get; set; }
            public string SpotterCallsign { get; set; }
            public string Comment { get; set; }
            public string Locator { get; set; }
            public double? DxLat { get; set; }
            public double? DxLon { get; set; }
            public double? SpotterLat { get; set; }
            public double? SpotterLon { get; set; }
            public string Country { get; set; }
            public bool IsInLog { get; set; }
            public string SpotKey { get; set; }

            public Brush ModeForeground
            {
                get
                {
                    string mode = (Mode ?? string.Empty).Trim().ToUpperInvariant();
                    if (mode == "CW")
                    {
                        return Brushes.Red;
                    }

                    if (mode == "SSB")
                    {
                        return Brushes.Blue;
                    }

                    return Brushes.Black;
                }
            }

            public FontWeight ModeFontWeight
            {
                get
                {
                    string mode = (Mode ?? string.Empty).Trim().ToUpperInvariant();
                    if (mode == "CW" || mode == "SSB")
                    {
                        return FontWeights.Bold;
                    }

                    return FontWeights.Normal;
                }
            }

            private bool _isNeededCountry;
            public bool IsNeededCountry
            {
                get => _isNeededCountry;
                set
                {
                    if (_isNeededCountry != value)
                    {
                        _isNeededCountry = value;
                        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsNeededCountry)));
                        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DXFontWeight)));
                        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DXForeground)));
                    }
                }
            }

            private bool _isOnFrequency;
            public bool IsOnFrequency
            {
                get => _isOnFrequency;
                set
                {
                    if (_isOnFrequency != value)
                    {
                        _isOnFrequency = value;
                        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsOnFrequency)));
                        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RowBackground)));
                    }
                }
            }

            private bool _isMyCallsign;
            public bool IsMyCallsign
            {
                get => _isMyCallsign;
                set
                {
                    if (_isMyCallsign != value)
                    {
                        _isMyCallsign = value;
                        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsMyCallsign)));
                        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DXForeground)));
                        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DXBackground)));
                    }
                }
            }

            public FontWeight DXFontWeight => FontWeights.Bold;
            public Brush DXForeground
            {
                get
                {
                    if (IsMyCallsign)
                        return Brushes.White;
                    if (IsNeededCountry)
                        return Brushes.Red;
                    if (IsInLog)
                        return new SolidColorBrush(Color.FromRgb(0x00, 0x7A, 0xCC)); // Bold blue (not too dark)
                    return Brushes.Black;
                }
            }

            public Brush DXBackground => IsMyCallsign
                ? new SolidColorBrush(Color.FromRgb(0x00, 0x33, 0x99))
                : Brushes.Transparent;

            public Brush RowBackground => IsOnFrequency 
                ? new SolidColorBrush(Color.FromRgb(0x90, 0xEE, 0x90)) // Darker green (LightGreen)
                : Brushes.Transparent;

            public event PropertyChangedEventHandler PropertyChanged;
        }

        private bool IsClusterCallsignInLog(string dxCallsign)
        {
            string target = (dxCallsign ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(target) || Qsos == null)
            {
                return false;
            }

            return Qsos.Any(q => string.Equals((q.DXCall ?? string.Empty).Trim(), target, StringComparison.OrdinalIgnoreCase));
        }

        private bool IsMyStationCallsign(string dxCallsign)
        {
            string target = (dxCallsign ?? string.Empty).Trim();
            string myCallsign = TB_MyCallsign != null ? (TB_MyCallsign.Text ?? string.Empty).Trim() : string.Empty;
            if (string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(myCallsign))
            {
                return false;
            }

            return string.Equals(target, myCallsign, StringComparison.OrdinalIgnoreCase);
        }

        private void RefreshClusterMyCallsignHighlight()
        {
            if (clusterAllSpots == null)
            {
                return;
            }

            foreach (var spot in clusterAllSpots)
            {
                spot.IsMyCallsign = IsMyStationCallsign(spot.DXCallsign);
            }
        }

        private HashSet<string> GetWorkedCountriesFromLog()
        {
            var workedCountries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Qsos == null)
            {
                return workedCountries;
            }

            foreach (var qso in Qsos)
            {
                if (!string.IsNullOrWhiteSpace(qso.DXCall))
                {
                    var dxcc = rem.GetDXCC(qso.DXCall.Trim());
                    if (dxcc != null && !string.IsNullOrWhiteSpace(dxcc.Entity) && dxcc.Entity != "-1")
                    {
                        workedCountries.Add(dxcc.Entity);
                    }
                }
            }

            return workedCountries;
        }

        private bool IsNeededCountry(string dxCallsign, HashSet<string> workedCountries)
        {
            if (string.IsNullOrWhiteSpace(dxCallsign) || workedCountries == null)
            {
                return false;
            }

            var dxcc = rem.GetDXCC(dxCallsign.Trim());
            if (dxcc == null || string.IsNullOrWhiteSpace(dxcc.Entity) || dxcc.Entity == "-1")
            {
                return false;
            }

            return !workedCountries.Contains(dxcc.Entity);
        }

        private void RefreshClusterNeededCountries()
        {
            if (clusterVisibleSpots == null || clusterWorkedCountries == null)
            {
                return;
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                foreach (var spot in clusterVisibleSpots)
                {
                    bool wasNeeded = spot.IsNeededCountry;
                    bool isNeeded = IsNeededCountry(spot.DXCallsign, clusterWorkedCountries);

                    if (wasNeeded != isNeeded)
                    {
                        spot.IsNeededCountry = isNeeded;
                    }
                }

                if (clusterAllSpots != null)
                {
                    foreach (var spot in clusterAllSpots)
                    {
                        spot.IsNeededCountry = IsNeededCountry(spot.DXCallsign, clusterWorkedCountries);
                    }
                }
            }));
        }

        private void AddWorkedCountryAndRefreshCluster(string dxCallsign)
        {
            if (string.IsNullOrWhiteSpace(dxCallsign) || clusterWorkedCountries == null)
            {
                return;
            }

            var dxcc = rem.GetDXCC(dxCallsign.Trim());

            if (dxcc == null || string.IsNullOrWhiteSpace(dxcc.Entity) || dxcc.Entity == "-1")
            {
                return;
            }

            bool wasNew = clusterWorkedCountries.Add(dxcc.Entity);

            if (wasNew)
            {
                RefreshClusterNeededCountries();
            }
        }

        private void RebuildWorkedCountriesAndRefreshCluster()
        {
            if (clusterWorkedCountries == null)
            {
                return;
            }

            clusterWorkedCountries = GetWorkedCountriesFromLog();
            RefreshClusterNeededCountries();
        }

        private void UpdateClusterFrequencyHighlight()
        {
            if (clusterVisibleSpots == null)
            {
                return;
            }

            double currentFreqMhz = 0;
            string freqText = TB_Frequency.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(freqText))
            {
                double.TryParse(freqText, NumberStyles.Float, CultureInfo.InvariantCulture, out currentFreqMhz);
            }

            if (currentFreqMhz <= 0)
            {
                foreach (var spot in clusterVisibleSpots)
                {
                    spot.IsOnFrequency = false;
                }
                return;
            }

            const double toleranceKhz = 0.5; // 0.5 kHz tolerance

            Dispatcher.BeginInvoke(new Action(() =>
            {
                foreach (var spot in clusterVisibleSpots)
                {
                    string spotFreqText = spot.FreqText?.Trim();
                    if (!string.IsNullOrWhiteSpace(spotFreqText) &&
                        double.TryParse(spotFreqText, NumberStyles.Float, CultureInfo.InvariantCulture, out double spotFreqValue))
                    {
                        // Normalize cluster frequency to MHz (cluster can be in kHz if >= 1000, otherwise MHz)
                        double spotFreqMhz = spotFreqValue >= 1000 ? (spotFreqValue / 1000.0) : spotFreqValue;

                        // Compare in kHz for better precision
                        double freqDiffKhz = Math.Abs(currentFreqMhz - spotFreqMhz) * 1000.0;
                        spot.IsOnFrequency = freqDiffKhz <= toleranceKhz;
                    }
                    else
                    {
                        spot.IsOnFrequency = false;
                    }
                }
            }));
        }

        private static readonly string[] ClusterBandOptions = new[] { "160", "80", "60", "40", "30", "20", "17", "15", "12", "10", "6", "VHF", "UHF", "SHF" };
        private static readonly string[] ClusterModeOptions = new[] { "CW", "DIGI", "SSB", "FM", "FT8", "RTTY", "AM" };

        private static readonly Dictionary<string, string> DefaultBandColors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "160", "#156184" }, { "80", "#903727" }, { "60", "#152F47" }, { "40", "#18A018" },
            { "30", "#FAFA00" }, { "20", "#DC2828" }, { "17", "#751F6B" }, { "15", "#1515CB" },
            { "12", "#47DFF0" }, { "10", "#E87421" }, { "6",  "#FF61EA" },
            { "VHF", "#5EFFA0" }, { "UHF", "#5ECFFF" }, { "SHF", "#A07CFF" }
        };

        private Dictionary<string, string> _bandColorCache = null;

        private Dictionary<string, string> GetBandColors()
        {
            if (_bandColorCache != null) return _bandColorCache;
            var colors = new Dictionary<string, string>(DefaultBandColors, StringComparer.OrdinalIgnoreCase);
            try
            {
                string raw = Properties.Settings.Default.ClusterBandColors ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    var saved = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, string>>(raw);
                    if (saved != null)
                        foreach (var kv in saved) colors[kv.Key] = kv.Value;
                }
            }
            catch { }
            _bandColorCache = colors;
            return colors;
        }

        private string GetBandColor(string band)
        {
            var colors = GetBandColors();
            if (!string.IsNullOrEmpty(band) && colors.TryGetValue(band, out string c)) return c;
            return "#FF6600";
        }

        private void SaveBandColors(Dictionary<string, string> colors)
        {
            try
            {
                Properties.Settings.Default.ClusterBandColors = Newtonsoft.Json.JsonConvert.SerializeObject(colors);
                Properties.Settings.Default.Save();
            }
            catch { }
            _bandColorCache = null;
        }

        private HashSet<string> GetEnabledClusterBands()
        {
            string raw = Properties.Settings.Default.ClusterEnabledBands ?? string.Empty;
            var values = raw.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                            .Select(v => v.Trim().ToUpperInvariant())
                            .Where(v => !string.IsNullOrWhiteSpace(v));

            var set = new HashSet<string>(values, StringComparer.OrdinalIgnoreCase);
            if (set.Count == 0)
            {
                foreach (string band in ClusterBandOptions)
                {
                    set.Add(band);
                }
            }
            return set;
        }

        private void SaveEnabledClusterBands(HashSet<string> enabled)
        {
            if (enabled == null || enabled.Count == 0)
            {
                enabled = new HashSet<string>(ClusterBandOptions, StringComparer.OrdinalIgnoreCase);
            }

            string csv = string.Join(",", ClusterBandOptions.Where(b => enabled.Contains(b)));
            Properties.Settings.Default.ClusterEnabledBands = csv;
            Properties.Settings.Default.Save();
        }

        private string NormalizeClusterBandKey(string bandText)
        {
            string b = (bandText ?? string.Empty).Trim().ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(b))
                return string.Empty;

            if (Regex.IsMatch(b, "^\\d+M$"))
                return b.Substring(0, b.Length - 1);

            if (Regex.IsMatch(b, "^\\d+$"))
                return b;

            if (b == "VHF" || b == "UHF" || b == "SHF")
                return b;

            if (b == "2M" || b == "4M" || b == "6M")
                return b.Substring(0, b.Length - 1);

            if (b == "70CM")
                return "UHF";

            if (b.EndsWith("CM", StringComparison.Ordinal))
                return "SHF";

            return b;
        }

        private string FormatClusterBandDisplay(string bandText)
        {
            string normalized = NormalizeClusterBandKey(bandText);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return string.Empty;
            }

            return Regex.IsMatch(normalized, "^\\d+$") ? normalized + "m" : normalized;
        }

        private bool IsClusterBandEnabled(string bandText)
        {
            string normalized = NormalizeClusterBandKey(bandText);
            if (string.IsNullOrWhiteSpace(normalized))
                return true;

            string mode = Properties.Settings.Default.ClusterBandFilterMode ?? "PreSelected";

            if (string.Equals(mode, "All", StringComparison.OrdinalIgnoreCase))
                return true;

            if (string.Equals(mode, "Active", StringComparison.OrdinalIgnoreCase))
            {
                string activeBand = TB_Band != null ? TB_Band.Text : string.Empty;
                string active = NormalizeClusterBandKey(activeBand);
                return !string.IsNullOrWhiteSpace(active) && string.Equals(active, normalized, StringComparison.OrdinalIgnoreCase);
            }

            // PreSelected
            var enabled = GetEnabledClusterBands();
            return enabled.Contains(normalized);
        }

        private HashSet<string> GetEnabledClusterModes()
        {
            string raw = Properties.Settings.Default.ClusterEnabledModes ?? string.Empty;
            var values = raw.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                            .Select(v => v.Trim().ToUpperInvariant())
                            .Where(v => !string.IsNullOrWhiteSpace(v));

            var set = new HashSet<string>(values, StringComparer.OrdinalIgnoreCase);
            if (set.Count == 0)
            {
                foreach (string mode in ClusterModeOptions)
                {
                    set.Add(mode);
                }
            }
            return set;
        }

        private void SaveEnabledClusterModes(HashSet<string> enabled)
        {
            if (enabled == null || enabled.Count == 0)
            {
                enabled = new HashSet<string>(ClusterModeOptions, StringComparer.OrdinalIgnoreCase);
            }

            string csv = string.Join(",", ClusterModeOptions.Where(m => enabled.Contains(m)));
            Properties.Settings.Default.ClusterEnabledModes = csv;
            Properties.Settings.Default.Save();
        }

        private bool IsClusterModeEnabled(string modeText)
        {
            string normalized = (modeText ?? string.Empty).Trim().ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return true;
            }

            var enabled = GetEnabledClusterModes();
            return enabled.Contains(normalized);
        }

        private void RefreshClusterVisibleSpots()
        {
            if (clusterVisibleSpots == null)
            {
                return;
            }

            var filtered = clusterAllSpots.Where(s => IsClusterBandEnabled(s.BandText) && IsClusterModeEnabled(s.Mode))
                                          .Where(s => s.UnixTime > 0 && s.UnixTime >= DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (clusterLastMinutesFilterValue * 60L))
                                          .OrderByDescending(s => s.UnixTime)
                                          .Take(500)
                                          .ToList();

            foreach (var item in filtered)
            {
                item.IsInLog = IsClusterCallsignInLog(item.DXCallsign);
            }

            clusterVisibleSpots.Clear();
            foreach (var item in filtered)
            {
                clusterVisibleSpots.Add(item);
            }

            UpdateClusterFrequencyHighlight();
            UpdateClusterSpotCountIndicator();
            RequestClusterHeaderAlignmentRefresh();
            UpdateClusterSpotsOnMap();
        }

        private void UpdateClusterSpotsOnMap()
        {
            if (MapControl == null || MapControl.Visibility != Visibility.Visible)
                return;
            if (!Properties.Settings.Default.ClusterMapEnabled)
                return;
            if (_dxQsoInProgress)
                return;
            if (_mapUpdateDebounceTimer == null)
            {
                DoUpdateClusterSpotsOnMap();
                return;
            }
            _mapUpdateDebounceTimer.Stop();
            _mapUpdateDebounceTimer.Start();
        }

        private void DoUpdateClusterSpotsOnMap()
        {
            if (MapControl == null || MapControl.Visibility != Visibility.Visible)
                return;
            if (!Properties.Settings.Default.ClusterMapEnabled)
                return;

            if (string.IsNullOrWhiteSpace(TB_MyLocator.Text))
                return;

            if (clusterVisibleSpots == null)
                return;

            try
            {
                var homell = MaidenheadLocator.LocatorToLatLng(TB_MyLocator.Text);
                var spots = new System.Collections.Generic.List<HolyLogger.ToolsUserControls.ClusterSpotInfo>();
                foreach (var spot in clusterVisibleSpots)
                {
                    if (spot.DxLat.HasValue && spot.DxLon.HasValue)
                    {
                        double freqMhz = 0;
                        if (double.TryParse(spot.FreqText ?? string.Empty, System.Globalization.NumberStyles.Float, CultureInfo.InvariantCulture, out double fv) && fv > 0)
                            freqMhz = fv >= 1000 ? fv / 1000.0 : fv;

                        spots.Add(new HolyLogger.ToolsUserControls.ClusterSpotInfo
                        {
                            Lat = spot.DxLat.Value,
                            Lon = spot.DxLon.Value,
                            SpotterLat = spot.SpotterLat,
                            SpotterLon = spot.SpotterLon,
                            Callsign = spot.DXCallsign ?? string.Empty,
                            Freq = freqMhz > 0 ? freqMhz.ToString("0.###", CultureInfo.InvariantCulture) : (spot.FreqText ?? string.Empty),
                            Mode = spot.Mode ?? string.Empty,
                            Color = GetBandColor(spot.BandText ?? string.Empty),
                            Band = spot.BandText ?? string.Empty
                        });
                    }
                }

                MapControl.ShowClusterSpots(spots, homell.Lat, homell.Long, GetMapRadiusKm());
            }
            catch
            {
            }
        }

        private void UpdateClusterSpotCountIndicator()
        {
            if (clusterSpotCountText == null)
            {
                return;
            }

            int count = clusterVisibleSpots != null ? clusterVisibleSpots.Count : 0;
            clusterSpotCountText.Text = count.ToString(CultureInfo.InvariantCulture);
        }

        private void ClusterSpotsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var grid = sender as DataGrid;
            var source = e.OriginalSource as DependencyObject;
            DataGridCell cell = FindVisualParent<DataGridCell>(source);
            ClusterSpotViewItem selectedSpot = null;

            if (cell != null)
            {
                selectedSpot = cell.DataContext as ClusterSpotViewItem;
            }

            if (selectedSpot == null)
            {
                while (source != null && !(source is DataGridRow))
                {
                    source = VisualTreeHelper.GetParent(source);
                }

                var row = source as DataGridRow;
                selectedSpot = row?.Item as ClusterSpotViewItem ?? grid?.SelectedItem as ClusterSpotViewItem;
            }

            if (selectedSpot == null)
            {
                return;
            }

            TuneToClusterSpot(selectedSpot);
        }

        private void ShowClusterSpotOnMap(ClusterSpotViewItem spot)
        {
            if (spot == null)
                return;

            if (MapControl == null || MapControl.Visibility != Visibility.Visible)
                return;

            if (string.IsNullOrWhiteSpace(TB_MyLocator.Text))
                return;

            try
            {
                // Use the lat/lon stored directly from the server's dx_loc field.
                // Fall back to DXCC locator only when no coordinates were received.
                double dxLat, dxLon;
                if (spot.DxLat.HasValue && spot.DxLon.HasValue)
                {
                    dxLat = spot.DxLat.Value;
                    dxLon = spot.DxLon.Value;
                }
                else
                {
                    string locator = spot.Locator;
                    if (string.IsNullOrWhiteSpace(locator))
                    {
                        var dxcc = rem.GetDXCC((spot.DXCallsign ?? string.Empty).Trim());
                        locator = dxcc != null ? dxcc.Locator : string.Empty;
                    }

                    if (string.IsNullOrWhiteSpace(locator))
                        return;

                    var ll = MaidenheadLocator.LocatorToLatLng(locator);
                    dxLat = ll.Lat;
                    dxLon = ll.Long;
                }

                var homell = MaidenheadLocator.LocatorToLatLng(TB_MyLocator.Text);
                var dxLatLng = new HolyParser.LatLng { Lat = dxLat, Long = dxLon };
                Azimuth = MaidenheadLocator.Azimuth(homell, dxLatLng);
                MapControl.ShowMap(dxLat, dxLon, GetMapRadiusKm(), Azimuth, homell.Lat, homell.Long);
            }
            catch
            {
            }
        }

        private async void TuneToClusterSpot(ClusterSpotViewItem spot)
        {
            if (spot == null)
            {
                return;
            }

            string freqText = (spot.FreqText ?? string.Empty).Trim();
            if (!double.TryParse(freqText, NumberStyles.Float, CultureInfo.InvariantCulture, out double freqValue) || freqValue <= 0)
            {
                return;
            }

            double freqMhz = freqValue >= 1000 ? (freqValue / 1000.0) : freqValue;
            CaptureClusterUndoState();

            TB_Frequency.Text = freqMhz.ToString("0.0###", CultureInfo.InvariantCulture);
            TB_DXCallsign.Text = (spot.DXCallsign ?? string.Empty).Trim().ToUpperInvariant();

            string normalizedMode = NormalizeClusterModeForLogger(spot.Mode);
            SelectLoggerMode(normalizedMode);

            if (!Properties.Settings.Default.EnableOmniRigCAT || Rig == null || Rig.Status != OmniRig.RigStatusX.ST_ONLINE)
            {
                return;
            }

            int freqHz = (int)Math.Round(freqMhz * 1000000.0, MidpointRounding.AwayFromZero);
            int? rigMode = MapClusterModeToRigMode(normalizedMode, freqMhz);
            var modeToSend = (OmniRig.RigParamX)(rigMode ?? PM_DIG_U);
            await TryTuneRigFrequencyAsync(freqHz, modeToSend);
        }

        private string NormalizeClusterModeForLogger(string clusterMode)
        {
            string mode = (clusterMode ?? string.Empty).Trim().ToUpperInvariant();
            if (mode == "CW")
            {
                return "CW";
            }

            if (mode == "SSB" || mode == "FM" || mode == "AM")
            {
                return mode;
            }

            if (mode == "DIGI" || mode == "FT8" || mode == "RTTY" || mode == "PSK")
            {
                return "DIGI";
            }

            return "DIGI";
        }

        private void SelectLoggerMode(string mode)
        {
            string normalized = (mode ?? string.Empty).Trim().ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return;
            }

            ComboBoxItem selectedItem = CB_Mode.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => string.Equals((i.Content as string) ?? string.Empty, normalized, StringComparison.OrdinalIgnoreCase));

            if (selectedItem != null)
            {
                CB_Mode.SelectedItem = selectedItem;
            }
            else
            {
                CB_Mode.Text = normalized;
            }
        }

        private int? MapClusterModeToRigMode(string loggerMode, double freqMhz)
        {
            string mode = (loggerMode ?? string.Empty).Trim().ToUpperInvariant();
            switch (mode)
            {
                case "CW":
                    return PM_CW_U;
                case "SSB":
                    return freqMhz < 10.0 ? PM_SSB_L : PM_SSB_U;
                case "FM":
                    return PM_FM;
                case "AM":
                    return PM_AM;
                case "DIGI":
                    return PM_DIG_U;
                default:
                    return null;
            }
        }

        private void CaptureClusterUndoState()
        {
            string frequencyText = (TB_Frequency.Text ?? string.Empty).Trim();
            string modeText = (CB_Mode.Text ?? string.Empty).Trim().ToUpperInvariant();
            string dxCallsignText = (TB_DXCallsign.Text ?? string.Empty).Trim().ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(frequencyText) || string.IsNullOrWhiteSpace(modeText))
            {
                return;
            }

            if (clusterUndoStates.Count > 0)
            {
                var last = clusterUndoStates.Peek();
                if (string.Equals(last.FrequencyText, frequencyText, StringComparison.Ordinal)
                    && string.Equals(last.ModeText, modeText, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(last.DxCallsignText, dxCallsignText, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            clusterUndoStates.Push((frequencyText, modeText, dxCallsignText));
            UpdateClusterUndoButtonState();
        }

        private async void ClusterUndoButton_Click(object sender, RoutedEventArgs e)
        {
            if (clusterUndoStates.Count == 0)
            {
                return;
            }

            var undoState = clusterUndoStates.Pop();
            UpdateClusterUndoButtonState();

            string freqText = undoState.FrequencyText;
            string modeText = undoState.ModeText;
            string dxCallsignText = undoState.DxCallsignText;

            if (!double.TryParse(freqText, NumberStyles.Float, CultureInfo.InvariantCulture, out double freqMhz) || freqMhz <= 0)
            {
                return;
            }

            TB_Frequency.Text = freqMhz.ToString("0.0###", CultureInfo.InvariantCulture);
            SelectLoggerMode(modeText);
            TB_DXCallsign.Text = dxCallsignText;

            if (Properties.Settings.Default.EnableOmniRigCAT && Rig != null && Rig.Status == OmniRig.RigStatusX.ST_ONLINE)
            {
                int freqHz = (int)Math.Round(freqMhz * 1000000.0, MidpointRounding.AwayFromZero);
                int? rigMode = MapClusterModeToRigMode(modeText, freqMhz);
                var modeToSend = (OmniRig.RigParamX)(rigMode ?? PM_DIG_U);
                await TryTuneRigFrequencyAsync(freqHz, modeToSend);
            }
        }

        private void UpdateClusterUndoButtonState()
        {
            if (clusterUndoButton == null)
            {
                return;
            }

            bool hasUndo = clusterUndoStates.Count > 0;
            clusterUndoButton.IsEnabled = hasUndo;
            clusterUndoButton.Opacity = hasUndo ? 1.0 : 0.35;

            if (clusterUndoCountText != null)
            {
                clusterUndoCountText.Text = hasUndo ? clusterUndoStates.Count.ToString(CultureInfo.InvariantCulture) : string.Empty;
            }
        }

        private async Task TryTuneRigFrequencyAsync(int frequencyHz, OmniRig.RigParamX mode)
        {
            if (Rig == null || Rig.Status != OmniRig.RigStatusX.ST_ONLINE)
            {
                return;
            }

            try
            {
                int writable = (int)Rig.WriteableParams;
                bool freqWritable = (writable & PM_FREQ) != 0;
                bool freqAWritable = (writable & PM_FREQA) != 0;

                try
                {
                    Rig.Mode = mode;
                }
                catch
                {
                }

                if (freqWritable)
                {
                    Rig.Freq = frequencyHz;
                    await TryGetRigReadbackAsync(frequencyHz);
                    return;
                }

                if (freqAWritable)
                {
                    Rig.FreqA = frequencyHz;
                    await TryGetRigReadbackAsync(frequencyHz);
                }
            }
            catch
            {
            }
        }

        private async Task<bool> TryGetRigReadbackAsync(int targetHz)
        {
            int rxReadbackHz = 0;

            for (int i = 0; i < 8; i++)
            {
                try
                {
                    rxReadbackHz = (int)Rig.GetRxFrequency();
                    if (Math.Abs(rxReadbackHz - targetHz) <= 5000)
                    {
                        return true;
                    }
                }
                catch
                {
                    return false;
                }

                await Task.Delay(120);
            }

            return false;
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
            about = new AboutWindow(callsignListVersion);
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
                        // Ensure the dialog is owned by the main window so it doesn't get closed when the splash window is closed
                        if (MessageBox.Show(this, messageBoxText, caption, button, icon) == MessageBoxResult.Yes)
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
                Application.Current.Dispatcher.Invoke(() => Application.Current.Shutdown());
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
            if (e.Key == Key.Down && CallsignSuggestionsPopup.IsOpen && LB_DXCallsignSuggestions.Items.Count > 0 && !callsignSuggestionMouseControl)
            {
                LB_DXCallsignSuggestions.SelectedIndex = Math.Min(LB_DXCallsignSuggestions.SelectedIndex + 1, LB_DXCallsignSuggestions.Items.Count - 1);
                LB_DXCallsignSuggestions.ScrollIntoView(LB_DXCallsignSuggestions.SelectedItem);
                ApplyHighlightedCallsignSuggestionToTextBox();
                e.Handled = true;
            }
            else if (e.Key == Key.Up && CallsignSuggestionsPopup.IsOpen && LB_DXCallsignSuggestions.Items.Count > 0 && !callsignSuggestionMouseControl)
            {
                LB_DXCallsignSuggestions.SelectedIndex = Math.Max(LB_DXCallsignSuggestions.SelectedIndex - 1, 0);
                LB_DXCallsignSuggestions.ScrollIntoView(LB_DXCallsignSuggestions.SelectedItem);
                ApplyHighlightedCallsignSuggestionToTextBox();
                e.Handled = true;
            }
            else if ((e.Key == Key.Down || e.Key == Key.Up) && CallsignSuggestionsPopup.IsOpen && callsignSuggestionMouseControl)
            {
                // While mouse owns the list selection, block arrow-key navigation updates.
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                if (CallsignSuggestionsPopup.IsOpen)
                {
                    ApplySelectedCallsignSuggestion();
                    e.Handled = true;
                }
                else if (Properties.Settings.Default.AddQSOWithEnter || !Properties.Settings.Default.DoNothing)
                {
                    AddBtn_Click(null, null);
                    e.Handled = true;
                }
            }
            else if (e.Key == Key.Escape)
            {
                if (CallsignSuggestionsPopup.IsOpen)
                {
                    CallsignSuggestionsPopup.IsOpen = false;
                    e.Handled = true;
                }
            }
            else if (e.Key == Key.Space)
            {
                e.Handled = true;
            }
        }

        private void ApplyHighlightedCallsignSuggestionToTextBox()
        {
            string highlighted = (LB_DXCallsignSuggestions.SelectedItem as CallsignSuggestionItem)?.FullCallsign;
            if (string.IsNullOrWhiteSpace(highlighted)) return;

            isApplyingSuggestion = true;
            TB_DXCallsign.Text = highlighted;
            TB_DXCallsign.CaretIndex = TB_DXCallsign.Text.Length;
            isApplyingSuggestion = false;
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
            RefreshClusterMyCallsignHighlight();
        }
        
        private void TB_MyHolyland_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (signboard != null)
            {
                signboard.signboardData.Square = TB_MyHolyland.Text;
            }
            ShowHomeMap();
        }

        private void TB_MyLocator_PreviewLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (TB_MyLocator == null)
                return;

            string locator = (TB_MyLocator.Text ?? string.Empty).Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(locator))
                return;

            if (!Regex.IsMatch(locator, "^[A-Z]{2}[0-9]{2}[A-Z]{2}$"))
            {
                e.Handled = true;
                MessageBox.Show("Wrong locator format", "HolyLogger", MessageBoxButton.OK, MessageBoxImage.Warning);
                TB_MyLocator.Focus();
                TB_MyLocator.SelectAll();
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
            if (clusterActiveBandIndicatorText != null)
            {
                clusterActiveBandIndicatorText.Text = FormatClusterBandDisplay(TB_Band != null ? TB_Band.Text : string.Empty);
                UpdateClusterActiveBandIndicatorPosition();
            }

            if (string.Equals(Properties.Settings.Default.ClusterBandFilterMode, "Active", StringComparison.OrdinalIgnoreCase))
            {
                RefreshClusterVisibleSpots();
            }
        }

        private void TB_DX_Name_TextChanged(object sender, TextChangedEventArgs e)
        {
            const double rightEdge = 371;
            const double minLeft = 57;
            const double defaultLeft = 101;

            var ft = new System.Windows.Media.FormattedText(
                TB_DX_Name.Text,
                System.Globalization.CultureInfo.CurrentCulture,
                System.Windows.FlowDirection.LeftToRight,
                new System.Windows.Media.Typeface(TB_DX_Name.FontFamily, TB_DX_Name.FontStyle, TB_DX_Name.FontWeight, TB_DX_Name.FontStretch),
                TB_DX_Name.FontSize,
                System.Windows.Media.Brushes.Black);

            double neededWidth = ft.Width + 16; // padding
            double newLeft = rightEdge - neededWidth;
            if (newLeft > defaultLeft) newLeft = defaultLeft;
            if (newLeft < minLeft) newLeft = minLeft;

            TB_DX_Name.Margin = new Thickness(newLeft, TB_DX_Name.Margin.Top, 0, 0);
            TB_DX_Name.Width = rightEdge - newLeft;
        }

        private void SetQrzPhoto(string imageUrl)
        {
            if (!Properties.Settings.Default.ShowPhotoFromQRZ)
            {
                ClearQrzPhoto();
                return;
            }

            if (string.IsNullOrWhiteSpace(imageUrl))
            {
                ClearQrzPhoto();
                return;
            }

            try
            {
                string normalized = imageUrl.Trim();
                if (normalized.StartsWith("//"))
                {
                    normalized = "https:" + normalized;
                }
                ShowQrzPhotoWindow(normalized);
            }
            catch
            {
                ClearQrzPhoto();
            }
        }

        private void ClearQrzPhoto()
        {
            if (qrzPhotoWindow != null)
            {
                SaveQrzPhotoWindowBounds(qrzPhotoWindow);
                qrzPhotoWindow.Close();
                qrzPhotoWindow = null;
            }
        }

        private void SaveQrzPhotoWindowBounds(Window window)
        {
            if (window == null)
            {
                return;
            }

            var bounds = window.WindowState == WindowState.Normal
                ? new Rect(window.Left, window.Top, window.Width, window.Height)
                : window.RestoreBounds;

            if (!double.IsNaN(bounds.Left) && !double.IsInfinity(bounds.Left) &&
                !double.IsNaN(bounds.Top) && !double.IsInfinity(bounds.Top))
            {
                qrzPhotoLeft = bounds.Left;
                qrzPhotoTop = bounds.Top;
            }

            if (!double.IsNaN(bounds.Width) && !double.IsInfinity(bounds.Width) &&
                !double.IsNaN(bounds.Height) && !double.IsInfinity(bounds.Height))
            {
                qrzPhotoWidth = bounds.Width;
                qrzPhotoHeight = bounds.Height;
            }

            PersistQrzPhotoWindowBoundsToDisk();
        }

        private string GetQrzPhotoBoundsPath()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HolyLogger");
            return Path.Combine(dir, "qrz_photo_window_bounds.txt");
        }

        private void LoadQrzPhotoWindowBoundsFromDisk()
        {
            try
            {
                string filePath = GetQrzPhotoBoundsPath();
                if (!File.Exists(filePath))
                {
                    return;
                }

                string[] parts = File.ReadAllText(filePath).Split('|');
                if (parts.Length != 4)
                {
                    return;
                }

                if (double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double left) &&
                    double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double top) &&
                    double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double width) &&
                    double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double height))
                {
                    qrzPhotoLeft = left;
                    qrzPhotoTop = top;
                    qrzPhotoWidth = width;
                    qrzPhotoHeight = height;
                }
            }
            catch
            {
            }
        }

        private void PersistQrzPhotoWindowBoundsToDisk()
        {
            try
            {
                if (!qrzPhotoLeft.HasValue || !qrzPhotoTop.HasValue || !qrzPhotoWidth.HasValue || !qrzPhotoHeight.HasValue)
                {
                    return;
                }

                string filePath = GetQrzPhotoBoundsPath();
                Directory.CreateDirectory(Path.GetDirectoryName(filePath));

                string line = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}|{1}|{2}|{3}",
                    qrzPhotoLeft.Value,
                    qrzPhotoTop.Value,
                    qrzPhotoWidth.Value,
                    qrzPhotoHeight.Value);

                File.WriteAllText(filePath, line);
            }
            catch
            {
            }
        }

        private void ShowQrzPhotoWindow(string imageUrl)
        {
            if (string.IsNullOrWhiteSpace(imageUrl))
            {
                ClearQrzPhoto();
                return;
            }

            if (qrzPhotoWindow == null)
            {
                qrzPhotoWindow = new QRZPhotoWindow();
                qrzPhotoWindow.Owner = this;
                qrzPhotoWindow.Closed += (sender, args) =>
                {
                    SaveQrzPhotoWindowBounds(qrzPhotoWindow);
                    qrzPhotoWindow = null;
                };

                if (qrzPhotoWidth.HasValue && qrzPhotoHeight.HasValue)
                {
                    qrzPhotoWindow.Width = qrzPhotoWidth.Value;
                    qrzPhotoWindow.Height = qrzPhotoHeight.Value;
                }

                if (qrzPhotoLeft.HasValue && qrzPhotoTop.HasValue)
                {
                    qrzPhotoWindow.Left = qrzPhotoLeft.Value;
                    qrzPhotoWindow.Top = qrzPhotoTop.Value;
                }
                else
                {
                    qrzPhotoWindow.Left = Left + Width - qrzPhotoWindow.Width;
                    qrzPhotoWindow.Top = Top + Height - qrzPhotoWindow.Height;
                }

                qrzPhotoWindow.Show();
            }

            qrzPhotoWindow.SetPhoto(imageUrl);
        }

        private void TB_State_TextChanged(object sender, TextChangedEventArgs e)
        {
            TB_State.TextAlignment = TB_State.Text.Length <= 2
                ? TextAlignment.Center
                : TextAlignment.Left;
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
            if (!isApplyingSuggestion)
            {
                UpdateCallsignSuggestions();
            }

            string dxCallText = (TB_DXCallsign.Text ?? string.Empty).Trim();

            // Clear stale values from the previously highlighted callsign until new data is loaded.
            FName = string.Empty;
            ClearDXLocator();
            if (string.IsNullOrWhiteSpace(dxCallText))
            {
                CallsignLookupDebounceTimer.Stop();
                ClearQrzPhoto();
                TB_DXCC.Text = "";
                TB_DX_Name.Text = "";
                TB_State.Text = "";
                UpdateCountryFlag(null);
                ClearAzimuth();
                ClearMatrix();
                L_Duplicate.Visibility = Visibility.Hidden;
                L_Legal.Visibility = Visibility.Hidden;
                RestoreDataContext();
            }
            else
            {
                // Keep typing snappy: skip heavy DXCC/matrix/filter work until at least 2 chars.
                if (dxCallText.Length < 2)
                {
                    CallsignLookupDebounceTimer.Stop();
                    ClearQrzPhoto();
                    Prefix = dxCallText.ToUpperInvariant();
                    return;
                }

                // Prevent stale photo while callsign is not long enough for a QRZ lookup.
                if (dxCallText.Length < 3)
                {
                    CallsignLookupDebounceTimer.Stop();
                    ClearQrzPhoto();
                }

                if (!Properties.Settings.Default.isManualMode && state == State.New)
                    RefreshDateTime_Btn_MouseUp(null, null);
                DXCC dXCC = rem.GetDXCC(dxCallText);
                Country = dXCC.Name;
                UpdateCountryFlag(dXCC.Name);
                Continent = dXCC.Continent;
                QRZGrid = dXCC.Locator;
                Prefix = dxCallText.Length >= 2 ? dxCallText.Substring(0, 2) : "";
                RestartCallsignLookupDebounce();
                UpdateMatrix();
                if (Properties.Settings.Default.IsFilterQSOs)
                {
                    FilteredQsos = new ObservableCollection<QSO>(Qsos.Where(p => p.DXCall.Contains(dxCallText)));
                    if (LastQSO != null && Properties.Settings.Default.DisplayLastQSOinGrid) FilteredQsos.Insert(0, LastQSO);
                    DataContext = FilteredQsos;
                }
            }
        }

        private void RestartCallsignLookupDebounce()
        {
            CallsignLookupDebounceTimer.Stop();
            CallsignLookupDebounceTimer.Start();
        }

        private void CallsignLookupDebounceTimer_Tick(object sender, EventArgs e)
        {
            CallsignLookupDebounceTimer.Stop();

            if (string.IsNullOrWhiteSpace(TB_DXCallsign.Text))
            {
                ClearQrzPhoto();
                return;
            }

            SetAzimuth();
            if (state == State.New)
            {
                GetQrzData();
            }
        }
        
        private void TB_DXCallsign_LostFocus(object sender, RoutedEventArgs e)
        {
            callsignSuggestionMouseControl = false;
            CallsignSuggestionsPopup.IsOpen = false;
            TB_Exchange.Focusable = true;
        }

        private void AddNewCallsignIfMissing(string bareCallsign)
        {
            if (string.IsNullOrWhiteSpace(bareCallsign)) return;
            string call = bareCallsign.Trim().ToUpperInvariant();

            // If the callsign is already known from the big index, it is not "new".
            int idx = callsignIndex.BinarySearch(call, StringComparer.Ordinal);
            if (idx >= 0)
                return;

            // Add truly new callsigns to the in-memory dropdown index.
            callsignIndex.Insert(~idx, call);

            // Append to callsigns_new.txt only if not already recorded
            if (newCallsignsSet.Add(call))
            {
                try
                {
                    string filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "callsigns_new.txt");
                    File.AppendAllText(filePath, call + Environment.NewLine);
                }
                catch { }
                _callsignUploader?.TrySendFireAndForget();
            }
        }

        private void LoadNewCallsignsSet()
        {
            try
            {
                string filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "callsigns_new.txt");
                if (!File.Exists(filePath)) return;

                var deduped = new List<string>();
                foreach (var rawLine in File.ReadLines(filePath))
                {
                    string call = rawLine.Trim().ToUpperInvariant();
                    if (string.IsNullOrWhiteSpace(call)) continue;
                    if (newCallsignsSet.Add(call))
                        deduped.Add(call);
                }

                // Rewrite file without duplicates
                File.WriteAllLines(filePath, deduped);
            }
            catch { }
        }

        private void LoadCallsignIndex()
        {
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string[] bigTextCandidatePaths = new[]
                {
                    Path.Combine(baseDir, @"Data\callsigns_merged_big.txt"),
                    Path.Combine(baseDir, "callsigns_merged_big.txt"),
                    Path.GetFullPath(Path.Combine(baseDir, @"..\..\..\Data\callsigns_merged_big.txt")),
                    Path.GetFullPath(Path.Combine(baseDir, @"..\..\..\callsigns_merged_big.txt")),
                    Path.GetFullPath(Path.Combine(baseDir, @"..\..\..\..\Data\callsigns_merged_big.txt")),
                    Path.GetFullPath(Path.Combine(baseDir, @"..\..\..\..\callsigns_merged_big.txt"))
                };

                string bigTextPath = bigTextCandidatePaths.FirstOrDefault(File.Exists);
                if (string.IsNullOrWhiteSpace(bigTextPath))
                {
                    callsignIndex = new List<string>();
                    return;
                }

                LoadCallsignIndexFromText(bigTextPath);
            }
            catch
            {
                callsignIndex = new List<string>();
            }
        }

        private bool LoadCallsignIndexFromText(string filePath)
        {
            try
            {
                callsignListVersion = 0;
                var set = new HashSet<string>(StringComparer.Ordinal);
                bool firstDataLineHandled = false;
                foreach (var rawLine in File.ReadLines(filePath))
                {
                    string line = rawLine.Trim().ToUpperInvariant();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;

                    string token = line.Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                    if (!firstDataLineHandled)
                    {
                        firstDataLineHandled = true;
                        int parsedVersion;
                        if (!string.IsNullOrWhiteSpace(token) && int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsedVersion))
                        {
                            callsignListVersion = parsedVersion;
                            continue;
                        }
                    }

                    if (string.IsNullOrWhiteSpace(token) || token.Length > 15) continue;

                    set.Add(token);
                }

                callsignIndex = set.ToList();
                callsignIndex.Sort(StringComparer.Ordinal);
                return callsignIndex.Count > 0;
            }
            catch
            {
                return false;
            }
        }

        private void FetchCallsignListUpdateInfoFireAndForget()
        {
            // Log immediately at startup
            try
            {
                string logDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "HolyLogger",
                    "Logs");
                Directory.CreateDirectory(logDir);
                string logPath = Path.Combine(logDir, "callsign_update.log");
                File.WriteAllText(logPath, "Update process started at " + DateTime.Now.ToString() + "\n");
            }
            catch { }

            // Run async work on background thread
            Task.Run(async () =>
            {
                try
                {
                    string logDir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "HolyLogger",
                        "Logs");
                    Directory.CreateDirectory(logDir);
                    string logPath = Path.Combine(logDir, "callsign_update.log");
                    string traceLogPath = Path.Combine(logDir, "callsign_sync_trace.log");
                    bool verboseSyncTrace = Properties.Settings.Default.CallsignSyncVerboseLog;

                    // Keep trace log bounded by rolling it when it gets too large.
                    try
                    {
                        if (verboseSyncTrace && File.Exists(traceLogPath))
                        {
                            var traceInfo = new FileInfo(traceLogPath);
                            if (traceInfo.Length > (10 * 1024 * 1024))
                            {
                                string rolledPath = Path.Combine(
                                    logDir,
                                    "callsign_sync_trace_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".log");
                                File.Move(traceLogPath, rolledPath);
                            }
                        }
                    }
                    catch { }

                    Action<string> appendTrace = message =>
                    {
                        if (!verboseSyncTrace)
                            return;

                        try
                        {
                            File.AppendAllText(traceLogPath, message + "\n");
                        }
                        catch { }
                    };

                    appendTrace("============================================================");
                    appendTrace("SYNC START " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                    appendTrace("Initial local version: " + callsignListVersion.ToString(CultureInfo.InvariantCulture));

                    using (var client = new HttpClient())
                    {
                        client.Timeout = TimeSpan.FromSeconds(20);
                        const int maxBatches = 1000;
                        int batchNumber = 0;
                        bool hasMore;

                        do
                        {
                            int requestVersion = callsignListVersion;
                            File.AppendAllText(logPath, "Building URL with version: " + requestVersion.ToString(CultureInfo.InvariantCulture) + "\n");

                            string url = "https://tools.iarc.org/holyland/server/getcallsign.php?version="
                                + requestVersion.ToString(CultureInfo.InvariantCulture);

                            appendTrace("---- BATCH " + (batchNumber + 1).ToString(CultureInfo.InvariantCulture) + " ----");
                            appendTrace("Request time: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                            appendTrace("Local version before request: " + requestVersion.ToString(CultureInfo.InvariantCulture));
                            appendTrace("Request URL: " + url);

                            File.AppendAllText(logPath, "URL: " + url + "\n");
                            File.AppendAllText(logPath, "Making HTTP request...\n");

                            string serverReply = await client.GetStringAsync(url);
                            File.AppendAllText(logPath, "Server reply received: " + serverReply.Substring(0, Math.Min(200, serverReply.Length)) + "...\n");

                            appendTrace("Raw server reply:");
                            appendTrace(serverReply);

                            var response = Newtonsoft.Json.Linq.JObject.Parse(serverReply);
                            bool success = response["success"] != null && response["success"].ToObject<bool>();
                            hasMore = response["hasMore"] != null && response["hasMore"].ToObject<bool>();
                            int latestVersion = response["latestVersion"] != null
                                ? response["latestVersion"].ToObject<int>()
                                : -1;
                            int itemCount = response["count"] != null
                                ? response["count"].ToObject<int>()
                                : ((response["data"] as Newtonsoft.Json.Linq.JArray)?.Count ?? 0);

                            int addRequests = 0;
                            int removeRequests = 0;
                            var responseData = response["data"] as Newtonsoft.Json.Linq.JArray;
                            if (responseData != null)
                            {
                                foreach (var row in responseData)
                                {
                                    int active = row["active"] != null ? row["active"].ToObject<int>() : 0;
                                    if (active == 1)
                                        addRequests++;
                                    else if (active == -1)
                                        removeRequests++;
                                }
                            }

                            appendTrace(
                                "Parsed reply: success=" + success.ToString(CultureInfo.InvariantCulture)
                                + ", hasMore=" + hasMore.ToString(CultureInfo.InvariantCulture)
                                + ", latestVersion=" + latestVersion.ToString(CultureInfo.InvariantCulture)
                                + ", count=" + itemCount.ToString(CultureInfo.InvariantCulture));
                            appendTrace(
                                "Batch delta requests: adds=" + addRequests.ToString(CultureInfo.InvariantCulture)
                                + ", removes=" + removeRequests.ToString(CultureInfo.InvariantCulture)
                                + ", net=" + (addRequests - removeRequests).ToString(CultureInfo.InvariantCulture));

                            string updateResult = ApplyCallsignListUpdate(serverReply);
                            File.AppendAllText(logPath, "Update result: " + updateResult + "\n");
                            appendTrace("Apply result: " + updateResult);
                            appendTrace("Local version after apply: " + callsignListVersion.ToString(CultureInfo.InvariantCulture));

                            if (updateResult.StartsWith("ERROR:", StringComparison.Ordinal))
                            {
                                appendTrace("Stopping sync because apply returned an error.");
                                break;
                            }

                            batchNumber++;

                            // Prevent infinite loops if the server reports hasMore without version progress.
                            if (hasMore && callsignListVersion <= requestVersion)
                            {
                                File.AppendAllText(logPath, "ERROR: hasMore=true but version did not advance. Stopping to avoid loop.\n");
                                appendTrace("Stopping sync because hasMore=true but version did not advance.");
                                break;
                            }

                            if (batchNumber >= maxBatches)
                            {
                                File.AppendAllText(logPath, "ERROR: Reached max batches (" + maxBatches.ToString(CultureInfo.InvariantCulture) + "). Stopping.\n");
                                appendTrace("Stopping sync because max batch limit was reached: " + maxBatches.ToString(CultureInfo.InvariantCulture));
                                break;
                            }

                            appendTrace("Will request next batch: " + hasMore.ToString(CultureInfo.InvariantCulture));
                        } while (hasMore);

                        File.AppendAllText(logPath, "Update process finished after " + batchNumber.ToString(CultureInfo.InvariantCulture) + " batch(es).\n");
                        appendTrace("SYNC END " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                        appendTrace("Final local version: " + callsignListVersion.ToString(CultureInfo.InvariantCulture));
                        appendTrace("Batches completed: " + batchNumber.ToString(CultureInfo.InvariantCulture));

                        // Keep startup UI quiet: no popup and no status bar updates.
                    }
                }
                catch (Exception ex)
                {
                    string msg = "Callsign update request failed: " + ex.Message;

                    string logDir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "HolyLogger",
                        "Logs");
                    string logPath = Path.Combine(logDir, "callsign_update.log");
                    try
                    {
                        Directory.CreateDirectory(logDir);
                        File.AppendAllText(logPath, "ERROR: " + msg + "\n");

                        if (Properties.Settings.Default.CallsignSyncVerboseLog)
                        {
                            string traceLogPath = Path.Combine(logDir, "callsign_sync_trace.log");
                            File.AppendAllText(
                                traceLogPath,
                                "SYNC ERROR " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                                + " - " + msg + "\n");
                        }
                    }
                    catch { }
                    
                    // No popup on failure; error is kept in status and log.
                }
            });
        }

        private string ApplyCallsignListUpdate(string jsonResponse)
        {
            try
            {
                var response = Newtonsoft.Json.Linq.JObject.Parse(jsonResponse);
                if (response == null || response["success"] == null || !response["success"].ToObject<bool>())
                    return "ERROR: Invalid server response or success field";

                string callsignFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"Data\callsigns_merged_big.txt");
                if (!File.Exists(callsignFilePath))
                    return "ERROR: Callsign file not found at " + callsignFilePath;

                // In dev runs (bin/x86/Debug or Release), also keep project Data file in sync.
                string projectDataFilePath = Path.GetFullPath(
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\..\Data\callsigns_merged_big.txt"));

                var callsignSet = new HashSet<string>(StringComparer.Ordinal);
                var fileLines = File.ReadAllLines(callsignFilePath);
                int newVersion = callsignListVersion;
                bool hasLatestVersion = false;

                if (response["latestVersion"] != null)
                {
                    int parsedLatestVersion;
                    if (int.TryParse(response["latestVersion"].ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsedLatestVersion))
                    {
                        newVersion = parsedLatestVersion;
                        hasLatestVersion = true;
                    }
                }

                foreach (var line in fileLines.Skip(1))
                {
                    string trimmed = line.Trim().ToUpperInvariant();
                    if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("#") || trimmed.StartsWith(";"))
                        continue;
                    string token = trimmed.Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(token) && token.Length <= 15)
                        callsignSet.Add(token);
                }

                var dataArray = response["data"] as Newtonsoft.Json.Linq.JArray;
                if (dataArray == null)
                    return "ERROR: Invalid data array in response";

                int lastItemVersion = 0;

                foreach (var item in dataArray)
                {
                    string callsign = (item["callsign"]?.ToString() ?? "").ToUpperInvariant();
                    int active = item["active"] != null ? item["active"].ToObject<int>() : 0;
                    int version = item["version"] != null ? item["version"].ToObject<int>() : 0;

                    if (!string.IsNullOrEmpty(callsign))
                    {
                        if (active == 1)
                            callsignSet.Add(callsign);
                        else if (active == -1)
                            callsignSet.Remove(callsign);
                    }

                    lastItemVersion = version;
                }

                if (!hasLatestVersion && lastItemVersion > 0)
                    newVersion = lastItemVersion;

                var sortedCallsigns = callsignSet.ToList();
                sortedCallsigns.Sort(StringComparer.Ordinal);

                var outputLines = new List<string> { newVersion.ToString(CultureInfo.InvariantCulture) };
                outputLines.AddRange(sortedCallsigns);

                File.WriteAllLines(callsignFilePath, outputLines);

                bool projectFileUpdated = false;
                if (!string.Equals(callsignFilePath, projectDataFilePath, StringComparison.OrdinalIgnoreCase)
                    && File.Exists(projectDataFilePath))
                {
                    File.WriteAllLines(projectDataFilePath, outputLines);
                    projectFileUpdated = true;
                }

                callsignListVersion = newVersion;
                LoadCallsignIndex();

                return "SUCCESS: File updated to version " + newVersion.ToString() + " with " + sortedCallsigns.Count + " callsigns"
                    + (projectFileUpdated ? " (project Data file synced)" : "");
            }
            catch (Exception ex)
            {
                return "ERROR: " + ex.Message;
            }
        }

        private bool LoadCallsignIndexFromSqlite(string sqlitePath)
        {
            try
            {
                var set = new HashSet<string>(StringComparer.Ordinal);
                using (var con = new SQLiteConnection(@"Data Source = " + sqlitePath + @";Version=3"))
                {
                    con.Open();
                    using (var cmd = new SQLiteCommand("SELECT callsign FROM callsigns ORDER BY callsign", con))
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            string call = reader["callsign"].ToString().Trim().ToUpperInvariant();
                            if (string.IsNullOrWhiteSpace(call) || call.Length > 15) continue;
                            set.Add(call);
                        }
                    }
                }

                callsignIndex = set.ToList();
                callsignIndex.Sort(StringComparer.Ordinal);
                return callsignIndex.Count > 0;
            }
            catch
            {
                return false;
            }
        }

        private void UpdateCallsignSuggestions()
        {
            string prefix = (TB_DXCallsign.Text ?? string.Empty).Trim().ToUpperInvariant();
            var matches = new List<CallsignSuggestionItem>(maxCallsignSuggestions);

            // Wildcard modes:
            // *ABC   -> callsigns ending with ABC
            // *ABC*  -> callsigns containing ABC
            if (prefix.StartsWith("*", StringComparison.Ordinal))
            {
                if (prefix.EndsWith("*", StringComparison.Ordinal) && prefix.Length > 2)
                {
                    string containsTerm = prefix.Substring(1, prefix.Length - 2);
                    for (int i = 0; i < callsignIndex.Count && matches.Count < maxCallsignSuggestions; i++)
                    {
                        string call = callsignIndex[i];
                        int pos = call.IndexOf(containsTerm, StringComparison.Ordinal);
                        if (pos >= 0)
                            matches.Add(BuildSuggestionItem(call, containsTerm, pos));
                    }
                }
                else if (prefix.Length > 1)
                {
                    string suffixTerm = prefix.Substring(1);
                    for (int i = 0; i < callsignIndex.Count && matches.Count < maxCallsignSuggestions; i++)
                    {
                        string call = callsignIndex[i];
                        if (call.EndsWith(suffixTerm, StringComparison.Ordinal))
                            matches.Add(BuildSuggestionItem(call, suffixTerm, call.Length - suffixTerm.Length));
                    }
                }
            }
            else
            {
                if (prefix.Length < 2)
                {
                    CallsignSuggestionsPopup.IsOpen = false;
                    return;
                }

                int index = callsignIndex.BinarySearch(prefix, StringComparer.Ordinal);
                if (index < 0) index = ~index;

                // Two-pass: collect non-slash matches first, then slash matches.
                var slashMatches = new List<CallsignSuggestionItem>();
                for (int i = index; i < callsignIndex.Count; i++)
                {
                    string call = callsignIndex[i];
                    if (!call.StartsWith(prefix, StringComparison.Ordinal)) break;
                    if (call.Contains('/'))
                        slashMatches.Add(BuildSuggestionItem(call, prefix, 0));
                    else if (matches.Count < maxCallsignSuggestions)
                        matches.Add(BuildSuggestionItem(call, prefix, 0));
                }
                // Fill remaining slots with slash matches
                int remaining = maxCallsignSuggestions - matches.Count;
                if (remaining > 0)
                    matches.AddRange(slashMatches.Take(remaining));
            }

            // Show suggestions to the right of the DX callsign textbox, same vertical level.
            Point dxCallPosition = TB_DXCallsign.TranslatePoint(new Point(0, 0), this);
            CallsignSuggestionsPopup.PlacementTarget = this;
            CallsignSuggestionsPopup.Placement = PlacementMode.Relative;
            CallsignSuggestionsPopup.HorizontalOffset = dxCallPosition.X + TB_DXCallsign.ActualWidth - 8;
            CallsignSuggestionsPopup.VerticalOffset = dxCallPosition.Y;

            LB_DXCallsignSuggestions.ItemsSource = matches;
            LB_DXCallsignSuggestions.SelectedIndex = matches.Count > 0 ? 0 : -1;
            callsignSuggestionMouseControl = false;
            CallsignSuggestionsPopup.IsOpen = matches.Count > 0 && Properties.Settings.Default.ShowCallsignDropdown;

            if (!Properties.Settings.Default.ShowCallsignDropdown && prefix.StartsWith("*", StringComparison.Ordinal))
            {
                var tt = new ToolTip
                {
                    Content = "Autocomplete dropdown is disabled.\nEnable it in Tools ? Options ? User Interface\n? \"Show callsign autocomplete dropdown\"",
                    PlacementTarget = TB_DXCallsign,
                    Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
                    IsOpen = true,
                    StaysOpen = false
                };
                TB_DXCallsign.ToolTip = tt;
            }
            else
            {
                TB_DXCallsign.ToolTip = null;
            }
        }

        private static readonly Dictionary<string, string> DxccNameToIso = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            {"Afghanistan","af"},{"Agalega & St. Brandon","mu"},{"Aland Is.","fi"},{"Alaska","us"},
            {"Albania","al"},{"Algeria","dz"},{"Andaman & Nicobar Is.","in"},
            {"Andorra","ad"},{"Angola","ao"},{"Antarctica","aq"},
            {"Antigua & Barbuda","ag"},{"Argentina","ar"},{"Armenia","am"},{"Aruba","aw"},
            {"Ascension I.","sh"},{"Australia","au"},{"Austria","at"},{"Aves I.","ve"},
            {"Azores","pt"},{"Azerbaijan","az"},{"Bahamas","bs"},{"Bahrain","bh"},
            {"Bangladesh","bd"},{"Barbados","bb"},{"Belarus","by"},{"Belgium","be"},
            {"Belize","bz"},{"Benin","bj"},{"Bhutan","bt"},
            {"Bolivia","bo"},{"Bosnia-Herzegovina","ba"},{"Botswana","bw"},
            {"Bouvet","no"},{"Brazil","br"},{"British Virgin Is.","vg"},{"Brunei Darussalam","bn"},
            {"Bulgaria","bg"},{"Burkina Faso","bf"},{"Burundi","bi"},{"Cambodia","kh"},
            {"Cameroon","cm"},{"Canada","ca"},{"Canary Is.","es"},{"Cape Verde","cv"},
            {"Cayman Is.","ky"},{"Central Africa","cf"},{"Ceuta & Melilla","es"},{"Chad","td"},
            {"Chagos Is.","gb"},{"Chatham Is.","nz"},{"Chesterfield Is.","nc"},{"Chile","cl"},
            {"China","cn"},{"Cocos I.","cr"},{"Colombia","co"},
            {"Comoros","km"},{"Congo","cg"},{"Conway Reef","fj"},{"Corsica","fr"},
            {"Costa Rica","cr"},{"Cote d'Ivoire","ci"},{"Crete","gr"},{"Croatia","hr"},
            {"Cuba","cu"},{"Cyprus","cy"},
            {"Czech Republic","cz"},{"Dem. Rep. Of Congo","cd"},{"Denmark","dk"},{"Desecheo I.","pr"},
            {"Djibouti","dj"},{"Dodecanese","gr"},{"Dominica","dm"},{"Dominican Republic","do"},
            {"East Malaysia","my"},{"East Timor","tl"},{"Easter I.","cl"},
            {"Ecuador","ec"},{"Egypt","eg"},{"El Salvador","sv"},{"England","gb"},
            {"Equatorial Guinea","gq"},{"Eritrea","er"},{"Estonia","ee"},{"Ethiopia","et"},
            {"European Russia","ru"},{"Falkland Is.","fk"},{"Faroe Is.","fo"},{"Fed. Rep. of Germany","de"},
            {"Fernando de Noronha","br"},{"Fiji","fj"},{"Finland","fi"},{"France","fr"},
            {"Franz Josef Land","ru"},{"French Polynesia","pf"},{"Gabon","ga"},
            {"Galapagos Is.","ec"},{"Georgia","ge"},{"Ghana","gh"},{"Gibraltar","gi"},
            {"Greece","gr"},{"Greenland","gl"},{"Grenada","gd"},
            {"Guam","gu"},{"Guantanamo Bay","cu"},{"Guatemala","gt"},
            {"Guinea","gn"},{"Guinea-Bissau","gw"},{"Guyana","gy"},
            {"Haiti","ht"},{"Hawaii","us"},{"Honduras","hn"},
            {"Hong Kong","hk"},{"Hungary","hu"},{"Iceland","is"},{"India","in"},
            {"Indonesia","id"},{"Iran","ir"},{"Iraq","iq"},{"Ireland","ie"},
            {"Isle of Man","im"},{"Israel","il"},{"Italy","it"},{"ITU HQ","ch"},
            {"Jamaica","jm"},{"Jan Mayen","no"},{"Japan","jp"},{"Jersey","je"},
            {"Jordan","jo"},{"Juan Fernandez Is.","cl"},{"Kaliningrad","ru"},
            {"Kazakhstan","kz"},{"Kenya","ke"},{"Kermadec Is.","nz"},
            {"Kuwait","kw"},{"Kyrgystan","kg"},{"Laos","la"},{"Latvia","lv"},
            {"Lebanon","lb"},{"Lesotho","ls"},{"Liberia","lr"},{"Libya","ly"},
            {"Liechtenstein","li"},{"Lithuania","lt"},{"Lord Howe I.","au"},{"Luxembourg","lu"},
            {"Macao","mo"},{"Macedonia","mk"},{"Macquarie I.","au"},{"Madagascar","mg"},
            {"Madeira Is.","pt"},{"Maldives","mv"},{"Malawi","mw"},{"Malaysia","my"},
            {"Mali","ml"},{"Malpelo I.","co"},{"Malta","mt"},{"Mariana Is.","mp"},
            {"Market Reef","fi"},{"Marshall Is.","mh"},{"Mauritania","mr"},
            {"Mauritius","mu"},{"Mellish Reef","au"},{"Mexico","mx"},
            {"Micronesia","fm"},{"Minami Torishima","jp"},{"Moldova","md"},
            {"Monaco","mc"},{"Mongolia","mn"},{"Montenegro","me"},{"Montserrat","ms"},
            {"Morocco","ma"},{"Mount Athos","gr"},{"Mozambique","mz"},{"Myanmar","mm"},
            {"Namibia","na"},{"Nauru","nr"},{"Nepal","np"},{"Netherlands","nl"},
            {"New Caledonia","nc"},{"New Zealand","nz"},{"New Zealand Subantarctic Islands","nz"},{"Nicaragua","ni"},
            {"Niger","ne"},{"Nigeria","ng"},{"Norfolk I.","nf"},
            {"North Korea","kp"},{"Northern Ireland","gb"},{"Norway","no"},{"Ogasawara","jp"},
            {"Oman","om"},{"Pakistan","pk"},{"Palestine","ps"},{"Palau","pw"},
            {"Panama","pa"},{"Papua New Guinea","pg"},{"Paraguay","py"},{"Peru","pe"},
            {"Peter I I.","no"},{"Philippines","ph"},{"Poland","pl"},
            {"Portugal","pt"},{"Pratas","tw"},{"Puerto Rico","pr"},{"Qatar","qa"},
            {"Republic of Kosovo","xk"},{"Reunion","re"},{"Rodriguez I.","mu"},{"Romania","ro"},
            {"Rotuma I.","fj"},{"Russia","ru"},{"Asiatic Russia","ru"},{"Rwanda","rw"},
            {"Sao Tome & Principe","st"},
            {"Sardinia","it"},{"Saudi Arabia","sa"},{"Scarborough Reef","ph"},{"Scotland","gb"},
            {"Senegal","sn"},{"Serbia","rs"},{"Seychelles","sc"},{"Sierra Leone","sl"},
            {"Singapore","sg"},{"Slovak Republic","sk"},{"Slovenia","si"},{"Solomon Is.","sb"},
            {"Somalia","so"},{"South Africa","za"},{"South Korea","kr"},
            {"South Sudan","ss"},
            {"Sov. Mil. Order of Malta","it"},{"Spain","es"},{"Spratly Is.","ph"},{"Sri Lanka","lk"},
            {"St. Helena","sh"},{"St. Kitts & Nevis","kn"},{"St. Lucia","lc"},{"St. Maarten","sx"},
            {"St. Peter & St. Paul Rocks","br"},{"St. Pierre & Miquelon","fr"},{"St. Vincent","vc"},
            {"Sudan","sd"},{"Suriname","sr"},{"Swaziland","sz"},
            {"Sweden","se"},{"Switzerland","ch"},{"Syria","sy"},{"Taiwan","tw"},
            {"Tajikistan","tj"},{"Tanzania","tz"},{"Temotu Province","sb"},{"Thailand","th"},
            {"The Gambia","gm"},{"Togo","tg"},{"Tonga","to"},
            {"Trinidad & Tobago","tt"},{"Trindade & Martim Vaz Is.","br"},{"Tunisia","tn"},
            {"Turkey","tr"},{"Turkmenistan","tm"},{"Turks & Caicos Is.","tc"},{"Tuvalu","tv"},
            {"Uganda","ug"},{"UK Sovereign Base Areas on Cyprus","cy"},{"Ukraine","ua"},{"United Arab Emirates","ae"},
            {"United States of America","us"},{"Uruguay","uy"},{"Uzbekistan","uz"},
            {"Vanuatu","vu"},{"Vatican","va"},{"Venezuela","ve"},{"Vietnam","vn"},
            {"Virgin Is.","vi"},{"Wales","gb"},{"Wallis & Futuna Is.","wf"},{"West Malaysia","my"},
            {"Western Samoa","ws"},{"Willis I.","au"},{"Yemen","ye"},
            {"Zambia","zm"},{"Zimbabwe","zw"},{"Balearic Is.","es"},{"C. Kiribati (British Phoenix Is.)","ki"},
            {"E. Kiribati (Line Is.)","ki"},{"W. Kiribati (Gilbert Is. )","ki"},{"Banaba I. (Ocean I.)","ki"},
            {"San Andres & Providencia","co"},{"San Felix & San Ambrosio","cl"},{"Navassa I.","ht"},
            {"American Samoa","us"},{"Austral I.","fr"},{"Baker & Howland Is.","us"},{"Christmas I.","au"},
            {"Clipperton I.","fr"},{"Johnston I.","us"},{"Kure I.","us"},{"Lakshadweep Is.","in"},
            {"Marquesas I.","fr"},{"N. Cook Is.","nz"},{"Pagalu I.","gq"},{"Palmyra & Jarvis Is.","us"},
            {"Prince Edward & Marion Is.","za"},{"Revilla Gigedo","mx"},{"S. Cook Is.","nz"},{"Sable I.","ca"},
            {"San Marino","sm"},{"St. Paul I.","ca"},{"Swains I.","us"},{"Tristan da Cunha & Gough I.","gb"},
            {"Wake I.","us"},
            {"Amsterdam & St. Paul Is.","fr"},{"Anguilla","gb"},{"Bermuda","gb"},
            {"Bonaire","nl"},{"Cocos (Keeling) Is.","au"},{"Crozet I.","fr"},{"Curacao","nl"},
            {"Ducie I.","gb"},{"French Guiana","fr"},{"Glorioso Is.","fr"},{"Guadeloupe","fr"},
            {"Guernsey","gb"},{"Heard I.","au"},{"Juan de Nova, Europa","fr"},{"Kerguelen Is.","fr"},
            {"Martinique","fr"},{"Mayotte","fr"},{"Midway I.","us"},
            {"Pitcairn I.","gb"},{"Saba & St. Eustatius","nl"},
            {"Saint Barthelemy","fr"},{"Saint Martin","fr"},{"South Georgia I.","gb"},
            {"South Orkney Is.","gb"},{"South Sandwich Is.","gb"},{"South Shetland Is.","gb"},
            {"St Maarten","nl"},{"Svalbard","no"},{"Tokelau Is.","nz"},{"Tromelin I.","fr"},
            {"Western Sahara","ma"},{"Niue","nu"},{"United Nations HQ","un"},
        };

        private void UpdateCountryFlag(string countryName)
        {
            if (string.IsNullOrWhiteSpace(countryName))
            {
                Img_CountryFlag.Visibility = Visibility.Collapsed;
                L_CountryLabel.Visibility = Visibility.Visible;
                return;
            }
            if (DxccNameToIso.TryGetValue(countryName, out string isoCode))
            {
                try
                {
                    var uri = new Uri($"pack://application:,,,/Images/flags/{isoCode}.png", UriKind.Absolute);
                    Img_CountryFlag.Source = new System.Windows.Media.Imaging.BitmapImage(uri);
                    Img_CountryFlag.ToolTip = countryName;
                    Img_CountryFlag.Visibility = Visibility.Visible;
                    L_CountryLabel.Visibility = Visibility.Collapsed;
                    return;
                }
                catch { }
            }
            Img_CountryFlag.Visibility = Visibility.Collapsed;
            L_CountryLabel.Visibility = Visibility.Visible;
        }

        private void ApplySelectedCallsignSuggestion()
        {
            string selected = (LB_DXCallsignSuggestions.SelectedItem as CallsignSuggestionItem)?.FullCallsign;
            if (string.IsNullOrWhiteSpace(selected)) return;

            isApplyingSuggestion = true;
            TB_DXCallsign.Text = selected;
            TB_DXCallsign.CaretIndex = TB_DXCallsign.Text.Length;
            isApplyingSuggestion = false;
            CallsignSuggestionsPopup.IsOpen = false;
        }

        private void LB_DXCallsignSuggestions_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var source = e.OriginalSource as DependencyObject;
            var item = ItemsControl.ContainerFromElement(LB_DXCallsignSuggestions, source) as ListBoxItem;
            if (item?.DataContext is CallsignSuggestionItem clicked)
            {
                callsignSuggestionMouseControl = true;
                LB_DXCallsignSuggestions.SelectedItem = clicked;
                ApplySelectedCallsignSuggestion();
                e.Handled = true;
            }
        }

        private void LB_DXCallsignSuggestions_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            var source = e.OriginalSource as DependencyObject;
            var item = ItemsControl.ContainerFromElement(LB_DXCallsignSuggestions, source) as ListBoxItem;
            if (item?.DataContext is CallsignSuggestionItem hovered)
            {
                callsignSuggestionMouseControl = true;
                if (!Equals(LB_DXCallsignSuggestions.SelectedItem, hovered))
                {
                    LB_DXCallsignSuggestions.SelectedItem = hovered;
                    ApplyHighlightedCallsignSuggestionToTextBox();
                }
            }
        }

        private void LB_DXCallsignSuggestions_MouseLeave(object sender, MouseEventArgs e)
        {
            // Keep the last highlighted row selected, but give arrow-key control back to keyboard.
            callsignSuggestionMouseControl = false;
        }

        private void LB_DXCallsignSuggestions_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            ApplySelectedCallsignSuggestion();
        }

        private class CallsignSuggestionItem
        {
            public string Before { get; set; }
            public string Match { get; set; }
            public string After { get; set; }
            public string FullCallsign => Before + Match + After;
        }

        private CallsignSuggestionItem BuildSuggestionItem(string callsign, string matchTerm, int matchStart)
        {
            return new CallsignSuggestionItem
            {
                Before = callsign.Substring(0, matchStart),
                Match = callsign.Substring(matchStart, matchTerm.Length),
                After = callsign.Substring(matchStart + matchTerm.Length)
            };
        }

        private int NormalizeCallsignSuggestionRows(int rows)
        {
            if (rows <= 0) return DefaultCallsignSuggestionRows;
            return Math.Max(MinCallsignSuggestionRows, Math.Min(MaxCallsignSuggestionRows, rows));
        }

        private void ApplyCallsignSuggestionRowsSetting()
        {
            int rows = NormalizeCallsignSuggestionRows(Properties.Settings.Default.CallsignSuggestionRows);

            maxCallsignSuggestions = rows;
            LB_DXCallsignSuggestions.MaxHeight = rows * CallsignSuggestionRowHeight;
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
            SaveMainWindowBounds();
        }

        private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            SaveMainWindowBounds();
        }

        private void SaveMainWindowBounds()
        {
            if (!hasRestoredMainWindowBounds)
                return;

            Rect bounds = WindowState == WindowState.Normal
                ? new Rect(Left, Top, Width, Height)
                : RestoreBounds;

            if (bounds.Width > 0)
                Properties.Settings.Default.MainWindowWidth = bounds.Width;
            if (bounds.Height > 0)
                Properties.Settings.Default.MainWindowHeight = bounds.Height;

            Properties.Settings.Default.MainWindowLeft = bounds.Left;
            Properties.Settings.Default.MainWindowTop = bounds.Top;
        }

        private void SetAzimuth()
        {
            if (!string.IsNullOrWhiteSpace(TB_MyLocator.Text) && !string.IsNullOrWhiteSpace(TB_DXCallsign.Text))
            {
                try
                {
                    // Priority for map center:
                    // 1. QRZ grid ק the station's declared operating grid square
                    // 2. DXCC entity locator ק country-level fallback
                    // Note: QRZ lat/lon is intentionally skipped ק it reflects the
                    //       operator's home address which can be in a different country.
                    string locator = null;

                    if (!string.IsNullOrWhiteSpace(QRZGrid))
                        locator = QRZGrid;

                    if (string.IsNullOrWhiteSpace(locator))
                    {
                        DXCC entityDXCC = rem.GetDXCC(TB_DXCallsign.Text);
                        if (entityDXCC != null && !string.IsNullOrWhiteSpace(entityDXCC.Locator))
                            locator = entityDXCC.Locator;
                    }

                    if (string.IsNullOrWhiteSpace(locator))
                    {
                        ClearAzimuth();
                        return;
                    }

                    Azimuth = MaidenheadLocator.Azimuth(TB_MyLocator.Text, locator);
                    var ll = MaidenheadLocator.LocatorToLatLng(locator);
                    var homell = MaidenheadLocator.LocatorToLatLng(TB_MyLocator.Text);
                    // Auto-fit: compute distance between home and DX, add 10% padding.
                    double distKm = MaidenheadLocator.Distance(homell, ll);
                    int autoFitRadius = Math.Max(500, (int)(distKm * 1.10));
                    _dxQsoInProgress = true;
                    MapControl.ShowMap(ll.Lat, ll.Long, autoFitRadius, Azimuth, homell.Lat, homell.Long);
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
            _dxQsoInProgress = false;
            // Immediately repaint cluster spots instead of waiting for the debounce timer.
            if (MapControl != null && MapControl.Visibility == Visibility.Visible
                && Properties.Settings.Default.ClusterMapEnabled)
            {
                DoUpdateClusterSpotsOnMap();
            }
            else
            {
                ShowHomeMap();
            }
        }

        private void ShowHomeMap()
        {
            if (MapControl == null) return;
            if (!string.IsNullOrWhiteSpace(TB_MyLocator.Text))
            {
                try
                {
                    var ll = MaidenheadLocator.LocatorToLatLng(TB_MyLocator.Text);
                    MapControl.ShowMap(ll.Lat, ll.Long, GetMapRadiusKm());
                }
                catch { }
            }
            else
            {
                MapControl.ShowPlaceholder("Please set My Locator&#x0a;to enable the map");
            }
        }

        private int GetMapRadiusKm()
        {
            int radiusKm = Properties.Settings.Default.MapRadiusKm;
            if (radiusKm < 100 || radiusKm > 20000)
            {
                return 3500;
            }

            return radiusKm;
        }

        private void OnMapRadiusChanged(int radiusKm)
        {
            if (Properties.Settings.Default.MapRadiusKm != radiusKm)
            {
                Properties.Settings.Default.MapRadiusKm = radiusKm;
                Properties.Settings.Default.Save();
            }
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (MapControl != null && MapControl.Visibility == Visibility.Visible)
                {
                    if (MapControl.IsClusterMode)
                        UpdateClusterSpotsOnMap();
                    else
                        SetAzimuth();
                }
            }), DispatcherPriority.Background);
        }

        private void OnMapSpotTuneRequested(string freq, string mode)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                // Find the matching visible spot by freq+mode and reuse TuneToClusterSpot
                if (clusterVisibleSpots == null) return;
                double freqVal;
                if (!double.TryParse(freq, System.Globalization.NumberStyles.Float, CultureInfo.InvariantCulture, out freqVal) || freqVal <= 0)
                    return;
                // Build a temporary spot so TuneToClusterSpot can do the full tune sequence
                var tempSpot = clusterVisibleSpots.FirstOrDefault(s =>
                {
                    if (!double.TryParse(s.FreqText ?? string.Empty, System.Globalization.NumberStyles.Float, CultureInfo.InvariantCulture, out double sv) || sv <= 0)
                        return false;
                    double sMhz = sv >= 1000 ? sv / 1000.0 : sv;
                    return Math.Abs(sMhz - freqVal) < 0.001 &&
                           string.Equals(s.Mode ?? string.Empty, mode ?? string.Empty, StringComparison.OrdinalIgnoreCase);
                });
                if (tempSpot != null)
                {
                    TuneToClusterSpot(tempSpot);
                }
                else
                {
                    // Fallback: build a minimal spot from the raw freq/mode strings
                    var fallback = new ClusterSpotViewItem
                    {
                        FreqText = freq,
                        Mode = mode,
                        DXCallsign = string.Empty
                    };
                    TuneToClusterSpot(fallback);
                }
            }), DispatcherPriority.Normal);
        }

        private void Settings_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Properties.Settings.Default.ShowOnTheAir))
            {
                Dispatcher.BeginInvoke(new Action(UpdateShareIconVisibility), DispatcherPriority.Background);
            }

            if (e.PropertyName == nameof(Properties.Settings.Default.ShowClusterWindowOption))
            {
                Dispatcher.BeginInvoke(new Action(ApplyClusterWindowSetting), DispatcherPriority.Background);
            }

            if (e.PropertyName == nameof(Properties.Settings.Default.MainFormBackgroundColor))
            {
                Dispatcher.BeginInvoke(new Action(ApplyMainFormBackgroundFromSettings), DispatcherPriority.Background);
            }

            if (e.PropertyName == nameof(Properties.Settings.Default.QsoTableHeaderBackgroundColor))
            {
                Dispatcher.BeginInvoke(new Action(ApplyQsoTableHeaderBackgroundFromSettings), DispatcherPriority.Background);
            }

            if (e.PropertyName == nameof(Properties.Settings.Default.ShowPhotoFromQRZ))
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!Properties.Settings.Default.ShowPhotoFromQRZ)
                    {
                        ClearQrzPhoto();
                    }
                }), DispatcherPriority.Background);
            }

            if (e.PropertyName == nameof(Properties.Settings.Default.MapDistanceUnit))
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (MapControl != null && MapControl.Visibility == Visibility.Visible)
                    {
                        SetAzimuth();
                    }
                }), DispatcherPriority.Background);
            }
        }

        public void RefreshMapAfterUnitChange()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (MapControl == null || MapControl.Visibility != Visibility.Visible)
                {
                    return;
                }

                MapControl.RefreshMap();
            }), DispatcherPriority.Background);
        }

        private void UpdateShareIconVisibility()
        {
            if (ShareStatusButton == null)
            {
                return;
            }

            ShareStatusButton.Visibility = isNetworkAvailable && Properties.Settings.Default.ShowOnTheAir
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void ApplyClusterWindowSetting()
        {
            if (Properties.Settings.Default.ShowClusterWindowOption)
            {
                if (clusterWindow == null)
                {
                    GenerateNewClusterWindow();
                }
            }
            else
            {
                if (clusterWindow != null)
                {
                    clusterWindow.Close();
                    clusterWindow = null;
                }
            }
        }

        private Color ParseMainFormBackgroundColor(string colorText)
        {
            try
            {
                var parsed = (Color)ColorConverter.ConvertFromString(colorText);
                return parsed;
            }
            catch
            {
                return (Color)ColorConverter.ConvertFromString("#BDDFFF");
            }
        }

        private Color ParseQsoTableHeaderBackgroundColor(string colorText)
        {
            try
            {
                var parsed = (Color)ColorConverter.ConvertFromString(colorText);
                return parsed;
            }
            catch
            {
                return (Color)ColorConverter.ConvertFromString("#DEB887");
            }
        }

        private void ApplyMainFormBackgroundFromSettings()
        {
            if (MainFormBackgroundRect == null)
            {
                return;
            }

            Color color = ParseMainFormBackgroundColor(Properties.Settings.Default.MainFormBackgroundColor);
            MainFormBackgroundRect.Fill = new SolidColorBrush(color);
        }

        private void ApplyQsoTableHeaderBackgroundFromSettings()
        {
            if (QSODataGrid == null)
            {
                return;
            }

            Color color = ParseQsoTableHeaderBackgroundColor(Properties.Settings.Default.QsoTableHeaderBackgroundColor);

            var headerStyle = new Style(typeof(DataGridColumnHeader));
            headerStyle.Setters.Add(new Setter(Control.BorderBrushProperty, (Brush)new BrushConverter().ConvertFromString("#1565C0")));
            headerStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0, 0, 1, 3)));
            headerStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(5, 3, 5, 3)));
            headerStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
            headerStyle.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(color)));

            QSODataGrid.ColumnHeaderStyle = headerStyle;

            ApplyClusterTableHeaderBackgroundFromSettings(color);
        }

        private void ApplyClusterTableHeaderBackgroundFromSettings(Color color)
        {
            if (clusterSpotsDataGrid == null)
            {
                return;
            }

            var clusterHeaderStyle = new Style(typeof(DataGridColumnHeader));
            clusterHeaderStyle.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(color)));
            clusterHeaderStyle.Setters.Add(new Setter(Control.BorderBrushProperty, (Brush)new BrushConverter().ConvertFromString("#1565C0")));
            clusterHeaderStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0, 0, 1, 3)));
            clusterHeaderStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(5, 3, 5, 3)));
            clusterHeaderStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));

            clusterSpotsDataGrid.ColumnHeaderStyle = clusterHeaderStyle;
            foreach (var col in clusterSpotsDataGrid.Columns)
            {
                col.HeaderStyle = new Style(typeof(DataGridColumnHeader), clusterHeaderStyle);
            }
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

                try
                {
                    string baseRequest = "http://xmldata.qrz.com/xml/current/?s=";
                    var response = await _sharedHttpClient.GetAsync(baseRequest + SessionKey + ";callsign=" + bare_dxcall);
                    var responseFromServer = await response.Content.ReadAsStringAsync();
                    XDocument xDoc = XDocument.Parse(responseFromServer);
                    XNamespace ns = xDoc.Root.GetDefaultNamespace();

                    if (!string.IsNullOrWhiteSpace(SessionKey) && !string.IsNullOrWhiteSpace(TB_DXCallsign.Text) && (dxcall == TB_DXCallsign.Text.Trim()))
                    {
                        IEnumerable<XElement> xref = xDoc.Root.Descendants(ns + "xref");
                        IEnumerable<XElement> call = xDoc.Root.Descendants(ns + "call");
                        IEnumerable<XElement> error = xDoc.Root.Descendants(ns + "Error");

                        if (call.Count() > 0 || xref.Count() > 0)
                        {
                            IEnumerable<XElement> fname = xDoc.Root.Descendants(ns + "fname");
                            if (fname.Count() > 0)
                                FName = fname.FirstOrDefault().Value;
                            else
                                FName = "";

                            IEnumerable<XElement> lname = xDoc.Root.Descendants(ns + "name");
                            if (lname.Count() > 0)
                                FName += " " + lname.FirstOrDefault().Value;

                            //****************** AZIMUTH *****************//
                            IEnumerable<XElement> lat = xDoc.Root.Descendants(ns + "lat");
                            if (lat.Count() > 0)
                                QRZLat = lat.FirstOrDefault().Value;

                            IEnumerable<XElement> lon = xDoc.Root.Descendants(ns + "lon");
                            if (lon.Count() > 0)
                                QRZLon = lon.FirstOrDefault().Value;

                            IEnumerable<XElement> grid = xDoc.Root.Descendants(ns + "grid");
                            if (grid.Count() > 0)
                                QRZGrid = grid.FirstOrDefault().Value.ToUpper();

                            IEnumerable<XElement> stateEl = xDoc.Root.Descendants(ns + "state");
                            TB_State.Text = stateEl.Count() > 0 ? stateEl.FirstOrDefault().Value.Trim() : string.Empty;

                            SetAzimuth();
                            SetDXLocator(QRZGrid);
                            //*************************************************//

                            AddNewCallsignIfMissing(bare_dxcall);

                            try
                            {
                                IEnumerable<XElement> image = xDoc.Root.Descendants(ns + "image");
                                string xmlImageUrl = image.Select(i => i.Value).FirstOrDefault();
                                if (!string.IsNullOrWhiteSpace(xmlImageUrl))
                                {
                                    SetQrzPhoto(xmlImageUrl);
                                }
                                else
                                {
                                    await LoadQrzPhotoFromWebAsync(bare_dxcall);
                                }
                            }
                            catch
                            {
                                ClearQrzPhoto();
                            }

                            string key = xDoc.Root.Descendants(ns + "Key").FirstOrDefault().Value;
                            if (SessionKey != key)
                                if (isNetworkAvailable) Helper.LoginToQRZ(out _SessionKey);
                        }
                        else if (error.Count() > 0)
                        {
                            string errorCall = error.FirstOrDefault().Value.Split(':')[1].Trim();
                            if (errorCall == dxcall || errorCall == bare_dxcall)
                            {
                                FName = "";
                                TB_State.Text = "";
                                ClearQrzPhoto();
                            }
                        }
                    }
                }
                catch (Exception)
                {
                    FName = "";
                    TB_State.Text = "";
                    ClearQrzPhoto();
                }
            }
            else
            {
                FName = "";
                TB_State.Text = "";
                ClearQrzPhoto();
            }
        }

        private async Task LoadQrzPhotoFromWebAsync(string bareCallsign)
        {
            if (string.IsNullOrWhiteSpace(bareCallsign))
            {
                ClearQrzPhoto();
                return;
            }

            try
            {
                using (var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate })
                using (var client = new HttpClient(handler))
                {
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
                    client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
                    client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
                    string html = string.Empty;

                    if (!string.IsNullOrWhiteSpace(SessionKey))
                    {
                        html = await client.GetStringAsync("https://xmldata.qrz.com/xml/current/?s=" + SessionKey + ";html=" + bareCallsign);
                    }

                    if (string.IsNullOrWhiteSpace(html))
                    {
                        html = await client.GetStringAsync("https://www.qrz.com/db/" + bareCallsign);
                    }

                    Match match = Regex.Match(html, @"https://cdn-bio\.qrz\.com/[^""'<> --]+", RegexOptions.IgnoreCase);
                    if (match.Success)
                    {
                        SetQrzPhoto(match.Value);
                        return;
                    }

                    Match altMatch = Regex.Match(html, @"https?://[^""'<> --]+\.(jpg|jpeg|png|gif)", RegexOptions.IgnoreCase);
                    if (altMatch.Success)
                    {
                        SetQrzPhoto(altMatch.Value);
                        return;
                    }
                }
            }
            catch
            {
            }

            ClearQrzPhoto();
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
            try
            {
                string baseRequest = "http://xmldata.qrz.com/xml/current/?s=";
                var response = await _sharedHttpClient.GetAsync(baseRequest + SessionKey + ";callsign=" + Services.getBareCallsign(callsign));
                var responseFromServer = await response.Content.ReadAsStringAsync();
                XDocument xDoc = XDocument.Parse(responseFromServer);

                if (!string.IsNullOrWhiteSpace(SessionKey) && !string.IsNullOrWhiteSpace(callsign))
                {
                    XNamespace ns = xDoc.Root.GetDefaultNamespace();
                    IEnumerable<XElement> call = xDoc.Root.Descendants(ns + "call");

                    if (call.Count() > 0)
                    {
                        string name = "";
                        IEnumerable<XElement> fname = xDoc.Root.Descendants(ns + "fname");
                        if (fname.Count() > 0)
                            name = fname.FirstOrDefault().Value;

                        IEnumerable<XElement> lname = xDoc.Root.Descendants(ns + "name");
                        if (lname.Count() > 0)
                            name += " " + lname.FirstOrDefault().Value;

                        string key = xDoc.Root.Descendants(ns + "Key").FirstOrDefault().Value;
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
                StopOmniRig();
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
        private bool _showRigParamsQueued;

        private void StartOmniRig()
        {
            try
            {
                if (OmniRigEngine != null)
                {
                    //MessageBox.Show("OmniRig Is running");
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
        private void StopOmniRig()
        {
            UnsubscribeFromEvents();
            Process[] workers = Process.GetProcessesByName("OmniRig");
            foreach (Process worker in workers)
            {
                worker.Kill();
                worker.WaitForExit();
                worker.Dispose();
            }
            OmniRigEngine = null;
            Rig = null;
            UpdateStatus();
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
            QueueShowRigParams();
        }

        //OmniRig StatusChange events
        private void OmniRigEngine_StatusChange(int RigNumber)
        {
            QueueShowRigParams();
        }

        private void QueueShowRigParams()
        {
            if (_showRigParamsQueued)
            {
                return;
            }

            _showRigParamsQueued = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _showRigParamsQueued = false;
                ShowRigParams();
            }), DispatcherPriority.Background);
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
                    UpdateStatus();
                });
            }
        }

        private void UpdateStatus()
        {
            TB_Frequency.BorderBrush = System.Windows.Media.Brushes.Gray;
            L_OmniRig.Foreground = System.Windows.Media.Brushes.Black;
            L_OmniRig.FontWeight = FontWeights.Normal;
            
            Status = "CAT Enabled";
            if (!Properties.Settings.Default.EnableOmniRigCAT || Rig == null || Rig.Status != OmniRig.RigStatusX.ST_ONLINE)//disabled or offline -> red border
            {
                TB_Frequency.BorderBrush = System.Windows.Media.Brushes.Red;
                TB_Frequency.BorderThickness = new Thickness(2);
            }
            else // -> normal border
            {
                TB_Frequency.BorderBrush = System.Windows.Media.Brushes.Gray;
                TB_Frequency.BorderThickness = new Thickness(1);
            }
            
            if (Rig == null)
            {
                Status = "CAT Disabled";
                return;
            }
            Status = Rig.StatusStr;
            if (Rig.Status == OmniRig.RigStatusX.ST_ONLINE)//online
            {
                Status = string.IsNullOrWhiteSpace(Rig.RigType) ? "CAT Enabled" : Rig.RigType;
                L_OmniRig.Foreground = System.Windows.Media.Brushes.Green;
                L_OmniRig.FontWeight = FontWeights.Bold;
            }
            if (!Properties.Settings.Default.EnableOmniRigCAT)//disabled
            {
                Status = "CAT Disabled";
            }
            if (state == State.Edit)
            {
                Status = "Edit Mode";
            }
        }

        private void ShowRigParams()
        {
            ShowRigStatus();
            if (OmniRigEngine == null || Rig == null || Rig.Status != OmniRig.RigStatusX.ST_ONLINE || !Properties.Settings.Default.EnableOmniRigCAT)
            {
                ClearVoiceMessageState();
                return;
            }

            if (Properties.Settings.Default.isManualMode || state == State.Edit)
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
                    CB_Mode.Text = GetNormalizedRigMode();

                    UpdateVoiceMessageState();
                    UpdateVoiceMessageAvailabilityState();
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

    public class QsoDateDisplayConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string raw = value as string;
            if (!string.IsNullOrWhiteSpace(raw) && raw.Length == 8 && DateTime.TryParseExact(raw, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime dt))
            {
                return dt.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture);
            }

            return value;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return System.Windows.Data.Binding.DoNothing;
        }
    }

    public class QsoTimeDisplayConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string raw = value as string;
            if (!string.IsNullOrWhiteSpace(raw) && raw.Length == 6 && DateTime.TryParseExact(raw, "HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime dt))
            {
                return dt.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            }

            return value;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return System.Windows.Data.Binding.DoNothing;
        }
    }
}




