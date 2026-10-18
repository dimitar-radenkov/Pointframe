#Requires -Version 7.0
<#
Mechanics of one work package for a Codex or Claude executor, with durable state, the runner's commit and a reviewed,
host-checked acceptance. No push, PR or merge (merge-pr.ps1 keeps the gate).

  pwsh scripts/package.ps1 commit -Id <slug> [-Message <text>]
  pwsh scripts/package.ps1 review -Id <slug> -Level light|heavy [-Reviewer codex-sol|codex-luna|claude-opus|claude-sonnet] [-ResetBudget -Reason <text>]
  pwsh scripts/package.ps1 disposition -Id <slug> -Finding <id> -Accept|-Reject -Reason <text>
  pwsh scripts/package.ps1 fix -Id <slug> [-Executor same] [-ResetBudget -Reason <text>]
  pwsh scripts/package.ps1 accept -Id <slug> -Reason <text>
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
commit  coordinator step after a fresh passing check: commits the package changes on pkg/<slug> (trailer "Package: <slug> run
        <runId>"), fetches and rebases onto the base ref, and re-runs check when the rebase changed the tree. Executors never commit.
review  runs a read-only reviewer of the other provider (Codex through codex-ask.ps1 -Sandbox read-only, Claude through claude -p
        with Read/Glob/Grep only and no shell) on exactly the committed head. The reviewer's final message is saved as
        the verdict file; the host validates it against scripts/package-verdict.schema.json and checks it against the observed head,
        tree, clean worktree, the brief's criteria and a fresh check. Anything else is recorded as invalid, never PASS. Each review
        round counts against a persisted budget of 3; later rounds resume the reviewer's session and must return a full verdict.
disposition  the coordinator's accept or reject (with a reason) of one finding of the latest review.
fix     builds a fix request from the accepted findings, resumes the same executor (Claude -Resume; Codex: a new run with the
        previous report appended), then runs check, commit and review again.
accept  the coordinator's recorded acceptance: only for a valid PASS bound to the current head and tree; writes the review record
        through record-review.ps1 from values in the state. A later change to the head invalidates it (the plugin pin files excepted).

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
    [ValidateSet('new', 'run', 'check', 'show', 'commit', 'review', 'fix', 'disposition', 'accept')]
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
    [ValidateSet('codex', 'claude-sonnet', 'claude-haiku', 'same')]
    [string]$Executor = 'codex',
    [string]$Message,
    [ValidateSet('light', 'heavy')]
    [string]$Level,
    [ValidateSet('codex-sol', 'codex-luna', 'claude-opus', 'claude-sonnet')]
    [string]$Reviewer,
    [string]$Finding,
    [switch]$Accept,
    [switch]$Reject,
    [string]$Reason,
    [switch]$ResetBudget,
    [string]$CodexAskScript = (Join-Path $PSScriptRoot 'codex-ask.ps1'),
    [string]$CodexTestScript,
    [string]$RecordReviewScript = (Join-Path $PSScriptRoot 'record-review.ps1'),
    [string]$DiscoveryScript,
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
$script:RepairLimit = 3
$script:BriefOverride = $null
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
    # Packages created before commit, review and accept existed get the new fields empty; the schema version is unchanged.
    if (-not $state.ContainsKey('repair') -or $null -eq $state['repair']) { $state['repair'] = @{ limit = $script:RepairLimit; roundsUsed = 0; resets = @() } }
    foreach ($key in @('reviews', 'commits')) { if (-not $state.ContainsKey($key) -or $null -eq $state[$key]) { $state[$key] = @() } }
    foreach ($key in @('commit', 'acceptance', 'pendingFix')) { if (-not $state.ContainsKey($key)) { $state[$key] = $null } }
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
    $changed = @((Invoke-Git $Wt @('-c', 'core.quotepath=false', 'diff', '--no-ext-diff', '--no-textconv', '--name-only', '--diff-filter=d', $BaseSha)) -split "`n") +
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
    # The self-test never probes a real binary: a stub path has to pass -CodexVersion.
    if ($env:POINTFRAME_PKG_SELFTEST) { throw 'self-test guard: Get-CodexCliVersion would run the real codex --version; pass -CodexVersion' }
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
$script:ExecutorBound = $PSBoundParameters.ContainsKey('Executor')
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
    # While the self-test runs (it exports POINTFRAME_PKG_SELFTEST to its children) only a marked stub, or a script the self-test
    # wrote under its own temp root, may be started. Anything else would be a real claude.
    if (-not $env:POINTFRAME_PKG_SELFTEST) { return }
    if ($Launcher.Stub) { return }
    $selfRoot = [string]$env:POINTFRAME_PKG_SELFTEST_ROOT
    if ($selfRoot -and ([string]$Launcher.Display).IndexOf($selfRoot, [StringComparison]::OrdinalIgnoreCase) -ge 0) { return }
    throw "self-test guard: refusing to start '$($Launcher.Display)', which is not a marked self-test stub"
}

function Get-ClaudeCliVersion($Launcher)
{
    # The probe is a child like any other: inside a job, stdin closed, bounded by -ProbeSeconds, killed with its tree.
    Assert-SelfTestLauncher $Launcher
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
        'checks-passed' { @('commit', 'run', 'check') }
        'committed' { @('review -Level light|heavy') }
        'reviewing' { @('review (recovers an abandoned review first)') }
        'review-passed' { @('accept -Reason <text>', 'review -Level light|heavy') }
        'review-failed' { @('disposition -Finding <id> -Accept|-Reject -Reason <text>', 'fix', 'review -Level light|heavy') }
        'review-invalid' { @('review -Level light|heavy') }
        'needs-coordinator' { @('review -ResetBudget -Reason <text>', 'fix -ResetBudget -Reason <text>') }
        'accepted' { @() }
        'invalidated' { @('check') }
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
        commit = $null
        commits = @()
        reviews = @()
        repair = @{ limit = $script:RepairLimit; roundsUsed = 0; resets = @() }
        pendingFix = $null
        acceptance = $null
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
    if ($Executor -eq 'same') { throw "-Executor same is only meaningful for 'fix'; run needs codex, claude-sonnet or claude-haiku" }
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
    if ($env:POINTFRAME_PKG_SELFTEST -and $Executor -eq 'codex' -and -not (Test-SelfTestStub $AgentRunScript)) { throw 'self-test guard: a Codex executor run needs a marked -AgentRunScript stub' }
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
        effort = $(if ($isClaude) { $null } else { $Effort }); escalationReason = $(if ($EscalationReason) { $EscalationReason } else { $null }); maxMinutes = $MaxMinutes; maxInputTokens = $MaxInputTokens; permissions = $permissions; codexCliVersion = $cliVersion
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
    try { Invoke-CheckCore $info | Out-Null }
    finally { $stateLock.Dispose() }
}

function Invoke-CheckCore($info)
{
    # The package lock is held by the caller. Returns whether the check passed.
    try
    {
        Sync-Invalidation $info $Id
        $state = Read-State $info.StateRoot $Id
        Assert-StateConsistent $state $info
        if ([string]$state.stage -in @('creating', 'creation-failed', 'preflight-failed', 'running', 'executor-kill-failed', 'blocked-main-mutated', 'reviewing')) { throw "check is not allowed in stage '$($state.stage)'" }
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
        foreach ($item in $records) { Write-Host ("  {0,-6} {1}{2}" -f $(if ($item.skipped) { 'SKIP' } elseif ($item.exitCode -eq 0) { 'ok' } else { "exit $($item.exitCode)" }), $item.name, $(if (Get-Field $item 'modifiedWorktree') { ' (changed the worktree, so the result cannot be trusted)' } else { '' })) }
        Write-Host "check ${checkId}: $(if ($ok) { 'passed' } else { 'FAILED' }) on tree $tree"
        if (-not $ok) { $script:ExitCode = 1 }
        return $ok
    }
    finally { }
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
    $latestReview = Get-LatestReview $state
    $binding = $null
    if ($integrity.Count -eq 0 -and $latestReview -and $latestReview.status -eq 'valid' -and $latestReview.verdict -eq 'PASS') { $binding = Get-ReviewBinding $state $latestReview }
    $reviews = @(Get-List $state.reviews | ForEach-Object {
            [ordered]@{ reviewId = $_.reviewId; round = $_.round; level = $_.level; reviewer = $_.reviewer.kind; model = $_.reviewer.model; sessionId = $_.reviewer.sessionId; resumedFrom = $_.reviewer.resumedFrom; status = $_.status; verdict = $_.verdict
                commit = $_.commit; tree = $_.tree; blockingFindings = $_.blockingFindings; invalidReasons = @($_.invalidReasons); verdictFile = $_.verdictFile.path; termination = $_.termination
                findings = @(Get-List $_.findings | ForEach-Object { [ordered]@{ id = $_.id; severity = $_.severity; file = $_.file; line = $_.line; summary = $_.summary; disposition = $(if ($_.disposition) { "$($_.disposition.decision): $($_.disposition.reason)" } else { $null }) } }) }
        })
    $acceptance = $null
    if ($state.acceptance)
    {
        $acceptanceStatus = if ($state.acceptance.invalidated) { "invalidated: $($state.acceptance.invalidated.reason)" } elseif ($binding -and $binding.Current) { $(if ($binding.PinOnly) { 'valid (the head differs from the reviewed commit by the plugin pin files only)' } else { 'valid' }) } elseif ($binding) { "invalidated: $($binding.Reason)" } else { 'not verifiable' }
        $acceptance = [ordered]@{ utc = $state.acceptance.utc; reason = $state.acceptance.reason; reviewId = $state.acceptance.reviewId; commit = $state.acceptance.commit; status = $acceptanceStatus; recordPath = $state.acceptance.recordPath }
    }
    $view = [ordered]@{
        slug = $state.slug; packageId = $state.packageId; stage = $state.stage; worktree = $wt; branch = $state.branch; baseSha = $state.baseSha
        commit = $state.commit; repair = [ordered]@{ roundsUsed = $state.repair.roundsUsed; limit = $script:RepairLimit; resets = @(Get-List $state.repair.resets) }; reviews = $reviews; acceptance = $acceptance
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
    if ($state.commit) { Write-Host "  commit: $($state.commit.sha) tree $($state.commit.tree) ($($state.commit.message))" }
    Write-Host "  reviews: $($reviews.Count), repair rounds used $($view.repair.roundsUsed) of $script:RepairLimit$(if ($view.repair.resets.Count) { ", $($view.repair.resets.Count) reset(s)" })"
    foreach ($review in $reviews)
    {
        Write-Host "    round $($review.round) $($review.level) $($review.reviewer) $($review.status) $($review.verdict) blocking=$($review.blockingFindings) commit=$($review.commit)$(if ($review.invalidReasons.Count) { ' INVALID: ' + ($review.invalidReasons -join '; ') })"
        foreach ($f in $review.findings) { Write-Host "      $($f.id) [$($f.severity)] $($f.file):$($f.line) $($f.summary)$(if ($f.disposition) { " => $($f.disposition)" })" }
    }
    if ($acceptance) { Write-Host "  acceptance: $($acceptance.status) ($($acceptance.reason))" }
    Write-Host "  main checkout guard: $(if ($view.mainCheckoutGuard.mutated) { 'MUTATED' } else { 'clean' })"
    Write-Host "  next: $(if ($view.nextTransitions.Count) { $view.nextTransitions -join ', ' } else { '(none)' })"
}

# ---- commit, review, fix, accept: shared helpers -------------------------------------------------------------------

# The only commits that keep a review or an acceptance valid: net changes of these files alone (the same list merge-pr.ps1 knows).
$script:PinFiles = @('plugin/pointframe/server.lock.json', 'plugin/pointframe/.claude-plugin/plugin.json')
$script:ReviewerCatalog = @{
    'codex-sol' = @{ provider = 'codex'; model = 'gpt-6.1-sol'; effort = 'high'; heavy = $true }
    'codex-luna' = @{ provider = 'codex'; model = 'gpt-6-luna'; effort = 'medium'; heavy = $false }
    'claude-opus' = @{ provider = 'claude'; model = 'claude-opus-5-5'; effort = $null; heavy = $true }
    'claude-sonnet' = @{ provider = 'claude'; model = 'claude-sonnet-5-5'; effort = $null; heavy = $false }
}

function Get-List($Value) { @($Value | Where-Object { $null -ne $_ }) }

function Get-GitHead([string]$Wt) { Invoke-Git $Wt @('rev-parse', 'HEAD') }

function Get-GitTree([string]$Wt, [string]$Rev = 'HEAD') { Invoke-Git $Wt @('rev-parse', "$Rev^{tree}") }

function Test-WorktreeClean([string]$Wt)
{
    $status = @(& git -C $Wt status --porcelain --untracked-files=all 2>$null | Where-Object { $_ })
    if ($LASTEXITCODE -ne 0) { throw "git status failed in $Wt" }
    $status.Count -eq 0
}

function Get-LatestReview($State)
{
    $reviews = @(Get-List $State.reviews)
    if ($reviews.Count) { $reviews[-1] } else { $null }
}

function Get-CheckFreshnessProblem($State, [string]$CurrentContent)
{
    # $null when the latest check passed, is complete evidence, and ran on exactly this content.
    $checks = @(Get-List $State.checks)
    if ($checks.Count -eq 0) { return 'there is no check' }
    $latest = $checks[-1]
    if (-not $latest.ok) { return 'the latest check failed' }
    if ([string]$latest.treeHash -ne $CurrentContent) { return 'the latest check is stale: the worktree changed after it ran' }
    $snapshotProblems = @(Test-SnapshotsIntact $State)
    if ($snapshotProblems.Count) { return ($snapshotProblems -join '; ') }
    $evidence = @(Get-CheckEvidenceProblems $latest @(Get-CheckCommands (Read-Manifest $State.manifest.path)))
    if ($evidence.Count) { return "the latest check is not complete evidence: $($evidence -join '; ')" }
    $null
}

function Get-FileHashMap([string]$Dir)
{
    # Path -> SHA-256 of the raw bytes of every tracked and untracked non-ignored file; git only supplies the names.
    $listing = (& git -C $Dir ls-files -z --cached --others --exclude-standard 2>&1 | ForEach-Object { "$_" }) -join ''
    if ($LASTEXITCODE -ne 0) { throw "git ls-files failed in ${Dir}: $listing" }
    $map = @{}
    foreach ($name in @($listing -split "`0" | Where-Object { $_ } | Select-Object -Unique))
    {
        $full = [IO.Path]::Combine($Dir, $name)
        $map[($name -replace '\\', '/')] = if (Test-Path -LiteralPath $full -PathType Leaf)
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
    $map
}

function Get-NameStatusEntries([string]$Wt, [string]$From, [string]$To)
{
    # Without rename detection every entry has one path; an R or C entry would carry two and both are returned either way.
    $raw = (& git -C $Wt -c core.quotepath=false diff --no-renames --no-ext-diff --no-textconv --name-status -z $From $To 2>&1 | ForEach-Object { "$_" }) -join ''
    if ($LASTEXITCODE -ne 0) { throw "git diff failed in ${Wt}: $raw" }
    $tokens = @($raw -split "`0" | Where-Object { $_ })
    $entries = @()
    $i = 0
    while ($i -lt $tokens.Count)
    {
        $status = $tokens[$i]; $i++
        $count = if ($status -match '^[RC]') { 2 } else { 1 }
        $paths = @()
        for ($k = 0; $k -lt $count -and $i -lt $tokens.Count; $k++) { $paths += @($tokens[$i]); $i++ }
        $entries += @(@{ status = $status; paths = $paths })
    }
    $entries
}

function Test-PinChange([string]$Wt)
{
    # The changed pin files are validated before an approval is kept: both parse as JSON, server.lock.json has its schema and a
    # sha256 of the right shape, its version equals the one in plugin.json, and the repository's own offline discovery check
    # (the one merge-pr.ps1 runs for a pin-only change) passes on the package worktree. Returns the problem, or $null.
    $files = [ordered]@{ lock = (Join-Path $Wt 'plugin/pointframe/server.lock.json'); plugin = (Join-Path $Wt 'plugin/pointframe/.claude-plugin/plugin.json') }
    $docs = @{}
    foreach ($name in $files.Keys)
    {
        if (-not (Test-Path -LiteralPath $files[$name] -PathType Leaf)) { return "$($files[$name]) is missing" }
        try { $docs[$name] = Get-Content -LiteralPath $files[$name] -Raw | ConvertFrom-Json -AsHashtable -ErrorAction Stop }
        catch { return "$(Split-Path $files[$name] -Leaf) is not valid JSON: $($_.Exception.Message)" }
        if ($docs[$name] -isnot [hashtable]) { return "$(Split-Path $files[$name] -Leaf) is not a JSON object" }
    }
    $lock = $docs.lock
    $plugin = $docs.plugin
    if ((Get-Field $lock 'schemaVersion') -ne 1) { return 'server.lock.json has no schemaVersion 1' }
    $lockVersion = Get-Field $lock 'version'
    $pluginVersion = Get-Field $plugin 'version'
    if ($lockVersion -isnot [string] -or -not $lockVersion -or $pluginVersion -isnot [string] -or -not $pluginVersion) { return 'server.lock.json or plugin.json has no version' }
    if ($lockVersion -ne $pluginVersion) { return "the version in server.lock.json ($lockVersion) differs from the one in plugin.json ($pluginVersion)" }
    $checksum = Get-Field $lock 'sha256'
    if ($checksum -isnot [string] -or $checksum -notmatch '^[0-9a-f]{64}$') { return 'the sha256 in server.lock.json is not a 64-digit lowercase hex checksum' }
    $script = if ($DiscoveryScript) { $DiscoveryScript } else { Join-Path $Wt 'scripts/check-agent-discovery.ps1' }
    if ($env:POINTFRAME_PKG_SELFTEST -and -not (Test-SelfTestStub $script)) { return 'self-test guard: the discovery check has to be a marked stub' }
    if (-not (Test-Path -LiteralPath $script -PathType Leaf)) { return "the discovery check $script does not exist" }
    Push-Location -LiteralPath $Wt
    try { $output = @(& pwsh -NoProfile -NonInteractive -File $script 2>&1 | ForEach-Object { "$_" }); $exit = $LASTEXITCODE }
    finally { Pop-Location }
    if ($exit -ne 0) { return "check-agent-discovery.ps1 failed (exit $exit): $(@($output | Where-Object { $_ })[-1])" }
    $null
}
function Get-ReviewBinding($State, $Review)
{
    # Is this review (or the acceptance built on it) still about the branch as it is? Head, git tree and worktree content must be
    # the reviewed ones. The one exception is a clean descendant whose net change is the plugin pin files and nothing else: no
    # renames, only added or modified entries, every path of every entry a pin file, and a per-file re-hash of the whole worktree
    # against the reviewed one that differs in pin files only (so an edit hidden from git status cannot ride along).
    $wt = [string]$State.worktree
    try
    {
        $head = Get-GitHead $wt
        $tree = Get-GitTree $wt
        $clean = Test-WorktreeClean $wt
        $content = Get-TreeHash $wt
    }
    catch { return @{ Current = $false; PinOnly = $false; Reason = "the branch cannot be observed: $($_.Exception.Message)" } }
    if ($head -eq [string]$Review.commit -and $tree -eq [string]$Review.tree -and $content -eq [string]$Review.contentTree -and $clean) { return @{ Current = $true; PinOnly = $false; Reason = $null; Head = $head } }
    if (-not $clean) { return @{ Current = $false; PinOnly = $false; Reason = 'the worktree has uncommitted changes since the review' } }
    if ($head -eq [string]$Review.commit) { return @{ Current = $false; PinOnly = $false; Reason = 'the worktree content changed since the review' } }
    & git -C $wt merge-base --is-ancestor ([string]$Review.commit) $head 2>$null
    if ($LASTEXITCODE -ne 0) { return @{ Current = $false; PinOnly = $false; Reason = "the branch head changed ($($Review.commit) -> $head) and is not a descendant of the reviewed commit" } }
    try { $entries = @(Get-NameStatusEntries $wt ([string]$Review.commit) $head) }
    catch { return @{ Current = $false; PinOnly = $false; Reason = "the change since the review cannot be listed: $($_.Exception.Message)" } }
    $allPaths = @($entries | ForEach-Object { $_.paths } | ForEach-Object { $_ })
    $outside = @($allPaths | Where-Object { $_ -notin $script:PinFiles })
    $badStatus = @($entries | Where-Object { $_.status -notin @('M', 'A') })
    if ($entries.Count -eq 0 -or $outside.Count -or $badStatus.Count)
    {
        $shown = @($entries | Select-Object -First 5 | ForEach-Object { "$($_.status) $($_.paths -join '>')" }) -join ', '
        return @{ Current = $false; PinOnly = $false; Reason = "the branch head changed ($($Review.commit) -> $head)$(if ($shown) { ': ' + $shown })" }
    }
    $fileHashes = Get-Field $Review 'fileHashes'
    if (-not $fileHashes -or -not $fileHashes.path -or -not (Test-Path -LiteralPath $fileHashes.path -PathType Leaf) -or (Get-Sha256 $fileHashes.path) -ne [string]$fileHashes.sha256)
    { return @{ Current = $false; PinOnly = $false; Reason = 'the reviewed per-file fingerprint is missing or changed, so a plugin-pin-only change cannot be proved' } }
    $before = Get-Content -LiteralPath $fileHashes.path -Raw | ConvertFrom-Json -AsHashtable
    try { $now = Get-FileHashMap $wt } catch { return @{ Current = $false; PinOnly = $false; Reason = "the worktree cannot be re-hashed: $($_.Exception.Message)" } }
    $differing = @(@($before.Keys) + @($now.Keys) | Select-Object -Unique | Where-Object { $before[$_] -ne $now[$_] })
    $differingOutside = @($differing | Where-Object { $_ -notin $script:PinFiles })
    if ($differingOutside.Count) { return @{ Current = $false; PinOnly = $false; Reason = "file content differs from the reviewed content outside the plugin pin files: $(($differingOutside | Select-Object -First 5) -join ', ')" } }
    $pinProblem = Test-PinChange $wt
    if ($pinProblem) { return @{ Current = $false; PinOnly = $false; Reason = "the plugin pin change does not validate: $pinProblem" } }
    @{ Current = $true; PinOnly = $true; Reason = $null; Head = $head }
}
function Sync-Invalidation($Info, [string]$Slug)
{
    # The package lock is held by the caller. A passed review or an acceptance whose binding to the branch is gone is invalidated.
    $state = Read-State $Info.StateRoot $Slug
    if ([string]$state.stage -notin @('review-passed', 'accepted')) { return }
    $review = Get-LatestReview $state
    if (-not $review -or $review.status -ne 'valid' -or $review.verdict -ne 'PASS') { return }
    $binding = Get-ReviewBinding $state $review
    if ($binding.Current) { return }
    $why = [string]$binding.Reason
    Update-State $Info.StateRoot $Slug 'invalidated' {
        param($s)
        if ($s.acceptance) { $s.acceptance.invalidated = @{ utc = (Get-Now); reason = $why } }
        $s.stage = 'invalidated'
    } -Locked | Out-Null
    Write-Host "NOTE the earlier PASS$(if ($state.stage -eq 'accepted') { ' and the acceptance' }) no longer apply: $why"
}

function Reset-RepairBudget($Info, [string]$Slug, [string]$Why)
{
    # Lock held. The only way the persisted count goes down; recorded in the state and in the history.
    $state = Read-State $Info.StateRoot $Slug
    $previous = [int]$state.repair.roundsUsed
    $latest = Get-LatestReview $state
    $stage = [string]$state.stage
    Update-State $Info.StateRoot $Slug 'budget-reset' {
        param($s)
        $s.repair.resets = @(Get-List $s.repair.resets) + @(@{ utc = (Get-Now); reason = $Why; roundsUsedBefore = $previous })
        $s.repair.roundsUsed = 0
        if ($s.stage -eq 'needs-coordinator') { $s.stage = if (-not $latest) { 'committed' } elseif ($latest.status -eq 'valid') { 'review-failed' } else { 'review-invalid' } }
    } -Locked | Out-Null
    Write-Host "repair budget reset (was $previous of $script:RepairLimit): $Why"
}

# ---- commit -----------------------------------------------------------------------------------------------------

function Invoke-Commit
{
    $info = Get-RepoInfo $Repo
    $stateLock = Enter-StateLock $info.StateRoot $Id
    try
    {
        Sync-Invalidation $info $Id
        $state = Read-State $info.StateRoot $Id
        Assert-StateConsistent $state $info
        $problems = @(Test-SnapshotsIntact $state)
        if ($problems.Count) { throw ($problems -join '; ') }
        if ([string]$state.stage -ne 'checks-passed') { throw "commit is not allowed in stage '$($state.stage)': it needs 'checks-passed' (run check first)" }
        if ([bool]$state.guard.mainCheckoutMutated) { throw 'the main-checkout guard is not clean; nothing is committed' }
        $wt = [string]$state.worktree
        $content = Get-TreeHash $wt
        $stale = Get-CheckFreshnessProblem $state $content
        if ($stale) { throw "commit needs a fresh passing check for the current tree: $stale" }
        $name = ([string](& git -C $wt config user.name)).Trim()
        $email = ([string](& git -C $wt config user.email)).Trim()
        if (-not $name -or -not $email) { throw 'git user.name and user.email are not configured: the commit author is the configured git user' }
        $runs = @(Get-List $state.runs)
        if ($runs.Count -eq 0) { throw 'no executor run is recorded for this package' }
        # Every run since the base that could have touched the worktree is an executor of what is committed: failed, timed-out
        # and mixed runs included. Only a run that never started is left out.
        $executorRuns = @($runs | Where-Object { (Get-Field $_ 'termination') -ne 'launch-failed' })
        if ($executorRuns.Count -eq 0) { throw 'no executor run that started is recorded for this package' }
        $executorSet = @($executorRuns | ForEach-Object { @{ runId = [string]$_.runId; provider = [string]$_.provider; model = [string]$_.model; executorKind = [string](Get-Field $_ 'executorKind'); outcome = [string]$_.outcome } })
        $runIds = @($executorSet | ForEach-Object { $_.runId }) -join ' '
        $dirty = -not (Test-WorktreeClean $wt)
        $ahead = [int](Invoke-Git $wt @('rev-list', '--count', "$($state.baseSha)..HEAD"))
        if (-not $dirty -and $ahead -eq 0) { throw 'nothing to commit: the worktree is clean and the branch has no commit beyond its base' }
        $summary = if ($Message -and $Message.Trim()) { $Message.Trim() } else { "Package $($state.slug)" }
        if ($dirty)
        {
            $paths = Get-PackagePaths $info.StateRoot $Id
            $messageFile = Join-Path $paths.Dir 'commit-message.txt'
            [IO.File]::WriteAllText($messageFile, "$summary`n`nPackage: $($state.slug) run $runIds`n", [Text.UTF8Encoding]::new($false))
            Invoke-Git $wt @('add', '-A') | Out-Null
            Invoke-Git $wt @('commit', '-F', $messageFile) | Out-Null
        }
        $remote = if ([string]$state.baseRef -like '*/*') { ([string]$state.baseRef).Split('/')[0] } else { $null }
        if ($remote) { Invoke-Git $wt @('fetch', $remote) | Out-Null }
        $preTree = Get-GitTree $wt
        $baseNow = Invoke-Git $wt @('rev-parse', '--verify', "$($state.baseRef)^{commit}")
        $rebaseOutput = @(& git -C $wt rebase ([string]$state.baseRef) 2>&1 | ForEach-Object { "$_" })
        if ($LASTEXITCODE -ne 0)
        {
            $abortOutput = @(& git -C $wt rebase --abort 2>&1 | ForEach-Object { "$_" })
            $abortExit = $LASTEXITCODE
            $abortNote = if ($abortExit -eq 0) { 'the rebase was aborted; the commit stays on the branch and nothing else changed' } else { "rebase --abort FAILED (exit ${abortExit}: $(($abortOutput | Select-Object -Last 2) -join ' ')); the worktree may still be in the middle of a rebase and needs the coordinator" }
            Update-State $info.StateRoot $Id 'commit-rebase-failed' { param($s) $s.lastError = "rebase onto $($state.baseRef) failed; abort exit $abortExit" } -Locked | Out-Null
            throw "rebase onto $($state.baseRef) failed ($abortNote): $(($rebaseOutput | Select-Object -Last 3) -join ' ')"
        }
        $sha = Get-GitHead $wt
        $tree = Get-GitTree $wt
        $treeChanged = $tree -ne $preTree
        $contentNow = Get-TreeHash $wt
        $entry = @{ sha = $sha; tree = $tree; contentTree = $contentNow; utc = (Get-Now); message = $summary; runIds = @($executorSet | ForEach-Object { $_.runId }); executors = $executorSet; baseSha = $baseNow; rebaseChangedTree = $treeChanged; checkedAfterRebase = (-not $treeChanged); author = "$name <$email>" }
        Update-State $info.StateRoot $Id 'committed' {
            param($s)
            $s.commit = $entry
            $s.commits = @(Get-List $s.commits) + @($entry)
            $s.baseSha = $baseNow
            $s.stage = 'committed'
        } -Locked | Out-Null
        Write-Host "committed ${sha} on $($state.branch) (tree $tree, base $baseNow)$(if ($treeChanged) { ' - the rebase changed the tree' })"
        if ($treeChanged)
        {
            Write-Host 'the rebase changed the tree: re-running check'
            $ok = Invoke-CheckCore $info
            if (-not $ok) { Write-Host 'ERROR the check after the rebase failed; the stage is checks-failed'; return }
            if (-not (Test-WorktreeClean $wt)) { Write-Host 'NOTE the check changed files (format or CRLF); the stage is checks-passed: run commit again to commit them'; return }
            $afterContent = Get-TreeHash $wt
            Update-State $info.StateRoot $Id 'committed-after-recheck' {
                param($s)
                $s.commit.checkedAfterRebase = $true
                $s.commit.contentTree = $afterContent
                $s.commits[-1] = $s.commit
                $s.stage = 'committed'
            } -Locked | Out-Null
        }
    }
    finally { $stateLock.Dispose() }
}

# ---- review: criteria, prompt, verdict --------------------------------------------------------------------------

function Get-ReviewCriteria([string]$BriefPath)
{
    # The brief's "Acceptance cases" list, one criterion per bullet or number, numbered C1... in order.
    $items = [Collections.Generic.List[string]]::new()
    $inSection = $false
    $current = $null
    foreach ($line in (Get-Content -LiteralPath $BriefPath))
    {
        if ($line -match '^\s{0,3}#{1,6}\s+(.*?)\s*$')
        {
            if ($inSection) { break }
            if ($Matches[1] -match '^(?i)acceptance(\s+(cases|criteria))?\b') { $inSection = $true }
            continue
        }
        if (-not $inSection) { continue }
        if ($line -match '^\s{0,3}(?:[-*+]|\d+[.)])\s+(.*\S)\s*$')
        {
            if ($current) { $items.Add($current) }
            $current = $Matches[1]
        }
        elseif ($current -and $line -match '^\s+\S') { $current += ' ' + $line.Trim() }
    }
    if ($current) { $items.Add($current) }
    if ($items.Count -eq 0) { throw 'the brief has no "Acceptance cases" list: a review needs criteria to be checked against' }
    $criteria = @()
    for ($i = 0; $i -lt $items.Count; $i++) { $criteria += @(@{ id = "C$($i + 1)"; text = $items[$i] }) }
    $criteria
}

function Get-ExecutorIdentity($State)
{
    # The executors of the reviewed content: every run recorded at commit time (any run since the base that started, whatever its
    # outcome). The latest of them names the executor in the review record.
    $recorded = if ($State.commit) { @(Get-List (Get-Field $State.commit 'executors')) } else { @() }
    $set = @(if ($recorded.Count) { $recorded } else { @(Get-List $State.runs | Where-Object { (Get-Field $_ 'termination') -ne 'launch-failed' } | ForEach-Object { @{ runId = [string]$_.runId; provider = [string]$_.provider; model = [string]$_.model; executorKind = [string](Get-Field $_ 'executorKind'); outcome = [string]$_.outcome } }) })
    if ($set.Count -eq 0) { throw 'no executor run is recorded for this package' }
    $latest = $set[-1]
    $run = @(Get-List $State.runs | Where-Object { $_.runId -eq $latest.runId })[0]
    @{ run = $run; provider = [string]$latest.provider; model = [string]$latest.model; kind = [string]$latest.executorKind; session = $(if ($run) { $run.sessionId } else { $null }); set = $set; models = @($set | ForEach-Object { [string]$_.model } | Select-Object -Unique) }
}

function Test-ReviewerConflict($Entry, $ExecutorInfo)
{
    # $null when the reviewer differs from every model of the executor set, Claude family rule included.
    foreach ($executor in $ExecutorInfo.set)
    {
        if ($Entry.model -ieq [string]$executor.model) { return "it is the model of executor run $($executor.runId) ($($executor.model))" }
        if ($Entry.provider -eq 'claude' -and [string]$executor.provider -eq 'claude' -and (Get-ModelFamily $Entry.model) -eq (Get-ModelFamily ([string]$executor.model))) { return "it is the same Claude family as executor run $($executor.runId) ($($executor.model))" }
    }
    $null
}

function Resolve-ReviewerChoice($ExecutorInfo, [string]$LevelName, [string]$Requested)
{
    # Heavy needs a heavy reviewer (gpt-6.1-sol at high effort, or claude-opus), also when one is requested. The default prefers a
    # provider none of the executors used. The reviewer must differ from every executor model.
    $order = if ($LevelName -eq 'heavy') { @('codex-sol', 'claude-opus') } else { @('claude-sonnet', 'codex-luna', 'claude-opus', 'codex-sol') }
    $kind = $Requested
    if ($kind)
    {
        if ($LevelName -eq 'heavy' -and -not $script:ReviewerCatalog[$kind].heavy) { throw "refused: -Level heavy needs a heavy reviewer (codex-sol at high effort, or claude-opus), not $kind" }
        $conflict = Test-ReviewerConflict $script:ReviewerCatalog[$kind] $ExecutorInfo
        if ($conflict) { throw "refused: the reviewer $kind ($($script:ReviewerCatalog[$kind].model)) cannot review this package: $conflict; a reviewer must differ from every executor model" }
    }
    else
    {
        $executorProviders = @($ExecutorInfo.set | ForEach-Object { [string]$_.provider })
        $ranked = @($order | Where-Object { $script:ReviewerCatalog[$_].provider -notin $executorProviders }) + @($order | Where-Object { $script:ReviewerCatalog[$_].provider -in $executorProviders })
        $kind = @($ranked | Where-Object { -not (Test-ReviewerConflict $script:ReviewerCatalog[$_] $ExecutorInfo) })[0]
        if (-not $kind) { throw "refused: no $LevelName reviewer differs from every executor model ($($ExecutorInfo.models -join ', ')); name one with -Reviewer or reassess" }
    }
    $entry = $script:ReviewerCatalog[$kind]
    @{ kind = $kind; provider = $entry.provider; model = $entry.model; effort = $entry.effort }
}
function New-ReviewPrompt($State, $Review, [string]$VerdictPath, $Prior)
{
    $template = Join-Path $PSScriptRoot 'package-review-prompt.txt'
    $schema = Join-Path $PSScriptRoot 'package-verdict.schema.json'
    if (-not (Test-Path -LiteralPath $template -PathType Leaf) -or -not (Test-Path -LiteralPath $schema -PathType Leaf)) { throw 'the review prompt template or the verdict schema is missing from the script folder' }
    $note = ''
    if ($Prior)
    {
        $lines = @("This is round $($Review.round) of a review you started earlier in this session. The branch head may have changed since your last verdict. Inspect the commit above afresh and return a COMPLETE, CURRENT verdict: every criterion with its result and evidence, every finding that still applies (and none that no longer does), and your omissions. Do not answer with only what changed since the last round.")
        foreach ($old in (Get-List $Prior.findings))
        {
            $disposition = $old.disposition
            $lines += "Earlier finding $($old.id) ($($old.severity), $($old.file):$($old.line)): $($old.summary)$(if ($disposition) { " - the coordinator decided: $($disposition.decision) ($($disposition.reason))" } else { '' })"
        }
        $note = $lines -join "`n"
    }
    $text = [IO.File]::ReadAllText($template)
    $values = [ordered]@{
        SLUG = [string]$State.slug; LEVEL = [string]$Review.level; ROUND = [string]$Review.round; WORKTREE = [string]$Review.snapshot.path; BRIEF = (Join-Path ([string]$Review.snapshot.inputDir) 'brief.md'); INPUT_DIR = [string]$Review.snapshot.inputDir
        BASE = [string]$Review.baseSha; COMMIT = [string]$Review.commit; TREE = [string]$Review.tree; VERDICT_FILE = $VerdictPath; RESUME_NOTE = $note
        CRITERIA = ((Get-List $Review.criteria | ForEach-Object { "$($_.id): $($_.text)" }) -join "`n"); SCHEMA = ([IO.File]::ReadAllText($schema).Trim())
    }
    foreach ($key in $values.Keys) { $text = $text.Replace("{{$key}}", [string]$values[$key]) }
    $text
}

function Get-VerdictJsonText([string]$FinalText)
{
    # The verdict is the whole final message. One enclosing code fence is tolerated; nothing else is searched for or stripped.
    $text = $FinalText.Trim()
    if ($text -match '(?s)^```(?:json)?[ \t]*\r?\n(.*?)\r?\n```$') { $text = $Matches[1].Trim() }
    $text
}

function Find-DuplicateJsonProperty([string]$Text)
{
    # ConvertFrom-Json keeps only the last of two equal property names, so a contradiction would vanish. A strict tokenizer
    # (Utf8JsonReader: comments and trailing commas are errors) walks the raw text first. It returns "dup:<name>" for a property
    # repeated within one object at any depth, "error:<message>" for anything it cannot read as strict JSON, and $null for a clean
    # document. An error is never "no duplicate".
    if (-not ('PointframeJsonDuplicates' -as [type]))
    {
        Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
public static class PointframeJsonDuplicates
{
    public static string Find(string json)
    {
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json), new JsonReaderOptions { CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false });
        var stack = new Stack<HashSet<string>>();
        try
        {
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject: stack.Push(new HashSet<string>(StringComparer.Ordinal)); break;
                    case JsonTokenType.StartArray: stack.Push(null); break;
                    case JsonTokenType.EndObject: case JsonTokenType.EndArray: stack.Pop(); break;
                    case JsonTokenType.PropertyName:
                        var name = reader.GetString();
                        if (!stack.Peek().Add(name)) { return "dup:" + name; }
                        break;
                }
            }
        }
        catch (Exception e) { return "error:" + e.Message; }
        return null;
    }
}
"@
    }
    [PointframeJsonDuplicates]::Find($Text)
}
function Test-VerdictDocument([string]$Text, $Criteria, [string]$Commit, [string]$Tree, [bool]$CheckFresh)
{
    # Schema (scripts/package-verdict.schema.json) plus the checks the host never takes from the model: the reviewed commit and
    # tree equal what the host observed, every criterion of the brief is present once, nothing contradicts itself, and PASS
    # means every criterion pass, no blocking finding and a fresh check.
    $problems = [Collections.Generic.List[string]]::new()
    $result = @{ ok = $false; verdict = $null; problems = $problems; criteria = @(); findings = @(); omissions = @(); blocking = 0 }
    if ([string]::IsNullOrWhiteSpace($Text)) { $problems.Add('the verdict is empty'); return $result }
    $scan = Find-DuplicateJsonProperty $Text
    if ($scan -like 'error:*') { $problems.Add("the verdict is not a JSON document (missing, truncated, free text, or not strict JSON: comments and trailing commas are refused): $($scan.Substring(6))"); return $result }
    if ($scan -like 'dup:*') { $problems.Add("the verdict repeats the property '$($scan.Substring(4))' (the last value would silently win)"); return $result }
    try { $doc = $Text | ConvertFrom-Json -AsHashtable @script:JsonDate -ErrorAction Stop }
    catch { $problems.Add("the verdict is not a JSON document (missing, truncated or free text): $($_.Exception.Message)"); return $result }
    if ($doc -isnot [hashtable]) { $problems.Add('the verdict is not a JSON object'); return $result }
    $allowedKeys = @('schemaVersion', 'reviewedCommit', 'reviewedTree', 'verdict', 'criteria', 'findings', 'omissions')
    # One optional $schema, exactly the draft URL of the schema file, is tolerated because reviewers copy it from there.
    foreach ($key in $doc.Keys) { if ($key -notin $allowedKeys -and $key -cne '$schema') { $problems.Add("unknown top-level key '$key'") } }
    if ($doc.ContainsKey('$schema') -and ($doc['$schema'] -isnot [string] -or $doc['$schema'] -cne 'https://json-schema.org/draft/2020-12/schema')) { $problems.Add('the $schema value, when present, must be exactly https://json-schema.org/draft/2020-12/schema') }
    foreach ($key in $allowedKeys) { if (-not $doc.ContainsKey($key)) { $problems.Add("missing top-level key '$key'") } }
    if ($problems.Count) { return $result }
    $isInt = { param($v) $v -is [int] -or $v -is [long] }
    $isText = { param($v) $v -is [string] -and -not [string]::IsNullOrWhiteSpace($v) }
    if (-not (& $isInt $doc.schemaVersion) -or $doc.schemaVersion -ne 1) { $problems.Add('schemaVersion must be the integer 1') }
    foreach ($key in @('reviewedCommit', 'reviewedTree')) { if ($doc[$key] -isnot [string] -or $doc[$key] -notmatch '^[0-9a-fA-F]{40,64}$') { $problems.Add("$key must be a full object id") } }
    if ($doc.verdict -isnot [string] -or $doc.verdict -cnotin @('PASS', 'FAIL')) { $problems.Add('verdict must be PASS or FAIL') }
    foreach ($key in @('criteria', 'findings', 'omissions')) { if ($doc[$key] -isnot [array]) { $problems.Add("$key must be an array") } }
    if ($problems.Count) { return $result }

    $criteriaOut = @()
    $seen = @{}
    $index = 0
    foreach ($item in $doc.criteria)
    {
        $index++
        if ($item -isnot [hashtable]) { $problems.Add("criteria[$index] is not an object"); continue }
        $extraKeys = @($item.Keys | Where-Object { $_ -notin @('id', 'result', 'evidence') })
        $missingKeys = @(@('id', 'result', 'evidence') | Where-Object { -not $item.ContainsKey($_) })
        if ($extraKeys.Count -or $missingKeys.Count) { $problems.Add("criteria[$index] must have exactly id, result and evidence (extra: $($extraKeys -join ','); missing: $($missingKeys -join ','))"); continue }
        if (-not (& $isText $item.id)) { $problems.Add("criteria[$index].id must be a non-empty string"); continue }
        if ($item.result -isnot [string] -or $item.result -cnotin @('pass', 'fail', 'not-checked')) { $problems.Add("criterion $($item.id): result must be pass, fail or not-checked") }
        if (-not (& $isText $item.evidence)) { $problems.Add("criterion $($item.id): evidence must be a non-empty string") }
        if ($seen.ContainsKey($item.id)) { $problems.Add("criterion $($item.id) appears twice") }
        $seen[$item.id] = $true
        $criteriaOut += @(@{ id = $item.id; result = $item.result; evidence = $item.evidence })
    }
    foreach ($expected in $Criteria) { if (-not $seen.ContainsKey($expected.id)) { $problems.Add("criterion $($expected.id) of the brief is missing from the verdict") } }
    foreach ($id in $seen.Keys) { if (-not @($Criteria | Where-Object { $_.id -eq $id })) { $problems.Add("the verdict names criterion $id, which the brief does not have") } }

    $findingsOut = @()
    $seenFindings = @{}
    $index = 0
    foreach ($item in $doc.findings)
    {
        $index++
        if ($item -isnot [hashtable]) { $problems.Add("findings[$index] is not an object"); continue }
        $required = @('id', 'severity', 'file', 'line', 'summary', 'scenario')
        $extraKeys = @($item.Keys | Where-Object { $_ -notin $required })
        $missingKeys = @($required | Where-Object { -not $item.ContainsKey($_) })
        if ($extraKeys.Count -or $missingKeys.Count) { $problems.Add("findings[$index] must have exactly id, severity, file, line, summary and scenario (extra: $($extraKeys -join ','); missing: $($missingKeys -join ','))"); continue }
        foreach ($key in @('id', 'file', 'summary', 'scenario')) { if (-not (& $isText $item[$key])) { $problems.Add("findings[$index].$key must be a non-empty string") } }
        if ($item.severity -isnot [string] -or $item.severity -cnotin @('blocking', 'non-blocking')) { $problems.Add("findings[$index].severity must be blocking or non-blocking") }
        if (-not (& $isInt $item.line) -or $item.line -lt 0) { $problems.Add("findings[$index].line must be an integer >= 0") }
        if ($item.id -is [string] -and $seenFindings.ContainsKey($item.id)) { $problems.Add("finding $($item.id) appears twice") }
        if ($item.id -is [string]) { $seenFindings[$item.id] = $true }
        $findingsOut += @(@{ id = $item.id; severity = $item.severity; file = $item.file; line = $item.line; summary = $item.summary; scenario = $item.scenario; disposition = $null })
    }
    $omissionsOut = @()
    foreach ($item in $doc.omissions)
    {
        if ($item -isnot [string]) { $problems.Add('omissions must be strings'); break }
        $omissionsOut += @($item)
    }
    if ($problems.Count) { return $result }

    if ($doc.reviewedCommit -ine $Commit) { $problems.Add("reviewedCommit $($doc.reviewedCommit) is not the host-observed head $Commit") }
    if ($doc.reviewedTree -ine $Tree) { $problems.Add("reviewedTree $($doc.reviewedTree) is not the host-observed tree $Tree") }
    $blocking = @($findingsOut | Where-Object { $_.severity -eq 'blocking' }).Count
    $notPass = @($criteriaOut | Where-Object { $_.result -ne 'pass' })
    if ($doc.verdict -ceq 'PASS')
    {
        if ($notPass.Count) { $problems.Add("contradiction: verdict PASS but criteria $((($notPass | ForEach-Object { "$($_.id)=$($_.result)" }) -join ', '))") }
        if ($blocking) { $problems.Add("contradiction: verdict PASS with $blocking blocking finding(s)") }
        if (-not $CheckFresh) { $problems.Add('PASS needs a fresh passing check for the reviewed tree and there is none') }
    }
    elseif (@($criteriaOut | Where-Object { $_.result -ne 'pass' }).Count -eq 0 -and $blocking -eq 0) { $problems.Add('contradiction: verdict FAIL but every criterion passes and no finding is blocking') }
    $result.criteria = $criteriaOut; $result.findings = $findingsOut; $result.omissions = $omissionsOut; $result.blocking = $blocking
    if ($problems.Count -eq 0) { $result.ok = $true; $result.verdict = [string]$doc.verdict }
    $result
}

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
        "You only read, with Read, Glob and Grep. You have no shell. The host prepared the review inputs in $InputDir (the commit log, the stat, the full diff and the brief).",
        'If a tool call is denied, do not look for a way around it: list what you could not inspect under omissions.',
        'Never mark a criterion pass that you did not verify; use not-checked and say why in the evidence.',
        'Your final message is the verdict document and nothing else: exactly the JSON object the prompt describes, no prose before or after it.'
    ) | Join-String -Separator ' '
}
function Invoke-ClaudeReviewProcess([string]$Wt, [string]$ReviewDir, [string]$PromptPath, $Chosen, [string]$ResumeSession, [string]$InputDir)
{
    $launcher = Resolve-ClaudeLauncher
    $version = Get-ClaudeCliVersion $launcher
    $settingsPath = Join-Path $ReviewDir 'claude-settings.json'
    $mcpPath = Join-Path $ReviewDir 'empty-mcp.json'
    New-ClaudeReviewerSettings $settingsPath
    [IO.File]::WriteAllText($mcpPath, "{`"mcpServers`":{}}`r`n", [Text.UTF8Encoding]::new($false))
    $rules = Get-ClaudeReviewerRules $Wt $InputDir
    $arguments = @('-p', '--model', $Chosen.model, '--output-format', 'stream-json', '--verbose', '--settings', $settingsPath, '--setting-sources', '',
        '--tools', 'Read,Glob,Grep', '--add-dir', $InputDir, '--strict-mcp-config', '--mcp-config', $mcpPath, '--append-system-prompt', $rules)
    if ($MaxBudgetUsd -gt 0) { $arguments += @('--max-budget-usd', $MaxBudgetUsd.ToString([Globalization.CultureInfo]::InvariantCulture)) }
    if ($ResumeSession) { $arguments += @('--resume', $ResumeSession) }
    Assert-NoBypass $arguments
    $events = Join-Path $ReviewDir 'events.jsonl'
    Initialize-ProcessJob
    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    $job = [PointframeProcessJob]::Create()
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
    [PointframeProcessJob]::Close($job)
    $exitCode = if ($termination -ne 'exit' -or -not (Test-ProcessExited $process)) { $null } else { Get-ProcessExitCode $process }
    [PointframeProcessJob]::CloseProcess($process.Handle)
    Read-StreamMonitor $monitor -Final
    $tokens = Get-ClaudeTokenRecord $monitor
    if ($termination -eq 'exit' -and [Math]::Max((Get-MonitorEnforcedInput $monitor), [long]$tokens.input) -gt $MaxInputTokens) { $termination = 'budget' }
    $identityProblem = $null
    if (-not $monitor.SessionId) { $identityProblem = 'the stream has no init event with a session id' }
    elseif ($ResumeSession -and $monitor.SessionId -ne $ResumeSession) { $identityProblem = "the resumed session answered with id $($monitor.SessionId), not $ResumeSession" }
    @{
        Termination = $termination; ExitCode = $exitCode; ContainmentVerified = [bool]$containment.Verified; ContainmentReason = $containment.Reason
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
    if ($env:POINTFRAME_PKG_SELFTEST -and -not $CodexTestScript) { throw 'self-test guard: a Codex reviewer needs a marked -CodexTestScript stub' }
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
    @{
        Termination = $termination; ExitCode = $(if ($result) { $result.exitCode } else { $exit }); ContainmentVerified = [bool]($result -and $result.containmentVerified); ContainmentReason = $(if ($result) { $result.containmentReason } else { "codex-ask.ps1 exit $exit without a result file: $(($output | Select-Object -Last 2) -join ' ')" })
        SessionId = $(if ($result -and $result.sessionId) { [string]$result.sessionId } else { $null }); IdentityProblem = $null
        FinalText = $(if (Test-Path -LiteralPath $final -PathType Leaf) { [IO.File]::ReadAllText($final) } else { $null }); ResultError = $false
        Denials = @(); Tokens = $tokens; Seconds = $(if ($result) { $result.elapsedSeconds } else { $null }); CliVersion = $null
        Permissions = @{ sandbox = 'read-only'; approval = 'never' }; Executable = "codex-ask.ps1 -Sandbox read-only"
    }
}

# ---- review ---------------------------------------------------------------------------------------------------------

function Save-GitOutput([string]$Wt, [string[]]$GitArgs, [string]$Path)
{
    $lines = @(& git -C $Wt @GitArgs 2>$null)
    if ($LASTEXITCODE -ne 0) { throw "git $($GitArgs -join ' ') failed in $Wt" }
    [IO.File]::WriteAllText($Path, ((($lines | ForEach-Object { "$_" }) -join "`n") + "`n"), [Text.UTF8Encoding]::new($false))
}

function Set-TreeReadOnly([string]$Dir, [bool]$ReadOnly)
{
    $marker = Join-Path $Dir '.git'
    foreach ($file in @(Get-ChildItem -LiteralPath $Dir -Recurse -File -Force -ErrorAction SilentlyContinue))
    {
        if ($file.FullName -eq $marker -and $ReadOnly) { continue }
        if ($file.IsReadOnly -ne $ReadOnly) { $file.IsReadOnly = $ReadOnly }
    }
}

function Get-SnapshotObservation([string]$Snap)
{
    @{ head = (Get-GitHead $Snap); tree = (Get-GitTree $Snap); clean = (Test-WorktreeClean $Snap); contentTree = $(try { Get-TreeHash $Snap } catch { $null }) }
}

function New-ReviewSnapshot($State, $Record)
{
    # The reviewer reads an immutable detached worktree at exactly the reviewed commit, never the package worktree. Its files are
    # read-only and the host observes its tree and content at the start and the end. The host also prepares the review inputs
    # (commit log, stat, full diff without external diff or textconv programs, the brief) so a reviewer without a shell has them.
    $wt = [string]$State.worktree
    $snap = [string]$Record.snapshot.path
    $inputs = [string]$Record.snapshot.inputDir
    $commit = [string]$Record.commit
    Invoke-Git $wt @('worktree', 'add', '--detach', $snap, $commit) | Out-Null
    $observed = Get-SnapshotObservation $snap
    if ($observed.head -ne $commit -or $observed.tree -ne [string]$Record.tree -or -not $observed.clean -or -not $observed.contentTree) { throw "the review snapshot does not match the reviewed commit (head $($observed.head), tree $($observed.tree), clean $($observed.clean))" }
    Set-TreeReadOnly $snap $true
    New-Item -ItemType Directory -Path $inputs -Force | Out-Null
    $base = [string]$Record.baseSha
    Save-GitOutput $wt @('log', '--no-ext-diff', '--no-textconv', '--no-color', '--format=fuller', "$base..$commit") (Join-Path $inputs 'log.txt')
    Save-GitOutput $wt @('show', '--stat', '--no-ext-diff', '--no-textconv', '--no-color', $commit) (Join-Path $inputs 'stat.txt')
    Save-GitOutput $wt @('diff', '--no-ext-diff', '--no-textconv', '--no-color', $base, $commit) (Join-Path $inputs 'diff.patch')
    Copy-Item -LiteralPath ([string]$State.brief.path) -Destination (Join-Path $inputs 'brief.md') -Force
    Set-TreeReadOnly $inputs $true
    $observed
}

function Close-ReviewSnapshot([string]$Wt, [string]$Snap)
{
    # Observes the snapshot once more, then removes it; both outcomes are returned for the record.
    $result = @{ endObservation = $null; removed = $false; removeError = $null }
    if (-not (Test-Path -LiteralPath $Snap)) { $result.removeError = 'the snapshot does not exist'; return $result }
    try { $result.endObservation = Get-SnapshotObservation $Snap } catch { $result.removeError = "the snapshot could not be observed: $($_.Exception.Message)" }
    try { Set-TreeReadOnly $Snap $false; Invoke-Git $Wt @('worktree', 'remove', '--force', $Snap) | Out-Null }
    catch
    {
        $result.removeError = "git worktree remove failed: $($_.Exception.Message)"
        try { Remove-Item -LiteralPath $Snap -Recurse -Force -ErrorAction Stop; & git -C $Wt worktree prune 2>&1 | Out-Null } catch { $result.removeError += "; deleting the directory failed: $($_.Exception.Message)" }
    }
    $result.removed = -not (Test-Path -LiteralPath $Snap)
    if ($result.removed -and $result.removeError) { $result.removeError = "$($result.removeError) (removed by deleting the directory)" }
    $result
}
function Get-ReviewInputHashes([string]$InputDir, [string]$PromptPath)
{
    $hashes = @{}
    foreach ($name in @('brief.md', 'diff.patch', 'log.txt', 'stat.txt')) { $hashes[$name] = Get-OptionalSha256 (Join-Path $InputDir $name) }
    $hashes['prompt.md'] = Get-OptionalSha256 $PromptPath
    $hashes
}

function Get-ReviewInputProblems($Recorded, [string]$InputDir, [string]$PromptPath)
{
    # Every file the reviewer was given, against the hashes recorded before it started.
    if (-not $Recorded) { return @('no review input hashes were recorded') }
    $now = Get-ReviewInputHashes $InputDir $PromptPath
    @($now.Keys | Sort-Object | Where-Object { -not $Recorded.ContainsKey($_) -or -not $Recorded[$_] -or $Recorded[$_] -ne $now[$_] } | ForEach-Object { "review input $_ changed or is missing" })
}

function Test-ReviewInputsAtAccept($State, $Review)
{
    # Re-hashed, then bound to their sources: brief.md is the brief snapshot byte for byte and diff.patch is what the host generates
    # for base..reviewed commit today.
    $problems = @(Get-ReviewInputProblems (Get-Field $Review 'inputHashes') ([string]$Review.snapshot.inputDir) ([string]$Review.promptFile.path))
    if ($problems.Count) { return $problems }
    $dir = [string]$Review.snapshot.inputDir
    if ((Get-Sha256 (Join-Path $dir 'brief.md')) -ne (Get-Sha256 ([string]$State.brief.path))) { $problems += 'the brief the reviewer read is not the brief snapshot byte for byte' }
    $temp = Join-Path ([IO.Path]::GetTempPath()) "package-diff-$([guid]::NewGuid().ToString('N')).patch"
    try
    {
        Save-GitOutput ([string]$State.worktree) @('diff', '--no-ext-diff', '--no-textconv', '--no-color', [string]$Review.baseSha, [string]$Review.commit) $temp
        if ((Get-Sha256 $temp) -ne (Get-Sha256 (Join-Path $dir 'diff.patch'))) { $problems += 'diff.patch is not the diff the host generates for base..reviewed commit' }
    }
    catch { $problems += "the diff could not be regenerated: $($_.Exception.Message)" }
    finally { Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue }
    $problems
}
function Resolve-AbandonedReview($Info, [string]$Slug)
{
    # A package left in 'reviewing' (its wrapper died): the round stays spent, the review is recorded as invalid.
    $stateLock = Enter-StateLock $Info.StateRoot $Slug
    try
    {
        $state = Read-State $Info.StateRoot $Slug
        if ([string]$state.stage -ne 'reviewing') { return }
        $review = @(Get-List $state.reviews | Where-Object { $_.status -eq 'running' }) | Select-Object -Last 1
        if ($review -and $review.wrapper -and (Test-ProcessIdentityAlive ([int]$review.wrapper.pid) ([string]$review.wrapper.startUtc)) -and [int]$review.wrapper.pid -ne $PID) { throw "review $($review.reviewId) is still in progress (pid $($review.wrapper.pid))" }
        $reviewId = if ($review) { [string]$review.reviewId } else { '' }
        $leftover = if ($review) { Get-Field (Get-Field $review 'snapshot') 'path' } else { $null }
        if ($leftover -and (Test-Path -LiteralPath $leftover)) { Close-ReviewSnapshot ([string]$state.worktree) ([string]$leftover) | Out-Null }
        $exhausted = [int]$state.repair.roundsUsed -ge $script:RepairLimit
        Update-State $Info.StateRoot $Slug 'review-recovered' {
            param($s)
            $s.reviews = @(Get-List $s.reviews | ForEach-Object {
                    if ($_.reviewId -eq $reviewId) { $_.status = 'invalid'; $_.verdict = $null; $_.invalidReasons = @('the review was interrupted before it finished (the wrapper died)'); $_.termination = 'abandoned'; $_.endedUtc = (Get-Now) }
                    $_
                })
            $s.stage = if ($exhausted) { 'needs-coordinator' } else { 'review-invalid' }
        } -Locked | Out-Null
    }
    finally { $stateLock.Dispose() }
}

function Start-Review($Info)
{
    # Under the package lock: every precondition, the budget, then the review is recorded as running (and its round spent)
    # before anything starts, so a restart cannot get the round back.
    Sync-Invalidation $Info $Id
    $state = Read-State $Info.StateRoot $Id
    Assert-StateConsistent $state $Info
    $problems = @(Test-SnapshotsIntact $state)
    if ($problems.Count) { throw ($problems -join '; ') }
    $stage = [string]$state.stage
    if ($stage -eq 'invalidated') { throw 'the package is invalidated (the branch head changed after the review): run check, commit, then review' }
    if ($stage -notin @('committed', 'review-failed', 'review-invalid', 'review-passed', 'needs-coordinator')) { throw "review is not allowed in stage '$stage'$(if ($stage -eq 'checks-passed') { ": run commit first" })" }
    if (-not $Level) { throw '-Level light|heavy is required' }
    if ([bool]$state.guard.mainCheckoutMutated) { throw 'the main-checkout guard is not clean; no review is started' }
    $wt = [string]$state.worktree
    $commit = $state.commit
    if (-not $commit) { throw 'no commit is recorded: run commit first' }
    $head = Get-GitHead $wt
    $tree = Get-GitTree $wt
    if ($head -ne [string]$commit.sha -or $tree -ne [string]$commit.tree) { throw "the branch head $head is not the commit recorded by 'commit' ($($commit.sha)): run check and commit again" }
    if (-not (Test-WorktreeClean $wt)) { throw 'the worktree has uncommitted changes; a review inspects a committed head with a clean worktree' }
    $content = Get-TreeHash $wt
    $stale = Get-CheckFreshnessProblem $state $content
    if ($stale) { throw "review needs a fresh passing check for this commit's tree: $stale" }
    $latestCheck = @(Get-List $state.checks)[-1]

    $used = [int]$state.repair.roundsUsed
    if ($ResetBudget)
    {
        if ([string]::IsNullOrWhiteSpace($Reason)) { throw '-ResetBudget needs -Reason' }
        Reset-RepairBudget $Info $Id $Reason
        $used = 0
    }
    if ($used -ge $script:RepairLimit)
    {
        Update-State $Info.StateRoot $Id 'needs-coordinator' { param($s) $s.stage = 'needs-coordinator' } -Locked | Out-Null
        throw "the repair budget is used up ($used of $script:RepairLimit review rounds): the package needs the coordinator. Reassess it, then resume with -ResetBudget -Reason <text>"
    }

    $criteria = @(Get-ReviewCriteria ([string]$state.brief.path))
    $executorInfo = Get-ExecutorIdentity $state
    $chosen = Resolve-ReviewerChoice $executorInfo $Level $Reviewer
    $prior = @(Get-List $state.reviews | Where-Object { $who = Get-Field $_ 'reviewer'; $who -and $who.kind -eq $chosen.kind -and $who.sessionId }) | Select-Object -Last 1
    $resumeSession = if ($prior) { [string]$prior.reviewer.sessionId } else { $null }
    $reviewId = [guid]::NewGuid().ToString('N')
    $round = $used + 1
    $paths = Get-PackagePaths $Info.StateRoot $Id
    $dir = Join-Path $paths.Dir "reviews/$reviewId"
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    $verdictPath = Join-Path $dir 'verdict.json'
    # The base the host observes itself (after a fetch), not the one the state says; a mismatch makes the review invalid.
    $startProblems = [Collections.Generic.List[string]]::new()
    $remote = if ([string]$state.baseRef -like '*/*') { ([string]$state.baseRef).Split('/')[0] } else { $null }
    if ($remote) { try { Invoke-Git $wt @('fetch', $remote) | Out-Null } catch { $startProblems.Add("cannot fetch $remote to observe the base: $($_.Exception.Message)") } }
    $observedBase = ([string](& git -C $wt merge-base HEAD ([string]$state.baseRef) 2>$null)).Trim()
    if (-not $observedBase) { $startProblems.Add("the base cannot be observed (git merge-base HEAD $($state.baseRef))") }
    elseif ($observedBase -ne [string]$state.baseSha) { $startProblems.Add("the observed base $observedBase differs from the recorded baseSha $($state.baseSha)") }
    $fileMap = Get-FileHashMap $wt
    $fileMapPath = Join-Path $dir 'files.json'
    [IO.File]::WriteAllText($fileMapPath, ($fileMap | ConvertTo-Json -Depth 3), [Text.UTF8Encoding]::new($false))
    $mainCheckout = [string]$state.repository.mainCheckout
    $mainBefore = Get-Fingerprint $mainCheckout
    if (-not $mainBefore.treeHash) { throw "the main checkout cannot be fingerprinted (unreadable: $($mainBefore.unreadable -join ', ')); the review was not started" }
    $record = @{
        reviewId = $reviewId; round = $round; level = $Level; startedUtc = (Get-Now); endedUtc = $null; status = 'running'; verdict = $null; invalidReasons = @()
        reviewer = @{ kind = $chosen.kind; provider = $chosen.provider; model = $chosen.model; effort = $chosen.effort; sessionId = $null; resumedFrom = $resumeSession; cliVersion = $null; permissions = $null; executable = $null }
        executor = @{ provider = $executorInfo.provider; model = $executorInfo.model; kind = $executorInfo.kind; runId = [string]$executorInfo.run.runId; session = $executorInfo.session }
        commit = $head; tree = $tree; contentTree = $content; checkId = [string]$latestCheck.checkId; baseSha = $(if ($observedBase) { $observedBase } else { [string]$state.baseSha }); baseObserved = $observedBase; startProblems = @($startProblems)
        executors = @($executorInfo.set); fileHashes = @{ path = $fileMapPath; sha256 = (Get-Sha256 $fileMapPath) }; manifestSha256 = [string]$state.manifest.sha256
        snapshot = @{ path = (Join-Path $paths.Dir "review-$reviewId"); inputDir = (Join-Path $paths.Dir "review-$reviewId-input"); created = $false; startObservation = $null; endObservation = $null; removed = $false; removeError = $null }
        briefSha256 = [string]$state.brief.sha256; criteria = $criteria; criteriaSha256 = (Get-StringSha256 (($criteria | ForEach-Object { "$($_.id)=$($_.text)" }) -join "`n"))
        promptFile = $null; finalMessage = $null; verdictFile = @{ path = $verdictPath; sha256 = $null }; observedStart = @{ head = $head; tree = $tree; contentTree = $content; clean = $true }; observedEnd = $null
        wrapper = @{ pid = $PID; startUtc = (Get-ProcessStartIdentity $PID) }; mainCheckoutBefore = $mainBefore; mainCheckoutAfter = $null; termination = 'running'; exitCode = $null; containmentVerified = $null
        tokens = $null; denials = @(); findings = @(); criteriaResults = @(); omissions = @(); blockingFindings = 0; seconds = $null
    }
    $promptText = New-ReviewPrompt $state $record $verdictPath $prior
    $promptPath = Join-Path $dir 'prompt.md'
    [IO.File]::WriteAllText($promptPath, $promptText, [Text.UTF8Encoding]::new($false))
    $record.promptFile = @{ path = $promptPath; sha256 = (Get-Sha256 $promptPath) }
    Update-State $Info.StateRoot $Id 'review-started' {
        param($s)
        $s.repair.roundsUsed = $round
        $s.reviews = @(Get-List $s.reviews) + @($record)
        $s.pendingFix = $null
        $s.stage = 'reviewing'
    } -Locked | Out-Null
    Write-Host "review ${reviewId}: round $round of $script:RepairLimit, $Level, reviewer $($chosen.kind) ($($chosen.model))$(if ($resumeSession) { ", resuming session $resumeSession" })"
    @{ Record = $record; Chosen = $chosen; Dir = $dir; PromptPath = $promptPath; ResumeSession = $resumeSession; Worktree = $wt; MainCheckout = $mainCheckout; MainBefore = $mainBefore; StartProblems = @($startProblems); SnapshotStart = $null; SnapshotEnd = $null; InputHashes = $null }
}

function Complete-Review($Info, $Context, $Proc)
{
    $record = $Context.Record
    $reviewId = [string]$record.reviewId
    $wt = $Context.Worktree
    $stateLock = Enter-StateLock $Info.StateRoot $Id
    try
    {
        $state = Read-State $Info.StateRoot $Id
        $reasons = [Collections.Generic.List[string]]::new()
        # What the host saw when the reviewer finished: the same head and tree, a clean worktree, unchanged content.
        $endHead = Get-GitHead $wt
        $endTree = Get-GitTree $wt
        $endClean = Test-WorktreeClean $wt
        $endContent = try { Get-TreeHash $wt } catch { $null }
        $observedEnd = @{ head = $endHead; tree = $endTree; contentTree = $endContent; clean = $endClean }
        $mainAfter = Get-Fingerprint $Context.MainCheckout
        $mainMutated = -not (Test-FingerprintsEqual $Context.MainBefore $mainAfter)
        if ($Proc.Termination -ne 'exit') { $reasons.Add("the reviewer did not finish cleanly: $($Proc.Termination)") }
        if (-not $Proc.ContainmentVerified) { $reasons.Add("containment unverified: $($Proc.ContainmentReason)") }
        if ($Proc.Termination -eq 'exit' -and $Proc.ExitCode -ne 0) { $reasons.Add("the reviewer exited with code $($Proc.ExitCode)") }
        if ($Proc.ResultError) { $reasons.Add('the reviewer reported an error result') }
        if ($Proc.IdentityProblem) { $reasons.Add("session identity: $($Proc.IdentityProblem)") }
        if (@($Proc.Denials).Count) { $reasons.Add("the reviewer was denied $(@($Proc.Denials).Count) tool call(s), so it may not have inspected everything") }
        if ($endHead -ne [string]$record.commit) { $reasons.Add("the branch head changed during the review ($($record.commit) -> $endHead)") }
        if ($endTree -ne [string]$record.tree) { $reasons.Add("the git tree changed during the review ($($record.tree) -> $endTree)") }
        if (-not $endClean) { $reasons.Add('the worktree was not clean at the end of the review') }
        if ($endContent -ne [string]$record.contentTree) { $reasons.Add('the worktree content changed during the review') }
        if ($mainMutated) { $reasons.Add('the main checkout changed during the review') }
        foreach ($problem in @($Context.StartProblems)) { $reasons.Add([string]$problem) }
        # Everything the reviewer was given (the prepared inputs and the prompt) is unchanged since before it started.
        if ($Context.InputHashes) { foreach ($problem in @(Get-ReviewInputProblems $Context.InputHashes ([string]$record.snapshot.inputDir) ([string]$Context.PromptPath))) { $reasons.Add([string]$problem) } }
        # The base again, after the reviewer finished.
        $endBase = ([string](& git -C $wt merge-base HEAD ([string]$state.baseRef) 2>$null)).Trim()
        if ($endBase -ne [string]$state.baseSha) { $reasons.Add("the observed base at the end of the review ($endBase) differs from the recorded baseSha ($($state.baseSha))") }
        # The immutable snapshot the reviewer read: created, unchanged from start to end, removed afterwards.
        $snapStart = $Context.SnapshotStart
        $snapEnd = $Context.SnapshotEnd
        if (-not $snapStart) { $reasons.Add('the review snapshot was not created or verified') }
        else
        {
            if (-not $snapEnd -or -not $snapEnd.endObservation) { $reasons.Add('the review snapshot could not be observed at the end of the review') }
            else
            {
                $e = $snapEnd.endObservation
                if ($e.head -ne $snapStart.head -or $e.tree -ne $snapStart.tree -or $e.contentTree -ne $snapStart.contentTree -or -not $e.clean) { $reasons.Add('the review snapshot changed during the review (head, tree, content or cleanliness)') }
            }
            if ($snapEnd -and -not $snapEnd.removed) { $reasons.Add("the review snapshot could not be removed: $($snapEnd.removeError)") }
        }

        $finalPath = Join-Path $Context.Dir 'final.md'
        $verdictPath = [string]$record.verdictFile.path
        $verdictText = $null
        if ($null -ne $Proc.FinalText)
        {
            if (-not (Test-Path -LiteralPath $finalPath -PathType Leaf)) { [IO.File]::WriteAllText($finalPath, [string]$Proc.FinalText, [Text.UTF8Encoding]::new($false)) }
            $verdictText = Get-VerdictJsonText ([string]$Proc.FinalText)
            [IO.File]::WriteAllText($verdictPath, $verdictText, [Text.UTF8Encoding]::new($false))
        }
        else { $reasons.Add('the reviewer produced no final message, so there is no verdict file') }
        $checkFresh = (-not (Get-CheckFreshnessProblem $state ([string]$endContent))) -and $endContent
        $validation = if ($null -ne $verdictText) { Test-VerdictDocument $verdictText $record.criteria ([string]$record.commit) ([string]$record.tree) ([bool]$checkFresh) } else { $null }
        if ($validation) { foreach ($problem in $validation.problems) { $reasons.Add([string]$problem) } }
        $valid = ($reasons.Count -eq 0) -and $validation -and $validation.ok
        $verdict = if ($valid) { $validation.verdict } else { $null }

        $exhausted = [int]$state.repair.roundsUsed -ge $script:RepairLimit
        $stage = if ($mainMutated) { 'blocked-main-mutated' } elseif ($valid -and $verdict -eq 'PASS') { 'review-passed' } elseif ($exhausted) { 'needs-coordinator' } elseif ($valid) { 'review-failed' } else { 'review-invalid' }
        $finalHash = Get-OptionalSha256 $finalPath
        $verdictHash = Get-OptionalSha256 $verdictPath
        Update-State $Info.StateRoot $Id 'review-finished' {
            param($s)
            $s.reviews = @(Get-List $s.reviews | ForEach-Object {
                    if ($_.reviewId -eq $reviewId)
                    {
                        $_.endedUtc = (Get-Now); $_.status = $(if ($valid) { 'valid' } else { 'invalid' }); $_.verdict = $verdict; $_.invalidReasons = @($reasons)
                        $_.reviewer.sessionId = $Proc.SessionId; $_.reviewer.cliVersion = $Proc.CliVersion; $_.reviewer.permissions = $Proc.Permissions; $_.reviewer.executable = $Proc.Executable
                        $_.finalMessage = @{ path = $finalPath; sha256 = $finalHash }; $_.verdictFile = @{ path = $verdictPath; sha256 = $verdictHash }
                        $_.observedEnd = $observedEnd; $_.mainCheckoutAfter = $mainAfter; $_.startProblems = @($Context.StartProblems); $_.inputHashes = $Context.InputHashes; $_.snapshot.created = [bool]$snapStart; $_.snapshot.startObservation = $snapStart; $_.snapshot.endObservation = $(if ($snapEnd) { $snapEnd.endObservation } else { $null }); $_.snapshot.removed = [bool]($snapEnd -and $snapEnd.removed); $_.snapshot.removeError = $(if ($snapEnd) { $snapEnd.removeError } else { $null }); $_.termination = $Proc.Termination; $_.exitCode = $Proc.ExitCode; $_.containmentVerified = [bool]$Proc.ContainmentVerified
                        $_.tokens = $Proc.Tokens; $_.denials = @($Proc.Denials); $_.seconds = $Proc.Seconds
                        if ($valid) { $_.findings = @($validation.findings); $_.criteriaResults = @($validation.criteria); $_.omissions = @($validation.omissions); $_.blockingFindings = $validation.blocking }
                    }
                    $_
                })
            if ($mainMutated) { $s.guard = @{ mainCheckoutMutated = $true; lastRunId = $reviewId; before = $Context.MainBefore; after = $mainAfter } }
            $s.stage = $stage
        } -Locked | Out-Null
        if ($mainMutated) { $script:ExitCode = 5; Write-Host 'ERROR the main checkout changed during the review. Nothing was reverted; the package is stopped.'; return }
        if ($valid)
        {
            Write-Host "review ${reviewId}: $verdict (valid) - $($validation.blocking) blocking finding(s), $(@($validation.findings).Count) in all, $(@($validation.omissions).Count) omission(s)"
            foreach ($f in $validation.findings) { Write-Host "  $($f.id) [$($f.severity)] $($f.file):$($f.line) $($f.summary)" }
        }
        else
        {
            Write-Host "review ${reviewId}: INVALID (never a PASS)"
            foreach ($why in $reasons) { Write-Host "  - $why" }
        }
        if ($stage -eq 'needs-coordinator') { Write-Host "the repair budget is used up ($script:RepairLimit of $script:RepairLimit rounds): the package needs the coordinator" }
        if ($stage -ne 'review-passed') { $script:ExitCode = 1 }
    }
    finally { $stateLock.Dispose() }
}

function Invoke-Review
{
    $info = Get-RepoInfo $Repo
    Resolve-AbandonedReview $info $Id
    $stateLock = Enter-StateLock $info.StateRoot $Id
    try { $context = Start-Review $info }
    finally { $stateLock.Dispose() }
    $process = $null
    $record = $context.Record
    $snapshotCreated = $false
    $failed = { param($why) @{ Termination = 'failed'; ExitCode = $null; ContainmentVerified = $false; ContainmentReason = $why; SessionId = $null; IdentityProblem = $null; FinalText = $null; ResultError = $false; Denials = @(); Tokens = $null; Seconds = $null; CliVersion = $null; Permissions = $null; Executable = $null } }
    try
    {
        if (@($context.StartProblems).Count) { $process = & $failed "the reviewer was not started: $(@($context.StartProblems) -join '; ')" }
        else
        {
            $snapshotCreated = $true
            $context.SnapshotStart = New-ReviewSnapshot (Read-State $info.StateRoot $Id) $record
            $snap = [string]$record.snapshot.path
            # The checks ran on the package worktree: the snapshot has to hold exactly the same raw bytes. No normalization, no
            # tolerance: any difference means the checks say nothing about what the reviewer reads. The paths come from the
            # per-file fingerprint recorded with the check's content at the start of the review.
            if ($context.SnapshotStart.contentTree -ne [string]$record.contentTree)
            {
                $checked = Get-Content -LiteralPath ([string]$record.fileHashes.path) -Raw | ConvertFrom-Json -AsHashtable
                $snapMap = Get-FileHashMap $snap
                $differing = @(@($checked.Keys) + @($snapMap.Keys) | Select-Object -Unique | Where-Object { $checked[$_] -ne $snapMap[$_] } | Sort-Object)
                $context.StartProblems = @($context.StartProblems) + @("checks did not run on the committed content (raw bytes differ: $(($differing | Select-Object -First 5) -join ', '))")
            }
            $context.InputHashes = Get-ReviewInputHashes ([string]$record.snapshot.inputDir) ([string]$context.PromptPath)
            $inputHashes = $context.InputHashes
            $reviewIdNow = [string]$record.reviewId
            Update-State $info.StateRoot $Id 'review-inputs-bound' { param($s) $s.reviews = @(Get-List $s.reviews | ForEach-Object { if ($_.reviewId -eq $reviewIdNow) { $_.inputHashes = $inputHashes }; $_ }) } | Out-Null
            $process = if (@($context.StartProblems).Count) { & $failed "the reviewer was not started: $(@($context.StartProblems) -join '; ')" } elseif ($context.Chosen.provider -eq 'claude') { Invoke-ClaudeReviewProcess $snap $context.Dir $context.PromptPath $context.Chosen $context.ResumeSession ([string]$record.snapshot.inputDir) }
            else { Invoke-CodexReviewProcess $snap $context.Dir $context.PromptPath $context.Chosen $context.ResumeSession }
        }
    }
    catch { $process = & $failed "the reviewer could not be started: $($_.Exception.Message)" }
    finally
    {
        if ($snapshotCreated -and (Test-Path -LiteralPath ([string]$record.snapshot.path))) { $context.SnapshotEnd = Close-ReviewSnapshot $context.Worktree ([string]$record.snapshot.path) }
    }
    Complete-Review $info $context $process
}
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

function Invoke-Fix
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
        if ($accepted.Count -eq 0) { throw 'every blocking finding was rejected: there is nothing to fix. Review again, or reassess' }
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
        Update-State $info.StateRoot $Id 'fix-started' { param($s) $s.pendingFix = @{ reviewId = $fixReviewId; utc = (Get-Now); fixRequest = $fixPath } } -Locked | Out-Null
    }
    finally { $stateLock.Dispose() }

    Write-Host "fix: $(($accepted | ForEach-Object { $_.id }) -join ', ') accepted; executor $kind$(if ($kind -ne 'codex') { ' (resumed)' } else { ' (new run with the previous report)' })"
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
        $recordArgs = @('-NoProfile', '-File', $RecordReviewScript, '-Worktree', $wt, '-Head', [string]$review.commit,
            '-Executor', "$($ex.provider):$($ex.model)", '-ExecutorSession', $executorSession,
            '-Reviewer', "$($review.reviewer.provider):$($review.reviewer.model)", '-ReviewerSession', $reviewerSession,
            '-Verdict', 'PASS', '-BlockingFindings', '0', '-Evidence', $verdictPath, '-Brief', [string]$state.brief.path, '-Review', [string]$review.level)
        $recordOutput = @(& pwsh @recordArgs 2>&1 | ForEach-Object { "$_" })
        if ($LASTEXITCODE -ne 0) { throw "record-review.ps1 refused (exit $LASTEXITCODE): $($recordOutput -join ' ')" }
        $recordPath = Join-Path $info.CommonDir "pointframe/reviews/$($review.commit).json"
        if (-not (Test-Path -LiteralPath $recordPath -PathType Leaf)) { throw "record-review.ps1 reported success but $recordPath does not exist" }
        $acceptance = @{ utc = (Get-Now); reason = $Reason.Trim(); reviewId = [string]$review.reviewId; commit = [string]$review.commit; tree = [string]$review.tree; head = $binding.Head; pinOnly = [bool]$binding.PinOnly; recordPath = $recordPath; recordSha256 = (Get-Sha256 $recordPath); invalidated = $null }
        Update-State $info.StateRoot $Id 'accepted' { param($s) $s.acceptance = $acceptance; $s.stage = 'accepted' } -Locked | Out-Null
        Write-Host "accepted: review $($review.reviewId) (PASS) is bound to $($review.commit)$(if ($binding.PinOnly) { " and the plugin-pin-only head $($binding.Head)" }); review record $recordPath"
    }
    finally { $stateLock.Dispose() }
}

# ---- self-test--------------------------------------------------------------------------------------------------

function Invoke-ReviewSelfTest
{
    # Runs inside Invoke-SelfTest (dynamic scope): $root, $main, $origin, $seed, $git, $stubDir, $stub, $pkg, $stateFile, $statePath, $packagesRoot,
    # $expect and $failures are the caller's. Reviewers and executors are marked stubs; no real claude or codex process is ever started.
    & git -C $main config user.name SelfTest
    & git -C $main config user.email selftest@example.invalid
    $briefRv = Join-Path $root 'brief-review.md'
    [IO.File]::WriteAllText($briefRv, (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'tests/agent-run/complete.md') -Raw) + "`r`n# Acceptance cases`r`n`r`n- The feature file exists.`r`n- The feature file says v2, and the`r`n  comment above it explains why.`r`n1. Nothing else changes.`r`n`r`n# Authorized repairs`r`n`r`nNone.`r`n")
    $rvManifest = Join-Path $root 'rv-checks.json'
    [IO.File]::WriteAllText($rvManifest, (@{ extra = @(@{ name = 'passes'; exe = 'pwsh'; args = @('-NoProfile', '-Command', 'exit 0') }) } | ConvertTo-Json -Depth 6))
    $recheckManifest = Join-Path $root 'recheck-checks.json'
    [IO.File]::WriteAllText($recheckManifest, (@{ extra = @(@{ name = 'no-c2'; exe = 'pwsh'; args = @('-NoProfile', '-Command', 'if (Test-Path c2.txt) { exit 3 }; exit 0') }) } | ConvertTo-Json -Depth 6))
    $rrecordDir = Join-Path $root 'review-records'
    $env:POINTFRAME_PKG_TEST_RRECORD = $rrecordDir
    $resetR = { Remove-Item -LiteralPath $rrecordDir -Recurse -Force -ErrorAction SilentlyContinue; New-Item -ItemType Directory -Path $rrecordDir -Force | Out-Null }
    $rcalls = { @(Get-ChildItem -LiteralPath $rrecordDir -Filter 'rcall-*.json' -ErrorAction SilentlyContinue | Sort-Object { [int]($_.BaseName -replace '\D') }) }
    $readCall = { param($file) Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json }
    & $resetR

    # Reviewer stubs. The mode comes from POINTFRAME_PKG_TEST_VMODE; the commit, tree and criteria are read from the prompt itself.
    $verdictCore = @'
$stdin = [Console]::In.ReadToEnd()
$all = @($args)
$rec = $env:POINTFRAME_PKG_TEST_RRECORD
$n = @(Get-ChildItem -LiteralPath $rec -Filter 'rcall-*.json').Count + 1
$si = [Array]::IndexOf($all, '--settings')
$settingsText = if ($si -ge 0) { Get-Content -LiteralPath $all[$si + 1] -Raw } else { '' }
[ordered]@{ args = $all; cwd = (Get-Location).Path; stdin = $stdin; settings = $settingsText } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $rec "rcall-$n.json")
$commit = if ($stdin -match 'Reviewed commit: ([0-9a-f]{40,64})') { $Matches[1] } else { '' }
$tree = if ($stdin -match 'Reviewed tree: ([0-9a-f]{40,64})') { $Matches[1] } else { '' }
$ids = @([regex]::Matches($stdin, '(?m)^(C\d+): ') | ForEach-Object { $_.Groups[1].Value })
$mode = $env:POINTFRAME_PKG_TEST_VMODE
$zero = '0' * 40
$criteria = @($ids | ForEach-Object { [ordered]@{ id = $_; result = 'pass'; evidence = "read it ($_)" } })
$doc = [ordered]@{ schemaVersion = 1; reviewedCommit = $commit; reviewedTree = $tree; verdict = 'PASS'; criteria = $criteria; findings = @(); omissions = @('integration tests were not run') }
$blocking = [ordered]@{ id = 'F1'; severity = 'blocking'; file = 'feature.txt'; line = 1; summary = 'feature.txt is wrong'; scenario = 'open feature.txt: it says v1 and the brief wants v2' }
$minor = [ordered]@{ id = 'F2'; severity = 'non-blocking'; file = 'a.txt'; line = 1; summary = 'naming'; scenario = 'a.txt is named a' }
switch ($mode)
{
    'fail' { $doc.verdict = 'FAIL'; $doc.criteria[0].result = 'fail'; $doc.findings = @($blocking, $minor) }
    'stale' { $doc.reviewedCommit = $zero }
    'missingcrit' { $doc.criteria = @($doc.criteria | Select-Object -SkipLast 1) }
    'passblock' { $doc.findings = @($blocking) }
}
$json = $doc | ConvertTo-Json -Depth 6
switch ($mode)
{
    'malformed' { $json = $json.Substring(0, [int]($json.Length / 2)) }
    'prose' { $json = 'VERDICT: PASS - looks good to me' }
    'empty' { $json = '' }
    'fenced' { $json = '```json' + "`n" + $json + "`n" + '```' }
    'sleep' { Start-Sleep -Seconds 120 }
    'dirty' { Set-Content -LiteralPath (Join-Path (Get-Location).Path 'dirty-by-reviewer.txt') 'x' }
    'snapwrite' { Set-Content -LiteralPath (Join-Path (Get-Location).Path 'written-by-reviewer.txt') 'x' }
    'editinput' { if ($stdin -match 'read them first\): ([^\r\n]+)') { $f = Join-Path $Matches[1].Trim() 'diff.patch'; (Get-Item $f).IsReadOnly = $false; Add-Content -LiteralPath $f 'tampered by the reviewer' } }
}

'@
    $codexReviewer = "# pointframe-selftest-stub`r`n" + $verdictCore + @'
$oi = [Array]::IndexOf($all, '-o')
[IO.File]::WriteAllText($all[$oi + 1], $json)
$sid = if ($all -contains 'resume') { $all[$all.Count - 2] } else { 'codex-rv-session' }
'{"type":"thread.started","thread_id":"' + $sid + '"}'
'{"type":"turn.completed","usage":{"input_tokens":7,"cached_input_tokens":2,"output_tokens":3}}'

'@
    $claudeReviewer = "# pointframe-selftest-stub`r`n`$a = @(`$args)`r`nif (`$a -contains '--version') { '9.9.9-stub (Claude Code)'; exit 0 }`r`n" + $verdictCore + @'
function Emit([string]$Line) { [Console]::Out.WriteLine($Line); [Console]::Out.Flush() }
$resumeAt = [Array]::IndexOf($all, '--resume')
$sid = if ($resumeAt -ge 0) { $all[$resumeAt + 1] } else { 'stub-claude-review' }
Emit ('{"type":"system","subtype":"init","session_id":"' + $sid + '","model":"stub-model","cwd":' + ((Get-Location).Path | ConvertTo-Json) + '}')
if ($mode -eq 'denied')
{
    Emit '{"type":"assistant","message":{"id":"m1","content":[{"type":"tool_use","id":"m1-t","name":"Bash","input":{}}],"usage":{"input_tokens":50,"output_tokens":9}}}'
    Emit '{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"m1-t","is_error":true,"content":"Permission to use Bash has been denied."}]}}'
}
Emit ('{"type":"result","subtype":"success","is_error":false,"result":' + ($json | ConvertTo-Json) + ',"session_id":"' + $sid + '","usage":{"input_tokens":50,"cache_creation_input_tokens":0,"cache_read_input_tokens":0,"output_tokens":9},"permission_denials":[]}')

'@
    $rvClaude = Join-Path $stubDir 'rv-claude.ps1'
    $rvCodex = Join-Path $stubDir 'rv-codex.ps1'
    [IO.File]::WriteAllText($rvClaude, $claudeReviewer)
    [IO.File]::WriteAllText($rvCodex, $codexReviewer)
    $fixerCodex = Join-Path $stubDir 'fixer.ps1'
    [IO.File]::WriteAllText($fixerCodex, $prelude + @'
$briefAt = $a[[Array]::IndexOf($a, '-Brief') + 1]
$wtAt = $a[[Array]::IndexOf($a, '-Worktree') + 1]
Copy-Item -LiteralPath $briefAt -Destination (Join-Path $env:POINTFRAME_PKG_TEST_RRECORD 'fix-brief.md') -Force
Set-Content -LiteralPath (Join-Path $wtAt 'fixed.txt') "fixed`r`n"
Set-Content -LiteralPath (Join-Path $log 'out.md') 'fixed what the review found'
exit 0

'@)
    $fixerClaude = Join-Path $stubDir 'c-fix.ps1'
    [IO.File]::WriteAllText($fixerClaude, $claudeStubPrelude + "Set-Content -LiteralPath (Join-Path (Get-Location).Path 'fixed.txt') 'fixed'; Emit-Result 'fixed what the review found'; exit 0`r`n")
    $env:POINTFRAME_PKG_TEST_TOKENS = '100'
    $wtOf = { param($slug) [string](& $stateFile $slug).worktree }
    $headOf = { param($slug) (& git -C (& $wtOf $slug) rev-parse HEAD).Trim() }
    $script:DiscoveryScript = Join-Path $stubDir 'discovery.ps1'
    $writePins = {
        param($wt, $version = '1.0.0', $lockVersion = $null, $sha = ('a' * 64), $lockRaw = $null)
        New-Item -ItemType Directory -Path (Join-Path $wt 'plugin/pointframe/.claude-plugin') -Force | Out-Null
        if (-not $lockVersion) { $lockVersion = $version }
        $lock = if ($lockRaw) { $lockRaw } else { @{ schemaVersion = 1; version = $lockVersion; asset = 'x.mcpb'; url = 'https://example.invalid/x'; sha256 = $sha } | ConvertTo-Json }
        [IO.File]::WriteAllText((Join-Path $wt 'plugin/pointframe/server.lock.json'), $lock + "`r`n")
        [IO.File]::WriteAllText((Join-Path $wt 'plugin/pointframe/.claude-plugin/plugin.json'), (@{ name = 'pointframe'; version = $version } | ConvertTo-Json) + "`r`n")
    }
    $write = { param($slug, $name, $text) [IO.File]::WriteAllText((Join-Path (& $wtOf $slug) $name), $text) }
    $newPackage = {
        param($slug, $manifest = $rvManifest, $runArgs = @())
        $r = & $pkg @('new', '-Id', $slug, '-Brief', $briefRv, '-Checks', $manifest)
        & $expect ($r.Exit -eq 0) "package $slug could not be created: $($r.Text)"
        $r = & $pkg (@('run', '-Id', $slug, '-AgentRunScript', (Join-Path $stubDir 'ok.ps1'), '-CodexVersion', '9.9.9-test') + @($runArgs))
        & $expect ($r.Exit -eq 0 -and (& $stateFile $slug).stage -eq 'executor-completed') "package $slug run failed: $($r.Text)"
    }

    # Pure functions: criteria from the brief, the verdict document against the schema and the host's own observations.
    $criteria = @(Get-ReviewCriteria $briefRv)
    & $expect ($criteria.Count -eq 3 -and $criteria[0].id -eq 'C1' -and $criteria[1].text -eq 'The feature file says v2, and the comment above it explains why.' -and $criteria[2].id -eq 'C3' -and $criteria[2].text -eq 'Nothing else changes.') "criteria were not read from the Acceptance cases list: $($criteria | ConvertTo-Json -Compress)"
    $noCriteria = Join-Path $root 'no-criteria.md'
    [IO.File]::WriteAllText($noCriteria, "# Objective`r`nx`r`n")
    $refused = $false
    try { Get-ReviewCriteria $noCriteria | Out-Null } catch { $refused = $true }
    & $expect $refused 'a brief without Acceptance cases was accepted for review'
    $schema = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'package-verdict.schema.json') -Raw | ConvertFrom-Json
    & $expect ((@($schema.required) -join ',') -eq 'schemaVersion,reviewedCommit,reviewedTree,verdict,criteria,findings,omissions' -and (@($schema.properties.findings.items.required) -join ',') -eq 'id,severity,file,line,summary,scenario' -and (@($schema.properties.criteria.items.properties.result.enum) -join ',') -eq 'pass,fail,not-checked' -and (@($schema.properties.verdict.enum) -join ',') -eq 'PASS,FAIL') 'the schema file does not describe the document the host validates'
    $sha = 'a' * 40
    $tre = 'b' * 40
    $unit = [ordered]@{ criteria = @(@{ id = 'C1'; text = 'one' }, @{ id = 'C2'; text = 'two' }) }
    $validDoc = {
        [ordered]@{ schemaVersion = 1; reviewedCommit = $sha; reviewedTree = $tre; verdict = 'PASS'
            criteria = @([ordered]@{ id = 'C1'; result = 'pass'; evidence = 'e1' }, [ordered]@{ id = 'C2'; result = 'pass'; evidence = 'e2' }); findings = @(); omissions = @() }
    }
    $sampleFinding = { [ordered]@{ id = 'F1'; severity = 'blocking'; file = 'x.cs'; line = 3; summary = 's'; scenario = 'sc' } }
    $verdictCases = @(
        @{ name = 'valid PASS'; edit = { }; ok = $true; verdict = 'PASS' }
        @{ name = 'valid FAIL'; edit = { param($d) $d.verdict = 'FAIL'; $d.criteria[1].result = 'fail'; $d.findings = @(& $sampleFinding) }; ok = $true; verdict = 'FAIL' }
        @{ name = 'unknown key'; edit = { param($d) $d.extra = 1 }; why = 'unknown top-level key' }
        @{ name = 'exact schema URL'; edit = { param($d) $d['$schema'] = 'https://json-schema.org/draft/2020-12/schema' }; ok = $true; verdict = 'PASS' }
        @{ name = 'other schema URL'; edit = { param($d) $d['$schema'] = 'https://example.invalid/schema' }; why = 'the $schema value, when present, must be exactly' }
        @{ name = 'schema as an empty array'; edit = { param($d) $d['$schema'] = @() }; why = 'the $schema value, when present, must be exactly' }
        @{ name = 'schema as an array holding the URL'; edit = { param($d) $d['$schema'] = @('https://json-schema.org/draft/2020-12/schema') }; why = 'the $schema value, when present, must be exactly' }
        @{ name = 'schema as a boolean'; edit = { param($d) $d['$schema'] = $true }; why = 'the $schema value, when present, must be exactly' }
        @{ name = 'schema as a number'; edit = { param($d) $d['$schema'] = 5 }; why = 'the $schema value, when present, must be exactly' }
        @{ name = 'schema as null'; edit = { param($d) $d['$schema'] = $null }; why = 'the $schema value, when present, must be exactly' }
        @{ name = 'schema URL with a different case'; edit = { param($d) $d['$schema'] = 'HTTPS://json-schema.org/draft/2020-12/schema' }; why = 'the $schema value, when present, must be exactly' }
        @{ name = 'missing key'; edit = { param($d) $d.Remove('omissions') }; why = "missing top-level key 'omissions'" }
        @{ name = 'schema version'; edit = { param($d) $d.schemaVersion = 2 }; why = 'schemaVersion' }
        @{ name = 'verdict value'; edit = { param($d) $d.verdict = 'MAYBE' }; why = 'verdict must be PASS or FAIL' }
        @{ name = 'verdict case'; edit = { param($d) $d.verdict = 'pass' }; why = 'verdict must be PASS or FAIL' }
        @{ name = 'short commit'; edit = { param($d) $d.reviewedCommit = 'abc123' }; why = 'full object id' }
        @{ name = 'stale commit'; edit = { param($d) $d.reviewedCommit = '1' * 40 }; why = 'not the host-observed head' }
        @{ name = 'stale tree'; edit = { param($d) $d.reviewedTree = '1' * 40 }; why = 'not the host-observed tree' }
        @{ name = 'missing criterion'; edit = { param($d) $d.criteria = @($d.criteria[0]) }; why = 'criterion C2 of the brief is missing' }
        @{ name = 'unknown criterion'; edit = { param($d) $d.criteria = @($d.criteria[0], $d.criteria[1], [ordered]@{ id = 'C9'; result = 'pass'; evidence = 'e' }) }; why = 'criterion C9, which the brief does not have' }
        @{ name = 'duplicate criterion'; edit = { param($d) $d.criteria = @($d.criteria[0], $d.criteria[0], $d.criteria[1]) }; why = 'criterion C1 appears twice' }
        @{ name = 'criterion result'; edit = { param($d) $d.criteria[0].result = 'passed' }; why = 'result must be pass, fail or not-checked' }
        @{ name = 'empty evidence'; edit = { param($d) $d.criteria[0].evidence = ' ' }; why = 'evidence must be a non-empty string' }
        @{ name = 'criterion extra key'; edit = { param($d) $d.criteria[0].note = 'x' }; why = 'exactly id, result and evidence' }
        @{ name = 'PASS with a failing criterion'; edit = { param($d) $d.criteria[0].result = 'fail' }; why = 'contradiction: verdict PASS but criteria C1=fail' }
        @{ name = 'PASS with not-checked'; edit = { param($d) $d.criteria[1].result = 'not-checked' }; why = 'contradiction: verdict PASS but criteria C2=not-checked' }
        @{ name = 'PASS with a blocking finding'; edit = { param($d) $d.findings = @(& $sampleFinding) }; why = 'PASS with 1 blocking finding' }
        @{ name = 'FAIL with nothing wrong'; edit = { param($d) $d.verdict = 'FAIL' }; why = 'FAIL but every criterion passes' }
        @{ name = 'FAIL with only a minor finding'; edit = { param($d) $d.verdict = 'FAIL'; $f = & $sampleFinding; $f.severity = 'non-blocking'; $d.findings = @($f) }; why = 'FAIL but every criterion passes' }
        @{ name = 'finding severity'; edit = { param($d) $f = & $sampleFinding; $f.severity = 'critical'; $d.findings = @($f) }; why = 'severity must be blocking or non-blocking' }
        @{ name = 'finding without scenario'; edit = { param($d) $f = & $sampleFinding; $f.Remove('scenario'); $d.findings = @($f) }; why = 'exactly id, severity, file, line, summary and scenario' }
        @{ name = 'negative line'; edit = { param($d) $f = & $sampleFinding; $f.line = -1; $d.findings = @($f) }; why = 'line must be an integer' }
        @{ name = 'text line'; edit = { param($d) $f = & $sampleFinding; $f.line = 'x'; $d.findings = @($f) }; why = 'line must be an integer' }
        @{ name = 'duplicate finding'; edit = { param($d) $d.findings = @((& $sampleFinding), (& $sampleFinding)) }; why = 'finding F1 appears twice' }
        @{ name = 'omissions type'; edit = { param($d) $d.omissions = @(1) }; why = 'omissions must be strings' }
        @{ name = 'criteria not an array'; edit = { param($d) $d.criteria = 'all pass' }; why = 'criteria must be an array' }
    )
    foreach ($case in $verdictCases)
    {
        $d = & $validDoc
        & $case.edit $d
        $res = Test-VerdictDocument ($d | ConvertTo-Json -Depth 6) $unit.criteria $sha $tre $true
        if ($res -isnot [hashtable]) { $failures.Add("verdict case '$($case.name)' returned $(if ($null -eq $res) { 'nothing' } else { $res.GetType().Name + ': ' + ($res | Out-String) })"); continue }
        if ($case.ContainsKey('ok')) { & $expect ($res.ok -and $res.verdict -eq $case.verdict) "verdict case '$($case.name)' was refused: $($res.problems -join '; ')" }
        else { & $expect (-not $res.ok -and $null -eq $res.verdict -and ($res.problems -join '; ') -match [regex]::Escape($case.why)) "verdict case '$($case.name)' was not refused for '$($case.why)': ok=$($res.ok) $($res.problems -join '; ')" }
    }
    $notFresh = Test-VerdictDocument ((& $validDoc) | ConvertTo-Json -Depth 6) $unit.criteria $sha $tre $false
    & $expect (-not $notFresh.ok -and ($notFresh.problems -join ' ') -match 'fresh passing check') 'a PASS without a fresh check was accepted'
    foreach ($bad in @(@('', 'the verdict is empty'), @('VERDICT: PASS', 'not a JSON document'), @('{"schemaVersion": 1, "reviewedCom', 'not a JSON document'), @('[]', 'not a JSON object'), @('null', 'not a JSON object'), @('"PASS"', 'not a JSON object')))
    {
        $res = Test-VerdictDocument $bad[0] $unit.criteria $sha $tre $true
        & $expect (-not $res.ok -and ($res.problems -join ' ') -match [regex]::Escape($bad[1])) "'$($bad[0])' was not refused as '$($bad[1])': $($res.problems -join '; ')"
    }
    $fencedText = Get-VerdictJsonText ("``````json`r`n" + ((& $validDoc) | ConvertTo-Json -Depth 6) + "`r`n``````")
    & $expect ((Test-VerdictDocument $fencedText $unit.criteria $sha $tre $true).ok) 'a single enclosing code fence was not tolerated'
    & $expect ((Get-VerdictJsonText "Here is my verdict:`n{}") -ne '{}') 'prose around a verdict was stripped instead of refused'
    $reviewerParams = (Get-Command $PSCommandPath).Parameters.Keys
    & $expect (@(@('Head', 'Verdict', 'Evidence', 'BlockingFindings', 'ReviewerSession', 'ExecutorSession', 'ReviewerIdentity') | Where-Object { $_ -in $reviewerParams }).Count -eq 0) 'accept takes record values from the caller'

    # ---- package rv: commit, review, dispositions, fix, resume, accept (Codex executor, Claude reviewer) ----
    & $newPackage 'rv' $rvManifest @('-Effort', 'high', '-EscalationReason', 'selftest')
    $r = & $pkg @('commit', '-Id', 'rv')
    & $expect ($r.Exit -ne 0 -and $r.Text -match "needs 'checks-passed'") "commit was allowed before a passing check: $($r.Text)"
    $r = & $pkg @('run', '-Id', 'rv', '-Executor', 'same', '-AgentRunScript', (Join-Path $stubDir 'ok.ps1'))
    & $expect ($r.Exit -ne 0 -and $r.Text -match 'only meaningful for') "run -Executor same was accepted: $($r.Text)"
    $r = & $pkg @('fix', '-Id', 'rv', '-Executor', 'claude-sonnet')
    & $expect ($r.Exit -ne 0 -and $r.Text -match 'resumes the same executor') "fix -Executor <other> was accepted: $($r.Text)"
    & $write 'rv' 'feature.txt' "v1`r`n"
    $r = & $pkg @('check', '-Id', 'rv')
    & $expect ($r.Exit -eq 0 -and (& $stateFile 'rv').stage -eq 'checks-passed') "check of rv failed: $($r.Text)"
    & $write 'rv' 'stale.txt' "after the check`r`n"
    $r = & $pkg @('commit', '-Id', 'rv')
    & $expect ($r.Exit -ne 0 -and $r.Text -match 'fresh passing check' -and $r.Text -match 'stale' -and (& $stateFile 'rv').stage -eq 'checks-passed' -and @(Get-List (& $stateFile 'rv').commits).Count -eq 0) "commit accepted a stale check: $($r.Text)"
    Remove-Item -LiteralPath (Join-Path (& $wtOf 'rv') 'stale.txt')
    $r = & $pkg @('commit', '-Id', 'rv', '-Message', 'Add the feature')
    $state = & $stateFile 'rv'
    $head1 = & $headOf 'rv'
    $runIdRv = [string]$state.runs[-1].runId
    $body = (& git -C (& $wtOf 'rv') log -1 --format=%B) -join "`n"
    & $expect ($r.Exit -eq 0 -and $state.stage -eq 'committed' -and $state.commit.sha -eq $head1 -and $state.commit.tree -eq (& git -C (& $wtOf 'rv') rev-parse 'HEAD^{tree}').Trim() -and $state.commit.rebaseChangedTree -eq $false) "commit failed or did not record the commit: $($r.Text)"
    & $expect ($body -match 'Add the feature' -and $body -match "Package: rv run $runIdRv" -and (& git -C (& $wtOf 'rv') log -1 --format=%an).Trim() -eq 'SelfTest') "the commit lacks the message, the Package trailer or the configured author: $body"
    & $expect (Test-WorktreeClean (& $wtOf 'rv')) 'the worktree is not clean after commit'

    # A rebase that changes the tree re-runs check automatically; a failing re-check sends the stage back to checks-failed.
    [IO.File]::WriteAllText((Join-Path $seed 'c.txt'), "c`r`n"); & $git $seed @('add', '-A'); & $git $seed @('commit', '-qm', 'three'); & $git $seed @('push', '-q', 'origin', 'master')
    & $write 'rv' 'feature2.txt' "more`r`n"
    $r = & $pkg @('check', '-Id', 'rv')
    $checksBefore = @((& $stateFile 'rv').checks).Count
    $r = & $pkg @('commit', '-Id', 'rv', '-Message', 'More')
    $state = & $stateFile 'rv'
    & $expect ($r.Exit -eq 0 -and $state.stage -eq 'committed' -and $state.commit.rebaseChangedTree -eq $true -and $state.commit.checkedAfterRebase -eq $true -and @($state.checks).Count -eq $checksBefore + 1 -and (Test-Path (Join-Path $state.worktree 'c.txt')) -and $r.Text -match 're-running check') "the rebase that changed the tree did not re-run check: $($r.Text)"
    & $expect ($state.commit.sha -eq (& $headOf 'rv') -and $state.baseSha -eq (& git -C $main rev-parse origin/master).Trim()) 'the commit after the rebase was not recorded with the new base'
    $r = & $pkg @('commit', '-Id', 'rv')
    & $expect ($r.Exit -ne 0 -and $r.Text -match "needs 'checks-passed'") 'a second commit was allowed without a new check'

    $r = & $pkg @('new', '-Id', 'rbf', '-Brief', $briefRv, '-Checks', $recheckManifest)
    $r = & $pkg @('run', '-Id', 'rbf', '-AgentRunScript', (Join-Path $stubDir 'ok.ps1'))
    & $write 'rbf' 'f.txt' "f`r`n"
    $r = & $pkg @('check', '-Id', 'rbf')
    [IO.File]::WriteAllText((Join-Path $seed 'c2.txt'), "c2`r`n"); & $git $seed @('add', '-A'); & $git $seed @('commit', '-qm', 'four'); & $git $seed @('push', '-q', 'origin', 'master')
    $r = & $pkg @('commit', '-Id', 'rbf')
    $state = & $stateFile 'rbf'
    & $expect ($r.Exit -eq 1 -and $state.stage -eq 'checks-failed' -and $state.commit.sha -and $state.commit.checkedAfterRebase -eq $false -and $r.Text -match 'check after the rebase failed') "a failing re-check after the rebase did not send the package back to checks-failed: $($r.Text)"
    $r = & $pkg @('review', '-Id', 'rbf', '-Level', 'light', '-ClaudePath', $rvClaude)
    & $expect ($r.Exit -ne 0 -and $r.Text -match "not allowed in stage 'checks-failed'") "a review was allowed on a failed check: $($r.Text)"
    $r = & $pkg @('commit', '-Id', 'mut')
    & $expect ($r.Exit -ne 0 -and $r.Text -match "not allowed in stage 'blocked-main-mutated'") "commit was allowed with a dirty main-checkout guard: $($r.Text)"

    # Reviews: preconditions that do not spend a round.
    $r = & $pkg @('review', '-Id', 'rv', '-ClaudePath', $rvClaude)
    & $expect ($r.Exit -ne 0 -and $r.Text -match '-Level') "review without -Level was accepted: $($r.Text)"
    $r = & $pkg @('review', '-Id', 'rv', '-Level', 'light', '-Reviewer', 'codex-luna', '-CodexTestScript', $rvCodex)
    & $expect ($r.Exit -ne 0 -and $r.Text -match 'cannot review this package' -and (& $stateFile 'rv').repair.roundsUsed -eq 0) "a reviewer equal to the executor model was accepted: $($r.Text)"
    & $write 'rv' 'loose.txt' "uncommitted`r`n"
    $r = & $pkg @('review', '-Id', 'rv', '-Level', 'light', '-ClaudePath', $rvClaude)
    & $expect ($r.Exit -ne 0 -and $r.Text -match 'uncommitted changes' -and (& $stateFile 'rv').repair.roundsUsed -eq 0) "a review started on a dirty worktree: $($r.Text)"
    Remove-Item -LiteralPath (Join-Path (& $wtOf 'rv') 'loose.txt')
    $r = & $pkg @('accept', '-Id', 'rv', '-Reason', 'too early')
    & $expect ($r.Exit -ne 0 -and $r.Text -match "needs a valid PASS review" ) "accept was allowed without a review: $($r.Text)"

    # Round 1: the default reviewer for a Codex executor is Claude; the verdict FAILs with a blocking finding.
    & $resetR
    $env:POINTFRAME_PKG_TEST_VMODE = 'fail'
    $r = & $pkg @('review', '-Id', 'rv', '-Level', 'light', '-ClaudePath', $rvClaude)
    $state = & $stateFile 'rv'
    $rev = Get-LatestReview $state
    $head2 = & $headOf 'rv'
    & $expect ($r.Exit -eq 1 -and $state.stage -eq 'review-failed' -and $rev.status -eq 'valid' -and $rev.verdict -eq 'FAIL' -and $rev.blockingFindings -eq 1 -and @($rev.findings).Count -eq 2 -and $rev.reviewer.kind -eq 'claude-sonnet' -and $rev.reviewer.sessionId -eq 'stub-claude-review' -and $state.repair.roundsUsed -eq 1) "round 1 was not recorded as a valid FAIL: $($r.Text)"
    & $expect ($rev.commit -eq $head2 -and $rev.tree -eq (& git -C (& $wtOf 'rv') rev-parse 'HEAD^{tree}').Trim() -and $rev.verdictFile.sha256 -eq (Get-Sha256 $rev.verdictFile.path) -and (Test-Path $rev.finalMessage.path) -and $rev.briefSha256 -eq $state.brief.sha256 -and $rev.criteriaResults.Count -eq 3) 'the review does not record the reviewed commit, tree, verdict hash and criteria'
    $calls1 = @(& $rcalls)
    $c1 = if ($calls1.Count) { & $readCall $calls1[0] } else { $null }
    & $expect ($calls1.Count -eq 1 -and $c1 -and [IO.Path]::GetFullPath([string]$c1.cwd).TrimEnd('\') -ieq [IO.Path]::GetFullPath([string]$rev.snapshot.path).TrimEnd('\') -and [string]$rev.snapshot.path -like "*review-$($rev.reviewId)" -and [string]$rev.snapshot.path -notlike "$($state.worktree)*") 'the Claude reviewer did not run in its own review snapshot (never the package worktree)'
    & $expect ($rev.snapshot.created -and $rev.snapshot.removed -and -not (Test-Path $rev.snapshot.path) -and $rev.snapshot.startObservation.tree -eq $rev.tree -and $rev.snapshot.endObservation.tree -eq $rev.tree -and $rev.snapshot.startObservation.contentTree -eq $rev.snapshot.endObservation.contentTree -and $rev.snapshot.startObservation.clean -and -not $rev.snapshot.removeError) 'the review snapshot was not observed at both ends and removed'
    & $expect ((Test-Path (Join-Path $rev.snapshot.inputDir 'log.txt')) -and (Test-Path (Join-Path $rev.snapshot.inputDir 'stat.txt')) -and (Test-Path (Join-Path $rev.snapshot.inputDir 'brief.md')) -and ((Get-Content -LiteralPath (Join-Path $rev.snapshot.inputDir 'diff.patch') -Raw) -match 'feature\.txt') -and $rev.baseSha -eq $state.baseSha -and (Get-Sha256 $rev.fileHashes.path) -eq $rev.fileHashes.sha256) 'the host did not prepare the review inputs (log, stat, diff, brief) and the per-file fingerprint'
    $c1args = @($c1.args)
    & $expect ($c1args[[Array]::IndexOf($c1args, '--tools') + 1] -eq 'Read,Glob,Grep' -and $c1args[[Array]::IndexOf($c1args, '--add-dir') + 1] -eq $rev.snapshot.inputDir -and -not ($c1args -match '(?i)bypass|dangerously|permission-mode') -and $c1args[[Array]::IndexOf($c1args, '--setting-sources') + 1] -eq '' -and $c1args -notcontains '--resume') 'the Claude reviewer arguments are wrong'
    $s1 = $c1.settings | ConvertFrom-Json
    & $expect ((@($s1.permissions.allow) -join ',') -eq 'Read,Glob,Grep' -and -not (@($s1.permissions.allow) -match 'Bash') -and $s1.permissions.deny -contains 'Bash' -and $s1.permissions.deny -contains 'Edit' -and $s1.permissions.deny -contains 'Write') 'the Claude reviewer is not limited to Read, Glob and Grep'
    & $expect ($c1.stdin -match "Reviewed commit: $head2" -and $c1.stdin -match "Reviewed tree: $($rev.tree)" -and $c1.stdin -match [regex]::Escape([string]$rev.snapshot.path) -and $c1.stdin -match [regex]::Escape((Join-Path $rev.snapshot.inputDir 'brief.md')) -and $c1.stdin -match 'diff\.patch' -and $c1.stdin -match '(?m)^C1: The feature file exists\.' -and $c1.stdin -match '(?m)^C3: Nothing else changes\.' -and $c1.stdin -match [regex]::Escape($rev.verdictFile.path) -and $c1.stdin -match '"schemaVersion"') 'the review prompt lacks the commit, tree, worktree, brief, criteria, verdict path or schema'
    & $expect ((Test-WorktreeClean (& $wtOf 'rv'))) 'the worktree is not clean after the review'

    # Not a PASS: accept is refused; fix needs a disposition for every blocking finding; rejecting all leaves nothing to fix.
    $r = & $pkg @('accept', '-Id', 'rv', '-Reason', 'no')
    & $expect ($r.Exit -ne 0 -and $r.Text -match "needs a valid PASS review" -and (& $stateFile 'rv').stage -eq 'review-failed') "accept was allowed after a FAIL: $($r.Text)"
    $runsBefore = @((& $stateFile 'rv').runs).Count
    $r = & $pkg @('fix', '-Id', 'rv', '-AgentRunScript', $fixerCodex, '-ClaudePath', $rvClaude)
    & $expect ($r.Exit -ne 0 -and $r.Text -match 'without a disposition: F1' -and @((& $stateFile 'rv').runs).Count -eq $runsBefore) "fix ran with an undecided blocking finding: $($r.Text)"
    $r = & $pkg @('disposition', '-Id', 'rv', '-Finding', 'F1', '-Reason', 'x')
    & $expect ($r.Exit -ne 0 -and $r.Text -match 'exactly one') 'a disposition without -Accept or -Reject was accepted'
    $r = & $pkg @('disposition', '-Id', 'rv', '-Finding', 'F1', '-Accept', '-Reject', '-Reason', 'x')
    & $expect ($r.Exit -ne 0 -and $r.Text -match 'exactly one') 'a disposition with both flags was accepted'
    $r = & $pkg @('disposition', '-Id', 'rv', '-Finding', 'F1', '-Accept')
    & $expect ($r.Exit -ne 0 -and $r.Text -match 'needs -Reason' -and $null -eq @((Get-LatestReview (& $stateFile 'rv')).findings)[0].disposition) 'a disposition without a reason was accepted'
    $r = & $pkg @('disposition', '-Id', 'rv', '-Finding', 'F9', '-Accept', '-Reason', 'x')
    & $expect ($r.Exit -ne 0 -and $r.Text -match "no finding 'F9'") 'a disposition for an unknown finding was accepted'
    $r = & $pkg @('disposition', '-Id', 'rv', '-Finding', 'F1', '-Reject', '-Reason', 'disputed: the brief allows v1')
    & $expect ($r.Exit -eq 0) "reject failed: $($r.Text)"
    $r = & $pkg @('fix', '-Id', 'rv', '-AgentRunScript', $fixerCodex, '-ClaudePath', $rvClaude)
    & $expect ($r.Exit -ne 0 -and $r.Text -match 'every blocking finding was rejected' -and @((& $stateFile 'rv').runs).Count -eq $runsBefore) "fix ran although every blocking finding was rejected: $($r.Text)"
    $r = & $pkg @('disposition', '-Id', 'rv', '-Finding', 'F1', '-Accept', '-Reason', 'the reviewer is right: v2 is required')
    $f1 = @((Get-LatestReview (& $stateFile 'rv')).findings | Where-Object { $_.id -eq 'F1' })[0]
    & $expect ($r.Exit -eq 0 -and $f1.disposition.decision -eq 'accept' -and $f1.disposition.reason -match 'v2 is required' -and @($f1.dispositionHistory).Count -eq 2) "dispositions were not kept with reasons and history: $($r.Text)"
    $shown = (& $pkg @('show', '-Id', 'rv')).Text
    & $expect ($shown -match 'F1 \[blocking\].*=> accept: the reviewer is right' -and $shown -match 'repair rounds used 1 of 3') 'show does not list the findings with their dispositions and the budget'

    # fix: the same (Codex) executor runs again with the previous report appended, then check, commit and a resumed review.
    & $resetR
    $env:POINTFRAME_PKG_TEST_VMODE = 'pass'
    $r = & $pkg @('fix', '-Id', 'rv', '-AgentRunScript', $fixerCodex, '-ClaudePath', $rvClaude)
    $state = & $stateFile 'rv'
    $rev2 = Get-LatestReview $state
    $head3 = & $headOf 'rv'
    & $expect ($r.Exit -eq 0 -and $state.stage -eq 'review-passed' -and @($state.runs).Count -eq $runsBefore + 1 -and $state.runs[-1].outcome -eq 'completed' -and $head3 -ne $head2 -and $rev2.commit -eq $head3 -and $rev2.verdict -eq 'PASS' -and $rev2.status -eq 'valid' -and $rev2.round -eq 2 -and $state.repair.roundsUsed -eq 2 -and $null -eq $state.pendingFix) "fix did not run executor, check, commit and review: $($r.Text)"
    & $expect ($state.runs[-1].effort -eq 'high' -and $state.runs[-1].escalationReason -eq 'selftest' -and $state.runs[-1].model -eq 'gpt-6-luna') 'fix did not restore the model, effort and escalation reason of the run it continued'
    $fixBrief = if (Test-Path (Join-Path $rrecordDir 'fix-brief.md')) { Get-Content -LiteralPath (Join-Path $rrecordDir 'fix-brief.md') -Raw } else { '' }
    & $expect ($fixBrief -match '# Previous report' -and $fixBrief -match 'it broke|done|fixed|ok' -and $fixBrief -match 'F1 \(blocking\) feature\.txt:1' -and $fixBrief -match 'the reviewer is right: v2 is required' -and $fixBrief -notmatch 'disputed') 'the Codex fix brief lacks the previous report or the accepted finding'
    $calls2 = @(& $rcalls)
    $c2 = if ($calls2.Count -ge 1) { & $readCall $calls2[-1] } else { $null }
    $c2args = @($c2.args)
    & $expect ($calls2.Count -eq 1 -and $c2args[[Array]::IndexOf($c2args, '--resume') + 1] -eq 'stub-claude-review' -and $rev2.reviewer.resumedFrom -eq 'stub-claude-review' -and $c2.stdin -match 'COMPLETE, CURRENT verdict' -and $c2.stdin -match 'Earlier finding F1' -and $c2.stdin -match 'Earlier finding F2' -and $c2.stdin -match "Reviewed commit: $head3") "the reviewer was not resumed with a request for a complete, current verdict: $($c2args -join ' ')"
    & $expect ($rev2.criteriaResults.Count -eq 3 -and @($rev2.criteriaResults | Where-Object { $_.result -ne 'pass' }).Count -eq 0) 'the resumed verdict does not cover every criterion'

    # accept: a separate recorded step; the record-review values come from the state.
    $r = & $pkg @('accept', '-Id', 'rv')
    & $expect ($r.Exit -ne 0 -and $r.Text -match 'needs -Reason') 'accept without a reason was allowed'
    $r = & $pkg @('accept', '-Id', 'rv', '-Reason', 'criteria verified against the diff')
    $state = & $stateFile 'rv'
    $recordFile = Join-Path $main ".git/pointframe/reviews/$head3.json"
    $record = if (Test-Path $recordFile) { Get-Content -LiteralPath $recordFile -Raw | ConvertFrom-Json } else { $null }
    & $expect ($r.Exit -eq 0 -and $state.stage -eq 'accepted' -and $state.acceptance.reason -eq 'criteria verified against the diff' -and $state.acceptance.commit -eq $head3 -and $record) "accept failed: $($r.Text)"
    & $expect ($record.headSha -eq $head3 -and $record.treeSha -eq $rev2.tree -and $record.executor -eq 'codex:gpt-6-luna' -and $record.executorSession -eq 'stub-session' -and $record.reviewer -eq 'claude:claude-sonnet-5-5' -and $record.reviewerSession -eq 'stub-claude-review' -and $record.verdict -eq 'PASS' -and $record.blockingFindings -eq 0 -and $record.review -eq 'light' -and $record.evidence -eq $rev2.verdictFile.path -and $record.briefSha256 -eq $state.brief.sha256) "the review record was not written from the state's values: $($record | ConvertTo-Json -Compress)"
    $r = & $pkg @('accept', '-Id', 'rv', '-Reason', 'again')
    & $expect ($r.Exit -ne 0 -and $r.Text -match 'already accepted') 'a second accept was allowed'

    # A later change to the head invalidates the PASS and the acceptance; only a pin-files-only commit keeps them.
    $rvWt = & $wtOf 'rv'
    $commitFile = { param($name, $text) [IO.File]::WriteAllText((Join-Path $rvWt $name), $text); & $git $rvWt @('add', '-A'); & $git $rvWt @('commit', '-qm', "touch $name") }
    & $writePins $rvWt '1.0.0'; & $git $rvWt @('add', '-A'); & $git $rvWt @('commit', '-qm', 'pin both files')
    $view = (& $pkg @('show', '-Id', 'rv', '-Json')).Text | ConvertFrom-Json
    & $expect ($view.acceptance.status -match '^valid' -and $view.acceptance.status -match 'plugin pin') "a plugin-pin-only commit invalidated the acceptance: $($view.acceptance.status)"
    & $writePins $rvWt '1.0.1'; & $git $rvWt @('add', '-A'); & $git $rvWt @('commit', '-qm', 'pin bump')
    $view = (& $pkg @('show', '-Id', 'rv', '-Json')).Text | ConvertFrom-Json
    & $expect ($view.acceptance.status -match '^valid') "both plugin pin files together invalidated the acceptance: $($view.acceptance.status)"
    & $commitFile 'other.txt' "not a pin`r`n"
    $view = (& $pkg @('show', '-Id', 'rv', '-Json')).Text | ConvertFrom-Json
    & $expect ($view.acceptance.status -match '^invalidated' -and $view.acceptance.status -match 'other\.txt') "a later commit did not invalidate the acceptance: $($view.acceptance.status)"
    $r = & $pkg @('review', '-Id', 'rv', '-Level', 'light', '-ClaudePath', $rvClaude)
    & $expect ($r.Exit -ne 0 -and $r.Text -match 'invalidated' -and (& $stateFile 'rv').stage -eq 'invalidated' -and (& $stateFile 'rv').acceptance.invalidated.reason -match 'other\.txt') "the invalidation was not recorded in the state: $($r.Text)"
    $r = & $pkg @('accept', '-Id', 'rv', '-Reason', 'late')
    & $expect ($r.Exit -ne 0 -and $r.Text -match "stage 'invalidated'") "accept was allowed after the head changed: $($r.Text)"
    $r = & $pkg @('show', '-Id', 'rv')
    & $expect ($r.Text -match 'next: check' ) 'show does not point an invalidated package at check'
    # A pin commit mixed with another file is not the exception.
    $r = & $pkg @('new', '-Id', 'mix', '-Brief', $briefRv, '-Checks', $rvManifest)
    $r = & $pkg @('run', '-Id', 'mix', '-AgentRunScript', (Join-Path $stubDir 'ok.ps1'))
    & $write 'mix' 'feature.txt' "v2`r`n"
    $r = & $pkg @('check', '-Id', 'mix'); $r = & $pkg @('commit', '-Id', 'mix')
    $env:POINTFRAME_PKG_TEST_VMODE = 'pass'
    $r = & $pkg @('review', '-Id', 'mix', '-Level', 'light', '-ClaudePath', $rvClaude)
    $mixState = & $stateFile 'mix'
    & $expect ($r.Exit -eq 0 -and $mixState.stage -eq 'review-passed') "review of mix did not pass: $($r.Text)"
    $mixWt = & $wtOf 'mix'
    New-Item -ItemType Directory -Path (Join-Path $mixWt 'plugin/pointframe') -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $mixWt 'plugin/pointframe/server.lock.json'), "{`"v`":2}`r`n"); [IO.File]::WriteAllText((Join-Path $mixWt 'extra.txt'), "x`r`n")
    & $git $mixWt @('add', '-A'); & $git $mixWt @('commit', '-qm', 'pin plus extra')
    $r = & $pkg @('accept', '-Id', 'mix', '-Reason', 'x')
    & $expect ($r.Exit -ne 0 -and (& $stateFile 'mix').stage -eq 'invalidated') "a pin commit with another file kept the PASS: $($r.Text)"

    # ---- package vd: invalid verdicts, the repair budget across restarts, needs-coordinator, resetting it ----
    & $newPackage 'vd'
    & $write 'vd' 'feature.txt' "v2`r`n"
    $r = & $pkg @('check', '-Id', 'vd'); $r = & $pkg @('commit', '-Id', 'vd')
    $vdHead = & $headOf 'vd'
    $review = { param($mode, [string[]]$more = @()) $env:POINTFRAME_PKG_TEST_VMODE = $mode; & $pkg (@('review', '-Id', 'vd', '-Level', 'light', '-ClaudePath', $rvClaude) + $more) }
    $r = & $review 'stale'
    $state = & $stateFile 'vd'; $rev = Get-LatestReview $state
    & $expect ($r.Exit -eq 1 -and $rev.status -eq 'invalid' -and $null -eq $rev.verdict -and ($rev.invalidReasons -join ' ') -match 'not the host-observed head' -and $state.stage -eq 'review-invalid' -and $r.Text -match 'INVALID') "a verdict for another commit was not invalid: $($r.Text)"
    $r = & $review 'missingcrit'
    $rev = Get-LatestReview (& $stateFile 'vd')
    & $expect ($rev.status -eq 'invalid' -and ($rev.invalidReasons -join ' ') -match 'criterion C3 of the brief is missing') "a verdict without every criterion was not invalid: $($r.Text)"
    $r = & $review 'passblock'
    $state = & $stateFile 'vd'; $rev = Get-LatestReview $state
    & $expect ($rev.status -eq 'invalid' -and ($rev.invalidReasons -join ' ') -match 'PASS with 1 blocking' -and $state.stage -eq 'needs-coordinator' -and $state.repair.roundsUsed -eq 3 -and $r.Text -match 'repair budget is used up') "a PASS with a blocking finding was accepted or the budget did not run out: $($r.Text)"
    $callCount = @(& $rcalls).Count
    $r = & $review 'pass'
    & $expect ($r.Exit -ne 0 -and $r.Text -match 'repair budget is used up' -and (& $stateFile 'vd').repair.roundsUsed -eq 3 -and (& $stateFile 'vd').stage -eq 'needs-coordinator' -and @(& $rcalls).Count -eq $callCount) "a fourth round was started after the budget ran out (a restart does not reset it): $($r.Text)"
    $r = & $pkg @('show', '-Id', 'vd')
    & $expect ($r.Text -match 'repair rounds used 3 of 3' -and $r.Text -match 'review -ResetBudget') 'show does not report the exhausted budget and the way out'
    $r = & $review 'pass' @('-ResetBudget')
    & $expect ($r.Exit -ne 0 -and $r.Text -match '-ResetBudget needs -Reason' -and (& $stateFile 'vd').repair.roundsUsed -eq 3) "a budget reset without a reason was accepted: $($r.Text)"
    $r = & $review 'malformed' @('-ResetBudget', '-Reason', 'coordinator reassessed the findings')
    $state = & $stateFile 'vd'; $rev = Get-LatestReview $state
    & $expect ($state.repair.roundsUsed -eq 1 -and @($state.repair.resets).Count -eq 1 -and $state.repair.resets[0].reason -match 'reassessed' -and $state.repair.resets[0].roundsUsedBefore -eq 3 -and @($state.history | Where-Object { $_.event -eq 'budget-reset' }).Count -eq 1 -and $rev.status -eq 'invalid' -and ($rev.invalidReasons -join ' ') -match 'not a JSON document') "the reset was not recorded or a truncated verdict was accepted: $($r.Text)"
    $r = & $review 'prose'
    $rev = Get-LatestReview (& $stateFile 'vd')
    & $expect ($rev.status -eq 'invalid' -and ($rev.invalidReasons -join ' ') -match 'not a JSON document' -and $null -eq $rev.verdict) "a free-text verdict was accepted: $($r.Text)"
    $r = & $review 'dirty'
    $state = & $stateFile 'vd'; $rev = Get-LatestReview $state
    Remove-Item -LiteralPath (Join-Path (& $wtOf 'vd') 'dirty-by-reviewer.txt') -Force -ErrorAction SilentlyContinue
    & $expect ($rev.status -eq 'invalid' -and ($rev.invalidReasons -join ' ') -match 'review snapshot changed' -and $state.repair.roundsUsed -eq 3 -and $state.stage -eq 'needs-coordinator') "a reviewer that dirtied the worktree was not invalid: $($r.Text)"
    $r = & $review 'sleep' @('-ResetBudget', '-Reason', 'second reset', '-MaxMinutes', '0.05')
    $rev = Get-LatestReview (& $stateFile 'vd')
    & $expect ($rev.status -eq 'invalid' -and $rev.termination -eq 'timeout' -and ($rev.invalidReasons -join ' ') -match 'timeout' -and @((& $stateFile 'vd').repair.resets).Count -eq 2) "a timed-out reviewer was not invalid: $($r.Text)"
    $r = & $review 'denied'
    $rev = Get-LatestReview (& $stateFile 'vd')
    & $expect ($rev.status -eq 'invalid' -and ($rev.invalidReasons -join ' ') -match 'denied 1 tool call') "a reviewer with a denied tool call produced a valid verdict: $($r.Text)"
    $r = & $review 'empty'
    $rev = Get-LatestReview (& $stateFile 'vd')
    & $expect ($rev.status -eq 'invalid' -and ($rev.invalidReasons -join ' ') -match 'verdict is empty') "an empty verdict was accepted: $($r.Text)"

    # An abandoned review (the wrapper died) is recovered as invalid, keeps its round, and blocks nothing else.
    $deadId = $deadWrapper.Id
    Update-State $packagesRoot 'vd' 'test-crash-review' {
        param($s)
        $s.stage = 'reviewing'
        $s.reviews = @(Get-List $s.reviews) + @(@{ reviewId = 'abandoned'; round = 9; level = 'light'; status = 'running'; verdict = $null; reviewer = @{ kind = 'claude-sonnet'; model = 'x'; sessionId = $null; resumedFrom = $null }; findings = @(); invalidReasons = @(); verdictFile = @{ path = $null }; termination = 'running'; blockingFindings = 0; commit = 'x'; wrapper = @{ pid = $deadId; startUtc = '2000-01-01T00:00:00.0000000+00:00' } })
    } | Out-Null
    $r = & $review 'fenced' @('-ResetBudget', '-Reason', 'third reset')
    $state = & $stateFile 'vd'
    $abandoned = @($state.reviews | Where-Object { $_.reviewId -eq 'abandoned' })[0]
    $final = Get-LatestReview $state
    & $expect ($abandoned.status -eq 'invalid' -and $abandoned.termination -eq 'abandoned' -and $r.Exit -eq 0 -and $final.status -eq 'valid' -and $final.verdict -eq 'PASS' -and $state.stage -eq 'review-passed') "the abandoned review was not recovered, or a fenced PASS was refused: $($r.Text)"
    & $expect ($final.reviewer.resumedFrom -eq 'stub-claude-review') 'a later round did not resume the reviewer session'

    # ---- package cx: Claude executor, Codex reviewer (read-only, heavy default), Claude resume fix, Codex resume ----
    $r = & $pkg @('new', '-Id', 'cx', '-Brief', $briefRv, '-Checks', $rvManifest)
    & $resetRecords
    $r = & $pkg @('run', '-Id', 'cx', '-Executor', 'claude-sonnet', '-ClaudePath', (& $stub 'c-ok'))
    & $expect ($r.Exit -eq 0) "cx executor run failed: $($r.Text)"
    & $write 'cx' 'feature.txt' "v1`r`n"
    $r = & $pkg @('check', '-Id', 'cx'); $r = & $pkg @('commit', '-Id', 'cx')
    $r = & $pkg @('review', '-Id', 'cx', '-Level', 'light', '-Reviewer', 'claude-sonnet', '-ClaudePath', $rvClaude)
    & $expect ($r.Exit -ne 0 -and $r.Text -match 'cannot review this package' -and (& $stateFile 'cx').repair.roundsUsed -eq 0) "a Claude reviewer of the same model as the Claude executor was accepted: $($r.Text)"
    $r = & $pkg @('review', '-Id', 'cx', '-Level', 'heavy', '-Reviewer', 'codex-luna', '-CodexTestScript', $rvCodex)
    & $expect ($r.Exit -ne 0 -and $r.Text -match 'needs a heavy reviewer' -and (& $stateFile 'cx').repair.roundsUsed -eq 0) "a heavy review was allowed with a light reviewer: $($r.Text)"
    $r = & $pkg @('review', '-Id', 'cx', '-Level', 'heavy', '-Reviewer', 'claude-sonnet', '-ClaudePath', $rvClaude)
    & $expect ($r.Exit -ne 0 -and $r.Text -match 'needs a heavy reviewer') "a heavy review was allowed with claude-sonnet: $($r.Text)"
    & $resetR
    $env:POINTFRAME_PKG_TEST_VMODE = 'fail'
    $r = & $pkg @('review', '-Id', 'cx', '-Level', 'heavy', '-CodexTestScript', $rvCodex)
    $state = & $stateFile 'cx'; $rev = Get-LatestReview $state
    $cc = @(& $rcalls)
    $cx1 = if ($cc.Count) { & $readCall $cc[0] } else { $null }
    $cxArgs = @($cx1.args)
    & $expect ($r.Exit -eq 1 -and $rev.reviewer.kind -eq 'codex-sol' -and $rev.reviewer.model -eq 'gpt-6.1-sol' -and $rev.reviewer.sessionId -eq 'codex-rv-session' -and $rev.status -eq 'valid' -and $rev.verdict -eq 'FAIL' -and $state.stage -eq 'review-failed') "the Codex heavy review of a Claude executor failed: $($r.Text)"
    & $expect ($cxArgs -contains 'read-only' -and $cxArgs -notcontains 'workspace-write' -and $cxArgs -contains 'gpt-6.1-sol' -and $cxArgs -contains 'model_reasoning_effort=high' -and $cx1.stdin -match "Reviewed commit: $($rev.commit)") "the Codex reviewer was not run read-only on the reviewed commit: $($cxArgs -join ' ')"
    $r = & $pkg @('disposition', '-Id', 'cx', '-Finding', 'F1', '-Accept', '-Reason', 'right')
    & $resetRecords
    $env:POINTFRAME_PKG_TEST_VMODE = 'pass'
    $r = & $pkg @('fix', '-Id', 'cx', '-ClaudePath', $fixerClaude, '-CodexTestScript', $rvCodex)
    $state = & $stateFile 'cx'; $rev = Get-LatestReview $state
    $fixCalls = @(& $calls)
    $fixCall = if ($fixCalls.Count) { Get-Content -LiteralPath $fixCalls[0].FullName -Raw | ConvertFrom-Json } else { $null }
    $fixArgs = @($fixCall.args)
    $cc = @(& $rcalls)
    $cx2 = if ($cc.Count -ge 2) { & $readCall $cc[-1] } else { $null }
    $cx2Args = @($cx2.args)
    & $expect ($r.Exit -eq 0 -and $state.stage -eq 'review-passed' -and $fixCall -and $fixArgs[[Array]::IndexOf($fixArgs, '--resume') + 1] -eq 'stub-claude-session' -and $fixCall.stdin -match 'F1 \(blocking\)' -and $state.runs[-1].resumedFrom.sessionId -eq 'stub-claude-session') "a Claude executor was not resumed with the accepted finding: $($r.Text)"
    & $expect ($cc.Count -eq 2 -and $cx2Args -contains 'resume' -and $cx2Args -contains 'codex-rv-session' -and ($cx2Args -join ' ') -match 'sandbox_mode="?read-only' -and $rev.reviewer.resumedFrom -eq 'codex-rv-session' -and $rev.round -eq 2) "the Codex reviewer was not resumed read-only: $($cx2Args -join ' ')"
    $r = & $pkg @('accept', '-Id', 'cx', '-Reason', 'codex review verified')
    $state = & $stateFile 'cx'
    $cxHead = & $headOf 'cx'
    $cxRecordFile = Join-Path $main ".git/pointframe/reviews/$cxHead.json"
    $cxRecord = if (Test-Path $cxRecordFile) { Get-Content -LiteralPath $cxRecordFile -Raw | ConvertFrom-Json } else { $null }
    & $expect ($r.Exit -eq 0 -and $state.stage -eq 'accepted' -and $cxRecord.executor -eq 'claude:claude-sonnet-5-5' -and $cxRecord.executorSession -eq 'stub-claude-session' -and $cxRecord.reviewer -eq 'codex:gpt-6.1-sol' -and $cxRecord.reviewerSession -eq 'codex-rv-session' -and $cxRecord.review -eq 'heavy') "accept of the Codex-reviewed package failed: $($r.Text) $($cxRecord | ConvertTo-Json -Compress)"

    # ---- duplicate JSON properties are refused before deserialization can drop one ----
    $validText = (& $validDoc) | ConvertTo-Json -Depth 6
    & $expect ((Test-VerdictDocument $validText $unit.criteria $sha $tre $true).ok) 'the control verdict text was refused'
    $failDoc = & $validDoc
    $failDoc.verdict = 'FAIL'; $failDoc.criteria[1].result = 'fail'; $failDoc.findings = @(& $sampleFinding)
    $failText = $failDoc | ConvertTo-Json -Depth 6
    & $expect ((Test-VerdictDocument $failText $unit.criteria $sha $tre $true).ok) 'the control FAIL verdict text was refused'
    $dupCases = @(
        @{ name = 'duplicate verdict'; text = $validText.Replace('"verdict": "PASS"', '"verdict": "FAIL", "verdict": "PASS"'); prop = 'verdict' }
        @{ name = 'duplicate verdict (escaped name)'; text = $validText.Replace('"verdict": "PASS"', '"verdict": "FAIL", "\u0076erdict": "PASS"'); prop = 'verdict' }
        @{ name = 'duplicate criteria'; text = $validText.Replace('"criteria": [', '"criteria": [], "criteria": ['); prop = 'criteria' }
        @{ name = 'duplicate inside a finding'; text = $failText.Replace('"severity": "blocking"', '"severity": "non-blocking", "severity": "blocking"'); prop = 'severity' }
        @{ name = 'duplicate inside a criterion'; text = $validText.Replace('"result": "pass"', '"result": "fail", "result": "pass"'); prop = 'result' }
    )
    foreach ($case in $dupCases)
    {
        $source = if ($case.name -like '*finding*') { $failText } else { $validText }
        $res = Test-VerdictDocument $case.text $unit.criteria $sha $tre $true
        & $expect ($case.text -ne $source -and -not $res.ok -and ($res.problems -join ' ') -match "repeats the property '$($case.prop)'") "'$($case.name)' was not refused: ok=$($res.ok) $($res.problems -join '; ')"
    }
    # A tokenizer error is never "no duplicate": comments and trailing commas make the verdict invalid.
    $dupText = $validText.Replace('"verdict": "PASS"', '"verdict": "FAIL", "verdict": "PASS"')
    $strictCases = @(
        @{ name = 'comment-prefixed duplicate verdict'; text = "/* note */ $dupText" }
        @{ name = 'line comment before the document'; text = "// note`n$validText" }
        @{ name = 'comment inside the document'; text = $validText.Replace('"verdict": "PASS"', '"verdict": /* inline */ "PASS"') }
        @{ name = 'trailing comma'; text = ($validText -replace '\s*\}\s*$', ', }') }
        @{ name = 'trailing comma in an array'; text = $validText.Replace('"omissions": []', '"omissions": ["a",]') }
    )
    foreach ($case in $strictCases)
    {
        $res = Test-VerdictDocument $case.text $unit.criteria $sha $tre $true
        & $expect ($case.text -ne $validText -and -not $res.ok -and ($res.problems -join ' ') -match 'not a JSON document' -and ($res.problems -join ' ') -match 'not strict JSON') "'$($case.name)' was not refused as not strict JSON: ok=$($res.ok) $($res.problems -join '; ')"
    }
    $guarded = $false
    try { Get-CodexCliVersion | Out-Null } catch { $guarded = $_.Exception.Message -match 'self-test guard' }
    & $expect $guarded 'the self-test would have probed the real codex --version'
    $guarded = $false
    try { Assert-SelfTestLauncher ([pscustomobject]@{ Stub = $false; Display = 'C:\Windows\System32\notepad.exe' }) } catch { $guarded = $_.Exception.Message -match 'self-test guard' }
    & $expect $guarded 'the self-test would have started a launcher that is not a marked stub'

    # ---- package pn: the plugin-pin exception cannot hide a rename or a hidden edit ----
    & $newPackage 'pn'
    & $write 'pn' 'feature.txt' "v2`r`n"
    $r = & $pkg @('check', '-Id', 'pn'); $r = & $pkg @('commit', '-Id', 'pn')
    $env:POINTFRAME_PKG_TEST_VMODE = 'pass'
    $r = & $pkg @('review', '-Id', 'pn', '-Level', 'light', '-ClaudePath', $rvClaude)
    $pnState = & $stateFile 'pn'; $pnRev = Get-LatestReview $pnState; $pnWt = [string]$pnState.worktree; $pnCommit = [string]$pnRev.commit
    & $expect ($r.Exit -eq 0 -and $pnState.stage -eq 'review-passed') "review of pn did not pass: $($r.Text)"
    $binding = Get-ReviewBinding $pnState $pnRev
    & $expect ($binding.Current -and -not $binding.PinOnly) 'the binding of an untouched review is not current'
    $pinFile = Join-Path $pnWt 'plugin/pointframe/server.lock.json'
    New-Item -ItemType Directory -Path (Split-Path $pinFile -Parent) -Force | Out-Null
    Remove-Item -LiteralPath (Join-Path $rrecordDir 'discovery.txt') -ErrorAction SilentlyContinue
    & $writePins $pnWt '1.0.0'; & $git $pnWt @('add', '-A'); & $git $pnWt @('commit', '-qm', 'pin only')
    $binding = Get-ReviewBinding $pnState $pnRev
    & $expect ($binding.Current -and $binding.PinOnly -and (Test-Path (Join-Path $rrecordDir 'discovery.txt')) -and ((Get-Content (Join-Path $rrecordDir 'discovery.txt') -Raw).Trim() -ieq $pnWt)) "a valid plugin-pin-only commit was not the exception, or the discovery check did not run on the package worktree: $($binding.Reason)"
    $pinCases = @(
        @{ name = 'malformed JSON'; pins = @{ lockRaw = '{ not json' }; why = 'is not valid JSON' }
        @{ name = 'version mismatch'; pins = @{ version = '1.0.0'; lockVersion = '2.0.0' }; why = 'differs from the one in plugin.json' }
        @{ name = 'bad checksum'; pins = @{ sha = 'XYZ' }; why = 'sha256' }
        @{ name = 'failing discovery check'; pins = @{}; discovery = 'fail'; why = 'check-agent-discovery.ps1 failed' }
    )
    foreach ($case in $pinCases)
    {
        & $git $pnWt @('reset', '-q', '--hard', $pnCommit)
        $pins = $case.pins
        & $writePins $pnWt @pins; & $git $pnWt @('add', '-A'); & $git $pnWt @('commit', '-qm', "pin: $($case.name)")
        if ($case.ContainsKey('discovery')) { $env:POINTFRAME_PKG_TEST_DISCOVERY = $case.discovery }
        $binding = Get-ReviewBinding $pnState $pnRev
        Remove-Item Env:POINTFRAME_PKG_TEST_DISCOVERY -ErrorAction SilentlyContinue
        & $expect (-not $binding.Current -and $binding.Reason -match 'does not validate' -and $binding.Reason -match [regex]::Escape($case.why)) "a pin commit with $($case.name) kept the review current: $($binding.Reason)"
    }
    & $git $pnWt @('reset', '-q', '--hard', $pnCommit)
    New-Item -ItemType Directory -Path (Split-Path $pinFile -Parent) -Force | Out-Null
    & $git $pnWt @('mv', 'feature.txt', 'plugin/pointframe/server.lock.json'); & $git $pnWt @('commit', '-qm', 'rename over the pin file')
    $binding = Get-ReviewBinding $pnState $pnRev
    & $expect (-not $binding.Current -and $binding.Reason -match 'D feature\.txt') "a rename of a file over a pin file kept the review current: $($binding.Reason)"
    & $git $pnWt @('reset', '-q', '--hard', $pnCommit)
    & $git $pnWt @('update-index', '--assume-unchanged', 'feature.txt')
    [IO.File]::WriteAllText((Join-Path $pnWt 'feature.txt'), "tampered`r`n")
    New-Item -ItemType Directory -Path (Split-Path $pinFile -Parent) -Force | Out-Null
    & $writePins $pnWt '1.0.2'; & $git $pnWt @('add', '-A'); & $git $pnWt @('commit', '-qm', 'pin next to a hidden edit')
    $binding = Get-ReviewBinding $pnState $pnRev
    & $expect ((Test-WorktreeClean $pnWt) -and -not $binding.Current -and $binding.Reason -match 'outside the plugin pin files: feature\.txt') "an assume-unchanged edit next to a pin commit kept the review current: clean=$(Test-WorktreeClean $pnWt) $($binding.Reason)"
    $r = & $pkg @('accept', '-Id', 'pn', '-Reason', 'hidden edit')
    & $expect ($r.Exit -ne 0 -and (& $stateFile 'pn').stage -eq 'invalidated') "accept took a pin commit with a hidden edit: $($r.Text)"

    # ---- package fe: a failed later executor is an executor of the committed content ----
    $r = & $pkg @('new', '-Id', 'fe', '-Brief', $briefRv, '-Checks', $rvManifest)
    $r = & $pkg @('run', '-Id', 'fe', '-AgentRunScript', (Join-Path $stubDir 'ok.ps1'))
    [IO.File]::WriteAllText((Join-Path $stubDir 'c-fail.ps1'), $claudeStubPrelude + "Set-Content -LiteralPath (Join-Path (Get-Location).Path 'late.txt') 'x'; Emit-Result 'partial work'; exit 3`r`n")
    & $resetRecords
    $r = & $pkg @('run', '-Id', 'fe', '-Executor', 'claude-sonnet', '-ClaudePath', (Join-Path $stubDir 'c-fail.ps1'))
    $feState = & $stateFile 'fe'
    & $expect ($feState.runs[-1].outcome -eq 'impl-failed' -and (Test-Path (Join-Path $feState.worktree 'late.txt'))) "the failing Claude run did not leave its change: $($r.Text)"
    & $write 'fe' 'feature.txt' "v2`r`n"
    $r = & $pkg @('check', '-Id', 'fe'); $r = & $pkg @('commit', '-Id', 'fe')
    $feState = & $stateFile 'fe'
    $feBody = (& git -C $feState.worktree log -1 --format=%B) -join "`n"
    & $expect ($feState.stage -eq 'committed' -and @($feState.commit.executors).Count -eq 2 -and $feBody -match "Package: fe run $($feState.runs[0].runId) $($feState.runs[1].runId)") "the commit does not list every executor run: $feBody"
    $r = & $pkg @('review', '-Id', 'fe', '-Level', 'light', '-Reviewer', 'claude-sonnet', '-ClaudePath', $rvClaude)
    & $expect ($r.Exit -ne 0 -and $r.Text -match 'cannot review this package' -and $r.Text -match 'executor run') "the model of a failed executor was allowed to review: $($r.Text)"
    $r = & $pkg @('review', '-Id', 'fe', '-Level', 'light', '-Reviewer', 'codex-luna', '-CodexTestScript', $rvCodex)
    & $expect ($r.Exit -ne 0 -and $r.Text -match 'cannot review this package') "the model of the first executor was allowed to review: $($r.Text)"
    $env:POINTFRAME_PKG_TEST_VMODE = 'pass'
    $r = & $pkg @('review', '-Id', 'fe', '-Level', 'light', '-ClaudePath', $rvClaude)
    $feRev = Get-LatestReview (& $stateFile 'fe')
    & $expect ($r.Exit -eq 0 -and $feRev.reviewer.kind -eq 'claude-opus' -and @($feRev.executors).Count -eq 2 -and $feRev.executor.model -eq 'claude-sonnet-5-5') "the default reviewer ignored the failed executor: $($r.Text)"
    $r = & $pkg @('accept', '-Id', 'fe', '-Reason', 'independent of both executors')
    $feHead = & $headOf 'fe'
    $feRecordFile = Join-Path $main ".git/pointframe/reviews/$feHead.json"
    $feRecord = if (Test-Path $feRecordFile) { Get-Content -LiteralPath $feRecordFile -Raw | ConvertFrom-Json } else { $null }
    & $expect ($r.Exit -eq 0 -and $feRecord.executor -eq 'claude:claude-sonnet-5-5' -and $feRecord.reviewer -eq 'claude:claude-opus-5-5') "accept of fe failed or named the wrong executor: $($r.Text)"

    # ---- package bm: the base is observed by the host; a reviewer cannot change its snapshot ----
    & $newPackage 'bm'
    & $write 'bm' 'feature.txt' "v2`r`n"
    $r = & $pkg @('check', '-Id', 'bm'); $r = & $pkg @('commit', '-Id', 'bm')
    $bmState = & $stateFile 'bm'; $bmBase = [string]$bmState.baseSha; $bmHead = & $headOf 'bm'
    Update-State $packagesRoot 'bm' 'tamper-base' { param($s) $s.baseSha = $bmHead } | Out-Null
    & $resetR
    $env:POINTFRAME_PKG_TEST_VMODE = 'pass'
    $r = & $pkg @('review', '-Id', 'bm', '-Level', 'light', '-ClaudePath', $rvClaude)
    $bmRev = Get-LatestReview (& $stateFile 'bm')
    & $expect ($r.Exit -eq 1 -and $bmRev.status -eq 'invalid' -and ($bmRev.invalidReasons -join ' ') -match 'observed base \w+ differs from the recorded baseSha' -and @(& $rcalls).Count -eq 0) "an edited baseSha did not make the review invalid: $($r.Text)"
    Update-State $packagesRoot 'bm' 'restore-base' { param($s) $s.baseSha = $bmBase } | Out-Null
    $env:POINTFRAME_PKG_TEST_VMODE = 'snapwrite'
    $r = & $pkg @('review', '-Id', 'bm', '-Level', 'light', '-ClaudePath', $rvClaude)
    $bmRev = Get-LatestReview (& $stateFile 'bm')
    & $expect ($r.Exit -eq 1 -and $bmRev.status -eq 'invalid' -and ($bmRev.invalidReasons -join ' ') -match 'review snapshot changed' -and $bmRev.snapshot.removed -and -not (Test-Path $bmRev.snapshot.path) -and (Test-WorktreeClean (& $wtOf 'bm'))) "a reviewer that wrote into its snapshot was not invalid: $($r.Text)"
    # An input edited while the reviewer runs invalidates the review.
    $env:POINTFRAME_PKG_TEST_VMODE = 'editinput'
    $r = & $pkg @('review', '-Id', 'bm', '-Level', 'light', '-ClaudePath', $rvClaude)
    $bmRev = Get-LatestReview (& $stateFile 'bm')
    & $expect ($r.Exit -eq 1 -and $bmRev.status -eq 'invalid' -and ($bmRev.invalidReasons -join ' ') -match 'review input diff\.patch changed' -and $bmRev.inputHashes.'diff.patch') "an input edited mid-review did not invalidate the review: $($r.Text)"
    # accept re-checks the inputs the review was bound to.
    $env:POINTFRAME_PKG_TEST_VMODE = 'pass'
    $r = & $pkg @('review', '-Id', 'bm', '-Level', 'light', '-ClaudePath', $rvClaude, '-ResetBudget', '-Reason', 'bm retry after the edited input')
    & $expect ($r.Exit -eq 0 -and (& $stateFile 'bm').stage -eq 'review-passed') "the review of bm did not pass: $($r.Text)"
    $bmRev = Get-LatestReview (& $stateFile 'bm')
    $bmInputs = [string]$bmRev.snapshot.inputDir
    & $expect ((@($bmRev.inputHashes.Keys) | Sort-Object) -join ',' -eq 'brief.md,diff.patch,log.txt,prompt.md,stat.txt') 'the review did not record a hash for each input and the prompt'
    $editFile = { param($path, $text) (Get-Item -LiteralPath $path).IsReadOnly = $false; [IO.File]::AppendAllText($path, $text) }
    $logPath = Join-Path $bmInputs 'log.txt'
    $logBytes = [IO.File]::ReadAllBytes($logPath)
    & $editFile $logPath 'edited after the review'
    $r = & $pkg @('accept', '-Id', 'bm', '-Reason', 'x')
    & $expect ($r.Exit -ne 0 -and $r.Text -match 'review input log\.txt changed' -and (& $stateFile 'bm').stage -eq 'review-passed') "accept took a review whose input was edited afterwards: $($r.Text)"
    [IO.File]::WriteAllBytes($logPath, $logBytes)
    # Even with the recorded hash forged to match, the diff is regenerated and compared.
    $diffPath = Join-Path $bmInputs 'diff.patch'
    $diffBytes = [IO.File]::ReadAllBytes($diffPath)
    $recordedDiff = [string]$bmRev.inputHashes.'diff.patch'
    & $editFile $diffPath 'forged'
    $forged = Get-Sha256 $diffPath
    Update-State $packagesRoot 'bm' 'forge-diff-hash' { param($s) $s.reviews = @(Get-List $s.reviews | ForEach-Object { if ($_.status -eq 'valid') { $_.inputHashes['diff.patch'] = $forged }; $_ }) } | Out-Null
    $r = & $pkg @('accept', '-Id', 'bm', '-Reason', 'x')
    & $expect ($r.Exit -ne 0 -and $r.Text -match 'diff\.patch is not the diff the host generates') "accept did not regenerate the diff: $($r.Text)"
    [IO.File]::WriteAllBytes($diffPath, $diffBytes)
    Update-State $packagesRoot 'bm' 'restore-diff-hash' { param($s) $s.reviews = @(Get-List $s.reviews | ForEach-Object { if ($_.status -eq 'valid') { $_.inputHashes['diff.patch'] = $recordedDiff }; $_ }) } | Out-Null
    $briefCopy = Join-Path $bmInputs 'brief.md'
    $briefBytes = [IO.File]::ReadAllBytes($briefCopy)
    & $editFile $briefCopy 'forged'
    $forgedBrief = Get-Sha256 $briefCopy
    Update-State $packagesRoot 'bm' 'forge-brief-hash' { param($s) $s.reviews = @(Get-List $s.reviews | ForEach-Object { if ($_.status -eq 'valid') { $_.inputHashes['brief.md'] = $forgedBrief }; $_ }) } | Out-Null
    $r = & $pkg @('accept', '-Id', 'bm', '-Reason', 'x')
    & $expect ($r.Exit -ne 0 -and $r.Text -match 'not the brief snapshot byte for byte') "accept did not compare the brief copy with the snapshot: $($r.Text)"
    [IO.File]::WriteAllBytes($briefCopy, $briefBytes)
    Update-State $packagesRoot 'bm' 'restore-brief-hash' { param($s) $s.reviews = @(Get-List $s.reviews | ForEach-Object { if ($_.status -eq 'valid') { $_.inputHashes['brief.md'] = (Get-Sha256 $briefCopy) }; $_ }) } | Out-Null
    Update-State $packagesRoot 'bm' 'tamper-criteria' { param($s) $s.reviews = @(Get-List $s.reviews | ForEach-Object { if ($_.status -eq 'valid') { $_.criteriaSha256 = 'f' * 64 }; $_ }) } | Out-Null
    $r = & $pkg @('accept', '-Id', 'bm', '-Reason', 'x')
    & $expect ($r.Exit -ne 0 -and $r.Text -match 'criteria parsed from the brief differ' -and (& $stateFile 'bm').stage -eq 'review-passed') "accept did not re-check the criteria: $($r.Text)"

    # ---- package ac: the snapshot has to hold the content the checks ran on ----
    & $newPackage 'ac'
    & $write 'ac' 'feature.txt' "v2`r`n"
    $r = & $pkg @('check', '-Id', 'ac'); $r = & $pkg @('commit', '-Id', 'ac')
    $acWt = & $wtOf 'ac'
    & $git $acWt @('update-index', '--assume-unchanged', 'feature.txt')
    [IO.File]::WriteAllText((Join-Path $acWt 'feature.txt'), "hidden edit that no commit holds`r`n")
    & $write 'ac' 'other.txt' "another file`r`n"
    $r = & $pkg @('check', '-Id', 'ac')
    & $expect ($r.Exit -eq 0) "check of ac failed: $($r.Text)"
    $r = & $pkg @('commit', '-Id', 'ac')
    $acState = & $stateFile 'ac'
    & $expect ($r.Exit -eq 0 -and $acState.stage -eq 'committed' -and (Test-WorktreeClean $acWt)) "commit of ac failed: $($r.Text)"
    & $resetR
    $env:POINTFRAME_PKG_TEST_VMODE = 'pass'
    $r = & $pkg @('review', '-Id', 'ac', '-Level', 'light', '-ClaudePath', $rvClaude)
    $acRev = Get-LatestReview (& $stateFile 'ac')
    & $expect ($r.Exit -eq 1 -and $acRev.status -eq 'invalid' -and ($acRev.invalidReasons -join ' ') -match 'checks did not run on the committed content' -and @(& $rcalls).Count -eq 0 -and $acRev.snapshot.removed) "a snapshot that differs from the checked content was reviewed: $($r.Text)"

    # ---- package nl: raw bytes, no line-ending tolerance; the ordinary CRLF path still matches ----
    & $newPackage 'nl'
    $nlWt = & $wtOf 'nl'
    [IO.File]::WriteAllText((Join-Path $nlWt '.gitattributes'), "raw.dat -text`r`n")
    [IO.File]::WriteAllBytes((Join-Path $nlWt 'raw.dat'), [byte[]](1, 0, 10, 2))
    [IO.File]::WriteAllBytes((Join-Path $nlWt 'lf.txt'), [byte[]](111, 110, 101, 10, 116, 119, 111, 10))
    $r = & $pkg @('check', '-Id', 'nl'); $r = & $pkg @('commit', '-Id', 'nl')
    & $expect ($r.Exit -eq 0 -and [IO.File]::ReadAllText((Join-Path $nlWt 'lf.txt')) -eq "one`r`ntwo`r`n") "check did not normalize an LF text file: $($r.Text)"
    $env:POINTFRAME_PKG_TEST_VMODE = 'pass'
    $r = & $pkg @('review', '-Id', 'nl', '-Level', 'light', '-ClaudePath', $rvClaude)
    $nlRev = Get-LatestReview (& $stateFile 'nl')
    & $expect ($r.Exit -eq 0 -and $nlRev.status -eq 'valid' -and $nlRev.snapshot.startObservation.contentTree -eq $nlRev.contentTree) "the ordinary path (check normalization, commit, snapshot checkout) did not give identical raw bytes: $($r.Text) $($nlRev.invalidReasons -join '; ')"
    & $git $nlWt @('update-index', '--assume-unchanged', 'raw.dat')
    [IO.File]::WriteAllBytes((Join-Path $nlWt 'raw.dat'), [byte[]](1, 0, 13, 10, 2))
    [IO.File]::WriteAllText((Join-Path $nlWt 'other.txt'), "other`r`n")
    $r = & $pkg @('check', '-Id', 'nl')
    & $expect ($r.Exit -eq 0) "check of nl after the hidden edit failed: $($r.Text)"
    $r = & $pkg @('commit', '-Id', 'nl')
    & $expect ($r.Exit -eq 0 -and (& $stateFile 'nl').stage -eq 'committed') "commit of nl after the hidden edit failed: $($r.Text)"
    & $resetR
    $r = & $pkg @('review', '-Id', 'nl', '-Level', 'light', '-ClaudePath', $rvClaude)
    $nlRev = Get-LatestReview (& $stateFile 'nl')
    & $expect ($r.Exit -eq 1 -and $nlRev.status -eq 'invalid' -and ($nlRev.invalidReasons -join ' ') -match 'checks did not run on the committed content \(raw bytes differ: raw\.dat\)' -and @(& $rcalls).Count -eq 0) "a newline-only hidden edit of a -text file was tolerated: $($r.Text) $($nlRev.invalidReasons -join '; ')"
    & $expect ($null -eq (Get-Command Get-NormalizedContentHash -ErrorAction SilentlyContinue)) 'the normalized fallback still exists'

    # ---- package cf: a rebase conflict is aborted truthfully ----
    $r = & $pkg @('new', '-Id', 'cf', '-Brief', $briefRv, '-Checks', $rvManifest)
    $r = & $pkg @('run', '-Id', 'cf', '-AgentRunScript', (Join-Path $stubDir 'ok.ps1'))
    & $write 'cf' 'a.txt' "package change`r`n"
    $r = & $pkg @('check', '-Id', 'cf')
    [IO.File]::WriteAllText((Join-Path $seed 'a.txt'), "origin change`r`n"); & $git $seed @('add', '-A'); & $git $seed @('commit', '-qm', 'same line'); & $git $seed @('push', '-q', 'origin', 'master')
    $r = & $pkg @('commit', '-Id', 'cf')
    $cfState = & $stateFile 'cf'
    $cfWt = [string]$cfState.worktree
    $rebaseDir = ((& git -C $cfWt rev-parse --git-path rebase-merge) | Select-Object -First 1).Trim()
    if (-not [IO.Path]::IsPathRooted($rebaseDir)) { $rebaseDir = Join-Path $cfWt $rebaseDir }
    & $expect ($r.Exit -ne 0 -and $r.Text -match 'rebase onto origin/master failed \(the rebase was aborted' -and $cfState.stage -eq 'checks-passed' -and (Test-WorktreeClean $cfWt) -and -not (Test-Path $rebaseDir) -and @($cfState.history | Where-Object { $_.event -eq 'commit-rebase-failed' }).Count -eq 1 -and $cfState.lastError -match 'abort exit 0') "a rebase conflict was not aborted and reported truthfully: $($r.Text)"
    Write-Host 'review self-test: commit, review, fix, accept cases finished.'
}

function Invoke-SelfTest
{
    $failures = [Collections.Generic.List[string]]::new()
    $expect = { param([bool]$Condition, [string]$Message) if (-not $Condition) { $failures.Add($Message) } }
    $root = Join-Path ([IO.Path]::GetTempPath()) "package-selftest-$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $root | Out-Null
    $oldMain = $env:POINTFRAME_PKG_TEST_MAIN
    # Exported to every child: the real-binary guards (Get-CodexCliVersion, Assert-SelfTestLauncher, the Codex reviewer) key on these.
    $env:POINTFRAME_PKG_SELFTEST = '1'
    $env:POINTFRAME_PKG_SELFTEST_ROOT = $root
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
        [IO.File]::WriteAllText((Join-Path $stubDir 'discovery.ps1'), "# pointframe-selftest-stub`r`nif (`$env:POINTFRAME_PKG_TEST_RRECORD -and (Test-Path `$env:POINTFRAME_PKG_TEST_RRECORD)) { Set-Content -LiteralPath (Join-Path `$env:POINTFRAME_PKG_TEST_RRECORD 'discovery.txt') (Get-Location).Path }`r`nif (`$env:POINTFRAME_PKG_TEST_DISCOVERY -eq 'fail') { Write-Host 'ERROR the discovery check says no'; exit 1 }`r`nWrite-Host 'discovery ok'; exit 0`r`n")
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
            if ($PackageArgs -notcontains '-CodexVersion') { $PackageArgs = @($PackageArgs) + @('-CodexVersion', '9.9.9-test') }
            if ($PackageArgs -notcontains '-DiscoveryScript') { $PackageArgs = @($PackageArgs) + @('-DiscoveryScript', (Join-Path $stubDir 'discovery.ps1')) }
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
        if ($r.Exit -ne 0) { throw "show -Json failed: $($r.Text)" }
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
                -ArgumentList @('-NoProfile', '-File', "`"$PSCommandPath`"", 'run', '-Id', 'rec2', '-Repo', "`"$main`"", '-StatusScript', "`"$statusStub`"", '-AgentRunScript', "`"$(Join-Path $stubDir 'ok.ps1')`"", '-CodexVersion', '9.9.9-test', '-LockTimeoutSeconds', '120', '-GraceSeconds', '0')
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

        Invoke-ReviewSelfTest
    }
    catch { $failures.Add("self-test crashed: $($_.Exception.Message) at line $($_.InvocationInfo.ScriptLineNumber)") }
    finally
    {
        if ($null -eq $oldMain) { Remove-Item Env:POINTFRAME_PKG_TEST_MAIN -ErrorAction SilentlyContinue } else { $env:POINTFRAME_PKG_TEST_MAIN = $oldMain }
        Remove-Item Env:POINTFRAME_PKG_SELFTEST, Env:POINTFRAME_PKG_SELFTEST_ROOT, Env:POINTFRAME_PKG_TEST_RRECORD, Env:POINTFRAME_PKG_TEST_VMODE -ErrorAction SilentlyContinue
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
if (-not $Command) { Write-Host 'ERROR a command is required: new, run, check, show, commit, review, fix, disposition or accept'; exit 2 }
if (-not $Id) { Write-Host 'ERROR -Id is required'; exit 2 }
try
{
    switch ($Command)
    {
        'new' { Invoke-New }
        'run' { Invoke-Run }
        'check' { Invoke-Check }
        'show' { Invoke-Show }
        'commit' { Invoke-Commit }
        'review' { Invoke-Review }
        'fix' { Invoke-Fix }
        'disposition' { Invoke-Disposition }
        'accept' { Invoke-Accept }
    }
}
catch
{
    Write-Host "ERROR $($_.Exception.Message)"
    exit $(if ($script:ExitCode) { $script:ExitCode } else { 1 })
}
exit $script:ExitCode
