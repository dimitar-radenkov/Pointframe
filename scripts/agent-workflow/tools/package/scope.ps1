# ---- Scope: what a run changed in its worktree --------------------------------------------------------------------

function Test-BuildOutputPath([string]$Path)
{
    # Build and tool output is rewritten by every dotnet build; it is not a deliverable and not scope.
    $Path -match '(^|/)(bin|obj|TestResults|artifacts|node_modules|\.vs)(/|$)'
}

function Get-WorktreeSnapshot([string]$Wt)
{
    # Path -> content hash for every tracked, untracked and ignored file (build output excepted), so a run's own changes
    # are the difference between two snapshots: edits, deletions, new files, ignored files, and renames as both ends.
    $listing = (& git -C $Wt ls-files -z --cached --others 2>&1 | ForEach-Object { "$_" }) -join ''
    if ($LASTEXITCODE -ne 0) { throw "git ls-files failed in ${Wt}: $listing" }
    $snapshot = @{}
    foreach ($name in @($listing -split "`0" | Where-Object { $_ } | Select-Object -Unique))
    {
        $path = $name -replace '\\', '/'
        if (Test-BuildOutputPath $path) { continue }
        $full = [IO.Path]::Combine($Wt, $name)
        $snapshot[$path] = if (Test-Path -LiteralPath $full -PathType Leaf)
        {
            try
            {
                $stream = [IO.File]::Open($full, 'Open', 'Read', 'ReadWrite')
                try { [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) } finally { $stream.Dispose() }
            }
            catch { 'unreadable' }
        }
        else { 'missing' }
    }
    $snapshot
}

function Get-SnapshotChanges($Before, $After)
{
    $names = @($Before.Keys) + @($After.Keys) | Select-Object -Unique
    @($names | Where-Object { $Before[$_] -ne $After[$_] } | Sort-Object)
}

function ConvertTo-GlobRegex([string]$Pattern)
{
    # Glob semantics: * matches within one path segment (never /), ** matches across segments, **/ also matches nothing,
    # ? matches one character that is not /. Everything else is literal. Whole-path match, case-insensitive.
    $builder = [Text.StringBuilder]::new('^')
    $i = 0
    while ($i -lt $Pattern.Length)
    {
        $c = $Pattern[$i]
        if ($c -eq '*')
        {
            if ($i + 1 -lt $Pattern.Length -and $Pattern[$i + 1] -eq '*')
            {
                if ($i + 2 -lt $Pattern.Length -and $Pattern[$i + 2] -eq '/') { [void]$builder.Append('(?:.*/)?'); $i += 3 } else { [void]$builder.Append('.*'); $i += 2 }
            }
            else { [void]$builder.Append('[^/]*'); $i++ }
        }
        elseif ($c -eq '?') { [void]$builder.Append('[^/]'); $i++ }
        else { [void]$builder.Append([regex]::Escape([string]$c)); $i++ }
    }
    [void]$builder.Append('$')
    $builder.ToString()
}

function Test-PathAllowed([string]$Path, [string[]]$Allowed)
{
    # An entry ending in / is a directory (everything below it); any other entry is a glob (see ConvertTo-GlobRegex).
    foreach ($entry in $Allowed)
    {
        if ($entry.EndsWith('/')) { if ($Path.StartsWith($entry, [StringComparison]::OrdinalIgnoreCase)) { return $true } }
        elseif ([regex]::IsMatch($Path, (ConvertTo-GlobRegex $entry), 'IgnoreCase')) { return $true }
    }
    $false
}

function Get-ScopeViolations($Before, $After, [string]$Scope, [string[]]$Allowed)
{
    # helper: read-only, so any change is a violation. mechanical: every changed file must be an allowed path.
    $changed = @(Get-SnapshotChanges $Before $After)
    if ($Scope -eq 'helper') { return $changed }
    @($changed | Where-Object { -not (Test-PathAllowed $_ $Allowed) })
}

function Initialize-ClaudeRun($State, [string]$RunId, [string]$RunDir)
{
    # Everything the Claude launch needs, decided and validated before the run is recorded or anything is started.
    if ($PromptFile -and -not $Resume) { throw '-PromptFile is only used together with -Resume' }
    if ($Resume -and -not $PromptFile) { throw '-Resume needs -PromptFile with the fix request' }
    if ($PromptFile -and -not (Test-Path -LiteralPath $PromptFile -PathType Leaf)) { throw "-PromptFile not found: $PromptFile" }
    $extra = @(([string]$ClaudeExtraArgs).Trim() -split '\s+' | Where-Object { $_ })
    Assert-ClaudeExtraArguments $extra

    # The model belongs to the executor's family; a Haiku model is gated whatever the executor kind says.
    $model = if ($script:ModelBound) { $Model } else { $script:ClaudeModels[$Executor] }
    $family = Get-ModelFamily $model
    if ($family -ne $script:ClaudeFamilies[$Executor]) { throw "refused: model '$model' (family '$family') does not belong to executor $Executor" }

    $launcher = Resolve-ClaudeLauncher
    $briefText = Get-Content -LiteralPath $State.brief.path -Raw
    $scope = $null
    $allowed = @()
    $scripts = @()
    $manifest = Read-Manifest $State.manifest.path
    if ($family -eq 'haiku')
    {
        $match = [regex]::Match($briefText, '(?mi)^\s*Haiku-scope:\s*(helper|mechanical)\s*$')
        if (-not $match.Success) { throw "claude-haiku is only allowed when the brief carries a 'Haiku-scope: helper|mechanical' header line (W-39, W-42); this brief has none" }
        $scope = $match.Groups[1].Value.ToLowerInvariant()
        if ($scope -eq 'mechanical')
        {
            $allowed = @($manifest.allowedPaths)
            if ($allowed.Count -eq 0) { throw "Haiku-scope: mechanical needs an allowedPaths list in the check manifest" }
        }
    }
    $readOnly = ($scope -eq 'helper')
    $scripts = @($manifest.allowedScripts)

    $chainId = $RunId
    $prior = $null
    if ($Resume)
    {
        $candidates = @($State.runs | Where-Object { $_.provider -eq 'claude' -and (Get-Field $_ 'executorKind') -eq $Executor -and $_.sessionId -and $_.outcome -ne 'running' -and -not (Get-Field $_ 'sessionIdentityProblem') })
        if ($candidates.Count -eq 0)
        {
            $rejected = @($State.runs | Where-Object { $_.provider -eq 'claude' -and (Get-Field $_ 'executorKind') -eq $Executor -and (Get-Field $_ 'sessionIdentityProblem') } | ForEach-Object { "run $($_.runId): $(Get-Field $_ 'sessionIdentityProblem')" })
            throw "nothing to resume: no earlier $Executor run of this package has a session id from the init event that is consistent with its chain$(if ($rejected.Count) { ' (' + ($rejected -join '; ') + ')' })"
        }
        $prior = $candidates[-1]
        $chainId = [string](Get-Field $prior 'chainId')
    }
    $chain = @($State.runs | Where-Object { $_.provider -eq 'claude' -and (Get-Field $_ 'executorKind') -eq $Executor -and [string](Get-Field $_ 'chainId') -eq $chainId })
    $priorSeconds = 0.0
    $priorTokens = [long]0
    foreach ($earlier in $chain)
    {
        if ((Get-Field $earlier 'termination') -eq 'launch-failed') { continue }
        $accounting = Get-Field $earlier 'accounting'
        if ($accounting -and (Get-Field $accounting 'known') -eq $false) { throw "cannot resume: the accounting of run $($earlier.runId) is unknown ($(Get-Field $accounting 'reason')); the chain's budget cannot be carried" }
        $elapsedBefore = Get-Field $earlier 'elapsedSeconds'
        $tokensBefore = Get-Field (Get-Field $earlier 'tokens') 'input'
        if ($null -eq $elapsedBefore -or $null -eq $tokensBefore) { throw "cannot resume: run $($earlier.runId) has no recorded elapsed time or tokens; the chain's budget cannot be carried" }
        $priorSeconds += [double]$elapsedBefore
        $priorTokens += [long]$tokensBefore
    }
    $remainingSeconds = $MaxMinutes * 60 - $priorSeconds
    $remainingTokens = $MaxInputTokens - $priorTokens
    if ($remainingSeconds -le 0 -or $remainingTokens -le 0) { throw "the budget of this executor is used up (used $([int]$priorSeconds) s of $([int]($MaxMinutes * 60)) s, $priorTokens of $MaxInputTokens input tokens); raise -MaxMinutes or -MaxInputTokens to continue" }

    $wt = [string]$State.worktree
    New-Item -ItemType Directory -Path $RunDir -Force | Out-Null
    $settingsPath = Join-Path $RunDir 'claude-settings.json'
    $mcpPath = Join-Path $RunDir 'empty-mcp.json'
    $promptPath = Join-Path $RunDir 'prompt.md'
    New-ClaudeSettings $settingsPath $wt $readOnly $scripts
    [IO.File]::WriteAllText($mcpPath, "{`"mcpServers`":{}}`r`n", [Text.UTF8Encoding]::new($false))
    $promptText = if ($Resume) { Get-Content -LiteralPath $PromptFile -Raw }
    else { "You are the executor for the work package below. The worktree is $wt; work only there. Deliver the brief completely, run the checks it lists that your environment allows, and end with your report. The brief follows.`r`n`r`n$briefText" }
    [IO.File]::WriteAllText($promptPath, $promptText, [Text.UTF8Encoding]::new($false))

    $rules = Get-ClaudeExecutorRules $wt $(if ($readOnly) { 'helper' } else { 'executor' })
    # --setting-sources with an empty value loads no user, project or local settings: the runner's --settings is the only
    # source. --tools is the whole tool set the executor gets.
    $arguments = @('-p', '--model', $model, '--output-format', 'stream-json', '--verbose', '--settings', $settingsPath, '--setting-sources', '',
        '--tools', (Get-ClaudeToolList $readOnly), '--strict-mcp-config', '--mcp-config', $mcpPath, '--append-system-prompt', $rules)
    if ($MaxBudgetUsd -gt 0) { $arguments += @('--max-budget-usd', $MaxBudgetUsd.ToString([Globalization.CultureInfo]::InvariantCulture)) }
    if ($Resume) { $arguments += @('--resume', [string]$prior.sessionId) }
    $arguments += $extra
    Assert-NoBypass $arguments
    $recorded = @($arguments | ForEach-Object { if ($_ -eq $rules) { "<fixed executor rules, sha256 $((Get-StringSha256 $rules).Substring(0, 12))>" } else { $_ } })

    $cliVersion = Get-ClaudeCliVersion $launcher
    $cliVersionProblem = $script:ClaudeVersionProbeProblem
    @{
        Launcher = $launcher; Model = $model; Arguments = $arguments; RecordedArguments = $recorded; Stdin = $promptPath
        Settings = @{ path = $settingsPath; sha256 = (Get-Sha256 $settingsPath) }; Prompt = @{ path = $promptPath; sha256 = (Get-Sha256 $promptPath) }
        AuthMode = (Get-ClaudeAuthMode); Version = $cliVersion; VersionProblem = $(if ($cliVersion) { $null } else { $cliVersionProblem }); ChainId = $chainId
        ResumedFrom = $(if ($prior) { @{ runId = [string]$prior.runId; sessionId = [string]$prior.sessionId } } else { $null })
        Budget = @{ maxSeconds = ($MaxMinutes * 60); maxInputTokens = $MaxInputTokens; priorSeconds = [Math]::Round($priorSeconds, 1); priorInputTokens = $priorTokens; remainingSeconds = $remainingSeconds; remainingInputTokens = $remainingTokens; maxBudgetUsd = $(if ($MaxBudgetUsd -gt 0) { $MaxBudgetUsd } else { $null }) }
        Scope = $scope; Allowed = $allowed; ReadOnly = $readOnly; Tools = (Get-ClaudeToolList $readOnly)
    }
}

function Get-StringSha256([string]$Text)
{
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Text))).ToLowerInvariant()
}

function Get-NextTransitions($State, [bool]$SnapshotsOk = $true)
{
    if (-not $SnapshotsOk) { return @() }
    switch ([string]$State.stage)
    {
        'ready' { @('run', 'check') }
        'running' { @('run (recovers the abandoned run first)') }
        'executor-kill-failed' { @('run (reaps the surviving executor first)') }
        'creating' { @() }
        'creation-failed' { @() }
        'preflight-failed' { @() }
        'blocked-main-mutated' { @('recover -Reason <text>') }
        'checks-passed' { @('commit', 'run', 'check') }
        'committed' { @('review -Level light|heavy') }
        'reviewing' { @('review (recovers an abandoned review first)') }
        'review-passed' { @('accept -Reason <text>', 'review -Level light|heavy') }
        'review-failed' { @('disposition -Finding <id> -Accept|-Reject -Reason <text>', 'fix', 'review -Level light|heavy') }
        'review-invalid' { @('review -Level light|heavy') }
        'needs-coordinator' { @('review -ResetBudget -Reason <text>', 'fix -ResetBudget -Reason <text>') }
        'accepted' { @('ship [-Title <text>] [-AllowDesktop] [-NoWait]') }
        'invalidated' { @('check') }
        'shipping' { @('ship (recovers the abandoned ship first)') }
        'needs-review' { @('review -Level light|heavy') }
        'ship-failed' { @('ship (resumes at the failed step)') }
        'desktop-required' { @('ship -AllowDesktop') }
        'no-remote' { @('ship -LocalMerge') }
        'auto-merge-enabled' { @('ship (waits for the merge, then reports the channels)') }
        'merged' { if ($State.ship -and (Get-Field (Get-Field $State.ship 'steps') 'channels') -and (Get-Field (Get-Field (Get-Field $State.ship 'steps') 'channels') 'status') -in @('done', 'not-configured')) { @() } else { @('ship (reports the release channels)') } }
        default { @('run', 'check') }
    }
}

