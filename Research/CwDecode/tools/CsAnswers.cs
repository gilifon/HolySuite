using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace HolyLogger
{
    static class Log { public static void Swallow(Exception e) { } }

    // What the C# side makes of one recording, step by step: the envelope it produced and the
    // answer the network gave for each number of it. Printed so it can be set against the same
    // thing computed in Python from the same audio - if the two disagree the fault is in the path,
    // and if they agree the fault is in what the C# decoder does with the answers afterwards.
    static class CsAnswers
    {
        static void Main(string[] args)
        {
            string wav = args[0], weights = args[1];
            double tone = double.Parse(args[2], CultureInfo.InvariantCulture);
            double wpm = double.Parse(args[3], CultureInfo.InvariantCulture);

            var net = new CwNeuralNet();
            string error;
            if (!net.Load(weights, out error)) { Console.Error.WriteLine(error); return; }

            int rate;
            short[] audio = ReadWav(wav, out rate);

            var front = new CwNeuralFrontEnd(rate);
            front.Configure(tone, wpm);

            var envelope = new List<float>();
            var answers = new List<int>();

            front.Envelope += v =>
            {
                envelope.Add(v);
                float[] y = net.Step(v);
                int best = 0;
                for (int k = 1; k < y.Length; k++) if (y[k] > y[best]) best = k;
                answers.Add(best);
            };

            int block = rate / 10;
            var buf = new short[block];
            for (int i = 0; i < audio.Length; i += block)
            {
                int n = Math.Min(block, audio.Length - i);
                Array.Copy(audio, i, buf, 0, n);
                front.Process(buf, n);
            }

            var e = new StringBuilder();
            var a = new StringBuilder();
            for (int i = 0; i < envelope.Count; i++)
            {
                if (i > 0) e.Append(' ');
                e.Append(envelope[i].ToString("F4", CultureInfo.InvariantCulture));
                a.Append(answers[i]);
            }
            Console.WriteLine(e.ToString());
            Console.WriteLine(a.ToString());
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
