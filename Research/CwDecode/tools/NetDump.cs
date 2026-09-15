using System;
using System.Globalization;

namespace HolyLogger
{
    static class Log { public static void Swallow(Exception e) { } }

    // Prints what the C# network makes of each envelope number, so it can be set beside the same
    // network written independently in numpy. Reads the numbers on standard input, one per line.
    static class NetDump
    {
        static void Main(string[] args)
        {
            var net = new CwNeuralNet();
            string error;
            if (!net.Load(args[0], out error)) { Console.Error.WriteLine(error); return; }

            string line;
            while ((line = Console.ReadLine()) != null)
            {
                line = line.Trim();
                if (line.Length == 0 || line[0] == '#') continue;

                float[] y = net.Step(float.Parse(line, CultureInfo.InvariantCulture));

                var text = new System.Text.StringBuilder();
                for (int i = 0; i < y.Length; i++)
                {
                    if (i > 0) text.Append(' ');
                    text.Append(y[i].ToString("F6", CultureInfo.InvariantCulture));
                }
                Console.WriteLine(text.ToString());
            }
        }
    }
}
