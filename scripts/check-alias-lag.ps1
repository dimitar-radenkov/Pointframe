#Requires -Version 7.0
<#
Checks whether Latest's stable CLI alias points at the newest complete agent release. Prints exactly one JSON result;
alerts are data and exit 0, while a failed inspection exits 1. -SelfTest uses fixture responses through a fake gh seam.
#>
[CmdletBinding()]
param(
    [string]$Repository = 'dimitar-radenkov/Pointframe',
    [string]$Now = ([DateTime]::UtcNow.ToString('o')),
    [switch]$SelfTest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
. (Join-Path $PSScriptRoot 'release-model.ps1')

function Invoke-LagGh
{
    param([scriptblock]$Gh, [string[]]$Arguments)
    return & $Gh -Arguments $Arguments
}

function Read-LagReleasePages
{
    param([scriptblock]$Gh, [string]$Repository, [int]$MaxPages = 50)
    $all = [Collections.Generic.List[object]]::new()
    for ($page = 1; $page -le $MaxPages; $page++)
    {
        $response = Invoke-LagGh -Gh $Gh -Arguments @('api', "repos/$Repository/releases?per_page=100&page=$page")
        if ($null -eq $response) { throw "Could not read release listing page $page." }
        $items = @($response.data)
        foreach ($item in $items) { $all.Add($item) }
        if ($items.Count -lt 100) { return $all.ToArray() }
    }
    throw "More than $MaxPages pages of releases."
}

function Get-LagChecksum
{
    param([scriptblock]$Gh, [string]$Repository, [string]$Tag, [string]$Asset, [string]$Directory, [string]$ExpectedLeaf)
    $null = Invoke-LagGh -Gh $Gh -Arguments @('release', 'download', $Tag, '--pattern', $Asset, '--dir', $Directory, '--clobber')
    $path = Join-Path $Directory $Asset
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Could not download checksum asset $Asset from $Tag." }
    $line = (Get-Content -LiteralPath $path -Raw).Trim()
    if ($line -notmatch '^([0-9a-fA-F]{64})\s+\*?(.+)$') { throw "Invalid SHA-256 checksum asset $Asset on $Tag." }
    if ([IO.Path]::GetFileName($Matches[2]) -cne $ExpectedLeaf) { throw "Checksum $Asset on $Tag names '$($Matches[2])', expected '$ExpectedLeaf'." }
    return $Matches[1].ToLowerInvariant()
}

function Test-LagStrictAgentRelease($Release)
{
    if (-not (Test-ReleasePublished $Release)) { return $false }
    $version = ConvertTo-ReleaseVersion ([string](Get-ModelProperty $Release 'tag_name' ''))
    if ($null -eq $version) { return $false }
    $names = @(Get-ReleaseAssetNames $Release)
    foreach ($name in Get-TrainAssetNames $version.ToString() @('agent')) { if ($name -cnotin $names) { return $false } }
    return $true
}

function Test-ReleaseSecurityMark
{
    param([scriptblock]$Gh, [string]$Repository, $Release)
    $sha = [string](Get-ModelProperty $Release 'target_commitish' '')
    if ($sha -notmatch '^[0-9a-f]{40}$') { return $false }
    $response = Invoke-LagGh -Gh $Gh -Arguments @('api', "repos/$Repository/commits/$sha/pulls")
    if ($null -eq $response) { throw "Could not read merged PR labels for agent release commit $sha." }
    $pulls = @($response.data)
    return @($pulls | ForEach-Object { $_.labels } | Where-Object { $_.name -ceq 'security' }).Count -gt 0
}

function Get-AliasLagResult
{
    param([scriptblock]$Gh, [string]$Repository, [DateTime]$NowUtc, [string]$TempDirectory)
    $latestResponse = Invoke-LagGh -Gh $Gh -Arguments @('api', "repos/$Repository/releases/latest")
    if ($null -eq $latestResponse) { throw 'Could not read Latest release.' }
    $latest = $latestResponse.data
    if (-not (Test-TrainRelease $latest 'desktop')) { throw "Latest $($latest.tag_name) is not a qualified desktop release." }
    New-Item -ItemType Directory -Path $TempDirectory -Force | Out-Null
    $aliasHashFile = 'Pointframe.Cli-win-x64.zip.sha256'
    $aliasHash = Get-LagChecksum $Gh $Repository ([string]$latest.tag_name) $aliasHashFile $TempDirectory 'Pointframe.Cli-win-x64.zip'
    $releases = @(Read-LagReleasePages $Gh $Repository)
    $agentReleases = @($releases | Where-Object { Test-LagStrictAgentRelease $_ } | Sort-Object { ConvertTo-ReleaseVersion ([string]$_.tag_name) } -Descending)
    $source = $null
    $sourceVersion = $null
    foreach ($candidate in $agentReleases)
    {
        $version = (ConvertTo-ReleaseVersion ([string]$candidate.tag_name)).ToString()
        $asset = "Pointframe.Cli-$version-win-x64.zip.sha256"
        $hash = Get-LagChecksum $Gh $Repository ([string]$candidate.tag_name) $asset $TempDirectory "Pointframe.Cli-$version-win-x64.zip"
        if ($hash -ceq $aliasHash) { $source = $candidate; $sourceVersion = $version; break }
    }
    if (-not $source)
    {
        return [ordered]@{ status = 'alert'; sourceVersion = $null; newestAgentVersion = $(if ($agentReleases.Count) { (ConvertTo-ReleaseVersion ([string]$agentReleases[0].tag_name)).ToString() } else { $null }); oldestUnreflectedRelease = $null; ageHours = $null; thresholdHours = 168; reasons = @('Latest alias SHA-256 does not identify a complete published agent release.') }
    }
    $newestVersion = if ($agentReleases.Count) { (ConvertTo-ReleaseVersion ([string]$agentReleases[0].tag_name)).ToString() } else { $sourceVersion }
    $newer = @($agentReleases | Where-Object { (ConvertTo-ReleaseVersion ([string]$_.tag_name)) -gt [version]$sourceVersion } | Sort-Object { ConvertTo-ReleaseVersion ([string]$_.tag_name) })
    if ($newer.Count -eq 0)
    {
        return [ordered]@{ status = 'ok'; sourceVersion = $sourceVersion; newestAgentVersion = $newestVersion; oldestUnreflectedRelease = $null; ageHours = 0; thresholdHours = 168; reasons = @() }
    }
    $oldest = $newer[0]
    $oldestVersion = (ConvertTo-ReleaseVersion ([string]$oldest.tag_name)).ToString()
    $publishedText = [string](Get-ModelProperty $oldest 'published_at' '')
    if (-not $publishedText)
    {
        return [ordered]@{ status = 'alert'; sourceVersion = $sourceVersion; newestAgentVersion = $newestVersion; oldestUnreflectedRelease = $oldestVersion; ageHours = $null; thresholdHours = 168; reasons = @('The oldest unreflected agent release has no published_at timestamp.') }
    }
    $ageHours = [Math]::Max(0, ($NowUtc - [DateTime]::Parse($publishedText).ToUniversalTime()).TotalHours)
    $security = $false
    foreach ($release in $newer) { if (Test-ReleaseSecurityMark $Gh $Repository $release) { $security = $true } }
    $threshold = if ($security) { 24 } else { 168 }
    $reasons = @()
    if ($security) { $reasons += 'At least one unreflected agent release is marked security.' }
    if ($ageHours -ge $threshold) { $reasons += "Alias lag is at or above the $threshold-hour threshold." }
    $status = if ($ageHours -ge $threshold) { 'alert' } else { 'ok' }
    return [ordered]@{ status = $status; sourceVersion = $sourceVersion; newestAgentVersion = $newestVersion; oldestUnreflectedRelease = $oldestVersion; ageHours = [Math]::Round($ageHours, 2); thresholdHours = $threshold; reasons = $reasons }
}

function New-LagFakeGh
{
    param($Fixture, [string]$Directory)
    return {
        param([string[]]$Arguments)
        $global:LASTEXITCODE = 0
        if ($Arguments[0] -eq 'api' -and $Arguments[1] -match '/releases/latest$') { return [pscustomobject]@{ data = $Fixture.latest } }
        if ($Arguments[0] -eq 'api' -and $Arguments[1] -match '/releases\?')
        {
            $page = [int]([regex]::Match($Arguments[1], '[?&]page=(\d+)').Groups[1].Value)
            if ($page -le $Fixture.pages.Count) { $pageItems = [object[]]$Fixture.pages[$page - 1]; return [pscustomobject]@{ data = $pageItems } }
            return [pscustomobject]@{ data = @() }
        }
        if ($Arguments[0] -eq 'api' -and $Arguments[1] -match '/commits/([0-9a-f]{40})/pulls$')
        {
            $sha = $Matches[1]
            if ($Fixture.security.ContainsKey($sha)) { return [pscustomobject]@{ data = @($Fixture.security[$sha]) } }
            return [pscustomobject]@{ data = @() }
        }
        if ($Arguments[0] -eq 'release' -and $Arguments[1] -eq 'download')
        {
            $asset = $Arguments[[Array]::IndexOf($Arguments, '--pattern') + 1]
            $tag = $Arguments[2]
            $key = "$tag/$asset"
            if (-not $Fixture.checksums.ContainsKey($key)) { $global:LASTEXITCODE = 1; return $null }
            $directoryArg = $Arguments[[Array]::IndexOf($Arguments, '--dir') + 1]
            New-Item -ItemType Directory -Path $directoryArg -Force | Out-Null
            Set-Content -LiteralPath (Join-Path $directoryArg $asset) -Value $Fixture.checksums[$key] -NoNewline
            return $null
        }
        return $null
    }.GetNewClosure()
}

function New-LagFixtureModel
{
    param($Case)
    $now = [DateTime]::Parse($Case.now).ToUniversalTime()
    $sourceHash = ('a' * 63) + '1'
    $unmatchedHash = ('b' * 63) + '2'
    $source = [pscustomobject]@{
        tag_name = 'v1.0.0'; draft = $false; prerelease = $false; published_at = '2026-10-01T12:00:00Z'; target_commitish = ('a' * 40)
        assets = @(Get-TrainAssetNames '1.0.0' @('agent') | ForEach-Object { [pscustomobject]@{ name = $_ } })
    }
    $newerAt = switch ($Case.case)
    {
        'lag-six-days' { $now.AddDays(-6) }
        'lag-eight-days' { $now.AddDays(-8) }
        'security-25-hours' { $now.AddHours(-25) }
        'security-23-hours' { $now.AddHours(-23) }
        'pagination' { $now.AddDays(-8) }
        default { $now.AddDays(-8) }
    }
    $newer = [pscustomobject]@{
        tag_name = 'v1.1.0'; draft = $false; prerelease = $false; published_at = $newerAt.ToString('yyyy-MM-ddTHH:mm:ssZ'); target_commitish = ('b' * 40)
        assets = @(Get-TrainAssetNames '1.1.0' @('agent') | ForEach-Object { [pscustomobject]@{ name = $_ } })
    }
    $latest = [pscustomobject]@{ tag_name = 'v2.0.0'; draft = $false; prerelease = $false; published_at = '2026-10-20T12:00:00Z'; assets = @([pscustomobject]@{ name = 'Pointframe-2.0.0-x64-Setup.exe' }) }
    $aliasHash = if ($Case.case -eq 'source-unidentifiable') { $unmatchedHash } else { $sourceHash }
    $checksums = @{
        'v2.0.0/Pointframe.Cli-win-x64.zip.sha256' = "$aliasHash  Pointframe.Cli-win-x64.zip"
        'v1.1.0/Pointframe.Cli-1.1.0-win-x64.zip.sha256' = "$(('c' * 63) + '3')  Pointframe.Cli-1.1.0-win-x64.zip"
        'v1.0.0/Pointframe.Cli-1.0.0-win-x64.zip.sha256' = "$sourceHash  Pointframe.Cli-1.0.0-win-x64.zip"
    }
    $security = @{}
    if ($Case.case -in @('security-25-hours', 'security-23-hours')) { $security[('b' * 40)] = @([pscustomobject]@{ labels = @([pscustomobject]@{ name = 'security' }) }) }
    $pageList = [Collections.Generic.List[object]]::new()
    if ($Case.case -eq 'aliases-current') { $pageList.Add([object[]]@($source)) }
    elseif ($Case.case -eq 'pagination')
    {
        $filler = @((1..100 | ForEach-Object { [pscustomobject]@{ tag_name = "draft-$($_)"; draft = $true; prerelease = $false; published_at = '' ; assets = @() } }))
        $pageList.Add([object[]]$filler)
        $pageList.Add([object[]]@($newer, $source))
    }
    else { $pageList.Add([object[]]@($newer, $source)) }
    return [ordered]@{ latest = $latest; pages = $pageList.ToArray(); checksums = $checksums; security = $security }
}

function Invoke-LagSelfTest
{
    $fixturesPath = Join-Path $PSScriptRoot 'tests/alias-lag'
    $fixtures = @(Get-ChildItem -LiteralPath $fixturesPath -Filter '*.json' | Sort-Object Name)
    if ($fixtures.Count -lt 7) { throw 'Alias lag fixtures are missing.' }
    $cases = 0
    foreach ($file in $fixtures)
    {
        $case = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
            $fixture = New-LagFixtureModel $case
        $root = Join-Path ([IO.Path]::GetTempPath()) "alias-lag-$([guid]::NewGuid().ToString('N'))"
        try
        {
            $gh = New-LagFakeGh $fixture $root
            $result = Get-AliasLagResult $gh 'o/r' ([DateTime]::Parse($case.now).ToUniversalTime()) $root
            if ($result.status -cne $case.expectedStatus -or $result.sourceVersion -cne $case.expectedSource -or $result.thresholdHours -ne $case.expectedThreshold)
            {
                throw "$($file.Name): expected $($case.expectedStatus)/$($case.expectedSource)/$($case.expectedThreshold), got $($result | ConvertTo-Json -Compress -Depth 6)."
            }
            $expectedOldest = Get-ModelProperty $case 'expectedOldest' $null
            $expectedAgeMax = Get-ModelProperty $case 'expectedAgeMax' $null
            $expectedPages = Get-ModelProperty $case 'expectedPages' $null
            if ($expectedOldest -and $result.oldestUnreflectedRelease -cne $expectedOldest) { throw "$($file.Name): wrong oldest unreflected release." }
            if ($expectedAgeMax -and $result.ageHours -gt $expectedAgeMax) { throw "$($file.Name): age exceeded expected maximum." }
            if ($expectedPages -and @($fixture.pages).Count -ne $expectedPages) { throw "$($file.Name): pagination fixture did not include two pages." }
            $cases++
        }
        finally { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue }
    }
    Write-Host "check-alias-lag self-test passed: $cases fixture(s), paging, source identification, and 24 h/7 day thresholds."
    return 0
}

if ($SelfTest) { exit (Invoke-LagSelfTest) }
try
{
    $nowUtc = [DateTime]::Parse($Now).ToUniversalTime()
    $root = Join-Path ([IO.Path]::GetTempPath()) "alias-lag-live-$([guid]::NewGuid().ToString('N'))"
    $gh = {
        param([string[]]$Arguments)
        $output = @(& gh @Arguments 2>&1)
        if ($LASTEXITCODE -ne 0) { throw "gh $($Arguments -join ' ') failed: $($output -join ' ')" }
        if ($Arguments[0] -eq 'api')
        {
            $text = ($output -join "`n").Trim()
            if (-not $text) { throw "gh api $($Arguments[1]) returned no JSON." }
            return [pscustomobject]@{ data = ($text | ConvertFrom-Json) }
        }
        return $null
    }
    $result = Get-AliasLagResult $gh $Repository $nowUtc $root
    $result | ConvertTo-Json -Compress -Depth 8 | Write-Output
    exit 0
}
catch
{
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
