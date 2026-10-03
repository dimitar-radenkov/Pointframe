#Requires -Version 7.0
<#
The Pointframe knowledge base tool: docs/knowledge-base/knowledge-base.md (cross-cutting) and
docs/knowledge-base/features/*.md (one file per feature area). The /kb-read, /kb-write, and /kb-check skills call it.

  pwsh scripts/kb.ps1 read <path|area|F-NN|topic> [...] [-All]   # what to read: sections, features, top 5 lessons (-All: every lesson)
  pwsh scripts/kb.ps1 read                                       # the Feature index and the area files
  pwsh scripts/kb.ps1 check                                      # refresh generated blocks, then check everything; exit 1 on errors
  pwsh scripts/kb.ps1 check -NoFix                               # check only, never write (CI)
  pwsh scripts/kb.ps1 changed [-Base <ref>]                      # sections and features to review for a diff
  pwsh scripts/kb.ps1 hook                                       # Claude Code hook: payload JSON on stdin, guidance as additionalContext

Generated: the main file's table of contents and Feature index, and the Codex copies of the kb-* skills in
.agents/skills/.

Checks, in every file: backticked repo paths exist and are not gitignored; "- Lesson: <heading>" matches a
"## " heading in lessons.md; links resolve to a heading in the target file; no two headings in a file share
an anchor; a section has at most one "**Files.**" line. Feature rows: seven cells, unique F-NN IDs, an entry
point, a linked section, telemetry events that exist, every TelemetryEvents event and every smoke test
claimed. File map rows: three cells, short descriptions, no dead patterns, every tracked file covered, no
redundant row. Lessons that no section links are reported as warnings.
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('read', 'check', 'changed', 'hook')]
    [string]$Command = 'check',
    [Parameter(Position = 1, ValueFromRemainingArguments = $true)]
    [string[]]$Targets,
    [switch]$All,
    [switch]$NoFix,
    [string]$Base = 'HEAD'
)

$usage = 'Usage: kb.ps1 read <path|area|F-NN|topic> [...] [-All] | check [-NoFix] | changed [-Base <ref>] | hook'
$Targets = @($Targets | Where-Object { $_ })
$stray = @($Targets | Where-Object { $_.StartsWith('-') })
if ($stray.Count -gt 0 -or ($Targets.Count -gt 0 -and $Command -ne 'read'))
{
    Write-Host "ERROR unexpected arguments: $($Targets -join ' '). $usage"
    exit 2
}
$For = @(if ($Command -eq 'read') { $Targets })
$ReadMode = $Command -eq 'read'
$Changed = $Command -eq 'changed'
$Hook = $Command -eq 'hook'
$Check = $Command -eq 'check' -and $NoFix

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$KbDir = Join-Path $RepoRoot 'docs' 'knowledge-base'
$MainPath = Join-Path $KbDir 'knowledge-base.md'
$FeatureDir = Join-Path $KbDir 'features'
$LessonsPath = Join-Path $RepoRoot 'lessons.md'
$TocMarkers = @('<!-- toc -->', '<!-- /toc -->')
$IndexMarkers = @('<!-- features -->', '<!-- /features -->')
$DecisionMarkers = @('<!-- decisions -->', '<!-- /decisions -->')
$FileMapHeading = 'File map'
$FeaturesHeading = 'Features'
$MaxDescriptionLength = 160

if (-not (Test-Path $MainPath))
{
    if ($Hook) { exit 0 }
    Write-Host "ERROR knowledge base not found: $MainPath"
    exit 1
}

$errors = [System.Collections.Generic.List[string]]::new()
$notes = [System.Collections.Generic.List[string]]::new()

function ConvertTo-Anchor([string]$Heading)
{
    $a = $Heading.Trim().ToLowerInvariant()
    $a = $a -replace '[^\p{L}\p{N}\s-]', ''
    return ($a -replace '\s', '-')
}

function ConvertTo-RepoRelative([string]$FullPath)
{
    return ([System.IO.Path]::GetRelativePath($RepoRoot, $FullPath) -replace '\\', '/')
}

# Repo-root-anchored glob: '**' crosses folders, '*' and '?' stay inside one, a trailing '/' means the whole folder.
function ConvertFrom-Glob([string]$Glob)
{
    if ($Glob.EndsWith('/'))
    {
        $Glob += '**'
    }
    $sb = [System.Text.StringBuilder]::new('^')
    for ($i = 0; $i -lt $Glob.Length; $i++)
    {
        $c = $Glob[$i]
        if ($c -eq '*' -and $i + 1 -lt $Glob.Length -and $Glob[$i + 1] -eq '*')
        {
            if ($i + 2 -lt $Glob.Length -and $Glob[$i + 2] -eq '/')
            {
                [void]$sb.Append('(?:.*/)?')
                $i += 2
            }
            else
            {
                [void]$sb.Append('.*')
                $i += 1
            }
        }
        elseif ($c -eq '*')
        {
            [void]$sb.Append('[^/]*')
        }
        elseif ($c -eq '?')
        {
            [void]$sb.Append('[^/]')
        }
        else
        {
            [void]$sb.Append([regex]::Escape([string]$c))
        }
    }
    [void]$sb.Append('$')
    return [regex]::new($sb.ToString(), [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
}

$script:trackedCache = $null
function Get-TrackedCached
{
    if ($null -eq $script:trackedCache)
    {
        try
        {
            $out = & git -C $RepoRoot ls-files 2>$null
            $script:trackedCache = if ($LASTEXITCODE -eq 0) { @($out | Where-Object { $_ }) } else { @() }
        }
        catch
        {
            $script:trackedCache = @()
        }
    }
    return $script:trackedCache
}

# Splits "| a | b | c |" into cells, ignoring pipes that are escaped (\|) or inside a backtick span.
function Split-TableRow([string]$Row)
{
    $cells = [System.Collections.Generic.List[string]]::new()
    $sb = [System.Text.StringBuilder]::new()
    $inTick = $false
    $text = $Row.Trim().Trim('|')
    for ($k = 0; $k -lt $text.Length; $k++)
    {
        $c = $text[$k]
        if ($c -eq '`')
        {
            $inTick = -not $inTick
        }
        if ($c -eq '\' -and $k + 1 -lt $text.Length -and $text[$k + 1] -eq '|')
        {
            [void]$sb.Append('|')
            $k++
            continue
        }
        if ($c -eq '|' -and -not $inTick)
        {
            $cells.Add($sb.ToString().Trim())
            [void]$sb.Clear()
            continue
        }
        [void]$sb.Append($c)
    }
    $cells.Add($sb.ToString().Trim())
    return , $cells
}

# ---------------------------------------------------------------- documents

# One knowledge base file: its lines, which lines are inside code fences, and its headings. A heading owns the
# lines up to the next heading of the same or a higher level. Sections are the "### " topics of the main file and
# the "## " topics of an area file.
function Read-Doc([string]$Path, [bool]$IsMain)
{
    $text = [System.IO.File]::ReadAllText($Path)
    $lines = $text -split "\r?\n"
    $doc = [pscustomobject]@{
        Path = $Path
        Rel = ConvertTo-RepoRelative $Path
        Name = ([System.IO.Path]::GetRelativePath($KbDir, $Path) -replace '\\', '/')
        IsMain = $IsMain
        Newline = if ($text.Contains("`r`n")) { "`r`n" } else { "`n" }
        Lines = $lines
        Fenced = [bool[]]::new($lines.Length)
        Headings = [System.Collections.Generic.List[object]]::new()
        Title = ''
    }
    $inFence = $false
    for ($i = 0; $i -lt $lines.Length; $i++)
    {
        if ($lines[$i] -match '^\s*```')
        {
            $inFence = -not $inFence
            $doc.Fenced[$i] = $true
            continue
        }
        if ($inFence)
        {
            $doc.Fenced[$i] = $true
            continue
        }
        if ($lines[$i] -match '^(#{1,3})\s+(.+?)\s*$')
        {
            $doc.Headings.Add([pscustomobject]@{
                Level = $Matches[1].Length
                Text = $Matches[2]
                Anchor = ConvertTo-Anchor $Matches[2]
                Line = $i
                EndLine = $lines.Length - 1
                Doc = $doc
                IsSection = $false
            })
        }
    }
    for ($k = 0; $k -lt $doc.Headings.Count; $k++)
    {
        $h = $doc.Headings[$k]
        for ($n = $k + 1; $n -lt $doc.Headings.Count; $n++)
        {
            if ($doc.Headings[$n].Level -le $h.Level)
            {
                $h.EndLine = $doc.Headings[$n].Line - 1
                break
            }
        }
        $h.IsSection = if ($IsMain) { $h.Level -eq 3 } else { $h.Level -eq 2 -and $h.Text -ne $FeaturesHeading }
        if ($h.Level -eq 1 -and -not $doc.Title)
        {
            $doc.Title = $h.Text
        }
    }
    return $doc
}

$mainDoc = Read-Doc $MainPath $true
$docs = [System.Collections.Generic.List[object]]::new()
$docs.Add($mainDoc)
# Other files next to the main one (decisions.md), then one file per feature area.
foreach ($f in Get-ChildItem -Path $KbDir -Filter '*.md' -File | Where-Object { $_.FullName -ne $MainPath } | Sort-Object Name)
{
    $docs.Add((Read-Doc $f.FullName $false))
}
if (Test-Path $FeatureDir)
{
    foreach ($f in Get-ChildItem -Path $FeatureDir -Filter '*.md' -File | Sort-Object Name)
    {
        $docs.Add((Read-Doc $f.FullName $false))
    }
}
$featureDocs = @($docs | Where-Object { $_.Name.StartsWith('features/') })

# Decisions ("D-NNN Title" sections) live in decisions.md or in the area file they govern; numbers are global.
$decisions = @(foreach ($doc in $docs)
    {
        foreach ($h in $doc.Headings | Where-Object { $_.IsSection -and $_.Text -match '^D-(\d{3}) ' })
        {
            [pscustomobject]@{ Number = [int]$h.Text.Substring(2, 3); Heading = $h }
        }
    })

# Anchors are per file: "features/recording.md#recording-pipeline".
$anchorIndex = @{}
foreach ($doc in $docs)
{
    foreach ($h in $doc.Headings)
    {
        $key = "$($doc.Name)#$($h.Anchor)"
        if ($anchorIndex.ContainsKey($key))
        {
            $errors.Add("$($doc.Rel):$($h.Line + 1): heading '$($h.Text)' produces the same anchor as an earlier heading in this file; rename one")
        }
        else
        {
            $anchorIndex[$key] = $h
        }
    }
}

# Resolves a markdown link target written in $Doc. Returns @{ Kind = 'heading'|'file'|'external'|'broken'; Heading; Name }.
function Resolve-Link($Doc, [string]$Target)
{
    if ($Target -match '^[a-z]+:' )
    {
        return @{ Kind = 'external' }
    }
    $file, $anchor = if ($Target.Contains('#')) { $Target.Split('#', 2) } else { @($Target, '') }
    if ($file -eq '')
    {
        $name = $Doc.Name
    }
    else
    {
        $full = [System.IO.Path]::GetFullPath((Join-Path (Split-Path $Doc.Path -Parent) $file))
        if (-not $full.StartsWith($KbDir, [System.StringComparison]::OrdinalIgnoreCase))
        {
            return @{ Kind = if (Test-Path -LiteralPath $full) { 'external' } else { 'broken' } }
        }
        $name = [System.IO.Path]::GetRelativePath($KbDir, $full) -replace '\\', '/'
        if (-not ($docs | Where-Object { $_.Name -eq $name }))
        {
            return @{ Kind = 'broken' }
        }
    }
    if ($anchor -eq '')
    {
        return @{ Kind = 'file'; Name = $name }
    }
    $key = "$name#$anchor"
    if ($anchorIndex.ContainsKey($key))
    {
        return @{ Kind = 'heading'; Heading = $anchorIndex[$key]; Name = $name }
    }
    return @{ Kind = 'broken' }
}

function Get-LinkedHeadings($Doc, [string]$Cell)
{
    return @([regex]::Matches($Cell, '\]\(([^)\s]+)\)') | ForEach-Object {
            $r = Resolve-Link $Doc $_.Groups[1].Value
            if ($r.Kind -eq 'heading') { $r.Heading }
        })
}

function Get-SectionLessons($Section)
{
    $found = [System.Collections.Generic.List[string]]::new()
    for ($k = $Section.Line; $k -le $Section.EndLine; $k++)
    {
        if ($Section.Doc.Lines[$k] -match '^\s*-\s*Lesson:\s*(.+?)\s*$')
        {
            $found.Add($Matches[1])
        }
    }
    return $found
}

function Format-Section($Section)
{
    return "$($Section.Doc.Rel):$($Section.Line + 1)  $($Section.Text)"
}

# ---------------------------------------------------------------- File map (main file)

$fileMap = [System.Collections.Generic.List[object]]::new()
$mapHeading = $mainDoc.Headings | Where-Object { $_.Level -eq 2 -and $_.Text -eq $FileMapHeading } | Select-Object -First 1
if ($mapHeading)
{
    for ($i = $mapHeading.Line + 1; $i -le $mapHeading.EndLine; $i++)
    {
        if ($mainDoc.Fenced[$i] -or -not $mainDoc.Lines[$i].TrimStart().StartsWith('|'))
        {
            continue
        }
        $cells = Split-TableRow $mainDoc.Lines[$i]
        $patterns = @([regex]::Matches($cells[0], '`([^`]+)`') | ForEach-Object { $_.Groups[1].Value })
        if ($patterns.Count -eq 0)
        {
            continue
        }
        if ($cells.Count -ne 3)
        {
            $errors.Add("$($mainDoc.Rel):$($i + 1): File map row needs three cells (Path, What lives here, Read first), found $($cells.Count)")
            continue
        }
        $fileMap.Add([pscustomobject]@{
            Line = $i
            Patterns = $patterns
            Regexes = @($patterns | ForEach-Object { ConvertFrom-Glob $_ })
            Sections = Get-LinkedHeadings $mainDoc $cells[2]
            Description = $cells[1]
        })
    }
}

function Test-RowMatch($Row, [string]$Path)
{
    foreach ($r in $Row.Regexes)
    {
        if ($r.IsMatch($Path))
        {
            return $true
        }
    }
    return $false
}

function Get-RowFileCount($Row)
{
    if (-not $Row.PSObject.Properties['FileCount'])
    {
        $count = @(Get-TrackedCached | Where-Object { Test-RowMatch $Row $_ }).Count
        $Row | Add-Member -NotePropertyName FileCount -NotePropertyValue ([Math]::Max(1, $count))
    }
    return $Row.FileCount
}

# ---------------------------------------------------------------- features (area files)

# "| F-NN | Feature | Triggered from | Entry point | Telemetry | Tests | Read first |". IDs are stable: never renumbered or reused.
$features = [System.Collections.Generic.List[object]]::new()
foreach ($doc in $featureDocs)
{
    for ($i = 0; $i -lt $doc.Lines.Length; $i++)
    {
        if ($doc.Fenced[$i] -or -not $doc.Lines[$i].TrimStart().StartsWith('|'))
        {
            continue
        }
        $cells = Split-TableRow $doc.Lines[$i]
        if ($cells[0] -notmatch '^F-\d{2,3}$')
        {
            continue
        }
        if ($cells.Count -ne 7)
        {
            $errors.Add("$($doc.Rel):$($i + 1): feature row needs seven cells (ID, Feature, Triggered from, Entry point, Telemetry, Tests, Read first), found $($cells.Count)")
            continue
        }
        $pathsIn = {
            param($Cell)
            @([regex]::Matches($Cell, '`([^`]+)`') | ForEach-Object { $_.Groups[1].Value } | Where-Object { $_ -match '/' -and $_ -notmatch '\s' })
        }
        $features.Add([pscustomobject]@{
            Doc = $doc
            Line = $i
            Id = $cells[0]
            Name = $cells[1]
            EntryPaths = & $pathsIn $cells[3]
            TestPaths = & $pathsIn $cells[5]
            Events = @([regex]::Matches($cells[4], '`([a-z0-9_]+)`') | ForEach-Object { $_.Groups[1].Value })
            Sections = Get-LinkedHeadings $doc $cells[6]
        })
    }
}

# Features whose entry point or tests name the path (a folder matches every path under it).
function Get-FeaturesForPath([string]$Path)
{
    return @($features | Where-Object {
            @(@($_.EntryPaths) + @($_.TestPaths) | Where-Object {
                    if ($Path.EndsWith('/')) { $_.StartsWith($Path, [System.StringComparison]::OrdinalIgnoreCase) }
                    else { [string]::Equals($_, $Path, [System.StringComparison]::OrdinalIgnoreCase) }
                }).Count -gt 0
        })
}

# ---------------------------------------------------------------- guidance for a path

# Turns a user path (relative to -BaseDir or the current folder, absolute, either slash) into a repo-relative one.
# Returns $null outside the repo. A folder gets exactly one trailing '/'.
function Resolve-RepoPath([string]$Raw, [string]$BaseDir = (Get-Location).Path)
{
    $full = [System.IO.Path]::GetFullPath($Raw, $BaseDir)
    $rel = ((ConvertTo-RepoRelative $full)).TrimEnd('/')
    if ($rel -eq '.' -or $rel -eq '..' -or $rel.StartsWith('../') -or [System.IO.Path]::IsPathRooted($rel))
    {
        return $null
    }
    if (Test-Path -LiteralPath $full -PathType Container)
    {
        return "$rel/"
    }
    return $rel
}

function Get-SectionKey($Section)
{
    return "$($Section.Doc.Name)#$($Section.Anchor)"
}

# File map rows, the sections they link, the features of the path and their sections, and other sections naming
# the path. Weight: a narrow File map row beats a catch-all; a feature's own sections weigh like a narrow row;
# a section that only names the path weighs least.
function Get-Guidance([string]$Path)
{
    if ($Path.EndsWith('/'))
    {
        $under = @(Get-TrackedCached | Where-Object { $_.StartsWith($Path, [System.StringComparison]::OrdinalIgnoreCase) })
        $rows = @($fileMap | Where-Object { $row = $_; $under | Where-Object { Test-RowMatch $row $_ } | Select-Object -First 1 })
        $needle = "``$Path"
    }
    else
    {
        $rows = @($fileMap | Where-Object { Test-RowMatch $_ $Path })
        $needle = "``$Path``"
    }

    $weights = [ordered]@{}
    $byKey = @{}
    $add = {
        param($Section, [double]$Weight)
        $key = Get-SectionKey $Section
        $byKey[$key] = $Section
        if (-not $weights.Contains($key) -or $weights[$key] -lt $Weight)
        {
            $weights[$key] = $Weight
        }
    }
    foreach ($row in $rows)
    {
        $w = 1.0 / (1.0 + [Math]::Log10((Get-RowFileCount $row)))
        foreach ($s in $row.Sections) { & $add $s $w }
    }
    $pathFeatures = @(Get-FeaturesForPath $Path)
    foreach ($f in $pathFeatures)
    {
        foreach ($s in $f.Sections) { & $add $s 0.8 }
    }
    $mentions = @(foreach ($doc in $docs)
        {
            foreach ($h in $doc.Headings | Where-Object { $_.IsSection })
            {
                if (-not $weights.Contains((Get-SectionKey $h)) -and
                    (($doc.Lines[$h.Line..$h.EndLine] -join "`n").IndexOf($needle, [System.StringComparison]::OrdinalIgnoreCase) -ge 0))
                {
                    $h
                }
            }
        })
    return [pscustomobject]@{
        Path = $Path
        Exists = Test-Path -LiteralPath (Join-Path $RepoRoot $Path)
        Rows = $rows
        Features = @($pathFeatures)
        ReadFirst = @($weights.Keys | ForEach-Object { $byKey[$_] })
        Weights = $weights
        Mentions = $mentions
    }
}

$StopTokens = @('cs', 'xaml', 'json', 'md', 'ps1', 'i', 'service', 'services', 'window', 'windows', 'view', 'views',
    'model', 'models', 'viewmodel', 'viewmodels', 'test', 'tests', 'pointframe', 'helper', 'helpers', 'the', 'and', 'for',
    'handler', 'handlers', 'infrastructure', 'directory', 'package', 'packages', 'props', 'iss', 'csproj', 'docs', 'src')

# Name tokens of every path segment: "Pointframe/Views/OverlayWindow.RecordingHud.cs" -> overlay, recording, hud.
function Get-PathTokens([string[]]$Paths)
{
    $tokens = [System.Collections.Generic.HashSet[string]]::new()
    foreach ($p in $Paths)
    {
        foreach ($part in ($p.TrimEnd('/') -split '/'))
        {
            foreach ($m in [regex]::Matches($part, '[A-Z]{2,}(?=[A-Z][a-z]|\b|[^A-Za-z])|[A-Z]?[a-z]+|[A-Z]+'))
            {
                $t = $m.Value.ToLowerInvariant()
                if ($t.Length -ge 3 -and $StopTokens -notcontains $t)
                {
                    [void]$tokens.Add($t)
                }
            }
        }
    }
    return @($tokens)
}

# Lessons of all guided sections, ranked: name-token relevance (IDF-weighted, so a rare word such as
# "hud" beats a common one such as "recording") first, then the weight of the section they come from.
function Get-RankedLessons($Guidances, [string[]]$Paths)
{
    $sectionWeight = @{}
    $sections = @{}
    foreach ($g in $Guidances)
    {
        foreach ($s in $g.ReadFirst)
        {
            $key = Get-SectionKey $s
            $sections[$key] = $s
            if (-not $sectionWeight.ContainsKey($key) -or $sectionWeight[$key] -lt $g.Weights[$key])
            {
                $sectionWeight[$key] = $g.Weights[$key]
            }
        }
        foreach ($s in $g.Mentions)
        {
            $key = Get-SectionKey $s
            $sections[$key] = $s
            if (-not $sectionWeight.ContainsKey($key))
            {
                $sectionWeight[$key] = 0.2
            }
        }
    }
    $candidates = [ordered]@{}
    foreach ($key in $sectionWeight.Keys)
    {
        foreach ($l in (Get-SectionLessons $sections[$key]))
        {
            if (-not $candidates.Contains($l) -or $candidates[$l] -lt $sectionWeight[$key])
            {
                $candidates[$l] = $sectionWeight[$key]
            }
        }
    }
    $n = $candidates.Count
    $idf = @{}
    foreach ($t in (Get-PathTokens $Paths))
    {
        $rx = [regex]::new("\b$([regex]::Escape($t))", 'IgnoreCase')
        $df = @($candidates.Keys | Where-Object { $rx.IsMatch($_) }).Count
        if ($df -gt 0)
        {
            $idf[$t] = [pscustomobject]@{ Regex = $rx; Weight = [Math]::Log(($n + 1) / $df) }
        }
    }
    $order = 0
    $ranked = foreach ($l in $candidates.Keys)
    {
        $relevance = 0.0
        foreach ($entry in $idf.Values)
        {
            if ($entry.Regex.IsMatch($l))
            {
                $relevance += $entry.Weight
            }
        }
        [pscustomobject]@{ Heading = $l; Score = $relevance + $candidates[$l]; Order = $order++ }
    }
    return @($ranked | Sort-Object @{ Expression = 'Score'; Descending = $true }, Order | ForEach-Object { $_.Heading })
}

if (($ReadMode -or $Changed -or $Hook) -and -not $mapHeading)
{
    if ($Hook) { exit 0 }
    Write-Host "ERROR no '## $FileMapHeading' section in $($mainDoc.Rel)"
    exit 1
}

# ---------------------------------------------------------------- read

function Write-Area($Doc)
{
    Write-Host "$($Doc.Rel)  $($Doc.Title)" -ForegroundColor Cyan
    foreach ($f in $features | Where-Object { $_.Doc -eq $Doc })
    {
        Write-Host "  feature $($f.Id) $($f.Name)  (line $($f.Line + 1))"
    }
    foreach ($h in $Doc.Headings | Where-Object { $_.IsSection })
    {
        Write-Host "  section $($Doc.Rel):$($h.Line + 1)  $($h.Text)"
    }
}

function Write-Feature($Feature)
{
    $tests = @($Feature.TestPaths)
    $events = @($Feature.Events)
    Write-Host "$($Feature.Id) $($Feature.Name)  ($($Feature.Doc.Rel):$($Feature.Line + 1))" -ForegroundColor Cyan
    Write-Host "  entry   $(@($Feature.EntryPaths) -join ', ')"
    Write-Host "  tests   $(if ($tests.Count) { $tests -join ', ' } else { 'none (known gap)' })"
    Write-Host "  events  $(if ($events.Count) { $events -join ', ' } else { 'none' })"
    foreach ($s in $Feature.Sections)
    {
        Write-Host "  read    $(Format-Section $s)"
    }
}

if ($ReadMode)
{
    # No target: where to start.
    if ($For.Count -eq 0)
    {
        Write-Host "$($mainDoc.Rel)  cross-cutting: composition, decisions, shared rules and recipes, references, File map" -ForegroundColor Cyan
        foreach ($doc in $featureDocs)
        {
            $ids = @($features | Where-Object { $_.Doc -eq $doc } | ForEach-Object { $_.Id })
            Write-Host "  $($doc.Rel)  $($doc.Title)  ($($ids -join ', '))"
        }
        Write-Host 'Next: kb.ps1 read <path|area|F-NN|topic>'
        exit 0
    }

    # A target is a feature ID, an area name, a repo path, or else a topic searched in headings and feature names.
    $problems = 0
    $pathTargets = [System.Collections.Generic.List[string]]::new()
    foreach ($raw in $For)
    {
        $feature = $features | Where-Object { $_.Id -ieq $raw } | Select-Object -First 1
        $areaDoc = $featureDocs | Where-Object { [System.IO.Path]::GetFileNameWithoutExtension($_.Path) -ieq $raw -or $_.Title -ieq $raw } | Select-Object -First 1
        if ($feature)
        {
            Write-Feature $feature
        }
        elseif ($areaDoc)
        {
            Write-Area $areaDoc
        }
        elseif ($raw -match '[\\/]' -or $raw -match '\.\w{1,6}$' -or (Test-Path -LiteralPath $raw))
        {
            $pathTargets.Add($raw)
        }
        else
        {
            $featureHits = @($features | Where-Object { $_.Name.IndexOf($raw, [System.StringComparison]::OrdinalIgnoreCase) -ge 0 })
            $headingHits = @(foreach ($doc in $docs)
                {
                    $doc.Headings | Where-Object { $_.Level -ge 2 -and $_.Text.IndexOf($raw, [System.StringComparison]::OrdinalIgnoreCase) -ge 0 }
                })
            if ($featureHits.Count + $headingHits.Count -eq 0)
            {
                Write-Host "$raw  -> no area, feature, heading, or path matches; try a file path or an area: $(($featureDocs | ForEach-Object { [System.IO.Path]::GetFileNameWithoutExtension($_.Path) }) -join ', ')" -ForegroundColor Yellow
                $problems++
                continue
            }
            Write-Host $raw -ForegroundColor Cyan
            foreach ($f in $featureHits)
            {
                Write-Host "  feature $($f.Id) $($f.Name)  ($($f.Doc.Rel):$($f.Line + 1))"
            }
            foreach ($h in $headingHits)
            {
                Write-Host "  section $(Format-Section $h)"
            }
        }
    }
    if ($pathTargets.Count -eq 0)
    {
        exit ([int]($problems -gt 0))
    }

    $resolved = [System.Collections.Generic.List[string]]::new()
    foreach ($raw in $pathTargets)
    {
        $path = Resolve-RepoPath $raw
        if ($null -eq $path)
        {
            Write-Host "  outside the repository: $raw"
            $problems++
        }
        elseif (-not $resolved.Contains($path))
        {
            $resolved.Add($path)
        }
    }

    $guidances = @($resolved | ForEach-Object { Get-Guidance $_ })
    foreach ($g in $guidances)
    {
        $suffix = if (-not $g.Exists) { '  (not on disk yet)' } else { '' }
        if ($g.Rows.Count -eq 0)
        {
            Write-Host "$($g.Path)$suffix  -> no File map row matches; add one" -ForegroundColor Yellow
            $problems++
        }
        else
        {
            Write-Host "$($g.Path)$suffix" -ForegroundColor Cyan
        }
    }

    # Merge across paths: each row, feature, and section once, with the paths that reached it.
    $multi = $resolved.Count -gt 1
    $collect = {
        param($Table, [string]$Key, $Item, [string]$Path)
        if (-not $Table.Contains($Key)) { $Table[$Key] = [pscustomobject]@{ Item = $Item; Paths = [System.Collections.Generic.List[string]]::new() } }
        $Table[$Key].Paths.Add($Path)
    }
    $via = { param($Paths) if ($multi) { "  <- $(@($Paths | Sort-Object -Unique) -join ', ')" } else { '' } }
    $rowHits = [ordered]@{}
    $featureHits = [ordered]@{}
    $readHits = [ordered]@{}
    $alsoHits = [ordered]@{}
    foreach ($g in $guidances)
    {
        foreach ($row in $g.Rows) { & $collect $rowHits "$($row.Line)" $row $g.Path }
        foreach ($f in $g.Features) { & $collect $featureHits $f.Id $f $g.Path }
        foreach ($s in $g.ReadFirst) { & $collect $readHits (Get-SectionKey $s) $s $g.Path }
        foreach ($s in $g.Mentions) { & $collect $alsoHits (Get-SectionKey $s) $s $g.Path }
    }
    foreach ($h in $rowHits.Values | Sort-Object { $_.Item.Line })
    {
        Write-Host "  row     $($mainDoc.Rel):$($h.Item.Line + 1)  $($h.Item.Description)$(& $via $h.Paths)"
    }
    foreach ($h in $featureHits.Values)
    {
        Write-Host "  feature $($h.Item.Id) $($h.Item.Name)  ($($h.Item.Doc.Rel):$($h.Item.Line + 1))$(& $via $h.Paths)"
    }
    foreach ($h in $readHits.Values)
    {
        Write-Host "  read    $(Format-Section $h.Item)$(& $via $h.Paths)"
    }
    foreach ($h in $alsoHits.Values | Where-Object { -not $readHits.Contains((Get-SectionKey $_.Item)) })
    {
        Write-Host "  also    $(Format-Section $h.Item)  (names the path)$(& $via $h.Paths)"
    }

    $lessons = @(Get-RankedLessons $guidances @($resolved))
    $shown = @(if ($All) { $lessons } else { $lessons | Select-Object -First 5 })
    foreach ($l in $shown)
    {
        Write-Host "  lesson  $l"
    }
    if ($lessons.Count -gt $shown.Count)
    {
        Write-Host "  ...     $($lessons.Count - $shown.Count) more related lessons; add -All to list them"
    }
    exit ([int]($problems -gt 0))
}

# ---------------------------------------------------------------- -Changed

if ($Changed)
{
    $diffFiles = @(& git -C $RepoRoot diff --name-only $Base 2>$null)
    if ($LASTEXITCODE -ne 0)
    {
        Write-Host "ERROR git diff against '$Base' failed; is it a valid ref?"
        exit 1
    }
    $untracked = @(& git -C $RepoRoot ls-files --others --exclude-standard 2>$null)
    $changedFiles = @(@($diffFiles) + @($untracked) | Where-Object { $_ } | Sort-Object -Unique)
    $bySection = [ordered]@{}
    $touched = [ordered]@{}
    $uncovered = [System.Collections.Generic.List[string]]::new()
    foreach ($f in $changedFiles)
    {
        $g = Get-Guidance $f
        if ($g.Rows.Count -eq 0)
        {
            $uncovered.Add($f)
        }
        foreach ($feat in $g.Features)
        {
            $touched[$feat.Id] = $feat
        }
        foreach ($s in @($g.ReadFirst) + @($g.Mentions))
        {
            $key = Get-SectionKey $s
            if (-not $bySection.Contains($key))
            {
                $bySection[$key] = [pscustomobject]@{ Section = $s; Files = [System.Collections.Generic.List[string]]::new() }
            }
            $bySection[$key].Files.Add($f)
        }
    }
    $kbChanged = @($changedFiles | Where-Object { $_ -like 'docs/knowledge-base/*' }).Count -gt 0
    Write-Host "$($changedFiles.Count) changed files against $Base; knowledge base changed: $(if ($kbChanged) { 'yes' } else { 'no' })"
    foreach ($entry in $bySection.Values)
    {
        $names = @($entry.Files | Select-Object -First 4)
        $more = if ($entry.Files.Count -gt 4) { " +$($entry.Files.Count - 4) more" } else { '' }
        Write-Host "  review  $(Format-Section $entry.Section)  <- $($names -join ', ')$more"
    }
    foreach ($feat in $touched.Values)
    {
        Write-Host "  feature $($feat.Id) $($feat.Name): re-check its triggers, telemetry, and tests ($($feat.Doc.Rel):$($feat.Line + 1))"
    }
    foreach ($f in $uncovered)
    {
        Write-Host "  no row  $f"
    }
    exit 0
}

# ---------------------------------------------------------------- -Hook

if ($Hook)
{
    # A hook must never get in the way of a tool call: any failure exits 0 with no output.
    try
    {
        $payload = [Console]::In.ReadToEnd() | ConvertFrom-Json
        $toolInput = $payload.tool_input
        $target = if ($toolInput.PSObject.Properties['file_path']) { $toolInput.file_path } elseif ($toolInput.PSObject.Properties['notebook_path']) { $toolInput.notebook_path } else { $null }
        if (-not $target)
        {
            exit 0
        }
        $baseDir = if ($payload.PSObject.Properties['cwd'] -and $payload.cwd) { $payload.cwd } else { (Get-Location).Path }
        $path = Resolve-RepoPath $target $baseDir
        if ($null -eq $path -or $path.EndsWith('/'))
        {
            exit 0
        }
        $g = Get-Guidance $path
        $sections = @($g.ReadFirst) + @($g.Mentions)
        if ($sections.Count -eq 0 -and $g.Features.Count -eq 0)
        {
            exit 0
        }

        # Say each combination of sections once per session; the next file in the same area adds nothing new.
        $key = (@($sections | ForEach-Object { Get-SectionKey $_ }) + @($g.Features | ForEach-Object { $_.Id }) | Sort-Object) -join ','
        $stateDir = Join-Path ([System.IO.Path]::GetTempPath()) 'pointframe-kb-hook'
        [void](New-Item -ItemType Directory -Force -Path $stateDir)
        $session = if ($payload.PSObject.Properties['session_id']) { $payload.session_id -replace '[^\w-]', '' } else { 'unknown' }
        $stateFile = Join-Path $stateDir "$session.txt"
        if ((Test-Path $stateFile) -and (Get-Content -LiteralPath $stateFile) -contains $key)
        {
            exit 0
        }
        Add-Content -LiteralPath $stateFile -Value $key

        $lessons = @(Get-RankedLessons @($g) @($path))
        $sb = [System.Text.StringBuilder]::new()
        [void]$sb.AppendLine("Knowledge base guidance for $path. Read these sections before editing if you have not already:")
        foreach ($s in $sections)
        {
            [void]$sb.AppendLine("- $(Format-Section $s)")
        }
        if ($g.Features.Count -gt 0)
        {
            [void]$sb.AppendLine("User-facing features implemented or tested here: $(($g.Features | ForEach-Object { "$($_.Id) $($_.Name) ($($_.Doc.Rel))" }) -join '; ')")
        }
        if ($lessons.Count -gt 0)
        {
            [void]$sb.AppendLine('Most relevant lessons.md headings (past bugs in this area):')
            foreach ($l in $lessons | Select-Object -First 5)
            {
                [void]$sb.AppendLine("- $l")
            }
            if ($lessons.Count -gt 5)
            {
                [void]$sb.AppendLine("- $($lessons.Count - 5) more: pwsh scripts/kb.ps1 read $path -All")
            }
        }
        $eventName = if ($payload.PSObject.Properties['hook_event_name']) { $payload.hook_event_name } else { 'PostToolUse' }
        # EscapeNonAscii keeps the JSON valid whatever the console code page is (em dashes in lesson headings).
        @{
            hookSpecificOutput = @{
                hookEventName = $eventName
                additionalContext = $sb.ToString().TrimEnd()
            }
        } | ConvertTo-Json -Depth 4 -Compress -EscapeHandling EscapeNonAscii
    }
    catch
    {
        # Silent by design; set KB_HOOK_DEBUG=1 to see why a hook call produced nothing.
        if ($env:KB_HOOK_DEBUG)
        {
            [Console]::Error.WriteLine("knowledge-base hook: $($_.Exception.Message) at $($_.InvocationInfo.PositionMessage)")
        }
    }
    exit 0
}

# ---------------------------------------------------------------- generated blocks (main file)

# Replaces the lines between two marker lines; returns the new line array, or $null when unchanged.
function Update-Block([string[]]$Lines, [string[]]$Markers, [string[]]$Desired, [string]$What)
{
    $start = -1
    $end = -1
    for ($i = 0; $i -lt $Lines.Length; $i++)
    {
        if ($Lines[$i].Trim() -eq $Markers[0]) { $start = $i }
        if ($Lines[$i].Trim() -eq $Markers[1]) { $end = $i }
    }
    if ($start -lt 0 -or $end -lt $start)
    {
        $errors.Add("$($mainDoc.Rel): markers '$($Markers[0])' and '$($Markers[1])' must both exist, in that order")
        return $null
    }
    $current = @()
    if ($end - $start -gt 1)
    {
        $current = @($Lines[($start + 1)..($end - 1)] | Where-Object { $_.Trim() -ne '' })
    }
    if (($current -join "`n") -eq ($Desired -join "`n"))
    {
        return $null
    }
    if ($Check)
    {
        $errors.Add("$($mainDoc.Rel): the $What is out of date; run kb.ps1 check")
        return $null
    }
    $notes.Add("$What refreshed ($($Desired.Count) entries)")
    return @($Lines[0..$start]) + @('') + @($Desired) + @('') + @($Lines[$end..($Lines.Length - 1)])
}

$tocEnd = ($mainDoc.Lines | Select-String -SimpleMatch $TocMarkers[1] | Select-Object -First 1)
$tocEndLine = if ($tocEnd) { $tocEnd.LineNumber - 1 } else { -1 }
$toc = @($mainDoc.Headings | Where-Object { $_.Line -gt $tocEndLine -and $_.Level -gt 1 } | ForEach-Object {
        $indent = if ($_.Level -eq 2) { '' } else { '  ' }
        "$indent- [$($_.Text)](#$($_.Anchor))"
    })
# Feature index: one line per area file, ordered by its lowest feature ID.
$areas = foreach ($doc in $featureDocs)
{
    $own = @($features | Where-Object { $_.Doc -eq $doc })
    $firstId = if ($own.Count) { ($own | ForEach-Object { [int]$_.Id.Substring(2) } | Measure-Object -Minimum).Minimum } else { [int]::MaxValue }
    $title = if ($doc.Title) { $doc.Title } else { $doc.Name }
    $list = if ($own.Count) { ($own | ForEach-Object { "$($_.Id) $($_.Name)" }) -join '; ' } else { 'no features yet' }
    [pscustomobject]@{ Order = $firstId; Line = "- [$title]($($doc.Name)): $list" }
}
$index = @($areas | Sort-Object Order | ForEach-Object { $_.Line })

# Decision index: every D-NNN in number order, linked into the file that holds it.
$decisionIndex = @($decisions | Sort-Object Number | ForEach-Object {
        $h = $_.Heading
        $superseded = if (($h.Doc.Lines[$h.Line..$h.EndLine] -join "`n") -match '(?m)^Superseded by') { ' (superseded)' } else { '' }
        "- [$($h.Text)]($($h.Doc.Name)#$($h.Anchor))$superseded"
    })

$mainLines = $mainDoc.Lines
$changedMain = $false
$blocks = @(
    @{ Markers = $TocMarkers; Desired = $toc; What = 'table of contents' },
    @{ Markers = $IndexMarkers; Desired = $index; What = 'Feature index' },
    @{ Markers = $DecisionMarkers; Desired = $decisionIndex; What = 'decision index' }
)
foreach ($block in $blocks)
{
    $updated = Update-Block $mainLines $block.Markers $block.Desired $block.What
    if ($null -ne $updated)
    {
        $mainLines = $updated
        $changedMain = $true
    }
}
if ($changedMain)
{
    [System.IO.File]::WriteAllText($MainPath, ($mainLines -join $mainDoc.Newline), [System.Text.UTF8Encoding]::new($false))
}

# ---------------------------------------------------------------- Codex copies of the kb-* skills

# Codex reads skills from .agents/skills. Those copies are generated from .claude/skills/kb-*/SKILL.md so the
# two never drift: check rewrites them, check -NoFix reports them. CLAUDE.md becomes AGENTS.md, and the
# Claude Code hook section is dropped.
$claudeSkills = Join-Path $RepoRoot '.claude' 'skills'
$agentSkills = Join-Path $RepoRoot '.agents' 'skills'
foreach ($skill in @(Get-ChildItem -Path $claudeSkills -Directory -Filter 'kb-*' -ErrorAction SilentlyContinue))
{
    $sourcePath = Join-Path $skill.FullName 'SKILL.md'
    if (-not (Test-Path $sourcePath))
    {
        continue
    }
    $rel = ".agents/skills/$($skill.Name)/SKILL.md"
    $mirrorPath = Join-Path $agentSkills $skill.Name 'SKILL.md'
    $text = [System.IO.File]::ReadAllText($sourcePath)
    $text = $text -replace '`CLAUDE\.md`', '`AGENTS.md`'
    $text = ($text -replace '(?s)\r?\n## The project hook.*$', '').TrimEnd() + "`n"
    $desired = $text -replace '\r?\n', $mainDoc.Newline
    $current = if (Test-Path $mirrorPath) { [System.IO.File]::ReadAllText($mirrorPath) } else { $null }
    if ($null -ne $current -and ($current -replace '\r\n', "`n") -eq ($desired -replace '\r\n', "`n"))
    {
        continue
    }
    if ($Check)
    {
        $errors.Add("$rel is out of date with .claude/skills/$($skill.Name)/SKILL.md; run kb.ps1 check")
    }
    else
    {
        [void](New-Item -ItemType Directory -Force -Path (Split-Path $mirrorPath -Parent))
        [System.IO.File]::WriteAllText($mirrorPath, $desired, [System.Text.UTF8Encoding]::new($false))
        $notes.Add("$rel regenerated from .claude/skills/$($skill.Name)")
    }
}

# ---------------------------------------------------------------- checks across all files

$tracked = @(Get-TrackedCached)

# Repo paths in backticks: must exist and must not be gitignored (an ignored file is visible on this machine
# but absent on every clone). Placeholders (<Name>, *) and URLs are skipped.
$seen = @{}
$existing = [System.Collections.Generic.List[string]]::new()
foreach ($doc in $docs)
{
    foreach ($m in [regex]::Matches(($doc.Lines -join "`n"), '`((?:[\w.-]+/)+[\w.-]+\.[A-Za-z0-9]+)`'))
    {
        $p = $m.Groups[1].Value
        if ($p -match '[<>*]' -or $p.StartsWith('http') -or $seen.ContainsKey($p))
        {
            continue
        }
        $seen[$p] = $true
        if (Test-Path (Join-Path $RepoRoot $p))
        {
            $existing.Add($p)
        }
        else
        {
            $errors.Add("$($doc.Rel): path does not exist: $p")
        }
    }
}
if ($existing.Count -gt 0)
{
    try
    {
        # Paths go as arguments in batches; piping to --stdin from PowerShell appends CRLF and nothing matches.
        for ($i = 0; $i -lt $existing.Count; $i += 50)
        {
            $batch = @($existing.GetRange($i, [Math]::Min(50, $existing.Count - $i)))
            $out = & git -C $RepoRoot check-ignore -- @batch 2>$null
            foreach ($p in @($out))
            {
                if ($p)
                {
                    $errors.Add("path is gitignored and will not exist on a clone: $($p.Trim())")
                }
            }
        }
    }
    catch
    {
        $notes.Add('git not available; gitignore check skipped')
    }
}

# Lesson references, links, and one "**Files.**" line per section.
$lessonHeadings = $null
if (Test-Path $LessonsPath)
{
    $lessonHeadings = @{}
    foreach ($l in [System.IO.File]::ReadAllLines($LessonsPath))
    {
        if ($l -match '^##\s+(.+?)\s*$')
        {
            $lessonHeadings[$Matches[1]] = $true
        }
    }
}
else
{
    $notes.Add('lessons.md not present on this clone; lesson references not checked')
}
$linkedLessons = [System.Collections.Generic.HashSet[string]]::new()
foreach ($doc in $docs)
{
    for ($i = 0; $i -lt $doc.Lines.Length; $i++)
    {
        if ($doc.Fenced[$i])
        {
            continue
        }
        $line = $doc.Lines[$i]
        if ($null -ne $lessonHeadings -and $line -match '^\s*-\s*Lesson:\s*(.+?)\s*$' -and -not $lessonHeadings.ContainsKey($Matches[1]))
        {
            $errors.Add("$($doc.Rel):$($i + 1): lesson heading not found in lessons.md: $($Matches[1])")
        }
        foreach ($m in [regex]::Matches($line, '\]\(([^)\s]+)\)'))
        {
            $target = $m.Groups[1].Value
            if (-not ($target.StartsWith('#') -or $target -match '\.md(#|$)'))
            {
                continue
            }
            if ((Resolve-Link $doc $target).Kind -eq 'broken')
            {
                $errors.Add("$($doc.Rel):$($i + 1): broken link: $target")
            }
        }
    }
    foreach ($h in $doc.Headings | Where-Object { $_.IsSection })
    {
        foreach ($l in (Get-SectionLessons $h))
        {
            [void]$linkedLessons.Add($l)
        }
        $filesLines = @($h.Line..$h.EndLine | Where-Object { -not $doc.Fenced[$_] -and $doc.Lines[$_] -match '^\*\*Files\.\*\*' })
        if ($filesLines.Count -gt 1)
        {
            $errors.Add("$($doc.Rel):$($filesLines[1] + 1): section '$($h.Text)' has $($filesLines.Count) **Files.** lines; a block is probably under the wrong heading")
        }
    }
}

# A lesson no section links is invisible to read and the hook. A warning, not an error: a lesson can land on
# master before its feature does (written on a branch that is not merged yet).
if ($null -ne $lessonHeadings)
{
    foreach ($l in $lessonHeadings.Keys | Where-Object { -not $linkedLessons.Contains($_) } | Sort-Object)
    {
        $notes.Add("lesson not linked from any section, so kb.ps1 read never shows it: $l")
    }
}

# Decision numbers are global across files and never reused.
foreach ($group in $decisions | Group-Object Number | Where-Object { $_.Count -gt 1 })
{
    $where = ($group.Group | ForEach-Object { "$($_.Heading.Doc.Rel):$($_.Heading.Line + 1)" }) -join ', '
    $errors.Add("decision D-$('{0:D3}' -f [int]$group.Name) is used more than once: $where; numbers are never reused")
}

# Features: unique IDs, an entry point and a section each, telemetry events that exist, and every product
# telemetry event and every smoke test claimed by some feature.
$seenIds = @{}
foreach ($f in $features)
{
    $where = "$($f.Doc.Rel):$($f.Line + 1)"
    if ($seenIds.ContainsKey($f.Id))
    {
        $errors.Add("${where}: feature ID $($f.Id) is also used at $($seenIds[$f.Id]); IDs are never reused")
    }
    else
    {
        $seenIds[$f.Id] = $where
    }
    if (@($f.EntryPaths).Count -eq 0)
    {
        $errors.Add("${where}: feature $($f.Id) names no entry point file")
    }
    if (@($f.Sections).Count -eq 0)
    {
        $errors.Add("${where}: feature $($f.Id) links no section")
    }
}
$catalogPath = Join-Path $RepoRoot 'Pointframe' 'Services' 'Infrastructure' 'TelemetryEventCatalog.cs'
if (Test-Path $catalogPath)
{
    $eventsBlock = [regex]::Match([System.IO.File]::ReadAllText($catalogPath), '(?s)public static class TelemetryEvents\s*\{(.*?)\n\}').Groups[1].Value
    $catalogEvents = @([regex]::Matches($eventsBlock, 'const string \w+ = "([a-z0-9_]+)"') | ForEach-Object { $_.Groups[1].Value })
    $claimed = @{}
    foreach ($f in $features)
    {
        foreach ($ev in $f.Events)
        {
            $claimed[$ev] = $true
            if ($catalogEvents -notcontains $ev)
            {
                $errors.Add("$($f.Doc.Rel):$($f.Line + 1): feature $($f.Id) names telemetry event '$ev', which TelemetryEvents does not define")
            }
        }
    }
    foreach ($ev in $catalogEvents | Where-Object { -not $claimed.ContainsKey($_) })
    {
        $errors.Add("telemetry event '$ev' belongs to no feature; add it to a feature row")
    }
}
$claimedTests = @($features | ForEach-Object { $_.TestPaths })
foreach ($t in $tracked | Where-Object { $_ -like 'Pointframe.AutomationTests/Smoke/*.cs' -and $claimedTests -notcontains $_ })
{
    $errors.Add("smoke test $t belongs to no feature; add it to a feature row")
}

# File map: short descriptions, no dead patterns, full coverage, no redundant rows.
if (-not $mapHeading)
{
    $errors.Add("$($mainDoc.Rel): no '## $FileMapHeading' section")
}
elseif ($tracked.Count -eq 0)
{
    $notes.Add('git not available; File map coverage skipped')
}
else
{
    $covered = [System.Collections.Generic.HashSet[string]]::new()
    foreach ($row in $fileMap)
    {
        $where = "$($mainDoc.Rel):$($row.Line + 1)"
        if ($row.Description.Length -gt $MaxDescriptionLength)
        {
            $errors.Add("${where}: File map description is $($row.Description.Length) characters; keep it under $MaxDescriptionLength and move explanation into a section")
        }
        $rowFiles = [System.Collections.Generic.HashSet[string]]::new()
        for ($p = 0; $p -lt $row.Patterns.Count; $p++)
        {
            $rx = $row.Regexes[$p]
            $hits = @($tracked | Where-Object { $rx.IsMatch($_) })
            if ($hits.Count -eq 0)
            {
                $errors.Add("${where}: File map pattern matches no tracked file: $($row.Patterns[$p])")
            }
            foreach ($f in $hits)
            {
                [void]$rowFiles.Add($f)
                [void]$covered.Add($f)
            }
        }
        $row | Add-Member -NotePropertyName Files -NotePropertyValue $rowFiles -Force
        $row | Add-Member -NotePropertyName Keys -NotePropertyValue @($row.Sections | ForEach-Object { Get-SectionKey $_ }) -Force
    }

    $uncovered = @($tracked | Where-Object { -not $covered.Contains($_) })
    foreach ($f in $uncovered | Select-Object -First 20)
    {
        $errors.Add("tracked file matches no File map row: $f")
    }
    if ($uncovered.Count -gt 20)
    {
        $errors.Add("... and $($uncovered.Count - 20) more files with no File map row")
    }

    # Every matching row applies, so a row whose files and links are both inside another row's adds nothing.
    for ($r = 0; $r -lt $fileMap.Count; $r++)
    {
        $row = $fileMap[$r]
        if ($row.Files.Count -eq 0)
        {
            continue
        }
        for ($o = 0; $o -lt $fileMap.Count; $o++)
        {
            $other = $fileMap[$o]
            if ($o -eq $r -or -not $row.Files.IsSubsetOf($other.Files))
            {
                continue
            }
            $extra = @($row.Keys | Where-Object { $other.Keys -notcontains $_ })
            $identical = $other.Files.IsSubsetOf($row.Files) -and @($other.Keys | Where-Object { $row.Keys -notcontains $_ }).Count -eq 0
            if ($extra.Count -eq 0 -and -not ($identical -and $o -gt $r))
            {
                $errors.Add("$($mainDoc.Rel):$($row.Line + 1): File map row is redundant; line $($other.Line + 1) already covers its files and links")
                break
            }
        }
    }
}

foreach ($n in $notes)
{
    Write-Host "note  $n"
}
foreach ($e in $errors)
{
    Write-Host "ERROR $e" -ForegroundColor Red
}
Write-Host ('{0} files, {1} features, {2} paths checked, {3} errors' -f $docs.Count, $features.Count, $seen.Count, $errors.Count)
if ($errors.Count -gt 0)
{
    exit 1
}
