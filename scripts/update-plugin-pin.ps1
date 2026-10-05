#Requires -Version 7.0
<#
.SYNOPSIS
Pins the Claude plugin (plugin/pointframe) to one published Pointframe release.

.DESCRIPTION
Downloads Pointframe.Mcp-<Version>-win-x64.mcpb from the GitHub release, checks it against the release's own .sha256 asset,
and rewrites plugin/pointframe/server.lock.json and the "version" in plugin/pointframe/.claude-plugin/plugin.json. Nothing
else is touched. Prints "changed=true" or "changed=false" and, when GITHUB_OUTPUT is set, writes the same line there.

  pwsh scripts/update-plugin-pin.ps1 -Version 6.7.63
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9]+(\.[0-9]+){2,3}$')]
    [string]$Version,

    [string]$Repository = 'dimitar-radenkov/Pointframe'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$pluginRoot = Join-Path (Split-Path $PSScriptRoot -Parent) 'plugin' 'pointframe'
$lockPath = Join-Path $pluginRoot 'server.lock.json'
$manifestPath = Join-Path $pluginRoot '.claude-plugin' 'plugin.json'

$asset = "Pointframe.Mcp-$Version-win-x64.mcpb"
$url = "https://github.com/$Repository/releases/download/v$Version/$asset"
$work = Join-Path ([IO.Path]::GetTempPath()) "plugin-pin-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $work | Out-Null
try
{
    $archive = Join-Path $work $asset
    Invoke-WebRequest -Uri $url -OutFile $archive
    $actual = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()

    $checksumText = (Invoke-WebRequest -Uri "$url.sha256").Content
    if ($checksumText -is [byte[]])
    {
        $checksumText = [Text.Encoding]::UTF8.GetString($checksumText)
    }
    $published = (([string]$checksumText).Trim() -split '\s+')[0].ToLowerInvariant()
    if ($published -ne $actual)
    {
        throw "The release's $asset.sha256 says $published but the downloaded archive hashes to $actual."
    }
}
finally
{
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}

$lock = @"
{
  "schemaVersion": 1,
  "version": "$Version",
  "asset": "$asset",
  "url": "$url",
  "sha256": "$actual"
}

"@
$lock = $lock.Replace("`r`n", "`n").Replace("`n", "`r`n")
$manifest = [IO.File]::ReadAllText($manifestPath)
$updatedManifest = [regex]::Replace($manifest, '("version"\s*:\s*")[^"]*(")', "`${1}$Version`${2}", 1)

$changed = ([IO.File]::ReadAllText($lockPath) -ne $lock) -or ($manifest -ne $updatedManifest)
if ($changed)
{
    $utf8 = New-Object Text.UTF8Encoding($false)
    [IO.File]::WriteAllText($lockPath, $lock, $utf8)
    [IO.File]::WriteAllText($manifestPath, $updatedManifest, $utf8)
}

Write-Host "changed=$($changed.ToString().ToLowerInvariant())"
if ($env:GITHUB_OUTPUT)
{
    "changed=$($changed.ToString().ToLowerInvariant())" >> $env:GITHUB_OUTPUT
}
