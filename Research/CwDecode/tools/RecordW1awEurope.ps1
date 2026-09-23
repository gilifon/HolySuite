# W1AW'S EVENING CODE BULLETIN, HEARD IN EUROPE - recorded every day by itself.
#
# WHY EUROPE. The letter network was taught on W1AW heard by receivers in the USA and Canada, where
# the signal comes in strong. It now equals the plain decoder, and what it lacks is exactly the case
# where a network could win: weak, fading CW. Across the Atlantic W1AW arrives weak and fading - and
# ARRL still publishes every word, so every letter is known. Each evening becomes new training
# material with nobody at the radio.
#
# WHEN. W1AW's schedule is fixed in US Eastern time, and this script works the hour out itself
# through Windows' own time zones, so the daylight-saving changes on both sides of the Atlantic need
# nobody. Recording starts three minutes early and runs 35 minutes.
#
# THE 17:00 ET BULLETIN IS NO USE HERE, MEASURED. Four nights of it (18, 19, 21, 22 September) were
# recorded on all six receivers and hold no W1AW at all: 24 to 27 dB of something in the passband
# against the American copies' 48, and the decoder reads nothing but stray letters. 17:00 ET is
# broad daylight in Connecticut and the forty-metre path over the Atlantic is not open. So the
# default is now the 20:00 ET bulletin - midnight UTC, three in the morning in Israel - when that
# path usually is.
#
# RUN BY A WINDOWS SCHEDULED TASK ("HolySuite W1AW Europe"), started each evening well before the
# bulletin; it waits here until the time. Started after the bulletin has begun, it does nothing - the
# next evening is soon enough. The computer has to be on (the task may wake it from sleep).
#
# Receivers: each tested on W1AW's 40 m frequency on 18 September 2026 and recording past the
# ten-second cut-off some receivers impose. Output: training\w1aw\<yyyyMMdd>-europe\.

param([int]$EasternHour = 20, [int]$Minutes = 35, [int]$SegmentSeconds = 600, [switch]$Now)

$tools = $PSScriptRoot
$py = Join-Path $tools "..\training\pyenv\Scripts\python.exe"

$receivers = @(
    @("malinheadkiwi.hopto.org", "8073", "7047.5",  "EI_Malin_40m"),
    @("websdr.heppen.be",        "8073", "7047.5",  "ON_Heppen_40m"),
    @("f4joy.ddns.net",          "8073", "7047.5",  "F_Pradiers_40m"),
    @("hb9cwk.internet-box.ch",  "8073", "7047.5",  "HB_Heimiswil_40m"),
    @("dl2sba.ddns.net",         "8073", "7047.5",  "DL_Filderstadt_40m"),
    @("websdr.heppen.be",        "8073", "3581.5",  "ON_Heppen_80m")   # 20 m is shut at midnight UTC; 80 m can be open
)

$eastern = [System.TimeZoneInfo]::FindSystemTimeZoneById("Eastern Standard Time")
$nowUtc = (Get-Date).ToUniversalTime()
$todayEastern = [System.TimeZoneInfo]::ConvertTimeFromUtc($nowUtc, $eastern).Date
$bulletinEastern = [datetime]::SpecifyKind($todayEastern.AddHours($EasternHour), [System.DateTimeKind]::Unspecified)
$bulletinUtc = [System.TimeZoneInfo]::ConvertTimeToUtc($bulletinEastern, $eastern)
$startUtc = $bulletinUtc.AddMinutes(-3)

if ($Now) { $startUtc = $nowUtc }
elseif ($nowUtc -gt $startUtc.AddMinutes(5)) { "today's bulletin began at $($bulletinUtc.ToString('HH:mm')) UTC - too late, nothing to do"; exit 0 }

$dir = Join-Path $tools ("..\training\w1aw\" + $bulletinUtc.ToString("yyyyMMdd") + "-europe")
New-Item -ItemType Directory -Force $dir | Out-Null
$log = Join-Path $dir "session.log"

$wait = [int]($startUtc - (Get-Date).ToUniversalTime()).TotalSeconds
"$(Get-Date -Format s) waiting $wait s for the bulletin at $($bulletinUtc.ToString('HH:mm')) UTC" | Out-File $log -Append
if ($wait -gt 0) { Start-Sleep -Seconds $wait }

"$(Get-Date -Format s) recording $Minutes minutes from $($receivers.Count) receivers" | Out-File $log -Append
$total = $Minutes * 60
$procs = foreach ($r in $receivers) {
    Start-Process -FilePath $py -ArgumentList @("$tools\KiwiSession.py", $r[0], $r[1], $r[2], "$total", "$SegmentSeconds", "$dir\$($r[3])") `
        -RedirectStandardOutput "$dir\$($r[3]).log" -RedirectStandardError "$dir\$($r[3]).err" -PassThru -WindowStyle Hidden
}
$procs | ForEach-Object { $_.WaitForExit() }
"$(Get-Date -Format s) done: $((Get-ChildItem $dir -Filter *.wav).Count) files" | Out-File $log -Append
