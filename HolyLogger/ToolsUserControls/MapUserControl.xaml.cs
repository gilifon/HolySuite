using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Controls;

namespace HolyLogger.ToolsUserControls
{
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
    }

    public partial class MapUserControl : UserControl
    {
        private readonly string _tempMapFile;
        private int _currentRadiusKm = 1000;
        private double _currentLat, _currentLon;
        private double? _currentAzimuth, _currentHomeLat, _currentHomeLon;
        private bool _isPolar;

        public event Action<int> RadiusChanged;

        internal void RaiseRadiusChanged(int km) => RadiusChanged?.Invoke(km);

        internal void ToggleProjection()
        {
            _isPolar = !_isPolar;
          Properties.Settings.Default.MapUsePolar = _isPolar;
          Properties.Settings.Default.Save();
            RenderMap();
        }

        public MapUserControl()
        {
            InitializeComponent();
          _isPolar = Properties.Settings.Default.MapUsePolar;
            _tempMapFile = Path.Combine(Path.GetTempPath(), "holylogger_map.html");
            MapBrowser.ObjectForScripting = new MapScriptHelper(this);
            MapBrowser.LoadCompleted += (s, e) => SuppressScriptErrors();
        }

        public void ShowMap(double lat, double lon, int radiusKm, double? azimuthDeg = null, double? homeLat = null, double? homeLon = null)
        {
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
            int[] radiiOptions = { 100, 250, 500, 1000, 2000, 3500, 5000, 7500, 10000, 20000 };
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
    display:flex; align-items:stretch;
  }
  #radius-ctrl {
    background:rgba(255,255,255,0.88); border:1px solid #aaa;
    border-right:none; border-radius:0;
    padding:2px 4px; font-size:13px; font-family:sans-serif; cursor:pointer;
    height:100%;
  }
  #center-btn {
    background:rgba(255,255,255,0.88); border:1px solid #aaa;
    border-radius:10px; padding:0 4px; cursor:pointer;
    display:flex; align-items:center; justify-content:center;
    color:#333;
  }
  #center-btn:hover { background:rgba(220,220,255,0.95); }
  #center-btn svg { width:18px; height:18px; }
  #proj-btn {
    position:absolute; top:0; right:0; z-index:1000;
    background:rgba(255,255,255,0.88); border:1px solid #aaa;
    border-radius:0; padding:3px 7px; cursor:pointer;
    font-size:12px; font-weight:700; font-family:sans-serif; color:#333;
  }
  #proj-btn:hover { background:rgba(220,240,255,0.95); }
  #distance-box {
    position:absolute; bottom:0; right:0; z-index:1000;
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
  <select id='radius-ctrl' onchange='onRadiusChange(this.value)'>" + options.ToString() + @"</select>
  <button id='center-btn' onclick='recenter()' title='Re-center map'>
    <svg viewBox='0 0 24 24' fill='none' stroke='currentColor' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'>
      <circle cx='12' cy='12' r='3'/>
      <line x1='12' y1='2' x2='12' y2='6'/>
      <line x1='12' y1='18' x2='12' y2='22'/>
      <line x1='2' y1='12' x2='6' y2='12'/>
      <line x1='18' y1='12' x2='22' y2='12'/>
    </svg>
  </button>
</div>
<div id='distance-box'>DIST --</div>
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
// Center map and radius circle on operator home; fall back to DX if home unknown
var circleLat = (operatorLat !== null) ? operatorLat : dxLat;
var circleLon = (operatorLon !== null) ? operatorLon : dxLon;
var map = L.map('map', { zoomControl: false, attributionControl: false, zoomSnap: 0 }).setView([circleLat, circleLon], 5);
L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', { maxZoom: 18 }).addTo(map);

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
function toggleProjection() {
    try { window.external.ToggleProjection(); } catch(e) {}
}
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
            int[] radiiOptions = { 100, 250, 500, 1000, 2000, 3500, 5000, 7500, 10000, 20000 };
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
    background:rgba(255,255,255,0.88); border:1px solid #aaa;
    padding:3px 7px; cursor:pointer;
    font-size:12px; font-weight:700; font-family:sans-serif; color:#333;
  }
  #az-only {
    position:absolute; top:-1px; left:-1px; z-index:1000;
    font-size:14px; font-weight:700; color:#e0f0ff;
    background:rgba(0,0,0,0.55); border-radius:0; padding:2px 6px;
    font-family:sans-serif;
  }
  #distance-box {
    position:absolute; bottom:0; right:0; z-index:1000;
    background:rgba(255,255,255,0.9); border:1px solid #aaa;
    border-radius:0; padding:3px 7px;
    font-size:13px; font-weight:700; font-family:sans-serif; color:#333;
    white-space:nowrap;
  }
  #bottom-ctrl {
    position:absolute; bottom:0; left:0; z-index:1000;
    display:flex; align-items:stretch;
  }
  #radius-ctrl {
    background:rgba(255,255,255,0.88); border:1px solid #aaa;
    border-right:none; border-radius:0;
    padding:2px 4px; font-size:13px; font-family:sans-serif; cursor:pointer;
  }
  #center-btn {
    background:rgba(255,255,255,0.88); border:1px solid #aaa;
    border-radius:10px; padding:0 4px; cursor:pointer;
    display:flex; align-items:center; justify-content:center;
    color:#333;
  }
  #center-btn:hover { background:rgba(220,220,255,0.95); }
  #center-btn svg { width:18px; height:18px; }
</style>
</head>
<body>
<svg id='polar-svg'></svg>
<button id='proj-btn' onclick='toggleProjection()'>&#9974; Flat</button>
<div id='az-only'>AZ 0&deg;</div>
<div id='bottom-ctrl'>
  <select id='radius-ctrl' onchange='onRadiusChange(this.value)'>" + options.ToString() + @"</select><button id='center-btn' onclick='recenter()' title='Reset zoom to selected radius'><svg viewBox='0 0 24 24' fill='none' stroke='currentColor' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><circle cx='12' cy='12' r='3'/><line x1='12' y1='2' x2='12' y2='6'/><line x1='12' y1='18' x2='12' y2='22'/><line x1='2' y1='12' x2='6' y2='12'/><line x1='18' y1='12' x2='22' y2='12'/></svg></button>
</div>
<div id='distance-box'>DIST --</div>
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
scaleToRadius();

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
svg.append('circle')
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

    // Home dot at center
    overlaysG.append('circle').attr('cx', cx).attr('cy', cy).attr('r', 5)
      .attr('fill', '#1565C0').attr('stroke', 'none');

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
};
xhr.onerror = function() { drawOverlays(); };
try { xhr.send(); } catch(e5) { drawOverlays(); }

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

// Mouse-wheel zoom: scale projection up/down and redraw everything
document.addEventListener('wheel', function(e) {
    e.preventDefault();
    var factor = (e.deltaY < 0) ? 1.15 : (1 / 1.15);
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
