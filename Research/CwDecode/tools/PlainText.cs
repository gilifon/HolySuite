using System;
using System.IO;
using System.Text;

// WHAT PLAIN PRINTS for a WAV (2026-10-20): HolyLogger's own CwDecoder fed 0.1 s blocks, as the sound card does;
// its text as it came out. For comparing Plain and Net on one recording.
//
//   PlainText.exe <wav> [...]
static class PlainText
{
    static int Main(string[] args)
    {
        foreach (string f in args)
        {
            byte[] raw = File.ReadAllBytes(f);
            int rate = BitConverter.ToInt32(raw, 24);
            var dec = new HolyLogger.CwDecoder(rate);
            var sb = new StringBuilder();
            dec.Text += s => sb.Append(s);
            int block = rate / 10;
            var b = new short[block];
            for (int i = 44; i + 2 * block <= raw.Length; )
            {
                for (int n = 0; n < block; n++) { b[n] = BitConverter.ToInt16(raw, i); i += 2; }
                dec.Process(b, block);
            }
            Console.WriteLine("{0}: {1}", Path.GetFileName(f), sb.ToString());
        }
        return 0;
    }
}
