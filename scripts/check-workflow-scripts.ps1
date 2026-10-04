#Requires -Version 7.0
<#
Parses the PowerShell inside GitHub workflow files, because nothing else runs it before merge: YAML validation
and the done-gate both pass a `run:` block whose script cannot even be parsed (lessons.md, "PowerShell reads
"$name:" inside a string as a scoped variable").

  pwsh scripts/check-workflow-scripts.ps1                  # check every .github/workflows/*.yml; exit 1 on any problem
  pwsh scripts/check-workflow-scripts.ps1 -Path <dir>      # check the *.yml files in another directory
  pwsh scripts/check-workflow-scripts.ps1 -SelfTest        # run the checker on scripts/tests/workflow-scripts fixtures

For each step's `run:` (block scalar `|` or `>`, or a single-line value) the shell is the step's `shell:`, else the
job's `defaults.run.shell`, else the workflow's `defaults.run.shell`, else pwsh when the job's `runs-on` contains
"windows" and bash otherwise. Only pwsh and powershell blocks are parsed. Each `${{ ... }}` expression becomes a
placeholder word (a string stays a string inside quotes, a bare word outside) before
[System.Management.Automation.Language.Parser]::ParseInput runs. Syntax only: it does not run the script, and it
does not catch runtime mistakes such as a tolerated native failure with no `exit 0`.

Prints one line per problem, "<file>:<line of the run: key> step '<name>': <parser message>", and exits 1 if
there are any; otherwise prints a one-line summary and exits 0. Exit 2 on bad arguments.

The line scanner handles what the repo's workflows use: block scalars with chomping and indentation indicators,
single-line plain and quoted values, step lists at any indent, job and workflow `defaults.run.shell`. It is not a
YAML parser: anchors, flow-style step lists, and a `runs-on` built from a matrix expression are approximated (a
matrix expression has no "windows" in it, so the default shell is bash unless `shell:` says otherwise).
#>
[CmdletBinding()]
param(
    [string]$Path,
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$Placeholder = 'GHA_EXPRESSION'

function Get-Indent([string]$Line)
{
    $Line.Length - $Line.TrimStart(' ').Length
}

function Test-Content([string]$Line)
{
    $Line.Trim().Length -gt 0 -and -not $Line.TrimStart().StartsWith('#')
}

function ConvertFrom-YamlScalar([string]$Value)
{
    $text = $Value.Trim()
    if ($text.Length -ge 2 -and $text.StartsWith("'") -and $text.EndsWith("'"))
    {
        return $text.Substring(1, $text.Length - 2).Replace("''", "'")
    }
    if ($text.Length -ge 2 -and $text.StartsWith('"') -and $text.EndsWith('"'))
    {
        return $text.Substring(1, $text.Length - 2).Replace('\"', '"').Replace('\\', '\')
    }
    ($text -replace '\s+#.*$', '')
}

function Get-ShellName([string]$Value)
{
    $name = (ConvertFrom-YamlScalar $Value).Trim()
    if (-not $name)
    {
        return $null
    }
    ($name -split '\s+')[0].ToLowerInvariant()
}

function Read-RunScript([string[]]$Lines, [int]$Index, [int]$KeyIndent, [string]$Value)
{
    # Returns the script text of the run: key on $Lines[$Index] whose key sits at column $KeyIndent.
    $content = [System.Collections.Generic.List[string]]::new()
    for ($i = $Index + 1; $i -lt $Lines.Count; $i++)
    {
        $line = $Lines[$i]
        if ($line.Trim().Length -gt 0 -and (Get-Indent $line) -le $KeyIndent)
        {
            break
        }
        $content.Add($line)
    }

    if ($Value -match '^([|>])[+-]?\d?(\s+#.*)?$')
    {
        $folded = $Matches[1] -eq '>'
        while ($content.Count -gt 0 -and $content[$content.Count - 1].Trim().Length -eq 0)
        {
            $content.RemoveAt($content.Count - 1)
        }
        $first = $content | Where-Object { $_.Trim().Length -gt 0 } | Select-Object -First 1
        $indent = if ($first) { Get-Indent $first } else { 0 }
        $stripped = @($content | ForEach-Object { if ($_.Length -ge $indent) { $_.Substring($indent) } else { '' } })
        if (-not $folded)
        {
            return ($stripped -join "`n")
        }
        $out = [System.Text.StringBuilder]::new()
        for ($i = 0; $i -lt $stripped.Count; $i++)
        {
            $line = $stripped[$i]
            if ($i -gt 0)
            {
                $previous = $stripped[$i - 1]
                $keepBreak = $line.Length -eq 0 -or $previous.Length -eq 0 -or $line.StartsWith(' ') -or $previous.StartsWith(' ')
                [void]$out.Append($(if ($keepBreak) { "`n" } else { ' ' }))
            }
            [void]$out.Append($line)
        }
        return $out.ToString()
    }

    # A plain or quoted scalar, possibly continued on more-indented lines.
    $parts = @($Value) + @($content | Where-Object { Test-Content $_ } | ForEach-Object { $_.Trim() })
    ConvertFrom-YamlScalar ($parts -join ' ')
}

function Get-WorkflowBlocks([string[]]$Lines)
{
    # Yields one object per run: key: Line (1-based), Step, Shell, Script.
    $jobsAt = -1
    for ($i = 0; $i -lt $Lines.Count; $i++)
    {
        if ($Lines[$i] -match '^jobs:\s*(#.*)?$')
        {
            $jobsAt = $i
            break
        }
    }
    if ($jobsAt -lt 0)
    {
        return
    }

    $workflowShell = $null
    for ($i = 0; $i -lt $jobsAt; $i++)
    {
        if ($Lines[$i] -match '^\s+shell:\s*(.+)$')
        {
            $workflowShell = Get-ShellName $Matches[1]
        }
    }

    # Job regions: a key at the indent of the first job under jobs:.
    $jobStarts = [System.Collections.Generic.List[int]]::new()
    $jobIndent = -1
    $jobsEnd = $Lines.Count
    for ($i = $jobsAt + 1; $i -lt $Lines.Count; $i++)
    {
        if (-not (Test-Content $Lines[$i]))
        {
            continue
        }
        $indent = Get-Indent $Lines[$i]
        if ($indent -eq 0)
        {
            $jobsEnd = $i
            break
        }
        if ($jobIndent -lt 0)
        {
            $jobIndent = $indent
        }
        if ($indent -eq $jobIndent -and $Lines[$i] -match '^\s*[\w.-]+:\s*(#.*)?$')
        {
            $jobStarts.Add($i)
        }
    }

    for ($j = 0; $j -lt $jobStarts.Count; $j++)
    {
        $start = $jobStarts[$j]
        $end = if ($j + 1 -lt $jobStarts.Count) { $jobStarts[$j + 1] } else { $jobsEnd }

        $stepsAt = -1
        for ($i = $start + 1; $i -lt $end; $i++)
        {
            if ($Lines[$i] -match '^\s+steps:\s*(#.*)?$' -and (Get-Indent $Lines[$i]) -eq $jobIndent + 2)
            {
                $stepsAt = $i
                break
            }
        }
        $headerEnd = if ($stepsAt -ge 0) { $stepsAt } else { $end }

        $runsOn = ''
        $jobShell = $null
        for ($i = $start + 1; $i -lt $headerEnd; $i++)
        {
            if ($Lines[$i] -match '^\s+runs-on:\s*(.*)$' -and (Get-Indent $Lines[$i]) -eq $jobIndent + 2)
            {
                $runsOn = $Matches[1]
                for ($k = $i + 1; $k -lt $headerEnd -and (Get-Indent $Lines[$k]) -gt $jobIndent + 2; $k++)
                {
                    $runsOn += " $($Lines[$k])"
                }
            }
            elseif ($Lines[$i] -match '^\s+shell:\s*(.+)$')
            {
                $jobShell = Get-ShellName $Matches[1]
            }
        }
        if ($stepsAt -lt 0)
        {
            continue
        }

        $defaultShell = if ($jobShell) { $jobShell } elseif ($workflowShell) { $workflowShell } elseif ($runsOn -match '(?i)windows') { 'pwsh' } else { 'bash' }

        # Steps: dash lines at the indent of the first content line after steps:.
        $dashIndent = -1
        $stepStarts = [System.Collections.Generic.List[int]]::new()
        $stepsEnd = $end
        for ($i = $stepsAt + 1; $i -lt $end; $i++)
        {
            if (-not (Test-Content $Lines[$i]))
            {
                continue
            }
            $indent = Get-Indent $Lines[$i]
            if ($indent -le $jobIndent + 2)
            {
                $stepsEnd = $i
                break
            }
            if ($dashIndent -lt 0)
            {
                $dashIndent = $indent
            }
            if ($indent -eq $dashIndent -and $Lines[$i].TrimStart().StartsWith('- '))
            {
                $stepStarts.Add($i)
            }
        }

        for ($s = 0; $s -lt $stepStarts.Count; $s++)
        {
            $stepStart = $stepStarts[$s]
            $stepEnd = if ($s + 1 -lt $stepStarts.Count) { $stepStarts[$s + 1] } else { $stepsEnd }
            $keyIndent = $dashIndent + 2

            $chunk = @(for ($i = $stepStart; $i -lt $stepEnd; $i++)
                {
                    if ($i -eq $stepStart)
                    {
                        (' ' * $keyIndent) + $Lines[$i].Substring($dashIndent + 2)
                    }
                    else
                    {
                        $Lines[$i]
                    }
                })

            $name = '(unnamed)'
            $stepShell = $null
            $runIndex = -1
            $runValue = ''
            for ($i = 0; $i -lt $chunk.Count; $i++)
            {
                if ((Get-Indent $chunk[$i]) -ne $keyIndent)
                {
                    continue
                }
                if ($chunk[$i] -match '^\s+name:\s*(.+)$')
                {
                    $name = (ConvertFrom-YamlScalar $Matches[1])
                }
                elseif ($chunk[$i] -match '^\s+shell:\s*(.+)$')
                {
                    $stepShell = Get-ShellName $Matches[1]
                }
                elseif ($chunk[$i] -match '^\s+run:\s*(.*)$')
                {
                    $runIndex = $i
                    $runValue = $Matches[1].Trim()
                }
            }
            if ($runIndex -lt 0)
            {
                continue
            }

            [pscustomobject]@{
                Line = $stepStart + $runIndex + 1
                Step = $name
                Shell = if ($stepShell) { $stepShell } else { $defaultShell }
                Script = Read-RunScript $chunk $runIndex $keyIndent $runValue
            }
        }
    }
}

function Test-WorkflowDirectory([string]$Directory)
{
    $files = @(Get-ChildItem -Path $Directory -Filter '*.yml' -File | Sort-Object Name)
    $blocks = 0
    $problems = [System.Collections.Generic.List[string]]::new()
    foreach ($file in $files)
    {
        $label = ((Join-Path $Directory $file.Name) -replace '\\', '/').Replace(($RepoRoot -replace '\\', '/') + '/', '')
        $lines = @(Get-Content -LiteralPath $file.FullName | ForEach-Object { $_.TrimEnd("`r") })
        foreach ($block in @(Get-WorkflowBlocks $lines))
        {
            if ($block.Shell -notin @('pwsh', 'powershell'))
            {
                continue
            }
            $blocks++
            $script = [regex]::Replace($block.Script, '(?s)\$\{\{.*?\}\}', $Placeholder)
            $tokens = $null
            $errors = $null
            [void][System.Management.Automation.Language.Parser]::ParseInput($script, [ref]$tokens, [ref]$errors)
            foreach ($err in $errors)
            {
                $problems.Add("${label}:$($block.Line) step '$($block.Step)': $($err.Message) (script line $($err.Extent.StartLineNumber))")
            }
        }
    }
    [pscustomobject]@{ Workflows = $files.Count; Blocks = $blocks; Problems = @($problems) }
}

if ($SelfTest)
{
    $fixtures = Join-Path $PSScriptRoot 'tests' 'workflow-scripts'
    $failures = [System.Collections.Generic.List[string]]::new()

    $good = Test-WorkflowDirectory (Join-Path $fixtures 'good')
    if ($good.Problems.Count -gt 0)
    {
        $failures.Add("good fixture reported problems: $($good.Problems -join ' | ')")
    }
    if ($good.Blocks -ne 7)
    {
        $failures.Add("good fixture: expected 7 pwsh/powershell blocks, found $($good.Blocks)")
    }

    $bad = Test-WorkflowDirectory (Join-Path $fixtures 'bad')
    if ($bad.Problems.Count -ne 1)
    {
        $failures.Add("bad fixture: expected exactly 1 problem, found $($bad.Problems.Count): $($bad.Problems -join ' | ')")
    }
    elseif ($bad.Problems[0] -notmatch "bad/release\.yml:\d+ step 'Check existing release': Variable reference is not valid")
    {
        $failures.Add("bad fixture: unexpected problem: $($bad.Problems[0])")
    }

    if ($failures.Count -gt 0)
    {
        $failures | ForEach-Object { Write-Host "ERROR self-test: $_" }
        exit 1
    }
    Write-Host "Workflow script check self-test passed: good fixture clean ($($good.Blocks) pwsh blocks), bad fixture rejected ($($bad.Problems[0]))."
    exit 0
}

if (-not $Path)
{
    $Path = Join-Path $RepoRoot '.github' 'workflows'
}
if (-not (Test-Path -LiteralPath $Path -PathType Container))
{
    Write-Host "ERROR workflow directory not found: $Path"
    exit 2
}

$result = Test-WorkflowDirectory $Path
if ($result.Problems.Count -gt 0)
{
    $result.Problems | ForEach-Object { Write-Host $_ }
    Write-Host "ERROR $($result.Problems.Count) PowerShell parse problem(s) in workflow run blocks ($($result.Workflows) workflows, $($result.Blocks) pwsh blocks)."
    exit 1
}
Write-Host "Workflow scripts OK: $($result.Workflows) workflows, $($result.Blocks) pwsh blocks parsed."
exit 0
