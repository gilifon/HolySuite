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
    background:linear-gradient(180deg, rgba(255,255,255,0.95), rgba(238,243,247,0.92));
    border:1px solid #708090; padding:4px 5px 2px 5px;
    border-radius:0; font-family:Segoe UI, Tahoma, sans-serif;
    box-shadow:0 1px 4px rgba(0,0,0,0.25);
  }
  #compass-ring {
    width:52px; height:52px; border:2px solid #3c4a57; border-radius:50%;
    position:relative; background:radial-gradient(circle, rgba(255,255,255,0.98) 0%, rgba(220,228,236,0.95) 70%, rgba(200,212,224,0.95) 100%);
    overflow:hidden;
  }
  .cardinal {
    position:absolute; font-size:9px; font-weight:700; color:#1f2d3a;
    text-shadow:0 1px 0 rgba(255,255,255,0.8);
    user-select:none;
  }
  #card-n { top:2px; left:50%; transform:translateX(-50%); color:#b71c1c; }
  #card-e { right:2px; top:50%; transform:translateY(-50%); }
  #card-s { bottom:2px; left:50%; transform:translateX(-50%); }
  #card-w { left:2px; top:50%; transform:translateY(-50%); }
  #compass-needle {
    position:absolute; left:50%; top:50%; width:2px; height:18px;
    transform-origin:50% 95%; transform:translate(-50%, -95%) rotate(0deg);
    background:#c62828;
    box-shadow:0 0 2px rgba(0,0,0,0.4);
  }
  #compass-needle:before {
    content:''; position:absolute; top:-8px; left:-4px;
    border-left:5px solid transparent; border-right:5px solid transparent;
    border-bottom:9px solid #c62828;
  }
  #compass-center {
    position:absolute; width:6px; height:6px; border-radius:50%;
    background:#263238; left:50%; top:50%; transform:translate(-50%, -50%);
  }
  #compass-text {
    margin-top:2px; font-size:11px; font-weight:700; color:#1f2d3a;
    letter-spacing:0.3px;
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
    <div id='card-n' class='cardinal'>N</div>
    <div id='card-e' class='cardinal'>E</div>
    <div id='card-s' class='cardinal'>S</div>
    <div id='card-w' class='cardinal'>W</div>
    <div id='compass-needle'></div>
    <div id='compass-center'></div>
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
var homeLat = " + latStr + @", homeLon = " + lonStr + @";
var azimuthDeg = " + azimuthJs + @";
var map = L.map('map', { zoomControl: false, attributionControl: false }).setView([homeLat, homeLon], 8);
L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', { maxZoom: 18 }).addTo(map);
L.marker([homeLat, homeLon]).addTo(map);
var rangeCircle = L.circle([homeLat, homeLon], { radius: " + radiusMeters + @", color: '#E53935', fill: false, weight: 2 }).addTo(map);
map.fitBounds(rangeCircle.getBounds(), { padding: [10, 10] });

document.getElementById('compass-text').innerHTML = 'AZ ' + Math.round(azimuthDeg) + '&deg;';
document.getElementById('compass-needle').style.transform = 'translate(-50%, -95%) rotate(' + azimuthDeg + 'deg)';

function onRadiusChange(km) {
    try { window.external.SetRadius(km); } catch(e) {}
}
function recenter() {
    map.fitBounds(rangeCircle.getBounds(), { padding: [10, 10] });
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
