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
                // Hide all graphics options
                MapControl.Visibility = Visibility.Hidden;
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

                    // Don't update map if Empty mode is active
                    if (Properties.Settings.Default.MapAreaDisplayMode != 4)
                    {
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
    }
}
