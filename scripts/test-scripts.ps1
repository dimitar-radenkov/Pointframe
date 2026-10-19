#Requires -Version 7.0
<#
Runs the offline -SelfTest of every coordinator and release script, and fails when a file under scripts/tests is
ignored by git (the *.md ignore rule once dropped the fixtures of status.ps1 and agent-run.ps1, so their self-tests
passed only in the worktree that created them).

  pwsh scripts/test-scripts.ps1                      # exit 0 when every self-test passes and every fixture is tracked
  pwsh scripts/test-scripts.ps1 -All                 # also run the heavy self-tests
  pwsh scripts/test-scripts.ps1 -BaseRef origin/main # compare against another base (default origin/master)

A heavy script ($HeavyScripts, package.ps1: ~16 minutes) runs only with -All, or when any path under scripts/ differs
between the merge base of HEAD and -BaseRef and the working tree (committed, staged, unstaged, and untracked files that
git does not ignore all count). Otherwise it is skipped and printed as SKIP; a skip never fails the run. When the merge
base cannot be computed (unknown ref, shallow clone, git error) everything runs. Every other self-test and the
ignored-fixture check always run.

Used by scripts/verify.ps1 (gate "scripts") and by CI (package-checks), which runs it on a clean checkout; the daily
workflow script-selftests-daily.yml runs it with -All.
A new script with an offline -SelfTest belongs in $SelfTestScripts.
#>
[CmdletBinding()]
param(
    [switch]$All,
    [string]$BaseRef = 'origin/master'
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest

$SelfTestScripts = @(
    'merge-pr.ps1',
    'record-review.ps1',
    'codex-ask.ps1',
    'status.ps1',
    'agent-run.ps1',
    'package.ps1',
    'release-impact.ps1',
    'check-release-channels.ps1',
    'check-released-desktop.ps1',
    'usage-report.ps1',
    'update-download-badges.ps1',
    'measure-mcp-payloads.ps1',
    'test-agent-onboarding.ps1',
    'trial.ps1',
    'weekly-trial.ps1'
)

$HeavyScripts = @('package.ps1')

$repoRoot = Split-Path $PSScriptRoot -Parent
$failures = [System.Collections.Generic.List[string]]::new()

# Returns $null when the answer is unknown (the caller then runs everything), else @{ Changed = <bool>; Base = <short sha> }.
function Get-ScriptsChange
{
    $mergeBaseOut = @(& git -C $repoRoot merge-base HEAD $BaseRef 2>$null)
    if ($LASTEXITCODE -ne 0) { return $null }
    $mergeBase = "$(@($mergeBaseOut | Where-Object { "$_".Trim() })[0])".Trim()
    if (-not $mergeBase) { return $null }

    $diff = @(& git -C $repoRoot diff --name-only $mergeBase -- scripts 2>$null | Where-Object { $_ })
    if ($LASTEXITCODE -ne 0) { return $null }
    $untracked = @(& git -C $repoRoot ls-files --others --exclude-standard -- scripts 2>$null | Where-Object { $_ })
    if ($LASTEXITCODE -ne 0) { return $null }

    $shortOut = @(& git -C $repoRoot rev-parse --short $mergeBase 2>$null)
    $short = if ($LASTEXITCODE -eq 0) { "$(@($shortOut | Where-Object { "$_".Trim() })[0])".Trim() } else { '' }
    if (-not $short) { $short = $mergeBase }
    return @{ Changed = (($diff.Count + $untracked.Count) -gt 0); Base = $short }
}

$scriptsChange = $null
if (-not $All -and @($HeavyScripts).Count -gt 0)
{
    try { $scriptsChange = Get-ScriptsChange } catch { $scriptsChange = $null }
}
$skipHeavy = (-not $All) -and $null -ne $scriptsChange -and -not $scriptsChange.Changed
$ran = 0
$skipped = 0

# Fixtures that git ignores exist only in the worktree that created them; a clean checkout would not have them.
$ignored = @(& git -C $repoRoot ls-files --others --ignored --exclude-standard -- 'scripts/tests' 2>$null | Where-Object { $_ })
foreach ($path in $ignored)
{
    $failures.Add("ignored fixture (add a .gitignore negation): $path")
}

foreach ($name in $SelfTestScripts)
{
    $path = Join-Path $PSScriptRoot $name
    if (-not (Test-Path -LiteralPath $path))
    {
        $failures.Add("$name is listed but does not exist")
        continue
    }

    if ($skipHeavy -and $HeavyScripts -contains $name)
    {
        Write-Host "SKIP  $name  no change under scripts/ since $($scriptsChange.Base) (run with -All to force)"
        $skipped++
        continue
    }

    $output = @(& pwsh -NoProfile -NonInteractive -File $path -SelfTest 2>&1 | ForEach-Object { "$_" })
    $last = $output | Where-Object { $_.Trim() } | Select-Object -Last 1
    if ($LASTEXITCODE -ne 0)
    {
        $failures.Add("$name -SelfTest failed (exit $LASTEXITCODE): $last")
        continue
    }

    Write-Host "PASS  $name  $last"
    $ran++
}

if ($failures.Count -gt 0)
{
    foreach ($failure in $failures)
    {
        Write-Host "FAIL  $failure"
    }
    Write-Host "Script self-tests: $($failures.Count) problem(s)."
    exit 1
}

$skippedText = if ($skipped -gt 0) { ", $skipped skipped (no scripts/ change)" } else { '' }
Write-Host "Script self-tests: $ran scripts passed$skippedText, every fixture is tracked."
exit 0
