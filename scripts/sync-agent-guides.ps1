#Requires -Version 7.0
<#
Keeps the agent-facing copies of the verify recipe equal to its one canonical source, Pointframe.Mcp/Guides/verify-desktop-work.md
(the guide the MCP server embeds and serves as pointframe://guides/verify-desktop-work).

  pwsh scripts/sync-agent-guides.ps1           # rewrite the generated files from the guide
  pwsh scripts/sync-agent-guides.ps1 -Check    # report drift and exit 1; change nothing
  pwsh scripts/sync-agent-guides.ps1 -Root <folder> -Check

Generated files (do not edit by hand):
  plugin/pointframe/skills/verify-desktop-work/SKILL.md
      its own front matter (kept from the file) followed by the guide
  plugin/pointframe/skills/create-desktop-verification-skill/references/recipe.md
      the guide, unchanged
  plugin/pointframe/skills/create-desktop-verification-skill/references/verify-spec.md
      docs/cli/verify-spec.md, unchanged (the spec schema, bundled so the bundled skill never fetches it from the web)

Generated regions (the text between the short-path:begin and short-path:end comments; the markers must already exist):
  the guide's own short-path region (section "Short path for coding agents") is the source; it is copied into
  README.md, docs/cli/verify.md and website/llms.txt. Edit the region only in the guide.

Line endings are compared without regard to CR; files are written with CRLF like the rest of the repository.
Exit 0 when in sync, 1 on drift (-Check) or a missing source, 2 on bad arguments.
#>
[CmdletBinding()]
param(
    [switch]$Check,
    [string]$Root
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ([string]::IsNullOrWhiteSpace($Root))
{
    $Root = Join-Path $PSScriptRoot '..'
}
if (-not (Test-Path -LiteralPath $Root -PathType Container))
{
    Write-Host "ERROR sync-agent-guides: root '$Root' is not a folder."
    exit 2
}
$Root = (Resolve-Path -LiteralPath $Root).Path

$GuidePath = 'Pointframe.Mcp/Guides/verify-desktop-work.md'
$SkillPath = 'plugin/pointframe/skills/verify-desktop-work/SKILL.md'
$RecipePath = 'plugin/pointframe/skills/create-desktop-verification-skill/references/recipe.md'
$SpecSourcePath = 'docs/cli/verify-spec.md'
$SpecCopyPath = 'plugin/pointframe/skills/create-desktop-verification-skill/references/verify-spec.md'
$RegionTargets = @('README.md', 'docs/cli/verify.md', 'website/llms.txt')
$RegionBegin = '<!-- short-path:begin'
$RegionEnd = '<!-- short-path:end -->'

function ConvertTo-Lf([string]$Text)
{
    $Text.Replace("`r`n", "`n").Replace("`r", "`n")
}

function Read-Lf([string]$Relative)
{
    $full = Join-Path $Root $Relative
    if (-not (Test-Path -LiteralPath $full -PathType Leaf))
    {
        return $null
    }
    ConvertTo-Lf ([IO.File]::ReadAllText($full))
}

$guide = Read-Lf $GuidePath
if ($null -eq $guide)
{
    Write-Host "ERROR sync-agent-guides: the canonical guide $GuidePath is missing."
    exit 1
}
if (-not $guide.EndsWith("`n"))
{
    $guide += "`n"
}

$spec = Read-Lf $SpecSourcePath
if ($null -eq $spec)
{
    Write-Host "ERROR sync-agent-guides: the spec reference $SpecSourcePath is missing."
    exit 1
}

$skill = Read-Lf $SkillPath
$frontMatter = $null
if ($null -ne $skill -and $skill -match '(?s)\A(---\n.*?\n---\n)')
{
    $frontMatter = $Matches[1]
}
if ($null -eq $frontMatter)
{
    Write-Host "ERROR sync-agent-guides: $SkillPath is missing or has no YAML front matter to keep."
    exit 1
}

function Get-Region([string]$Text)
{
    $b = $Text.IndexOf($RegionBegin, [StringComparison]::Ordinal)
    $e = $Text.IndexOf($RegionEnd, [StringComparison]::Ordinal)
    if ($b -lt 0 -or $e -lt $b)
    {
        return $null
    }
    $bodyStart = $Text.IndexOf("`n", $b) + 1
    [pscustomobject]@{ BodyStart = $bodyStart; End = $e; Body = $Text.Substring($bodyStart, $e - $bodyStart) }
}

$guideRegion = Get-Region $guide
if ($null -eq $guideRegion -or [string]::IsNullOrWhiteSpace($guideRegion.Body))
{
    Write-Host "ERROR sync-agent-guides: $GuidePath has no short-path region between $RegionBegin ... --> and $RegionEnd."
    exit 1
}

$expected = [ordered]@{
    $SkillPath = $frontMatter + $guide
    $RecipePath = $guide
    $SpecCopyPath = $spec
}
foreach ($target in $RegionTargets)
{
    $text = Read-Lf $target
    $region = if ($null -eq $text) { $null } else { Get-Region $text }
    if ($null -eq $region)
    {
        Write-Host "ERROR sync-agent-guides: $target is missing or has no short-path region markers ($RegionBegin ... --> and $RegionEnd)."
        exit 1
    }
    $expected[$target] = $text.Substring(0, $region.BodyStart) + $guideRegion.Body + $text.Substring($region.End)
}
$drift = [System.Collections.Generic.List[string]]::new()
foreach ($relative in $expected.Keys)
{
    $current = Read-Lf $relative
    if ($current -ceq $expected[$relative])
    {
        continue
    }
    if ($Check)
    {
        $state = if ($null -eq $current) { 'is missing' } else { 'differs from its source' }
        $drift.Add("ERROR sync-agent-guides: $relative $state. Run: pwsh scripts/sync-agent-guides.ps1")
        continue
    }
    $full = Join-Path $Root $relative
    New-Item -ItemType Directory -Force -Path (Split-Path $full) | Out-Null
    [IO.File]::WriteAllText($full, $expected[$relative].Replace("`n", "`r`n"), [Text.UTF8Encoding]::new($false))
    Write-Host "wrote $relative"
}

if ($drift.Count -gt 0)
{
    $drift | ForEach-Object { Write-Host $_ }
    exit 1
}
Write-Host $(if ($Check) { 'Agent guides are in sync.' } else { 'Agent guides synced.' })
exit 0
