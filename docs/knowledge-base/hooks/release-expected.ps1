#Requires -Version 7.0
<#
hooks.releaseExpected of agent-workflow.json: tells the kit's package ship (after the merge) whether CD will cut a release for the merge
commit, so ship does not wait 20 minutes for a release that never comes.

  release-expected.ps1 -Root <main checkout> -MergeCommit <sha> -Base <sha before the merge>

It classifies the merge commit's own change ("<MergeCommit>~1".."<MergeCommit>", the range cd.yml uses) with scripts/release-impact.ps1
of the main checkout and prints exactly one line:

  RELEASE none - <reason>   release-impact selected no release train (CI, script or docs-only change)
  RELEASE expected          any train was selected

Any failure (git, an unclassified path, an unreadable output) exits 1 without a decision, and the kit keeps its bounded release wait.
Known gap: CD also re-selects a train whose last publication failed; this hook does not read the releases list, so such a retry is
reported as "none" and its channels are not reported by ship (run scripts/check-release-channels.ps1 by hand).
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

try
{
    $script = Join-Path $Root 'scripts' 'release-impact.ps1'
    if (-not (Test-Path -LiteralPath $script -PathType Leaf)) { throw "the release impact script does not exist: $script" }
    $lines = @(& pwsh -NoProfile -File $script -Base "$MergeCommit~1" -Head $MergeCommit 2>&1 | ForEach-Object { [string]$_ })
    if ($LASTEXITCODE -ne 0) { throw "release-impact exited $LASTEXITCODE" }
    $trainLines = @($lines | Where-Object { $_ -match '^release-trains: ' })
    if ($trainLines.Count -ne 1) { throw "release-impact printed $($trainLines.Count) release-trains lines" }
    $trains = $trainLines[0].Substring('release-trains: '.Length).Trim()
    if ($trains -ceq 'none')
    {
        Write-Output "RELEASE none - release-impact selected no release train for $MergeCommit"
        exit 0
    }
    if ($trains -notmatch '^(desktop|agent)(,(desktop|agent))?$') { throw "unexpected release-trains value: $trains" }
    Write-Output 'RELEASE expected'
    exit 0
}
catch
{
    [Console]::Error.WriteLine("release-expected: $($_.Exception.Message)")
    exit 1
}
