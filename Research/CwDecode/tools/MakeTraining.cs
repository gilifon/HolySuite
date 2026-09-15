using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace HolyLogger
{
    static class Log { public static void Swallow(Exception e) { } }

    // BUILDS CW WITH A KNOWN ANSWER THAT SOUNDS LIKE THE BAND.
    //
    // The pair nobody has: audio as hard to read as a real weak signal, and the text that was
    // actually sent. Recording a station off the air gives the first without the second; a tone
    // generator gives the second without the first. This gives both, by keying real text into
    // REAL BAND NOISE recorded off this operator's own receiver.
    //
    // WHY NOT RECORD THE RADIO TRANSMITTING IT. That was tried first and the IC-7610 settles it:
    // its USB audio carries receive audio only and is muted while transmitting, so twelve seconds
    // of keying into a dummy load recorded a flat line at 0.1% of full scale. The transmitter was
    // going to contribute a realistic keying shape and nothing else, and a keying shape can be
    // modelled honestly - see the raised cosine below. The half that could NOT be faked is the
    // noise, and that is the half we have for real.
    //
    // WHAT IS REAL HERE, and what is modelled:
    //   REAL  - the noise, every crash of static in it, and every other station that happened to be
    //           in the passband. Eleven minutes of it, taken off the air with the same receiver and
    //           the same filter the decoder will meet in service.
    //   MODEL - the keying: a raised-cosine rise and fall of a few milliseconds, which is what a
    //           transmitter's shaping does and why real CW does not click.
    //   MODEL - the fading, as a slow wander in strength. Real QSB, not multipath.
    //
    // Writes a WAV and a text file beside it holding what was sent.
    static class MakeTraining
    {
        const int Rate = 8000;

        // How long the tone takes to come up and go down. A transmitter shapes its keying so it does
        // not splatter, and 5 ms is the ordinary figure - long enough to matter to a 30 ms dit at
        // 40 WPM, which is exactly the case where elements go missing.
        const double EdgeMs = 5.0;

        static readonly Dictionary<char, string> Morse = new Dictionary<char, string>
        {
            {'A',".-"},{'B',"-..."},{'C',"-.-."},{'D',"-.."},{'E',"."},{'F',"..-."},{'G',"--."},
            {'H',"...."},{'I',".."},{'J',".---"},{'K',"-.-"},{'L',".-.."},{'M',"--"},{'N',"-."},
            {'O',"---"},{'P',".--."},{'Q',"--.-"},{'R',".-."},{'S',"..."},{'T',"-"},{'U',"..-"},
            {'V',"...-"},{'W',".--"},{'X',"-..-"},{'Y',"-.--"},{'Z',"--.."},
            {'0',"-----"},{'1',".----"},{'2',"..---"},{'3',"...--"},{'4',"....-"},{'5',"....."},
            {'6',"-...."},{'7',"--..."},{'8',"---.."},{'9',"----."},
            {'/',"-..-."},{'=',"-...-"}   // ? . and , are SIX elements and the network has five outputs
        };

        static void Main(string[] args)
        {
            string folder = args.Length > 0 ? args[0] : ".";
            int howMany = args.Length > 1 ? int.Parse(args[1]) : 200;
            int seed = args.Length > 2 ? int.Parse(args[2]) : 1;

            string outFolder = Path.Combine(folder, "training");
            Directory.CreateDirectory(outFolder);

            // The noise beds: everything recorded off the air with no readable station in it.
            var beds = new List<short[]>();
            foreach (string bed in new[] { "session1.wav", "beacons.wav" })
            {
                string path = Path.Combine(folder, bed);
                if (!File.Exists(path)) continue;
                int rate;
                short[] audio = ReadWav(path, out rate);
                if (rate == Rate && audio.Length > Rate * 10) beds.Add(audio);
            }

            if (beds.Count == 0) { Console.WriteLine("no noise beds found - need session1.wav or beacons.wav"); return; }
            Console.WriteLine("noise beds: " + beds.Count + ", "
                + (beds.ConvertAll(b => b.Length).ToArray().Length > 0 ? "" : "") + "total "
                + (TotalSeconds(beds)).ToString("F0") + " seconds of real band");

            var rnd = new Random(seed);
            var index = new StringBuilder();

            for (int n = 0; n < howMany; n++)
            {
                // ONE IN FIVE IS NOTHING BUT THE BAND, and that is not padding - it is the most
                // important lesson in the set. A network shown only signals learns that there is
                // always a signal, which is precisely what went wrong the first time: it called the
                // empty band "element one" and printed eleven hundred letters over eight minutes of
                // nothing. Silence has to be taught explicitly, with the same real noise in it.
                bool nothingAtAll = rnd.Next(5) == 0;

                string text = nothingAtAll ? string.Empty : RandomOver(rnd);
                double wpm = 12 + rnd.NextDouble() * 28;          // 12 to 40, the range he meets
                double tone = 400 + rnd.NextDouble() * 400;       // 400 to 800 Hz

                // NOT DOWN TO A HUNDREDTH ANY MORE. The first set ran the strength over two orders
                // of magnitude, which sounded thorough and was a mistake: at the bottom of it the
                // CW is genuinely inaudible - the plain decoder got under 40% of the characters on
                // a fifth of the set - and every one of those moments was still labelled "an
                // element is being sent". That taught the network to call noise a signal. A signal
                // that cannot be read must not be in the set with a label saying it can.
                double strength = nothingAtAll ? 0.0 : Math.Pow(10, -0.85 * rnd.NextDouble());
                double fadeDepth = rnd.NextDouble() * 0.8;        // none, to deep
                double fadeSeconds = 2 + rnd.NextDouble() * 8;

                short[] bed = beds[rnd.Next(beds.Count)];
                short[] audio = Build(text, wpm, tone, strength, fadeDepth, fadeSeconds, bed, rnd);

                string name = "cw" + n.ToString("0000", CultureInfo.InvariantCulture);
                WriteWav(Path.Combine(outFolder, name + ".wav"), audio);
                index.Append(name).Append('\t')
                     .Append(text).Append('\t')
                     .Append(wpm.ToString("F1", CultureInfo.InvariantCulture)).Append('\t')
                     .Append(tone.ToString("F0", CultureInfo.InvariantCulture)).Append('\t')
                     .Append(strength.ToString("F4", CultureInfo.InvariantCulture)).Append('\t')
                     .Append(fadeDepth.ToString("F2", CultureInfo.InvariantCulture)).Append('\n');
            }

            File.WriteAllText(Path.Combine(outFolder, "index.txt"), index.ToString());
            Console.WriteLine("wrote " + howMany + " examples to " + outFolder);
        }

        static double TotalSeconds(List<short[]> beds)
        {
            double n = 0;
            foreach (var b in beds) n += b.Length;
            return n / Rate;
        }

        // The kind of thing that is actually sent - a call, an exchange, a scrap of a ragchew.
        static string RandomOver(Random rnd)
        {
            string call = RandomCall(rnd);
            switch (rnd.Next(6))
            {
                case 0: return "CQ CQ CQ DE " + call + " " + call + " K";
                case 1: return call + " DE " + RandomCall(rnd) + " 5NN 5NN K";
                case 2: return "TU " + call + " 73 GL";
                case 3: return "RST 599 599 QTH " + RandomWord(rnd) + " NAME " + RandomWord(rnd) + " HW";
                case 4: return call + " DE " + RandomCall(rnd) + " R R TNX FER CALL UR RST 579 579";
                default: return RandomWord(rnd) + " " + RandomWord(rnd) + " " + call + " " + RandomWord(rnd) + " AR";
            }
        }

        static readonly string[] Prefixes = { "4Z", "4X", "G", "DL", "F", "I", "SP", "OK", "LY", "SM", "OH",
                                              "EA", "PA", "ON", "W1", "K3", "VE3", "JA1", "VK6", "ZL2", "UA3",
                                              "9A", "S5", "YO", "LZ", "SV", "TA", "OE", "HB9", "OZ", "LA" };

        static string RandomCall(Random rnd)
        {
            string p = Prefixes[rnd.Next(Prefixes.Length)];
            var s = new StringBuilder(p);
            if (!char.IsDigit(p[p.Length - 1])) s.Append((char)('0' + rnd.Next(10)));
            int suffix = 1 + rnd.Next(3);
            for (int i = 0; i < suffix; i++) s.Append((char)('A' + rnd.Next(26)));
            return s.ToString();
        }

        static readonly string[] Words = { "TNX", "FER", "QSO", "RIG", "ANT", "PWR", "WX", "SUNNY", "RAIN",
                                           "COLD", "WARM", "DIPOLE", "VERTICAL", "YAGI", "100W", "5W", "QRP",
                                           "OM", "DR", "GUD", "HPE", "CUAGN", "BCNU", "SRI", "AGN", "PSE" };

        static string RandomWord(Random rnd) { return Words[rnd.Next(Words.Length)]; }

        static short[] Build(string text, double wpm, double tone, double strength,
                             double fadeDepth, double fadeSeconds, short[] bed, Random rnd)
        {
            double ditSeconds = 1.2 / wpm;
            int ditSamples = (int)Math.Round(ditSeconds * Rate);

            // Lay the keying out first as on/off, then shape the edges.
            var key = new List<bool>();
            Action<int, bool> Hold = (samples, on) => { for (int i = 0; i < samples; i++) key.Add(on); };

            Hold(ditSamples * 4, false);                 // a moment of band before he starts

            foreach (char c in text.ToUpperInvariant())
            {
                if (c == ' ') { Hold(ditSamples * 4, false); continue; }   // 3 more on top of the 3 below
                string pattern;
                if (!Morse.TryGetValue(c, out pattern)) continue;

                for (int i = 0; i < pattern.Length; i++)
                {
                    Hold(ditSamples * (pattern[i] == '-' ? 3 : 1), true);
                    if (i < pattern.Length - 1) Hold(ditSamples, false);
                }
                Hold(ditSamples * 3, false);
            }

            Hold(ditSamples * 6, false);

            int edge = (int)Math.Round(EdgeMs * Rate / 1000.0);
            var audio = new short[key.Count];

            // A raised cosine on each edge - what a transmitter's shaping does, and the reason real
            // CW does not click. It also takes a bite out of a short dit, which is precisely the
            // case where an element goes missing, so it must not be left out.
            double phase = 0, step = 2 * Math.PI * tone / Rate;
            double level = 0;

            for (int i = 0; i < key.Count; i++)
            {
                double want = key[i] ? 1.0 : 0.0;
                double rate = 1.0 / edge;
                if (level < want) level = Math.Min(want, level + rate);
                else if (level > want) level = Math.Max(want, level - rate);

                double shaped = 0.5 - 0.5 * Math.Cos(Math.PI * level);   // raised cosine, not a ramp

                // QSB: a slow wander in strength, which is what actually loses elements.
                double fade = 1.0 - fadeDepth * 0.5 * (1 - Math.Cos(2 * Math.PI * i / (fadeSeconds * Rate)));

                phase += step;
                double v = Math.Sin(phase) * shaped * strength * fade * 12000.0;

                // ...and drop it into real band noise, at a random place in it.
                audio[i] = Clip(v);
            }

            int at = rnd.Next(Math.Max(1, bed.Length - key.Count));
            for (int i = 0; i < audio.Length; i++)
                audio[i] = Clip(audio[i] + (double)bed[(at + i) % bed.Length]);

            return audio;
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
                w.Write(new[] { 'R', 'I', 'F', 'F' });
                w.Write(36 + bytes);
                w.Write(new[] { 'W', 'A', 'V', 'E' });
                w.Write(new[] { 'f', 'm', 't', ' ' });
                w.Write(16);
                w.Write((short)1);
                w.Write((short)1);
                w.Write(Rate);
                w.Write(Rate * 2);
                w.Write((short)2);
                w.Write((short)16);
                w.Write(new[] { 'd', 'a', 't', 'a' });
                w.Write(bytes);
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
