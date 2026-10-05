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
are in the sitemap, the index, and pages.yml; the telemetry disclosure is complete: no text still claims "no telemetry",
the agent page, both drafts, the README Privacy Policy, and website/privacy.html name the opt-out, every property the code
sends is disclosed, the MCPB manifest links the privacy page, and the source build carries no connection string.

Claude plugin checks (plugin/pointframe, the directory's rules that can be checked offline): plugin.json fields and name
pattern, README of at least 40 words outside code blocks that names the pinned download, the opt-out, and the privacy page,
LICENSE, server.lock.json schema and version agreement with plugin.json, .mcp.json that starts powershell on a script written
from ${CLAUDE_PLUGIN_ROOT} with no other variable or inline code, start script that never writes to stdout and uses no package
launcher, skill front matter with a single-string description, only small text files, no symlinks or system files, CI that ignores
plugin-only master pushes, the pin workflow, and the directory submission draft. The self-test mutates a copy of the plugin.

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
$PrivacyPage = 'website/privacy.html'
$PrivacyUrl = 'https://dimitar-radenkov.github.io/Pointframe/privacy.html'
$TelemetrySource = 'Pointframe.Telemetry/OperationTelemetry.cs'
$TelemetryConfig = 'Pointframe.Telemetry/telemetry.json'
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
    $required = @($AgentPage, $Llms, $Sitemap, $Index, $Readme, $CdWorkflow, $PagesWorkflow, $McpPackaging, $PrivacyPage, $TelemetrySource, $TelemetryConfig) + $Submissions
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

function Test-TelemetryDisclosure([string]$Root, [System.Collections.Generic.List[string]]$Problems)
{
    foreach ($path in $AgentTexts)
    {
        $text = Read-RepoFile $Root $path
        if ($text -and $text -match '(?i)\bsends?\s+no\s+telemetry\b|\bno\s+telemetry\b')
        {
            Add-Problem $Problems "$path still claims there is no telemetry; the CLI and MCP server send anonymous usage counts."
        }
    }

    $optOut = 'POINTFRAME_TELEMETRY_OPTOUT'
    $disclosures = @($AgentPage, 'packaging/directory-submissions/claude-local-extension-directory.txt', $Readme, $PrivacyPage)
    foreach ($path in $disclosures)
    {
        $text = Read-RepoFile $Root $path
        if ($text -and $text.IndexOf($optOut, [StringComparison]::Ordinal) -lt 0)
        {
            Add-Problem $Problems "$path does not name the opt-out $optOut."
        }
    }

    foreach ($path in @($AgentPage, 'packaging/directory-submissions/claude-local-extension-directory.txt'))
    {
        $text = Read-RepoFile $Root $path
        if ($text -and $text -notmatch 'privacy\.html')
        {
            Add-Problem $Problems "$path does not link the privacy page $PrivacyUrl."
        }
    }

    $privacy = Read-RepoFile $Root $PrivacyPage
    if ($privacy)
    {
        if ($privacy -notmatch [regex]::Escape("<link rel=`"canonical`" href=`"$PrivacyUrl`">"))
        {
            Add-Problem $Problems "$PrivacyPage canonical link is not $PrivacyUrl."
        }
        foreach ($term in @('DO_NOT_TRACK', 'agent-telemetry.json', 'Application Insights', 'github.com/dimitar-radenkov/Pointframe/issues'))
        {
            if ($privacy.IndexOf($term, [StringComparison]::Ordinal) -lt 0)
            {
                Add-Problem $Problems "$PrivacyPage does not mention '$term'."
            }
        }
    }
    $sitemapText = Read-RepoFile $Root $Sitemap
    if ($sitemapText -and $sitemapText -notmatch "<loc>$([regex]::Escape($PrivacyUrl))</loc>")
    {
        Add-Problem $Problems "$Sitemap does not list $PrivacyUrl."
    }

    $readmeText = Read-RepoFile $Root $Readme
    if ($readmeText -and $readmeText -notmatch '(?m)^## Privacy Policy\s*$')
    {
        Add-Problem $Problems "$Readme has no '## Privacy Policy' section."
    }

    $script = Read-RepoFile $Root $McpPackaging
    if ($script -and $script -notmatch "privacy_policies\s*=\s*@\(\s*`"$([regex]::Escape($PrivacyUrl))`"")
    {
        Add-Problem $Problems "The MCPB manifest in $McpPackaging does not set privacy_policies to $PrivacyUrl."
    }

    $source = Read-RepoFile $Root $TelemetrySource
    if ($source)
    {
        $keys = @([regex]::Matches($source, 'internal const string \w+Key = "([a-z_]+)";') | ForEach-Object { $_.Groups[1].Value })
        if ($keys.Count -eq 0)
        {
            Add-Problem $Problems "No telemetry property keys found in $TelemetrySource."
        }
        foreach ($key in $keys)
        {
            foreach ($path in @($Readme, $PrivacyPage))
            {
                $text = Read-RepoFile $Root $path
                if ($text -and $text.IndexOf("``$key``", [StringComparison]::Ordinal) -lt 0 -and $text.IndexOf("<code>$key</code>", [StringComparison]::Ordinal) -lt 0)
                {
                    Add-Problem $Problems "$path does not disclose the telemetry property '$key' that $TelemetrySource sends."
                }
            }
        }
    }

    $config = Read-RepoFile $Root $TelemetryConfig
    if ($config -and $config -match '"ConnectionString"\s*:\s*"[^"]')
    {
        Add-Problem $Problems "$TelemetryConfig carries a connection string in source; only the release pipeline may inject it."
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
    Test-TelemetryDisclosure $Root $problems
    @($problems)
}

function Get-MarkdownWordCount([string]$Markdown)
{
    $outside = [regex]::Replace($Markdown, '(?s)```.*?```', ' ')
    @([regex]::Matches($outside, '[\p{L}\p{N}][\p{L}\p{N}''-]*')).Count
}

function Test-ClaudePlugin([string]$PluginRoot, [System.Collections.Generic.List[string]]$Problems)
{
    $label = 'plugin/pointframe'
    if (-not (Test-Path -LiteralPath $PluginRoot -PathType Container))
    {
        Add-Problem $Problems "$label is missing."
        return
    }

    $files = @(Get-ChildItem -LiteralPath $PluginRoot -Recurse -Force -File)
    if ($files.Count -gt 512)
    {
        Add-Problem $Problems "$label has $($files.Count) files; the directory holds a plugin of more than 512 files for review."
    }
    foreach ($item in @(Get-ChildItem -LiteralPath $PluginRoot -Recurse -Force))
    {
        $relative = $item.FullName.Substring($PluginRoot.Length).TrimStart('\', '/')
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)
        {
            Add-Problem $Problems "$label contains the symbolic link $relative."
        }
        if ($item.Name -in @('.DS_Store', 'Thumbs.db', 'desktop.ini', '__MACOSX'))
        {
            Add-Problem $Problems "$label contains the system file $relative, which the directory rejects."
        }
    }
    foreach ($file in $files)
    {
        $relative = $file.FullName.Substring($PluginRoot.Length).TrimStart('\', '/')
        if ($file.Length -ge 256KB)
        {
            Add-Problem $Problems "$label/$relative is $($file.Length) bytes; keep every file under 256 KiB."
        }
        if ($relative -ne '.claude-plugin\icon.png' -and $relative -ne '.claude-plugin/icon.png' -and [IO.File]::ReadAllBytes($file.FullName) -contains 0)
        {
            Add-Problem $Problems "$label/$relative is a binary file; a plugin may hold only text files and .claude-plugin/icon.png."
        }
    }

    $manifestText = Read-RepoFile $PluginRoot '.claude-plugin/plugin.json'
    $manifest = $null
    if ($null -eq $manifestText)
    {
        Add-Problem $Problems "$label/.claude-plugin/plugin.json is missing."
    }
    else
    {
        try { $manifest = $manifestText | ConvertFrom-Json } catch { Add-Problem $Problems "$label/.claude-plugin/plugin.json is not valid JSON." }
    }
    if ($manifest)
    {
        $names = @($manifest.PSObject.Properties.Name)
        if ($names -notcontains 'name' -or $manifest.name -cnotmatch '^[a-z0-9]([a-z0-9-]{0,62}[a-z0-9])?$' -or $manifest.name -in @('claude', 'anthropic', 'official', 'plugin', 'mcp', 'test'))
        {
            Add-Problem $Problems "$label plugin.json name must be lowercase letters, digits, and hyphens, and not a reserved word."
        }
        foreach ($field in @('displayName', 'description', 'version', 'license', 'homepage', 'repository'))
        {
            if ($names -notcontains $field -or [string]::IsNullOrWhiteSpace([string]$manifest.$field))
            {
                Add-Problem $Problems "$label plugin.json does not set $field."
            }
        }
        if ($names -notcontains 'author' -or [string]::IsNullOrWhiteSpace([string]$manifest.author.name))
        {
            Add-Problem $Problems "$label plugin.json does not set author.name."
        }
        if ($names -contains 'license' -and $manifest.license -ne 'MIT')
        {
            Add-Problem $Problems "$label plugin.json license is '$($manifest.license)' but the repository is MIT."
        }
    }

    $iconPath = Join-Path $PluginRoot '.claude-plugin/icon.png'
    if (-not (Test-Path -LiteralPath $iconPath -PathType Leaf))
    {
        Add-Problem $Problems "$label/.claude-plugin/icon.png is missing; the directory shows a No icon warning without it."
    }
    else
    {
        $iconBytes = [IO.File]::ReadAllBytes($iconPath)
        $pngSignature = [byte[]](0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A)
        $isPng = $iconBytes.Length -ge 24 -and (-not (Compare-Object $pngSignature $iconBytes[0..7]))
        $iconWidth = 0
        $iconHeight = 0
        if ($isPng)
        {
            $iconWidth = ([int]$iconBytes[16] -shl 24) + ([int]$iconBytes[17] -shl 16) + ([int]$iconBytes[18] -shl 8) + [int]$iconBytes[19]
            $iconHeight = ([int]$iconBytes[20] -shl 24) + ([int]$iconBytes[21] -shl 16) + ([int]$iconBytes[22] -shl 8) + [int]$iconBytes[23]
        }
        if (-not $isPng -or $iconWidth -ne $iconHeight -or $iconWidth -lt 512 -or $iconWidth -gt 2048)
        {
            Add-Problem $Problems "$label/.claude-plugin/icon.png must be a square PNG between 512 and 2048 pixels (found $iconWidth x $iconHeight)."
        }
    }

    if ($null -eq (Read-RepoFile $PluginRoot 'LICENSE'))
    {
        Add-Problem $Problems "$label/LICENSE is missing."
    }

    $readme = Read-RepoFile $PluginRoot 'README.md'
    if ($null -eq $readme)
    {
        Add-Problem $Problems "$label/README.md is missing."
    }
    else
    {
        if ((Get-MarkdownWordCount $readme) -lt 40)
        {
            Add-Problem $Problems "$label/README.md has fewer than 40 words outside code blocks."
        }
        foreach ($required in @($PrivacyUrl, 'POINTFRAME_TELEMETRY_OPTOUT', 'DO_NOT_TRACK', 'plugin-mcp', 'server.lock.json', 'SHA-256'))
        {
            if (-not $readme.Contains($required))
            {
                Add-Problem $Problems "$label/README.md does not mention '$required'."
            }
        }
    }

    $lock = $null
    $lockText = Read-RepoFile $PluginRoot 'server.lock.json'
    if ($null -eq $lockText)
    {
        Add-Problem $Problems "$label/server.lock.json is missing."
    }
    else
    {
        try { $lock = $lockText | ConvertFrom-Json } catch { Add-Problem $Problems "$label/server.lock.json is not valid JSON." }
    }
    if ($lock)
    {
        $version = [string]$lock.version
        $expectedUrl = "$RepoUrl/releases/download/v$version/Pointframe.Mcp-$version-win-x64.mcpb"
        if ($lock.schemaVersion -ne 1 -or $version -notmatch '^[0-9]+(\.[0-9]+){2,3}$' -or [string]$lock.sha256 -cnotmatch '^[0-9a-f]{64}$' -or [string]$lock.url -cne $expectedUrl -or [string]$lock.asset -cne "Pointframe.Mcp-$version-win-x64.mcpb")
        {
            Add-Problem $Problems "$label/server.lock.json must have schemaVersion 1, a version, a lowercase sha256, the asset name, and the matching release URL $expectedUrl."
        }
        if ($manifest -and [string]$manifest.version -ne $version)
        {
            Add-Problem $Problems "$label plugin.json version '$($manifest.version)' differs from the pinned server version '$version'."
        }
    }

    $mcpText = Read-RepoFile $PluginRoot '.mcp.json'
    if ($null -eq $mcpText)
    {
        Add-Problem $Problems "$label/.mcp.json is missing."
    }
    else
    {
        try
        {
            $entry = ($mcpText | ConvertFrom-Json).mcpServers.pointframe
            $arguments = @(if ($entry.PSObject.Properties.Name -contains 'args') { $entry.args | ForEach-Object { [string]$_ } })
            $command = [string]$entry.command
            $launcherRelative = if ($command -cmatch '^\$\{CLAUDE_PLUGIN_ROOT\}/(?<file>[A-Za-z0-9._-]+(/[A-Za-z0-9._-]+)*)$') { $Matches['file'] } else { $null }
            if (-not $launcherRelative -or -not (Test-Path -LiteralPath (Join-Path $PluginRoot $launcherRelative) -PathType Leaf))
            {
                Add-Problem $Problems "$label/.mcp.json command must be a literal `${CLAUDE_PLUGIN_ROOT}/<file> that exists in the plugin."
            }
            if ($arguments | Where-Object { $_ -match '^-' -or $_ -match '[\\/]' -or $_ -match '\.(ps1|cmd|bat|exe)$' })
            {
                Add-Problem $Problems "$label/.mcp.json must not pass args that are flags or look like paths; the directory validator reads every arg as a path. Put them in the launcher."
            }
            if ([regex]::Matches($mcpText, '\$\{[^}]+\}') | Where-Object { $_.Value -ne '${CLAUDE_PLUGIN_ROOT}' })
            {
                Add-Problem $Problems "$label/.mcp.json uses a variable other than `${CLAUDE_PLUGIN_ROOT}."
            }
        }
        catch
        {
            Add-Problem $Problems "$label/.mcp.json is not valid JSON or has no pointframe server."
        }
    }

    $launcher = Read-RepoFile $PluginRoot 'scripts/start-mcp.cmd'
    if ($null -eq $launcher)
    {
        Add-Problem $Problems "$label/scripts/start-mcp.cmd is missing."
    }
    elseif ($launcher -notmatch '(?m)^@powershell\.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0start-mcp\.ps1" %\*\r?$' -or $launcher -match '(?im)^\s*@?echo\b')
    {
        Add-Problem $Problems "$label/scripts/start-mcp.cmd must run powershell.exe -NoProfile -ExecutionPolicy Bypass -File start-mcp.ps1 with %* and must not echo."
    }

    $start = Read-RepoFile $PluginRoot 'scripts/start-mcp.ps1'
    if ($null -eq $start)
    {
        Add-Problem $Problems "$label/scripts/start-mcp.ps1 is missing."
    }
    else
    {
        if ($start -match '(?im)^\s*(Write-Host|Write-Output|Out-Host)\b|\bnpx\b|\buvx\b|\bnpm\b|\bpipx?\b')
        {
            Add-Problem $Problems "$label/scripts/start-mcp.ps1 must not write to stdout (Write-Host, Write-Output, Out-Host) or use a package launcher."
        }
        if ($start -notmatch 'Tls12')
        {
            Add-Problem $Problems "$label/scripts/start-mcp.ps1 does not enforce TLS 1.2."
        }
    }

    $skills = @(Get-ChildItem -LiteralPath (Join-Path $PluginRoot 'skills') -Recurse -Filter 'SKILL.md' -File -ErrorAction SilentlyContinue)
    if ($skills.Count -eq 0)
    {
        Add-Problem $Problems "$label has no skills/<name>/SKILL.md."
    }
    foreach ($skill in $skills)
    {
        $text = [IO.File]::ReadAllText($skill.FullName)
        $frontMatter = if ($text -match '(?s)\A---\r?\n(.*?)\r?\n---') { $Matches[1] } else { '' }
        if ($frontMatter -notmatch '(?m)^description:\s*\S[^\r\n]*$' -or $frontMatter -match '(?m)^description:\s*[\[|>]')
        {
            Add-Problem $Problems "$label/skills/$($skill.Directory.Name)/SKILL.md needs YAML front matter with a single-string description."
        }
    }
}

function Test-ClaudePluginRepository([string]$Root, [System.Collections.Generic.List[string]]$Problems)
{
    $ci = Read-RepoFile $Root '.github/workflows/ci.yml'
    if ($null -eq $ci -or $ci -notmatch "(?s)push:.*?paths-ignore:\s*\r?\n\s*- 'plugin/\*\*'")
    {
        Add-Problem $Problems "ci.yml does not ignore master pushes that change only plugin/**; a plugin-only merge would cut a release."
    }
    $merge = Read-RepoFile $Root 'scripts/merge-pr.ps1'
    if ($null -eq $merge -or -not $merge.Contains('update-plugin-pin.ps1') -or -not $merge.Contains('function Update-PluginPin'))
    {
        Add-Problem $Problems 'scripts/merge-pr.ps1 no longer pins the Claude plugin to the latest release (Update-PluginPin calling update-plugin-pin.ps1).'
    }
    $draft = Read-RepoFile $Root 'packaging/directory-submissions/claude-plugin-directory.txt'
    if ($null -eq $draft)
    {
        Add-Problem $Problems 'packaging/directory-submissions/claude-plugin-directory.txt is missing.'
    }
    else
    {
        foreach ($required in @($PrivacyUrl, 'POINTFRAME_TELEMETRY_OPTOUT', 'DRAFT'))
        {
            if (-not $draft.Contains($required))
            {
                Add-Problem $Problems "claude-plugin-directory.txt does not mention '$required'."
            }
        }
    }
}

function Invoke-PluginChecks([string]$Root)
{
    $problems = [System.Collections.Generic.List[string]]::new()
    Test-ClaudePlugin (Join-Path $Root 'plugin' 'pointframe') $problems
    Test-ClaudePluginRepository $Root $problems
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
        @{ Name = 'stale no-telemetry claim'; Expect = 'still claims there is no telemetry'; Apply = { param($r) Edit-FixtureFile $r 'website/llms.txt' 'A fixture summary.' 'A fixture summary. The MCP server sends no telemetry.' } },
        @{ Name = 'opt-out missing on agent page'; Expect = 'does not name the opt-out'; Apply = { param($r) Edit-FixtureFile $r 'website/windows-screenshot-mcp-server.html' 'POINTFRAME_TELEMETRY_OPTOUT' 'POINTFRAME_OTHER' } },
        @{ Name = 'privacy page missing from sitemap'; Expect = 'privacy.html'; Apply = { param($r) Edit-FixtureFile $r 'website/sitemap.xml' 'Pointframe/privacy.html' 'Pointframe/other.html' } },
        @{ Name = 'undisclosed telemetry property'; Expect = "does not disclose the telemetry property 'host'"; Apply = { param($r) Edit-FixtureFile $r 'website/privacy.html' '<code>host</code>' '<code>machine</code>' } },
        @{ Name = 'manifest without privacy policy'; Expect = 'does not set privacy_policies'; Apply = { param($r) Edit-FixtureFile $r 'packaging/build-mcp-package.ps1' 'privacy_policies' 'privacy_links' } },
        @{ Name = 'connection string in source'; Expect = 'carries a connection string in source'; Apply = { param($r) Edit-FixtureFile $r 'Pointframe.Telemetry/telemetry.json' '"ConnectionString": ""' '"ConnectionString": "InstrumentationKey=abc"' } }
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

    $pluginBaseline = [System.Collections.Generic.List[string]]::new()
    Test-ClaudePlugin (Join-Path $RepoRoot 'plugin' 'pointframe') $pluginBaseline
    $cases++
    if ($pluginBaseline.Count -ne 0)
    {
        $failures.Add("the plugin should pass but reported: $($pluginBaseline -join ' | ')")
    }

    $pluginMutations = @(
        @{ Name = 'uppercase plugin name'; Expect = 'plugin.json name must be'; Apply = { param($r) Edit-FixtureFile $r '.claude-plugin/plugin.json' '"name": "pointframe"' '"name": "Pointframe"' } },
        @{ Name = 'missing description'; Expect = 'does not set description'; Apply = { param($r) Edit-FixtureFile $r '.claude-plugin/plugin.json' '"description"' '"summary"' } },
        @{ Name = 'version drift'; Expect = 'differs from the pinned server version'; Apply = { param($r) Edit-FixtureFile $r '.claude-plugin/plugin.json' '"version": "' '"version": "0.0.' } },
        @{ Name = 'short sha256'; Expect = 'server.lock.json must have'; Apply = { param($r) Edit-FixtureFile $r 'server.lock.json' '"sha256": "' '"sha256": "AB' } },
        @{ Name = 'lock url for another version'; Expect = 'server.lock.json must have'; Apply = { param($r) Edit-FixtureFile $r 'server.lock.json' '/releases/download/v' '/releases/download/v9.' } },
        @{ Name = 'mcp command is not the plugin root launcher'; Expect = 'command must be a literal'; Apply = { param($r) Edit-FixtureFile $r '.mcp.json' '"${CLAUDE_PLUGIN_ROOT}/scripts/start-mcp.cmd"' '"powershell"' } },
        @{ Name = 'mcp command launcher missing'; Expect = 'command must be a literal'; Apply = { param($r) Edit-FixtureFile $r '.mcp.json' 'start-mcp.cmd' 'missing.cmd' } },
        @{ Name = 'mcp args that look like flags or paths'; Expect = 'must not pass args'; Apply = { param($r) Edit-FixtureFile $r '.mcp.json' 'start-mcp.cmd"' 'start-mcp.cmd", "args": ["-ExecutionPolicy", "Bypass"]' } },
        @{ Name = 'other variable in mcp command'; Expect = 'variable other than'; Apply = { param($r) Edit-FixtureFile $r '.mcp.json' 'start-mcp.cmd"' 'start-mcp.cmd", "args": ["${HOME}"]' } },
        @{ Name = 'launcher drops the execution policy'; Expect = 'start-mcp.cmd must run'; Apply = { param($r) Edit-FixtureFile $r 'scripts/start-mcp.cmd' ' -ExecutionPolicy Bypass' '' } },
        @{ Name = 'launcher echoes'; Expect = 'must not echo'; Apply = { param($r) Edit-FixtureFile $r 'scripts/start-mcp.cmd' "@exit" "@echo hi`r`n@exit" } },
        @{ Name = 'missing icon'; Expect = 'icon.png is missing'; Apply = { param($r) Remove-Item (Join-Path $r '.claude-plugin/icon.png') } },
        @{ Name = 'icon is not a png'; Expect = 'square PNG'; Apply = { param($r) Set-Content -LiteralPath (Join-Path $r '.claude-plugin/icon.png') -Value ('x' * 40) } },
        @{ Name = 'icon too small'; Expect = 'square PNG'; Apply = { param($r) $p = Join-Path $r '.claude-plugin/icon.png'; $b = [IO.File]::ReadAllBytes($p); $b[18] = 0; $b[19] = 64; [IO.File]::WriteAllBytes($p, $b) } },
        @{ Name = 'start script writes to stdout'; Expect = 'must not write to stdout'; Apply = { param($r) Edit-FixtureFile $r 'scripts/start-mcp.ps1' 'function Write-Log' "Write-Host 'x'`r`nfunction Write-Log" } },
        @{ Name = 'short README'; Expect = 'fewer than 40 words'; Apply = { param($r) Set-Content -LiteralPath (Join-Path $r 'README.md') -Value 'Too short.' } },
        @{ Name = 'README without opt-out'; Expect = "does not mention 'POINTFRAME_TELEMETRY_OPTOUT'"; Apply = { param($r) Edit-FixtureFile $r 'README.md' 'POINTFRAME_TELEMETRY_OPTOUT' 'POINTFRAME_OTHER' } },
        @{ Name = 'missing LICENSE'; Expect = 'LICENSE is missing'; Apply = { param($r) Remove-Item (Join-Path $r 'LICENSE') } },
        @{ Name = 'binary file'; Expect = 'binary file'; Apply = { param($r) [IO.File]::WriteAllBytes((Join-Path $r 'tool.bin'), [byte[]](1, 0, 2)) } },
        @{ Name = 'large file'; Expect = 'keep every file under 256 KiB'; Apply = { param($r) Set-Content -LiteralPath (Join-Path $r 'big.txt') -Value ('x' * 300000) } },
        @{ Name = 'system file'; Expect = 'system file'; Apply = { param($r) Set-Content -LiteralPath (Join-Path $r 'Thumbs.db') -Value 'x' } },
        @{ Name = 'skill description as a list'; Expect = 'single-string description'; Apply = { param($r) Edit-FixtureFile $r 'skills/capture-screen/SKILL.md' 'description: ' 'description: [' } }
    )
    foreach ($mutation in $pluginMutations)
    {
        $cases++
        $copy = Copy-FixtureTree (Join-Path $RepoRoot 'plugin' 'pointframe')
        try
        {
            & $mutation.Apply $copy
            $found = [System.Collections.Generic.List[string]]::new()
            Test-ClaudePlugin $copy $found
            if (@($found | Where-Object { $_.Contains($mutation.Expect) }).Count -eq 0)
            {
                $failures.Add("plugin mutation '$($mutation.Name)' should report '$($mutation.Expect)' but reported: $($found -join ' | ')")
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

$all = @(Invoke-OfflineChecks $RepoRoot) + @(Invoke-PluginChecks $RepoRoot)
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
