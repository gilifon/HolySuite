using System;
using System.IO;
using System.Text;

namespace HolyLogger
{
    // DOES PLAIN COME BACK AFTER SILENCE? (2026-10-21) While we transmit, the decode window gives the readers
    // silence instead of our own keying. This reads a recording twice - as it is, and with [from, to) seconds
    // replaced by silence - and prints both texts, so what Plain does after the gap can be compared.
    //
    //   PlainGap.exe <wav> <from s> <to s>
    static class PlainGap
    {
        static int Main(string[] a)
        {
            byte[] raw = File.ReadAllBytes(a[0]);
            int rate = BitConverter.ToInt32(raw, 24);
            double from = double.Parse(a[1], System.Globalization.CultureInfo.InvariantCulture);
            double to = double.Parse(a[2], System.Globalization.CultureInfo.InvariantCulture);
            foreach (bool gap in new[] { false, true })
            {
                var plain = new CwDecoder(rate);
                var text = new StringBuilder();
                long done = 0;
                plain.Text += t => text.Append(t);
                int block = rate / 10;
                var b = new short[block];
                for (int i = 44; i + 2 * block <= raw.Length; )
                {
                    for (int n = 0; n < block; n++) { b[n] = BitConverter.ToInt16(raw, i); i += 2; }
                    double sec = done / (double)rate;
                    if (gap && sec >= from && sec < to) Array.Clear(b, 0, block);
                    if (sec >= to && sec < to + 0.1) text.Append(" | ");
                    plain.Process(b, block);
                    done += block;
                }
                Console.WriteLine((gap ? "WITH GAP: " : "AS IT IS: ") + text.ToString().Replace("\r", " ").Replace("\n", " "));
            }
            return 0;
        }
    }
}
