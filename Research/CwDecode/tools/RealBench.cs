using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace HolyLogger
{
    static class Log { public static void Swallow(Exception e) { } }

    // THE REAL-SIGNAL SCORE: how much of what was actually sent does the decoder find?
    //
    // The generated bench says whether a change works on made-up Morse. It has never once told me
    // whether a change works on the air - every real improvement this project has had came from
    // reading two lines of text side by side and squinting at them. This puts a number on that.
    //
    // WHERE THE EXPECTED WORDS COME FROM, and how far they can be trusted. They are NOT hand-typed
    // guesses. Each one is a word that this decoder AND ggmorse - a separate decoder, written by
    // somebody else, sharing no code with this one - both produced from the same recording,
    // independently. Two strangers agreeing on "IZ5CMG" is strong evidence somebody sent IZ5CMG.
    // It is not proof: both could be wrong the same way, and neither of us can hear. So this is a
    // good yardstick, not a truth, and a change that scores worse here is worth a hard look rather
    // than an automatic revert.
    //
    // SPACES ARE IGNORED on both sides. Gluing words together and splitting them apart is a known
    // fault of its own, measured elsewhere; here the question is only whether the LETTERS came out.
    // So "WR 8 0 W" counts as finding "WR80W".
    static class RealBench
    {
        class Recording
        {
            public string File;
            public string What;
            public string[] Expected;
        }

        static readonly Recording[] Recordings =
        {
            new Recording { File = "quiet.wav", What = "V4TQ working IZ5CMG and SP9ADG",
                Expected = new[] { "V4TQ", "5NN", "IZ5CMG", "SP9ADG" } },

            new Recording { File = "radio.wav", What = "French QSO, 675 Hz, fast",
                Expected = new[] { "PWR", "SUR", "DELTA", "22MUP", "MERCI", "POURCE", "AUPLAISIR",
                                   "SOIR", "BONNE" } },

            new Recording { File = "radio2.wav", What = "about 19 WPM ragchew",
                Expected = new[] { "RIGRIG", "80W" } },

            new Recording { File = "narrow.wav", What = "LB2WD, radio filter narrowed",
                Expected = new[] { "LB2WD", "DERA1QN", "73GL" } },

            new Recording { File = "bad.wav", What = "same QSO, four stations in the passband",
                Expected = new[] { "LB2WD", "DERA1Q" } },

            new Recording { File = "ly2px2.wav", What = "LY2PX, fast and weak",
                Expected = new[] { "K1Y", "TOMEET", "AGN" } },

            // LY2PX is the one word here that is not evidence but FACT: the operator heard this
            // station himself and told me the callsign, which is how the L-read-as-D fault was
            // found in the first place. ggmorse reads "DE LY2PX" off this recording cleanly; this
            // decoder still loses the L and gives "R Y2PX" and "EY2PX". So it is a real target with
            // real headroom, not a word we already get.
            new Recording { File = "ly2px.wav", What = "LY2PX calling CQ - the L is still lost",
                Expected = new[] { "LY2PX", "CQCQCQDE" } },

            // The slow end, which everything else here was missing - 78 ms to the dit, about 15
            // words a minute, where all the other recordings run 19 to 40. IU5RDL calling CQ; both
            // decoders read the callsign whole off this one.
            new Recording { File = "slow.wav", What = "IU5RDL calling CQ at 15 WPM",
                Expected = new[] { "IU5RDL", "CQCQ", "PSE" } },

            // The same station a couple of minutes on, and much harder - both decoders make a mess
            // of it, so there is nothing here either of them can be trusted about yet. Kept as
            // material to come back to rather than scored.

            // A weak one, 24 WPM at 496 Hz - and the recording where this decoder is plainly BETTER
            // than ggmorse rather than level with it: ggmorse threw a long run of rubbish into the
            // middle of the over where this one stayed readable. Both agree on these three.
            new Recording { File = "weak.wav", What = "weak F4A.. working VK6, 24 WPM",
                Expected = new[] { "GUD", "HIHI", "AGN" } },

            // R1LN calling, caught while the operator was listening. Both decoders read "DE R1LN"
            // several times over independently, which is as sure as this evidence gets.
            new Recording { File = "active.wav", What = "R1LN, busy frequency",
                Expected = new[] { "R1LN", "DER1LN", "7388" } },

            // Nothing is expected here and that is the point. Three minutes of band noise with three
            // beacons in it that NEITHER decoder can read - ggmorse, handed the exact note and
            // speed, managed two letters in three minutes. So the right answer is silence, and
            // whatever is printed is noise turned into letters. Scored the other way round.
            new Recording { File = "beacons.wav", What = "14.100, three unreadable beacons - stay QUIET",
                Expected = new string[0] },

            // EIGHT MINUTES OF A BAND THAT EMPTIED. The station left while this was recording, so
            // most of it is nothing at all - and it is the clearest thing measured all day. This
            // decoder printed 22 characters over the eight minutes; ggmorse printed several
            // THOUSAND, page after page of rubbish, on the same audio. Staying quiet on an empty
            // frequency was the hardest thing to get right here and it is where this decoder is
            // furthest ahead of the library. Guarded so it is never quietly given away.
            new Recording { File = "session1.wav", What = "eight minutes of a near-empty band - stay QUIET",
                Expected = new string[0] },
        };

        static void Main(string[] args)
        {
            string folder = args.Length > 0 ? args[0] : ".";

            int found = 0, wanted = 0, noise = 0;

            foreach (var r in Recordings)
            {
                string path = Path.Combine(folder, r.File);
                if (!File.Exists(path)) { Console.WriteLine("missing: " + r.File); continue; }

                string got = Decode(path);
                string flat = Flatten(got);

                var missed = new List<string>();
                int here = 0;
                foreach (string want in r.Expected)
                {
                    wanted++;
                    if (flat.Contains(Flatten(want))) { found++; here++; }
                    else missed.Add(want);
                }

                if (r.Expected.Length == 0)
                {
                    // Every twenty letters of rubbish on an unreadable recording costs one mark.
                    noise += flat.Length / 20;
                    Console.WriteLine("{0,-12} {1,-46} {2} letters of noise (want 0)",
                        r.File, r.What, flat.Length);
                }
                else
                {
                    Console.WriteLine("{0,-12} {1,-46} {2} of {3}{4}",
                        r.File, r.What, here, r.Expected.Length,
                        missed.Count > 0 ? "   missed: " + string.Join(" ", missed.ToArray()) : "");
                }

                Console.WriteLine("             " + Squash(got));
            }

            Console.WriteLine();
            Console.WriteLine("REAL-SIGNAL SCORE: {0} of {1} words found, {2} off for noise  =>  {3}",
                found, wanted, noise, found - noise);
        }

        static string Flatten(string s)
        {
            var text = new StringBuilder();
            foreach (char c in s.ToUpperInvariant())
                if (char.IsLetterOrDigit(c)) text.Append(c);
            return text.ToString();
        }

        static string Squash(string s)
        {
            var text = new StringBuilder();
            bool space = true;
            foreach (char c in s)
            {
                bool blank = char.IsWhiteSpace(c);
                if (blank && space) continue;
                text.Append(blank ? ' ' : c);
                space = blank;
            }
            string one = text.ToString().Trim();
            return one.Length > 170 ? one.Substring(0, 170) + " ..." : one;
        }

        static string Decode(string path)
        {
            int rate;
            short[] audio = ReadWav(path, out rate);

            var plain = new CwDecoder(rate);
            var got = new StringBuilder();
            plain.Text += s => got.Append(s);

            int block = rate / 10;
            var buf = new short[block];
            for (int i = 0; i < audio.Length; i += block)
            {
                int n = Math.Min(block, audio.Length - i);
                Array.Copy(audio, i, buf, 0, n);
                plain.Process(buf, n);
            }
            return got.ToString();
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
