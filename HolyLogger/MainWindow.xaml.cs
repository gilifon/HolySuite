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
            catch (ObjectDisposedException) { /* socket closed during shutdown — expected */ }
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
            catch (ObjectDisposedException) { /* socket closed during shutdown — expected */ }
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


;;;





