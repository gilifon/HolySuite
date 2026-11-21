using System;
using System.Windows;
using System.Windows.Threading;

namespace HolyLogger
{
    // CLOSING THE PROGRAM WITH THE DECODER OPEN (2026-10-21, "Index was out of range" when he closed HolyLogger):
    // a main window owns the decode window, as in HolyLogger; after `seconds` the MAIN window is closed and the
    // program shuts down. Every error on the window thread is counted. Repeated `times` times, a new process
    // each time is best (run it in a loop) - this does one.
    //   OwnerCloseTest.exe <seconds> [shutdown]   (shutdown: Application.Shutdown instead of closing the owner)
    static class OwnerCloseTest
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool DestroyWindow(IntPtr hwnd);

        [STAThread]
        static int Main(string[] a)
        {
            double seconds = double.Parse(a[0], System.Globalization.CultureInfo.InvariantCulture);
            bool shutdown = a.Length > 1 && a[1] == "shutdown";
            bool destroy = a.Length > 1 && a[1] == "destroy";   // the main window's handle destroyed by Windows, as a hard close does
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            int errors = 0;
            app.DispatcherUnhandledException += (s, e) => { errors++; Console.WriteLine("ERROR: " + e.Exception.GetType().Name + ": " + e.Exception.Message); e.Handled = true; };
            app.Exit += (s, e) => Console.WriteLine(errors + " error(s)");
            var main = new Window { Width = 400, Height = 300, Title = "owner" };
            app.MainWindow = main;
            main.Show();
            var win = new CwDecodeWindow { Owner = main };
            win.Show();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                if (shutdown) app.Shutdown();
                else if (destroy) { DestroyWindow(new System.Windows.Interop.WindowInteropHelper(main).Handle); app.Shutdown(); }
                else main.Close();
            };
            timer.Start();
            app.Run();
            return errors;
        }
    }
}
