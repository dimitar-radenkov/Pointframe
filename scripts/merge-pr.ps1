#Requires -Version 7.0
<#
Waits for a pull request's checks, squash-merges the exact head that was checked, then cleans up. For agents.

  pwsh scripts/merge-pr.ps1 -Pr 190                          # PR number or branch name
  pwsh scripts/merge-pr.ps1 -Pr 190 -Worktree ..\Pointframe-wt-x   # also remove that worktree after the merge
  pwsh scripts/merge-pr.ps1 -Pr 190 -DryRun                  # evaluate once and report; never merges or deletes
  pwsh scripts/merge-pr.ps1 -PinOnly -Worktree <path>        # pin plugin locally before verify; commit work -> PinOnly -> verify -> push + gh pr create -> -Auto
  pwsh scripts/merge-pr.ps1 -Pr 190 -Auto                    # enable GitHub auto-merge (squash, delete branch) and return at once
  pwsh scripts/merge-pr.ps1 -Pr 190 -Auto -WaitMerged        # enable, wait, and clean up after merge
  pwsh scripts/merge-pr.ps1 -Pr 190 -Required unit-tests,CodeQL -TimeoutMinutes 20 -PollSeconds 15
  pwsh scripts/merge-pr.ps1 -SelfTest                        # offline: decision logic on scripts/tests/merge-pr fixtures

Rules, each from a past incident:
  - Every name in -Required (default unit-tests, package-checks, CodeQL, Analyze (csharp)) must be PRESENT in `gh pr checks`, and no
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
  - Pin before verify: commit work -> -PinOnly -Worktree -> verify.ps1 -> push + gh pr create -> -Auto -Worktree.
    Auto requires a complete passing receipt for the PR head and current worktree. Use -NoReceipt only for non-worktree PRs.

Exit codes: 0 merged and cleaned up (or -DryRun finished, or -Auto enabled auto-merge / found the PR merged), 1 check failed, timeout, closed PR, or merge did not
complete, 2 gh missing or not authenticated or bad arguments, 3 merge conflict, 4 pull after merge failed.
#>
[CmdletBinding()]
param(
    [string]$Pr,
    [string]$Worktree,
    [string[]]$Required = @('unit-tests', 'package-checks', 'CodeQL', 'Analyze (csharp)'),
    [int]$TimeoutMinutes = 40,
    [int]$PollSeconds = 30,
    [switch]$DryRun,
    [switch]$Auto,
    [switch]$NoPluginPin,
    [switch]$PinOnly,
    [switch]$NoReceipt,
    [switch]$WaitMerged,
    [string]$StatusId,
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'lib/tree-hash.ps1')

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
    & pwsh @statusArguments
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

function Update-PluginPin
{
    param([string]$WorktreePath)

    $root = (Resolve-Path -LiteralPath $WorktreePath).Path
    $lockPath = Join-Path $root 'plugin' 'pointframe' 'server.lock.json'
    $pinScript = Join-Path $root 'scripts' 'update-plugin-pin.ps1'
    if (-not (Test-Path -LiteralPath $lockPath) -or -not (Test-Path -LiteralPath $pinScript))
    {
        throw "Plugin pin files are missing from '$root'."
    }

    $latest = (gh release view --json tagName -q .tagName 2>$null)
    if ($LASTEXITCODE -ne 0 -or -not $latest)
    {
        throw 'Plugin pin: could not read the latest release.'
    }

    $latest = $latest.Trim() -replace '^v', ''
    $pinned = (Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json).version
    if ($pinned -eq $latest)
    {
        Write-Host "Plugin pin: already v$latest"
        return $false
    }

    Write-Host "Plugin pin: v$pinned -> v$latest."
    & pwsh -NoProfile -File $pinScript -Version $latest | Out-Host
    if ($LASTEXITCODE -ne 0)
    {
        Write-Host "ERROR plugin pin update failed (exit $LASTEXITCODE); not enabling auto-merge."
        exit 1
    }

    git -C $root add -- plugin/pointframe/server.lock.json plugin/pointframe/.claude-plugin/plugin.json
    git -C $root commit -q -m "Pin the Claude plugin to v$latest" -- plugin/pointframe/server.lock.json plugin/pointframe/.claude-plugin/plugin.json
    if ($LASTEXITCODE -ne 0)
    {
        Write-Host 'ERROR committing the plugin pin failed.'
        exit 1
    }
    $commit = (git -C $root rev-parse HEAD).Trim()
    Write-Host "Plugin pin: committed $commit"
    return $true
}

function Test-PluginPinBehind([string]$WorktreePath)
{
    $root = (Resolve-Path -LiteralPath $WorktreePath).Path
    $lockPath = Join-Path $root 'plugin' 'pointframe' 'server.lock.json'
    if (-not (Test-Path -LiteralPath $lockPath)) { return $false }
    $latest = (& gh release view --json tagName -q .tagName 2>$null)
    if ($LASTEXITCODE -ne 0 -or -not $latest) { throw 'Plugin pin: could not read the latest release.' }
    $latest = $latest.Trim() -replace '^v', ''
    $pinned = [string](Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json).version
    return ([version]$pinned -lt [version]$latest)
}

function Invoke-MergedCleanup([string]$Branch)
{
    # Only after GitHub reported MERGED: remove the worktree and both branches, then fast-forward the main tree.
    $mainTree = ((& git worktree list --porcelain | Select-Object -First 1) -replace '^worktree ', '')
    if ($Worktree -and (Test-Path -LiteralPath $Worktree))
    {
        $removed = & git -C $mainTree worktree remove $Worktree 2>&1
        if ($LASTEXITCODE -ne 0)
        {
            Write-Host "WARNING worktree not removed (uncommitted changes? still in use?): $removed"
        }
    }
    & git -C $mainTree push origin --delete $Branch 2>&1 | Select-Object -Last 1 | ForEach-Object { Write-Host $_ }
    & git -C $mainTree branch -D $Branch 2>&1 | Select-Object -Last 1 | ForEach-Object { Write-Host $_ }

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
    if ($Worktree -and -not $NoReceipt -and $prInfo.state -eq 'OPEN')
    {
        try { $currentTree = Get-WorkingTreeHash (Resolve-Path -LiteralPath $Worktree).Path }
        catch { Write-Host "RECEIPT REFUSED: tree hash failed: $_"; exit 1 }
        $receiptPath = Join-Path $Worktree 'artifacts' 'verify' 'verdict.json'
        $receiptCheck = Test-VerifyReceiptObject (Read-VerifyReceipt $receiptPath) ([string]$prInfo.headRefOid) $currentTree
        if (-not $receiptCheck.Ok) { Write-Host "RECEIPT REFUSED: $($receiptCheck.Reason)"; exit 1 }
    }
    elseif ($NoReceipt) { Write-Host 'WARNING: -NoReceipt skips the verify receipt gate.' }
    if ($Worktree -and -not $NoPluginPin -and $prInfo.state -eq 'OPEN' -and (Test-PluginPinBehind $Worktree))
    { Write-Host 'plugin pin is behind: run -PinOnly, then verify.ps1 again'; exit 1 }
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
    if ($LASTEXITCODE -ne 0)
    {
        Write-Host "ERROR enabling auto-merge failed: $out"
        exit 1
    }
    $out | Select-Object -Last 3 | ForEach-Object { Write-Host $_ }
    Write-Host 'AUTO-MERGE ENABLED (not merged yet)'
    Update-Status "auto-merge enabled #$($prInfo.number)" ([string]$decision.Head) ''
    if ($Worktree)
    {
        Write-Host "Worktree '$Worktree' was NOT removed; clean it up after the merge (git worktree remove, git branch -D, git pull --ff-only)."
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
    try { $changed = Update-PluginPin $Worktree }
    catch { Write-Host "ERROR $_"; exit 1 }
    exit 0
}
if ($WaitMerged -and -not $Auto)
{
    Write-Host 'ERROR -WaitMerged requires -Auto.'
    exit 2
}

$deadline = (Get-Date).AddMinutes($TimeoutMinutes)
if ($NoReceipt) { Write-Host 'WARNING: -NoReceipt skips the verify receipt gate.' }
while ($true)
{
    $prInfo = Get-PrJson
    $checks = if ($prInfo.state -eq 'OPEN') { Get-ChecksJson } else { @() }
    $decision = Get-MergeDecision $prInfo $checks $Required
    if ($Worktree -and -not $NoReceipt -and $prInfo.state -eq 'OPEN')
    {
        try { $currentTree = Get-WorkingTreeHash (Resolve-Path -LiteralPath $Worktree).Path }
        catch { Write-Host "RECEIPT REFUSED: tree hash failed: $_"; exit 1 }
        $receiptCheck = Test-VerifyReceiptObject (Read-VerifyReceipt (Join-Path $Worktree 'artifacts' 'verify' 'verdict.json')) ([string]$prInfo.headRefOid) $currentTree
        if (-not $receiptCheck.Ok) { Write-Host "RECEIPT REFUSED: $($receiptCheck.Reason)"; exit 1 }
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
