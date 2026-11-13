#Requires -Version 7.0
<#
hooks.releaseExpected of agent-workflow.json: tells the kit's package ship (after the merge) whether CD will cut a release for the merge
commit, so ship does not wait 20 minutes for a release that never comes.

  release-expected.ps1 -Root <main checkout> -MergeCommit <sha> -Base <sha before the merge>

It repeats the train selection of cd.yml ("Compute and gate release plan"): the complete paged GitHub release list, then
scripts/release-impact.ps1 of the main checkout on "<MergeCommit>~1".."<MergeCommit>" with -ReleasesFile and -PlanOut, and a train is
selected when plan.perTrain.<train>.selected is true (each train is compared with its own last complete release, so a train whose
publication failed, or that has no complete release yet, is selected again). It prints exactly one line:

  RELEASE none - <reason>   no train is selected
  RELEASE expected          a train is selected

A release (not a draft) whose target_commitish is the merge commit already exists (ship resumed after CD published) -> RELEASE expected,
before any plan. -CurrentVersion is not passed: CD uses it only to mark a stale rerun (which can only unselect a train) and to reselect the
trains of a release already published at this version, which is the case the target_commitish check answers first; so without it "none"
is never printed when CD selects a train. Any failure (gh, git, an unclassified path, an unreadable plan) exits 1 without a decision, and the kit keeps its
bounded release wait.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Root,
    [Parameter(Mandatory)]
    [string]$MergeCommit,
    [string]$Base
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest

$work = Join-Path ([IO.Path]::GetTempPath()) "pf-release-expected-$([guid]::NewGuid().ToString('N'))"
try
{
    $script = Join-Path $Root 'scripts' 'release-impact.ps1'
    if (-not (Test-Path -LiteralPath $script -PathType Leaf)) { throw "the release impact script does not exist: $script" }
    New-Item -ItemType Directory -Path $work -Force | Out-Null

    Push-Location -LiteralPath $Root
    try
    {
        $repo = (& gh repo view --json nameWithOwner --jq .nameWithOwner 2>&1 | ForEach-Object { [string]$_ }) -join ''
        if ($LASTEXITCODE -ne 0 -or $repo -notmatch '^[^/\s]+/[^/\s]+$') { throw "gh repo view failed: $repo" }
        $all = [System.Collections.Generic.List[object]]::new()
        for ($page = 1; $page -le 50; $page++)
        {
            $output = & gh api "repos/$repo/releases?per_page=100&page=$page" 2>&1
            if ($LASTEXITCODE -ne 0) { throw "could not list releases (page $page): $($output -join ' ')" }
            $chunk = @(($output -join "`n") | ConvertFrom-Json)
            foreach ($release in $chunk) { $all.Add($release) }
            if ($chunk.Count -lt 100) { break }
            if ($page -eq 50) { throw 'more than 50 pages of releases' }
        }
    }
    finally { Pop-Location }

    $published = @($all | Where-Object { -not [bool]$_.draft -and ([string]$_.target_commitish) -ieq $MergeCommit })
    if ($published.Count -gt 0)
    {
        Write-Output 'RELEASE expected'
        exit 0
    }
    $releasesFile = Join-Path $work 'releases.json'
    $planFile = Join-Path $work 'plan.json'
    ConvertTo-Json -InputObject $all.ToArray() -Depth 10 | Set-Content -LiteralPath $releasesFile -Encoding utf8NoBOM
    $impact = @(& pwsh -NoProfile -File $script -Base "$MergeCommit~1" -Head $MergeCommit -ReleasesFile $releasesFile -PlanOut $planFile 2>&1 | ForEach-Object { [string]$_ })
    if ($LASTEXITCODE -ne 0) { throw "release-impact exited $LASTEXITCODE`: $(($impact | Select-Object -Last 3) -join ' ')" }
    $plan = Get-Content -LiteralPath $planFile -Raw | ConvertFrom-Json
    $selected = @(foreach ($train in @('desktop', 'agent'))
        {
            $entry = $plan.perTrain.$train
            if ($null -eq $entry) { throw "the plan has no perTrain.$train entry" }
            if ([bool]$entry.selected) { $train }
        })
    if ($selected.Count -eq 0)
    {
        Write-Output "RELEASE none - release-impact selected no release train for $MergeCommit against each train's last complete release"
        exit 0
    }
    Write-Output 'RELEASE expected'
    exit 0
}
catch
{
    [Console]::Error.WriteLine("release-expected: $($_.Exception.Message)")
    exit 1
}
finally
{
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
