function Invoke-SelfTest
{
    $failures = [Collections.Generic.List[string]]::new()
    # AW_PKG_SELFTEST_CASES=1 prints one CASE line per check (number, result, the message up to its first ": "), so a run can be compared
    # with another by case. KITCASE lines are the checks that exist only here: the contracts of the qualify and channels hooks.
    $script:CaseNumber = 0
    $expect = { param([bool]$Condition, [string]$Message) $script:CaseNumber++; if ($env:AW_PKG_SELFTEST_CASES) { Write-Host ('CASE {0:D4} {1} {2}' -f $script:CaseNumber, $(if ($Condition) { 'PASS' } else { 'FAIL' }), (($Message -split '(?<=\S): |\r?\n')[0].Trim())) }; if (-not $Condition) { $failures.Add($Message) } }
    $kitCase = { param([bool]$Condition, [string]$Name, [string]$Detail = '') Write-Host ('KITCASE {0} {1}' -f $(if ($Condition) { 'PASS' } else { 'FAIL' }), $Name); if (-not $Condition) { $failures.Add("$Name : $Detail") } }
    $root = Join-Path ([IO.Path]::GetTempPath()) "package-selftest-$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $root | Out-Null
    # The self-test runs a copy of the kit from a folder outside any configured repository. The gate context compares the repository a tool judges
    # with the repository that holds the tool, and the temporary repositories built here are not the one a vendored copy lives in; a copy placed
    # outside every configured repository has nothing to compare, and no switch, parameter or variable exists that a caller could use to skip it.
    $selfKit = Join-Path $root 'kit'
    Copy-Item -LiteralPath $script:KitDir -Destination $selfKit -Recurse
    $selfScript = Join-Path $selfKit 'tools' 'package.ps1'
    $script:ContextScriptRoot = Join-Path $selfKit 'tools'
    $oldMain = $env:AW_PKG_TEST_MAIN
    # Exported to every child: the real-binary guards (Get-CodexCliVersion, Assert-SelfTestLauncher, the Codex reviewer) key on these.
    $env:AW_PKG_SELFTEST = '1'
    $env:AW_PKG_SELFTEST_ROOT = $root
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
        # The repositories of the review and ship cases are valid consumers of the kit: record-review and merge-pr read the gate
        # configuration from the main checkout (kit selftest shape; the gate matches the stub verify, the pin files the writePins cases).
        New-Item -ItemType Directory -Path (Join-Path $seed 'hooks') -Force | Out-Null
        $hookStubs = Get-SelfTestHookScripts
        foreach ($hookName in $hookStubs.Keys) { [IO.File]::WriteAllText((Join-Path $seed "hooks/$hookName.ps1"), $hookStubs[$hookName]) }
        Write-AwSelfTestConfig $seed (Get-AwSelfTestConfig -PinFiles @('vendor/pin/lock.json', 'vendor/pin/meta.json') -PinHook 'hooks/pin.ps1' -QualifyHook 'hooks/qualify.ps1' -ChannelsHook 'hooks/channels.ps1')
        & $git $seed @('add', '-A'); & $git $seed @('commit', '-qm', 'one'); & $git $seed @('remote', 'add', 'origin', $origin); & $git $seed @('push', '-q', 'origin', 'master')
        & git clone -q $origin $main 2>&1 | Out-Null
        # A commit that exists only on origin: new must fetch before it resolves origin/master.
        [IO.File]::WriteAllText((Join-Path $seed 'b.txt'), "b`r`n")
        & $git $seed @('add', '-A'); & $git $seed @('commit', '-qm', 'two'); & $git $seed @('push', '-q', 'origin', 'master')
        $originHead = (& git -C $seed rev-parse HEAD).Trim()
        $env:AW_PKG_TEST_MAIN = $main
        Initialize-PackageContext $main

        $stubDir = Join-Path $root 'stubs'
        New-Item -ItemType Directory -Path $stubDir | Out-Null
        $prelude = @'
# aw-selftest-stub
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
            tree = "`$child = Start-Process pwsh -ArgumentList '-NoProfile','-Command','Start-Sleep 300' -PassThru -NoNewWindow; Set-Content -LiteralPath `$env:AW_PKG_TEST_PIDFILE `$child.Id; Start-Sleep -Seconds 120"
            mutate = "Set-Content -LiteralPath (Join-Path `$env:AW_PKG_TEST_MAIN 'rogue.txt') 'x'; Set-Content -LiteralPath (Join-Path `$log 'out.md') 'wrote into main'; exit 0"
            env = "Set-Content -LiteralPath (Join-Path `$log 'out.md') 'ENV-BLOCKED: the sandbox could not run dotnet format'; exit 0"
        }
        foreach ($name in $stubs.Keys) { [IO.File]::WriteAllText((Join-Path $stubDir "$name.ps1"), $prelude + $stubs[$name] + "`r`n") }
        $statusLog = Join-Path $root 'status.log'
        $statusStub = Join-Path $stubDir 'status.ps1'
        [IO.File]::WriteAllText($statusStub, "Add-Content -LiteralPath '$statusLog' (`$args -join ' ')`r`n")

        $briefFile = Join-Path $root 'brief.md'
        Copy-Item -LiteralPath (Join-Path $script:KitDir 'fixtures/gates/agent-run/complete.md') -Destination $briefFile
        $manifestFile = Join-Path $root 'checks.json'
        [IO.File]::WriteAllText($manifestFile, (@{ extra = @(
                    @{ name = 'passes'; exe = 'pwsh'; args = @('-NoProfile', '-Command', 'exit 0') },
                    @{ name = 'fails'; exe = 'pwsh'; args = @('-NoProfile', '-Command', 'exit 3') },
                    @{ name = 'mutator'; mutates = $true; exe = 'pwsh'; args = @('-NoProfile', '-Command', "Set-Content -LiteralPath mutated.txt -Value 'x'") }) } | ConvertTo-Json -Depth 6))
        $dirtyManifest = Join-Path $root 'dirty-checks.json'
        [IO.File]::WriteAllText($dirtyManifest, (@{ extra = @(@{ name = 'dirty'; exe = 'pwsh'; args = @('-NoProfile', '-Command', "Set-Content -LiteralPath dirty.txt -Value 'x'") }) } | ConvertTo-Json -Depth 6))
        $env:AW_PKG_TEST_PIDFILE = Join-Path $root 'child.pid'
        $badManifest = Join-Path $root 'bad-checks.json'
        [IO.File]::WriteAllText($badManifest, (@{ build = @('nope/Nope.csproj') } | ConvertTo-Json))

        $pkg = {
            param([string[]]$PackageArgs)
            if ($PackageArgs -notcontains '-CodexVersion') { $PackageArgs = @($PackageArgs) + @('-CodexVersion', '9.9.9-test') }
            $output = & pwsh -NoProfile -File $selfScript @PackageArgs -Repo $main -StatusScript $statusStub -LockTimeoutSeconds 1 -GraceSeconds 0 2>&1
            [pscustomobject]@{ Exit = $LASTEXITCODE; Text = (($output | ForEach-Object { "$_" }) -join "`n") }
        }
        $stateFile = { param($slug) Get-Content -LiteralPath (Join-Path $main ".git/selftest/packages/$slug.json") -Raw | ConvertFrom-Json -AsHashtable @script:JsonDate }
        $statePath = { param($slug) Join-Path $main ".git/selftest/packages/$slug.json" }
        $packagesRoot = Join-Path $main '.git/selftest/packages'

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

        # The cases after the shared set-up: the full suite. -Quick runs Invoke-QuickSelfTest instead.
        function Invoke-SelfTestMain
        {
            # Dry run prints the plan and creates nothing.
            $r = & $pkg @('new', '-Id', 'dry', '-Brief', $briefFile, '-DryRun')
            & $expect ($r.Exit -eq 0 -and $r.Text -match 'dry run: nothing was created') "dry run failed: $($r.Text)"
            & $expect (-not (Test-Path (& $statePath 'dry')) -and -not (Test-Path (Join-Path $root 'main.worktrees')) -and -not (Test-Path (Join-Path $main '.git/selftest'))) 'dry run created state or a worktree'
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
            Remove-Item -LiteralPath $env:AW_PKG_TEST_PIDFILE -ErrorAction SilentlyContinue
            $r = & $pkg @('run', '-Id', 'pk', '-AgentRunScript', (Join-Path $stubDir 'tree.ps1'), '-MaxMinutes', '0.15')
            $state = & $stateFile 'pk'
            $childPid = if (Test-Path $env:AW_PKG_TEST_PIDFILE) { [int](Get-Content $env:AW_PKG_TEST_PIDFILE -Raw).Trim() } else { 0 }
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
            $holder = Enter-StateLock (Join-Path $main '.git/selftest/packages') 'pk'
            try
            {
                $before = Get-Sha256 (& $statePath 'pk')
                $r = & $pkg @('check', '-Id', 'pk')
                & $expect ($r.Exit -eq 1 -and $r.Text -match 'locked' -and (Get-Sha256 (& $statePath 'pk')) -eq $before) "locked state not refused: $($r.Text)"
            }
            finally { $holder.Dispose() }

            # A write interrupted between the temp file and the rename leaves the old state, readable.
            $packages = Join-Path $main '.git/selftest/packages'
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
            # Main sync lock: an exclusive child blocks run before state changes or executor launch; shared holders coexist.
            $lockHolder = Join-Path $root 'main-sync-lock-holder.ps1'
            [IO.File]::WriteAllText($lockHolder, @'
param([string]$LockPath, [string]$Mode, [string]$ReadyPath)
$share = if ($Mode -eq 'shared') { [IO.FileShare]::ReadWrite } else { [IO.FileShare]::None }
$stream = [IO.File]::Open($LockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::Read, $share)
[IO.File]::WriteAllText($ReadyPath, 'ready')
Start-Sleep -Seconds 60
'@)
            $commonDir = ([string](& git -C $main rev-parse --path-format=absolute --git-common-dir)).Trim()
            $mainSyncLock = Join-Path $commonDir 'selftest/main-sync.lock'
            New-Item -ItemType Directory -Path (Split-Path $mainSyncLock) -Force | Out-Null
            $executorMarker = Join-Path $root 'locked-run-executor-started.txt'
            $lockedRunStub = Join-Path $stubDir 'locked-run.ps1'
            [IO.File]::WriteAllText($lockedRunStub, "# aw-selftest-stub`r`nSet-Content -LiteralPath '$executorMarker' 'started'`r`n" + $prelude + $stubs.ok + "`r`n")
            $r = & $pkg @('new', '-Id', 'msl', '-Brief', $briefFile)
            & $expect ($r.Exit -eq 0) "main-lock refusal package could not be created: $($r.Text)"
            $beforeLockStage = [string](& $stateFile 'msl').stage
            $ready = Join-Path $root 'main-sync-exclusive.ready'
            $holder = Start-Process -FilePath (Get-Command pwsh -CommandType Application | Select-Object -First 1).Source -ArgumentList @('-NoProfile', '-File', "`"$lockHolder`"", "`"$mainSyncLock`"", 'exclusive', "`"$ready`"") -PassThru -NoNewWindow
            try
            {
                $waitUntil = [DateTime]::UtcNow.AddSeconds(10)
                while (-not (Test-Path -LiteralPath $ready) -and [DateTime]::UtcNow -lt $waitUntil) { Start-Sleep -Milliseconds 50 }
                $env:AW_PKG_SELFTEST_LOCK_WAIT_SECONDS = '1'
                $r = & $pkg @('run', '-Id', 'msl', '-AgentRunScript', $lockedRunStub)
                $afterLockStage = [string](& $stateFile 'msl').stage
                & $expect ($r.Exit -eq 1 -and $r.Text -match 'main-sync\.lock' -and $r.Text -match '-SyncMain' -and $afterLockStage -eq $beforeLockStage -and -not (Test-Path -LiteralPath $executorMarker)) "run did not refuse an exclusive main-sync lock before state or executor changes: $($r.Text); stage $beforeLockStage -> $afterLockStage"
            }
            finally { Stop-Process -Id $holder.Id -Force -ErrorAction SilentlyContinue; Remove-Item Env:AW_PKG_SELFTEST_LOCK_WAIT_SECONDS -ErrorAction SilentlyContinue }

            $r = & $pkg @('new', '-Id', 'msls', '-Brief', $briefFile)
            & $expect ($r.Exit -eq 0) "shared-lock coexistence package could not be created: $($r.Text)"
            $ready = Join-Path $root 'main-sync-shared.ready'
            $holder = Start-Process -FilePath (Get-Command pwsh -CommandType Application | Select-Object -First 1).Source -ArgumentList @('-NoProfile', '-File', "`"$lockHolder`"", "`"$mainSyncLock`"", 'shared', "`"$ready`"") -PassThru -NoNewWindow
            try
            {
                $waitUntil = [DateTime]::UtcNow.AddSeconds(10)
                while (-not (Test-Path -LiteralPath $ready) -and [DateTime]::UtcNow -lt $waitUntil) { Start-Sleep -Milliseconds 50 }
                $r = & $pkg @('run', '-Id', 'msls', '-AgentRunScript', (Join-Path $stubDir 'ok.ps1'))
                & $expect ($r.Exit -eq 0 -and (& $stateFile 'msls').stage -eq 'executor-completed') "a run could not coexist with another shared main-sync holder: $($r.Text)"
            }
            finally { Stop-Process -Id $holder.Id -Force -ErrorAction SilentlyContinue }

            $raceOutputs = @()
            $raceProcesses = foreach ($n in 1..2)
            {
                $raceOutputs += @(Join-Path $root "race$n.out")
                Start-Process -FilePath (Get-Command pwsh -CommandType Application | Select-Object -First 1).Source -PassThru -NoNewWindow -RedirectStandardOutput (Join-Path $root "race$n.out") `
                    -ArgumentList @('-NoProfile', '-File', "`"$selfScript`"", 'new', '-Id', 'race', '-Brief', "`"$briefFile`"", '-Repo', "`"$main`"", '-StatusScript', "`"$statusStub`"", '-LockTimeoutSeconds', '120')
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
                    -ArgumentList @('-NoProfile', '-File', "`"$selfScript`"", 'run', '-Id', 'rec2', '-Repo', "`"$main`"", '-StatusScript', "`"$statusStub`"", '-AgentRunScript', "`"$(Join-Path $stubDir 'ok.ps1')`"", '-CodexVersion', '9.9.9-test', '-LockTimeoutSeconds', '120', '-GraceSeconds', '0')
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
            $r = & $pkg @('new', '-Id', 'mut', '-Brief', (Join-Path $script:KitDir 'fixtures/gates/agent-run/complete.md'))
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
            $env:AW_PKG_TEST_RECORD = $recordDir
            $resetRecords = { Remove-Item -LiteralPath $recordDir -Recurse -Force -ErrorAction SilentlyContinue; New-Item -ItemType Directory -Path $recordDir -Force | Out-Null }
            $calls = { @(Get-ChildItem -LiteralPath $recordDir -Filter 'call-*.json' -ErrorAction SilentlyContinue | Sort-Object { [int]($_.BaseName -replace '\D') }) }
            $claudeStubPrelude = @'
# aw-selftest-stub
$a = @($args)
if ($a -contains '--version') { if ($env:AW_PKG_TEST_HANGVERSION) { Start-Sleep -Seconds 60 }; '9.9.9-stub (Claude Code)'; exit 0 }
$rec = $env:AW_PKG_TEST_RECORD
$n = @(Get-ChildItem -LiteralPath $rec -Filter 'call-*.json').Count + 1
$stdinText = [Console]::In.ReadToEnd()
$settingsPath = $a[[Array]::IndexOf($a, '--settings') + 1]
[ordered]@{ args = $a; cwd = (Resolve-Path -LiteralPath '.').Path; stdin = $stdinText; settings = (Get-Content -LiteralPath $settingsPath -Raw); mcp = (Get-Content -LiteralPath $a[[Array]::IndexOf($a, '--mcp-config') + 1] -Raw) } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $rec "call-$n.json")
function Emit([string]$Line) { [Console]::Out.WriteLine($Line); [Console]::Out.Flush() }
$tokens = if ($env:AW_PKG_TEST_TOKENS) { [int]$env:AW_PKG_TEST_TOKENS } else { 100 }
$resumeAt = [Array]::IndexOf($a, '--resume')
$sid = if ($resumeAt -ge 0) { $a[$resumeAt + 1] } else { 'stub-claude-session' }
if ($env:AW_PKG_TEST_SID) { $sid = $env:AW_PKG_TEST_SID }
if (-not $env:AW_PKG_TEST_NOINIT) { Emit ('{"type":"system","subtype":"init","session_id":"' + $sid + '","model":"stub-model","cwd":' + ((Resolve-Path -LiteralPath '.').Path | ConvertTo-Json) + '}') }
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
                'c-tree' = "`$child = Start-Process pwsh -ArgumentList '-NoProfile','-Command','Start-Sleep 300' -PassThru -NoNewWindow; Set-Content -LiteralPath `$env:AW_PKG_TEST_PIDFILE `$child.Id; Start-Sleep -Seconds 120"
                'c-write-in' = "New-Item -ItemType Directory -Path 'allowed' -Force | Out-Null; Set-Content -LiteralPath (Join-Path (Resolve-Path -LiteralPath '.').Path 'allowed/a.txt') 'x'; Emit-Result 'changed allowed/a.txt'; exit 0"
                'c-write-out' = "Set-Content -LiteralPath (Join-Path (Resolve-Path -LiteralPath '.').Path 'outside.txt') 'x'; Emit-Result 'changed outside.txt'; exit 0"
                'c-write-ignored' = "Set-Content -LiteralPath (Join-Path (Resolve-Path -LiteralPath '.').Path 'ignored-note.txt') 'x'; Emit-Result 'changed an ignored file'; exit 0"
                'c-rename' = "New-Item -ItemType Directory -Path 'allowed' -Force | Out-Null; Move-Item -LiteralPath 'b.txt' -Destination 'allowed/moved.txt'; Emit-Result 'moved b.txt into allowed/'; exit 0"
                'c-write-bin' = "New-Item -ItemType Directory -Path 'bin' -Force | Out-Null; Set-Content -LiteralPath (Join-Path (Resolve-Path -LiteralPath '.').Path 'bin/out.dll') 'x'; Emit-Result 'build output'; exit 0"
                'c-terminal-only' = @'
Emit '{"type":"result","subtype":"success","is_error":false,"result":"done: terminal usage only","session_id":"stub-claude-session","usage":{"input_tokens":1200,"cache_creation_input_tokens":0,"cache_read_input_tokens":800,"output_tokens":10},"permission_denials":[]}'; exit 0
'@
                'c-mutate' ="Set-Content -LiteralPath (Join-Path `$env:AW_PKG_TEST_MAIN 'rogue-claude.txt') 'x'; Emit-Result 'wrote into main'; exit 0"
            }
            foreach ($name in $claudeStubs.Keys) { [IO.File]::WriteAllText((Join-Path $stubDir "$name.ps1"), $claudeStubPrelude + $claudeStubs[$name] + "`r`n") }
            [IO.File]::WriteAllText((Join-Path $stubDir 'unmarked.ps1'), "exit 0`r`n")
            $stub = { param($name) Join-Path $stubDir "$name.ps1" }
            $briefHaiku = Join-Path $root 'brief-haiku.md'
            [IO.File]::WriteAllText($briefHaiku, (Get-Content -LiteralPath (Join-Path $script:KitDir 'fixtures/gates/agent-run/complete.md') -Raw) + "`r`nHaiku-scope: mechanical`r`n")
            $briefHelper = Join-Path $root 'brief-helper.md'
            [IO.File]::WriteAllText($briefHelper, (Get-Content -LiteralPath (Join-Path $script:KitDir 'fixtures/gates/agent-run/complete.md') -Raw) + "`r`nHaiku-scope: helper`r`n")
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
            $fixtureDir = Join-Path $script:KitDir 'fixtures/package-claude'
            foreach ($case in @(@{ file = 'smoke-1-events.jsonl'; input = 81942; output = 1842; session = '11111111' }, @{ file = 'smoke-2-events.jsonl'; input = 87489; output = 1897; session = '22222222' }))
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
            $env:AW_PKG_TEST_TOKENS = '100'
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
            $env:AW_PKG_TEST_TOKENS = '5000'
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
            $env:AW_PKG_TEST_TOKENS = '600'
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
            $env:AW_PKG_TEST_TOKENS = '100'
            Remove-Item -LiteralPath $env:AW_PKG_TEST_PIDFILE -ErrorAction SilentlyContinue
            $r = & $pkg @('run', '-Id', 'cl', '-Executor', 'claude-sonnet', '-ClaudePath', (& $stub 'c-tree'), '-MaxMinutes', '0.15')
            $state = & $stateFile 'cl'
            $childPid = if (Test-Path $env:AW_PKG_TEST_PIDFILE) { [int](Get-Content $env:AW_PKG_TEST_PIDFILE -Raw).Trim() } else { 0 }
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
            $env:AW_PKG_TEST_HANGVERSION = '1'
            $timer = [Diagnostics.Stopwatch]::StartNew()
            $r = & $pkg @('run', '-Id', 'cl', '-Executor', 'claude-sonnet', '-ClaudePath', (& $stub 'c-ok'), '-ProbeSeconds', '2')
            $state = & $stateFile 'cl'
            Remove-Item Env:AW_PKG_TEST_HANGVERSION -ErrorAction SilentlyContinue
            & $expect ($r.Exit -eq 0 -and $null -eq $state.runs[-1].claudeCliVersion -and $state.runs[-1].outcome -eq 'completed' -and $timer.Elapsed.TotalSeconds -lt 50) "a hanging version probe was not bounded: $([int]$timer.Elapsed.TotalSeconds)s, $($r.Text)"

            # The session identity comes from the init event; a resumed session must answer with the id it resumed.
            $env:AW_PKG_TEST_NOINIT = '1'
            $r = & $pkg @('run', '-Id', 'cl', '-Executor', 'claude-sonnet', '-ClaudePath', (& $stub 'c-ok'))
            $state = & $stateFile 'cl'
            Remove-Item Env:AW_PKG_TEST_NOINIT -ErrorAction SilentlyContinue
            & $expect ($r.Exit -eq 1 -and $state.runs[-1].outcome -eq 'impl-failed' -and $state.runs[-1].reason -match 'session identity' -and $null -eq $state.runs[-1].sessionId) "a run without an init session id was accepted: $($r.Text)"
            $r = & $pkg @('run', '-Id', 'cl', '-Executor', 'claude-sonnet', '-ClaudePath', (& $stub 'c-ok'))
            $env:AW_PKG_TEST_SID = 'some-other-session'
            $r = & $pkg @('run', '-Id', 'cl', '-Executor', 'claude-sonnet', '-ClaudePath', (& $stub 'c-ok'), '-Resume', '-PromptFile', $fixPrompt)
            $state = & $stateFile 'cl'
            Remove-Item Env:AW_PKG_TEST_SID -ErrorAction SilentlyContinue
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
if (process.env.AW_PKG_TEST_JS_TREE) {
  const c = require('child_process').spawn('pwsh', ['-NoProfile', '-Command', 'Start-Sleep 300'], { stdio: 'ignore' });
  fs.writeFileSync(process.env.AW_PKG_TEST_PIDFILE, String(c.pid));
  setTimeout(() => {}, 120000);
} else {
  out({ type: 'result', subtype: 'success', is_error: false, result: 'npm stub done, stdin chars ' + stdin.length, session_id: sid, usage: { input_tokens: 5, output_tokens: 1 } });
}
'@)
                $r = & $pkg @('run', '-Id', 'cl', '-Executor', 'claude-sonnet', '-ClaudePath', $fakeShim, '-ProbeSeconds', '60')
                $state = & $stateFile 'cl'
                $run = $state.runs[-1]
                $npmReport = if ($run.artifacts -and $run.artifacts.outMd -and (Test-Path -LiteralPath $run.artifacts.outMd.path -PathType Leaf)) { Get-Content -LiteralPath $run.artifacts.outMd.path -Raw } else { $null }
                & $expect ($r.Exit -eq 0) "npm shim run: exit code is '$($r.Exit)', expected 0: $($r.Text)"
                & $expect ($run.outcome -eq 'completed') "npm shim run: outcome is '$($run.outcome)', expected 'completed': $($r.Text)"
                & $expect ($run.sessionId -eq 'npm-stub-session') "npm shim run: session id is '$($run.sessionId)', expected 'npm-stub-session'"
                & $expect ($run.claudeCliVersion -eq '9.9.9-npm-stub') "npm shim run: claudeCliVersion is '$($run.claudeCliVersion)', expected '9.9.9-npm-stub' (probe problem: '$($run.claudeCliVersionProblem)')"
                & $expect ($run.claudeExecutable -match 'cli\.js') "npm shim run: claudeExecutable is '$($run.claudeExecutable)', expected a path naming cli.js"
                & $expect ($run.executor.pid -gt 0) "npm shim run: executor pid is '$($run.executor.pid)', expected > 0"
                & $expect ($npmReport -match 'stdin chars [1-9]') "npm shim run: the report (out.md) is '$npmReport', expected 'stdin chars' followed by a non-zero count"

                # node's own children are in the job: a grandchild of node does not survive the timeout.
                $env:AW_PKG_TEST_JS_TREE = '1'
                Remove-Item -LiteralPath $env:AW_PKG_TEST_PIDFILE -ErrorAction SilentlyContinue
                $r = & $pkg @('run', '-Id', 'cl', '-Executor', 'claude-sonnet', '-ClaudePath', $fakeShim, '-MaxMinutes', '0.15', '-ProbeSeconds', '60')
                $state = & $stateFile 'cl'
                $childPid = if (Test-Path $env:AW_PKG_TEST_PIDFILE) { [int](Get-Content $env:AW_PKG_TEST_PIDFILE -Raw).Trim() } else { 0 }
                Start-Sleep -Milliseconds 500
                & $expect ($childPid -gt 0 -and $state.runs[-1].termination -eq 'timeout' -and -not (Get-Process -Id $childPid -ErrorAction SilentlyContinue)) "a child of node survived the timeout (pid $childPid): $($r.Text)"
                if ($childPid -gt 0) { Stop-Process -Id $childPid -Force -ErrorAction SilentlyContinue }
                Remove-Item Env:AW_PKG_TEST_JS_TREE -ErrorAction SilentlyContinue
            }
            # An interrupted Claude run keeps its place in the cumulative budget: rebuilt from its archived stream, or unknown and then blocking.
            $r = & $pkg @('new', '-Id', 'rc', '-Brief', $briefFile)
            & $expect ($r.Exit -eq 0) "accounting package could not be created: $($r.Text)"
            $env:AW_PKG_TEST_TOKENS = '100'
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
            & $expect ($r.Exit -eq 0 -and $crashedRun.tokens.input -eq 87489 -and $crashedRun.tokens.output -eq 1897 -and $crashedRun.sessionId -like '22222222*' -and $crashedRun.elapsedSeconds -ge 250 -and $crashedRun.elapsedSeconds -le 1800 -and $crashedRun.accounting.elapsedIsUpperBound -eq $true -and $crashedRun.accounting.known -eq $true -and $crashedRun.accounting.reconstructed -eq $true -and $crashedRun.termination -eq 'abandoned') "an interrupted run's accounting was not reconstructed: $($r.Text)"
            & $expect ($state.runs[-1].budget.priorInputTokens -eq 87589 -and $state.runs[-1].resumedFrom.runId -eq 'crashed-claude' -and $state.runs[-1].outcome -eq 'completed') "the resume did not charge the interrupted run: $($state.runs[-1].budget | ConvertTo-Json -Compress) $($r.Text)"
            & $crashClaude 'crashed-claude-2' (Join-Path $root 'no-such-archive')
            $r = & $pkg @('run', '-Id', 'rc', '-Executor', 'claude-sonnet', '-ClaudePath', (& $stub 'c-ok'), '-MaxInputTokens', '100000', '-Resume', '-PromptFile', $fixPrompt)
            $state = & $stateFile 'rc'
            $unknownRun = @($state.runs | Where-Object { $_.runId -eq 'crashed-claude-2' })[0]
            & $expect ($r.Exit -ne 0 -and $r.Text -match 'accounting of run crashed-claude-2 is unknown' -and $r.Text -match 'events\.jsonl is missing' -and $unknownRun.accounting.known -eq $false) "a resume after an unrecoverable run was not refused: $($r.Text)"

            # Session identity holds for every termination: a timed-out run is a resume candidate only with a consistent init id.
            $r = & $pkg @('new', '-Id', 'ti', '-Brief', $briefFile)
            & $expect ($r.Exit -eq 0) "identity package could not be created: $($r.Text)"
            $env:AW_PKG_TEST_TOKENS = '100'
            $env:AW_PKG_TEST_NOINIT = '1'
            $r = & $pkg @('run', '-Id', 'ti', '-Executor', 'claude-sonnet', '-ClaudePath', (& $stub 'c-usagesleep'), '-MaxMinutes', '0.05')
            Remove-Item Env:AW_PKG_TEST_NOINIT -ErrorAction SilentlyContinue
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
            $env:AW_PKG_TEST_SID = 'mismatched-session'
            $r = & $pkg @('run', '-Id', 'ti', '-Executor', 'claude-sonnet', '-ClaudePath', (& $stub 'c-usagesleep'), '-MaxMinutes', '0.3', '-Resume', '-PromptFile', $fixPrompt)
            Remove-Item Env:AW_PKG_TEST_SID -ErrorAction SilentlyContinue
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
        if ($Quick) { Invoke-QuickSelfTest } else { Invoke-SelfTestMain }
    }
    catch { $failures.Add("self-test crashed: $($_.Exception.Message) at line $($_.InvocationInfo.ScriptLineNumber)") }
    finally
    {
        if ($null -eq $oldMain) { Remove-Item Env:AW_PKG_TEST_MAIN -ErrorAction SilentlyContinue } else { $env:AW_PKG_TEST_MAIN = $oldMain }
        Remove-Item Env:AW_PKG_SELFTEST, Env:AW_PKG_SELFTEST_ROOT, Env:AW_PKG_TEST_RRECORD, Env:AW_PKG_TEST_VMODE -ErrorAction SilentlyContinue
        Remove-Item Env:AW_PKG_TEST_PIDFILE, Env:AW_PKG_TEST_SKIP_KILL, Env:AW_PKG_TEST_RECORD, Env:AW_PKG_TEST_TOKENS -ErrorAction SilentlyContinue
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

