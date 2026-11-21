using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows.Controls;
using System.Windows.Documents;

namespace HolyLogger
{
    // WHAT HYBRID SHOWS, for WAVs (2026-10-21): the bench scored the network alone, never the text Hybrid puts on
    // the screen. This runs Plain and the network as the decode window does (0.1 s blocks, Plain's note handed to
    // the network after each block, the network's letters 0.8 dit earlier) and feeds HolyLogger's own CwMixText
    // exactly as FlushPendingText does, then reads the box. 18 s of silence at the end let the network finish.
    //
    // Writes four files, one line "name<TAB>text" per WAV, for tools/ScoreText.py:
    //   <out>_plain.txt  Plain alone          <out>_net.txt   the network alone
    //   <out>_hybrid.txt Hybrid, unconfirmed letters hidden (the default)
    //   <out>_grey.txt   Hybrid, unconfirmed letters shown
    //
    //   HybridText.exe <CwDeep.bin> <out prefix> <wav> [<wav> ...]
    static class HybridText
    {
        [STAThread]
        static int Main(string[] a)
        {
            // The network's reading (CwBeamReading): BEAM (0 = the old single best) and BONUS from the environment.
            string beam = Environment.GetEnvironmentVariable("BEAM"), bonus = Environment.GetEnvironmentVariable("BONUS");
            if (!string.IsNullOrEmpty(beam)) CwBeamReading.Beam = int.Parse(beam);
            if (!string.IsNullOrEmpty(bonus)) CwBeamReading.Bonus = double.Parse(bonus, System.Globalization.CultureInfo.InvariantCulture);
            var net = new CwDeepNet(); string err;
            if (!net.Load(a[0], out err)) { Console.WriteLine(err); return 1; }
            var outs = new Dictionary<string, StringBuilder>();
            foreach (string k in new[] { "plain", "net", "hybrid", "grey" }) outs[k] = new StringBuilder();
            for (int w = 2; w < a.Length; w++)
            {
                string name = Path.GetFileNameWithoutExtension(a[w]);
                string[] got = One(net, a[w]);
                outs["plain"].Append(name).Append('\t').Append(got[0]).Append('\n');
                outs["net"].Append(name).Append('\t').Append(got[1]).Append('\n');
                outs["hybrid"].Append(name).Append('\t').Append(got[2]).Append('\n');
                outs["grey"].Append(name).Append('\t').Append(got[3]).Append('\n');
                Console.WriteLine(name);
            }
            foreach (var kv in outs) File.WriteAllText(a[1] + "_" + kv.Key + ".txt", kv.Value.ToString());
            return 0;
        }

        static string[] One(CwDeepNet net, string path)
        {
            byte[] raw = File.ReadAllBytes(path);
            int rate = BitConverter.ToInt32(raw, 24);
            var plain = new CwDecoder(rate);
            var deep = new CwDeepDecoder(rate, net, false);
            string pitch = Environment.GetEnvironmentVariable("PITCH");      // his CW pitch setting (the window passes it); 700 if unset
            if (!string.IsNullOrEmpty(pitch)) deep.PitchHz = double.Parse(pitch, System.Globalization.CultureInfo.InvariantCulture);
            var mix = new CwMixText();
            var plainText = new StringBuilder();
            var netText = new StringBuilder();
            var times = new List<KeyValuePair<string, double>>();
            double note = 0, speed = 0;
            Func<double> early = () => 0.8 * 1.2 / (plain.Wpm >= 5 ? plain.Wpm : 20);

            // As the window does since 2026-10-21: Plain's letters and word gaps the moment it hears them.
            plain.LetterTimed += (l, s0, s1) => mix.AddPlain(l, s1 / (double)rate, note, speed);
            plain.WordGapTimed += () => mix.AddPlain(" ", double.NaN, note, speed);
            plain.Text += text => plainText.Append(text);
            deep.Text += text => netText.Append(text);
            deep.LetterTimed += (l, sec) => mix.AddNet(l, l == " " ? sec : sec - early());
            deep.Covered += (f, t) => mix.Covered(f - early(), t - early());

            int block = rate / 10;
            var b = new short[block];
            long samples = (raw.Length - 44) / 2;
            long silence = 18L * rate;
            for (long done = 0; done + block <= samples + silence; done += block)
            {
                for (int n = 0; n < block; n++)
                {
                    long k = done + n;
                    b[n] = k < samples ? BitConverter.ToInt16(raw, 44 + (int)(2 * k)) : (short)0;
                }
                plain.Process(b, block);
                note = plain.SignalPresent ? plain.ToneHz : 0;
                speed = plain.SignalPresent ? plain.Wpm : 0;
                deep.Process(b, block);
                deep.Note(note);
            }

            var box = (RichTextBox)mix.Box;
            mix.HideUnconfirmed = true;
            string hidden = Read(box);
            mix.HideUnconfirmed = false;
            string grey = Read(box);
            return new[] { Flat(plainText.ToString()), Flat(netText.ToString()), hidden, grey };
        }

        static string Read(RichTextBox box)
        {
            return Flat(new TextRange(box.Document.ContentStart, box.Document.ContentEnd).Text);
        }

        static string Flat(string s)
        {
            return s.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
        }
    }
}
