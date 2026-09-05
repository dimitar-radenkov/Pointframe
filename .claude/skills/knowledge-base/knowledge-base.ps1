#Requires -Version 7.0
<#
Keeps docs/knowledge-base/knowledge-base.md honest.

  pwsh .claude/skills/knowledge-base/knowledge-base.ps1          # refresh the table of contents, then check; exit 1 on errors
  pwsh .claude/skills/knowledge-base/knowledge-base.ps1 -Check   # check only, never write

Checks: every backticked repo path exists, every "- Lesson: <heading>" matches a "## " heading in
lessons.md, every "(#anchor)" link points at a heading, no two headings share an anchor.
#>
[CmdletBinding()]
param(
    [switch]$Check
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..' '..' '..')).Path
$KbPath = Join-Path $RepoRoot 'docs' 'knowledge-base' 'knowledge-base.md'
$LessonsPath = Join-Path $RepoRoot 'lessons.md'
$TocStart = '<!-- toc -->'
$TocEnd = '<!-- /toc -->'

if (-not (Test-Path $KbPath))
{
    Write-Host "ERROR knowledge base not found: $KbPath"
    exit 1
}

function ConvertTo-Anchor([string]$Heading)
{
    $a = $Heading.Trim().ToLowerInvariant()
    $a = $a -replace '[^\p{L}\p{N}\s-]', ''
    return ($a -replace '\s', '-')
}

$original = [System.IO.File]::ReadAllText($KbPath)
$newline = if ($original.Contains("`r`n")) { "`r`n" } else { "`n" }
$lines = $original -split "\r?\n"

$errors = [System.Collections.Generic.List[string]]::new()
$notes = [System.Collections.Generic.List[string]]::new()

# Headings (outside code fences) and their anchors.
$headings = [System.Collections.Generic.List[object]]::new()
$inFence = $false
$tocStartLine = -1
$tocEndLine = -1
for ($i = 0; $i -lt $lines.Length; $i++)
{
    $line = $lines[$i]
    if ($line -match '^\s*```')
    {
        $inFence = -not $inFence
        continue
    }
    if ($inFence)
    {
        continue
    }
    if ($line.Trim() -eq $TocStart) { $tocStartLine = $i }
    if ($line.Trim() -eq $TocEnd) { $tocEndLine = $i }
    if ($line -match '^(#{1,3})\s+(.+?)\s*$')
    {
        $headings.Add([pscustomobject]@{
            Level = $Matches[1].Length
            Text = $Matches[2]
            Anchor = ConvertTo-Anchor $Matches[2]
            Line = $i
        })
    }
}

$anchors = @{}
foreach ($h in $headings)
{
    if ($anchors.ContainsKey($h.Anchor))
    {
        $errors.Add("line $($h.Line + 1): heading '$($h.Text)' produces the same anchor as an earlier heading; rename one")
    }
    else
    {
        $anchors[$h.Anchor] = $h
    }
}

# Table of contents: every ## and ### after the toc block.
if ($tocStartLine -lt 0 -or $tocEndLine -lt 0 -or $tocEndLine -lt $tocStartLine)
{
    $errors.Add("table of contents markers '$TocStart' and '$TocEnd' must both exist, in that order")
}
else
{
    $toc = [System.Collections.Generic.List[string]]::new()
    foreach ($h in $headings)
    {
        if ($h.Line -le $tocEndLine -or $h.Level -eq 1)
        {
            continue
        }
        $indent = if ($h.Level -eq 2) { '' } else { '  ' }
        $toc.Add("$indent- [$($h.Text)](#$($h.Anchor))")
    }
    $current = @()
    if ($tocEndLine - $tocStartLine -gt 1)
    {
        $current = $lines[($tocStartLine + 1)..($tocEndLine - 1)] | Where-Object { $_.Trim() -ne '' }
    }
    $desired = @($toc)
    $same = ($current.Count -eq $desired.Count)
    if ($same)
    {
        for ($j = 0; $j -lt $desired.Count; $j++)
        {
            if ($current[$j] -ne $desired[$j])
            {
                $same = $false
                break
            }
        }
    }
    if (-not $same)
    {
        if ($Check)
        {
            $errors.Add("table of contents is out of date; run the script without -Check")
        }
        else
        {
            $before = $lines[0..$tocStartLine]
            $after = $lines[$tocEndLine..($lines.Length - 1)]
            $rebuilt = @($before) + @('') + @($desired) + @('') + @($after)
            [System.IO.File]::WriteAllText($KbPath, ($rebuilt -join $newline), [System.Text.UTF8Encoding]::new($false))
            $notes.Add("table of contents refreshed ($($desired.Count) entries)")
        }
    }
}

# Repo paths in backticks: must exist. Placeholders (<Name>, *) and URLs are skipped.
$body = $lines -join "`n"
$seen = @{}
foreach ($m in [regex]::Matches($body, '`((?:[\w.-]+/)+[\w.-]+\.[A-Za-z0-9]+)`'))
{
    $p = $m.Groups[1].Value
    if ($p -match '[<>*]' -or $p.StartsWith('http') -or $seen.ContainsKey($p))
    {
        continue
    }
    $seen[$p] = $true
    if (-not (Test-Path (Join-Path $RepoRoot $p)))
    {
        $errors.Add("path does not exist: $p")
    }
}

# Lesson references: "- Lesson: <heading>" must match a "## " heading in lessons.md.
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
    for ($i = 0; $i -lt $lines.Length; $i++)
    {
        if ($lines[$i] -match '^\s*-\s*Lesson:\s*(.+?)\s*$' -and -not $lessonHeadings.ContainsKey($Matches[1]))
        {
            $errors.Add("line $($i + 1): lesson heading not found in lessons.md: $($Matches[1])")
        }
    }
}
else
{
    $notes.Add('lessons.md not present on this clone; lesson references not checked')
}

# Internal links: "(#anchor)" must point at a heading.
foreach ($m in [regex]::Matches($body, '\]\(#([^)]+)\)'))
{
    $a = $m.Groups[1].Value
    if (-not $anchors.ContainsKey($a))
    {
        $errors.Add("broken internal link: #$a")
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
Write-Host ('{0} headings, {1} paths checked, {2} errors' -f $headings.Count, $seen.Count, $errors.Count)
if ($errors.Count -gt 0)
{
    exit 1
}
