using System;
using System.Windows;
using System.Windows.Threading;

namespace HolyLogger
{
    // THE REAL DECODE WINDOW IN A THROWAWAY PROCESS (2026-10-21): opened as HolyLogger opens it, listening to the
    // real sound input, while its thread is kept busy (Busy ms every 200 ms). After `seconds` a screen photo of it
    // goes to %TEMP%\decode_window_test.png; the strip's frame log is in %TEMP%\HolyLogger_cw_flow.tsv.
    //   DecodeWindowTest.exe <seconds> <busy ms>
    static class DecodeWindowTest
    {
        [STAThread]
        static int Main(string[] a)
        {
            int seconds = int.Parse(a[0]), busy = int.Parse(a[1]);
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var win = new CwDecodeWindow();
            win.Show();
            var hog = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            hog.Tick += (s, e) => System.Threading.Thread.Sleep(busy);
            hog.Start();
            // Optional: a CAT message "sent" at <cat at> s through the window's own transmit code, as MainWindow does.
            if (a.Length > 2)
            {
                var cat = new DispatcherTimer { Interval = TimeSpan.FromSeconds(double.Parse(a[2], System.Globalization.CultureInfo.InvariantCulture)) };
                cat.Tick += (s, e) => { cat.Stop(); CwDecodeWindow.CatCwSent("CQ CQ DE 4Z5SL 4Z5SL K", 25); };
                cat.Start();
            }
            var stop = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            stop.Tick += (s, e) =>
            {
                stop.Stop();
                var p = win.PointToScreen(new Point(0, 0));
                using (var bmp = new System.Drawing.Bitmap((int)win.ActualWidth, (int)win.ActualHeight))
                using (var g = System.Drawing.Graphics.FromImage(bmp))
                {
                    g.CopyFromScreen((int)p.X, (int)p.Y, 0, 0, bmp.Size);
                    bmp.Save(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "decode_window_test.png"));
                }
                win.Close();
                app.Shutdown();
            };
            stop.Start();
            app.Run();
            return 0;
        }
    }
}
