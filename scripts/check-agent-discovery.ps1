#Requires -Version 7.0
<#
Checks that what agents and directories read about Pointframe is consistent with the repository: the agent page,
llms.txt, the directory submission drafts, the README install commands, and the release assets the pipeline publishes.

  pwsh scripts/check-agent-discovery.ps1                # offline checks; exit 1 on any problem
  pwsh scripts/check-agent-discovery.ps1 -SelfTest      # run the checks on scripts/tests/agent-discovery fixtures
  pwsh scripts/check-agent-discovery.ps1 -Online        # also HEAD every absolute URL and compare with the latest release
  pwsh scripts/check-agent-discovery.ps1 -Snapshot      # read-only release download counts and traffic, as a table

Offline checks: the page, llms.txt, both drafts, the sitemap and pages.yml exist; every relative link and every
in-page anchor resolves; every github.com/dimitar-radenkov/Pointframe link is well formed and its README or blob
anchor exists; every code-block line on the page is a line of README.md or docs/cli/README.md, and the README
registration commands are on the page; version-free release asset names are published by cd.yml and have a versioned
counterpart in packaging/; every tool in the MCPB manifest is on the page and in both drafts; the page and llms.txt
are in the sitemap, the index, and pages.yml; the "no telemetry" claim holds for the MCP and CLI projects.

-Online and -Snapshot only send GET and HEAD requests (gh api GET). Nothing is posted, and nothing is submitted.
Prints one "ERROR ..." line per problem and exits 1; otherwise prints a summary and exits 0. Exit 2 on bad arguments.
#>
[CmdletBinding()]
param(
    [switch]$SelfTest,
    [switch]$Online,
    [switch]$Snapshot,
    [string]$Repository = 'dimitar-radenkov/Pointframe'
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$PagesBase = 'https://dimitar-radenkov.github.io/Pointframe/'
$RepoUrl = 'https://github.com/dimitar-radenkov/Pointframe'
$AgentPage = 'website/windows-screenshot-mcp-server.html'
$Llms = 'website/llms.txt'
$Sitemap = 'website/sitemap.xml'
$Index = 'website/index.html'
$Readme = 'README.md'
$CliReadme = 'docs/cli/README.md'
$CdWorkflow = '.github/workflows/cd.yml'
$PagesWorkflow = '.github/workflows/pages.yml'
$McpPackaging = 'packaging/build-mcp-package.ps1'
$CliPackaging = 'packaging/build-cli-package.ps1'
$Submissions = @(
    'packaging/directory-submissions/mcp-so-issue.txt',
    'packaging/directory-submissions/claude-local-extension-directory.txt'
)
$AgentTexts = @($AgentPage, $Llms) + $Submissions
$NumberWords = @{ one = 1; two = 2; three = 3; four = 4; five = 5; six = 6; seven = 7; eight = 8; nine = 9; ten = 10; eleven = 11; twelve = 12; thirteen = 13; fourteen = 14; fifteen = 15; sixteen = 16 }

function Read-RepoFile([string]$Root, [string]$RelativePath)
{
    $full = Join-Path $Root $RelativePath
    if (-not (Test-Path -LiteralPath $full -PathType Leaf))
    {
        return $null
    }
    (Get-Content -LiteralPath $full -Raw)
}

function Get-TrimmedLines([string]$Text)
{
    @($Text -split "`r?`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ })
}

function Get-HtmlCodeLines([string]$Html)
{
    $lines = [System.Collections.Generic.List[string]]::new()
    foreach ($match in [regex]::Matches($Html, '(?s)<pre[^>]*>\s*<code[^>]*>(.*?)</code>\s*</pre>'))
    {
        $decoded = [System.Net.WebUtility]::HtmlDecode($match.Groups[1].Value)
        foreach ($line in (Get-TrimmedLines $decoded))
        {
            $lines.Add($line)
        }
    }
    @($lines)
}

function Get-FencedLines([string]$Markdown)
{
    $lines = [System.Collections.Generic.List[string]]::new()
    $inFence = $false
    foreach ($raw in ($Markdown -split "`r?`n"))
    {
        if ($raw.Trim().StartsWith('```'))
        {
            $inFence = -not $inFence
            continue
        }
        if ($inFence -and $raw.Trim())
        {
            $lines.Add($raw.Trim())
        }
    }
    @($lines)
}

function Get-HeadingSlugs([string]$Markdown)
{
    $slugs = [System.Collections.Generic.HashSet[string]]::new()
    $inFence = $false
    foreach ($raw in ($Markdown -split "`r?`n"))
    {
        if ($raw.Trim().StartsWith('```'))
        {
            $inFence = -not $inFence
            continue
        }
        if ($inFence -or $raw -notmatch '^#{1,6}\s+(.+?)\s*#*\s*$')
        {
            continue
        }
        $title = ($Matches[1] -replace '[`*_]', '').ToLowerInvariant()
        $slug = ($title -replace '[^\p{L}\p{N} _-]', '') -replace ' ', '-'
        [void]$slugs.Add($slug)
    }
    $slugs
}

function Get-ToolNames([string]$PackagingScript)
{
    @([regex]::Matches($PackagingScript, '@\{ name = "([a-z_]+)"; description') | ForEach-Object { $_.Groups[1].Value })
}

function Add-Problem([System.Collections.Generic.List[string]]$Problems, [string]$Message)
{
    $Problems.Add("ERROR $Message")
}

function Test-FilesExist([string]$Root, [System.Collections.Generic.List[string]]$Problems)
{
    $required = @($AgentPage, $Llms, $Sitemap, $Index, $Readme, $CdWorkflow, $PagesWorkflow, $McpPackaging) + $Submissions
    foreach ($path in $required)
    {
        if (-not (Test-Path -LiteralPath (Join-Path $Root $path) -PathType Leaf))
        {
            Add-Problem $Problems "$path is missing."
        }
    }
}

function Test-RelativeLinks([string]$Root, [System.Collections.Generic.List[string]]$Problems)
{
    $page = Read-RepoFile $Root $AgentPage
    if ($page)
    {
        $ids = @([regex]::Matches($page, '\bid="([^"]+)"') | ForEach-Object { $_.Groups[1].Value })
        foreach ($match in [regex]::Matches($page, '(?:href|src)="([^"]+)"'))
        {
            $target = $match.Groups[1].Value
            if ($target -match '^(https?:|mailto:|data:)')
            {
                continue
            }
            if ($target.StartsWith('#'))
            {
                if ($ids -notcontains $target.Substring(1))
                {
                    Add-Problem $Problems "$AgentPage links to $target but no element has that id."
                }
                continue
            }
            $file = ($target -split '[#?]')[0]
            if (-not (Test-Path -LiteralPath (Join-Path $Root 'website' $file)))
            {
                Add-Problem $Problems "$AgentPage links to $target, which is not a file under website/."
            }
        }
    }

    $llms = Read-RepoFile $Root $Llms
    if ($llms)
    {
        foreach ($match in [regex]::Matches($llms, '\]\(([^)\s]+)\)'))
        {
            $target = $match.Groups[1].Value
            if ($target -match '^(https?:|mailto:)')
            {
                continue
            }
            $file = ($target -split '[#?]')[0]
            if (-not (Test-Path -LiteralPath (Join-Path $Root 'website' $file)))
            {
                Add-Problem $Problems "$Llms links to $target, which is not a file under website/."
            }
        }
    }

    foreach ($draft in $Submissions)
    {
        $text = Read-RepoFile $Root $draft
        if (-not $text)
        {
            continue
        }
        foreach ($match in [regex]::Matches($text, '\bpackaging/[A-Za-z0-9._/-]*[A-Za-z0-9_]'))
        {
            if (-not (Test-Path -LiteralPath (Join-Path $Root $match.Value)))
            {
                Add-Problem $Problems "$draft names $($match.Value), which does not exist."
            }
        }
    }
}

function Test-RepoLinkAnchor([string]$Root, [string]$RelativeFile, [string]$Fragment, [string]$Source, [System.Collections.Generic.List[string]]$Problems)
{
    $text = Read-RepoFile $Root $RelativeFile
    if ($null -eq $text)
    {
        Add-Problem $Problems "$Source links to $RelativeFile, which does not exist."
        return
    }
    if ($Fragment -and -not (Get-HeadingSlugs $text).Contains($Fragment))
    {
        Add-Problem $Problems "$Source links to $RelativeFile#$Fragment, but no heading there has that anchor."
    }
}

function Test-GitHubLinks([string]$Root, [System.Collections.Generic.List[string]]$Problems)
{
    foreach ($path in $AgentTexts)
    {
        $text = Read-RepoFile $Root $path
        if (-not $text)
        {
            continue
        }
        foreach ($match in [regex]::Matches($text, '(?i)https?://github\.com/dimitar-radenkov/[^\s"''<>)\]]*'))
        {
            $url = $match.Value
            $pattern = '^https://github\.com/dimitar-radenkov/Pointframe(?:\.git)?(?<rest>(?:/(?:releases(?:/latest(?:/download(?:/[A-Za-z0-9._-]+)?)?|/download/v[0-9][0-9.]*/[A-Za-z0-9._-]+|/tag/v[0-9][0-9.]*)?|issues(?:/[0-9]+)?|blob/master/[A-Za-z0-9._/-]+|tree/master(?:/[A-Za-z0-9._/-]+)?))?)(?:#(?<frag>[A-Za-z0-9_-]+))?$'
            if ($url -cnotmatch $pattern)
            {
                Add-Problem $Problems "$path has a malformed repository link: $url"
                continue
            }
            $fragment = $Matches['frag']
            $rest = $Matches['rest']
            if (-not $fragment)
            {
                continue
            }
            if ($rest -match '^/blob/master/(.+)$')
            {
                Test-RepoLinkAnchor $Root $Matches[1] $fragment $path $Problems
            }
            elseif (-not $rest)
            {
                Test-RepoLinkAnchor $Root $Readme $fragment $path $Problems
            }
        }
        if ($text -match '(?i)\bhttp://github\.com/')
        {
            Add-Problem $Problems "$path has an http:// GitHub link; use https."
        }
    }
}

function Test-InstallCommands([string]$Root, [System.Collections.Generic.List[string]]$Problems)
{
    $page = Read-RepoFile $Root $AgentPage
    $readme = Read-RepoFile $Root $Readme
    if (-not $page -or -not $readme)
    {
        return
    }
    $known = [System.Collections.Generic.HashSet[string]]::new([string[]]@(Get-TrimmedLines $readme))
    $cliReadme = Read-RepoFile $Root $CliReadme
    if ($cliReadme)
    {
        foreach ($line in (Get-TrimmedLines $cliReadme))
        {
            [void]$known.Add($line)
        }
    }

    $pageLines = @(Get-HtmlCodeLines $page)
    foreach ($line in $pageLines)
    {
        if (-not $known.Contains($line))
        {
            Add-Problem $Problems "$AgentPage code line is not in README.md: $line"
        }
    }

    $mustAppear = '^(claude mcp add|codex mcp add|pointframe mcp (install|doctor)|tar -xf|\.\\Pointframe\.Cli\.exe install$|Invoke-WebRequest)'
    foreach ($line in (Get-FencedLines $readme))
    {
        if ($line -match $mustAppear -and $line -notmatch '--dry-run' -and $pageLines -notcontains $line)
        {
            Add-Problem $Problems "$AgentPage is missing the README install command: $line"
        }
    }

    foreach ($path in @($Llms) + $Submissions)
    {
        $text = Read-RepoFile $Root $path
        if (-not $text)
        {
            continue
        }
        foreach ($line in (Get-TrimmedLines $text))
        {
            if ($line -match '^(claude|codex) mcp add' -and -not $known.Contains($line))
            {
                Add-Problem $Problems "$path install command is not in README.md: $line"
            }
        }
    }
}

function Test-ReleaseAssetNames([string]$Root, [System.Collections.Generic.List[string]]$Problems)
{
    $cd = Read-RepoFile $Root $CdWorkflow
    $mcpScript = Read-RepoFile $Root $McpPackaging
    $cliScript = Read-RepoFile $Root $CliPackaging
    if (-not $cd)
    {
        return
    }
    $assetPattern = 'Pointframe\.(?:Cli|Mcp)-win-x64\.(?:zip|mcpb)(?:\.sha256)?'
    $named = [System.Collections.Generic.HashSet[string]]::new()
    foreach ($path in (@($AgentTexts) + @($Readme)))
    {
        $text = Read-RepoFile $Root $path
        if (-not $text)
        {
            continue
        }
        foreach ($match in [regex]::Matches($text, "(?<![\w.-])$assetPattern(?![\w-])"))
        {
            [void]$named.Add($match.Value)
        }
    }
    foreach ($name in $named)
    {
        if ($cd -notmatch "packaging/output/$([regex]::Escape($name))")
        {
            Add-Problem $Problems "Asset $name is named in the docs but $CdWorkflow does not publish it."
        }
        $versioned = $name -replace '-win-x64', '-$Version-win-x64'
        $script = if ($name -like 'Pointframe.Mcp*') { $mcpScript } else { $cliScript }
        $base = $versioned -replace '\.sha256$', ''
        if ($script -and $script.IndexOf(($base -replace '\.(zip|mcpb)$', ''), [StringComparison]::Ordinal) -lt 0)
        {
            Add-Problem $Problems "Asset $name has no versioned counterpart ($base) built by packaging/."
        }
    }
    if ($named.Count -eq 0)
    {
        Add-Problem $Problems 'No version-free release asset name is referenced by the agent page, llms.txt, or the drafts.'
    }
}

function Test-ToolCoverage([string]$Root, [System.Collections.Generic.List[string]]$Problems)
{
    $script = Read-RepoFile $Root $McpPackaging
    if (-not $script)
    {
        return
    }
    $tools = @(Get-ToolNames $script)
    if ($tools.Count -eq 0)
    {
        Add-Problem $Problems "No tools found in the MCPB manifest in $McpPackaging."
        return
    }
    foreach ($path in @($AgentPage) + $Submissions)
    {
        $text = Read-RepoFile $Root $path
        if (-not $text)
        {
            continue
        }
        foreach ($tool in $tools)
        {
            if ($text -notmatch "(?<![a-z_])$tool(?![a-z_])")
            {
                Add-Problem $Problems "$path does not mention the tool $tool."
            }
        }
    }
    $page = Read-RepoFile $Root $AgentPage
    if ($page -and $page -match '>All (\w+) tools<')
    {
        $word = $Matches[1].ToLowerInvariant()
        if (-not $NumberWords.ContainsKey($word) -or $NumberWords[$word] -ne $tools.Count)
        {
            Add-Problem $Problems "$AgentPage says 'All $word tools' but the manifest lists $($tools.Count)."
        }
    }
}

function Test-Publishing([string]$Root, [System.Collections.Generic.List[string]]$Problems)
{
    $page = Read-RepoFile $Root $AgentPage
    $pageUrl = $PagesBase + (Split-Path $AgentPage -Leaf)
    if ($page)
    {
        if ($page -notmatch [regex]::Escape("rel=""canonical"" href=""$pageUrl"""))
        {
            Add-Problem $Problems "$AgentPage canonical link is not $pageUrl."
        }
        if ($page -notmatch 'href="\./llms\.txt"')
        {
            Add-Problem $Problems "$AgentPage does not link to ./llms.txt."
        }
    }
    $sitemap = Read-RepoFile $Root $Sitemap
    if ($sitemap -and $sitemap -notmatch "<loc>$([regex]::Escape($pageUrl))</loc>")
    {
        Add-Problem $Problems "$Sitemap does not list $pageUrl."
    }
    $index = Read-RepoFile $Root $Index
    if ($index -and $index -notmatch 'href="\./windows-screenshot-mcp-server\.html"')
    {
        Add-Problem $Problems "$Index does not link to the agent page."
    }
    $llms = Read-RepoFile $Root $Llms
    if ($llms)
    {
        if ($llms -notmatch '^# \S')
        {
            Add-Problem $Problems "$Llms must start with an H1 title."
        }
        if ($llms -notmatch '(?m)^> \S')
        {
            Add-Problem $Problems "$Llms needs a blockquote summary."
        }
        if ($llms -notmatch [regex]::Escape($pageUrl))
        {
            Add-Problem $Problems "$Llms does not link to the agent page $pageUrl."
        }
        if ($llms -notmatch '(?m)^## ')
        {
            Add-Problem $Problems "$Llms needs at least one link section."
        }
    }
    $pages = Read-RepoFile $Root $PagesWorkflow
    if ($pages -and $pages -notmatch "path:\s*['""]?\./website['""]?")
    {
        Add-Problem $Problems "$PagesWorkflow does not publish ./website, so llms.txt would not be deployed."
    }
}

function Test-TelemetryClaim([string]$Root, [System.Collections.Generic.List[string]]$Problems)
{
    $claimed = $false
    foreach ($path in $AgentTexts)
    {
        $text = Read-RepoFile $Root $path
        if ($text -and $text -match 'send no telemetry')
        {
            $claimed = $true
        }
    }
    if (-not $claimed)
    {
        Add-Problem $Problems 'No agent-facing file states that the MCP server and CLI send no telemetry.'
        return
    }
    foreach ($project in @('Pointframe.Mcp', 'Pointframe.Cli', 'Pointframe.Engine'))
    {
        $dir = Join-Path $Root $project
        if (-not (Test-Path -LiteralPath $dir))
        {
            continue
        }
        $hits = @(Get-ChildItem -LiteralPath $dir -Recurse -File -Include *.cs, *.csproj |
                Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } |
                Select-String -Pattern 'ApplicationInsights|OpenTelemetry|TelemetryClient' -List)
        foreach ($hit in $hits)
        {
            Add-Problem $Problems "The no-telemetry claim is false: $project references telemetry in $($hit.Path)."
        }
    }
}

function Invoke-OfflineChecks([string]$Root)
{
    $problems = [System.Collections.Generic.List[string]]::new()
    Test-FilesExist $Root $problems
    Test-RelativeLinks $Root $problems
    Test-GitHubLinks $Root $problems
    Test-InstallCommands $Root $problems
    Test-ReleaseAssetNames $Root $problems
    Test-ToolCoverage $Root $problems
    Test-Publishing $Root $problems
    Test-TelemetryClaim $Root $problems
    @($problems)
}

function Get-UrlStatus([string]$Url)
{
    foreach ($method in @('Head', 'Get'))
    {
        try
        {
            $response = Invoke-WebRequest -Uri $Url -Method $method -MaximumRedirection 5 -TimeoutSec 30 -SkipHttpErrorCheck
            if ([int]$response.StatusCode -lt 400)
            {
                return [int]$response.StatusCode
            }
            $last = [int]$response.StatusCode
        }
        catch
        {
            $last = 0
        }
    }
    $last
}

function Invoke-OnlineChecks([string]$Root)
{
    $problems = [System.Collections.Generic.List[string]]::new()
    $urls = [System.Collections.Generic.HashSet[string]]::new()
    $assets = [System.Collections.Generic.HashSet[string]]::new()
    foreach ($path in $AgentTexts)
    {
        $text = Read-RepoFile $Root $path
        if (-not $text)
        {
            continue
        }
        foreach ($match in [regex]::Matches($text, 'https://[A-Za-z0-9._/?=%&~+-]+'))
        {
            [void]$urls.Add($match.Value.TrimEnd('.', ',', ')'))
        }
        foreach ($match in [regex]::Matches($text, 'Pointframe\.(?:Cli|Mcp)-win-x64\.(?:zip|mcpb)(?:\.sha256)?'))
        {
            [void]$assets.Add($match.Value)
        }
    }
    foreach ($url in ($urls | Where-Object { -not $_.EndsWith('/download') } | Sort-Object))
    {
        $status = Get-UrlStatus $url
        if ($status -ge 400 -or $status -eq 0)
        {
            Add-Problem $problems "$url did not answer with success (HTTP $status; 0 means unreachable)."
        }
    }

    $json = & gh api "repos/$Repository/releases/latest" 2>&1
    if ($LASTEXITCODE -ne 0)
    {
        Add-Problem $problems "gh api releases/latest failed: $json"
        return @($problems)
    }
    $release = ($json -join "`n") | ConvertFrom-Json
    $published = @($release.assets | ForEach-Object { $_.name })
    foreach ($asset in $assets)
    {
        if ($published -notcontains $asset)
        {
            Add-Problem $problems "Latest release $($release.tag_name) has no asset named $asset."
        }
    }
    Write-Host "Latest release $($release.tag_name) has $($published.Count) asset(s)."
    @($problems)
}

function Get-AssetType([string]$Name)
{
    switch -Regex ($Name)
    {
        '\.mcpb\.sha256$' { return 'MCP bundle checksum' }
        '\.mcpb$' { return 'MCP bundle (.mcpb)' }
        '\.server\.json$' { return 'MCP registry metadata' }
        'Cli-.*\.zip\.sha256$' { return 'CLI zip checksum' }
        'Cli-.*\.zip$' { return 'CLI zip' }
        '\.exe$' { return 'Desktop installer (.exe)' }
        '\.sha256$' { return 'Other checksum' }
        default { return 'Other' }
    }
}

function Invoke-Snapshot
{
    $json = & gh api "repos/$Repository/releases?per_page=100" --paginate 2>&1
    if ($LASTEXITCODE -ne 0)
    {
        Write-Host "ERROR gh api releases failed: $json"
        return 1
    }
    $releases = @((($json -join "`n") -replace '\]\s*\[', ',') | ConvertFrom-Json)
    $rows = foreach ($release in $releases)
    {
        foreach ($asset in $release.assets)
        {
            [pscustomobject]@{ Release = $release.tag_name; Name = $asset.name; Type = Get-AssetType $asset.name; Downloads = [int]$asset.download_count }
        }
    }
    $rows = @($rows)
    Write-Host "Release downloads for $Repository ($($releases.Count) releases, $($rows.Count) assets)"
    $rows | Group-Object Type | ForEach-Object {
        [pscustomobject]@{ AssetType = $_.Name; Assets = $_.Count; Downloads = [int]($_.Group | Measure-Object Downloads -Sum).Sum }
    } | Sort-Object Downloads -Descending | Format-Table -AutoSize | Out-String | Write-Host

    $latest = @($releases | Where-Object { -not $_.draft -and -not $_.prerelease } | Select-Object -First 1)
    if ($latest.Count -gt 0)
    {
        Write-Host "Latest release $($latest[0].tag_name):"
        @($latest[0].assets | ForEach-Object { [pscustomobject]@{ Asset = $_.name; Downloads = [int]$_.download_count } }) |
            Format-Table -AutoSize | Out-String | Write-Host
    }

    foreach ($kind in @('views', 'clones'))
    {
        $traffic = & gh api "repos/$Repository/traffic/$kind" 2>&1
        if ($LASTEXITCODE -eq 0)
        {
            $data = ($traffic -join "`n") | ConvertFrom-Json
            Write-Host "Traffic $kind (last 14 days): $($data.count) total, $($data.uniques) unique"
        }
        else
        {
            Write-Host "Traffic $kind not available (needs push access): $(($traffic -join ' ').Trim())"
        }
    }
    0
}

function Copy-FixtureTree([string]$Source)
{
    $target = Join-Path ([IO.Path]::GetTempPath()) ("agent-discovery-" + [guid]::NewGuid().ToString('N'))
    Copy-Item -LiteralPath $Source -Destination $target -Recurse
    $target
}

function Edit-FixtureFile([string]$Root, [string]$RelativePath, [string]$Old, [string]$New)
{
    $full = Join-Path $Root $RelativePath
    $text = Get-Content -LiteralPath $full -Raw
    if (-not $text.Contains($Old))
    {
        throw "Self-test mutation target '$Old' not found in $RelativePath."
    }
    Set-Content -LiteralPath $full -Value $text.Replace($Old, $New) -NoNewline
}

function Invoke-SelfTest
{
    $good = Join-Path $PSScriptRoot 'tests' 'agent-discovery' 'good'
    $failures = [System.Collections.Generic.List[string]]::new()
    $cases = 0

    $baseline = @(Invoke-OfflineChecks $good)
    $cases++
    if ($baseline.Count -ne 0)
    {
        $failures.Add("good fixture should pass but reported: $($baseline -join ' | ')")
    }

    $mutations = @(
        @{ Name = 'missing llms.txt'; Expect = 'website/llms.txt is missing'; Apply = { param($r) Remove-Item (Join-Path $r 'website' 'llms.txt') } },
        @{ Name = 'broken relative link'; Expect = 'nope.html'; Apply = { param($r) Edit-FixtureFile $r 'website/windows-screenshot-mcp-server.html' 'href="./index.html"' 'href="./nope.html"' } },
        @{ Name = 'malformed repo link'; Expect = 'malformed repository link'; Apply = { param($r) Edit-FixtureFile $r 'website/llms.txt' 'https://github.com/dimitar-radenkov/Pointframe/releases/latest)' 'https://github.com/dimitar-radenkov/pointframe/releases/latest)' } },
        @{ Name = 'dead README anchor'; Expect = 'no heading there has that anchor'; Apply = { param($r) Edit-FixtureFile $r 'website/llms.txt' 'Pointframe#pointframe-mcp-server' 'Pointframe#no-such-section' } },
        @{ Name = 'drifted install command'; Expect = 'code line is not in README.md'; Apply = { param($r) Edit-FixtureFile $r 'website/windows-screenshot-mcp-server.html' 'claude mcp add --scope user' 'claude mcp add --scope project' } },
        @{ Name = 'drifted draft command'; Expect = 'install command is not in README.md'; Apply = { param($r) Edit-FixtureFile $r 'packaging/directory-submissions/mcp-so-issue.txt' 'codex mcp add pointframe' 'codex mcp add pointframe2' } },
        @{ Name = 'asset not published'; Expect = 'does not publish it'; Apply = { param($r) Edit-FixtureFile $r '.github/workflows/cd.yml' 'packaging/output/Pointframe.Mcp-win-x64.mcpb' 'packaging/output/Other.mcpb' } },
        @{ Name = 'tool missing on page'; Expect = 'does not mention the tool get_capture'; Apply = { param($r) Edit-FixtureFile $r 'website/windows-screenshot-mcp-server.html' 'get_capture' 'get_other' } },
        @{ Name = 'tool count drift'; Expect = "says 'All ten tools'"; Apply = { param($r) Edit-FixtureFile $r 'website/windows-screenshot-mcp-server.html' 'All three tools' 'All ten tools' } },
        @{ Name = 'page missing from sitemap'; Expect = 'does not list'; Apply = { param($r) Edit-FixtureFile $r 'website/sitemap.xml' 'windows-screenshot-mcp-server.html' 'other.html' } },
        @{ Name = 'pages.yml stops publishing website'; Expect = 'does not publish ./website'; Apply = { param($r) Edit-FixtureFile $r '.github/workflows/pages.yml' './website' './elsewhere' } },
        @{ Name = 'false no-telemetry claim'; Expect = 'no-telemetry claim is false'; Apply = { param($r) New-Item -ItemType Directory -Force (Join-Path $r "Pointframe.Mcp") | Out-Null; Set-Content (Join-Path $r "Pointframe.Mcp" "Telemetry.cs") 'var c = new TelemetryClient();' } }
    )

    foreach ($mutation in $mutations)
    {
        $cases++
        $copy = Copy-FixtureTree $good
        try
        {
            & $mutation.Apply $copy
            $found = @(Invoke-OfflineChecks $copy)
            $hit = @($found | Where-Object { $_.Contains($mutation.Expect) })
            if ($hit.Count -eq 0)
            {
                $failures.Add("mutation '$($mutation.Name)' should report '$($mutation.Expect)' but reported: $($found -join ' | ')")
            }
        }
        finally
        {
            Remove-Item -LiteralPath $copy -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    if ($failures.Count -gt 0)
    {
        foreach ($failure in $failures)
        {
            Write-Host "ERROR self-test: $failure"
        }
        return 1
    }
    Write-Host "Self-test passed: $cases case(s)."
    0
}

if ($SelfTest)
{
    exit (Invoke-SelfTest)
}
if ($Snapshot)
{
    exit (Invoke-Snapshot)
}

$all = @(Invoke-OfflineChecks $RepoRoot)
if ($Online)
{
    $all += @(Invoke-OnlineChecks $RepoRoot)
}
if ($all.Count -gt 0)
{
    $all | ForEach-Object { Write-Host $_ }
    exit 1
}
$mode = if ($Online) { 'offline and online' } else { 'offline' }
Write-Host "Agent discovery check passed ($mode)."
exit 0
