using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace HolyLogger
{
    static class Log { public static void Swallow(Exception e) { } }

    // WHAT DID THE ORIGINAL AUTHOR LABEL EACH MOMENT AS?
    //
    // To train a replacement network the training data has to be labelled the way HIS was, or the
    // C# side that reads the answers - which is already proven correct - will read the new network
    // wrongly. Two of the seven answers are named in the code ("character ended", "word ended") and
    // five are "which element is being sent". What is NOT written down anywhere is what he labelled
    // the gap BETWEEN two elements of one character: the element just finished, the one about to
    // start, or something else again.
    //
    // Rather than guess, ask his own trained network. Feed it clean Morse whose keying is known
    // exactly, and print what it answers against what the key was actually doing. Whatever it says
    // during those gaps is the convention to copy.
    static class LabelProbe
    {
        static void Main(string[] args)
        {
            var net = new CwNeuralNet();
            string error;
            if (!net.Load(args[0], out error)) { Console.Error.WriteLine(error); return; }

            // "R" is .-. : three elements, two gaps inside the character. Short enough to read here.
            double[] envelope = KeyToEnvelope(".-.", 7.69);

            Console.WriteLine("step  envelope   answer  (0 char-sep, 1 word-sep, 2-6 element 1-5)");
            for (int i = 0; i < envelope.Length; i++)
            {
                float[] y = net.Step((float)envelope[i]);
                int best = 0;
                for (int k = 1; k < y.Length; k++) if (y[k] > y[best]) best = k;

                Console.WriteLine("{0,4}  {1,7:F2}   {2}   {3}",
                    i, envelope[i], best, Bars(y, best));
            }
        }

        static string Bars(float[] y, int best)
        {
            var s = new StringBuilder();
            for (int k = 0; k < y.Length; k++)
                s.Append(k == best ? "[" : " ").Append(y[k].ToString("F1", CultureInfo.InvariantCulture))
                 .Append(k == best ? "]" : " ");
            return s.ToString();
        }

        // The key, at the front end's own scale: a dit is 7.69 numbers, a dah three of those, the gap
        // inside a character one, and the gap after it three.
        static double[] KeyToEnvelope(string pattern, double perDit)
        {
            var v = new System.Collections.Generic.List<double>();
            for (int i = 0; i < (int)(perDit * 4); i++) v.Add(0.0);

            for (int e = 0; e < pattern.Length; e++)
            {
                int on = (int)Math.Round(perDit * (pattern[e] == '-' ? 3 : 1));
                for (int i = 0; i < on; i++) v.Add(1.0);
                if (e < pattern.Length - 1) for (int i = 0; i < (int)Math.Round(perDit); i++) v.Add(0.0);
            }

            for (int i = 0; i < (int)(perDit * 8); i++) v.Add(0.0);
            return v.ToArray();
        }
    }
}
