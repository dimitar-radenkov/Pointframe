#Requires -Version 7.0
<#
Validate and run a bounded Codex executor task.

  agent-run.ps1 -Brief task.md -Worktree <path> [-Model <model>] [-Effort medium]
  agent-run.ps1 -Brief task.md -Worktree <path> -ValidateOnly
  agent-run.ps1 -SelfTest

The defaults come from agent-workflow.json: model, effort and sandbox from the executor role, the budgets and the run record from
agentRuns (default 30 minutes, 20000000 input tokens, plan/agent-runs.jsonl), the temporary log folder from
state.commonDirNamespace. The models allowed are those of the openai roles. The repository is -Root, else the -Worktree when it
exists, else the repository that holds this script; the caller's folder is never consulted.
#>
[CmdletBinding()]
param(
    [string]$Brief,
    [string]$Worktree,
    [string]$Model,
    [ValidateSet('low', 'medium', 'high', 'xhigh', 'max')]
    [string]$Effort,
    [string]$EscalationReason,
    [ValidateRange(1, 1440)]
    [int]$MaxMinutes,
    [ValidateRange(1, [long]::MaxValue)]
    [long]$MaxInputTokens,
    [ValidateSet('workspace-write', 'read-only')]
    [string]$Sandbox,
    [string]$Task,
    [string]$LogDirectory,
    [switch]$ValidateOnly,
    [string]$Root,
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '..' 'lib' 'root.ps1')
. (Join-Path $PSScriptRoot '..' 'lib' 'config.ps1')
. (Join-Path $PSScriptRoot '..' 'lib' 'context.ps1')
. (Join-Path $PSScriptRoot '..' 'lib' 'selftest.ps1')
. (Join-Path $PSScriptRoot '..' 'lib' 'codex.ps1')

function Test-Brief
{
    param([string]$Text)
    $missing = [Collections.Generic.List[string]]::new()
    $checks = @(
        @{ Name = 'objective/why'; Pattern = '(?im)^#{1,2}\s+.*\b(objective|why)\b' },
        @{ Name = 'worktree'; Pattern = '(?im)^#{1,2}\s+.*\bworktree\b|(?im)^\s*worktree\s*:' },
        @{ Name = 'read first'; Pattern = '(?im)^#{1,2}\s+.*\bread first\b' },
        @{ Name = 'deliverable(s)'; Pattern = '(?im)^#{1,2}\s+.*\bdeliverables?\b' },
        @{ Name = 'exclusions'; Pattern = '(?im)^#{1,2}\s+.*\bexclusions?\b' },
        @{ Name = 'stop conditions'; Pattern = '(?im)^#{1,2}\s+.*\bstop conditions?\b' },
        @{ Name = 'checks'; Pattern = '(?im)^#{1,2}\s+.*\bchecks\b' }
    )
    foreach ($check in $checks)
    {
        if ($Text -notmatch $check.Pattern)
        {
            $missing.Add($check.Name)
        }
    }
    [pscustomobject]@{ Ok = ($missing.Count -eq 0); Missing = @($missing) }
}

function Test-RunPolicy
{
    param([string]$Model, [string]$Effort, [string]$EscalationReason, [string]$Sandbox)
    Test-CodexRunPolicy $Model $Effort $EscalationReason $Sandbox $false $script:ModelPolicy.Allowed $script:ModelPolicy.Strong
}

function Get-TokenUsage
{
    param([string[]]$Lines)
    $inputTokens = [long]0
    $cached = [long]0
    $output = [long]0
    foreach ($line in $Lines)
    {
        if (-not $line) { continue }
        try { $turnEvent = $line | ConvertFrom-Json } catch { continue }
        if ($turnEvent.type -ne 'turn.completed') { continue }
        $usage = $turnEvent.usage
        if ($usage)
        {
            $inputTokens += [long]$usage.input_tokens
            $cached += [long]$usage.cached_input_tokens
            $output += [long]$usage.output_tokens
        }
    }
    [pscustomobject]@{ Input = $inputTokens; Cached = $cached; Output = $output }
}

function Get-BudgetDecision
{
    param([double]$Minutes, [long]$InputTokens, [int]$MaxMinutes, [long]$MaxInputTokens)
    if ($Minutes -ge $MaxMinutes) { return 'time' }
    if ($InputTokens -gt $MaxInputTokens) { return 'tokens' }
    return ''
}

function Test-GitWorktree([string]$Path)
{
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) { return $false }
    $inside = ([string](& git -C $Path rev-parse --is-inside-work-tree 2>$null)).Trim()
    return ($LASTEXITCODE -eq 0 -and $inside -eq 'true')
}

function Invoke-SelfTest
{
    $fixtureDir = Join-Path $PSScriptRoot '..' 'fixtures' 'gates' 'agent-run'
    $script:ModelPolicy = Get-AwCodexPolicy (Get-AwSelfTestConfig)
    $complete = Get-Content (Join-Path $fixtureDir 'complete.md') -Raw
    $missingExclusions = Get-Content (Join-Path $fixtureDir 'missing-exclusions.md') -Raw
    $missingStops = Get-Content (Join-Path $fixtureDir 'missing-stop-conditions.md') -Raw
    $failures = [Collections.Generic.List[string]]::new()
    if (-not (Test-Brief $complete).Ok) { $failures.Add('complete brief rejected') }
    if ((Test-Brief $missingExclusions).Missing -notcontains 'exclusions') { $failures.Add('missing exclusions not detected') }
    if ((Test-Brief $missingStops).Missing -notcontains 'stop conditions') { $failures.Add('missing stop conditions not detected') }
    if (-not (Test-RunPolicy 'gpt-6-luna' 'medium' '' 'workspace-write').Ok) { $failures.Add('luna medium rejected') }
    if ((Test-RunPolicy 'gpt-6-luna' 'high' '' 'workspace-write').Ok) { $failures.Add('luna high without reason accepted') }
    if ((Test-RunPolicy 'gpt-6.1-sol' 'medium' '' 'workspace-write').Ok) { $failures.Add('sol without reason accepted') }
    if ((Test-RunPolicy 'gpt-6-astra' 'medium' '' 'workspace-write').Ok) { $failures.Add('astra accepted') }
    if ((Test-RunPolicy 'gpt-6-luna' 'medium' '' 'danger-full-access').Ok) { $failures.Add('full access accepted') }
    $usage = Get-TokenUsage @(Get-Content (Join-Path $fixtureDir 'events.jsonl'))
    if ($usage.Input -ne 30 -or $usage.Cached -ne 5 -or $usage.Output -ne 7) { $failures.Add('token usage sum mismatch') }
    if ((Get-BudgetDecision 30 0 30 100) -ne 'time') { $failures.Add('time budget decision mismatch') }
    if ((Get-BudgetDecision 1 101 30 100) -ne 'tokens') { $failures.Add('token budget decision mismatch') }
    $tempRepo = Join-Path ([IO.Path]::GetTempPath()) "agent-run-e2e-$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $tempRepo | Out-Null
    try
    {
        & git -C $tempRepo init -q
        & git -C $tempRepo -c user.name=SelfTest -c user.email=selftest@example.invalid commit --allow-empty -qm initial
        if ($LASTEXITCODE -ne 0 -or -not (Test-GitWorktree $tempRepo)) { $failures.Add('temporary repository failed real worktree check') }
        $runRecord = Join-Path $tempRepo 'run-record.json'
        [IO.File]::WriteAllText($runRecord, (@{ head = (& git -C $tempRepo rev-parse HEAD).Trim(); outcome = 'self-test' } | ConvertTo-Json -Compress))
        $loaded = Get-Content -LiteralPath $runRecord -Raw | ConvertFrom-Json
        if ($loaded.outcome -ne 'self-test' -or -not $loaded.head) { $failures.Add('temporary run record could not be read back') }
    }
    catch { $failures.Add("end-to-end repository test failed: $($_.Exception.Message)") }
    finally { Remove-Item -LiteralPath $tempRepo -Recurse -Force -ErrorAction SilentlyContinue }
    if ($failures.Count)
    {
        $failures | ForEach-Object { Write-Host "ERROR self-test: $_" }
        exit 1
    }
    Write-Host 'agent-run self-test passed.'
    exit 0
}

if ($SelfTest) { Invoke-SelfTest }
if (-not $Brief) { Write-Error '-Brief is required'; exit 2 }
if (-not (Test-Path -LiteralPath $Brief -PathType Leaf)) { Write-Error "Brief not found: $Brief"; exit 2 }
$briefPath = (Resolve-Path -LiteralPath $Brief).Path
$briefText = Get-Content -LiteralPath $briefPath -Raw
# The repository: -Root, else an existing -Worktree, else the repository that holds this script.
$contextWorktree = if ($Worktree -and (Test-Path -LiteralPath $Worktree -PathType Container)) { $Worktree } else { '' }
try { $Context = Get-AwToolContext -Root $Root -Worktree $contextWorktree -ScriptRoot $PSScriptRoot }
catch { Write-Host "Refused: $($_.Exception.Message)"; exit 2 }
$executorRole = $Context.Config.roles.executor
$runSettings = Get-AwSection $Context 'agentRuns'
if (-not $PSBoundParameters.ContainsKey('Model')) { $Model = [string]$executorRole.model }
if (-not $PSBoundParameters.ContainsKey('Effort')) { $Effort = [string]$executorRole.effort }
if (-not $PSBoundParameters.ContainsKey('Sandbox')) { $Sandbox = [string]$executorRole.sandbox }
if (-not $PSBoundParameters.ContainsKey('MaxMinutes')) { $MaxMinutes = [int](Get-AwSetting $runSettings 'maxMinutes' 30) }
if (-not $PSBoundParameters.ContainsKey('MaxInputTokens')) { $MaxInputTokens = [long](Get-AwSetting $runSettings 'maxInputTokens' 20000000) }
$script:ModelPolicy = Get-AwCodexPolicy $Context.Config
$briefCheck = Test-Brief $briefText
$policy = Test-RunPolicy $Model $Effort $EscalationReason $Sandbox
if (-not $briefCheck.Ok) { Write-Host "Missing brief sections: $($briefCheck.Missing -join ', ')" }
if (-not $policy.Ok) { $policy.Reasons | ForEach-Object { Write-Host "Policy refusal: $_" } }
if (-not $briefCheck.Ok -or -not $policy.Ok) { exit 2 }
if ($ValidateOnly) { Write-Host 'Brief and policy checks passed.'; exit 0 }

function Resolve-RepositoryPath([string]$Path)
{
    if ([IO.Path]::IsPathRooted($Path)) { return [IO.Path]::GetFullPath($Path) }
    [IO.Path]::GetFullPath((Join-Path $Context.Root $Path))
}

$resolvedWorktree = (Resolve-Path -LiteralPath $Worktree -ErrorAction SilentlyContinue).Path
if (-not $resolvedWorktree -or -not (Test-GitWorktree $resolvedWorktree)) { Write-Host 'Refused: worktree is not a git worktree'; exit 2 }
# The npm install puts codex.ps1/codex.cmd shims on PATH, which Start-Process cannot start with redirected streams;
# run node on the package's codex.js instead (a real codex.exe is used as is).
$codexResolution = Resolve-CodexCommand
if (-not $codexResolution.Ok) { Write-Host "Refused: $($codexResolution.Reason)"; exit 2 }
$codexFile = $codexResolution.File
$codexPrefix = $codexResolution.Prefix
$started = [DateTimeOffset]::UtcNow
$briefName = [IO.Path]::GetFileNameWithoutExtension($briefPath)
if (-not $LogDirectory) { $LogDirectory = Join-Path ([IO.Path]::GetTempPath()) ("$($Context.Config.state.commonDirNamespace)-agent-runs/" + $started.ToLocalTime().ToString('yyyyMMdd-HHmmss') + '-' + $(if ($Task) { $Task } else { $briefName })) }
try
{
    New-Item -ItemType Directory -Path $LogDirectory -Force | Out-Null
    $probe = Join-Path $LogDirectory '.write-test'
    [IO.File]::WriteAllText($probe, '')
    Remove-Item -LiteralPath $probe
}
catch { Write-Host "Refused: log directory is not writable: $LogDirectory"; exit 2 }
$LogDirectory = [IO.Path]::GetFullPath($LogDirectory)
$eventsPath = Join-Path $LogDirectory 'events.jsonl'
$stderrPath = Join-Path $LogDirectory 'stderr.log'
$outPath = Join-Path $LogDirectory 'out.md'
$briefHash = (Get-FileHash -LiteralPath $briefPath -Algorithm SHA256).Hash
$arguments = $codexPrefix + @('--ask-for-approval', 'never', 'exec', '-m', $Model, '-c', "model_reasoning_effort=$Effort", '-c', 'windows.sandbox="unelevated"', '--sandbox', $Sandbox, '--json', '-C', "`"$resolvedWorktree`"", '-o', "`"$outPath`"", '-')
[IO.File]::WriteAllText((Join-Path $LogDirectory 'stdin.tmp'), $briefText, [Text.UTF8Encoding]::new($false))
$process = Start-Process -FilePath $codexFile -ArgumentList $arguments -PassThru -NoNewWindow -RedirectStandardInput (Join-Path $LogDirectory 'stdin.tmp') -RedirectStandardOutput $eventsPath -RedirectStandardError $stderrPath
$outcome = 'failed'
$budget = ''
while (-not $process.HasExited)
{
    Start-Sleep -Seconds 15
    $process.Refresh()
    $lines = if (Test-Path $eventsPath) { @(Get-Content -LiteralPath $eventsPath) } else { @() }
    $usage = Get-TokenUsage $lines
    $minutes = ([DateTimeOffset]::UtcNow - $started).TotalMinutes
    Write-Host ('RUN {0} {1:N1}m tokens={2}/{3}' -f $(if ($Task) { $Task } else { $briefName }), $minutes, $usage.Input, $usage.Output)
    $budget = Get-BudgetDecision $minutes $usage.Input $MaxMinutes $MaxInputTokens
    if ($budget)
    {
        & taskkill.exe /PID $process.Id /T /F | Out-Null
        $process.WaitForExit()
        break
    }
}
$process.Refresh()
$ended = [DateTimeOffset]::UtcNow
$minutes = [Math]::Round(($ended - $started).TotalMinutes, 2)
$lines = if (Test-Path $eventsPath) { @(Get-Content -LiteralPath $eventsPath) } else { @() }
$usage = Get-TokenUsage $lines
$exitCode = if ($budget) { $null } else { $process.ExitCode }
if ($budget) { $outcome = 'budget-exceeded' }
elseif ($exitCode -eq 0) { $outcome = 'completed' }
$record = [ordered]@{ utcStart = $started.ToString('o'); utcEnd = $ended.ToString('o'); task = $Task; briefPath = $briefPath; briefSha256 = $briefHash; model = $Model; effort = $Effort; escalationReason = $EscalationReason; sandbox = $Sandbox; worktree = $resolvedWorktree; minutes = $minutes; tokens = @{ input = $usage.Input; cached = $usage.Cached; output = $usage.Output }; exitCode = $exitCode; outcome = $outcome; logDirectory = $LogDirectory }
$recordPath = Resolve-RepositoryPath ([string](Get-AwSetting $runSettings 'log' 'plan/agent-runs.jsonl'))
New-Item -ItemType Directory -Path (Split-Path $recordPath -Parent) -Force | Out-Null
[IO.File]::AppendAllText($recordPath, (($record | ConvertTo-Json -Compress -Depth 8) + "`r`n"), [Text.UTF8Encoding]::new($false))
if ($Task)
{
    & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'status.ps1') -Set -Id $Task -Stage "executor $outcome" -Root $Context.Root
}
Write-Host ('RUN {0}: {1} {2:N1}m tokens={3}/{4} log={5}' -f $(if ($Task) { $Task } else { $briefName }), $outcome, $minutes, $usage.Input, $usage.Output, $LogDirectory)
if ($outcome -eq 'completed') { exit 0 }
if ($outcome -eq 'budget-exceeded') { exit 3 }
exit 1
