using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace HolyLogger
{
    /// <summary>
    /// Reads the CW being received: pick the recording device the radio's audio arrives on, listen,
    /// and watch the letters appear.
    ///
    /// THE METER EARNED ITS PLACE BEFORE THE DECODER DID. Every "it does not decode" report of this
    /// kind starts as a level problem - the wrong device picked, the radio's USB output at zero, or
    /// another program already holding the codec - and with a bar on screen the operator settles all
    /// three himself in seconds. The very first silence here turned out to be a muted recording
    /// input in Windows, nothing to do with the radio or with this program.
    ///
    /// The listening knows nothing about any radio (see WaveInRecorder) and the decoding knows
    /// nothing about any radio either (see CwDecoder): it follows the note in the passband, so it
    /// works the same on a rig HolyLogger cannot even reach over CAT.
    /// </summary>
    public partial class CwDecodeWindow : Window
    {
        // Sentinel dropdown entry for "use the Windows default device"; stored as an empty setting.
        // Same word and same rule as the sound-output picker in Options.
        const string SystemDefaultDevice = "System default";

        // Long enough to scroll back through a QSO, short enough that the box never becomes the
        // reason the window is slow. Trimmed from the front, so the newest text is always kept.
        const int MostCharactersKept = 20000;
        const int CharactersTrimmedAtOnce = 5000;

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
        readonly DispatcherTimer _screenTimer;

        CwDecoder _decoder;

        // Letters arrive on the capture thread, one or two at a time, and are collected here for the
        // screen timer to put on show in one go. A Dispatcher call per letter would be a thousand
        // hops a minute for something the eye cannot see happening that fast anyway.
        readonly StringBuilder _pending = new StringBuilder();
        readonly object _pendingGate = new object();

        // The bar falls back gently instead of snapping to zero between characters. Without it the
        // meter flickers on CW - which is silence half the time - and cannot be read at all.
        double _shownLevel;

        bool _loading;

        public CwDecodeWindow()
        {
            InitializeComponent();

            _recorder.Failed += OnRecorderFailed;
            _recorder.Samples += OnSamples;

            LoadDevices();

            _screenTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(50)
            };
            _screenTimer.Tick += ScreenTimer_Tick;
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

        /// <summary>
        /// Starts listening without the operator pressing anything. Used when the window opens by
        /// itself because the radio went to CW.
        /// </summary>
        public void StartListeningNow()
        {
            StartListening();
        }

        void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            StopListening();
            StatusText.Text = "Not listening.";
        }

        // Empties the text AND makes the decoder forget the speed and the note it had settled on.
        // Both belong to the station that has just gone; keeping them would only slow down the lock
        // onto the next one.
        void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            lock (_pendingGate) _pending.Clear();
            DecodedText.Clear();

            var decoder = _decoder;
            if (decoder != null) decoder.Reset();
            ToneAndSpeed.Text = string.Empty;
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

            // Built here rather than in the constructor because it has to be told the rate the device
            // actually opened at, which is not known until it opens.
            var decoder = new CwDecoder(_recorder.ActualSampleRate);
            decoder.Text += OnDecodedText;
            _decoder = decoder;

            StatusText.Foreground = (Brush)FindResource("MutedTextBrush");
            StatusText.Text = "Listening to " + _recorder.ActualDeviceName
                            + " at " + _recorder.ActualSampleRate.ToString("N0") + " samples a second.";

            BtnListen.IsEnabled = false;
            BtnStop.IsEnabled = true;
            _shownLevel = 0;
            _screenTimer.Start();
        }

        void StopListening()
        {
            _screenTimer.Stop();
            try { _recorder.Stop(); }
            catch (Exception swallowed) { Log.Swallow(swallowed); }

            var decoder = _decoder;
            _decoder = null;
            if (decoder != null) decoder.Text -= OnDecodedText;

            // Whatever the decoder had already handed over still belongs on screen.
            FlushPendingText();

            BtnListen.IsEnabled = true;
            BtnStop.IsEnabled = false;
            LevelBar.Value = 0;
            LevelWords.Text = string.Empty;
            ToneAndSpeed.Text = string.Empty;
            _shownLevel = 0;
        }

        // On the capture thread. Hand the block straight to the decoder - it is a few thousand
        // multiplications, far less than the 100 ms of audio it represents.
        void OnSamples(short[] samples, int count)
        {
            var decoder = _decoder;
            if (decoder == null) return;

            try { decoder.Process(samples, count); }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // Also on the capture thread: collect, do not touch the screen.
        void OnDecodedText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            lock (_pendingGate) _pending.Append(text);
        }

        void ScreenTimer_Tick(object sender, EventArgs e)
        {
            FlushPendingText();
            UpdateMeter();
            UpdateToneAndSpeed();
        }

        void FlushPendingText()
        {
            string text;
            lock (_pendingGate)
            {
                if (_pending.Length == 0) return;
                text = _pending.ToString();
                _pending.Clear();
            }

            if (DecodedText.Text.Length > MostCharactersKept)
                DecodedText.Text = DecodedText.Text.Substring(CharactersTrimmedAtOnce);

            DecodedText.AppendText(text);
            DecodedText.ScrollToEnd();
        }

        void UpdateMeter()
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

        void UpdateToneAndSpeed()
        {
            var decoder = _decoder;
            if (decoder == null) { ToneAndSpeed.Text = string.Empty; return; }

            if (!decoder.SignalPresent)
            {
                ToneAndSpeed.Text = "Waiting for a signal.";
                return;
            }

            ToneAndSpeed.Text = "Note " + Math.Round(decoder.ToneHz).ToString("N0") + " Hz"
                              + "     Speed " + Math.Round(decoder.Wpm).ToString("N0") + " WPM";
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
            _recorder.Samples -= OnSamples;
            StopListening();
            try { _recorder.Dispose(); }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }
    }
}
