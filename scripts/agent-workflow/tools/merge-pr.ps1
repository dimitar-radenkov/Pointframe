#Requires -Version 7.0
<#
Waits for a pull request's checks, squash-merges the exact head that was checked, then cleans up. For agents.

  merge-pr.ps1 -Pr 190                          # PR number or branch name
  merge-pr.ps1 -Pr 190 -Worktree ..\repo-wt-x   # also remove that worktree after the merge
  merge-pr.ps1 -Pr 190 -DryRun                  # evaluate once and report; never merges or deletes
  merge-pr.ps1 -PinOnly -Worktree <path>        # pin locally before verify (hooks.pin); commit work -> PinOnly -> verify -> push + gh pr create -> -Auto
  merge-pr.ps1 -Pr 190 -Worktree <path> -Auto   # enable auto-merge after receipt and review gates
  merge-pr.ps1 -Pr 190 -Auto -WaitMerged        # enable, wait, and clean up after merge
  merge-pr.ps1 -Pr 190 -Required unit-tests,CodeQL -TimeoutMinutes 20 -PollSeconds 15
  merge-pr.ps1 -SyncMain                        # fast-forward the main checkout to <remote>/<base> when nothing is running
  merge-pr.ps1 -SyncMain -DryRun                # print that decision; change nothing
  merge-pr.ps1 -SelfTest                        # offline: decision logic on the kit's merge-pr fixtures
  merge-pr.ps1 ... -Root <main checkout>         # must be the main checkout of the repository of -Worktree (default: that main checkout)

The gate configuration is read ONLY from the main checkout of the repository that holds -Worktree (a pull request can change its own
agent-workflow.json, so the worktree's copy is never trusted); an explicit -Root that is not that main checkout is refused. It supplies the required checks (merge.requiredChecks), the base (repository.baseRef:
remote and branch), the state folder under git's common directory (state.commonDirNamespace: reviews, packages, ship.lock,
main-sync.lock), the pin files (pinException.paths) and the pin adapter (hooks.pin). The caller's working folder is never
consulted; gh runs in the main checkout.

Rules, each from a past incident:
  - Every name in -Required (default: merge.requiredChecks) must be PRESENT in `gh pr checks`, and no
    check may be pending. A branch deleted while only unit-tests had reported made GitHub close the PR.
  - A CONFLICTING or DIRTY pull request never gets pull_request checks, so it stops at once with exit 3.
  - The merge passes --match-head-commit with the headRefOid read when the checks were evaluated, so a push after the
    checks cannot be merged unchecked.
  - The remote branch, local branch and worktree are removed only after `gh pr view` reports state MERGED.
  - Any failed or cancelled check (required or not) stops with exit 1; skipped checks are fine.
  - After the merge the main checkout's HEAD and files are NOT touched (a work package runner fingerprints the main checkout, and any
    change aborts every package whose executor or review is running). Cleanup only runs `git fetch <remote> --prune` there and
    prints where the main checkout and <remote>/<base> are. A failed fetch is a warning with recovery commands, not an error.
  - -SyncMain brings the main checkout up to date (`git pull --ff-only <remote> <base>`), and only when the main checkout is on
    the base branch with no uncommitted changes, no package state file is in a stage where an executor or reviewer may be
    active (running, executor-kill-failed, reviewing), and no ship holds <git common dir>/<namespace>/ship.lock. Otherwise it
    changes nothing, names every blocker and exits 3; a failed fast-forward exits 4. Blank git refs are not repaired.

  - -Auto does not wait and does not clean up. It refuses a CONFLICTING, closed or failing pull request, otherwise runs
    gh pr merge --auto --squash --delete-branch and exits 0; GitHub merges once the repository's required checks pass.
    A -Worktree is NOT removed in this mode (it is still needed until the merge); remove it afterwards by hand.
  - Order: commit work -> -PinOnly -Worktree -> verify.ps1 -> record-review.ps1 -> push + gh pr create -> -Auto -Worktree.
    Auto requires a complete passing receipt for the PR head and current worktree, or for an ancestor whose only later
    change is the pin files (then the pin adapter, hooks.pin -Action Validate, must pass). If a release lands after verify, -Auto commits and
    pushes the pin itself, so no second verify.ps1 is needed. Merging always requires -Worktree and a review record.

The pin adapter (hooks.pin) is a repository script run with -Action Behind|Update|Validate -Root <worktree>. Behind: exit 0 not
behind, 10 behind. Update: exit 0 already current, 10 updated and committed, 1 failed (it printed why). Validate: exit 0 valid;
otherwise its last line says why. Any other exit code is an error whose message is the hook's last line. A null hook means no
pinning, and the pin files then excuse no change.

Exit codes: 0 merged and cleaned up (or -DryRun finished, or -Auto enabled auto-merge / found the PR merged), 1 check failed, timeout, closed PR, or merge did not
complete, 2 gh missing or not authenticated or bad arguments, 3 merge conflict (-SyncMain: refused, blockers named), 4 -SyncMain's fast-forward failed.
#>
[CmdletBinding()]
param(
    [string]$Pr,
    [string]$Worktree,
    [string[]]$Required = @(),
    [int]$TimeoutMinutes = 40,
    [int]$PollSeconds = 30,
    [switch]$DryRun,
    [switch]$Auto,
    [switch]$NoPluginPin,
    [switch]$PinOnly,
    [switch]$WaitMerged,
    [switch]$SyncMain,
    [string]$StatusId,
    [string]$Root,
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '..' 'lib' 'root.ps1')
. (Join-Path $PSScriptRoot '..' 'lib' 'config.ps1')
. (Join-Path $PSScriptRoot '..' 'lib' 'context.ps1')
. (Join-Path $PSScriptRoot '..' 'lib' 'selftest.ps1')
. (Join-Path $PSScriptRoot '..' 'lib' 'tree-hash.ps1')
. (Join-Path $PSScriptRoot '..' 'lib' 'review-record.ps1')
. (Join-Path $PSScriptRoot '..' 'lib' 'verify-cache.ps1')

# Settings of the repository this run works on, set from the configuration (or by the self-test for a temporary one).
$script:Remote = 'origin'
$script:BaseBranchName = 'master'
$script:Namespace = 'selftest'
$script:MainCheckout = ''
$script:ReviewSettings = $null
$script:Ctx = $null
$script:ToolPath = $PSCommandPath
$WorktreeFull = if ($Worktree) { ConvertTo-AwFullPath $Worktree } else { '' }

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
        return & $result 'Conflict' "the pull request has merge conflicts, so GitHub will never run its pull_request checks; rebase or merge $($script:BaseBranchName) into the branch, push, and run this again"
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

function Test-ReceiptFixture
{
    param($Fixture)
    $receipt = if ($Fixture.PSObject.Properties['receipt']) { $Fixture.receipt } else { $null }
    if ($Fixture.PSObject.Properties['missing'] -and $Fixture.missing) { $receipt = $null }
    $environment = if ($Fixture.PSObject.Properties['environment']) { $Fixture.environment } else { $null }
    Test-VerifyReceiptObject $receipt 'head-a' 'tree-a' $environment
}

function Update-Status([string]$Stage, [string]$Artifact, [string]$Blocker)
{
    if (-not $StatusId) { return }
    $statusScript = Join-Path $PSScriptRoot 'status.ps1'
    # Success stages pass no blocker, which clears an earlier one ('none').
    $statusArguments = @('-NoProfile', '-File', $statusScript, '-Set', '-Id', $StatusId, '-Stage', $Stage)
    $statusArguments += @('-Blocker', $(if ($Blocker) { $Blocker } else { 'none' }))
    if ($Artifact)
    {
        $statusArguments += @('-Artifact', $Artifact)
    }
    $statusArguments += @('-Root', $script:Ctx.Root)
    & pwsh @statusArguments
}

function Test-MergeReview([string]$WorktreePath, [string]$PrHead, $Settings = $script:ReviewSettings)
{
    # Settings: Namespace (the state folder under git's common directory), BaseRef (the merge base of a small fix) and
    # PinFiles (pinException.paths).
    $root = (Resolve-Path -LiteralPath $WorktreePath).Path
    $tree = (& git -C $root rev-parse "$PrHead^{tree}").Trim()
    $common = (& git -C $root rev-parse --path-format=absolute --git-common-dir).Trim()
    if (-not [IO.Path]::IsPathRooted($common)) { $common = Join-Path $root $common }
    $reviewDir = Join-Path ([IO.Path]::GetFullPath($common)) "$($Settings.Namespace)/reviews"
    $candidate = $PrHead
    for ($depth = 0; $depth -le 3; $depth++)
    {
        $path = Join-Path $reviewDir "$candidate.json"
        if (Test-Path -LiteralPath $path -PathType Leaf)
        {
            try { $record = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json } catch { return [pscustomobject]@{ Ok = $false; Reason = 'record is unparseable' } }
            $recordTree = (& git -C $root rev-parse "$candidate^{tree}" 2>$null).Trim()
            $decision = Test-ReviewRecordObject $record $candidate $recordTree (Test-Path -LiteralPath ([string]$record.evidence) -PathType Leaf)
            if (-not $decision.Ok) { return $decision }
            $isSmallFix = $record.PSObject.Properties['reason'] -and $record.reason -eq 'small-fix'
            if ($candidate -eq $PrHead)
            {
                if ($isSmallFix)
                {
                    if ($record.reviewer -ne 'self' -or $record.executor -ne 'claude:coordinator') { return [pscustomobject]@{ Ok = $false; Reason = 'review identities must be reviewer self and executor claude:coordinator' } }
                    $base = (& git -C $root merge-base $PrHead $Settings.BaseRef 2>$null).Trim()
                    if ($LASTEXITCODE -ne 0 -or -not $base) { return [pscustomobject]@{ Ok = $false; Reason = "cannot recompute small-fix change counts ($($Settings.BaseRef) merge-base unavailable)" } }
                    $actual = Get-ReviewChangeCount $root $base $PrHead $Settings.PinFiles
                    if (@($record.changedFiles).Count -ne $actual.Files.Count -or [int]$record.changedLines -ne $actual.Lines) { return [pscustomobject]@{ Ok = $false; Reason = 'recorded changed files/lines do not match git' } }
                }
                return $decision
            }
            if ($isSmallFix)
            {
                $base = (& git -C $root merge-base $candidate $Settings.BaseRef 2>$null).Trim()
                if ($LASTEXITCODE -ne 0 -or -not $base) { return [pscustomobject]@{ Ok = $false; Reason = "cannot recompute small-fix change counts ($($Settings.BaseRef) merge-base unavailable)" } }
                $actual = Get-ReviewChangeCount $root $base $candidate $Settings.PinFiles
                if (@($record.changedFiles).Count -ne $actual.Files.Count -or [int]$record.changedLines -ne $actual.Lines) { return [pscustomobject]@{ Ok = $false; Reason = 'recorded changed files/lines do not match git' } }
            }
            $paths = @(& git -C $root diff --name-only $candidate $PrHead 2>$null)
            $receipt = [pscustomobject]@{ status='pass'; complete=$true; filter=$null; head=$candidate; treeHash=$recordTree }
            $pin = Test-PinOnlyChange $receipt $recordTree $true $tree (Get-WorkingTreeHash $root) $paths $Settings.PinFiles
            if ($pin.Ok -and -not $Settings.PinHook) { return [pscustomobject]@{ Ok=$false; Reason='pin-only change after the reviewed commit, but hooks.pin is not configured to validate the pin files' } }
            if ($pin.Ok) { return [pscustomobject]@{ Ok=$true; Reason="review record for ancestor accepted; $($pin.Reason)" } }
            return [pscustomobject]@{ Ok=$false; Reason=$pin.Reason }
        }
        if ($depth -eq 3) { break }
        $candidate = (& git -C $root rev-parse "$candidate^1" 2>$null).Trim()
        if ($LASTEXITCODE -ne 0 -or -not $candidate) { break }
    }
    Test-ReviewRecordObject $null $PrHead $tree
}

# --------------------------------------------------------- main checkout: cleanup and sync

# Stages in which a work package runner may have an executor or reviewer running while it holds a main-checkout fingerprint:
# 'running' (run-started: set before the executor launches), 'executor-kill-failed' (a killed executor survived;
# the next run reaps it) and 'reviewing' (review-started). The runs of `fix` pass through 'running'.
$script:SyncMainActiveStages = @('running', 'executor-kill-failed', 'reviewing')

function Get-MainTree
{
    $script:MainCheckout
}

function Get-SyncMainCommand
{
    # The command a person types to bring the main checkout up to date: the consumer's own wrapper (scripts/merge-pr.ps1)
    # when it has one, otherwise this tool addressed by its full path and the repository it works on.
    $wrapper = Join-Path $script:MainCheckout 'scripts\merge-pr.ps1'
    if ($script:MainCheckout -and (Test-Path -LiteralPath $wrapper -PathType Leaf))
    {
        return 'pwsh scripts/merge-pr.ps1 -SyncMain'
    }
    if ($script:MainCheckout)
    {
        return "pwsh -NoProfile -File `"$($script:ToolPath)`" -SyncMain -Root `"$($script:MainCheckout)`""
    }
    return 'pwsh scripts/merge-pr.ps1 -SyncMain'
}

function Get-BaseBranch([string]$MainTree)
{
    # The base branch is the one in repository.baseRef; the remote's HEAD is not consulted.
    $script:BaseBranchName
}

function Invoke-MergedCleanup([string]$Branch, [string]$MainTree = (Get-MainTree), [string]$WorktreePath = $WorktreeFull)
{
    # Only after GitHub reported MERGED: remove the worktree and both branches, then fetch. HEAD, the index and the files
    # of the main checkout stay as they are (a work package runner fingerprints them); -SyncMain moves them when nothing is running.
    $remote = $script:Remote
    if ($WorktreePath -and (Test-Path -LiteralPath $WorktreePath))
    {
        $removed = & git -C $MainTree worktree remove $WorktreePath 2>&1
        if ($LASTEXITCODE -ne 0)
        {
            Write-Host "WARNING worktree not removed (uncommitted changes? still in use?): $removed"
        }
    }
    $remoteBranch = & git -C $MainTree ls-remote --exit-code --heads $remote $Branch 2>&1
    if ($LASTEXITCODE -eq 0)
    {
        & git -C $MainTree push $remote --delete $Branch 2>&1 | Select-Object -Last 1 | ForEach-Object { Write-Host $_ }
    }
    else
    {
        Write-Host "Remote branch $Branch already deleted."
    }
    & git -C $MainTree branch -D $Branch 2>&1 | Select-Object -Last 1 | ForEach-Object { Write-Host $_ }

    $fetch = & git -C $MainTree fetch $remote --prune 2>&1
    if ($LASTEXITCODE -ne 0)
    {
        Write-Host "WARNING git fetch $remote --prune failed in ${MainTree}: $fetch"
        Write-Host 'The merge and the branch cleanup are done. Recover by hand:'
        Write-Host "  git -C `"$MainTree`" fetch $remote --prune"
        Write-Host "  $(Get-SyncMainCommand)"
        Write-Host "  If fetch reports a bad or empty ref, delete that empty file under .git/refs/remotes/$remote and fetch again."
        return
    }
    $base = Get-BaseBranch $MainTree
    $head = [string](& git -C $MainTree rev-parse --short=10 HEAD 2>$null)
    $remoteHead = [string](& git -C $MainTree rev-parse --short=10 "refs/remotes/$remote/$base" 2>$null)
    Write-Host "Main checkout left at $($head.Trim()); $remote/$base is at $($remoteHead.Trim()). When no package is running, bring it up to date with: $(Get-SyncMainCommand)"
}

function Get-SyncMainBlockers([string]$MainTree, [string]$Base)
{
    $blockers = [Collections.Generic.List[string]]::new()

    $branch = [string](& git -C $MainTree symbolic-ref -q --short HEAD 2>$null)
    if (-not $branch)
    {
        $blockers.Add("the main checkout is on a detached HEAD, not the base branch '$Base'")
    }
    elseif ($branch -ne $Base)
    {
        $blockers.Add("the main checkout is on branch '$branch', not the base branch '$Base'")
    }
    $status = @(& git -C $MainTree status --porcelain --untracked-files=all 2>&1 | ForEach-Object { "$_" } | Where-Object { $_ })
    if ($LASTEXITCODE -ne 0)
    {
        $blockers.Add("git status failed in ${MainTree}: $($status -join ' ')")
    }
    elseif ($status.Count -gt 0)
    {
        $blockers.Add("the main checkout has $($status.Count) uncommitted tracked or untracked change(s), first: $($status[0])")
    }

    $common = [string](& git -C $MainTree rev-parse --path-format=absolute --git-common-dir 2>$null)
    $packageDir = Join-Path $common "$($script:Namespace)/packages"
    if (Test-Path -LiteralPath $packageDir -PathType Container)
    {
        foreach ($file in @(Get-ChildItem -LiteralPath $packageDir -Filter '*.json' -File | Sort-Object Name))
        {
            try
            {
                $package = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
                $stage = if ($package.PSObject.Properties['stage']) { [string]$package.stage } else { '' }
            }
            catch
            {
                $blockers.Add("package state $($file.Name) is unreadable, so it cannot be shown idle: $($_.Exception.Message)")
                continue
            }
            if ($stage -in $script:SyncMainActiveStages)
            {
                $blockers.Add("package '$($file.BaseName)' is in stage '$stage', so an executor or reviewer may be running (finish it with package.ps1 run or review first)")
            }
        }
    }

    # The work package runner keeps this file open with no sharing while a ship runs; a file nobody holds is a dead ship's leftover.
    $shipLock = Join-Path $common "$($script:Namespace)/ship.lock"
    if (Test-Path -LiteralPath $shipLock -PathType Leaf)
    {
        try
        {
            $stream = [IO.File]::Open($shipLock, 'Open', 'Read', 'ReadWrite')
            $stream.Dispose()
        }
        catch [IO.IOException]
        {
            $blockers.Add("a ship is in flight (the ship lock $shipLock is held)")
        }
    }
    $blockers.ToArray()
}

function Enter-MainSyncExclusiveLock([string]$MainTree)
{
    # -SyncMain uses <git common dir>/<namespace>/main-sync.lock with FileShare.None; package run/review/fix open it shared with FileShare.ReadWrite.
    $common = [string](& git -C $MainTree rev-parse --path-format=absolute --git-common-dir 2>$null)
    if ($LASTEXITCODE -ne 0 -or -not $common) { throw "cannot resolve the git common directory for -SyncMain in $MainTree" }
    $directory = Join-Path $common $script:Namespace
    $lockPath = Join-Path $directory 'main-sync.lock'
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    try { [IO.File]::Open($lockPath, 'OpenOrCreate', 'ReadWrite', 'None') }
    catch [IO.IOException]
    {
        throw "a package run or review is active (the main-sync lock $lockPath is held); -SyncMain was refused"
    }
}

function Invoke-SyncMainCore([string]$MainTree, [switch]$Preview)
{
    $remote = $script:Remote
    $base = Get-BaseBranch $MainTree
    $prefix = if ($Preview) { 'DRY RUN: ' } else { '' }
    $blockers = @(Get-SyncMainBlockers $MainTree $base)
    if ($blockers.Count -gt 0)
    {
        Write-Host "${prefix}SYNC REFUSED: the main checkout $MainTree was not changed; $($blockers.Count) blocker(s):"
        $blockers | ForEach-Object { Write-Host "  - $_" }
        return 3
    }
    $head = [string](& git -C $MainTree rev-parse HEAD)
    $target = [string](& git -C $MainTree rev-parse --verify -q "refs/remotes/$remote/$base" 2>$null)
    if ($Preview)
    {
        & git -C $MainTree merge-base --is-ancestor $head $target 2>$null
        if (-not $target) { Write-Host "DRY RUN: $remote/$base is not known locally; the real run would fetch it and fast-forward only if possible." }
        elseif ($head -eq $target) { Write-Host "DRY RUN: nothing blocks the sync, and the main checkout is already at $remote/$base ($($head.Substring(0, 10)))." }
        elseif ($LASTEXITCODE -eq 0) { Write-Host "DRY RUN: nothing blocks the sync; it would fast-forward $($head.Substring(0, 10)) -> $($target.Substring(0, 10)) ($remote/$base as last fetched; the real run fetches first)." }
        else { Write-Host "DRY RUN: nothing blocks the sync, but HEAD $($head.Substring(0, 10)) is not behind $remote/$base $($target.Substring(0, 10)) as last fetched; the fast-forward may fail." }
        return 0
    }
    $pull = & git -C $MainTree pull -q --ff-only $remote $base 2>&1
    if ($LASTEXITCODE -ne 0)
    {
        Write-Host "ERROR git pull --ff-only $remote $base failed in ${MainTree}: $pull"
        Write-Host 'Recover by hand:'
        Write-Host "  git -C `"$MainTree`" fetch $remote --prune"
        Write-Host "  git -C `"$MainTree`" pull --ff-only $remote $base"
        Write-Host "  If fetch reports a bad or empty ref, delete that empty file under .git/refs/remotes/$remote and fetch again."
        return 4
    }
    $now = [string](& git -C $MainTree rev-parse HEAD)
    Write-Host "Main checkout is at $remote/$base ($($now.Substring(0, 10)))."
    & git -C $MainTree log --oneline -1 | Out-Host
    0
}

function Invoke-SyncMain([string]$MainTree, [switch]$Preview)
{
    try { $lock = Enter-MainSyncExclusiveLock $MainTree }
    catch
    {
        Write-Host "SYNC REFUSED: $($_.Exception.Message)"
        return 3
    }
    try { return (Invoke-SyncMainCore $MainTree -Preview:$Preview) }
    finally { $lock.Dispose() }
}

function Test-MainCheckoutBehavior([Collections.Generic.List[string]]$Failures)
{
    # Temporary repositories only: a bare "origin", the "main" checkout, and a second clone that plays GitHub's merge.
    $count = 0
    $root = Join-Path ([IO.Path]::GetTempPath()) "merge-main-$([guid]::NewGuid().ToString('N'))"
    $identity = @('-c', 'user.name=SelfTest', '-c', 'user.email=selftest@example.invalid')
    $git = {
        param([string]$Dir, [string[]]$GitArgs)
        $out = & git @identity -C $Dir @GitArgs 2>&1
        if ($LASTEXITCODE -ne 0) { throw "git $($GitArgs -join ' ') failed in ${Dir}: $out" }
        (($out | ForEach-Object { "$_" }) -join "`n").Trim()
    }
    $expect = { param([bool]$Condition, [string]$Message) $script:mainCheckoutCount++; if (-not $Condition) { $Failures.Add("main checkout: $Message") } }
    $script:mainCheckoutCount = 0
    $heldLock = $null
    try
    {
        New-Item -ItemType Directory -Path $root | Out-Null
        $script:Remote = 'origin'; $script:BaseBranchName = 'master'; $script:Namespace = 'selftest'
        $origin = Join-Path $root 'origin.git'
        $main = Join-Path $root 'main'
        $other = Join-Path $root 'other'
        & $git $root @('init', '-q', '--bare', '-b', 'master', $origin) | Out-Null
        & $git $root @('clone', '-q', $origin, $main) | Out-Null
        & $git $main @('config', 'core.autocrlf', 'false') | Out-Null
        & $git $main @('symbolic-ref', 'HEAD', 'refs/heads/master') | Out-Null
        [IO.File]::WriteAllText((Join-Path $main 'tracked.txt'), "v1`n")
        Write-AwSelfTestConfig $main (Get-AwSelfTestConfig -Namespace 'selftest')
        & $git $main @('add', '.') | Out-Null
        & $git $main @('commit', '-q', '-m', 'v1') | Out-Null
        & $git $main @('push', '-q', 'origin', 'master') | Out-Null
        & $git $main @('remote', 'set-head', 'origin', 'master') | Out-Null
        & $git $root @('clone', '-q', $origin, $other) | Out-Null
        & $git $other @('config', 'core.autocrlf', 'false') | Out-Null

        # A package worktree on branch feature, pushed, then "merged" by another clone moving origin/master.
        $worktree = Join-Path $root 'pkg-wt'
        & $git $main @('worktree', 'add', '-q', '-b', 'feature', $worktree) | Out-Null
        & $git $worktree @('config', 'core.autocrlf', 'false') | Out-Null
        [IO.File]::WriteAllText((Join-Path $worktree 'feature.txt'), "f`n")
        & $git $worktree @('add', '.') | Out-Null
        & $git $worktree @('commit', '-q', '-m', 'feature') | Out-Null
        & $git $worktree @('push', '-q', 'origin', 'feature') | Out-Null
        $v1 = & $git $main @('rev-parse', 'HEAD')
        [IO.File]::WriteAllText((Join-Path $other 'tracked.txt'), "v2`n")
        & $git $other @('commit', '-q', '-am', 'v2 (the squash merge)') | Out-Null
        & $git $other @('push', '-q', 'origin', 'master') | Out-Null
        $v2 = & $git $other @('rev-parse', 'HEAD')

        $script:MainCheckout = $main
        $output = (Invoke-MergedCleanup 'feature' $main $worktree 6>&1 | Out-String)
        & $expect ((& $git $main @('rev-parse', 'HEAD')) -eq $v1) 'cleanup moved the main checkout HEAD'
        & $expect (([IO.File]::ReadAllText((Join-Path $main 'tracked.txt'))) -eq "v1`n") 'cleanup changed a tracked file of the main checkout'
        & $expect ((& $git $main @('rev-parse', 'refs/remotes/origin/master')) -eq $v2) 'cleanup did not fetch: origin/master did not move'
        & $expect ((& $git $main @('status', '--porcelain')) -eq '') 'cleanup left the main checkout dirty'
        & $expect (-not (Test-Path -LiteralPath $worktree)) 'cleanup did not remove the worktree'
        & $expect (-not (& $git $main @('branch', '--list', 'feature'))) 'cleanup did not delete the local branch'
        & $expect (-not (& $git $main @('ls-remote', '--heads', 'origin', 'feature'))) 'cleanup did not delete the remote branch'
        & $expect ($output -match "Main checkout left at $($v1.Substring(0, 10)); origin/master is at $($v2.Substring(0, 10))" -and $output -match '-SyncMain' -and $output -notmatch 'pull --ff-only') "cleanup did not print where the checkout was left and the sync command: $output"

        # A failed fetch is a warning with recovery commands; the function returns instead of exiting 4.
        & $git $main @('remote', 'set-url', 'origin', (Join-Path $root 'missing.git')) | Out-Null
        $output = (Invoke-MergedCleanup 'gone' $main '' 6>&1 | Out-String)
        & $expect ($output -match 'WARNING git fetch origin --prune failed' -and $output -match '-SyncMain') "a failed fetch was not a warning with recovery commands: $output"
        & $git $main @('remote', 'set-url', 'origin', $origin) | Out-Null

        # -SyncMain, run as a child process from a folder outside the checkout, with -Root naming it.
        # The children run a copy of the tool placed outside any configured repository: the gate context refuses a repository other than the one
        # that holds the tool, and there is no switch, parameter or variable to skip that.
        $toolCopy = Join-Path $root 'tool-copy'
        New-Item -ItemType Directory -Path (Join-Path $toolCopy 'tools') -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot '..' 'lib') -Destination (Join-Path $toolCopy 'lib') -Recurse
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot '..' 'schema') -Destination (Join-Path $toolCopy 'schema') -Recurse
        Copy-Item -LiteralPath $PSCommandPath -Destination (Join-Path $toolCopy 'tools' 'merge-pr.ps1')
        $selfPath = Join-Path $toolCopy 'tools' 'merge-pr.ps1'
        $sync = {
            param([switch]$Preview)
            Push-Location $root
            try
            {
                $syncArgs = @('-NoProfile', '-File', $selfPath, '-SyncMain', '-Root', $main) + $(if ($Preview) { '-DryRun' })
                $text = (& pwsh @syncArgs 2>&1 | Out-String)
                [pscustomobject]@{ Exit = $LASTEXITCODE; Text = $text }
            }
            finally { Pop-Location }
        }
        $unchanged = { param([string]$Why) & $expect ((& $git $main @('rev-parse', 'HEAD')) -eq $v1 -and ([IO.File]::ReadAllText((Join-Path $main 'tracked.txt'))) -eq "v1`n" -and (& $git $main @('rev-parse', 'refs/remotes/origin/master')) -eq $v2) "$Why changed HEAD, a tracked file or origin/master" }
        $stateDir = Join-Path $main '.git/selftest'
        $packageDir = Join-Path $stateDir 'packages'
        New-Item -ItemType Directory -Path $packageDir -Force | Out-Null

        # Blocker 1: uncommitted changes (tracked, then untracked).
        [IO.File]::WriteAllText((Join-Path $main 'tracked.txt'), "edited`n")
        $r = & $sync
        & $expect ($r.Exit -eq 3 -and $r.Text -match 'SYNC REFUSED' -and $r.Text -match 'uncommitted') "a dirty tracked file did not refuse with exit 3: exit $($r.Exit) $($r.Text)"
        & $expect (([IO.File]::ReadAllText((Join-Path $main 'tracked.txt'))) -eq "edited`n" -and (& $git $main @('rev-parse', 'HEAD')) -eq $v1) 'a refused sync touched the dirty checkout'
        [IO.File]::WriteAllText((Join-Path $main 'tracked.txt'), "v1`n")
        [IO.File]::WriteAllText((Join-Path $main 'untracked.txt'), "u`n")
        $r = & $sync
        & $expect ($r.Exit -eq 3 -and $r.Text -match 'uncommitted' -and $r.Text -match 'untracked\.txt') "an untracked file did not refuse with exit 3: exit $($r.Exit) $($r.Text)"
        Remove-Item -LiteralPath (Join-Path $main 'untracked.txt') -Force

        # Blocker 2: a package in a stage where an executor or reviewer may be active; idle stages do not block.
        foreach ($stage in $script:SyncMainActiveStages)
        {
            $statePath = Join-Path $packageDir 'busy-pkg.json'
            [IO.File]::WriteAllText($statePath, "{`"stage`":`"$stage`"}")
            $r = & $sync
            & $expect ($r.Exit -eq 3 -and $r.Text -match "busy-pkg" -and $r.Text -match "stage '$stage'") "a package in stage '$stage' did not refuse with exit 3: exit $($r.Exit) $($r.Text)"
            Remove-Item -LiteralPath $statePath -Force
        }

        # Blocker 3: a live ship lock (held open with no sharing, as Enter-ShipLock does).
        $lockPath = Join-Path $stateDir 'ship.lock'
        $heldLock = [IO.File]::Open($lockPath, 'OpenOrCreate', 'ReadWrite', 'None')
        $r = & $sync
        & $expect ($r.Exit -eq 3 -and $r.Text -match 'ship is in flight') "a held ship lock did not refuse with exit 3: exit $($r.Exit) $($r.Text)"
        $heldLock.Dispose(); $heldLock = $null
        & $unchanged 'the refused syncs'

        # Several blockers are all named.
        [IO.File]::WriteAllText((Join-Path $main 'untracked.txt'), "u`n")
        [IO.File]::WriteAllText((Join-Path $packageDir 'busy-pkg.json'), '{"stage":"reviewing"}')
        $r = & $sync
        & $expect ($r.Exit -eq 3 -and $r.Text -match 'uncommitted' -and $r.Text -match "stage 'reviewing'") "two blockers were not both named: $($r.Text)"
        Remove-Item -LiteralPath (Join-Path $main 'untracked.txt'), (Join-Path $packageDir 'busy-pkg.json') -Force

        # No blocker (an idle package state and a dead ship's leftover lock file do not count): -DryRun changes nothing, the real run fast-forwards.
        [IO.File]::WriteAllText((Join-Path $packageDir 'idle-pkg.json'), '{"stage":"review-passed"}')
        [IO.File]::WriteAllText($lockPath, '')
        $r = & $sync -Preview
        & $expect ($r.Exit -eq 0 -and $r.Text -match 'DRY RUN' -and $r.Text -match 'fast-forward') "-SyncMain -DryRun did not report the fast-forward: exit $($r.Exit) $($r.Text)"
        & $unchanged '-SyncMain -DryRun'
        $r = & $sync
        $after = & $git $main @('rev-parse', 'HEAD')
        & $expect ($r.Exit -eq 0 -and $after -eq $v2 -and $after -eq (& $git $main @('rev-parse', 'refs/remotes/origin/master')) -and ([IO.File]::ReadAllText((Join-Path $main 'tracked.txt'))) -eq "v2`n") "-SyncMain did not fast-forward HEAD to origin/master: exit $($r.Exit) $($r.Text)"
        $r = & $sync
        & $expect ($r.Exit -eq 0 -and (& $git $main @('rev-parse', 'HEAD')) -eq $v2) "a second -SyncMain at origin/master failed: exit $($r.Exit) $($r.Text)"

        # The exclusive sync lock refuses a live shared package holder and succeeds once that child releases it.
        $commonDir = (& $git $main @('rev-parse', '--path-format=absolute', '--git-common-dir'))
        $syncLock = Join-Path $commonDir 'selftest/main-sync.lock'
        $holderScript = Join-Path $root 'main-sync-lock-holder.ps1'
        $readyPath = Join-Path $root 'main-sync-lock-holder.ready'
        [IO.File]::WriteAllText($holderScript, @'
param([string]$LockPath, [string]$ReadyPath)
$stream = [IO.File]::Open($LockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
[IO.File]::WriteAllText($ReadyPath, 'ready')
Start-Sleep -Seconds 60
'@)
        # origin/master moves ahead (v3), so the sync after the holder exits has real work to do.
        [IO.File]::WriteAllText((Join-Path $other 'tracked.txt'), "v3`n")
        & $git $other @('commit', '-q', '-am', 'v3') | Out-Null
        & $git $other @('push', '-q', 'origin', 'master') | Out-Null
        $v3 = & $git $other @('rev-parse', 'HEAD')
        $holder = Start-Process -FilePath (Get-Command pwsh -CommandType Application | Select-Object -First 1).Source -ArgumentList @('-NoProfile', '-File', "`"$holderScript`"", "`"$syncLock`"", "`"$readyPath`"") -PassThru -NoNewWindow
        try
        {
            $waitUntil = [DateTime]::UtcNow.AddSeconds(10)
            while (-not (Test-Path -LiteralPath $readyPath) -and [DateTime]::UtcNow -lt $waitUntil) { Start-Sleep -Milliseconds 50 }
            $r = & $sync
            & $expect ($r.Exit -eq 3 -and $r.Text -match 'main-sync\.lock' -and $r.Text -match 'package run or review is active' -and (& $git $main @('rev-parse', 'HEAD')) -eq $v2) "-SyncMain did not refuse a child holding the shared lock with exit 3 and the lock named, or moved HEAD: exit $($r.Exit) $($r.Text)"
        }
        finally { Stop-Process -Id $holder.Id -Force -ErrorAction SilentlyContinue }
        $r = & $sync
        & $expect ($r.Exit -eq 0 -and $r.Text -match 'Main checkout is at origin/master' -and (& $git $main @('rev-parse', 'HEAD')) -eq $v3 -and (& $git $main @('rev-parse', 'HEAD')) -eq (& $git $main @('rev-parse', 'refs/remotes/origin/master'))) "-SyncMain did not fast-forward to origin/master after the shared child exited: exit $($r.Exit) $($r.Text)"
    }
    catch { $Failures.Add("main checkout self-test failed: $($_.Exception.Message)") }
    finally
    {
        if ($heldLock) { $heldLock.Dispose() }
        Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
    }
    $script:mainCheckoutCount
}

# ------------------------------------------------------------------------- self-test

function Invoke-SelfTest
{
    $fixtureDir = Join-Path $PSScriptRoot '..' 'fixtures' 'gates' 'merge-pr'
    $script:SelfTestPinFiles = @('vendor/pin/lock.json', 'vendor/pin/manifest.json')
    $script:ReviewSettings = [pscustomobject]@{ Namespace = 'selftest'; BaseRef = 'origin/master'; PinFiles = $script:SelfTestPinFiles; PinHook = 'hooks/pin.ps1' }
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
    $receiptDir = Join-Path $fixtureDir 'receipts'
    foreach ($file in @(Get-ChildItem -LiteralPath $receiptDir -Filter '*.json' -File | Sort-Object Name))
    {
        $fx = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
        $got = Test-ReceiptFixture $fx
        $count++
        $expectReason = if ($fx.expect.PSObject.Properties['reason']) { [string]$fx.expect.reason } else { '' }
        if ($got.Ok -ne [bool]$fx.expect.ok -or ($expectReason -and $got.Reason -notlike "*$expectReason*"))
        { $failures.Add("receipt/$($file.Name): expected $($fx.expect.ok) '$expectReason', got $($got.Ok) '$($got.Reason)'") }
    }
    $pinOnlyDir = Join-Path $fixtureDir 'pin-only'
    foreach ($file in @(Get-ChildItem -LiteralPath $pinOnlyDir -Filter '*.json' -File | Sort-Object Name))
    {
        $fx = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
        $got = Test-PinOnlyChange $fx.receipt ([string]$fx.verifiedCommitTree) ([bool]$fx.isAncestor) ([string]$fx.headTree) ([string]$fx.currentTree) @($fx.changed) $script:SelfTestPinFiles
        $count++
        $expectReason = if ($fx.expect.PSObject.Properties['reason']) { [string]$fx.expect.reason } else { '' }
        if ($got.Ok -ne [bool]$fx.expect.ok -or ($expectReason -and $got.Reason -notlike "*$expectReason*"))
        {
            $failures.Add("pin-only/$($file.Name): expected $($fx.expect.ok) '$expectReason', got $($got.Ok) '$($got.Reason)'")
        }
    }
    $reviewDir = Join-Path $fixtureDir 'reviews'
    foreach ($file in @(Get-ChildItem -LiteralPath $reviewDir -Filter '*.json' -File | Sort-Object Name))
    {
        $fx = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
        $record = if ($fx.PSObject.Properties['record']) { $fx.record } else { $null }
        if ($fx.PSObject.Properties['mode'] -and $fx.mode -in @('pin-only-descendant', 'non-pin-descendant'))
        {
            $receipt = [pscustomobject]@{ status = 'pass'; complete = $true; filter = $null; head = 'head-a'; treeHash = 'tree-a' }
            $paths = if ($fx.mode -eq 'pin-only-descendant') { @('vendor/pin/lock.json') } else { @('src/change.cs') }
            $pinDecision = Test-PinOnlyChange $receipt 'tree-a' $true 'tree-b' 'tree-b' $paths $script:SelfTestPinFiles
            $got = [pscustomobject]@{ Ok = $pinDecision.Ok; Reason = $pinDecision.Reason }
        }
        else { $got = Test-ReviewRecordObject $record 'head-a' 'tree-a' $true }
        $count++
        if ($got.Ok -ne [bool]$fx.expect.ok) { $failures.Add("reviews/$($file.Name): expected $($fx.expect.ok), got $($got.Ok) '$($got.Reason)'") }
    }
    $strictSchema = [pscustomobject]@{ schema='1'; headSha='head-a'; treeSha='tree-a'; verdict='PASS'; blockingFindings=0; executor='codex:gpt-6-luna'; reviewer='claude:gpt-6-luna'; evidence='present' }
    $strictSchemaResult = Test-ReviewRecordObject $strictSchema 'head-a' 'tree-a' $true
    $count++
    if ($strictSchemaResult.Ok -or $strictSchemaResult.Reason -notmatch 'wrong schema') { $failures.Add('string schema 1 was accepted') }
    $sameModel = [pscustomobject]@{ schema=1; headSha='head-a'; treeSha='tree-a'; verdict='PASS'; blockingFindings=0; executor='codex:gpt-6-luna'; reviewer='claude:gpt-6-luna'; evidence='present' }
    $sameModelResult = Test-ReviewRecordObject $sameModel 'head-a' 'tree-a' $true
    $count++
    if ($sameModelResult.Ok -or $sameModelResult.Reason -notmatch 'same') { $failures.Add('same-model cross-provider review was accepted') }
    $tempRepo = Join-Path ([IO.Path]::GetTempPath()) "merge-review-e2e-$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $tempRepo | Out-Null
    try
    {
        & git -C $tempRepo init -q
        & git -C $tempRepo -c user.name=SelfTest -c user.email=selftest@example.invalid config core.autocrlf false
        $pinA = Join-Path $tempRepo 'vendor/pin/lock.json'; New-Item -ItemType Directory -Path (Split-Path $pinA) -Force | Out-Null
        [IO.File]::WriteAllText($pinA, '{"version":"1"}')
        $evidence = Join-Path $tempRepo 'evidence.md'; [IO.File]::WriteAllText($evidence, 'offline evidence')
        & git -C $tempRepo add .; & git -C $tempRepo -c user.name=SelfTest -c user.email=selftest@example.invalid commit -qm initial
        $base = (& git -C $tempRepo rev-parse HEAD).Trim(); $baseTree = (& git -C $tempRepo rev-parse 'HEAD^{tree}').Trim()
        & git -C $tempRepo update-ref refs/remotes/origin/master HEAD
        $recordDir = Join-Path $tempRepo '.git/selftest/reviews'; New-Item -ItemType Directory -Path $recordDir -Force | Out-Null
        $normalRecord = [ordered]@{ schema=1; headSha=$base; treeSha=$baseTree; verdict='PASS'; blockingFindings=0; executor='codex:gpt-6-luna'; reviewer='claude:gpt-6.1-sol'; evidence=$evidence }
        [IO.File]::WriteAllText((Join-Path $recordDir "$base.json"), ($normalRecord | ConvertTo-Json -Depth 5))
        $normalExact = Test-MergeReview $tempRepo $base; $count++
        if (-not $normalExact.Ok) { $failures.Add("normal exact-head record refused: $($normalExact.Reason)") }
        [IO.File]::WriteAllText($pinA, '{"version":"2"}')
        & git -C $tempRepo add .; & git -C $tempRepo -c user.name=SelfTest -c user.email=selftest@example.invalid commit -qm pin
        $pinHead = (& git -C $tempRepo rev-parse HEAD).Trim()
        $normalPin = Test-MergeReview $tempRepo $pinHead; $count++
        if (-not $normalPin.Ok) { $failures.Add("normal record at pin-only descendant refused: $($normalPin.Reason)") }
        $noHook = [pscustomobject]@{ Namespace = 'selftest'; BaseRef = 'origin/master'; PinFiles = $script:SelfTestPinFiles; PinHook = $null }
        $noHookPin = Test-MergeReview $tempRepo $pinHead $noHook; $count++
        if ($noHookPin.Ok -or $noHookPin.Reason -notmatch 'hooks\.pin is not configured') { $failures.Add('a pin-only ancestor was accepted although hooks.pin is not configured') }
        & git -C $tempRepo checkout -qb small-fix $base
        [IO.File]::WriteAllText((Join-Path $tempRepo 'small.txt'), "small`n")
        & git -C $tempRepo add .; & git -C $tempRepo -c user.name=SelfTest -c user.email=selftest@example.invalid commit -qm small-fix
        $smallHead = (& git -C $tempRepo rev-parse HEAD).Trim(); $smallTree = (& git -C $tempRepo rev-parse 'HEAD^{tree}').Trim()
        $actualSmall = Get-ReviewChangeCount $tempRepo $base $smallHead $script:SelfTestPinFiles
        $smallRecord = [ordered]@{ schema=1; headSha=$smallHead; treeSha=$smallTree; verdict='PASS'; blockingFindings=0; executor='claude:coordinator'; reviewer='self'; reason='small-fix'; changedFiles=$actualSmall.Files; changedLines=$actualSmall.Lines; evidence=$evidence }
        [IO.File]::WriteAllText((Join-Path $recordDir "$smallHead.json"), ($smallRecord | ConvertTo-Json -Depth 5))
        $smallExact = Test-MergeReview $tempRepo $smallHead; $count++
        if (-not $smallExact.Ok) { $failures.Add("small-fix exact-head record refused: $($smallExact.Reason)") }
        [IO.File]::WriteAllText($pinA, '{"version":"3"}')
        & git -C $tempRepo add .; & git -C $tempRepo -c user.name=SelfTest -c user.email=selftest@example.invalid commit -qm pin-after-small-fix
        $smallPinHead = (& git -C $tempRepo rev-parse HEAD).Trim()
        $smallPin = Test-MergeReview $tempRepo $smallPinHead; $count++
        if (-not $smallPin.Ok) { $failures.Add("small-fix at pin-only descendant refused: $($smallPin.Reason)") }
        [IO.File]::WriteAllText((Join-Path $tempRepo 'other.txt'), 'non pin')
        & git -C $tempRepo add .; & git -C $tempRepo -c user.name=SelfTest -c user.email=selftest@example.invalid commit -qm change
        $otherHead = (& git -C $tempRepo rev-parse HEAD).Trim()
        $otherDecision = Test-MergeReview $tempRepo $otherHead; $count++
        if ($otherDecision.Ok) { $failures.Add('end-to-end non-pin descendant review was accepted') }
    }
    catch { $failures.Add("end-to-end merge review repository test failed: $($_.Exception.Message)") }
    finally { Remove-Item -LiteralPath $tempRepo -Recurse -Force -ErrorAction SilentlyContinue }
    $count += Test-MainCheckoutBehavior $failures
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

try
{
    $Context = Get-AwGateContext -Root $Root -Worktree $Worktree -ScriptRoot $PSScriptRoot
}
catch
{
    Write-Host "ERROR $($_.Exception.Message)"
    exit 2
}
$script:Ctx = $Context
$script:Remote = $Context.BaseRemote
$script:BaseBranchName = $Context.BaseBranch
$script:Namespace = [string]$Context.Config.state.commonDirNamespace
$script:MainCheckout = $Context.MainCheckout
$script:ReviewSettings = [pscustomobject]@{ Namespace = $script:Namespace; BaseRef = $Context.BaseRef; PinFiles = $Context.PinFiles; PinHook = $Context.Config.hooks.pin }
if (-not $PSBoundParameters.ContainsKey('Required'))
{
    $Required = @($Context.Config.merge.requiredChecks)
}
# gh and every other child process start in the main checkout, which the cleanup never removes; never in the caller's folder.
Set-Location -LiteralPath $Context.MainCheckout

if ($SyncMain)
{
    exit (Invoke-SyncMain $script:MainCheckout -Preview:$DryRun)
}

if (-not $DryRun -and -not $PinOnly -and -not $Worktree)
{
    Write-Host 'ERROR merging requires -Worktree.'
    exit 2
}

# ----------------------------------------------------------------------------- main

if (-not $Pr -and -not $PinOnly)
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

function Invoke-PinHook([string]$Action, [string]$WorktreePath)
{
    # hooks.pin: a repository script run as -Action Behind|Update|Validate -Root <worktree> (see the header).
    Invoke-AwHook -Context $script:Ctx -Name 'pin' -HookArgs @('-Action', $Action, '-Root', $WorktreePath) -WorkingDirectory $WorktreePath
}

function Get-PinHookMessage($Result)
{
    if ($Result.Lines.Count -gt 0) { return [string]$Result.Lines[-1] }
    if ($Result.Detail) { return [string]$Result.Detail }
    ([string]$Result.Errors).Trim()
}

function Update-PluginPin
{
    param([string]$WorktreePath)

    $root = (Resolve-Path -LiteralPath $WorktreePath).Path
    $hook = Invoke-PinHook 'Update' $root
    if (-not $hook.Configured)
    {
        Write-Host 'Plugin pin: not configured (hooks.pin is null in agent-workflow.json); nothing to pin.'
        return $false
    }
    if (-not $hook.Ran)
    {
        throw "Plugin pin: $($hook.Detail)"
    }
    if ($hook.ExitCode -notin 0, 10, 1) { throw (Get-PinHookMessage $hook) }
    foreach ($line in $hook.Lines) { Write-Host $line }
    if ($hook.ExitCode -eq 0) { return $false }
    if ($hook.ExitCode -eq 10) { return $true }
    exit 1
}

function Test-PluginPinBehind([string]$WorktreePath)
{
    $root = (Resolve-Path -LiteralPath $WorktreePath).Path
    $hook = Invoke-PinHook 'Behind' $root
    if (-not $hook.Configured) { return $false }
    if (-not $hook.Ran) { throw "Plugin pin: $($hook.Detail)" }
    if ($hook.ExitCode -eq 0) { return $false }
    if ($hook.ExitCode -eq 10) { return $true }
    throw (Get-PinHookMessage $hook)
}

function Get-VerdictPath([string]$RepositoryRoot)
{
    $settings = Get-AwSection $script:Ctx 'verify'
    [IO.Path]::GetFullPath((Join-Path $RepositoryRoot (Join-Path ([string](Get-AwSetting $settings 'outputDir' 'artifacts/verify')) 'verdict.json')))
}

function Test-ReceiptGates($Receipt)
{
    # A receipt must contain every gate the main checkout's configuration defines, each passed; with verify.doctor on, that
    # includes a passing doctor gate. A verdict made under a weaker configuration is refused.
    $names = @(Get-AwConfiguredGateNames $script:Ctx.Config)
    $byName = @{}
    if ($Receipt.PSObject.Properties['gates'])
    {
        foreach ($g in @($Receipt.gates)) { if ($g -and $g.PSObject.Properties['name']) { $byName[[string]$g.name] = $g } }
    }
    foreach ($name in $names)
    {
        if (-not $byName.ContainsKey($name)) { return [pscustomobject]@{ Ok = $false; Reason = "the receipt lacks the configured gate '$name' (it was made under a weaker configuration)" } }
        if ([string]$byName[$name].status -ne 'pass') { return [pscustomobject]@{ Ok = $false; Reason = "the receipt's gate '$name' did not pass (status '$($byName[$name].status)')" } }
    }
    [pscustomobject]@{ Ok = $true; Reason = 'every configured gate passed' }
}
function Test-MergeReceiptCore([string]$WorktreePath, [string]$PrHead)
{
    # The receipt must describe the PR head's tree, or an ancestor whose only later change is the pin files
    # (then the pin adapter, which validates those files offline, must pass too).
    $root = (Resolve-Path -LiteralPath $WorktreePath).Path
    $currentTree = Get-WorkingTreeHash $root
    $receipt = Read-VerifyReceipt (Get-VerdictPath $root)
    $exact = Test-VerifyReceiptObject $receipt $PrHead $currentTree
    if ($exact.Ok -or -not $receipt -or $exact.Reason -notmatch '^(head|tree) mismatch')
    {
        return $exact
    }

    # Content, not commit: a receipt for the committed tree of the head is as good as one made at the head itself when it was made under the
    # configuration and kit of the main checkout and by this verify tool (the same tree under another commit SHA: an amend, a rebase onto
    # an unchanged tree, a reused receipt). Anything it cannot prove falls through to the pin-only rule below.
    if ($exact.Reason -match '^head mismatch')
    {
        $headTreeNow = ([string](& git -C $root rev-parse "$PrHead^{tree}" 2>$null)).Trim()
        $verifyTool = Join-Path $PSScriptRoot 'verify.ps1'
        if ($headTreeNow -and (Test-Path -LiteralPath $verifyTool -PathType Leaf))
        {
            $content = Test-AwReceiptContent $receipt $headTreeNow $currentTree $script:Ctx.Config $script:Ctx.Root $verifyTool
            if ($content.Ok)
            {
                return $content
            }
        }
    }

    $verified = [string]$receipt.head
    $verifiedTree = [string](& git -C $root rev-parse "$verified^{tree}" 2>$null)
    & git -C $root merge-base --is-ancestor $verified $PrHead 2>$null
    $isAncestor = $LASTEXITCODE -eq 0
    $headTree = [string](& git -C $root rev-parse "$PrHead^{tree}" 2>$null)
    $changed = @(& git -C $root diff --name-only $verified $PrHead 2>$null)
    $pinOnly = Test-PinOnlyChange $receipt $verifiedTree.Trim() $isAncestor $headTree.Trim() $currentTree $changed $script:Ctx.PinFiles
    if (-not $pinOnly.Ok)
    {
        return $pinOnly
    }

    $validate = Invoke-PinHook 'Validate' $root
    if (-not $validate.Configured)
    {
        return [pscustomobject]@{ Ok = $false; Reason = 'pin-only change, but hooks.pin is not configured to validate the pin files' }
    }
    if (-not $validate.Ran -or $validate.ExitCode -ne 0)
    {
        return [pscustomobject]@{ Ok = $false; Reason = "pin-only change, but $(Get-PinHookMessage $validate)" }
    }
    $passed = if ($validate.Lines.Count -gt 0) { [string]$validate.Lines[-1] } else { 'pin check passed' }
    [pscustomobject]@{ Ok = $true; Reason = "$($pinOnly.Reason); $passed" }
}


function Test-MergeReceipt([string]$WorktreePath, [string]$PrHead)
{
    # What the pull request would put on the base branch includes the vendored kit and its lock: a candidate install never merges.
    $candidate = Get-AwCandidateLockRefusal (Resolve-Path -LiteralPath $WorktreePath).Path -Revision $PrHead
    if ($candidate) { return [pscustomobject]@{ Ok = $false; Reason = $candidate } }
    $core = Test-MergeReceiptCore $WorktreePath $PrHead
    if (-not $core.Ok) { return $core }
    $root = (Resolve-Path -LiteralPath $WorktreePath).Path
    $gates = Test-ReceiptGates (Read-VerifyReceipt (Get-VerdictPath $root))
    if (-not $gates.Ok) { return $gates }
    $core
}
function Push-PluginPin([string]$WorktreePath, [string]$PullRequest)
{
    # -Auto moves a stale pin itself: commit, push, and wait until GitHub reports the pushed commit as the PR head.
    $root = (Resolve-Path -LiteralPath $WorktreePath).Path
    if (-not (Update-PluginPin $root))
    {
        return
    }
    & git -C $root push -q 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0)
    {
        Write-Host 'ERROR pushing the plugin pin failed; not enabling auto-merge.'
        exit 1
    }
    $pushed = (& git -C $root rev-parse HEAD).Trim()
    for ($attempt = 0; $attempt -lt 30; $attempt++)
    {
        if ((& gh pr view $PullRequest --json headRefOid -q .headRefOid) -eq $pushed)
        {
            return
        }
        Start-Sleep -Seconds 2
    }
    Write-Host "ERROR the pull request head did not reach the pushed pin commit $pushed; not enabling auto-merge."
    exit 1
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
    if (-not $DryRun -and -not $Worktree) { Write-Host 'ERROR merging requires -Worktree.'; exit 2 }
    $prInfo = Get-PrJson
    if ($DryRun -and -not $Worktree) { Write-Host 'REVIEW REFUSED: -Worktree is required for merge; dry run continues to report the remaining PR decision.' }
    if ($Worktree -and -not $NoPluginPin -and $prInfo.state -eq 'OPEN' -and (Test-PluginPinBehind $WorktreeFull))
    {
        # A release landed after verify.ps1 ran; a pin-only commit does not need another full verify.
        Push-PluginPin $WorktreeFull $Pr
        $prInfo = Get-PrJson
    }
    if ($Worktree -and $prInfo.state -eq 'OPEN')
    {
        try { $receiptCheck = Test-MergeReceipt $WorktreeFull ([string]$prInfo.headRefOid) }
        catch { Write-Host "RECEIPT REFUSED: receipt check failed: $_"; exit 1 }
        if (-not $receiptCheck.Ok) { Write-Host "RECEIPT REFUSED: $($receiptCheck.Reason)"; exit 1 }
        Write-Host "Receipt: $($receiptCheck.Reason)"
    }
    if ($Worktree -and $prInfo.state -eq 'OPEN')
    {
        try { $reviewCheck = Test-MergeReview $WorktreeFull ([string]$prInfo.headRefOid) }
        catch { $reviewCheck = [pscustomobject]@{ Ok = $false; Reason = "review check failed: $_" } }
        if (-not $reviewCheck.Ok)
        {
            Write-Host "REVIEW REFUSED: $($reviewCheck.Reason)"
            Update-Status "auto-merge refused #$($prInfo.number)" '' $reviewCheck.Reason
            if (-not $DryRun) { exit 1 }
        }
        else { Write-Host "Review: $($reviewCheck.Reason)" }
    }
    $checks = if ($prInfo.state -eq 'OPEN') { Get-ChecksJson } else { @() }
    $decision = Get-AutoDecision $prInfo $checks $Required
    Write-Host "PR #$($prInfo.number) $($prInfo.headRefName) @ $($decision.Head): $($decision.Action) - $($decision.Reason)"
    if ($DryRun -and -not $Worktree)
    {
        $checks | ForEach-Object { Write-Host "  $($_.name): $($_.bucket)" }
        Write-Host 'DRY RUN: merge gate would refuse because no -Worktree was supplied.'
        exit 0
    }
    if ($decision.Action -eq 'Merged')
    {
        Write-Host 'Already merged; nothing to enable.'
        exit 0
    }
    if ($decision.Action -eq 'Conflict')
    {
        Update-Status "auto-merge refused #$($prInfo.number)" '' $decision.Reason
        exit 3
    }
    if ($decision.Action -in 'Fail', 'Closed')
    {
        Write-Host 'NOT ENABLING AUTO-MERGE.'
        Update-Status "auto-merge refused #$($prInfo.number)" '' $decision.Reason
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
    $mergeFailed = $LASTEXITCODE -ne 0
    $mergedAtOnce = $false
    if ($mergeFailed)
    {
        # With the checks already green GitHub merges at once, and `--delete-branch` then fails to delete the local branch because
        # the pull request worktree has it checked out: gh exits non-zero although the pull request is merged. Ask GitHub.
        $viewText = & gh pr view ([string]$prInfo.number) --json state,mergeCommit 2>&1
        $viewState = $null
        if ($LASTEXITCODE -eq 0) { try { $viewState = ($viewText -join "`n") | ConvertFrom-Json } catch { $viewState = $null } }
        if ($viewState -and [string]$viewState.state -eq 'MERGED')
        {
            $mergedAtOnce = $true
            $mergeSha = if ($viewState.PSObject.Properties['mergeCommit'] -and $viewState.mergeCommit) { [string]$viewState.mergeCommit.oid } else { '' }
            Write-Host "gh pr merge reported an error, but GitHub says pull request #$($prInfo.number) is MERGED: $(($out | Select-Object -Last 1))"
            Write-Host "MERGED $mergeSha"
        }
        else
        {
            Write-Host "ERROR enabling auto-merge failed: $out"
            exit 1
        }
    }
    else
    {
        $out | Select-Object -Last 3 | ForEach-Object { Write-Host $_ }
        Write-Host 'AUTO-MERGE ENABLED (not merged yet)'
    }
    if ($mergedAtOnce)
    {
        Update-Status "merged #$($prInfo.number)" $mergeSha ''
        if ($WaitMerged)
        {
            Write-Host 'WAIT state=MERGED pending=0 failed=0'
            Invoke-MergedCleanup ([string]$prInfo.headRefName)
        }
        else
        {
            Write-Host "Worktree '$Worktree' was NOT removed; clean it up after the merge (git worktree remove, git branch -D). Then bring the main checkout up to date with: $(Get-SyncMainCommand)"
        }
        exit 0
    }
    Update-Status "auto-merge enabled #$($prInfo.number)" ([string]$decision.Head) ''
    if ($Worktree)
    {
        Write-Host "Worktree '$Worktree' was NOT removed; clean it up after the merge (git worktree remove, git branch -D). Then bring the main checkout up to date with: $(Get-SyncMainCommand)"
    }
    if ($WaitMerged)
    {
        $deadline = (Get-Date).AddMinutes($TimeoutMinutes)
        while ($true)
        {
            Start-Sleep -Seconds $PollSeconds
            $waitPr = Get-PrJson
            if ($waitPr.state -eq 'MERGED')
            {
                Write-Host 'WAIT state=MERGED pending=0 failed=0'
                $mergeSha = [string](& gh pr view $waitPr.number --json mergeCommit -q .mergeCommit.oid)
                Write-Host "MERGED $mergeSha"
                Update-Status "merged #$($waitPr.number)" $mergeSha ''
                Invoke-MergedCleanup ([string]$waitPr.headRefName)
                exit 0
            }
            if ($waitPr.state -eq 'CLOSED') { Write-Host 'WAIT STOPPED: PR is CLOSED.'; Update-Status "wait stopped #$($waitPr.number)" '' 'PR is CLOSED'; exit 1 }
            $waitChecks = Get-ChecksJson
            $failedChecks = @($waitChecks | Where-Object { $_.bucket -in 'fail', 'cancel' })
            $pendingChecks = @($waitChecks | Where-Object { $_.bucket -eq 'pending' })
            Write-Host "WAIT state=$($waitPr.state) pending=$($pendingChecks.Count) failed=$($failedChecks.Count)"
            if ($failedChecks.Count -gt 0) { $reason = "failed/cancelled checks: $(($failedChecks | ForEach-Object { $_.name }) -join ', ')"; Write-Host "WAIT STOPPED: $reason"; Update-Status "wait stopped #$($waitPr.number)" '' $reason; exit 1 }
            if ((Get-Date) -ge $deadline) { $reason = "timeout after $TimeoutMinutes minutes"; Write-Host "WAIT STOPPED: $reason."; Update-Status "wait stopped #$($waitPr.number)" '' $reason; exit 1 }
        }
    }
    exit 0
}

if ($PinOnly)
{
    if (-not $Worktree) { Write-Host 'ERROR -PinOnly requires -Worktree.'; exit 2 }
    try { $changed = Update-PluginPin $WorktreeFull }
    catch { Write-Host "ERROR $_"; exit 1 }
    exit 0
}
if ($WaitMerged -and -not $Auto)
{
    Write-Host 'ERROR -WaitMerged requires -Auto.'
    exit 2
}

$deadline = (Get-Date).AddMinutes($TimeoutMinutes)
while ($true)
{
    $prInfo = Get-PrJson
    if ($DryRun -and -not $Worktree) { Write-Host 'REVIEW REFUSED: -Worktree is required for merge; dry run continues to report the remaining PR decision.' }
    $checks = if ($prInfo.state -eq 'OPEN') { Get-ChecksJson } else { @() }
    $decision = Get-MergeDecision $prInfo $checks $Required
    if ($DryRun -and -not $Worktree)
    {
        Write-Host "PR #$($prInfo.number) $($prInfo.headRefName) @ $($decision.Head): $($decision.Action) - $($decision.Reason)"
        $checks | ForEach-Object { Write-Host "  $($_.name): $($_.bucket)" }
        Write-Host 'DRY RUN: merge gate would refuse because no -Worktree was supplied.'
        exit 0
    }
    if ($Worktree -and $prInfo.state -eq 'OPEN')
    {
        try { $receiptCheck = Test-MergeReceipt $WorktreeFull ([string]$prInfo.headRefOid) }
        catch { Write-Host "RECEIPT REFUSED: receipt check failed: $_"; exit 1 }
        if (-not $receiptCheck.Ok) { Write-Host "RECEIPT REFUSED: $($receiptCheck.Reason)"; exit 1 }
    }
    if ($Worktree -and $prInfo.state -eq 'OPEN')
    {
        try { $reviewCheck = Test-MergeReview $WorktreeFull ([string]$prInfo.headRefOid) }
        catch { $reviewCheck = [pscustomobject]@{ Ok = $false; Reason = "review check failed: $_" } }
        if (-not $reviewCheck.Ok)
        {
            Write-Host "REVIEW REFUSED: $($reviewCheck.Reason)"
            Update-Status "merge refused #$($prInfo.number)" '' $reviewCheck.Reason
            if (-not $DryRun) { exit 1 }
        }
        else { Write-Host "Review: $($reviewCheck.Reason)" }
    }
    Write-Host "PR #$($prInfo.number) $($prInfo.headRefName) @ $($decision.Head): $($decision.Action) - $($decision.Reason)"

    if ($decision.Action -eq 'Conflict')
    {
        Update-Status "merge refused #$($prInfo.number)" '' $decision.Reason
        exit 3
    }
    if ($decision.Action -eq 'Fail')
    {
        Write-Host 'NOT MERGING: a check failed.'
        Update-Status "merge refused #$($prInfo.number)" '' $decision.Reason
        exit 1
    }
    if ($decision.Action -eq 'Closed')
    {
        Update-Status "merge refused #$($prInfo.number)" '' $decision.Reason
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
    Update-Status "merge refused #$($prInfo.number)" '' "PR state is $state after merge attempt"
    exit 1
}

$mergeSha = [string](& gh pr view $prInfo.number --json mergeCommit -q .mergeCommit.oid)
Update-Status "merged #$($prInfo.number)" $mergeSha ''

Invoke-MergedCleanup ([string]$prInfo.headRefName)
exit 0
