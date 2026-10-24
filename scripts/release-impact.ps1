#Requires -Version 7.0
<#
The release plan of a change: which release trains it ships, and whether it needs desktop qualification of the packaged
CLI and MCP server before it is merged. The two are separate outputs.

  pwsh scripts/release-impact.ps1                              # origin/master...HEAD
  pwsh scripts/release-impact.ps1 -Base origin/master -Head my-branch
  pwsh scripts/release-impact.ps1 -PlanOut plan.json [-Security] [-ReleasesFile releases.json]
  pwsh scripts/release-impact.ps1 -Paths 'a/b.cs;docs/x.md'    # classify these paths instead of a git diff (offline)
  pwsh scripts/release-impact.ps1 -SelfTest                    # offline: fixtures -> expected decisions

Lines, in this order (the first three kinds are parsed by hooks.qualify and the package ship step, keep them):

  CHANGED <path>                                  one per changed path
  desktop-qualification: required|not-required    exactly one; then one "  <path> [rule: <name>]" line per matched path
  release-trains: desktop,agent | desktop | agent | none
  release-train-baseline: <train> <tag|none> selected=<true|false>     only with -ReleasesFile
  UNCLASSIFIED <path>                             a path no class rule covers (fail closed)

When desktop qualification is required, run `pwsh scripts/check-released-desktop.ps1 -BuildCandidate` on the interactive
desktop before merging.

Classes (every path has exactly one): desktop, agent, both (selects both trains), none (ships no binary). The class
decides the trains; the qualification rules below are independent of it and unchanged by the train work.

-PlanOut writes the plan as JSON (schemaVersion 1): base, head, changedPaths [{path, class, rule}], trains,
desktopQualification {required, reasons [{path, rule}]}, unclassifiedPaths, security (from -Security, recorded only) and,
with -ReleasesFile, perTrain.<train> {baseline, changedPaths, selected, reason}. -ReleasesFile is a JSON array of GitHub
REST releases (see release-model.ps1); each train is compared with its own last successful release, not the previous
merge, so a failed publication is never skipped. There is no online mode.

Exit codes: 0 either decision, 2 bad arguments or git failure, 3 an unclassified path.
#>
[CmdletBinding()]
param(
    [string]$Base = 'origin/master',
    [string]$Head = 'HEAD',
    [switch]$SelfTest,
    [string]$PlanOut,
    [string]$CurrentVersion,
    [string]$ReleasesFile,
    [switch]$Security,
    [string]$Paths
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

. (Join-Path $PSScriptRoot 'release-model.ps1')

$script:RepositoryRoot = Split-Path $PSScriptRoot -Parent

# Qualification rules: a path is "risky" when any pattern matches; the first matching rule names it. Unchanged by the
# train work, plus the release marker (a refresh PR must be qualified on the desktop like any desktop release).
$script:Rules = @(
    [pscustomobject]@{ Name = 'shipping project'; Pattern = '^(Pointframe\.Mcp|Pointframe\.Cli|Pointframe\.Engine|Pointframe\.Automation[^/]*)/' }
    [pscustomobject]@{ Name = 'packaging'; Pattern = '^packaging/' }
    [pscustomobject]@{ Name = 'CD workflow'; Pattern = '^\.github/workflows/cd\.yml$' }
    [pscustomobject]@{ Name = 'project file'; Pattern = '\.csproj$' }
    [pscustomobject]@{ Name = 'central build configuration'; Pattern = '(^|/)(Directory\.Build\.[^/]+|Directory\.Packages\.props|global\.json|NuGet\.config)$' }
    [pscustomobject]@{ Name = 'release marker'; Pattern = '^release/refresh-aliases\.marker$' }
)

# Class rules, first match wins. A path that no rule covers is unclassified and fails the plan: a new top-level
# folder, root file or packaging file must be decided here before it can ship.
$script:ClassRules = @(
    [pscustomobject]@{ Class = 'desktop'; Name = 'release marker'; Pattern = '^release/refresh-aliases\.marker$' }
    [pscustomobject]@{ Class = 'none'; Name = 'release folder notes'; Pattern = '^release/README\.md$' }

    # Tests and fixtures ship no binary, whatever they reference.
    [pscustomobject]@{ Class = 'none'; Name = 'tests'; Pattern = '^(Pointframe\.Tests|Pointframe\.Agent\.Tests|Pointframe\.AutomationTests|Pointframe\.DesktopTestFixture)/' }
    [pscustomobject]@{ Class = 'none'; Name = 'samples'; Pattern = '^samples/' }

    # Shared build inputs reach every binary.
    [pscustomobject]@{ Class = 'both'; Name = 'project file'; Pattern = '\.csproj$' }
    [pscustomobject]@{ Class = 'both'; Name = 'central build configuration'; Pattern = '(^|/)(Directory\.Build\.[^/]+|Directory\.Packages\.props|global\.json|NuGet\.config)$' }
    [pscustomobject]@{ Class = 'both'; Name = 'version'; Pattern = '^version\.json$' }
    [pscustomobject]@{ Class = 'both'; Name = 'build tools'; Pattern = '^dotnet-tools\.json$' }
    [pscustomobject]@{ Class = 'both'; Name = 'CD workflow'; Pattern = '^\.github/workflows/cd\.yml$' }
    [pscustomobject]@{ Class = 'both'; Name = 'shared application manifest'; Pattern = '^Pointframe/app\.manifest$' }
    [pscustomobject]@{ Class = 'both'; Name = 'shared library'; Pattern = '^(Pointframe\.Engine|Pointframe\.Data|Pointframe\.Telemetry)/' }

    [pscustomobject]@{ Class = 'desktop'; Name = 'desktop app'; Pattern = '^Pointframe/' }
    [pscustomobject]@{ Class = 'desktop'; Name = 'installer'; Pattern = '^installer/' }

    [pscustomobject]@{ Class = 'agent'; Name = 'CLI and MCP'; Pattern = '^(Pointframe\.Cli|Pointframe\.Mcp)/' }
    [pscustomobject]@{ Class = 'agent'; Name = 'automation library'; Pattern = '^Pointframe\.Automation[^/]*/' }
    [pscustomobject]@{ Class = 'agent'; Name = 'agent packaging'; Pattern = '^packaging/(build-cli-package\.ps1|build-mcp-package\.ps1|assets/)' }
    [pscustomobject]@{ Class = 'none'; Name = 'packaging notes and checks'; Pattern = '^packaging/(directory-submissions/|test-mcp-stdio\.ps1$)' }

    # No binary: documentation, knowledge base, tooling, the website, the plugin (shipped through the plugin
    # directory and its pin file, not inside the CLI or MCP binaries), winget manifests, agent and editor settings.
    [pscustomobject]@{ Class = 'none'; Name = 'repository metadata'; Pattern = '^\.github/' }
    [pscustomobject]@{ Class = 'none'; Name = 'agent and editor settings'; Pattern = '^(\.agents|\.claude|\.codex|\.vscode|\.pointframe)/' }
    [pscustomobject]@{ Class = 'none'; Name = 'documentation and plans'; Pattern = '^(docs|plan)/' }
    [pscustomobject]@{ Class = 'none'; Name = 'script tooling'; Pattern = '^scripts/' }
    [pscustomobject]@{ Class = 'none'; Name = 'website'; Pattern = '^website/' }
    [pscustomobject]@{ Class = 'none'; Name = 'plugin'; Pattern = '^plugin/' }
    [pscustomobject]@{ Class = 'none'; Name = 'winget manifests'; Pattern = '^winget/' }
    [pscustomobject]@{ Class = 'none'; Name = 'root documents'; Pattern = '^(README|CONTRIBUTING|SECURITY|PRIVACY|CHANGELOG|AGENTS|CLAUDE|lessons|executor-report)\.md$' }
    [pscustomobject]@{ Class = 'none'; Name = 'root settings'; Pattern = '^(LICENSE|\.editorconfig|\.gitattributes|\.gitignore|codecov\.yml|agent-workflow\.json|agent-workflow\.lock\.json|Pointframe\.slnx)$' }
)

function Get-PathClass
{
    # The class and the rule that chose it, or $null when no rule covers the path.
    param([string]$Path)

    $normalized = $Path.Replace('\', '/')
    foreach ($rule in $script:ClassRules)
    {
        if ($normalized -match $rule.Pattern)
        {
            return [pscustomobject]@{ Path = $Path; Class = $rule.Class; Rule = $rule.Name }
        }
    }

    return $null
}

function Get-ClassTrains
{
    param([string]$Class)

    switch ($Class)
    {
        'desktop' { return @('desktop') }
        'agent' { return @('agent') }
        'both' { return @('desktop', 'agent') }
        default { return @() }
    }
}

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

function Get-Classification
{
    # Every path with its class, the trains the set selects (desktop first), and the paths no rule covers.
    param([string[]]$Paths)

    $classified = [System.Collections.Generic.List[object]]::new()
    $unclassified = [System.Collections.Generic.List[string]]::new()
    $trains = [System.Collections.Generic.HashSet[string]]::new()
    foreach ($path in $Paths)
    {
        $entry = Get-PathClass $path
        if ($null -eq $entry)
        {
            $unclassified.Add($path)
            continue
        }

        $classified.Add($entry)
        foreach ($train in Get-ClassTrains $entry.Class)
        {
            [void]$trains.Add($train)
        }
    }

    return [pscustomobject]@{
        Paths = $classified.ToArray()
        Unclassified = $unclassified.ToArray()
        Trains = @(@('desktop', 'agent') | Where-Object { $trains.Contains($_) })
    }
}

function New-ReleasePlan
{
    # GetPathsSince takes a tag and returns the paths changed from it to Head (the only impure input; null without Releases).
    param(
        [string]$Base,
        [string]$Head,
        [string[]]$ChangedPaths,
        [bool]$Security = $false,
        [object[]]$Releases = $null,
        [string]$CurrentVersion = '',
        [scriptblock]$GetPathsSince = $null
    )

    $classification = Get-Classification $ChangedPaths
    $impact = Get-Impact $ChangedPaths

    $plan = [ordered]@{
        schemaVersion = 1
        base = $Base
        head = $Head
        changedPaths = @($classification.Paths | ForEach-Object { [ordered]@{ path = $_.Path; class = $_.Class; rule = $_.Rule } })
        trains = @($classification.Trains)
        desktopQualification = [ordered]@{
            required = $impact.Required
            reasons = @($impact.Matches | ForEach-Object { [ordered]@{ path = $_.Path; rule = $_.Rule } })
        }
        unclassifiedPaths = @($classification.Unclassified)
        security = $Security
    }

    if ($null -ne $Releases)
    {
        $unclassified = [System.Collections.Generic.List[string]]::new()
        foreach ($path in $classification.Unclassified)
        {
            $unclassified.Add($path)
        }

        $perTrain = [ordered]@{}
        foreach ($train in @('desktop', 'agent'))
        {
            if ($CurrentVersion -match '^\d+\.\d+\.\d+$')
            {
                $higherThanRun = @(Get-TrainReleases $Releases $train | Where-Object { $_.Version -gt [version]$CurrentVersion })
                if ($higherThanRun.Count -gt 0)
                {
                    $perTrain[$train] = [ordered]@{
                        baseline = [string]$higherThanRun[0].Release.tag_name
                        changedPaths = @()
                        selected = $false
                        stale = $true
                        reason = "stale run v$CurrentVersion; higher published $train release $($higherThanRun[0].Release.tag_name) already exists"
                    }
                    continue
                }
            }

            $baseline = Get-TrainBaseline $Releases $train
            if ($null -eq $baseline)
            {
                $perTrain[$train] = [ordered]@{ baseline = $null; changedPaths = @(); selected = $true; stale = $false; reason = 'no complete release of this train exists' }
                continue
            }

            $tag = [string]$baseline.tag_name
            $sincePaths = @(& $GetPathsSince $tag)
            $sinceClass = Get-Classification $sincePaths
            foreach ($path in $sinceClass.Unclassified)
            {
                if ($path -notin $unclassified)
                {
                    $unclassified.Add($path)
                }
            }

            $mine = @($sinceClass.Paths | Where-Object { $train -in (Get-ClassTrains $_.Class) } | ForEach-Object { $_.Path })
            $perTrain[$train] = [ordered]@{
                baseline = $tag
                changedPaths = $mine
                selected = ($mine.Count -gt 0)
                stale = $false
                reason = $(if ($mine.Count -gt 0) { "$($mine.Count) path(s) changed since $tag" } else { "no path of this train changed since $tag" })
            }
        }

        # A retry after publication must still select the trains already published at this version so CD can run
        # Latest repair, Registry, Winget receipt, and channel reporting. The publisher itself remains idempotent.
        if ($CurrentVersion -match '^\d+\.\d+\.\d+$')
        {
            $currentRelease = @($Releases | Where-Object { [string](Get-ModelProperty $_ 'tag_name' '') -ceq "v$CurrentVersion" -and -not [bool](Get-ModelProperty $_ 'draft' $false) } | Select-Object -First 1)
            if ($currentRelease.Count -gt 0)
            {
                $names = @(Get-ReleaseAssetNames $currentRelease[0])
                $agentNames = @(Get-TrainAssetNames $CurrentVersion @('agent'))
                $desktopEvidence = @(Get-DesktopAssetNames $CurrentVersion)
                $desktopEvidence += @(Get-DesktopAliasAssetNames $CurrentVersion)
                foreach ($train in @('desktop', 'agent'))
                {
                    $evidence = if ($train -eq 'desktop') { $desktopEvidence } else { $agentNames }
                    if (@($evidence | Where-Object { $_ -in $names }).Count -gt 0)
                    {
                        $perTrain[$train]['selected'] = $true
                        $perTrain[$train]['reason'] = "published release v$CurrentVersion already contains assets of this train; continue downstream on retry"
                    }
                }
            }
        }

        $plan['perTrain'] = $perTrain
        $plan['unclassifiedPaths'] = $unclassified.ToArray()
    }

    return $plan
}

function Get-GitPaths
{
    param([string]$Range)

    $output = @(& git diff --name-only $Range 2>&1)
    if ($LASTEXITCODE -ne 0)
    {
        throw "git diff failed for ${Range}: $($output -join ' ')"
    }

    return @($output | Where-Object { $_ })
}

function Write-ImpactLines
{
    param([string[]]$ChangedPaths, $Plan)

    foreach ($path in $ChangedPaths)
    {
        Write-Host "CHANGED $path"
    }

    $impact = Get-Impact $ChangedPaths
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

    $trains = @($Plan.trains)
    Write-Host "release-trains: $(if ($trains.Count -gt 0) { $trains -join ',' } else { 'none' })"
    if ($Plan.Contains('perTrain'))
    {
        foreach ($train in $Plan.perTrain.Keys)
        {
            $entry = $Plan.perTrain[$train]
            $tag = if ($null -eq $entry.baseline) { 'none' } else { $entry.baseline }
            Write-Host "release-train-baseline: $train $tag selected=$($entry.selected.ToString().ToLowerInvariant())"
        }
    }

    foreach ($path in @($Plan.unclassifiedPaths))
    {
        Write-Host "UNCLASSIFIED $path"
    }
}

# ---------------------------------------------------------------------------------------------------------------------
# Self-test
# ---------------------------------------------------------------------------------------------------------------------

function Invoke-Self
{
    # Runs this script as a child process; returns the output lines and the exit code.
    param([string[]]$Arguments, [string]$WorkingDirectory = $script:RepositoryRoot)

    Push-Location $WorkingDirectory
    try
    {
        $lines = @(& pwsh -NoProfile -File $PSCommandPath @Arguments 2>&1 | ForEach-Object { "$_" })
        return [pscustomobject]@{ Lines = $lines; ExitCode = $LASTEXITCODE }
    }
    finally
    {
        Pop-Location
    }
}

function Test-QualificationCases
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

    $marker = Get-Impact @('release/refresh-aliases.marker')
    if (-not $marker.Required -or $marker.Matches[0].Rule -ne 'release marker')
    {
        throw 'SelfTest failed: the release marker must require desktop qualification.'
    }

    return $cases.Count
}

function Test-ClassificationFixture
{
    $fixture = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'tests/release-impact/classification.json') -Raw | ConvertFrom-Json
    foreach ($case in $fixture.cases)
    {
        $entry = Get-PathClass $case.path
        if ($case.class -eq 'unclassified')
        {
            if ($null -ne $entry)
            {
                throw "classification: '$($case.path)' should be unclassified, got $($entry.Class)."
            }

            continue
        }

        if ($null -eq $entry -or $entry.Class -ne $case.class)
        {
            throw "classification: '$($case.path)' expected $($case.class), got $(if ($null -eq $entry) { 'unclassified' } else { $entry.Class })."
        }

        $trains = (Get-Classification @($case.path)).Trains -join ','
        if ($trains -ne $case.trains)
        {
            throw "classification: '$($case.path)' expected trains '$($case.trains)', got '$trains'."
        }

        $required = (Get-Impact @($case.path)).Required
        if ($required -ne [bool]$case.qualification)
        {
            throw "classification: '$($case.path)' expected qualification $($case.qualification), got $required."
        }
    }

    return @($fixture.cases).Count
}

function Test-BaselineFixtures
{
    $files = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'tests/release-impact') -Filter 'baseline-*.json' | Sort-Object Name)
    if ($files.Count -eq 0)
    {
        throw 'No baseline fixtures in scripts/tests/release-impact.'
    }

    foreach ($file in $files)
    {
        $fixture = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
        foreach ($train in @('desktop', 'agent'))
        {
            $expected = $fixture.expected.$train
            $baseline = Get-TrainBaseline @($fixture.releases) $train
            $got = if ($null -eq $baseline) { $null } else { [string]$baseline.tag_name }
            if ($got -ne $expected)
            {
                throw "$($file.Name): $train baseline expected '$expected', got '$got'."
            }
        }
    }

    return $files.Count
}

function Test-PlanFixture
{
    $fixture = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'tests/release-impact/plan-per-train.json') -Raw | ConvertFrom-Json
    $diffs = $fixture.diffsSinceTag
    $provider = { param([string]$Tag) @($diffs.$Tag) }.GetNewClosure()
    $plan = New-ReleasePlan -Base 'v' -Head 'h' -ChangedPaths @($fixture.changed) -Security $true -Releases @($fixture.releases) -GetPathsSince $provider
    foreach ($train in @('desktop', 'agent'))
    {
        $want = $fixture.expected.$train
        $got = $plan.perTrain[$train]
        if ($got.baseline -ne $want.baseline -or $got.selected -ne [bool]$want.selected)
        {
            throw "plan-per-train: $train expected baseline $($want.baseline) selected=$($want.selected), got $($got.baseline) selected=$($got.selected)."
        }
    }

    if (-not $plan.security -or $plan.schemaVersion -ne 1)
    {
        throw 'plan-per-train: security and schemaVersion were not recorded.'
    }

    $json = $plan | ConvertTo-Json -Depth 8 | ConvertFrom-Json
    foreach ($property in @('schemaVersion', 'base', 'head', 'changedPaths', 'trains', 'desktopQualification', 'unclassifiedPaths', 'security', 'perTrain'))
    {
        if ($null -eq $json.PSObject.Properties[$property])
        {
            throw "plan JSON lacks '$property'."
        }
    }
}

function Test-UnclassifiedBaselineDiff
{
    $fixture = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'tests/release-impact/plan-unclassified-in-baseline-diff.json') -Raw | ConvertFrom-Json
    $diffs = $fixture.diffsSinceTag
    $provider = { param([string]$Tag) @($diffs.$Tag) }.GetNewClosure()
    $plan = New-ReleasePlan -Base 'b' -Head 'h' -ChangedPaths @($fixture.changed) -Releases @($fixture.releases) -GetPathsSince $provider
    $wanted = @($fixture.expected.unclassified)
    if (($plan.unclassifiedPaths -join '|') -ne ($wanted -join '|'))
    {
        throw "plan-unclassified-in-baseline-diff: unclassifiedPaths expected [$($wanted -join ', ')], got [$($plan.unclassifiedPaths -join ', ')]."
    }

    if ($plan.perTrain['desktop'].selected -ne [bool]$fixture.expected.desktopSelected -or $plan.perTrain['agent'].selected -ne [bool]$fixture.expected.agentSelected)
    {
        throw 'plan-unclassified-in-baseline-diff: the selected trains differ from the fixture.'
    }

    $repository = Join-Path ([IO.Path]::GetTempPath()) "release-impact-git-$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $repository -Force | Out-Null
    try
    {
        $git = { & git -C $repository -c user.name=selftest -c user.email=selftest@example.invalid -c commit.gpgsign=false -c core.autocrlf=false @args 2>&1 | Out-Null; if ($LASTEXITCODE -ne 0) { throw "git $($args -join ' ') failed in the self-test repository." } }
        $commit = {
            param([string]$Path, [string]$Message)
            $target = Join-Path $repository $Path
            New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
            Set-Content -LiteralPath $target -Value $Message
            & $git @('add', '--', $Path)
            & $git @('commit', '-m', $Message)
        }
        & $git @('init', '-q')
        & $commit 'docs/start.md' 'start'
        & $git @('tag', 'v6.7.10')
        & $commit 'new-shipping-library/Code.cs' 'unclassified code'
        & $commit 'docs/desktop-only.md' 'desktop-only release'
        & $git @('tag', 'v6.7.12')
        & $commit 'docs/a.md' 'docs'

        $releasesFile = Join-Path $repository 'releases.json'
        ConvertTo-Json -InputObject @($fixture.releases) -Depth 8 | Set-Content -LiteralPath $releasesFile
        $run = Invoke-Self @('-Base', 'v6.7.12', '-Head', 'HEAD', '-ReleasesFile', $releasesFile) $repository
        if ($run.ExitCode -ne 3 -or 'UNCLASSIFIED new-shipping-library/Code.cs' -notin $run.Lines -or 'desktop-qualification: not-required' -notin $run.Lines)
        {
            throw "an unclassified path in a baseline diff must print UNCLASSIFIED and exit 3 (exit $($run.ExitCode): $($run.Lines -join ' | '))."
        }
    }
    finally
    {
        Remove-Item -LiteralPath $repository -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Test-OutputContract
{
    $qualification = '^desktop-qualification:\s*(required|not-required)\s*$'
    $cases = @(
        @{ Paths = 'README.md;docs/notes.md'; Decision = 'not-required'; Trains = 'none' }
        @{ Paths = 'Pointframe.Cli/Program.cs'; Decision = 'required'; Trains = 'agent' }
        @{ Paths = 'Pointframe/App.xaml.cs'; Decision = 'not-required'; Trains = 'desktop' }
        @{ Paths = 'release/refresh-aliases.marker'; Decision = 'required'; Trains = 'desktop' }
    )
    foreach ($case in $cases)
    {
        $run = Invoke-Self @('-Paths', $case.Paths)
        $lines = $run.Lines
        $decisionLines = @($lines | Where-Object { $_ -match $qualification })
        if ($run.ExitCode -ne 0 -or $decisionLines.Count -ne 1 -or $decisionLines[0] -ne "desktop-qualification: $($case.Decision)")
        {
            throw "output contract for '$($case.Paths)': exit $($run.ExitCode), decision lines [$($decisionLines -join '|')]."
        }

        $decisionIndex = [array]::IndexOf($lines, $decisionLines[0])
        $trainsIndex = [array]::IndexOf($lines, "release-trains: $($case.Trains)")
        if ($trainsIndex -lt 0 -or $trainsIndex -lt $decisionIndex)
        {
            throw "output contract for '$($case.Paths)': 'release-trains: $($case.Trains)' must follow the decision line."
        }

        $changed = @($lines | Where-Object { $_ -like 'CHANGED *' })
        if ($changed.Count -ne @($case.Paths -split ';').Count -or [array]::IndexOf($lines, $changed[-1]) -gt $decisionIndex)
        {
            throw "output contract for '$($case.Paths)': CHANGED lines must come first."
        }

        $ruleLines = @($lines | Where-Object { $_ -match '\[rule: ' })
        if (($case.Decision -eq 'required') -ne ($ruleLines.Count -gt 0))
        {
            throw "output contract for '$($case.Paths)': [rule: ...] lines do not match the decision."
        }
    }

    $unknown = Invoke-Self @('-Paths', 'README.md;strange-new-root/file.txt')
    if ($unknown.ExitCode -ne 3 -or 'UNCLASSIFIED strange-new-root/file.txt' -notin $unknown.Lines)
    {
        throw "an unknown path must print UNCLASSIFIED and exit 3 (exit $($unknown.ExitCode): $($unknown.Lines -join ' | '))."
    }

    $decisionFirst = @($unknown.Lines | Where-Object { $_ -match $qualification }).Count
    if ($decisionFirst -ne 1)
    {
        throw 'an unclassified run must still print exactly one desktop-qualification line.'
    }

    $bad = Invoke-Self @('-Base', 'not a revision')
    if ($bad.ExitCode -ne 2)
    {
        throw "a bad revision must exit 2, got $($bad.ExitCode)."
    }

    $empty = Invoke-Self @('-Base', 'HEAD', '-Head', 'HEAD')
    if ($empty.ExitCode -ne 0 -or 'desktop-qualification: not-required' -notin $empty.Lines)
    {
        throw "an empty git diff must be not-required with exit 0 (exit $($empty.ExitCode))."
    }

    $planFile = Join-Path ([IO.Path]::GetTempPath()) "release-plan-$([guid]::NewGuid().ToString('N')).json"
    try
    {
        $planRun = Invoke-Self @('-Paths', 'Pointframe.Engine/A.cs;docs/a.md', '-PlanOut', $planFile, '-Security')
        $plan = Get-Content -LiteralPath $planFile -Raw | ConvertFrom-Json
        if ($planRun.ExitCode -ne 0 -or @($plan.trains).Count -ne 2 -or -not $plan.desktopQualification.required -or -not $plan.security -or @($plan.changedPaths).Count -ne 2)
        {
            throw "-PlanOut did not record the plan (exit $($planRun.ExitCode))."
        }
    }
    finally
    {
        Remove-Item -LiteralPath $planFile -Force -ErrorAction SilentlyContinue
    }
}

function Test-TrackedTree
{
    $tracked = @(& git -C $script:RepositoryRoot ls-files 2>&1)
    if ($LASTEXITCODE -ne 0 -or $tracked.Count -eq 0)
    {
        throw "git ls-files failed: $($tracked -join ' ')"
    }

    $scan = Get-Classification $tracked
    if ($scan.Unclassified.Count -gt 0)
    {
        throw "UNCLASSIFIED $($scan.Unclassified[0]) (and $($scan.Unclassified.Count - 1) more): classify it in `$script:ClassRules."
    }

    return $tracked.Count
}

function Test-CurrentReleaseRerun
{
    $fixture = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'tests/release-impact/plan-current-release-rerun.json') -Raw | ConvertFrom-Json
    $provider = { param([string]$Tag) @() }
    $plan = New-ReleasePlan -Base 'b' -Head 'h' -ChangedPaths @() -Releases @($fixture.release) -CurrentVersion $fixture.currentVersion -GetPathsSince $provider
    foreach ($train in @('desktop', 'agent'))
    {
        if ([bool]$plan.perTrain[$train].selected -ne [bool]$fixture.expected.$train)
        {
            throw "current-release rerun fixture: $train expected selected=$($fixture.expected.$train), got $($plan.perTrain[$train].selected)."
        }
    }

    $staleFixture = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'tests/release-impact/plan-stale-rerun.json') -Raw | ConvertFrom-Json
    $stale = New-ReleasePlan -Base 'b' -Head 'h' -ChangedPaths @() -Releases @($staleFixture.releases) -CurrentVersion $staleFixture.currentVersion -GetPathsSince $provider
    foreach ($train in @('desktop', 'agent'))
    {
        $expectedTag = if ($train -eq 'desktop') { 'v1.4.0' } else { 'v1.5.0' }
        if (-not $stale.perTrain[$train].stale -or $stale.perTrain[$train].selected -or $stale.perTrain[$train].baseline -ne $expectedTag)
        {
            throw "stale rerun fixture: $train should be marked stale and remain unselected."
        }
    }
}

if ($SelfTest)
{
    $qualificationCases = Test-QualificationCases
    $classificationCases = Test-ClassificationFixture
    $baselineFixtures = Test-BaselineFixtures
    Test-PlanFixture
    Test-UnclassifiedBaselineDiff
    Test-OutputContract
    Test-CurrentReleaseRerun
    $trackedPaths = Test-TrackedTree
    Write-Host "release-impact self-test passed: $qualificationCases qualification cases, $classificationCases class cases, $baselineFixtures baseline fixtures, per-train plan, current-release and stale reruns, unclassified path in a baseline diff, output contract, $trackedPaths tracked paths classified."
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

try
{
    $changed = if ($PSBoundParameters.ContainsKey('Paths')) { @($Paths -split ';' | Where-Object { $_ }) } else { Get-GitPaths "$Base...$Head" }

    $releases = $null
    if ($ReleasesFile)
    {
        if (-not (Test-Path -LiteralPath $ReleasesFile -PathType Leaf))
        {
            throw "-ReleasesFile does not exist: $ReleasesFile"
        }

        $releases = @(Get-Content -LiteralPath $ReleasesFile -Raw | ConvertFrom-Json)
    }

    $since = { param([string]$Tag) Get-GitPaths "refs/tags/$Tag...$Head" }
    $plan = New-ReleasePlan -Base $Base -Head $Head -ChangedPaths $changed -Security ([bool]$Security) -Releases $releases -CurrentVersion $CurrentVersion -GetPathsSince $since
}
catch
{
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 2
}

Write-ImpactLines $changed $plan

if ($PlanOut)
{
    $plan | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $PlanOut -Encoding utf8NoBOM
}

if (@($plan.unclassifiedPaths).Count -gt 0)
{
    exit 3
}

exit 0
