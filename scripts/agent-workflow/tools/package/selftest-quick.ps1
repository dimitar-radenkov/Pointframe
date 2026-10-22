function Get-SelfTestHookScripts
{
    # The hooks of the self-test repositories, all marked stubs. They take their behavior from the AW_PKG_TEST_* variables and log to the
    # ship log, so a case can count the calls. The pin hook validates the two pin files the way a real adapter would.
    $pin = @'
# aw-selftest-stub
param([string]$Action, [string]$Root)
if ($Action -ne 'Validate') { exit 0 }
$files = [ordered]@{ lock = (Join-Path $Root 'vendor/pin/lock.json'); meta = (Join-Path $Root 'vendor/pin/meta.json') }
$docs = @{}
foreach ($name in $files.Keys)
{
    if (-not (Test-Path -LiteralPath $files[$name] -PathType Leaf)) { Write-Output "$($files[$name]) is missing"; exit 1 }
    try { $docs[$name] = Get-Content -LiteralPath $files[$name] -Raw | ConvertFrom-Json -AsHashtable -ErrorAction Stop }
    catch { Write-Output "$(Split-Path $files[$name] -Leaf) is not valid JSON: $($_.Exception.Message)"; exit 1 }
    if ($docs[$name] -isnot [hashtable]) { Write-Output "$(Split-Path $files[$name] -Leaf) is not a JSON object"; exit 1 }
}
$lock = $docs.lock
$meta = $docs.meta
if ($lock['schemaVersion'] -ne 1) { Write-Output 'lock.json has no schemaVersion 1'; exit 1 }
$lockVersion = $lock['version']
$metaVersion = $meta['version']
if ($lockVersion -isnot [string] -or -not $lockVersion -or $metaVersion -isnot [string] -or -not $metaVersion) { Write-Output 'lock.json or meta.json has no version'; exit 1 }
if ($lockVersion -ne $metaVersion) { Write-Output "the version in lock.json ($lockVersion) differs from the one in meta.json ($metaVersion)"; exit 1 }
$checksum = $lock['sha256']
if ($checksum -isnot [string] -or $checksum -notmatch '^[0-9a-f]{64}$') { Write-Output 'the sha256 in lock.json is not a 64-digit lowercase hex checksum'; exit 1 }
$rec = $env:AW_PKG_TEST_RRECORD
if ($rec -and (Test-Path -LiteralPath $rec)) { Set-Content -LiteralPath (Join-Path $rec 'discovery.txt') (Resolve-Path -LiteralPath '.').Path }
if ($env:AW_PKG_TEST_DISCOVERY -eq 'fail') { Write-Output 'ERROR the pin validation says no'; exit 1 }
Write-Output 'pin validation ok'
exit 0
'@
    $qualify = @'
# aw-selftest-stub
param([string]$Action, [string]$Root, [string]$MainRoot, [string]$Base, [string]$Head, [string]$WorkDirectory)
if ($Action -eq 'Impact')
{
    Add-Content -LiteralPath $env:AW_PKG_TEST_SHIPLOG -Value 'impact'
    switch ($env:AW_PKG_TEST_IMPACT)
    {
        'required' { Write-Host 'CHANGED src/app/Program.cs'; Write-Host 'desktop-qualification: required'; Write-Host '  src/app/Program.cs [rule: shipping project]'; exit 0 }
        'none' { exit 2 }
        default { Write-Host 'CHANGED feature.txt'; Write-Host 'desktop-qualification: not-required'; Write-Host '  no changed path matched a qualification rule'; exit 0 }
    }
}
if ($Action -eq 'Run')
{
    Add-Content -LiteralPath $env:AW_PKG_TEST_SHIPLOG -Value ('desktop run ' + $WorkDirectory)
    New-Item -ItemType Directory -Path (Join-Path $WorkDirectory 'candidate') -Force | Out-Null
    $names = @('App.Cli-0.0.0-candidate.zip', 'App.Server-0.0.0-candidate.pkg')
    $packages = @()
    foreach ($name in $names)
    {
        $file = Join-Path $WorkDirectory "candidate/$name"
        [IO.File]::WriteAllText($file, "candidate $name $([guid]::NewGuid())")
        $packages += [ordered]@{ name = $name; sha256 = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() }
    }
    $mode = $env:AW_PKG_TEST_DESKTOPMODE
    $steps = @([ordered]@{ name = 'candidate smoke'; status = 'passed'; durationSeconds = 1; detail = 'ok' })
    if ($mode -eq 'skips') { $steps += [ordered]@{ name = 'candidate round trip'; status = 'skipped'; durationSeconds = 0; detail = 'skipped (not in this release): the feature is absent' } }
    $status = switch ($mode) { 'fail' { 'failed' } 'skips' { 'passed_with_skips' } default { 'passed' } }
    [ordered]@{ version = 1; label = 'candidate'; packages = $packages; steps = $steps; status = $status } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $WorkDirectory 'result.json')
    if ($status -eq 'failed') { Write-Host 'FAILED: candidate check'; exit 1 }
    Write-Host 'PASSED: candidate check'
    exit 0
}
exit 2
'@
    $channels = @'
# aw-selftest-stub
param([string]$Root, [string]$Version, [string]$MergeCommit, [string]$OutDirectory, [switch]$Watch, [int]$TimeoutMinutes, [int]$PollSeconds)
Add-Content -LiteralPath $env:AW_PKG_TEST_SHIPLOG -Value ("channels -Version $Version -MergeCommit $MergeCommit$(if ($Watch) { ' -Watch' })")
Write-Host 'CHANNEL github-release: accepted - release is published'
switch ($env:AW_PKG_TEST_CHANNELMODE)
{
    'failed' { Write-Host 'CHANNEL store:example.app: failed - Store job failure: boom'; Write-Host 'CHANNEL plugin-pin: accepted - pinned'; Write-Host 'CHANNELS: failed'; exit 1 }
    'stalepin' { Write-Host 'CHANNEL plugin-pin: pending - lock.json is the previous release'; Write-Host 'NOTE the plugin pin is pending: lock.json is the previous release. The next merged PR moves it (merge-pr.ps1 -Auto); no pin PR is opened from here.'; Write-Host 'CHANNELS: pending'; exit 3 }
    default { Write-Host 'CHANNEL plugin-pin: accepted - pinned'; Write-Host 'CHANNELS: ok'; exit 0 }
}
'@
    [ordered]@{ pin = $pin + "`r`n"; qualify = $qualify + "`r`n"; channels = $channels + "`r`n" }
}


function Invoke-QuickSelfTest
{
    # -SelfTest -Quick. Runs inside Invoke-SelfTest (dynamic scope): $pkg, $stateFile, $statePath, $packagesRoot, $briefFile, $manifestFile, $badManifest,
    # $stubDir, $originHead, $statusLog, $main, $root and $expect are the caller's. The cases of Invoke-SelfTestMain that cost little and between them
    # reach new, run, check and show (with their refusals), then the thin form of the review, fix, disposition, accept, recover and ship cases of
    # Invoke-ReviewSelfTest. What it leaves out is the timeouts, the races, the crash recoveries, the Claude executor matrix, the pin cases, the rebases
    # and the long ship matrix: the full suite is the release gate, this is the loop while a change is made.
    Remove-Item Env:ANTHROPIC_API_KEY -ErrorAction SilentlyContinue
    # The Claude executor stubs belong to the full suite; the review preamble only writes a file that would use this prelude.
    $claudeStubPrelude = "# aw-selftest-stub`r`n"

    $res = & $pkg @('new', '-Id', 'dry', '-Brief', $briefFile, '-DryRun')
    & $expect ($res.Exit -eq 0 -and $res.Text -match 'dry run: nothing was created') "dry run failed: $($res.Text)"
    & $expect (-not (Test-Path (& $statePath 'dry')) -and -not (Test-Path (Join-Path $root 'main.worktrees')) -and -not (Test-Path (Join-Path $main '.git/selftest'))) 'dry run created state or a worktree'
    $res = & $pkg @('new', '-Id', 'badpath', '-Brief', $briefFile, '-Checks', $badManifest)
    & $expect ($res.Exit -eq 1 -and $res.Text -match 'manifest path is not in' -and -not (Test-Path (& $statePath 'badpath'))) "missing manifest path accepted: $($res.Text)"

    $res = & $pkg @('new', '-Id', 'pk', '-Brief', $briefFile, '-Checks', $manifestFile)
    & $expect ($res.Exit -eq 0) "new failed: $($res.Text)"
    $state = & $stateFile 'pk'
    & $expect ($state.stage -eq 'ready' -and $state.baseSha -eq $originHead -and (Test-Path (Join-Path $state.worktree 'b.txt')) -and $state.branch -eq 'pkg/pk') 'new did not fetch and branch from origin/master'
    & $expect ($state.brief.sha256 -eq (Get-Sha256 $briefFile) -and $state.manifest.sha256 -eq (Get-Sha256 $state.manifest.path) -and (Test-Path $statusLog) -and @($state.history).Count -ge 2) 'snapshot hashes, the status call or the history were not recorded'
    $res = & $pkg @('new', '-Id', 'pk', '-Brief', $briefFile)
    & $expect ($res.Exit -ne 0 -and $res.Text -match 'already exists') 'a second new for the same slug was accepted'

    $res = & $pkg @('run', '-Id', 'pk', '-AgentRunScript', (Join-Path $stubDir 'ok.ps1'), '-CodexVersion', '9.9.9-test')
    $state = & $stateFile 'pk'
    $run = $state.runs[-1]
    & $expect ($res.Exit -eq 0 -and $run.outcome -eq 'completed' -and $run.provider -eq 'codex' -and $run.model -eq 'gpt-6-luna' -and $run.sessionId -eq 'stub-session' -and $run.artifacts.outMd.sha256 -eq (Get-Sha256 $run.artifacts.outMd.path) -and $run.mainCheckoutBefore.treeHash -eq $run.mainCheckoutAfter.treeHash -and -not $state.guard.mainCheckoutMutated) "completed run not recorded: $($res.Text)"

    [IO.File]::WriteAllText((Join-Path $state.worktree 'lf.txt'), "one`ntwo`n")
    $res = & $pkg @('check', '-Id', 'pk')
    $state = & $stateFile 'pk'
    $check = $state.checks[-1]
    $named = @{}; foreach ($c in $check.commands) { $named[$c.name] = $c }
    & $expect ($res.Exit -eq 1 -and -not $check.ok -and $named['passes'].exitCode -eq 0 -and $named['fails'].exitCode -eq 3 -and $state.stage -eq 'checks-failed' -and (($check.commands | ForEach-Object { $_.name }) -join ',') -eq 'mutator,crlf normalization,passes,fails' -and [IO.File]::ReadAllText((Join-Path $state.worktree 'lf.txt')) -eq "one`r`ntwo`r`n") "check results not recorded, in order, or the changed text file was not normalized: $($res.Text)"
    $res = & $pkg @('show', '-Id', 'pk', '-Json')
    $view = $res.Text | ConvertFrom-Json
    & $expect ($res.Exit -eq 0 -and $view.checks[-1].fresh -eq $true -and $view.nextTransitions -contains 'run' -and @($view.snapshotProblems).Count -eq 0) "a check on an unchanged tree was not fresh: $($res.Text)"
    $holder = Enter-StateLock (Join-Path $main '.git/selftest/packages') 'pk'
    try
    {
        $res = & $pkg @('check', '-Id', 'pk')
        & $expect ($res.Exit -eq 1 -and $res.Text -match 'locked') "locked state not refused: $($res.Text)"
    }
    finally { $holder.Dispose() }
    $old = Get-Sha256 (& $statePath 'pk')
    $interrupted = $false
    try { Update-State $packagesRoot 'pk' 'interrupted' { param($s) $s.stage = 'torn' } { throw 'simulated crash before rename' } | Out-Null } catch { $interrupted = $true }
    & $expect ($interrupted -and (Get-Sha256 (& $statePath 'pk')) -eq $old -and (Read-State $packagesRoot 'pk').stage -ne 'torn') 'an interrupted write changed the state file'
    Update-State $packagesRoot 'pk' 'recovered' { param($s) $s.stage = 'checks-failed' } | Out-Null
    & $expect (@(Get-ChildItem $packagesRoot -Filter 'pk.json.*.tmp').Count -eq 0) 'the next write did not recover and clean the stale temp file'
    Set-ItemProperty -LiteralPath $state.brief.path -Name IsReadOnly -Value $false
    Add-Content -LiteralPath $state.brief.path 'tampered'
    $res = & $pkg @('run', '-Id', 'pk', '-AgentRunScript', (Join-Path $stubDir 'ok.ps1'))
    & $expect ($res.Exit -ne 0 -and $res.Text -match 'snapshot changed' -and (Read-State $packagesRoot 'pk').stage -eq 'checks-failed') "tampered snapshot accepted: $($res.Text)"

    Invoke-ReviewSelfTest
}

