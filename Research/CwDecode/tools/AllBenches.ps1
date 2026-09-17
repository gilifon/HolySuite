# Scores one value of one constant on ALL FOUR benches at once.
#
# THE FOURTH, 4Z5SL: the operator's own IC-7610 at 30 W on 7028, keyed from HolyLogger's CW keyer
# on 16 September 2026, heard in Austria, Czechia and Hungary through public KiwiSDRs. Every letter
# sent is known (recordings\4z5sl\SENT.txt), and the recordings are cut to his transmission, so the
# other stations on the frequency - IK5WOB calling CQ before, G0BQV after - are not counted against
# the decoder. His own radio, keying and path: the closest of the four to what he actually runs.
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
#
# -Also holds fixed replacements applied to every value, "find=>replace||find=>replace", for sweeping
# one constant while another is held somewhere other than where the source has it.

param(
    [string]$Find,
    [string]$Pattern,
    [string[]]$Values,
    [string]$Label = "value",
    [string]$Also,
    [string]$BulletinOnly = "K1RA,Milton,W3PIE"      # three receivers is enough to see a real change
)

$tools = $PSScriptRoot
$build = Join-Path $tools "..\build"
$app = Join-Path $tools "..\..\..\HolyLogger"
$recordings = Join-Path $tools "..\recordings"
$bulletin = Join-Path $tools "..\training\w1aw\20260915-bulletin0000"
$own = Join-Path $recordings "4z5sl"
$own2 = Join-Path $recordings "4z5sl2"
$py = "C:\Users\user\AppData\Local\Temp\claude\D--Dropbox-LAB-X230-PC-Holysuit-clone-HolySuite\2f8ddf56-5512-4cdb-af92-2976c1b4bafc\scratchpad\pyenv\Scripts\python.exe"
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$src = [System.IO.File]::ReadAllText((Join-Path $app "CwDecoder.cs"))

if ($Also) {
    foreach ($pair in ($Also -split "\|\|")) {
        $parts = $pair -split "=>", 2
        if (-not $src.Contains($parts[0])) { "NOT FOUND: $($parts[0])"; exit 1 }
        $src = $src.Replace($parts[0], $parts[1])
    }
}

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

    # BOTH of his transmissions, counted together - 16 September on 7028 (three receivers, a busy
    # frequency) and 17 September on 7036 (five receivers, clear, all three parts sent).
    $ownRead = 0; $ownOf = 0; $ownInvented = 0
    foreach ($folder in @($own, $own2)) {
        & "$build\AbBul.exe" $folder | Out-File "$build\own.txt" -Encoding utf8
        $o = & $py "$tools\ScoreText.py" "$folder\SENT.txt" "$build\own.txt"
        if ($o -match "READ (\d+) of (\d+)") { $ownRead += [int]$Matches[1]; $ownOf += [int]$Matches[2] }
        if ($o -match "INVENTED (\d+)") { $ownInvented += [int]$Matches[1] }
    }

    "{0} = {1,-8} generated {2,3}/{3}   his recordings {4,3}/34   W1AW read {5,5}  invented {6,5}   4Z5SL read {7,3}/{8}  invented {9,3}" -f $Label, $v, $pass, $tot, $real, $read, $invented, $ownRead, $ownOf, $ownInvented
}
