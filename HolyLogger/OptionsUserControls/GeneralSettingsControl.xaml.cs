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

        public GeneralSettingsControl()
        {
            InitializeComponent();

            // The cluster alert sounds (new-country / Unconfirmed spot) moved to the Cluster window's
            // gear; only the app-wide output device is configured here.
            // Output-device picker: "System default" (Windows default
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

        [DllImport("user32.dll")] private static extern bool SetCursorPos(int X, int Y);

        // Land the operator directly on the audio-output-device picker: scroll it into view, give it
        // keyboard focus, and park the MOUSE POINTER on it so it doesn't have to be hunted for. Used by
        // the Cluster Settings window's "Sounds" link.
        //
        // Deferred to Loaded priority (and retried) because the General page may only just have been
        // switched in: before layout the control has no size and PointToScreen would be meaningless.
        public void FocusSoundDevicePicker(int attempt = 0)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (CB_SoundDevice == null) return;

                    // Not laid out / not on screen yet -> try again shortly (bounded, so we never spin).
                    if (!CB_SoundDevice.IsVisible || CB_SoundDevice.ActualWidth <= 0 || CB_SoundDevice.ActualHeight <= 0)
                    {
                        if (attempt < 10) FocusSoundDevicePicker(attempt + 1);
                        return;
                    }

                    CB_SoundDevice.BringIntoView();
                    CB_SoundDevice.Focus();
                    Keyboard.Focus(CB_SoundDevice);

                    // PointToScreen gives physical screen pixels, which is what SetCursorPos wants.
                    Point centre = CB_SoundDevice.PointToScreen(
                        new Point(CB_SoundDevice.ActualWidth / 2.0, CB_SoundDevice.ActualHeight / 2.0));
                    SetCursorPos((int)centre.X, (int)centre.Y);
                }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        // Land the operator directly on the "Validate for HAM frequency" checkbox: scroll it into view,
        // give it keyboard focus, and park the MOUSE POINTER on it so it doesn't have to be hunted for.
        // Used by the "here" link in the non-HAM-frequency warning. Same deferred/retry dance as
        // FocusSoundDevicePicker, for the same reason (the General page may only just have been shown).
        public void FocusHamFrequencyValidation(int attempt = 0)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (CBX_ValidateHamFrequency == null) return;

                    if (!CBX_ValidateHamFrequency.IsVisible || CBX_ValidateHamFrequency.ActualWidth <= 0 || CBX_ValidateHamFrequency.ActualHeight <= 0)
                    {
                        if (attempt < 10) FocusHamFrequencyValidation(attempt + 1);
                        return;
                    }

                    CBX_ValidateHamFrequency.BringIntoView();
                    CBX_ValidateHamFrequency.Focus();
                    Keyboard.Focus(CBX_ValidateHamFrequency);

                    Point centre = CBX_ValidateHamFrequency.PointToScreen(
                        new Point(CBX_ValidateHamFrequency.ActualWidth / 2.0, CBX_ValidateHamFrequency.ActualHeight / 2.0));
                    SetCursorPos((int)centre.X, (int)centre.Y);
                }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void CB_SoundDevice_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            Properties.Settings.Default.SoundOutputDevice = DeviceSettingFrom(CB_SoundDevice);
            HasChanged = true;
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
