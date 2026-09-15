using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace HolyLogger
{
    static class Log { public static void Swallow(Exception e) { } }

    // Records the radio's audio to a WAV file so the decoders can be run against real signals as
    // often as we like. Uses HolyLogger's own capture code, so what lands in the file is exactly
    // what the decoders would have heard live.
    static class Recorder
    {
        static void Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "list")
            {
                Console.WriteLine("Recording devices:");
                var names = WaveInRecorder.GetInputDeviceNames();
                for (int i = 0; i < names.Count; i++) Console.WriteLine("  " + i + ": " + names[i]);
                return;
            }

            string device = args.Length > 0 ? args[0] : "";
            int seconds = args.Length > 1 ? int.Parse(args[1]) : 60;
            string path = args.Length > 2 ? args[2] : "radio.wav";

            var recorder = new WaveInRecorder();
            var captured = new List<byte>();
            int rate = 0;

            recorder.Samples += (samples, count) =>
            {
                for (int i = 0; i < count; i++)
                {
                    captured.Add((byte)(samples[i] & 0xFF));
                    captured.Add((byte)((samples[i] >> 8) & 0xFF));
                }
            };
            recorder.Failed += m => Console.WriteLine("STOPPED: " + m);

            string error;
            if (!recorder.Start(device, out error))
            {
                Console.WriteLine("Could not start: " + error);
                return;
            }

            rate = recorder.ActualSampleRate;
            Console.WriteLine("Recording " + seconds + "s from " + recorder.ActualDeviceName
                + " at " + rate + " samples a second...");

            for (int s = 0; s < seconds; s++)
            {
                Thread.Sleep(1000);
                Console.Write("\r  " + (s + 1) + "s   level " + Bar(recorder.Level) + "   ");
            }
            Console.WriteLine();

            recorder.Stop();
            WriteWav(path, captured, rate);
            Console.WriteLine("Wrote " + path + "  (" + captured.Count / 2 + " samples, "
                + (captured.Count / 2.0 / rate).ToString("F1") + "s)");
        }

        static string Bar(double level)
        {
            int n = (int)(Math.Sqrt(level) * 20);
            return new string('#', n).PadRight(20, '.');
        }

        static void WriteWav(string path, List<byte> data, int rate)
        {
            using (var f = new FileStream(path, FileMode.Create))
            using (var w = new BinaryWriter(f))
            {
                int bytes = data.Count;
                w.Write(new char[] { 'R', 'I', 'F', 'F' });
                w.Write(36 + bytes);
                w.Write(new char[] { 'W', 'A', 'V', 'E' });
                w.Write(new char[] { 'f', 'm', 't', ' ' });
                w.Write(16);
                w.Write((short)1);          // PCM
                w.Write((short)1);          // mono
                w.Write(rate);
                w.Write(rate * 2);          // bytes a second
                w.Write((short)2);          // block align
                w.Write((short)16);         // bits
                w.Write(new char[] { 'd', 'a', 't', 'a' });
                w.Write(bytes);
                w.Write(data.ToArray());
            }
        }
    }
}
