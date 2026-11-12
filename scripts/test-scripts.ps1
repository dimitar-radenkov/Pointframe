#Requires -Version 7.0
<#
Runs the offline -SelfTest of every coordinator and release script, and fails when a file under scripts/tests is
ignored by git (the *.md ignore rule once dropped the fixtures of status.ps1 and agent-run.ps1, so their self-tests
passed only in the worktree that created them).

  pwsh scripts/test-scripts.ps1                      # exit 0 when every self-test passes and every fixture is tracked
  pwsh scripts/test-scripts.ps1 -All                 # run the heavy self-tests in full (package.ps1 -SelfTest, about 25 minutes)
  pwsh scripts/test-scripts.ps1 -BaseRef origin/main # compare against another base (default origin/master)
  pwsh scripts/test-scripts.ps1 -BaseSha <push-before-sha> # compare the complete push range

The package.ps1 self-test runs when scripts/ changed; it uses -Quick unless -All was requested. It is skipped when
scripts/ did not change. Any change to $FullRunPaths runs the full suite. This fixed repository-relative glob list is
scripts/agent-workflow/**, agent-workflow.json, agent-workflow.lock.json, scripts/package.ps1, agent-run.ps1,
codex-ask.ps1, merge-pr.ps1, record-review.ps1, status.ps1, verify.ps1, kb.ps1, doctor.ps1, test-scripts.ps1,
check-workflow-scripts.ps1, docs/knowledge-base/hooks/**, docs/knowledge-base/workflow.md, kb-slots.txt,
.github/workflows/ci.yml, script-selftests-daily.yml, .gitattributes and .gitignore. Unknown bases fail closed to -All.
Timings are printed per script; -ResultFile writes a JSON record.

Used by scripts/verify.ps1 (gate "scripts") and by CI (package-checks), which runs it on a clean checkout; the daily
workflow script-selftests-daily.yml runs it with -All.
A new script with an offline -SelfTest belongs in $SelfTestScripts.
#>
[CmdletBinding()]
param(
    [switch]$All,
    [string]$BaseRef = 'origin/master',
    [AllowEmptyString()][string]$BaseSha,
    [string]$ResultFile,
    [switch]$SelfTest,
    [switch]$SelectionOnly,
    [string]$RepoRoot
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest

$SelfTestScripts = @(
    'merge-pr.ps1',
    'record-review.ps1',
    'codex-ask.ps1',
    'status.ps1',
    'agent-run.ps1',
    'package.ps1',
    'release-impact.ps1',
    'publish-release.ps1',
    'check-alias-lag.ps1',
    'check-latest-release.ps1',
    'agent-release.ps1',
    'write-release-receipt.ps1',
    'check-release-channels.ps1',
    'check-released-desktop.ps1',
    'usage-report.ps1',
    'update-download-badges.ps1',
    'measure-mcp-payloads.ps1',
    'test-agent-onboarding.ps1',
    'trial.ps1',
    'weekly-trial.ps1'
)

$HeavyScripts = @('package.ps1')
$FullRunPaths = @(
    'scripts/agent-workflow/**', 'agent-workflow.json', 'agent-workflow.lock.json',
    'scripts/package.ps1', 'scripts/agent-run.ps1', 'scripts/codex-ask.ps1', 'scripts/merge-pr.ps1',
    'scripts/record-review.ps1', 'scripts/status.ps1', 'scripts/verify.ps1', 'scripts/kb.ps1',
    'scripts/doctor.ps1', 'scripts/test-scripts.ps1', 'scripts/check-workflow-scripts.ps1',
    'docs/knowledge-base/hooks/**', 'docs/knowledge-base/workflow.md', 'docs/knowledge-base/kb-slots.txt',
    '.github/workflows/ci.yml', '.github/workflows/script-selftests-daily.yml', '.gitattributes', '.gitignore'
)

$repoRoot = if ($RepoRoot) { [IO.Path]::GetFullPath($RepoRoot) } else { Split-Path $PSScriptRoot -Parent }
$failures = [System.Collections.Generic.List[string]]::new()
$useBaseSha = $PSBoundParameters.ContainsKey('BaseSha')

# Returns a closed decision and the changed paths for either a PR merge base or a push's before SHA.
function Get-ScriptsChange
{
    $base = ''
    if ($useBaseSha)
    {
        if (-not $BaseSha -or $BaseSha -match '^0+$') { return @{ Mode='all'; Base=$BaseSha; Reason='empty or all-zero before SHA'; Changed=$true; Paths=@() } }
        $resolved = @(& git -C $repoRoot rev-parse --verify "$BaseSha^{commit}" 2>$null)
        if ($LASTEXITCODE -ne 0 -or -not $resolved) { return @{ Mode='all'; Base=$BaseSha; Reason='before SHA cannot be resolved'; Changed=$true; Paths=@() } }
        $base = "$($resolved[0])".Trim()
    }
    else
    {
        $mergeBaseOut = @(& git -C $repoRoot merge-base HEAD $BaseRef 2>$null)
        if ($LASTEXITCODE -ne 0 -or -not $mergeBaseOut) { return @{ Mode='all'; Base=$BaseRef; Reason='merge base cannot be computed'; Changed=$true; Paths=@() } }
        $base = "$($mergeBaseOut[0])".Trim()
    }

    $paths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($gitArgs in @(@('diff','--name-only','--no-renames',"$base..HEAD"), @('diff','--name-only','--no-renames',$base), @('diff','--cached','--name-only','--no-renames',$base), @('ls-files','--others','--exclude-standard')))
    {
        $found = @(& git -C $repoRoot @gitArgs 2>$null)
        if ($LASTEXITCODE -ne 0) { return @{ Mode='all'; Base=$base; Reason='git could not enumerate changed paths'; Changed=$true; Paths=@() } }
        foreach ($item in $found) { if ($item) { [void]$paths.Add(([string]$item).Replace('\','/')) } }
    }
    $shortOut = @(& git -C $repoRoot rev-parse --short $base 2>$null)
    $short = if ($LASTEXITCODE -eq 0 -and $shortOut) { "$($shortOut[0])".Trim() } else { $base }
    $changedPaths = @($paths | Sort-Object)
    $scriptChanged = @($changedPaths | Where-Object { $_ -match '^scripts/' }).Count -gt 0
    foreach ($pattern in $FullRunPaths)
    {
        $regex = '^' + [regex]::Escape($pattern).Replace('\*\*','.*').Replace('\*','[^/]*') + '$'
        $match = $changedPaths | Where-Object { $_ -match $regex } | Select-Object -First 1
        if ($match) { return @{ Mode='all'; Base=$short; Reason="full-run path: $match"; Changed=$scriptChanged; Paths=$changedPaths; Match=$match } }
    }
    if ($scriptChanged) { return @{ Mode='quick'; Base=$short; Reason='scripts/ changed'; Changed=$true; Paths=$changedPaths } }
    return @{ Mode='skip'; Base=$short; Reason='no change under scripts/'; Changed=$false; Paths=$changedPaths }
}

if ($SelfTest)
{
    $fixture = Join-Path ([IO.Path]::GetTempPath()) ("test-scripts-" + [guid]::NewGuid().ToString('N'))
    [void](New-Item -ItemType Directory -Path $fixture)
    try
    {
        $invokeGit = { param([string[]]$GitArgs) & git -C $fixture @GitArgs 2>$null; if ($LASTEXITCODE -ne 0) { throw "git $($GitArgs -join ' ') failed" } }
        & $invokeGit @('init','-q')
        & $invokeGit @('config','user.email','fixture@example.invalid')
        & $invokeGit @('config','user.name','Fixture')
        [void](New-Item -ItemType Directory -Force -Path (Join-Path $fixture 'scripts'))
        [void](New-Item -ItemType Directory -Force -Path (Join-Path $fixture 'Pointframe'))
        Set-Content -LiteralPath (Join-Path $fixture 'scripts/base.ps1') -Value 'base'
        Set-Content -LiteralPath (Join-Path $fixture 'Pointframe/base.txt') -Value 'base'
        & $invokeGit @('add','.')
        & $invokeGit @('commit','-qm','base')
        $base = "$(& git -C $fixture rev-parse HEAD)".Trim()
        $selection = {
            param([string]$Sha=$base)
            $out = @(& pwsh -NoProfile -NonInteractive -File $PSCommandPath -SelectionOnly -RepoRoot $fixture -BaseSha $Sha 2>&1)
            if ($LASTEXITCODE -ne 0) { throw "selection command failed: $out" }
            return (($out | Select-Object -Last 1) | ConvertFrom-Json)
        }
        $cases = [Collections.Generic.List[object]]::new()
        $assert = { param([string]$Label,[string]$Expected) $actual = & $selection; if ($actual.Mode -ne $Expected) { throw "$Label expected $Expected, got $($actual.Mode)" }; $cases.Add([pscustomobject]@{case=$Label; mode=$actual.Mode; reason=$actual.Reason}) }
        Set-Content -LiteralPath (Join-Path $fixture 'scripts/release-impact.ps1') -Value 'ordinary'
        & $invokeGit @('add','.')
        & $invokeGit @('commit','-qm','ordinary scripts change')
        & $assert 'ordinary scripts change' 'quick'
        & $invokeGit @('reset','--hard',$base)
        Set-Content -LiteralPath (Join-Path $fixture 'Pointframe/change.txt') -Value 'product'
        & $invokeGit @('add','.')
        & $invokeGit @('commit','-qm','product change')
        & $assert 'no scripts change' 'skip'
        & $invokeGit @('reset','--hard',$base)
        [void](New-Item -ItemType Directory -Force -Path (Join-Path $fixture 'docs/knowledge-base'))
        Set-Content -LiteralPath (Join-Path $fixture 'docs/knowledge-base/workflow.md') -Value 'workflow'
        & $invokeGit @('add','.')
        & $invokeGit @('commit','-qm','full path')
        & $assert 'full-run path' 'all'
        & $invokeGit @('reset','--hard',$base)
        Set-Content -LiteralPath (Join-Path $fixture 'scripts/earlier.ps1') -Value 'one'
        & $invokeGit @('add','.')
        & $invokeGit @('commit','-qm','earlier scripts')
        Set-Content -LiteralPath (Join-Path $fixture 'Pointframe/later.txt') -Value 'two'
        & $invokeGit @('add','.')
        & $invokeGit @('commit','-qm','later product')
        & $assert 'multi-commit push range' 'quick'
        & $invokeGit @('reset','--hard',$base)
        Set-Content -LiteralPath (Join-Path $fixture 'scripts/rename-old.ps1') -Value 'rename'
        & $invokeGit @('add','.')
        & $invokeGit @('commit','-qm','rename source')
        $renameBase = "$(& git -C $fixture rev-parse HEAD)".Trim()
        Move-Item -LiteralPath (Join-Path $fixture 'scripts/rename-old.ps1') -Destination (Join-Path $fixture 'scripts/rename-new.ps1')
        & $invokeGit @('add','-A')
        & $invokeGit @('commit','-qm','rename')
        $rename = & $selection $renameBase
        if ($rename.Mode -ne 'quick') { throw 'rename did not select quick' }
        $cases.Add([pscustomobject]@{case='rename';mode=$rename.Mode;reason=$rename.Reason})
        Remove-Item -LiteralPath (Join-Path $fixture 'scripts/rename-new.ps1')
        & $assert 'deletion' 'quick'
        $zero = & $selection ('0' * 40)
        if ($zero.Mode -ne 'all') { throw 'all-zero SHA did not select all' }
        $unknown = & $selection 'not-a-real-sha'
        if ($unknown.Mode -ne 'all') { throw 'unknown SHA did not select all' }
        $cases.Add([pscustomobject]@{case='all-zero SHA';mode=$zero.Mode;reason=$zero.Reason})
        $cases.Add([pscustomobject]@{case='unknown SHA';mode=$unknown.Mode;reason=$unknown.Reason})
        & $invokeGit @('reset','--hard',$base)
        Set-Content -LiteralPath (Join-Path $fixture 'scripts/dirty.ps1') -Value 'dirty'
        $dirty = & $selection
        if ($dirty.Mode -ne 'quick') { throw 'dirty file did not select quick' }
        $cases.Add([pscustomobject]@{case='dirty';mode=$dirty.Mode;reason=$dirty.Reason})
        & $invokeGit @('reset','--hard',$base)
        Set-Content -LiteralPath (Join-Path $fixture 'scripts/untracked.ps1') -Value 'untracked'
        $untracked = & $selection
        if ($untracked.Mode -ne 'quick') { throw 'untracked file did not select quick' }
        $cases.Add([pscustomobject]@{case='untracked';mode=$untracked.Mode;reason=$untracked.Reason})
        $empty = & $selection ''
        if ($empty.Mode -ne 'all') { throw 'empty SHA did not select all' }
        $cases.Add([pscustomobject]@{case='empty SHA';mode=$empty.Mode;reason=$empty.Reason})
        $cases | ForEach-Object { Write-Host ("PASS  selection {0}: {1} ({2})" -f $_.case,$_.mode,$_.reason) }
    }
    finally { Remove-Item -LiteralPath $fixture -Recurse -Force }
    exit 0
}
$decision = if ($All) { @{ Mode='all'; Base=''; Reason='-All specified'; Changed=$true; Paths=@() } } else { try { Get-ScriptsChange } catch { @{ Mode='all'; Base=''; Reason="selection error: $($_.Exception.Message)"; Changed=$true; Paths=@() } } }
if ($SelectionOnly) { [pscustomobject]$decision | ConvertTo-Json -Compress -Depth 5; exit 0 }
$allRun = $decision.Mode -eq 'all'
$skipHeavy = $decision.Mode -eq 'skip'
Write-Host "Selection: $($decision.Mode); base=$($decision.Base); reason=$($decision.Reason)"
if ($decision.Match) { Write-Host "Full-run path matched: $($decision.Match)" }
$ran = 0
$skipped = 0
$records = [Collections.Generic.List[object]]::new()
$totalWatch = [Diagnostics.Stopwatch]::StartNew()

# Fixtures that git ignores exist only in the worktree that created them; a clean checkout would not have them.
$ignored = @(& git -C $repoRoot ls-files --others --ignored --exclude-standard -- 'scripts/tests' 2>$null | Where-Object { $_ })
foreach ($path in $ignored)
{
    $failures.Add("ignored fixture (add a .gitignore negation): $path")
}

foreach ($name in $SelfTestScripts)
{
    $path = Join-Path $PSScriptRoot $name
    if (-not (Test-Path -LiteralPath $path))
    {
        $failures.Add("$name is listed but does not exist")
        $records.Add(@{ name=$name; args=@(); seconds=0; exitCode=1; skipped=$false })
        Write-Host "FAIL  $name is listed but does not exist (0.000s)"
        continue
    }

    if ($skipHeavy -and $HeavyScripts -contains $name)
    {
        Write-Host "SKIP  $name  no change under scripts/ since $($decision.Base) (run with -All to force) (0.000s)"
        $skipped++
        $records.Add(@{ name=$name; args=@(); seconds=0; exitCode=0; skipped=$true })
        continue
    }

    $selfTestArgs = @(if (-not $allRun -and $HeavyScripts -contains $name) { '-SelfTest'; '-Quick' } else { '-SelfTest' })
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $output = @(& pwsh -NoProfile -NonInteractive -File $path @selfTestArgs 2>&1 | ForEach-Object { "$_" })
    $watch.Stop()
    $exitCode = $LASTEXITCODE
    $records.Add(@{ name=$name; args=$selfTestArgs; seconds=[math]::Round($watch.Elapsed.TotalSeconds, 3); exitCode=$exitCode; skipped=$false })
    $last = $output | Where-Object { $_.Trim() } | Select-Object -Last 1
    if ($exitCode -ne 0)
    {
        $failures.Add(("{0} -SelfTest failed (exit {1}): {2} ({3:N3}s)" -f $name, $exitCode, $last, $watch.Elapsed.TotalSeconds))
        continue
    }

    Write-Host ("PASS  {0}  {1}  ({2:N3}s)" -f $name, $last, $watch.Elapsed.TotalSeconds)
    $ran++
}

$skippedText = if ($skipped -gt 0) { "$skipped skipped (no scripts/ change)" } else { '0 skipped' }
$totalWatch.Stop()
if ($ResultFile)
{
    $result = [ordered]@{ mode=$decision.Mode; base=$decision.Base; reason=$decision.Reason; scripts=@($records); totalSeconds=[math]::Round($totalWatch.Elapsed.TotalSeconds,3) }
    $resultPath = [IO.Path]::GetFullPath($ResultFile, $repoRoot)
    $parent = Split-Path -Parent $resultPath
    if ($parent) { [void](New-Item -ItemType Directory -Force -Path $parent) }
    $result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $resultPath -Encoding utf8
}

if ($failures.Count -gt 0)
{
    foreach ($failure in $failures)
    {
        Write-Host "FAIL  $failure"
    }
    Write-Host "Script self-tests: $($failures.Count) problem(s), $ran scripts passed, $skippedText."
    exit 1
}

Write-Host "Script self-tests: $ran scripts passed, $skippedText, every fixture is tracked."
exit 0
