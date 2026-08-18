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
        BulkObservableCollection<ClusterSpotViewItem> clusterVisibleSpots = null;

        // ObservableCollection whose ReplaceAll swaps the whole content with ONE Reset notification.
        // The spot list is rebuilt on every arrival batch; Clear()+Add() per item fired hundreds of
        // CollectionChanged events into the live-sorted DataGrid per refresh — this makes it one.
        internal sealed class BulkObservableCollection<T> : ObservableCollection<T>
        {
            public void ReplaceAll(IEnumerable<T> items)
            {
                CheckReentrancy();
                Items.Clear();
                if (items != null)
                    foreach (T item in items) Items.Add(item);
                OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs("Count"));
                OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs("Item[]"));
                OnCollectionChanged(new System.Collections.Specialized.NotifyCollectionChangedEventArgs(
                    System.Collections.Specialized.NotifyCollectionChangedAction.Reset));
            }
        }
        // While the mouse hovers a band checkbox, the cluster temporarily shows ONLY that band's
        // spots (table + map), as if it were the active band; cleared when the mouse leaves.
        string _clusterHoverBandOverride = null;

        // While hovering a band checkbox, the DX GUI is hidden so the map/table preview that band
        // instead of the filled station's path. True for the whole time the mouse is over any band
        // checkbox; while true the on-frequency auto-fill/clear leave the DX box alone. The station that
        // was showing before the hover is snapshotted here and restored when the mouse leaves.
        bool _clusterBandHoverActive = false;
        string _clusterBandHoverSavedCall = null;
        // The band-checkbox row + a watchdog that ends the preview if its MouseLeave is ever missed
        // (otherwise the preview sticks and silently kills the on-frequency auto-fill).
        StackPanel clusterBandRowPanel = null;
        System.Windows.Threading.DispatcherTimer _clusterBandHoverWatchdog = null;
        HashSet<string> clusterWorkedCountries = null;
        TextBlock clusterActiveBandIndicatorText = null;
        Button clusterBandFilterAllBtn = null;
        Button clusterBandFilterPreSelectedBtn = null;
        Button clusterBandFilterActiveBtn = null;
        StackPanel clusterShowBandsPanel = null;
        TextBlock clusterShowBandsLabelText = null;
        TextBlock clusterNewCountryLegendText = null;
        TextBlock clusterNewCountryCountText = null;
        TextBlock clusterUnconfirmedCountText = null;   // counter for the "Unconfirmed" legend line
        DispatcherTimer _clusterNewCountryBlinkTimer = null;
        DateTime _clusterNewCountryBlinkStopTime;
        bool _clusterNewCountryBlinkOn = true;
        int _lastNewCountryCount = 0;
        DateTime _lastNewCountryAlertUtc = DateTime.MinValue;   // throttles the new-country alert sound
        DateTime _lastUnconfirmedAlertUtc = DateTime.MinValue;  // throttles the unconfirmed-spot alert sound
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

        // ── Live Scale (real-time frequency scale) ─────────────────────────────────────────────────
        // When on, the spot list is sorted by frequency (highest at top), the grid auto-scrolls so the
        // current radio frequency sits on a fixed center line, and manual scroll/sort are locked.
        bool clusterLiveScaleOn = false;
        Button clusterLiveScaleBtn = null;

        // "Latest report" toggle: when on, the list keeps only the newest spot per callsign+band
        // (collapsing repeated spots of the same station); off shows every spot in the time window.
        bool clusterLatestPerCallsignOn = Properties.Settings.Default.ClusterLatestPerCallsign;
        Button clusterLatestBtn = null;
        FrameworkElement clusterCenterLine = null;    // overlay that hosts the reference line (fills the table area)
        Grid clusterCenterLineBand = null;            // the movable strip (line + readout) positioned at the rows-area center
        TextBlock clusterCenterLineFreqText = null;   // live VFO frequency shown on the line
        System.Windows.Controls.Primitives.DataGridRowsPresenter clusterLiveScaleRowsHost = null;  // rows panel; carries the off-screen spacer margins
        int clusterLiveScaleAlignRetries = 0;         // guards the layout-not-ready retry loop of the scroll engine
        System.Windows.Threading.DispatcherTimer _centerLineRevealTimer = null;  // debounces revealing the Live Scale readout band until the table layout settles
        bool _centerLineRevealed = false;             // the band is shown only after its centered position has stabilized (no startup flash)
        string clusterPreLiveScaleBandFilterMode = null;  // band-filter mode to restore when Live Scale is turned off
        string clusterPreLiveScaleSortMember = "UnixTime";
        System.ComponentModel.ListSortDirection clusterPreLiveScaleSortDir = System.ComponentModel.ListSortDirection.Descending;

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
        // ONE shared radio-undo history. Selecting a station in the cluster OR in the log table pushes
        // to this single stack, and both undo controls act on it: the cluster title-bar button and the
        // main-GUI icon. The main icon is shown only while the list is non-empty.
        Stack<(string FrequencyText, string ModeText, string DxCallsignText)> logRadioUndoStates = new Stack<(string FrequencyText, string ModeText, string DxCallsignText)>();
        // Alias so the existing cluster-undo code drives the same shared list (never a separate stack).
        Stack<(string FrequencyText, string ModeText, string DxCallsignText)> clusterUndoStates => logRadioUndoStates;
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

        // How long one attempt to open the cluster connection may take before it is given up and
        // retried. Generous on purpose: a slow link that is going to succeed should be allowed to,
        // and failing early costs a full ten-second wait before the next try.
        private const int ClusterConnectTimeoutMs = 20000;

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
            clusterLiveScaleOn = false;   // engaged below (after the window is built) if it was remembered on
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

            // The spots table is wrapped in a host Grid together with the Live Scale center line, so the
            // line overlays the TABLE AREA only: it spans the table side to side and sits at exactly the
            // table frame's middle height (VerticalAlignment=Center of the host that fills row 2).
            var tableHost = new Grid();
            tableHost.Children.Add(spotsGrid);
            var centerLine = BuildClusterCenterLine();
            Panel.SetZIndex(centerLine, 50);
            tableHost.Children.Add(centerLine);
            // Keep the line on the rows-area center — and the table aligned to it — through any resize
            // (the spacer margins and scroll target both depend on the viewport height).
            spotsGrid.SizeChanged += (s, e) =>
            {
                PositionClusterCenterLine();
                ScrollClusterLiveScale();
                // While the band is still hidden on startup, keep pushing the reveal out until the table
                // stops resizing — so it's only shown once at its final centered position.
                if (clusterLiveScaleOn && !_centerLineRevealed)
                    StartCenterLineRevealDebounce();
            };

            Grid.SetRow(headerGrid, 0);
            Grid.SetRow(headerCanvas, 1);
            Grid.SetRow(tableHost, 2);
            layoutGrid.Children.Add(headerGrid);
            layoutGrid.Children.Add(headerCanvas);
            layoutGrid.Children.Add(tableHost);

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
            // Don't clear the shared history when the cluster window opens — it may already hold undo
            // states from the log table. Just sync the freshly-built button to the current count.
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

            // Live Scale is a remembered state: if it was on when the cluster was last used, re-engage it
            // now (after the window has laid out, so the grid/scroll measurements are real).
            if (Properties.Settings.Default.ClusterLiveScaleOn)
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!clusterLiveScaleOn) ToggleClusterLiveScale(userInitiated: false);
                }), System.Windows.Threading.DispatcherPriority.Loaded);

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

            // Cluster settings gear: sits in the empty title-bar gap, just left of the window buttons.
            // Clicking it opens the Cluster Settings window — the single home for the cluster's
            // display/behaviour settings (they used to be split between a small gear popup and two
            // different Options pages). Placed inside the caption-button group so nothing else in the
            // bar moves — it only fills space that was previously empty.
            var gearBtn = new Button
            {
                Content = "",   // Segoe MDL2 "Settings" gear glyph (same font as the caption buttons)
                Style = Application.Current.Resources["CaptionButtonStyle"] as Style,
                ToolTip = "Cluster settings",
                FontSize = 18,        // larger than the window glyphs so the gear stands out
                FontWeight = FontWeights.Bold
            };
            System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(gearBtn, true);
            gearBtn.Click += (s, e) => OpenClusterSettingsWindow();

            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(gearBtn);
            buttons.Children.Add(minimizeBtn);
            buttons.Children.Add(clusterMaxRestoreBtn);
            buttons.Children.Add(closeBtn);
            DockPanel.SetDock(buttons, Dock.Right);

            var icon = new Image { Source = new BitmapImage(new Uri("Images/crown.png", UriKind.Relative)), Width = 16, Height = 16, Margin = new Thickness(10, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            // Resource references (not brush snapshots) so the title bar follows a live scheme
            // switch -- a snapshot froze whatever theme was active when the window opened, leaving
            // a dark bar on a light scheme after toggling.
            var titleText = new TextBlock { Text = "Cluster", FontSize = 15, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center };
            titleText.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");

            var dock = new DockPanel { LastChildFill = true };
            dock.Children.Add(buttons);
            dock.Children.Add(icon);
            dock.Children.Add(titleText);

            var bar = new Border { Height = 32, Child = dock };
            bar.SetResourceReference(Border.BackgroundProperty, "TitleBarBg");
            return bar;
        }

        // The gear opens the Cluster Settings window: one home for the cluster's display/behaviour
        // settings. Single instance -- clicking the gear again focuses the open window.
        private void OpenClusterSettingsWindow()
        {
            // The two LoTW options are mutually exclusive. A stale config (or an older build) can have
            // both saved as on; if so, keep the mark and drop the filter so they never load both-checked.
            if (Properties.Settings.Default.ClusterShowLotw && Properties.Settings.Default.ClusterLotwOnly)
            {
                Properties.Settings.Default.ClusterLotwOnly = false;
                Properties.Settings.Default.Save();
            }

            if (clusterSettingsWindow != null)
            {
                clusterSettingsWindow.Activate();
                return;
            }

            var win = new ClusterSettingsWindow(this);
            clusterSettingsWindow = win;
            win.Closed += (s, e) => clusterSettingsWindow = null;
            win.Show();
        }

        // "Show LoTW" toggle: turn the yellow LoTW row highlight on/off. Repaint the grid so every
        // visible row re-reads RowBackground (which is gated on this setting).
        internal void SetClusterShowLotw(bool on)
        {
            Properties.Settings.Default.ClusterShowLotw = on;
            Properties.Settings.Default.Save();
            try { clusterSpotsGrid?.Items.Refresh(); } catch (System.Exception swallowed) { Log.Swallow(swallowed); }
        }

        // "LoTW only" toggle: show only LoTW spots. Re-run the visible-spot filter (which now honors it).
        internal void SetClusterLotwOnly(bool on)
        {
            Properties.Settings.Default.ClusterLotwOnly = on;
            Properties.Settings.Default.Save();
            RefreshClusterVisibleSpots();
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
            clusterLiveScaleOn = false;
            clusterLiveScaleBtn = null;
            clusterCenterLine = null;
            clusterCenterLineBand = null;
            clusterCenterLineFreqText = null;
            clusterLiveScaleRowsHost = null;
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
            // Keep the shared undo history when the cluster window closes — the main-GUI undo icon still
            // uses it. (Previously this stack was cluster-only and was cleared here.) Refresh the main
            // icon so it reflects the history now that the cluster button is going away.
            clusterWindow = null;
            UpdateLogRadioUndoButtonState();
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

            // Comment column (auto-width, so a centered header drifts with the widest comment — keep it left)
            var commentHeaderStyle = new Style(typeof(DataGridColumnHeader), clusterColumnHeaderStyle);
            commentHeaderStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Left));
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
                clusterVisibleSpots = new BulkObservableCollection<ClusterSpotViewItem>();
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
            // Right-click a spot: the menu that puts it on the Try Again list. PREVIEW, because the
            // DataGrid's own right-button handling selects rows and would otherwise get there first.
            spotsGrid.PreviewMouseRightButtonDown += ClusterSpotsGrid_MouseRightButtonDown;
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
                // Nudged up 5px (was -1) to make room for the extra "Unconfirmed" line without pushing
                // the rest of the header/table down.
                Margin = new Thickness(0, -6, 4, 0)
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

            // "Unconfirmed" (worked but not confirmed on LoTW) sits directly below New Country, with its
            // own big counter, mirroring the New Country line. Amber to distinguish from red New Country.
            var unconfirmedCountText = new TextBlock
            {
                Text = "0",
                Foreground = ThemeManager.Brush("TextBrush"),   // 0 → theme text; turns amber when >0
                FontWeight = FontWeights.Bold,
                FontSize = 22,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, -7, 0, -7)   // negative top/bottom keep the tall glyph from growing the line
            };
            clusterUnconfirmedCountText = unconfirmedCountText;

            var unconfirmedRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 3, 0, 0)
            };
            unconfirmedRow.Children.Add(BuildClusterLegendItem(new SolidColorBrush(Color.FromRgb(0xFF, 0x8C, 0x00)), "Unconfirmed", false, new Thickness(0, 0, 0, 0)));
            unconfirmedRow.Children.Add(unconfirmedCountText);
            legendPanel.Children.Add(unconfirmedRow);

            // Slightly tighter gaps (3 instead of 5) on the remaining lines so the new line barely moves
            // anything below the legend.
            legendPanel.Children.Add(BuildClusterLegendItem(new SolidColorBrush(Color.FromRgb(0x00, 0x7A, 0xCC)), "Worked Before", false, new Thickness(0, 3, 0, 0)));
            legendPanel.Children.Add(BuildClusterLegendItem(ThemeManager.Brush("TextBrush"), "Worked Country", false, new Thickness(0, 3, 0, 0)));

            var onMyFreqLegend = BuildClusterLegendItem(new SolidColorBrush(Color.FromRgb(0x90, 0xEE, 0x90)), "On My Radio Freq", true, new Thickness(0, 3, 0, 0));
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
            undoButton.HorizontalAlignment = HorizontalAlignment.Center;

            // "Latest" toggle sits directly UNDER the undo icon, at the per-band counter row level.
            var btnLatest = new Button
            {
                Content = "Latest",
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 0),
                Padding = new Thickness(6, 0, 6, 0),
                Style = MakeClusterBandFilterBtnStyle(clusterLatestPerCallsignOn),
                ToolTip = "Latest report per callsign: show only the newest spot for each callsign on each band, collapsing repeats. Off = every spot within the \"Last N min\" window."
            };
            clusterLatestBtn = btnLatest;
            btnLatest.Click += (s, e) => ToggleClusterLatestPerCallsign();

            // Undo icon on top (at the band-checkbox level), Latest toggle beneath it so it lands on
            // the per-band counter row. Top-aligned so the pair tracks the band cells, not the row center.
            undoButton.Margin = new Thickness(0, 0, 0, 0);   // was bottom 8; removed so Latest sits at the counter row
            var undoColumn = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(2, 0, 0, 0)
            };
            undoColumn.Children.Add(undoButton);
            undoColumn.Children.Add(btnLatest);

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
            bandRow.Children.Add(undoColumn);

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
                Margin = new Thickness(0, 0, 0, 0),
                // Transparent (not null) so the whole row — including the gaps between checkboxes — is a
                // hit-test target, and MouseLeave fires only when the mouse leaves the entire band group.
                Background = Brushes.Transparent
            };

            // End the hover preview only when the mouse leaves the WHOLE band row. Per-checkbox MouseLeave
            // must NOT end it (that fired in the gaps between checkboxes and briefly restored the station).
            row.MouseLeave += (s, e) => EndClusterBandHoverPreview();
            clusterBandRowPanel = row;   // the watchdog polls this to detect a MISSED MouseLeave

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
            UpdateClusterActiveBandIndicatorText();   // band in green/gray, or red "out of band"

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

            // Live Scale toggle: to the right of "All Bands", same key look as the band buttons;
            // highlighted (pressed) while engaged.
            var btnLiveScale = new Button
            {
                Content = "Live Scale",
                HorizontalAlignment = HorizontalAlignment.Left,
                // left margin 4 (same as Selected/All Bands) so its gap matches the others
                Margin = new Thickness(4, 2, 2, 4),
                Style = MakeClusterBandFilterBtnStyle(clusterLiveScaleOn),
                ToolTip = "Live frequency scale: the list scrolls so your current radio frequency stays on the center line, so you can see at a glance which way to turn the knob to reach a spot. Turns on Active band + frequency sort."
            };
            clusterLiveScaleBtn = btnLiveScale;
            btnLiveScale.Click += (s, e) => ToggleClusterLiveScale();

            var topButtonsRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            topButtonsRow.Children.Add(btnPreSelected);
            topButtonsRow.Children.Add(btnAllBands);
            topButtonsRow.Children.Add(btnLiveScale);
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
            // Live Scale forces the Active-band view; choosing "Selected" or "All Bands" is incompatible
            // with it, so a user click on those must first disengage Live Scale (Active Band is exempt —
            // it IS Live Scale's band mode). Only on a real click; the internal restore call that turns
            // Live Scale off passes userInitiated: false and must not recurse here.
            if (userInitiated && clusterLiveScaleOn && !string.Equals(newMode, "Active", StringComparison.OrdinalIgnoreCase))
                ToggleClusterLiveScale(userInitiated: false);

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
            if (clusterActiveBandIndicatorText != null)
            {
                UpdateClusterActiveBandIndicatorText();
                clusterActiveBandIndicatorText.Visibility = Visibility.Visible;
            }
            RefreshClusterVisibleSpots();
        }

        // ── Live Scale ─────────────────────────────────────────────────────────────────────────────

        // The horizontal reference line (+ live frequency readout). The overlay fills the table area; the
        // inner "band" strip is positioned by PositionClusterCenterLine at the exact vertical center of the
        // ROWS viewport (excluding the column headers and scrollbars), and re-positioned on every resize.
        // Non-hit-test so the spots underneath stay clickable. Hidden until Live Scale is engaged.
        private FrameworkElement BuildClusterCenterLine()
        {
            var container = new Grid { IsHitTestVisible = false, Visibility = Visibility.Collapsed };

            // Two thin parallel lines, one row height apart (the strip's Height is kept at the measured
            // row height by ScrollClusterLiveScale), framing the on-frequency row instead of striking
            // through it — the station text between them stays fully readable.
            var band = new Grid { Height = 24, VerticalAlignment = VerticalAlignment.Top };

            var topLine = new Border
            {
                Height = 1,
                Background = new SolidColorBrush(Color.FromRgb(0xE0, 0x00, 0x00)),
                SnapsToDevicePixels = true,
                UseLayoutRounding = true,
                VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            band.Children.Add(topLine);

            var bottomLine = new Border
            {
                Height = 1,
                Background = new SolidColorBrush(Color.FromRgb(0xE0, 0x00, 0x00)),
                SnapsToDevicePixels = true,
                UseLayoutRounding = true,
                VerticalAlignment = VerticalAlignment.Bottom,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            band.Children.Add(bottomLine);

            var freqText = new TextBlock
            {
                Text = string.Empty,
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                Background = new SolidColorBrush(Color.FromRgb(0xE0, 0x00, 0x00)),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 0, 22, 0),   // clear of the vertical scrollbar
                Padding = new Thickness(6, 1, 6, 1)
            };
            band.Children.Add(freqText);

            container.Children.Add(band);

            clusterCenterLine = container;
            clusterCenterLineBand = band;
            clusterCenterLineFreqText = freqText;
            return container;
        }

        // Puts the line at exactly the middle height of the rows area (the frame where the spots are
        // printed): finds the grid's ScrollContentPresenter — which excludes the column-header band and
        // the scrollbars — and aligns the strip's center to its vertical center. Cheap; safe to call often.
        private void PositionClusterCenterLine()
        {
            if (clusterCenterLine == null || clusterCenterLineBand == null || clusterSpotsGrid == null) return;
            if (clusterCenterLine.Visibility != Visibility.Visible) return;
            var rows = FindVisualChild<ScrollContentPresenter>(clusterSpotsGrid);
            if (rows == null || rows.ActualHeight <= 0) return;
            double centerY;
            try { centerY = rows.TransformToVisual(clusterCenterLine).Transform(new Point(0, rows.ActualHeight / 2.0)).Y; }
            catch { return; }
            double top = Math.Max(0, centerY - clusterCenterLineBand.Height / 2.0);
            if (Math.Abs(clusterCenterLineBand.Margin.Top - top) > 0.5)
                clusterCenterLineBand.Margin = new Thickness(0, top, 0, 0);
        }

        // Restart the debounce that reveals the Live Scale readout band. The band stays HIDDEN until the
        // table has stopped resizing for the interval, so it is only ever shown already-centered — no
        // startup flash at the top of the table while the window is still laying out.
        private void StartCenterLineRevealDebounce()
        {
            if (_centerLineRevealTimer == null)
            {
                _centerLineRevealTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(400)
                };
                _centerLineRevealTimer.Tick += (s, e) => { _centerLineRevealTimer.Stop(); RevealCenterLineNow(); };
            }
            _centerLineRevealTimer.Stop();
            _centerLineRevealTimer.Start();
        }

        private void RevealCenterLineNow()
        {
            if (!clusterLiveScaleOn || clusterCenterLineBand == null) return;

            var rows = FindVisualChild<ScrollContentPresenter>(clusterSpotsGrid);
            if (rows == null || rows.ActualHeight <= 0)
            {
                StartCenterLineRevealDebounce();   // layout still not ready — wait a little longer
                return;
            }

            PositionClusterCenterLine();   // final centered position
            ScrollClusterLiveScale();      // align the VFO row to it
            clusterCenterLineBand.Visibility = Visibility.Visible;
            _centerLineRevealed = true;
        }

        // Toggles the "Latest report per callsign+band" view (remembered across sessions) and refreshes.
        private void ToggleClusterLatestPerCallsign()
        {
            clusterLatestPerCallsignOn = !clusterLatestPerCallsignOn;
            Properties.Settings.Default.ClusterLatestPerCallsign = clusterLatestPerCallsignOn;
            try { Properties.Settings.Default.Save(); } catch (Exception swallowed) { Log.Swallow(swallowed); }
            if (clusterLatestBtn != null)
                clusterLatestBtn.Style = MakeClusterBandFilterBtnStyle(clusterLatestPerCallsignOn);
            RefreshClusterVisibleSpots();

            // The toggle rebuilds the ENTIRE row set at once, so under Live Scale every row has to
            // re-layout before the scroll engine can measure a row height. Reset its retry budget and
            // re-align after layout, or it parks on the blank top spacer (looks like "no spots").
            if (clusterLiveScaleOn)
            {
                clusterLiveScaleAlignRetries = 0;
                Dispatcher.BeginInvoke(new Action(UpdateClusterLiveScale), System.Windows.Threading.DispatcherPriority.Background);
            }
        }

        // Toggles Live Scale on/off: engages Active band + frequency sort (highest at top), shows the
        // center line and locks manual scroll/sort; turning off restores the previous mode + sort.
        private void ToggleClusterLiveScale(bool userInitiated = true)
        {
            // Engaging needs a valid active band: out of band there are no active-band spots, so turning
            // on would just present an empty list. On a CLICK, refuse with an explanation. On the silent
            // auto-restore of a remembered state, engage anyway — the red "out of band" label and the
            // empty table explain themselves, and spots appear as soon as the radio re-enters a band.
            if (!clusterLiveScaleOn && userInitiated)
            {
                string band = TB_Band != null ? (TB_Band.Text ?? string.Empty).Trim() : string.Empty;
                if (band.Length == 0)
                {
                    HolyMessageBox.ShowWarning(
                        "Live Scale needs the radio on a valid ham band.\n\n" +
                        "The current frequency is outside every band, so there is no active band to show. " +
                        "Tune inside a band (e.g. 14.200) and press Live Scale again.",
                        "Live Scale", clusterWindow);
                    return;
                }
            }

            clusterLiveScaleOn = !clusterLiveScaleOn;

            // Live Scale is a remembered state: reopening the cluster (or restarting the program)
            // restores it.
            Properties.Settings.Default.ClusterLiveScaleOn = clusterLiveScaleOn;
            SettingsFlush.RequestSave();

            if (clusterLiveScaleOn)
            {
                // Snapshot what to restore later: band-filter mode + the user's current sort.
                clusterPreLiveScaleBandFilterMode = Properties.Settings.Default.ClusterBandFilterMode ?? "PreSelected";
                var v0 = GetClusterSpotsView();
                if (v0 != null)
                    foreach (var sd in v0.SortDescriptions)
                        if (sd.PropertyName != "IsNeededCountry")
                        {
                            clusterPreLiveScaleSortMember = sd.PropertyName;
                            clusterPreLiveScaleSortDir = sd.Direction;
                            break;
                        }

                ApplyClusterBandFilterMode("Active", false);   // engage Active band (don't overwrite the saved preference)
                ApplyClusterLiveScaleSort();                    // frequency, highest at top
                SetClusterLiveScaleScrollSetup(true);           // program owns the scroll from here
                if (clusterCenterLine != null) clusterCenterLine.Visibility = Visibility.Visible;
                // Keep the readout band HIDDEN until the table layout has settled, so it never flashes at
                // the top of the table while the window is still growing on startup.
                if (clusterCenterLineBand != null) clusterCenterLineBand.Visibility = Visibility.Hidden;
                _centerLineRevealed = false;
                UpdateClusterLiveScale();                       // readout + first alignment
                Dispatcher.BeginInvoke(new Action(PositionClusterCenterLine),
                    System.Windows.Threading.DispatcherPriority.Loaded);
                StartCenterLineRevealDebounce();                // reveal the band only once sizes stop changing
            }
            else
            {
                _centerLineRevealTimer?.Stop();
                _centerLineRevealed = false;
                SetClusterLiveScaleScrollSetup(false);          // unlock scroll/sort, drop the spacers
                if (clusterCenterLine != null) clusterCenterLine.Visibility = Visibility.Collapsed;
                ApplyClusterBandFilterMode(clusterPreLiveScaleBandFilterMode ?? "PreSelected", false);
                var v1 = GetClusterSpotsView();
                if (v1 != null) ApplyClusterSort(v1, clusterPreLiveScaleSortMember, clusterPreLiveScaleSortDir);
            }

            if (clusterLiveScaleBtn != null)
                clusterLiveScaleBtn.Style = MakeClusterBandFilterBtnStyle(clusterLiveScaleOn);
        }

        private System.Windows.Data.ListCollectionView GetClusterSpotsView()
        {
            if (clusterSpotsDataGrid == null) return null;
            return System.Windows.Data.CollectionViewSource.GetDefaultView(clusterSpotsDataGrid.ItemsSource)
                   as System.Windows.Data.ListCollectionView;
        }

        // Pure frequency order (highest at top). NO needed-country pin, since Live Scale's position -> frequency
        // mapping requires a strictly monotonic frequency order.
        private void ApplyClusterLiveScaleSort()
        {
            var view = GetClusterSpotsView();
            if (view == null) return;
            using (view.DeferRefresh())
            {
                view.SortDescriptions.Clear();
                view.SortDescriptions.Add(new System.ComponentModel.SortDescription("FreqMhz", System.ComponentModel.ListSortDirection.Descending));
            }
            if (clusterSpotsDataGrid != null)
            {
                foreach (var col in clusterSpotsDataGrid.Columns) col.SortDirection = null;
                if (clusterFreqColumn != null) clusterFreqColumn.SortDirection = System.ComponentModel.ListSortDirection.Descending;
            }
        }

        // Refreshes the readout on the line and re-aligns the table to the current VFO. Runs on every
        // frequency change AND after every spot refresh (both via UpdateClusterFrequencyHighlight), so
        // knob turns and newly arriving spots both keep the line truthful.
        private void UpdateClusterLiveScale()
        {
            if (!clusterLiveScaleOn || clusterCenterLine == null) return;

            double vfoMhz = 0;
            double.TryParse((TB_Frequency.Text ?? string.Empty).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out vfoMhz);
            if (clusterCenterLineFreqText != null)
                clusterCenterLineFreqText.Text = vfoMhz > 0
                    ? vfoMhz.ToString("0.000", CultureInfo.InvariantCulture) + " MHz"
                    : string.Empty;

            ScrollClusterLiveScale();
        }

        // Locks/unlocks the table for Live Scale: while on, the PROGRAM owns the scroll position — the
        // wheel is blocked, the vertical scrollbar is hidden, and column sorting is disabled (the scale
        // requires strict frequency order). Off restores everything, including virtualized scrolling.
        private void SetClusterLiveScaleScrollSetup(bool on)
        {
            if (clusterSpotsDataGrid == null) return;
            clusterSpotsDataGrid.CanUserSortColumns = !on;
            ScrollViewer.SetVerticalScrollBarVisibility(clusterSpotsDataGrid, on ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Auto);
            clusterSpotsDataGrid.PreviewMouseWheel -= ClusterLiveScale_BlockWheel;
            if (on)
            {
                clusterSpotsDataGrid.PreviewMouseWheel += ClusterLiveScale_BlockWheel;
            }
            else
            {
                if (clusterLiveScaleRowsHost != null) clusterLiveScaleRowsHost.Margin = new Thickness(0);
                var sv = clusterSpotsScrollViewer ?? FindVisualChild<ScrollViewer>(clusterSpotsDataGrid);
                if (sv != null) sv.CanContentScroll = true;   // back to normal virtualized scrolling
            }
        }

        private void ClusterLiveScale_BlockWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            e.Handled = true;   // no manual scrolling while Live Scale is on
        }

        // The Live Scale engine: scrolls the table so the current VFO frequency sits exactly on the center
        // line. Between two spots the position interpolates proportionally with frequency (so the table
        // moves fast across a 1 kHz gap and slowly across a 20 kHz gap — every row is one row tall
        // regardless of its frequency distance). Beyond the highest/lowest spot it extrapolates using the
        // list's average spacing, letting the whole list slide away off-screen. Full-viewport spacer
        // margins on the ROWS panel (never the grid itself — the column headers must not move) give the
        // scroll range needed for all of that.
        private void ScrollClusterLiveScale()
        {
            // Paused while a band-hover preview owns the table (it shows that band as a normal list, not
            // the VFO-centered Live Scale view). The scroll is re-established when the hover ends.
            if (!clusterLiveScaleOn || _clusterBandHoverActive || clusterSpotsDataGrid == null) return;

            if (clusterSpotsScrollViewer == null)
                clusterSpotsScrollViewer = FindVisualChild<ScrollViewer>(clusterSpotsDataGrid);
            var sv = clusterSpotsScrollViewer;
            if (sv == null) return;

            // Pixel-precise scrolling so the line can sit BETWEEN rows; needs a re-layout, so re-run after.
            if (sv.CanContentScroll)
            {
                sv.CanContentScroll = false;
                Dispatcher.BeginInvoke(new Action(ScrollClusterLiveScale), System.Windows.Threading.DispatcherPriority.Loaded);
                return;
            }

            double vh = sv.ViewportHeight;
            if (vh <= 0) return;

            if (clusterLiveScaleRowsHost == null)
                clusterLiveScaleRowsHost = FindVisualChild<System.Windows.Controls.Primitives.DataGridRowsPresenter>(clusterSpotsDataGrid);
            if (clusterLiveScaleRowsHost == null) return;

            double pad = vh;   // full viewport above and below -> any frequency can reach the line
            var m = clusterLiveScaleRowsHost.Margin;
            if (Math.Abs(m.Top - pad) > 1 || Math.Abs(m.Bottom - pad) > 1)
            {
                clusterLiveScaleRowsHost.Margin = new Thickness(0, pad, 0, pad);
                Dispatcher.BeginInvoke(new Action(ScrollClusterLiveScale), System.Windows.Threading.DispatcherPriority.Loaded);
                return;
            }

            int n = clusterSpotsDataGrid.Items.Count;
            double vfo = 0;
            double.TryParse((TB_Frequency.Text ?? string.Empty).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out vfo);
            if (n <= 0 || vfo <= 0) return;

            // Out of band: position the list as if the radio were sitting exactly on the nearest band
            // edge, ignoring the real (out-of-band) frequency — so it parks stably at the edge.
            if (IsClusterOutOfBand() && TryClusterNearestBandEdge(vfo, out double edgeMhz, out _))
                vfo = edgeMhz;

            double rowH = 0;
            if (clusterSpotsDataGrid.ItemContainerGenerator.ContainerFromIndex(0) is DataGridRow r0 && r0.ActualHeight > 0)
                rowH = r0.ActualHeight;
            if (rowH <= 0)
            {
                // Rows exist but aren't laid out yet (e.g. Live Scale restored at window open, spots
                // arriving right after). Without this retry the view stays parked on the blank top
                // spacer — an "empty" table with all the spots scrolled out of sight.
                if (clusterLiveScaleAlignRetries < 30)
                {
                    clusterLiveScaleAlignRetries++;
                    Dispatcher.BeginInvoke(new Action(ScrollClusterLiveScale), System.Windows.Threading.DispatcherPriority.Loaded);
                }
                return;
            }
            clusterLiveScaleAlignRetries = 0;

            // Keep the two frame lines exactly one row apart: strip height = row height + both line
            // thicknesses, so the lines sit just OUTSIDE the row's edges and the row text is untouched.
            if (clusterCenterLineBand != null)
            {
                double desired = rowH + 2;   // 1px top line + 1px bottom line outside the row
                if (Math.Abs(clusterCenterLineBand.Height - desired) > 0.5)
                {
                    clusterCenterLineBand.Height = desired;
                    Dispatcher.BeginInvoke(new Action(PositionClusterCenterLine),
                        System.Windows.Threading.DispatcherPriority.Loaded);   // re-center for the new height
                }
            }

            Func<int, double> freqAt = i => (clusterSpotsDataGrid.Items[i] as ClusterSpotViewItem)?.FreqMhz ?? 0;
            double topF = freqAt(0);          // highest frequency (sorted descending)
            double botF = freqAt(n - 1);      // lowest
            // Average spacing (MHz per row) for extrapolating beyond the ends; 1 kHz fallback.
            double avgGap = (n > 1 && topF > botF) ? (topF - botF) / (n - 1) : 0.001;
            if (avgGap <= 0) avgGap = 0.001;

            // Vertical position of the VFO in row units (row i's center = i + 0.5).
            double rowsY;
            if (vfo >= topF)
                rowsY = 0.5 - (vfo - topF) / avgGap;              // above everything -> list slides down/away
            else if (vfo <= botF)
                rowsY = (n - 1) + 0.5 + (botF - vfo) / avgGap;    // below everything -> list slides up/away
            else
            {
                rowsY = (n - 1) + 0.5;
                for (int i = 0; i < n - 1; i++)
                {
                    double fi = freqAt(i), fj = freqAt(i + 1);
                    if (fi >= vfo && vfo >= fj)
                    {
                        double span = fi - fj;
                        double t = span > 0 ? (fi - vfo) / span : 0.0;   // 0 at spot i .. 1 at spot i+1
                        rowsY = i + 0.5 + t;
                        break;
                    }
                }
            }

            // Align that position (inside the padded content) with the line. Use the line's MEASURED
            // position within the rows viewport — not an assumed "viewport center" — so the drawn line and
            // the scroll target can never disagree (a half-row mismatch here put the line on the row's
            // bottom edge instead of its center).
            double lineY = vh / 2.0;
            var rowsViewport = FindVisualChild<ScrollContentPresenter>(clusterSpotsDataGrid);
            if (rowsViewport != null && clusterCenterLineBand != null)
            {
                try
                {
                    lineY = clusterCenterLineBand.TransformToVisual(rowsViewport)
                        .Transform(new Point(0, clusterCenterLineBand.ActualHeight / 2.0)).Y;
                }
                catch (System.Exception swallowed) { Log.Swallow(swallowed); }
            }

            double target = pad + rowsY * rowH - lineY;
            double maxOffset = Math.Max(0, sv.ExtentHeight - vh);
            if (target < 0) target = 0; else if (target > maxOffset) target = maxOffset;
            sv.ScrollToVerticalOffset(target);
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
                Width = 30,
                Height = 30,
                CornerRadius = new CornerRadius(15),
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
                    catch (System.Exception swallowed) { Log.Swallow(swallowed); }
                }

                Canvas.SetTop(clusterShowBandsPanel, showTop);

                // Spot-count badge floats just to the right of the band-filter group; its bottom is
                // pinned a small gap above the table top (canvas-Y == ClusterTableTopGap), so it never
                // touches the grid header.
                double tableTopInCanvas = ClusterTableTopGap;
                if (clusterSpotCountBadge != null)
                {
                    // Out of band the label reads "out of band" — wider than a band name, which pushed the
                    // badge rightward into the Last dropdown. Simplest cure (user 2026-07-12): hide the
                    // badge entirely while out of band.
                    bool bandValid = TB_Band != null && !string.IsNullOrWhiteSpace(TB_Band.Text);
                    clusterSpotCountBadge.Visibility = bandValid ? Visibility.Visible : Visibility.Collapsed;

                    // Anchor the badge to the right edge of the "Active Band + <band>" row so adding the
                    // Live Scale button to the row above (which widens the whole panel) doesn't push it.
                    double badgeLeft = panelLeft + panelWidth + ClusterBandGroupToCounterGap;   // fallback
                    if (clusterActiveBandIndicatorText != null)
                    {
                        try
                        {
                            Point indRight = clusterActiveBandIndicatorText.TranslatePoint(
                                new Point(clusterActiveBandIndicatorText.ActualWidth, 0), clusterHeaderCanvas);
                            badgeLeft = indRight.X + ClusterBandGroupToCounterGap;
                        }
                        catch (System.Exception swallowed) { Log.Swallow(swallowed); }
                    }
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
                    catch (System.Exception swallowed) { Log.Swallow(swallowed); }
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
            catch (System.Exception swallowed) { Log.Swallow(swallowed); }
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

        // These settings used to live in loose .txt files under AppData. They now live in
        // Properties.Settings like everything else, so a profile (which snapshots Properties.Settings)
        // captures them too. MigrateLegacyFileSettings imports any old file once.
        private bool LoadClusterHoverPopupSetting() => Properties.Settings.Default.ClusterHoverPopupEnabled;

        private void SaveClusterHoverPopupSetting(bool enabled)
        {
            Properties.Settings.Default.ClusterHoverPopupEnabled = enabled;
            try { Properties.Settings.Default.Save(); } catch (System.Exception swallowed) { Log.Swallow(swallowed); }
        }

        private int LoadClusterLastMinutesFilterSetting()
        {
            int value = Properties.Settings.Default.ClusterLastMinutesFilter;
            return (value == 5 || value == 15 || value == 30 || value == 60) ? value : 60;
        }

        private void SaveClusterLastMinutesFilterSetting(int minutes)
        {
            if (!(minutes == 5 || minutes == 15 || minutes == 30 || minutes == 60)) return;
            Properties.Settings.Default.ClusterLastMinutesFilter = minutes;
            try { Properties.Settings.Default.Save(); } catch (System.Exception swallowed) { Log.Swallow(swallowed); }
        }

        private double LoadClusterCountryColumnWidthSetting()
        {
            double value = Properties.Settings.Default.ClusterCountryColumnWidth;
            return (!double.IsNaN(value) && !double.IsInfinity(value) && value >= 40) ? value : 100;
        }

        private void SaveClusterCountryColumnWidthSetting(double width)
        {
            if (double.IsNaN(width) || double.IsInfinity(width) || width < 40)
            {
                return;
            }

            try
            {
                Properties.Settings.Default.ClusterCountryColumnWidth = width;
                Properties.Settings.Default.Save();
            }
            catch (System.Exception swallowed) { Log.Swallow(swallowed); }
        }

        private int LoadClusterCountryColumnDisplayIndexSetting()
        {
            int value = Properties.Settings.Default.ClusterCountryColumnDisplayIndex;
            return value >= 0 ? value : 2;
        }

        private void SaveClusterCountryColumnDisplayIndexSetting(int displayIndex)
        {
            if (displayIndex < 0) return;
            Properties.Settings.Default.ClusterCountryColumnDisplayIndex = displayIndex;
            try { Properties.Settings.Default.Save(); } catch (System.Exception swallowed) { Log.Swallow(swallowed); }
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
                Properties.Settings.Default.ClusterColumnOrder = string.Join(",", order);
                Properties.Settings.Default.Save();
            }
            catch (System.Exception swallowed) { Log.Swallow(swallowed); }
        }

        private void ApplyClusterColumnOrder(DataGrid grid)
        {
            if (grid == null) return;
            try
            {
                string content = (Properties.Settings.Default.ClusterColumnOrder ?? string.Empty).Trim();
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
            catch (System.Exception swallowed) { Log.Swallow(swallowed); }
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

                    // The attempt is given a deadline, because ConnectAsync has none of its own. A
                    // network that REFUSES is not the problem - Windows gives up on that in about
                    // twenty seconds and the loop below comes round again. The problem is a network
                    // that accepts the connection and then never answers: a captive portal, the hotel
                    // wifi still waiting for someone to click "I agree". There the wait never ends,
                    // the loop never comes round, and the cluster stays silently dead until the
                    // window is closed and reopened. Cancelling the attempt puts it back in the loop.
                    using (var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(token))
                    {
                        attemptCts.CancelAfter(ClusterConnectTimeoutMs);
                        try
                        {
                            await clusterWebSocket.ConnectAsync(new Uri(HolyClusterWebSocketUrl), attemptCts.Token);
                        }
                        catch (OperationCanceledException) when (!token.IsCancellationRequested)
                        {
                            // The deadline, not the window closing. Say which, and let the loop retry.
                            throw new TimeoutException("The cluster did not answer within "
                                + (ClusterConnectTimeoutMs / 1000) + " seconds.");
                        }
                    }
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
                var confirmedEntities = GetClusterConfirmedEntities();   // LoTW-confirmed names (empty if never fetched)
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
                            var spDxcc = CountryLookup.Shared.Resolve(spotter.Trim());
                            if (spDxcc != null && !string.IsNullOrWhiteSpace(spDxcc.Locator))
                            {
                                var spll = MaidenheadLocator.LocatorToLatLng(spDxcc.Locator);
                                spotterLat = spll.Lat;
                                spotterLon = spll.Long;
                            }
                        }
                        catch (System.Exception swallowed) { Log.Swallow(swallowed); }
                    }

                    var dxccInfo = CountryLookup.Shared.Resolve(dx.Trim());
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
                        IsUnconfirmedCountry = IsUnconfirmedCountry(dx, workedCountries, confirmedEntities),
                        IsLotwUser = LotwUserService.IsLotwUser(dx),
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

                    // ONLY WHILE THE CLUSTER WINDOW IS OPEN. Closing it does not close the connection -
                    // the spots still feed the map in the main window - so the alerts went on sounding
                    // for a table nobody had in front of them. A sound is a summons: it says "look at
                    // the cluster", and there is nothing to look at. clusterWindow is set to null when
                    // that window closes, which makes it the whole test.
                    // The Test buttons in Cluster Settings are unaffected: they play the sound directly.
                    if (clusterWindow != null)
                    {
                        if (newItems.Any(ClusterSpotQualifiesForNewCountryAlert))
                            PlayNewCountrySpotAlert();
                        else if (newItems.Any(ClusterSpotQualifiesForUnconfirmedAlert))
                            PlayUnconfirmedSpotAlert();
                    }

                    RefreshClusterVisibleSpots();
                }));
            }
            catch (System.Exception swallowed) { Log.Swallow(swallowed); }
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
            catch (System.Exception swallowed) { Log.Swallow(swallowed); }

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

            var dxcc = CountryLookup.Shared.Resolve(dxCallsign.Trim());

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

            // A station selected in HolyCluster (received over UDP) is authoritative: while its call is
            // held, the frequency-based auto-fill below neither overwrites nor clears the DX box, so the
            // exact clicked callsign stands even when that spot isn't in HolyLogger's own (filtered) feed.
            // Release the hold once the radio clearly moves off that spot's frequency (a wider tolerance
            // than the on-frequency ring, so a CW pitch offset or small drift doesn't drop it), letting
            // normal auto-fill resume.
            const double holyClusterReleaseKhz = 3.0;
            if (!string.IsNullOrWhiteSpace(_holyClusterSelectedCall) && _holyClusterSelectedFreqMhz > 0)
            {
                double diffKhz = Math.Abs(currentFreqMhz - _holyClusterSelectedFreqMhz) * 1000.0;
                if (diffKhz <= holyClusterReleaseKhz)
                    _holyClusterReachedFreq = true;    // the radio has landed on the selected spot's frequency
                else if (_holyClusterReachedFreq)
                    _holyClusterSelectedCall = null;   // ...and has since moved away -> release the hold
                else if ((DateTime.UtcNow - _holyClusterSelectedAtUtc).TotalSeconds > SuspensionTimeoutSeconds)
                {
                    // The radio never got there: CAT off, tuning refused, the operator turned the knob
                    // somewhere else. Holding for a landing that will never happen suspends the rule for
                    // the rest of the session, which is how a station stays on screen forever.
                    _holyClusterSelectedCall = null;
                }
            }
            bool holdingHolyClusterCall = !string.IsNullOrWhiteSpace(_holyClusterSelectedCall);

            // A band-hover preview that outlived its MouseLeave. The watchdog timer normally ends it;
            // this is a second, independent check on every recompute, so the rule cannot be suspended by
            // a hover the mouse left long ago.
            if (_clusterBandHoverActive && (clusterBandRowPanel == null || !clusterBandRowPanel.IsMouseOver))
                EndClusterBandHoverPreview();

            // A double-clicked spot whose frequency the radio never reached. Same reasoning as the
            // HolyCluster hold above: waiting forever for an arrival that is not coming would leave the
            // station on screen no matter where the operator tunes.
            if (_clusterAutoFilledDXCall && !_clusterAutoFilledReached
                && (DateTime.UtcNow - _clusterAutoFilledAtUtc).TotalSeconds > SuspensionTimeoutSeconds)
                _clusterAutoFilledReached = true;

            // An F9-dismissed callsign stays dismissed until the operator moves to ANOTHER filled spot
            // (a different on-frequency call gets filled -- see the auto-fill below) or double-clicks it.
            // It is NOT released merely by tuning off the frequency (e.g. to empty space and back), so a
            // dismissed spot doesn't quietly re-fill when you return to it.

            var onFreqSig = new System.Text.StringBuilder();     // which spots are on frequency right now
            var onFreqCalls = new System.Text.StringBuilder();   // their callsigns, for the in-place map restyle
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
                if (spot.IsOnFrequency)
                {
                    onFreqSig.Append(spot.SpotKey).Append('|');
                    onFreqCalls.Append((spot.DXCallsign ?? string.Empty).Trim()).Append(',');
                }
            }

            // The map highlights on-frequency spots too (green ring). Restyle those dots IN PLACE
            // (reusing the hover-highlight mechanism) only when the SET actually changed — never a
            // full re-render, never per knob tick.
            string sig = onFreqSig.ToString();
            string onFreqCallsStr = onFreqCalls.ToString().TrimEnd(',');
            if (!string.Equals(sig, _lastMapOnFreqSig, StringComparison.Ordinal))
            {
                _lastMapOnFreqSig = sig;
                if (MapControl != null && Properties.Settings.Default.ClusterMapEnabled)
                    MapControl.SetOnFreqSpots(onFreqCallsStr);
            }

            // Has the radio actually arrived at the frequency the call was filled at? After a
            // double-click, CAT needs a moment to slew, and during that moment the station is legitimately
            // not yet on frequency. The rule below is only enforced once the radio has been there.
            if (_clusterAutoFilledFreqMhz > 0
                && Math.Abs(currentFreqMhz - _clusterAutoFilledFreqMhz) * 1000.0 <= toleranceKhz)
                _clusterAutoFilledReached = true;

            // ONE rule, tested every pass: a cluster-filled DX callsign stays only while that station is
            // still on the radio's frequency. If it is not, the box (and the name, locator, country and
            // QRZ photo that came with it) is cleared.
            //
            // The authority is the per-spot IsOnFrequency flag - the same one that tints the row green,
            // recomputed against the VFO on every pass. Earlier versions inferred the same fact
            // indirectly, from the on-frequency SET changing plus how far the VFO had moved since the
            // fill, and that inference kept being wrong in ways the flag never is:
            //   * the set emptied for another reason (a band filter, a refresh, the cluster's undo
            //     button) and the later tune-away produced no further change, so nothing ever cleared;
            //   * the spot dropped out of the list while the VFO stayed put, which the "has the radio
            //     moved?" test read as "still on the station" - leaving a callsign, photo and locator on
            //     screen for a station that was no longer being shown as on frequency at all.
            // If the operator is on the frequency, the spot is on frequency, and the flag says so.
            if (_clusterAutoFilledDXCall && _clusterAutoFilledReached && !holdingHolyClusterCall
                && !_clusterBandHoverActive)   // a band-hover preview owns the DX box; don't clear here
            {
                string onFreqSnapshot = onFreqCallsStr;
                try
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            if (!_clusterAutoFilledDXCall) return;

                            string filled = (TB_DXCallsign?.Text ?? string.Empty).Trim();
                            if (filled.Length == 0) return;                       // nothing to clear
                            if (IsCallOnFrequency(onFreqSnapshot, filled)) return; // still there: keep it

                            _clusterAutoFilledDXCall = false;
                            // Flagged as OUR clear, not the operator's, so it does not count as
                            // dismissing the call (see _clusterAutoClearingDxCall).
                            _clusterAutoClearingDxCall = true;
                            try { HandleGlobalFunctionKey(System.Windows.Input.Key.F9, false); }
                            finally { _clusterAutoClearingDxCall = false; }
                        }
                        catch (Exception swallowed) { Log.Swallow(swallowed); }
                    }));
                }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
            }

            // Auto-fill the DX callsign when there is at least one on-frequency spot -- unless the
            // operator turned that off in Cluster Settings ("Auto fill DX callsign while - On My Radio
            // Freq"), in which case only a double-click ever fills the box.
            // While a HolyCluster selection is held, leave the DX box alone — it already shows the exact
            // clicked callsign, which must not be overwritten by a different on-frequency spot. While a
            // band-hover preview is active the DX box is intentionally hidden, so don't refill it either.
            //
            // MANUAL MODE never auto-fills: the frequency is typed by the operator, not read from the
            // radio, so "on my radio frequency" is not true even when the number happens to equal a
            // spot's. Matching a typed frequency and filling a callsign the operator never tuned to
            // would be wrong. A double-click on a spot still fills it explicitly.
            if (!holdingHolyClusterCall && !_clusterBandHoverActive
                && !Properties.Settings.Default.isManualMode
                && Properties.Settings.Default.ClusterAutoFillDxCall
                && !string.IsNullOrWhiteSpace(onFreqCallsStr) && TB_DXCallsign != null)
            {
                // THE SPOT ON MY FREQUENCY MAY BE ME. Somebody works me, spots me, and the spot comes
                // back round the world onto my own screen - on my own frequency, because that is where
                // I am. Filling it in would put MY callsign in the DX box while I am calling CQ, and
                // the box has to be empty and waiting for whoever answers.
                // Skipped, not stopped: if two stations are on frequency and one of them is me, the
                // other is still a perfectly good fill. Compared by identity, so 4Z5SL spotted as
                // 4Z5SL/P is still me (see CallsignIdentity).
                string firstCall = null;
                foreach (string candidate in onFreqCallsStr.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string c = candidate.Trim();
                    if (c.Length == 0 || IsMyOwnStation(c)) continue;
                    firstCall = c;
                    break;
                }
                if (!string.IsNullOrWhiteSpace(firstCall))
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            string current = (TB_DXCallsign.Text ?? string.Empty).Trim();

                            // Don't clobber a callsign the user is actively typing. But focus alone is
                            // NOT "typing": F9/Clear (which is what the leave-frequency auto-clear calls)
                            // parks keyboard focus in this now-empty box, so a focused-but-empty box must
                            // still accept the auto-fill. Otherwise, after leaving a spot and tuning back
                            // onto its frequency, the callsign would never be re-filled.
                            //
                            // ...and a call the CLUSTER put there is not the operator's either, however
                            // the focus happens to sit. Double-clicking a spot leaves focus in this box,
                            // so without the _clusterAutoFilledDXCall test, tuning from one spotted
                            // station straight onto another left the first one standing - with its name,
                            // locator and QRZ photo - while a different station sat on the frequency.
                            // The "left the frequency" clear cannot catch that case: it only fires when
                            // NO spot is on frequency, and here one is.
                            bool userEditing = TB_DXCallsign.IsFocused
                                               && !string.IsNullOrEmpty(current)
                                               && !_clusterAutoFilledDXCall;
                            if (userEditing)
                                return;

                            // The user cleared this exact call with F9 — don't put it back. It stays
                            // dismissed until the radio lands on a DIFFERENT filled spot (below) or the
                            // spot is double-clicked (TuneToClusterSpot clears the dismissal explicitly).
                            if (!string.IsNullOrEmpty(_clusterDismissedCall)
                                && string.Equals(firstCall, _clusterDismissedCall, StringComparison.OrdinalIgnoreCase))
                                return;

                            // Reaching here with a DIFFERENT on-frequency call means the operator moved to
                            // another filled spot — so any earlier F9 dismissal no longer applies, and
                            // returning to the old spot later will fill it normally again.
                            _clusterDismissedCall = null;

                            // Only (re)fill when the callsign actually differs. Re-setting the same call
                            // used to re-run TB_DXCallsign_TextChanged every refresh, which clears the
                            // name/locator/zones and re-queries QRZ — so the Name blinked out and back on
                            // every Live Scale / spot update. (Empty and user-typed-different are covered
                            // by "differs"; a focused edit already returned above.)
                            bool shouldOverwrite = !string.Equals(current, firstCall, StringComparison.OrdinalIgnoreCase);
                            if (shouldOverwrite)
                            {
                                // Suppress the "focused edit = manual change" handling in
                                // TB_DXCallsign_TextChanged while WE are the ones setting the text; the
                                // box may legitimately hold focus here (see note above).
                                _clusterFillingDXCall = true;
                                try
                                {
                                    // Callsign comes from the cluster (on-frequency spot), not typing — it's
                                    // already a complete call, so don't pop the suggestions dropdown.
                                    suppressNextCallsignSuggestions = true;
                                    TB_DXCallsign.Text = firstCall;
                                    TB_DXCallsign.CaretIndex = TB_DXCallsign.Text.Length;
                                    _clusterAutoFilledDXCall = true;
                                    _clusterAutoFilledFreqMhz = currentFreqMhz;   // remember where we filled
                                    // Filled BECAUSE the radio is on this frequency, so it is there by
                                    // definition - no slew to wait for.
                                    _clusterAutoFilledReached = true;
                                    _clusterAutoFilledAtUtc = DateTime.UtcNow;
                                    TB_DXCallsign_TextChanged(TB_DXCallsign, null);
                                    TB_DXCallsign_LostFocus(TB_DXCallsign, new RoutedEventArgs());
                                }
                                finally { _clusterFillingDXCall = false; }
                            }
                            else if (_clusterAutoFilledDXCall)
                            {
                                TB_DXCallsign_LostFocus(TB_DXCallsign, new RoutedEventArgs());
                            }
                        }
                        catch (Exception swallowed) { Log.Swallow(swallowed); }
                    }), DispatcherPriority.Background);
                }
            }

            if (clusterLiveScaleOn && !_clusterBandHoverActive) UpdateClusterLiveScale();
        }

        // IS THIS SPOT ME? A station that works me may spot me, and that spot arrives back here like any
        // other - sitting on my own frequency, because that is where I am transmitting. Everything that
        // reads "the station on my frequency" has to be able to tell that one apart from a station I
        // could work.
        // The station callsign, not the operator: a spot names the station that was heard. Compared by
        // identity so a portable suffix somebody else added ("4Z5SL/P") is still recognised as me.
        private bool IsMyOwnStation(string callsign)
        {
            if (string.IsNullOrWhiteSpace(callsign)) return false;

            string mine = TB_MyCallsign != null ? (TB_MyCallsign.Text ?? string.Empty).Trim() : string.Empty;
            if (mine.Length == 0) mine = (Properties.Settings.Default.my_callsign ?? string.Empty).Trim();
            if (mine.Length == 0) return false;

            return CallsignIdentity.Same(callsign, mine);
        }

        // Enter/leave the band-checkbox hover preview: show ONLY the hovered band's spots in the table and
        // on the map (in every state, Live Scale included), and hide the filled DX station so the map shows
        // the band instead of the DX path. The pre-hover station is restored on leave (Case 5 of the spec).
        private void EnterClusterBandHoverPreview(string band)
        {
            if (!_clusterBandHoverActive)
            {
                _clusterBandHoverActive = true;
                _clusterBandHoverSavedCall = (_clusterAutoFilledDXCall && TB_DXCallsign != null
                                              && !string.IsNullOrWhiteSpace(TB_DXCallsign.Text))
                    ? TB_DXCallsign.Text.Trim()
                    : null;
                if (!string.IsNullOrEmpty(_clusterBandHoverSavedCall))
                    TB_DXCallsign.Clear();   // ClearAzimuth restores the cluster-spots map view

                // In Live Scale the table is locked to the VFO-centered view; unlock it so the hovered
                // band shows as a normal top-down list (otherwise it centers on the VFO, which isn't in
                // this band, and looks empty). Hide the center line while previewing.
                if (clusterLiveScaleOn)
                {
                    SetClusterLiveScaleScrollSetup(false);
                    if (clusterCenterLineBand != null) clusterCenterLineBand.Visibility = Visibility.Hidden;
                }
            }
            _clusterHoverBandOverride = band;
            StartClusterBandHoverWatchdog();
            RefreshClusterVisibleSpots();

            // In Live Scale the table kept the VFO-centered scroll offset; show the previewed band from
            // the top so its spots are visible (otherwise the leftover offset can leave it looking empty).
            if (clusterLiveScaleOn && clusterSpotsDataGrid != null)
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    var sv = clusterSpotsScrollViewer ?? FindVisualChild<ScrollViewer>(clusterSpotsDataGrid);
                    sv?.ScrollToTop();
                }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        // Ends the preview when the mouse leaves the WHOLE band-checkbox row (wired to the row's
        // MouseLeave, not each checkbox — so moving between checkboxes or across the gaps between them
        // keeps the preview alive and doesn't briefly restore the station).
        // Safety net for a MISSED MouseLeave. The row's MouseLeave is not guaranteed to fire -- e.g. the
        // pointer leaves straight onto another window, or a window opens under it. If that happened the
        // preview stayed active forever, which BOTH kept the DX box cleared and suppressed the
        // on-frequency auto-fill (it looked like "auto fill just stopped working"). This polls whether
        // the pointer is really still over the band row and ends the preview as soon as it isn't.
        private void StartClusterBandHoverWatchdog()
        {
            if (_clusterBandHoverWatchdog == null)
            {
                _clusterBandHoverWatchdog = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(250)
                };
                _clusterBandHoverWatchdog.Tick += (s, e) =>
                {
                    if (!_clusterBandHoverActive) { _clusterBandHoverWatchdog.Stop(); return; }
                    if (clusterBandRowPanel == null || !clusterBandRowPanel.IsMouseOver)
                        EndClusterBandHoverPreview();   // stops the timer itself
                };
            }
            _clusterBandHoverWatchdog.Start();
        }

        private void EndClusterBandHoverPreview()
        {
            _clusterBandHoverWatchdog?.Stop();
            if (!_clusterBandHoverActive) return;

            _clusterHoverBandOverride = null;
            string restoreCall = _clusterBandHoverSavedCall;
            _clusterBandHoverSavedCall = null;
            _clusterBandHoverActive = false;

            if (clusterLiveScaleOn)
            {
                // Re-establish Live Scale: re-lock the scroll, re-sort by frequency, re-center on the VFO.
                SetClusterLiveScaleScrollSetup(true);
                ApplyClusterLiveScaleSort();
                if (clusterCenterLineBand != null && _centerLineRevealed)
                    clusterCenterLineBand.Visibility = Visibility.Visible;
            }
            RefreshClusterVisibleSpots();

            if (!string.IsNullOrEmpty(restoreCall) && TB_DXCallsign != null)
            {
                // Re-show exactly what was filled before the hover (the re-lookup restores the
                // name/country/locator/QRZ/map path). Keep it flagged as a cluster fill so the normal
                // tune-away clear still applies afterwards.
                TB_DXCallsign.Text = restoreCall;
                _clusterAutoFilledDXCall = true;
            }
        }

        // Signature of the spots last reported to the map as on-frequency (see above).
        private string _lastMapOnFreqSig = string.Empty;

        // Whether the DX callsign was auto-filled by the cluster on-frequency feature. If true,
        // clearing on-frequency spots should clear that auto-filled textbox (F9). False when the
        // user manually typed/changed the DX callsign.
        private bool _clusterAutoFilledDXCall = false;

        // The VFO frequency (MHz) at which the DX callsign was auto-filled (on-frequency auto-fill or a
        // cluster double-click). The leave-frequency auto-clear only fires once the radio moves off THIS
        // frequency, so a band-filter/hover refresh that transiently empties the on-frequency set while
        // the VFO stays put does not wrongly clear the filled call.
        private double _clusterAutoFilledFreqMhz = 0;

        // True once the radio has actually been ON that frequency. A double-clicked spot fills the box
        // immediately while CAT is still slewing, and until the radio arrives the station is genuinely
        // not on frequency yet - clearing then would wipe the call the operator just chose.
        private bool _clusterAutoFilledReached = false;

        // When the current call was put in the box, for the timeout above.
        private DateTime _clusterAutoFilledAtUtc = DateTime.UtcNow;

        // How long any suspension of the on-frequency rule may last before it is forced to end.
        //
        // Every suspension here waits for something that USUALLY happens: the mouse leaving a band
        // checkbox, the radio arriving where it was sent. When one of those never happens, an unbounded
        // wait does not merely delay the rule - it switches it off for the rest of the session, and the
        // DX box keeps a station the operator left long ago. Generous enough for a slow CAT slew, short
        // enough that nobody is left staring at a stale callsign.
        private const double SuspensionTimeoutSeconds = 15.0;

        // Is this callsign one of the spots currently on the radio's frequency? The list is the
        // comma-separated set built from the per-spot IsOnFrequency flags.
        private static bool IsCallOnFrequency(string onFreqCallsCsv, string callsign)
        {
            if (string.IsNullOrEmpty(onFreqCallsCsv) || string.IsNullOrWhiteSpace(callsign)) return false;
            foreach (string call in onFreqCallsCsv.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                if (string.Equals(call.Trim(), callsign, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // Reentrancy guard: true only while the cluster auto-fill is programmatically setting the DX
        // callsign. TB_DXCallsign_TextChanged treats any change made while the box has focus as a
        // manual edit and drops _clusterAutoFilledDXCall; this flag suppresses that during our own
        // fill, because F9/Clear parks focus in the (empty) box before we re-fill it.
        private bool _clusterFillingDXCall = false;

        // A callsign the user cleared with F9. While set, the on-frequency auto-fill will NOT put this
        // call back — the user dismissed it on purpose. Released when the radio moves to another filled
        // spot (see UpdateClusterFrequencyHighlight); a double-click on the spot still fills it explicitly.
        private string _clusterDismissedCall;

        // True only while the code below clears the DX box ITSELF, because the radio tuned away from the
        // spot it had auto-filled. That cleanup goes through the same F9/Clear handler the operator uses,
        // and that handler records whatever it clears as "dismissed" - so without this flag simply tuning
        // off a spot marked it dismissed, and tuning back on to it would never re-fill. A dismissal must
        // only ever come from the operator actually pressing F9 / Clear.
        private bool _clusterAutoClearingDxCall;

        internal bool IsClusterAutoClearingDxCall => _clusterAutoClearingDxCall;

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

        // Memoized on the raw settings string: these getters are called PER SPOT in the refresh and
        // count loops (1500 spots per batch), and re-splitting the string + allocating a HashSet each
        // time was pure churn. A plain string compare serves the cached set until the setting changes.
        private string _enabledBandsRaw;
        private HashSet<string> _enabledBandsCache;

        private HashSet<string> GetEnabledClusterBands()
        {
            string raw = Properties.Settings.Default.ClusterEnabledBands ?? string.Empty;
            if (_enabledBandsCache != null && string.Equals(raw, _enabledBandsRaw, StringComparison.Ordinal))
                return _enabledBandsCache;

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
            _enabledBandsRaw = raw;
            _enabledBandsCache = set;
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

        // The label next to the Active Band button: the current band (green when Active mode is selected,
        // gray otherwise), or a red "out of band" when the radio frequency is outside every ham band — so
        // an empty Active-band table is always self-explanatory instead of looking like a bug.
        private void UpdateClusterActiveBandIndicatorText()
        {
            if (clusterActiveBandIndicatorText == null) return;
            string display = FormatClusterBandDisplay(TB_Band != null ? TB_Band.Text : string.Empty);
            bool isActive = string.Equals(Properties.Settings.Default.ClusterBandFilterMode, "Active", StringComparison.OrdinalIgnoreCase);
            if (display.Length > 0)
            {
                clusterActiveBandIndicatorText.Text = display;
                clusterActiveBandIndicatorText.Foreground = isActive
                    ? new SolidColorBrush(Color.FromRgb(0, 190, 0))
                    : (Brush)new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA));
            }
            else
            {
                clusterActiveBandIndicatorText.Text = "out of band";
                clusterActiveBandIndicatorText.Foreground = Brushes.Red;
            }

            // Out of band only empties the list in Active-band mode (Live Scale forces Active). Tint the
            // whole spots area red then, so it's obvious at a glance WHY the table is empty — not a bug.
            UpdateClusterOutOfBandTint(isActive && display.Length == 0);
        }

        // Pale-red wash over the spots grid while out of band; otherwise the normal themed row bg.
        private static readonly SolidColorBrush ClusterOutOfBandBrush =
            new SolidColorBrush(Color.FromRgb(0xFF, 0xD6, 0xD6));

        private void UpdateClusterOutOfBandTint(bool outOfBand)
        {
            if (clusterSpotsDataGrid == null) return;
            if (outOfBand)
                clusterSpotsDataGrid.Background = ClusterOutOfBandBrush;
            else
                clusterSpotsDataGrid.SetResourceReference(Control.BackgroundProperty, "GridRowBg");
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
                string active = NormalizeClusterBandKey(TB_Band != null ? TB_Band.Text : string.Empty);
                // Out of band under Live Scale: keep showing the NEAREST band's spots (so the list can
                // park at that band's edge) instead of emptying.
                if (string.IsNullOrWhiteSpace(active) && clusterLiveScaleOn
                    && TryClusterNearestBandEdge(ClusterVfoMhz(), out _, out string edgeBand))
                    active = NormalizeClusterBandKey(edgeBand);
                return !string.IsNullOrWhiteSpace(active) && string.Equals(active, normalized, StringComparison.OrdinalIgnoreCase);
            }

            // PreSelected
            var enabled = GetEnabledClusterBands();
            return enabled.Contains(normalized);
        }

        // The HF/6m band edges the program already recognizes (MHz), for parking Live Scale at the band
        // edge when the radio tunes just out of a band.
        private static readonly (double Lo, double Hi, string Band)[] ClusterBandLimits =
        {
            (1.8, 2.0, "160M"), (3.5, 4.0, "80M"), (5.0, 5.4, "60M"), (7.0, 7.3, "40M"),
            (10.0, 10.15, "30M"), (14.0, 14.35, "20M"), (18.0, 18.168, "17M"), (21.0, 21.45, "15M"),
            (24.89, 24.99, "12M"), (28.0, 29.7, "10M"), (50.0, 54.0, "6M")
        };

        // The current VFO in MHz (0 when unparseable).
        private double ClusterVfoMhz()
        {
            double.TryParse((TB_Frequency != null ? (TB_Frequency.Text ?? string.Empty) : string.Empty).Trim(),
                NumberStyles.Float, CultureInfo.InvariantCulture, out double v);
            return v;
        }

        // True when the radio is outside every ham band (the frequency handler blanks TB_Band then).
        private bool IsClusterOutOfBand()
            => string.IsNullOrWhiteSpace(TB_Band != null ? TB_Band.Text : null);

        // For an out-of-band VFO, the nearest band edge (MHz) and that band's name. false if vfo <= 0.
        private bool TryClusterNearestBandEdge(double vfoMhz, out double edgeMhz, out string band)
        {
            edgeMhz = 0; band = null;
            if (vfoMhz <= 0) return false;
            double best = double.MaxValue;
            foreach (var b in ClusterBandLimits)
            {
                double e = vfoMhz < b.Lo ? b.Lo : (vfoMhz > b.Hi ? b.Hi : vfoMhz);
                double d = Math.Abs(vfoMhz - e);
                if (d < best) { best = d; edgeMhz = e; band = b.Band; }
            }
            return band != null;
        }

        private string _enabledModesRaw;
        private HashSet<string> _enabledModesCache;

        private HashSet<string> GetEnabledClusterModes()
        {
            string raw = Properties.Settings.Default.ClusterEnabledModes ?? string.Empty;
            if (_enabledModesCache != null && string.Equals(raw, _enabledModesRaw, StringComparison.Ordinal))
                return _enabledModesCache;

            var values = raw.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                            .Select(v => v.Trim().ToUpperInvariant())
                            .Where(v => !string.IsNullOrWhiteSpace(v));

            var set = new HashSet<string>(values, StringComparer.OrdinalIgnoreCase);
            // Don't auto-fill here - let the save function handle empty sets
            _enabledModesRaw = raw;
            _enabledModesCache = set;
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

            bool lotwOnly = Properties.Settings.Default.ClusterLotwOnly;
            var ordered = clusterAllSpots.Where(s => IsClusterBandEnabled(s.BandText) && IsClusterModeEnabled(s.Mode))
                                         .Where(s => !lotwOnly || s.IsLotwUser)
                                         .Where(s => s.UnixTime > 0 && s.UnixTime >= DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (clusterLastMinutesFilterValue * 60L))
                                         .OrderByDescending(s => s.UnixTime);

            List<ClusterSpotViewItem> filtered;
            if (clusterLatestPerCallsignOn)
            {
                // Keep only the newest spot for each callsign+band. Source is newest-first, so the
                // first item seen for a (call|band) key is the one to keep. Then collapse again by
                // FREQUENCY: when two different stations sit on the same frequency, show only the newest
                // one (an older spot on that frequency is stale — the frequency is now the newer station's).
                filtered = ordered
                    .GroupBy(s => (s.DXCallsign ?? string.Empty).Trim().ToUpperInvariant() + "|" + NormalizeClusterBandKey(s.BandText))
                    .Select(g => g.First())
                    .GroupBy(s => s.FreqMhz > 0
                        ? s.FreqMhz.ToString("F5", CultureInfo.InvariantCulture)   // one entry per exact frequency
                        : "_" + s.SpotKey)                                          // no freq -> never collapse
                    .Select(g => g.OrderByDescending(x => x.UnixTime).First())
                    .OrderByDescending(s => s.UnixTime)
                    .Take(500)
                    .ToList();
            }
            else
            {
                filtered = ordered.Take(500).ToList();
            }

            // One pass over the log builds an O(1) lookup set; the old per-spot IsClusterCallsignInLog
            // scanned the whole log for EVERY spot (500 spots x 11k QSOs per refresh, on the UI thread).
            var loggedDxCalls = BuildLoggedDxCallSet();
            foreach (var item in filtered)
            {
                item.IsInLog = loggedDxCalls.Contains((item.DXCallsign ?? string.Empty).Trim());
            }

            clusterVisibleSpots.ReplaceAll(filtered);   // one Reset event, one DataGrid layout pass

            UpdateClusterFrequencyHighlight();

            // Re-align Live Scale AFTER the new rows have been laid out.
            //
            // The call above reaches ScrollClusterLiveScale synchronously, in the same call stack as the
            // ReplaceAll a few lines up - so it measures the table as it was BEFORE the new spots were
            // arranged. The engine's own retry only rescues the case where a row reports zero height;
            // right after a Reset the old containers usually still report their PREVIOUS height, so the
            // measurement looks valid, one wrong offset is applied, and nothing tries again.
            //
            // The result was a centre line that drifted off its row as spots arrived and stayed off,
            // until some later event - a turn of the VFO knob - happened to recompute it against a
            // settled layout, at which point the row snapped into the frame.
            if (clusterLiveScaleOn && !_clusterBandHoverActive)
                Dispatcher.BeginInvoke(new Action(ScrollClusterLiveScale),
                                       System.Windows.Threading.DispatcherPriority.Loaded);

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
                // When "Latest" is on the visible list keeps one row per callsign+band AND one row per
                // frequency (newest wins), so these per-band counters must apply the SAME collapse to
                // match the table. Iterate newest-first so "first seen = the one kept".
                var seen = clusterLatestPerCallsignOn ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) : null;
                var seenFreq = clusterLatestPerCallsignOn ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) : null;
                IEnumerable<ClusterSpotViewItem> source = clusterLatestPerCallsignOn
                    ? clusterAllSpots.OrderByDescending(s => s.UnixTime)
                    : (IEnumerable<ClusterSpotViewItem>)clusterAllSpots;
                foreach (var s in source)
                {
                    if (s.UnixTime <= 0 || s.UnixTime < cutoff)
                        continue;
                    if (!IsClusterModeEnabled(s.Mode))
                        continue;
                    if (string.IsNullOrWhiteSpace(s.BandText))
                        continue;
                    if (seen != null &&
                        !seen.Add((s.DXCallsign ?? string.Empty).Trim().ToUpperInvariant() + "|" + s.BandText.Trim().ToUpperInvariant()))
                        continue;   // already counted this callsign on this band
                    if (seenFreq != null &&
                        !seenFreq.Add(s.FreqMhz > 0 ? s.FreqMhz.ToString("F5", CultureInfo.InvariantCulture) : ("_" + s.SpotKey)))
                        continue;   // a newer station already occupies this frequency
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
                            Band = NormalizeClusterBandKey(spot.BandText),
                            IsOnFrequency = spot.IsOnFrequency
                        });
                    }
                }

                MapControl.ShowClusterSpots(spots, homell.Lat, homell.Long, GetMapRadiusKm());
            }
            catch (System.Exception swallowed) { Log.Swallow(swallowed); }
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
            catch (System.Exception swallowed) { Log.Swallow(swallowed); }
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

            if (clusterUnconfirmedCountText != null)
            {
                int unconfirmed = clusterVisibleSpots != null
                    ? clusterVisibleSpots.Count(s => s.IsUnconfirmedCountry)
                    : 0;
                clusterUnconfirmedCountText.Text = unconfirmed.ToString(CultureInfo.InvariantCulture);
                // Theme text at 0, amber when there are worked-but-unconfirmed spots on view.
                clusterUnconfirmedCountText.Foreground = unconfirmed > 0
                    ? (Brush)new SolidColorBrush(Color.FromRgb(0xFF, 0x8C, 0x00))
                    : ThemeManager.Brush("TextBrush");
            }
        }

        // ── New-country spot sound alert ───────────────────────────────────────────────────────────
        // A "New Country" spot can arrive invisibly: Live Scale narrows the table to the active band
        // and sorts strictly by frequency (no top pin), so a rare one on another band — or far from
        // the VFO — never catches the eye. The sound alert fires on ARRIVAL, judged against the
        // user's OWN band/mode preferences (never Live Scale's temporary Active-band narrowing).

        private bool ClusterSpotQualifiesForNewCountryAlert(ClusterSpotViewItem item)
        {
            if (item == null || !item.IsNeededCountry) return false;
            if (!Properties.Settings.Default.ClusterNewCountrySoundOn) return false;
            if (!IsClusterModeEnabled(item.Mode)) return false;

            // Ignore stale spots (e.g. the backlog replayed on connect) outside the "Last N min" window.
            if (item.UnixTime <= 0 ||
                item.UnixTime < DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (clusterLastMinutesFilterValue * 60L))
                return false;

            // Band gate: the user's preference, not Live Scale's forced Active mode.
            string mode = clusterLiveScaleOn
                ? (clusterPreLiveScaleBandFilterMode ?? "PreSelected")
                : (Properties.Settings.Default.ClusterBandFilterMode ?? "PreSelected");
            if (string.Equals(mode, "All", StringComparison.OrdinalIgnoreCase)) return true;
            string band = NormalizeClusterBandKey(item.BandText);
            if (string.Equals(mode, "Active", StringComparison.OrdinalIgnoreCase))
            {
                string active = NormalizeClusterBandKey(TB_Band != null ? TB_Band.Text : string.Empty);
                return !string.IsNullOrWhiteSpace(active) && string.Equals(active, band, StringComparison.OrdinalIgnoreCase);
            }
            return GetEnabledClusterBands().Contains(band);
        }

        // One ring per burst: a batch (or reconnect backlog) with several needed spots plays once.
        private void PlayNewCountrySpotAlert()
        {
            var now = DateTime.UtcNow;
            if ((now - _lastNewCountryAlertUtc).TotalSeconds < 3) return;
            _lastNewCountryAlertUtc = now;
            PlayClusterAlertSound(Properties.Settings.Default.ClusterNewCountrySound);
        }

        // Same arrival test as the new-country alert, but for a worked-but-unconfirmed-on-LoTW spot.
        private bool ClusterSpotQualifiesForUnconfirmedAlert(ClusterSpotViewItem item)
        {
            if (item == null || !item.IsUnconfirmedCountry) return false;
            if (!Properties.Settings.Default.ClusterUnconfirmedSoundOn) return false;
            if (!IsClusterModeEnabled(item.Mode)) return false;

            if (item.UnixTime <= 0 ||
                item.UnixTime < DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (clusterLastMinutesFilterValue * 60L))
                return false;

            string mode = clusterLiveScaleOn
                ? (clusterPreLiveScaleBandFilterMode ?? "PreSelected")
                : (Properties.Settings.Default.ClusterBandFilterMode ?? "PreSelected");
            if (string.Equals(mode, "All", StringComparison.OrdinalIgnoreCase)) return true;
            string band = NormalizeClusterBandKey(item.BandText);
            if (string.Equals(mode, "Active", StringComparison.OrdinalIgnoreCase))
            {
                string active = NormalizeClusterBandKey(TB_Band != null ? TB_Band.Text : string.Empty);
                return !string.IsNullOrWhiteSpace(active) && string.Equals(active, band, StringComparison.OrdinalIgnoreCase);
            }
            return GetEnabledClusterBands().Contains(band);
        }

        private void PlayUnconfirmedSpotAlert()
        {
            var now = DateTime.UtcNow;
            if ((now - _lastUnconfirmedAlertUtc).TotalSeconds < 3) return;
            _lastUnconfirmedAlertUtc = now;
            PlayClusterAlertSound(Properties.Settings.Default.ClusterUnconfirmedSound);
        }

        // Plays the alert sound named in Options → General. A *.wav name plays from C:\Windows\Media,
        // anything else is one of the five system sounds. Also used by the options page's Test button,
        // hence static. This overload uses the saved output device.
        static System.Media.SoundPlayer _clusterAlertWavPlayer;   // kept alive so playback isn't GC-cut

        internal static void PlayClusterAlertSound(string name)
            => PlayClusterAlertSound(name, Properties.Settings.Default.SoundOutputDevice);

        // deviceName empty/default -> the Windows default device (original behavior). A specific device
        // (e.g. the speakers, chosen so alerts don't go down a USB radio codec) needs a WAV to target it,
        // so a system-sound name is mapped to a comparable Windows\Media WAV in that case.
        internal static void PlayClusterAlertSound(string name, string deviceName)
        {
            try
            {
                string n = (name ?? string.Empty).Trim();
                uint deviceId = WaveOutPlayer.ResolveDeviceId(deviceName);
                bool specificDevice = !string.IsNullOrWhiteSpace(deviceName) && deviceId != 0xFFFFFFFF;

                if (specificDevice)
                {
                    string wav = ResolveAlertWavPath(n);
                    if (wav != null) { WaveOutPlayer.Play(wav, deviceId); return; }
                    // No WAV available -> fall through to default-device playback below.
                }

                if (n.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
                {
                    string path = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media", n);
                    if (System.IO.File.Exists(path))
                    {
                        _clusterAlertWavPlayer = new System.Media.SoundPlayer(path);
                        _clusterAlertWavPlayer.Play();   // async; no blocking of the UI thread
                        return;
                    }
                    // fall through to the default chime if the file vanished
                }
                switch (n)
                {
                    case "Beep": System.Media.SystemSounds.Beep.Play(); break;
                    case "Exclamation": System.Media.SystemSounds.Exclamation.Play(); break;
                    case "Question": System.Media.SystemSounds.Question.Play(); break;
                    case "Critical": System.Media.SystemSounds.Hand.Play(); break;
                    default: System.Media.SystemSounds.Asterisk.Play(); break;   // "Chime"
                }
            }
            catch (System.Exception swallowed) { Log.Swallow(swallowed); }
        }

        // A playable WAV path for a sound name, or null if none exists. A *.wav name resolves in
        // C:\Windows\Media; the five system-sound names map to a comparable Windows\Media WAV so they
        // can still be routed to a chosen device (System.Media system sounds can't target a device).
        static string ResolveAlertWavPath(string n)
        {
            string mediaDir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media");
            string file;
            if (n.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
                file = n;
            else
                switch (n)
                {
                    case "Beep": file = "Windows Ding.wav"; break;
                    case "Exclamation": file = "Windows Exclamation.wav"; break;
                    case "Question": file = "Windows Ding.wav"; break;
                    case "Critical": file = "Windows Critical Stop.wav"; break;
                    default: file = "Windows Notify.wav"; break;   // "Chime"
                }
            string path = System.IO.Path.Combine(mediaDir, file);
            return System.IO.File.Exists(path) ? path : null;
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
                    clusterVisibleSpots = new BulkObservableCollection<ClusterSpotViewItem>();
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
                clusterVisibleSpots = new BulkObservableCollection<ClusterSpotViewItem>();
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
            var selectedSpot = ClusterSpotFromEventSource(e.OriginalSource as DependencyObject, sender as DataGrid);
            if (selectedSpot == null)
            {
                return;
            }

            TuneToClusterSpot(selectedSpot);
        }

        // Which spot is under the mouse. The cell's DataContext is the direct answer; the walk up to
        // the row covers a click that landed on padding between cells, and the grid's selection is the
        // last resort. Shared by the double-click (tune to it) and the right-click (menu about it), so
        // both agree on which row was meant.
        private ClusterSpotViewItem ClusterSpotFromEventSource(DependencyObject source, DataGrid grid)
        {
            DataGridCell cell = FindVisualParent<DataGridCell>(source);
            if (cell != null)
            {
                var fromCell = cell.DataContext as ClusterSpotViewItem;
                if (fromCell != null) return fromCell;
            }

            while (source != null && !(source is DataGridRow))
            {
                source = VisualTreeHelper.GetParent(source);
            }

            var row = source as DataGridRow;
            return row?.Item as ClusterSpotViewItem ?? grid?.SelectedItem as ClusterSpotViewItem;
        }

        // RIGHT-CLICK ON A SPOT. One menu, one item for now: put this station on the Try Again list.
        // Handled (and the event stopped) only when a spot was actually hit - a right-click on the
        // header or on empty space below the last row must keep doing whatever it did before.
        private void ClusterSpotsGrid_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            var grid = sender as DataGrid;
            if (grid == null) return;

            var spot = ClusterSpotFromEventSource(e.OriginalSource as DependencyObject, grid);
            if (spot == null) return;

            try
            {
                var menu = BuildClusterSpotContextMenu(spot);
                if (menu == null) return;

                // THE MENU MUST NOT COVER THE SPOT IT IS ABOUT. Opening at the mouse point - which is
                // what a context menu does by default - put the card straight over the row that had
                // just been right-clicked, so the callsign, frequency and mode being acted on were
                // hidden at the moment of deciding. The card names the station at the top, but the
                // operator still wants to see the row itself: whether it is a new country, whether it
                // is a LoTW user, what the comment says.
                //
                // So it sits ABOVE THE ROW. Above rather than below because the cluster grows downwards
                // from the top: a card hanging below would cover the rows the operator is working
                // through next, while the space above holds spots he has already passed over. Placement
                // is measured against the row, not the grid. WPF still turns it the other way up by
                // itself if the row is near the top of the screen with no room above - the row stays
                // uncovered either way, which is the point.
                var row = FindVisualParent<DataGridRow>(e.OriginalSource as DependencyObject);
                if (row != null)
                {
                    menu.PlacementTarget = row;
                    menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;

                    // ALWAYS THE SAME PLACE ACROSS, a few pixels in from the left edge of the table.
                    // Top placement already lines the card up with the row's left edge, and the row is
                    // as wide as the table, so a fixed 8 is 8px from the table's edge on every spot.
                    //
                    // It used to be offset by the pointer's own position, to keep the card under the
                    // hand. That made the card land somewhere different on every right-click, so there
                    // was nowhere to learn to look and the eye had to hunt for it each time. A card
                    // that is always in the same place is read faster than one that follows the mouse.
                    menu.HorizontalOffset = 8;

                    // AND A LITTLE HIGHER STILL. With Top placement the card's bottom edge lands exactly
                    // on the row's top edge, which leaves them touching - the card looks like part of the
                    // row, and the row's own top border is lost under it. NEGATIVE, because the offset is
                    // measured in screen coordinates where down is positive, so up is below zero.
                    menu.VerticalOffset = -10;
                }
                else
                {
                    menu.PlacementTarget = grid;
                }

                menu.IsOpen = true;
                e.Handled = true;
            }
            catch (System.Exception swallowed) { Log.Swallow(swallowed); }
        }

        // THE MENU. Light green, the Try Again colour - the styles live in Themes/Controls.xaml and are
        // shared with the Try Again window's own right-click menu, so the whole feature reads as one
        // thing. NOT the log's white-and-blue menu resources: those belong to the log.
        private ContextMenu BuildClusterSpotContextMenu(ClusterSpotViewItem spot)
        {
            var menu = new ContextMenu { Style = (Style)FindResource("HolyCtxMenu") };
            var darkGreen = (Brush)new SolidColorBrush(Color.FromRgb(0x1B, 0x5E, 0x20));

            // Which spot this menu is about, spelled out. A cluster list scrolls under the pointer as
            // new spots arrive, and a menu that does not name its station leaves the operator guessing
            // whether he right-clicked the row he was aiming at.
            //
            // FLAG, CALLSIGN, FREQUENCY, MODE - each carrying the same meaning in colour that it carries
            // in the table behind the card, so the line is read the same way the row is: the flag says
            // the country at a glance, the frequency is its band's colour, and the mode is red for CW
            // and blue for SSB.
            //
            // The colours are spelled out here rather than taken from the spot's own ModeForeground.
            // That property falls back to the theme's TextBrush for anything that is not CW or SSB, and
            // TextBrush is a LIGHT colour in the dark schemes - which on this light green card would be
            // a line of text nobody could read. The band brushes are fixed colours and are safe as they
            // are. Same reasoning as the rest of this window's palette.
            string call = (spot.DXCallsign ?? string.Empty).Trim().ToUpperInvariant();
            string freq = (spot.FreqDisplayText ?? spot.FreqText ?? string.Empty).Trim();
            string mode = (spot.Mode ?? string.Empty).Trim().ToUpperInvariant();

            // ONE LINE: flag, callsign, frequency, mode.
            //
            // It is wrapped in a MenuItem of our own carrying HolyCtxTitle, and that is not decoration.
            // Handing a bare panel to ContextMenu.Items makes WPF wrap it in a DEFAULT MenuItem, which
            // reserves an icon column, a shortcut-text column and a submenu arrow that this line will
            // never use - measured, 66px of empty structure around a 90px title. Worse, the items panel
            // stretches every row to the widest, so that invisible 66px set the width of the whole card
            // and of the button underneath it. HolyCtxTitle is a Border and the content, nothing else.
            var titleContent = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(3, 2, 3, 4)
            };

            if (!string.IsNullOrWhiteSpace(spot.FlagPath))
            {
                try
                {
                    titleContent.Children.Add(new System.Windows.Controls.Image
                    {
                        Source = new System.Windows.Media.Imaging.BitmapImage(new Uri(spot.FlagPath)),
                        Width = 24,
                        Height = 16,
                        Stretch = Stretch.Uniform,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(0, 0, 5, 0)
                    });
                }
                catch (System.Exception swallowed) { Log.Swallow(swallowed); }
            }

            titleContent.Children.Add(MenuTitlePart(call, darkGreen, 0));
            if (freq.Length > 0)
                titleContent.Children.Add(MenuTitlePart(freq, GetBandBrush((spot.BandText ?? string.Empty).Trim()), 6));
            if (mode.Length > 0)
                titleContent.Children.Add(MenuTitlePart(mode, MenuModeBrush(mode), 6));

            menu.Items.Add(new MenuItem
            {
                Header = titleContent,
                Style = (Style)FindResource("HolyCtxTitle")
            });
            menu.Items.Add(new Separator { Style = (Style)FindResource("HolyCtxSep") });

            var tryAgain = new MenuItem
            {
                Header = "Copy into Try Again",
                Style = (Style)FindResource("HolyCtxItemGo"),
                // A RIGHT ARROW, to the RIGHT of the words: the line reads "Copy into Try Again" and
                // then points at where it is going. What was there first was the pair of circular
                // arrows out of the icon font - the mark this program uses for Undo and for a refresh,
                // which is the opposite of the feeling wanted for something being pushed somewhere.
                //
                // DRAWN, not typed. It began as the text arrow U+2192, which was too thin to see at
                // this size, and a font's arrow cannot be made thicker - only bigger, which would have
                // widened the card. A shape can: this one is a 3px shaft into a 9px head inside the
                // same 13x12 box the character occupied, so it is far heavier at exactly the same size.
                // Drawing it also settles the question of whether a given font has the glyph at all.
                //
                // The fill FOLLOWS THE ITEM'S Foreground, which a shape does not inherit the way text
                // does - hence the binding. Without it the arrow would stay dark when the row lights up
                // dark green, and disappear into it.
                Icon = MakeRightArrow(),
                ToolTip = "Put this station on the Try Again list, to come back to later"
            };
            tryAgain.Click += (s, args) => CopySpotIntoTryAgain(spot);
            menu.Items.Add(tryAgain);

            return menu;
        }

        // One coloured word of the menu's title line.
        private static TextBlock MenuTitlePart(string text, Brush colour, double leftGap)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Foreground = colour,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(leftGap, 0, 0, 0)
            };
        }

        // The mode's colour for the menu title: the cluster's own rule - red for CW, blue for SSB -
        // but with a FIXED dark for everything else instead of the theme's TextBrush. The card is light
        // green whatever colour scheme is running, and TextBrush goes light in the dark schemes.
        private static Brush MenuModeBrush(string mode)
        {
            if (mode == "CW") return Brushes.Red;
            if (mode == "SSB") return new SolidColorBrush(Color.FromRgb(0x15, 0x65, 0xC0));
            return new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A));
        }

        // The solid right arrow the "Copy into Try Again" button carries. 13 wide by 12 high - the same
        // box a text arrow filled - but a 3px shaft and a 9px head instead of a hairline.
        private static UIElement MakeRightArrow()
        {
            var arrow = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M 0,4.5 L 7.5,4.5 L 7.5,1.5 L 13,6 L 7.5,10.5 L 7.5,7.5 L 0,7.5 Z"),
                Width = 13,
                Height = 12,
                Stretch = Stretch.Fill,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            // Follow the menu item's own text colour, so the arrow goes white with the words when the
            // row is highlighted. A Path has no inherited Foreground of its own.
            arrow.SetBinding(System.Windows.Shapes.Shape.FillProperty,
                new System.Windows.Data.Binding("Foreground")
                {
                    RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor)
                    {
                        AncestorType = typeof(MenuItem)
                    }
                });

            return arrow;
        }

        // Sends one cluster spot to the Try Again list. The frequency is stored exactly as the cluster
        // gave it, so pressing Try later tunes to the same place the spot said.
        private void CopySpotIntoTryAgain(ClusterSpotViewItem spot)
        {
            if (spot == null || dal == null) return;

            string call = (spot.DXCallsign ?? string.Empty).Trim();
            if (call.Length == 0) return;

            try
            {
                dal.AddTryAgain(call, spot.FreqText, spot.Mode, spot.BandText);
                // Whether it was added or was already there, the list is not empty now: show the
                // button, and let an open Try Again window pick the new row up.
                RefreshTryAgain();
            }
            catch (System.Exception swallowed) { Log.Swallow(swallowed); }
        }

        // ---------------------------------------------------------------------------------------
        // TRY AGAIN
        //
        // A list of stations the operator saw on the cluster and meant to come back to. Kept in the
        // database rather than in memory, so it is still there tomorrow - the whole point of "try
        // again" is that the try does not have to be today.
        // ---------------------------------------------------------------------------------------

        // The open Try Again window, or null. One at a time: a second copy of the same list would show
        // stale rows the moment the other one deleted something.
        private TryAgainWindow _tryAgainWindow;

        // Brings the button and any open window back in step with the table. Called after anything
        // that can change the list: a spot copied in, a row deleted, a QSO logged.
        private void RefreshTryAgain()
        {
            try
            {
                int n = dal != null ? dal.GetTryAgainCount() : 0;

                if (Btn_TryAgain != null)
                {
                    // Hidden entirely at zero: the button's only job is to open a list, and an empty
                    // list is not worth a trip. The count is in the TOOLTIP, not on the face - the row
                    // is only 677px wide and a face wide enough for "Try Again (100)" ran over the
                    // activity hint beside it.
                    Btn_TryAgain.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
                    Btn_TryAgain.ToolTip = n == 1
                        ? "1 station waiting to be tried again"
                        : string.Format(CultureInfo.InvariantCulture, "{0} stations waiting to be tried again", n);
                }

                if (_tryAgainWindow != null)
                    _tryAgainWindow.ReloadList();
            }
            catch (System.Exception swallowed) { Log.Swallow(swallowed); }
        }

        private void Btn_TryAgain_Click(object sender, RoutedEventArgs e)
        {
            ShowTryAgainWindow();
        }

        private void ShowTryAgainWindow()
        {
            try
            {
                if (_tryAgainWindow != null)
                {
                    // Already open, possibly behind something or on the other screen. Un-minimise and
                    // raise it rather than opening a second one.
                    if (_tryAgainWindow.WindowState == WindowState.Minimized)
                        _tryAgainWindow.WindowState = WindowState.Normal;
                    _tryAgainWindow.Activate();
                    return;
                }

                // NOT owned by the main window, for the same reason the Log Workshop is not: an owned
                // window is pinned above its owner for ever, so clicking the main window could never
                // bring it forward. This is a second place to work, not a dialog.
                //
                // Asked BEFORE the window is built, because building it restores any saved placement.
                bool firstEverOpen = !WindowBounds.HasSaved("TryAgain");

                var win = new TryAgainWindow(dal);
                win.TryRequested += TryAgainEntryPressed;
                win.ListChanged += RefreshTryAgain;
                win.Closed += (s, args) => { _tryAgainWindow = null; };
                _tryAgainWindow = win;

                if (firstEverOpen)
                    CenterOnLogTable(win);

                win.Show();
            }
            catch (System.Exception swallowed) { Log.Swallow(swallowed); }
        }

        // FIRST EVER OPENING: over the middle of the log table. Only the first, and only when the
        // operator has never placed this window himself - after that his own position is restored and
        // this is never consulted again.
        //
        // Measured from the LOG TABLE'S OWN position on screen, which is a real rectangle on whichever
        // monitor the main window is on. SystemParameters.WorkArea is not consulted and must not be:
        // it answers for the primary screen only, and trusting it is what stranded a window on this
        // desktop once before. PointToScreen returns device pixels while Left/Top are measured in
        // device-INDEPENDENT units, so the two are converted through the window's own transform - on a
        // scaled desktop, skipping that puts the window a long way from the middle of anything.
        private void CenterOnLogTable(Window win)
        {
            try
            {
                if (win == null || QSODataGrid == null) return;
                if (!QSODataGrid.IsVisible || QSODataGrid.ActualWidth <= 0 || QSODataGrid.ActualHeight <= 0) return;

                var source = PresentationSource.FromVisual(this);
                double sx = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
                double sy = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
                if (sx <= 0) sx = 1.0;
                if (sy <= 0) sy = 1.0;

                Point topLeftDevice = QSODataGrid.PointToScreen(new Point(0, 0));
                double centerX = (topLeftDevice.X / sx) + (QSODataGrid.ActualWidth / 2.0);
                double centerY = (topLeftDevice.Y / sy) + (QSODataGrid.ActualHeight / 2.0);

                win.WindowStartupLocation = WindowStartupLocation.Manual;

                // BOTH sizes are the window's own business now (SizeToContent="WidthAndHeight"), worked
                // out from the list it is about to show. That means neither Width nor Height exists to
                // centre on yet - both read NaN until the window has measured itself, and NaN/2 would
                // put it nowhere at all. So the whole placement waits for Loaded, which runs after the
                // measure and before the window is painted.
                RoutedEventHandler placeIt = null;
                placeIt = (s, args) =>
                {
                    win.Loaded -= placeIt;
                    double w = win.ActualWidth;
                    double h = win.ActualHeight;
                    if (w > 0) win.Left = centerX - (w / 2.0);
                    if (h > 0) win.Top = centerY - (h / 2.0);
                };
                win.Loaded += placeIt;
            }
            catch (System.Exception swallowed) { Log.Swallow(swallowed); }
        }

        // THE TRY BUTTON. Everything it has to do is already written: a spot's callsign, frequency and
        // mode go to the radio through TuneToClusterSpot, which is what a double-click on a live spot
        // does. A Try Again entry is those same three things, so it is handed over as one.
        private void TryAgainEntryPressed(TryAgainEntry entry)
        {
            if (entry == null) return;

            TuneToClusterSpot(new ClusterSpotViewItem
            {
                DXCallsign = entry.DXCallsign,
                FreqText = entry.FreqText,
                Mode = entry.Mode
            });

            // The main window is where the QSO gets typed, so bring it forward - the operator pressed
            // Try in order to work the station, not to keep looking at the list.
            try { Activate(); } catch (System.Exception swallowed) { Log.Swallow(swallowed); }
        }

        // He is in the log now, so he comes off the list. Matched on the callsign's identity, so
        // logging 4Z5SL/M clears an entry that says 4Z5SL, and every entry for that station goes -
        // on whatever band it was spotted.
        private void RemoveFromTryAgainAfterLogging(string dxCallsign)
        {
            try
            {
                if (dal == null || string.IsNullOrWhiteSpace(dxCallsign)) return;
                if (dal.RemoveTryAgainForCallsign(dxCallsign) > 0)
                    RefreshTryAgain();
            }
            catch (System.Exception swallowed) { Log.Swallow(swallowed); }
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
                        var dxcc = CountryLookup.Shared.Resolve((spot.DXCallsign ?? string.Empty).Trim());
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
            catch (System.Exception swallowed) { Log.Swallow(swallowed); }
        }

        private async void TuneToClusterSpot(ClusterSpotViewItem spot)
        {
            if (spot == null)
            {
                return;
            }

            // Explicitly selecting a spot cancels any earlier F9 dismissal — the user wants it filled.
            _clusterDismissedCall = null;

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

            // Mark this as a cluster-originated fill (like the on-frequency auto-fill does), so that when
            // the radio later tunes off this spot's frequency, the existing "left the frequency" logic in
            // UpdateClusterFrequencyHighlight clears the DX box (and its name/country/locator/QRZ photo).
            // Without this flag a double-clicked spot was treated like a hand-typed call and never cleared,
            // so stale info lingered after tuning away (e.g. re-engaging Live Scale off the spot's freq).
            // Set AFTER the assignment above: setting .Text runs TB_DXCallsign_TextChanged first.
            _clusterAutoFilledDXCall = true;
            _clusterAutoFilledFreqMhz = freqMhz;
            // CAT may still be slewing to this spot. Hold the "is it still on frequency?" rule off until
            // the radio has actually arrived, or it would clear the call the operator just double-clicked.
            _clusterAutoFilledReached = false;
            _clusterAutoFilledAtUtc = DateTime.UtcNow;

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

            // USB/LSB are user-chosen sidebands (Channels). The logged mode is SSB; the rig mapping
            // (MapClusterModeToRigMode) keeps the exact sideband.
            if (mode == "USB" || mode == "LSB")
            {
                return "SSB";
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
                case "USB":
                    return PM_SSB_U;   // explicit sideband (Channels) — honor the operator's choice
                case "LSB":
                    return PM_SSB_L;
                case "SSB":
                    return freqMhz < 10.0 ? PM_SSB_L : PM_SSB_U;
                case "FM":
                    return PM_FM;
                case "AM":
                    return PM_AM;
                case "DIGI":
                case "FT8":
                    return PM_DIG_U;   // data-USB: FT8/PSK/etc. The rig's OmniRig .ini maps DIG_U to
                                       // the actual DATA command.
                case "RTTY":
                    return PM_DIG_L;   // separate slot from FT8/data so the .ini can route RTTY to a
                                       // RTTY/FSK command (RTTY is traditionally lower). OmniRig has no
                                       // native RTTY mode; DIG_L is the closest, per how Log4OM/OmniRig work.
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
            UpdateRadioUndoButtons();
            PulseUndoIcon();   // make the main-GUI icon appear/jump too, so a cluster undo isn't missed
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

        // Clears the entire shared undo history (the "reset" action triggered by a long press on the
        // cluster button). Refreshes both controls since the list is shared.
        private void ResetClusterUndo()
        {
            if (clusterUndoStates.Count == 0) return;
            clusterUndoStates.Clear();
            UpdateRadioUndoButtons();
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
                UpdateRadioUndoButtons();

                string freqText = undoState.FrequencyText;
                string modeText = undoState.ModeText;
                string dxCallsignText = undoState.DxCallsignText;

                if (!double.TryParse(freqText, NumberStyles.Float, CultureInfo.InvariantCulture, out double freqMhz) || freqMhz <= 0)
                {
                    return;
                }

                TB_Frequency.Text = freqMhz.ToString("0.0###", CultureInfo.InvariantCulture);
                SelectLoggerMode(modeText);
                // Restored callsign is a complete call, not typing — don't pop the suggestions dropdown.
                suppressNextCallsignSuggestions = true;
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

        // Both undo controls reflect the one shared history, so every push/pop/clear refreshes both:
        // the main-GUI icon (visible only when non-empty) and the cluster title-bar button (a no-op
        // when the cluster window is closed).
        private void UpdateRadioUndoButtons()
        {
            UpdateLogRadioUndoButtonState();
            UpdateClusterUndoButtonState();
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

