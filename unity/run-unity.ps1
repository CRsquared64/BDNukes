# Runs a BDNukesBuild editor method in batch mode and waits for Unity itself (not its leftover helper processes).
#   powershell -File run-unity.ps1 BuildAll
#   powershell -File run-unity.ps1 RenderTests
param([Parameter(Mandatory = $true)][string]$Method, [string]$Unity = "D:\Unity\2019.4.18f1\Editor\Unity.exe")
$proj = $PSScriptRoot
New-Item -ItemType Directory -Force "$proj\Build" | Out-Null
$log = "$proj\Build\$Method.log"
$sw = [Diagnostics.Stopwatch]::StartNew()
$p = Start-Process $Unity -ArgumentList '-batchmode', '-quit', '-projectPath', "`"$proj`"", '-executeMethod', "BDNukesBuild.$Method", '-logFile', "`"$log`"" -PassThru
$p.WaitForExit()
"Unity exit $($p.ExitCode) after $([int]$sw.Elapsed.TotalSeconds) s"
Select-String $log -Pattern '\[BDNukesBuild\]|Shader error|error CS|Exception:' | Select-Object -First 20 | ForEach-Object Line
exit $p.ExitCode
