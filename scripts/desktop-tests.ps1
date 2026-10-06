#Requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateRange(1, 100)]
    [int]$Repeat = 1,
    [switch]$IncludeVerifyApp
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$framework = 'net10.0-windows10.0.18362.0'
$testProject = Join-Path $repoRoot 'Pointframe.AutomationTests/Pointframe.AutomationTests.csproj'
$fixtureProject = Join-Path $repoRoot 'Pointframe.DesktopTestFixture/Pointframe.DesktopTestFixture.csproj'
$releaseRoot = Join-Path $repoRoot 'artifacts/desktop-tests'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
$runRoot = Join-Path $releaseRoot $stamp

# This required list is intentionally explicit. Expected counts make discovery drift fail closed.
$selection = @(
    [pscustomobject]@{ Class = 'SettingsWindowSmokeTests'; Expected = 1 }
    [pscustomobject]@{ Class = 'SettingsSectionNavigationTests'; Expected = 6 }
    [pscustomobject]@{ Class = 'ScrollingCaptureDesktopTests'; Expected = 2 }
    [pscustomobject]@{ Class = 'WelcomeWindowSmokeTests'; Expected = 1 }
    [pscustomobject]@{ Class = 'McpOrdinaryStartupObservationTests'; Expected = 2 }
    [pscustomobject]@{ Class = 'RecordingOverlaySmokeTests'; Expected = 2 }
    [pscustomobject]@{ Class = 'RecordingHudInteractionTests'; Expected = 4 }
    [pscustomobject]@{ Class = 'McpDesktopModalInvokeTests'; Expected = 1 }
)
$expectedPerRun = ($selection | Measure-Object -Property Expected -Sum).Sum
$classFilters = @($selection | ForEach-Object { "FullyQualifiedName~Pointframe.AutomationTests.Smoke.$($_.Class)" })
$filter = $classFilters -join '|'

function Find-ReleaseExecutable([string]$Project, [string]$Name)
{
    $directory = Join-Path $repoRoot "$Project/bin/Release/$framework"
    $candidate = Join-Path $directory $Name
    if (-not (Test-Path $candidate -PathType Leaf))
    {
        return $null
    }
    $candidate
}

function Invoke-Native([string]$Exe, [string[]]$Arguments)
{
    & $Exe @Arguments
    if ($LASTEXITCODE -ne 0)
    {
        throw "$Exe failed with exit code $LASTEXITCODE."
    }
}

function Read-Trx([string]$Path, [int]$RunNumber)
{
    if (-not (Test-Path $Path -PathType Leaf))
    {
        throw "Run $RunNumber produced no TRX result file: $Path"
    }

    [xml]$trx = Get-Content -Path $Path -Raw
    $ns = @{ t = 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010' }
    $counters = (Select-Xml -Xml $trx -XPath '//t:ResultSummary/t:Counters' -Namespace $ns).Node
    if ($null -eq $counters)
    {
        throw "Run $RunNumber TRX has no result counters: $Path"
    }
    $results = @(Select-Xml -Xml $trx -XPath '//t:UnitTestResult' -Namespace $ns | ForEach-Object { $_.Node })
    $skipped = @($results | Where-Object { $_.outcome -in @('NotExecuted', 'Skipped') })
    $failed = @($results | Where-Object { $_.outcome -eq 'Failed' })
    $passed = @($results | Where-Object { $_.outcome -eq 'Passed' })
    $total = [int]$counters.total
    if ($total -ne $expectedPerRun -or $results.Count -ne $expectedPerRun)
    {
        throw "Run $RunNumber discovered $total test(s) ($($results.Count) TRX results); expected exactly $expectedPerRun. Required classes: $($selection.Class -join ', ')."
    }
    if ($skipped.Count -gt 0)
    {
        $names = @($skipped | ForEach-Object { "$($_.testName) [$($_.outcome)]" })
        throw "Run $RunNumber skipped $($skipped.Count) required test(s): $($names -join '; ')"
    }
    if ($failed.Count -gt 0 -or $passed.Count -ne $expectedPerRun)
    {
        $names = @($failed | ForEach-Object { $_.testName })
        throw "Run $RunNumber passed $($passed.Count)/$expectedPerRun and failed $($failed.Count): $($names -join ', ')"
    }

    [pscustomobject]@{ Run = $RunNumber; Passed = $passed.Count; Skipped = $skipped.Count; Failed = $failed.Count; Trx = $Path }
}

if (-not $IsWindows -or -not [Environment]::UserInteractive -or [Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT)
{
    throw 'Desktop automation requires an interactive Windows session.'
}
if ((Get-Process -Id $PID).SessionId -eq 0)
{
    throw 'Desktop automation cannot run from session 0. Start PowerShell in the signed-in desktop session.'
}

$mcpProcesses = @(Get-CimInstance Win32_Process -Filter "Name = 'Pointframe.Mcp.exe'" -ErrorAction SilentlyContinue | Where-Object {
        $_.ExecutablePath -and $_.ExecutablePath.StartsWith($repoRoot, [StringComparison]::OrdinalIgnoreCase)
    })
if ($mcpProcesses.Count -gt 0)
{
    $details = @($mcpProcesses | ForEach-Object { "PID $($_.ProcessId): $($_.ExecutablePath)" }) -join [Environment]::NewLine
    throw "A repository Pointframe.Mcp.exe is running and may hold Release files. Stop the editor-started MCP process before desktop automation:`n$details"
}

New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
$appExe = Find-ReleaseExecutable 'Pointframe' 'Pointframe.exe'
$mcpExe = Find-ReleaseExecutable 'Pointframe.Mcp' 'Pointframe.Mcp.exe'
$cliExe = Find-ReleaseExecutable 'Pointframe.Cli' 'Pointframe.Cli.exe'
$testExe = Find-ReleaseExecutable 'Pointframe.AutomationTests' 'Pointframe.AutomationTests.dll'
$fixtureExe = Find-ReleaseExecutable 'Pointframe.DesktopTestFixture' 'Pointframe.DesktopTestFixture.exe'
Write-Host 'Building the current Release automation tests (including app/MCP/CLI) and desktop fixture.'
Invoke-Native 'dotnet' @('build', $testProject, '--configuration', 'Release', '--nologo')
Invoke-Native 'dotnet' @('build', $fixtureProject, '--configuration', 'Release', '--nologo')
$appExe = Find-ReleaseExecutable 'Pointframe' 'Pointframe.exe'
$mcpExe = Find-ReleaseExecutable 'Pointframe.Mcp' 'Pointframe.Mcp.exe'
$cliExe = Find-ReleaseExecutable 'Pointframe.Cli' 'Pointframe.Cli.exe'
$testExe = Find-ReleaseExecutable 'Pointframe.AutomationTests' 'Pointframe.AutomationTests.dll'
$fixtureExe = Find-ReleaseExecutable 'Pointframe.DesktopTestFixture' 'Pointframe.DesktopTestFixture.exe'
if (-not $appExe -or -not $mcpExe -or -not $cliExe -or -not $testExe -or -not $fixtureExe)
{
    throw "Release build outputs are incomplete under $repoRoot."
}

$settingsPath = Join-Path $runRoot 'automation-settings.json'
$automationOutput = Join-Path $runRoot 'automation-output'
$mcpOutput = Join-Path $runRoot 'mcp-output'
New-Item -ItemType Directory -Path $automationOutput, $mcpOutput -Force | Out-Null
$env:POINTFRAME_EXECUTABLE = $appExe
$env:POINTFRAME_MCP_EXECUTABLE = $mcpExe
$env:POINTFRAME_DESKTOP_TEST_ACK = 'true'
$env:POINTFRAME_DESKTOP_TEST_OUTPUT = $runRoot
$env:POINTFRAME_MCP_TEST_APP_PATH = $appExe
$env:POINTFRAME_MCP_TEST_SERVER_PATH = $mcpExe
$env:POINTFRAME_MCP_TEST_OUTPUT_DIRECTORY = $mcpOutput
$env:POINTFRAME_MCP_TEST_DEDICATED_ENVIRONMENT = '1'
$env:SNIPPINGTOOL_AUTOMATION_SETTINGS_PATH = $settingsPath
$env:SNIPPINGTOOL_AUTOMATION_OUTPUT_DIRECTORY = $automationOutput
$env:POINTFRAME_FIXTURE_EXECUTABLE = $fixtureExe
$appDataDirectory = Join-Path $runRoot 'app-data'
New-Item -ItemType Directory -Path $appDataDirectory -Force | Out-Null
$env:SNIPPINGTOOL_AUTOMATION_DATA_DIRECTORY = $appDataDirectory

Write-Host "Required desktop selection ($expectedPerRun tests per run):"
$selection | ForEach-Object { Write-Host "  $($_.Class): $($_.Expected)" }
Write-Host "Evidence: $runRoot"

$summary = [System.Collections.Generic.List[object]]::new()
$totalTimer = [Diagnostics.Stopwatch]::StartNew()
for ($runNumber = 1; $runNumber -le $Repeat; $runNumber++)
{
    $resultDirectory = Join-Path $runRoot ("run-{0:D2}" -f $runNumber)
    New-Item -ItemType Directory -Path $resultDirectory -Force | Out-Null
    Write-Host "Desktop run $runNumber/$Repeat ..."
    & dotnet test $testProject --configuration Release --no-build --no-restore --nologo `
        --filter $filter --logger 'trx;LogFileName=desktop-tests.trx' --results-directory $resultDirectory
    $testExitCode = $LASTEXITCODE
    $trxPath = Join-Path $resultDirectory 'desktop-tests.trx'
    $runResult = Read-Trx $trxPath $runNumber
    if ($testExitCode -ne 0)
    {
        throw "Run $runNumber dotnet test returned exit code $testExitCode."
    }
    $summary.Add($runResult)
    Write-Host "Run $runNumber PASS: $($runResult.Passed)/$expectedPerRun passed, skipped 0, failed 0."
}

if ($IncludeVerifyApp)
{
    $copiedCli = Join-Path $runRoot 'verify-cli'
    New-Item -ItemType Directory -Path $copiedCli -Force | Out-Null
    Copy-Item -Path (Join-Path (Split-Path $cliExe) '*') -Destination $copiedCli -Recurse -Force
    $copiedCliExe = Join-Path $copiedCli 'Pointframe.Cli.exe'
    if (-not (Test-Path $copiedCliExe -PathType Leaf))
    {
        throw "Could not copy the Release CLI output to $copiedCli."
    }
    Write-Host 'Running pointframe verify run against the real app with copied Release CLI and Release MCP ...'
    Invoke-Native $copiedCliExe @('verify', 'run', '--spec', '.pointframe/verify-app.json', '--mcp', $mcpExe)
}

$totalTimer.Stop()
Write-Host ''
Write-Host 'Desktop regression summary:'
$summary | ForEach-Object { Write-Host ("Run {0}: {1}/{1} passed, skipped {2}, failed {3}" -f $_.Run, $_.Passed, $_.Skipped, $_.Failed) }
Write-Host ("Total desktop test time: {0:N1}s" -f $totalTimer.Elapsed.TotalSeconds)
if ($IncludeVerifyApp)
{
    Write-Host 'pointframe verify app: PASS'
}
Write-Host "TRX evidence: $runRoot"
