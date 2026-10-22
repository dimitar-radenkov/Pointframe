# ---- ship: pure helpers ------------------------------------------------------------------------------------------

function Get-ShipTitle([string]$BriefPath, [string]$Explicit)
{
    if (-not [string]::IsNullOrWhiteSpace($Explicit)) { return $Explicit.Trim() }
    foreach ($line in (Get-Content -LiteralPath $BriefPath))
    {
        if ($line -match '^#\s+(.*\S)\s*$')
        {
            $heading = ($Matches[1] -replace '(?i)^Brief:\s*', '').Trim()
            if ($heading) { return $(if ($heading.Length -gt 240) { $heading.Substring(0, 240) } else { $heading }) }
        }
    }
    throw 'the brief has no H1 heading to use as the pull request title: pass -Title'
}

function Get-ShipObjective([string]$BriefPath)
{
    $lines = @(Get-Content -LiteralPath $BriefPath)
    $start = 0
    for ($i = 0; $i -lt $lines.Count; $i++) { if ($lines[$i] -match '^\s{0,3}#{1,6}\s+Objective\b') { $start = $i + 1; break } }
    $paragraph = @()
    for ($i = $start; $i -lt $lines.Count; $i++)
    {
        $line = $lines[$i]
        if ($line -match '^\s{0,3}#{1,6}\s' -or $line.Trim() -eq '') { if ($paragraph.Count) { break } else { continue } }
        $paragraph += $line.Trim()
    }
    if ($paragraph.Count) { $paragraph -join ' ' } else { '(the brief has no objective paragraph)' }
}

function Get-ShortText([string]$Text, [int]$Max = 300)
{
    $flat = (($Text -replace '\s+', ' ').Trim())
    if ($flat.Length -gt $Max) { $flat.Substring(0, $Max) + '...' } else { $flat }
}

function Get-ShipNotTested($Review, $Qualify)
{
    # Everything the record itself says was not covered: criteria that are not a pass, the reviewer's omissions, a skipped desktop run.
    $items = @()
    foreach ($result in (Get-List $Review.criteriaResults))
    {
        if ($result.result -ne 'pass')
        {
            $text = (@(Get-List $Review.criteria | Where-Object { $_.id -eq $result.id })[0]).text
            $items += "Criterion $($result.id) ($text): $($result.result) - $(Get-ShortText ([string]$result.evidence))"
        }
    }
    foreach ($omission in (Get-List (Get-Field $Review 'omissions'))) { $items += "Reviewer omission: $(Get-ShortText ([string]$omission))" }
    if (@(Get-List $Review.denials).Count) { $items += "The reviewer was denied $(@(Get-List $Review.denials).Count) tool call(s), so it may not have inspected everything." }
    if ($Qualify)
    {
        if ([string](Get-Field $Qualify 'status') -eq 'not-configured') { $items += 'No desktop qualification: hooks.qualify is not configured for this repository, so no impact decision was made and nothing was exercised on the desktop.' }
        elseif (-not [bool](Get-Field $Qualify 'required')) { $items += 'No desktop run: the qualify hook reported desktop-qualification not-required for this diff, so the candidate was not exercised on the desktop.' }
        else
        {
            foreach ($skipped in (Get-List (Get-Field $Qualify 'skippedSteps'))) { $items += "Desktop qualification skipped: $(Get-ShortText ([string]$skipped))" }
        }
    }
    else { $items += 'Desktop qualification: no record (it was not evaluated).' }
    @($items)
}

function New-ShipPrBody($State, $Review, [string]$Head, [string]$Tree, $VerifyStep, $QualifyStep)
{
    $brief = [string]$State.brief.path
    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add('## Summary')
    $lines.Add('')
    $lines.Add((Get-ShipObjective $brief))
    $lines.Add('')
    $lines.Add('## Package')
    $lines.Add('')
    $lines.Add("- package ``$($State.slug)``, branch ``$($State.branch)``, base ``$($State.baseSha)``")
    $lines.Add("- head ``$Head``, tree ``$Tree``")
    $executors = @(Get-List $Review.executors | ForEach-Object { "$($_.provider):$($_.model) ($($_.executorKind))" } | Select-Object -Unique)
    $lines.Add("- executor: $(if ($executors.Count) { $executors -join ', ' } else { "$($Review.executor.provider):$($Review.executor.model)" })")
    $lines.Add("- review: $($Review.level), round $($Review.round), reviewer $($Review.reviewer.kind) ($($Review.reviewer.provider):$($Review.reviewer.model)), $($Review.blockingFindings) blocking finding(s); verdict file ``$($Review.verdictFile.path)``")
    $lines.Add("- accepted by the coordinator: $($State.acceptance.reason)")
    $lines.Add('')
    $lines.Add('## Acceptance criteria')
    $lines.Add('')
    foreach ($criterion in (Get-List $Review.criteria))
    {
        $result = @(Get-List $Review.criteriaResults | Where-Object { $_.id -eq $criterion.id })[0]
        $lines.Add("- **$($criterion.id)** $($criterion.text)")
        $lines.Add("  - reviewer result: $(if ($result) { $result.result } else { 'no result' }) - $(if ($result) { Get-ShortText ([string]$result.evidence) } else { '' })")
    }
    $lines.Add('')
    $lines.Add('## Checks run')
    $lines.Add('')
    $check = @(Get-List $State.checks | Where-Object { $_.checkId -eq $Review.checkId })[0]
    if ($check)
    {
        foreach ($command in (Get-List $check.commands))
        {
            $outcome = if ($command.skipped) { "skipped ($($command.skipped))" } elseif ($command.exitCode -eq 0) { 'passed' } else { "exit $($command.exitCode)" }
            $lines.Add("- check ``$($command.name)``: $outcome")
        }
    }
    else { $lines.Add('- no covering check is recorded') }
    $lines.Add("- verify.ps1: $($VerifyStep.receipt.status), complete $($VerifyStep.receipt.complete), head ``$($VerifyStep.receipt.head)``, tree ``$($VerifyStep.receipt.treeHash)``")
    if ($QualifyStep -and [string](Get-Field $QualifyStep 'status') -eq 'not-configured') { $lines.Add('- desktop qualification: not configured (hooks.qualify is null; no decision was made and nothing was run)') }
    elseif ($QualifyStep -and [bool](Get-Field $QualifyStep 'required'))
    {
        $lines.Add("- desktop qualification (candidate check): $($QualifyStep.result)$(if (Get-Field $QualifyStep 'reused') { ' (reused: bound to this head and tree)' }); packages $(@(Get-List $QualifyStep.packages | ForEach-Object { "$($_.name) sha256 $($_.sha256)" }) -join '; ')")
    }
    else { $lines.Add('- desktop qualification: not required (the qualify hook matched no shipping path)') }
    $lines.Add('')
    $lines.Add('## What was not tested')
    $lines.Add('')
    foreach ($item in (Get-ShipNotTested $Review $QualifyStep)) { $lines.Add("- $item") }
    $lines.Add('- Only the checks listed above were run; anything not listed was not exercised by this record.')
    $lines -join "`n"
}

function Test-ShipInputDesktop
{
    if ($env:AW_PKG_SELFTEST -and $env:AW_PKG_TEST_DESKTOP) { return ($env:AW_PKG_TEST_DESKTOP -eq 'unlocked') }
    if (-not ('AwPackageInputDesktop' -as [type]))
    {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class AwPackageInputDesktop {
    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] public static extern bool CloseDesktop(IntPtr desktop);
}
'@
    }
    $desktop = [AwPackageInputDesktop]::OpenInputDesktop(0, $false, 0x0100)
    if ($desktop -eq [IntPtr]::Zero) { return $false }
    [void][AwPackageInputDesktop]::CloseDesktop($desktop)
    $true
}

function Find-ReusableQualification($State, [string]$Head, [string]$Tree)
{
    # A recorded passing qualification is evidence only for the head and tree it ran on, and only while the candidate artifacts it
    # hashed are still the ones on disk. Returns the record, or the reasons why none can be reused.
    $problems = @()
    foreach ($candidate in @(Get-List $State.qualifications | Sort-Object { [string]$_.utc } -Descending))
    {
        $why = @()
        if ($candidate.result -notin @('passed', 'passed_with_skips')) { continue }
        if ([string]$candidate.head -ne $Head) { $why += "bound to head $($candidate.head), not $Head" }
        elseif ([string]$candidate.tree -ne $Tree) { $why += "bound to tree $($candidate.tree), not $Tree" }
        else
        {
            $packages = @(Get-List $candidate.packages)
            if ($packages.Count -eq 0) { $why += 'it recorded no candidate artifact hash' }
            $resultFile = [string]$candidate.resultJson.path
            if (-not $resultFile -or -not (Test-Path -LiteralPath $resultFile -PathType Leaf) -or (Get-Sha256 $resultFile) -ne [string]$candidate.resultJson.sha256) { $why += 'its result.json is missing or changed' }
            foreach ($package in $packages)
            {
                $file = Join-Path ([string]$candidate.workDirectory) "candidate/$($package.name)"
                if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { $why += "the candidate artifact $($package.name) is no longer on disk" }
                elseif ((Get-Sha256 $file) -ne [string]$package.sha256) { $why += "the candidate artifact $($package.name) differs from the recorded hash" }
            }
        }
        if ($why.Count -eq 0) { return @{ Record = $candidate; Problems = @() } }
        $problems += "qualification $($candidate.id): $($why -join '; ')"
    }
    @{ Record = $null; Problems = $problems }
}

function Get-ShipReview($State)
{
    $review = Get-LatestReview $State
    if (-not $review -or $review.status -ne 'valid' -or $review.verdict -ne 'PASS' -or [int]$review.blockingFindings -ne 0) { return $null }
    $acceptance = $State.acceptance
    if (-not $acceptance -or $acceptance.invalidated -or [string]$acceptance.reviewId -ne [string]$review.reviewId -or [string]$acceptance.commit -ne [string]$review.commit) { return $null }
    $review
}

function Get-ShipPrState($Info, [string]$Number)
{
    $result = Invoke-Gh @('pr', 'view', $Number, '--json', 'state,mergeCommit,headRefOid,autoMergeRequest') $Info.MainCheckout -StdoutOnly
    if ($result.Exit -ne 0) { return $null }
    try { $prJson = $result.Text | ConvertFrom-Json } catch { return $null }
    $mergeCommit = $null
    if ($prJson.PSObject.Properties['mergeCommit'] -and $prJson.mergeCommit -and $prJson.mergeCommit.PSObject.Properties['oid']) { $mergeCommit = [string]$prJson.mergeCommit.oid }
    $autoMerge = [bool]($prJson.PSObject.Properties['autoMergeRequest'] -and $prJson.autoMergeRequest)
    @{ State = [string]$prJson.state; MergeCommit = $mergeCommit; Head = [string]$prJson.headRefOid; AutoMerge = $autoMerge }
}

function Find-ShipPr($Info, [string]$Branch)
{
    $result = Invoke-Gh @('pr', 'list', '--head', $Branch, '--state', 'all', '--limit', '20', '--json', 'number,url,state,headRefOid') $Info.MainCheckout -StdoutOnly
    if ($result.Exit -ne 0) { throw "gh pr list failed (exit $($result.Exit))" }
    if (-not $result.Text.Trim()) { return $null }
    $found = @($result.Text | ConvertFrom-Json)
    $open = @($found | Where-Object { $_.state -eq 'OPEN' })
    if ($open.Count -gt 1) { throw "several open pull requests exist for $Branch ($(($open | ForEach-Object { '#' + $_.number }) -join ', ')); close the extra ones by hand" }
    if ($open.Count -eq 1) { return @{ Number = [string]$open[0].number; Url = [string]$open[0].url; State = 'OPEN'; Head = [string]$open[0].headRefOid } }
    $merged = @($found | Where-Object { $_.state -eq 'MERGED' })
    if ($merged.Count) { return @{ Number = [string]$merged[0].number; Url = [string]$merged[0].url; State = 'MERGED'; Head = [string]$merged[0].headRefOid } }
    $null
}

