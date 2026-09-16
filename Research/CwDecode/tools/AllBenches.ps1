# Scores one value of one constant on ALL THREE benches at once.
#
# WHY ALL THREE, AND NEVER ONE ALONE. Tuned on W1AW's bulletin by itself, the two keying thresholds
# looked like a triumph - 2102 words read up to 2398, invented words 1186 down to 690. On the
# generated bench the same change fell from 247 of 256 to 188, and on the operator's own recordings
# from 32 of 34 to 12. The bulletin is recorded through internet receivers with their own AGC and
# audio path, not through his IC-7610, so tuning to it alone quietly fits the decoder to somebody
# else's radio. A change is only an improvement if it holds on all three.
#
#   AllBenches.ps1 -Find 'return _ditMs * 2.0;' -Pattern 'return _ditMs * {0};' -Values 1.8,2.0,2.2
#   AllBenches.ps1                     (no arguments: just score the decoder as it stands)

param(
    [string]$Find,
    [string]$Pattern,
    [string[]]$Values,
    [string]$Label = "value",
    [string]$BulletinOnly = "K1RA,Milton,W3PIE"      # three receivers is enough to see a real change
)

$tools = $PSScriptRoot
$build = Join-Path $tools "..\build"
$app = Join-Path $tools "..\..\..\HolyLogger"
$recordings = Join-Path $tools "..\recordings"
$bulletin = Join-Path $tools "..\training\w1aw\20260915-bulletin0000"
$py = "C:\Users\user\AppData\Local\Temp\claude\D--Dropbox-LAB-X230-PC-Holysuit-clone-HolySuite\2f8ddf56-5512-4cdb-af92-2976c1b4bafc\scratchpad\pyenv\Scripts\python.exe"
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$src = [System.IO.File]::ReadAllText((Join-Path $app "CwDecoder.cs"))

if ($Find -and -not $src.Contains($Find)) { "NOT FOUND: $Find"; exit 1 }
if (-not $Values) { $Values = @("as it stands") }

foreach ($v in $Values) {
    $variant = if ($Find) { $src.Replace($Find, ($Pattern -f $v)) } else { $src }
    [System.IO.File]::WriteAllText("$build\AbVar.cs", $variant)

    & $csc /nologo /o /out:"$build\AbDT.exe"  "$tools\DecoderTest.cs"  "$build\AbVar.cs" 2>&1 | Select-Object -First 2
    & $csc /nologo /o /out:"$build\AbRB.exe"  "$tools\RealBench.cs"    "$build\AbVar.cs" 2>&1 | Select-Object -First 2
    & $csc /nologo /o /out:"$build\AbBul.exe" "$tools\BulletinText.cs" "$build\AbVar.cs" 2>&1 | Select-Object -First 2

    $tot = 0; $pass = 0
    foreach ($s in 0,100,200,300,400,500,600,700) {
        $o = & "$build\AbDT.exe" $s | Select-Object -Last 1
        if ($o -match "(\d+) passed, (\d+) failed") { $pass += [int]$Matches[1]; $tot += [int]$Matches[1] + [int]$Matches[2] }
    }

    $real = 0
    $line = & "$build\AbRB.exe" $recordings | Select-Object -Last 1
    if ($line -match "=>\s+(-?\d+)") { $real = [int]$Matches[1] }

    "" | Out-File "$build\ab.txt" -Encoding utf8
    foreach ($who in $BulletinOnly.Split(",")) { & "$build\AbBul.exe" $bulletin $who | Out-File "$build\ab.txt" -Append -Encoding utf8 }
    $b = & $py "$tools\ScoreText.py" "$recordings\w1aw\ARLP037.txt" "$build\ab.txt"
    $read = if ($b -match "READ (\d+) ") { [int]$Matches[1] } else { 0 }
    $invented = if ($b -match "INVENTED (\d+)") { [int]$Matches[1] } else { 0 }

    "{0} = {1,-8} generated {2,3}/{3}   his recordings {4,3}/34   W1AW read {5,5}  invented {6,5}" -f $Label, $v, $pass, $tot, $real, $read, $invented
}
