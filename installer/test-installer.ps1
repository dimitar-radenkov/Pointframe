<#
.SYNOPSIS
    Builds the SnippingTool installer if needed and runs the installer smoke test.

.DESCRIPTION
    1. Optionally builds the installer with build-installer.ps1.
    2. Exposes the installer path to the automation tests.
    3. Runs only the InstallerSmoke test category.

.PARAMETER InstallerPath
    Path to an existing SnippingTool setup executable. If omitted, the script uses the
    newest installer from installer\output, optionally building first.

.PARAMETER Version
    Optional version passed through to build-installer.ps1 when building.

.PARAMETER SkipBuild
    Skip the installer build step and use an existing installer.

.PARAMETER SkipPublish
    Passed through to build-installer.ps1 when building.

.EXAMPLE
    .\installer\test-installer.ps1

.EXAMPLE
    .\installer\test-installer.ps1 -InstallerPath .\installer\output\SnippingTool-4.2.0-Setup.exe
#>

[CmdletBinding()]
param(
    [string] $InstallerPath,
    [string] $Version,
    [switch] $SkipBuild,
    [switch] $SkipPublish
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$RepoRoot = Split-Path $PSScriptRoot -Parent
$AutomationProject = Join-Path $RepoRoot "SnippingTool.AutomationTests\SnippingTool.AutomationTests.csproj"
$BuildScript = Join-Path $PSScriptRoot "build-installer.ps1"
$InstallerOutputDirectory = Join-Path $PSScriptRoot "output"

function Test-IsAdministrator
{
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-IsAdministrator))
{
    throw "Installer smoke tests must run from an elevated PowerShell session because the Inno Setup installer requires administrator privileges."
}

if ([string]::IsNullOrWhiteSpace($InstallerPath))
{
    if (-not $SkipBuild)
    {
        $buildArguments = @{}
        if ($PSBoundParameters.ContainsKey("Version"))
        {
            $buildArguments.Version = $Version
        }

        if ($SkipPublish)
        {
            $buildArguments.SkipPublish = $true
        }

        & $BuildScript @buildArguments
        if ($LASTEXITCODE -ne 0)
        {
            exit $LASTEXITCODE
        }
    }

    $installerFile = Get-ChildItem $InstallerOutputDirectory -Filter "SnippingTool-*-Setup.exe" |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if ($null -eq $installerFile)
    {
        throw "No installer was found in $InstallerOutputDirectory. Build one first or pass -InstallerPath."
    }

    $InstallerPath = $installerFile.FullName
}
elseif (-not (Test-Path $InstallerPath))
{
    throw "Installer path not found: $InstallerPath"
}

Write-Host "Using installer: $InstallerPath" -ForegroundColor Cyan

$env:SNIPPINGTOOL_AUTOMATION_INSTALLER_PATH = $InstallerPath

try
{
    dotnet test $AutomationProject --filter "Category=InstallerSmoke" --nologo
    if ($LASTEXITCODE -ne 0)
    {
        exit $LASTEXITCODE
    }
}
finally
{
    Remove-Item Env:SNIPPINGTOOL_AUTOMATION_INSTALLER_PATH -ErrorAction SilentlyContinue
}
