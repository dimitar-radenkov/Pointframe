#Requires -Version 7.0
<#
Shared release model, dot-sourced by release-impact.ps1, check-latest-release.ps1, write-release-receipt.ps1 and
check-release-channels.ps1. It has no parameters and does nothing when dot-sourced; every function is pure (it takes
parsed JSON and decides, no gh, no network, no git).

A release is a GitHub REST release object: tag_name, draft, prerelease, published_at, assets (objects with a name, or
plain names in fixtures). Two trains are recognised by the assets a release carries, never by its tag:

  desktop  published, numeric v<x.y.z> tag, and Pointframe-<x.y.z>-x64-Setup.exe with the version equal to the tag.
           This is what an installed GitHubUpdateService needs from releases/latest.
  agent    published, numeric v<x.y.z> tag, Pointframe.Cli-<v>-win-x64.zip, its .sha256 and Pointframe.Mcp-<v>-win-x64.mcpb.

A combined release (a merge that selects both trains) is both. Drafts, prereleases, non-numeric tags and incomplete releases are neither. Order
is the numeric version, never creation order.
#>

function Get-ModelProperty
{
    param($Object, [string]$Name, $Default = $null)

    if ($null -eq $Object)
    {
        return $Default
    }

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property -or $null -eq $property.Value)
    {
        return $Default
    }

    return $property.Value
}

function ConvertTo-ReleaseVersion
{
    # The version of a numeric v<x.y.z> tag, or $null for anything else.
    param([string]$Tag)

    if ($Tag -cmatch '^v(\d{1,9})\.(\d{1,9})\.(\d{1,9})$')
    {
        return [version]"$($Matches[1]).$($Matches[2]).$($Matches[3])"
    }

    return $null
}

function Get-ReleaseAssetNames
{
    param($Release)

    $names = foreach ($asset in @(Get-ModelProperty $Release 'assets' @()))
    {
        if ($asset -is [string])
        {
            $asset
        }
        else
        {
            [string](Get-ModelProperty $asset 'name' '')
        }
    }

    return @($names | Where-Object { $_ })
}

function Test-ReleasePublished
{
    param($Release)

    if ([bool](Get-ModelProperty $Release 'draft' $false) -or [bool](Get-ModelProperty $Release 'prerelease' $false))
    {
        return $false
    }

    return -not [string]::IsNullOrWhiteSpace([string](Get-ModelProperty $Release 'published_at' ''))
}

function Get-DesktopAssetNames
{
    param([string]$Version)

    return @("Pointframe-$Version-x64-Setup.exe")
}

function Get-DesktopAliasAssetNames
{
    param([string]$Version)

    return @(
        'Pointframe.Cli-win-x64.zip'
        'Pointframe.Cli-win-x64.zip.sha256'
        'Pointframe.Mcp-win-x64.mcpb'
        'Pointframe.Mcp-win-x64.mcpb.sha256'
    )
}

function Get-AgentRequiredAssetNames
{
    param([string]$Version)

    return @(
        "Pointframe.Cli-$Version-win-x64.zip"
        "Pointframe.Cli-$Version-win-x64.zip.sha256"
        "Pointframe.Mcp-$Version-win-x64.mcpb"
    )
}

function Get-TrainAssetNames
{
    param([string]$Version, [string[]]$Trains)

    $names = [System.Collections.Generic.List[string]]::new()
    foreach ($train in @('desktop', 'agent'))
    {
        if ($train -notin $Trains) { continue }
        $assets = if ($train -eq 'desktop') { @(Get-DesktopAssetNames $Version) + @(Get-DesktopAliasAssetNames $Version) } else {
            @(
                (Get-AgentRequiredAssetNames $Version)
                "Pointframe.Mcp-$Version-win-x64.mcpb.sha256"
                "Pointframe.Mcp-$Version-win-x64.server.json"
            )
        }
        foreach ($asset in $assets) { if ($asset -notin $names) { $names.Add($asset) } }
    }
    return $names.ToArray()
}

function Test-TrainRelease
{
    param($Release, [ValidateSet('desktop', 'agent')][string]$Train)

    if ($null -eq $Release -or -not (Test-ReleasePublished $Release))
    {
        return $false
    }

    $version = ConvertTo-ReleaseVersion ([string](Get-ModelProperty $Release 'tag_name' ''))
    if ($null -eq $version)
    {
        return $false
    }

    $names = @(Get-ReleaseAssetNames $Release)
    $required = if ($Train -eq 'desktop') { Get-DesktopAssetNames $version.ToString() } else { Get-AgentRequiredAssetNames $version.ToString() }
    foreach ($name in $required)
    {
        if ($name -cnotin $names)
        {
            return $false
        }
    }

    return $true
}

function Get-TrainReleases
{
    # Complete releases of the train, highest version first.
    param([object[]]$Releases, [ValidateSet('desktop', 'agent')][string]$Train)

    $complete = foreach ($release in @($Releases))
    {
        if (Test-TrainRelease $release $Train)
        {
            [pscustomobject]@{
                Version = ConvertTo-ReleaseVersion ([string]$release.tag_name)
                Release = $release
            }
        }
    }

    return @($complete | Sort-Object -Property Version -Descending)
}

function Get-TrainBaseline
{
    # The last successful release of the train: the highest complete one, or $null when there is none.
    param([object[]]$Releases, [ValidateSet('desktop', 'agent')][string]$Train)

    $complete = @(Get-TrainReleases $Releases $Train)
    if ($complete.Count -eq 0)
    {
        return $null
    }

    return $complete[0].Release
}

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

function Get-PreviousTrainRelease
{
    # The highest complete release of the train below Before, or $null.
    param([object[]]$Releases, [ValidateSet('desktop', 'agent')][string]$Train, [version]$Before)

    $older = @(Get-TrainReleases $Releases $Train | Where-Object { $_.Version -lt $Before })
    if ($older.Count -eq 0)
    {
        return $null
    }

    return $older[0].Release
}

function Get-ReleaseFileList
{
    # Shared paths, names and train ownership for all release consumers.
    param([string]$WorkflowPath, [string]$Version, [string[]]$Trains = @('desktop', 'agent'))

    $files = [System.Collections.Generic.List[object]]::new()
    foreach ($name in Get-TrainAssetNames $Version $Trains)
    {
        $train = if ($name -in (@(Get-DesktopAssetNames $Version) + @(Get-DesktopAliasAssetNames $Version))) { 'desktop' } else { 'agent' }
        $directory = if ($train -eq 'desktop' -and $name -like 'Pointframe-*-Setup.exe') { 'installer/output' } else { 'packaging/output' }
        $files.Add([pscustomobject]@{ Path = (Join-Path $directory $name).Replace('\', '/'); Name = $name; Train = $train })
    }

    return $files.ToArray()
}

function Test-ReleaseReceipt
{
    # Problems with a release receipt (schema 1), as strings; empty when it is valid.
    param($Receipt)

    $problems = [System.Collections.Generic.List[string]]::new()
    if ($null -eq $Receipt -or [int](Get-ModelProperty $Receipt 'schemaVersion' 0) -ne 1)
    {
        $problems.Add('schemaVersion must be 1')
        return $problems.ToArray()
    }

    $trains = Get-ModelProperty $Receipt 'trains' $null
    foreach ($name in @('desktop', 'agent'))
    {
        $train = Get-ModelProperty $trains $name $null
        if ($null -eq $train -or $null -eq $train.PSObject.Properties['selected'] -or $train.selected -isnot [bool])
        {
            $problems.Add("trains.$name.selected must be true or false")
            continue
        }

        if (-not $train.selected)
        {
            continue
        }

        $version = [string](Get-ModelProperty $train 'version' '')
        if ($version -notmatch '^\d+\.\d+\.\d+$')
        {
            $problems.Add("trains.$name.version must be x.y.z")
        }
        elseif ([string](Get-ModelProperty $train 'tag' '') -cne "v$version")
        {
            $problems.Add("trains.$name.tag must be v$version")
        }

        if ([string](Get-ModelProperty $train 'commit' '') -notmatch '^[0-9a-f]{40}$')
        {
            $problems.Add("trains.$name.commit must be a 40-character SHA")
        }

        if ([string](Get-ModelProperty $train 'publication' '') -notin @('published', 'failed', 'draft', 'skipped'))
        {
            $problems.Add("trains.$name.publication must be published, failed or draft")
        }

        $assets = @(Get-ModelProperty $train 'assets' @())
        if ($assets.Count -eq 0)
        {
            $problems.Add("trains.$name.assets must list the train's assets")
        }

        foreach ($asset in $assets)
        {
            $sha = Get-ModelProperty $asset 'sha256' $null
            if ([string]::IsNullOrEmpty([string](Get-ModelProperty $asset 'name' '')) -or ($null -ne $sha -and "$sha" -notmatch '^[0-9a-f]{64}$'))
            {
                $problems.Add("trains.$name.assets entries need a name and a sha256 (64 lowercase hex characters, or null when unknown)")
                break
            }
        }
    }

    return $problems.ToArray()
}
