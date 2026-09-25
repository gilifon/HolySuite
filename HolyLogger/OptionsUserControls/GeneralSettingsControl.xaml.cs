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

        // WHAT OMNIRIG IS ACTUALLY SET TO, beside the rig it applies to. The number is read out of
        // OmniRig's own file - it owns that file and we never write to it - and shown only when it is
        // worse than the 500 ms everything in this program is comfortable with. At 500 or better there
        // is nothing to say and the line stays out of the way.
        // Straight to OmniRig's own settings window - the only place the number can be changed, since
        // OmniRig owns that file and has it open. Coming back here re-reads it.
        private void OmniRigOpenBtn_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            try
            {
                var main = System.Windows.Application.Current != null
                         ? System.Windows.Application.Current.MainWindow as MainWindow
                         : null;

                if (main == null) return;

                main.OpenOmniRigSettings();
                ShowOmniRigPollInterval();   // he may have just changed it
            }
            catch (System.Exception swallowed) { Log.Swallow(swallowed); }
        }

        private void ShowOmniRigPollInterval()
        {
            try
            {
                if (OmniRigPollNote == null) return;

                int pollMs = MainWindow.ReadOmniRigPollMsFromFile(Properties.Settings.Default.SelectedOmniRig2);

                // NOTHING TO READ. A fresh install with OmniRig never opened, or its file somewhere
                // else - saying "0 ms" would be worse than saying nothing.
                if (pollMs <= 0) return;

                bool good = pollMs <= 500;

                // THE NUMBER IS SHOWN EITHER WAY, so he can see the program knows it and has looked.
                // Green and upright when there is nothing to do; red and italic when there is - and the
                // number itself in bold, because it is the one word in the sentence he is looking for.
                OmniRigPollNote.Inlines.Clear();
                OmniRigPollNote.Inlines.Add(
                    new System.Windows.Documents.Run("Your OmniRig asks the radio every "));
                OmniRigPollNote.Inlines.Add(new System.Windows.Documents.Run(pollMs + " ms")
                {
                    FontWeight = System.Windows.FontWeights.Bold
                });
                OmniRigPollNote.Inlines.Add(new System.Windows.Documents.Run(good
                    ? ". That is fine - nothing to change. You may go faster if you like, but not below "
                      + "100 ms."
                    : ". Everything here follows the radio that slowly - the frequency most of all. Set "
                      + "\"Poll int., ms\" to 500 or less, and not below 100 ms."));

                OmniRigPollNote.FontStyle = good
                    ? System.Windows.FontStyles.Normal
                    : System.Windows.FontStyles.Italic;

                OmniRigPollNote.Foreground = new System.Windows.Media.SolidColorBrush(
                    good ? System.Windows.Media.Color.FromRgb(0x1B, 0x5E, 0x20)
                         : System.Windows.Media.Color.FromRgb(0xC6, 0x28, 0x28));

                // The short green sentence ends early on its last line, so the button sits beside it in
                // the frame's corner. The red one runs to the edge, so the button gets a line below it.
                OmniRigPollNote.Margin = new System.Windows.Thickness(0, 0, 0, good ? 0 : 38);

                if (OmniRigPollFrame != null) OmniRigPollFrame.Visibility = System.Windows.Visibility.Visible;
            }
            catch (System.Exception swallowed) { Log.Swallow(swallowed); }
        }

        public GeneralSettingsControl()
        {
            InitializeComponent();

            ShowOmniRigPollInterval();

            // The cluster alert sounds (new-country / Unconfirmed spot) moved to the Cluster window's
            // gear; only the app-wide output device is configured here.
            // Output-device picker: "System default" (Windows default
            // device) + each real output device, so sounds can go to the speakers instead of a USB codec.
            InitSoundDevicePicker(CB_SoundDevice, Properties.Settings.Default.SoundOutputDevice);
            InitCwViaPicker();

            // THE PORT LIST FOLLOWS THE USB PLUGS by itself while this page is open - see ListenForPorts.
            Loaded += (sender, args) => ListenForPorts(true);
            Unloaded += (sender, args) => ListenForPorts(false);

            HasChanged = false;
        }

        // ── CW VIA: CAT commands, or a COM port HolyLogger keys itself ────────────────────────────
        //
        // Saved the moment it changes, like the decoder's input device, and MainWindow is told at once
        // (OnCwViaChanged): it lets go of the old port, and a keyer window already open follows.
        bool _loadingCwVia;

        // Properties, not fields: the list's template binds to IsNew to paint a new port bold green.
        public sealed class PortChoice
        {
            public string Port { get; set; }
            public string Text { get; set; }
            public bool IsNew { get; set; }
            public override string ToString() { return Text; }
        }

        // WHICH PORT IS THE KEYING CABLE? Nobody knows COM numbers by heart, and the adapter's name in the
        // list rarely says "keyer". So the page watches: every port that appears while it is open is
        // painted bold green, and one that appears on its own is the cable just plugged in - it is
        // chosen for him and the line underneath says so.
        HashSet<string> _knownPorts;
        readonly HashSet<string> _newPorts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string _justAppeared;

        void InitCwViaPicker()
        {
            string choose = null;
            _loadingCwVia = true;
            try
            {
                string saved = (Properties.Settings.Default.CwKeyPort ?? string.Empty).Trim();
                string catPort = MainWindow.ReadOmniRigCatPortFromFile(Properties.Settings.Default.SelectedOmniRig2);

                var present = new List<string>();
                try { present.AddRange(System.IO.Ports.SerialPort.GetPortNames()); }
                catch (Exception swallowed) { Log.Swallow(swallowed); }

                // What changed since the list was last read. Nothing is "new" on the first look - every
                // port was already there when the page opened.
                if (_knownPorts != null)
                {
                    var appeared = present.Where(p => !_knownPorts.Contains(p)).ToList();
                    foreach (string p in appeared) _newPorts.Add(p);
                    if (appeared.Count == 1) { _justAppeared = appeared[0]; choose = appeared[0]; }
                    else if (appeared.Count > 1) _justAppeared = null;
                }
                _newPorts.RemoveWhere(p => !present.Contains(p, StringComparer.OrdinalIgnoreCase));
                if (_justAppeared != null && !present.Contains(_justAppeared, StringComparer.OrdinalIgnoreCase)) _justAppeared = null;
                _knownPorts = new HashSet<string>(present, StringComparer.OrdinalIgnoreCase);

                // A port chosen earlier and not plugged in now is still offered, so the choice is not
                // silently lost - it just says it is not there.
                var names = new List<string>(present);
                if (saved.Length > 0 && !names.Contains(saved, StringComparer.OrdinalIgnoreCase)) names.Add(saved);

                var friendly = FriendlyPortNames();
                var items = names.Distinct(StringComparer.OrdinalIgnoreCase)
                                 .OrderBy(n => PortNumber(n))
                                 .Select(n =>
                                 {
                                     string what;
                                     friendly.TryGetValue(n, out what);
                                     string text = n + (string.IsNullOrEmpty(what) ? string.Empty : "  " + what);
                                     if (catPort != null && string.Equals(n, catPort, StringComparison.OrdinalIgnoreCase))
                                         text += "  (radio CAT)";
                                     else if (!present.Contains(n, StringComparer.OrdinalIgnoreCase))
                                         text += "  (not plugged in)";
                                     return new PortChoice { Port = n, Text = text, IsNew = _newPorts.Contains(n) };
                                 })
                                 .ToList();

                CB_CwKeyPort.ItemsSource = items;
                CB_CwKeyPort.SelectedItem = items.FirstOrDefault(i => string.Equals(i.Port, saved, StringComparison.OrdinalIgnoreCase));

                CB_CwKeyLine.SelectedIndex =
                    string.Equals(Properties.Settings.Default.CwKeyLine, "RTS", StringComparison.OrdinalIgnoreCase) ? 1 : 0;

                bool byPort = string.Equals(Properties.Settings.Default.CwVia, "PORT", StringComparison.OrdinalIgnoreCase);
                RB_CwViaPort.IsChecked = byPort;
                RB_CwViaCat.IsChecked = !byPort;
            }
            finally { _loadingCwVia = false; }

            // The cable just plugged in is chosen - and saved, through the same path as a click - unless
            // it is the radio's own CAT port, which is never a keying cable.
            if (choose != null && CB_CwKeyPort.ItemsSource is List<PortChoice>)
            {
                var item = ((List<PortChoice>)CB_CwKeyPort.ItemsSource)
                           .FirstOrDefault(i => string.Equals(i.Port, choose, StringComparison.OrdinalIgnoreCase));
                string catPort = MainWindow.ReadOmniRigCatPortFromFile(Properties.Settings.Default.SelectedOmniRig2);
                if (item != null && !string.Equals(choose, catPort, StringComparison.OrdinalIgnoreCase))
                    CB_CwKeyPort.SelectedItem = item;      // raises CwVia_Changed, which saves it
            }

            ShowCwViaNote();
        }

        // -- LISTENING FOR USB PLUGS --------------------------------------------------------------
        //
        // Windows tells every open window when a device comes or goes (WM_DEVICECHANGE) - it is how
        // Device Manager refreshes itself. For a serial port it can even name the port, but not every
        // driver says so (virtual ports made by software may not), so the port-specific news is not
        // trusted alone: ANY device change makes the page read the port list again and compare it with
        // the last one. That is also why there is no Refresh button any more.
        //
        // A plug-in arrives as several messages, and the port may not be in the list at the first of
        // them, so the list is read once they have been quiet for a moment.
        const int WM_DEVICECHANGE = 0x0219;
        System.Windows.Interop.HwndSource _hookedTo;
        System.Windows.Threading.DispatcherTimer _portSettle;

        void ListenForPorts(bool on)
        {
            try
            {
                if (_hookedTo != null) { _hookedTo.RemoveHook(DeviceChangeHook); _hookedTo = null; }
                if (!on) { if (_portSettle != null) _portSettle.Stop(); return; }

                _hookedTo = PresentationSource.FromVisual(this) as System.Windows.Interop.HwndSource;
                if (_hookedTo != null) _hookedTo.AddHook(DeviceChangeHook);
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        IntPtr DeviceChangeHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg != WM_DEVICECHANGE) return IntPtr.Zero;

            if (_portSettle == null)
            {
                _portSettle = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
                _portSettle.Tick += (sender, args) => { _portSettle.Stop(); InitCwViaPicker(); };
            }
            _portSettle.Stop();
            _portSettle.Start();
            return IntPtr.Zero;
        }

        static int PortNumber(string name)
        {
            int n;
            return name != null && name.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                   && int.TryParse(name.Substring(3), out n) ? n : 9999;
        }

        // "COM7" -> "USB-SERIAL CH340": the names Device Manager shows, read from where Windows keeps
        // them. Every serial device leaves "Device Parameters\PortName" under its entry in Enum.
        static Dictionary<string, string> FriendlyPortNames()
        {
            var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (var enumKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum"))
                {
                    if (enumKey == null) return found;
                    foreach (string bus in enumKey.GetSubKeyNames())
                    {
                        using (var busKey = enumKey.OpenSubKey(bus))
                        {
                            if (busKey == null) continue;
                            foreach (string device in busKey.GetSubKeyNames())
                            {
                                using (var deviceKey = busKey.OpenSubKey(device))
                                {
                                    if (deviceKey == null) continue;
                                    foreach (string instance in deviceKey.GetSubKeyNames())
                                    {
                                        using (var instanceKey = deviceKey.OpenSubKey(instance))
                                        using (var parameters = instanceKey == null ? null : instanceKey.OpenSubKey("Device Parameters"))
                                        {
                                            string port = parameters == null ? null : parameters.GetValue("PortName") as string;
                                            if (string.IsNullOrEmpty(port) || !port.StartsWith("COM", StringComparison.OrdinalIgnoreCase)) continue;

                                            string name = instanceKey.GetValue("FriendlyName") as string;
                                            if (string.IsNullOrEmpty(name)) continue;

                                            // "USB-SERIAL CH340 (COM7)" - the port is already in front of it.
                                            int at = name.LastIndexOf(" (COM", StringComparison.OrdinalIgnoreCase);
                                            if (at > 0) name = name.Substring(0, at);
                                            found[port] = name;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
            return found;
        }

        private void CwVia_Changed(object sender, RoutedEventArgs e)
        {
            if (_loadingCwVia || RB_CwViaPort == null || CB_CwKeyPort == null || CB_CwKeyLine == null) return;

            try
            {
                var choice = CB_CwKeyPort.SelectedItem as PortChoice;
                bool byPort = RB_CwViaPort.IsChecked == true;

                Properties.Settings.Default.CwVia = byPort ? "PORT" : "CAT";
                Properties.Settings.Default.CwKeyPort = choice == null ? string.Empty : choice.Port;
                Properties.Settings.Default.CwKeyLine = CB_CwKeyLine.SelectedIndex == 1 ? "RTS" : "DTR";
                Properties.Settings.Default.Save();
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }

            ShowCwViaNote();

            try
            {
                var main = Application.Current != null ? Application.Current.MainWindow as MainWindow : null;
                if (main != null) main.OnCwViaChanged();
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // One short line under the choice: what it means, or what is wrong with it.
        void ShowCwViaNote()
        {
            if (CwViaNote == null || RB_CwViaPort == null || CB_CwKeyPort == null) return;

            bool byPort = RB_CwViaPort.IsChecked == true;
            var choice = CB_CwKeyPort.SelectedItem as PortChoice;
            string catPort = MainWindow.ReadOmniRigCatPortFromFile(Properties.Settings.Default.SelectedOmniRig2);
            bool bad = false;
            string text;

            bool fresh = choice != null && _justAppeared != null
                         && string.Equals(choice.Port, _justAppeared, StringComparison.OrdinalIgnoreCase);

            if (!byPort)
                text = "The radio keys the CW itself, at the speed set on the radio.";
            else if (choice == null)
            { text = "Plug in your keying cable - or unplug it and plug it back in - and its port will be chosen."; bad = true; }
            else if (catPort != null && string.Equals(choice.Port, catPort, StringComparison.OrdinalIgnoreCase))
            { text = choice.Port + " is the radio's CAT port. Choose the keying cable's port."; bad = true; }
            else if (fresh)
                text = choice.Port + " just appeared - this is probably your keying cable. BK-IN must be on at the radio.";
            else
                text = "HolyLogger keys the radio on " + choice.Port + ", at the speed set in the CW Keyer. "
                     + "BK-IN must be on at the radio.";

            CwViaNote.Text = text;
            CwViaNote.FontWeight = fresh ? FontWeights.Bold : FontWeights.Normal;
            CwViaNote.Foreground = bad
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xC6, 0x28, 0x28))
                : fresh ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1B, 0x7F, 0x2E))
                        : System.Windows.Media.Brushes.Gray;
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

        // ── WHAT WAS TYPED BUT NOT TABBED AWAY FROM ─────────────────────────────────────────────
        //
        // A box on this page writes its value into the setting when it LOSES FOCUS - that is what a
        // WPF text binding does unless it is told otherwise. Close the window with the X while the
        // caret is still in the box and the value goes nowhere: it was on the screen, it was never in
        // the settings, and next time the old one is back.
        //
        // An operator reported exactly that about the UDP port, and he was right. Two other pages had
        // already been given this - the eQSL accounts and the Radio Control Panel's frequencies - and
        // this one, which holds three port numbers, had been missed.
        //
        // Every box on the page rather than the three by name: a box added later would otherwise have
        // the same fault and nobody would think to come back here.
        public void SaveAll()
        {
            try
            {
                foreach (TextBox box in FindTextBoxes(this))
                {
                    BindingExpression bound = box.GetBindingExpression(TextBox.TextProperty);
                    if (bound != null && bound.IsDirty) { bound.UpdateSource(); HasChanged = true; }
                }
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        private static IEnumerable<TextBox> FindTextBoxes(DependencyObject root)
        {
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                if (child is TextBox box) yield return box;
                foreach (TextBox deeper in FindTextBoxes(child)) yield return deeper;
            }
        }

        private void HasChanged_Click(object sender, RoutedEventArgs e)
        {
            HasChanged = true;
        }

        // The UDP Ports table. Its own Save button writes the list; the sockets are opened or closed to
        // match when the Options window closes (MainWindow calls ApplyUdpListeners then), so nothing has
        // to be flagged as changed here.
        private void BTN_UdpPorts_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                new UdpPortsWindow(Window.GetWindow(this)).ShowDialog();
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // The radio commands a Sukkot log sends (VFO, simplex, no tone, no TSQL). The window needs the
        // main window, which knows the rig on CAT, for its Send now button.
        private void BTN_ContestRadioSetup_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var main = Application.Current != null ? Application.Current.MainWindow as MainWindow : null;
                if (main != null) main.OpenContestRadioSetup(Window.GetWindow(this));
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // The ADIF Monitor list. Like the UDP Ports table, it is re-read when the Options window closes.
        private void BTN_AdifMonitor_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                new AdifMonitorWindow(Window.GetWindow(this)).ShowDialog();
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
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
