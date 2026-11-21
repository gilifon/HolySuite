using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

namespace HolyLogger
{
    // HOW TRUE IS THE COM-PORT KEYING? (2026-10-21, he heard broken CW from COM4.) PortCwKeyer with no port
    // keys a known text; every edge's real time is compared with where it should have been (the first edge
    // of each letter sets the letter's start, as the keyer schedules it). Optionally the CW network decodes
    // a recording in the same process at the same time, as the decode window does, to see if that disturbs it.
    //
    //   KeyTiming.exe <wpm> <repeats> [<CwDeep.bin> <wav>]
    static class KeyTiming
    {
        static volatile PortCwKeyer _keyer;
        static readonly bool Pause = Environment.GetEnvironmentVariable("PAUSE") == "1";
        static int Main(string[] a)
        {
            double wpm = double.Parse(a[0], CultureInfo.InvariantCulture);
            int repeats = int.Parse(a[1]);
            if (Environment.GetEnvironmentVariable("LOWLAT") == "1") System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;
            if (a.Length >= 4) StartDecoding(a[2], a[3]);

            var edges = new List<KeyValuePair<bool, double>>();
            var keyer = PortCwKeyer.WithoutPort(wpm);
            _keyer = keyer;
            if (Environment.GetEnvironmentVariable("HOLD") == "1") keyer.BusyChanged += busy => CwDeepDecoder.HoldForKeying = busy;
            keyer.Edge += (down, ms) => { lock (edges) edges.Add(new KeyValuePair<bool, double>(down, ms)); };
            string text = "CQ CQ DE 4Z5SL 4Z5SL K ";
            int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
            for (int r = 0; r < repeats; r++) keyer.Send(text);
            Thread.Sleep(200);
            while (keyer.Busy) Thread.Sleep(50);

            // Element lengths: every down..up should be 1 or 3 units, every up..down 1, 3 or 7 units.
            double unit = 1200.0 / wpm;
            var err = new List<double>();
            for (int i = 1; i < edges.Count; i++)
            {
                double len = edges[i].Value - edges[i - 1].Value;
                double units = len / unit;
                double want = edges[i - 1].Key ? (units < 2 ? 1 : 3) : (units < 2 ? 1 : units < 5 ? 3 : 7);
                err.Add(len - want * unit);
            }
            err.Sort((x, y) => Math.Abs(x).CompareTo(Math.Abs(y)));
            int n = err.Count;
            Func<double, double> pct = p => Math.Abs(err[Math.Min(n - 1, (int)(p * n))]);
            int bad = err.FindAll(e => Math.Abs(e) > 0.25 * unit).Count;
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "{0} WPM, unit {1:F1} ms, {2} edges: median error {3:F2} ms, 99% {4:F2} ms, worst {5:F1} ms; {6} element(s) off by more than a quarter dit",
                wpm, unit, n, pct(0.5), pct(0.99), Math.Abs(err[n - 1]), bad));
            Console.WriteLine("GCs while keying: gen0 " + (GC.CollectionCount(0) - g0) + ", gen1 " + (GC.CollectionCount(1) - g1) + ", gen2 " + (GC.CollectionCount(2) - g2)
                + "; server GC " + System.Runtime.GCSettings.IsServerGC + ", latency " + System.Runtime.GCSettings.LatencyMode);
            keyer.Dispose();
            Environment.Exit(0);
            return 0;
        }

        // The network reading a recording over and over in real time, on its own thread, as in the decode window.
        static void StartDecoding(string netPath, string wav)
        {
            var net = new CwDeepNet(); string e;
            if (!net.Load(netPath, out e)) { Console.WriteLine(e); return; }
            byte[] raw = System.IO.File.ReadAllBytes(wav);
            int rate = BitConverter.ToInt32(raw, 24);
            var t = new Thread(() =>
            {
                var plain = new CwDecoder(rate);
                var deep = new CwDeepDecoder(rate, net);
                int block = rate / 10;
                var b = new short[block];
                var clock = System.Diagnostics.Stopwatch.StartNew();
                long blocks = 0;
                while (true)
                    for (int i = 44; i + 2 * block <= raw.Length; )
                    {
                        for (int k = 0; k < block; k++) { b[k] = BitConverter.ToInt16(raw, i); i += 2; }
                        plain.Process(b, block);
                        var ky = _keyer;
                        if (!(Pause && ky != null && ky.Busy))
                        {
                            deep.Process(b, block);
                            deep.Note(plain.SignalPresent ? plain.ToneHz : 0);
                        }
                        blocks++;
                        int wait = (int)(blocks * 100 - clock.ElapsedMilliseconds);
                        if (wait > 0) Thread.Sleep(wait);
                    }
            }) { IsBackground = true };
            t.Start();
            Thread.Sleep(20000);   // let the network get going: it reads 15 s windows
        }
    }
}
