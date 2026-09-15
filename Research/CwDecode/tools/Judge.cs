using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace HolyLogger
{
    static class Log { public static void Swallow(Exception e) { } }

    // THE ONE THAT DECIDES WHETHER THE NEW NETWORK SHIPS.
    //
    // Runs the plain decoder and the neural one over the SAME ten real recordings, and counts the
    // same words the real-signal bench counts. A network that wins on made-up audio and loses here
    // has learned the made-up audio, which is the whole thing to guard against - so this is the only
    // number that decides anything.
    //
    // Give it a weights file and it judges that one. Give it two and it judges both, which is how
    // the retrained network is set beside the one it would replace.
    static class Judge
    {
        class Recording
        {
            public string File;
            public string[] Expected;
            public bool ShouldStayQuiet;
        }

        static readonly Recording[] Recordings =
        {
            new Recording { File = "quiet.wav",  Expected = new[] { "V4TQ", "5NN", "IZ5CMG", "SP9ADG" } },
            new Recording { File = "radio.wav",  Expected = new[] { "PWR", "SUR", "DELTA", "22MUP", "MERCI", "POURCE", "AUPLAISIR", "SOIR", "BONNE" } },
            new Recording { File = "radio2.wav", Expected = new[] { "RIGRIG", "80W" } },
            new Recording { File = "narrow.wav", Expected = new[] { "LB2WD", "DERA1QN", "73GL" } },
            new Recording { File = "bad.wav",    Expected = new[] { "LB2WD", "DERA1Q" } },
            new Recording { File = "ly2px2.wav", Expected = new[] { "K1Y", "TOMEET", "AGN" } },
            new Recording { File = "ly2px.wav",  Expected = new[] { "LY2PX", "CQCQCQDE" } },
            new Recording { File = "slow.wav",   Expected = new[] { "IU5RDL", "CQCQ", "PSE" } },
            new Recording { File = "weak.wav",   Expected = new[] { "GUD", "HIHI", "AGN" } },
            new Recording { File = "active.wav", Expected = new[] { "R1LN", "DER1LN", "7388" } },
            new Recording { File = "beacons.wav",  Expected = new string[0], ShouldStayQuiet = true },
            new Recording { File = "session1.wav", Expected = new string[0], ShouldStayQuiet = true },
        };

        static void Main(string[] args)
        {
            string folder = args[0];

            Console.WriteLine("PLAIN DECODER");
            Run(folder, null);

            for (int i = 1; i < args.Length; i++)
            {
                Console.WriteLine();
                Console.WriteLine("NEURAL, weights " + Path.GetFileName(args[i]));
                Run(folder, args[i]);
            }
        }

        static void Run(string folder, string weights)
        {
            CwNeuralNet net = null;
            if (weights != null)
            {
                net = new CwNeuralNet();
                string error;
                if (!net.Load(weights, out error)) { Console.WriteLine("  cannot load: " + error); return; }
            }

            int found = 0, wanted = 0, noise = 0;

            foreach (var r in Recordings)
            {
                string path = Path.Combine(folder, r.File);
                if (!File.Exists(path)) continue;

                string got = net == null ? Plain(path) : Neural(path, net);
                string flat = Flatten(got);

                if (r.ShouldStayQuiet)
                {
                    noise += flat.Length / 20;
                    Console.WriteLine("  {0,-13} {1,6} letters of noise (want none)", r.File, flat.Length);
                    continue;
                }

                var missed = new List<string>();
                int here = 0;
                foreach (string want in r.Expected)
                {
                    wanted++;
                    if (flat.Contains(Flatten(want))) { found++; here++; }
                    else missed.Add(want);
                }
                Console.WriteLine("  {0,-13} {1} of {2}{3}", r.File, here, r.Expected.Length,
                    missed.Count > 0 ? "   missed: " + string.Join(" ", missed.ToArray()) : "");
            }

            Console.WriteLine("  SCORE {0} of {1} words, {2} off for noise  =>  {3}", found, wanted, noise, found - noise);
        }

        static string Plain(string path)
        {
            int rate;
            short[] audio = ReadWav(path, out rate);
            var d = new CwDecoder(rate);
            var got = new StringBuilder();
            d.Text += s => got.Append(s);
            Feed(audio, rate, (buf, n) => d.Process(buf, n));
            return got.ToString();
        }

        // The neural reader needs to be told the note and the speed, and the PLAIN decoder is what
        // finds them - exactly as the program itself does it. So the two are not independent, and
        // that is not a flaw: on the air there is nothing else to ask.
        static string Neural(string path, CwNeuralNet net)
        {
            int rate;
            short[] audio = ReadWav(path, out rate);

            var plain = new CwDecoder(rate);
            var neural = new CwNeuralDecoder(rate, net);
            var got = new StringBuilder();
            neural.Text += s => got.Append(s);

            Feed(audio, rate, (buf, n) =>
            {
                plain.Process(buf, n);
                if (plain.SignalPresent) neural.Configure(plain.ToneHz, plain.Wpm);
                neural.Process(buf, n);
            });

            return got.ToString();
        }

        static void Feed(short[] audio, int rate, Action<short[], int> to)
        {
            int block = rate / 10;
            var buf = new short[block];
            for (int i = 0; i < audio.Length; i += block)
            {
                int n = Math.Min(block, audio.Length - i);
                Array.Copy(audio, i, buf, 0, n);
                to(buf, n);
            }
        }

        static string Flatten(string s)
        {
            var t = new StringBuilder();
            foreach (char c in (s ?? string.Empty).ToUpperInvariant())
                if (char.IsLetterOrDigit(c)) t.Append(c);
            return t.ToString();
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
