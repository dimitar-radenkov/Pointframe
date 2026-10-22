# ---- recover: the audited way out of blocked-main-mutated ------------------------------------------------------------

function Get-StageAfterLastRun($State)
{
    # The stage a package is in once its last run has finished and nothing has been checked since: what 'run' itself leaves behind.
    $runs = @(Get-List $State.runs)
    if ($runs.Count -eq 0) { return 'ready' }
    $last = $runs[-1]
    if ([string](Get-Field $last 'termination') -eq 'kill-failed') { return 'executor-kill-failed' }
    $outcome = [string](Get-Field $last 'outcome')
    if ($outcome -in @('completed', 'impl-failed', 'env-blocked')) { return "executor-$outcome" }
    'executor-impl-failed'
}

function Invoke-Recover
{
    if ([string]::IsNullOrWhiteSpace($Reason)) { throw 'recover needs -Reason: what was found in the main checkout and what was done about it' }
    $info = Get-RepoInfo $Repo
    $stateLock = Enter-StateLock $info.StateRoot $Id
    try
    {
        $state = Read-State $info.StateRoot $Id
        if ([string]$state.stage -ne 'blocked-main-mutated') { throw "recover is only for stage 'blocked-main-mutated' (the main-checkout guard), not '$($state.stage)'" }
        $mainCheckout = [string]$state.repository.mainCheckout
        # The main checkout as it is now, recorded and never reverted: its fingerprint (or that it cannot be read) and what git reports as changed.
        $fingerprint = Get-Fingerprint $mainCheckout
        $mainStatus = @(& git -C $mainCheckout status --porcelain --untracked-files=all 2>&1 | ForEach-Object { "$_" } | Where-Object { $_ } | Select-Object -First 50)
        $toStage = Get-StageAfterLastRun $state
        $when = Get-Now
        $why = $Reason.Trim()
        $guardBefore = $state.guard
        $counts = @{ checks = @(Get-List $state.checks).Count; reviewPasses = @(Get-List $state.reviews | Where-Object { $_.status -eq 'valid' -and $_.verdict -eq 'PASS' }).Count; acceptance = [bool]$state.acceptance }
        $entry = @{
            utc = $when; reason = $why; fromStage = 'blocked-main-mutated'; toStage = $toStage
            guard = @{ lastRunId = (Get-Field $guardBefore 'lastRunId'); before = (Get-Field $guardBefore 'before'); after = (Get-Field $guardBefore 'after') }
            mainCheckout = @{ path = $mainCheckout; head = (Get-Field $fingerprint 'head'); treeHash = (Get-Field $fingerprint 'treeHash'); files = (Get-Field $fingerprint 'files'); unknown = [bool](Get-Field $fingerprint 'unknown'); unreadable = @(Get-List (Get-Field $fingerprint 'unreadable')) }
            mainStatus = @($mainStatus); invalidated = $counts
        }
        $mark = @{ utc = $when; reason = "recover: $why" }
        # A review record that merge-pr.ps1 would read for one of the package's commits must not outlive the acceptance it was written for: it is moved
        # to <common dir>/<namespace>/reviews/invalidated/, where merge-pr.ps1 never looks, and the move is recorded in the recovery entry.
        $reviewsDir = Join-Path $info.CommonDir "$($script:Namespace)/reviews"
        $invalidatedDir = Join-Path $reviewsDir 'invalidated'
        $shas = [Collections.Generic.List[string]]::new()
        foreach ($candidate in @((Get-Field $state.commit 'sha'), (Get-Field $state.acceptance 'commit'), (Get-Field $state.acceptance 'head')) + @(Get-List $state.commits | ForEach-Object { Get-Field $_ 'sha' }) + @(Get-List $state.reviews | ForEach-Object { Get-Field $_ 'commit' }))
        {
            $text = ([string]$candidate).Trim().ToLowerInvariant()
            if ($text -match '^[0-9a-f]{40,64}$' -and -not $shas.Contains($text)) { $shas.Add($text) }
        }
        $movedRecords = [Collections.Generic.List[object]]::new()
        try
        {
            foreach ($sha in $shas)
            {
                $source = Join-Path $reviewsDir "$sha.json"
                if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { continue }
                New-Item -ItemType Directory -Path $invalidatedDir -Force | Out-Null
                $target = Join-Path $invalidatedDir "$sha.json"
                if (Test-Path -LiteralPath $target) { $target = Join-Path $invalidatedDir "$sha.$([DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfff')).json" }
                $recordSha256 = Get-Sha256 $source
                Move-Item -LiteralPath $source -Destination $target
                $movedRecords.Add(@{ commit = $sha; from = $source; to = $target; sha256 = $recordSha256 })
            }
            $entry.reviewRecords = @($movedRecords)
            Update-State $info.StateRoot $Id 'recovered' {
                param($s)
                $s.recoveries = @(Get-List $s.recoveries) + @($entry)
                $s.checks = @(Get-List $s.checks | ForEach-Object { if (-not (Get-Field $_ 'invalidated')) { $_['invalidated'] = $mark }; $_ })
                $s.reviews = @(Get-List $s.reviews | ForEach-Object { if ($_.status -eq 'valid' -and $_.verdict -eq 'PASS' -and -not (Get-Field $_ 'invalidated')) { $_['invalidated'] = $mark }; $_ })
                if ($s.acceptance -and -not $s.acceptance.invalidated) { $s.acceptance.invalidated = $mark }
                # The guard is cleared here, in the open: the flag goes down, what it saw stays, and the recovery entry holds the reason.
                $s.guard = @{ mainCheckoutMutated = $false; lastRunId = (Get-Field $guardBefore 'lastRunId'); before = (Get-Field $guardBefore 'before'); after = (Get-Field $guardBefore 'after'); recoveredUtc = $when }
                $s.stage = $toStage
            } -Locked | Out-Null
        }
        catch
        {
            # The state was not written: put the records back, so that nothing was half done.
            foreach ($moved in $movedRecords) { if (Test-Path -LiteralPath $moved.to) { Move-Item -LiteralPath $moved.to -Destination $moved.from -Force -ErrorAction SilentlyContinue } }
            throw
        }
        Write-Host "recovered: the main-checkout guard is cleared on your word ($why). Recorded: the main checkout now ($(if ($entry.mainCheckout.unknown) { 'cannot be fingerprinted' } else { "HEAD $($entry.mainCheckout.head), tree $($entry.mainCheckout.treeHash)" }), $($mainStatus.Count) changed path(s) in git status), and $($counts.checks) check(s), $($counts.reviewPasses) review PASS(es)$(if ($counts.acceptance) { ' and the acceptance' }) invalidated$(if ($movedRecords.Count) { "; $($movedRecords.Count) review record(s) moved to $invalidatedDir" })."
        Write-Host "stage is now $toStage; nothing was reverted. Next: $((Get-NextTransitions (Read-State $info.StateRoot $Id)) -join ', ')"
    }
    finally { $stateLock.Dispose() }
}

