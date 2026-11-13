#Requires -Version 7.0
<#
Runs a named selection of integration tests (tests tagged [Trait("Category","Integration")]) and prints one result line
per test. A package check manifest always appends Category!=Integration to its "tests" filters, so integration tests
run only through an "extra" entry that calls this script. A raw `dotnet test --filter X` that matches no test exits 0,
so a typo passes silently; this wrapper fails on an empty selection.

  pwsh -NoProfile -File scripts/run-integration-tests.ps1 -Project Pointframe.Tests/Pointframe.Tests.csproj -Filter "FullyQualifiedName~Recording" [-NoBuild]
  pwsh -NoProfile -File scripts/run-integration-tests.ps1 -SelfTest    # offline: TRX parsing and exit decision on fixtures

Runs `dotnet test <Project> --filter "(<Filter>)&Category=Integration"` with a TRX logger into a temporary
results directory (--no-build with -NoBuild). It uses the default configuration, the one the package runner's build and
test steps produce, so -NoBuild reuses that fresh build instead of stale Release binaries. Output: one `PASS|FAIL|SKIP <fully qualified name>` line per test, then
`INTEGRATION <passed> passed, <failed> failed, <skipped> skipped`.

Exit 0 only when at least one test passed and none failed. Exit 1 when the selection matched no test (the message names the
filter), when every matched test was skipped (nothing ran), when any test failed, when dotnet test failed without a TRX
(build failure), or when the TRX is unreadable. Exit 2 on bad arguments.
#>
[CmdletBinding()]
param(
    [string]$Project,
    [string]$Filter,
    [switch]$NoBuild,
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest

# Parses a TRX file into { Name, Outcome } records, Outcome being PASS, FAIL or SKIP. Throws when the file is unreadable.
function Read-TrxResults
{
    param([string]$Path)
    try
    {
        [xml]$trx = Get-Content -LiteralPath $Path -Raw
    }
    catch
    {
        throw "the TRX file is unreadable: $Path ($($_.Exception.Message))"
    }

    $names = @{}
    foreach ($unit in $trx.GetElementsByTagName('UnitTest'))
    {
        $method = $unit.GetElementsByTagName('TestMethod') | Select-Object -First 1
        if ($method -and $method.className -and $method.name)
        {
            $names[[string]$unit.id] = ('{0}.{1}' -f ($method.className -replace ',.*$', ''), $method.name)
        }
    }

    $results = [System.Collections.Generic.List[object]]::new()
    foreach ($result in $trx.GetElementsByTagName('UnitTestResult'))
    {
        $name = if ($names.ContainsKey([string]$result.testId)) { $names[[string]$result.testId] } else { [string]$result.testName }
        $outcome = switch ([string]$result.outcome)
        {
            'Passed' { 'PASS' }
            'Failed' { 'FAIL' }
            'Error' { 'FAIL' }
            'Timeout' { 'FAIL' }
            'Aborted' { 'FAIL' }
            default { 'SKIP' }
        }
        $results.Add([pscustomobject]@{ Name = $name; Outcome = $outcome })
    }
    , @($results)
}

# The exit decision for a parsed result list: { ExitCode, Lines, Message }.
function Get-IntegrationDecision
{
    param([object[]]$Results, [string]$Filter)
    $passed = @($Results | Where-Object { $_.Outcome -eq 'PASS' }).Count
    $failed = @($Results | Where-Object { $_.Outcome -eq 'FAIL' }).Count
    $skipped = @($Results | Where-Object { $_.Outcome -eq 'SKIP' }).Count
    $lines = @($Results | ForEach-Object { '{0} {1}' -f $_.Outcome, $_.Name })
    $lines += "INTEGRATION $passed passed, $failed failed, $skipped skipped"
    $exitCode = 0
    $message = ''
    if (@($Results).Count -eq 0)
    {
        $exitCode = 1
        $message = "the filter matched no integration test: ($Filter)&Category=Integration"
    }
    elseif ($failed -gt 0)
    {
        $exitCode = 1
        $message = "$failed integration test(s) failed"
    }
    elseif ($passed -eq 0)
    {
        $exitCode = 1
        $message = "no integration test ran (all $skipped skipped): ($Filter)&Category=Integration"
    }
    [pscustomobject]@{ ExitCode = $exitCode; Lines = $lines; Message = $message }
}

# TRX path -> exit code, printing the result lines. Used by the real run and by the self-test.
function Complete-IntegrationRun
{
    param([string]$TrxPath, [string]$Filter, [int]$DotnetExitCode)
    if (-not $TrxPath -or -not (Test-Path -LiteralPath $TrxPath -PathType Leaf))
    {
        if ($DotnetExitCode -ne 0)
        {
            Write-Host "dotnet test failed (exit $DotnetExitCode) without writing a TRX file (build failure?)"
        }
        else
        {
            Write-Host "dotnet test wrote no TRX file, so the filter matched no integration test: ($Filter)&Category=Integration"
        }
        return 1
    }

    try
    {
        $results = Read-TrxResults $TrxPath
    }
    catch
    {
        Write-Host $_.Exception.Message
        return 1
    }

    $decision = Get-IntegrationDecision $results $Filter
    foreach ($line in $decision.Lines)
    {
        Write-Host $line
    }
    if ($decision.Message)
    {
        Write-Host $decision.Message
    }
    if ($decision.ExitCode -eq 0 -and $DotnetExitCode -ne 0)
    {
        Write-Host "dotnet test exited $DotnetExitCode although no test failed (test host crash or run error)"
        return 1
    }
    return $decision.ExitCode
}

function Invoke-SelfTest
{
    $fixtures = Join-Path $PSScriptRoot 'tests/run-integration-tests'
    $cases = @(
        @{ Name = 'all passed'; Trx = 'all-pass.trx'; Dotnet = 0; Expect = 0; Lines = @('PASS Pointframe.Tests.Services.AlphaTests.Works', 'INTEGRATION 2 passed, 0 failed, 0 skipped') },
        @{ Name = 'one failed'; Trx = 'one-failed.trx'; Dotnet = 1; Expect = 1; Lines = @('FAIL Pointframe.Tests.Services.AlphaTests.Breaks', 'INTEGRATION 1 passed, 1 failed, 0 skipped') },
        @{ Name = 'zero tests'; Trx = 'zero-tests.trx'; Dotnet = 0; Expect = 1; Lines = @('INTEGRATION 0 passed, 0 failed, 0 skipped', 'matched no integration test') },
        @{ Name = 'skipped only'; Trx = 'skipped-only.trx'; Dotnet = 0; Expect = 1; Lines = @('SKIP Pointframe.Tests.Services.AlphaTests.Skipped', 'no integration test ran') },
        @{ Name = 'missing TRX, build failed'; Trx = 'does-not-exist.trx'; Dotnet = 1; Expect = 1; Lines = @('without writing a TRX') },
        @{ Name = 'missing TRX, dotnet ok'; Trx = 'does-not-exist.trx'; Dotnet = 0; Expect = 1; Lines = @('matched no integration test') },
        @{ Name = 'unreadable TRX'; Trx = 'broken.trx'; Dotnet = 1; Expect = 1; Lines = @('unreadable') },
        @{ Name = 'passed but dotnet crashed'; Trx = 'all-pass.trx'; Dotnet = 1; Expect = 1; Lines = @('test host crash') }
    )

    $failures = 0
    foreach ($case in $cases)
    {
        $captured = [System.Collections.Generic.List[string]]::new()
        $exitCode = -1
        $output = & {
            $exitCode = Complete-IntegrationRun (Join-Path $fixtures $case.Trx) 'FullyQualifiedName~Fixture' $case.Dotnet
            $exitCode
        } 6>&1
        $exitCode = [int](@($output | Where-Object { $_ -is [int] }) | Select-Object -Last 1)
        foreach ($item in @($output | Where-Object { $_ -isnot [int] }))
        {
            $captured.Add([string]$item)
        }
        $text = $captured -join "`n"
        $problems = [System.Collections.Generic.List[string]]::new()
        if ($exitCode -ne $case.Expect)
        {
            $problems.Add("exit $exitCode, expected $($case.Expect)")
        }
        foreach ($needle in $case.Lines)
        {
            if (-not $text.Contains($needle))
            {
                $problems.Add("output lacks '$needle'")
            }
        }

        if ($problems.Count -eq 0)
        {
            Write-Host ("PASS  {0} -> exit {1}" -f $case.Name, $exitCode)
        }
        else
        {
            Write-Host ("FAIL  {0}: {1}" -f $case.Name, ($problems -join '; '))
            $failures++
        }
    }

    if ($failures -gt 0)
    {
        Write-Host "run-integration-tests self-test: $failures of $($cases.Count) case(s) failed"
        return 1
    }
    Write-Host "run-integration-tests self-test: all $($cases.Count) cases passed"
    return 0
}

if ($SelfTest)
{
    exit (Invoke-SelfTest)
}

if (-not $Project -or -not $Filter)
{
    Write-Host 'usage: pwsh -NoProfile -File scripts/run-integration-tests.ps1 -Project <csproj> -Filter <expression> [-NoBuild]'
    exit 2
}

$repoRoot = Split-Path $PSScriptRoot -Parent
$projectPath = if ([IO.Path]::IsPathRooted($Project)) { $Project } else { Join-Path $repoRoot $Project }
if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf))
{
    Write-Host "project not found: $Project"
    exit 2
}

$resultsDir = Join-Path ([IO.Path]::GetTempPath()) ('integration-tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $resultsDir -Force | Out-Null
try
{
    $testArgs = @('test', $projectPath, '--filter', "($Filter)&Category=Integration",
        '--logger', 'trx;LogFileName=integration.trx', '--results-directory', $resultsDir)
    if ($NoBuild)
    {
        $testArgs += '--no-build'
    }

    Write-Host ("dotnet test {0} --filter ({1})&Category=Integration" -f $Project, $Filter)
    & dotnet @testArgs 2>&1 | ForEach-Object { Write-Host "$_" }
    $dotnetExit = $LASTEXITCODE

    $trx = Get-ChildItem -LiteralPath $resultsDir -Filter '*.trx' -Recurse -File -ErrorAction SilentlyContinue | Select-Object -First 1
    $exit = Complete-IntegrationRun $(if ($trx) { $trx.FullName } else { '' }) $Filter $dotnetExit
    exit $exit
}
finally
{
    Remove-Item -LiteralPath $resultsDir -Recurse -Force -ErrorAction SilentlyContinue
}
