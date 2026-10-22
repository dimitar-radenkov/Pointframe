# ---- commit -----------------------------------------------------------------------------------------------------

function Invoke-BaseRebase($Info, [string]$Slug, [string]$Wt, [string]$BaseRef, [string]$FailEvent)
{
    # The caller fetched and holds the package lock. A failed rebase is aborted and reported truthfully, including an abort that fails too.
    $preTree = Get-GitTree $Wt
    $baseNow = Invoke-Git $Wt @('rev-parse', '--verify', "$BaseRef^{commit}")
    $rebaseOutput = @(& git -C $Wt rebase $BaseRef 2>&1 | ForEach-Object { "$_" })
    if ($LASTEXITCODE -ne 0)
    {
        $abortOutput = @(& git -C $Wt rebase --abort 2>&1 | ForEach-Object { "$_" })
        $abortExit = $LASTEXITCODE
        $abortNote = if ($abortExit -eq 0) { 'the rebase was aborted; the commit stays on the branch and nothing else changed' } else { "rebase --abort FAILED (exit ${abortExit}: $(($abortOutput | Select-Object -Last 2) -join ' ')); the worktree may still be in the middle of a rebase and needs the coordinator" }
        Update-State $Info.StateRoot $Slug $FailEvent { param($s) $s.lastError = "rebase onto $BaseRef failed; abort exit $abortExit" } -Locked | Out-Null
        throw "rebase onto $BaseRef failed ($abortNote): $(($rebaseOutput | Select-Object -Last 3) -join ' ')"
    }
    @{ preTree = $preTree; baseNow = $baseNow }
}

function Invoke-Commit
{
    $info = Get-RepoInfo $Repo
    $stateLock = Enter-StateLock $info.StateRoot $Id
    try
    {
        Sync-Invalidation $info $Id
        $state = Read-State $info.StateRoot $Id
        Assert-StateConsistent $state $info
        $problems = @(Test-SnapshotsIntact $state)
        if ($problems.Count) { throw ($problems -join '; ') }
        if ([string]$state.stage -ne 'checks-passed') { throw "commit is not allowed in stage '$($state.stage)': it needs 'checks-passed' (run check first)" }
        if ([bool]$state.guard.mainCheckoutMutated) { throw 'the main-checkout guard is not clean; nothing is committed' }
        $wt = [string]$state.worktree
        $content = Get-TreeHash $wt
        $stale = Get-CheckFreshnessProblem $state $content
        if ($stale) { throw "commit needs a fresh passing check for the current tree: $stale" }
        $name = ([string](& git -C $wt config user.name)).Trim()
        $email = ([string](& git -C $wt config user.email)).Trim()
        if (-not $name -or -not $email) { throw 'git user.name and user.email are not configured: the commit author is the configured git user' }
        $runs = @(Get-List $state.runs)
        if ($runs.Count -eq 0) { throw 'no executor run is recorded for this package' }
        # Every run since the base that could have touched the worktree is an executor of what is committed: failed, timed-out
        # and mixed runs included. Only a run that never started is left out.
        $executorRuns = @($runs | Where-Object { (Get-Field $_ 'termination') -ne 'launch-failed' })
        if ($executorRuns.Count -eq 0) { throw 'no executor run that started is recorded for this package' }
        $executorSet = @($executorRuns | ForEach-Object { @{ runId = [string]$_.runId; provider = [string]$_.provider; model = [string]$_.model; executorKind = [string](Get-Field $_ 'executorKind'); outcome = [string]$_.outcome } })
        $runIds = @($executorSet | ForEach-Object { $_.runId }) -join ' '
        $dirty = -not (Test-WorktreeClean $wt)
        $ahead = [int](Invoke-Git $wt @('rev-list', '--count', "$($state.baseSha)..HEAD"))
        if (-not $dirty -and $ahead -eq 0) { throw 'nothing to commit: the worktree is clean and the branch has no commit beyond its base' }
        $summary = if ($Message -and $Message.Trim()) { $Message.Trim() } else { "Package $($state.slug)" }
        if ($dirty)
        {
            $paths = Get-PackagePaths $info.StateRoot $Id
            $messageFile = Join-Path $paths.Dir 'commit-message.txt'
            [IO.File]::WriteAllText($messageFile, "$summary`n`nPackage: $($state.slug) run $runIds`n", [Text.UTF8Encoding]::new($false))
            Invoke-Git $wt @('add', '-A') | Out-Null
            Invoke-Git $wt @('commit', '-F', $messageFile) | Out-Null
        }
        $remote = Get-PackageRemote ([string]$state.baseRef)
        if ($remote) { Invoke-Git $wt @('fetch', $remote) | Out-Null }
        $rebased = Invoke-BaseRebase $info $Id $wt ([string]$state.baseRef) 'commit-rebase-failed'
        $preTree = $rebased.preTree
        $baseNow = $rebased.baseNow
        $sha = Get-GitHead $wt
        $tree = Get-GitTree $wt
        $treeChanged = $tree -ne $preTree
        $contentNow = Get-TreeHash $wt
        $entry = @{ sha = $sha; tree = $tree; contentTree = $contentNow; utc = (Get-Now); message = $summary; runIds = @($executorSet | ForEach-Object { $_.runId }); executors = $executorSet; baseSha = $baseNow; rebaseChangedTree = $treeChanged; checkedAfterRebase = (-not $treeChanged); author = "$name <$email>" }
        Update-State $info.StateRoot $Id 'committed' {
            param($s)
            $s.commit = $entry
            $s.commits = @(Get-List $s.commits) + @($entry)
            $s.baseSha = $baseNow
            $s.stage = 'committed'
        } -Locked | Out-Null
        Write-Host "committed ${sha} on $($state.branch) (tree $tree, base $baseNow)$(if ($treeChanged) { ' - the rebase changed the tree' })"
        if ($treeChanged)
        {
            Write-Host 'the rebase changed the tree: re-running check'
            $ok = Invoke-CheckCore $info
            if (-not $ok) { Write-Host 'ERROR the check after the rebase failed; the stage is checks-failed'; return }
            if (-not (Test-WorktreeClean $wt)) { Write-Host 'NOTE the check changed files (format or CRLF); the stage is checks-passed: run commit again to commit them'; return }
            $afterContent = Get-TreeHash $wt
            Update-State $info.StateRoot $Id 'committed-after-recheck' {
                param($s)
                $s.commit.checkedAfterRebase = $true
                $s.commit.contentTree = $afterContent
                $s.commits[-1] = $s.commit
                $s.stage = 'committed'
            } -Locked | Out-Null
        }
    }
    finally { $stateLock.Dispose() }
}

