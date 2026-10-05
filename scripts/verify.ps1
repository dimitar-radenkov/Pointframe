#Requires -Version 7.0
<#
One command that decides whether a change is ready: the same gates CI runs, with failures reported
so an agent can act on them. Run it before saying a task is done; a task is done when it passes.

  pwsh scripts/verify.ps1                                    # every gate; exit 0 only when all pass
  pwsh scripts/verify.ps1 -Filter "FullyQualifiedName~Foo"   # narrow the unit tests while iterating
  pwsh scripts/verify.ps1 -Skip kb                           # leave gates out while iterating (never for the final run)

Gates, in order: preflight (no running process locks the Release output), build (Release, like CI),
format (dotnet format --verify-no-changes on the main project), tests (unit lane, Category!=Integration),
kb (scripts/kb.ps1 check -NoFix), workflows (scripts/check-workflow-scripts.ps1: its self-test, then every pwsh
run: block in .github/workflows must parse), discovery (scripts/check-agent-discovery.ps1: its self-test, then the offline
checks of the agent page, llms.txt, directory drafts, install commands, and release asset names). Tests are skipped when the build fails; every other gate always
runs, so one pass reports every problem.

Writes artifacts/verify/verdict.json (status, gates, failure details, and the working-tree hash it verified)
and a log per gate next to it. Exit 0 when every gate passed, 1 when any failed, 2 on bad arguments.
#>
[CmdletBinding()]
param(
    [string]$Filter,
    [string[]]$Skip = @()
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest

$GateNames = @('preflight', 'build', 'format', 'tests', 'kb', 'workflows', 'discovery')
$Skip = @($Skip | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$unknown = @($Skip | Where-Object { $GateNames -notcontains $_ })
if ($unknown.Count -gt 0)
{
    Write-Host "ERROR unknown gate(s): $($unknown -join ', '). Gates: $($GateNames -join ', ')."
    exit 2
}

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$OutDir = Join-Path $RepoRoot 'artifacts' 'verify'
$VerdictPath = Join-Path $OutDir 'verdict.json'
$TestProject = 'Pointframe.Tests/Pointframe.Tests.csproj'
$MainProject = 'Pointframe/Pointframe.csproj'
$MaxDetails = 20

New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
Get-ChildItem -Path $OutDir -File | Remove-Item -Force
Push-Location $RepoRoot

function ConvertTo-RepoRelative([string]$Text)
{
    $Text.Replace("$RepoRoot\", '').Replace("$RepoRoot/", '')
}

function Invoke-Logged([string]$Gate, [string]$Exe, [string[]]$Arguments)
{
    $log = Join-Path $OutDir "$Gate.log"
    $lines = @(& $Exe @Arguments 2>&1 | ForEach-Object { "$_" })
    $exitCode = $LASTEXITCODE
    $lines | Set-Content -Path $log -Encoding utf8
    [pscustomobject]@{ ExitCode = $exitCode; Lines = $lines; Log = ConvertTo-RepoRelative $log }
}

function Get-ErrorLines([string[]]$Lines)
{
    @($Lines |
        Where-Object { $_ -match ':\s+error\s' } |
        ForEach-Object { ConvertTo-RepoRelative ($_ -replace '\s+\[[^\]]+\.csproj[^\]]*\]\s*$', '').Trim() } |
        Select-Object -Unique)
}

function New-Gate([string]$Name, [string]$Status, [string]$Summary, [string[]]$Details = @(), [string]$Log = $null)
{
    $all = @($Details)
    $shown = @($all | Select-Object -First $MaxDetails)
    if ($all.Count -gt $MaxDetails)
    {
        $shown += "... $($all.Count - $MaxDetails) more, see $Log"
    }
    [ordered]@{ name = $Name; status = $Status; summary = $Summary; details = $shown; log = $Log; seconds = 0 }
}

function Get-WorkingTreeHash
{
    $index = git rev-parse --git-path index
    $tempIndex = Join-Path $OutDir 'tree.index'
    if (Test-Path $index)
    {
        Copy-Item $index $tempIndex -Force
    }
    $env:GIT_INDEX_FILE = $tempIndex
    try
    {
        git add -A 2>$null | Out-Null
        (git write-tree).Trim()
    }
    finally
    {
        Remove-Item Env:GIT_INDEX_FILE
        Remove-Item $tempIndex -Force -ErrorAction SilentlyContinue
    }
}

function Test-Preflight
{
    $releaseDirs = @('Pointframe', 'Pointframe.Engine', 'Pointframe.Mcp', 'Pointframe.Cli', 'Pointframe.Tests') |
        ForEach-Object { Join-Path $RepoRoot $_ 'bin' 'Release' }
    $lockers = @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
            $path = $null
            try { $path = $_.Path } catch { }
            $path -and @($releaseDirs | Where-Object { $path.StartsWith($_, [StringComparison]::OrdinalIgnoreCase) }).Count -gt 0
        })
    if ($lockers.Count -eq 0)
    {
        return New-Gate 'preflight' 'pass' 'No running process holds the Release build output.'
    }
    $details = @($lockers | ForEach-Object { "$($_.ProcessName) (PID $($_.Id)): $(ConvertTo-RepoRelative $_.Path)" })
    $details += 'Stop these processes (Stop-Process -Id <PID>) or the build fails with MSB3027 (file locked).'
    New-Gate 'preflight' 'fail' "$($lockers.Count) running process(es) lock the Release build output." $details
}

function Test-Build
{
    $run = Invoke-Logged 'build' 'dotnet' @('build', $TestProject, '--configuration', 'Release', '--nologo', '-v', 'q')
    if ($run.ExitCode -eq 0)
    {
        return New-Gate 'build' 'pass' 'Release build of the test project and everything it references succeeded.' -Log $run.Log
    }
    $errors = @(Get-ErrorLines $run.Lines)
    if ($errors.Count -eq 0)
    {
        # No compiler-style error line (a locked file, a restore failure): show the tail of the log.
        return New-Gate 'build' 'fail' "Build failed (exit $($run.ExitCode))." @($run.Lines | Select-Object -Last 15) $run.Log
    }
    New-Gate 'build' 'fail' "Build failed with $($errors.Count) error(s)." $errors $run.Log
}

function Test-Format
{
    $run = Invoke-Logged 'format' 'dotnet' @('format', $MainProject, '--verify-no-changes', '--no-restore')
    if ($run.ExitCode -eq 0)
    {
        return New-Gate 'format' 'pass' 'dotnet format reports no changes.' -Log $run.Log
    }
    $errors = @(Get-ErrorLines $run.Lines)
    $details = @("Fix: dotnet format $MainProject (main project only; do not format Pointframe.Tests).") + $errors
    New-Gate 'format' 'fail' "Formatting differs in $($errors.Count) place(s)." $details $run.Log
}

function Test-Tests
{
    $resultsDir = Join-Path $OutDir 'test-results'
    $filterExpression = if ($Filter) { "(Category!=Integration)&($Filter)" } else { 'Category!=Integration' }
    $run = Invoke-Logged 'tests' 'dotnet' @(
        'test', $TestProject, '--configuration', 'Release', '--no-build', '--nologo',
        '--filter', $filterExpression,
        '--logger', 'trx;LogFileName=tests.trx', '--results-directory', $resultsDir)
    $trx = Join-Path $resultsDir 'tests.trx'
    if (-not (Test-Path $trx))
    {
        return New-Gate 'tests' 'fail' "Test run produced no results (exit $($run.ExitCode))." @($run.Lines | Select-Object -Last 10) $run.Log
    }

    [xml]$doc = Get-Content -Path $trx -Raw
    $ns = @{ t = 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010' }
    $counters = (Select-Xml -Xml $doc -XPath '//t:ResultSummary/t:Counters' -Namespace $ns).Node
    $total = [int]$counters.total
    $failedNodes = @(Select-Xml -Xml $doc -XPath "//t:UnitTestResult[@outcome='Failed']" -Namespace $ns | ForEach-Object { $_.Node })
    $scope = if ($Filter) { " (filter: $Filter)" } else { '' }

    if ($total -eq 0)
    {
        return New-Gate 'tests' 'fail' "No tests matched$scope." -Log $run.Log
    }
    if ($failedNodes.Count -eq 0 -and $run.ExitCode -eq 0)
    {
        return New-Gate 'tests' 'pass' "$($counters.passed) of $total test(s) passed$scope." -Log $run.Log
    }

    $details = @($failedNodes | ForEach-Object {
            $message = ''
            $messageNode = $_.SelectSingleNode('.//*[local-name()="ErrorInfo"]/*[local-name()="Message"]')
            if ($messageNode)
            {
                $message = (($messageNode.InnerText -split "`r?`n") | Where-Object { $_.Trim() } | Select-Object -First 3) -join ' | '
            }
            "$($_.testName): $message"
        })
    New-Gate 'tests' 'fail' "$($failedNodes.Count) of $total test(s) failed$scope." $details $run.Log
}

function Test-Kb
{
    $run = Invoke-Logged 'kb' 'pwsh' @('-NoProfile', '-NonInteractive', '-File', (Join-Path $RepoRoot 'scripts' 'kb.ps1'), 'check', '-NoFix')
    if ($run.ExitCode -eq 0)
    {
        return New-Gate 'kb' 'pass' 'Knowledge base check passed.' -Log $run.Log
    }
    $errors = @($run.Lines | Where-Object { $_ -match '^\s*ERROR' } | ForEach-Object { $_.Trim() })
    $details = @('Fix: run /kb-write or /kb-check (pwsh scripts/kb.ps1 check refreshes generated blocks).') + $errors
    New-Gate 'kb' 'fail' "Knowledge base check reported $($errors.Count) error(s)." $details $run.Log
}

function Test-Workflows
{
    $script = Join-Path $RepoRoot 'scripts' 'check-workflow-scripts.ps1'
    $selfTest = Invoke-Logged 'workflows' 'pwsh' @('-NoProfile', '-NonInteractive', '-File', $script, '-SelfTest')
    $selfTestLog = $selfTest.Lines
    $run = Invoke-Logged 'workflows' 'pwsh' @('-NoProfile', '-NonInteractive', '-File', $script)
    $run.Lines = @($selfTestLog) + @($run.Lines)
    $run.Lines | Set-Content -Path (Join-Path $OutDir 'workflows.log') -Encoding utf8
    if ($selfTest.ExitCode -ne 0)
    {
        $details = @($selfTestLog | ForEach-Object { $_.Trim() } | Where-Object { $_ })
        return New-Gate 'workflows' 'fail' 'The workflow script checker failed its self-test.' $details $run.Log
    }
    if ($run.ExitCode -eq 0)
    {
        return New-Gate 'workflows' 'pass' ($run.Lines | Select-Object -Last 1) -Log $run.Log
    }
    $problems = @($run.Lines | Select-Object -Skip $selfTestLog.Count | Where-Object { $_ -match '\.yml:\d+ step ' } | ForEach-Object { $_.Trim() })
    $details = @('Fix: delimit "${name}:" in strings and re-run pwsh scripts/check-workflow-scripts.ps1.') + $problems
    New-Gate 'workflows' 'fail' "$($problems.Count) workflow run: block(s) do not parse as PowerShell." $details $run.Log
}

function Test-Discovery
{
    $script = Join-Path $RepoRoot 'scripts' 'check-agent-discovery.ps1'
    $selfTest = Invoke-Logged 'discovery' 'pwsh' @('-NoProfile', '-NonInteractive', '-File', $script, '-SelfTest')
    $selfTestLog = $selfTest.Lines
    $run = Invoke-Logged 'discovery' 'pwsh' @('-NoProfile', '-NonInteractive', '-File', $script)
    $run.Lines = @($selfTestLog) + @($run.Lines)
    $run.Lines | Set-Content -Path (Join-Path $OutDir 'discovery.log') -Encoding utf8
    if ($selfTest.ExitCode -ne 0)
    {
        $details = @($selfTestLog | ForEach-Object { $_.Trim() } | Where-Object { $_ })
        return New-Gate 'discovery' 'fail' 'The agent discovery checker failed its self-test.' $details $run.Log
    }
    if ($run.ExitCode -eq 0)
    {
        return New-Gate 'discovery' 'pass' ($run.Lines | Select-Object -Last 1) -Log $run.Log
    }
    $problems = @($run.Lines | Select-Object -Skip $selfTestLog.Count | Where-Object { $_ -match '^ERROR ' } | ForEach-Object { $_.Trim() })
    $details = @('Fix the page, llms.txt, drafts, or packaging named below, then run pwsh scripts/check-agent-discovery.ps1.') + $problems
    New-Gate 'discovery' 'fail' "$($problems.Count) agent discovery problem(s)." $details $run.Log
}

$started = Get-Date
$treeHash = Get-WorkingTreeHash
$gates = [System.Collections.Generic.List[object]]::new()
$checks = [ordered]@{
    preflight = { Test-Preflight }
    build = { Test-Build }
    format = { Test-Format }
    tests = { Test-Tests }
    kb = { Test-Kb }
    workflows = { Test-Workflows }
    discovery = { Test-Discovery }
}

try
{
    $buildFailed = $false
    foreach ($name in $checks.Keys)
    {
        if ($Skip -contains $name)
        {
            $gates.Add((New-Gate $name 'skipped' 'Skipped with -Skip.'))
            continue
        }
        if ($name -eq 'tests' -and $buildFailed)
        {
            $gates.Add((New-Gate $name 'skipped' 'Skipped because the build failed.'))
            continue
        }
        Write-Host "verify: $name ..."
        $timer = [Diagnostics.Stopwatch]::StartNew()
        $gate = & $checks[$name]
        $gate.seconds = [math]::Round($timer.Elapsed.TotalSeconds, 1)
        $gates.Add($gate)
        if ($name -eq 'build' -and $gate.status -ne 'pass')
        {
            $buildFailed = $true
        }
    }
}
finally
{
    Pop-Location
}

$failed = @($gates | Where-Object { $_.status -eq 'fail' })
$skipped = @($gates | Where-Object { $_.status -eq 'skipped' })
$status = if ($failed.Count -gt 0) { 'fail' } elseif ($skipped.Count -gt 0 -or $Filter) { 'partial' } else { 'pass' }

$verdict = [ordered]@{
    status = $status
    complete = $status -eq 'pass'
    startedAt = $started.ToString('o')
    seconds = [math]::Round(((Get-Date) - $started).TotalSeconds, 1)
    head = (git -C $RepoRoot rev-parse HEAD).Trim()
    treeHash = $treeHash
    filter = $Filter
    gates = $gates
}
$verdict | ConvertTo-Json -Depth 6 | Set-Content -Path $VerdictPath -Encoding utf8

Write-Host ''
foreach ($gate in $gates)
{
    $mark = switch ($gate.status) { 'pass' { 'PASS' } 'fail' { 'FAIL' } default { 'SKIP' } }
    Write-Host ("{0}  {1,-9} {2}" -f $mark, $gate.name, $gate.summary)
    foreach ($line in $gate.details)
    {
        Write-Host "        $line"
    }
}
Write-Host ''
$resultLine = switch ($status)
{
    'pass' { 'VERIFY PASSED: every gate passed.' }
    'partial' { 'VERIFY PARTIAL: no failures, but gates were skipped or tests filtered; run without -Skip and -Filter for a final verdict.' }
    default { "VERIFY FAILED: $($failed.Count) gate(s) failed. Fix the details above and run again." }
}
Write-Host $resultLine
Write-Host "Verdict: $(ConvertTo-RepoRelative $VerdictPath)"

exit ([int]($status -eq 'fail'))
