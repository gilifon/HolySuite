using System;
using System.IO;
// THE GAP BEFORE EVERY LETTER (2026-10-29): Plain spelled DG8NRI's CQ as "D G 8 N R I" on his live audio. This
// prints each letter Plain wrote with the silence before it in Plain's own dits, and whether a word space came
// out in that silence, so the operator's real letter and word gaps can be seen.
//   PlainLetterGaps.exe file.wav
namespace HolyLogger
{
    static class Log { public static void Swallow(Exception e) { } }
    static class PlainLetterGaps
    {
        static void Main(string[] a)
        {
            var bytes = File.ReadAllBytes(a[0]);
            int rate = BitConverter.ToInt32(bytes, 24);
            var d = new CwDecoder(rate);
            long lastEnd = -1; bool space = false;
            d.Text += s => { if (s.Contains(" ")) space = true; };
            d.LetterTimed += (l, s, e) =>
            {
                double ditMs = 1200.0 / d.Wpm;
                double gap = lastEnd < 0 ? -1 : (s - lastEnd) * 1000.0 / rate / ditMs;
                Console.WriteLine("{0,8:F2}\t{1}\t{2:F2}\t{3}\t{4:F1}", s / (double)rate, l, gap, space ? "SP" : "", d.Wpm);
                lastEnd = e; space = false;
            };
            int block = rate / 200; var buf = new short[block];
            for (int i = 44; i + 2 * block <= bytes.Length; i += 2 * block)
            {
                for (int k = 0; k < block; k++) buf[k] = BitConverter.ToInt16(bytes, i + 2 * k);
                d.Process(buf, block);
            }
        }
    }
}
