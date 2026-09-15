using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace HolyLogger
{
    static class Log { public static void Swallow(Exception e) { } }

    // TURNS THE GENERATED AUDIO INTO WHAT THE NETWORK IS TAUGHT FROM.
    //
    // Two rows per example: the envelope the front end produces from the audio, and beside it, for
    // every one of those numbers, which of the seven answers is the right one.
    //
    // THE SEVEN, and the convention is not invented here - it was read off the original author's own
    // trained network by feeding it a known ".-." and watching what it said (see LabelProbe):
    //
    //   0  the character has ended - the gap between two letters
    //   1  the word has ended - the longer gap between two words
    //   2..6  the first, second .. fifth element of this character is in progress
    //
    // AND THE ELEMENT HOLDS THROUGH THE GAP AFTER IT. That is the part nobody wrote down and it
    // matters: an element and the silence that follows it inside a character are labelled together.
    // It is also why the C# reader draws its dit/dah line at 21 steps when a dit is 7.69 by
    // construction - a dit plus its gap is 15.4 and a dah plus its gap is 30.7, and 21 sits between
    // them. Label it any other way and that line, and everything tuned around it, becomes wrong.
    //
    // The envelope comes from the REAL front end, the same CwNeuralFrontEnd the program runs, so
    // what the network is taught from is exactly what it will be shown in service.
    static class MakeLabels
    {
        static readonly Dictionary<char, string> Morse = new Dictionary<char, string>
        {
            {'A',".-"},{'B',"-..."},{'C',"-.-."},{'D',"-.."},{'E',"."},{'F',"..-."},{'G',"--."},
            {'H',"...."},{'I',".."},{'J',".---"},{'K',"-.-"},{'L',".-.."},{'M',"--"},{'N',"-."},
            {'O',"---"},{'P',".--."},{'Q',"--.-"},{'R',".-."},{'S',"..."},{'T',"-"},{'U',"..-"},
            {'V',"...-"},{'W',".--"},{'X',"-..-"},{'Y',"-.--"},{'Z',"--.."},
            {'0',"-----"},{'1',".----"},{'2',"..---"},{'3',"...--"},{'4',"....-"},{'5',"....."},
            {'6',"-...."},{'7',"--..."},{'8',"---.."},{'9',"----."},
            {'/',"-..-."},{'?',"..--.."},{'=',"-...-"},{'.',".-.-.-"},{',',"--..--"}
        };

        static void Main(string[] args)
        {
            string folder = Path.Combine(args.Length > 0 ? args[0] : ".", "training");
            string index = Path.Combine(folder, "index.txt");
            if (!File.Exists(index)) { Console.WriteLine("no index.txt - run MakeTraining first"); return; }

            int written = 0, skipped = 0;
            using (var outFile = new StreamWriter(Path.Combine(folder, "labelled.txt")))
            {
                foreach (string line in File.ReadAllLines(index))
                {
                    string[] bits = line.Split('\t');
                    if (bits.Length < 5) continue;

                    string wav = Path.Combine(folder, bits[0] + ".wav");
                    if (!File.Exists(wav)) continue;

                    string text = bits[1];
                    double wpm = double.Parse(bits[2], CultureInfo.InvariantCulture);
                    double tone = double.Parse(bits[3], CultureInfo.InvariantCulture);

                    int rate;
                    short[] audio = ReadWav(wav, out rate);

                    var envelope = Envelope(audio, rate, tone, wpm);
                    var labels = Labels(text, wpm, rate, envelope.Count);

                    if (labels == null) { skipped++; continue; }

                    var e = new StringBuilder();
                    var l = new StringBuilder();
                    for (int i = 0; i < envelope.Count; i++)
                    {
                        if (i > 0) { e.Append(' '); l.Append(' '); }
                        e.Append(envelope[i].ToString("F4", CultureInfo.InvariantCulture));
                        l.Append(labels[i]);
                    }

                    outFile.WriteLine(bits[0] + "\t" + text + "\t" + e + "\t" + l);
                    written++;
                }
            }

            Console.WriteLine("labelled " + written + " examples" + (skipped > 0 ? ", skipped " + skipped : ""));
        }

        static List<float> Envelope(short[] audio, int rate, double tone, double wpm)
        {
            var front = new CwNeuralFrontEnd(rate);
            front.Configure(tone, wpm);

            var all = new List<float>();
            front.Envelope += v => all.Add(v);

            int block = rate / 10;
            var buf = new short[block];
            for (int i = 0; i < audio.Length; i += block)
            {
                int n = Math.Min(block, audio.Length - i);
                Array.Copy(audio, i, buf, 0, n);
                front.Process(buf, n);
            }
            return all;
        }

        // The right answer for every step, laid out on the front end's own clock: it stretches time
        // so that a dit is always NumbersPerDit steps whatever the speed, which is the whole reason
        // the network never has to know how fast anybody is sending.
        static int[] Labels(string text, double wpm, int rate, int howMany)
        {
            double perDit = CwNeuralFrontEnd.NumbersPerDit;

            var seq = new List<int>();
            Action<double, int> hold = (dits, what) =>
            {
                int n = (int)Math.Round(dits * perDit);
                for (int i = 0; i < n; i++) seq.Add(what);
            };

            // MUST MATCH MakeTraining's own layout exactly, or the labels slide against the audio.
            hold(4, 1);                       // the band before he starts - a word gap

            // Nothing sent at all: the whole recording is band noise, and every step of it is
            // labelled "no word in progress". See the note in MakeTraining about why a fifth of the
            // set is like this.
            if (text.Trim().Length == 0)
            {
                var quiet = new int[howMany];
                for (int i = 0; i < howMany; i++) quiet[i] = 1;
                return quiet;
            }

            foreach (char c in text.ToUpperInvariant())
            {
                if (c == ' ') { hold(4, 1); continue; }
                string pattern;
                if (!Morse.TryGetValue(c, out pattern)) continue;
                if (pattern.Length > 5) return null;         // nothing here has six elements

                for (int i = 0; i < pattern.Length; i++)
                {
                    int element = 2 + i;
                    // The element itself...
                    hold(pattern[i] == '-' ? 3 : 1, element);
                    // ...and the gap after it inside the character, labelled the same. This is the
                    // convention read off the original network, and the reason the dit/dah line is
                    // drawn where it is.
                    if (i < pattern.Length - 1) hold(1, element);
                }
                hold(3, 0);                   // the character has ended
            }

            hold(6, 1);

            // The front end may hand back a few more or fewer numbers than the arithmetic predicts -
            // it works in whole blocks of audio. Pad with "word ended", which is what silence is.
            var labels = new int[howMany];
            for (int i = 0; i < howMany; i++) labels[i] = i < seq.Count ? seq[i] : 1;
            return labels;
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
