#Requires -Version 7.0
<#
Runs the offline -SelfTest of every coordinator and release script, and fails when a file under scripts/tests is
ignored by git (the *.md ignore rule once dropped the fixtures of status.ps1 and agent-run.ps1, so their self-tests
passed only in the worktree that created them).

  pwsh scripts/test-scripts.ps1        # exit 0 when every self-test passes and every fixture is tracked

Used by scripts/verify.ps1 (gate "scripts") and by CI (package-checks), which runs it on a clean checkout.
A new script with an offline -SelfTest belongs in $SelfTestScripts.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest

$SelfTestScripts = @(
    'merge-pr.ps1',
    'status.ps1',
    'agent-run.ps1',
    'release-impact.ps1',
    'check-release-channels.ps1',
    'check-released-desktop.ps1',
    'usage-report.ps1',
    'update-download-badges.ps1',
    'measure-mcp-payloads.ps1',
    'test-agent-onboarding.ps1'
)

$repoRoot = Split-Path $PSScriptRoot -Parent
$failures = [System.Collections.Generic.List[string]]::new()

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

    $output = @(& pwsh -NoProfile -NonInteractive -File $path -SelfTest 2>&1 | ForEach-Object { "$_" })
    $last = $output | Where-Object { $_.Trim() } | Select-Object -Last 1
    if ($LASTEXITCODE -ne 0)
    {
        $failures.Add("$name -SelfTest failed (exit $LASTEXITCODE): $last")
        continue
    }

    Write-Host "PASS  $name  $last"
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

Write-Host "Script self-tests: $($SelfTestScripts.Count) scripts passed, every fixture is tracked."
exit 0
