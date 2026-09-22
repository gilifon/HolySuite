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
    // Map / azimuth glue: Leaflet map control wiring, spot dots, azimuth arc, compass overlay.
    // Move-only split from MainWindow.xaml.cs; no behavior change.
    public partial class MainWindow
    {

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
                // Hide all graphics options. (The map is not one of them any more - it has its own
                // window, which this setting does not open or close.)
                Img_CustomGraphics.Visibility = Visibility.Collapsed;
                Img_QRZGraphics.Visibility = Visibility.Collapsed;
                MapDisabledPanel.Visibility = Visibility.Visible;
                this.MinWidth = 800;
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

        // The azimuth map stretches horizontally but is a fixed 325px tall, so without a floor the
        // window could be narrowed until the map became a portrait rectangle. Everything to the left
        // of the map (the blue panel + gaps) plus the window border is a constant overhead equal to
        // (WindowWidth - MapWidth); the width at which the map is exactly square is therefore
        // overhead + mapHeight. Measuring it live keeps it correct across DPI / chrome differences.
        private void EnforceMapSquareMinWidth()
        {
            if (!Properties.Settings.Default.IsShowAzimuthControl) return;
            if (MapPinArea == null) return;

            // Measured on the place the map is pinned to, which is the picture area itself: the map
            // window, pinned, is exactly that size.
            double mapWidth = MapPinArea.ActualWidth;
            double mapHeight = MapPinArea.ActualHeight > 0 ? MapPinArea.ActualHeight : MapPinArea.Height;
            if (mapWidth <= 0 || this.ActualWidth <= 0 || double.IsNaN(mapHeight) || mapHeight <= 0)
                return;

            double overhead = this.ActualWidth - mapWidth;   // blue panel + gaps + window chrome (constant)
            double squareMinWidth = Math.Ceiling(overhead + mapHeight);
            if (Math.Abs(this.MinWidth - squareMinWidth) > 0.5)
                this.MinWidth = squareMinWidth;
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
                        DXCC entityDXCC = CountryLookup.Shared.Resolve(TB_DXCallsign.Text);
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

                    // If the current DX matches the cluster spot the user just selected, pass that
                    // spot's spotter location so the map's DE button can center on the spotter.
                    double? spotterLat = null, spotterLon = null;
                    if (_selectedSpotterLat.HasValue && _selectedSpotterLon.HasValue &&
                        string.Equals((TB_DXCallsign.Text ?? string.Empty).Trim(), _selectedSpotterDxCall, StringComparison.OrdinalIgnoreCase))
                    {
                        spotterLat = _selectedSpotterLat;
                        spotterLon = _selectedSpotterLon;
                    }

                    // Don't update map if Empty mode is active, or while the map window is closed
                    if (Properties.Settings.Default.MapAreaDisplayMode != 4 && MapControl != null)
                    {
                        _homeMapShownAt = DateTime.MinValue;   // the home map is no longer what is showing
                        _dxMapDrawnAt = DateTime.UtcNow;
                        MapControl.ShowMap(ll.Lat, ll.Long, autoFitRadius, Azimuth, homell.Lat, homell.Long, spotterLat, spotterLon);
                    }
                }
                catch (Exception e)
                {
                    Log.Swallow(e);
                    ClearAzimuth();
                }
            }
            else
            {
                ClearAzimuth();
            }
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

        // When the home map was last drawn, cleared again by any drawing of a DX map. Lets the delayed
        // clearing after Add / F9 skip redrawing a home map that ClearBtn_Click has only just drawn.
        private DateTime _homeMapShownAt = DateTime.MinValue;

        // When a DX station's map was last drawn. After Add the home map is drawn late, behind the
        // keyboard, and must not cover the map of the next station if that is already showing.
        private DateTime _dxMapDrawnAt = DateTime.MinValue;

        private bool HomeMapJustShown() =>
            (DateTime.UtcNow - _homeMapShownAt).TotalSeconds < 2;

        private void ShowHomeMap()
        {
            if (MapControl == null) return;

            // Don't show map if Empty mode is active
            if (Properties.Settings.Default.MapAreaDisplayMode == 4)
                return;

            _homeMapShownAt = DateTime.UtcNow;

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
                    // The shape comes from MaidenheadLocator, the same place the parser that just
                    // threw takes it from - the old text named only KM72 and KM72OR, which read as
                    // though 8 and 10 characters were not allowed.
                    MapControl.ShowPlaceholder("Invalid My Locator: \"" + TB_MyLocator.Text.Trim() + "\"&#x0a;Enter a valid grid square: " + MaidenheadLocator.ShortFormatHint);
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
                    // Cluster map: the JavaScript already rescaled the view for the new radius, so
                    // do NOT re-render here — a re-render recenters on home and discards a view the
                    // user dragged/zoomed to. Only the non-cluster azimuth readout needs a refresh.
                    if (!MapControl.IsClusterMode)
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

        private void OnMapSpotTuneRequested(string freq, string mode)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                // The click was in the map's own window, which took the keyboard with it. Give it
                // back, so the callsign the operator types next lands in the log and not in the map.
                if (!IsActive) Activate();

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

        // Colored vs. Black & White map: the flag is baked into the map HTML at generation time
        // (like the day/night flag), so a full RefreshMap re-reads the setting and redraws.
        public void UpdateMapColorMode()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (MapControl != null)
                {
                    MapControl.RefreshMap();
                }
            }), DispatcherPriority.Background);
        }

        // ---- the map window ---------------------------------------------------------------------

        private MapWindow _mapWindow;

        // THE MAP, WHILE ITS WINDOW IS ON SCREEN - and null while it is closed. Every place that draws
        // on the map already asks "MapControl == null" before it does, from the days when the map sat
        // in this window, so answering null for a closed window keeps all of them right without
        // touching them. The window keeps the one map for the whole run; closing only hides it.
        private ToolsUserControls.MapUserControl MapControl =>
            _mapWindow != null && _mapWindow.IsVisible ? _mapWindow.Map : null;

        // Built once, from MainWindow_Loaded. Shown later, when this window has been drawn - a pinned
        // map shown any earlier stands alone on the desktop, over the empty place where the main
        // window is about to appear.
        private void CreateMapWindow()
        {
            _mapWindow = new MapWindow(this, MapPinArea);
            var map = _mapWindow.Map;
            map.RadiusChanged += OnMapRadiusChanged;
            map.SpotTuneRequested += OnMapSpotTuneRequested;
            map.SpotHovered += OnMapSpotHovered;
            map.SpotHoverEnded += OnMapSpotHoverEnded;
            _mapWindow.ClosedByOperator += () => MapMenuItem.IsChecked = false;
            MapMenuItem.IsChecked = Properties.Settings.Default.ShowMapWindow;

            // THE MAP COMES UP AT EVERY START unless Options > User Interface > "Show the map window"
            // is unticked. Closing it (its X, or View > Map) is for this run only and is not
            // remembered: an operator who closed it once, and then set the picture area to Empty,
            // started the program with no map anywhere and no sign that one exists.
            EventHandler showMap = null;
            showMap = (s, e) =>
            {
                ContentRendered -= showMap;
                if (!Properties.Settings.Default.ShowMapWindow) return;
                try { ShowMapWindow(); }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
            };
            ContentRendered += showMap;
        }

        private void ShowMapWindow()
        {
            if (_mapWindow == null) return;
            bool wasShowing = _mapWindow.IsVisible;
            _mapWindow.ShowWithOwner(this);
            MapMenuItem.IsChecked = true;
            if (wasShowing) return;

            // Nothing was drawn on it while it was closed, so it gets the picture of now: the DX
            // station's map if there is one in the callsign box, the home map if not, and the spots.
            if (!string.IsNullOrWhiteSpace(TB_DXCallsign.Text)) SetAzimuth();
            else ShowHomeMap();
            UpdateClusterSpotsOnMap();
        }

        // View > Map: open it, or close it - a pinned map has no title bar and so no X of its own.
        private void MapMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (_mapWindow == null) return;
            bool open = !_mapWindow.IsVisible;
            if (open) ShowMapWindow();
            else _mapWindow.HideMap();
            MapMenuItem.IsChecked = open;
        }

        // "Show the map window" ticked or unticked in Options: the map appears or goes at once.
        private void ApplyMapWindowSetting()
        {
            if (_mapWindow == null) return;
            if (Properties.Settings.Default.ShowMapWindow) ShowMapWindow();
            else
            {
                _mapWindow.HideMap();
                MapMenuItem.IsChecked = false;
            }
        }
    }

    // THE MAP'S OWN WINDOW. It floats - dragged and sized like any window - or it is PINNED: laid
    // exactly over the picture area of the main window, where the map used to live, and carried along
    // when the main window moves or changes size. The pin is in the map's top right corner.
    //
    // Pinned, it has no title bar and no frame of its own, so it looks as the map always did. Floating,
    // it has a title bar, and a frame wide enough to take hold of: the map is a WebBrowser, which
    // takes every mouse event over it, so the frame is the only edge a window can be resized by.
    //
    // (Kept in this file for now. A class in a file of its own has to be listed in the project file,
    // and that cannot be edited while Visual Studio has the solution open. It can move to
    // MapWindow.cs later without any change.)
    public class MapWindow : Window
    {
        private const string BoundsKey = "MapWindow";
        private const double TitleHeight = 32;
        private const double FloatingFrame = 6;      // = the resize border, see above
        private const double FloatingMinWidth = 250;
        private const double FloatingMinHeight = 200;

        private readonly Window _main;
        private readonly FrameworkElement _pinArea;
        private readonly Border _frame;
        private readonly Border _titleBar;

        private bool _pinned;
        private bool _allowClose;
        private bool _placing;                       // our own moves are not the operator's
        private Rect _floatRect = Rect.Empty;        // where the operator last had it floating
        private Rect _pinnedRect = Rect.Empty;       // where it was last laid over the main window

        public ToolsUserControls.MapUserControl Map { get; }

        // The X, pressed by the operator. The main window keeps its View > Map tick in step.
        public event Action ClosedByOperator;

        public MapWindow(Window main, FrameworkElement pinArea)
        {
            _main = main;
            _pinArea = pinArea;

            Title = "Map";
            ShowInTaskbar = false;
            ShowActivated = false;                   // appearing must not take the keyboard from the log
            WindowStyle = WindowStyle.None;
            WindowStartupLocation = WindowStartupLocation.Manual;
            SetResourceReference(BackgroundProperty, "WindowBg");

            Map = new ToolsUserControls.MapUserControl();
            _titleBar = BuildTitleBar();
            var dock = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(_titleBar, Dock.Top);
            dock.Children.Add(_titleBar);
            dock.Children.Add(Map);
            _frame = new Border { BorderBrush = new SolidColorBrush(Color.FromRgb(0x15, 0x65, 0xC0)), Child = dock };
            Content = _frame;

            // Answered later, not inside the click: the pin is pressed in the map's page, and the
            // page is still in the middle of that click while the window changes shape around it.
            Map.PinToggleRequested += () => Dispatcher.BeginInvoke(new Action(() =>
            {
                Cloak(true);
                try { SetPinned(!_pinned); }
                catch (Exception swallowed) { Cloak(false); Log.Swallow(swallowed); return; }
                ShowWhenRedrawn();
            }));

            LocationChanged += (s, e) => RememberFloating();
            SizeChanged += (s, e) => RememberFloating();
            Closing += MapWindow_Closing;

            // Pinned, it goes wherever the picture area goes. LayoutUpdated covers the area moving
            // inside the main window as well as the window itself changing size; it fires often, and
            // FollowMain does nothing unless the place has really changed.
            //
            // NOT RESIZED FROM INSIDE LayoutUpdated. Done there, the window took its new size but the
            // map's browser inside it kept the old one - the old Internet Explorer control is moved by
            // WPF from that same event, and a size changed in the middle of it never reached it. The
            // test showed a map 395 wide in a 515-wide window, for good. So it is only noted there,
            // and done a moment later, once the layout has finished.
            _main.LocationChanged += (s, e) => FollowMain();
            _main.StateChanged += (s, e) => FollowMain();
            _pinArea.LayoutUpdated += (s, e) => FollowMainSoon();

            if (WindowBounds.TryGetSaved(BoundsKey, out Rect saved)) _floatRect = saved;
            ApplyMode(Properties.Settings.Default.MapWindowPinned);
        }

        public bool IsPinned => _pinned;

        public void ShowWithOwner(Window owner)
        {
            if (Owner == null) Owner = owner;        // above the main window, and minimized with it

            // Pinned, it is put in its place BEFORE it appears, so it is never seen anywhere else.
            if (_pinned) { _pinnedRect = Rect.Empty; FollowMain(force: true); }
            if (!IsVisible)
            {
                // A floating map the operator maximized (double-click on its title bar, or dragged to
                // the top of the screen) and then closed is still Maximized, and WPF refuses to show a
                // maximized window without activating it: "Cannot show Window when ShowActivated is
                // false and WindowState is set to Maximized" - the map never came back (4Z1KD). It
                // comes back maximized, as he left it, taking the keyboard this one time.
                bool maximized = WindowState == WindowState.Maximized;
                if (maximized) ShowActivated = true;
                try { Show(); }
                finally { if (maximized) ShowActivated = false; }
            }
            if (!_pinned) WindowBounds.KeepOnScreen(this);
        }

        public void HideMap()
        {
            SaveFloating();
            Hide();
        }

        // Called by the main window as the program closes: from here on a close is a real one.
        public void AllowClose()
        {
            _allowClose = true;
            SaveFloating();
        }

        private void MapWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_allowClose) return;

            // The X only hides it. The map inside is an old Internet Explorer control that is slow to
            // start and must be disposed of by hand, so there is one for the whole run.
            // Not remembered: it is back at the next start unless Options says otherwise.
            e.Cancel = true;
            HideMap();
            ClosedByOperator?.Invoke();
        }

        private void SetPinned(bool pinned)
        {
            if (pinned == _pinned) return;
            if (!_pinned) RememberFloating();
            Properties.Settings.Default.MapWindowPinned = pinned;
            ApplyMode(pinned);

            // Written to disk after the map is back on screen: each save is a file write (measured 20 and
            // 50 ms), and the map is hidden until it is redrawn, so every millisecond spent before that is
            // time the operator sees nothing. The floating place was already noted above, before pinning.
            Rect floating = _floatRect;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (pinned && !floating.IsEmpty) WindowBounds.SaveRect(BoundsKey, floating);
                else { try { Properties.Settings.Default.Save(); } catch (Exception swallowed) { Log.Swallow(swallowed); } }
            }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        // ── OUT OF SIGHT WHILE IT CHANGES SHAPE ───────────────────────────────────────────────
        //
        // Pinned or unpinned, the window took its new size at once, but the map inside only heard of
        // it a moment later - so for that moment the old picture stood in the new frame, cut off in
        // the small box or small in the big window, and then jumped to fit. Now the window is hidden
        // for the change (cloaked: Windows stops showing it, but it keeps its size, its layout and
        // its painting), the map is redrawn for the new size, and only then is it shown again.
        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool RedrawWindow(IntPtr hWnd, IntPtr rect, IntPtr region, uint flags);
        private const int DWMWA_CLOAK = 13;
        private const uint RDW_INVALIDATE = 0x1, RDW_ERASE = 0x4, RDW_ALLCHILDREN = 0x80, RDW_UPDATENOW = 0x100, RDW_FRAME = 0x400;

        private System.Windows.Threading.DispatcherTimer _uncloakTimer;

        private void Cloak(bool hide)
        {
            try
            {
                IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;
                int value = hide ? 1 : 0;
                DwmSetWindowAttribute(hwnd, DWMWA_CLOAK, ref value, sizeof(int));
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }

            // NEVER LEFT HIDDEN. Whatever goes wrong on the way, the map is back on screen within two
            // seconds - a window that stays invisible is a map that has simply gone.
            _uncloakTimer?.Stop();
            if (hide)
            {
                _uncloakTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                _uncloakTimer.Tick += (s, e) => { _uncloakTimer.Stop(); Cloak(false); };
                _uncloakTimer.Start();
            }
        }

        // After the layout has put the browser at its new size, the map is redrawn for that size and
        // painted, and one frame later - for the title bar and frame drawn by WPF - the window is shown.
        // LOADED, NOT ContextIdle: Loaded runs straight after the layout pass, while ContextIdle waited
        // behind every timer in the queue as well (measured: up to 0.2 s of hidden window for nothing).
        private void ShowWhenRedrawn()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    Map.RedrawForNewSize();
                    var browser = Map.MapBrowser;
                    if (browser != null && browser.Handle != IntPtr.Zero)
                        RedrawWindow(browser.Handle, IntPtr.Zero, IntPtr.Zero,
                                     RDW_INVALIDATE | RDW_ERASE | RDW_ALLCHILDREN | RDW_UPDATENOW | RDW_FRAME);
                }
                catch (Exception swallowed) { Log.Swallow(swallowed); }

                var oneFrame = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(16) };
                oneFrame.Tick += (s, e) => { oneFrame.Stop(); Cloak(false); };
                oneFrame.Start();
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void ApplyMode(bool pinned)
        {
            _pinned = pinned;
            _placing = true;
            try
            {
                if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;

                System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome
                {
                    CaptionHeight = pinned ? 0 : TitleHeight,
                    CornerRadius = new CornerRadius(0),
                    GlassFrameThickness = new Thickness(0),
                    ResizeBorderThickness = new Thickness(pinned ? 0 : FloatingFrame),
                    UseAeroCaptionButtons = false
                });
                ResizeMode = pinned ? ResizeMode.NoResize : ResizeMode.CanResize;
                MinWidth = pinned ? 0 : FloatingMinWidth;
                MinHeight = pinned ? 0 : FloatingMinHeight;
                _titleBar.Visibility = pinned ? Visibility.Collapsed : Visibility.Visible;
                _frame.BorderThickness = new Thickness(pinned ? 0 : FloatingFrame);
                Map.ShowOwnFrame(pinned);
                Map.IsPinned = pinned;
            }
            finally { _placing = false; }

            if (pinned) { _pinnedRect = Rect.Empty; FollowMain(); }
            else PlaceFloating();
        }

        // UNPINNED, IT GOES BACK TO WHERE THE OPERATOR LAST HAD IT FLOATING, at that size. The first
        // time ever there is no such place, so it stays where it is and takes on its title bar and
        // frame around the same map.
        private void PlaceFloating()
        {
            _placing = true;
            try
            {
                Rect r = _floatRect;
                if (r.IsEmpty)
                {
                    if (!_pinnedRect.IsEmpty)
                        r = new Rect(_pinnedRect.Left, _pinnedRect.Top,
                                     _pinnedRect.Width + 2 * FloatingFrame,
                                     _pinnedRect.Height + TitleHeight + 2 * FloatingFrame);
                    else
                    {
                        // Never pinned and never floated: a first size, centred on the main window.
                        Width = 600;
                        Height = 450;
                        WindowStartupLocation = WindowStartupLocation.CenterOwner;
                        return;
                    }
                }
                Width = Math.Max(r.Width, FloatingMinWidth);
                Height = Math.Max(r.Height, FloatingMinHeight);
                Left = r.Left;
                Top = r.Top;
            }
            finally { _placing = false; }

            RememberFloating();
            if (IsVisible) WindowBounds.KeepOnScreen(this);
        }

        private bool _followQueued;

        private void FollowMainSoon()
        {
            if (!_pinned || _followQueued) return;
            _followQueued = true;
            Dispatcher.BeginInvoke(new Action(() => { _followQueued = false; FollowMain(); }),
                                   System.Windows.Threading.DispatcherPriority.Loaded);
        }

        // Lay the window over the picture area, in the units the window's own position is given in.
        private void FollowMain(bool force = false)
        {
            if (!_pinned || (!IsVisible && !force)) return;
            try
            {
                if (_main.WindowState == WindowState.Minimized) return;
                if (!_pinArea.IsVisible || _pinArea.ActualWidth < 1 || _pinArea.ActualHeight < 1) return;
                var source = PresentationSource.FromVisual(_main);
                if (source?.CompositionTarget == null) return;

                var toDips = source.CompositionTarget.TransformFromDevice;
                Point a = toDips.Transform(_pinArea.PointToScreen(new Point(0, 0)));
                Point b = toDips.Transform(_pinArea.PointToScreen(new Point(_pinArea.ActualWidth, _pinArea.ActualHeight)));
                var r = new Rect(a, b);
                if (r.Width < 1 || r.Height < 1 || r == _pinnedRect) return;

                _pinnedRect = r;
                _placing = true;
                try
                {
                    Left = r.Left;
                    Top = r.Top;
                    Width = r.Width;
                    Height = r.Height;
                }
                finally { _placing = false; }
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        private void RememberFloating()
        {
            if (_pinned || _placing || !IsVisible || WindowState != WindowState.Normal) return;
            if (double.IsNaN(Left) || double.IsNaN(Top) || ActualWidth <= 0 || ActualHeight <= 0) return;
            _floatRect = new Rect(Left, Top, ActualWidth, ActualHeight);
        }

        private void SaveFloating()
        {
            RememberFloating();
            if (!_floatRect.IsEmpty) WindowBounds.SaveRect(BoundsKey, _floatRect);
        }

        // Title and X, in the style of the Cluster and My Favorite Channels windows.
        private Border BuildTitleBar()
        {
            var closeBtn = new Button
            {
                Content = "",
                Style = Application.Current.Resources["CaptionCloseButtonStyle"] as Style,
                ToolTip = "Close the map (View > Map opens it again)"
            };
            System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(closeBtn, true);
            closeBtn.Click += (s, e) => Close();
            DockPanel.SetDock(closeBtn, Dock.Right);

            var title = new TextBlock
            {
                Text = "Map",
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0)
            };
            title.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");

            var row = new DockPanel { LastChildFill = true };
            row.Children.Add(closeBtn);
            row.Children.Add(title);

            var bar = new Border { Height = TitleHeight, Child = row };
            bar.SetResourceReference(Border.BackgroundProperty, "TitleBarBg");
            return bar;
        }
    }
}
