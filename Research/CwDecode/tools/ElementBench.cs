using System;
using System.IO;
using System.Text;

namespace HolyLogger
{
    static class Log { public static void Swallow(Exception e) { } }

    // THE NEW DECODER (CwElementDecoder) over every recording in a folder, fed 100 ms at a time as it
    // is live, printing "name<TAB>text" for ScoreText.py - and, on the error stream, how long it took.
    // Copied from BulletinText.cs, which does the same for the plain decoder:
    //
    // Decodes every recording in a folder and prints "name<TAB>what the decoder made of it", one
    // line each, for ScoreText.py to score against W1AW's published words.
    //
    // Separate from RealBench because this is the big test: forty minutes of real off-air CW whose
    // exact text ARRL publishes, against which a change can be judged on thousands of words instead
    // of the thirty-four the older bench could be sure of.
    static class ElementBench
    {
        static void Main(string[] args)
        {
            BenchSwitches.Apply();
            string folder = args[0];
            string only = args.Length > 1 ? args[1] : null;
            string hold = Environment.GetEnvironmentVariable("HOLD_BACK");
            if (!string.IsNullOrEmpty(hold)) CwElementDecoder.HoldBackSeconds = double.Parse(hold, System.Globalization.CultureInfo.InvariantCulture);
            string gm = Environment.GetEnvironmentVariable("GATE_MODE");
            if (!string.IsNullOrEmpty(gm)) CwElementDecoder.GateMode = int.Parse(gm);
            string sp = Environment.GetEnvironmentVariable("SPEED_FROM_PLAIN");
            if (!string.IsNullOrEmpty(sp)) CwElementDecoder.SpeedFromPlain = int.Parse(sp);
            string nfp = Environment.GetEnvironmentVariable("NOTE_FROM_PLAIN");
            if (!string.IsNullOrEmpty(nfp)) CwElementDecoder.NoteFromPlain = nfp == "1";
            string lk = Environment.GetEnvironmentVariable("TONE_LEAK");
            if (!string.IsNullOrEmpty(lk)) CwElementDecoder.Leak = double.Parse(lk, System.Globalization.CultureInfo.InvariantCulture);
            string al = Environment.GetEnvironmentVariable("ADAPTIVE_LEAK");
            if (!string.IsNullOrEmpty(al)) CwElementDecoder.AdaptiveLeak = al == "1";
            string wg = Environment.GetEnvironmentVariable("WORDGAP_NO_PAUSES");
            if (!string.IsNullOrEmpty(wg)) CwElementDecoder.WordGapWithoutPauses = wg == "1";
            string lg = Environment.GetEnvironmentVariable("LETTER_GATE");
            if (!string.IsNullOrEmpty(lg)) CwElementDecoder.LetterGate = double.Parse(lg, System.Globalization.CultureInfo.InvariantCulture);
            string ig = Environment.GetEnvironmentVariable("INWORD_GATE");
            if (!string.IsNullOrEmpty(ig)) CwElementDecoder.InWordGate = double.Parse(ig, System.Globalization.CultureInfo.InvariantCulture);
            string sg = Environment.GetEnvironmentVariable("SHORT_GATE");
            if (!string.IsNullOrEmpty(sg)) CwElementDecoder.ShortLetterGate = double.Parse(sg, System.Globalization.CultureInfo.InvariantCulture);
            string letterLog = Environment.GetEnvironmentVariable("LETTER_LOG");
            string sdb = Environment.GetEnvironmentVariable("STRONG_DB");
            if (!string.IsNullOrEmpty(sdb)) CwElementDecoder.StrongDb = double.Parse(sdb, System.Globalization.CultureInfo.InvariantCulture);
            string lt = Environment.GetEnvironmentVariable("LEARN_TIMING");
            if (!string.IsNullOrEmpty(lt)) CwElementDecoder.LearnOperatorTiming = lt == "1";
            string slo = Environment.GetEnvironmentVariable("SPEED_LETTERS_ONLY");
            if (!string.IsNullOrEmpty(slo)) CwElementDecoder.SpeedFromLettersOnly = slo == "1";
            string rr = Environment.GetEnvironmentVariable("REREAD");
            if (!string.IsNullOrEmpty(rr)) CwElementDecoder.ReReadOnSpeedChange = rr == "1";
            string cbs = Environment.GetEnvironmentVariable("CONFIRM_SPEED");
            if (!string.IsNullOrEmpty(cbs)) CwElementDecoder.ConfirmBigSpeedChange = cbs == "1";
            string prt = Environment.GetEnvironmentVariable("PART_RUNS");
            if (!string.IsNullOrEmpty(prt)) CwElementDecoder.PartRunsTogether = prt == "1";
            string sfo = Environment.GetEnvironmentVariable("SHORT_FIRST_ONLY");
            if (!string.IsNullOrEmpty(sfo)) CwElementDecoder.ShortGateFirstOnly = sfo == "1";
            string nh = Environment.GetEnvironmentVariable("NEIGHBOUR_HZ");
            if (!string.IsNullOrEmpty(nh)) CwElementDecoder.NeighbourHz = double.Parse(nh, System.Globalization.CultureInfo.InvariantCulture);
            string ms = Environment.GetEnvironmentVariable("MIN_STEADY");
            if (!string.IsNullOrEmpty(ms)) CwElementDecoder.MinSteadiness = double.Parse(ms, System.Globalization.CultureInfo.InvariantCulture);
            string mb = Environment.GetEnvironmentVariable("MIN_BESIDE");
            if (!string.IsNullOrEmpty(mb)) CwElementDecoder.MinBeside = double.Parse(mb, System.Globalization.CultureInfo.InvariantCulture);
            string mg = Environment.GetEnvironmentVariable("MAX_GAPFILL");
            if (!string.IsNullOrEmpty(mg)) CwElementDecoder.MaxGapFill = double.Parse(mg, System.Globalization.CultureInfo.InvariantCulture);
            string cf = Environment.GetEnvironmentVariable("CARRIER_FILL");
            if (!string.IsNullOrEmpty(cf)) CwElementDecoder.CarrierGapFill = double.Parse(cf, System.Globalization.CultureInfo.InvariantCulture);
            string lm = Environment.GetEnvironmentVariable("LEAK_MARGIN");
            if (!string.IsNullOrEmpty(lm)) CwElementDecoder.LeakMargin = int.Parse(lm);
            string sl = Environment.GetEnvironmentVariable("STRONG_LETTER");
            if (!string.IsNullOrEmpty(sl)) CwElementDecoder.StrongLetter = double.Parse(sl, System.Globalization.CultureInfo.InvariantCulture);

            foreach (string path in Directory.GetFiles(folder, "*.wav"))
            {
                string name = Path.GetFileNameWithoutExtension(path);
                if (only != null && name.IndexOf(only, StringComparison.OrdinalIgnoreCase) < 0) continue;

                int rate;
                short[] audio = ReadWav(path, out rate);
                var decoder = new CwElementDecoder(rate, false); var clock = System.Diagnostics.Stopwatch.StartNew();
                var got = new StringBuilder();
                decoder.Text += s => got.Append(s);
                if (!string.IsNullOrEmpty(letterLog))
                {
                    var dd = decoder; string nm = name;
                    decoder.Judged = (pat, end, ev, shown) =>
                    {
                        if (!shown) return;
                        File.AppendAllText(letterLog, string.Format(System.Globalization.CultureInfo.InvariantCulture,
                            "{0}\t{1}\t{2:F3}\t{3}\t{4}\t{5}\t{6:F3}\t{7:F2}\t{8:F3}\n", nm, pat, ev, dd.LastJudgedInWord ? 1 : 0, dd.LastJudgedPlainHeard ? 1 : 0, end, dd.LastSteadiness, dd.LastBeside, dd.LastGapFill));
                    };
                }

                int block = rate / 10;
                var buf = new short[block];
                for (int i = 0; i < audio.Length; i += block)
                {
                    int n = Math.Min(block, audio.Length - i);
                    Array.Copy(audio, i, buf, 0, n);
                    decoder.Process(buf, n);
                }

                decoder.Finish();
                Console.Error.WriteLine(string.Format("{0}: {1:F1} s of audio in {2:F1} s", name, audio.Length / (double)rate, clock.Elapsed.TotalSeconds));
                Console.WriteLine(name + "\t" + got.ToString());
            }
        }

        static short[] ReadWav(string path, out int rate)
        {
            using (var f = File.OpenRead(path))
            using (var r = new BinaryReader(f))
            {
                r.ReadBytes(4); r.ReadInt32(); r.ReadBytes(4);
                rate = 8000; short channels = 1, bits = 16; short[] data = null;
                while (f.Position < f.Length - 8)
                {
                    string id = new string(r.ReadChars(4));
                    int size = r.ReadInt32();
                    long next = f.Position + size + (size & 1);
                    if (id == "fmt ")
                    {
                        r.ReadInt16(); channels = r.ReadInt16(); rate = r.ReadInt32();
                        r.ReadInt32(); r.ReadInt16(); bits = r.ReadInt16();
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
