using System;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace HolyLogger
{
    public partial class SplashWindow : Window
    {
        public SplashWindow()
        {
            InitializeComponent();

            // Load the splash image from resources
            var uri = new System.Uri("pack://application:,,,/HolyLogger;component/Images/splash.png");
            SplashImage.Source = new BitmapImage(uri);

            // Set version text from assembly — single source of truth, never stale
            var ver = Assembly.GetExecutingAssembly().GetName().Version;
            VersionText.Text = $"v{ver.Major}.{ver.Minor}.{ver.Build}";

            StartSpinner();
        }

        // THE SPINNER AND THE SECONDS, both started as the window opens and both left to stop with it.
        //
        // No stopping code: this window is closed when the main window is up, and a closed window's
        // animation and timer go with it. That is the whole difference from the import's spinner, which
        // has to be stopped and started while its window lives on.
        private DateTime _started;
        private DispatcherTimer _seconds;

        private void StartSpinner()
        {
            _started = DateTime.UtcNow;

            var turn = new DoubleAnimation(0, 360, new Duration(TimeSpan.FromSeconds(1.1)))
            {
                RepeatBehavior = RepeatBehavior.Forever
            };
            SplashSpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, turn);

            // Whole seconds, because that is what the number says. It ticks four times a second so the
            // number turns over close to when it should - a one-second timer competing with a busy
            // startup can be most of a second late.
            _seconds = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(250)
            };
            _seconds.Tick += (s, e) =>
                SecondsText.Text = ((int)(DateTime.UtcNow - _started).TotalSeconds).ToString();
            _seconds.Start();

            Closed += (s, e) =>
            {
                try
                {
                    _seconds.Stop();
                    SplashSpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, null);
                }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
            };
        }

    }
}
