# Builds the RTTY bench tools into ..\build with the .NET Framework compiler that comes with Windows.
# Nothing in here is part of the program or its installer.

$tools = $PSScriptRoot
$build = Join-Path $tools "..\build"
New-Item -ItemType Directory -Force $build | Out-Null
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

$out = & $csc /nologo /o /out:"$build\RttyBench.exe" "$tools\RttyBench.cs" "$tools\MmttyRef.cs" 2>&1
if ($LASTEXITCODE -ne 0) { "FAILED  RttyBench"; $out | Where-Object { $_ -match "error" } | Select-Object -First 10 } else { "built   RttyBench" }
