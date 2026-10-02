using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace HolyLogger
{
    static class Log { public static void Swallow(Exception e) { } }

    // THE WATCHER (his idea, 2026-10-02): listens to the radio all the time, runs Plain and New side by
    // side exactly as the decode window does, and KEEPS ONLY THE SOUND WHERE THEY DISAGREE. Everything
    // else is thrown away, so it can run for hours and leave behind a folder of cases to study, without
    // him having to say "record" at the right moment.
    //
    // Every 10 s it judges the block that ended 30 s ago (by then both readers have printed everything
    // in it - New prints up to ~2 s late). A block is interesting when the two spell it differently by
    // MinDiff letters or more. Interesting blocks run together into one clip, with 20 s of sound kept
    // either side; a clip closes once 30 s pass with no more disagreement, or at MaxClipSeconds.
    // Each clip is a WAV plus a letters TSV in the same format as ReplayLetters, so ReplayStrip.py and
    // Disagree.py work on it directly.
    //
    //   Watcher.exe <device> <out folder> [hours, default 12] [max MB, default 1000]
    static class Watcher
    {
        const int BlockSeconds = 10, SettleSeconds = 30, PadSeconds = 20, QuietSeconds = 30;
        const int MaxClipSeconds = 240, RingSeconds = 360, MinDiff = 2;

        struct Letter { public string Reader, Text; public long Sample, Start, End; }

        static int rate;
        static short[] ring;
        static long written;                              // samples ever fed (absolute clock)
        static readonly List<Letter> letters = new List<Letter>();
        static readonly object gate = new object();

        static void Main(string[] args)
        {
            string device = args.Length > 0 ? args[0] : "";
            string outDir = args.Length > 1 ? args[1] : "watch";
            double hours = args.Length > 2 ? double.Parse(args[2], CultureInfo.InvariantCulture) : 12;
            long maxBytes = (args.Length > 3 ? long.Parse(args[3]) : 1000) * 1024L * 1024L;
            Directory.CreateDirectory(outDir);
            string logPath = Path.Combine(outDir, "watch.log");

            var queue = new BlockingCollection<short[]>(2000);
            var recorder = new WaveInRecorder();
            recorder.Samples += (samples, count) =>
            {
                var copy = new short[count];
                Array.Copy(samples, copy, count);
                queue.TryAdd(copy);
            };
            recorder.Failed += m => Say(logPath, "CAPTURE STOPPED: " + m);

            string error;
            if (!recorder.Start(device, out error)) { Console.WriteLine("Could not start: " + error); return; }
            rate = recorder.ActualSampleRate;
            ring = new short[rate * RingSeconds];
            Say(logPath, "Watching " + recorder.ActualDeviceName + " at " + rate + " Hz for " + hours + " h");

            var plain = new CwDecoder(rate);
            var element = new CwElementDecoder(rate, false);
            plain.LetterTimed += (l, s, e) => Add("Plain", l, s, e);
            element.LetterTimed += (l, s, e) => Add("New", l, s, e);

            DateTime stopAt = DateTime.UtcNow.AddHours(hours);
            int block = Math.Max(1, rate / 200);
            var buf = new short[block];
            long nextJudge = (long)rate * (BlockSeconds + SettleSeconds);
            long clipStart = -1, clipLastHit = -1;
            DateTime clipWall = DateTime.UtcNow;
            long savedBytes = 0;
            int clips = 0;

            while (DateTime.UtcNow < stopAt && savedBytes < maxBytes)
            {
                short[] chunk;
                if (!queue.TryTake(out chunk, 1000)) continue;
                for (int i = 0; i < chunk.Length; i += block)
                {
                    int n = Math.Min(block, chunk.Length - i);
                    Array.Copy(chunk, i, buf, 0, n);
                    for (int k = 0; k < n; k++) ring[(written + k) % ring.Length] = buf[k];
                    written += n;
                    plain.Process(buf, n);
                    element.Process(buf, n);
                }

                if (written < nextJudge) continue;
                long bEnd = written - (long)rate * SettleSeconds, bStart = bEnd - (long)rate * BlockSeconds;
                nextJudge += (long)rate * BlockSeconds;

                string p, q;
                Spell(bStart, bEnd, out p, out q);
                int diff = Distance(p, q);
                // A CLEAR SIGNAL PLAIN DID NOT READ (2026-10-02, the Plain work): the waterfall's own view
                // of the block - a keyed tone well over the noise - says roughly how many letters were
                // sent; Plain printing under half of them is a miss worth keeping, agree or not.
                int marks; double snr;
                bool clear = ClearKeyedTone(bStart, bEnd, out marks, out snr);
                int expected = (int)(marks / 2.6);
                bool plainMiss = clear && p.Length < expected / 2;
                if (plainMiss)
                    Say(logPath, string.Format("  PLAIN MISS: clear tone {0:F0} dB, ~{1} letters sent, Plain [{2}]  New [{3}]", snr, expected, p, q));
                if (diff >= MinDiff || plainMiss)
                {
                    if (clipStart < 0)
                    {
                        clipStart = Math.Max(0, bStart - (long)rate * PadSeconds);
                        clipWall = DateTime.UtcNow.AddSeconds(-(written - clipStart) / (double)rate);
                    }
                    clipLastHit = bEnd;
                    Say(logPath, string.Format("  diff {0}  Plain [{1}]  New [{2}]", diff, p, q));
                }

                if (clipStart >= 0)
                {
                    long end = clipLastHit + (long)rate * PadSeconds;
                    bool quiet = bEnd - clipLastHit >= (long)rate * QuietSeconds;
                    bool tooLong = end - clipStart >= (long)rate * MaxClipSeconds;
                    if ((quiet || tooLong) && written - end >= 0)
                    {
                        savedBytes += SaveClip(outDir, logPath, clipStart, Math.Min(end, written), clipWall);
                        clips++;
                        clipStart = -1;
                    }
                }

                lock (gate) letters.RemoveAll(x => x.Sample < written - (long)rate * RingSeconds);
            }

            recorder.Stop();
            Say(logPath, "Stopped: " + clips + " clips, " + savedBytes / 1024 / 1024 + " MB");
        }

        static void Add(string reader, string text, long start, long end)
        {
            lock (gate) letters.Add(new Letter { Reader = reader, Text = text, Sample = (start + end) / 2, Start = start, End = end });
        }

        static void Spell(long from, long to, out string plain, out string neu)
        {
            var p = new StringBuilder(); var q = new StringBuilder();
            lock (gate)
            {
                var inBlock = letters.FindAll(x => x.Sample >= from && x.Sample < to);
                inBlock.Sort((a, b) => a.Sample.CompareTo(b.Sample));
                foreach (var x in inBlock) (x.Reader == "Plain" ? p : q).Append(x.Text);
            }
            plain = p.ToString(); neu = q.ToString();
        }

        // The waterfall's view of a block: 32 ms pieces every 10 ms, 300-1000 Hz. The loudest note over
        // the block, and per piece whether it stands 9 dB over that piece's median across the band.
        // Clear = at least 15 marks, keyed between 20% and 75% of the time (not a carrier), and the
        // marks' median 12 dB or more over the noise.
        static bool ClearKeyedTone(long from, long to, out int marks, out double snrDb)
        {
            marks = 0; snrDb = 0;
            int n = 256, hop = rate / 100;
            int lo = (int)Math.Ceiling(300.0 * n / rate), hi = (int)Math.Floor(1000.0 * n / rate);
            int bins = hi - lo + 1;
            var frames = new List<double[]>();
            var win = new double[n];
            for (int i = 0; i < n; i++) win[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (n - 1));
            var cosT = new double[bins, n]; var sinT = new double[bins, n];
            for (int b = 0; b < bins; b++)
                for (int i = 0; i < n; i++) { double a = 2 * Math.PI * (lo + b) * i / n; cosT[b, i] = Math.Cos(a) * win[i]; sinT[b, i] = Math.Sin(a) * win[i]; }
            var x = new double[n];
            for (long a = from; a + n <= to; a += hop)
            {
                for (int i = 0; i < n; i++) x[i] = ring[(a + i) % ring.Length];
                var pw = new double[bins];
                for (int b = 0; b < bins; b++)
                {
                    double re = 0, im = 0;
                    for (int i = 0; i < n; i++) { re += x[i] * cosT[b, i]; im += x[i] * sinT[b, i]; }
                    pw[b] = re * re + im * im;
                }
                frames.Add(pw);
            }
            if (frames.Count < 100) return false;
            var mean = new double[bins];
            foreach (var f in frames) for (int b = 0; b < bins; b++) mean[b] += f[b];
            int peak = 0; for (int b = 1; b < bins; b++) if (mean[b] > mean[peak]) peak = b;
            // A mark counts only if it lasts 30 ms or more: noise flickers over the line for a reading
            // or two at a time, and counting those turned an empty stretch into "49 letters sent".
            int on = 0, run = 0; bool was = false; var markSnr = new List<double>();
            foreach (var f in frames)
            {
                var sorted = (double[])f.Clone(); Array.Sort(sorted);
                double floor = Math.Max(1e-9, sorted[bins / 2]);
                double p = f[peak];
                if (peak > 0) p = Math.Max(p, f[peak - 1]);
                if (peak < bins - 1) p = Math.Max(p, f[peak + 1]);
                bool isOn = p > 8 * floor;
                if (isOn) { on++; markSnr.Add(10 * Math.Log10(p / floor)); }
                run = isOn ? run + 1 : 0;
                if (run == 3) marks++;
                was = isOn;
            }
            double duty = on / (double)frames.Count;
            if (markSnr.Count > 0) { markSnr.Sort(); snrDb = markSnr[markSnr.Count / 2]; }
            return marks >= 15 && duty >= 0.2 && duty <= 0.75 && snrDb >= 12;
        }

        static int Distance(string a, string b)
        {
            var d = new int[a.Length + 1, b.Length + 1];
            for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
            for (int j = 0; j <= b.Length; j++) d[0, j] = j;
            for (int i = 1; i <= a.Length; i++)
                for (int j = 1; j <= b.Length; j++)
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            return d[a.Length, b.Length];
        }

        static long SaveClip(string outDir, string logPath, long from, long to, DateTime wall)
        {
            from = Math.Max(from, written - ring.Length + 1);
            string name = "diff_" + wall.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            int count = (int)(to - from);
            var bytes = new byte[count * 2];
            for (int i = 0; i < count; i++)
            {
                short s = ring[(from + i) % ring.Length];
                bytes[2 * i] = (byte)(s & 0xFF); bytes[2 * i + 1] = (byte)((s >> 8) & 0xFF);
            }
            using (var f = new FileStream(Path.Combine(outDir, name + ".wav"), FileMode.Create))
            using (var w = new BinaryWriter(f))
            {
                w.Write(new[] { 'R', 'I', 'F', 'F' }); w.Write(36 + bytes.Length);
                w.Write(new[] { 'W', 'A', 'V', 'E' }); w.Write(new[] { 'f', 'm', 't', ' ' });
                w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2);
                w.Write((short)2); w.Write((short)16);
                w.Write(new[] { 'd', 'a', 't', 'a' }); w.Write(bytes.Length); w.Write(bytes);
            }

            // Letters with samples counted from the clip's own start, as ReplayLetters writes them.
            var tsv = new StringBuilder("reader\tletter\tsample\tstart\tend\n");
            lock (gate)
            {
                var inClip = letters.FindAll(x => x.Sample >= from && x.Sample < to);
                inClip.Sort((a, b) => a.Sample.CompareTo(b.Sample));
                foreach (var x in inClip)
                    tsv.AppendFormat(CultureInfo.InvariantCulture, "{0}\t{1}\t{2}\t{3}\t{4}\n",
                        x.Reader, x.Text, x.Sample - from, x.Start - from, x.End - from);
            }
            File.WriteAllText(Path.Combine(outDir, name + "_letters.tsv"), tsv.ToString());
            Say(logPath, "SAVED " + name + " (" + (count / (double)rate).ToString("F0", CultureInfo.InvariantCulture) + " s)");
            return bytes.Length;
        }

        static void Say(string logPath, string line)
        {
            line = DateTime.UtcNow.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + line;
            Console.WriteLine(line);
            try { File.AppendAllText(logPath, line + Environment.NewLine); } catch (IOException) { }
        }
    }
}
