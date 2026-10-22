// THE RTTY TEST BENCH.
//
// Makes RTTY recordings whose every letter is known, puts them through the troubles a real HF path
// brings - noise, two-path fading, a station a little off frequency, a second station close by -
// and scores decoders on them. Nothing here is part of HolyLogger or its installer.
//
//   RttyBench one <out.wav> [snr=dB] [fade=none|good|moderate|poor|flutter] [offset=Hz] [qrm=dB@Hz]
//                 [stop=1|1.5|2] [seed=n] [rate=11025]           one recording + <out>.txt (what was sent)
//   RttyBench make <folder> [rate=11025]                          the whole bench + manifest.tsv
//   RttyBench mmtty <folder|file.wav> [types=0,1,2,3] [atc=0,1] [sq=1]   score MMTTY's decoders
//
// SNR is measured the way the WSJT programs do it: signal power against the noise in 2500 Hz. RTTY
// at 45.45 baud puts that 17.4 dB below Eb/N0, so 0 dB here is a weak but copyable signal.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace RttyBench
{
    /// <summary>What one recording is made of.</summary>
    sealed class Recipe
    {
        public int Rate = 11025;
        public double Mark = 2125, Shift = 170, Baud = 45.45, StopBits = 1.5;
        public double Snr = 99;              // dB in 2500 Hz; 99 = no noise
        public string Fade = "none";
        public double Offset;                // Hz the station is off the expected mark
        public double QrmDb = -999, QrmHz;   // second station: level against ours, distance from our mark
        public int Seed = 1;
        public double Lead = 1.0;            // seconds of noise alone before and after the station
        public string Text;
        public string QrmText;

        public string Describe()
        {
            return string.Format(CultureInfo.InvariantCulture, "snr={0} fade={1} offset={2} qrm={3} stop={4}",
                Snr >= 99 ? "clean" : Snr.ToString("0.#", CultureInfo.InvariantCulture), Fade, Offset,
                QrmDb <= -999 ? "none" : QrmDb.ToString(CultureInfo.InvariantCulture) + "@" + QrmHz.ToString(CultureInfo.InvariantCulture),
                StopBits.ToString(CultureInfo.InvariantCulture));
        }
    }

    /// <summary>Baudot (ITA2) as MMTTY numbers it: the first bit on the air is the top bit of the 5.</summary>
    static class Baudot
    {
        public const int LTRS = 0x1F, FIGS = 0x1B, SPACE = 0x04, CR = 0x02, LF = 0x08;
        // The same two tables MMTTY prints from (Rtty.cpp _LTR and _FIG, S-BELL).
        static readonly char[] Ltr = {
            '\0', 'T', '\r', 'O', ' ', 'H', 'N', 'M',
            '\n', 'L', 'R', 'G', 'I', 'P', 'C', 'V',
            'E', 'Z', 'D', 'B', 'S', 'Y', 'F', 'X',
            'A', 'W', 'J', '\0', 'U', 'Q', 'K', '\0' };
        static readonly char[] Fig = {
            '\0', '5', '\r', '9', ' ', 'h', ',', '.',
            '\n', ')', '4', '&', '8', '0', ':', ';',
            '3', '"', '$', '?', 's', '6', '!', '/',
            '-', '2', '\'', '\0', '7', '1', '(', '\0' };

        // Built once from the two tables: for each printable character its code and whether it needs
        // figures (1), letters (0) or either (2: space, CR, LF).
        static readonly Dictionary<char, KeyValuePair<int, int>> Codes = Build();

        static Dictionary<char, KeyValuePair<int, int>> Build()
        {
            var d = new Dictionary<char, KeyValuePair<int, int>>();
            for (int i = 0; i < 32; i++)
            {
                char l = Ltr[i], f = Fig[i];
                if (l == ' ' || l == '\r' || l == '\n') { d[l] = new KeyValuePair<int, int>(i, 2); continue; }
                if (l != '\0') d[l] = new KeyValuePair<int, int>(i, 0);
                if (f != '\0' && f != 'h' && f != 's' && !d.ContainsKey(f)) d[f] = new KeyValuePair<int, int>(i, 1);
            }
            return d;
        }

        /// <summary>
        /// Text to Baudot codes the way MMTTY sends them (unshift-on-space on): figures are announced
        /// again after a space, since a receiver with UOS on drops back to letters there. Anything
        /// Baudot cannot carry is skipped, and the clean text that was really sent is returned.
        /// </summary>
        public static List<int> Encode(string text, out string sent)
        {
            var codes = new List<int> { LTRS, LTRS };
            var clean = new StringBuilder();
            int shift = 0;   // 0 letters, 1 figures, 2 unknown
            foreach (char raw in text.ToUpperInvariant())
            {
                KeyValuePair<int, int> c;
                if (!Codes.TryGetValue(raw, out c)) continue;
                if (c.Value != 2 && c.Value != shift)
                {
                    codes.Add(c.Value == 1 ? FIGS : LTRS);
                    shift = c.Value;
                }
                codes.Add(c.Key);
                clean.Append(raw);
                if (c.Key == SPACE && shift == 1) shift = 2;
            }
            sent = clean.ToString();
            return codes;
        }
    }

    /// <summary>Plain 16-bit mono WAV in and out.</summary>
    static class Wav
    {
        public static void Write(string path, short[] s, int rate)
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

        public static short[] Read(string path, out int rate)
        {
            byte[] b = File.ReadAllBytes(path);
            rate = BitConverter.ToInt32(b, 24);
            int channels = BitConverter.ToInt16(b, 22);
            int p = 12;
            while (p + 8 <= b.Length)
            {
                string id = Encoding.ASCII.GetString(b, p, 4);
                int len = BitConverter.ToInt32(b, p + 4);
                if (id == "data")
                {
                    int n = Math.Min(len, b.Length - p - 8) / 2 / channels;
                    var s = new short[n];
                    for (int i = 0; i < n; i++) s[i] = BitConverter.ToInt16(b, p + 8 + i * 2 * channels);
                    return s;
                }
                p += 8 + len + (len & 1);
            }
            throw new InvalidDataException("no data chunk in " + path);
        }
    }

    /// <summary>Makes a recording from a Recipe.</summary>
    static class Maker
    {
        /// <summary>
        /// The station's own audio as an analytic signal (positive frequencies only), so fading can
        /// be applied as a complex gain. Continuous phase, like both AFSK from a sound card and FSK
        /// from a radio. Mark between overs is the transmitter idling; null gaps are carrier off.
        /// </summary>
        static void Modulate(List<int> codes, Recipe r, double markHz, double startSec, double[] re, double[] im, double gain)
        {
            double fs = r.Rate;
            double bitLen = fs / r.Baud;
            double pos = startSec * fs;
            double phase = 0;
            var bits = new List<KeyValuePair<int, double>>();   // (mark?, length in bits)
            bits.Add(new KeyValuePair<int, double>(1, 8));      // a little mark before the first character
            foreach (int c in codes)
            {
                bits.Add(new KeyValuePair<int, double>(0, 1));
                for (int k = 4; k >= 0; k--) bits.Add(new KeyValuePair<int, double>((c >> k) & 1, 1));
                bits.Add(new KeyValuePair<int, double>(1, r.StopBits));
            }
            bits.Add(new KeyValuePair<int, double>(1, 8));
            foreach (var b in bits)
            {
                double end = pos + b.Value * bitLen;
                double f = b.Key == 1 ? markHz : markHz + r.Shift;
                double step = 2 * Math.PI * f / fs;
                for (int n = (int)Math.Ceiling(pos); n < end && n < re.Length; n++)
                {
                    if (n >= 0) { re[n] += gain * Math.Cos(phase); im[n] += gain * Math.Sin(phase); }
                    phase += step;
                }
                if (phase > 1e6) phase %= 2 * Math.PI;
                pos = end;
            }
        }

        public static double Seconds(string text, Recipe r)
        {
            string sent;
            int n = Baudot.Encode(text, out sent).Count;
            return (16 + n * (6 + r.StopBits)) / r.Baud;
        }

        /// <summary>Complex gain path for Watterson fading: Gaussian Doppler spectrum, unit power.</summary>
        static void FadePath(Random rnd, int n, int fs, double spreadHz, double[] gr, double[] gi)
        {
            const int lowRate = 200;
            double sigmaF = spreadHz / 2.0;
            double sigmaT = 1.0 / (2 * Math.PI * sigmaF) * lowRate;   // in low-rate samples
            int half = (int)Math.Ceiling(4 * sigmaT);
            var kern = new double[2 * half + 1];
            for (int i = -half; i <= half; i++) kern[i + half] = Math.Exp(-0.5 * i * i / (sigmaT * sigmaT));
            int m = n * lowRate / fs + 4;
            var wr = new double[m + 2 * half];
            var wi = new double[m + 2 * half];
            for (int i = 0; i < wr.Length; i++) { wr[i] = Gauss(rnd); wi[i] = Gauss(rnd); }
            var lr = new double[m];
            var li = new double[m];
            double p = 0;
            for (int i = 0; i < m; i++)
            {
                double a = 0, b = 0;
                for (int k = 0; k < kern.Length; k++) { a += wr[i + k] * kern[k]; b += wi[i + k] * kern[k]; }
                lr[i] = a; li[i] = b; p += a * a + b * b;
            }
            double norm = 1 / Math.Sqrt(p / m);
            for (int i = 0; i < n; i++)
            {
                double x = (double)i * lowRate / fs;
                int k = (int)x;
                double t = x - k;
                gr[i] = norm * (lr[k] * (1 - t) + lr[k + 1] * t);
                gi[i] = norm * (li[k] * (1 - t) + li[k + 1] * t);
            }
        }

        static double Gauss(Random rnd)
        {
            double u1 = 1.0 - rnd.NextDouble(), u2 = rnd.NextDouble();
            return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
        }

        /// <summary>ITU-R F.1487 channels: (delay between the two paths in ms, Doppler spread in Hz).</summary>
        static bool FadeModel(string name, out double delayMs, out double spreadHz)
        {
            delayMs = 0; spreadHz = 0;
            switch (name)
            {
                case "good": delayMs = 0.5; spreadHz = 0.1; return true;
                case "moderate": delayMs = 1; spreadHz = 0.5; return true;
                case "poor": delayMs = 2; spreadHz = 1; return true;
                case "flutter": delayMs = 0.5; spreadHz = 10; return true;
                default: return false;
            }
        }

        public static short[] Make(Recipe r, out string sent)
        {
            int fs = r.Rate;
            var codes = Baudot.Encode(r.Text, out sent);
            double seconds = (16 + codes.Count * (6 + r.StopBits)) / r.Baud + 2 * r.Lead;
            int n = (int)(seconds * fs);
            var re = new double[n];
            var im = new double[n];
            Modulate(codes, r, r.Mark + r.Offset, r.Lead, re, im, 1.0);

            var rnd = new Random(r.Seed);
            double delayMs, spreadHz;
            if (FadeModel(r.Fade, out delayMs, out spreadHz))
            {
                int delay = (int)Math.Round(delayMs * fs / 1000.0);
                var g1r = new double[n]; var g1i = new double[n];
                var g2r = new double[n]; var g2i = new double[n];
                FadePath(rnd, n, fs, spreadHz, g1r, g1i);
                FadePath(rnd, n, fs, spreadHz, g2r, g2i);
                var fr = new double[n]; var fi = new double[n];
                double s = Math.Sqrt(0.5);
                for (int i = 0; i < n; i++)
                {
                    double ar = re[i], ai = im[i];
                    double br = i >= delay ? re[i - delay] : 0, bi = i >= delay ? im[i - delay] : 0;
                    fr[i] = s * (g1r[i] * ar - g1i[i] * ai + g2r[i] * br - g2i[i] * bi);
                    fi[i] = s * (g1r[i] * ai + g1i[i] * ar + g2r[i] * bi + g2i[i] * br);
                }
                re = fr; im = fi;
            }

            var x = new double[n];
            for (int i = 0; i < n; i++) x[i] = re[i];   // the real audio; power 1/2 for a steady tone

            if (r.QrmDb > -999)
            {
                var qre = new double[n]; var qim = new double[n];
                string qsent;
                var q = new Recipe { Rate = r.Rate, Shift = r.Shift, Baud = r.Baud, StopBits = r.StopBits };
                Modulate(Baudot.Encode(r.QrmText, out qsent), q, r.Mark + r.QrmHz, 0.3, qre, qim, Math.Pow(10, r.QrmDb / 20));
                for (int i = 0; i < n; i++) x[i] += qre[i];
            }

            if (r.Snr < 99)
            {
                // Noise through a receiver's SSB filter (about 300-2800 Hz), sized so that the noise in
                // 2500 Hz sits Snr below the signal's power.
                double signalPower = 0.5;
                double noiseIn2500 = signalPower / Math.Pow(10, r.Snr / 10);
                double sigma = Math.Sqrt(noiseIn2500 * (fs / 2.0) / 2500.0);
                var w = new double[n];
                for (int i = 0; i < n; i++) w[i] = sigma * Gauss(rnd);
                w = BandPass(w, fs, 300, 2800);
                for (int i = 0; i < n; i++) x[i] += w[i];
            }

            // A level a radio's USB codec would give: the steady signal at about -20 dBFS.
            double scale = 0.1 * 32767 / Math.Sqrt(0.5);
            var o = new short[n];
            for (int i = 0; i < n; i++)
            {
                double v = x[i] * scale;
                o[i] = (short)Math.Max(-32767, Math.Min(32767, Math.Round(v)));
            }
            return o;
        }

        /// <summary>Windowed-sinc band-pass, and its power gain across the passband corrected to one.</summary>
        static double[] BandPass(double[] x, int fs, double lo, double hi)
        {
            const int taps = 255;
            var h = new double[taps];
            int m = taps / 2;
            for (int i = 0; i < taps; i++)
            {
                int k = i - m;
                double v = k == 0 ? 2 * (hi - lo) / fs
                    : (Math.Sin(2 * Math.PI * hi * k / fs) - Math.Sin(2 * Math.PI * lo * k / fs)) / (Math.PI * k);
                h[i] = v * (0.42 - 0.5 * Math.Cos(2 * Math.PI * i / (taps - 1)) + 0.08 * Math.Cos(4 * Math.PI * i / (taps - 1)));
            }
            var y = new double[x.Length];
            for (int n = 0; n < x.Length; n++)
            {
                double a = 0;
                for (int i = 0; i < taps; i++)
                {
                    int j = n - i + m;
                    if (j >= 0 && j < x.Length) a += x[j] * h[i];
                }
                y[n] = a;
            }
            // Noise power is spread over 0..fs/2, the band keeps (hi-lo) of it; scale so that what is in
            // the band has exactly the power it had before filtering.
            double before = 0, after = 0;
            for (int n = 0; n < x.Length; n++) { before += x[n] * x[n]; after += y[n] * y[n]; }
            double want = before * (hi - lo) / (fs / 2.0);
            double g = after > 0 ? Math.Sqrt(want / after) : 1;
            for (int n = 0; n < y.Length; n++) y[n] *= g;
            return y;
        }
    }

    /// <summary>Texts like those heard on the air: contest exchanges and ragchew overs, every callsign known.</summary>
    static class Texts
    {
        static readonly string[] Prefixes = { "DL", "OK", "G", "F", "I", "EA", "UA", "JA", "K", "W", "N", "VE", "4X", "4Z",
            "SP", "HA", "YO", "LZ", "9A", "S5", "OH", "SM", "LA", "PA", "ON", "OE", "HB9", "EI", "CT", "SV", "UR", "YU", "RA", "VK", "ZS", "PY", "LU" };
        static readonly string[] Names = { "JAN", "PETER", "MIKE", "TONY", "HANS", "YOSSI", "DAN", "IVAN", "JOSE", "KARL", "LARS", "PAOLO" };
        static readonly string[] Qths = { "PRAGUE", "MUNICH", "TEL AVIV", "LYON", "ROME", "MADRID", "MOSCOW", "OSAKA", "BOSTON", "OSLO", "VIENNA", "HAIFA" };

        public static string Call(Random r)
        {
            string p = Prefixes[r.Next(Prefixes.Length)];
            string call = p + (char)('0' + r.Next(10));
            int letters = 1 + r.Next(3);
            for (int i = 0; i < letters; i++) call += (char)('A' + r.Next(26));
            return call;
        }

        /// <summary>A run of contest QSOs as the running station's side and the callers', about seconds long.</summary>
        public static string Contest(Random r, out List<string> calls, double seconds, Recipe recipe)
        {
            calls = new List<string>();
            var sb = new StringBuilder();
            string me = Call(r);
            calls.Add(me);
            int serial = 1 + r.Next(300);
            while (Maker.Seconds(sb.ToString(), recipe) < seconds)
            {
                string dx = Call(r);
                calls.Add(dx);
                switch (r.Next(4))
                {
                    case 0: sb.Append("CQ TEST " + me + " " + me + " CQ\r\n"); break;
                    case 1: sb.Append(dx + " " + dx + "\r\n"); break;
                    case 2: sb.Append(dx + " 599 " + serial.ToString("000") + " " + serial.ToString("000") + " " + dx + "\r\n"); serial++; break;
                    default: sb.Append(me + " TU 599 " + (r.Next(40) + 1) + " " + (r.Next(40) + 1) + " " + me + "\r\n"); break;
                }
            }
            return sb.ToString();
        }

        /// <summary>A ragchew over: longer plain-language lines with names, places and figures.</summary>
        public static string Ragchew(Random r, out List<string> calls, double seconds, Recipe recipe)
        {
            calls = new List<string>();
            var sb = new StringBuilder();
            string a = Call(r), b = Call(r);
            calls.Add(a); calls.Add(b);
            sb.Append(a + " DE " + b + " " + b + " PSE K\r\n");
            while (Maker.Seconds(sb.ToString(), recipe) < seconds)
            {
                switch (r.Next(5))
                {
                    case 0: sb.Append("R R " + a + " DE " + b + " GOOD MORNING AND TNX FOR THE CALL\r\n"); break;
                    case 1: sb.Append("UR RST 5" + (5 + r.Next(5)) + "9 NAME" + Names[r.Next(Names.Length)] + " QTH " + Qths[r.Next(Qths.Length)] + "\r\n"); break;
                    case 2: sb.Append("RIG IS " + (100 + 10 * r.Next(50)) + " WATTS TO A DIPOLE AT " + (5 + r.Next(30)) + " METERS. WX " + (r.Next(35)) + " C.\r\n"); break;
                    case 3: sb.Append("QSL VIA BUREAU OR LOTW, 73 ES CUL? " + a + " DE " + b + " SK\r\n"); break;
                    default: sb.Append("BTU " + a + " DE " + b + " K\r\n"); break;
                }
            }
            return sb.ToString();
        }
    }

    /// <summary>How well a decoded text matches what was sent.</summary>
    static class Score
    {
        public static string Normalize(string s)
        {
            var sb = new StringBuilder();
            bool space = true;
            foreach (char c0 in s.ToUpperInvariant())
            {
                char c = c0 == '\r' || c0 == '\n' ? ' ' : c0;
                if (c == ' ') { if (!space) sb.Append(' '); space = true; }
                else { sb.Append(c); space = false; }
            }
            return sb.ToString().Trim();
        }

        public static int Distance(string a, string b)
        {
            var prev = new int[b.Length + 1];
            var cur = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) prev[j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                cur[0] = i;
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + cost);
                }
                var t = prev; prev = cur; cur = t;
            }
            return prev[b.Length];
        }

        /// <summary>Letters right out of letters sent: 1 - edit distance / length, never below 0.</summary>
        public static double Accuracy(string sent, string got)
        {
            string a = Normalize(sent), b = Normalize(got);
            if (a.Length == 0) return 1;
            return Math.Max(0, 1 - (double)Distance(a, b) / a.Length);
        }

        /// <summary>How many of the sent callsigns appear whole in the decoded text, each occurrence counted.</summary>
        public static void Calls(string sent, string got, out int sentCalls, out int copied, IEnumerable<string> callList)
        {
            var known = new HashSet<string>(callList);
            var sentWords = Normalize(sent).Split(' ').Where(w => known.Contains(w)).ToList();
            var gotWords = Normalize(got).Split(' ').GroupBy(w => w).ToDictionary(g => g.Key, g => g.Count());
            sentCalls = sentWords.Count;
            copied = 0;
            foreach (var g in sentWords.GroupBy(w => w))
            {
                int have;
                gotWords.TryGetValue(g.Key, out have);
                copied += Math.Min(have, g.Count());
            }
        }
    }

    static class Program
    {
        static int Main(string[] args)
        {
            try
            {
                if (args.Length < 2) { Usage(); return 1; }
                var opts = Options(args.Skip(2));
                switch (args[0])
                {
                    case "one": return One(args[1], opts);
                    case "make": return MakeSet(args[1], opts);
                    case "mmtty": return RunMmtty(args[1], opts);
                    case "ours": return RunOurs(args[1], opts);
                    case "trace": return Trace(args[1], opts);
                }
                Usage();
                return 1;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e);
                return 2;
            }
        }

        static void Usage()
        {
            Console.WriteLine("RttyBench one <out.wav> [snr=] [fade=] [offset=] [qrm=dB@Hz] [stop=] [seed=] [rate=]");
            Console.WriteLine("RttyBench make <folder> [rate=11025]");
            Console.WriteLine("RttyBench mmtty <folder|file.wav> [types=0,1,2,3] [atc=0,1] [sq=1]");
        }

        static Dictionary<string, string> Options(IEnumerable<string> a)
        {
            var d = new Dictionary<string, string>();
            foreach (string s in a)
            {
                int k = s.IndexOf('=');
                if (k > 0) d[s.Substring(0, k)] = s.Substring(k + 1);
            }
            return d;
        }

        static double D(Dictionary<string, string> o, string k, double def)
        {
            string v;
            return o.TryGetValue(k, out v) ? double.Parse(v, CultureInfo.InvariantCulture) : def;
        }

        static Recipe FromOptions(Dictionary<string, string> o)
        {
            var r = new Recipe();
            r.Rate = (int)D(o, "rate", 11025);
            r.Snr = D(o, "snr", 99);
            r.Offset = D(o, "offset", 0);
            r.StopBits = D(o, "stop", 1.5);
            r.Seed = (int)D(o, "seed", 1);
            r.Baud = D(o, "baud", 45.45);
            string v;
            if (o.TryGetValue("fade", out v)) r.Fade = v;
            if (o.TryGetValue("qrm", out v))
            {
                var p = v.Split('@');
                r.QrmDb = double.Parse(p[0], CultureInfo.InvariantCulture);
                r.QrmHz = double.Parse(p[1], CultureInfo.InvariantCulture);
            }
            return r;
        }

        // One recording: what it says goes to <name>.txt beside it, and the callsigns to <name>.calls.
        static void Produce(string wav, Recipe r, string kind, double seconds)
        {
            var rnd = new Random(r.Seed * 7919 + 17);
            List<string> calls, qcalls;
            r.Text = kind == "ragchew" ? Texts.Ragchew(rnd, out calls, seconds, r) : Texts.Contest(rnd, out calls, seconds, r);
            r.QrmText = Texts.Contest(new Random(r.Seed * 31 + 5), out qcalls, seconds + 10, r);
            string sent;
            short[] s = Maker.Make(r, out sent);
            Wav.Write(wav, s, r.Rate);
            File.WriteAllText(Path.ChangeExtension(wav, ".txt"), sent);
            File.WriteAllLines(Path.ChangeExtension(wav, ".calls"), calls.Distinct());
        }

        static int One(string wav, Dictionary<string, string> o)
        {
            var r = FromOptions(o);
            string kind;
            if (!o.TryGetValue("kind", out kind)) kind = "contest";
            Produce(wav, r, kind, D(o, "seconds", 60));
            Console.WriteLine(wav + "  " + r.Describe());
            return 0;
        }

        /// <summary>
        /// The bench: every channel at a ladder of signal strengths, contest runs and ragchews, two
        /// different texts each. Rebuilt from the seeds the same every time, so it need not be kept.
        /// </summary>
        static int MakeSet(string folder, Dictionary<string, string> o)
        {
            Directory.CreateDirectory(folder);
            int rate = (int)D(o, "rate", 11025);
            var lines = new List<string> { "file\tchannel\tsnr\tkind\tseed\trecipe" };
            double[] snrs = { -10, -8, -6, -4, -2, 0, 3, 6 };
            string[] channels = { "awgn", "moderate", "poor", "flutter", "qrm", "offset", "gaps" };
            foreach (string ch in channels)
                foreach (double snr in snrs)
                    for (int k = 0; k < 2; k++)
                    {
                        string kind = k == 0 ? "contest" : "ragchew";
                        int seed = 1000 + Array.IndexOf(channels, ch) * 100 + (int)(snr + 20) * 2 + k;
                        var r = new Recipe { Rate = rate, Snr = snr, Seed = seed };
                        if (ch == "moderate" || ch == "poor" || ch == "flutter") r.Fade = ch;
                        if (ch == "qrm") { r.QrmDb = 6; r.QrmHz = 300; }      // a louder station 300 Hz up
                        if (ch == "offset") r.Offset = 12;                    // tuned 12 Hz off
                        if (ch == "gaps") r.Lead = 10;                        // ten seconds of noise each side
                        string name = string.Format(CultureInfo.InvariantCulture, "{0}_{1}{2}_{3}.wav",
                            ch, snr < 0 ? "m" : "p", Math.Abs(snr), kind);
                        string path = Path.Combine(folder, name);
                        Produce(path, r, kind, 50);
                        lines.Add(name + "\t" + ch + "\t" + snr.ToString(CultureInfo.InvariantCulture) + "\t" + kind + "\t" + seed + "\t" + r.Describe());
                        Console.Write(".");
                    }
            File.WriteAllLines(Path.Combine(folder, "manifest.tsv"), lines);
            Console.WriteLine();
            Console.WriteLine((lines.Count - 1) + " recordings in " + folder);
            return 0;
        }

        static List<string> Wavs(string target)
        {
            if (File.Exists(target)) return new List<string> { target };
            return Directory.GetFiles(target, "*.wav").OrderBy(f => f).ToList();
        }

        /// <summary>
        /// Decodes every recording with each MMTTY demodulator asked for, scores it, and prints one
        /// line per recording and setting plus a summary by channel and strength. The decoded text is
        /// kept beside each recording as name.mmtty-TYPE.txt.
        /// </summary>
        static int RunMmtty(string target, Dictionary<string, string> o)
        {
            string v;
            int[] types = (o.TryGetValue("types", out v) ? v : "0,1,2,3").Split(',').Select(int.Parse).ToArray();
            int[] atcs = (o.TryGetValue("atc", out v) ? v : "0").Split(',').Select(int.Parse).ToArray();
            bool sq = D(o, "sq", 1) != 0;
            var decoders = new List<KeyValuePair<string, Func<short[], int, string>>>();
            foreach (int type in types)
                foreach (int atc in atcs)
                {
                    int t = type, a = atc;
                    decoders.Add(new KeyValuePair<string, Func<short[], int, string>>(
                        "MMTTY-" + MmttyReceiver.TypeNames[type] + (atc != 0 ? "+ATC" : ""),
                        (s, rate) =>
                        {
                            var rx = new MmttyReceiver(rate, t, a != 0, 2125, 170, 45.45, sq);
                            rx.Feed(s, s.Length);
                            return rx.Text;
                        }));
                }
            return RunDecoders(target, decoders, D(o, "quiet", 0) != 0);
        }

        /// <summary>
        /// Our decoder, HolyLogger's own RttyDecoder.cs. Any setting can be swept by giving a comma
        /// list: squelch=0.1,0.2,0.3 runs three times and prints the three side by side.
        /// </summary>
        // The decoder's settings the bench can sweep, by the short name used on the command line.
        // The decoder's settings the bench can sweep, by the short name used on the command line.
        static readonly string[] Knobs = { "decision", "squelch", "duesq", "mean", "open", "stay", "close", "balance", "width", "due", "afc", "hold" };

        static void Apply(HolyLogger.RttyDecoder d, string knob, double x)
        {
            switch (knob)
            {
                case "decision": d.Decision = (int)x; break;
                case "squelch": d.Squelch = x; break;
                case "duesq": d.DueSquelch = x; break;
                case "mean": d.MeanGate = x; break;
                case "open": d.OpenGate = x; break;
                case "stay": d.StayGate = x; break;
                case "close": d.CloseAfterBits = x; break;
                case "balance": d.AtcBalance = x; break;
                case "width": d.SampleWidth = x; break;
                case "due": d.UseDue = x != 0; break;
                case "afc": d.FineTuning = x != 0; break;
                case "hold": d.HoldTuningOnNoise = x != 0; break;
            }
        }

        static int RunOurs(string target, Dictionary<string, string> o)
        {
            // Every combination of the values given, e.g. decision=0,1 afc=0,1 runs four decoders.
            var combos = new List<List<KeyValuePair<string, double>>> { new List<KeyValuePair<string, double>>() };
            foreach (string knob in Knobs)
            {
                string v;
                if (!o.TryGetValue(knob, out v)) continue;
                var values = v.Split(',').Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToList();
                combos = combos.SelectMany(c => values.Select(x => new List<KeyValuePair<string, double>>(c) { new KeyValuePair<string, double>(knob, x) })).ToList();
            }
            var decoders = new List<KeyValuePair<string, Func<short[], int, string>>>();
            foreach (var combo in combos)
            {
                var settings = combo;
                string name = "Ours" + string.Concat(settings.Where(kv => o[kv.Key].Contains(",")).Select(kv => " " + kv.Key + kv.Value.ToString(CultureInfo.InvariantCulture)));
                decoders.Add(new KeyValuePair<string, Func<short[], int, string>>(name, (s, rate) =>
                {
                    var d = new HolyLogger.RttyDecoder(rate);
                    foreach (var kv in settings) Apply(d, kv.Key, kv.Value);
                    d.Tune(2125, 170, 45.45);
                    var text = new StringBuilder();
                    d.CharacterDecoded += c => text.Append(c);
                    d.Process(s, s.Length);
                    return text.ToString();
                }));
            }
            return RunDecoders(target, decoders, D(o, "quiet", 0) != 0);
        }

        /// <summary>Every frame our decoder fits in one recording, with the numbers it decides on.</summary>
        static int Trace(string wav, Dictionary<string, string> o)
        {
            int rate;
            short[] s = Wav.Read(wav, out rate);
            var d = new HolyLogger.RttyDecoder(rate);
            foreach (string knob in Knobs) { string v; if (o.TryGetValue(knob, out v)) Apply(d, knob, double.Parse(v, CultureInfo.InvariantCulture)); }
            d.Tune(2125, 170, 45.45);
            d.FrameSeen += (start, clearest, mean) => Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "{0,8:0.000} s  clearest {1:0.00}  mean {2:0.00}", (double)start / rate, clearest, mean));
            d.Process(s, s.Length);
            return 0;
        }

        static int RunDecoders(string target, List<KeyValuePair<string, Func<short[], int, string>>> decoders, bool quiet)
        {
            var results = new List<Result>();
            foreach (string wav in Wavs(target))
            {
                int rate;
                short[] s = Wav.Read(wav, out rate);
                string sent = File.Exists(Path.ChangeExtension(wav, ".txt")) ? File.ReadAllText(Path.ChangeExtension(wav, ".txt")) : "";
                var calls = File.Exists(Path.ChangeExtension(wav, ".calls")) ? File.ReadAllLines(Path.ChangeExtension(wav, ".calls")) : new string[0];
                foreach (var dec in decoders)
                {
                    string got = dec.Value(s, rate);
                    File.WriteAllText(Path.ChangeExtension(wav, "." + dec.Key.Replace(' ', '_') + ".txt"), got);
                    var res = Result.Of(wav, dec.Key, sent, got, calls);
                    results.Add(res);
                    if (!quiet) Console.WriteLine(res.Line());
                }
            }
            Result.Summary(results);
            return 0;
        }
    }

    sealed class Result
    {
        public string File, Setting, Channel, Snr;
        public double Accuracy;
        public int Calls, CallsCopied, Sent, Got;

        public static Result Of(string wav, string setting, string sent, string got, IEnumerable<string> calls)
        {
            var r = new Result { File = Path.GetFileName(wav), Setting = setting };
            string[] parts = Path.GetFileNameWithoutExtension(wav).Split('_');
            r.Channel = parts.Length > 0 ? parts[0] : "";
            r.Snr = parts.Length > 1 ? parts[1].Replace("m", "-").Replace("p", "") : "";
            r.Accuracy = Score.Accuracy(sent, got);
            Score.Calls(sent, got, out r.Calls, out r.CallsCopied, calls);
            r.Sent = Score.Normalize(sent).Length;
            r.Got = Score.Normalize(got).Length;
            return r;
        }

        public string Line()
        {
            return string.Format(CultureInfo.InvariantCulture, "{0,-34} {1,-16} letters {2,5:0.0}%  calls {3,3}/{4,-3}  ({5} sent, {6} printed)",
                File, Setting, 100 * Accuracy, CallsCopied, Calls, Sent, Got);
        }

        static double SnrKey(string s)
        {
            double d;
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : 999;
        }

        /// <summary>Calls copied, per channel and strength, one column per setting.</summary>
        public static void Summary(List<Result> all)
        {
            var settings = all.Select(r => r.Setting).Distinct().ToList();
            Console.WriteLine();
            Console.WriteLine("CALLSIGNS COPIED (letters right in brackets)");
            Console.Write("{0,-10} {1,5}", "channel", "snr");
            foreach (string s in settings) Console.Write(" {0,20}", s);
            Console.WriteLine();
            foreach (var g in all.GroupBy(r => r.Channel + "|" + r.Snr).OrderBy(g => g.First().Channel).ThenBy(g => SnrKey(g.First().Snr)))
            {
                Console.Write("{0,-10} {1,5}", g.First().Channel, g.First().Snr);
                foreach (string s in settings)
                {
                    var rs = g.Where(r => r.Setting == s).ToList();
                    int c = rs.Sum(r => r.CallsCopied), n = rs.Sum(r => r.Calls);
                    double acc = rs.Count > 0 ? rs.Sum(r => r.Accuracy * r.Sent) / Math.Max(1, rs.Sum(r => r.Sent)) : 0;
                    Console.Write(" {0,20}", string.Format(CultureInfo.InvariantCulture, "{0}/{1} ({2:0}%)", c, n, 100 * acc));
                }
                Console.WriteLine();
            }
            Console.Write("{0,-16}", "TOTAL");
            foreach (string s in settings)
            {
                var rs = all.Where(r => r.Setting == s).ToList();
                int c = rs.Sum(r => r.CallsCopied), n = rs.Sum(r => r.Calls);
                double acc = rs.Sum(r => r.Accuracy * r.Sent) / Math.Max(1, rs.Sum(r => r.Sent));
                Console.Write(" {0,20}", string.Format(CultureInfo.InvariantCulture, "{0}/{1} ({2:0}%)", c, n, 100 * acc));
            }
            Console.WriteLine();
        }
    }
}
