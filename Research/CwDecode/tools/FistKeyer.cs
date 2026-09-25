using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

// FISTKEYER - KEYS HIS RADIO THE WAY PEOPLE DO, NOT THE WAY MACHINES DO, AND WRITES DOWN EVERY EDGE.
//
// Every recording with a known text so far was machine-sent: his radio's keyer, W1AW. Much of what the
// decoder meets on the air is sent by hand - dahs stretched or clipped, gaps too long, a pause in the
// middle of a callsign, a speed that wanders. This keys his IC-7610 through the same kind of line
// HolyLogger's "CW via" uses (a COM port's DTR, COM4 at his station) with timing drawn from a model of
// a human fist, and records the exact moment of every key-down and key-up. European KiwiSDRs record the
// air at the same time, so the decoder can be scored - and a network taught - on hand-sent CW where
// every letter AND every millisecond is known.
//
// NOT PART OF HOLYLOGGER. A research tool, run while he sits at the radio as the licensed operator.
// Every part it sends starts and ends with his callsign.
//
// SAFETY, the same rules as HolyLogger's PortCwKeyer - a key left down is a transmitter left on:
//   - the line is off before the port opens, and released after every element whatever happens;
//   - a watchdog lets go of a key held longer than any element can last;
//   - a file named STOP in the output folder stops it at the next element;
//   - a hard limit on the whole run (--minutes);
//   - Ctrl+C, closing the window or killing the process all drop the line (Windows does that).
//
//   FistKeyer.exe make  <plan.txt> <parts> <seed>             write a plan: a fist and a text per part
//   FistKeyer.exe dry   <plan.txt> <outdir>                   time it all, touch nothing
//   FistKeyer.exe key   <plan.txt> <outdir> <COMn> [DTR|RTS] [--minutes N]   key the radio
//
// Output in <outdir>: SENT.txt (the parts, one per line), and edges.tsv - for every element the part,
// the letter, its position in the text, down and up times in ms planned and actual.
static class FistKeyer
{
    // ---------------------------------------------------------------- Morse
    static readonly Dictionary<char, string> Morse = new Dictionary<char, string>
    {
        {'A',".-"},{'B',"-..."},{'C',"-.-."},{'D',"-.."},{'E',"."},{'F',"..-."},{'G',"--."},{'H',"...."},
        {'I',".."},{'J',".---"},{'K',"-.-"},{'L',".-.."},{'M',"--"},{'N',"-."},{'O',"---"},{'P',".--."},
        {'Q',"--.-"},{'R',".-."},{'S',"..."},{'T',"-"},{'U',"..-"},{'V',"...-"},{'W',".--"},{'X',"-..-"},
        {'Y',"-.--"},{'Z',"--.."},{'0',"-----"},{'1',".----"},{'2',"..---"},{'3',"...--"},{'4',"....-"},
        {'5',"....."},{'6',"-...."},{'7',"--..."},{'8',"---.."},{'9',"----."},{'/',"-..-."},{'=',"-...-"},
        {'?',"..--.."},{'.',".-.-.-"},{',',"--..--"}
    };

    // ---------------------------------------------------------------- fists
    //
    // What makes one operator's sending his own. Lengths are in units of the dit at his speed.
    //   Dah        how long his dah is (3 by the book; hand senders run 2.6 to 4)
    //   Dit        how long his dit is (1 by the book; a heavy hand holds them longer)
    //   ElemGap    the gap inside a letter (1 by the book; some clip it, some drag)
    //   LetterGap  the gap between letters (3 by the book; many leave 4 or 5)
    //   WordGap    the gap between words (7 by the book; hand senders wander widely)
    //   Jitter     how much each single length scatters, as a fraction
    //   Drift      how far the speed wanders over a part, as a fraction
    //   Hesitate   the chance of a longer pause between two letters inside a word - "HB9 D NP"
    //   BugDits    a semi-automatic key: the dits are made by the key, machine-steady; only the dahs
    //              and the gaps are the hand's
    sealed class Fist
    {
        public string Name; public double Wpm, Dah, Dit, ElemGap, LetterGap, WordGap, Jitter, Drift, Hesitate;
        public bool BugDits;
        public Fist Copy() { return (Fist)MemberwiseClone(); }
    }

    static readonly Fist[] Fists =
    {
        new Fist { Name = "careful",  Wpm = 16, Dah = 3.2, Dit = 1.05, ElemGap = 1.0,  LetterGap = 3.6, WordGap = 8.0,  Jitter = 0.08, Drift = 0.05, Hesitate = 0.02 },
        new Fist { Name = "average",  Wpm = 21, Dah = 3.3, Dit = 1.0,  ElemGap = 1.0,  LetterGap = 3.4, WordGap = 7.5,  Jitter = 0.12, Drift = 0.08, Hesitate = 0.04 },
        new Fist { Name = "fast",     Wpm = 28, Dah = 3.0, Dit = 0.95, ElemGap = 0.9,  LetterGap = 3.0, WordGap = 6.5,  Jitter = 0.12, Drift = 0.06, Hesitate = 0.02 },
        new Fist { Name = "heavy",    Wpm = 19, Dah = 3.8, Dit = 1.25, ElemGap = 0.8,  LetterGap = 3.2, WordGap = 7.0,  Jitter = 0.14, Drift = 0.08, Hesitate = 0.05 },
        new Fist { Name = "clipped",  Wpm = 23, Dah = 2.6, Dit = 0.85, ElemGap = 1.25, LetterGap = 3.0, WordGap = 7.0,  Jitter = 0.14, Drift = 0.07, Hesitate = 0.03 },
        new Fist { Name = "bug",      Wpm = 25, Dah = 3.6, Dit = 1.0,  ElemGap = 1.0,  LetterGap = 3.5, WordGap = 8.0,  Jitter = 0.16, Drift = 0.05, Hesitate = 0.04, BugDits = true },
        new Fist { Name = "sloppy",   Wpm = 18, Dah = 3.4, Dit = 1.1,  ElemGap = 1.15, LetterGap = 4.2, WordGap = 9.5,  Jitter = 0.22, Drift = 0.12, Hesitate = 0.08 },
        new Fist { Name = "hesitant", Wpm = 20, Dah = 3.2, Dit = 1.0,  ElemGap = 1.0,  LetterGap = 3.4, WordGap = 8.0,  Jitter = 0.12, Drift = 0.08, Hesitate = 0.18 },
    };

    // ---------------------------------------------------------------- texts
    //
    // What people actually send: callsigns (many, from everywhere, some with strokes), reports, names,
    // places, rigs, weather, numbers, and the Q-codes and abbreviations of a ragchew. Different every
    // part, so a network trained on some parts has never seen the words of the others.
    static readonly string[] Prefixes = { "DL", "G", "F", "I", "EA", "OK", "SP", "HA", "YO", "LZ", "SV", "9A", "S5", "OE", "HB9", "ON", "PA", "OZ", "SM", "LA", "OH", "ES", "YL", "LY", "UR", "UA", "RA", "4X", "4Z", "5B", "SU", "A6", "JA", "VK", "ZL", "W", "K", "N", "AA", "VE", "PY", "LU", "ZS", "EI", "GM", "CT", "EA8", "IS0", "TA", "UN" };
    static readonly string[] Names = { "DAN", "JOHN", "PETER", "MARIO", "HANS", "JAN", "PAVEL", "YURI", "MIKE", "TOM", "BOB", "LUIS", "ANDRE", "NICK", "KURT", "OLE", "LARS", "ARI", "SAM", "EVA", "ANNA", "MOSHE", "AVI", "RON", "GIL", "TONY", "JOSE", "IVAN", "BORIS", "MARC" };
    static readonly string[] Places = { "BERLIN", "PARIS", "ROME", "MADRID", "PRAGUE", "WARSAW", "VIENNA", "ZURICH", "OSLO", "HELSINKI", "RIGA", "KIEV", "MOSCOW", "ATHENS", "SOFIA", "HAIFA", "TEL AVIV", "NICOSIA", "CAIRO", "TOKYO", "SYDNEY", "BOSTON", "OHIO", "TEXAS", "LISBON", "DUBLIN", "MILAN", "LYON", "MUNICH", "LEEDS" };
    static readonly string[] Rigs = { "IC7610", "IC7300", "FT991", "FTDX10", "K3", "TS590", "FT817", "IC705", "KX3", "FTDX101" };
    static readonly string[] Ants = { "DIPOLE", "YAGI", "VERTICAL", "LOOP", "WINDOM", "BEAM", "LONG WIRE", "HEXBEAM" };
    static readonly string[] Wx = { "SUNNY", "CLOUDY", "RAIN", "WARM", "COLD", "WINDY", "FOG", "HOT", "SNOW" };
    static readonly string[] Words = { "TNX", "FER", "CALL", "UR", "SIGS", "FB", "OM", "HR", "ES", "PSE", "AGN", "QSL", "VIA", "BURO", "LOTW", "HPE", "CUAGN", "GL", "DX", "73", "88", "QRM", "QSB", "QRN", "RIG", "ANT", "PWR", "WATTS", "WX", "TEMP", "NAME", "QTH", "RST", "CONDX", "BAND", "OPEN", "NICE", "SOLID", "COPY", "AR", "BK" };

    static string Call(Random r)
    {
        string p = Prefixes[r.Next(Prefixes.Length)];
        string call = p + (char.IsDigit(p[p.Length - 1]) ? "" : r.Next(10).ToString(CultureInfo.InvariantCulture));
        int n = 1 + r.Next(3);
        for (int i = 0; i < n; i++) call += (char)('A' + r.Next(26));
        double s = r.NextDouble();
        if (s < 0.06) call += "/P";
        else if (s < 0.10) call += "/M";
        else if (s < 0.13) call = Prefixes[r.Next(Prefixes.Length)] + "/" + call;
        return call;
    }

    static string Rst(Random r)
    {
        return (5).ToString() + (3 + r.Next(7)).ToString() + "9";
    }

    static string MakeText(Random r)
    {
        var parts = new List<string>();
        string other = Call(r);
        int kind = r.Next(4);
        if (kind == 0)
        {
            parts.Add(other + " DE 4Z5SL");
            parts.Add("TNX FER CALL UR RST " + Rst(r) + " " + Rst(r));
            parts.Add("NAME " + Names[r.Next(Names.Length)] + " QTH " + Places[r.Next(Places.Length)]);
            parts.Add("HW CPY?");
        }
        else if (kind == 1)
        {
            parts.Add("R R TNX " + Names[r.Next(Names.Length)] + " FB");
            parts.Add("RIG " + Rigs[r.Next(Rigs.Length)] + " PWR " + (5 + 5 * r.Next(20)) + " W ANT " + Ants[r.Next(Ants.Length)]);
            parts.Add("WX " + Wx[r.Next(Wx.Length)] + " TEMP " + (5 + r.Next(30)) + " C");
            parts.Add("CALLS " + Call(r) + " " + Call(r) + " " + Call(r));
        }
        else if (kind == 2)
        {
            parts.Add("TEST " + Call(r) + " " + Rst(r) + " " + (1 + r.Next(999)).ToString("000"));
            parts.Add(Call(r) + " " + Rst(r) + " " + (1 + r.Next(999)).ToString("000"));
            parts.Add(Call(r) + " " + Rst(r) + " " + (1 + r.Next(999)).ToString("000"));
            parts.Add("QRZ? " + Call(r));
        }
        else
        {
            var w = new List<string>();
            for (int i = 0; i < 10; i++) w.Add(Words[r.Next(Words.Length)]);
            parts.Add(string.Join(" ", w));
            parts.Add("CALLS " + Call(r) + " " + Call(r));
            parts.Add("NR " + r.Next(100) + " " + r.Next(1000) + " " + r.Next(10000));
        }
        return "DE 4Z5SL = " + string.Join(" = ", parts) + " = DE 4Z5SL K";
    }

    // ---------------------------------------------------------------- the schedule
    sealed class Edge
    {
        public int Part, TextIndex; public char Letter; public double DownMs, UpMs;
        public double ActualDown = double.NaN, ActualUp = double.NaN;
    }

    static double Gauss(Random r)
    {
        double u1 = 1.0 - r.NextDouble(), u2 = r.NextDouble();
        return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }

    // One part's edges, starting at startMs. Every length drawn from the fist, never under half its
    // own size - a hand is sloppy, not impossible.
    static List<Edge> Schedule(int part, string text, Fist fist, Random r, double startMs, out double endMs)
    {
        var edges = new List<Edge>();
        double t = startMs;
        double speedWalk = 0;
        bool afterLetter = false;
        Func<double, double, double> draw = (units, jitter) =>
        {
            double f = 1 + jitter * Gauss(r);
            if (f < 0.5) f = 0.5;
            return units * f;
        };

        for (int i = 0; i < text.Length; i++)
        {
            char c = char.ToUpperInvariant(text[i]);
            // the speed wanders slowly across the part
            speedWalk = 0.9 * speedWalk + 0.1 * Gauss(r);
            double unit = 1200.0 / (fist.Wpm * (1 + fist.Drift * speedWalk));

            if (c == ' ')
            {
                if (afterLetter) t += draw(fist.WordGap - fist.LetterGap, fist.Jitter) * unit;
                afterLetter = false;
                continue;
            }
            string code;
            if (!Morse.TryGetValue(c, out code)) continue;

            if (afterLetter && r.NextDouble() < fist.Hesitate)
                t += (2 + 3 * r.NextDouble()) * unit;          // a pause inside the word

            for (int e = 0; e < code.Length; e++)
            {
                bool dah = code[e] == '-';
                double len = dah ? draw(fist.Dah, fist.Jitter) * unit
                                 : (fist.BugDits ? fist.Dit * unit * (1 + 0.02 * Gauss(r)) : draw(fist.Dit, fist.Jitter) * unit);
                edges.Add(new Edge { Part = part, TextIndex = i, Letter = c, DownMs = t, UpMs = t + len });
                t += len;
                if (e < code.Length - 1)
                    t += (fist.BugDits && !dah && code[e + 1] == '.' ? unit : draw(fist.ElemGap, fist.Jitter) * unit);
            }
            t += draw(fist.LetterGap, fist.Jitter) * unit;
            afterLetter = true;
        }
        endMs = t;
        return edges;
    }

    // ---------------------------------------------------------------- plan
    static List<Tuple<Fist, string>> ReadPlan(string path)
    {
        var plan = new List<Tuple<Fist, string>>();
        foreach (string line in File.ReadAllLines(path))
        {
            if (line.Trim().Length == 0 || line.StartsWith("#")) continue;
            var bits = line.Split('\t');
            var fist = Fists.First(f => f.Name == bits[0]).Copy();
            plan.Add(Tuple.Create(fist, bits[1].Trim().ToUpperInvariant()));
        }
        return plan;
    }

    static void Make(string path, int parts, int seed)
    {
        var r = new Random(seed);
        var lines = new List<string> { "# fist<TAB>text - made with seed " + seed };
        for (int i = 0; i < parts; i++)
            lines.Add(Fists[i % Fists.Length].Name + "\t" + MakeText(r));
        File.WriteAllLines(path, lines);
        Console.WriteLine("plan with {0} parts written to {1}", parts, path);
    }

    // ---------------------------------------------------------------- keying
    [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint ms);

    static System.IO.Ports.SerialPort _port;
    static bool _rts;
    static volatile bool _down;
    static long _downSince;
    static readonly object LineGate = new object();

    static void SetLine(bool on)
    {
        lock (LineGate)
        {
            if (_port != null && _port.IsOpen)
            {
                try { if (_rts) _port.RtsEnable = on; else _port.DtrEnable = on; } catch { }
            }
            _down = on;
            if (on) _downSince = DateTime.UtcNow.Ticks;
        }
    }

    static void WaitUntil(Stopwatch clock, double ms)
    {
        while (true)
        {
            double left = ms - clock.Elapsed.TotalMilliseconds;
            if (left <= 0) return;
            if (left > 3) Thread.Sleep((int)(left - 2)); else Thread.SpinWait(200);
        }
    }

    static int Run(string planPath, string outDir, string portName, bool rts, double maxMinutes, bool dry)
    {
        Directory.CreateDirectory(outDir);
        string stopFile = Path.Combine(outDir, "STOP");
        if (File.Exists(stopFile)) File.Delete(stopFile);

        var plan = ReadPlan(planPath);
        var r = new Random(12345);
        var all = new List<Edge>();
        double t = 1500;                                             // a moment's silence first
        var sent = new List<string>();
        for (int p = 0; p < plan.Count; p++)
        {
            double end;
            all.AddRange(Schedule(p, plan[p].Item2, plan[p].Item1, r, t, out end));
            sent.Add(plan[p].Item2);
            t = end + 4000;                                          // four seconds between parts
        }
        double totalMin = t / 60000.0;
        Console.WriteLine("{0} parts, {1} elements, {2:F1} minutes of keying", plan.Count, all.Count, totalMin);
        if (totalMin > maxMinutes)
        {
            Console.WriteLine("LONGER THAN THE LIMIT OF {0} MINUTES - nothing keyed. Use a shorter plan or raise --minutes.", maxMinutes);
            return 2;
        }
        File.WriteAllLines(Path.Combine(outDir, "SENT.txt"), sent);

        var clock = Stopwatch.StartNew();
        var watchdog = new Timer(_ =>
        {
            if (_down && DateTime.UtcNow.Ticks - _downSince > TimeSpan.FromMilliseconds(2500).Ticks)
            {
                SetLine(false);
                Console.WriteLine("WATCHDOG: key held too long - released.");
                Environment.Exit(3);
            }
        }, null, 200, 200);

        if (!dry)
        {
            _rts = rts;
            _port = new System.IO.Ports.SerialPort(portName) { DtrEnable = false, RtsEnable = false, Handshake = System.IO.Ports.Handshake.None };
            _port.Open();
            _port.DtrEnable = false; _port.RtsEnable = false;
            Console.CancelKeyPress += (s, e) => { SetLine(false); };
            AppDomain.CurrentDomain.ProcessExit += (s, e) => SetLine(false);
        }

        timeBeginPeriod(1);
        Thread.CurrentThread.Priority = ThreadPriority.Highest;
        bool stopped = false;
        int lastPart = -1;
        try
        {
            clock.Restart();
            // where time zero of edges.tsv sits on the clock the receivers' files are named by
            File.WriteAllText(Path.Combine(outDir, "started_utc.txt"), DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + Environment.NewLine);
            foreach (var e in all)
            {
                if (e.Part != lastPart)
                {
                    lastPart = e.Part;
                    Console.WriteLine("{0,6:F1}s  part {1} ({2}, {3} WPM): {4}", e.DownMs / 1000, e.Part + 1, plan[e.Part].Item1.Name, plan[e.Part].Item1.Wpm, plan[e.Part].Item2);
                }
                if (File.Exists(stopFile)) { stopped = true; break; }
                WaitUntil(clock, e.DownMs);
                SetLine(true);
                e.ActualDown = clock.Elapsed.TotalMilliseconds;
                try { WaitUntil(clock, e.UpMs); }
                finally { SetLine(false); e.ActualUp = clock.Elapsed.TotalMilliseconds; }
            }
        }
        finally
        {
            SetLine(false);
            timeEndPeriod(1);
            watchdog.Dispose();
            if (_port != null) { try { _port.Close(); } catch { } }
        }

        var sb = new StringBuilder("part\ttext_index\tletter\tdown_ms\tup_ms\tactual_down_ms\tactual_up_ms\n");
        double worst = 0;
        foreach (var e in all)
        {
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0}\t{1}\t{2}\t{3:F2}\t{4:F2}\t{5:F2}\t{6:F2}\n", e.Part, e.TextIndex, e.Letter, e.DownMs, e.UpMs, e.ActualDown, e.ActualUp);
            if (!double.IsNaN(e.ActualDown)) worst = Math.Max(worst, Math.Max(Math.Abs(e.ActualDown - e.DownMs), Math.Abs(e.ActualUp - e.UpMs)));
        }
        File.WriteAllText(Path.Combine(outDir, "edges.tsv"), sb.ToString());
        Console.WriteLine(stopped ? "STOPPED by the STOP file." : "done.");
        Console.WriteLine("worst difference between planned and actual edge: {0:F2} ms", worst);
        return 0;
    }

    static int Main(string[] args)
    {
        if (args.Length >= 4 && args[0] == "make") { Make(args[1], int.Parse(args[2]), int.Parse(args[3])); return 0; }
        if (args.Length >= 3 && args[0] == "dry") return Run(args[1], args[2], null, false, 1e9, true);
        if (args.Length >= 4 && args[0] == "key")
        {
            bool rts = args.Length > 4 && args[4].Equals("RTS", StringComparison.OrdinalIgnoreCase);
            double minutes = 20;
            int at = Array.IndexOf(args, "--minutes");
            if (at > 0 && at + 1 < args.Length) minutes = double.Parse(args[at + 1], CultureInfo.InvariantCulture);
            return Run(args[1], args[2], args[3], rts, minutes, false);
        }
        Console.WriteLine("FistKeyer make <plan> <parts> <seed> | dry <plan> <outdir> | key <plan> <outdir> <COMn> [DTR|RTS] [--minutes N]");
        return 1;
    }
}
