using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Threading;

namespace HolyLogger
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        Mutex myMutex;
        private SplashScreen _splash;
        private DispatcherTimer _splashCloseTimer;
        private bool _mainWindowRendered;

        public App()
        {
     
        }

        private void Application_Startup(object sender, StartupEventArgs e)
        {
            bool aIsNewInstance = false;
            myMutex = new Mutex(true, "HolyLoggerApplication", out aIsNewInstance);
            if (!aIsNewInstance)
            {
                MessageBox.Show("Holyland logger is already open...");
                App.Current.Shutdown();
                return;
            }

            _splash = new SplashScreen("Images/splash.png");
            _splash.Show(false, true); // no auto-close, topmost
            Mouse.OverrideCursor = Cursors.Wait;

            // Hook main window events as soon as WPF creates StartupUri window.
            Dispatcher.BeginInvoke(new Action(HookMainWindowForSplashClose), DispatcherPriority.Loaded);

            // Safety net: close splash as soon as the main window is visible/loaded.
            _splashCloseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _splashCloseTimer.Tick += SplashCloseTimer_Tick;
            _splashCloseTimer.Start();
        }

        private void HookMainWindowForSplashClose()
        {
            if (MainWindow != null)
            {
                MainWindow.SourceInitialized += OnMainWindowSourceInitialized;
                MainWindow.ContentRendered += OnMainWindowContentRendered;

                // If app is already rendered by the time we hook, finish immediately.
                if (MainWindow.IsLoaded && MainWindow.IsVisible && _mainWindowRendered)
                    CloseSplash();
            }
        }

        private void SplashCloseTimer_Tick(object sender, EventArgs e)
        {
            if (MainWindow != null && MainWindow.IsLoaded && MainWindow.IsVisible && _mainWindowRendered)
                CloseSplash();
        }

        private void OnMainWindowSourceInitialized(object sender, EventArgs e)
        {
            if (MainWindow != null)
                MainWindow.Cursor = Cursors.Wait;
        }

        private void OnMainWindowContentRendered(object sender, EventArgs e)
        {
            _mainWindowRendered = true;
            ((Window)sender).ContentRendered -= OnMainWindowContentRendered;
            Dispatcher.BeginInvoke(new Action(CloseSplash), DispatcherPriority.Background);
        }

        private void CloseSplash()
        {
            if (_splashCloseTimer != null)
            {
                _splashCloseTimer.Stop();
                _splashCloseTimer.Tick -= SplashCloseTimer_Tick;
                _splashCloseTimer = null;
            }

            if (MainWindow != null)
            {
                MainWindow.SourceInitialized -= OnMainWindowSourceInitialized;
                MainWindow.ContentRendered -= OnMainWindowContentRendered;
                MainWindow.Cursor = null;
            }

            _splash?.Close(TimeSpan.FromSeconds(0.5));
            _splash = null;
            Mouse.OverrideCursor = null;
        }

    }
}
