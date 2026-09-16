# How much is thrown away because the signal test says "no station"?
#
# When SignalPresent goes false the letter half spelled out is dropped on the floor, on purpose (it
# is what kept lone Es off the screen). On a signal that fades - which is every signal through an
# internet receiver - that could be throwing away real letters by the hundred. This counts, on a
# copy of the decoder, how many half letters die that way and how much of the recording the signal
# test calls empty. The program itself is not touched.

$tools = $PSScriptRoot
$build = Join-Path $tools "..\build"
$app = Join-Path $tools "..\..\..\HolyLogger"
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

$src = [System.IO.File]::ReadAllText((Join-Path $app "CwDecoder.cs"))

$at = "                            && span > 1e-5;"
if (-not $src.Contains($at)) { "NOT FOUND: signal test"; exit 1 }
$src = $src.Replace($at, "$at`r`n            Probe.Frame(SignalPresent, _letterMarks.Count);")

[System.IO.File]::WriteAllText("$build\DropVar.cs", $src)
& $csc /nologo /o /out:"$build\Dropped.exe" "$tools\Dropped.cs" "$build\DropVar.cs" 2>&1 | Select-Object -First 3
& "$build\Dropped.exe" @args
