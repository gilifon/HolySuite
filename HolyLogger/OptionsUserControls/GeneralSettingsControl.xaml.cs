using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;


namespace HolyLogger.OptionsUserControls
{
    /// <summary>
    /// Interaction logic for GeneralSettingsControl.xaml
    /// </summary>
    public delegate void OmniRigEngine();
    public partial class GeneralSettingsControl : UserControl
    {
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool SetForegroundWindow(IntPtr hWnd);

        public event OmniRigEngine OmniRigEngine_Changed;

        public bool HasChanged { get; set; }

        public string _Rig1 = "Not Connected";
        public string Rig1
        {
            get { return _Rig1; }
            set
            {
                _Rig1 = value;
                Rig1_RB.Content = "1:  " + _Rig1;
            }
        }
        public string _Rig2 = "Not Connected";
        public string Rig2
        {
            get { return _Rig2; }
            set
            {
                _Rig2 = value;
                Rig2_RB.Content = "2:  " + _Rig2;
            }
        }

        // Choices for the new-country spot alert: the five Windows system sounds (always available,
        // no files) plus every .wav that ships in C:\Windows\Media (tada, chimes, Alarm01…, Ring01…).
        // A name ending in .wav is played from that folder; the rest map to system sounds in
        // MainWindow.PlayClusterAlertSound.
        static readonly string[] NewCountrySoundOptions = { "Chime", "Beep", "Exclamation", "Question", "Critical" };

        public GeneralSettingsControl()
        {
            InitializeComponent();

            var sounds = new List<string>(NewCountrySoundOptions);
            try
            {
                string mediaDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media");
                sounds.AddRange(Directory.GetFiles(mediaDir, "*.wav")
                                         .Select(Path.GetFileName)
                                         .OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }   // no Media folder -> system sounds only
            CB_NewCountrySound.ItemsSource = sounds;
            string saved = Properties.Settings.Default.ClusterNewCountrySound;
            CB_NewCountrySound.SelectedItem = sounds.Contains(saved, StringComparer.OrdinalIgnoreCase)
                ? sounds.First(n => string.Equals(n, saved, StringComparison.OrdinalIgnoreCase))
                : "Chime";

            // Same sound choices for the Unconfirmed-spot alert (its own separate list instance).
            CB_UnconfirmedSound.ItemsSource = new List<string>(sounds);
            string savedUnconf = Properties.Settings.Default.ClusterUnconfirmedSound;
            CB_UnconfirmedSound.SelectedItem = sounds.Contains(savedUnconf, StringComparer.OrdinalIgnoreCase)
                ? sounds.First(n => string.Equals(n, savedUnconf, StringComparison.OrdinalIgnoreCase))
                : "Chime";

            // Shared output-device picker for both alert sounds: "System default" (Windows default
            // device) + each real output device, so sounds can go to the speakers instead of a USB codec.
            InitSoundDevicePicker(CB_SoundDevice, Properties.Settings.Default.SoundOutputDevice);

            HasChanged = false;
        }

        // Sentinel dropdown entry for "use the Windows default device"; stored as an empty setting.
        const string SystemDefaultDevice = "System default";

        static void InitSoundDevicePicker(System.Windows.Controls.ComboBox combo, string savedDev)
        {
            var devices = new List<string> { SystemDefaultDevice };
            try { devices.AddRange(WaveOutPlayer.GetOutputDeviceNames()); }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
            combo.ItemsSource = devices;
            combo.SelectedItem =
                (!string.IsNullOrWhiteSpace(savedDev) && devices.Contains(savedDev, StringComparer.OrdinalIgnoreCase))
                    ? devices.First(d => string.Equals(d, savedDev, StringComparison.OrdinalIgnoreCase))
                    : SystemDefaultDevice;
        }

        // The saved device string for a picker: empty for "System default", else the device name.
        static string DeviceSettingFrom(System.Windows.Controls.ComboBox combo)
        {
            string d = combo.SelectedItem as string;
            return string.Equals(d, SystemDefaultDevice, StringComparison.Ordinal) ? string.Empty : (d ?? string.Empty);
        }

        private void CB_NewCountrySound_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CB_NewCountrySound.SelectedItem is string s)
                Properties.Settings.Default.ClusterNewCountrySound = s;
            HasChanged = true;
        }

        private void CB_SoundDevice_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            Properties.Settings.Default.SoundOutputDevice = DeviceSettingFrom(CB_SoundDevice);
            HasChanged = true;
        }

        private void BTN_TestNewCountrySound_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            MainWindow.PlayClusterAlertSound(CB_NewCountrySound.SelectedItem as string, DeviceSettingFrom(CB_SoundDevice));
        }

        private void CB_UnconfirmedSound_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CB_UnconfirmedSound.SelectedItem is string s)
                Properties.Settings.Default.ClusterUnconfirmedSound = s;
            HasChanged = true;
        }

        private void BTN_TestUnconfirmedSound_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            MainWindow.PlayClusterAlertSound(CB_UnconfirmedSound.SelectedItem as string, DeviceSettingFrom(CB_SoundDevice));
        }

        private void CBX_EnableOmniRigCAT_Changed(object sender, RoutedEventArgs e)
        {
            HasChanged = true;
            if (OmniRigEngine_Changed != null)
            {
                this.Dispatcher.Invoke(() =>
                {
                    OmniRigEngine_Changed.Invoke();
                });
            }
        }

        // Speaker button next to the "Beep when typing…" option: plays the beep on the selected device
        // so the user can confirm it. e.Handled stops the click from also toggling the checkbox.
        private void BTN_TestBeep_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            MainWindow.PlayClusterAlertSound("Beep", DeviceSettingFrom(CB_SoundDevice));
        }

        private void HasChanged_Click(object sender, RoutedEventArgs e)
        {
            HasChanged = true;
        }

        private static bool IsValidPort(string text)
        {
            int x;
            return int.TryParse(text, out x);
        }

        private void PreviewTextInputHandler(Object sender, TextCompositionEventArgs e)
        {
            e.Handled = !IsValidPort(e.Text);
        }

        // Use the DataObject.Pasting Handler  
        private void PastingHandler(object sender, DataObjectPastingEventArgs e)
        {
            if (e.DataObject.GetDataPresent(typeof(string)))
            {
                string text = (string)e.DataObject.GetData(typeof(string));
                if (!IsValidPort(text)) e.CancelCommand();
            }
            else e.CancelCommand();
        }
    }
    
}
