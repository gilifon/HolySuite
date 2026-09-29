using System;
using System.IO;
// Every letter the new decoder (CwElementDecoder) judges, shown or dropped, with its time on the
// recording's own clock, its evidence and the speed then:  LetterTrace.exe file.wav from_s to_s
namespace HolyLogger
{
    static class Log { public static void Swallow(Exception e) { } }
    static class LetterTrace
    {
        static void Main(string[] a)
        {
            var bytes = File.ReadAllBytes(a[0]);
            int rate = BitConverter.ToInt32(bytes, 24); int start = 44;
            var d = new CwElementDecoder(rate, false);
            double from = double.Parse(a[1]), to = double.Parse(a[2]);
            d.Judged = (pat, end, ev, shown) => { double t = end * 0.005 + d.FirstReadingSample / (double)rate; if (t >= from && t <= to) Console.WriteLine("{0,7:F2}s {1,-7} ev {2,5:F2} {3} {4:F1} WPM", t, pat, ev, shown ? "shown" : "-- dropped", d.Wpm); };
            d.Text += s => { };
            var buf = new short[rate / 10];
            for (int i = start; i + 2 <= bytes.Length; )
            {
                int n = 0;
                while (n < buf.Length && i + 2 <= bytes.Length) { buf[n++] = BitConverter.ToInt16(bytes, i); i += 2; }
                d.Process(buf, n);
            }
        }
    }
}
