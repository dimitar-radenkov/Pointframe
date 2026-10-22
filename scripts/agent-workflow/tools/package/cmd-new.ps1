# ---- new --------------------------------------------------------------------------------------------------------

function Get-UnobservableCriterionWarnings($Criteria, $Manifest)
{
    # An acceptance case the reviewer cannot observe: it names only a command run ("tests pass"; the reviewer has no shell, it reads the check
    # summary of the commands the manifest lists) or only the executor's report ("stated in the report"; the report is the executor's word).
    # Heuristic, and only ever a warning. A case that names a check of the manifest, or something to look at (a file, the diff, a line, an
    # output), is left alone.
    $commandRun = '(?i)\b(tests?|builds?|compil\w+|lint\w*|format\w*|verify|checks?|gates?|run-all|self-?test|suite)\b.{0,60}\b(pass(es|ed)?|succeed(s|ed)?|green|clean|ok)\b|\bexits?( with)?( code)? 0\b|\b(all )?(tests?|builds?|checks?) (are |is )?(pass|green)'
    $reportOnly = '(?i)\b(stated|reported|documented|confirmed|mentioned|noted|listed|explained|claimed)\b.{0,40}\b(in|by)\b.{0,25}\breport\b|\b(the|executor''?s?|final)( \w+){0,2} report\b.{0,30}\b(says?|states?|lists?|confirms?|mentions?|names?|documents?|notes?)\b|\bin the report\b'
    $observable = '(?i)\b(files?|diff|lines?|functions?|class(es)?|methods?|contains?|exists?|renamed?|removed?|adds?|added|unchanged|refus\w+|returns?|prints?|output|text|says)\b'
    $names = @(@($Manifest.build) + @($Manifest.format) + @($Manifest.tests | ForEach-Object { $_.project }) + @($Manifest.extra | ForEach-Object { $_.name }) | Where-Object { $_ } | ForEach-Object { $_; [IO.Path]::GetFileName((([string]$_) -replace '\\', '/')) } | Where-Object { $_ })
    foreach ($criterion in @($Criteria))
    {
        $text = [string]$criterion.text
        $namesCheck = $false
        foreach ($name in $names) { if ($text.IndexOf([string]$name, [StringComparison]::OrdinalIgnoreCase) -ge 0) { $namesCheck = $true; break } }
        if ($namesCheck) { continue }
        $kind = if ($text -match $reportOnly -and $text -notmatch $commandRun) { 'the executor''s report' } elseif ($text -match $commandRun) { 'a command run' } else { $null }
        if ($kind -and ($kind -ne 'a command run' -or $text -notmatch $observable)) { "acceptance case $($criterion.id) names only $kind (`"$(Get-ShortText $text 80)`"): a reviewer cannot run commands and cannot take the report for evidence. Name something it can observe: a file or line of the diff, or a gate of the check manifest (it reads check-summary.json). Not refused." }
    }
}

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

    $validation = @(& pwsh -NoProfile -File (Join-Path $script:PackageToolsRoot 'agent-run.ps1') -Brief $briefSource -Worktree $wt -ValidateOnly -Root $script:Repo 2>&1 | ForEach-Object { "$_" })
    if ($LASTEXITCODE -ne 0) { throw "brief validation failed: $($validation -join ' ')" }

    $remote = Get-PackageRemote $BaseRef
    Write-Host "package $Id"
    Write-Host "  repository   $($info.MainCheckout)"
    Write-Host "  state        $($paths.State)"
    Write-Host "  brief        $briefSource (snapshot with SHA-256, read-only)"
    Write-Host "  manifest     $(if ($Checks) { $Checks } else { '(none: empty manifest)' }): build=$($manifest.build.Count) tests=$($manifest.tests.Count) format=$($manifest.format.Count) extra=$($manifest.extra.Count)"
    Write-Host "  worktree     $wt"
    Write-Host "  branch       $branch from $BaseRef$(if ($remote) { " after 'git fetch $remote'" } else { ' (no remote: the local branch, no fetch)' })"
    Write-Host "  preflight    $((Get-PackageCommand 'build').exe) build of: $((@($manifest.build) + @($manifest.tests | ForEach-Object { $_.project }) | Select-Object -Unique) -join ', ')"
    # Warned about, never refused: a brief without acceptance cases is refused by 'review', not here.
    $briefCriteria = @(try { Get-ReviewCriteria $briefSource } catch { })
    foreach ($warning in @(Get-UnobservableCriterionWarnings $briefCriteria $manifest)) { Write-Host "WARNING $warning" }
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
        $c = Expand-PackageCommand (Get-PackageCommand 'build') @{ target = $target }
        $code = Invoke-Logged $c.exe @($c.args) $wt $log
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
    & pwsh -NoProfile -File $StatusScript -Root $script:Repo -Set -Id $Id -Objective "package $Id" -Stage 'package ready' | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Host "WARNING status.ps1 -Set failed (exit $LASTEXITCODE); the package state is unaffected." }
    Write-Host "package $Id is ready: worktree $wt, branch $branch, base $baseSha"
}

