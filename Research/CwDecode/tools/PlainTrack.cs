using System;
using System.Globalization;
using System.IO;
using System.Text;

// THE NOTE PLAIN FOLLOWS, written down every 0.1 s (2026-10-19), for the centred network's benches: in HolyLogger the
// network is told which station to read by Plain's note, so the benches must give it the same. Feeds each WAV
// (16-bit mono, any rate) to HolyLogger's own CwDecoder in 0.1 s blocks, as the sound card does, and writes
// <wav>.track: one line per block, "seconds<TAB>Hz" - Hz 0 when Plain hears no signal.
//
//   PlainTrack.exe <folder or wav> [...]
static class PlainTrack
{
    static int Main(string[] args)
    {
        foreach (string arg in args)
        {
            var files = Directory.Exists(arg) ? Directory.GetFiles(arg, "*.wav", SearchOption.AllDirectories) : new[] { arg };
            foreach (string f in files) Track(f);
        }
        return 0;
    }

    static void Track(string path)
    {
        short[] x; int rate;
        Read(path, out x, out rate);
        var dec = new HolyLogger.CwDecoder(rate);
        int block = rate / 10;
        var sb = new StringBuilder();
        var b = new short[block];
        int heard = 0, n = 0;
        for (int at = 0; at + block <= x.Length; at += block)
        {
            Array.Copy(x, at, b, 0, block);
            dec.Process(b, block);
            double hz = dec.SignalPresent ? dec.ToneHz : 0;
            if (hz > 0) heard++;
            n++;
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:F2}\t{1:F1}\n", (at + block) / (double)rate, hz);
        }
        File.WriteAllText(path + ".track", sb.ToString());
        Console.WriteLine("{0}: {1} blocks, signal in {2}", Path.GetFileName(path), n, heard);
    }

    static void Read(string path, out short[] x, out int rate)
    {
        byte[] raw = File.ReadAllBytes(path);
        int ch = BitConverter.ToInt16(raw, 22);
        rate = BitConverter.ToInt32(raw, 24);
        int pos = 12, data = -1, len = 0;
        while (pos + 8 <= raw.Length)
        {
            string id = Encoding.ASCII.GetString(raw, pos, 4);
            int size = BitConverter.ToInt32(raw, pos + 4);
            if (id == "data") { data = pos + 8; len = Math.Min(size, raw.Length - data); break; }
            pos += 8 + size + (size & 1);
        }
        int frames = len / 2 / ch;
        x = new short[frames];
        for (int i = 0; i < frames; i++)
        {
            int s = 0;
            for (int c = 0; c < ch; c++) s += BitConverter.ToInt16(raw, data + 2 * (i * ch + c));
            x[i] = (short)(s / ch);
        }
    }
}
