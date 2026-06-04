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
        private SplashWindow _splash;
        private DispatcherTimer _splashCloseTimer;
        private Window _realMainWindow;
        private bool _mainWindowRendered;

        public App()
        {
     
        }

        private void Application_Startup(object sender, StartupEventArgs e)
        {
            // Enable IE11 rendering mode for the WebBrowser control (required for Leaflet.js map)
            try
            {
                string exeName = System.IO.Path.GetFileName(System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName);
                Microsoft.Win32.Registry.SetValue(
                    @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Internet Explorer\Main\FeatureControl\FEATURE_BROWSER_EMULATION",
                    exeName, 11001, Microsoft.Win32.RegistryValueKind.DWord);
            }
            catch { }

            bool aIsNewInstance = false;
            myMutex = new Mutex(true, "HolyLoggerApplication", out aIsNewInstance);
            if (!aIsNewInstance)
            {
                MessageBox.Show("Holyland logger is already open...");
                App.Current.Shutdown();
                return;
            }

            // Keep app alive while splash is shown before the real main window is tracked.
            ShutdownMode = ShutdownMode.OnLastWindowClose;

            _splash = new SplashWindow();
            _splash.Show(); // no auto-close, topmost
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
            var realMain = Current.Windows.OfType<Window>().FirstOrDefault(w => w is MainWindow);
            if (realMain == null)
                return;

            if (_realMainWindow == realMain)
                return;

            _realMainWindow = realMain;
            MainWindow = _realMainWindow;
            ShutdownMode = ShutdownMode.OnMainWindowClose;

            _realMainWindow.SourceInitialized += OnMainWindowSourceInitialized;
            _realMainWindow.ContentRendered += OnMainWindowContentRendered;

            // If app is already rendered by the time we hook, finish immediately.
            if (_realMainWindow.IsLoaded && _realMainWindow.IsVisible && _mainWindowRendered)
                CloseSplash();
        }

        private void SplashCloseTimer_Tick(object sender, EventArgs e)
        {
            HookMainWindowForSplashClose();

            if (_realMainWindow != null && _realMainWindow.IsLoaded && _realMainWindow.IsVisible && _mainWindowRendered)
                CloseSplash();
        }

        private void OnMainWindowSourceInitialized(object sender, EventArgs e)
        {
            if (_realMainWindow != null)
                _realMainWindow.Cursor = Cursors.Wait;
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

            if (_realMainWindow != null)
            {
                _realMainWindow.SourceInitialized -= OnMainWindowSourceInitialized;
                _realMainWindow.ContentRendered -= OnMainWindowContentRendered;
                _realMainWindow.Cursor = null;
            }

            _splash?.Close();
            _splash = null;
            Mouse.OverrideCursor = null;

            // The splash was topmost and stole activation. Explicitly re-activate the main window
            // and move keyboard focus into it; otherwise it has no focus until the user clicks it,
            // which is why the F-keys (SSB and CW) did nothing until the first mouse click.
            if (_realMainWindow != null)
            {
                _realMainWindow.Activate();
                _realMainWindow.Focus();
                Keyboard.Focus(_realMainWindow);
            }
        }

    }
}
