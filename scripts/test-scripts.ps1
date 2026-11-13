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
.github/workflows/ci.yml, script-selftests-daily.yml, .gitattributes and .gitignore. The list applies in CI only
(-BaseSha, or GITHUB_ACTIONS=true); a local run stays -Quick, since the full suite does not fit the 30-minute verify
gate. Unknown bases fail closed to -All.
Unless the mode is all (-All, a CI full-run path, an unknown base), each other script runs only when a changed path matches
its dependency globs in $SelfTestDependencies (the script, the libraries it dot-sources, its scripts/tests folders, the
repository files its self-test reads); '*' always runs. A changed scripts/ path that no entry, no full-run path and no
$NotReadBySelfTests rule explains runs every script. A listed script without an entry fails the run. -SelectionOnly prints
the decision as JSON, with a scripts array of { name, run, reason }.
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
    [string]$RepoRoot,
    [string]$ScriptsDir,
    [string]$Scripts
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
    'weekly-trial.ps1',
    'run-integration-tests.ps1'
)

$HeavyScripts = @('package.ps1')

# What each self-test reads, as repository-relative globs (same semantics as $FullRunPaths). A script runs when a changed
# path matches one of its globs. '*' means the script always runs. Each entry lists the script, the libraries it
# dot-sources (transitively: agent-release.ps1 loads release-model.ps1), the scripts/tests folders it reads, and the
# repository files its -SelfTest reads outside scripts/. A script without an entry fails the run.
$kitWrapper = { param([string]$Name) @("scripts/$Name", 'scripts/agent-workflow/**', 'agent-workflow*.json') }  # wrapper -> vendored kit
$SelfTestDependencies = [ordered]@{
    'merge-pr.ps1' = & $kitWrapper 'merge-pr.ps1'
    'record-review.ps1' = & $kitWrapper 'record-review.ps1'
    'codex-ask.ps1' = & $kitWrapper 'codex-ask.ps1'
    'status.ps1' = & $kitWrapper 'status.ps1'
    'agent-run.ps1' = & $kitWrapper 'agent-run.ps1'
    # Runs by its own rule ($HeavyScripts), never by this map; the globs only stop its files counting as unmapped.
    'package.ps1' = & $kitWrapper 'package.ps1'
    # '*': Test-TrackedTree classifies `git ls-files` of the whole repository, so any added or renamed tracked file can fail it.
    # The globs after the marker only keep its own files from counting as unmapped.
    'release-impact.ps1' = @('*', 'scripts/release-impact.ps1', 'scripts/release-model.ps1', 'scripts/tests/release-impact/**')
    'publish-release.ps1' = @('scripts/publish-release.ps1', 'scripts/release-model.ps1', 'scripts/tests/publish-release/**')
    'check-alias-lag.ps1' = @('scripts/check-alias-lag.ps1', 'scripts/release-model.ps1', 'scripts/tests/alias-lag/**')
    'check-latest-release.ps1' = @('scripts/check-latest-release.ps1', 'scripts/release-model.ps1', 'scripts/tests/latest-release/**')
    # Test-PinHook runs docs/knowledge-base/hooks/pin.ps1 (which loads the copied agent-release.ps1); Test-InstallOneLiner reads the MCP guide.
    'agent-release.ps1' = @('scripts/agent-release.ps1', 'scripts/release-model.ps1', 'scripts/tests/agent-release/**', 'docs/knowledge-base/hooks/pin.ps1', 'Pointframe.Mcp/Guides/verify-desktop-work.md')
    # The cd.yml path is passed to Get-ReleaseFileList, which ignores it today; kept so a future read stays covered.
    'write-release-receipt.ps1' = @('scripts/write-release-receipt.ps1', 'scripts/release-model.ps1', '.github/workflows/cd.yml')
    # Test-ChannelFixtures reads winget-release.yml and packaging/build-mcp-package.ps1; cd.yml is passed but ignored.
    'check-release-channels.ps1' = @('scripts/check-release-channels.ps1', 'scripts/release-model.ps1', 'scripts/tests/release-channels/**', '.github/workflows/winget-release.yml', '.github/workflows/cd.yml', 'packaging/build-mcp-package.ps1')
    'check-released-desktop.ps1' = @('scripts/check-released-desktop.ps1', 'scripts/agent-release.ps1', 'scripts/release-model.ps1', 'scripts/tests/agent-release/**')
    'usage-report.ps1' = @('scripts/usage-report.ps1', 'scripts/tests/usage-report/**')
    'update-download-badges.ps1' = @('scripts/update-download-badges.ps1')
    'measure-mcp-payloads.ps1' = @('scripts/measure-mcp-payloads.ps1')
    # Invoke-PluginSelfTest copies plugin/pointframe and runs its launcher.
    'test-agent-onboarding.ps1' = @('scripts/test-agent-onboarding.ps1', 'scripts/agent-release.ps1', 'scripts/release-model.ps1', 'scripts/tests/agent-release/**', 'plugin/pointframe/**')
    # Test-Self reads scripts/trials/apps.json and copies the wpf-sample localPath (samples/verify/wpf) through New-TrialClone.
    'trial.ps1' = @('scripts/trial.ps1', 'scripts/agent-release.ps1', 'scripts/release-model.ps1', 'scripts/tests/trial/**', 'scripts/tests/agent-release/**', 'scripts/trials/apps.json', 'samples/verify/wpf/**')
    # Every resolver and the trial runner are faked; it loads agent-release.ps1 for Resolve-NewestAgentVersion only.
    'weekly-trial.ps1' = @('scripts/weekly-trial.ps1', 'scripts/agent-release.ps1', 'scripts/release-model.ps1', 'scripts/tests/weekly-trial/**', 'scripts/tests/agent-release/**')
    # Reads only its own TRX fixtures; dotnet test is never invoked by -SelfTest.
    'run-integration-tests.ps1' = @('scripts/run-integration-tests.ps1', 'scripts/tests/run-integration-tests/**')
}
# scripts/ files that no listed self-test loads, runs or reads (checked by searching every listed script and the kit for
# their names; agent-release.ps1 only writes a stub update-plugin-pin.ps1 into a temporary root). Changing one runs no self-test.
$NotReadBySelfTests = @(
    'scripts/check-agent-discovery.ps1', 'scripts/desktop-tests.ps1', 'scripts/sync-agent-guides.ps1',
    'scripts/update-plugin-pin.ps1', 'scripts/tests/agent-discovery/**'
)
# -ScriptsDir and -Scripts (comma-separated) exist for the -SelfTest only: they swap in stub scripts.
if ($Scripts) { $SelfTestScripts = @($Scripts -split ',' | Where-Object { $_ }) }
$scriptsDirPath = if ($ScriptsDir) { [IO.Path]::GetFullPath($ScriptsDir) } else { $PSScriptRoot }
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

# Returns the first glob in $Patterns that matches $Path, or $null. The '*' marker is not a glob.
function Test-PathGlob
{
    param([string]$Path, [string[]]$Patterns)
    foreach ($pattern in $Patterns)
    {
        if ($pattern -eq '*') { continue }
        if ($Path -match ('^' + [regex]::Escape($pattern).Replace('\*\*','.*').Replace('\*','[^/]*') + '$')) { return $pattern }
    }
    return $null
}

# The first changed path under scripts/ that no dependency map entry, full-run path or not-read rule explains (for example a new library).
function Get-UnmappedScriptsPath
{
    param([string[]]$ChangedPaths)
    $known = @($FullRunPaths) + @($NotReadBySelfTests) + @($SelfTestDependencies.Values | ForEach-Object { $_ })
    foreach ($changed in $ChangedPaths)
    {
        if ($changed -like 'scripts/*' -and -not (Test-PathGlob $changed $known)) { return $changed }
    }
    return $null
}

# One { name, run, reason } per script. Mode all runs everything; otherwise a script runs when a changed path matches its
# dependency globs. A script with no entry, or '*', always runs; an unmapped scripts/ path runs every script. package.ps1
# keeps its own rule: it runs unless the mode is skip.
function Get-ScriptSelection
{
    param($Decision, [string[]]$Names)
    foreach ($name in $Names)
    {
        $run = $true
        if ($Decision.Mode -eq 'all') { $reason = "full run: $($Decision.Reason)" }
        elseif ($Decision.Unmapped) { $reason = "unmapped scripts path: $($Decision.Unmapped)" }
        elseif ($HeavyScripts -contains $name) { $run = $Decision.Mode -ne 'skip'; $reason = if ($run) { 'heavy script: runs when scripts/ changed' } else { 'heavy script: no change under scripts/' } }
        elseif (-not $SelfTestDependencies.Contains($name)) { $reason = 'no dependency map entry' }
        elseif ($SelfTestDependencies[$name] -contains '*') { $reason = "always runs ('*' in the dependency map)" }
        else
        {
            $run = $false
            $reason = 'no dependency changed'
            foreach ($changed in $Decision.Paths)
            {
                $hit = Test-PathGlob $changed $SelfTestDependencies[$name]
                if ($hit) { $run = $true; $reason = "dependency changed: $changed ($hit)"; break }
            }
        }
        [pscustomobject]@{ name=$name; run=$run; reason=$reason }
    }
}

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
    # -z gives raw, NUL-separated paths: without it git quotes names with non-ASCII or special characters, and a
    # quoted name would match no dependency glob and no unmapped-path rule.
    $savedEncoding = [Console]::OutputEncoding
    [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
    try
    {
        foreach ($gitArgs in @(@('diff','--name-only','--no-renames','-z',"$base..HEAD"), @('diff','--name-only','--no-renames','-z',$base), @('diff','--cached','--name-only','--no-renames','-z',$base), @('ls-files','--others','--exclude-standard','-z')))
        {
            $raw = @(& git -C $repoRoot -c core.quotepath=off @gitArgs 2>$null) -join "`n"
            if ($LASTEXITCODE -ne 0) { return @{ Mode='all'; Base=$base; Reason='git could not enumerate changed paths'; Changed=$true; Paths=@() } }
            foreach ($item in $raw.Split([char]0)) { if ($item) { [void]$paths.Add($item.Replace('\','/')) } }
        }
    }
    finally { [Console]::OutputEncoding = $savedEncoding }
    $shortOut = @(& git -C $repoRoot rev-parse --short $base 2>$null)
    $short = if ($LASTEXITCODE -eq 0 -and $shortOut) { "$($shortOut[0])".Trim() } else { $base }
    $changedPaths = @($paths | Sort-Object)
    $scriptChanged = @($changedPaths | Where-Object { $_ -match '^scripts/' }).Count -gt 0
    $unmapped = Get-UnmappedScriptsPath $changedPaths
    $unmappedNote = if ($unmapped) { "; unmapped scripts path: $unmapped (every self-test runs)" } else { '' }
    foreach ($pattern in $FullRunPaths)
    {
        $regex = '^' + [regex]::Escape($pattern).Replace('\*\*','.*').Replace('\*','[^/]*') + '$'
        $match = $changedPaths | Where-Object { $_ -match $regex } | Select-Object -First 1
        if (-not $match) { continue }
        # The full suite (about 25 minutes) does not fit the 30-minute local verify gate, so only CI (a push's
        # -BaseSha, or any run under GitHub Actions) honours the list; a local run stays -Quick and says so.
        if ($useBaseSha -or $env:GITHUB_ACTIONS -eq 'true') { return @{ Mode='all'; Base=$short; Reason="full-run path: $match"; Changed=$scriptChanged; Paths=$changedPaths; Match=$match } }
        return @{ Mode='quick'; Base=$short; Reason="full-run path $match (local run: -Quick; CI runs -All)$unmappedNote"; Changed=$true; Paths=$changedPaths; Unmapped=$unmapped }
    }
    if ($scriptChanged) { return @{ Mode='quick'; Base=$short; Reason="scripts/ changed$unmappedNote"; Changed=$true; Paths=$changedPaths; Unmapped=$unmapped } }
    return @{ Mode='skip'; Base=$short; Reason='no change under scripts/'; Changed=$false; Paths=$changedPaths; Unmapped=$null }
}

if ($SelfTest)
{
    $fixture = Join-Path ([IO.Path]::GetTempPath()) ("test-scripts-" + [guid]::NewGuid().ToString('N'))
    $stubs = "$fixture-stubs"
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
        foreach ($tracked in @('scripts/release-model.ps1', 'scripts/agent-release.ps1', 'scripts/update-download-badges.ps1', 'scripts/trials/apps.json', 'scripts/tests/alias-lag/a.json', 'scripts/tests/latest-release/x.json', 'scripts/tests/usage-report/x.json'))
        {
            [void](New-Item -ItemType Directory -Force -Path (Split-Path (Join-Path $fixture $tracked) -Parent))
            Set-Content -LiteralPath (Join-Path $fixture $tracked) -Value 'base'
        }
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
        $baseRefSelection = {
            param([string]$Actions)
            $saved = $env:GITHUB_ACTIONS
            try
            {
                $env:GITHUB_ACTIONS = $Actions
                $out = @(& pwsh -NoProfile -NonInteractive -File $PSCommandPath -SelectionOnly -RepoRoot $fixture -BaseRef $base 2>&1)
                if ($LASTEXITCODE -ne 0) { throw "selection command failed: $out" }
            }
            finally { $env:GITHUB_ACTIONS = $saved }
            return (($out | Select-Object -Last 1) | ConvertFrom-Json)
        }
        $localRef = & $baseRefSelection ''
        if ($localRef.Mode -ne 'quick' -or $localRef.Reason -notmatch 'CI runs -All') { throw "local -BaseRef full-run path expected quick, got $($localRef.Mode) ($($localRef.Reason))" }
        $cases.Add([pscustomobject]@{case='full-run path, local -BaseRef';mode=$localRef.Mode;reason=$localRef.Reason})
        $actionsRef = & $baseRefSelection 'true'
        if ($actionsRef.Mode -ne 'all') { throw "-BaseRef full-run path under GitHub Actions expected all, got $($actionsRef.Mode)" }
        $cases.Add([pscustomobject]@{case='full-run path, -BaseRef in CI';mode=$actionsRef.Mode;reason=$actionsRef.Reason})
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

        # Per-script selection on the real dependency map. Only paths are compared, so the fixture needs no script bodies.
        $allNames = @('merge-pr.ps1','record-review.ps1','codex-ask.ps1','status.ps1','agent-run.ps1','package.ps1','release-impact.ps1','publish-release.ps1','check-alias-lag.ps1','check-latest-release.ps1','agent-release.ps1','write-release-receipt.ps1','check-release-channels.ps1','check-released-desktop.ps1','usage-report.ps1','update-download-badges.ps1','measure-mcp-payloads.ps1','test-agent-onboarding.ps1','trial.ps1','weekly-trial.ps1')
        $quickAlways = @('package.ps1','release-impact.ps1')
        $modelDependents = @('publish-release.ps1','check-alias-lag.ps1','check-latest-release.ps1','agent-release.ps1','write-release-receipt.ps1','check-release-channels.ps1','check-released-desktop.ps1','test-agent-onboarding.ps1','trial.ps1','weekly-trial.ps1')
        $agentReleaseDependents = @('agent-release.ps1','check-released-desktop.ps1','test-agent-onboarding.ps1','trial.ps1','weekly-trial.ps1')
        $kitWrappers = @('merge-pr.ps1','record-review.ps1','codex-ask.ps1','status.ps1','agent-run.ps1')
        $reset = { & $invokeGit @('reset','-q','--hard',$base); & $invokeGit @('clean','-fdq') }
        $commitAll = { param([string]$Message) & $invokeGit @('add','-A'); & $invokeGit @('commit','-qm',$Message) }
        $write = { param([string]$Relative, [string]$Text = 'changed') $target = Join-Path $fixture $Relative; [void](New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent)); Set-Content -LiteralPath $target -Value $Text }
        $check = {
            param([string]$Label, $Actual, [string]$ExpectedMode, [string[]]$ExpectedRun)
            if ($Actual.Mode -ne $ExpectedMode) { throw "$Label expected mode $ExpectedMode, got $($Actual.Mode) ($($Actual.Reason))" }
            if (@($Actual.scripts).Count -ne $allNames.Count) { throw "$Label listed $(@($Actual.scripts).Count) scripts, expected $($allNames.Count)" }
            $ran = @($Actual.scripts | Where-Object { $_.run } | ForEach-Object { $_.name } | Sort-Object)
            $want = @($ExpectedRun | Sort-Object)
            if (($ran -join ',') -ne ($want -join ',')) { throw "$Label ran [$($ran -join ', ')], expected [$($want -join ', ')]" }
            $cases.Add([pscustomobject]@{case=$Label; mode=$Actual.Mode; reason="$($ran.Count) of $($allNames.Count) run: $($ran -join ', ')"})
        }

        & $reset
        & $write 'Pointframe/change.txt'
        & $commitAll 'product only'
        & $check 'scripts: product-only change' (& $selection) 'skip' @('release-impact.ps1')
        $productOnly = & $selection
        $skippedCount = @($productOnly.scripts | Where-Object { -not $_.run }).Count
        if ($skippedCount -ne 19) { throw "scripts: product-only change expected 19 skipped scripts, got $skippedCount" }

        & $reset
        & $write 'scripts/update-download-badges.ps1'
        & $commitAll 'one script'
        & $check 'scripts: one script changed' (& $selection) 'quick' (@('update-download-badges.ps1') + $quickAlways)

        & $reset
        & $write 'scripts/tests/alias-lag/b.json'
        & $commitAll 'fixture folder'
        & $check 'scripts: fixture folder changed' (& $selection) 'quick' (@('check-alias-lag.ps1') + $quickAlways)

        & $reset
        & $write 'scripts/release-model.ps1'
        & $commitAll 'release model'
        & $check 'scripts: release-model.ps1 changed' (& $selection) 'quick' ($modelDependents + $quickAlways)

        & $reset
        & $write 'scripts/agent-release.ps1'
        & $commitAll 'agent release'
        & $check 'scripts: agent-release.ps1 changed (transitive)' (& $selection) 'quick' ($agentReleaseDependents + $quickAlways)

        & $reset
        & $write 'scripts/newlib.ps1'
        & $commitAll 'new library'
        $newLib = & $selection
        & $check 'scripts: unmapped new file' $newLib 'quick' $allNames
        foreach ($entry in $newLib.scripts) { if ($entry.reason -notmatch 'unmapped scripts path: scripts/newlib\.ps1') { throw "unmapped new file: $($entry.name) reason does not name the path: $($entry.reason)" } }
        if ($newLib.Reason -notmatch 'scripts/newlib\.ps1') { throw "unmapped new file: the selection reason does not name the path: $($newLib.Reason)" }

        # Names git quotes by default (non-ASCII; Windows forbids the other quoted characters): committed and untracked.
        & $reset
        & $write "scripts/tests/alias-lag/$([char]0x0434)ata q.json"
        & $commitAll 'quoted name in a fixture folder'
        & $check 'scripts: quoted name in a mapped folder' (& $selection) 'quick' (@('check-alias-lag.ps1') + $quickAlways)

        & $reset
        & $write "scripts/$([char]0x043D)ew lib.ps1"
        $quotedLib = & $selection
        & $check 'scripts: quoted unmapped untracked file' $quotedLib 'quick' $allNames
        if ($quotedLib.Reason -notmatch "scripts/$([char]0x043D)ew lib\.ps1") { throw "quoted unmapped file: the reason does not name the raw path: $($quotedLib.Reason)" }

        & $reset
        & $write 'scripts/sync-agent-guides.ps1'
        & $commitAll 'script no self-test reads'
        & $check 'scripts: script no self-test reads' (& $selection) 'quick' $quickAlways

        & $reset
        & $write 'scripts/release-impact.ps1'
        & $commitAll "own file of the '*' script"
        & $check "scripts: own file of the '*' script is not unmapped" (& $selection) 'quick' $quickAlways

        & $reset
        & $invokeGit @('mv','scripts/tests/alias-lag/a.json','scripts/tests/alias-lag/c.json')
        & $commitAll 'rename inside a fixture folder'
        & $check 'scripts: rename inside a mapped folder' (& $selection) 'quick' (@('check-alias-lag.ps1') + $quickAlways)

        & $reset
        & $invokeGit @('mv','scripts/tests/usage-report/x.json','Pointframe/x.json')
        & $commitAll 'rename out of a fixture folder'
        & $check 'scripts: rename out of a mapped folder' (& $selection) 'quick' (@('usage-report.ps1') + $quickAlways)

        & $reset
        Remove-Item -LiteralPath (Join-Path $fixture 'scripts/tests/latest-release/x.json')
        & $commitAll 'delete a fixture'
        & $check 'scripts: deleted mapped file' (& $selection) 'quick' (@('check-latest-release.ps1') + $quickAlways)

        & $reset
        & $write 'scripts/trials/apps.json'
        & $check 'scripts: dirty mapped file' (& $selection) 'quick' (@('trial.ps1') + $quickAlways)

        & $reset
        & $write 'scripts/tests/publish-release/new.json'
        & $check 'scripts: untracked mapped file' (& $selection) 'quick' (@('publish-release.ps1') + $quickAlways)

        & $reset
        & $write 'scripts/tests/weekly-trial/staged.json'
        & $invokeGit @('add','-A')
        & $check 'scripts: staged mapped file' (& $selection) 'quick' (@('weekly-trial.ps1') + $quickAlways)

        & $reset
        & $write 'scripts/agent-workflow/lib/x.ps1'
        & $commitAll 'kit file'
        & $check 'scripts: kit file, local run' (& $baseRefSelection '') 'quick' ($kitWrappers + $quickAlways)

        & $reset
        & $check 'scripts: unknown base runs everything' (& $selection 'not-a-real-sha') 'all' $allNames

        # -ResultFile: run the real dispatch against stub scripts (real names, so the real map applies) outside the fixture repository.
        & $reset
        [void](New-Item -ItemType Directory -Path $stubs)
        Set-Content -LiteralPath (Join-Path $stubs 'package.ps1') -Value 'Write-Output "stub package $args"; exit 0'
        Set-Content -LiteralPath (Join-Path $stubs 'update-download-badges.ps1') -Value 'Write-Output "stub ok"; exit 0'
        Set-Content -LiteralPath (Join-Path $stubs 'release-impact.ps1') -Value 'Write-Output "stub bad"; exit 3'
        Set-Content -LiteralPath (Join-Path $stubs 'check-alias-lag.ps1') -Value 'Write-Output "stub ok"; exit 0'
        Set-Content -LiteralPath (Join-Path $stubs 'unlisted.ps1') -Value 'Write-Output "stub unlisted"; exit 0'
        $runStubs = {
            param([string]$List, [string]$Out)
            $output = @(& pwsh -NoProfile -NonInteractive -File $PSCommandPath -RepoRoot $fixture -BaseSha $base -ScriptsDir $stubs -Scripts $List -ResultFile $Out 2>&1 | ForEach-Object { "$_" })
            return [pscustomobject]@{ Exit=$LASTEXITCODE; Output=($output -join "`n") }
        }
        & $write 'Pointframe/result.txt' 'product'
        & $commitAll 'product change for result file'
        $skipOut = Join-Path $stubs 'out/skip.json'
        $skipRun = & $runStubs 'package.ps1,update-download-badges.ps1,release-impact.ps1,usage-report.ps1,unlisted.ps1' $skipOut
        if ($skipRun.Exit -ne 1) { throw "result file run with a failing stub exited $($skipRun.Exit), expected 1" }
        $skipJson = Get-Content -LiteralPath $skipOut -Raw | ConvertFrom-Json
        if ($skipJson.mode -ne 'skip' -or @($skipJson.scripts).Count -ne 5) { throw "skip result file: mode $($skipJson.mode), $(@($skipJson.scripts).Count) entries" }
        $byName = @{}; foreach ($s in $skipJson.scripts) { $byName[$s.name] = $s }
        if (-not $byName['package.ps1'].skipped -or $byName['package.ps1'].exitCode -ne 0) { throw 'skipped package.ps1 is not recorded as skipped' }
        if (-not $byName['update-download-badges.ps1'].skipped -or $byName['update-download-badges.ps1'].exitCode -ne 0) { throw 'a script with no changed dependency is not recorded as skipped' }
        if ($byName['release-impact.ps1'].skipped -or $byName['release-impact.ps1'].exitCode -ne 3) { throw "the '*' script did not run on a product-only change or lost its exit code" }
        if ($byName['usage-report.ps1'].exitCode -ne 1 -or $byName['unlisted.ps1'].exitCode -ne 1 -or $byName['unlisted.ps1'].skipped) { throw 'a missing script or a script without a map entry is not recorded as a failure' }
        if ($skipRun.Output -notmatch 'SKIP  update-download-badges\.ps1  no dependency changed \(1 path\(s\) changed\)') { throw "the dependency skip line is missing: $($skipRun.Output)" }
        if ($skipRun.Output -notmatch 'FAIL  unlisted\.ps1 has no dependency map entry') { throw "the missing map entry is not reported: $($skipRun.Output)" }
        if ($skipRun.Output -match 'stub unlisted') { throw 'a script without a map entry must not run' }
        if ($null -eq $skipJson.totalSeconds -or -not $skipJson.reason) { throw 'skip result file lacks totalSeconds or reason' }
        $cases.Add([pscustomobject]@{case='result file, failing stubs';mode=$skipJson.mode;reason='exit 1 kept, 5 entries, package and update-download-badges skipped, "*" script ran, unlisted script reported'})
        Set-Content -LiteralPath (Join-Path $stubs 'release-impact.ps1') -Value 'Write-Output "stub ok"; exit 0'
        & $write 'scripts/update-download-badges.ps1'
        & $commitAll 'scripts change for result file'
        $quickOut = Join-Path $stubs 'out/quick.json'
        $quickRun = & $runStubs 'package.ps1,update-download-badges.ps1,check-alias-lag.ps1,release-impact.ps1' $quickOut
        if ($quickRun.Exit -ne 0) { throw "result file run with passing stubs exited $($quickRun.Exit), expected 0: $($quickRun.Output)" }
        $quickJson = Get-Content -LiteralPath $quickOut -Raw | ConvertFrom-Json
        $quickByName = @{}; foreach ($s in $quickJson.scripts) { $quickByName[$s.name] = $s }
        $quickPackage = $quickByName['package.ps1']
        if ($quickJson.mode -ne 'quick' -or $quickPackage.skipped -or @($quickPackage.args) -notcontains '-Quick') { throw 'quick result file does not record package.ps1 -SelfTest -Quick' }
        if ($quickByName['update-download-badges.ps1'].skipped -or -not $quickByName['check-alias-lag.ps1'].skipped -or $quickByName['release-impact.ps1'].skipped) { throw 'quick result file does not run the changed script and skip the unrelated one' }
        $cases.Add([pscustomobject]@{case='result file, passing stubs';mode=$quickJson.mode;reason="exit 0 kept, package.ps1 $(@($quickPackage.args) -join ' '), changed script ran, unrelated script skipped"})
        $cases | ForEach-Object { Write-Host ("PASS  selection {0}: {1} ({2})" -f $_.case,$_.mode,$_.reason) }
    }
    finally
    {
        Remove-Item -LiteralPath $fixture -Recurse -Force
        if (Test-Path -LiteralPath $stubs) { Remove-Item -LiteralPath $stubs -Recurse -Force }
    }
    exit 0
}
$decision = if ($All) { @{ Mode='all'; Base=''; Reason='-All specified'; Changed=$true; Paths=@() } } else { try { Get-ScriptsChange } catch { @{ Mode='all'; Base=''; Reason="selection error: $($_.Exception.Message)"; Changed=$true; Paths=@() } } }
$scriptSelection = @(Get-ScriptSelection $decision $SelfTestScripts)
$decision['scripts'] = $scriptSelection
# Printed before -SelectionOnly exits, so the self-test exercises this for every decision shape.
Write-Host "Selection: $($decision.Mode); base=$($decision.Base); reason=$($decision.Reason)"
if ($decision.ContainsKey('Match') -and $decision['Match']) { Write-Host "Full-run path matched: $($decision['Match'])" }
if ($SelectionOnly) { [pscustomobject]$decision | ConvertTo-Json -Compress -Depth 5; exit 0 }
$allRun = $decision.Mode -eq 'all'
$skipHeavy = $decision.Mode -eq 'skip'
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
    if (-not $SelfTestDependencies.Contains($name))
    {
        $failures.Add("$name has no dependency map entry")
        $records.Add(@{ name=$name; args=@(); seconds=0; exitCode=1; skipped=$false })
        continue
    }
    $path = Join-Path $scriptsDirPath $name
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

    $selected = @($scriptSelection | Where-Object { $_.name -eq $name })[0]
    if (-not $selected.run)
    {
        Write-Host ("SKIP  {0}  no dependency changed ({1} path(s) changed)" -f $name, @($decision.Paths).Count)
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

$skippedText = if ($skipped -gt 0) { "$skipped skipped (no dependency changed)" } else { '0 skipped' }
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
