using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Controls;

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
    }

    public partial class MapUserControl : UserControl
    {
        private readonly string _tempMapFile;
        private int _currentRadiusKm = 1000;
        private double _currentLat, _currentLon;
        private double? _currentAzimuth, _currentHomeLat, _currentHomeLon;
        private bool _isPolar;
        private bool _isClusterMode;
        private System.Collections.Generic.List<ClusterSpotInfo> _clusterSpots;
        private double _clusterHomeLat, _clusterHomeLon;
        private bool _clusterMapLoaded;

        public bool IsClusterMode => _isClusterMode;

        public event Action<int> RadiusChanged;
        public event Action<string, string> SpotTuneRequested;

        internal void RaiseRadiusChanged(int km) => RadiusChanged?.Invoke(km);
        internal void RaiseSpotTuneRequested(string freq, string mode) => SpotTuneRequested?.Invoke(freq, mode);

        internal void ToggleProjection()
        {
            _isPolar = !_isPolar;
            _clusterMapLoaded = false;
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
            MapBrowser.LoadCompleted += (s, e) =>
            {
                SuppressScriptErrors();
                if (_isClusterMode)
                    _clusterMapLoaded = true;
            };
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
                string spStr = (s.SpotterLat.HasValue && s.SpotterLon.HasValue)
                    ? (isPolar
                        ? "[" + s.SpotterLon.Value.ToString(ic) + "," + s.SpotterLat.Value.ToString(ic) + "]"
                        : "[" + s.SpotterLat.Value.ToString(ic) + "," + s.SpotterLon.Value.ToString(ic) + "]")
                    : "null";
                if (isPolar)
                    sb.AppendFormat(ic, "{{\"c\":[{0},{1}],\"sp\":{2},\"cs\":\"{3}\",\"f\":\"{4}\",\"m\":\"{5}\",\"k\":\"{6}\"}}",
                        s.Lon, s.Lat, spStr, callsign, freq, mode, color);
                else
                    sb.AppendFormat(ic, "{{\"c\":[{0},{1}],\"sp\":{2},\"cs\":\"{3}\",\"f\":\"{4}\",\"m\":\"{5}\",\"k\":\"{6}\"}}",
                        s.Lat, s.Lon, spStr, callsign, freq, mode, color);
            }
            sb.Append("]");
            try
            {
                MapBrowser.InvokeScript("updateClusterSpots", new object[] { sb.ToString() });
            }
            catch { }
        }

        public void ShowClusterSpots(System.Collections.Generic.IList<ClusterSpotInfo> spots, double homeLat, double homeLon, int radiusKm)
        {
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
                string spStr = (s.SpotterLat.HasValue && s.SpotterLon.HasValue)
                    ? "[" + s.SpotterLat.Value.ToString(ic) + "," + s.SpotterLon.Value.ToString(ic) + "]"
                    : "null";
                spotsJs.AppendFormat(ic, "{{\"c\":[{0},{1}],\"sp\":{2},\"cs\":\"{3}\",\"f\":\"{4}\",\"m\":\"{5}\",\"k\":\"{6}\"}}",
                    s.Lat, s.Lon, spStr, callsign, freq, mode, color);
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
var clusterSpots = " + spotsJs.ToString() + @";
var map = L.map('map', { zoomControl:false, attributionControl:false, zoomSnap:0 }).setView([homeLat, homeLon], 4);
L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', { maxZoom:18 }).addTo(map);
// Equator
L.polyline([[0,-180],[0,-90],[0,0],[0,90],[0,180]], { color:'#000000', weight:1.2, opacity:0.5, interactive:false }).addTo(map);
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
function renderSpots() {
    spotsLayer.clearLayers();
    for (var i = 0; i < clusterSpots.length; i++) {
        var sp = clusterSpots[i];
        // Great circle line spotter -> DX
        if (sp.sp) {
            var arcPts = gcArcPoints(sp.sp[0], sp.sp[1], sp.c[0], sp.c[1], 50);
            L.polyline(arcPts, {
                color: sp.k || '#FF6600', weight: 0.8, opacity: 0.7, interactive: false
            }).addTo(spotsLayer);
        }
        // Spotter dot (black)
        if (sp.sp) {
            L.circleMarker(sp.sp, {
                radius: 2, color: '#000000', fillColor: '#000000', fillOpacity: 1,
                weight: 0, interactive: false
            }).addTo(spotsLayer);
        }
        // Band-colored DX dot with tooltip and click
        var dotColor = sp.k || '#FF6600';
        var m = L.circleMarker(sp.c, {
            radius: 5, color: dotColor, fillColor: dotColor, fillOpacity: 1,
            weight: 0, interactive: true
        });
        m.bindTooltip('<b>' + sp.cs + '</b><br/>' + sp.f + '<span style=""font-size:9px;font-weight:normal""> MHz</span>&nbsp;' + sp.m, {
            permanent: false, sticky: true, direction: 'top',
            className: 'spot-tip'
        });
        (function(freq, mode) {
            m.on('click', function() {
                try { window.external.TuneToSpot(freq, mode); } catch(e) {}
            });
        })(sp.f, sp.m);
        m.addTo(spotsLayer);
    }
}
renderSpots();
map.fitBounds(radiusCircle.getBounds(), { padding:[2,2] });
function updateClusterSpots(json) {
    try { clusterSpots = JSON.parse(json); } catch(e) { return; }
    renderSpots();
}
function onRadiusChange(km) {
    radiusMeters = km * 1000;
    radiusCircle.setRadius(radiusMeters);
    map.fitBounds(radiusCircle.getBounds(), { padding:[2,2] });
    try { window.external.SetRadius(km); } catch(e) {}
}
function recenter() { map.fitBounds(radiusCircle.getBounds(), { padding:[2,2] }); }
function centerOnDx() { map.setView([homeLat, homeLon], map.getZoom()); }
function toggleProjection() { try { window.external.ToggleProjection(); } catch(e) {} }
window.addEventListener('resize', function() { if (map) { map.invalidateSize(); } });
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
                // polar projection expects [lon, lat]
                string spStr = (s.SpotterLat.HasValue && s.SpotterLon.HasValue)
                    ? "[" + s.SpotterLon.Value.ToString(ic) + "," + s.SpotterLat.Value.ToString(ic) + "]"
                    : "null";
                spotsJs.AppendFormat(ic, "{{\"c\":[{0},{1}],\"sp\":{2},\"cs\":\"{3}\",\"f\":\"{4}\",\"m\":\"{5}\",\"k\":\"{6}\"}}",
                    s.Lon, s.Lat, spStr, callsign, freq, mode, color);
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
  html, body { width:100%; height:100%; overflow:hidden; background:#1a2a3a; }
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
  #radius-stack { display:flex; flex-direction:column; align-items:center; }
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
  #distance-stack {
    position:absolute; right:0; bottom:0; z-index:1000;
    display:flex; flex-direction:column; align-items:center;
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
    <button id='center-btn' onclick='recenter()' title='Re-center map'><span class='de-label'>DE</span><svg viewBox='0 0 24 24' fill='none' stroke='currentColor' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><circle cx='12' cy='12' r='3'/><line x1='12' y1='2' x2='12' y2='6'/><line x1='12' y1='18' x2='12' y2='22'/><line x1='2' y1='12' x2='6' y2='12'/><line x1='18' y1='12' x2='22' y2='12'/></svg></button>
    <select id='radius-ctrl' onchange='onRadiusChange(this.value)'>" + options.ToString() + @"</select>
    <div id='radius-label'>-- km</div>
  </div>
</div>
<div id='distance-stack'>
  <button id='dx-center-btn' onclick='recenter()' title='Re-center map'><span class='dx-label'>DX</span><svg viewBox='0 0 24 24' fill='none' stroke='currentColor' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><circle cx='12' cy='12' r='3'/><line x1='12' y1='2' x2='12' y2='6'/><line x1='12' y1='18' x2='12' y2='22'/><line x1='2' y1='12' x2='6' y2='12'/><line x1='18' y1='12' x2='22' y2='12'/></svg></button>
  <div id='distance-box'>DIST --</div>
</div>
<script src='https://d3js.org/d3.v5.min.js'></script>
<script src='https://cdn.jsdelivr.net/npm/topojson-client@3/dist/topojson-client.min.js'></script>
<script>
window.onerror = function() { return true; };
var centerLat = " + homeLatJs + @";
var centerLon = " + homeLonJs + @";
var radiusKm = " + radiusKm.ToString() + @";
var marginMultiplier = " + marginJs + @";
var useMiles = " + useMilesJs + @";
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

svg.append('circle').attr('cx', cx).attr('cy', cy).attr('r', mapR)
    .attr('fill', '#4a90c4').attr('stroke', '#1a4060').attr('stroke-width', 2);
var countriesG = svg.append('g').attr('clip-path', 'url(#globe-clip)');
svg.append('path').datum(d3.geoGraticule().step([30, 30])())
    .attr('fill', 'none').attr('stroke', 'rgba(255,255,255,0.15)').attr('stroke-width', 0.7)
    .attr('d', path).attr('clip-path', 'url(#globe-clip)');
// Equator
svg.append('path').datum({type:'LineString', coordinates:[[-180,0],[-90,0],[0,0],[90,0],[180,0]]})
    .attr('fill','none').attr('stroke','rgba(255,255,255,0.7)').attr('stroke-width',1.2)
    .attr('d',path).attr('clip-path','url(#globe-clip)');
var ringsG = svg.append('g');
function drawRings() {
    ringsG.selectAll('*').remove();
    for (var i = 1; i <= 5; i++) {
        var km = Math.round((radiusKm * i) / 5);
        var ang = km / EARTH_KM;
        if (ang >= Math.PI) continue;
        var r = projection.scale() * ang;
        ringsG.append('circle').attr('cx', cx).attr('cy', cy).attr('r', r)
            .attr('fill', 'none').attr('stroke', 'rgba(255,255,255,0.18)')
            .attr('stroke-width', 1).attr('stroke-dasharray', '4,3');
        var lbl = useMiles ? (Math.round(km * 0.621371) + ' mi') : (km + ' km');
        ringsG.append('text').attr('x', cx + 3).attr('y', cy - r - 2)
            .attr('fill', 'rgba(255,255,255,0.4)').attr('font-size', '9px').text(lbl);
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
var overlaysG = svg.append('g');
var tooltip = d3.select('body').append('div')
    .style('position','absolute').style('pointer-events','none')
    .style('background','rgba(255,255,255,0.95)').style('border','1px solid #aaa')
    .style('border-radius','4px').style('padding','3px 7px')
    .style('font-size','12px').style('font-family','sans-serif')
    .style('color','#222').style('display','none').style('z-index','9999');
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
    // Cluster spots: lines, spotter dots, DX dots
    for (var i = 0; i < clusterSpots.length; i++) {
        try {
            var sp = clusterSpots[i];
            var pt = projection(sp.c);
            var spt = sp.sp ? projection(sp.sp) : null;
            // Great circle line spotter -> DX (D3 geoPath draws it curved automatically)
            if (sp.sp) {
                try {
                    var gcLine = { type: 'LineString', coordinates: [sp.sp, sp.c] };
                    overlaysG.append('path')
                        .datum(gcLine)
                        .attr('d', path)
                        .attr('fill', 'none')
                        .attr('stroke', sp.k || '#FF6600').attr('stroke-width', 0.8).attr('opacity', 0.6)
                        .attr('clip-path', 'url(#globe-clip)');
                } catch(el) {}
            }
            // Spotter dot (black)
            if (spt && isFinite(spt[0]) && isFinite(spt[1])) {
                overlaysG.append('circle')
                    .attr('cx', spt[0]).attr('cy', spt[1]).attr('r', 2)
                    .attr('fill', '#000000').attr('stroke', 'none')
                    .attr('clip-path', 'url(#globe-clip)');
            }
            // Band-colored DX dot with tooltip and click
            if (pt && isFinite(pt[0]) && isFinite(pt[1])) {
                (function(spot, px, py) {
                    var dotColor = spot.k || '#FF6600';
                    overlaysG.append('circle')
                        .attr('cx', px).attr('cy', py).attr('r', 4)
                        .attr('fill', dotColor).attr('stroke', 'none')
                        .attr('clip-path', 'url(#globe-clip)')
                        .style('cursor', 'pointer')
                        .on('mouseover', function() {
                            tooltip.style('display','block')
                                .html('<b>' + spot.cs + '</b><br/>' + spot.f + '<span style=""font-size:9px;font-weight:normal""> MHz</span>&nbsp;' + spot.m);
                        })
                        .on('mousemove', function() {
                            tooltip.style('left', (d3.event.pageX + 10) + 'px')
                                   .style('top',  (d3.event.pageY - 28) + 'px');
                        })
                        .on('mouseout', function() { tooltip.style('display','none'); })
                        .on('click', function() {
                            try { window.external.TuneToSpot(spot.f, spot.m); } catch(e2) {}
                        });
                })(sp, pt[0], pt[1]);
            }
        } catch(es) {}
    }
    // Outer border
    overlaysG.append('circle').attr('cx', cx).attr('cy', cy).attr('r', mapR)
        .attr('fill', 'none').attr('stroke', '#2a607a').attr('stroke-width', 2);
}
drawOverlays();
if (autoZoomActive) applyAutoZoom();

var xhr = new XMLHttpRequest();
xhr.open('GET', 'https://cdn.jsdelivr.net/npm/world-atlas@2/countries-110m.json', true);
xhr.onreadystatechange = function() {
    if (xhr.readyState !== 4) return;
    if (xhr.status === 200) {
        try {
            var world = JSON.parse(xhr.responseText);
            countriesG.selectAll('path').data(topojson.feature(world, world.objects.countries).features)
                .enter().append('path').attr('d', path)
                .attr('fill', '#5a8a6a').attr('stroke', '#2a4a3a').attr('stroke-width', 0.5);
        } catch(e) {}
    }
    drawOverlays();
    drawRadiusRing(radiusKm);
    if (autoZoomActive) applyAutoZoom();
};
xhr.onerror = function() { drawOverlays(); if (autoZoomActive) applyAutoZoom(); };
try { xhr.send(); } catch(e) { drawOverlays(); if (autoZoomActive) applyAutoZoom(); }

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
    drawRings(); drawRadiusRing(radiusKm); drawOverlays();
}
function toggleProjection() { try { window.external.ToggleProjection(); } catch(e) {} }
function updateClusterSpots(json) {
    try { clusterSpots = JSON.parse(json); } catch(e) { return; }
    drawOverlays();
    if (autoZoomActive) applyAutoZoom();
}
function haversineKm(lat1, lon1, lat2, lon2) {
    var R = 6371, toR = Math.PI/180;
    var dLat = (lat2-lat1)*toR, dLon = (lon2-lon1)*toR;
    var a = Math.sin(dLat/2)*Math.sin(dLat/2) +
            Math.cos(lat1*toR)*Math.cos(lat2*toR)*Math.sin(dLon/2)*Math.sin(dLon/2);
    return R * 2 * Math.atan2(Math.sqrt(a), Math.sqrt(1-a));
}
function applyAutoZoom() {
    if (!clusterSpots || clusterSpots.length === 0) return;
    // Helper: validate a [lon,lat] pair and push its distance from home.
    function pushDist(arr, distances) {
        if (!arr || arr.length !== 2) return;
        var lon = arr[0], lat = arr[1];
        if (!isFinite(lon) || !isFinite(lat)) return;
        if (lon < -180 || lon > 180 || lat < -90 || lat > 90) return;
        var km = haversineKm(centerLat, centerLon, lat, lon);
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
        // Sample 10 intermediate points along the spotter→DX arc so that arcs
        // bowing outward on the azimuthal projection are also accounted for.
        if (sp.sp && sp.c) pushArcDists(sp.sp, sp.c, 10, distances);
    }
    if (distances.length === 0) return;
    // Radius = farthest station (DX, spotter, or arc midpoint) + 10% padding.
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
        drawRings(); drawRadiusRing(radiusKm); drawOverlays();
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
    drawRings(); drawRadiusRing(radiusKm); drawOverlays();
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
        drawRings(); drawRadiusRing(radiusKm); drawOverlays();
    })
);
window.addEventListener('resize', function() {
    W = window.innerWidth; H = window.innerHeight;
    mapR = Math.floor((Math.min(W, H) / 2) - 4);
    cx = W / 2; cy = H / 2;
    svg.attr('width', W).attr('height', H);
    defs.select('clipPath circle').attr('cx', cx).attr('cy', cy).attr('r', mapR - 1);
    projection.translate([cx, cy]);
    baseScale = mapR / Math.PI;
    scaleToRadius();
    countriesG.selectAll('path').attr('d', path);
    svg.selectAll('.graticule-path').attr('d', path);
    drawRings(); drawRadiusRing(radiusKm); drawOverlays();
});
</script>
</body>
</html>";

            File.WriteAllText(_tempMapFile, html, System.Text.Encoding.UTF8);
            var uriBuilder = new UriBuilder(new Uri(_tempMapFile));
            uriBuilder.Query = "v=" + DateTime.UtcNow.Ticks.ToString();
            MapBrowser.Navigate(uriBuilder.Uri);
        }

        public void ShowMap(double lat, double lon, int radiusKm, double? azimuthDeg = null, double? homeLat = null, double? homeLon = null)
        {
            _isClusterMode = false;
            _currentLat = lat;
            _currentLon = lon;
            _currentRadiusKm = radiusKm;
            _currentAzimuth = azimuthDeg;
            _currentHomeLat = homeLat;
            _currentHomeLon = homeLon;
            PlaceholderPanel.Visibility = System.Windows.Visibility.Collapsed;
            MapBrowser.Visibility = System.Windows.Visibility.Visible;
            RenderMap();
        }

          public void RefreshMap()
          {
            if (MapBrowser.Visibility == System.Windows.Visibility.Visible)
            {
              RenderMap();
            }
          }

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

            string html = _isPolar
                ? BuildPolarMapHtml(_currentLat, _currentLon, _currentRadiusKm, _currentAzimuth, _currentHomeLat, _currentHomeLon, marginMultiplier)
                : BuildFlatMapHtml(_currentLat, _currentLon, _currentRadiusKm, _currentAzimuth, _currentHomeLat, _currentHomeLon, marginMultiplier);
            File.WriteAllText(_tempMapFile, html, System.Text.Encoding.UTF8);
            var uriBuilder = new UriBuilder(new Uri(_tempMapFile));
            uriBuilder.Query = "v=" + DateTime.UtcNow.Ticks.ToString();
            MapBrowser.Navigate(uriBuilder.Uri);
        }

        public void ClearMap()
        {
            MapBrowser.Visibility = System.Windows.Visibility.Collapsed;
            PlaceholderPanel.Visibility = System.Windows.Visibility.Visible;
        }

        public void ShowPlaceholder(string message)
        {
            PlaceholderText.Text = System.Net.WebUtility.HtmlDecode(message);
            MapBrowser.Visibility = System.Windows.Visibility.Collapsed;
            PlaceholderPanel.Visibility = System.Windows.Visibility.Visible;
        }

        private string BuildFlatMapHtml(double lat, double lon, int radiusKm, double? azimuthDeg, double? homeLat = null, double? homeLon = null, double marginMultiplier = 1.15)
        {
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
  html, body { width:100%; height:100%; margin:0; padding:0; overflow:hidden; }
  #map { width:100%; height:100%; }
  #compass-ctrl {
    position:absolute; top:0; left:0; z-index:1000;
    display:flex; flex-direction:column; align-items:center;
    background:transparent;
    border:none; padding:2px 2px 1px 2px;
    border-radius:0; font-family:Segoe UI, Tahoma, sans-serif;
    box-shadow:none;
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
    letter-spacing:0.3px;
    background:rgba(255,255,255,0.88);
    border:1px solid rgba(36,77,80,0.45);
    border-radius:10px;
    padding:2px 7px;
  }
  #bottom-ctrl {
    position:absolute; bottom:0; left:0; z-index:1000;
    display:flex; align-items:flex-end;
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
    height:24px; display:flex; align-items:center; justify-content:center;
    font-size:11px; font-weight:700; font-family:sans-serif; color:#333;
  }
  #proj-btn:hover { background:#8CBDF0; }
  #distance-stack {
    position:absolute; right:0; bottom:0; z-index:1000;
    display:flex; flex-direction:column; align-items:center;
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

        private string BuildPolarMapHtml(double dxLat, double dxLon, int radiusKm, double? azimuthDeg, double? homeLat = null, double? homeLon = null, double marginMultiplier = 1.15)
        {
            // Center on home QTH; fall back to DX if no home available.
            double centerLat = homeLat.HasValue ? homeLat.Value : dxLat;
            double centerLon = homeLon.HasValue ? homeLon.Value : dxLon;
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
  html, body { width:100%; height:100%; overflow:hidden; background:#1a2a3a; }
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
    display:flex; flex-direction:column; align-items:center;
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
    display:flex; flex-direction:column; align-items:center;
  }
  #radius-ctrl {
    background:rgba(255,255,255,0.88); border:1px solid #aaa;
    border-radius:0;
    padding:2px 4px; font-size:13px; font-family:sans-serif; cursor:pointer;
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
</style>
</head>
<body>
<svg id='polar-svg'></svg>
<button id='proj-btn' onclick='toggleProjection()'>&#9974; Flat</button>
<div id='az-only'>AZ 0&deg;</div>
<div id='bottom-ctrl'>
  <div id='radius-stack'>
    <button id='center-btn' onclick='recenter()' title='Reset zoom to selected radius'><span class='de-label'>DE</span><svg viewBox='0 0 24 24' fill='none' stroke='currentColor' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><circle cx='12' cy='12' r='3'/><line x1='12' y1='2' x2='12' y2='6'/><line x1='12' y1='18' x2='12' y2='22'/><line x1='2' y1='12' x2='6' y2='12'/><line x1='18' y1='12' x2='22' y2='12'/></svg></button>
    <select id='radius-ctrl' onchange='onRadiusChange(this.value)'>" + options.ToString() + @"</select>
  </div>
</div>
<div id='distance-stack'>
  <button id='dx-center-btn' onclick='centerOnDx()' title='Center on DX station'><span class='dx-label'>DX</span><svg viewBox='0 0 24 24' fill='none' stroke='currentColor' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><circle cx='12' cy='12' r='3'/><line x1='12' y1='2' x2='12' y2='6'/><line x1='12' y1='18' x2='12' y2='22'/><line x1='2' y1='12' x2='6' y2='12'/><line x1='18' y1='12' x2='22' y2='12'/></svg></button>
  <div id='distance-box'>DIST --</div>
</div>
<script src='https://d3js.org/d3.v5.min.js'></script>
<script src='https://cdn.jsdelivr.net/npm/topojson-client@3/dist/topojson-client.min.js'></script>
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
document.getElementById('az-only').innerHTML = 'AZ ' + Math.round(azimuthDeg) + '&deg;';
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
svg.append('circle').attr('class', 'ocean-fill')
    .attr('cx', cx).attr('cy', cy).attr('r', mapR)
    .attr('fill', '#4a90c4').attr('stroke', '#1a4060').attr('stroke-width', 2);

// Layer for countries (inserted before overlays)
var countriesG = svg.append('g').attr('clip-path', 'url(#globe-clip)');

// Graticule
svg.append('path')
    .datum(d3.geoGraticule().step([30, 30])())
  .attr('class', 'graticule-path')
    .attr('d', path)
    .attr('fill', 'none').attr('stroke', 'rgba(255,255,255,0.15)').attr('stroke-width', 0.7)
    .attr('clip-path', 'url(#globe-clip)');
// Equator
svg.append('path').datum({type:'LineString', coordinates:[[-180,0],[-90,0],[0,0],[90,0],[180,0]]})
    .attr('class', 'graticule-path')
    .attr('fill','none').attr('stroke','rgba(255,255,255,0.7)').attr('stroke-width',1.2)
    .attr('d',path).attr('clip-path','url(#globe-clip)');

// Distance rings layer (above countries)
var ringsG = svg.append('g');
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
            .attr('fill', 'none').attr('stroke', 'rgba(255,255,255,0.18)')
            .attr('stroke-width', 1).attr('stroke-dasharray', '4,3');
        var ringLabel = useMiles ? (Math.round(km * 0.621371) + ' mi') : (km + ' km');
        ringsG.append('text').attr('x', cx + 3).attr('y', cy - r - 2)
          .attr('fill', 'rgba(255,255,255,0.4)').attr('font-size', '9px').text(ringLabel);
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
var overlaysG = svg.append('g');
function drawOverlays() {
    overlaysG.selectAll('*').remove();

    // Great-circle line home -> DX
    try {
        var gcLine = { type: 'LineString', coordinates: [[centerLon, centerLat], [dxLon, dxLat]] };
        overlaysG.append('path').datum(gcLine).attr('d', path)
            .attr('fill', 'none').attr('stroke', '#90CAF9').attr('stroke-width', 2.5)
            .attr('stroke-dasharray', '7,4').attr('clip-path', 'url(#globe-clip)');
    } catch(e2) {}

    // Home dot (projected): may move away from center while dragging view
    try {
        var homePt = projection([centerLon, centerLat]);
        if (homePt && isFinite(homePt[0]) && isFinite(homePt[1])) {
            overlaysG.append('circle')
              .attr('cx', homePt[0]).attr('cy', homePt[1]).attr('r', 5)
              .attr('fill', '#1565C0').attr('stroke', 'none');
        }
    } catch(eHome) {}

    // DX dot
    try {
        var dxPt = projection([dxLon, dxLat]);
        if (dxPt && isFinite(dxPt[0]) && isFinite(dxPt[1])) {
            overlaysG.append('circle')
            .attr('cx', dxPt[0]).attr('cy', dxPt[1]).attr('r', 4)
            .attr('fill', '#E53935').attr('stroke', 'none');
        }
    } catch(e3) {}

    // Outer border ring
    overlaysG.append('circle').attr('cx', cx).attr('cy', cy).attr('r', mapR)
        .attr('fill', 'none').attr('stroke', '#2a607a').attr('stroke-width', 2);
}
drawOverlays();

// Load world countries via XHR (IE11-safe, no Promise)
var xhr = new XMLHttpRequest();
xhr.open('GET', 'https://cdn.jsdelivr.net/npm/world-atlas@2/countries-110m.json', true);
xhr.onreadystatechange = function() {
    if (xhr.readyState !== 4) return;
    if (xhr.status === 200) {
        try {
            var world = JSON.parse(xhr.responseText);
            var features = topojson.feature(world, world.objects.countries).features;
            countriesG.selectAll('path').data(features).enter().append('path')
                .attr('d', path)
                .attr('fill', '#5a8a6a').attr('stroke', '#2a4a3a').attr('stroke-width', 0.5);
        } catch(e4) {}
    }
    // Always redraw overlays on top after countries attempt
    drawOverlays();
    drawRadiusRing(radiusKm);
    // If auto zoom was restored from settings, apply it now that all functions are ready
    if (autoZoomActive) applyAutoZoom();
};
xhr.onerror = function() { drawOverlays(); if (autoZoomActive) applyAutoZoom(); };
try { xhr.send(); } catch(e5) { drawOverlays(); if (autoZoomActive) applyAutoZoom(); }

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
    
    // Update ocean fill background
    svg.select('.ocean-fill').attr('cx', cx).attr('cy', cy).attr('r', mapR);
    
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
    }
}
