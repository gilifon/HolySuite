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
    }

    public partial class MapUserControl : UserControl
    {
        private readonly string _tempMapFile;
        private int _currentRadiusKm = 1000;
        private double _currentLat, _currentLon;

        public event Action<int> RadiusChanged;

        internal void RaiseRadiusChanged(int km) => RadiusChanged?.Invoke(km);

        public MapUserControl()
        {
            InitializeComponent();
            _tempMapFile = Path.Combine(Path.GetTempPath(), "holylogger_map.html");
            MapBrowser.ObjectForScripting = new MapScriptHelper(this);
            MapBrowser.LoadCompleted += (s, e) => SuppressScriptErrors();
        }

        public void ShowMap(double lat, double lon, int radiusKm, double? azimuthDeg = null)
        {
            _currentLat = lat;
            _currentLon = lon;
            _currentRadiusKm = radiusKm;
            PlaceholderPanel.Visibility = System.Windows.Visibility.Collapsed;
          string html = BuildMapHtml(lat, lon, radiusKm, azimuthDeg);
            File.WriteAllText(_tempMapFile, html, System.Text.Encoding.UTF8);
            var uriBuilder = new UriBuilder(new Uri(_tempMapFile));
            uriBuilder.Query = "v=" + DateTime.UtcNow.Ticks.ToString();
            MapBrowser.Navigate(uriBuilder.Uri);
        }

        public void ClearMap()
        {
            PlaceholderPanel.Visibility = System.Windows.Visibility.Visible;
            MapBrowser.NavigateToString("<html><body style='background:#E3F2FD;margin:0'></body></html>");
        }

        private string BuildMapHtml(double lat, double lon, int radiusKm, double? azimuthDeg)
        {
            string latStr = lat.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string lonStr = lon.ToString(System.Globalization.CultureInfo.InvariantCulture);
            int radiusMeters = radiusKm * 1000;
            int[] radiiOptions = { 100, 250, 500, 1000, 2000, 5000 };
          string azimuthJs = "0";
          if (azimuthDeg.HasValue)
          {
            double normalizedAzimuth = azimuthDeg.Value % 360;
            if (normalizedAzimuth < 0)
              normalizedAzimuth += 360;
            azimuthJs = normalizedAzimuth.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
          }
            var options = new System.Text.StringBuilder();
            foreach (int r in radiiOptions)
                options.AppendFormat("<option value='{0}'{1}>{0} km</option>", r, r == radiusKm ? " selected" : "");

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
    border-radius:0; padding:0 6px; cursor:pointer;
    font-size:16px; line-height:1; display:flex; align-items:center; justify-content:center;
    color:#333;
  }
  #center-btn:hover { background:rgba(220,220,255,0.95); }
  #center-btn svg { width:18px; height:18px; }
</style>
<link rel='stylesheet' href='https://unpkg.com/leaflet@1.9.4/dist/leaflet.css'/>
</head>
<body>
<div id='map'></div>
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
<script src='https://unpkg.com/leaflet@1.9.4/dist/leaflet.js'></script>
<script>
window.onerror = function() { return true; };
var homeLat = " + latStr + @", homeLon = " + lonStr + @";
var azimuthDeg = " + azimuthJs + @";
var radiusMeters = " + radiusMeters + @";
var map = L.map('map', { zoomControl: false, attributionControl: false }).setView([homeLat, homeLon], 5);
L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', { maxZoom: 18 }).addTo(map);
L.marker([homeLat, homeLon]).addTo(map);

// Create visible red circle to show the search radius
var radiusCircle = L.circle([homeLat, homeLon], { radius: radiusMeters, color: '#E53935', fill: false, weight: 2 }).addTo(map);
map.fitBounds(radiusCircle.getBounds(), { padding: [20, 20] });

document.getElementById('compass-text').innerHTML = 'AZ ' + Math.round(azimuthDeg) + '&deg;';
document.getElementById('compass-needle').setAttribute('transform', 'rotate(' + azimuthDeg + ' 50 50)');

function onRadiusChange(km) {
    radiusMeters = km * 1000; // Convert km to meters and update immediately
    radiusCircle.setRadius(radiusMeters);
    map.fitBounds(radiusCircle.getBounds(), { padding: [20, 20] });
    try { window.external.SetRadius(km); } catch(e) {}
}
function recenter() {
    radiusCircle.setRadius(radiusMeters);
    map.fitBounds(radiusCircle.getBounds(), { padding: [20, 20] });
}
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
