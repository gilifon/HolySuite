using System;
using System.IO;
// What the plain decoder believes, every 50 ms, over a stretch of a recording: its note, whether it
// hears a station, whether it has proved Morse, its speed.   PlainTrace.exe file.wav from_s to_s
namespace HolyLogger
{
    static class Log { public static void Swallow(Exception e) { } }
    static class PlainTrace
    {
        static void Main(string[] a)
        {
            var bytes = File.ReadAllBytes(a[0]);
            int rate = BitConverter.ToInt32(bytes, 24);
            double from = double.Parse(a[1], System.Globalization.CultureInfo.InvariantCulture), to = double.Parse(a[2], System.Globalization.CultureInfo.InvariantCulture);
            if (Environment.GetEnvironmentVariable("PT_NOREREAD") == "1") CwDecoder.ReReadOnNewNote = false;
            var all = new System.Text.StringBuilder();
            var d = new CwDecoder(rate);
            d.Text += s => all.Append(s);
            if (Environment.GetEnvironmentVariable("PT_READ") == "1") CwDecoder.TraceReading = m => { double tt = double.Parse(m.Substring(0, m.IndexOf('s')), System.Globalization.CultureInfo.InvariantCulture); if (tt >= from - 0.3 && tt <= to) Console.WriteLine("        " + m); };
            long fed = 0;
            d.LetterTimed += (l, s, e) => { if (fed >= from * rate && fed <= to * rate + rate) Console.WriteLine("      letter {0} {1:F3}-{2:F3}", l, s / (double)rate, e / (double)rate); };
            d.Text += s => { if (fed >= from * rate && fed <= to * rate + rate) Console.WriteLine("      text [{0}] at {1:F3}", s, fed / (double)rate); };
            int block = rate / 200; var buf = new short[block];
            for (int i = 44; i + 2 * block <= bytes.Length; i += 2 * block)
            {
                for (int k = 0; k < block; k++) buf[k] = BitConverter.ToInt16(bytes, i + 2 * k);
                d.Process(buf, block); fed += block;
                double t = fed / (double)rate;
                if (t >= from && t <= to && fed % (rate / 20) < block)
                    Console.WriteLine("{0,7:F2}s note {1,5:F0} present {2} morse {3} wpm {4:F1}", t, d.ToneHz, d.SignalPresent ? 1 : 0, d.ProvedMorse ? 1 : 0, d.Wpm);
            }
            if (Environment.GetEnvironmentVariable("PT_ALL") == "1") Console.WriteLine("ALL: " + all.ToString());
        }
    }
}
