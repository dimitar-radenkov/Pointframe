<#
.SYNOPSIS
    Publishes SnippingTool and builds the Inno Setup installer.

.DESCRIPTION
    1. Runs `dotnet publish` with the win-x64 publish profile
       (self-contained, single-file, Release).
    2. Calls Inno Setup Compiler (iscc.exe) to produce the setup .exe.

.PARAMETER Version
    Version string embedded in the installer filename, e.g. "1.2.0".
    Defaults to "1.0.0".

.PARAMETER SkipPublish
    Skip the dotnet publish step (useful when the publish output is already up-to-date).

.EXAMPLE
    .\installer\build-installer.ps1
    .\installer\build-installer.ps1 -Version "1.2.0"
    .\installer\build-installer.ps1 -SkipPublish
#>

[CmdletBinding()]
param(
    [string] $Version    = "1.0.0",
    [switch] $SkipPublish
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$RepoRoot    = Split-Path $PSScriptRoot -Parent
$ProjectFile = Join-Path $RepoRoot "SnippingTool\SnippingTool.csproj"
$IssScript   = Join-Path $PSScriptRoot "SnippingTool.iss"
$OutputDir   = Join-Path $PSScriptRoot "output"

# ── 1. Locate Inno Setup ────────────────────────────────────────────────────
$IsccPaths = @(
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe"
)
$Iscc = $IsccPaths | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $Iscc)
{
    Write-Error @"
Inno Setup 6 not found. Install it from:
  https://jrsoftware.org/isdl.php
Then re-run this script.
"@
}

Write-Host "Using Inno Setup: $Iscc" -ForegroundColor Cyan

# ── 2. dotnet publish ────────────────────────────────────────────────────────
if (-not $SkipPublish)
{
    Write-Host "`n==> Publishing SnippingTool (win-x64, self-contained, single-file)..." -ForegroundColor Cyan
    dotnet publish $ProjectFile `
        /p:PublishProfile=win-x64 `
        /p:Version=$Version `
        --nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
else
{
    Write-Host "Skipping publish (--SkipPublish specified)." -ForegroundColor Yellow
}

# ── 3. Build installer ───────────────────────────────────────────────────────
Write-Host "`n==> Building installer (version $Version)..." -ForegroundColor Cyan

New-Item -ItemType Directory -Force $OutputDir | Out-Null

& $Iscc `
    /DAppVersion=$Version `
    /O"$OutputDir" `
    $IssScript

if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$InstallerFile = Get-ChildItem $OutputDir -Filter "*.exe" |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if ($InstallerFile)
{
    Write-Host "`n✓ Installer ready: $($InstallerFile.FullName)" -ForegroundColor Green
}
else
{
    Write-Warning "Build succeeded but no .exe found in $OutputDir"
}
