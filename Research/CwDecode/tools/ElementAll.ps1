# THE NEW DECODER (CwElementDecoder) ON EVERY BENCH AT ONCE - the companion of AllBenches.ps1, which
# scores the plain one. Switches are set through environment variables (see ElementBench.cs and
# OverStartBench.cs), so a change can be scored with and without it from the same build:
#
#   $env:SPEED_FROM_EDGES="0"; .\ElementAll.ps1 -Label "no edges"
#
# Air = his three on-air sessions heard through European KiwiSDRs (SENT.txt known), W1AW = three
# receivers of the 15 Sep bulletin, fist = weak hand-style sending, RealBench = his own IC-7610
# recordings (34 known answers), QSO = OverStartBench (generated two-station QSOs, callsigns read).
param([string]$Label = "as it stands", [switch]$NoBuild, [string]$Tag = "")

$tools = $PSScriptRoot
$build = Join-Path $tools "..\build"
$app = Join-Path $tools "..\..\..\HolyLogger"
$recordings = Join-Path $tools "..\recordings"
$bulletin = Join-Path $tools "..\training\w1aw\20260915-bulletin0000"
$py = Join-Path $tools "..\training\pyenv\Scripts\python.exe"
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

if (-not $NoBuild) {
    & $csc /nologo /o /nowarn:162,649 /out:"$build\EB.exe"  "$tools\ElementBench.cs" "$tools\BenchSwitches.cs" "$app\CwElementDecoder.cs" "$app\CwDecoder.cs" | Select-Object -First 3
    & $csc /nologo /o /nowarn:162,649 /out:"$build\ERB.exe" "$build\ElementRealBench.cs" "$tools\BenchSwitches.cs" "$app\CwElementDecoder.cs" "$app\CwDecoder.cs" | Select-Object -First 3
    & $csc /nologo /o /nowarn:162,649 /out:"$build\OSB.exe" "$tools\OverStartBench.cs" "$tools\BenchSwitches.cs" "$app\CwElementDecoder.cs" "$app\CwDecoder.cs" | Select-Object -First 3
}

$airRead = 0; $airOf = 0; $airInv = 0
foreach ($f in "4z5sl","4z5sl2","4z5sl3") {
    & "$build\EB.exe" "$recordings\$f" 2>$null | Out-File "$build\ea_air$Tag.txt" -Encoding utf8
    $o = & $py "$tools\ScoreText.py" "$recordings\$f\SENT.txt" "$build\ea_air$Tag.txt"
    if ($o -match "READ (\d+) of (\d+)") { $airRead += [int]$Matches[1]; $airOf += [int]$Matches[2] }
    if ($o -match "INVENTED (\d+)") { $airInv += [int]$Matches[1] }
}

"" | Out-File "$build\ea_w1aw$Tag.txt" -Encoding utf8
foreach ($who in "K1RA","Milton","W3PIE") { & "$build\EB.exe" $bulletin $who 2>$null | Out-File "$build\ea_w1aw$Tag.txt" -Append -Encoding utf8 }
$o = & $py "$tools\ScoreText.py" "$recordings\w1aw\ARLP037.txt" "$build\ea_w1aw$Tag.txt"
$wRead = if ($o -match "READ (\d+) ") { [int]$Matches[1] } else { 0 }
$wInv = if ($o -match "INVENTED (\d+)") { [int]$Matches[1] } else { 0 }

& "$build\EB.exe" "$recordings\fist1" 2>$null | Out-File "$build\ea_fist$Tag.txt" -Encoding utf8
$o = & $py "$tools\ScoreText.py" "$recordings\fist1\SENT.txt" "$build\ea_fist$Tag.txt"
$fRead = if ($o -match "READ (\d+) of") { [int]$Matches[1] } else { 0 }
$fInv = if ($o -match "INVENTED (\d+)") { [int]$Matches[1] } else { 0 }

$line = & "$build\ERB.exe" $recordings 2>$null | Select-Object -Last 1
$real = if ($line -match "=>\s+(-?\d+)") { [int]$Matches[1] } else { "?" }

$q = & "$build\OSB.exe" 12 | Select-String "^New"
$qso = if ("$q" -match "calls (\d+/\d+)") { $Matches[1] } else { "?" }

"{0,-22} air {1}/{2} inv {3}   W1AW {4} inv {5}   fist {6} inv {7}   RealBench {8}/34   QSO calls {9}" -f $Label, $airRead, $airOf, $airInv, $wRead, $wInv, $fRead, $fInv, $real, $qso
