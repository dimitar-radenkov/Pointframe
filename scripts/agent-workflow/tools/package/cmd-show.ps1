# ---- show -------------------------------------------------------------------------------------------------------

function Invoke-Show
{
    $info = Get-RepoInfo $Repo
    $state = Read-State $info.StateRoot $Id
    $integrity = @(Get-StateProblems $state $info)
    $wt = [string]$state.worktree
    # merge-pr.ps1 removes the worktree after the merge: a merged package has none, and that is not an inconsistency.
    $worktreeGone = ([string]$state.stage -eq 'merged' -and -not (Test-Path -LiteralPath $wt -PathType Container))
    if ($worktreeGone) { $integrity = @() }
    $currentTree = $null
    if ($integrity.Count -eq 0 -and -not $worktreeGone)
    {
        # An unreadable file makes the tree unknown, and an unknown tree cannot make any check fresh.
        try { $currentTree = Get-TreeHash $wt } catch { $integrity += @($_.Exception.Message) }
    }
    $snapshotProblems = @(Test-SnapshotsIntact $state)
    $plan = @()
    if ($snapshotProblems.Count -eq 0) { $plan = @(Get-CheckCommands (Read-Manifest $state.manifest.path)) }
    $checks = @($state.checks | ForEach-Object {
            $check = $_
            $evidence = @(if ($snapshotProblems.Count) { 'the manifest snapshot cannot be verified' } else { Get-CheckEvidenceProblems $check $plan })
            $stale = ($null -eq $currentTree -or $currentTree -ne $check.treeHash)
            [ordered]@{ checkId = $check.checkId; utc = $check.utc; ok = $check.ok; treeHash = $check.treeHash; stale = $stale; evidenceProblems = $evidence; fresh = (-not $stale -and $evidence.Count -eq 0)
                rawBytesProblems = @(Get-List (Get-Field $check 'rawBytesProblems')); invalidated = $(if (Get-Field $check 'invalidated') { Get-Field (Get-Field $check 'invalidated') 'reason' } else { $null })
                commands = @($check.commands | ForEach-Object { [ordered]@{ name = $_.name; exitCode = $_.exitCode; log = $_.log; inputTree = $_.inputTree; outputTree = $_.outputTree } }) }
        })
    $runs = @($state.runs | ForEach-Object {
            [ordered]@{ runId = $_.runId; startedUtc = $_.startedUtc; provider = $_.provider; executorKind = (Get-Field $_ 'executorKind'); model = $_.model; effort = $_.effort; codexCliVersion = $_.codexCliVersion; claudeCliVersion = (Get-Field $_ 'claudeCliVersion')
                authMode = (Get-Field $_ 'authMode'); permissionDenials = @(Get-Field $_ 'permissionDenials').Count; chainId = (Get-Field $_ 'chainId'); permissions = $_.permissions
                maxInputTokens = $_.maxInputTokens; tokens = $_.tokens; sessionId = $_.sessionId; exitCode = $_.exitCode; termination = $_.termination; outcome = $_.outcome; reason = $_.reason
                envBlocked = $_.envBlocked; mainCheckoutMutated = $_.mainCheckoutMutated }
        })
    $latestReview = Get-LatestReview $state
    $binding = $null
    if ($integrity.Count -eq 0 -and -not $worktreeGone -and $latestReview -and $latestReview.status -eq 'valid' -and $latestReview.verdict -eq 'PASS' -and -not (Get-Field $latestReview 'invalidated')) { $binding = Get-ReviewBinding $state $latestReview }
    $reviews = @(Get-List $state.reviews | ForEach-Object {
            [ordered]@{ reviewId = $_.reviewId; round = $_.round; level = $_.level; reviewer = $_.reviewer.kind; model = $_.reviewer.model; sessionId = $_.reviewer.sessionId; resumedFrom = $_.reviewer.resumedFrom; status = $_.status; verdict = $_.verdict
                commit = $_.commit; tree = (Get-Field $_ 'tree'); blockingFindings = $_.blockingFindings; invalidReasons = @($_.invalidReasons); verdictFile = $_.verdictFile.path; termination = $_.termination
                refunded = $(if (Get-Field $_ 'refunded') { Get-Field (Get-Field $_ 'refunded') 'reason' } else { $null }); refundDenied = $(if (Get-Field $_ 'refundDenied') { Get-Field (Get-Field $_ 'refundDenied') 'reason' } else { $null }); invalidated = $(if (Get-Field $_ 'invalidated') { Get-Field (Get-Field $_ 'invalidated') 'reason' } else { $null })
                findings = @(Get-List $_.findings | ForEach-Object { [ordered]@{ id = $_.id; severity = $_.severity; file = $_.file; line = $_.line; summary = $_.summary; disposition = $(if ($_.disposition) { "$($_.disposition.decision): $($_.disposition.reason)" } else { $null }) } }) }
        })
    $acceptance = $null
    if ($state.acceptance)
    {
        $acceptanceStatus = if ($state.acceptance.invalidated) { "invalidated: $($state.acceptance.invalidated.reason)" } elseif ($binding -and $binding.Current) { $(if ($binding.PinOnly) { 'valid (the head differs from the reviewed commit by the plugin pin files only)' } else { 'valid' }) } elseif ($binding) { "invalidated: $($binding.Reason)" } else { 'not verifiable' }
        $acceptance = [ordered]@{ utc = $state.acceptance.utc; reason = $state.acceptance.reason; reviewId = $state.acceptance.reviewId; commit = $state.acceptance.commit; status = $acceptanceStatus; recordPath = $state.acceptance.recordPath }
    }
    $view = [ordered]@{
        slug = $state.slug; packageId = $state.packageId; stage = $state.stage; worktree = $wt; branch = $state.branch; baseSha = $state.baseSha
        commit = $state.commit; repair = [ordered]@{ roundsUsed = $state.repair.roundsUsed; limit = $script:RepairLimit; resets = @(Get-List $state.repair.resets); refunds = @(Get-List $state.repair.refunds); refundLimit = $script:RefundLimit }; reviews = $reviews; acceptance = $acceptance
        recoveries = @(Get-List $state.recoveries | ForEach-Object { [ordered]@{ utc = $_.utc; reason = $_.reason; fromStage = $_.fromStage; toStage = $_.toStage; mainCheckout = $_.mainCheckout; mainStatus = @($_.mainStatus); invalidated = $_.invalidated; reviewRecords = @(Get-List (Get-Field $_ 'reviewRecords')) } })
        coordinatorFindings = @(Get-List $state.coordinatorFindings | ForEach-Object { [ordered]@{ utc = $_.utc; reviewId = $_.reviewId; path = $_.path; sha256 = $_.sha256; findings = @($_.findings) } })
        ship = (Get-ShipView $state)
        integrityProblems = $integrity; snapshotProblems = $snapshotProblems; runs = $runs; checks = $checks
        mainCheckoutGuard = [ordered]@{ mutated = [bool]$state.guard.mainCheckoutMutated; lastRunId = $state.guard.lastRunId }
        currentTreeHash = $currentTree; nextTransitions = @(Get-NextTransitions $state ($snapshotProblems.Count -eq 0 -and $integrity.Count -eq 0))
    }
    if ($Json) { $view | ConvertTo-Json -Depth 8; return }
    Write-Host "package $($state.slug)  stage=$($state.stage)"
    Write-Host "  worktree $wt  branch $($state.branch)  base $($state.baseSha)"
    if ($integrity.Count) { Write-Host "  INCONSISTENT STATE: $($integrity -join '; ')" }
    Write-Host "  snapshots $(if ($snapshotProblems.Count) { 'CHANGED: ' + ($snapshotProblems -join '; ') } else { 'intact' })"
    Write-Host "  runs: $($runs.Count)"
    foreach ($run in $runs) { Write-Host "    $($run.startedUtc) $($run.provider) $($run.model) $($run.effort) cli=$(if ($run.claudeCliVersion) { $run.claudeCliVersion } else { $run.codexCliVersion }) denials=$($run.permissionDenials) termination=$($run.termination) exit=$($run.exitCode) outcome=$($run.outcome) envBlocked=$($run.envBlocked) session=$($run.sessionId)" }
    Write-Host "  checks: $($checks.Count)"
    foreach ($check in $checks)
    {
        $freshness = if ($check.fresh) { 'fresh' } elseif ($check.stale) { 'STALE (the worktree changed after this check)' } else { "NOT EVIDENCE ($($check.evidenceProblems -join '; '))" }
        Write-Host "    $($check.utc) $(if ($check.ok) { 'passed' } else { 'FAILED' }) $freshness$(if ($check.rawBytesProblems.Count) { " RAW BYTES: $(($check.rawBytesProblems | Select-Object -First 10) -join ', ')" })"
    }
    if ($state.commit) { Write-Host "  commit: $($state.commit.sha) tree $($state.commit.tree) ($($state.commit.message))" }
    Write-Host "  reviews: $($reviews.Count), repair rounds used $($view.repair.roundsUsed) of $script:RepairLimit$(if ($view.repair.resets.Count) { ", $($view.repair.resets.Count) reset(s)" }), $($view.repair.refunds.Count) refund(s) of $($view.repair.refundLimit) allowed"
    foreach ($refund in $view.repair.refunds) { Write-Host "    refunded round $($refund.round) ($($refund.utc)): $($refund.reason)" }
    foreach ($recovery in $view.recoveries) { Write-Host "  recovered $($recovery.utc): $($recovery.reason) (main checkout: $(if ($recovery.mainCheckout.unknown) { 'unreadable' } else { "HEAD $($recovery.mainCheckout.head)" }), $(@($recovery.mainStatus).Count) changed path(s))$(if (@($recovery.reviewRecords).Count) { ", $(@($recovery.reviewRecords).Count) review record(s) moved to reviews/invalidated" })" }
    foreach ($review in $reviews)
    {
        Write-Host "    round $($review.round) $($review.level) $($review.reviewer) $($review.status) $($review.verdict) blocking=$($review.blockingFindings) commit=$($review.commit)$(if ($review.invalidReasons.Count) { ' INVALID: ' + ($review.invalidReasons -join '; ') })$(if ($review.refunded) { ' REFUNDED: ' + $review.refunded })$(if ($review.refundDenied) { ' REFUND DENIED: ' + $review.refundDenied })$(if ($review.invalidated) { ' INVALIDATED: ' + $review.invalidated })"
        foreach ($f in $review.findings) { Write-Host "      $($f.id) [$($f.severity)] $($f.file):$($f.line) $($f.summary)$(if ($f.disposition) { " => $($f.disposition)" })" }
    }
    if ($acceptance) { Write-Host "  acceptance: $($acceptance.status) ($($acceptance.reason))" }
    if ($view.ship)
    {
        Write-Host "  ship: attempt $($view.ship.attempts)$(if ($view.ship.outcome) { ", outcome $($view.ship.outcome)" })"
        foreach ($step in $view.ship.steps) { Write-Host ("    {0,-9} {1,-19} {2}" -f $step.step, $step.status, $step.detail) }
    }
    Write-Host "  main checkout guard: $(if ($view.mainCheckoutGuard.mutated) { 'MUTATED' } else { 'clean' })"
    Write-Host "  next: $(if ($view.nextTransitions.Count) { $view.nextTransitions -join ', ' } else { '(none)' })"
}

