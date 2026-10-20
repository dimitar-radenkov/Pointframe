#Requires -Version 7.0
<#
Writes the independent-review record of a commit under <git common dir>/<state.commonDirNamespace>/reviews/<sha>.json; merge-pr.ps1 refuses a merge
without a passing record for the head. The configuration is read from the MAIN CHECKOUT of the repository of -Worktree (never from the worktree itself, which a pull
request can change); an explicit -Root must be that main checkout, else the record is refused. The
base (repository.baseRef) and the pin files (pinException.paths, which do not count as review changes) come from agent-workflow.json.

  record-review.ps1 -Worktree <path> -Executor codex:gpt-6-luna -ExecutorSession <id> -Reviewer claude:opus -ReviewerSession <id> `n    -Verdict PASS -Evidence <file> -Requirement <text> -Review light|heavy [-Head <sha>] [-Brief <file>] [-SmallFix] [-Root <repository>]
  record-review.ps1 -SelfTest

Exit 0 written, 2 refused (the reason is printed).
#>
[CmdletBinding()]
param(
    [string]$Worktree,
    [string]$Head,
    [string]$Executor,
    [string]$ExecutorSession,
    [string]$Reviewer,
    [string]$ReviewerSession,
    [ValidateSet('PASS', 'FAIL')][string]$Verdict,
    [ValidateRange(0, [int]::MaxValue)][int]$BlockingFindings = 0,
    [string]$Evidence,
    [string]$Brief,
    [string]$Requirement,
    [ValidateSet('light', 'heavy')][string]$Review,
    [switch]$SmallFix,
    [string]$Root,
    [switch]$SelfTest
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '..' 'lib' 'root.ps1')
. (Join-Path $PSScriptRoot '..' 'lib' 'config.ps1')
. (Join-Path $PSScriptRoot '..' 'lib' 'context.ps1')
. (Join-Path $PSScriptRoot '..' 'lib' 'selftest.ps1')
. (Join-Path $PSScriptRoot '..' 'lib' 'tree-hash.ps1')
. (Join-Path $PSScriptRoot '..' 'lib' 'review-record.ps1')

function Invoke-SelfTest
{
    $dir = Join-Path $PSScriptRoot '..' 'fixtures' 'gates' 'merge-pr' 'reviews'
    $failures = [Collections.Generic.List[string]]::new()
    foreach ($file in Get-ChildItem -LiteralPath $dir -Filter '*.json' -File | Sort-Object Name)
    {
        $fx = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
        if ($fx.PSObject.Properties['mode']) { continue }
        $fixtureRecord = if ($fx.PSObject.Properties['record']) { $fx.record } else { $null }
        $result = Test-ReviewRecordObject $fixtureRecord 'head-a' 'tree-a' $true $true
        $expectedReason = if ($fx.expect.PSObject.Properties['reason']) { [string]$fx.expect.reason } else { '' }
        if ($result.Ok -ne [bool]$fx.expect.ok)
        { $failures.Add("$($file.Name): expected $($fx.expect.ok) '$expectedReason', got $($result.Ok) '$($result.Reason)'") }
    }
    $tempRepo = Join-Path ([IO.Path]::GetTempPath()) "record-review-e2e-$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $tempRepo | Out-Null
    try
    {
        & git -C $tempRepo init -q
        & git -C $tempRepo -c user.name=SelfTest -c user.email=selftest@example.invalid commit --allow-empty -qm initial
        if ($LASTEXITCODE -ne 0) { throw 'could not create temporary repository commit' }
        & git -C $tempRepo update-ref refs/remotes/origin/master HEAD
        Write-AwSelfTestConfig $tempRepo (Get-AwSelfTestConfig -Namespace 'selftest')
        $evidenceFile = Join-Path $tempRepo 'evidence.md'; [IO.File]::WriteAllText($evidenceFile, 'offline evidence')
        $briefFile = Join-Path $tempRepo 'brief.md'; [IO.File]::WriteAllText($briefFile, 'requirement: self test')
        $invoke = @('-NoProfile','-File',$PSCommandPath,'-Worktree',$tempRepo,'-Executor','claude:coordinator','-ExecutorSession','e2e','-Reviewer','codex:gpt-6-luna','-ReviewerSession','review','-Verdict','PASS','-Evidence',$evidenceFile,'-Requirement','offline e2e','-Review','light')
        & pwsh @invoke | Out-Null
        if ($LASTEXITCODE -ne 0) { $failures.Add('end-to-end record creation command failed') }
        $badHeadOutput = & pwsh -NoProfile -File $PSCommandPath -Worktree $tempRepo -Head does-not-exist -Executor claude:coordinator -ExecutorSession e2e -Reviewer codex:gpt-6-luna -ReviewerSession review -Verdict PASS -Evidence $evidenceFile -Requirement 'bad head check' -Review light 2>&1
        if ($LASTEXITCODE -ne 2 -or ($badHeadOutput -join "`n") -notmatch 'Head.*does-not-exist.*does not resolve') { $failures.Add("bad head did not exit 2 with a clear refusal (exit=$LASTEXITCODE output=$($badHeadOutput -join ' | '))") }
        $sameModelOutput = & pwsh -NoProfile -File $PSCommandPath -Worktree $tempRepo -Executor codex:gpt-6-luna -ExecutorSession e2e -Reviewer claude:gpt-6-luna -ReviewerSession review -Verdict PASS -Evidence $evidenceFile -Requirement 'same model check' -Review light 2>&1
        if ($LASTEXITCODE -ne 2 -or ($sameModelOutput -join "`n") -notmatch 'different models') { $failures.Add('cross-provider same-model review was accepted') }
        $head = (& git -C $tempRepo rev-parse HEAD).Trim()
        $recordFile = Join-Path $tempRepo '.git' "selftest/reviews/$head.json"
        if (-not (Test-Path -LiteralPath $recordFile -PathType Leaf)) { $failures.Add('end-to-end record was not written under git common dir') }
        else
        {
            $written = Get-Content -LiteralPath $recordFile -Raw | ConvertFrom-Json
            if (-not (Test-ReviewRecordObject $written $head ((& git -C $tempRepo rev-parse "$head^{tree}").Trim()) $true $true).Ok) { $failures.Add('end-to-end record failed shared validation') }
        }
    }
    catch { $failures.Add("end-to-end repository test failed: $($_.Exception.Message)") }
    finally { Remove-Item -LiteralPath $tempRepo -Recurse -Force -ErrorAction SilentlyContinue }
    if ($failures.Count) { $failures | ForEach-Object { Write-Host "ERROR self-test: $_" }; exit 1 }
Write-Host 'record-review self-test passed.'
    exit 0
}
if ($SelfTest) { Invoke-SelfTest }

function Refuse([string]$Message) { Write-Host "REVIEW RECORD REFUSED: $Message"; exit 2 }
if (-not $Worktree -or -not $Executor -or -not $ExecutorSession -or -not $Reviewer -or -not $Verdict -or -not $Evidence -or -not $Review)
{ Refuse 'Worktree, executor and session, reviewer, verdict, evidence, and review are required.' }
if ($Executor -notmatch '^(codex|claude):[A-Za-z0-9._-]+$' -or ($Reviewer -ne 'self' -and $Reviewer -notmatch '^(codex|claude):[A-Za-z0-9._-]+$'))
{ Refuse 'Executor and reviewer must use provider:model identity syntax (reviewer may be self for -SmallFix).' }
if ($ExecutorSession -notmatch '^[A-Za-z0-9_-]+$' -or ($ReviewerSession -and $ReviewerSession -notmatch '^[A-Za-z0-9_-]+$'))
{ Refuse 'Session identifiers may contain only letters, digits, underscore, and hyphen.' }
if ($Requirement -and [string]::IsNullOrWhiteSpace($Requirement)) { Refuse '-Requirement must not be blank.' }
if (($Brief -and $Requirement) -or (-not $Brief -and -not $Requirement)) { Refuse 'Specify exactly one of -Brief or -Requirement.' }
if ($Reviewer -eq 'self' -and (-not $SmallFix -or $ReviewerSession)) { Refuse '-Reviewer self is valid only with -SmallFix and no reviewer session.' }
if ($SmallFix -and ($Executor -ne 'claude:coordinator' -or $Reviewer -ne 'self')) { Refuse '-SmallFix requires executor claude:coordinator and reviewer self.' }
if (-not $SmallFix -and -not $ReviewerSession) { Refuse '-ReviewerSession is required outside the small-fix exception.' }
if (-not $SmallFix) {
    $executorModel = ($Executor -split ':', 2)[-1].ToLowerInvariant()
    $reviewerModel = ($Reviewer -split ':', 2)[-1].ToLowerInvariant()
    if ($Executor.Trim().ToLowerInvariant() -eq $Reviewer.Trim().ToLowerInvariant() -or $executorModel -eq $reviewerModel) { Refuse 'executor and reviewer must use different models.' }
}
if (-not (Test-Path -LiteralPath $Worktree -PathType Container)) { Refuse "worktree does not exist: $Worktree" }
$worktreeRoot = (Resolve-Path -LiteralPath $Worktree).Path
if (([string](& git -C $worktreeRoot rev-parse --is-inside-work-tree 2>$null)).Trim() -ne 'true') { Refuse "worktree is not a Git worktree: $worktreeRoot" }
try { $Context = Get-AwGateContext -Root $Root -Worktree $worktreeRoot -ScriptRoot $PSScriptRoot }
catch { Refuse $_.Exception.Message }
$baseRef = $Context.BaseRef
if (-not (Test-Path -LiteralPath $Evidence -PathType Leaf)) { Refuse "evidence file does not exist: $Evidence" }
$evidencePath = (Resolve-Path -LiteralPath $Evidence).Path
if ($Brief -and -not (Test-Path -LiteralPath $Brief -PathType Leaf)) { Refuse "brief file does not exist: $Brief" }
$headOutput = if ($Head) { & git -C $worktreeRoot rev-parse --verify "$Head^{commit}" 2>$null } else { & git -C $worktreeRoot rev-parse --verify 'HEAD^{commit}' 2>$null }
$headExitCode = $LASTEXITCODE
$resolved = if ($headOutput) { ([string]$headOutput).Trim() } else { '' }
if ($headExitCode -ne 0 -or $resolved -notmatch '^[0-9a-fA-F]{40,64}$') { Refuse "Head '$Head' does not resolve to a full commit SHA." }
$tree = (& git -C $worktreeRoot rev-parse "$resolved^{tree}").Trim()
if ($LASTEXITCODE -ne 0 -or $tree -notmatch '^[0-9a-fA-F]{40,64}$') { Refuse 'Could not resolve the head tree SHA.' }
$files = @(); $lines = 0
if ($SmallFix)
{
    $base = (& git -C $worktreeRoot merge-base $resolved $baseRef 2>$null).Trim()
    if ($LASTEXITCODE -ne 0 -or -not $base) { Refuse "Could not resolve $baseRef merge-base for small-fix count." }
    $changeCount = Get-ReviewChangeCount $worktreeRoot $base $resolved $Context.PinFiles
    $files = $changeCount.Files; $lines = $changeCount.Lines
    if ($files.Count -gt 2 -or $lines -gt 12) { Refuse "Small-fix exceeds threshold: $($files.Count) files / $lines changed lines (maximum 2 files and 12 added+removed lines)." }
}
else
{
    $base = (& git -C $worktreeRoot merge-base $resolved $baseRef 2>$null).Trim()
    if ($LASTEXITCODE -ne 0 -or -not $base) { Refuse "Could not resolve $baseRef merge-base for review change counts." }
    $changeCount = Get-ReviewChangeCount $worktreeRoot $base $resolved $Context.PinFiles
    $files = $changeCount.Files; $lines = $changeCount.Lines
}
$common = (& git -C $worktreeRoot rev-parse --path-format=absolute --git-common-dir).Trim()
if ($LASTEXITCODE -ne 0 -or -not $common) { Refuse 'Could not resolve git common directory.' }
if (-not [IO.Path]::IsPathRooted($common)) { $common = Join-Path $worktreeRoot $common }
$common = [IO.Path]::GetFullPath($common)
$directory = Join-Path $common "$($Context.Config.state.commonDirNamespace)/reviews"
New-Item -ItemType Directory -Path $directory -Force | Out-Null
$record = [ordered]@{ schema = 1; headSha = $resolved; treeSha = $tree }
if ($Brief) { $record.briefSha256 = (Get-FileHash -LiteralPath $Brief -Algorithm SHA256).Hash.ToLowerInvariant() } else { $record.requirement = $Requirement }
$record.executor = $Executor; $record.executorSession = $ExecutorSession; $record.reviewer = $Reviewer
if ($ReviewerSession) { $record.reviewerSession = $ReviewerSession }
$record.review = $Review; $record.verdict = $Verdict; $record.blockingFindings = $BlockingFindings
$record.evidence = $evidencePath; $record.recordedAtUtc = [DateTimeOffset]::UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'")
$record.recordedBy = 'claude:coordinator'
if ($SmallFix) { $record.reason = 'small-fix' }
$record.changedFiles = $files; $record.changedLines = $lines
$target = Join-Path $directory "$resolved.json"; $temp = Join-Path $directory ".$resolved.$([guid]::NewGuid().ToString('N')).tmp"
try { [IO.File]::WriteAllText($temp, ($record | ConvertTo-Json -Depth 8) + "`n", [Text.UTF8Encoding]::new($false)); Move-Item -LiteralPath $temp -Destination $target -Force }
finally { Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue }
Write-Host "Review record written: $target"
exit 0
