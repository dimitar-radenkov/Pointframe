# ---- run --------------------------------------------------------------------------------------------------------

function Test-SelfTestStub([string]$Path)
{
    # Fault injection is only ever accepted together with an executor script the self-test wrote and marked.
    if (-not $Path -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $false }
    ((Get-Content -LiteralPath $Path -TotalCount 1) -eq '# aw-selftest-stub')
}

function Resolve-PriorRun($Info, [string]$Slug)
{
    # A package left in stage 'running' (its wrapper died) or 'executor-kill-failed' owns an executor that may still be
    # alive. Under the package lock for the whole recovery: terminate it and verify it is gone, reconcile the main
    # checkout against the fingerprint saved before that run, write the result. A second caller that arrives later sees
    # a stage that no longer needs recovery and does nothing, so it can never clear another run's ownership.
    $stateLock = Enter-StateLock $Info.StateRoot $Slug
    try
    {
        $State = Read-State $Info.StateRoot $Slug
        if ($State.stage -notin @('running', 'executor-kill-failed')) { return }
        $currentId = if ($State.currentRun) { [string]$State.currentRun.runId } else { '' }
        $run = @($State.runs | Where-Object { $_.runId -eq $currentId }) | Select-Object -First 1
        if (-not $run) { $run = @($State.runs) | Select-Object -Last 1 }
        $wrapper = $run.wrapper
        if ($wrapper -and (Test-ProcessIdentityAlive ([int]$wrapper.pid) ([string]$wrapper.startUtc)) -and [int]$wrapper.pid -ne $PID)
        {
            throw "run $($run.runId) is still in progress (pid $($wrapper.pid))"
        }
        $reaped = $false
        $executor = $run.executor
        if ($executor -and (Test-ProcessIdentityAlive ([int]$executor.pid) ([string]$executor.startUtc)))
        {
            if (-not (Stop-ProcessTreeById ([int]$executor.pid) 10))
            {
                throw "the executor of run $($run.runId) (pid $($executor.pid)) or one of its children is still alive and could not be terminated; the package stays blocked"
            }
            $reaped = $true
        }
        $now = Get-Fingerprint ([string]$State.repository.mainCheckout)
        $saved = $run.mainCheckoutBefore
        $mutated = -not (Test-FingerprintsEqual $saved $now)
        $runIdToFix = [string]$run.runId
        # A Claude run's tokens, session and elapsed time are rebuilt from its archived stream, or marked unknown (which
        # blocks later resumes of its chain); they are never silently dropped from the cumulative budget.
        $reconstructed = if ((Get-Field $run 'provider') -eq 'claude' -and $null -eq (Get-Field $run 'tokens')) { Get-ReconstructedClaudeAccounting $run } else { $null }
        Update-State $Info.StateRoot $Slug 'run-recovered' {
            param($s)
            $s.runs = @($s.runs | ForEach-Object {
                    if ($_.runId -eq $runIdToFix)
                    {
                        if ($_.termination -eq 'running') { $_.termination = 'abandoned' }
                        $_.outcome = 'impl-failed'
                        $_.reason = "the run was interrupted; the executor was reaped: $reaped"
                        $_.endedUtc = (Get-Now)
                        if ($mutated) { $_.mainCheckoutAfter = $now; $_.mainCheckoutMutated = $true }
                        if ($reconstructed)
                        {
                            $_.tokens = $reconstructed.tokens; $_.sessionId = $reconstructed.sessionId; $_.sessionIdentityProblem = $reconstructed.sessionIdentityProblem; $_.elapsedSeconds = $reconstructed.elapsedSeconds
                            $_.permissionDenials = @($reconstructed.denials); $_.accounting = $reconstructed.accounting
                        }
                    }
                    $_
                })
            $s.currentRun = $null
            if ($mutated) { $s.guard = @{ mainCheckoutMutated = $true; lastRunId = $runIdToFix; before = $saved; after = $now } }
            $s.stage = if ($mutated) { 'blocked-main-mutated' } else { 'executor-impl-failed' }
        } -Locked | Out-Null
        if ($mutated)
        {
            $script:ExitCode = 5
            throw 'the main checkout changed (or could not be fingerprinted) since the interrupted run started; nothing was reverted and the package is stopped'
        }
    }
    finally { $stateLock.Dispose() }
}

function Stop-ExecutorTree([IntPtr]$Job, [bool]$SimulateFailure)
{
    # SimulateFailure is only ever true when -SimulateKillFailure was given together with a marked self-test stub.
    if ($SimulateFailure) { return $false }
    Stop-ProcessJob $Job 10
}

function Invoke-RunCore
{
    if ($Effort -eq 'high' -and [string]::IsNullOrWhiteSpace($EscalationReason)) { throw '-Effort high requires -EscalationReason' }
    if ($Executor -eq 'same') { throw "-Executor same is only meaningful for 'fix'; run needs codex, claude-sonnet or claude-haiku" }
    $simulateKillFailure = [bool]$SimulateKillFailure
    if ($simulateKillFailure -and -not (Test-SelfTestStub $AgentRunScript)) { throw '-SimulateKillFailure is only accepted together with a marked self-test stub executor' }
    $info = Get-RepoInfo $Repo
    $state = Read-State $info.StateRoot $Id
    Assert-StateConsistent $state $info
    $paths = Get-PackagePaths $info.StateRoot $Id
    $problems = @(Test-SnapshotsIntact $state)
    if ($problems.Count) { throw ($problems -join '; ') }
    if ($state.stage -eq 'blocked-main-mutated') { throw 'the main checkout was mutated by an earlier run; resolve it by hand, then record that with: recover -Id <slug> -Reason <text>. Until then this package stays stopped' }
    if ($state.stage -in @('shipping', 'desktop-required', 'no-remote', 'ship-failed', 'auto-merge-enabled', 'merged')) { throw "run is not allowed in stage '$($state.stage)': the package is being shipped" }
    if ($state.stage -in @('running', 'executor-kill-failed'))
    {
        Resolve-PriorRun $info $Id
        $state = Read-State $info.StateRoot $Id
    }
    $runId = [guid]::NewGuid().ToString('N')
    $runDir = Join-Path $paths.Dir "runs/$runId"
    $wt = [string]$state.worktree
    $mainCheckout = [string]$state.repository.mainCheckout
    $isClaude = $Executor -ne 'codex'
    if ($Resume -and -not $isClaude) { throw '-Resume is only supported for the Claude executors' }
    if ($PromptFile -and -not $isClaude) { throw '-PromptFile is only supported for the Claude executors' }
    if ($env:AW_PKG_SELFTEST -and $Executor -eq 'codex' -and -not (Test-SelfTestStub $AgentRunScript)) { throw 'self-test guard: a Codex executor run needs a marked -AgentRunScript stub' }
    $claude = if ($isClaude) { Initialize-ClaudeRun $state $runId $runDir } else { $null }
    $remainingSeconds = if ($isClaude) { [double]$claude.Budget.remainingSeconds } else { $MaxMinutes * 60 }
    $remainingTokens = if ($isClaude) { [long]$claude.Budget.remainingInputTokens } else { $MaxInputTokens }

    $before = Get-Fingerprint $mainCheckout
    if (-not $before.treeHash) { throw "the main checkout cannot be fingerprinted (unreadable: $($before.unreadable -join ', ')); the run was not started" }
    $scopeBefore = if ($isClaude -and $claude.Scope) { Get-WorktreeSnapshot $wt } else { $null }
    $started = Get-Now
    $permissions = if ($isClaude) { @{ mode = 'default'; bypass = $false; settingsFile = $claude.Settings; settingSources = 'none (only the runner settings file)'; mcp = 'strict, empty'; tools = $claude.Tools; shell = 'git reads, kb.ps1 init|read|check|changed|selftest, verify.ps1, brief-named scripts, ' + (Get-PackageCommandSummary) + ' (executors)' } }
    else { @{ sandbox = 'workspace-write'; approval = 'never'; windowsSandbox = 'unelevated' } }
    $cliVersion = if ($isClaude) { $null } else { Get-CodexCliVersion }
    $self = @{ pid = $PID; startUtc = (Get-ProcessStartIdentity $PID) }
    $runRecord = @{
        runId = $runId; seq = 0; startedUtc = $started; endedUtc = $null; provider = $(if ($isClaude) { 'claude' } else { 'codex' }); executorKind = $Executor; model = $(if ($isClaude) { $claude.Model } else { $Model })
        effort = $(if ($isClaude) { $null } else { $Effort }); escalationReason = $(if ($EscalationReason) { $EscalationReason } else { $null }); maxMinutes = $MaxMinutes; maxInputTokens = $MaxInputTokens; permissions = $permissions; codexCliVersion = $cliVersion
        wrapper = $self; executor = $null; sessionId = $null; exitCode = $null; termination = 'running'; outcome = 'running'; reason = $null
        envBlocked = $false; envEvidence = $null; tokens = $null; containmentVerified = $null; containmentReason = $null; logDirectory = $runDir
        artifacts = @{ outMd = $null; eventsJsonl = $null }; mainCheckoutBefore = $before; mainCheckoutAfter = $null; mainCheckoutMutated = $false; chainId = $(if ($isClaude) { $claude.ChainId } else { $runId })
    }
    if ($isClaude)
    {
        $runRecord.claudeCliVersion = $claude.Version
        $runRecord.claudeCliVersionProblem = $claude.VersionProblem
        $runRecord.authMode = $claude.AuthMode
        $runRecord.claudeArguments = $claude.RecordedArguments
        $runRecord.claudeExecutable = $claude.Launcher.Display
        $runRecord.prompt = $claude.Prompt
        $runRecord.resumedFrom = $claude.ResumedFrom
        $runRecord.budget = $claude.Budget
        $runRecord.haikuScope = $claude.Scope
        $runRecord.permissionDenials = @()
    }
    Update-State $info.StateRoot $Id 'run-started' {
        param($s)
        if ($s.stage -notin @('ready', 'executor-completed', 'executor-env-blocked', 'executor-impl-failed', 'checks-passed', 'checks-failed', 'review-failed')) { throw "run is not allowed in stage '$($s.stage)'" }
        $s.currentRun = @{ runId = $runId; pid = $PID }
        $runRecord.seq = (@($s.runs).Count + 1)
        $s.runs = @($s.runs) + @($runRecord)
        $s.stage = 'running'
    } | Out-Null

    $briefForRun = if ($script:BriefOverride) { $script:BriefOverride } else { [string]$state.brief.path }
    $agentArgs = @('-NoProfile', '-File', "`"$AgentRunScript`"", '-Brief', "`"$briefForRun`"", '-Worktree', "`"$wt`"", '-Model', $Model, '-Effort', $Effort,
        '-MaxMinutes', ([string][int][Math]::Ceiling($MaxMinutes)), '-MaxInputTokens', ([string]$MaxInputTokens), '-Sandbox', $(if ($isClaude) { '' } else { $permissions.sandbox }),
        '-Task', $Id, '-LogDirectory', "`"$runDir`"")
    if ($EscalationReason) { $agentArgs += @('-EscalationReason', "`"$($EscalationReason -replace '"', '\"')`"") }
    New-Item -ItemType Directory -Path $runDir -Force | Out-Null
    Initialize-ProcessJob
    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    $job = [AwProcessJob]::Create()
    try
    {
        if ($job -eq [IntPtr]::Zero) { throw 'cannot create a job object; an uncontained executor is never started' }
        # The executor is created suspended, assigned to the kill-on-close job and only then resumed, so it cannot spawn a
        # child outside the job. Accepted limitation: if this script dies between creation and assignment, that one
        # suspended process (which never ran) is not contained.
        if ($isClaude)
        {
            # stdin is the prompt file: the executor reads it to its end and gets EOF, so it can never wait for input. The
            # working directory is the worktree (--add-dir would not relocate it). stdout is the stream log, stderr its own file.
            $launchArgs = @($claude.Launcher.Prefix) + @($claude.Arguments | ForEach-Object { Format-WindowsArgument ([string]$_) })
            $process = Start-ProcessInJob $job $claude.Launcher.File $launchArgs $wt $claude.Stdin (Join-Path $runDir 'events.jsonl') (Join-Path $runDir 'claude.err')
        }
        else
        {
            $process = Start-ProcessInJob $job (Get-Command pwsh -CommandType Application | Select-Object -First 1).Source $agentArgs $wt $null (Join-Path $runDir 'agent-run.log') (Join-Path $runDir 'agent-run.err')
        }
    }
    catch
    {
        $launchError = $_.Exception.Message
        if ($job -ne [IntPtr]::Zero) { [AwProcessJob]::Close($job) }
        Update-State $info.StateRoot $Id 'run-launch-failed' {
            param($s)
            $s.runs = @($s.runs | ForEach-Object { if ($_.runId -eq $runId) { $_.termination = 'launch-failed'; $_.outcome = 'impl-failed'; $_.reason = $launchError; $_.endedUtc = (Get-Now) }; $_ })
            $s.currentRun = $null
            $s.stage = 'executor-impl-failed'
        } | Out-Null
        throw
    }
    $executorIdentity = @{ pid = $process.Id; startUtc = (Get-ProcessStartIdentity $process.Id); jobObject = $true }
    Update-State $info.StateRoot $Id 'executor-started' {
        param($s)
        $s.runs = @($s.runs | ForEach-Object { if ($_.runId -eq $runId) { $_.executor = $executorIdentity }; $_ })
    } | Out-Null

    $limitMs = [long]($remainingSeconds * 1000 + $GraceSeconds * 1000)
    $monitor = $null
    $budgetHit = $false
    if ($isClaude)
    {
        # Polled from outside the process: the stream is read as it grows, so the token budget is enforced while the
        # executor runs, not only after it ends.
        $monitor = New-StreamMonitor (Join-Path $runDir 'events.jsonl')
        $deadline = [DateTime]::UtcNow.AddMilliseconds($limitMs)
        $finished = $false
        while ($true)
        {
            $slice = [long][Math]::Min(250, [Math]::Max(1, ($deadline - [DateTime]::UtcNow).TotalMilliseconds))
            if (Wait-ProcessExit $process $slice) { $finished = $true; break }
            Read-StreamMonitor $monitor
            if ((Get-MonitorEnforcedInput $monitor) -gt $remainingTokens) { $budgetHit = $true; break }
            if ([DateTime]::UtcNow -ge $deadline) { break }
        }
    }
    else { $finished = Wait-ProcessExit $process $limitMs }
    $termination = 'exit'
    $containment = $null
    if (-not $finished)
    {
        # The bound is enforced and the result checked: the tree must be verifiably gone, else the state says so.
        $stopped = Stop-ExecutorTree $job $simulateKillFailure
        $termination = if (-not $stopped) { 'kill-failed' } elseif ($budgetHit) { 'budget' } else { 'timeout' }
        $containment = [pscustomobject]@{ Verified = $stopped; Leftover = 0; Alive = (-not $stopped); Reason = $(if ($stopped) { $null } else { 'the process tree could not be verified gone' }) }
        Wait-ProcessExit $process 5000 | Out-Null
    }
    else
    {
        $containment = Test-JobContainment $job
        if (-not $containment.Verified -and $containment.Alive) { $termination = 'kill-failed' }
    }
    [AwProcessJob]::Close($job)
    $leftover = $containment.Leftover
    $exitCode = if ($termination -in @('timeout', 'kill-failed', 'budget') -or -not (Test-ProcessExited $process)) { $null } else { Get-ProcessExitCode $process }
    [AwProcessJob]::CloseProcess($process.Handle)
    if ($termination -eq 'exit' -and $exitCode -eq 3 -and -not $isClaude) { $termination = 'budget' }
    if ($isClaude)
    {
        Read-StreamMonitor $monitor -Final
        # The final decision: the authoritative (terminal) total, and never less than anything observed while running.
        $finalInput = [Math]::Max((Get-MonitorEnforcedInput $monitor), [long](Get-ClaudeTokenRecord $monitor).input)
        if ($termination -eq 'exit' -and $finalInput -gt $remainingTokens) { $termination = 'budget' }
    }
    $elapsed = [Math]::Round($stopwatch.Elapsed.TotalSeconds, 1)

    $after = Get-Fingerprint $mainCheckout
    $mutated = -not (Test-FingerprintsEqual $before $after)
    $outMd = Join-Path $runDir 'out.md'
    $events = Join-Path $runDir 'events.jsonl'
    if ($isClaude -and $monitor.ResultSeen -and $monitor.ResultText) { [IO.File]::WriteAllText($outMd, $monitor.ResultText, [Text.UTF8Encoding]::new($false)) }
    $report = if (Test-Path -LiteralPath $outMd -PathType Leaf) { Get-Content -LiteralPath $outMd -Raw } else { '' }
    $missing = @(@($outMd, $events) | Where-Object { -not (Test-Path -LiteralPath $_ -PathType Leaf) -or (Get-Item -LiteralPath $_).Length -eq 0 } | ForEach-Object { Split-Path $_ -Leaf })
    $containmentProblem = if ($containment.Verified) { '' } else { [string]$containment.Reason }
    $extraFailure = ''
    $denials = @()
    $scopeViolations = @()
    if ($isClaude)
    {
        $denials = @($monitor.Denials)
        if ($denials.Count) { $extraFailure = "permission denied for $((@($denials | ForEach-Object { if ($_.tool) { $_.tool } else { 'a tool' } }) | Select-Object -Unique) -join ', ') ($($denials.Count) denial(s)); the runner never retries with broader permissions" }
        elseif ($monitor.ResultError) { $extraFailure = "the executor's result reported an error ($($monitor.ResultSubtype))" }
        if ($claude.Scope) { $scopeViolations = @(Get-ScopeViolations $scopeBefore (Get-WorktreeSnapshot $wt) $claude.Scope @($claude.Allowed)) }
    }
    $classification = Get-RunClassification $exitCode $termination $report ($missing.Count -eq 0) ($missing -join ', ') $containmentProblem $extraFailure
    # The session must be named by the init event; a resumed run must come back with the id it resumed.
    $identityProblem = $null
    if ($isClaude)
    {
        # Whatever the termination: a session id that did not come from the init event, or that differs from the one this run
        # resumed, is not recorded, so the run can never become a resume candidate.
        if (-not $monitor.SessionId) { $identityProblem = 'the stream has no init event with a session id' }
        elseif ($claude.ResumedFrom -and $monitor.SessionId -ne $claude.ResumedFrom.sessionId) { $identityProblem = "the resumed session answered with id $($monitor.SessionId), not $($claude.ResumedFrom.sessionId)" }
        if ($identityProblem)
        {
            $classification.outcome = 'impl-failed'
            $classification.reason = if ($termination -eq 'exit') { "session identity: $identityProblem" } else { "$($classification.reason); session identity: $identityProblem" }
        }
    }
    if ($scopeViolations.Count)
    {
        $classification.outcome = 'impl-failed'
        $classification.reason = "scope: Haiku-scope $($claude.Scope) does not allow changes to $(($scopeViolations | Select-Object -First 10) -join ', ')"
    }
    $session = if ($identityProblem) { $null } elseif ($isClaude) { $monitor.SessionId } else { Get-SessionIdFromEvents $events }
    $tokens = if ($isClaude) { Get-ClaudeTokenRecord $monitor } else { Get-EventTokens $events }
    $stage = if ($mutated) { 'blocked-main-mutated' } elseif ($termination -eq 'kill-failed') { 'executor-kill-failed' } else { "executor-$($classification.outcome)" }
    Update-State $info.StateRoot $Id 'run-finished' {
        param($s)
        $s.runs = @($s.runs | ForEach-Object {
                if ($_.runId -eq $runId)
                {
                    $_.endedUtc = (Get-Now); $_.sessionId = $session; $_.exitCode = $exitCode; $_.termination = $termination; $_.outcome = $classification.outcome
                    $_.reason = $classification.reason; $_.envBlocked = $classification.envBlocked; $_.envEvidence = $classification.envEvidence
                    $_.containmentVerified = [bool]$containment.Verified; $_.containmentReason = $containment.Reason
                    $_.elapsedSeconds = $elapsed; $_.leftoverProcesses = $leftover; $_.tokens = $tokens
                    $_.artifacts = @{ outMd = @{ path = $outMd; sha256 = (Get-OptionalSha256 $outMd) }; eventsJsonl = @{ path = $events; sha256 = (Get-OptionalSha256 $events) } }
                    $_.mainCheckoutAfter = $after; $_.mainCheckoutMutated = $mutated
                    if ($isClaude)
                    {
                        $_.sessionIdentityProblem = $identityProblem
                        $_.permissionDenials = @($denials); $_.scopeViolations = @($scopeViolations); $_.reportedCwd = $monitor.Cwd; $_.reportedModel = $monitor.Model
                        $_.reportedCostUsd = $monitor.CostUsd; $_.costNote = 'reported by the CLI, not an invoice'
                    }
                }
                $_
            })
        $s.currentRun = if ($termination -eq 'kill-failed') { $s.currentRun } else { $null }
        $s.guard = @{ mainCheckoutMutated = ($mutated -or [bool]$s.guard.mainCheckoutMutated); lastRunId = $runId; before = $before; after = $after }
        $s.stage = $stage
    } | Out-Null

    Write-Host "run ${runId}: $($classification.outcome) (termination=$termination exit=$exitCode containment=$(if ($containment.Verified) { 'verified' } else { 'UNVERIFIED' }) session=$session$(if ($isClaude) { " denials=$($denials.Count)" }) $elapsed s) log=$runDir"
    if ($mutated)
    {
        $script:ExitCode = 5
        $why = if (-not $after.treeHash) { "the main checkout could not be fingerprinted after the run (unreadable: $($after.unreadable -join ', ')), so a change cannot be ruled out" } else { "the main checkout changed during the run (HEAD $($before.head) -> $($after.head), tree $($before.treeHash) -> $($after.treeHash))" }
        Write-Host "ERROR $why. Nothing was reverted; the package is stopped. Inspect 'git status' in $mainCheckout."
        return
    }
    if ($classification.outcome -ne 'completed' -or $termination -ne 'exit') { $script:ExitCode = 1 }
}

function Invoke-Run
{
    $info = Get-RepoInfo $Repo
    $lock = Enter-MainSyncSharedLock $info.CommonDir
    try { Invoke-RunCore }
    finally { $lock.Dispose() }
}

