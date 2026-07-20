using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
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
        // Overrides the ~1s OS-default hover delay for every ToolTip in the app, so they all pop
        // consistently instead of only the handful of controls that set ToolTipService.InitialShowDelay
        // locally. Must run once, before any FrameworkElement is created, hence static ctor.
        static App()
        {
            ToolTipService.InitialShowDelayProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(400));
            ToolTipService.BetweenShowDelayProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(0));
        }

        // True once Windows starts ending the session (logoff / shutdown / restart). Checked by
        // MainWindow.Window_Closing to skip the upload-on-exit dialogs: a modal dialog there would
        // stall the whole logoff, and WPF ignores e.Cancel during session end anyway, which used
        // to strand the flow half-run (see DoShutdownCleanup's history).
        internal static bool IsWindowsSessionEnding;

        Mutex myMutex;

        const string SingleInstanceMutexName = "HolyLoggerApplication";

        // Set at startup when the active profile's file was missing and factory defaults were loaded
        // instead. MainWindow reports it once it exists, so the message has a proper owner window.
        internal static string MissingProfileAtStartup { get; private set; }

        // Hands back the single-instance mutex so a RELAUNCH can start before this process has finished
        // exiting. The Profile Manager restarts the app to apply a profile; without this the new instance
        // starts while the old one still holds the mutex and refuses with "Holyland logger is already
        // open." Only called on that deliberate restart path.
        internal static void ReleaseSingleInstanceMutex()
        {
            try
            {
                var app = Current as App;
                if (app?.myMutex == null) return;
                try { app.myMutex.ReleaseMutex(); }
                catch (System.Exception swallowed) { Log.Swallow(swallowed); }   // not owned -> Dispose still frees it
                app.myMutex.Dispose();
                app.myMutex = null;
            }
            catch (System.Exception swallowed) { Log.Swallow(swallowed); }
        }
        private SplashWindow _splash;
        private DispatcherTimer _splashCloseTimer;
        private Window _realMainWindow;
        private bool _mainWindowRendered;
        private DateTime _lastDispatcherException = DateTime.MinValue;   // cascade guard for the handler below

        public App()
        {
     
        }

        private void Application_Startup(object sender, StartupEventArgs e)
        {
            // Last-chance handling. Every unhandled exception lands in holylogger.log with a stack
            // trace. A UI (dispatcher) exception is additionally MARKED HANDLED so one bad click
            // doesn't kill a logging session mid-contest: the user is told it was recorded and the
            // app keeps running. Two escapes within 10 seconds mean something is systemically broken
            // (an error loop) -- then let it crash rather than limp on as a zombie. AppDomain /
            // task exceptions can't be recovered and stay log-only.
            DispatcherUnhandledException += (s, args) =>
            {
                Log.Fatal("Dispatcher", args.Exception);
                var now = DateTime.UtcNow;
                if ((now - _lastDispatcherException).TotalSeconds < 10) return;   // cascading -> crash
                _lastDispatcherException = now;
                args.Handled = true;
                try
                {
                    HolyMessageBox.ShowError(
                        "An unexpected error occurred. It was recorded in holylogger.log.\n\n" +
                        "You can keep working; if the program misbehaves, restart it.\n\nDetails: " +
                        args.Exception?.Message, "Unexpected Error");
                }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
            };
            AppDomain.CurrentDomain.UnhandledException += (s, args) => Log.Fatal("AppDomain", args.ExceptionObject as Exception);
            TaskScheduler.UnobservedTaskException += (s, args) => Log.Fatal("UnobservedTask", args.Exception);

            SessionEnding += (s, args) => { IsWindowsSessionEnding = true; Log.Warn("Windows session ending: " + args.ReasonSessionEnding); };

            // Load the active profile FIRST: it is the source of truth at startup, and everything below
            // (theme, then every window) reads the settings it writes. A missing profile falls back to
            // factory defaults; tell the operator rather than letting the setup silently change.
            try { MissingProfileAtStartup = ProfileManager.ApplyActiveProfileAtStartup(); }
            catch (System.Exception swallowed) { Log.Swallow(swallowed); }

            // Apply the saved Light/Dark theme before the main window loads.
            try { ThemeManager.ApplyFromSettings(); } catch (System.Exception swallowed) { Log.Swallow(swallowed); }

            // Make every window's native title bar/border follow the theme too (not just the
            // DynamicResource-themed client area), applying to the main window and every dialog
            // (HolyMessageBox, ViewLogsWindow, NewLogWindow, etc.) as soon as each one gets its
            // handle, with no per-window code needed. Intentional exceptions: SplashWindow has no
            // native chrome to color (WindowStyle=None + AllowsTransparency); QRZPhotoWindow is a
            // deliberately always-white photo viewer; OptionsWindow is forced to Background="White"
            // in its own XAML because none of its many OptionsUserControls panels are theme-aware
            // (all hardcoded black/gray text, never migrated). For all three, darkening only the
            // title bar would create the exact light-body/dark-chrome mismatch this fix removes
            // elsewhere.
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
                new RoutedEventHandler((s, args) =>
                {
                    if (!(s is Window w) || w is SplashWindow || w is QRZPhotoWindow) return;

                    if (!(w is OptionsWindow))
                        ThemeManager.ApplyWindowChrome(w);

                    // Close the Window-SUBCLASS theming hole in one place. WPF implicit styles match
                    // exact types only, so the app-wide themed Window style never reaches subclasses:
                    // their background fell back to the system WindowBrush key (repointed at MenuBg by
                    // ThemeManager) while their TextBlocks kept the hardwired BLACK default -- the
                    // recurring "black text on dark dialog" bug (About window, LoTW exit dialog, ...).
                    // For any window that did not set its own value locally, wire Background and the
                    // inheritable text color to the theme tokens. Windows that DID set their own (XAML
                    // or code) are left untouched. OptionsWindow resolves these against the light
                    // tokens locked in its own Resources, so it stays correctly light.
                    if (w.ReadLocalValue(Window.BackgroundProperty) == DependencyProperty.UnsetValue)
                        w.SetResourceReference(Window.BackgroundProperty, "WindowBg");
                    if (w.ReadLocalValue(System.Windows.Documents.TextElement.ForegroundProperty) == DependencyProperty.UnsetValue)
                        w.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "TextBrush");
                }));

            // Enable IE11 rendering mode for the WebBrowser control (required for Leaflet.js map)
            try
            {
                string exeName = System.IO.Path.GetFileName(System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName);
                Microsoft.Win32.Registry.SetValue(
                    @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Internet Explorer\Main\FeatureControl\FEATURE_BROWSER_EMULATION",
                    exeName, 11001, Microsoft.Win32.RegistryValueKind.DWord);
            }
            catch (System.Exception swallowed) { Log.Swallow(swallowed); }

            bool aIsNewInstance = false;
            myMutex = new Mutex(true, SingleInstanceMutexName, out aIsNewInstance);
            if (!aIsNewInstance)
            {
                HolyMessageBox.ShowWarning("Holyland logger is already open.", "HolyLogger");
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
