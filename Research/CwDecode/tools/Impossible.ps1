# Counts the mark and gap lengths the decoder actually sees, in units of the dit it had learned at
# the time. Compiles a COPY of CwDecoder.cs with two counting calls inserted; the program is not
# touched. See the head of Impossible.cs for what the answer is for.

$tools = $PSScriptRoot
$build = Join-Path $tools "..\build"
$app = Join-Path $tools "..\..\..\HolyLogger"
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

$src = [System.IO.File]::ReadAllText((Join-Path $app "CwDecoder.cs"))

$markAt = "            bool isDah = lengthMs > boundary;"
$gapAt = "            if (lengthMs < 5 || lengthMs > 3000) return;"
if (-not $src.Contains($markAt)) { "NOT FOUND: mark line"; exit 1 }
if (-not $src.Contains($gapAt)) { "NOT FOUND: gap line"; exit 1 }

$src = $src.Replace($markAt, "            Probe.Mark(lengthMs, _ditMs);`r`n$markAt")
$src = $src.Replace($gapAt, "$gapAt`r`n            Probe.Gap(lengthMs, _ditMs);")

[System.IO.File]::WriteAllText("$build\ImpVar.cs", $src)
& $csc /nologo /o /out:"$build\Impossible.exe" "$tools\Impossible.cs" "$build\ImpVar.cs" 2>&1 | Select-Object -First 3
& "$build\Impossible.exe" @args
