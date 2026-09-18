# WHEN DID EACH LETTER END? - the plain decoder's own answer, to the sample.
#
# Compiles a COPY of CwDecoder.cs that reports, for every letter it spells out, the moment the last
# element of that letter ended: the reading count at the time the letter is written, less the
# silence that has passed since the key went up. Letters the decoder cannot spell are reported too,
# as "#", so an aligner can see there WAS a letter there. The program itself is not touched.
#
# Output, one line per recording:   name<TAB>A@123456 B@125000 #@130000 ...   (samples at 8000 Hz)
#
#   LetterTimes.ps1 <folder or wav> [out.txt]

param([Parameter(Mandatory=$true)][string]$Path, [string]$Out)

$tools = $PSScriptRoot
$build = Join-Path $tools "..\build"
$app = Join-Path $tools "..\..\..\HolyLogger"
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

$src = [System.IO.File]::ReadAllText((Join-Path $app "CwDecoder.cs"))

$tick = "            if (_forgetAsked) { _forgetAsked = false; ForgetNow(); }"
$emit = "            string letter;"
if (-not $src.Contains($tick)) { "NOT FOUND: start of AnalyseWindow"; exit 1 }
if (-not $src.Contains($emit)) { "NOT FOUND: letter lookup in EmitLetter"; exit 1 }

$src = $src.Replace($tick, "            TimeProbe.Readings++;`r`n$tick")
$src = $src.Replace($emit, "            TimeProbe.Letter(FromMorseTable.ContainsKey(pattern) ? FromMorseTable[pattern] : `"#`", _stateMs);`r`n$emit")

$probe = @"
using System;
using System.IO;
using System.Text;
namespace HolyLogger {
  static class Log { public static void Swallow(Exception e) { } }
  static class TimeProbe {
    public static long Readings;
    public static StringBuilder Out = new StringBuilder();
    // One reading every 5 ms. The letter ended when the key went up, which is the silence so far ago.
    public static void Letter(string letter, double silenceMs) {
      double endMs = Readings * 5.0 - silenceMs;
      Out.Append(letter).Append('@').Append(((long)(endMs * 8)).ToString()).Append(' ');
    }
  }
  static class LetterTimesMain {
    static void Main(string[] args) {
      string p = args[0];
      string[] files = Directory.Exists(p) ? Directory.GetFiles(p, "*.wav") : new[] { p };
      Array.Sort(files);
      var all = new StringBuilder();
      foreach (string f in files) {
        TimeProbe.Readings = 0; TimeProbe.Out.Clear();
        int rate; short[] a = Wav.Read(f, out rate);
        if (rate != 8000) { Console.Error.WriteLine("skipped (not 8000 Hz): " + f); continue; }
        var d = new CwDecoder(rate); d.Text += s => { };
        int block = rate / 10; var buf = new short[block];
        for (int i = 0; i < a.Length; i += block) { int n = Math.Min(block, a.Length - i); Array.Copy(a, i, buf, 0, n); d.Process(buf, n); }
        all.Append(Path.GetFileNameWithoutExtension(f)).Append('\t').Append(TimeProbe.Out.ToString().Trim()).Append('\n');
      }
      if (args.Length > 1) File.WriteAllText(args[1], all.ToString()); else Console.Write(all.ToString());
    }
  }
  static class Wav {
    public static short[] Read(string path, out int rate) {
      using (var f = File.OpenRead(path)) using (var r = new BinaryReader(f)) {
        r.ReadBytes(12); rate = 8000; short ch = 1, bits = 16; short[] data = null;
        while (f.Position < f.Length - 8) {
          string id = new string(r.ReadChars(4)); int size = r.ReadInt32();
          if (id == "fmt ") { r.ReadInt16(); ch = r.ReadInt16(); rate = r.ReadInt32(); r.ReadInt32(); r.ReadInt16(); bits = r.ReadInt16(); if (size > 16) r.ReadBytes(size - 16); }
          else if (id == "data") { byte[] raw = r.ReadBytes(size); int n = raw.Length / (bits / 8) / ch; data = new short[n]; for (int i = 0; i < n; i++) data[i] = BitConverter.ToInt16(raw, i * ch * (bits / 8)); }
          else { if (size < 0 || f.Position + size > f.Length) break; r.ReadBytes(size); }
        }
        return data ?? new short[0];
      }
    }
  }
}
"@
[System.IO.File]::WriteAllText("$build\LtVar.cs", $src)
[System.IO.File]::WriteAllText("$build\LtMain.cs", $probe)
& $csc /nologo /o /out:"$build\LetterTimes.exe" "$build\LtMain.cs" "$build\LtVar.cs" 2>&1 | Where-Object { $_ -notmatch "CS0162" } | Select-Object -First 3
if ($Out) { & "$build\LetterTimes.exe" $Path $Out } else { & "$build\LetterTimes.exe" $Path }
