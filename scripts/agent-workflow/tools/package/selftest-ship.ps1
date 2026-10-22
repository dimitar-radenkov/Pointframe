function Invoke-ShipSelfTest
{
    # Runs inside Invoke-ReviewSelfTest (dynamic scope): its helpers ($pkg, $stateFile, $wtOf, $write, $newPackage, $rvClaude, $rvManifest,
    # $resetR, $expect, $failures, $main, $origin, $root, $stubDir, $packagesRoot, $deadWrapper) are the caller's. Every external tool is a
    # marked stub: gh, merge-pr and verify, and the qualify and channels hooks of the seed repository. The only remote is a bare
    # repository under the self-test root.
    $shipRoot = Join-Path $root 'ship'
    $ghDir = Join-Path $shipRoot 'gh'
    $shipLog = Join-Path $shipRoot 'calls.log'
    New-Item -ItemType Directory -Path $shipRoot -Force | Out-Null
    $env:AW_PKG_TEST_SHIPLOG = $shipLog
    $env:AW_PKG_TEST_GH_DIR = $ghDir
    $env:AW_PKG_TEST_LIBDIR = Join-Path $script:KitDir 'lib'
    Add-Content -LiteralPath (Join-Path $main '.git/info/exclude') -Value 'artifacts/'
    # ship refuses a main checkout that is not at the base ref (its configuration may be stale). The seed pushed commits to origin after the
    # main checkout was cloned, so it sits behind; it is brought up to date here, and again whenever a case moves origin/master.
    $syncMain = {
        & git -C $main fetch -q origin 2>&1 | Out-Null
        & git -C $main -c user.name=SelfTest -c user.email=selftest@example.invalid merge --ff-only -q origin/master 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'self-test: the main checkout could not be fast-forwarded to origin/master' }
    }
    & $syncMain

    $stubBodies = [ordered]@{}
    $stubBodies['gh'] = @'
$a = @($args)
$dir = $env:AW_PKG_TEST_GH_DIR
Add-Content -LiteralPath $env:AW_PKG_TEST_SHIPLOG -Value ('gh ' + ($a -join ' '))
New-Item -ItemType Directory -Path $dir -Force | Out-Null
$prsPath = Join-Path $dir 'prs.json'
$relPath = Join-Path $dir 'releases.json'
$prs = if (Test-Path $prsPath) { @(Get-Content $prsPath -Raw | ConvertFrom-Json) } else { @() }
$releases = if (Test-Path $relPath) { @(Get-Content $relPath -Raw | ConvertFrom-Json) } else { @() }
function Arg([string]$Name) { $i = [Array]::IndexOf($a, $Name); if ($i -ge 0) { $a[$i + 1] } }
$verb = "$($a[0]) $($a[1])"
if ($verb -eq 'pr list')
{
    if ($env:AW_PKG_TEST_GH_FAIL -eq 'list') { exit 1 }
    $head = Arg '--head'
    ConvertTo-Json -InputObject @($prs | Where-Object { $_.headRefName -eq $head }) -Depth 6
    exit 0
}
if ($verb -eq 'pr create')
{
    if ($env:AW_PKG_TEST_GH_FAIL -eq 'create') { Write-Host 'gh: simulated failure'; exit 1 }
    $n = $prs.Count + 101
    $head = Arg '--head'
    $oid = ([string](& git rev-parse "refs/heads/$head")).Trim()
    Copy-Item -LiteralPath (Arg '--body-file') -Destination (Join-Path $dir "body-$n.md") -Force
    Set-Content -LiteralPath (Join-Path $dir "title-$n.txt") -Value (Arg '--title') -NoNewline
    $pr = [ordered]@{ number = $n; url = "https://github.example.invalid/o/r/pull/$n"; state = 'OPEN'; headRefName = $head; headRefOid = $oid; mergeCommit = $null; autoMerge = $false }
    ConvertTo-Json -InputObject @($prs + $pr) -Depth 6 | Set-Content -LiteralPath $prsPath
    Write-Output $pr.url
    exit 0
}
if ($verb -eq 'pr edit')
{
    if ($env:AW_PKG_TEST_GH_FAIL -eq 'edit') { Write-Host 'gh: simulated edit failure'; exit 1 }
    $n = [int]$a[2]
    Copy-Item -LiteralPath (Arg '--body-file') -Destination (Join-Path $dir "body-$n.md") -Force
    exit 0
}
if ($verb -eq 'pr view')
{
    if ($env:AW_PKG_TEST_GH_FAIL -eq 'view') { exit 1 }
    $n = [int]$a[2]
    $pr = @($prs | Where-Object { $_.number -eq $n })[0]
    if (-not $pr) { exit 1 }
    # GitHub merges a pull request with auto-merge on once its checks pass: after N views, when the test says so.
    if ($env:AW_PKG_TEST_MERGE_AFTER -and $pr.state -eq 'OPEN' -and $pr.autoMerge)
    {
        $counter = Join-Path $dir 'views.txt'
        $before = if (Test-Path $counter) { [int](Get-Content $counter -Raw) } else { 0 }
        $seen = $before + 1
        Set-Content -LiteralPath $counter -Value $seen
        if ($seen -gt [int]$env:AW_PKG_TEST_MERGE_AFTER)
        {
            $sha = ([string](& git rev-parse origin/master)).Trim()
            $pr.state = 'MERGED'; $pr.mergeCommit = $sha
            ConvertTo-Json -InputObject @($prs) -Depth 6 | Set-Content -LiteralPath $prsPath
            ConvertTo-Json -InputObject @($releases + [ordered]@{ tagName = "v9.9.$($releases.Count + 1)"; targetCommitish = $sha }) -Depth 4 | Set-Content -LiteralPath $relPath
        }
    }
    $oid = ([string](& git rev-parse "refs/heads/$($pr.headRefName)")).Trim()
    $merge = if ($pr.mergeCommit) { [ordered]@{ oid = [string]$pr.mergeCommit } } else { $null }
    $auto = if ($pr.autoMerge) { [ordered]@{ enabledAt = '2026-01-01T00:00:00Z' } } else { $null }
    ConvertTo-Json -InputObject ([ordered]@{ state = $pr.state; headRefOid = $oid; mergeCommit = $merge; autoMergeRequest = $auto }) -Depth 6
    exit 0
}
if ($verb -eq 'release list')
{
    ConvertTo-Json -InputObject @($releases | ForEach-Object { [ordered]@{ tagName = $_.tagName; isDraft = $false } }) -Depth 4
    exit 0
}
if ($verb -eq 'release view')
{
    $rel = @($releases | Where-Object { $_.tagName -eq $a[2] })[0]
    if (-not $rel) { exit 1 }
    ConvertTo-Json -InputObject ([ordered]@{ targetCommitish = $rel.targetCommitish })
    exit 0
}
Write-Host "unexpected gh call: $($a -join ' ')"
exit 2
'@
    $stubBodies['mergepr'] = @'
$a = @($args)
Add-Content -LiteralPath $env:AW_PKG_TEST_SHIPLOG -Value ('mergepr ' + ($a -join ' '))
function Arg([string]$Name) { $i = [Array]::IndexOf($a, $Name); if ($i -ge 0) { $a[$i + 1] } }
$dir = $env:AW_PKG_TEST_GH_DIR
$wt = Arg '-Worktree'
$identity = @('-c', 'user.name=SelfTest', '-c', 'user.email=selftest@example.invalid')
if ($a -contains '-PinOnly')
{
    $mode = $env:AW_PKG_TEST_PINMODE
    if ($mode -in @('pin', 'pin-extra', 'pin-dirty'))
    {
        New-Item -ItemType Directory -Path (Join-Path $wt 'vendor/pin') -Force | Out-Null
        [IO.File]::WriteAllText((Join-Path $wt 'vendor/pin/lock.json'), ((@{ schemaVersion = 1; version = '1.0.0'; asset = 'x.mcpb'; url = 'https://example.invalid/x'; sha256 = ('a' * 64) } | ConvertTo-Json) + "`r`n"))
        [IO.File]::WriteAllText((Join-Path $wt 'vendor/pin/meta.json'), ((@{ name = 'app'; version = '1.0.0' } | ConvertTo-Json) + "`r`n"))
        if ($mode -eq 'pin-extra') { [IO.File]::WriteAllText((Join-Path $wt 'smuggled.txt'), "smuggled`r`n") }
        & git -C $wt add -A
        & git -C $wt @identity commit -qm 'Pin the Claude plugin to v1.0.0'
        if ($mode -eq 'pin-dirty') { [IO.File]::WriteAllText((Join-Path $wt 'dirty-by-pin.txt'), "dirty`r`n") }
        Write-Host 'Plugin pin: v0.0.1 -> v1.0.0.'
    }
    else { Write-Host 'Plugin pin: already current' }
    exit 0
}
$n = [int](Arg '-Pr')
$prsPath = Join-Path $dir 'prs.json'
$prs = @(Get-Content $prsPath -Raw | ConvertFrom-Json)
if (-not ($a -contains '-Auto'))
{
    if ($wt -and (Test-Path $wt)) { & git worktree remove --force $wt }
    Write-Host 'cleaned up after a pull request that GitHub merged'
    exit 0
}
if ($env:AW_PKG_TEST_MERGEMODE -eq 'fail') { Write-Host 'RECEIPT REFUSED: simulated refusal'; exit 1 }
Write-Host 'AUTO-MERGE ENABLED (not merged yet)'
foreach ($pr in $prs) { if ($pr.number -eq $n) { $pr.autoMerge = $true } }
$mode = $env:AW_PKG_TEST_MERGEMODE
# merged: -WaitMerged waits and cleans up after the merge. instant: GitHub merges at once, whatever the flags, and nothing is cleaned up.
if ((($a -contains '-WaitMerged') -and $mode -eq 'merged') -or $mode -eq 'instant')
{
    $sha = ([string](& git rev-parse origin/master)).Trim()
    foreach ($pr in $prs) { if ($pr.number -eq $n) { $pr.state = 'MERGED'; $pr.mergeCommit = $sha } }
    $relPath = Join-Path $dir 'releases.json'
    $releases = if (Test-Path $relPath) { @(Get-Content $relPath -Raw | ConvertFrom-Json) } else { @() }
    ConvertTo-Json -InputObject @($releases + [ordered]@{ tagName = "v9.9.$($releases.Count + 1)"; targetCommitish = $sha }) -Depth 4 | Set-Content -LiteralPath $relPath
    if ($mode -eq 'merged' -and $wt -and (Test-Path $wt)) { & git worktree remove --force $wt }
    Write-Host "MERGED $sha"
}
ConvertTo-Json -InputObject @($prs) -Depth 6 | Set-Content -LiteralPath $prsPath
exit 0
'@
    $stubBodies['verify'] = @'
Add-Content -LiteralPath $env:AW_PKG_TEST_SHIPLOG -Value 'verify'
. (Join-Path $env:AW_PKG_TEST_LIBDIR 'tree-hash.ps1')
$top = ([string](& git rev-parse --show-toplevel)).Trim()
$head = ([string](& git rev-parse HEAD)).Trim()
$tree = Get-WorkingTreeHash $top
$status = switch ($env:AW_PKG_TEST_VERMODE) { 'fail' { 'fail' } 'partial' { 'partial' } default { 'pass' } }
$out = Join-Path $top 'artifacts/verify'
New-Item -ItemType Directory -Path $out -Force | Out-Null
[ordered]@{ status = $status; complete = ($status -eq 'pass'); startedAt = [DateTime]::UtcNow.ToString('o'); head = $head; treeHash = $tree; filter = $null; gates = @([ordered]@{ name = 'noop'; status = 'pass' }) } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $out 'verdict.json')
if ($status -eq 'pass') { Write-Host 'VERIFY PASSED: every gate passed.'; exit 0 }
Write-Host "VERIFY FAILED (stub $status)"
exit 1
'@
    $stubPath = @{}
    foreach ($name in $stubBodies.Keys)
    {
        $stubPath[$name] = Join-Path $stubDir "ship-$name.ps1"
        [IO.File]::WriteAllText($stubPath[$name], "# aw-selftest-stub`r`n" + $stubBodies[$name] + "`r`n")
    }
    $shipCommon = @('-GhScript', $stubPath['gh'], '-MergePrScript', $stubPath['mergepr'], '-VerifyScript', $stubPath['verify'], '-ChannelMinutes', '0', '-ReleasePollSeconds', '1')
    $defaults = @{ VERMODE = 'pass'; IMPACT = 'not-required'; DESKTOPMODE = 'pass'; DESKTOP = 'unlocked'; PINMODE = 'none'; MERGEMODE = 'merged'; CHANNELMODE = 'ok'; GH_FAIL = ''; SHIP_CRASH = ''; MERGE_AFTER = '' }
    $setEnv = {
        param([hashtable]$Set = @{})
        $all = $defaults.Clone()
        foreach ($key in $Set.Keys) { $all[$key] = $Set[$key] }
        foreach ($key in $all.Keys) { [Environment]::SetEnvironmentVariable("AW_PKG_TEST_$key", $(if ($all[$key] -ne '') { $all[$key] } else { $null })) }
    }
    $ship = { param([string]$Slug, [string[]]$More = @()) & $pkg (@('ship', '-Id', $Slug) + $shipCommon + @($More)) }
    $calls = { param([string]$Pattern) $lines = if (Test-Path $shipLog) { @(Get-Content -LiteralPath $shipLog) } else { @() }; @($lines | Where-Object { $_ -match $Pattern }).Count }
    $resetLog = { Remove-Item -LiteralPath $shipLog -Force -ErrorAction SilentlyContinue; Remove-Item -LiteralPath $ghDir -Recurse -Force -ErrorAction SilentlyContinue; New-Item -ItemType Directory -Path $ghDir -Force | Out-Null }
    $originRef = { param($slug) ([string](& git -C $origin rev-parse --verify --quiet "refs/heads/pkg/$slug" 2>$null)).Trim() }
    $dropRemote = { param($slug) & git -C $origin update-ref -d "refs/heads/pkg/$slug" 2>$null | Out-Null }
    $dropLockFile = { Remove-Item -LiteralPath (Join-Path $main '.git/selftest/ship.json') -Force -ErrorAction SilentlyContinue }
    $softReset = {
        param($slug)
        Update-State $packagesRoot $slug 'test-soft-reset' { param($s) $s.ship = $null; $s.stage = 'accepted' } | Out-Null
        & $dropRemote $slug
        & $dropLockFile
        & $resetLog
    }
    $newAccepted = {
        param($slug)
        & $newPackage $slug $rvManifest
        & $write $slug "feature-$slug.txt" "v2 $slug`r`n"
        $r = & $pkg @('check', '-Id', $slug)
        & $expect ($r.Exit -eq 0) "ship test package $slug : check failed: $($r.Text)"
        $r = & $pkg @('commit', '-Id', $slug, '-Message', "Ship test $slug")
        & $expect ($r.Exit -eq 0) "ship test package $slug : commit failed: $($r.Text)"
        & $resetR
        $env:AW_PKG_TEST_VMODE = 'pass'
        $r = & $pkg @('review', '-Id', $slug, '-Level', 'light', '-ClaudePath', $rvClaude)
        & $expect ($r.Exit -eq 0) "ship test package $slug : review failed: $($r.Text)"
        $r = & $pkg @('accept', '-Id', $slug, '-Reason', 'ship test')
        & $expect ($r.Exit -eq 0 -and (& $stateFile $slug).stage -eq 'accepted') "ship test package $slug : accept failed: $($r.Text)"
    }

    # item 17: a main checkout that is behind origin/master may hold a stale configuration. ship refuses it before anything is rebased, pushed or
    # merged, and names -SyncMain; the main checkout is then put back where it was. The caller runs the ship again with an up to date main checkout.
    $assertStaleMainRefused = {
        param([string]$Slug)
        & $setEnv @{}
        & $resetLog
        $mainTip = ([string](& git -C $main rev-parse HEAD)).Trim()
        & git -C $main reset -q --hard "$mainTip~1" 2>&1 | Out-Null
        $res = & $ship $Slug
        $state = & $stateFile $Slug
        # The refusal belongs to the base step before the push. Once this ship's merge call has run (or is running), origin/<base> may hold the
        # merge commit while the main checkout lags, and a resume must not be refused for that.
        $unitInfo = Get-RepoInfo $main
        $unitWt = [string]$state.worktree
        $unitBase = [string]$state.baseRef
        $resumeState = @{ ship = @{ steps = @{ merge = @{ status = 'done' } } } }
        $runningState = @{ ship = @{ steps = @{ merge = @{ status = 'running' } } } }
        $pendingState = @{ ship = @{ steps = @{ merge = @{ status = 'pending' } } } }
        $stillRefused = Get-ShipMainCheckoutProblem $unitInfo $pendingState $unitWt $unitBase
        $notRefusedDone = Get-ShipMainCheckoutProblem $unitInfo $resumeState $unitWt $unitBase
        $notRefusedRunning = Get-ShipMainCheckoutProblem $unitInfo $runningState $unitWt $unitBase
        & git -C $main reset -q --hard $mainTip 2>&1 | Out-Null
        & $kitCase ($null -ne $stillRefused -and $null -eq $notRefusedDone -and $null -eq $notRefusedRunning) 'item 17: a ship that has begun its merge is never refused for a lagging main checkout, only the base step before the push is' "refused before merge: $($null -ne $stillRefused); after merge done: $($null -ne $notRefusedDone); running: $($null -ne $notRefusedRunning)"
        & $kitCase ($res.Exit -ne 0 -and $state.stage -eq 'ship-failed' -and $state.ship.steps.base.status -eq 'failed' -and $state.ship.steps.base.cause -eq 'main-checkout-not-at-base' -and $res.Text -match 'main checkout .* is at [0-9a-f]{10} but origin/master is at [0-9a-f]{10}' -and $res.Text -match '-SyncMain' -and (& $calls '.') -eq 0 -and -not $state.ship.steps.Contains('verify') -and ([string](& git -C $main rev-parse HEAD)).Trim() -eq $mainTip) 'item 17: ship refuses a main checkout that is not at origin/master and names -SyncMain' "exit $($res.Exit) stage $($state.stage) $($res.Text)"
    }

    # A candidate kit (install.ps1 -Candidate) never ships: with such a lock in the package worktree ship refuses before it does anything.
    $assertCandidateRefused = {
        param([string]$Slug)
        & $setEnv @{}
        & $resetLog
        $candidateWt = [string](& $stateFile $Slug).worktree
        $candidateLock = Join-Path $candidateWt 'agent-workflow.lock.json'
        $stageBefore = [string](& $stateFile $Slug).stage
        # The lock that counts is the committed one: the candidate lock is committed on the package head, and removed again afterwards.
        $headBefore = ([string](& git -C $candidateWt rev-parse HEAD)).Trim()
        [IO.File]::WriteAllText($candidateLock, '{ "schemaVersion": 1, "source": "x", "tag": null, "candidate": true, "commit": "0123456789012345678901234567890123456789", "manifestSha256": "x", "files": {} }')
        & git -C $candidateWt add -- agent-workflow.lock.json 2>&1 | Out-Null
        & git -C $candidateWt -c user.name=SelfTest -c user.email=selftest@example.invalid commit -q -m 'candidate lock' 2>&1 | Out-Null
        try { $res = & $ship $Slug }
        finally
        {
            & git -C $candidateWt reset -q --hard $headBefore 2>&1 | Out-Null
            Remove-Item -LiteralPath $candidateLock -Force -ErrorAction SilentlyContinue
        }
        $after = & $stateFile $Slug
        & $kitCase ($res.Exit -ne 0 -and $res.Text -match 'ship refused' -and $res.Text -match 'candidate install' -and $res.Text -match 'install a tagged release first' -and [string]$after.stage -eq $stageBefore -and (& $calls '.') -eq 0) 'candidate: package ship refuses a worktree whose vendored kit lock is a candidate' "exit $($res.Exit) stage $($after.stage) $($res.Text)"
    }

    if ($Quick)
    {
        # -Quick: one ship, of the package bx that Invoke-PackageBSelfTest left accepted: base, pin, verify, record, qualify, push, pr and merge, each recorded.
        & $setEnv @{}
        & $resetLog
        & $assertCandidateRefused 'bx'
        & $assertStaleMainRefused 'bx'
        & $setEnv @{}
        & $resetLog
        $res = & $ship 'bx'
        $state = & $stateFile 'bx'
        $steps = $state.ship.steps
        & $expect ($res.Exit -eq 0 -and $state.stage -eq 'merged' -and $steps.base.status -eq 'done' -and $steps.verify.status -eq 'done' -and $steps.qualify.status -eq 'done' -and $steps.push.status -eq 'done' -and $steps.pr.status -eq 'done' -and $steps.merge.status -eq 'done' -and (& $calls '^verify$') -eq 1 -and (& $calls '^push') -eq 1 -and (& $calls 'pr create') -eq 1 -and (& $calls '^mergepr .*-Auto -NoPluginPin') -eq 1 -and -not (Test-Path $state.worktree)) "the quick ship of bx did not reach merged through every step: exit $($res.Exit) stage $($state.stage) $($res.Text)"
        $res = & $pkg @('show', '-Id', 'bx', '-Json')
        $view = $res.Text | ConvertFrom-Json
        & $expect ($res.Exit -eq 0 -and $view.stage -eq 'merged' -and @($view.ship.steps).Count -ge 7) "show does not list the ship steps of a merged package: $($res.Text)"
        return
    }

    # ---- the packages: all accepted on the same base before anything moves it ----
    foreach ($slug in @('sa', 'sc', 'sb', 'sd')) { & $newAccepted $slug }
    # The package of the null-hook cases is set up without counting its checks, so the case list stays the one of the original self-test.
    $expect = { param([bool]$Condition, [string]$Message) if (-not $Condition) { $failures.Add($Message) } }
    & $newAccepted 'sn'
    Remove-Variable -Name expect -Scope Local
    $aWt = & $wtOf 'sa'
    $aHead = & $headOf 'sa'
    $aState = Join-Path $root 'sa-accepted-state.json'
    Copy-Item -LiteralPath (& $statePath 'sa') -Destination $aState -Force
    $restoreA = {
        $aTree = [string](Get-Content -LiteralPath $aState -Raw | ConvertFrom-Json).worktree
        & git -C $main worktree prune
        if (-not (Test-Path $aTree)) { & git -C $main worktree add $aTree 'pkg/sa' 2>&1 | Out-Null }
        & git -C $aTree reset --hard $aHead 2>&1 | Out-Null
        & git -C $aTree clean -fdxq
        Copy-Item -LiteralPath $aState -Destination (& $statePath 'sa') -Force
        & $dropRemote 'sa'
        & $dropLockFile
        & $resetLog
        & $setEnv @{}
    }
    $info = Get-RepoInfo $main

    # ---- hooks that are not configured: the records say so, they never read as passed, and nothing runs ----
    # The configuration is read from the main checkout at each start, so the working copy there is switched for this ship only.
    $configPath = Join-Path $main 'agent-workflow.json'
    $configText = [IO.File]::ReadAllText($configPath)
    try
    {
        $nullConfig = $configText | ConvertFrom-Json -AsHashtable
        $nullConfig.hooks.qualify = $null
        $nullConfig.hooks.channels = $null
        [IO.File]::WriteAllText($configPath, ($nullConfig | ConvertTo-Json -Depth 20))
        & $setEnv @{}
        & $resetLog
        $r = & $ship 'sn'
        $state = & $stateFile 'sn'
        $nq = Get-Field $state.ship.steps 'qualify'
        $nc = Get-Field $state.ship.steps 'channels'
        $nullBody = if (Test-Path (Join-Path $ghDir 'body-101.md')) { Get-Content -LiteralPath (Join-Path $ghDir 'body-101.md') -Raw } else { '' }
        $nullShow = (& $pkg @('show', '-Id', 'sn')).Text
        & $kitCase ($r.Exit -eq 0 -and $state.stage -eq 'merged' -and $nq -and $nq.status -eq 'not-configured' -and $nq.configured -eq $false -and $nq.required -eq $false -and $nq.status -notin @('done', 'passed') -and -not $nq.ContainsKey('result') -and $r.Text -match 'desktop qualification is not configured' -and $nullBody -match 'desktop qualification: not configured' -and $nullBody -notmatch 'desktop qualification: not required' -and $nullBody -match 'No desktop qualification: hooks\.qualify is not configured' -and $nullShow -match 'qualify\s+not-configured' -and (& $calls '^impact') -eq 0 -and (& $calls '^desktop') -eq 0) 'hook contract: qualify null' "exit $($r.Exit) stage $($state.stage) qualify $($nq | ConvertTo-Json -Compress) $($r.Text)"
        & $kitCase ($nc -and $nc.status -eq 'not-configured' -and $nc.configured -eq $false -and $nc.status -ne 'done' -and $r.Text -match 'release channels are not configured' -and (& $calls '^channels') -eq 0 -and (& $calls '^gh release') -eq 0 -and @(((& $pkg @('show', '-Id', 'sn', '-Json')).Text | ConvertFrom-Json).nextTransitions).Count -eq 0) 'hook contract: channels null' "exit $($r.Exit) channels $($nc | ConvertTo-Json -Compress) $($r.Text)"
        & $kitCase ($r.Exit -eq 0 -and $state.stage -eq 'merged' -and $nq.status -eq 'not-configured' -and $nq.configured -eq $false -and $state.ship.steps.base.status -eq 'done' -and ([string](& git -C $main rev-parse HEAD)).Trim() -eq ([string](& git -C $main rev-parse origin/master)).Trim()) 'item 17: with the main checkout at origin/master and hooks.qualify null in its current configuration, ship still records qualify as not-configured' "exit $($r.Exit) stage $($state.stage) qualify $($nq | ConvertTo-Json -Compress)"
    }
    finally { [IO.File]::WriteAllText($configPath, $configText) }
    & $setEnv @{}
    & $resetLog

    # ---- guards, refusals: a stale main checkout, no acceptance, no stubs, a later non-pin commit ----
    & $assertCandidateRefused 'sa'
    & $assertStaleMainRefused 'sa'
    & $restoreA
    & $setEnv @{}
    & $resetLog
    $r = & $pkg @('ship', '-Id', 'sa')
    & $expect ($r.Exit -ne 0 -and $r.Text -match 'self-test guard' -and (& $calls '.') -eq 0) "ship ran in the self-test without marked stubs: $($r.Text)"
    $r = & $ship 'ac'
    & $expect ($r.Exit -ne 0 -and $r.Text -match "ship is not allowed in stage '(committed|review-invalid)'" -and (& $calls '.') -eq 0 -and (& $stateFile 'ac').stage -notin @('shipping', 'accepted', 'merged')) "ship was allowed without an acceptance: $($r.Text)"
    [IO.File]::WriteAllText((Join-Path $aWt 'later.txt'), "after the acceptance`r`n")
    & $git $aWt @('add', '-A'); & $git $aWt @('commit', '-qm', 'a later non-pin commit')
    $r = & $ship 'sa'
    $state = & $stateFile 'sa'
    & $expect ($r.Exit -eq 3 -and $state.stage -eq 'needs-review' -and $state.acceptance.invalidated.reason -match 'later\.txt' -and $state.commit.sha -eq (& $headOf 'sa') -and $state.checks[-1].ok -and $r.Text -match 'review -Id sa' -and (& $calls '.') -eq 0) "a later non-pin commit did not return the package to needs-review (or ship went ahead): exit $($r.Exit) stage $($state.stage) $($r.Text)"
    $r = & $ship 'sa'
    & $expect ($r.Exit -ne 0 -and $r.Text -match "stage 'needs-review'" -and (& $calls '.') -eq 0) "ship was allowed in needs-review after a later commit: $($r.Text)"
    & $restoreA

    # ---- the ship lock ----
    $held = Enter-ShipLock $info 'other-package'
    try
    {
        $r = & $ship 'ac'
        & $expect ($r.Exit -ne 0 -and $r.Text -match 'another ship is in flight' -and $r.Text -match 'other-package') "a second ship ran while the lock was held: $($r.Text)"
    }
    finally { Exit-ShipLock $held $info }
    & $expect (-not (Test-Path (Join-Path $main '.git/selftest/ship.json'))) 'releasing the ship lock left its info file behind'
    $deadStart = '2000-01-01T00:00:00.0000000+00:00'
    [IO.File]::WriteAllText((Join-Path $main '.git/selftest/ship.json'), (@{ pid = $deadWrapper.Id; startUtc = $deadStart; slug = 'ghost'; utc = '2026-01-01T00:00:00Z' } | ConvertTo-Json))
    $r = & $ship 'ac'
    & $expect ($r.Exit -ne 0 -and $r.Text -match 'stale ship lock' -and $r.Text -match "ship -Id ghost" -and (Test-Path (Join-Path $main '.git/selftest/ship.json'))) "a stale lock of another package was not reported and kept: $($r.Text)"
    & $dropLockFile

    # ---- the plugin pin step ----
    & $setEnv @{ PINMODE = 'pin'; VERMODE = 'fail' }
    $r = & $ship 'sa'
    $state = & $stateFile 'sa'
    if (-not $state.ship -or -not $state.ship.steps.ContainsKey('pin')) { throw "ship recorded no pin step (exit $($r.Exit), stage $($state.stage)): $($r.Text)" }
    $pinStep = $state.ship.steps.pin
    & $expect ($r.Exit -eq 1 -and $state.stage -eq 'ship-failed' -and $pinStep.status -eq 'done' -and $pinStep.pinOnly -and $pinStep.changed -and -not $state.acceptance.invalidated -and (& $headOf 'sa') -ne $aHead -and $state.ship.steps.verify.status -eq 'failed') "a pin-only commit did not keep the acceptance: $($r.Text)"
    & $expect ($r.Text -match 'verify failed' -and $r.Text -match 'verdict\.json' -and $state.ship.steps.verify.receipt.status -eq 'fail' -and (& $calls '^verify$') -eq 1 -and (& $calls '^gh ') -eq 0 -and (& $calls 'push') -eq 0) "a verify failure was not reported with its receipt, or something ran after it: $($r.Text)"
    $view = (& $pkg @('show', '-Id', 'sa', '-Json')).Text | ConvertFrom-Json
    & $expect ($view.acceptance.status -match '^valid' -and @($view.ship.steps).Count -eq 9 -and (@($view.ship.steps | Where-Object { $_.step -eq 'verify' })[0].status -eq 'failed') -and @($view.nextTransitions) -contains 'ship (resumes at the failed step)') "show does not list the ship steps and the next command: $($view | ConvertTo-Json -Depth 4 -Compress)"
    & $restoreA
    & $setEnv @{ PINMODE = 'pin-extra' }
    $r = & $ship 'sa'
    $state = & $stateFile 'sa'
    & $expect ($r.Exit -eq 3 -and $state.stage -eq 'needs-review' -and $state.acceptance.invalidated.reason -match 'plugin pin step changed more than the pin' -and $state.commit.sha -eq (& $headOf 'sa') -and $r.Text -match 'review -Id sa' -and (& $calls '^verify$') -eq 0 -and (& $calls '^gh ') -eq 0) "a pin step that changed other files kept the acceptance or did not return to review: $($r.Text)"
    $r = & $ship 'sa'
    & $expect ($r.Exit -ne 0 -and $r.Text -match "stage 'needs-review'") "ship was allowed in needs-review: $($r.Text)"
    & $restoreA
    & $setEnv @{ PINMODE = 'pin-dirty' }
    $r = & $ship 'sa'
    $state = & $stateFile 'sa'
    & $expect ($r.Exit -ne 0 -and $state.stage -eq 'checks-passed' -and $state.acceptance.invalidated -and (& $calls '^verify$') -eq 0) "a pin step that left the worktree dirty kept the acceptance: $($r.Text)"
    & $restoreA

    # ---- verify failure, resume, desktop qualification ----
    & $setEnv @{ VERMODE = 'fail' }
    $r = & $ship 'sa'
    $state = & $stateFile 'sa'
    & $expect ($r.Exit -eq 1 -and $state.stage -eq 'ship-failed' -and $state.ship.steps.verify.status -eq 'failed' -and $state.ship.steps.verify.head -eq $aHead -and $state.ship.steps.verify.receipt.path -match 'verdict\.json' -and (& $calls 'push') -eq 0) "a verify failure did not stop at ship-failed with the receipt: $($r.Text)"
    & $setEnv @{ IMPACT = 'required' }
    $r = & $ship 'sa'
    $state = & $stateFile 'sa'
    $verifyHead = $state.ship.steps.verify.head
    & $expect ($r.Exit -eq 3 -and $state.stage -eq 'desktop-required' -and $r.Text -match 'pwsh scripts/package\.ps1 ship -Id sa -AllowDesktop' -and (& $calls '^desktop') -eq 0 -and (& $calls 'push') -eq 0 -and $verifyHead -eq $aHead -and $state.ship.steps.verify.receipt.treeHash -eq $state.ship.steps.verify.tree) "desktop qualification was not required without the switch, or the command was not printed: $($r.Text)"
    $shown = (& $pkg @('show', '-Id', 'sa')).Text
    & $expect ($shown -match 'next: ship -AllowDesktop' -and $shown -match 'qualify\s+stopped') "show does not name the next command after desktop-required: $shown"
    & $setEnv @{ IMPACT = 'required'; DESKTOP = 'locked' }
    $r = & $ship 'sa' @('-AllowDesktop')
    & $expect ($r.Exit -eq 3 -and (& $stateFile 'sa').stage -eq 'desktop-required' -and $r.Text -match 'locked' -and (& $calls '^desktop') -eq 0) "the desktop was used although the session is locked: $($r.Text)"
    & $setEnv @{ IMPACT = 'required'; DESKTOPMODE = 'fail' }
    $r = & $ship 'sa' @('-AllowDesktop')
    $state = & $stateFile 'sa'
    & $expect ($r.Exit -eq 1 -and $state.stage -eq 'ship-failed' -and $r.Text -match 'ANNOUNCE' -and (& $calls '^desktop') -eq 1 -and @($state.qualifications).Count -eq 1 -and $state.qualifications[0].result -eq 'failed' -and (& $calls 'push') -eq 0) "a failed desktop run was not ship-failed: $($r.Text)"
    & $setEnv @{ IMPACT = 'required'; GH_FAIL = 'create' }
    $r = & $ship 'sa' @('-AllowDesktop')
    $state = & $stateFile 'sa'
    $qualification = $state.qualifications[-1]
    & $kitCase ($state.ship.steps.qualify.status -eq 'done' -and $state.ship.steps.qualify.required -eq $true -and $state.ship.steps.qualify.result -eq 'passed' -and $state.ship.steps.qualify.head -eq $aHead -and (& $calls '^impact') -ge 1 -and (& $calls '^desktop') -eq 2 -and @($state.ship.steps.qualify.packages).Count -eq 2) 'hook contract: qualify required' "$($state.ship.steps.qualify | ConvertTo-Json -Compress -Depth 4)"
    & $expect ($r.Exit -eq 1 -and $state.stage -eq 'ship-failed' -and $state.ship.steps.qualify.status -eq 'done' -and $state.ship.steps.qualify.qualificationId -eq $qualification.id -and $qualification.result -eq 'passed' -and $qualification.head -eq $aHead -and $qualification.tree -eq (& git -C $aWt rev-parse 'HEAD^{tree}').Trim() -and @($qualification.packages).Count -eq 2 -and $qualification.packages[0].sha256 -match '^[0-9a-f]{64}$' -and (& $calls '^desktop') -eq 2 -and (& $calls '^push') -eq 1 -and $state.ship.steps.push.status -eq 'done' -and $state.ship.steps.pr.status -eq 'failed') "a passing desktop run was not recorded bound to the head, tree and artifacts, or the PR failure was not reported: $($r.Text)"
    & $expect ($state.ship.steps.verify.status -eq 'done' -and (& $calls '^verify$') -eq 2) 'verify ran more than twice (the failure and the passing rerun): a valid receipt for the same head and tree was not reused'
    # The qualification is reused for the same head and tree: no desktop, no switch.
    & $softReset 'sa'
    $r = & $ship 'sa'
    $state = & $stateFile 'sa'
    & $expect ($r.Text -match 'reusing qualification' -and $state.ship.steps.qualify.reused -and (& $calls '^desktop') -eq 0 -and $state.stage -eq 'ship-failed') "a recorded qualification for the same head and tree was not reused: $($r.Text)"
    # Bound to another head: refused.
    $originalHead = $qualification.head
    Update-State $packagesRoot 'sa' 'test-tamper' { param($s) $s.qualifications[-1].head = ('f' * 40) } | Out-Null
    & $softReset 'sa'
    $r = & $ship 'sa'
    & $expect ($r.Exit -eq 3 -and (& $stateFile 'sa').stage -eq 'desktop-required' -and $r.Text -match 'bound to head f{40}' -and (& $calls '^desktop') -eq 0) "a qualification bound to another head was reused: $($r.Text)"
    Update-State $packagesRoot 'sa' 'test-untamper' { param($s) $s.qualifications[-1].head = $originalHead } | Out-Null
    # The candidate artifact changed on disk: refused.
    $artifact = Join-Path $qualification.workDirectory "candidate/$($qualification.packages[0].name)"
    Add-Content -LiteralPath $artifact -Value 'tampered'
    & $softReset 'sa'
    $r = & $ship 'sa'
    & $expect ($r.Exit -eq 3 -and (& $stateFile 'sa').stage -eq 'desktop-required' -and $r.Text -match 'differs from the recorded hash' -and (& $calls '^desktop') -eq 0) "a qualification whose candidate artifact changed was reused: $($r.Text)"
    & $restoreA

    # ---- crash after the push, crash after the PR, resume, -NoWait, MERGED only, the PR body, the channels ----
    & $setEnv @{ SHIP_CRASH = 'push-before-record' }
    $r = & $ship 'sa' @('-Title', 'Explicit PR title')
    $state = & $stateFile 'sa'
    & $expect ($r.Exit -eq 99 -and $state.stage -eq 'shipping' -and (& $originRef 'sa') -eq $aHead -and (& $calls '^push') -eq 1 -and (& $calls 'pr create') -eq 0 -and (Test-Path (Join-Path $main '.git/selftest/ship.json'))) "the crash after the push did not leave the expected state: exit $($r.Exit) stage $($state.stage) $($r.Text)"
    & $setEnv @{ SHIP_CRASH = 'pr-before-record' }
    $r = & $ship 'sa' @('-Title', 'Explicit PR title')
    $state = & $stateFile 'sa'
    & $expect ($r.Exit -eq 99 -and $r.Text -match 'stale ship lock' -and $r.Text -match 'already on origin' -and (& $calls '^push') -eq 1 -and (& $calls 'pr create') -eq 1 -and $state.stage -eq 'shipping' -and $state.ship.steps.push.status -eq 'done') "the resume after the push pushed again, did not report the stale lock, or did not reach the PR: exit $($r.Exit) $($r.Text)"
    & $setEnv @{ MERGEMODE = 'merged' }
    $r = & $ship 'sa' @('-NoWait')
    $state = & $stateFile 'sa'
    & $expect ($r.Exit -eq 0 -and $state.stage -eq 'auto-merge-enabled' -and $r.Text -match 'already exists' -and (& $calls 'pr create') -eq 1 -and (& $calls '^push') -eq 1 -and (& $calls '^mergepr .*-Auto') -eq 1 -and (& $calls '^mergepr .*-WaitMerged') -eq 0 -and $r.Text -match 'NOT merged yet' -and $state.stage -ne 'merged') "-NoWait did not stop at auto-merge-enabled without a second PR or push: exit $($r.Exit) stage $($state.stage) $($r.Text)"
    $body = Get-Content -LiteralPath (Join-Path $ghDir 'body-101.md') -Raw
    & $expect ($body -match '## Summary' -and $body -match 'Use the checked executor launcher\.' -and $body -match '## Acceptance criteria' -and $body -match '\*\*C1\*\* The feature file exists\.' -and $body -match 'reviewer result: pass' -and $body -match '## Checks run' -and $body -match 'verify\.ps1: pass, complete True' -and $body -match "head ``$aHead``" -and $body -match 'codex:gpt-6-luna' -and $body -match 'claude-sonnet' -and $body -match 'light, round' -and $body -match '## What was not tested' -and $body -match 'integration tests were not run' -and $body -match 'No desktop run' -and $body -match 'desktop qualification: not required') "the PR body lacks the criteria, checks, identities or the not-tested section: $body"
    & $expect ($body -notmatch '(?i)generated with|co-authored-by|claude code' -and (Get-Content -LiteralPath (Join-Path $ghDir 'title-101.txt') -Raw) -eq 'Explicit PR title') 'the PR body has an attribution line, or -Title was ignored'
    & $expect ((Get-ShipTitle (Join-Path $script:KitDir 'fixtures/gates/agent-run/complete.md') '') -eq 'Objective / Why') 'the title is not the first H1 of the brief'
    $briefWithPrefix = Join-Path $root 'brief-prefix.md'
    [IO.File]::WriteAllText($briefWithPrefix, "# Brief: PR9 - Something useful`r`n`r`n## Objective`r`n`r`nDo the thing`r`nover two lines.`r`n`r`nSecond paragraph.`r`n")
    & $expect ((Get-ShipTitle $briefWithPrefix '') -eq 'PR9 - Something useful' -and (Get-ShipObjective $briefWithPrefix) -eq 'Do the thing over two lines.') 'the title or the objective is not taken from the brief'
    & $expect ((& $calls '^mergepr .*-Auto -NoPluginPin') -eq 1) 'merge-pr.ps1 -Auto was called without -NoPluginPin, so it could commit and push a pin after verify'

    # A second ship while auto-merge is enabled for the same head waits on it; it does not call merge-pr.ps1 again, however often it runs.
    foreach ($again in 1..2)
    {
        $r = & $ship 'sa' @('-NoWait')
        $state = & $stateFile 'sa'
        & $expect ($r.Exit -eq 0 -and $state.stage -eq 'auto-merge-enabled' -and $r.Text -match 'merge-pr\.ps1 is not called again' -and (& $calls '^mergepr .*-Auto') -eq 1 -and (& $calls 'pr create') -eq 1 -and (& $calls '^push') -eq 1 -and $state.ship.steps.merge.calls -eq 1) "rerun $again at auto-merge-enabled called merge-pr.ps1 or pushed again: exit $($r.Exit) $($r.Text)"
    }

    # A failed gh pr view stops the ship but keeps what is known about the auto-merge: the next run, which sees it OPEN with the auto-merge
    # request, still does not call merge-pr.ps1 -Auto for the same head.
    & $setEnv @{ GH_FAIL = 'view' }
    $r = & $ship 'sa' @('-NoWait')
    $state = & $stateFile 'sa'
    & $expect ($r.Exit -eq 1 -and $state.stage -eq 'ship-failed' -and $r.Text -match 'gh pr view' -and $state.ship.steps.merge.enabledHead -eq $aHead -and $state.ship.steps.merge.confirmed -eq $true -and (& $calls '^mergepr .*-Auto') -eq 1) "a failed gh pr view dropped the auto-merge record or called merge-pr.ps1: exit $($r.Exit) $($state.ship.steps.merge | ConvertTo-Json -Compress) $($r.Text)"
    & $setEnv @{}
    $r = & $ship 'sa' @('-NoWait')
    $state = & $stateFile 'sa'
    & $expect ($r.Exit -eq 0 -and $state.stage -eq 'auto-merge-enabled' -and $r.Text -match 'merge-pr\.ps1 is not called again' -and (& $calls '^mergepr .*-Auto') -eq 1) "the rerun after a failed gh pr view enabled the auto-merge again: exit $($r.Exit) $($r.Text)"

    # The evidence is bound to the head to be merged: a receipt or qualification for another head or tree is refused, and the refusal ships nothing.
    $evidenceState = Read-State $packagesRoot 'sa'
    $evidenceClean = @(Get-ShipEvidenceProblems $evidenceState $aHead $aWt)
    & $expect ($evidenceClean.Count -eq 0) "the evidence of the shipped head was refused: $($evidenceClean -join '; ')"
    $evidenceState.ship.steps.verify.head = ('f' * 40)
    & $expect ((@(Get-ShipEvidenceProblems $evidenceState $aHead $aWt) -join ' ') -match 'verify\.ps1 receipt is bound to head f{40}') 'a verify receipt for another head was accepted as evidence'
    $evidenceState.ship.steps.verify.head = $aHead
    $evidenceState.ship.steps.qualify.tree = ('e' * 40)
    & $expect ((@(Get-ShipEvidenceProblems $evidenceState $aHead $aWt) -join ' ') -match 'desktop-qualification decision is bound to head') 'a qualification decision for another tree was accepted as evidence'
    Update-State $packagesRoot 'sa' 'test-tamper-verify' { param($s) $s.ship.steps.verify.tree = ('d' * 40) } | Out-Null
    $r = & $ship 'sa' @('-NoWait')
    $state = & $stateFile 'sa'
    & $expect ($r.Exit -eq 1 -and $state.stage -eq 'ship-failed' -and $r.Text -match 'evidence does not cover the head' -and (& $calls '^mergepr .*-Auto') -eq 1) "a merge went ahead on a verify receipt bound to another tree: exit $($r.Exit) $($r.Text)"
    Update-State $packagesRoot 'sa' 'test-untamper-verify' { param($s) $s.ship.steps.verify.tree = (Get-WorkingTreeHash $aWt) } | Out-Null

    # The head moves by the plugin pin after the push: the steps run again for the new head, the new head is pushed, verified and gated,
    # and merge-pr.ps1 is called again (with -NoPluginPin) because its gates ran for the old head only.
    $verifiesBefore = & $calls '^verify$'
    & $writePins $aWt '1.0.0'; & $git $aWt @('add', '-A'); & $git $aWt @('commit', '-qm', 'pin both files')
    $r = & $ship 'sa' @('-NoWait')
    $state = & $stateFile 'sa'
    $aHead2 = & $headOf 'sa'
    & $expect ($r.Exit -eq 0 -and $aHead2 -ne $aHead -and $state.stage -eq 'auto-merge-enabled' -and -not $state.acceptance.invalidated -and (& $calls '^push') -eq 2 -and (& $originRef 'sa') -eq $aHead2 -and (& $calls '^verify$') -eq $verifiesBefore + 1 -and $state.ship.steps.verify.head -eq $aHead2 -and $state.ship.steps.merge.enabledHead -eq $aHead2 -and (& $calls '^mergepr .*-Auto') -eq 2 -and (& $calls '^mergepr .*-Auto -NoPluginPin') -eq 2 -and (& $calls 'pr create') -eq 1) "a head moved by the pin after the push was not re-verified, pushed and re-gated: exit $($r.Exit) $($r.Text)"
    # The pull request exists for the old head: its description is regenerated for the new one (head, tree, receipt), and edited, not recreated.
    $editedBody = Get-Content -LiteralPath (Join-Path $ghDir 'body-101.md') -Raw
    & $expect ((& $calls '^gh pr edit') -eq 2 -and $editedBody -match "head ``$aHead2``" -and $editedBody -notmatch $aHead -and $editedBody -match "tree ``$($state.ship.steps.verify.tree)``" -and $state.ship.steps.pr.bodyHead -eq $aHead2 -and $state.ship.steps.pr.head -eq $aHead2) "the pull request description still names the earlier head after the pin advance: $editedBody"
    & $setEnv @{ GH_FAIL = 'edit' }
    Update-State $packagesRoot 'sa' 'test-pr-head-moved' { param($s) $s.ship.steps.pr.bodyHead = ('c' * 40) } | Out-Null
    $r = & $ship 'sa' @('-NoWait')
    $state = & $stateFile 'sa'
    & $expect ($r.Exit -eq 1 -and $state.stage -eq 'ship-failed' -and $state.ship.steps.pr.status -eq 'failed' -and $r.Text -match 'gh pr edit failed' -and (& $calls '^mergepr .*-Auto') -eq 2) "a failed description update did not stop the ship before the merge: exit $($r.Exit) $($r.Text)"
    & $setEnv @{}
    $r = & $ship 'sa' @('-NoWait')
    $state = & $stateFile 'sa'
    & $expect ($r.Exit -eq 0 -and $state.stage -eq 'auto-merge-enabled' -and $state.ship.steps.pr.status -eq 'done' -and $state.ship.steps.pr.bodyHead -eq $aHead2 -and (& $calls 'pr create') -eq 1 -and (& $calls '^mergepr .*-Auto') -eq 2) "the rerun after a failed description update did not repair it without a second PR or merge call: exit $($r.Exit) $($r.Text)"

    # A crash inside the merge-pr.ps1 call: no auto-merge is visible on the pull request, so the call is repeated, waiting this time.
    # GitHub never merges in this case, and the package must not claim it did.
    Update-State $packagesRoot 'sa' 'test-crash-in-merge-call' { param($s) $s.ship.steps.merge.status = 'running'; $s.ship.steps.merge.confirmed = $false } | Out-Null
    $prsPath = Join-Path $ghDir 'prs.json'
    $stubPrs = @(Get-Content -LiteralPath $prsPath -Raw | ConvertFrom-Json)
    foreach ($stubPr in $stubPrs) { $stubPr.autoMerge = $false }
    ConvertTo-Json -InputObject @($stubPrs) -Depth 6 | Set-Content -LiteralPath $prsPath
    & $setEnv @{ MERGEMODE = 'liar' }
    $r = & $ship 'sa'
    $state = & $stateFile 'sa'
    & $expect ($r.Exit -eq 1 -and $state.stage -eq 'auto-merge-enabled' -and $state.stage -ne 'merged' -and $r.Text -match 'not MERGED' -and (& $calls '^mergepr .*-WaitMerged') -eq 1 -and (& $calls '^mergepr .*-Auto') -eq 3 -and (& $calls 'pr create') -eq 1) "merged was set without GitHub reporting MERGED: $($r.Text)"

    # A plain ship while GitHub's auto-merge is pending waits for the merge without another call, records MERGED, removes the worktree and reports the channels.
    Remove-Item -LiteralPath (Join-Path $ghDir 'views.txt') -Force -ErrorAction SilentlyContinue
    & $setEnv @{ MERGEMODE = 'merged'; CHANNELMODE = 'failed'; MERGE_AFTER = '1' }
    $r = & $ship 'sa'
    $state = & $stateFile 'sa'
    & $expect ($r.Exit -eq 1 -and $state.stage -eq 'merged' -and $state.ship.steps.merge.status -eq 'done' -and $state.ship.steps.merge.mergeCommit -match '^[0-9a-f]{40}$' -and -not (Test-Path $aWt) -and $r.Text -match 'waiting for GitHub to merge' -and $state.ship.steps.channels.final -eq 'failed' -and $r.Text -match 'channel store:example\.app failed' -and (& $calls 'pr create') -eq 1 -and (& $calls '^mergepr .*-Auto') -eq 3 -and (& $calls '^mergepr -Pr \d+ -Worktree \S+ -Root \S+$') -eq 1 -and (& $calls '^channels .*-Version 9\.9\.1') -eq 1) "the merge or the channel failure was not recorded (waited without a call, worktree removed, merge commit, failed channel, no new PR): exit $($r.Exit) $($r.Text)"
    $r = & $pkg @('show', '-Id', 'sa')
    & $expect ($r.Exit -eq 0 -and $r.Text -match 'stage=merged' -and $r.Text -notmatch 'INCONSISTENT' -and $r.Text -match 'channels\s+reported') "show failed on a merged package whose worktree is gone: $($r.Text)"
    & $setEnv @{ CHANNELMODE = 'stalepin' }
    $r = & $ship 'sa'
    & $expect ($r.Exit -eq 0 -and $r.Text -match 'plugin pin is pending' -and $r.Text -match 'no pin PR is opened' -and (& $calls 'pr create') -eq 1 -and (& $calls '^mergepr .*-Auto') -eq 3) "a stale plugin pin was not reported, or a PR was opened: $($r.Text)"
    & $setEnv @{ CHANNELMODE = 'ok' }
    $r = & $ship 'sa'
    $state = & $stateFile 'sa'
    & $expect ($r.Exit -eq 0 -and $state.ship.steps.channels.status -eq 'done' -and (& $calls '^mergepr .*-Auto') -eq 3 -and (& $calls 'pr create') -eq 1 -and @(((& $pkg @('show', '-Id', 'sa', '-Json')).Text | ConvertFrom-Json).nextTransitions).Count -eq 0) "a rerun after the merge repeated a merge call, or the channels were not reported ok: $($r.Text)"
    Remove-Item -LiteralPath (Join-Path $ghDir 'releases.json') -Force
    $r = & $ship 'sa'
    & $expect ($r.Exit -eq 0 -and $r.Text -match 'CHANNELS: not reported' -and (& $stateFile 'sa').ship.steps.channels.status -eq 'pending') "a missing release was not reported as not yet reported: $($r.Text)"

    # ---- base moved, no-op rebase: the base is a commit of the branch itself ----
    & $setEnv @{}
    & $resetLog
    $cWt = & $wtOf 'sc'
    $cHead = & $headOf 'sc'
    & $git $cWt @('push', 'origin', "${cHead}:refs/heads/master")
    & $syncMain
    # GitHub merges at once, while -NoWait is on: the package still stops at auto-merge-enabled and does not poll for the release.
    & $setEnv @{ MERGEMODE = 'instant' }
    $r = & $ship 'sc' @('-NoWait')
    $state = & $stateFile 'sc'
    & $expect ($r.Exit -eq 0 -and $state.stage -eq 'auto-merge-enabled' -and $state.ship.steps.base.noOp -eq $true -and -not $state.acceptance.invalidated -and $r.Text -match 'rebase changed nothing' -and (& $headOf 'sc') -eq $cHead -and (& $calls '^verify$') -eq 1) "a no-op rebase did not keep the acceptance: exit $($r.Exit) stage $($state.stage) $($r.Text)"
    & $kitCase ($state.ship.steps.qualify.status -eq 'done' -and $state.ship.steps.qualify.required -eq $false -and $null -eq (Get-Field $state.ship.steps.qualify 'configured') -and (& $calls '^impact') -eq 1 -and (& $calls '^desktop') -eq 0) 'hook contract: qualify not required' "$($state.ship.steps.qualify | ConvertTo-Json -Compress -Depth 4)"
    & $expect ($state.stage -ne 'merged' -and $r.Text -match 'already reports it MERGED' -and (& $calls '^channels') -eq 0 -and (& $calls '^gh release') -eq 0 -and $null -eq (Get-Field $state.ship.steps 'channels')) "-NoWait recorded merged or went on to the channels although it asked for no waiting: stage $($state.stage) $($r.Text)"
    # GitHub has merged it by the time -NoWait runs again: MERGED is recorded (it is true) and the worktree cleaned up, but the release is not waited for.
    & $setEnv @{}
    $r = & $ship 'sc' @('-NoWait')
    $state = & $stateFile 'sc'
    & $expect ($r.Exit -eq 0 -and $state.stage -eq 'merged' -and $state.ship.steps.merge.status -eq 'done' -and -not (Test-Path $cWt) -and $r.Text -match 'release channels are not waited for' -and $r.Text -match 'package\.ps1 ship -Id sc' -and (& $calls '^channels') -eq 0 -and (& $calls '^gh release') -eq 0 -and $null -eq (Get-Field $state.ship.steps 'channels') -and (& $calls '^mergepr .*-Auto') -eq 1) "-NoWait on a pull request GitHub had merged went on to the channels, or did not record the merge: exit $($r.Exit) stage $($state.stage) $($r.Text)"
    # A plain ship reports the channels.
    $r = & $ship 'sc'
    $state = & $stateFile 'sc'
    & $expect ($r.Exit -eq 0 -and $state.stage -eq 'merged' -and $state.ship.steps.merge.cleanupExit -eq 0 -and $state.ship.steps.merge.calls -eq 1 -and (& $calls '^mergepr .*-Auto') -eq 1 -and (& $calls '^mergepr -Pr \d+ -Worktree \S+ -Root \S+$') -eq 1 -and -not (Test-Path $cWt) -and $state.ship.steps.channels.status -eq 'done') "a pull request merged by GitHub was not recorded, cleaned up and reported: exit $($r.Exit) $($r.Text)"

    # ---- base moved, real rebase: back to review, then review, accept and ship again ----
    & $resetLog
    $bHeadBefore = & $headOf 'sb'
    $r = & $ship 'sb' @('-NoWait')
    $state = & $stateFile 'sb'
    & $expect ($r.Exit -eq 3 -and $state.stage -eq 'needs-review' -and $state.acceptance.invalidated.reason -match 'rewrote the branch' -and (& $headOf 'sb') -ne $bHeadBefore -and $state.commit.sha -eq (& $headOf 'sb') -and $r.Text -match 'review -Id sb' -and (& $calls '.') -eq 0 -and $state.checks[-1].ok) "a real rebase did not invalidate the acceptance and return to review: exit $($r.Exit) $($r.Text)"
    & $resetR
    $env:AW_PKG_TEST_VMODE = 'pass'
    $r = & $pkg @('review', '-Id', 'sb', '-Level', 'light', '-ClaudePath', $rvClaude)
    & $expect ($r.Exit -eq 0 -and (& $stateFile 'sb').stage -eq 'review-passed') "the review after the ship rebase failed: $($r.Text)"
    $r = & $pkg @('accept', '-Id', 'sb', '-Reason', 'reviewed after the rebase')
    & $expect ($r.Exit -eq 0 -and (& $stateFile 'sb').stage -eq 'accepted') "accept after the ship rebase failed: $($r.Text)"
    $sbHead = & $headOf 'sb'
    $sbWt = & $wtOf 'sb'
    # The waiting merge helper merges and removes the worktree, then the process dies before the merge is recorded.
    & $setEnv @{ SHIP_CRASH = 'merge-before-record' }
    $r = & $ship 'sb'
    $state = & $stateFile 'sb'
    & $expect ($r.Exit -eq 99 -and $state.stage -eq 'shipping' -and -not (Test-Path $sbWt) -and $state.ship.steps.verify.head -eq $sbHead -and (& $originRef 'sb') -eq $sbHead -and (& $calls '^mergepr .*-Auto -NoPluginPin -WaitMerged') -eq 1 -and $state.ship.steps.merge.status -eq 'running') "the crash after the merge helper did not leave a merged pull request and no worktree: exit $($r.Exit) stage $($state.stage) $($r.Text)"
    # Resumed with -NoWait: GitHub's MERGED is recorded, but the release is not waited for.
    & $setEnv @{}
    $r = & $ship 'sb' @('-NoWait')
    $state = & $stateFile 'sb'
    & $expect ($r.Exit -eq 0 -and $state.stage -eq 'merged' -and $state.ship.steps.merge.status -eq 'done' -and $state.ship.steps.merge.recoveredAfterWorktreeLoss -eq $true -and $r.Text -match 'release channels are not waited for' -and $r.Text -match 'package\.ps1 ship -Id sb' -and (& $calls '^channels') -eq 0 -and (& $calls '^gh release') -eq 0 -and $null -eq (Get-Field $state.ship.steps 'channels') -and (& $calls '^mergepr .*-Auto') -eq 1) "-NoWait on the worktree-loss recovery went on to the channels or did not record the merge: exit $($r.Exit) stage $($state.stage) $($r.Text)"
    $r = & $ship 'sb'
    $state = & $stateFile 'sb'
    & $expect ($r.Exit -eq 0 -and $state.stage -eq 'merged' -and (& $calls '^mergepr .*-Auto') -eq 1 -and (& $calls '^channels') -eq 1 -and $state.ship.steps.channels.status -eq 'done') "a plain ship after the recovery did not report the channels once without another merge call: exit $($r.Exit) $($r.Text)"

    # ---- a failing rebase --abort ----
    & $resetLog
    $dWt = & $wtOf 'sd'
    $dHead = & $headOf 'sd'
    $indexLock = ([string](& git -C $dWt rev-parse --git-path index.lock)).Trim()
    if (-not [IO.Path]::IsPathRooted($indexLock)) { $indexLock = Join-Path $dWt $indexLock }
    [IO.File]::WriteAllText($indexLock, '')
    $r = & $ship 'sd'
    $state = & $stateFile 'sd'
    Remove-Item -LiteralPath $indexLock -Force -ErrorAction SilentlyContinue
    & $expect ($r.Exit -eq 1 -and $state.stage -eq 'ship-failed' -and $r.Text -match 'rebase --abort FAILED' -and $state.lastError -match 'abort exit [1-9]' -and (& $headOf 'sd') -eq $dHead -and -not $state.acceptance.invalidated -and (& $calls '.') -eq 0) "a rebase whose --abort fails was not reported truthfully: exit $($r.Exit) stage $($state.stage) $($r.Text)"
    & $setEnv @{}
}

