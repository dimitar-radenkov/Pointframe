#Requires -Version 7.0
<#
Tracks every downstream channel of one release: the GitHub release, the winget-pkgs submissions, the MCP Registry
and the plugin pin. Run it after a release (CD cuts one for every merge to master).

  pwsh scripts/check-release-channels.ps1 -Version 6.7.87
  pwsh scripts/check-release-channels.ps1 -Version 6.7.87 -Watch -TimeoutMinutes 60 -PollSeconds 60
  pwsh scripts/check-release-channels.ps1 -SelfTest            # offline: state functions on scripts/tests/release-channels fixtures

Each channel has a state: pending, submitted, accepted, failed or skipped. One line per channel is printed:

  CHANNEL <name>: <state> - <detail>
  CHANNELS: ok|pending|failed

and result.json is written to -OutDirectory (default artifacts/release-channels/<version>/). With -Watch the script polls
until nothing is pending or the timeout passes, printing a HEARTBEAT line per poll so a watchdog can see it is alive.

Channels:
  github-release  release v<x> is published (not a draft) and has every asset CD uploads (read from cd.yml); a failed CD run
                  for the release commit is failed, a running one is pending.
  winget:<id>     one per package id in winget-release.yml. The Winget Release run for the release commit: failed -> failed
                  (with the failing step's error line), succeeded -> the winget-pkgs PR decides (open = submitted,
                  merged = accepted, none yet = pending, closed unmerged = failed).
  mcp-registry    registry.modelcontextprotocol.io has the version (accepted), or CD's registry publish step failed (failed).
  plugin-pin      plugin/pointframe/server.lock.json on master: == x accepted, == previous release pending (the next merged
                  PR pins it), older failed.

Exit codes: 0 every channel accepted or skipped, 1 any failed, 2 bad arguments, 3 something still pending or submitted.
#>
[CmdletBinding()]
param(
    [string]$Version,
    [switch]$Watch,
    [int]$TimeoutMinutes = 60,
    [int]$PollSeconds = 60,
    [string]$OutDirectory,
    [switch]$SelfTest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$script:RepositoryRoot = Split-Path $PSScriptRoot -Parent
$script:Repository = 'dimitar-radenkov/Pointframe'
$script:WingetRepository = 'microsoft/winget-pkgs'
$script:RegistryBase = 'https://registry.modelcontextprotocol.io/v0'
$script:RegistryPublishStep = 'Publish MCP server to the MCP Registry'

function New-Channel
{
    param([string]$Name, [string]$State, [string]$Detail)
    return [pscustomobject]@{ name = $Name; state = $State; detail = $Detail }
}

function Get-Opt
{
    # StrictMode makes reading a missing property an error; fixtures and gh output omit empty fields.
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

# ---------------------------------------------------------------------------------------------------------------------
# Pure state functions. They take parsed JSON and decide; they never call gh or the network.
# ---------------------------------------------------------------------------------------------------------------------

function Resolve-ReleaseChannel
{
    param($Release)

    $name = 'github-release'
    $cdConclusion = Get-Opt $Release 'cdConclusion' ''
    $cdStatus = Get-Opt $Release 'cdStatus' ''

    if ($cdConclusion -in @('failure', 'cancelled', 'timed_out'))
    {
        return New-Channel $name 'failed' "CD run finished as $cdConclusion"
    }

    if (-not $Release.exists)
    {
        if ($cdStatus -and $cdStatus -ne 'completed')
        {
            return New-Channel $name 'pending' "release does not exist yet; CD run is $cdStatus"
        }

        return New-Channel $name 'pending' 'release does not exist yet'
    }

    if ($Release.draft)
    {
        return New-Channel $name 'pending' 'release is still a draft'
    }

    $assets = @($Release.assets)
    $missing = @($Release.expectedAssets | Where-Object { $_ -notin $assets })
    if ($missing.Count -gt 0)
    {
        return New-Channel $name 'failed' "missing assets: $($missing -join ', ')"
    }

    return New-Channel $name 'accepted' "release is published with all $(@($Release.expectedAssets).Count) expected assets"
}

function Resolve-WingetChannel
{
    param($Package)

    $name = $Package.name
    $conclusion = Get-Opt $Package 'workflowConclusion' ''
    $prState = ([string](Get-Opt $Package 'prState' '')).ToLowerInvariant()

    if ($conclusion -in @('failure', 'timed_out', 'cancelled'))
    {
        $errorLine = Get-Opt $Package 'errorLine' ''
        if (-not $errorLine)
        {
            $errorLine = "Winget Release job $conclusion; no error line was found"
        }

        return New-Channel $name 'failed' $errorLine
    }

    if ($conclusion -in @('', 'queued', 'in_progress', 'waiting', 'pending', 'requested'))
    {
        return New-Channel $name 'pending' 'Winget Release has not finished'
    }

    if ($conclusion -eq 'skipped' -or (Get-Opt $Package 'submitSkipped' $false))
    {
        return New-Channel $name 'skipped' (Get-Opt $Package 'skipReason' 'Winget Release skipped this package')
    }

    switch ($prState)
    {
        'merged' { return New-Channel $name 'accepted' 'winget-pkgs PR merged' }
        'open' { return New-Channel $name 'submitted' 'winget-pkgs PR is open' }
        'closed' { return New-Channel $name 'failed' 'winget-pkgs PR was closed without merging' }
    }

    return New-Channel $name 'pending' 'Winget Release succeeded; waiting for the winget-pkgs PR'
}

function Resolve-RegistryChannel
{
    param($Registry)

    $name = 'mcp-registry'
    if ($Registry.publishFailed)
    {
        return New-Channel $name 'failed' (Get-Opt $Registry 'error' 'CD publish to the MCP Registry failed')
    }

    if ($Registry.versionPresent)
    {
        return New-Channel $name 'accepted' 'version is present in the MCP Registry'
    }

    $detail = Get-Opt $Registry 'error' ''
    if ($detail)
    {
        return New-Channel $name 'pending' "version is not present yet ($detail)"
    }

    return New-Channel $name 'pending' 'version is not present in the MCP Registry yet'
}

function Resolve-PluginChannel
{
    param($Pin)

    $name = 'plugin-pin'
    $current = [version]$Pin.current
    $target = [version]$Pin.version
    $previous = Get-Opt $Pin 'previous' ''

    if ($current -eq $target)
    {
        return New-Channel $name 'accepted' "server.lock.json pins $($Pin.current)"
    }

    if ($current -gt $target)
    {
        return New-Channel $name 'accepted' "server.lock.json pins $($Pin.current), newer than $($Pin.version)"
    }

    if ($previous -and $current -eq [version]$previous)
    {
        return New-Channel $name 'pending' "server.lock.json pins the previous release $($Pin.current); the next merged PR pins $($Pin.version)"
    }

    return New-Channel $name 'failed' "server.lock.json pins $($Pin.current), more than one release behind $($Pin.version)"
}

function Get-FinalState
{
    param($Channels)

    $states = @($Channels | ForEach-Object { $_.state })
    if ($states -contains 'failed')
    {
        return 'failed'
    }

    if ($states -contains 'pending' -or $states -contains 'submitted')
    {
        return 'pending'
    }

    return 'ok'
}

function Get-ErrorLine
{
    # Picks the line that says why a failed job failed from `gh run view --log-failed` output.
    param([string[]]$Log)

    $cleaned = foreach ($raw in $Log)
    {
        $line = $raw -replace '^[^\t]*\t[^\t]*\t\S+Z\s?', ''
        $line = $line -replace '\x1b\[[0-9;]*m', ''
        $line = $line -replace '\[\d+(;\d+)*m', ''
        $line.Trim()
    }

    $cleaned = @($cleaned | Where-Object { $_ })
    if ($cleaned.Count -eq 0)
    {
        return ''
    }

    $picked = $null
    foreach ($line in $cleaned)
    {
        # Rust eyre reports number their causes ("0: Ref cannot be created.", "1: failed to create branch ...");
        # the last one is the operation that failed.
        if ($line -match '^\d+:\s+(\S.*)$')
        {
            $picked = $Matches[1]
        }
    }

    if (-not $picked)
    {
        $annotated = @($cleaned | Where-Object { $_ -match '^##\[error\]' })
        if ($annotated.Count -gt 0)
        {
            $picked = $annotated[-1] -replace '^##\[error\]', ''
        }
    }

    if (-not $picked)
    {
        $picked = $cleaned[-1]
    }

    if ($picked.Length -gt 200)
    {
        $picked = $picked.Substring(0, 200)
    }

    return $picked
}

# ---------------------------------------------------------------------------------------------------------------------
# Repository facts read from the checkout, so the check follows the workflows instead of repeating their lists.
# ---------------------------------------------------------------------------------------------------------------------

function Get-WingetPackageIds
{
    param([string]$WorkflowPath)

    $ids = foreach ($line in Get-Content -LiteralPath $WorkflowPath)
    {
        if ($line -match '^\s*-\s*identifier:\s*(\S+)\s*$')
        {
            $Matches[1]
        }
    }

    return @($ids)
}

function Get-ExpectedAssets
{
    # The release asset leaf names listed under the Create GitHub Release step's `files:` block in cd.yml.
    param([string]$WorkflowPath, [string]$Version)

    $inFiles = $false
    $assets = [System.Collections.Generic.List[string]]::new()
    foreach ($line in Get-Content -LiteralPath $WorkflowPath)
    {
        if (-not $inFiles)
        {
            if ($line -match '^\s*files:\s*\|\s*$')
            {
                $inFiles = $true
            }

            continue
        }

        if ($line -match '^\s+(\S+/.*\S)\s*$')
        {
            $leaf = ($Matches[1] -split '/')[-1]
            $assets.Add($leaf.Replace('${{ steps.version.outputs.display }}', $Version))
            continue
        }

        break
    }

    return $assets.ToArray()
}

function Get-RegistryServerName
{
    param([string]$BuildScriptPath)

    foreach ($line in Get-Content -LiteralPath $BuildScriptPath)
    {
        if ($line -match '^\$serverName\s*=\s*"([^"]+)"')
        {
            return $Matches[1]
        }
    }

    throw "No `$serverName assignment found in $BuildScriptPath."
}

# ---------------------------------------------------------------------------------------------------------------------
# Self-test
# ---------------------------------------------------------------------------------------------------------------------

function Test-ChannelFixtures
{
    $fixtureDirectory = Join-Path $PSScriptRoot 'tests/release-channels'
    $files = @(Get-ChildItem -LiteralPath $fixtureDirectory -Filter '*.json' | Sort-Object Name)
    if ($files.Count -eq 0)
    {
        throw "No fixtures in $fixtureDirectory."
    }

    foreach ($file in $files)
    {
        $fixture = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
        switch ($fixture.kind)
        {
            'release' { $got = Resolve-ReleaseChannel $fixture.input }
            'winget' { $got = Resolve-WingetChannel $fixture.input }
            'registry' { $got = Resolve-RegistryChannel $fixture.input }
            'plugin' { $got = Resolve-PluginChannel $fixture.input }
            'errorline' { $got = New-Channel 'errorline' 'extracted' (Get-ErrorLine @($fixture.input.log)) }
            default { throw "$($file.Name): unknown fixture kind '$($fixture.kind)'." }
        }

        if ($got.state -ne $fixture.expected)
        {
            throw "$($file.Name): expected state '$($fixture.expected)', got '$($got.state)' ($($got.detail))."
        }

        $expectedDetail = Get-Opt $fixture 'expectedDetail' ''
        if ($expectedDetail -and $got.detail -notlike "*$expectedDetail*")
        {
            throw "$($file.Name): expected detail containing '$expectedDetail', got '$($got.detail)'."
        }
    }

    $rollup = @(
        @{ States = @('accepted', 'skipped'); Expected = 'ok' }
        @{ States = @('accepted', 'submitted'); Expected = 'pending' }
        @{ States = @('pending', 'failed', 'accepted'); Expected = 'failed' }
    )
    foreach ($case in $rollup)
    {
        $channels = @($case.States | ForEach-Object { New-Channel 'x' $_ '' })
        if ((Get-FinalState $channels) -ne $case.Expected)
        {
            throw "Roll-up of [$($case.States -join ', ')] was not '$($case.Expected)'."
        }
    }

    $workflowDirectory = Join-Path $script:RepositoryRoot '.github/workflows'
    $ids = Get-WingetPackageIds (Join-Path $workflowDirectory 'winget-release.yml')
    if ($ids.Count -lt 2 -or 'DimitarRadenkov.Pointframe' -notin $ids -or 'DimitarRadenkov.Pointframe.Cli' -notin $ids)
    {
        throw "Winget package ids were not read from winget-release.yml: [$($ids -join ', ')]."
    }

    $assets = Get-ExpectedAssets (Join-Path $workflowDirectory 'cd.yml') '1.2.3'
    foreach ($required in @('Pointframe-1.2.3-x64-Setup.exe', 'Pointframe.Mcp-1.2.3-win-x64.mcpb', 'Pointframe.Mcp-1.2.3-win-x64.server.json', 'Pointframe.Cli-1.2.3-win-x64.zip'))
    {
        if ($required -notin $assets)
        {
            throw "Expected asset '$required' was not read from cd.yml: [$($assets -join ', ')]."
        }
    }

    $serverName = Get-RegistryServerName (Join-Path $script:RepositoryRoot 'packaging/build-mcp-package.ps1')
    if ($serverName -notlike 'io.github.*')
    {
        throw "Registry server name looks wrong: $serverName."
    }

    return "release-channel fixtures ($($files.Count)), roll-up, and workflow parsing ($($ids.Count) packages, $($assets.Count) assets) passed offline."
}

if ($SelfTest)
{
    Write-Host (Test-ChannelFixtures)
    exit 0
}

if ($Version -notmatch '^\d+\.\d+\.\d+$' -or $TimeoutMinutes -lt 1 -or $PollSeconds -lt 1)
{
    [Console]::Error.WriteLine('-Version must be x.y.z, and -TimeoutMinutes and -PollSeconds must be positive.')
    exit 2
}

# ---------------------------------------------------------------------------------------------------------------------
# Live collection. Everything below is read-only: gh queries, HTTP GETs, git-free.
# ---------------------------------------------------------------------------------------------------------------------

function Invoke-GhJson
{
    # Returns the parsed JSON, or $null when gh fails (the caller decides what a missing answer means).
    param([string[]]$Arguments)

    $output = @(& gh @Arguments 2>$null)
    if ($LASTEXITCODE -ne 0)
    {
        return $null
    }

    $text = ($output -join "`n").Trim()
    if (-not $text)
    {
        return $null
    }

    return $text | ConvertFrom-Json
}

function Get-Step
{
    param($Run, [string]$StepName)

    foreach ($job in @(Get-Opt $Run 'jobs' @()))
    {
        foreach ($step in @(Get-Opt $job 'steps' @()))
        {
            if ($step.name -eq $StepName)
            {
                return $step
            }
        }
    }

    return $null
}

function Get-RunForCommit
{
    param([string]$Workflow, [string]$Sha)

    $runs = Invoke-GhJson @('run', 'list', '--repo', $script:Repository, '--workflow', $Workflow, '--limit', '60', '--json', 'databaseId,headSha,conclusion,status')
    if ($null -eq $runs)
    {
        return $null
    }

    return ($runs | Where-Object { $_.headSha -eq $Sha } | Select-Object -First 1)
}

function Get-PreviousRelease
{
    param([string]$Version)

    $releases = Invoke-GhJson @('release', 'list', '--repo', $script:Repository, '--limit', '100', '--json', 'tagName,isDraft')
    if ($null -eq $releases)
    {
        return ''
    }

    $older = @($releases |
        Where-Object { -not $_.isDraft -and $_.tagName -match '^v(\d+\.\d+\.\d+)$' } |
        ForEach-Object { [version]($_.tagName.Substring(1)) } |
        Where-Object { $_ -lt [version]$Version } |
        Sort-Object -Descending)
    if ($older.Count -eq 0)
    {
        return ''
    }

    return $older[0].ToString()
}

function Get-WingetPrState
{
    param([string]$PackageId, [string]$Version)

    $query = "$PackageId version $Version"
    $pattern = [regex]::Escape("$PackageId version $Version") + '$'
    $prs = Invoke-GhJson @('search', 'prs', '--repo', $script:WingetRepository, $query, '--limit', '20', '--json', 'state,title')
    if ($null -eq $prs)
    {
        return ''
    }

    $mine = @($prs | Where-Object { $_.title -match $pattern })
    if ($mine.Count -eq 0)
    {
        return ''
    }

    foreach ($state in @('merged', 'open', 'closed'))
    {
        if (@($mine | Where-Object { $_.state -eq $state }).Count -gt 0)
        {
            return $state
        }
    }

    return ''
}

function Get-RegistryVersionState
{
    # Returns @{ Present; Error }. 404 means the version is not published yet.
    param([string]$ServerName, [string]$Version)

    $uri = "$($script:RegistryBase)/servers/$([uri]::EscapeDataString($ServerName))/versions/$Version"
    try
    {
        Invoke-RestMethod -Uri $uri -Method Get -TimeoutSec 30 | Out-Null
        return @{ Present = $true; Error = '' }
    }
    catch
    {
        $status = $null
        if ($null -ne $_.Exception.PSObject.Properties['Response'] -and $null -ne $_.Exception.Response)
        {
            $status = [int]$_.Exception.Response.StatusCode
        }

        if ($status -eq 404)
        {
            return @{ Present = $false; Error = '' }
        }

        return @{ Present = $false; Error = "registry query failed: $($_.Exception.Message)" }
    }
}

function Get-PluginPinVersion
{
    $encoded = @(& gh api "repos/$($script:Repository)/contents/plugin/pointframe/server.lock.json?ref=master" --jq .content 2>$null)
    if ($LASTEXITCODE -ne 0 -or $encoded.Count -eq 0)
    {
        return ''
    }

    $json = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String((($encoded -join '') -replace '\s', '')))
    return [string]($json | ConvertFrom-Json).version
}

function Get-Channels
{
    param([string]$Version)

    $workflowDirectory = Join-Path $script:RepositoryRoot '.github/workflows'
    $channels = [System.Collections.Generic.List[object]]::new()

    # The release and its commit anchor everything else: the CD and Winget runs are the ones for that commit.
    $release = Invoke-GhJson @('release', 'view', "v$Version", '--repo', $script:Repository, '--json', 'isDraft,targetCommitish,assets')
    $sha = ''
    if ($null -ne $release)
    {
        $sha = [string]$release.targetCommitish
    }

    $cdRun = $null
    if ($sha -match '^[0-9a-f]{40}$')
    {
        $cdRun = Get-RunForCommit 'CD' $sha
    }
    else
    {
        # No release yet: the newest CD run is the only candidate; a failed one still says why nothing appeared.
        $latest = Invoke-GhJson @('run', 'list', '--repo', $script:Repository, '--workflow', 'CD', '--branch', 'master', '--limit', '1', '--json', 'databaseId,headSha,conclusion,status')
        if ($null -ne $latest -and @($latest).Count -gt 0)
        {
            $cdRun = @($latest)[0]
            $sha = [string]$cdRun.headSha
        }
    }

    $cdDetails = $null
    if ($null -ne $cdRun)
    {
        $cdDetails = Invoke-GhJson @('run', 'view', [string]$cdRun.databaseId, '--repo', $script:Repository, '--json', 'jobs')
    }

    $releaseInput = [pscustomobject]@{
        exists = ($null -ne $release)
        draft = [bool](Get-Opt $release 'isDraft' $false)
        assets = @(Get-Opt $release 'assets' @() | ForEach-Object { $_.name })
        expectedAssets = @(Get-ExpectedAssets (Join-Path $workflowDirectory 'cd.yml') $Version)
        cdConclusion = [string](Get-Opt $cdRun 'conclusion' '')
        cdStatus = [string](Get-Opt $cdRun 'status' '')
    }
    $channels.Add((Resolve-ReleaseChannel $releaseInput))

    # Winget: one channel per package id; each is judged by its own matrix job in the run for the release commit.
    $wingetRun = $null
    $wingetDetails = $null
    if ($sha)
    {
        $wingetRun = Get-RunForCommit 'Winget Release' $sha
        if ($null -ne $wingetRun)
        {
            $wingetDetails = Invoke-GhJson @('run', 'view', [string]$wingetRun.databaseId, '--repo', $script:Repository, '--json', 'jobs')
        }
    }

    foreach ($id in Get-WingetPackageIds (Join-Path $workflowDirectory 'winget-release.yml'))
    {
        $channelName = "winget:$id"
        if ($null -eq $wingetRun)
        {
            $channels.Add((New-Channel $channelName 'pending' 'no Winget Release run for the release commit yet'))
            continue
        }

        $job = $null
        if ($null -ne $wingetDetails)
        {
            $job = $wingetDetails.jobs | Where-Object { $_.name -like "submit ($id,*" } | Select-Object -First 1
        }

        $conclusion = [string]$wingetRun.conclusion
        if ($wingetRun.status -ne 'completed')
        {
            $conclusion = 'in_progress'
        }

        if ($null -ne $job)
        {
            $conclusion = if ($job.status -ne 'completed') { 'in_progress' } else { [string]$job.conclusion }
        }

        $errorLine = ''
        if ($conclusion -eq 'failure' -and $null -ne $job)
        {
            $log = @(& gh run view ([string]$wingetRun.databaseId) --repo $script:Repository --job ([string]$job.databaseId) --log-failed 2>$null)
            $errorLine = Get-ErrorLine $log
        }

        $submitSkipped = $false
        if ($null -ne $job)
        {
            $submit = $job.steps | Where-Object { $_.name -eq 'Submit to winget' } | Select-Object -First 1
            $submitSkipped = ($null -ne $submit -and $submit.conclusion -eq 'skipped')
        }

        $prState = ''
        if ($conclusion -eq 'success' -and -not $submitSkipped)
        {
            $prState = Get-WingetPrState $id $Version
        }

        $channels.Add((Resolve-WingetChannel ([pscustomobject]@{
            name = $channelName
            workflowConclusion = $conclusion
            errorLine = $errorLine
            prState = $prState
            submitSkipped = $submitSkipped
            skipReason = "$id is not in winget-pkgs yet; its first version must be submitted manually"
        })))
    }

    # MCP Registry: the version endpoint answers; a failed CD publish step is the only way to call it failed.
    $publishFailed = $false
    $publishError = ''
    $publishStep = Get-Step $cdDetails $script:RegistryPublishStep
    if ($null -ne $publishStep -and $publishStep.conclusion -eq 'failure')
    {
        $publishFailed = $true
        $publishError = "CD step '$($script:RegistryPublishStep)' failed"
    }

    $serverName = Get-RegistryServerName (Join-Path $script:RepositoryRoot 'packaging/build-mcp-package.ps1')
    $registry = Get-RegistryVersionState $serverName $Version
    $channels.Add((Resolve-RegistryChannel ([pscustomobject]@{
        publishFailed = ($publishFailed -and -not $registry.Present)
        error = $(if ($publishFailed) { $publishError } else { $registry.Error })
        versionPresent = $registry.Present
    })))

    # Plugin pin: master's pin against this release and its predecessor.
    $pin = Get-PluginPinVersion
    if (-not $pin)
    {
        $channels.Add((New-Channel 'plugin-pin' 'pending' 'could not read plugin/pointframe/server.lock.json from master'))
    }
    else
    {
        $channels.Add((Resolve-PluginChannel ([pscustomobject]@{
            version = $Version
            current = $pin
            previous = (Get-PreviousRelease $Version)
        })))
    }

    return $channels.ToArray()
}

if (-not $OutDirectory)
{
    $OutDirectory = Join-Path $script:RepositoryRoot "artifacts/release-channels/$Version"
}

$OutDirectory = [IO.Path]::GetFullPath($OutDirectory)
New-Item -ItemType Directory -Path $OutDirectory -Force | Out-Null
$deadline = (Get-Date).AddMinutes($TimeoutMinutes)

while ($true)
{
    $channels = Get-Channels $Version
    foreach ($channel in $channels)
    {
        Write-Host ('CHANNEL {0}: {1} - {2}' -f $channel.name, $channel.state, $channel.detail)
    }

    $final = Get-FinalState $channels
    Write-Host "CHANNELS: $final"

    $result = [ordered]@{
        version = $Version
        checkedUtc = [DateTime]::UtcNow.ToString('o')
        status = $final
        channels = @($channels)
    }
    $result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $OutDirectory 'result.json') -Encoding utf8NoBOM

    if (-not $Watch -or $final -ne 'pending' -or (Get-Date) -ge $deadline)
    {
        break
    }

    Write-Host ('HEARTBEAT {0}: waiting {1}s for pending channels' -f [DateTime]::UtcNow.ToString('o'), $PollSeconds)
    Start-Sleep -Seconds $PollSeconds
}

switch ($final)
{
    'failed' { exit 1 }
    'pending' { exit 3 }
}

exit 0
