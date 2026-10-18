#Requires -Version 7.0
<#
Mechanics of one work package for a Codex executor, with durable state. No review, fix loop, commit or merge.

  pwsh scripts/package.ps1 new -Id <slug> -Brief <file> [-Checks <manifest.json>] [-DryRun]
  pwsh scripts/package.ps1 run -Id <slug> [-Effort medium|high -EscalationReason <text>] [-MaxMinutes 30]
  pwsh scripts/package.ps1 check -Id <slug>
  pwsh scripts/package.ps1 show -Id <slug> [-Json]
  pwsh scripts/package.ps1 -SelfTest

new     snapshots the brief and the check manifest (SHA-256, read-only), creates branch pkg/<slug> and its worktree from
        the fetched origin/master, preflight-builds the check targets, validates the brief with agent-run.ps1.
run     launches the Codex executor through agent-run.ps1 under a hard wall-clock bound, and fingerprints the main checkout
        before and after. A changed fingerprint stops the run (mainCheckoutMutated); nothing is ever reverted.
check   runs the manifest's checks in the worktree as the runner. Mutating steps run first (extra commands marked "mutates",
        dotnet format, CRLF normalization), then build, focused tests and the other extra commands. Every command records
        the content hash of the worktree it ran on; a result is fresh only while the worktree still has that hash.
show    prints stage, runs, checks (stale or incomplete evidence is reported), the guard and the allowed next steps.

The check manifest is JSON: { "build": ["App/App.csproj"], "tests": [{ "project": "App.Tests/App.Tests.csproj", "filter":
"FullyQualifiedName~Foo" }], "format": ["App/App.csproj"], "extra": [{ "name": "x", "exe": "pwsh", "args": ["-File", "x.ps1"],
"mutates": false }] }.
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
    $unknown = @($source.Keys | Where-Object { $_ -notin @('build', 'tests', 'format', 'extra', 'version') })
    if ($unknown.Count) { throw "check manifest has unknown key(s): $($unknown -join ', ')" }
    $manifest = [ordered]@{ version = 1; build = @(); tests = @(); format = @(); extra = @() }
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

function Get-RunClassification([object]$ExitCode, [string]$Termination, [string]$ReportText, [bool]$ArtifactsPresent, [string]$MissingArtifacts, [string]$ContainmentProblem = '')
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

    $before = Get-Fingerprint $mainCheckout
    if (-not $before.treeHash) { throw "the main checkout cannot be fingerprinted (unreadable: $($before.unreadable -join ', ')); the run was not started" }
    $started = Get-Now
    $permissions = @{ sandbox = 'workspace-write'; approval = 'never'; windowsSandbox = 'unelevated' }
    $cliVersion = Get-CodexCliVersion
    $self = @{ pid = $PID; startUtc = (Get-ProcessStartIdentity $PID) }
    Update-State $info.StateRoot $Id 'run-started' {
        param($s)
        if ($s.stage -notin @('ready', 'executor-completed', 'executor-env-blocked', 'executor-impl-failed', 'checks-passed', 'checks-failed')) { throw "run is not allowed in stage '$($s.stage)'" }
        $s.currentRun = @{ runId = $runId; pid = $PID }
        $s.runs = @($s.runs) + @(@{
                runId = $runId; seq = (@($s.runs).Count + 1); startedUtc = $started; endedUtc = $null; provider = 'codex'; model = $Model; effort = $Effort
                maxMinutes = $MaxMinutes; maxInputTokens = $MaxInputTokens; permissions = $permissions; codexCliVersion = $cliVersion
                wrapper = $self; executor = $null; sessionId = $null; exitCode = $null; termination = 'running'; outcome = 'running'; reason = $null
                envBlocked = $false; envEvidence = $null; tokens = $null; containmentVerified = $null; containmentReason = $null; logDirectory = $runDir
                artifacts = @{ outMd = $null; eventsJsonl = $null }; mainCheckoutBefore = $before; mainCheckoutAfter = $null; mainCheckoutMutated = $false
            })
        $s.stage = 'running'
    } | Out-Null

    $agentArgs = @('-NoProfile', '-File', "`"$AgentRunScript`"", '-Brief', "`"$($state.brief.path)`"", '-Worktree', "`"$wt`"", '-Model', $Model, '-Effort', $Effort,
        '-MaxMinutes', ([string][int][Math]::Ceiling($MaxMinutes)), '-MaxInputTokens', ([string]$MaxInputTokens), '-Sandbox', $permissions.sandbox,
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
        $process = Start-ProcessInJob $job (Get-Command pwsh -CommandType Application | Select-Object -First 1).Source $agentArgs $wt $null (Join-Path $runDir 'agent-run.log') (Join-Path $runDir 'agent-run.err')
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

    $limitMs = [long]($MaxMinutes * 60000 + $GraceSeconds * 1000)
    $finished = Wait-ProcessExit $process $limitMs
    $termination = 'exit'
    $containment = $null
    if (-not $finished)
    {
        # The bound is enforced and the result checked: the tree must be verifiably gone, else the state says so.
        $stopped = Stop-ExecutorTree $job $simulateKillFailure
        $termination = if ($stopped) { 'timeout' } else { 'kill-failed' }
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
    $exitCode = if ($termination -in @('timeout', 'kill-failed') -or -not (Test-ProcessExited $process)) { $null } else { Get-ProcessExitCode $process }
    [PointframeProcessJob]::CloseProcess($process.Handle)
    if ($termination -eq 'exit' -and $exitCode -eq 3) { $termination = 'budget' }
    $elapsed = [Math]::Round($stopwatch.Elapsed.TotalSeconds, 1)

    $after = Get-Fingerprint $mainCheckout
    $mutated = -not (Test-FingerprintsEqual $before $after)
    $outMd = Join-Path $runDir 'out.md'
    $events = Join-Path $runDir 'events.jsonl'
    $report = if (Test-Path -LiteralPath $outMd -PathType Leaf) { Get-Content -LiteralPath $outMd -Raw } else { '' }
    $missing = @(@($outMd, $events) | Where-Object { -not (Test-Path -LiteralPath $_ -PathType Leaf) -or (Get-Item -LiteralPath $_).Length -eq 0 } | ForEach-Object { Split-Path $_ -Leaf })
    $containmentProblem = if ($containment.Verified) { '' } else { [string]$containment.Reason }
    $classification = Get-RunClassification $exitCode $termination $report ($missing.Count -eq 0) ($missing -join ', ') $containmentProblem
    $session = Get-SessionIdFromEvents $events
    $tokens = Get-EventTokens $events
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
                }
                $_
            })
        $s.currentRun = if ($termination -eq 'kill-failed') { $s.currentRun } else { $null }
        $s.guard = @{ mainCheckoutMutated = ($mutated -or [bool]$s.guard.mainCheckoutMutated); lastRunId = $runId; before = $before; after = $after }
        $s.stage = $stage
    } | Out-Null

    Write-Host "run ${runId}: $($classification.outcome) (termination=$termination exit=$exitCode containment=$(if ($containment.Verified) { 'verified' } else { 'UNVERIFIED' }) session=$session $elapsed s) log=$runDir"
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
            [ordered]@{ runId = $_.runId; startedUtc = $_.startedUtc; provider = $_.provider; model = $_.model; effort = $_.effort; codexCliVersion = $_.codexCliVersion; permissions = $_.permissions
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
    foreach ($run in $runs) { Write-Host "    $($run.startedUtc) $($run.provider) $($run.model) $($run.effort) cli=$($run.codexCliVersion) termination=$($run.termination) exit=$($run.exitCode) outcome=$($run.outcome) envBlocked=$($run.envBlocked) session=$($run.sessionId)" }
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
    }
    catch { $failures.Add("self-test crashed: $($_.Exception.Message) at line $($_.InvocationInfo.ScriptLineNumber)") }
    finally
    {
        if ($null -eq $oldMain) { Remove-Item Env:POINTFRAME_PKG_TEST_MAIN -ErrorAction SilentlyContinue } else { $env:POINTFRAME_PKG_TEST_MAIN = $oldMain }
        Remove-Item Env:POINTFRAME_PKG_TEST_PIDFILE, Env:POINTFRAME_PKG_TEST_SKIP_KILL -ErrorAction SilentlyContinue
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
