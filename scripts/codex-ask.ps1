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
    [switch]$SelfTest
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'lib/codex.ps1')

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
        try { $event = $line | ConvertFrom-Json } catch { continue }
        if ($event.session_id) { return [string]$event.session_id }
        if ($event.thread_id) { return [string]$event.thread_id }
        if ($event.type -eq 'session.started' -and $event.sessionId) { return [string]$event.sessionId }
    }
    ''
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
        $fake = { param($output) [IO.File]::WriteAllText($output, 'fake answer'); @('{"type":"session.started","session_id":"fake-session"}') }.GetNewClosure()
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
$arguments = $codex.Prefix + @('--ask-for-approval', 'never', 'exec')
if ($SessionId) { $arguments += @('resume', $SessionId) }
$arguments += @('-m', $Model, '-c', "model_reasoning_effort=$Effort", '-c', 'windows.sandbox="unelevated"', '--sandbox', $Sandbox, '--skip-git-repo-check', '--json', '-C', "`"$repo`"", '-o', "`"$outPath`"", '-')
$stdin = Join-Path ([IO.Path]::GetTempPath()) "codex-ask-$([guid]::NewGuid().ToString('N')).txt"
$stdout = Join-Path ([IO.Path]::GetTempPath()) "codex-ask-$([guid]::NewGuid().ToString('N')).jsonl"
$stderr = Join-Path ([IO.Path]::GetTempPath()) "codex-ask-$([guid]::NewGuid().ToString('N')).err"
try
{
    [IO.File]::WriteAllText($stdin, $prompt, [Text.UTF8Encoding]::new($false))
    $process = Start-Process -FilePath $codex.File -ArgumentList $arguments -PassThru -NoNewWindow -RedirectStandardInput $stdin -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    $process.WaitForExit()
    $events = if (Test-Path $stdout) { [IO.File]::ReadAllText($stdout, [Text.Encoding]::UTF8) } else { '' }
    $errors = if (Test-Path $stderr) { [IO.File]::ReadAllText($stderr, [Text.Encoding]::UTF8) } else { '' }
    [IO.File]::WriteAllText($logPath, $events + $errors, [Text.UTF8Encoding]::new($false))
    $session = Get-SessionId @($events -split "`r?`n")
    $outcome = 'completed'; $exit = $process.ExitCode
    if ($exit -ne 0) { $outcome = 'failed' }
    $outputCheck = Test-ConsultOutput $outPath $started
    if ($exit -eq 0 -and -not $outputCheck.Ok) { $outcome = 'failed'; Write-Host "ERROR $($outputCheck.Reason)" }
    $record = [ordered]@{ utcStart = $started.ToString('o'); utcEnd = [DateTimeOffset]::UtcNow.ToString('o'); task = $null; briefPath = $promptPath; briefSha256 = (Get-FileHash $promptPath -Algorithm SHA256).Hash; model = $Model; effort = $Effort; escalationReason = $EscalationReason; sandbox = $Sandbox; worktree = $repo; minutes = [Math]::Round(([DateTimeOffset]::UtcNow - $started).TotalMinutes, 2); tokens = @{ input = 0; cached = 0; output = 0 }; exitCode = $exit; outcome = $outcome; logDirectory = Split-Path $logPath -Parent; kind = 'consult'; sessionId = $session }
    $recordPath = if ($RunRecordPath) { [IO.Path]::GetFullPath($RunRecordPath) } else { Join-Path (Split-Path $PSScriptRoot -Parent) 'plan/agent-runs.jsonl' }
    if (Test-Path -LiteralPath (Split-Path $recordPath -Parent) -PathType Container)
    { [IO.File]::AppendAllText($recordPath, (($record | ConvertTo-Json -Compress -Depth 8) + "`r`n"), [Text.UTF8Encoding]::new($false)) }
    if ($exit -ne 0) { Write-Host "ERROR Codex exited with code $exit. See $logPath"; exit 1 }
    if (-not $outputCheck.Ok) { exit 1 }
    Write-Host "Codex session: $(if ($session) { $session } else { '(not reported)' }); model=$Model effort=$Effort"
    Get-Content -LiteralPath $outPath -Raw -Encoding UTF8
    exit 0
}
finally
{
    Remove-Item -LiteralPath $stdin, $stdout, $stderr -Force -ErrorAction SilentlyContinue
}
