using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace HolyLogger
{
    static class Log { public static void Swallow(Exception e) { } }

    // KNOWN TEXT, KEYED THE WAY PEOPLE ACTUALLY KEY, INTO THE BAND THIS OPERATOR ACTUALLY HEARS.
    //
    // The generator for the CTC network. A CTC network is taught from audio and the TEXT alone -
    // no moment-by-moment labels - so nothing here has to line up with anything, and the old
    // generator's two worst habits can go:
    //
    // PERFECT TIMING. The first generator keyed like a machine: every dit exactly one unit. Nobody
    // on a straight key or a bug sends like that, and a network that never heard a long dit or a
    // short letter gap learns nothing about the operators who are hardest to read. Now each example
    // gets its own sender: a scatter on every element, his own dah length, and his own habit of
    // stretching the gaps between letters and words.
    //
    // ONLY SLOW FADING. Real HF also flutters - several times a second, inside a single letter - and
    // that is exactly where a weak element vanishes. Four in ten examples now carry flutter as well
    // as the slow fade.
    //
    // UNCHANGED, because they were right: the noise is real, eleven minutes of it recorded off the
    // operator's own receiver; one example in five is nothing but that noise, so silence is taught
    // rather than assumed; and no signal is made so weak that it cannot be heard, because a label
    // saying "this was sent" over audio in which it was not audible teaches the network to invent.
    static class MakeCtcAudio
    {
        const int Rate = 8000;
        const double EdgeMs = 5.0;

        public static readonly Dictionary<char, string> Morse = new Dictionary<char, string>
        {
            {'A',".-"},{'B',"-..."},{'C',"-.-."},{'D',"-.."},{'E',"."},{'F',"..-."},{'G',"--."},
            {'H',"...."},{'I',".."},{'J',".---"},{'K',"-.-"},{'L',".-.."},{'M',"--"},{'N',"-."},
            {'O',"---"},{'P',".--."},{'Q',"--.-"},{'R',".-."},{'S',"..."},{'T',"-"},{'U',"..-"},
            {'V',"...-"},{'W',".--"},{'X',"-..-"},{'Y',"-.--"},{'Z',"--.."},
            {'0',"-----"},{'1',".----"},{'2',"..---"},{'3',"...--"},{'4',"....-"},{'5',"....."},
            {'6',"-...."},{'7',"--..."},{'8',"---.."},{'9',"----."},
            // Six-element characters are back: CTC has no five-element limit.
            {'/',"-..-."},{'=',"-...-"},{'?',"..--.."},{'.',".-.-.-"},{',',"--..--"}
        };

        static void Main(string[] args)
        {
            if (args.Length < 3) { Console.WriteLine("MakeCtcAudio recordingsFolder outFolder count [seed]"); return; }
            string recordings = args[0], outFolder = args[1];
            int howMany = int.Parse(args[2]);
            int seed = args.Length > 3 ? int.Parse(args[3]) : 1;

            // MOSTLY SHORT, and this is what let the network learn at all. The first set was whole
            // overs, up to fifty seconds each, and a CTC network starting from nothing could not work
            // out which stretch of sound went with which letter across that much: ten minutes of
            // training and it still said "nothing" everywhere, 99% of characters wrong. A callsign,
            // a 5NN, two words - short enough that the answer is findable - and the long overs
            // alongside, so it still learns to keep going for minutes on end.
            double shortShare = args.Length > 4 ? double.Parse(args[4], CultureInfo.InvariantCulture) : 0.0;

            // The receiver's AGC - see Agc. How many examples get it, and how high above the band its
            // ceiling may sit, in tens. Set from measurement against the real recordings.
            if (args.Length > 5) AgcShare = double.Parse(args[5], CultureInfo.InvariantCulture);
            if (args.Length > 6) AgcLow = double.Parse(args[6], CultureInfo.InvariantCulture);
            if (args.Length > 7) AgcHigh = double.Parse(args[7], CultureInfo.InvariantCulture);
            if (args.Length > 8) StrengthLow = double.Parse(args[8], CultureInfo.InvariantCulture);
            if (args.Length > 9) StrengthHigh = double.Parse(args[9], CultureInfo.InvariantCulture);

            Directory.CreateDirectory(outFolder);

            var beds = new List<short[]>();
            foreach (string bed in new[] { "session1.wav", "beacons.wav" })
            {
                string path = Path.Combine(recordings, bed);
                if (!File.Exists(path)) continue;
                int rate;
                short[] audio = ReadWav(path, out rate);
                if (rate == Rate && audio.Length > Rate * 10) beds.Add(audio);
            }
            if (beds.Count == 0) { Console.WriteLine("no noise beds in " + recordings); return; }

            var rnd = new Random(seed);
            var index = new StringBuilder();

            for (int n = 0; n < howMany; n++)
            {
                bool nothing = rnd.Next(5) == 0;
                string text = nothing ? string.Empty
                            : rnd.NextDouble() < shortShare ? ShortOver(rnd)
                            : RandomOver(rnd);

                var sender = new Sender
                {
                    Wpm = 12 + rnd.NextDouble() * 28,
                    Scatter = rnd.NextDouble() * 0.20,
                    DahRatio = 2.6 + rnd.NextDouble() * 1.0,
                    LetterGap = 1.0 + rnd.NextDouble() * 0.8,
                    WordGap = 1.0 + rnd.NextDouble() * 1.0
                };

                double tone = 400 + rnd.NextDouble() * 400;
                // AS STRONG AS THE REAL BAND, AND NO STRONGER - measured, not chosen.
                //
                // On the features the network hears, the key-down level of every real recording stands
                // 0.9 to 1.5 above key-up (median 1.21), even for a station at half of full scale; plain
                // band noise alone spreads about 0.5, and nothing under about 0.9 is readable. The
                // practice audio's strong signals stood 1.6 to 2.8 above - up to five hundred times - so
                // nearly all of it lay outside the one window where real readable CW lives, and the
                // network that learned from it misread clean strong real stations: U4TQR for V4TQ.
                //
                // An AGC was tried first and changed nothing (depth 2.49 without it, 2.13 at its
                // hardest): it turns the signal and the noise between its elements down together, so
                // the contrast inside a transmission stays where it was.
                double strength = nothing ? 0.0 : Math.Pow(10, StrengthLow + (StrengthHigh - StrengthLow) * rnd.NextDouble());
                double fadeDepth = rnd.NextDouble() * 0.7;
                double fadeSeconds = 2 + rnd.NextDouble() * 8;
                bool flutter = rnd.NextDouble() < 0.4;
                double flutterHz = 2 + rnd.NextDouble() * 13;
                double flutterDepth = flutter ? rnd.NextDouble() * 0.8 : 0.0;

                short[] bed = beds[rnd.Next(beds.Count)];
                var marks = new StringBuilder();
                short[] audio = Build(text, sender, tone, strength, fadeDepth, fadeSeconds,
                                      flutterHz, flutterDepth, bed, rnd, marks);

                string name = "ctc" + n.ToString("00000", CultureInfo.InvariantCulture);
                WriteWav(Path.Combine(outFolder, name + ".wav"), audio);
                index.Append(name).Append('\t').Append(text).Append('\t')
                     .Append(sender.Wpm.ToString("F1", CultureInfo.InvariantCulture)).Append('\t')
                     .Append(strength.ToString("F3", CultureInfo.InvariantCulture)).Append('\t')
                     .Append(sender.Scatter.ToString("F2", CultureInfo.InvariantCulture)).Append('\t')
                     .Append(flutterDepth.ToString("F2", CultureInfo.InvariantCulture)).Append('\t')
                     .Append(marks.ToString().Trim()).Append('\n');
            }

            File.WriteAllText(Path.Combine(outFolder, "index.txt"), index.ToString());
            Console.WriteLine("wrote " + howMany + " examples to " + outFolder);
        }

        class Sender
        {
            public double Wpm, Scatter, DahRatio, LetterGap, WordGap;
        }

        static string RandomOver(Random rnd)
        {
            string call = RandomCall(rnd);
            switch (rnd.Next(8))
            {
                case 0: return "CQ CQ CQ DE " + call + " " + call + " K";
                case 1: return call + " DE " + RandomCall(rnd) + " 5NN 5NN K";
                case 2: return "TU " + call + " 73 GL";
                case 3: return "RST 599 599 QTH " + Word(rnd) + " NAME " + Word(rnd) + " HW?";
                case 4: return call + " DE " + RandomCall(rnd) + " R R TNX FER CALL UR RST 579 579";
                case 5: return "QRZ? DE " + call + " K";
                case 6: return call + " " + Word(rnd) + " " + Word(rnd) + " = " + Word(rnd) + " " + Word(rnd);
                default: return Word(rnd) + " " + Word(rnd) + " " + call + " " + Word(rnd) + " " + (rnd.Next(1000)).ToString(CultureInfo.InvariantCulture);
            }
        }

        static string ShortOver(Random rnd)
        {
            switch (rnd.Next(10))
            {
                case 0: return RandomCall(rnd);
                case 1: return "CQ " + RandomCall(rnd);
                case 2: return "5NN";
                case 3: return "TU";
                case 4: return RandomCall(rnd) + " " + Word(rnd);
                case 5: return Word(rnd) + " " + Word(rnd);
                case 6: return "DE " + RandomCall(rnd);
                case 7: return rnd.Next(1000).ToString(CultureInfo.InvariantCulture);
                case 8: return "R R " + Word(rnd);
                default: return Word(rnd);
            }
        }

        static readonly string[] Prefixes = { "4Z", "4X", "G", "DL", "F", "I", "SP", "OK", "LY", "SM", "OH",
                                              "EA", "PA", "ON", "W1", "K3", "VE3", "JA1", "VK6", "ZL2", "UA3",
                                              "9A", "S5", "YO", "LZ", "SV", "TA", "OE", "HB9", "OZ", "LA",
                                              "R1", "IZ", "SP9", "IU", "V4", "2E", "M0", "EI", "CT", "YB" };

        static string RandomCall(Random rnd)
        {
            var s = new StringBuilder(Prefixes[rnd.Next(Prefixes.Length)]);
            if (!char.IsDigit(s[s.Length - 1])) s.Append((char)('0' + rnd.Next(10)));
            int suffix = 1 + rnd.Next(3);
            for (int i = 0; i < suffix; i++) s.Append((char)('A' + rnd.Next(26)));
            if (rnd.Next(12) == 0) s.Append(rnd.Next(2) == 0 ? "/P" : "/M");
            return s.ToString();
        }

        static readonly string[] Words = { "TNX", "FER", "QSO", "RIG", "ANT", "PWR", "WX", "SUNNY", "RAIN",
                                           "COLD", "WARM", "DIPOLE", "VERTICAL", "YAGI", "100W", "5W", "QRP",
                                           "OM", "DR", "GUD", "HPE", "CUAGN", "BCNU", "SRI", "AGN", "PSE", "UR",
                                           "ES", "HR", "FB", "OP", "NAME", "QTH", "BK", "KN", "SK", "TU", "73" };

        static string Word(Random rnd) { return Words[rnd.Next(Words.Length)]; }

        // The key as this sender would key it: on/off for every sample.
        //
        // AND WHERE EACH LETTER WAS NAMED, written into marks. CTC learning from nothing collapsed on
        // this data - the network said "A" for every input - so the network is taught letter by
        // letter instead, and that needs to know when each one was sent. Each mark is the letter, the
        // sample where its label starts, and how many samples it lasts: "S@12345+180".
        //
        // THE LABEL SITS IN THE GAP AFTER THE LETTER, from 0.4 to 1.2 dits past its last element.
        // Not on the letter itself: while "..." is still being sent it could yet become an H, and a
        // network cannot be asked to know what has not happened. Not later than 1.2 dits either: the
        // shortest gap a sloppy sender leaves before his next letter is one and a half. A word gap is
        // named the same way just after the letter before it, from 1.3 to 1.9 dits, inside the
        // shortest word gap this generator ever sends.
        static List<bool> Key(string text, Sender who, Random rnd, StringBuilder marks)
        {
            var key = new List<bool>();
            double unit = 1.2 / who.Wpm * Rate;

            Action<double, bool> hold = (units, on) =>
            {
                // Every stretch gets its own scatter, and never below half its proper length -
                // an operator can be sloppy, he cannot send a negative dit.
                double stretch = 1.0 + who.Scatter * Gaussian(rnd);
                if (stretch < 0.5) stretch = 0.5;
                int n = (int)Math.Round(units * unit * stretch);
                for (int i = 0; i < n; i++) key.Add(on);
            };

            for (int i = 0, lead = (int)((0.5 + rnd.NextDouble() * 1.5) * Rate); i < lead; i++) key.Add(false);

            // NAMED ONLY ONCE IT IS CERTAIN - the rule the first labels broke.
            //
            // They named each letter 0.4 of a dit after its last element. But the gap between two
            // elements INSIDE a letter is a whole dit, so at 0.4 of a dit nobody can tell whether the
            // letter is finished or another element is coming. On perfectly timed practice audio the
            // network scraped by; on the real recordings it named a letter after every single element
            // - "E I S H 5" for a run of dits, "T N D" for dah-dit-dit, "E A R" for dit-dah-dit.
            //
            // So a letter is named from 1.5 dits of silence - past any gap inside a letter - and a
            // word gap from 3.3 dits, past an ordinary gap between letters. And NEVER after the next
            // letter has begun, which is why a mark is only written once the next element's start is
            // known: a sloppy sender who leaves too little room gets his label squeezed into the room
            // there is, rather than laid over his next letter.
            string pendingChar = null;
            bool pendingSpace = false;
            int pendingEnd = 0;

            Action<string, double, double> write = (what, from, to) =>
            {
                if (to - from < 1) to = from + 1;
                marks.Append(what).Append('@')
                     .Append(((int)from).ToString(CultureInfo.InvariantCulture)).Append('+')
                     .Append(((int)(to - from)).ToString(CultureInfo.InvariantCulture)).Append(' ');
            };

            Action<int> settle = nextStart =>
            {
                if (pendingChar == null) return;
                double limit = nextStart - 0.3 * unit;

                double from = pendingEnd + 1.5 * unit;
                double to = Math.Min(pendingEnd + 2.3 * unit, limit);
                if (to - from < 0.25 * unit)
                {
                    // Too little room: the last moments before the next letter, which is the most
                    // certain the letter ever gets.
                    to = limit;
                    from = Math.Max(pendingEnd + 0.8 * unit, to - 0.3 * unit);
                }
                write(pendingChar, from, to);

                if (pendingSpace)
                {
                    double spaceFrom = Math.Max(to + 0.2 * unit, pendingEnd + 3.3 * unit);
                    double spaceTo = Math.Min(spaceFrom + 0.6 * unit, limit);
                    if (spaceTo > spaceFrom) write("_", spaceFrom, spaceTo);
                }

                pendingChar = null;
                pendingSpace = false;
            };

            bool first = true;
            foreach (char c in text.ToUpperInvariant())
            {
                if (c == ' ')
                {
                    if (pendingChar != null) pendingSpace = true;
                    hold(4 * who.WordGap, false);
                    first = true;
                    continue;
                }
                string pattern;
                if (!Morse.TryGetValue(c, out pattern)) continue;
                if (!first) hold(3 * who.LetterGap, false);
                first = false;

                settle(key.Count);          // the previous letter's room ends where this one begins

                for (int e = 0; e < pattern.Length; e++)
                {
                    if (e > 0) hold(1, false);
                    hold(pattern[e] == '-' ? who.DahRatio : 1, true);
                }

                pendingChar = c.ToString();
                pendingEnd = key.Count;
            }

            for (int i = 0; i < Rate; i++) key.Add(false);
            settle(key.Count);
            return key;
        }

        static double Gaussian(Random rnd)
        {
            double u1 = 1.0 - rnd.NextDouble(), u2 = rnd.NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        }

        static short[] Build(string text, Sender who, double tone, double strength,
                             double fadeDepth, double fadeSeconds, double flutterHz, double flutterDepth,
                             short[] bed, Random rnd, StringBuilder marks)
        {
            List<bool> key = Key(text, who, rnd, marks);
            var audio = new short[key.Count];

            int edge = (int)Math.Round(EdgeMs * Rate / 1000.0);
            double phase = 0, step = 2 * Math.PI * tone / Rate, level = 0;
            double flutterPhase = rnd.NextDouble() * 2 * Math.PI;

            for (int i = 0; i < key.Count; i++)
            {
                double want = key[i] ? 1.0 : 0.0;
                if (level < want) level = Math.Min(want, level + 1.0 / edge);
                else if (level > want) level = Math.Max(want, level - 1.0 / edge);
                double shaped = 0.5 - 0.5 * Math.Cos(Math.PI * level);

                double t = i / (double)Rate;
                double fade = 1.0 - fadeDepth * 0.5 * (1 - Math.Cos(2 * Math.PI * t / fadeSeconds));
                double flutter = 1.0 - flutterDepth * 0.5 * (1 + Math.Sin(2 * Math.PI * flutterHz * t + flutterPhase));

                phase += step;
                audio[i] = Clip(Math.Sin(phase) * shaped * strength * fade * flutter * 12000.0);
            }

            int at = rnd.Next(Math.Max(1, bed.Length - key.Count));
            var mixed = new double[audio.Length];
            double bedAbs = 0;
            for (int i = 0; i < audio.Length; i++)
            {
                double b = bed[(at + i) % bed.Length];
                mixed[i] = audio[i] + b;
                bedAbs += Math.Abs(b);
            }
            bedAbs = Math.Max(1.0, bedAbs / Math.Max(1, audio.Length));

            if (rnd.NextDouble() < AgcShare) Agc(mixed, bedAbs, rnd);

            for (int i = 0; i < audio.Length; i++) audio[i] = Clip(mixed[i]);
            return audio;
        }

        // THE RADIO'S AGC, which the practice audio did not have and the real band always does.
        //
        // Measured on the five numbers the network hears: on every real recording the key-down level
        // stands 0.9 to 1.5 above the key-up level (in tens: ten to thirty times louder), even on a
        // station at half of full scale. The strong practice signals stood 1.9 to 2.7 above - up to five
        // hundred times. The receiver does that: it turns a loud signal down to a set level above the
        // noise and no further. So on the real band a strong station looks clean but MODERATE, a thing
        // the network had never practised on - in practice audio, moderate contrast only ever came with
        // plenty of noise - and it misread the clean strong ones: U4TQR for V4TQ.
        //
        // So here the mix goes through the same kind of thing: a level follower that rises in a few
        // milliseconds and falls back over tens to hundreds, and a gain that holds whatever is louder
        // than a ceiling down to the ceiling. The band noise itself sits under the ceiling and passes
        // untouched - it was recorded through the real AGC already. The ceiling is set a random amount
        // above the band, so how much a strong station is squashed varies from example to example the
        // way it varies between stations. Weak signals under the ceiling keep their own contrast.
        // Signal strength, as a power of ten of the 12000 full keying amplitude. The first sets ran
        // from -0.85 to 0 - see the note where strength is drawn for why that was far too strong.
        static double StrengthLow = -0.85, StrengthHigh = 0.0;

        static double AgcShare = 0.0;
        static double AgcLow = 0.8, AgcHigh = 1.6;     // the ceiling above the band, in tens

        static void Agc(double[] x, double bedAbs, Random rnd)
        {
            double ceiling = bedAbs * Math.Pow(10, AgcLow + rnd.NextDouble() * (AgcHigh - AgcLow));
            double attack = Math.Exp(-1.0 / (Rate * (0.002 + rnd.NextDouble() * 0.006)));
            double release = Math.Exp(-1.0 / (Rate * (0.03 + rnd.NextDouble() * 0.30)));

            double level = bedAbs;
            for (int i = 0; i < x.Length; i++)
            {
                double a = Math.Abs(x[i]) * 1.57;      // a sine's average rectified value is 2/pi of its peak
                level = a > level ? attack * level + (1 - attack) * a : release * level + (1 - release) * a;
                if (level > ceiling) x[i] *= ceiling / level;
            }
        }

        static short Clip(double v)
        {
            if (v > 32767) return 32767;
            if (v < -32768) return -32768;
            return (short)v;
        }

        static void WriteWav(string path, short[] data)
        {
            using (var f = File.Create(path))
            using (var w = new BinaryWriter(f))
            {
                int bytes = data.Length * 2;
                w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + bytes);
                w.Write(Encoding.ASCII.GetBytes("WAVE")); w.Write(Encoding.ASCII.GetBytes("fmt "));
                w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(Rate); w.Write(Rate * 2);
                w.Write((short)2); w.Write((short)16);
                w.Write(Encoding.ASCII.GetBytes("data")); w.Write(bytes);
                foreach (short s in data) w.Write(s);
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
