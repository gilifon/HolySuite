// THE FSK SENDER, CHECKED WITHOUT A RADIO.
//
// HolyLogger's own RttyFskKeyer keys a port that is not there; every change of its line is caught
// with the time it happened, turned back into RTTY audio (mark 2125, space 2295, continuous phase),
// and read by HolyLogger's own decoder. What comes back must be what was sent - that proves the
// Baudot, the shifts and the bit timing together. It also reports how far each line change was from
// where it should have been, which is the timing the radio will actually get.
//
//   FskLoop.exe ["text to send"] [out.wav]

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace HolyLogger
{
    static class Log
    {
        public static void Swallow(Exception e) { }
        public static void Warn(string s) { Console.WriteLine("WARN " + s); }
    }

    static class FskLoop
    {
        static int Main(string[] args)
        {
            string text = args.Length > 0 ? args[0] : "CQ TEST 4Z5SL 4Z5SL CQ\r\nDL1ABC 599 001 001 DL1ABC TU 73";
            var edges = new List<KeyValuePair<bool, double>>();
            var sent = new StringBuilder();
            bool done = false;
            using (var k = RttyFskKeyer.WithoutPort())
            {
                k.Edge += (mark, ms) => { lock (edges) edges.Add(new KeyValuePair<bool, double>(mark, ms)); };
                k.CharacterSent += c => sent.Append(c);
                k.BusyChanged += busy => { if (!busy) done = true; };
                k.Send(text);
                var until = DateTime.UtcNow.AddSeconds(60);
                while (!done && DateTime.UtcNow < until) System.Threading.Thread.Sleep(20);
            }

            // Timing: every edge should sit on a whole number of bits from the first start bit, plus
            // the half bit of each stop. Measured as the distance to the nearest half-bit grid point.
            double bit = 1000.0 / RttyFskKeyer.Baud;
            var spaceEdges = edges.Where(e => !e.Key).ToList();
            double t0 = spaceEdges.Count > 0 ? spaceEdges[0].Value : 0;
            double worst = 0, sum = 0; int n = 0;
            foreach (var e in edges.Where(e => e.Value >= t0))
            {
                double halfBits = (e.Value - t0) / (bit / 2);
                double err = Math.Abs(halfBits - Math.Round(halfBits)) * bit / 2;
                worst = Math.Max(worst, err); sum += err; n++;
            }
            Console.WriteLine("edges " + edges.Count + "   timing error: mean " + (n > 0 ? sum / n : 0).ToString("0.00") + " ms, worst " + worst.ToString("0.00") + " ms  (a bit is " + bit.ToString("0.0") + " ms)");

            // The line, turned into audio and read back.
            int rate = 11025;
            double start = edges.Count > 0 ? edges[0].Value - 500 : 0, end = edges.Count > 0 ? edges[edges.Count - 1].Value + 500 : 0;
            int count = (int)((end - start) / 1000 * rate);
            var audio = new short[count];
            double phase = 0; int ei = 0; bool markNow = true;
            for (int i = 0; i < count; i++)
            {
                double ms = start + i * 1000.0 / rate;
                while (ei < edges.Count && edges[ei].Value <= ms) { markNow = edges[ei].Key; ei++; }
                phase += 2 * Math.PI * (markNow ? 2125 : 2295) / rate;
                audio[i] = (short)(8000 * Math.Sin(phase));
            }
            var dec = new RttyDecoder(rate);
            dec.Tune(2125, 170, 45.45);
            var got = new StringBuilder();
            dec.CharacterDecoded += c => got.Append(c);
            dec.Process(audio, audio.Length);

            Func<string, string> norm = s => string.Join(" ", s.Replace('\r', ' ').Replace('\n', ' ').Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
            Console.WriteLine("asked:   " + norm(text.ToUpperInvariant()));
            Console.WriteLine("sent:    " + norm(sent.ToString()));
            Console.WriteLine("decoded: " + norm(got.ToString()));
            bool ok = norm(got.ToString()) == norm(text.ToUpperInvariant()) && norm(sent.ToString()) == norm(text.ToUpperInvariant());
            Console.WriteLine(ok ? "MATCH" : "DIFFERENT");
            return ok ? 0 : 1;
        }
    }
}
