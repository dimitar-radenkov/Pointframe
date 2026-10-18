#Requires -Version 7.0
[CmdletBinding()]
param(
    [string]$PromptFile,
    [string]$OutFile,
    [string]$Model,
    [ValidateSet('low', 'medium', 'high', 'xhigh')][string]$Effort,
    [ValidateSet('read-only', 'workspace-write')][string]$Sandbox = 'read-only',
    [string]$SessionId,
    [string]$Worktree = (Split-Path $PSScriptRoot -Parent),
    [string]$EscalationReason,
    [string]$TestCodexScript,
    [string]$RunRecordPath,
    [ValidateRange(0.01, 1440)]
    [double]$MaxMinutes = 30,
    [switch]$SimulateKillFailure,
    [switch]$SelfTest
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'lib/codex.ps1')
. (Join-Path $PSScriptRoot 'lib/process-tree.ps1')

function Test-ConsultOutput([string]$Path, [DateTimeOffset]$Started)
{
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return [pscustomobject]@{ Ok = $false; Reason = 'Codex did not create the output file.' } }
    $item = Get-Item -LiteralPath $Path
    if ($item.Length -eq 0) { return [pscustomobject]@{ Ok = $false; Reason = 'Codex output file is empty.' } }
    if ($item.LastWriteTimeUtc -lt $Started.UtcDateTime) { return [pscustomobject]@{ Ok = $false; Reason = 'Codex output file is stale.' } }
    [pscustomobject]@{ Ok = $true; Reason = 'output is fresh' }
}

function Get-SessionId([string[]]$Lines)
{
    foreach ($line in $Lines)
    {
        if (-not $line) { continue }
        try { $event = $line | ConvertFrom-Json } catch { continue }
        if ($null -eq $event) { continue }
        if ($event.PSObject.Properties['thread_id'] -and $event.thread_id) { return [string]$event.thread_id }
        if ($event.PSObject.Properties['session_id'] -and $event.session_id) { return [string]$event.session_id }
        if ($event.PSObject.Properties['type'] -and $event.type -eq 'session.started' -and
            $event.PSObject.Properties['sessionId'] -and $event.sessionId) { return [string]$event.sessionId }
    }
    ''
}

function Get-TokenFields([string[]]$Lines)
{
    # Sums the usage of every turn.completed event. A field no event reported stays null, never 0.
    $sums = @{ inputTokens = $null; cachedInputTokens = $null; outputTokens = $null; reasoningOutputTokens = $null }
    $map = @{ inputTokens = 'input_tokens'; cachedInputTokens = 'cached_input_tokens'; outputTokens = 'output_tokens'; reasoningOutputTokens = 'reasoning_output_tokens' }
    foreach ($line in $Lines)
    {
        if (-not $line) { continue }
        try { $item = $line | ConvertFrom-Json } catch { continue }
        if (-not $item.PSObject.Properties['type'] -or $item.type -ne 'turn.completed') { continue }
        if (-not $item.PSObject.Properties['usage'] -or -not $item.usage) { continue }
        foreach ($key in $map.Keys)
        {
            $source = $map[$key]
            if ($item.usage.PSObject.Properties[$source] -and $null -ne $item.usage.$source)
            {
                $sums[$key] = [long]$sums[$key] + [long]$item.usage.$source
            }
        }
    }
    $sums
}

function Test-SelfTestStub([string]$Path)
{
    # Fault injection is only ever accepted together with a Codex substitute the self-test wrote and marked.
    if (-not $Path -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $false }
    ((Get-Content -LiteralPath $Path -TotalCount 1) -eq '# pointframe-selftest-stub')
}

function Write-ResultFile([string]$Path, [hashtable]$Result)
{
    $temp = "$Path.$([guid]::NewGuid().ToString('N')).tmp"
    [IO.File]::WriteAllText($temp, ($Result | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
    [IO.File]::Move($temp, $Path, $true)
}

function Invoke-SelfTest
{
    $failures = [Collections.Generic.List[string]]::new()
    foreach ($args in @(
        @('', 'x', 'gpt-6-luna', 'medium', 'read-only', ''),
        @('p', '', 'gpt-6-luna', 'medium', 'read-only', ''),
        @('p', 'o', '', 'medium', 'read-only', ''),
        @('p', 'o', 'gpt-6-luna', '', 'read-only', '')
    ))
    { if (Test-ConsultArguments @args) { $failures.Add("invalid argument tuple accepted: $($args -join ',')") } }
    if (-not (Test-CodexRunPolicy 'gpt-6.1-sol' high '' read-only $true).Ok) { $failures.Add('read-only sol/high was refused') }
    if ((Test-CodexRunPolicy 'gpt-6.1-sol' high '' workspace-write).Ok) { $failures.Add('workspace-write sol/high without reason accepted') }
    if ((Test-CodexRunPolicy 'gpt-6-luna' medium '' workspace-write).Ok -eq $false) { $failures.Add('luna medium refused') }
    $freshArgs = New-CodexArguments @() $false 'gpt-6-luna' 'medium' 'read-only' '' 'C:\repo' 'C:\out.md'
    $expectedFresh = @('--ask-for-approval', 'never', 'exec', '-m', 'gpt-6-luna', '-c', 'model_reasoning_effort=medium', '-c', 'windows.sandbox="unelevated"', '--sandbox', 'read-only', '--skip-git-repo-check', '--json', '-C', '"C:\repo"', '-o', '"C:\out.md"', '-')
    if (($freshArgs -join "`n") -cne ($expectedFresh -join "`n")) { $failures.Add("fresh arguments mismatch: $($freshArgs -join ' | ')") }
    $resumeArgs = New-CodexArguments @() $true 'gpt-6-luna' 'medium' 'workspace-write' 'session-123' 'C:\repo' 'C:\out.md'
    $expectedResume = @('--ask-for-approval', 'never', 'exec', 'resume', '-m', 'gpt-6-luna', '-c', 'model_reasoning_effort=medium', '-c', 'windows.sandbox="unelevated"', '-c', 'sandbox_mode="workspace-write"', '--skip-git-repo-check', '--json', '-o', '"C:\out.md"', 'session-123', '-')
    if (($resumeArgs -join "`n") -cne ($expectedResume -join "`n") -or $resumeArgs -ccontains '--sandbox' -or $resumeArgs -ccontains '-C') { $failures.Add("resume arguments mismatch: $($resumeArgs -join ' | ')") }
    $strictEvent = Get-SessionId @('{"type":"thread.started","thread_id":"thread-123"}')
    if ($strictEvent -ne 'thread-123') { $failures.Add('thread.started event did not return thread_id under strict mode') }
    $temp = Join-Path ([IO.Path]::GetTempPath()) "codex-ask-test-$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $temp | Out-Null
    try
    {
        $path = Join-Path $temp 'out.md'; $started = [DateTimeOffset]::UtcNow
        $missing = Test-ConsultOutput $path $started
        if ($missing.Ok -or $missing.Reason -notmatch 'did not create') { $failures.Add('missing output accepted') }
        [IO.File]::WriteAllText($path, '')
        if ((Test-ConsultOutput $path $started).Reason -notmatch 'empty') { $failures.Add('empty output accepted') }
        [IO.File]::WriteAllText($path, 'old')
        [IO.File]::SetLastWriteTimeUtc($path, $started.AddMinutes(-5).UtcDateTime)
        if ((Test-ConsultOutput $path $started).Reason -notmatch 'stale') { $failures.Add('stale output accepted') }
        # Fake codex command writes a fresh answer and emits a session event; it never opens a network connection.
        $fake = { param($output) [IO.File]::WriteAllText($output, 'fake answer'); @('{"type":"thread.started","thread_id":"fake-session"}') }.GetNewClosure()
        $fakeResult = & $fake $path
        if ((Test-ConsultOutput $path $started).Ok -ne $true -or (Get-SessionId $fakeResult) -ne 'fake-session') { $failures.Add('fake codex output/session not accepted') }

        $repo = Join-Path $temp 'repo'; New-Item -ItemType Directory -Path $repo | Out-Null
        & git -C $repo init -q
        & git -C $repo -c user.name=SelfTest -c user.email=selftest@example.invalid commit --allow-empty -qm initial
        if ($LASTEXITCODE -ne 0) { throw 'could not create temporary repository commit' }
        $fakeScript = Join-Path $temp 'fake-codex.ps1'
        [IO.File]::WriteAllText($fakeScript, @'
$all = @($args)
$oi = [Array]::IndexOf($all, '-o')
if ($oi -lt 0) { exit 8 }
[IO.File]::WriteAllText($all[$oi + 1], 'offline fake answer')
'{"type":"session.started","session_id":"fake-e2e-session"}'
'@, [Text.UTF8Encoding]::new($false))
        $prompt = Join-Path $temp 'prompt.md'; [IO.File]::WriteAllText($prompt, 'offline prompt')
        $destination = Join-Path $temp 'real-output.md'
        $oldFake = $env:POINTFRAME_CODEX_TEST_SCRIPT; $env:POINTFRAME_CODEX_TEST_SCRIPT = $fakeScript
        try
        {
            $runRecordPath = Join-Path $temp 'agent-runs.jsonl'
            & pwsh -NoProfile -File $PSCommandPath -PromptFile $prompt -OutFile $destination -Model gpt-6-luna -Effort medium -Sandbox read-only -Worktree $repo -TestCodexScript $fakeScript -RunRecordPath $runRecordPath | Out-Null
            if ($LASTEXITCODE -ne 0 -or -not (Test-Path $destination) -or (Get-Content "$destination.log" -Raw) -notmatch 'fake-e2e-session') { $failures.Add('real launch path did not complete against fake Codex command') }
            if (-not (Test-Path $runRecordPath)) { $failures.Add('self-test run record was not written under the temp root') }
        }
        finally { if ($null -eq $oldFake) { Remove-Item Env:POINTFRAME_CODEX_TEST_SCRIPT -ErrorAction SilentlyContinue } else { $env:POINTFRAME_CODEX_TEST_SCRIPT = $oldFake } }

        # No usage events: the result file carries the session and null (not 0) token fields.
        $resultPath = "$destination.result.json"
        if (-not (Test-Path $resultPath)) { $failures.Add('result file was not written') }
        else
        {
            $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
            if ($result.sessionId -ne 'fake-e2e-session' -or $result.model -ne 'gpt-6-luna' -or $result.effort -ne 'medium' -or $result.exitCode -ne 0) { $failures.Add('result file identity fields mismatch') }
            if ($null -ne $result.tokens.inputTokens -or $null -ne $result.tokens.outputTokens) { $failures.Add('absent token fields were not null') }
            if ($null -eq $result.elapsedSeconds) { $failures.Add('result file has no elapsed time') }
        }

        # Usage events: the token fields are the sums over turn.completed events.
        $usageScript = Join-Path $temp 'fake-usage.ps1'
        [IO.File]::WriteAllText($usageScript, @'
$all = @($args)
$oi = [Array]::IndexOf($all, '-o')
[IO.File]::WriteAllText($all[$oi + 1], 'usage answer')
'{"type":"thread.started","thread_id":"usage-session"}'
'{"type":"turn.completed","usage":{"input_tokens":10,"cached_input_tokens":4,"output_tokens":3}}'
'{"type":"turn.completed","usage":{"input_tokens":5,"cached_input_tokens":1,"output_tokens":2}}'
'@, [Text.UTF8Encoding]::new($false))
        $usageOut = Join-Path $temp 'usage-output.md'
        & pwsh -NoProfile -File $PSCommandPath -PromptFile $prompt -OutFile $usageOut -Model gpt-6-luna -Effort medium -Sandbox read-only -Worktree $repo -TestCodexScript $usageScript -RunRecordPath (Join-Path $temp 'usage-runs.jsonl') | Out-Null
        $usageResult = if (Test-Path "$usageOut.result.json") { Get-Content -LiteralPath "$usageOut.result.json" -Raw | ConvertFrom-Json } else { $null }
        if ($LASTEXITCODE -ne 0 -or -not $usageResult -or $usageResult.tokens.inputTokens -ne 15 -or $usageResult.tokens.cachedInputTokens -ne 5 -or $usageResult.tokens.outputTokens -ne 5 -or $null -ne $usageResult.tokens.reasoningOutputTokens)
        { $failures.Add('token fields were not summed from the event stream') }

        # A Codex process that never finishes is killed at -MaxMinutes and reported.
        $sleepScript = Join-Path $temp 'fake-sleep.ps1'
        [IO.File]::WriteAllText($sleepScript, "# pointframe-selftest-stub`r`nStart-Sleep -Seconds 120`r`n", [Text.UTF8Encoding]::new($false))
        $sleepOut = Join-Path $temp 'sleep-output.md'
        $timer = [Diagnostics.Stopwatch]::StartNew()
        $sleepText = (& pwsh -NoProfile -File $PSCommandPath -PromptFile $prompt -OutFile $sleepOut -Model gpt-6-luna -Effort medium -Sandbox read-only -Worktree $repo -TestCodexScript $sleepScript -RunRecordPath (Join-Path $temp 'sleep-runs.jsonl') -MaxMinutes 0.03) -join "`n"
        $sleepExit = $LASTEXITCODE
        if ($sleepExit -ne 1 -or $sleepText -notmatch 'did not finish.*and was stopped' -or $timer.Elapsed.TotalSeconds -gt 60) { $failures.Add("timeout was not enforced with the existing failure code (exit $sleepExit, $([int]$timer.Elapsed.TotalSeconds)s): $sleepText") }
        $sleepResult = if (Test-Path "$sleepOut.result.json") { Get-Content -LiteralPath "$sleepOut.result.json" -Raw | ConvertFrom-Json } else { $null }
        if (-not $sleepResult -or -not $sleepResult.timedOut -or $sleepResult.outcome -ne 'timeout' -or $null -ne $sleepResult.exitCode -or $sleepResult.terminated -ne $true) { $failures.Add('timeout was not recorded in the result file') }
        $jsonl = Get-Content -LiteralPath (Join-Path $temp 'usage-runs.jsonl') -Raw | ConvertFrom-Json
        if ($jsonl.tokens.input -ne 15 -or $jsonl.tokens.output -ne 5) { $failures.Add('agent-runs record did not carry the real token sums') }
        $nullJsonl = (Get-Content -LiteralPath (Join-Path $temp 'sleep-runs.jsonl') -Raw | ConvertFrom-Json)
        if ($null -ne $nullJsonl.tokens.input -or $null -ne $nullJsonl.tokens.output) { $failures.Add('agent-runs record used a zero placeholder for absent tokens') }

        # A grandchild of Codex does not survive the timeout.
        $treeScript = Join-Path $temp 'fake-tree.ps1'
        $pidFile = Join-Path $temp 'grandchild.pid'
        [IO.File]::WriteAllText($treeScript, "`$c = Start-Process pwsh -ArgumentList '-NoProfile','-Command','Start-Sleep 300' -PassThru -NoNewWindow; Set-Content -LiteralPath '$pidFile' `$c.Id; Start-Sleep -Seconds 120`r`n", [Text.UTF8Encoding]::new($false))
        & pwsh -NoProfile -File $PSCommandPath -PromptFile $prompt -OutFile (Join-Path $temp 'tree-output.md') -Model gpt-6-luna -Effort medium -Sandbox read-only -Worktree $repo -TestCodexScript $treeScript -RunRecordPath (Join-Path $temp 'tree-runs.jsonl') -MaxMinutes 0.15 | Out-Null
        $grandchild = if (Test-Path $pidFile) { [int](Get-Content $pidFile -Raw).Trim() } else { 0 }
        Start-Sleep -Milliseconds 500
        if ($grandchild -le 0 -or (Get-Process -Id $grandchild -ErrorAction SilentlyContinue)) { $failures.Add("a grandchild survived the timeout (pid $grandchild)") }
        if ($grandchild -gt 0) { Stop-Process -Id $grandchild -Force -ErrorAction SilentlyContinue }

        # When the kill cannot be verified the message and the result file do not claim it.
        $stuckOut = Join-Path $temp 'stuck-output.md'
        $stuckText = (& pwsh -NoProfile -File $PSCommandPath -PromptFile $prompt -OutFile $stuckOut -Model gpt-6-luna -Effort medium -Sandbox read-only -Worktree $repo -TestCodexScript $sleepScript -RunRecordPath (Join-Path $temp 'stuck-runs.jsonl') -MaxMinutes 0.03 -SimulateKillFailure) -join "`n"
        $stuckExit = $LASTEXITCODE
        $stuckResult = if (Test-Path "$stuckOut.result.json") { Get-Content -LiteralPath "$stuckOut.result.json" -Raw | ConvertFrom-Json } else { $null }
        if ($stuckExit -ne 1 -or $stuckText -notmatch 'COULD NOT be stopped' -or $stuckText -match 'and was stopped' -or -not $stuckResult -or $stuckResult.terminated -ne $false) { $failures.Add("an unverified kill was reported as stopped: $stuckText") }

        # Fault injection is refused for anything but a marked self-test stub, so real launches cannot be affected.
        & pwsh -NoProfile -File $PSCommandPath -PromptFile $prompt -OutFile (Join-Path $temp 'refused-output.md') -Model gpt-6-luna -Effort medium -Sandbox read-only -Worktree $repo -TestCodexScript $fakeScript -RunRecordPath (Join-Path $temp 'refused-runs.jsonl') -SimulateKillFailure | Out-Null
        if ($LASTEXITCODE -ne 2) { $failures.Add('-SimulateKillFailure was accepted with an unmarked executor') }
    }
    finally { Remove-Item -LiteralPath $temp -Recurse -Force }
    if ($failures.Count) { $failures | ForEach-Object { Write-Host "ERROR self-test: $_" }; exit 1 }
    Write-Host 'codex-ask self-test passed.'
    exit 0
}

function Test-ConsultArguments
{
    param([string]$Prompt, [string]$Output, [string]$RunModel, [string]$RunEffort, [string]$RunSandbox, [string]$Id)
    if (-not $Prompt -or -not $Output -or -not $RunModel -or -not $RunEffort) { return $false }
    if ($RunSandbox -notin @('read-only', 'workspace-write')) { return $false }
    if ($Id -and $Id -notmatch '^[A-Za-z0-9_-]+$') { return $false }
    $true
}

function New-CodexArguments
{
    param([string[]]$Prefix, [bool]$Resume, [string]$RunModel, [string]$RunEffort, [string]$RunSandbox, [string]$Id, [string]$Repo, [string]$Output)
    $result = $Prefix + @('--ask-for-approval', 'never', 'exec')
    if ($Resume) { $result += 'resume' }
    $result += @('-m', $RunModel, '-c', "model_reasoning_effort=$RunEffort", '-c', 'windows.sandbox="unelevated"')
    if ($Resume) { $result += @('-c', "sandbox_mode=`"$RunSandbox`"") }
    else { $result += @('--sandbox', $RunSandbox) }
    $result += '--skip-git-repo-check'
    if (-not $Resume) { $result += @('--json', '-C', "`"$Repo`"") }
    else { $result += '--json' }
    $result += @('-o', "`"$Output`"")
    if ($Resume) { $result += $Id }
    $result += '-'
    $result
}
if ($SelfTest) { Invoke-SelfTest }
if (-not (Test-ConsultArguments $PromptFile $OutFile $Model $Effort $Sandbox $SessionId))
{ Write-Host 'ERROR PromptFile, OutFile, Model, and Effort are mandatory; sandbox and session id must be valid.'; exit 2 }
if (-not (Test-Path -LiteralPath $PromptFile -PathType Leaf)) { Write-Host "ERROR prompt file not found: $PromptFile"; exit 2 }
if (-not (Test-Path -LiteralPath $Worktree -PathType Container) -or ([string](& git -C $Worktree rev-parse --is-inside-work-tree 2>$null)).Trim() -ne 'true')
{ Write-Host "ERROR worktree is not a git worktree: $Worktree"; exit 2 }
$policy = Test-CodexRunPolicy $Model $Effort $EscalationReason $Sandbox $true
if (-not $policy.Ok) { $policy.Reasons | ForEach-Object { Write-Host "Policy refusal: $_" }; exit 2 }
$codex = Resolve-CodexCommand $TestCodexScript
if (-not $codex.Ok) { Write-Host "ERROR $($codex.Reason)"; exit 2 }
$promptPath = (Resolve-Path -LiteralPath $PromptFile).Path
$outPath = [IO.Path]::GetFullPath($OutFile)
$logPath = "$outPath.log"
New-Item -ItemType Directory -Path (Split-Path $outPath -Parent) -Force | Out-Null
Remove-Item -LiteralPath $outPath -Force -ErrorAction SilentlyContinue
$prompt = [IO.File]::ReadAllText($promptPath, [Text.Encoding]::UTF8)
$started = [DateTimeOffset]::UtcNow
$repo = (Resolve-Path -LiteralPath $Worktree).Path
$arguments = New-CodexArguments $codex.Prefix ([bool]$SessionId) $Model $Effort $Sandbox $SessionId $repo $outPath
$stdin = Join-Path ([IO.Path]::GetTempPath()) "codex-ask-$([guid]::NewGuid().ToString('N')).txt"
$stdout = Join-Path ([IO.Path]::GetTempPath()) "codex-ask-$([guid]::NewGuid().ToString('N')).jsonl"
$stderr = Join-Path ([IO.Path]::GetTempPath()) "codex-ask-$([guid]::NewGuid().ToString('N')).err"
try
{
    [IO.File]::WriteAllText($stdin, $prompt, [Text.UTF8Encoding]::new($false))
    if ($SimulateKillFailure -and -not (Test-SelfTestStub $TestCodexScript)) { Write-Host 'ERROR -SimulateKillFailure is only accepted together with a marked self-test stub executor'; exit 2 }
    Initialize-ProcessJob
    $job = [PointframeProcessJob]::Create()
    if ($job -eq [IntPtr]::Zero) { Write-Host 'ERROR cannot create a job object; an uncontained Codex process is never started'; exit 1 }
    # Created suspended, assigned to the kill-on-close job, then resumed: Codex cannot spawn a child outside the job.
    # Accepted limitation: a crash of this script between creation and assignment leaves one suspended process that never ran.
    $process = Start-ProcessInJob $job $codex.File $arguments $(if ($SessionId) { $repo } else { (Get-Location).Path }) $stdin $stdout $stderr
    $timedOut = -not (Wait-ProcessExit $process ([long][Math]::Ceiling($MaxMinutes * 60000)))
    $terminated = $null
    if ($timedOut)
    {
        # "Stopped" is only claimed when it is verified: the whole job (children and grandchildren) is empty.
        $terminated = if ($SimulateKillFailure) { $false } else { Stop-ProcessJob $job 10 }
        $containment = [pscustomobject]@{ Verified = $terminated; Alive = (-not $terminated); Leftover = 0; Reason = $(if ($terminated) { $null } else { 'the process tree could not be verified gone' }) }
        if ($terminated) { Wait-ProcessExit $process 5000 | Out-Null }
    }
    else
    {
        $containment = Test-JobContainment $job
    }
    $readShared = { param($path) $s = [IO.File]::Open($path, 'Open', 'Read', 'ReadWrite'); try { [IO.StreamReader]::new($s, [Text.Encoding]::UTF8).ReadToEnd() } finally { $s.Dispose() } }
    $events = if (Test-Path $stdout) { & $readShared $stdout } else { '' }
    $errors = if (Test-Path $stderr) { & $readShared $stderr } else { '' }
    [IO.File]::WriteAllText($logPath, $events + $errors, [Text.UTF8Encoding]::new($false))
    $eventLines = @($events -split "`r?`n")
    $session = Get-SessionId $eventLines
    $outcome = 'completed'; $exit = if ($timedOut) { $null } else { Get-ProcessExitCode $process }
    [PointframeProcessJob]::CloseProcess($process.Handle)
    $tokenFields = Get-TokenFields $eventLines
    if ($timedOut) { $outcome = 'timeout' }
    elseif ($exit -ne 0) { $outcome = 'failed' }
    elseif (-not $containment.Verified) { $outcome = 'failed' }
    $outputCheck = Test-ConsultOutput $outPath $started
    if ($exit -eq 0 -and -not $outputCheck.Ok) { $outcome = 'failed'; Write-Host "ERROR $($outputCheck.Reason)" }
    $elapsed = [DateTimeOffset]::UtcNow - $started
    Write-ResultFile "$outPath.result.json" @{
        sessionId = if ($session) { $session } else { $null }
        model = $Model
        effort = $Effort
        sandbox = $Sandbox
        exitCode = $exit
        outcome = $outcome
        timedOut = $timedOut
        terminated = $terminated
        containmentVerified = [bool]$containment.Verified
        containmentReason = $containment.Reason
        leftoverProcesses = $containment.Leftover
        maxMinutes = $MaxMinutes
        elapsedSeconds = [Math]::Round($elapsed.TotalSeconds, 1)
        tokens = $tokenFields
    }
    [PointframeProcessJob]::Close($job)
    $record = [ordered]@{ utcStart = $started.ToString('o'); utcEnd = [DateTimeOffset]::UtcNow.ToString('o'); task = $null; briefPath = $promptPath; briefSha256 = (Get-FileHash $promptPath -Algorithm SHA256).Hash; model = $Model; effort = $Effort; escalationReason = $EscalationReason; sandbox = $Sandbox; worktree = $repo; minutes = [Math]::Round(([DateTimeOffset]::UtcNow - $started).TotalMinutes, 2); tokens = @{ input = $tokenFields.inputTokens; cached = $tokenFields.cachedInputTokens; output = $tokenFields.outputTokens }; exitCode = $exit; outcome = $outcome; logDirectory = Split-Path $logPath -Parent; kind = 'consult'; sessionId = $session }
    $recordPath = if ($RunRecordPath) { [IO.Path]::GetFullPath($RunRecordPath) } else { Join-Path (Split-Path $PSScriptRoot -Parent) 'plan/agent-runs.jsonl' }
    if (Test-Path -LiteralPath (Split-Path $recordPath -Parent) -PathType Container)
    { [IO.File]::AppendAllText($recordPath, (($record | ConvertTo-Json -Compress -Depth 8) + "`r`n"), [Text.UTF8Encoding]::new($false)) }
    if ($timedOut)
    {
        $stopNote = if ($terminated) { 'and was stopped' } else { "and COULD NOT be stopped (pid $($process.Id)); stop it by hand" }
        Write-Host "ERROR Codex did not finish within $MaxMinutes minute(s) $stopNote. See $logPath"
        exit 1
    }
    if ($exit -ne 0) { Write-Host "ERROR Codex exited with code $exit. See $logPath"; exit 1 }
    if (-not $containment.Verified) { Write-Host "ERROR containment unverified: $($containment.Reason). The answer is not trusted. See $logPath"; exit 1 }
    if (-not $outputCheck.Ok) { exit 1 }
    Write-Host "Codex session: $(if ($session) { $session } else { '(not reported)' }); model=$Model effort=$Effort"
    Get-Content -LiteralPath $outPath -Raw -Encoding UTF8
    exit 0
}
finally
{
    Remove-Item -LiteralPath $stdin, $stdout, $stderr -Force -ErrorAction SilentlyContinue
}
