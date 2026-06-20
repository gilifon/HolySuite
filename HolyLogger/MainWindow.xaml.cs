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
        // Read-only overlay that renders the frequency as ##.### (3 decimals) when the field is
        // not being edited. The underlying TB_Frequency keeps the full-precision source value,
        // so logging, heartbeat, and ADIF are unaffected — only the on-screen display is shortened.
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

        private string _UploadProgressTitle;
        public string UploadProgressTitle
        {
            get { return _UploadProgressTitle; }
            set
            {
                _UploadProgressTitle = value;
                OnPropertyChanged("UploadProgressTitle");
            }
        }

        private sealed class AdifImportResult
        {
            public int FaultyQso { get; set; }
            public int ImportedQsoCount { get; set; }
            public ObservableCollection<QSO> RefreshedQsos { get; set; }
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

        private double _azimuth;
        public double Azimuth
        {
            get { return _azimuth; }
            set
            {
                _azimuth = value;
                UpdateCompassDisplay();
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

        private bool isRemoteServerLiveLog = false;
        private bool isInitializeComponentsComplete = false;
        private bool hasRestoredMainWindowBounds = false;

        public bool isNetworkAvailable { get; set; }

        HolyLogParser _holyLogParser;
        // UNUSED: Process field was declared but never used in the codebase.
        // If you need to launch QRZ processes in the future, uncomment this:
        // Process QRZProcess;

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
        // While the mouse hovers a band checkbox, the cluster temporarily shows ONLY that band's
        // spots (table + map), as if it were the active band; cleared when the mouse leaves.
        string _clusterHoverBandOverride = null;
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
        StackPanel clusterBandSelectorPanel = null;
        ComboBox clusterLastMinutesComboBox = null;
        int clusterLastMinutesFilterValue = 60;
        DispatcherTimer clusterSingleClickOpenQrzTimer = null;
        string clusterPendingQrzCallsign = null;
        DataGridColumn clusterLastHoverToolTipColumn = null;
        ToolTip clusterHoverToolTip = null;
        bool clusterHoverPopupEnabled = true;
        // DX callsign of the cluster-list row currently hovered, whose map dot is enlarged.
        string _lastHoveredSpotCall = null;
        Button clusterUndoButton = null;
        TextBlock clusterUndoCountText = null;
        TextBlock clusterSpotCountText = null;
        Stack<(string FrequencyText, string ModeText, string DxCallsignText)> clusterUndoStates = new Stack<(string FrequencyText, string ModeText, string DxCallsignText)>();
        // Independent undo stack for the log-row "Set Radio to Freq" action — kept separate from the cluster undo.
        Stack<(string FrequencyText, string ModeText, string DxCallsignText)> logRadioUndoStates = new Stack<(string FrequencyText, string ModeText, string DxCallsignText)>();
        bool clusterHeaderAlignmentRefreshPending = false;
        Action _clusterWidthHandlerCleanup = null;

        // Layout constants for the cluster window floating overlay panels
        const double ClusterOffScreenPosition = -400;
        const double ClusterHeaderCanvasHeight = 92;
        const double ClusterTableTopGap = 10;
        const double ClusterShowBandsPanelWidth = 115;
        // Fixed half-width used to center the active-band indicator under the Freq column.
        // Using a constant (instead of the indicator's live ActualWidth) keeps the Selected/
        // All Bands buttons at the same horizontal position whether the band is legal, illegal,
        // or a different band name (which would otherwise have a different text width).
        const double ClusterBandIndicatorHalfWidth = 15.0;
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
        SearchWindow searchWindow = null;
        StatisticsWindow statisticsWindow = null;
        QRZPhotoWindow qrzPhotoWindow = null;
        double? qrzPhotoLeft = null;
        double? qrzPhotoTop = null;
        double? qrzPhotoWidth = null;
        double? qrzPhotoHeight = null;
        string currentQrzImageUrl = null; // Track current QRZ photo URL for graphics box display
        bool qrzPhotoClearQueued = false;

        BackgroundWorker AdifHandlerWorker;
        private bool _isShutdownCleanupDone = false;
        private bool _uploadOnExitHandled = false; // guards the single upload-on-exit pass in Window_Closing
        // UNUSED: BackgroundWorker for entire log QRZ processing was disabled.
        // Left commented for future reference if batch QRZ processing is needed:
        // BackgroundWorker EntireLogQrzWorker;

        private StickyWindow _stickyWindow;
        private State state = State.New;
        private bool NotifyVersionUpToDate = false;

        QSO QsoToUpdate;
        QSO QsoPreUpdate;
        QSO LastQSO;

        private List<string> callsignIndex = new List<string>();
        private bool isApplyingSuggestion = false;
        // Set when a callsign is pulled from the cluster/map (not typed) so the suggestions dropdown stays closed.
        private bool suppressNextCallsignSuggestions = false;
        // Set while loading a logged QSO into the form for editing, so setting the DX callsign does not
        // trigger the lookup that would clear/overwrite the QSO's saved Name/Locator/Country/etc.
        private bool _suppressCallsignLookupForEdit = false;
        private const int DefaultCallsignSuggestionRows = 20;
        private const int MinCallsignSuggestionRows = 10;
        private const int MaxCallsignSuggestionRows = 30;
        private const double CallsignSuggestionRowHeight = 22;
        private const int CallsignLookupDebounceMs = 280;
        // How long the DX callsign must stay unchanged (after name/locator are shown) before the QRZ
        // photo is fetched. Quick typing/corrections bump callsignLookupRevision and skip the download.
        private const int QrzPhotoDelayMs = 0;
        // The visible-rows setting only controls how many rows are shown at once; the list can hold
        // up to this many matches so the user can scroll through the full set (often hundreds).
        private const int MaxCallsignSuggestionResults = 500;
        private int maxCallsignSuggestions = MaxCallsignSuggestionResults;
        private bool callsignSuggestionMouseControl = false;
        // Last physical cursor position over the suggestion list. Used to ignore synthetic MouseMove
        // events WPF raises when the item under a stationary cursor changes (list re-populates after
        // deleting '?', or scrolls via the keyboard).
        private Point? lastCallsignSuggestionMousePos = null;
        private HashSet<string> newCallsignsSet = new HashSet<string>(StringComparer.Ordinal);
        private CallsignUploader _callsignUploader;
        private int callsignListVersion = 0;
        private int callsignLookupRevision = 0;

        DispatcherTimer UTCTimer = new DispatcherTimer();
        DispatcherTimer HeartbeatTimer = new DispatcherTimer();
        DispatcherTimer CallsignLookupDebounceTimer = new DispatcherTimer(DispatcherPriority.Background);
        DispatcherTimer VoiceMessageAvailabilityTimer = new DispatcherTimer();
        System.Windows.Forms.Timer NewDXCCTimer = new System.Windows.Forms.Timer();

        // High-Priority Stability Improvements
        private static readonly HttpClient _sharedHttpClient = new HttpClient(new WebRequestHandler { CachePolicy = new HttpRequestCachePolicy(HttpRequestCacheLevel.BypassCache) }) { Timeout = TimeSpan.FromSeconds(20) };
        // Serializes access to the shared database (inserts, batch import, full refresh) across the
        // UI thread and background threads (UDP loggers, ADIF import worker). Use this instead of
        // lock(this), which external code could also lock on and deadlock.
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

        // CW sending monitor: visualises the keyed text with a blinking cursor advancing in sync
        // with the radio. The radio does not report keying progress, so the cursor is driven by a
        // self-calibrated WPM estimate (cwLearnedWpm), refined after each transmission from the real
        // elapsed TX time divided by the message's PARIS unit count.
        private CwSendMonitorWindow cwSendMonitor;
        private bool cwMonitorCursorStarted;
        private double cwMonitorTotalUnits;
        private DateTime cwMonitorStartUtc;
        private double cwLearnedWpm = 20.0;

        // The two states of the main-window QRZ icon: the normal blue globe when QRZ.com is
        // reachable/logged in, and the grayed globe when there is no connection to QRZ.com.
        // Swapped by SetQrzConnected().
        BitmapImage qrz_on_path = new BitmapImage(new Uri("Images/qrz.png", UriKind.Relative));
        BitmapImage qrz_off_path = new BitmapImage(new Uri("Images/qrz_off.png", UriKind.Relative));
        BitmapImage lock_path = new BitmapImage(new Uri("Images/lock.png", UriKind.Relative));
        BitmapImage unlock_path = new BitmapImage(new Uri("Images/unlock.png", UriKind.Relative));

        List<string> ImportFileQ = new List<string>();

        public static UdpClient Client;
        public static UdpClient N1MMClient;

        // Static compiled regex for N1MM+ UDP parsing (performance optimization)
        private static readonly Regex N1MMTxFreqRegex = new Regex(@"<TXFreq>(.*)?<", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex N1MMModeRegex = new Regex(@"<Mode>(.*)?<", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        string MachineName = "Default";

        public MainWindow()
        {
            MachineName = Environment.MachineName;
            LoadQrzPhotoWindowBoundsFromDisk();

            Qsos = new ObservableCollection<QSO>();
            rem = new EntityResolver();
            InitializeComponent();

            // Overlay that shows the 3-decimal display while the box is not focused. Positioned to
            // sit exactly over TB_Frequency's text (its margin + border + left padding), at the same
            // font size, so switching between display and edit causes no visible jump.
            TB_FrequencyDisplay = new TextBlock
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(222, 57, 0, 0),
                Width = 52,
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
            TB_DXCallsign.PreviewMouseLeftButtonDown += TB_DXCallsign_PreviewMouseLeftButtonDown;
            // Bindings populate after the constructor, so defer the first overlay refresh to Loaded
            // priority — by then the bound value is present and we can render its 3-decimal form.
            Dispatcher.BeginInvoke(new Action(UpdateFrequencyDisplay), System.Windows.Threading.DispatcherPriority.Loaded);
            Dispatcher.BeginInvoke(new Action(UpdateRigLabel), System.Windows.Threading.DispatcherPriority.Loaded);

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
                    HolyMessageBox.ShowWarning("Failed to open UDP port.", "UDP Client", this);
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
                    HolyMessageBox.ShowWarning("Failed to open N1MM+ UDP port.", "N1MM+ UDP Client", this);
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
            

            this.Title = title;
            UpdateTitleClock();

            NetworkFlagItem.Visibility = Properties.Settings.Default.ShowNetworkFlag ? Visibility.Visible : Visibility.Collapsed;
            UpdateShareIconVisibility();

            if (Properties.Settings.Default.UpdateSettings)
            {
                Properties.Settings.Default.Upgrade();
                Properties.Settings.Default.UpdateSettings = false;
                try 
                { 
                    Properties.Settings.Default.Save(); 
                } 
                catch (Exception ex) 
                { 
                    System.Diagnostics.Debug.WriteLine($"Failed to save settings after upgrade: {ex.Message}");
                }
            }

            NormalizeEnterKeyBehaviorSettings();

            if (Properties.Settings.Default.isAutoCheckUpdates && isNetworkAvailable)
            {
                NotifyVersionUpToDate = false;
                // Defer the update check until after the main window is initialized and shown so any dialogs are
                // owned by the main window rather than the splash window (prevents them from being closed with the splash).
                Dispatcher.BeginInvoke(new Action(() => UpdatesMenuItem_Click(null, null)), DispatcherPriority.ApplicationIdle);
            }
            this.Loaded += MainWindow_Loaded;
                Properties.Settings.Default.PropertyChanged += Settings_PropertyChanged;

            ManualModeMenuItem.Header = Properties.Settings.Default.isManualMode ? "Manual Mode - ON" : "Manual Mode - OFF";
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
                HolyMessageBox.ShowError(e.Message, "Database Error");
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

            // Initialize RST fields based on the selected mode
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
            UpdateLotwMenuCount();
            UpdateQrzMenuCount();
            LastQSO = Qsos.FirstOrDefault();
            ApplyDefaultLogSort();

            UpdateNumOfQSOs();
            TB_Frequency_TextChanged(null, null);
            // Log in to QRZ entirely on a background thread so NOTHING about the request — not even
            // the synchronous DNS/proxy resolution that GetResponseAsync does on the calling thread —
            // can stall the UI thread during startup. The key is stored when it arrives.
            if (isNetworkAvailable)
                _ = Task.Run(async () =>
                {
                    string key = await Helper.LoginToQRZAsync().ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(key)) _SessionKey = key;
                    // Reflect QRZ.com connectivity on the main-window icon: gray it if QRZ.com could
                    // not be reached / logged in, normal blue if the session was established.
                    SetQrzConnected(!string.IsNullOrEmpty(key));
                });
            else
                SetQrzConnected(false); // no network at startup -> QRZ.com is unreachable

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
                try { Properties.Settings.Default.Save(); } catch { }
            }
            else if (addQsoWithEnter && doNothing)
            {
                Properties.Settings.Default.DoNothing = false;
                try { Properties.Settings.Default.Save(); } catch { }
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

            // Perform QRZ lookup outside Dispatcher to avoid blocking UI and ensure proper exception handling
            string qrzName = string.Empty;
            string qrzGrid = string.Empty;
            if (string.IsNullOrWhiteSpace(qso.Name) && isNetworkAvailable)
            {
                try
                {
                    var result = await GetQrzForCall(qso.DXCall);
                    qrzName = result.Name;
                    qrzGrid = result.Grid;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"QRZ lookup failed for {qso.DXCall}: {ex.Message}");
                }
            }

            this.Dispatcher.Invoke(() =>
            {
                try
                {
                    bool isValid = false;
                    if (!string.IsNullOrWhiteSpace(qrzName))
                    {
                        qso.Name = qrzName;
                    }
                    if (!string.IsNullOrWhiteSpace(qrzGrid))
                    {
                        qso.DXLocator = qrzGrid;
                    }
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
                    HolyMessageBox.ShowError("Failed to save QSO: " + ex.Message, "Save Error", this);
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
                    Match match = N1MMTxFreqRegex.Match(data);
                    if (match.Success)
                    {
                        string freq_str = Regex.Split(data, @"<TXFreq>(.*)?<", RegexOptions.IgnoreCase)[1].Trim().ToUpper();
                        double freq = 0;
                        if (double.TryParse(freq_str,out freq))
                        {
                            TB_Frequency.Text = (freq / 100).ToString("F2");
                        }
                    }

                    match = N1MMModeRegex.Match(data);
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
                    HolyMessageBox.ShowError("Failed to save QSO: " + ex.Message, "Save Error", this);
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

        private async void NetworkChange_NetworkAvailabilityChanged(object sender, NetworkAvailabilityEventArgs e)
        {
            isNetworkAvailable = e.IsAvailable;
            // Update the bottom-bar network dot immediately and coordinate the QRZ icon with it: the
            // instant the dot goes red (no network) the QRZ icon drops to its disconnected "!" state,
            // without waiting for any QRZ round-trip.
            this.Dispatcher.Invoke(() =>
            {
                NetworkFlag.Fill = isNetworkAvailable ? new SolidColorBrush(Color.FromRgb(0x00, 0xFF, 0x00)) : new SolidColorBrush(Color.FromRgb(0xFF, 0x00, 0x00));
                UpdateShareIconVisibility();
                if (!isNetworkAvailable) SetQrzConnected(false);
            });

            // When the network is back, re-establish the QRZ session and light the icon if it works.
            if (isNetworkAvailable)
            {
                string key = await Helper.LoginToQRZAsync();
                _SessionKey = key;
                SetQrzConnected(!string.IsNullOrEmpty(key));
            }
        }

        // Tracks the last known QRZ.com connection state, so the QRZ icon's click can branch:
        // connected -> normal QRZ lookup; not connected -> open the QRZ Service options page.
        private bool _qrzConnected = true;

        // Callsigns that QRZ returned no data for — skip them on subsequent service runs this session.
        private readonly HashSet<string> _qrzNoData = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Reflects QRZ.com connectivity on the main-window QRZ icon: the normal blue globe
        // (Images/qrz.png) when we have a working QRZ session, or the grayed globe + red "!" badge
        // (Images/qrz_off.png) when there is no connection to QRZ.com. Safe to call from any thread.
        private void SetQrzConnected(bool connected)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => SetQrzConnected(connected)));
                return;
            }
            _qrzConnected = connected;
            QRZBtn.Source = connected ? qrz_on_path : qrz_off_path;
            QRZBtn.ToolTip = connected
                ? "Get Data from QRZ.com and open the callsign's QRZ.com page"
                : "No connection to QRZ.com — QRZ lookups are unavailable";
            // The red "!" badge appears over the icon only when there is no QRZ.com connection.
            QrzNoConnBadge.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
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
                // Show the map area - now controlled by MapAreaDisplayMode setting
                UpdateGraphicsBoxDisplay();
                UpdateClusterSpotsOnMap();
                // Cap how far the window can be narrowed so the map can shrink only down to a
                // square. Deferred to Loaded priority because the map can't be measured until the
                // layout pass has run.
                Dispatcher.BeginInvoke(new Action(EnforceMapSquareMinWidth), System.Windows.Threading.DispatcherPriority.Loaded);
            }
            else
            {
                // Hide all graphics options
                MapControl.Visibility = Visibility.Hidden;
                Img_CustomGraphics.Visibility = Visibility.Collapsed;
                Img_QRZGraphics.Visibility = Visibility.Collapsed;
                MapDisabledPanel.Visibility = Visibility.Visible;
                this.MinWidth = 800;
            }
        }

        private void UpdateGraphicsBoxDisplay()
        {
            if (!Properties.Settings.Default.IsShowAzimuthControl)
            {
                return; // Graphics box is hidden, nothing to update
            }

            int mode = Properties.Settings.Default.MapAreaDisplayMode;

            // Always hide MapDisabledPanel first (it has highest ZIndex)
            MapDisabledPanel.Visibility = Visibility.Collapsed;

            // Hide all content options
            MapControl.Visibility = Visibility.Hidden;
            CustomGraphicsBorder.Visibility = Visibility.Collapsed;
            QRZGraphicsBorder.Visibility = Visibility.Collapsed;
            CompassBorder.Visibility = Visibility.Collapsed;

            // Force UI update before showing new content
            this.UpdateLayout();

            switch (mode)
            {
                case -1: // None - show blank panel with background color
                    MapDisabledPanel.Visibility = Visibility.Visible;
                    break;
                case 0: // Map
                    MapControl.Visibility = Visibility.Visible;
                    // Force map to render immediately with current data
                    MapControl.InvalidateVisual();
                    MapControl.UpdateLayout();
                    UpdateClusterSpotsOnMap();
                    break;
                case 1: // Compass
                    CompassBorder.Visibility = Visibility.Visible;
                    UpdateCompassDisplay();
                    break;
                case 2: // QRZ Photo
                    QRZGraphicsBorder.Visibility = Visibility.Visible;
                    LoadCurrentQRZPhotoToGraphicsBox();
                    break;
                case 3: // Custom Image
                    CustomGraphicsBorder.Visibility = Visibility.Visible;
                    LoadCustomImageToGraphicsBox();
                    // Force custom image to render immediately
                    CustomGraphicsBorder.InvalidateVisual();
                    CustomGraphicsBorder.UpdateLayout();
                    break;
                default:
                    MapControl.Visibility = Visibility.Visible;
                    MapControl.InvalidateVisual();
                    MapControl.UpdateLayout();
                    UpdateClusterSpotsOnMap();
                    break;
            }
        }

        private async void LoadCurrentQRZPhotoToGraphicsBox()
        {
            string urlAtCall = currentQrzImageUrl;
            if (string.IsNullOrWhiteSpace(urlAtCall))
            {
                // No QRZ photo available - clear the image but background stays white
                Img_QRZGraphics.Source = null;
                return;
            }

            try
            {
                string normalized = urlAtCall.Trim();
                if (normalized.StartsWith("//"))
                {
                    normalized = "https:" + normalized;
                }

                // Download off the UI thread; decoding from memory afterwards is cheap. This keeps
                // the callsign box responsive instead of freezing for the whole photo download.
                byte[] data = await Helper.DownloadImageBytesAsync(normalized);

                // Discard if the photo was cleared or a newer callsign was looked up meanwhile.
                if (currentQrzImageUrl != urlAtCall) return;

                if (data == null || data.Length == 0)
                {
                    Img_QRZGraphics.Source = null;
                    return;
                }

                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = new MemoryStream(data);
                bitmap.EndInit();
                bitmap.Freeze();
                Img_QRZGraphics.Source = bitmap;
            }
            catch
            {
                // Failed to load image - clear but keep white background
                Img_QRZGraphics.Source = null;
            }
        }

        private void LoadCustomImageToGraphicsBox()
        {
            string imagePath = Properties.Settings.Default.CustomMapImagePath;
            if (!string.IsNullOrEmpty(imagePath) && File.Exists(imagePath))
            {
                try
                {
                    BitmapImage bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.UriSource = new Uri(imagePath, UriKind.Absolute);
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache; // Force fresh load
                    bitmap.EndInit();
                    bitmap.Freeze(); // Freeze to improve performance
                    Img_CustomGraphics.Source = bitmap;
                }
                catch (Exception ex)
                {
                    // Log error and clear image
                    System.Diagnostics.Debug.WriteLine($"Failed to load custom image: {ex.Message}");
                    Img_CustomGraphics.Source = null;
                }
            }
            else
            {
                Img_CustomGraphics.Source = null;
            }
        }

        private void UpdateCompassDisplay()
        {
            if (CompassBorder == null || CompassNeedleRotation == null || CompassAzimuthText == null)
                return;

            // Only update if compass is currently visible
            if (CompassBorder.Visibility != Visibility.Visible)
                return;

            // Update needle rotation
            CompassNeedleRotation.Angle = Azimuth;

            // Update azimuth text
            CompassAzimuthText.Text = $"AZ {Math.Round(Azimuth, 0)}°";
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
            MapControl.SpotHovered += OnMapSpotHovered;
            MapControl.SpotHoverEnded += OnMapSpotHoverEnded;
            ShowHomeMap();

            // Reflect the persisted suggestions on/off state on the Suggest (F4) toggle button.
            if (BtnSuggestToggle != null)
                BtnSuggestToggle.IsChecked = Properties.Settings.Default.CallsignSuggestionsEnabled;

            // Reflect the persisted Contest Mode state in its Tools-menu header (YES/NO).
            UpdateContestModeMenuHeader();

            // eQSL queue: show how many QSOs are waiting (only for callsigns the user added to the
            // eQSL table). Nothing is sent automatically here.
            UpdateEqslQueueIndicator();

            // QRZ Logbook: show pending count and silently retry any QSOs that could not be pushed
            // earlier (e.g. logged while offline).
            UpdateQrzMenuCount();
            _ = PumpQrzQueue();

            // Initialize RST fields based on the selected mode after window is fully loaded
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

        // Updates the big centered UTC clock in the menu row (HH:mm:ss UTC). The window title itself
        // no longer carries the clock. Honors the ShowTitleClock setting.
        private void UpdateTitleClock()
        {
            if (L_TitleClock == null) return;
            if (Properties.Settings.Default.ShowTitleClock)
            {
                L_TitleClock.Text = DateTime.UtcNow.Hour.ToString("D2") + ":" + DateTime.UtcNow.Minute.ToString("D2") + ":" + DateTime.UtcNow.Second.ToString("D2") + " UTC";
                L_TitleClock.Visibility = Visibility.Visible;
            }
            else
            {
                L_TitleClock.Visibility = Visibility.Collapsed;
            }
        }

        private void UTCTimer_Elapsed(object sender, EventArgs e)
        {
            this.Dispatcher.Invoke(() =>
            {
                UpdateTitleClock();
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
                Helper.SendHeartbeat(MachineName, TB_MyCallsign.Text.Trim(), TB_Operator.Text.Trim(), TB_Frequency.Text.Trim(), CB_Mode.Text.Trim(), Properties.Settings.Default.ShowOnTheAir); //1000->seconds 60->minute 5->minutes
            }

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
                UpdateLotwMenuCount();
                UpdateQrzMenuCount();

                // A deleted QSO is gone from the DB, so it drops out of the eQSL waiting list —
                // refresh the "!" badge / menu count (and any open queue window) right away.
                UpdateEqslQueueIndicator();

                // The deleted QSO may have been the last one; refresh LastQSO to the
                // current top of the log so the Spot button uses the correct QSO.
                LastQSO = Qsos.FirstOrDefault();

                // Rebuild worked countries list after deletion
                RebuildWorkedCountriesAndRefreshCluster();
            }
            else if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                // The whole log table was cleared/replaced (e.g. "Clean log" or
                // Remove Duplicates). Rebuild the worked-countries cache so the
                // cluster spot colors (needed country = red) refresh immediately.
                LastQSO = Qsos.FirstOrDefault();
                RebuildWorkedCountriesAndRefreshCluster();
            }
            else if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems != null)
            {
                // Keep the worked-countries cache in sync when QSOs are added in
                // bulk (e.g. re-add after Remove Duplicates). Uses the cheap
                // incremental update so normal single-QSO adds stay fast.
                foreach (QSO qso in e.NewItems)
                {
                    AddWorkedCountryAndRefreshCluster(qso.DXCall);
                }
                UpdateLotwMenuCount();
                UpdateQrzMenuCount();
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
                    lock (_syncLock)
                    {
                        LastQSO = dal.Insert(qso);
                        Qsos.Insert(0, LastQSO);
                        Properties.Settings.Default.RecentQSOCounter++;
                    }

                    // dal.Insert returns a fresh object (LastQSO) carrying the new database Id; copy it
                    // back onto qso so the eQSL auto-upload can mark THIS row as sent (without it,
                    // SetEqslStatus would target Id 0 and the QSO would stay "pending" forever).
                    if (LastQSO != null) qso.id = LastQSO.id;

                    if (QSODataGrid.Items != null && QSODataGrid.Items.Count > 0)
                        QSODataGrid.ScrollIntoView(QSODataGrid.Items[0]);

                    AddWorkedCountryAndRefreshCluster(qso.DXCall);

                    // Auto-upload THIS QSO to the eQSL account of the callsign it was logged under.
                    // If it WILL be auto-uploaded (auto-upload on + callsign in the table + user name
                    // and password present), don't show the "!" now — let SendOneQsoToEqsl update the
                    // badge AFTER the attempt, so a successful upload never flashes a "!". Otherwise
                    // (manual mode, no credentials, or a callsign not set up to send) update the badge
                    // now so the QSO is shown as queued.
                    EqslAccount eqslAcct = dal.GetEqslAccount(qso.MyCall);
                    bool willAutoUpload = Properties.Settings.Default.EqslAutoUpload
                                          && eqslAcct != null
                                          && !string.IsNullOrWhiteSpace(eqslAcct.Username)
                                          && !string.IsNullOrWhiteSpace(eqslAcct.Password);
                    if (!willAutoUpload)
                        UpdateEqslQueueIndicator();
                    _ = SendOneQsoToEqsl(qso);

                    // Real-time push of THIS just-logged QSO to the QRZ.com online logbook (fire and
                    // forget). Does nothing unless the feature is enabled and an API key is configured;
                    // a failed/offline push simply leaves the QSO pending for a later silent retry.
                    _ = SendOneQsoToQrz(qso);
                }
                catch (Exception ex)
                {
                    HolyMessageBox.ShowError("Failed to save QSO: " + ex.Message, "Save Error", this);
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
            // When there is no QRZ.com connection (the icon shows the red "!" badge), the icon acts as
            // a shortcut to the QRZ Service options page so the user can fix their QRZ login.
            if (!_qrzConnected)
            {
                OpenQrzServiceOptions();
                return;
            }

            if (!string.IsNullOrWhiteSpace(TB_DXCallsign.Text))
            {
                GetQrzData();
                // Also open the QRZ.com web page for this callsign (in the default browser).
                OpenQrzPage(TB_DXCallsign.Text);
            }
        }

        // Opens (or focuses) the Options window and jumps straight to the QRZ Service page.
        private void OpenQrzServiceOptions()
        {
            OptionsMenuItemMenuItem_Click(null, null);
            if (options != null)
                options.QRZItem.IsSelected = true;
        }

        private void ClearBtn_Click(object sender, RoutedEventArgs e)
        {
            //TB_Frequency.Text = string.Empty;
            // Drop any stuck map-hover blue highlight on the cluster rows.
            SetClusterRowMapHighlight(null);
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
            // Don't reset the map to the home view while it is showing cluster spots — clearing the
            // QSO entry fields (F9) must not wipe the spotted stations from the cluster map.
            if (MapControl == null || !MapControl.IsClusterMode)
                ShowHomeMap();
            RestoreDataContext();
        }

        private void RestoreDataContext()
        {
            if (Properties.Settings.Default.IsFilterQSOs)
            {
                FilteredQsos = null;
                DataContext = Qsos;
            }
        }

        // Default log ordering on load: newest QSO first (Date desc, then Time desc) so the operator
        // immediately sees the last QSO he made at the top, with the sort arrow marking the Date column.
        private void ApplyDefaultLogSort()
        {
            if (Qsos == null)
                return;

            var view = System.Windows.Data.CollectionViewSource.GetDefaultView(Qsos);
            if (view != null)
            {
                using (view.DeferRefresh())
                {
                    view.SortDescriptions.Clear();
                    view.SortDescriptions.Add(new System.ComponentModel.SortDescription("Date", System.ComponentModel.ListSortDirection.Descending));
                    view.SortDescriptions.Add(new System.ComponentModel.SortDescription("Time", System.ComponentModel.ListSortDirection.Descending));
                }
            }

            // Mark the Date column as the active descending sort (the primary key), then paint the arrows.
            if (QSODataGrid != null)
            {
                foreach (var col in QSODataGrid.Columns)
                {
                    if (col.SortMemberPath == "Date")
                        col.SortDirection = System.ComponentModel.ListSortDirection.Descending;
                    else
                        col.SortDirection = null;
                }
                UpdateSortArrows();
            }
        }

        // The DataGridColumnHeader.SortDirection that a header ControlTemplate's triggers read does not
        // sync reliably in this app, so the arrow glyph is painted directly into the header text instead,
        // driven by the column's own SortDirection (which IS set correctly when sorting).
        private void QSODataGrid_Sorting(object sender, DataGridSortingEventArgs e)
        {
            // Let WPF perform the sort first; it updates the column's SortDirection afterwards.
            Dispatcher.BeginInvoke(new Action(UpdateSortArrows), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void QSODataGrid_LoadingRow(object sender, DataGridRowEventArgs e)
        {
            bool isAlternate = e.Row.GetIndex() % 2 != 0;
            bool isLastQso = LastQSO != null && e.Row.Item == LastQSO;

            if (FilteredQsos != null && !isLastQso)
            {
                // Filter active: green rows for matching QSOs.
                e.Row.Background = isAlternate
                    ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xA8, 0xD8, 0xB4))
                    : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xC8, 0xF0, 0xD0));
            }
            else
            {
                // Normal state (or pinned last-QSO row): standard white/gainsboro alternation.
                e.Row.Background = isAlternate
                    ? System.Windows.Media.Brushes.Gainsboro
                    : System.Windows.Media.Brushes.White;
            }
        }

        private void UpdateSortArrows()
        {
            if (QSODataGrid == null)
                return;

            foreach (var col in QSODataGrid.Columns)
            {
                string baseHeader = GetBaseColumnHeader(col);
                if (string.IsNullOrEmpty(baseHeader))
                    continue;

                if (col.SortDirection == System.ComponentModel.ListSortDirection.Ascending)
                    col.Header = baseHeader + "  ▲";   // ▲
                else if (col.SortDirection == System.ComponentModel.ListSortDirection.Descending)
                    col.Header = baseHeader + "  ▼";   // ▼
                else
                    col.Header = baseHeader;
            }
        }

        // Returns the column header text without any sort-arrow suffix.
        private string GetBaseColumnHeader(DataGridColumn col)
        {
            string header = col.Header as string;
            if (string.IsNullOrEmpty(header))
                return header;

            int idx = header.IndexOfAny(new[] { '▲', '▼' });
            if (idx >= 0)
                header = header.Substring(0, idx).TrimEnd();
            return header;
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && Properties.Settings.Default.AddQSOWithEnter)
            {
                AddBtn_Click(null, null);
                return;
            }

            if (HandleGlobalFunctionKey(e.Key, e.IsRepeat))
            {
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

        private void MessageButton_PreviewLeftDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            // With no radio to send to, swallow the left-press so the button doesn't animate or fire
            // (it would do nothing anyway), making its inactive state obvious. Right-click — the CW text
            // editor — uses a separate event and still works.
            if (!_messageSendAvailable)
                e.Handled = true;
        }

        private void MessageButton_RightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is Button button && int.TryParse(button.Tag?.ToString(), out int messageNumber))
            {
                e.Handled = true;
                // Right-click edits the CW text only while the buttons are in their CW ("Txt") look. In
                // voice ("Msg") mode the buttons play radio audio files, so there's no CW text to edit.
                if (IsCwModeActive())
                    ShowCwMessageEditDialog(messageNumber);
            }
        }

        private void ShowCwMessageEditDialog(int messageNumber)
        {
            string currentText = GetCwMessageText(messageNumber);

            Window dialog = new Window
            {
                Title = "Edit CW Text " + messageNumber + " (F" + (messageNumber + 4) + ")",
                Width = 360,
                Height = 130,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false,
                Owner = this,
                Icon = Icon
            };

            Grid grid = new Grid { Margin = new Thickness(10) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            TextBox tb = new TextBox
            {
                Text = currentText,
                FontSize = 14,
                Height = 28,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(4, 0, 4, 0),
                CharacterCasing = CharacterCasing.Upper,
                MaxLength = 120
            };

            // Add validation for CW-valid characters only
            tb.PreviewTextInput += (s, e) =>
            {
                // Valid CW characters: A-Z, 0-9, space, . , ? / @ = + -
                // Compare case-insensitively so lowercase typing (Caps Lock off) is accepted;
                // CharacterCasing.Upper still displays the letters as capitals.
                string validChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 .,?/@=+-";
                if (!validChars.Contains(e.Text.ToUpperInvariant()))
                {
                    e.Handled = true;  // Block invalid character
                }
            };

            Grid.SetRow(tb, 0);
            Grid.SetColumnSpan(tb, 3);
            grid.Children.Add(tb);

            Button btnSave = new Button
            {
                Content = "Save",
                Width = 70,
                Height = 28,
                IsDefault = true
            };
            Grid.SetRow(btnSave, 2);
            Grid.SetColumn(btnSave, 2);
            grid.Children.Add(btnSave);

            Button btnCancel = new Button
            {
                Content = "Cancel",
                Width = 70,
                Height = 28,
                IsCancel = true
            };
            Grid.SetRow(btnCancel, 2);
            Grid.SetColumn(btnCancel, 0);
            grid.Children.Add(btnCancel);

            dialog.Content = grid;

            btnSave.Click += (s, e) =>
            {
                SetCwMessageText(messageNumber, tb.Text.Trim());
                UpdateMessageButtonLabel(GetMessageButton(messageNumber), messageNumber, isCw: true);
                dialog.DialogResult = true;
            };
            btnCancel.Click += (s, e) => { dialog.DialogResult = false; };

            tb.SelectAll();
            tb.Focus();
            dialog.ShowDialog();
        }

        private string GetCwMessageText(int messageNumber)
        {
            switch (messageNumber)
            {
                case 1: return Properties.Settings.Default.CwMsgText1 ?? string.Empty;
                case 2: return Properties.Settings.Default.CwMsgText2 ?? string.Empty;
                case 3: return Properties.Settings.Default.CwMsgText3 ?? string.Empty;
                case 4: return Properties.Settings.Default.CwMsgText4 ?? string.Empty;
                default: return string.Empty;
            }
        }

        private void SetCwMessageText(int messageNumber, string text)
        {
            switch (messageNumber)
            {
                case 1: Properties.Settings.Default.CwMsgText1 = text; break;
                case 2: Properties.Settings.Default.CwMsgText2 = text; break;
                case 3: Properties.Settings.Default.CwMsgText3 = text; break;
                case 4: Properties.Settings.Default.CwMsgText4 = text; break;
            }

            try { Properties.Settings.Default.Save(); } catch { }
        }

        private Button GetMessageButton(int messageNumber)
        {
            switch (messageNumber)
            {
                case 1: return Btn_Msg1;
                case 2: return Btn_Msg2;
                case 3: return Btn_Msg3;
                case 4: return Btn_Msg4;
                default: return null;
            }
        }

        private void TriggerCwTextMessage(int messageNumber)
        {
            string rigType = NormalizeRigType(Rig != null ? Rig.RigType : null);

            if (!Properties.Settings.Default.EnableOmniRigCAT || OmniRigEngine == null || Rig == null)
            {
                HolyMessageBox.ShowWarning("OmniRig CAT is not available.", "CW Text", this);
                return;
            }

            if (Rig.Status != OmniRig.RigStatusX.ST_ONLINE)
            {
                HolyMessageBox.ShowWarning("The radio is offline.", "CW Text", this);
                return;
            }

            // Toggle/stop: if a CW message is already being sent, a second press aborts it
            // (same pattern as SSB voice messages, using the radio-specific CW stop command).
            int? currentMessageNumber = activeVoiceMessageNumber ?? pendingVoiceMessageNumber;

            if (currentMessageNumber.HasValue)
            {
                string stopCommand = BuildCwStopCommand(rigType);

                if (!string.IsNullOrWhiteSpace(stopCommand) && !TrySendOmniRigCustomCommand(stopCommand))
                {
                    HolyMessageBox.ShowWarning("Failed to send the CW stop CAT command to " + rigType + ".", "CW Text", this);
                    return;
                }

                ClearVoiceMessageState();

                if (currentMessageNumber.Value == messageNumber)
                {
                    return;
                }
            }

            string cwText = GetCwMessageText(messageNumber);

            if (string.IsNullOrWhiteSpace(cwText))
            {
                HolyMessageBox.ShowWarning("CW text " + messageNumber + " is empty. Right-click the button to edit it.", "CW Text", this);
                return;
            }

            string command = BuildCwSendCommand(rigType, cwText);

            if (command == null)
            {
                HolyMessageBox.ShowWarning("CW text keying via CAT is not supported for this radio model (" + rigType + ").", "CW Text", this);
                return;
            }

            if (!TrySendOmniRigCustomCommand(command))
            {
                HolyMessageBox.ShowWarning("Failed to send CW text CAT command to " + rigType + ".", "CW Text", this);
                return;
            }

            pendingVoiceMessageNumber = messageNumber;
            activeVoiceMessageNumber = null;
            pendingVoiceMessageDeadlineUtc = DateTime.UtcNow.AddSeconds(30);

            ShowCwSendMonitor(cwText);
        }

        // Opens (or replaces) the CW sending monitor window for the given text. The cursor does not
        // start moving until the radio actually keys up (UpdateVoiceMessageState detects TX on);
        // this keeps the visual aligned with the real start of transmission regardless of CAT latency.
        private void ShowCwSendMonitor(string cwText)
        {
            CloseCwSendMonitor(false);

            if (string.IsNullOrWhiteSpace(cwText))
            {
                return;
            }

            try
            {
                cwMonitorTotalUnits = CwSendMonitorWindow.ComputeTotalUnits(cwText);
                cwMonitorCursorStarted = false;

                cwSendMonitor = new CwSendMonitorWindow(cwText, cwLearnedWpm, "CW Sending");
                cwSendMonitor.Owner = this;
                cwSendMonitor.Closed += (s, e) =>
                {
                    if (ReferenceEquals(s, cwSendMonitor))
                    {
                        cwSendMonitor = null;
                    }
                };
                cwSendMonitor.Show();
            }
            catch
            {
                cwSendMonitor = null;
            }
        }

        // Called when the radio reports it has actually started transmitting. Starts the cursor and
        // records the real start time so we can learn the radio's true WPM when TX ends.
        private void OnCwTransmitStarted()
        {
            cwMonitorStartUtc = DateTime.UtcNow;

            if (cwSendMonitor != null && !cwMonitorCursorStarted)
            {
                cwMonitorCursorStarted = true;
                cwSendMonitor.UpdateWpm(cwLearnedWpm);
                cwSendMonitor.StartCursor();
            }
        }

        // Called when the radio reports it has returned to receive after keying our message.
        // Self-calibration: real elapsed TX seconds / PARIS units gives the unit duration, from which
        // we derive the radio's actual WPM (units/sec * 1.2). This refines the cursor speed used for
        // the next message, so changing the radio's keyer speed is automatically tracked.
        private void OnCwTransmitEnded()
        {
            if (cwMonitorCursorStarted && cwMonitorTotalUnits > 0)
            {
                double elapsed = (DateTime.UtcNow - cwMonitorStartUtc).TotalSeconds;
                if (elapsed > 0.2)
                {
                    double unitSeconds = elapsed / cwMonitorTotalUnits;
                    double measuredWpm = 1.2 / unitSeconds;
                    if (measuredWpm >= 5 && measuredWpm <= 80)
                    {
                        // Light smoothing so a single odd reading doesn't swing the estimate.
                        cwLearnedWpm = (cwLearnedWpm * 0.4) + (measuredWpm * 0.6);
                    }
                }
            }

            CloseCwSendMonitor(true);
        }

        // Closes the CW monitor. completed=true flashes the "done" state briefly and auto-closes;
        // completed=false freezes the cursor (used when the transmission was aborted).
        private void CloseCwSendMonitor(bool completed)
        {
            var monitor = cwSendMonitor;
            cwSendMonitor = null;
            cwMonitorCursorStarted = false;

            if (monitor == null)
            {
                return;
            }

            try
            {
                if (completed)
                {
                    monitor.Complete();
                }
                else
                {
                    monitor.Freeze();
                }
            }
            catch
            {
                try { monitor.Close(); } catch { }
            }
        }

        private static string BuildCwSendCommand(string rigType, string text)
        {
            // Yaesu: KY text; (max ~28 chars per command, space-pad to 28)
            bool isYaesu = rigType.StartsWith("FT", StringComparison.OrdinalIgnoreCase)
                        || rigType.StartsWith("FTDX", StringComparison.OrdinalIgnoreCase);
            // Elecraft K3
            bool isElecraft = rigType.StartsWith("K3", StringComparison.OrdinalIgnoreCase);
            // Kenwood (if added later)
            bool isKenwood = rigType.StartsWith("TS", StringComparison.OrdinalIgnoreCase);

            if (isYaesu || isElecraft || isKenwood)
            {
                string safe = new string(text.ToUpper().Where(c => c >= ' ' && c <= 'Z').ToArray());
                if (safe.Length > 28) safe = safe.Substring(0, 28);
                safe = safe.PadRight(28);
                return "KY " + safe + ";";
            }

            // Icom CI-V: FE FE <addr> E0 17 00 <ASCII bytes as hex> FD
            string icomAddress = GetIcomCivAddress(rigType);
            if (icomAddress != null)
            {
                // Keep only printable ASCII (space–Z range is safe for CW keyer)
                string safe = new string(text.ToUpper().Where(c => c >= ' ' && c <= 'Z').ToArray());
                if (string.IsNullOrEmpty(safe)) return null;
                string textHex = string.Join(" ", safe.Select(c => ((byte)c).ToString("X2")));
                return "FE FE " + icomAddress + " E0 17 00 " + textHex + " FD";
            }

            return null;
        }

        // Builds the CAT command that aborts an in-progress CW transmission.
        // Icom CI-V: command 17 with data byte FF stops CW sending (FE FE <addr> E0 17 FF FD).
        // Returns null for radios where a verified CW-abort command is not available.
        private static string BuildCwStopCommand(string rigType)
        {
            string icomAddress = GetIcomCivAddress(rigType);
            if (icomAddress != null)
            {
                return "FE FE " + icomAddress + " E0 17 FF FD";
            }

            return null;
        }

        private static readonly Dictionary<string, string> IcomCivAddresses = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "IC-7300",    "94" },
            { "IC-7300MK2", "B6" },
            { "IC-7610",    "98" },
        };

        private static string GetIcomCivAddress(string rigType)
        {
            string key = IcomCivAddresses.Keys.FirstOrDefault(k => string.Equals(k, rigType, StringComparison.OrdinalIgnoreCase));
            return key != null ? IcomCivAddresses[key] : null;
        }

        private void UpdateMessageButtonLabels()
        {
            bool isCw = IsCwModeActive();
            UpdateMessageButtonLabel(Btn_Msg1, 1, isCw);
            UpdateMessageButtonLabel(Btn_Msg2, 2, isCw);
            UpdateMessageButtonLabel(Btn_Msg3, 3, isCw);
            UpdateMessageButtonLabel(Btn_Msg4, 4, isCw);
        }

        private void UpdateMessageButtonLabel(Button button, int messageNumber, bool isCw)
        {
            if (button == null) return;

            var panel = button.Content as StackPanel;
            if (panel == null || panel.Children.Count < 1) return;

            if (panel.Children[0] is TextBlock labelBlock)
            {
                labelBlock.Text = isCw ? "Txt " + messageNumber : "Msg" + messageNumber;
                labelBlock.Foreground = System.Windows.Media.Brushes.Black;
            }

            // Swap the entire style so hover/press colours are also correct
            Style cwStyle  = (Style)FindResource("MsgButtonCwStyle");
            Style ssbStyle = (Style)FindResource("MsgButtonStyle");
            button.Style = isCw ? cwStyle : ssbStyle;
        }

        private bool IsCwModeActive()
        {
            string mode = null;
            // Trust the radio's reported mode ONLY when it's actually online. A disconnected/off radio
            // (Rig may still be non-null) reports a meaningless default mode, so in that case fall back
            // to the mode chosen in the UI Mode dropdown.
            bool rigOnline = Properties.Settings.Default.EnableOmniRigCAT
                             && OmniRigEngine != null && Rig != null
                             && Rig.Status == OmniRig.RigStatusX.ST_ONLINE;
            if (rigOnline)
                mode = GetNormalizedRigMode();

            if (string.IsNullOrEmpty(mode))
            {
                if (CB_Mode != null && CB_Mode.SelectedItem is ComboBoxItem item)
                    mode = item.Content as string;
                else if (CB_Mode != null)
                    mode = CB_Mode.Text;
            }
            return string.Equals((mode ?? string.Empty).Trim(), "CW", StringComparison.OrdinalIgnoreCase);
        }

        private void SpotButton_Click(object sender, RoutedEventArgs e)
        {
            Window dialog = BuildSpotDialog();
            dialog.Owner = this;
            dialog.ShowDialog();
        }

        // Right-click on a log row: select that row and show a context menu of actions.
        private void QSODataGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            var row = FindVisualParent<DataGridRow>(e.OriginalSource as DependencyObject);
            if (row == null)
            {
                // Not on a data row (header / empty area) — suppress any menu.
                e.Handled = true;
                return;
            }

            row.IsSelected = true;
            QSODataGrid.SelectedItem = row.Item;

            QSO qso = row.Item as QSO;
            if (qso == null)
            {
                e.Handled = true;
                return;
            }

            // Build a fresh menu bound to the right-clicked QSO and let WPF open it on mouse-up.
            QSODataGrid.ContextMenu = BuildQsoRowContextMenu(qso);
        }

        // Parsed-once styles for the log-row context menu (rounded card, hover highlights, icons).
        private ResourceDictionary _qsoCtxMenuResources;

        private ResourceDictionary QsoCtxMenuResources
        {
            get
            {
                if (_qsoCtxMenuResources == null)
                {
                    const string xaml =
@"<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                     xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
  <Style x:Key='CtxMenu' TargetType='ContextMenu'>
    <Setter Property='Background' Value='Transparent'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='ContextMenu'>
          <Border Background='#FFFFFF' BorderBrush='#1565C0' BorderThickness='1.5' CornerRadius='10' Padding='6' SnapsToDevicePixels='True'>
            <Border.Effect>
              <DropShadowEffect BlurRadius='14' ShadowDepth='2' Opacity='0.35' Color='#666666'/>
            </Border.Effect>
            <StackPanel IsItemsHost='True' KeyboardNavigation.DirectionalNavigation='Cycle'/>
          </Border>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <ControlTemplate x:Key='CtxItemTemplate' TargetType='MenuItem'>
    <Border x:Name='bd' Background='Transparent' CornerRadius='6' Padding='{TemplateBinding Padding}'>
      <Grid>
        <Grid.ColumnDefinitions>
          <ColumnDefinition Width='24'/>
          <ColumnDefinition Width='*'/>
        </Grid.ColumnDefinitions>
        <ContentPresenter Grid.Column='0' ContentSource='Icon' VerticalAlignment='Center' HorizontalAlignment='Center'/>
        <ContentPresenter Grid.Column='1' ContentSource='Header' VerticalAlignment='Center' Margin='8,0,0,0'/>
      </Grid>
    </Border>
    <ControlTemplate.Triggers>
      <Trigger Property='IsHighlighted' Value='True'>
        <Setter TargetName='bd' Property='Background' Value='#1565C0'/>
        <Setter Property='Foreground' Value='White'/>
      </Trigger>
      <Trigger Property='IsEnabled' Value='False'>
        <Setter Property='Foreground' Value='#AAAAAA'/>
      </Trigger>
    </ControlTemplate.Triggers>
  </ControlTemplate>

  <ControlTemplate x:Key='CtxItemDangerTemplate' TargetType='MenuItem'>
    <Border x:Name='bd' Background='Transparent' CornerRadius='6' Padding='{TemplateBinding Padding}'>
      <Grid>
        <Grid.ColumnDefinitions>
          <ColumnDefinition Width='24'/>
          <ColumnDefinition Width='*'/>
        </Grid.ColumnDefinitions>
        <ContentPresenter Grid.Column='0' ContentSource='Icon' VerticalAlignment='Center' HorizontalAlignment='Center'/>
        <ContentPresenter Grid.Column='1' ContentSource='Header' VerticalAlignment='Center' Margin='8,0,0,0'/>
      </Grid>
    </Border>
    <ControlTemplate.Triggers>
      <Trigger Property='IsHighlighted' Value='True'>
        <Setter TargetName='bd' Property='Background' Value='#D32F2F'/>
        <Setter Property='Foreground' Value='White'/>
      </Trigger>
    </ControlTemplate.Triggers>
  </ControlTemplate>

  <Style x:Key='CtxItem' TargetType='MenuItem'>
    <Setter Property='FontSize' Value='15'/>
    <Setter Property='Foreground' Value='#1A1A1A'/>
    <Setter Property='Padding' Value='12,7'/>
    <Setter Property='Margin' Value='2,1'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='Template' Value='{StaticResource CtxItemTemplate}'/>
  </Style>

  <Style x:Key='CtxItemDanger' TargetType='MenuItem' BasedOn='{StaticResource CtxItem}'>
    <Setter Property='Foreground' Value='#C62828'/>
    <Setter Property='Template' Value='{StaticResource CtxItemDangerTemplate}'/>
  </Style>

  <Style x:Key='CtxSep' TargetType='Separator'>
    <Setter Property='Margin' Value='8,5'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='Separator'>
          <Border Height='1' Background='#BDBDBD' SnapsToDevicePixels='True'/>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
</ResourceDictionary>";
                    _qsoCtxMenuResources = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(xaml);
                }
                return _qsoCtxMenuResources;
            }
        }

        private static TextBlock MakeMenuGlyph(string glyph, Brush color)
        {
            return new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 15,
                Foreground = color,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
        }

        private ContextMenu BuildQsoRowContextMenu(QSO qso)
        {
            var res = QsoCtxMenuResources;
            var itemStyle = (Style)res["CtxItem"];
            var dangerStyle = (Style)res["CtxItemDanger"];
            var sepStyle = (Style)res["CtxSep"];
            var blue = (Brush)new SolidColorBrush(Color.FromRgb(0x15, 0x65, 0xC0));
            var red = (Brush)new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28));

            var menu = new ContextMenu { Style = (Style)res["CtxMenu"] };

            var spotItem = new MenuItem { Header = "Spot", Style = itemStyle, Icon = MakeMenuGlyph("", blue) };
            spotItem.Click += (s, e) =>
            {
                Window dialog = BuildSpotDialog(qso.DXCall, qso.Freq);
                dialog.Owner = this;
                dialog.ShowDialog();
            };
            menu.Items.Add(spotItem);

            var setFreqItem = new MenuItem { Header = "Set Radio to Freq", Style = itemStyle, Icon = MakeMenuGlyph("", blue) };
            setFreqItem.Click += (s, e) => SetRadioToQsoFreq(qso);
            menu.Items.Add(setFreqItem);

            var qrzItem = new MenuItem { Header = "Open QRZ Page", Style = itemStyle, Icon = MakeMenuGlyph("", blue) };
            qrzItem.Click += (s, e) => OpenQrzPage(qso.DXCall);
            menu.Items.Add(qrzItem);

            var searchItem = new MenuItem { Header = "Search", Style = itemStyle, Icon = MakeMenuGlyph("", blue) };
            searchItem.Click += (s, e) => OpenSearchWindow(qso.DXCall);
            menu.Items.Add(searchItem);

            var copyItem = new MenuItem { Header = "Copy QSO Info", Style = itemStyle, Icon = MakeMenuGlyph("", blue) };
            copyItem.Click += (s, e) =>
            {
                try { Clipboard.SetText(BuildQsoClipboardText(qso)); } catch { }
            };
            menu.Items.Add(copyItem);

            menu.Items.Add(new Separator { Style = sepStyle });

            var editItem = new MenuItem { Header = "Edit", Style = itemStyle, Icon = MakeMenuGlyph("", blue) };
            editItem.Click += (s, e) => EditQsoFromContextMenu(qso);
            menu.Items.Add(editItem);

            var deleteItem = new MenuItem { Header = "Delete", Style = dangerStyle, Icon = MakeMenuGlyph("", red) };
            deleteItem.Click += (s, e) => DeleteQsoFromContextMenu(qso);
            menu.Items.Add(deleteItem);

            return menu;
        }

        private void OpenQrzPage(string callsign)
        {
            string call = (callsign ?? string.Empty).Trim().ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(call))
                return;
            try { Process.Start("https://www.qrz.com/db/" + call); } catch { }
        }

        // Shared client for eQSL uploads (a single long-lived HttpClient avoids socket exhaustion).
        private static readonly System.Net.Http.HttpClient _eqslHttp =
            new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(25) };

        // Guarantees only one upload operation runs at a time, so the on-save auto-upload and the
        // manual "Send" pass can never double-send the same QSO.
        private readonly System.Threading.SemaphoreSlim _eqslPumpLock = new System.Threading.SemaphoreSlim(1, 1);

        // Builds the eQSL ImportADIF upload URL for a single QSO.
        private static string BuildEqslUrl(QSO qso, string user, string pwd, string nickname)
        {
            string adif = BuildEqslAdif(qso, nickname);
            return "https://www.eQSL.cc/qslcard/ImportADIF.cfm"
                + "?EQSL_USER=" + Uri.EscapeDataString(user)
                + "&EQSL_PSWD=" + Uri.EscapeDataString(pwd)
                + "&ADIFData=" + Uri.EscapeDataString(adif);
        }

        // Auto-uploads a single just-logged QSO to the eQSL account that belongs to the callsign it
        // was logged under. On success it is marked sent; if it can't be confirmed (offline / auth /
        // error) it stays pending and the "!" badge appears so the user can send it manually later.
        // Does nothing unless "Automatically upload each QSO" is on AND that callsign has an account
        // with credentials. The backlog is NEVER flushed here.
        private async System.Threading.Tasks.Task SendOneQsoToEqsl(QSO qso)
        {
            // This runs as fire-and-forget (_ = SendOneQsoToEqsl(...)). Any exception here (e.g. a DB
            // error from GetEqslAccount/SetEqslStatus) would otherwise be an unobserved task exception,
            // so the whole body is guarded. The QSO simply stays pending if anything goes wrong.
            try
            {
                if (qso == null || dal == null) return;
                if (!Properties.Settings.Default.EqslAutoUpload) return;

                EqslAccount acct = dal.GetEqslAccount(qso.MyCall);
                if (acct == null || string.IsNullOrWhiteSpace(acct.Username) || string.IsNullOrWhiteSpace(acct.Password))
                    return; // no eQSL account configured for this callsign -> leave it pending

                // If a send pass is already running, leave this QSO pending; it will be picked up later.
                if (!await _eqslPumpLock.WaitAsync(0)) return;
                try
                {
                    string url = BuildEqslUrl(qso, acct.Username, acct.Password, null);

                    int outcome;
                    try
                    {
                        string body = await _eqslHttp.GetStringAsync(url);
                        outcome = ClassifyEqslResponse(body);
                    }
                    catch
                    {
                        outcome = 0; // offline / timeout -> leave pending
                    }

                    if (outcome == 1) dal.SetEqslStatus(qso.id, 1);
                    else if (outcome == 2) dal.SetEqslStatus(qso.id, 2);
                    // outcome 0 -> leave pending

                    UpdateEqslQueueIndicator();
                }
                finally
                {
                    _eqslPumpLock.Release();
                }
            }
            catch
            {
                // Auto-upload must never crash the app; the QSO remains pending for a later retry.
            }
        }

        // Manually uploads every pending QSO that has a configured account, routing each to the eQSL
        // account of the callsign it was logged under. Marks each sent or rejected from eQSL's reply,
        // and leaves anything that can't be confirmed sent as pending so nothing is ever lost. Called
        // only from the queue window's "Send" button. Returns the number of QSOs successfully uploaded
        // in this pass. Must run on the UI thread (touches DB + UI).
        private async System.Threading.Tasks.Task<int> PumpEqslQueue(bool force = false, UploadProgressWindow progressWindow = null)
        {
            if (dal == null) return 0;

            // On a forced exit-upload, wait up to 30 s for any concurrent pump to finish rather
            // than silently skipping. For normal fire-and-forget calls, give up immediately.
            var timeout = force ? TimeSpan.FromSeconds(30) : TimeSpan.Zero;
            if (!await _eqslPumpLock.WaitAsync(timeout)) return 0;
            try
            {
                // Only QSOs whose callsign has an account come back here.
                System.Collections.Generic.List<QSO> pending = dal.GetPendingEqslQsos();
                int sentCount = 0;

                if (progressWindow != null)
                {
                    if (pending.Count > 0)
                        progressWindow.StartService("eQSL", pending.Count);
                    else
                        progressWindow.SkipService("eQSL", "nothing to upload — queue is empty");
                }

                // Load the accounts once into a callsign-keyed map (case-insensitive, matching the DB
                // NOCASE collation) instead of querying GetEqslAccount per QSO.
                var accounts = new System.Collections.Generic.Dictionary<string, EqslAccount>(StringComparer.OrdinalIgnoreCase);
                foreach (var a in dal.GetEqslAccounts())
                    if (!string.IsNullOrWhiteSpace(a.Callsign)) accounts[a.Callsign.Trim()] = a;

                foreach (var qso in pending)
                {
                    EqslAccount acct = null;
                    string myCall = (qso.MyCall ?? string.Empty).Trim();
                    if (myCall.Length > 0) accounts.TryGetValue(myCall, out acct);
                    if (acct == null || string.IsNullOrWhiteSpace(acct.Username) || string.IsNullOrWhiteSpace(acct.Password))
                        continue; // shouldn't happen (filtered), but skip defensively

                    string url = BuildEqslUrl(qso, acct.Username, acct.Password, null);
                    int outcome;
                    bool networkError = false;
                    try
                    {
                        // No ConfigureAwait(false): resume on the UI thread so DB/UI stay single-threaded.
                        string body = await _eqslHttp.GetStringAsync(url);
                        outcome = ClassifyEqslResponse(body);
                    }
                    catch
                    {
                        networkError = true; // offline / timeout
                        outcome = 0;
                    }

                    if (networkError)
                        break; // no internet -> stop; everything else stays pending for next time

                    if (outcome == 1)        // accepted by eQSL
                    {
                        dal.SetEqslStatus(qso.id, 1);
                        sentCount++;
                        progressWindow?.ReportQso(qso.DXCall, qso.Band, qso.Mode, true);
                    }
                    else if (outcome == 2)   // permanently rejected (bad record) - skip so it can't block the queue
                    {
                        dal.SetEqslStatus(qso.id, 2);
                        progressWindow?.ReportQso(qso.DXCall, qso.Band, qso.Mode, false);
                    }
                    // outcome 0 (unrecognized reply, e.g. one account's auth failed) -> leave this QSO
                    // pending and move on to the next, so one bad account can't block the others.

                    UpdateEqslQueueIndicator();
                }

                UpdateEqslQueueIndicator();
                return sentCount;
            }
            finally
            {
                _eqslPumpLock.Release();
            }
        }

        // Interprets eQSL's ImportADIF reply. Deliberately conservative: only an explicit success is
        // treated as "sent"; an explicit bad-record is treated as "rejected"; anything else (auth
        // failure, maintenance page, unrecognized text) leaves the QSO pending so it is never lost.
        // Returns 1 = sent, 2 = rejected, 0 = unknown (keep pending). May need tuning once we have a
        // real eQSL response sample to look at.
        private static int ClassifyEqslResponse(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return 0;
            string text = body.ToLowerInvariant();

            // eQSL reports e.g. "Result: 1 out of 1 records added".
            var m = System.Text.RegularExpressions.Regex.Match(text, @"(\d+)\s+out\s+of\s+(\d+)\s+record");
            if (m.Success)
            {
                int added = 0;
                int.TryParse(m.Groups[1].Value, out added);
                if (added >= 1) return 1;
                // 0 added: a duplicate already on eQSL counts as done; a real bad record is rejected.
                if (text.Contains("duplicate") || text.Contains("already")) return 1;
                if (text.Contains("bad record") || text.Contains("rejected") || text.Contains("error")) return 2;
                return 0;
            }

            if (text.Contains("bad record") || text.Contains("rejected")) return 2;
            return 0;
        }

        // Builds a one-record ADIF for eQSL by reusing the app's ADIF generator and (optionally)
        // injecting the QTH nickname tag so eQSL matches the upload to the right QTH profile.
        private static string BuildEqslAdif(QSO qso, string qthNickname)
        {
            string adif = Services.GenerateAdif(new System.Collections.Generic.List<QSO> { qso });
            if (!string.IsNullOrWhiteSpace(qthNickname))
            {
                string tag = string.Format("<app_eqsl_qth_nickname:{0}>{1}", qthNickname.Length, qthNickname);
                int idx = adif.LastIndexOf("<eor>", StringComparison.OrdinalIgnoreCase);
                if (idx >= 0) adif = adif.Insert(idx, tag);
            }
            return adif;
        }

        // ---- QRZ.com Logbook real-time upload --------------------------------------------------------

        // Serializes one QSO Plus terminates it with a single <EOR>. Reuses the app's canonical ADIF
        // generator and strips the file header (<adif_ver>...<eoh>) so only the record block remains,
        // which is what the QRZ Logbook API's ADIF parameter expects.
        private static string BuildQrzAdif(QSO qso)
        {
            string adif = Services.GenerateAdif(new System.Collections.Generic.List<QSO> { qso });
            int idx = adif.IndexOf("<eoh>", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0) adif = adif.Substring(idx + "<eoh>".Length);
            return adif.Trim();
        }

        // Only one QRZ upload runs at a time so the on-save push and the silent retry pass can never
        // double-send the same QSO.
        private readonly System.Threading.SemaphoreSlim _qrzPumpLock = new System.Threading.SemaphoreSlim(1, 1);

        // True when the QRZ Logbook real-time push is switched on and an API key is present.
        private static bool QrzPushEnabled
        {
            get
            {
                return Properties.Settings.Default.qrz_logbook_auto_push
                       && !string.IsNullOrWhiteSpace(Properties.Settings.Default.qrz_api_key);
            }
        }

        // Pushes a single just-logged QSO to the QRZ.com online logbook. On success the QSO is marked
        // uploaded and QRZ's LOGID transaction id is stored next to it. A definitive rejection from QRZ
        // (bad key / no subscription / bad record) marks it rejected so it is not retried forever; an
        // offline/timeout leaves it pending for the next silent retry. Never throws (fire and forget).
        private async System.Threading.Tasks.Task SendOneQsoToQrz(QSO qso)
        {
            try
            {
                if (qso == null || dal == null) return;
                if (!QrzPushEnabled) return;

                // Wait for any concurrent pump to finish (e.g. the startup retry pass).
                // Using a timeout instead of WaitAsync(0) so a just-saved QSO is not silently
                // skipped when the startup pump is still holding the lock.
                if (!await _qrzPumpLock.WaitAsync(TimeSpan.FromSeconds(30))) return;
                try
                {
                    string key = Properties.Settings.Default.qrz_api_key.Trim();
                    QrzLogbookResult r = await QrzLogbookService.InsertAsync(key, BuildQrzAdif(qso));

                    if (r.Ok)
                        dal.SetQrzStatus(qso.id, 1, r.LogId);
                    else if (r.IsPermanentFailure)
                        dal.SetQrzStatus(qso.id, 2, null);
                    // network error -> leave pending (status stays 0)
                }
                finally
                {
                    _qrzPumpLock.Release();
                }
                UpdateQrzMenuCount();
            }
            catch
            {
                // Auto-upload must never crash the app; the QSO remains pending for a later retry.
            }
        }

        // Silently uploads every QSO still pending for QRZ (status 0), oldest first. Runs at startup so
        // anything that could not be pushed while offline is retried automatically. Stops on the first
        // network error (everything else stays pending); a per-record rejection is marked and skipped so
        // one bad record can't block the queue. Never throws.
        // force=true bypasses the QrzPushEnabled guard — used by the on-exit upload when the user
        // explicitly confirmed the upload even though real-time auto-push is turned off.
        private async System.Threading.Tasks.Task PumpQrzQueue(bool force = false, UploadProgressWindow progressWindow = null)
        {
            try
            {
                if (dal == null) return;
                if (!force && !QrzPushEnabled) return;

                // When forced (exit-upload), wait up to 30 s for a concurrent pump to finish.
                // For regular fire-and-forget calls give up immediately so the caller is not blocked.
                var lockTimeout = force ? TimeSpan.FromSeconds(30) : TimeSpan.Zero;
                if (!await _qrzPumpLock.WaitAsync(lockTimeout)) return;
                try
                {
                    string key = Properties.Settings.Default.qrz_api_key.Trim();
                    System.Collections.Generic.List<QSO> pending = dal.GetPendingQrzQsos();

                    if (progressWindow != null)
                    {
                        if (pending.Count > 0)
                            progressWindow.StartService("QRZ Logbook", pending.Count);
                        else
                            progressWindow.SkipService("QRZ Logbook", "nothing to upload — queue is empty");
                    }

                    foreach (var qso in pending)
                    {
                        QrzLogbookResult r = await QrzLogbookService.InsertAsync(key, BuildQrzAdif(qso));

                        if (r.NetworkError)
                            break;   // offline -> stop; the rest stays pending for next time

                        if (r.Ok)
                        {
                            dal.SetQrzStatus(qso.id, 1, r.LogId);
                            progressWindow?.ReportQso(qso.DXCall, qso.Band, qso.Mode, true);
                        }
                        else if (r.IsPermanentFailure)
                        {
                            dal.SetQrzStatus(qso.id, 2, null);
                            progressWindow?.ReportQso(qso.DXCall, qso.Band, qso.Mode, false);
                        }
                    }
                }
                finally
                {
                    _qrzPumpLock.Release();
                }
                UpdateQrzMenuCount();
            }
            catch
            {
                // Best effort; anything not confirmed sent simply stays pending.
            }
        }

        // Refreshes everything that reflects the eQSL queue size: the "!" badge on the log grid and
        // the Tools-menu item (grayed when empty, with the count in its header). Safe to call often.
        private void UpdateEqslQueueIndicator()
        {
            int pending = 0;
            // Counts not-yet-sent QSOs whose callsign is in the eQSL table (the opt-in list). A
            // callsign that isn't in the table is ignored, so the "!" never shows for callsigns the
            // user chose not to upload.
            try { if (dal != null) pending = dal.GetPendingEqslCount(); }
            catch { pending = 0; }

            bool any = pending > 0;

            if (EqslQueueBadge != null)
            {
                EqslQueueBadge.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
                EqslQueueBadge.ToolTip = pending + (pending == 1 ? " QSO" : " QSOs") + " waiting for eQSL — click to review";
            }

            if (SendQueueToEqslMenuItem != null)
            {
                // Build the header with just the word "eQSL" in bold; always append the count
                // (including (0)) so the queue state is never ambiguous.
                var header = new System.Windows.Controls.TextBlock();
                header.Inlines.Add(new System.Windows.Documents.Run("Upload Queue to "));
                header.Inlines.Add(new System.Windows.Documents.Run("eQSL") { FontWeight = System.Windows.FontWeights.Bold });
                header.Inlines.Add(new System.Windows.Documents.Run("  (" + pending + ")"));
                SendQueueToEqslMenuItem.Header = header;
            }

            // The "!" badge is red when QSOs are waiting, gray when the queue is empty (a custom
            // colored Border doesn't dim on its own when the menu item is disabled).
            if (SendQueueBadgeIcon != null)
                SendQueueBadgeIcon.Background = new SolidColorBrush(
                    any ? (Color)ColorConverter.ConvertFromString("#D32F2F")
                        : (Color)ColorConverter.ConvertFromString("#9E9E9E"));

            // Keep an open queue window in sync too (e.g. a QSO was deleted from the log behind it).
            if (_eqslQueueWindow != null)
                _eqslQueueWindow.RefreshList();
        }

        // Recompute the queue size whenever the Tools menu is opened, so the menu item's gray state
        // and count are always current.
        private void ToolsMenu_SubmenuOpened(object sender, RoutedEventArgs e)
        {
            UpdateEqslQueueIndicator();
        }

        private void SendQueueToEqslMenuItem_Click(object sender, RoutedEventArgs e)
        {
            ShowEqslQueueWindow();
        }

        private void UpdateLotwMenuCount()
        {
            try
            {
                int count = dal?.GetPendingLotwQsos()?.Count ?? 0;
                var header = new System.Windows.Controls.TextBlock();
                header.Inlines.Add(new System.Windows.Documents.Run("Upload Queue to "));
                header.Inlines.Add(new System.Windows.Documents.Run("LoTW") { FontWeight = System.Windows.FontWeights.Bold });
                header.Inlines.Add(new System.Windows.Documents.Run("  (" + count + ")"));
                SendQueueToLotwMenuItem.Header = header;
            }
            catch { }
        }

        private void UpdateQrzMenuCount()
        {
            try
            {
                int count = dal?.GetPendingQrzCount() ?? 0;
                var header = new System.Windows.Controls.TextBlock();
                header.Inlines.Add(new System.Windows.Documents.Run("Upload Queue to "));
                header.Inlines.Add(new System.Windows.Documents.Run("QRZ") { FontWeight = System.Windows.FontWeights.Bold });
                header.Inlines.Add(new System.Windows.Documents.Run(" Logbook  (" + count + ")"));
                UploadQueueToQrzMenuItem.Header = header;
            }
            catch { }
        }

        private async void UploadQueueToQrzMenuItem_Click(object sender, RoutedEventArgs e)
        {
            string key = (Properties.Settings.Default.qrz_api_key ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                HolyMessageBox.ShowWarning(
                    "QRZ Logbook API key is not configured.\nPlease enter your API key in Options → QRZ Services.",
                    "QRZ Logbook", this);
                return;
            }

            int before = dal?.GetPendingQrzCount() ?? 0;
            if (before == 0)
            {
                HolyMessageBox.Show("The QRZ Logbook queue is empty. Nothing to upload.",
                    "QRZ Logbook", HolyMsgType.Info, this);
                return;
            }

            UploadQueueToQrzMenuItem.IsEnabled = false;
            try
            {
                await PumpQrzQueue();
                int after = dal?.GetPendingQrzCount() ?? 0;
                int uploaded = before - after;
                UpdateQrzMenuCount();

                if (uploaded > 0)
                    HolyMessageBox.ShowSuccess(
                        $"{uploaded} QSO{(uploaded == 1 ? "" : "s")} uploaded to QRZ Logbook successfully." +
                        (after > 0 ? $"\n{after} QSO{(after == 1 ? "" : "s")} could not be uploaded (network error or rejected)." : ""),
                        "QRZ Logbook", this);
                else
                    HolyMessageBox.ShowWarning(
                        "No QSOs were uploaded.\nCheck your internet connection and API key.",
                        "QRZ Logbook", this);
            }
            finally
            {
                UploadQueueToQrzMenuItem.IsEnabled = true;
            }
        }

        private void ClearLotwQueueContextMenuItem_Click(object sender, RoutedEventArgs e)
        {
            int pending = dal?.GetPendingLotwCount() ?? 0;
            if (pending == 0)
            {
                HolyMessageBox.Show("The LoTW queue is already empty.", "Clear LoTW Queue", HolyMsgType.Info, this);
                return;
            }

            bool confirmed = HolyMessageBox.ShowConfirm(
                $"Remove all {pending:N0} QSO(s) from the LoTW upload queue?\n\nThey will no longer be included in the next upload.",
                "Clear LoTW Queue", HolyMsgType.Warning, this);
            if (!confirmed) return;

            dal.ClearLotwQueue();
            UpdateLotwMenuCount();
        }

        private void ClearEqslQueueContextMenuItem_Click(object sender, RoutedEventArgs e)
        {
            int pending = dal?.GetPendingEqslCount() ?? 0;
            if (pending == 0)
            {
                HolyMessageBox.Show("The eQSL queue is already empty.", "Clear eQSL Queue", HolyMsgType.Info, this);
                return;
            }

            bool confirmed = HolyMessageBox.ShowConfirm(
                $"Remove all {pending:N0} QSO(s) from the eQSL upload queue?\n\nThey will no longer be included in the next upload.",
                "Clear eQSL Queue", HolyMsgType.Warning, this);
            if (!confirmed) return;

            dal.ClearEqslQueue();
            UpdateEqslQueueIndicator();
        }

        private void ClearQrzQueueContextMenuItem_Click(object sender, RoutedEventArgs e)
        {
            int pending = dal?.GetPendingQrzCount() ?? 0;
            if (pending == 0)
            {
                HolyMessageBox.Show("The QRZ Logbook queue is already empty.", "Clear QRZ Queue", HolyMsgType.Info, this);
                return;
            }

            bool confirmed = HolyMessageBox.ShowConfirm(
                $"Remove all {pending:N0} QSO(s) from the QRZ Logbook upload queue?\n\nThey will no longer be included in the next upload.",
                "Clear QRZ Queue", HolyMsgType.Warning, this);
            if (!confirmed) return;

            dal.ClearQrzQueue();
            UpdateQrzMenuCount();
        }

        private async void SendQueueToLotwMenuItem_Click(object sender, RoutedEventArgs e)
        {
            string tqslPath = Properties.Settings.Default.LotwTqslPath?.Trim();
            string location = Properties.Settings.Default.LotwStationLocation?.Trim();
            string password = Properties.Settings.Default.LotwTqslPassword;

            if (string.IsNullOrWhiteSpace(tqslPath) || !System.IO.File.Exists(tqslPath))
            {
                HolyMessageBox.ShowWarning("TQSL executable not found.\nPlease set the correct path in Options → LoTW Upload.", "LoTW Upload", this);
                return;
            }
            if (string.IsNullOrWhiteSpace(location))
            {
                HolyMessageBox.ShowWarning("Station location is not configured.\nPlease set it in Options → LoTW Upload.", "LoTW Upload", this);
                return;
            }

            var pending = dal.GetPendingLotwQsos();
            if (pending.Count == 0)
            {
                HolyMessageBox.Show("No pending QSOs to upload to LoTW.", "LoTW Upload", HolyMsgType.Info, this);
                return;
            }

            if (!HolyMessageBox.ShowConfirm($"Upload {pending.Count} pending QSO(s) to LoTW?\n\nStation location: {location}", "LoTW Upload", HolyMsgType.Warning, this))
                return;

            SendQueueToLotwMenuItem.IsEnabled = false;
            try { await UploadLotwQueueCoreAsync(pending, tqslPath, location, password); }
            finally { SendQueueToLotwMenuItem.IsEnabled = true; }
        }

        // Core LoTW queue upload — writes the ADIF, signs+uploads via TQSL, clears the queue on
        // success and reports the result. Shared by the "Upload Queue to LoTW" menu command and the
        // upload-on-exit feature.
        private async Task UploadLotwQueueCoreAsync(List<QSO> pending, string tqslPath, string location, string password, UploadProgressWindow progressWindow = null)
        {
            string adiPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "holylogger_lotw.adi");
            UploadProgressTitle = "LoTW Upload";
            UploadProgress = $"Preparing QSO 0 / {pending.Count:N0}";
            if (progressWindow == null) ToggleUploadProgress(Visibility.Visible);
            else progressWindow.StartService("LoTW", pending.Count);
            var lotwProgress = new Progress<string>(msg => UploadProgress = msg);
            try
            {
                int skippedNoBand = 0;
                await Task.Run(() => { skippedNoBand = LotwUploader.WriteAdif(pending, adiPath, lotwProgress); });
                int toSign = pending.Count - skippedNoBand;
                UploadProgress = $"Signing QSO 0 / {toSign:N0}";
                var result = await LotwUploader.SignAndUploadAsync(
                    tqslPath, location, password, adiPath, lotwProgress, toSign);
                if (progressWindow == null) ToggleUploadProgress(Visibility.Hidden);

                // Always save TQSL's full report so the actual outcome can be inspected.
                string reportPath = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    "lotw_last_upload.txt");
                try
                {
                    string detail = result.Detail ?? "";
                    if (detail.Length > 500000) detail = detail.Substring(0, 500000) + "\r\n…(truncated)";
                    System.IO.File.WriteAllText(reportPath,
                        $"LoTW upload report — {DateTime.Now:yyyy-MM-dd HH:mm:ss}\r\n" +
                        $"TQSL exit code: {result.ExitCode}\r\n" +
                        $"QSOs sent to TQSL: {toSign}\r\n\r\n" +
                        detail, System.Text.Encoding.UTF8);
                }
                catch { }

                if (result.ExitCode == 8)
                {
                    // TQSL found no QSOs to process — something is off; leave the queue untouched.
                    UpdateLotwMenuCount();
                    if (progressWindow != null)
                        progressWindow.ReportBatchResult("TQSL found no QSOs to process — queue unchanged", false);
                    else
                        HolyMessageBox.ShowWarning(
                            "TQSL did not process any QSOs.\n\n" +
                            "The queue was left unchanged. The full TQSL report was saved to " +
                            "lotw_last_upload.txt on your Desktop.",
                            "LoTW Upload", this);
                }
                else
                {
                    // exit 0 = uploaded, exit 9 = already in LoTW (duplicates).
                    // Either way the QSOs are now in LoTW, so clear them from the queue.
                    foreach (var q in pending)
                    {
                        if (!string.IsNullOrWhiteSpace(q.Band) || !string.IsNullOrWhiteSpace(q.Freq))
                            dal.SetLotwStatus(q.id, 1);
                    }
                    UpdateLotwMenuCount();
                    int handled = pending.Count - skippedNoBand;
                    if (progressWindow != null)
                    {
                        string resultLine = result.NothingUploaded
                            ? $"All {handled:N0} QSO(s) already in LoTW (duplicates — queue cleared)"
                            : $"{handled:N0} QSO(s) uploaded to LoTW";
                        if (skippedNoBand > 0)
                            resultLine += $" ({skippedNoBand:N0} skipped — no band/frequency)";
                        progressWindow.ReportBatchResult(resultLine, true);
                    }
                    else
                    {
                        var sb = new System.Text.StringBuilder();
                        if (result.NothingUploaded)
                            sb.AppendLine($"All {handled:N0} QSO(s) are already in LoTW (detected as duplicates).\n\nThey have been cleared from the upload queue.");
                        else
                            sb.AppendLine($"Successfully uploaded {handled:N0} QSO(s) to LoTW.");
                        if (skippedNoBand > 0)
                            sb.AppendLine($"\n{skippedNoBand:N0} QSO(s) skipped — no band or frequency recorded.");
                        sb.AppendLine("\nThe full TQSL report was saved to lotw_last_upload.txt on your Desktop.");
                        HolyMessageBox.ShowSuccess(
                            sb.ToString().TrimEnd(),
                            "LoTW Upload", this);
                    }
                }
            }
            catch (Exception ex)
            {
                if (progressWindow == null) ToggleUploadProgress(Visibility.Hidden);
                try
                {
                    string logPath = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                        "lotw_upload_error.txt");
                    System.IO.File.WriteAllText(logPath,
                        $"LoTW upload error — {DateTime.Now:yyyy-MM-dd HH:mm:ss}\r\n" +
                        $"Message: {ex.Message}\r\n",
                        System.Text.Encoding.UTF8);
                }
                catch { }
                if (progressWindow != null)
                    progressWindow.ReportBatchResult($"Upload failed: {ex.Message}", false);
                else
                    HolyMessageBox.ShowError(
                        "LoTW upload failed:\n\n" + ex.Message +
                        "\n\nDetails written to lotw_upload_error.txt on your Desktop.",
                        "LoTW Upload Failed", this);
            }
            finally
            {
                UploadProgressTitle = "";
                UploadProgress = "";
                try { if (System.IO.File.Exists(adiPath)) System.IO.File.Delete(adiPath); } catch { }
            }
        }

        // Click on the "!" badge in the log grid's header corner opens the same queue window.
        private void EqslQueueBadge_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            ShowEqslQueueWindow();
        }

        private EqslQueueWindow _eqslQueueWindow;

        // Opens (or focuses) a small window listing the QSOs still waiting for eQSL, with a Send
        // button that runs the same upload pass and refreshes the list as each one goes out.
        private void ShowEqslQueueWindow()
        {
            if (dal == null) return;

            if (_eqslQueueWindow != null)
            {
                _eqslQueueWindow.Activate();
                _eqslQueueWindow.RefreshList();
                return;
            }

            _eqslQueueWindow = new EqslQueueWindow(
                () => dal.GetPendingEqslQsos(),
                () => PumpEqslQueue())
            {
                Owner = this
            };
            _eqslQueueWindow.Closed += (s, ev) => { _eqslQueueWindow = null; UpdateEqslQueueIndicator(); };
            _eqslQueueWindow.Show();
        }

        // Builds an aligned, label-friendly text block of the full QSO record for the clipboard.
        private static string BuildQsoClipboardText(QSO qso)
        {
            if (qso == null) return string.Empty;

            var sb = new StringBuilder();
            void Add(string label, string value)
            {
                if (!string.IsNullOrWhiteSpace(value))
                    sb.AppendLine(label.PadRight(11) + ": " + value.Trim());
            }

            string freq = (qso.Freq ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(freq)) freq += " MHz";

            Add("Callsign", qso.DXCall);
            Add("Name", qso.Name);
            Add("Country", qso.Country);
            Add("Date", FormatQsoDate(qso.Date));
            Add("Time", string.IsNullOrWhiteSpace(qso.Time) ? null : FormatQsoTime(qso.Time) + " UTC");
            Add("Band", qso.Band);
            Add("Frequency", freq);
            Add("Mode", qso.Mode);
            Add("RST Sent", qso.RST_SENT);
            Add("RST Rcvd", qso.RST_RCVD);
            Add("DX Locator", qso.DXLocator);
            Add("Exchange", qso.SRX);
            Add("My Call", qso.MyCall);
            Add("Operator", qso.Operator);
            Add("My Locator", qso.MyLocator);
            Add("Comment", qso.Comment);

            return sb.ToString().TrimEnd();
        }

        private static string FormatQsoDate(string raw)
        {
            string d = (raw ?? string.Empty).Trim();
            if (d.Length == 8 && d.All(char.IsDigit))
                return d.Substring(0, 4) + "-" + d.Substring(4, 2) + "-" + d.Substring(6, 2);
            return d;
        }

        private static string FormatQsoTime(string raw)
        {
            string t = (raw ?? string.Empty).Trim();
            if ((t.Length == 6 || t.Length == 4) && t.All(char.IsDigit))
                return t.Substring(0, 2) + ":" + t.Substring(2, 2);
            return t;
        }

        private void EditQsoFromContextMenu(QSO qso)
        {
            if (qso == null) return;
            if (string.IsNullOrWhiteSpace(TB_DXCallsign.Text) || HolyMessageBox.ShowConfirm("Do you want to override current QSO?", "Edit QSO", HolyMsgType.Warning, this))
            {
                QsoToUpdate = qso;
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
                    HolyMessageBox.ShowError("Error: " + ex.Message, "Edit QSO", this);
                }
                UpdateMatrix();
            }
        }

        private void DeleteQsoFromContextMenu(QSO qso)
        {
            if (qso == null) return;
            if (!HolyMessageBox.ShowConfirm("Are you sure you want to delete this QSO?\n\n" + (qso.DXCall ?? string.Empty), "Delete Confirmation", HolyMsgType.Warning, this))
                return;

            // Remove from the filtered view (if present) so the grid updates immediately,
            // then from the master collection which performs the DB delete and refreshes LastQSO.
            if (FilteredQsos != null && FilteredQsos.Contains(qso))
                FilteredQsos.Remove(qso);
            if (Qsos != null && Qsos.Contains(qso))
                Qsos.Remove(qso);
        }

        private async void SetRadioToQsoFreq(QSO qso)
        {
            if (qso == null) return;

            string freqText = (qso.Freq ?? string.Empty).Trim();
            if (!double.TryParse(freqText, NumberStyles.Float, CultureInfo.InvariantCulture, out double freqValue) || freqValue <= 0)
            {
                HolyMessageBox.ShowWarning("This QSO has no valid frequency.", "Set Radio to Frequency", this);
                return;
            }

            double freqMhz = freqValue >= 1000 ? (freqValue / 1000.0) : freqValue;
            string normalizedMode = NormalizeClusterModeForLogger(qso.Mode);

            // Capture the current freq/mode onto the LOG-ROW undo stack (independent of the cluster undo),
            // so the log undo icon's counter increments and the user can step back to the original.
            CaptureLogRadioUndoState();

            // Reflect the QSO's freq/mode in the logger fields (mirrors cluster-spot behavior so undo restores them).
            TB_Frequency.Text = freqMhz.ToString("0.0###", CultureInfo.InvariantCulture);
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

        // ---- Log-row "Set Radio to Freq" undo (independent of the cluster undo) ----

        private void CaptureLogRadioUndoState()
        {
            string frequencyText = (TB_Frequency.Text ?? string.Empty).Trim();
            string modeText = (CB_Mode.Text ?? string.Empty).Trim().ToUpperInvariant();
            string dxCallsignText = (TB_DXCallsign.Text ?? string.Empty).Trim().ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(frequencyText) || string.IsNullOrWhiteSpace(modeText))
            {
                return;
            }

            if (logRadioUndoStates.Count > 0)
            {
                var last = logRadioUndoStates.Peek();
                if (string.Equals(last.FrequencyText, frequencyText, StringComparison.Ordinal)
                    && string.Equals(last.ModeText, modeText, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(last.DxCallsignText, dxCallsignText, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            logRadioUndoStates.Push((frequencyText, modeText, dxCallsignText));
            UpdateLogRadioUndoButtonState();
        }

        // Long-press support for the undo icon: holding the button for ~700 ms clears the whole undo
        // stack at once, instead of stepping back one entry per click.
        private System.Windows.Threading.DispatcherTimer _undoResetTimer;
        private bool _undoResetFired;

        private void MainUndoButton_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _undoResetFired = false;
            if (logRadioUndoStates.Count == 0) return;

            if (_undoResetTimer == null)
            {
                _undoResetTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(700)
                };
                _undoResetTimer.Tick += (s, ev) =>
                {
                    _undoResetTimer.Stop();
                    _undoResetFired = true;   // suppress the upcoming Click (single undo)
                    ResetLogRadioUndo();
                };
            }
            _undoResetTimer.Start();
        }

        private void MainUndoButton_PreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _undoResetTimer?.Stop();
        }

        private void MainUndoButton_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            // Moved off the button before the hold completed - cancel the reset.
            _undoResetTimer?.Stop();
        }

        // Clears the entire log-radio undo stack (the "reset" action triggered by a long press).
        private void ResetLogRadioUndo()
        {
            if (logRadioUndoStates.Count == 0) return;
            logRadioUndoStates.Clear();
            UpdateLogRadioUndoButtonState();
            if (QSODataGrid != null && QSODataGrid.SelectedItem != null)
                QSODataGrid.UnselectAll();
        }

        private async void LogRadioUndoButton_Click(object sender, RoutedEventArgs e)
        {
            // async void: an exception here would be unhandled and crash the app, so the whole body
            // is guarded.
            try
            {
                // If a long press just cleared the stack, swallow this click so it doesn't also undo.
                if (_undoResetFired)
                {
                    _undoResetFired = false;
                    return;
                }

                if (logRadioUndoStates.Count == 0)
                {
                    return;
                }

                var undoState = logRadioUndoStates.Pop();
                UpdateLogRadioUndoButtonState();

                // Clear the log-row blue highlight once an undo step is taken.
                if (QSODataGrid != null && QSODataGrid.SelectedItem != null)
                    QSODataGrid.UnselectAll();

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
            catch { /* never crash the app from the undo button */ }
        }

        private void UpdateLogRadioUndoButtonState()
        {
            bool hasUndo = logRadioUndoStates.Count > 0;

            if (MainUndoIconGrid != null)
            {
                MainUndoIconGrid.Visibility = hasUndo ? Visibility.Visible : Visibility.Collapsed;
            }
            if (MainUndoCountText != null)
            {
                MainUndoCountText.Text = hasUndo ? logRadioUndoStates.Count.ToString(CultureInfo.InvariantCulture) : string.Empty;
            }
        }

        private Window BuildSpotDialog(string presetCallsign = null, string presetFrequency = null)
        {
            bool hasPreset = !string.IsNullOrWhiteSpace(presetCallsign);
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

            string defaultSpottedCallsign = hasPreset
                ? presetCallsign
                : (string.IsNullOrWhiteSpace(TB_DXCallsign.Text)
                    ? (LastQSO != null ? LastQSO.DXCall : string.Empty)
                    : TB_DXCallsign.Text);

            AddSpotDialogLabel(grid, "Spotted Callsign", 1, new Thickness(0, 8, 0, 0));
            TextBox spottedCallsignTextBox = AddSpotDialogTextBox(grid, defaultSpottedCallsign, 1, false, new Thickness(0, 8, 0, 0));

            string defaultFrequency = hasPreset
                ? (presetFrequency ?? string.Empty)
                : (string.IsNullOrWhiteSpace(TB_DXCallsign.Text)
                    ? (LastQSO != null ? LastQSO.Freq : string.Empty)
                    : TB_Frequency.Text);

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
                    HolyMessageBox.ShowError(ex.Message, "Spot Failed", dialog);
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

            // Sending needs the radio/CAT. The buttons stay enabled only so they can be right-clicked to
            // edit the CW text, so a left-click / F-key send is simply ignored when sending isn't possible.
            if (!_messageSendAvailable)
            {
                return;
            }

            if (IsCwModeActive())
            {
                TriggerCwTextMessage(messageNumber);
                return;
            }

            if (!TryGetVoiceCommandProfile(out RadioVoiceCommandProfile profile, out string rigType, out string errorMessage))
            {
                HolyMessageBox.ShowWarning(errorMessage, "Voice Message", this);
                return;
            }

            int? currentMessageNumber = activeVoiceMessageNumber ?? pendingVoiceMessageNumber;

            if (currentMessageNumber.HasValue)
            {
                if (!string.IsNullOrWhiteSpace(profile.StopCommand) && !TrySendOmniRigCustomCommand(profile.StopCommand))
                {
                    HolyMessageBox.ShowWarning("Failed to send the stop CAT command to " + rigType + ".", "Voice Message", this);
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
                HolyMessageBox.ShowWarning("No voice-message CAT command is defined for this button.", "Voice Message", this);
                return;
            }

            if (!TrySendOmniRigCustomCommand(command))
            {
                HolyMessageBox.ShowWarning("Failed to send the CAT command to " + rigType + ".", "Voice Message", this);
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
                    OnCwTransmitStarted();
                }
                else if (DateTime.UtcNow >= pendingVoiceMessageDeadlineUtc)
                {
                    pendingVoiceMessageNumber = null;
                    CloseCwSendMonitor(false);
                }
            }
            else if (activeVoiceMessageNumber.HasValue && !txOn)
            {
                activeVoiceMessageNumber = null;
                OnCwTransmitEnded();
            }

            UpdateVoiceMessageButtonHighlight();
        }

        private void ClearVoiceMessageState()
        {
            pendingVoiceMessageNumber = null;
            activeVoiceMessageNumber = null;
            pendingVoiceMessageDeadlineUtc = DateTime.MinValue;
            CloseCwSendMonitor(false);
            UpdateVoiceMessageButtonHighlight();
        }

        private void VoiceMessageAvailabilityTimer_Tick(object sender, EventArgs e)
        {
            UpdateVoiceMessageAvailabilityState();
        }

        // True when sending CW/voice messages to the radio is actually possible (CAT online). The Msg
        // buttons stay ENABLED regardless, so they can always be right-clicked to edit the CW text;
        // this flag gates only the left-click / F-key SEND action.
        private bool _messageSendAvailable = false;

        private void UpdateVoiceMessageAvailabilityState()
        {
            if (PlayCommandsBorder == null)
            {
                return;
            }

            bool isCw = IsCwModeActive();
            bool isVoiceAvailable = TryGetVoiceMessageAvailability(out _, out string errorMessage);
            bool isAvailable = isVoiceAvailable || (isCw && Properties.Settings.Default.EnableOmniRigCAT && OmniRigEngine != null && Rig != null && Rig.Status == OmniRig.RigStatusX.ST_ONLINE);

            _messageSendAvailable = isAvailable;
            // Keep the row ENABLED at all times so the buttons can always be right-clicked to edit the
            // CW text (a disabled button ignores right-clicks too). When sending isn't available the row
            // is just dimmed, and a left-click / F-key send is ignored.
            PlayCommandsBorder.IsEnabled = true;
            SetVoiceMessageButtonsEnabled(true);
            PlayCommandsBorder.Opacity = isAvailable ? 1.0 : 0.5;

            if (isCw)
            {
                PlayCommandsBorder.ToolTip = isAvailable
                    ? "Send CW text to radio (F5-F8) — right-click to edit"
                    : "Radio off — sending is disabled. Right-click a button to edit its CW text.";
            }
            else
            {
                PlayCommandsBorder.ToolTip = isVoiceAvailable ? "Play radio voice messages (F5-F8)" : errorMessage;
            }

            if (!isAvailable)
            {
                ClearVoiceMessageState();
            }

            UpdateMessageButtonLabels();
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

            // In CW mode the style controls the idle colour (bright cyan). Use ClearValue (not
            // Background = null) when idle: a local null value would beat the style's Background
            // setter and make the inner KeyFace transparent, exposing the dark outer border across
            // the whole button. While transmitting, apply the same orange highlight as SSB.
            if (IsCwModeActive())
            {
                if (isActive)
                {
                    button.Background = VoiceMessageActiveBrush;
                }
                else
                {
                    button.ClearValue(Control.BackgroundProperty);
                }
                return;
            }

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
                // A log already exists: let the user choose to MERGE (add to it) or REPLACE it. An
                // empty log is unambiguous, so no prompt is needed.
                int existing = 0;
                try { if (dal != null) existing = dal.GetQsoCount(); }
                catch { existing = 0; }

                if (existing > 0)
                {
                    ImportLogChoice choice = AskImportMergeOrReplace(existing);
                    if (choice == ImportLogChoice.Cancel)
                        return;
                    if (choice == ImportLogChoice.Replace && !BackupAndClearLogForReplace())
                        return; // backup cancelled or failed -> abort; the log is left untouched
                }

                ImportFileQ.Add(openFileDialog.FileName);
                StartAdifImportWorker();
            }
        }

        private enum ImportLogChoice { Cancel, Merge, Replace }

        // Warns that a log already exists and asks the user to Merge (append) or Replace it. Built as
        // a small custom dialog because a standard MessageBox can't have "Merge"/"Replace" buttons.
        private ImportLogChoice AskImportMergeOrReplace(int existingCount)
        {
            ImportLogChoice result = ImportLogChoice.Cancel;

            var dialog = new Window
            {
                Title = "Import ADIF",
                SizeToContent = SizeToContent.WidthAndHeight,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ShowInTaskbar = false
            };

            var root = new StackPanel { Margin = new Thickness(18, 10, 18, 18) };

            var headerRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
            headerRow.Children.Add(new TextBlock
            {
                Text = "⚠",                       // warning sign
                FontSize = 26,
                Foreground = System.Windows.Media.Brushes.DarkOrange,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            });
            headerRow.Children.Add(new TextBlock
            {
                Text = "Your log already contains " + existingCount + " QSO" + (existingCount == 1 ? "" : "s") + ".",
                FontSize = 15,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center
            });
            root.Children.Add(headerRow);

            // Each option is a bold label with a hanging-indented description: when the description
            // wraps, the continuation lines align under the first word of the description (i.e. under
            // "first" for Replace), not back at the left edge.
            DockPanel MakeOption(string label, string desc, Thickness margin)
            {
                var row = new DockPanel { MaxWidth = 430, Margin = margin };
                var lbl = new TextBlock { FontSize = 14, VerticalAlignment = VerticalAlignment.Top };
                lbl.Inlines.Add(new System.Windows.Documents.Run(label) { FontWeight = FontWeights.Bold });
                lbl.Inlines.Add(new System.Windows.Documents.Run(" — "));
                DockPanel.SetDock(lbl, Dock.Left);
                row.Children.Add(lbl);
                row.Children.Add(new TextBlock { Text = desc, TextWrapping = TextWrapping.Wrap, FontSize = 14 });
                return row;
            }

            root.Children.Add(MakeOption("Merge", "add the file's QSOs to your existing log.", new Thickness(0, 0, 0, 12)));
            root.Children.Add(MakeOption("Replace", "first save a backup of your current log to a file you choose, then clear the log and import the file.", new Thickness(0, 0, 0, 34)));

            var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            Button MakeButton(string text)
            {
                return new Button { Content = text, MinWidth = 90, Margin = new Thickness(6, 0, 6, 0), Padding = new Thickness(12, 5, 12, 5), FontSize = 14 };
            }
            var mergeBtn = MakeButton("Merge");
            var replaceBtn = MakeButton("Replace");
            var cancelBtn = MakeButton("Cancel");
            cancelBtn.IsCancel = true;
            mergeBtn.Click += (s, e) => { result = ImportLogChoice.Merge; dialog.Close(); };
            replaceBtn.Click += (s, e) => { result = ImportLogChoice.Replace; dialog.Close(); };
            cancelBtn.Click += (s, e) => { result = ImportLogChoice.Cancel; dialog.Close(); };
            buttonRow.Children.Add(mergeBtn);
            buttonRow.Children.Add(replaceBtn);
            buttonRow.Children.Add(cancelBtn);
            root.Children.Add(buttonRow);

            dialog.Content = root;
            dialog.ShowDialog();
            return result;
        }

        // For "Replace": let the user save a backup of the current log to a file they choose, then
        // clear the log. Returns false (log left untouched) if the user cancels the save dialog or the
        // backup fails — we never destroy the log without a successful backup.
        private bool BackupAndClearLogForReplace()
        {
            var saveDialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "ADIF files (*.adi)|*.adi",
                FileName = "HolyLogger_backup_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".adi",
                Title = "Save a backup of your current log before replacing it"
            };
            if (saveDialog.ShowDialog() != true)
                return false; // user cancelled -> abort the replace

            try
            {
                string adif = Services.GenerateAdif(dal.GetAllQSOs());
                System.IO.File.WriteAllText(saveDialog.FileName, adif);
            }
            catch (Exception ex)
            {
                HolyMessageBox.ShowError("Failed to save the backup:\n" + ex.Message + "\n\nReplace cancelled — your log was not changed.", "Backup Failed", this);
                return false;
            }

            // Backup succeeded -> safe to clear the current log before importing the new file.
            Properties.Settings.Default.RecentQSOCounter = 0;
            Qsos.Clear();
            dal.DeleteAll();
            ClearBtn_Click(null, null);
            UpdateNumOfQSOs();
            UpdateEqslQueueIndicator();
            UpdateQrzMenuCount();
            return true;
        }

        private void StartAdifImportWorker()
        {
            if (AdifHandlerWorker == null || AdifHandlerWorker.IsBusy)
                return;

            UploadProgress = "Starting import 0%";
            ToggleUploadProgress(Visibility.Visible);
            AdifHandlerWorker.RunWorkerAsync();
        }
        
        private void AdifHandlerWorker_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            if (e.Error != null)
            {
                ToggleUploadProgress(Visibility.Hidden);
                HolyMessageBox.ShowError($"Import failed.\n\n{e.Error.Message}", "Import Error", this);
                return;
            }

            var result = e.Result as AdifImportResult ?? new AdifImportResult();

            if (Qsos != null)
            {
                Qsos.CollectionChanged -= Qsos_CollectionChanged;
            }

            Qsos = result.RefreshedQsos ?? new ObservableCollection<QSO>();
            Qsos.CollectionChanged += Qsos_CollectionChanged;
            DataContext = Qsos;
            LastQSO = Qsos.FirstOrDefault();
            ApplyDefaultLogSort();

            // Replacing the whole collection does NOT raise CollectionChanged, so the cluster
            // colors aren't refreshed automatically. Rebuild the worked-countries cache (needed =
            // red) and re-evaluate the in-log status (worked before = blue) against the new log.
            RebuildWorkedCountriesAndRefreshCluster();
            if (clusterVisibleSpots != null)
                RefreshClusterVisibleSpots();

            ToggleUploadProgress(Visibility.Hidden);
            UpdateNumOfQSOs();
            UpdateLotwMenuCount();
            UpdateQrzMenuCount();

            if (result.FaultyQso > 0)
            {
                HolyMessageBox.ShowWarning($"{result.FaultyQso} QSO(s) failed to import. Check the file format and try again.", "Import Complete with Errors", this);
            }
            else
            {
                if (result.ImportedQsoCount > 0)
                {
                    int totalQsos = result.RefreshedQsos != null ? result.RefreshedQsos.Count : dal.GetQsoCount();
                    HolyMessageBox.ShowSuccess($"Import completed successfully!\nImported QSOs: {result.ImportedQsoCount}\nTotal QSOs in log: {totalQsos}", "Import Complete", this);
                }
            }
            TB_Comment.Text = "";
            UpdateNumOfQSOs();
        }

        private void AdifHandlerWorker_ProgressChanged(object sender, ProgressChangedEventArgs e)
        {
            ToggleUploadProgress(Visibility.Visible);
            UploadProgress = e.UserState as string ?? (e.ProgressPercentage.ToString() + "%");
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
            int importedQsoCount = 0;
            const int importBatchSize = 500;
            int lastReportedPercent = 0;
            const int readPhasePercent = 3;
            const int parsePhaseEndPercent = 78;
            const int savePhaseStartPercent = 79;
            const int savePhaseEndPercent = 95;
            const int refreshPhaseStartPercent = 96;
            const int refreshPhaseEndPercent = 100;

            foreach (var filename in files)
            {
                try
                {
                    lastReportedPercent = 1;
                    AdifHandlerWorker.ReportProgress(lastReportedPercent, "Preparing import 1%");

                    if (!File.Exists(filename))
                    {
                        this.Dispatcher.Invoke(() =>
                            HolyMessageBox.ShowError($"File not found:\n{filename}", "Import Error", this));
                        continue;
                    }

                    lastReportedPercent = readPhasePercent;
                    AdifHandlerWorker.ReportProgress(lastReportedPercent, "Reading file 3%");
                    string RawAdif = File.ReadAllText(filename, Encoding.UTF8);

                    if (string.IsNullOrWhiteSpace(RawAdif))
                    {
                        this.Dispatcher.Invoke(() =>
                            HolyMessageBox.ShowWarning($"File is empty:\n{filename}", "Import Error", this));
                        continue;
                    }

                    var parser = new HolyLogParser(RawAdif,
                        HolyLogParser.IsIsraeliStation(myCallsign) ? HolyLogParser.Operator.Israeli : HolyLogParser.Operator.Foreign,
                        isParseDuplicates, isParseWARC);

                    parser.Parse(parseProgress =>
                    {
                        int percent = readPhasePercent + (int)Math.Floor((parseProgress * (parsePhaseEndPercent - readPhasePercent)) / 100.0);
                        if (percent > lastReportedPercent)
                        {
                            lastReportedPercent = percent;
                            AdifHandlerWorker.ReportProgress(percent, $"Parsing ADIF {parseProgress}%");
                        }
                    });
                    List<QSO> rawQSOList = parser.GetRawQSO();
                    int count = rawQSOList.Count;

                    if (count == 0)
                    {
                        this.Dispatcher.Invoke(() =>
                            HolyMessageBox.ShowWarning($"No QSOs found in file:\n{filename}\n\nThe file may be in an unsupported format or empty.", "Import Warning", this));
                        continue;
                    }

                    // If the file's station callsign(s) differ from the current station callsign, warn
                    // the user with a clear prompt centered on the program window and let them approve
                    // or cancel importing this file.
                    if (!string.IsNullOrWhiteSpace(myCallsign))
                    {
                        List<string> fileCalls = rawQSOList
                            .Select(q => (q.MyCall ?? string.Empty).Trim())
                            .Where(s => s.Length > 0)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();

                        bool differentCall = fileCalls.Any(c => !string.Equals(c, myCallsign.Trim(), StringComparison.OrdinalIgnoreCase));
                        if (differentCall)
                        {
                            string fileName = System.IO.Path.GetFileName(filename);
                            string callsInFile = fileCalls.Count > 0 ? string.Join(", ", fileCalls) : "(none)";
                            bool approved = this.Dispatcher.Invoke(() =>
                                HolyMessageBox.ShowConfirm(
                                    "The ADIF file \"" + fileName + "\" contains QSOs logged under a different callsign than your current station callsign.\n\n" +
                                    "Callsign(s) in the file:  " + callsInFile + "\n" +
                                    "Your current station callsign:  " + myCallsign.Trim() + "\n\n" +
                                    "Do you want to import these QSOs into your log anyway?",
                                    "Different callsign in ADIF file", HolyMsgType.Warning, this));

                            if (!approved)
                            {
                                // User declined importing this file's QSOs.
                                continue;
                            }
                        }
                    }

                    foreach (var rq in rawQSOList)
                    {
                        if (isOverride)
                        {
                            rq.Operator = overrideOperator;
                        }
                    }

                    for (int i = 0; i < count; i += importBatchSize)
                    {
                        List<QSO> batch = rawQSOList.Skip(i).Take(importBatchSize).ToList();
                        int batchFaulty;
                        int batchStartIndex = i;
                        lock (_syncLock)
                        {
                            batchFaulty = dal.InsertBatch(batch, processedInBatch =>
                            {
                                int processedOverall = batchStartIndex + processedInBatch;
                                int savePercent = (int)Math.Ceiling((float)processedOverall * 100 / count);
                                int percent = savePhaseStartPercent + (int)Math.Floor((savePercent * (savePhaseEndPercent - savePhaseStartPercent)) / 100.0);
                                if (percent > lastReportedPercent)
                                {
                                    lastReportedPercent = percent;
                                    AdifHandlerWorker.ReportProgress(percent, $"Saving to log {savePercent}%");
                                }
                            });
                        }

                        faultyQSO += batchFaulty;
                        importedQsoCount += batch.Count - batchFaulty;
                    }
                }
                catch (Exception ex)
                {
                    string failedFile = filename;
                    string errorMsg = $"Failed to load file:\n{failedFile}\n\nError: {ex.Message}";
                    if (ex.InnerException != null)
                    {
                        errorMsg += $"\n\nDetails: {ex.InnerException.Message}";
                    }
                    this.Dispatcher.Invoke(() =>
                        HolyMessageBox.ShowError(errorMsg, "Import Error", this));
                }
            }

            if (lastReportedPercent < savePhaseEndPercent)
            {
                lastReportedPercent = savePhaseEndPercent;
                AdifHandlerWorker.ReportProgress(lastReportedPercent, $"Saving to log {savePhaseEndPercent}%");
            }

            ObservableCollection<QSO> refreshedQsos;
            lock (_syncLock)
            {
                refreshedQsos = dal.GetAllQSOs(refreshProgress =>
                {
                    int percent = refreshPhaseStartPercent + (int)Math.Floor((refreshProgress * (refreshPhaseEndPercent - refreshPhaseStartPercent)) / 100.0);
                    if (percent > lastReportedPercent)
                    {
                        lastReportedPercent = percent;
                        AdifHandlerWorker.ReportProgress(percent, $"Refreshing log table {refreshProgress}%");
                    }
                });
            }

            AdifHandlerWorker.ReportProgress(100, "Import complete 100%");

            e.Result = new AdifImportResult
            {
                FaultyQso = faultyQSO,
                ImportedQsoCount = importedQsoCount,
                RefreshedQsos = refreshedQsos
            };
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
                StartAdifImportWorker();
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
                    using (StreamWriter sw = new StreamWriter(fs))
                    {
                        sw.Write(adif);
                    }
                    HolyMessageBox.ShowSuccess("File created successfully!", "Export ADIF", this);
                }
            }
            catch (Exception ex)
            {
                HolyMessageBox.ShowError("Export failed: " + ex.Message, "Export ADIF", this);
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
                    using (StreamWriter sw = new StreamWriter(fs))
                    {
                        sw.Write(cabrillo);
                    }
                    HolyMessageBox.ShowSuccess("File created successfully!", "Export Cabrillo", this);
                }
            }
            catch (Exception ex)
            {
                HolyMessageBox.ShowError("Export failed: " + ex.Message, "Export Cabrillo", this);
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
                    using (StreamWriter sw = new StreamWriter(fs))
                    {
                        sw.Write(adif);
                    }
                    HolyMessageBox.ShowSuccess("File created successfully!", "Export CSV", this);
                }
            }
            catch (Exception ex)
            {
                HolyMessageBox.ShowError("Export failed: " + ex.Message, "Export CSV", this);
            }

        }

        private async void L_SendLog(object sender, EventArgs e)
        {
            LogUploadWindow w = (LogUploadWindow)sender;
            if (Qsos.Count == 0)
            {
                HolyMessageBox.ShowWarning("Cannot upload an empty log.", "Log Upload", this);
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
                HolyMessageBox.Show(UploadCabrilloToIARC_result, "Log Upload", HolyMsgType.Info, this);
            }
            else
            {
                string AddParticipant_result = await AddParticipant(bareCallsign, w.selectedOperator.Name, w.selectedMode.Name, w.selectedPower.Name, Properties.Settings.Default.PersonalInfoEmail, Properties.Settings.Default.PersonalInfoName, country);
                string UploadLogToIARC_result = await UploadLogToIARC(new Progress<int>(percent => w.UploadProgress = percent), dal.GetAllQSOs());
                w.Close();
                HolyMessageBox.Show(UploadLogToIARC_result, "Log Upload", HolyMsgType.Info, this);
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
                if (!HolyMessageBox.ShowConfirm("Are you sure you want to delete this QSO?", "Delete Confirmation", HolyMsgType.Warning, this))
                    e.Handled = true;
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

        private void QSODataGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Enter edit mode on a double-click. Handling it here (Preview, which tunnels from the
            // window before focus logic can swallow the first click) makes it work on the FIRST
            // double-click even when focus was elsewhere or the grid was just rebound after F1/Add.
            if (e.ClickCount != 2) return;

            var row = FindVisualParent<DataGridRow>(e.OriginalSource as DependencyObject);
            if (row == null) return; // not on a data row (header / empty area)

            QSO qso = row.Item as QSO;
            if (qso == null) return;

            QSODataGrid.SelectedItem = qso;
            e.Handled = true;
            EditQsoFromContextMenu(qso);
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
            // Suppress the DX-callsign typing lookup for the entire load — including the ClearBtn_Click
            // reset below, which also clears the callsign. Otherwise the lookup's deferred field-clearing
            // and QRZ re-query run after we set the fields and wipe the QSO's saved Name/Locator/Country.
            _suppressCallsignLookupForEdit = true;
            try
            {
                CallsignLookupDebounceTimer.Stop();
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
                // Country/Continent are normally filled by the (now-suppressed) callsign lookup, so
                // load the QSO's saved values directly into the bound properties, and show the
                // country flag the same way the lookup does (falls back to the text label if there
                // is no flag for that country).
                Country = QsoToUpdate.Country;
                Continent = QsoToUpdate.Continent;
                UpdateCountryFlag(QsoToUpdate.Country);

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
            finally
            {
                _suppressCallsignLookupForEdit = false;
            }
            
        }
        
        private void UpdateState(State newState)
        {
            state = newState;
            UpdateAddBtnLabel();
            UpdateEditModeBackground();
        }

        private void UpdateAddBtnLabel()
        {
            if (state == State.Edit)
            {
                AddBtn.Content = "Update (F1)";
                ClearBtn.Content = "Exit (F9)";
            }
            else if (state == State.New)
            {
                AddBtn.Content = "Add (F1)";
                ClearBtn.Content = "Clear (F9)";
            }
        }

        private void UpdateEditModeBackground()
        {
            var editModeColor = new SolidColorBrush(Colors.Yellow);
            var normalColor = new SolidColorBrush(Colors.White);

            var backgroundColor = (state == State.Edit) ? editModeColor : normalColor;

            // Only highlight QSO-specific fields, not user station information
            TB_Frequency.Background = backgroundColor;
            TB_DXCallsign.Background = backgroundColor;
            TB_Exchange.Background = backgroundColor;
            TB_RSTSent.Background = backgroundColor;
            TB_RSTRcvd.Background = backgroundColor;
            TB_DX_Name.Background = backgroundColor;
            TB_State.Background = backgroundColor;
            TB_DXLocator.Background = backgroundColor;
            TB_Comment.Background = backgroundColor;
            CB_Mode.Background = backgroundColor;
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
            ManualModeMenuItem.Header = Properties.Settings.Default.isManualMode ? "Manual Mode - ON" : "Manual Mode - OFF";
            L_IsManual.Text = Properties.Settings.Default.isManualMode ? "On" : "Off";
            ShowRigParams();
        }

        private void ResetRecentQSOCounterMenuItem_Click(object sender, RoutedEventArgs e)
        {
            Properties.Settings.Default.RecentQSOCounter = 0;
        }

        // Contest Mode on/off (Tools menu). When on, exact-match QSOs (same callsigns + band + mode)
        // are flagged as "Duplicate"; when off, the program never reports a duplicate and instead
        // shows how many times the station was worked before.
        private void ContestModeMenuItem_Click(object sender, RoutedEventArgs e)
        {
            Properties.Settings.Default.ContestMode = !Properties.Settings.Default.ContestMode;
            Properties.Settings.Default.Save();
            UpdateContestModeMenuHeader();
            // Re-evaluate the duplicate / worked-before indicator for the current callsign.
            UpdateDup();
        }

        private void UpdateContestModeMenuHeader()
        {
            bool on = Properties.Settings.Default.ContestMode;
            var gold = new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07));
            var gray = new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E));
            Brush blue = new SolidColorBrush(Color.FromRgb(0x15, 0x65, 0xC0));

            if (ContestModeMenuItem != null)
                ContestModeMenuItem.Header = on ? "Contest Mode - ON" : "Contest Mode - OFF";

            // Tools-menu icon: gold trophy on a blue tile when contesting, plain gray when not.
            if (ContestTrophyPath != null)
                ContestTrophyPath.Fill = on ? gold : gray;
            if (ContestTrophyBg != null)
                ContestTrophyBg.Background = on ? blue : Brushes.Transparent;

            // Main-screen state indicator (display only) mirrors the same look, and its tooltip
            // explains the current mode on hover.
            if (ContestIndicatorPath != null)
                ContestIndicatorPath.Fill = on ? gold : gray;
            if (ContestIndicator != null)
            {
                ContestIndicator.Background = on ? blue : Brushes.Transparent;
                ContestIndicator.ToolTip = on
                    ? "Contest Mode: ON — duplicates are flagged.\nA QSO with the same callsign, band and mode\ncounts as a dupe. Change it in the Tools menu."
                    : "Contest Mode: OFF — duplicates aren't flagged.\nThe log shows how many times each station\nwas worked. Change it in the Tools menu.";
            }
        }

        private async void PropertiesWindow_Closed(object sender, EventArgs e)
        {
            if (String.IsNullOrWhiteSpace(SessionKey))
                if (isNetworkAvailable) _SessionKey = await Helper.LoginToQRZAsync();
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
                HolyMessageBox.ShowError("Parsing failed.", "HolyLogger", this);
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
            if (_isShutdownCleanupDone)
                return;

            // Upload-on-exit: show ALL service dialogs in one pass before any uploading starts,
            // so we never call Close() from inside an async upload (which caused freezes when the
            // second/third dialog tried to show while the main window was already half-destroyed).
            // A single UploadAllAndCloseAsync call does all uploads, then calls Close() exactly once.
            if (!_uploadOnExitHandled)
            {
                _uploadOnExitHandled = true;

                List<QSO> lotwToUpload = null;
                bool uploadEqsl = false;
                bool uploadQrz = false;

                // ── LoTW ─────────────────────────────────────────────────────────────────────
                int lotwMode = Properties.Settings.Default.LotwUploadOnExitMode;
                if (lotwMode != 0)
                {
                    List<QSO> lotwPending = null;
                    try { lotwPending = dal?.GetPendingLotwQsos(); } catch { }
                    if (lotwPending != null && lotwPending.Count > 0)
                    {
                        bool doUpload = lotwMode == 2;
                        if (lotwMode == 1)
                        {
                            var dlg = new LotwUploadOnExitDialog(lotwPending.Count) { Owner = this };
                            dlg.ShowDialog();
                            if (dlg.Choice == LotwExitChoice.Cancel)
                            {
                                _uploadOnExitHandled = false;
                                e.Cancel = true;
                                return;
                            }
                            doUpload = dlg.Choice == LotwExitChoice.Upload;
                        }
                        if (doUpload) lotwToUpload = lotwPending;
                    }
                }

                // ── eQSL ─────────────────────────────────────────────────────────────────────
                int eqslMode = Properties.Settings.Default.EqslUploadOnExitMode;
                if (eqslMode != 0)
                {
                    int eqslPending = 0;
                    try { eqslPending = dal?.GetPendingEqslCount() ?? 0; } catch { }
                    if (eqslPending > 0)
                    {
                        bool doUpload = eqslMode == 2;
                        if (eqslMode == 1)
                        {
                            var dlg = new ServiceUploadOnExitDialog("eQSL", eqslPending) { Owner = this };
                            dlg.ShowDialog();
                            if (dlg.DialogResult2 == ServiceUploadOnExitDialog.Result.Cancel)
                            {
                                _uploadOnExitHandled = false;
                                e.Cancel = true;
                                return;
                            }
                            doUpload = dlg.DialogResult2 == ServiceUploadOnExitDialog.Result.Yes;
                        }
                        uploadEqsl = doUpload;
                    }
                }

                // ── QRZ ──────────────────────────────────────────────────────────────────────
                int qrzMode = Properties.Settings.Default.QrzUploadOnExitMode;
                if (qrzMode != 0)
                {
                    int qrzPending = 0;
                    try { qrzPending = dal?.GetPendingQrzCount() ?? 0; } catch { }
                    if (qrzPending > 0)
                    {
                        bool doUpload = qrzMode == 2;
                        if (qrzMode == 1)
                        {
                            var dlg = new ServiceUploadOnExitDialog("QRZ Logbook", qrzPending) { Owner = this };
                            dlg.ShowDialog();
                            if (dlg.DialogResult2 == ServiceUploadOnExitDialog.Result.Cancel)
                            {
                                _uploadOnExitHandled = false;
                                e.Cancel = true;
                                return;
                            }
                            doUpload = dlg.DialogResult2 == ServiceUploadOnExitDialog.Result.Yes;
                        }
                        uploadQrz = doUpload;
                    }
                }

                if (lotwToUpload != null || uploadEqsl || uploadQrz)
                {
                    e.Cancel = true;
                    UploadAllAndCloseAsync(lotwToUpload, uploadEqsl, uploadQrz);
                    return;
                }
            }

            SaveAutosnapshot();
            _isShutdownCleanupDone = true;

            // Stop all timers before shutdown to prevent pending async operations
            try { if (HeartbeatTimer != null && HeartbeatTimer.IsEnabled) HeartbeatTimer.Stop(); } catch { }
            try { if (UTCTimer != null && UTCTimer.IsEnabled) UTCTimer.Stop(); } catch { }
            try { if (CallsignLookupDebounceTimer != null && CallsignLookupDebounceTimer.IsEnabled) CallsignLookupDebounceTimer.Stop(); } catch { }
            try { VoiceMessageAvailabilityTimer.Tick -= VoiceMessageAvailabilityTimer_Tick; if (VoiceMessageAvailabilityTimer.IsEnabled) VoiceMessageAvailabilityTimer.Stop(); } catch { }
            try { if (NewDXCCTimer != null) { NewDXCCTimer.Stop(); NewDXCCTimer.Dispose(); } } catch { }
            try { if (_mapUpdateDebounceTimer != null) { _mapUpdateDebounceTimer.Stop(); _mapUpdateDebounceTimer = null; } } catch { }

            // Unsubscribe from network availability events
            try { NetworkChange.NetworkAvailabilityChanged -= NetworkChange_NetworkAvailabilityChanged; } catch { }

            // Dispose CallsignUploader to unsubscribe from NetworkChange events
            try { _callsignUploader?.Dispose(); } catch { }

            // Close and dispose UDP clients
            try 
            { 
                if (Client != null)
                {
                    Client.Close();
                    Client.Dispose();
                    Client = null;
                }
            } 
            catch { }

            try 
            { 
                if (N1MMClient != null)
                {
                    N1MMClient.Close();
                    N1MMClient.Dispose();
                    N1MMClient = null;
                }
            } 
            catch { }

            // Close cluster WebSocket
            try { CloseClusterWebSocket(); } catch { }

            // Unsubscribe from MapControl events
            try { MapControl.RadiusChanged -= OnMapRadiusChanged; } catch { }
            try { MapControl.SpotTuneRequested -= OnMapSpotTuneRequested; } catch { }
            try { MapControl.SpotHovered -= OnMapSpotHovered; } catch { }
            try { MapControl.SpotHoverEnded -= OnMapSpotHoverEnded; } catch { }
        }

        // Uploads any confirmed services in sequence, showing per-QSO progress, then closes exactly once.
        // All confirmation dialogs were already shown in Window_Closing before this is called.
        private async void UploadAllAndCloseAsync(List<QSO> lotwPending, bool uploadEqsl, bool uploadQrz)
        {
            this.IsEnabled = false;

            // Check connectivity before showing the window so the window never appears blank
            // while waiting for the network check to complete.
            bool online = false;
            try { online = Helper.CheckForInternetConnection(); } catch { }

            var progressWindow = new UploadProgressWindow { Owner = this };
            progressWindow.Show();

            if (lotwPending != null)
            {
                string tqslPath = Properties.Settings.Default.LotwTqslPath?.Trim();
                string location = Properties.Settings.Default.LotwStationLocation?.Trim();
                string password = Properties.Settings.Default.LotwTqslPassword;
                bool tqslConfigured = !string.IsNullOrWhiteSpace(tqslPath) && System.IO.File.Exists(tqslPath)
                                      && !string.IsNullOrWhiteSpace(location);
                if (!online || !tqslConfigured)
                {
                    string why = !online ? "no internet connection"
                        : "TQSL not configured (set path and location in Options → LoTW)";
                    progressWindow.SkipService("LoTW", $"{lotwPending.Count:N0} QSO(s) — {why}");
                }
                else
                {
                    try { await UploadLotwQueueCoreAsync(lotwPending, tqslPath, location, password, progressWindow); } catch { }
                }
            }

            if (uploadEqsl)
            {
                if (!online)
                    progressWindow.SkipService("eQSL", "no internet connection — QSOs remain in queue");
                else
                    try { await PumpEqslQueue(force: true, progressWindow); } catch { }
            }

            if (uploadQrz)
            {
                bool qrzConfigured = Properties.Settings.Default.qrz_logbook_key_valid
                                     && !string.IsNullOrWhiteSpace(Properties.Settings.Default.qrz_api_key);
                if (!online || !qrzConfigured)
                {
                    string why = !online ? "no internet connection"
                        : "API key not configured (set it in Options → QRZ)";
                    progressWindow.SkipService("QRZ Logbook", $"{why} — QSOs remain in queue");
                }
                else
                    try { await PumpQrzQueue(force: true, progressWindow); } catch { }
            }

            progressWindow.ShowComplete();
            await progressWindow.WaitForOkAsync();

            this.Close();
        }

        private void Window_Closed(object sender, EventArgs e)
        {
            if (!_isShutdownCleanupDone)
            {
                Window_Closing(this, new System.ComponentModel.CancelEventArgs());
            }

            // Unsubscribe from event handlers to prevent memory leaks
            try { this.Loaded -= MainWindow_Loaded; } catch { }
            try { Properties.Settings.Default.PropertyChanged -= Settings_PropertyChanged; } catch { }

            if (AdifHandlerWorker != null)
            {
                try { AdifHandlerWorker.DoWork -= AdifHandlerWorker_DoWork; } catch { }
                try { AdifHandlerWorker.ProgressChanged -= AdifHandlerWorker_ProgressChanged; } catch { }
                try { AdifHandlerWorker.RunWorkerCompleted -= AdifHandlerWorker_RunWorkerCompleted; } catch { }
            }

            UTCTimer.Tick -= UTCTimer_Elapsed;
            if (OmniRigEngine != null)
            {
                OmniRigEngine.StatusChange -= OmniRigEngine_StatusChange;
                OmniRigEngine.ParamsChange -= OmniRigEngine_ParamsChange;
                Rig = null;
                OmniRigEngine = null;
            }
            NewDXCCTimer.Tick -= NewDXCCTimer_Tick;
            NewDXCCTimer.Stop();
            NewDXCCTimer.Dispose();
            Properties.Settings.Default.SignBoardWindowIsOpen = Application.Current.Windows.Cast<Window>().SingleOrDefault(w => w == signboard) != null;
            Properties.Settings.Default.MatrixWindowIsOpen = Application.Current.Windows.Cast<Window>().SingleOrDefault(w => w == matrix) != null;
            Properties.Settings.Default.TimerWindowIsOpen = Application.Current.Windows.Cast<Window>().SingleOrDefault(w => w == timerscreen) != null;
            try { Properties.Settings.Default.Save(); } catch { }
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
            // Editing: hide the 3-decimal overlay and reveal the real, full-precision value.
            if (TB_FrequencyDisplay != null)
                TB_FrequencyDisplay.Visibility = Visibility.Collapsed;
            TB_Frequency.Foreground = System.Windows.Media.Brushes.Black;
        }

        private void TB_Frequency_LostFocus(object sender, RoutedEventArgs e)
        {
            UpdateFrequencyDisplay();
        }

        // The old in-form overlay is superseded by the LED readout next to the Help menu. Keep it
        // permanently hidden and route updates to the LED instead.
        private void UpdateFrequencyDisplay()
        {
            if (TB_FrequencyDisplay != null)
                TB_FrequencyDisplay.Visibility = Visibility.Collapsed;
            UpdateFreqLed();
        }

        // Amber for the kHz integer part, soft white for the Hz (last three) — matching the reference
        // rig display. Cached + frozen so we don't rebuild brushes on every update.
        private static readonly System.Windows.Media.Brush LedAmberBrush =
            FreezeBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xB0, 0x00));
        private static readonly System.Windows.Media.Brush LedWhiteBrush =
            FreezeBrush(System.Windows.Media.Color.FromRgb(0xF0, 0xF0, 0xF0));

        private static System.Windows.Media.Brush FreezeBrush(System.Windows.Media.Color c)
        {
            var b = new System.Windows.Media.SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        // Renders TB_Frequency (stored in MHz) onto the 7-segment LED as kHz with 3 decimals, e.g.
        // 21.278520 MHz -> "21278.520". Display only — TB_Frequency keeps its MHz value untouched.
        private void UpdateFreqLed()
        {
            if (FreqLedLive == null || FreqLedGhost == null) return;
            // While the user is typing in the inline editor, leave the display alone.
            if (TB_FreqLedEdit != null && TB_FreqLedEdit.Visibility == Visibility.Visible) return;

            // In CAT (non-manual) mode the frequency must come live from the selected rig. If that
            // rig is not online (e.g. RIG2 selected but not present), there is no fresh value — show
            // a blanked "------.---" display instead of the previous rig's stale frequency.
            bool catEnabled = Properties.Settings.Default.EnableOmniRigCAT;
            bool manualMode = Properties.Settings.Default.isManualMode;
            bool rigOnline = catEnabled && OmniRigEngine != null && Rig != null
                             && Rig.Status == OmniRig.RigStatusX.ST_ONLINE;
            if (catEnabled && !manualMode && !rigOnline)
            {
                ShowLedBlank();
                return;
            }

            string raw = (TB_Frequency.Text ?? string.Empty).Trim();
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double mhz) || mhz <= 0)
            {
                ShowLedBlank();
                return;
            }

            long hz = (long)Math.Round(mhz * 1000000.0);
            string intPart = (hz / 1000).ToString(CultureInfo.InvariantCulture);  // kHz
            string fracPart = (hz % 1000).ToString("D3", CultureInfo.InvariantCulture); // Hz
            string full = intPart + "." + fracPart;

            // Ghost layer: every digit forced to 8 (all segments lit), dots kept in place.
            var ghost = new StringBuilder(full.Length);
            foreach (char c in full) ghost.Append(c == '.' ? '.' : '8');
            FreqLedGhost.Text = ghost.ToString();

            FreqLedLive.Inlines.Clear();
            FreqLedLive.Inlines.Add(new System.Windows.Documents.Run(intPart + ".") { Foreground = LedAmberBrush });
            FreqLedLive.Inlines.Add(new System.Windows.Documents.Run(fracPart) { Foreground = LedWhiteBrush });
        }

        // "No live frequency" state — dashes on the LED, with the dim all-segments ghost behind.
        private void ShowLedBlank()
        {
            if (FreqLedLive == null || FreqLedGhost == null) return;
            FreqLedGhost.Text = "888888.888";
            FreqLedLive.Inlines.Clear();
            FreqLedLive.Inlines.Add(new System.Windows.Documents.Run("------.---") { Foreground = LedAmberBrush });
        }

        // Click the LED to edit. Show an inline TextBox pre-filled with the current kHz value.
        private void FreqLed_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            string raw = (TB_Frequency.Text ?? string.Empty).Trim();
            string editVal = string.Empty;
            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double mhz) && mhz > 0)
            {
                long hz = (long)Math.Round(mhz * 1000000.0);
                editVal = (hz / 1000).ToString(CultureInfo.InvariantCulture) + "." + (hz % 1000).ToString("D3", CultureInfo.InvariantCulture);
            }

            FreqLedGhost.Visibility = Visibility.Collapsed;
            FreqLedLive.Visibility = Visibility.Collapsed;
            TB_FreqLedEdit.Text = editVal;
            TB_FreqLedEdit.Visibility = Visibility.Visible;
            TB_FreqLedEdit.Focus();
            TB_FreqLedEdit.SelectAll();
        }

        // Commit the inline edit: the editor is in kHz; convert back to MHz for TB_Frequency so the
        // stored format (and every consumer of it) stays exactly as before.
        private void CommitFreqLedEdit()
        {
            TB_FreqLedEdit.Visibility = Visibility.Collapsed;
            FreqLedGhost.Visibility = Visibility.Visible;
            FreqLedLive.Visibility = Visibility.Visible;

            string txt = (TB_FreqLedEdit.Text ?? string.Empty).Trim();
            if (double.TryParse(txt, NumberStyles.Float, CultureInfo.InvariantCulture, out double kHz) && kHz > 0)
            {
                double mhz = kHz / 1000.0;
                TB_Frequency.Text = mhz.ToString("0.0#####", CultureInfo.InvariantCulture);
            }
            UpdateFreqLed();
        }

        private void TB_FreqLedEdit_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                CommitFreqLedEdit();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Escape)
            {
                TB_FreqLedEdit.Visibility = Visibility.Collapsed;
                FreqLedGhost.Visibility = Visibility.Visible;
                FreqLedLive.Visibility = Visibility.Visible;
                UpdateFreqLed();
                e.Handled = true;
                return;
            }
            // Allow only digits and a single decimal point.
            bool isDigit = (e.Key >= Key.D0 && e.Key <= Key.D9) || (e.Key >= Key.NumPad0 && e.Key <= Key.NumPad9);
            bool isDot = e.Key == Key.OemPeriod || e.Key == Key.Decimal;
            if (isDot && TB_FreqLedEdit.Text.IndexOf('.') > -1) { e.Handled = true; return; }
            if (!isDigit && !isDot && e.Key != Key.Back && e.Key != Key.Delete &&
                e.Key != Key.Left && e.Key != Key.Right && e.Key != Key.Tab)
                e.Handled = true;
        }

        private void TB_FreqLedEdit_LostFocus(object sender, RoutedEventArgs e)
        {
            if (TB_FreqLedEdit.Visibility == Visibility.Visible)
                CommitFreqLedEdit();
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
            if (HolyMessageBox.ShowConfirm("Are you sure you want to clear the entire log?", "Delete Confirmation", HolyMsgType.Warning, this))
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
                    HolyMessageBox.ShowError("Backup failed: " + ex.Message, "Clear Log", this);
                }
                finally
                {
                    Properties.Settings.Default.RecentQSOCounter = 0;
                    Qsos.Clear();
                    dal.DeleteAll();
                    ClearBtn_Click(null, null);
                    UpdateNumOfQSOs();
                    UpdateEqslQueueIndicator();
                    UpdateQrzMenuCount();
                }
            }
            else
            {
                e.Handled = true;
            }            
        }

        // ── Autosave ──────────────────────────────────────────────────────────────

        private static readonly string AutosaveDir =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "autosave");

        private void SaveAutosnapshot()
        {
            try
            {
                if (dal == null) return;
                var qsos = dal.GetAllQSOs();
                if (qsos == null || qsos.Count == 0) return;

                Directory.CreateDirectory(AutosaveDir);
                string filename = Path.Combine(AutosaveDir,
                    "autosave_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".adi");
                File.WriteAllText(filename, Services.GenerateAdif(qsos), Encoding.UTF8);

                PruneAutosaves(AutosaveDir);
            }
            catch { }
        }

        private static void PruneAutosaves(string dir)
        {
            try
            {
                var files = Directory.GetFiles(dir, "autosave_*.adi")
                    .Select(f => new FileInfo(f))
                    .OrderByDescending(f => f.LastWriteTime)
                    .ToList();

                if (files.Count <= 5) return;

                var cutoff = DateTime.Now.AddDays(-10);
                foreach (var f in files.Skip(5))
                {
                    if (f.LastWriteTime < cutoff)
                        try { f.Delete(); } catch { }
                }
            }
            catch { }
        }

        private void ImportAutosaveMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Import Autosaved Log",
                Filter = "ADIF files (*.adi;*.adif)|*.adi;*.adif|All files (*.*)|*.*",
                FilterIndex = 1
            };
            if (Directory.Exists(AutosaveDir))
                dlg.InitialDirectory = AutosaveDir;

            if (dlg.ShowDialog() != true) return;

            int existing = 0;
            try { if (dal != null) existing = dal.GetQsoCount(); } catch { }

            if (!HolyMessageBox.ShowConfirm(
                $"This will REPLACE your current log ({existing} QSO(s)) with the selected autosave file.\n\nAre you sure?",
                "Import Autosaved Log", HolyMsgType.Warning, this))
                return;

            Properties.Settings.Default.RecentQSOCounter = 0;
            Qsos.CollectionChanged -= Qsos_CollectionChanged;
            Qsos.Clear();
            Qsos.CollectionChanged += Qsos_CollectionChanged;
            dal.DeleteAll();
            ClearBtn_Click(null, null);
            UpdateNumOfQSOs();
            UpdateEqslQueueIndicator();
            UpdateQrzMenuCount();

            ImportFileQ.Clear();
            ImportFileQ.Add(dlg.FileName);
            StartAdifImportWorker();
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

        private void SearchMenuItem_Click(object sender, RoutedEventArgs e)
        {
            OpenSearchWindow();
        }

        // Opens the Search window (or re-activates the existing one). If a callsign is supplied
        // (e.g. from a log-row right-click), the Callsign box is pre-filled so the user only has
        // to press Search.
        private void OpenSearchWindow(string presetCallsign = null)
        {
            if (searchWindow != null && searchWindow.IsLoaded)
            {
                if (!string.IsNullOrWhiteSpace(presetCallsign))
                    searchWindow.SetCallsign(presetCallsign, runSearch: true);
                searchWindow.Activate();
                return;
            }
            searchWindow = new SearchWindow(Qsos);
            searchWindow.Closed += (s, _) => searchWindow = null;
            searchWindow.Show();
            if (!string.IsNullOrWhiteSpace(presetCallsign))
                searchWindow.SetCallsign(presetCallsign, runSearch: true);
        }

        private void StatisticsMenuItem_Click(object sender, RoutedEventArgs e)
        {
            OpenStatisticsWindow();
        }

        // Opens the Statistics window (or re-activates the existing one). Single-instance like the
        // Search window; it gets the full QSO collection to compute stats from.
        private void OpenStatisticsWindow()
        {
            if (statisticsWindow != null && statisticsWindow.IsLoaded)
            {
                statisticsWindow.Activate();
                return;
            }
            statisticsWindow = new StatisticsWindow(Qsos);
            statisticsWindow.Dal = dal;
            statisticsWindow.Closed += (s, _) => statisticsWindow = null;
            statisticsWindow.Show();
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

            double savedLeft = Properties.Settings.Default.OptionsWindowLeft;
            double savedTop  = Properties.Settings.Default.OptionsWindowTop;

            if (savedLeft <= 0 && savedTop <= 0)
            {
                options.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }
            else
            {
                // Use the screen that contains the saved position (multi-monitor aware).
                var screen = System.Windows.Forms.Screen.FromPoint(
                    new System.Drawing.Point((int)savedLeft, (int)savedTop));
                var wa = screen.WorkingArea;
                options.Left = Math.Max(wa.Left, Math.Min(savedLeft, wa.Right  - options.Width));
                options.Top  = Math.Max(wa.Top,  Math.Min(savedTop,  wa.Bottom - options.Height));
            }

            // Subscribe to graphics box mode changes for immediate refresh
            options.UserInterfaceControlInstance.GraphicsBoxModeChanged += UserInterfaceControl_GraphicsBoxModeChanged;
            // Refresh the QRZ icon as soon as the user tests the connection in QRZ Service options.
            options.QRZServicesControlInstance.ConnectionTested += QRZServiceControl_ConnectionTested;
            // Give the LoTW control access to the database so it can reset the upload queue.
            options.LotwControlInstance.Dal = dal;
            options.LotwControlInstance.LotwQueueChanged += UpdateLotwMenuCount;
            options.EqslServiceControlInstance.EqslQueueChanged += UpdateEqslQueueIndicator;
            options.QRZServicesControlInstance.QrzQueueChanged += UpdateQrzMenuCount;

            options.Show();
        }

        private void UserInterfaceControl_GraphicsBoxModeChanged(object sender, EventArgs e)
        {
            // Immediately refresh graphics box display when settings change
            UpdateGraphicsBoxDisplay();
        }

        // Fired when the user presses "Test Connection" in QRZ Service options: light or gray the QRZ
        // icon to match the result, and reuse the freshly obtained session key.
        private void QRZServiceControl_ConnectionTested(bool success, string sessionKey)
        {
            if (success && !string.IsNullOrWhiteSpace(sessionKey))
                _SessionKey = sessionKey;
            SetQrzConnected(success);
        }

        private async void Options_Closed(object sender, EventArgs e)
        {
            OptionsWindow optionWindow = (OptionsWindow)sender;
            if(optionWindow.QRZServicesControlInstance.HasChanged)
            {
                _SessionKey = isNetworkAvailable ? await Helper.LoginToQRZAsync() : "";
                // Refresh the QRZ icon to reflect the (possibly corrected) credentials.
                SetQrzConnected(isNetworkAvailable && !string.IsNullOrWhiteSpace(_SessionKey));
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
                }
                this.Title = title;
                UpdateTitleClock();

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
                    HolyMessageBox.ShowWarning("Failed to open UDP port.", "UDP Client", this);
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
                    HolyMessageBox.ShowWarning("Failed to open N1MM+ UDP port.", "N1MM+ UDP Client", this);
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

            // The eQSL accounts table may have changed (a callsign added/removed). Re-evaluate the "!"
            // badge so QSOs whose callsign just became listed show up (or removed ones disappear)
            // immediately, without waiting for the next refresh.
            UpdateEqslQueueIndicator();
            UpdateQrzMenuCount();

            // Save the window position so it reopens in the same place next time.
            Properties.Settings.Default.OptionsWindowLeft = (int)optionWindow.Left;
            Properties.Settings.Default.OptionsWindowTop  = (int)optionWindow.Top;
            Properties.Settings.Default.Save();
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
                HolyMessageBox.Show("Please install 'Chrome' and try again.", "HolyLogger", HolyMsgType.Info, this);
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
                HolyMessageBox.Show("Please install 'Chrome' and try again.", "HolyLogger", HolyMsgType.Info, this);
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

            // Update the Visible setting when user opens cluster from View menu
            Properties.Settings.Default.ShowClusterWindowOption = true;
            try { Properties.Settings.Default.Save(); } catch { }

            // Refresh the settings dialog if it's open
            var optionsWindow = Application.Current.Windows.OfType<OptionsWindow>().FirstOrDefault();
            optionsWindow?.RefreshClusterSettings();

            GenerateNewClusterWindow();
        }

        private async void GenerateNewClusterWindow()
        {
            clusterHoverPopupEnabled = LoadClusterHoverPopupSetting();
            clusterLastMinutesFilterValue = LoadClusterLastMinutesFilterSetting();

            var undoButton = BuildClusterUndoButton();

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
            var headerGrid = BuildClusterHeaderPanel(undoButton);
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
                Width = Properties.Settings.Default.ClusterWindowWidth > 0 ? Properties.Settings.Default.ClusterWindowWidth : 600,
                Height = Properties.Settings.Default.ClusterWindowHeight > 0 ? Properties.Settings.Default.ClusterWindowHeight : 400,
                MinWidth = 200,
                MinHeight = 260,
                Left = Properties.Settings.Default.ClusterWindowLeft,
                Top = Properties.Settings.Default.ClusterWindowTop,
                Content = layoutGrid
            };
            clusterWindow.Owner = this;

            // Ensure window is visible on screen
            EnsureClusterWindowOnScreen();

            clusterUndoButton = undoButton;
            clusterUndoStates.Clear();
            UpdateClusterUndoButtonState();

            undoButton.Click += ClusterUndoButton_Click;
            // Long press (hold) clears the whole cluster undo stack, same as the log undo icon.
            undoButton.PreviewMouseLeftButtonDown += ClusterUndoButton_PreviewMouseLeftButtonDown;
            undoButton.PreviewMouseLeftButtonUp += ClusterUndoButton_PreviewMouseLeftButtonUp;
            undoButton.MouseLeave += ClusterUndoButton_MouseLeave;

            clusterWindow.LocationChanged += ClusterWindow_LocationChanged;
            clusterWindow.SizeChanged += ClusterWindow_SizeChanged;
            clusterWindow.Closed += ClusterWindow_Closed;
            clusterWindow.PreviewKeyDown += ForwardGlobalFunctionKeys;

            clusterWorkedCountries = GetWorkedCountriesFromLog();
            clusterWindow.Show();

            // Only start WebSocket if not already connected
            if (clusterWebSocketCts == null || clusterWebSocketCts.IsCancellationRequested)
            {
                await ConnectClusterWebSocketAsync(statusText, clusterVisibleSpots);
            }
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
            try { Properties.Settings.Default.Save(); } catch { }

            if (clusterSettingsWindow != null)
            {
                clusterSettingsWindow.Close();
                clusterSettingsWindow = null;
            }

            // Only close WebSocket and clear map if cluster is being deactivated, not just hidden
            if (!Properties.Settings.Default.ClusterActive)
            {
                CloseClusterWebSocket();
                ClearClusterSpotsFromMap();
                clusterVisibleSpots = null;
                clusterWorkedCountries = null;
            }

            try { _clusterWidthHandlerCleanup?.Invoke(); } catch { }
            _clusterWidthHandlerCleanup = null;
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
            clusterBandSelectorPanel = null;
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

        private void EnsureClusterWindowOnScreen()
        {
            if (clusterWindow == null)
                return;

            // Check if window position is valid (not off-screen or at invalid coordinates)
            bool needsRepositioning = false;

            // Get screen bounds
            var screenWidth = SystemParameters.VirtualScreenWidth;
            var screenHeight = SystemParameters.VirtualScreenHeight;
            var screenLeft = SystemParameters.VirtualScreenLeft;
            var screenTop = SystemParameters.VirtualScreenTop;

            // Check if window is completely off-screen or at invalid position
            if (clusterWindow.Left < screenLeft - clusterWindow.Width + 50 ||
                clusterWindow.Left > screenLeft + screenWidth - 50 ||
                clusterWindow.Top < screenTop - clusterWindow.Height + 50 ||
                clusterWindow.Top > screenTop + screenHeight - 50)
            {
                needsRepositioning = true;
            }

            // If invalid, position relative to main window
            if (needsRepositioning && this.IsLoaded)
            {
                clusterWindow.Left = this.Left + 50;
                clusterWindow.Top = this.Top + 50;
            }
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
                ToolTip = "Click to undo last spot tune • Hold to clear all",
                Margin = new Thickness(0, 0, 0, 8),
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
                //AlternatingRowBackground = Brushes.Gainsboro,
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

            // Create template via XAML string for reliability
            string templateXaml = @"
                <ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                                 xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
                                 TargetType='{x:Type DataGridColumnHeader}'>
                    <Grid>
                        <Border Background='{TemplateBinding Background}'
                                BorderBrush='{TemplateBinding BorderBrush}'
                                BorderThickness='{TemplateBinding BorderThickness}'>
                            <Grid>
                                <ContentPresenter HorizontalAlignment='{TemplateBinding HorizontalContentAlignment}'
                                                  VerticalAlignment='{TemplateBinding VerticalContentAlignment}'
                                                  Margin='{TemplateBinding Padding}' />
                                <Path x:Name='SortArrow'
                                      HorizontalAlignment='Center'
                                      VerticalAlignment='Top'
                                      Margin='0,0,0,0'
                                      Fill='#000000'
                                      Stretch='Uniform'
                                      Width='8'
                                      Height='6'
                                      Data='M 0,0 L 1,1 L 2,0 Z'
                                      Visibility='Collapsed'
                                      RenderTransformOrigin='0.5,0.5'>
                                    <Path.RenderTransform>
                                        <ScaleTransform ScaleY='1' />
                                    </Path.RenderTransform>
                                </Path>
                            </Grid>
                        </Border>
                        <Thumb x:Name='PART_LeftHeaderGripper' HorizontalAlignment='Left' Width='4' Cursor='SizeWE'>
                            <Thumb.Style>
                                <Style TargetType='Thumb'>
                                    <Setter Property='Background' Value='Transparent'/>
                                    <Setter Property='Template'>
                                        <Setter.Value>
                                            <ControlTemplate TargetType='Thumb'>
                                                <Border Background='{TemplateBinding Background}' Padding='0'/>
                                            </ControlTemplate>
                                        </Setter.Value>
                                    </Setter>
                                </Style>
                            </Thumb.Style>
                        </Thumb>
                        <Thumb x:Name='PART_RightHeaderGripper' HorizontalAlignment='Right' Width='4' Cursor='SizeWE'>
                            <Thumb.Style>
                                <Style TargetType='Thumb'>
                                    <Setter Property='Background' Value='Transparent'/>
                                    <Setter Property='Template'>
                                        <Setter.Value>
                                            <ControlTemplate TargetType='Thumb'>
                                                <Border Background='{TemplateBinding Background}' Padding='0'/>
                                            </ControlTemplate>
                                        </Setter.Value>
                                    </Setter>
                                </Style>
                            </Thumb.Style>
                        </Thumb>
                    </Grid>
                    <ControlTemplate.Triggers>
                        <Trigger Property='SortDirection' Value='Ascending'>
                            <Setter TargetName='SortArrow' Property='Visibility' Value='Visible' />
                            <Setter TargetName='SortArrow' Property='RenderTransform'>
                                <Setter.Value>
                                    <ScaleTransform ScaleY='1' />
                                </Setter.Value>
                            </Setter>
                        </Trigger>
                        <Trigger Property='SortDirection' Value='Descending'>
                            <Setter TargetName='SortArrow' Property='Visibility' Value='Visible' />
                            <Setter TargetName='SortArrow' Property='RenderTransform'>
                                <Setter.Value>
                                    <ScaleTransform ScaleY='-1' />
                                </Setter.Value>
                            </Setter>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>";

            var headerTemplate = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(templateXaml);
            clusterColumnHeaderStyle.Setters.Add(new Setter(Control.TemplateProperty, headerTemplate));
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
            var dxColumn = new DataGridTemplateColumn { Header = "DX", HeaderStyle = dxHeaderStyle, CellTemplate = dxColumnTemplate, SortMemberPath = "DXCallsign", Width = DataGridLength.Auto };

            // Spotter / Country columns
            var spotterColumn = new DataGridTextColumn { Header = "Spotter", HeaderStyle = clusterColumnHeaderStyle, Binding = new System.Windows.Data.Binding("SpotterCallsign"), Width = new DataGridLength(Math.Max(40, Properties.Settings.Default.ClusterColWidthSpotter)) };
            var countryColumn = new DataGridTextColumn { Header = "Country", HeaderStyle = clusterColumnHeaderStyle, Binding = new System.Windows.Data.Binding("Country"), Width = new DataGridLength(Math.Max(40, LoadClusterCountryColumnWidthSetting())) };

            // Freq column with band color
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

            var freqColumnTemplate = new DataTemplate();
            var freqTextBlockFactory = new FrameworkElementFactory(typeof(TextBlock));
            freqTextBlockFactory.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("FreqDisplayText"));
            freqTextBlockFactory.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding("FreqForeground"));
            freqTextBlockFactory.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold);
            freqColumnTemplate.VisualTree = freqTextBlockFactory;

            var freqColumn = new DataGridTemplateColumn { Header = freqHeaderText, HeaderStyle = freqHeaderStyle, CellTemplate = freqColumnTemplate, SortMemberPath = "FreqDisplayText", Width = DataGridLength.Auto };

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
            var modeColumn = new DataGridTemplateColumn { Header = "Mode", HeaderStyle = modeHeaderStyle, CellTemplate = modeTemplate, Width = DataGridLength.Auto };

            // Comment column
            var commentHeaderStyle = new Style(typeof(DataGridColumnHeader), clusterColumnHeaderStyle);
            commentHeaderStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
            var commentColumn = new DataGridTextColumn { Header = "Comment", HeaderStyle = commentHeaderStyle, Binding = new System.Windows.Data.Binding("Comment"), MinWidth = 60, Width = new DataGridLength(1, DataGridLengthUnitType.Star) };

            // Flag column
            var flagHeaderStyle = new Style(typeof(DataGridColumnHeader), clusterColumnHeaderStyle);
            flagHeaderStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
            var flagTemplate = new DataTemplate();
            var flagImageFactory = new FrameworkElementFactory(typeof(System.Windows.Controls.Image));
            flagImageFactory.SetBinding(System.Windows.Controls.Image.SourceProperty, new System.Windows.Data.Binding("FlagPath"));
            flagImageFactory.SetValue(System.Windows.Controls.Image.WidthProperty, 24.0);
            flagImageFactory.SetValue(System.Windows.Controls.Image.HeightProperty, 16.0);
            flagImageFactory.SetValue(System.Windows.Controls.Image.StretchProperty, System.Windows.Media.Stretch.Uniform);
            flagImageFactory.SetValue(System.Windows.Controls.Image.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            flagTemplate.VisualTree = flagImageFactory;
            var flagColumn = new DataGridTemplateColumn { Header = "Flag", HeaderStyle = flagHeaderStyle, CellTemplate = flagTemplate, Width = new DataGridLength(40), CanUserResize = false };

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
            spotsGrid.Columns.Add(flagColumn);
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

            if (clusterVisibleSpots == null)
            {
                clusterVisibleSpots = new ObservableCollection<ClusterSpotViewItem>();
            }
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

        private Grid BuildClusterHeaderPanel(Button undoButton)
        {
            var legendPanel = new StackPanel
            {
                Orientation = Orientation.Vertical,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, -6, 4, 0)
            };

            legendPanel.Children.Add(BuildClusterLegendTopRow());
            legendPanel.Children.Add(BuildClusterLegendItem(new SolidColorBrush(Color.FromRgb(0x00, 0x7A, 0xCC)), "Worked Before", false, new Thickness(0, 7, 0, 0)));
            legendPanel.Children.Add(BuildClusterLegendItem(Brushes.Black, "Worked Country", false, new Thickness(0, 5, 0, 0)));

            var onMyFreqLegend = BuildClusterLegendItem(new SolidColorBrush(Color.FromRgb(0x90, 0xEE, 0x90)), "On My Radio Freq", true, new Thickness(0, 7, 0, 0));
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

            // Add band selector panel
            var bandSelectorPanel = BuildClusterBandSelectorPanel();
            bandSelectorPanel.Margin = new Thickness(0, -12, 0, 0);
            bandSelectorPanel.HorizontalAlignment = HorizontalAlignment.Right;
            bandSelectorPanel.VerticalAlignment = VerticalAlignment.Center;
            clusterBandSelectorPanel = bandSelectorPanel;

            // Add mode selector panel below band selector
            var modeSelectorPanel = BuildClusterModeSelectorPanel();
            modeSelectorPanel.Margin = new Thickness(0, -9, 34, 0);
            modeSelectorPanel.HorizontalAlignment = HorizontalAlignment.Right;
            modeSelectorPanel.VerticalAlignment = VerticalAlignment.Center;

            var actionsPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, -8, 0, 0)
            };
            actionsPanel.Children.Add(bandSelectorPanel);
            actionsPanel.Children.Add(undoButton);

            var rightColumnPanel = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 0, -12, 0)
            };
            rightColumnPanel.Children.Add(actionsPanel);
            rightColumnPanel.Children.Add(modeSelectorPanel);

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
                Margin = new Thickness(0, 8, 0, 1)
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
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = itemMargin ?? new Thickness(0, 0, 0, 1)
            };

            var itemText = new TextBlock
            {
                Text = text,
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Foreground = useTextBackground ? Brushes.Black : color,
                Background = useTextBackground ? color : Brushes.Transparent,
                Padding = useTextBackground ? new Thickness(3, 0, 3, 0) : new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            itemPanel.Children.Add(itemText);

            if (string.Equals(text, "New Country", StringComparison.Ordinal))
            {
                clusterNewCountryLegendText = itemText;
            }

            return itemPanel;
        }

        private StackPanel BuildClusterBandSelectorPanel()
        {
            var enabledBands = GetEnabledClusterBands();
            var bandColors = GetBandColors();

            // Single horizontal row with ALL bands
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 0)
            };

            // All bands in order from left to right: SHF, UHF, VHF, 6, 10, 12, 15, 17, 20, 30, 40, 60, 80, 160
            string[] allBands = { "SHF", "UHF", "VHF", "6", "10", "12", "15", "17", "20", "30", "40", "60", "80", "160" };

            foreach (string band in allBands)
            {
                string colorHex = bandColors.ContainsKey(band) ? bandColors[band] : "#FF6600";
                Color color;
                try { color = (Color)ColorConverter.ConvertFromString(colorHex); }
                catch { color = Colors.OrangeRed; }

                var bandCheckBox = BuildBandCheckBox(band, color, enabledBands.Contains(band));
                row.Children.Add(bandCheckBox);
            }

            return row;
        }

        private StackPanel BuildClusterModeSelectorPanel()
        {
            var enabledModes = GetEnabledClusterModes();

            // If no modes are enabled, enable all by default
            if (enabledModes.Count == 0)
            {
                enabledModes = new HashSet<string>(ClusterModeOptions, StringComparer.OrdinalIgnoreCase);
                SaveEnabledClusterModes(enabledModes);
            }

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(3, 3, 3, 3)
            };

            // Mode list in order: SSB, CW, FT8, DIGI, RTTY, FM, AM
            string[] allModes = { "SSB", "CW", "FT8", "DIGI", "RTTY", "FM", "AM" };

            foreach (string mode in allModes)
            {
                var modeCheckBox = BuildModeCheckBox(mode, enabledModes.Contains(mode));
                row.Children.Add(modeCheckBox);
            }

            return row;
        }

        private StackPanel BuildModeCheckBox(string mode, bool isChecked)
        {
            var modeText = new TextBlock
            {
                Text = mode,
                FontSize = 9,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.Black,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(1,1,1,1)
            };

            var checkBox = new CheckBox
            {
                Width = 15,
                Height = 15,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 2, 2, 2),
                Padding = new Thickness(4),
                IsChecked = isChecked,
                Tag = mode
            };

            bool isUpdating = false;

            checkBox.Checked += (s, e) =>
            {
                if (isUpdating) return;
                var enabledModes = GetEnabledClusterModes();
                if (!enabledModes.Contains(mode))
                {
                    enabledModes.Add(mode);
                    SaveEnabledClusterModes(enabledModes);
                    RefreshClusterVisibleSpots();
                }
            };

            checkBox.Unchecked += (s, e) =>
            {
                if (isUpdating) return;
                var enabledModes = GetEnabledClusterModes();
                if (enabledModes.Contains(mode))
                {
                    // Prevent unchecking the last selected mode
                    if (enabledModes.Count <= 1)
                    {
                        isUpdating = true;
                        checkBox.IsChecked = true;
                        isUpdating = false;
                        return;
                    }
                    enabledModes.Remove(mode);
                    SaveEnabledClusterModes(enabledModes);
                    RefreshClusterVisibleSpots();
                }
            };

            var modeIndicator = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(1, 0, 1, 0)
            };
            modeIndicator.Children.Add(modeText);
            modeIndicator.Children.Add(checkBox);

            return modeIndicator;
        }

        private StackPanel BuildBandCheckBox(string band, Color color, bool isChecked)
        {
            var bandText = new TextBlock
            {
                Text = band,
                FontSize = 9,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.Black,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(1, 4, 1, 1)
            };

            var checkBox = new CheckBox
            {
                Width = 15,
                Height = 15,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 2, 2, 2),
                Padding = new Thickness(4),
                IsChecked = isChecked,
                Tag = band
            };

            // Custom template for the checkbox
            var checkBoxTemplate = new ControlTemplate(typeof(CheckBox));
            var templateFactory = new FrameworkElementFactory(typeof(Border));
            templateFactory.SetValue(Border.WidthProperty, 14.0);
            templateFactory.SetValue(Border.HeightProperty, 14.0);
            templateFactory.SetValue(Border.BackgroundProperty, new SolidColorBrush(color));
            templateFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(2));
            templateFactory.SetValue(Border.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            templateFactory.SetValue(Border.VerticalAlignmentProperty, VerticalAlignment.Top);
            templateFactory.SetValue(Border.MarginProperty, new Thickness(0, -2, 0, 0));

            // Add checkmark (white text "✓")
            var checkMarkFactory = new FrameworkElementFactory(typeof(TextBlock));
            checkMarkFactory.SetValue(TextBlock.TextProperty, "✓");
            checkMarkFactory.SetValue(TextBlock.ForegroundProperty, Brushes.White);
            checkMarkFactory.SetValue(TextBlock.FontSizeProperty, 10.0);
            checkMarkFactory.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold);
            checkMarkFactory.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            checkMarkFactory.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            checkMarkFactory.SetValue(TextBlock.MarginProperty, new Thickness(0, -1, 0, 0));
            checkMarkFactory.SetValue(TextBlock.VisibilityProperty, Visibility.Collapsed);
            checkMarkFactory.Name = "CheckMark";

            templateFactory.AppendChild(checkMarkFactory);
            checkBoxTemplate.VisualTree = templateFactory;

            // Add trigger to show checkmark when checked
            var trigger = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
            trigger.Setters.Add(new Setter(TextBlock.VisibilityProperty, Visibility.Visible, "CheckMark"));
            checkBoxTemplate.Triggers.Add(trigger);

            checkBox.Template = checkBoxTemplate;

            // Handle checkbox changes
            checkBox.Checked += (s, e) =>
            {
                var enabledBands = GetEnabledClusterBands();
                if (!enabledBands.Contains(band))
                {
                    enabledBands.Add(band);
                    SaveEnabledClusterBands(enabledBands);
                    RefreshClusterVisibleSpots();
                }
            };

            checkBox.Unchecked += (s, e) =>
            {
                var enabledBands = GetEnabledClusterBands();
                if (enabledBands.Contains(band))
                {
                    // Prevent unchecking the last selected band
                    if (enabledBands.Count <= 1)
                    {
                        checkBox.IsChecked = true;
                        return;
                    }
                    enabledBands.Remove(band);
                    SaveEnabledClusterBands(enabledBands);
                    RefreshClusterVisibleSpots();
                }
            };

            var bandIndicator = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 2, 0),
                Tag = band  // Store band name for right-click handler
            };
            // Wrap the checkbox in a cell so a circle can appear around it on hover.
            var checkBoxCell = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            var hoverCircle = new System.Windows.Shapes.Ellipse
            {
                Width = 23,
                Height = 23,
                Stroke = new SolidColorBrush(color),
                StrokeThickness = 2.5,
                Fill = Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                // Nudge the circle up so it's centered on the checkbox (whose colored square sits
                // toward the top of its cell), instead of sitting slightly below it.
                Margin = new Thickness(0, -4, 0, 0),
                IsHitTestVisible = false,
                Visibility = Visibility.Hidden
            };
            checkBoxCell.Children.Add(hoverCircle);
            checkBoxCell.Children.Add(checkBox);

            bandIndicator.Children.Add(bandText);
            bandIndicator.Children.Add(checkBoxCell);

            // A transparent background makes the whole cell a reliable hover target.
            bandIndicator.Background = Brushes.Transparent;

            // Hovering a band momentarily previews it: a circle appears around the checkbox and the
            // cluster table + map show ONLY this band's spots while the mouse is over it; leaving the
            // cell hides the circle and restores whatever was showing before.
            bandIndicator.MouseEnter += (s, e) =>
            {
                hoverCircle.Visibility = Visibility.Visible;
                _clusterHoverBandOverride = band;
                RefreshClusterVisibleSpots();
            };
            bandIndicator.MouseLeave += (s, e) =>
            {
                hoverCircle.Visibility = Visibility.Hidden;
                if (string.Equals(_clusterHoverBandOverride, band, StringComparison.OrdinalIgnoreCase))
                {
                    _clusterHoverBandOverride = null;
                    RefreshClusterVisibleSpots();
                }
            };

            // Add right-click handler for color editing
            bandIndicator.MouseRightButtonDown += (s, e) =>
            {
                e.Handled = true;
                EditBandColor(band);
            };

            return bandIndicator;
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

            var btnAllBands = new Button { Content = "All Bands", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(4, 2, 2, 4), Style = MakeClusterBandFilterBtnStyle(string.Equals(currentFilterMode, "All", StringComparison.OrdinalIgnoreCase)) };
            var btnPreSelected = new Button { Content = "Selected", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(4, 2, 2, 4), Style = MakeClusterBandFilterBtnStyle(string.Equals(currentFilterMode, "PreSelected", StringComparison.OrdinalIgnoreCase)) };
            var btnActiveBand = new Button { Content = "Active Band", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(4, 2, 2, 4), Style = MakeClusterBandFilterBtnStyle(string.Equals(currentFilterMode, "Active", StringComparison.OrdinalIgnoreCase)) };

            clusterBandFilterAllBtn = btnAllBands;
            clusterBandFilterPreSelectedBtn = btnPreSelected;
            clusterBandFilterActiveBtn = btnActiveBand;

            // Use a Grid with two fixed-height rows so the buttons are completely independent —
            // hiding Active Band never shifts Selected or All Bands regardless of when it happens.
            var grid = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top
            };
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });

            var topButtonsRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            topButtonsRow.Children.Add(btnPreSelected);
            topButtonsRow.Children.Add(btnAllBands);
            Grid.SetRow(topButtonsRow, 0);
            grid.Children.Add(topButtonsRow);

            var activeBandRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            activeBandRow.Children.Add(btnActiveBand);
            activeBandRow.Children.Add(activeBandIndicator);
            Grid.SetRow(activeBandRow, 1);
            grid.Children.Add(activeBandRow);

            var wrapper = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top
            };
            wrapper.Children.Add(grid);
            clusterShowBandsPanel = wrapper;

            // User-initiated clicks record the preferred mode (persisted across restarts)
            btnAllBands.Click += (s, e) => ApplyClusterBandFilterMode("All", true);
            btnPreSelected.Click += (s, e) => ApplyClusterBandFilterMode("PreSelected", true);
            btnActiveBand.Click += (s, e) => ApplyClusterBandFilterMode("Active", true);

            // Apply initial state: sets button visibility, falls back if out of band, and
            // restores the user's preferred Active mode if a legal band is already present.
            UpdateActiveBandButtonVisibility();

            return wrapper;
        }

        private void ApplyClusterBandFilterMode(string newMode, bool userInitiated = false)
        {
            Properties.Settings.Default.ClusterBandFilterMode = newMode;
            Properties.Settings.Default.ClusterUseActiveBand = string.Equals(newMode, "Active", StringComparison.OrdinalIgnoreCase);
            // Only an explicit user click records the *preferred* mode. Automatic fallbacks
            // (e.g. when the radio leaves a legal band) must not overwrite the user's intent,
            // so that Active mode is restored — even across program restarts — when a legal band returns.
            if (userInitiated)
                Properties.Settings.Default.ClusterPreferredBandMode = newMode;
            try { Properties.Settings.Default.Save(); } catch { }
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

        private void UpdateActiveBandButtonVisibility()
        {
            if (clusterBandFilterActiveBtn == null)
                return;

            string activeBand = TB_Band != null ? TB_Band.Text : string.Empty;
            bool bandIsValid = !string.IsNullOrWhiteSpace(activeBand);

            // Hide only the button and label — never the row itself, so layout never shifts
            var vis = bandIsValid ? Visibility.Visible : Visibility.Hidden;
            clusterBandFilterActiveBtn.Visibility = vis;
            if (clusterActiveBandIndicatorText != null)
                clusterActiveBandIndicatorText.Visibility = vis;

            string preferred = Properties.Settings.Default.ClusterPreferredBandMode ?? "PreSelected";
            string current = Properties.Settings.Default.ClusterBandFilterMode ?? "PreSelected";

            if (!bandIsValid)
            {
                // Band invalid: if currently in Active mode, fall back. The user's preferred
                // mode (persisted) is left untouched so Active can be restored later.
                if (string.Equals(current, "Active", StringComparison.OrdinalIgnoreCase))
                {
                    string fallback = GetEnabledClusterBands().Count > 0 ? "PreSelected" : "All";
                    ApplyClusterBandFilterMode(fallback);
                }
            }
            else
            {
                // Band valid: restore Active if it is the user's persisted preference.
                // This also covers the case where the program was restarted while out of band.
                if (string.Equals(preferred, "Active", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(current, "Active", StringComparison.OrdinalIgnoreCase))
                {
                    ApplyClusterBandFilterMode("Active");
                }
            }
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
                Margin = new Thickness(0, 0, 0, -3)
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

            var spotCountBadge = new Border
            {
                Width = 34,
                Height = 34,
                CornerRadius = new CornerRadius(17),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x8C, 0x00)),
                BorderThickness = new Thickness(2),
                Background = Brushes.Transparent,
                Margin = new Thickness(4, -6, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Child = clusterSpotCountText
            };

            var lastMinutesValuePanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            };
            lastMinutesValuePanel.Children.Add(lastMinutesCombo);
            lastMinutesValuePanel.Children.Add(minutesUnitLabel);
            lastMinutesValuePanel.Children.Add(spotCountBadge);

            var lastMinutesFilterPanel = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top
            };
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
                        // Use the indicator's stable X-within-panel plus a FIXED half-width, so the
                        // panel's horizontal position never depends on the band text width (or its
                        // absence when the band is illegal). This keeps Selected/All Bands fixed.
                        Point indicatorTopInShow = clusterActiveBandIndicatorText.TranslatePoint(new Point(0, 0), clusterShowBandsPanel);
                        indicatorCenterOffset = indicatorTopInShow.X + ClusterBandIndicatorHalfWidth;
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
                ClearClusterMapHover();
                return;
            }

            // Enlarge the hovered DX station's dot on the map — only while hovering the DX callsign
            // column (not the other columns of the row). Only refresh when the callsign changes.
            if (cell.Column == clusterDxColumn)
            {
                string hoveredCall = (cell.DataContext as ClusterSpotViewItem)?.DXCallsign;
                if (!string.IsNullOrEmpty(hoveredCall))
                {
                    if (hoveredCall != _lastHoveredSpotCall)
                    {
                        _lastHoveredSpotCall = hoveredCall;
                        try { MapControl?.HighlightSpot(hoveredCall); } catch { }
                    }
                }
                else
                {
                    ClearClusterMapHover();
                }
            }
            else
            {
                ClearClusterMapHover();
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
            ClearClusterMapHover();
        }

        // Restores all map spot dots to normal size once the hover leaves a row / the cluster grid.
        private void ClearClusterMapHover()
        {
            if (_lastHoveredSpotCall != null)
            {
                _lastHoveredSpotCall = null;
                try { MapControl?.ClearSpotHighlight(); } catch { }
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
            try { Properties.Settings.Default.Save(); } catch { }
        }

        // Cluster settings window removed - settings now in cluster header and main User Interface settings
        // private void OpenClusterSettingsWindow() { ... }

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

                    if (statusText != null)
                    {
                        statusText.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            statusText.Text = "(connected)";
                            statusText.Foreground = new SolidColorBrush(Color.FromRgb(0, 190, 0));
                        }));
                    }

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
                if (statusText != null)
                {
                    statusText.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        statusText.Text = "(reconnecting...)";
                        statusText.Foreground = Brushes.Orange;
                    }));
                }

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

            if (statusText != null)
            {
                statusText.Dispatcher.BeginInvoke(new Action(() =>
                {
                    statusText.Text = "(disconnected)";
                    statusText.Foreground = Brushes.Red;
                }));
            }
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
                            if (statusText != null)
                            {
                                statusText.Dispatcher.BeginInvoke(new Action(() =>
                                {
                                    statusText.Text = "(disconnected)";
                                    statusText.Foreground = Brushes.Red;
                                }));
                            }
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

        private string GetFlagPathFromCountryName(string countryName)
        {
            if (string.IsNullOrWhiteSpace(countryName))
            {
                return null;
            }
            if (DxccNameToIso.TryGetValue(countryName, out string isoCode))
            {
                return string.Format("pack://application:,,,/Images/flags/{0}.png", isoCode);
            }
            return null;
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

                // Use the cached worked-countries set (rebuilt only when the log changes) instead of
                // rescanning all ~11k QSOs on every payload. Also build an O(1) lookup of logged DX
                // callsigns ONCE per payload, so the per-spot "in log?" test is a hash lookup instead
                // of a linear scan of the entire log for every single spot. With a big log this is the
                // difference between the UI thread freezing on each spot batch and staying responsive.
                var workedCountries = clusterWorkedCountries ?? GetWorkedCountriesFromLog();
                var loggedDxCalls = BuildLoggedDxCallSet();

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
                    string countryName = dxccInfo != null ? dxccInfo.Name : string.Empty;
                    string flagPath = GetFlagPathFromCountryName(countryName);
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
                        Country = countryName,
                        FlagPath = flagPath,
                        IsInLog = !string.IsNullOrWhiteSpace(dx) && loggedDxCalls.Contains(dx.Trim()),
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
            public string FlagPath { get; set; }
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

            public Brush FreqForeground
            {
                get
                {
                    try
                    {
                        string bandText = (BandText ?? string.Empty).Trim();
                        if (string.IsNullOrWhiteSpace(bandText))
                            return Brushes.Black;

                        // Resolve through the same band-color source as the band checkboxes and the
                        // map spot dots (defaults + user customizations, normalized band key) so the
                        // Freq color always matches the band selection checkbox exactly.
                        string colorHex = GetBandColor(bandText);
                        return (Brush)new BrushConverter().ConvertFromString(colorHex);
                    }
                    catch
                    {
                        return Brushes.Black;
                    }
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

            public Brush DXBackground
            {
                get
                {
                    if (IsMyCallsign)
                    {
                        return new SolidColorBrush(Color.FromRgb(0x00, 0x33, 0x99));
                    }

                    return Brushes.Transparent;
                }
            }

            private bool _isMapHovered;
            // Set true while the user hovers this station's dot on the map, so the row is shown
            // with a blue background.
            public bool IsMapHovered
            {
                get => _isMapHovered;
                set
                {
                    if (_isMapHovered != value)
                    {
                        _isMapHovered = value;
                        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsMapHovered)));
                        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RowBackground)));
                    }
                }
            }

            public Brush RowBackground
            {
                get
                {
                    if (IsMapHovered)
                    {
                        return new SolidColorBrush(Color.FromRgb(0x90, 0xCA, 0xF9)); // Blue highlight (map hover)
                    }
                    if (IsOnFrequency)
                    {
                        return new SolidColorBrush(Color.FromRgb(0x90, 0xEE, 0x90)); // LightGreen
                    }
                    else
                    {
                        return Brushes.Transparent;
                    }
                }
            }

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

        // Builds a case-insensitive set of all DX callsigns currently in the log, so cluster spot
        // processing can test "is this call already logged?" in O(1) instead of scanning the whole
        // log per spot. Built once per cluster payload on the UI thread (a single ~11k pass is cheap).
        private HashSet<string> BuildLoggedDxCallSet()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var qsos = Qsos;
            if (qsos != null)
            {
                foreach (var q in qsos)
                {
                    string c = (q.DXCall ?? string.Empty).Trim();
                    if (c.Length > 0)
                        set.Add(c);
                }
            }
            return set;
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
        }

        private static readonly string[] ClusterBandOptions = new[] { "160", "80", "60", "40", "30", "20", "17", "15", "12", "10", "6", "VHF", "UHF", "SHF" };
        private static readonly string[] ClusterModeOptions = new[] { "CW", "DIGI", "SSB", "FM", "FT8", "RTTY", "AM" };

        private static readonly Dictionary<string, string> DefaultBandColors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "160", "#156184" }, { "80", "#903727" }, { "60", "#152F47" }, { "40", "#18A018" },
            { "30", "#F1E00A" }, { "20", "#DC2828" }, { "17", "#751F6B" }, { "15", "#1515CB" },
            { "12", "#47DFF0" }, { "10", "#E87421" }, { "6",  "#FF61EA" },
            { "VHF", "#5EFFA0" }, { "UHF", "#5ECFFF" }, { "SHF", "#A07CFF" }
        };

        private static Dictionary<string, string> _bandColorCache = null;

        // Single source of truth for band colors: built-in defaults overridden by any colors the
        // user customised via the band-selection checkboxes (stored in ClusterBandColors). The band
        // checkboxes, the cluster list Freq color, and the map spot dots all resolve through here so
        // they always show the exact same color per band.
        private static Dictionary<string, string> GetBandColors()
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

        // Resolves a raw band string (e.g. "40", "40M", "70CM") to its color, normalizing it to the
        // same key the band checkboxes use so the colors match exactly.
        private static string GetBandColor(string band)
        {
            var colors = GetBandColors();
            string key = NormalizeClusterBandKey(band);
            if (!string.IsNullOrEmpty(key) && colors.TryGetValue(key, out string c)) return c;
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

        private void EditBandColor(string band)
        {
            if (string.IsNullOrWhiteSpace(band)) return;

            var colors = GetBandColors();
            string currentColorHex = colors.ContainsKey(band) ? colors[band] : "#FF6600";

            // Show color picker dialog
            string newColorHex = PickColorHex(currentColorHex);
            if (string.IsNullOrWhiteSpace(newColorHex)) return; // User cancelled

            // Update the color
            colors[band] = newColorHex;
            SaveBandColors(colors);

            // Rebuild the band selector panel to show the new color
            RebuildClusterBandSelector();

            // Repaint everything already on screen with the new color instead of waiting for the
            // next spot to arrive: the cluster list's Freq color (FreqForeground is re-evaluated on
            // refresh) and the map spot dots.
            try { clusterSpotsDataGrid?.Items.Refresh(); } catch { }
            if (Properties.Settings.Default.ClusterMapEnabled
                && MapControl != null && MapControl.Visibility == Visibility.Visible)
            {
                DoUpdateClusterSpotsOnMap();
            }
        }

        private static string PickColorHex(string currentHex)
        {
            Color current;
            try { current = (Color)ColorConverter.ConvertFromString(currentHex); }
            catch { current = Colors.OrangeRed; }

            using (var dlg = new System.Windows.Forms.ColorDialog())
            {
                dlg.AllowFullOpen = true;
                dlg.FullOpen = true;
                dlg.Color = System.Drawing.Color.FromArgb(current.A, current.R, current.G, current.B);

                if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                {
                    return null;
                }

                return string.Format("#{0:X2}{1:X2}{2:X2}", dlg.Color.R, dlg.Color.G, dlg.Color.B);
            }
        }

        private void RebuildClusterBandSelector()
        {
            if (clusterWindow == null || clusterBandSelectorPanel == null) return;

            // Find the parent container
            var parent = clusterBandSelectorPanel.Parent as Panel;
            if (parent == null) return;

            int index = parent.Children.IndexOf(clusterBandSelectorPanel);
            if (index < 0) return;

            // Remove old panel
            parent.Children.RemoveAt(index);

            // Create new panel with updated colors
            var newPanel = BuildClusterBandSelectorPanel();
            newPanel.Margin = clusterBandSelectorPanel.Margin;
            newPanel.HorizontalAlignment = clusterBandSelectorPanel.HorizontalAlignment;

            // Insert at same position
            parent.Children.Insert(index, newPanel);

            // Update reference
            clusterBandSelectorPanel = newPanel;
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

        private static string NormalizeClusterBandKey(string bandText)
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

            // While the mouse is hovering a band checkbox, momentarily show ONLY that band's spots,
            // overriding whatever filter mode/selection is normally in effect.
            if (!string.IsNullOrEmpty(_clusterHoverBandOverride))
                return string.Equals(NormalizeClusterBandKey(_clusterHoverBandOverride), normalized, StringComparison.OrdinalIgnoreCase);

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
            // Don't auto-fill here - let the save function handle empty sets
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
                            Band = NormalizeClusterBandKey(spot.BandText)
                        });
                    }
                }

                MapControl.ShowClusterSpots(spots, homell.Lat, homell.Long, GetMapRadiusKm());
            }
            catch
            {
            }
        }

        private void ClearClusterSpotsFromMap()
        {
            if (MapControl == null)
                return;

            try
            {
                var emptySpots = new System.Collections.Generic.List<HolyLogger.ToolsUserControls.ClusterSpotInfo>();
                if (!string.IsNullOrWhiteSpace(TB_MyLocator.Text))
                {
                    var homell = MaidenheadLocator.LocatorToLatLng(TB_MyLocator.Text);
                    MapControl.ShowClusterSpots(emptySpots, homell.Lat, homell.Long, GetMapRadiusKm());
                }
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

        public bool GetClusterHoverPopupEnabled()
        {
            return clusterHoverPopupEnabled;
        }

        public void SetClusterHoverPopupEnabled(bool enabled)
        {
            clusterHoverPopupEnabled = enabled;
            SaveClusterHoverPopupSetting(enabled);
            if (!enabled)
            {
                if (clusterHoverToolTip != null)
                {
                    clusterHoverToolTip.IsOpen = false;
                }
                if (clusterSpotsDataGrid != null)
                {
                    clusterSpotsDataGrid.Cursor = Cursors.Arrow;
                }
                clusterLastHoverToolTipColumn = null;
            }
        }

        public void UpdateClusterMapFromSettings()
        {
            if (Properties.Settings.Default.ClusterMapEnabled)
            {
                UpdateClusterSpotsOnMap();
            }
            else
            {
                ClearClusterSpotsFromMap();
            }
        }

        public void HandleClusterActiveChanged(bool isActive)
        {
            if (!isActive)
            {
                if (clusterWindow != null)
                {
                    clusterWindow.Close();
                }
                CloseClusterWebSocket();
                ClearClusterSpotsFromMap();
                if (clusterVisibleSpots != null)
                {
                    clusterVisibleSpots.Clear();
                }
            }
            else
            {
                // Initialize cluster data structures if needed
                if (clusterVisibleSpots == null)
                {
                    clusterVisibleSpots = new ObservableCollection<ClusterSpotViewItem>();
                }
                if (clusterWorkedCountries == null)
                {
                    clusterWorkedCountries = GetWorkedCountriesFromLog();
                }

                // Load filter settings even if window is not shown
                clusterLastMinutesFilterValue = LoadClusterLastMinutesFilterSetting();

                // Refresh visible spots and map with any existing data
                RefreshClusterVisibleSpots();

                // Start WebSocket connection for cluster activity
                StartClusterConnectionAsync();

                // Open window only if Visible is checked
                if (Properties.Settings.Default.ShowClusterWindowOption && clusterWindow == null)
                {
                    GenerateNewClusterWindow();
                }
            }
        }

        private async void StartClusterConnectionAsync()
        {
            if (clusterVisibleSpots == null)
            {
                clusterVisibleSpots = new ObservableCollection<ClusterSpotViewItem>();
            }

            await ConnectClusterWebSocketAsync(null, clusterVisibleSpots);
        }

        public void HandleClusterVisibilityChanged(bool isVisible)
        {
            if (!Properties.Settings.Default.ClusterActive)
            {
                return; // Don't show window if cluster is not active
            }

            if (isVisible)
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
                }
            }
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

            // Don't show map if Empty mode is active
            if (Properties.Settings.Default.MapAreaDisplayMode == 4)
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

            // If in edit mode, exit to new mode first before applying cluster spot
            if (state == State.Edit)
            {
                ClearBtn_Click(null, null);
            }

            string freqText = (spot.FreqText ?? string.Empty).Trim();
            if (!double.TryParse(freqText, NumberStyles.Float, CultureInfo.InvariantCulture, out double freqValue) || freqValue <= 0)
            {
                return;
            }

            double freqMhz = freqValue >= 1000 ? (freqValue / 1000.0) : freqValue;
            CaptureClusterUndoState();

            TB_Frequency.Text = freqMhz.ToString("0.0###", CultureInfo.InvariantCulture);
            // Callsign is pulled from the cluster/map, not typed — don't open the suggestions dropdown.
            suppressNextCallsignSuggestions = true;
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

        // Long-press support for the cluster undo button: holding it ~700 ms clears the whole cluster
        // undo stack at once (mirrors the log-radio undo icon).
        private System.Windows.Threading.DispatcherTimer _clusterUndoResetTimer;
        private bool _clusterUndoResetFired;

        private void ClusterUndoButton_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _clusterUndoResetFired = false;
            if (clusterUndoStates.Count == 0) return;

            if (_clusterUndoResetTimer == null)
            {
                _clusterUndoResetTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(700)
                };
                _clusterUndoResetTimer.Tick += (s, ev) =>
                {
                    _clusterUndoResetTimer.Stop();
                    _clusterUndoResetFired = true;   // suppress the upcoming Click (single undo)
                    ResetClusterUndo();
                };
            }
            _clusterUndoResetTimer.Start();
        }

        private void ClusterUndoButton_PreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _clusterUndoResetTimer?.Stop();
        }

        private void ClusterUndoButton_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            _clusterUndoResetTimer?.Stop();
        }

        // Clears the entire cluster undo stack (the "reset" action triggered by a long press).
        private void ResetClusterUndo()
        {
            if (clusterUndoStates.Count == 0) return;
            clusterUndoStates.Clear();
            UpdateClusterUndoButtonState();
        }

        private async void ClusterUndoButton_Click(object sender, RoutedEventArgs e)
        {
            // async void: guard the whole body so an exception can't crash the app.
            try
            {
                // If a long press just cleared the stack, swallow this click so it doesn't also undo.
                if (_clusterUndoResetFired)
                {
                    _clusterUndoResetFired = false;
                    return;
                }

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
            catch { /* never crash the app from the undo button */ }
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
                HolyMessageBox.Show("Please install 'Chrome' and try again.", "HolyLogger", HolyMsgType.Info, this);
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
                HolyMessageBox.Show("Please install 'Chrome' and try again.", "HolyLogger", HolyMsgType.Info, this);
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
                        if (HolyMessageBox.ShowConfirm("There is a new version. Do you want to install?", "New updates are available", HolyMsgType.Info, this))
                        {
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
                                HolyMessageBox.ShowError(ex.Message, "Download Error", this);
                            }
                        }
                    }
                    else
                    {
                        if (NotifyVersionUpToDate)
                        {
                            HolyMessageBox.ShowSuccess("Your version is up-to-date.", "HolyLogger", this);
                        }
                        else
                        {
                            NotifyVersionUpToDate = true;
                        }
                    }
                }
                catch (Exception)
                {
                    HolyMessageBox.ShowWarning("Auto checking for update failed. Please try again manually later.", "HolyLogger Update", this);
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
                HolyMessageBox.ShowError("Failed to download, please check your connection.", "Download Failed", this);
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
                return;
            }

            // Handle the function/message keys here (tunneling preview) so they work regardless of
            // which child control currently has keyboard focus. The bubbling Window_KeyDown only
            // fires if the focused control (e.g. the callsign box or QSO grid) doesn't consume the
            // key first, which is why the keys appeared "blocked" until a control was clicked.
            if (HandleGlobalFunctionKey(e.Key, e.IsRepeat))
            {
                e.Handled = true;
            }
        }

        protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnPreviewMouseLeftButtonDown(e);

            // When a log row is selected (highlighted blue) and the user left-clicks somewhere
            // that is not inside the log grid (e.g. the DX Callsign box), drop the selection so the
            // row no longer stays blue. Right-click / context-menu keeps the row highlighted via the
            // grid's InactiveSelection resource, which is a separate path and unaffected here.
            if (QSODataGrid != null && QSODataGrid.SelectedItem != null)
            {
                var grid = FindVisualParent<DataGrid>(e.OriginalSource as DependencyObject);
                if (grid != QSODataGrid)
                {
                    QSODataGrid.UnselectAll();
                }
            }
        }

        // Central handler for the application-wide function keys. Returns true if the key was handled.
        // Shared by the main window preview and the cluster window so the keys keep responding even
        // when a secondary window (e.g. the Cluster window) has keyboard focus.
        // Ignores auto-repeat for the F5-F8 message keys so a held key doesn't toggle CW on and off.
        private bool HandleGlobalFunctionKey(Key key, bool isRepeat)
        {
            if (key == Key.F1)
            {
                AddBtn_Click(null, null);
                return true;
            }
            if (key == Key.F2)
            {
                OptionsMenuItemMenuItem_Click(null, null);
                return true;
            }
            if (key == Key.F3)
            {
                SpotButton_Click(null, null);
                return true;
            }
            if (key == Key.F9 || key == Key.Escape)
            {
                ClearBtn_Click(null, null);
                return true;
            }
            if (key == Key.F4)
            {
                // Toggle the callsign suggestions dropdown on/off. The state is sticky (persisted)
                // and only changes when F4 is pressed again. Ignore auto-repeat so holding the key
                // doesn't flicker the state.
                if (!isRepeat)
                {
                    ToggleCallsignSuggestionsEnabled();
                }
                return true;
            }
            if (key >= Key.F5 && key <= Key.F8)
            {
                if (!isRepeat)
                {
                    TriggerVoiceMessage(key - Key.F4);
                }
                return true;
            }

            return false;
        }

        // Flips the persisted callsign-suggestions on/off state (bound to F4).
        private void ToggleCallsignSuggestionsEnabled()
        {
            ApplyCallsignSuggestionsEnabled(!Properties.Settings.Default.CallsignSuggestionsEnabled);
        }

        // Single entry point for setting the suggestions on/off state, used by both F4 and the
        // Suggest (F4) toggle button. Persists the state, keeps the button's pressed/raised look in
        // sync, and applies it immediately: closing the dropdown when off, or re-showing it for the
        // current callsign text when turning back on.
        private void ApplyCallsignSuggestionsEnabled(bool enabled)
        {
            Properties.Settings.Default.CallsignSuggestionsEnabled = enabled;
            Properties.Settings.Default.Save();

            // Reflect on the toggle button (no-op / no recursion: Click isn't raised by code).
            if (BtnSuggestToggle != null && (BtnSuggestToggle.IsChecked == true) != enabled)
                BtnSuggestToggle.IsChecked = enabled;

            if (!enabled)
            {
                if (CallsignSuggestionsPopup != null)
                    CallsignSuggestionsPopup.IsOpen = false;
                if (LB_DXCallsignSuggestions != null)
                    LB_DXCallsignSuggestions.ItemsSource = null;
            }
            else if (TB_DXCallsign != null && TB_DXCallsign.IsKeyboardFocusWithin
                     && !string.IsNullOrWhiteSpace(TB_DXCallsign.Text))
            {
                UpdateCallsignSuggestions();
            }

            Status = enabled ? "Callsign suggestions: On (F4)" : "Callsign suggestions: Off (F4)";
        }

        private void BtnSuggestToggle_Click(object sender, RoutedEventArgs e)
        {
            // The ToggleButton has already flipped IsChecked by the time Click fires.
            ApplyCallsignSuggestionsEnabled(BtnSuggestToggle.IsChecked == true);
        }

        // Forwards function keys pressed while a secondary window (e.g. the Cluster window or the
        // Cluster Settings window) has focus, so F1/F2/F3/F5-F8/F9 keep working without switching
        // back to the main window first. Attach this to any new top-level window that should
        // inherit the global function-key behavior.
        private void ForwardGlobalFunctionKeys(object sender, KeyEventArgs e)
        {
            if (HandleGlobalFunctionKey(e.Key, e.IsRepeat))
            {
                e.Handled = true;
            }
        }


        private void TB_DXCallsign_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Down && CallsignSuggestionsPopup.IsOpen && LB_DXCallsignSuggestions.Items.Count > 0)
            {
                // An arrow key always hands control back to the keyboard (even right after the mouse
                // hovered/scrolled the list), so navigation is never blocked.
                callsignSuggestionMouseControl = false;
                LB_DXCallsignSuggestions.SelectedIndex = Math.Min(LB_DXCallsignSuggestions.SelectedIndex + 1, LB_DXCallsignSuggestions.Items.Count - 1);
                LB_DXCallsignSuggestions.ScrollIntoView(LB_DXCallsignSuggestions.SelectedItem);
                // Arrow keys only navigate; do not auto-fill the textbox
                e.Handled = true;
            }
            else if (e.Key == Key.Up && CallsignSuggestionsPopup.IsOpen && LB_DXCallsignSuggestions.Items.Count > 0)
            {
                callsignSuggestionMouseControl = false;
                LB_DXCallsignSuggestions.SelectedIndex = Math.Max(LB_DXCallsignSuggestions.SelectedIndex - 1, 0);
                LB_DXCallsignSuggestions.ScrollIntoView(LB_DXCallsignSuggestions.SelectedItem);
                // Arrow keys only navigate; do not auto-fill the textbox
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

        private void TB_DXCallsign_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Do NOT set e.Handled here. Swallowing the mouse-down cancels the TextBox's built-in
            // caret positioning, which is why the cursor did not land where the user clicked (and
            // appeared only after a delay). Let WPF handle focus + caret placement normally.
        }

        private void ApplyHighlightedCallsignSuggestionToTextBox()
        {
            // When a '?' search pattern is active, do not feed the highlighted callsign into the
            // textbox while navigating/scrolling - that would destroy the pattern and collapse the
            // result list. The full callsign is only committed on explicit selection (Enter/click).
            if ((TB_DXCallsign.Text ?? string.Empty).IndexOf('?') >= 0) return;

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

            // Validate against the same rules the map parser uses, so valid 4-char grids
            // (e.g. KM72) are accepted and malformed ones (e.g. KM720R, a zero where the
            // 5th-position letter belongs) are caught here rather than silently breaking the map.
            if (!MaidenheadLocator.IsValidLocator(locator))
            {
                e.Handled = true;
                HolyMessageBox.ShowWarning("\"" + locator + "\" is not a valid grid square.\n\nUse 2 letters + 2 digits (e.g. KM72), optionally followed by 2 letters (e.g. KM72OR). Note: the 5th/6th characters are letters (O), not zeros (0).", "Invalid My Locator", this);
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

            UpdateActiveBandButtonVisibility();

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

                // Track current QRZ image URL
                currentQrzImageUrl = normalized;

                // Update graphics box if in QRZ Photo mode
                if (Properties.Settings.Default.MapAreaDisplayMode == 2)
                {
                    LoadCurrentQRZPhotoToGraphicsBox();
                }

                // Show separate photo window only if NOT showing in graphics box
                if (Properties.Settings.Default.MapAreaDisplayMode != 2)
                {
                    ShowQrzPhotoWindow(normalized);
                }
            }
            catch
            {
                ClearQrzPhoto();
            }
        }

        private void ClearQrzPhoto()
        {
            // Clear tracked image URL
            currentQrzImageUrl = null;

            // Update graphics box if in QRZ Photo mode
            if (Properties.Settings.Default.MapAreaDisplayMode == 2)
            {
                LoadCurrentQRZPhotoToGraphicsBox();
            }

            if (qrzPhotoWindow != null)
            {
                SaveQrzPhotoWindowBounds(qrzPhotoWindow);
                qrzPhotoWindow.Close();
                qrzPhotoWindow = null;
            }
        }

        private void QueueClearQrzPhoto()
        {
            if (qrzPhotoClearQueued)
            {
                return;
            }

            qrzPhotoClearQueued = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                qrzPhotoClearQueued = false;
                ClearQrzPhoto();
            }), DispatcherPriority.Background);
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
                // Ensure TextBoxes are initialized before trying to update them
                if (TB_RSTSent == null || TB_RSTRcvd == null || CB_Mode == null)
                    return;

                // Inside SelectionChanged the CB_Mode.Text property can still hold the
                // previous value (it lags one event because Text is data-bound), which
                // caused the RST fields to update only on the next QSO. Read the newly
                // selected item directly so the RST fields update immediately.
                string val;
                if (e.AddedItems != null && e.AddedItems.Count > 0 && e.AddedItems[0] is ComboBoxItem addedItem)
                {
                    val = addedItem.Content as string;
                }
                else if (CB_Mode.SelectedItem is ComboBoxItem selectedItem)
                {
                    val = selectedItem.Content as string;
                }
                else
                {
                    val = CB_Mode.Text;
                }

                val = (val ?? string.Empty).Trim().ToUpperInvariant();

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
                // Refresh the Msg buttons so they switch to/from the CW look immediately on a mode
                // change (matters when the radio is off, where the mode comes from this dropdown).
                UpdateMessageButtonLabels();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"CB_Mode_SelectionChanged error: {ex.Message}");
                //throw;
            }

        }

        private void TB_DXCallsign_TextChanged(object sender, TextChangedEventArgs e)
        {
            // While loading a QSO into the form for editing, the callsign is set programmatically and
            // we must NOT run the typing lookup (it would clear/overwrite the QSO's saved fields).
            if (_suppressCallsignLookupForEdit)
                return;

            // Starting a new callsign clears the log-row blue highlight left by a right-click menu.
            if (QSODataGrid != null && QSODataGrid.SelectedItem != null)
                QSODataGrid.UnselectAll();
            // Also drop any stuck map-hover blue highlight on the cluster rows.
            SetClusterRowMapHighlight(null);

            callsignLookupRevision++;
            string dxCallText = (TB_DXCallsign.Text ?? string.Empty).Trim();

            // A pattern containing '?' is a search filter, not a real callsign: only drive the
            // suggestions dropdown and skip DXCC / QRZ / azimuth / matrix lookups.
            if (dxCallText.IndexOf('?') >= 0)
            {
                QueueClearQrzPhoto();
                RestartCallsignLookupDebounce();
                return;
            }

            if (string.IsNullOrWhiteSpace(dxCallText))
            {
                CallsignLookupDebounceTimer.Stop();
                CallsignSuggestionsPopup.IsOpen = false;
                LB_DXCallsignSuggestions.ItemsSource = null;
                TB_DXCallsign.ToolTip = null;
                QueueClearQrzPhoto();

                // Defer ALL UI updates to allow immediate textbox response during fast deletion
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    FName = string.Empty;
                    ClearDXLocator();
                    TB_DXCC.Text = "";
                    TB_DX_Name.Text = "";
                    TB_State.Text = "";
                    UpdateCountryFlag(null);
                    // Use ClearAzimuth (not ClearAzimuthForTyping) so emptying the DX callsign removes
                    // the azimuth line to the deleted station and immediately restores the cluster-spots
                    // map view, instead of leaving the stale arc until the next spot batch arrives.
                    ClearAzimuth();
                    ClearMatrix();
                    L_Duplicate.Visibility = Visibility.Hidden;
                    L_Legal.Visibility = Visibility.Hidden;
                    RestoreDataContext();
                }), DispatcherPriority.Background);
            }
            else
            {
                // Defer stale value clearing to avoid blocking keyboard
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    FName = string.Empty;
                    ClearDXLocator();
                }), DispatcherPriority.Send);

                // Keep typing snappy: skip heavy DXCC/matrix/filter work until at least 2 chars.
                if (dxCallText.Length < 2)
                {
                    CallsignLookupDebounceTimer.Stop();
                    CallsignSuggestionsPopup.IsOpen = false;
                    LB_DXCallsignSuggestions.ItemsSource = null;
                    TB_DXCallsign.ToolTip = null;
                    QueueClearQrzPhoto();
                    Prefix = dxCallText.ToUpperInvariant();
                    return;
                }

                // Prevent stale photo while callsign is not long enough for a QRZ lookup.
                if (dxCallText.Length < 3)
                {
                    CallsignLookupDebounceTimer.Stop();
                    QueueClearQrzPhoto();
                }

                // Defer all heavy operations to debounce timer for instant keyboard response
                RestartCallsignLookupDebounce();
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
            int revisionAtTick = callsignLookupRevision;

            // Consume the suppress flag (set when the callsign came from the cluster/map, not typing).
            bool suppressSuggestions = suppressNextCallsignSuggestions;
            suppressNextCallsignSuggestions = false;

            if (string.IsNullOrWhiteSpace(TB_DXCallsign.Text))
            {
                ClearQrzPhoto();
                ClearAzimuthForTyping();
                return;
            }

            string dxCallText = TB_DXCallsign.Text.Trim();

             if (!isApplyingSuggestion && !suppressSuggestions)
            {
                UpdateCallsignSuggestions();
            }
            else if (suppressSuggestions)
            {
                CallsignSuggestionsPopup.IsOpen = false;
            }

            if (revisionAtTick != callsignLookupRevision)
            {
                return;
            }

            if (dxCallText.IndexOf('?') >= 0)
            {
                ClearQrzPhoto();
                ClearAzimuthForTyping();
                return;
            }

            if (dxCallText.Length < 3)
            {
                ClearAzimuthForTyping();
                return;
            }

            // Refresh date/time if in automatic mode
            if (!Properties.Settings.Default.isManualMode && state == State.New)
                RefreshDateTime_Btn_MouseUp(null, null);

            // Perform DXCC lookup (cached by EntityResolver)
            DXCC dXCC = rem.GetDXCC(dxCallText);
            Country = dXCC.Name;
            UpdateCountryFlag(dXCC.Name);
            Continent = dXCC.Continent;
            QRZGrid = dXCC.Locator;
            Prefix = dxCallText.Length >= 2 ? dxCallText.Substring(0, 2) : "";

            // Capture all UI-thread values needed for background computation.
            int capturedRevision = revisionAtTick;
            string capturedDxCall = dxCallText;
            string capturedMyCall = TB_MyCallsign.Text;
            string capturedBand = TB_Band.Text;
            string capturedMode = CB_Mode.Text;
            State capturedState = state;
            int capturedEditId = (state == State.Edit && QsoToUpdate != null) ? QsoToUpdate.id : -1;
            bool isFilterQSOs = Properties.Settings.Default.IsFilterQSOs;
            QSO capturedLastQSO = LastQSO;
            bool showLastQso = capturedLastQSO != null && capturedState != State.Edit
                               && Properties.Settings.Default.DisplayLastQSOinGrid;
            // Snapshot so background thread never touches ObservableCollection directly.
            var qsosSnapshot = Qsos.ToList();

            // Run all LINQ over 11k QSOs on a thread-pool thread so the UI thread
            // stays free for keystrokes while the queries execute.
            Task.Run(() =>
            {
                if (capturedRevision != callsignLookupRevision) return;

                // Matrix query
                var qsoList = qsosSnapshot
                    .Where(qso => qso.MyCall == capturedMyCall && qso.DXCall == capturedDxCall)
                    .ToList();

                // Dup / legal check
                var dupQuery = qsosSnapshot.Where(qso =>
                    qso.MyCall == capturedMyCall && qso.DXCall == capturedDxCall &&
                    qso.Band == capturedBand && qso.Mode == capturedMode);
                if (capturedEditId >= 0)
                    dupQuery = dupQuery.Where(p => p.id != capturedEditId);
                bool hasDups = dupQuery.Any();
                // Count all prior QSOs with this station (any band/mode) — used for the
                // "worked before" indicator, including when it is also an exact duplicate.
                int legalCount = qsosSnapshot.Count(qso =>
                    qso.MyCall == capturedMyCall && qso.DXCall == capturedDxCall);

                // QSO list filter
                List<QSO> matchingQsos = null;
                if (isFilterQSOs)
                {
                    matchingQsos = qsosSnapshot
                        .Where(p => p.DXCall != null && p.DXCall.Contains(capturedDxCall))
                        .Take(1000)
                        .ToList();
                    if (showLastQso)
                        matchingQsos.Insert(0, capturedLastQSO);
                }

                // Return to UI thread for the actual UI updates (fast — no more LINQ here).
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (capturedRevision != callsignLookupRevision) return;

                    UpdateMatrixWithData(qsoList, skipDupUpdate: true);

                    if (Properties.Settings.Default.ContestMode && hasDups)
                    {
                        L_Duplicate.Visibility = Visibility.Visible;
                        L_Legal.Visibility = Visibility.Hidden;
                        matrix?.SetDup();
                    }
                    else
                    {
                        L_Duplicate.Visibility = Visibility.Hidden;
                        if (legalCount > 0)
                            ShowLegalQsoBefore(legalCount);
                        else
                            L_Legal.Visibility = Visibility.Hidden;
                        matrix?.ClearDup();
                    }

                    if (matchingQsos != null)
                    {
                        FilteredQsos = new ObservableCollection<QSO>(matchingQsos);
                        DataContext = FilteredQsos;
                    }

                    SetAzimuth();
                    if (capturedState == State.New)
                        GetQrzData();

                }), DispatcherPriority.Background);
            });
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
            // The user can switch the suggestions dropdown off/on with F4 (state persists). When it's
            // off, keep the popup closed regardless of typing.
            if (!Properties.Settings.Default.CallsignSuggestionsEnabled)
            {
                CallsignSuggestionsPopup.IsOpen = false;
                LB_DXCallsignSuggestions.ItemsSource = null;
                return;
            }

            string pattern = (TB_DXCallsign.Text ?? string.Empty).Trim().ToUpperInvariant();

            // Rule: no search until at least 2 characters are typed.
            if (pattern.Length < 2)
            {
                CallsignSuggestionsPopup.IsOpen = false;
                LB_DXCallsignSuggestions.ItemsSource = null;
                TB_DXCallsign.ToolTip = null;
                return;
            }

            var matches = new List<CallsignSuggestionItem>(maxCallsignSuggestions);
            var slashMatches = new List<CallsignSuggestionItem>();

            // '?' is a single-character wildcard that must match exactly one character at that position.
            // Literal characters must match the same position. Anything after the pattern is allowed.
            // With no '?' the pattern behaves as a plain prefix search.
            int firstWildcard = pattern.IndexOf('?');
            bool hasWildcard = firstWildcard >= 0;

            // Use the literal prefix (characters before the first '?') to jump into the sorted index quickly.
            string literalPrefix = hasWildcard ? pattern.Substring(0, firstWildcard) : pattern;

            int start = 0;
            if (literalPrefix.Length > 0)
            {
                int index = callsignIndex.BinarySearch(literalPrefix, StringComparer.Ordinal);
                if (index < 0) index = ~index;
                start = index;
            }

            for (int i = start; i < callsignIndex.Count; i++)
            {
                string call = callsignIndex[i];

                if (literalPrefix.Length > 0)
                {
                    // Past the literal-prefix block: nothing else can match.
                    if (!call.StartsWith(literalPrefix, StringComparison.Ordinal)) break;
                }
                else if (matches.Count >= maxCallsignSuggestions && slashMatches.Count >= maxCallsignSuggestions)
                {
                    // No literal prefix to bound the scan (e.g. "?E"): stop once both lists are full.
                    break;
                }

                if (hasWildcard && !MatchesPositionalPattern(call, pattern)) continue;

                int matchLength = hasWildcard ? pattern.Length : literalPrefix.Length;
                if (call.Contains('/'))
                {
                    if (slashMatches.Count < maxCallsignSuggestions)
                        slashMatches.Add(BuildSuggestionItem(call, pattern, hasWildcard, matchLength));
                }
                else if (matches.Count < maxCallsignSuggestions)
                    matches.Add(BuildSuggestionItem(call, pattern, hasWildcard, matchLength));

                // Early exit if we have enough matches
                if (literalPrefix.Length > 0 && matches.Count >= maxCallsignSuggestions)
                    break;
            }

            // Fill remaining slots with slash matches (non-slash callsigns are shown first).
            int remaining = maxCallsignSuggestions - matches.Count;
            if (remaining > 0)
                matches.AddRange(slashMatches.Take(remaining));

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

            if (!Properties.Settings.Default.ShowCallsignDropdown && hasWildcard)
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

        // Positional match: each '?' matches any single character, every other character must match the
        // same position in the callsign, and anything after the pattern is allowed.
        private static bool MatchesPositionalPattern(string call, string pattern)
        {
            if (call.Length < pattern.Length) return false;
            for (int j = 0; j < pattern.Length; j++)
            {
                char pc = pattern[j];
                if (pc != '?' && pc != call[j]) return false;
            }
            return true;
        }

        internal static readonly Dictionary<string, string> DxccNameToIso = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
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
            // WPF raises synthetic MouseMove events when the item under a stationary cursor changes
            // (e.g. the list re-populates after deleting '?', or the keyboard scrolls the list).
            // Only let the mouse take control when the cursor physically moved.
            Point pos = e.GetPosition(LB_DXCallsignSuggestions);
            if (lastCallsignSuggestionMousePos.HasValue && lastCallsignSuggestionMousePos.Value == pos)
                return;
            lastCallsignSuggestionMousePos = pos;

            var source = e.OriginalSource as DependencyObject;
            var item = ItemsControl.ContainerFromElement(LB_DXCallsignSuggestions, source) as ListBoxItem;
            if (item?.DataContext is CallsignSuggestionItem hovered)
            {
                callsignSuggestionMouseControl = true;
                if (!Equals(LB_DXCallsignSuggestions.SelectedItem, hovered))
                {
                    LB_DXCallsignSuggestions.SelectedItem = hovered;
                    // Mouse hover only highlights; do not auto-fill the textbox
                }
            }
        }

        private void LB_DXCallsignSuggestions_MouseLeave(object sender, MouseEventArgs e)
        {
            // Keep the last highlighted row selected, but give arrow-key control back to keyboard.
            callsignSuggestionMouseControl = false;
            lastCallsignSuggestionMousePos = null;
        }

        private void LB_DXCallsignSuggestions_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            ApplySelectedCallsignSuggestion();
        }

        private class CallsignSuggestionItem
        {
            // Legacy properties for backward compatibility with non-wildcard searches
            public string Before { get; set; }
            public string Match { get; set; }
            public string After { get; set; }

            // New properties for wildcard-aware display
            public List<CallsignSegment> Segments { get; set; }
            public string FullCallsign => Before + Match + After;
        }

        private class CallsignSegment
        {
            public string Text { get; set; }
            public string Color { get; set; }  // "Normal", "Green", "Red"
            public bool IsBold { get; set; }
        }

        private CallsignSuggestionItem BuildSuggestionItem(string callsign, string pattern, bool hasWildcard, int matchLength)
        {
            var item = new CallsignSuggestionItem
            {
                Before = string.Empty,
                Match = callsign.Length >= matchLength ? callsign.Substring(0, matchLength) : callsign,
                After = callsign.Length > matchLength ? callsign.Substring(matchLength) : string.Empty,
                Segments = new List<CallsignSegment>()
            };

            if (!hasWildcard)
            {
                // No wildcards: simple green prefix match
                item.Segments.Add(new CallsignSegment { Text = item.Match, Color = "Green", IsBold = true });
                if (!string.IsNullOrEmpty(item.After))
                    item.Segments.Add(new CallsignSegment { Text = item.After, Color = "Normal", IsBold = false });
            }
            else
            {
                // Wildcards: color wildcard positions red, literal matches green
                for (int i = 0; i < pattern.Length && i < callsign.Length; i++)
                {
                    char patternChar = pattern[i];
                    char callsignChar = callsign[i];

                    if (patternChar == '?')
                    {
                        // Wildcard position: red
                        item.Segments.Add(new CallsignSegment 
                        { 
                            Text = callsignChar.ToString(), 
                            Color = "Red", 
                            IsBold = true 
                        });
                    }
                    else
                    {
                        // Literal match: green
                        item.Segments.Add(new CallsignSegment 
                        { 
                            Text = callsignChar.ToString(), 
                            Color = "Green", 
                            IsBold = true 
                        });
                    }
                }

                // Add remainder (after pattern) in normal color
                if (callsign.Length > pattern.Length)
                {
                    item.Segments.Add(new CallsignSegment 
                    { 
                        Text = callsign.Substring(pattern.Length), 
                        Color = "Normal", 
                        IsBold = false 
                    });
                }
            }

            return item;
        }


        private int NormalizeCallsignSuggestionRows(int rows)
        {
            if (rows <= 0) return DefaultCallsignSuggestionRows;
            return Math.Max(MinCallsignSuggestionRows, Math.Min(MaxCallsignSuggestionRows, rows));
        }

        private void ApplyCallsignSuggestionRowsSetting()
        {
            int rows = NormalizeCallsignSuggestionRows(Properties.Settings.Default.CallsignSuggestionRows);

            // The setting controls only how many rows are visible at once. The result list itself
            // always collects up to MaxCallsignSuggestionResults so the user can scroll the full list.
            maxCallsignSuggestions = MaxCallsignSuggestionResults;
            LB_DXCallsignSuggestions.MaxHeight = rows * CallsignSuggestionRowHeight;
        }

        private void UpdateMatrix()
        {
            if (!isInitializeComponentsComplete) return;
            ClearMatrix();

            if (Qsos == null) return;

            // Optimize: materialize the filtered list once with ToList() to avoid multiple enumerations
            string myCall = TB_MyCallsign.Text;
            string dxCall = TB_DXCallsign.Text;
            var qso_list = Qsos.Where(qso => qso.MyCall == myCall && qso.DXCall == dxCall).ToList();
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

        private void UpdateMatrixWithData(List<QSO> qso_list, bool skipDupUpdate = false)
        {
            if (!isInitializeComponentsComplete) return;
            ClearMatrix();

            if (qso_list == null || qso_list.Count == 0) return;

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

            if (!skipDupUpdate)
                UpdateDup();
        }

        // Shows the green "Legal ... QSO(s) Before" indicator with the count of prior QSOs with this
        // DX station. The count is drawn in black; the word is "QSO" for 1 and "QSOs" for more.
        private void ShowLegalQsoBefore(int count)
        {
            L_LegalCount.Text = count.ToString(CultureInfo.InvariantCulture);
            L_LegalSuffix.Text = count == 1 ? " QSO Before" : " QSOs Before";
            L_Legal.Visibility = Visibility.Visible;
        }

        private void UpdateDup()
        {
            // Optimize: cache values and materialize queries to avoid repeated enumeration
            string myCall = TB_MyCallsign.Text;
            string dxCall = TB_DXCallsign.Text;
            string band = TB_Band.Text;
            string mode = CB_Mode.Text;

            var dups = Qsos.Where(qso => qso.MyCall == myCall && qso.DXCall == dxCall && qso.Band == band && qso.Mode == mode);
            var legal = Qsos.Where(qso => qso.MyCall == myCall && qso.DXCall == dxCall);

            if (state == State.Edit)
                dups = dups.Where(p => p.id != QsoToUpdate.id);

            // "Duplicate" is only meaningful in Contest Mode. Outside a contest we never report a
            // duplicate; we just show how many times the station was worked before.
            if (Properties.Settings.Default.ContestMode && dups.Any())
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
                int legalCount = legal.Count();
                if (legalCount > 0)
                {
                    ShowLegalQsoBefore(legalCount);
                }
                else
                {
                    L_Legal.Visibility = Visibility.Hidden;
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
            EnforceMapSquareMinWidth();
            SaveMainWindowBounds();
        }

        // The azimuth map stretches horizontally but is a fixed 325px tall, so without a floor the
        // window could be narrowed until the map became a portrait rectangle. Everything to the left
        // of the map (the blue panel + gaps) plus the window border is a constant overhead equal to
        // (WindowWidth - MapWidth); the width at which the map is exactly square is therefore
        // overhead + mapHeight. Measuring it live keeps it correct across DPI / chrome differences.
        private void EnforceMapSquareMinWidth()
        {
            if (!Properties.Settings.Default.IsShowAzimuthControl) return;
            if (MapControl == null) return;

            double mapWidth = MapControl.ActualWidth;
            double mapHeight = MapControl.ActualHeight > 0 ? MapControl.ActualHeight : MapControl.Height;
            if (mapWidth <= 0 || this.ActualWidth <= 0 || double.IsNaN(mapHeight) || mapHeight <= 0)
                return;

            double overhead = this.ActualWidth - mapWidth;   // blue panel + gaps + window chrome (constant)
            double squareMinWidth = Math.Ceiling(overhead + mapHeight);
            if (Math.Abs(this.MinWidth - squareMinWidth) > 0.5)
                this.MinWidth = squareMinWidth;
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

                    // Don't update map if Empty mode is active
                    if (Properties.Settings.Default.MapAreaDisplayMode != 4)
                    {
                        MapControl.ShowMap(ll.Lat, ll.Long, autoFitRadius, Azimuth, homell.Lat, homell.Long);
                    }
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

        private void ClearAzimuthForTyping()
        {
            Azimuth = 0;
            _dxQsoInProgress = false;
        }



        private void ClearAzimuth()
        {
            ClearAzimuthForTyping();
            // Reset to home, clearing any DX arc. ShowHomeMap now repaints the cluster spots
            // itself when the cluster map is enabled, so no separate overlay call is needed.
            ShowHomeMap();
        }

        private void ShowHomeMap()
        {
            if (MapControl == null) return;

            // Don't show map if Empty mode is active
            if (Properties.Settings.Default.MapAreaDisplayMode == 4)
                return;

            if (!string.IsNullOrWhiteSpace(TB_MyLocator.Text))
            {
                try
                {
                    var ll = MaidenheadLocator.LocatorToLatLng(TB_MyLocator.Text);
                    MapControl.ShowMap(ll.Lat, ll.Long, GetMapRadiusKm());

                    // The home map is now visible. If the cluster map is enabled, immediately
                    // overlay the spots we already hold instead of leaving the map empty until
                    // the next spot arrives from the cluster. Covers every path that brings the
                    // map into view from a hidden/placeholder state (locator fixed, startup,
                    // ClearAzimuth, switching back to Map mode, etc.).
                    if (Properties.Settings.Default.ClusterMapEnabled)
                    {
                        DoUpdateClusterSpotsOnMap();
                    }
                }
                catch
                {
                    // Locator is present but not a valid Maidenhead grid (e.g. a digit where a
                    // letter belongs, like the easily-confused 'O' vs '0'). Tell the user instead
                    // of leaving a silently blank map.
                    MapControl.ShowPlaceholder("Invalid My Locator: \"" + TB_MyLocator.Text.Trim() + "\"&#x0a;Enter a valid grid square (e.g. KM72 or KM72OR)");
                }
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

        // The map reports which station dot the mouse is over (its popup is showing); highlight the
        // matching cluster-list row(s) blue. A null/empty callsign clears the highlight.
        private void OnMapSpotHovered(string callsign)
        {
            Dispatcher.BeginInvoke(new Action(() => SetClusterRowMapHighlight(callsign)));
        }

        private void OnMapSpotHoverEnded()
        {
            Dispatcher.BeginInvoke(new Action(() => SetClusterRowMapHighlight(null)));
        }

        private void SetClusterRowMapHighlight(string callsign)
        {
            if (clusterVisibleSpots == null) return;
            bool any = !string.IsNullOrEmpty(callsign);
            foreach (var s in clusterVisibleSpots)
            {
                s.IsMapHovered = any && string.Equals(s.DXCallsign, callsign, StringComparison.OrdinalIgnoreCase);
            }
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

            if (e.PropertyName == nameof(Properties.Settings.Default.ClusterMapEnabled))
            {
                if (!Properties.Settings.Default.ClusterMapEnabled)
                {
                    Dispatcher.BeginInvoke(new Action(ClearClusterSpotsFromMap), DispatcherPriority.Background);
                }
                else
                {
                    Dispatcher.BeginInvoke(new Action(UpdateClusterSpotsOnMap), DispatcherPriority.Background);
                }
            }

            if (e.PropertyName == nameof(Properties.Settings.Default.ClusterActive))
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    HandleClusterActiveChanged(Properties.Settings.Default.ClusterActive);
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
            // Start cluster connection if Active, regardless of visibility
            if (Properties.Settings.Default.ClusterActive)
            {
                // Initialize cluster structures and start WebSocket
                HandleClusterActiveChanged(true);
            }
            else
            {
                // Clean up cluster if not active
                if (clusterWindow != null)
                {
                    clusterWindow.Close();
                    clusterWindow = null;
                }
            }
        }

        public void UpdateMapDayNightOverlay()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (MapControl != null)
                {
                    MapControl.RefreshMap();
                }
            }), DispatcherPriority.Background);
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
                // Await the login instead of blocking the UI thread on a synchronous web request.
                _SessionKey = await Helper.LoginToQRZAsync();
            }
            if (!string.IsNullOrWhiteSpace(SessionKey) && !string.IsNullOrWhiteSpace(TB_DXCallsign.Text) && TB_DXCallsign.Text.Trim().Length >=3)
            {
                string dxcall = TB_DXCallsign.Text.Trim();
                string bare_dxcall = Services.getBareCallsign(dxcall);

                try
                {
                    string baseRequest = "https://xmldata.qrz.com/xml/current/?s=";
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

                            // Defer the QRZ photo: only fetch it once the operator has stayed on this
                            // callsign for a short predefined time. If they keep typing/correcting,
                            // callsignLookupRevision changes and the image download is skipped entirely.
                            int photoRevision = callsignLookupRevision;
                            await Task.Delay(QrzPhotoDelayMs);
                            if (photoRevision == callsignLookupRevision
                                && dxcall == (TB_DXCallsign.Text ?? string.Empty).Trim())
                            {
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
                            }

                            string key = xDoc.Root.Descendants(ns + "Key").FirstOrDefault().Value;
                            if (SessionKey != key)
                                if (isNetworkAvailable) _SessionKey = await Helper.LoginToQRZAsync();
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
            bool anyNeedLookup = Qsos.Any(q => !_qrzNoData.Contains(q.DXCall) &&
                                               (string.IsNullOrWhiteSpace(q.Name) ||
                                                string.IsNullOrWhiteSpace(q.DXLocator)));
            if (!anyNeedLookup)
            {
                var dlg = new Window
                {
                    Title = "QRZ Lookup",
                    SizeToContent = SizeToContent.WidthAndHeight,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Owner = this,
                    ResizeMode = ResizeMode.NoResize
                };
                var btn = new System.Windows.Controls.Button
                {
                    Content = "OK", Width = 90, Height = 34, FontSize = 16,
                    Margin = new Thickness(0, 16, 0, 0),
                    IsDefault = true
                };
                btn.Click += (s2, e2) => dlg.Close();
                dlg.Content = new System.Windows.Controls.StackPanel
                {
                    Margin = new Thickness(30, 24, 30, 20),
                    Children =
                    {
                        new System.Windows.Controls.TextBlock
                        {
                            Text = "Log file is fully populated —\nall QSOs already have Name and Locator.",
                            FontSize = 18, TextAlignment = TextAlignment.Center
                        },
                        btn
                    }
                };
                dlg.ShowDialog();
                return;
            }

            UploadProgressTitle = "QRZ Lookup";
            ToggleUploadProgress(Visibility.Visible);
            await GetQrzForEntireLogAsync(new Progress<string>(msg => UploadProgress = msg));
            ToggleUploadProgress(Visibility.Hidden);
            UploadProgressTitle = "";
        }

        private async Task<bool> GetQrzForEntireLogAsync(IProgress<string> progress)
        {
            if (!isNetworkAvailable) return false;

            var needsLookup = Qsos.Where(q => !_qrzNoData.Contains(q.DXCall) &&
                                              (string.IsNullOrWhiteSpace(q.Name) ||
                                               string.IsNullOrWhiteSpace(q.DXLocator))).ToList();
            if (needsLookup.Count == 0) return true;

            // Debug: dump every QSO that will be re-queried so we can see what fields are missing
            try
            {
                string debugPath = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    "qrz_missing_debug.txt");
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"QRZ re-query candidates — {DateTime.Now:yyyy-MM-dd HH:mm:ss}  ({needsLookup.Count} QSOs)");
                sb.AppendLine(new string('-', 100));
                sb.AppendLine($"{"ID",-8} {"DXCall",-12} {"Date",-10} {"Time",-8} {"Band",-6} {"Mode",-6} {"Name",-20} {"DXLocator",-12} {"Country",-20} {"Freq",-10}");
                sb.AppendLine(new string('-', 100));
                foreach (var q in needsLookup)
                    sb.AppendLine($"{q.id,-8} {q.DXCall,-12} {q.Date,-10} {q.Time,-8} {q.Band,-6} {q.Mode,-6} {(q.Name ?? ""),-20} {(q.DXLocator ?? ""),-12} {(q.Country ?? ""),-20} {(q.Freq ?? ""),-10}");
                System.IO.File.WriteAllText(debugPath, sb.ToString(), System.Text.Encoding.UTF8);
            }
            catch { /* debug write failure must never break the main flow */ }

            // Collect per-QSO QRZ results for the debug log written after the loop.
            var debugResults = new System.Collections.Generic.List<string>();

            int updated = 0;
            for (int i = 0; i < needsLookup.Count; i++)
            {
                progress.Report($"{i + 1} / {needsLookup.Count}");
                try
                {
                    // Small delay between requests to avoid QRZ rate-limiting
                    // and to keep the UI message loop free between iterations.
                    await Task.Delay(150);
                    QSO qso = needsLookup[i];
                    var (name, grid) = await GetQrzForCall(qso.DXCall);
                    if (!string.IsNullOrWhiteSpace(name))
                        qso.Name = name.Length > 15 ? name.Substring(0, 15) + "..." : name;
                    else if (string.IsNullOrWhiteSpace(qso.Name)) qso.Name = "N/A";
                    if (!string.IsNullOrWhiteSpace(grid)) qso.DXLocator = grid;
                    else if (string.IsNullOrWhiteSpace(qso.DXLocator)) qso.DXLocator = "AA00JJ";
                    if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(grid))
                        _qrzNoData.Add(qso.DXCall);
                    dal.Update(qso);
                    updated++;
                    debugResults.Add($"  ID={qso.id,-6} {qso.DXCall,-12}  qrz_name=[{name}] ({name.Length} chars)  saved_name=[{qso.Name}]  saved_locator=[{qso.DXLocator}]");

                    // Refresh the grid every 25 updates so the user sees progress
                    // without paying the cost of a full refresh on every single QSO.
                    if (updated % 25 == 0)
                        QSODataGrid.Items.Refresh();
                }
                catch (Exception ex)
                {
                    System.Windows.Forms.MessageBox.Show("Failed to execute QRZ Service: " + ex.Message);
                    break;
                }
            }
            QSODataGrid.Items.Refresh();

            // Append what QRZ returned and what was saved, so we can diagnose round-trip losses.
            try
            {
                string debugPath = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    "qrz_missing_debug.txt");
                var sb = new System.Text.StringBuilder();
                sb.AppendLine();
                sb.AppendLine($"QRZ results — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine(new string('-', 100));
                foreach (var line in debugResults) sb.AppendLine(line);
                System.IO.File.AppendAllText(debugPath, sb.ToString(), System.Text.Encoding.UTF8);
            }
            catch { }

            return true;
        }

        private async Task<(string Name, string Grid)> GetQrzForCall(string callsign)
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

                        string grid = xDoc.Root.Descendants(ns + "grid").FirstOrDefault()?.Value ?? "";

                        string key = xDoc.Root.Descendants(ns + "Key").FirstOrDefault().Value;
                        if (SessionKey != key) _SessionKey = await Helper.LoginToQRZAsync();

                        return (name, grid);
                    }
                    else
                    {
                        return ("", "");
                    }
                }
            }
            catch (Exception)
            {
                return ("", "");
            }
            return ("", "");
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
                        lock (_syncLock)
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
            UpdateRigLabel();
            if (OmniRigEngine == null) { UpdateFreqLed(); return; }
            if (Properties.Settings.Default.SelectedOmniRig1)
                Rig = OmniRigEngine.Rig1;
            else if (Properties.Settings.Default.SelectedOmniRig2)
                Rig = OmniRigEngine.Rig2;
            UpdateFreqLed();   // reflect the newly-selected rig (or blank if it isn't online)
        }

        // Shows "RIG1" or "RIG2" next to the LED, reflecting the rig chosen in Options → General.
        private void UpdateRigLabel()
        {
            if (RigLabel == null) return;
            RigLabel.Text = Properties.Settings.Default.SelectedOmniRig2 ? "RIG2" : "RIG1";
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

            bool rigOnline = OmniRigEngine != null && Rig != null
                             && Rig.Status == OmniRig.RigStatusX.ST_ONLINE
                             && Properties.Settings.Default.EnableOmniRigCAT;

            // When the radio is online it controls the mode — block user interaction with the combo.
            // When offline the operator must be able to pick the mode manually.
            if (CB_Mode != null) CB_Mode.IsHitTestVisible = !rigOnline;

            if (!rigOnline)
            {
                ClearVoiceMessageState();
                UpdateFreqLed();   // no live rig -> blank the LED instead of showing a stale value
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
                return dt.ToString("HH:mm", CultureInfo.InvariantCulture);
            }

            return value;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return System.Windows.Data.Binding.DoNothing;
        }
    }

    public class BoolToFontWeightConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool isBold && isBold)
            {
                return System.Windows.FontWeights.Bold;
            }
            return System.Windows.FontWeights.Normal;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return System.Windows.Data.Binding.DoNothing;
        }
    }
}




