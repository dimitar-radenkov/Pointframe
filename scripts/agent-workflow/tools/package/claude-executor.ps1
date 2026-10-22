# ---- Claude executor ----------------------------------------------------------------------------------------------

$script:ClaudeModels = @{ 'claude-sonnet' = 'claude-sonnet-5-5'; 'claude-haiku' = 'claude-haiku-5-5' }
$script:ClaudeFamilies = @{ 'claude-sonnet' = 'sonnet'; 'claude-haiku' = 'haiku' }
# $script:ModelBound and $script:ExecutorBound are set in package.ps1: $PSBoundParameters is empty in a dot-sourced file.
# -ClaudeExtraArgs is refused unless every argument is on this list. It is empty on purpose: the runner owns every claude
# argument (settings, tools, MCP, resume, model, permission mode, setting sources, directories, system prompt).
$script:ClaudeExtraAllowed = @()
$script:ClaudeRunnerOwned = '^(settings|setting-sources|allowed-?tools|disallowed-?tools|tools|mcp-config|strict-mcp-config|resume|continue|fork-session|model|permission-mode|permission-prompt-tool|add-dir|system-prompt|append-system-prompt|output-format|input-format|print|p|plugin-dir|dangerously.*|allow-dangerously.*)$'

function Format-WindowsArgument([string]$Value)
{
    # CommandLineToArgvW quoting: quotes get a backslash, backslashes before a quote are doubled, and so are trailing ones.
    if ($Value -ne '' -and $Value -notmatch '[\s"]') { return $Value }
    $escaped = [regex]::Replace($Value, '(\\*)"', '$1$1\"')
    $escaped = [regex]::Replace($escaped, '(\\+)$', '$1$1')
    '"' + $escaped + '"'
}

function Get-JsonProperty($Object, [string]$Name)
{
    if ($null -eq $Object -or $Object -isnot [pscustomobject]) { return $null }
    $property = $Object.PSObject.Properties[$Name]
    if ($property) { $property.Value } else { $null }
}

function Get-Field($Record, [string]$Name)
{
    # Runs written before a field existed do not have it; a missing field is null, never an error.
    if ($null -eq $Record) { return $null }
    if ($Record -is [System.Collections.IDictionary]) { if ($Record.Contains($Name)) { return $Record[$Name] } else { return $null } }
    Get-JsonProperty $Record $Name
}

function Assert-NoBypass([string[]]$ArgList)
{
    # The runner never raises the permission mode: no bypass flag, no permission-mode flag at all, whoever supplied it.
    foreach ($item in @($ArgList))
    {
        if ([string]$item -match '(?i)bypass|dangerously|skip-permissions|permission-mode|permission-prompt') { throw "refused: the argument '$item' would change or bypass the permission mode; the runner runs Claude in the default mode with an allowlist only" }
    }
}

function Assert-ClaudeExtraArguments([string[]]$ArgList)
{
    # Both forms (--flag value and --flag=value) are judged by the flag name. A runner-owned flag is a refusal, and so is
    # anything that is not on the (empty) allowlist.
    Assert-NoBypass $ArgList
    foreach ($item in @($ArgList))
    {
        $text = [string]$item
        $name = if ($text -match '^--?([^=\s]+)(?:=.*)?$') { $Matches[1].ToLowerInvariant() } else { $null }
        if ($name -and $name -match $script:ClaudeRunnerOwned) { throw "refused: '$text' is set by the runner and cannot be overridden" }
        if ($text -notin $script:ClaudeExtraAllowed) { throw "refused: '$text' is not on the extra-argument allowlist (the allowlist is empty: the runner owns every claude argument)" }
    }
}

function Get-ModelFamily([string]$ModelName)
{
    if ($ModelName -match '(?i)haiku') { return 'haiku' }
    if ($ModelName -match '(?i)sonnet') { return 'sonnet' }
    if ($ModelName -match '(?i)opus') { return 'opus' }
    'unknown'
}

function Resolve-ClaudeLauncher
{
    # A script is accepted only as a marked self-test stub, so a test can never fall through to the real binary.
    $pwshExe = (Get-Command pwsh -CommandType Application | Select-Object -First 1).Source
    if ($ClaudePath)
    {
        $full = [IO.Path]::GetFullPath($ClaudePath)
        if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { throw "-ClaudePath not found: $ClaudePath" }
        if ($full -like '*.ps1')
        {
            if (-not (Test-SelfTestStub $full)) { throw '-ClaudePath may be a script only when it is a marked self-test stub' }
            return [pscustomobject]@{ File = $pwshExe; Prefix = @('-NoProfile', '-File', (Format-WindowsArgument $full)); Stub = $true; Display = $full }
        }
        return Resolve-ClaudeShim $full
    }
    $candidates = @(Get-Command claude -All -ErrorAction SilentlyContinue | Where-Object { $_.CommandType -in 'Application', 'ExternalScript' -and $_.Source })
    $found = (@($candidates | Where-Object { $_.Source -like '*.exe' }) + @($candidates | Where-Object { $_.Source -notlike '*.exe' })) | Select-Object -First 1
    if (-not $found) { throw 'claude was not found on PATH; pass -ClaudePath' }
    Resolve-ClaudeShim $found.Source
}

function Resolve-ClaudeShim([string]$Path)
{
    # An .exe is launched as is. An npm shim (claude.cmd, or the claude / claude.ps1 beside it) cannot be started without a
    # shell, so it is resolved from its own non-comment text, and only to the Claude Code entry point:
    # node_modules\@anthropic-ai\claude-code\ followed by bin\claude.exe or cli.js, canonicalized and kept under the shim's
    # own node_modules directory. Any other target is refused, so a crafted shim cannot select another program.
    $extension = [IO.Path]::GetExtension($Path).ToLowerInvariant()
    if ($extension -eq '.exe') { return [pscustomobject]@{ File = $Path; Prefix = @(); Stub = $false; Display = $Path } }
    $shim = $Path
    if ($extension -notin '.cmd', '.bat')
    {
        $sibling = [IO.Path]::ChangeExtension($Path, '.cmd')
        if ($extension -notin '', '.ps1' -or -not (Test-Path -LiteralPath $sibling -PathType Leaf)) { throw "cannot resolve the claude command '$Path': it is neither an .exe nor an npm shim with a .cmd beside it" }
        $shim = $sibling
    }
    $directory = Split-Path $shim -Parent
    $modules = [IO.Path]::GetFullPath((Join-Path $directory 'node_modules')).TrimEnd('\') + '\'
    $text = (@(Get-Content -LiteralPath $shim | Where-Object { $_ -notmatch '^\s*(@?rem(\s|$)|::)' })) -join "`n"
    $match = [regex]::Match($text, '(?i)node_modules[\\/]@anthropic-ai[\\/]claude-code[\\/](bin[\\/]claude\.exe|cli\.js)(?=["\s]|$)')
    if (-not $match.Success) { throw "cannot resolve the npm shim '$shim' to the claude entry point: it names no node_modules\@anthropic-ai\claude-code\bin\claude.exe or cli.js" }
    $target = [IO.Path]::GetFullPath((Join-Path $directory ($match.Value -replace '/', '\')))
    if (-not $target.StartsWith($modules, [StringComparison]::OrdinalIgnoreCase)) { throw "cannot resolve the npm shim '$shim': its target is outside the shim's node_modules ($target)" }
    if (-not (Test-Path -LiteralPath $target -PathType Leaf)) { throw "cannot resolve the npm shim '$shim': the entry point it names does not exist ($target)" }
    if ($target -like '*.exe') { return [pscustomobject]@{ File = $target; Prefix = @(); Stub = $false; Display = $target } }
    $node = Join-Path $directory 'node.exe'
    if (-not (Test-Path -LiteralPath $node -PathType Leaf))
    {
        $onPath = Get-Command node -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $onPath) { throw "cannot resolve the npm shim '$shim': node.exe was not found beside it or on PATH" }
        $node = $onPath.Source
    }
    [pscustomobject]@{ File = $node; Prefix = @((Format-WindowsArgument $target)); Stub = $false; Display = "$node $target" }
}

function Assert-SelfTestLauncher($Launcher)
{
    # While the self-test runs (it exports AW_PKG_SELFTEST to its children) only a marked stub, or a script the self-test
    # wrote under its own temp root, may be started. Anything else would be a real claude.
    if (-not $env:AW_PKG_SELFTEST) { return }
    if ($Launcher.Stub) { return }
    $selfRoot = [string]$env:AW_PKG_SELFTEST_ROOT
    if ($selfRoot -and ([string]$Launcher.Display).IndexOf($selfRoot, [StringComparison]::OrdinalIgnoreCase) -ge 0) { return }
    throw "self-test guard: refusing to start '$($Launcher.Display)', which is not a marked self-test stub"
}

function Get-ClaudeCliVersion($Launcher)
{
    # The probe is a child like any other: inside a job, stdin closed, bounded by -ProbeSeconds, killed with its tree.
    Assert-SelfTestLauncher $Launcher
    Initialize-ProcessJob
    $job = [AwProcessJob]::Create()
    if ($job -eq [IntPtr]::Zero) { return $null }
    $base = Join-Path ([IO.Path]::GetTempPath()) "claude-version-$([guid]::NewGuid().ToString('N'))"
    $process = $null
    $script:ClaudeVersionProbeProblem = $null
    try
    {
        $process = Start-ProcessInJob $job $Launcher.File (@($Launcher.Prefix) + @('--version')) ([IO.Path]::GetTempPath()) $null "$base.out" "$base.err"
        if (-not (Wait-ProcessExit $process ([long]($ProbeSeconds * 1000)))) { $script:ClaudeVersionProbeProblem = "the version probe did not exit within $ProbeSeconds s"; return $null }
        $probeExit = Get-ProcessExitCode $process
        if ($probeExit -ne 0) { $script:ClaudeVersionProbeProblem = "the version probe exited with code '$probeExit'"; return $null }
        # Most likely cause of the intermittent CI failure (#271; not reproduced locally, every other checked field is
        # deterministic): this read used Get-Content with -ErrorAction SilentlyContinue, so a transient sharing violation on
        # the freshly written temp file (a scanner or indexer holding it for a moment) was swallowed and the version was
        # recorded as null. The probe's exit does not guarantee the file is readable at that instant, so the read now waits,
        # bounded, until it is readable and keeps the reason when it never is (run.claudeCliVersionProblem).
        $readDeadline = [DateTime]::UtcNow.AddSeconds(5)
        while ($true)
        {
            try
            {
                $stream = [IO.File]::Open("$base.out", [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
                try { $line = ([IO.StreamReader]::new($stream)).ReadLine() } finally { $stream.Dispose() }
                if ($line) { return ([string]$line).Trim() }
                $script:ClaudeVersionProbeProblem = 'the version probe printed no version'
            }
            catch [IO.IOException] { $script:ClaudeVersionProbeProblem = "the version probe output could not be read: $($_.Exception.Message)" }
            if ([DateTime]::UtcNow -ge $readDeadline) { return $null }
            Start-Sleep -Milliseconds 100
        }
    }
    catch { $script:ClaudeVersionProbeProblem = "the version probe failed: $($_.Exception.Message)" }
    finally
    {
        Stop-ProcessJob $job 5 | Out-Null
        if ($process) { [AwProcessJob]::CloseProcess($process.Handle) }
        [AwProcessJob]::Close($job)
        Remove-Item -LiteralPath "$base.out", "$base.err" -Force -ErrorAction SilentlyContinue
    }
    $null
}

# The oldest Claude CLI that serves each model: the kit's own table. package.minCliVersions of the configuration is merged over it.
$script:ClaudeMinCliVersions = @{ 'claude-opus-5-5' = '2.1.280' }

function Get-ClaudeMinCliVersions
{
    $table = @{}
    foreach ($key in $script:ClaudeMinCliVersions.Keys) { $table[$key] = [string]$script:ClaudeMinCliVersions[$key] }
    if ($script:Ctx)
    {
        $configured = Get-AwSetting (Get-AwSection $script:Ctx 'package') 'minCliVersions' @{}
        foreach ($key in $configured.Keys) { $table[[string]$key] = [string]$configured[$key] }
    }
    $table
}

function ConvertTo-CliVersion([string]$Text)
{
    # The leading major.minor.patch of what `claude --version` prints ("2.1.280 (Claude Code)"); $null when there is none.
    $parsed = [regex]::Match($Text, '(\d+)\.(\d+)\.(\d+)')
    if (-not $parsed.Success) { return $null }
    [version]::new([int]$parsed.Groups[1].Value, [int]$parsed.Groups[2].Value, [int]$parsed.Groups[3].Value)
}

function Get-CliVersionProblem([string]$Model, [string]$Reported, [string]$ProbeProblem)
{
    # $null when the CLI can serve the model (or the model has no minimum), else the actionable refusal. A minimum that cannot be checked
    # because the probe produced no version is a refusal too: the round is not started on a guess.
    $table = Get-ClaudeMinCliVersions
    if (-not $table.ContainsKey($Model)) { return $null }
    $minimum = ConvertTo-CliVersion $table[$Model]
    $found = ConvertTo-CliVersion $Reported
    $update = 'Update it (npm install -g @anthropic-ai/claude-code@latest) or pass -ClaudePath to a newer claude. No review round was spent.'
    if (-not $found) { return "refused: the claude CLI version could not be read ($(if ($ProbeProblem) { $ProbeProblem } else { "it printed '$Reported'" })), and $Model needs $minimum or newer. $update" }
    if ($found -lt $minimum) { return "refused: the claude CLI is version $found, but $Model needs $minimum or newer. $update" }
    $null
}

function Get-ClaudeAuthMode
{
    # Presence only: the value of the variable is never read.
    if (Test-Path Env:ANTHROPIC_API_KEY) { 'api-key (ANTHROPIC_API_KEY is set)' } else { 'subscription (no ANTHROPIC_API_KEY in the environment)' }
}

function Get-ClaudeToolList([bool]$ReadOnly)
{
    # Bash only: the PowerShell tool refuses a nested pwsh ("cannot be validated"), so every shell command goes through Bash.
    if ($ReadOnly) { 'Read,Glob,Grep,Bash' } else { 'Read,Edit,Write,Glob,Grep,Bash' }
}

function Get-ClaudeExecutorRules([string]$Worktree, [string]$Role = 'executor')
{
    @(
        "You are the executor of one work package. Work only in the worktree $Worktree.",
        "Start with: pwsh scripts/kb.ps1 init -Role $Role",
        'Your working directory is already the package worktree: run git commands there without -C.',
        ('Use the Bash tool for every shell command, including `pwsh scripts/...` and `' + (Get-PackageCommand 'build').exe + ' ...`.'),
        'Use absolute paths inside the worktree and set an explicit working directory for every process.',
        'Never run a command that reads stdin in the background (for example python -).',
        'Never commit, push or open a pull request, and never write outside the worktree.',
        'If a tool call is denied, do not look for a way around it: report it.',
        'If a check cannot run in this environment, report it on its own line as ENV-BLOCKED: <command>: <error> and stop retrying it.',
        $(if ($Role -eq 'helper') { 'You are a read-only helper: you have no Edit or Write tool and must not change any file.' } else { '' }),
        "Accepted limitation: the command allowlist guards against accidental actions (commit, push, gh, network); the worktree is writable and $((Get-PackageCommand 'build').exe) $((Get-PackageCommand 'build').args[0]) and $((Get-PackageCommand 'test').exe) $((Get-PackageCommand 'test').args[0]) run code an executor authors, so it is not a sandbox. The main-checkout guard, job containment and review are the real controls.",
        'End with a report: what changed, the checks you ran and their results, and what you could not check.'
    ) | Where-Object { $_ } | ForEach-Object { $_ } | Join-String -Separator ' '
}

function New-ClaudeSettings([string]$Path, [string]$Worktree, [bool]$ReadOnly, [string[]]$Scripts = @())
{
    # Allowlist, nothing else: git reads (also as git -C <this worktree>), the kb script's read commands, the verify script
    # and the scripts the brief names (executors only), dotnet build/test/format (executors only). A helper has no Edit or
    # Write tool and no mutating shell rule.
    $shell = @('git status:*', 'git diff:*', 'git log:*', 'git show:*')
    $forward = ($Worktree -replace '\\', '/').TrimEnd('/')
    $back = ($Worktree -replace '/', '\').TrimEnd('\')
    foreach ($form in @($forward, $back | Select-Object -Unique))
    {
        foreach ($sub in @('status', 'diff', 'log', 'show')) { $shell += @("git -C $form ${sub}:*", "git -C `"$form`" ${sub}:*") }
    }
    # A helper only reads: init, read and changed by prefix, and check solely as the exact command with -NoFix (no prefix
    # rule, because a bare check rewrites generated KB blocks and the baseline). Executors also get check and selftest.
    foreach ($sub in @('init', 'read', 'changed')) { $shell += @("pwsh scripts/kb.ps1 ${sub}:*", "pwsh -NoProfile -File scripts/kb.ps1 ${sub}:*") }
    if ($ReadOnly) { $shell += @('pwsh scripts/kb.ps1 check -NoFix', 'pwsh -NoProfile -File scripts/kb.ps1 check -NoFix') }
    if (-not $ReadOnly)
    {
        foreach ($sub in @('check', 'selftest')) { $shell += @("pwsh scripts/kb.ps1 ${sub}:*", "pwsh -NoProfile -File scripts/kb.ps1 ${sub}:*") }
        foreach ($kind in @('build', 'test', 'format')) { $c = Get-PackageCommand $kind; $shell += "$($c.exe) $($c.args[0]):*" }
        $shell += @('pwsh scripts/verify.ps1:*', 'pwsh -NoProfile -File scripts/verify.ps1:*')
        foreach ($script in @($Scripts)) { $shell += @("pwsh ${script}:*", "pwsh -NoProfile -File ${script}:*") }
    }
    $tools = if ($ReadOnly) { @('Read', 'Glob', 'Grep') } else { @('Read', 'Edit', 'Write', 'Glob', 'Grep') }
    $allow = $tools + @($shell | ForEach-Object { "Bash($_)" })
    $denied = @('git commit:*', 'git push:*', 'git add:*', 'git reset:*', 'git checkout:*', 'git merge:*', 'git rebase:*', 'git clean:*', 'git stash:*', 'gh:*')
    $deny = @('WebFetch', 'WebSearch', 'Agent', 'Task', 'NotebookEdit', 'PowerShell') + $(if ($ReadOnly) { @('Edit', 'Write') } else { @() }) + @($denied | ForEach-Object { "Bash($_)" })
    $settings = [ordered]@{ permissions = [ordered]@{ defaultMode = 'default'; disableBypassPermissionsMode = 'disable'; allow = $allow; deny = $deny } }
    New-Item -ItemType Directory -Path (Split-Path $Path -Parent) -Force | Out-Null
    [IO.File]::WriteAllText($Path, (($settings | ConvertTo-Json -Depth 6) + "`r`n"), [Text.UTF8Encoding]::new($false))
}

function New-StreamMonitor([string]$Path)
{
    @{
        Path = $Path; Offset = [long]0; Messages = @{}; Input = [long]0; CacheCreation = [long]0; CacheRead = [long]0; Output = [long]0; HasUsage = $false; ResultUsage = $null; PeakInput = [long]0
        SessionId = $null; Model = $null; Cwd = $null; ToolUses = @{}; ToolCount = 0; Denials = [Collections.Generic.List[hashtable]]::new(); DenialIds = [Collections.Generic.HashSet[string]]::new()
        ResultSeen = $false; ResultText = $null; ResultError = $false; ResultSubtype = $null; CostUsd = $null; Events = 0
    }
}

function Get-MonitorInputTotal($Monitor) { [long]$Monitor.Input + [long]$Monitor.CacheCreation + [long]$Monitor.CacheRead }

function Get-MonitorEnforcedInput($Monitor)
{
    # What the budget is enforced against: the larger of the per-message sums and any terminal usage seen so far, and never
    # less than the largest value already observed (a later, smaller report cannot give consumption back).
    $terminal = [long]0
    if ($Monitor.ResultUsage) { foreach ($key in @('input', 'cacheCreation', 'cacheRead')) { if ($null -ne $Monitor.ResultUsage[$key]) { $terminal += [long]$Monitor.ResultUsage[$key] } } }
    $Monitor.PeakInput = [Math]::Max([long]$Monitor.PeakInput, [Math]::Max((Get-MonitorInputTotal $Monitor), $terminal))
    [long]$Monitor.PeakInput
}

function Add-MonitorMessageUsage($Monitor, [string]$MessageId, $Usage)
{
    # One message is streamed as several events that repeat its usage; the output count grows across them, so each
    # message contributes the largest value it reported for every field.
    if ($null -eq $Usage -or $Usage -isnot [pscustomobject]) { return }
    $map = @{ Input = 'input_tokens'; CacheCreation = 'cache_creation_input_tokens'; CacheRead = 'cache_read_input_tokens'; Output = 'output_tokens' }
    if (-not $Monitor.Messages.ContainsKey($MessageId)) { $Monitor.Messages[$MessageId] = @{ Input = [long]0; CacheCreation = [long]0; CacheRead = [long]0; Output = [long]0 } }
    $seen = $Monitor.Messages[$MessageId]
    foreach ($key in $map.Keys)
    {
        $value = Get-JsonProperty $Usage $map[$key]
        if ($null -ne $value -and [long]$value -gt $seen[$key]) { $Monitor[$key] = [long]$Monitor[$key] + ([long]$value - $seen[$key]); $seen[$key] = [long]$value; $Monitor.HasUsage = $true }
    }
}

function Add-MonitorDenial($Monitor, [string]$ToolUseId, [string]$Tool, [string]$Reason)
{
    $key = if ($ToolUseId) { $ToolUseId } else { "denial-$($Monitor.Denials.Count)" }
    if (-not $Monitor.DenialIds.Add($key)) { return }
    $text = ([string]$Reason -replace '\s+', ' ').Trim()
    if ($text.Length -gt 300) { $text = $text.Substring(0, 300) }
    $Monitor.Denials.Add(@{ tool = $Tool; toolUseId = $ToolUseId; reason = $text })
}

function Add-StreamLine($Monitor, [string]$Line)
{
    if ([string]::IsNullOrWhiteSpace($Line)) { return }
    try { $item = $Line | ConvertFrom-Json } catch { return }
    if ($null -eq $item -or $item -isnot [pscustomobject]) { return }
    $Monitor.Events++
    switch ([string](Get-JsonProperty $item 'type'))
    {
        'system'
        {
            # The session id is taken from the init event only: a result-only id is not an identity.
            if ((Get-JsonProperty $item 'subtype') -eq 'init' -and -not $Monitor.SessionId)
            {
                $Monitor.SessionId = [string](Get-JsonProperty $item 'session_id'); $Monitor.Model = [string](Get-JsonProperty $item 'model'); $Monitor.Cwd = [string](Get-JsonProperty $item 'cwd')
            }
        }
        'assistant'
        {
            $message = Get-JsonProperty $item 'message'
            $id = [string](Get-JsonProperty $message 'id')
            if (-not $id) { $id = "no-id-$($Monitor.Events)" }
            Add-MonitorMessageUsage $Monitor $id (Get-JsonProperty $message 'usage')
            foreach ($block in @(Get-JsonProperty $message 'content'))
            {
                if ((Get-JsonProperty $block 'type') -eq 'tool_use') { $Monitor.ToolUses[[string](Get-JsonProperty $block 'id')] = [string](Get-JsonProperty $block 'name'); $Monitor.ToolCount++ }
            }
        }
        'user'
        {
            foreach ($block in @(Get-JsonProperty (Get-JsonProperty $item 'message') 'content'))
            {
                if ((Get-JsonProperty $block 'type') -ne 'tool_result' -or (Get-JsonProperty $block 'is_error') -ne $true) { continue }
                $content = Get-JsonProperty $block 'content'
                $text = if ($content -is [string]) { $content } else { (@($content) | ForEach-Object { [string](Get-JsonProperty $_ 'text') }) -join ' ' }
                if ($text -match "(?i)(requested permissions? to use|haven't granted|has been denied|was denied|not allowed|classifier|auto mode|blocked by|requires approval|don't have permission)")
                {
                    $useId = [string](Get-JsonProperty $block 'tool_use_id')
                    Add-MonitorDenial $Monitor $useId $(if ($Monitor.ToolUses.ContainsKey($useId)) { $Monitor.ToolUses[$useId] } else { '' }) $text
                }
            }
        }
        'result'
        {
            $Monitor.ResultSeen = $true
            $Monitor.ResultText = [string](Get-JsonProperty $item 'result')
            $Monitor.ResultError = ((Get-JsonProperty $item 'is_error') -eq $true)
            $Monitor.ResultSubtype = [string](Get-JsonProperty $item 'subtype')
            $cost = Get-JsonProperty $item 'total_cost_usd'
            if ($null -ne $cost) { $Monitor.CostUsd = [double]$cost }
            $usage = Get-JsonProperty $item 'usage'
            if ($usage -is [pscustomobject])
            {
                $reported = @{}
                foreach ($pair in @(@('input', 'input_tokens'), @('cacheCreation', 'cache_creation_input_tokens'), @('cacheRead', 'cache_read_input_tokens'), @('output', 'output_tokens')))
                {
                    $value = Get-JsonProperty $usage $pair[1]
                    $reported[$pair[0]] = if ($null -ne $value) { [long]$value } else { $null }
                }
                $Monitor.ResultUsage = $reported
            }
            foreach ($denial in @(Get-JsonProperty $item 'permission_denials'))
            {
                if ($null -eq $denial) { continue }
                Add-MonitorDenial $Monitor ([string](Get-JsonProperty $denial 'tool_use_id')) ([string](Get-JsonProperty $denial 'tool_name')) 'denied by the permission settings (result.permission_denials)'
            }
        }
    }
}

function Read-StreamMonitor($Monitor, [switch]$Final)
{
    # Reads what the executor appended since the last call. Only complete lines are consumed until the final read.
    if (-not (Test-Path -LiteralPath $Monitor.Path -PathType Leaf)) { return }
    $buffer = $null
    $read = 0
    try
    {
        $stream = [IO.FileStream]::new($Monitor.Path, 'Open', 'Read', 'ReadWrite')
        try
        {
            if ($stream.Length -le $Monitor.Offset) { return }
            $stream.Seek($Monitor.Offset, 'Begin') | Out-Null
            $buffer = [byte[]]::new($stream.Length - $Monitor.Offset)
            $read = $stream.Read($buffer, 0, $buffer.Length)
        }
        finally { $stream.Dispose() }
    }
    catch { return }
    if ($read -le 0) { return }
    $end = if ($Final) { $read } else { [Array]::LastIndexOf($buffer, [byte]10, $read - 1) + 1 }
    if ($end -le 0) { return }
    $Monitor.Offset = [long]$Monitor.Offset + $end
    foreach ($line in ([Text.Encoding]::UTF8.GetString($buffer, 0, $end) -split "`r?`n")) { Add-StreamLine $Monitor $line }
}

function Get-ClaudeTokenRecord($Monitor)
{
    # The terminal result's usage is the authoritative total. The per-message sums (largest value per message) are kept
    # next to it and cross-checked; with no terminal result the per-message sums stand in and the record says so.
    $perMessage = @{ input = [long]$Monitor.Input; cacheCreation = [long]$Monitor.CacheCreation; cacheRead = [long]$Monitor.CacheRead; output = [long]$Monitor.Output }
    $reported = $Monitor.ResultUsage
    $auth = $perMessage
    $check = 'no terminal result usage: per-message sums used'
    if ($reported)
    {
        $auth = @{}
        foreach ($key in $perMessage.Keys) { $auth[$key] = if ($null -ne $reported[$key]) { [long]$reported[$key] } else { $perMessage[$key] } }
        $differences = @($perMessage.Keys | Sort-Object | Where-Object { $perMessage[$_] -ne $auth[$_] } | ForEach-Object { "$_ per-message $($perMessage[$_]) vs reported $($auth[$_])" })
        $check = if ($differences.Count) { 'differs: ' + ($differences -join '; ') } else { 'match' }
    }
    @{
        input = ([long]$auth.input + [long]$auth.cacheCreation + [long]$auth.cacheRead); cached = $auth.cacheRead; output = $auth.output; freshInput = $auth.input; cacheCreation = $auth.cacheCreation
        source = $(if ($reported) { 'terminal result usage' } else { 'per-message sums' })
        perMessage = @{ input = ($perMessage.input + $perMessage.cacheCreation + $perMessage.cacheRead); output = $perMessage.output }
        reported = $(if ($reported) { @{ input = ([long]$auth.input + [long]$auth.cacheCreation + [long]$auth.cacheRead); output = $reported.output } } else { $null })
        crossCheck = $check
        reportedCostUsd = $Monitor.CostUsd; costNote = 'reported by the CLI, not an invoice'; toolCalls = $Monitor.ToolCount
    }
}

function Get-ReconstructedClaudeAccounting($Run)
{
    # A run whose wrapper died: rebuild tokens, session id and elapsed time from the archived stream. Anything that cannot
    # be rebuilt is named in the accounting record, and later resumes of the chain are refused.
    $path = Join-Path ([string]$Run.logDirectory) 'events.jsonl'
    $monitor = New-StreamMonitor $path
    $problems = [Collections.Generic.List[string]]::new()
    $elapsed = $null
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { $problems.Add('events.jsonl is missing') }
    else
    {
        Read-StreamMonitor $monitor -Final
        if (-not $monitor.SessionId) { $problems.Add('the stream has no init event with a session id') }
        if (-not $monitor.HasUsage -and -not $monitor.ResultUsage) { $problems.Add('the stream has no usage') }
    }
    # The stream's last write is not the end of the run (an executor can hang after its last event), so the elapsed time is
    # a conservative upper bound: from the run's start to now, when recovery runs, capped at the run's own wall-clock limit.
    # The wrapper's death time is not recorded; if the start cannot be established the elapsed time is unknown.
    try
    {
        $started = [DateTimeOffset]::Parse([string]$Run.startedUtc)
        $elapsed = [Math]::Max(0, ([DateTimeOffset]::UtcNow - $started).TotalSeconds)
        $limit = Get-Field $Run 'maxMinutes'
        if ($null -ne $limit) { $elapsed = [Math]::Min($elapsed, [double]$limit * 60) }
        $elapsed = [Math]::Round($elapsed, 1)
    }
    catch { $problems.Add("the elapsed time cannot be established: $($_.Exception.Message)") }
    # The same identity rule as a live run: the id must come from the init event and equal the one this run resumed.
    $identityProblem = $null
    $resumedFrom = Get-Field $Run 'resumedFrom'
    if (-not $monitor.SessionId) { $identityProblem = 'the stream has no init event with a session id' }
    elseif ($resumedFrom -and $monitor.SessionId -ne [string](Get-Field $resumedFrom 'sessionId')) { $identityProblem = "the resumed session answered with id $($monitor.SessionId), not $(Get-Field $resumedFrom 'sessionId')" }
    @{
        tokens = (Get-ClaudeTokenRecord $monitor); sessionId = $(if ($identityProblem) { $null } else { $monitor.SessionId }); sessionIdentityProblem = $identityProblem; elapsedSeconds = $elapsed; denials = @($monitor.Denials)
        accounting = @{ known = ($problems.Count -eq 0); reconstructed = $true; elapsedIsUpperBound = $true; reason = ($problems -join '; ') }
    }
}

