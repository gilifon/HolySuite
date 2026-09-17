# HOW MANY LETTERS ARE DROPPED BECAUSE THEIR DITS AND DAHS SPELL NOTHING?
#
# EmitLetter prints nothing for a pattern that is not in the Morse table - the honest answer when a
# signal breaks up. If that happens often on a fading signal, the dropped patterns are where a repair
# would have to look; if it is rare, there is nothing there. Prints how many letters were spelled and
# how many dropped, and the commonest dropped patterns.
#
#   Unspelled.ps1 <folder or wav>

param([Parameter(Mandatory=$true)][string]$Path)

$tools = $PSScriptRoot
$build = Join-Path $tools "..\build"
$app = Join-Path $tools "..\..\..\HolyLogger"
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

$src = [System.IO.File]::ReadAllText((Join-Path $app "CwDecoder.cs"))
$at = "            if (FromMorseTable.TryGetValue(pattern, out letter)) Output(letter);"
if (-not $src.Contains($at)) { "NOT FOUND: letter output line"; exit 1 }
$src = $src.Replace($at, "            PatternProbe.Add(pattern, FromMorseTable.ContainsKey(pattern));`r`n$at")

$probe = @"
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
namespace HolyLogger {
  static class Log { public static void Swallow(Exception e) { } }
  static class PatternProbe {
    public static int Spelled, Dropped;
    public static Dictionary<string,int> DroppedPatterns = new Dictionary<string,int>();
    public static void Add(string p, bool ok) {
      if (ok) { Spelled++; return; }
      Dropped++; int n; DroppedPatterns.TryGetValue(p, out n); DroppedPatterns[p] = n + 1;
    }
  }
  static class UnspelledMain {
    static void Main(string[] args) {
      string p = args[0];
      string[] files = Directory.Exists(p) ? Directory.GetFiles(p, "*.wav") : new[] { p };
      foreach (string f in files) {
        int rate; short[] a = Wav.Read(f, out rate);
        var d = new CwDecoder(rate); d.Text += s => { };
        int block = rate / 10; var buf = new short[block];
        for (int i = 0; i < a.Length; i += block) { int n = Math.Min(block, a.Length - i); Array.Copy(a, i, buf, 0, n); d.Process(buf, n); }
      }
      int all = PatternProbe.Spelled + PatternProbe.Dropped;
      Console.WriteLine("{0} letters: {1} spelled, {2} dropped ({3:F1}%)", all, PatternProbe.Spelled, PatternProbe.Dropped,
        all == 0 ? 0 : PatternProbe.Dropped * 100.0 / all);
      foreach (var kv in PatternProbe.DroppedPatterns.OrderByDescending(k => k.Value).Take(15))
        Console.WriteLine("  {0,-10} {1}", kv.Key, kv.Value);
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
[System.IO.File]::WriteAllText("$build\UnspVar.cs", $src)
[System.IO.File]::WriteAllText("$build\UnspMain.cs", $probe)
& $csc /nologo /o /out:"$build\Unspelled.exe" "$build\UnspMain.cs" "$build\UnspVar.cs" 2>&1 | Where-Object { $_ -notmatch "CS0162" } | Select-Object -First 3
& "$build\Unspelled.exe" $Path
