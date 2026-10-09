[CmdletBinding()]
param(
    [string]$Version,
    [string]$WorkDirectory,
    [string]$CandidateDirectory,
    [switch]$BuildCandidate,
    [switch]$PackageOnly,
    [switch]$SelfTest,
    [switch]$KeepWorkDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$script:RepositoryRoot = Split-Path $PSScriptRoot -Parent
$script:Results = [System.Collections.Generic.List[object]]::new()
$script:StartedUtc = [DateTime]::UtcNow
$script:DesktopLockPath = Join-Path ([System.IO.Path]::GetTempPath()) 'pointframe-desktop.lock'
$script:LockStream = $null
$script:WorkDirectory = $WorkDirectory # the same variable as the parameter: never reset it
$script:Failure = $null
$script:EnvironmentBackup = @{}
$script:McpRequestId = 0
$script:StderrTask = $null
$script:Mode = 'release'
$script:Packages = [System.Collections.Generic.List[object]]::new()
$script:CandidateVersion = '0.0.0-candidate'

function Get-Sha256Hex {
    param([Parameter(Mandatory)][string]$Path)
    $stream = [System.IO.File]::OpenRead($Path)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { return ([Convert]::ToHexString($sha.ComputeHash($stream))).ToLowerInvariant() }
    finally { $sha.Dispose(); $stream.Dispose() }
}

function Assert-Checksum {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$ChecksumPath)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Package is missing: $Path" }
    if (-not (Test-Path -LiteralPath $ChecksumPath -PathType Leaf)) { throw "Checksum is missing: $ChecksumPath" }
    $line = (Get-Content -LiteralPath $ChecksumPath -Raw).Trim()
    if ($line -notmatch '^([0-9a-fA-F]{64})(?:\s+\*?(.+))?$') { throw "Invalid SHA-256 file: $ChecksumPath" }
    $expected = $Matches[1].ToLowerInvariant()
    if ($Matches[2] -and [IO.Path]::GetFileName($Matches[2]) -ne [IO.Path]::GetFileName($Path)) {
        throw "Checksum file names '$($Matches[2])', expected '$([IO.Path]::GetFileName($Path))'."
    }
    $actual = Get-Sha256Hex -Path $Path
    if ($actual -ne $expected) { throw "SHA-256 mismatch for $([IO.Path]::GetFileName($Path)): expected $expected, got $actual." }
    return $actual
}

function New-VerifySpec {
    param([Parameter(Mandatory)][string]$AppRelativePath)
    return [ordered]@{
        schemaVersion = 1
        app = [ordered]@{ id = 'fixture'; executable = $AppRelativePath }
        gates = @()
        scenarios = @([ordered]@{
            id = 'released-smoke'
            criteria = @(
                'The fixture save button exists.'
                'The fixture modal opens and shows Modal OK.'
                'The fixture modal closes.'
            )
            steps = @(
                [ordered]@{ check = [ordered]@{ kind = 'exists'; automationId = 'saveButton'; criterion = 'C1' } }
                [ordered]@{ invoke = [ordered]@{ automationId = 'openModalButton' } }
                [ordered]@{ check = [ordered]@{ kind = 'exists'; role = 'Text'; name = 'Modal OK'; criterion = 'C2' } }
                [ordered]@{ invoke = [ordered]@{ role = 'Button'; name = 'OK' } }
                [ordered]@{ check = [ordered]@{ kind = 'absent'; role = 'Text'; name = 'Modal OK'; criterion = 'C3' } }
                [ordered]@{ invoke = [ordered]@{ automationId = 'saveButton' } }
                [ordered]@{ check = [ordered]@{ kind = 'exists'; automationId = 'noSuchElement'; expectFailure = $true; timeoutSeconds = 1 } }
            )
        })
    }
}

function New-ResultShape {
    param([object[]]$Steps)
    return [ordered]@{
        version = 1
        label = $script:Mode
        packages = @($script:Packages | ForEach-Object { [ordered]@{ name = $_.name; sha256 = $_.sha256 } })
        startedUtc = $script:StartedUtc.ToString('o')
        finishedUtc = [DateTime]::UtcNow.ToString('o')
        steps = @($Steps | ForEach-Object {
            [ordered]@{ name = $_.name; status = $_.status; durationSeconds = [double]$_.durationSeconds; detail = [string]$_.detail }
        })
        status = if (@($Steps | Where-Object status -eq 'failed').Count -gt 0) { 'failed' }
                 elseif (@($Steps | Where-Object status -eq 'skipped').Count -gt 0) { 'passed_with_skips' }
                 else { 'passed' }
    }
}

function Add-Step {
    param([string]$Name, [string]$Status, [double]$DurationSeconds, [string]$Detail)
    $script:Results.Add([pscustomobject]@{
        name = $Name; status = $Status; durationSeconds = [Math]::Round($DurationSeconds, 3); detail = $Detail
    })
}

function Invoke-Step {
    param([Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][scriptblock]$Action)
    $timer = [Diagnostics.Stopwatch]::StartNew()
    try {
        $detail = & $Action
        $timer.Stop()
        $stepStatus = if ([string]$detail -like 'skipped (*') { 'skipped' } else { 'passed' }
        Add-Step $Name $stepStatus $timer.Elapsed.TotalSeconds ([string]$detail)
        Write-Host ("{0} {1}: {2}" -f $(if ($stepStatus -eq 'skipped') { 'SKIP' } else { 'PASS' }), $Name, $detail)
        return $detail
    }
    catch {
        $timer.Stop()
        $message = $_.Exception.Message
        Add-Step $Name 'failed' $timer.Elapsed.TotalSeconds $message
        Write-Host ("FAIL {0}: {1}" -f $Name, $message)
        throw "${Name}: $message"
    }
}

function Invoke-NativeChecked {
    param([Parameter(Mandatory)][string]$FilePath, [Parameter(Mandatory)][string[]]$Arguments, [string]$FailureLabel = '')
    $output = @(& $FilePath @Arguments 2>&1 | ForEach-Object { "$_" })
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        $label = if ($FailureLabel) { $FailureLabel } else { "$FilePath $($Arguments -join ' ')" }
        throw "$label failed (exit $exitCode): $($output -join ' ')"
    }
    return ($output -join "`n")
}

function Read-Verdict {
    # verify run writes the verdict to artifacts/pointframe-verify/verdict.json; its stdout JSON is indented and
    # mixed with progress lines, so the file is the reliable source.
    param([Parameter(Mandatory)][string]$ProjectRoot)
    $path = Join-Path $ProjectRoot 'artifacts/pointframe-verify/verdict.json'
    if (-not (Test-Path $path)) { throw "verify run wrote no verdict at $path." }
    return Get-Content $path -Raw | ConvertFrom-Json
}

function ConvertFrom-CliJson {
    # A command's JSON result is the trailing JSON document of its output, possibly indented over many lines.
    param([Parameter(Mandatory)][string]$Text)
    $start = $Text.IndexOf('{')
    while ($start -ge 0) {
        try { return $Text.Substring($start) | ConvertFrom-Json } catch { }
        $start = $Text.IndexOf("`n{", $start + 1)
        if ($start -ge 0) { $start++ }
    }
    throw "No JSON result in the CLI output: $Text"
}

function Get-OptionalProperty {
    # StrictMode makes reading a missing JSON property an error; MCP responses omit null fields.
    param($Object, [Parameter(Mandatory)][string]$Name)
    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Enter-DesktopLock {
    Assert-InteractiveDesktop
    try {
        $script:LockStream = [IO.File]::Open($script:DesktopLockPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        $bytes = [Text.Encoding]::UTF8.GetBytes("check-released-desktop pid=$PID utc=$([DateTime]::UtcNow.ToString('o'))")
        $script:LockStream.Write($bytes, 0, $bytes.Length)
        $script:LockStream.Flush()
    }
    catch [IO.IOException] {
        throw "Desktop lock already exists at '$($script:DesktopLockPath)'. Finish the other desktop run, then retry."
    }
}

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
        throw 'The Windows session is locked or has no interactive desktop. Sign in and rerun this check on the interactive desktop.'
    }
}

function Exit-DesktopLock {
    if ($null -ne $script:LockStream) { $script:LockStream.Dispose(); $script:LockStream = $null }
    if (Test-Path -LiteralPath $script:DesktopLockPath) { Remove-Item -LiteralPath $script:DesktopLockPath -Force -ErrorAction SilentlyContinue }
}

function Resolve-RunMode {
    # release: download the published packages. candidate: take packages from a directory or build them from this checkout.
    param([string]$Version, [string]$CandidateDirectory, [bool]$BuildCandidate, [bool]$PackageOnly)
    if ($CandidateDirectory -and $BuildCandidate) { throw '-CandidateDirectory and -BuildCandidate cannot be combined.' }
    $candidate = [bool]$CandidateDirectory -or $BuildCandidate
    if ($candidate -and $Version) { throw '-Version selects a published release; it cannot be combined with -CandidateDirectory or -BuildCandidate.' }
    if ($PackageOnly -and -not $candidate) { throw '-PackageOnly applies to candidate runs (-CandidateDirectory or -BuildCandidate).' }
    if ($BuildCandidate) { return 'build-candidate' }
    if ($CandidateDirectory) { return 'candidate-directory' }
    return 'release'
}

function Get-FfmpegPath {
    # $env:FFMPEG_PATH wins, then ffmpeg on PATH; the MCP package cannot be built without it.
    param([string]$EnvironmentValue, [string]$CommandSource)
    if ($EnvironmentValue) {
        if (-not (Test-Path -LiteralPath $EnvironmentValue -PathType Leaf)) { throw "FFMPEG_PATH points to '$EnvironmentValue', which does not exist." }
        return $EnvironmentValue
    }
    if ($CommandSource) { return $CommandSource }
    throw 'ffmpeg was not found. The MCP package bundles ffmpeg.exe: set FFMPEG_PATH to an ffmpeg.exe or put ffmpeg on PATH.'
}

function Find-CandidatePackage {
    param([Parameter(Mandatory)][string]$Directory, [Parameter(Mandatory)][string]$Filter)
    if (-not (Test-Path -LiteralPath $Directory -PathType Container)) { throw "Candidate directory not found: $Directory" }
    $found = @(Get-ChildItem -LiteralPath $Directory -Filter $Filter -File)
    if ($found.Count -eq 0) { throw "No '$Filter' in candidate directory $Directory." }
    if ($found.Count -gt 1) { throw "Several '$Filter' in candidate directory ${Directory}: $(($found | ForEach-Object Name) -join ', '). Keep one." }
    return $found[0].FullName
}

function Install-Packages {
    # Extracts the CLI zip and the MCPB bundle (a zip) and locates both executables.
    param([Parameter(Mandatory)][string]$CliPackage, [Parameter(Mandatory)][string]$McpPackage, [Parameter(Mandatory)][string]$Description)
    $cliDirectory = Join-Path $script:WorkDirectory 'released-cli'
    $mcpDirectory = Join-Path $script:WorkDirectory 'released-mcp'
    Expand-Archive -LiteralPath $CliPackage -DestinationPath $cliDirectory -Force
    $mcpZip = Join-Path $script:WorkDirectory 'mcpb.zip'
    Copy-Item -LiteralPath $McpPackage -Destination $mcpZip
    Expand-Archive -LiteralPath $mcpZip -DestinationPath $mcpDirectory -Force
    Remove-Item -LiteralPath $mcpZip -Force
    $script:CliExe = Join-Path $cliDirectory 'Pointframe.Cli.exe'
    $script:McpExe = Join-Path $mcpDirectory 'Pointframe.Mcp.exe'
    if (-not (Test-Path $script:CliExe -PathType Leaf)) { throw "CLI executable not found in $Description." }
    if (-not (Test-Path $script:McpExe -PathType Leaf)) { throw "MCP executable not found in $Description." }
}

function Register-Package {
    param([Parameter(Mandatory)][string]$Path)
    $hash = Get-Sha256Hex -Path $Path
    $script:Packages.Add([pscustomobject]@{ name = [IO.Path]::GetFileName($Path); sha256 = $hash })
    return $hash
}

function Build-CandidatePackages {
    # Builds both packages from this checkout with the packaging scripts and copies them into a fresh directory.
    # The version is the fixed 0.0.0-candidate: it never collides with a release and needs neither git history nor nbgv.
    param([Parameter(Mandatory)][string]$Destination)
    $command = Get-Command ffmpeg -ErrorAction SilentlyContinue | Select-Object -First 1
    $commandSource = if ($command) { $command.Source } else { '' }
    $ffmpeg = Get-FfmpegPath $env:FFMPEG_PATH $commandSource
    $version = $script:CandidateVersion
    $packaging = Join-Path $script:RepositoryRoot 'packaging'
    Invoke-NativeChecked 'pwsh' @('-NoProfile', '-File', (Join-Path $packaging 'build-cli-package.ps1'), '-Version', $version, '-FfmpegPath', $ffmpeg) 'Build candidate CLI package' | Out-Null
    Invoke-NativeChecked 'pwsh' @('-NoProfile', '-File', (Join-Path $packaging 'build-mcp-package.ps1'), '-Version', $version, '-FfmpegPath', $ffmpeg) 'Build candidate MCP package' | Out-Null
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    foreach ($name in @("Pointframe.Cli-$version-win-x64.zip", "Pointframe.Mcp-$version-win-x64.mcpb")) {
        $built = Join-Path $packaging "output/$name"
        if (-not (Test-Path -LiteralPath $built -PathType Leaf)) { throw "The packaging script did not produce $built." }
        Copy-Item -LiteralPath $built -Destination $Destination -Force
    }
}

function Test-Self {
    $scratch = Join-Path ([IO.Path]::GetTempPath()) ("pointframe-released-desktop-selftest-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $scratch -Force | Out-Null
    try {
        $sample = Join-Path $scratch 'package.bin'
        [IO.File]::WriteAllBytes($sample, [Text.Encoding]::UTF8.GetBytes('released-artifact-self-test'))
        $hash = Get-Sha256Hex $sample
        Set-Content -LiteralPath "$sample.sha256" -Value "$hash  package.bin" -NoNewline -Encoding ascii
        if ((Assert-Checksum $sample "$sample.sha256") -ne $hash) { throw 'Matching hash did not return the expected digest.' }
        Set-Content -LiteralPath "$sample.sha256" -Value (('0' * 64) + '  package.bin') -NoNewline -Encoding ascii
        $mismatchRejected = $false
        try { Assert-Checksum $sample "$sample.sha256" | Out-Null } catch { $mismatchRejected = $_.Exception.Message -like '*SHA-256 mismatch*' }
        if (-not $mismatchRejected) { throw 'Mismatched hash was not rejected with a clear mismatch.' }

        $spec = New-VerifySpec 'app/Pointframe.DesktopTestFixture.exe'
        $json = $spec | ConvertTo-Json -Depth 20
        $parsed = $json | ConvertFrom-Json
        $kinds = @($parsed.scenarios[0].steps | ForEach-Object { @($_.PSObject.Properties.Name)[0] })
        if (($kinds -join ',') -ne 'check,invoke,check,invoke,check,invoke,check') { throw "Unexpected v1 step kinds: $($kinds -join ', ')." }
        if ($parsed.app.executable -ne 'app/Pointframe.DesktopTestFixture.exe' -or $parsed.gates.Count -ne 0) { throw 'verify.json app or gates did not round-trip.' }
        $shape = New-ResultShape @([pscustomobject]@{ name = 'self-test'; status = 'passed'; durationSeconds = 0.1; detail = 'ok' })
        $roundTrip = ($shape | ConvertTo-Json -Depth 8) | ConvertFrom-Json
        if ($roundTrip.steps.Count -ne 1 -or $roundTrip.steps[0].status -ne 'passed' -or $roundTrip.steps[0].durationSeconds -ne 0.1 -or -not $roundTrip.finishedUtc) { throw 'result.json shape is invalid.' }

        $modeCases = @(
            @{ Version = ''; Directory = ''; Build = $false; Only = $false; Expected = 'release' }
            @{ Version = '6.7.1'; Directory = ''; Build = $false; Only = $false; Expected = 'release' }
            @{ Version = ''; Directory = 'C:\pkgs'; Build = $false; Only = $false; Expected = 'candidate-directory' }
            @{ Version = ''; Directory = 'C:\pkgs'; Build = $false; Only = $true; Expected = 'candidate-directory' }
            @{ Version = ''; Directory = ''; Build = $true; Only = $false; Expected = 'build-candidate' }
            @{ Version = ''; Directory = ''; Build = $true; Only = $true; Expected = 'build-candidate' }
        )
        foreach ($case in $modeCases) {
            $mode = Resolve-RunMode $case.Version $case.Directory $case.Build $case.Only
            if ($mode -ne $case.Expected) { throw "Resolve-RunMode returned '$mode', expected '$($case.Expected)'." }
        }
        $badModeCases = @(
            @{ Version = ''; Directory = 'C:\pkgs'; Build = $true; Only = $false; Message = 'cannot be combined' }
            @{ Version = '6.7.1'; Directory = 'C:\pkgs'; Build = $false; Only = $false; Message = '-Version' }
            @{ Version = '6.7.1'; Directory = ''; Build = $true; Only = $false; Message = '-Version' }
            @{ Version = ''; Directory = ''; Build = $false; Only = $true; Message = '-PackageOnly' }
        )
        foreach ($case in $badModeCases) {
            $rejected = $false
            try { Resolve-RunMode $case.Version $case.Directory $case.Build $case.Only | Out-Null } catch { $rejected = $_.Exception.Message -like "*$($case.Message)*" }
            if (-not $rejected) { throw "Resolve-RunMode did not reject a bad argument combination with '$($case.Message)'." }
        }

        $existingFfmpeg = Join-Path $scratch 'ffmpeg.exe'
        [IO.File]::WriteAllBytes($existingFfmpeg, [byte[]](1, 2, 3))
        if ((Get-FfmpegPath $existingFfmpeg 'C:\on\path\ffmpeg.exe') -ne $existingFfmpeg) { throw 'FFMPEG_PATH did not win over PATH.' }
        if ((Get-FfmpegPath '' 'C:\on\path\ffmpeg.exe') -ne 'C:\on\path\ffmpeg.exe') { throw 'ffmpeg on PATH was not used.' }
        $noFfmpeg = $false
        try { Get-FfmpegPath '' '' | Out-Null } catch { $noFfmpeg = $_.Exception.Message -like '*FFMPEG_PATH*' }
        if (-not $noFfmpeg) { throw 'A missing ffmpeg was not reported clearly.' }

        $candidates = Join-Path $scratch 'candidates'
        New-Item -ItemType Directory -Path $candidates -Force | Out-Null
        $none = $false
        try { Find-CandidatePackage $candidates 'Pointframe.Cli-*-win-x64.zip' | Out-Null } catch { $none = $_.Exception.Message -like 'No *' }
        if (-not $none) { throw 'An empty candidate directory was not reported.' }
        [IO.File]::WriteAllBytes((Join-Path $candidates 'Pointframe.Cli-win-x64.zip'), [byte[]](1))
        [IO.File]::WriteAllBytes((Join-Path $candidates 'Pointframe.Cli-0.0.0-candidate-win-x64.zip'), [byte[]](1))
        $picked = Split-Path (Find-CandidatePackage $candidates 'Pointframe.Cli-*-win-x64.zip') -Leaf
        if ($picked -ne 'Pointframe.Cli-0.0.0-candidate-win-x64.zip') { throw "The stable alias was picked as the candidate: $picked." }
        [IO.File]::WriteAllBytes((Join-Path $candidates 'Pointframe.Cli-0.0.1-candidate-win-x64.zip'), [byte[]](1))
        $ambiguous = $false
        try { Find-CandidatePackage $candidates 'Pointframe.Cli-*-win-x64.zip' | Out-Null } catch { $ambiguous = $_.Exception.Message -like 'Several *' }
        if (-not $ambiguous) { throw 'Two candidate packages were not reported as ambiguous.' }

        $script:Mode = 'candidate'
        $script:Packages.Add([pscustomobject]@{ name = 'pkg.zip'; sha256 = ('a' * 64) })
        try {
            $candidateShape = (New-ResultShape @([pscustomobject]@{ name = 'self-test'; status = 'passed'; durationSeconds = 0.1; detail = 'ok' }) | ConvertTo-Json -Depth 8) | ConvertFrom-Json
        }
        finally {
            $script:Packages.Clear()
            $script:Mode = 'release'
        }
        if ($candidateShape.label -ne 'candidate' -or @($candidateShape.packages).Count -ne 1 -or $candidateShape.packages[0].sha256 -ne ('a' * 64)) { throw 'result.json did not carry the candidate label and package hashes.' }
        return 'hash match/mismatch, verify.json v1 steps, result.json shape, candidate argument handling, ffmpeg resolution, and candidate package lookup passed offline.'
    }
    finally { Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue }
}

function Start-McpClient {
    param([string]$Executable, [string[]]$Arguments)
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $Executable
    $info.UseShellExecute = $false
    $info.RedirectStandardInput = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.StandardInputEncoding = [Text.UTF8Encoding]::new($false)
    $info.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    $info.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
    foreach ($argument in $Arguments) { [void]$info.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $info
    if (-not $process.Start()) { throw "Could not start released MCP server '$Executable'." }
    $script:StartedProcesses.Add($process) | Out-Null
    # Drain stderr from the start (an unread pipe blocks the server). Not with an event handler: a PowerShell
    # script block run on a thread-pool thread has no runspace and crashes the whole process.
    $script:StderrTask = $process.StandardError.ReadToEndAsync()
    return $process
}

function Send-McpRequest {
    param([Diagnostics.Process]$Process, [string]$Method, [object]$Params = @{})
    $script:McpRequestId++
    $id = $script:McpRequestId
    $request = @{ jsonrpc = '2.0'; id = $id; method = $Method; params = $Params } | ConvertTo-Json -Depth 30 -Compress
    $Process.StandardInput.WriteLine($request)
    $Process.StandardInput.Flush()
    while ($true) {
        if ($Process.HasExited) { throw "Released MCP server exited before replying to '$Method' (exit $($Process.ExitCode))." }
        $lineTask = $Process.StandardOutput.ReadLineAsync()
        if (-not $lineTask.Wait([TimeSpan]::FromSeconds(60))) { throw "Timed out waiting for released MCP response to '$Method'." }
        $line = $lineTask.Result
        if ($null -eq $line) { throw "Released MCP server closed stdout during '$Method'." }
        $message = $line | ConvertFrom-Json
        if ($message.id -eq $id) {
            $messageError = Get-OptionalProperty $message 'error'
            if ($messageError) { throw "MCP '$Method' returned error $(Get-OptionalProperty $messageError 'code'): $(Get-OptionalProperty $messageError 'message')" }
            return $message.result
        }
    }
}

function Invoke-McpTool {
    param([Diagnostics.Process]$Process, [string]$Name, [hashtable]$Arguments)
    $result = Send-McpRequest $Process 'tools/call' @{ name = $Name; arguments = $Arguments }
    if (Get-OptionalProperty $result 'isError') { throw "MCP tool '$Name' failed: $(($result.content | ForEach-Object text) -join ' ')" }
    $structured = Get-OptionalProperty $result 'structuredContent'
    if ($structured) { return $structured }
    foreach ($block in $result.content) {
        if ($block.text) {
            try { return ($block.text | ConvertFrom-Json) } catch { }
        }
    }
    return $result
}

function Stop-TrackedProcess {
    param([Diagnostics.Process]$Process)
    if ($null -ne $Process -and -not $Process.HasExited) {
        try { $Process.StandardInput.Close() } catch { }
        if (-not $Process.WaitForExit(5000)) { $Process.Kill($true); $Process.WaitForExit(5000) }
    }
    if ($null -ne $Process) { $Process.Dispose() }
}

function Stop-WorkProcesses {
    if ($null -eq $script:WorkDirectory -or -not (Test-Path $script:WorkDirectory)) { return }
    $prefix = [IO.Path]::GetFullPath($script:WorkDirectory).TrimEnd('\') + '\'
    foreach ($process in @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object {
        $_.ExecutablePath -and ([IO.Path]::GetFullPath($_.ExecutablePath).StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase))
    })) {
        Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue
    }
    foreach ($process in @($script:StartedProcesses)) { try { Stop-TrackedProcess $process } catch { } }
}

$script:StartedProcesses = [Collections.Generic.List[Diagnostics.Process]]::new()

if ($SelfTest) {
    try {
        if ($WorkDirectory) {
            $script:WorkDirectory = [IO.Path]::GetFullPath($WorkDirectory)
            New-Item -ItemType Directory -Path $script:WorkDirectory -Force | Out-Null
        }
        $detail = Invoke-Step 'offline self-test' { Test-Self }
    }
    catch { $script:Failure = $_.Exception.Message }
}
else {
    try {
        if (-not $WorkDirectory) { $WorkDirectory = Join-Path ([IO.Path]::GetTempPath()) ("pointframe-released-desktop-" + [guid]::NewGuid().ToString('N')) }
        $script:WorkDirectory = [IO.Path]::GetFullPath($WorkDirectory)
        New-Item -ItemType Directory -Path $script:WorkDirectory -Force | Out-Null
        $runMode = Resolve-RunMode $Version $CandidateDirectory ([bool]$BuildCandidate) ([bool]$PackageOnly)
        $script:Mode = if ($runMode -eq 'release') { 'release' } else { 'candidate' }
        if (-not $PackageOnly) {
            Invoke-Step 'interactive desktop preflight' { Assert-InteractiveDesktop; 'Windows session is unlocked.' } | Out-Null
        }

        if ($runMode -eq 'release') {
            $Version = Invoke-Step 'resolve release version' {
                $resolvedVersion = $Version
                if (-not $resolvedVersion) {
                    $release = Invoke-NativeChecked 'gh' @('release', 'view', '--repo', 'dimitar-radenkov/Pointframe', '--json', 'tagName') 'Resolve latest GitHub release'
                    $resolvedVersion = (($release | ConvertFrom-Json).tagName -replace '^v', '')
                }
                if ($resolvedVersion -notmatch '^\d+\.\d+\.\d+$') { throw "Version '$resolvedVersion' is not a semantic x.y.z release version." }
                $resolvedVersion
            }
            $tag = "v$Version"
            $cliName = "Pointframe.Cli-$Version-win-x64.zip"
            $mcpName = "Pointframe.Mcp-$Version-win-x64.mcpb"
            $downloadDirectory = Join-Path $script:WorkDirectory 'downloads'
            New-Item -ItemType Directory -Path $downloadDirectory -Force | Out-Null

            Invoke-Step 'download and verify release packages' {
                foreach ($asset in @($cliName, "$cliName.sha256", $mcpName, "$mcpName.sha256")) {
                    Invoke-NativeChecked 'gh' @('release', 'download', $tag, '--repo', 'dimitar-radenkov/Pointframe', '--pattern', $asset, '--dir', $downloadDirectory) "Download $asset" | Out-Null
                }
                $cliHash = Assert-Checksum (Join-Path $downloadDirectory $cliName) (Join-Path $downloadDirectory "$cliName.sha256")
                $mcpHash = Assert-Checksum (Join-Path $downloadDirectory $mcpName) (Join-Path $downloadDirectory "$mcpName.sha256")
                $script:Packages.Add([pscustomobject]@{ name = $cliName; sha256 = $cliHash })
                $script:Packages.Add([pscustomobject]@{ name = $mcpName; sha256 = $mcpHash })
                Install-Packages (Join-Path $downloadDirectory $cliName) (Join-Path $downloadDirectory $mcpName) 'the release packages'
                "version=$Version cliSha256=$cliHash mcpSha256=$mcpHash"
            } | Out-Null
        }
        else {
            if ($runMode -eq 'build-candidate') {
                $CandidateDirectory = Join-Path $script:WorkDirectory 'candidate'
                Invoke-Step 'build candidate packages' {
                    Build-CandidatePackages $CandidateDirectory
                    "built version $($script:CandidateVersion) from the current checkout into $CandidateDirectory"
                } | Out-Null
            }

            Invoke-Step 'locate and hash candidate packages' {
                $cliPackage = Find-CandidatePackage $CandidateDirectory 'Pointframe.Cli-*-win-x64.zip'
                $mcpPackage = Find-CandidatePackage $CandidateDirectory 'Pointframe.Mcp-*-win-x64.mcpb'
                $cliHash = Register-Package $cliPackage
                $mcpHash = Register-Package $mcpPackage
                Install-Packages $cliPackage $mcpPackage 'the candidate packages'
                "candidate cli=$([IO.Path]::GetFileName($cliPackage)) sha256=$cliHash mcp=$([IO.Path]::GetFileName($mcpPackage)) sha256=$mcpHash"
            } | Out-Null
        }

        if (-not $PackageOnly) {
            Invoke-Step 'acquire desktop lock' { Enter-DesktopLock; "Held $($script:DesktopLockPath)" } | Out-Null
            Invoke-Step 'released MCP stdio desktop smoke' {
                $smoke = Join-Path $script:RepositoryRoot 'packaging/test-mcp-stdio.ps1'
                Invoke-NativeChecked 'pwsh' @('-NoProfile', '-File', $smoke, '-ExecutablePath', $script:McpExe) 'Released MCP stdio smoke'
            } | Out-Null

            Invoke-Step 'build and stage fixture' {
                $project = Join-Path $script:RepositoryRoot 'Pointframe.DesktopTestFixture/Pointframe.DesktopTestFixture.csproj'
                Invoke-NativeChecked 'dotnet' @('build', $project, '--configuration', 'Release', '--nologo', '-v', 'q') 'Build Pointframe.DesktopTestFixture Release' | Out-Null
                $output = Join-Path $script:RepositoryRoot 'Pointframe.DesktopTestFixture/bin/Release'
                $exe = Get-ChildItem -LiteralPath $output -Filter 'Pointframe.DesktopTestFixture.exe' -File -Recurse | Select-Object -First 1
                if ($null -eq $exe) { throw 'Release fixture executable was not produced.' }
                $appDirectory = Join-Path $script:WorkDirectory 'project/app'
                New-Item -ItemType Directory -Path $appDirectory -Force | Out-Null
                Get-ChildItem -LiteralPath $exe.DirectoryName -Force | Copy-Item -Destination $appDirectory -Recurse -Force
                "Fixture staged at $appDirectory"
            } | Out-Null

            Invoke-Step 'create committed verification project' {
                $projectRoot = Join-Path $script:WorkDirectory 'project'
                Push-Location $projectRoot
                try {
                    git init --quiet
                    if ($LASTEXITCODE -ne 0) { throw 'git init failed.' }
                    git config user.email 'released-desktop-check@pointframe.local'
                    git config user.name 'Pointframe released desktop check'
                    $specDirectory = Join-Path $projectRoot '.pointframe'
                    New-Item -ItemType Directory -Path $specDirectory -Force | Out-Null
                    (New-VerifySpec 'app/Pointframe.DesktopTestFixture.exe' | ConvertTo-Json -Depth 20) | Set-Content -LiteralPath (Join-Path $specDirectory 'verify.json') -Encoding utf8NoBOM
                    git add -f .pointframe/verify.json app
                    if ($LASTEXITCODE -ne 0) { throw 'git add failed for the spec and fixture.' }
                    git ls-files --error-unmatch .pointframe/verify.json *> $null
                    if ($LASTEXITCODE -ne 0) { throw 'The verification spec is not tracked in the isolated project.' }
                    git commit --quiet -m 'Add released desktop smoke spec'
                    if ($LASTEXITCODE -ne 0) { throw 'git commit failed.' }
                    'Committed .pointframe/verify.json and the staged fixture to the isolated project.'
                }
                finally { Pop-Location }
            } | Out-Null

            Invoke-Step 'released CLI verify run' {
                $projectRoot = Join-Path $script:WorkDirectory 'project'
                Push-Location $projectRoot
                $previous = $env:POINTFRAME_MCP_EXECUTABLE
                try {
                    Remove-Item Env:POINTFRAME_MCP_EXECUTABLE -ErrorAction SilentlyContinue
                    $response = Invoke-NativeChecked $script:CliExe @('verify', 'run', '--mcp', $script:McpExe) 'Released CLI verify run'
                    $json = Read-Verdict $projectRoot
                    if ($json.status -ne 'pass') { throw "Released CLI verdict was '$($json.status)' instead of 'pass'." }
                    "verdict=$($json.status)"
                }
                finally {
                    if ($null -ne $previous) { $env:POINTFRAME_MCP_EXECUTABLE = $previous } else { Remove-Item Env:POINTFRAME_MCP_EXECUTABLE -ErrorAction SilentlyContinue }
                    Pop-Location
                }
            } | Out-Null

            Invoke-Step 'released MCP record-export-add round trip' {
                $policyPath = Join-Path $script:WorkDirectory 'desktop-policy.json'
                $projectRoot = Join-Path $script:WorkDirectory 'project'
                $fixtureExe = Join-Path $projectRoot 'app/Pointframe.DesktopTestFixture.exe'
                $policy = @{
                    schemaVersion = 1; artifactRoot = (Join-Path $script:WorkDirectory 'artifacts'); evidencePolicy = 'Failures'
                    profiles = @(@{ id = 'released-fixture'; executablePath = $fixtureExe; arguments = @(); workingDirectory = $projectRoot; allowAttach = $false
                        allowedActions = @('ListApps','StartTestSession','ObserveApp','Invoke','CheckUi','GetActionResult','GetTestReport','EndTestSession')
                        allowedGlobalHotkeys = @{}; allowedShellSurfaces = @('NotificationArea','NotificationOverflow'); allowMonitorObservation = $false })
                }
                New-Item -ItemType Directory -Path $policy.artifactRoot -Force | Out-Null
                $policy | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $policyPath -Encoding utf8NoBOM
                $process = Start-McpClient $script:McpExe @('--desktop-testing','--desktop-policy',$policyPath)
                $sessionId = $null
                try {
                    $initialize = Send-McpRequest $process 'initialize' @{ protocolVersion = '2025-11-25'; capabilities = @{}; clientInfo = @{ name = 'released-desktop-check'; version = '1.0.0' } }
                    if (-not $initialize.serverInfo) { throw 'Released MCP initialize returned no serverInfo.' }
                    $process.StandardInput.WriteLine((@{ jsonrpc='2.0'; method='notifications/initialized'; params=@{} } | ConvertTo-Json -Compress))
                    $tools = Send-McpRequest $process 'tools/list' @{}
                    $toolNames = @($tools.tools | ForEach-Object name)
                    if ('desktop_export_scenario' -notin $toolNames) { return 'skipped (not in this release): desktop_export_scenario is absent from tools/list.' }
                    $started = Invoke-McpTool $process 'desktop_start_test_session' @{ actionId = [guid]::NewGuid().ToString(); profileId = 'released-fixture'; criteria = @('The fixture save button is present.') }
                    $sessionId = [string](Get-OptionalProperty $started 'sessionRef')
                    if (-not $sessionId) { $sessionId = [string](Get-OptionalProperty $started 'sessionId') }
                    if (-not $sessionId) { throw "desktop_start_test_session failed: $($started | ConvertTo-Json -Compress -Depth 8)" }
                    $displays = Invoke-McpTool $process 'list_displays' @{}
                    $display = @($displays.displays | Select-Object -First 1)[0]
                    if ($null -eq $display) { throw 'list_displays returned no monitor bounds.' }
                    $observation = Invoke-McpTool $process 'desktop_observe_app' @{ sessionId = $sessionId; captureBoundsPixels = @($display.boundsPixels); includeUiAutomation = $true; includeImages = $false }
                    $button = @((Get-OptionalProperty $observation 'elements') | Where-Object automationId -eq 'saveButton' | Select-Object -First 1)[0]
                    if ($null -eq $button -or -not $button.elementRef) { throw 'desktop_observe_app did not return the saveButton element reference.' }
                    $action = Invoke-McpTool $process 'desktop_invoke' @{ sessionId = $sessionId; actionId = [guid]::NewGuid().ToString(); elementRef = [string]$button.elementRef }
                    if ((Get-OptionalProperty $action 'error') -or (Get-OptionalProperty $action 'dispatch') -eq 'NotStarted') { throw "desktop_invoke saveButton failed: $($action | ConvertTo-Json -Compress -Depth 8)" }
                    $check = Invoke-McpTool $process 'desktop_check_ui' @{ sessionId = $sessionId; kind = 'exists'; automationId = 'saveButton'; criterionId = 'C1'; timeoutSeconds = 5 }
                    if ((Get-OptionalProperty $check 'verification') -ne 'passed') { throw "desktop_check_ui saveButton: $($check | ConvertTo-Json -Compress -Depth 8)" }
                    $negative = Invoke-McpTool $process 'desktop_check_ui' @{ sessionId = $sessionId; kind = 'exists'; automationId = 'noSuchElement'; expectFailure = $true; timeoutSeconds = 1 }
                    if ((Get-OptionalProperty $negative 'verification') -ne 'passed') { throw "desktop_check_ui negative control: $($negative | ConvertTo-Json -Compress -Depth 8)" }
                    $exportPath = Join-Path $script:WorkDirectory 'released-smoke.export.json'
                    $exportTool = @($tools.tools | Where-Object name -eq 'desktop_export_scenario' | Select-Object -First 1)[0]
                    $exportProperties = @($exportTool.inputSchema.properties.PSObject.Properties.Name)
                    $exportArguments = @{}
                    foreach ($propertyName in $exportProperties) {
                        switch -Regex ($propertyName) {
                            '^(sessionId|sessionRef)$' { $exportArguments[$propertyName] = $sessionId; break }
                            '^(scenarioId|id|name)$' { $exportArguments[$propertyName] = 'recorded-smoke'; break }
                            '^(outputPath|filePath|path)$' { $exportArguments[$propertyName] = $exportPath; break }
                        }
                    }
                    foreach ($requiredName in @(Get-OptionalProperty (Get-OptionalProperty $exportTool 'inputSchema') 'required')) {
                        if (-not $exportArguments.ContainsKey([string]$requiredName)) { throw "desktop_export_scenario requires unsupported parameter '$requiredName'." }
                    }
                    $export = Invoke-McpTool $process 'desktop_export_scenario' $exportArguments
                    if (-not (Test-Path $exportPath)) {
                        foreach ($field in @('path','filePath','exportPath','scenarioPath')) {
                            $candidate = [string](Get-OptionalProperty $export $field)
                            if ($candidate -and (Test-Path $candidate)) { $exportPath = $candidate; break }
                        }
                    }
                    if (-not (Test-Path $exportPath)) {
                        # The tool returns { scenario, unsupported, complete } and saves its own copy in the session
                        # folder; write the returned export for `verify scenario add`.
                        if (-not (Get-OptionalProperty $export 'scenario')) { throw "desktop_export_scenario returned no scenario: $($export | ConvertTo-Json -Compress -Depth 8)" }
                        if (-not (Get-OptionalProperty $export 'complete')) { throw "desktop_export_scenario reported unsupported steps: $($export | ConvertTo-Json -Compress -Depth 8)" }
                        $export | ConvertTo-Json -Depth 12 | Set-Content -Path $exportPath -Encoding utf8NoBOM
                    }
                    Invoke-McpTool $process 'desktop_end_test_session' @{ sessionId = $sessionId; actionId = [guid]::NewGuid().ToString() } | Out-Null
                    $sessionId = $null
                }
                finally {
                    if ($sessionId) { try { Invoke-McpTool $process 'desktop_end_test_session' @{ sessionId = $sessionId; actionId = [guid]::NewGuid().ToString() } | Out-Null } catch { } }
                    Stop-TrackedProcess $process
                }
                Push-Location $projectRoot
                $previousMcpEnvironment = $env:POINTFRAME_MCP_EXECUTABLE
                try {
                    Remove-Item Env:POINTFRAME_MCP_EXECUTABLE -ErrorAction SilentlyContinue
                    $addOutput = Invoke-NativeChecked $script:CliExe @('verify','scenario','add','--from',$exportPath,'--spec',(Join-Path $projectRoot '.pointframe/verify.json')) 'Released CLI verify scenario add'
                    $scenarioId = 'recorded-smoke'
                    try { $added = ConvertFrom-CliJson $addOutput; if ($added.id) { $scenarioId = [string]$added.id } elseif ($added.scenarioId) { $scenarioId = [string]$added.scenarioId } } catch { }
                    $run = Invoke-NativeChecked $script:CliExe @('verify','run','--mcp',$script:McpExe,'--scenario',$scenarioId) 'Released CLI verify imported scenario'
                    # A --scenario run reports 'partial' overall; the imported scenario itself must pass.
                    $verdict = Read-Verdict $projectRoot
                    $imported = @($verdict.scenarios | Where-Object { $_.id -eq $scenarioId }) | Select-Object -First 1
                    if ($null -eq $imported -or $imported.status -ne 'pass') { throw "Imported scenario '$scenarioId' did not pass (verdict $($verdict.status))." }
                    "exported and added scenario '$scenarioId'; verdict=pass"
                }
                finally {
                    if ($null -ne $previousMcpEnvironment) { $env:POINTFRAME_MCP_EXECUTABLE = $previousMcpEnvironment } else { Remove-Item Env:POINTFRAME_MCP_EXECUTABLE -ErrorAction SilentlyContinue }
                    Pop-Location
                }
            } | Out-Null
        }
    }
    catch { $script:Failure = $_.Exception.Message }
    finally {
        Stop-WorkProcesses
        Exit-DesktopLock
    }
}

if (-not $script:WorkDirectory) {
    $script:WorkDirectory = if ($SelfTest) { Join-Path ([IO.Path]::GetTempPath()) ("pointframe-released-desktop-selftest-result-" + [guid]::NewGuid().ToString('N')) } else { $WorkDirectory }
    if ($script:WorkDirectory) { New-Item -ItemType Directory -Path $script:WorkDirectory -Force | Out-Null }
}

if ($script:WorkDirectory) {
    $resultPath = Join-Path $script:WorkDirectory 'result.json'
    $result = New-ResultShape @($script:Results)
    $result | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $resultPath -Encoding utf8NoBOM
}
$overall = if ($script:Failure) { 'FAILED' } elseif (@($script:Results | Where-Object status -eq 'skipped').Count -gt 0) { 'PASSED (with skipped capability)' } else { 'PASSED' }
$retain = $KeepWorkDirectory -or $script:Failure -or $PackageOnly
$summaryDetail = if ($script:WorkDirectory -and $retain) { "; result=$((Join-Path $script:WorkDirectory 'result.json'))" } elseif ($script:WorkDirectory) { '; work directory cleaned' } else { '' }
$checkName = if ($script:Mode -eq 'candidate' -and $PackageOnly) { 'candidate package-only check' } elseif ($script:Mode -eq 'candidate') { 'candidate desktop check' } else { 'released desktop check' }
Write-Host ("{0}: {1}{2}" -f $overall, $checkName, $summaryDetail)
if ($script:WorkDirectory -and -not $retain) {
    Remove-Item -LiteralPath $script:WorkDirectory -Recurse -Force
}
if ($script:Failure) { [Console]::Error.WriteLine($script:Failure); exit 1 }
exit 0

