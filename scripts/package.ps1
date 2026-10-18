#Requires -Version 7.0
<#
Mechanics of one work package for a Codex or Claude executor, with durable state. No review, fix loop, commit or merge.

  pwsh scripts/package.ps1 new -Id <slug> -Brief <file> [-Checks <manifest.json>] [-DryRun]
  pwsh scripts/package.ps1 run -Id <slug> [-Executor codex|claude-sonnet|claude-haiku] [-Effort medium|high -EscalationReason <text>] [-MaxMinutes 30]
  pwsh scripts/package.ps1 run -Id <slug> -Executor claude-sonnet -Resume -PromptFile <fix-request.md>
  pwsh scripts/package.ps1 check -Id <slug>
  pwsh scripts/package.ps1 show -Id <slug> [-Json]
  pwsh scripts/package.ps1 -SelfTest

new     snapshots the brief and the check manifest (SHA-256, read-only), creates branch pkg/<slug> and its worktree from
        the fetched origin/master, preflight-builds the check targets, validates the brief with agent-run.ps1.
run     launches the Codex executor through agent-run.ps1 under a hard wall-clock bound, and fingerprints the main checkout
        before and after. A changed fingerprint stops the run (mainCheckoutMutated); nothing is ever reverted.
        -Executor claude-sonnet|claude-haiku launches `claude -p` through the same suspended job-object launch: the working
        directory is the worktree, stdin is the prompt file (then EOF), stdout is the stream-json log, settings come from a
        file in the package state directory (an allowlist, default permission mode, never a bypass), MCP is empty and strict.
        Permission denials in the stream are recorded and never retried. -MaxMinutes and -MaxInputTokens are enforced from
        outside (job kill) and are cumulative across -Resume runs of the same Claude session. claude-haiku needs a
        `Haiku-scope: helper|mechanical` header line in the brief; helper may change nothing, mechanical only the manifest's
        allowedPaths.
check   runs the manifest's checks in the worktree as the runner. Mutating steps run first (extra commands marked "mutates",
        dotnet format, CRLF normalization), then build, focused tests and the other extra commands. Every command records
        the content hash of the worktree it ran on; a result is fresh only while the worktree still has that hash.
show    prints stage, runs, checks (stale or incomplete evidence is reported), the guard and the allowed next steps.

The check manifest is JSON: { "build": ["App/App.csproj"], "tests": [{ "project": "App.Tests/App.Tests.csproj", "filter":
"FullyQualifiedName~Foo" }], "format": ["App/App.csproj"], "extra": [{ "name": "x", "exe": "pwsh", "args": ["-File", "x.ps1"],
"mutates": false }], "allowedPaths": ["docs/x.md", "scripts/lib/", "docs/*.md"], "allowedScripts": ["scripts/x.ps1"] }.
allowedPaths globs: * stays inside one path segment, ** crosses segments, ? is one character, a trailing / is a directory.
allowedScripts are the only scripts besides kb.ps1 (init|read|check|changed|selftest) and verify.ps1 a Claude executor may run.
State lives in <git common dir>/pointframe/packages/<slug>.json. Exit codes: 0 ok, 1 failure, 2 bad input, 5 main checkout mutated.
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('new', 'run', 'check', 'show')]
    [string]$Command,
    [string]$Id,
    [string]$Brief,
    [string]$Checks,
    [ValidateSet('medium', 'high')]
    [string]$Effort = 'medium',
    [string]$Model = 'gpt-6-luna',
    [string]$EscalationReason,
    [ValidateRange(0.01, 1440)]
    [double]$MaxMinutes = 30,
    [ValidateRange(1, [long]::MaxValue)]
    [long]$MaxInputTokens = 20000000,
    [string]$CodexVersion,
    [ValidateSet('codex', 'claude-sonnet', 'claude-haiku')]
    [string]$Executor = 'codex',
    [switch]$Resume,
    [string]$PromptFile,
    [string]$ClaudePath,
    [string]$ClaudeExtraArgs,
    [ValidateRange(0, 1000)]
    [double]$MaxBudgetUsd = 0,
    [ValidateRange(1, 300)]
    [int]$ProbeSeconds = 15,
    [switch]$Json,
    [Alias('WhatIf')]
    [switch]$DryRun,
    [string]$Repo = (Split-Path $PSScriptRoot -Parent),
    [string]$Worktree,
    [string]$BaseRef = 'origin/master',
    [string]$AgentRunScript = (Join-Path $PSScriptRoot 'agent-run.ps1'),
    [string]$StatusScript = (Join-Path $PSScriptRoot 'status.ps1'),
    [switch]$SimulateKillFailure,
    [ValidateRange(0, 3600)]
    [int]$LockTimeoutSeconds = 10,
    [ValidateRange(0, 3600)]
    [int]$GraceSeconds = 60,
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'lib/process-tree.ps1')
. (Join-Path $PSScriptRoot 'lib/codex.ps1')

$SchemaVersion = 1
$script:ExitCode = 0
$script:JsonDate = if ((Get-Command ConvertFrom-Json).Parameters.ContainsKey('DateKind')) { @{ DateKind = 'String' } } else { @{} }

function Get-Now { [DateTimeOffset]::UtcNow.ToString('o') }

function Get-Sha256([string]$Path)
{
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-OptionalSha256([string]$Path)
{
    if ($Path -and (Test-Path -LiteralPath $Path -PathType Leaf)) { return Get-Sha256 $Path }
    $null
}

function Invoke-Git([string]$Dir, [string[]]$GitArgs)
{
    $output = & git -C $Dir @GitArgs 2>&1
    if ($LASTEXITCODE -ne 0)
    {
        throw "git $($GitArgs -join ' ') failed in ${Dir}: $(($output | ForEach-Object { "$_" }) -join ' ')"
    }
    (($output | ForEach-Object { "$_" }) -join "`n").Trim()
}

function Get-RepoInfo([string]$Path)
{
    $top = [IO.Path]::GetFullPath((Invoke-Git $Path @('rev-parse', '--show-toplevel')))
    $common = [IO.Path]::GetFullPath((Invoke-Git $Path @('rev-parse', '--path-format=absolute', '--git-common-dir')))
    $main = $top
    foreach ($line in ((Invoke-Git $Path @('worktree', 'list', '--porcelain')) -split "`n"))
    {
        if ($line -like 'worktree *') { $main = [IO.Path]::GetFullPath($line.Substring(9).Trim()); break }
    }
    [pscustomobject]@{ Top = $top; CommonDir = $common; MainCheckout = $main; StateRoot = (Join-Path $common 'pointframe/packages') }
}

function Get-PackagePaths([string]$Root, [string]$Slug)
{
    [pscustomobject]@{ State = (Join-Path $Root "$Slug.json"); Lock = (Join-Path $Root "$Slug.lock"); Dir = (Join-Path $Root $Slug) }
}

# ---- State: atomic writes, per-package lock, history ------------------------------------------------------------

function Enter-StateLock([string]$Root, [string]$Slug)
{
    New-Item -ItemType Directory -Path $Root -Force | Out-Null
    $path = (Get-PackagePaths $Root $Slug).Lock
    $deadline = [DateTime]::UtcNow.AddSeconds($LockTimeoutSeconds)
    while ($true)
    {
        try { return [IO.File]::Open($path, 'OpenOrCreate', 'ReadWrite', 'None') }
        catch [IO.IOException]
        {
            if ([DateTime]::UtcNow -ge $deadline) { throw "state of package '$Slug' is locked by another process ($path)" }
            Start-Sleep -Milliseconds 100
        }
    }
}

function Read-State([string]$Root, [string]$Slug)
{
    $path = (Get-PackagePaths $Root $Slug).State
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "unknown package '$Slug' (no state at $path)" }
    $state = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -AsHashtable @script:JsonDate
    if ($state.schemaVersion -ne $SchemaVersion) { throw "state schema version $($state.schemaVersion) is not supported (expected $SchemaVersion)" }
    $state
}

function Write-StateAtomic([string]$Root, [string]$Slug, [hashtable]$State, [scriptblock]$BeforeRename = $null)
{
    # A crash leaves the old file or the new one: the content goes to a temp file in the same directory and is
    # renamed over the state file. Stale temp files are removed by the next writer, which holds the lock.
    $path = (Get-PackagePaths $Root $Slug).State
    Get-ChildItem -LiteralPath $Root -Filter "$Slug.json.*.tmp" -File -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
    $temp = "$path.$([guid]::NewGuid().ToString('N')).tmp"
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($State | ConvertTo-Json -Depth 20))
    $stream = [IO.File]::Open($temp, 'CreateNew', 'Write', 'None')
    try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) }
    finally { $stream.Dispose() }
    if ($BeforeRename) { & $BeforeRename }
    [IO.File]::Move($temp, $path, $true)
}

function Update-State([string]$Root, [string]$Slug, [string]$TransitionEvent, [scriptblock]$Mutate, [scriptblock]$BeforeRename = $null, [switch]$Locked)
{
    $stateLock = if ($Locked) { $null } else { Enter-StateLock $Root $Slug }
    try
    {
        $current = Read-State $Root $Slug
        $fromStage = [string]$current.stage
        & $Mutate $current | Out-Null
        $current.history = @($current.history) + @(@{ utc = (Get-Now); event = $TransitionEvent; from = $fromStage; to = [string]$current.stage })
        Write-StateAtomic $Root $Slug $current $BeforeRename
        $current
    }
    finally { if ($stateLock) { $stateLock.Dispose() } }
}

# ---- Main checkout fingerprint ------------------------------------------------------------------------------------

function Get-Fingerprint([string]$Dir)
{
    # HEAD plus a SHA-256 over the raw bytes of every tracked and untracked non-ignored file. Git is only asked for the
    # file list (names, no content, no index copy, no objects written), so autocrlf, assume-unchanged and skip-worktree
    # cannot hide a byte change, and the shared object database is not touched.
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $head = Invoke-Git $Dir @('rev-parse', 'HEAD')
    $listing = (& git -C $Dir ls-files -z --cached --others --exclude-standard 2>&1 | ForEach-Object { "$_" }) -join ''
    if ($LASTEXITCODE -ne 0) { throw "git ls-files failed in ${Dir}: $listing" }
    $names = @($listing -split "`0" | Where-Object { $_ } | Select-Object -Unique)
    [Array]::Sort($names, [StringComparer]::Ordinal)
    $combined = [Text.StringBuilder]::new()
    $unreadable = [Collections.Generic.List[string]]::new()
    foreach ($name in $names)
    {
        $full = [IO.Path]::Combine($Dir, $name)
        $digest = if (Test-Path -LiteralPath $full -PathType Container) { 'directory' }
        elseif (Test-Path -LiteralPath $full -PathType Leaf)
        {
            try
            {
                $stream = [IO.File]::Open($full, 'Open', 'Read', 'ReadWrite')
                try { [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) } finally { $stream.Dispose() }
            }
            catch { $unreadable.Add($name); 'unreadable' }
        }
        else { 'missing' }
        [void]$combined.Append($name).Append("`0").Append($digest).Append("`n")
    }
    # A file that cannot be read makes the whole fingerprint unknown: no hash, so nothing can compare equal to it.
    $tree = if ($unreadable.Count) { $null } else { [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($combined.ToString()))).ToLowerInvariant() }
    @{ head = $head; treeHash = $tree; unknown = ($unreadable.Count -gt 0); unreadable = @($unreadable | Select-Object -First 10); files = $names.Count; seconds = [Math]::Round($timer.Elapsed.TotalSeconds, 2) }
}

function Test-FingerprintsEqual($A, $B)
{
    # Unknown (unreadable files) never equals anything, including itself.
    if (-not $A -or -not $B -or -not $A.treeHash -or -not $B.treeHash) { return $false }
    $A.head -eq $B.head -and $A.treeHash -eq $B.treeHash
}

function Get-TreeHash([string]$Dir)
{
    $fingerprint = Get-Fingerprint $Dir
    if (-not $fingerprint.treeHash) { throw "the worktree cannot be fingerprinted, unreadable: $($fingerprint.unreadable -join ', ')" }
    [string]$fingerprint.treeHash
}

function Get-WorktreeEntries([string]$Top)
{
    $entries = @()
    $current = $null
    foreach ($line in ((Invoke-Git $Top @('worktree', 'list', '--porcelain')) -split "`n"))
    {
        if ($line -like 'worktree *') { $current = @{ path = [IO.Path]::GetFullPath($line.Substring(9).Trim()); branch = '' }; $entries += @($current) }
        elseif ($line -like 'branch *' -and $current) { $current.branch = $line.Substring(7).Trim() }
    }
    $entries
}

function Get-StateProblems($State, $Info)
{
    # Consistency of the saved state with this repository (the state file is shared writable data, not an authority).
    $problems = [Collections.Generic.List[string]]::new()
    if ([IO.Path]::GetFullPath([string]$State.repository.commonDir) -ne $Info.CommonDir) { $problems.Add("the package belongs to repository $($State.repository.commonDir), not $($Info.CommonDir)") }
    if ([IO.Path]::GetFullPath([string]$State.repository.mainCheckout) -ne $Info.MainCheckout) { $problems.Add("the recorded main checkout $($State.repository.mainCheckout) is not this repository's ($($Info.MainCheckout))") }
    if ([string]$State.branch -ne "pkg/$($State.slug)") { $problems.Add("the recorded branch $($State.branch) is not pkg/$($State.slug)") }
    $wt = [string]$State.worktree
    $top = if ($wt -and (Test-Path -LiteralPath $wt -PathType Container)) { (& git -C $wt rev-parse --show-toplevel 2>$null) } else { $null }
    if (-not $top) { $problems.Add("the worktree is missing or not a git checkout: $wt"); return @($problems) }
    $common = (& git -C $wt rev-parse --path-format=absolute --git-common-dir 2>$null)
    if (-not $common -or [IO.Path]::GetFullPath($common) -ne $Info.CommonDir) { $problems.Add("the worktree $wt belongs to a different repository"); return @($problems) }
    $entry = @(Get-WorktreeEntries $Info.Top | Where-Object { $_.path -eq [IO.Path]::GetFullPath($top) })
    if ($entry.Count -eq 0) { $problems.Add("$wt is not a worktree of this repository (git worktree list)") }
    elseif ($entry[0].branch -ne "refs/heads/$($State.branch)") { $problems.Add("the worktree $wt is on '$($entry[0].branch)', not on the recorded branch refs/heads/$($State.branch)") }
    @($problems)
}

function Assert-StateConsistent($State, $Info)
{
    $problems = @(Get-StateProblems $State $Info)
    if ($problems.Count) { throw "state of package '$($State.slug)' is inconsistent: $($problems -join '; ')" }
}

# ---- Manifest and snapshots -------------------------------------------------------------------------------------

function Assert-RelativePath([string]$Value, [string]$Where)
{
    if (-not $Value -or [IO.Path]::IsPathRooted($Value) -or $Value -match '(^|[\\/])\.\.([\\/]|$)') { throw "manifest $Where must be a repository-relative path, got '$Value'" }
}

function Read-Manifest([string]$Path)
{
    $raw = if ($Path) { Get-Content -LiteralPath $Path -Raw } else { '{}' }
    try { $source = $raw | ConvertFrom-Json -AsHashtable @script:JsonDate } catch { throw "check manifest is not valid JSON: $($_.Exception.Message)" }
    if ($source -isnot [hashtable]) { throw 'check manifest must be a JSON object' }
    $unknown = @($source.Keys | Where-Object { $_ -notin @('build', 'tests', 'format', 'extra', 'version', 'allowedPaths', 'allowedScripts') })
    if ($unknown.Count) { throw "check manifest has unknown key(s): $($unknown -join ', ')" }
    $manifest = [ordered]@{ version = 1; build = @(); tests = @(); format = @(); extra = @(); allowedPaths = @(); allowedScripts = @() }
    foreach ($item in @($source['allowedScripts']) | Where-Object { $_ })
    {
        $script = ([string]$item) -replace '\\', '/'
        Assert-RelativePath $script 'allowed script'
        if ($script -notmatch '^scripts/[A-Za-z0-9_./-]+\.ps1$' -or $script -match '(^|/)\.\.?(/|$)') { throw "manifest allowed script must be scripts/<name>.ps1 without '..', got '$item'" }
        $manifest.allowedScripts += $script
    }
    foreach ($item in @($source['allowedPaths']) | Where-Object { $_ }) { Assert-RelativePath ([string]$item) 'allowed path'; $manifest.allowedPaths += ([string]$item -replace '\\', '/') }
    foreach ($item in @($source['build']) | Where-Object { $_ }) { Assert-RelativePath ([string]$item) 'build target'; $manifest.build += [string]$item }
    foreach ($item in @($source['format']) | Where-Object { $_ }) { Assert-RelativePath ([string]$item) 'format target'; $manifest.format += [string]$item }
    foreach ($item in @($source['tests']) | Where-Object { $_ })
    {
        $project = if ($item -is [hashtable]) { [string]$item['project'] } else { [string]$item }
        $filter = if ($item -is [hashtable] -and $item['filter']) { [string]$item['filter'] } else { '' }
        Assert-RelativePath $project 'test project'
        $manifest.tests += @{ project = $project; filter = $filter }
    }
    foreach ($item in @($source['extra']) | Where-Object { $_ })
    {
        if ($item -isnot [hashtable] -or -not $item['name'] -or -not $item['exe']) { throw 'manifest extra entries need name and exe' }
        $manifest.extra += @{ name = [string]$item['name']; exe = [string]$item['exe']; mutates = [bool]$item['mutates']; args = @(@($item['args']) | Where-Object { $null -ne $_ } | ForEach-Object { [string]$_ }) }
    }
    $manifest
}

function Get-ManifestPaths($Manifest)
{
    @($Manifest.build) + @($Manifest.format) + @($Manifest.tests | ForEach-Object { $_.project }) | Select-Object -Unique
}

function Test-SnapshotsIntact($State)
{
    $problems = [Collections.Generic.List[string]]::new()
    foreach ($key in @('brief', 'manifest'))
    {
        $snap = $State[$key]
        if (-not (Test-Path -LiteralPath $snap.path -PathType Leaf)) { $problems.Add("$key snapshot is missing: $($snap.path)") }
        elseif ((Get-Sha256 $snap.path) -ne $snap.sha256) { $problems.Add("$key snapshot changed since 'new' (hash mismatch): $($snap.path)") }
    }
    @($problems)
}

# ---- Process helpers --------------------------------------------------------------------------------------------

function Invoke-Logged([string]$Exe, [string[]]$ArgList, [string]$Dir, [string]$Log)
{
    $previous = $env:MSBUILDDISABLENODEREUSE
    $env:MSBUILDDISABLENODEREUSE = '1'
    Push-Location -LiteralPath $Dir
    try
    {
        New-Item -ItemType Directory -Path (Split-Path $Log -Parent) -Force | Out-Null
        [IO.File]::WriteAllText($Log, '')
        & $Exe @ArgList *>&1 | ForEach-Object { "$_" } | Set-Content -LiteralPath $Log -Encoding utf8
        return $LASTEXITCODE
    }
    catch
    {
        "failed to start: $($_.Exception.Message)" | Set-Content -LiteralPath $Log -Encoding utf8
        return 127
    }
    finally
    {
        Pop-Location
        if ($null -eq $previous) { Remove-Item Env:MSBUILDDISABLENODEREUSE -ErrorAction SilentlyContinue } else { $env:MSBUILDDISABLENODEREUSE = $previous }
    }
}

function Get-CheckCommands($Manifest)
{
    # Mutating steps first (extra commands marked "mutates", dotnet format, CRLF normalization), then the validators
    # (build, tests, the other extra commands), so every validator runs on the tree the check ends with.
    $list = [Collections.Generic.List[hashtable]]::new()
    foreach ($extra in $Manifest.extra | Where-Object { $_.mutates }) { $list.Add(@{ name = $extra.name; kind = 'extra'; mutator = $true; exe = $extra.exe; args = @($extra.args) }) }
    foreach ($target in $Manifest.format) { $list.Add(@{ name = "format $target"; kind = 'format'; mutator = $true; exe = 'dotnet'; args = @('format', $target, '--no-restore') }) }
    $list.Add(@{ name = 'crlf normalization'; kind = 'crlf'; mutator = $true; exe = ''; args = @() })
    foreach ($target in $Manifest.build) { $list.Add(@{ name = "build $target"; kind = 'build'; mutator = $false; exe = 'dotnet'; args = @('build', $target, '--nologo', '-v', 'q') }) }
    foreach ($test in $Manifest.tests)
    {
        $filter = if ($test.filter) { "(Category!=Integration)&($($test.filter))" } else { 'Category!=Integration' }
        $list.Add(@{ name = "test $($test.project)"; kind = 'test'; mutator = $false; exe = 'dotnet'; args = @('test', $test.project, '--nologo', '--filter', $filter) })
    }
    foreach ($extra in $Manifest.extra | Where-Object { -not $_.mutates }) { $list.Add(@{ name = $extra.name; kind = 'extra'; mutator = $false; exe = $extra.exe; args = @($extra.args) }) }
    @($list)
}

function Get-CheckEvidenceProblems($Check, $Plan)
{
    # A check counts as evidence only if it covers every command of the manifest, in order, with an exit code and an
    # existing log, and each command ran on the tree the previous one left (validators on the check's final tree).
    $problems = [Collections.Generic.List[string]]::new()
    $recorded = @($Check.commands)
    $covered = $recorded.Count -eq $Plan.Count
    for ($i = 0; $covered -and $i -lt $Plan.Count; $i++) { if ($recorded[$i].name -ne $Plan[$i].name) { $covered = $false } }
    if (-not $covered)
    {
        $problems.Add("the check does not cover the manifest's commands ($($recorded.Count) recorded, $($Plan.Count) expected, or the names differ)")
        return @($problems)
    }
    $expectedInput = [string]$Check.treeBefore
    for ($i = 0; $i -lt $Plan.Count; $i++)
    {
        $c = $recorded[$i]
        if ($c.skipped) { if ($Check.ok) { $problems.Add("$($c.name) was skipped in a passing check") } }
        else
        {
            if ($null -eq $c.exitCode) { $problems.Add("$($c.name) has no exit code") }
            if ($Plan[$i].kind -ne 'crlf' -and (-not $c.log -or -not (Test-Path -LiteralPath $c.log -PathType Leaf))) { $problems.Add("$($c.name) has no log") }
        }
        if ($c.inputTree -ne $expectedInput) { $problems.Add("$($c.name) did not run on the tree the previous command left") }
        if (-not $Plan[$i].mutator -and $c.inputTree -ne $Check.treeHash) { $problems.Add("$($c.name) validated a tree other than the one the check ended with") }
        $expectedInput = [string]$c.outputTree
    }
    if ($expectedInput -ne [string]$Check.treeHash) { $problems.Add('the last command did not leave the recorded final tree') }
    # ok must be exactly "every command ran, exited 0, and no validator changed the tree"; anything else contradicts itself.
    $derivedOk = $true
    foreach ($c in $recorded) { if ($c.skipped -or $null -eq $c.exitCode -or $c.exitCode -ne 0 -or ($c.ContainsKey('modifiedWorktree') -and $c.modifiedWorktree)) { $derivedOk = $false } }
    if ([bool]$Check.ok -ne $derivedOk) { $problems.Add("the check says ok=$([bool]$Check.ok) but its recorded exit codes say $derivedOk") }
    @($problems)
}

function Invoke-CrlfNormalize([string]$Wt, [string]$BaseSha)
{
    $changed = @((Invoke-Git $Wt @('-c', 'core.quotepath=false', 'diff', '--name-only', '--diff-filter=d', $BaseSha)) -split "`n") +
    @((Invoke-Git $Wt @('-c', 'core.quotepath=false', 'ls-files', '--others', '--exclude-standard')) -split "`n")
    $fixed = [Collections.Generic.List[string]]::new()
    foreach ($relative in ($changed | Where-Object { $_ } | Select-Object -Unique))
    {
        if ($relative -match '\.(sh|bash)$') { continue }
        $path = Join-Path $Wt $relative
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }
        $bytes = [IO.File]::ReadAllBytes($path)
        if ($bytes.Length -gt 5MB -or [Array]::IndexOf($bytes, [byte]0) -ge 0) { continue }
        $needs = $false
        for ($i = 0; $i -lt $bytes.Length; $i++)
        {
            if ($bytes[$i] -eq 10 -and ($i -eq 0 -or $bytes[$i - 1] -ne 13)) { $needs = $true; break }
        }
        if (-not $needs) { continue }
        $buffer = [IO.MemoryStream]::new()
        for ($i = 0; $i -lt $bytes.Length; $i++)
        {
            if ($bytes[$i] -eq 10 -and ($i -eq 0 -or $bytes[$i - 1] -ne 13)) { $buffer.WriteByte(13) }
            $buffer.WriteByte($bytes[$i])
        }
        [IO.File]::WriteAllBytes($path, $buffer.ToArray())
        $fixed.Add($relative)
    }
    @($fixed)
}

function Get-SessionIdFromEvents([string]$Path)
{
    if (-not $Path -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    foreach ($line in (Get-Content -LiteralPath $Path))
    {
        if (-not $line) { continue }
        try { $item = $line | ConvertFrom-Json } catch { continue }
        if ($null -eq $item -or $item -isnot [pscustomobject]) { continue }
        foreach ($name in @('thread_id', 'session_id', 'sessionId'))
        {
            if ($item.PSObject.Properties[$name] -and $item.$name) { return [string]$item.$name }
        }
    }
    $null
}

function Get-RunClassification([object]$ExitCode, [string]$Termination, [string]$ReportText, [bool]$ArtifactsPresent, [string]$MissingArtifacts, [string]$ContainmentProblem = '', [string]$ExtraFailure = '')
{
    # Exit status, termination and environment evidence stay separate facts; the outcome is derived from them.
    # env-blocked only applies when the executor reported the blocker with an explicit "ENV-BLOCKED:" line and nothing
    # else failed (exit 0, both artifacts present, containment verified); when unsure the outcome is impl-failed and the
    # evidence is kept. A timeout, a failed kill or an exhausted budget stays impl-failed with that termination; a run is
    # completed only with exit 0, both artifacts present and the process tree verified gone. completed is a statement
    # about the run, not "accepted".
    $line = $null
    foreach ($candidate in ($ReportText -split "`r?`n")) { if ($candidate -match '^\s*\W{0,4}ENV-BLOCKED\W{0,4}\s*:') { $line = $candidate.Trim(); break } }
    $outcome = 'completed'
    $reason = $null
    if ($Termination -ne 'exit') { $outcome = 'impl-failed'; $reason = "termination: $Termination" }
    elseif ($ContainmentProblem) { $outcome = 'impl-failed'; $reason = "containment unverified: $ContainmentProblem" }
    elseif ($line -and $ExitCode -ne 0) { $outcome = 'impl-failed'; $reason = "mixed: implementation failure with environment blocker (exit $ExitCode)" }
    elseif ($line -and -not $ArtifactsPresent) { $outcome = 'impl-failed'; $reason = "mixed: environment blocker reported but artifacts are missing: $MissingArtifacts" }
    elseif ($line) { $outcome = 'env-blocked'; $reason = 'the executor reported ENV-BLOCKED and nothing else failed' }
    elseif ($ExitCode -ne 0) { $outcome = 'impl-failed'; $reason = "exit code $ExitCode" }
    elseif ($ExtraFailure) { $outcome = 'impl-failed'; $reason = $ExtraFailure }
    elseif (-not $ArtifactsPresent) { $outcome = 'impl-failed'; $reason = "missing artifacts: $MissingArtifacts" }
    @{ outcome = $outcome; reason = $reason; envBlocked = [bool]$line; envEvidence = $line }
}

function Get-EventTokens([string]$Path)
{
    # Sums of the turn.completed usage; a field no event reported is null, never 0.
    $sums = @{ input = $null; cached = $null; output = $null }
    $map = @{ input = 'input_tokens'; cached = 'cached_input_tokens'; output = 'output_tokens' }
    if ($Path -and (Test-Path -LiteralPath $Path -PathType Leaf))
    {
        foreach ($line in (Get-Content -LiteralPath $Path))
        {
            if (-not $line) { continue }
            try { $item = $line | ConvertFrom-Json } catch { continue }
            if ($null -eq $item -or $item -isnot [pscustomobject] -or -not $item.PSObject.Properties['type'] -or $item.type -ne 'turn.completed') { continue }
            if (-not $item.PSObject.Properties['usage'] -or -not $item.usage) { continue }
            foreach ($key in $map.Keys)
            {
                $source = $map[$key]
                if ($item.usage.PSObject.Properties[$source] -and $null -ne $item.usage.$source) { $sums[$key] = [long]$sums[$key] + [long]$item.usage.$source }
            }
        }
    }
    $sums
}

function Get-CodexCliVersion
{
    if ($CodexVersion) { return $CodexVersion }
    try
    {
        $resolved = Resolve-CodexCommand
        if (-not $resolved.Ok) { return $null }
        $version = & $resolved.File @(@($resolved.Prefix) | ForEach-Object { $_.Trim('"') }) --version 2>&1 | Select-Object -First 1
        if ($LASTEXITCODE -eq 0 -and $version) { return ([string]$version).Trim() }
    }
    catch { }
    $null
}

# ---- Claude executor ----------------------------------------------------------------------------------------------

$script:ClaudeModels = @{ 'claude-sonnet' = 'claude-sonnet-5-5'; 'claude-haiku' = 'claude-haiku-5-5' }
$script:ClaudeFamilies = @{ 'claude-sonnet' = 'sonnet'; 'claude-haiku' = 'haiku' }
$script:ModelBound = $PSBoundParameters.ContainsKey('Model')
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

function Get-ClaudeCliVersion($Launcher)
{
    # The probe is a child like any other: inside a job, stdin closed, bounded by -ProbeSeconds, killed with its tree.
    Initialize-ProcessJob
    $job = [PointframeProcessJob]::Create()
    if ($job -eq [IntPtr]::Zero) { return $null }
    $base = Join-Path ([IO.Path]::GetTempPath()) "claude-version-$([guid]::NewGuid().ToString('N'))"
    $process = $null
    try
    {
        $process = Start-ProcessInJob $job $Launcher.File (@($Launcher.Prefix) + @('--version')) ([IO.Path]::GetTempPath()) $null "$base.out" "$base.err"
        if (-not (Wait-ProcessExit $process ([long]($ProbeSeconds * 1000)))) { return $null }
        if ((Get-ProcessExitCode $process) -ne 0) { return $null }
        $line = Get-Content -LiteralPath "$base.out" -TotalCount 1 -ErrorAction SilentlyContinue
        if ($line) { return ([string]$line).Trim() }
    }
    catch { }
    finally
    {
        Stop-ProcessJob $job 5 | Out-Null
        if ($process) { [PointframeProcessJob]::CloseProcess($process.Handle) }
        [PointframeProcessJob]::Close($job)
        Remove-Item -LiteralPath "$base.out", "$base.err" -Force -ErrorAction SilentlyContinue
    }
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
        'Use the Bash tool for every shell command, including `pwsh scripts/...` and `dotnet ...`.',
        'Use absolute paths inside the worktree and set an explicit working directory for every process.',
        'Never run a command that reads stdin in the background (for example python -).',
        'Never commit, push or open a pull request, and never write outside the worktree.',
        'If a tool call is denied, do not look for a way around it: report it.',
        'If a check cannot run in this environment, report it on its own line as ENV-BLOCKED: <command>: <error> and stop retrying it.',
        $(if ($Role -eq 'helper') { 'You are a read-only helper: you have no Edit or Write tool and must not change any file.' } else { '' }),
        'Accepted limitation: the command allowlist guards against accidental actions (commit, push, gh, network); the worktree is writable and dotnet build and dotnet test run code an executor authors, so it is not a sandbox. The main-checkout guard, job containment and review are the real controls.',
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
        $shell += @('dotnet build:*', 'dotnet test:*', 'dotnet format:*', 'pwsh scripts/verify.ps1:*', 'pwsh -NoProfile -File scripts/verify.ps1:*')
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

    @{
        Launcher = $launcher; Model = $model; Arguments = $arguments; RecordedArguments = $recorded; Stdin = $promptPath
        Settings = @{ path = $settingsPath; sha256 = (Get-Sha256 $settingsPath) }; Prompt = @{ path = $promptPath; sha256 = (Get-Sha256 $promptPath) }
        AuthMode = (Get-ClaudeAuthMode); Version = (Get-ClaudeCliVersion $launcher); ChainId = $chainId
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
        'blocked-main-mutated' { @() }
        default { @('run', 'check') }
    }
}

# ---- new --------------------------------------------------------------------------------------------------------

function Invoke-New
{
    if ($Id -notmatch '^[a-z0-9][a-z0-9-]{0,48}$') { throw '-Id must be a slug: lowercase letters, digits and dashes' }
    if (-not $Brief -or -not (Test-Path -LiteralPath $Brief -PathType Leaf)) { throw "-Brief file not found: $Brief" }
    if ($Checks -and -not (Test-Path -LiteralPath $Checks -PathType Leaf)) { throw "-Checks file not found: $Checks" }
    $briefSource = (Resolve-Path -LiteralPath $Brief).Path
    $manifest = Read-Manifest $Checks
    $info = Get-RepoInfo $Repo
    $paths = Get-PackagePaths $info.StateRoot $Id
    if (Test-Path -LiteralPath $paths.State) { throw "package '$Id' already exists ($($paths.State))" }
    $wt = if ($Worktree) { [IO.Path]::GetFullPath($Worktree) } else { Join-Path (Split-Path $info.MainCheckout -Parent) "$(Split-Path $info.MainCheckout -Leaf).worktrees/$Id" }
    $wt = [IO.Path]::GetFullPath($wt)
    $branch = "pkg/$Id"
    if (Test-Path -LiteralPath $wt) { throw "worktree path already exists: $wt" }
    & git -C $info.Top show-ref --verify --quiet "refs/heads/$branch"
    if ($LASTEXITCODE -eq 0) { throw "branch $branch already exists" }

    $validation = @(& pwsh -NoProfile -File (Join-Path $PSScriptRoot 'agent-run.ps1') -Brief $briefSource -Worktree $wt -ValidateOnly 2>&1 | ForEach-Object { "$_" })
    if ($LASTEXITCODE -ne 0) { throw "brief validation failed: $($validation -join ' ')" }

    $remote = if ($BaseRef.Contains('/')) { $BaseRef.Split('/')[0] } else { $null }
    Write-Host "package $Id"
    Write-Host "  repository   $($info.MainCheckout)"
    Write-Host "  state        $($paths.State)"
    Write-Host "  brief        $briefSource (snapshot with SHA-256, read-only)"
    Write-Host "  manifest     $(if ($Checks) { $Checks } else { '(none: empty manifest)' }): build=$($manifest.build.Count) tests=$($manifest.tests.Count) format=$($manifest.format.Count) extra=$($manifest.extra.Count)"
    Write-Host "  worktree     $wt"
    Write-Host "  branch       $branch from $BaseRef$(if ($remote) { " after 'git fetch $remote'" })"
    Write-Host "  preflight    dotnet build of: $((@($manifest.build) + @($manifest.tests | ForEach-Object { $_.project }) | Select-Object -Unique) -join ', ')"
    if ($DryRun) { Write-Host 'dry run: nothing was created.'; return }

    # The whole creation is one transaction under the package lock; existence is rechecked while holding it.
    $stateLock = Enter-StateLock $info.StateRoot $Id
    try { Invoke-NewLocked $info $paths $wt $branch $briefSource $manifest $remote }
    finally { $stateLock.Dispose() }
}

function Invoke-NewLocked($info, $paths, [string]$wt, [string]$branch, [string]$briefSource, $manifest, $remote)
{
    if (Test-Path -LiteralPath $paths.State) { throw "package '$Id' already exists ($($paths.State))" }
    if (Test-Path -LiteralPath $wt) { throw "worktree path already exists: $wt" }
    & git -C $info.Top show-ref --verify --quiet "refs/heads/$branch"
    if ($LASTEXITCODE -eq 0) { throw "branch $branch already exists" }
    if (Test-Path -LiteralPath $paths.Dir)
    {
        # Leftovers of a creation that died before it wrote any state.
        Get-ChildItem -LiteralPath $paths.Dir -Recurse -File -Force | ForEach-Object { $_.IsReadOnly = $false }
        Remove-Item -LiteralPath $paths.Dir -Recurse -Force
    }

    if ($remote) { Invoke-Git $info.Top @('fetch', $remote) | Out-Null }
    $baseSha = Invoke-Git $info.Top @('rev-parse', '--verify', "$BaseRef^{commit}")
    foreach ($relative in (Get-ManifestPaths $manifest))
    {
        & git -C $info.Top cat-file -e "${baseSha}:$($relative -replace '\\', '/')" 2>$null
        if ($LASTEXITCODE -ne 0) { throw "manifest path is not in $BaseRef ($baseSha): $relative" }
    }

    New-Item -ItemType Directory -Path $paths.Dir -Force | Out-Null
    $briefSnapshot = Join-Path $paths.Dir 'brief.md'
    $manifestSnapshot = Join-Path $paths.Dir 'checks.json'
    Copy-Item -LiteralPath $briefSource -Destination $briefSnapshot
    [IO.File]::WriteAllText($manifestSnapshot, ($manifest | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
    foreach ($snapshot in @($briefSnapshot, $manifestSnapshot)) { Set-ItemProperty -LiteralPath $snapshot -Name IsReadOnly -Value $true }

    $state = @{
        schemaVersion = $SchemaVersion
        packageId = [guid]::NewGuid().ToString()
        slug = $Id
        createdUtc = (Get-Now)
        repository = @{ top = $info.Top; commonDir = $info.CommonDir; mainCheckout = $info.MainCheckout }
        worktree = $wt
        branch = $branch
        baseRef = $BaseRef
        baseSha = $baseSha
        stage = 'creating'
        brief = @{ source = $briefSource; path = $briefSnapshot; sha256 = (Get-Sha256 $briefSnapshot) }
        manifest = @{ source = $Checks; path = $manifestSnapshot; sha256 = (Get-Sha256 $manifestSnapshot) }
        preflight = $null
        currentRun = $null
        runs = @()
        checks = @()
        guard = @{ mainCheckoutMutated = $false; lastRunId = $null; before = $null; after = $null }
        history = @(@{ utc = (Get-Now); event = 'new'; from = ''; to = 'creating' })
    }
    Write-StateAtomic $info.StateRoot $Id $state

    try
    {
        Invoke-Git $info.Top @('worktree', 'add', '-b', $branch, $wt, $baseSha) | Out-Null
        $wt = (Get-Item -LiteralPath $wt).FullName
    }
    catch
    {
        Update-State $info.StateRoot $Id 'creation-failed' { param($s) $s.stage = 'creation-failed' } -Locked | Out-Null
        throw
    }
    $targets = @(@($manifest.build) + @($manifest.tests | ForEach-Object { $_.project }) | Select-Object -Unique)
    $results = @()
    $ok = $true
    foreach ($target in $targets)
    {
        $log = Join-Path $paths.Dir "preflight/build-$($results.Count + 1).log"
        $code = Invoke-Logged 'dotnet' @('build', $target, '--nologo', '-v', 'q') $wt $log
        $results += @(@{ target = $target; exitCode = $code; log = $log })
        if ($code -ne 0) { $ok = $false; break }
    }
    $stage = if ($ok) { 'ready' } else { 'preflight-failed' }
    Update-State $info.StateRoot $Id "preflight-$(if ($ok) { 'passed' } else { 'failed' })" {
        param($s)
        $s.worktree = $wt
        $s.preflight = @{ utc = (Get-Now); ok = $ok; builds = $results }
        $s.stage = $stage
    } -Locked | Out-Null
    if (-not $ok)
    {
        $script:ExitCode = 1
        Write-Host "ERROR preflight build failed for $($results[-1].target); see $($results[-1].log)"
        return
    }
    & pwsh -NoProfile -File $StatusScript -Set -Id $Id -Objective "package $Id" -Stage 'package ready' | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Host "WARNING status.ps1 -Set failed (exit $LASTEXITCODE); the package state is unaffected." }
    Write-Host "package $Id is ready: worktree $wt, branch $branch, base $baseSha"
}

# ---- run --------------------------------------------------------------------------------------------------------

function Test-SelfTestStub([string]$Path)
{
    # Fault injection is only ever accepted together with an executor script the self-test wrote and marked.
    if (-not $Path -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $false }
    ((Get-Content -LiteralPath $Path -TotalCount 1) -eq '# pointframe-selftest-stub')
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

function Invoke-Run
{
    if ($Effort -eq 'high' -and [string]::IsNullOrWhiteSpace($EscalationReason)) { throw '-Effort high requires -EscalationReason' }
    $simulateKillFailure = [bool]$SimulateKillFailure
    if ($simulateKillFailure -and -not (Test-SelfTestStub $AgentRunScript)) { throw '-SimulateKillFailure is only accepted together with a marked self-test stub executor' }
    $info = Get-RepoInfo $Repo
    $state = Read-State $info.StateRoot $Id
    Assert-StateConsistent $state $info
    $paths = Get-PackagePaths $info.StateRoot $Id
    $problems = @(Test-SnapshotsIntact $state)
    if ($problems.Count) { throw ($problems -join '; ') }
    if ($state.stage -eq 'blocked-main-mutated') { throw 'the main checkout was mutated by an earlier run; resolve it by hand, this package stays stopped' }
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
    $claude = if ($isClaude) { Initialize-ClaudeRun $state $runId $runDir } else { $null }
    $remainingSeconds = if ($isClaude) { [double]$claude.Budget.remainingSeconds } else { $MaxMinutes * 60 }
    $remainingTokens = if ($isClaude) { [long]$claude.Budget.remainingInputTokens } else { $MaxInputTokens }

    $before = Get-Fingerprint $mainCheckout
    if (-not $before.treeHash) { throw "the main checkout cannot be fingerprinted (unreadable: $($before.unreadable -join ', ')); the run was not started" }
    $scopeBefore = if ($isClaude -and $claude.Scope) { Get-WorktreeSnapshot $wt } else { $null }
    $started = Get-Now
    $permissions = if ($isClaude) { @{ mode = 'default'; bypass = $false; settingsFile = $claude.Settings; settingSources = 'none (only the runner settings file)'; mcp = 'strict, empty'; tools = $claude.Tools; shell = 'git reads, kb.ps1 init|read|check|changed|selftest, verify.ps1, brief-named scripts, dotnet build|test|format (executors)' } }
    else { @{ sandbox = 'workspace-write'; approval = 'never'; windowsSandbox = 'unelevated' } }
    $cliVersion = if ($isClaude) { $null } else { Get-CodexCliVersion }
    $self = @{ pid = $PID; startUtc = (Get-ProcessStartIdentity $PID) }
    $runRecord = @{
        runId = $runId; seq = 0; startedUtc = $started; endedUtc = $null; provider = $(if ($isClaude) { 'claude' } else { 'codex' }); executorKind = $Executor; model = $(if ($isClaude) { $claude.Model } else { $Model })
        effort = $(if ($isClaude) { $null } else { $Effort }); maxMinutes = $MaxMinutes; maxInputTokens = $MaxInputTokens; permissions = $permissions; codexCliVersion = $cliVersion
        wrapper = $self; executor = $null; sessionId = $null; exitCode = $null; termination = 'running'; outcome = 'running'; reason = $null
        envBlocked = $false; envEvidence = $null; tokens = $null; containmentVerified = $null; containmentReason = $null; logDirectory = $runDir
        artifacts = @{ outMd = $null; eventsJsonl = $null }; mainCheckoutBefore = $before; mainCheckoutAfter = $null; mainCheckoutMutated = $false; chainId = $(if ($isClaude) { $claude.ChainId } else { $runId })
    }
    if ($isClaude)
    {
        $runRecord.claudeCliVersion = $claude.Version
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
        if ($s.stage -notin @('ready', 'executor-completed', 'executor-env-blocked', 'executor-impl-failed', 'checks-passed', 'checks-failed')) { throw "run is not allowed in stage '$($s.stage)'" }
        $s.currentRun = @{ runId = $runId; pid = $PID }
        $runRecord.seq = (@($s.runs).Count + 1)
        $s.runs = @($s.runs) + @($runRecord)
        $s.stage = 'running'
    } | Out-Null

    $agentArgs = @('-NoProfile', '-File', "`"$AgentRunScript`"", '-Brief', "`"$($state.brief.path)`"", '-Worktree', "`"$wt`"", '-Model', $Model, '-Effort', $Effort,
        '-MaxMinutes', ([string][int][Math]::Ceiling($MaxMinutes)), '-MaxInputTokens', ([string]$MaxInputTokens), '-Sandbox', $(if ($isClaude) { '' } else { $permissions.sandbox }),
        '-Task', $Id, '-LogDirectory', "`"$runDir`"")
    if ($EscalationReason) { $agentArgs += @('-EscalationReason', "`"$($EscalationReason -replace '"', '\"')`"") }
    New-Item -ItemType Directory -Path $runDir -Force | Out-Null
    Initialize-ProcessJob
    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    $job = [PointframeProcessJob]::Create()
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
        if ($job -ne [IntPtr]::Zero) { [PointframeProcessJob]::Close($job) }
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
    [PointframeProcessJob]::Close($job)
    $leftover = $containment.Leftover
    $exitCode = if ($termination -in @('timeout', 'kill-failed', 'budget') -or -not (Test-ProcessExited $process)) { $null } else { Get-ProcessExitCode $process }
    [PointframeProcessJob]::CloseProcess($process.Handle)
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

# ---- check ------------------------------------------------------------------------------------------------------

function Invoke-Check
{
    $info = Get-RepoInfo $Repo
    $stateLock = Enter-StateLock $info.StateRoot $Id
    try
    {
        $state = Read-State $info.StateRoot $Id
        Assert-StateConsistent $state $info
        if ([string]$state.stage -in @('creating', 'creation-failed', 'preflight-failed', 'running', 'executor-kill-failed', 'blocked-main-mutated')) { throw "check is not allowed in stage '$($state.stage)'" }
        $problems = @(Test-SnapshotsIntact $state)
        if ($problems.Count) { throw ($problems -join '; ') }
        $wt = [string]$state.worktree
        $manifest = Read-Manifest $state.manifest.path
        $paths = Get-PackagePaths $info.StateRoot $Id
        $checkId = [guid]::NewGuid().ToString('N')
        $dir = Join-Path $paths.Dir "checks/$checkId"
        $treeBefore = Get-TreeHash $wt
        $tree = $treeBefore
        $records = @()
        $buildFailed = $false
        $modifiedByValidator = $false
        $number = 0
        foreach ($command in (Get-CheckCommands $manifest))
        {
            $number++
            $log = Join-Path $dir "$number-$($command.kind).log"
            $commandLine = if ($command.kind -eq 'crlf') { 'normalize changed text files to CRLF' } else { "$($command.exe) $($command.args -join ' ')" }
            $record = @{ name = $command.name; kind = $command.kind; mutator = [bool]$command.mutator; command = $commandLine; exitCode = $null; skipped = $null; log = $null; inputTree = $tree; outputTree = $tree }
            if ($command.kind -eq 'test' -and $buildFailed) { $record.skipped = 'a build failed'; $records += @($record); continue }
            if ($command.kind -eq 'crlf')
            {
                $record.files = @(Invoke-CrlfNormalize $wt ([string]$state.baseSha))
                $record.exitCode = 0
            }
            else
            {
                $record.exitCode = Invoke-Logged $command.exe @($command.args) $wt $log
                $record.log = $log
                if ($command.kind -eq 'build' -and $record.exitCode -ne 0) { $buildFailed = $true }
            }
            $tree = Get-TreeHash $wt
            $record.outputTree = $tree
            if (-not $command.mutator -and $tree -ne $record.inputTree) { $record.modifiedWorktree = $true; $modifiedByValidator = $true }
            $records += @($record)
        }
        $ok = @($records | Where-Object { $null -ne $_.exitCode -and $_.exitCode -ne 0 }).Count -eq 0 -and -not $buildFailed -and -not $modifiedByValidator
        $record = @{ checkId = $checkId; utc = (Get-Now); ok = $ok; treeBefore = $treeBefore; treeHash = $tree; commands = $records }
        $stage = if ($ok) { 'checks-passed' } else { 'checks-failed' }
        Update-State $info.StateRoot $Id 'checked' {
            param($s)
            $s.checks = @($s.checks) + @($record)
            $s.stage = $stage
        } -Locked | Out-Null
        foreach ($item in $records) { Write-Host ("  {0,-6} {1}{2}" -f $(if ($item.skipped) { 'SKIP' } elseif ($item.exitCode -eq 0) { 'ok' } else { "exit $($item.exitCode)" }), $item.name, $(if ($item.modifiedWorktree) { ' (changed the worktree, so the result cannot be trusted)' } else { '' })) }
        Write-Host "check ${checkId}: $(if ($ok) { 'passed' } else { 'FAILED' }) on tree $tree"
        if (-not $ok) { $script:ExitCode = 1 }
    }
    finally { $stateLock.Dispose() }
}

# ---- show -------------------------------------------------------------------------------------------------------

function Invoke-Show
{
    $info = Get-RepoInfo $Repo
    $state = Read-State $info.StateRoot $Id
    $integrity = @(Get-StateProblems $state $info)
    $wt = [string]$state.worktree
    $currentTree = $null
    if ($integrity.Count -eq 0)
    {
        # An unreadable file makes the tree unknown, and an unknown tree cannot make any check fresh.
        try { $currentTree = Get-TreeHash $wt } catch { $integrity += @($_.Exception.Message) }
    }
    $snapshotProblems = @(Test-SnapshotsIntact $state)
    $plan = @()
    if ($snapshotProblems.Count -eq 0) { $plan = @(Get-CheckCommands (Read-Manifest $state.manifest.path)) }
    $checks = @($state.checks | ForEach-Object {
            $check = $_
            $evidence = @(if ($snapshotProblems.Count) { 'the manifest snapshot cannot be verified' } else { Get-CheckEvidenceProblems $check $plan })
            $stale = ($null -eq $currentTree -or $currentTree -ne $check.treeHash)
            [ordered]@{ checkId = $check.checkId; utc = $check.utc; ok = $check.ok; treeHash = $check.treeHash; stale = $stale; evidenceProblems = $evidence; fresh = (-not $stale -and $evidence.Count -eq 0)
                commands = @($check.commands | ForEach-Object { [ordered]@{ name = $_.name; exitCode = $_.exitCode; log = $_.log; inputTree = $_.inputTree; outputTree = $_.outputTree } }) }
        })
    $runs = @($state.runs | ForEach-Object {
            [ordered]@{ runId = $_.runId; startedUtc = $_.startedUtc; provider = $_.provider; executorKind = (Get-Field $_ 'executorKind'); model = $_.model; effort = $_.effort; codexCliVersion = $_.codexCliVersion; claudeCliVersion = (Get-Field $_ 'claudeCliVersion')
                authMode = (Get-Field $_ 'authMode'); permissionDenials = @(Get-Field $_ 'permissionDenials').Count; chainId = (Get-Field $_ 'chainId'); permissions = $_.permissions
                maxInputTokens = $_.maxInputTokens; tokens = $_.tokens; sessionId = $_.sessionId; exitCode = $_.exitCode; termination = $_.termination; outcome = $_.outcome; reason = $_.reason
                envBlocked = $_.envBlocked; mainCheckoutMutated = $_.mainCheckoutMutated }
        })
    $view = [ordered]@{
        slug = $state.slug; packageId = $state.packageId; stage = $state.stage; worktree = $wt; branch = $state.branch; baseSha = $state.baseSha
        integrityProblems = $integrity; snapshotProblems = $snapshotProblems; runs = $runs; checks = $checks
        mainCheckoutGuard = [ordered]@{ mutated = [bool]$state.guard.mainCheckoutMutated; lastRunId = $state.guard.lastRunId }
        currentTreeHash = $currentTree; nextTransitions = @(Get-NextTransitions $state ($snapshotProblems.Count -eq 0 -and $integrity.Count -eq 0))
    }
    if ($Json) { $view | ConvertTo-Json -Depth 8; return }
    Write-Host "package $($state.slug)  stage=$($state.stage)"
    Write-Host "  worktree $wt  branch $($state.branch)  base $($state.baseSha)"
    if ($integrity.Count) { Write-Host "  INCONSISTENT STATE: $($integrity -join '; ')" }
    Write-Host "  snapshots $(if ($snapshotProblems.Count) { 'CHANGED: ' + ($snapshotProblems -join '; ') } else { 'intact' })"
    Write-Host "  runs: $($runs.Count)"
    foreach ($run in $runs) { Write-Host "    $($run.startedUtc) $($run.provider) $($run.model) $($run.effort) cli=$(if ($run.claudeCliVersion) { $run.claudeCliVersion } else { $run.codexCliVersion }) denials=$($run.permissionDenials) termination=$($run.termination) exit=$($run.exitCode) outcome=$($run.outcome) envBlocked=$($run.envBlocked) session=$($run.sessionId)" }
    Write-Host "  checks: $($checks.Count)"
    foreach ($check in $checks)
    {
        $freshness = if ($check.fresh) { 'fresh' } elseif ($check.stale) { 'STALE (the worktree changed after this check)' } else { "NOT EVIDENCE ($($check.evidenceProblems -join '; '))" }
        Write-Host "    $($check.utc) $(if ($check.ok) { 'passed' } else { 'FAILED' }) $freshness"
    }
    Write-Host "  main checkout guard: $(if ($view.mainCheckoutGuard.mutated) { 'MUTATED' } else { 'clean' })"
    Write-Host "  next: $(if ($view.nextTransitions.Count) { $view.nextTransitions -join ', ' } else { '(none)' })"
}

# ---- self-test --------------------------------------------------------------------------------------------------

function Invoke-SelfTest
{
    $failures = [Collections.Generic.List[string]]::new()
    $expect = { param([bool]$Condition, [string]$Message) if (-not $Condition) { $failures.Add($Message) } }
    $root = Join-Path ([IO.Path]::GetTempPath()) "package-selftest-$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $root | Out-Null
    $oldMain = $env:POINTFRAME_PKG_TEST_MAIN
    $oldApiKey = $env:ANTHROPIC_API_KEY
    try
    {
        $git = { param([string]$Dir, [string[]]$GitArgs) & git -C $Dir -c user.name=SelfTest -c user.email=selftest@example.invalid @GitArgs 2>&1 | Out-Null; if ($LASTEXITCODE -ne 0) { throw "git $GitArgs failed" } }
        $origin = Join-Path $root 'origin.git'
        $seed = Join-Path $root 'seed'
        $main = Join-Path $root 'main'
        & git init -q --bare -b master $origin
        & git init -q -b master $seed
        [IO.File]::WriteAllText((Join-Path $seed 'a.txt'), "a`r`n")
        & $git $seed @('add', '-A'); & $git $seed @('commit', '-qm', 'one'); & $git $seed @('remote', 'add', 'origin', $origin); & $git $seed @('push', '-q', 'origin', 'master')
        & git clone -q $origin $main 2>&1 | Out-Null
        # A commit that exists only on origin: new must fetch before it resolves origin/master.
        [IO.File]::WriteAllText((Join-Path $seed 'b.txt'), "b`r`n")
        & $git $seed @('add', '-A'); & $git $seed @('commit', '-qm', 'two'); & $git $seed @('push', '-q', 'origin', 'master')
        $originHead = (& git -C $seed rev-parse HEAD).Trim()
        $env:POINTFRAME_PKG_TEST_MAIN = $main

        $stubDir = Join-Path $root 'stubs'
        New-Item -ItemType Directory -Path $stubDir | Out-Null
        $prelude = @'
# pointframe-selftest-stub
$a = @($args); $i = [Array]::IndexOf($a, '-LogDirectory'); $log = $a[$i + 1]
New-Item -ItemType Directory -Path $log -Force | Out-Null
Set-Content -LiteralPath (Join-Path $log 'events.jsonl') @('{"type":"thread.started","thread_id":"stub-session"}', '{"type":"turn.completed","usage":{"input_tokens":3,"cached_input_tokens":1,"output_tokens":2}}')

'@
        $stubs = @{
            ok = "Set-Content -LiteralPath (Join-Path `$log 'out.md') 'done'; exit 0"
            fail = "Set-Content -LiteralPath (Join-Path `$log 'out.md') 'it broke: Access is denied (an expected message of the code under test)'; exit 7"
            sleep = 'Start-Sleep -Seconds 120'
            envsleep = "Set-Content -LiteralPath (Join-Path `$log 'out.md') 'ENV-BLOCKED: sandbox'; Start-Sleep -Seconds 120"
            envfail = "Set-Content -LiteralPath (Join-Path `$log 'out.md') 'ENV-BLOCKED: no dotnet'; exit 5"
            bare = 'exit 0'
            tree = "`$child = Start-Process pwsh -ArgumentList '-NoProfile','-Command','Start-Sleep 300' -PassThru -NoNewWindow; Set-Content -LiteralPath `$env:POINTFRAME_PKG_TEST_PIDFILE `$child.Id; Start-Sleep -Seconds 120"
            mutate = "Set-Content -LiteralPath (Join-Path `$env:POINTFRAME_PKG_TEST_MAIN 'rogue.txt') 'x'; Set-Content -LiteralPath (Join-Path `$log 'out.md') 'wrote into main'; exit 0"
            env = "Set-Content -LiteralPath (Join-Path `$log 'out.md') 'ENV-BLOCKED: the sandbox could not run dotnet format'; exit 0"
        }
        foreach ($name in $stubs.Keys) { [IO.File]::WriteAllText((Join-Path $stubDir "$name.ps1"), $prelude + $stubs[$name] + "`r`n") }
        $statusLog = Join-Path $root 'status.log'
        $statusStub = Join-Path $stubDir 'status.ps1'
        [IO.File]::WriteAllText($statusStub, "Add-Content -LiteralPath '$statusLog' (`$args -join ' ')`r`n")

        $briefFile = Join-Path $root 'brief.md'
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'tests/agent-run/complete.md') -Destination $briefFile
        $manifestFile = Join-Path $root 'checks.json'
        [IO.File]::WriteAllText($manifestFile, (@{ extra = @(
                    @{ name = 'passes'; exe = 'pwsh'; args = @('-NoProfile', '-Command', 'exit 0') },
                    @{ name = 'fails'; exe = 'pwsh'; args = @('-NoProfile', '-Command', 'exit 3') },
                    @{ name = 'mutator'; mutates = $true; exe = 'pwsh'; args = @('-NoProfile', '-Command', "Set-Content -LiteralPath mutated.txt -Value 'x'") }) } | ConvertTo-Json -Depth 6))
        $dirtyManifest = Join-Path $root 'dirty-checks.json'
        [IO.File]::WriteAllText($dirtyManifest, (@{ extra = @(@{ name = 'dirty'; exe = 'pwsh'; args = @('-NoProfile', '-Command', "Set-Content -LiteralPath dirty.txt -Value 'x'") }) } | ConvertTo-Json -Depth 6))
        $env:POINTFRAME_PKG_TEST_PIDFILE = Join-Path $root 'child.pid'
        $badManifest = Join-Path $root 'bad-checks.json'
        [IO.File]::WriteAllText($badManifest, (@{ build = @('nope/Nope.csproj') } | ConvertTo-Json))

        $pkg = {
            param([string[]]$PackageArgs)
            $output = & pwsh -NoProfile -File $PSCommandPath @PackageArgs -Repo $main -StatusScript $statusStub -LockTimeoutSeconds 1 -GraceSeconds 0 2>&1
            [pscustomobject]@{ Exit = $LASTEXITCODE; Text = (($output | ForEach-Object { "$_" }) -join "`n") }
        }
        $stateFile = { param($slug) Get-Content -LiteralPath (Join-Path $main ".git/pointframe/packages/$slug.json") -Raw | ConvertFrom-Json -AsHashtable @script:JsonDate }
        $statePath = { param($slug) Join-Path $main ".git/pointframe/packages/$slug.json" }
        $packagesRoot = Join-Path $main '.git/pointframe/packages'

        # Fingerprint: raw file bytes, whatever autocrlf or assume-unchanged say, and no objects written to the shared database.
        & git -C $main config core.autocrlf true
        $objectsBefore = (& git -C $main count-objects -v) -join ';'
        $tracked = Join-Path $main 'a.txt'
        [IO.File]::WriteAllBytes($tracked, [byte[]](97, 10)); $fpLf = (Get-Fingerprint $main).treeHash
        [IO.File]::WriteAllBytes($tracked, [byte[]](97, 13, 10)); $fpCrlf = (Get-Fingerprint $main).treeHash
        & $expect ($fpLf -ne $fpCrlf) 'an LF to CRLF change was invisible to the fingerprint (autocrlf)'
        & git -C $main update-index --assume-unchanged a.txt
        [IO.File]::WriteAllBytes($tracked, [byte[]](98, 13, 10)); $fpHidden = (Get-Fingerprint $main).treeHash
        & $expect ($fpHidden -ne $fpCrlf) 'assume-unchanged hid a content change from the fingerprint'
        & git -C $main update-index --no-assume-unchanged a.txt
        [IO.File]::WriteAllBytes($tracked, [byte[]](97, 13, 10))
        $objectsAfter = (& git -C $main count-objects -v) -join ';'
        & $expect ($objectsBefore -eq $objectsAfter) 'fingerprinting wrote objects into the shared database'
        & git -C $main config --unset core.autocrlf

        # Dry run prints the plan and creates nothing.
        $r = & $pkg @('new', '-Id', 'dry', '-Brief', $briefFile, '-DryRun')
        & $expect ($r.Exit -eq 0 -and $r.Text -match 'dry run: nothing was created') "dry run failed: $($r.Text)"
        & $expect (-not (Test-Path (& $statePath 'dry')) -and -not (Test-Path (Join-Path $root 'main.worktrees')) -and -not (Test-Path (Join-Path $main '.git/pointframe'))) 'dry run created state or a worktree'
        & git -C $main show-ref --verify --quiet refs/heads/pkg/dry
        & $expect ($LASTEXITCODE -ne 0) 'dry run created a branch'

        # A manifest that names a missing project is refused before anything is created.
        $r = & $pkg @('new', '-Id', 'badpath', '-Brief', $briefFile, '-Checks', $badManifest)
        & $expect ($r.Exit -eq 1 -and $r.Text -match 'manifest path is not in' -and -not (Test-Path (& $statePath 'badpath'))) "missing manifest path accepted: $($r.Text)"

        # new: worktree, branch and base from the fetched origin/master, snapshots with hashes, status call.
        $r = & $pkg @('new', '-Id', 'pk', '-Brief', $briefFile, '-Checks', $manifestFile)
        & $expect ($r.Exit -eq 0) "new failed: $($r.Text)"
        $state = & $stateFile 'pk'
        & $expect ($state.stage -eq 'ready' -and $state.baseSha -eq $originHead) 'new did not fetch and branch from origin/master'
        & $expect ((Test-Path (Join-Path $state.worktree 'b.txt')) -and $state.branch -eq 'pkg/pk') 'worktree does not hold the fetched commit'
        & $expect ($state.brief.sha256 -eq (Get-Sha256 $briefFile) -and $state.manifest.sha256 -eq (Get-Sha256 $state.manifest.path)) 'snapshot hashes were not recorded'
        & $expect ((Test-Path $statusLog) -and (Get-Content $statusLog -Raw) -match '-Set -Id pk') 'status.ps1 -Set was not called'
        & $expect (@($state.history).Count -ge 2 -and $state.history[-1].to -eq 'ready') 'history is missing transitions'
        $r = & $pkg @('new', '-Id', 'pk', '-Brief', $briefFile)
        & $expect ($r.Exit -ne 0 -and $r.Text -match 'already exists') 'a second new for the same slug was accepted'

        # Immutable snapshots: read-only on disk, source edits do not matter, tampering is detected.
        $snapshot = [string]$state.brief.path
        $writable = $true
        try { Add-Content -LiteralPath $snapshot 'x' -ErrorAction Stop } catch { $writable = $false }
        & $expect (-not $writable) 'the brief snapshot was writable'
        $originalHash = $state.brief.sha256
        Add-Content -LiteralPath $briefFile "`r`n# Edited after new"
        $r = & $pkg @('show', '-Id', 'pk', '-Json')
        $view = $r.Text | ConvertFrom-Json
        & $expect (@($view.snapshotProblems).Count -eq 0 -and (Get-Sha256 $snapshot) -eq $originalHash) 'editing the source brief changed the snapshot'

        # Non-zero exit: an implementation failure stays one, even when its report mentions an access error.
        $r = & $pkg @('run', '-Id', 'pk', '-AgentRunScript', (Join-Path $stubDir 'fail.ps1'))
        $state = & $stateFile 'pk'
        if (@($state.runs).Count -eq 0) { throw "run recorded nothing (exit $($r.Exit)): $($r.Text)" }
        $run = $state.runs[-1]
        & $expect ($r.Exit -eq 1 -and $run.exitCode -eq 7 -and $run.termination -eq 'exit' -and $run.outcome -eq 'impl-failed' -and -not $run.envBlocked -and $state.stage -eq 'executor-impl-failed') "non-zero exit not recorded: $($r.Text)"

        # Environment-blocked: only the explicit ENV-BLOCKED line, and the exit status is kept next to it.
        $r = & $pkg @('run', '-Id', 'pk', '-AgentRunScript', (Join-Path $stubDir 'env.ps1'))
        $state = & $stateFile 'pk'
        & $expect ($state.runs[-1].outcome -eq 'env-blocked' -and $state.runs[-1].exitCode -eq 0 -and $state.runs[-1].envBlocked -and $state.runs[-1].envEvidence -match 'ENV-BLOCKED' -and $state.stage -eq 'executor-env-blocked') "env-blocked report not classified: $($r.Text)"
        $r = & $pkg @('run', '-Id', 'pk', '-AgentRunScript', (Join-Path $stubDir 'envfail.ps1'))
        $state = & $stateFile 'pk'
        & $expect ($state.runs[-1].outcome -eq 'impl-failed' -and $state.runs[-1].exitCode -eq 5 -and $state.runs[-1].envBlocked -and $state.runs[-1].envEvidence -match 'ENV-BLOCKED' -and $state.runs[-1].reason -match 'mixed: implementation failure with environment blocker') 'a mixed failure (non-zero exit plus an ENV-BLOCKED line) was not kept as impl-failed with the evidence'

        # A completed run records provider, model, effort, session id, artifact hashes and the provenance of the execution.
        $r = & $pkg @('run', '-Id', 'pk', '-AgentRunScript', (Join-Path $stubDir 'ok.ps1'), '-CodexVersion', '9.9.9-test')
        $state = & $stateFile 'pk'
        $run = $state.runs[-1]
        & $expect ($r.Exit -eq 0 -and $run.outcome -eq 'completed' -and $run.provider -eq 'codex' -and $run.model -eq 'gpt-6-luna' -and $run.effort -eq 'medium' -and $run.sessionId -eq 'stub-session') "completed run not recorded: $($r.Text)"
        & $expect ($run.artifacts.outMd.sha256 -eq (Get-Sha256 $run.artifacts.outMd.path) -and $run.artifacts.eventsJsonl.sha256 -eq (Get-Sha256 $run.artifacts.eventsJsonl.path)) 'artifact hashes are wrong'
        & $expect ($run.mainCheckoutBefore.treeHash -eq $run.mainCheckoutAfter.treeHash -and -not $state.guard.mainCheckoutMutated -and $run.mainCheckoutBefore.files -ge 1) 'the guard reported a mutation for a clean run'
        & $expect ($run.codexCliVersion -eq '9.9.9-test' -and $run.permissions.sandbox -eq 'workspace-write' -and $run.permissions.approval -eq 'never' -and $run.maxInputTokens -eq 20000000 -and $run.tokens.input -eq 3 -and $run.tokens.output -eq 2) 'run provenance (CLI version, permissions, token budget, tokens) was not recorded'
        & $expect ($run.executor.pid -gt 0 -and $run.executor.startUtc -and $run.wrapper.pid -gt 0 -and $run.wrapper.startUtc) 'the executor and wrapper process identities were not recorded'

        # Exit 0 without both artifacts is not a completed run.
        $r = & $pkg @('run', '-Id', 'pk', '-AgentRunScript', (Join-Path $stubDir 'bare.ps1'))
        $state = & $stateFile 'pk'
        & $expect ($r.Exit -eq 1 -and $state.runs[-1].outcome -eq 'impl-failed' -and $state.runs[-1].reason -match 'missing artifacts: out.md') "a run without its report was accepted as completed: $($r.Text)"

        # Timeout: the process tree is killed at the bound; an ENV-BLOCKED line does not turn it into something else.
        $timer = [Diagnostics.Stopwatch]::StartNew()
        $r = & $pkg @('run', '-Id', 'pk', '-AgentRunScript', (Join-Path $stubDir 'sleep.ps1'), '-MaxMinutes', '0.03')
        $state = & $stateFile 'pk'
        & $expect ($r.Exit -eq 1 -and $state.runs[-1].termination -eq 'timeout' -and $null -eq $state.runs[-1].exitCode -and $state.runs[-1].outcome -eq 'impl-failed' -and $timer.Elapsed.TotalSeconds -lt 60) "timeout not enforced: exit $($r.Exit), $([int]$timer.Elapsed.TotalSeconds)s, $($r.Text)"
        $r = & $pkg @('run', '-Id', 'pk', '-AgentRunScript', (Join-Path $stubDir 'envsleep.ps1'), '-MaxMinutes', '0.1')
        $state = & $stateFile 'pk'
        & $expect ($state.runs[-1].termination -eq 'timeout' -and $state.runs[-1].outcome -eq 'impl-failed' -and $state.runs[-1].envBlocked) "a timeout with an ENV-BLOCKED line was relabelled: $($r.Text)"

        # A grandchild of the executor does not survive the timeout.
        Remove-Item -LiteralPath $env:POINTFRAME_PKG_TEST_PIDFILE -ErrorAction SilentlyContinue
        $r = & $pkg @('run', '-Id', 'pk', '-AgentRunScript', (Join-Path $stubDir 'tree.ps1'), '-MaxMinutes', '0.15')
        $state = & $stateFile 'pk'
        $childPid = if (Test-Path $env:POINTFRAME_PKG_TEST_PIDFILE) { [int](Get-Content $env:POINTFRAME_PKG_TEST_PIDFILE -Raw).Trim() } else { 0 }
        Start-Sleep -Milliseconds 500
        & $expect ($childPid -gt 0 -and $state.runs[-1].termination -eq 'timeout' -and -not (Get-Process -Id $childPid -ErrorAction SilentlyContinue)) "a grandchild survived the timeout (pid $childPid): $($r.Text)"
        if ($childPid -gt 0) { Stop-Process -Id $childPid -Force -ErrorAction SilentlyContinue }

        # A kill that cannot be verified is recorded as kill-failed, never left as running, and the next run recovers.
        $r = & $pkg @('run', '-Id', 'pk', '-SimulateKillFailure')
        & $expect ($r.Exit -ne 0 -and $r.Text -match 'only accepted together with a marked self-test stub') "fault injection was accepted for a real executor: $($r.Text)"
        $r = & $pkg @('run', '-Id', 'pk', '-AgentRunScript', (Join-Path $stubDir 'sleep.ps1'), '-MaxMinutes', '0.03', '-SimulateKillFailure')
        $state = & $stateFile 'pk'
        & $expect ($state.runs[-1].termination -eq 'kill-failed' -and $state.stage -eq 'executor-kill-failed' -and $r.Exit -eq 1) "kill failure not recorded: stage $($state.stage), $($r.Text)"
        $r = & $pkg @('run', '-Id', 'pk', '-AgentRunScript', (Join-Path $stubDir 'ok.ps1'))
        $state = & $stateFile 'pk'
        & $expect ($r.Exit -eq 0 -and $state.stage -eq 'executor-completed' -and $state.runs[-2].termination -eq 'kill-failed' -and $state.runs[-2].reason -match 'interrupted') "recovery after kill-failed did not complete: $($r.Text)"
        & $expect ($state.runs[-1].containmentVerified -eq $true) 'a completed run did not record verified containment'

        # An unreadable file makes the fingerprint unknown: the run is refused, nothing is reported clean.
        $locked = [IO.File]::Open((Join-Path $main 'a.txt'), 'Open', 'Read', 'None')
        try
        {
            $unknown = Get-Fingerprint $main
            & $expect ($unknown.unknown -eq $true -and -not $unknown.treeHash -and -not (Test-FingerprintsEqual $unknown $unknown)) 'an unreadable file did not make the fingerprint unknown'
            $r = & $pkg @('run', '-Id', 'pk', '-AgentRunScript', (Join-Path $stubDir 'ok.ps1'))
            & $expect ($r.Exit -ne 0 -and $r.Text -match 'cannot be fingerprinted' -and (& $stateFile 'pk').stage -eq 'executor-completed') "a run started with an unknown main checkout fingerprint: $($r.Text)"
        }
        finally { $locked.Dispose() }

        # Checks: mutating steps first, every command with its exit code, log and the tree it ran on.
        [IO.File]::WriteAllText((Join-Path $state.worktree 'lf.txt'), "one`ntwo`n")
        $r = & $pkg @('check', '-Id', 'pk')
        $state = & $stateFile 'pk'
        if (@($state.checks).Count -eq 0) { throw "check recorded nothing (exit $($r.Exit)): $($r.Text)" }
        $check = $state.checks[-1]
        $named = @{}; foreach ($c in $check.commands) { $named[$c.name] = $c }
        & $expect ($r.Exit -eq 1 -and -not $check.ok -and $named['passes'].exitCode -eq 0 -and $named['fails'].exitCode -eq 3 -and (Test-Path $named['fails'].log) -and $state.stage -eq 'checks-failed') "check results not recorded: $($r.Text)"
        & $expect ([IO.File]::ReadAllText((Join-Path $state.worktree 'lf.txt')) -eq "one`r`ntwo`r`n" -and $named['crlf normalization'].files -contains 'lf.txt') 'changed text file was not normalized to CRLF'
        $order = @($check.commands | ForEach-Object { $_.name })
        & $expect (($order -join ',') -eq 'mutator,crlf normalization,passes,fails') "mutating steps did not run first: $($order -join ',')"
        & $expect ($named['mutator'].outputTree -ne $named['mutator'].inputTree -and $named['passes'].inputTree -eq $check.treeHash -and $named['fails'].inputTree -eq $check.treeHash -and $named['crlf normalization'].outputTree -eq $check.treeHash) 'commands do not record the tree they ran on'
        $r = & $pkg @('show', '-Id', 'pk', '-Json')
        if ($r.Exit -ne 0) { throw "show failed: $($r.Text)" }
        $view = $r.Text | ConvertFrom-Json
        & $expect ($view.checks[-1].stale -eq $false -and $view.checks[-1].fresh -eq $true -and $view.nextTransitions -contains 'run') "a check on an unchanged tree was not fresh: $($view.checks[-1].evidenceProblems)"
        [IO.File]::WriteAllText((Join-Path $state.worktree 'later.txt'), "changed after the check`r`n")
        $r = & $pkg @('show', '-Id', 'pk', '-Json'); $view = $r.Text | ConvertFrom-Json
        & $expect ($view.checks[-1].stale -eq $true -and $view.checks[-1].fresh -eq $false) 'a check was not reported stale after a worktree change'
        $r = & $pkg @('show', '-Id', 'pk')
        & $expect ($r.Text -match 'STALE' -and $r.Text -match 'next: run, check') 'show text does not report the stale check and next steps'

        # A forged check (ok, current tree, no commands) is not evidence.
        $currentTree = $view.currentTreeHash
        Update-State $packagesRoot 'pk' 'forge' { param($s) $s.checks = @($s.checks) + @(@{ checkId = 'forged'; utc = (Get-Now); ok = $true; treeBefore = 'x'; treeHash = $currentTree; commands = @() }) } | Out-Null
        $r = & $pkg @('show', '-Id', 'pk', '-Json'); $view = $r.Text | ConvertFrom-Json
        & $expect ($view.checks[-1].checkId -eq 'forged' -and $view.checks[-1].stale -eq $false -and $view.checks[-1].fresh -eq $false -and @($view.checks[-1].evidenceProblems).Count -gt 0) 'a forged check was displayed as fresh'
        Update-State $packagesRoot 'pk' 'unforge' { param($s) $s.checks = @($s.checks | Where-Object { $_.checkId -ne 'forged' }) } | Out-Null

        # A check whose ok contradicts its own exit codes is not evidence, even on an unchanged tree.
        Remove-Item -LiteralPath (Join-Path $state.worktree 'later.txt') -Force
        $lastCheckId = (& $stateFile 'pk').checks[-1].checkId
        Update-State $packagesRoot 'pk' 'contradict' { param($s) $s.checks = @($s.checks | ForEach-Object { if ($_.checkId -eq $lastCheckId) { $_.ok = $true }; $_ }) } | Out-Null
        $r = & $pkg @('show', '-Id', 'pk', '-Json'); $view = $r.Text | ConvertFrom-Json
        & $expect ($view.checks[-1].stale -eq $false -and $view.checks[-1].fresh -eq $false -and ($view.checks[-1].evidenceProblems -join ' ') -match 'ok=True but') "a contradictory passing check was fresh: $($r.Text)"
        Update-State $packagesRoot 'pk' 'uncontradict' { param($s) $s.checks = @($s.checks | ForEach-Object { if ($_.checkId -eq $lastCheckId) { $_.ok = $false }; $_ }) } | Out-Null
        $r = & $pkg @('show', '-Id', 'pk', '-Json'); $view = $r.Text | ConvertFrom-Json
        & $expect ($view.checks[-1].fresh -eq $true) 'restoring the check did not make it fresh again'

        # A worktree path pointing at another checkout is refused, not followed.
        $realWorktree = $state.worktree
        $foreign = Join-Path $root 'foreign'
        & git clone -q $origin $foreign 2>&1 | Out-Null
        foreach ($case in @(@{ path = $foreign; expect = 'different repository' }, @{ path = $main; expect = 'recorded branch' }))
        {
            $target = $case.path
            Update-State $packagesRoot 'pk' 'redirect' { param($s) $s.worktree = $target } | Out-Null
            $r = & $pkg @('check', '-Id', 'pk')
            & $expect ($r.Exit -eq 1 -and $r.Text -match $case.expect) "a redirected worktree ($target) was accepted: $($r.Text)"
            $r = & $pkg @('show', '-Id', 'pk', '-Json'); $view = $r.Text | ConvertFrom-Json
            & $expect (@($view.integrityProblems).Count -gt 0 -and -not ($view.checks | Where-Object { $_.fresh })) 'show trusted a redirected worktree'
        }
        Update-State $packagesRoot 'pk' 'restore' { param($s) $s.worktree = $realWorktree } | Out-Null

        # A validator that changes the worktree makes its own result untrustworthy.
        $r = & $pkg @('new', '-Id', 'dirty', '-Brief', $briefFile, '-Checks', $dirtyManifest)
        & $expect ($r.Exit -eq 0) "dirty package could not be created: $($r.Text)"
        $r = & $pkg @('check', '-Id', 'dirty')
        $state = & $stateFile 'dirty'
        & $expect ($r.Exit -eq 1 -and -not $state.checks[-1].ok -and $state.checks[-1].commands[-1].modifiedWorktree) "a validator that changed the tree was trusted: $($r.Text)"
        $r = & $pkg @('show', '-Id', 'dirty', '-Json'); $view = $r.Text | ConvertFrom-Json
        & $expect ($view.checks[-1].fresh -eq $false) 'a check whose validator changed the tree was reported fresh'

        # A locked state is refused, not overwritten.
        $holder = Enter-StateLock (Join-Path $main '.git/pointframe/packages') 'pk'
        try
        {
            $before = Get-Sha256 (& $statePath 'pk')
            $r = & $pkg @('check', '-Id', 'pk')
            & $expect ($r.Exit -eq 1 -and $r.Text -match 'locked' -and (Get-Sha256 (& $statePath 'pk')) -eq $before) "locked state not refused: $($r.Text)"
        }
        finally { $holder.Dispose() }

        # A write interrupted between the temp file and the rename leaves the old state, readable.
        $packages = Join-Path $main '.git/pointframe/packages'
        $old = Get-Sha256 (& $statePath 'pk')
        $interrupted = $false
        try { Update-State $packages 'pk' 'interrupted' { param($s) $s.stage = 'torn' } { throw 'simulated crash before rename' } | Out-Null } catch { $interrupted = $true }
        $survivor = Read-State $packages 'pk'
        & $expect ($interrupted -and (Get-Sha256 (& $statePath 'pk')) -eq $old -and $survivor.stage -ne 'torn') 'an interrupted write changed the state file'
        & $expect (@(Get-ChildItem $packages -Filter 'pk.json.*.tmp').Count -eq 1) 'the interrupted write left no temp file to recover from'
        Update-State $packages 'pk' 'recovered' { param($s) $s.stage = 'ready' } | Out-Null
        & $expect ((Read-State $packages 'pk').stage -eq 'ready' -and @(Get-ChildItem $packages -Filter 'pk.json.*.tmp').Count -eq 0) 'the next write did not recover and clean the stale temp file'

        # Tampered snapshot: refused before anything starts.
        Set-ItemProperty -LiteralPath $snapshot -Name IsReadOnly -Value $false
        Add-Content -LiteralPath $snapshot 'tampered'
        $r = & $pkg @('run', '-Id', 'pk', '-AgentRunScript', (Join-Path $stubDir 'ok.ps1'))
        & $expect ($r.Exit -ne 0 -and $r.Text -match 'snapshot changed' -and (Read-State $packages 'pk').stage -eq 'ready') "tampered snapshot accepted: $($r.Text)"

        # new is one transaction under the lock: a held lock refuses it, and two concurrent creations cannot both win.
        $holder = Enter-StateLock $packagesRoot 'lk'
        try
        {
            $r = & $pkg @('new', '-Id', 'lk', '-Brief', $briefFile)
            & $expect ($r.Exit -eq 1 -and $r.Text -match 'locked' -and -not (Test-Path (& $statePath 'lk'))) "new ignored a held lock: $($r.Text)"
        }
        finally { $holder.Dispose() }
        $raceOutputs = @()
        $raceProcesses = foreach ($n in 1..2)
        {
            $raceOutputs += @(Join-Path $root "race$n.out")
            Start-Process -FilePath (Get-Command pwsh -CommandType Application | Select-Object -First 1).Source -PassThru -NoNewWindow -RedirectStandardOutput (Join-Path $root "race$n.out") `
                -ArgumentList @('-NoProfile', '-File', "`"$PSCommandPath`"", 'new', '-Id', 'race', '-Brief', "`"$briefFile`"", '-Repo', "`"$main`"", '-StatusScript', "`"$statusStub`"", '-LockTimeoutSeconds', '120')
        }
        foreach ($p in $raceProcesses) { if (-not $p.WaitForExit(180000)) { $p.Kill($true) } }
        $codes = @($raceProcesses | ForEach-Object { $_.ExitCode } | Sort-Object)
        $raceState = & $stateFile 'race'
        $creations = @($raceState.history | Where-Object { $_.event -eq 'new' })
        & $expect (($codes -join ',') -eq '0,1' -and $raceState.stage -eq 'ready' -and $creations.Count -eq 1) "concurrent creation was not serialized (exit codes $($codes -join ','), stage $($raceState.stage), $($creations.Count) creations)"

        # Crash recovery: the wrapper died, its executor survived. The next run reaps it, reconciles the main checkout
        # against the fingerprint saved before the interrupted run, and only then starts.
        $r = & $pkg @('new', '-Id', 'rec', '-Brief', $briefFile)
        & $expect ($r.Exit -eq 0) "recovery package could not be created: $($r.Text)"
        $deadWrapper = Start-Process -FilePath (Get-Command pwsh -CommandType Application | Select-Object -First 1).Source -ArgumentList @('-NoProfile', '-Command', 'exit 0') -PassThru -NoNewWindow
        $deadWrapper.WaitForExit()
        $crash = {
            param([string]$Before, [string]$Slug = 'rec')
            $sleeper =Start-Process -FilePath (Get-Command pwsh -CommandType Application | Select-Object -First 1).Source -ArgumentList @('-NoProfile', '-Command', 'Start-Sleep -Seconds 300') -PassThru -NoNewWindow
            $sleeperStart = Get-ProcessStartIdentity $sleeper.Id
            $fingerprint = if ($Before -eq 'actual') { Get-Fingerprint $main } else { @{ head = 'bogus'; treeHash = 'bogus'; files = 1; seconds = 0 } }
            $deadId = $deadWrapper.Id
            Update-State $packagesRoot $Slug 'test-crash' {
                param($s)
                $s.stage = 'running'
                $s.currentRun = @{ runId = "crashed-$Before"; pid = $deadId }
                $s.runs = @($s.runs) + @(@{ runId = "crashed-$Before"; seq = 99; termination = 'running'; outcome = 'running'; wrapper = @{ pid = $deadId; startUtc = '2000-01-01T00:00:00.0000000+00:00' }
                        executor = @{ pid = $sleeper.Id; startUtc = $sleeperStart; jobObject = $false }; mainCheckoutBefore = $fingerprint; mainCheckoutAfter = $null; mainCheckoutMutated = $false })
            } | Out-Null
            $sleeper
        }
        $sleeper = & $crash 'actual'
        $r = & $pkg @('run', '-Id', 'rec', '-AgentRunScript', (Join-Path $stubDir 'ok.ps1'))
        $state = & $stateFile 'rec'
        $sleeper.WaitForExit(5000) | Out-Null
        $crashed = @($state.runs | Where-Object { $_.runId -eq 'crashed-actual' })[0]
        & $expect ($r.Exit -eq 0 -and $sleeper.HasExited -and $crashed.termination -eq 'abandoned' -and $crashed.reason -match 'reaped: True' -and $state.runs[-1].outcome -eq 'completed') "an interrupted run was not recovered: $($r.Text)"
        $sleeper = & $crash 'bogus'
        $r = & $pkg @('run', '-Id', 'rec', '-AgentRunScript', (Join-Path $stubDir 'ok.ps1'))
        $state = & $stateFile 'rec'
        $sleeper.WaitForExit(5000) | Out-Null
        & $expect ($r.Exit -eq 5 -and $sleeper.HasExited -and $state.stage -eq 'blocked-main-mutated' -and $state.guard.mainCheckoutMutated -and $r.Text -match 'since the interrupted run started') "a main checkout change before the restart became the new baseline: $($r.Text)"
        if (-not $sleeper.HasExited) { $sleeper.Kill() }

        # Two callers recovering the same abandoned run: the recovery is serialized, the second never clears the first's run.
        $r = & $pkg @('new', '-Id', 'rec2', '-Brief', $briefFile)
        & $expect ($r.Exit -eq 0) "concurrent-recovery package could not be created: $($r.Text)"
        $sleeper = & $crash 'actual' 'rec2'
        $recoverers = foreach ($n in 1..2)
        {
            Start-Process -FilePath (Get-Command pwsh -CommandType Application | Select-Object -First 1).Source -PassThru -NoNewWindow -RedirectStandardOutput (Join-Path $root "recover$n.out") `
                -ArgumentList @('-NoProfile', '-File', "`"$PSCommandPath`"", 'run', '-Id', 'rec2', '-Repo', "`"$main`"", '-StatusScript', "`"$statusStub`"", '-AgentRunScript', "`"$(Join-Path $stubDir 'ok.ps1')`"", '-LockTimeoutSeconds', '120', '-GraceSeconds', '0')
        }
        foreach ($p in $recoverers) { if (-not $p.WaitForExit(240000)) { $p.Kill($true) } }
        $sleeper.WaitForExit(5000) | Out-Null
        $recoverCodes = @($recoverers | ForEach-Object { $_.ExitCode })
        $state = & $stateFile 'rec2'
        $recoveries = @($state.history | Where-Object { $_.event -eq 'run-recovered' })
        $stillRunning = @($state.runs | Where-Object { $_.termination -eq 'running' })
        $completedRuns = @($state.runs | Where-Object { $_.outcome -eq 'completed' })
        & $expect ($sleeper.HasExited -and $recoveries.Count -eq 1 -and $stillRunning.Count -eq 0 -and $completedRuns.Count -ge 1 -and -not ($recoverCodes | Where-Object { $_ -notin @(0, 1) }) -and -not $state.currentRun -and $state.stage -eq 'executor-completed') "concurrent recoveries were not serialized (codes $($recoverCodes -join ','), $($recoveries.Count) recoveries, stage $($state.stage), $($completedRuns.Count) completed)"

        # Main checkout mutation: detected, the run stops, nothing is reverted.
        $r = & $pkg @('new', '-Id', 'mut', '-Brief', (Join-Path $PSScriptRoot 'tests/agent-run/complete.md'))
        & $expect ($r.Exit -eq 0) "second package could not be created: $($r.Text)"
        $r = & $pkg @('run', '-Id', 'mut', '-AgentRunScript', (Join-Path $stubDir 'mutate.ps1'))
        $state = & $stateFile 'mut'
        & $expect ($r.Exit -eq 5 -and $state.guard.mainCheckoutMutated -and $state.runs[-1].mainCheckoutMutated -and $state.stage -eq 'blocked-main-mutated') "main checkout mutation not detected: $($r.Text)"
        & $expect (Test-Path (Join-Path $main 'rogue.txt')) 'the guard reverted the executor''s change'
        $r = & $pkg @('run', '-Id', 'mut', '-AgentRunScript', (Join-Path $stubDir 'ok.ps1'))
        & $expect ($r.Exit -ne 0 -and $r.Text -match 'mutated') 'a package with a mutated main checkout accepted another run'

        # ---- Claude executors, against a marked stub that emits a recorded stream-json. No real claude process is ever started. ----
        Remove-Item Env:ANTHROPIC_API_KEY -ErrorAction SilentlyContinue
        $recordDir = Join-Path $root 'claude-records'
        $env:POINTFRAME_PKG_TEST_RECORD = $recordDir
        $resetRecords = { Remove-Item -LiteralPath $recordDir -Recurse -Force -ErrorAction SilentlyContinue; New-Item -ItemType Directory -Path $recordDir -Force | Out-Null }
        $calls = { @(Get-ChildItem -LiteralPath $recordDir -Filter 'call-*.json' -ErrorAction SilentlyContinue | Sort-Object { [int]($_.BaseName -replace '\D') }) }
        $claudeStubPrelude = @'
# pointframe-selftest-stub
$a = @($args)
if ($a -contains '--version') { if ($env:POINTFRAME_PKG_TEST_HANGVERSION) { Start-Sleep -Seconds 60 }; '9.9.9-stub (Claude Code)'; exit 0 }
$rec = $env:POINTFRAME_PKG_TEST_RECORD
$n = @(Get-ChildItem -LiteralPath $rec -Filter 'call-*.json').Count + 1
$stdinText = [Console]::In.ReadToEnd()
$settingsPath = $a[[Array]::IndexOf($a, '--settings') + 1]
[ordered]@{ args = $a; cwd = (Get-Location).Path; stdin = $stdinText; settings = (Get-Content -LiteralPath $settingsPath -Raw); mcp = (Get-Content -LiteralPath $a[[Array]::IndexOf($a, '--mcp-config') + 1] -Raw) } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $rec "call-$n.json")
function Emit([string]$Line) { [Console]::Out.WriteLine($Line); [Console]::Out.Flush() }
$tokens = if ($env:POINTFRAME_PKG_TEST_TOKENS) { [int]$env:POINTFRAME_PKG_TEST_TOKENS } else { 100 }
$resumeAt = [Array]::IndexOf($a, '--resume')
$sid = if ($resumeAt -ge 0) { $a[$resumeAt + 1] } else { 'stub-claude-session' }
if ($env:POINTFRAME_PKG_TEST_SID) { $sid = $env:POINTFRAME_PKG_TEST_SID }
if (-not $env:POINTFRAME_PKG_TEST_NOINIT) { Emit ('{"type":"system","subtype":"init","session_id":"' + $sid + '","model":"stub-model","cwd":' + ((Get-Location).Path | ConvertTo-Json) + '}') }
$usage = '"usage":{"input_tokens":' + $tokens + ',"cache_creation_input_tokens":0,"cache_read_input_tokens":0,"output_tokens":10}'
function Emit-Assistant([string]$Id, [string]$Tool) { Emit ('{"type":"assistant","message":{"id":"' + $Id + '","content":[{"type":"tool_use","id":"' + $Id + '-t","name":"' + $Tool + '","input":{}}],' + $usage + '}}') }
function Emit-Result([string]$Text, [string]$Denials = '[]') { Emit ('{"type":"result","subtype":"success","is_error":false,"result":' + ($Text | ConvertTo-Json) + ',"session_id":"' + $sid + '","total_cost_usd":0.0123,"usage":{"input_tokens":' + $tokens + ',"cache_creation_input_tokens":0,"cache_read_input_tokens":0,"output_tokens":10},"permission_denials":' + $Denials + '}') }
$denyEvent = '{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"m1-t","is_error":true,"content":"Permission to use Bash has been denied."}]}}'
$denyList = '[{"tool_name":"Bash","tool_use_id":"m1-t","tool_input":{"command":"rm -rf x"}}]'

'@
        $claudeStubs = @{
            'c-ok' = "Emit-Assistant 'm1' 'Read'; Emit-Assistant 'm1' 'Read'; Emit-Result 'done: all good'; exit 0"
            'c-deny' = "Emit-Assistant 'm1' 'Bash'; Emit `$denyEvent; Emit-Result 'I could not run the command.' `$denyList; exit 0"
            'c-denyenv' = "Emit-Assistant 'm1' 'Bash'; Emit `$denyEvent; Emit-Result 'ENV-BLOCKED: dotnet build: Permission to use Bash has been denied.' `$denyList; exit 0"
            'c-usagesleep' = "Emit-Assistant 'm1' 'Read'; Start-Sleep -Seconds 120"
            'c-tree' = "`$child = Start-Process pwsh -ArgumentList '-NoProfile','-Command','Start-Sleep 300' -PassThru -NoNewWindow; Set-Content -LiteralPath `$env:POINTFRAME_PKG_TEST_PIDFILE `$child.Id; Start-Sleep -Seconds 120"
            'c-write-in' = "New-Item -ItemType Directory -Path 'allowed' -Force | Out-Null; Set-Content -LiteralPath (Join-Path (Get-Location).Path 'allowed/a.txt') 'x'; Emit-Result 'changed allowed/a.txt'; exit 0"
            'c-write-out' = "Set-Content -LiteralPath (Join-Path (Get-Location).Path 'outside.txt') 'x'; Emit-Result 'changed outside.txt'; exit 0"
            'c-write-ignored' = "Set-Content -LiteralPath (Join-Path (Get-Location).Path 'ignored-note.txt') 'x'; Emit-Result 'changed an ignored file'; exit 0"
            'c-rename' = "New-Item -ItemType Directory -Path 'allowed' -Force | Out-Null; Move-Item -LiteralPath 'b.txt' -Destination 'allowed/moved.txt'; Emit-Result 'moved b.txt into allowed/'; exit 0"
            'c-write-bin' = "New-Item -ItemType Directory -Path 'bin' -Force | Out-Null; Set-Content -LiteralPath (Join-Path (Get-Location).Path 'bin/out.dll') 'x'; Emit-Result 'build output'; exit 0"
            'c-terminal-only' = @'
Emit '{"type":"result","subtype":"success","is_error":false,"result":"done: terminal usage only","session_id":"stub-claude-session","usage":{"input_tokens":1200,"cache_creation_input_tokens":0,"cache_read_input_tokens":800,"output_tokens":10},"permission_denials":[]}'; exit 0
'@
            'c-mutate' ="Set-Content -LiteralPath (Join-Path `$env:POINTFRAME_PKG_TEST_MAIN 'rogue-claude.txt') 'x'; Emit-Result 'wrote into main'; exit 0"
        }
        foreach ($name in $claudeStubs.Keys) { [IO.File]::WriteAllText((Join-Path $stubDir "$name.ps1"), $claudeStubPrelude + $claudeStubs[$name] + "`r`n") }
        [IO.File]::WriteAllText((Join-Path $stubDir 'unmarked.ps1'), "exit 0`r`n")
        $stub = { param($name) Join-Path $stubDir "$name.ps1" }
        $briefHaiku = Join-Path $root 'brief-haiku.md'
        [IO.File]::WriteAllText($briefHaiku, (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'tests/agent-run/complete.md') -Raw) + "`r`nHaiku-scope: mechanical`r`n")
        $briefHelper = Join-Path $root 'brief-helper.md'
        [IO.File]::WriteAllText($briefHelper, (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'tests/agent-run/complete.md') -Raw) + "`r`nHaiku-scope: helper`r`n")
        $scopeManifest = Join-Path $root 'scope-checks.json'
        [IO.File]::WriteAllText($scopeManifest, (@{ allowedPaths = @('allowed/'); allowedScripts = @('scripts/custom-check.ps1') } | ConvertTo-Json))
        $fixPrompt = Join-Path $root 'fix.md'
        [IO.File]::WriteAllText($fixPrompt, "Fix request: please make the build pass.`r`n")

        # In-process: argument quoting, the bypass refusal and the incremental stream reader.
        & $expect ((Format-WindowsArgument 'plain') -eq 'plain' -and (Format-WindowsArgument 'a b') -eq '"a b"' -and (Format-WindowsArgument 'say "hi"') -eq '"say \"hi\""' -and (Format-WindowsArgument 'C:\dir with space\') -eq '"C:\dir with space\\"') 'argument quoting is wrong'
        foreach ($bad in @('bypassPermissions', '--dangerously-skip-permissions', '--permission-mode', '--allow-dangerously-skip-permissions'))
        {
            $refused = $false
            try { Assert-NoBypass @('-p', $bad) } catch { $refused = $true }
            & $expect $refused "a bypass argument ($bad) was not refused"
        }
        $refused = $false
        try { Assert-NoBypass @('-p', '--model', 'x', '--settings', 'y') } catch { $refused = $true }
        & $expect (-not $refused) 'a harmless argument list was refused'
        $partialPath = Join-Path $root 'partial.jsonl'
        [IO.File]::WriteAllText($partialPath, '{"type":"system","subtype":"init","session_id":"s1"}' + "`n" + '{"type":"result","result":"tail"')
        $partial = New-StreamMonitor $partialPath
        Read-StreamMonitor $partial
        & $expect ($partial.SessionId -eq 's1' -and -not $partial.ResultSeen) 'a partial last line was consumed early'
        [IO.File]::AppendAllText($partialPath, ',"session_id":"s1"}' + "`n")
        Read-StreamMonitor $partial
        & $expect ($partial.ResultSeen -and $partial.ResultText -eq 'tail') 'the completed line was not read on the next poll'

        # Glob semantics of allowedPaths: * stays inside one segment, ** crosses segments, ? is one non-/ character, a trailing / is a directory.
        $globCases = @(
            @('docs/*.md', 'docs/a.md', $true), @('docs/*.md', 'docs/nested/a.md', $false), @('docs/**', 'docs/nested/a.md', $true), @('docs/**/*.md', 'docs/a.md', $true), @('docs/**/*.md', 'docs/x/y/a.md', $true)
            @('a?.txt', 'ab.txt', $true), @('a?.txt', 'a/.txt', $false), @('a?.txt', 'abc.txt', $false), @('src/', 'src/deep/file.cs', $true), @('src/', 'srcx/file.cs', $false)
            @('exact.txt', 'exact.txt', $true), @('exact.txt', 'dir/exact.txt', $false), @('*.md', 'README.md', $true), @('*.md', 'docs/README.md', $false), @('**/x.md', 'x.md', $true), @('**/x.md', 'a/b/x.md', $true)
        )
        foreach ($case in $globCases) { & $expect ((Test-PathAllowed $case[1] @($case[0])) -eq $case[2]) "glob '$($case[0])' against '$($case[1])' should be $($case[2])" }

        # Token totals come from the terminal result; the per-message sums are kept next to them. Fixtures: the two real smoke streams.
        $fixtureDir = Join-Path $PSScriptRoot 'tests/package-claude'
        foreach ($case in @(@{ file = 'smoke-1-events.jsonl'; input = 81942; output = 1842; session = 'c60c8b16' }, @{ file = 'smoke-2-events.jsonl'; input = 87489; output = 1897; session = '8a7a7fb5' }))
        {
            $fixtureMonitor = New-StreamMonitor (Join-Path $fixtureDir $case.file)
            Read-StreamMonitor $fixtureMonitor -Final
            $record = Get-ClaudeTokenRecord $fixtureMonitor
            & $expect ($record.input -eq $case.input -and $record.output -eq $case.output -and $record.source -eq 'terminal result usage' -and $record.reported.output -eq $case.output -and $record.perMessage.output -gt 0 -and $record.crossCheck -and $fixtureMonitor.SessionId -like "$($case.session)*") "token totals of $($case.file) are wrong: $($record | ConvertTo-Json -Depth 4 -Compress)"
        }

        # A completed Sonnet run: working directory, stdin, settings, no bypass, provenance.
        $r = & $pkg @('new', '-Id', 'cl', '-Brief', $briefFile)
        & $expect ($r.Exit -eq 0) "claude package could not be created: $($r.Text)"
        & $resetRecords
        $env:POINTFRAME_PKG_TEST_TOKENS = '100'
        $r = & $pkg @('run', '-Id', 'cl', '-Executor', 'claude-sonnet', '-ClaudePath', (& $stub 'c-ok'))
        $state = & $stateFile 'cl'
        if (@($state.runs).Count -eq 0) { throw "claude run recorded nothing (exit $($r.Exit)): $($r.Text)" }
        $run = $state.runs[-1]
        & $expect ($r.Exit -eq 0 -and $run.outcome -eq 'completed' -and $run.provider -eq 'claude' -and $run.executorKind -eq 'claude-sonnet' -and $run.model -eq 'claude-sonnet-5-5' -and $run.sessionId -eq 'stub-claude-session' -and $state.stage -eq 'executor-completed') "claude run not completed: $($r.Text)"
        & $expect ($run.tokens.input -eq 100 -and $run.tokens.output -eq 10 -and $run.tokens.reportedCostUsd -eq 0.0123 -and $run.tokens.costNote -match 'not an invoice') "tokens were not summed once per message or the cost is not labelled: $($run.tokens | ConvertTo-Json -Compress)"
        & $expect ($run.claudeCliVersion -eq '9.9.9-stub (Claude Code)' -and $run.permissions.mode -eq 'default' -and $run.permissions.bypass -eq $false -and $run.permissions.settingsFile.sha256 -eq (Get-Sha256 $run.permissions.settingsFile.path) -and $run.authMode -match '^subscription') 'claude provenance (version, permission mode, settings hash, auth mode) was not recorded'
        & $expect ($run.artifacts.outMd.sha256 -and (Get-Content -LiteralPath $run.artifacts.outMd.path -Raw) -match 'done: all good' -and $run.containmentVerified -eq $true -and @($run.permissionDenials).Count -eq 0) 'the report, containment or denial list of a clean claude run is wrong'
        $callFiles = @(& $calls)
        & $expect ($callFiles.Count -eq 1) "expected exactly one claude invocation, got $($callFiles.Count)"
        $call = Get-Content -LiteralPath $callFiles[0].FullName -Raw | ConvertFrom-Json
        $callArgs = @($call.args)
        & $expect ([IO.Path]::GetFullPath([string]$call.cwd).TrimEnd('\') -ieq [IO.Path]::GetFullPath([string]$state.worktree).TrimEnd('\')) "the process working directory was '$($call.cwd)', not the worktree '$($state.worktree)'"
        & $expect ($call.stdin -eq (Get-Content -LiteralPath $run.prompt.path -Raw) -and $call.stdin -match 'Use the checked executor launcher' -and $call.stdin -match [regex]::Escape([string]$state.worktree)) 'stdin was not the prompt file followed by EOF'
        $settingsAt = [Array]::IndexOf($callArgs, '--settings')
        $settingsArg = if ($settingsAt -ge 0) { [string]$callArgs[$settingsAt + 1] } else { '' }
        & $expect ($settingsAt -ge 0 -and $settingsArg -like "$(Join-Path $packagesRoot 'cl')*" -and $settingsArg -notlike "$($state.worktree)*" -and (Get-Sha256 $settingsArg) -eq $run.permissions.settingsFile.sha256) "the settings file was not passed from the package state directory: $settingsArg"
        & $expect (-not ($callArgs -match '(?i)bypass|dangerously|permission-mode')) 'a bypass or permission-mode argument was passed'
        foreach ($required in @('-p', '--model', 'claude-sonnet-5-5', '--output-format', 'stream-json', '--verbose', '--strict-mcp-config', '--mcp-config', '--append-system-prompt', '--setting-sources')) { & $expect ($callArgs -contains $required) "claude argument '$required' is missing" }
        # No project, user or local settings are loaded, and the tool set is explicit.
        $sourcesAt = [Array]::IndexOf($callArgs, '--setting-sources')
        $toolsAt = [Array]::IndexOf($callArgs, '--tools')
        & $expect ($sourcesAt -ge 0 -and $callArgs[$sourcesAt + 1] -eq '' -and -not ($callArgs -contains 'project')) "--setting-sources must be empty so no project settings load: '$($callArgs[$sourcesAt + 1])'"
        & $expect ($toolsAt -ge 0 -and $callArgs[$toolsAt + 1] -eq 'Read,Edit,Write,Glob,Grep,Bash') "--tools is not the explicit tool list: '$($callArgs[$toolsAt + 1])'"
        $rulesArg = [string]$callArgs[[Array]::IndexOf($callArgs, '--append-system-prompt') + 1]
        & $expect ($rulesArg -match 'not a sandbox' -and $rulesArg -match 'main-checkout guard') 'the rules do not state the accepted limitation of the allowlist'
        & $expect ($rulesArg -match 'absolute paths' -and $rulesArg -match 'explicit working directory' -and $rulesArg -match 'stdin' -and $rulesArg -match 'Never commit, push' -and $rulesArg -match 'ENV-BLOCKED: <command>: <error>' -and $rulesArg -match 'End with a report') 'the executor rules are incomplete'
        $settingsJson = $call.settings | ConvertFrom-Json
        & $expect ($settingsJson.permissions.defaultMode -eq 'default' -and $settingsJson.permissions.allow -contains 'Read' -and $settingsJson.permissions.allow -contains 'Bash(dotnet test:*)' -and $settingsJson.permissions.allow -contains 'Bash(pwsh scripts/kb.ps1 init:*)' -and $settingsJson.permissions.allow -contains 'Bash(pwsh scripts/verify.ps1:*)' -and -not ($settingsJson.permissions.allow -contains 'Bash(pwsh scripts/*)') -and -not ($settingsJson.permissions.allow -contains 'Bash(dotnet restore:*)') -and $settingsJson.permissions.deny -contains 'WebFetch' -and $settingsJson.permissions.deny -contains 'Bash(git push:*)' -and -not ($settingsJson.permissions.allow -match 'python|bypass|rm ')) "the settings allowlist is wrong: $($call.settings)"
        & $expect (@(($call.mcp | ConvertFrom-Json).mcpServers.PSObject.Properties).Count -eq 0) 'the MCP config is not empty'
        & $expect ($settingsJson.permissions.allow -contains 'Bash(pwsh scripts/kb.ps1 check:*)' -and $settingsJson.permissions.allow -contains 'Bash(pwsh scripts/kb.ps1 selftest:*)') 'an executor lost kb.ps1 check'
        & $expect ($rulesArg -match 'pwsh scripts/kb\.ps1 init -Role executor' -and $rulesArg -match 'without -C') 'the rules do not start with the kb init or forbid git -C'
        $wtForward = ([string]$state.worktree -replace '\\', '/').TrimEnd('/')
        $wtBack = ([string]$state.worktree -replace '/', '\').TrimEnd('\')
        $allowed = {
            param([string]$Tool, [string]$Command)
            foreach ($rule in @($settingsJson.permissions.allow))
            {
                if ($rule -match "^$Tool\((.*)\)$" -and $Command -like ($Matches[1] -replace ':\*$', '*')) { return $true }
            }
            $false
        }
        & $expect (-not (@($settingsJson.permissions.allow) -match '^PowerShell') -and $settingsJson.permissions.deny -contains 'PowerShell' -and $callArgs[$toolsAt + 1] -notmatch 'PowerShell' -and $rulesArg -match 'Use the Bash tool for every shell command') 'the PowerShell tool is still available or the rules do not say to use Bash'
        foreach ($tool in @('Bash'))
        {
            foreach ($command in @('pwsh scripts/kb.ps1 init -Role helper', 'pwsh scripts/kb.ps1 init -Role executor', 'pwsh -NoProfile -File scripts/kb.ps1 init -Role helper', 'pwsh -NoProfile -File scripts/kb.ps1 init -Role executor',
                    "git -C `"$wtForward`" status --short", "git -C $wtForward diff --stat", "git -C `"$wtBack`" log -3", "git -C $wtBack show HEAD", 'pwsh scripts/verify.ps1', 'pwsh -NoProfile -File scripts/verify.ps1', 'pwsh scripts/kb.ps1 read scripts/package.ps1', 'pwsh scripts/kb.ps1 check'))
            {
                & $expect (& $allowed $tool $command) "$tool rule does not allow '$command'"
            }
            foreach ($command in @("git -C `"C:/somewhere/else`" status", "git -C `"$wtForward`" commit -m x", "git -C $wtBack push", 'pwsh scripts/custom.ps1', 'pwsh scripts/../outside.ps1', 'pwsh scripts/status.ps1 -Publish', 'pwsh -NoProfile -File scripts/status.ps1 -Publish', 'dotnet restore'))
            {
                & $expect (-not (& $allowed $tool $command)) "$tool rule allows '$command'"
            }
        }
        & $expect (($run.claudeArguments -join ' ') -match '--settings' -and -not (($run.claudeArguments -join ' ') -match 'absolute paths')) 'the recorded argument list is wrong'

        # The key is never read for its value: only its presence selects the auth mode.
        $env:ANTHROPIC_API_KEY = 'sk-selftest-not-a-real-key'
        $r = & $pkg @('run', '-Id', 'cl', '-Executor', 'claude-sonnet', '-ClaudePath', (& $stub 'c-ok'))
        $state = & $stateFile 'cl'
        & $expect ($state.runs[-1].authMode -match '^api-key' -and -not ((Get-Content -LiteralPath (& $statePath 'cl') -Raw) -match 'sk-selftest')) 'the auth mode was wrong or the key value reached the state'
        Remove-Item Env:ANTHROPIC_API_KEY -ErrorAction SilentlyContinue

        # A permission denial is recorded as a blocker, the outcome is impl-failed, and there is no retry.
        & $resetRecords
        $r = & $pkg @('run', '-Id', 'cl', '-Executor', 'claude-sonnet', '-ClaudePath', (& $stub 'c-deny'))
        $state = & $stateFile 'cl'
        $run = $state.runs[-1]
        & $expect ($r.Exit -eq 1 -and $run.outcome -eq 'impl-failed' -and @($run.permissionDenials).Count -eq 1 -and $run.permissionDenials[0].tool -eq 'Bash' -and $run.permissionDenials[0].reason -match 'denied' -and $run.reason -match 'permission denied' -and -not $run.envBlocked -and $state.stage -eq 'executor-impl-failed') "a permission denial was not recorded as impl-failed: $($r.Text)"
        & $expect (@(& $calls).Count -eq 1) 'a denied run was retried'
        $r = & $pkg @('run', '-Id', 'cl', '-Executor', 'claude-sonnet', '-ClaudePath', (& $stub 'c-denyenv'))
        $state = & $stateFile 'cl'
        $run = $state.runs[-1]
        & $expect ($run.outcome -eq 'env-blocked' -and $run.envBlocked -and @($run.permissionDenials).Count -eq 1 -and $state.stage -eq 'executor-env-blocked') "an executor ENV-BLOCKED line with a denial was not env-blocked: $($r.Text)"

        # The runner refuses to add a bypass, or to override its own isolation arguments, and starts nothing.
        & $resetRecords
        $runsBefore = @((& $stateFile 'cl').runs).Count
        foreach ($extra in @(' --permission-mode bypassPermissions', ' --permission-mode=bypassPermissions', ' --dangerously-skip-permissions', ' --settings other.json', ' --settings=other.json', ' --allowedTools=Bash(*)', ' --allowed-tools Bash', ' --tools=Bash',
                ' --mcp-config=other.json', ' --resume=other-session', ' --model=claude-opus-9', ' --add-dir=C:/elsewhere', ' --system-prompt=x', ' --setting-sources=user', ' --verbose-extra'))
        {
            $r = & $pkg @('run', '-Id', 'cl', '-Executor', 'claude-sonnet', '-ClaudePath', (& $stub 'c-ok'), '-ClaudeExtraArgs', $extra)
            & $expect ($r.Exit -ne 0 -and $r.Text -match 'refused' -and @(& $calls).Count -eq 0 -and @((& $stateFile 'cl').runs).Count -eq $runsBefore) "extra arguments '$extra' were not refused before the launch: $($r.Text)"
        }
        $r = & $pkg @('run', '-Id', 'cl', '-Executor', 'claude-sonnet', '-ClaudePath', (& $stub 'unmarked'))
        & $expect ($r.Exit -ne 0 -and $r.Text -match 'marked self-test stub' -and @(& $calls).Count -eq 0) "an unmarked script was accepted as claude: $($r.Text)"
        $r = & $pkg @('run', '-Id', 'cl', '-Resume', '-PromptFile', $fixPrompt)
        & $expect ($r.Exit -ne 0 -and $r.Text -match 'only supported for the Claude executors') "-Resume was accepted for codex: $($r.Text)"
        $r = & $pkg @('run', '-Id', 'cl', '-Executor', 'claude-sonnet', '-ClaudePath', (& $stub 'c-ok'), '-Resume')
        & $expect ($r.Exit -ne 0 -and $r.Text -match 'needs -PromptFile') "-Resume without a prompt file was accepted: $($r.Text)"

        # Token budget: enforced from the stream while the process runs; the tree is killed, termination=budget.
        $env:POINTFRAME_PKG_TEST_TOKENS = '5000'
        $timer = [Diagnostics.Stopwatch]::StartNew()
        $r = & $pkg @('run', '-Id', 'cl', '-Executor', 'claude-sonnet', '-ClaudePath', (& $stub 'c-usagesleep'), '-MaxInputTokens', '1000')
        $state = & $stateFile 'cl'
        $run = $state.runs[-1]
        & $expect ($r.Exit -eq 1 -and $run.termination -eq 'budget' -and $null -eq $run.exitCode -and $run.outcome -eq 'impl-failed' -and $run.containmentVerified -eq $true -and $timer.Elapsed.TotalSeconds -lt 90) "the token budget was not enforced: exit $($r.Exit), $([int]$timer.Elapsed.TotalSeconds)s, $($r.Text)"
        # Terminal usage alone (no per-message usage) counts: 1200 fresh + 800 cached against a 1000 limit is over budget, never completed.
        $r = & $pkg @('run', '-Id', 'cl', '-Executor', 'claude-sonnet', '-ClaudePath', (& $stub 'c-terminal-only'), '-MaxInputTokens', '1000')
        $state = & $stateFile 'cl'
        & $expect ($r.Exit -eq 1 -and $state.runs[-1].termination -eq 'budget' -and $state.runs[-1].outcome -eq 'impl-failed' -and $state.runs[-1].tokens.input -eq 2000) "terminal-only usage over the limit was not a budget failure: $($r.Text)"
        $r = & $pkg @('run', '-Id', 'cl', '-Executor', 'claude-sonnet', '-ClaudePath', (& $stub 'c-terminal-only'), '-MaxInputTokens', '5000')
        $state = & $stateFile 'cl'
        & $expect ($r.Exit -eq 0 -and $state.runs[-1].termination -eq 'exit' -and $state.runs[-1].outcome -eq 'completed' -and $state.runs[-1].tokens.input -eq 2000) "terminal-only usage within the limit did not complete: $($r.Text)"
        $peak = New-StreamMonitor (Join-Path $root 'no-such-peak.jsonl')
        $peak.ResultUsage = @{ input = 1200; cacheCreation = 0; cacheRead = 800; output = 10 }
        $first = Get-MonitorEnforcedInput $peak
        $peak.ResultUsage = @{ input = 10; cacheCreation = 0; cacheRead = 0; output = 10 }
        & $expect ($first -eq 2000 -and (Get-MonitorEnforcedInput $peak) -eq 2000) 'the enforced input went down after a smaller report'

        # Resume continues the same session; the remaining budget is what is left of the cumulative one.
        $env:POINTFRAME_PKG_TEST_TOKENS = '600'
        $r = & $pkg @('run', '-Id', 'cl', '-Executor', 'claude-sonnet', '-ClaudePath', (& $stub 'c-ok'), '-MaxInputTokens', '1000')
        $state = & $stateFile 'cl'
        & $expect ($r.Exit -eq 0 -and $state.runs[-1].outcome -eq 'completed' -and $state.runs[-1].tokens.input -eq 600 -and $state.runs[-1].budget.priorInputTokens -eq 0) "the budget of a new session did not start fresh: $($r.Text)"
        & $resetRecords
        $r = & $pkg @('run', '-Id', 'cl', '-Executor', 'claude-sonnet', '-ClaudePath', (& $stub 'c-usagesleep'), '-MaxInputTokens', '1000', '-Resume', '-PromptFile', $fixPrompt)
        $state = & $stateFile 'cl'
        $run = $state.runs[-1]
        & $expect ($run.termination -eq 'budget' -and $run.budget.priorInputTokens -eq 600 -and $run.budget.remainingInputTokens -eq 400 -and $run.budget.priorSeconds -gt 0 -and $run.resumedFrom.sessionId -eq 'stub-claude-session' -and $run.chainId -eq $state.runs[-2].chainId) "resume did not carry the budgets: $($r.Text)"
        $callFiles = @(& $calls)
        $call = if ($callFiles.Count) { Get-Content -LiteralPath $callFiles[0].FullName -Raw | ConvertFrom-Json } else { $null }
        $resumeAt = if ($call) { [Array]::IndexOf(@($call.args), '--resume') } else { -1 }
        & $expect ($call -and $resumeAt -ge 0 -and $call.args[$resumeAt + 1] -eq 'stub-claude-session' -and $call.stdin -match 'Fix request' -and [IO.Path]::GetFullPath([string]$call.cwd).TrimEnd('\') -ieq [IO.Path]::GetFullPath([string]$state.worktree).TrimEnd('\')) 'resume did not continue the session with the new prompt in the worktree'
        $r = & $pkg @('run', '-Id', 'cl', '-Executor', 'claude-sonnet', '-ClaudePath', (& $stub 'c-ok'), '-MaxInputTokens', '1000', '-Resume', '-PromptFile', $fixPrompt)
        & $expect ($r.Exit -ne 0 -and $r.Text -match 'budget of this executor is used up') "a resume past the cumulative budget was started: $($r.Text)"

        # A grandchild of the Claude executor does not survive the timeout.
        $env:POINTFRAME_PKG_TEST_TOKENS = '100'
        Remove-Item -LiteralPath $env:POINTFRAME_PKG_TEST_PIDFILE -ErrorAction SilentlyContinue
        $r = & $pkg @('run', '-Id', 'cl', '-Executor', 'claude-sonnet', '-ClaudePath', (& $stub 'c-tree'), '-MaxMinutes', '0.15')
        $state = & $stateFile 'cl'
        $childPid = if (Test-Path $env:POINTFRAME_PKG_TEST_PIDFILE) { [int](Get-Content $env:POINTFRAME_PKG_TEST_PIDFILE -Raw).Trim() } else { 0 }
        Start-Sleep -Milliseconds 500
        & $expect ($childPid -gt 0 -and $state.runs[-1].termination -eq 'timeout' -and -not (Get-Process -Id $childPid -ErrorAction SilentlyContinue)) "a grandchild of the claude stub survived the timeout (pid $childPid): $($r.Text)"
        if ($childPid -gt 0) { Stop-Process -Id $childPid -Force -ErrorAction SilentlyContinue }

        # Haiku: refused without the header line, before anything is recorded or started.
        & $resetRecords
        $runsBefore = @((& $stateFile 'cl').runs).Count
        $r = & $pkg @('run', '-Id', 'cl', '-Executor', 'claude-haiku', '-ClaudePath', (& $stub 'c-ok'))
        & $expect ($r.Exit -ne 0 -and $r.Text -match 'Haiku-scope' -and @(& $calls).Count -eq 0 -and @((& $stateFile 'cl').runs).Count -eq $runsBefore) "claude-haiku was accepted without the Haiku-scope header: $($r.Text)"
        # The model belongs to the executor's family: a Haiku model cannot ride in on another executor kind, and the reverse.
        foreach ($pair in @(@('claude-sonnet', 'claude-haiku-5-5'), @('claude-sonnet', 'haiku'), @('claude-haiku', 'claude-sonnet-5-5'), @('claude-sonnet', 'claude-opus-5-5')))
        {
            $r = & $pkg @('run', '-Id', 'cl', '-Executor', $pair[0], '-Model', $pair[1], '-ClaudePath', (& $stub 'c-ok'))
            & $expect ($r.Exit -ne 0 -and $r.Text -match 'does not belong to executor' -and @(& $calls).Count -eq 0 -and @((& $stateFile 'cl').runs).Count -eq $runsBefore) "model $($pair[1]) was accepted for $($pair[0]): $($r.Text)"
        }

        # The version probe is bounded and contained: a hanging --version does not stall the run.
        $env:POINTFRAME_PKG_TEST_HANGVERSION = '1'
        $timer = [Diagnostics.Stopwatch]::StartNew()
        $r = & $pkg @('run', '-Id', 'cl', '-Executor', 'claude-sonnet', '-ClaudePath', (& $stub 'c-ok'), '-ProbeSeconds', '2')
        $state = & $stateFile 'cl'
        Remove-Item Env:POINTFRAME_PKG_TEST_HANGVERSION -ErrorAction SilentlyContinue
        & $expect ($r.Exit -eq 0 -and $null -eq $state.runs[-1].claudeCliVersion -and $state.runs[-1].outcome -eq 'completed' -and $timer.Elapsed.TotalSeconds -lt 50) "a hanging version probe was not bounded: $([int]$timer.Elapsed.TotalSeconds)s, $($r.Text)"

        # The session identity comes from the init event; a resumed session must answer with the id it resumed.
        $env:POINTFRAME_PKG_TEST_NOINIT = '1'
        $r = & $pkg @('run', '-Id', 'cl', '-Executor', 'claude-sonnet', '-ClaudePath', (& $stub 'c-ok'))
        $state = & $stateFile 'cl'
        Remove-Item Env:POINTFRAME_PKG_TEST_NOINIT -ErrorAction SilentlyContinue
        & $expect ($r.Exit -eq 1 -and $state.runs[-1].outcome -eq 'impl-failed' -and $state.runs[-1].reason -match 'session identity' -and $null -eq $state.runs[-1].sessionId) "a run without an init session id was accepted: $($r.Text)"
        $r = & $pkg @('run', '-Id', 'cl', '-Executor', 'claude-sonnet', '-ClaudePath', (& $stub 'c-ok'))
        $env:POINTFRAME_PKG_TEST_SID = 'some-other-session'
        $r = & $pkg @('run', '-Id', 'cl', '-Executor', 'claude-sonnet', '-ClaudePath', (& $stub 'c-ok'), '-Resume', '-PromptFile', $fixPrompt)
        $state = & $stateFile 'cl'
        Remove-Item Env:POINTFRAME_PKG_TEST_SID -ErrorAction SilentlyContinue
        & $expect ($r.Exit -eq 1 -and $state.runs[-1].outcome -eq 'impl-failed' -and $state.runs[-1].reason -match 'session identity: the resumed session answered' -and $null -eq $state.runs[-1].sessionId) "a resumed session with a different init id was accepted: $($r.Text)"

        # Haiku mechanical: changes inside allowedPaths pass, a change outside them is a scope failure.
        $r = & $pkg @('new', '-Id', 'hk', '-Brief', $briefHaiku, '-Checks', $scopeManifest)
        & $expect ($r.Exit -eq 0) "haiku package could not be created: $($r.Text)"
        $r = & $pkg @('run', '-Id', 'hk', '-Executor', 'claude-haiku', '-ClaudePath', (& $stub 'c-write-in'))
        $state = & $stateFile 'hk'
        & $expect ($r.Exit -eq 0 -and $state.runs[-1].outcome -eq 'completed' -and $state.runs[-1].model -eq 'claude-haiku-5-5' -and $state.runs[-1].haikuScope -eq 'mechanical') "a change inside allowedPaths was refused: $($r.Text)"
        $r = & $pkg @('run', '-Id', 'hk', '-Executor', 'claude-haiku', '-ClaudePath', (& $stub 'c-write-out'))
        $state = & $stateFile 'hk'
        & $expect ($r.Exit -eq 1 -and $state.runs[-1].outcome -eq 'impl-failed' -and $state.runs[-1].reason -match '^scope:' -and $state.runs[-1].reason -match 'outside\.txt' -and @($state.runs[-1].scopeViolations) -contains 'outside.txt') "a change outside allowedPaths was not a scope failure: $($r.Text)"
        # The brief-named script is allowed (executors), the settings are the executor's, and nothing else of scripts/ is.
        $hkSettings = Get-Content -LiteralPath $state.runs[-1].permissions.settingsFile.path -Raw | ConvertFrom-Json
        & $expect ($hkSettings.permissions.allow -contains 'Bash(pwsh scripts/custom-check.ps1:*)' -and $hkSettings.permissions.allow -contains 'Bash(pwsh -NoProfile -File scripts/custom-check.ps1:*)' -and $hkSettings.permissions.allow -contains 'Edit') 'the script named by the brief is not allowed'
        $badScripts = Join-Path $root 'bad-scripts.json'
        foreach ($badScript in @('../outside.ps1', 'scripts/../x.ps1', 'other/x.ps1', '/abs/x.ps1', 'scripts/x.sh'))
        {
            [IO.File]::WriteAllText($badScripts, (@{ allowedScripts = @($badScript) } | ConvertTo-Json))
            $refused = $false
            try { Read-Manifest $badScripts | Out-Null } catch { $refused = $true }
            & $expect $refused "allowed script '$badScript' was accepted"
        }
        # Ignored files and renames are scope too (the run's own changes are the difference of two worktree snapshots).
        Add-Content -LiteralPath (Join-Path $main '.git/info/exclude') "ignored-*.txt`n"
        $r = & $pkg @('run', '-Id', 'hk', '-Executor', 'claude-haiku', '-ClaudePath', (& $stub 'c-write-ignored'))
        $state = & $stateFile 'hk'
        & $expect ($r.Exit -eq 1 -and $state.runs[-1].reason -match '^scope:' -and @($state.runs[-1].scopeViolations) -contains 'ignored-note.txt') "an ignored file outside allowedPaths was not a scope failure: $($r.Text)"
        $r = & $pkg @('run', '-Id', 'hk', '-Executor', 'claude-haiku', '-ClaudePath', (& $stub 'c-rename'))
        $state = & $stateFile 'hk'
        & $expect ($r.Exit -eq 1 -and @($state.runs[-1].scopeViolations) -contains 'b.txt' -and @($state.runs[-1].scopeViolations) -notcontains 'allowed/moved.txt') "a rename out of scope into allowed/ hid the source: $($r.Text) $(@($state.runs[-1].scopeViolations) -join ',')"
        $r = & $pkg @('run', '-Id', 'hk', '-Executor', 'claude-haiku', '-ClaudePath', (& $stub 'c-write-bin'))
        $state = & $stateFile 'hk'
        & $expect ($r.Exit -eq 0 -and $state.runs[-1].outcome -eq 'completed') "build output was treated as scope: $($r.Text)"

        # Haiku helper is read-only: any change is a scope failure; the main checkout guard covers Claude executors too.
        $r = & $pkg @('new', '-Id', 'hh', '-Brief', $briefHelper)
        & $expect ($r.Exit -eq 0) "helper package could not be created: $($r.Text)"
        $r = & $pkg @('run', '-Id', 'hh', '-Executor', 'claude-haiku', '-ClaudePath', (& $stub 'c-ok'))
        $state = & $stateFile 'hh'
        & $expect ($r.Exit -eq 0 -and $state.runs[-1].outcome -eq 'completed') "a read-only helper run failed: $($r.Text)"
        $helperCall = Get-Content -LiteralPath (@(& $calls)[-1].FullName) -Raw | ConvertFrom-Json
        $helperRules = [string]$helperCall.args[[Array]::IndexOf(@($helperCall.args), '--append-system-prompt') + 1]
        & $expect ($helperRules -match 'pwsh scripts/kb\.ps1 init -Role helper' -and $helperRules -notmatch 'init -Role executor') 'a Haiku helper was not told to start with kb init -Role helper'
        $helperSettings = Get-Content -LiteralPath $state.runs[-1].permissions.settingsFile.path -Raw | ConvertFrom-Json
        $helperArgs = @($helperCall.args)
        & $expect ($helperArgs[[Array]::IndexOf($helperArgs, '--tools') + 1] -eq 'Read,Glob,Grep,Bash' -and $helperSettings.permissions.deny -contains 'Edit' -and $helperSettings.permissions.deny -contains 'Write' -and -not ($helperSettings.permissions.allow -contains 'Edit') -and -not ($helperSettings.permissions.allow -contains 'Write') -and -not ($helperSettings.permissions.allow -contains 'Bash(dotnet build:*)') -and -not ($helperSettings.permissions.allow -contains 'Bash(pwsh scripts/verify.ps1:*)') -and $helperSettings.permissions.allow -contains 'Bash(pwsh scripts/kb.ps1 read:*)') 'a Haiku helper still has Edit, Write or mutating shell rules'
        & $expect ($helperSettings.permissions.allow -contains 'Bash(pwsh scripts/kb.ps1 check -NoFix)' -and $helperSettings.permissions.allow -contains 'Bash(pwsh -NoProfile -File scripts/kb.ps1 check -NoFix)' -and -not ($helperSettings.permissions.allow -contains 'Bash(pwsh scripts/kb.ps1 check:*)') -and -not ($helperSettings.permissions.allow -contains 'Bash(pwsh -NoProfile -File scripts/kb.ps1 check:*)') -and -not ($helperSettings.permissions.allow -contains 'Bash(pwsh scripts/kb.ps1 selftest:*)') -and $helperSettings.permissions.allow -contains 'Bash(pwsh scripts/kb.ps1 changed:*)') 'a helper can run kb.ps1 check without -NoFix'
        $helperRuleMatches = {
            param([string]$Command)
            foreach ($rule in @($helperSettings.permissions.allow)) { if ($rule -match '^Bash\((.*)\)$' -and $Command -like ($Matches[1] -replace ':\*$', '*')) { return $true } }
            $false
        }
        & $expect ((& $helperRuleMatches 'pwsh scripts/kb.ps1 check -NoFix') -and -not (& $helperRuleMatches 'pwsh scripts/kb.ps1 check') -and -not (& $helperRuleMatches 'pwsh scripts/kb.ps1 check -Json') -and -not (& $helperRuleMatches 'pwsh scripts/kb.ps1 check -NoFix; rm -rf x')) 'the helper check rule does not pin -NoFix exactly'
        $r = & $pkg @('run', '-Id', 'hh', '-Executor', 'claude-haiku', '-ClaudePath', (& $stub 'c-write-ignored'))
        $state = & $stateFile 'hh'
        & $expect ($r.Exit -eq 1 -and @($state.runs[-1].scopeViolations) -contains 'ignored-note.txt') "a helper that wrote an ignored file was not a scope failure: $($r.Text)"
        $r = & $pkg @('run', '-Id', 'hh', '-Executor', 'claude-haiku', '-ClaudePath', (& $stub 'c-write-in'))
        $state = & $stateFile 'hh'
        & $expect ($r.Exit -eq 1 -and $state.runs[-1].outcome -eq 'impl-failed' -and $state.runs[-1].reason -match '^scope:') "a helper that changed a file was not a scope failure: $($r.Text)"
        $r = & $pkg @('run', '-Id', 'hh', '-Executor', 'claude-haiku', '-ClaudePath', (& $stub 'c-mutate'))
        $state = & $stateFile 'hh'
        & $expect ($r.Exit -eq 5 -and $state.guard.mainCheckoutMutated -and $state.stage -eq 'blocked-main-mutated') "a claude executor writing into the main checkout was not stopped: $($r.Text)"

        # The real install is an npm shim: it is resolved from its own text to node plus the package script, never run through a shell.
        $fakeNpm = Join-Path $root 'fake-npm'
        $fakeScriptDir = Join-Path $fakeNpm 'node_modules/@anthropic-ai/claude-code'
        New-Item -ItemType Directory -Path $fakeScriptDir -Force | Out-Null
        $fakeShim = Join-Path $fakeNpm 'claude.cmd'
        [IO.File]::WriteAllText($fakeShim, "@ECHO off`r`nSETLOCAL`r`nSET dp0=%~dp0`r`n`"%_prog%`"  `"%dp0%\node_modules\@anthropic-ai\claude-code\cli.js`" %*`r`n")
        [IO.File]::WriteAllText((Join-Path $fakeScriptDir 'cli.js'), "// replaced below`r`n")
        $oldClaudePath = $script:ClaudePath
        try
        {
            $pwshExe = (Get-Command pwsh -CommandType Application | Select-Object -First 1).Source
            $script:ClaudePath = $pwshExe
            $launcher = Resolve-ClaudeLauncher
            & $expect ($launcher.File -eq $pwshExe -and @($launcher.Prefix).Count -eq 0) 'an .exe path was not accepted as is'
            $nodeCommand = Get-Command node -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
            if (-not $nodeCommand) { Write-Host 'SKIP self-test: node is not installed, the npm shim cases cannot run.' }
            else
            {
                $script:ClaudePath = $fakeShim
                $launcher = Resolve-ClaudeLauncher
                & $expect ($launcher.File -eq $nodeCommand.Source -and (@($launcher.Prefix) -join ' ') -match 'claude-code\\cli\.js' -and $launcher.Display -match 'cli\.js') "an npm shim did not resolve to node plus cli.js: $($launcher | ConvertTo-Json -Compress)"
                $script:ClaudePath = Join-Path $fakeNpm 'claude'
                [IO.File]::WriteAllText($script:ClaudePath, "#!/bin/sh`n")
                $launcher = Resolve-ClaudeLauncher
                & $expect ($launcher.File -eq $nodeCommand.Source) 'the extensionless shim did not resolve through the .cmd beside it'
            }
            # The current npm package ships a native binary and its shim names it: resolved to that .exe, nothing started.
            $nativeDir = Join-Path $fakeNpm 'native'
            New-Item -ItemType Directory -Path (Join-Path $nativeDir 'node_modules/@anthropic-ai/claude-code/bin') -Force | Out-Null
            [IO.File]::WriteAllText((Join-Path $nativeDir 'node_modules/@anthropic-ai/claude-code/bin/claude.exe'), 'not a real binary')
            [IO.File]::WriteAllText((Join-Path $nativeDir 'claude.cmd'), "@ECHO off`r`n`"%dp0%\node_modules\@anthropic-ai\claude-code\bin\claude.exe`"   %*`r`n")
            $script:ClaudePath = Join-Path $nativeDir 'claude.cmd'
            $launcher = Resolve-ClaudeLauncher
            & $expect ($launcher.File -like '*claude-code\bin\claude.exe' -and @($launcher.Prefix).Count -eq 0) "a shim that names the native claude.exe was not resolved to it: $($launcher | ConvertTo-Json -Compress)"
            $brokenShim = Join-Path $fakeNpm 'broken.cmd'
            [IO.File]::WriteAllText($brokenShim, "@ECHO off`r`necho nothing useful here`r`n")
            $missingShim = Join-Path $fakeNpm 'missing.cmd'
            [IO.File]::WriteAllText($missingShim, "@ECHO off`r`n`"%_prog%`" `"%dp0%\node_modules\@anthropic-ai\claude-code\bin\claude.exe`" %*`r`n")
            New-Item -ItemType Directory -Path (Join-Path $fakeNpm 'node_modules/evil') -Force | Out-Null
            [IO.File]::WriteAllText((Join-Path $fakeNpm 'node_modules/evil/tool.js'), "// not claude`r`n")
            $evilShim = Join-Path $fakeNpm 'evil.cmd'
            [IO.File]::WriteAllText($evilShim, "@ECHO off`r`n`"%_prog%`" `"%dp0%\node_modules\evil\tool.js`" %*`r`n")
            $commentShim = Join-Path $fakeNpm 'comment.cmd'
            [IO.File]::WriteAllText($commentShim, "@ECHO off`r`nREM `"%dp0%\node_modules\@anthropic-ai\claude-code\cli.js`"`r`n:: `"%dp0%\node_modules\@anthropic-ai\claude-code\cli.js`"`r`n`"%_prog%`" `"%dp0%\node_modules\evil\tool.js`" %*`r`n")
            foreach ($case in @(@{ path = $evilShim; expect = 'names no node_modules' }, @{ path = $commentShim; expect = 'names no node_modules' }, @{ path = $brokenShim; expect = 'names no node_modules' }, @{ path = $missingShim; expect = 'does not exist' }, @{ path = (Join-Path $fakeNpm 'x.txt'); expect = 'neither an .exe nor an npm shim' }))
            {
                $script:ClaudePath = $case.path
                if ($case.path -like '*.txt') { [IO.File]::WriteAllText($case.path, 'x') }
                $message = ''
                try { Resolve-ClaudeLauncher | Out-Null } catch { $message = $_.Exception.Message }
                & $expect ($message -match 'cannot resolve' -and $message -match [regex]::Escape($case.expect)) "an unresolvable shim ($($case.path)) was not refused clearly: '$message'"
            }
        }
        finally { $script:ClaudePath = $oldClaudePath }

        if ($nodeCommand)
        {
            [IO.File]::WriteAllText((Join-Path $fakeScriptDir 'cli.js'), @'
const fs = require('fs');
const a = process.argv.slice(2);
if (a.includes('--version')) { console.log('9.9.9-npm-stub'); process.exit(0); }
const stdin = fs.readFileSync(0, 'utf8');
const sid = 'npm-stub-session';
const out = (o) => process.stdout.write(JSON.stringify(o) + '\n');
out({ type: 'system', subtype: 'init', session_id: sid, model: 'stub', cwd: process.cwd() });
if (process.env.POINTFRAME_PKG_TEST_JS_TREE) {
  const c = require('child_process').spawn('pwsh', ['-NoProfile', '-Command', 'Start-Sleep 300'], { stdio: 'ignore' });
  fs.writeFileSync(process.env.POINTFRAME_PKG_TEST_PIDFILE, String(c.pid));
  setTimeout(() => {}, 120000);
} else {
  out({ type: 'result', subtype: 'success', is_error: false, result: 'npm stub done, stdin chars ' + stdin.length, session_id: sid, usage: { input_tokens: 5, output_tokens: 1 } });
}
'@)
            $r = & $pkg @('run', '-Id', 'cl', '-Executor', 'claude-sonnet', '-ClaudePath', $fakeShim)
            $state = & $stateFile 'cl'
            $run = $state.runs[-1]
            & $expect ($r.Exit -eq 0 -and $run.outcome -eq 'completed' -and $run.sessionId -eq 'npm-stub-session' -and $run.claudeCliVersion -eq '9.9.9-npm-stub' -and $run.claudeExecutable -match 'cli\.js' -and $run.executor.pid -gt 0 -and (Get-Content -LiteralPath $run.artifacts.outMd.path -Raw) -match 'stdin chars [1-9]') "a run through an npm shim (node + cli.js) did not complete: $($r.Text)"

            # node's own children are in the job: a grandchild of node does not survive the timeout.
            $env:POINTFRAME_PKG_TEST_JS_TREE = '1'
            Remove-Item -LiteralPath $env:POINTFRAME_PKG_TEST_PIDFILE -ErrorAction SilentlyContinue
            $r = & $pkg @('run', '-Id', 'cl', '-Executor', 'claude-sonnet', '-ClaudePath', $fakeShim, '-MaxMinutes', '0.15')
            $state = & $stateFile 'cl'
            $childPid = if (Test-Path $env:POINTFRAME_PKG_TEST_PIDFILE) { [int](Get-Content $env:POINTFRAME_PKG_TEST_PIDFILE -Raw).Trim() } else { 0 }
            Start-Sleep -Milliseconds 500
            & $expect ($childPid -gt 0 -and $state.runs[-1].termination -eq 'timeout' -and -not (Get-Process -Id $childPid -ErrorAction SilentlyContinue)) "a child of node survived the timeout (pid $childPid): $($r.Text)"
            if ($childPid -gt 0) { Stop-Process -Id $childPid -Force -ErrorAction SilentlyContinue }
            Remove-Item Env:POINTFRAME_PKG_TEST_JS_TREE -ErrorAction SilentlyContinue
        }
        # An interrupted Claude run keeps its place in the cumulative budget: rebuilt from its archived stream, or unknown and then blocking.
        $r = & $pkg @('new', '-Id', 'rc', '-Brief', $briefFile)
        & $expect ($r.Exit -eq 0) "accounting package could not be created: $($r.Text)"
        $env:POINTFRAME_PKG_TEST_TOKENS = '100'
        $r = & $pkg @('run', '-Id', 'rc', '-Executor', 'claude-sonnet', '-ClaudePath', (& $stub 'c-ok'), '-MaxInputTokens', '100000')
        $state = & $stateFile 'rc'
        $chainOfRc = [string]$state.runs[-1].chainId
        $crashedDir = Join-Path $root 'crashed-claude'
        New-Item -ItemType Directory -Path $crashedDir -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $fixtureDir 'smoke-2-events.jsonl') -Destination (Join-Path $crashedDir 'events.jsonl')
        (Get-Item -LiteralPath (Join-Path $crashedDir 'events.jsonl')).LastWriteTimeUtc = [DateTime]::UtcNow.AddSeconds(-295)
        $crashClaude = {
            param([string]$RunName, [string]$LogDir)
            $fingerprint = Get-Fingerprint $main
            $deadId = $deadWrapper.Id
            Update-State $packagesRoot 'rc' 'test-crash-claude' {
                param($s)
                $s.stage = 'running'
                $s.currentRun = @{ runId = $RunName; pid = $deadId }
                $s.runs = @($s.runs) + @(@{ runId = $RunName; seq = 99; provider = 'claude'; executorKind = 'claude-sonnet'; chainId = $chainOfRc; maxMinutes = 30; startedUtc = [DateTime]::UtcNow.AddMinutes(-5).ToString('o'); termination = 'running'; outcome = 'running'
                        sessionId = $null; tokens = $null; wrapper = @{ pid = $deadId; startUtc = '2000-01-01T00:00:00.0000000+00:00' }; executor = $null; logDirectory = $LogDir; mainCheckoutBefore = $fingerprint; mainCheckoutAfter = $null; mainCheckoutMutated = $false })
            } | Out-Null
        }
        & $crashClaude 'crashed-claude' $crashedDir
        $r = & $pkg @('run', '-Id', 'rc', '-Executor', 'claude-sonnet', '-ClaudePath', (& $stub 'c-ok'), '-MaxInputTokens', '100000', '-Resume', '-PromptFile', $fixPrompt)
        $state = & $stateFile 'rc'
        $crashedRun = @($state.runs | Where-Object { $_.runId -eq 'crashed-claude' })[0]
        & $expect ($r.Exit -eq 0 -and $crashedRun.tokens.input -eq 87489 -and $crashedRun.tokens.output -eq 1897 -and $crashedRun.sessionId -like '8a7a7fb5*' -and $crashedRun.elapsedSeconds -ge 250 -and $crashedRun.elapsedSeconds -le 1800 -and $crashedRun.accounting.elapsedIsUpperBound -eq $true -and $crashedRun.accounting.known -eq $true -and $crashedRun.accounting.reconstructed -eq $true -and $crashedRun.termination -eq 'abandoned') "an interrupted run's accounting was not reconstructed: $($r.Text)"
        & $expect ($state.runs[-1].budget.priorInputTokens -eq 87589 -and $state.runs[-1].resumedFrom.runId -eq 'crashed-claude' -and $state.runs[-1].outcome -eq 'completed') "the resume did not charge the interrupted run: $($state.runs[-1].budget | ConvertTo-Json -Compress) $($r.Text)"
        & $crashClaude 'crashed-claude-2' (Join-Path $root 'no-such-archive')
        $r = & $pkg @('run', '-Id', 'rc', '-Executor', 'claude-sonnet', '-ClaudePath', (& $stub 'c-ok'), '-MaxInputTokens', '100000', '-Resume', '-PromptFile', $fixPrompt)
        $state = & $stateFile 'rc'
        $unknownRun = @($state.runs | Where-Object { $_.runId -eq 'crashed-claude-2' })[0]
        & $expect ($r.Exit -ne 0 -and $r.Text -match 'accounting of run crashed-claude-2 is unknown' -and $r.Text -match 'events\.jsonl is missing' -and $unknownRun.accounting.known -eq $false) "a resume after an unrecoverable run was not refused: $($r.Text)"

        # Session identity holds for every termination: a timed-out run is a resume candidate only with a consistent init id.
        $r = & $pkg @('new', '-Id', 'ti', '-Brief', $briefFile)
        & $expect ($r.Exit -eq 0) "identity package could not be created: $($r.Text)"
        $env:POINTFRAME_PKG_TEST_TOKENS = '100'
        $env:POINTFRAME_PKG_TEST_NOINIT = '1'
        $r = & $pkg @('run', '-Id', 'ti', '-Executor', 'claude-sonnet', '-ClaudePath', (& $stub 'c-usagesleep'), '-MaxMinutes', '0.05')
        Remove-Item Env:POINTFRAME_PKG_TEST_NOINIT -ErrorAction SilentlyContinue
        $state = & $stateFile 'ti'
        & $expect ($state.runs[-1].termination -eq 'timeout' -and $null -eq $state.runs[-1].sessionId -and $state.runs[-1].reason -match 'session identity') "a timed-out run without an init id kept a session id: $($r.Text)"
        $r = & $pkg @('run', '-Id', 'ti', '-Executor', 'claude-sonnet', '-ClaudePath', (& $stub 'c-ok'), '-MaxMinutes', '10', '-Resume', '-PromptFile', $fixPrompt)
        & $expect ($r.Exit -ne 0 -and $r.Text -match 'nothing to resume' -and $r.Text -match 'init event' -and $r.Text -match 'no init event') "a timed-out run without an init id was resumable: $($r.Text)"
        $r = & $pkg @('run', '-Id', 'ti', '-Executor', 'claude-sonnet', '-ClaudePath', (& $stub 'c-usagesleep'), '-MaxMinutes', '0.05')
        $state = & $stateFile 'ti'
        $timedOut = $state.runs[-1]
        & $expect ($timedOut.termination -eq 'timeout' -and $timedOut.sessionId -eq 'stub-claude-session' -and -not $timedOut.sessionIdentityProblem) "a timed-out run with a valid init id lost its session: $($r.Text)"
        $r = & $pkg @('run', '-Id', 'ti', '-Executor', 'claude-sonnet', '-ClaudePath', (& $stub 'c-ok'), '-MaxMinutes', '10', '-Resume', '-PromptFile', $fixPrompt)
        $state = & $stateFile 'ti'
        & $expect ($r.Exit -eq 0 -and $state.runs[-1].resumedFrom.runId -eq $timedOut.runId -and $state.runs[-1].outcome -eq 'completed') "a timed-out run with a matching id was not resumable: $($r.Text)"
        $validRunId = [string]$state.runs[-1].runId
        $env:POINTFRAME_PKG_TEST_SID = 'mismatched-session'
        $r = & $pkg @('run', '-Id', 'ti', '-Executor', 'claude-sonnet', '-ClaudePath', (& $stub 'c-usagesleep'), '-MaxMinutes', '0.3', '-Resume', '-PromptFile', $fixPrompt)
        Remove-Item Env:POINTFRAME_PKG_TEST_SID -ErrorAction SilentlyContinue
        $state = & $stateFile 'ti'
        $mismatched = $state.runs[-1]
        & $expect ($mismatched.termination -eq 'timeout' -and $null -eq $mismatched.sessionId -and $mismatched.sessionIdentityProblem -match 'resumed session answered' -and $mismatched.reason -match 'session identity') "a timed-out resumed run with a mismatched id kept its session id: $($r.Text)"
        $r = & $pkg @('run', '-Id', 'ti', '-Executor', 'claude-sonnet', '-ClaudePath', (& $stub 'c-ok'), '-MaxMinutes', '10', '-Resume', '-PromptFile', $fixPrompt)
        $state = & $stateFile 'ti'
        & $expect ($r.Exit -eq 0 -and $state.runs[-1].resumedFrom.runId -eq $validRunId -and $state.runs[-1].resumedFrom.runId -ne $mismatched.runId) "the mismatched run became a resume candidate: $($r.Text)"

        $runsBefore = @((& $stateFile 'cl').runs).Count
        $r = & $pkg @('run', '-Id', 'cl', '-Executor', 'claude-sonnet', '-ClaudePath', $brokenShim)
        & $expect ($r.Exit -ne 0 -and $r.Text -match 'cannot resolve the npm shim' -and @((& $stateFile 'cl').runs).Count -eq $runsBefore) "an unresolvable shim started a run: $($r.Text)"
    }
    catch { $failures.Add("self-test crashed: $($_.Exception.Message) at line $($_.InvocationInfo.ScriptLineNumber)") }
    finally
    {
        if ($null -eq $oldMain) { Remove-Item Env:POINTFRAME_PKG_TEST_MAIN -ErrorAction SilentlyContinue } else { $env:POINTFRAME_PKG_TEST_MAIN = $oldMain }
        Remove-Item Env:POINTFRAME_PKG_TEST_PIDFILE, Env:POINTFRAME_PKG_TEST_SKIP_KILL, Env:POINTFRAME_PKG_TEST_RECORD, Env:POINTFRAME_PKG_TEST_TOKENS -ErrorAction SilentlyContinue
        if ($oldApiKey) { $env:ANTHROPIC_API_KEY = $oldApiKey }
        Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
    }
    if ($failures.Count)
    {
        $failures | ForEach-Object { Write-Host "ERROR self-test: $_" }
        exit 1
    }
    Write-Host 'package self-test passed.'
    exit 0
}

if ($SelfTest) { Invoke-SelfTest }
if (-not $Command) { Write-Host 'ERROR a command is required: new, run, check or show'; exit 2 }
if (-not $Id) { Write-Host 'ERROR -Id is required'; exit 2 }
try
{
    switch ($Command)
    {
        'new' { Invoke-New }
        'run' { Invoke-Run }
        'check' { Invoke-Check }
        'show' { Invoke-Show }
    }
}
catch
{
    Write-Host "ERROR $($_.Exception.Message)"
    exit $(if ($script:ExitCode) { $script:ExitCode } else { 1 })
}
exit $script:ExitCode
