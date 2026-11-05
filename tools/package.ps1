# Builds BDNukes in Release and produces release/BDNukes-<version>.zip
# Zip layout (what CKAN and manual installs expect):
#   GameData/BDNukes/...      the mod, including Plugins/BDNukes.dll, LICENSE and README
#   Ships/VAB, Ships/SPH      example craft
#
# Usage:  powershell -ExecutionPolicy Bypass -File tools/package.ps1 [-KSPDir "C:\...\Kerbal Space Program"]
param([string]$KSPDir = "")

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$modDir = Join-Path $repo "GameData\BDNukes"

$ver = Get-Content (Join-Path $modDir "BDNukes.version") -Raw | ConvertFrom-Json
$version = "$($ver.VERSION.MAJOR).$($ver.VERSION.MINOR).$($ver.VERSION.PATCH)"
Write-Host "Packaging BDNukes $version"

$buildArgs = @("build", (Join-Path $repo "src\BDNukes.csproj"), "-c", "Release", "-p:Version=$version")
if ($KSPDir -ne "") { $buildArgs += "-p:KSPDir=$KSPDir" }
& dotnet @buildArgs
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

$dll = Join-Path $modDir "Plugins\BDNukes.dll"
if (-not (Test-Path $dll)) { throw "BDNukes.dll missing after build" }
# the volumetric cloud shader bundle is built in Unity 2019.4.18f1: powershell -File unity/run-unity.ps1 BuildAll
$bundle = Join-Path $modDir "Shaders\bdnukes-windows.shaders"
if (-not (Test-Path $bundle)) { throw "Shader bundle missing: build it with unity/run-unity.ps1 BuildAll" }

$stage = Join-Path $repo "release\stage"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
$stageMod = Join-Path $stage "GameData\BDNukes"
New-Item -ItemType Directory -Force $stageMod | Out-Null
Copy-Item (Join-Path $modDir "*") $stageMod -Recurse -Exclude ".gitkeep"
Get-ChildItem $stageMod -Recurse -Filter ".gitkeep" | Remove-Item -Force
# PluginData holds the developer's own settings and logs: never ship it (it would overwrite players' settings)
$pluginData = Join-Path $stageMod "PluginData"
if (Test-Path $pluginData) { Remove-Item $pluginData -Recurse -Force }
Copy-Item (Join-Path $repo "LICENSE") $stageMod
Copy-Item (Join-Path $repo "README.md") $stageMod
Copy-Item (Join-Path $repo "CHANGELOG.md") $stageMod
# example craft go to <KSP>/Ships/VAB and Ships/SPH
$ships = Join-Path $repo "Ships"
if (Test-Path $ships) { Copy-Item $ships (Join-Path $stage "Ships") -Recurse }

$zip = Join-Path $repo "release\BDNukes-$version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
# Windows PowerShell's Compress-Archive writes '\' separators, which breaks CKAN and non-Windows unzip.
# Build the archive by hand with '/' entry names.
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::Open($zip, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    Get-ChildItem $stage -Recurse -File | ForEach-Object {
        $entry = $_.FullName.Substring($stage.Length + 1).Replace('\', '/')
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $_.FullName, $entry, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally { $archive.Dispose() }
Remove-Item $stage -Recurse -Force
Write-Host "Created $zip"
