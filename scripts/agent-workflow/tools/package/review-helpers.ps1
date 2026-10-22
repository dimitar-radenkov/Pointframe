# ---- commit, review, fix, accept: shared helpers -------------------------------------------------------------------

# The only commits that keep a review or an acceptance valid: net changes of the pin files alone. The list is pinException.paths of the
# configuration (script:PinFiles, set by Initialize-PackageContext), the one list merge-pr.ps1 knows too.
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
    # The changed pin files are validated by the repository's pin adapter before an approval is kept: hooks.pin -Action Validate, run in
    # the package worktree (the check merge-pr.ps1 runs for a pin-only change). Exit 0 is valid; otherwise its last line says why. A
    # repository without the hook has no pin adapter, so a pin-file change cannot be validated and keeps nothing. Returns the problem, or $null.
    try { $hook = Resolve-PackageHook 'pin' } catch { return $_.Exception.Message }
    if (-not $hook) { return 'hooks.pin is not configured, so a change of the pin files cannot be validated' }
    Push-Location -LiteralPath $Wt
    try { $output = @(& pwsh -NoProfile -NonInteractive -File $hook -Action Validate -Root $Wt 2>&1 | ForEach-Object { "$_" }); $exit = $LASTEXITCODE }
    finally { Pop-Location }
    if ($exit -ne 0) { return "the pin hook failed validation (exit $exit): $(@($output | Where-Object { $_ })[-1])" }
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

