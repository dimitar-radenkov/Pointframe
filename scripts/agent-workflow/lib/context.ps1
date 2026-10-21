#Requires -Version 7.0
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Shared by the gate, merge, review, status and Codex tools. A tool never asks the caller's working folder where the
# repository is: the target is the explicit -Root, else the explicit -Worktree, else the repository that holds the
# script (walking up from the script to agent-workflow.json). An explicit path is an ordinary command line argument,
# so a relative one is resolved the way every other path argument is.

function ConvertTo-AwFullPath {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    if ([IO.Path]::IsPathRooted($Path)) {
        return [IO.Path]::GetFullPath($Path)
    }
    return $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path)
}

function Get-AwToolContext {
    param(
        [string]$Root,
        [string]$Worktree,
        [Parameter(Mandatory)]
        [string]$ScriptRoot
    )

    $explicit = if ($Root) { $Root } elseif ($Worktree) { $Worktree } else { $null }
    if ($explicit) {
        $resolved = Resolve-AwRoot -Explicit (ConvertTo-AwFullPath $explicit) -ScriptRoot $ScriptRoot
    }
    else {
        $resolved = Resolve-AwRoot -ScriptRoot $ScriptRoot
    }

    $config = Read-AwConfig -Root $resolved.WorkTree
    $baseRef = [string]$config.repository.baseRef
    $slash = $baseRef.IndexOf('/')
    $pinFiles = @()
    if ($null -ne $config.pinException) {
        $pinFiles = @($config.pinException.paths | ForEach-Object { [string]$_ })
    }

    return [pscustomobject][ordered]@{
        Root = $resolved.WorkTree
        MainCheckout = $resolved.MainCheckout
        CommonDir = $resolved.CommonDir
        KitDir = $resolved.KitDir
        Config = $config
        StateDir = [IO.Path]::GetFullPath((Join-Path $resolved.CommonDir ([string]$config.state.commonDirNamespace)))
        BaseRef = $baseRef
        BaseRemote = $(if ($slash -gt 0) { $baseRef.Substring(0, $slash) } else { 'origin' })
        BaseBranch = $(if ($slash -gt 0) { $baseRef.Substring($slash + 1) } else { $baseRef })
        PinFiles = $pinFiles
    }
}

function Get-AwSection {
    # An optional top-level section of the configuration, or an empty table when the consumer left it out.
    param(
        [Parameter(Mandatory)]
        $Context,
        [Parameter(Mandatory)]
        [string]$Name
    )

    if ($Context.Config.Contains($Name) -and $null -ne $Context.Config[$Name]) {
        return $Context.Config[$Name]
    }
    return @{}
}

function Get-AwSetting {
    param(
        [Parameter(Mandatory)]
        $Section,
        [Parameter(Mandatory)]
        [string]$Name,
        $Default
    )

    if ($Section.Contains($Name) -and $null -ne $Section[$Name]) {
        return $Section[$Name]
    }
    return $Default
}

function Invoke-AwHook {
    # Runs a repository hook (a repository-relative PowerShell script named in agent-workflow.json) in a child process
    # with the repository as its working folder. Configured is false for a null hook: the caller says so and never
    # treats it as passed. Ran is false when the file is missing, cannot start, or times out. The exit code and the
    # standard output lines belong to the hook's own protocol.
    param(
        [Parameter(Mandatory)]
        $Context,
        [Parameter(Mandatory)]
        [string]$Name,
        [string[]]$HookArgs = @(),
        [int]$TimeoutSeconds = 300,
        [string]$WorkingDirectory
    )

    $rel = if ($Context.Config.hooks.Contains($Name)) { $Context.Config.hooks[$Name] } else { $null }
    if ($null -eq $rel) {
        return [pscustomobject]@{ Configured = $false; Ran = $false; ExitCode = $null; Lines = @(); Errors = ''; Detail = "hooks.$Name is not configured" }
    }

    $path = [IO.Path]::GetFullPath((Join-Path $Context.Root ([string]$rel)))
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        return [pscustomobject]@{ Configured = $true; Ran = $false; ExitCode = $null; Lines = @(); Errors = ''; Detail = "hook not found: $rel" }
    }

    try {
        $psi = [Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path)
        foreach ($argument in @('-NoProfile', '-NonInteractive', '-File', $path) + $HookArgs) {
            [void]$psi.ArgumentList.Add($argument)
        }
        $psi.WorkingDirectory = $(if ($WorkingDirectory) { $WorkingDirectory } else { $Context.Root })
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $psi.RedirectStandardInput = $true
        $psi.UseShellExecute = $false
        $psi.StandardOutputEncoding = [Text.Encoding]::UTF8
        $psi.StandardErrorEncoding = [Text.Encoding]::UTF8
        $process = [Diagnostics.Process]::Start($psi)
        $process.StandardInput.Close()
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $process.Kill($true)
            return [pscustomobject]@{ Configured = $true; Ran = $false; ExitCode = $null; Lines = @(); Errors = ''; Detail = "hook timed out after $TimeoutSeconds s: $rel" }
        }
        $process.WaitForExit()
        $lines = @(($stdout.Result -split "`r?`n") | Where-Object { $_.Trim() -ne '' })
        return [pscustomobject]@{ Configured = $true; Ran = $true; ExitCode = $process.ExitCode; Lines = $lines; Errors = $stderr.Result; Detail = '' }
    }
    catch {
        return [pscustomobject]@{ Configured = $true; Ran = $false; ExitCode = $null; Lines = @(); Errors = ''; Detail = "hook failed: $rel ($($_.Exception.Message))" }
    }
}

function Get-AwGateContext {
    # The context of the merge gate (merge-pr, record-review). The gate configuration (required checks, pinException,
    # hooks.pin, baseRef, the state namespace) is read ONLY from the main checkout of the repository that holds the target
    # worktree: a pull request can change its own agent-workflow.json, and a gate must never be loosened by the change it
    # judges. -Worktree (else -Root, else the repository that holds the script) only selects the tree to check. An explicit
    # -Root must be the main checkout of that same repository, otherwise it is refused.
    param(
        [string]$Root,
        [string]$Worktree,
        [Parameter(Mandatory)]
        [string]$ScriptRoot
    )

    $selected = if ($Worktree) { $Worktree } elseif ($Root) { $Root } else { $null }
    if ($selected) {
        $target = Resolve-AwRoot -Explicit (ConvertTo-AwFullPath $selected) -ScriptRoot $ScriptRoot
    }
    else {
        $target = Resolve-AwRoot -ScriptRoot $ScriptRoot
    }

    if ($Root) {
        $given = Resolve-AwRoot -Explicit (ConvertTo-AwFullPath $Root) -ScriptRoot $ScriptRoot
        if (-not [string]::Equals($given.CommonDir, $target.CommonDir, [StringComparison]::OrdinalIgnoreCase)) {
            throw "-Root '$Root' belongs to another repository than the worktree '$($target.WorkTree)'; the gate configuration is read from the main checkout of the worktree's own repository."
        }
        if (-not [string]::Equals($given.WorkTree, $target.MainCheckout, [StringComparison]::OrdinalIgnoreCase)) {
            throw "-Root '$Root' is not the main checkout of the repository ($($target.MainCheckout)); the gate configuration is read from the main checkout only."
        }
    }

    # Whenever the tool itself lives in a configured repository (a consumer's vendored copy), the repository it judges, whether named by -Root,
    # by -Worktree or by neither, must be that repository: its main checkout or one of its linked worktrees. A standalone clone is its own main
    # checkout and would be judged by its own, changeable configuration; naming it with -Root changes nothing about that. A tool outside any
    # configured repository (the kit source, or a copy of it that a self-test places outside the temporary repositories it builds) has no
    # repository to compare, and there is deliberately no switch, parameter or variable that skips the comparison.
    $holder = $null
    try {
        $holder = Resolve-AwRoot -ScriptRoot $ScriptRoot
    }
    catch {
        if ($_.Exception.Message -notmatch 'No agent-workflow\.json found') { throw }
    }
    if ($holder -and -not [string]::Equals($holder.CommonDir, $target.CommonDir, [StringComparison]::OrdinalIgnoreCase)) {
        $named = if ($Worktree) { "-Worktree '$($target.WorkTree)'" } elseif ($Root) { "-Root '$Root'" } else { "'$($target.WorkTree)'" }
        throw "$named is neither the main checkout nor a linked worktree of the repository that holds this tool ($($holder.MainCheckout)); a standalone clone or another repository is refused because it would be judged by its own configuration."
    }

    $main = Resolve-AwRoot -Explicit $target.MainCheckout -ScriptRoot $ScriptRoot
    $config = Read-AwConfig -Root $main.WorkTree
    $baseRef = [string]$config.repository.baseRef
    $slash = $baseRef.IndexOf('/')
    $pinFiles = @()
    if ($null -ne $config.pinException) {
        $pinFiles = @($config.pinException.paths | ForEach-Object { [string]$_ })
    }

    return [pscustomobject][ordered]@{
        Root = $main.WorkTree
        Worktree = $target.WorkTree
        MainCheckout = $main.MainCheckout
        CommonDir = $main.CommonDir
        KitDir = $main.KitDir
        Config = $config
        StateDir = [IO.Path]::GetFullPath((Join-Path $main.CommonDir ([string]$config.state.commonDirNamespace)))
        BaseRef = $baseRef
        BaseRemote = $(if ($slash -gt 0) { $baseRef.Substring(0, $slash) } else { 'origin' })
        BaseBranch = $(if ($slash -gt 0) { $baseRef.Substring($slash + 1) } else { $baseRef })
        PinFiles = $pinFiles
    }
}

function Get-AwCandidateLockRefusal {
    # The reason a merge is refused when the vendored kit of Root was installed as a candidate (install.ps1 -Candidate: a commit without a release
    # tag), else $null. A candidate is for trying a kit change in a consumer; it must never reach the base branch.
    # With -Revision the lock is the one COMMITTED at that revision (git show), which is what a merge puts on the base branch; a lock edited in the
    # worktree without a commit changes nothing. Without it the file in Root is read.
    param(
        [Parameter(Mandatory)]
        [string]$Root,
        [string]$Revision
    )

    try {
        if ($Revision) {
            $shown = & git -C $Root show "${Revision}:agent-workflow.lock.json" 2>$null
            if ($LASTEXITCODE -ne 0) {
                return $null
            }
            $lock = ($shown -join "`n") | ConvertFrom-Json -AsHashtable
        }
        else {
            $path = Join-Path $Root 'agent-workflow.lock.json'
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
                return $null
            }
            $lock = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -AsHashtable
        }
    }
    catch {
        return $null
    }
    if ($lock -is [Collections.IDictionary] -and $lock.Contains('candidate') -and $lock['candidate'] -eq $true) {
        return "the vendored kit is a candidate install (commit $($lock['commit']), no release tag): install a tagged release first (install.ps1 -Tag vX.Y.Z -Commit <sha>), then merge"
    }
    return $null
}

function Get-AwConfiguredGateNames {
    # The gates a complete verdict of this configuration must contain: the built-ins that are switched on, then the
    # configured gates by their gate name (default: the record id), in configuration order.
    param(
        [Parameter(Mandatory)]
        [Collections.IDictionary]$Config
    )

    $names = [Collections.Generic.List[string]]::new()
    $settings = if ($Config.Contains('verify') -and $null -ne $Config['verify']) { $Config['verify'] } else { @{} }
    if ($settings.Contains('preflight') -and $null -ne $settings['preflight']) { $names.Add('preflight') }
    if ($settings.Contains('doctor') -and $settings['doctor'] -eq $true) { $names.Add('doctor') }
    foreach ($record in $Config.gates) {
        $name = if ($record.Contains('gate')) { [string]$record.gate } else { [string]$record.id }
        if (-not $names.Contains($name)) { $names.Add($name) }
    }
    return $names.ToArray()
}