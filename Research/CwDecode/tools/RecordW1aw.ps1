# Records W1AW from several public KiwiSDRs at once, starting at a given UTC time.
#
# Every receiver on this list was tested and records beyond ten seconds (some others cut every
# listener off at ten), and every one heard W1AW's code practice clearly when tested. Different
# distances and bands on purpose: the same known text arrives with different fading and noise at
# each, so one transmission becomes several honest test recordings.
#
#   RecordW1aw.ps1 -StartUtc "22:00" -Minutes 60 -Name bulletin

param(
    [string]$StartUtc = "21:58",
    [int]$Minutes = 60,
    [string]$Name = "w1aw",
    [int]$SegmentSeconds = 600
)

$tools = $PSScriptRoot
$py = "C:\Users\user\AppData\Local\Temp\claude\D--Dropbox-LAB-X230-PC-Holysuit-clone-HolySuite\2f8ddf56-5512-4cdb-af92-2976c1b4bafc\scratchpad\pyenv\Scripts\python.exe"
$day = (Get-Date).ToUniversalTime().ToString("yyyyMMdd")
$dir = Join-Path $tools "..\training\w1aw\$day-$Name"
New-Item -ItemType Directory -Force $dir | Out-Null

$receivers = @(
    @("kiwisdr.k1ra.us",   "8075", "7047.5",  "K1RA_VA_40m"),
    @("sdr.k2rh.radio",    "8072", "7047.5",  "K2RH_NJ_40m"),
    @("w2naf.com",         "8073", "7047.5",  "W2NAF_PA_40m"),
    @("kiwi.hobiecat.cc",  "8073", "7047.5",  "Milton_ON_40m"),
    @("w3piesdr.ddns.net", "8073", "7047.5",  "W3PIE_WV_40m"),
    @("sdr.k1vl.com",      "8076", "3581.5",  "K1VL_VT_80m"),
    @("kiwi.sdr.audio",    "8073", "14047.5", "IHB_FL_20m")
)

$now = (Get-Date).ToUniversalTime()
$start = [datetime]::ParseExact($now.ToString("yyyy-MM-dd") + " " + $StartUtc, "yyyy-MM-dd HH:mm", $null)
if ($start -lt $now.AddMinutes(-1)) { $start = $start.AddDays(1) }
$wait = [int]($start - $now).TotalSeconds
if ($wait -gt 0) { "waiting $wait s until $StartUtc UTC"; Start-Sleep -Seconds $wait }

"recording $Minutes minutes from $($receivers.Count) receivers into $dir"
$total = $Minutes * 60
$procs = foreach ($r in $receivers) {
    Start-Process -FilePath $py -ArgumentList @("$tools\KiwiSession.py", $r[0], $r[1], $r[2], "$total", "$SegmentSeconds", "$dir\$($r[3])") `
        -RedirectStandardOutput "$dir\$($r[3]).log" -RedirectStandardError "$dir\$($r[3]).err" -PassThru -NoNewWindow
}
$procs | ForEach-Object { $_.WaitForExit() }
"done"
Get-ChildItem $dir -Filter *.wav | Select-Object Name, Length
