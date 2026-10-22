// READS AN ON-AIR RECORDING WITH HOLYLOGGER'S OWN RTTY DECODER.
//
// Finds the station by itself - the strongest pair of tones 170 Hz apart in the recording - then
// reads it both ways round (normal and reversed), so a recording taken on USB, where mark is the
// higher tone, reads as well as one taken on LSB. Prints the tones found and both texts.
//
//   RttyRead.exe recording.wav [lowToneHz]

using System;
using System.IO;
using System.Linq;
using System.Text;

namespace HolyLogger
{
    static class Log { public static void Swallow(Exception e) { } public static void Warn(string s) { } }

    static class RttyRead
    {
        static int Main(string[] args)
        {
            int rate;
            short[] s = Read(args[0], out rate);
            double low = args.Length > 1 ? double.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : FindPair(s, rate);
            Console.WriteLine(Path.GetFileName(args[0]) + ": tones " + low.ToString("0") + " / " + (low + 170).ToString("0") + " Hz");
            foreach (bool reverse in new[] { false, true })
            {
                var d = new RttyDecoder(rate) { Reverse = reverse };
                d.Tune(low, 170, 45.45);
                var text = new StringBuilder();
                d.CharacterDecoded += c => text.Append(c == '\r' ? ' ' : c == '\n' ? '|' : c);
                d.Process(s, s.Length);
                Console.WriteLine((reverse ? "  reversed: " : "  normal:   ") + text.ToString().Trim());
            }
            return 0;
        }

        // The strongest pair of tones 170 Hz apart, from the whole recording's averaged spectrum.
        static double FindPair(short[] s, int rate)
        {
            int n = 1; while (n < rate) n <<= 1;              // about a second a block: ~1 Hz bins
            var sum = new double[n / 2];
            for (int at = 0; at + n <= s.Length; at += n / 2)
            {
                var re = new double[n]; var im = new double[n];
                for (int i = 0; i < n; i++) re[i] = s[at + i] * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (n - 1)));
                Fft(re, im);
                for (int b = 0; b < n / 2; b++) sum[b] += re[b] * re[b] + im[b] * im[b];
            }
            double binHz = (double)rate / n, best = 0, bestHz = 2125;
            for (double f = 150; f + 170 < Math.Min(3000, rate / 2 - 50); f += binHz / 2)
            {
                double a = sum[(int)Math.Round(f / binHz)], b = sum[(int)Math.Round((f + 170) / binHz)];
                double score = Math.Sqrt(a * b);
                if (score > best) { best = score; bestHz = f; }
            }
            return bestHz;
        }

        static void Fft(double[] re, double[] im)
        {
            int n = re.Length;
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j) { double t = re[i]; re[i] = re[j]; re[j] = t; t = im[i]; im[i] = im[j]; im[j] = t; }
            }
            for (int len = 2; len <= n; len <<= 1)
            {
                double ang = -2 * Math.PI / len, wr = Math.Cos(ang), wi = Math.Sin(ang);
                for (int i = 0; i < n; i += len)
                {
                    double cr = 1, ci = 0;
                    for (int k = 0; k < len / 2; k++)
                    {
                        int a = i + k, b = i + k + len / 2;
                        double tr = re[b] * cr - im[b] * ci, ti = re[b] * ci + im[b] * cr;
                        re[b] = re[a] - tr; im[b] = im[a] - ti; re[a] += tr; im[a] += ti;
                        double nr = cr * wr - ci * wi; ci = cr * wi + ci * wr; cr = nr;
                    }
                }
            }
        }

        static short[] Read(string path, out int rate)
        {
            byte[] b = File.ReadAllBytes(path);
            rate = BitConverter.ToInt32(b, 24);
            int p = 12;
            while (p + 8 <= b.Length)
            {
                string id = Encoding.ASCII.GetString(b, p, 4);
                int len = BitConverter.ToInt32(b, p + 4);
                if (id == "data")
                {
                    int n = Math.Min(len, b.Length - p - 8) / 2;
                    var s = new short[n];
                    for (int i = 0; i < n; i++) s[i] = BitConverter.ToInt16(b, p + 8 + 2 * i);
                    return s;
                }
                p += 8 + len + (len & 1);
            }
            throw new InvalidDataException(path);
        }
    }
}
