using System;
using System.Windows;
using System.Windows.Threading;

namespace HolyLogger
{
    // OPEN AND CLOSE THE DECODE WINDOW OVER AND OVER (2026-10-21, "Index was out of range" on closing it): each
    // time it listens for `seconds`, then closes; every error on the window's thread is counted and printed.
    //   OpenCloseTest.exe <times> <seconds>
    static class OpenCloseTest
    {
        [STAThread]
        static int Main(string[] a)
        {
            int times = int.Parse(a[0]); double seconds = double.Parse(a[1], System.Globalization.CultureInfo.InvariantCulture);
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            int errors = 0;
            app.DispatcherUnhandledException += (s, e) => { errors++; Console.WriteLine("ERROR: " + e.Exception.GetType().Name + ": " + e.Exception.Message); e.Handled = true; };
            int done = 0;
            CwDecodeWindow win = null;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            timer.Tick += (s, e) =>
            {
                if (win != null) { win.Close(); win = null; done++; Console.WriteLine("closed " + done); return; }
                if (done >= times) { timer.Stop(); Console.WriteLine(errors + " error(s)"); app.Shutdown(); return; }
                win = new CwDecodeWindow();
                win.Show();
            };
            timer.Start();
            app.Run();
            return 0;
        }
    }
}
