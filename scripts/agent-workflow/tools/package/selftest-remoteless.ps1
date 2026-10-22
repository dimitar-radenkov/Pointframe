function Invoke-RemotelessSelfTest
{
    # item 30. Runs inside Invoke-ReviewSelfTest (dynamic scope): $root, $selfKit, $selfScript, $git, $stubDir, $statusStub, $briefRv, $rvManifest,
    # $rvClaude, $resetR, $expect and $failures are the caller's. A repository that has no remote at all ("remote": null, baseRef master) takes one
    # package through new, run, check, commit, review, accept and ship -LocalMerge. The executor and the reviewer are marked stubs; verify.ps1,
    # merge-pr.ps1 and record-review.ps1 are the real ones (the marked stubs of the ship cases only forward to them), so the evidence checks of
    # the local merge are the kit's own. gh is a marked stub that must never be called.
    Write-Host 'PHASE remoteless'
    $rl = Join-Path $root 'rl-main'
    New-Item -ItemType Directory -Path $rl | Out-Null
    & git init -q -b master $rl
    [IO.File]::WriteAllText((Join-Path $rl 'a.txt'), "a`r`n")
    $rlConfig = Get-AwSelfTestConfig -BaseRef 'master'
    $rlConfig.repository.remote = $null
    Write-AwSelfTestConfig $rl $rlConfig
    & $git $rl @('add', '-A'); & $git $rl @('commit', '-qm', 'one')
    & git -C $rl config user.name SelfTest
    & git -C $rl config user.email selftest@example.invalid
    Add-Content -LiteralPath (Join-Path $rl '.git/info/exclude') -Value 'artifacts/'
    $rlTip = { ([string](& git -C $rl rev-parse refs/heads/master)).Trim() }
    $rlStubs = Join-Path $root 'rl-stubs'
    New-Item -ItemType Directory -Path $rlStubs | Out-Null
    $mergeTool = Join-Path $selfKit 'tools/merge-pr.ps1'
    $forward = { param($tool) "# aw-selftest-stub`r`n& pwsh -NoProfile -File '$(Join-Path $selfKit "tools/$tool.ps1")' `$args`r`nexit `$LASTEXITCODE`r`n" }
    $rlGh = Join-Path $rlStubs 'gh.ps1'
    $rlMergePr = Join-Path $rlStubs 'merge-pr.ps1'
    $rlVerify = Join-Path $rlStubs 'verify.ps1'
    [IO.File]::WriteAllText($rlGh, "# aw-selftest-stub`r`nWrite-Host 'gh must never be called without a remote'; exit 99`r`n")
    [IO.File]::WriteAllText($rlMergePr, (& $forward 'merge-pr'))
    [IO.File]::WriteAllText($rlVerify, (& $forward 'verify'))
    $rlShipArgs = @('-GhScript', $rlGh, '-MergePrScript', $rlMergePr, '-VerifyScript', $rlVerify, '-ChannelMinutes', '0')
    $rlPkg = {
        param([string[]]$PackageArgs)
        if ($PackageArgs -notcontains '-CodexVersion') { $PackageArgs = @($PackageArgs) + @('-CodexVersion', '9.9.9-test') }
        $output = & pwsh -NoProfile -File $selfScript @PackageArgs -Repo $rl -StatusScript $statusStub -LockTimeoutSeconds 1 -GraceSeconds 0 2>&1
        [pscustomobject]@{ Exit = $LASTEXITCODE; Text = (($output | ForEach-Object { "$_" }) -join "`n") }
    }
    $rlState = { param($slug) Get-Content -LiteralPath (Join-Path $rl ".git/selftest/packages/$slug.json") -Raw | ConvertFrom-Json -AsHashtable @script:JsonDate }
    $rlMerge = {
        param([string]$Worktree, [string[]]$More = @())
        $output = & pwsh -NoProfile -File $mergeTool -LocalMerge -Worktree $Worktree -Root $rl @More 2>&1
        [pscustomobject]@{ Exit = $LASTEXITCODE; Text = (($output | ForEach-Object { "$_" }) -join "`n") }
    }
    $baseTip = & $rlTip

    $r = & $rlPkg @('new', '-Id', 'rlx', '-Brief', $briefRv, '-Checks', $rvManifest, '-DryRun')
    & $expect ($r.Exit -eq 0 -and $r.Text -match 'branch\s+pkg/rlx from master \(no remote: the local branch, no fetch\)' -and $r.Text -notmatch "after 'git fetch") "item 30: new plans a branch from the local base without a fetch: $($r.Text)"
    $r = & $rlPkg @('new', '-Id', 'rlx', '-Brief', $briefRv, '-Checks', $rvManifest)
    $state = & $rlState 'rlx'
    & $expect ($r.Exit -eq 0 -and $state.stage -eq 'ready' -and $state.baseRef -eq 'master' -and $state.baseSha -eq $baseTip -and $state.branch -eq 'pkg/rlx') "item 30: new did not branch pkg/rlx from the local master: $($r.Text)"
    $wt = [string]$state.worktree
    $r = & $rlPkg @('run', '-Id', 'rlx', '-AgentRunScript', (Join-Path $stubDir 'ok.ps1'))
    & $expect ($r.Exit -eq 0 -and (& $rlState 'rlx').stage -eq 'executor-completed') "item 30: run failed: $($r.Text)"
    [IO.File]::WriteAllText((Join-Path $wt 'feature-rlx.txt'), "v2 rlx`r`n")
    $r = & $rlPkg @('check', '-Id', 'rlx')
    & $expect ($r.Exit -eq 0 -and (& $rlState 'rlx').stage -eq 'checks-passed') "item 30: check failed: $($r.Text)"
    $r = & $rlPkg @('commit', '-Id', 'rlx', '-Message', 'Remoteless package')
    $state = & $rlState 'rlx'
    & $expect ($r.Exit -eq 0 -and $state.stage -eq 'committed' -and $r.Text -notmatch 'fetch') "item 30: commit failed or fetched a remote: $($r.Text)"
    & $resetR
    $env:AW_PKG_TEST_VMODE = 'pass'
    $r = & $rlPkg @('review', '-Id', 'rlx', '-Level', 'light', '-ClaudePath', $rvClaude)
    & $expect ($r.Exit -eq 0 -and (& $rlState 'rlx').stage -eq 'review-passed') "item 30: review failed: $($r.Text)"
    $r = & $rlPkg @('accept', '-Id', 'rlx', '-Reason', 'remoteless test')
    $state = & $rlState 'rlx'
    & $expect ($r.Exit -eq 0 -and $state.stage -eq 'accepted' -and (Test-Path -LiteralPath $state.acceptance.recordPath)) "item 30: accept failed (the review record is written without a remote): $($r.Text)"

    # ship without -LocalMerge: everything is prepared and checked, and it stops before a push that cannot happen.
    $r = & $rlPkg (@('ship', '-Id', 'rlx') + $rlShipArgs)
    $state = & $rlState 'rlx'
    $steps = $state.ship.steps
    $head = ([string](& git -C $wt rev-parse HEAD)).Trim()
    & $expect ($r.Exit -eq 3 -and $state.stage -eq 'no-remote' -and $r.Text -match 'no remote is configured' -and $r.Text -match 'ship -Id rlx -LocalMerge' -and $steps.base.status -eq 'done' -and $steps.verify.status -eq 'done' -and $steps.record.status -eq 'done' -and $steps.qualify.status -eq 'not-configured' -and $steps.push.status -eq 'no-remote' -and -not $steps.ContainsKey('pr') -and -not $steps.ContainsKey('merge')) "item 30: ship without -LocalMerge did not stop at the stage no-remote: $($r.Text)"
    & $expect ((& $rlTip) -eq $baseTip -and -not (Test-Path -LiteralPath (Join-Path $rl 'feature-rlx.txt'))) 'item 30: ship without -LocalMerge changed the base branch'
    $r = & $rlPkg @('show', '-Id', 'rlx', '-Json')
    $view = $r.Text | ConvertFrom-Json
    & $expect ($r.Exit -eq 0 -and $view.stage -eq 'no-remote' -and $view.nextTransitions -contains 'ship -LocalMerge') "item 30: show does not name the next command at the stage no-remote: $($r.Text)"
    $r = & $rlPkg @('run', '-Id', 'rlx', '-AgentRunScript', (Join-Path $stubDir 'ok.ps1'))
    & $expect ($r.Exit -ne 0 -and $r.Text -match 'run is not allowed in stage .no-remote.') "item 30: run was allowed at the stage no-remote: $($r.Text)"

    # The evidence checks of the local merge itself (merge-pr.ps1 -LocalMerge, the real one): a stale receipt, a missing review, a FAIL review.
    $recordPath = Join-Path $rl ".git/selftest/reviews/$head.json"
    $recordBytes = [IO.File]::ReadAllBytes($recordPath)
    $featurePath = Join-Path $wt 'feature-rlx.txt'
    $featureBytes = [IO.File]::ReadAllBytes($featurePath)
    try
    {
        # A receipt is a fact about content: a tree that changed after verify (here an uncommitted edit) is not covered by it.
        [IO.File]::WriteAllText($featurePath, "v3 edited after verify`r`n")
        $r = & $rlMerge $wt
        & $expect ($r.Exit -eq 1 -and $r.Text -match 'RECEIPT REFUSED' -and (& $rlTip) -eq $baseTip) "item 30: a stale receipt was not refused at the local merge: $($r.Text)"
    }
    finally { [IO.File]::WriteAllBytes($featurePath, $featureBytes) }
    try
    {
        Remove-Item -LiteralPath $recordPath -Force
        $r = & $rlMerge $wt
        & $expect ($r.Exit -eq 1 -and $r.Text -match 'REVIEW REFUSED' -and $r.Text -match 'Receipt:' -and (& $rlTip) -eq $baseTip) "item 30: a missing review record was not refused at the local merge: $($r.Text)"
    }
    finally { [IO.File]::WriteAllBytes($recordPath, $recordBytes) }
    try
    {
        [IO.File]::WriteAllText($recordPath, ([IO.File]::ReadAllText($recordPath) -replace '"verdict":\s*"PASS"', '"verdict": "FAIL"'))
        $r = & $rlMerge $wt
        & $expect ($r.Exit -eq 1 -and $r.Text -match 'REVIEW REFUSED' -and (& $rlTip) -eq $baseTip) "item 30: a FAIL review record was not refused at the local merge: $($r.Text)"
    }
    finally { [IO.File]::WriteAllBytes($recordPath, $recordBytes) }
    $r = & $rlMerge $wt @('-DryRun')
    & $expect ($r.Exit -eq 0 -and $r.Text -match 'DRY RUN: would fast-forward' -and (& $rlTip) -eq $baseTip -and (Test-Path -LiteralPath $wt)) "item 30: the untouched evidence did not pass a local dry run: $($r.Text)"

    # The merge: base branch fast-forwarded to the shipped head, worktree and branch gone, no channels, no second merge.
    $r = & $rlPkg (@('ship', '-Id', 'rlx', '-LocalMerge') + $rlShipArgs)
    $state = & $rlState 'rlx'
    $steps = $state.ship.steps
    & $expect ($r.Exit -eq 0 -and $state.stage -eq 'merged' -and $steps.merge.status -eq 'done' -and $steps.merge.local -eq $true -and $steps.merge.mergeCommit -eq $head -and $steps.channels.status -eq 'not-configured') "item 30: ship -LocalMerge did not merge: $($r.Text)"
    & $expect ((& $rlTip) -eq $head -and ([string](& git -C $rl rev-parse HEAD)).Trim() -eq $head -and (Test-Path -LiteralPath (Join-Path $rl 'feature-rlx.txt'))) 'item 30: the base branch and the main checkout are not at the shipped head'
    & $expect (-not (Test-Path -LiteralPath $wt) -and @(& git -C $rl branch --list 'pkg/rlx').Count -eq 0) 'item 30: the worktree or the work branch is still there after the local merge'
    $r = & $rlPkg @('show', '-Id', 'rlx', '-Json')
    $view = $r.Text | ConvertFrom-Json
    & $expect ($r.Exit -eq 0 -and $view.stage -eq 'merged' -and @($view.nextTransitions).Count -eq 0) "item 30: show lists a next step for a merged package: $($r.Text)"
    $r = & $rlPkg (@('ship', '-Id', 'rlx', '-LocalMerge') + $rlShipArgs)
    & $expect ($r.Exit -eq 0 -and $r.Text -match 'already merged' -and (& $rlTip) -eq $head) "item 30: ship after the merge did not say so: $($r.Text)"

    # The same package id cannot ship through a remote flow it does not have, and the other commands stay usable for a new package.
    $r = & $rlPkg @('new', '-Id', 'rly', '-Brief', $briefRv, '-Checks', $rvManifest)
    & $expect ($r.Exit -eq 0 -and (& $rlState 'rly').baseSha -eq $head) "item 30: a second package did not branch from the merged base: $($r.Text)"
}

