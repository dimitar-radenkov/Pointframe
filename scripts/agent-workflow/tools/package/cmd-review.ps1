# ---- review ---------------------------------------------------------------------------------------------------------

function Save-GitOutput([string]$Wt, [string[]]$GitArgs, [string]$Path)
{
    $lines = @(& git -C $Wt @GitArgs 2>$null)
    if ($LASTEXITCODE -ne 0) { throw "git $($GitArgs -join ' ') failed in $Wt" }
    [IO.File]::WriteAllText($Path, ((($lines | ForEach-Object { "$_" }) -join "`n") + "`n"), [Text.UTF8Encoding]::new($false))
}

function Set-TreeReadOnly([string]$Dir, [bool]$ReadOnly)
{
    $marker = Join-Path $Dir '.git'
    foreach ($file in @(Get-ChildItem -LiteralPath $Dir -Recurse -File -Force -ErrorAction SilentlyContinue))
    {
        if ($file.FullName -eq $marker -and $ReadOnly) { continue }
        if ($file.IsReadOnly -ne $ReadOnly) { $file.IsReadOnly = $ReadOnly }
    }
}

function Get-SnapshotObservation([string]$Snap)
{
    @{ head = (Get-GitHead $Snap); tree = (Get-GitTree $Snap); clean = (Test-WorktreeClean $Snap); contentTree = $(try { Get-TreeHash $Snap } catch { $null }) }
}

function New-ReviewSnapshot($State, $Record)
{
    # The reviewer reads an immutable detached worktree at exactly the reviewed commit, never the package worktree. Its files are
    # read-only and the host observes its tree and content at the start and the end. The host also prepares the review inputs
    # (commit log, stat, full diff without external diff or textconv programs, the brief) so a reviewer without a shell has them.
    $wt = [string]$State.worktree
    $snap = [string]$Record.snapshot.path
    $inputs = [string]$Record.snapshot.inputDir
    $commit = [string]$Record.commit
    Invoke-Git $wt @('worktree', 'add', '--detach', $snap, $commit) | Out-Null
    $observed = Get-SnapshotObservation $snap
    if ($observed.head -ne $commit -or $observed.tree -ne [string]$Record.tree -or -not $observed.clean -or -not $observed.contentTree) { throw "the review snapshot does not match the reviewed commit (head $($observed.head), tree $($observed.tree), clean $($observed.clean))" }
    Set-TreeReadOnly $snap $true
    New-Item -ItemType Directory -Path $inputs -Force | Out-Null
    $base = [string]$Record.baseSha
    Save-GitOutput $wt @('log', '--no-ext-diff', '--no-textconv', '--no-color', '--format=fuller', "$base..$commit") (Join-Path $inputs 'log.txt')
    Save-GitOutput $wt @('show', '--stat', '--no-ext-diff', '--no-textconv', '--no-color', $commit) (Join-Path $inputs 'stat.txt')
    Save-GitOutput $wt @('diff', '--no-ext-diff', '--no-textconv', '--no-color', $base, $commit) (Join-Path $inputs 'diff.patch')
    Copy-Item -LiteralPath ([string]$State.brief.path) -Destination (Join-Path $inputs 'brief.md') -Force
    # The two things an acceptance case can rest on that a reviewer without a shell could not see: what the fresh check ran and returned,
    # and what the executor said it did.
    [IO.File]::WriteAllText((Join-Path $inputs 'check-summary.json'), (Get-CheckSummaryText $State ([string]$Record.checkId) $commit ([string]$Record.tree)), [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $inputs 'executor-report.md'), (Get-ExecutorReportText $State ([string]$Record.executor.runId)), [Text.UTF8Encoding]::new($false))
    Set-TreeReadOnly $inputs $true
    $observed
}

function Close-ReviewSnapshot([string]$Wt, [string]$Snap)
{
    # Observes the snapshot once more, then removes it; both outcomes are returned for the record.
    $result = @{ endObservation = $null; removed = $false; removeError = $null }
    if (-not (Test-Path -LiteralPath $Snap)) { $result.removeError = 'the snapshot does not exist'; return $result }
    try { $result.endObservation = Get-SnapshotObservation $Snap } catch { $result.removeError = "the snapshot could not be observed: $($_.Exception.Message)" }
    try { Set-TreeReadOnly $Snap $false; Invoke-Git $Wt @('worktree', 'remove', '--force', $Snap) | Out-Null }
    catch
    {
        $result.removeError = "git worktree remove failed: $($_.Exception.Message)"
        try { Remove-Item -LiteralPath $Snap -Recurse -Force -ErrorAction Stop; & git -C $Wt worktree prune 2>&1 | Out-Null } catch { $result.removeError += "; deleting the directory failed: $($_.Exception.Message)" }
    }
    $result.removed = -not (Test-Path -LiteralPath $Snap)
    if ($result.removed -and $result.removeError) { $result.removeError = "$($result.removeError) (removed by deleting the directory)" }
    $result
}
function Get-ReviewInputHashes([string]$InputDir, [string]$PromptPath)
{
    $hashes = @{}
    foreach ($name in @('brief.md', 'diff.patch', 'log.txt', 'stat.txt')) { $hashes[$name] = Get-OptionalSha256 (Join-Path $InputDir $name) }
    # The check summary and the executor's report exist only in reviews prepared by this version of the runner; an older review has
    # neither file, and is checked without them.
    foreach ($name in $script:ReviewOptionalInputs) { if (Test-Path -LiteralPath (Join-Path $InputDir $name) -PathType Leaf) { $hashes[$name] = Get-Sha256 (Join-Path $InputDir $name) } }
    $hashes['prompt.md'] = Get-OptionalSha256 $PromptPath
    $hashes
}

function Get-ReviewInputProblems($Recorded, [string]$InputDir, [string]$PromptPath)
{
    # Every file the reviewer was given, against the hashes recorded before it started.
    if (-not $Recorded) { return @('no review input hashes were recorded') }
    $now = Get-ReviewInputHashes $InputDir $PromptPath
    $problems = @($now.Keys | Sort-Object | Where-Object { -not $Recorded.ContainsKey($_) -or -not $Recorded[$_] -or $Recorded[$_] -ne $now[$_] } | ForEach-Object { "review input $_ changed or is missing" })
    # An input that was recorded and is gone now.
    $problems + @($Recorded.Keys | Sort-Object | Where-Object { $Recorded[$_] -and -not $now.ContainsKey($_) } | ForEach-Object { "review input $_ changed or is missing" })
}

$script:ReviewOptionalInputs = @('check-summary.json', 'executor-report.md')

function Get-CheckSummaryText($State, [string]$CheckId, [string]$Commit, [string]$Tree)
{
    # What the reviewer cannot run: the gates of the fresh check with their exit codes, and the head and tree they were checked for. Built
    # from the state alone, so accept builds the very same text again.
    $check = @(Get-List $State.checks | Where-Object { $_.checkId -eq $CheckId })[0]
    if (-not $check) { return ((([ordered]@{ schemaVersion = 1; checkId = $CheckId; head = $Commit; tree = $Tree; ok = $false; note = 'the check is not in the state' }) | ConvertTo-Json -Depth 4) + "`n") }
    $summary = [ordered]@{
        schemaVersion = 1
        note = 'The runner ran these gates in the package worktree before the review; exit code 0 means the gate passed. They were run on the content tree below, which is the content of the reviewed commit.'
        checkId = [string]$check.checkId; utc = [string]$check.utc; ok = [bool]$check.ok; head = $Commit; tree = $Tree; contentTree = [string]$check.treeHash
        gates = @(Get-List $check.commands | ForEach-Object { [ordered]@{ name = [string]$_.name; kind = [string](Get-Field $_ 'kind'); command = [string](Get-Field $_ 'command'); exitCode = $_.exitCode; skipped = $_.skipped; changedTheWorktree = [bool](Get-Field $_ 'modifiedWorktree') } })
        rawBytesProblems = @(Get-List (Get-Field $check 'rawBytesProblems'))
    }
    ($summary | ConvertTo-Json -Depth 6) + "`n"
}

function Get-ExecutorReportProblem($State, [string]$RunId)
{
    # The report the reviewer is handed has to be the one the run recorded: out.md against run.artifacts.outMd.sha256. $null when it is (or when
    # the run recorded no hash, as the runs of older versions may not), else what differs. The report is the executor's word and nothing else.
    $run = @(Get-List $State.runs | Where-Object { $_.runId -eq $RunId })[0]
    $artifact = if ($run) { Get-Field (Get-Field $run 'artifacts') 'outMd' } else { $null }
    $recorded = [string](Get-Field $artifact 'sha256')
    if (-not $recorded) { return $null }
    $path = [string](Get-Field $artifact 'path')
    if (-not $path -or -not (Test-Path -LiteralPath $path -PathType Leaf)) { return "the executor report of run $RunId ($path) is missing, but the run recorded its hash" }
    if ((Get-Sha256 $path) -ne $recorded) { return "the executor report of run $RunId ($path) differs from the hash recorded when the run ended" }
    $null
}

function Get-ExecutorReportText($State, [string]$RunId)
{
    # The executor's final report as the runner kept it (the artifact of the run the review names), or a line saying there is none.
    $run = @(Get-List $State.runs | Where-Object { $_.runId -eq $RunId })[0]
    $path = if ($run) { [string](Get-Field (Get-Field (Get-Field $run 'artifacts') 'outMd') 'path') } else { '' }
    if ($path -and (Test-Path -LiteralPath $path -PathType Leaf)) { return [IO.File]::ReadAllText($path) }
    '(the executor left no report that the runner kept)'
}

function Test-ReviewInputsAtAccept($State, $Review)
{
    # Re-hashed, then bound to their sources: brief.md is the brief snapshot byte for byte and diff.patch is what the host generates
    # for base..reviewed commit today.
    $problems = @(Get-ReviewInputProblems (Get-Field $Review 'inputHashes') ([string]$Review.snapshot.inputDir) ([string]$Review.promptFile.path))
    if ($problems.Count) { return $problems }
    $dir = [string]$Review.snapshot.inputDir
    if ((Get-Sha256 (Join-Path $dir 'brief.md')) -ne (Get-Sha256 ([string]$State.brief.path))) { $problems += 'the brief the reviewer read is not the brief snapshot byte for byte' }
    $reportProblem = Get-ExecutorReportProblem $State ([string]$Review.executor.runId)
    if ($reportProblem) { $problems += $reportProblem }
    # The check summary is the one the state yields for the review's check and commit, the executor report the one of the run the review names.
    $recordedInputs = Get-Field $Review 'inputHashes'
    if ($recordedInputs -and $recordedInputs.ContainsKey('check-summary.json') -and (Get-Sha256 (Join-Path $dir 'check-summary.json')) -ne (Get-StringSha256 (Get-CheckSummaryText $State ([string]$Review.checkId) ([string]$Review.commit) ([string]$Review.tree)))) { $problems += 'check-summary.json is not the summary of the check the review is bound to' }
    if ($recordedInputs -and $recordedInputs.ContainsKey('executor-report.md') -and (Get-Sha256 (Join-Path $dir 'executor-report.md')) -ne (Get-StringSha256 (Get-ExecutorReportText $State ([string]$Review.executor.runId)))) { $problems += 'executor-report.md is not the report of the executor run the review names' }
    $temp = Join-Path ([IO.Path]::GetTempPath()) "package-diff-$([guid]::NewGuid().ToString('N')).patch"
    try
    {
        Save-GitOutput ([string]$State.worktree) @('diff', '--no-ext-diff', '--no-textconv', '--no-color', [string]$Review.baseSha, [string]$Review.commit) $temp
        if ((Get-Sha256 $temp) -ne (Get-Sha256 (Join-Path $dir 'diff.patch'))) { $problems += 'diff.patch is not the diff the host generates for base..reviewed commit' }
    }
    catch { $problems += "the diff could not be regenerated: $($_.Exception.Message)" }
    finally { Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue }
    $problems
}
function Get-ReviewerOutputObservation([string]$ReviewDir)
{
    # What the host finds in the review folder: 'none', or what exists. A Codex final message (final.md, not empty) or a Claude result event
    # in events.jsonl is reviewer output. A folder that is not there cannot be observed.
    if (-not (Test-Path -LiteralPath $ReviewDir -PathType Container)) { return 'the review folder is missing' }
    $final = Join-Path $ReviewDir 'final.md'
    if ((Test-Path -LiteralPath $final -PathType Leaf) -and -not [string]::IsNullOrWhiteSpace([IO.File]::ReadAllText($final))) { return 'a Codex final message exists' }
    $events = Join-Path $ReviewDir 'events.jsonl'
    if ((Test-Path -LiteralPath $events -PathType Leaf) -and (Select-String -LiteralPath $events -Pattern '"type"\s*:\s*"result"' -Quiet)) { return 'a Claude result event exists' }
    'none'
}

function Get-RefundDecision($State, [bool]$Eligible)
{
    # Refund: the round is given back. Denied: why an eligible round is charged anyway (the package used up its refunds), else $null.
    # Without a cap an observer of the intermediate events could kill every reviewer before its verdict and re-roll for free.
    if (-not $Eligible) { return @{ Refund = $false; Denied = $null } }
    $used = @(Get-List $State.repair.refunds).Count
    if ($used -ge $script:RefundLimit) { return @{ Refund = $false; Denied = "this package has already had $used refunded review round(s), which is the cap ($script:RefundLimit), so the round is charged" } }
    @{ Refund = $true; Denied = $null }
}

function Resolve-AbandonedReview($Info, [string]$Slug)
{
    # A package left in 'reviewing' (its wrapper died): the review is recorded as invalid and its round is given back.
    $stateLock = Enter-StateLock $Info.StateRoot $Slug
    try
    {
        $state = Read-State $Info.StateRoot $Slug
        if ([string]$state.stage -ne 'reviewing') { return }
        $review = @(Get-List $state.reviews | Where-Object { $_.status -eq 'running' }) | Select-Object -Last 1
        if ($review -and $review.wrapper -and (Test-ProcessIdentityAlive ([int]$review.wrapper.pid) ([string]$review.wrapper.startUtc)) -and [int]$review.wrapper.pid -ne $PID) { throw "review $($review.reviewId) is still in progress (pid $($review.wrapper.pid))" }
        $reviewId = if ($review) { [string]$review.reviewId } else { '' }
        $leftover = if ($review) { Get-Field (Get-Field $review 'snapshot') 'path' } else { $null }
        if ($leftover -and (Test-Path -LiteralPath $leftover)) { Close-ReviewSnapshot ([string]$state.worktree) ([string]$leftover) | Out-Null }
        # The round is given back only when the host sees that the reviewer produced nothing (no Codex final message, no Claude result event)
        # in the review folder. Killing the wrapper while a verdict is forming must not buy a free round: with output present it stays charged.
        $reviewDir = if ($reviewId) { Join-Path (Get-PackagePaths $Info.StateRoot $Slug).Dir "reviews/$reviewId" } else { $null }
        $observation = if ($reviewDir) { Get-ReviewerOutputObservation $reviewDir } else { 'the review folder cannot be observed' }
        $decision = Get-RefundDecision $state ([bool]$review -and ($observation -eq 'none'))
        $refund = $decision.Refund
        $roundsAfter = [int]$state.repair.roundsUsed - $(if ($refund) { 1 } else { 0 })
        $exhausted = $roundsAfter -ge $script:RepairLimit
        $abandonedReason = if ($refund) { 'the review was interrupted before it finished (the wrapper died) and the reviewer had produced no output' }
        elseif ($decision.Denied) { "the review was interrupted before it finished (the wrapper died) and the reviewer had produced no output, but $($decision.Denied)" }
        elseif ($observation -eq 'none' -or $observation -like 'the review folder*') { "the review was interrupted before it finished (the wrapper died); $observation, so the round stays charged" }
        else { "abandoned after output existed ($observation); the round stays charged" }
        Update-State $Info.StateRoot $Slug 'review-recovered' {
            param($s)
            $s.reviews = @(Get-List $s.reviews | ForEach-Object {
                    if ($_.reviewId -eq $reviewId)
                    {
                        $_.status = 'invalid'; $_.verdict = $null; $_.invalidReasons = @($abandonedReason); $_.termination = 'abandoned'; $_.endedUtc = (Get-Now)
                        if ($refund) { $_.refunded = @{ utc = (Get-Now); reason = $abandonedReason } }
                        if ($decision.Denied) { $_.refundDenied = @{ utc = (Get-Now); reason = $decision.Denied } }
                    }
                    $_
                })
            if ($refund)
            {
                $s.repair.refunds = @(Get-List $s.repair.refunds) + @(@{ utc = (Get-Now); reviewId = $reviewId; round = (Get-Field $review 'round'); reason = $abandonedReason; roundsUsedBefore = [int]$s.repair.roundsUsed })
                $s.repair.roundsUsed = [Math]::Max(0, [int]$s.repair.roundsUsed - 1)
            }
            $s.stage = if ($exhausted) { 'needs-coordinator' } else { 'review-invalid' }
        } -Locked | Out-Null
    }
    finally { $stateLock.Dispose() }
}

function Start-Review($Info)
{
    # Under the package lock: every precondition, the budget, then the review is recorded as running (and its round spent)
    # before anything starts, so a restart cannot get the round back.
    Sync-Invalidation $Info $Id
    $state = Read-State $Info.StateRoot $Id
    Assert-StateConsistent $state $Info
    $problems = @(Test-SnapshotsIntact $state)
    if ($problems.Count) { throw ($problems -join '; ') }
    $stage = [string]$state.stage
    if ($stage -eq 'invalidated') { throw 'the package is invalidated (the branch head changed after the review): run check, commit, then review' }
    if ($stage -notin @('committed', 'review-failed', 'review-invalid', 'review-passed', 'needs-coordinator', 'needs-review')) { throw "review is not allowed in stage '$stage'$(if ($stage -eq 'checks-passed') { ": run commit first" })" }
    if (-not $Level) { throw '-Level light|heavy is required' }
    if ([bool]$state.guard.mainCheckoutMutated) { throw 'the main-checkout guard is not clean; no review is started' }
    $wt = [string]$state.worktree
    $commit = $state.commit
    if (-not $commit) { throw 'no commit is recorded: run commit first' }
    $head = Get-GitHead $wt
    $tree = Get-GitTree $wt
    if ($head -ne [string]$commit.sha -or $tree -ne [string]$commit.tree) { throw "the branch head $head is not the commit recorded by 'commit' ($($commit.sha)): run check and commit again" }
    if (-not (Test-WorktreeClean $wt)) { throw 'the worktree has uncommitted changes; a review inspects a committed head with a clean worktree' }
    $content = Get-TreeHash $wt
    $stale = Get-CheckFreshnessProblem $state $content
    if ($stale) { throw "review needs a fresh passing check for this commit's tree: $stale" }
    $latestCheck = @(Get-List $state.checks)[-1]

    # The reviewer is chosen, and a Claude CLI that is too old for its model refuses the round, before the budget is looked at or spent.
    $criteria = @(Get-ReviewCriteria ([string]$state.brief.path))
    $executorInfo = Get-ExecutorIdentity $state
    $chosen = Resolve-ReviewerChoice $executorInfo $Level $Reviewer
    # A report that is not the one the run recorded is a host refusal: nothing is started and no round is spent.
    $reportProblem = Get-ExecutorReportProblem $state ([string]$executorInfo.run.runId)
    if ($reportProblem) { throw "refused: $reportProblem. No review round was spent; restore the report or run the executor again" }
    $claudeLauncher = $null
    $claudeVersion = $null
    if ($chosen.provider -eq 'claude')
    {
        $claudeLauncher = Resolve-ClaudeLauncher
        $claudeVersion = Get-ClaudeCliVersion $claudeLauncher
        $versionProblem = Get-CliVersionProblem ([string]$chosen.model) $claudeVersion $script:ClaudeVersionProbeProblem
        if ($versionProblem) { throw $versionProblem }
    }

    $used = [int]$state.repair.roundsUsed
    if ($ResetBudget)
    {
        if ([string]::IsNullOrWhiteSpace($Reason)) { throw '-ResetBudget needs -Reason' }
        Reset-RepairBudget $Info $Id $Reason
        $used = 0
    }
    if ($used -ge $script:RepairLimit)
    {
        Update-State $Info.StateRoot $Id 'needs-coordinator' { param($s) $s.stage = 'needs-coordinator' } -Locked | Out-Null
        throw "the repair budget is used up ($used of $script:RepairLimit review rounds): the package needs the coordinator. Reassess it, then resume with -ResetBudget -Reason <text>"
    }

    # Codex continues its earlier session. Claude never does: its earlier round ran in a snapshot that is gone, so it starts fresh and
    # the prompt carries the previous round's verdict and findings. A round that was refunded gave nothing to carry.
    $prior = if ($chosen.provider -eq 'claude') { @(Get-List $state.reviews | Where-Object { $who = Get-Field $_ 'reviewer'; $who -and $who.kind -eq $chosen.kind -and -not (Get-Field $_ 'refunded') }) | Select-Object -Last 1 }
    else { @(Get-List $state.reviews | Where-Object { $who = Get-Field $_ 'reviewer'; $who -and $who.kind -eq $chosen.kind -and $who.sessionId }) | Select-Object -Last 1 }
    $resumeSession = if ($prior -and $chosen.provider -ne 'claude') { [string]$prior.reviewer.sessionId } else { $null }
    $reviewId = [guid]::NewGuid().ToString('N')
    $round = $used + 1
    $paths = Get-PackagePaths $Info.StateRoot $Id
    $dir = Join-Path $paths.Dir "reviews/$reviewId"
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    $verdictPath = Join-Path $dir 'verdict.json'
    # The base the host observes itself (after a fetch), not the one the state says; a mismatch makes the review invalid.
    $startProblems = [Collections.Generic.List[string]]::new()
    $remote = Get-PackageRemote ([string]$state.baseRef)
    if ($remote) { try { Invoke-Git $wt @('fetch', $remote) | Out-Null } catch { $startProblems.Add("cannot fetch $remote to observe the base: $($_.Exception.Message)") } }
    $observedBase = ([string](& git -C $wt merge-base HEAD ([string]$state.baseRef) 2>$null)).Trim()
    if (-not $observedBase) { $startProblems.Add("the base cannot be observed (git merge-base HEAD $($state.baseRef))") }
    elseif ($observedBase -ne [string]$state.baseSha) { $startProblems.Add("the observed base $observedBase differs from the recorded baseSha $($state.baseSha)") }
    $fileMap = Get-FileHashMap $wt
    $fileMapPath = Join-Path $dir 'files.json'
    [IO.File]::WriteAllText($fileMapPath, ($fileMap | ConvertTo-Json -Depth 3), [Text.UTF8Encoding]::new($false))
    $mainCheckout = [string]$state.repository.mainCheckout
    $mainBefore = Get-Fingerprint $mainCheckout
    if (-not $mainBefore.treeHash) { throw "the main checkout cannot be fingerprinted (unreadable: $($mainBefore.unreadable -join ', ')); the review was not started" }
    $record = @{
        reviewId = $reviewId; round = $round; level = $Level; startedUtc = (Get-Now); endedUtc = $null; status = 'running'; verdict = $null; invalidReasons = @()
        reviewer = @{ kind = $chosen.kind; provider = $chosen.provider; model = $chosen.model; effort = $chosen.effort; sessionId = $null; resumedFrom = $resumeSession; priorRound = $(if ($prior) { Get-Field $prior 'round' } else { $null }); cliVersion = $null; permissions = $null; executable = $null }
        executor = @{ provider = $executorInfo.provider; model = $executorInfo.model; kind = $executorInfo.kind; runId = [string]$executorInfo.run.runId; session = $executorInfo.session }
        commit = $head; tree = $tree; contentTree = $content; checkId = [string]$latestCheck.checkId; baseSha = $(if ($observedBase) { $observedBase } else { [string]$state.baseSha }); baseObserved = $observedBase; startProblems = @($startProblems)
        executors = @($executorInfo.set); fileHashes = @{ path = $fileMapPath; sha256 = (Get-Sha256 $fileMapPath) }; manifestSha256 = [string]$state.manifest.sha256
        snapshot = @{ path = (Join-Path $paths.Dir "review-$reviewId"); inputDir = (Join-Path $paths.Dir "review-$reviewId-input"); created = $false; startObservation = $null; endObservation = $null; removed = $false; removeError = $null }
        briefSha256 = [string]$state.brief.sha256; criteria = $criteria; criteriaSha256 = (Get-StringSha256 (($criteria | ForEach-Object { "$($_.id)=$($_.text)" }) -join "`n"))
        promptFile = $null; finalMessage = $null; verdictFile = @{ path = $verdictPath; sha256 = $null }; observedStart = @{ head = $head; tree = $tree; contentTree = $content; clean = $true }; observedEnd = $null
        wrapper = @{ pid = $PID; startUtc = (Get-ProcessStartIdentity $PID) }; mainCheckoutBefore = $mainBefore; mainCheckoutAfter = $null; termination = 'running'; exitCode = $null; containmentVerified = $null
        tokens = $null; denials = @(); findings = @(); criteriaResults = @(); omissions = @(); blockingFindings = 0; seconds = $null
    }
    $promptText = New-ReviewPrompt $state $record $verdictPath $prior ([bool]$resumeSession)
    $promptPath = Join-Path $dir 'prompt.md'
    [IO.File]::WriteAllText($promptPath, $promptText, [Text.UTF8Encoding]::new($false))
    $record.promptFile = @{ path = $promptPath; sha256 = (Get-Sha256 $promptPath) }
    Update-State $Info.StateRoot $Id 'review-started' {
        param($s)
        $s.repair.roundsUsed = $round
        $s.reviews = @(Get-List $s.reviews) + @($record)
        $s.pendingFix = $null
        $s.stage = 'reviewing'
    } -Locked | Out-Null
    Write-Host "review ${reviewId}: round $round of $script:RepairLimit, $Level, reviewer $($chosen.kind) ($($chosen.model))$(if ($resumeSession) { ", resuming session $resumeSession" })"
    @{ Record = $record; Chosen = $chosen; Dir = $dir; PromptPath = $promptPath; ResumeSession = $resumeSession; Worktree = $wt; MainCheckout = $mainCheckout; MainBefore = $mainBefore; StartProblems = @($startProblems); SnapshotStart = $null; SnapshotEnd = $null; InputHashes = $null; ClaudeLauncher = $claudeLauncher; ClaudeVersion = $claudeVersion }
}

function Complete-Review($Info, $Context, $Proc)
{
    $record = $Context.Record
    $reviewId = [string]$record.reviewId
    $wt = $Context.Worktree
    $stateLock = Enter-StateLock $Info.StateRoot $Id
    try
    {
        $state = Read-State $Info.StateRoot $Id
        $reasons = [Collections.Generic.List[string]]::new()
        # What the host saw when the reviewer finished: the same head and tree, a clean worktree, unchanged content.
        $endHead = Get-GitHead $wt
        $endTree = Get-GitTree $wt
        $endClean = Test-WorktreeClean $wt
        $endContent = try { Get-TreeHash $wt } catch { $null }
        $observedEnd = @{ head = $endHead; tree = $endTree; contentTree = $endContent; clean = $endClean }
        $mainAfter = Get-Fingerprint $Context.MainCheckout
        $mainMutated = -not (Test-FingerprintsEqual $Context.MainBefore $mainAfter)
        if ($Proc.Termination -ne 'exit') { $reasons.Add("the reviewer did not finish cleanly: $($Proc.Termination)") }
        if (-not $Proc.ContainmentVerified) { $reasons.Add("containment unverified: $($Proc.ContainmentReason)") }
        if ($Proc.Termination -eq 'exit' -and $Proc.ExitCode -ne 0) { $reasons.Add("the reviewer exited with code $($Proc.ExitCode)") }
        if ($Proc.ResultError) { $reasons.Add('the reviewer reported an error result') }
        if ($Proc.IdentityProblem) { $reasons.Add("session identity: $($Proc.IdentityProblem)") }
        if (@($Proc.Denials).Count) { $reasons.Add("the reviewer was denied $(@($Proc.Denials).Count) tool call(s), so it may not have inspected everything") }
        if ($endHead -ne [string]$record.commit) { $reasons.Add("the branch head changed during the review ($($record.commit) -> $endHead)") }
        if ($endTree -ne [string]$record.tree) { $reasons.Add("the git tree changed during the review ($($record.tree) -> $endTree)") }
        if (-not $endClean) { $reasons.Add('the worktree was not clean at the end of the review') }
        if ($endContent -ne [string]$record.contentTree) { $reasons.Add('the worktree content changed during the review') }
        if ($mainMutated) { $reasons.Add('the main checkout changed during the review') }
        foreach ($problem in @($Context.StartProblems)) { $reasons.Add([string]$problem) }
        # Everything the reviewer was given (the prepared inputs and the prompt) is unchanged since before it started.
        if ($Context.InputHashes) { foreach ($problem in @(Get-ReviewInputProblems $Context.InputHashes ([string]$record.snapshot.inputDir) ([string]$Context.PromptPath))) { $reasons.Add([string]$problem) } }
        # The base again, after the reviewer finished.
        $endBase = ([string](& git -C $wt merge-base HEAD ([string]$state.baseRef) 2>$null)).Trim()
        if ($endBase -ne [string]$state.baseSha) { $reasons.Add("the observed base at the end of the review ($endBase) differs from the recorded baseSha ($($state.baseSha))") }
        # The immutable snapshot the reviewer read: created, unchanged from start to end, removed afterwards.
        $snapStart = $Context.SnapshotStart
        $snapEnd = $Context.SnapshotEnd
        if (-not $snapStart) { $reasons.Add('the review snapshot was not created or verified') }
        else
        {
            if (-not $snapEnd -or -not $snapEnd.endObservation) { $reasons.Add('the review snapshot could not be observed at the end of the review') }
            else
            {
                $e = $snapEnd.endObservation
                if ($e.head -ne $snapStart.head -or $e.tree -ne $snapStart.tree -or $e.contentTree -ne $snapStart.contentTree -or -not $e.clean) { $reasons.Add('the review snapshot changed during the review (head, tree, content or cleanliness)') }
            }
            if ($snapEnd -and -not $snapEnd.removed) { $reasons.Add("the review snapshot could not be removed: $($snapEnd.removeError)") }
        }

        $finalPath = Join-Path $Context.Dir 'final.md'
        $verdictPath = [string]$record.verdictFile.path
        $verdictText = $null
        if ($null -ne $Proc.FinalText)
        {
            if (-not (Test-Path -LiteralPath $finalPath -PathType Leaf)) { [IO.File]::WriteAllText($finalPath, [string]$Proc.FinalText, [Text.UTF8Encoding]::new($false)) }
            $verdictText = Get-VerdictJsonText ([string]$Proc.FinalText)
            [IO.File]::WriteAllText($verdictPath, $verdictText, [Text.UTF8Encoding]::new($false))
        }
        else { $reasons.Add('the reviewer produced no final message, so there is no verdict file') }
        $checkFresh = (-not (Get-CheckFreshnessProblem $state ([string]$endContent))) -and $endContent
        $validation = if ($null -ne $verdictText) { Test-VerdictDocument $verdictText $record.criteria ([string]$record.commit) ([string]$record.tree) ([bool]$checkFresh) } else { $null }
        if ($validation) { foreach ($problem in $validation.problems) { $reasons.Add([string]$problem) } }
        $valid = ($reasons.Count -eq 0) -and $validation -and $validation.ok
        $verdict = if ($valid) { $validation.verdict } else { $null }

        # A round lost to a host-detected environment failure (the reviewer could not be started, or crashed before any verdict) is given
        # back, with the reason on record. What the reviewer itself got wrong (schema, criteria, an unclean snapshot, a timeout) still costs it.
        $infraReason = [string](Get-Field $Proc 'InfraFailure')
        $decision = Get-RefundDecision $state ([bool]($infraReason -and -not $valid -and -not $mainMutated))
        $refund = $decision.Refund
        if ($decision.Denied) { $reasons.Add("the environment failure ($infraReason) would have been refunded, but $($decision.Denied)") }
        $roundsAfter = [int]$state.repair.roundsUsed - $(if ($refund) { 1 } else { 0 })
        $exhausted = $roundsAfter -ge $script:RepairLimit
        $stage = if ($mainMutated) { 'blocked-main-mutated' } elseif ($valid -and $verdict -eq 'PASS') { 'review-passed' } elseif ($exhausted) { 'needs-coordinator' } elseif ($valid) { 'review-failed' } else { 'review-invalid' }
        $finalHash = Get-OptionalSha256 $finalPath
        $verdictHash = Get-OptionalSha256 $verdictPath
        Update-State $Info.StateRoot $Id 'review-finished' {
            param($s)
            $s.reviews = @(Get-List $s.reviews | ForEach-Object {
                    if ($_.reviewId -eq $reviewId)
                    {
                        $_.endedUtc = (Get-Now); $_.status = $(if ($valid) { 'valid' } else { 'invalid' }); $_.verdict = $verdict; $_.invalidReasons = @($reasons)
                        $_.reviewer.sessionId = $Proc.SessionId; $_.reviewer.cliVersion = $Proc.CliVersion; $_.reviewer.permissions = $Proc.Permissions; $_.reviewer.executable = $Proc.Executable
                        $_.finalMessage = @{ path = $finalPath; sha256 = $finalHash }; $_.verdictFile = @{ path = $verdictPath; sha256 = $verdictHash }
                        $_.observedEnd = $observedEnd; $_.mainCheckoutAfter = $mainAfter; $_.startProblems = @($Context.StartProblems); $_.inputHashes = $Context.InputHashes; $_.snapshot.created = [bool]$snapStart; $_.snapshot.startObservation = $snapStart; $_.snapshot.endObservation = $(if ($snapEnd) { $snapEnd.endObservation } else { $null }); $_.snapshot.removed = [bool]($snapEnd -and $snapEnd.removed); $_.snapshot.removeError = $(if ($snapEnd) { $snapEnd.removeError } else { $null }); $_.termination = $Proc.Termination; $_.exitCode = $Proc.ExitCode; $_.containmentVerified = [bool]$Proc.ContainmentVerified
                        $_.tokens = $Proc.Tokens; $_.denials = @($Proc.Denials); $_.seconds = $Proc.Seconds
                        if ($valid) { $_.findings = @($validation.findings); $_.criteriaResults = @($validation.criteria); $_.omissions = @($validation.omissions); $_.blockingFindings = $validation.blocking }
                        if ($refund) { $_.refunded = @{ utc = (Get-Now); reason = $infraReason } }
                        if ($decision.Denied) { $_.refundDenied = @{ utc = (Get-Now); reason = $decision.Denied } }
                    }
                    $_
                })
            if ($refund)
            {
                $s.repair.refunds = @(Get-List $s.repair.refunds) + @(@{ utc = (Get-Now); reviewId = $reviewId; round = $record.round; reason = $infraReason; roundsUsedBefore = [int]$s.repair.roundsUsed })
                $s.repair.roundsUsed = [Math]::Max(0, [int]$s.repair.roundsUsed - 1)
            }
            if ($mainMutated) { $s.guard = @{ mainCheckoutMutated = $true; lastRunId = $reviewId; before = $Context.MainBefore; after = $mainAfter } }
            $s.stage = $stage
        } -Locked | Out-Null
        if ($mainMutated) { $script:ExitCode = 5; Write-Host 'ERROR the main checkout changed during the review. Nothing was reverted; the package is stopped.'; return }
        if ($valid)
        {
            Write-Host "review ${reviewId}: $verdict (valid) - $($validation.blocking) blocking finding(s), $(@($validation.findings).Count) in all, $(@($validation.omissions).Count) omission(s)"
            foreach ($f in $validation.findings) { Write-Host "  $($f.id) [$($f.severity)] $($f.file):$($f.line) $($f.summary)" }
        }
        else
        {
            Write-Host "review ${reviewId}: INVALID (never a PASS)"
            foreach ($why in $reasons) { Write-Host "  - $why" }
            if ($refund) { Write-Host "  the round was refunded because the failure was the environment's, not the reviewer's ($infraReason): repair rounds used $roundsAfter of $script:RepairLimit" }
        }
        if ($stage -eq 'needs-coordinator') { Write-Host "the repair budget is used up ($roundsAfter of $script:RepairLimit rounds): the package needs the coordinator" }
        if ($stage -ne 'review-passed') { $script:ExitCode = 1 }
    }
    finally { $stateLock.Dispose() }
}

function Invoke-ReviewCore
{
    $info = Get-RepoInfo $Repo
    Resolve-AbandonedReview $info $Id
    $stateLock = Enter-StateLock $info.StateRoot $Id
    try { $context = Start-Review $info }
    finally { $stateLock.Dispose() }
    $process = $null
    $record = $context.Record
    $snapshotCreated = $false
    $failed = { param($why, $infra = $null) @{ Termination = 'failed'; ExitCode = $null; ContainmentVerified = $false; ContainmentReason = $why; SessionId = $null; IdentityProblem = $null; FinalText = $null; ResultError = $false; Denials = @(); Tokens = $null; Seconds = $null; CliVersion = $null; Permissions = $null; Executable = $null; InfraFailure = $infra } }
    try
    {
        if (@($context.StartProblems).Count) { $process = & $failed "the reviewer was not started: $(@($context.StartProblems) -join '; ')" }
        else
        {
            $snapshotCreated = $true
            $context.SnapshotStart = New-ReviewSnapshot (Read-State $info.StateRoot $Id) $record
            $snap = [string]$record.snapshot.path
            # The checks ran on the package worktree: the snapshot has to hold exactly the same raw bytes. No normalization, no
            # tolerance: any difference means the checks say nothing about what the reviewer reads. The paths come from the
            # per-file fingerprint recorded with the check's content at the start of the review.
            if ($context.SnapshotStart.contentTree -ne [string]$record.contentTree)
            {
                $checked = Get-Content -LiteralPath ([string]$record.fileHashes.path) -Raw | ConvertFrom-Json -AsHashtable
                $snapMap = Get-FileHashMap $snap
                $differing = @(@($checked.Keys) + @($snapMap.Keys) | Select-Object -Unique | Where-Object { $checked[$_] -ne $snapMap[$_] } | Sort-Object)
                $context.StartProblems = @($context.StartProblems) + @("checks did not run on the committed content (raw bytes differ: $(($differing | Select-Object -First 5) -join ', '))")
            }
            $context.InputHashes = Get-ReviewInputHashes ([string]$record.snapshot.inputDir) ([string]$context.PromptPath)
            $inputHashes = $context.InputHashes
            $reviewIdNow = [string]$record.reviewId
            Update-State $info.StateRoot $Id 'review-inputs-bound' { param($s) $s.reviews = @(Get-List $s.reviews | ForEach-Object { if ($_.reviewId -eq $reviewIdNow) { $_.inputHashes = $inputHashes }; $_ }) } | Out-Null
            $process = if (@($context.StartProblems).Count) { & $failed "the reviewer was not started: $(@($context.StartProblems) -join '; ')" } elseif ($context.Chosen.provider -eq 'claude') { Invoke-ClaudeReviewProcess $snap $context.Dir $context.PromptPath $context.Chosen ([string]$record.snapshot.inputDir) $context.ClaudeLauncher ([string]$context.ClaudeVersion) }
            else { Invoke-CodexReviewProcess $snap $context.Dir $context.PromptPath $context.Chosen $context.ResumeSession }
        }
    }
    catch { $process = & $failed "the reviewer could not be started: $($_.Exception.Message)" "the reviewer could not be started: $($_.Exception.Message)" }
    finally
    {
        if ($snapshotCreated -and (Test-Path -LiteralPath ([string]$record.snapshot.path))) { $context.SnapshotEnd = Close-ReviewSnapshot $context.Worktree ([string]$record.snapshot.path) }
    }
    Complete-Review $info $context $process
}

function Invoke-Review
{
    $info = Get-RepoInfo $Repo
    $lock = Enter-MainSyncSharedLock $info.CommonDir
    try { Invoke-ReviewCore }
    finally { $lock.Dispose() }
}
