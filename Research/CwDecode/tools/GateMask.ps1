# WHEN DOES THE PLAIN DECODER BELIEVE A STATION IS SENDING? - one character per 5 ms reading.
#
#   0  no station (the signal test says the frequency is empty)
#   1  a station stands out, not yet proved to be Morse
#   2  a station, and its keying proved to be Morse
#
# The letter network hears letters in noise; the plain decoder is good at knowing when there is
# nothing there. This lets the network be heard only where the plain decoder says someone is sending.
# Compiled against a COPY of CwDecoder.cs; the program is not touched.
#
#   GateMask.ps1 <folder or wav> out.txt

param([Parameter(Mandatory=$true)][string]$Path, [Parameter(Mandatory=$true)][string]$Out)

$tools = $PSScriptRoot
$build = Join-Path $tools "..\build"
$app = Join-Path $tools "..\..\..\HolyLogger"
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

$src = [System.IO.File]::ReadAllText((Join-Path $app "CwDecoder.cs"))
$at = "            SignalPresent = standsOut;"
if (-not $src.Contains($at)) { "NOT FOUND: signal test"; exit 1 }
$src = $src.Replace($at, "$at`r`n            GateProbe.Add(SignalPresent, _looksLikeMorse);")

$probe = @"
using System;
using System.IO;
using System.Text;
namespace HolyLogger {
  static class Log { public static void Swallow(Exception e) { } }
  static class GateProbe {
    public static StringBuilder Out = new StringBuilder();
    public static void Add(bool present, bool morse) { Out.Append(!present ? '0' : (morse ? '2' : '1')); }
  }
  static class GateMain {
    static void Main(string[] args) {
      string p = args[0];
      string[] files = Directory.Exists(p) ? Directory.GetFiles(p, "*.wav") : new[] { p };
      Array.Sort(files);
      var all = new StringBuilder();
      foreach (string f in files) {
        GateProbe.Out.Clear();
        int rate; short[] a = Wav.Read(f, out rate);
        var d = new CwDecoder(rate); d.Text += s => { };
        int block = rate / 10; var buf = new short[block];
        for (int i = 0; i < a.Length; i += block) { int n = Math.Min(block, a.Length - i); Array.Copy(a, i, buf, 0, n); d.Process(buf, n); }
        all.Append(Path.GetFileNameWithoutExtension(f)).Append('\t').Append(GateProbe.Out.ToString()).Append('\n');
      }
      File.WriteAllText(args[1], all.ToString());
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
[System.IO.File]::WriteAllText("$build\GateVar.cs", $src)
[System.IO.File]::WriteAllText("$build\GateMain.cs", $probe)
& $csc /nologo /o /out:"$build\GateMask.exe" "$build\GateMain.cs" "$build\GateVar.cs" 2>&1 | Where-Object { $_ -notmatch "CS0162" } | Select-Object -First 3
& "$build\GateMask.exe" $Path $Out
