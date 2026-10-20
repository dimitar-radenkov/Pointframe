#Requires -Version 7.0
<#
hooks.environment of agent-workflow.json: the read-only environment probes `kb.ps1 init` prints. Each probe has a
2 second timeout and prints "unknown" when it fails, times out, or throws (KB_PROBE_FAULT=1 forces that). One line
per probe on standard output; the kit prints them under its Environment heading. -NoProbe prints that nothing was
probed. Moved from the probes that were part of scripts/kb.ps1.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Root,
    [switch]$NoProbe
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest

# One read-only probe with a 2 second timeout. Returns $null when it fails, times out, or throws: the caller prints "unknown".
function Invoke-Probe([scriptblock]$Block, [object[]]$Arguments = @())
{
    try
    {
        if ($env:KB_PROBE_FAULT)
        {
            throw 'probe fault'
        }
        if (Get-Command Start-ThreadJob -ErrorAction SilentlyContinue)
        {
            $job = Start-ThreadJob -ScriptBlock $Block -ArgumentList $Arguments
            if (-not (Wait-Job -Job $job -Timeout 2))
            {
                Stop-Job -Job $job
                Remove-Job -Job $job -Force
                return $null
            }
            $value = Receive-Job -Job $job -ErrorAction Stop
            Remove-Job -Job $job -Force
            return [pscustomobject]@{ Value = $value }
        }
        # No bounded way to run it here: never run a probe synchronously, report unknown.
        return $null
    }
    catch
    {
        return $null
    }
}

if ($NoProbe)
{
    foreach ($name in @('Pointframe.Mcp.exe', 'desktop lock', 'trial hidden installs', 'git worktrees'))
    {
        "- ${name}: not probed (-NoProbe)"
    }
    exit 0
}

$mcp = Invoke-Probe { @(Get-Process -Name 'Pointframe.Mcp' -ErrorAction SilentlyContinue | ForEach-Object { $_.Id }) }
$(if ($null -eq $mcp) { '- Pointframe.Mcp.exe: unknown' }
    elseif (@($mcp.Value).Count -gt 0) { "- Pointframe.Mcp.exe: running (pid $(@($mcp.Value) -join ', '))" }
    else { '- Pointframe.Mcp.exe: not running' })

$lockPath = Join-Path ([System.IO.Path]::GetTempPath()) 'pointframe-desktop.lock'
$lock = Invoke-Probe {
    param($Path)
    if (-not (Test-Path -LiteralPath $Path)) { return 'absent' }
    $content = ''
    try
    {
        $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
        try { $content = [System.IO.StreamReader]::new($stream).ReadToEnd() } finally { $stream.Dispose() }
    }
    catch
    {
        return 'present; content not readable'
    }
    $pidMatch = [regex]::Match($content, '(?i)(?:"?pid"?\s*[:=]\s*)?\b(\d{2,9})\b')
    if (-not $pidMatch.Success) { return 'present; no pid recorded' }
    $alive = [bool](Get-Process -Id ([int]$pidMatch.Groups[1].Value) -ErrorAction SilentlyContinue)
    return "present; records pid $($pidMatch.Groups[1].Value) ($(if ($alive) { 'alive' } else { 'not alive' }))"
} @($lockPath)
$(if ($null -eq $lock) { '- %TEMP%\pointframe-desktop.lock: unknown' } else { "- %TEMP%\pointframe-desktop.lock: $($lock.Value)" })

$localAppData = [Environment]::GetFolderPath('LocalApplicationData')
$hidden = Invoke-Probe {
    param($Local)
    $folders = @(Get-ChildItem -LiteralPath (Join-Path $Local 'Programs') -Directory -Filter '.pointframe-trial-hidden-*' -Force -ErrorAction SilentlyContinue).Count
    $markers = @(Get-ChildItem -Path (Join-Path $Local 'Pointframe\trial-hidden') -Filter '*.json' -File -ErrorAction SilentlyContinue).Count
    return "$folders hidden-install folder(s) in Programs, $markers marker file(s) in Pointframe\trial-hidden"
} @($localAppData)
$(if ($null -eq $hidden) { '- trial hidden installs: unknown' } else { "- trial hidden installs: $($hidden.Value)" })

$worktrees = Invoke-Probe {
    param($Repo)
    $out = & git -C $Repo worktree list 2>$null
    if ($LASTEXITCODE -ne 0) { throw 'git failed' }
    return @($out | Where-Object { $_ }).Count
} @($Root)
$(if ($null -eq $worktrees) { '- git worktrees: unknown' } else { "- git worktrees: $($worktrees.Value) listed by git worktree list" })
