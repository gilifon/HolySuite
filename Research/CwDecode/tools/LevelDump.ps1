# WHAT THE PLAIN DECODER SAW, every 5 ms: the loudness at its note, as a share of the span between
# its noise floor (0) and its peak (1). The same number its two keying lines are drawn on (start a
# mark at 0.55, keep it above 0.35), so what a missed element looked like can be read against them.
#
# One line per recording: name<TAB>level level level ...   (two decimals, one per 5 ms reading)
#
#   LevelDump.ps1 <folder or wav> out.txt

param([Parameter(Mandatory=$true)][string]$Path, [Parameter(Mandatory=$true)][string]$Out)

$tools = $PSScriptRoot
$build = Join-Path $tools "..\build"
$app = Join-Path $tools "..\..\..\HolyLogger"
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

$src = [System.IO.File]::ReadAllText((Join-Path $app "CwDecoder.cs"))
$at = "            double span = _peak - _noiseFloor;"
if (-not $src.Contains($at)) { "NOT FOUND: span line"; exit 1 }
$src = $src.Replace($at, "$at`r`n            LevelProbe.Add(span > 1e-9 ? (smoothed - _noiseFloor) / span : 0);")

$probe = @"
using System;
using System.Globalization;
using System.IO;
using System.Text;
namespace HolyLogger {
  static class Log { public static void Swallow(Exception e) { } }
  static class LevelProbe {
    public static StringBuilder Out = new StringBuilder();
    public static void Add(double v) { if (v < -1) v = -1; if (v > 3) v = 3; Out.Append(v.ToString("F2", CultureInfo.InvariantCulture)).Append(' '); }
  }
  static class LevelMain {
    static void Main(string[] args) {
      string p = args[0];
      string[] files = Directory.Exists(p) ? Directory.GetFiles(p, "*.wav") : new[] { p };
      Array.Sort(files);
      using (var w = new StreamWriter(args[1])) {
        foreach (string f in files) {
          LevelProbe.Out.Clear();
          int rate; short[] a = Wav.Read(f, out rate);
          var d = new CwDecoder(rate); d.Text += s => { };
          int block = rate / 10; var buf = new short[block];
          for (int i = 0; i < a.Length; i += block) { int n = Math.Min(block, a.Length - i); Array.Copy(a, i, buf, 0, n); d.Process(buf, n); }
          w.Write(Path.GetFileNameWithoutExtension(f)); w.Write('\t'); w.Write(LevelProbe.Out.ToString().TrimEnd()); w.Write('\n');
        }
      }
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
[System.IO.File]::WriteAllText("$build\LvVar.cs", $src)
[System.IO.File]::WriteAllText("$build\LvMain.cs", $probe)
& $csc /nologo /o /out:"$build\LevelDump.exe" "$build\LvMain.cs" "$build\LvVar.cs" 2>&1 | Where-Object { $_ -notmatch "CS0162" } | Select-Object -First 3
& "$build\LevelDump.exe" $Path $Out
