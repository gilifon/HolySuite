using System;
using System.Globalization;
using System.IO;
using System.Text;

// PLAIN OVER HIS SAVED LIVE INPUT (2026-10-27): %TEMP%\HolyLogger_cw_input_<rate>.raw holds the exact sound the
// window gave Plain (16-bit mono, every run appended); %TEMP%\HolyLogger_cw_letters.tsv what Plain spelled live.
// This plays the whole file through one fresh Plain in 0.1 s blocks, as the sound card does, and writes every
// letter with the sample it ends at - for tools/LiveVsReplay.py to find where live and replay disagree.
//
//   PlainRaw.exe <in.raw> <rate> <out.tsv>
static class PlainRaw
{
    static int Main(string[] a)
    {
        int rate = int.Parse(a[1]);
        var dec = new HolyLogger.CwDecoder(rate);
        var outp = new StreamWriter(a[2], false, new UTF8Encoding(false));
        dec.LetterTimed += (l, s0, s1) => outp.Write(string.Format(CultureInfo.InvariantCulture, "{0}\t{1}\n", l, s1));
        dec.WordGapTimed += () => outp.Write(" \t-1\n");
        int block = rate / 10;
        var b = new short[block];
        var bytes = new byte[2 * block];
        using (var f = new FileStream(a[0], FileMode.Open, FileAccess.Read))
            while (f.Read(bytes, 0, bytes.Length) == bytes.Length)
            {
                Buffer.BlockCopy(bytes, 0, b, 0, bytes.Length);
                dec.Process(b, block);
            }
        outp.Close();
        return 0;
    }
}
