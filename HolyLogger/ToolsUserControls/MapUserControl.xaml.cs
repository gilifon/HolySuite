using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Media;


namespace HolyLogger.ToolsUserControls
{
    /// <summary>A cluster spot with coordinates and radio data for map display.</summary>
    public struct ClusterSpotInfo
    {
        public double Lat;
        public double Lon;
        public double? SpotterLat;
        public double? SpotterLon;
        public string Callsign;
        public string Freq;   // e.g. "14.025"
        public string Mode;   // e.g. "CW"
        public string Color;  // e.g. "#DC2828"
        public string Band;   // e.g. "40"
    }

    /// <summary>Exposes methods callable from JavaScript via window.external</summary>
    [ComVisible(true)]
    public class MapScriptHelper
    {
        private readonly MapUserControl _owner;
        public MapScriptHelper(MapUserControl owner) { _owner = owner; }

        public void SetRadius(string km)
        {
            if (int.TryParse(km, out int radiusKm))
                _owner.RaiseRadiusChanged(radiusKm);
        }

        public void ToggleProjection()
        {
            _owner.ToggleProjection();
        }

        public void SetAutoZoom(string active)
        {
            bool isActive = active == "1";
            Properties.Settings.Default.MapAutoZoom = isActive;
            Properties.Settings.Default.Save();
        }

        public void TuneToSpot(string freq, string mode)
        {
            _owner.RaiseSpotTuneRequested(freq, mode);
        }

        // Called from JS when the mouse enters/leaves a station's dot on the map, so the matching
        // cluster-list row can be highlighted.
        public void SpotHovered(string callsign)
        {
            _owner.RaiseSpotHovered(callsign);
        }

        public void SpotHoverEnd()
        {
            _owner.RaiseSpotHoverEnded();
        }
    }

    public partial class MapUserControl : UserControl
    {
        private readonly string _tempMapFile;
        private int _currentRadiusKm = 1000;
        private double _currentLat, _currentLon;
        private double? _currentAzimuth, _currentHomeLat, _currentHomeLon;
        private double? _currentSpotterLat, _currentSpotterLon;  // spotter of the selected cluster spot (for the DE button)
        private bool _isPolar;
        private bool _isClusterMode;
        private System.Collections.Generic.List<ClusterSpotInfo> _clusterSpots;
        private double _clusterHomeLat, _clusterHomeLon;
        private bool _clusterMapLoaded;
        // For the polar home/DX map: once it has loaded with a given home, later DX-only changes
        // (typing/clearing the DX callsign) are pushed via JS (updateDx) instead of a full reload,
        // so the embedded d3 isn't re-parsed and the countries aren't rebuilt (keeps F9 fast).
        private bool _homeMapLoaded;
        private double? _renderedHomeLat, _renderedHomeLon;

        public bool IsClusterMode => _isClusterMode;

        public event Action<int> RadiusChanged;
        public event Action<string, string> SpotTuneRequested;
        public event Action<string> SpotHovered;
        public event Action SpotHoverEnded;

        // The map's JavaScript reports radius changes (wheel/dropdown) here. Keep _currentRadiusKm
        // in sync so the next ShowClusterSpots does NOT see a radius change and trigger a full
        // re-render — that re-render would recenter the map on home and lose a view the user has
        // dragged/zoomed to. The JS already rescaled the view itself.
        internal void RaiseRadiusChanged(int km) { _currentRadiusKm = km; RadiusChanged?.Invoke(km); }
        internal void RaiseSpotTuneRequested(string freq, string mode) => SpotTuneRequested?.Invoke(freq, mode);
        internal void RaiseSpotHovered(string callsign) => SpotHovered?.Invoke(callsign);
        internal void RaiseSpotHoverEnded() => SpotHoverEnded?.Invoke();

        public bool IsPolarProjection => _isPolar;

        public void EnsureFlatProjection()
        {
            if (_isPolar)
            {
                ToggleProjection();
            }
        }

        internal void ToggleProjection()
        {
            _isPolar = !_isPolar;
            _clusterMapLoaded = false;
            _homeMapLoaded = false;
            Properties.Settings.Default.MapUsePolar = _isPolar;
            Properties.Settings.Default.Save();
            if (_isClusterMode)
                RenderClusterMap();
            else
                RenderMap();
        }

        public MapUserControl()
        {
            InitializeComponent();
            _isPolar = Properties.Settings.Default.MapUsePolar;
            _tempMapFile = Path.Combine(Path.GetTempPath(), "holylogger_map.html");
            MapBrowser.ObjectForScripting = new MapScriptHelper(this);
            // The IE WebBrowser control resets its "Silent" flag on every navigation, so a script
            // error that fires while the page is still loading would pop the native IE error dialog
            // before LoadCompleted runs. Re-suppress on Navigated (fires before body scripts run) so
            // the dialog never appears on any navigation, not just the first.
            MapBrowser.Navigated += (s, e) => SuppressScriptErrors();
            MapBrowser.LoadCompleted += (s, e) =>
            {
                SuppressScriptErrors();
                if (_isClusterMode)
                    _clusterMapLoaded = true;
                else if (_isPolar)
                    _homeMapLoaded = true;
            };
            this.SizeChanged += MapUserControl_SizeChanged;
        }

        private void MapUserControl_SizeChanged(object sender, System.Windows.SizeChangedEventArgs e)
        {
            // Trigger map redraw after resize with a small delay to ensure browser layout is updated
            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            timer.Tick += (s, args) =>
            {
                timer.Stop();
                try
                {
                    // Force the browser to recalculate and trigger resize by dispatching event
                    // and then explicitly forcing a layout recalculation
                    MapBrowser.InvokeScript("eval", new object[] {
                        @"
                        (function() {
                            // The legacy WebBrowser engine may not support the Event() constructor,
                            // which throws 'object doesn't support this property or method'. Build the
                            // resize event in a way that works on both modern and legacy engines.
                            function fireResize() {
                                try {
                                    if (typeof Event === 'function') {
                                        window.dispatchEvent(new Event('resize'));
                                    } else if (document.createEvent) {
                                        var ev = document.createEvent('Event');
                                        ev.initEvent('resize', true, true);
                                        window.dispatchEvent(ev);
                                    } else if (window.fireEvent) {
                                        window.fireEvent('onresize');
                                    }
                                } catch (e) {}
                            }
                            fireResize();
                            if (typeof svg !== 'undefined') {
                                setTimeout(fireResize, 50);
                            }
                        })();
                        "
                    });
                }
                catch { }
            };
            timer.Start();
        }

        // Updates only spot markers on the already-loaded map via JS — no page reload
        private void UpdateClusterSpotsJs(System.Collections.Generic.List<ClusterSpotInfo> spots)
        {
            if (!_clusterMapLoaded)
                return;
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            var sb = new System.Text.StringBuilder("[");
            bool isPolar = _isPolar;
            for (int i = 0; i < spots.Count; i++)
            {
                if (i > 0) sb.Append(",");
                var s = spots[i];
                string callsign = (s.Callsign ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
                string freq = (s.Freq ?? string.Empty).Replace("\"", "\\\"");
                string mode = (s.Mode ?? string.Empty).Replace("\"", "\\\"");
                string color = (s.Color ?? "#FF6600").Replace("\"", "\\\"");
                string band = (s.Band ?? string.Empty).Replace("\"", "\\\"");
                string spStr = (s.SpotterLat.HasValue && s.SpotterLon.HasValue)
                    ? (isPolar
                        ? "[" + s.SpotterLon.Value.ToString(ic) + "," + s.SpotterLat.Value.ToString(ic) + "]"
                        : "[" + s.SpotterLat.Value.ToString(ic) + "," + s.SpotterLon.Value.ToString(ic) + "]")
                    : "null";
                if (isPolar)
                    sb.AppendFormat(ic, "{{\"c\":[{0},{1}],\"sp\":{2},\"cs\":\"{3}\",\"f\":\"{4}\",\"m\":\"{5}\",\"k\":\"{6}\",\"b\":\"{7}\"}}",
                        s.Lon, s.Lat, spStr, callsign, freq, mode, color, band);
                else
                    sb.AppendFormat(ic, "{{\"c\":[{0},{1}],\"sp\":{2},\"cs\":\"{3}\",\"f\":\"{4}\",\"m\":\"{5}\",\"k\":\"{6}\",\"b\":\"{7}\"}}",
                        s.Lat, s.Lon, spStr, callsign, freq, mode, color, band);
            }
            sb.Append("]");
            try
            {
                MapBrowser.InvokeScript("updateClusterSpots", new object[] { sb.ToString() });
            }
            catch { }
        }

        // Enlarges the map dot(s) for the given DX callsign — called while the user hovers a row in
        // the cluster list — so they can see where in the world that station is. No-op unless a
        // cluster map is currently loaded.
        public void HighlightSpot(string callsign)
        {
            if (!_isClusterMode || !_clusterMapLoaded || string.IsNullOrEmpty(callsign)) return;
            try { MapBrowser.InvokeScript("highlightSpot", new object[] { callsign, "" }); }
            catch { }
        }

        // Restores all spot dots to their normal size (called when the hover leaves the row/grid).
        public void ClearSpotHighlight()
        {
            if (!_isClusterMode || !_clusterMapLoaded) return;
            try { MapBrowser.InvokeScript("clearSpotHighlight", new object[] { }); }
            catch { }
        }

        public void ShowClusterSpots(System.Collections.Generic.IList<ClusterSpotInfo> spots, double homeLat, double homeLon, int radiusKm)
        {
            // Switching to cluster mode navigates independently of RenderMap; invalidate its dedupe
            // cache so a later identical home/DX render is never skipped over a cluster view.
            _lastRenderedHtml = null;
            var newSpots = new System.Collections.Generic.List<ClusterSpotInfo>(spots);

            bool homeChanged = !_isClusterMode
                || Math.Abs(_clusterHomeLat - homeLat) > 0.0001
                || Math.Abs(_clusterHomeLon - homeLon) > 0.0001;
            bool radiusChanged = _currentRadiusKm != radiusKm;

            _isClusterMode = true;
            _clusterSpots = newSpots;
            _clusterHomeLat = homeLat;
            _clusterHomeLon = homeLon;
            _currentRadiusKm = radiusKm;
            PlaceholderPanel.Visibility = System.Windows.Visibility.Collapsed;
            MapBrowser.Visibility = System.Windows.Visibility.Visible;

            if (_clusterMapLoaded && !homeChanged && !radiusChanged)
            {
                // Map is already loaded — just swap spot markers via JS, no reload
                UpdateClusterSpotsJs(newSpots);
            }
            else
            {
                _clusterMapLoaded = false;
                RenderClusterMap();
            }
        }

        private void RenderClusterMap()
        {
            var spots = _clusterSpots ?? new System.Collections.Generic.List<ClusterSpotInfo>();
            double homeLat = _clusterHomeLat;
            double homeLon = _clusterHomeLon;
            int radiusKm = _currentRadiusKm;

            if (_isPolar)
            {
                RenderClusterPolarMap(spots, homeLat, homeLon, radiusKm);
                return;
            }

            double marginMultiplier = 1.15;
            try
            {
                marginMultiplier = Properties.Settings.Default.MapAutoFitMargin;
                if (marginMultiplier < 1.0 || marginMultiplier > 2.0) marginMultiplier = 1.15;
            }
            catch { marginMultiplier = 1.15; }

            var ic = System.Globalization.CultureInfo.InvariantCulture;
            string homeLatJs = homeLat.ToString(ic);
            string homeLonJs = homeLon.ToString(ic);
            string marginJs = marginMultiplier.ToString(ic);
            int radiusMeters = radiusKm * 1000;
            bool useMiles = string.Equals(Properties.Settings.Default.MapDistanceUnit, "Miles", System.StringComparison.OrdinalIgnoreCase);
            string useMilesJs = useMiles ? "true" : "false";

            int[] radiiOptions = { 100, 250, 500, 1000, 2000, 3500, 5000, 7500, 10000, 15000, 20000 };
            var options = new System.Text.StringBuilder();
            foreach (int r in radiiOptions)
            {
                string label = useMiles
                    ? Math.Round(r * 0.621371).ToString(ic) + " mi"
                    : r.ToString(ic) + " km";
                options.AppendFormat("<option value='{0}'{1}>{2}</option>", r, r == radiusKm ? " selected" : "", label);
            }

            var spotsJs = new System.Text.StringBuilder("[");
            for (int i = 0; i < spots.Count; i++)
            {
                if (i > 0) spotsJs.Append(",");
                var s = spots[i];
                string callsign = (s.Callsign ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
                string freq = (s.Freq ?? string.Empty).Replace("\"", "\\\"");
                string mode = (s.Mode ?? string.Empty).Replace("\"", "\\\"");
                string color = (s.Color ?? "#FF6600").Replace("\"", "\\\"");
                string band = (s.Band ?? string.Empty).Replace("\"", "\\\"");
                string spStr = (s.SpotterLat.HasValue && s.SpotterLon.HasValue)
                    ? "[" + s.SpotterLat.Value.ToString(ic) + "," + s.SpotterLon.Value.ToString(ic) + "]"
                    : "null";
                spotsJs.AppendFormat(ic, "{{\"c\":[{0},{1}],\"sp\":{2},\"cs\":\"{3}\",\"f\":\"{4}\",\"m\":\"{5}\",\"k\":\"{6}\",\"b\":\"{7}\"}}",
                    s.Lat, s.Lon, spStr, callsign, freq, mode, color, band);
            }
            spotsJs.Append("]");

            string html =
@"<!DOCTYPE html>
<html>
<head>
<meta http-equiv='X-UA-Compatible' content='IE=edge'/>
<meta charset='utf-8'/>
<style>
  * { margin:0; padding:0; box-sizing:border-box; }
  html, body { width:100%; height:100%; margin:0; padding:0; overflow:hidden; }
  #map { width:100%; height:100%; }
  #compass-ctrl {
    position:absolute; top:0; left:0; z-index:1000;
    display:flex; flex-direction:column; align-items:center;
    background:transparent; border:none; padding:2px 2px 1px 2px;
    border-radius:0; font-family:Segoe UI, Tahoma, sans-serif; box-shadow:none;
  }
  #compass-ring {
    width:74px; height:74px; border:2px solid #25464a; border-radius:50%;
    position:relative; background:radial-gradient(circle, rgba(255,255,255,0.98) 0%, rgba(220,228,236,0.95) 70%, rgba(200,212,224,0.95) 100%);
    overflow:hidden;
  }
  #compass-svg { width:100%; height:100%; display:block; }
  #compass-needle { transform-origin:50px 50px; }
  #compass-text {
    margin-top:2px; font-size:13px; font-weight:700; color:#18393c;
    letter-spacing:0.3px; background:rgba(255,255,255,0.88);
    border:1px solid rgba(36,77,80,0.45); border-radius:10px; padding:2px 7px;
  }
  #bottom-ctrl {
    position:absolute; bottom:0; left:0; z-index:1000; display:flex; align-items:flex-end;
  }
  #radius-stack { display:flex; flex-direction:column; align-items:center; }
  #radius-ctrl {
    background:rgba(255,255,255,0.88); border:1px solid #aaa; border-radius:0;
    padding:2px 4px; font-size:13px; font-family:sans-serif; cursor:pointer; height:100%;
  }
  #center-btn {
    background:#9FCBF5; border:1px solid #4B76A0; border-radius:10px; padding:0 6px; cursor:pointer;
    display:flex; align-items:center; justify-content:center;
    color:#333; height:24px; margin-bottom:2px;
    font-family:sans-serif; font-size:11px; font-weight:700;
  }
  #center-btn:hover { background:#8CBDF0; }
  #center-btn svg { width:16px; height:16px; }
  #center-btn .de-label { margin-right:4px; }
  #dx-center-btn {
    background:#9FCBF5; border:1px solid #4B76A0; border-radius:10px; padding:0 6px; cursor:pointer;
    display:flex; align-items:center; justify-content:center;
    color:#333; height:24px; margin-bottom:2px;
    font-family:sans-serif; font-size:11px; font-weight:700;
  }
  #dx-center-btn:hover { background:#8CBDF0; }
  #dx-center-btn svg { width:16px; height:16px; }
  #dx-center-btn .dx-label { margin-right:4px; }
  #proj-btn {
    position:absolute; top:0; right:0; z-index:1000;
    background:#9FCBF5; border:1px solid #4B76A0; border-radius:10px; padding:0 6px; cursor:pointer;
    height:24px; display:flex; align-items:center; justify-content:center;
    font-size:11px; font-weight:700; font-family:sans-serif; color:#333;
  }
  #proj-btn:hover { background:#8CBDF0; }
  #distance-stack {
    position:absolute; right:0; bottom:0; z-index:1000; display:flex; flex-direction:column; align-items:center;
  }
  #distance-box {
    background:rgba(255,255,255,0.9); border:1px solid #aaa; border-radius:0; padding:3px 7px;
    font-size:13px; font-weight:700; font-family:sans-serif; color:#333; white-space:nowrap;
  }
</style>
<link rel='stylesheet' href='https://unpkg.com/leaflet@1.9.4/dist/leaflet.css'/>
</head>
<body>
<div id='map'></div>
<button id='proj-btn' onclick='toggleProjection()' title='Switch to polar azimuthal map'>&#127757; Polar</button>
<div id='compass-ctrl'>
  <div id='compass-ring'>
    <svg id='compass-svg' viewBox='0 0 100 100' aria-hidden='true'>
      <circle cx='50' cy='50' r='48' fill='#8dd6d4' stroke='#2b4f50' stroke-width='3'/>
      <circle cx='50' cy='50' r='39' fill='#efe6a0' stroke='#b9b37e' stroke-width='2'/>
      <g opacity='0.4' fill='#7f7858'>
        <polygon points='50,17 57,50 50,83 43,50'/>
        <polygon points='17,50 50,57 83,50 50,43'/>
        <polygon points='26,26 53,47 74,74 47,53'/>
        <polygon points='74,26 53,53 26,74 47,47'/>
      </g>
      <g fill='#134346' font-size='11' font-weight='700' text-anchor='middle' font-family='Segoe UI, Tahoma, sans-serif'>
        <text x='50' y='16'>N</text><text x='86' y='54'>E</text>
        <text x='50' y='91'>S</text><text x='14' y='54'>W</text>
      </g>
      <g id='compass-needle'>
        <polygon points='50,12 58,50 42,50' fill='#d10f20' stroke='#7b0d18' stroke-width='1.2'/>
        <polygon points='50,88 58,50 42,50' fill='#197a74' stroke='#0f4e4a' stroke-width='1.2'/>
      </g>
      <circle cx='50' cy='50' r='4.5' fill='#e9e4b9' stroke='#6f6a4d' stroke-width='1.2'/>
    </svg>
  </div>
  <div id='compass-text'>AZ --</div>
</div>
<div id='bottom-ctrl'>
  <div id='radius-stack'>
    <button id='center-btn' onclick='recenter()' title='Re-center map'>
      <span class='de-label'>DE</span>
      <svg viewBox='0 0 24 24' fill='none' stroke='currentColor' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'>
        <circle cx='12' cy='12' r='3'/><line x1='12' y1='2' x2='12' y2='6'/>
        <line x1='12' y1='18' x2='12' y2='22'/><line x1='2' y1='12' x2='6' y2='12'/>
        <line x1='18' y1='12' x2='22' y2='12'/>
      </svg>
    </button>
    <select id='radius-ctrl' onchange='onRadiusChange(this.value)'>" + options.ToString() + @"</select>
  </div>
</div>
<div id='distance-stack'>
  <button id='dx-center-btn' onclick='centerOnDx()' title='Center on home station'><span class='dx-label'>DX</span><svg viewBox='0 0 24 24' fill='none' stroke='currentColor' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><circle cx='12' cy='12' r='3'/><line x1='12' y1='2' x2='12' y2='6'/><line x1='12' y1='18' x2='12' y2='22'/><line x1='2' y1='12' x2='6' y2='12'/><line x1='18' y1='12' x2='22' y2='12'/></svg></button>
  <div id='distance-box'>DIST --</div>
</div>
<script src='https://unpkg.com/leaflet@1.9.4/dist/leaflet.js'></script>
<script>
window.onerror = function() { return true; };
var homeLat = " + homeLatJs + @", homeLon = " + homeLonJs + @";
var radiusMeters = " + radiusMeters.ToString(ic) + @";
var useMiles = " + useMilesJs + @";
var showDayNight = " + (Properties.Settings.Default.MapShowDayNight ? "true" : "false") + @";
var clusterSpots = " + spotsJs.ToString() + @";
var map = L.map('map', { zoomControl:false, attributionControl:false, zoomSnap:0 }).setView([homeLat, homeLon], 4);
L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', { maxZoom:18 }).addTo(map);
// Equator
L.polyline([[0,-180],[0,-90],[0,0],[0,90],[0,180]], { color:'#000000', weight:1.2, opacity:0.5, interactive:false }).addTo(map);

// Day/Night overlay
var dayNightLayer = null;
var sunMarker = null;

function getSolarPosition() {
    var now = new Date();
    var year = now.getUTCFullYear();
    var month = now.getUTCMonth() + 1;
    var day = now.getUTCDate();
    var hour = now.getUTCHours();
    var minute = now.getUTCMinutes();
    var second = now.getUTCSeconds();
    var a = Math.floor((14 - month) / 12);
    var y = year + 4800 - a;
    var m = month + 12 * a - 3;
    var jdn = day + Math.floor((153 * m + 2) / 5) + 365 * y + Math.floor(y / 4) - Math.floor(y / 100) + Math.floor(y / 400) - 32045;
    var jd = jdn + (hour - 12) / 24 + minute / 1440 + second / 86400;
    var n = jd - 2451545.0;
    var L = (280.460 + 0.9856474 * n) % 360;
    if (L < 0) L += 360;
    var g = (357.528 + 0.9856003 * n) % 360;
    if (g < 0) g += 360;
    var gRad = g * Math.PI / 180;
    var lambda = (L + 1.915 * Math.sin(gRad) + 0.020 * Math.sin(2 * gRad)) % 360;
    if (lambda < 0) lambda += 360;
    var lambdaRad = lambda * Math.PI / 180;
    var epsilon = 23.439 - 0.0000004 * n;
    var epsilonRad = epsilon * Math.PI / 180;
    var dec = Math.asin(Math.sin(epsilonRad) * Math.sin(lambdaRad)) * 180 / Math.PI;
    var ra = Math.atan2(Math.cos(epsilonRad) * Math.sin(lambdaRad), Math.cos(lambdaRad)) * 180 / Math.PI;
    if (ra < 0) ra += 360;
    var t = (jd - 2451545.0) / 36525.0;
    var gmst0 = (6.697374558 + 2400.051336 * t + 0.000025862 * t * t) % 24;
    if (gmst0 < 0) gmst0 += 24;
    var utHours = hour + minute / 60 + second / 3600;
    var gmst = (gmst0 + utHours * 1.00273790935) % 24;
    if (gmst < 0) gmst += 24;
    var gha = (gmst * 15 - ra) % 360;
    if (gha > 180) gha -= 360;
    if (gha < -180) gha += 360;
    var sunLon = -gha;
    if (sunLon > 180) sunLon -= 360;
    if (sunLon < -180) sunLon += 360;
    return { lat: dec, lon: sunLon };
}

function ensureDayNightCanvas() {
    if (dayNightLayer) return dayNightLayer;
    var pane = map.getPanes().tilePane;
    dayNightLayer = L.DomUtil.create('canvas', '', pane);
    dayNightLayer.style.position = 'absolute';
    dayNightLayer.style.pointerEvents = 'none';
    dayNightLayer.style.zIndex = '250';
    return dayNightLayer;
}

function drawDayNight() {
    if (sunMarker) {
        map.removeLayer(sunMarker);
        sunMarker = null;
    }

    if (!showDayNight) {
        if (dayNightLayer && dayNightLayer.parentNode) {
            dayNightLayer.parentNode.removeChild(dayNightLayer);
            dayNightLayer = null;
        }
        return;
    }

    var canvas = ensureDayNightCanvas();
    var size = map.getSize();
    canvas.width = size.x;
    canvas.height = size.y;
    canvas.style.width = size.x + 'px';
    canvas.style.height = size.y + 'px';
    var topLeft = map.containerPointToLayerPoint([0, 0]);
    L.DomUtil.setPosition(canvas, topLeft);

    var ctx = canvas.getContext('2d');
    ctx.clearRect(0, 0, canvas.width, canvas.height);

    try {
        var sunPos = getSolarPosition();
        var sunLatRad = sunPos.lat * Math.PI / 180;
        var sunLonRad = sunPos.lon * Math.PI / 180;
        var sinSunLat = Math.sin(sunLatRad);
        var cosSunLat = Math.cos(sunLatRad);

        var width = size.x;
        var height = size.y;
        var twilightBand = 0.02;
        var maxAlpha = 0.25;
        var image = ctx.createImageData(width, height);
        var data = image.data;

        for (var y = 0; y < height; y++) {
            for (var x = 0; x < width; x++) {
                var ll = map.containerPointToLatLng([x + 0.5, y + 0.5]);
                var latRad = ll.lat * Math.PI / 180;
                var lonRad = ll.lng * Math.PI / 180;
                var cosZenith = Math.sin(latRad) * sinSunLat + Math.cos(latRad) * cosSunLat * Math.cos(lonRad - sunLonRad);
                if (cosZenith >= twilightBand) continue;

                var alpha = maxAlpha;
                if (cosZenith > -twilightBand) {
                    alpha = maxAlpha * ((twilightBand - cosZenith) / (2 * twilightBand));
                }

                var idx = (y * width + x) * 4;
                data[idx] = 0;
                data[idx + 1] = 0;
                data[idx + 2] = 30;
                data[idx + 3] = Math.round(alpha * 255);
            }
        }

        ctx.putImageData(image, 0, 0);

        var sunIcon = L.divIcon({
            className: '',
            html: '<div style=""width:10px;height:10px;position:relative""><div style=""position:absolute;top:50%;left:50%;transform:translate(-50%,-50%);width:7px;height:7px;background:#FFD700;border:1px solid #FFA500;border-radius:50%;box-shadow:0 0 6px rgba(255,220,0,0.5)""></div></div>',
            iconSize: [10, 10],
            iconAnchor: [5, 5]
        });
        sunMarker = L.marker([sunPos.lat, sunPos.lon], { icon: sunIcon, interactive: false }).addTo(map);
    } catch(e) {}
}

drawDayNight();
setInterval(drawDayNight, 60000);

var homeIcon = L.divIcon({ className:'', html:'<div style=""width:10px;height:10px;background:#1565C0;border:2px solid #fff;border-radius:50%;box-shadow:0 0 2px rgba(0,0,0,0.5)""></div>', iconAnchor:[5,5] });
L.marker([homeLat, homeLon], { icon:homeIcon }).addTo(map);
var radiusCircle = L.circle([homeLat, homeLon], { radius:radiusMeters, color:'#E53935', fill:false, weight:2 }).addTo(map);
var spotIcon = L.divIcon({ className:'', html:'<div style=""width:8px;height:8px;background:#FF6600;border-radius:50%;box-shadow:0 0 2px rgba(0,0,0,0.5)""></div>', iconAnchor:[4,4] });
var spotsLayer = L.layerGroup().addTo(map);
function gcArcPoints(lat1, lon1, lat2, lon2, n) {
    var toRad = Math.PI/180, toDeg = 180/Math.PI;
    var la1=lat1*toRad, lo1=lon1*toRad, la2=lat2*toRad, lo2=lon2*toRad;
    var d = 2*Math.asin(Math.sqrt(Math.pow(Math.sin((la2-la1)/2),2)+Math.cos(la1)*Math.cos(la2)*Math.pow(Math.sin((lo2-lo1)/2),2)));
    if (d < 0.0001) return [[lat1,lon1],[lat2,lon2]];
    var pts = [];
    for (var i=0; i<=n; i++) {
        var f=i/n;
        var A=Math.sin((1-f)*d)/Math.sin(d), B=Math.sin(f*d)/Math.sin(d);
        var x=A*Math.cos(la1)*Math.cos(lo1)+B*Math.cos(la2)*Math.cos(lo2);
        var y=A*Math.cos(la1)*Math.sin(lo1)+B*Math.cos(la2)*Math.sin(lo2);
        var z=A*Math.sin(la1)+B*Math.sin(la2);
        pts.push([Math.atan2(z,Math.sqrt(x*x+y*y))*toDeg, Math.atan2(y,x)*toDeg]);
    }
    return pts;
}
var hlCs = null, hlF = null;  // cluster-list hover highlight (callsign + freq)
function renderSpots() {
    spotsLayer.clearLayers();
    for (var i = 0; i < clusterSpots.length; i++) {
        var sp = clusterSpots[i];
        // Great circle line spotter -> DX
        if (sp.sp) {
            var arcPts = gcArcPoints(sp.sp[0], sp.sp[1], sp.c[0], sp.c[1], 50);
            var arcColor = (sp.b === '40' || sp.b === '40m' || (parseFloat(sp.f) >= 7.0 && parseFloat(sp.f) <= 7.3)) ? '#FFFFFF' : (sp.k || '#FF6600');
            L.polyline(arcPts, {
                color: arcColor, weight: 0.8, opacity: 0.7, interactive: false
            }).addTo(spotsLayer);
        }
        // Spotter dot (black)
        if (sp.sp) {
            L.circleMarker(sp.sp, {
                radius: 2, color: '#000000', fillColor: '#000000', fillOpacity: 1,
                weight: 0, interactive: false
            }).addTo(spotsLayer);
        }
        // Band-colored DX dot with tooltip and click. When this spot's row is hovered in the
        // cluster list, draw an enlarged dot with a white ring so it stands out on the map.
        var dotColor = sp.k || '#FF6600';
        var isHl = (hlCs !== null && sp.cs === hlCs);
        var m = L.circleMarker(sp.c, {
            radius: isHl ? 12 : 5, color: isHl ? '#FFFFFF' : dotColor,
            fillColor: dotColor, fillOpacity: 1,
            weight: isHl ? 2 : 0, interactive: true
        });
        m.bindTooltip('<b>' + sp.cs + '</b><br/>' + sp.f + '<span style=""font-size:9px;font-weight:normal""> MHz</span>&nbsp;' + sp.m, {
            permanent: false, sticky: true, direction: 'top',
            className: 'spot-tip'
        });
        (function(freq, mode, cs) {
            m.on('click', function() {
                try { window.external.TuneToSpot(freq, mode); } catch(e) {}
                try { window.external.SpotHoverEnd(); } catch(e) {}
            });
            m.on('mouseover', function() { try { window.external.SpotHovered(cs); } catch(e) {} });
            m.on('mouseout', function() { try { window.external.SpotHoverEnd(); } catch(e) {} });
        })(sp.f, sp.m, sp.cs);
        m.addTo(spotsLayer);
    }
}
renderSpots();
map.fitBounds(radiusCircle.getBounds(), { padding:[2,2] });
function updateClusterSpots(json) {
    try { clusterSpots = JSON.parse(json); } catch(e) { return; }
    renderSpots();
    drawDayNight();
}
function highlightSpot(cs, f) { hlCs = cs; hlF = f; renderSpots(); }
function clearSpotHighlight() { hlCs = null; hlF = null; renderSpots(); }
function onRadiusChange(km) {
    radiusMeters = km * 1000;
    radiusCircle.setRadius(radiusMeters);
    map.fitBounds(radiusCircle.getBounds(), { padding:[2,2] });
    drawDayNight();
    try { window.external.SetRadius(km); } catch(e) {}
}
function recenter() { 
    map.fitBounds(radiusCircle.getBounds(), { padding:[2,2] }); 
    drawDayNight();
}
function centerOnDx() { map.setView([homeLat, homeLon], map.getZoom()); }
function toggleProjection() { try { window.external.ToggleProjection(); } catch(e) {} }
map.on('move zoom resize', drawDayNight);
window.addEventListener('resize', function() { if (map) { map.invalidateSize(); drawDayNight(); } });
</script>
</body>
</html>";

            File.WriteAllText(_tempMapFile, html, System.Text.Encoding.UTF8);
            var uriBuilder = new UriBuilder(new Uri(_tempMapFile));
            uriBuilder.Query = "v=" + DateTime.UtcNow.Ticks.ToString();
            MapBrowser.Navigate(uriBuilder.Uri);
        }

        private void RenderClusterPolarMap(System.Collections.Generic.List<ClusterSpotInfo> spots, double homeLat, double homeLon, int radiusKm)
        {
            // Check if we should show compass instead of map
            bool showCompass = Properties.Settings.Default.MapAreaDisplayMode == 1;

            var ic = System.Globalization.CultureInfo.InvariantCulture;
            double marginMultiplier = 1.15;
            try
            {
                marginMultiplier = Properties.Settings.Default.MapAutoFitMargin;
                if (marginMultiplier < 1.0 || marginMultiplier > 2.0) marginMultiplier = 1.15;
            }
            catch { marginMultiplier = 1.15; }

            bool useMiles = string.Equals(Properties.Settings.Default.MapDistanceUnit, "Miles", System.StringComparison.OrdinalIgnoreCase);
            int[] radiiOptions = { 100, 250, 500, 1000, 2000, 3500, 5000, 7500, 10000, 15000, 20000 };
            var options = new System.Text.StringBuilder();
            foreach (int r in radiiOptions)
            {
                string label = useMiles
                    ? Math.Round(r * 0.621371).ToString(ic) + " mi"
                    : r.ToString(ic) + " km";
                options.AppendFormat("<option value='{0}'{1}>{2}</option>", r, r == radiusKm ? " selected" : "", label);
            }

            var spotsJs = new System.Text.StringBuilder("[");
            for (int i = 0; i < spots.Count; i++)
            {
                if (i > 0) spotsJs.Append(",");
                var s = spots[i];
                string callsign = (s.Callsign ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
                string freq = (s.Freq ?? string.Empty).Replace("\"", "\\\"");
                string mode = (s.Mode ?? string.Empty).Replace("\"", "\\\"");
                string color = (s.Color ?? "#FF6600").Replace("\"", "\\\"");
                string band = (s.Band ?? string.Empty).Replace("\"", "\\\"");
                // polar projection expects [lon, lat]
                string spStr = (s.SpotterLat.HasValue && s.SpotterLon.HasValue)
                    ? "[" + s.SpotterLon.Value.ToString(ic) + "," + s.SpotterLat.Value.ToString(ic) + "]"
                    : "null";
                spotsJs.AppendFormat(ic, "{{\"c\":[{0},{1}],\"sp\":{2},\"cs\":\"{3}\",\"f\":\"{4}\",\"m\":\"{5}\",\"k\":\"{6}\",\"b\":\"{7}\"}}",
                    s.Lon, s.Lat, spStr, callsign, freq, mode, color, band);
            }
            spotsJs.Append("]");

            string homeLatJs = homeLat.ToString(ic);
            string homeLonJs = homeLon.ToString(ic);
            string marginJs = marginMultiplier.ToString(ic);
            string useMilesJs = useMiles ? "true" : "false";

            string html =
@"<!DOCTYPE html>
<html>
<head>
<meta http-equiv='X-UA-Compatible' content='IE=edge'/>
<meta charset='utf-8'/>
<style>
  * { margin:0; padding:0; box-sizing:border-box; }
  html, body { width:100%; height:100%; overflow:hidden; background:#000000; }
  #polar-svg { position:absolute; top:0; left:0; width:100%; height:100%; }
  #proj-btn {
    position:absolute; top:0; right:0; z-index:1000;
    background:#9FCBF5; border:1px solid #4B76A0; border-radius:10px; padding:0 6px; cursor:pointer;
    height:24px; display:flex; align-items:center; justify-content:center;
    font-size:11px; font-weight:700; font-family:sans-serif; color:#333;
  }
  #proj-btn:hover { background:#8CBDF0; }
  #az-only {
    position:absolute; top:30px; right:4px; z-index:1000;
    background:rgba(255,255,255,0.88); border:1px solid rgba(36,77,80,0.45);
    border-radius:10px; padding:2px 7px; font-size:13px; font-weight:700;
    color:#18393c; font-family:sans-serif;
  }
  #bottom-ctrl {
    position:absolute; bottom:0; left:0; z-index:1000; display:flex; align-items:flex-end;
  }
  #radius-stack { display:flex; flex-direction:column; align-items:flex-start; }
  #radius-ctrl {
    background:rgba(255,255,255,0.88); border:1px solid #aaa;
    padding:2px 4px; font-size:13px; font-family:sans-serif; cursor:pointer;
  }
  #radius-label {
    display:none;
    background:rgba(255,255,255,0.88); border:1px solid #aaa;
    padding:2px 6px; font-size:13px; font-weight:700; font-family:sans-serif;
    color:#1a9e55; white-space:nowrap; text-align:center;
  }
  #center-btn, #home-btn, #de-btn {
    background:#9FCBF5; border:1px solid #4B76A0; border-radius:10px; padding:0 6px; cursor:pointer;
    display:flex; align-items:center; justify-content:center;
    color:#333; height:24px; margin-bottom:2px;
    font-family:sans-serif; font-size:11px; font-weight:700;
  }
  #center-btn:hover, #home-btn:hover, #de-btn:hover { background:#8CBDF0; }
  #center-btn svg, #home-btn svg, #de-btn svg { width:16px; height:16px; }
  #center-btn .de-label, #de-btn .de-label { margin-right:4px; }
  #dx-center-btn {
    background:#9FCBF5; border:1px solid #4B76A0; border-radius:10px; padding:0 6px; cursor:pointer;
    display:flex; align-items:center; justify-content:center;
    color:#333; height:24px; margin-bottom:2px;
    font-family:sans-serif; font-size:11px; font-weight:700;
  }
  #dx-center-btn:hover { background:#8CBDF0; }
  #dx-center-btn svg { width:16px; height:16px; }
  #dx-center-btn .dx-label { margin-right:4px; }
  #distance-stack {
    position:absolute; right:0; bottom:0; z-index:1000;
    display:flex; flex-direction:column; align-items:flex-end;
  }
  #distance-box {
    background:rgba(255,255,255,0.9); border:1px solid #aaa;
    padding:3px 7px; font-size:13px; font-weight:700;
    font-family:sans-serif; color:#333; white-space:nowrap;
  }
  #autozoom-wrap {
    position:absolute; top:4px; left:6px; z-index:1000;
    display:flex; flex-direction:column; align-items:center;
    cursor:pointer; user-select:none;
  }
  #autozoom-toggle {
    width:34px; height:18px; border-radius:9px;
    background:#888; border:2px solid #666;
    position:relative; transition:background 0.2s, border-color 0.2s;
    box-shadow:inset 0 1px 3px rgba(0,0,0,0.4);
  }
  #autozoom-toggle .knob {
    position:absolute; top:1px; left:1px;
    width:12px; height:12px; border-radius:50%;
    background:#fff;
    box-shadow:0 1px 3px rgba(0,0,0,0.4);
    transition:left 0.2s;
  }
  #autozoom-wrap.active #autozoom-toggle {
    background:#2ecc71; border-color:#1a9e55;
  }
  #autozoom-wrap.active #autozoom-toggle .knob {
    left:17px;
  }
  #autozoom-label {
    font-size:10px; font-weight:700; color:rgba(255,255,255,0.9);
    font-family:sans-serif; margin-top:2px; text-shadow:0 1px 2px rgba(0,0,0,0.8);
    letter-spacing:0.3px;
  }
</style>
</head>
<body>
<svg id='polar-svg'></svg>
<button id='proj-btn' onclick='toggleProjection()'>&#9974; Flat</button>
<div id='autozoom-wrap' onclick='toggleAutoZoom()' title='Auto Zoom: fit all spots in view'>
  <div id='autozoom-toggle'><div class='knob'></div></div>
  <div id='autozoom-label'>Auto Zoom</div>
</div>
<div id='az-only'>AZ --</div>
<div id='bottom-ctrl'>
  <div id='radius-stack'>
    <button id='home-btn' title='Center on home' style='display:none'></button>
    <button id='de-btn' title='Center on home'></button>
    <select id='radius-ctrl' onchange='onRadiusChange(this.value)'>" + options.ToString() + @"</select>
    <div id='radius-label'>-- km</div>
  </div>
</div>
<div id='distance-stack'>
  <button id='dx-center-btn' title='Center on DX' style='display:none'></button>
  <div id='distance-box'>DIST --</div>
</div>
" + MapAssetProvider.D3ScriptTag + @"
" + MapAssetProvider.CountryDataScriptTag + @"
<script>
window.onerror = function() { return true; };
var centerLat = " + homeLatJs + @";
var centerLon = " + homeLonJs + @";
var radiusKm = " + radiusKm.ToString() + @";
var marginMultiplier = " + marginJs + @";
var useMiles = " + useMilesJs + @";
var showDayNight = " + (Properties.Settings.Default.MapShowDayNight ? "true" : "false") + @";
var clusterSpots = " + spotsJs.ToString() + @"; // [[lon,lat],...]
var autoZoomInitActive = " + (Properties.Settings.Default.MapAutoZoom ? "true" : "false") + @";
var EARTH_KM = 6371;

var W = window.innerWidth, H = window.innerHeight;
var mapR = Math.floor((Math.min(W, H) / 2) - 4);
var cx = W / 2, cy = H / 2;
var svg = d3.select('#polar-svg').attr('width', W).attr('height', H);
var defs = svg.append('defs');
defs.append('clipPath').attr('id', 'globe-clip')
    .append('circle').attr('cx', cx).attr('cy', cy).attr('r', mapR - 1);
var projection = d3.geoAzimuthalEquidistant()
    .rotate([-centerLon, -centerLat])
    .scale(mapR / Math.PI)
    .translate([cx, cy])
    .clipAngle(180);
var path = d3.geoPath().projection(projection);
var baseScale = mapR / Math.PI;
var viewCenterLat = centerLat, viewCenterLon = centerLon;
scaleToRadius();
// Restore saved auto zoom state
var autoZoomActive = false;
var autoZoomSavedRadiusKm = -1;
if (autoZoomInitActive) {
    autoZoomActive = true;
    autoZoomSavedRadiusKm = radiusKm;
    var azBtn = document.getElementById('autozoom-wrap');
    if (azBtn) azBtn.classList.add('active');
    setRadiusControlVisibility(true);
}

function scaleToRadius() {
    var ang = radiusKm / EARTH_KM;
    if (!isFinite(ang) || ang <= 0) { projection.scale(baseScale); return; }
    var targetPx = mapR - 4;
    projection.scale(targetPx / ang);
}
function applyViewCenter() { projection.rotate([-viewCenterLon, -viewCenterLat]); }

var oceanFill = svg.append('circle').attr('class', 'ocean-fill').attr('cx', cx).attr('cy', cy).attr('r', mapR)
    .attr('fill', '#b8e8ee').attr('stroke', '#000000').attr('stroke-width', 1.5);
var countriesG = svg.append('g').attr('clip-path', 'url(#globe-clip)');
svg.append('path').datum(d3.geoGraticule().step([30, 30])())
    .attr('class', 'graticule-path')
    .attr('fill', 'none').attr('stroke', '#6bb7c4').attr('stroke-width', 0.7)
    .attr('d', path).attr('clip-path', 'url(#globe-clip)');
// Equator
svg.append('path').datum({type:'LineString', coordinates:[[-180,0],[-90,0],[0,0],[90,0],[180,0]]})
    .attr('class', 'graticule-path')
    .attr('fill','none').attr('stroke','rgba(0,0,0,0.55)').attr('stroke-width',1.2)
    .attr('d',path).attr('clip-path','url(#globe-clip)');
var ringsG = svg.append('g').attr('clip-path', 'url(#globe-clip)');
function drawRings() {
    ringsG.selectAll('*').remove();
    for (var i = 1; i <= 5; i++) {
        var km = Math.round((radiusKm * i) / 5);
        var ang = km / EARTH_KM;
        if (ang >= Math.PI) continue;
        var r = projection.scale() * ang;
        ringsG.append('circle').attr('cx', cx).attr('cy', cy).attr('r', r)
            .attr('fill', 'none').attr('stroke', 'rgba(0,0,0,0.2)')
            .attr('stroke-width', 1).attr('stroke-dasharray', '4,3');
        var lbl = useMiles ? (Math.round(km * 0.621371) + ' mi') : (km + ' km');
        ringsG.append('text').attr('x', cx + 3).attr('y', cy - r - 2)
            .attr('fill', 'rgba(0,0,0,0.45)').attr('font-size', '9px').text(lbl);
    }
}
drawRings();
function drawRadiusRing(km) {
    svg.selectAll('.radius-ring').remove();
    var ang = km / EARTH_KM;
    if (ang < Math.PI) {
        var r = projection.scale() * ang;
        svg.append('circle').attr('class', 'radius-ring').attr('cx', cx).attr('cy', cy).attr('r', r)
            .attr('fill', 'none').attr('stroke', '#E53935').attr('stroke-width', 2);
    }
}
drawRadiusRing(radiusKm);

// Solar position and day/night overlay
var dayNightG = svg.append('g').attr('clip-path', 'url(#globe-clip)');

function getSolarPosition() {
    var now = new Date();
    var year = now.getUTCFullYear();
    var month = now.getUTCMonth() + 1;
    var day = now.getUTCDate();
    var hour = now.getUTCHours();
    var minute = now.getUTCMinutes();
    var second = now.getUTCSeconds();
    var a = Math.floor((14 - month) / 12);
    var y = year + 4800 - a;
    var m = month + 12 * a - 3;
    var jdn = day + Math.floor((153 * m + 2) / 5) + 365 * y + Math.floor(y / 4) - Math.floor(y / 100) + Math.floor(y / 400) - 32045;
    var jd = jdn + (hour - 12) / 24 + minute / 1440 + second / 86400;
    var n = jd - 2451545.0;
    var L = (280.460 + 0.9856474 * n) % 360;
    if (L < 0) L += 360;
    var g = (357.528 + 0.9856003 * n) % 360;
    if (g < 0) g += 360;
    var gRad = g * Math.PI / 180;
    var lambda = (L + 1.915 * Math.sin(gRad) + 0.020 * Math.sin(2 * gRad)) % 360;
    if (lambda < 0) lambda += 360;
    var lambdaRad = lambda * Math.PI / 180;
    var epsilon = 23.439 - 0.0000004 * n;
    var epsilonRad = epsilon * Math.PI / 180;
    var dec = Math.asin(Math.sin(epsilonRad) * Math.sin(lambdaRad)) * 180 / Math.PI;
    var ra = Math.atan2(Math.cos(epsilonRad) * Math.sin(lambdaRad), Math.cos(lambdaRad)) * 180 / Math.PI;
    if (ra < 0) ra += 360;
    var t = (jd - 2451545.0) / 36525.0;
    var gmst0 = (6.697374558 + 2400.051336 * t + 0.000025862 * t * t) % 24;
    if (gmst0 < 0) gmst0 += 24;
    var utHours = hour + minute / 60 + second / 3600;
    var gmst = (gmst0 + utHours * 1.00273790935) % 24;
    if (gmst < 0) gmst += 24;
    var gha = (gmst * 15 - ra) % 360;
    if (gha > 180) gha -= 360;
    if (gha < -180) gha += 360;
    var sunLon = -gha;
    if (sunLon > 180) sunLon -= 360;
    if (sunLon < -180) sunLon += 360;
    return { lat: dec, lon: sunLon };
}

function drawDayNight() {
    dayNightG.selectAll('*').remove();
    if (!showDayNight) {
        if (typeof overlaysG !== 'undefined' && overlaysG) drawOverlays();
        return;
    }
    try {
        var sunPos = getSolarPosition();
        var nightCoords = [];
        for (var lat = -90; lat <= 90; lat += 1) {
            var latRad = lat * Math.PI / 180;
            var sunLatRad = sunPos.lat * Math.PI / 180;
            var cosH = -Math.tan(latRad) * Math.tan(sunLatRad);
            if (cosH >= -1 && cosH <= 1) {
                var hourAngle = Math.acos(cosH) * 180 / Math.PI;
                var lon = sunPos.lon - hourAngle;
                if (lon > 180) lon -= 360;
                if (lon < -180) lon += 360;
                nightCoords.push([lon, lat]);
            } else if (cosH > 1) {
                nightCoords.push([sunPos.lon, lat]);
            } else {
                var lon = sunPos.lon + 180;
                if (lon > 180) lon -= 360;
                nightCoords.push([lon, lat]);
            }
        }
        var antiSunLon = sunPos.lon + 180;
        if (antiSunLon > 180) antiSunLon -= 360;
        nightCoords.push([antiSunLon, 90]);
        nightCoords.push([antiSunLon, -90]);
        for (var lat = 90; lat >= -90; lat -= 1) {
            var latRad = lat * Math.PI / 180;
            var sunLatRad = sunPos.lat * Math.PI / 180;
            var cosH = -Math.tan(latRad) * Math.tan(sunLatRad);
            if (cosH >= -1 && cosH <= 1) {
                var hourAngle = Math.acos(cosH) * 180 / Math.PI;
                var lon = sunPos.lon + hourAngle;
                if (lon > 180) lon -= 360;
                if (lon < -180) lon += 360;
                nightCoords.push([lon, lat]);
            } else if (cosH > 1) {
                nightCoords.push([sunPos.lon, lat]);
            } else {
                var lon = sunPos.lon + 180;
                if (lon > 180) lon -= 360;
                nightCoords.push([lon, lat]);
            }
        }
        nightCoords.push(nightCoords[0]);
        nightCoords.reverse();
        dayNightG.append('path')
            .datum({type: 'Polygon', coordinates: [nightCoords]})
            .attr('d', path)
            .attr('fill', 'rgba(0,0,170,0.3)')
            .attr('stroke', 'none');
        var sunPt = projection([sunPos.lon, sunPos.lat]);
        if (sunPt && isFinite(sunPt[0]) && isFinite(sunPt[1])) {
            dayNightG.append('circle')
                .attr('cx', sunPt[0])
                .attr('cy', sunPt[1])
                .attr('r', 6)
                .attr('fill', 'rgba(255,220,0,0.3)')
                .attr('stroke', 'none');
            dayNightG.append('circle')
                .attr('cx', sunPt[0])
                .attr('cy', sunPt[1])
                .attr('r', 3.5)
                .attr('fill', '#FFD700')
                .attr('stroke', '#FFA500')
                .attr('stroke-width', 1);
            for (var a = 0; a < 360; a += 45) {
                var rad = a * Math.PI / 180;
                var x1 = sunPt[0] + Math.cos(rad) * 5;
                var y1 = sunPt[1] + Math.sin(rad) * 5;
                var x2 = sunPt[0] + Math.cos(rad) * 7;
                var y2 = sunPt[1] + Math.sin(rad) * 7;
                dayNightG.append('line')
                    .attr('x1', x1).attr('y1', y1)
                    .attr('x2', x2).attr('y2', y2)
                    .attr('stroke', '#FFA500')
                    .attr('stroke-width', 1);
            }
        }
    } catch(e) {}
    if (typeof overlaysG !== 'undefined' && overlaysG) drawOverlays();
}

drawDayNight();
setInterval(drawDayNight, 60000);

var overlaysG = svg.append('g').attr('clip-path', 'url(#globe-clip)');
var tooltip = d3.select('body').append('div')
    .style('position','absolute').style('pointer-events','none')
    .style('background','rgba(255,255,255,0.95)').style('border','1px solid #aaa')
    .style('border-radius','4px').style('padding','3px 7px')
    .style('font-size','12px').style('font-family','sans-serif')
    .style('color','#222').style('display','none').style('z-index','9999');
var hlCs = null, hlF = null;  // cluster-list hover highlight (callsign + freq)
function drawOverlays() {
    overlaysG.selectAll('*').remove();
    // Home dot
    try {
        var homePt = projection([centerLon, centerLat]);
        if (homePt && isFinite(homePt[0]) && isFinite(homePt[1])) {
            overlaysG.append('circle').attr('cx', homePt[0]).attr('cy', homePt[1]).attr('r', 5)
                .attr('fill', '#1565C0').attr('stroke', 'none');
        }
    } catch(e) {}
    // Draw in three layers so DX dots always sit above every arc/spotter dot: arcs, then spotter
    // dots, then the interactive DX dots. Each element is tagged with its DX callsign (data-cs)
    // so applyHighlight() can restyle the hovered spot's arc + dots in place (no full re-render).
    var arcsG = overlaysG.append('g');
    var spottersG = overlaysG.append('g');
    var dxG = overlaysG.append('g');
    for (var i = 0; i < clusterSpots.length; i++) {
        try {
            var sp = clusterSpots[i];
            var spt = sp.sp ? projection(sp.sp) : null;
            var pt = projection(sp.c);
            // Great circle line spotter -> DX (D3 geoPath draws it curved automatically)
            if (sp.sp) {
                try {
                    var gcLine = { type: 'LineString', coordinates: [sp.sp, sp.c] };
                    var arcColor = (sp.b === '40' || sp.b === '40m' || (parseFloat(sp.f) >= 7.0 && parseFloat(sp.f) <= 7.3)) ? '#FFFFFF' : (sp.k || '#FF6600');
                    arcsG.append('path')
                        .datum(gcLine)
                        .attr('class', 'spot-arc').attr('data-cs', sp.cs)
                        .attr('d', path)
                        .attr('fill', 'none')
                        .attr('stroke', arcColor).attr('stroke-width', 0.8).attr('opacity', 0.6)
                        .attr('clip-path', 'url(#globe-clip)');
                } catch(el) {}
            }
            // Spotter dot (black)
            if (spt && isFinite(spt[0]) && isFinite(spt[1])) {
                spottersG.append('circle')
                    .attr('class', 'spot-spotter').attr('data-cs', sp.cs)
                    .attr('cx', spt[0]).attr('cy', spt[1]).attr('r', 2)
                    .attr('fill', '#000000').attr('stroke', 'none')
                    .attr('clip-path', 'url(#globe-clip)');
            }
            // Band-colored DX dot with tooltip and click
            if (pt && isFinite(pt[0]) && isFinite(pt[1])) {
                (function(spot, px, py) {
                    var dotColor = spot.k || '#FF6600';
                    dxG.append('circle')
                        .attr('class', 'spot-dx').attr('data-cs', spot.cs)
                        .attr('cx', px).attr('cy', py).attr('r', 4)
                        .attr('fill', dotColor).attr('stroke', 'none').attr('stroke-width', 0)
                        .attr('clip-path', 'url(#globe-clip)')
                        .style('cursor', 'pointer')
                        .on('mouseover', function() {
                            tooltip.style('display','block')
                                .html('<b>' + spot.cs + '</b><br/>' + spot.f + '<span style=""font-size:9px;font-weight:normal""> MHz</span>&nbsp;' + spot.m);
                            applyHighlight(spot.cs);   // light up this spot's arc + dots on the map
                            try { window.external.SpotHovered(spot.cs); } catch(e3) {}
                        })
                        .on('mousemove', function() {
                            tooltip.style('left', (d3.event.pageX + 10) + 'px')
                                   .style('top',  (d3.event.pageY - 28) + 'px');
                        })
                        .on('mouseout', function() { tooltip.style('display','none'); applyHighlight(null); try { window.external.SpotHoverEnd(); } catch(e4) {} })
                        .on('click', function() {
                            selectSpot(spot);
                            try { window.external.TuneToSpot(spot.f, spot.m); } catch(e2) {}
                            try { window.external.SpotHoverEnd(); } catch(e5) {}
                        });
                })(sp, pt[0], pt[1]);
            }
        } catch(es) {}
    }
    // Outer border
    overlaysG.append('circle').attr('cx', cx).attr('cy', cy).attr('r', mapR)
        .attr('fill', 'none').attr('stroke', '#2a607a').attr('stroke-width', 2);
    applyHighlight(hlCs);   // re-apply any active hover highlight after this redraw
}
// Restyle the hovered spot's arc + spotter dot + DX dot in place (matched by DX callsign) so the
// change never removes/re-adds nodes -- which would otherwise retrigger the map's own hover
// events and flicker. Only the non-interactive arc/spotter are raised (never the DX dot).
function applyHighlight(cs) {
    hlCs = cs;
    if (!overlaysG) return;
    overlaysG.selectAll('.spot-arc').each(function() {
        var el = d3.select(this); var on = (cs !== null && el.attr('data-cs') === cs);
        el.attr('stroke-width', on ? 2.5 : 0.8).attr('opacity', on ? 1 : 0.6);
        if (on) el.raise();
    });
    overlaysG.selectAll('.spot-spotter').each(function() {
        var el = d3.select(this); var on = (cs !== null && el.attr('data-cs') === cs);
        el.attr('r', on ? 4 : 2).attr('stroke', on ? '#FFFFFF' : 'none').attr('stroke-width', on ? 1 : 0);
        if (on) el.raise();
    });
    overlaysG.selectAll('.spot-dx').each(function() {
        var el = d3.select(this); var on = (cs !== null && el.attr('data-cs') === cs);
        el.attr('r', on ? 9 : 4).attr('stroke', on ? '#FFFFFF' : 'none').attr('stroke-width', on ? 2 : 0);
    });
}
drawOverlays();
drawDayNight();
if (autoZoomActive) applyAutoZoom();

// Offline colored countries from embedded window.DXCC_DATA (see non-cluster map for details).
try {
    var dxccFeatures = window.DXCC_DATA.features.map(function(f) {
        return { type: 'Feature', properties: { ci: f.ci, p: f.p }, geometry: f.geometry };
    });
    var dxccPalette = window.DXCC_DATA.palette;
    countriesG.selectAll('path').data(dxccFeatures).enter().append('path')
        .attr('d', path)
        .attr('fill', function(d) { return dxccPalette[d.properties.ci]; })
        .attr('stroke', '#777777').attr('stroke-width', 0.4);
} catch(e) {}
drawOverlays();
drawDayNight();
drawRadiusRing(radiusKm);
if (autoZoomActive) applyAutoZoom();

function onRadiusChange(km) {
    radiusKm = parseInt(km, 10);
    scaleToRadius();
    countriesG.selectAll('path').attr('d', path);
    svg.selectAll('.graticule-path').attr('d', path);
    drawRings(); drawRadiusRing(radiusKm); drawOverlays();
    try { window.external.SetRadius(km); } catch(e) {}
}
function recenter() {
    viewCenterLat = centerLat; viewCenterLon = centerLon;
    applyViewCenter(); scaleToRadius();
    countriesG.selectAll('path').attr('d', path);
    svg.selectAll('.graticule-path').attr('d', path);
    drawRings(); drawRadiusRing(radiusKm); drawOverlays(); drawDayNight();
}

// ---- Center buttons (Home / DE-spotter / DX) -------------------------------------------
// Default (no spot picked): the bottom-left button is a Home icon -> center on home.
// After the user clicks a spot dot: that button turns into 'DE' -> center on the spotter,
// a Home icon button appears above it, and the bottom-right 'DX' button -> center on the DX.
var CROSSHAIR_SVG = ""<svg viewBox='0 0 24 24' fill='none' stroke='currentColor' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><circle cx='12' cy='12' r='3'/><line x1='12' y1='2' x2='12' y2='6'/><line x1='12' y1='18' x2='12' y2='22'/><line x1='2' y1='12' x2='6' y2='12'/><line x1='18' y1='12' x2='22' y2='12'/></svg>"";
var HOME_SVG = ""<svg viewBox='0 0 24 24' fill='none' stroke='currentColor' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><path d='M3 10.5L12 3l9 7.5'/><path d='M5 9.5V21h5v-6h4v6h5V9.5'/></svg>"";

var selActive = false, selSpotter = null, selDx = null;  // selected-spot centering state

function panTo(lon, lat) {
    if (!isFinite(lon) || !isFinite(lat)) return;
    viewCenterLon = lon; viewCenterLat = lat;
    applyViewCenter();
    countriesG.selectAll('path').attr('d', path);
    svg.selectAll('.graticule-path').attr('d', path);
    drawRings(); drawRadiusRing(radiusKm); drawOverlays(); drawDayNight();
}
function centerOnHome()    { panTo(centerLon, centerLat); }
function centerOnSpotter() { if (selSpotter) panTo(selSpotter[0], selSpotter[1]); }
function centerOnDx()      { if (selDx) panTo(selDx[0], selDx[1]); }

function selectSpot(spot) {
    selActive = true;
    selSpotter = spot.sp ? [spot.sp[0], spot.sp[1]] : null;
    selDx = spot.c ? [spot.c[0], spot.c[1]] : null;
    updateCenterButtons();
}

function updateCenterButtons() {
    var homeBtn = document.getElementById('home-btn');
    var deBtn = document.getElementById('de-btn');
    var dxBtn = document.getElementById('dx-center-btn');
    if (!deBtn) return;
    if (!selActive) {
        // No spot picked: the main button is just a Home icon.
        if (homeBtn) homeBtn.style.display = 'none';
        deBtn.innerHTML = HOME_SVG;
        deBtn.onclick = centerOnHome;
        deBtn.title = 'Center on home';
        if (dxBtn) dxBtn.style.display = 'none';
    } else {
        // Spot picked: main button -> DE (spotter); Home pops up above; DX appears on the right.
        if (homeBtn) {
            homeBtn.style.display = '';
            homeBtn.innerHTML = HOME_SVG;
            homeBtn.onclick = centerOnHome;
            homeBtn.title = 'Center on home';
        }
        deBtn.innerHTML = ""<span class='de-label'>DE</span>"" + CROSSHAIR_SVG;
        deBtn.onclick = (selSpotter ? centerOnSpotter : centerOnHome);
        deBtn.title = selSpotter ? 'Center on spotter' : 'Spotter location unknown';
        if (dxBtn) {
            dxBtn.style.display = '';
            dxBtn.innerHTML = ""<span class='dx-label'>DX</span>"" + CROSSHAIR_SVG;
            dxBtn.onclick = centerOnDx;
            dxBtn.title = 'Center on DX';
        }
    }
}
updateCenterButtons();

function toggleProjection() { try { window.external.ToggleProjection(); } catch(e) {} }
function updateClusterSpots(json) {
    try { clusterSpots = JSON.parse(json); } catch(e) { return; }
    drawOverlays(); drawDayNight();
    if (autoZoomActive) applyAutoZoom();
}
function highlightSpot(cs, f) { hlF = f; applyHighlight(cs); }
function clearSpotHighlight() { hlF = null; applyHighlight(null); }
function haversineKm(lat1, lon1, lat2, lon2) {
    var R = 6371, toR = Math.PI/180;
    var dLat = (lat2-lat1)*toR, dLon = (lon2-lon1)*toR;
    var a = Math.sin(dLat/2)*Math.sin(dLat/2) +
            Math.cos(lat1*toR)*Math.cos(lat2*toR)*Math.sin(dLon/2)*Math.sin(dLon/2);
    return R * 2 * Math.atan2(Math.sqrt(a), Math.sqrt(1-a));
}
function applyAutoZoom() {
    if (!clusterSpots || clusterSpots.length === 0) return;
    // Helper: validate a [lon,lat] pair and push its distance from the CURRENT view center.
    // Using the view center (not home) means that when the user drags the map in auto-zoom
    // mode, the radius re-fits so every spotted station stays inside the circle. At first
    // render viewCenter == home, so the initial fit is unchanged.
    function pushDist(arr, distances) {
        if (!arr || arr.length !== 2) return;
        var lon = arr[0], lat = arr[1];
        if (!isFinite(lon) || !isFinite(lat)) return;
        if (lon < -180 || lon > 180 || lat < -90 || lat > 90) return;
        var km = haversineKm(viewCenterLat, viewCenterLon, lat, lon);
        if (isFinite(km) && km > 0) distances.push(km);
    }
    // Helper: sample N intermediate great-circle points between two [lon,lat] coords
    // and push their distances.  Arc midpoints can bow farther from home than the
    // endpoints on an azimuthal equidistant projection — this is why arcs were
    // escaping the circle even when both endpoints were inside.
    function pushArcDists(a, b, n, distances) {
        if (!a || !b) return;
        var toRad = Math.PI/180, toDeg = 180/Math.PI;
        var la1=a[1]*toRad, lo1=a[0]*toRad, la2=b[1]*toRad, lo2=b[0]*toRad;
        var d = 2*Math.asin(Math.sqrt(
            Math.pow(Math.sin((la2-la1)/2),2) +
            Math.cos(la1)*Math.cos(la2)*Math.pow(Math.sin((lo2-lo1)/2),2)));
        if (d < 0.0001) return;
        for (var k=1; k<n; k++) {
            var f=k/n;
            var A=Math.sin((1-f)*d)/Math.sin(d), B=Math.sin(f*d)/Math.sin(d);
            var x=A*Math.cos(la1)*Math.cos(lo1)+B*Math.cos(la2)*Math.cos(lo2);
            var y=A*Math.cos(la1)*Math.sin(lo1)+B*Math.cos(la2)*Math.sin(lo2);
            var z=A*Math.sin(la1)+B*Math.sin(la2);
            var ptLat=Math.atan2(z,Math.sqrt(x*x+y*y))*toDeg;
            var ptLon=Math.atan2(y,x)*toDeg;
            var km=haversineKm(centerLat, centerLon, ptLat, ptLon);
            if (isFinite(km) && km > 0) distances.push(km);
        }
    }
    var distances = [];
    for (var i = 0; i < clusterSpots.length; i++) {
        var sp = clusterSpots[i];
        pushDist(sp.c,  distances);   // DX endpoint
        pushDist(sp.sp, distances);   // Spotter endpoint
        // FIX: Do NOT include arc intermediate points in auto-zoom calculation.
        // The arc is just a visual connector; only the actual station positions matter.
        // Previously this was sampling arc points which made the zoom radius too large:
        // if (sp.sp && sp.c) pushArcDists(sp.sp, sp.c, 10, distances);
    }
    if (distances.length === 0) return;
    // Radius = farthest station (DX or spotter) + 10% padding.
    var maxKm = 0;
    for (var j = 0; j < distances.length; j++) { if (distances[j] > maxKm) maxKm = distances[j]; }
    if (maxKm < 100) maxKm = 100;
    var newKm = Math.ceil(maxKm * 1.10);
    // Update label
    var lbl = document.getElementById('radius-label');
    if (lbl) lbl.textContent = useMiles ? (Math.round(newKm * 0.621371) + ' mi') : (newKm + ' km');
    if (newKm !== radiusKm) {
        radiusKm = newKm;
        document.getElementById('radius-ctrl').value = newKm;
        scaleToRadius();
        countriesG.selectAll('path').attr('d', path);
        svg.selectAll('.graticule-path').attr('d', path);
        drawRings(); drawRadiusRing(radiusKm); drawOverlays(); drawDayNight();
        // Do NOT notify C# — auto zoom radius is visual only, not persisted
    }
}
function setRadiusControlVisibility(azActive) {
    var sel = document.getElementById('radius-ctrl');
    var lbl = document.getElementById('radius-label');
    if (sel) sel.style.display = azActive ? 'none' : '';
    if (lbl) lbl.style.display = azActive ? 'block' : 'none';
}
function restoreRadius(km) {
    radiusKm = km;
    document.getElementById('radius-ctrl').value = km;
    scaleToRadius();
    countriesG.selectAll('path').attr('d', path);
    svg.selectAll('.graticule-path').attr('d', path);
    drawRings(); drawRadiusRing(radiusKm); drawOverlays(); drawDayNight();
    try { window.external.SetRadius(km); } catch(e) {}  // restore persisted radius
}
function toggleAutoZoom() {
    autoZoomActive = !autoZoomActive;
    var btn = document.getElementById('autozoom-wrap');
    if (autoZoomActive) {
        autoZoomSavedRadiusKm = radiusKm;  // save current radius
        btn.classList.add('active');
        setRadiusControlVisibility(true);
        applyAutoZoom();
    } else {
        btn.classList.remove('active');
        setRadiusControlVisibility(false);
        if (autoZoomSavedRadiusKm > 0) restoreRadius(autoZoomSavedRadiusKm);
    }
    try { window.external.SetAutoZoom(autoZoomActive ? '1' : '0'); } catch(e) {}
}
svg.call(d3.drag()
    .on('start', function() {})
    .on('drag', function() {
        var dx = d3.event.dx, dy = d3.event.dy;
        var scale = projection.scale();
        var dLon = -(dx / scale) * (180 / Math.PI);
        var dLat = (dy / scale) * (180 / Math.PI);
        var r = projection.rotate();
        var newLon = r[0] - dLon, newLat = -r[1] + dLat;
        if (newLat > 85) newLat = 85; if (newLat < -85) newLat = -85;
        viewCenterLat = newLat; viewCenterLon = -newLon;
        projection.rotate([newLon, -newLat]);
        countriesG.selectAll('path').attr('d', path);
        svg.selectAll('.graticule-path').attr('d', path);
        drawRings(); drawRadiusRing(radiusKm); drawOverlays(); drawDayNight();
        // In auto-zoom, re-fit the radius around the new view center so dragging can never
        // push a spotted station outside the circle.
        if (autoZoomActive) applyAutoZoom();
    })
);

// Mouse-wheel zoom (like HolyCluster): wheel up = zoom in (smaller radius), wheel down =
// zoom out. Steps through the preset radius levels so the radius control stays in sync.
// The first wheel notch while Auto Zoom is on drops out to manual mode at the nearest level.
function wheelZoom(zoomIn) {
    var sel = document.getElementById('radius-ctrl');
    if (!sel || sel.options.length === 0) return;
    if (autoZoomActive) {
        autoZoomActive = false;
        var azb = document.getElementById('autozoom-wrap');
        if (azb) azb.classList.remove('active');
        setRadiusControlVisibility(false);
        try { window.external.SetAutoZoom('0'); } catch(e) {}
        var best = 0, bestDiff = Infinity;
        for (var i = 0; i < sel.options.length; i++) {
            var diff = Math.abs(parseInt(sel.options[i].value, 10) - radiusKm);
            if (diff < bestDiff) { bestDiff = diff; best = i; }
        }
        sel.selectedIndex = best;
        onRadiusChange(sel.value);
        return;
    }
    // Fixed-step zoom (independent of the preset dropdown list): 500 km per notch, or 250 km
    // when below 2000 km for finer control near home. Using '<=' when zooming in and '<' when
    // zooming out keeps 2000 km a clean grid point in both directions.
    var step = zoomIn ? (radiusKm <= 2000 ? 250 : 500) : (radiusKm < 2000 ? 250 : 500);
    var newKm = radiusKm + (zoomIn ? -step : step);
    // Clamp to the preset radius range (ignore any temporary wheel option already present).
    var minKm = Infinity, maxKm = -Infinity;
    for (var i = 0; i < sel.options.length; i++) {
        if (sel.options[i].getAttribute('data-wheel')) continue;
        var v = parseInt(sel.options[i].value, 10);
        if (v < minKm) minKm = v;
        if (v > maxKm) maxKm = v;
    }
    if (newKm < minKm) newKm = minKm;
    if (newKm > maxKm) newKm = maxKm;
    if (newKm === radiusKm) return;
    applyWheelRadius(newKm);
}
// Apply an arbitrary (off-grid) wheel radius. The dropdown only carries preset values, so when
// the stepped value isn't a preset we show it via a single reusable 'custom' option inserted in
// ascending order; if it matches a preset we select that instead and drop the custom option.
function applyWheelRadius(km) {
    var sel = document.getElementById('radius-ctrl');
    if (sel) {
        // Remove any previous wheel-added option so only one custom entry ever exists.
        for (var k = sel.options.length - 1; k >= 0; k--) {
            if (sel.options[k].getAttribute('data-wheel')) sel.remove(k);
        }
        var matched = false;
        for (var i = 0; i < sel.options.length; i++) {
            if (parseInt(sel.options[i].value, 10) === km) { sel.selectedIndex = i; matched = true; break; }
        }
        if (!matched) {
            var opt = document.createElement('option');
            opt.value = km;
            opt.text = useMiles ? (Math.round(km * 0.621371) + ' mi') : (km + ' km');
            opt.setAttribute('data-wheel', '1');
            var before = null;
            for (var j = 0; j < sel.options.length; j++) {
                if (parseInt(sel.options[j].value, 10) > km) { before = sel.options[j]; break; }
            }
            sel.add(opt, before);
            sel.value = km;
        }
    }
    onRadiusChange(km);
}
function onWheelEvent(e) {
    e = e || window.event;
    var delta = 0;
    if (e.wheelDelta !== undefined && e.wheelDelta !== 0) delta = e.wheelDelta;   // IE/legacy
    else if (e.deltaY !== undefined) delta = -e.deltaY;                            // standard
    else if (e.detail) delta = -e.detail;
    if (delta !== 0) wheelZoom(delta > 0);
    if (e.preventDefault) e.preventDefault();
    e.returnValue = false;
    return false;
}
// The host is the IE WebBrowser, which fires 'mousewheel'; fall back to 'wheel' otherwise.
// Attach a single event name to avoid double-stepping per notch.
var wheelName = ('onmousewheel' in document) ? 'mousewheel' : 'wheel';
if (document.addEventListener) document.addEventListener(wheelName, onWheelEvent, false);
else if (document.attachEvent) document.attachEvent('on' + wheelName, onWheelEvent);

window.addEventListener('resize', function() {
    W = window.innerWidth; H = window.innerHeight;
    mapR = Math.floor((Math.min(W, H) / 2) - 4);
    cx = W / 2; cy = H / 2;
    svg.attr('width', W).attr('height', H);
    oceanFill.attr('cx', cx).attr('cy', cy).attr('r', mapR);
    defs.select('clipPath circle').attr('cx', cx).attr('cy', cy).attr('r', mapR - 1);
    projection.translate([cx, cy]);
    baseScale = mapR / Math.PI;
    scaleToRadius();
    countriesG.selectAll('path').attr('d', path);
    svg.selectAll('.graticule-path').attr('d', path);
    drawRings(); drawRadiusRing(radiusKm); drawOverlays(); drawDayNight();
});
</script>
</body>
</html>";

            File.WriteAllText(_tempMapFile, html, System.Text.Encoding.UTF8);
            var uriBuilder = new UriBuilder(new Uri(_tempMapFile));
            uriBuilder.Query = "v=" + DateTime.UtcNow.Ticks.ToString();
            MapBrowser.Navigate(uriBuilder.Uri);
        }

        public void ShowMap(double lat, double lon, int radiusKm, double? azimuthDeg = null, double? homeLat = null, double? homeLon = null, double? spotterLat = null, double? spotterLon = null)
        {
            _isClusterMode = false;
            _currentLat = lat;
            _currentLon = lon;
            _currentRadiusKm = radiusKm;
            _currentAzimuth = azimuthDeg;
            _currentHomeLat = homeLat;
            _currentHomeLon = homeLon;
            _currentSpotterLat = spotterLat;
            _currentSpotterLon = spotterLon;
            PlaceholderPanel.Visibility = System.Windows.Visibility.Collapsed;
            MapBrowser.Visibility = System.Windows.Visibility.Visible;
            RenderMap();
        }

          public void RefreshMap()
          {
            if (MapBrowser.Visibility == System.Windows.Visibility.Visible)
            {
              if (_isClusterMode)
                RenderClusterMap();
              else
                RenderMap();
            }
          }

        // Tracks the last HTML navigated to via RenderMap so we can skip redundant reloads (e.g. the
        // two SetAzimuth calls per lookup that resolve to the same spot). Navigation itself stays
        // synchronous so the render order is preserved (e.g. ClearAzimuth renders home then cluster).
        private string _lastRenderedHtml;

        private void RenderMap()
        {
            double marginMultiplier = 1.15; // default
            try
            {
                marginMultiplier = Properties.Settings.Default.MapAutoFitMargin;
                if (marginMultiplier < 1.0 || marginMultiplier > 2.0)
                    marginMultiplier = 1.15;
            }
            catch
            {
                marginMultiplier = 1.15;
            }

            // FAST PATH: polar home/DX map already loaded and the home (map center) is unchanged.
            // Only the DX/spotter/zoom differ, so push them via JS (no reload, no d3 re-parse, no
            // country rebuild). This is what makes clearing the DX callsign (F9) react quickly.
            double centerLatNow = _currentHomeLat ?? _currentLat;
            double centerLonNow = _currentHomeLon ?? _currentLon;
            if (_isPolar && !_isClusterMode && _homeMapLoaded
                && _renderedHomeLat.HasValue && _renderedHomeLon.HasValue
                && Math.Abs(_renderedHomeLat.Value - centerLatNow) < 1e-9
                && Math.Abs(_renderedHomeLon.Value - centerLonNow) < 1e-9)
            {
                if (TryUpdateDxViaJs())
                {
                    _lastRenderedHtml = null;  // overlay changed; force a real diff on the next full render
                    return;
                }
            }

            string html = _isPolar
                ? BuildPolarMapHtml(_currentLat, _currentLon, _currentRadiusKm, _currentAzimuth, _currentHomeLat, _currentHomeLon, marginMultiplier, _currentSpotterLat, _currentSpotterLon)
                : BuildFlatMapHtml(_currentLat, _currentLon, _currentRadiusKm, _currentAzimuth, _currentHomeLat, _currentHomeLon, marginMultiplier);

            // Identical map: nothing changed, skip the costly IE reload.
            if (html == _lastRenderedHtml) return;
            _lastRenderedHtml = html;

            // A full reload is happening: remember the home/center it loads with, and mark the map
            // not-yet-loaded until LoadCompleted (so DX changes during the load still reload safely).
            _homeMapLoaded = false;
            _renderedHomeLat = centerLatNow;
            _renderedHomeLon = centerLonNow;

            File.WriteAllText(_tempMapFile, html, System.Text.Encoding.UTF8);
            var uriBuilder = new UriBuilder(new Uri(_tempMapFile));
            uriBuilder.Query = "v=" + DateTime.UtcNow.Ticks.ToString();
            MapBrowser.Navigate(uriBuilder.Uri);
        }

        // Push the current DX / spotter / radius to the already-loaded polar home map's updateDx()
        // JS function, so the map updates without a full reload. Returns false if the call fails
        // (then RenderMap falls back to a full reload).
        private bool TryUpdateDxViaJs()
        {
            try
            {
                var ic = System.Globalization.CultureInfo.InvariantCulture;
                bool hasDx = _currentHomeLat.HasValue && _currentHomeLon.HasValue;   // home set => lat/lon is a real DX
                bool hasSp = _currentSpotterLat.HasValue && _currentSpotterLon.HasValue;
                double az = _currentAzimuth ?? 0;
                MapBrowser.InvokeScript("updateDx", new object[]
                {
                    hasDx ? "1" : "0",
                    _currentLat.ToString(ic),
                    _currentLon.ToString(ic),
                    az.ToString(ic),
                    hasSp ? "1" : "0",
                    hasSp ? _currentSpotterLat.Value.ToString(ic) : "0",
                    hasSp ? _currentSpotterLon.Value.ToString(ic) : "0",
                    _currentRadiusKm.ToString(ic)
                });
                return true;
            }
            catch
            {
                return false;
            }
        }

        public void ClearMap()
        {
            _lastRenderedHtml = null;
            MapBrowser.Visibility = System.Windows.Visibility.Collapsed;
            PlaceholderPanel.Visibility = System.Windows.Visibility.Visible;
        }

        public void ShowPlaceholder(string message)
        {
            _lastRenderedHtml = null;
            PlaceholderText.Text = System.Net.WebUtility.HtmlDecode(message);
            MapBrowser.Visibility = System.Windows.Visibility.Collapsed;
            PlaceholderPanel.Visibility = System.Windows.Visibility.Visible;
        }


        private string BuildFlatMapHtml(double lat, double lon, int radiusKm, double? azimuthDeg, double? homeLat = null, double? homeLon = null, double marginMultiplier = 1.15)
        {
            // Check if we should show compass instead of map
            bool showCompass = Properties.Settings.Default.MapAreaDisplayMode == 1;
            // Always show compass overlay if azimuth is provided
            bool hasAzimuth = azimuthDeg.HasValue;

            string latStr = lat.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string lonStr = lon.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string marginJs = marginMultiplier.ToString(System.Globalization.CultureInfo.InvariantCulture);
          bool useMiles = string.Equals(Properties.Settings.Default.MapDistanceUnit, "Miles", StringComparison.OrdinalIgnoreCase);
          string useMilesJs = useMiles ? "true" : "false";
            int radiusMeters = radiusKm * 1000;
            int[] radiiOptions = { 100, 250, 500, 1000, 2000, 3500, 5000, 7500, 10000, 15000, 20000 };
          string azimuthJs = "0";
          if (azimuthDeg.HasValue)
          {
            double normalizedAzimuth = azimuthDeg.Value % 360;
            if (normalizedAzimuth < 0)
              normalizedAzimuth += 360;
            azimuthJs = normalizedAzimuth.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
          }            string homeLatJs = homeLat.HasValue ? homeLat.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "null";
            string homeLonJs = homeLon.HasValue ? homeLon.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "null";            var options = new System.Text.StringBuilder();
            foreach (int r in radiiOptions)
            {
              string optionText = useMiles
                ? Math.Round(r * 0.621371).ToString(System.Globalization.CultureInfo.InvariantCulture) + " mi"
                : r.ToString(System.Globalization.CultureInfo.InvariantCulture) + " km";
              options.AppendFormat("<option value='{0}'{1}>{2}</option>", r, r == radiusKm ? " selected" : "", optionText);
            }

            return
@"<!DOCTYPE html>
<html>
<head>
<meta http-equiv='X-UA-Compatible' content='IE=edge'/>
<meta charset='utf-8'/>
<style>
  * { margin:0; padding:0; box-sizing:border-box; }
  html, body { width:100%; height:100%; margin:0; padding:0; overflow:hidden; background:" + (showCompass ? "white" : "transparent") + @"; }
  #map { width:100%; height:100%; display:" + (showCompass ? "none" : "block") + @"; }
  #compass-ctrl {
    position:absolute; 
    top:" + (showCompass ? "50%" : "0") + @"; 
    left:" + (showCompass ? "50%" : "0") + @"; 
    " + (showCompass ? "transform: translate(-50%, -50%);" : "") + @"
    z-index:1000;
    display:" + (showCompass || hasAzimuth ? "flex" : "none") + @"; 
    flex-direction:column; 
    align-items:center;
    background:transparent;
    border:none; 
    padding:2px 2px 1px 2px;
    border-radius:0; 
    font-family:Segoe UI, Tahoma, sans-serif;
    box-shadow:none;
  }
  #compass-ring {
    width:" + (showCompass ? "280px" : "74px") + @"; 
    height:" + (showCompass ? "280px" : "74px") + @"; 
    border:2px solid #25464a; 
    border-radius:50%;
    position:relative; 
    background:radial-gradient(circle, rgba(255,255,255,0.98) 0%, rgba(220,228,236,0.95) 70%, rgba(200,212,224,0.95) 100%);
    overflow:hidden;
  }
  #compass-svg { width:100%; height:100%; display:block; }
  #compass-needle { transform-origin:50px 50px; }
  #compass-text {
    margin-top:" + (showCompass ? "8px" : "2px") + @"; 
    font-size:" + (showCompass ? "24px" : "13px") + @"; 
    font-weight:700; 
    color:#18393c;
    letter-spacing:0.3px;
    background:rgba(255,255,255,0.88);
    border:1px solid rgba(36,77,80,0.45);
    border-radius:10px;
    padding:" + (showCompass ? "6px 14px" : "2px 7px") + @";
  }
  #bottom-ctrl {
    position:absolute; bottom:0; left:0; z-index:1000;
    display:" + (showCompass ? "none" : "flex") + @"; align-items:flex-end;
  }
  #radius-stack {
    display:flex; flex-direction:column; align-items:center;
  }
  #radius-ctrl {
    background:rgba(255,255,255,0.88); border:1px solid #aaa;
    border-radius:0;
    padding:2px 4px; font-size:13px; font-family:sans-serif; cursor:pointer;
    height:100%;
  }
  #center-btn {
    background:#9FCBF5; border:1px solid #4B76A0;
    border-radius:10px; padding:0 6px; cursor:pointer;
    display:flex; align-items:center; justify-content:center;
    color:#333; height:24px; margin-bottom:2px;
    font-family:sans-serif; font-size:11px; font-weight:700;
  }
  #center-btn:hover { background:#8CBDF0; }
  #center-btn svg { width:16px; height:16px; }
  #center-btn .de-label { margin-right:4px; }
  #proj-btn {
    position:absolute; top:0; right:0; z-index:1000;
    background:#9FCBF5; border:1px solid #4B76A0;
    border-radius:10px; padding:0 6px; cursor:pointer;
    height:24px; display:" + (showCompass ? "none" : "flex") + @"; align-items:center; justify-content:center;
    font-size:11px; font-weight:700; font-family:sans-serif; color:#333;
  }
  #proj-btn:hover { background:#8CBDF0; }
  #distance-stack {
    position:absolute; right:0; bottom:0; z-index:1000;
    display:" + (showCompass ? "none" : "flex") + @"; flex-direction:column; align-items:center;
  }
  #dx-center-btn {
    background:#9FCBF5; border:1px solid #4B76A0;
    border-radius:10px; padding:0 6px; cursor:pointer;
    display:flex; align-items:center; justify-content:center;
    color:#333; height:24px; margin-bottom:2px;
    font-family:sans-serif; font-size:11px; font-weight:700;
  }
  #dx-center-btn:hover { background:#8CBDF0; }
  #dx-center-btn .dx-label { margin-right:4px; }
  #dx-center-btn svg { width:16px; height:16px; }
  #distance-box {
    background:rgba(255,255,255,0.9); border:1px solid #aaa;
    border-radius:0; padding:3px 7px;
    font-size:13px; font-weight:700; font-family:sans-serif; color:#333;
    white-space:nowrap;
  }
</style>
<link rel='stylesheet' href='https://unpkg.com/leaflet@1.9.4/dist/leaflet.css'/>
</head>
<body>
<div id='map'></div>
<button id='proj-btn' onclick='toggleProjection()' title='Switch to polar azimuthal map'>&#127757; Polar</button>
<div id='compass-ctrl'>
  <div id='compass-ring'>
    <svg id='compass-svg' viewBox='0 0 100 100' aria-hidden='true'>
      <circle cx='50' cy='50' r='48' fill='#8dd6d4' stroke='#2b4f50' stroke-width='3'/>
      <circle cx='50' cy='50' r='39' fill='#efe6a0' stroke='#b9b37e' stroke-width='2'/>
      <g opacity='0.4' fill='#7f7858'>
        <polygon points='50,17 57,50 50,83 43,50'/>
        <polygon points='17,50 50,57 83,50 50,43'/>
        <polygon points='26,26 53,47 74,74 47,53'/>
        <polygon points='74,26 53,53 26,74 47,47'/>
      </g>
      <g fill='#134346' font-size='11' font-weight='700' text-anchor='middle' font-family='Segoe UI, Tahoma, sans-serif'>
        <text x='50' y='16'>N</text>
        <text x='86' y='54'>E</text>
        <text x='50' y='91'>S</text>
        <text x='14' y='54'>W</text>
      </g>
      <g id='compass-needle'>
        <polygon points='50,12 58,50 42,50' fill='#d10f20' stroke='#7b0d18' stroke-width='1.2'/>
        <polygon points='50,88 58,50 42,50' fill='#197a74' stroke='#0f4e4a' stroke-width='1.2'/>
      </g>
      <circle cx='50' cy='50' r='4.5' fill='#e9e4b9' stroke='#6f6a4d' stroke-width='1.2'/>
    </svg>
  </div>
  <div id='compass-text'>AZ 0°</div>
</div>
<div id='bottom-ctrl'>
  <div id='radius-stack'>
    <button id='center-btn' onclick='recenter()' title='Re-center map'>
      <span class='de-label'>DE</span>
      <svg viewBox='0 0 24 24' fill='none' stroke='currentColor' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'>
        <circle cx='12' cy='12' r='3'/>
        <line x1='12' y1='2' x2='12' y2='6'/>
        <line x1='12' y1='18' x2='12' y2='22'/>
        <line x1='2' y1='12' x2='6' y2='12'/>
        <line x1='18' y1='12' x2='22' y2='12'/>
      </svg>
    </button>
    <select id='radius-ctrl' onchange='onRadiusChange(this.value)'>" + options.ToString() + @"</select>
  </div>
</div>
<div id='distance-stack'>
  <button id='dx-center-btn' onclick='centerOnDx()' title='Center on DX station'>
    <span class='dx-label'>DX</span>
    <svg viewBox='0 0 24 24' fill='none' stroke='currentColor' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'>
      <circle cx='12' cy='12' r='3'/>
      <line x1='12' y1='2' x2='12' y2='6'/>
      <line x1='12' y1='18' x2='12' y2='22'/>
      <line x1='2' y1='12' x2='6' y2='12'/>
      <line x1='18' y1='12' x2='22' y2='12'/>
    </svg>
  </button>
  <div id='distance-box'>DIST --</div>
</div>
<script src='https://unpkg.com/leaflet@1.9.4/dist/leaflet.js'></script>
<script>
window.onerror = function() { return true; };
var homeLat = " + latStr + @", homeLon = " + lonStr + @";
var azimuthDeg = " + azimuthJs + @";
var radiusMeters = " + radiusMeters + @";
var marginMultiplier = " + marginJs + @";
var useMiles = " + useMilesJs + @";
var dxLat = homeLat, dxLon = homeLon;
var operatorLat = " + homeLatJs + @";
var operatorLon = " + homeLonJs + @";
var hasDxReference = isFinite(dxLat) && isFinite(dxLon);
// Center map and radius circle on operator home; fall back to DX if home unknown
var circleLat = (operatorLat !== null) ? operatorLat : dxLat;
var circleLon = (operatorLon !== null) ? operatorLon : dxLon;
var map = L.map('map', { zoomControl: false, attributionControl: false, zoomSnap: 0 }).setView([circleLat, circleLon], 5);
L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', { maxZoom: 18 }).addTo(map);
// Equator
L.polyline([[0,-180],[0,-90],[0,0],[0,90],[0,180]], { color:'#000000', weight:1.2, opacity:0.5, interactive:false }).addTo(map);

// Create visible red circle to show the search radius (centered on home)
var radiusCircle = L.circle([circleLat, circleLon], { radius: radiusMeters, color: '#E53935', fill: false, weight: 2 }).addTo(map);

function haversineMeters(lat1, lon1, lat2, lon2) {
  var toRad = Math.PI / 180;
  var dLat = (lat2 - lat1) * toRad;
  var dLon = (lon2 - lon1) * toRad;
  var a = Math.sin(dLat / 2) * Math.sin(dLat / 2) +
      Math.cos(lat1 * toRad) * Math.cos(lat2 * toRad) *
      Math.sin(dLon / 2) * Math.sin(dLon / 2);
  return 6371000 * 2 * Math.atan2(Math.sqrt(a), Math.sqrt(1 - a));
}

function formatDistanceText(distanceMeters) {
  if (distanceMeters === null || !isFinite(distanceMeters)) {
    return useMiles ? 'DIST 0 mi' : 'DIST 0 km';
  }
  if (useMiles) {
    return 'DIST ' + Math.round(distanceMeters / 1609.344) + ' mi';
  }
  return 'DIST ' + Math.round(distanceMeters / 1000) + ' km';
}

function destinationPoint(lat, lon, bearingRad, distanceMeters) {
  var R = 6371000;
  var angDist = distanceMeters / R;
  var lat1 = lat * Math.PI / 180;
  var lon1 = lon * Math.PI / 180;

  var sinLat1 = Math.sin(lat1), cosLat1 = Math.cos(lat1);
  var sinAng = Math.sin(angDist), cosAng = Math.cos(angDist);

  var lat2 = Math.asin(sinLat1 * cosAng + cosLat1 * sinAng * Math.cos(bearingRad));
  var lon2 = lon1 + Math.atan2(
      Math.sin(bearingRad) * sinAng * cosLat1,
      cosAng - sinLat1 * Math.sin(lat2)
  );

  return { lat: lat2 * 180 / Math.PI, lon: lon2 * 180 / Math.PI };
}

function buildFitBounds() {
  var b = radiusCircle.getBounds();
  if (operatorLat !== null && operatorLon !== null) {
    var dxDistMeters = haversineMeters(operatorLat, operatorLon, dxLat, dxLon);
    b = b.extend(L.latLng(dxLat, dxLon));

    if (dxDistMeters > radiusMeters) {
      var lat1 = operatorLat * Math.PI / 180;
      var lat2 = dxLat * Math.PI / 180;
      var dLon = (dxLon - operatorLon) * Math.PI / 180;
      var y = Math.sin(dLon) * Math.cos(lat2);
      var x = Math.cos(lat1) * Math.sin(lat2) - Math.sin(lat1) * Math.cos(lat2) * Math.cos(dLon);
      var bearing = Math.atan2(y, x);
      var p = destinationPoint(operatorLat, operatorLon, bearing, dxDistMeters * marginMultiplier);
      b = b.extend(L.latLng(p.lat, p.lon));
    }
  }
  return b;
}

map.fitBounds(buildFitBounds(), { padding: [2, 2] });

// Interpolate N points along a great circle arc (IE11-safe, no external lib)
function gcArcPoints(lat1, lon1, lat2, lon2, n) {
    var toRad = Math.PI/180, toDeg = 180/Math.PI;
    var la1=lat1*toRad, lo1=lon1*toRad, la2=lat2*toRad, lo2=lon2*toRad;
    var d = 2*Math.asin(Math.sqrt(Math.pow(Math.sin((la2-la1)/2),2)+Math.cos(la1)*Math.cos(la2)*Math.pow(Math.sin((lo2-lo1)/2),2)));
    if (d < 0.0001) return [[lat1,lon1],[lat2,lon2]];
    var pts = [];
    for (var i=0; i<=n; i++) {
        var f=i/n;
        var A=Math.sin((1-f)*d)/Math.sin(d), B=Math.sin(f*d)/Math.sin(d);
        var x=A*Math.cos(la1)*Math.cos(lo1)+B*Math.cos(la2)*Math.cos(lo2);
        var y=A*Math.cos(la1)*Math.sin(lo1)+B*Math.cos(la2)*Math.sin(lo2);
        var z=A*Math.sin(la1)+B*Math.sin(la2);
        pts.push([Math.atan2(z,Math.sqrt(x*x+y*y))*toDeg, Math.atan2(y,x)*toDeg]);
    }
    return pts;
}

// Draw DX station marker and GC arc if home location is known
if (operatorLat !== null && operatorLon !== null) {
    L.marker([dxLat, dxLon]).addTo(map);
    var homeIcon = L.divIcon({ className: '', html: '<div style=""width:10px;height:10px;background:#1565C0;border:2px solid #fff;border-radius:50%;box-shadow:0 0 3px rgba(0,0,0,0.6)""></div>', iconAnchor:[5,5] });
    L.marker([operatorLat, operatorLon], { icon: homeIcon }).addTo(map);
    var arcPts = gcArcPoints(operatorLat, operatorLon, dxLat, dxLon, 100);
    L.polyline(arcPts, { color: '#1565C0', weight: 2, dashArray: '6,5', opacity: 0.8 }).addTo(map);
} else {
    L.marker([dxLat, dxLon]).addTo(map);
}

document.getElementById('compass-text').innerHTML = 'AZ ' + Math.round(azimuthDeg) + '&deg;';
document.getElementById('compass-needle').setAttribute('transform', 'rotate(' + azimuthDeg + ' 50 50)');
var dxDistanceMeters = (operatorLat !== null && operatorLon !== null)
  ? haversineMeters(operatorLat, operatorLon, dxLat, dxLon)
  : null;
document.getElementById('distance-box').innerHTML = formatDistanceText(dxDistanceMeters);
if (!hasDxReference) {
  document.getElementById('dx-center-btn').style.display = 'none';
}

function fitAll() {
  map.fitBounds(buildFitBounds(), { padding: [2, 2] });
}
function onRadiusChange(km) {
    radiusMeters = km * 1000;
    radiusCircle.setRadius(radiusMeters);
    fitAll();
    try { window.external.SetRadius(km); } catch(e) {}
}
function recenter() {
    radiusCircle.setLatLng([circleLat, circleLon]);
    radiusCircle.setRadius(radiusMeters);
    fitAll();
}
function centerOnDx() {
  if (!hasDxReference) return;
  map.setView([dxLat, dxLon], map.getZoom());
}
function toggleProjection() {
    try { window.external.ToggleProjection(); } catch(e) {}
}

function showMapView() {
    document.getElementById('compass-ctrl').style.display = 'none';
}

function showCompassView() {
    document.getElementById('compass-ctrl').style.display = 'flex';
}

// Auto-resize on window resize: invalidate map to reflow and refocus
window.addEventListener('resize', function() {
    if (map) {
        map.invalidateSize();
        fitAll();
    }
});
</script>
</body>
</html>";
        }

        private string BuildPolarMapHtml(double dxLat, double dxLon, int radiusKm, double? azimuthDeg, double? homeLat = null, double? homeLon = null, double marginMultiplier = 1.15, double? spotterLat = null, double? spotterLon = null)
        {
            // Center on home QTH; fall back to DX if no home available.
            double centerLat = homeLat.HasValue ? homeLat.Value : dxLat;
            double centerLon = homeLon.HasValue ? homeLon.Value : dxLon;
          // Spotter of the selected cluster spot (so the DE button can center on it).
          bool hasSpotter = spotterLat.HasValue && spotterLon.HasValue;
          string hasSpotterJs = hasSpotter ? "true" : "false";
          string spotterLatJs = hasSpotter ? spotterLat.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "0";
          string spotterLonJs = hasSpotter ? spotterLon.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "0";
          bool hasHomeReference = homeLat.HasValue && homeLon.HasValue;
          bool useMiles = string.Equals(Properties.Settings.Default.MapDistanceUnit, "Miles", StringComparison.OrdinalIgnoreCase);
          string useMilesJs = useMiles ? "true" : "false";
          string hasHomeReferenceJs = hasHomeReference ? "true" : "false";
            string centerLatJs = centerLat.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string centerLonJs = centerLon.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string dxLatJs = dxLat.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string dxLonJs = dxLon.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string marginJs = marginMultiplier.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string azJs = "0";
            if (azimuthDeg.HasValue)
            {
                double norm = azimuthDeg.Value % 360;
                if (norm < 0) norm += 360;
                azJs = norm.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            }
            int[] radiiOptions = { 100, 250, 500, 1000, 2000, 3500, 5000, 7500, 10000, 15000, 20000 };
            var options = new System.Text.StringBuilder();
            foreach (int r in radiiOptions)
            {
              string optionText = useMiles
                ? Math.Round(r * 0.621371).ToString(System.Globalization.CultureInfo.InvariantCulture) + " mi"
                : r.ToString(System.Globalization.CultureInfo.InvariantCulture) + " km";
              options.AppendFormat("<option value='{0}'{1}>{2}</option>", r, r == radiusKm ? " selected" : "", optionText);
            }

            return
@"<!DOCTYPE html>
<html>
<head>
<meta http-equiv='X-UA-Compatible' content='IE=edge'/>
<meta charset='utf-8'/>
<style>
  * { margin:0; padding:0; box-sizing:border-box; }
  html, body { width:100%; height:100%; overflow:hidden; background:#000000; }
  svg#polar-svg { width:100%; height:100%; display:block; }
  #proj-btn {
    position:absolute; top:0; right:0; z-index:1000;
    background:#9FCBF5; border:1px solid #4B76A0;
    border-radius:10px; padding:0 6px; cursor:pointer;
    height:24px; display:flex; align-items:center; justify-content:center;
    font-size:11px; font-weight:700; font-family:sans-serif; color:#333;
  }
  #proj-btn:hover { background:#8CBDF0; }
  #az-only {
    position:absolute; top:-1px; left:-1px; z-index:1000;
    font-size:14px; font-weight:700; color:#e0f0ff;
    background:rgba(0,0,0,0.55); border-radius:0; padding:2px 6px;
    font-family:sans-serif;
  }
  #distance-stack {
    position:absolute; right:0; bottom:0; z-index:1000;
    display:flex; flex-direction:column; align-items:flex-end;
  }
  #dx-center-btn {
    background:#9FCBF5; border:1px solid #4B76A0;
    border-radius:10px; padding:0 6px; cursor:pointer;
    display:flex; align-items:center; justify-content:center;
    color:#333; height:24px; margin-bottom:2px;
    font-family:sans-serif; font-size:11px; font-weight:700;
  }
  #dx-center-btn:hover { background:#8CBDF0; }
  #dx-center-btn .dx-label { margin-right:4px; }
  #dx-center-btn svg { width:16px; height:16px; }
  #distance-box {
    background:rgba(255,255,255,0.9); border:1px solid #aaa;
    border-radius:0; padding:3px 7px;
    font-size:13px; font-weight:700; font-family:sans-serif; color:#333;
    white-space:nowrap;
  }
  #bottom-ctrl {
    position:absolute; bottom:0; left:0; z-index:1000;
    display:flex; align-items:flex-end;
  }
  #radius-stack {
    display:flex; flex-direction:column; align-items:flex-start;
  }
  #radius-ctrl {
    background:rgba(255,255,255,0.88); border:1px solid #aaa;
    border-radius:0;
    padding:2px 4px; font-size:13px; font-family:sans-serif; cursor:pointer;
  }
  #center-btn, #home-btn {
    background:#9FCBF5; border:1px solid #4B76A0;
    border-radius:10px; padding:0 6px; cursor:pointer;
    display:flex; align-items:center; justify-content:center;
    color:#333; height:24px; margin-bottom:2px;
    font-family:sans-serif; font-size:11px; font-weight:700;
  }
  #center-btn:hover, #home-btn:hover { background:#8CBDF0; }
  #center-btn svg, #home-btn svg { width:16px; height:16px; }
  #center-btn .de-label { margin-right:4px; }
</style>
</head>
<body>
<svg id='polar-svg'></svg>
<button id='proj-btn' onclick='toggleProjection()'>&#9974; Flat</button>
<div id='az-only'>AZ 0&deg;</div>
<div id='bottom-ctrl'>
  <div id='radius-stack'>
    <button id='home-btn' onclick='centerOnHome()' title='Center on home'><svg viewBox='0 0 24 24' fill='none' stroke='currentColor' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><path d='M3 10.5L12 3l9 7.5'/><path d='M5 9.5V21h5v-6h4v6h5V9.5'/></svg></button>
    <button id='center-btn' onclick='centerOnSpotter()' title='Center on spotter'><span class='de-label'>DE</span><svg viewBox='0 0 24 24' fill='none' stroke='currentColor' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><circle cx='12' cy='12' r='3'/><line x1='12' y1='2' x2='12' y2='6'/><line x1='12' y1='18' x2='12' y2='22'/><line x1='2' y1='12' x2='6' y2='12'/><line x1='18' y1='12' x2='22' y2='12'/></svg></button>
    <select id='radius-ctrl' onchange='onRadiusChange(this.value)'>" + options.ToString() + @"</select>
  </div>
</div>
<div id='distance-stack'>
  <button id='dx-center-btn' onclick='centerOnDx()' title='Center on DX station'><span class='dx-label'>DX</span><svg viewBox='0 0 24 24' fill='none' stroke='currentColor' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><circle cx='12' cy='12' r='3'/><line x1='12' y1='2' x2='12' y2='6'/><line x1='12' y1='18' x2='12' y2='22'/><line x1='2' y1='12' x2='6' y2='12'/><line x1='18' y1='12' x2='22' y2='12'/></svg></button>
  <div id='distance-box'>DIST --</div>
</div>
" + MapAssetProvider.D3ScriptTag + @"
" + MapAssetProvider.CountryDataScriptTag + @"
<script>
window.onerror = function() { return true; };

var centerLat = " + centerLatJs + @";
var centerLon = " + centerLonJs + @";
var dxLat    = " + dxLatJs + @";
var dxLon    = " + dxLonJs + @";
var azimuthDeg = " + azJs + @";
var radiusKm   = " + radiusKm.ToString() + @";
var marginMultiplier = " + marginJs + @";
var useMiles = " + useMilesJs + @";
var hasHomeReference = " + hasHomeReferenceJs + @";
var spotterLat = " + spotterLatJs + @";
var spotterLon = " + spotterLonJs + @";
var hasSpotter = " + hasSpotterJs + @";
var EARTH_KM   = 6371;
var hasDxReference = isFinite(dxLat) && isFinite(dxLon);

// GC distance home->DX in km (haversine), so we can expand scale if DX is beyond selected radius
function haversineKm(lat1, lon1, lat2, lon2) {
  var toRad = Math.PI / 180;
  var dLat = (lat2-lat1)*toRad, dLon = (lon2-lon1)*toRad;
  var a = Math.sin(dLat/2)*Math.sin(dLat/2) + Math.cos(lat1*toRad)*Math.cos(lat2*toRad)*Math.sin(dLon/2)*Math.sin(dLon/2);
  return EARTH_KM * 2 * Math.atan2(Math.sqrt(a), Math.sqrt(1-a));
}
var dxDistKm = hasHomeReference ? haversineKm(centerLat, centerLon, dxLat, dxLon) : null;

function formatDistanceText(distanceKm) {
  if (distanceKm === null || !isFinite(distanceKm)) {
    return useMiles ? 'DIST 0 mi' : 'DIST 0 km';
  }
  if (useMiles) {
    return 'DIST ' + Math.round(distanceKm * 0.621371) + ' mi';
  }
  return 'DIST ' + Math.round(distanceKm) + ' km';
}

// Azimuth label (polar mode has no compass ring)
document.getElementById('az-only').innerHTML = 'AZ ' + Math.round(azimuthDeg) + '&deg;' + '<br>(' + Math.round((azimuthDeg + 180) % 360) + '&deg;)';
document.getElementById('distance-box').innerHTML = formatDistanceText(dxDistKm);
if (!hasDxReference) {
  document.getElementById('dx-center-btn').style.display = 'none';
}

// SVG setup
var W = window.innerWidth, H = window.innerHeight;
var mapR = Math.floor((Math.min(W, H) / 2) - 4);
var cx = W / 2, cy = H / 2;

var svg = d3.select('#polar-svg').attr('width', W).attr('height', H);

// Define clip path first so all subsequent elements can reference it
var defs = svg.append('defs');
defs.append('clipPath').attr('id', 'globe-clip')
    .append('circle').attr('cx', cx).attr('cy', cy).attr('r', mapR - 1);

var projection = d3.geoAzimuthalEquidistant()
    .rotate([-centerLon, -centerLat])
    .scale(mapR / Math.PI)
    .translate([cx, cy])
    .clipAngle(180);

var path = d3.geoPath().projection(projection);
var baseScale = mapR / Math.PI;
var viewCenterLat = centerLat;
var viewCenterLon = centerLon;
scaleToRadius();

function normalizeLon(lon) {
  while (lon < -180) lon += 360;
  while (lon > 180) lon -= 360;
  return lon;
}

function clampLat(lat) {
  if (lat < -85) return -85;
  if (lat > 85) return 85;
  return lat;
}

function applyViewCenter() {
  projection.rotate([-viewCenterLon, -viewCenterLat]);
}

function scaleToRadius() {
  // Keep selected-radius behavior, but when DX is outside the selected radius
  // add headroom so the DX marker is not glued to the edge.
  var effectiveKm = radiusKm;
  if (dxDistKm > radiusKm) {
    effectiveKm = dxDistKm * marginMultiplier;
  }
  var ang = effectiveKm / EARTH_KM;
  if (!isFinite(ang) || ang <= 0) { projection.scale(baseScale); return; }
  var targetPx = mapR - 4;
  projection.scale(targetPx / ang);
}

// Ocean fill
var oceanFill = svg.append('circle').attr('class', 'ocean-fill')
    .attr('cx', cx).attr('cy', cy).attr('r', mapR)
    .attr('fill', '#b8e8ee').attr('stroke', '#000000').attr('stroke-width', 1.5);

// Layer for countries (inserted before overlays)
var countriesG = svg.append('g').attr('clip-path', 'url(#globe-clip)');

// Graticule
svg.append('path')
    .datum(d3.geoGraticule().step([30, 30])())
  .attr('class', 'graticule-path')
    .attr('d', path)
    .attr('fill', 'none').attr('stroke', '#6bb7c4').attr('stroke-width', 0.7)
    .attr('clip-path', 'url(#globe-clip)');
// Equator
svg.append('path').datum({type:'LineString', coordinates:[[-180,0],[-90,0],[0,0],[90,0],[180,0]]})
    .attr('class', 'graticule-path')
    .attr('fill','none').attr('stroke','rgba(0,0,0,0.55)').attr('stroke-width',1.2)
    .attr('d',path).attr('clip-path','url(#globe-clip)');

// Distance rings layer (above countries)
var ringsG = svg.append('g').attr('clip-path', 'url(#globe-clip)');
function drawRings() {
    ringsG.selectAll('*').remove();
  var ringKms = [];
  for (var i = 1; i <= 5; i++) {
    ringKms.push(Math.round((radiusKm * i) / 5));
  }
    ringKms.forEach(function(km) {
        var ang = km / EARTH_KM;
        if (ang >= Math.PI) return;
        var r = projection.scale() * ang;
        ringsG.append('circle').attr('cx', cx).attr('cy', cy).attr('r', r)
            .attr('fill', 'none').attr('stroke', 'rgba(0,0,0,0.2)')
            .attr('stroke-width', 1).attr('stroke-dasharray', '4,3');
        var ringLabel = useMiles ? (Math.round(km * 0.621371) + ' mi') : (km + ' km');
        ringsG.append('text').attr('x', cx + 3).attr('y', cy - r - 2)
          .attr('fill', 'rgba(0,0,0,0.45)').attr('font-size', '9px').text(ringLabel);
    });
}
drawRings();

// Radius ring
function drawRadiusRing(km) {
    svg.selectAll('.radius-ring').remove();
    var ang = km / EARTH_KM;
    if (ang < Math.PI) {
        var r = projection.scale() * ang;
        svg.append('circle').attr('class', 'radius-ring')
            .attr('cx', cx).attr('cy', cy).attr('r', r)
            .attr('fill', 'none').attr('stroke', '#E53935').attr('stroke-width', 2);
    }
}
drawRadiusRing(radiusKm);

// Overlays layer (always on top)
var overlaysG = svg.append('g').attr('clip-path', 'url(#globe-clip)');
function drawOverlays() {
    overlaysG.selectAll('*').remove();

    // Great-circle line home -> DX (only when a DX is referenced)
    if (hasDxReference) {
        try {
            var gcLine = { type: 'LineString', coordinates: [[centerLon, centerLat], [dxLon, dxLat]] };
            overlaysG.append('path').datum(gcLine).attr('d', path)
                .attr('fill', 'none').attr('stroke', '#90CAF9').attr('stroke-width', 2.5)
                .attr('stroke-dasharray', '7,4').attr('clip-path', 'url(#globe-clip)');
        } catch(e2) {}
    }

    // Home dot (projected): may move away from center while dragging view
    try {
        var homePt = projection([centerLon, centerLat]);
        if (homePt && isFinite(homePt[0]) && isFinite(homePt[1])) {
            overlaysG.append('circle')
              .attr('cx', homePt[0]).attr('cy', homePt[1]).attr('r', 5)
              .attr('fill', '#1565C0').attr('stroke', 'none');
        }
    } catch(eHome) {}

    // DX dot (only when a DX is referenced)
    if (hasDxReference) {
        try {
            var dxPt = projection([dxLon, dxLat]);
            if (dxPt && isFinite(dxPt[0]) && isFinite(dxPt[1])) {
                overlaysG.append('circle')
                .attr('cx', dxPt[0]).attr('cy', dxPt[1]).attr('r', 4)
                .attr('fill', '#E53935').attr('stroke', 'none');
            }
        } catch(e3) {}
    }

    // Spotter dot (black) - the spotter of the selected cluster spot, so pressing DE
    // (which centers on the spotter) lands on a visible marker.
    if (hasSpotter) {
        try {
            var spPt = projection([spotterLon, spotterLat]);
            if (spPt && isFinite(spPt[0]) && isFinite(spPt[1])) {
                overlaysG.append('circle')
                  .attr('cx', spPt[0]).attr('cy', spPt[1]).attr('r', 3)
                  .attr('fill', '#000000').attr('stroke', '#ffffff').attr('stroke-width', 1);
            }
        } catch(eSp) {}
    }

    // Outer border ring
    overlaysG.append('circle').attr('cx', cx).attr('cy', cy).attr('r', mapR)
        .attr('fill', 'none').attr('stroke', '#2a607a').attr('stroke-width', 2);
}

// Update the DX / spotter in place WITHOUT reloading the page (called from C# when only the
// DX changes, e.g. typing or clearing the DX callsign). This avoids re-parsing the embedded d3
// and rebuilding the country DOM, so clearing (F9) is fast. Same home center is assumed.
function updateDx(hasDxStr, dLat, dLon, az, hasSpStr, sLat, sLon, newRadiusKm) {
    dxLat = parseFloat(dLat); dxLon = parseFloat(dLon);
    azimuthDeg = parseFloat(az);
    spotterLat = parseFloat(sLat); spotterLon = parseFloat(sLon);
    hasSpotter = (hasSpStr === '1' || hasSpStr === true || hasSpStr === 'true');
    hasDxReference = (hasDxStr === '1' || hasDxStr === true || hasDxStr === 'true') && isFinite(dxLat) && isFinite(dxLon);
    radiusKm = parseInt(newRadiusKm, 10);
    dxDistKm = (hasHomeReference && hasDxReference) ? haversineKm(centerLat, centerLon, dxLat, dxLon) : null;

    var azEl = document.getElementById('az-only');
    if (azEl) azEl.innerHTML = hasDxReference
        ? ('AZ ' + Math.round(azimuthDeg) + '&deg;' + '<br>(' + Math.round((azimuthDeg + 180) % 360) + '&deg;)')
        : 'AZ --';
    var distEl = document.getElementById('distance-box');
    if (distEl) distEl.innerHTML = hasDxReference ? formatDistanceText(dxDistKm) : 'DIST --';
    var dxBtn = document.getElementById('dx-center-btn');
    if (dxBtn) dxBtn.style.display = hasDxReference ? '' : 'none';

    viewCenterLat = centerLat; viewCenterLon = centerLon;
    applyViewCenter();
    scaleToRadius();
    countriesG.selectAll('path').attr('d', path);
    svg.selectAll('.graticule-path').attr('d', path);
    drawRings(); drawRadiusRing(radiusKm); drawOverlays();
}
drawOverlays();

// Draw colored countries from the offline-embedded data (window.DXCC_DATA). Each feature
// is wrapped as a GeoJSON Feature so every existing 'countriesG...attr(d, path)' redraw
// keeps working unchanged. Per-country fill comes from the precomputed 4-color palette.
try {
    var dxccFeatures = window.DXCC_DATA.features.map(function(f) {
        return { type: 'Feature', properties: { ci: f.ci, p: f.p }, geometry: f.geometry };
    });
    var dxccPalette = window.DXCC_DATA.palette;
    countriesG.selectAll('path').data(dxccFeatures).enter().append('path')
        .attr('d', path)
        .attr('fill', function(d) { return dxccPalette[d.properties.ci]; })
        .attr('stroke', '#777777').attr('stroke-width', 0.4);
} catch(e4) {}
drawOverlays();
drawRadiusRing(radiusKm);
if (autoZoomActive) applyAutoZoom();

function onRadiusChange(km) {
    radiusKm = parseInt(km, 10);
  scaleToRadius();
  countriesG.selectAll('path').attr('d', path);
  svg.selectAll('.graticule-path').attr('d', path);
  drawRings();
    drawRadiusRing(radiusKm);
  drawOverlays();
    try { window.external.SetRadius(km); } catch(e) {}
}
function recenter() {
  viewCenterLat = centerLat;
  viewCenterLon = centerLon;
  applyViewCenter();
    scaleToRadius();
    countriesG.selectAll('path').attr('d', path);
    svg.selectAll('.graticule-path').attr('d', path);
    drawRings();
    drawRadiusRing(radiusKm);
    drawOverlays();
}
function centerOnDx() {
  if (!hasDxReference) return;
  viewCenterLat = dxLat;
  viewCenterLon = dxLon;
  applyViewCenter();
  scaleToRadius();
  countriesG.selectAll('path').attr('d', path);
  svg.selectAll('.graticule-path').attr('d', path);
  drawRings();
  drawRadiusRing(radiusKm);
  drawOverlays();
}
// Home icon button: pan back to the home QTH, keeping the current zoom.
function centerOnHome() {
  viewCenterLat = centerLat;
  viewCenterLon = centerLon;
  applyViewCenter();
  countriesG.selectAll('path').attr('d', path);
  svg.selectAll('.graticule-path').attr('d', path);
  drawRings();
  drawRadiusRing(radiusKm);
  drawOverlays();
}
// DE button: pan to the spotter of the selected cluster spot. If no spotter is known
// (e.g. the DX was typed by hand), fall back to home so the button still does something.
function centerOnSpotter() {
  if (hasSpotter && isFinite(spotterLat) && isFinite(spotterLon)) {
    viewCenterLat = spotterLat;
    viewCenterLon = spotterLon;
  } else {
    viewCenterLat = centerLat;
    viewCenterLon = centerLon;
  }
  applyViewCenter();
  countriesG.selectAll('path').attr('d', path);
  svg.selectAll('.graticule-path').attr('d', path);
  drawRings();
  drawRadiusRing(radiusKm);
  drawOverlays();
}
function toggleProjection() {
    try { window.external.ToggleProjection(); } catch(e) {}
}

// Mouse drag pan: rotate globe and redraw to create a new polar-centered view
svg.call(
  d3.drag()
    .on('start', function() {
      var ev = d3.event;
      this._dragStartX = ev.x;
      this._dragStartY = ev.y;
      this._dragStartLon = viewCenterLon;
      this._dragStartLat = viewCenterLat;
    })
    .on('drag', function() {
      var ev = d3.event;
      var dx = ev.x - this._dragStartX;
      var dy = ev.y - this._dragStartY;
      // Lower sensitivity so drag feels controlled and does not jump too much.
      var degPerPixel = 20 / Math.max(mapR, 1);

      viewCenterLon = normalizeLon(this._dragStartLon - (dx * degPerPixel));
      viewCenterLat = clampLat(this._dragStartLat + (dy * degPerPixel));
      applyViewCenter();

      countriesG.selectAll('path').attr('d', path);
      svg.selectAll('.graticule-path').attr('d', path);
      drawRings();
      drawRadiusRing(radiusKm);
      drawOverlays();
    })
);

// Mouse-wheel zoom: scale projection up/down and redraw everything
document.addEventListener('wheel', function(e) {
    e.preventDefault();
    var factor = (e.deltaY < 0) ? 1.5 : (1 / 1.5);
    var newScale = projection.scale() * factor;
    var minScale = baseScale * 0.5;
    var maxScale = baseScale * 100;
    if (newScale < minScale) newScale = minScale;
    if (newScale > maxScale) newScale = maxScale;
    projection.scale(newScale);
    countriesG.selectAll('path').attr('d', path);
    svg.selectAll('.graticule-path').attr('d', path);
    drawRings();
    drawRadiusRing(radiusKm);
    drawOverlays();
}, { passive: false });

// Auto-resize on window resize: recalculate dimensions and redraw polar map
window.addEventListener('resize', function() {
    var oldW = W, oldH = H;
    W = window.innerWidth;
    H = window.innerHeight;
    if (W === oldW && H === oldH) return; // No size change

    mapR = Math.floor((Math.min(W, H) / 2) - 4);
    cx = W / 2;
    cy = H / 2;
    baseScale = mapR / Math.PI;

    svg.attr('width', W).attr('height', H);

    // Update ocean fill background using direct reference
    oceanFill.attr('cx', cx).attr('cy', cy).attr('r', mapR);

    // Update clip path circle
    svg.select('#globe-clip circle').attr('cx', cx).attr('cy', cy).attr('r', mapR - 1);

    projection.translate([cx, cy]);
    applyViewCenter();
    scaleToRadius();
    
    // Redraw all elements
    countriesG.selectAll('path').attr('d', path);
    svg.selectAll('.graticule-path').attr('d', path);
    drawRings();
    drawRadiusRing(radiusKm);
    drawOverlays();
});
</script>
</body>
</html>";
        }

        private void SuppressScriptErrors()
        {
            try
            {
                dynamic activeX = typeof(WebBrowser)
                    .GetProperty("ActiveXInstance", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.GetValue(MapBrowser, null);
                if (activeX != null)
                    activeX.Silent = true;
            }
            catch { }
        }

        public void ShowMapView()
        {
            try
            {
                MapBrowser.InvokeScript("showMapView");
            }
            catch { }
        }

        public void ShowCompassView()
        {
            try
            {
                MapBrowser.InvokeScript("showCompassView");
            }
            catch { }
        }
    }
}
