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

function Enter-MainSyncSharedLock([string]$CommonDir)
{
    # Shared readers use <git common dir>/<namespace>/main-sync.lock with FileShare.ReadWrite; merge-pr.ps1 -SyncMain opens it with FileShare.None.
    $directory = Join-Path $CommonDir $script:Namespace
    $lockPath = Join-Path $directory 'main-sync.lock'
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $waitSeconds = 120
    if ($env:AW_PKG_SELFTEST -and $env:AW_PKG_SELFTEST_LOCK_WAIT_SECONDS -match '^\d+$')
    {
        $waitSeconds = [Math]::Min(120, [int]$env:AW_PKG_SELFTEST_LOCK_WAIT_SECONDS)
    }
    $deadline = [DateTime]::UtcNow.AddSeconds($waitSeconds)
    while ($true)
    {
        try { return [IO.File]::Open($lockPath, 'OpenOrCreate', 'Read', 'ReadWrite') }
        catch [IO.IOException]
        {
            if ([DateTime]::UtcNow -ge $deadline)
            {
                throw "could not acquire the shared main-checkout lock $lockPath within $waitSeconds seconds; -SyncMain may be holding it, wait for the sync to finish and retry"
            }
            Start-Sleep -Milliseconds 100
        }
    }
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

