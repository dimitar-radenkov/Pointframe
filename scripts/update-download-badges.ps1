#Requires -Version 7.0
<#
Writes shields.io endpoint badges with download totals across ALL GitHub releases.
The stock shields "github/downloads/<repo>/total" badge reads only the newest 100 releases, and the
"latest/<asset>" badge only the newest release, so both undercount for a repo that releases on every merge.
The Pages workflow runs this at build time; nothing is committed. -SelfTest checks the counting offline.
#>
[CmdletBinding()]
param(
    [string]$Repository = 'dimitar-radenkov/Pointframe',
    [string]$OutDir = (Join-Path $PSScriptRoot '..' 'website' 'badges'),
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-DownloadTotals
{
    param([Parameter(Mandatory)][object[]]$Releases)

    $totals = [ordered]@{ total = 0L; mcp = 0L; cli = 0L }
    foreach ($release in $Releases)
    {
        foreach ($asset in @($release.assets))
        {
            $count = [long]$asset.download_count
            $totals.total += $count
            if ($asset.name -like 'Pointframe.Mcp-*.mcpb')
            {
                $totals.mcp += $count
            }
            elseif ($asset.name -like 'Pointframe.Cli-*.zip')
            {
                $totals.cli += $count
            }
        }
    }

    $totals
}

function Format-Count
{
    param([Parameter(Mandatory)][long]$Count)

    if ($Count -ge 1000)
    {
        $thousands = [math]::Floor($Count / 100) / 10
        return ('{0}k' -f $thousands.ToString([Globalization.CultureInfo]::InvariantCulture))
    }

    $Count.ToString([Globalization.CultureInfo]::InvariantCulture)
}

function New-Badge
{
    param(
        [Parameter(Mandatory)][string]$Label,
        [Parameter(Mandatory)][long]$Count
    )

    [ordered]@{
        schemaVersion = 1
        label = $Label
        message = Format-Count $Count
        color = 'brightgreen'
    }
}

function Write-Badges
{
    param(
        [Parameter(Mandatory)][System.Collections.IDictionary]$Totals,
        [Parameter(Mandatory)][string]$Directory
    )

    New-Item -ItemType Directory -Force -Path $Directory | Out-Null
    $badges = [ordered]@{
        'downloads-total.json' = New-Badge 'downloads' $Totals.total
        'downloads-mcp.json' = New-Badge 'mcp downloads' $Totals.mcp
        'downloads-cli.json' = New-Badge 'cli downloads' $Totals.cli
    }
    foreach ($name in $badges.Keys)
    {
        $badges[$name] | ConvertTo-Json -Compress | Set-Content -Path (Join-Path $Directory $name) -Encoding utf8NoBOM
    }
}

function Invoke-SelfTest
{
    $failures = [System.Collections.Generic.List[string]]::new()
    $releases = @(
        [pscustomobject]@{ assets = @(
            [pscustomobject]@{ name = 'Pointframe-6.7.1-x64-Setup.exe'; download_count = 1200 }
            [pscustomobject]@{ name = 'Pointframe.Mcp-6.7.1-win-x64.mcpb'; download_count = 7 }
            [pscustomobject]@{ name = 'Pointframe.Mcp-win-x64.mcpb'; download_count = 3 }
            [pscustomobject]@{ name = 'Pointframe.Mcp-win-x64.mcpb.sha256'; download_count = 50 }
            [pscustomobject]@{ name = 'Pointframe.Cli-win-x64.zip'; download_count = 4 }
            [pscustomobject]@{ name = 'Pointframe.Cli-win-x64.zip.sha256'; download_count = 9 }
        ) }
        [pscustomobject]@{ assets = @() }
    )
    $totals = Get-DownloadTotals -Releases $releases
    $expected = @{ total = 1273L; mcp = 10L; cli = 4L }
    foreach ($key in $expected.Keys)
    {
        if ($totals[$key] -ne $expected[$key])
        {
            $failures.Add("$key was $($totals[$key]), expected $($expected[$key])")
        }
    }

    $formats = @{ 0L = '0'; 999L = '999'; 1000L = '1k'; 21484L = '21.4k' }
    foreach ($count in $formats.Keys)
    {
        $actual = Format-Count $count
        if ($actual -ne $formats[$count])
        {
            $failures.Add("Format-Count $count was '$actual', expected '$($formats[$count])'")
        }
    }

    if ($failures.Count -gt 0)
    {
        $failures | ForEach-Object { Write-Output "FAIL $_" }
        exit 1
    }

    Write-Output "Self-test passed: $($expected.Count + $formats.Count) case(s)."
    exit 0
}

if ($SelfTest)
{
    Invoke-SelfTest
}

# One compact JSON object per release per line, across every page.
$lines = gh api --paginate "repos/$Repository/releases?per_page=100" --jq '.[] | {assets: [.assets[] | {name, download_count}]}'
if ($LASTEXITCODE -ne 0)
{
    throw "gh api failed with exit code $LASTEXITCODE."
}

$releases = @($lines | Where-Object { $_ } | ForEach-Object { $_ | ConvertFrom-Json })
$totals = Get-DownloadTotals -Releases $releases
Write-Badges -Totals $totals -Directory $OutDir
Write-Output ("{0} releases: total {1}, mcp {2}, cli {3} -> {4}" -f $releases.Count, $totals.total, $totals.mcp, $totals.cli, $OutDir)
