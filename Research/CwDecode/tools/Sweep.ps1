# Sweeps one constant in CwDecoder.cs across three measures at once: the 256-case generated bench,
# the real-signal word score, and how long the operator waits before the first letter appears.
# Nothing is written back to the project - each variant is compiled from a copy.
param(
    [Parameter(Mandatory=$true)][string]$Find,
    [Parameter(Mandatory=$true)][string[]]$Values,
    [string]$Label = "value"
)

# Everything is found from where this script sits, so it runs from any checkout.
$tools = $PSScriptRoot
$recordings = Join-Path $tools "..\recordings"
$build = Join-Path $tools "..\build"
$app = Join-Path $tools "..\..\..\HolyLogger"
New-Item -ItemType Directory -Force $build | Out-Null

$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$src = [System.IO.File]::ReadAllText((Join-Path $app "CwDecoder.cs"))
$wavs = "slow.wav","quiet.wav","radio.wav","radio2.wav","narrow.wav","ly2px.wav","ly2px2.wav"

if (-not $src.Contains($Find)) { "NOT FOUND: $Find"; exit 1 }

foreach ($v in $Values) {
    $variant = $src.Replace($Find, ($Find -replace '=\s*[\d.]+\s*;', "= $v;"))
    [System.IO.File]::WriteAllText("$build\Var.cs", $variant)

    & $csc /nologo /o /out:"$build\VarDT.exe" "$tools\DecoderTest.cs" "$build\Var.cs" 2>&1 | Select-Object -First 2
    & $csc /nologo /o /out:"$build\VarRB.exe" "$tools\RealBench.cs" "$build\Var.cs" 2>&1 | Select-Object -First 2
    & $csc /nologo /o /out:"$build\VarDL.exe" "$tools\Delay.cs"      "$build\Var.cs" 2>&1 | Select-Object -First 2

    $tot = 0; $pass = 0
    foreach ($s in 0,100,200,300,400,500,600,700) {
        $o = & "$build\VarDT.exe" $s | Select-Object -Last 1
        if ($o -match "(\d+) passed, (\d+) failed") { $pass += [int]$Matches[1]; $tot += [int]$Matches[1] + [int]$Matches[2] }
    }

    $real = 0
    $line = & "$build\VarRB.exe" $recordings | Select-Object -Last 1
    if ($line -match "=>\s+(-?\d+)") { $real = [int]$Matches[1] }

    $waits = @()
    foreach ($w in $wavs) {
        $d = & "$build\VarDL.exe" "$recordings\$w"
        if ($d -match "WAIT\s+(-?[\d.]+)s") { $waits += [double]$Matches[1] }
    }
    $avg = ($waits | Measure-Object -Average).Average
    $max = ($waits | Measure-Object -Maximum).Maximum

    "{0} = {1,-5}  generated {2,3}/{3}   real {4,3}   wait avg {5,5:F1}s  worst {6,5:F1}s" -f $Label, $v, $pass, $tot, $real, $avg, $max
}
