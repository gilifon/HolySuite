using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace HolyLogger
{
    // One home for the cluster's display/behaviour settings, opened from the gear in the Cluster
    // window's title bar. Previously these were scattered: two of them lived in a gear popup, the
    // hover-popup and map toggles in Options > User Interface, and the sound alerts in Options >
    // General. Settings that decide whether the cluster RUNS at all (Active / Visible) stay in
    // Options, as does the app-wide audio output device.
    public partial class ClusterSettingsWindow : Window
    {
        private readonly MainWindow _main;
        private bool _loading;

        // The named system sounds offered first, before the Windows\Media *.wav files. These are exactly
        // the names MainWindow.PlayClusterAlertSound maps -- anything else falls through to Chime.
        private static readonly string[] BuiltInSounds = { "Chime", "Beep", "Exclamation", "Question", "Critical" };

        public ClusterSettingsWindow(MainWindow main)
        {
            InitializeComponent();
            _main = main;
            Owner = main;

            RestoreBounds_();

            _loading = true;
            try
            {
                var s = Properties.Settings.Default;
                CBX_ShowLotw.IsChecked   = s.ClusterShowLotw;
                CBX_LotwOnly.IsChecked   = s.ClusterLotwOnly;
                CBX_PlotMap.IsChecked    = s.ClusterMapEnabled;
                CBX_HoverPopup.IsChecked = _main != null && _main.GetClusterHoverPopupEnabled();
                CBX_AutoFillDxCall.IsChecked = s.ClusterAutoFillDxCall;

                var sounds = BuildSoundList();
                CB_NewCountrySound.ItemsSource = sounds;
                CB_NewCountrySound.SelectedItem = PickSound(sounds, s.ClusterNewCountrySound);
                CBX_NewCountrySound.IsChecked = s.ClusterNewCountrySoundOn;

                CB_UnconfirmedSound.ItemsSource = new List<string>(sounds);
                CB_UnconfirmedSound.SelectedItem = PickSound(sounds, s.ClusterUnconfirmedSound);
                CBX_UnconfirmedSound.IsChecked = s.ClusterUnconfirmedSoundOn;
            }
            finally { _loading = false; }

            Closing += (a, b) => SaveBounds_();
        }

        // Named system sounds + every Windows\Media *.wav, same choices the Options page offered.
        private static List<string> BuildSoundList()
        {
            var sounds = new List<string>(BuiltInSounds);
            try
            {
                string mediaDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media");
                sounds.AddRange(Directory.GetFiles(mediaDir, "*.wav")
                                         .Select(Path.GetFileName)
                                         .OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }   // no Media folder -> system sounds only
            return sounds;
        }

        private static string PickSound(List<string> sounds, string saved)
            => sounds.Contains(saved, StringComparer.OrdinalIgnoreCase)
                ? sounds.First(n => string.Equals(n, saved, StringComparison.OrdinalIgnoreCase))
                : "Chime";

        // One handler for every checkbox: push each toggle straight through to the live cluster.
        private void Setting_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading || _main == null) return;
            var s = Properties.Settings.Default;

            if (sender == CBX_ShowLotw)
            {
                _main.SetClusterShowLotw(CBX_ShowLotw.IsChecked == true);
                // The two LoTW options are mutually exclusive: marking them is redundant when only
                // LoTW spots are shown, and vice versa.
                if (CBX_ShowLotw.IsChecked == true && CBX_LotwOnly.IsChecked == true)
                    CBX_LotwOnly.IsChecked = false;
            }
            else if (sender == CBX_LotwOnly)
            {
                _main.SetClusterLotwOnly(CBX_LotwOnly.IsChecked == true);
                if (CBX_LotwOnly.IsChecked == true && CBX_ShowLotw.IsChecked == true)
                    CBX_ShowLotw.IsChecked = false;
            }
            else if (sender == CBX_PlotMap)
            {
                s.ClusterMapEnabled = CBX_PlotMap.IsChecked == true;
                Save(s);
                _main.UpdateClusterMapFromSettings();
            }
            else if (sender == CBX_HoverPopup)
            {
                _main.SetClusterHoverPopupEnabled(CBX_HoverPopup.IsChecked == true);
            }
            else if (sender == CBX_AutoFillDxCall)
            {
                // Off = the DX box is only ever filled by double-clicking a spot, never by the radio
                // landing on a spotted frequency.
                s.ClusterAutoFillDxCall = CBX_AutoFillDxCall.IsChecked == true;
                Save(s);
            }
            else if (sender == CBX_NewCountrySound)
            {
                s.ClusterNewCountrySoundOn = CBX_NewCountrySound.IsChecked == true;
                Save(s);
            }
            else if (sender == CBX_UnconfirmedSound)
            {
                s.ClusterUnconfirmedSoundOn = CBX_UnconfirmedSound.IsChecked == true;
                Save(s);
            }
        }

        private void CB_NewCountrySound_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            if (CB_NewCountrySound.SelectedItem is string name)
            {
                Properties.Settings.Default.ClusterNewCountrySound = name;
                Save(Properties.Settings.Default);
            }
        }

        private void CB_UnconfirmedSound_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            if (CB_UnconfirmedSound.SelectedItem is string name)
            {
                Properties.Settings.Default.ClusterUnconfirmedSound = name;
                Save(Properties.Settings.Default);
            }
        }

        // Test buttons use the app-wide output device (chosen in Options > General > Sounds).
        private void BTN_TestNewCountry_Click(object sender, RoutedEventArgs e)
            => MainWindow.PlayClusterAlertSound(CB_NewCountrySound.SelectedItem as string);

        private void BTN_TestUnconfirmed_Click(object sender, RoutedEventArgs e)
            => MainWindow.PlayClusterAlertSound(CB_UnconfirmedSound.SelectedItem as string);

        // "Sounds" link: jump straight to Options > General, where the app-wide output device lives.
        private void SoundsLink_Click(object sender, RoutedEventArgs e)
        {
            _main?.OpenOptionsOnGeneralPage();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        private static void Save(Properties.Settings s)
        {
            try { s.Save(); } catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // ── window position / size persistence ────────────────────────────

        private void RestoreBounds_()
        {
            // Height is NOT restored: the window is SizeToContent="Height", so it always computes its own
            // exact height. Only the position and width are remembered.
            var s = Properties.Settings.Default;
            if (s.ClusterSettingsWindowWidth >= MinWidth) Width = s.ClusterSettingsWindowWidth;

            if (IsPositionOnScreen(s.ClusterSettingsWindowLeft, s.ClusterSettingsWindowTop))
            {
                Left = s.ClusterSettingsWindowLeft;
                Top  = s.ClusterSettingsWindowTop;
            }
            else
            {
                WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
        }

        private void SaveBounds_()
        {
            try
            {
                var b = WindowState == WindowState.Normal
                    ? new Rect(Left, Top, Width, Height)
                    : RestoreBounds;
                var s = Properties.Settings.Default;
                if (!double.IsNaN(b.Left) && !double.IsInfinity(b.Left) &&
                    !double.IsNaN(b.Top)  && !double.IsInfinity(b.Top))
                {
                    s.ClusterSettingsWindowLeft = b.Left;
                    s.ClusterSettingsWindowTop  = b.Top;
                }
                // Width only — the height is content-driven (SizeToContent), so saving it is pointless
                // and a stale saved value could reintroduce the old dead space.
                if (b.Width > 0) s.ClusterSettingsWindowWidth = b.Width;
                s.Save();
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // A saved spot on a monitor that's since been removed must not strand the window off-screen.
        private static bool IsPositionOnScreen(double left, double top)
        {
            if (double.IsNaN(left) || double.IsNaN(top) ||
                double.IsInfinity(left) || double.IsInfinity(top))
                return false;

            double vsLeft   = SystemParameters.VirtualScreenLeft;
            double vsTop    = SystemParameters.VirtualScreenTop;
            double vsRight  = vsLeft + SystemParameters.VirtualScreenWidth;
            double vsBottom = vsTop  + SystemParameters.VirtualScreenHeight;

            return left >= vsLeft - 10 && top >= vsTop - 10 &&
                   left <= vsRight - 100 && top <= vsBottom - 60;
        }
    }
}
