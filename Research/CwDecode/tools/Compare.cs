using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace HolyLogger
{
    static class Log { public static void Swallow(Exception e) { } }

    // Runs a recorded WAV through BOTH decoders and prints what each made of it.
    static class Compare
    {
        static bool Fixed; static double FixedTone, FixedWpm;
        static void Main(string[] args)
        {
            string wav = args[0];
            string weights = args[1];
            Fixed = args.Length > 2;
            if (Fixed) { FixedTone = double.Parse(args[2]); FixedWpm = double.Parse(args[3]); }

            int rate;
            short[] audio = ReadWav(wav, out rate);
            Console.WriteLine("read " + wav + ": " + audio.Length + " samples at " + rate
                + " (" + (audio.Length / (double)rate).ToString("F1") + "s)");
            Console.WriteLine();

            var net = new CwNeuralNet();
            string error;
            bool haveNet = net.Load(weights, out error);
            if (!haveNet) Console.WriteLine("no network: " + error);

            var plain = new CwDecoder(rate);
            var plainText = new StringBuilder();
            plain.Text += s => plainText.Append(s);

            var neural = haveNet ? new CwNeuralDecoder(rate, net) : null;
            if (neural != null && Fixed) neural.Configure(FixedTone, FixedWpm);
            var neuralText = new StringBuilder();
            if (neural != null) neural.Text += s => neuralText.Append(s);

            int block = rate / 10;
            var buf = new short[block];

            double lastTone = 0, lastWpm = 0;
            var speeds = new List<double>();

            for (int i = 0; i < audio.Length; i += block)
            {
                int n = Math.Min(block, audio.Length - i);
                Array.Copy(audio, i, buf, 0, n);

                plain.Process(buf, n);

                if (plain.SignalPresent)
                {
                    lastTone = plain.ToneHz;
                    lastWpm = plain.Wpm;
                    speeds.Add(lastWpm);
                    if (neural != null && !Fixed) neural.Configure(lastTone, lastWpm);
                }

                if (neural != null) neural.Process(buf, n);
            }

            speeds.Sort();
            Console.WriteLine("the plain decoder heard a note of " + Math.Round(lastTone) + " Hz"
                + (speeds.Count > 0 ? " and a speed around " + Math.Round(speeds[speeds.Count / 2]) + " WPM" : "")
                + "  (a signal in " + speeds.Count + " of " + (audio.Length / block) + " blocks)");
            Console.WriteLine();

            Console.WriteLine("=== PLAIN DECODER ===");
            Console.WriteLine(Wrap(plainText.ToString()));
            Console.WriteLine();
            Console.WriteLine("=== NEURAL DECODER ===");
            Console.WriteLine(Wrap(neuralText.ToString()));
        }

        static string Wrap(string s)
        {
            if (string.IsNullOrEmpty(s)) return "(nothing)";
            var b = new StringBuilder();
            for (int i = 0; i < s.Length; i += 78)
                b.AppendLine(s.Substring(i, Math.Min(78, s.Length - i)));
            return b.ToString();
        }

        static short[] ReadWav(string path, out int rate)
        {
            using (var f = File.OpenRead(path))
            using (var r = new BinaryReader(f))
            {
                r.ReadBytes(4);                 // RIFF
                r.ReadInt32();
                r.ReadBytes(4);                 // WAVE

                rate = 8000;
                short channels = 1, bits = 16;
                short[] data = null;

                while (f.Position < f.Length - 8)
                {
                    string id = new string(r.ReadChars(4));
                    int size = r.ReadInt32();
                    long next = f.Position + size + (size & 1);

                    if (id == "fmt ")
                    {
                        r.ReadInt16();
                        channels = r.ReadInt16();
                        rate = r.ReadInt32();
                        r.ReadInt32();
                        r.ReadInt16();
                        bits = r.ReadInt16();
                    }
                    else if (id == "data")
                    {
                        var bytes = r.ReadBytes(size);
                        int count = size / (bits / 8) / channels;
                        data = new short[count];
                        for (int i = 0; i < count; i++)
                            data[i] = BitConverter.ToInt16(bytes, i * (bits / 8) * channels);
                    }

                    f.Position = next;
                }

                return data ?? new short[0];
            }
        }
    }
}
