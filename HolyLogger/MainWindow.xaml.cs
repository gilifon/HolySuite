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

        // Tracks the last-applied bounds of the "Received Confirmation" overlay so
        // ConfirmationStripHelper can skip re-arranging it when layout is already stable.
        private Rect _confirmationStripLastRect = Rect.Empty;

        public ObservableCollection<QSO> Qsos;
        public ObservableCollection<QSO> FilteredQsos;

        // Rows in FilteredQsos that come from the ACTIVE log's copy-target (shown for reference, painted
        // light blue, never counted or editable). Tracked by object reference — QSO.Equals compares by
        // content-hash, so a copied contact would otherwise be mistaken for its original.
        private HashSet<QSO> _foreignFilterRows;
        private sealed class RefEq : IEqualityComparer<QSO>
        {
            public static readonly RefEq Instance = new RefEq();
            public bool Equals(QSO a, QSO b) => ReferenceEquals(a, b);
            public int GetHashCode(QSO o) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);
        }

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

        // Cancellation sources for the long-running spinner operations that the user can Stop
        // (Remove Duplicates, Full-Log QRZ Service). Non-null only while the operation runs.
        private CancellationTokenSource _dedupCts;
        private CancellationTokenSource _qrzCts;

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
        // Loaded as pack URIs (not filesystem-relative): these PNGs are compiled <Resource>s and are
        // NOT copied to bin\Images, so a relative Uri would throw when used as an OpacityMask brush.
        BitmapImage lock_path = new BitmapImage(new Uri("pack://application:,,,/Images/lock.png"));
        BitmapImage unlock_path = new BitmapImage(new Uri("pack://application:,,,/Images/unlock.png"));

        public static UdpClient Client;
        public static UdpClient N1MMClient;

        // Static compiled regex for N1MM+ UDP parsing (performance optimization)
        private static readonly Regex N1MMTxFreqRegex = new Regex(@"<TXFreq>(.*)?<", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex N1MMModeRegex = new Regex(@"<Mode>(.*)?<", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        string MachineName = "Default";

        public MainWindow()
        {
            MachineName = Environment.MachineName;
            // Must run before ANYTHING reads the settings that used to be loose files (the QRZ photo
            // bounds right below, the cluster ones when the cluster is built later).
            MigrateLegacyFileSettings();
            LoadQrzPhotoWindowBoundsFromDisk();

            Qsos = new ObservableCollection<QSO>();
            // Point the resolver at the updatable cty.dat (seeded from the embedded copy on first
            // run) before any EntityResolver is created, so the whole app uses the same file.
            CtyDatService.Initialize();
            // And at Club Log's copy, so CountryLookup can consult it. Both must be pointed at their
            // files before the first lookup is built.
            ClublogCtyService.Initialize();
            rem = new EntityResolver();
            InitializeComponent();

            // Build the View > Color Scheme submenu from the palette's scheme registry, and
            // re-paint code-colored areas (QSO rows) whenever the theme changes.
            BuildColorSchemeMenu();
            ThemeManager.ThemeChanged += OnThemeChanged;

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
            AddLogGrid.Children.Add(TB_FrequencyDisplay);

            TB_Frequency.GotFocus += TB_Frequency_GotFocus;
            TB_Frequency.LostFocus += TB_Frequency_LostFocus;

            // Keep the "Received Confirmation" overlay tracking the LoTW..Paper QSL header group's
            // actual on-screen bounds — column widths change (Auto-sizing) and the window resizes.
            QSODataGrid.Loaded += (s, e) => UpdateConfirmationStripPosition();
            QSODataGrid.LayoutUpdated += (s, e) => UpdateConfirmationStripPosition();

            // The same five columns drag as one block and admit no column between them.
            ConfirmationColumnGroup.Attach(QSODataGrid);
            TB_DXCallsign.PreviewMouseLeftButtonDown += TB_DXCallsign_PreviewMouseLeftButtonDown;
            // Bindings populate after the constructor, so defer the first overlay refresh to Loaded
            // priority — by then the bound value is present and we can render its 3-decimal form.
            Dispatcher.BeginInvoke(new Action(UpdateFrequencyDisplay), System.Windows.Threading.DispatcherPriority.Loaded);
            Dispatcher.BeginInvoke(new Action(UpdateRigLabel), System.Windows.Threading.DispatcherPriority.Loaded);

            // The form background, contest frames etc. are DynamicResource-bound in XAML; only the
            // grid header style needs a one-time assignment (its background is dynamic from there).
            ApplyQsoTableHeaderBackgroundFromSettings();
            UpdateContestLabelContrast();

            // Restrict every text box in the app to English (ASCII) input only. Registered as a
            // class handler so it applies to all TextBoxes without wiring each one individually,
            // and blocks non-Latin scripts (e.g. Hebrew) whether typed or pasted.
            EventManager.RegisterClassHandler(typeof(TextBox), UIElement.PreviewTextInputEvent,
                new TextCompositionEventHandler(GlobalTextBox_EnglishOnly_PreviewTextInput));
            EventManager.RegisterClassHandler(typeof(TextBox), DataObject.PastingEvent,
                new DataObjectPastingEventHandler(GlobalTextBox_EnglishOnly_Pasting));

            isInitializeComponentsComplete = true;
            ApplyCallsignSuggestionRowsSetting();
            LoadCallsignIndex();
            FetchCallsignListUpdateInfoFireAndForget();
            LoadNewCallsignsSet();
            _callsignUploader = new CallsignUploader(AppDomain.CurrentDomain.BaseDirectory);
            _callsignUploader.TrySendFireAndForget();

            // Quietly check country-files.com for a newer cty.dat. A downloaded update lands on
            // disk and is picked up on the next launch; failures (offline etc.) are ignored.
            CheckCtyDatUpdateFireAndForget();

            // Same for Club Log's date-aware database, which is what lets an old QSO be named by the
            // entity that existed on its date. Needs an API key; without one this does nothing at all.
            CheckClublogCtyUpdateFireAndForget();

            // Load the cached LoTW user list (for the yellow cluster highlight) and refresh it in the
            // background if it's missing or more than a week old. Failures are ignored.
            LotwUserService.Initialize();
            CheckLotwUpdateFireAndForget();

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

            ApplyHolyClusterListener();

            // The program must not wait on the internet in order to appear. Windows' own answer - is any
            // adapter up - costs nothing and sends nothing, so it is what the program believes for the
            // first moment. The real probe then runs in the background and corrects the flag, the network
            // light and the QRZ icon when it replies. Being optimistic for that moment is the safe way
            // round: everything gated on this flag makes a network call that has its own timeout, whereas
            // starting out "offline" would have those features refuse to try on a machine that is online.
            isNetworkAvailable = NetworkInterface.GetIsNetworkAvailable();
            _ = Task.Run(async () =>
            {
                bool online = await Helper.CheckForInternetConnectionAsync().ConfigureAwait(false);
                await Dispatcher.InvokeAsync(() =>
                {
                    isNetworkAvailable = online;
                    if (NetworkFlag != null)
                        NetworkFlag.Fill = online ? new SolidColorBrush(Color.FromRgb(0x00, 0xFF, 0x00))
                                                  : new SolidColorBrush(Color.FromRgb(0xFF, 0x00, 0x00));
                    if (!online) SetQrzConnected(false);
                });
            });
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
            TitlePrefix = title;   // custom title bar shows this until UpdateActiveLogTitle runs
            UpdateTitleClock();

            NetworkFlagItem.Visibility = Properties.Settings.Default.ShowNetworkFlag ? Visibility.Visible : Visibility.Collapsed;
            UpdateShareIconVisibility();

            // Runs ONCE per newly-installed version: user.config lives in a per-version folder, so a new
            // build starts with UpdateSettings=true, pulls the previous settings forward, then clears it.
            if (Properties.Settings.Default.UpdateSettings)
            {
                Properties.Settings.Default.Upgrade();
                // AFTER Upgrade (which would otherwise carry the operator's old value forward): put the
                // settings listed below back to their defined defaults.
                ForceSettingsToDefault(SettingsForcedToDefaultOnUpgrade);
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

            // Settings.Upgrade() above only bridges versions WITHIN one install identity; when the
            // installed path/identity changes between releases it carries nothing, blanking the online-
            // service logins. Restore any that are missing from the identity-independent mirror, then
            // refresh the mirror so it always reflects the current logins. Runs every start (not only on
            // a version change), so the mirror recovers even a store that a bad upgrade left half-empty.
            CredentialStore.RestoreMissing();
            CredentialStore.Backup();

            NormalizeEnterKeyBehaviorSettings();

            if (Properties.Settings.Default.isAutoCheckUpdates && isNetworkAvailable)
            {
                NotifyVersionUpToDate = false;
                // Defer the update check until after the main window is initialized and shown so any dialogs are
                // owned by the main window rather than the splash window (prevents them from being closed with the splash).
                Dispatcher.BeginInvoke(new Action(() => UpdatesMenuItem_Click(null, null)), DispatcherPriority.ApplicationIdle);
            }

            // THE FIRST RUN AFTER AN UPDATE SAYS WHAT CHANGED. Deferred like the update check above, so
            // the window it opens belongs to the main window and not to the splash - a dialog owned by
            // the splash is torn down with it. Runs whether or not automatic update checking is on:
            // this is about the version already installed, not about finding a newer one.
            Dispatcher.BeginInvoke(new Action(async () => await ShowWhatsNewIfVersionChanged()),
                                   DispatcherPriority.ApplicationIdle);

            this.Loaded += MainWindow_Loaded;
                Properties.Settings.Default.PropertyChanged += Settings_PropertyChanged;

            UpdateFreqModeRadios();
            UpdateTimeModeRadios();

            AdifHandlerWorker = new BackgroundWorker();
            AdifHandlerWorker.WorkerReportsProgress = true;
            AdifHandlerWorker.DoWork += AdifHandlerWorker_DoWork;
            AdifHandlerWorker.ProgressChanged += AdifHandlerWorker_ProgressChanged;
            AdifHandlerWorker.RunWorkerCompleted += AdifHandlerWorker_RunWorkerCompleted;
            
            TB_Exchange.IsEnabled = Properties.Settings.Default.validation_enabled;

            // Lock via IsReadOnly (not IsEnabled) so the field keeps full opacity — a disabled TextBox
            // dims to ~56%, which washed out the lock-blue background and greyed the text.
            TB_MyCallsign.IsReadOnly = Properties.Settings.Default.isLocked;
            TB_Operator.IsReadOnly = Properties.Settings.Default.isLocked;
            setLockBtnState();

            TB_Comment.IsEnabled = !Properties.Settings.Default.isCommentLocked;
            setLockCommentBtnState();

            try
            {
                dal = DataAccess.GetInstance();
            }
            catch (Exception e)
            {
                // WHAT TO DO ABOUT IT, not the database engine's own words. "attempt to write a
                // readonly database" is true and tells an operator nothing; it happened to one on the
                // first launch after installing, because the installer had not finished with the file.
                // The program waits and retries now, so reaching here means it really is stuck - and
                // then the two things worth saying are WHERE the file is and that closing everything
                // and starting again is the usual cure.
                HolyMessageBox.ShowError(
                    "HolyLogger cannot open your log database.\n\n"
                    + "If you have just installed or updated the program, give it a few seconds and "
                    + "start it again — the installer may not have finished with the file.\n\n"
                    + "The database is:\n"
                    + System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "4Z1KD", "HolyLogger", "logDB.db")
                    + "\n\nDetails: " + e.Message,
                    "Database Error", null, 620);
                System.Windows.Application.Current.Shutdown();
                return;
            }

            // Resolve the active Log (first run forces creating the Main Log). Must happen before the
            // QSO list is loaded below, since the table shows only the active log's QSOs.
            if (!EnsureActiveLog())
            {
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
            ResetRstForMode();

            // The activity row's program list - the same eight names the Other window and the QSO
            // editor offer, from the one place that holds them.
            FillActivitySigList();

            TB_MyCallsign.Focus();

            // POSITION IS NOT SET HERE. It used to be, with a "< 0 ? 0" clamp - and that clamp is wrong
            // on any desk whose second monitor sits to the left of or above the primary one, where every
            // position is legitimately negative: it dragged HolyLogger onto the primary screen. The real
            // restore is in Window_SourceInitialized, which runs before the window is shown and checks
            // that the saved spot is still reachable instead of squashing it to zero.
            Width = Properties.Settings.Default.MainWindowWidth;
            Height = Properties.Settings.Default.MainWindowHeight;

            //WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen;

            TP_Date.Value = DateTime.UtcNow;
            TP_Time.Value = DateTime.UtcNow;
            
            Qsos = dal.GetQSOsForLog(dal.ActiveLogId);
            Qsos.CollectionChanged += Qsos_CollectionChanged;
            DataContext = Qsos;
            UpdateActiveLogTitle();
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
            // (Pinned My Favorite Channels is reopened in MainWindow_Loaded, not here: ChannelsWindow
            //  sets Owner = this, and WPF refuses to take an owner that has not been shown yet.)

            // Ctrl+C: tell every template column what it stands for, or the copy is headings only.
            GridCopy.Enable(QSODataGrid);

            // Say so whenever the program writes a report, wherever in the program it happens.
            Reports.Written += OnReportWritten;

            ApplyLogColumnLayout();
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
                try { Properties.Settings.Default.Save(); } catch (System.Exception swallowed) { Log.Swallow(swallowed); }
            }
            else if (addQsoWithEnter && doNothing)
            {
                Properties.Settings.Default.DoNothing = false;
                try { Properties.Settings.Default.Save(); } catch (System.Exception swallowed) { Log.Swallow(swallowed); }
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
                            CopyLoggedQsoToTargetLog(q);
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
                            // N1MM+ sends TXFreq in units of 10 Hz (e.g. 352211 = 3.52210 MHz). TB_Frequency
                            // holds MHz everywhere else (CAT / cluster setters and convertFreqToBand), so
                            // convert 10-Hz -> MHz (÷100000). The old ÷100 produced kHz, which the MHz box
                            // showed ~1000× too high.
                            double freqMhz = freq / 100000.0;
                            TB_Frequency.Text = freqMhz.ToString("0.0#####", System.Globalization.CultureInfo.InvariantCulture);
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
            // Only the matrix's own visibility changes with the setting. The form row and map keep
            // their full height either way, so hiding the matrix leaves an empty area of the same
            // size in its place and the rest of the GUI (log table below, map on the right) does not
            // move.
            MatrixC.Visibility = Properties.Settings.Default.IsShowMatrixControl
                ? Visibility.Visible
                : Visibility.Hidden;

            // THE HEIGHT IS THE XAML'S TO DECIDE, NOT THIS METHOD'S. These two lines used to re-assert
            // 325 every time the matrix was shown or hidden, which silently overruled the form row and
            // the map set in MainWindow.xaml - so growing the form there changed nothing on screen and
            // the X-array was cut off at the bottom. Only the matrix's own visibility belongs here; the
            // comment above says as much, and now the code does too.
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

        // Twice the 344-pixel height of QRZGraphicsBorder in the XAML, so the photo is still sharp at
        // 150% and 200% display scaling. Change it if that border's height changes.
        private const int QrzPhotoBoxDecodeHeight = 688;

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
                // Decoded at the size of the box it goes into, not the size it arrives at. QRZ photos
                // come as the operator uploaded them - some are 3000 pixels across, which is 24 MB of
                // memory once decoded, all of it to fill a box 344 pixels tall, and the CPU shrinks it
                // on every repaint. This costs nothing on a strong machine and a good deal on a weak
                // one, and it happens for every callsign looked up.
                //
                // Height only, so the proportions are WPF's to keep, and doubled so the picture is
                // still sharp on a 150% or 200% display. The pop-out photo window has its own download
                // and is deliberately left at full size - that is the one meant to be looked at.
                bitmap.DecodePixelHeight = QrzPhotoBoxDecodeHeight;
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

        // Offers to correct QSOs whose frequency was stored in kHz instead of MHz (see
        // DataAccess.FindKhzFrequencyFixes). Nothing is written without the operator saying yes: this
        // is their logged data, and a program that quietly rewrites a log is a program you cannot trust.
        //
        // The full before/after list is written to the Desktop BEFORE the question is asked, so the
        // answer can be given with the actual QSOs in front of you rather than on the word of a dialog.
        private void OfferFrequencyRepair()
        {
            try
            {
                if (dal == null) return;

                var fixes = dal.FindKhzFrequencyFixes();
                if (fixes.Count == 0) return;

                // "Not now" is remembered as the number of QSOs it was said about, rather than a plain
                // yes/no. Nothing changes -> the question stays away; but import a log that brings in
                // more of them and the count differs, so the offer comes back for the new ones.
                if (Properties.Settings.Default.FreqRepairDeclinedCount == fixes.Count) return;

                string reportPath = System.IO.Path.Combine(
                    DataAccess.ReportsFolder,
                    "holylogger_frequency_changes.txt");

                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"HolyLogger — frequency corrections — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine();
                sb.AppendLine("These QSOs have their frequency stored in kHz. HolyLogger works in MHz,");
                sb.AppendLine("so they show wrongly in the log and are uploaded wrongly to LoTW, eQSL,");
                sb.AppendLine("QRZ and Club Log. Listed below is exactly what would change.");
                sb.AppendLine();
                sb.AppendLine($"{"DATE",-12}{"TIME",-8}{"CALLSIGN",-14}{"BAND",-8}{"WAS",-16}{"BECOMES",-16}");
                sb.AppendLine(new string('-', 74));
                foreach (var f in fixes)
                    sb.AppendLine($"{f.Date,-12}{f.Time,-8}{f.Callsign,-14}{f.Band,-8}{f.OldFreq,-16}{f.NewFreq,-16}");
                sb.AppendLine();
                sb.AppendLine($"Total: {fixes.Count} QSO(s).");
                sb.AppendLine();
                sb.AppendLine("QSOs with no band, and any value that would not land back inside the band");
                sb.AppendLine("it is logged on, are never touched — they are not in this list.");
                System.IO.File.WriteAllText(reportPath, sb.ToString(), System.Text.Encoding.UTF8);
                Reports.Note(reportPath);

                bool confirmed = HolyMessageBox.ShowConfirm(
                    $"{fixes.Count} QSO(s) in your log have the frequency stored in kHz (for example " +
                    $"\"{fixes[0].OldFreq}\" instead of \"{fixes[0].NewFreq}\").\n\n" +
                    "This is an old HolyLogger fault, not something you did. Those QSOs are uploaded to " +
                    "LoTW, eQSL, QRZ and Club Log with a wrong frequency.\n\n" +
                    "The full list of what would change was saved to your Desktop:\n" +
                    "holylogger_frequency_changes.txt\n\n" +
                    "Correct them now?",
                    "Correct QSO frequencies", HolyMsgType.Warning, this);

                if (!confirmed)
                {
                    Properties.Settings.Default.FreqRepairDeclinedCount = fixes.Count;
                    Properties.Settings.Default.Save();
                    return;
                }

                int changed = dal.ApplyFrequencyFixes(fixes);
                Properties.Settings.Default.FreqRepairDeclinedCount = 0;
                Properties.Settings.Default.Save();

                if (changed > 0)
                {
                    Qsos = dal.GetQSOsForLog(dal.ActiveLogId);
                    HolyMessageBox.ShowSuccess(
                        $"{changed} QSO(s) corrected.\n\n" +
                        "The list of changes is on your Desktop:\nholylogger_frequency_changes.txt",
                        "Frequencies corrected", this);
                }
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // Fills the entity number in for every QSO logged before there was a column to put it in.
        //
        // ON A BACKGROUND THREAD and never awaited: it reads tens of thousands of rows and resolves each
        // callsign, and the operator did not ask for it - it must not hold the window for a second. It
        // only ever writes rows whose number is missing, so an interrupted run simply finishes next time,
        // and a number the operator has corrected by hand is never overwritten.
        private void StartEntityCodeBackfill()
        {
            Task.Run(() =>
            {
                try
                {
                    var dal = DataAccess.GetInstance();
                    if (dal == null) return;
                    int filled = dal.BackfillEntityCodes(EntityCodeForCall);
                    if (filled > 0)
                        Log.Warn($"Entity numbers filled in for {filled:N0} QSO(s) logged before the column existed.");
                }
                catch (System.Exception swallowed) { Log.Swallow(swallowed); }
            });
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // Old QSOs have no entity number; this fills them in, once, quietly, in the background.
            StartEntityCodeBackfill();

            // The active profile's file was gone at startup, so factory defaults were loaded. Say so
            // once the main window exists (it owns the dialog) instead of letting the whole setup
            // change without explanation.
            if (!string.IsNullOrWhiteSpace(App.MissingProfileAtStartup))
            {
                string missing = App.MissingProfileAtStartup;
                Dispatcher.BeginInvoke(new Action(() =>
                    HolyMessageBox.ShowWarning(
                        $"The profile \"{missing}\" could not be found, so HolyLogger started with the " +
                        "factory default settings.\n\n" +
                        "Your logs and QSOs are not affected.",
                        "Profile not found", this)), DispatcherPriority.ApplicationIdle);
            }

            // Old logs can hold frequencies in kHz; offer to correct them. Deferred to idle so it lands
            // after the window is on screen rather than in the middle of the startup sequence.
            Dispatcher.BeginInvoke(new Action(OfferFrequencyRepair), DispatcherPriority.ApplicationIdle);

            // MY FAVORITE CHANNELS COMES BACK ONLY IF IT IS PINNED. Nothing else opens it - not "it was
            // open last time", which was tried and taken out again: it kept putting the window on screen
            // when it was not wanted, and a window that reappears after being closed is worse than one
            // that has to be fetched from the menu. The pin is the operator's own explicit "always bring
            // this back", and it is the whole rule.
            //
            // Done HERE rather than in the constructor because ChannelsWindow sets Owner = this, and WPF
            // throws "Cannot set Owner property to a Window that has not been shown previously" while the
            // main window is still being built.
            Log.Warn("My Favorite Channels at startup: pinned="
                     + Properties.Settings.Default.ChannelsWindowPinned);

            if (Properties.Settings.Default.ChannelsWindowPinned)
            {
                // ShowChannelsWindow, NOT the menu handler: the menu handler deliberately unpins, and
                // nothing about reopening it here is a request to change the pin.
                try { ShowChannelsWindow(); }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
            }

            // Re-assert the log name in the title bar once the window is fully loaded. The constructor
            // already sets it, but if that early call hit a transient DB hiccup the title would be left
            // bare; doing it again here (dal + ActiveLogId are settled by now) makes it reliable.
            UpdateActiveLogTitle();

            // Show the red "copying is live" dot if the active log already copies its QSOs elsewhere.
            RefreshCopyIndicator();

            // A log that already has QSOs but no identity (legacy log) gets prompted now, at startup. An
            // empty log waits — its identity is set when you import into it or log the first QSO.
            EnsureActiveLogHasIdentity(promptIfEmpty: false);
            SyncCallsignToActiveLog();   // startup: box shows the active log's callsign (no stray lock)

            ApplyClusterWindowSetting();

            _stickyWindow = new StickyWindow(this);
            _stickyWindow.StickToScreen = false;
            _stickyWindow.StickToOther = true;
            _stickyWindow.StickOnResize = true;
            _stickyWindow.StickOnMove = true;

            RestartHeartbeatTimer();

            // Always run the 1-second UTC timer: besides the optional title clock it now keeps
            // the QSO Date/Time pickers current (see UTCTimer_Elapsed).
            StartUTCTimer();

            MapControl.RadiusChanged += OnMapRadiusChanged;
            MapControl.SpotTuneRequested += OnMapSpotTuneRequested;
            MapControl.SpotHovered += OnMapSpotHovered;
            MapControl.SpotHoverEnded += OnMapSpotHoverEnded;
            ShowHomeMap();

            // Reflect the persisted suggestions on/off state on the Suggest (F4) toggle button.
            if (BtnSuggestToggle != null)
                BtnSuggestToggle.IsChecked = Properties.Settings.Default.CallsignSuggestionsEnabled;

            // Restore the active contest (if any) selected in a previous session, then reflect the
            // Contest Mode state in the Tools-menu header, trophy, and contest-name label.
            Contests.ContestService.Activate(
                Contests.ContestService.FindById(Properties.Settings.Default.ActiveContestId));
            UpdateContestIndicator();
            ApplyContestExchangeUI();

            // eQSL queue: show how many QSOs are waiting (only for callsigns the user added to the
            // eQSL table). Nothing is sent automatically here.
            UpdateEqslQueueIndicator();

            // Try Again: the list survives between sessions, so the button has to know at startup
            // whether anybody is still waiting on it. It stays hidden when nobody is.
            RefreshTryAgain();

            // QRZ Logbook: show pending count and silently retry any QSOs that could not be pushed
            // earlier (e.g. logged while offline).
            UpdateQrzMenuCount();
            _ = PumpQrzQueue();

            // Club Log: same idea — show the pending count and silently drain the queue if the service
            // is on and configured (e.g. QSOs logged while offline get pushed now).
            UpdateClublogMenuCount();
            _ = PumpClublogQueue();

            // Initialize RST fields based on the selected mode after window is fully loaded
            ResetRstForMode();

            // One-time, skippable offer to set an off-machine backup folder. Deferred to ApplicationIdle
            // so it appears only after the startup splash has closed (the splash is Topmost and would
            // otherwise cover it) and never holds up the window finishing its load.
            Dispatcher.BeginInvoke(new Action(MaybePromptForExtraBackupFolder),
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        // Shown once (to both new and existing users) so the extra-backup-folder feature gets
        // discovered. The ExtraBackupPrompted flag guarantees it is never shown again, whatever the
        // user chooses. If a folder is already set, we just mark it prompted and skip.
        private void MaybePromptForExtraBackupFolder()
        {
            var s = Properties.Settings.Default;
            if (s.ExtraBackupPrompted)
                return;

            // Mark as asked up front, so a crash/close mid-prompt can't make it reappear next launch.
            s.ExtraBackupPrompted = true;
            s.Save();

            if (!string.IsNullOrWhiteSpace(s.ExtraBackupFolder))
                return;   // already configured (e.g. via Backups & Restore) -- nothing to offer

            bool wantsToSet = HolyMessageBox.ShowConfirm(
                "HolyLogger keeps daily backups of your log on this computer.\n\n" +
                "Would you like to also save a copy of each backup to another folder — for example a " +
                "cloud folder (Dropbox / OneDrive / Google Drive) or an external drive? That gives you an " +
                "off-machine copy in case something happens to this PC.\n\n" +
                "You can always set or change this later in File → Backups & Restore.",
                "Extra backup copy", HolyMsgType.Info, this);

            if (!wantsToSet)
                return;

            if (BackupRestoreWindow.TryPickWritableFolder(this, null, out string chosen))
            {
                s.ExtraBackupFolder = chosen;
                s.Save();
                HolyMessageBox.ShowSuccess("Extra backups will be saved to:\n" + chosen, "Extra backup copy", this);
            }
        }

        // Custom title bar button handlers (WindowStyle="None" -- see MainWindow.xaml). These call
        // the same SystemCommands the native caption buttons would, so behavior (Aero snap,
        // taskbar thumbnail preview, Alt+Space system menu, etc.) is unchanged.
        private void TitleBar_Minimize_Click(object sender, RoutedEventArgs e) => System.Windows.SystemCommands.MinimizeWindow(this);

        private void TitleBar_MaxRestore_Click(object sender, RoutedEventArgs e)
        {
            if (WindowState == WindowState.Maximized) System.Windows.SystemCommands.RestoreWindow(this);
            else System.Windows.SystemCommands.MaximizeWindow(this);
        }

        private void TitleBar_Close_Click(object sender, RoutedEventArgs e) => System.Windows.SystemCommands.CloseWindow(this);

        // Keeps the maximize/restore glyph in sync when the window is maximized/restored by any
        // means (the button, double-click on the title bar, Win+Up/Down, dragging to a screen edge).
        private void MainWindow_StateChanged(object sender, EventArgs e)
        {
            if (TitleBar_MaxRestoreBtn == null) return;
            bool maximized = WindowState == WindowState.Maximized;
            TitleBar_MaxRestoreBtn.Content = maximized ? "\uE923" : "\uE922";
            TitleBar_MaxRestoreBtn.ToolTip = maximized ? "Restore Down" : "Maximize";
        }

        private void Window_SourceInitialized(object sender, EventArgs e)
        {
            // Restore window position and size before first show.
            if (Properties.Settings.Default.MainWindowWidth > 0)
                Width = Properties.Settings.Default.MainWindowWidth;
            if (Properties.Settings.Default.MainWindowHeight > 0)
                Height = Properties.Settings.Default.MainWindowHeight;

            double savedLeft = Properties.Settings.Default.MainWindowLeft;
            double savedTop  = Properties.Settings.Default.MainWindowTop;

            // Guard against a position saved off-screen: a minimized window reports
            // (-32000,-32000), and a monitor that's since been disconnected leaves a
            // position outside the virtual desktop. Either way the window would open
            // where the user can't see it ("program doesn't load"). Fall back to a
            // visible spot on the primary work area.
            if (!IsPositionOnScreen(savedLeft, savedTop))
            {
                savedLeft = SystemParameters.WorkArea.Left + 40;
                savedTop  = SystemParameters.WorkArea.Top + 40;
            }

            Left = savedLeft;
            Top  = savedTop;

            hasRestoredMainWindowBounds = true;
        }

        // The working area of the monitor that holds the given point - the primary one only if the
        // point really is there. Used to open a window on the screen HolyLogger is on rather than on
        // whichever monitor Windows calls primary, which is all SystemParameters.WorkArea can tell us.
        private static Rect WorkAreaContaining(double x, double y)
        {
            try
            {
                double scale;
                using (var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero))
                    scale = g.DpiX / 96.0;
                if (scale <= 0) scale = 1.0;

                foreach (var sc in System.Windows.Forms.Screen.AllScreens)
                {
                    var wa = sc.WorkingArea;
                    var inDips = new Rect(wa.Left / scale, wa.Top / scale, wa.Width / scale, wa.Height / scale);
                    if (inDips.Contains(x, y)) return inDips;
                }
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
            return SystemParameters.WorkArea;
        }

        // True when the given top-left corner falls inside the current virtual screen
        // (with a margin so at least a grabbable sliver of the title bar is reachable).
        private static bool IsPositionOnScreen(double left, double top)
        {
            if (double.IsNaN(left) || double.IsNaN(top) ||
                double.IsInfinity(left) || double.IsInfinity(top))
                return false;

            // The virtual screen is the BOUNDING BOX around every monitor, so on any arrangement that
            // is not a neat row it includes corners no monitor covers - and a saved position in one of
            // those passed this test and opened the window where it could not be seen or grabbed. Ask
            // the monitors themselves, and ask about the one spot that matters: a point on the title
            // bar, past the icon, which is what the mouse needs to drag the window anywhere else.
            double grabX = left + 60;
            double grabY = top + 12;

            try
            {
                double scale;
                using (var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero))
                    scale = g.DpiX / 96.0;
                if (scale <= 0) scale = 1.0;

                foreach (var sc in System.Windows.Forms.Screen.AllScreens)
                {
                    var wa = sc.WorkingArea;
                    if (grabX >= wa.Left / scale && grabX <= wa.Right  / scale - 40 &&
                        grabY >= wa.Top  / scale && grabY <= wa.Bottom / scale - 40)
                        return true;
                }
                return false;
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }

            double vsLeft   = SystemParameters.VirtualScreenLeft;
            double vsTop    = SystemParameters.VirtualScreenTop;
            double vsRight  = vsLeft + SystemParameters.VirtualScreenWidth;
            double vsBottom = vsTop  + SystemParameters.VirtualScreenHeight;

            return left >= vsLeft - 10 && top >= vsTop &&
                   left <= vsRight - 100 && top <= vsBottom - 60;
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

        // Clips the whole window content to the same rounded rectangle as the white frame (radius 8,
        // minus the 2px border => 6), so the top corners (title bar) follow the curve like the bottom
        // ones instead of showing square over the rounded border.
        private void AddLogGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            AddLogGrid.Clip = new System.Windows.Media.RectangleGeometry(
                new Rect(0, 0, AddLogGrid.ActualWidth, AddLogGrid.ActualHeight), 6, 6);
        }

        private void UTCTimer_Elapsed(object sender, EventArgs e)
        {
            this.Dispatcher.Invoke(() =>
            {
                UpdateTitleClock();
                // Keep the QSO Date/Time pickers ticking with UTC. Two things stop them: a QSO being
                // edited (state != New), which must not have the clock typed over it, and the status
                // bar's Time toggle set to Manual.
                //
                // The FREQUENCY toggle (CAT/Manual) deliberately has no say here any more: where the
                // frequency comes from and whether the clock runs are unrelated questions, and answering
                // one by answering the other surprised operators who had only meant to type a frequency.
                if (state == State.New && !Properties.Settings.Default.isTimeManual)
                {
                    TP_Date.Value = DateTime.UtcNow;
                    TP_Time.Value = DateTime.UtcNow;
                }
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
                UpdateClublogMenuCount();

                // A deleted QSO is gone from the DB, so it drops out of the eQSL waiting list —
                // refresh the "!" badge / menu count (and any open queue window) right away. (A
                // single-QSO delete also removes its copy-to-log partner, which may free a queue slot
                // in another log — the refreshed counts pick that up too.)
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
            // Lock via IsReadOnly (not IsEnabled) so the field keeps full opacity — a disabled TextBox
            // dims to ~56%, which washed out the lock-blue background and greyed the text.
            TB_MyCallsign.IsReadOnly = Properties.Settings.Default.isLocked;
            TB_Operator.IsReadOnly = Properties.Settings.Default.isLocked;
            //TB_MyGrid.IsEnabled = !Properties.Settings.Default.isLocked;
            setLockBtnState();

            // Locking approves the callsign as typed, so it must pass the same identity guard as
            // leaving the box (this image click doesn't move focus, so LostFocus won't run it).
            if (Properties.Settings.Default.isLocked)
                CommitStationCallsignEdit();
        }

        private void setLockBtnState()
        {
            bool locked = Properties.Settings.Default.isLocked;
            LockMask.ImageSource = locked ? lock_path : unlock_path;

            var lightRed  = new SolidColorBrush(Color.FromRgb(0xFF, 0xB0, 0xB0)); // unlocked / editable
            var lightBlue = new SolidColorBrush(Color.FromRgb(0x64, 0xB5, 0xF6)); // locked (matches the status-bar lock)
            var bg = locked ? lightBlue : lightRed;

            if (LockBtnBorder != null) LockBtnBorder.Background = bg;

            // Callsign fields carry the same red/blue background as the lock, with bold black text in
            // both states (legible on the light red and the vivid lock-blue alike).
            TB_MyCallsign.Background = bg;
            TB_Operator.Background = bg;
            TB_MyCallsign.Foreground = System.Windows.Media.Brushes.Black;
            TB_Operator.Foreground = System.Windows.Media.Brushes.Black;
            TB_MyCallsign.FontWeight = FontWeights.Bold;
            TB_Operator.FontWeight = FontWeights.Bold;
        }

        private void LockComment_Btn_MouseUp(object sender, MouseButtonEventArgs e)
        {
            Properties.Settings.Default.isCommentLocked = !Properties.Settings.Default.isCommentLocked;
            TB_Comment.IsEnabled = !Properties.Settings.Default.isCommentLocked;
            setLockCommentBtnState();
        }

        private void setLockCommentBtnState()
        {
            if (!Properties.Settings.Default.isCommentLocked) LockCommentMask.ImageSource = unlock_path;
            else LockCommentMask.ImageSource = lock_path;
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

        // The band to store with a QSO. Read out of the frequency as it always was; only when there is
        // no frequency to read does the band the operator picked stand in for it. Written this way round
        // so that with a frequency present the answer is byte-for-byte the one this used to give.
        private string BandForLog()
        {
            // Only when there really is a frequency - a stale stored one behind an emptied box must not
            // decide the band of the QSO being logged.
            if (!FrequencyIsEmpty)
            {
                string fromFreq = HolyLogParser.convertFreqToBand(TB_Frequency.Text);
                if (!string.IsNullOrWhiteSpace(fromFreq)) return fromFreq;
            }
            return (TB_Band.Text ?? string.Empty).Trim();
        }

        // The frequency to store. What was typed, or - for a QSO logged by band alone - that band's
        // calling frequency, which is where such a contact almost certainly was. It is an estimate, not
        // a reading, but a QSO with no frequency at all is worse: the log grid, the map and every
        // confirmation service that matches on frequency have nothing to work with.
        private string FreqForLog(string band)
        {
            string typed = FrequencyIsEmpty ? string.Empty : (TB_Frequency.Text ?? string.Empty).Trim();
            if (typed.Length > 0) return typed;
            if (string.IsNullOrWhiteSpace(band)) return string.Empty;
            return HolyLogParser.BandModeToFreq(band, CB_Mode != null ? CB_Mode.Text : null);
        }

        private void AddBtn_Click(object sender, RoutedEventArgs e)
        {
            // Before anything else: a QSO must have a log to go into. Without this the INSERT would
            // name a log id that matches no row, and the contact would be stored where nothing reads.
            if (!RequireActiveLog("log a QSO into")) return;
            if (!Validate()) return;
            // Optional HAM-frequency check (Options > General). Applies in Manual AND CAT: if the
            // frequency is not an amateur band the operator is warned and can save anyway, bail out, or
            // jump to the setting to switch the check off. Runs after Validate so field-presence is
            // already satisfied and this is the only thing standing between here and the log.
            if (!ConfirmHamFrequencyBeforeSave()) return;
            // A QSO can't be logged into a log that has no identity yet — enforce it here too (belt and
            // suspenders; startup / log-switch already prompt).
            if (!EnsureActiveLogHasIdentity())
            {
                HolyMessageBox.ShowWarning("Set this log's identity (station callsign + operator) before logging QSOs into it.",
                    "Log identity required", this);
                return;
            }
            // A malformed activity reference is worth one question before it goes into the log, because
            // an award program matching on it will never find "EU-5". Answering "log it anyway" keeps
            // what was typed - the operator's data is never silently dropped.
            if (!ConfirmActivityBeforeSave()) return;
            if (state == State.New)
            {
                QSO qso = new QSO();
                qso.Comment = TB_Comment.Text;
                qso.DXCall = TB_DXCallsign.Text;
                qso.Mode = CB_Mode.Text;
                qso.SRX = TB_Exchange.Text;
                qso.Band = BandForLog();
                qso.Freq = FreqForLog(qso.Band);
                qso.Country = Country;
                qso.Continent = Continent;
                qso.CQZone = TB_CQZone.Text;
                qso.ITUZone = TB_ITUZone.Text;
                qso.State = TB_State.Text;          // ADIF STATE - now stored with the QSO
                qso.Qth = TB_QTH.Text;              // ADIF QTH - the worked station's town
                qso.DxccCode = EntityCodeForCall(qso.DXCall, qso.Date);   // the country's identity
                qso.Name = FName.Length > 25 ? FName.Substring(0,25): FName;
                qso.MyCall = TB_MyCallsign.Text;
                qso.Operator = TB_Operator.Text;
                qso.STX = ContestSendExchangeForLog();
                qso.MyLocator = TB_MyLocator.Text;
                qso.DXLocator = TB_DXLocator.Text;
                ActivityToQso(qso);                 // IOTA / SOTA / POTA / WWFF and the Other pair
                qso.RST_RCVD = TB_RSTRcvd.Text;
                qso.RST_SENT = TB_RSTSent.Text;
                DateTime date = TP_Date.Value.Value;
                qso.Date = date.Year.ToString("D4") + date.Month.ToString("D2") + date.Day.ToString("D2");
                DateTime time = TP_Time.Value.Value;
                qso.Time = time.Hour.ToString("D2") + time.Minute.ToString("D2") + time.Second.ToString("D2");
                qso.PROP_MODE = Properties.Settings.Default.IsSatelliteMode ? "SAT" : "";
                qso.SAT_NAME = "";
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

                    // Copy-to-log: mirror this QSO into the active log's copy-target, if configured.
                    CopyLoggedQsoToTargetLog(LastQSO);

                    if (QSODataGrid.Items != null && QSODataGrid.Items.Count > 0)
                        QSODataGrid.ScrollIntoView(QSODataGrid.Items[0]);

                    AddWorkedCountryAndRefreshCluster(qso.DXCall);

                    // He was on the Try Again list and he is in the log now, so he comes off it.
                    RemoveFromTryAgainAfterLogging(qso.DXCall);

                    // In a contest whose sent exchange is a serial number, advance it for the next QSO.
                    AdvanceContestSerial();

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

                    // Real-time push of THIS just-logged QSO to Club Log (fire and forget). Does
                    // nothing unless the Club Log service is enabled with credentials configured.
                    _ = SendOneQsoToClublog(qso);
                }
                catch (Exception ex)
                {
                    HolyMessageBox.ShowError("Failed to save QSO: " + ex.Message, "Save Error", this);
                }
            }
            else if (state == State.Edit)
            {
                // WHAT IT SAID BEFORE, taken before a single field is overwritten. The main window
                // could delete a QSO and put it back, but an EDIT was final: a mistyped callsign or a
                // date changed by accident had to be found and typed again from memory. The Log
                // Workshop has had an undo for its edits all along; this side had none.
                _editUndoTarget = QsoToUpdate;
                _editUndoBefore = CopyOfQso(QsoToUpdate);

                QsoToUpdate.Comment = TB_Comment.Text;
                QsoToUpdate.DXCall = TB_DXCallsign.Text;
                QsoToUpdate.Mode = CB_Mode.Text;
                QsoToUpdate.SRX = TB_Exchange.Text;
                QsoToUpdate.Band = BandForLog();
                QsoToUpdate.Freq = FreqForLog(QsoToUpdate.Band);
                QsoToUpdate.Country = Country;
                QsoToUpdate.Continent = Continent;
                QsoToUpdate.CQZone = TB_CQZone.Text;
                QsoToUpdate.ITUZone = TB_ITUZone.Text;
                QsoToUpdate.State = TB_State.Text;   // ADIF STATE - now stored with the QSO
                QsoToUpdate.Qth = TB_QTH.Text;       // ADIF QTH - the worked station's town
                QsoToUpdate.DxccCode = EntityCodeForCall(QsoToUpdate.DXCall, QsoToUpdate.Date);
                QsoToUpdate.Name = TB_DX_Name.Text.Length > 25 ? TB_DX_Name.Text.Substring(0, 25) : TB_DX_Name.Text; //FName.Length > 25 ? FName.Substring(0, 25) : FName;
                QsoToUpdate.MyCall = TB_MyCallsign.Text;
                QsoToUpdate.Operator = TB_Operator.Text;
                QsoToUpdate.STX = TB_MyHolyland.Text;
                QsoToUpdate.MyLocator = TB_MyLocator.Text;
                QsoToUpdate.DXLocator = TB_DXLocator.Text;
                ActivityToQso(QsoToUpdate);         // IOTA / SOTA / POTA / WWFF and the Other pair
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

                ShowEditUndoBar();

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
            // Remember whatever F9 clears so the on-frequency auto-fill won't put it straight back -- and
            // so it can't sneak back into the DX box and get captured into the undo history (the "undo
            // brought back a call I'd F9'd" bug). Recorded UNCONDITIONALLY for any non-empty callsign:
            // earlier versions gated this on on-frequency detection, which momentarily flickers (e.g.
            // while hovering a band checkbox), and in that window F9 recorded nothing and the call
            // reappeared. The dismissal is released only when the radio moves to ANOTHER filled spot, or
            // when the spot is double-clicked (both in UpdateClusterFrequencyHighlight / TuneToClusterSpot)
            // -- NOT by merely tuning off and back, so a dismissed spot stays dismissed. Harmless for a
            // user-typed call that isn't a spot -- there's simply no on-frequency spot for it to block.
            //
            // ...but ONLY when the operator pressed F9 / Clear. The cluster clears this box itself when
            // the radio tunes away from a spot it auto-filled, and that cleanup comes through here too.
            // Recording a dismissal for it meant that tuning off a spot and back on could never re-fill
            // the callsign: the auto-fill saw its own earlier clear as the operator's refusal, and the
            // DX box stayed empty with the spot sitting green on frequency.
            string clearedCall = (TB_DXCallsign.Text ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(clearedCall) && !IsClusterAutoClearingDxCall)
                _clusterDismissedCall = clearedCall.ToUpperInvariant();

            //TB_Frequency.Text = string.Empty;
            // Drop any stuck map-hover blue highlight on the cluster rows.
            SetClusterRowMapHighlight(null);

            // ...and the clicked-row selection with it. The highlighted row is what says "this is the
            // station in the form"; once the form has been emptied - by F9, or by the cluster clearing
            // a call whose station is no longer on frequency - a row left highlighted points at nothing.
            try { if (clusterSpotsGrid?.SelectedItem != null) clusterSpotsGrid.UnselectAll(); }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
            TB_DXCallsign.Clear();
            TB_Exchange.Clear();
            // Contest mode: also empty the received-exchange cells beside RST-R (they mirror into
            // TB_Exchange, which we just cleared, but the visible cells keep their own text).
            ClearContestReceivedExchange();
            TB_DXLocator.Clear();
            TB_ITUZone.Text = "";
            TB_CQZone.Text = "";

            ResetRstForMode();
            if (TB_Comment.IsEnabled) TB_Comment.Clear();
            ClearActivityRow();
            TB_State.Text = string.Empty;
            TB_QTH.Text = string.Empty;
            FName = string.Empty;
            Country = string.Empty;
            UpdateCountryFlag(null);

            ShowDxccCode(0);
            ClearQrzPhoto();
            Continent = string.Empty;
            // Clearing the form starts a new contact, so its date and time are now - unless the operator
            // is holding them (Time: Manual), e.g. while typing up a page of contacts made off-line.
            if (!Properties.Settings.Default.isTimeManual)
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
                _foreignFilterRows = null;
                DataContext = Qsos;
            }
        }

        // Resolves which Log is active at startup. On the first run of the multi-log version (no logs
        // exist yet), the user is forced to create the Main Log and choose what happens to any existing
        // QSOs. Returns false only if the user dismissed the mandatory setup dialog (app then shuts down).
        private bool EnsureActiveLog()
        {
            if (dal == null) return false;
            try
            {
                if (dal.GetLogCount() == 0)
                {
                    int existing = dal.CountUnassignedQSOs();

                    // The startup splash is Topmost and would hide this mandatory dialog behind it
                    // (the splash only closes once the main window has rendered, which happens after
                    // this call). Drop the splash's topmost so the setup window is sure to be on top
                    // and clickable.
                    foreach (var splash in Application.Current.Windows.OfType<SplashWindow>())
                        splash.Topmost = false;

                    // Startup shows an application-wide "wait" spinner (set while the splash is up and
                    // only cleared once the main window renders — which is after this dialog). Clear it
                    // while this interactive dialog is open so the pointer is a normal arrow and the user
                    // doesn't think the app is still busy; restore it afterwards for the rest of startup.
                    var savedCursor = System.Windows.Input.Mouse.OverrideCursor;
                    System.Windows.Input.Mouse.OverrideCursor = null;

                    // No Owner: the main window has no position yet at this point in startup (its Left
                    // and Top are restored further down). But it WILL open where its saved position
                    // says, so put this dialog on THAT monitor. The XAML asked for CenterScreen, and
                    // CenterScreen always means the primary screen - so on a two-monitor desk the first
                    // thing a new operator ever sees appeared on the other screen from the program.
                    var setup = new LogSetupWindow(existing);
                    Rect setupArea = WorkAreaContaining(Properties.Settings.Default.MainWindowLeft,
                                                        Properties.Settings.Default.MainWindowTop);
                    setup.WindowStartupLocation = WindowStartupLocation.Manual;
                    // Centred once its real size is known - the window sizes itself to its content, so
                    // the height does not exist until it has been laid out.
                    setup.Loaded += (snd, ev) =>
                    {
                        setup.Left = setupArea.Left + Math.Max(0, (setupArea.Width  - setup.ActualWidth)  / 2);
                        setup.Top  = setupArea.Top  + Math.Max(0, (setupArea.Height - setup.ActualHeight) / 2);
                    };
                    setup.ShowDialog();

                    System.Windows.Input.Mouse.OverrideCursor = savedCursor;
                    if (!setup.Completed) return false;

                    long mainId = dal.CreateLog(setup.LogName, "");   // day-by-day log, no event type
                    if (setup.ImportExisting)
                    {
                        dal.AssignUnassignedToLog(mainId);
                    }
                    else if (existing > 0)
                    {
                        // Option B: keep the old QSOs safe in a separate log; the new one stays empty.
                        long prevId = dal.CreateLog(UniqueLogName("Previous Log"), "");
                        dal.AssignUnassignedToLog(prevId);
                    }
                    dal.ActiveLogId = mainId;
                }
                else
                {
                    dal.ActiveLogId = Properties.Settings.Default.ActiveLogId;
                    // NoLogId is not a missing setting - it is the operator having closed or deleted
                    // their last log. That choice survives a restart; the program opens with no log,
                    // exactly as they left it. Any OTHER id that no longer exists is a log that went
                    // missing (a restore, a file swapped underneath us) - or a setting never written,
                    // which is every user upgrading from before this existed - and opening the first
                    // log is still the right answer for those.
                    if (dal.ActiveLogId != DataAccess.NoLogId && dal.GetLogName(dal.ActiveLogId) == null)
                    {
                        var logs = dal.GetLogs();
                        if (logs.Count > 0) dal.ActiveLogId = logs[0].Id;
                    }
                }
                Properties.Settings.Default.ActiveLogId = dal.ActiveLogId;
                Properties.Settings.Default.Save();
                return true;
            }
            catch (Exception ex)
            {
                HolyMessageBox.ShowError("Failed to set up your log: " + ex.Message, "Log setup");
                return false;
            }
        }

        // Returns baseName, or baseName + " 2"/" 3"/... if that name is already taken by a log.
        private string UniqueLogName(string baseName)
        {
            string name = baseName;
            int i = 2;
            while (dal.LogNameExists(name)) name = baseName + " " + (i++);
            return name;
        }

        // The custom title bar shows the title in three runs so just "Log:" can be bold: prefix, the bold
        // "Log:" label, then the log name. (Window.Title stays a plain string for the taskbar.)
        private string _titlePrefix = string.Empty;
        public string TitlePrefix { get => _titlePrefix; private set { _titlePrefix = value; OnPropertyChanged("TitlePrefix"); } }
        private string _titleLogLabel = string.Empty;
        public string TitleLogLabel { get => _titleLogLabel; private set { _titleLogLabel = value; OnPropertyChanged("TitleLogLabel"); } }
        private string _titleLogName = string.Empty;
        public string TitleLogName { get => _titleLogName; private set { _titleLogName = value; OnPropertyChanged("TitleLogName"); } }

        // Shows the active log's name in the window title bar.
        public void UpdateActiveLogTitle()
        {
            string name = null;
            try
            {
                if (dal != null) name = dal.GetLogName(dal.ActiveLogId);
            }
            catch (Exception ex)
            {
                // Transient DB failure (e.g. the connection is briefly busy during startup): keep the
                // title already shown rather than silently dropping the log name to the bare title.
                // Not swallowed silently anymore, and MainWindow_Loaded re-asserts the title later.
                System.Diagnostics.Debug.WriteLine("UpdateActiveLogTitle failed: " + ex.Message);
                return;
            }
            if (string.IsNullOrEmpty(name))
            {
                // No log open is a state the operator chose (they deleted the last one), so the title
                // says it outright rather than going quiet and leaving them to wonder which log the
                // empty table belongs to.
                bool noLog = dal != null && !dal.HasActiveLog;
                this.Title = noLog ? title + "  —  no log open" : title;
                TitlePrefix = title;
                TitleLogLabel = noLog ? "  —  " : string.Empty;
                TitleLogName = noLog ? "no log open" : string.Empty;
            }
            else
            {
                this.Title = title + "  —  Log: " + name;
                TitlePrefix = title + "  —  ";
                TitleLogLabel = "Log: ";
                TitleLogName = name;
            }
        }

        // Makes the given log the active one: reloads the log table, refreshes counts/title and sets
        // Contest Mode to match the log. Used by Create New Log, the contest flow and View Logs -> Open.
        // True if the entry form holds an in-progress QSO the user hasn't added yet: a new QSO
        // (state New) with a DX callsign typed in. Editing an existing QSO (state Edit) is not
        // treated as "unsaved" here -- that QSO already exists in the log.
        private bool HasUnsavedQso()
        {
            return state == State.New && !string.IsNullOrWhiteSpace(TB_DXCallsign?.Text);
        }

        // Call before any action that would discard the in-progress QSO (switch log, exit, import,
        // edit another QSO). If one is pending, offers to add (save) it first or discard it. Both
        // choices let the action continue; there is no unsaved data left either way. actionText is
        // a short phrase like "switch logs" or "close HolyLogger" for the prompt.
        private void GuardUnsavedQso(string actionText)
        {
            Log.Warn("GuardUnsavedQso('" + actionText + "'): state=" + state +
                     ", dxCall='" + (TB_DXCallsign?.Text ?? "<null>") + "' -> unsaved=" + HasUnsavedQso());
            if (!HasUnsavedQso()) return;
            string call = (TB_DXCallsign.Text ?? string.Empty).Trim();
            bool save = HolyMessageBox.ShowConfirm(
                "You have started a QSO for \"" + call + "\" but have not added it to the log yet.\n\n" +
                "YES — add (save) this QSO first, then " + actionText + ".\n" +
                "NO — discard it and " + actionText + ".",
                "Unsaved QSO", HolyMsgType.Warning, this);
            if (save)
                AddBtn_Click(null, null);   // add the QSO to the log before the action proceeds
        }

        // Closes whatever log is open and leaves NONE open. Everything SwitchActiveLog does for a real
        // log - guarding an unsaved QSO, emptying the grid, resetting the entry form, retitling the
        // window, closing Search and Statistics - is exactly what closing needs, so it is the same road
        // with NoLogId at the end of it. A query scoped to it returns nothing, which is the truth.
        public void CloseActiveLog() => SwitchActiveLog(DataAccess.NoLogId);

        // The one gate for everything that cannot mean anything without a log: logging a QSO, importing
        // into one, exporting one, its statistics, its confirmations, its uploads. Says which action was
        // asked for, so the message answers "why can't I?" rather than only "you can't".
        //
        // Returns true when there IS a log and the caller may go ahead.
        public bool RequireActiveLog(string action)
        {
            if (dal != null && dal.HasActiveLog) return true;
            HolyMessageBox.ShowWarning(
                "No log is open, so there is nothing to " + action + ".\n\n" +
                "Open an existing log or create a new one:  Tools → Logs.",
                "No log open", this);
            return false;
        }

        public void SwitchActiveLog(long logId)
        {
            // Guard the in-progress QSO before the log changes: it is added to the CURRENT log
            // (ActiveLogId not changed yet) or discarded, per the user's choice.
            GuardUnsavedQso("switch logs");

            // Loading a large log freezes the UI thread while the grid binds/renders; show a busy
            // overlay + wait cursor so the user knows it is working, not hung.
            Mouse.OverrideCursor = Cursors.Wait;
            ShowLogLoadingOverlay(true);

            // TIMED, and written to the log. "Opening a log feels slower" is not something to settle by
            // argument: this records how long the database read took, how long everything after it took,
            // and how many QSOs were involved, so the answer comes from the operator's own machine.
            var swTotal = System.Diagnostics.Stopwatch.StartNew();
            long msRead = 0, msCounts = 0, msCluster = 0;
            try
            {
                dal.ActiveLogId = logId;
                Properties.Settings.Default.ActiveLogId = logId;
                Properties.Settings.Default.Save();

                if (Qsos != null) Qsos.CollectionChanged -= Qsos_CollectionChanged;
                var swRead = System.Diagnostics.Stopwatch.StartNew();
                Qsos = dal.GetQSOsForLog(logId);
                swRead.Stop();
                msRead = swRead.ElapsedMilliseconds;
                Qsos.CollectionChanged += Qsos_CollectionChanged;
                DataContext = Qsos;
                RestoreDataContext();
                LastQSO = Qsos.FirstOrDefault();

                ClearBtn_Click(null, null);       // reset the entry form for the newly active log

                // A DELETE OFFERED IN ANOTHER LOG IS NOT OFFERED IN THIS ONE. The bar would otherwise
                // still be standing over a table it says nothing about, and its Undo would put rows back
                // into a log the operator is no longer looking at - the restore would appear to do
                // nothing at all. The QSOs are still deleted; only the offer expires. The same goes for
                // an edit: the contact it belongs to is not in the table any more.
                ClearUndo();

                ApplyContestModeForActiveLog();
                UpdateActiveLogTitle();

                // Timed separately: this one walks every QSO in the log and resolves its country, so on
                // a big log it is a second pass over everything the read above just fetched.
                var swCounts = System.Diagnostics.Stopwatch.StartNew();
                UpdateNumOfQSOs();
                swCounts.Stop();
                msCounts = swCounts.ElapsedMilliseconds;
                UpdateEqslQueueIndicator();
                UpdateQrzMenuCount();
                RefreshCopyIndicator();           // show/hide the red "copying is live" dot for this log
                // No log open: there is no identity to check and nothing to ask about.
                if (dal.HasActiveLog)
                    EnsureActiveLogHasIdentity(promptIfEmpty: false);   // legacy log with QSOs gets its identity now
                SyncCallsignToActiveLog();        // show this log's callsign; clears any "Select Log" lock
                // Recompute worked countries from the newly active log so the cluster's "new
                // country" (red) flags reflect THIS log immediately -- e.g. a brand-new empty log
                // makes every spotted entity needed. Without this they stayed stale until restart.
                // Timed: a THIRD pass over the log (worked countries) plus a cluster refresh, which
                // itself rebuilds the set of every callsign in the log.
                var swCluster = System.Diagnostics.Stopwatch.StartNew();
                RebuildWorkedCountriesAndRefreshCluster();
                swCluster.Stop();
                msCluster = swCluster.ElapsedMilliseconds;

                // Search and Statistics capture the Qsos collection at construction (readonly field)
                // and compute everything from it. We just REPLACED Qsos with the new log's collection,
                // so an open instance would silently keep showing the previous log's data. Close them;
                // reopening binds them to the new active log. (Both null their field on Closed.)
                try { searchWindow?.Close(); } catch (System.Exception swallowed) { Log.Swallow(swallowed); }
                try { statisticsWindow?.Close(); } catch (System.Exception swallowed) { Log.Swallow(swallowed); }
            }
            finally
            {
                swTotal.Stop();
                long msBeforePaint = swTotal.ElapsedMilliseconds;

                // Clear the busy indicator only after the grid has finished its layout/render pass.
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    // Now the grid has laid out and painted, so this is the whole wait as the operator
                    // experiences it - not just the part that happens in this method.
                    swTotal.Stop();
                    Log.Warn("Open log " + logId + ": " + (Qsos == null ? 0 : Qsos.Count).ToString("N0")
                             + " QSOs | database read " + msRead + " ms | counts " + msCounts
                             + " ms | worked-countries+cluster " + msCluster
                             + " ms | ready in " + msBeforePaint
                             + " ms | painted after " + swTotal.ElapsedMilliseconds + " ms");

                    ShowLogLoadingOverlay(false);
                    Mouse.OverrideCursor = null;

                    // Switching logs changes the station callsign to the new log's identity (set above
                    // by SyncCallsignToActiveLog) — the same situation as startup, so run the same
                    // services check. It couldn't run on its own: SyncCallsignToActiveLog sets the box
                    // programmatically, which never fires LostFocus, so the manual-edit path missed it.
                    // Startup semantics (isStartup: true) keep routine switches quiet, only interrupting
                    // when the new log's callsign has a real gap in an in-use eQSL/LoTW/Club Log service.
                    // Deferred to here so the grid has painted and the busy cursor is gone first.
                    ShowStationCallsignServicesAlert(TB_MyCallsign.Text?.Trim(), isStartup: true);
                }), System.Windows.Threading.DispatcherPriority.ContextIdle);
            }
        }

        // Toggle the "Loading log…" overlay. When showing, force a render pass so it actually
        // paints before the heavy, UI-thread-blocking load begins.
        private void ShowLogLoadingOverlay(bool show)
        {
            if (LogLoadingOverlay == null) return;
            LogLoadingOverlay.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (show)
            {
                LogLoadingOverlay.UpdateLayout();
                Dispatcher.Invoke(new Action(() => { }), System.Windows.Threading.DispatcherPriority.Render);
            }
        }

        // "Create Regular Log" button in ViewLogsWindow: name it (duplicates rejected), confirm, then
        // create an empty log and make it active. No QSOs are deleted — the previous log's QSOs stay
        // in the database. Returns true if a log was created and activated; false if cancelled.
        // The station callsign / operator currently entered on the main form — used to pre-fill a new
        // log's identity for the copy-to-log feature.
        public string CurrentStationCallsign => (TB_MyCallsign.Text ?? string.Empty).Trim();
        public string CurrentOperator => (TB_Operator.Text ?? string.Empty).Trim();

        public bool CreateNewRegularLog(Window owner)
        {
            var dlg = new NewLogWindow(dal, "Enter a name for the new log:", string.Empty, 0,
                                       showCopyOptions: true, defaultCallsign: CurrentStationCallsign,
                                       defaultOperator: CurrentOperator) { Owner = owner };
            if (dlg.ShowDialog() != true) return false;

            if (!HolyMessageBox.ShowConfirm(
                    "A new empty log \"" + dlg.LogName + "\" will be created and shown.\n\n" +
                    "The current log table will be cleared from view, but every QSO stays safely in the " +
                    "HolyLogger database under its log — nothing is deleted.\n\nCreate the new log now?",
                    "Create New Log", HolyMsgType.Info, owner))
                return false;

            long id = dal.CreateLog(dlg.LogName, string.Empty, dlg.LogCallsign, dlg.LogOperator, dlg.CopyTargetLogId);   // normal (day-by-day) log
            SwitchActiveLog(id);
            return true;
        }

        // File -> View Logs: open the log manager (list all logs; open / rename / delete / export).
        private void ViewLogsMenuItem_Click(object sender, RoutedEventArgs e)
        {
            OpenLogManager();
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
                // Filter active. A row from the ACTIVE log's copy-target (worked-before reference) is
                // light blue; a match from the active log itself is light green. Both theme-aware.
                bool foreign = _foreignFilterRows != null && (e.Row.Item is QSO qr) && _foreignFilterRows.Contains(qr);
                if (foreign)
                    e.Row.Background = isAlternate
                        ? ThemeManager.Brush("WorkedElsewhereAltBg")
                        : ThemeManager.Brush("WorkedElsewhereBg");
                else
                    e.Row.Background = isAlternate
                        ? ThemeManager.Brush("FilterRowAltBg")
                        : ThemeManager.Brush("FilterRowBg");
            }
            else
            {
                // Normal state (or pinned last-QSO row): themed row alternation.
                e.Row.Background = isAlternate
                    ? ThemeManager.Brush("GridAltRowBg")
                    : ThemeManager.Brush("GridRowBg");
            }
        }

        // Reference rows shown from the copy-target log (blue) are for information only — block editing.
        private void QSODataGrid_BeginningEdit(object sender, DataGridBeginningEditEventArgs e)
        {
            if (_foreignFilterRows != null && e.Row?.Item is QSO qr && _foreignFilterRows.Contains(qr))
                e.Cancel = true;
        }

        // The Paper QSL checkbox was ticked/unticked in the log grid. The two-way binding has already
        // updated the QSO; persist it to the database and tell the Statistics window (if open) so its
        // Paper QSL folder recomputes its confirmed countries live - no recalculate button needed.
        // Attached at the DataGrid level (CheckBox.Checked/Unchecked="PaperQsl_Changed" on QSODataGrid, in
        // the XAML), so this fires for the shared PaperQslTemplate's checkbox via routed-event bubbling -
        // that template has no handler of its own, since it is shared with the Log Workshop's grid too.
        // Bubbling means `sender` is the DataGrid the handler is registered on, NOT the checkbox that was
        // actually clicked - e.OriginalSource is the one that raised it.
        private void PaperQsl_Changed(object sender, RoutedEventArgs e)
        {
            if (!((e.OriginalSource as System.Windows.Controls.CheckBox)?.DataContext is QSO qso)) return;
            try
            {
                dal?.SetPaperQslConfirmed(qso.id, qso.PaperQslConfirmed);
                if (statisticsWindow != null && statisticsWindow.IsLoaded)
                    statisticsWindow.NotifyPaperQslChanged(qso.id, qso.PaperQslConfirmed);
            }
            catch (Exception ex) { Log.Swallow(ex); }
        }

        private void UpdateConfirmationStripPosition()
        {
            ConfirmationStripHelper.UpdatePosition(QSODataGrid, ConfirmationStripLabel, ref _confirmationStripLastRect,
                "LotwStatusRank", "PaperQslStatusRank");
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

        // Tunnels from the window root for EVERY key, regardless of which field has focus — so Esc
        // can clear a selected log row even though keyboard focus normally stays on the entry fields.
        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape) return;
            if (QSODataGrid == null) return;

            // Leave an in-progress cell edit alone: there the source is the edit TextBox inside a
            // DataGridCell, and Esc should cancel that edit (handled by the grid further down the tunnel).
            bool editingGridCell = (e.OriginalSource is System.Windows.Controls.Primitives.TextBoxBase)
                                   && FindVisualParent<DataGridCell>(e.OriginalSource as DependencyObject) != null;
            if (editingGridCell) return;

            // If a log row (or cell) is highlighted, Esc clears the selection (row returns to its
            // normal color) and is consumed so it doesn't also fire the global Esc = Clear-the-entry-
            // form. When nothing is selected, Esc falls through unchanged and still clears the form.
            bool hasSelection = QSODataGrid.SelectedItem != null
                                || QSODataGrid.SelectedItems.Count > 0
                                || QSODataGrid.SelectedCells.Count > 0;
            if (hasSelection)
            {
                QSODataGrid.UnselectAll();
                QSODataGrid.UnselectAllCells();
                QSODataGrid.SelectedItem = null;
                e.Handled = true;
            }
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
                FontSize = 16,
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
                default: Log.Warn("SetCwMessageText: unsupported message number " + messageNumber); return;
            }

            try { Properties.Settings.Default.Save(); } catch (System.Exception swallowed) { Log.Swallow(swallowed); }
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
                try { monitor.Close(); } catch (System.Exception swallowed) { Log.Swallow(swallowed); }
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

            // A SELECTION IS NOT THROWN AWAY BY THE CLICK THAT ASKS WHAT TO DO WITH IT. Right-clicking
            // used to select the row under the mouse unconditionally, so Ctrl-clicking a dozen rows and
            // then right-clicking them left one row selected and a menu about that one - the whole point
            // of the selection gone at the moment it was about to be used. Now the selection survives if
            // the clicked row belongs to it, and the menu is about the group.
            var picked = SelectedQsosInLog();
            bool clickedInsideSelection = picked.Count > 1 && picked.Contains(row.Item as QSO);

            if (!clickedInsideSelection)
            {
                row.IsSelected = true;
                QSODataGrid.SelectedItem = row.Item;
            }

            QSO qso = row.Item as QSO;
            if (qso == null)
            {
                e.Handled = true;
                return;
            }

            if (clickedInsideSelection)
            {
                ContextMenu groupMenu = BuildLogSelectionMenu(picked);
                groupMenu.PlacementTarget = QSODataGrid;
                groupMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
                groupMenu.IsOpen = true;
                e.Handled = true;
                return;
            }

            // Build a fresh menu bound to the right-clicked QSO. By default a right-click menu opens
            // at the mouse, on top of the table. ContextMenu ignores Custom placement on auto-open,
            // but it DOES honor the named placement modes: anchoring it to the grid with Placement
            // = Top puts the menu directly ABOVE the grid, its bottom edge at the grid's top (just
            // above the header row), so it never covers any QSO data.
            var menu = BuildQsoRowContextMenu(qso);

            // The two copy items are added HERE rather than inside BuildQsoRowContextMenu, because
            // only this handler knows which CELL the mouse was over - by the time the item is clicked
            // the pointer has moved onto the menu.
            AddCopyItems(menu, QSODataGrid, e.OriginalSource);

            menu.PlacementTarget = QSODataGrid;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
            // Don't leave the row highlighted blue once the menu goes away (e.g. dismissed with
            // Esc or by clicking elsewhere). Clear the selection when the menu closes. The menu-item
            // actions captured the QSO directly, so they don't rely on the selection.
            menu.Closed += (s2, e2) =>
            {
                QSODataGrid.SelectedItem = null;
                QSODataGrid.UnselectAll();
            };
            QSODataGrid.ContextMenu = menu;
        }

        // The report the notice in the status bar is about, so clicking it opens that file.
        private string _lastReportPath;

        // A report was written, somewhere in the program. Raised on whatever thread wrote it - an
        // upload runs on a worker - so the hop to the UI happens here.
        private void OnReportWritten(string path)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action<string>(OnReportWritten), path);
                return;
            }

            // NOT IN THE STATUS BAR. That strip already carries the QSO count, the Holyland squares,
            // the DXCC total, the rig, the clock and the frequency mode - it is a dashboard, not a
            // place for passing announcements, and one more thing appearing and vanishing in it makes
            // the rest harder to read. Remembered here for File > Open Reports Folder and for whatever
            // is chosen to show it.
            _lastReportPath = path;
        }

        private void ReportsFolderMenuItem_Click(object sender, RoutedEventArgs e)
        {
            Reports.OpenFolder();
        }

        // A separator and the two copy items, in the menu's own style so they do not look bolted on.
        // Shared by the log table and the Log Workshop; see GridCopy for why copying is two commands.
        private static void AddCopyItems(ContextMenu menu, DataGrid grid, object rightClickedOn)
        {
            if (menu == null || grid == null) return;
            try
            {
                var cell = GridCopy.CellFrom(rightClickedOn);
                string cellText = GridCopy.TextOf(cell);

                // The FIRST REAL MenuItem, not Items[0]: both menus open with a title block naming the
                // QSO, so index 0 is not a MenuItem at all and the style came back null - which is why
                // the two copy lines were drawn in the default font while everything above them was
                // not. The separator is matched the same way.
                Style itemStyle = null, sepStyle = null;
                foreach (object o in menu.Items)
                {
                    if (itemStyle == null && o is MenuItem) itemStyle = ((MenuItem)o).Style;
                    if (sepStyle == null && o is Separator) sepStyle = ((Separator)o).Style;
                    if (itemStyle != null && sepStyle != null) break;
                }

                menu.Items.Add(sepStyle == null ? new Separator() : new Separator { Style = sepStyle });
                menu.Items.Add(GridCopy.CopyCellItem(cellText, itemStyle));
                menu.Items.Add(GridCopy.CopyRowsItem(grid, itemStyle));
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
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
    <Setter Property='Padding' Value='12,3'/>
    <Setter Property='Margin' Value='2,0'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='Template' Value='{StaticResource CtxItemTemplate}'/>
  </Style>

  <Style x:Key='CtxItemDanger' TargetType='MenuItem' BasedOn='{StaticResource CtxItem}'>
    <Setter Property='Foreground' Value='#C62828'/>
    <Setter Property='Template' Value='{StaticResource CtxItemDangerTemplate}'/>
  </Style>

  <Style x:Key='CtxSep' TargetType='Separator'>
    <Setter Property='Margin' Value='8,3'/>
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
                FontSize = 16,
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

            // Whose QSO this menu is about, spelled out at the top - the same caption the Log Workshop's
            // row menu carries, built by the same helper so the two cannot drift apart. Delete and Edit
            // act without a second look at the table, and the row underneath is half-covered by the menu.
            menu.Items.Add(RowMenuParts.MakeMenuTitle(qso.DXCall, RowMenuParts.QsoSubtitle(qso)));
            menu.Items.Add(new Separator { Style = sepStyle });

            var spotItem = new MenuItem { Header = "Spot", Style = itemStyle, Icon = MakeMenuGlyph("", blue) };
            spotItem.Click += (s, e) =>
            {
                // A SPOT IS ABOUT NOW. Telling the world a station is on a frequency is only true while
                // it still is; an hour later it is noise on everybody's screen, and the operator who
                // sent it looks careless. So an old contact cannot be spotted from the log at all.
                if (!IsFreshEnoughToSpot(qso))
                {
                    double? age = MinutesSinceQso(qso);
                    string howOld = age.HasValue
                        ? (age.Value < 120
                            ? Math.Round(age.Value) + " minutes ago"
                            : (age.Value < 2880 ? Math.Round(age.Value / 60) + " hours ago"
                                                : Math.Round(age.Value / 1440) + " days ago"))
                        : "some time ago";

                    HolyMessageBox.ShowError(
                        (qso.DXCall ?? "This station") + " was worked " + howOld + ".\n\n" +
                        "A spot says a station is on frequency NOW, so only a contact from the last " +
                        SpotFreshnessMinutes + " minutes can be spotted from the log.\n\n" +
                        "If you can hear it now, use Spot (F3) and give the frequency you hear it on.",
                        "Too old to spot", this);
                    return;
                }

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
                try { Clipboard.SetText(BuildQsoClipboardText(qso)); } catch (System.Exception swallowed) { Log.Swallow(swallowed); }
            };
            menu.Items.Add(copyItem);

            menu.Items.Add(new Separator { Style = sepStyle });

            var editItem = new MenuItem { Header = "Edit", Style = itemStyle, Icon = MakeMenuGlyph("", blue) };
            editItem.Click += (s, e) => EditQsoFromContextMenu(qso);
            menu.Items.Add(editItem);

            var deleteItem = new MenuItem { Header = "Delete", Style = dangerStyle, Icon = MakeMenuGlyph("", red) };
            // Defer the delete until the context menu has fully closed. Showing the modal confirm
            // dialog synchronously here — while the menu is still dismissing and holding mouse
            // capture — left the dialog unable to receive the click, so the delete appeared to do
            // nothing. Running it at Background priority lets the menu close first.
            deleteItem.Click += (s, e) =>
                Dispatcher.BeginInvoke(new Action(() => DeleteQsoFromContextMenu(qso)),
                                       System.Windows.Threading.DispatcherPriority.Background);
            menu.Items.Add(deleteItem);

            // A visible way out, as in the Workshop's menus. Centred under the items, which lines it up
            // with the centred callsign at the top and closes the card off symmetrically.
            var close = RowMenuParts.MakeCloseButton(menu);
            close.HorizontalAlignment = HorizontalAlignment.Center;
            close.Margin = new Thickness(8, 8, 8, 4);
            menu.Items.Add(new Separator { Style = sepStyle });
            menu.Items.Add(close);

            return menu;
        }

        // ── SEVERAL ROWS AT ONCE ───────────────────────────────────────────────────────────────────
        //
        // Ctrl-clicking rows highlighted them and nothing more: the table promised a group and the
        // program had nothing to do with one. These three are what a group is FOR - the same three the
        // Log Workshop has offered for months, in the same words, so the two tables answer alike.
        // "Clear selection" is not among them: Esc already does it in this window.

        private List<QSO> SelectedQsosInLog()
        {
            var picked = new List<QSO>();
            if (QSODataGrid == null) return picked;
            foreach (object item in QSODataGrid.SelectedItems)
            {
                var q = item as QSO;
                if (q == null || picked.Contains(q)) continue;

                // ROWS BORROWED FROM ANOTHER LOG ARE NOT THIS LOG'S TO ACT ON. While a callsign filter is
                // up, the table also shows contacts from the active log's copy-target, painted blue, for
                // reference - the program already refuses to let them be EDITED (see
                // QSODataGrid_BeginningEdit). Deleting one from here would have reached into a log the
                // operator is not even looking at, so they are left out of the group entirely.
                if (_foreignFilterRows != null && _foreignFilterRows.Contains(q)) continue;

                picked.Add(q);
            }
            return picked;
        }

        private ContextMenu BuildLogSelectionMenu(List<QSO> picked)
        {
            // THE SAME DICTIONARY THE SINGLE-ROW MENU USES. It is a private one built in this window,
            // not Application.Current.Resources - reading the app's resources found none of these keys,
            // every style came back null, and the group menu appeared in bare WPF grey beside a
            // carefully dressed one-row menu.
            var res = QsoCtxMenuResources;
            var itemStyle = (Style)res["CtxItem"];
            var dangerStyle = (Style)res["CtxItemDanger"];
            var sepStyle = (Style)res["CtxSep"];
            var blue = (Brush)new SolidColorBrush(Color.FromRgb(0x15, 0x65, 0xC0));
            var red = (Brush)new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28));

            var menu = new ContextMenu { Style = (Style)res["CtxMenu"] };
            int n = picked.Count;

            // The callsigns, not just the count: "delete 12 QSOs" is easy to agree to without checking,
            // and the ticks may have been made minutes ago.
            string names = string.Join(", ", picked.Take(6).Select(q => q.DXCall));
            if (n > 6) names += string.Format(", … (+{0:N0} more)", n - 6);
            menu.Items.Add(RowMenuParts.MakeMenuTitle(string.Format("{0:N0} QSOs selected", n), names));
            menu.Items.Add(new Separator { Style = sepStyle });

            var export = new MenuItem { Header = string.Format("Export these {0:N0} to ADIF…", n), Style = itemStyle, Icon = MakeMenuGlyph("", blue) };
            // Deferred like the delete: a modal dialog opened while the menu is still dismissing (and
            // holding mouse capture) cannot take the click.
            export.Click += (s, e) => Dispatcher.BeginInvoke(new Action(() => ExportSelectedToAdif(picked)),
                                                             System.Windows.Threading.DispatcherPriority.Background);
            menu.Items.Add(export);

            var del = new MenuItem { Header = string.Format("Delete these {0:N0} QSOs", n), Style = dangerStyle, Icon = MakeMenuGlyph("", red) };
            del.Click += (s, e) => Dispatcher.BeginInvoke(new Action(() => DeleteSelectedQsos(picked)),
                                                          System.Windows.Threading.DispatcherPriority.Background);
            menu.Items.Add(del);

            menu.Items.Add(new Separator { Style = sepStyle });

            var header = new MenuItem { Header = string.Format("Send these {0:N0} to upload queue for:", n), Style = itemStyle, IsEnabled = false, Icon = MakeMenuGlyph("", blue) };
            menu.Items.Add(header);

            var s0 = Properties.Settings.Default;
            var cbLotw = RowMenuParts.MakeServiceCheck("LoTW", s0.UseLotwService);
            var cbQrz = RowMenuParts.MakeServiceCheck("QRZ", s0.UseQrzLogbook);
            var cbEqsl = RowMenuParts.MakeServiceCheck("eQSL", s0.UseEqslService);
            var cbClub = RowMenuParts.MakeServiceCheck("Club Log", s0.UseClublogService);
            menu.Items.Add(RowMenuParts.MakeServiceGrid(cbLotw, cbQrz, cbEqsl, cbClub));

            var ok = new Button { Content = "OK", Width = 80, FontSize = 16, Padding = new Thickness(10, 4, 10, 4), Cursor = Cursors.Hand };
            ok.Click += (s, e) =>
            {
                QueueSelectedForUpload(picked, cbLotw.IsChecked == true, cbQrz.IsChecked == true,
                                       cbEqsl.IsChecked == true, cbClub.IsChecked == true);
                menu.IsOpen = false;
            };

            // Centred under the items, exactly as the one-row menu closes itself off.
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(8, 8, 8, 4) };
            buttons.Children.Add(ok);
            var close = RowMenuParts.MakeCloseButton(menu);
            close.Margin = new Thickness(10, 0, 0, 0);
            buttons.Children.Add(close);
            menu.Items.Add(new Separator { Style = sepStyle });
            menu.Items.Add(buttons);

            return menu;
        }

        // Puts them into the chosen services' queues (status 0 = pending). Always allowed, even for a QSO
        // already sent: an edited contact must be able to go again, and the uploader reads its CURRENT
        // fields when it goes.
        private void QueueSelectedForUpload(List<QSO> picked, bool lotw, bool qrz, bool eqsl, bool club)
        {
            if (picked == null || picked.Count == 0) return;
            if (!lotw && !qrz && !eqsl && !club) return;

            var dal = DataAccess.GetInstance();
            if (dal == null) return;

            var services = new List<string>();
            if (lotw) services.Add("LoTW");
            if (qrz) services.Add("QRZ");
            if (eqsl) services.Add("eQSL");
            if (club) services.Add("Club Log");

            int done = 0;
            foreach (QSO q in picked)
            {
                try
                {
                    if (lotw) dal.SetLotwStatus(q.id, 0);
                    if (qrz) dal.SetQrzStatus(q.id, 0);
                    if (eqsl) dal.SetEqslStatus(q.id, 0);
                    if (club) dal.SetClublogStatus(q.id, 0);
                    done++;
                }
                catch (Exception ex) { Log.Swallow(ex); }
            }

            try { UpdateEqslQueueIndicator(); } catch (Exception ex) { Log.Swallow(ex); }
            try { UpdateLotwMenuCount(); } catch (Exception ex) { Log.Swallow(ex); }

            HolyMessageBox.ShowSuccess(
                string.Format("{0:N0} QSO{1} queued for {2}.", done, done == 1 ? "" : "s", string.Join(", ", services)),
                "Upload queue", this);
        }

        // The same generator the File menu's export uses, so a selection comes out with exactly the
        // fields and formats a whole log would.
        private void ExportSelectedToAdif(List<QSO> picked)
        {
            if (picked == null || picked.Count == 0) return;
            try
            {
                // The carried ADIF fields are not loaded with the log - they are most of its weight and
                // no screen shows them - so they are fetched here for the QSOs being written out.
                try { DataAccess.GetInstance()?.FillCarriedAdif(picked); }
                catch (Exception ex) { Log.Swallow(ex); }

                string adif = HolyParser.Services.GenerateAdif(picked, Contests.ContestService.Active?.CabrilloName,
                                                               includeImportedFields: true);
                var save = new Microsoft.Win32.SaveFileDialog
                {
                    Filter = "ADIF File|*.adi",
                    DefaultExt = "adi",
                    Title = string.Format("Export {0:N0} selected QSOs", picked.Count),
                    FileName = string.Format("selection_{0:yyyyMMdd_HHmm}.adi", DateTime.Now)
                };
                if (save.ShowDialog() != true) return;

                System.IO.File.WriteAllText(save.FileName, adif);
                HolyMessageBox.ShowSuccess(
                    string.Format("{0:N0} QSO{1} exported.", picked.Count, picked.Count == 1 ? "" : "s"),
                    "Export ADIF", this);
            }
            catch (Exception ex)
            {
                Log.Warn("Exporting the selection failed: " + ex.GetType().Name + ": " + ex.Message);
                HolyMessageBox.ShowError("Export failed: " + ex.Message, "Export ADIF", this);
            }
        }

        // What the last group delete removed, kept whole so it can be put back into the logs it came
        // from. One step only: this is the way out of the delete just made, not a history.
        private List<QSO> _deletedForUndo;
        private List<long> _deletedForUndoLogIds;

        private async void DeleteSelectedQsos(List<QSO> picked)
        {
            if (picked == null || picked.Count == 0) return;

            string preview = string.Join(", ", picked.Take(10).Select(q => q.DXCall));
            if (picked.Count > 10) preview += string.Format(", … (+{0:N0} more)", picked.Count - 10);

            if (!HolyMessageBox.ShowConfirm(
                    string.Format("Delete these {0:N0} QSOs?\n\n{1}\n\nYou can put them straight back with the Undo button.", picked.Count, preview),
                    "Delete QSOs", HolyMsgType.Warning, this))
                return;

            // EACH DELETE IS A WRITE TO THE DATABASE, and a dozen of them is a visible pause - done on
            // the UI thread with nothing shown, the window simply stops answering and the operator cannot
            // tell a working program from a stuck one. Off the thread, behind the same progress panel the
            // imports and uploads use, counting as it goes.
            var dal = DataAccess.GetInstance();
            var deleted = new List<QSO>(picked.Count);
            var logIds = new List<long>(picked.Count);
            var toDelete = new List<QSO>(picked);
            int total = toDelete.Count;
            string failure = null;

            UploadProgressTitle = "Deleting";
            UploadProgress = string.Format("Deleting 0 of {0:N0}…", total);
            ToggleUploadProgress(Visibility.Visible);

            var progress = new Progress<int>(done =>
                UploadProgress = string.Format("Deleting {0:N0} of {1:N0}…", done, total));

            try
            {
                await Task.Run(() =>
                {
                    var report = (IProgress<int>)progress;
                    var lastPost = System.Diagnostics.Stopwatch.StartNew();

                    for (int i = 0; i < toDelete.Count; i++)
                    {
                        QSO q = toDelete[i];
                        try
                        {
                            long logId = dal != null ? dal.GetQsoLogId(q.id) : -1;
                            if (dal != null) dal.Delete(q.id);
                            deleted.Add(q);
                            logIds.Add(logId);
                        }
                        catch (Exception ex)
                        {
                            failure = ex.Message;
                            Log.Warn("Deleting " + (q.DXCall ?? "?") + " failed: " + ex.GetType().Name + ": " + ex.Message);
                            break;
                        }

                        // Throttled, and always on the last one: a report per row floods the dispatcher
                        // at Normal priority, which outranks rendering - and the panel then never paints.
                        if (i == toDelete.Count - 1 || lastPost.ElapsedMilliseconds >= 150)
                        {
                            lastPost.Restart();
                            report.Report(i + 1);
                        }
                    }
                });
            }
            finally
            {
                ToggleUploadProgress(Visibility.Hidden);
                UploadProgressTitle = "";
                UploadProgress = "";
            }

            // THE ROWS COME OUT WITH THE HANDLER DETACHED, and this is why the delete was slow. Removing
            // from Qsos raises Qsos_CollectionChanged, which does the database delete ITSELF and then
            // rebuilds the worked-country cache, the cluster colours and four menu counts - per row. So
            // twelve rows meant twelve deletes of rows already deleted above, and twelve full rebuilds.
            // Detached, removed, re-attached, and everything refreshed ONCE at the end.
            //
            // The filtered view has to be told as well: with a callsign filter on, the grid is bound to
            // FilteredQsos, and rows removed only from Qsos stayed on screen after being deleted.
            try
            {
                if (Qsos != null) Qsos.CollectionChanged -= Qsos_CollectionChanged;
                foreach (QSO q in deleted)
                {
                    if (FilteredQsos != null) FilteredQsos.Remove(q);
                    if (Qsos != null) Qsos.Remove(q);
                }
            }
            finally
            {
                if (Qsos != null) Qsos.CollectionChanged += Qsos_CollectionChanged;
            }

            // Nothing may stay selected that no longer exists.
            try { QSODataGrid.UnselectAll(); QSODataGrid.UnselectAllCells(); }
            catch (Exception ex) { Log.Swallow(ex); }

            if (failure != null)
                HolyMessageBox.ShowError("Could not delete them all: " + failure, "Delete QSOs", this);

            if (deleted.Count > 0)
                PushUndo(new UndoStep
                {
                    Deleted = deleted,
                    DeletedLogIds = logIds,
                    Label = string.Format("Deleted {0:N0} QSO{1}", deleted.Count, deleted.Count == 1 ? "" : "s"),
                });

            // Once, now, instead of once per row - the same work Qsos_CollectionChanged would have done.
            LastQSO = Qsos?.FirstOrDefault();
            UpdateNumOfQSOs();
            UpdateLotwMenuCount();
            UpdateQrzMenuCount();
            UpdateClublogMenuCount();
            UpdateEqslQueueIndicator();
            RebuildWorkedCountriesAndRefreshCluster();
        }

        // ── ONE UNDO STACK FOR THE MAIN WINDOW ───────────────────────────────────────────────────
        //
        // Edits and deletions go on the SAME stack, so Undo always takes back the last thing done -
        // whichever it was. Two separate one-shot slots would have to decide between themselves which
        // to offer, and would get it wrong the moment an operator deleted a row and then edited
        // another.
        //
        // A stack, not a single step, because the Log Workshop has had one all along and the two
        // windows edit the same log: "do like we do in the workshop. keeps a stack".
        //
        // Deep in the stack the promise weakens by itself - an older edit undone after a newer one to
        // the same contact would put back the older values - so the bar names only the top step and
        // says how many are behind it.
        private class UndoStep
        {
            public string Label;

            // An edit: the live QSO object the grid is showing, and a copy of it as it was.
            public QSO EditTarget;
            public QSO EditBefore;

            // A deletion: the contacts, and the log each came out of.
            public List<QSO> Deleted;
            public List<long> DeletedLogIds;

            public bool IsEdit { get { return EditTarget != null && EditBefore != null; } }
        }

        private readonly Stack<UndoStep> _mainUndo = new Stack<UndoStep>();

        // How deep it is allowed to go. Every step holds a copy of one QSO (or a list of deleted
        // ones), so this is small - but it is not nothing, and an operator editing all afternoon
        // should not be paying for a step he took four hours ago.
        private const int MaxUndoSteps = 50;

        private QSO _editUndoBefore;
        private QSO _editUndoTarget;

        private void PushUndo(UndoStep step)
        {
            if (step == null) return;
            _mainUndo.Push(step);

            // Stack<T> has no way to drop from the bottom, so it is rebuilt on the rare occasion it
            // grows past the limit - newest kept, oldest let go.
            if (_mainUndo.Count > MaxUndoSteps)
            {
                var keep = _mainUndo.ToArray();                    // top first
                _mainUndo.Clear();
                for (int i = MaxUndoSteps - 1; i >= 0; i--) _mainUndo.Push(keep[i]);
            }

            ShowUndoBar();
        }

        // ONE CONTROL, NOT TWO. There was a floating red bar as well, and after an edit both appeared
        // at once saying the same thing - the bar over the log table and the button on the form. The
        // bar is gone; this is the whole of it. What it would undo is in the tooltip, which is where a
        // detail belongs when the button itself has to be small.
        private void ShowUndoBar()
        {
            if (Btn_UndoMain == null) return;

            if (_mainUndo.Count == 0)
            {
                Btn_UndoMain.Visibility = Visibility.Collapsed;
                return;
            }

            UndoStep top = _mainUndo.Peek();
            Btn_UndoMain.ToolTip = "Undo: " + top.Label
                + (_mainUndo.Count > 1 ? string.Format("   ({0} more can be undone)", _mainUndo.Count - 1) : "");
            Btn_UndoMain.Visibility = Visibility.Visible;
        }

        private void ClearUndo()
        {
            _mainUndo.Clear();
            _editUndoBefore = null;
            _editUndoTarget = null;
            _deletedForUndo = null;
            _deletedForUndoLogIds = null;
            if (Btn_UndoMain != null) Btn_UndoMain.Visibility = Visibility.Collapsed;
        }

        // Every readable-and-writable property, copied. Written by reflection rather than by hand
        // because a QSO has some seventy fields and a list typed out here would be missing one within
        // a release - and the field it was missing is the field somebody would want back.
        private static QSO CopyOfQso(QSO source)
        {
            if (source == null) return null;
            var copy = new QSO();
            foreach (var p in typeof(QSO).GetProperties())
            {
                if (!p.CanRead || !p.CanWrite) continue;
                try { p.SetValue(copy, p.GetValue(source)); }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
            }
            return copy;
        }

        private void ShowEditUndoBar()
        {
            if (_editUndoBefore == null || _editUndoTarget == null) return;

            string call = (_editUndoBefore.DXCall ?? string.Empty).Trim();
            PushUndo(new UndoStep
            {
                EditTarget = _editUndoTarget,
                EditBefore = _editUndoBefore,
                Label = "Edited " + (call.Length > 0 ? call : "a QSO"),
            });

            _editUndoBefore = null;
            _editUndoTarget = null;
        }

        private void UndoTheEdit(QSO live, QSO before)
        {
            if (before == null || live == null) return;

            try
            {
                // Back onto the SAME object the grid is showing, so the row changes under the operator
                // rather than after a reload - and the id is kept, because it is the row's identity and
                // the copy's id is only a copy of it.
                int id = live.id;
                foreach (var p in typeof(QSO).GetProperties())
                {
                    if (!p.CanRead || !p.CanWrite) continue;
                    try { p.SetValue(live, p.GetValue(before)); }
                    catch (Exception swallowed) { Log.Swallow(swallowed); }
                }
                live.id = id;

                DataAccess.GetInstance()?.Update(live);
                QSODataGrid.Items.Refresh();
                RebuildWorkedCountriesAndRefreshCluster();
                UpdateNumOfQSOs();

                HolyMessageBox.ShowSuccess(
                    "The edit was undone — " + ((before.DXCall ?? "").Trim().Length > 0 ? before.DXCall.Trim() : "the QSO")
                    + " is back as it was.", "Undo", this);
            }
            catch (Exception ex)
            {
                Log.Warn("Undoing an edit failed: " + ex.Message);
                HolyMessageBox.ShowError("The edit could not be undone.\n\n" + ex.Message, "Undo", this);
            }
        }

        private void Btn_DeleteUndo_Click(object sender, RoutedEventArgs e)
        {
            // ONE STEP OFF THE TOP. The button goes on offering the next one down, so several undos in
            // a row walk back through what was done - the same way the Log Workshop's does.
            if (_mainUndo.Count == 0) { ShowUndoBar(); return; }

            UndoStep step = _mainUndo.Pop();
            ShowUndoBar();

            if (step.IsEdit) { UndoTheEdit(step.EditTarget, step.EditBefore); return; }
            if (step.Deleted == null || step.Deleted.Count == 0) return;

            var dal = DataAccess.GetInstance();
            int restored = 0;
            for (int i = 0; i < step.Deleted.Count; i++)
            {
                try
                {
                    QSO q = step.Deleted[i];
                    int newId = dal != null ? dal.RestoreQso(q, step.DeletedLogIds[i]) : 0;
                    if (newId > 0) q.id = newId;
                    restored++;
                }
                catch (Exception ex) { Log.Swallow(ex); }
            }

            // RELOADED, not added back one by one. A plain Add leaves them at the end, unsorted and
            // probably off-screen, which reads as "nothing came back". The same three steps every other
            // reload path here takes: Qsos is a plain field and the grid binds to the DataContext, so
            // assigning Qsos alone changes nothing on screen.
            try
            {
                if (dal != null)
                {
                    Qsos = dal.GetQSOsForLog(dal.ActiveLogId);
                    Qsos.CollectionChanged += Qsos_CollectionChanged;
                    FilteredQsos = null;
                    _foreignFilterRows = null;
                    DataContext = Qsos;
                    LastQSO = Qsos.FirstOrDefault();
                }
            }
            catch (Exception ex) { Log.Warn("Reloading the log after Undo failed: " + ex.Message); }

            UpdateNumOfQSOs();
            RebuildWorkedCountriesAndRefreshCluster();

            HolyMessageBox.ShowSuccess(
                string.Format("{0:N0} QSO{1} restored.", restored, restored == 1 ? "" : "s"),
                "Undo", this);
        }

        // Kept only because the XAML that used it has gone and something may still reference it; it
        // dismisses nothing now, because there is nothing to dismiss.
        private void Btn_DeleteUndoDismiss_Click(object sender, RoutedEventArgs e)
        {
        }

        private void OpenQrzPage(string callsign)
        {
            string call = (callsign ?? string.Empty).Trim().ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(call))
                return;
            try { Process.Start("https://www.qrz.com/db/" + call); } catch (System.Exception swallowed) { Log.Swallow(swallowed); }
        }

        // Only one QRZ upload runs at a time so the on-save push and the silent retry pass can never
        // double-send the same QSO.
        private readonly System.Threading.SemaphoreSlim _qrzPumpLock = new System.Threading.SemaphoreSlim(1, 1);

        // The distinct rejection reasons QRZ gave during the last manual queue upload, so a failed run
        // can tell the user WHY (duplicate, wrong callsign for the logbook, no subscription, ...) instead
        // of a generic "check your connection". Set by PumpQrzQueue, read by the menu handler.
        private readonly System.Collections.Generic.HashSet<string> _lastQrzFailReasons =
            new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        private bool _lastQrzHadNetworkError;

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
                    // Anything else (rejection or network error) leaves the QSO pending so it stays in
                    // the queue and is retried. A QSO only leaves the queue once QRZ confirms it.
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

        // Club Log warns that a 403 (bad credentials) must stop real-time uploads at once or the IP
        // may be blocked. Once we see one auth failure we suspend Club Log pushes for the rest of the
        // session; the operator fixes their credentials and restarts. Reset only on app restart.
        private bool _clublogAuthBlockedThisSession;
        private readonly System.Threading.SemaphoreSlim _clublogPumpLock = new System.Threading.SemaphoreSlim(1, 1);

        // True when Club Log real-time upload is switched on, HolyLogger's application API key is
        // present, the user's credentials are set, and we have not been auth-blocked this session.
        private bool ClublogPushEnabled
        {
            get
            {
                var s = Properties.Settings.Default;
                return !_clublogAuthBlockedThisSession
                       && ClublogService.HasApiKey
                       && s.UseClublogService
                       && s.ClublogAutoUpload
                       && !string.IsNullOrWhiteSpace(s.ClublogEmail)
                       && !string.IsNullOrWhiteSpace(s.ClublogPassword);
            }
        }

        // Pushes a single just-logged QSO to Club Log in real time (fire and forget). The station
        // callsign comes from the QSO, so one Club Log account serves all your callsigns. This is a
        // real-time-only service: a failed upload is not queued or retried (by design). Never throws.
        private async System.Threading.Tasks.Task SendOneQsoToClublog(QSO qso)
        {
            try
            {
                if (qso == null) return;
                if (!ClublogPushEnabled) return;

                if (dal == null) return;

                // Serialize with the queue pump so a just-logged QSO and a running pump don't collide.
                if (!await _clublogPumpLock.WaitAsync(TimeSpan.FromSeconds(30))) return;
                try
                {
                    var s = Properties.Settings.Default;
                    ClublogResult r = await ClublogService.UploadAsync(s.ClublogEmail, s.ClublogPassword, qso.MyCall, BuildQrzAdif(qso));

                    if (r.Ok)
                    {
                        dal.SetClublogStatus(qso.id, 1);   // uploaded
                    }
                    else if (r.NetworkError)
                    {
                        // offline / timeout -> leave pending (status stays 0) for a later retry
                    }
                    else if (r.StatusCode == 403)
                    {
                        // 403 = authentication failure. Club Log requires us to stop immediately
                        // (continuing risks an IP block), so suspend auto-upload for this session. The
                        // QSO stays pending (0) — it's a fixable credentials/key problem, not a bad record.
                        _clublogAuthBlockedThisSession = true;
                        Log.Warn("Club Log rejected the upload (403). Suspending Club Log auto-upload for this session. "
                                 + "Check the e-mail / password / API key in Options -> Club Log Service. Response: "
                                 + (r.Message ?? string.Empty));
                    }
                    else
                    {
                        // Rejected by Club Log -> keep the QSO pending (stays in the queue) instead of
                        // dropping it. Only a confirmed upload clears it.
                    }
                }
                finally
                {
                    _clublogPumpLock.Release();
                }
                UpdateClublogMenuCount();
            }
            catch
            {
                // Auto-upload must never crash the app; the QSO remains pending for a later retry.
            }
        }

        // Drains the Club Log pending queue (status 0), pushing each QSO in real-time order. Networks
        // errors stop the pass (the rest stays pending); definitive rejections are marked 2; a 403
        // suspends Club Log for the session. Mirrors PumpQrzQueue.
        private async System.Threading.Tasks.Task PumpClublogQueue(bool force = false, UploadProgressWindow progressWindow = null)
        {
            try
            {
                if (dal == null) return;
                if (!Properties.Settings.Default.UseClublogService) return;
                if (!force && !ClublogPushEnabled) return;
                if (!ClublogService.HasApiKey) return;

                var s = Properties.Settings.Default;
                if (string.IsNullOrWhiteSpace(s.ClublogEmail) || string.IsNullOrWhiteSpace(s.ClublogPassword)) return;

                var lockTimeout = force ? TimeSpan.FromSeconds(30) : TimeSpan.Zero;
                if (!await _clublogPumpLock.WaitAsync(lockTimeout)) return;
                try
                {
                    System.Collections.Generic.List<QSO> pending = dal.GetPendingClublogQsos();

                    if (progressWindow != null)
                    {
                        if (pending.Count > 0)
                            progressWindow.StartService("Club Log", pending.Count);
                        else
                            progressWindow.SkipService("Club Log", "nothing to upload — queue is empty");
                    }

                    foreach (var qso in pending)
                    {
                        ClublogResult r = await ClublogService.UploadAsync(s.ClublogEmail, s.ClublogPassword, qso.MyCall, BuildQrzAdif(qso));

                        if (r.NetworkError)
                            break;   // offline -> stop; the rest stays pending for next time

                        if (r.Ok)
                        {
                            dal.SetClublogStatus(qso.id, 1);
                            progressWindow?.ReportQso(qso.DXCall, qso.Band, qso.Mode, true);
                        }
                        else if (r.StatusCode == 403)
                        {
                            _clublogAuthBlockedThisSession = true;   // stop at once on auth failure
                            break;
                        }
                        else
                        {
                            // Rejected by Club Log -> keep the QSO pending (stays in the queue) and
                            // report it as not-sent for this run. Only a confirmed upload clears it.
                            progressWindow?.ReportQso(qso.DXCall, qso.Band, qso.Mode, false);
                        }
                    }
                }
                finally
                {
                    _clublogPumpLock.Release();
                }
                UpdateClublogMenuCount();
            }
            catch
            {
                // Best effort; anything not confirmed sent simply stays pending.
            }
        }

        // ---- Copy-to-log "copying is live" indicator (red dot in the Date column header) ----------

        private Visibility _copyDotVisibility = Visibility.Collapsed;
        public Visibility CopyDotVisibility
        {
            get => _copyDotVisibility;
            private set { _copyDotVisibility = value; OnPropertyChanged("CopyDotVisibility"); }
        }

        private string _copyTargetTooltip = string.Empty;
        public string CopyTargetTooltip
        {
            get => _copyTargetTooltip;
            private set { _copyTargetTooltip = value; OnPropertyChanged("CopyTargetTooltip"); }
        }

        // Shows/hides the red dot from the ACTIVE log's copy-target. Call when the active log changes or
        // copy settings are edited.
        public void RefreshCopyIndicator()
        {
            try
            {
                long? target = dal?.GetCopyTargetLogId(dal.ActiveLogId);
                if (target.HasValue)
                {
                    string name = dal.GetLogName(target.Value) ?? "another log";
                    CopyTargetTooltip = "Copying new QSOs → " + name + "   (click to stop)";
                    CopyDotVisibility = Visibility.Visible;
                }
                else
                {
                    CopyTargetTooltip = string.Empty;
                    CopyDotVisibility = Visibility.Collapsed;
                }
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); CopyDotVisibility = Visibility.Collapsed; }
        }

        // Clicking the dot stops the active log's copying (its identity is kept). Handled so the click
        // doesn't also sort the Date column.
        private void CopyLiveDot_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            e.Handled = true;
            try
            {
                if (dal == null) return;
                long active = dal.ActiveLogId;
                if (dal.GetCopyTargetLogId(active) == null) return;
                if (!HolyMessageBox.ShowConfirm(
                        "Stop copying new QSOs from this log into the other log?\n\nQSOs already copied are not affected.",
                        "Stop copying", HolyMsgType.Info, this))
                    return;
                dal.SetCopyTarget(active, null);   // stop copying; identity is untouched (permanent)
                RefreshCopyIndicator();
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // A log must carry a permanent identity (station callsign + operator) before you can log into it.
        // If the active log has none, prompt for it — pre-filled from the callsigns actually used in the
        // log's QSOs (imported/legacy logs), or from the main-window callsign/operator if the log is empty.
        // Returns true if the active log has an identity afterward. Called on startup and every log switch,
        // and guards the logging button.
        public bool EnsureActiveLogHasIdentity(bool promptIfEmpty = true)
        {
            try
            {
                if (dal == null) return true;
                long id = dal.ActiveLogId;
                if (dal.LogHasIdentity(id)) return true;

                var candidates = dal.GetStationIdentitiesInLog(id);
                // An empty log doesn't need an identity yet — it gets one when you import into it (from the
                // ADIF) or log the first QSO. Only nag at startup/switch for logs that already have QSOs.
                if (candidates.Count == 0 && !promptIfEmpty) return false;
                string logName = dal.GetLogName(id) ?? "this log";
                var dlg = new SetIdentityWindow(candidates, logName, CurrentStationCallsign, CurrentOperator) { Owner = this };
                if (dlg.ShowDialog() == true)
                {
                    dal.SetLogIdentity(id, dlg.Callsign, dlg.Operator);
                    return dal.LogHasIdentity(id);
                }
                return false;   // cancelled -> still no identity; caller blocks logging
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); return false; }
        }

        // Copy-to-log: after a QSO is logged into the active log, mirror it into that log's copy-target
        // (if one is configured and the QSO's station callsign + operator match the target's identity).
        // If a copy was placed, it enters the target log's upload queues, so refresh the queue counts.
        private void CopyLoggedQsoToTargetLog(QSO justLogged)
        {
            if (justLogged == null || dal == null) return;
            long copyId = dal.CopyQsoToTargetIfConfigured(justLogged, dal.ActiveLogId);
            if (copyId > 0)
            {
                try
                {
                    UpdateEqslQueueIndicator();
                    UpdateQrzMenuCount();
                    UpdateLotwMenuCount();
                    UpdateClublogMenuCount();
                }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
            }
        }

        // Refreshes the "Upload Queue to Club Log (N)" Tools-menu item: bold service name + live count.
        private void UpdateClublogMenuCount()
        {
            try
            {
                int count = dal?.GetPendingClublogCount() ?? 0;
                if (UploadQueueToClublogMenuItem == null) return;
                var header = new System.Windows.Controls.TextBlock();
                header.Inlines.Add(new System.Windows.Documents.Run("Upload Queue to "));
                header.Inlines.Add(new System.Windows.Documents.Run("Club Log") { FontWeight = System.Windows.FontWeights.Bold });
                header.Inlines.Add(new System.Windows.Documents.Run("  (" + count + ")"));
                UploadQueueToClublogMenuItem.Header = header;
            }
            catch (System.Exception swallowed) { Log.Swallow(swallowed); }
        }

        private async void UploadQueueToClublogMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (!RequireActiveLog("upload")) return;
            if (!ClublogService.HasApiKey)
            {
                HolyMessageBox.ShowError("This copy of HolyLogger has no Club Log application key.", "Club Log", this);
                return;
            }
            var s = Properties.Settings.Default;
            if (string.IsNullOrWhiteSpace(s.ClublogEmail) || string.IsNullOrWhiteSpace(s.ClublogPassword))
            {
                HolyMessageBox.ShowWarning("Set your Club Log e-mail and password first in Options → Club Log Service (and press Test).", "Club Log", this);
                return;
            }

            int before = dal?.GetPendingClublogCount() ?? 0;
            if (before == 0)
            {
                HolyMessageBox.Show("The Club Log queue is empty. Nothing to upload.", "Club Log", HolyMsgType.Info, this);
                return;
            }

            UploadQueueToClublogMenuItem.IsEnabled = false;
            this.IsEnabled = false;   // owned progress window stays live
            var progressWindow = new UploadProgressWindow { Owner = this };
            progressWindow.Show();
            try
            {
                // force: explicit "upload now" click. Progress window shows a ✓/✗ row per QSO.
                await PumpClublogQueue(force: true, progressWindow);
                progressWindow.ShowComplete();
                UpdateClublogMenuCount();
                await progressWindow.WaitForOkAsync();
            }
            finally
            {
                this.IsEnabled = true;
                UploadQueueToClublogMenuItem.IsEnabled = true;
            }
        }

        // Tools -> Upload Full Log to Club Log: bulk-uploads the whole active log as one ADIF file
        // (putlogs.php). Club Log merges and de-duplicates, so it is safe to run again and is the way
        // to seed a log made before Club Log was switched on. Distinct from the real-time per-QSO push.
        // Recompute the queue size whenever the Tools menu is opened, so the menu item's gray state
        // and count are always current.
        private void ToolsMenu_SubmenuOpened(object sender, RoutedEventArgs e)
        {
            UpdateEqslQueueIndicator();
            UpdateClublogMenuCount();
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
            catch (System.Exception swallowed) { Log.Swallow(swallowed); }
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
            Add("CQ Zone", qso.CQZone);
            Add("ITU Zone", qso.ITUZone);
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
            // Offer to save (or discard) an in-progress new QSO before loading this one for editing,
            // so it isn't silently overwritten. Both choices proceed to the edit.
            GuardUnsavedQso("edit the selected QSO");
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

        // HOW RECENT A CONTACT HAS TO BE BEFORE IT CAN BE SPOTTED. A spot is a claim about the present
        // tense - "this station is on this frequency" - so a contact from an hour ago is not evidence of
        // anything. One number, used by both ways in: the log row's Spot menu and the F3 window.
        internal const int SpotFreshnessMinutes = 5;

        // Minutes since the contact was logged, or null when the stored date/time cannot be read.
        // The log keeps them as yyyyMMdd and HHmmss in UTC, which is what the clock at the top shows.
        private static double? MinutesSinceQso(QSO qso)
        {
            if (qso == null) return null;

            string d = (qso.Date ?? string.Empty).Trim();
            string t = (qso.Time ?? string.Empty).Trim();
            if (d.Length != 8) return null;
            if (t.Length == 4) t += "00";        // some records carry only HHmm
            if (t.Length != 6) return null;

            DateTime when;
            if (!DateTime.TryParseExact(d + t, "yyyyMMddHHmmss",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal |
                    System.Globalization.DateTimeStyles.AdjustToUniversal, out when))
                return null;

            double minutes = (DateTime.UtcNow - when).TotalMinutes;
            return minutes < 0 ? 0 : minutes;    // a contact logged this second may round into the future
        }

        private static bool IsFreshEnoughToSpot(QSO qso)
        {
            double? age = MinutesSinceQso(qso);
            return age.HasValue && age.Value <= SpotFreshnessMinutes;
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

            // WHAT IS THIS WINDOW ABOUT? Three situations, and the window has to be right about which:
            //   * the last contact is stale (older than SpotFreshnessMinutes) - it is no longer evidence
            //     that anybody is on any frequency, so it is not offered at all. Whatever is in the DX
            //     box, on the radio's frequency.
            //   * the last contact is recent and the DX box is empty or holds that same station - one
            //     obvious answer, filled in with that contact's own frequency.
            //   * the last contact is recent AND the DX box holds someone ELSE - two honest answers and
            //     no way to tell which the operator means. The window asks, and sends nothing until it
            //     is told (see the two buttons below).
            QSO lastQso = LastQSO;
            bool lastIsFresh = !hasPreset && IsFreshEnoughToSpot(lastQso);
            string dxBoxCall = (TB_DXCallsign != null ? (TB_DXCallsign.Text ?? string.Empty) : string.Empty).Trim();
            bool mustChoose = lastIsFresh
                              && dxBoxCall.Length > 0
                              && !CallsignIdentity.Same(dxBoxCall, lastQso.DXCall);

            string defaultSpottedCallsign;
            string defaultFrequencyValue;
            if (hasPreset)
            {
                defaultSpottedCallsign = presetCallsign;
                defaultFrequencyValue = presetFrequency ?? string.Empty;
            }
            else if (mustChoose)
            {
                defaultSpottedCallsign = string.Empty;   // filled by whichever button is pressed
                defaultFrequencyValue = string.Empty;
            }
            else if (lastIsFresh)
            {
                // The station just worked, at the frequency it was worked on.
                defaultSpottedCallsign = lastQso.DXCall ?? string.Empty;
                defaultFrequencyValue = lastQso.Freq ?? string.Empty;
            }
            else
            {
                // Nothing recent to offer: whatever is being typed, where the radio actually is.
                defaultSpottedCallsign = dxBoxCall;
                defaultFrequencyValue = TB_Frequency.Text;
            }

            AddSpotDialogLabel(grid, "Spotted Callsign", 1, new Thickness(0, 8, 0, 0));
            TextBox spottedCallsignTextBox = AddSpotDialogTextBox(grid, defaultSpottedCallsign, 1, false, new Thickness(0, 8, 0, 0));

            AddSpotDialogLabel(grid, "Frequency MHz", 2, new Thickness(0, 8, 0, 0));
            TextBox frequencyTextBox = AddSpotDialogTextBox(grid, defaultFrequencyValue, 2, false, new Thickness(0, 8, 0, 0));

            AddSpotDialogLabel(grid, "Comment", 3, new Thickness(0, 8, 0, 0), VerticalAlignment.Top);
            TextBox commentTextBox = AddSpotDialogTextBox(grid, string.Empty, 3, false, new Thickness(0, 8, 0, 0));
            commentTextBox.MaxLength = 60;

            // WHICH STATION? Only when there are genuinely two answers. Each button says the callsign it
            // will spot rather than "Last Saved" / "New", because the operator is choosing between two
            // stations, not between two words. Until one is pressed the boxes stay empty and Send is
            // dead - there is no default here that would not sometimes be the wrong station.
            Button chooseLastButton = null;
            Button chooseDxBoxButton = null;
            if (mustChoose)
            {
                dialog.Height = 335;
                dialog.MinHeight = 335;
                dialog.MaxHeight = 335;

                var choosePanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 10, 0, 0)
                };

                chooseLastButton = new Button
                {
                    Content = (lastQso.DXCall ?? string.Empty) + "  —  just worked",
                    Height = 32,
                    Padding = new Thickness(10, 0, 10, 0),
                    FontSize = 16,
                    Margin = new Thickness(0, 0, 10, 0)
                };
                chooseDxBoxButton = new Button
                {
                    Content = dxBoxCall + "  —  in the DX box",
                    Height = 32,
                    Padding = new Thickness(10, 0, 10, 0),
                    FontSize = 16
                };

                choosePanel.Children.Add(chooseLastButton);
                choosePanel.Children.Add(chooseDxBoxButton);
                Grid.SetRow(choosePanel, 4);
                Grid.SetColumn(choosePanel, 0);
                Grid.SetColumnSpan(choosePanel, 2);
                grid.Children.Add(choosePanel);
            }

            // Centred, and green once it will actually do something - the same green as Add (F1), so a
            // live Send looks the same everywhere in the program.
            Button sendButton = new Button
            {
                Content = "Send",
                Width = 110,
                Height = 32,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 12, 0, 0),
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                IsDefault = true,
                IsEnabled = false
            };

            bool isSendingSpot = false;
            bool choiceMade = !mustChoose;      // nothing to choose = already chosen
            Action updateSendButtonState = () =>
            {
                // A HALF-TYPED CALLSIGN IS NOT A CALLSIGN. "Not empty" was the only test, so a spot for
                // "4" - the first keystroke of 4Z5SL, caught while the operator was still typing - was
                // ready to be broadcast to every cluster in the world. It has to LOOK like a callsign,
                // by the same rule the rest of the program uses; and the frequency has to be a number,
                // for the same reason.
                string spotted = (spottedCallsignTextBox.Text ?? string.Empty).Trim();
                string freqText = (frequencyTextBox.Text ?? string.Empty).Trim();

                bool callIsReal = CallsignIdentity.LooksLikeCallsign(spotted);
                double freqMhz;
                bool freqIsReal = double.TryParse(freqText,
                                      System.Globalization.NumberStyles.Float,
                                      System.Globalization.CultureInfo.InvariantCulture, out freqMhz)
                                  && freqMhz > 0;

                sendButton.IsEnabled = !isSendingSpot && choiceMade && callIsReal && freqIsReal;

                sendButton.ToolTip =
                      !choiceMade ? "Choose which station you are spotting"
                    : !callIsReal ? (spotted.Length == 0 ? "Type the callsign you are spotting"
                                                         : "\"" + spotted + "\" is not a whole callsign")
                    : !freqIsReal ? "The frequency has to be a number in MHz"
                    : null;

                if (sendButton.IsEnabled)
                {
                    sendButton.Background = new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32));
                    sendButton.Foreground = Brushes.White;
                    sendButton.BorderBrush = new SolidColorBrush(Color.FromRgb(0x1B, 0x5E, 0x20));
                }
                else
                {
                    sendButton.ClearValue(Control.BackgroundProperty);
                    sendButton.ClearValue(Control.ForegroundProperty);
                    sendButton.ClearValue(Control.BorderBrushProperty);
                }
            };

            if (mustChoose)
            {
                chooseLastButton.Click += (s, args) =>
                {
                    spottedCallsignTextBox.Text = lastQso.DXCall ?? string.Empty;
                    frequencyTextBox.Text = lastQso.Freq ?? string.Empty;   // where that contact was made
                    choiceMade = true;
                    updateSendButtonState();
                };
                chooseDxBoxButton.Click += (s, args) =>
                {
                    spottedCallsignTextBox.Text = dxBoxCall;
                    frequencyTextBox.Text = TB_Frequency.Text;              // a station heard now, here
                    choiceMade = true;
                    updateSendButtonState();
                };
            }

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
            // Centred on the WINDOW, not on the right-hand column: spanning both columns is what makes
            // "HorizontalAlignment.Center" mean the middle of the dialog. Always, in every case.
            Grid.SetRow(sendButton, 5);
            Grid.SetColumn(sendButton, 0);
            Grid.SetColumnSpan(sendButton, 2);
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

        // Aborts any message transmission in progress (SSB voice or CW) by sending the radio's stop
        // CAT command and resetting the message state. Returns true if something was actually stopped.
        // Best-effort: if the stop CAT command can't be sent we still reset state so the UI recovers.
        // Called by Esc (which otherwise clears the entry form).
        private bool StopActiveMessageTransmission()
        {
            int? current = activeVoiceMessageNumber ?? pendingVoiceMessageNumber;
            if (!current.HasValue)
                return false;

            string rigType = NormalizeRigType(Rig != null ? Rig.RigType : null);

            if (IsCwModeActive())
            {
                string stopCommand = BuildCwStopCommand(rigType);
                if (!string.IsNullOrWhiteSpace(stopCommand))
                    TrySendOmniRigCustomCommand(stopCommand);
            }
            else if (TryGetVoiceCommandProfile(out RadioVoiceCommandProfile profile, out _, out _)
                     && !string.IsNullOrWhiteSpace(profile.StopCommand))
            {
                TrySendOmniRigCustomCommand(profile.StopCommand);
            }

            ClearVoiceMessageState();
            return true;
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

        // How many digits an RST report has in the current mode: 2 on voice, 3 on CW and the data modes,
        // where the report carries a tone digit as well. The contest RST cells read it too, since what is
        // typed in those is copied straight into these boxes.
        private int _rstDigits = 2;

        // The two RST boxes, set to the report the current mode uses - 59 on voice, 599 everywhere else -
        // and, on voice only, offering 59 down to 51 on a chevron. Rebuilt on every mode change rather
        // than written out in the XAML, so the two boxes can never disagree about the current mode.
        //
        // The digit limit moves with the mode as well, so the box will not ACCEPT a third digit on SSB or
        // a fourth on CW. It only limits typing - WPF leaves text assigned in code alone (measured) - so
        // opening an old QSO for editing still shows whatever was logged, however long it is, rather than
        // quietly dropping its last digit.
        //
        // The mode is passed in from CB_Mode's SelectionChanged, where CB_Mode.Text still holds the
        // PREVIOUS mode; everywhere else the dropdown itself is the truth.
        private void ResetRstForMode(string modeOverride = null)
        {
            if (TB_RSTSent == null || TB_RSTRcvd == null) return;

            string mode = (modeOverride ?? (CB_Mode == null ? "" : CB_Mode.Text) ?? "").Trim().ToUpperInvariant();
            bool voice = mode == "SSB" || mode == "FM" || mode == "AM";

            _rstDigits = voice ? 2 : 3;
            string best = voice ? "59" : "599";

            // The list is offered on voice only. On CW and the data modes the report is 599 almost
            // without exception, so those boxes lose the chevron and go back to being plain typing boxes.
            ControlTemplate look = TryFindResource(voice ? "FlatEditableComboTemplate" : "FlatTypeOnlyComboTemplate") as ControlTemplate;

            foreach (ComboBox box in new[] { TB_RSTSent, TB_RSTRcvd })
            {
                if (look != null && !ReferenceEquals(box.Template, look)) box.Template = look;

                box.Items.Clear();
                // Best at the top, worst at the bottom: the strength digit counts down from 9 to 1 while
                // readability stays 5 - below R5 you are not completing the contact.
                if (voice) for (int s = 9; s >= 1; s--) box.Items.Add("5" + s);
                box.Text = best;

                TextBox editor = RstEditor(box);
                if (editor != null) editor.MaxLength = _rstDigits;
            }
        }

        // The TextBox inside an editable ComboBox. WPF builds it from the control template and insists on
        // the name PART_EditableTextBox; it is where MaxLength and the caret actually live, neither of
        // which the ComboBox itself exposes. ApplyTemplate is called first because this can run before
        // the window has ever been laid out.
        private static TextBox RstEditor(ComboBox box)
        {
            if (box == null) return null;
            try
            {
                box.ApplyTemplate();
                return box.Template == null ? null : box.Template.FindName("PART_EditableTextBox", box) as TextBox;
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); return null; }
        }

        // THE WHOLE BOX OPENS THE LIST, not just the chevron. The chevron is 10px wide in a 36px box and
        // hitting it with a mouse mid-QSO is a nuisance, so a click anywhere on the box does what a click
        // on the chevron does. Typing is untouched: the box keeps the keyboard focus while the list is
        // open (StaysOpenOnEdit), so a report can still simply be typed over.
        //
        // ON THE BUTTON COMING UP, NOT GOING DOWN. Opening it on the way down looks like doing nothing at
        // all: the list appears and the ComboBox's own handling of the release closes it again before
        // anything is drawn. Measured all four ways in a test harness - open on down, open on down and
        // swallow the release, defer the opening, open on up - and only opening on the way up survives.
        //
        // Two things this must not break. On CW there IS no list - the box has no items and wears the
        // plain template - so it returns at once and stays an ordinary typing box. And a click on a value
        // in the open list must select that value: those clicks tunnel through this handler on their way
        // down, and are told apart by being outside the box's own rectangle, the popup hanging below it.
        private void RST_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            ComboBox box = sender as ComboBox;
            if (box == null || box.Items.Count == 0) return;

            Point p = e.GetPosition(box);
            if (p.X < 0 || p.Y < 0 || p.X > box.ActualWidth || p.Y > box.ActualHeight) return;

            TextBox editor = RstEditor(box);
            if (editor != null && !editor.IsKeyboardFocusWithin) editor.Focus();

            box.IsDropDownOpen = !box.IsDropDownOpen;
            e.Handled = true;
        }

        // NO DIGIT IS EVER LEFT HIGHLIGHTED. Landing in the box used to select the strength digit so that
        // typing one number changed 59 to 57, and picking a report off the list leaves WPF's own
        // selection over the value; both painted a block of colour across a two-digit box and read as if
        // something was wrong with it. The selection is dropped and the caret parked at the end instead.
        //
        // Typing still replaces rather than being refused by a full box: the box is marked FRESH while it
        // is newly focused, and the first character typed empties it first (RST_PreviewTextInput). So
        // "5", "7" gives 57, exactly as before, and nothing is tinted on the way.
        private ComboBox _rstFresh;

        private void RST_GotFocus(object sender, RoutedEventArgs e)
        {
            _rstFresh = sender as ComboBox;
            ClearRstSelection(sender as ComboBox);
        }

        private void RST_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            ComboBox box = sender as ComboBox;
            if (box == null || !ReferenceEquals(box, _rstFresh)) return;
            _rstFresh = null;

            TextBox editor = RstEditor(box);
            if (editor != null) editor.Text = "";   // the character being typed then lands in an empty box
        }

        private void RST_DropDownClosed(object sender, EventArgs e)
        {
            ComboBox box = sender as ComboBox;
            _rstFresh = box;                        // typing after a pick starts a new report
            ClearRstSelection(box);
        }

        // AND AGAIN WHEN THE LIST OPENS. Opening the drop-down makes the ComboBox select the whole value
        // in its text box, and it does that AFTER the click that opened it - so clearing the selection on
        // focus alone left both digits highlighted, which is what a click looked like. Measured: with
        // this handler a click leaves 0 characters selected, without it 2.
        private void RST_DropDownOpened(object sender, EventArgs e)
        {
            ClearRstSelection(sender as ComboBox);
        }

        // Queued at Input priority because the ComboBox selects the text itself as focus comes back to
        // the box, after these events have been raised - clearing it any sooner would be undone.
        private void ClearRstSelection(ComboBox box)
        {
            TextBox editor = RstEditor(box);
            if (editor == null) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                editor.SelectionLength = 0;
                editor.CaretIndex = editor.Text.Length;
            }), System.Windows.Threading.DispatcherPriority.Input);
        }

        private void TB_RSTSent_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (SMeter == null) return;
            if (TB_RSTSent.Text.Length >= 2 && int.TryParse(TB_RSTSent.Text[1].ToString(), out int s))
                SMeter.SetSValue(s);
            else
                SMeter.SetSValue(0);
        }

        // Callsign -> DXCC entity name, resolved live from cty.dat and cached. Lets the status-bar DXCC
        // count be computed from the CURRENT country file (matching the Statistics window) instead of from
        // possibly-stale stored country strings, so the headline number is always fresh and never drifts.
        private readonly Dictionary<string, string> _dxccEntityCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // The entity a LOGGED callsign belongs to, answered for the day the QSO was made.
        //
        // It used to ask CountryLookup.Shared.Resolve(call) - the no-date overload, which means "as things
        // stand today". That is right for a call being typed or a cluster spot, and wrong for a QSO made
        // in 1990: prefixes are retired and reassigned, so an old contact was credited to an entity that
        // today's rules put somewhere else, or nowhere. The status bar therefore counted a different set
        // of entities from the Statistics window, which has always passed the QSO's date - and it showed:
        // "DXCCs (worked) 324" beside "Confirmed 325", a log confirmed for more entities than it had
        // worked, which cannot happen. Same call, same date, same answer, everywhere.
        private string ResolveEntityName(string call, string adifDate)
        {
            call = (call ?? string.Empty).Trim();
            if (call.Length == 0 || rem == null) return null;
            string key = call + "|" + (adifDate ?? string.Empty).Trim();
            if (!_dxccEntityCache.TryGetValue(key, out var name))
            {
                try { name = CountryLookup.Shared.Resolve(call, CountryLookup.QsoDate(adifDate))?.Name; }
                catch { name = null; }
                _dxccEntityCache[key] = name;
            }
            return name;
        }

        private void UpdateNumOfQSOs()
        {
            //parseAdif();
            NumOfQSOs = dal.GetQsoCountForLog(dal.ActiveLogId).ToString();
            NumOfGrids = dal.GetGridCountForLog(dal.ActiveLogId).ToString();
            // Count distinct DXCC entities resolved live from callsigns (always fresh, never from stale
            // stored strings). Only names that are in the OFFICIAL DXCC entity list are counted — the exact
            // same basis the Statistics window uses — so the two numbers always agree (a callsign that
            // resolves to a non-DXCC name must not inflate the count).
            var officialEntities = new HashSet<string>(
                rem != null ? rem.GetAllEntityNames() : Enumerable.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);
            NumOfDXCCs = (Qsos ?? new ObservableCollection<QSO>())
                .Select(q => ResolveEntityName(q.DXCall, q.Date))
                .Where(n => !string.IsNullOrEmpty(n) && officialEntities.Contains(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count().ToString();
            Score = "0";// _holyLogParser.Result.ToString();
        }

        private void ExitMenuItem_Click(object sender, RoutedEventArgs e)
        {
            // Close() runs the normal Window_Closing path (unsaved-QSO guard + upload-on-exit),
            // unlike Application.Shutdown() which force-tears-down and can skip that flow.
            this.Close();
        }
        private void OpenFolderItem_Click(object sender, RoutedEventArgs e)
        {
            Process.Start(AppDomain.CurrentDomain.BaseDirectory);
        }

        // For "Replace": let the user save a backup of the current log to a file they choose, then
        // clear the log. Returns false (log left untouched) if the user cancels the save dialog or the
        // backup fails — we never destroy the log without a successful backup.
        private bool BackupAndClearLogForReplace()
        {
            // The active log's name is part of the proposed backup filename so it is easy to tell which
            // log the backup belongs to. Invalid filename characters are replaced with '_'.
            string logName = null;
            try { logName = dal.GetLogName(dal.ActiveLogId); } catch (System.Exception swallowed) { Log.Swallow(swallowed); }
            string safeLog = string.IsNullOrWhiteSpace(logName)
                ? string.Empty
                : string.Join("_", logName.Split(System.IO.Path.GetInvalidFileNameChars())).Trim() + "_";

            var saveDialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "ADIF files (*.adi)|*.adi",
                FileName = "HolyLogger_backup_" + safeLog + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".adi",
                Title = "Save a backup of your current log before replacing it"
            };
            if (saveDialog.ShowDialog() != true)
                return false; // user cancelled -> abort the replace

            try
            {
                // Back up ONLY the active log (Replace replaces just this log, not every log).
                // A backup that drops fields is not a backup: carried imported fields go in too - and
                // since the log read no longer carries them, they are fetched for this backup.
                var backupQsos = dal.GetQSOsForLog(dal.ActiveLogId);
                dal.FillCarriedAdif(backupQsos);
                string adif = Services.GenerateAdif(backupQsos, Contests.ContestService.Active?.CabrilloName,
                                                    includeImportedFields: true);
                System.IO.File.WriteAllText(saveDialog.FileName, adif);
            }
            catch (Exception ex)
            {
                HolyMessageBox.ShowError("Failed to save the backup:\n" + ex.Message + "\n\nReplace cancelled — your log was not changed.", "Backup Failed", this);
                return false;
            }

            // Backup succeeded -> safe to clear ONLY the active log before importing the new file.
            Properties.Settings.Default.RecentQSOCounter = 0;
            Qsos.Clear();
            dal.DeleteQSOsForLog(dal.ActiveLogId);
            ClearBtn_Click(null, null);
            UpdateNumOfQSOs();
            UpdateEqslQueueIndicator();
            UpdateQrzMenuCount();
            return true;
        }

        private void ToggleUploadProgress(Visibility visibility)
        {
            UploadProgressSpinner.Visibility = visibility;
            L_UploadProgress.Visibility = visibility;
        }

        // Show/hide the spinner's Stop button. Only the cancellable operations (Remove Duplicates,
        // Full-Log QRZ Service) turn it on; everything else leaves it hidden.
        private void ShowStopButton(bool show)
        {
            if (Btn_StopProgress == null) return;
            Btn_StopProgress.Content = "Stop";
            Btn_StopProgress.IsEnabled = true;
            Btn_StopProgress.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }

        // Stop button inside the spinner window: cancels whichever long operation is running.
        private void Btn_StopProgress_Click(object sender, RoutedEventArgs e)
        {
            _dedupCts?.Cancel();
            _qrzCts?.Cancel();
            Btn_StopProgress.IsEnabled = false;
            Btn_StopProgress.Content = "Stopping…";
            UploadProgress = "Stopping…";
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

        private void ExpotCSVMenuItem_Click(object sender, RoutedEventArgs e)
        {
            // Export the ACTIVE log only (the "(Active Log)" menu label), via the shared helper.
            ExportQsosToCsv(dal.GetQSOsForLog(dal.ActiveLogId), this);
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

            // Send exactly what the operator has open — the ACTIVE log. This used to read every QSO in
            // the database (an imported friend's log, an old contest), so the file that reached the
            // organisers was not the log shown on screen.
            var logToSend = dal.GetQSOsForLog(dal.ActiveLogId);

            if (w.selectedRadioEvent.Name.ToLower() == "holyland")
            {
                string UploadCabrilloToIARC_result = await UploadCabrilloToIARC(bareCallsign, w.selectedOperator.Name, w.selectedMode.Name, w.selectedBand.Name, w.selectedPower.Name, w.selectedOverlay.Name, Properties.Settings.Default.PersonalInfoEmail, Properties.Settings.Default.PersonalInfoName, country, logToSend);
                w.Close();
                HolyMessageBox.Show(UploadCabrilloToIARC_result, "Log Upload", HolyMsgType.Info, this);
            }
            else
            {
                string AddParticipant_result = await AddParticipant(bareCallsign, w.selectedOperator.Name, w.selectedMode.Name, w.selectedPower.Name, Properties.Settings.Default.PersonalInfoEmail, Properties.Settings.Default.PersonalInfoName, country);
                string UploadLogToIARC_result = await UploadLogToIARC(new Progress<int>(percent => w.UploadProgress = percent), logToSend);
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
                // Started off the UI thread: on .NET Framework the proxy for a request is resolved on
                // whichever thread starts it, and this is called from the form.
                var response = await Task.Run(() => _sharedHttpClient.PostAsync("https://tools.iarc.org/Holyland/Server/AddParticipant.php", content));
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
                    var response = await Task.Run(() => _sharedHttpClient.PostAsync("https://tools.iarc.org/Holyland/Server/AddQSO.php", content));
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

        private List<List<QSO>> SplitQSOList(ObservableCollection<QSO> QSOList)
        {
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
            SaveLogColumnLayout();
        }

        // ── THE LOG TABLE'S COLUMN LAYOUT ─────────────────────────────────
        //
        // Captured and restored by GridColumnLayout, which the Log Workshop's table uses too - see there
        // for the format and for why it replaced the old one-setting-per-column scheme.

        // Set while the saved layout is being applied, so the DisplayIndex changes made here do not each
        // fire the handler and write a half-applied order back over the setting.
        private bool _applyingLogColumnLayout;

        // False until the saved layout has been put on the grid. WPF raises ColumnDisplayIndexChanged
        // while it is still assigning the XAML's own indexes at load, and saving then would write the
        // default arrangement straight over the one the operator arranged in the last session.
        private bool _logColumnLayoutApplied;

        private void SaveLogColumnLayout()
        {
            if (_applyingLogColumnLayout || !_logColumnLayoutApplied || QSODataGrid == null) return;
            try
            {
                Properties.Settings.Default.LogColumnLayout = GridColumnLayout.Capture(QSODataGrid);
                SettingsFlush.RequestSave();
            }
            catch (System.Exception swallowed) { Log.Swallow(swallowed); }
        }

        private void ApplyLogColumnLayout()
        {
            if (QSODataGrid == null) return;
            try
            {
                _applyingLogColumnLayout = true;

                // Nothing saved by this scheme yet - so this is either a new installation or the first run
                // after upgrading from the per-column settings. Seed from those, so an operator who has
                // already arranged their table does not find it rearranged by the upgrade.
                string layout = (Properties.Settings.Default.LogColumnLayout ?? string.Empty).Trim();
                if (layout.Length == 0)
                {
                    ApplyLegacyLogColumnLayout();
                    return;
                }

                GridColumnLayout.Apply(QSODataGrid, layout);
            }
            catch (System.Exception swallowed) { Log.Swallow(swallowed); }
            finally
            {
                _applyingLogColumnLayout = false;
                _logColumnLayoutApplied = true;   // from here on, a drag or a resize is the operator's
            }
        }

        // The layout as the pre-8.8.7 settings held it: a position for thirteen of the columns and a width
        // for three. Read once, on the first run after the upgrade, and then written back out in the new
        // form - after which these settings are never read again.
        private void ApplyLegacyLogColumnLayout()
        {
            var saved = new List<KeyValuePair<string, int>>
            {
                new KeyValuePair<string, int>("Date", Properties.Settings.Default.Date_index),
                new KeyValuePair<string, int>("Time", Properties.Settings.Default.Time_index),
                new KeyValuePair<string, int>("Callsign", Properties.Settings.Default.Callsign_index),
                new KeyValuePair<string, int>("Name", Properties.Settings.Default.Name_index),
                new KeyValuePair<string, int>("Country", Properties.Settings.Default.Country_index),
                new KeyValuePair<string, int>("Frequency", Properties.Settings.Default.Frequency_index),
                new KeyValuePair<string, int>("Band", Properties.Settings.Default.Band_index),
                new KeyValuePair<string, int>("RST-R", Properties.Settings.Default.RSTrcvd_index),
                new KeyValuePair<string, int>("RST-S", Properties.Settings.Default.RSTsent_index),
                new KeyValuePair<string, int>("Mode", Properties.Settings.Default.Mode_index),
                new KeyValuePair<string, int>("Exchange", Properties.Settings.Default.Exchange_index),
                new KeyValuePair<string, int>("Comment", Properties.Settings.Default.Comment_index),
                new KeyValuePair<string, int>("LoTW", Properties.Settings.Default.Lotw_index),
            };

            foreach (var col in QSODataGrid.Columns)
            {
                string header = GetBaseColumnHeader(col);
                var pos = saved.FirstOrDefault(p => p.Key == header);
                // A column with no saved position keeps the place the XAML gave it. -1 means "never
                // saved" for a known column and is left alone for the same reason.
                if (pos.Key == null) continue;
                if (pos.Value < 0 || pos.Value >= QSODataGrid.Columns.Count) continue;
                col.DisplayIndex = pos.Value;
            }

            // Only LoTW's position was remembered out of the five confirmation columns, so the other four
            // are still where the XAML put them. Pull the block back together around LoTW, or the group
            // arrives split - see ConfirmationColumnGroup.
            ConfirmationColumnGroup.Normalize(QSODataGrid);

            ApplyLegacyWidth("Callsign", Properties.Settings.Default.ColWidthCallsign);
            ApplyLegacyWidth("Name", Properties.Settings.Default.ColWidthName);
            ApplyLegacyWidth("Country", Properties.Settings.Default.ColWidthCountry);
        }

        private void ApplyLegacyWidth(string header, double width)
        {
            if (width <= 0 || double.IsNaN(width)) return;
            var col = QSODataGrid.Columns.FirstOrDefault(c =>
                string.Equals(GetBaseColumnHeader(c), header, StringComparison.Ordinal));
            if (col != null) col.Width = new DataGridLength(width);
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
                ActivityFromQso(QsoToUpdate);       // IOTA / SOTA / POTA / WWFF and the Other pair
                TB_RSTRcvd.Text = QsoToUpdate.RST_RCVD;
                TB_RSTSent.Text = QsoToUpdate.RST_SENT;
                TB_DX_Name.Text = QsoToUpdate.Name;

                // In a contest the received exchange (RST-R + e.g. Holyland Square) lives in the
                // ContestRxPanel boxes, not the now-hidden TB_RSTRcvd/TB_Exchange. Setting the DX
                // callsign above was suppressed, so the received frame was NOT rebuilt for this QSO —
                // do it now so the correct field shows, populated from the loaded values, and editable.
                if (Contests.ContestService.Active != null)
                    ApplyContestExchangeUI();
                // Country/Continent are normally filled by the (now-suppressed) callsign lookup, so
                // load the QSO's saved values directly into the bound properties, and show the
                // country flag the same way the lookup does (falls back to the text label if there
                // is no flag for that country).
                Country = QsoToUpdate.Country;
                Continent = QsoToUpdate.Continent;
                UpdateCountryFlag(QsoToUpdate.Country);

                ShowDxccCode(QsoToUpdate.DxccCode > 0 ? QsoToUpdate.DxccCode : EntityCodeForCall(QsoToUpdate.DXCall, QsoToUpdate.Date));

                // Load the QSO's stored ITU/CQ zones. For QSOs logged before zones were stored
                // (empty), fall back to re-deriving them from the callsign via cty.dat so the
                // boxes aren't blank.
                TB_CQZone.Text = QsoToUpdate.CQZone ?? string.Empty;
                TB_ITUZone.Text = QsoToUpdate.ITUZone ?? string.Empty;
                TB_State.Text = QsoToUpdate.State ?? string.Empty;   // ADIF STATE, stored with the QSO
                TB_QTH.Text = QsoToUpdate.Qth ?? string.Empty;       // ADIF QTH, stored with the QSO
                if (string.IsNullOrWhiteSpace(TB_CQZone.Text) || string.IsNullOrWhiteSpace(TB_ITUZone.Text))
                {
                    try
                    {
                        DXCC editDxcc = CountryLookup.Shared.Resolve((QsoToUpdate.DXCall ?? string.Empty).Trim(),
                                                                    CountryLookup.QsoDate(QsoToUpdate.Date));
                        if (string.IsNullOrWhiteSpace(TB_ITUZone.Text) && editDxcc.ItuZone > 0)
                            TB_ITUZone.Text = editDxcc.ItuZone.ToString();
                        if (string.IsNullOrWhiteSpace(TB_CQZone.Text) && editDxcc.CqZone > 0)
                            TB_CQZone.Text = editDxcc.CqZone.ToString();
                    }
                    catch (System.Exception swallowed) { Log.Swallow(swallowed); }
                }

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
                    // Malformed stored date/time (old import, corrupt row): recover with NOW and keep
                    // loading — throwing here aborted the rest of the edit-load (ShowRigParams etc.)
                    // and scared the user with an error box over a value we already fixed.
                    Log.Swallow(e);
                    TP_Date.Value = DateTime.UtcNow;
                    TP_Time.Value = DateTime.UtcNow;
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
            // Editing an existing QSO means the radio is no longer setting the mode, so the combo has to
            // be the operator's for as long as the edit lasts - and locked again the moment it ends.
            UpdateModeComboLock();
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
            // Theme-aware: normal = input surface (white in light, dark in dark); edit = highlight.
            var editModeColor = ThemeManager.Brush("EditFieldBg");
            var normalColor = ThemeManager.Brush("ControlBg");

            var backgroundColor = (state == State.Edit) ? editModeColor : normalColor;

            // Only highlight QSO-specific fields, not user station information
            TB_Frequency.Background = backgroundColor;
            TB_DXCallsign.Background = backgroundColor;
            TB_Exchange.Background = backgroundColor;
            TB_RSTSent.Background = backgroundColor;
            TB_RSTRcvd.Background = backgroundColor;
            TB_DX_Name.Background = backgroundColor;
            TB_State.Background = backgroundColor;
            TB_QTH.Background = backgroundColor;
            TB_DXLocator.Background = backgroundColor;
            TB_Comment.Background = backgroundColor;
            TB_DXCC.Background = backgroundColor;
            // The activity row's "other program" pair belongs to the QSO like every box above it, so it
            // wears the edit-mode colour too - and is as editable in edit mode as it is when logging.
            CB_ActivitySig.Background = backgroundColor;
            TB_ActivitySigInfo.Background = backgroundColor;
            CB_Mode.Background = backgroundColor;
            TB_ITUZone.Background = backgroundColor;
            TB_CQZone.Background = backgroundColor;

            // The activity boxes take the same yellow, but they also paint themselves pale red while
            // what is in them is not a valid reference. Remember which colour "not complaining" means
            // right now, then let each box decide again - so an edit-mode box that holds rubbish stays
            // red instead of being quietly turned yellow.
            SetActivityNormalBackground(backgroundColor);

            // Contest mode replaces TB_RSTRcvd/TB_Exchange with the ContestRxPanel cells (RST-R +
            // e.g. Holyland Square). Highlight/reset those the same way, so leaving edit mode clears
            // their yellow like every other field.
            if (_contestRstRcvdBox != null) _contestRstRcvdBox.Background = backgroundColor;
            foreach (var b in _contestRxBoxes) b.Background = backgroundColor;
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


                // The frequency/band check is no longer here. Whether a QSO must sit on a real amateur band
                // is its own opt-in setting ("Validate for HAM frequency"), enforced as a soft warning in
                // ConfirmHamFrequencyBeforeSave on Add (F1) — independent of this general validation flag.
                // Still refresh the status so the frequency box keeps the right border colour (red = CAT
                // off/offline or Manual, which UpdateStatus owns and re-applies).
                UpdateStatus();

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

        // Guard so the radio buttons' Checked handler doesn't re-enter while WE are setting them.
        private bool _settingFreqModeRadios = false;

        // Clicking "Manual" or "CAT" in the status bar. Routed through the same path as the menu item so
        // there is one owner of the state.
        //
        // Compared against the SETTING, not against what the radios show. The two differ whenever CAT is
        // unavailable: the display then reads Manual (the frequency is typed, whatever the setting says)
        // while the setting is still CAT. Comparing against the display made the click a no-op there, so
        // a station with no CAT interface could never actually choose Manual - and therefore never got
        // the band picker.
        private void FreqMode_Click(object sender, RoutedEventArgs e)
        {
            if (_settingFreqModeRadios) return;
            bool wantManual = sender == RB_ManualMode;
            if (wantManual == Properties.Settings.Default.isManualMode)
            {
                // Already in that state. The click may still have moved the dot away from where the
                // display belongs (clicking CAT while it is only shown as Manual), so put it back.
                UpdateFreqModeRadios();
                return;
            }
            ToggleManualMode();
        }

        // True when the frequency really can come from the radio right now.
        private bool IsCatFrequencyAvailable =>
            Properties.Settings.Default.EnableOmniRigCAT
            && OmniRigEngine != null && Rig != null
            && Rig.Status == OmniRig.RigStatusX.ST_ONLINE;

        // Where the frequency ACTUALLY comes from, which is what the radio buttons must show. Manual
        // mode means typed - but so does having no CAT, whatever the manual-mode setting says.
        private bool IsFrequencyTyped =>
            Properties.Settings.Default.isManualMode || !IsCatFrequencyAvailable;

        // Whether the frequency was TYPED last time the radios were refreshed. Null until the first
        // refresh, so starting the program with CAT live is not mistaken for the operator switching to
        // it - a Time: Manual saved from the last session survives the launch.
        private bool? _prevFreqTyped;

        // Selects the right radio button and disables CAT when the frequency cannot come from the radio
        // (CAT off, no rig, or rig offline) - choosing it there would promise something that can't happen.
        // Also the one place that notices CAT taking the frequency back over (see _prevFreqTyped).
        private void UpdateFreqModeRadios()
        {
            if (RB_ManualMode == null || RB_CatMode == null) return;

            bool catAvailable = IsCatFrequencyAvailable;
            bool typed = IsFrequencyTyped;

            // The frequency has just gone back to coming from the radio (the operator clicked CAT, or
            // enabled CAT / brought the rig online). Release the clock with it: a program that is
            // following the rig live has no business showing a time that stopped some while ago, and an
            // operator who set BOTH to Manual to type up a page of old contacts should not have to
            // remember the second switch on the way back.
            //
            // Deliberately an EDGE, not a standing rule: only this transition forces Auto, so choosing
            // Time: Manual afterwards, with CAT running, is left alone.
            if (_prevFreqTyped == true && !typed)
                ForceTimeAuto();
            _prevFreqTyped = typed;

            _settingFreqModeRadios = true;
            try
            {
                // Show MANUAL whenever the frequency is typed. With no CAT it used to show "CAT"
                // selected but greyed, which claimed the frequency came from a radio that wasn't
                // connected. Note this only changes what is DISPLAYED: the isManualMode setting is left
                // alone, so when the radio comes online the program returns to CAT by itself.
                RB_ManualMode.IsChecked = typed;
                RB_CatMode.IsChecked = !typed;

                bool chosenManual = Properties.Settings.Default.isManualMode;

                // CAT is ALWAYS clickable, even with no radio connected: a greyed-out, unpressable CAT
                // button left a station whose rig was offline (or has no CAT at all) with no way to select
                // CAT. It still dims (0.5) while CAT can't actually read the frequency, as a cue that the
                // frequency stays typed until a radio comes online — but the click itself is never blocked.
                RB_CatMode.IsEnabled = true;
                RB_CatMode.Opacity = catAvailable ? 1.0 : 0.5;
                // The tooltips speak of the frequency and the mode ONLY. They used to promise that Manual
                // also held the date and time still; it no longer does (see UTCTimer_Elapsed), and a
                // tooltip that describes behaviour the program has stopped having is worse than none.
                RB_CatMode.ToolTip = catAvailable
                    ? "The frequency and mode are read from the radio over CAT."
                    : chosenManual
                        ? "Leave Manual. CAT is not connected, so the frequency is still typed - but the "
                          + "program goes back to the radio by itself as soon as one comes online."
                        : "CAT is not connected, so the frequency cannot be read from the radio. "
                          + "Enable it in Options > General and put the radio online.";

                // Manual is shown for two different reasons - because it was chosen, or because there is
                // no CAT to read from - and they behave differently (a chosen Manual lets you pick the
                // band, and holds even after a radio comes online). Bold says which one this is.
                // Manual is BOLD whenever it is the active mode — which includes the automatic fallback
                // when CAT can't be used (no radio, or the rig has stopped responding): there is no dial
                // to read, so Manual takes focus by itself and CAT is left unselected. Only when CAT is
                // truly live and selected does Manual drop to regular weight.
                RB_ManualMode.FontWeight = typed ? FontWeights.Bold : FontWeights.Normal;
                RB_ManualMode.ToolTip = chosenManual
                    ? "Manual: you type the frequency and pick the mode yourself, and the program leaves "
                      + "them alone even when a radio is online."
                    : catAvailable
                        ? "You type the frequency and pick the mode yourself instead of reading them from "
                          + "the radio."
                        : "CAT is not connected, so the frequency is typed. HolyLogger stays in Manual "
                          + "until a radio comes online.";
            }
            finally { _settingFreqModeRadios = false; }
        }

        // Kept as the handler for the frequency box's "Change mode" context-menu item.
        private void ManualModeMenuItem_Click(object sender, RoutedEventArgs e) => ToggleManualMode();

        // Flips between Manual and CAT. The single owner of the state: the status-bar radios and the
        // "Change mode" context item both come through here.
        private void ToggleManualMode()
        {
            // CAT needs a radio. Trying to switch from Manual to CAT with none connected does nothing:
            // there is no dial to read, so pressing CAT must leave Manual exactly as it was — still
            // chosen, still bold, CAT never lit. (The CAT button stays clickable, not greyed; the click
            // simply has no effect until a radio is actually there.) Only the Manual->CAT direction is
            // guarded — leaving CAT for Manual is always allowed.
            bool wouldChooseCat = Properties.Settings.Default.isManualMode;
            if (wouldChooseCat && !IsCatFrequencyAvailable)
            {
                UpdateFreqModeRadios();   // re-assert Manual (selected + bold); undo the radio's own click
                RB_ManualMode?.Focus();   // and take focus off CAT, which never became the mode
                return;
            }

            Properties.Settings.Default.isManualMode = !Properties.Settings.Default.isManualMode;
            // Drive the radios from the setting, not from their own click, so they follow the state
            // however it was changed.
            UpdateFreqModeRadios();
            ShowRigParams();
            // Repaint the frequency box's border: in Manual Mode the frequency is typed, not read from
            // the radio, so it gets the same red border as CAT disabled/offline. Called directly because
            // ShowRigParams -> ShowRigStatus only reaches UpdateStatus when a rig object exists.
            UpdateStatus();
            // Swap the lit LED for the no-CAT typing box (and back). ShowRigParams returns early once
            // the rig is online, so it does not do this for us when CAT is connected.
            UpdateFreqLed();

            // The Band box becomes a drop-down in Manual with no frequency, and goes back to being a
            // read-out of the frequency the moment CAT is chosen.
            UpdateBandPickAvailability();

            // Nothing to do about the date and time here: they are the Time toggle's business now (see
            // TimeMode_Click), and this switch no longer touches them.
        }

        // ---- Time: Manual / Auto -----------------------------------------------------------------

        // Guard, like _settingFreqModeRadios: ignore the clicks we raise ourselves.
        private bool _settingTimeModeRadios = false;

        // Clicking "Manual" or "Auto" in the status bar's Time box. Manual holds the QSO's date and time
        // exactly where the operator put them, so a contact that happened earlier can be logged with its
        // real time; Auto lets them follow the UTC clock. The frequency is not this switch's business -
        // that is the box next door.
        private void TimeMode_Click(object sender, RoutedEventArgs e)
        {
            if (_settingTimeModeRadios) return;

            bool wantManual = sender == RB_TimeManual;
            if (wantManual == Properties.Settings.Default.isTimeManual)
            {
                // Already in that state; the click may still have moved the fill, so put it back.
                UpdateTimeModeRadios();
                return;
            }

            Properties.Settings.Default.isTimeManual = wantManual;
            UpdateTimeModeRadios();

            // Back to Auto: catch the clock up at once rather than waiting for the next tick, so the
            // click is visibly doing something. Never over a QSO being edited - its own time stands.
            if (!wantManual && state == State.New)
            {
                TP_Date.Value = DateTime.UtcNow;
                TP_Time.Value = DateTime.UtcNow;
            }
        }

        // Puts the Time toggle back to Auto and catches the clock up. Called when CAT takes the frequency
        // back over; does nothing if the clock is already running, so it costs nothing to call.
        private void ForceTimeAuto()
        {
            if (!Properties.Settings.Default.isTimeManual) return;

            Properties.Settings.Default.isTimeManual = false;
            UpdateTimeModeRadios();

            // Never over a QSO being edited - its own date and time stand.
            if (state == State.New)
            {
                TP_Date.Value = DateTime.UtcNow;
                TP_Time.Value = DateTime.UtcNow;
            }
        }

        // Shows which half of the Time toggle is live, driven from the setting so it follows the state
        // however it was changed. Bold on Manual for the same reason the frequency pill bolds its Manual:
        // the half that is holding something still is the one worth spotting.
        private void UpdateTimeModeRadios()
        {
            if (RB_TimeManual == null || RB_TimeAuto == null) return;

            bool manual = Properties.Settings.Default.isTimeManual;
            _settingTimeModeRadios = true;
            try
            {
                RB_TimeManual.IsChecked = manual;
                RB_TimeAuto.IsChecked = !manual;
                RB_TimeManual.FontWeight = manual ? FontWeights.Bold : FontWeights.Normal;
            }
            finally { _settingTimeModeRadios = false; }
        }

        private void ResetRecentQSOCounterMenuItem_Click(object sender, RoutedEventArgs e)
        {
            Properties.Settings.Default.RecentQSOCounter = 0;
        }

        // The cty.dat continent ("NA","AS",…) for a callsign, used to pick asymmetric exchange variants.
        private string ContinentOf(string call)
        {
            if (rem == null || string.IsNullOrWhiteSpace(call)) return null;
            var d = CountryLookup.Shared.Resolve(call.Trim());
            return d != null ? d.Continent : null;
        }

        private static bool IsRstField(string f) =>
            string.Equals(f, "RST", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(f, "RS", StringComparison.OrdinalIgnoreCase);

        // The value your station sends for a given exchange field: the operator's override for the
        // current contest if set, otherwise the auto value (cty.dat zone / serial counter / my square).
        private string GetSendFieldValue(string field, string myCall)
        {
            string zoneOverride = (Properties.Settings.Default.ContestMyZoneOverride ?? string.Empty).Trim();
            switch ((field ?? string.Empty).ToUpperInvariant())
            {
                case "CQ_ZONE":
                {
                    if (zoneOverride.Length > 0) return zoneOverride;
                    var d = rem != null ? rem.GetDXCC((myCall ?? string.Empty).Trim()) : null;
                    return d != null && d.CqZone > 0 ? d.CqZone.ToString() : string.Empty;
                }
                case "ITU_ZONE":
                {
                    if (zoneOverride.Length > 0) return zoneOverride;
                    var d = rem != null ? rem.GetDXCC((myCall ?? string.Empty).Trim()) : null;
                    return d != null && d.ItuZone > 0 ? d.ItuZone.ToString() : string.Empty;
                }
                case "SERIAL":
                    return Properties.Settings.Default.ContestNextSerial.ToString("000");
                case "HOLYLAND_AREA":
                    return Properties.Settings.Default.my_square ?? string.Empty;
                default:
                    // Anything else (NAME, STATE, SECTION, CHECK, GRID, …) has no automatic source, so
                    // we reuse whatever the operator last typed for that field (remembered across the
                    // contest and restarts).
                    return GetRememberedSendValue((field ?? string.Empty).ToUpperInvariant());
            }
        }

        // A small remembered store of operator-typed send values keyed by field name, persisted as a
        // simple "FIELD=value" line list in settings so fields like NAME / STATE survive restarts.
        private static Dictionary<string, string> LoadRememberedSendValues()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string raw = Properties.Settings.Default.ContestSendValues ?? string.Empty;
            foreach (string line in raw.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = line.IndexOf('=');
                if (eq > 0) map[line.Substring(0, eq)] = line.Substring(eq + 1);
            }
            return map;
        }

        private static string GetRememberedSendValue(string fieldKey)
        {
            return LoadRememberedSendValues().TryGetValue(fieldKey, out string v) ? v : string.Empty;
        }

        private static void SetRememberedSendValue(string fieldKey, string value)
        {
            var map = LoadRememberedSendValues();
            map[fieldKey] = value;
            Properties.Settings.Default.ContestSendValues =
                string.Join("\n", map.Select(kv => kv.Key + "=" + kv.Value));
            Properties.Settings.Default.Save();
        }

        // Persists an operator edit to an auto-filled send box so it sticks for the rest of the contest
        // and across restarts. A serial keeps counting from the entered number; a zone overrides cty.dat.
        private void SaveSendFieldEdit(string fieldKey, string text)
        {
            string t = (text ?? string.Empty).Trim();
            switch (fieldKey)
            {
                case "SERIAL":
                    if (int.TryParse(t, out int n) && n > 0)
                    {
                        Properties.Settings.Default.ContestNextSerial = n;
                        Properties.Settings.Default.Save();
                    }
                    break;
                case "CQ_ZONE":
                case "ITU_ZONE":
                    Properties.Settings.Default.ContestMyZoneOverride = t;
                    Properties.Settings.Default.Save();
                    break;
                case "HOLYLAND_AREA":
                    Properties.Settings.Default.my_square = t;
                    Properties.Settings.Default.Save();
                    break;
                default:
                    SetRememberedSendValue(fieldKey, t);
                    break;
            }
        }

        // Sets a contest exchange label to "line1" alone, or "line1 / line2" (line2 bold) when twoLine.
        private static void SetExchangeLabel(TextBlock tb, string line1, string line2, bool twoLine)
        {
            if (tb == null) return;
            tb.Inlines.Clear();
            tb.Inlines.Add(new System.Windows.Documents.Run(line1) { FontWeight = FontWeights.Bold });
            if (twoLine)
            {
                tb.Inlines.Add(new System.Windows.Documents.LineBreak());
                tb.Inlines.Add(new System.Windows.Documents.Run(line2));
            }
        }

        // Original Margin.Top of each Grid.Row=1 control, captured once so layout toggles are exact.
        private Dictionary<FrameworkElement, double> _rowOrigTop;

        // How far each control moves in contest mode, by its normal Y band. Space is reclaimed from
        // BOTH ends — the top two rows nudge up, the lower rows slide down toward the X-icons — so the
        // Exchange row gets enough height to keep the label above the box without crowding.
        //
        // EVERY NUMBER HERE WAS RE-DERIVED when the normal-mode rows were re-pitched to an even 10px
        // gap (tops 11, 49, 87, 125, 163, 201, and the activity row at 239). The contest layout itself
        // did not change by one pixel: each shift grew by exactly as much as its row moved up, so
        // baseTop + shift still lands where it always did. The rows moved up by 0, 7, 14, 20, 26 and 30
        // pixels from the top down, which is where the odd-looking constants below come from.
        //
        // The activity row is not in this table at all: contest mode hides it (see SetActivityRowVisible).
        private double RowShift(FrameworkElement fe, double baseTop)
        {
            if (fe == MainFormBackgroundRect) return 0;     // page background never moves
            if (fe == FormFrame) return 0;                  // blue entry-form frame is fixed; must not shift
            if (fe == ContestExchangeFrame) return 0;       // frame is positioned for contest mode already
            if (fe == ActivityRow) return 0;                // hidden in contest mode; nothing to place

            // Duplicate/Legal banner: follows the DX Callsign box down so it keeps touching the box's
            // LOWER rim in contest mode, as in normal mode. L_LegalFrame is named explicitly because it
            // is the element that carries the margin (L_Legal is the TextBlock inside it), and it now
            // sits 5px above the DX Callsign row's band, so the band default would place it wrongly.
            if (fe == L_Duplicate || fe == L_Legal || fe == L_LegalFrame) return 45;

            if (fe.Margin.Left >= 670) return 0;            // right-hand map area never moves

            if (fe == ContestSendBand) return -9;           // "You send" band sits in the freed top strip (~y73)
            if (fe == ContestTxPanel) return -7;            // send cells (RST S + send field) centered in the band
            if (fe == L_SendLabel) return -2;               // "Exchange/send" 2-line label, centered in the band
            if (fe == ContestDividerLine) return 45;        // divider + DX Callsign row

            if (fe == L_ExchangeLabel) return 38;           // "Exchange/received" 2-line label, centered in the frame

            // The received exchange box gets a label above it, which lowers it. Drop the RST
            // labels+boxes and the Add(F1) button to that same line so the Exchange row aligns.
            if (fe == TB_RSTSent || fe == TB_RSTRcvd || fe == L_RstSLabel || fe == L_RstRLabel
                || fe == TB_QTH || fe == L_QTHLabel
                || fe == SMeter || fe == AddBtn) return 52;

            if (baseTop < 40) return -5;                    // top row (Station / My Locator / Square) up
            if (baseTop < 70) return -3;                    // Operator / Freq / Band / Mode row up
            if (baseTop < 120) return 40;                   // DX Callsign row down
            if (baseTop < 155) return 40;                   // Exchange row content (boxes) down
            if (baseTop < 185) return 49;                   // Name / Country / State row down
            if (baseTop < 230) return 47;                   // DX Locator / ITU / CQ / Comment row down
            return 0;                                       // activity row, X icons + log table — fixed
        }

        private void ShareStatusButton_Click(object sender, RoutedEventArgs e)
        {
            Properties.Settings.Default.ShowOnTheAir = !Properties.Settings.Default.ShowOnTheAir;
            Properties.Settings.Default.Save();
            UpdateShareStatusButtonState();
        }

        private void UpdateShareStatusButtonState()
        {
            if (ShareStatusButton == null) return;
            bool on = Properties.Settings.Default.ShowOnTheAir;
            ShareStatusButton.Background = on
                ? new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50))
                : new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E));
            ShareStatusButton.BorderBrush = on
                ? new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32))
                : new SolidColorBrush(Color.FromRgb(0x75, 0x75, 0x75));
        }

        private async void PropertiesWindow_Closed(object sender, EventArgs e)
        {
            if (String.IsNullOrWhiteSpace(SessionKey))
                if (isNetworkAvailable) _SessionKey = await Helper.LoginToQRZAsync();
        }

        private bool _startupCallsignChecked;

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            if (Properties.Settings.Default.EnableOmniRigCAT)
                StartOmniRig();
            UpdateStatus();
            UpdateShareStatusButtonState();

            // Also check the stored Station Callsign on startup: a wrong/uncovered callsign saved in a
            // previous session would otherwise give no clue that no upload service handles it. Deferred
            // so the main window paints before the alert (if any) appears, and run only once.
            if (!_startupCallsignChecked)
            {
                _startupCallsignChecked = true;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    ShowStationCallsignServicesAlert(TB_MyCallsign.Text?.Trim(), isStartup: true);
                }), System.Windows.Threading.DispatcherPriority.Background);
            }
        }

        // Guards against asking about the profile twice in one close attempt.
        private bool _profileSaveOnExitHandled = false;

        // Asks whether to keep this session's changes in the active profile. Returns false when the
        // operator cancelled the close. Startup reloads the profile from its file, so "No" genuinely
        // discards the changes - the wording says so rather than leaving it to be discovered.
        private bool GuardUnsavedProfile(System.ComponentModel.CancelEventArgs e)
        {
            try
            {
                if (!ProfileManager.CurrentDiffersFromActive()) return true;

                string name = ProfileManager.ActiveProfile;
                bool save = HolyMessageBox.ShowConfirm(
                    $"You changed settings since the profile \"{name}\" was saved.\n\n" +
                    $"YES - save the changes into \"{name}\".\n" +
                    "NO  - discard them; HolyLogger will start from the profile as it was saved.",
                    "Unsaved profile changes", HolyMsgType.Warning, this);

                if (save && !ProfileManager.Save(name))
                    HolyMessageBox.ShowError($"Could not save the profile \"{name}\".", "Profile Manager", this);
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
            return true;
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_isShutdownCleanupDone)
                return;

            // A re-entrant Closing call (Alt+F4, or the taskbar/system-menu close, arriving while the
            // async upload from a PRIOR Closing pass is still running -- this.IsEnabled=false disables
            // our own custom title-bar close button, but not OS-level close signals) must not fall
            // through to the unconditional cleanup below and close the window out from under that
            // in-flight work. Doing so left UploadProgressWindow (and the per-service confirmation
            // dialogs) with Owner pointing at a window WPF had already torn down, throwing
            // InvalidOperationException: "Cannot set Owner property to a Window that has been closed."
            // This flag is only true while the upload is genuinely in flight; UploadAllAndCloseAsync
            // clears it right before its own, legitimate, final Close() call, so that one isn't blocked.
            if (_uploadInFlight)
            {
                e.Cancel = true;
                return;
            }

            // Offer to save an in-progress QSO before the app closes (unless Windows is ending the
            // session, where we must not block with a dialog). Only runs once per close attempt.
            if (!_uploadOnExitHandled && !App.IsWindowsSessionEnding)
                GuardUnsavedQso("close HolyLogger");

            // The program starts from whatever the active profile holds, so anything changed this
            // session and not saved into it would be lost. Say so and let the operator decide. Skipped
            // during Windows logoff/shutdown, where a modal dialog would stall the whole session.
            if (!_profileSaveOnExitHandled && !App.IsWindowsSessionEnding)
            {
                _profileSaveOnExitHandled = true;
                if (!GuardUnsavedProfile(e)) return;   // cancelled -> stop closing
            }

            // Upload-on-exit: show ALL service dialogs in one pass before any uploading starts,
            // so we never call Close() from inside an async upload (which caused freezes when the
            // second/third dialog tried to show while the main window was already half-destroyed).
            // A single UploadAllAndCloseAsync call does all uploads, then calls Close() exactly once.
            // Skipped entirely during Windows logoff/shutdown: a modal dialog would stall the whole
            // session end, and e.Cancel is ignored there anyway -- queued QSOs stay safely in the
            // queues and upload on the next normal exit.
            if (!_uploadOnExitHandled && !App.IsWindowsSessionEnding)
            {
                _uploadOnExitHandled = true;

                List<QSO> lotwToUpload = null;
                bool uploadEqsl = false;
                bool uploadQrz = false;
                bool uploadClublog = false;

                // ── LoTW ─────────────────────────────────────────────────────────────────────
                int lotwMode = Properties.Settings.Default.UseLotwService
                    ? Properties.Settings.Default.LotwUploadOnExitMode
                    : 0;   // service switched off in Options -> never prompt or upload on exit
                if (lotwMode != 0)
                {
                    List<QSO> lotwPending = null;
                    try { lotwPending = dal?.GetPendingLotwQsos(); } catch (System.Exception swallowed) { Log.Swallow(swallowed); }
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
                int eqslMode = Properties.Settings.Default.UseEqslService
                    ? Properties.Settings.Default.EqslUploadOnExitMode
                    : 0;   // service switched off in Options -> never prompt or upload on exit
                if (eqslMode != 0)
                {
                    int eqslPending = 0;
                    try { eqslPending = dal?.GetPendingEqslCount() ?? 0; } catch (System.Exception swallowed) { Log.Swallow(swallowed); }
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
                int qrzMode = Properties.Settings.Default.UseQrzLogbook
                    ? Properties.Settings.Default.QrzUploadOnExitMode
                    : 0;   // service switched off in Options -> never prompt or upload on exit
                if (qrzMode != 0)
                {
                    int qrzPending = 0;
                    try { qrzPending = dal?.GetPendingQrzCount() ?? 0; } catch (System.Exception swallowed) { Log.Swallow(swallowed); }
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

                // ── Club Log ─────────────────────────────────────────────────────────────────
                int clublogMode = Properties.Settings.Default.UseClublogService
                    ? Properties.Settings.Default.ClublogUploadOnExitMode
                    : 0;   // service switched off in Options -> never prompt or upload on exit
                if (clublogMode != 0)
                {
                    int clublogPending = 0;
                    try { clublogPending = dal?.GetPendingClublogCount() ?? 0; } catch (System.Exception swallowed) { Log.Swallow(swallowed); }
                    if (clublogPending > 0)
                    {
                        bool doUpload = clublogMode == 2;
                        if (clublogMode == 1)
                        {
                            var dlg = new ServiceUploadOnExitDialog("Club Log", clublogPending) { Owner = this };
                            dlg.ShowDialog();
                            if (dlg.DialogResult2 == ServiceUploadOnExitDialog.Result.Cancel)
                            {
                                _uploadOnExitHandled = false;
                                e.Cancel = true;
                                return;
                            }
                            doUpload = dlg.DialogResult2 == ServiceUploadOnExitDialog.Result.Yes;
                        }
                        uploadClublog = doUpload;
                    }
                }

                if (lotwToUpload != null || uploadEqsl || uploadQrz || uploadClublog)
                {
                    e.Cancel = true;
                    _uploadInFlight = true;
                    UploadAllAndCloseAsync(lotwToUpload, uploadEqsl, uploadQrz, uploadClublog);
                    return;
                }
            }

            DoShutdownCleanup();
        }

        // Timer/socket/event teardown shared by the normal Closing path and the Window_Closed
        // fallback. MUST stay dialog-free and window-free: Window_Closed runs it after the window
        // is already closed, where creating any window with Owner = this throws
        // InvalidOperationException ("Cannot set Owner property to a Window that has been closed").
        // That crash happened when a close proceeded despite e.Cancel (WPF ignores Cancel during
        // Application.Shutdown / Windows session end): the old fallback re-invoked Window_Closing
        // itself, which re-entered the upload-on-exit block and tried to show the LoTW dialog
        // owned by the dead window.
        private void DoShutdownCleanup()
        {
            // Windows still OPEN at shutdown never run their own Closing save - the program tears them
            // down instead - so a window left open lost the position it had been moved to. Persist them
            // here, BEFORE the flush below, so their placement lands in the same write.
            try { WindowBounds.SaveAllOpen(); }
            catch (System.Exception swallowed) { Log.Swallow(swallowed); }
            try { _channelsWindow?.PersistNow(); }   // same exposure for the channel list itself
            catch (System.Exception swallowed) { Log.Swallow(swallowed); }

            // (Nothing is recorded here about My Favorite Channels being open. It reopens only when it is
            // pinned, which is a setting the operator turns on himself and nothing else may write.)

            // Land any debounced settings write still waiting on its timer (window bounds saved
            // moments before exit would otherwise be lost with the timer).
            SettingsFlush.FlushNow();
            _isShutdownCleanupDone = true;

            // Stop all timers before shutdown to prevent pending async operations
            try { if (HeartbeatTimer != null && HeartbeatTimer.IsEnabled) HeartbeatTimer.Stop(); } catch (System.Exception swallowed) { Log.Swallow(swallowed); }
            try { if (UTCTimer != null && UTCTimer.IsEnabled) UTCTimer.Stop(); } catch (System.Exception swallowed) { Log.Swallow(swallowed); }
            try { if (CallsignLookupDebounceTimer != null && CallsignLookupDebounceTimer.IsEnabled) CallsignLookupDebounceTimer.Stop(); } catch (System.Exception swallowed) { Log.Swallow(swallowed); }
            try { VoiceMessageAvailabilityTimer.Tick -= VoiceMessageAvailabilityTimer_Tick; if (VoiceMessageAvailabilityTimer.IsEnabled) VoiceMessageAvailabilityTimer.Stop(); } catch (System.Exception swallowed) { Log.Swallow(swallowed); }
            // Null after disposing so Window_Closed's teardown (which runs after this on a normal
            // close) can tell it's already done and doesn't Stop()/Dispose() a disposed timer.
            try { if (NewDXCCTimer != null) { NewDXCCTimer.Tick -= NewDXCCTimer_Tick; NewDXCCTimer.Stop(); NewDXCCTimer.Dispose(); NewDXCCTimer = null; } } catch (System.Exception swallowed) { Log.Swallow(swallowed); }
            try { if (_mapUpdateDebounceTimer != null) { _mapUpdateDebounceTimer.Stop(); _mapUpdateDebounceTimer = null; } } catch (System.Exception swallowed) { Log.Swallow(swallowed); }

            // Unsubscribe from network availability events
            try { NetworkChange.NetworkAvailabilityChanged -= NetworkChange_NetworkAvailabilityChanged; } catch (System.Exception swallowed) { Log.Swallow(swallowed); }

            // Dispose CallsignUploader to unsubscribe from NetworkChange events
            try { _callsignUploader?.Dispose(); } catch (System.Exception swallowed) { Log.Swallow(swallowed); }

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
            catch (System.Exception swallowed) { Log.Swallow(swallowed); }

            try
            {
                if (N1MMClient != null)
                {
                    N1MMClient.Close();
                    N1MMClient.Dispose();
                    N1MMClient = null;
                }
            }
            catch (System.Exception swallowed) { Log.Swallow(swallowed); }

            try
            {
                if (HolyClusterClient != null)
                {
                    HolyClusterClient.Close();
                    HolyClusterClient.Dispose();
                    HolyClusterClient = null;
                }
            }
            catch (System.Exception swallowed) { Log.Swallow(swallowed); }

            // Close cluster WebSocket
            try { CloseClusterWebSocket(); } catch (System.Exception swallowed) { Log.Swallow(swallowed); }

            // Unsubscribe from MapControl events
            try { MapControl.RadiusChanged -= OnMapRadiusChanged; } catch (System.Exception swallowed) { Log.Swallow(swallowed); }
            try { MapControl.SpotTuneRequested -= OnMapSpotTuneRequested; } catch (System.Exception swallowed) { Log.Swallow(swallowed); }
            try { MapControl.SpotHovered -= OnMapSpotHovered; } catch (System.Exception swallowed) { Log.Swallow(swallowed); }
            try { MapControl.SpotHoverEnded -= OnMapSpotHoverEnded; } catch (System.Exception swallowed) { Log.Swallow(swallowed); }
        }

        private void Window_Closed(object sender, EventArgs e)
        {
            if (!_isShutdownCleanupDone)
            {
                // Cleanup only — never Window_Closing, whose upload-on-exit block shows dialogs
                // with Owner = this and throws now that the window is closed (see DoShutdownCleanup).
                DoShutdownCleanup();
            }

            // Unsubscribe from event handlers to prevent memory leaks
            try { this.Loaded -= MainWindow_Loaded; } catch (System.Exception swallowed) { Log.Swallow(swallowed); }
            try { Properties.Settings.Default.PropertyChanged -= Settings_PropertyChanged; } catch (System.Exception swallowed) { Log.Swallow(swallowed); }

            if (AdifHandlerWorker != null)
            {
                try { AdifHandlerWorker.DoWork -= AdifHandlerWorker_DoWork; } catch (System.Exception swallowed) { Log.Swallow(swallowed); }
                try { AdifHandlerWorker.ProgressChanged -= AdifHandlerWorker_ProgressChanged; } catch (System.Exception swallowed) { Log.Swallow(swallowed); }
                try { AdifHandlerWorker.RunWorkerCompleted -= AdifHandlerWorker_RunWorkerCompleted; } catch (System.Exception swallowed) { Log.Swallow(swallowed); }
            }

            UTCTimer.Tick -= UTCTimer_Elapsed;
            if (OmniRigEngine != null)
            {
                OmniRigEngine.StatusChange -= OmniRigEngine_StatusChange;
                OmniRigEngine.ParamsChange -= OmniRigEngine_ParamsChange;
                Rig = null;
                OmniRigEngine = null;
            }
            // DoShutdownCleanup normally disposed and nulled this already; only tear it down here
            // if that path was somehow skipped (Stop() on a disposed WinForms timer throws).
            if (NewDXCCTimer != null)
            {
                try { NewDXCCTimer.Tick -= NewDXCCTimer_Tick; NewDXCCTimer.Stop(); NewDXCCTimer.Dispose(); NewDXCCTimer = null; } catch (System.Exception swallowed) { Log.Swallow(swallowed); }
            }
            Properties.Settings.Default.SignBoardWindowIsOpen = Application.Current.Windows.Cast<Window>().SingleOrDefault(w => w == signboard) != null;
            Properties.Settings.Default.MatrixWindowIsOpen = Application.Current.Windows.Cast<Window>().SingleOrDefault(w => w == matrix) != null;
            Properties.Settings.Default.TimerWindowIsOpen = Application.Current.Windows.Cast<Window>().SingleOrDefault(w => w == timerscreen) != null;
            // Where every column sits AND how wide it is - the widths can only be read now, since dragging
            // a column divider raises no event we listen for. See SaveLogColumnLayout.
            SaveLogColumnLayout();
            try { Properties.Settings.Default.Save(); } catch (System.Exception swallowed) { Log.Swallow(swallowed); }
            try { MapControl?.DisposeBrowser(); } catch (System.Exception swallowed) { Log.Swallow(swallowed); }
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

            UpdateBandWarningIcon();

            UpdateClusterFrequencyHighlight();
            UpdateFrequencyDisplay();

            // A frequency arriving or going away is what decides whether the band is read out or picked.
            // AFTER UpdateFrequencyDisplay, never before: that is what copies the new frequency into the
            // visible no-CAT box, and the visible box is what "is there a frequency" now reads. Asked
            // first, it still saw the empty box a moment earlier and left the picker up over a frequency
            // that had just been filled in.
            UpdateBandPickAvailability();
        }

        // Show a red attention glyph over the band box when the operator has entered a frequency that maps
        // to no amateur band (TB_Band is then blank). Only while the HAM-frequency check is on, and never
        // for an empty frequency box — that is "not entered yet", not "wrong". Kept in step wherever the
        // frequency changes (typing, or a CAT update that writes TB_Frequency).
        private void UpdateBandWarningIcon()
        {
            if (BandWarningOverlay == null) return;
            bool wrongBand = Properties.Settings.Default.ValidateHamFrequency
                             && !FrequencyIsEmpty
                             && string.IsNullOrWhiteSpace(HolyLogParser.convertFreqToBand(TB_Frequency.Text));
            BandWarningOverlay.Visibility = wrongBand ? Visibility.Visible : Visibility.Collapsed;
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

            // The LED display is only valid when the frequency comes live from the rig — i.e. CAT is
            // enabled AND the selected rig is online. Whenever it isn't (CAT disabled, no rig defined,
            // the rig not online, or MANUAL MODE) there is no radio frequency, so switch to the
            // white/red no-CAT box where the operator types it.
            //
            // Manual mode is included deliberately: the rig may well be online, but its frequency is
            // ignored while manual, so showing the lit LED implied a live reading that was not being
            // updated. Same situation for the operator as CAT being off, so it must look the same.
            bool catEnabled = Properties.Settings.Default.EnableOmniRigCAT;
            bool manualMode = Properties.Settings.Default.isManualMode;
            bool rigOnline = catEnabled && OmniRigEngine != null && Rig != null
                             && Rig.Status == OmniRig.RigStatusX.ST_ONLINE;
            if (manualMode || !rigOnline)
            {
                // Only initialise the no-CAT box when first switching to that mode so we don't
                // overwrite text the user is actively typing.
                if (FreqNoCatBezel != null && FreqNoCatBezel.Visibility != Visibility.Visible)
                    ShowLedNoCat();
                // Already showing: reflect a programmatic frequency change (e.g. tuning to a
                // cluster/map spot) so the visible box follows TB_Frequency — but never while the
                // user is typing in it.
                else if (TB_FreqNoCat != null && !TB_FreqNoCat.IsFocused)
                    FillFreqNoCatFromFrequency();
                return;
            }

            ShowLedActive();   // ensure LED bezel is visible when CAT is working

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
            FreqLedGhost.Text = "8888888.888";
            FreqLedLive.Inlines.Clear();
            FreqLedLive.Inlines.Add(new System.Windows.Documents.Run("-------.---") { Foreground = LedAmberBrush });
        }

        // No CAT / rig offline — switch to a plain editable textbox with a red border.
        private void ShowLedNoCat()
        {
            if (FreqLedBezel == null || FreqNoCatBezel == null) return;
            FreqLedBezel.Visibility = Visibility.Hidden;
            FreqNoCatBezel.Visibility = Visibility.Visible;
            FillFreqNoCatFromFrequency();   // pre-fill with any stored frequency
        }

        // Render TB_Frequency (MHz) into the no-CAT editable box as kHz with 3 decimals,
        // e.g. 21.278520 -> "21278.520". Empty/invalid clears it. Callers must not invoke this
        // while the box has focus, or they'll overwrite what the user is typing.
        private void FillFreqNoCatFromFrequency()
        {
            if (TB_FreqNoCat == null) return;
            string raw = (TB_Frequency.Text ?? string.Empty).Trim();
            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double mhz) && mhz > 0)
            {
                long hz = (long)Math.Round(mhz * 1000000.0);
                TB_FreqNoCat.Text = (hz / 1000).ToString(CultureInfo.InvariantCulture)
                                    + "." + (hz % 1000).ToString("D3", CultureInfo.InvariantCulture);
            }
            else
            {
                TB_FreqNoCat.Text = string.Empty;
            }
        }

        // CAT / rig back online — switch back to the LED display.
        private void ShowLedActive()
        {
            if (FreqNoCatBezel == null || FreqLedBezel == null) return;
            // Do NOT CommitFreqNoCat here: the rig's live frequency takes priority over whatever
            // the operator may have typed while offline. The rig will report its frequency via
            // ShowRigParams, which sets TB_Frequency directly.
            FreqNoCatBezel.Visibility = Visibility.Hidden;
            FreqLedBezel.Visibility = Visibility.Visible;
        }

        private void CommitFreqNoCat()
        {
            string txt = (TB_FreqNoCat?.Text ?? string.Empty).Trim();
            if (txt.Length == 0)
            {
                // Emptying the box is an answer, not a mistake: it says there is no frequency for this
                // QSO, which is how the Band box becomes a picker. Anything else that will not parse is
                // still ignored - a half-typed number must not wipe a good frequency.
                SetWorkingFrequency(string.Empty);
                _freqNoCatBeforeEdit = string.Empty;   // "no frequency" is now the committed state
                return;
            }
            if (double.TryParse(txt, System.Globalization.NumberStyles.Float,
                                 System.Globalization.CultureInfo.InvariantCulture, out double kHz) && kHz > 0)
            {
                double mhz = kHz / 1000.0;
                SetWorkingFrequency(mhz.ToString("0.0#####", System.Globalization.CultureInfo.InvariantCulture));
                long hz = (long)Math.Round(mhz * 1000000.0);
                TB_FreqNoCat.Text = (hz / 1000).ToString(System.Globalization.CultureInfo.InvariantCulture)
                                    + "." + (hz % 1000).ToString("D3", System.Globalization.CultureInfo.InvariantCulture);
            }

            // Committed: THIS is now what the box held before any further editing, so Escape after
            // pressing Enter goes back to what was just entered rather than to something older.
            _freqNoCatBeforeEdit = TB_FreqNoCat.Text;
        }

        private void TB_FreqNoCat_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { CommitFreqNoCat(); e.Handled = true; }
            else if (e.Key == Key.Escape) { RestoreFreqNoCat(); e.Handled = true; }
        }

        // What was in the box when the operator started editing it, so Escape can put it back.
        private string _freqNoCatBeforeEdit;

        private void TB_FreqNoCat_GotFocus(object sender, RoutedEventArgs e)
        {
            _freqNoCatBeforeEdit = TB_FreqNoCat == null ? null : TB_FreqNoCat.Text;
        }

        // Escape means "forget what I just did to this box" - typed over, or deleted altogether.
        //
        // It used to call UpdateFreqLed, which restores the box from the stored frequency but refuses to
        // touch it while it has focus - and pressing Escape IN the box means it always does, so Escape
        // did nothing whatsoever. Restoring from a snapshot also survives the deletion having already
        // cleared the stored frequency, which is what makes the Band box a picker: by then there is
        // nothing left to restore FROM.
        private void RestoreFreqNoCat()
        {
            if (TB_FreqNoCat == null) return;

            TB_FreqNoCat.Text = _freqNoCatBeforeEdit ?? string.Empty;
            TB_FreqNoCat.CaretIndex = TB_FreqNoCat.Text.Length;
            // Puts the frequency, the band and the read-out/picker back with it. An empty snapshot goes
            // through the same path and leaves everything cleared, which is equally "as it was".
            CommitFreqNoCat();
        }

        private void TB_FreqNoCat_LostFocus(object sender, RoutedEventArgs e)
        {
            CommitFreqNoCat();
        }

        // Clearing the box counts the moment the last character goes, without waiting for Enter or for
        // the focus to leave. An empty frequency is what turns the Band box into its drop-down, and
        // there is nothing to press Enter ON when the box is empty - so the operator deleted the
        // frequency and the form went on showing a band it no longer had any basis for.
        //
        // Only the empty case. A part-typed number is still left for CommitFreqNoCat: "14" on its way to
        // "14200" would otherwise be taken as 14 kHz and drag the band along on every keystroke.
        private void TB_FreqNoCat_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (TB_FreqNoCat == null || TB_Frequency == null) return;

            if ((TB_FreqNoCat.Text ?? string.Empty).Trim().Length == 0)
            {
                if ((TB_Frequency.Text ?? string.Empty).Length != 0)
                    SetWorkingFrequency(string.Empty);

                // The band was READ OUT of the frequency that has just been deleted, so it goes with it -
                // cleared here directly rather than left to the stored frequency's own change event. A
                // band left behind is not just stale: the picker then opens with that band already
                // selected, and choosing it again is no change at all, so the list closes having done
                // nothing at all.
                if (!string.IsNullOrEmpty(TB_Band.Text))
                    TB_Band.Text = string.Empty;
            }

            // Every keystroke in this box, in both directions: it is the box that decides whether Band is
            // a read-out or a picker, and relying on the stored frequency's own change event to carry the
            // news left the two out of step.
            UpdateBandPickAvailability();
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
            if (!RequireActiveLog("search")) return;
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
            // NO OWNER, DELIBERATELY. An owned window is pinned above its owner for ever: the Workshop
            // sat over the main window and clicking the main window could not bring it forward. It is
            // not a dialog - it is a second place to work, open for as long as the operator wants - so
            // it is a window in its own right, orderable like any other, with its own taskbar button
            // and its own minimise.
            //
            // Nothing is left running by this: the app is ShutdownMode="OnMainWindowClose", and the
            // main window closes this one explicitly on its way out.
            searchWindow = new SearchWindow(Qsos, SafeActiveLogName());
            searchWindow.Closed += (s, _) => searchWindow = null;
            searchWindow.Show();
            if (!string.IsNullOrWhiteSpace(presetCallsign))
                searchWindow.SetCallsign(presetCallsign, runSearch: true);
        }

        // Opens the Search window filtered by a country and runs the search — used from the
        // Statistics window's worked-countries list.
        private void OpenSearchWindowForCountry(string country)
        {
            if (string.IsNullOrWhiteSpace(country)) return;
            if (searchWindow != null && searchWindow.IsLoaded)
            {
                searchWindow.SetCountry(country, runSearch: true);
                searchWindow.Activate();
                return;
            }
            searchWindow = new SearchWindow(Qsos, SafeActiveLogName());   // no owner - see OpenSearchWindow
            searchWindow.Closed += (s, _) => searchWindow = null;
            searchWindow.Show();
            searchWindow.SetCountry(country, runSearch: true);
        }

        private void StatisticsMenuItem_Click(object sender, RoutedEventArgs e)
        {
            // Statistics are a log's statistics, and confirmations are fetched INTO a log. With none
            // open the window would offer to check LoTW for a log that is not there.
            if (!RequireActiveLog("show statistics for")) return;
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
            statisticsWindow.CountrySearchRequested += OpenSearchWindowForCountry;
            statisticsWindow.QsoSubsetRequested += OpenWorkshopForSubset;

            // A subset Workshop opened from a figure in this window is OWNED by it, so that opening it
            // does not throw the main GUI in front (see OpenWorkshopForSubset). An owned window is
            // closed with its owner, and that part is wrong here: the Workshop is where QSOs get
            // edited, uploaded and deleted, and closing a window of numbers must not take it away
            // mid-job. So hand it to the main window on the way out - it stays open, and from then on
            // behaves like any other Workshop.
            statisticsWindow.Closing += (s, _) =>
            {
                if (subsetWorkshop != null && subsetWorkshop.IsLoaded
                    && ReferenceEquals(subsetWorkshop.Owner, s))
                    subsetWorkshop.Owner = this;
            };
            statisticsWindow.Closed += (s, _) => statisticsWindow = null;
            statisticsWindow.Show();
        }

        // A Log Workshop over a SLICE of the log - the QSOs behind a figure the Statistics window shows,
        // e.g. the deleted DXCC entities behind its "N deleted" link.
        //
        // A SECOND Workshop, not the main one: that one is the operator's view of the whole log, and
        // quietly repointing it at seven QSOs would take their working window away from them. This one is
        // single-instance in its own right, so asking twice re-uses it rather than stacking windows, and
        // its caption names the slice so a small count cannot be mistaken for a shrunken log.
        private SearchWindow subsetWorkshop;

        private void OpenWorkshopForSubset(ObservableCollection<QSO> qsos, string what)
        {
            if (qsos == null || qsos.Count == 0) return;
            string label = (what ?? "selected QSOs") + " — " + SafeActiveLogName();

            // OWNED BY THE WINDOW THE OPERATOR CAME FROM, not by the main window. Windows raises the
            // whole owner chain when an owned window opens, so owning this from the main window threw
            // the ENTIRE main GUI in front of the Statistics window on the way - the operator clicked
            // "3 deleted" and watched the log they were not looking at come up first. Owned from the
            // Statistics window, the only window raised is the one already on screen.
            //
            // Ownership by Statistics would normally mean this window closes with it. It must not:
            // OpenStatisticsWindow hands it to the main window as Statistics closes, so it survives.
            Window owner = (statisticsWindow != null && statisticsWindow.IsLoaded)
                ? (Window)statisticsWindow
                : this;
            try
            {
                if (subsetWorkshop != null && subsetWorkshop.IsLoaded)
                {
                    subsetWorkshop.SetTitleLog(label);
                    subsetWorkshop.ReplaceSource(qsos);
                    subsetWorkshop.Activate();
                    return;
                }
                subsetWorkshop = new SearchWindow(qsos, label) { Owner = owner };
                subsetWorkshop.Closed += (s, _) => subsetWorkshop = null;
                subsetWorkshop.Show();
            }
            catch (System.Exception ex)
            {
                HolyMessageBox.ShowError("Could not open the Log Workshop for those QSOs.\n\n" + ex.Message,
                                         "Log Workshop", this);
            }
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

        // Open Options on the General page — where the app-wide Sounds settings live. Used by the
        // "Sounds" link in the Cluster Settings window so the operator can jump straight there.
        internal void OpenOptionsOnGeneralPage()
        {
            try
            {
                OptionsMenuItemMenuItem_Click(null, null);
                if (options != null)
                {
                    options.GeneralItem.IsSelected = true;
                    options.Activate();
                    // Put the caret on the audio-device picker so it doesn't have to be hunted for.
                    options.GeneralSettingsControlControlInstance?.FocusSoundDevicePicker();
                }
            }
            catch (System.Exception swallowed) { Log.Swallow(swallowed); }
        }

        // Soft HAM-frequency guard on Add (F1). Returns true to let the save proceed, false to stop it.
        // Does nothing unless "Validate for HAM frequency" (Options > General) is ticked; a real amateur
        // band always passes. Otherwise it warns — Yes saves anyway, No stops, and the "here" link stops
        // and opens the setting so the check can be switched off. Deliberately mode-agnostic: it fires in
        // Manual and in CAT alike, because an out-of-band frequency is wrong however it got there.
        private bool ConfirmHamFrequencyBeforeSave()
        {
            if (!Properties.Settings.Default.ValidateHamFrequency) return true;

            string band = FrequencyIsEmpty
                ? string.Empty
                : HolyLogParser.convertFreqToBand(TB_Frequency.Text);
            if (!string.IsNullOrWhiteSpace(band)) return true;   // a real amateur band -> nothing to warn

            var dlg = new HamFreqWarningWindow(FrequencyInKhzText()) { Owner = this };
            dlg.ShowDialog();

            if (dlg.OpenSettingsRequested)
            {
                OpenOptionsOnHamFreqValidation();
                return false;   // don't save; the operator went to change the setting
            }
            return dlg.SaveAnyway;
        }

        // The entered frequency, in kHz, for the warning. The visible no-CAT box already shows kHz (its
        // "kHz" label sits right beside it), so use exactly what the operator typed — verbatim, no
        // conversion. That is the box in play whenever this warning fires (Manual / CAT off/offline). Only
        // if it is not the active box (e.g. live CAT) fall back to the stored value, which is MHz, x1000.
        private string FrequencyInKhzText()
        {
            bool noCatBoxShowing = FreqNoCatBezel != null && FreqNoCatBezel.Visibility == Visibility.Visible;
            if (noCatBoxShowing)
            {
                string typed = (TB_FreqNoCat?.Text ?? string.Empty).Trim();
                if (typed.Length > 0) return typed;
            }

            string raw = (TB_Frequency.Text ?? string.Empty).Trim();
            if (raw.Length == 0) return string.Empty;
            if (!double.TryParse(raw, System.Globalization.NumberStyles.Float,
                                 System.Globalization.CultureInfo.InvariantCulture, out double mhz))
                return raw;
            return (mhz * 1000.0).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        }

        // Open Options on the General page with focus + the mouse pointer parked on the
        // "Validate for HAM frequency" checkbox. Reached from the "here" link in the warning above.
        internal void OpenOptionsOnHamFreqValidation()
        {
            try
            {
                OptionsMenuItemMenuItem_Click(null, null);
                if (options != null)
                {
                    options.GeneralItem.IsSelected = true;
                    options.Activate();
                    options.GeneralSettingsControlControlInstance?.FocusHamFrequencyValidation();
                }
            }
            catch (System.Exception swallowed) { Log.Swallow(swallowed); }
        }

        // ===== "My" menu: open each online service's personal area in the browser =====
        // We deliberately do NOT submit the operator's credentials to the site's web login (fragile and a
        // security risk). We just open the personal-area URL; the site authenticates via the browser
        // session or its own login page. The stored username/password only decide whether the service is
        // configured yet -- if not, we offer to jump straight to its settings section.

        // Re-loads the active log's QSOs from the database.
        //
        // Needed after something writes to the QSOs behind the grid's back - the LoTW confirmation
        // marking does exactly that, updating rows in the database while the grid holds QSO objects
        // read when the log was opened. Without this the ticks appear only after switching logs or
        // restarting, which looks like the download did nothing.
        public void ReloadActiveLogQsos()
        {
            try
            {
                if (dal == null) return;

                // Qsos is a plain FIELD, and the grid binds to the window's DataContext - so assigning
                // Qsos on its own changes nothing on screen. The three existing reload paths all follow
                // the load with the CollectionChanged hook and DataContext assignment below; leaving
                // those out was why the LoTW ticks only appeared after a restart.
                Qsos = dal.GetQSOsForLog(dal.ActiveLogId);
                Qsos.CollectionChanged += Qsos_CollectionChanged;

                // Drop any active callsign filter: its FilteredQsos holds the OLD QSO objects, and
                // leaving it in place would keep showing them.
                FilteredQsos = null;
                _foreignFilterRows = null;

                DataContext = Qsos;
                LastQSO = Qsos.FirstOrDefault();

                // An open Search window holds the PREVIOUS collection, whose objects never received the
                // confirmation marks (the marking updated the database, not the in-memory QSOs). Point
                // it at the freshly-loaded collection so it updates without being reopened.
                if (searchWindow != null && searchWindow.IsLoaded)
                    searchWindow.ReplaceSource(Qsos);

                // AND THE FIGURES ALONG THE FOOT OF THE WINDOW. This method is called when the log has
                // CHANGED underneath the grid, and the QSO / squares / countries counts are made from
                // exactly that. Without this, restoring 147 contacts from LoTW filled the table and left
                // the status bar still reporting the count from before them.
                UpdateNumOfQSOs();
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // Re-reads the log grid's rows so the LoTW callsign tint appears or disappears at once when the
        // option is toggled. Also refreshes any open Search window, which shows the same mark.
        public void RefreshLogTableMarks()
        {
            try { QSODataGrid?.Items.Refresh(); }
            catch (Exception swallowed) { Log.Swallow(swallowed); }

            foreach (var window in Application.Current.Windows.OfType<SearchWindow>())
            {
                try { window.RefreshRows(); }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
            }
        }

        // The active log's name for the Search window's title bar. Never throws: a search that opens
        // with a blank name is a nuisance, a search that fails to open because the name lookup hiccuped
        // is a bug.
        private string SafeActiveLogName()
        {
            try { return dal?.GetLogName(dal.ActiveLogId); }
            catch (Exception swallowed) { Log.Swallow(swallowed); return null; }
        }

        private enum OnlineLogger { Lotw, Eqsl, Qrz, Clublog }

        private void MyLotwMenuItem_Click(object sender, RoutedEventArgs e)    => OpenLoggerPersonalArea(OnlineLogger.Lotw);
        private void MyEqslMenuItem_Click(object sender, RoutedEventArgs e)    => OpenLoggerPersonalArea(OnlineLogger.Eqsl);
        private void MyQrzMenuItem_Click(object sender, RoutedEventArgs e)     => OpenLoggerPersonalArea(OnlineLogger.Qrz);
        private void MyClublogMenuItem_Click(object sender, RoutedEventArgs e) => OpenLoggerPersonalArea(OnlineLogger.Clublog);

        private ChannelsWindow _channelsWindow;

        // Opening from the menu ALWAYS starts unpinned. Pinning is a deliberate act that means "bring
        // this back next time"; it must never be inherited just because the window was opened again.
        // Only the pin button itself turns it on, and only the startup path below opens it still pinned.
        private void MyChannelsMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (Properties.Settings.Default.ChannelsWindowPinned)
            {
                Properties.Settings.Default.ChannelsWindowPinned = false;
                try { Properties.Settings.Default.Save(); }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
                // Already open (and pinned): just refresh its pin icon to the new state.
                try { _channelsWindow?.RefreshPinButton(); }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
            }
            ShowChannelsWindow();
        }

        // Opens (or focuses) the window WITHOUT touching the pinned state.
        //
        // "Was it open?" is written the moment it opens and the moment it closes, NOT only at shutdown.
        // Recording it at shutdown alone was wrong in one direction that matters: any exit that does not
        // reach DoShutdownCleanup - a crash, an end-of-session teardown - leaves the last "yes" standing
        // for ever, and the window then opens on every start whatever the operator does with it. Written
        // here it is always the truth about the last thing that actually happened to the window.
        private void ShowChannelsWindow()
        {
            if (_channelsWindow != null)
            {
                try { _channelsWindow.Activate(); return; }
                catch { _channelsWindow = null; }
            }
            _channelsWindow = new ChannelsWindow(this);
            _channelsWindow.Closed += (s, ev) => _channelsWindow = null;
            _channelsWindow.Show();
        }

        private void OpenLoggerPersonalArea(OnlineLogger logger)
        {
            string name, url;
            bool configured;
            switch (logger)
            {
                case OnlineLogger.Lotw:
                    name = "LoTW";
                    url = "https://lotw.arrl.org/lotwuser/default";
                    // Uploads use a TQSL certificate (not the web login), so treat LoTW as configured if
                    // EITHER the web credentials or a TQSL path is set -- don't nag someone who already
                    // uploads to LoTW but never entered web credentials.
                    configured = (!string.IsNullOrWhiteSpace(Properties.Settings.Default.LotwWebUser)
                                  && !string.IsNullOrWhiteSpace(Properties.Settings.Default.LotwWebPassword))
                                 || !string.IsNullOrWhiteSpace(Properties.Settings.Default.LotwTqslPath);
                    break;
                case OnlineLogger.Eqsl:
                    name = "eQSL";
                    url = "https://www.eqsl.cc/qslcard/";
                    // eQSL isn't a single login — it's the per-callsign accounts table (eqsl_accounts).
                    // "Configured" means at least one account row exists.
                    configured = HasEqslAccount();
                    break;
                case OnlineLogger.Qrz:
                    name = "QRZ.com";
                    url = "https://www.qrz.com/";
                    // QRZ can be set up as a lookup login (username/password) or a logbook API key.
                    configured = (!string.IsNullOrWhiteSpace(Properties.Settings.Default.qrz_username)
                                  && !string.IsNullOrWhiteSpace(Properties.Settings.Default.qrz_password))
                                 || !string.IsNullOrWhiteSpace(Properties.Settings.Default.qrz_api_key);
                    break;
                default: // Clublog
                    name = "Club Log";
                    url = "https://clublog.org/";
                    configured = !string.IsNullOrWhiteSpace(Properties.Settings.Default.ClublogEmail)
                                 && !string.IsNullOrWhiteSpace(Properties.Settings.Default.ClublogPassword);
                    break;
            }

            if (configured)
            {
                OpenUrlInBrowser(url);
                return;
            }

            bool goToSettings = HolyMessageBox.ShowConfirm(
                $"Your {name} username and password are not set yet.\n\nWould you like to open the {name} settings to enter them?",
                name + " not configured", HolyMsgType.Info, this);
            if (goToSettings)
                OpenLoggerSettings(logger);
        }

        // eQSL is configured when at least one per-callsign account exists in the eqsl_accounts table.
        private bool HasEqslAccount()
        {
            try { return dal != null && dal.GetEqslAccounts().Count > 0; }
            catch (System.Exception swallowed) { Log.Swallow(swallowed); return false; }
        }

        private void OpenLoggerSettings(OnlineLogger logger)
        {
            OptionsMenuItemMenuItem_Click(null, null);
            if (options == null) return;
            switch (logger)
            {
                case OnlineLogger.Lotw:    options.LotwItem.IsSelected = true;    break;
                case OnlineLogger.Eqsl:    options.EqslItem.IsSelected = true;    break;
                case OnlineLogger.Qrz:     options.QRZItem.IsSelected = true;     break;
                case OnlineLogger.Clublog: options.ClublogItem.IsSelected = true; break;
            }
        }

        private void OpenUrlInBrowser(string url)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (System.Exception ex)
            {
                HolyMessageBox.ShowError("Could not open the browser: " + ex.Message, "Open Link", this);
            }
        }

        // ── settings forced back to default on a new install ──────────────
        //
        // user.config SURVIVES an upgrade, so a setting the operator switched off stays off forever on
        // their machine. A tester reported "the cluster does not show up" when in fact ShowClusterWindowOption
        // had simply been unchecked and was carried forward by the upgrade.
        //
        // ADD A SETTING'S NAME HERE to make it return to its declared default on every newly-installed
        // version. Use the exact name from Settings.settings. Note this triggers on a VERSION CHANGE
        // (user.config is per-version) — reinstalling the SAME version resets nothing, so bump the
        // version for each build handed out.
        private static readonly string[] SettingsForcedToDefaultOnUpgrade =
        {
            "ClusterActive",           // Options > User Interface > Cluster > Active   (default True)
            "ShowClusterWindowOption", // Options > User Interface > Cluster > Visible  (default True)
        };

        // A handful of settings used to be stored as loose .txt files under AppData instead of in
        // Properties.Settings. They have been moved in with the rest so a PROFILE (a snapshot of
        // Properties.Settings) captures them. This imports any old file once, so nobody loses the value
        // they had, then deletes it. Safe to run repeatedly; gated by LegacyFileSettingsMigrated.
        private void MigrateLegacyFileSettings()
        {
            var s = Properties.Settings.Default;
            if (s.LegacyFileSettingsMigrated) return;

            string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HolyLogger");
            string localData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HolyLogger");

            ImportLegacyFile(Path.Combine(appData, "cluster-hover-popup-enabled.txt"), text =>
            { if (bool.TryParse(text, out bool v)) s.ClusterHoverPopupEnabled = v; });

            ImportLegacyFile(Path.Combine(appData, "cluster-last-minutes-filter.txt"), text =>
            { if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)) s.ClusterLastMinutesFilter = v; });

            ImportLegacyFile(Path.Combine(appData, "cluster-country-col-width.txt"), text =>
            { if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) s.ClusterCountryColumnWidth = v; });

            ImportLegacyFile(Path.Combine(appData, "cluster-country-col-display-index.txt"), text =>
            { if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)) s.ClusterCountryColumnDisplayIndex = v; });

            ImportLegacyFile(Path.Combine(appData, "cluster-col-order.txt"), text => { s.ClusterColumnOrder = text; });

            ImportLegacyFile(Path.Combine(localData, "qrz_photo_window_bounds.txt"), text =>
            {
                string[] p = text.Split('|');
                if (p.Length == 4 &&
                    double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double l) &&
                    double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double t) &&
                    double.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double w) &&
                    double.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double h))
                {
                    s.QrzPhotoWindowLeft = l; s.QrzPhotoWindowTop = t;
                    s.QrzPhotoWindowWidth = w; s.QrzPhotoWindowHeight = h;
                }
            });

            s.LegacyFileSettingsMigrated = true;
            try { s.Save(); } catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // Reads one legacy setting file into Properties.Settings, then removes it. Any failure is
        // ignored: the setting simply keeps its default, which is never worse than crashing at startup.
        private static void ImportLegacyFile(string path, Action<string> apply)
        {
            try
            {
                if (!File.Exists(path)) return;
                string text = File.ReadAllText(path).Trim();
                if (!string.IsNullOrWhiteSpace(text)) apply(text);
                File.Delete(path);
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // Puts each named setting back to the DefaultSettingValue declared in Settings.Designer.cs.
        // Unknown names are skipped, so renaming or removing a setting can never crash startup.
        private static void ForceSettingsToDefault(params string[] names)
        {
            var s = Properties.Settings.Default;
            foreach (string name in names)
            {
                try
                {
                    var prop = s.Properties[name];
                    if (prop == null) continue;

                    // DefaultValue comes from the attribute as a string; convert it to the real type.
                    object def = prop.DefaultValue;
                    s[name] = def is string text
                        ? System.ComponentModel.TypeDescriptor.GetConverter(prop.PropertyType)
                                .ConvertFromInvariantString(text)
                        : def;
                }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
            }
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
            options.LotwControlInstance.CurrentCallsign = TB_MyCallsign.Text?.Trim();
            options.LotwControlInstance.LotwQueueChanged += UpdateLotwMenuCount;
            options.EqslServiceControlInstance.EqslQueueChanged += UpdateEqslQueueIndicator;
            options.QRZServicesControlInstance.QrzQueueChanged += UpdateQrzMenuCount;
            options.ClublogServiceControlInstance.ClublogQueueChanged += UpdateClublogMenuCount;

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
            // Mirror any credential the operator just entered/changed to the identity-independent store,
            // so it survives the next version upgrade or reinstall even if user.config does not.
            CredentialStore.Backup();
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
                // The UTC timer always runs now (it also refreshes the QSO Date/Time pickers);
                // UpdateTitleClock() itself shows/hides the clock label per the setting. Not
                // calling StartUTCTimer() again also avoids stacking duplicate Tick handlers.
                UpdateActiveLogTitle();   // keep the "— Log: <name>" suffix; don't reset to the bare title
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

            // Open/close the HolyCluster listener to match the (possibly just-changed) setting/port.
            ApplyHolyClusterListener();

            NetworkFlagItem.Visibility = Properties.Settings.Default.ShowNetworkFlag ? Visibility.Visible : Visibility.Collapsed;
            // Lock via IsReadOnly (not IsEnabled) so the field keeps full opacity — a disabled TextBox
            // dims to ~56%, which washed out the lock-blue background and greyed the text.
            TB_MyCallsign.IsReadOnly = Properties.Settings.Default.isLocked;
            TB_Operator.IsReadOnly = Properties.Settings.Default.isLocked;
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

        // Fills View > Color Scheme with one radio-checked item per scheme in ThemePalette.Schemes,
        // plus the "Customize Colors" editor entry. Schemes are edited in place, so there is no
        // separate "Custom" entry. Adding a scheme to the palette automatically shows up here.
        private void BuildColorSchemeMenu()
        {
            if (ColorSchemeMenuItem == null) return;
            ColorSchemeMenuItem.Items.Clear();

            foreach (var scheme in ThemePalette.Schemes)
                ColorSchemeMenuItem.Items.Add(MakeSchemeItem(scheme.Id, scheme.DisplayName));

            ColorSchemeMenuItem.Items.Add(new Separator());
            var customize = new MenuItem
            {
                Header = "Customize Current Color Scheme",
                ToolTip = "Change individual colors of the scheme you are using now; your changes are saved to that scheme and can be undone with Reset"
            };
            customize.Click += CustomizeColorsItem_Click;
            ColorSchemeMenuItem.Items.Add(customize);
        }

        private MenuItem MakeSchemeItem(string id, string displayName)
        {
            var item = new MenuItem
            {
                Header = displayName,
                IsCheckable = true,
                IsChecked = id == ThemeManager.CurrentSchemeId,
                Tag = id
            };
            item.Click += ColorSchemeItem_Click;
            return item;
        }

        private void ColorSchemeItem_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as MenuItem)?.Tag is string id)
                ThemeManager.Apply(id);
            RefreshColorSchemeChecks();
        }

        // Re-check exactly the active scheme (clicking a checked radio item must not un-check it).
        private void RefreshColorSchemeChecks()
        {
            foreach (var item in ColorSchemeMenuItem.Items.OfType<MenuItem>())
                if (item.Tag is string id)
                    item.IsChecked = id == ThemeManager.CurrentSchemeId;
        }

        private void CustomizeColorsItem_Click(object sender, RoutedEventArgs e)
        {
            var editor = new ColorSchemeEditorWindow { Owner = this };
            editor.ShowDialog();
            // Edits stay on the active scheme; rebuild only to keep the checkmark correct.
            BuildColorSchemeMenu();
        }

        // Re-run code-driven coloring for the new palette. QSO rows are painted in LoadingRow, so a
        // grid refresh re-fires it against the new theme brushes.
        private void OnThemeChanged()
        {
            try { QSODataGrid?.Items.Refresh(); } catch (System.Exception swallowed) { Log.Swallow(swallowed); }
            // Grid bg/fg and the table headers auto-update via resource references; refresh
            // re-evaluates the per-spot colors (DXForeground / RowBackground) which read the
            // palette at getter time.
            try { clusterSpotsGrid?.Items.Refresh(); } catch (System.Exception swallowed) { Log.Swallow(swallowed); }
            try { UpdateEditModeBackground(); } catch (System.Exception swallowed) { Log.Swallow(swallowed); }                    // re-theme the QSO entry fields
            try { UpdateStatus(); } catch (System.Exception swallowed) { Log.Swallow(swallowed); }                                // re-color the CAT/RIG status text (was hard-coded black)
            try { UpdateContestLabelContrast(); } catch (System.Exception swallowed) { Log.Swallow(swallowed); }                  // black/white contest labels per frame brightness
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
            matrix = new MatrixWindow { Owner = this };

            // A saved spot is used only when it is REACHABLE. Clamping a negative Left to 0 - what this
            // used to do - is not a fix but a bug of its own: on a desk whose second monitor sits to the
            // LEFT of the primary, every position on that monitor is negative, so the clamp dragged this
            // window onto the primary screen every single time, away from the program that opened it.
            double mLeft = Properties.Settings.Default.MatrixWindowLeft;
            double mTop  = Properties.Settings.Default.MatrixWindowTop;
            if (IsPositionOnScreen(mLeft, mTop))
            {
                matrix.Left = mLeft;
                matrix.Top  = mTop;
            }
            else
            {
                matrix.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
            matrix.Show();
        }

        // File > Profile Manager
        private void ProfilesMenuItem_Click(object sender, RoutedEventArgs e)
        {
            // Owner, not just a constructor argument: the XAML asks for CenterOwner, and without an
            // Owner WPF quietly falls back to centring on the PRIMARY screen.
            new ProfilesWindow(this) { Owner = this }.ShowDialog();
        }

        private void GenerateNewLogInfoWindow()
        {
            // Placement is handled by WindowBounds inside LogInfoWindow itself. It used to be restored
            // here from LogInfoWindowLeft/Top, but nothing ever WROTE those, so the window never actually
            // remembered where it was put.
            loginfo = new LogInfoWindow();

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

        private StackPanel BuildModeCheckBox(string mode, bool isChecked)
        {
            var modeText = new TextBlock
            {
                Text = mode,
                FontSize = 9,
                FontWeight = FontWeights.Bold,
                // Foreground inherited from the cluster window's themed TextElement.Foreground.
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
                // top -1 (was 2): pulls the checkbox up 3px closer to its label above
                Margin = new Thickness(2, -1, 2, 2),
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
                // 0.5 each side → 1px gap between modes (was 1+1 = 2px); reduced by 1px
                Margin = new Thickness(0.5, 0, 0.5, 0)
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
                // Foreground inherited from the cluster window's themed TextElement.Foreground.
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
                // 1 each side → 2px gap between bands (was 2+2 = 4px); condensed by 2px.
                Margin = new Thickness(1, 0, 1, 0),
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
                Width = 18,
                Height = 18,
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

            // Spot count for this band, shown under the checkbox (dark blue). Updated on every
            // RefreshClusterVisibleSpots (new spot or "Last" change) via UpdateClusterBandSpotCounts.
            var bandSpotCountText = new TextBlock
            {
                Text = "0",
                FontSize = 9,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center
            };
            clusterBandSpotCountTexts[band] = bandSpotCountText;

            // White number inside a red circular "coin" (pill for 2+ digits) under the checkbox.
            var bandSpotCountCoin = new Border
            {
                Background = Brushes.Red,
                CornerRadius = new CornerRadius(8),
                MinWidth = 16,
                Height = 16,
                Padding = new Thickness(3, 0, 3, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 1, 0, 0),
                Child = bandSpotCountText
            };

            bandIndicator.Children.Add(bandText);
            bandIndicator.Children.Add(checkBoxCell);
            bandIndicator.Children.Add(bandSpotCountCoin);

            // A transparent background makes the whole cell a reliable hover target.
            bandIndicator.Background = Brushes.Transparent;

            // Hovering a band momentarily previews it: a circle appears around the checkbox and the
            // cluster table + map show ONLY this band's spots while the mouse is over it; leaving the
            // cell hides the circle and restores whatever was showing before.
            bandIndicator.MouseEnter += (s, e) =>
            {
                hoverCircle.Visibility = Visibility.Visible;
                EnterClusterBandHoverPreview(band);
            };
            bandIndicator.MouseLeave += (s, e) =>
            {
                // Only hide this checkbox's ring. The preview itself ends on the whole-row MouseLeave
                // (see BuildClusterBandSelectorPanel) so moving across checkboxes/gaps keeps it alive.
                hoverCircle.Visibility = Visibility.Hidden;
            };

            // Add right-click handler for color editing
            bandIndicator.MouseRightButtonDown += (s, e) =>
            {
                e.Handled = true;
                EditBandColor(band);
            };

            return bandIndicator;
        }

        private void UpdateActiveBandButtonVisibility()
        {
            if (clusterBandFilterActiveBtn == null)
                return;

            string activeBand = TB_Band != null ? TB_Band.Text : string.Empty;
            bool bandIsValid = !string.IsNullOrWhiteSpace(activeBand);

            // The button and label are ALWAYS visible and always look like the other mode buttons — blue
            // when Active mode is selected, normal key-face otherwise (user 2026-07-12: never hidden and
            // never greyed; they used to vanish whenever the radio was outside a ham band, which read as
            // "the button was deleted").
            clusterBandFilterActiveBtn.Visibility = Visibility.Visible;
            clusterBandFilterActiveBtn.IsEnabled = true;
            clusterBandFilterActiveBtn.Opacity = 1.0;
            if (clusterActiveBandIndicatorText != null)
            {
                clusterActiveBandIndicatorText.Visibility = Visibility.Visible;
                clusterActiveBandIndicatorText.Opacity = 1.0;
            }

            string preferred = Properties.Settings.Default.ClusterPreferredBandMode ?? "PreSelected";
            string current = Properties.Settings.Default.ClusterBandFilterMode ?? "PreSelected";

            if (!bandIsValid)
            {
                // Band invalid: STAY in Active mode (user 2026-07-12). The button keeps its focus, the
                // label shows red "out of band", and the table is empty (the Active filter matches no
                // spot without a valid band) until the radio returns to a band. The old auto-fallback to
                // Selected silently changed the mode and flooded the (Live Scale) view with other bands'
                // spots. Just refresh so the empty view applies.
                if (string.Equals(current, "Active", StringComparison.OrdinalIgnoreCase))
                    RefreshClusterVisibleSpots();
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

        private sealed class ClusterSpotViewItem : INotifyPropertyChanged
        {
            public long UnixTime { get; set; }
            public string TimeUtc { get; set; }

            // The parsed MHz value is cached when FreqText is set: FreqMhz is the Live Scale sort key
            // AND is read per row on every scroll tick, so parsing the string on each access burned
            // CPU exactly where smooth knob-tracking needs it most.
            private string _freqText;
            private double _freqMhz;
            public string FreqText
            {
                get => _freqText;
                set
                {
                    _freqText = value;
                    if (double.TryParse((value ?? string.Empty).Trim(),
                                        System.Globalization.NumberStyles.Float,
                                        System.Globalization.CultureInfo.InvariantCulture, out double v) && v > 0)
                        _freqMhz = v >= 1000 ? v / 1000.0 : v;
                    else
                        _freqMhz = 0;
                }
            }
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
                        return ThemeManager.Brush("AccentBrush"); // readable blue in both themes
                    }

                    return ThemeManager.Brush("TextBrush");
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
                            return ThemeManager.Brush("TextBrush");

                        // Resolve through the same band-color source as the band checkboxes and the
                        // map spot dots (defaults + user customizations, normalized band key) so the
                        // Freq color always matches the band selection checkbox exactly.
                        return GetBandBrush(bandText);
                    }
                    catch
                    {
                        return ThemeManager.Brush("TextBrush");
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

            // Worked, but not yet confirmed on LoTW — drives the cluster's "Unconfirmed" legend counter.
            public bool IsUnconfirmedCountry { get; set; }

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
                    if (IsUnconfirmedCountry)
                        return new SolidColorBrush(Color.FromRgb(0xFF, 0x8C, 0x00)); // amber: worked, not confirmed on LoTW
                    if (IsInLog)
                        return new SolidColorBrush(Color.FromRgb(0x00, 0x7A, 0xCC)); // Bold blue (not too dark)
                    return ThemeManager.Brush("TextBrush"); // normal: theme text (black light / light dark)
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
                    if (IsLotwUser && Properties.Settings.Default.ClusterShowLotw)
                    {
                        return ThemeManager.Brush("RowLotwBg"); // LoTW user: mark only the callsign cell (yellow)
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

            private bool _isLotwUser;
            // True when this spot's DX callsign is a Logbook of The World (LoTW) uploader, so the DX
            // callsign cell gets a yellow background. Set once when the spot is built (see the spot list).
            public bool IsLotwUser
            {
                get => _isLotwUser;
                set
                {
                    if (_isLotwUser != value)
                    {
                        _isLotwUser = value;
                        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLotwUser)));
                        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DXBackground)));
                    }
                }
            }

            public Brush RowBackground
            {
                get
                {
                    if (IsMapHovered)
                    {
                        return ThemeManager.Brush("RowHoverBg"); // blue highlight (map hover), theme-aware
                    }
                    if (IsOnFrequency)
                    {
                        return ThemeManager.Brush("RowOnFreqBg"); // on-frequency green, theme-aware
                    }
                    else
                    {
                        return Brushes.Transparent; // normal: shows the grid's themed background
                    }
                }
            }

            // Numeric frequency in MHz, parsed from FreqText (the cluster sends kHz when the value is
            // >= 1000, otherwise MHz). Used by the Live Scale feature to sort the list and to position the
            // current-frequency scale. 0 when FreqText can't be parsed.
            public double FreqMhz => _freqMhz;   // cached by the FreqText setter

            public event PropertyChangedEventHandler PropertyChanged;
        }

        // TWO LOOKUPS OVER THE WHOLE LOG, BUILT ONCE INSTEAD OF OVER AND OVER
        //
        // The cluster asks "is this callsign already in my log?" for every spot on screen, and the
        // duplicate check asks "which QSOs are with this station?" for every character typed into the
        // callsign box. Both used to walk all 28,454 QSOs each time they were asked: measured here at
        // 3.96 ms per cluster refresh and 0.97 ms per keystroke, on a fast machine. Three to five times
        // that on the weak laptop this work is for, and the cluster refreshes on every payload, every
        // band button, every mode button and every move of the VFO.
        //
        // Caching was refused before, and the reason recorded in UpdateDup was a good one: a QSO edit
        // changes the object in place and raises no collection event, so a stale cache could call a
        // contact a duplicate when it is not - worse than slow, mid-contest.
        //
        // What makes it safe now is that every edit in the program funnels through DataAccess, which
        // counts the writes that can add a QSO, remove one, or change its callsigns. THREE things are
        // checked before a cached answer is used: the collection is the same object, its count is
        // unchanged, and nothing has been written since. Any one of them differing rebuilds both.
        private HashSet<string> _loggedDxCalls;
        private Dictionary<string, List<QSO>> _qsosByStation;
        private ObservableCollection<QSO> _lookupsBuiltFrom;
        private int _lookupsBuiltAtCount = -1;
        private long _lookupsBuiltAtVersion = -1;

        // Handed out when a station has never been worked. Shared and never added to - UpdateDupCore
        // only reads it.
        private static readonly List<QSO> _noQsosWithStation = new List<QSO>();

        // Joins the two callsigns into one key. A control character, so it cannot occur inside either
        // one and no pair of callsigns can be mistaken for a different pair.
        private const string StationKeySeparator = "\u0001";

        private void EnsureLogLookups()
        {
            var qsos = Qsos;
            int count = qsos == null ? 0 : qsos.Count;
            long version = DataAccess.ContentVersion;

            if (_loggedDxCalls != null
                && ReferenceEquals(_lookupsBuiltFrom, qsos)
                && _lookupsBuiltAtCount == count
                && _lookupsBuiltAtVersion == version)
                return;

            var calls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // Ordinal and case-sensitive, because that is what the walk it replaces used: string ==
            // in C# is an ordinal compare, and answering a different set of QSOs than the old code did
            // is exactly the kind of change nobody would notice until a contest.
            var byStation = new Dictionary<string, List<QSO>>(StringComparer.Ordinal);

            if (qsos != null)
            {
                foreach (var q in qsos)
                {
                    string c = (q.DXCall ?? string.Empty).Trim();
                    if (c.Length > 0)
                        calls.Add(c);

                    // A QSO with no callsign at all is left out of the station index on purpose: the
                    // old comparison was against the text of a box, which is never null, so null could
                    // never match. Keying it would have made an empty callsign box find these.
                    if (q.MyCall == null || q.DXCall == null)
                        continue;

                    string key = q.MyCall + StationKeySeparator + q.DXCall;
                    List<QSO> withStation;
                    if (!byStation.TryGetValue(key, out withStation))
                    {
                        withStation = new List<QSO>();
                        byStation[key] = withStation;
                    }
                    withStation.Add(q);
                }
            }

            _loggedDxCalls = calls;
            _qsosByStation = byStation;
            _lookupsBuiltFrom = qsos;
            _lookupsBuiltAtCount = count;
            _lookupsBuiltAtVersion = version;
        }

        // A case-insensitive set of all DX callsigns currently in the log, so cluster spot processing
        // can test "is this call already logged?" in O(1) instead of scanning the whole log per spot.
        // The callers only ask it questions; nothing adds to it.
        private HashSet<string> BuildLoggedDxCallSet()
        {
            EnsureLogLookups();
            return _loggedDxCalls;
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
                    // Each QSO is resolved on its own date: a prefix that no longer exists (4N = Serbia)
                    // still counts as that country worked.
                    var dxcc = CountryLookup.Shared.Resolve(qso.DXCall.Trim(), CountryLookup.QsoDate(qso.Date));
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

            var dxcc = CountryLookup.Shared.Resolve(dxCallsign.Trim());
            if (dxcc == null || string.IsNullOrWhiteSpace(dxcc.Entity) || dxcc.Entity == "-1")
            {
                return false;
            }

            return !workedCountries.Contains(dxcc.Entity);
        }

        // Worked but NOT confirmed on LoTW. Worked is keyed by entity prefix (dxcc.Entity); the LoTW
        // confirmed cache is keyed by entity NAME (dxcc.Name), so we bridge via the resolved DXCC.
        // Returns false when there's no LoTW data yet (empty set) — the feature stays inert until the
        // user runs Check LoTW confirmations in Statistics.
        private bool IsUnconfirmedCountry(string dxCallsign, HashSet<string> workedCountries, HashSet<string> confirmedNames)
        {
            if (confirmedNames == null || confirmedNames.Count == 0) return false;
            if (string.IsNullOrWhiteSpace(dxCallsign) || workedCountries == null) return false;

            var dxcc = CountryLookup.Shared.Resolve(dxCallsign.Trim());
            if (dxcc == null || string.IsNullOrWhiteSpace(dxcc.Entity) || dxcc.Entity == "-1") return false;

            if (!workedCountries.Contains(dxcc.Entity)) return false;   // not worked -> that's a New Country, not unconfirmed
            return !string.IsNullOrWhiteSpace(dxcc.Name) && !confirmedNames.Contains(dxcc.Name);
        }

        // The LoTW-confirmed entity NAMES, from the Statistics download cache (Settings.LotwConfirmedEntities).
        private HashSet<string> GetClusterConfirmedEntities()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string cached = Properties.Settings.Default.LotwConfirmedEntities;
            if (!string.IsNullOrWhiteSpace(cached))
                foreach (var n in cached.Split('|'))
                    if (!string.IsNullOrWhiteSpace(n)) set.Add(n.Trim());
            return set;
        }

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
            catch (System.Exception swallowed) { Log.Swallow(swallowed); }
            _bandColorCache = colors;
            return colors;
        }

        // Resolves a raw band string (e.g. "40", "40M", "70CM") to its color, normalizing it to the
        // same key the band checkboxes use so the colors match exactly. Internal (not private) so
        // BandColorConverter, below, can reuse this single source of truth for the QSO log grid's
        // Band/Frequency columns instead of duplicating the lookup.
        internal static string GetBandColor(string band)
        {
            var colors = GetBandColors();
            string key = NormalizeClusterBandKey(band);
            if (!string.IsNullOrEmpty(key) && colors.TryGetValue(key, out string c)) return c;
            return "#FF6600";
        }

        // Frozen-brush cache in front of GetBandColor. The cluster grid's FreqForeground and the
        // QSO grid's Band/Frequency cells resolve a brush per cell on every refresh; allocating a
        // BrushConverter + SolidColorBrush each time added constant GC churn. Keyed by hex (a few
        // distinct values), invalidated together with _bandColorCache when the user edits a color.
        private static Dictionary<string, SolidColorBrush> _bandBrushCache = new Dictionary<string, SolidColorBrush>(StringComparer.OrdinalIgnoreCase);

        internal static SolidColorBrush GetBandBrush(string band)
        {
            string hex = GetBandColor(band);
            lock (_bandBrushCache)
            {
                if (!_bandBrushCache.TryGetValue(hex, out SolidColorBrush b))
                {
                    b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
                    b.Freeze();   // frozen => shareable across the UI thread and render thread
                    _bandBrushCache[hex] = b;
                }
                return b;
            }
        }

        private void SaveBandColors(Dictionary<string, string> colors)
        {
            try
            {
                Properties.Settings.Default.ClusterBandColors = Newtonsoft.Json.JsonConvert.SerializeObject(colors);
                Properties.Settings.Default.Save();
            }
            catch (System.Exception swallowed) { Log.Swallow(swallowed); }
            _bandColorCache = null;
            lock (_bandBrushCache) _bandBrushCache.Clear();
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
            UpdateBandTextBoxColor();

            // Repaint everything already on screen with the new color instead of waiting for the
            // next spot to arrive: the cluster list's Freq color (FreqForeground is re-evaluated on
            // refresh) and the map spot dots.
            try { clusterSpotsDataGrid?.Items.Refresh(); } catch (System.Exception swallowed) { Log.Swallow(swallowed); }
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

        private void StartNewCountryBlink()
        {
            if (_clusterNewCountryBlinkTimer == null)
            {
                _clusterNewCountryBlinkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                _clusterNewCountryBlinkTimer.Tick += (s, e) =>
                {
                    if (clusterNewCountryCountText == null || DateTime.UtcNow >= _clusterNewCountryBlinkStopTime)
                    {
                        _clusterNewCountryBlinkTimer.Stop();
                        if (clusterNewCountryCountText != null)
                            clusterNewCountryCountText.Opacity = 1.0;
                        return;
                    }
                    _clusterNewCountryBlinkOn = !_clusterNewCountryBlinkOn;
                    clusterNewCountryCountText.Opacity = _clusterNewCountryBlinkOn ? 1.0 : 0.0;
                };
            }
            _clusterNewCountryBlinkStopTime = DateTime.UtcNow.AddSeconds(10);
            _clusterNewCountryBlinkOn = true;
            if (clusterNewCountryCountText != null)
                clusterNewCountryCountText.Opacity = 1.0;
            _clusterNewCountryBlinkTimer.Stop();
            _clusterNewCountryBlinkTimer.Start();
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
                catch (System.Exception swallowed) { Log.Swallow(swallowed); }

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
            catch (System.Exception swallowed) { Log.Swallow(swallowed); }
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

        // File -> Backups & Restore: shows the restore instructions in-app (the same text as
        // HOW TO RESTORE.txt) with a button to open the daily-backups folder, so the user sees
        // exactly what to do without having to hunt for and open the text file.
        private void BackupRestoreMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var win = new BackupRestoreWindow(dal.BackupsFolder) { Owner = this };
                win.ShowDialog();
            }
            catch (Exception ex)
            {
                Log.Swallow(ex);
                HolyMessageBox.ShowError("Could not open Backups & Restore:\n" + ex.Message, "Backups & Restore", this);
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
            about = new AboutWindow(callsignListVersion);
            about.Show();
        }

        // HELP > SUPPORT > SEND MAIL TO THE DEVELOPERS. The callsign is filled in from the station's own
        // - the operator has already typed it once today and should not be asked again.
        private void SendMailToDevelopersMenuItem_Click(object sender, RoutedEventArgs e)
        {
            string from = (TB_MyCallsign != null ? (TB_MyCallsign.Text ?? string.Empty) : string.Empty).Trim();
            var w = new SupportMailWindow(from) { Owner = this };
            w.ShowDialog();
        }

        // HELP > SUPPORT > OPEN THE ERROR LOG.
        // Everywhere the program catches a fault and keeps going, it writes a line here first. That is
        // the only trace such a fault leaves, and until this menu existed the file could only be reached
        // by being told its path - which is no use at all to someone whose program is misbehaving.
        // Opened with Notepad by name rather than by letting Windows choose: ".log" is very often
        // registered to nothing at all, and then Process.Start on the file offers "how do you want to
        // open this?" instead of showing the operator their log.
        private void OpenErrorLogMenuItem_Click(object sender, RoutedEventArgs e)
        {
            string path = Log.FilePath;
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
            {
                MessageBox.Show(this,
                    "There is no error log yet.\n\nThat is good news: it means nothing has gone wrong " +
                    "that HolyLogger had to write down.",
                    "Error log", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                System.Diagnostics.Process.Start("notepad.exe", "\"" + path + "\"");
            }
            catch (Exception ex)
            {
                Log.Swallow(ex);
                MessageBox.Show(this, "The log could not be opened.\n\nIt is here:\n" + path,
                    "Error log", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // The same file, shown in its folder - which is also where logDB.db lives, so this is the way to
        // reach the database for a backup or to send a copy to the developer.
        private void ShowErrorLogFolderMenuItem_Click(object sender, RoutedEventArgs e)
        {
            string path = Log.FilePath;
            try
            {
                if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
                {
                    System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + path + "\"");
                    return;
                }

                // No log file yet: open the folder it would be written to, which still holds the database.
                string dir = string.IsNullOrEmpty(path) ? null : System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && System.IO.Directory.Exists(dir))
                    System.Diagnostics.Process.Start("explorer.exe", "\"" + dir + "\"");
                else
                    MessageBox.Show(this, "The folder could not be found.", "Error log",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                Log.Swallow(ex);
                MessageBox.Show(this, "The folder could not be opened.\n\nIt is here:\n" + path,
                    "Error log", MessageBoxButton.OK, MessageBoxImage.Warning);
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

        // HELP > WHAT'S NEW. THE WHOLE HISTORY, not merely the version that happens to be running.
        //
        // It used to show one section - the current version's - and that answered the wrong question.
        // A release with one small fix in it then looked like the sum of the program's news, and
        // everything done in the versions before it was invisible to anyone who had not been watching
        // at the time. The file keeps every section for exactly this reason; showing one of them threw
        // that away.
        //
        // The window that opens BY ITSELF after an update is still the narrow one: there, "what has
        // changed since the version I was running" is precisely the question.
        //
        // Fetched fresh every time. The file is a few hundred bytes and the operator asking deserves
        // the current answer, not whatever was true the day he installed.
        private async void WhatsNewMenuItem_Click(object sender, RoutedEventArgs e)
        {
            string file = await ReleaseNotes.FetchAsync();

            if (string.IsNullOrWhiteSpace(file))
            {
                HolyMessageBox.Show(file == null
                    ? "The list of changes could not be fetched. Check your internet connection and try again."
                    : "Nothing has been written down yet.",
                    "What's New", HolyMsgType.Info, this);
                return;
            }

            // No version in the heading: this is every version there has been.
            WhatsNewWindow.ShowIfAny(this, null, file.Trim());
        }

        // THE FIRST RUN OF A NEW VERSION SHOWS WHAT CHANGED, BY ITSELF. Everything since the version
        // this operator was last running, not merely the newest - somebody who skips three releases
        // and then updates is told what happened in all three.
        //
        // A first-ever install shows nothing: there is no "before" to report on, and greeting a new
        // operator with a list of repairs to a program he has never used explains nothing.
        private async Task ShowWhatsNewIfVersionChanged()
        {
            try
            {
                string current = ReleaseNotes.CurrentVersion;
                if (string.IsNullOrWhiteSpace(current)) return;

                string seen = ReleaseNotes.LastSeenVersion;
                if (string.Equals(seen, current, StringComparison.Ordinal)) return;

                // Written FIRST, and whatever happens next. If the fetch fails, or the file has nothing
                // for this version, the operator must not be asked the same question at every startup
                // for ever after.
                ReleaseNotes.LastSeenVersion = current;
                if (string.IsNullOrWhiteSpace(seen)) return;   // first ever install

                // THE WHOLE HISTORY HERE TOO. This window used to show only what had changed since the
                // version that was running - defensible, and not what was asked for. A release with
                // one small fix in it then opened saying one line, and the twenty things that arrived
                // in the version before were nowhere, which reads as though the program had barely
                // changed. The newest section is at the top, so what IS new is still the first thing
                // read; everything else is underneath for whoever wants it.
                string file = await ReleaseNotes.FetchAsync();
                WhatsNewWindow.ShowIfAny(this, null, (file ?? string.Empty).Trim());
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
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
                    // Off the UI thread. This is a menu item, but it also fires by itself at startup
                    // when "check for updates automatically" is on.
                    var response = await Task.Run(() => client.GetAsync(baseRequest));
                    var responseFromServer = await response.Content.ReadAsStringAsync();

                    if (CompareVersions(CurrentVersion, responseFromServer))
                    {
                        // WHAT IS IN IT, BEFORE HE DECIDES. "There is a new version" asks an operator
                        // to install something without telling him what it does - and the one time he
                        // most wants to know is while the question is on screen. Everything between
                        // the version he is running and the one on offer, so a run of releases he
                        // skipped is not reduced to the last of them.
                        string newVersion = (responseFromServer ?? "").Trim();
                        string notesFile = await ReleaseNotes.FetchAsync();
                        string notes = ReleaseNotes.Since(notesFile, CurrentVersion);

                        string ask = "There is a new version"
                                   + (newVersion.Length > 0 ? " — " + newVersion : "") + ".";
                        if (!string.IsNullOrWhiteSpace(notes))
                            ask += "\n\n" + notes.Trim();
                        ask += "\n\nDo you want to install?";

                        if (HolyMessageBox.ShowConfirm(ask, "New updates are available", HolyMsgType.Info, this,
                                                       string.IsNullOrWhiteSpace(notes) ? 0 : 620))
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

        // True while the caret is in one of the two frequency editors - the no-CAT box, or the inline
        // editor over the LED. Both treat Esc as "undo this edit", which only reaches them if the
        // window-wide Esc stands aside.
        private bool IsEditingFrequency()
        {
            return (TB_FreqNoCat != null && TB_FreqNoCat.IsKeyboardFocused)
                || (TB_FreqLedEdit != null && TB_FreqLedEdit.IsKeyboardFocused
                    && TB_FreqLedEdit.Visibility == Visibility.Visible);
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
                // Esc belongs to whichever frequency editor the caret is sitting in: there it undoes the
                // edit in progress and puts back the frequency that was there. This is a TUNNELING
                // handler, so without stepping aside it consumed Esc before the box ever saw it - and
                // ClearBtn_Click deliberately leaves the frequency alone, so what the operator had just
                // typed survived and looked as though Esc had accepted it. F9 still clears the form from
                // anywhere, including from inside these boxes.
                if (key == Key.Escape && IsEditingFrequency())
                    return false;

                // Esc also aborts a message transmission in progress (SSB voice or CW) — the same as
                // pressing the sending F-key again. If nothing is being transmitted it clears the entry
                // form as before. (F9 keeps its clear-only behaviour.)
                if (key == Key.Escape && StopActiveMessageTransmission())
                    return true;
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
                e.Handled = true;   // a space is never part of a callsign

                // Contest mode: Space jumps straight to the first received-exchange cell (the one
                // right after RST-R), skipping RST-R for fast entry. Tab still stops on RST-R first.
                if (Properties.Settings.Default.ContestMode && _contestRxBoxes.Count > 0)
                {
                    TextBox target = _contestRxBoxes[0];
                    target.Focus();
                    target.SelectAll();
                }
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
            // While the DX box is locked ("Select Log"), re-check on every keystroke so typing the
            // log's callsign back unlocks immediately — LostFocus alone is unreliable (clicking a
            // non-focusable element never fires it).
            if (_callsignLocked) RefreshCallsignLockState();
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

        // Remembers the callsign when editing begins, so LostFocus can tell whether it actually
        // changed (and avoid firing on a programmatic Text update, which never moves focus).
        private string _callsignOnFocus;

        private void TB_MyCallsign_GotFocus(object sender, RoutedEventArgs e)
        {
            _callsignOnFocus = TB_MyCallsign.Text?.Trim();
        }

        private void TB_MyCallsign_LostFocus(object sender, RoutedEventArgs e)
        {
            CommitStationCallsignEdit();
        }

        // Runs the station-callsign change guard on a committed edit. Called on focus leave AND from
        // the lock button: locking approves whatever is typed, and the lock is a plain image whose
        // click never moves keyboard focus, so LostFocus alone would let a mismatched callsign be
        // locked in without ever being checked against the active log's identity.
        private void CommitStationCallsignEdit()
        {
            string now = TB_MyCallsign.Text?.Trim();
            if (string.IsNullOrEmpty(now)) return;
            if (string.Equals(now, _callsignOnFocus, StringComparison.OrdinalIgnoreCase)) return;
            _callsignOnFocus = now;

            // Changing the station callsign so it no longer matches the active log's identity would put
            // two different station callsigns in one log. Don't allow it: lock the box ("Select Log")
            // and send the user to the Log Manager to open or create a log for this callsign. (The
            // operator may vary within one log -- multi-op -- so only the callsign is enforced.)
            if (HandleStationCallsignChange(now)) return;   // mismatch handled -> skip the services alert

            // The callsign agrees with the active log again (e.g. a mismatch was typed and then
            // reverted) -> clear a leftover "Select Log" lock.
            RefreshCallsignLockState();

            ShowStationCallsignServicesAlert(now);
        }

        // ── Station-callsign ↔ active-log identity guard ──────────────────────────────────────────
        // A log holds QSOs for exactly one station callsign (its permanent identity). The callsign box
        // must therefore always agree with the active log's identity. Changing it to something else is
        // treated as "I want to operate as a different call" and routes the user to the Log Manager to
        // open/create a log for that call, rather than silently mixing callsigns in the current log.

        private bool _callsignLocked;
        private string _pendingStationCallsign;   // the callsign typed while the box is locked

        // Handles a deliberate change of the station callsign. Returns true (and locks the DX box) when
        // the new callsign has no log set for it (differs from the active log's identity); false when
        // there's nothing to enforce. The station callsign box itself stays editable throughout.
        private bool HandleStationCallsignChange(string now)
        {
            if (dal == null || state != State.New) return false;   // only guard while logging new QSOs
            string idCall = ActiveLogIdentityCallsign();
            if (idCall.Length == 0) return false;                  // log has no identity yet -> this call becomes it
            if (CallsignIdentity.Same(idCall, now)) return false;  // same identity (stroke suffixes ignored) -> fine

            SetCallsignLocked(true, now);   // block DX entry until a log for this callsign is set
            bool open = HolyMessageBox.ShowConfirm(
                "The active log belongs to station callsign \"" + idCall + "\".\n\n" +
                "A log holds QSOs for one station callsign only, so you can't log as \"" + now + "\" until a " +
                "log is set for it. Open an existing log for this callsign or create a new one — its identity " +
                "fills in automatically.\n\nOpen the Log Manager now?",
                "Select a log for " + now, HolyMsgType.Warning, this);
            if (open)
                OpenLogManager(now);   // filter the list to logs for this callsign
            // Opening/creating a matching log re-syncs the station box and unlocks; otherwise the DX box
            // stays "Select Log" until a log for this callsign is chosen.
            RefreshCallsignLockState();
            return true;
        }

        private void CallsignLockOverlay_MouseDown(object sender, MouseButtonEventArgs e)
        {
            OpenLogManager(_pendingStationCallsign);   // still resolving this callsign -> keep the list filtered
            RefreshCallsignLockState();
        }

        // Opens the Log Manager (same as File -> View Logs). When filterCallsign is given (the callsign-
        // change flow), the list shows only logs for that callsign.
        private void OpenLogManager(string filterCallsign = null)
        {
            var win = new ViewLogsWindow(this, dal, filterCallsign) { Owner = this };
            win.ShowDialog();
        }

        // The active log's permanent identity callsign (empty if it has none / on error).
        private string ActiveLogIdentityCallsign()
        {
            try
            {
                dal.GetLogIdentity(dal.ActiveLogId, out string idCall, out string _);
                return (idCall ?? string.Empty).Trim();
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); return string.Empty; }
        }

        // Shows/hides the "Select Log" overlay over the DX callsign box and blocks/unblocks typing a DX
        // callsign. The station callsign box stays editable; only starting a QSO is blocked until a log is
        // set for the current station callsign. `pending` = the station callsign with no log yet, used to
        // filter the Log Manager when the overlay is clicked.
        // caption: what the overlay says. "Select Log" for a callsign with no log of its own; "No log"
        // when nothing at all is open, because those are two different things to be told.
        private void SetCallsignLocked(bool locked, string pending, string caption = "Select Log")
        {
            _callsignLocked = locked;
            _pendingStationCallsign = locked ? pending : null;
            if (CallsignLockOverlay != null)
            {
                CallsignLockOverlay.Visibility = locked ? Visibility.Visible : Visibility.Collapsed;
                if (CallsignLockText != null) CallsignLockText.Text = caption;
                CallsignLockOverlay.ToolTip = caption == "No log"
                    ? "No log is open, so there is nowhere to put a QSO. Click to open the Log Manager and open or create one."
                    : "No log is set for your current station callsign. Click to open the Log Manager and open or create a log for it before logging QSOs.";
            }
            if (TB_DXCallsign != null)
                TB_DXCallsign.IsReadOnly = locked;
        }

        // Locks the box iff the current callsign differs from the active log's identity callsign - or
        // there is no log open at all, which is the same situation in its purest form: there is nowhere
        // for the QSO to go. Locking the DX callsign box stops a QSO being STARTED, which is far better
        // than letting the operator fill in the whole form and refusing it at the end.
        private void RefreshCallsignLockState()
        {
            if (dal == null || TB_MyCallsign == null) return;
            string boxCall = (TB_MyCallsign.Text ?? string.Empty).Trim();

            if (!dal.HasActiveLog)
            {
                // No filter callsign: with nothing open, the Log Manager must show EVERY log, not only
                // those matching whatever is in the station box - the operator is choosing a log, not
                // resolving a callsign.
                SetCallsignLocked(true, null, "No log");
                return;
            }

            string idCall = ActiveLogIdentityCallsign();
            bool mismatch = idCall.Length > 0 && boxCall.Length > 0
                            && !CallsignIdentity.Same(idCall, boxCall);
            SetCallsignLocked(mismatch, boxCall);
        }

        // Keeps the callsign box in step with the active log: switching to a log shows that log's
        // identity callsign. Empty logs (no identity) keep whatever is typed -- it becomes their
        // identity on the first QSO. A stroke variant of the identity (4Z5SL/M for a 4Z5SL log)
        // is the same identity, so it stays as typed. Call after the active log changes.
        private void SyncCallsignToActiveLog()
        {
            if (dal == null || TB_MyCallsign == null) return;
            string idCall = ActiveLogIdentityCallsign();
            if (idCall.Length > 0 &&
                !CallsignIdentity.Same(idCall, (TB_MyCallsign.Text ?? string.Empty).Trim()))
            {
                TB_MyCallsign.Text = idCall;    // updates the box + the my_callsign setting
                _callsignOnFocus = idCall;
            }
            RefreshCallsignLockState();
        }

        // Holds a services check that arrived while a dialog was open, until the dialog closes.
        private DispatcherTimer _servicesAlertWait;

        // True while something is on screen that the services alert must not cover: any modal dialog,
        // or an ADIF import still running behind its progress window.
        private bool ServicesAlertMustWait
        {
            get
            {
                if (System.Windows.Interop.ComponentDispatcher.IsThreadModal) return true;
                return AdifHandlerWorker != null && AdifHandlerWorker.IsBusy;
            }
        }

        // When the operator switches to a different Station Callsign, summarise how each upload
        // service will treat QSOs logged under it, so special-event calls aren't silently sent to
        // the wrong place. Only shown when at least one service needs attention.
        private void ShowStationCallsignServicesAlert(string call, bool isStartup = false)
        {
            if (string.IsNullOrWhiteSpace(call) || dal == null) return;
            call = call.Trim();

            // NO LOG, NOTHING TO SAY. With no log open no QSO can be made, so how eQSL and LoTW would
            // treat one is not a question yet. Deleting the last log used to be answered with this
            // window - a report about uploading contacts that cannot be logged at all - which is
            // noise at exactly the moment the operator is being told to go and open a log.
            if (!dal.HasActiveLog) return;

            // NEVER INTERRUPT AN IMPORT, OR A DIALOG THAT IS ALREADY ASKING SOMETHING. This alert is
            // modal and owned by the main window, so it opens ON TOP of whatever else is up - first over
            // the import's "Confirm the identity of this file" window, hiding the very button that
            // continues the import, and then over "Parsing ADIF 36%". Neither is a moment to be told
            // about eQSL: until the file has been read there is nothing to upload anywhere.
            //
            // Two things to wait for. A modal frame is running whenever IsThreadModal is set, whichever
            // dialog owns it, so that covers every question without naming the windows. And the import
            // worker covers the long silent stretch in between, which is not modal at all. The import's
            // own completion message is modal, so the check lands after the operator closes it - which
            // is the first moment the answer actually matters.
            //
            // A timer, not another Dispatcher post: a modal frame goes on pumping the dispatcher, so a
            // re-queued call would come straight back while the dialog is still up and spin.
            if (ServicesAlertMustWait)
            {
                if (_servicesAlertWait != null) return;   // one deferred check is enough
                string waitingCall = call;
                bool waitingStartup = isStartup;
                _servicesAlertWait = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
                {
                    Interval = TimeSpan.FromMilliseconds(400)
                };
                _servicesAlertWait.Tick += (s, e) =>
                {
                    if (ServicesAlertMustWait) return;
                    _servicesAlertWait.Stop();
                    _servicesAlertWait = null;
                    ShowStationCallsignServicesAlert(waitingCall, waitingStartup);
                };
                _servicesAlertWait.Start();
                return;
            }

            // Per-service "use this service" master switches (Options pages). A service the user
            // switched off never triggers this alert and is reported as "not in use". With all
            // three off there is nothing to check at all.
            bool useEqsl = Properties.Settings.Default.UseEqslService;
            bool useLotw = Properties.Settings.Default.UseLotwService;
            bool useQrz  = Properties.Settings.Default.UseQrzLogbook;
            bool useClublog = Properties.Settings.Default.UseClublogService;
            if (!useEqsl && !useLotw && !useQrz && !useClublog) return;

            // eQSL — per-callsign accounts table.
            bool eqslHasAccount = false;
            try { eqslHasAccount = dal.IsCallsignInEqslTable(call); } catch (System.Exception swallowed) { Log.Swallow(swallowed); }

            // LoTW — per-callsign TQSL station location.
            string savedPicks = Properties.Settings.Default.LotwCallsignLocations;
            var choice = LotwStationResolver.Resolve(call, savedPicks);
            bool lotwOk = !choice.Ambiguous && !string.IsNullOrWhiteSpace(choice.LocationName);

            // QRZ — one logbook/API key for every callsign.
            bool qrzOn = useQrz && QrzPushEnabled;

            // Club Log — account-level (e-mail + password + the app key), not per-callsign.
            bool clublogConfigured = ClublogService.HasApiKey
                && !string.IsNullOrWhiteSpace(Properties.Settings.Default.ClublogEmail)
                && !string.IsNullOrWhiteSpace(Properties.Settings.Default.ClublogPassword);

            // A disabled service counts as "nothing to warn about". Club Log joins eQSL/LoTW here so a
            // Club-Log-only misconfiguration also opens this window on startup.
            bool registered = (!useEqsl || eqslHasAccount) && (!useLotw || lotwOk) && (!useClublog || clublogConfigured);
            if (isStartup)
            {
                // On startup only interrupt when the callsign is NOT registered for an eQSL/LoTW
                // service that is in use. A fully-registered call opens silently even if QRZ
                // auto-upload is on.
                if (registered) return;
            }
            else
            {
                // On a deliberate change, surface anything worth knowing — including the QRZ
                // single-logbook caveat.
                if (registered && !qrzOn) return;
            }

            string eqslMsg = !useEqsl
                ? "Not in use — switched off in Options → eQSL Service."
                : eqslHasAccount
                    ? $"Account configured — QSOs will upload under {call}."
                    : $"No eQSL account for {call} — its QSOs will NOT be sent to eQSL.";

            string lotwMsg;
            if (!useLotw)
                lotwMsg = "Not in use — switched off in Options → LoTW Upload.";
            else if (choice.Ambiguous)
                lotwMsg = $"{call} has several TQSL locations — pick one in Options → LoTW Upload.";
            else if (!string.IsNullOrWhiteSpace(choice.LocationName))
                lotwMsg = $"Will sign with TQSL location: \"{choice.LocationName}\".";
            else
                lotwMsg = $"No TQSL certificate/location for {call} — its QSOs will NOT upload to LoTW.";

            string qrzMsg = !useQrz
                ? "Not in use — switched off in Options → QRZ Services."
                : qrzOn
                    ? $"QRZ uses ONE logbook for all callsigns. QSOs under {call} go into your configured QRZ logbook regardless of the call. Turn off QRZ auto-upload in Options → QRZ Services if that is not what you want."
                    : "QRZ auto-upload is off — nothing is sent automatically.";

            string clublogMsg = !useClublog
                ? "Not in use — switched off in Options → Club Log Service."
                : !ClublogService.HasApiKey
                    ? "This copy of HolyLogger has no Club Log application key — QSOs will NOT be sent to Club Log."
                    : clublogConfigured
                        ? "Configured — QSOs will be uploaded to Club Log."
                        : "Club Log e-mail/password not set — QSOs will NOT be sent. Set them in Options → Club Log Service.";

            var alert = new StationServicesAlertWindow(
                call,
                !useEqsl || eqslHasAccount, eqslMsg,
                !useLotw || lotwOk,         lotwMsg,
                !qrzOn,                     qrzMsg,
                !useClublog || clublogConfigured, clublogMsg)
            { Owner = this };
            alert.ShowDialog();
        }
        
        // Shared by the My Locator and My Holyland Square boxes (both wire TextChanged here).
        private void TB_MyHolyland_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (signboard != null)
            {
                signboard.signboardData.Square = TB_MyHolyland.Text;
            }

            // The home map is centered on the LOCATOR, not the square — ShowHomeMap re-renders the
            // WebView map (and re-plots every cluster spot when the cluster map is on). Doing that on
            // each Holyland-square keystroke made typing feel sluggish, for a value the map ignores.
            // Only re-render when the change came from the locator box.
            if (!ReferenceEquals(sender, TB_MyHolyland))
            {
                ShowHomeMap();
            }
        }

        // The official Holyland squares (HolyLogParser.validSquares) are stored as "K-07-YZ"; the
        // square is typed here without the dashes ("K07YZ"). Normalize both to dash-free uppercase
        // for an O(1) membership check.
        private static readonly HashSet<string> _validHolylandSquares = new HashSet<string>(
            HolyParser.HolyLogParser.validSquares.Select(s => s.Replace("-", string.Empty).ToUpperInvariant()),
            StringComparer.OrdinalIgnoreCase);

        // Validates the operator's own Holyland square against the official list. Blank is fine (only
        // Israeli 4X/4Z stations send a square). Only user edits fire this — programmatic changes don't.
        private void TB_MyHolyland_PreviewLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (TB_MyHolyland == null || _fieldWarningOpen)
                return;

            string typed = (TB_MyHolyland.Text ?? string.Empty).Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(typed))
                return;

            string normalized = typed.Replace("-", string.Empty).Replace(" ", string.Empty);
            if (!_validHolylandSquares.Contains(normalized))
            {
                e.Handled = true;
                WarnInvalidField(TB_MyHolyland,
                    "\"" + typed + "\" is not a valid Holyland square.\n\nA square is a letter + 2 digits + a 2-letter region (e.g. K07YZ). It must be one of the official Holyland squares — see https://tools.iarc.org/holysquare/",
                    "Invalid Holyland Square");
            }
        }

        // Guards field-validation warnings against re-entrancy. HolyMessageBox is modal, so opening it
        // pulls keyboard focus off the field and fires PreviewLostKeyboardFocus AGAIN — without this
        // guard that stacked dialogs endlessly (looked like a freeze). The dialog is deferred until the
        // current focus change settles, then focus is returned to the field for correction.
        private bool _fieldWarningOpen;
        private void WarnInvalidField(System.Windows.Controls.TextBox box, string message, string title)
        {
            _fieldWarningOpen = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try { HolyMessageBox.ShowWarning(message, title, this); }
                finally { _fieldWarningOpen = false; }
                if (box != null) { box.Focus(); box.SelectAll(); }
            }), System.Windows.Threading.DispatcherPriority.Input);
        }

        private void TB_MyLocator_PreviewLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (TB_MyLocator == null || _fieldWarningOpen)
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
                WarnInvalidField(TB_MyLocator,
                    "\"" + locator + "\" is not a valid grid square.\n\nUse 2 letters + 2 digits (e.g. KM72), optionally followed by 2 letters (e.g. KM72OR). The first pair is A–R, the 5th/6th characters are letters A–X (e.g. O), not zeros (0).",
                    "Invalid My Locator");
            }
        }

        private void TB_Exchange_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            //if (!char.IsDigit(e.Text, e.Text.Length - 1))
            //    e.Handled = true;
        }

        // ITU/CQ zone boxes accept digits only.
        private void ZoneTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            foreach (char c in e.Text)
            {
                if (!char.IsDigit(c)) { e.Handled = true; return; }
            }
        }

        // True only when every character belongs to the Latin script (or is a common digit,
        // space or punctuation symbol). Standard ASCII plus the Latin accented ranges are
        // allowed — so names like "José" or "Müller" pass — while non-Latin scripts such as
        // Hebrew, Cyrillic, Greek, Arabic and CJK are rejected.
        private static bool IsEnglishOnly(string text)
        {
            if (string.IsNullOrEmpty(text)) return true;
            foreach (char c in text)
            {
                if (!IsAllowedLatinChar(c))
                    return false;
            }
            return true;
        }

        private static bool IsAllowedLatinChar(char c)
        {
            // Printable ASCII: English letters, digits and common punctuation (U+0020..U+007E).
            if (c >= ' ' && c <= '~') return true;
            // Latin-1 Supplement: accented Western letters (U+00A0..U+00FF).
            if (c >= ' ' && c <= 'ÿ') return true;
            // Latin Extended-A and Extended-B (U+0100..U+024F).
            if (c >= 'Ā' && c <= 'ɏ') return true;
            // Latin Extended Additional (U+1E00..U+1EFF, e.g. Vietnamese letters).
            if (c >= 'Ḁ' && c <= 'ỿ') return true;
            // Everything else - Hebrew, Cyrillic, Greek, Arabic, CJK, etc. - is rejected.
            return false;
        }

        // Throttles the "wrong keyboard language" beep so a held-down key (auto-repeat) or fast
        // mashing does not machine-gun the sound; a deliberate keystroke still beeps each time.
        private DateTime _lastNonEnglishBeepUtc = DateTime.MinValue;

        // Blocks non-English characters typed into any text box, and beeps so the operator notices
        // their keyboard is in a non-English state (e.g. Hebrew) — otherwise the blocked keystroke
        // is silent and looks like the field is broken.
        private void GlobalTextBox_EnglishOnly_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            if (!IsEnglishOnly(e.Text))
            {
                e.Handled = true;

                if (Properties.Settings.Default.NonEnglishKeyBeep)
                {
                    var now = DateTime.UtcNow;
                    if ((now - _lastNonEnglishBeepUtc).TotalMilliseconds > 200)
                    {
                        _lastNonEnglishBeepUtc = now;
                        // Route to the user's chosen device (e.g. speakers) so it doesn't go into a USB codec.
                        PlayClusterAlertSound("Beep", Properties.Settings.Default.SoundOutputDevice);
                    }
                }
            }
        }

        // Cancels a paste into any text box when the clipboard text contains non-English characters.
        private void GlobalTextBox_EnglishOnly_Pasting(object sender, DataObjectPastingEventArgs e)
        {
            if (e.DataObject.GetDataPresent(DataFormats.UnicodeText) || e.DataObject.GetDataPresent(DataFormats.Text))
            {
                string text = (e.DataObject.GetData(DataFormats.UnicodeText) ?? e.DataObject.GetData(DataFormats.Text)) as string;
                if (!IsEnglishOnly(text))
                    e.CancelCommand();
            }
            else
            {
                // Non-text payload (e.g. an image) — disallow.
                e.CancelCommand();
            }
        }

        private void TB_Band_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateDup();
            if (clusterActiveBandIndicatorText != null)
            {
                UpdateClusterActiveBandIndicatorText();   // band in green/gray, or red "out of band"
                UpdateClusterActiveBandIndicatorPosition();
            }

            UpdateActiveBandButtonVisibility();

            if (string.Equals(Properties.Settings.Default.ClusterBandFilterMode, "Active", StringComparison.OrdinalIgnoreCase))
            {
                RefreshClusterVisibleSpots();
            }

            UpdateBandTextBoxColor();
            UpdateBandPickAvailability();   // the chevron only shows while the box is still empty
        }

        // ---------- Band picker: Manual mode with no frequency -----------------------------------
        //
        // The Band box is a read-out - it is filled from the frequency and from nothing else. That
        // leaves nowhere to say which band a QSO was on when there IS no frequency, which is exactly
        // the case once the operator has chosen Manual and not typed one (logging from paper, or from a
        // radio the program cannot read). Then, and only then, the box becomes a drop-down of every
        // band the program works.
        //
        // TB_Band.Text stays the one answer to "what band are we on": the cluster filters, the duplicate
        // check, the matrix and the logged QSO all read it, so picking from the list just writes it and
        // every one of them follows without needing to know where the band came from.
        // The working frequency, in MHz. Writes BOTH ends, and everything that changes the frequency
        // outside the radio must go through here.
        //
        // TB_Frequency is a permanently collapsed box whose Text is bound TwoWay to Settings.Frequency
        // with the TextBox default UpdateSourceTrigger of LostFocus - and a collapsed box never gets
        // focus, so the setting never catches up on its own and a refresh of that binding puts the old
        // value straight back. Clearing the box alone therefore did not stick: the frequency reappeared
        // while the band, an ordinary unbound box, stayed cleared. The rig path (MainWindow.Rig.cs) has
        // always set both for the same reason.
        private void SetWorkingFrequency(string mhz)
        {
            string value = mhz ?? string.Empty;
            if (TB_Frequency == null) return;

            Properties.Settings.Default.Frequency = value;
            TB_Frequency.Text = value;
        }

        // "There is no frequency" - answered from the box the operator can SEE, whenever that box is the
        // one on screen.
        //
        // TB_Frequency is the stored value, and in the no-CAT box it only catches up when an entry is
        // committed - Enter, or the focus leaving. Asking it alone meant a frequency that had visibly
        // been deleted still counted as present, and the Band box went on showing a band derived from
        // it. What the operator is looking at decides.
        private bool FrequencyIsEmpty
        {
            get
            {
                if (FreqNoCatBezel != null && FreqNoCatBezel.Visibility == Visibility.Visible
                    && TB_FreqNoCat != null)
                    return (TB_FreqNoCat.Text ?? string.Empty).Trim().Length == 0;

                return TB_Frequency == null || string.IsNullOrWhiteSpace(TB_Frequency.Text);
            }
        }

        // The band is always READ from the frequency now — manual mode included — and shown read-only, so
        // the hand-pick drop-down is never offered. In manual mode you type a frequency and the band
        // follows it; a QSO with no legal frequency is refused at save time (see Validate). Left as a
        // single flag so UpdateBandPickAvailability and the CB_Band handlers simply stay dormant.
        private bool BandPickAvailable => false;

        // Guard so filling the list, or following TB_Band, is not mistaken for the operator picking.
        private bool _settingBandPick;

        // Swaps the read-out box for the drop-down and back. Called wherever either half of the
        // condition can change - the frequency arriving or going away, and Manual/CAT - so the swap
        // happens the moment it becomes true, with no click needed to discover it.
        private void UpdateBandPickAvailability()
        {
            if (TB_Band == null || CB_Band == null) return;

            bool pickable = BandPickAvailable;

            if (pickable && CB_Band.Items.Count == 0)
                foreach (string b in HolyLogParser.KnownBands) CB_Band.Items.Add(b);

            // Whichever one is showing must show the same band, so switching between them never loses it.
            // SelectedIndex = -1 for "no band": setting SelectedItem to a string that is not in the list
            // leaves the old selection sitting there to be re-announced later.
            _settingBandPick = true;
            try
            {
                string current = (TB_Band.Text ?? string.Empty).Trim();
                if (current.Length == 0) CB_Band.SelectedIndex = -1;
                else CB_Band.SelectedItem = current;
            }
            finally { _settingBandPick = false; }

            // Shut the list BEFORE hiding the box it hangs off: picking a band fills the frequency, and
            // that swaps this control away while its own selection is still being handled.
            if (!pickable) CB_Band.IsDropDownOpen = false;
            CB_Band.Visibility = pickable ? Visibility.Visible : Visibility.Collapsed;
            TB_Band.Visibility = pickable ? Visibility.Collapsed : Visibility.Visible;
        }

        // A band chosen from the list. Writing TB_Band.Text is most of the job: everything downstream
        // watches that box, exactly as it does when the frequency fills it in.
        // The operator opened the list. This - not a flag around the assignments - is what makes a
        // selection "theirs".
        //
        // Keeping the box's selection in step with the band is done while the box is COLLAPSED, and a
        // collapsed ComboBox has generated no item containers, so WPF cannot apply the selection yet and
        // defers the event. It arrives tens of milliseconds later, once the box is shown, long after any
        // flag around the assignment has been reset - carrying the band that was selected BEFORE. Read
        // as a choice, it re-filled the frequency the operator had just deleted, which put the band back
        // too. It only failed on the first try because the stale selection is then spent.
        private bool _bandDropDownWasOpened;

        private void CB_Band_DropDownOpened(object sender, EventArgs e)
        {
            _bandDropDownWasOpened = true;
        }

        private void CB_Band_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_settingBandPick) return;
            if (!_bandDropDownWasOpened) return;   // not a choice: a deferred event from a past selection
            _bandDropDownWasOpened = false;

            string band = CB_Band.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(band)) return;

            TB_Band.Text = band;
            // force: choosing a band from this list IS the operator saying where the QSO was, so the
            // frequency follows it even if one is already showing. Without that, picking a second band
            // left the form claiming 10M at 18080 kHz - a frequency on 17m.
            AutoFillFreqFromBandMode(null, force: true);
        }

        // Band and mode together say roughly where on the dial a QSO was, and that is worth more in the
        // log than an empty frequency. Fills the frequency box from the two of them - but ONLY while it
        // is empty. A frequency that is already there was either typed or read from the radio, and
        // neither may be overwritten by a guess.
        //
        // modeOverride is for the mode combo's own SelectionChanged, where CB_Mode.Text still holds the
        // PREVIOUS mode (it is data-bound and lags one event).
        //
        // force is for a band picked by hand: that is a statement about where the QSO was, so the
        // frequency has to match it whether or not one is already showing. A mode change never forces -
        // changing mode with a frequency in the box leaves that frequency alone.
        private void AutoFillFreqFromBandMode(string modeOverride, bool force = false)
        {
            if (TB_Frequency == null || TB_Band == null) return;
            if (!force && !FrequencyIsEmpty) return;

            string band = (TB_Band.Text ?? string.Empty).Trim();
            if (band.Length == 0) return;

            string mode = !string.IsNullOrWhiteSpace(modeOverride)
                ? modeOverride
                : (CB_Mode != null ? CB_Mode.Text : null);

            string freq = HolyLogParser.BandModeToFreq(band, mode);
            if (string.IsNullOrWhiteSpace(freq)) return;

            SetWorkingFrequency(freq);   // TextChanged re-derives the band from it, and lands on the same one

            // And show it in the box the operator is looking at. UpdateFreqLed refuses to touch that box
            // while it has keyboard focus - rightly, since it will not fight someone typing in it - but
            // the caret is still sitting there right after they deleted the old frequency, so the value
            // went into the stored frequency and never appeared on screen. This one did not come from
            // typing: it came from the band they just picked, so it has to be shown.
            if (TB_FreqNoCat != null && FreqNoCatBezel != null
                && FreqNoCatBezel.Visibility == Visibility.Visible)
                FillFreqNoCatFromFrequency();
        }

        private void UpdateBandTextBoxColor()
        {
            if (TB_Band == null) return;
            string band = TB_Band.Text;
            if (string.IsNullOrWhiteSpace(band))
            {
                SetBandBoxForeground(SystemColors.ControlTextBrush);
                return;
            }
            try
            {
                SetBandBoxForeground(GetBandBrush(band));
            }
            catch
            {
                SetBandBoxForeground(SystemColors.ControlTextBrush);
            }
        }

        // Both halves of the Band box - the read-out and the drop-down that stands in for it - carry the
        // band's own colour, so which one is showing makes no difference to what the operator sees.
        private void SetBandBoxForeground(System.Windows.Media.Brush brush)
        {
            TB_Band.Foreground = brush;
            if (CB_Band != null) CB_Band.Foreground = brush;
        }

        private void TB_DX_Name_TextChanged(object sender, TextChangedEventArgs e)
        {
            // The box is right-anchored: it keeps its right edge fixed and grows leftward so long
            // names stay visible. These must match the XAML placement (Margin 69, Width 264 ->
            // right edge 333) so the box does not jump when the text is cleared after saving a QSO.
            const double rightEdge = 333;   // 69 + 264, stops short of the Country label at x=340
            const double minLeft = 57;      // just right of the "Name" label, the leftmost it may grow
            const double defaultLeft = 69;  // normal resting position (matches XAML)

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

        // Now stored in Properties.Settings (it used to be a "left|top|width|height" text file) so a
        // profile captures the QRZ Photo window's placement too. NaN means "never saved".
        private void LoadQrzPhotoWindowBoundsFromDisk()
        {
            try
            {
                var s = Properties.Settings.Default;
                if (double.IsNaN(s.QrzPhotoWindowLeft) || double.IsNaN(s.QrzPhotoWindowTop) ||
                    double.IsNaN(s.QrzPhotoWindowWidth) || double.IsNaN(s.QrzPhotoWindowHeight))
                    return;

                qrzPhotoLeft = s.QrzPhotoWindowLeft;
                qrzPhotoTop = s.QrzPhotoWindowTop;
                qrzPhotoWidth = s.QrzPhotoWindowWidth;
                qrzPhotoHeight = s.QrzPhotoWindowHeight;
            }
            catch (System.Exception swallowed) { Log.Swallow(swallowed); }
        }

        private void PersistQrzPhotoWindowBoundsToDisk()
        {
            try
            {
                if (!qrzPhotoLeft.HasValue || !qrzPhotoTop.HasValue || !qrzPhotoWidth.HasValue || !qrzPhotoHeight.HasValue)
                {
                    return;
                }

                var s = Properties.Settings.Default;
                s.QrzPhotoWindowLeft = qrzPhotoLeft.Value;
                s.QrzPhotoWindowTop = qrzPhotoTop.Value;
                s.QrzPhotoWindowWidth = qrzPhotoWidth.Value;
                s.QrzPhotoWindowHeight = qrzPhotoHeight.Value;
                s.Save();
            }
            catch (System.Exception swallowed) { Log.Swallow(swallowed); }
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
                // Owner alone keeps the photo above the MAIN window, which is all it ever needed.
                // It used to be Topmost as well, which put it above every OTHER window too - the
                // Channels window could not be raised over it by clicking, and its custom title bar
                // stayed buried underneath where there was nothing left to drag.
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

                ResetRstForMode(val);
                UpdateDup();
                // Refresh the Msg buttons so they switch to/from the CW look immediately on a mode
                // change (matters when the radio is off, where the mode comes from this dropdown).
                UpdateMessageButtonLabels();
                // CW and phone live in different parts of a band, so the mode is half of the answer to
                // "which frequency". Only ever fills an EMPTY box - changing mode with a frequency
                // already in it leaves that frequency exactly where it is.
                AutoFillFreqFromBandMode(val);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"CB_Mode_SelectionChanged error: {ex.Message}");
                //throw;
            }

        }

        private void TB_DXCallsign_TextChanged(object sender, TextChangedEventArgs e)
        {
            // If the user is actively editing the DX textbox, consider this a manual edit and
            // clear the cluster auto-fill flag so the cluster no longer overwrites/clears it.
            try
            {
                if (TB_DXCallsign != null && TB_DXCallsign.IsFocused && !_clusterFillingDXCall)
                {
                    _clusterAutoFilledDXCall = false;
                    _holyClusterSelectedCall = null;   // operator took over the DX field; forget the HolyCluster selection
                }
            }
            catch { }

            // While loading a QSO into the form for editing, the callsign is set programmatically and
            // we must NOT run the typing lookup (it would clear/overwrite the QSO's saved fields).
            if (_suppressCallsignLookupForEdit)
                return;

            // Asymmetric contests (Holyland) show a different received field for an Israeli vs a DX
            // worked station — rebuild the received frame when the callsign crosses that boundary.
            RefreshContestRxForCallsign();

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
                // The DX box was emptied (F9/clear, a logged QSO, or manual delete) — release any held
                // HolyCluster selection so it isn't re-applied and normal auto-fill can resume.
                _holyClusterSelectedCall = null;

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
                    TB_QTH.Text = "";
                    TB_ITUZone.Text = "";
                    TB_CQZone.Text = "";
                    UpdateCountryFlag(null);

                    ShowDxccCode(0);
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
                    TB_ITUZone.Text = "";
                    TB_CQZone.Text = "";
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

            // Typing a callsign is the start of a contact, so its date and time are now - unless a QSO is
            // being edited, or the operator is holding the clock (Time: Manual).
            if (state == State.New && !Properties.Settings.Default.isTimeManual)
                RefreshDateTime_Btn_MouseUp(null, null);

            // Perform DXCC lookup. Club Log answers first when it knows this callsign on this date
            // (K9W was Wake Island, not the USA its prefix suggests); cty.dat answers otherwise and
            // always supplies the wording and the ITU zone. A QSO being edited is resolved on its own
            // date, not today's.
            DateTime lookupWhen = (state == State.Edit && QsoToUpdate != null)
                ? CountryLookup.QsoDate(QsoToUpdate.Date)
                : DateTime.UtcNow;
            DXCC dXCC = CountryLookup.Shared.Resolve(dxCallText, lookupWhen);
            Country = dXCC.Name;
            UpdateCountryFlag(dXCC.Name);
            ShowDxccCode(dXCC.IsDxccEntity ? (dXCC.DxccCode > 0 ? dXCC.DxccCode : EntityCodeForCall(dxCallText, null)) : 0);
            // "XX" is the resolver saying it did not recognise the prefix, not a continent. Storing it
            // puts a placeholder in the QSO that every screen then has to read as data - it turned up in
            // the Log Workshop's Continent filter sitting among AF, AS, EU and the rest, as though the
            // operator had worked a place called XX. Blank is the honest answer, and it is the same rule
            // the parser, the Log Verifier and the QSO editor already follow.
            Continent = string.Equals(dXCC.Continent, "XX", StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : dXCC.Continent;
            QRZGrid = dXCC.Locator;
            // Fill ITU/CQ zones offline from cty.dat (entity default or the prefix-specific
            // override). A later QRZ.com lookup, when available, overrides these as the gold source.
            TB_ITUZone.Text = dXCC.ItuZone > 0 ? dXCC.ItuZone.ToString() : "";
            TB_CQZone.Text = dXCC.CqZone > 0 ? dXCC.CqZone.ToString() : "";
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

                // Also gather same-callsign QSOs from the ACTIVE log's copy-target log — shown in the
                // grid for reference (painted blue), never counted or treated as a duplicate. Only when
                // filtering and there is a callsign to match (an empty fragment would match everything).
                List<QSO> foreignMatches = null;
                if (isFilterQSOs && !string.IsNullOrWhiteSpace(capturedDxCall))
                {
                    try
                    {
                        long? tgt = dal.GetCopyTargetLogId(dal.ActiveLogId);
                        if (tgt.HasValue)
                            foreignMatches = dal.GetQsosWithCallsignInLog(tgt.Value, capturedDxCall);
                    }
                    catch (Exception swallowed) { Log.Swallow(swallowed); }
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
                        var combined = new ObservableCollection<QSO>(matchingQsos);
                        var foreignSet = new HashSet<QSO>(RefEq.Instance);
                        if (foreignMatches != null)
                            foreach (var fq in foreignMatches)
                            {
                                foreignSet.Add(fq);
                                combined.Add(fq);
                            }
                        _foreignFilterRows = foreignSet.Count > 0 ? foreignSet : null;
                        FilteredQsos = combined;
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
                catch (System.Exception swallowed) { Log.Swallow(swallowed); }
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
            catch (System.Exception swallowed) { Log.Swallow(swallowed); }
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

        // Background check for a newer cty.dat from country-files.com. The download (if any) is
        // saved to disk and applied on the next launch; we stay silent so startup is undisturbed.
        private void CheckCtyDatUpdateFireAndForget()
        {
            Task.Run(async () =>
            {
                try { await CtyDatService.CheckForUpdateAsync(_sharedHttpClient, isNetworkAvailable); }
                catch (System.Exception swallowed) { Log.Swallow(swallowed); }
            });
        }

        // Background check for a newer Club Log country database. A download is saved to disk and used
        // from the next launch, so a session's country answers never change under the operator's feet.
        private void CheckClublogCtyUpdateFireAndForget()
        {
            Task.Run(async () =>
            {
                try { await ClublogCtyService.CheckForUpdateAsync(_sharedHttpClient, isNetworkAvailable); }
                catch (System.Exception swallowed) { Log.Swallow(swallowed); }
            });
        }

        private void CheckLotwUpdateFireAndForget()
        {
            Task.Run(async () =>
            {
                try { await LotwUserService.RefreshIfStaleAsync(_sharedHttpClient); }
                catch (System.Exception swallowed) { Log.Swallow(swallowed); }
            });
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
            catch (System.Exception swallowed) { Log.Swallow(swallowed); }

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
                    catch (System.Exception swallowed) { Log.Swallow(swallowed); }

                    Action<string> appendTrace = message =>
                    {
                        if (!verboseSyncTrace)
                            return;

                        try
                        {
                            File.AppendAllText(traceLogPath, message + "\n");
                        }
                        catch (System.Exception swallowed) { Log.Swallow(swallowed); }
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
                    catch (System.Exception swallowed) { Log.Swallow(swallowed); }
                    
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
            // --- cty.dat (AD1C) entity name spellings ---
            // The resolver now returns cty.dat's names, which differ in spelling from the older
            // table above (e.g. "...Islands" vs "...Is.", "United States" vs "...of America").
            // These re-key the SAME flag images to the new names. The older spellings are kept
            // above so previously-logged QSOs (stored under the old names) keep their flags.
            {"African Italy","it"},{"Aland Islands","fi"},{"Annobon Island","gq"},
            {"Ascension Island","sh"},{"Asiatic Turkey","tr"},{"Austral Islands","fr"},
            {"Aves Island","ve"},{"Baker & Howland Islands","us"},{"Balearic Islands","es"},
            {"Banaba Island","ki"},{"Bear Island","no"},{"British Virgin Islands","vg"},
            {"Canary Islands","es"},{"Cayman Islands","ky"},{"Central African Republic","cf"},
            {"Central Kiribati","ki"},{"Chagos Islands","gb"},{"Chatham Islands","nz"},
            {"Chesterfield Islands","nc"},{"Christmas Island","au"},{"Clipperton Island","fr"},
            {"Cocos (Keeling) Islands","au"},{"Cocos Island","cr"},{"Crozet Island","fr"},
            {"DPR of Korea","kp"},{"Dem. Rep. of the Congo","cd"},{"Desecheo Island","pr"},
            {"Ducie Island","gb"},{"Easter Island","cl"},{"Eastern Kiribati","ki"},
            {"European Turkey","tr"},{"Falkland Islands","fk"},{"Faroe Islands","fo"},
            {"Galapagos Islands","ec"},{"Glorioso Islands","fr"},{"Heard Island","au"},
            {"Johnston Island","us"},{"Juan Fernandez Islands","cl"},{"Kerguelen Islands","fr"},
            {"Kermadec Islands","nz"},{"Kingdom of Eswatini","sz"},{"Kure Island","us"},
            {"Kyrgyzstan","kg"},{"Lakshadweep Islands","in"},{"Lord Howe Island","au"},
            {"Macquarie Island","au"},{"Madeira Islands","pt"},{"Malpelo Island","co"},
            {"Mariana Islands","mp"},{"Marquesas Islands","fr"},{"Marshall Islands","mh"},
            {"Midway Island","us"},{"N.Z. Subantarctic Is.","nz"},{"Navassa Island","us"},
            {"Norfolk Island","nf"},{"North Cook Islands","ck"},{"North Macedonia","mk"},
            {"Palmyra & Jarvis Islands","us"},{"Peter 1 Island","no"},{"Pitcairn Island","gb"},
            {"Pr. Edward & Marion Is.","za"},{"Pratas Island","tw"},{"Republic of Korea","kr"},
            {"Republic of South Sudan","ss"},{"Republic of the Congo","cg"},{"Reunion Island","re"},
            {"Revillagigedo","mx"},{"Rodriguez Island","mu"},{"Rotuma Island","fj"},
            {"Sable Island","ca"},{"Samoa","ws"},{"Shetland Islands","gb"},{"Sicily","it"},
            {"Sint Maarten","sx"},{"Solomon Islands","sb"},{"South Cook Islands","ck"},
            {"South Georgia Island","gb"},{"South Orkney Islands","gb"},{"South Sandwich Islands","gb"},
            {"South Shetland Islands","gb"},{"Sov Mil Order of Malta","it"},{"Spratly Islands","ph"},
            {"St. Barthelemy","fr"},{"St. Martin","fr"},{"St. Paul Island","ca"},
            {"St. Peter & St. Paul","br"},{"Swains Island","us"},{"Timor - Leste","tl"},
            {"Tokelau Islands","nz"},{"Trindade & Martim Vaz","br"},{"Tristan da Cunha & Gough","gb"},
            {"Tromelin Island","fr"},{"Turks & Caicos Islands","tc"},{"UK Base Areas on Cyprus","cy"},
            {"US Virgin Islands","vi"},{"United States","us"},{"Vatican City","va"},
            {"Vienna Intl Ctr","un"},{"Wake Island","us"},{"Wallis & Futuna Islands","wf"},
            {"Western Kiribati","ki"},{"Willis Island","au"},
        };

        // Puts the country's number in the small box beside its name. Blank for 0: a contact that belongs
        // to no entity has no number, and a "0" in that box would read as a country numbered zero.
        private void ShowDxccCode(int code)
        {
            if (TB_DxccCode == null) return;
            TB_DxccCode.Text = code > 0 ? code.ToString() : string.Empty;
        }

        // The ADIF entity number for a callsign worked on a given date - the country's identity, stored
        // with the QSO so nothing downstream has to work it out from the name again. 0 when it cannot be
        // known, or when the contact belongs to no entity at all (a station at sea counts for nobody).
        internal static int EntityCodeForCall(string call, string adifDate)
        {
            if (string.IsNullOrWhiteSpace(call)) return 0;
            try
            {
                DXCC d = CountryLookup.Shared.Resolve(call.Trim(), CountryLookup.QsoDate(adifDate));
                if (d == null || !d.IsDxccEntity) return 0;
                return d.DxccCode > 0 ? d.DxccCode : CountryLookup.Shared.EntityCodeForCountry(d.Name);
            }
            catch (System.Exception swallowed) { Log.Swallow(swallowed); return 0; }
        }

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
                catch (System.Exception swallowed) { Log.Swallow(swallowed); }
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

            // qso_list is exactly the per-station filter UpdateDup would rebuild; reuse it so a
            // keystroke costs one scan of the log instead of two.
            UpdateDupCore(qso_list);
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
            // The QSOs with this station, found in one lookup rather than by walking the log.
            //
            // This runs on every character typed into the callsign box. The walk it replaces measured
            // 0.97 ms per keystroke on a 28,454-QSO log here, and several times that on a slow machine
            // - paid again for every letter, in the middle of a contest.
            //
            // The comment that used to stand here refused a cache because a QSO edit changes the object
            // in place and raises no collection event, so the cache could report a wrong Duplicate
            // verdict. That danger is answered in EnsureLogLookups, not ignored: the index is thrown
            // away the moment anything is written that could move a callsign, and every edit in the
            // program goes through that one door.
            if (Qsos == null) return;
            string myCall = TB_MyCallsign.Text;
            string dxCall = TB_DXCallsign.Text;

            EnsureLogLookups();
            List<QSO> perStation;
            if (!_qsosByStation.TryGetValue(myCall + StationKeySeparator + dxCall, out perStation))
                perStation = _noQsosWithStation;

            UpdateDupCore(perStation);
        }

        // Evaluates the Duplicate / "Legal N QSOs Before" indicators from an already-filtered
        // list of QSOs with the current station (same MyCall + DXCall). Lets UpdateMatrix reuse
        // its own filtered list instead of rescanning the whole log.
        private void UpdateDupCore(List<QSO> perStation)
        {
            string band = TB_Band.Text;
            string mode = CB_Mode.Text;

            bool hasDup = false;
            foreach (var qso in perStation)
            {
                if (qso.Band == band && qso.Mode == mode
                    && (state != State.Edit || qso.id != QsoToUpdate.id))
                {
                    hasDup = true;
                    break;
                }
            }

            // "Duplicate" is only meaningful in Contest Mode. Outside a contest we never report a
            // duplicate; we just show how many times the station was worked before.
            if (Properties.Settings.Default.ContestMode && hasDup)
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
                int legalCount = perStation.Count;
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

            // Never persist an off-screen top-left (e.g. -32000,-32000 while minimized,
            // or RestoreBounds = Empty). Otherwise the next launch opens invisibly.
            if (IsPositionOnScreen(bounds.Left, bounds.Top))
            {
                Properties.Settings.Default.MainWindowLeft = bounds.Left;
                Properties.Settings.Default.MainWindowTop = bounds.Top;
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

            // (The old MainFormBackgroundColor / QsoTableHeaderBackgroundColor / ContestExchangeColor /
            // ContestSendColor watchers are gone: those item colors are palette tokens now -- edited
            // in View > Color Scheme > Customize Colors and updated live via DynamicResource.)

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

        private void UpdateShareIconVisibility()
        {
            if (ShareStatusButton == null) return;
            ShareStatusButton.Visibility = Visibility.Visible;
            UpdateShareStatusButtonState();
        }

        // The one header look used by every log-style table (QSO grid, cluster spots, and the
        // Logs window's grid): the LogHeaderBg palette token (designer default burlywood in every
        // scheme; user-overridable via View > Color Scheme > Customize Colors) with black text.
        // Background is a DynamicResource setter, so scheme switches and Customize Colors edits
        // repaint the headers live -- no per-change re-apply needed. Static so other windows
        // (ViewLogsWindow) get the identical style from the same source of truth.
        internal static Style BuildLogTableHeaderStyle()
        {
            var headerStyle = new Style(typeof(DataGridColumnHeader));
            headerStyle.Setters.Add(new Setter(Control.BorderBrushProperty, (Brush)new BrushConverter().ConvertFromString("#1565C0")));
            headerStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0, 0, 1, 3)));
            headerStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(5, 3, 5, 3)));
            headerStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
            headerStyle.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("LogHeaderBg")));
            headerStyle.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.Black));
            return headerStyle;
        }

        // Assigns the shared header style to the QSO grid (once; the style's DynamicResource
        // background keeps it current from then on).
        private void ApplyQsoTableHeaderBackgroundFromSettings()
        {
            if (QSODataGrid == null)
            {
                return;
            }

            QSODataGrid.ColumnHeaderStyle = BuildLogTableHeaderStyle();
        }

        private async void GetQrzData()
        {
            // Snapshot the lookup revision up front. Typing another character, and clearing the form
            // with F9 or Add-with-F1 (both empty the DX-callsign box, which bumps this counter), all
            // change it — so a QRZ response that comes back after the operator has moved on is
            // discarded instead of writing stale ITU/CQ zones, name, etc. onto a cleared/different
            // callsign. This mirrors the revision guard already used for the QRZ photo below.
            int revisionAtStart = callsignLookupRevision;

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
                    // Started on a background thread, not here. Awaiting is not enough on .NET Framework:
                    // the proxy for a request is resolved on whichever thread STARTS it, before the call
                    // goes off on its own. With "automatically detect proxy settings" on - the Windows
                    // default, and what hotel and club networks run - that resolution can take a moment,
                    // and this method runs on every callsign the operator types. The startup code dodges
                    // the same trap for the QRZ login; this is the typing path, where it matters more.
                    var response = await Task.Run(() => _sharedHttpClient.GetAsync(baseRequest + SessionKey + ";callsign=" + bare_dxcall));
                    var responseFromServer = await response.Content.ReadAsStringAsync();
                    XDocument xDoc = XDocument.Parse(responseFromServer);
                    XNamespace ns = xDoc.Root.GetDefaultNamespace();

                    if (revisionAtStart == callsignLookupRevision && !string.IsNullOrWhiteSpace(SessionKey) && !string.IsNullOrWhiteSpace(TB_DXCallsign.Text) && (dxcall == TB_DXCallsign.Text.Trim()))
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

                            // Override the offline (cty.dat) zones with QRZ ONLY when QRZ actually
                            // provides them. Many records (e.g. DXpeditions like VP6D) carry no
                            // zone fields; blanking them here would wipe the good cty.dat values.
                            XElement ituEl = xDoc.Root.Descendants(ns + "ituzone").FirstOrDefault();
                            if (ituEl != null && !string.IsNullOrWhiteSpace(ituEl.Value))
                                TB_ITUZone.Text = ituEl.Value.Trim();
                            XElement cqEl = xDoc.Root.Descendants(ns + "cqzone").FirstOrDefault();
                            if (cqEl != null && !string.IsNullOrWhiteSpace(cqEl.Value))
                                TB_CQZone.Text = cqEl.Value.Trim();

                            IEnumerable<XElement> stateEl = xDoc.Root.Descendants(ns + "state");
                            TB_State.Text = stateEl.Count() > 0 ? stateEl.FirstOrDefault().Value.Trim() : string.Empty;

                            // The station's town. QRZ has no field called "qth": its mailing address is
                            // split into addr1 (house number and street) and addr2 (the city), so addr2
                            // is what ADIF calls QTH. Nothing extra is requested for it - the lookup
                            // already returns the whole record.
                            XElement qthEl = xDoc.Root.Descendants(ns + "addr2").FirstOrDefault();
                            TB_QTH.Text = qthEl != null ? qthEl.Value.Trim() : string.Empty;

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
                                TB_QTH.Text = "";
                                // Keep the offline cty.dat ITU/CQ zones — QRZ not finding this call
                                // doesn't invalidate the prefix-based zones already shown.
                                ClearQrzPhoto();
                            }
                        }
                    }
                }
                catch (Exception)
                {
                    FName = "";
                    TB_State.Text = "";
                    TB_QTH.Text = "";
                    // Keep the offline cty.dat ITU/CQ zones (prefix-based, still valid).
                    ClearQrzPhoto();
                }
            }
            else
            {
                FName = "";
                TB_State.Text = "";
                TB_QTH.Text = "";
                TB_ITUZone.Text = "";
                TB_CQZone.Text = "";
                ClearQrzPhoto();
            }
        }

        // Shared client for scraping the QRZ profile page for the operator photo. Browser-like
        // headers because qrz.com serves different (or no) content to unknown user agents.
        private static readonly HttpClient _qrzPhotoHttpClient = CreateQrzPhotoHttpClient();

        private static HttpClient CreateQrzPhotoHttpClient()
        {
            var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate };
            var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
            client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
            client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
            return client;
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
                {
                    // Shared static client (see _qrzPhotoHttpClient): this method runs on every
                    // callsign lookup, and new HttpClient(+handler) per call leaks sockets into
                    // TIME_WAIT during an active session.
                    var client = _qrzPhotoHttpClient;
                    string html = string.Empty;

                    // Off the UI thread for the same reason as the lookup above: the proxy is resolved
                    // where the request is started, and this is two requests, on the typing path.
                    if (!string.IsNullOrWhiteSpace(SessionKey))
                    {
                        html = await Task.Run(() => client.GetStringAsync("https://xmldata.qrz.com/xml/current/?s=" + SessionKey + ";html=" + bareCallsign));
                    }

                    if (string.IsNullOrWhiteSpace(html))
                    {
                        html = await Task.Run(() => client.GetStringAsync("https://www.qrz.com/db/" + bareCallsign));
                    }

                    // The address runs to the first quote, angle bracket or space - the characters that
                    // can only be the HTML around it. What stood here was [^"'<>\x00-*] with three RAW
                    // control characters in the source file, invisible in any editor: 0x00-0x1F, then
                    // 0x7F-0x1F, a range running backwards. .NET rejects that, so BOTH of these matches
                    // threw ArgumentException every single time and the profile page could never yield a
                    // photo - proved by ArgumentException "range in reverse order" in holylogger.log.
                    Match match = Regex.Match(html, @"https://cdn-bio\.qrz\.com/[^""'<>\s]+", RegexOptions.IgnoreCase);
                    if (match.Success)
                    {
                        SetQrzPhoto(match.Value);
                        return;
                    }

                    Match altMatch = Regex.Match(html, @"https?://[^""'<>\s]+\.(jpg|jpeg|png|gif)", RegexOptions.IgnoreCase);
                    if (altMatch.Success)
                    {
                        SetQrzPhoto(altMatch.Value);
                        return;
                    }
                }
            }
            catch (System.Exception swallowed) { Log.Swallow(swallowed); }

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

            _qrzCts = new CancellationTokenSource();
            UploadProgressTitle = "QRZ Lookup";
            ShowStopButton(true);
            ToggleUploadProgress(Visibility.Visible);
            try
            {
                await GetQrzForEntireLogAsync(new Progress<string>(msg => UploadProgress = msg), _qrzCts.Token);
            }
            finally
            {
                ShowStopButton(false);
                ToggleUploadProgress(Visibility.Hidden);
                UploadProgressTitle = "";
                _qrzCts.Dispose();
                _qrzCts = null;
            }
        }

        private async Task<bool> GetQrzForEntireLogAsync(IProgress<string> progress, CancellationToken token = default)
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
                    DataAccess.ReportsFolder,
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
                if (token.IsCancellationRequested) break;   // Stop button pressed
                progress.Report($"{i + 1} / {needsLookup.Count}");
                try
                {
                    // Small delay between requests to avoid QRZ rate-limiting
                    // and to keep the UI message loop free between iterations.
                    await Task.Delay(150);
                    if (token.IsCancellationRequested) break;   // Stop pressed during the delay: write nothing
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
                    DataAccess.ReportsFolder,
                    "qrz_missing_debug.txt");
                var sb = new System.Text.StringBuilder();
                sb.AppendLine();
                sb.AppendLine($"QRZ results — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine(new string('-', 100));
                foreach (var line in debugResults) sb.AppendLine(line);
                System.IO.File.AppendAllText(debugPath, sb.ToString(), System.Text.Encoding.UTF8);
            }
            catch (System.Exception swallowed) { Log.Swallow(swallowed); }

            return true;
        }

        // QRZ's grid square for one callsign, for windows that are not this one - the Log Fixer offers
        // it as a suggested DX Locator. The lookup needs this window's QRZ session key and its shared
        // HTTP client, so it stays here and is reached through the running main window rather than
        // being duplicated with a second session of its own.
        //
        // Returns null for anything that did not work: no main window, no subscription, no such call,
        // network down. The caller treats null as "QRZ had no answer", which is the truth in every one
        // of those cases and is never worth four different messages.
        internal static async Task<string> QrzGridFor(string callsign)
        {
            if (string.IsNullOrWhiteSpace(callsign)) return null;
            MainWindow mw = null;
            if (Application.Current != null)
                foreach (Window w in Application.Current.Windows)
                    if (w is MainWindow) { mw = (MainWindow)w; break; }
            if (mw == null) return null;
            try
            {
                var r = await mw.GetQrzForCall(callsign);
                return string.IsNullOrWhiteSpace(r.Grid) ? null : r.Grid.Trim().ToUpperInvariant();
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); return null; }
        }

        private async Task<(string Name, string Grid)> GetQrzForCall(string callsign)
        {
            try
            {
                string baseRequest = "http://xmldata.qrz.com/xml/current/?s=";
                // Off the UI thread, like the lookup on the typing path: the proxy is resolved where
                // the request is started. This one runs once per QSO of a whole-log lookup.
                var response = await Task.Run(() => _sharedHttpClient.GetAsync(baseRequest + SessionKey + ";callsign=" + Services.getBareCallsign(callsign)));
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
            // Identify duplicates up front WITHOUT touching the DB, in the ACTIVE log only. Two QSOs are
            // duplicates when they are THE SAME CONTACT by the program's one definition of that -
            // DataAccess.MatchKey: callsign, date, band, mode and the minute. The import's merge asks
            // the same question of the same method, so a pair this window would call duplicates is
            // exactly a pair the import would not add twice.
            //
            // It used to compare the frequency, the station callsign and the operator as well. Those
            // came out because they are what a file exported by another program gets WRONG - a rounded
            // frequency, a missing operator - and a rule that only one half of the program can honour is
            // not one rule. On the file this was measured against it made a difference of three records
            // in 17,430, every one of them shown for review before anything is deleted.
            //
            // The first of each group is kept; the extras are
            // shown for review in DuplicatesWindow (same background color per group) and deleted
            // only after the operator confirms there. Deleting only those rows -- instead of the
            // old "delete everything then re-insert the unique ones" -- means the log is never left
            // partial: cancelling mid-run simply leaves some duplicates un-removed.
            var all = dal.GetQSOsForLog(dal.ActiveLogId);
            List<DupGroup> groups = DuplicateScan.Find(all);

            if (groups.Count == 0)
            {
                HolyMessageBox.Show("No duplicate QSOs were found in the active log.",
                    "Remove Duplicates", HolyMsgType.Info, this);
                return;
            }

            // TWO STEPS, BECAUSE TWO DIFFERENT QUESTIONS ARE BEING ASKED. Nearly every duplicate is a
            // plain copy: the same contact twice, nothing written on either that the other does not
            // say. Those are reviewed and removed in one go. The rare group where the copies carry
            // DIFFERENT comments is not a thing the program can settle - somebody wrote two different
            // notes about one contact - so it is held back and asked about on its own screen.
            var simple = groups.Where(g => !g.NeedsChoice).ToList();
            var conflicts = groups.Where(g => g.NeedsChoice).ToList();

            var approved = new List<DupGroup>();

            if (simple.Count > 0)
            {
                var review = new DuplicatesWindow(simple.Select(g => g.Members).ToList()) { Owner = this };
                if (review.ShowDialog() != true) return;   // backing out of the first step stops both
                approved.AddRange(simple);
            }

            if (conflicts.Count > 0)
            {
                var choose = new DuplicatesWindow(conflicts) { Owner = this };
                if (choose.ShowDialog() == true)
                    approved.AddRange(conflicts.Where(g => !g.Skipped));
            }

            var toDelete = approved.SelectMany(g => g.Extras).ToList();
            if (toDelete.Count == 0) return;

            _dedupCts = new CancellationTokenSource();
            var token = _dedupCts.Token;
            UploadProgressTitle = "Removing Duplicates";
            UploadProgress = $"0 / {toDelete.Count:N0}";
            ShowStopButton(true);
            ToggleUploadProgress(Visibility.Visible);

            int removed = 0;
            bool cancelled = false;
            try
            {
                await Task.Run(() =>
                {
                    // THE COMMENT MOVES ACROSS BEFORE THE COPY GOES. Whether the operator chose it on
                    // the second screen or the group simply had one comment on the copy and none on
                    // the contact that stays, it is written first - so a note somebody wrote can never
                    // be deleted along with the row that happened to be carrying it.
                    foreach (DupGroup g in approved)
                    {
                        string chosen = (g.ChosenComment ?? string.Empty).Trim();
                        if (chosen.Length == 0) continue;
                        if (string.Equals(chosen, (g.Keep.Comment ?? string.Empty).Trim(),
                                          StringComparison.Ordinal)) continue;
                        try
                        {
                            g.Keep.Comment = chosen;
                            lock (_syncLock) { dal.Update(g.Keep); }
                        }
                        catch (Exception swallowed) { Log.Swallow(swallowed); }
                    }

                    int lastPct = -1;
                    for (int i = 0; i < toDelete.Count; i++)
                    {
                        if (token.IsCancellationRequested) break;
                        try
                        {
                            lock (_syncLock) { dal.Delete(toDelete[i].id); }
                            removed++;
                        }
                        catch { /* skip a row that won't delete; never abort the whole run */ }

                        int pct = (i + 1) * 100 / toDelete.Count;
                        if (pct != lastPct)
                        {
                            lastPct = pct;
                            int shown = removed;
                            Dispatcher.Invoke(() => UploadProgress = $"{shown:N0} / {toDelete.Count:N0}");
                        }
                    }
                }, token);
            }
            catch (OperationCanceledException) { }
            finally
            {
                cancelled = token.IsCancellationRequested;
                ShowStopButton(false);
                ToggleUploadProgress(Visibility.Hidden);
                UploadProgressTitle = "";
                _dedupCts.Dispose();
                _dedupCts = null;
            }

            // Reload the grid from the (now smaller) ACTIVE log.
            Qsos.Clear();
            foreach (var item in dal.GetQSOsForLog(dal.ActiveLogId))
                Qsos.Add(item);
            UpdateNumOfQSOs();

            HolyMessageBox.Show(
                cancelled
                    ? $"Stopped. Removed {removed:N0} duplicate(s); the rest were left in place."
                    : $"Removed {removed:N0} duplicate QSO(s).",
                "Remove Duplicates", HolyMsgType.Info, this);
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
                    // Off the UI thread: this runs from the constructor, so the proxy resolution it
                    // would otherwise do there lands squarely in the startup the program just stopped
                    // making people wait through.
                    var response = await Task.Run(() => client.GetAsync(baseRequest));
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
        /// The events subscribed
        /// </summary>
        private bool EventsSubscribed = false;

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

        // Shows "RIG1" or "RIG2" next to the LED, reflecting the rig chosen in Options → General.
        private void UpdateRigLabel()
        {
            if (RigLabel == null) return;
            RigLabel.Text = Properties.Settings.Default.SelectedOmniRig2 ? "RIG2" : "RIG1";
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
            // CAT may have just come online or dropped; the Manual/CAT choice must reflect that.
            UpdateFreqModeRadios();

            TB_Frequency.BorderBrush = System.Windows.Media.Brushes.Gray;
            L_OmniRig.Foreground = ThemeManager.Brush("TextBrush");
            L_OmniRig.FontWeight = FontWeights.Normal;
            
            Status = "CAT Enabled";
            // Red border = the frequency is NOT being driven by the radio, so the operator owns it.
            // Manual Mode counts: CAT may be online, but the frequency is typed rather than read from
            // the rig, so it must look the same as CAT disabled/offline instead of implying it is live.
            if (!Properties.Settings.Default.EnableOmniRigCAT || Rig == null
                || Rig.Status != OmniRig.RigStatusX.ST_ONLINE
                || Properties.Settings.Default.isManualMode)
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

        #endregion

        private void PreventSpaceInCallsign(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Space)
            {
                e.Handled = true;
            }
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

        // Used when a Date cell is edited (the log grid is read-only, so this never runs there).
        // Anything that is not a date we recognise is REJECTED - DoNothing leaves the stored value
        // alone rather than writing a typo into the log.
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string typed = (value as string)?.Trim();
            if (string.IsNullOrWhiteSpace(typed)) return System.Windows.Data.Binding.DoNothing;

            string[] accepted = { "dd-MM-yyyy", "dd/MM/yyyy", "dd.MM.yyyy", "yyyyMMdd", "yyyy-MM-dd" };
            if (DateTime.TryParseExact(typed, accepted, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime dt))
                return dt.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

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

    // Time for EDITING, as opposed to the display converter above.
    //
    // The display drops the seconds ("08:54"), which is right for reading a log and wrong for editing
    // one: committing that back would store 08:54:00 and quietly destroy the logged seconds. This
    // shows the full HH:mm:ss so nothing is lost by opening the cell.
    public class QsoTimeEditConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string raw = value as string;
            if (!string.IsNullOrWhiteSpace(raw) && raw.Length == 6 &&
                DateTime.TryParseExact(raw, "HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime dt))
                return dt.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            return value;
        }

        // Anything that is not a time we recognise is REJECTED (DoNothing keeps the stored value)
        // rather than written through - a log is worth more than a typo.
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string typed = (value as string)?.Trim();
            if (string.IsNullOrWhiteSpace(typed)) return System.Windows.Data.Binding.DoNothing;

            string[] accepted = { "HH:mm:ss", "HH:mm", "HHmmss", "HHmm" };
            if (DateTime.TryParseExact(typed, accepted, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime dt))
                return dt.ToString("HHmmss", CultureInfo.InvariantCulture);

            return System.Windows.Data.Binding.DoNothing;
        }
    }

    // True when this callsign is a known LoTW uploader, so the log table can flag it the way the
    // cluster already flags a spotted station.
    //
    // Returns a BOOL rather than a brush on purpose: the caller turns it into a colour through a
    // DynamicResource, so the marking follows a theme change. A brush handed back from here would be
    // resolved once and then stay whatever colour was current when the row was drawn.
    //
    // LotwUserService keeps the ARRL list in a HashSet, so this is an O(1) lookup per row - and it
    // answers false until a list has been downloaded, which simply means nothing is marked yet.
    public class LotwUserConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => Properties.Settings.Default.MarkLotwUsersInLog && LotwUserService.IsLotwUser(value as string);

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => System.Windows.Data.Binding.DoNothing;
    }

    // Colors the Band/Frequency cells in the QSO log grid using the exact same per-band colors as
    // the cluster band-filter buttons and spot dots (MainWindow.GetBandColor is the single source of
    // truth for all three).
    public class BandColorConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string band = value as string;
            if (string.IsNullOrWhiteSpace(band)) return ThemeManager.Brush("TextBrush");
            try { return MainWindow.GetBandBrush(band); }
            catch { return ThemeManager.Brush("TextBrush"); }
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






