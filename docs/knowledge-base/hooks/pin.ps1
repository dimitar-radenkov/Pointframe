#Requires -Version 7.0
<#
hooks.pin of agent-workflow.json: the Claude plugin pin adapter of merge-pr.ps1. After every release the next pull request
must move the pin (the two files in pinException.paths), and the pin-only change needs no second verify.ps1. Moved from the
functions Update-PluginPin and Test-PluginPinBehind and the discovery check of Test-MergeReceipt in scripts/merge-pr.ps1.

  pin.ps1 -Action Behind   -Root <worktree>   exit 0 the pin is current, 10 a newer release exists, 2 error (last line says why)
  pin.ps1 -Action Update   -Root <worktree>   exit 0 already current, 10 pinned and committed, 1 failed (printed), 2 error
  pin.ps1 -Action Validate -Root <worktree>   exit 0 the pin files pass the offline discovery check, 1 not (last line says why)

The kit runs it in a child process with the worktree as the working folder. Standard output lines are shown to the user as is.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Behind', 'Update', 'Validate')]
    [string]$Action,
    [Parameter(Mandatory)]
    [string]$Root
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest

function Update-PluginPin
{
    param([string]$WorktreePath)

    $root = (Resolve-Path -LiteralPath $WorktreePath).Path
    $lockPath = Join-Path $root 'plugin' 'pointframe' 'server.lock.json'
    $pinScript = Join-Path $root 'scripts' 'update-plugin-pin.ps1'
    if (-not (Test-Path -LiteralPath $lockPath) -or -not (Test-Path -LiteralPath $pinScript))
    {
        throw "Plugin pin files are missing from '$root'."
    }

    $latest = (gh release view --json tagName -q .tagName 2>$null)
    if ($LASTEXITCODE -ne 0 -or -not $latest)
    {
        throw 'Plugin pin: could not read the latest release.'
    }

    $latest = $latest.Trim() -replace '^v', ''
    $pinned = (Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json).version
    if ($pinned -eq $latest)
    {
        Write-Host "Plugin pin: already v$latest"
        return $false
    }

    Write-Host "Plugin pin: v$pinned -> v$latest."
    & pwsh -NoProfile -File $pinScript -Version $latest | Out-Host
    if ($LASTEXITCODE -ne 0)
    {
        Write-Host "ERROR plugin pin update failed (exit $LASTEXITCODE); not enabling auto-merge."
        exit 1
    }

    git -C $root add -- plugin/pointframe/server.lock.json plugin/pointframe/.claude-plugin/plugin.json
    git -C $root commit -q -m "Pin the Claude plugin to v$latest" -- plugin/pointframe/server.lock.json plugin/pointframe/.claude-plugin/plugin.json
    if ($LASTEXITCODE -ne 0)
    {
        Write-Host 'ERROR committing the plugin pin failed.'
        exit 1
    }
    $commit = (git -C $root rev-parse HEAD).Trim()
    Write-Host "Plugin pin: committed $commit"
    return $true
}

function Test-PluginPinBehind([string]$WorktreePath)
{
    $root = (Resolve-Path -LiteralPath $WorktreePath).Path
    $lockPath = Join-Path $root 'plugin' 'pointframe' 'server.lock.json'
    if (-not (Test-Path -LiteralPath $lockPath)) { return $false }
    $latest = (& gh release view --json tagName -q .tagName 2>$null)
    if ($LASTEXITCODE -ne 0 -or -not $latest) { throw 'Plugin pin: could not read the latest release.' }
    $latest = $latest.Trim() -replace '^v', ''
    $pinned = [string](Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json).version
    return ([version]$pinned -lt [version]$latest)
}

try
{
    switch ($Action)
    {
        'Behind'
        {
            exit $(if (Test-PluginPinBehind $Root) { 10 } else { 0 })
        }
        'Update'
        {
            exit $(if (Update-PluginPin $Root) { 10 } else { 0 })
        }
        'Validate'
        {
            $resolved = (Resolve-Path -LiteralPath $Root).Path
            # Both pin files (pinException.paths) must parse to JSON objects; a null, an array or a string is refused here, whatever the
            # discovery check below says about them.
            foreach ($pinFile in @('plugin/pointframe/server.lock.json', 'plugin/pointframe/.claude-plugin/plugin.json'))
            {
                $pinPath = Join-Path $resolved $pinFile
                if (-not (Test-Path -LiteralPath $pinPath -PathType Leaf)) { Write-Output "$pinFile is missing"; exit 1 }
                try { $pinDocument = Get-Content -LiteralPath $pinPath -Raw | ConvertFrom-Json -NoEnumerate -AsHashtable -ErrorAction Stop }
                catch { Write-Output "$pinFile is not valid JSON"; exit 1 }
                if ($pinDocument -isnot [hashtable]) { Write-Output "$pinFile is not a JSON object"; exit 1 }
            }
            $discovery = & pwsh -NoProfile -NonInteractive -File (Join-Path $resolved 'scripts' 'check-agent-discovery.ps1') 2>&1
            if ($LASTEXITCODE -ne 0)
            {
                Write-Output "check-agent-discovery.ps1 failed: $(@($discovery)[-1])"
                exit 1
            }
            Write-Output 'discovery check passed'
            exit 0
        }
    }
}
catch
{
    Write-Output $_.Exception.Message
    exit 2
}
