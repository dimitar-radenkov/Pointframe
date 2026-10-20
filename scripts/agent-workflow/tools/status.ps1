#Requires -Version 7.0
<#
Authoritative status store of the repository: the issue and the handoff file named in agent-workflow.json.
  status.ps1 -Set -Id <id> -Stage <stage> [-Objective <text>] [-Note <text>]
  status.ps1 -Done -Id <id> [-Evidence <text>] [-Note <text>]
  status.ps1 -Decision -Objective <text> -Text <decision/reason>
  status.ps1 -Show
  status.ps1 -Publish [-Issue <number>] [-HandoffPath <file>] [-DryRun]
  status.ps1 -SelfTest
The store, the issue, the handoff file and the accountable name default to status.store, status.issue, status.handoff and
status.accountable of agent-workflow.json, which is read from -Root, else from the repository that holds this script (never
the caller's folder). Writes to the store are serialized by a lock under git's common directory. Publish uses gh issue
view/edit. Exit 0 success, 1 operation failure.
#>
[CmdletBinding(DefaultParameterSetName = 'Show')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Set')]
    [switch]$Set,
    [Parameter(Mandatory, ParameterSetName = 'Done')]
    [switch]$Done,
    [Parameter(Mandatory, ParameterSetName = 'Decision')]
    [switch]$Decision,
    [Parameter(Mandatory, ParameterSetName = 'Publish')]
    [switch]$Publish,
    [Parameter(Mandatory, ParameterSetName = 'SelfTest')]
    [switch]$SelfTest,
    [Parameter(ParameterSetName = 'Show')]
    [switch]$Show,
    [Parameter(Mandatory, ParameterSetName = 'Set')]
    [Parameter(Mandatory, ParameterSetName = 'Done')]
    [string]$Id,
    [Parameter(ParameterSetName = 'Set')]
    [Parameter(Mandatory, ParameterSetName = 'Decision')]
    [string]$Objective,
    [Parameter(ParameterSetName = 'Set')]
    [Parameter(ParameterSetName = 'Decision')]
    [string]$Accountable,
    [Parameter(ParameterSetName = 'Set')]
    [string]$Stage,
    [Parameter(ParameterSetName = 'Set')]
    [string]$Artifact,
    [Parameter(ParameterSetName = 'Set')]
    [Parameter(ParameterSetName = 'Done')]
    [string]$Evidence,
    [Parameter(ParameterSetName = 'Set')]
    [string]$Next,
    [Parameter(ParameterSetName = 'Set')]
    [string]$Blocker,
    [Parameter(ParameterSetName = 'Set')]
    [Parameter(ParameterSetName = 'Done')]
    [string]$Note,
    [Parameter(Mandatory, ParameterSetName = 'Decision')]
    [string]$Text,
    [Parameter(ParameterSetName = 'Decision')]
    [string]$Scope,
    [Parameter(ParameterSetName = 'Decision')]
    [string]$DecisionEvidence,
    [Parameter(ParameterSetName = 'Decision')]
    [string]$Recovery,
    [Parameter(ParameterSetName = 'Decision')]
    [string]$Outcome,
    [Parameter(ParameterSetName = 'Publish')]
    [int]$Issue,
    [Parameter(ParameterSetName = 'Publish')]
    [string]$HandoffPath,
    [Parameter(ParameterSetName = 'Publish')]
    [switch]$DryRun,
    [string]$StorePath,
    [string]$Root
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '..' 'lib' 'root.ps1')
. (Join-Path $PSScriptRoot '..' 'lib' 'config.ps1')
. (Join-Path $PSScriptRoot '..' 'lib' 'context.ps1')
. (Join-Path $PSScriptRoot '..' 'lib' 'selftest.ps1')

function Resolve-RepositoryPath
{
    param(
        [string]$Path
    )

    if ([IO.Path]::IsPathRooted($Path))
    {
        return [IO.Path]::GetFullPath($Path)
    }

    return [IO.Path]::GetFullPath((Join-Path $script:RepositoryRoot $Path))
}

function New-Store
{
    [pscustomobject]@{ schemaVersion = 1; jobs = @(); decisions = @() }
}

function Read-Store
{
    param(
        [string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path))
    {
        return New-Store
    }

    $store = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -DateKind String
    if ($store.schemaVersion -ne 1)
    {
        throw 'Unsupported status store schemaVersion.'
    }

    return $store
}

function Save-Store
{
    param(
        $Store,
        [string]$Path
    )

    $directory = Split-Path -Parent $Path
    if ($directory -and -not (Test-Path $directory))
    {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }

    [IO.File]::WriteAllText([IO.Path]::GetFullPath($Path), ($Store | ConvertTo-Json -Depth 20) + "`r`n", [Text.UTF8Encoding]::new($false))
}

function Format-Cell
{
    param(
        [string]$Value
    )

    if ($null -eq $Value)
    {
        return ''
    }

    return (($Value -replace '\|', '\|') -replace '[\r\n]+', ' ')
}

function Format-ActiveRow
{
    param(
        $Job
    )

    return '| ' + (@((Format-Cell $Job.objective), (Format-Cell $Job.accountable), (Format-Cell $Job.stage), (Format-Cell $Job.artifact), (Format-Cell $Job.evidence), (Format-Cell $Job.next), (Format-Cell $Job.blocker)) -join ' | ') + ' |'
}

function Format-ActiveRegion
{
    param(
        $Store
    )

    $rows = @('<!-- status:active:start -->', '## Active', '', '| Objective | Accountable | Stage | SHA / artifact | Evidence | Next step | Blocker |', '|---|---|---|---|---|---|---|')
    foreach ($job in @($Store.jobs | Where-Object state -eq 'active'))
    {
        $rows += Format-ActiveRow $job
    }

    $rows += '<!-- status:active:end -->'
    return ($rows -join "`r`n")
}

function Format-HandoffRegion
{
    param(
        $Store
    )

    $lines = @('<!-- status:handoff:start -->', '## In flight')
    $activeJobs = @($Store.jobs | Where-Object state -eq 'active')
    if (-not $activeJobs.Count)
    {
        $lines += '- No active jobs.'
    }

    foreach ($job in $activeJobs)
    {
        $lines += ('- **{0}** — stage: {1}; next: {2}; blocker: {3}' -f (Format-Cell $job.objective), (Format-Cell $job.stage), (Format-Cell $job.next), (Format-Cell $job.blocker))
        $history = @($job.history | Select-Object -Last 5)
        foreach ($entry in $history)
        {
            $lines += ('  - {0}: {1} — {2}' -f $entry.utc, $entry.stage, $entry.note)
        }
    }

    $lines += '<!-- status:handoff:end -->'
    return ($lines -join "`r`n")
}

function Update-MarkedRegion
{
    param(
        [string]$Text,
        [string]$Start,
        [string]$End,
        [string]$Replacement,
        [string]$Fallback
    )

    $startIndex = $Text.IndexOf($Start)
    $endIndex = $Text.IndexOf($End)
    if ($startIndex -ge 0 -and $endIndex -gt $startIndex)
    {
        $endIndex += $End.Length
        return $Text.Substring(0, $startIndex) + $Replacement + $Text.Substring($endIndex)
    }

    if ($Fallback -eq 'active')
    {
        $match = [regex]::Match($Text, '(?ms)^## Active\r?\n.*?(?=^## Done\s*$)')
        if (-not $match.Success)
        {
            throw 'Cannot locate ## Active table for first-run replacement.'
        }

        return $Text.Substring(0, $match.Index) + $Replacement + "`r`n`r`n" + $Text.Substring($match.Index + $match.Length)
    }

    $match = [regex]::Match($Text, '(?m)^# .+$')
    if (-not $match.Success)
    {
        throw 'Cannot insert handoff status region: no heading found.'
    }

    $insertAt = $match.Index + $match.Length
    return $Text.Substring(0, $insertAt) + "`r`n`r`n" + $Replacement + $Text.Substring($insertAt)
}

function Format-IssueBody
{
    param(
        [string]$Body,
        $Store
    )

    $output = Update-MarkedRegion $Body '<!-- status:active:start -->' '<!-- status:active:end -->' (Format-ActiveRegion $Store) 'active'
    $freshDecisions = @($Store.decisions | Where-Object { -not $_.published })
    if ($freshDecisions.Count)
    {
        $rows = foreach ($decision in $freshDecisions)
        {
            '| {0} | {1} | {2} | {3} | {4} | {5} | {6} | {7} |' -f (Format-Cell $decision.utc), (Format-Cell $decision.objective), (Format-Cell $decision.accountable), (Format-Cell $decision.decision), (Format-Cell $decision.scope), (Format-Cell $decision.evidence), (Format-Cell $decision.recovery), (Format-Cell $decision.outcome)
        }

        # Restrict insertion to the Decision log table, then append after its existing rows.
        $heading = [regex]::Match($output, '(?m)^## Decision log\s*$')
        if (-not $heading.Success)
        {
            throw 'Cannot locate ## Decision log heading.'
        }

        $table = [regex]::Match($output.Substring($heading.Index + $heading.Length), '(?ms)^\|.*?\r?\n\|[-| :]+\|(?:\r?\n(?:\|.*(?:\r?\n|$))?)*')
        if (-not $table.Success)
        {
            throw 'Cannot locate Decision log table header.'
        }

        $tableStart = $heading.Index + $heading.Length + $table.Index
        $tableText = $table.Value
        $lastRow = [regex]::Matches($tableText, '(?m)^\|.*\|\s*$') | Select-Object -Last 1
        if (-not $lastRow)
        {
            throw 'Cannot locate last Decision log row.'
        }

        $insertAt = $tableStart + $lastRow.Index + $lastRow.Length
        $output = $output.Insert($insertAt, "`r`n" + ($rows -join "`r`n"))
    }

    return $output
}

function Format-Handoff
{
    param(
        [string]$Body,
        $Store
    )

    return Update-MarkedRegion $Body '<!-- status:handoff:start -->' '<!-- status:handoff:end -->' (Format-HandoffRegion $Store) 'handoff'
}

function Update-Job
{
    param(
        $Store,
        [string]$Id,
        [string]$Now,
        [string]$Accountable,
        [bool]$IsSet,
        [bool]$IsDone,
        [string]$Stage,
        [string]$Artifact,
        [string]$Evidence,
        [string]$Next,
        [string]$Blocker,
        [string]$Objective,
        [string]$Note,
        [System.Collections.IDictionary]$BoundParameters
    )

    $existing = @($Store.jobs | Where-Object id -eq $Id | Select-Object -First 1)
    if (-not $existing.Count)
    {
        if ($IsDone)
        {
            throw "Unknown job '$Id'."
        }

        $job = [pscustomobject]@{ id = $Id; objective = ''; accountable = $Accountable; stage = ''; artifact = ''; evidence = ''; next = ''; blocker = ''; state = 'active'; updatedUtc = ''; history = @() }
        $Store.jobs = @($Store.jobs) + $job
    }
    else
    {
        $job = $existing[0]
    }

    if (($BoundParameters.Keys -contains 'Objective')) { $job.objective = $Objective }
    if ($IsSet -and ($BoundParameters.Keys -contains 'Accountable')) { $job.accountable = $Accountable }
    if (($BoundParameters.Keys -contains 'Stage')) { $job.stage = $Stage }
    if (($BoundParameters.Keys -contains 'Artifact')) { $job.artifact = $Artifact }
    if (($BoundParameters.Keys -contains 'Evidence')) { $job.evidence = $Evidence }
    if (($BoundParameters.Keys -contains 'Next')) { $job.next = $Next }
    if (($BoundParameters.Keys -contains 'Blocker')) { $job.blocker = if ($Blocker -eq 'none') { '' } else { $Blocker } }
    if ($IsDone) { $job.state = 'done' }

    $eventStage = if ($IsDone) { 'done' } elseif ($Stage) { $Stage } else { $job.stage }
    $job.history = @($job.history) + [pscustomobject]@{ utc = $Now; stage = $eventStage; note = $Note }
    $job.updatedUtc = $Now
    return $Store
}

if ($SelfTest)
{
    $fixtureDirectory = Join-Path $PSScriptRoot '..' 'fixtures' 'gates' 'status'
    $store = Get-Content (Join-Path $fixtureDirectory 'store.json') -Raw | ConvertFrom-Json -DateKind String
    $body = Get-Content (Join-Path $fixtureDirectory 'issue-without-markers.md') -Raw
    $rendered = Format-IssueBody $body $store
    $handoff = Get-Content (Join-Path $fixtureDirectory 'handoff.md') -Raw
    $renderedHandoff = Format-Handoff $handoff $store
    $checks = 0

    if ($rendered -notmatch '<!-- status:active:start -->') { throw 'first-run active markers missing' }
    $checks++
    if ($rendered -notmatch 'preserve this intro' -or $rendered -notmatch 'Keep this done line') { throw 'non-owned issue text lost' }
    $checks++
    if ($rendered -notmatch 'a\\\|b') { throw 'pipe escaping failed' }
    $checks++
    if ($rendered -match 'done-job') { throw 'done job rendered as active' }
    $checks++
    if ($renderedHandoff -notmatch '<!-- status:handoff:start -->' -or $renderedHandoff -notmatch 'handoff tail') { throw 'handoff region/preservation failed' }
    $checks++
    $marked = Get-Content (Join-Path $fixtureDirectory 'issue-with-markers.md') -Raw
    $renderedMarked = Format-IssueBody $marked $store
    if ($renderedMarked -notmatch 'older decision row') { throw 'older decision row lost' }
    $checks++
    $published = Read-Store (Join-Path $fixtureDirectory 'store.json')
    $published.decisions[0].published = $true
    $once = Format-IssueBody $body $published
    if ($once -match '\| 2026-10-07 \| decision row \|') { throw 'published decision was appended again' }
    $checks++
    $jobStore = New-Store
    $jobStore.jobs = @([pscustomobject]@{ id = 'self-test'; objective = ''; accountable = 'Claude'; stage = ''; artifact = ''; evidence = ''; next = ''; blocker = ''; state = 'active'; updatedUtc = ''; history = @() })
    $bound = [ordered]@{ Blocker = 'x' }
    $jobStore = Update-Job $jobStore 'self-test' '2026-10-07T00:00:00Z' 'Claude' $true $false '' '' '' '' 'x' '' '' $bound
    $bound = [ordered]@{ Blocker = 'none' }
    $jobStore = Update-Job $jobStore 'self-test' '2026-10-07T00:00:01Z' 'Claude' $true $false '' '' '' '' 'none' '' '' $bound
    if ($jobStore.jobs[0].blocker -ne '') { throw 'blocker was not cleared by -Blocker none' }
    $checks++
    $orderedStore = [pscustomobject]@{ jobs = @(); decisions = @([pscustomobject]@{ utc = '2026-10-07'; objective = 'new decision'; accountable = 'Claude'; decision = 'new decision'; scope = ''; evidence = ''; recovery = ''; outcome = ''; published = $false }) }
    $orderedBody = Format-IssueBody $marked $orderedStore
    if ($orderedBody.IndexOf('| new decision |') -le $orderedBody.IndexOf('| older decision row |')) { throw 'new decision was not appended after the existing decision row' }
    $checks++

    # End to end through the real command line, so parameter binding ($PSBoundParameters) and the store round trip
    # (dates stay strings) are exercised too.
    $temporaryRepo = Join-Path ([IO.Path]::GetTempPath()) "aw-status-$([guid]::NewGuid().ToString('N'))"
    $temporaryStore = Join-Path $temporaryRepo 'store.json'
    try
    {
        New-Item -ItemType Directory -Path $temporaryRepo | Out-Null
        & git -C $temporaryRepo init -q
        Write-AwSelfTestConfig $temporaryRepo (Get-AwSelfTestConfig)
        & pwsh -NoProfile -File $PSCommandPath -Set -Id e2e -Objective 'end to end' -Stage one -Blocker 'stuck' -StorePath $temporaryStore -Root $temporaryRepo
        & pwsh -NoProfile -File $PSCommandPath -Set -Id e2e -Stage two -Blocker none -StorePath $temporaryStore -Root $temporaryRepo
        & pwsh -NoProfile -File $PSCommandPath -Done -Id e2e -StorePath $temporaryStore -Root $temporaryRepo
        $endToEnd = Read-Store $temporaryStore
        $job = @($endToEnd.jobs)[0]
        if ($job.objective -ne 'end to end' -or $job.stage -ne 'two' -or $job.blocker -ne '' -or $job.state -ne 'done' -or
            @($job.history).Count -ne 3 -or $job.updatedUtc -notmatch 'Z$')
        {
            throw 'command-line -Set/-Done did not update the store as expected'
        }
    }
    finally
    {
        Remove-Item -LiteralPath $temporaryRepo -Recurse -Force -ErrorAction SilentlyContinue
    }
    $checks++
    Write-Host "status self-test passed: $checks checks."
    exit 0
}

try
{
    $Context = Get-AwToolContext -Root $Root -ScriptRoot $PSScriptRoot
}
catch
{
    Write-Host "ERROR $($_.Exception.Message)"
    exit 1
}
$script:RepositoryRoot = $Context.Root
$statusSettings = $Context.Config.status
if (-not $StorePath) { $StorePath = [string]$statusSettings.store }
if (-not $HandoffPath) { $HandoffPath = [string]$statusSettings.handoff }
if (-not $PSBoundParameters.ContainsKey('Issue') -and $null -ne $statusSettings.issue) { $Issue = [int]$statusSettings.issue }
if (-not $PSBoundParameters.ContainsKey('Accountable'))
{
    # The default accountable name; it is only applied to new jobs and decisions, never to an existing job (see Update-Job).
    $Accountable = if ($statusSettings.Contains('accountable')) { [string]$statusSettings.accountable } else { 'Claude' }
}
$StorePath = Resolve-RepositoryPath $StorePath

# A read-modify-write of the store is serialized across processes by an exclusive lock under git's common directory.
$storeLock = $null
if ($Set -or $Done -or $Decision -or ($Publish -and -not $DryRun))
{
    $lockDirectory = $Context.StateDir
    New-Item -ItemType Directory -Path $lockDirectory -Force | Out-Null
    $lockPath = Join-Path $lockDirectory 'status.lock'
    $lockDeadline = [DateTime]::UtcNow.AddSeconds(60)
    while ($null -eq $storeLock)
    {
        try { $storeLock = [IO.File]::Open($lockPath, 'OpenOrCreate', 'ReadWrite', 'None') }
        catch [IO.IOException]
        {
            if ([DateTime]::UtcNow -gt $lockDeadline)
            {
                Write-Host "ERROR the status store lock $lockPath is held by another process"
                exit 1
            }
            Start-Sleep -Milliseconds 100
        }
    }
}
try
{
$store = Read-Store $StorePath
$now = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
if ($Set -or $Done)
{
    $store = Update-Job $store $Id $now $Accountable ([bool]$Set) ([bool]$Done) $Stage $Artifact $Evidence $Next $Blocker $Objective $Note $PSBoundParameters
    Save-Store $store $StorePath
    exit 0
}

if ($Decision)
{
    $store.decisions = @($store.decisions) + [pscustomobject]@{ utc = $now; objective = $Objective; accountable = $Accountable; decision = $Text; scope = $Scope; evidence = $DecisionEvidence; recovery = $Recovery; outcome = $Outcome; published = $false }
    Save-Store $store $StorePath
    exit 0
}

if ($Show)
{
    foreach ($job in @($store.jobs | Where-Object state -eq 'active'))
    {
        $age = ([DateTime]::UtcNow - [DateTime]::Parse($job.updatedUtc, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::AdjustToUniversal)).ToString('d\.hh\:mm')
        Write-Output "$($job.id) | $($job.stage) | $($job.next) | $($job.blocker) | $($job.updatedUtc) ($age)"
    }

    exit 0
}

if ($Publish)
{
    if ($Issue -le 0) { throw 'No issue to publish to: pass -Issue or set status.issue in agent-workflow.json.' }
    $handoffFullPath = Resolve-RepositoryPath $HandoffPath
    if (-not (Test-Path $handoffFullPath)) { throw "Handoff not found: $handoffFullPath" }
    $handoff = [IO.File]::ReadAllText($handoffFullPath)
    $issueBody = & gh issue view $Issue --json body -q .body
    if ($LASTEXITCODE -ne 0) { throw 'gh issue view failed.' }
    $newBody = Format-IssueBody ($issueBody -join "`n") $store
    $newHandoff = Format-Handoff $handoff $store
    $region = Format-HandoffRegion $store
    if ($DryRun)
    {
        Write-Output $newBody
        Write-Output $region
        exit 0
    }

    $temporaryPath = [IO.Path]::GetTempFileName()
    [IO.File]::WriteAllText($temporaryPath, $newBody, [Text.UTF8Encoding]::new($false))
    & gh issue edit $Issue --body-file $temporaryPath
    Remove-Item $temporaryPath -Force
    if ($LASTEXITCODE -ne 0) { throw 'gh issue edit failed.' }
    [IO.File]::WriteAllText($handoffFullPath, $newHandoff, [Text.UTF8Encoding]::new($false))
    foreach ($publishedDecision in @($store.decisions | Where-Object { -not $_.published })) { $publishedDecision.published = $true }
    Save-Store $store $StorePath
    exit 0
}
}
finally
{
    if ($storeLock) { $storeLock.Dispose() }
}
