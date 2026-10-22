# ---- disposition, fix, accept ---------------------------------------------------------------------------------------

function Invoke-Disposition
{
    if (-not $Finding) { throw '-Finding <id> is required' }
    if (([bool]$Accept) -eq ([bool]$Reject)) { throw 'give exactly one of -Accept or -Reject' }
    if ([string]::IsNullOrWhiteSpace($Reason)) { throw 'a disposition needs -Reason' }
    $info = Get-RepoInfo $Repo
    $stateLock = Enter-StateLock $info.StateRoot $Id
    try
    {
        $state = Read-State $info.StateRoot $Id
        Assert-StateConsistent $state $info
        $review = Get-LatestReview $state
        if (-not $review -or $review.status -ne 'valid') { throw 'there is no valid review whose findings could be disposed of' }
        $target = @(Get-List $review.findings | Where-Object { $_.id -eq $Finding })
        if ($target.Count -eq 0) { throw "the latest review ($($review.reviewId)) has no finding '$Finding' (it has: $((Get-List $review.findings | ForEach-Object { $_.id }) -join ', '))" }
        $decision = if ($Accept) { 'accept' } else { 'reject' }
        $reviewId = [string]$review.reviewId
        $text = $Reason.Trim()
        Update-State $info.StateRoot $Id "disposition-$decision" {
            param($s)
            $s.reviews = @(Get-List $s.reviews | ForEach-Object {
                    if ($_.reviewId -eq $reviewId)
                    {
                        $_.findings = @(Get-List $_.findings | ForEach-Object {
                                if ($_.id -eq $Finding)
                                {
                                    $entry = @{ decision = $decision; reason = $text; utc = (Get-Now) }
                                    $_.dispositionHistory = @(Get-List (Get-Field $_ 'dispositionHistory')) + @($entry)
                                    $_.disposition = $entry
                                }
                                $_
                            })
                    }
                    $_
                })
        } -Locked | Out-Null
        Write-Host "finding $Finding of review ${reviewId}: $decision ($text)"
    }
    finally { $stateLock.Dispose() }
}

function Read-CoordinatorFindings([string]$Path, $Review, [string]$PackageDir)
{
    # The findings the coordinator wrote for a FAIL verdict that listed none: a strict JSON array (or { "findings": [...] }) of objects with
    # file, line, summary and scenario (id and severity optional: K1.. and blocking). A copy is kept read-only in the package state
    # directory and hashed; the entry returned is what the state records.
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "-Findings file not found: $Path" }
    $raw = [IO.File]::ReadAllText($Path)
    $scan = Find-DuplicateJsonProperty $raw
    if ($scan -like 'error:*') { throw "-Findings is not strict JSON: $($scan.Substring(6))" }
    if ($scan -like 'dup:*') { throw "-Findings repeats the property '$($scan.Substring(4))'" }
    $document = $raw | ConvertFrom-Json -AsHashtable -NoEnumerate @script:JsonDate
    $list = if ($document -is [System.Collections.IDictionary] -and $document.Contains('findings')) { ,$document['findings'] } else { ,$document }
    if ($list -isnot [array] -or $list.Count -eq 0) { throw '-Findings must be a non-empty JSON array of {file, line, summary, scenario[, id, severity]} objects' }
    $items = @()
    $seen = @{}
    $index = 0
    foreach ($item in $list)
    {
        $index++
        if ($item -isnot [System.Collections.IDictionary]) { throw "-Findings[$index] is not an object" }
        $unknown = @($item.Keys | Where-Object { $_ -notin @('id', 'severity', 'file', 'line', 'summary', 'scenario') })
        if ($unknown.Count) { throw "-Findings[$index] has unknown key(s): $($unknown -join ', ')" }
        foreach ($key in @('file', 'summary', 'scenario')) { if ($item[$key] -isnot [string] -or [string]::IsNullOrWhiteSpace($item[$key])) { throw "-Findings[$index].$key must be a non-empty string" } }
        if (-not ($item['line'] -is [int] -or $item['line'] -is [long]) -or $item['line'] -lt 0) { throw "-Findings[$index].line must be an integer >= 0" }
        $severity = if ($item.Contains('severity')) { $item['severity'] } else { 'blocking' }
        if ($severity -isnot [string] -or $severity -cnotin @('blocking', 'non-blocking')) { throw "-Findings[$index].severity must be blocking or non-blocking" }
        $id = if ($item.Contains('id')) { $item['id'] } else { "K$index" }
        if ($id -isnot [string] -or [string]::IsNullOrWhiteSpace($id)) { throw "-Findings[$index].id must be a non-empty string" }
        if ($seen.ContainsKey($id)) { throw "-Findings: id $id appears twice" }
        $seen[$id] = $true
        $items += @(@{ id = $id; severity = $severity; file = $item['file']; line = [int]$item['line']; summary = $item['summary']; scenario = $item['scenario'] })
    }
    $dir = Join-Path $PackageDir 'coordinator-findings'
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    $kept = Join-Path $dir "$([guid]::NewGuid().ToString('N')).json"
    Copy-Item -LiteralPath $Path -Destination $kept
    Set-ItemProperty -LiteralPath $kept -Name IsReadOnly -Value $true
    @{ utc = (Get-Now); reviewId = [string]$Review.reviewId; round = $Review.round; source = ([IO.Path]::GetFullPath($Path)); path = $kept; sha256 = (Get-Sha256 $kept); findings = @($items) }
}

function Invoke-FixCore
{
    if ($script:ExecutorBound -and $Executor -ne 'same') { throw "fix resumes the same executor; -Executor $Executor is not supported" }
    $info = Get-RepoInfo $Repo
    $paths = Get-PackagePaths $info.StateRoot $Id
    $stateLock = Enter-StateLock $info.StateRoot $Id
    try
    {
        Sync-Invalidation $info $Id
        $state = Read-State $info.StateRoot $Id
        Assert-StateConsistent $state $info
        $problems = @(Test-SnapshotsIntact $state)
        if ($problems.Count) { throw ($problems -join '; ') }
        $review = Get-LatestReview $state
        if (-not $review -or $review.status -ne 'valid' -or $review.verdict -ne 'FAIL') { throw 'fix needs a valid FAIL review as the latest review' }
        if ($Findings -and @(Get-List $review.findings).Count -gt 0) { throw "-Findings is only for a FAIL verdict that carries no finding at all; this review lists $(@(Get-List $review.findings).Count) finding(s): dispose of them with 'disposition' instead" }
        $pending = $state.pendingFix
        $retry = $pending -and $pending.reviewId -eq $review.reviewId -and [string]$state.stage -in @('executor-completed', 'executor-impl-failed', 'executor-env-blocked', 'checks-failed')
        if ([string]$state.stage -notin @('review-failed', 'needs-coordinator') -and -not $retry) { throw "fix is not allowed in stage '$($state.stage)'" }
        if ($ResetBudget)
        {
            if ([string]::IsNullOrWhiteSpace($Reason)) { throw '-ResetBudget needs -Reason' }
            Reset-RepairBudget $info $Id $Reason
            $state = Read-State $info.StateRoot $Id
        }
        if ([int]$state.repair.roundsUsed -ge $script:RepairLimit)
        {
            Update-State $info.StateRoot $Id 'needs-coordinator' { param($s) $s.stage = 'needs-coordinator' } -Locked | Out-Null
            throw "the repair budget is used up ($($state.repair.roundsUsed) of $script:RepairLimit review rounds): the package needs the coordinator. Resume with -ResetBudget -Reason <text>"
        }
        $blocking = @(Get-List $review.findings | Where-Object { $_.severity -eq 'blocking' })
        $undecided = @($blocking | Where-Object { -not $_.disposition } | ForEach-Object { $_.id })
        if ($undecided.Count) { throw "blocking finding(s) without a disposition: $($undecided -join ', '). Set each with: disposition -Finding <id> -Accept|-Reject -Reason <text>" }
        $accepted = @(Get-List $review.findings | Where-Object { $_.disposition -and $_.disposition.decision -eq 'accept' })
        # A FAIL verdict can carry no finding at all (a failing criterion alone). Then there is nothing to accept, and the coordinator's own
        # list stands in for the findings: recorded, hashed, and the only thing the fix request is built from.
        $reviewFindingCount = @(Get-List $review.findings).Count
        $coordinatorEntry = $null
        $coordinatorFresh = $false
        if ($Findings)
        {
            $coordinatorEntry = Read-CoordinatorFindings $Findings $review $paths.Dir
            $coordinatorFresh = $true
        }
        elseif ($reviewFindingCount -eq 0 -and $retry -and (Get-Field $pending 'coordinatorFindings'))
        {
            $coordinatorEntry = $pending.coordinatorFindings
            if (-not (Test-Path -LiteralPath ([string]$coordinatorEntry.path) -PathType Leaf) -or (Get-Sha256 ([string]$coordinatorEntry.path)) -ne [string]$coordinatorEntry.sha256) { throw 'the coordinator findings recorded for this fix changed or are missing; pass -Findings <file> again' }
        }
        if ($reviewFindingCount -eq 0 -and -not $coordinatorEntry) { throw "the FAIL verdict carries no finding, so there is nothing to accept or reject. Write the findings yourself and pass them with: fix -Findings <file> (a JSON array of {file, line, summary, scenario[, id, severity]})" }
        if ($accepted.Count -eq 0 -and -not $coordinatorEntry) { throw 'every blocking finding was rejected: there is nothing to fix. Review again, or reassess' }
        $executorInfo = Get-ExecutorIdentity $state
        $kind = $executorInfo.kind
        if (-not $kind) { $kind = if ($executorInfo.provider -eq 'codex') { 'codex' } else { throw 'the executor kind of the last run is unknown' } }
        $fixDir = Join-Path $paths.Dir "fixes/$([guid]::NewGuid().ToString('N'))"
        New-Item -ItemType Directory -Path $fixDir -Force | Out-Null
        $fixLines = @("# Fix request for work package $($state.slug)", '', "The independent review of commit $($review.commit) (round $($review.round)) found the problems below, and the coordinator accepted them. Fix exactly these and keep everything else unchanged. Do not commit or push. Run the checks your environment allows and end with your report.", '')
        foreach ($f in $accepted)
        {
            $fixLines += @("## $($f.id) ($($f.severity)) $($f.file):$($f.line)", "Summary: $($f.summary)", "Scenario: $($f.scenario)", "Coordinator: $($f.disposition.reason)", '')
        }
        if ($coordinatorEntry)
        {
            foreach ($f in @(Get-List $coordinatorEntry.findings))
            {
                $fixLines += @("## $($f.id) ($($f.severity), written by the coordinator) $($f.file):$($f.line)", "Summary: $($f.summary)", "Scenario: $($f.scenario)", "Coordinator: the verdict failed a criterion without naming a finding; this one is the coordinator's own (list sha256 $(([string]$coordinatorEntry.sha256).Substring(0, 12)))", '')
            }
        }
        $fixIds = @(@($accepted | ForEach-Object { $_.id }) + @(Get-List (Get-Field $coordinatorEntry findings) | ForEach-Object { $_.id }))
        $fixText = $fixLines -join "`r`n"
        $fixPath = Join-Path $fixDir 'fix-request.md'
        [IO.File]::WriteAllText($fixPath, $fixText, [Text.UTF8Encoding]::new($false))
        $lastRun = $executorInfo.run
        $fixModel = [string](Get-Field $lastRun 'model')
        $fixEffort = [string](Get-Field $lastRun 'effort')
        $fixEscalation = [string](Get-Field $lastRun 'escalationReason')
        if ($kind -eq 'codex')
        {
            $previous = if ($lastRun.artifacts -and $lastRun.artifacts.outMd -and $lastRun.artifacts.outMd.path -and (Test-Path -LiteralPath $lastRun.artifacts.outMd.path -PathType Leaf)) { Get-Content -LiteralPath $lastRun.artifacts.outMd.path -Raw } else { '(no earlier report was kept)' }
            $briefOverride = Join-Path $fixDir 'brief.md'
            [IO.File]::WriteAllText($briefOverride, ((Get-Content -LiteralPath $state.brief.path -Raw).TrimEnd() + "`r`n`r`n# Previous report`r`n`r`n$previous`r`n`r`n$fixText"), [Text.UTF8Encoding]::new($false))
            $script:BriefOverride = $briefOverride
        }
        $fixReviewId = [string]$review.reviewId
        $fixReviewLevel = [string]$review.level
        $fixReviewerKind = [string]$review.reviewer.kind
        Update-State $info.StateRoot $Id 'fix-started' {
            param($s)
            $s.pendingFix = @{ reviewId = $fixReviewId; utc = (Get-Now); fixRequest = $fixPath; coordinatorFindings = $coordinatorEntry }
            if ($coordinatorFresh) { $s.coordinatorFindings = @(Get-List $s.coordinatorFindings) + @($coordinatorEntry) }
        } -Locked | Out-Null
    }
    finally { $stateLock.Dispose() }

    Write-Host "fix: $($fixIds -join ', ') $(if ($coordinatorEntry) { 'taken (the coordinator wrote the findings)' } else { 'accepted' }); executor $kind$(if ($kind -ne 'codex') { ' (resumed)' } else { ' (new run with the previous report)' })"
    $script:Executor = $kind
    # The latest run of that kind is the one continued, with its own model and effort, not the defaults.
    if ($fixModel) { $script:Model = $fixModel; $script:ModelBound = $true }
    if ($kind -eq 'codex') { if ($fixEffort) { $script:Effort = $fixEffort }; if ($fixEscalation) { $script:EscalationReason = $fixEscalation } }
    if ($kind -ne 'codex') { $script:Resume = $true; $script:PromptFile = $fixPath }
    Invoke-Run
    if ($script:ExitCode -ne 0) { return }
    Invoke-Check
    if ($script:ExitCode -ne 0) { return }
    Invoke-Commit
    if ($script:ExitCode -ne 0) { return }
    $stateNow = Read-State $info.StateRoot $Id
    if ([string]$stateNow.stage -ne 'committed') { Write-Host "NOTE fix stops in stage '$($stateNow.stage)': finish with commit, then review"; return }
    $script:Level = $fixReviewLevel
    $script:Reviewer = $fixReviewerKind
    $script:ResetBudget = $false
    Invoke-Review
}

function Invoke-Fix
{
    $info = Get-RepoInfo $Repo
    $lock = Enter-MainSyncSharedLock $info.CommonDir
    try { Invoke-FixCore }
    finally { $lock.Dispose() }
}

function Invoke-Accept
{
    if ([string]::IsNullOrWhiteSpace($Reason)) { throw 'accept needs -Reason' }
    $info = Get-RepoInfo $Repo
    $stateLock = Enter-StateLock $info.StateRoot $Id
    try
    {
        Sync-Invalidation $info $Id
        $state = Read-State $info.StateRoot $Id
        Assert-StateConsistent $state $info
        $problems = @(Test-SnapshotsIntact $state)
        if ($problems.Count) { throw ($problems -join '; ') }
        if ([string]$state.stage -eq 'accepted') { throw 'the package is already accepted' }
        if ([string]$state.stage -ne 'review-passed') { throw "accept is not allowed in stage '$($state.stage)': it needs a valid PASS review bound to the current head (stage 'review-passed')" }
        $review = Get-LatestReview $state
        if (-not $review -or $review.status -ne 'valid' -or $review.verdict -ne 'PASS' -or [int]$review.blockingFindings -ne 0) { throw 'the latest review is not a valid PASS without blocking findings' }
        $unresolved = @(Get-List $review.findings | Where-Object { $_.severity -eq 'blocking' })
        if ($unresolved.Count) { throw "the latest review still lists blocking finding(s): $(($unresolved | ForEach-Object { $_.id }) -join ', ')" }
        $verdictPath = [string]$review.verdictFile.path
        if (-not (Test-Path -LiteralPath $verdictPath -PathType Leaf) -or (Get-Sha256 $verdictPath) -ne [string]$review.verdictFile.sha256) { throw 'the verdict file is missing or changed since the review' }
        $again = Test-VerdictDocument ([IO.File]::ReadAllText($verdictPath)) $review.criteria ([string]$review.commit) ([string]$review.tree) $true
        if (-not $again.ok -or $again.verdict -ne 'PASS') { throw "the verdict file no longer validates: $($again.problems -join '; ')" }
        $binding = Get-ReviewBinding $state $review
        if (-not $binding.Current) { throw "the review does not cover the branch as it is now: $($binding.Reason)" }
        $wt = [string]$state.worktree
        if (-not (Test-WorktreeClean $wt)) { throw 'the worktree has uncommitted changes' }
        if (-not $binding.PinOnly)
        {
            $stale = Get-CheckFreshnessProblem $state (Get-TreeHash $wt)
            if ($stale) { throw "accept needs a fresh passing check for the reviewed tree: $stale" }
        }
        else
        {
            # The pin files changed the content, so the non-pin content (byte-identical to the reviewed one, proved by the per-file
            # re-hash) needs the passing check that was fresh for the reviewed content.
            $reviewedCheck = @(Get-List $state.checks | Where-Object { $_.checkId -eq $review.checkId })[0]
            if (-not $reviewedCheck -or -not $reviewedCheck.ok -or [string]$reviewedCheck.treeHash -ne [string]$review.contentTree) { throw 'the check that covered the reviewed content is missing or does not match it' }
            $evidence = @(Get-CheckEvidenceProblems $reviewedCheck @(Get-CheckCommands (Read-Manifest $state.manifest.path)))
            if ($evidence.Count) { throw "the check that covered the reviewed content is not complete evidence: $($evidence -join '; ')" }
        }
        # The inputs the review was bound to: the brief and the manifest as recorded at 'new' (re-hashed above), the criteria parsed
        # from the brief again, and the strength and independence of the reviewer as enforced.
        if ([string]$review.briefSha256 -ne [string]$state.brief.sha256 -or [string]$review.manifestSha256 -ne [string]$state.manifest.sha256) { throw 'the brief or check manifest hash recorded at review time differs from the one recorded at new' }
        $criteriaNow = @(Get-ReviewCriteria ([string]$state.brief.path))
        if ((Get-StringSha256 (($criteriaNow | ForEach-Object { "$($_.id)=$($_.text)" }) -join "`n")) -ne [string]$review.criteriaSha256) { throw 'the criteria parsed from the brief differ from the ones the review was bound to' }
        $inputProblems = @(Test-ReviewInputsAtAccept $state $review)
        if ($inputProblems.Count) { throw "the review inputs no longer match what the reviewer was given: $($inputProblems -join '; ')" }
        $catalog = $script:ReviewerCatalog[[string]$review.reviewer.kind]
        if (-not $catalog -or $catalog.model -ne [string]$review.reviewer.model -or ([string]$catalog.effort) -ne ([string]$review.reviewer.effort)) { throw 'the recorded reviewer model or effort is not the one the reviewer kind stands for' }
        if ([string]$review.level -eq 'heavy' -and -not $catalog.heavy) { throw 'a heavy review was run by a reviewer that is not heavy' }
        $conflict = Test-ReviewerConflict $catalog @{ set = @(Get-List $review.executors) }
        if ($conflict) { throw "the reviewer is not independent of the executors: $conflict" }
        $reviewerSession = [string]$review.reviewer.sessionId
        if (-not $reviewerSession) { throw 'the reviewer session is unknown, so the review record cannot name it' }
        $ex = $review.executor
        $executorSession = if ($ex.session) { [string]$ex.session } else { [string]$ex.runId }
        $recordArgs = @('-NoProfile', '-File', $RecordReviewScript, '-Root', [string]$info.MainCheckout, '-Worktree', $wt, '-Head', [string]$review.commit,
            '-Executor', "$($ex.provider):$($ex.model)", '-ExecutorSession', $executorSession,
            '-Reviewer', "$($review.reviewer.provider):$($review.reviewer.model)", '-ReviewerSession', $reviewerSession,
            '-Verdict', 'PASS', '-BlockingFindings', '0', '-Evidence', $verdictPath, '-Brief', [string]$state.brief.path, '-Review', [string]$review.level)
        $recordOutput = @(& pwsh @recordArgs 2>&1 | ForEach-Object { "$_" })
        if ($LASTEXITCODE -ne 0) { throw "record-review.ps1 refused (exit $LASTEXITCODE): $($recordOutput -join ' ')" }
        $recordPath = Join-Path $info.CommonDir "$($script:Namespace)/reviews/$($review.commit).json"
        if (-not (Test-Path -LiteralPath $recordPath -PathType Leaf)) { throw "record-review.ps1 reported success but $recordPath does not exist" }
        $acceptance = @{ utc = (Get-Now); reason = $Reason.Trim(); reviewId = [string]$review.reviewId; commit = [string]$review.commit; tree = [string]$review.tree; head = $binding.Head; pinOnly = [bool]$binding.PinOnly; recordPath = $recordPath; recordSha256 = (Get-Sha256 $recordPath); invalidated = $null }
        Update-State $info.StateRoot $Id 'accepted' { param($s) $s.acceptance = $acceptance; $s.stage = 'accepted' } -Locked | Out-Null
        Write-Host "accepted: review $($review.reviewId) (PASS) is bound to $($review.commit)$(if ($binding.PinOnly) { " and the plugin-pin-only head $($binding.Head)" }); review record $recordPath"
    }
    finally { $stateLock.Dispose() }
}

