#Requires -Version 7.0
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# The consumer's thin entry points (scripts/<tool>.ps1) are all rendered from one kit template, templates/wrapper.ps1, so
# every consumer gets the same wrapper. doctor.ps1 compares them with the render.

function Get-AwWrapperTable {
    # tool -> the sentence for the header and the parameters that mean "the caller named the repository".
    return [ordered]@{
        'kb' = @{ Summary = 'the knowledge base tool'; Selectors = @('Root') }
        'verify' = @{ Summary = 'the done gate'; Selectors = @('Root') }
        # MainRoot: the gate tools read the gate configuration from the main checkout only, so the wrapper names that checkout, not the
        # (possibly linked) worktree it was started from.
        'merge-pr' = @{ Summary = 'the pull request merge tool'; Selectors = @('Root', 'Worktree'); MainRoot = $true }
        'record-review' = @{ Summary = 'the review record tool'; Selectors = @('Root', 'Worktree'); MainRoot = $true }
        'codex-ask' = @{ Summary = 'Codex consultations'; Selectors = @('Root') }
        'agent-run' = @{ Summary = 'the Codex executor launcher'; Selectors = @('Root') }
        'status' = @{ Summary = 'the status store'; Selectors = @('Root') }
        'check-workflow-scripts' = @{ Summary = 'the workflow script checker'; Selectors = @('Root') }
        'package' = @{ Summary = 'the work package runner'; Selectors = @('Root'); MainRoot = $true }
    }
}

function Get-AwWrapperText {
    # The wrapper of one tool, rendered from the template with LF line endings and one final newline.
    param(
        [Parameter(Mandatory)]
        [string]$KitDir,
        [Parameter(Mandatory)]
        [string]$Tool,
        [string]$Summary,
        [string[]]$Selectors,
        [Nullable[bool]]$MainRoot
    )

    $table = Get-AwWrapperTable
    if ($null -eq $MainRoot) {
        $MainRoot = $table.Contains($Tool) -and $table[$Tool].Contains('MainRoot') -and $table[$Tool].MainRoot
    }
    if (-not $Summary) {
        if (-not $table.Contains($Tool)) {
            throw "No wrapper summary for tool '$Tool'."
        }
        $Summary = $table[$Tool].Summary
    }
    if (-not $Selectors) {
        $Selectors = if ($table.Contains($Tool)) { @($table[$Tool].Selectors) } else { @('Root') }
    }
    $template = [IO.File]::ReadAllText((Join-Path $KitDir 'templates\wrapper.ps1')) -replace "`r`n", "`n"
    $text = $template.Replace('{{tool}}', $Tool).Replace('{{summary}}', $Summary).Replace('{{selectors}}', ($Selectors -join ',')).Replace('{{mainRoot}}', $(if ($MainRoot) { 'main' } else { 'here' }))
    return $text.TrimEnd("`n") + "`n"
}

function Get-AwWrapperDifferences {
    # The consumer wrappers (scripts/<tool>.ps1 that call the vendored tool) whose text is not the render of the template.
    # A script of the same name that does not call the vendored tool is the consumer's own and is left alone.
    param(
        [Parameter(Mandatory)]
        [string]$KitDir,
        [Parameter(Mandatory)]
        [string]$Root
    )

    $found = [Collections.Generic.List[object]]::new()
    foreach ($tool in (Get-AwWrapperTable).Keys) {
        $path = Join-Path $Root "scripts\$tool.ps1"
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            continue
        }
        $actual = [IO.File]::ReadAllText($path)
        if ($actual -notmatch "'agent-workflow'\s+'tools'") {
            continue
        }
        $expected = Get-AwWrapperText -KitDir $KitDir -Tool $tool
        if (($actual -replace "`r`n", "`n").TrimEnd("`n") -cne $expected.TrimEnd("`n")) {
            $found.Add([pscustomobject]@{ Tool = $tool; Path = $path; Relative = "scripts/$tool.ps1"; Expected = $expected; Actual = $actual })
        }
    }

    return @($found)
}
