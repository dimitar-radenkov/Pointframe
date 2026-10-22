# ---- self-test--------------------------------------------------------------------------------------------------

function Invoke-ReviewSelfTest
{
    # Runs inside Invoke-SelfTest (dynamic scope): $root, $main, $origin, $seed, $git, $stubDir, $stub, $pkg, $stateFile, $statePath, $packagesRoot,
    # $expect and $failures are the caller's. Reviewers and executors are marked stubs; no real claude or codex process is ever started.
    Write-Host 'PHASE review'
    & git -C $main config user.name SelfTest
    & git -C $main config user.email selftest@example.invalid
    $briefRv = Join-Path $root 'brief-review.md'
    [IO.File]::WriteAllText($briefRv, (Get-Content -LiteralPath (Join-Path $script:KitDir 'fixtures/gates/agent-run/complete.md') -Raw) + "`r`n# Acceptance cases`r`n`r`n- The feature file exists.`r`n- The feature file says v2, and the`r`n  comment above it explains why.`r`n1. Nothing else changes.`r`n`r`n# Authorized repairs`r`n`r`nNone.`r`n")
    $rvManifest = Join-Path $root 'rv-checks.json'
    [IO.File]::WriteAllText($rvManifest, (@{ extra = @(@{ name = 'passes'; exe = 'pwsh'; args = @('-NoProfile', '-Command', 'exit 0') }) } | ConvertTo-Json -Depth 6))
    $recheckManifest = Join-Path $root 'recheck-checks.json'
    [IO.File]::WriteAllText($recheckManifest, (@{ extra = @(@{ name = 'no-c2'; exe = 'pwsh'; args = @('-NoProfile', '-Command', 'if (Test-Path c2.txt) { exit 3 }; exit 0') }) } | ConvertTo-Json -Depth 6))
    $rrecordDir = Join-Path $root 'review-records'
    $env:AW_PKG_TEST_RRECORD = $rrecordDir
    $resetR = { Remove-Item -LiteralPath $rrecordDir -Recurse -Force -ErrorAction SilentlyContinue; New-Item -ItemType Directory -Path $rrecordDir -Force | Out-Null }
    $rcalls = { @(Get-ChildItem -LiteralPath $rrecordDir -Filter 'rcall-*.json' -ErrorAction SilentlyContinue | Sort-Object { [int]($_.BaseName -replace '\D') }) }
    $readCall = { param($file) Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json }
    & $resetR

    # Reviewer stubs. The mode comes from AW_PKG_TEST_VMODE; the commit, tree and criteria are read from the prompt itself.
    $verdictCore = @'
$stdin = [Console]::In.ReadToEnd()
$all = @($args)
$rec = $env:AW_PKG_TEST_RRECORD
$n = @(Get-ChildItem -LiteralPath $rec -Filter 'rcall-*.json').Count + 1
$si = [Array]::IndexOf($all, '--settings')
$settingsText = if ($si -ge 0) { Get-Content -LiteralPath $all[$si + 1] -Raw } else { '' }
[ordered]@{ args = $all; cwd = (Resolve-Path -LiteralPath '.').Path; stdin = $stdin; settings = $settingsText } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $rec "rcall-$n.json")
$commit = if ($stdin -match 'Reviewed commit: ([0-9a-f]{40,64})') { $Matches[1] } else { '' }
$tree = if ($stdin -match 'Reviewed tree: ([0-9a-f]{40,64})') { $Matches[1] } else { '' }
$ids = @([regex]::Matches($stdin, '(?m)^(C\d+): ') | ForEach-Object { $_.Groups[1].Value })
$mode = $env:AW_PKG_TEST_VMODE
$zero = '0' * 40
$criteria = @($ids | ForEach-Object { [ordered]@{ id = $_; result = 'pass'; evidence = "read it ($_)" } })
$doc = [ordered]@{ schemaVersion = 1; reviewedCommit = $commit; reviewedTree = $tree; verdict = 'PASS'; criteria = $criteria; findings = @(); omissions = @('integration tests were not run') }
$blocking = [ordered]@{ id = 'F1'; severity = 'blocking'; file = 'feature.txt'; line = 1; summary = 'feature.txt is wrong'; scenario = 'open feature.txt: it says v1 and the brief wants v2' }
$minor = [ordered]@{ id = 'F2'; severity = 'non-blocking'; file = 'a.txt'; line = 1; summary = 'naming'; scenario = 'a.txt is named a' }
switch ($mode)
{
    'fail' { $doc.verdict = 'FAIL'; $doc.criteria[0].result = 'fail'; $doc.findings = @($blocking, $minor) }
    'failcrit' { $doc.verdict = 'FAIL'; $doc.criteria[0].result = 'fail' }
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
    'crashevent' { $json = '' }
    'fenced' { $json = '```json' + "`n" + $json + "`n" + '```' }
    'sleep' { Start-Sleep -Seconds 120 }
    'dirty' { Set-Content -LiteralPath (Join-Path (Resolve-Path -LiteralPath '.').Path 'dirty-by-reviewer.txt') 'x' }
    'snapwrite' { Set-Content -LiteralPath (Join-Path (Resolve-Path -LiteralPath '.').Path 'written-by-reviewer.txt') 'x' }
    'editinput' { if ($stdin -match 'read them first\): ([^\r\n]+)') { $f = Join-Path $Matches[1].Trim() 'diff.patch'; (Get-Item $f).IsReadOnly = $false; Add-Content -LiteralPath $f 'tampered by the reviewer' } }
}

'@
    $codexReviewer = "# aw-selftest-stub`r`n" + $verdictCore + @'
if ($mode -eq 'crash') { exit 3 }
$oi = [Array]::IndexOf($all, '-o')
[IO.File]::WriteAllText($all[$oi + 1], $json)
$sid = if ($all -contains 'resume') { $all[$all.Count - 2] } else { 'codex-rv-session' }
'{"type":"thread.started","thread_id":"' + $sid + '"}'
'{"type":"turn.completed","usage":{"input_tokens":7,"cached_input_tokens":2,"output_tokens":3}}'
if ($mode -eq 'crashevent') { exit 3 }

'@
    $claudeReviewer = "# aw-selftest-stub`r`n`$a = @(`$args)`r`nif (`$a -contains '--version') { `$v = if (`$env:AW_PKG_TEST_CLIVERSION) { `$env:AW_PKG_TEST_CLIVERSION } else { '9.9.9-stub' }; `"`$v (Claude Code)`"; exit 0 }`r`n" + $verdictCore + @'
function Emit([string]$Line) { [Console]::Out.WriteLine($Line); [Console]::Out.Flush() }
$resumeAt = [Array]::IndexOf($all, '--resume')
$sid = if ($resumeAt -ge 0) { $all[$resumeAt + 1] } else { 'stub-claude-review' }
Emit ('{"type":"system","subtype":"init","session_id":"' + $sid + '","model":"stub-model","cwd":' + ((Resolve-Path -LiteralPath '.').Path | ConvertTo-Json) + '}')
if ($mode -eq 'denied')
{
    Emit '{"type":"assistant","message":{"id":"m1","content":[{"type":"tool_use","id":"m1-t","name":"Bash","input":{}}],"usage":{"input_tokens":50,"output_tokens":9}}}'
    Emit '{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"m1-t","is_error":true,"content":"Permission to use Bash has been denied."}]}}'
}
if ($mode -eq 'crash') { exit 1 }
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
Copy-Item -LiteralPath $briefAt -Destination (Join-Path $env:AW_PKG_TEST_RRECORD 'fix-brief.md') -Force
Set-Content -LiteralPath (Join-Path $wtAt 'fixed.txt') "fixed`r`n"
Set-Content -LiteralPath (Join-Path $log 'out.md') 'fixed what the review found'
exit 0

'@)
    $fixerClaude = Join-Path $stubDir 'c-fix.ps1'
    [IO.File]::WriteAllText($fixerClaude, $claudeStubPrelude + "Set-Content -LiteralPath (Join-Path (Resolve-Path -LiteralPath '.').Path 'fixed.txt') 'fixed'; Emit-Result 'fixed what the review found'; exit 0`r`n")
    $env:AW_PKG_TEST_TOKENS = '100'
    $wtOf = { param($slug) [string](& $stateFile $slug).worktree }
    $headOf = { param($slug) (& git -C (& $wtOf $slug) rev-parse HEAD).Trim() }
    $writePins = {
        param($wt, $version = '1.0.0', $lockVersion = $null, $sha = ('a' * 64), $lockRaw = $null)
        New-Item -ItemType Directory -Path (Join-Path $wt 'vendor/pin') -Force | Out-Null
        if (-not $lockVersion) { $lockVersion = $version }
        $lock = if ($lockRaw) { $lockRaw } else { @{ schemaVersion = 1; version = $lockVersion; asset = 'x.mcpb'; url = 'https://example.invalid/x'; sha256 = $sha } | ConvertTo-Json }
        [IO.File]::WriteAllText((Join-Path $wt 'vendor/pin/lock.json'), $lock + "`r`n")
        [IO.File]::WriteAllText((Join-Path $wt 'vendor/pin/meta.json'), (@{ name = 'app'; version = $version } | ConvertTo-Json) + "`r`n")
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
    $schema = Get-Content -LiteralPath (Join-Path $script:KitDir 'review/package-verdict.schema.json') -Raw | ConvertFrom-Json
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
    $reviewerParams = (Get-Command $script:PackageScriptPath).Parameters.Keys
    & $expect (@(@('Head', 'Verdict', 'Evidence', 'BlockingFindings', 'ReviewerSession', 'ExecutorSession', 'ReviewerIdentity') | Where-Object { $_ -in $reviewerParams }).Count -eq 0) 'accept takes record values from the caller'

    # item 30: a whole package in a repository without a remote. AW_PKG_SELFTEST_ONLY=remoteless runs only this and the pure cases above.
    Invoke-RemotelessSelfTest
    if ($env:AW_PKG_SELFTEST_ONLY -eq 'remoteless') { Write-Host 'remoteless self-test finished.'; return }

    if ($Quick)
    {
        # -Quick: the in-process cases above, then the thin path of package B (it ends with bx accepted) and one ship of bx. The packages below are the full suite's.
        Invoke-PackageBSelfTest
        Invoke-ShipSelfTest
        Write-Host 'quick self-test: review, recovery and ship cases finished.'
        return
    }

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
    $env:AW_PKG_TEST_VMODE = 'fail'
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
    $env:AW_PKG_TEST_VMODE = 'pass'
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
    & $expect ($calls2.Count -eq 1 -and $c2args -notcontains '--resume' -and $null -eq $rev2.reviewer.resumedFrom -and (Get-Field $rev2.reviewer 'priorRound') -eq 1 -and $c2.stdin -match 'COMPLETE, CURRENT verdict' -and $c2.stdin -match 'the verdict was FAIL' -and $c2.stdin -match 'Earlier finding F1' -and $c2.stdin -match 'Earlier finding F2' -and $c2.stdin -match "Reviewed commit: $head3") "the Claude reviewer was resumed, or was not told the earlier verdict and findings with a request for a complete, current verdict: $($c2args -join ' ')"
    & $expect ($rev2.criteriaResults.Count -eq 3 -and @($rev2.criteriaResults | Where-Object { $_.result -ne 'pass' }).Count -eq 0) 'the resumed verdict does not cover every criterion'

    # accept: a separate recorded step; the record-review values come from the state.
    $r = & $pkg @('accept', '-Id', 'rv')
    & $expect ($r.Exit -ne 0 -and $r.Text -match 'needs -Reason') 'accept without a reason was allowed'
    $r = & $pkg @('accept', '-Id', 'rv', '-Reason', 'criteria verified against the diff')
    $state = & $stateFile 'rv'
    $recordFile = Join-Path $main ".git/selftest/reviews/$head3.json"
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
    $env:AW_PKG_TEST_VMODE = 'pass'
    $r = & $pkg @('review', '-Id', 'mix', '-Level', 'light', '-ClaudePath', $rvClaude)
    $mixState = & $stateFile 'mix'
    & $expect ($r.Exit -eq 0 -and $mixState.stage -eq 'review-passed') "review of mix did not pass: $($r.Text)"
    $mixWt = & $wtOf 'mix'
    New-Item -ItemType Directory -Path (Join-Path $mixWt 'vendor/pin') -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $mixWt 'vendor/pin/lock.json'), "{`"v`":2}`r`n"); [IO.File]::WriteAllText((Join-Path $mixWt 'extra.txt'), "x`r`n")
    & $git $mixWt @('add', '-A'); & $git $mixWt @('commit', '-qm', 'pin plus extra')
    $r = & $pkg @('accept', '-Id', 'mix', '-Reason', 'x')
    & $expect ($r.Exit -ne 0 -and (& $stateFile 'mix').stage -eq 'invalidated') "a pin commit with another file kept the PASS: $($r.Text)"

    # ---- package vd: invalid verdicts, the repair budget across restarts, needs-coordinator, resetting it ----
    & $newPackage 'vd'
    & $write 'vd' 'feature.txt' "v2`r`n"
    $r = & $pkg @('check', '-Id', 'vd'); $r = & $pkg @('commit', '-Id', 'vd')
    $vdHead = & $headOf 'vd'
    $review = { param($mode, [string[]]$more = @()) $env:AW_PKG_TEST_VMODE = $mode; & $pkg (@('review', '-Id', 'vd', '-Level', 'light', '-ClaudePath', $rvClaude) + $more) }
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
    & $expect ($null -eq $final.reviewer.resumedFrom -and $null -ne (Get-Field $final.reviewer 'priorRound')) 'a later round of a Claude reviewer resumed a session instead of starting fresh with the earlier round in the prompt'

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
    $env:AW_PKG_TEST_VMODE = 'fail'
    $r = & $pkg @('review', '-Id', 'cx', '-Level', 'heavy', '-CodexTestScript', $rvCodex)
    $state = & $stateFile 'cx'; $rev = Get-LatestReview $state
    $cc = @(& $rcalls)
    $cx1 = if ($cc.Count) { & $readCall $cc[0] } else { $null }
    $cxArgs = @($cx1.args)
    & $expect ($r.Exit -eq 1 -and $rev.reviewer.kind -eq 'codex-sol' -and $rev.reviewer.model -eq 'gpt-6.1-sol' -and $rev.reviewer.sessionId -eq 'codex-rv-session' -and $rev.status -eq 'valid' -and $rev.verdict -eq 'FAIL' -and $state.stage -eq 'review-failed') "the Codex heavy review of a Claude executor failed: $($r.Text)"
    & $expect ($cxArgs -contains 'read-only' -and $cxArgs -notcontains 'workspace-write' -and $cxArgs -contains 'gpt-6.1-sol' -and $cxArgs -contains 'model_reasoning_effort=high' -and $cx1.stdin -match "Reviewed commit: $($rev.commit)") "the Codex reviewer was not run read-only on the reviewed commit: $($cxArgs -join ' ')"
    $r = & $pkg @('disposition', '-Id', 'cx', '-Finding', 'F1', '-Accept', '-Reason', 'right')
    & $resetRecords
    $env:AW_PKG_TEST_VMODE = 'pass'
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
    $cxRecordFile = Join-Path $main ".git/selftest/reviews/$cxHead.json"
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
    $env:AW_PKG_TEST_VMODE = 'pass'
    $r = & $pkg @('review', '-Id', 'pn', '-Level', 'light', '-ClaudePath', $rvClaude)
    $pnState = & $stateFile 'pn'; $pnRev = Get-LatestReview $pnState; $pnWt = [string]$pnState.worktree; $pnCommit = [string]$pnRev.commit
    & $expect ($r.Exit -eq 0 -and $pnState.stage -eq 'review-passed') "review of pn did not pass: $($r.Text)"
    $binding = Get-ReviewBinding $pnState $pnRev
    & $expect ($binding.Current -and -not $binding.PinOnly) 'the binding of an untouched review is not current'
    $pinFile = Join-Path $pnWt 'vendor/pin/lock.json'
    New-Item -ItemType Directory -Path (Split-Path $pinFile -Parent) -Force | Out-Null
    Remove-Item -LiteralPath (Join-Path $rrecordDir 'discovery.txt') -ErrorAction SilentlyContinue
    & $writePins $pnWt '1.0.0'; & $git $pnWt @('add', '-A'); & $git $pnWt @('commit', '-qm', 'pin only')
    $binding = Get-ReviewBinding $pnState $pnRev
    & $expect ($binding.Current -and $binding.PinOnly -and (Test-Path (Join-Path $rrecordDir 'discovery.txt')) -and ((Get-Content (Join-Path $rrecordDir 'discovery.txt') -Raw).Trim() -ieq $pnWt)) "a valid plugin-pin-only commit was not the exception, or the discovery check did not run on the package worktree: $($binding.Reason)"
    $pinCases = @(
        @{ name = 'malformed JSON'; pins = @{ lockRaw = '{ not json' }; why = 'is not valid JSON' }
        @{ name = 'version mismatch'; pins = @{ version = '1.0.0'; lockVersion = '2.0.0' }; why = 'differs from the one in meta.json' }
        @{ name = 'bad checksum'; pins = @{ sha = 'XYZ' }; why = 'sha256' }
        @{ name = 'failing discovery check'; pins = @{}; discovery = 'fail'; why = 'the pin hook failed validation' }
    )
    foreach ($case in $pinCases)
    {
        & $git $pnWt @('reset', '-q', '--hard', $pnCommit)
        $pins = $case.pins
        & $writePins $pnWt @pins; & $git $pnWt @('add', '-A'); & $git $pnWt @('commit', '-qm', "pin: $($case.name)")
        if ($case.ContainsKey('discovery')) { $env:AW_PKG_TEST_DISCOVERY = $case.discovery }
        $binding = Get-ReviewBinding $pnState $pnRev
        Remove-Item Env:AW_PKG_TEST_DISCOVERY -ErrorAction SilentlyContinue
        & $expect (-not $binding.Current -and $binding.Reason -match 'does not validate' -and $binding.Reason -match [regex]::Escape($case.why)) "a pin commit with $($case.name) kept the review current: $($binding.Reason)"
    }
    & $git $pnWt @('reset', '-q', '--hard', $pnCommit)
    New-Item -ItemType Directory -Path (Split-Path $pinFile -Parent) -Force | Out-Null
    & $git $pnWt @('mv', 'feature.txt', 'vendor/pin/lock.json'); & $git $pnWt @('commit', '-qm', 'rename over the pin file')
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
    [IO.File]::WriteAllText((Join-Path $stubDir 'c-fail.ps1'), $claudeStubPrelude + "Set-Content -LiteralPath (Join-Path (Resolve-Path -LiteralPath '.').Path 'late.txt') 'x'; Emit-Result 'partial work'; exit 3`r`n")
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
    $env:AW_PKG_TEST_VMODE = 'pass'
    $r = & $pkg @('review', '-Id', 'fe', '-Level', 'light', '-ClaudePath', $rvClaude)
    $feRev = Get-LatestReview (& $stateFile 'fe')
    & $expect ($r.Exit -eq 0 -and $feRev.reviewer.kind -eq 'claude-opus' -and @($feRev.executors).Count -eq 2 -and $feRev.executor.model -eq 'claude-sonnet-5-5') "the default reviewer ignored the failed executor: $($r.Text)"
    $r = & $pkg @('accept', '-Id', 'fe', '-Reason', 'independent of both executors')
    $feHead = & $headOf 'fe'
    $feRecordFile = Join-Path $main ".git/selftest/reviews/$feHead.json"
    $feRecord = if (Test-Path $feRecordFile) { Get-Content -LiteralPath $feRecordFile -Raw | ConvertFrom-Json } else { $null }
    & $expect ($r.Exit -eq 0 -and $feRecord.executor -eq 'claude:claude-sonnet-5-5' -and $feRecord.reviewer -eq 'claude:claude-opus-5-5') "accept of fe failed or named the wrong executor: $($r.Text)"

    # ---- package bm: the base is observed by the host; a reviewer cannot change its snapshot ----
    & $newPackage 'bm'
    & $write 'bm' 'feature.txt' "v2`r`n"
    $r = & $pkg @('check', '-Id', 'bm'); $r = & $pkg @('commit', '-Id', 'bm')
    $bmState = & $stateFile 'bm'; $bmBase = [string]$bmState.baseSha; $bmHead = & $headOf 'bm'
    Update-State $packagesRoot 'bm' 'tamper-base' { param($s) $s.baseSha = $bmHead } | Out-Null
    & $resetR
    $env:AW_PKG_TEST_VMODE = 'pass'
    $r = & $pkg @('review', '-Id', 'bm', '-Level', 'light', '-ClaudePath', $rvClaude)
    $bmRev = Get-LatestReview (& $stateFile 'bm')
    & $expect ($r.Exit -eq 1 -and $bmRev.status -eq 'invalid' -and ($bmRev.invalidReasons -join ' ') -match 'observed base \w+ differs from the recorded baseSha' -and @(& $rcalls).Count -eq 0) "an edited baseSha did not make the review invalid: $($r.Text)"
    Update-State $packagesRoot 'bm' 'restore-base' { param($s) $s.baseSha = $bmBase } | Out-Null
    $env:AW_PKG_TEST_VMODE = 'snapwrite'
    $r = & $pkg @('review', '-Id', 'bm', '-Level', 'light', '-ClaudePath', $rvClaude)
    $bmRev = Get-LatestReview (& $stateFile 'bm')
    & $expect ($r.Exit -eq 1 -and $bmRev.status -eq 'invalid' -and ($bmRev.invalidReasons -join ' ') -match 'review snapshot changed' -and $bmRev.snapshot.removed -and -not (Test-Path $bmRev.snapshot.path) -and (Test-WorktreeClean (& $wtOf 'bm'))) "a reviewer that wrote into its snapshot was not invalid: $($r.Text)"
    # An input edited while the reviewer runs invalidates the review.
    $env:AW_PKG_TEST_VMODE = 'editinput'
    $r = & $pkg @('review', '-Id', 'bm', '-Level', 'light', '-ClaudePath', $rvClaude)
    $bmRev = Get-LatestReview (& $stateFile 'bm')
    & $expect ($r.Exit -eq 1 -and $bmRev.status -eq 'invalid' -and ($bmRev.invalidReasons -join ' ') -match 'review input diff\.patch changed' -and $bmRev.inputHashes.'diff.patch') "an input edited mid-review did not invalidate the review: $($r.Text)"
    # accept re-checks the inputs the review was bound to.
    $env:AW_PKG_TEST_VMODE = 'pass'
    $r = & $pkg @('review', '-Id', 'bm', '-Level', 'light', '-ClaudePath', $rvClaude, '-ResetBudget', '-Reason', 'bm retry after the edited input')
    & $expect ($r.Exit -eq 0 -and (& $stateFile 'bm').stage -eq 'review-passed') "the review of bm did not pass: $($r.Text)"
    $bmRev = Get-LatestReview (& $stateFile 'bm')
    $bmInputs = [string]$bmRev.snapshot.inputDir
    & $expect ((@($bmRev.inputHashes.Keys) | Sort-Object) -join ',' -eq 'brief.md,check-summary.json,diff.patch,executor-report.md,log.txt,prompt.md,stat.txt') 'the review did not record a hash for each input and the prompt'
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
    # The check names the tracked files whose raw bytes a checkout would not write (item 7 below); what it cannot see is a file that is new, whose
    # committed form differs from the bytes it was checked as: here an eol=lf attribute turns CRLF into LF on commit. The review still refuses it.
    & $newPackage 'ac'
    & $write 'ac' '.gitattributes' "lfattr.txt text eol=lf`r`n"
    & $write 'ac' 'lfattr.txt' "checked as CRLF, committed as LF`r`n"
    & $write 'ac' 'feature.txt' "v2`r`n"
    $acWt = & $wtOf 'ac'
    $r = & $pkg @('check', '-Id', 'ac')
    & $expect ($r.Exit -eq 0) "check of ac failed: $($r.Text)"
    $r = & $pkg @('commit', '-Id', 'ac')
    $acState = & $stateFile 'ac'
    & $expect ($r.Exit -eq 0 -and $acState.stage -eq 'committed' -and (Test-WorktreeClean $acWt)) "commit of ac failed: $($r.Text)"
    & $resetR
    $env:AW_PKG_TEST_VMODE = 'pass'
    $r = & $pkg @('review', '-Id', 'ac', '-Level', 'light', '-ClaudePath', $rvClaude)
    $acRev = Get-LatestReview (& $stateFile 'ac')
    & $expect ($r.Exit -eq 1 -and $acRev.status -eq 'invalid' -and ($acRev.invalidReasons -join ' ') -match 'checks did not run on the committed content \(raw bytes differ: [^)]*lfattr\.txt' -and @(& $rcalls).Count -eq 0 -and $acRev.snapshot.removed) "a snapshot that differs from the checked content was reviewed: $($r.Text)"

    # ---- package nl: raw bytes, no line-ending tolerance; the ordinary CRLF path still matches ----
    & $newPackage 'nl'
    $nlWt = & $wtOf 'nl'
    [IO.File]::WriteAllText((Join-Path $nlWt '.gitattributes'), "raw.dat -text`r`nraw2.sh -text`r`n")
    [IO.File]::WriteAllBytes((Join-Path $nlWt 'raw.dat'), [byte[]](1, 0, 10, 2))
    # A NUL-free -text file whose only later difference is its line endings (a .sh file is never normalized): the raw-bytes rule has to catch it without the NUL byte of raw.dat.
    [IO.File]::WriteAllBytes((Join-Path $nlWt 'raw2.sh'), [byte[]](97, 10, 98, 10))
    [IO.File]::WriteAllBytes((Join-Path $nlWt 'lf.txt'), [byte[]](111, 110, 101, 10, 116, 119, 111, 10))
    $r = & $pkg @('check', '-Id', 'nl'); $r = & $pkg @('commit', '-Id', 'nl')
    & $expect ($r.Exit -eq 0 -and [IO.File]::ReadAllText((Join-Path $nlWt 'lf.txt')) -eq "one`r`ntwo`r`n") "check did not normalize an LF text file: $($r.Text)"
    $env:AW_PKG_TEST_VMODE = 'pass'
    $r = & $pkg @('review', '-Id', 'nl', '-Level', 'light', '-ClaudePath', $rvClaude)
    $nlRev = Get-LatestReview (& $stateFile 'nl')
    & $expect ($r.Exit -eq 0 -and $nlRev.status -eq 'valid' -and $nlRev.snapshot.startObservation.contentTree -eq $nlRev.contentTree) "the ordinary path (check normalization, commit, snapshot checkout) did not give identical raw bytes: $($r.Text) $($nlRev.invalidReasons -join '; ')"
    & $git $nlWt @('update-index', '--assume-unchanged', 'raw.dat')
    & $git $nlWt @('update-index', '--assume-unchanged', 'raw2.sh')
    [IO.File]::WriteAllBytes((Join-Path $nlWt 'raw.dat'), [byte[]](1, 0, 13, 10, 2))
    [IO.File]::WriteAllBytes((Join-Path $nlWt 'raw2.sh'), [byte[]](97, 13, 10, 98, 13, 10))
    [IO.File]::WriteAllText((Join-Path $nlWt 'other.txt'), "other`r`n")
    # Item 7: the hidden edits are named by check itself (the later review would have found them as "checks did not run on the committed content").
    $r = & $pkg @('check', '-Id', 'nl')
    $nlCheck = @((& $stateFile 'nl').checks)[-1]
    & $expect ($r.Exit -eq 1 -and -not $nlCheck.ok -and (& $stateFile 'nl').stage -eq 'checks-failed' -and ($nlCheck.rawBytesProblems -join ',') -eq 'raw.dat,raw2.sh' -and $r.Text -match 'RAW BYTES' -and $r.Text -match 'raw\.dat, raw2\.sh') "a newline-only hidden edit of a -text file (with or without a NUL byte) was not named by check: $($r.Text)"
    & $expect (([IO.File]::ReadAllBytes((Join-Path $nlWt 'raw.dat')) -join ',') -eq '1,0,13,10,2' -and ([IO.File]::ReadAllBytes((Join-Path $nlWt 'raw2.sh')) -join ',') -eq '97,13,10,98,13,10') 'check rewrote a file it reported (report only)'
    $r = & $pkg @('commit', '-Id', 'nl')
    & $expect ($r.Exit -ne 0 -and $r.Text -match "needs 'checks-passed'" -and (& $stateFile 'nl').stage -eq 'checks-failed') "commit was allowed after a check that named raw-byte problems: $($r.Text)"
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
    Invoke-ShipSelfTest
    Write-Host 'ship self-test: cases finished.'
    Invoke-PackageBSelfTest
    Write-Host 'package B self-test: cases finished.'
}

