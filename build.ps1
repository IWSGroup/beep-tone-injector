# Builds BeepTone.exe, BeepToneCtl.exe and dist\BeepTone.msi, then runs the self-test.
# Needs the .NET SDK; WiX comes from NuGet on the first build.
param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

dotnet build (Join-Path $root 'installer\BeepTone.wixproj') -c $Configuration -nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$msi = Get-ChildItem (Join-Path $root 'installer\bin') -Recurse -Filter 'BeepTone.msi' |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Path $dist -Force | Out-Null
Copy-Item -LiteralPath $msi.FullName -Destination (Join-Path $dist 'BeepTone.msi') -Force
Write-Host "Built $(Join-Path $dist 'BeepTone.msi')"

& (Join-Path $root "src\BeepToneCtl\bin\$Configuration\BeepToneCtl.exe") selftest
exit $LASTEXITCODE
