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

        // ── A WINDOW THAT IS NOT IN THE TASKBAR MUST NOT BE MINIMISABLE ──────────────────────────
        //
        // The operator opened the Log Workshop, pressed Verify, and minimised the Log Fixer that
        // appeared. It has ShowInTaskbar="False", so there was no button left to click - and it is
        // MODAL, so the Workshop underneath it stayed dead. Nothing on screen could be used and
        // nothing could be closed. His words: "this will put the users in dead end".
        //
        // Nearly every dialog in the program is declared that way, so this is fixed once, for all of
        // them, rather than in the one that was reported. Registered as a class handler on Window, so
        // it reaches every window there is and every window added later without anyone remembering.
        //
        // The minimise BUTTON is taken away rather than the minimising undone: a button that visibly
        // does nothing is the next thing to be reported. The main window is untouched - it is in the
        // taskbar, so minimising it is exactly what it should do.
        private const int GWL_STYLE = -16;
        private const int WS_MINIMIZEBOX = 0x00020000;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        // Changing the style is not enough on a window that is already on screen: Windows goes on
        // painting the caption it drew the first time, so the minimise button stayed there looking
        // pressable and did nothing when pressed. SWP_FRAMECHANGED is what makes it redraw the frame
        // and actually drop the button.
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_FRAMECHANGED = 0x0020;

        private static void RemoveMinimiseFromTaskbarlessWindows()
        {
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
                new RoutedEventHandler((sender, args) =>
                {
                    try
                    {
                        var w = sender as Window;
                        if (w == null) return;

                        // WILL THIS WINDOW HAVE A BUTTON IN THE TASKBAR? Only then may it be minimised.
                        //
                        // ShowInTaskbar is not the whole test. An OWNED window does not get a taskbar
                        // button of its own - Windows shows the owner instead - so the Log Workshop,
                        // which never set ShowInTaskbar at all, still disappeared when it was minimised:
                        // "i simply did not see and icon of the workshop after minizing it". The main
                        // window owns nothing and is in the taskbar, so it keeps its button.
                        if (w.ShowInTaskbar && w.Owner == null) return;

                        // A WINDOW THAT DRAWS ITS OWN TITLE BAR needs its own button hidden - there is
                        // no system caption to take anything away from. The Workshop and the Channels
                        // window are both WindowStyle="None" with hand-made caption buttons, so the
                        // Win32 style below would have left theirs sitting there.
                        var custom = w.FindName("TitleBar_MinimizeBtn") as UIElement;
                        if (custom != null) custom.Visibility = Visibility.Collapsed;

                        IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
                        if (hwnd == IntPtr.Zero) return;

                        int style = GetWindowLong(hwnd, GWL_STYLE);
                        if ((style & WS_MINIMIZEBOX) != 0)
                        {
                            SetWindowLong(hwnd, GWL_STYLE, style & ~WS_MINIMIZEBOX);
                            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                                         SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE
                                         | SWP_FRAMECHANGED);
                        }

                        // Belt and braces: a window minimised some other way - the taskbar's "show the
                        // desktop", a keyboard shortcut - comes straight back rather than disappearing.
                        w.StateChanged += (s2, e2) =>
                        {
                            if (w.WindowState == WindowState.Minimized
                                && !(w.ShowInTaskbar && w.Owner == null))
                                w.WindowState = WindowState.Normal;
                        };
                    }
                    catch (Exception swallowed) { Log.Swallow(swallowed); }
                }));
        }

        private void Application_Startup(object sender, StartupEventArgs e)
        {
            RemoveMinimiseFromTaskbarlessWindows();

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
                HolyMessageBox.ShowWarning(
                "HolyLogger is already open.\n\n"
                + "Only one copy can run at a time — two would write to the same log at "
                + "once. Look for it on the taskbar.",
                "HolyLogger");
                App.Current.Shutdown();
                return;
            }

            // Keep app alive while splash is shown before the real main window is tracked.
            ShutdownMode = ShutdownMode.OnLastWindowClose;

            Log.Warn("STARTUP " + Log.SinceLaunch() + "  app: showing the splash");
            StartSplashOnItsOwnThread();
            Mouse.OverrideCursor = Cursors.Wait;

            // Hook main window events as soon as WPF creates StartupUri window.
            Dispatcher.BeginInvoke(new Action(HookMainWindowForSplashClose), DispatcherPriority.Loaded);

            // Safety net: close splash as soon as the main window is visible/loaded.
            _splashCloseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _splashCloseTimer.Tick += SplashCloseTimer_Tick;
            _splashCloseTimer.Start();
        }

        // THE SPLASH GETS A THREAD OF ITS OWN, and it has to.
        //
        // WPF draws an animation on the thread that owns the window. The splash used to be owned by the
        // thread that is loading the program - the log, the country files, the cluster - so its spinner
        // could not move while any of that was happening: it stood still for the first ten seconds of a
        // thirteen-second start and then span for the last three. A spinner that only moves when there
        // is nothing to wait for is worse than none.
        //
        // On its own thread it turns steadily throughout, and the seconds in the middle of it count in
        // real time, because nothing that thread owns is doing any work.
        //
        // The window is created, shown and closed ON THAT THREAD. Nothing else in the program touches
        // it; CloseSplash asks it to close through its own dispatcher.
        private System.Threading.Thread _splashThread;

        private void StartSplashOnItsOwnThread()
        {
            var showing = new System.Threading.ManualResetEventSlim(false);

            _splashThread = new System.Threading.Thread(() =>
            {
                try
                {
                    _splash = new SplashWindow();
                    _splash.Show();
                }
                catch (Exception ex) { Log.Warn("Splash could not be shown: " + ex); }
                finally { showing.Set(); }

                // Its own message loop: this is what makes the animation and the seconds tick while the
                // main thread is busy. It ends when CloseSplash shuts this dispatcher down.
                System.Windows.Threading.Dispatcher.Run();
            });
            _splashThread.SetApartmentState(System.Threading.ApartmentState.STA);
            _splashThread.IsBackground = true;   // never keeps the program alive on its own
            _splashThread.Start();

            // Waited for, so the splash is up before the loading starts - but only briefly. A splash is
            // never worth holding the program for.
            showing.Wait(2000);
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
            Log.Warn("STARTUP " + Log.SinceLaunch() + "  main window: painted");

            _mainWindowRendered = true;
            ((Window)sender).ContentRendered -= OnMainWindowContentRendered;

            // Normal priority, NOT Background. Background means "when the window has nothing better to
            // do", and at startup it has plenty: the splash stood over a window that was already
            // painted for another 1,100 ms - measured. ContentRendered has already fired; the picture
            // is on the screen; there is nothing left to wait for.
            Dispatcher.BeginInvoke(new Action(CloseSplash), DispatcherPriority.Normal);
        }

        private bool _splashClosed;

        private void CloseSplash()
        {
            // ONCE. Two paths lead here - the ContentRendered event and the safety-net timer - and both
            // used to run, which is why the log carried the line twice, a second apart.
            if (_splashClosed) return;
            _splashClosed = true;

            Log.Warn("STARTUP " + Log.SinceLaunch() + "  splash closing - the program is up");

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

            // Closed on the thread that owns it, and that thread's loop ended with it - anything else
            // throws "the calling thread cannot access this object".
            var splash = _splash;
            _splash = null;
            if (splash != null)
            {
                try
                {
                    splash.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try { splash.Close(); }
                        catch (Exception swallowed) { Log.Swallow(swallowed); }
                        splash.Dispatcher.InvokeShutdown();
                    }));
                }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
            }
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
