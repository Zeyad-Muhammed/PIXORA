<#
.SYNOPSIS
  Builds a PIXORA release: tests, self-contained publish, NSIS installer.
.EXAMPLE
  pwsh packaging\build.ps1                # version from Directory.Build.props
  pwsh packaging\build.ps1 -SkipTests
#>
param(
    [switch]$SkipTests,
    [string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

[xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
$version = $props.Project.PropertyGroup.Version
Write-Host "PIXORA $version" -ForegroundColor Cyan

if (-not $SkipTests) {
    dotnet test tests/PIXORA.Tests -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}

$publish = Join-Path $root 'artifacts/publish/PIXORA'
dotnet publish src/PIXORA.App -c $Configuration -r win-x64 --self-contained true -o $publish
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
if (-not (Test-Path (Join-Path $publish 'PIXORA.exe'))) { throw 'PIXORA.exe missing from publish output.' }

# makensis: PATH, standard install, or the copy Tauri caches (used by NOVU Launcher builds).
$makensis = (Get-Command makensis -ErrorAction SilentlyContinue)?.Source
foreach ($c in @("${env:ProgramFiles(x86)}\NSIS\makensis.exe", "$env:ProgramFiles\NSIS\makensis.exe", "$env:LOCALAPPDATA\tauri\NSIS\makensis.exe")) {
    if (-not $makensis -and (Test-Path $c)) { $makensis = $c }
}
if (-not $makensis) { throw 'makensis.exe not found. Install NSIS 3 (https://nsis.sourceforge.io).' }

New-Item -ItemType Directory -Force (Join-Path $root 'artifacts/installer') | Out-Null
& $makensis /V2 "/DVER=$version" (Join-Path $root 'packaging/pixora.nsi')
if ($LASTEXITCODE -ne 0) { throw 'makensis failed.' }

$setup = Join-Path $root 'artifacts/installer/PIXORA-Setup.exe'
$hash = (Get-FileHash $setup -Algorithm SHA256).Hash
"{0}  {1}" -f $hash, (Split-Path $setup -Leaf) | Set-Content (Join-Path $root 'artifacts/installer/SHA256SUMS.txt')
Write-Host ("Installer: {0} ({1:N1} MB)" -f $setup, ((Get-Item $setup).Length / 1MB)) -ForegroundColor Green
Write-Host "SHA256: $hash"
