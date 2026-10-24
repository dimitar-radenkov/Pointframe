#Requires -Version 7.0
<#
Writes the release receipt CD uploads as the `release-plan` artifact (release-plan.json). The receipt is what
check-release-channels.ps1 -Receipt reads instead of assuming one version everywhere: per train its version, tag, commit,
the SHA-256 of every asset CD uploaded, the publication state, the Latest proofs and the release plan of the merge.

  pwsh scripts/write-release-receipt.ps1 -Version 6.7.90 -Commit <sha> -Publication published -Out release-plan.json `
      [-Trains desktop,agent] [-ProofCreate create.json] [-ProofUpdate update.json] [-PromoteResult promote.json] `
      [-LatestResult latest.json] [-PlanFile plan.json] [-Root <repo root>]
  pwsh scripts/write-release-receipt.ps1 -SelfTest

The asset list is the `files: |` block of the Create GitHub Release step in cd.yml, so the receipt, the release and the
channel check cannot disagree about what a release holds. The installer belongs to the desktop train, everything else to
the agent train. A file that does not exist (a failed build) is listed with a null sha256. Proof, promote, latest and plan
files that do not exist are recorded as not run / null, never as passed.

Receipt, schemaVersion 1:
  schemaVersion, repository, createdUtc, commit,
  trains.<desktop|agent>: { selected: false }
                       or { selected: true, version, tag, commit, publication: published|failed|draft,
                            assets: [{ name, sha256 }] },
  proofs: { makeLatestOnCreate, makeLatestOnUpdate }: { status: passed|failed|not-run, expected, actual, message },
  promotion: { promoted, reason } or null, latest: { decision, latest, highestDesktop, repaired } or null,
  plan: the release-impact.ps1 -PlanOut document, or null.

Exit codes: 0 written, 2 bad arguments.
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$Commit,
    [ValidateSet('published', 'failed', 'draft')]
    [string]$Publication = 'published',
    [string]$Trains = 'desktop,agent',
    [string]$AliasSourceVersion,
    [string]$StateFile,
    [string]$Repository = 'dimitar-radenkov/Pointframe',
    [string]$ProofCreate,
    [string]$ProofUpdate,
    [string]$PromoteResult,
    [string]$LatestResult,
    [string]$PlanFile,
    [string]$Root = (Get-Location).Path,
    [string]$WorkflowPath,
    [string]$Out,
    [switch]$SelfTest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

. (Join-Path $PSScriptRoot 'release-model.ps1')

$script:RepositoryRoot = Split-Path $PSScriptRoot -Parent
if (-not $WorkflowPath)
{
    $WorkflowPath = Join-Path $script:RepositoryRoot '.github/workflows/cd.yml'
}

function Read-JsonFile
{
    param([string]$Path)

    if (-not $Path -or -not (Test-Path -LiteralPath $Path -PathType Leaf))
    {
        return $null
    }

    return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
}

function ConvertTo-ProofRecord
{
    param($Result)

    if ($null -eq $Result)
    {
        return [ordered]@{ status = 'not-run'; expected = $null; actual = $null; message = 'the proof step did not run or wrote no result' }
    }

    return [ordered]@{
        status = [string](Get-ModelProperty $Result 'status' 'failed')
        expected = Get-ModelProperty $Result 'expected' $null
        actual = Get-ModelProperty $Result 'actual' $null
        message = Get-ModelProperty $Result 'message' $null
    }
}

function New-ReleaseReceipt
{
    # Files: objects with Name, Train and Sha256 (null when the file does not exist).
    param(
        [string]$Repository,
        [string]$Commit,
        [string]$Version,
        [string[]]$SelectedTrains,
        [object[]]$Files,
        [string]$Publication,
        [string]$AliasSourceVersion = '',
        [hashtable]$TrainStates = @{},
        $ProofCreate = $null,
        $ProofUpdate = $null,
        $PromoteResult = $null,
        $LatestResult = $null,
        $Plan = $null,
        [string]$CreatedUtc
    )

    $trains = [ordered]@{}
    foreach ($name in @('desktop', 'agent'))
    {
        if ($name -notin $SelectedTrains)
        {
            $trains[$name] = [ordered]@{ selected = $false }
            continue
        }

        $trains[$name] = [ordered]@{
            selected = $true
            version = $Version
            tag = "v$Version"
            commit = $Commit
            publication = $Publication
            state = $(if ($TrainStates.ContainsKey($name)) { [string]$TrainStates[$name] } else { $Publication })
            assets = @($Files | Where-Object { $_.Train -eq $name } | ForEach-Object { [ordered]@{ name = $_.Name; sha256 = $_.Sha256 } })
        }
        if ($name -eq 'desktop' -and $AliasSourceVersion) { $trains[$name]['aliasSourceVersion'] = $AliasSourceVersion }
    }

    $promotion = if ($null -eq $PromoteResult) { $null } else { [ordered]@{ promoted = [bool](Get-ModelProperty $PromoteResult 'promoted' $false); reason = Get-ModelProperty $PromoteResult 'reason' $null } }
    $latest = if ($null -eq $LatestResult) { $null } else {
        [ordered]@{
            decision = Get-ModelProperty $LatestResult 'decision' $null
            latest = Get-ModelProperty $LatestResult 'latest' $null
            highestDesktop = Get-ModelProperty $LatestResult 'highestDesktop' $null
            repaired = [bool](Get-ModelProperty $LatestResult 'repaired' $false)
        }
    }

    return [ordered]@{
        schemaVersion = 1
        repository = $Repository
        createdUtc = $CreatedUtc
        commit = $Commit
        trains = $trains
        proofs = [ordered]@{
            makeLatestOnCreate = ConvertTo-ProofRecord $ProofCreate
            makeLatestOnUpdate = ConvertTo-ProofRecord $ProofUpdate
        }
        promotion = $promotion
        latest = $latest
        plan = $Plan
    }
}

function Get-FileSha256
{
    param([string]$Root, [string]$RelativePath)

    $path = Join-Path $Root $RelativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf))
    {
        return $null
    }

    return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-ReceiptFiles
{
    param([string]$WorkflowPath, [string]$Version, [string]$Root, [string[]]$SelectedTrains = @('desktop', 'agent'))

    $files = @(Get-ReleaseFileList $WorkflowPath $Version $SelectedTrains)
    if ($files.Count -eq 0 -and $SelectedTrains.Count -gt 0)
    {
        throw "No release files were read from the 'files: |' block of $WorkflowPath."
    }

    return @($files | ForEach-Object { [pscustomobject]@{ Name = $_.Name; Train = $_.Train; Sha256 = Get-FileSha256 $Root $_.Path } })
}

function Test-SelfCases
{
    $version = '1.2.3'
    $sha = '0123456789abcdef0123456789abcdef01234567'
    $listed = @(Get-ReleaseFileList $WorkflowPath $version @('desktop', 'agent'))
    $desktop = @($listed | Where-Object { $_.Train -eq 'desktop' })
    if ($listed.Count -ne 10 -or $desktop.Count -ne 5 -or 'Pointframe-1.2.3-x64-Setup.exe' -notin @($desktop.Name))
    {
        throw "cd.yml files block: expected 10 files with the installer as the only desktop file, got $($listed.Count) files and $($desktop.Count) desktop file(s)."
    }

    $root = Join-Path ([IO.Path]::GetTempPath()) "receipt-$([guid]::NewGuid().ToString('N'))"
    try
    {
        foreach ($file in $listed | Where-Object { $_.Name -ne 'Pointframe.Mcp-win-x64.mcpb.sha256' })
        {
            $target = Join-Path $root $file.Path
            New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
            Set-Content -LiteralPath $target -Value "content of $($file.Name)" -NoNewline
        }

        $files = @(Get-ReceiptFiles $WorkflowPath $version $root)
        $expectedSha = (Get-FileHash -InputStream ([IO.MemoryStream]::new([Text.Encoding]::UTF8.GetBytes('content of Pointframe-1.2.3-x64-Setup.exe'))) -Algorithm SHA256).Hash.ToLowerInvariant()
        $installer = $files | Where-Object { $_.Name -eq 'Pointframe-1.2.3-x64-Setup.exe' }
        if ($installer.Sha256 -ne $expectedSha)
        {
            throw 'the installer sha256 in the receipt does not match the file.'
        }

        $absent = $files | Where-Object { $_.Name -eq 'Pointframe.Mcp-win-x64.mcpb.sha256' }
        if ($null -ne $absent.Sha256)
        {
            throw 'a file that does not exist must be listed with a null sha256.'
        }

        $plan = [ordered]@{ schemaVersion = 1; trains = @('desktop', 'agent') }
        $proof = [pscustomobject]@{ status = 'passed'; expected = 'v1.2.2'; actual = 'v1.2.2'; message = $null }
        $combined = New-ReleaseReceipt -Repository 'o/r' -Commit $sha -Version $version -SelectedTrains @('desktop', 'agent') -Files $files -Publication 'published' -ProofCreate $proof -ProofUpdate $proof -PromoteResult ([pscustomobject]@{ promoted = $true; reason = 'highest' }) -LatestResult ([pscustomobject]@{ decision = 'ok'; latest = 'v1.2.3'; highestDesktop = 'v1.2.3'; repaired = $false }) -Plan $plan -CreatedUtc '2026-10-23T10:00:00Z'
        $json = $combined | ConvertTo-Json -Depth 8 | ConvertFrom-Json
        $problems = @(Test-ReleaseReceipt $json)
        if ($problems.Count -gt 0)
        {
            throw "the combined receipt is not valid: $($problems -join '; ')"
        }

        if (@($json.trains.desktop.assets).Count -ne 5 -or @($json.trains.agent.assets).Count -ne 5 -or $json.proofs.makeLatestOnCreate.status -ne 'passed' -or $json.latest.decision -ne 'ok' -or $json.plan.schemaVersion -ne 1)
        {
            throw 'the combined receipt does not hold 1 desktop asset, 9 agent assets, the proofs, the latest result and the plan.'
        }

        $desktopOnly = New-ReleaseReceipt -Repository 'o/r' -Commit $sha -Version $version -SelectedTrains @('desktop') -Files $files -Publication 'failed' -AliasSourceVersion '1.2.2' -CreatedUtc '2026-10-23T10:00:00Z' | ConvertTo-Json -Depth 8 | ConvertFrom-Json
        if (@(Test-ReleaseReceipt $desktopOnly).Count -gt 0 -or $desktopOnly.trains.agent.selected -or $desktopOnly.trains.desktop.publication -ne 'failed' -or @($desktopOnly.trains.desktop.assets).Count -ne 5 -or $desktopOnly.trains.desktop.aliasSourceVersion -ne '1.2.2' -or $desktopOnly.proofs.makeLatestOnUpdate.status -ne 'not-run' -or $null -ne $desktopOnly.plan)
        {
            throw 'a desktop-only receipt must validate, list installer and aliases, record the alias source, and leave the agent train unselected.'
        }

        $agentOnly = New-ReleaseReceipt -Repository 'o/r' -Commit $sha -Version $version -SelectedTrains @('agent') -Files $files -Publication 'published' -CreatedUtc '2026-10-23T10:00:00Z' | ConvertTo-Json -Depth 8 | ConvertFrom-Json
        if (@(Test-ReleaseReceipt $agentOnly).Count -gt 0 -or $agentOnly.trains.desktop.selected -or @($agentOnly.trains.agent.assets).Count -ne 5) { throw 'an agent-only receipt must list exactly the five versioned agent assets.' }
        $none = New-ReleaseReceipt -Repository 'o/r' -Commit $sha -Version $version -SelectedTrains @() -Files @() -Publication 'published' -CreatedUtc '2026-10-23T10:00:00Z' | ConvertTo-Json -Depth 8 | ConvertFrom-Json
        if (@(Test-ReleaseReceipt $none).Count -gt 0 -or $none.trains.desktop.selected -or $none.trains.agent.selected) { throw 'a none receipt must validate with both trains unselected.' }

        $broken = $json | ConvertTo-Json -Depth 8 | ConvertFrom-Json
        $broken.trains.agent.tag = 'v9.9.9'
        $broken.trains.desktop.assets[0].sha256 = 'not-a-hash'
        if (@(Test-ReleaseReceipt $broken).Count -ne 2)
        {
            throw "validation must report the wrong tag and the malformed sha256 (got $(@(Test-ReleaseReceipt $broken).Count) problem(s))."
        }
    }
    finally
    {
        Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
    }
}

if ($SelfTest)
{
    Test-SelfCases
    Write-Host 'write-release-receipt self-test passed: shared train asset lists, hashing, desktop-only, agent-only, both, and none receipts, alias source, validation.'
    exit 0
}

if ($Version -notmatch '^\d+\.\d+\.\d+$' -or $Commit -notmatch '^[0-9a-f]{40}$' -or -not $Out)
{
    [Console]::Error.WriteLine('-Version x.y.z, -Commit <40-character sha> and -Out <file> are required.')
    exit 2
}

$selected = @($Trains -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
if (@($selected | Where-Object { $_ -notin @('desktop', 'agent') }).Count -gt 0)
{
    [Console]::Error.WriteLine("-Trains must be a comma list of desktop and agent, got '$Trains'.")
    exit 2
}

$files = Get-ReceiptFiles $WorkflowPath $Version $Root $selected
$trainStates = @{}
$publicationResult = Read-JsonFile $StateFile
if ($null -ne $publicationResult)
{
    foreach ($train in @('desktop', 'agent'))
    {
        $state = [string](Get-ModelProperty $publicationResult.trains $train 'not-selected')
        if ($state -ne 'not-selected') { $trainStates[$train] = $state }
    }
}
$receipt = New-ReleaseReceipt -Repository $Repository -Commit $Commit -Version $Version -SelectedTrains $selected -Files $files -Publication $Publication `
    -AliasSourceVersion $AliasSourceVersion -TrainStates $trainStates `
    -ProofCreate (Read-JsonFile $ProofCreate) -ProofUpdate (Read-JsonFile $ProofUpdate) -PromoteResult (Read-JsonFile $PromoteResult) `
    -LatestResult (Read-JsonFile $LatestResult) -Plan (Read-JsonFile $PlanFile) -CreatedUtc ([DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ'))

$problems = @(Test-ReleaseReceipt ($receipt | ConvertTo-Json -Depth 12 | ConvertFrom-Json))
if ($problems.Count -gt 0)
{
    throw "The receipt is not valid: $($problems -join '; ')"
}

$directory = Split-Path -Path $Out -Parent
if ($directory -and -not (Test-Path -LiteralPath $directory))
{
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
}

$receipt | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $Out -Encoding utf8NoBOM
Write-Host "Wrote ${Out}: $($selected -join ', ') v$Version, $($files.Count) assets, publication $Publication."
exit 0
