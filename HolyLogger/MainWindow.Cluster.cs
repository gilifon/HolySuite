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
    // Cluster window: spot feed (WebSocket), spots grid, band filters, legend, map linkage, undo, spotting.
    // Move-only split from MainWindow.xaml.cs; no behavior change.
    public partial class MainWindow
    {
        Window clusterWindow = null;
        Button clusterMaxRestoreBtn = null;  // custom title bar maximize/restore glyph, kept in sync by ClusterWindow_StateChanged
        DataGrid clusterSpotsGrid = null;   // cluster spots table, kept for live re-theming
        Window clusterSettingsWindow = null;
        ClientWebSocket clusterWebSocket = null;
        CancellationTokenSource clusterWebSocketCts = null;
        long clusterLastSpotTime = 0;
        static readonly string ClusterLogPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "HolyLogger", "cluster_connection.log");
        HashSet<string> clusterSpotKeys = new HashSet<string>(StringComparer.Ordinal);
        List<ClusterSpotViewItem> clusterAllSpots = new List<ClusterSpotViewItem>();
        // Per-band spot-count label shown under each band checkbox, keyed by band name.
        Dictionary<string, TextBlock> clusterBandSpotCountTexts = new Dictionary<string, TextBlock>(StringComparer.OrdinalIgnoreCase);
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
        TextBlock clusterNewCountryCountText = null;
        DispatcherTimer _clusterNewCountryBlinkTimer = null;
        DateTime _clusterNewCountryBlinkStopTime;
        bool _clusterNewCountryBlinkOn = true;
        int _lastNewCountryCount = 0;
        StackPanel clusterOnMyFreqLegendItem = null;
        FrameworkElement clusterLegendPanel = null;
        Canvas clusterHeaderCanvas = null;
        DataGridColumn clusterDxColumn = null;
        DataGridColumn clusterSpotterColumn = null;
        DataGridColumn clusterFreqColumn = null;
        DataGridColumn clusterUtcColumn = null;
        DataGrid clusterSpotsDataGrid = null;
        // Stable key per cluster table column, used to persist/restore the column ORDER.
        Dictionary<DataGridColumn, string> clusterColumnKeys = new Dictionary<DataGridColumn, string>();
        ScrollViewer clusterSpotsScrollViewer = null;
        bool clusterTableMarginInitialized = false;
        StackPanel clusterLastMinutesFilterPanel = null;
        StackPanel clusterBandSelectorPanel = null;
        StackPanel clusterModeSelectorPanel = null;
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
        Border clusterSpotCountBadge = null;
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
        // Gap between the legend's right edge and the Selected/Active band-filter group, and
        // between that group and the floating spot-count badge. These anchor the band group to
        // the legend (left) instead of to the Freq column, so resizing columns no longer moves it.
        const double ClusterLegendToBandGroupGap = 6.0;
        const double ClusterBandGroupToCounterGap = 10.0;
        // The counter badge and the Last/dropdown both sit this many px above the table top
        // (the table top is at canvas-Y == ClusterTableTopGap).
        const double ClusterControlsToTableGap = 2.0;
        const double ClusterBaseSharedVerticalShift = -45.0;
        const double ClusterLastMinutesDropdownTop = -45.0;
        const double ClusterLastMinutesDropdownWidth = 44;

        // Extra column references needed for width persistence on close
        DataGridColumn clusterCountryColumn = null;
        DataGridColumn clusterModeColumn = null;
        DataGridColumn clusterCommentColumn = null;
        DispatcherTimer _mapUpdateDebounceTimer = null;
        bool _dxQsoInProgress = false;
        // Spotter of the cluster spot the user last selected, so the map's DE button can center on
        // it. Kept with the DX callsign it belongs to, so a hand-typed DX doesn't reuse a stale spotter.
        double? _selectedSpotterLat, _selectedSpotterLon;
        string _selectedSpotterDxCall;
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
        private bool _uploadInFlight = false; // true only while UploadAllAndCloseAsync's async work is actually running
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
            try { Properties.Settings.Default.Save(); } catch (System.Exception swallowed) { Log.Swallow(swallowed); }

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

            if (clusterSpotCountBadge != null)
            {
                Canvas.SetTop(clusterSpotCountBadge, 0);
                Canvas.SetLeft(clusterSpotCountBadge, ClusterOffScreenPosition);
                headerCanvas.Children.Add(clusterSpotCountBadge);
            }

            if (clusterModeSelectorPanel != null)
            {
                Canvas.SetTop(clusterModeSelectorPanel, 0);
                Canvas.SetLeft(clusterModeSelectorPanel, ClusterOffScreenPosition);
                headerCanvas.Children.Add(clusterModeSelectorPanel);
            }

            var layoutGrid = new Grid { Margin = new Thickness(12, 8, 4, 12) };
            // Default text in the whole cluster window follows the theme (band/mode labels, legend,
            // counts). Colored/explicit text overrides it. Resource reference => live toggle.
            layoutGrid.SetResourceReference(TextElement.ForegroundProperty, "TextBrush");
            layoutGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layoutGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layoutGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            Grid.SetRow(headerGrid, 0);
            Grid.SetRow(headerCanvas, 1);
            Grid.SetRow(spotsGrid, 2);
            layoutGrid.Children.Add(headerGrid);
            layoutGrid.Children.Add(headerCanvas);
            layoutGrid.Children.Add(spotsGrid);

            // Outer grid keeps the custom title bar flush with the frame edge (layoutGrid has its
            // own inset margin, above, so nesting the title bar inside it would push the bar in too).
            var clusterOuter = new Grid();
            clusterOuter.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            clusterOuter.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var clusterTitleBar = BuildClusterTitleBar();
            Grid.SetRow(clusterTitleBar, 0);
            Grid.SetRow(layoutGrid, 1);
            clusterOuter.Children.Add(clusterTitleBar);
            clusterOuter.Children.Add(layoutGrid);
            // Clip content to the frame's rounded rectangle so the top corners (title bar) follow
            // the curve like the bottom ones (radius 8 minus the 2px border => 6).
            clusterOuter.SizeChanged += (s, e) =>
                clusterOuter.Clip = new RectangleGeometry(
                    new Rect(0, 0, clusterOuter.ActualWidth, clusterOuter.ActualHeight), 6, 6);

            var clusterFrame = new Border
            {
                BorderBrush = Brushes.White,
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(8),
                Child = clusterOuter
            };

            clusterWindow = new Window
            {
                Title = "Cluster",
                WindowStyle = WindowStyle.None,
                Width = Properties.Settings.Default.ClusterWindowWidth > 0 ? Properties.Settings.Default.ClusterWindowWidth : 600,
                Height = Properties.Settings.Default.ClusterWindowHeight > 0 ? Properties.Settings.Default.ClusterWindowHeight : 400,
                MinWidth = 355,   // narrowest width the user chose (band row fully visible)
                MinHeight = 260,
                Left = Properties.Settings.Default.ClusterWindowLeft,
                Top = Properties.Settings.Default.ClusterWindowTop,
                Content = clusterFrame
            };
            clusterWindow.Owner = this;
            // Resource reference, not a brush snapshot, so the body follows live scheme switches.
            clusterWindow.SetResourceReference(Window.BackgroundProperty, "WindowBg");

            // Same custom-chrome setup as MainWindow: CaptionHeight matches the title bar we just
            // built, so drag/double-click-maximize/Aero snap all keep working via the app-drawn bar.
            System.Windows.Shell.WindowChrome.SetWindowChrome(clusterWindow, new System.Windows.Shell.WindowChrome
            {
                CaptionHeight = 32,
                CornerRadius = new CornerRadius(0),
                GlassFrameThickness = new Thickness(0),
                ResizeBorderThickness = new Thickness(6),
                UseAeroCaptionButtons = false
            });
            clusterWindow.StateChanged += ClusterWindow_StateChanged;

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
            undoButton.PreviewMouseRightButtonUp += ClusterUndoButton_RightClick;

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

        // Custom title bar for the Cluster window (built in code since the window itself is), mirroring
        // MainWindow's XAML title bar: icon, "Cluster" title, minimize/maximize/close buttons using the
        // app-wide CaptionButtonStyle/CaptionCloseButtonStyle from Themes/Controls.xaml.
        private Border BuildClusterTitleBar()
        {
            var minimizeBtn = new Button
            {
                Content = "\uE921",
                Style = Application.Current.Resources["CaptionButtonStyle"] as Style,
                ToolTip = "Minimize"
            };
            System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(minimizeBtn, true);
            minimizeBtn.Click += (s, e) => System.Windows.SystemCommands.MinimizeWindow(clusterWindow);

            clusterMaxRestoreBtn = new Button
            {
                Content = "\uE922",
                Style = Application.Current.Resources["CaptionButtonStyle"] as Style,
                ToolTip = "Maximize"
            };
            System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(clusterMaxRestoreBtn, true);
            clusterMaxRestoreBtn.Click += (s, e) =>
            {
                if (clusterWindow.WindowState == WindowState.Maximized) System.Windows.SystemCommands.RestoreWindow(clusterWindow);
                else System.Windows.SystemCommands.MaximizeWindow(clusterWindow);
            };

            var closeBtn = new Button
            {
                Content = "\uE8BB",
                Style = Application.Current.Resources["CaptionCloseButtonStyle"] as Style,
                ToolTip = "Close"
            };
            System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(closeBtn, true);
            closeBtn.Click += (s, e) => System.Windows.SystemCommands.CloseWindow(clusterWindow);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(minimizeBtn);
            buttons.Children.Add(clusterMaxRestoreBtn);
            buttons.Children.Add(closeBtn);
            DockPanel.SetDock(buttons, Dock.Right);

            var icon = new Image { Source = new BitmapImage(new Uri("Images/crown.png", UriKind.Relative)), Width = 16, Height = 16, Margin = new Thickness(10, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            // Resource references (not brush snapshots) so the title bar follows a live scheme
            // switch -- a snapshot froze whatever theme was active when the window opened, leaving
            // a dark bar on a light scheme after toggling.
            var titleText = new TextBlock { Text = "Cluster", FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            titleText.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");

            var dock = new DockPanel { LastChildFill = true };
            dock.Children.Add(buttons);
            dock.Children.Add(icon);
            dock.Children.Add(titleText);

            var bar = new Border { Height = 32, Child = dock };
            bar.SetResourceReference(Border.BackgroundProperty, "WindowBg");
            return bar;
        }

        // Keeps the Cluster window's maximize/restore glyph in sync, same reasoning as MainWindow_StateChanged.
        private void ClusterWindow_StateChanged(object sender, EventArgs e)
        {
            if (clusterMaxRestoreBtn == null) return;
            bool maximized = clusterWindow.WindowState == WindowState.Maximized;
            clusterMaxRestoreBtn.Content = maximized ? "\uE923" : "\uE922";
            clusterMaxRestoreBtn.ToolTip = maximized ? "Restore Down" : "Maximize";
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
            try { Properties.Settings.Default.Save(); } catch (System.Exception swallowed) { Log.Swallow(swallowed); }

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

            try { _clusterWidthHandlerCleanup?.Invoke(); } catch (System.Exception swallowed) { Log.Swallow(swallowed); }
            _clusterWidthHandlerCleanup = null;
            clusterUndoButton = null;
            clusterUndoCountText = null;
            clusterSpotCountText = null;
            clusterSpotCountBadge = null;
            clusterLegendPanel = null;
            clusterNewCountryCountText = null;
            _clusterNewCountryBlinkTimer?.Stop();
            _clusterNewCountryBlinkTimer = null;
            _lastNewCountryCount = 0;
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
            clusterModeSelectorPanel = null;
            clusterBandSpotCountTexts.Clear();
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
                Opacity = 1.0,
                Content = undoContentGrid
            };
            // Opt out of the app-wide themed Button style (added for dark mode) whose Padding="12,5"
            // template squeezes/clips this 24px icon. Use the default template like before dark mode.
            undoButton.Style = null;

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
            clusterSpotsGrid = spotsGrid;
            // Themed via resource references so they live-update on Light/Dark toggle.
            spotsGrid.SetResourceReference(Control.BackgroundProperty, "GridRowBg");
            spotsGrid.SetResourceReference(Control.ForegroundProperty, "TextBrush");
            // Visible table gridlines in both themes.
            spotsGrid.GridLinesVisibility = DataGridGridLinesVisibility.All;
            spotsGrid.SetResourceReference(DataGrid.HorizontalGridLinesBrushProperty, "GridLine");
            spotsGrid.SetResourceReference(DataGrid.VerticalGridLinesBrushProperty, "GridLine");
            // Blue frame matching the main log table / entry-form / map frames (fixed blue, both themes).
            spotsGrid.BorderBrush = new SolidColorBrush(Color.FromRgb(0x15, 0x65, 0xC0));
            spotsGrid.BorderThickness = new Thickness(3);

            // Let the grid shrink below the sum of its columns (it scrolls/clips the overflow)
            // instead of forcing the whole window to stay wide enough for every column.
            spotsGrid.MinWidth = 0;
            ScrollViewer.SetHorizontalScrollBarVisibility(spotsGrid, ScrollBarVisibility.Auto);
            // A DataGrid always reports its full column width as its desired size, which would pin
            // the window open. Cap its MaxWidth to its container's actual width so its desired size
            // can't exceed what's available — the window can then narrow down to the header.
            var dgMaxWidthBinding = new System.Windows.Data.Binding("ActualWidth")
            {
                RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor)
                {
                    AncestorType = typeof(Grid),
                    AncestorLevel = 1
                }
            };
            spotsGrid.SetBinding(FrameworkElement.MaxWidthProperty, dgMaxWidthBinding);


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

            // Default cell text follows the theme (Spotter/Country/UTC/Comment columns; the DX/Freq/
            // Mode columns override with their own semantic colors). DynamicResource so it live-
            // updates on Light/Dark toggle. Selected rows use the accent selection colors.
            var clusterCellStyle = new Style(typeof(DataGridCell));
            clusterCellStyle.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("TextBrush")));
            clusterCellStyle.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
            clusterCellStyle.Setters.Add(new Setter(Control.BorderBrushProperty, Brushes.Transparent));
            var clusterCellSelTrigger = new Trigger { Property = DataGridCell.IsSelectedProperty, Value = true };
            clusterCellSelTrigger.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("SelectionBg")));
            clusterCellSelTrigger.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("SelectionText")));
            clusterCellStyle.Triggers.Add(clusterCellSelTrigger);
            spotsGrid.CellStyle = clusterCellStyle;

            // Same rule as the QSO table header: the LogHeaderBg palette token (designer burlywood
            // in every scheme; user-overridable via Customize Colors) with black text. Dynamic, so
            // scheme switches and color edits repaint the header live.
            var clusterColumnHeaderStyle = new Style(typeof(DataGridColumnHeader));
            clusterColumnHeaderStyle.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("LogHeaderBg")));
            clusterColumnHeaderStyle.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.Black));
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
            var dxColumn = new DataGridTemplateColumn { Header = "DX", HeaderStyle = dxHeaderStyle, CellTemplate = dxColumnTemplate, SortMemberPath = "DXCallsign", Width = new DataGridLength(83) };

            // Spotter / Country columns
            var spotterColumn = new DataGridTextColumn { Header = "Spotter", HeaderStyle = clusterColumnHeaderStyle, Binding = new System.Windows.Data.Binding("SpotterCallsign"), Width = new DataGridLength(61) };
            var countryColumn = new DataGridTextColumn { Header = "Country", HeaderStyle = clusterColumnHeaderStyle, Binding = new System.Windows.Data.Binding("Country"), Width = new DataGridLength(76) };

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
            var commentColumn = new DataGridTextColumn { Header = "Comment", HeaderStyle = commentHeaderStyle, Binding = new System.Windows.Data.Binding("Comment"), MinWidth = 60, Width = DataGridLength.Auto };

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
            var flagColumn = new DataGridTemplateColumn { Header = "Flag", HeaderStyle = flagHeaderStyle, CellTemplate = flagTemplate, Width = DataGridLength.Auto, CanUserResize = false };

            // Store references needed by other methods
            clusterDxColumn = dxColumn;
            clusterSpotterColumn = spotterColumn;
            clusterFreqColumn = freqColumn;
            clusterUtcColumn = utcColumn;
            clusterCountryColumn = countryColumn;
            clusterModeColumn = modeColumn;
            clusterCommentColumn = commentColumn;

            utcColumn.SortDirection = ListSortDirection.Descending;

            // Stable keys for persisting column ORDER. (Widths are never persisted — every column
            // starts at its hard-coded/Auto default each time the window opens.)
            clusterColumnKeys.Clear();
            clusterColumnKeys[dxColumn] = "DX";
            clusterColumnKeys[flagColumn] = "Flag";
            clusterColumnKeys[spotterColumn] = "Spotter";
            clusterColumnKeys[countryColumn] = "Country";
            clusterColumnKeys[freqColumn] = "Freq";
            clusterColumnKeys[utcColumn] = "UTC";
            clusterColumnKeys[modeColumn] = "Mode";
            clusterColumnKeys[commentColumn] = "Comment";

            spotsGrid.Columns.Add(dxColumn);
            spotsGrid.Columns.Add(flagColumn);
            spotsGrid.Columns.Add(spotterColumn);
            spotsGrid.Columns.Add(countryColumn);
            spotsGrid.Columns.Add(freqColumn);
            spotsGrid.Columns.Add(utcColumn);
            spotsGrid.Columns.Add(modeColumn);
            spotsGrid.Columns.Add(commentColumn);

            // Restore the user's saved column order (order persists across sessions; widths do not).
            ApplyClusterColumnOrder(spotsGrid);

            if (clusterVisibleSpots == null)
            {
                clusterVisibleSpots = new ObservableCollection<ClusterSpotViewItem>();
            }
            spotsGrid.ItemsSource = clusterVisibleSpots;
            // Pin "New Country" (needed) spots to the top regardless of the active column sort:
            // IsNeededCountry is the live primary sort key, the user's column choice is secondary.
            spotsGrid.Sorting += ClusterSpotsGrid_Sorting;
            var clusterView = System.Windows.Data.CollectionViewSource.GetDefaultView(clusterVisibleSpots) as System.Windows.Data.ListCollectionView;
            if (clusterView != null)
            {
                ApplyClusterSort(clusterView, "UnixTime", System.ComponentModel.ListSortDirection.Descending);
                clusterView.IsLiveSorting = true;
                if (!clusterView.LiveSortingProperties.Contains("IsNeededCountry"))
                    clusterView.LiveSortingProperties.Add("IsNeededCountry");
            }
            spotsGrid.PreviewMouseLeftButtonDown += ClusterSpotsGrid_MouseLeftButtonDown;
            spotsGrid.MouseMove += ClusterSpotsGrid_MouseMove;
            spotsGrid.MouseLeave += ClusterSpotsGrid_MouseLeave;
            spotsGrid.SizeChanged += (s, e) => RequestClusterHeaderAlignmentRefresh();
            spotsGrid.ColumnReordered += (s, e) =>
            {
                SaveClusterColumnOrder(spotsGrid);   // remember the order the user dragged to
                RequestClusterHeaderAlignmentRefresh();
            };
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

        // Re-sort the cluster spots view so "New Country" (needed) spots are always on top, then
        // the rest by the requested column/direction. Setting IsNeededCountry as the first
        // SortDescription is what overrides any column sort for those rows only.
        private void ApplyClusterSort(System.Windows.Data.ListCollectionView view, string memberPath, System.ComponentModel.ListSortDirection direction)
        {
            if (view == null) return;
            using (view.DeferRefresh())
            {
                view.SortDescriptions.Clear();
                view.SortDescriptions.Add(new System.ComponentModel.SortDescription("IsNeededCountry", System.ComponentModel.ListSortDirection.Descending));
                if (!string.IsNullOrEmpty(memberPath) && memberPath != "IsNeededCountry")
                    view.SortDescriptions.Add(new System.ComponentModel.SortDescription(memberPath, direction));
            }
        }

        // Intercept column-header sorting on the cluster grid so "New Country" spots stay pinned at
        // the top and the clicked column only sorts the remaining rows.
        private void ClusterSpotsGrid_Sorting(object sender, DataGridSortingEventArgs e)
        {
            var grid = sender as DataGrid;
            if (grid == null || e.Column == null || string.IsNullOrEmpty(e.Column.SortMemberPath))
                return;

            var view = System.Windows.Data.CollectionViewSource.GetDefaultView(grid.ItemsSource) as System.Windows.Data.ListCollectionView;
            if (view == null) return;

            var direction = (e.Column.SortDirection != System.ComponentModel.ListSortDirection.Ascending)
                ? System.ComponentModel.ListSortDirection.Ascending
                : System.ComponentModel.ListSortDirection.Descending;

            ApplyClusterSort(view, e.Column.SortMemberPath, direction);

            foreach (var col in grid.Columns) col.SortDirection = null;
            e.Column.SortDirection = direction;
            e.Handled = true;
        }

        private Grid BuildClusterHeaderPanel(Button undoButton)
        {
            // Four legend lines with an even 5px gap. New Country shares its line with its big
            // counter (its own TextBlock, kept right next to the text). The counter uses negative
            // vertical margins so its FontSize-22 glyph does NOT make that line taller than the rest.
            var legendPanel = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, -1, 4, 0)
            };
            clusterLegendPanel = legendPanel;

            var newCountryCountText = new TextBlock
            {
                Text = "0",
                Foreground = ThemeManager.Brush("TextBrush"),   // 0 → theme text; turns red when new countries appear
                FontWeight = FontWeights.Bold,
                FontSize = 22,
                VerticalAlignment = VerticalAlignment.Center,
                // negative top/bottom keep this tall glyph from growing the New Country line
                Margin = new Thickness(4, -7, 0, -7)
            };
            clusterNewCountryCountText = newCountryCountText;

            var newCountryRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top
            };
            newCountryRow.Children.Add(BuildClusterLegendItem(Brushes.Red, "New Country", false, new Thickness(0, 0, 0, 0)));
            newCountryRow.Children.Add(newCountryCountText);
            legendPanel.Children.Add(newCountryRow);

            legendPanel.Children.Add(BuildClusterLegendItem(new SolidColorBrush(Color.FromRgb(0x00, 0x7A, 0xCC)), "Worked Before", false, new Thickness(0, 5, 0, 0)));
            legendPanel.Children.Add(BuildClusterLegendItem(ThemeManager.Brush("TextBrush"), "Worked Country", false, new Thickness(0, 5, 0, 0)));

            var onMyFreqLegend = BuildClusterLegendItem(new SolidColorBrush(Color.FromRgb(0x90, 0xEE, 0x90)), "On My Radio Freq", true, new Thickness(0, 5, 0, 0));
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

            // Colored band selector + undo icon: left-justified, on their own row ABOVE the legend
            // block (in the space freed by moving New Country down). No longer anchored to the right.
            var bandSelectorPanel = BuildClusterBandSelectorPanel();
            bandSelectorPanel.Margin = new Thickness(0, 0, 0, 0);
            bandSelectorPanel.HorizontalAlignment = HorizontalAlignment.Left;
            bandSelectorPanel.VerticalAlignment = VerticalAlignment.Center;
            clusterBandSelectorPanel = bandSelectorPanel;

            undoButton.VerticalAlignment = VerticalAlignment.Center;
            var bandRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                // top -9 moves the bands+labels up 9px; bottom is the gap below the per-band count
                // line before the legend.
                Margin = new Thickness(0, -9, 0, 6)
            };
            bandRow.Children.Add(bandSelectorPanel);
            bandRow.Children.Add(undoButton);

            // Left block: band row on top, then the legend block (lines + counter) — both start at
            // the same left edge, so the bands are left-justified with the New Country legend.
            var leftColumnPanel = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top
            };
            leftColumnPanel.Children.Add(bandRow);
            leftColumnPanel.Children.Add(legendPanel);

            // Cap the header block to its container width so it (like the table) cannot force the
            // window to stay wide enough for the whole band row. Past the band row's natural width
            // the bands clip on the right, letting the window narrow further.
            var leftMaxBinding = new System.Windows.Data.Binding("ActualWidth")
            {
                RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor)
                {
                    AncestorType = typeof(Grid),
                    AncestorLevel = 1
                }
            };
            leftColumnPanel.SetBinding(FrameworkElement.MaxWidthProperty, leftMaxBinding);

            // Mode checkboxes are positioned on the header canvas (see
            // UpdateClusterActiveBandIndicatorPosition): same row as the Selected/All Bands buttons,
            // right-justified to the undo icon. Not added to the header grid.
            var modeSelectorPanel = BuildClusterModeSelectorPanel();
            modeSelectorPanel.Margin = new Thickness(0, 0, 0, 0);
            modeSelectorPanel.HorizontalAlignment = HorizontalAlignment.Left;
            modeSelectorPanel.VerticalAlignment = VerticalAlignment.Top;
            clusterModeSelectorPanel = modeSelectorPanel;

            var rightColumnPanel = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 0, -12, 0)
            };

            // Three columns:
            //   0 = left block (band row + legend) -> Auto, left-justified, never clipped
            //   1 = spacer                         -> Star, the empty gap that shrinks on resize
            //   2 = mode selector                  -> Auto, stays on the right
            // The window's MinWidth (set in the Loaded handler below) stops the drag once the
            // spacer reaches zero, so the left block and mode selector always stay fully visible.
            var headerGrid = new Grid { Margin = new Thickness(0, 0, 0, 0) };
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(leftColumnPanel, 0);
            Grid.SetColumn(rightColumnPanel, 2);
            headerGrid.Children.Add(leftColumnPanel);
            headerGrid.Children.Add(rightColumnPanel);

            // The header block and the table are both capped to their container, so neither forces
            // the window width. The window's only floor is its fixed MinWidth (set at creation).

            return headerGrid;
        }

        private StackPanel BuildClusterLegendTopRow()
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                // negative bottom absorbs the dead space the tall FontSize-22 counter adds below
                // the "New Country" text, so the gap to Worked Before matches the others (5px).
                Margin = new Thickness(0, 8, 0, -6)
            };
            row.Children.Add(BuildClusterLegendItem(Brushes.Red, "New Country", false, new Thickness(0, 0, 6, 0)));

            var countText = new TextBlock
            {
                Text = "0",
                Foreground = Brushes.Black,   // starts at 0 → black; turns red when new countries appear
                FontWeight = FontWeights.Bold,
                FontSize = 22,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 18, 0)
            };
            clusterNewCountryCountText = countText;
            row.Children.Add(countText);

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
            clusterBandSpotCountTexts.Clear();   // rebuilt below for this window instance
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
            try { Properties.Settings.Default.Save(); } catch (System.Exception swallowed) { Log.Swallow(swallowed); }
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
                Margin = new Thickness(0, 0, 0, 0)
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

            // The spot-count badge is no longer part of this UTC-anchored panel; it floats on the
            // header canvas between the band-filter group and this dropdown (positioned in
            // UpdateClusterActiveBandIndicatorPosition). The dropdown itself stays attached to UTC.
            clusterSpotCountBadge = new Border
            {
                Width = 34,
                Height = 34,
                CornerRadius = new CornerRadius(17),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x8C, 0x00)),
                BorderThickness = new Thickness(2),
                Background = Brushes.Transparent,
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
                        try { widthDescriptor.RemoveValueChanged(col, handler); } catch (System.Exception swallowed) { Log.Swallow(swallowed); }
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

            double utcStart = GetClusterColumnLeft(clusterUtcColumn);
            double horizontalOffset = clusterSpotsScrollViewer != null ? clusterSpotsScrollViewer.HorizontalOffset : 0;

            // The band-filter group (Selected / All Bands / Active Band + band indicator) is anchored
            // to the LEFT, just past the legend's right edge — independent of the Freq column and of
            // the total window width, so resizing/reordering grid columns no longer moves it.
            // Vertical target inside the unit: Active Band button bottom aligned to dropdown bottom.
            if (clusterShowBandsPanel != null && clusterHeaderCanvas != null)
            {
                double panelWidth = clusterShowBandsPanel.ActualWidth > 0 ? clusterShowBandsPanel.ActualWidth : ClusterShowBandsPanelWidth;

                double legendRight = 0;
                if (clusterLegendPanel != null)
                {
                    try
                    {
                        Point legendRightInCanvas = clusterLegendPanel.TranslatePoint(
                            new Point(clusterLegendPanel.ActualWidth, 0), clusterHeaderCanvas);
                        legendRight = legendRightInCanvas.X;
                    }
                    catch
                    {
                        legendRight = 0;
                    }
                }

                double panelLeft = legendRight + ClusterLegendToBandGroupGap;
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
                    }
                    catch
                    {
                    }
                }

                Canvas.SetTop(clusterShowBandsPanel, showTop);

                // Spot-count badge floats just to the right of the band-filter group; its bottom is
                // pinned a small gap above the table top (canvas-Y == ClusterTableTopGap), so it never
                // touches the grid header.
                double tableTopInCanvas = ClusterTableTopGap;
                if (clusterSpotCountBadge != null)
                {
                    double badgeLeft = panelLeft + panelWidth + ClusterBandGroupToCounterGap;
                    double badgeTop = tableTopInCanvas - ClusterControlsToTableGap - clusterSpotCountBadge.Height;
                    Canvas.SetLeft(clusterSpotCountBadge, badgeLeft);
                    Canvas.SetTop(clusterSpotCountBadge, badgeTop);
                }

                // Mode checkboxes: right edge aligned to the right edge of the band checkboxes (the
                // last band, not the undo icon), and the whole block sits ABOVE the Selected/All
                // Bands buttons (its bottom a couple px above the Selected button top) so it never
                // overlaps them when the window is narrow.
                if (clusterModeSelectorPanel != null && clusterBandFilterPreSelectedBtn != null && clusterBandSelectorPanel != null)
                {
                    try
                    {
                        Point selBtnInPanel = clusterBandFilterPreSelectedBtn.TranslatePoint(new Point(0, 0), clusterShowBandsPanel);
                        double selBtnTopInCanvas = showTop + selBtnInPanel.Y;
                        double modesTop = selBtnTopInCanvas - clusterModeSelectorPanel.ActualHeight - 2;

                        Point bandsTopRight = clusterBandSelectorPanel.TranslatePoint(new Point(clusterBandSelectorPanel.ActualWidth, 0), clusterHeaderCanvas);
                        double modesLeft = bandsTopRight.X - clusterModeSelectorPanel.ActualWidth;

                        Canvas.SetLeft(clusterModeSelectorPanel, modesLeft);
                        Canvas.SetTop(clusterModeSelectorPanel, modesTop);
                    }
                    catch
                    {
                    }
                }

                // The Last/dropdown stays anchored to UTC horizontally (set below), but its bottom is
                // pinned the same small gap above the table top so the frame nearly meets the grid.
                if (clusterLastMinutesFilterPanel != null)
                {
                    double dropH = clusterLastMinutesFilterPanel.ActualHeight > 0 ? clusterLastMinutesFilterPanel.ActualHeight : 40;
                    Canvas.SetTop(clusterLastMinutesFilterPanel, tableTopInCanvas - ClusterControlsToTableGap - dropH);
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
                        try { MapControl?.HighlightSpot(hoveredCall); } catch (System.Exception swallowed) { Log.Swallow(swallowed); }
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
                try { MapControl?.ClearSpotHighlight(); } catch (System.Exception swallowed) { Log.Swallow(swallowed); }
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

        private void ClusterWindow_LocationChanged(object sender, EventArgs e)
        {
            if (clusterWindow == null)
            {
                return;
            }

            Properties.Settings.Default.ClusterWindowLeft = clusterWindow.Left;
            Properties.Settings.Default.ClusterWindowTop = clusterWindow.Top;
            SettingsFlush.RequestSave();   // fires per pixel while dragging; debounce the disk write
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

        private string GetClusterColumnOrderSettingPath()
        {
            string baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HolyLogger");
            return Path.Combine(baseDir, "cluster-col-order.txt");
        }

        // Persists ONLY the column order (comma-separated stable keys, left-to-right). Column widths
        // are intentionally not saved, so they reset to their defaults each time the window opens.
        private void SaveClusterColumnOrder(DataGrid grid)
        {
            if (grid == null) return;
            try
            {
                var order = grid.Columns
                    .Where(c => clusterColumnKeys.ContainsKey(c))
                    .OrderBy(c => c.DisplayIndex)
                    .Select(c => clusterColumnKeys[c]);
                string content = string.Join(",", order);
                string path = GetClusterColumnOrderSettingPath();
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(path, content);
            }
            catch
            {
            }
        }

        private void ApplyClusterColumnOrder(DataGrid grid)
        {
            if (grid == null) return;
            try
            {
                string path = GetClusterColumnOrderSettingPath();
                if (!File.Exists(path)) return;
                string content = File.ReadAllText(path).Trim();
                if (string.IsNullOrWhiteSpace(content)) return;

                var keys = content.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                int index = 0;
                foreach (var key in keys)
                {
                    var col = grid.Columns.FirstOrDefault(c =>
                        clusterColumnKeys.TryGetValue(c, out var k) &&
                        string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
                    if (col != null)
                    {
                        col.DisplayIndex = index;
                        index++;
                    }
                }
            }
            catch
            {
            }
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
            SettingsFlush.RequestSave();   // fires per pixel while resizing; debounce the disk write
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
            catch (System.Exception swallowed) { Log.Swallow(swallowed); }
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

                    // Fall back to the spotter's DXCC-entity location when the cluster didn't send
                    // spotter_loc, so the spotter->DX arc can be drawn (and highlighted on hover)
                    // for every spot, not only the few that carry exact spotter coordinates.
                    if ((!spotterLat.HasValue || !spotterLon.HasValue) && !string.IsNullOrWhiteSpace(spotter))
                    {
                        try
                        {
                            var spDxcc = rem.GetDXCC(spotter.Trim());
                            if (spDxcc != null && !string.IsNullOrWhiteSpace(spDxcc.Locator))
                            {
                                var spll = MaidenheadLocator.LocatorToLatLng(spDxcc.Locator);
                                spotterLat = spll.Lat;
                                spotterLon = spll.Long;
                            }
                        }
                        catch (System.Exception swallowed) { Log.Swallow(swallowed); }
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
            catch (System.Exception swallowed) { Log.Swallow(swallowed); }
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

        private bool IsClusterCallsignInLog(string dxCallsign)
        {
            string target = (dxCallsign ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(target) || Qsos == null)
            {
                return false;
            }

            return Qsos.Any(q => string.Equals((q.DXCall ?? string.Empty).Trim(), target, StringComparison.OrdinalIgnoreCase));
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

                UpdateClusterSpotCountIndicator();
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
            UpdateClusterBandSpotCounts();
            RequestClusterHeaderAlignmentRefresh();
            UpdateClusterSpotsOnMap();
        }

        // Per-band spot counts shown under each band checkbox. Counts spots on each band within the
        // current "Last" time window and respecting the mode filter, independent of which bands are
        // enabled. Recomputed on every RefreshClusterVisibleSpots (new spot or "Last" change).
        private void UpdateClusterBandSpotCounts()
        {
            if (clusterBandSpotCountTexts == null || clusterBandSpotCountTexts.Count == 0)
            {
                return;
            }

            long cutoff = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (clusterLastMinutesFilterValue * 60L);
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (clusterAllSpots != null)
            {
                foreach (var s in clusterAllSpots)
                {
                    if (s.UnixTime <= 0 || s.UnixTime < cutoff)
                        continue;
                    if (!IsClusterModeEnabled(s.Mode))
                        continue;
                    if (string.IsNullOrWhiteSpace(s.BandText))
                        continue;
                    counts.TryGetValue(s.BandText, out int c);
                    counts[s.BandText] = c + 1;
                }
            }

            foreach (var kv in clusterBandSpotCountTexts)
            {
                counts.TryGetValue(kv.Key, out int c);
                kv.Value.Text = c.ToString(CultureInfo.InvariantCulture);
            }
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

            if (clusterNewCountryCountText != null)
            {
                int newCountry = clusterVisibleSpots != null
                    ? clusterVisibleSpots.Count(s => s.IsNeededCountry)
                    : 0;
                clusterNewCountryCountText.Text = newCountry.ToString(CultureInfo.InvariantCulture);
                // Black when there are no new countries, red when there are.
                clusterNewCountryCountText.Foreground = newCountry > 0 ? (Brush)Brushes.Red : ThemeManager.Brush("TextBrush");
                if (newCountry > _lastNewCountryCount)
                    StartNewCountryBlink();
                _lastNewCountryCount = newCountry;
            }
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
            // Remember this spot's spotter so the map's DE button can center on it (SetAzimuth,
            // triggered by the DX callsign below, reads these).
            _selectedSpotterLat = spot.SpotterLat;
            _selectedSpotterLon = spot.SpotterLon;
            _selectedSpotterDxCall = (spot.DXCallsign ?? string.Empty).Trim().ToUpperInvariant();

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

        private void ClusterUndoButton_RightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (clusterUndoStates.Count == 0) return;
            e.Handled = true;

            var resetBtn = new Button
            {
                Content = "Reset undo list",
                Padding = new Thickness(14, 7, 14, 7),
                FontSize = 13,
                Cursor = Cursors.Hand,
                // Dark red instead of the old light pink: this button never set its own Foreground, so
                // it picks up the app-wide Button style's theme text color, which is white in dark mode
                // -- white-on-light-pink was nearly unreadable. Foreground is now explicit (not
                // theme-driven) so it stays readable in both light and dark mode against this background.
                Background = new SolidColorBrush(Color.FromRgb(0x8B, 0x22, 0x22)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xE5, 0x73, 0x73)),
                BorderThickness = new Thickness(1)
            };

            var popup = new System.Windows.Controls.Primitives.Popup
            {
                PlacementTarget = clusterUndoButton,
                Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
                StaysOpen = false,
                AllowsTransparency = true,
                Child = new Border
                {
                    Background = Brushes.White,
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6),
                    Child = resetBtn
                }
            };

            resetBtn.Click += (s, ev) =>
            {
                popup.IsOpen = false;
                ResetClusterUndo();
            };

            popup.PreviewKeyDown += (s, ev) =>
            {
                if (ev.Key == System.Windows.Input.Key.Escape)
                {
                    popup.IsOpen = false;
                    ev.Handled = true;
                }
            };

            popup.IsOpen = true;
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
            clusterUndoButton.Opacity = 1.0;   // full opacity so the icon's white background stays truly white

            if (clusterUndoCountText != null)
            {
                clusterUndoCountText.Text = hasUndo ? clusterUndoStates.Count.ToString(CultureInfo.InvariantCulture) : string.Empty;
            }
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

        // (The old ApplyClusterTableHeaderBackgroundFromSettings is gone: the header style's
        // Background is a DynamicResource on the LogHeaderBg token, so scheme switches and
        // Customize Colors edits repaint it without any re-apply.)
    }
}
