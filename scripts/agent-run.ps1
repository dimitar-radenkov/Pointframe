#Requires -Version 7.0
<#
Validate and run a bounded Codex executor task.

  pwsh scripts/agent-run.ps1 -Brief task.md -Worktree <path> [-Model gpt-6-luna] [-Effort medium]
  pwsh scripts/agent-run.ps1 -Brief task.md -Worktree <path> -ValidateOnly
  pwsh scripts/agent-run.ps1 -SelfTest
#>
[CmdletBinding()]
param(
    [string]$Brief,
    [string]$Worktree,
    [string]$Model = 'gpt-6-luna',
    [ValidateSet('low', 'medium', 'high', 'xhigh', 'max')]
    [string]$Effort = 'medium',
    [string]$EscalationReason,
    [ValidateRange(1, 1440)]
    [int]$MaxMinutes = 30,
    [ValidateRange(1, [long]::MaxValue)]
    [long]$MaxInputTokens = 20000000,
    [ValidateSet('workspace-write', 'read-only')]
    [string]$Sandbox = 'workspace-write',
    [string]$Task,
    [string]$LogDirectory,
    [switch]$ValidateOnly,
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest

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
    $reasons = [Collections.Generic.List[string]]::new()
    if ($Model -notin @('gpt-6-luna', 'gpt-6.1-sol'))
    {
        if ($Model -match 'astra|5\.') { $reasons.Add("model '$Model' is refused; the owner must approve it") }
        else { $reasons.Add("model '$Model' is not allowed") }
    }
    if (($Effort -in @('high', 'xhigh', 'max') -or $Model -eq 'gpt-6.1-sol') -and [string]::IsNullOrWhiteSpace($EscalationReason))
    {
        $reasons.Add('high effort and gpt-6.1-sol require -EscalationReason')
    }
    if ($Sandbox -notin @('workspace-write', 'read-only'))
    {
        $reasons.Add("sandbox '$Sandbox' is not accepted")
    }
    [pscustomobject]@{ Ok = ($reasons.Count -eq 0); Reasons = @($reasons) }
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

function Invoke-SelfTest
{
    $fixtureDir = Join-Path $PSScriptRoot 'tests/agent-run'
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
$briefCheck = Test-Brief $briefText
$policy = Test-RunPolicy $Model $Effort $EscalationReason $Sandbox
if (-not $briefCheck.Ok) { Write-Host "Missing brief sections: $($briefCheck.Missing -join ', ')" }
if (-not $policy.Ok) { $policy.Reasons | ForEach-Object { Write-Host "Policy refusal: $_" } }
if (-not $briefCheck.Ok -or -not $policy.Ok) { exit 2 }
if ($ValidateOnly) { Write-Host 'Brief and policy checks passed.'; exit 0 }

function Resolve-RepositoryPath([string]$Path)
{
    if ([IO.Path]::IsPathRooted($Path)) { return [IO.Path]::GetFullPath($Path) }
    [IO.Path]::GetFullPath((Join-Path (Split-Path $PSScriptRoot -Parent) $Path))
}

$resolvedWorktree = (Resolve-Path -LiteralPath $Worktree -ErrorAction SilentlyContinue).Path
if (-not $resolvedWorktree -or -not (Test-Path (Join-Path $resolvedWorktree '.git')))
{
    $gitCheck = & git -C $Worktree rev-parse --is-work-tree 2>$null
    if ($LASTEXITCODE -ne 0 -or $gitCheck -ne 'true') { Write-Host 'Refused: worktree is not a git worktree'; exit 2 }
    $resolvedWorktree = (Resolve-Path -LiteralPath $Worktree).Path
}
# The npm install puts codex.ps1/codex.cmd shims on PATH, which Start-Process cannot start with redirected streams;
# run node on the package's codex.js instead (a real codex.exe is used as is).
$codexCommand = Get-Command codex -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $codexCommand)
{
    Write-Host 'Refused: codex is not on PATH'
    exit 2
}
$codexFile = $codexCommand.Source
$codexPrefix = @()
if ([IO.Path]::GetExtension($codexFile) -ne '.exe')
{
    $codexScript = Join-Path (Split-Path $codexFile -Parent) 'node_modules/@openai/codex/bin/codex.js'
    $node = Get-Command node -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not (Test-Path -LiteralPath $codexScript) -or -not $node)
    {
        Write-Host "Refused: cannot resolve the codex npm shim to node + codex.js ($codexScript)"
        exit 2
    }
    $codexFile = $node.Source
    $codexPrefix = @("`"$codexScript`"")
}
$started = [DateTimeOffset]::UtcNow
$briefName = [IO.Path]::GetFileNameWithoutExtension($briefPath)
if (-not $LogDirectory) { $LogDirectory = Join-Path ([IO.Path]::GetTempPath()) ('pointframe-agent-runs/' + $started.ToLocalTime().ToString('yyyyMMdd-HHmmss') + '-' + $(if ($Task) { $Task } else { $briefName })) }
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
$recordPath = Resolve-RepositoryPath 'plan/agent-runs.jsonl'
New-Item -ItemType Directory -Path (Split-Path $recordPath -Parent) -Force | Out-Null
[IO.File]::AppendAllText($recordPath, (($record | ConvertTo-Json -Compress -Depth 8) + "`r`n"), [Text.UTF8Encoding]::new($false))
if ($Task)
{
    & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'status.ps1') -Set -Id $Task -Stage "executor $outcome"
}
Write-Host ('RUN {0}: {1} {2:N1}m tokens={3}/{4} log={5}' -f $(if ($Task) { $Task } else { $briefName }), $outcome, $minutes, $usage.Input, $usage.Output, $LogDirectory)
if ($outcome -eq 'completed') { exit 0 }
if ($outcome -eq 'budget-exceeded') { exit 3 }
exit 1
