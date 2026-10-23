#Requires -Version 7.0
<#
Keeps GitHub's Latest release a desktop release. Installed desktop updaters read releases/latest and download its
Pointframe-*-Setup.exe, so Latest must always be the highest published release that has a real installer whose version
equals its tag (D-019). The decisions are pure functions over parsed releases; only the thin gh layer below touches GitHub.

  pwsh scripts/check-latest-release.ps1                          # report; exit 1 when Latest is wrong
  pwsh scripts/check-latest-release.ps1 -Repair                  # re-mark the correct release Latest, still exit 1 (CD turns red)
  pwsh scripts/check-latest-release.ps1 -Expect v6.7.90          # assert Latest is exactly this tag (CD proofs)
  pwsh scripts/check-latest-release.ps1 -Promote v6.7.91         # make_latest=true only if v6.7.91 is the highest desktop release
                                                                 # (an older desktop release: left alone, exit 0; not a desktop release at all: exit 1)
  pwsh scripts/check-latest-release.ps1 -Update v6.7.91 -MakeLatest false   # PATCH the release, body unchanged, make_latest explicit
  pwsh scripts/check-latest-release.ps1 -SelfTest                # offline: fixtures in scripts/tests/latest-release

Common: -Repository owner/name, -ResultOut <file> (JSON: mode, status, details). -ReleasesFile and -LatestFile read
the releases list (array) and the releases/latest object from files instead of GitHub; with either, nothing is changed:
the REST call that would run is printed as DRY-RUN.

A desktop release (what an installed GitHubUpdateService can use) is published (not draft, not prerelease), has a numeric
v<x.y.z> tag and Pointframe-<x.y.z>-x64-Setup.exe with the same version (see release-model.ps1). Decision:

  ok      Latest is a desktop release and no published desktop release has a higher version
  repair  Latest is missing, is not a desktop release (an agent-only release, a draft, a missing or mismatched installer),
          or is below the highest desktop release (a regression, or an older version created later); the target is the
          highest desktop release

Exit codes: 0 ok, 1 wrong Latest or a failed proof (a ::error:: line is printed), 2 bad arguments.
#>
[CmdletBinding()]
param(
    [switch]$SelfTest,
    [switch]$Repair,
    [string]$Expect,
    [string]$Promote,
    [string]$Update,
    [ValidateSet('true', 'false', 'legacy')]
    [string]$MakeLatest,
    [string]$Repository = 'dimitar-radenkov/Pointframe',
    [string]$ReleasesFile,
    [string]$LatestFile,
    [string]$ResultOut
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

. (Join-Path $PSScriptRoot 'release-model.ps1')

# ---------------------------------------------------------------------------------------------------------------------
# Pure decisions. They take parsed JSON and decide; they never call gh or the network.
# ---------------------------------------------------------------------------------------------------------------------

function Get-AllPages
{
    # Reads pages until one is short. FetchPage takes a 1-based page number and returns that page's items.
    param([scriptblock]$FetchPage, [int]$PageSize = 100, [int]$MaxPages = 50)

    $all = [System.Collections.Generic.List[object]]::new()
    for ($page = 1; $page -le $MaxPages; $page++)
    {
        $items = @(& $FetchPage $page)
        foreach ($item in $items)
        {
            $all.Add($item)
        }

        if ($items.Count -lt $PageSize)
        {
            return $all.ToArray()
        }
    }

    throw "More than $MaxPages pages of releases; raise -MaxPages deliberately."
}

function Get-LatestDecision
{
    param([object[]]$Releases, $Latest)

    $desktop = @(Get-TrainReleases $Releases 'desktop')
    $latestTag = if ($null -eq $Latest) { '' } else { [string](Get-ModelProperty $Latest 'tag_name' '') }
    $latestIsDesktop = $null -ne $Latest -and (Test-TrainRelease $Latest 'desktop')

    if ($desktop.Count -eq 0)
    {
        if ($latestIsDesktop)
        {
            return [pscustomobject]@{ Decision = 'ok'; Target = $null; Reason = "Latest $latestTag is a desktop release; the release list holds no other"; Highest = $latestTag }
        }

        return [pscustomobject]@{ Decision = 'repair'; Target = $null; Reason = 'no published desktop release exists to mark as Latest'; Highest = '' }
    }

    $highest = $desktop[0]
    $highestTag = [string]$highest.Release.tag_name
    if (-not $latestIsDesktop)
    {
        $why = if ($null -eq $Latest) { 'releases/latest returned no release' }
        else { "Latest $latestTag is not a published desktop release (needs a numeric tag and Pointframe-<version>-x64-Setup.exe with the version of the tag; assets: $((Get-ReleaseAssetNames $Latest) -join ', '))" }
        return [pscustomobject]@{ Decision = 'repair'; Target = $highest.Release; Reason = "$why; the highest desktop release is $highestTag"; Highest = $highestTag }
    }

    $latestVersion = ConvertTo-ReleaseVersion $latestTag
    if ($latestVersion -lt $highest.Version)
    {
        return [pscustomobject]@{ Decision = 'repair'; Target = $highest.Release; Reason = "Latest $latestTag is below the highest desktop release $highestTag"; Highest = $highestTag }
    }

    return [pscustomobject]@{ Decision = 'ok'; Target = $null; Reason = "Latest $latestTag is the highest desktop release"; Highest = $highestTag }
}

function Test-ExpectedLatest
{
    param($Latest, [string]$ExpectedTag)

    $latestTag = if ($null -eq $Latest) { '' } else { [string](Get-ModelProperty $Latest 'tag_name' '') }
    if ($latestTag -ceq $ExpectedTag)
    {
        return [pscustomobject]@{ Ok = $true; Actual = $latestTag; Reason = "Latest is $ExpectedTag as expected" }
    }

    return [pscustomobject]@{ Ok = $false; Actual = $latestTag; Reason = "Latest is '$latestTag', expected '$ExpectedTag'" }
}

function Get-PromotionDecision
{
    # A release is marked Latest only when it is the highest published desktop release, so a re-run of an older CD
    # attempt cannot move Latest backwards.
    param([object[]]$Releases, [string]$Tag)

    $desktop = @(Get-TrainReleases $Releases 'desktop')
    $mine = @($desktop | Where-Object { $_.Release.tag_name -ceq $Tag })
    if ($mine.Count -eq 0)
    {
        return [pscustomobject]@{ Promote = $false; Kind = 'unknown'; Release = $null; Reason = "$Tag is not a published desktop release" }
    }

    $highestTag = [string]$desktop[0].Release.tag_name
    if ($highestTag -cne $Tag)
    {
        return [pscustomobject]@{ Promote = $false; Kind = 'older'; Release = $mine[0].Release; Reason = "$Tag is not the highest desktop release ($highestTag is); Latest is left alone" }
    }

    return [pscustomobject]@{ Promote = $true; Kind = 'highest'; Release = $mine[0].Release; Reason = "$Tag is the highest published desktop release" }
}

# ---------------------------------------------------------------------------------------------------------------------
# Self-test
# ---------------------------------------------------------------------------------------------------------------------

function Get-FixtureReleases
{
    param($Fixture)

    if ($null -ne $Fixture.PSObject.Properties['pages'])
    {
        $pages = @($Fixture.pages)
        $fetch = { param($n) $pages[$n - 1] }.GetNewClosure()
        return @{ Releases = @(Get-AllPages -FetchPage $fetch); FirstPage = @($pages[0]); PageCount = $pages.Count }
    }

    return @{ Releases = @($Fixture.releases); FirstPage = @($Fixture.releases); PageCount = 1 }
}

function Test-LatestFixtures
{
    $directory = Join-Path $PSScriptRoot 'tests/latest-release'
    $required = @(
        'combined-historical', 'desktop-only', 'newer-agent-only', 'draft', 'prerelease', 'missing-installer',
        'malformed-installer', 'installer-version-differs-from-tag', 'pagination-over-100', 'agent-wrongly-latest',
        'regressed-latest', 'older-created-later', 'rerun-older-cd', 'latest-missing'
    )
    foreach ($name in $required)
    {
        if (-not (Test-Path -LiteralPath (Join-Path $directory "$name.json")))
        {
            throw "Missing fixture scripts/tests/latest-release/$name.json."
        }
    }

    $files = @(Get-ChildItem -LiteralPath $directory -Filter '*.json' | Sort-Object Name)
    foreach ($file in $files)
    {
        $fixture = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
        $data = Get-FixtureReleases $fixture
        $latest = $null
        $latestTag = Get-ModelProperty $fixture 'latestTag' $null
        if ($null -ne $latestTag)
        {
            $latest = @($data.Releases | Where-Object { $_.tag_name -ceq $latestTag }) | Select-Object -First 1
            if ($null -eq $latest)
            {
                throw "$($file.Name): latestTag '$latestTag' is not in the releases."
            }
        }

        $got = Get-LatestDecision $data.Releases $latest
        if ($got.Decision -ne $fixture.expected)
        {
            throw "$($file.Name): expected '$($fixture.expected)', got '$($got.Decision)' ($($got.Reason))."
        }

        $target = Get-ModelProperty $fixture 'repairTarget' $null
        if ($fixture.expected -eq 'repair')
        {
            $gotTarget = if ($null -eq $got.Target) { $null } else { [string]$got.Target.tag_name }
            if ($gotTarget -ne $target)
            {
                throw "$($file.Name): expected repair target '$target', got '$gotTarget'."
            }
        }

        $pagesNeeded = Get-ModelProperty $fixture 'pagesMatter' $false
        if ($pagesNeeded)
        {
            $firstOnly = Get-LatestDecision $data.FirstPage $latest
            if ($data.PageCount -lt 2 -or $firstOnly.Decision -eq $got.Decision)
            {
                throw "$($file.Name): reading only the first page must give a different decision than reading every page."
            }
        }

        foreach ($proof in @(Get-ModelProperty $fixture 'expectCases' @()))
        {
            $check = Test-ExpectedLatest $latest $proof.tag
            if ($check.Ok -ne [bool]$proof.ok)
            {
                throw "$($file.Name): -Expect $($proof.tag) expected ok=$($proof.ok), got $($check.Ok) ($($check.Reason))."
            }
        }

        foreach ($case in @(Get-ModelProperty $fixture 'promoteCases' @()))
        {
            $promotion = Get-PromotionDecision $data.Releases $case.tag
            if ($promotion.Promote -ne [bool]$case.promote)
            {
                throw "$($file.Name): promoting $($case.tag) expected $($case.promote), got $($promotion.Promote) ($($promotion.Reason))."
            }
        }
    }

    return $files.Count
}

function Test-CommandLine
{
    # Runs this script on file inputs (dry run: nothing is changed, the REST call is printed) and checks modes, output and exit codes.
    $inputs = @('-ReleasesFile', (Join-Path $PSScriptRoot 'tests/latest-release/cli/releases.json'), '-LatestFile', (Join-Path $PSScriptRoot 'tests/latest-release/cli/latest.json'))
    $cases = @(
        @{ Arguments = @(); Exit = 1; Contains = @('::error::', 'below the highest desktop release v6.9.5'); Absent = @('DRY-RUN') }
        @{ Arguments = @('-Repair'); Exit = 1; Contains = @('::error::', 'DRY-RUN PATCH repos/dimitar-radenkov/Pointframe/releases/101 make_latest=true', 'Re-marked v6.9.5'); Absent = @() }
        @{ Arguments = @('-Expect', 'v6.9.4'); Exit = 0; Contains = @('Latest is v6.9.4 as expected'); Absent = @('::error::') }
        @{ Arguments = @('-Expect', 'v6.9.5'); Exit = 1; Contains = @("::error::Latest is 'v6.9.4', expected 'v6.9.5'"); Absent = @() }
        @{ Arguments = @('-Promote', 'v6.9.4'); Exit = 0; Contains = @('Not promoting v6.9.4'); Absent = @('DRY-RUN') }
        @{ Arguments = @('-Promote', 'v6.9.5'); Exit = 0; Contains = @('DRY-RUN PATCH repos/dimitar-radenkov/Pointframe/releases/101 make_latest=true'); Absent = @() }
        @{ Arguments = @('-Promote', 'v9.9.9'); Exit = 1; Contains = @('::error::Cannot promote: v9.9.9 is not a published desktop release'); Absent = @('DRY-RUN') }
        @{ Arguments = @('-Update', 'v6.9.5', '-MakeLatest', 'false'); Exit = 0; Contains = @('DRY-RUN PATCH repos/dimitar-radenkov/Pointframe/releases/101 make_latest=false', 'body unchanged'); Absent = @() }
        @{ Arguments = @('-Expect', 'v6.9.5', '-Repair'); Exit = 2; Contains = @(); Absent = @() }
        @{ Arguments = @('-MakeLatest', 'true'); Exit = 2; Contains = @(); Absent = @() }
        @{ Arguments = @('-Update', 'v6.9.5'); Exit = 2; Contains = @(); Absent = @() }
        @{ Arguments = @('-Expect', '6.9.5'); Exit = 2; Contains = @(); Absent = @() }
    )
    foreach ($case in $cases)
    {
        $lines = @(& pwsh -NoProfile -File $PSCommandPath @inputs @($case.Arguments) 2>&1 | ForEach-Object { "$_" })
        $text = $lines -join "`n"
        if ($LASTEXITCODE -ne $case.Exit)
        {
            throw "check-latest-release $($case.Arguments -join ' '): expected exit $($case.Exit), got $LASTEXITCODE ($text)."
        }

        foreach ($needle in $case.Contains)
        {
            if ($text -notlike "*$needle*")
            {
                throw "check-latest-release $($case.Arguments -join ' '): output lacks '$needle' ($text)."
            }
        }

        foreach ($needle in $case.Absent)
        {
            if ($text -like "*$needle*")
            {
                throw "check-latest-release $($case.Arguments -join ' '): output must not contain '$needle' ($text)."
            }
        }
    }

    return $cases.Count
}

if ($SelfTest)
{
    $count = Test-LatestFixtures
    $commands = Test-CommandLine
    Write-Host "latest-release-selftest: $count fixtures and $commands command-line cases passed offline (decisions, repair targets, paging, -Expect, promotion and -Update dry runs)."
    exit 0
}

# ---------------------------------------------------------------------------------------------------------------------
# gh layer. Never exercised by the self-test.
# ---------------------------------------------------------------------------------------------------------------------

function Write-Result
{
    param([hashtable]$Result)

    if ($ResultOut)
    {
        $directory = Split-Path -Path $ResultOut -Parent
        if ($directory -and -not (Test-Path -LiteralPath $directory))
        {
            New-Item -ItemType Directory -Path $directory -Force | Out-Null
        }

        $Result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $ResultOut -Encoding utf8NoBOM
    }
}

function Invoke-GhApi
{
    # Returns the parsed JSON; $null on HTTP 404; throws on anything else.
    param([string[]]$Arguments)

    $output = @(& gh api @Arguments 2>&1 | ForEach-Object { "$_" })
    $text = ($output -join "`n").Trim()
    if ($LASTEXITCODE -ne 0)
    {
        if ($text -match 'HTTP 404')
        {
            return $null
        }

        throw "gh api $($Arguments -join ' ') failed: $text"
    }

    if (-not $text)
    {
        return $null
    }

    return $text | ConvertFrom-Json
}

function Get-ReleasesSource
{
    if ($ReleasesFile)
    {
        return @(Get-Content -LiteralPath $ReleasesFile -Raw | ConvertFrom-Json)
    }

    return @(Get-AllPages -FetchPage { param($page) Invoke-GhApi @("repos/$Repository/releases?per_page=100&page=$page") })
}

function Get-LatestSource
{
    if ($LatestFile)
    {
        return Get-Content -LiteralPath $LatestFile -Raw | ConvertFrom-Json
    }

    return Invoke-GhApi @("repos/$Repository/releases/latest")
}

function Invoke-ReleaseUpdate
{
    # PATCH /repos/{repo}/releases/{id}. make_latest is always sent (never defaulted); Body, when given, is sent back as it was.
    param($Release, [string]$Make, [object]$Body = $null)

    $payload = [ordered]@{ make_latest = $Make }
    if ($null -ne $Body)
    {
        $payload['body'] = $Body
    }

    $path = "repos/$Repository/releases/$($Release.id)"
    if ($ReleasesFile -or $LatestFile)
    {
        Write-Host "DRY-RUN PATCH $path make_latest=$Make"
        return
    }

    $directory = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }
    $file = Join-Path $directory "release-update-$([guid]::NewGuid().ToString('N')).json"
    try
    {
        $payload | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $file -Encoding utf8NoBOM
        Invoke-GhApi @('--method', 'PATCH', $path, '--input', $file) | Out-Null
    }
    finally
    {
        Remove-Item -LiteralPath $file -Force -ErrorAction SilentlyContinue
    }
}

function Stop-Failed
{
    param([string]$Message, [hashtable]$Result)

    Write-Host "::error::$Message"
    $Result['status'] = 'failed'
    $Result['message'] = $Message
    Write-Result $Result
    exit 1
}

$modes = @($Expect, $Promote, $Update | Where-Object { $_ })
if ($modes.Count -gt 1 -or ($Repair -and $modes.Count -gt 0) -or ([bool]$Update -ne [bool]$MakeLatest))
{
    [Console]::Error.WriteLine('Use one of: no mode (check, optional -Repair), -Expect <tag>, -Promote <tag>, -Update <tag> -MakeLatest true|false.')
    exit 2
}

foreach ($tag in @($Expect, $Promote, $Update | Where-Object { $_ }))
{
    if ($null -eq (ConvertTo-ReleaseVersion $tag))
    {
        [Console]::Error.WriteLine("'$tag' is not a numeric v<x.y.z> tag.")
        exit 2
    }
}

if ($Expect)
{
    $latest = Get-LatestSource
    $check = Test-ExpectedLatest $latest $Expect
    $result = @{ mode = 'expect'; expected = $Expect; actual = $check.Actual }
    if (-not $check.Ok)
    {
        Stop-Failed $check.Reason $result
    }

    Write-Host $check.Reason
    $result['status'] = 'passed'
    Write-Result $result
    exit 0
}

if ($Update)
{
    if ($ReleasesFile)
    {
        $release = @(Get-Content -LiteralPath $ReleasesFile -Raw | ConvertFrom-Json) | Where-Object { $_.tag_name -ceq $Update } | Select-Object -First 1
    }
    else
    {
        $release = Invoke-GhApi @("repos/$Repository/releases/tags/$Update")
    }

    $result = @{ mode = 'update'; tag = $Update; makeLatest = $MakeLatest }
    if ($null -eq $release)
    {
        Stop-Failed "Release $Update was not found." $result
    }

    $before = [string](Get-ModelProperty $release 'body' '')
    Invoke-ReleaseUpdate $release $MakeLatest $before
    if (-not ($ReleasesFile -or $LatestFile))
    {
        $after = Invoke-GhApi @("repos/$Repository/releases/tags/$Update")
        if ($null -eq $after -or [string](Get-ModelProperty $after 'body' '') -cne $before)
        {
            Stop-Failed "The update of $Update with make_latest=$MakeLatest changed the release body." $result
        }
    }

    Write-Host "Updated $Update with make_latest=$MakeLatest; body unchanged."
    $result['status'] = 'passed'
    Write-Result $result
    exit 0
}

$releases = Get-ReleasesSource

if ($Promote)
{
    $promotion = Get-PromotionDecision $releases $Promote
    $result = @{ mode = 'promote'; tag = $Promote; promoted = $promotion.Promote; reason = $promotion.Reason }
    if ($promotion.Kind -eq 'unknown')
    {
        Stop-Failed "Cannot promote: $($promotion.Reason) (it is missing, a draft or prerelease, or its installer does not match its tag)." $result
    }

    if ($promotion.Promote)
    {
        Invoke-ReleaseUpdate $promotion.Release 'true'
        Write-Host "Promoted $Promote to Latest: $($promotion.Reason)."
    }
    else
    {
        Write-Host "::notice::Not promoting $Promote`: $($promotion.Reason)."
    }

    $result['status'] = 'passed'
    Write-Result $result
    exit 0
}

$latest = Get-LatestSource
$decision = Get-LatestDecision $releases $latest
$result = @{ mode = 'check'; decision = $decision.Decision; latest = $(if ($null -eq $latest) { '' } else { [string]$latest.tag_name }); highestDesktop = $decision.Highest; repaired = $false; reason = $decision.Reason }
if ($decision.Decision -eq 'ok')
{
    Write-Host "Latest OK: $($decision.Reason)"
    $result['status'] = 'passed'
    Write-Result $result
    exit 0
}

$outcome = 'Run with -Repair to fix it.'
if ($Repair -and $null -ne $decision.Target)
{
    Invoke-ReleaseUpdate $decision.Target 'true'
    $result['repaired'] = $true
    $outcome = "Re-marked $($decision.Target.tag_name) as Latest."
}
elseif ($Repair)
{
    $outcome = 'It could not be repaired.'
}

Stop-Failed "Wrong Latest release: $($decision.Reason). $outcome" $result
