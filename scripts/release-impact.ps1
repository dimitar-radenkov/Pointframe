#Requires -Version 7.0
<#
Decides whether a change needs desktop qualification of the packaged CLI and MCP server before it is merged.

  pwsh scripts/release-impact.ps1                              # origin/master...HEAD
  pwsh scripts/release-impact.ps1 -Base origin/master -Head my-branch
  pwsh scripts/release-impact.ps1 -SelfTest                    # offline: fixture path lists -> expected decision

Prints the changed paths, then `desktop-qualification: required` (with the matched paths and rules) or
`desktop-qualification: not-required`. When required, run `pwsh scripts/check-released-desktop.ps1 -BuildCandidate`
on the interactive desktop before merging.

Exit codes: 0 either decision, 2 bad arguments or git failure.
#>
[CmdletBinding()]
param(
    [string]$Base = 'origin/master',
    [string]$Head = 'HEAD',
    [switch]$SelfTest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# The rules, in one place. A path is "risky" when any pattern matches; the first matching rule names it.
$script:Rules = @(
    [pscustomobject]@{ Name = 'shipping project'; Pattern = '^(Pointframe\.Mcp|Pointframe\.Cli|Pointframe\.Engine|Pointframe\.Automation[^/]*)/' }
    [pscustomobject]@{ Name = 'packaging'; Pattern = '^packaging/' }
    [pscustomobject]@{ Name = 'CD workflow'; Pattern = '^\.github/workflows/cd\.yml$' }
    [pscustomobject]@{ Name = 'project file'; Pattern = '\.csproj$' }
    [pscustomobject]@{ Name = 'central build configuration'; Pattern = '(^|/)(Directory\.Build\.[^/]+|Directory\.Packages\.props|global\.json|NuGet\.config)$' }
)

function Get-Impact
{
    param([string[]]$Paths)

    $matched = [System.Collections.Generic.List[object]]::new()
    foreach ($path in $Paths)
    {
        $normalized = $path.Replace('\', '/')
        foreach ($rule in $script:Rules)
        {
            if ($normalized -match $rule.Pattern)
            {
                $matched.Add([pscustomobject]@{ Path = $path; Rule = $rule.Name })
                break
            }
        }
    }

    return [pscustomobject]@{
        Required = ($matched.Count -gt 0)
        Matches = $matched
    }
}

function Test-SelfCases
{
    $cases = @(
        @{ Paths = @(); Expected = $false }
        @{ Paths = @('README.md', 'docs/notes.md', 'website/index.html'); Expected = $false }
        @{ Paths = @('Pointframe/Views/OverlayWindow.xaml.cs', 'Pointframe.Tests/Foo.cs'); Expected = $false }
        @{ Paths = @('.github/workflows/ci.yml', '.github/workflows/winget-release.yml'); Expected = $false }
        @{ Paths = @('Pointframe.Mcp/Tools/PointframeMcpTools.cs'); Expected = $true }
        @{ Paths = @('Pointframe.Cli/Program.cs'); Expected = $true }
        @{ Paths = @('Pointframe.Engine/Capture/Foo.cs'); Expected = $true }
        @{ Paths = @('Pointframe.AutomationTests/Smoke/X.cs'); Expected = $true }
        @{ Paths = @('packaging/build-mcp-package.ps1'); Expected = $true }
        @{ Paths = @('.github/workflows/cd.yml'); Expected = $true }
        @{ Paths = @('Pointframe.Data/Pointframe.Data.csproj'); Expected = $true }
        @{ Paths = @('Directory.Build.props'); Expected = $true }
        @{ Paths = @('Directory.Packages.props'); Expected = $true }
        @{ Paths = @('global.json'); Expected = $true }
        @{ Paths = @('NuGet.config'); Expected = $true }
        @{ Paths = @('README.md', 'Pointframe.Cli\Program.cs'); Expected = $true }
    )

    foreach ($case in $cases)
    {
        $got = Get-Impact $case.Paths
        if ($got.Required -ne $case.Expected)
        {
            throw "SelfTest failed for [$($case.Paths -join ', ')]: expected required=$($case.Expected), got $($got.Required)."
        }
    }

    $twoRules = Get-Impact @('packaging/a.ps1', 'Pointframe.Cli/b.cs')
    if ($twoRules.Matches.Count -ne 2 -or $twoRules.Matches[0].Rule -ne 'packaging' -or $twoRules.Matches[1].Rule -ne 'shipping project')
    {
        throw 'SelfTest failed: matches did not name each path with its rule.'
    }
}

if ($SelfTest)
{
    Test-SelfCases
    Write-Host 'release-impact self-test passed.'
    exit 0
}

foreach ($revision in @($Base, $Head))
{
    if ([string]::IsNullOrWhiteSpace($revision) -or $revision -match '\s')
    {
        [Console]::Error.WriteLine("Invalid git revision '$revision'.")
        exit 2
    }
}

$changed = @(& git diff --name-only "$Base...$Head" 2>&1)
if ($LASTEXITCODE -ne 0)
{
    [Console]::Error.WriteLine("git diff failed for $Base...${Head}: $($changed -join ' ')")
    exit 2
}

foreach ($path in $changed)
{
    Write-Host "CHANGED $path"
}

$impact = Get-Impact $changed
if ($impact.Required)
{
    Write-Host 'desktop-qualification: required'
    foreach ($item in $impact.Matches)
    {
        Write-Host "  $($item.Path) [rule: $($item.Rule)]"
    }
}
else
{
    Write-Host 'desktop-qualification: not-required'
    Write-Host '  no changed path matched a qualification rule'
}

exit 0
