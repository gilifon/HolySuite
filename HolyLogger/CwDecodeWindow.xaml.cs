using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace HolyLogger
{
    /// <summary>
    /// The ear for the CW decoder: pick the recording device the radio's audio arrives on, listen to
    /// it, and watch a meter that proves the sound is really there.
    ///
    /// THE METER EXISTS BEFORE THE DECODER ON PURPOSE. Every "the decoder does not work" report on
    /// every program of this kind starts as a level problem - the wrong device picked, the radio's
    /// USB output at zero, or another program already holding the codec. With a bar on screen the
    /// operator settles all three himself in a few seconds, and whatever is built on top starts from
    /// audio that is known to be good.
    ///
    /// Nothing here is particular to any radio: see the note at the top of WaveInRecorder.
    /// </summary>
    public partial class CwDecodeWindow : Window
    {
        // Sentinel dropdown entry for "use the Windows default device"; stored as an empty setting.
        // Same word and same rule as the sound-output picker in Options.
        const string SystemDefaultDevice = "System default";

        static readonly Brush QuietBrush = new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E));
        static readonly Brush GoodBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0xA8, 0x4D));
        static readonly Brush LoudBrush = new SolidColorBrush(Color.FromRgb(0xE8, 0x8A, 0x00));
        static readonly Brush TooLoudBrush = new SolidColorBrush(Color.FromRgb(0xCC, 0x33, 0x33));

        static CwDecodeWindow()
        {
            QuietBrush.Freeze();
            GoodBrush.Freeze();
            LoudBrush.Freeze();
            TooLoudBrush.Freeze();
        }

        readonly WaveInRecorder _recorder = new WaveInRecorder();
        readonly DispatcherTimer _meterTimer;

        // The bar falls back gently instead of snapping to zero between characters. Without it the
        // meter flickers on CW - which is silence half the time - and cannot be read at all.
        double _shownLevel;

        bool _loading;

        public CwDecodeWindow()
        {
            InitializeComponent();

            _recorder.Failed += OnRecorderFailed;

            LoadDevices();

            _meterTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(50)
            };
            _meterTimer.Tick += MeterTimer_Tick;
        }

        void LoadDevices()
        {
            _loading = true;
            try
            {
                string saved = Properties.Settings.Default.CwDecodeInputDevice;

                var devices = new List<string> { SystemDefaultDevice };
                try { devices.AddRange(WaveInRecorder.GetInputDeviceNames()); }
                catch (Exception swallowed) { Log.Swallow(swallowed); }

                CB_Device.ItemsSource = devices;
                CB_Device.SelectedItem =
                    (!string.IsNullOrWhiteSpace(saved) && devices.Contains(saved, StringComparer.OrdinalIgnoreCase))
                        ? devices.First(d => string.Equals(d, saved, StringComparison.OrdinalIgnoreCase))
                        : SystemDefaultDevice;

                // Said plainly rather than left to be discovered: a radio that is switched off has no
                // codec, so its name is simply not in the list.
                if (devices.Count == 1)
                    StatusText.Text = "This computer has no recording device. Switch the radio on, then press Refresh.";
            }
            finally { _loading = false; }
        }

        // The saved device string: empty for "System default", else the device name.
        string SelectedDeviceSetting()
        {
            string d = CB_Device.SelectedItem as string;
            return string.Equals(d, SystemDefaultDevice, StringComparison.Ordinal) ? string.Empty : (d ?? string.Empty);
        }

        void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            bool wasRunning = _recorder.IsRunning;
            if (wasRunning) StopListening();
            LoadDevices();
            if (wasRunning) StartListening();
        }

        // Switching device mid-listen moves the ear straight over rather than making him press Stop
        // and Listen again - that is what picking a different device means.
        void CB_Device_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;

            try
            {
                Properties.Settings.Default.CwDecodeInputDevice = SelectedDeviceSetting();
                Properties.Settings.Default.Save();
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }

            if (_recorder.IsRunning) { StopListening(); StartListening(); }
        }

        void BtnListen_Click(object sender, RoutedEventArgs e)
        {
            StartListening();
        }

        void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            StopListening();
            StatusText.Text = "Not listening.";
        }

        void StartListening()
        {
            if (_recorder.IsRunning) return;

            string error;
            if (!_recorder.Start(SelectedDeviceSetting(), out error))
            {
                StatusText.Text = error;
                StatusText.Foreground = TooLoudBrush;
                return;
            }

            StatusText.Foreground = (Brush)FindResource("MutedTextBrush");
            StatusText.Text = "Listening to " + _recorder.ActualDeviceName
                            + " at " + _recorder.ActualSampleRate.ToString("N0") + " samples a second.";

            BtnListen.IsEnabled = false;
            BtnStop.IsEnabled = true;
            _shownLevel = 0;
            _meterTimer.Start();
        }

        void StopListening()
        {
            _meterTimer.Stop();
            try { _recorder.Stop(); }
            catch (Exception swallowed) { Log.Swallow(swallowed); }

            BtnListen.IsEnabled = true;
            BtnStop.IsEnabled = false;
            LevelBar.Value = 0;
            LevelWords.Text = string.Empty;
            _shownLevel = 0;
        }

        void MeterTimer_Tick(object sender, EventArgs e)
        {
            double peak = _recorder.Level;

            // Rises at once, falls slowly: a meter that answers instantly to a dit but does not
            // collapse in the gap after it.
            _shownLevel = peak > _shownLevel ? peak : _shownLevel * 0.82;

            // A square-root scale. On a straight percentage the useful working range for a decoder -
            // roughly a tenth to a half of full scale - all huddles against the left-hand end where
            // it cannot be set by eye.
            LevelBar.Value = Math.Min(100.0, Math.Sqrt(_shownLevel) * 100.0);

            if (_shownLevel < 0.01)
            {
                LevelWords.Text = "No sound";
                LevelWords.Foreground = QuietBrush;
                LevelBar.Foreground = QuietBrush;
            }
            else if (_shownLevel > 0.92)
            {
                // Clipping squares the tone off and is worse for a decoder than a signal far too
                // quiet, so it is named plainly rather than left as a full bar.
                LevelWords.Text = "Too loud";
                LevelWords.Foreground = TooLoudBrush;
                LevelBar.Foreground = TooLoudBrush;
            }
            else if (_shownLevel > 0.7)
            {
                LevelWords.Text = "A bit loud";
                LevelWords.Foreground = LoudBrush;
                LevelBar.Foreground = LoudBrush;
            }
            else
            {
                LevelWords.Text = "Good";
                LevelWords.Foreground = GoodBrush;
                LevelBar.Foreground = GoodBrush;
            }
        }

        // The recorder gave up on its own thread - the radio was switched off, or the device was
        // taken away. Back onto the UI thread before touching anything on screen.
        void OnRecorderFailed(string message)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                StopListening();
                StatusText.Foreground = TooLoudBrush;
                StatusText.Text = message + " Press Refresh, then Listen again.";
            }));
        }

        void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            _recorder.Failed -= OnRecorderFailed;
            StopListening();
            try { _recorder.Dispose(); }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }
    }
}
