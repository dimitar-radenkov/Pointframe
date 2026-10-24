#Requires -Version 7.0
<#
hooks.channels of agent-workflow.json: the release channel report after a package ship's merge, run by the kit's package.ps1 (ship, step
channels). Moved from Step-ShipChannels of scripts/package.ps1; it calls scripts/check-release-channels.ps1 for the release the CD cut.

  channels.ps1 -Root <main checkout> -Version <x.y.z> -MergeCommit <sha> -OutDirectory <folder> [-Watch -TimeoutMinutes <n> -PollSeconds <n>]
      Fetches the CD release-plan receipt for MergeCommit when available, then passes the lines of check-release-channels.ps1 through ("CHANNEL <name>: <state> - <detail>" per channel, "CHANNELS: <final>") and
      exits with its code: 0 every channel ok, 1 a channel failed, 3 pending. After them it prints a NOTE line when the Claude plugin pin
      is not yet on this release. The kit reports failed channels and never opens a pin pull request.

The kit runs it in a child process with the main checkout as the working folder. A null hook means the repository reports no channels.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Root,
    [Parameter(Mandatory)]
    [string]$Version,
    [string]$MergeCommit,
    [Parameter(Mandatory)]
    [string]$OutDirectory,
    [switch]$Watch,
    [int]$TimeoutMinutes = 60,
    [int]$PollSeconds = 60
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest

try
{
    $script = Join-Path $Root 'scripts' 'check-release-channels.ps1'
    if (-not (Test-Path -LiteralPath $script -PathType Leaf)) { throw "the release channel check does not exist: $script" }
    $receipt = $null
    if ($MergeCommit -match '^[0-9a-f]{40}$')
    {
        try
        {
            $runsOutput = @(& gh run list --repo 'dimitar-radenkov/Pointframe' --workflow CD --commit $MergeCommit --json databaseId,status,conclusion --limit 20 2>$null)
            if ($LASTEXITCODE -eq 0 -and $runsOutput.Count -gt 0)
            {
                $runs = ($runsOutput -join "`n") | ConvertFrom-Json
                $run = @($runs | Where-Object { $_.status -eq 'completed' -and $_.conclusion -eq 'success' } | Select-Object -First 1)
                if ($run.Count -gt 0)
                {
                    $downloadDirectory = Join-Path $OutDirectory 'cd-receipt'
                    New-Item -ItemType Directory -Path $downloadDirectory -Force | Out-Null
                    & gh run download $run[0].databaseId --repo 'dimitar-radenkov/Pointframe' --name release-plan --dir $downloadDirectory 2>$null
                    if ($LASTEXITCODE -eq 0)
                    {
                        $candidate = Join-Path $downloadDirectory 'release-plan.json'
                        if (Test-Path -LiteralPath $candidate -PathType Leaf) { $receipt = $candidate }
                    }
                }
            }
        }
        catch { $receipt = $null }
    }

    if ($receipt)
    {
        $arguments = @('-NoProfile', '-File', $script, '-Receipt', $receipt, '-OutDirectory', $OutDirectory)
    }
    else { $arguments = @('-NoProfile', '-File', $script, '-Version', $Version, '-OutDirectory', $OutDirectory) }
    if ($Watch) { $arguments += @('-Watch', '-TimeoutMinutes', [string]$TimeoutMinutes, '-PollSeconds', [string]$PollSeconds) }
    $lines = [Collections.Generic.List[string]]::new()
    & pwsh @arguments | ForEach-Object { $text = "$_"; $lines.Add($text); $text }
    $exit = $LASTEXITCODE
    $pin = @($lines | Where-Object { $_ -match '^CHANNEL plugin-pin: (\w+) - (.*)$' } | ForEach-Object { @{ state = $Matches[1]; detail = $Matches[2] } } | Select-Object -First 1)
    if ($pin.Count -and $pin[0].state -ne 'accepted')
    {
        Write-Output "NOTE the plugin pin is $($pin[0].state): $($pin[0].detail). The next merged PR moves it (merge-pr.ps1 -Auto); no pin PR is opened from here."
    }
    exit $exit
}
catch
{
    Write-Output $_.Exception.Message
    exit 2
}
