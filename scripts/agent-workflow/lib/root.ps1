#Requires -Version 7.0
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Resolve-AwRoot {
    param(
        [string]$Explicit,
        [Parameter(Mandatory)]
        [string]$ScriptRoot
    )

    if (-not [IO.Path]::IsPathFullyQualified($ScriptRoot)) {
        throw 'ScriptRoot must be an absolute path.'
    }

    $selected = $null
    if ($PSBoundParameters.ContainsKey('Explicit') -and $Explicit) {
        if (-not [IO.Path]::IsPathFullyQualified($Explicit)) {
            throw 'Explicit root must be an absolute path.'
        }

        $selected = [IO.Path]::GetFullPath($Explicit)
        if (-not (Test-Path -LiteralPath $selected -PathType Container)) {
            throw "Root does not exist: $selected"
        }
    }
    else {
        $candidate = [IO.DirectoryInfo]::new([IO.Path]::GetFullPath($ScriptRoot))
        while ($null -ne $candidate) {
            if (Test-Path -LiteralPath (Join-Path $candidate.FullName 'agent-workflow.json') -PathType Leaf) {
                $selected = $candidate.FullName
                break
            }
            $candidate = $candidate.Parent
        }

        if (-not $selected) {
            throw "No agent-workflow.json found walking up from '$ScriptRoot'."
        }
    }

    $workTreeOutput = & git -C $selected rev-parse --show-toplevel 2>$null
    if ($LASTEXITCODE -ne 0) {
        throw "Not a Git work tree: $selected"
    }

    $workTree = [IO.Path]::GetFullPath(($workTreeOutput | Select-Object -First 1).Trim())
    if (-not $Explicit -and $workTree -ne [IO.Path]::GetFullPath($selected)) {
        throw "agent-workflow.json must be at the Git work-tree root: $selected"
    }

    $commonOutput = & git -C $selected rev-parse --path-format=absolute --git-common-dir 2>$null
    if ($LASTEXITCODE -ne 0) {
        throw "Could not resolve Git common directory: $selected"
    }

    $worktrees = & git -C $selected worktree list --porcelain 2>$null
    if ($LASTEXITCODE -ne 0) {
        throw "Could not list Git worktrees: $selected"
    }

    $firstWorktree = $worktrees |
        Where-Object { $_ -match '^worktree ' } |
        Select-Object -First 1
    if (-not $firstWorktree) {
        throw "Git reported no main checkout for '$selected'."
    }

    $mainCheckout = [IO.Path]::GetFullPath($firstWorktree.Substring(9))
    $commonDir = [IO.Path]::GetFullPath(($commonOutput | Select-Object -First 1).Trim())
    $kitDir = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

    return [pscustomobject][ordered]@{
        WorkTree = $workTree
        MainCheckout = $mainCheckout
        CommonDir = $commonDir
        KitDir = $kitDir
    }
}
