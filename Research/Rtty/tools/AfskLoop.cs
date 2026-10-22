// THE AFSK SENDER, CHECKED WITHOUT A SOUND CARD: the audio it makes is caught, saved as a WAV and
// read back by HolyLogger's own decoder. What comes back must be what was sent.
//
//   AfskLoop.exe ["text"] [out.wav]

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace HolyLogger
{
    static class Log
    {
        public static void Swallow(Exception e) { }
        public static void Warn(string s) { Console.WriteLine("WARN " + s); }
    }

    // The sender asks this for a device id; with no device it is never called.
    static class WaveOutPlayer { public static uint ResolveDeviceId(string name) { return 0xFFFFFFFF; } }

    static class AfskLoop
    {
        static int Main(string[] args)
        {
            string text = args.Length > 0 ? args[0] : "CQ TEST 4Z5SL 4Z5SL CQ\r\nDL1ABC 599 001 001 DL1ABC TU 73";
            var audio = new List<short>();
            var sent = new StringBuilder();
            bool done = false;
            using (var s = RttyAfskSender.WithoutDevice(block => { lock (audio) audio.AddRange(block); }))
            {
                s.CharacterSent += c => sent.Append(c);
                s.BusyChanged += busy => { if (!busy) done = true; };
                s.Send(text);
                var until = DateTime.UtcNow.AddSeconds(30);
                while (!done && DateTime.UtcNow < until) System.Threading.Thread.Sleep(10);
            }
            short[] a = audio.ToArray();
            if (args.Length > 1) WriteWav(args[1], a, 48000);

            var dec = new RttyDecoder(48000);
            dec.Tune(RttyAfskSender.MarkHz, 170, 45.45);
            var got = new StringBuilder();
            dec.CharacterDecoded += c => got.Append(c);
            dec.Process(a, a.Length);

            Func<string, string> norm = x => string.Join(" ", x.Replace('\r', ' ').Replace('\n', ' ').Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
            Console.WriteLine("audio:   " + (a.Length / 48000.0).ToString("0.0") + " s, peak " + Peak(a));
            Console.WriteLine("asked:   " + norm(text.ToUpperInvariant()));
            Console.WriteLine("sent:    " + norm(sent.ToString()));
            Console.WriteLine("decoded: " + norm(got.ToString()));
            bool ok = norm(got.ToString()) == norm(text.ToUpperInvariant()) && norm(sent.ToString()) == norm(text.ToUpperInvariant());
            Console.WriteLine(ok ? "MATCH" : "DIFFERENT");
            return ok ? 0 : 1;
        }

        static int Peak(short[] a) { int p = 0; foreach (short v in a) p = Math.Max(p, Math.Abs((int)v)); return p; }

        static void WriteWav(string path, short[] s, int rate)
        {
            using (var w = new BinaryWriter(File.Create(path)))
            {
                w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + s.Length * 2);
                w.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); w.Write(16); w.Write((short)1); w.Write((short)1);
                w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
                w.Write(Encoding.ASCII.GetBytes("data")); w.Write(s.Length * 2);
                foreach (short v in s) w.Write(v);
            }
        }
    }
}
