#Requires -Version 7.0
<#
Waits for a pull request's checks, squash-merges the exact head that was checked, then cleans up. For agents.

  pwsh scripts/merge-pr.ps1 -Pr 190                          # PR number or branch name
  pwsh scripts/merge-pr.ps1 -Pr 190 -Worktree ..\Pointframe-wt-x   # also remove that worktree after the merge
  pwsh scripts/merge-pr.ps1 -Pr 190 -DryRun                  # evaluate once and report; never merges or deletes
  pwsh scripts/merge-pr.ps1 -Pr 190 -Auto                    # enable GitHub auto-merge (squash, delete branch) and return at once
  pwsh scripts/merge-pr.ps1 -Pr 190 -Required unit-tests,CodeQL -TimeoutMinutes 20 -PollSeconds 15
  pwsh scripts/merge-pr.ps1 -SelfTest                        # offline: decision logic on scripts/tests/merge-pr fixtures

Rules, each from a past incident:
  - Every name in -Required (default unit-tests, CodeQL, Analyze (csharp)) must be PRESENT in `gh pr checks`, and no
    check may be pending. A branch deleted while only unit-tests had reported made GitHub close the PR.
  - A CONFLICTING or DIRTY pull request never gets pull_request checks, so it stops at once with exit 3.
  - The merge passes --match-head-commit with the headRefOid read when the checks were evaluated, so a push after the
    checks cannot be merged unchecked.
  - The remote branch, local branch and worktree are removed only after `gh pr view` reports state MERGED.
  - Any failed or cancelled check (required or not) stops with exit 1; skipped checks are fine.
  - Blank git refs are not repaired. If `git pull --ff-only` fails after the merge the script prints the recovery
    steps and exits 4.

  - -Auto does not wait and does not clean up. It refuses a CONFLICTING, closed or failing pull request, otherwise runs
    gh pr merge --auto --squash --delete-branch and exits 0; GitHub merges once the repository's required checks pass.
    A -Worktree is NOT removed in this mode (it is still needed until the merge); remove it afterwards by hand.

Exit codes: 0 merged and cleaned up (or -DryRun finished, or -Auto enabled auto-merge / found the PR merged), 1 check failed, timeout, closed PR, or merge did not
complete, 2 gh missing or not authenticated or bad arguments, 3 merge conflict, 4 pull after merge failed.
#>
[CmdletBinding()]
param(
    [string]$Pr,
    [string]$Worktree,
    [string[]]$Required = @('unit-tests', 'CodeQL', 'Analyze (csharp)'),
    [int]$TimeoutMinutes = 40,
    [int]$PollSeconds = 30,
    [switch]$DryRun,
    [switch]$Auto,
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest

# ------------------------------------------------------------- pure decision logic

function Get-MergeDecision
{
    # Pr: state, mergeable, mergeStateStatus, headRefOid. Checks: name and bucket (pass, fail, pending, skipping, cancel).
    # Action is one of Merged, Closed, Conflict, Fail, Wait, Merge.
    param($Pr, [object[]]$Checks, [string[]]$Required)

    $Checks = @($Checks | Where-Object { $_ })
    $names = @($Checks | ForEach-Object { [string]$_.name })
    $missing = @($Required | Where-Object { $_ -notin $names })
    $pending = @($Checks | Where-Object { $_.bucket -eq 'pending' } | ForEach-Object { [string]$_.name })
    $failed = @($Checks | Where-Object { $_.bucket -in 'fail', 'cancel' } | ForEach-Object { "$($_.name): $($_.bucket)" })
    $head = [string]$Pr.headRefOid

    $result = { param($action, $reason) [pscustomobject]@{ Action = $action; Reason = $reason; Head = $head; Missing = $missing; Pending = $pending; Failed = $failed } }

    if ($Pr.state -eq 'MERGED')
    {
        return & $result 'Merged' 'the pull request is already merged'
    }
    if ($Pr.state -eq 'CLOSED')
    {
        return & $result 'Closed' 'the pull request is closed without being merged'
    }
    if ($Pr.mergeable -eq 'CONFLICTING' -or $Pr.mergeStateStatus -eq 'DIRTY')
    {
        return & $result 'Conflict' 'the pull request has merge conflicts, so GitHub will never run its pull_request checks; rebase or merge master into the branch, push, and run this again'
    }
    if ($failed.Count -gt 0)
    {
        return & $result 'Fail' "failed checks: $($failed -join ', ')"
    }
    if ($missing.Count -gt 0 -or $pending.Count -gt 0)
    {
        $parts = @()
        if ($missing.Count -gt 0)
        {
            $parts += "required checks not registered yet: $($missing -join ', ')"
        }
        if ($pending.Count -gt 0)
        {
            $parts += "pending: $($pending -join ', ')"
        }
        return & $result 'Wait' ($parts -join '; ')
    }
    if (-not $head)
    {
        return & $result 'Wait' 'headRefOid is not available yet'
    }
    & $result 'Merge' "all required checks are present and none is pending or failed ($($Required -join ', '))"
}

function Get-AutoDecision
{
    # -Auto only enables GitHub auto-merge, which itself waits for the required checks, so pending or not yet registered
    # checks are fine. Action is one of Merged, Closed, Conflict, Fail, Enable.
    param($Pr, [object[]]$Checks, [string[]]$Required)

    $decision = Get-MergeDecision $Pr $Checks $Required
    $action = if ($decision.Action -in 'Wait', 'Merge') { 'Enable' } else { $decision.Action }
    [pscustomobject]@{ Action = $action; Reason = $decision.Reason; Head = $decision.Head }
}

function Test-HeadMatches([string]$CheckedHead, [string]$CurrentHead)
{
    [bool]$CheckedHead -and $CheckedHead -eq $CurrentHead
}

function Get-PostMergeDecision([string]$State)
{
    # Cleanup only when GitHub itself says the pull request is MERGED.
    if ($State -eq 'MERGED')
    {
        return 'Cleanup'
    }
    'Stop'
}

# ------------------------------------------------------------------------- self-test

function Invoke-SelfTest
{
    $fixtureDir = Join-Path $PSScriptRoot 'tests/merge-pr'
    $failures = [Collections.Generic.List[string]]::new()
    $count = 0
    $files = @(Get-ChildItem -LiteralPath $fixtureDir -Filter '*.json' -File | Sort-Object Name)
    if ($files.Count -lt 8)
    {
        $failures.Add("expected at least 8 fixtures in $fixtureDir, found $($files.Count)")
    }
    foreach ($file in $files)
    {
        $fx = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
        $required = if ($fx.PSObject.Properties['required']) { @($fx.required) } else { @('unit-tests', 'CodeQL', 'Analyze (csharp)') }
        $decision = Get-MergeDecision $fx.pr @($fx.checks) $required
        $count++
        if ($decision.Action -ne $fx.expect.action)
        {
            $failures.Add("$($file.Name): expected action $($fx.expect.action), got $($decision.Action) ($($decision.Reason))")
        }
        if ($fx.PSObject.Properties['expectAuto'])
        {
            $auto = Get-AutoDecision $fx.pr @($fx.checks) $required
            $count++
            if ($auto.Action -ne $fx.expectAuto.action)
            {
                $failures.Add("$($file.Name): expected -Auto action $($fx.expectAuto.action), got $($auto.Action) ($($auto.Reason))")
            }
        }
        if ($fx.expect.PSObject.Properties['reasonContains'] -and $decision.Reason -notlike "*$($fx.expect.reasonContains)*")
        {
            $failures.Add("$($file.Name): reason '$($decision.Reason)' lacks '$($fx.expect.reasonContains)'")
        }
        if ($fx.PSObject.Properties['afterMerge'])
        {
            $after = $fx.afterMerge
            $count += 2
            $same = Test-HeadMatches $decision.Head ([string]$after.currentHeadRefOid)
            if ($same -ne [bool]$after.expectHeadMatches)
            {
                $failures.Add("$($file.Name): head match expected $($after.expectHeadMatches), got $same")
            }
            $post = Get-PostMergeDecision ([string]$after.state)
            if ($post -ne $after.expectPostMerge)
            {
                $failures.Add("$($file.Name): post-merge expected $($after.expectPostMerge), got $post")
            }
        }
    }
    $count++
    if ((Get-PostMergeDecision 'OPEN') -ne 'Stop' -or (Get-PostMergeDecision 'CLOSED') -ne 'Stop' -or (Get-PostMergeDecision 'MERGED') -ne 'Cleanup')
    {
        $failures.Add('Get-PostMergeDecision must clean up only for MERGED')
    }
    if ($failures.Count -gt 0)
    {
        $failures | ForEach-Object { Write-Host "ERROR self-test: $_" }
        exit 1
    }
    Write-Host "merge-pr self-test passed: $count checks over $($files.Count) fixtures."
    exit 0
}

if ($SelfTest)
{
    Invoke-SelfTest
}

# ----------------------------------------------------------------------------- main

if (-not $Pr)
{
    Write-Host 'ERROR -Pr is required (a pull request number or branch name).'
    exit 2
}
if (-not (Get-Command gh -ErrorAction SilentlyContinue))
{
    Write-Host 'gh CLI not found. Install it from https://cli.github.com, then run: gh auth login'
    exit 2
}
$null = & gh auth status 2>&1
if ($LASTEXITCODE -ne 0)
{
    Write-Host 'gh is not authenticated. Run: gh auth login'
    exit 2
}

function Get-PrJson
{
    $text = & gh pr view $Pr --json number,state,headRefName,headRefOid,mergeable,mergeStateStatus 2>&1
    if ($LASTEXITCODE -ne 0)
    {
        Write-Host "ERROR gh pr view $Pr failed: $text"
        exit 1
    }
    ($text -join "`n") | ConvertFrom-Json
}

function Get-ChecksJson
{
    # gh pr checks exits 8 while checks are pending and 1 when none are reported; the JSON on stdout is what counts.
    $text = & gh pr checks $Pr --json name,bucket 2>$null
    if (-not $text)
    {
        return @()
    }
    try
    {
        @(($text -join "`n") | ConvertFrom-Json)
    }
    catch
    {
        @()
    }
}

if ($Auto)
{
    $prInfo = Get-PrJson
    $checks = if ($prInfo.state -eq 'OPEN') { Get-ChecksJson } else { @() }
    $decision = Get-AutoDecision $prInfo $checks $Required
    Write-Host "PR #$($prInfo.number) $($prInfo.headRefName) @ $($decision.Head): $($decision.Action) - $($decision.Reason)"
    if ($decision.Action -eq 'Merged')
    {
        Write-Host 'Already merged; nothing to enable.'
        exit 0
    }
    if ($decision.Action -eq 'Conflict')
    {
        exit 3
    }
    if ($decision.Action -in 'Fail', 'Closed')
    {
        Write-Host 'NOT ENABLING AUTO-MERGE.'
        exit 1
    }
    $mergeArgs = @('pr', 'merge', [string]$prInfo.number, '--auto', '--squash', '--delete-branch')
    if ($decision.Head)
    {
        $mergeArgs += @('--match-head-commit', $decision.Head)
    }
    if ($DryRun)
    {
        Write-Host "DRY RUN: would run gh $($mergeArgs -join ' ')"
        exit 0
    }
    $out = & gh @mergeArgs 2>&1
    if ($LASTEXITCODE -ne 0)
    {
        Write-Host "ERROR enabling auto-merge failed: $out"
        exit 1
    }
    $out | Select-Object -Last 3 | ForEach-Object { Write-Host $_ }
    Write-Host "Auto-merge enabled for #$($prInfo.number): GitHub squash-merges and deletes the remote branch once the required checks pass."
    if ($Worktree)
    {
        Write-Host "Worktree '$Worktree' was NOT removed; clean it up after the merge (git worktree remove, git branch -D, git pull --ff-only)."
    }
    exit 0
}

$deadline = (Get-Date).AddMinutes($TimeoutMinutes)
while ($true)
{
    $prInfo = Get-PrJson
    $checks = if ($prInfo.state -eq 'OPEN') { Get-ChecksJson } else { @() }
    $decision = Get-MergeDecision $prInfo $checks $Required
    Write-Host "PR #$($prInfo.number) $($prInfo.headRefName) @ $($decision.Head): $($decision.Action) - $($decision.Reason)"

    if ($decision.Action -eq 'Conflict')
    {
        exit 3
    }
    if ($decision.Action -eq 'Fail')
    {
        Write-Host 'NOT MERGING: a check failed.'
        exit 1
    }
    if ($decision.Action -eq 'Closed')
    {
        exit 1
    }
    if ($decision.Action -ne 'Wait')
    {
        break
    }
    if ($DryRun)
    {
        Write-Host 'DRY RUN: would keep waiting.'
        exit 0
    }
    if ((Get-Date) -gt $deadline)
    {
        Write-Host "TIMEOUT after $TimeoutMinutes minutes: $($decision.Reason)"
        exit 1
    }
    Start-Sleep -Seconds $PollSeconds
}

$checks | ForEach-Object { Write-Host "  $($_.name): $($_.bucket)" }
$branch = [string]$prInfo.headRefName

if ($decision.Action -eq 'Merge')
{
    if ($DryRun)
    {
        Write-Host "DRY RUN: would merge #$($prInfo.number) with --squash --match-head-commit $($decision.Head), then delete branch '$branch'."
        exit 0
    }
    $out = & gh pr merge $prInfo.number --squash --match-head-commit $decision.Head 2>&1
    $out | Select-Object -Last 3 | ForEach-Object { Write-Host $_ }
}
elseif ($DryRun)
{
    Write-Host "DRY RUN: #$($prInfo.number) is already merged; would only clean up branch '$branch'."
    exit 0
}

$state = [string](& gh pr view $prInfo.number --json state --jq .state 2>&1)
Write-Host "#$($prInfo.number) state: $state"
if ((Get-PostMergeDecision $state) -ne 'Cleanup')
{
    Write-Host 'NOT CLEANING UP: the pull request is not MERGED (the head may have changed after the checks, or the merge was refused). Nothing was deleted.'
    exit 1
}

$mainTree = ((& git worktree list --porcelain | Select-Object -First 1) -replace '^worktree ', '')
if ($Worktree -and (Test-Path -LiteralPath $Worktree))
{
    $removed = & git -C $mainTree worktree remove $Worktree 2>&1
    if ($LASTEXITCODE -ne 0)
    {
        Write-Host "WARNING worktree not removed (uncommitted changes? still in use?): $removed"
    }
}
& git -C $mainTree push origin --delete $branch 2>&1 | Select-Object -Last 1 | ForEach-Object { Write-Host $_ }
& git -C $mainTree branch -D $branch 2>&1 | Select-Object -Last 1 | ForEach-Object { Write-Host $_ }

$pull = & git -C $mainTree pull -q --ff-only 2>&1
if ($LASTEXITCODE -ne 0)
{
    Write-Host "ERROR git pull --ff-only failed in ${mainTree}: $pull"
    Write-Host 'The merge and the branch cleanup are done. Recover by hand:'
    Write-Host "  git -C `"$mainTree`" fetch origin --prune"
    Write-Host "  git -C `"$mainTree`" pull --ff-only"
    Write-Host '  If fetch reports a bad or empty ref, delete that empty file under .git/refs/remotes/origin and fetch again.'
    exit 4
}
& git -C $mainTree log --oneline -1
exit 0
