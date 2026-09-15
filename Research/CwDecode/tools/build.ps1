# Builds every C# test tool into ..\build, each compiled against the program's OWN decoder source
# files - so a tool always measures the code that ships, never a copy of it.
#
# Uses the .NET Framework compiler that comes with Windows: no Visual Studio needed, and nothing in
# here is part of the program or its installer.

$tools = $PSScriptRoot
$build = Join-Path $tools "..\build"
$app = Join-Path $tools "..\..\..\HolyLogger"
New-Item -ItemType Directory -Force $build | Out-Null
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

$decoder = "$app\CwDecoder.cs"
$net = "$app\CwNeuralNet.cs"
$front = "$app\CwNeuralFrontEnd.cs"
$neural = "$app\CwNeuralDecoder.cs"

$plan = [ordered]@{
    "RealBench"     = @($decoder)
    "DecoderTest"   = @($decoder)
    "Quiet"         = @($decoder)
    "Delay"         = @($decoder)
    "ScoreTraining" = @($decoder)
    "Judge"         = @($decoder, $net, $neural, $front)
    "Compare"       = @($decoder, $net, $neural, $front)
    "Recorder"      = @("$app\WaveInRecorder.cs")
    "MakeTraining"  = @()
    "MakeLabels"    = @($front)
    "FrontDelay"    = @($front)
    "CsAnswers"     = @($net, $front)
    "LabelProbe"    = @($net)
    "NetDump"       = @($net)
    "DumpEnvelope"  = @($decoder, $front)
}

foreach ($name in $plan.Keys) {
    $source = Join-Path $tools "$name.cs"
    if (-not (Test-Path $source)) { continue }
    $out = & $csc /nologo /o /out:"$build\$name.exe" $source @($plan[$name]) 2>&1
    if ($LASTEXITCODE -ne 0) { "FAILED  $name"; $out | Select-Object -First 5 } else { "built   $name" }
}
