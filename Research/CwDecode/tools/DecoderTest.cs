using System;
using System.Collections.Generic;
using System.Text;

namespace HolyLogger
{
    // Stub so CwDecoder.cs compiles outside the app.
    static class Log { public static void Swallow(Exception e) { } }

    static class DecoderTest
    {
        const int Rate = 8000;
        static int _pass, _fail;

        static readonly Dictionary<char, string> ToMorse = new Dictionary<char, string>
        {
            {'A',".-"},   {'B',"-..."}, {'C',"-.-."}, {'D',"-.."},  {'E',"."},    {'F',"..-."},
            {'G',"--."},  {'H',"...."}, {'I',".."},   {'J',".---"}, {'K',"-.-"},  {'L',".-.."},
            {'M',"--"},   {'N',"-."},   {'O',"---"},  {'P',".--."}, {'Q',"--.-"}, {'R',".-."},
            {'S',"..."},  {'T',"-"},    {'U',"..-"},  {'V',"...-"}, {'W',".--"},  {'X',"-..-"},
            {'Y',"-.--"}, {'Z',"--.."},
            {'0',"-----"},{'1',".----"},{'2',"..---"},{'3',"...--"},{'4',"....-"},
            {'5',"....."},{'6',"-...."},{'7',"--..."},{'8',"---.."},{'9',"----."},
            {'/',"-..-."},{'?',"..--.."},{'=',"-...-"},
        };

        static int _seedOffset;

        static void Main(string[] args)
        {
            if (args.Length > 0) _seedOffset = int.Parse(args[0]);
            Console.WriteLine("--- seed offset " + _seedOffset + " ---");

            Run("CQ CQ DE 4Z5SL K", 20, 600, 0.30, 0.010);
            Run("CQ DE 4Z5SL K", 30, 750, 0.30, 0.010);
            Run("TNX FER QSO 599 = 73", 15, 450, 0.30, 0.010);
            Run("CQ DE 4Z5SL K", 25, 600, 0.12, 0.030);
            Run("CQ DE 4Z5SL K", 20, 600, 0.30, 0.010, 0.15);
            Run("CQ DE 4Z5SL K", 40, 700, 0.30, 0.010);
            Run("CQ DE 4Z5SL K", 10, 500, 0.30, 0.010);
            Run("W1AW DE 4X4XYZ = RST 579 579 = BK", 22, 620, 0.25, 0.020);

            // The hard ones - what the real band does.
            NoiseOnly(0.03);
            NoiseOnly(0.15);
            Run("CQ DE 4Z5SL K", 22, 600, 0.06, 0.040);                       // weak, poor S/N
            Run("CQ DE 4Z5SL K", 22, 600, 0.30, 0.020, 0, true);              // slow deep fading
            Qrm("CQ DE 4Z5SL K", 22, 600, 0.30, "TEST W1XYZ TEST", 26, 850, 0.20, 0.020);

            // WHAT A REAL RECEIVER SOUNDS LIKE. The set has a CW filter, so what comes out of it is
            // not flat noise across the whole range - it is a hump of noise inside the filter and
            // near silence outside. This is the case the first signal test got wrong on the air.
            FilteredNoiseOnly(0.05, 600, 250);
            FilteredNoiseOnly(0.05, 600, 500);
            FilteredNoiseOnly(0.20, 700, 400);
            Run("CQ DE 4Z5SL K", 22, 600, 0.30, 0.050, 0, false, 600, 400);
            Run("CQ DE 4Z5SL K", 22, 600, 0.10, 0.050, 0, false, 600, 250);

            // A steady carrier or birdie in the passband. It stands well above the noise beside it,
            // so it passes for a signal - but it is not Morse and must print nothing.
            CarrierOnly(0.10, 600, 0.05, 600, 300);
            CarrierOnly(0.03, 660, 0.05, 600, 300);

            // WEAK SIGNALS - the ones he says start making mistakes. Through a real CW filter,
            // at several speeds, getting quieter until it breaks.
            Run("CQ DE 4Z5SL K", 12, 600, 0.05, 0.060, 0, false, 600, 300);
            Run("CQ DE 4Z5SL K", 12, 600, 0.035, 0.060, 0, false, 600, 300);
            Run("CQ DE 4Z5SL K", 16, 600, 0.05, 0.060, 0, false, 600, 300);
            Run("CQ DE 4Z5SL K", 16, 600, 0.035, 0.060, 0, false, 600, 300);
            Run("CQ DE 4Z5SL K", 22, 600, 0.05, 0.060, 0, false, 600, 300);
            Run("CQ DE 4Z5SL K", 22, 600, 0.035, 0.060, 0, false, 600, 300);
            Run("W1AW DE 4X4XYZ = RST 579 = BK", 18, 650, 0.045, 0.060, 0.08, false, 650, 300);
            Run("W1AW DE 4X4XYZ = RST 579 = BK", 28, 650, 0.045, 0.060, 0.08, false, 650, 300);
            Run("CQ DE LY2PX LY2PX K", 35, 700, 0.10, 0.050, 0, false, 700, 300);
            Run("CQ DE LY2PX LY2PX K", 35, 700, 0.06, 0.060, 0, false, 700, 300);
            Run("CQ DE LY2PX K", 32, 620, 0.08, 0.050, 0.08, false, 620, 300);
            Run("LLL EEE III SSS", 35, 700, 0.10, 0.050, 0, false, 700, 300);


            Console.WriteLine(_pass + " passed, " + _fail + " failed.");
        }

        static void Report(bool ok, string what, string wanted, string got, CwDecoder d)
        {
            if (ok) _pass++; else _fail++;
            Console.WriteLine((ok ? "PASS  " : "FAIL  ") + what);
            if (wanted != null) Console.WriteLine("      sent: " + wanted);
            Console.WriteLine("      got : " + got);
            if (d != null)
                Console.WriteLine("      tone found " + Math.Round(d.ToneHz) + " Hz, speed "
                    + Math.Round(d.Wpm) + " WPM");
            Console.WriteLine();
        }

        static string Decode(double[] audio, out CwDecoder decoder)
        {
            decoder = new CwDecoder(Rate);
            var got = new StringBuilder();
            decoder.Text += s => got.Append(s);

            int block = Rate / 10;
            var buf = new short[block];
            for (int i = 0; i < audio.Length; i += block)
            {
                int n = Math.Min(block, audio.Length - i);
                for (int j = 0; j < n; j++)
                {
                    double v = audio[i + j];
                    if (v > 1) v = 1; if (v < -1) v = -1;
                    buf[j] = (short)(v * 32000);
                }
                decoder.Process(buf, n);
            }
            return got.ToString().Trim();
        }

        static void Run(string text, double wpm, double toneHz, double amplitude, double noise,
                        double jitter = 0, bool fading = false,
                        double filterCentre = 0, double filterWidth = 0)
        {
            double[] audio = Generate(text, wpm, toneHz, amplitude, jitter, fading);
            AddNoise(audio, noise, 999);
            if (filterWidth > 0) BandPass(audio, filterCentre, filterWidth);

            CwDecoder d;
            string got = Decode(audio, out d);
            bool ok = string.Equals(got, text.Trim(), StringComparison.Ordinal);

            Report(ok, wpm.ToString("N0") + " WPM, " + toneHz.ToString("N0") + " Hz, amp "
                + amplitude.ToString("0.00") + ", noise " + noise.ToString("0.000")
                + (jitter > 0 ? ", jitter " + (jitter * 100).ToString("N0") + "%" : "")
                + (fading ? ", fading" : "")
                + (filterWidth > 0 ? ", through a " + filterWidth.ToString("N0") + " Hz CW filter" : ""),
                text.Trim(), got, d);
        }

        // Noise as it comes out of a receiver with a CW filter: a hump inside the filter, nothing
        // outside it. The only right answer is still silence.
        static void FilteredNoiseOnly(double noise, double centre, double width)
        {
            var audio = new double[Rate * 12];
            AddNoise(audio, noise, 3131);
            BandPass(audio, centre, width);

            CwDecoder d;
            string got = Decode(audio, out d);
            bool ok = got.Length == 0;

            Report(ok, "noise through a " + width.ToString("N0") + " Hz CW filter at "
                + centre.ToString("N0") + " Hz - must print NOTHING",
                "(nothing)", got.Length == 0 ? "(nothing)" : got, d);
        }

        static void CarrierOnly(double carrierAmp, double carrierHz, double noise,
                                double filterCentre, double filterWidth)
        {
            var audio = new double[Rate * 12];
            double phase = 0, step = 2 * Math.PI * carrierHz / Rate;
            for (int i = 0; i < audio.Length; i++) { audio[i] = Math.Sin(phase) * carrierAmp; phase += step; }
            AddNoise(audio, noise, 5150);
            BandPass(audio, filterCentre, filterWidth);

            CwDecoder d;
            string got = Decode(audio, out d);
            bool ok = got.Length == 0;

            Report(ok, "steady carrier at " + carrierHz.ToString("N0") + " Hz amp "
                + carrierAmp.ToString("0.00") + " in noise - must print NOTHING",
                "(nothing)", got.Length == 0 ? "(nothing)" : got, d);
        }

        // A plain two-pole band-pass, run twice for a steeper skirt - near enough to what a CW
        // filter does to the noise for this purpose.
        static void BandPass(double[] audio, double centre, double width)
        {
            for (int pass = 0; pass < 2; pass++)
            {
                double q = centre / width;
                double w0 = 2 * Math.PI * centre / Rate;
                double alpha = Math.Sin(w0) / (2 * q);
                double a0 = 1 + alpha;
                double b0 = alpha / a0, b2 = -alpha / a0;
                double a1 = -2 * Math.Cos(w0) / a0, a2 = (1 - alpha) / a0;

                double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
                for (int i = 0; i < audio.Length; i++)
                {
                    double x0 = audio[i];
                    double y0 = b0 * x0 + b2 * x2 - a1 * y1 - a2 * y2;
                    x2 = x1; x1 = x0; y2 = y1; y1 = y0;
                    audio[i] = y0;
                }
            }

            // The filter throws a lot of the level away; a receiver's AGC puts it back.
            double peak = 0;
            for (int i = 0; i < audio.Length; i++) { double v = Math.Abs(audio[i]); if (v > peak) peak = v; }
            if (peak > 1e-9)
            {
                double gain = 0.6 / peak;
                for (int i = 0; i < audio.Length; i++) audio[i] *= gain;
            }
        }

        // Nothing but band noise. The only right answer is silence.
        static void NoiseOnly(double noise)
        {
            var audio = new double[Rate * 12];
            AddNoise(audio, noise, 4242);

            CwDecoder d;
            string got = Decode(audio, out d);
            bool ok = got.Length == 0;

            Report(ok, "noise only, level " + noise.ToString("0.000") + " - must print NOTHING",
                "(nothing)", got.Length == 0 ? "(nothing)" : got, d);
        }

        // Two stations at once. The wanted one is the stronger; the other is a few hundred Hz away,
        // which is what the filter has to reject.
        static void Qrm(string text, double wpm, double toneHz, double amplitude,
                        string other, double otherWpm, double otherTone, double otherAmp, double noise)
        {
            double[] wanted = Generate(text, wpm, toneHz, amplitude, 0, false);
            double[] qrm = Generate(other, otherWpm, otherTone, otherAmp, 0, false);

            var audio = new double[Math.Max(wanted.Length, qrm.Length)];
            for (int i = 0; i < audio.Length; i++)
            {
                if (i < wanted.Length) audio[i] += wanted[i];
                if (i < qrm.Length) audio[i] += qrm[i];
            }
            AddNoise(audio, noise, 777);

            CwDecoder d;
            string got = Decode(audio, out d);
            bool ok = string.Equals(got, text.Trim(), StringComparison.Ordinal);

            Report(ok, "QRM: wanted " + toneHz.ToString("N0") + " Hz amp " + amplitude.ToString("0.00")
                + ", other station " + otherTone.ToString("N0") + " Hz amp " + otherAmp.ToString("0.00"),
                text.Trim(), got, d);
        }

        static void AddNoise(double[] audio, double noise, int seed)
        {
            if (noise <= 0) return;
            var rnd = new Random(seed + _seedOffset);
            for (int i = 0; i < audio.Length; i++)
                audio[i] += (rnd.NextDouble() * 2 - 1) * noise;
        }

        static double[] Generate(string text, double wpm, double toneHz, double amplitude,
                                 double jitter, bool fading)
        {
            double ditMs = 1200.0 / wpm;
            var rnd = new Random(12345);
            var samples = new List<double>();
            double phase = 0;
            double step = 2 * Math.PI * toneHz / Rate;

            Action<double, bool> emit = (ms, keyed) =>
            {
                if (jitter > 0) ms *= 1.0 + (rnd.NextDouble() * 2 - 1) * jitter;
                int n = (int)Math.Round(Rate * ms / 1000.0);
                for (int i = 0; i < n; i++)
                {
                    double amp = amplitude;
                    if (fading)
                    {
                        double t = samples.Count / (double)Rate;
                        amp *= 0.55 + 0.45 * Math.Sin(2 * Math.PI * t / 4.0);
                    }
                    samples.Add(keyed ? Math.Sin(phase) * amp : 0);
                    phase += step;
                }
            };

            emit(500, false);

            bool firstChar = true;
            foreach (char raw in text.ToUpperInvariant())
            {
                if (raw == ' ') { emit(ditMs * 7, false); firstChar = true; continue; }

                string pattern;
                if (!ToMorse.TryGetValue(raw, out pattern)) continue;

                if (!firstChar) emit(ditMs * 3, false);
                firstChar = false;

                for (int i = 0; i < pattern.Length; i++)
                {
                    if (i > 0) emit(ditMs, false);
                    emit(pattern[i] == '-' ? ditMs * 3 : ditMs, true);
                }
            }

            emit(1500, false);
            return samples.ToArray();
        }
    }
}
