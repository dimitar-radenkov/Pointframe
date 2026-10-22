# ---- review: reviewer processes -----------------------------------------------------------------------------------

function New-ClaudeReviewerSettings([string]$Path)
{
    # Read, Glob and Grep and nothing else: no Bash at all, so no command (and no git option such as --output or an external diff
    # program) can write or run anything. The host prepares the review input files beforehand.
    $deny = @('Bash', 'Edit', 'Write', 'WebFetch', 'WebSearch', 'Agent', 'Task', 'NotebookEdit', 'PowerShell')
    $settings = [ordered]@{ permissions = [ordered]@{ defaultMode = 'default'; disableBypassPermissionsMode = 'disable'; allow = @('Read', 'Glob', 'Grep'); deny = $deny } }
    New-Item -ItemType Directory -Path (Split-Path $Path -Parent) -Force | Out-Null
    [IO.File]::WriteAllText($Path, (($settings | ConvertTo-Json -Depth 6) + "`r`n"), [Text.UTF8Encoding]::new($false))
}

function Get-ClaudeReviewerRules([string]$Snapshot, [string]$InputDir)
{
    @(
        "You are the independent reviewer of one work package. Your working directory is a read-only snapshot of the reviewed commit ($Snapshot); you change nothing.",
        "You only read, with Read, Glob and Grep. You have no shell. The host prepared the review inputs in $InputDir (the commit log, the stat, the full diff, the brief, the check summary and the executor's report).",
        'If a tool call is denied, do not look for a way around it: list what you could not inspect under omissions.',
        'Never mark a criterion pass that you did not verify; use not-checked and say why in the evidence.',
        'Your final message is the verdict document and nothing else: exactly the JSON object the prompt describes, no prose before or after it.'
    ) | Join-String -Separator ' '
}
function Invoke-ClaudeReviewProcess([string]$Wt, [string]$ReviewDir, [string]$PromptPath, $Chosen, [string]$InputDir, $AdmittedLauncher = $null, [string]$AdmittedVersion = '')
{
    # A Claude reviewer is never resumed: the session it would continue ran in a snapshot that has been removed, so every round starts
    # fresh and the prompt carries what the earlier round found. The launcher and version are the ones the round was admitted with.
    $launcher = if ($AdmittedLauncher) { $AdmittedLauncher } else { Resolve-ClaudeLauncher }
    $version = if ($AdmittedLauncher) { $AdmittedVersion } else { Get-ClaudeCliVersion $launcher }
    $settingsPath = Join-Path $ReviewDir 'claude-settings.json'
    $mcpPath = Join-Path $ReviewDir 'empty-mcp.json'
    New-ClaudeReviewerSettings $settingsPath
    [IO.File]::WriteAllText($mcpPath, "{`"mcpServers`":{}}`r`n", [Text.UTF8Encoding]::new($false))
    $rules = Get-ClaudeReviewerRules $Wt $InputDir
    $arguments = @('-p', '--model', $Chosen.model, '--output-format', 'stream-json', '--verbose', '--settings', $settingsPath, '--setting-sources', '',
        '--tools', 'Read,Glob,Grep', '--add-dir', $InputDir, '--strict-mcp-config', '--mcp-config', $mcpPath, '--append-system-prompt', $rules)
    if ($MaxBudgetUsd -gt 0) { $arguments += @('--max-budget-usd', $MaxBudgetUsd.ToString([Globalization.CultureInfo]::InvariantCulture)) }
    Assert-NoBypass $arguments
    $events = Join-Path $ReviewDir 'events.jsonl'
    Initialize-ProcessJob
    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    $job = [AwProcessJob]::Create()
    if ($job -eq [IntPtr]::Zero) { throw 'cannot create a job object; an uncontained reviewer is never started' }
    $launchArgs = @($launcher.Prefix) + @($arguments | ForEach-Object { Format-WindowsArgument ([string]$_) })
    $process = Start-ProcessInJob $job $launcher.File $launchArgs $Wt $PromptPath $events (Join-Path $ReviewDir 'claude.err')
    $monitor = New-StreamMonitor $events
    $deadline = [DateTime]::UtcNow.AddMilliseconds([long]($MaxMinutes * 60000 + $GraceSeconds * 1000))
    $finished = $false
    $budgetHit = $false
    while ($true)
    {
        $slice = [long][Math]::Min(250, [Math]::Max(1, ($deadline - [DateTime]::UtcNow).TotalMilliseconds))
        if (Wait-ProcessExit $process $slice) { $finished = $true; break }
        Read-StreamMonitor $monitor
        if ((Get-MonitorEnforcedInput $monitor) -gt $MaxInputTokens) { $budgetHit = $true; break }
        if ([DateTime]::UtcNow -ge $deadline) { break }
    }
    $termination = 'exit'
    if (-not $finished)
    {
        $stopped = Stop-ProcessJob $job 10
        $termination = if (-not $stopped) { 'kill-failed' } elseif ($budgetHit) { 'budget' } else { 'timeout' }
        $containment = [pscustomobject]@{ Verified = $stopped; Reason = $(if ($stopped) { $null } else { 'the process tree could not be verified gone' }) }
        Wait-ProcessExit $process 5000 | Out-Null
    }
    else
    {
        $containment = Test-JobContainment $job
        if (-not $containment.Verified -and $containment.Alive) { $termination = 'kill-failed' }
    }
    [AwProcessJob]::Close($job)
    $exitCode = if ($termination -ne 'exit' -or -not (Test-ProcessExited $process)) { $null } else { Get-ProcessExitCode $process }
    [AwProcessJob]::CloseProcess($process.Handle)
    Read-StreamMonitor $monitor -Final
    $tokens = Get-ClaudeTokenRecord $monitor
    if ($termination -eq 'exit' -and [Math]::Max((Get-MonitorEnforcedInput $monitor), [long]$tokens.input) -gt $MaxInputTokens) { $termination = 'budget' }
    $identityProblem = $null
    if (-not $monitor.SessionId) { $identityProblem = 'the stream has no init event with a session id' }
    # A host-detected environment failure (not the reviewer's own output): the CLI died before it said anything, or said that it cannot serve
    # this model. Such a round is refunded.
    $infra = $null
    if ($termination -eq 'exit' -and $exitCode -ne 0 -and -not $monitor.ResultSeen) { $infra = "the claude CLI exited with code $exitCode before any verdict (a process crash, not a verdict)" }
    elseif ($termination -eq 'exit' -and $monitor.ResultSeen -and $monitor.ResultError -and [string]$monitor.ResultText -match '(?i)newer (version|claude)|update (claude|the cli)|upgrade (claude|the cli)|unknown model|model .{0,60}(not found|not available|not supported)|issue with the selected model') { $infra = "the claude CLI refused the model: $(Get-ShortText ([string]$monitor.ResultText) 200)" }
    @{
        Termination = $termination; ExitCode = $exitCode; ContainmentVerified = [bool]$containment.Verified; ContainmentReason = $containment.Reason; InfraFailure = $infra
        SessionId = $(if ($identityProblem) { $null } else { $monitor.SessionId }); IdentityProblem = $identityProblem; FinalText = $(if ($monitor.ResultSeen) { $monitor.ResultText } else { $null }); ResultError = $monitor.ResultError
        Denials = @($monitor.Denials); Tokens = $tokens; Seconds = [Math]::Round($stopwatch.Elapsed.TotalSeconds, 1); CliVersion = $version
        Permissions = @{ tools = 'Read,Glob,Grep'; addDir = $InputDir; settingsFile = @{ path = $settingsPath; sha256 = (Get-Sha256 $settingsPath) }; shell = 'none'; mode = 'default' }
        Executable = $launcher.Display
    }
}

function Invoke-CodexReviewProcess([string]$Wt, [string]$ReviewDir, [string]$PromptPath, $Chosen, [string]$ResumeSession)
{
    # codex-ask.ps1 runs Codex in a job, read-only, bounded, and writes <out>.result.json; the final message is the out file.
    if ($CodexTestScript -and -not (Test-SelfTestStub $CodexTestScript)) { throw '-CodexTestScript may only be a marked self-test stub' }
    if ($env:AW_PKG_SELFTEST -and -not $CodexTestScript) { throw 'self-test guard: a Codex reviewer needs a marked -CodexTestScript stub' }
    $final = Join-Path $ReviewDir 'final.md'
    Remove-Item -LiteralPath $final, "$final.result.json" -Force -ErrorAction SilentlyContinue
    $askArgs = @('-NoProfile', '-File', $CodexAskScript, '-PromptFile', $PromptPath, '-OutFile', $final, '-Model', [string]$Chosen.model, '-Effort', [string]$Chosen.effort, '-Sandbox', 'read-only',
        '-Worktree', $Wt, '-MaxMinutes', $MaxMinutes.ToString([Globalization.CultureInfo]::InvariantCulture), '-RunRecordPath', (Join-Path $ReviewDir 'agent-runs.jsonl'))
    if ($ResumeSession) { $askArgs += @('-SessionId', $ResumeSession) }
    if ($CodexTestScript) { $askArgs += @('-TestCodexScript', $CodexTestScript) }
    Push-Location -LiteralPath $Wt
    try { $output = @(& pwsh @askArgs 2>&1 | ForEach-Object { "$_" }); $exit = $LASTEXITCODE }
    finally { Pop-Location }
    $output | Set-Content -LiteralPath (Join-Path $ReviewDir 'codex-ask.log') -Encoding utf8
    $resultPath = "$final.result.json"
    $result = if (Test-Path -LiteralPath $resultPath -PathType Leaf) { Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json -AsHashtable @script:JsonDate } else { $null }
    $termination = if (-not $result) { 'failed' } elseif ($result.timedOut) { 'timeout' } elseif ($exit -ne 0 -or $result.outcome -ne 'completed') { 'failed' } else { 'exit' }
    if ($result -and $result.timedOut -and $result.terminated -ne $true) { $termination = 'kill-failed' }
    $tokens = if ($result -and $result.tokens) { @{ input = $result.tokens.inputTokens; cached = $result.tokens.cachedInputTokens; output = $result.tokens.outputTokens } } else { $null }
    # A host-detected environment failure: Codex failed before it wrote any final message (a resume that Codex could not continue included).
    $finalText = if (Test-Path -LiteralPath $final -PathType Leaf) { [IO.File]::ReadAllText($final) } else { $null }
    # Only a process that did not exit 0, or never produced a result, is the environment's failure. Exit 0 with an empty final message is the
    # reviewer's own (empty) output: invalid and charged, like Claude's empty result. "Resume failure" is said only when the host saw the resume
    # fail: Codex exited non-zero without a single event (no session id) after being told to resume.
    $infra = $null
    $finalEmpty = [string]::IsNullOrWhiteSpace($finalText)
    if ($finalEmpty -and $termination -eq 'failed')
    {
        if (-not $result) { $infra = "codex-ask.ps1 exited $exit without a result file, before any verdict" }
        elseif ($null -ne $result.exitCode -and $result.exitCode -ne 0)
        {
            $infra = if ($ResumeSession -and -not $result.sessionId) { "resume failure: codex exited with code $($result.exitCode) without any event after resuming session $ResumeSession" } else { "codex exited with code $($result.exitCode) before any verdict" }
        }
    }
    @{
        InfraFailure = $infra
        Termination = $termination; ExitCode = $(if ($result) { $result.exitCode } else { $exit }); ContainmentVerified = [bool]($result -and $result.containmentVerified); ContainmentReason = $(if ($result) { $result.containmentReason } else { "codex-ask.ps1 exit $exit without a result file: $(($output | Select-Object -Last 2) -join ' ')" })
        SessionId = $(if ($result -and $result.sessionId) { [string]$result.sessionId } else { $null }); IdentityProblem = $null
        FinalText = $(if (Test-Path -LiteralPath $final -PathType Leaf) { [IO.File]::ReadAllText($final) } else { $null }); ResultError = $false
        Denials = @(); Tokens = $tokens; Seconds = $(if ($result) { $result.elapsedSeconds } else { $null }); CliVersion = $null
        Permissions = @{ sandbox = 'read-only'; approval = 'never' }; Executable = "codex-ask.ps1 -Sandbox read-only"
    }
}

