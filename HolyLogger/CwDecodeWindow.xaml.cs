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
        /// <summary>
        /// Raised when the recording device is changed in Options, so a window already listening
        /// moves to the new one instead of going on with the old until it is closed and reopened.
        /// </summary>
        public static event Action InputDeviceChanged;

        internal static void RaiseInputDeviceChanged()
        {
            var handler = InputDeviceChanged;
            if (handler == null) return;
            try { handler(); }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

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

        public CwDecodeWindow()
        {
            InitializeComponent();

            _recorder.Failed += OnRecorderFailed;
            _recorder.Samples += OnSamples;
            InputDeviceChanged += OnInputDeviceChanged;

            _screenTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(50)
            };
            _screenTimer.Tick += ScreenTimer_Tick;

            RestoreWindowBounds();
        }
        // ---- where the window was left last time ----
        //
        // Same rule as the Cluster Alerts window: the saved spot is checked against the WHOLE desktop
        // - every monitor - before it is used. Checking only the main screen is how a window on the
        // second monitor gets restored somewhere the operator cannot reach it, and if the saved spot
        // is not on any screen any more the window simply opens centred instead.

        void RestoreWindowBounds()
        {
            try
            {
                var s = Properties.Settings.Default;
                if (s.CwDecodeWindowWidth >= MinWidth) Width = s.CwDecodeWindowWidth;
                if (s.CwDecodeWindowHeight >= MinHeight) Height = s.CwDecodeWindowHeight;

                if (IsPositionOnScreen(s.CwDecodeWindowLeft, s.CwDecodeWindowTop))
                {
                    WindowStartupLocation = WindowStartupLocation.Manual;
                    Left = s.CwDecodeWindowLeft;
                    Top = s.CwDecodeWindowTop;
                }
                else
                {
                    WindowStartupLocation = WindowStartupLocation.CenterOwner;
                }
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        void SaveWindowBounds()
        {
            try
            {
                var b = WindowState == WindowState.Normal
                    ? new Rect(Left, Top, Width, Height)
                    : RestoreBounds;

                var s = Properties.Settings.Default;
                if (!double.IsNaN(b.Left) && !double.IsInfinity(b.Left) &&
                    !double.IsNaN(b.Top) && !double.IsInfinity(b.Top))
                {
                    s.CwDecodeWindowLeft = b.Left;
                    s.CwDecodeWindowTop = b.Top;
                }
                if (b.Width > 0) s.CwDecodeWindowWidth = b.Width;
                if (b.Height > 0) s.CwDecodeWindowHeight = b.Height;
                s.Save();
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // A saved spot on a monitor that has since been unplugged must not strand the window where
        // it cannot be reached.
        static bool IsPositionOnScreen(double left, double top)
        {
            if (double.IsNaN(left) || double.IsNaN(top) ||
                double.IsInfinity(left) || double.IsInfinity(top))
                return false;

            double vsLeft = SystemParameters.VirtualScreenLeft;
            double vsTop = SystemParameters.VirtualScreenTop;
            double vsRight = vsLeft + SystemParameters.VirtualScreenWidth;
            double vsBottom = vsTop + SystemParameters.VirtualScreenHeight;

            return left >= vsLeft - 10 && top >= vsTop - 10 &&
                   left <= vsRight - 100 && top <= vsBottom - 60;
        }

        // The status line is for TROUBLE ONLY - a device another program is holding, a radio switched
        // off, a device that vanished. With nothing wrong it takes no room at all, rather than
        // sitting there stating the obvious under a level bar that already shows it.
        void SetStatus(string trouble)
        {
            if (string.IsNullOrEmpty(trouble))
            {
                StatusText.Text = string.Empty;
                StatusText.Visibility = Visibility.Collapsed;
                return;
            }

            StatusText.Text = trouble;
            StatusText.Foreground = TooLoudBrush;
            StatusText.Visibility = Visibility.Visible;
        }

        // Which device to listen to is set in Options (General > CW Decode) and simply read here.
        static string ChosenDevice()
        {
            try { return Properties.Settings.Default.CwDecodeInputDevice ?? string.Empty; }
            catch (Exception swallowed) { Log.Swallow(swallowed); return string.Empty; }
        }

        // Changed in Options while this window is listening: move the ear across at once.
        void OnInputDeviceChanged()
        {
            if (!_recorder.IsRunning) return;
            StopListening();
            StartListening();
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
            SetStatus(null);
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
            if (!_recorder.Start(ChosenDevice(), out error))
            {
                SetStatus(error);
                return;
            }

            // Built here rather than in the constructor because it has to be told the rate the device
            // actually opened at, which is not known until it opens.
            var decoder = new CwDecoder(_recorder.ActualSampleRate);
            decoder.Text += OnDecodedText;
            _decoder = decoder;

            // Nothing said when all is well. Which device it is and how fast it samples were only
            // ever of interest while this was being built; on the air they are a line of room taken
            // from the decoded text for ever, to say what the moving level bar already says.
            SetStatus(null);

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
                SetStatus(message + " Check the device in Options, then press Listen again.");
            }));
        }

        void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            SaveWindowBounds();
            _recorder.Failed -= OnRecorderFailed;
            _recorder.Samples -= OnSamples;
            InputDeviceChanged -= OnInputDeviceChanged;
            StopListening();
            try { _recorder.Dispose(); }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }
    }
}
