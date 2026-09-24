# Tries one line of CwDecoder.cs at several values and scores each against W1AW's published bulletin
# - thousands of words of real off-air CW, where the older bench could only be sure of thirty-four.
#
# Nothing is written back to the program: each value is compiled from a copy.
#
#   BulletinSweep.ps1 -Find 'const int ReadingsAveraged = 5;' -Pattern 'const int ReadingsAveraged = {0};' -Values 3,4,5,6,7
#
# -Find is the line as it stands; -Pattern is that line with {0} where the value goes.

param(
    [Parameter(Mandatory=$true)][string]$Find,
    [Parameter(Mandatory=$true)][string]$Pattern,
    [Parameter(Mandatory=$true)][string[]]$Values,
    [string]$Only = $null,
    [string]$Label = "value"
)

$tools = $PSScriptRoot
$build = Join-Path $tools "..\build"
$app = Join-Path $tools "..\..\..\HolyLogger"
$recordings = Join-Path $tools "..\recordings"
$bulletin = Join-Path $tools "..\training\w1aw\20260915-bulletin0000"
$py = Join-Path $PSScriptRoot "..\training\pyenv\Scripts\python.exe"     # its own Python - the one in the session scratchpad was wiped
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

$src = [System.IO.File]::ReadAllText((Join-Path $app "CwDecoder.cs"))
if (-not $src.Contains($Find)) { "NOT FOUND: $Find"; exit 1 }

foreach ($v in $Values) {
    $line = $Pattern -f $v
    [System.IO.File]::WriteAllText("$build\SwVar.cs", $src.Replace($Find, $line))
    & $csc /nologo /o /out:"$build\SwText.exe" "$tools\BulletinText.cs" "$build\SwVar.cs" 2>&1 | Select-Object -First 2
    if ($Only) { & "$build\SwText.exe" $bulletin $Only > "$build\sw.txt" } else { & "$build\SwText.exe" $bulletin > "$build\sw.txt" }
    $score = & $py "$tools\ScoreText.py" "$recordings\w1aw\ARLP037.txt" "$build\sw.txt"
    "{0} = {1,-6} {2}" -f $Label, $v, $score
}
