#Requires -Version 7.0
<#
The newest agent release: the highest published release that has the CLI zip, its .sha256 and the MCPB. GitHub Latest is
a desktop release from the release-train migration on (D-019), so nothing that needs the CLI/MCP pair may read Latest.

Dot-sourced (by trial.ps1, weekly-trial.ps1, check-released-desktop.ps1, test-agent-onboarding.ps1 and
docs/knowledge-base/hooks/pin.ps1) it only defines functions: no parameters, no output, no settings changed. Run directly
it is a command (arguments are read from $args so that dot-sourcing never binds or resets a caller's variable):

  pwsh scripts/agent-release.ps1                            # prints x.y.z; needs gh signed in or GITHUB_TOKEN
  pwsh scripts/agent-release.ps1 -ReleasesFile <fixture>    # same, from a fixture file (never touches GitHub)
  pwsh scripts/agent-release.ps1 -Repository owner/name -MaxPages 50
  pwsh scripts/agent-release.ps1 -SelfTest                  # offline: scripts/tests/agent-release fixtures, paging, the pin hook, the install one-liner

Exit codes: 0 found, 1 no complete agent release or a read failure (the message says why), 2 bad arguments.

Selection is pure (Get-NewestAgentRelease, over release-model.ps1 Test-TrainRelease 'agent', ordered by numeric version,
never creation order). The gh layer (Read-ReleaseListing) pages repos/<repo>/releases?per_page=100 and is never run by the
self-test; a -ReleasesFile fixture (an array, {releases: [...]} or {pages: [[...], [...]]}) replaces it.
#>

. (Join-Path $PSScriptRoot 'release-model.ps1')

function Get-NewestAgentRelease
{
    # The highest complete agent release of the listing. Throws when there is none.
    param([object[]]$Releases)

    $newest = Get-TrainBaseline $Releases 'agent'
    if ($null -eq $newest)
    {
        throw 'No complete agent release exists: none is published (not a draft or prerelease) with a numeric v<x.y.z> tag, Pointframe.Cli-<v>-win-x64.zip, its .sha256 and Pointframe.Mcp-<v>-win-x64.mcpb.'
    }

    return $newest
}

function Get-AgentReleaseVersion
{
    # x.y.z of a release object.
    param($Release)

    return (ConvertTo-ReleaseVersion ([string]$Release.tag_name)).ToString()
}

function Read-ReleaseListing
{
    # Every release of the repository (all pages), or the contents of a fixture file. gh must be signed in or GITHUB_TOKEN set
    # (scripts authenticate; only the CLI never sends a user's token).
    param([string]$Repository = 'dimitar-radenkov/Pointframe', [string]$ReleasesFile, [int]$MaxPages = 50)

    if ($ReleasesFile)
    {
        $fixture = Get-Content -LiteralPath $ReleasesFile -Raw | ConvertFrom-Json
        if ($null -ne $fixture.PSObject.Properties['pages'])
        {
            $pages = @($fixture.pages)
            $fetch = { param($number) $pages[$number - 1] }.GetNewClosure()
            return @(Get-AllPages -FetchPage $fetch -MaxPages $MaxPages)
        }

        if ($null -ne $fixture.PSObject.Properties['releases'])
        {
            return @($fixture.releases)
        }

        return @($fixture)
    }

    return @(Get-AllPages -MaxPages $MaxPages -FetchPage {
            param($page)
            $output = @(& gh api "repos/$Repository/releases?per_page=100&page=$page" 2>&1 | ForEach-Object { "$_" })
            $text = ($output -join "`n").Trim()
            if ($LASTEXITCODE -ne 0)
            {
                throw "gh api repos/$Repository/releases (page $page) failed: $text"
            }

            if ($text)
            {
                $text | ConvertFrom-Json
            }
        }.GetNewClosure())
}

function Resolve-NewestAgentRelease
{
    param([string]$Repository = 'dimitar-radenkov/Pointframe', [string]$ReleasesFile, [int]$MaxPages = 50)

    return Get-NewestAgentRelease (Read-ReleaseListing -Repository $Repository -ReleasesFile $ReleasesFile -MaxPages $MaxPages)
}

function Resolve-NewestAgentVersion
{
    param([string]$Repository = 'dimitar-radenkov/Pointframe', [string]$ReleasesFile, [int]$MaxPages = 50)

    return Get-AgentReleaseVersion (Resolve-NewestAgentRelease -Repository $Repository -ReleasesFile $ReleasesFile -MaxPages $MaxPages)
}

# ---------------------------------------------------------------------------------------------------------------------
# Offline self-test (only runs for -SelfTest; no GitHub, no network)
# ---------------------------------------------------------------------------------------------------------------------

function Assert-Throws
{
    param([scriptblock]$Action, [string]$Contains, [string]$Name)

    try
    {
        & $Action | Out-Null
    }
    catch
    {
        if ($_.Exception.Message -notlike "*$Contains*")
        {
            throw "${Name}: expected an error containing '$Contains', got '$($_.Exception.Message)'."
        }

        return
    }

    throw "${Name}: expected an error containing '$Contains', but it succeeded."
}

function New-ReleaseObject
{
    param([string]$Version, [string[]]$Assets)

    return [ordered]@{ tag_name = "v$Version"; draft = $false; prerelease = $false; published_at = '2026-10-01T10:00:00Z'; assets = $Assets }
}

function Test-ReaderFixtures
{
    $directory = Join-Path $PSScriptRoot 'tests/agent-release'
    $required = @(
        'combined-release', 'newest-over-desktop-only', 'older-created-later', 'draft-and-prerelease-ignored',
        'incomplete-pair-skipped', 'malformed-tags-ignored', 'no-complete-agent-release'
    )
    foreach ($name in $required)
    {
        if (-not (Test-Path -LiteralPath (Join-Path $directory "$name.json")))
        {
            throw "Missing fixture scripts/tests/agent-release/$name.json."
        }
    }

    $files = @(Get-ChildItem -LiteralPath $directory -Filter '*.json' | Sort-Object Name)
    foreach ($file in $files)
    {
        $fixture = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
        $releases = @(Read-ReleaseListing -ReleasesFile $file.FullName)
        $expectedError = Get-ModelProperty $fixture 'expectedError' $null
        if ($null -ne $expectedError)
        {
            Assert-Throws { Get-NewestAgentRelease $releases } $expectedError $file.Name
            Assert-Throws { Resolve-NewestAgentVersion -ReleasesFile $file.FullName } $expectedError "$($file.Name) (resolve)"
            continue
        }

        $got = Get-AgentReleaseVersion (Get-NewestAgentRelease $releases)
        if ($got -cne $fixture.expected)
        {
            throw "$($file.Name): expected agent version '$($fixture.expected)', got '$got'."
        }

        $resolved = Resolve-NewestAgentVersion -ReleasesFile $file.FullName
        if ($resolved -cne $fixture.expected)
        {
            throw "$($file.Name): Resolve-NewestAgentVersion gave '$resolved', expected '$($fixture.expected)'."
        }
    }

    return $files.Count
}

function Test-ReaderPaging
{
    $work = Join-Path ([IO.Path]::GetTempPath()) "agent-release-selftest-$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $work | Out-Null
    try
    {
        $desktopOnly = {
            param([int]$Count, [int]$Minor)
            1..$Count | ForEach-Object { New-ReleaseObject "6.$Minor.$_" @("Pointframe-6.$Minor.$_-x64-Setup.exe") }
        }

        # 101 releases over two pages: the only agent release is on page 2.
        $agent = New-ReleaseObject '6.2.0' @('Pointframe.Cli-6.2.0-win-x64.zip', 'Pointframe.Cli-6.2.0-win-x64.zip.sha256', 'Pointframe.Mcp-6.2.0-win-x64.mcpb')
        $pageOne = @(& $desktopOnly 100 1)
        $twoPages = Join-Path $work 'two-pages.json'
        ([ordered]@{ pages = @(, $pageOne; , @($agent)) } | ConvertTo-Json -Depth 8) | Set-Content -LiteralPath $twoPages -Encoding utf8
        $got = Resolve-NewestAgentVersion -ReleasesFile $twoPages
        if ($got -cne '6.2.0')
        {
            throw "paging beyond 100: expected 6.2.0 from page 2, got '$got'."
        }

        Assert-Throws { Get-NewestAgentRelease $pageOne } 'No complete agent release exists' 'reading only page 1 finds no agent release'

        # A listing that never gets short hits the hard cap, and the error names the cap.
        $threePages = Join-Path $work 'three-pages.json'
        ([ordered]@{ pages = @(, $pageOne; , @(& $desktopOnly 100 2); , @(& $desktopOnly 100 3)) } | ConvertTo-Json -Depth 8) | Set-Content -LiteralPath $threePages -Encoding utf8
        Assert-Throws { Resolve-NewestAgentVersion -ReleasesFile $threePages -MaxPages 2 } 'More than 2 pages' 'the page cap'

        Assert-Throws { Get-NewestAgentRelease @() } 'No complete agent release exists' 'an empty listing'
    }
    finally
    {
        Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
    }

    return 4
}

function Test-PinHook
{
    # The plugin pin hook follows the newest agent release, never a newer desktop-only release (run as the kit runs it: a child process).
    $repositoryRoot = Split-Path $PSScriptRoot -Parent
    $hook = Join-Path $repositoryRoot 'docs/knowledge-base/hooks/pin.ps1'
    $fixtures = Join-Path $PSScriptRoot 'tests/agent-release'
    $root = Join-Path ([IO.Path]::GetTempPath()) "agent-release-pin-$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path (Join-Path $root 'scripts') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $root 'plugin/pointframe') -Force | Out-Null
    try
    {
        foreach ($name in @('agent-release.ps1', 'release-model.ps1'))
        {
            Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $root 'scripts' $name)
        }

        Set-Content -LiteralPath (Join-Path $root 'scripts/update-plugin-pin.ps1') -Value 'exit 1' -Encoding utf8
        $lock = Join-Path $root 'plugin/pointframe/server.lock.json'
        $cases = @(
            @{ Pinned = '6.9.1'; Fixture = 'newest-over-desktop-only'; Action = 'Behind'; Exit = 10; Contains = $null }
            @{ Pinned = '6.9.2'; Fixture = 'newest-over-desktop-only'; Action = 'Behind'; Exit = 0; Contains = $null }
            @{ Pinned = '6.9.2'; Fixture = 'newest-over-desktop-only'; Action = 'Update'; Exit = 0; Contains = 'already v6.9.2' }
            @{ Pinned = '6.9.0'; Fixture = 'older-created-later'; Action = 'Behind'; Exit = 0; Contains = $null }
            @{ Pinned = '6.8.0'; Fixture = 'older-created-later'; Action = 'Behind'; Exit = 10; Contains = $null }
            @{ Pinned = '6.9.1'; Fixture = 'no-complete-agent-release'; Action = 'Behind'; Exit = 2; Contains = 'No complete agent release exists' }
        )
        foreach ($case in $cases)
        {
            Set-Content -LiteralPath $lock -Value "{ `"version`": `"$($case.Pinned)`" }" -Encoding utf8
            $arguments = @('-NoProfile', '-File', $hook, '-Action', $case.Action, '-Root', $root, '-ReleasesFile', (Join-Path $fixtures "$($case.Fixture).json"))
            $text = (@(& pwsh @arguments 2>&1 | ForEach-Object { "$_" })) -join "`n"
            if ($LASTEXITCODE -ne $case.Exit)
            {
                throw "pin hook $($case.Action) pinned $($case.Pinned) on $($case.Fixture): expected exit $($case.Exit), got $LASTEXITCODE ($text)."
            }

            if ($case.Contains -and $text -notlike "*$($case.Contains)*")
            {
                throw "pin hook $($case.Action) on $($case.Fixture): output lacks '$($case.Contains)' ($text)."
            }
        }

        return $cases.Count
    }
    finally
    {
        Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Test-InstallOneLiner
{
    # Runs the guide's real CLI install one-liner in a child shell against fake Invoke-RestMethod, Invoke-WebRequest and
    # Expand-Archive. Only the `install` call is replaced (the exe does not exist here). No network is touched.
    $repositoryRoot = Split-Path $PSScriptRoot -Parent
    $guide = Get-Content -LiteralPath (Join-Path $repositoryRoot 'Pointframe.Mcp/Guides/verify-desktop-work.md') -Raw
    $line = @($guide -split "`r?`n" | Where-Object { $_.TrimStart().StartsWith('pwsh -NoProfile -Command ''$d=Join-Path') })[0].Trim()
    $inner = $line.Substring($line.IndexOf("'") + 1, $line.LastIndexOf("'") - $line.IndexOf("'") - 1)
    $installCall = '& "$d\x\Pointframe.Cli.exe" install'
    if (-not $inner.Contains($installCall))
    {
        throw 'The install one-liner no longer runs `& "$d\x\Pointframe.Cli.exe" install`.'
    }

    $prelude = @'
function Invoke-RestMethod {
    param([Parameter(Position = 0)][string]$Uri, [hashtable]$Headers)
    Add-Content -LiteralPath $env:FAKE_LOG -Value "GET $Uri"
    if ($Headers['User-Agent'] -ne 'pointframe-install' -or $Headers['Accept'] -ne 'application/vnd.github+json' -or $Headers.ContainsKey('Authorization')) { throw 'unexpected API headers' }
    if ($env:FAKE_API -eq 'ratelimit') { throw 'Response status code does not indicate success: 403 (rate limit exceeded).' }
    if ($env:FAKE_API -eq 'network') { throw 'Unable to connect to the remote server' }
    $fixture = Get-Content -LiteralPath $env:FAKE_RELEASES -Raw | ConvertFrom-Json
    foreach ($release in @($fixture.releases)) {
        $release.assets = @($release.assets | ForEach-Object { [pscustomobject]@{ name = $_ } })
        $release
    }
}
function Invoke-WebRequest {
    param([Parameter(Position = 0)][string]$Uri, [string]$OutFile)
    Add-Content -LiteralPath $env:FAKE_LOG -Value "GET $Uri"
    if ($OutFile -like '*.sha256') {
        $hash = if ($env:FAKE_BADHASH) { '0' * 64 } else { (Get-FileHash -LiteralPath ($OutFile -replace '\.sha256$', '') -Algorithm SHA256).Hash }
        Set-Content -LiteralPath $OutFile -Value "$hash  asset.zip"
    }
    else { [IO.File]::WriteAllBytes($OutFile, [byte[]](1, 2, 3)) }
}
function Expand-Archive { param($Path, $DestinationPath) New-Item -ItemType Directory $DestinationPath -Force | Out-Null }

'@
    $directory = Join-Path ([IO.Path]::GetTempPath()) "agent-release-oneliner-$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $directory | Out-Null
    try
    {
        $scriptPath = Join-Path $directory 'run.ps1'
        Set-Content -LiteralPath $scriptPath -Value ($prelude + $inner.Replace($installCall, 'Write-Output "install-called"')) -Encoding utf8
        $fixtures = Join-Path $PSScriptRoot 'tests/agent-release'
        $download = 'https://github.com/dimitar-radenkov/Pointframe/releases/download'
        $alias = 'https://github.com/dimitar-radenkov/Pointframe/releases/latest/download/Pointframe.Cli-win-x64.zip'
        $cases = [System.Collections.Generic.List[hashtable]]::new()
        foreach ($file in Get-ChildItem -LiteralPath $fixtures -Filter '*.json')
        {
            $fixture = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
            if ($null -ne $fixture.PSObject.Properties['expected'])
            {
                $version = $fixture.expected
                $cases.Add(@{ Name = $file.BaseName; Fixture = $file.FullName; Api = ''; BadHash = ''; Exit = 0; Uri = "$download/v$version/Pointframe.Cli-$version-win-x64.zip"; Warning = $false; Shell = 'pwsh' })
            }
            else
            {
                $cases.Add(@{ Name = "$($file.BaseName) (first page has none)"; Fixture = $file.FullName; Api = ''; BadHash = ''; Exit = 0; Uri = $alias; Warning = $true; Shell = 'pwsh' })
            }
        }

        $first = Join-Path $fixtures 'newest-over-desktop-only.json'
        if (Get-Command powershell.exe -ErrorAction SilentlyContinue)
        {
            # Windows PowerShell 5.1 syntax rules: the same line must parse and behave there too.
            $cases.Add(@{ Name = 'Windows PowerShell 5.1'; Fixture = $first; Api = ''; BadHash = ''; Exit = 0; Uri = "$download/v6.9.2/Pointframe.Cli-6.9.2-win-x64.zip"; Warning = $false; Shell = 'powershell.exe' })
            $cases.Add(@{ Name = 'Windows PowerShell 5.1 rate limit'; Fixture = $first; Api = 'ratelimit'; BadHash = ''; Exit = 0; Uri = $alias; Warning = $true; Shell = 'powershell.exe' })
        }

        $cases.Add(@{ Name = 'rate limit'; Fixture = $first; Api = 'ratelimit'; BadHash = ''; Exit = 0; Uri = $alias; Warning = $true; Shell = 'pwsh' })
        $cases.Add(@{ Name = 'network error'; Fixture = $first; Api = 'network'; BadHash = ''; Exit = 0; Uri = $alias; Warning = $true; Shell = 'pwsh' })
        $cases.Add(@{ Name = 'hash mismatch'; Fixture = $first; Api = ''; BadHash = '1'; Exit = 1; Uri = "$download/v6.9.2/Pointframe.Cli-6.9.2-win-x64.zip"; Warning = $false; Shell = 'pwsh' })

        $savedTemp = $env:TEMP
        foreach ($case in $cases)
        {
            $log = Join-Path $directory 'requests.log'
            Remove-Item -LiteralPath $log -Force -ErrorAction SilentlyContinue
            $env:FAKE_LOG = $log
            $env:FAKE_RELEASES = $case.Fixture
            $env:FAKE_API = $case.Api
            $env:FAKE_BADHASH = $case.BadHash
            $env:TEMP = $directory
            try
            {
                $text = (@(& $case.Shell -NoProfile -File $scriptPath 2>&1 | ForEach-Object { "$_" })) -join "`n"
            }
            finally
            {
                $env:TEMP = $savedTemp
                foreach ($name in @('FAKE_LOG', 'FAKE_RELEASES', 'FAKE_API', 'FAKE_BADHASH'))
                {
                    Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue
                }
            }

            $requests = @(Get-Content -LiteralPath $log)
            if ($LASTEXITCODE -ne $case.Exit)
            {
                throw "install one-liner, $($case.Name): expected exit $($case.Exit), got $LASTEXITCODE ($text)."
            }

            if ($requests -notcontains "GET $($case.Uri)")
            {
                throw "install one-liner, $($case.Name): expected a request for $($case.Uri), made: $($requests -join ', ')."
            }

            if (($requests -contains "GET $alias") -and $case.Uri -ne $alias)
            {
                throw "install one-liner, $($case.Name): fell back to the latest alias although the API answered: $($requests -join ', ')."
            }

            if ($case.Warning -and $text -notlike '*WARNING*legacy latest-release download*')
            {
                throw "install one-liner, $($case.Name): the fallback must warn visibly ($text)."
            }

            if (-not $case.Warning -and $text -like '*WARNING*')
            {
                throw "install one-liner, $($case.Name): no warning expected ($text)."
            }

            if ($case.Exit -eq 0 -and $text -notlike '*install-called*pointframe.exe')
            {
                throw "install one-liner, $($case.Name): must run install and end with the exe path ($text)."
            }

            if ($case.Exit -ne 0 -and $text -notlike '*SHA-256 mismatch*')
            {
                throw "install one-liner, $($case.Name): a hash mismatch must stop with 'SHA-256 mismatch' ($text)."
            }
        }

        return $cases.Count
    }
    finally
    {
        Remove-Item -LiteralPath $directory -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Invoke-AgentReleaseMain
{
    param([object[]]$Arguments)

    $selfTest = $false
    $repository = 'dimitar-radenkov/Pointframe'
    $releasesFile = $null
    $maxPages = 50
    for ($i = 0; $i -lt $Arguments.Count; $i++)
    {
        switch -Regex ([string]$Arguments[$i])
        {
            '^-SelfTest$' { $selfTest = $true }
            '^-Repository$' { $i++; $repository = [string]$Arguments[$i] }
            '^-ReleasesFile$' { $i++; $releasesFile = [string]$Arguments[$i] }
            '^-MaxPages$' { $i++; $maxPages = [int]$Arguments[$i] }
            default { [Console]::Error.WriteLine("Unknown argument '$($Arguments[$i])'. Use -SelfTest, -Repository, -ReleasesFile or -MaxPages."); exit 2 }
        }
    }

    if ($selfTest)
    {
        try
        {
            $fixtureCount = Test-ReaderFixtures
            $pagingCount = Test-ReaderPaging
            $pinCount = Test-PinHook
            $oneLinerCount = Test-InstallOneLiner
        }
        catch
        {
            Write-Host "agent-release-selftest FAILED: $(($_.Exception.Message -split "`r?`n") -join ' | ')"
            exit 1
        }

        Write-Host "agent-release-selftest: $fixtureCount fixtures, $pagingCount paging and cap cases and $pinCount pin hook cases and $oneLinerCount install one-liner cases passed offline (newest agent over a newer desktop-only release, version order, drafts, prereleases, malformed tags, incomplete pairs, none found, paging beyond 100, rate-limit and network fallback, hash mismatch)."
        exit 0
    }

    try
    {
        Write-Output (Resolve-NewestAgentVersion -Repository $repository -ReleasesFile $releasesFile -MaxPages $maxPages)
        exit 0
    }
    catch
    {
        [Console]::Error.WriteLine($_.Exception.Message)
        exit 1
    }
}

# Run directly it is a command; dot-sourced (InvocationName '.') it only defines the functions above.
if ($MyInvocation.InvocationName -ne '.')
{
    Set-StrictMode -Version Latest
    $ErrorActionPreference = 'Stop'
    $PSNativeCommandUseErrorActionPreference = $false
    Invoke-AgentReleaseMain $args
}
