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
