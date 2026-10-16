[CmdletBinding()]
param(
    [ValidateSet('pkhex', 'dngrep', 'electron', 'wpf-sample')][string]$App,
    [ValidateSet('claude', 'codex')][string]$Agent = 'claude',
    [ValidateSet('change', 'create-skill')][string]$Task = 'change',
    [string]$Version,
    [string]$OutDirectory,
    [switch]$Matrix,
    [switch]$SelfTest,
    [switch]$FreshMachine,
    [string]$MetricsPath,
    [int]$TimeoutMinutes = 30,
    [switch]$AllowCodexFullAccess,
    [string]$AgentModel,
    [string]$AgentEffort,
    [int]$MaxToolCalls,
    [long]$MaxInputTokens,
    [int]$MaxMinutes,
    [string[]]$Skills,
    [string]$ClaudePluginDir,
    [ValidateRange(1, 100)][int]$Repeat = 1
)

# One reproducible fresh-agent trial: a coding agent adds a small UI change to an OSS Windows app and must set up
# Pointframe (from its public docs only) so the change is verified on the running app. The harness, not the agent,
# scores the run. Outcomes: pass-clean, pass-workaround, fail, timeout, invalid. Only pass-clean counts as a pass.
#
#   pwsh scripts/trial.ps1 -App pkhex [-Version 6.7.85] [-OutDirectory <dir>]
#   pwsh scripts/trial.ps1 -Matrix        # all 3 apps, sequentially
#   pwsh scripts/trial.ps1 -App wpf-sample -Task create-skill [-Skills <dir>[,<dir>]] [-Repeat 2]
#   pwsh scripts/trial.ps1 -App wpf-sample -Agent codex -AllowCodexFullAccess [-AgentModel gpt-6-luna] [-AgentEffort medium]
#   pwsh scripts/trial.ps1 -SelfTest      # offline: env allowlist, scoring fixtures, process selection, metrics
#
# Needs the interactive desktop (it holds %TEMP%\pointframe-desktop.lock), git, gh (latest release lookup), and the
# claude CLI, signed in. Exit code: 0 pass-clean, 1 pass-workaround/fail/timeout, 2 invalid.
#
# Codex full access is refused unless explicitly approved for the package with -AllowCodexFullAccess.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$script:RepositoryRoot = Split-Path $PSScriptRoot -Parent
$script:AppsPath = Join-Path $PSScriptRoot 'trials/apps.json'
$script:FixtureDirectory = Join-Path $PSScriptRoot 'tests/trial'
$script:DesktopLockPath = Join-Path ([System.IO.Path]::GetTempPath()) 'pointframe-desktop.lock'
$script:LockStream = $null
$script:ReleaseRepository = 'dimitar-radenkov/Pointframe'
$script:CliModel = 'claude-sonnet-5-5'

# The child environment is built from this allowlist and nothing else. Everything the agent CLIs, git, dotnet and
# node need is named here; anything not named (VS Code, Claude Code and Codex session variables, POINTFRAME_*,
# ELECTRON_RUN_AS_NODE, tokens for other services) never reaches the agent. PATH is kept but filtered.
# Claude/Codex credentials normally live under USERPROFILE/CODEX_HOME. Redirecting LOCALAPPDATA can hide their
# machine-local caches/update state (and dotnet/gh caches); fresh-machine runs record this limitation rather than
# exposing the real profile and thereby exposing installed Pointframe components.
$script:AllowedEnvironment = @(
    'SystemRoot', 'windir', 'SystemDrive', 'ComSpec', 'PATHEXT', 'TEMP', 'TMP', 'USERPROFILE', 'HOMEDRIVE', 'HOMEPATH',
    'APPDATA', 'LOCALAPPDATA', 'ProgramFiles', 'ProgramFiles(x86)', 'ProgramW6432', 'ProgramData', 'CommonProgramFiles',
    'CommonProgramFiles(x86)', 'CommonProgramW6432', 'ALLUSERSPROFILE', 'PUBLIC', 'USERNAME', 'USERDOMAIN', 'COMPUTERNAME',
    'OS', 'PROCESSOR_ARCHITECTURE', 'PROCESSOR_IDENTIFIER', 'NUMBER_OF_PROCESSORS', 'PSModulePath',
    'ANTHROPIC_API_KEY', 'ANTHROPIC_AUTH_TOKEN', 'CLAUDE_CODE_OAUTH_TOKEN', 'OPENAI_API_KEY',
    # Codex CLI uses these for endpoint selection and login; Codex session/workspace variables remain excluded.
    'CODEX_HOME', 'OPENAI_BASE_URL',
    'HTTP_PROXY', 'HTTPS_PROXY', 'NO_PROXY', 'http_proxy', 'https_proxy', 'no_proxy', 'SSL_CERT_FILE', 'NODE_EXTRA_CA_CERTS',
    'DOTNET_ROOT', 'DOTNET_ROOT(x86)', 'DOTNET_CLI_HOME', 'DOTNET_NOLOGO', 'DOTNET_CLI_TELEMETRY_OPTOUT',
    'NUGET_PACKAGES', 'NUGET_HTTP_CACHE_PATH'
)
$script:BuildServerNames = @('dotnet.exe', 'VBCSCompiler.exe', 'MSBuild.exe', 'conhost.exe')
$script:ForbiddenPattern = '^(POINTFRAME_.*|ELECTRON_RUN_AS_NODE)$'

# ---------------------------------------------------------------------------------------------------------------
# Small helpers

function Get-Fact {
    # Reads a property of a hashtable or a parsed JSON object; StrictMode makes a missing property an error.
    param($Facts, [Parameter(Mandatory)][string]$Name, $Default = $null)
    if ($null -eq $Facts) { return $Default }
    if ($Facts -is [System.Collections.IDictionary]) {
        if ($Facts.Contains($Name)) { return $Facts[$Name] }
        return $Default
    }
    $property = $Facts.PSObject.Properties[$Name]
    if ($null -eq $property) { return $Default }
    return $property.Value
}

function Get-Sha256Hex {
    param([Parameter(Mandatory)][string]$Path)
    $stream = [System.IO.File]::OpenRead($Path)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { return ([Convert]::ToHexString($sha.ComputeHash($stream))).ToLowerInvariant() }
    finally { $sha.Dispose(); $stream.Dispose() }
}

function Get-VersionNumber {
    # "6.7.85", "6.7.85+abc123" and "Pointframe 6.7.85-beta" all become 6.7.85; anything else is null.
    param([string]$Text)
    if ($Text -match '(\d+\.\d+\.\d+)') { return $Matches[1] }
    return $null
}

function Get-TextHit {
    param([string]$Text, [string[]]$Markers)
    if (-not $Text) { return $false }
    foreach ($marker in $Markers) {
        if ($Text.IndexOf($marker, [StringComparison]::OrdinalIgnoreCase) -ge 0) { return $true }
    }
    return $false
}

# ---------------------------------------------------------------------------------------------------------------
# Isolation: child environment

function Get-ChildEnvironment {
    # Returns the environment the agent gets: allowlisted names only, PATH without any Pointframe directory, every
    # forbidden name removed. -Source is the parent environment (a dictionary), -Extra names added after filtering.
    param([Parameter(Mandatory)][System.Collections.IDictionary]$Source, [System.Collections.IDictionary]$Extra, [string]$FreshLocalAppData)
    $result = [System.Collections.Generic.Dictionary[string, string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($name in $script:AllowedEnvironment) {
        if ($name -match $script:ForbiddenPattern) { continue }
        foreach ($key in @($Source.Keys)) {
            if ([string]::Equals([string]$key, $name, [StringComparison]::OrdinalIgnoreCase)) {
                $result[$name] = [string]$Source[$key]
            }
        }
    }
    foreach ($key in @($Source.Keys)) {
        if ([string]::Equals([string]$key, 'Path', [StringComparison]::OrdinalIgnoreCase)) {
            $entries = @(([string]$Source[$key]) -split ';' | Where-Object { $_ -and $_ -notmatch 'Pointframe' })
            $result['Path'] = $entries -join ';'
        }
    }
    if ($FreshLocalAppData) { $result['LOCALAPPDATA'] = [IO.Path]::GetFullPath($FreshLocalAppData) }
    if ($null -ne $Extra) {
        foreach ($key in @($Extra.Keys)) {
            if ([string]$key -match $script:ForbiddenPattern) { throw "Refusing to add forbidden variable '$key' to the child environment." }
            $result[[string]$key] = [string]$Extra[$key]
        }
    }
    return $result
}

function Get-ForbiddenVariableNames {
    param([string[]]$Names)
    return @($Names | Where-Object { $_ -match $script:ForbiddenPattern })
}

function Get-ParentEnvironment {
    $table = [System.Collections.Generic.Dictionary[string, string]]::new([StringComparer]::OrdinalIgnoreCase)
    $variables = [Environment]::GetEnvironmentVariables()
    foreach ($key in $variables.Keys) { $table[[string]$key] = [string]$variables[$key] }
    return $table
}

function Resolve-LaunchSpec {
    # npm shims (claude.cmd) cannot be started directly with UseShellExecute=false.
    param([Parameter(Mandatory)][string]$Path, [string[]]$Arguments = @())
    switch ([IO.Path]::GetExtension($Path).ToLowerInvariant()) {
        { $_ -in '.cmd', '.bat' } { return [pscustomobject]@{ File = $env:ComSpec; Arguments = @('/d', '/c', $Path) + $Arguments } }
        '.ps1' { return [pscustomobject]@{ File = 'pwsh'; Arguments = @('-NoProfile', '-File', $Path) + $Arguments } }
        default { return [pscustomobject]@{ File = $Path; Arguments = $Arguments } }
    }
}

function New-ChildStartInfo {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [string[]]$Arguments = @(),
        [string]$WorkingDirectory,
        [Parameter(Mandatory)][System.Collections.IDictionary]$Environment
    )
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $FilePath
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardInput = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.StandardInputEncoding = [Text.UTF8Encoding]::new($false)
    $info.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    $info.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
    if ($WorkingDirectory) { $info.WorkingDirectory = $WorkingDirectory }
    foreach ($argument in $Arguments) { [void]$info.ArgumentList.Add($argument) }
    $info.Environment.Clear()
    foreach ($key in $Environment.Keys) { $info.Environment[[string]$key] = [string]$Environment[$key] }
    return $info
}

function Invoke-Captured {
    # Runs a short command with a given environment and returns its output; the process is killed on timeout.
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [string[]]$Arguments = @(),
        [string]$WorkingDirectory,
        [Parameter(Mandatory)][System.Collections.IDictionary]$Environment,
        [int]$TimeoutSeconds = 120
    )
    $launch = Resolve-LaunchSpec $FilePath $Arguments
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = New-ChildStartInfo $launch.File $launch.Arguments $WorkingDirectory $Environment
    [void]$process.Start()
    $process.StandardInput.Close()
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    $timedOut = $false
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        $timedOut = $true
        try { $process.Kill($true) } catch { }
        [void]$process.WaitForExit(5000)
    }
    [void]$stdout.Wait(5000)
    [void]$stderr.Wait(5000)
    $exit = if ($timedOut) { -1 } else { $process.ExitCode }
    $result = [pscustomobject]@{
        ExitCode = $exit
        TimedOut = $timedOut
        Output = if ($stdout.IsCompleted) { $stdout.Result } else { '' }
        Error = if ($stderr.IsCompleted) { $stderr.Result } else { '' }
    }
    $process.Dispose()
    return $result
}

function Test-ChildEnvironment {
    # The probe: start a real child with the built environment and read what it sees. Returns the forbidden names
    # visible there, and whether any PATH entry still names Pointframe.
    param([Parameter(Mandatory)][System.Collections.IDictionary]$Environment)
    $run = Invoke-Captured $env:ComSpec @('/d', '/c', 'set') $null $Environment 30
    $names = @()
    $pathLeak = $false
    foreach ($line in ($run.Output -split "`r?`n")) {
        $index = $line.IndexOf('=')
        if ($index -le 0) { continue }
        $name = $line.Substring(0, $index)
        $names += $name
        if ($name -ieq 'Path' -and $line.Substring($index + 1) -match 'Pointframe') { $pathLeak = $true }
    }
    return [pscustomobject]@{ Forbidden = @(Get-ForbiddenVariableNames $names); PathLeak = $pathLeak; ProbeExit = $run.ExitCode; Seen = $names.Count }
}

# ---------------------------------------------------------------------------------------------------------------
# Process bookkeeping: only processes this run started are ever killed, matched by PID and start time

function Get-ProcessTable {
    $table = @()
    foreach ($item in @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue)) {
        if ($null -eq $item.CreationDate) { continue }
        $table += [pscustomobject]@{
            Pid = [int]$item.ProcessId
            ParentPid = [int]$item.ParentProcessId
            Start = $item.CreationDate.ToUniversalTime().ToString('o')
            Path = [string]$item.ExecutablePath
            Name = [string]$item.Name
        }
    }
    return $table
}

function Find-TrialProcesses {
    # The agent root, every descendant of it, and any process whose image lies under one of the trial directories.
    # A child must have started after its parent, so a recycled parent PID does not pull in strangers.
    param(
        [Parameter(Mandatory)][object[]]$Table,
        [int]$RootPid = 0,
        [string]$RootStart,
        [string[]]$Directories = @(),
        [object[]]$Known = @()
    )
    $found = [System.Collections.Generic.Dictionary[string, object]]::new()
    foreach ($entry in $Known) { $found["$($entry.Pid)|$($entry.Start)"] = $entry }
    if ($RootPid -gt 0) {
        $root = @($Table | Where-Object { $_.Pid -eq $RootPid -and $_.Start -eq $RootStart })
        if ($root.Count -gt 0) { $found["$($root[0].Pid)|$($root[0].Start)"] = $root[0] }
    }
    $changed = $true
    while ($changed) {
        $changed = $false
        foreach ($candidate in $Table) {
            $key = "$($candidate.Pid)|$($candidate.Start)"
            if ($found.ContainsKey($key)) { continue }
            # The recorded parent must still be in the table with the same start time; a recycled parent PID is not it.
            $parent = @($found.Values | Where-Object {
                $recordedParent = $_
                $recordedParent.Pid -eq $candidate.ParentPid -and [datetime]$recordedParent.Start -le [datetime]$candidate.Start -and
                @($Table | Where-Object { $_.Pid -eq $recordedParent.Pid -and $_.Start -eq $recordedParent.Start }).Count -gt 0
            })
            if ($parent.Count -gt 0) { $found[$key] = $candidate; $changed = $true }
        }
    }
    foreach ($candidate in $Table) {
        if (-not $candidate.Path) { continue }
        foreach ($directory in $Directories) {
            $prefix = [IO.Path]::GetFullPath($directory).TrimEnd('\') + '\'
            if ($candidate.Path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
                $found["$($candidate.Pid)|$($candidate.Start)"] = $candidate
            }
        }
    }
    return @($found.Values)
}

function Select-ProcessesToKill {
    # Recorded processes that are still running with the same PID AND the same start time. A reused PID has a
    # different start time and is never selected.
    param([object[]]$Recorded = @(), [object[]]$Running = @())
    $selected = @()
    foreach ($entry in $Recorded) {
        foreach ($live in $Running) {
            if ($live.Pid -eq $entry.Pid -and $live.Start -eq $entry.Start) { $selected += $entry; break }
        }
    }
    return $selected
}

function Stop-RecordedProcesses {
    param([object[]]$Recorded = @())
    $selected = @(Select-ProcessesToKill $Recorded (Get-ProcessTable))
    foreach ($entry in $selected) { Stop-Process -Id $entry.Pid -Force -ErrorAction SilentlyContinue }
    return $selected
}

# ---------------------------------------------------------------------------------------------------------------
# Scoring

function Get-TrialOutcome {
    # Pure function of the collected facts. Order: invalid isolation, timeout, failed criteria, version, defect
    # check, workaround. Only pass-clean counts as a pass.
    param([Parameter(Mandatory)]$Facts)
    $reasons = [System.Collections.Generic.List[string]]::new()

    $leak = @(Get-Fact $Facts 'envLeak' @())
    if ($leak.Count -gt 0) { $reasons.Add("forbidden variable visible in the agent environment: $($leak -join ', ')") }
    if ((Get-Fact $Facts 'pathLeak' $false) -eq $true) { $reasons.Add('a Pointframe directory is still on the agent PATH') }
    $harnessError = Get-Fact $Facts 'harnessError' $null
    if ($harnessError) { $reasons.Add("harness error: $harnessError") }
    if ($reasons.Count -gt 0) { return [pscustomobject]@{ Outcome = 'invalid'; Reasons = @($reasons) } }

    if ((Get-Fact $Facts 'timedOut' $false) -eq $true) {
        return [pscustomobject]@{ Outcome = 'timeout'; Reasons = @('the agent did not finish within the time limit') }
    }

    $defect = Get-Fact (Get-Fact $Facts 'defectCheck' $null) 'result' 'not-run'
    if ((Get-Fact $Facts 'verifyStatus' 'none') -ne 'pass') { $reasons.Add("verify status is '$(Get-Fact $Facts 'verifyStatus' 'none')', not pass") }
    if ((Get-Fact $Facts 'fresh' $false) -ne $true) { $reasons.Add('the verdict is not fresh') }
    if ((Get-Fact $Facts 'criterionTiedToChange' $false) -ne $true) { $reasons.Add('no passed criterion is tied to the change') }
    if ((Get-Fact $Facts 'negativeControl' $false) -ne $true) { $reasons.Add('no negative control passed') }
    if ((Get-Fact $Facts 'reportsVerified' $false) -ne $true) { $reasons.Add('the report proof did not verify') }
    $leftover = [int](Get-Fact $Facts 'leftoverProcesses' 0)
    if ($leftover -gt 0) { $reasons.Add("$leftover process(es) left running") }
    if ($defect -eq 'passed') { $reasons.Add('the defect check also passed: the verification does not detect the change being absent') }
    if ($defect -eq 'failed-other') { $reasons.Add('the defect check failed for a reason other than the missing change') }
    if ($reasons.Count -gt 0) { return [pscustomobject]@{ Outcome = 'fail'; Reasons = @($reasons) } }

    $expected = Get-Fact $Facts 'expectedVersion' $null
    $seen = [ordered]@{
        cli = Get-Fact $Facts 'cliVersion' $null
        mcp = Get-Fact $Facts 'mcpVersion' $null
        verifier = Get-Fact $Facts 'verifierVersion' $null
    }
    foreach ($name in $seen.Keys) {
        if (-not $seen[$name]) { $reasons.Add("the $name version the agent used could not be determined") }
        elseif ($expected -and $seen[$name] -ne $expected) { $reasons.Add("the agent used $name $($seen[$name]), expected $expected") }
    }
    if ($defect -in 'inconclusive', 'not-run') { $reasons.Add("the defect check was $defect") }
    if ($reasons.Count -gt 0) { return [pscustomobject]@{ Outcome = 'invalid'; Reasons = @($reasons) } }

    $hits = @(Get-Fact $Facts 'workaroundHits' @())
    if ($hits.Count -gt 0) {
        $listed = @($hits | ForEach-Object { "$(Get-Fact $_ 'file'): $(Get-Fact $_ 'pattern')" })
        return [pscustomobject]@{ Outcome = 'pass-workaround'; Reasons = @("the agent's source diff contains workaround patterns: $($listed -join '; ')") }
    }
    return [pscustomobject]@{ Outcome = 'pass-clean'; Reasons = @() }
}

function Copy-TrialSkills {
    param([string[]]$SkillList, [string]$ProjectDirectory, [string]$AgentName)
    $result = @()
    if (-not $SkillList) { return $result }
    $destinationRoot = if ($AgentName -eq 'claude') { '.claude/skills' } else { '.agents/skills' }
    foreach ($source in (@($SkillList | ForEach-Object { $_ -split ',' }) | ForEach-Object { $_.Trim() } | Where-Object { $_ })) {
        $resolved = (Resolve-Path -LiteralPath $source -ErrorAction Stop).Path
        if (-not (Test-Path -LiteralPath $resolved -PathType Container)) { throw "Skill source '$source' is not a directory." }
        $folder = Get-Item -LiteralPath $resolved
        $target = Join-Path (Join-Path $ProjectDirectory $destinationRoot) $folder.Name
        if (Test-Path -LiteralPath $target) { throw "Skill destination already exists: $target" }
        New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
        Copy-Item -LiteralPath $resolved -Destination $target -Recurse
        $hash = [System.Security.Cryptography.IncrementalHash]::CreateHash([System.Security.Cryptography.HashAlgorithmName]::SHA256)
        try {
            foreach ($file in Get-ChildItem -LiteralPath $target -File -Recurse | Sort-Object FullName) {
                $relative = [IO.Path]::GetRelativePath($target, $file.FullName).Replace('\', '/')
                $hash.AppendData([Text.Encoding]::UTF8.GetBytes($relative + "`0"))
                $hash.AppendData([IO.File]::ReadAllBytes($file.FullName))
            }
            $digest = [Convert]::ToHexString($hash.GetHashAndReset()).ToLowerInvariant()
        }
        finally { $hash.Dispose() }
        $result += [pscustomobject]@{ source = $resolved; destination = $target; sha256 = $digest; relativePath = (Join-Path $destinationRoot $folder.Name).Replace('\', '/') }
    }
    return $result
}

function Remove-AddedTrialPathEntries {
    # Reader/writer injection keeps cleanup testable without touching the registry.
    param([Parameter(Mandatory)][string]$TrialRoot, [Parameter(Mandatory)][scriptblock]$ReadPath, [Parameter(Mandatory)][scriptblock]$WritePath, [string]$BaselinePath = '')
    $currentValue = & $ReadPath
    $current = [string]$currentValue.Value
    $kind = $currentValue.Kind
    $baseline = @($BaselinePath -split ';' | Where-Object { $_ } | ForEach-Object { try { [IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($_)).TrimEnd('\') + '\' } catch { $_ } })
    $root = [IO.Path]::GetFullPath($TrialRoot).TrimEnd('\') + '\'
    $segments = @($current.Split([char]';'))
    $removed = [System.Collections.Generic.List[string]]::new()
    $kept = [System.Collections.Generic.List[string]]::new()
    foreach ($entry in $segments) {
        $expanded = [Environment]::ExpandEnvironmentVariables($entry)
        $full = try { [IO.Path]::GetFullPath($expanded).TrimEnd('\') + '\' } catch { '' }
        if ($entry -and $full -and $full.StartsWith($root, [StringComparison]::OrdinalIgnoreCase) -and @($baseline | Where-Object { [string]::Equals($_, $full, [StringComparison]::OrdinalIgnoreCase) }).Count -eq 0) { $removed.Add($entry) }
        else { $kept.Add($entry) }
    }
    if ($removed.Count -gt 0) { & $WritePath ($kept -join ';') $kind }
    return @($removed)
}

function Read-UserPathRegistryValue {
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Environment', $false)
    if ($null -eq $key) { return [pscustomobject]@{ Value = ''; Kind = [Microsoft.Win32.RegistryValueKind]::String } }
    try {
        if (@($key.GetValueNames()) -notcontains 'Path') { return [pscustomobject]@{ Value = ''; Kind = [Microsoft.Win32.RegistryValueKind]::String } }
        $kind = $key.GetValueKind('Path')
        return [pscustomobject]@{ Value = [string]$key.GetValue('Path', '', [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames); Kind = $kind }
    }
    finally { $key.Dispose() }
}

function Write-UserPathRegistryValue {
    param([string]$Value, [Microsoft.Win32.RegistryValueKind]$Kind)
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Environment', $true)
    if ($null -eq $key) { throw 'HKCU\\Environment could not be opened for writing.' }
    try { $key.SetValue('Path', $Value, $Kind) }
    finally { $key.Dispose() }
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class TrialEnvironmentBroadcast {
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, UIntPtr wParam, string lParam, uint flags, uint timeout, out UIntPtr result);
}
'@ -ErrorAction SilentlyContinue
    $result = [UIntPtr]::Zero
    [void][TrialEnvironmentBroadcast]::SendMessageTimeout([IntPtr]0xffff, 0x001A, [UIntPtr]::Zero, 'Environment', 0x0002, 5000, [ref]$result)
}

function Get-CreateSkillOutcome {
    param($Facts)
    $reasons = [System.Collections.Generic.List[string]]::new()
    if (@(Get-Fact $Facts 'envLeak' @()).Count -gt 0) { $reasons.Add('forbidden variable visible in the agent environment') }
    if ((Get-Fact $Facts 'pathLeak' $false) -eq $true) { $reasons.Add('a Pointframe directory is still on the agent PATH') }
    if (Get-Fact $Facts 'harnessError' $null) { $reasons.Add("harness error: $(Get-Fact $Facts 'harnessError' '')") }
    if ($reasons.Count -gt 0) { return [pscustomobject]@{ Outcome = 'invalid'; Reasons = @($reasons) } }
    if ((Get-Fact $Facts 'timedOut' $false) -eq $true) { return [pscustomobject]@{ Outcome = 'timeout'; Reasons = @('the agent did not finish within the time limit') } }
    foreach ($check in @(
        @{ Name = 'skill'; Key = 'skillValid'; Message = 'no valid verify skill with feature map was found' },
        @{ Name = 'verify status'; Key = 'verifyPassedFresh'; Message = 'verify status is not pass and fresh' },
        @{ Name = 'desktop criterion'; Key = 'desktopCriterionPassed'; Message = 'no desktop scenario has a passed criterion' },
        @{ Name = 'negative control'; Key = 'negativeControl'; Message = 'no negative control passed' },
        @{ Name = 'evidence'; Key = 'reportsVerified'; Message = 'the report proof did not survive or verify' }
    )) { if ((Get-Fact $Facts $check.Key $false) -ne $true) { $reasons.Add($check.Message) } }
    $leftover = [int](Get-Fact $Facts 'leftoverProcesses' 0)
    if ($leftover -gt 0) { $reasons.Add("$leftover process(es) left running") }
    $defect = Get-Fact (Get-Fact $Facts 'defectCheck' $null) 'result' 'not-run'
    if ($defect -eq 'passed') { $reasons.Add('the defect variant still passed verify run') }
    elseif ($defect -eq 'failed-other') { $reasons.Add('the defect variant failed for an unexpected reason') }
    $otherFailureCount = $reasons.Count
    if ($defect -in 'inconclusive', 'not-run') { $reasons.Add("the defect check was $defect") }
    if ($otherFailureCount -gt 0) { return [pscustomobject]@{ Outcome = 'fail'; Reasons = @($reasons) } }
    if ($defect -in 'inconclusive', 'not-run') { return [pscustomobject]@{ Outcome = 'invalid'; Reasons = @("the defect check was $defect") } }
    $expected = Get-Fact $Facts 'expectedVersion' $null
    foreach ($name in 'cliVersion', 'mcpVersion', 'verifierVersion') {
        $actual = Get-Fact $Facts $name $null
        if ($expected -and $actual -ne $expected) { $reasons.Add("the agent used $name $actual, expected $expected") }
    }
    if ($reasons.Count -gt 0) { return [pscustomobject]@{ Outcome = 'invalid'; Reasons = @($reasons) } }
    return [pscustomobject]@{ Outcome = 'pass-clean'; Reasons = @() }
}

function New-AgentInvocation {
    param(
        [ValidateSet('claude', 'codex')][string]$AgentName,
        [string[]]$SkillList,
        [string]$PluginDirectory,
        [string]$RunModel,
        [string]$RunEffort,
        [string]$McpConfigPath
    )
    $SkillList = @($SkillList | Where-Object { $_ })
    if ($AgentName -eq 'claude') {
        $model = if ($RunModel) { $RunModel } else { $script:CliModel }
        $allowedTools = 'Bash,Edit,Write,Read,Glob,Grep,WebFetch,WebSearch,TodoWrite'
        if ($SkillList.Count -gt 0 -or $PluginDirectory) { $allowedTools += ',Skill' }
        $arguments = @('-p', '--model', $model, '--setting-sources', 'project', '--max-budget-usd', '3',
            '--strict-mcp-config', '--mcp-config', $McpConfigPath, '--allowedTools', $allowedTools,
            '--output-format', 'stream-json', '--verbose')
        if ($PluginDirectory) { $arguments += @('--plugin-dir', (Resolve-Path -LiteralPath $PluginDirectory -ErrorAction Stop).Path) }
        return [pscustomobject]@{ Arguments = $arguments; Model = $model; Effort = $null; AllowedTools = $allowedTools }
    }
    $model = if ($RunModel) { $RunModel } else { 'gpt-6-luna' }
    $effort = if ($RunEffort) { $RunEffort } else { 'medium' }
    $arguments = @('exec', '--json', '--model', $model, '-c', "model_reasoning_effort=$effort", '--dangerously-bypass-approvals-and-sandbox', '-')
    return [pscustomobject]@{ Arguments = $arguments; Model = $model; Effort = $effort; AllowedTools = $null }
}

function Get-CreateSkillFacts {
    param([string]$ProjectDirectory, $Verdict, $Spec, [string[]]$CopiedSkillPaths = @())
    $result = [ordered]@{ skillValid = $false; skillPath = $null; featurePath = $null; verifyPassedFresh = $false; desktopCriterionPassed = $false }
    foreach ($base in @('.claude/skills', '.agents/skills', '.cursor/skills')) {
        $root = Join-Path $ProjectDirectory $base
        if (-not (Test-Path -LiteralPath $root -PathType Container)) { continue }
        foreach ($skillFile in Get-ChildItem -LiteralPath $root -Filter SKILL.md -File -Recurse) {
            $relativeSkillPath = [IO.Path]::GetRelativePath($ProjectDirectory, $skillFile.FullName).Replace([IO.Path]::DirectorySeparatorChar, '/')
            if (@($CopiedSkillPaths | Where-Object { $relativeSkillPath -eq $_ -or $relativeSkillPath.StartsWith($_.TrimEnd('/') + '/', [StringComparison]::OrdinalIgnoreCase) }).Count -gt 0) { continue }
            $content = Get-Content -LiteralPath $skillFile.FullName -Raw
            if ($content -notmatch '(?ms)^---\s*\r?\n(?<yaml>.*?)\r?\n---') { continue }
            if ($Matches.yaml -notmatch '(?m)^name:\s*verify-[^\s#]+\s*$') { continue }
            $features = Join-Path $skillFile.DirectoryName 'features'
            if (-not (Test-Path -LiteralPath $features -PathType Container)) { continue }
            $validFile = $false
            foreach ($feature in Get-ChildItem -LiteralPath $features -File -Recurse) {
                $text = Get-Content -LiteralPath $feature.FullName -Raw
                $headers = @('Sub-features', 'How to get to it \(user POV\)', 'Driving it with .+', 'Gotchas')
                if (@($headers | Where-Object { $text -notmatch "(?m)^## $_\s*$" }).Count -gt 0) { continue }
                $known = @(Get-Fact $Spec 'scenarios' @() | ForEach-Object { [string](Get-Fact $_ 'id' '') })
                if (@($known | Where-Object { $_ -and $text -match "(?<![A-Za-z0-9_-])$([regex]::Escape($_))(?![A-Za-z0-9_-])" }).Count -gt 0) { $validFile = $true; break }
            }
            if ($validFile) { $result.skillValid = $true; $result.skillPath = $skillFile.FullName; $result.featurePath = $features; break }
        }
        if ($result.skillValid) { break }
    }
    $result.verifyPassedFresh = (Get-Fact $Verdict 'status' '') -eq 'pass' -and (Get-Fact $Verdict 'fresh' $false) -eq $true
    $desktopIds = @()
    foreach ($specScenario in @(Get-Fact $Spec 'scenarios' @())) {
        $desktopSteps = @((Get-Fact $specScenario 'steps' @()) | Where-Object { $null -ne (Get-Fact $_ 'invoke' $null) -or $null -ne (Get-Fact $_ 'enterText' $null) })
        if ($desktopSteps.Count -gt 0) { $desktopIds += [string](Get-Fact $specScenario 'id' '') }
    }
    foreach ($scenario in @(Get-Fact $Verdict 'scenarios' @())) {
        if ($desktopIds -notcontains [string](Get-Fact $scenario 'id' '')) { continue }
        $passedCriteria = @((Get-Fact $scenario 'criteria' @()) | Where-Object { (Get-Fact $_ 'verdict' '') -match '^pass' })
        $passedCriterionSteps = @((Get-Fact $scenario 'steps' @()) | Where-Object { (Get-Fact $_ 'status' '') -match '^pass' -and (Get-Fact $_ 'criterion' $null) })
        if ($passedCriteria.Count -gt 0 -or $passedCriterionSteps.Count -gt 0) { $result.desktopCriterionPassed = $true }
        if ($result.desktopCriterionPassed) { break }
    }
    return $result
}

function Format-RepeatSummary {
    param([object[]]$Rows)
    return @($Rows | Select-Object run, outcome, gate, toolCalls, totalInputTokens, minutes | Format-Table -AutoSize | Out-String).TrimEnd()
}

function Get-WorkaroundHits {
    # Added lines of a unified diff that match a workaround pattern, in source files only. A pattern the change
    # prompt itself asks for is exempt.
    param([string]$DiffText, [string[]]$Patterns, [string[]]$SourceExtensions, [string]$ChangePrompt)
    $active = @($Patterns | Where-Object { -not [regex]::IsMatch($ChangePrompt, $_) })
    $hits = @()
    $file = $null
    foreach ($line in ($DiffText -split "`r?`n")) {
        if ($line.StartsWith('+++ ')) {
            $file = if ($line -match '^\+\+\+ b/(.+)$') { $Matches[1] } else { $null }
            continue
        }
        if ($null -eq $file -or -not $line.StartsWith('+')) { continue }
        if ([IO.Path]::GetExtension($file).ToLowerInvariant() -notin $SourceExtensions) { continue }
        foreach ($pattern in $active) {
            if ([regex]::IsMatch($line, $pattern)) { $hits += [pscustomobject]@{ file = $file; pattern = $pattern; line = $line.Substring(1).Trim() } }
        }
    }
    return $hits
}

function Test-CriterionTiedToChange {
    # A passed criterion (or a passed step) whose text or locator names the new element, not just app-starts.
    param($Verdict, [string[]]$Markers)
    foreach ($scenario in @(Get-Fact $Verdict 'scenarios' @())) {
        foreach ($criterion in @(Get-Fact $scenario 'criteria' @())) {
            if ((Get-Fact $criterion 'verdict' '') -match '^pass' -and (Get-TextHit (Get-Fact $criterion 'text' '') $Markers)) { return $true }
        }
        foreach ($step in @(Get-Fact $scenario 'steps' @())) {
            if ((Get-Fact $step 'status' '') -notmatch '^pass') { continue }
            $text = @('description', 'expected', 'actual') | ForEach-Object { Get-Fact $step $_ '' }
            if (Get-TextHit ($text -join ' ') $Markers) { return $true }
        }
    }
    return $false
}

function Test-NegativeControl {
    # The spec has a check with expectFailure in a scenario the verdict says passed.
    param($Spec, $Verdict)
    foreach ($scenario in @(Get-Fact $Spec 'scenarios' @())) {
        $controls = @(@(Get-Fact $scenario 'steps' @()) | Where-Object { (Get-Fact (Get-Fact $_ 'check' $null) 'expectFailure' $false) -eq $true })
        if ($controls.Count -eq 0) { continue }
        $id = Get-Fact $scenario 'id' ''
        foreach ($result in @(Get-Fact $Verdict 'scenarios' @())) {
            if ((Get-Fact $result 'id' '') -eq $id -and (Get-Fact $result 'status' '') -eq 'pass') { return $true }
        }
    }
    return $false
}

function Get-DefectResult {
    # Classifies the verdict of the run on the reverted copy.
    param($Verdict, $Expected)
    if ($null -eq $Verdict) { return 'inconclusive' }
    $status = Get-Fact $Verdict 'status' ''
    if ($status -eq 'pass') { return 'passed' }
    if ($status -ne 'fail') { return 'inconclusive' }
    $codes = @(Get-Fact $Expected 'codes' @())
    foreach ($scenario in @(Get-Fact $Verdict 'scenarios' @())) {
        foreach ($step in @(Get-Fact $scenario 'steps' @())) {
            $stepStatus = Get-Fact $step 'status' ''
            if ($stepStatus -in 'pass', 'skipped', '') { continue }
            if ((Get-Fact $step 'code' '') -in $codes) { return 'failed-expected' }
        }
    }
    return 'failed-other'
}

function Get-TranscriptMetrics {
    param([string]$Path, [string]$Agent = 'claude')
    $metrics = [ordered]@{ turns = $null; toolCalls = $null; commands = $null; inputTokens = $null; cacheCreationInputTokens = $null; cachedInputTokens = $null; totalInputTokens = $null; outputTokens = $null; costUsd = $null }
    if (-not (Test-Path -LiteralPath $Path)) { return $metrics }
    foreach ($line in [IO.File]::ReadLines($Path)) {
        try {
            $resultEvent = $line | ConvertFrom-Json
            if ($Agent -eq 'codex') {
                $type = Get-Fact $resultEvent 'type' ''
                if ($type -eq 'item.completed') {
                    $item = Get-Fact $resultEvent 'item' $null
                    if ((Get-Fact $item 'type' '') -in 'command_execution', 'mcp_tool_call') { if ($null -eq $metrics.toolCalls) { $metrics.toolCalls = 0 }; $metrics.toolCalls++ }
                }
                if ($type -eq 'turn.completed') {
                    $usage = Get-Fact $resultEvent 'usage' $null
                    foreach ($name in 'inputTokens', 'cachedInputTokens', 'outputTokens') { if ($null -eq $metrics[$name]) { $metrics[$name] = 0 } }
                    if ($null -eq $metrics.turns) { $metrics.turns = 0 }
                    $metrics.inputTokens += [long](Get-Fact $usage 'input_tokens' 0)
                    $metrics.cachedInputTokens += [long](Get-Fact $usage 'cached_input_tokens' 0)
                    # Codex input_tokens already includes cached input (agent-run.ps1 Get-TokenUsage uses it directly).
                    $metrics.totalInputTokens = $metrics.inputTokens
                    $metrics.outputTokens += [long](Get-Fact $usage 'output_tokens' 0)
                    $metrics.turns++
                }
                continue
            }
            $type = Get-Fact $resultEvent 'type' ''
            if ($type -eq 'assistant') {
                foreach ($block in @(Get-Fact (Get-Fact $resultEvent 'message' $null) 'content' @())) {
                    if ((Get-Fact $block 'type' '') -eq 'tool_use') { if ($null -eq $metrics.toolCalls) { $metrics.toolCalls = 0 }; $metrics.toolCalls++ }
                }
            }
            if ($type -eq 'result') {
                $metrics.turns = Get-Fact $resultEvent 'num_turns' $null
                $metrics.costUsd = Get-Fact $resultEvent 'total_cost_usd' $null
                $usage = Get-Fact $resultEvent 'usage' $null
                $metrics.inputTokens = Get-Fact $usage 'input_tokens' $null
                $metrics.cacheCreationInputTokens = Get-Fact $usage 'cache_creation_input_tokens' $null
                $metrics.cachedInputTokens = Get-Fact $usage 'cache_read_input_tokens' $null
                $metrics.outputTokens = Get-Fact $usage 'output_tokens' $null
                if ($null -ne $metrics.inputTokens -and $null -ne $metrics.cacheCreationInputTokens -and $null -ne $metrics.cachedInputTokens) {
                    $metrics.totalInputTokens = [long]$metrics.inputTokens + [long]$metrics.cacheCreationInputTokens + [long]$metrics.cachedInputTokens
                }
            }
        }
        catch { $metrics['note'] = "transcript line could not be parsed: $($_.Exception.Message)" }
    }
    return $metrics
}

function Get-LimitEvaluation {
    param($Metrics, [int]$ToolLimit, [long]$InputLimit, [int]$MinuteLimit)
    $failed = [System.Collections.Generic.List[string]]::new()
    $notMeasured = [System.Collections.Generic.List[string]]::new()
    foreach ($check in @(
        @{ Name = 'toolCalls'; Limit = $ToolLimit; Value = (Get-Fact $Metrics 'toolCalls' $null) },
        @{ Name = 'totalInputTokens'; Limit = $InputLimit; Value = (Get-Fact $Metrics 'totalInputTokens' $null) },
        @{ Name = 'minutes'; Limit = $MinuteLimit; Value = (Get-Fact $Metrics 'minutes' $null) }
    )) {
        if ($check.Limit -le 0) { continue }
        if ($null -eq $check.Value) { $notMeasured.Add($check.Name); continue }
        if ($check.Value -gt $check.Limit) { $failed.Add($check.Name) }
    }
    return [ordered]@{
        toolCalls = $ToolLimit; totalInputTokens = $InputLimit; minutes = $MinuteLimit
        withinLimits = if ($notMeasured.Count -gt 0) { $null } else { $failed.Count -eq 0 }
        failed = @($failed); notMeasured = @($notMeasured)
    }
}

function Get-TrustKey {
    # Mirrors VerificationStore.ProjectKey: first 16 hex characters of SHA-256 over the upper-cased full path.
    param([Parameter(Mandatory)][string]$ProjectRoot)
    $text = [IO.Path]::GetFullPath($ProjectRoot).TrimEnd('\', '/').ToUpperInvariant()
    $hash = [System.Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($text))
    return [Convert]::ToHexString($hash).Substring(0, 16)
}

# ---------------------------------------------------------------------------------------------------------------
# Self test (offline: no network, desktop, or agents)

function Test-Self {
    $apps = Get-Content -LiteralPath $script:AppsPath -Raw | ConvertFrom-Json
    foreach ($name in 'pkhex', 'dngrep', 'electron') {
        $entry = Get-Fact $apps.apps $name
        if ($null -eq $entry) { throw "apps.json has no app '$name'." }
        if ((Get-Fact $entry 'commit' '') -notmatch '^[0-9a-f]{40}$') { throw "apps.json $name commit is not a full SHA." }
        foreach ($field in 'repoUrl', 'cacheName', 'change', 'changeMarkers', 'expectedDefectFailure') {
            if ($null -eq (Get-Fact $entry $field)) { throw "apps.json $name is missing '$field'." }
        }
        if (-not (Get-TextHit $entry.change @($entry.changeMarkers))) { throw "apps.json $name change prompt does not contain its change markers." }
    }
    $localApp = Get-Fact $apps.apps 'wpf-sample'
    if ((Get-Fact $localApp 'localPath' '') -ne 'samples/verify/wpf') { throw 'wpf-sample localPath is incorrect.' }
    if (@($localApp.excludePaths) -notcontains 'reference') { throw 'wpf-sample must exclude reference/.' }
    if (@($localApp.tasks) -notcontains 'create-skill' -or -not (Get-Fact $localApp 'defectVariant' $null)) { throw 'wpf-sample must enable create-skill and define a defectVariant.' }
    foreach ($name in 'pkhex', 'dngrep', 'electron') { if (@(Get-Fact $apps.apps.$name 'tasks' @()) -contains 'create-skill') { throw "$name must not opt in to create-skill." } }
    if (@($apps.workaroundPatterns).Count -eq 0) { throw 'apps.json lists no workaroundPatterns.' }

    $noSkillsInvocation = New-AgentInvocation 'claude' $null '' 'test-model' '' 'empty-mcp.json'
    if ($noSkillsInvocation.AllowedTools -match '(^|,)Skill(,|$)') { throw 'Claude without skills or a plugin must not receive the Skill tool.' }
    $withSkillInvocation = New-AgentInvocation 'claude' @('skill-dir') '' 'test-model' '' 'empty-mcp.json'
    if ($withSkillInvocation.AllowedTools -notmatch '(^|,)Skill(,|$)') { throw 'Claude with skills must receive the Skill tool.' }
    $pluginRoot = Join-Path ([IO.Path]::GetTempPath()) ("pf-trial-plugin-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $pluginRoot -Force | Out-Null
    try {
        $withPluginInvocation = New-AgentInvocation 'claude' $null $pluginRoot 'test-model' '' 'empty-mcp.json'
        if ($withPluginInvocation.AllowedTools -notmatch '(^|,)Skill(,|$)' -or $withPluginInvocation.Arguments -notcontains '--plugin-dir') { throw 'Claude with a plugin directory must receive Skill and --plugin-dir.' }
    }
    finally { Remove-Item -LiteralPath $pluginRoot -Recurse -Force -ErrorAction SilentlyContinue }
    $codexInvocation = New-AgentInvocation 'codex' $null '' 'gpt-6-luna' 'medium' ''
    if ($codexInvocation.Arguments -notcontains '-' -or ($codexInvocation.Arguments -join ' ') -notmatch 'model_reasoning_effort=medium') { throw 'Codex invocation arguments are incorrect.' }

    # Environment allowlist builder strips forbidden variables and Pointframe PATH entries.
    $source = [System.Collections.Generic.Dictionary[string, string]]::new([StringComparer]::OrdinalIgnoreCase)
    $source['SystemRoot'] = 'C:\Windows'
    $source['TEMP'] = 'C:\Temp'
    $source['POINTFRAME_MCP_EXECUTABLE'] = 'C:\old\Pointframe.Mcp.exe'
    $source['ELECTRON_RUN_AS_NODE'] = '1'
    $source['VSCODE_PID'] = '42'
    $source['CLAUDECODE'] = '1'
    $source['Path'] = 'C:\Windows;C:\Users\x\AppData\Local\Programs\Pointframe.Cli;C:\Git\cmd;;'
    $built = Get-ChildEnvironment $source @{ TRIAL_EXTRA = 'extra-value' }
    if ($built.ContainsKey('POINTFRAME_MCP_EXECUTABLE') -or $built.ContainsKey('ELECTRON_RUN_AS_NODE')) { throw 'Forbidden variables survived the allowlist.' }
    if ($built.ContainsKey('VSCODE_PID') -or $built.ContainsKey('CLAUDECODE')) { throw 'A variable outside the allowlist survived.' }
    if ($built['SystemRoot'] -ne 'C:\Windows' -or $built['TRIAL_EXTRA'] -ne 'extra-value') { throw 'Allowlisted or extra variables were dropped.' }
    if ($built['Path'] -ne 'C:\Windows;C:\Git\cmd') { throw "PATH was not filtered: $($built['Path'])" }
    $freshRoot = Join-Path ([IO.Path]::GetTempPath()) ('pf-trial-fresh-selftest-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $freshRoot -Force | Out-Null
    try {
        $fresh = Get-ChildEnvironment $source $null $freshRoot
        if ($fresh['LOCALAPPDATA'] -ne [IO.Path]::GetFullPath($freshRoot)) { throw 'Fresh-machine LOCALAPPDATA was not redirected.' }
        if ($fresh['TEMP'] -ne 'C:\Temp' -or $fresh.ContainsKey('TMP')) { throw 'Fresh-machine mode unexpectedly changed TEMP/TMP.' }
        $trialPath = Join-Path $freshRoot 'Programs\Pointframe.Cli'
        $pathValue = 'C:\Windows;%USERPROFILE%\bin;' + $trialPath + ';C:\Git\cmd'
        $registryValue = [pscustomobject]@{ Value = $pathValue; Kind = [Microsoft.Win32.RegistryValueKind]::ExpandString }
        $removedPaths = @(Remove-AddedTrialPathEntries $freshRoot { $registryValue } { param($value, $kind) $script:selfTestWrittenPath = $value; $script:selfTestWrittenKind = $kind })
        if ($removedPaths.Count -ne 1 -or $removedPaths[0] -ne $trialPath) { throw 'PATH cleanup did not select exactly the trial entry.' }
        if ($script:selfTestWrittenPath -ne 'C:\Windows;%USERPROFILE%\bin;C:\Git\cmd' -or $script:selfTestWrittenKind -ne [Microsoft.Win32.RegistryValueKind]::ExpandString) { throw 'PATH cleanup expanded or changed unrelated entries or the registry value kind.' }
        $untouched = $false
        $baselineOnly = [pscustomobject]@{ Value = 'C:\Windows;' + $trialPath; Kind = [Microsoft.Win32.RegistryValueKind]::ExpandString }
        [void](Remove-AddedTrialPathEntries $freshRoot { $baselineOnly } { throw 'Writer must not run when no entry was added.' } $baselineOnly.Value)
        $writerFailed = $false
        try { [void](Remove-AddedTrialPathEntries $freshRoot { $registryValue } { throw 'mock registry write failure' }) } catch { $writerFailed = $true }
        if (-not $writerFailed) { throw 'A throwing PATH writer was not surfaced for invalid-outcome handling.' }
        $cleanupFacts = [ordered]@{ harnessError = 'Fresh-machine PATH cleanup failed: mock registry write failure' }
        if ((Get-TrialOutcome $cleanupFacts).Outcome -ne 'invalid') { throw 'A PATH cleanup failure did not score invalid.' }
        $mockLockReleased = $false
        $mockResultPath = Join-Path $freshRoot 'result.json'
        try { try { throw 'mock registry write failure' } catch { $cleanupFacts.harnessError = $_.Exception.Message } }
        finally { $mockLockReleased = $true }
        $cleanupFacts | ConvertTo-Json | Set-Content -LiteralPath $mockResultPath
        if (-not $mockLockReleased -or -not (Test-Path -LiteralPath $mockResultPath)) { throw 'Cleanup failure did not release the lock and write the result.' }
    }
    finally { Remove-Item -LiteralPath $freshRoot -Recurse -Force -ErrorAction SilentlyContinue }
    $extraRejected = $false
    try { Get-ChildEnvironment $source @{ POINTFRAME_X = '1' } | Out-Null } catch { $extraRejected = $true }
    if (-not $extraRejected) { throw 'A forbidden Extra variable was accepted.' }
    if ((Get-ForbiddenVariableNames @('Path', 'pointframe_cli', 'ELECTRON_RUN_AS_NODE')).Count -ne 2) { throw 'Forbidden-name detection is wrong.' }

    # The probe sees what a real child sees: a poisoned parent must come out clean, a poisoned child must be caught.
    $previous = $env:POINTFRAME_MCP_EXECUTABLE
    $env:POINTFRAME_MCP_EXECUTABLE = 'C:\poison\Pointframe.Mcp.exe'
    try {
        $clean = Get-ChildEnvironment (Get-ParentEnvironment) $null
        $probe = Test-ChildEnvironment $clean
        if ($probe.ProbeExit -ne 0 -or $probe.Seen -eq 0) { throw 'The environment probe did not run.' }
        if ($probe.Forbidden.Count -ne 0 -or $probe.PathLeak) { throw "The probe saw a leak in a clean environment: $($probe.Forbidden -join ', ')" }
        $dirty = Get-ChildEnvironment (Get-ParentEnvironment) $null
        $dirty['POINTFRAME_MCP_EXECUTABLE'] = 'x'
        if ((Test-ChildEnvironment $dirty).Forbidden.Count -ne 1) { throw 'The probe missed a leaked variable.' }
    }
    finally {
        if ($null -ne $previous) { $env:POINTFRAME_MCP_EXECUTABLE = $previous } else { Remove-Item Env:POINTFRAME_MCP_EXECUTABLE -ErrorAction SilentlyContinue }
    }

    # Process selection: PID and start time must both match; descendants and trial-dir images are found.
    $recorded = @(
        [pscustomobject]@{ Pid = 100; Start = '2026-10-07T10:00:00.0000000Z' },
        [pscustomobject]@{ Pid = 200; Start = '2026-10-07T10:00:05.0000000Z' },
        [pscustomobject]@{ Pid = 300; Start = '2026-10-07T10:00:06.0000000Z' }
    )
    $running = @(
        [pscustomobject]@{ Pid = 100; Start = '2026-10-07T10:00:00.0000000Z' },
        [pscustomobject]@{ Pid = 200; Start = '2026-10-07T11:30:00.0000000Z' },
        [pscustomobject]@{ Pid = 999; Start = '2026-10-07T10:00:06.0000000Z' }
    )
    $selected = @(Select-ProcessesToKill $recorded $running)
    if ($selected.Count -ne 1 -or $selected[0].Pid -ne 100) { throw 'Process selection must match PID and start time only.' }
    $table = @(
        [pscustomobject]@{ Pid = 10; ParentPid = 1; Start = '2026-10-07T10:00:00.0000000Z'; Path = 'C:\tools\claude.exe'; Name = 'claude.exe' },
        [pscustomobject]@{ Pid = 11; ParentPid = 10; Start = '2026-10-07T10:00:01.0000000Z'; Path = 'C:\Windows\cmd.exe'; Name = 'cmd.exe' },
        [pscustomobject]@{ Pid = 12; ParentPid = 11; Start = '2026-10-07T10:00:02.0000000Z'; Path = 'C:\trial\project\bin\app.exe'; Name = 'app.exe' },
        [pscustomobject]@{ Pid = 13; ParentPid = 1; Start = '2026-10-07T10:00:03.0000000Z'; Path = 'C:\trial\project\bin\orphan.exe'; Name = 'orphan.exe' },
        [pscustomobject]@{ Pid = 14; ParentPid = 10; Start = '2026-10-07T09:00:00.0000000Z'; Path = 'C:\other.exe'; Name = 'other.exe' },
        [pscustomobject]@{ Pid = 15; ParentPid = 1; Start = '2026-10-07T10:00:04.0000000Z'; Path = 'C:\Windows\notepad.exe'; Name = 'notepad.exe' }
    )
    $found = @(Find-TrialProcesses $table 10 '2026-10-07T10:00:00.0000000Z' @('C:\trial\project'))
    $pids = @($found | ForEach-Object { $_.Pid } | Sort-Object)
    if (($pids -join ',') -ne '10,11,12,13') { throw "Trial process discovery returned: $($pids -join ',')" }

    # Workaround detection and prompt exemption.
    $diff = @'
diff --git a/src/Main.cs b/src/Main.cs
--- a/src/Main.cs
+++ b/src/Main.cs
@@ -1,0 +2,2 @@
+    BeginInvoke(new Action(() => Show()));
+    var ok = 1;
diff --git a/notes.md b/notes.md
--- a/notes.md
+++ b/notes.md
@@ -1,0 +2 @@
+Thread.Sleep is mentioned here
'@
    $hits = @(Get-WorkaroundHits $diff @('BeginInvoke', 'Thread\.Sleep') @('.cs') 'Add a menu item.')
    if ($hits.Count -ne 1 -or $hits[0].file -ne 'src/Main.cs') { throw 'Workaround hits must come from added lines in source files only.' }
    if (@(Get-WorkaroundHits $diff @('BeginInvoke') @('.cs') 'Use BeginInvoke for the handler.').Count -ne 0) { throw 'A pattern the prompt asks for must be exempt.' }

    # Criterion and negative-control checks.
    $verdict = '{"scenarios":[{"id":"s","status":"pass","criteria":[{"id":"C1","text":"The About Trial menu item exists","verdict":"passed"}],"steps":[]}]}' | ConvertFrom-Json
    if (-not (Test-CriterionTiedToChange $verdict @('About Trial'))) { throw 'A tied criterion was not recognized.' }
    if (Test-CriterionTiedToChange $verdict @('Other')) { throw 'An untied criterion was accepted.' }
    $spec = '{"scenarios":[{"id":"s","steps":[{"check":{"kind":"exists","expectFailure":true}}]}]}' | ConvertFrom-Json
    if (-not (Test-NegativeControl $spec $verdict)) { throw 'The negative control was not recognized.' }
    $failed = '{"status":"fail","scenarios":[{"steps":[{"status":"fail","code":"ElementNotFound"}]}]}' | ConvertFrom-Json
    $expected = @{ codes = @('ElementNotFound', 'CheckFailed') }
    if ((Get-DefectResult $failed $expected) -ne 'failed-expected') { throw 'Defect classification (expected failure) is wrong.' }
    if ((Get-DefectResult ('{"status":"fail","gates":[],"scenarios":[]}' | ConvertFrom-Json) $expected) -ne 'failed-other') { throw 'Defect classification (other failure) is wrong.' }
    if ((Get-DefectResult ('{"status":"pass"}' | ConvertFrom-Json) $expected) -ne 'passed') { throw 'Defect classification (pass) is wrong.' }
    if ((Get-DefectResult $null $expected) -ne 'inconclusive') { throw 'Defect classification (none) is wrong.' }

    # Transcript metrics and trust key shape.
    $scratch = Join-Path ([IO.Path]::GetTempPath()) ("pf-trial-selftest-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $scratch -Force | Out-Null
    try {
        $claudeLog = Join-Path $scratch 'claude.jsonl'
        [IO.File]::WriteAllText($claudeLog, '{"type":"assistant","message":{"content":[{"type":"tool_use","id":"tool-1","name":"Read"}]}}' + "`n" + '{"type":"result","num_turns":52,"total_cost_usd":1.5,"usage":{"input_tokens":10,"cache_creation_input_tokens":11,"cache_read_input_tokens":7,"output_tokens":3}}' + "`n")
        $metrics = Get-TranscriptMetrics $claudeLog
        if ($metrics.turns -ne 52 -or $metrics.costUsd -ne 1.5 -or $metrics.outputTokens -ne 3 -or $metrics.toolCalls -ne 1 -or $metrics.cacheCreationInputTokens -ne 11 -or $metrics.totalInputTokens -ne 28) { throw 'Claude transcript metrics are wrong.' }
        $missingLimits = Get-LimitEvaluation ([ordered]@{ toolCalls = 1; totalInputTokens = $null; minutes = $null }) 2 100 10
        if ($null -ne $missingLimits.withinLimits -or @($missingLimits.notMeasured) -notcontains 'totalInputTokens' -or @($missingLimits.notMeasured) -notcontains 'minutes') { throw 'An unmeasured requested limit must be listed and make withinLimits null.' }
        $codexLog = Join-Path $scratch 'codex.jsonl'
        [IO.File]::WriteAllText($codexLog, '{"type":"item.completed","item":{"type":"command_execution"}}' + "`n" + '{"type":"item.completed","item":{"type":"mcp_tool_call"}}' + "`n" + '{"type":"turn.completed","usage":{"input_tokens":12,"cached_input_tokens":4,"output_tokens":6}}' + "`n" + '{"type":"turn.completed","usage":{"input_tokens":8,"cached_input_tokens":2,"output_tokens":3}}' + "`n")
        $codexMetrics = Get-TranscriptMetrics $codexLog 'codex'
        if ($codexMetrics.toolCalls -ne 2 -or $codexMetrics.inputTokens -ne 20 -or $codexMetrics.cachedInputTokens -ne 6 -or $codexMetrics.totalInputTokens -ne 20 -or $codexMetrics.outputTokens -ne 9 -or $codexMetrics.turns -ne 2) { throw 'Codex transcript metrics are wrong.' }
        $codexWithNoiseLog = Join-Path $scratch 'codex-noise.jsonl'
        [IO.File]::WriteAllText($codexWithNoiseLog, '{"type":"turn.completed","usage":{"input_tokens":5,"output_tokens":2}}' + "`nnot json`n" + '{"type":"turn.completed","usage":{"input_tokens":3,"output_tokens":1}}' + "`n")
        $codexWithNoise = Get-TranscriptMetrics $codexWithNoiseLog 'codex'
        if ($codexWithNoise.turns -ne 2 -or $codexWithNoise.inputTokens -ne 8) { throw 'A non-JSON transcript line stopped metrics parsing.' }
        $unparsedLog = Join-Path $scratch 'unparsed.jsonl'
        [IO.File]::WriteAllText($unparsedLog, 'not json' + "`n")
        $unparsedMetrics = Get-TranscriptMetrics $unparsedLog 'codex'
        foreach ($metricName in 'turns', 'toolCalls', 'inputTokens', 'cachedInputTokens', 'outputTokens') { if ($null -ne $unparsedMetrics[$metricName]) { throw "Unparsed transcript metric '$metricName' must be null." } }
        $localClone = Join-Path $scratch 'local-project'
        $localEntry = Get-Fact $apps.apps 'wpf-sample'
        $localCloneResult = New-TrialClone $localEntry $localClone
        if (-not $localCloneResult.Local -or (Test-Path (Join-Path $localClone 'reference')) -or (Test-Path (Join-Path $localClone 'obj')) -or (Test-Path (Join-Path $localClone 'bin'))) { throw 'Local source copy did not exclude reference/, bin/, and obj/.' }

        $skillProject = Join-Path $scratch 'skill-project'
        $featureDir = Join-Path $skillProject '.agents/skills/verify-contacts/features'
        New-Item -ItemType Directory -Path $featureDir -Force | Out-Null
        $skillFile = Join-Path (Split-Path $featureDir -Parent) 'SKILL.md'
        $featureFile = Join-Path $featureDir 'add-contact.md'
        $createFixtures = Get-Content -LiteralPath (Join-Path (Join-Path $script:FixtureDirectory 'create-skill') 'create-skill.json') -Raw | ConvertFrom-Json
        $sampleSpec = '{"scenarios":[{"id":"add-contact","steps":[{"invoke":{"automationId":"addButton"}}]}]}' | ConvertFrom-Json
        foreach ($fixture in $createFixtures.featureCases) {
            Set-Content -LiteralPath $skillFile -Value $fixture.frontmatter -Encoding utf8
            Set-Content -LiteralPath $featureFile -Value $fixture.feature -Encoding utf8
            $valid = (Get-CreateSkillFacts $skillProject $null $sampleSpec).skillValid
            if ($valid -ne $fixture.expectedValid) { throw "Create-skill feature fixture '$($fixture.name)' expected valid=$($fixture.expectedValid), got $valid." }
        }
        foreach ($fixture in $createFixtures.outcomeCases) {
            $createPass = [ordered]@{ harnessError = $null; timedOut = $false; envLeak = @(); pathLeak = $false; skillValid = $true; verifyPassedFresh = $true; desktopCriterionPassed = $true; negativeControl = $true; reportsVerified = $true; withinLimits = $true; leftoverProcesses = 0; defectCheck = @{ result = $fixture.defectResult } }
            foreach ($property in $fixture.facts.PSObject.Properties) { $createPass[$property.Name] = $property.Value }
            $outcome = (Get-CreateSkillOutcome $createPass).Outcome
            if ($outcome -ne $fixture.expectedOutcome) { throw "Create-skill outcome fixture '$($fixture.name)' expected $($fixture.expectedOutcome), got $outcome." }
            $expectedExitCode = Get-Fact $fixture 'expectedExitCode' $null
            if ($null -ne $expectedExitCode -and (Get-ExitCodeForOutcome $outcome) -ne $expectedExitCode) { throw "Create-skill outcome fixture '$($fixture.name)' expected exit $expectedExitCode." }
        }
        $inconclusiveFacts = [ordered]@{ skillValid = $true; verifyPassedFresh = $true; desktopCriterionPassed = $true; negativeControl = $true; reportsVerified = $true; leftoverProcesses = 0; defectCheck = @{ result = 'not-run' } }
        if ((Get-CreateSkillOutcome $inconclusiveFacts).Outcome -ne 'invalid') { throw 'An otherwise passing run with a not-run defect check must be invalid.' }
        $copyFixture = $createFixtures.copy
        $skillSource = Join-Path $scratch $copyFixture.skillName
        New-Item -ItemType Directory -Path $skillSource -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $skillSource $copyFixture.fileName) -Value $copyFixture.content
        $skillCopy = @(Copy-TrialSkills $skillSource $skillProject 'codex')
        if ($skillCopy.Count -ne 1 -or -not (Test-Path (Join-Path $skillProject "$($copyFixture.expectedRelativePath)/$($copyFixture.fileName)")) -or $skillCopy[0].relativePath -ne $copyFixture.expectedRelativePath) { throw 'Skill copy/hash/exclusion metadata fixture failed.' }
        $skillSourceB = Join-Path $scratch 'seed-skill-b'
        New-Item -ItemType Directory -Path $skillSourceB -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $skillSourceB 'SKILL.md') -Value 'seed-b'
        $commaInput = "$skillSource,$skillSourceB"
        $arrayInput = @($skillSource, $skillSourceB)
        $commaCopy = @(Copy-TrialSkills @($commaInput) (Join-Path $scratch 'comma-copy') 'codex')
        $arrayCopy = @(Copy-TrialSkills $arrayInput (Join-Path $scratch 'array-copy') 'codex')
        if ($commaCopy.Count -ne 2 -or $arrayCopy.Count -ne 2) { throw 'Skills input must support comma-separated and array forms.' }
        $verifyGolden = Join-Path $skillProject '.agents/skills/verify-golden'
        $bundled = Join-Path $skillProject '.agents/skills/creator/templates/verify-demo'
        foreach ($fixtureRoot in $verifyGolden, $bundled) {
            New-Item -ItemType Directory -Path (Join-Path $fixtureRoot 'features') -Force | Out-Null
            Set-Content -LiteralPath (Join-Path $fixtureRoot 'SKILL.md') -Value "---`nname: verify-copied`n---" -Encoding utf8
            Set-Content -LiteralPath (Join-Path $fixtureRoot 'features/add.md') -Value "## Sub-features`n## How to get to it (user POV)`n## Driving it with Pointframe`nadd-contact`n## Gotchas" -Encoding utf8
        }
        $copiedSkillPaths = @('.agents/skills/verify-golden', '.agents/skills/creator')
        if ((Get-CreateSkillFacts $skillProject $null $sampleSpec $copiedSkillPaths).skillValid) { throw 'Copied skills and bundled template skills must not count as agent-created skills.' }
        $formatted = Format-RepeatSummary @([pscustomobject]$createFixtures.summary)
        if ($formatted -notmatch 'outcome' -or $formatted -notmatch 'totalInputTokens' -or $formatted -notmatch 'pass-clean') { throw 'Repeat summary formatting fixture failed.' }
    }
    finally { Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue }
    if ((Get-TrustKey 'C:\Some\Project') -notmatch '^[0-9A-F]{16}$' -or (Get-TrustKey 'c:\some\project\') -ne (Get-TrustKey 'C:\Some\Project')) { throw 'Trust key is not case- and slash-insensitive.' }
    if ((Get-VersionNumber '6.7.85+abc') -ne '6.7.85' -or $null -ne (Get-VersionNumber 'none')) { throw 'Version parsing is wrong.' }

    $refusal = Invoke-Captured 'pwsh' @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'trial.ps1'), '-App', 'wpf-sample', '-Agent', 'codex') $script:RepositoryRoot (Get-ChildEnvironment (Get-ParentEnvironment) $null) 30
    if ($refusal.ExitCode -ne 2 -or $refusal.Error -notmatch 'owner approves full access per package') { throw 'Codex trial refusal without -AllowCodexFullAccess is wrong.' }
    $effortRefusal = Invoke-Captured 'pwsh' @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'trial.ps1'), '-App', 'wpf-sample', '-Agent', 'claude', '-AgentEffort', 'high') $script:RepositoryRoot (Get-ChildEnvironment (Get-ParentEnvironment) $null) 30
    if ($effortRefusal.ExitCode -ne 2 -or $effortRefusal.Error -notmatch 'AgentEffort is not supported for Claude') { throw 'Claude -AgentEffort refusal did not happen before trial setup.' }

    # Scoring fixtures.
    $fixtures = @(Get-ChildItem -LiteralPath $script:FixtureDirectory -Filter '*.json' -File | Sort-Object Name)
    if ($fixtures.Count -lt 8) { throw "Expected at least 8 scoring fixtures in $script:FixtureDirectory, found $($fixtures.Count)." }
    $outcomes = @{}
    foreach ($fixture in $fixtures) {
        $case = Get-Content -LiteralPath $fixture.FullName -Raw | ConvertFrom-Json
        $scored = Get-TrialOutcome $case.facts
        if ($scored.Outcome -ne $case.expected.outcome) { throw "$($fixture.Name): expected $($case.expected.outcome), got $($scored.Outcome) ($($scored.Reasons -join '; '))." }
        $needle = Get-Fact $case.expected 'reasonContains' $null
        if ($needle -and (($scored.Reasons -join ' | ') -notlike "*$needle*")) { throw "$($fixture.Name): reasons '$($scored.Reasons -join ' | ')' do not contain '$needle'." }
        $outcomes[$scored.Outcome] = $true
    }
    foreach ($required in 'pass-clean', 'pass-workaround', 'fail', 'timeout', 'invalid') {
        if (-not $outcomes.ContainsKey($required)) { throw "No fixture produces '$required'." }
    }
    return "env allowlist and probe, process selection, workaround and defect classification, metrics, $($fixtures.Count) change scoring fixtures, and create-skill fixtures passed offline."
}

# ---------------------------------------------------------------------------------------------------------------
# Real run

# A LogonUI process can outlive the lock screen by hours, so its presence is not a lock. The input desktop
# refuses DESKTOP_SWITCHDESKTOP while the session is locked, on the secure desktop, or without a session.
function Test-InputDesktopAvailable {
    if (-not ('PointframeInputDesktop' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class PointframeInputDesktop {
    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] public static extern bool CloseDesktop(IntPtr desktop);
}
'@
    }

    $desktop = [PointframeInputDesktop]::OpenInputDesktop(0, $false, 0x0100)
    if ($desktop -eq [IntPtr]::Zero) { return $false }
    [void][PointframeInputDesktop]::CloseDesktop($desktop)
    return $true
}

function Assert-InteractiveDesktop {
    if (-not (Test-InputDesktopAvailable)) {
        throw 'The Windows session is locked or has no interactive desktop. Sign in and rerun this trial on the interactive desktop.'
    }
}

function Enter-DesktopLock {
    Assert-InteractiveDesktop
    try {
        $script:LockStream = [IO.File]::Open($script:DesktopLockPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        $bytes = [Text.Encoding]::UTF8.GetBytes("trial pid=$PID utc=$([DateTime]::UtcNow.ToString('o'))")
        $script:LockStream.Write($bytes, 0, $bytes.Length)
        $script:LockStream.Flush()
    }
    catch [IO.IOException] {
        throw "Desktop lock already exists at '$($script:DesktopLockPath)'. Finish the other desktop run, then retry."
    }
}

function Exit-DesktopLock {
    # Only the run that created the lock file removes it; a run that found another run's lock leaves it alone.
    if ($null -eq $script:LockStream) { return }
    $script:LockStream.Dispose()
    $script:LockStream = $null
    if (Test-Path -LiteralPath $script:DesktopLockPath) { Remove-Item -LiteralPath $script:DesktopLockPath -Force -ErrorAction SilentlyContinue }
}

function Invoke-Git {
    param([string]$WorkingDirectory, [string[]]$Arguments, [switch]$AllowFailure)
    $output = @(& git -c core.longpaths=true -C $WorkingDirectory @Arguments 2>&1 | ForEach-Object { "$_" })
    if ($LASTEXITCODE -ne 0 -and -not $AllowFailure) { throw "git $($Arguments -join ' ') failed (exit $LASTEXITCODE): $($output -join ' ')" }
    return ($output -join "`n")
}

function Copy-Tree {
    param([string]$From, [string]$To)
    & robocopy $From $To /MIR /NFL /NDL /NJH /NJS /NP /R:1 /W:1 | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy $From -> $To failed (exit $LASTEXITCODE)." }
}

function New-TrialClone {
    # Fresh copy per run, from the %TEMP%\pf-pre cache when it holds the pinned commit, otherwise from GitHub.
    param($AppEntry, [string]$Destination)
    $localPath = Get-Fact $AppEntry 'localPath' $null
    if ($localPath) {
        $sourcePath = Join-Path $script:RepositoryRoot $localPath
        if (-not (Test-Path -LiteralPath $sourcePath -PathType Container)) { throw "Local trial source '$sourcePath' does not exist." }
        if (Test-Path $Destination) { Remove-Item -Recurse -Force $Destination }
        New-Item -ItemType Directory -Path $Destination -Force | Out-Null
        $excludes = @('bin', 'obj') + @((Get-Fact $AppEntry 'excludePaths' @()))
        & robocopy $sourcePath $Destination /E /XD @($excludes | ForEach-Object { Join-Path $sourcePath $_ }) /NFL /NDL /NJH /NJS /NP /R:1 /W:1 | Out-Null
        if ($LASTEXITCODE -ge 8) { throw "Could not copy local trial source '$sourcePath' (robocopy exit $LASTEXITCODE)." }
        Invoke-Git $Destination @('init', '-q') | Out-Null
        Invoke-Git $Destination @('config', 'user.email', 'trial@example.invalid') | Out-Null
        Invoke-Git $Destination @('config', 'user.name', 'Pointframe Trial') | Out-Null
        Invoke-Git $Destination @('add', '-A') | Out-Null
        Invoke-Git $Destination @('commit', '-q', '-m', 'Trial baseline') | Out-Null
        $localCommit = (Invoke-Git $Destination @('rev-parse', 'HEAD')).Trim()
        return [pscustomobject]@{ Source = $sourcePath; FromCache = $false; Commit = $localCommit; Local = $true }
    }
    $cache = Join-Path ([IO.Path]::GetTempPath()) "pf-pre\$($AppEntry.cacheName)"
    $source = $AppEntry.repoUrl
    $fromCache = $false
    if (Test-Path (Join-Path $cache '.git')) {
        $has = Invoke-Git $cache @('cat-file', '-t', $AppEntry.commit) -AllowFailure
        if ($has.Trim() -eq 'commit') { $source = $cache; $fromCache = $true }
    }
    if (Test-Path $Destination) { Remove-Item -Recurse -Force $Destination }
    & git -c core.longpaths=true clone -q --no-checkout -c core.longpaths=true $source $Destination 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "git clone from $source failed." }
    Invoke-Git $Destination @('checkout', '-q', '--detach', $AppEntry.commit) | Out-Null
    $head = (Invoke-Git $Destination @('rev-parse', 'HEAD')).Trim()
    if ($head -ne $AppEntry.commit) { throw "Trial clone is at $head, expected $($AppEntry.commit)." }
    return [pscustomobject]@{ Source = $source; FromCache = $fromCache; Commit = $head; Local = $false }
}

function Get-AgentPrompt {
    param($AppEntry, [string]$PinnedVersion, [string]$TaskName = 'change')
    $versionLine = if ($PinnedVersion) { "`nUse Pointframe release v$PinnedVersion (CLI and MCP server)." } else { '' }
    $taskText = if ($TaskName -eq 'create-skill') { 'Create a verification skill for this Windows desktop app so any coding agent can prove its features work, and prove it once.' } else { $AppEntry.change }
    return @"
You are working in this repository: a Windows desktop application. Task: $taskText

Before you finish, the change must be verified on the RUNNING application with Pointframe (a Windows tool that
lets coding agents verify desktop apps), set up so the verification is repeatable and binding for this project.
Pointframe is not installed. Use only its public documentation: https://dimitar-radenkov.github.io/Pointframe/
and https://github.com/dimitar-radenkov/Pointframe (README and docs/cli). Work autonomously; nobody will answer
questions. When done, state whether the verification passed and where its verdict is.$versionLine
"@
}

function Invoke-AgentProcess {
    param(
        [string]$FilePath, [string[]]$Arguments, [string]$WorkingDirectory, $Environment, [string]$Prompt,
        [string]$TranscriptPath, [string[]]$TrialDirectories, [int]$TimeoutSeconds
    )
    $launch = Resolve-LaunchSpec $FilePath $Arguments
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = New-ChildStartInfo $launch.File $launch.Arguments $WorkingDirectory $Environment
    $transcript = [IO.File]::Open($TranscriptPath, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    $errorLog = [IO.File]::Open("$TranscriptPath.stderr", [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    $watch = [Diagnostics.Stopwatch]::StartNew()
    [void]$process.Start()
    $rootPid = $process.Id
    $rootStart = $null
    $copyOut = $process.StandardOutput.BaseStream.CopyToAsync($transcript)
    $copyErr = $process.StandardError.BaseStream.CopyToAsync($errorLog)
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes($Prompt)
    try { $process.StandardInput.BaseStream.Write($bytes, 0, $bytes.Length); $process.StandardInput.Close() } catch { }
    $recorded = @()
    $timedOut = $false
    while (-not $process.HasExited) {
        $table = Get-ProcessTable
        if ($null -eq $rootStart) {
            $self = @($table | Where-Object { $_.Pid -eq $rootPid } | Select-Object -First 1)
            if ($self.Count -gt 0) { $rootStart = $self[0].Start }
        }
        $recorded = @(Find-TrialProcesses $table $rootPid $rootStart $TrialDirectories $recorded)
        if ($watch.Elapsed.TotalSeconds -ge $TimeoutSeconds) { $timedOut = $true; break }
        [void]$process.WaitForExit(2000)
    }
    $recorded = @(Find-TrialProcesses (Get-ProcessTable) $rootPid $rootStart $TrialDirectories $recorded)
    if ($timedOut) {
        # Kill by recorded PID + start time, deepest first.
        [void](Stop-RecordedProcesses $recorded)
        try { [void]$process.WaitForExit(10000) } catch { }
    }
    [void][Threading.Tasks.Task]::WaitAny(@($copyOut), 5000)
    [void][Threading.Tasks.Task]::WaitAny(@($copyErr), 2000)
    $transcript.Dispose(); $errorLog.Dispose()
    $exit = if ($process.HasExited) { $process.ExitCode } else { -1 }
    $process.Dispose()
    return [pscustomobject]@{ ExitCode = $exit; TimedOut = $timedOut; Seconds = $watch.Elapsed.TotalSeconds; Recorded = $recorded }
}

function Find-AgentCli {
    # Every Pointframe CLI the agent could have used, with version and SHA-256. The one whose version equals the
    # verdict's verifierVersion is the one it used.
    param([string]$TrialDirectory, [string]$VerifierVersion, $ChildEnvironment, [switch]$FreshMachine)
    $candidates = [System.Collections.Generic.List[string]]::new()
    $pathEntries = @()
    if ($FreshMachine) { $pathEntries += @(([string](Get-Fact $ChildEnvironment 'Path' '') -split ';')) }
    else { foreach ($scope in 'User', 'Machine') { $pathEntries += @(([Environment]::GetEnvironmentVariable('Path', $scope) -as [string]) -split ';') } }
    foreach ($entry in $pathEntries) {
        if (-not $entry) { continue }
        foreach ($name in 'pointframe.exe', 'Pointframe.Cli.exe') {
            $candidate = Join-Path $entry $name
            if (Test-Path -LiteralPath $candidate -PathType Leaf) { $candidates.Add($candidate) }
        }
    }
    foreach ($candidate in @(
            (Join-Path ([string](Get-Fact $ChildEnvironment 'LOCALAPPDATA' $env:LOCALAPPDATA)) 'Programs\Pointframe.Cli\Pointframe.Cli.exe'),
            (Join-Path ([string](Get-Fact $ChildEnvironment 'LOCALAPPDATA' $env:LOCALAPPDATA)) 'Microsoft\WinGet\Links\pointframe.exe'))) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { $candidates.Add($candidate) }
    }
    foreach ($file in @(Get-ChildItem -LiteralPath $TrialDirectory -Filter 'Pointframe.Cli.exe' -File -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -notmatch '\\(node_modules|\.git)\\' } | Select-Object -First 5)) { $candidates.Add($file.FullName) }
    $seen = @()
    foreach ($path in ($candidates | Select-Object -Unique)) {
        $version = $null
        $run = Invoke-Captured $path @('--version') $null $ChildEnvironment 30
        if ($run.ExitCode -eq 0) { $version = Get-VersionNumber $run.Output }
        $seen += [pscustomobject]@{ path = $path; version = $version; sha256 = (Get-Sha256Hex $path) }
    }
    $chosen = @($seen | Where-Object { $VerifierVersion -and $_.version -eq $VerifierVersion } | Select-Object -First 1)
    if ($chosen.Count -eq 0) { $chosen = @($seen | Select-Object -First 1) }
    return [pscustomobject]@{ Chosen = if ($chosen.Count -gt 0) { $chosen[0] } else { $null }; All = $seen }
}

function Get-McpInfo {
    param($Verdict, $ChildEnvironment, [switch]$FreshMachine)
    $provenance = Get-Fact $Verdict 'provenance' $null
    $path = Get-Fact $provenance 'mcpExecutablePath' $null
    $sha = Get-Fact $provenance 'mcpSha256' $null
    if (-not $path) {
        $state = Join-Path ([string](Get-Fact $ChildEnvironment 'LOCALAPPDATA' $env:LOCALAPPDATA)) 'Programs\Pointframe.Mcp\install-state.json'
        if (Test-Path -LiteralPath $state) {
            $installation = Get-Content -LiteralPath $state -Raw | ConvertFrom-Json
            $path = Get-Fact $installation 'ExecutablePath' (Get-Fact $installation 'executablePath' $null)
        }
    }
    if ($FreshMachine -and $path) {
        $freshRoot = [IO.Path]::GetFullPath([string](Get-Fact $ChildEnvironment 'LOCALAPPDATA' ''))
        if (-not ([IO.Path]::GetFullPath($path)).StartsWith($freshRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { $path = $null }
    }
    $version = $null
    $actualSha = $null
    if ($path -and (Test-Path -LiteralPath $path -PathType Leaf)) {
        $version = Get-VersionNumber ([Diagnostics.FileVersionInfo]::GetVersionInfo($path).ProductVersion)
        if (-not $version) { $version = Get-VersionNumber ([Diagnostics.FileVersionInfo]::GetVersionInfo($path).FileVersion) }
        $actualSha = Get-Sha256Hex $path
    }
    return [pscustomobject]@{ path = $path; version = $version; sha256 = $actualSha; verdictSha256 = $sha }
}

function Read-JsonFile {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    try { return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json } catch { return $null }
}

function Invoke-DefectCheck {
    # In a copy of the final trial directory, put every file outside the preserved Pointframe config back to the
    # pinned commit, then run the same CLI's `verify run`. The check must fail at the missing change.
    param($Config, [string]$TrialDirectory, [string]$DefectDirectory, [string]$Commit, [string]$CliPath, [string]$McpPath, $AppEntry, $ChildEnvironment, [string]$TaskName = 'change', [string[]]$ExtraIgnoredPaths = @())
    $trustFile = $null
    try {
        Copy-Tree $TrialDirectory $DefectDirectory
        $ignored = @(@($Config.ignoredPaths) + $ExtraIgnoredPaths | Select-Object -Unique | ForEach-Object { ":(exclude)$_" })
        Invoke-Git $DefectDirectory @('add', '-A', '-N', '--', '.') -AllowFailure | Out-Null
        $diff = Invoke-Git $DefectDirectory (@('diff', '--no-color', '--unified=0', $Commit, '--', '.') + $ignored) -AllowFailure
        [IO.File]::WriteAllText("$DefectDirectory.agent.diff", $diff)
        $patterns = @($Config.workaroundPatterns) + @($AppEntry.workaroundPatterns)
        $hits = @(Get-WorkaroundHits $diff $patterns @($Config.sourceExtensions) $AppEntry.change)

        $backup = "$DefectDirectory.preserve"
        if (Test-Path $backup) { Remove-Item -Recurse -Force $backup }
        New-Item -ItemType Directory -Path $backup -Force | Out-Null
        foreach ($preserved in $Config.preservePaths) {
            $item = Join-Path $DefectDirectory $preserved
            if (Test-Path -LiteralPath $item) { Copy-Item -LiteralPath $item -Destination (Join-Path $backup $preserved) -Recurse -Force }
        }
        Invoke-Git $DefectDirectory @('reset', '--hard', '-q', $Commit) | Out-Null
        Invoke-Git $DefectDirectory @('clean', '-ffdxq') | Out-Null
        foreach ($preserved in $Config.preservePaths) {
            $saved = Join-Path $backup $preserved
            if (Test-Path -LiteralPath $saved) {
                $target = Join-Path $DefectDirectory $preserved
                if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
                Copy-Item -LiteralPath $saved -Destination $target -Recurse -Force
            }
        }

        if ($TaskName -eq 'create-skill') {
            $variant = Get-Fact $AppEntry 'defectVariant' $null
            if ($null -eq $variant -or -not (Get-Fact $variant 'fileName' $null) -or -not (Get-Fact $variant 'content' $null)) { throw 'create-skill requires defectVariant.fileName and defectVariant.content in apps.json.' }
            [IO.File]::WriteAllText((Join-Path $DefectDirectory $variant.fileName), [string]$variant.content, [Text.UTF8Encoding]::new($false))
        }

        # Gate approval is keyed by the project path; give the copy the same approval the trial directory has.
        $trustRoot = Join-Path ([string](Get-Fact $ChildEnvironment 'LOCALAPPDATA' $env:LOCALAPPDATA)) 'Pointframe\verify\trust'
        $original = Join-Path $trustRoot "$(Get-TrustKey $TrialDirectory).json"
        if (Test-Path -LiteralPath $original) {
            $trust = Get-Content -LiteralPath $original -Raw | ConvertFrom-Json
            foreach ($property in $trust.PSObject.Properties) {
                if ($property.Name -ieq 'ProjectRoot') { $property.Value = [IO.Path]::GetFullPath($DefectDirectory) }
            }
            $trustFile = Join-Path $trustRoot "$(Get-TrustKey $DefectDirectory).json"
            $trust | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $trustFile -Encoding utf8NoBOM
        }

        $arguments = @('verify', 'run')
        if ($McpPath) { $arguments += @('--mcp', $McpPath) }
        $run = Invoke-Captured $CliPath $arguments $DefectDirectory $ChildEnvironment 1500
        $verdict = Read-JsonFile (Join-Path $DefectDirectory 'artifacts\pointframe-verify\verdict.json')
        if ($run.TimedOut) { $verdict = $null }
        $result = Get-DefectResult $verdict $AppEntry.expectedDefectFailure
        $mentions = $false
        if ($null -ne $verdict) { $mentions = Get-TextHit ((($verdict | ConvertTo-Json -Depth 12 -Compress))) @($AppEntry.expectedDefectFailure.mention) }
        return [pscustomobject]@{
            result = $result; exitCode = $run.ExitCode; timedOut = $run.TimedOut
            verdictStatus = if ($null -ne $verdict) { Get-Fact $verdict 'status' $null } else { $null }
            mentionsExpectedElement = $mentions; workaroundHits = $hits; copy = $DefectDirectory
        }
    }
    finally {
        if ($trustFile -and (Test-Path -LiteralPath $trustFile)) { Remove-Item -LiteralPath $trustFile -Force -ErrorAction SilentlyContinue }
        $under = @(Find-TrialProcesses (Get-ProcessTable) 0 $null @($DefectDirectory))
        [void](Stop-RecordedProcesses $under)
    }
}

function Invoke-Trial {
    param([string]$AppName, [string]$AgentName, [string]$PinnedVersion, [string]$OutputDirectory, [string]$RunModel, [string]$RunEffort, [string]$TaskName = 'change', [string[]]$SkillList = @(), [string]$PluginDirectory = '', [switch]$FreshMachine)
    $SkillList = @($SkillList | Where-Object { $_ })
    if ($null -eq $PluginDirectory) { $PluginDirectory = '' }
    $config = Get-Content -LiteralPath $script:AppsPath -Raw | ConvertFrom-Json
    $appEntry = $config.apps.$AppName
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    $trialDirectory = Join-Path $OutputDirectory 'project'
    $defectDirectory = Join-Path $OutputDirectory 'defect'
    $transcriptPath = Join-Path $OutputDirectory "$AppName-$AgentName.jsonl"
    $started = [DateTime]::UtcNow

    $facts = [ordered]@{
        expectedVersion = $null; envLeak = @(); pathLeak = $false; task = $TaskName
        harnessError = $null; timedOut = $false; verifyStatus = 'none'; fresh = $false; criterionTiedToChange = $false
        negativeControl = $false; reportsVerified = $false; leftoverProcesses = 0
        defectCheck = [ordered]@{ result = 'not-run' }; workaroundHits = @()
        cliVersion = $null; mcpVersion = $null; verifierVersion = $null
    }
    $record = [ordered]@{
        schemaVersion = 1; app = $AppName; agent = $AgentName; task = $TaskName; startedUtc = $started.ToString('o')
        outDirectory = $OutputDirectory; trialDirectory = $trialDirectory; transcript = $transcriptPath
        isolation = [ordered]@{ freshMachine = ($FreshMachine -eq $true) }; clone = $null; agentRun = $null; metrics = $null; pointframe = $null
        verify = $null; leftover = @(); notes = @()
    }

    try {
        $freshLocalAppData = $null
        $userPathBefore = [string](Read-UserPathRegistryValue).Value
        if ($FreshMachine) {
            $freshLocalAppData = Join-Path $OutputDirectory ('fresh-machine-' + [guid]::NewGuid().ToString('N') + '\LocalAppData')
            New-Item -ItemType Directory -Path $freshLocalAppData -Force | Out-Null
            $record.isolation.freshLocalAppData = $freshLocalAppData
            $record.notes += 'LOCALAPPDATA is isolated; agent CLI and dotnet/gh local caches or update state may be unavailable. Claude and Codex remain configured through their normal USERPROFILE/CODEX_HOME locations.'
            # TEMP/TMP are not redirected: the set-based probe does not need them, and tools retain their normal writable temp location.
        }
        Assert-InteractiveDesktop
        $stale = @(Get-ProcessTable | Where-Object { $_.Path -and $_.Path.StartsWith((Join-Path ([IO.Path]::GetTempPath()) 'pf-trials-out'), [StringComparison]::OrdinalIgnoreCase) })
        if ($stale.Count -gt 0) { throw "Lingering processes from an earlier trial are still running: $(($stale | ForEach-Object { "$($_.Name) pid $($_.Pid)" }) -join ', '). Stop them and rerun." }

        $expected = $PinnedVersion
        if (-not $expected) {
            $release = & gh release view --repo $script:ReleaseRepository --json tagName 2>&1
            if ($LASTEXITCODE -ne 0) { throw "Could not resolve the latest release: $release" }
            $expected = (($release -join "`n") | ConvertFrom-Json).tagName -replace '^v', ''
        }
        if ($expected -notmatch '^\d+\.\d+\.\d+$') { throw "Version '$expected' is not a semantic x.y.z release version." }
        $facts.expectedVersion = $expected
        $record.expectedVersion = $expected

        Enter-DesktopLock

        # Isolation: allowlisted environment, probed before the agent starts.
        $extra = [ordered]@{}
        $parent = Get-ParentEnvironment
        $childEnvironment = Get-ChildEnvironment $parent $extra $freshLocalAppData
        $probe = Test-ChildEnvironment $childEnvironment
        $facts.envLeak = @($probe.Forbidden)
        $facts.pathLeak = $probe.PathLeak
        $record.isolation.allowlist = @($script:AllowedEnvironment)
        $record.isolation.removedFromParent = @(Get-ForbiddenVariableNames @($parent.Keys))
        $record.isolation.probe = [ordered]@{ forbiddenVisible = @($probe.Forbidden); pointframeOnPath = $probe.PathLeak; variablesSeen = $probe.Seen; exitCode = $probe.ProbeExit }
        if ($probe.ProbeExit -ne 0) { throw "The environment probe failed (exit $($probe.ProbeExit))." }
        if ($facts.envLeak.Count -gt 0 -or $facts.pathLeak) { throw 'The agent environment is not clean; the run is invalid.' }

        $clone = New-TrialClone $appEntry $trialDirectory
        $record.clone = $clone
        if (@(Get-Fact $appEntry 'tasks' @('change')) -notcontains $TaskName) { throw "App '$AppName' does not opt in to task '$TaskName'." }
        $prompt = Get-AgentPrompt $appEntry $PinnedVersion $TaskName
        $record.prompt = $prompt
        $copiedSkills = @(Copy-TrialSkills $SkillList $trialDirectory $AgentName)
        $record.skillSetup = $copiedSkills
        $ignoredSkillPaths = @($copiedSkills | ForEach-Object { $_.relativePath })

        if ($AgentName -eq 'claude') {
            $emptyMcp = Join-Path $OutputDirectory 'empty-mcp.json'
            [IO.File]::WriteAllText($emptyMcp, '{"mcpServers":{}}')
            $exe = (Get-Command claude -ErrorAction Stop | Select-Object -First 1).Source
            if ($RunEffort) { throw '-AgentEffort is not supported for Claude trials.' }
            $invocation = New-AgentInvocation $AgentName $SkillList $PluginDirectory $RunModel $RunEffort $emptyMcp
            $agentArguments = $invocation.Arguments
            $record.isolation.claude = [ordered]@{ model = $invocation.Model; settingSources = 'project'; strictMcpConfig = $true; mcpConfig = $emptyMcp; maxBudgetUsd = 3 }
            if ($PluginDirectory) { $record.isolation.claude.pluginDirectory = (Resolve-Path -LiteralPath $PluginDirectory -ErrorAction Stop).Path }
        }
        else {
            $exe = (Get-Command codex -ErrorAction Stop | Select-Object -First 1).Source
            $invocation = New-AgentInvocation $AgentName $SkillList $PluginDirectory $RunModel $RunEffort ''
            $agentArguments = $invocation.Arguments
            $record.isolation.codex = [ordered]@{ model = $invocation.Model; effort = $invocation.Effort; fullAccess = $true }
        }
        $record.isolation.agentExecutable = $exe

        $run = Invoke-AgentProcess $exe $agentArguments $trialDirectory $childEnvironment $prompt $transcriptPath @($trialDirectory, $defectDirectory) ($TimeoutMinutes * 60)
        $facts.timedOut = $run.TimedOut
        $record.agentRun = [ordered]@{ exitCode = $run.ExitCode; timedOut = $run.TimedOut; minutes = [Math]::Round($run.Seconds / 60, 2); timeoutMinutes = $TimeoutMinutes; processesRecorded = @($run.Recorded).Count }
        $record.metrics = Get-TranscriptMetrics $transcriptPath $AgentName
        $record.metrics.minutes = [Math]::Round($run.Seconds / 60, 2)
        $limits = Get-LimitEvaluation $record.metrics $MaxToolCalls $MaxInputTokens $MaxMinutes
        if ($TaskName -eq 'create-skill' -or $MaxToolCalls -gt 0 -or $MaxInputTokens -gt 0 -or $MaxMinutes -gt 0) { $record.limits = $limits }

        # What is still running of what this run started (PID and start time), then kill exactly that.
        $recordedAll = @(Find-TrialProcesses (Get-ProcessTable) 0 $null @($trialDirectory) $run.Recorded)
        $leftover = @(Select-ProcessesToKill $recordedAll (Get-ProcessTable))
        if (-not $run.TimedOut) {
            # dotnet build servers (MSBuild nodes, Roslyn) and their console hosts outlive any `dotnet build` by design;
            # they are still stopped below but do not count as the agent leaving its app running.
            $facts.leftoverProcesses = @($leftover | Where-Object { $script:BuildServerNames -notcontains $_.Name }).Count
            $record.leftover = @($leftover | ForEach-Object { [ordered]@{ pid = $_.Pid; start = $_.Start; name = $_.Name; path = $_.Path } })
        }
        [void](Stop-RecordedProcesses $leftover)

        if (-not $run.TimedOut) {
            # Facts from the agent's verification, read with the allowlisted environment.
            $verdictPath = Join-Path $trialDirectory 'artifacts\pointframe-verify\verdict.json'
            $verdict = Read-JsonFile $verdictPath
            $verifierVersion = if ($null -ne $verdict) { Get-VersionNumber (Get-Fact (Get-Fact $verdict 'provenance' $null) 'verifierVersion' $null) } else { $null }
            $cliInfo = Find-AgentCli $trialDirectory $verifierVersion $childEnvironment -FreshMachine:$FreshMachine
            $mcpInfo = Get-McpInfo $verdict $childEnvironment -FreshMachine:$FreshMachine
            $facts.cliVersion = if ($cliInfo.Chosen) { $cliInfo.Chosen.version } else { $null }
            $facts.mcpVersion = $mcpInfo.version
            $facts.verifierVersion = $verifierVersion
            $record.pointframe = [ordered]@{ cli = $cliInfo.Chosen; allCliCandidates = @($cliInfo.All); mcp = $mcpInfo; verifierVersion = $verifierVersion }

            if ($cliInfo.Chosen) {
                $status = Invoke-Captured $cliInfo.Chosen.path @('verify', 'status') $trialDirectory $childEnvironment 120
                $statusJson = $null
                $start = $status.Output.IndexOf('{')
                if ($start -ge 0) { try { $statusJson = $status.Output.Substring($start) | ConvertFrom-Json } catch { } }
                if ($null -ne $statusJson) {
                    $facts.verifyStatus = [string](Get-Fact $statusJson 'status' 'none')
                    $facts.fresh = (Get-Fact $statusJson 'fresh' $false) -eq $true
                }
                $record.verify = [ordered]@{ statusExitCode = $status.ExitCode; status = $facts.verifyStatus; fresh = $facts.fresh; freshnessReason = if ($null -ne $statusJson) { Get-Fact $statusJson 'freshnessReason' $null } else { $null } }
            }
            else { $record.notes += 'No Pointframe CLI was found after the agent finished.' }

            if ($null -ne $verdict) {
                $specPath = Get-Fact $verdict 'specPath' '.pointframe/verify.json'
                if (-not [IO.Path]::IsPathRooted($specPath)) { $specPath = Join-Path $trialDirectory $specPath }
                $spec = Read-JsonFile $specPath
                $facts.criterionTiedToChange = Test-CriterionTiedToChange $verdict @($appEntry.changeMarkers)
                $facts.negativeControl = Test-NegativeControl $spec $verdict
                $scenarios = @(Get-Fact $verdict 'scenarios' @())
                $facts.reportsVerified = $scenarios.Count -gt 0 -and @($scenarios | Where-Object { (Get-Fact $_ 'proofValid' $false) -ne $true -or -not (Get-Fact $_ 'bundleDirectory' $null) -or -not (Test-Path -LiteralPath (Join-Path (Get-Fact $_ 'bundleDirectory' '') 'report.json')) }).Count -eq 0
                if ($TaskName -eq 'create-skill') {
                    $specPath = Get-Fact $verdict 'specPath' '.pointframe/verify.json'
                    if (-not [IO.Path]::IsPathRooted($specPath)) { $specPath = Join-Path $trialDirectory $specPath }
                    $createFacts = Get-CreateSkillFacts $trialDirectory $verdict (Read-JsonFile $specPath) $ignoredSkillPaths
                    foreach ($key in $createFacts.Keys) { $facts[$key] = $createFacts[$key] }
                    $facts.verifyPassedFresh = $facts.verifyStatus -eq 'pass' -and $facts.fresh
                    $facts.negativeControl = Test-NegativeControl (Read-JsonFile $specPath) $verdict
                }
            }

            # The defect check only matters for a run that otherwise passed.
            if ($facts.verifyStatus -eq 'pass' -and $facts.fresh -and $cliInfo.Chosen) {
                $defect = Invoke-DefectCheck $config $trialDirectory $defectDirectory $clone.Commit $cliInfo.Chosen.path $mcpInfo.path $appEntry $childEnvironment $TaskName $ignoredSkillPaths
                $facts.defectCheck = [ordered]@{ result = $defect.result }
                $facts.workaroundHits = @($defect.workaroundHits)
                $record.defectCheck = $defect
            }
        }
    }
    catch { $facts.harnessError = $_.Exception.Message }
    finally {
        try {
            if ($FreshMachine -and $freshLocalAppData) {
                $removedUserPath = @(Remove-AddedTrialPathEntries $freshLocalAppData { Read-UserPathRegistryValue } { param($value, $kind) Write-UserPathRegistryValue $value $kind } $userPathBefore)
                $record.isolation.removedUserPathEntries = $removedUserPath
            }
        }
        catch {
            $record.isolation.pathCleanupError = $_.Exception.Message
            $facts.harnessError = "Fresh-machine PATH cleanup failed: $($_.Exception.Message)"
        }
        finally {
            try { Exit-DesktopLock }
            catch {
                if (-not $facts.harnessError) { $facts.harnessError = "Desktop lock release failed: $($_.Exception.Message)" }
                $record.isolation.lockReleaseError = $_.Exception.Message
            }
        }
    }

    $scored = if ($TaskName -eq 'create-skill') { Get-CreateSkillOutcome $facts } else { Get-TrialOutcome $facts }
    $record.outcome = $scored.Outcome
    $record.reasons = @($scored.Reasons)
    if ($TaskName -eq 'create-skill') {
        $gateReasons = @($scored.Reasons)
        $withinLimits = Get-Fact (Get-Fact $record 'limits' $null) 'withinLimits' $null
        if ($withinLimits -ne $true) {
            $limitInfo = Get-Fact $record 'limits' $null
            $failedLimits = @(Get-Fact $limitInfo 'failed' @())
            $unmeasuredLimits = @(Get-Fact $limitInfo 'notMeasured' @())
            if ($failedLimits.Count -gt 0) { $gateReasons += "limits exceeded: $($failedLimits -join ', ')" }
            if ($unmeasuredLimits.Count -gt 0) { $gateReasons += "limits not measured: $($unmeasuredLimits -join ', ')" }
        }
        $record.gate = [ordered]@{ result = if ($scored.Outcome -eq 'pass-clean' -and $withinLimits -eq $true) { 'pass' } else { 'fail' }; reasons = @($gateReasons) }
    }
    $record.facts = $facts
    $record.finishedUtc = [DateTime]::UtcNow.ToString('o')
    $record | ConvertTo-Json -Depth 14 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'result.json') -Encoding utf8NoBOM

    $metrics = $record.metrics
    $minutes = if ($null -ne $record.agentRun) { $record.agentRun.minutes } else { $null }
    $turns = if ($null -ne $metrics) { $metrics.turns } else { $null }
    $cost = if ($null -ne $metrics -and $null -ne $metrics.costUsd) { ' cost=$' + $metrics.costUsd } elseif ($null -ne $metrics -and $null -ne $metrics.totalInputTokens) { " tokens=$($metrics.totalInputTokens)in/$($metrics.outputTokens)out" } else { '' }
    $why = if ($scored.Reasons.Count -gt 0) { " [$($scored.Reasons -join '; ')]" } else { '' }
    $toolCalls = if ($null -ne $metrics) { $metrics.toolCalls } else { $null }
    $totalInputTokens = if ($null -ne $metrics) { $metrics.totalInputTokens } else { $null }
    $measured = if ($null -ne $toolCalls -or $null -ne $totalInputTokens) { " toolCalls=$toolCalls totalInputTokens=$totalInputTokens" } else { '' }
    $line = "TRIAL $AppName $AgentName v$($facts.expectedVersion): $($scored.Outcome) turns=$turns min=$minutes$measured$cost$why -> $(Join-Path $OutputDirectory 'result.json')"
    Write-Host $line
    return $scored.Outcome
}

function Get-ExitCodeForOutcome {
    param([string]$Outcome)
    switch ($Outcome) { 'pass-clean' { 0 } 'invalid' { 2 } default { 1 } }
}

# ---------------------------------------------------------------------------------------------------------------
# Entry point

if ($MetricsPath) {
    Get-TranscriptMetrics $MetricsPath $Agent | ConvertTo-Json -Depth 5 -Compress | Write-Output
    exit 0
}

if ($SelfTest) {
    try {
        $detail = Test-Self
        Write-Host "PASSED: trial harness self-test: $detail"
        exit 0
    }
    catch {
        [Console]::Error.WriteLine("FAILED: trial harness self-test: $($_.Exception.Message)")
        exit 1
    }
}

if ($Matrix -and $Agent -eq 'codex') {
    [Console]::Error.WriteLine('Matrix trials are Claude-only; remove -Agent codex or run a single -App.')
    exit 2
}

if ($Task -eq 'create-skill' -and $Matrix) {
    [Console]::Error.WriteLine('-Matrix supports the change task only.')
    exit 2
}

if ($Agent -eq 'codex' -and -not $AllowCodexFullAccess) {
    [Console]::Error.WriteLine('Codex trials require full access to start and inspect GUI apps. The owner approves full access per package; pass -AllowCodexFullAccess for this package.')
    exit 2
}

if ($Agent -eq 'claude' -and $AgentEffort) {
    [Console]::Error.WriteLine('-AgentEffort is not supported for Claude trials.')
    exit 2
}

$stamp = [DateTime]::Now.ToString('yyyyMMdd-HHmmss')
$root = if ($OutDirectory) { $OutDirectory } else { Join-Path ([IO.Path]::GetTempPath()) 'pf-trials-out' }

if ($Matrix) {
    $worst = 0
    $summary = @()
    foreach ($matrixApp in 'pkhex', 'dngrep', 'electron') {
        foreach ($matrixAgent in @('claude')) {
            $directory = if ($OutDirectory) { Join-Path $OutDirectory "$matrixApp-$matrixAgent" } else { Join-Path $root "$stamp-$matrixApp-$matrixAgent" }
            $outcome = Invoke-Trial $matrixApp $matrixAgent $Version $directory $AgentModel $AgentEffort 'change' @() '' $FreshMachine
            $summary += "$matrixApp/$matrixAgent=$outcome"
            $worst = [Math]::Max($worst, (Get-ExitCodeForOutcome $outcome))
        }
    }
    Write-Host "MATRIX: $($summary -join ' ')"
    exit $worst
}

if (-not $App) {
    [Console]::Error.WriteLine('Pass -App, or -Matrix, or -SelfTest.')
    exit 2
}
if ($Agent -ne 'claude' -and $ClaudePluginDir) {
    [Console]::Error.WriteLine('-ClaudePluginDir is supported only for Claude trials.')
    exit 2
}
if ($Task -eq 'create-skill') {
    if ($MaxToolCalls -eq 0) { $MaxToolCalls = 15 }
    if ($MaxInputTokens -eq 0) { $MaxInputTokens = 786000 }
    if ($MaxMinutes -eq 0) { $MaxMinutes = 10 }
}
$directory = if ($OutDirectory) { $OutDirectory } elseif ($Task -eq 'change') { Join-Path $root "$stamp-$App-$Agent" } else { Join-Path $root "$stamp-$App-$Agent-$Task" }
$summary = @()
$worst = 0
for ($index = 1; $index -le $Repeat; $index++) {
    $runDirectory = if ($Repeat -gt 1) { Join-Path $directory ("{0:D2}" -f $index) } else { $directory }
    $outcome = Invoke-Trial $App $Agent $Version $runDirectory $AgentModel $AgentEffort $Task $Skills $ClaudePluginDir $FreshMachine
    $result = Read-JsonFile (Join-Path $runDirectory 'result.json')
    $metrics = Get-Fact $result 'metrics' $null
    $summary += [pscustomobject]@{
        run = $index; outcome = $outcome; toolCalls = Get-Fact $metrics 'toolCalls' $null
        totalInputTokens = Get-Fact $metrics 'totalInputTokens' $null
        minutes = Get-Fact (Get-Fact $result 'agentRun' $null) 'minutes' $null
        gate = Get-Fact (Get-Fact $result 'gate' $null) 'result' $null
    }
    $worst = [Math]::Max($worst, (Get-ExitCodeForOutcome $outcome))
}
if ($Repeat -gt 1 -or $PSBoundParameters.ContainsKey('Repeat')) { Write-Host 'REPEAT SUMMARY'; Write-Host (Format-RepeatSummary $summary) }
exit $worst
