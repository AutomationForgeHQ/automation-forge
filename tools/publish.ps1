#Requires -Version 7
<#
.SYNOPSIS
  Build everything a release ships: the hub, the CLI, the installer, the
  portable zips and the checksum list, into dist/.

.DESCRIPTION
  One script, two callers — a developer's machine and the release workflow —
  so what CI publishes is what you can build at your desk.

    tools/publish.ps1                       version from the VERSION file
    tools/publish.ps1 -Version 0.3.0-nightly.20260901
    tools/publish.ps1 -SkipInstaller        without Inno Setup

  Output, all in dist/:
    AutomationForge-Setup.exe               the installer (hub + CLI, per-user)
    AutomationForge-Hub-win-x64.zip         the hub, portable
    forge-win-x64.zip                       the CLI alone
    SHA256SUMS.txt
#>
param(
  [string]$Version,
  [string]$OutDir,
  [switch]$SkipInstaller
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $Version) { $Version = (Get-Content (Join-Path $root 'VERSION') -Raw).Trim() }
if (-not $OutDir) { $OutDir = Join-Path $root 'dist' }

$numeric = ($Version -split '-', 2)[0]
if ($numeric -notmatch '^\d+\.\d+\.\d+$') { throw "Version must be x.y.z or x.y.z-suffix; got '$Version'." }
$fileVersion = "$numeric.0"

if (Test-Path $OutDir) { Remove-Item -Recurse -Force $OutDir }
New-Item -ItemType Directory -Force $OutDir | Out-Null

$publishArgs = @(
  '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
  '-p:PublishSingleFile=true',
  '-p:IncludeNativeLibrariesForSelfExtract=true',
  '-p:EnableCompressionInSingleFile=true',
  '-p:DebugType=none', '-p:DebugSymbols=false',
  "-p:Version=$Version",
  '--nologo', '-v', 'minimal'
)

Write-Host "== hub $Version"
dotnet publish (Join-Path $root 'src/Forge.Hub') @publishArgs -o (Join-Path $OutDir 'hub')
if ($LASTEXITCODE) { throw 'hub publish failed' }

Write-Host "== forge $Version"
dotnet publish (Join-Path $root 'src/forge') @publishArgs -o (Join-Path $OutDir 'cli')
if ($LASTEXITCODE) { throw 'forge publish failed' }

# Native symbol files ride along with the single-file publish; nobody installs those.
Get-ChildItem $OutDir -Recurse -Filter *.pdb | Remove-Item -Force

Write-Host '== portable zips'
Compress-Archive -Path (Join-Path $OutDir 'hub/*') -DestinationPath (Join-Path $OutDir 'AutomationForge-Hub-win-x64.zip') -CompressionLevel Optimal
Compress-Archive -Path (Join-Path $OutDir 'cli/forge.exe') -DestinationPath (Join-Path $OutDir 'forge-win-x64.zip') -CompressionLevel Optimal

if (-not $SkipInstaller) {
  Write-Host '== installer'
  $candidates = @(
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
  )
  $iscc = $candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
  if (-not $iscc) { $iscc = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source }
  if (-not $iscc) { throw 'Inno Setup 6 was not found. Install it (winget install JRSoftware.InnoSetup) or pass -SkipInstaller.' }
  & $iscc /Q "/DAppVersion=$Version" "/DFileVersion=$fileVersion" "/DSourceDir=$OutDir" "/O$OutDir" (Join-Path $root 'installer/AutomationForge.iss')
  if ($LASTEXITCODE) { throw 'installer build failed' }
}

Write-Host '== checksums'
$lines = Get-ChildItem $OutDir -File | Where-Object { $_.Extension -in '.exe', '.zip' } | Sort-Object Name | ForEach-Object {
  '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
}
[IO.File]::WriteAllText((Join-Path $OutDir 'SHA256SUMS.txt'), (($lines -join "`n") + "`n"))

Get-ChildItem $OutDir -File | ForEach-Object { '{0,12:N0}  {1}' -f $_.Length, $_.Name } | Write-Host
