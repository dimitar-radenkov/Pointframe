#Requires -Version 7.0
<#
Creates a validated draft release and publishes it without allowing GitHub to move Latest implicitly.
Offline behavior is covered by -SelfTest; live GitHub behavior is deliberately not exercised locally.
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$Trains = 'desktop,agent',
    [string]$Commit,
    [string]$Repository = 'dimitar-radenkov/Pointframe',
    [string]$Root = (Get-Location).Path,
    [string]$ResultOut,
    [switch]$SelfTest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
. (Join-Path $PSScriptRoot 'release-model.ps1')

function Invoke-GhCall
{
    param([scriptblock]$Gh, [string[]]$Arguments)
    $result = & $Gh -Arguments $Arguments
    if ($null -eq $result) { return $null }
    if ($result -is [string]) { return $result | ConvertFrom-Json -ErrorAction SilentlyContinue }
    return $result
}

function Read-GhReleaseListing
{
    param([scriptblock]$Gh, [string]$Repository, [int]$MaxPages = 50)
    $all = [Collections.Generic.List[object]]::new()
    for ($page = 1; $page -le $MaxPages; $page++)
    {
        $pageArgs = @('api', "repos/$Repository/releases?per_page=100&page=$page")
        $response = & $Gh -Arguments $pageArgs
        if ($null -eq $response) { throw "Could not list releases (page $page)." }
        $json = @($response.data)
        foreach ($item in $json) { $all.Add($item) }
        if ($json.Count -lt 100) { return $all.ToArray() }
    }
    throw "More than $MaxPages pages of releases."
}

function Get-PublishTrains
{
    param([string]$Version, [string[]]$Trains)
    $assets = @(Get-TrainAssetNames $Version $Trains)
    $result = foreach ($name in $assets)
    {
        $train = if ($name -in (@(Get-DesktopAssetNames $Version) + @(Get-DesktopAliasAssetNames $Version))) { 'desktop' } else { 'agent' }
        $path = if ($train -eq 'desktop' -and $name -like 'Pointframe-*-Setup.exe') { "installer/output/$name" } else { "packaging/output/$name" }
        [pscustomobject]@{ Name = $name; Train = $train; Path = $path }
    }
    return @($result)
}

function Get-TrainVersionFromRelease($Release)
{
    $version = ConvertTo-ReleaseVersion ([string](Get-ModelProperty $Release 'tag_name' ''))
    return $version
}

function Resolve-PublishSelection
{
    param([string]$Version, [string[]]$Trains, [object[]]$Releases)
    $wanted = [version]$Version
    $stale = [Collections.Generic.List[string]]::new()
    foreach ($train in $Trains)
    {
        $higher = @($Releases | Where-Object {
            $versionTag = ConvertTo-ReleaseVersion ([string](Get-ModelProperty $_ 'tag_name' ''))
            $complete = if ($train -eq 'desktop') { Test-TrainRelease $_ 'desktop' } else { Test-StrictAgentRelease $_ }
            $null -ne $versionTag -and $versionTag -gt $wanted -and $complete
        })
        if ($higher.Count -gt 0) { $stale.Add($train) }
    }
    $active = @($Trains | Where-Object { $_ -notin $stale })
    return [pscustomobject]@{ Active = $active; Stale = $stale.ToArray() }
}

function Test-StrictAgentRelease($Release)
{
    if (-not (Test-ReleasePublished $Release)) { return $false }
    $version = ConvertTo-ReleaseVersion ([string](Get-ModelProperty $Release 'tag_name' ''))
    if ($null -eq $version) { return $false }
    $names = @(Get-ReleaseAssetNames $Release)
    foreach ($asset in Get-TrainAssetNames $version.ToString() @('agent')) { if ($asset -cnotin $names) { return $false } }
    return $true
}

function Get-StrictAgentSource
{
    param([object[]]$Releases)
    $complete = @($Releases | Where-Object { Test-StrictAgentRelease $_ } | Sort-Object { ConvertTo-ReleaseVersion ([string]$_.tag_name) } -Descending)
    if ($complete.Count -eq 0) { throw 'No complete published agent release exists with all five versioned agent assets for alias carry-forward.' }
    return $complete[0]
}

function Get-ChecksumHash
{
    param([string]$Path, [string]$ExpectedLeaf)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Missing checksum file: $Path" }
    $line = (Get-Content -LiteralPath $Path -Raw).Trim()
    if ($line -notmatch '^([0-9a-fA-F]{64})\s+\*?(.+)$') { throw "Invalid SHA-256 file: $Path" }
    if ([IO.Path]::GetFileName($Matches[2]) -cne $ExpectedLeaf) { throw "Checksum file $Path names '$($Matches[2])', expected '$ExpectedLeaf'." }
    return $Matches[1].ToLowerInvariant()
}

function Get-ServerPinnedHash
{
    param([string]$Path)
    $document = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    $hash = [string]$document.packages[0].fileSha256
    if (-not $hash) { $hash = [string]$document.packages[0].fileSha256 }
    if ($hash -notmatch '^[0-9a-fA-F]{64}$') { throw "MCP server metadata $Path has no valid package fileSha256." }
    return $hash.ToLowerInvariant()
}

function Assert-AgentPairIntegrity
{
    param([string]$ZipPath, [string]$ZipChecksumPath, [string]$McpbPath, [string]$McpbChecksumPath, [string]$ServerJsonPath)
    $zipLeaf = Split-Path -Leaf $ZipPath
    $mcpLeaf = Split-Path -Leaf $McpbPath
    $zipExpected = Get-ChecksumHash $ZipChecksumPath $zipLeaf
    $mcpExpected = Get-ChecksumHash $McpbChecksumPath $mcpLeaf
    $zipActual = (Get-FileHash -LiteralPath $ZipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $mcpActual = (Get-FileHash -LiteralPath $McpbPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($zipActual -cne $zipExpected) { throw "Agent source zip SHA-256 mismatch for $zipLeaf." }
    if ($mcpActual -cne $mcpExpected) { throw "Agent source MCPB SHA-256 mismatch for $mcpLeaf." }
    if ($mcpActual -cne (Get-ServerPinnedHash $ServerJsonPath)) { throw "Agent source MCPB does not match server.json for $mcpLeaf." }
}

function Get-ReleaseAssetUrl($Release, [string]$Name)
{
    $asset = @(Get-ModelProperty $Release 'assets' @() | Where-Object { [string](Get-ModelProperty $_ 'name' '') -ceq $Name } | Select-Object -First 1)
    if ($asset.Count -eq 0) { throw "Agent source $($Release.tag_name) is missing $Name." }
    return [string](Get-ModelProperty $asset[0] 'browser_download_url' '')
}

function Resolve-ExistingAliasSourceVersion
{
    param($Release, [object[]]$Releases, [string]$Version, [string]$Root, [scriptblock]$Gh, [string]$Repository)
    $directory = Join-Path $Root '.existing-alias-source'
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $aliasName = 'Pointframe.Cli-win-x64.zip.sha256'
    $downloadArgs = @('release', 'download', [string]$Release.tag_name, '--pattern', $aliasName, '--dir', $directory, '--clobber')
    $null = & $Gh -Arguments $downloadArgs
    if ($LASTEXITCODE -ne 0) { throw "Could not read $aliasName from published release $($Release.tag_name)." }
    $aliasHash = Get-ChecksumHash (Join-Path $directory $aliasName) 'Pointframe.Cli-win-x64.zip'
    $completeCandidates = @($Releases | Where-Object { Test-StrictAgentRelease $_ } | Sort-Object { ConvertTo-ReleaseVersion ([string]$_.tag_name) } -Descending)
    foreach ($candidate in $completeCandidates)
    {
        $candidateVersion = (ConvertTo-ReleaseVersion ([string]$candidate.tag_name)).ToString()
        $checksumName = "Pointframe.Cli-$candidateVersion-win-x64.zip.sha256"
        $candidateArgs = @('release', 'download', [string]$candidate.tag_name, '--pattern', $checksumName, '--dir', $directory, '--clobber')
        $null = & $Gh -Arguments $candidateArgs
        if ($LASTEXITCODE -ne 0) { throw "Could not read $checksumName from $($candidate.tag_name)." }
        if ((Get-ChecksumHash (Join-Path $directory $checksumName) "Pointframe.Cli-$candidateVersion-win-x64.zip") -ceq $aliasHash) { return $candidateVersion }
    }
    throw "Could not identify the agent source for aliases in published desktop release $($Release.tag_name)."
}

function Copy-AliasSource
{
    param([string]$Version, [string]$SourceVersion, [string]$Root, [object]$Release, [scriptblock]$Gh, [string]$Repository)
    $out = Join-Path $Root 'packaging/output'
    New-Item -ItemType Directory -Path $out -Force | Out-Null
    if (-not $Release)
    {
        $zip = Join-Path $out "Pointframe.Cli-$SourceVersion-win-x64.zip"
        $zipSha = "$zip.sha256"
        $mcpb = Join-Path $out "Pointframe.Mcp-$SourceVersion-win-x64.mcpb"
        $mcpbSha = "$mcpb.sha256"
        $server = Join-Path $out "Pointframe.Mcp-$SourceVersion-win-x64.server.json"
    }
    else
    {
        $downloadDir = Join-Path $Root '.alias-source'
        New-Item -ItemType Directory -Path $downloadDir -Force | Out-Null
        $names = @("Pointframe.Cli-$SourceVersion-win-x64.zip", "Pointframe.Cli-$SourceVersion-win-x64.zip.sha256", "Pointframe.Mcp-$SourceVersion-win-x64.mcpb", "Pointframe.Mcp-$SourceVersion-win-x64.mcpb.sha256", "Pointframe.Mcp-$SourceVersion-win-x64.server.json")
        foreach ($name in $names)
        {
            $url = Get-ReleaseAssetUrl $Release $name
            if ($url) { $downloadArgs = @('release', 'download', "v$SourceVersion", '--pattern', $name, '--dir', $downloadDir, '--clobber'); $null = & $Gh -Arguments $downloadArgs }
            else { throw "Agent source $($Release.tag_name) has no download URL for $name." }
        }
        $zip = Join-Path $downloadDir $names[0]; $zipSha = Join-Path $downloadDir $names[1]
        $mcpb = Join-Path $downloadDir $names[2]; $mcpbSha = Join-Path $downloadDir $names[3]; $server = Join-Path $downloadDir $names[4]
    }
    Assert-AgentPairIntegrity $zip $zipSha $mcpb $mcpbSha $server
    $zipAlias = Join-Path $out 'Pointframe.Cli-win-x64.zip'
    $mcpAlias = Join-Path $out 'Pointframe.Mcp-win-x64.mcpb'
    Copy-Item -LiteralPath $zip -Destination $zipAlias -Force
    Copy-Item -LiteralPath $mcpb -Destination $mcpAlias -Force
    $zipHash = (Get-FileHash -LiteralPath $zipAlias -Algorithm SHA256).Hash.ToLowerInvariant()
    $mcpHash = (Get-FileHash -LiteralPath $mcpAlias -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath "$zipAlias.sha256" -Value "$zipHash  $(Split-Path -Leaf $zipAlias)" -NoNewline -Encoding utf8NoBOM
    Set-Content -LiteralPath "$mcpAlias.sha256" -Value "$mcpHash  $(Split-Path -Leaf $mcpAlias)" -NoNewline -Encoding utf8NoBOM
}

function Write-PublishResult
{
    param([string]$Path, $Result)
    if (-not $Path) { return }
    $directory = Split-Path -Parent $Path
    if ($directory) { New-Item -ItemType Directory -Path $directory -Force | Out-Null }
    $Result | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $Path -Encoding utf8NoBOM
}

function Publish-ReleasePlan
{
    param([string]$Version, [string[]]$Trains, [string]$Commit, [string]$Repository, [string]$Root, [scriptblock]$Gh, [string]$ResultOut)
    $tag = "v$Version"
    $listing = @(Read-GhReleaseListing $Gh $Repository)
    $selection = Resolve-PublishSelection $Version $Trains $listing
    $states = [ordered]@{ desktop = 'not-selected'; agent = 'not-selected' }
    foreach ($train in $selection.Stale) { $states[$train] = 'stale' }
    if ($selection.Active.Count -eq 0) {
        $result = [pscustomobject]@{ tag = $tag; trains = $states; publication = 'skipped'; aliasSourceVersion = $null; published = $false; skippedComplete = $false }
        Write-PublishResult $ResultOut $result
        return $result
    }

    $existing = @($listing | Where-Object { [string]$_.tag_name -ceq $tag } | Select-Object -First 1)
    if ($existing.Count -gt 0 -and -not [bool]$existing[0].draft)
    {
        $assets = @(Get-ReleaseAssetNames $existing[0])
        $expected = @(Get-TrainAssetNames $Version $selection.Active)
        $missing = @($expected | Where-Object { $_ -notin $assets })
        if ($missing.Count -gt 0) { throw "Published release $tag is missing selected assets: $($missing -join ', '). Refusing to upload into a published release." }
        foreach ($train in $selection.Active) { $states[$train] = 'complete' }
        $aliasSource = if ('desktop' -in $selection.Active -and 'agent' -in $selection.Active) { $Version } elseif ('desktop' -in $selection.Active) { Resolve-ExistingAliasSourceVersion $existing[0] $listing $Version $Root $Gh $Repository } else { $null }
        $result = [pscustomobject]@{ tag = $tag; trains = $states; publication = 'published'; aliasSourceVersion = $aliasSource; published = $false; skippedComplete = $true }
        Write-PublishResult $ResultOut $result
        return $result
    }

    $agentRelease = $null
    $aliasSourceVersion = $Version
    if ('agent' -in $selection.Active)
    {
        $agentRelease = $null
    }
    elseif ('desktop' -in $selection.Active)
    {
        $agentRelease = Get-StrictAgentSource $listing
        if (-not $agentRelease) { throw 'No complete published agent release exists for alias carry-forward.' }
        $aliasSourceVersion = (ConvertTo-ReleaseVersion ([string]$agentRelease.tag_name)).ToString()
    }
    if ('desktop' -in $selection.Active)
    {
        if ('agent' -in $selection.Active)
        {
            $zip = Join-Path $Root "packaging/output/Pointframe.Cli-$Version-win-x64.zip"
            $zipSha = "$zip.sha256"
            $mcpb = Join-Path $Root "packaging/output/Pointframe.Mcp-$Version-win-x64.mcpb"
            $mcpbSha = "$mcpb.sha256"
            $server = Join-Path $Root "packaging/output/Pointframe.Mcp-$Version-win-x64.server.json"
            Assert-AgentPairIntegrity $zip $zipSha $mcpb $mcpbSha $server
            Copy-AliasSource $Version $Version $Root $null $Gh $Repository
        }
        else { Copy-AliasSource $Version $aliasSourceVersion $Root $agentRelease $Gh $Repository }
    }

    $files = @(Get-PublishTrains $Version $selection.Active)
    foreach ($file in $files)
    {
        if (-not (Test-Path -LiteralPath (Join-Path $Root $file.Path) -PathType Leaf)) { throw "Missing selected release asset: $($file.Path)" }
    }
    $notes = @("Pointframe $Version", '', "Contains: $($selection.Active -join ' and ') product train(s).")
    if ('desktop' -in $selection.Active -and 'agent' -notin $selection.Active) { $notes += "Legacy CLI and MCP aliases are copied from agent release v$aliasSourceVersion." }
    $notes += ''; $notes += 'For the full list of commits, see Compare in this release page.'

    if ($existing.Count -gt 0 -and [bool]$existing[0].draft)
    {
        $id = [string](Get-ModelProperty $existing[0] 'id' '')
        if (-not $id) { throw "Draft $tag has no release id and cannot be safely replaced." }
        $deleteArgs = @('api', "repos/$Repository/releases/$id", '-X', 'DELETE')
        $null = & $Gh -Arguments $deleteArgs
    }
    $ghArguments = @('release', 'create', $tag) + @($files | ForEach-Object { Join-Path $Root $_.Path }) + @('--draft', '--title', "Pointframe $Version", '--notes', ($notes -join "`n"), '--target', $Commit)
    $null = & $Gh -Arguments $ghArguments
    if ($LASTEXITCODE -ne 0) { throw "Could not create draft $tag." }
    $created = @(Read-GhReleaseListing $Gh $Repository | Where-Object { [string](Get-ModelProperty $_ 'tag_name' '') -ceq $tag -and [bool](Get-ModelProperty $_ 'draft' $false) } | Select-Object -First 1)
    if ($created.Count -eq 0) { throw "Could not read back draft $tag by id from the releases listing." }
    $created = $created[0]
    if (-not [bool](Get-ModelProperty $created 'draft' $false)) { throw "Release $tag was not created as a draft." }
    foreach ($train in $selection.Active) { $states[$train] = 'draft' }
    Write-PublishResult $ResultOut ([pscustomobject]@{ tag = $tag; trains = $states; publication = 'draft'; aliasSourceVersion = $aliasSourceVersion; published = $false; skippedComplete = $false; releaseId = [string](Get-ModelProperty $created 'id' '') })
    $expectedNames = @(Get-TrainAssetNames $Version $selection.Active | Sort-Object -Unique)
    $draftNames = @(Get-ReleaseAssetNames $created | Sort-Object -Unique)
    if (Compare-Object $expectedNames $draftNames) { throw "Draft $tag asset set differs from expected: expected [$($expectedNames -join ', ')], got [$($draftNames -join ', ')]." }
    $downloadDir = Join-Path $Root '.draft-validation'
    New-Item -ItemType Directory -Path $downloadDir -Force | Out-Null
    foreach ($file in $files)
    {
        $downloadArgs = @('release', 'download', $tag, '--pattern', $file.Name, '--dir', $downloadDir, '--clobber')
        $null = & $Gh -Arguments $downloadArgs
        if ($LASTEXITCODE -ne 0) { throw "Could not download draft asset $($file.Name) for validation." }
        $localHash = (Get-FileHash -LiteralPath (Join-Path $Root $file.Path) -Algorithm SHA256).Hash.ToLowerInvariant()
        $uploadedPath = Join-Path $downloadDir $file.Name
        if (-not (Test-Path -LiteralPath $uploadedPath) -or (Get-FileHash -LiteralPath $uploadedPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $localHash) { throw "Draft asset $($file.Name) does not hash equal to the local file." }
    }
    $releaseId = [string](Get-ModelProperty $created 'id' '')
    if (-not $releaseId) { throw "Draft $tag has no release id." }
    $publishBody = Join-Path $Root '.publish-release-request.json'
    @{ draft = $false; make_latest = 'false' } | ConvertTo-Json -Compress | Set-Content -LiteralPath $publishBody -Encoding utf8NoBOM
    $publishArgs = @('api', "repos/$Repository/releases/$releaseId", '-X', 'PATCH', '--input', $publishBody)
    $published = & $Gh -Arguments $publishArgs
    if ($LASTEXITCODE -ne 0) { throw "Could not publish validated draft $tag." }
    foreach ($train in $selection.Active) { $states[$train] = 'published' }
    $result = [pscustomobject]@{ tag = $tag; trains = $states; publication = 'published'; aliasSourceVersion = $aliasSourceVersion; published = $true; skippedComplete = $false; releaseId = $releaseId }
    Write-PublishResult $ResultOut $result
    return $result
}

function New-FakeGh
{
    param([hashtable]$State)
    $releaseList = [Collections.Generic.List[object]]::new()
    foreach ($release in @($State.releases)) { $releaseList.Add($release) }
    $State.releases = $releaseList
    return {
        param([string[]]$Arguments)
        $global:LASTEXITCODE = 0
        $State.calls.Add(($Arguments -join '|'))
        if ($Arguments[0] -eq 'api' -and $Arguments[1] -match '/releases\?') { return [pscustomobject]@{ data = $State.releases.ToArray() } }
        if ($Arguments[0] -eq 'api' -and $Arguments[1] -match '/releases/[^/]+$' -and [Array]::IndexOf($Arguments, '-X') -ge 0 -and $Arguments[[Array]::IndexOf($Arguments, '-X') + 1] -eq 'DELETE')
        {
            $id = ($Arguments[1] -split '/')[-1]
            for ($index = $State.releases.Count - 1; $index -ge 0; $index--) { if ([string]$State.releases[$index].id -ceq $id) { $State.releases.RemoveAt($index) } }
            return $null
        }
        if ($Arguments[0] -eq 'release' -and $Arguments[1] -eq 'create')
        {
            $assets = @($Arguments | Where-Object { $_ -match '\.(exe|zip|sha256|mcpb|json)$' } | ForEach-Object { [pscustomobject]@{ name = Split-Path -Leaf $_ } })
            $State.draft = [pscustomobject]@{ id = 'draft-1'; tag_name = $Arguments[2]; draft = $true; prerelease = $false; assets = $assets }
            $State.releases.Add($State.draft)
            return $null
        }
        if ($Arguments[0] -eq 'api' -and $Arguments[1] -match '/releases/tags/' -and $State.draft -and $State.draft.draft) { $global:LASTEXITCODE = 1; return $null }
        if ($Arguments[0] -eq 'release' -and $Arguments[1] -eq 'download')
        {
            $name = $Arguments[[Array]::IndexOf($Arguments, '--pattern') + 1]
            $dir = $Arguments[[Array]::IndexOf($Arguments, '--dir') + 1]
            $asset = Get-ChildItem $State.root -Recurse -File | Where-Object { $_.Name -CEQ $name -and $_.DirectoryName -notlike '*\.alias-source' -and $_.DirectoryName -notlike '*\.draft-validation' } | Select-Object -First 1
            if ($State.badAsset -eq $name) { Set-Content -LiteralPath (Join-Path $dir $name) -Value 'tampered upload' -NoNewline }
            elseif ($asset) { Copy-Item $asset.FullName (Join-Path $dir $name) -Force }
            else { $global:LASTEXITCODE = 1 }
            return $null
        }
        if ($Arguments[0] -eq 'api' -and [Array]::IndexOf($Arguments, '-X') -ge 0 -and $Arguments[[Array]::IndexOf($Arguments, '-X') + 1] -eq 'PATCH')
        {
            $bodyPath = $Arguments[[Array]::IndexOf($Arguments, '--input') + 1]
            $body = Get-Content -LiteralPath $bodyPath -Raw | ConvertFrom-Json
            if ($body.draft -cne $false -or $body.make_latest -isnot [string] -or $body.make_latest -cne 'false') { throw 'Publish PATCH body must have draft=false and string make_latest="false".' }
            $State.patchBody = $body
            $State.draft.draft = $false
            $State.draft | Add-Member -NotePropertyName published_at -NotePropertyValue '2026-10-24T12:00:00Z' -Force
            $State.releases.Add($State.draft)
            return [pscustomobject]@{ data = $State.draft }
        }
        return $null
    }.GetNewClosure()
}

function Invoke-PublishSelfTest
{
    $version = '1.2.3'; $commit = ('a' * 40); $cases = 0
    $fixtureDirectory = Join-Path $PSScriptRoot 'tests/publish-release'
    $aliasFixture = Get-Content (Join-Path $fixtureDirectory 'alias-source-selection.json') -Raw | ConvertFrom-Json
    $retryFixture = Get-Content (Join-Path $fixtureDirectory 'retry-cases.json') -Raw | ConvertFrom-Json
    $root = Join-Path ([IO.Path]::GetTempPath()) "publish-release-$([guid]::NewGuid().ToString('N'))"
    try
    {
        $createFile = {
            param([string]$Relative, [string]$Content = $Relative)
            $path = Join-Path $root $Relative
            New-Item -ItemType Directory -Path (Split-Path $path -Parent) -Force | Out-Null
            [IO.File]::WriteAllBytes($path, [Text.Encoding]::UTF8.GetBytes($Content))
            return $path
        }.GetNewClosure()
        foreach ($relative in @('installer/output/Pointframe-1.2.3-x64-Setup.exe', 'packaging/output/Pointframe.Cli-1.2.3-win-x64.zip', 'packaging/output/Pointframe.Mcp-1.2.3-win-x64.mcpb'))
        {
            $null = & $createFile $relative
        }
        $zip = Join-Path $root 'packaging/output/Pointframe.Cli-1.2.3-win-x64.zip'; $mcpb = Join-Path $root 'packaging/output/Pointframe.Mcp-1.2.3-win-x64.mcpb'
        foreach ($file in @($zip, $mcpb)) { $hash = (Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant(); Set-Content "$file.sha256" "$hash  $(Split-Path -Leaf $file)" -NoNewline }
        $server = Join-Path $root 'packaging/output/Pointframe.Mcp-1.2.3-win-x64.server.json'; $mcpHash = (Get-FileHash $mcpb -Algorithm SHA256).Hash.ToLowerInvariant(); @{ packages = @(@{ fileSha256 = $mcpHash }) } | ConvertTo-Json -Depth 5 | Set-Content $server
        $sourceVersion = [string]$aliasFixture.completeAgentVersion
        $sourceZip = & $createFile "source-assets/Pointframe.Cli-$sourceVersion-win-x64.zip" 'source CLI bytes'
        $sourceMcpb = & $createFile "source-assets/Pointframe.Mcp-$sourceVersion-win-x64.mcpb" 'source MCPB bytes'
        foreach ($file in @($sourceZip, $sourceMcpb)) { $hash = (Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant(); Set-Content "$file.sha256" "$hash  $(Split-Path -Leaf $file)" -NoNewline }
        $sourceServer = & $createFile "source-assets/Pointframe.Mcp-$sourceVersion-win-x64.server.json" (@{ packages = @(@{ fileSha256 = (Get-FileHash $sourceMcpb -Algorithm SHA256).Hash.ToLowerInvariant() }) } | ConvertTo-Json -Depth 5)
        $sourceAssetNames = @(Get-TrainAssetNames $sourceVersion @('agent'))
        $sourceRelease = [pscustomobject]@{ tag_name = "v$sourceVersion"; id = 'source-id'; draft = $false; prerelease = $false; published_at = '2026-10-01T00:00:00Z'; target_commitish = $commit; assets = @($sourceAssetNames | ForEach-Object { [pscustomobject]@{ name = $_; browser_download_url = "https://fake/$_" } }) }
        $desktopDecoy = [pscustomobject]@{ tag_name = "v$($aliasFixture.newerDesktopVersion)"; draft = $false; prerelease = $false; published_at = '2026-10-02T00:00:00Z'; assets = @([pscustomobject]@{ name = "Pointframe-$($aliasFixture.newerDesktopVersion)-x64-Setup.exe" }) }
        $incompleteAgent = [pscustomobject]@{ tag_name = "v$($aliasFixture.higherIncompleteAgentVersion)"; draft = $false; prerelease = $false; published_at = '2026-10-03T00:00:00Z'; assets = @(Get-AgentRequiredAssetNames $aliasFixture.higherIncompleteAgentVersion | ForEach-Object { [pscustomobject]@{ name = $_ } }) }
        $sourceSelection = Get-StrictAgentSource @($sourceRelease, $desktopDecoy, $incompleteAgent)
        if ($sourceSelection.tag_name -cne "v$($aliasFixture.expectedSourceVersion)") { throw 'Alias carry-forward did not select the newest complete agent release below the incomplete pair.' }
        $copyGhState = @{ calls = [Collections.Generic.List[object]]::new(); releases = @($sourceRelease, $desktopDecoy, $incompleteAgent); root = $root }
        $copyGh = New-FakeGh $copyGhState
        Copy-AliasSource '2.0.0' $sourceVersion $root $sourceRelease $copyGh 'o/r'
        if ((Get-FileHash $sourceZip -Algorithm SHA256).Hash -cne (Get-FileHash (Join-Path $root 'packaging/output/Pointframe.Cli-win-x64.zip') -Algorithm SHA256).Hash -or (Get-FileHash $sourceMcpb -Algorithm SHA256).Hash -cne (Get-FileHash (Join-Path $root 'packaging/output/Pointframe.Mcp-win-x64.mcpb') -Algorithm SHA256).Hash) { throw 'Alias carry-forward did not preserve source bytes.' }
        if ((Get-ChecksumHash "$sourceZip.sha256" (Split-Path -Leaf $sourceZip)) -cne (Get-ChecksumHash (Join-Path $root 'packaging/output/Pointframe.Cli-win-x64.zip.sha256') 'Pointframe.Cli-win-x64.zip') -or (Get-ChecksumHash "$sourceMcpb.sha256" (Split-Path -Leaf $sourceMcpb)) -cne (Get-ChecksumHash (Join-Path $root 'packaging/output/Pointframe.Mcp-win-x64.mcpb.sha256') 'Pointframe.Mcp-win-x64.mcpb')) { throw 'Alias SHA-256 values differ from the versioned source checksums.' }
        $cases++

        foreach ($mismatch in @('zip', 'mcpb', 'server'))
        {
            $badZip = Join-Path $root "source-assets/Pointframe.Cli-$sourceVersion-win-x64.zip.sha256"
            $badMcp = Join-Path $root "source-assets/Pointframe.Mcp-$sourceVersion-win-x64.mcpb.sha256"
            $badServer = $sourceServer
            $savedZip = Get-Content $badZip -Raw; $savedMcp = Get-Content $badMcp -Raw; $savedServer = Get-Content $sourceServer -Raw
            try
            {
                if ($mismatch -eq 'zip') { Set-Content $badZip ((('f' * 64) + '  Pointframe.Cli-1.1.0-win-x64.zip')) }
                if ($mismatch -eq 'mcpb') { Set-Content $badMcp ((('f' * 64) + '  Pointframe.Mcp-1.1.0-win-x64.mcpb')) }
                if ($mismatch -eq 'server') { @{ packages = @(@{ fileSha256 = ('f' * 64) }) } | ConvertTo-Json -Depth 5 | Set-Content $badServer }
                $mismatchState = @{ calls = [Collections.Generic.List[object]]::new(); releases = @($sourceRelease, $incompleteAgent); root = $root }
                try { Publish-ReleasePlan '2.0.0' @('desktop') $commit 'o/r' $root (New-FakeGh $mismatchState) | Out-Null; throw "Expected $mismatch hash mismatch." }
                catch { if ($_.Exception.Message -like "Expected $mismatch hash mismatch.") { throw } }
                if (@($mismatchState.calls | Where-Object { $_ -match 'release\|create|\|PATCH\|' }).Count -gt 0) { throw "The $mismatch mismatch reached a publish call." }
                $cases++
            }
            finally { Set-Content $badZip $savedZip -NoNewline; Set-Content $badMcp $savedMcp -NoNewline; Set-Content $badServer $savedServer -NoNewline }
        }

        $noSourceState = @{ calls = [Collections.Generic.List[object]]::new(); releases = @($incompleteAgent); root = $root }
        try { Publish-ReleasePlan '2.0.0' @('desktop') $commit 'o/r' $root (New-FakeGh $noSourceState) | Out-Null; throw 'Expected missing alias source failure.' }
        catch { if ($_.Exception.Message -notlike '*No complete published agent release exists*') { throw } }
        if (@($noSourceState.calls | Where-Object { $_ -match 'release\|create|\|PATCH\|' }).Count -gt 0) { throw 'Missing alias source reached a publish call.' }
        $cases++

        $assets = @(Get-PublishTrains $version @('desktop', 'agent'))
        if ($assets.Count -ne 10 -or @($assets | Where-Object Train -EQ desktop).Count -ne 5 -or @($assets | Where-Object Train -EQ agent).Count -ne 5) { throw 'Combined assets are not 5 desktop plus 5 agent.' }
        foreach ($plan in @(@{ trains = @(); count = 0 }, @{ trains = @('agent'); count = 5 }, @{ trains = @('desktop'); count = 5 }, @{ trains = @('desktop', 'agent'); count = 10 }))
        {
            if (@(Get-PublishTrains $version $plan.trains).Count -ne $plan.count) { throw "Wrong asset count for $($plan.trains -join ',')." }
            $state = @{ calls = [Collections.Generic.List[object]]::new(); releases = @($sourceRelease); root = $root }
            $fake = New-FakeGh $state
            $result = Publish-ReleasePlan $version $plan.trains $commit 'o/r' $root $fake
            if ($plan.count -eq 0)
            {
                if ($result.published -or @($state.calls | Where-Object { $_ -match 'release\|create' }).Count -gt 0) { throw 'The none plan created a release.' }
            }
            else
            {
                $expected = @(Get-TrainAssetNames $version $plan.trains | Sort-Object -Unique)
                $actual = @($state.draft.assets | ForEach-Object name | Sort-Object -Unique)
                if (Compare-Object $expected $actual) { throw "Draft assets differed for plan $($plan.trains -join ',')." }
                if ($plan.trains.Count -eq 1 -and $plan.trains[0] -eq 'agent' -and $result.trains.desktop -ne 'not-selected') { throw 'Agent-only run selected desktop promotion.' }
            }
            $cases++
        }
        $fakeState = @{ calls = [Collections.Generic.List[object]]::new(); releases = @($sourceRelease); root = $root }
        $gh = New-FakeGh $fakeState
        $result = Publish-ReleasePlan $version @('desktop', 'agent') $commit 'o/r' $root $gh
        if (-not $result.published -or @($fakeState.calls | Where-Object { $_ -match '\|--input\|' }).Count -ne 1) { throw 'Publish did not issue one JSON-body publish PATCH.' }
        $patchCall = [string]@($fakeState.calls | Where-Object { $_ -match '\|PATCH\|' } | Select-Object -Last 1)[0]
        if ($patchCall -notmatch '\|--input\|') { throw 'Publish PATCH did not send its JSON request body through --input.' }
        if ($fakeState.patchBody.draft -cne $false -or $fakeState.patchBody.make_latest -isnot [string] -or $fakeState.patchBody.make_latest -cne 'false') { throw 'Publish PATCH body did not preserve draft=false and string make_latest="false".' }
        $fakeState.draft.draft = $true
        $tagRead = & $gh -Arguments @('api', 'repos/o/r/releases/tags/v1.2.3')
        if ($null -ne $tagRead -or $LASTEXITCODE -ne 1) { throw 'Fake gh must return 404 for a draft read through releases/tags/<tag>.' }
        $fakeState.draft.draft = $false
        $cases++
        $rerun = Publish-ReleasePlan $version @('desktop', 'agent') $commit 'o/r' $root $gh
        if (-not $rerun.skippedComplete -or @($fakeState.calls | Where-Object { $_ -match 'release\|create' }).Count -ne 1) { throw 'Re-running a published version created it twice.' }
        $cases++
        $draftLeft = [pscustomobject]@{ id = 'old-draft'; tag_name = "v$version"; draft = $true; prerelease = $false; assets = @() }
        $draftState = @{ calls = [Collections.Generic.List[object]]::new(); releases = @($sourceRelease, $draftLeft); root = $root }
        $draftResult = Publish-ReleasePlan $version @('agent') $commit 'o/r' $root (New-FakeGh $draftState)
        if ($retryFixture.leftoverDraft -cne 'delete-and-recreate') { throw 'Retry fixture does not specify draft recreation.' }
        if (-not $draftResult.published -or @($draftState.calls | Where-Object { $_ -match 'releases/old-draft\|-X\|DELETE' }).Count -ne 1 -or @($draftState.calls | Where-Object { $_ -match 'release\|create' }).Count -ne 1) { throw 'A leftover draft was not deleted and recreated.' }
        $cases++

        $agentAssets = @(Get-TrainAssetNames $version @('agent') | ForEach-Object { [pscustomobject]@{ name = $_ } })
        $complete = [pscustomobject]@{ tag_name = "v$version"; id = 'complete-id'; draft = $false; prerelease = $false; published_at = '2026-10-20T00:00:00Z'; assets = $agentAssets }
        $completeState = @{ calls = [Collections.Generic.List[object]]::new(); releases = @($complete); root = $root }
        $completeResult = Publish-ReleasePlan $version @('agent') $commit 'o/r' $root (New-FakeGh $completeState)
        if ($retryFixture.publishedComplete -cne 'skip') { throw 'Retry fixture does not specify complete-release skip.' }
        if (-not $completeResult.skippedComplete -or @($completeState.calls | Where-Object { $_ -match 'release\|create|\|PATCH\|' }).Count -gt 0) { throw 'A published complete release was not skipped.' }
        $cases++

        $olderVersion = [string]$aliasFixture.olderCompleteAgentVersion
        $olderZip = & $createFile "source-assets/Pointframe.Cli-$olderVersion-win-x64.zip" 'older source CLI bytes'
        $olderMcpb = & $createFile "source-assets/Pointframe.Mcp-$olderVersion-win-x64.mcpb" 'older source MCPB bytes'
        foreach ($file in @($olderZip, $olderMcpb)) { $hash = (Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant(); Set-Content "$file.sha256" "$hash  $(Split-Path -Leaf $file)" -NoNewline }
        $olderServer = & $createFile "source-assets/Pointframe.Mcp-$olderVersion-win-x64.server.json" (@{ packages = @(@{ fileSha256 = (Get-FileHash $olderMcpb -Algorithm SHA256).Hash.ToLowerInvariant() }) } | ConvertTo-Json -Depth 5)
        $olderAssets = @(Get-TrainAssetNames $olderVersion @('agent') | ForEach-Object { [pscustomobject]@{ name = $_; browser_download_url = "https://fake/$_" } })
        $olderRelease = [pscustomobject]@{ tag_name = "v$olderVersion"; id = 'older-id'; draft = $false; prerelease = $false; published_at = '2026-09-30T00:00:00Z'; assets = $olderAssets }
        $oldZipHash = (Get-FileHash $olderZip -Algorithm SHA256).Hash.ToLowerInvariant()
        Copy-Item $olderZip (Join-Path $root 'packaging/output/Pointframe.Cli-win-x64.zip') -Force
        Set-Content (Join-Path $root 'packaging/output/Pointframe.Cli-win-x64.zip.sha256') "$oldZipHash  Pointframe.Cli-win-x64.zip" -NoNewline
        $desktopAssets = @(Get-TrainAssetNames $version @('desktop') | ForEach-Object { [pscustomobject]@{ name = $_ } })
        $desktopAssets += [pscustomobject]@{ name = 'Pointframe.Cli-win-x64.zip.sha256' }
        $desktopAssets += [pscustomobject]@{ name = 'Pointframe.Mcp-win-x64.mcpb.sha256' }
        $desktopAssets += [pscustomobject]@{ name = 'Pointframe.Cli-win-x64.zip' }
        $desktopAssets += [pscustomobject]@{ name = 'Pointframe.Mcp-win-x64.mcpb' }
        $desktopRerun = [pscustomobject]@{ tag_name = "v$version"; id = 'desktop-rerun'; draft = $false; prerelease = $false; assets = $desktopAssets }
        $desktopRerunState = @{ calls = [Collections.Generic.List[object]]::new(); releases = @($desktopRerun, $sourceRelease, $olderRelease); root = $root }
        $desktopRerunResult = Publish-ReleasePlan $version @('desktop') $commit 'o/r' $root (New-FakeGh $desktopRerunState)
        if (-not $desktopRerunResult.skippedComplete -or $desktopRerunResult.aliasSourceVersion -cne $olderVersion) { throw 'A complete desktop rerun did not identify its aliases from an older complete agent release.' }
        $cases++

        $incomplete = [pscustomobject]@{ tag_name = "v$version"; id = 'incomplete-id'; draft = $false; prerelease = $false; published_at = '2026-10-20T00:00:00Z'; assets = @([pscustomobject]@{ name = $agentAssets[0].name }) }
        if ($retryFixture.publishedIncomplete -cne 'fail') { throw 'Retry fixture does not specify incomplete-release failure.' }
        $incompleteState = @{ calls = [Collections.Generic.List[object]]::new(); releases = @($incomplete); root = $root }
        try { Publish-ReleasePlan $version @('agent') $commit 'o/r' $root (New-FakeGh $incompleteState) | Out-Null; throw 'Expected incomplete published release failure.' }
        catch { if ($_.Exception.Message -notlike '*missing selected assets*') { throw } }
        $cases++

        $higherAssets = @(Get-TrainAssetNames '1.3.0' @('agent') | ForEach-Object { [pscustomobject]@{ name = $_ } })
        $higher = [pscustomobject]@{ tag_name = 'v1.3.0'; draft = $false; prerelease = $false; published_at = '2026-10-21T00:00:00Z'; assets = $higherAssets }
        $staleState = @{ calls = [Collections.Generic.List[object]]::new(); releases = @($sourceRelease, $higher); root = $root }
        $staleResult = Publish-ReleasePlan $version @('agent') $commit 'o/r' $root (New-FakeGh $staleState)
        if ($retryFixture.higherAgent -cne 'stale-no-publish') { throw 'Retry fixture does not specify stale no-publish.' }
        if ($staleResult.trains.agent -ne 'stale' -or @($staleState.calls | Where-Object { $_ -match 'release\|create|\|PATCH\|' }).Count -gt 0) { throw 'A stale agent rerun published or changed Latest.' }
        $cases++

        $badUploadState = @{ calls = [Collections.Generic.List[object]]::new(); releases = @($sourceRelease); root = $root; badAsset = 'Pointframe.Cli-1.2.3-win-x64.zip' }
        if ($retryFixture.uploadedHashMismatch -cne 'fail-before-publish') { throw 'Retry fixture does not specify uploaded hash failure.' }
        try { Publish-ReleasePlan $version @('agent') $commit 'o/r' $root (New-FakeGh $badUploadState) | Out-Null; throw 'Expected uploaded asset hash mismatch.' }
        catch { if ($_.Exception.Message -notlike '*does not hash equal*') { throw } }
        if (@($badUploadState.calls | Where-Object { $_ -match '\|PATCH\|' }).Count -gt 0) { throw 'A bad upload was published.' }
        $cases++
        $names = @(Get-TrainAssetNames $version @('desktop', 'agent'))
        if ($names.Count -ne 10 -or 'Pointframe.Cli-win-x64.zip' -notin $names) { throw 'Shared train asset names are wrong.' }
        $cases++
    }
    finally { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue }
    Write-Host "publish-release self-test passed: $cases cases, fake gh draft validation and publish request flags."
    return 0
}

if ($SelfTest) { exit (Invoke-PublishSelfTest) }
if ($Version -notmatch '^\d+\.\d+\.\d+$' -or $Commit -notmatch '^[0-9a-f]{40}$' -or -not $ResultOut) { [Console]::Error.WriteLine('-Version, -Commit, and -ResultOut are required.'); exit 2 }
$selectedTrains = @($Trains -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
if (@($selectedTrains | Where-Object { $_ -notin @('desktop', 'agent') }).Count -gt 0) { [Console]::Error.WriteLine('-Trains accepts desktop and agent.'); exit 2 }
$gh = {
    param([string[]]$Arguments)
    $output = @(& gh @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "gh $($Arguments -join ' ') failed: $($output -join ' ')" }
    if ($Arguments[0] -eq 'api')
    {
        if ([Array]::IndexOf($Arguments, '-X') -ge 0 -and $Arguments[[Array]::IndexOf($Arguments, '-X') + 1] -eq 'DELETE') { return $null }
        $text = ($output -join "`n").Trim()
        if (-not $text) { throw "gh api $($Arguments[1]) returned no JSON." }
        return [pscustomobject]@{ data = ($text | ConvertFrom-Json) }
    }
    return ($output -join "`n")
}
try
{
    $result = Publish-ReleasePlan $Version $selectedTrains $Commit $Repository $Root $gh $ResultOut
    Write-PublishResult $ResultOut $result
    $result | ConvertTo-Json -Compress -Depth 8 | Write-Output
    exit 0
}
catch
{
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
