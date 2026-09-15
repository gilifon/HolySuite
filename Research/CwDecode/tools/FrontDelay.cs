using System;
using System.Collections.Generic;
using System.IO;

namespace HolyLogger
{
    static class Log { public static void Swallow(Exception e) { } }

    // HOW FAR BEHIND THE AUDIO IS THE ENVELOPE?
    //
    // The front end measures the note over a window of sound, so what it hands out at any moment
    // describes sound from slightly earlier. That delay has never mattered before - the plain
    // decoder measures lengths, and a constant delay does not change a length.
    //
    // IT MATTERS ENORMOUSLY FOR TRAINING. The labels are laid out on the ideal clock: at step t the
    // label says what the operator was sending at step t. But the network's INPUT at step t
    // describes what he was sending at t minus the delay. So it is being asked to predict the
    // future, which it cannot do, and it settles for answering late instead - measured at eight
    // steps late against the original network's one.
    //
    // Fixing that means shifting the labels by exactly this delay, so this measures it: key the tone
    // on at a known moment and see which envelope number first rises.
    static class FrontDelay
    {
        const int Rate = 8000;

        static void Main(string[] args)
        {
            double wpm = 20, tone = 600;
            double ditSeconds = 1.2 / wpm;
            int ditSamples = (int)Math.Round(ditSeconds * Rate);

            // Two dits of silence, then the tone hard on. No shaping and no noise: this is measuring
            // the front end alone, so nothing else should blur the edge.
            int silence = ditSamples * 4;
            int on = ditSamples * 6;
            var audio = new short[silence + on + silence];

            double phase = 0, step = 2 * Math.PI * tone / Rate;
            for (int i = 0; i < audio.Length; i++)
            {
                phase += step;
                bool keyed = i >= silence && i < silence + on;
                audio[i] = (short)(keyed ? Math.Sin(phase) * 12000 : 0);
            }

            var front = new CwNeuralFrontEnd(Rate);
            front.Configure(tone, wpm);

            var envelope = new List<float>();
            front.Envelope += v => envelope.Add(v);

            int block = Rate / 10;
            var buf = new short[block];
            for (int i = 0; i < audio.Length; i += block)
            {
                int n = Math.Min(block, audio.Length - i);
                Array.Copy(audio, i, buf, 0, n);
                front.Process(buf, n);
            }

            // Where the key went down, counted in envelope steps rather than samples.
            double perDit = CwNeuralFrontEnd.NumbersPerDit;
            double keyedAtStep = (silence / (double)ditSamples) * perDit;

            int rose = -1;
            for (int i = 0; i < envelope.Count; i++)
                if (envelope[i] > 0.5) { rose = i; break; }

            Console.WriteLine("envelope numbers produced: " + envelope.Count);
            Console.WriteLine("key went down at step {0:F1} on the ideal clock", keyedAtStep);
            Console.WriteLine("envelope first passed half at step {0}", rose);
            Console.WriteLine();
            Console.WriteLine("FRONT END DELAY: {0:F1} steps", rose - keyedAtStep);

            Console.Write("first 60 numbers: ");
            for (int i = 0; i < Math.Min(60, envelope.Count); i++)
                Console.Write(" .:-=+*#%@"[Math.Min(9, (int)(envelope[i] * 9.999))]);
            Console.WriteLine();
        }
    }
}
