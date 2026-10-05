<#
.SYNOPSIS
Proves that an AI client can be onboarded to the Pointframe MCP server and then see a real capture.

.DESCRIPTION
For the chosen client the script does the following:

  1. Resolves the MCP server (the checksum-verified release MCPB by default, or a local build / given path)
     and validates the bundle manifest when a bundle is used.
  2. Registers the server in an ISOLATED client configuration (a temp directory), twice, and proves that
     the second run keeps the unrelated servers and settings that were already there. The user's real
     client configuration is never read for writing and never modified.
  3. Resolves the launch command back out of that configuration.
  4. Drives the launch command over stdio:
       initialize -> tools/list -> list_displays -> list_windows -> capture_window
     where the window handle comes from list_windows and the target is Pointframe.DesktopTestFixture.
  5. Checks that the saved artifact SHA-256 matches, that the inline image decodes, and that the fixture's
     magenta patch is in it.
  6. Cleans up the fixture, the server, the saved artifact, and the isolated configuration.

Claude Desktop is validated at the bundle/manifest level only (its install is a GUI flow); the extracted
server is still driven through the same stdio sequence.

Every step that launches the fixture, the MCP desktop tools, or captures the screen holds an exclusive
lock file (pointframe-desktop.lock in the temp directory), so two agents never share the desktop.

.EXAMPLE
pwsh scripts/test-agent-onboarding.ps1 -Client claude-code

.EXAMPLE
pwsh scripts/test-agent-onboarding.ps1 -Client claude-code -AgentSmoke

.EXAMPLE
pwsh scripts/test-agent-onboarding.ps1 -SelfTest
#>
[CmdletBinding(DefaultParameterSetName = "Client")]
param(
    [Parameter(Mandatory = $true, ParameterSetName = "Client")]
    [ValidateSet("claude-code", "codex", "vscode", "claude-desktop")]
    [string]$Client,

    [Parameter(ParameterSetName = "Client")]
    [switch]$AgentSmoke,

    [Parameter(Mandatory = $true, ParameterSetName = "SelfTest")]
    [switch]$SelfTest,

    [Parameter(ParameterSetName = "Client")]
    [string]$ServerPath,

    [Parameter(ParameterSetName = "Client")]
    [string]$BundlePath,

    [Parameter(ParameterSetName = "Client")]
    [string]$FixturePath,

    [Parameter(ParameterSetName = "Client")]
    [switch]$UseLocalBuild,

    [Parameter(ParameterSetName = "Client")]
    [switch]$KeepWorkDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$script:RepositoryRoot = Split-Path $PSScriptRoot -Parent
$script:ReleaseBaseUrl = "https://github.com/dimitar-radenkov/Pointframe/releases/latest/download"
$script:BundleFileName = "Pointframe.Mcp-win-x64.mcpb"
$script:FixtureWindowTitle = "Pointframe Scrolling Capture Fixture"
$script:LockPath = Join-Path ([System.IO.Path]::GetTempPath()) "pointframe-desktop.lock"
$script:LockRetrySeconds = 30
$script:LockMaxWaitMinutes = 40
$script:LockStaleMinutes = 45
$script:AgentModel = "claude-haiku-4-5-20251001"
$script:AgentBudgetUsd = 0.30
$script:Results = [System.Collections.Generic.List[object]]::new()

# ---------------------------------------------------------------------------------------------
# Reporting
# ---------------------------------------------------------------------------------------------

function Add-Result
{
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Status,
        [string]$Detail = ""
    )

    $script:Results.Add([pscustomobject]@{ Name = $Name; Status = $Status; Detail = $Detail })
    Write-Host ("{0,-7} {1}  {2}" -f $Status, $Name, $Detail)
}

function Assert-That
{
    param(
        [Parameter(Mandatory = $true)][bool]$Condition,
        [Parameter(Mandatory = $true)][string]$Name,
        [string]$Detail = ""
    )

    if (-not $Condition)
    {
        Add-Result -Name $Name -Status "FAIL" -Detail $Detail
        throw "Check failed: $Name. $Detail"
    }

    Add-Result -Name $Name -Status "PASS" -Detail $Detail
}

# ---------------------------------------------------------------------------------------------
# Desktop lock (shared with other agents that use the desktop)
# ---------------------------------------------------------------------------------------------

function Enter-DesktopLock
{
    $deadline = [DateTime]::UtcNow.AddMinutes($script:LockMaxWaitMinutes)
    while ($true)
    {
        if (Test-Path $script:LockPath)
        {
            $ageMinutes = ([DateTime]::UtcNow - (Get-Item $script:LockPath).LastWriteTimeUtc).TotalMinutes
            if ($ageMinutes -gt $script:LockStaleMinutes)
            {
                Write-Host "Desktop lock is $([int]$ageMinutes) minutes old; treating it as stale."
                Remove-Item $script:LockPath -Force -ErrorAction SilentlyContinue
            }
        }

        try
        {
            $stream = [System.IO.File]::Open($script:LockPath, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write)
            try
            {
                $owner = [System.Text.Encoding]::UTF8.GetBytes("test-agent-onboarding pid=$PID utc=$([DateTime]::UtcNow.ToString('o'))")
                $stream.Write($owner, 0, $owner.Length)
            }
            finally
            {
                $stream.Dispose()
            }

            return
        }
        catch [System.IO.IOException]
        {
            if ([DateTime]::UtcNow -gt $deadline)
            {
                throw "Timed out waiting for the desktop lock at $($script:LockPath)."
            }

            Write-Host "Desktop is in use (lock exists); retrying in $($script:LockRetrySeconds) s."
            Start-Sleep -Seconds $script:LockRetrySeconds
        }
    }
}

function Exit-DesktopLock
{
    if (Test-Path $script:LockPath)
    {
        Remove-Item $script:LockPath -Force -ErrorAction SilentlyContinue
    }
}

# ---------------------------------------------------------------------------------------------
# Server resolution and bundle validation
# ---------------------------------------------------------------------------------------------

function Get-Sha256
{
    param([Parameter(Mandatory = $true)][string]$Path)

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Read-ExpectedChecksum
{
    param([Parameter(Mandatory = $true)][string]$ChecksumFile)

    $text = (Get-Content -LiteralPath $ChecksumFile -Raw).Trim()
    return ($text -split "\s+")[0].ToLowerInvariant()
}

function Get-VerifiedBundle
{
    param([Parameter(Mandatory = $true)][string]$WorkDirectory)

    $bundle = Join-Path $WorkDirectory $script:BundleFileName
    Invoke-WebRequest "$($script:ReleaseBaseUrl)/$($script:BundleFileName)" -OutFile $bundle
    Invoke-WebRequest "$($script:ReleaseBaseUrl)/$($script:BundleFileName).sha256" -OutFile "$bundle.sha256"
    $expected = Read-ExpectedChecksum -ChecksumFile "$bundle.sha256"
    $actual = Get-Sha256 -Path $bundle
    Assert-That -Condition ($actual -eq $expected) -Name "bundle checksum" -Detail "sha256=$actual"
    return $bundle
}

function Expand-Bundle
{
    param(
        [Parameter(Mandatory = $true)][string]$Bundle,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    New-Item -ItemType Directory -Force $Destination | Out-Null
    $zipCopy = Join-Path $Destination "bundle.zip"
    Copy-Item -LiteralPath $Bundle -Destination $zipCopy
    Expand-Archive -LiteralPath $zipCopy -DestinationPath $Destination -Force
    Remove-Item -LiteralPath $zipCopy -Force
}

function Test-BundleManifest
{
    param(
        [Parameter(Mandatory = $true)][string]$Directory
    )

    $manifestPath = Join-Path $Directory "manifest.json"
    Assert-That -Condition (Test-Path $manifestPath) -Name "manifest.json present"
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json

    Assert-That -Condition (-not [string]::IsNullOrWhiteSpace($manifest.manifest_version)) -Name "manifest_version set" -Detail "$($manifest.manifest_version)"
    Assert-That -Condition ($manifest.name -eq "pointframe-mcp") -Name "manifest name" -Detail "$($manifest.name)"
    Assert-That -Condition ($manifest.server.type -eq "binary") -Name "manifest server type" -Detail "$($manifest.server.type)"
    Assert-That -Condition ($manifest.compatibility.platforms -contains "win32") -Name "manifest platform win32"

    $entryPoint = Join-Path $Directory $manifest.server.entry_point
    Assert-That -Condition (Test-Path $entryPoint) -Name "manifest entry point exists" -Detail $manifest.server.entry_point

    $command = [string]$manifest.server.mcp_config.command
    Assert-That -Condition ($command -like '${__dirname}/*') -Name "manifest command uses __dirname" -Detail $command

    return [pscustomobject]@{
        Command = ($command.Replace('${__dirname}', $Directory) -replace "/", "\")
        Arguments = @($manifest.server.mcp_config.args | Where-Object { $null -ne $_ })
        Source = "manifest.json"
    }
}

function Resolve-Server
{
    param([Parameter(Mandatory = $true)][string]$WorkDirectory)

    if ($ServerPath)
    {
        $resolved = (Resolve-Path $ServerPath).Path
        Add-Result -Name "server resolved" -Status "PASS" -Detail "explicit path $resolved"
        return [pscustomobject]@{ Executable = $resolved; ManifestLaunch = $null }
    }

    if ($UseLocalBuild)
    {
        $outputDirectory = Join-Path $WorkDirectory "server-build"
        dotnet build (Join-Path $script:RepositoryRoot "Pointframe.Mcp\Pointframe.Mcp.csproj") -c Release -o $outputDirectory --nologo -v q | Out-Host
        if ($LASTEXITCODE -ne 0)
        {
            throw "Building Pointframe.Mcp failed."
        }

        $built = Join-Path $outputDirectory "Pointframe.Mcp.exe"
        Add-Result -Name "server resolved" -Status "PASS" -Detail "local build $built"
        return [pscustomobject]@{ Executable = $built; ManifestLaunch = $null }
    }

    $bundle = $BundlePath
    if (-not $bundle)
    {
        $bundle = Get-VerifiedBundle -WorkDirectory $WorkDirectory
    }

    $extracted = Join-Path $WorkDirectory "server-bundle"
    Expand-Bundle -Bundle $bundle -Destination $extracted
    $launch = Test-BundleManifest -Directory $extracted
    Add-Result -Name "server resolved" -Status "PASS" -Detail "bundle $bundle"
    return [pscustomobject]@{ Executable = $launch.Command; ManifestLaunch = $launch }
}

# ---------------------------------------------------------------------------------------------
# Isolated client registration
# ---------------------------------------------------------------------------------------------

function ConvertTo-ComparableJson
{
    param($Value)

    return ($Value | ConvertTo-Json -Depth 20 -Compress)
}

function Invoke-Native
{
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [switch]$AllowFailure
    )

    $output = & $FilePath @Arguments 2>&1 | Out-String
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0 -and -not $AllowFailure)
    {
        throw "$FilePath $($Arguments -join ' ') failed with exit code $exitCode. $output"
    }

    return [pscustomobject]@{ ExitCode = $exitCode; Output = $output }
}

function Invoke-ClaudeCodeRegistration
{
    param(
        [Parameter(Mandatory = $true)][string]$Executable,
        [Parameter(Mandatory = $true)][string]$IsolatedRoot
    )

    $configDirectory = Join-Path $IsolatedRoot "claude-config"
    New-Item -ItemType Directory -Force $configDirectory | Out-Null
    $configFile = Join-Path $configDirectory ".claude.json"
    $previous = $env:CLAUDE_CONFIG_DIR
    $env:CLAUDE_CONFIG_DIR = $configDirectory
    try
    {
        Invoke-Native -FilePath "claude" -Arguments @("mcp", "add", "--scope", "user", "unrelated-tool", "--", "cmd.exe", "/c", "echo", "unrelated") | Out-Null
        $before = (Get-Content -LiteralPath $configFile -Raw | ConvertFrom-Json).mcpServers."unrelated-tool"

        Invoke-Native -FilePath "claude" -Arguments @("mcp", "add", "--scope", "user", "pointframe", "--", $Executable) | Out-Null
        $first = (Get-Content -LiteralPath $configFile -Raw | ConvertFrom-Json).mcpServers.pointframe

        # Repeat setup: `claude mcp add` refuses an existing name, which must leave the config untouched.
        $repeat = Invoke-Native -FilePath "claude" -Arguments @("mcp", "add", "--scope", "user", "pointframe", "--", $Executable) -AllowFailure
        $afterRepeat = Get-Content -LiteralPath $configFile -Raw | ConvertFrom-Json
        Assert-That -Condition (($repeat.ExitCode -eq 0) -or ($repeat.Output -match "already exists")) -Name "claude-code repeat add is safe" -Detail "exit=$($repeat.ExitCode)"

        # Documented update path: remove the one entry, add it again.
        Invoke-Native -FilePath "claude" -Arguments @("mcp", "remove", "--scope", "user", "pointframe") | Out-Null
        Invoke-Native -FilePath "claude" -Arguments @("mcp", "add", "--scope", "user", "pointframe", "--", $Executable) | Out-Null
        $final = Get-Content -LiteralPath $configFile -Raw | ConvertFrom-Json

        Assert-That -Condition ((ConvertTo-ComparableJson $afterRepeat.mcpServers."unrelated-tool") -eq (ConvertTo-ComparableJson $before)) -Name "claude-code unrelated server preserved after repeat add"
        Assert-That -Condition ((ConvertTo-ComparableJson $final.mcpServers."unrelated-tool") -eq (ConvertTo-ComparableJson $before)) -Name "claude-code unrelated server preserved after update"
        Assert-That -Condition ((ConvertTo-ComparableJson $final.mcpServers.pointframe) -eq (ConvertTo-ComparableJson $first)) -Name "claude-code update yields the same pointframe entry"

        $get = Invoke-Native -FilePath "claude" -Arguments @("mcp", "get", "pointframe")
        Assert-That -Condition ($get.Output -like "*$Executable*") -Name "claude mcp get shows the launch command"

        $entry = $final.mcpServers.pointframe
        return [pscustomobject]@{ Command = $entry.command; Arguments = @($entry.args | Where-Object { $null -ne $_ }); Source = $configFile }
    }
    finally
    {
        $env:CLAUDE_CONFIG_DIR = $previous
    }
}

function Invoke-CodexRegistration
{
    param(
        [Parameter(Mandatory = $true)][string]$Executable,
        [Parameter(Mandatory = $true)][string]$IsolatedRoot
    )

    $codexHome = Join-Path $IsolatedRoot "codex-home"
    New-Item -ItemType Directory -Force $codexHome | Out-Null
    $configFile = Join-Path $codexHome "config.toml"
    $previous = $env:CODEX_HOME
    $env:CODEX_HOME = $codexHome
    try
    {
        Invoke-Native -FilePath "codex" -Arguments @("mcp", "add", "unrelated-tool", "--", "cmd.exe", "/c", "echo", "unrelated") | Out-Null
        $unrelatedBefore = Get-TomlSection -Text (Get-Content -LiteralPath $configFile -Raw) -Name "mcp_servers.unrelated-tool"

        Invoke-Native -FilePath "codex" -Arguments @("mcp", "add", "pointframe", "--", $Executable) | Out-Null
        $first = Get-TomlSection -Text (Get-Content -LiteralPath $configFile -Raw) -Name "mcp_servers.pointframe"
        Invoke-Native -FilePath "codex" -Arguments @("mcp", "add", "pointframe", "--", $Executable) | Out-Null
        $text = Get-Content -LiteralPath $configFile -Raw

        $headerCount = ([regex]::Matches($text, "(?m)^\[mcp_servers\.pointframe\]")).Count
        Assert-That -Condition ($headerCount -eq 1) -Name "codex repeat add keeps a single pointframe entry" -Detail "entries=$headerCount"
        Assert-That -Condition ((Get-TomlSection -Text $text -Name "mcp_servers.unrelated-tool") -eq $unrelatedBefore) -Name "codex unrelated server preserved after repeat add"
        Assert-That -Condition ((Get-TomlSection -Text $text -Name "mcp_servers.pointframe") -eq $first) -Name "codex repeat add yields the same pointframe entry"

        $get = Invoke-Native -FilePath "codex" -Arguments @("mcp", "get", "pointframe", "--json")
        $jsonStart = $get.Output.IndexOf("{")
        $entry = $get.Output.Substring($jsonStart) | ConvertFrom-Json
        return [pscustomobject]@{ Command = $entry.transport.command; Arguments = @($entry.transport.args | Where-Object { $null -ne $_ }); Source = $configFile }
    }
    finally
    {
        $env:CODEX_HOME = $previous
    }
}

function Get-TomlSection
{
    param(
        [Parameter(Mandatory = $true)][string]$Text,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $pattern = "(?ms)^\[" + [regex]::Escape($Name) + "\]\r?\n.*?(?=^\[|\z)"
    $match = [regex]::Match($Text, $pattern)
    if (-not $match.Success)
    {
        return ""
    }

    return $match.Value.Trim()
}

function Merge-VsCodeMcpConfig
{
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Executable
    )

    $root = @{}
    if (Test-Path $Path)
    {
        $root = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -AsHashtable
        $backup = "$Path.pointframe.bak"
        if (-not (Test-Path $backup))
        {
            Copy-Item -LiteralPath $Path -Destination $backup
        }
    }

    if (-not $root.ContainsKey("servers"))
    {
        $root["servers"] = @{}
    }

    $root["servers"]["pointframe"] = @{ type = "stdio"; command = $Executable }
    $directory = Split-Path $Path -Parent
    New-Item -ItemType Directory -Force $directory | Out-Null
    $root | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $Path -Encoding utf8NoBOM
}

function Invoke-VsCodeRegistration
{
    param(
        [Parameter(Mandatory = $true)][string]$Executable,
        [Parameter(Mandatory = $true)][string]$IsolatedRoot
    )

    $configFile = Join-Path $IsolatedRoot "vscode-user\mcp.json"
    New-Item -ItemType Directory -Force (Split-Path $configFile -Parent) | Out-Null
    $seed = @{
        inputs = @(@{ id = "token"; type = "promptString"; description = "unrelated input" })
        servers = @{ "unrelated-tool" = @{ type = "stdio"; command = "cmd.exe"; args = @("/c", "echo", "unrelated") } }
    }
    $seed | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $configFile -Encoding utf8NoBOM
    $before = Get-Content -LiteralPath $configFile -Raw | ConvertFrom-Json

    Merge-VsCodeMcpConfig -Path $configFile -Executable $Executable
    $first = Get-Content -LiteralPath $configFile -Raw | ConvertFrom-Json
    Merge-VsCodeMcpConfig -Path $configFile -Executable $Executable
    $final = Get-Content -LiteralPath $configFile -Raw | ConvertFrom-Json

    Assert-That -Condition ((ConvertTo-ComparableJson $final.servers."unrelated-tool") -eq (ConvertTo-ComparableJson $before.servers."unrelated-tool")) -Name "vscode unrelated server preserved after repeat setup"
    Assert-That -Condition ((ConvertTo-ComparableJson $final.inputs) -eq (ConvertTo-ComparableJson $before.inputs)) -Name "vscode unrelated top-level settings preserved"
    Assert-That -Condition ((ConvertTo-ComparableJson $final.servers.pointframe) -eq (ConvertTo-ComparableJson $first.servers.pointframe)) -Name "vscode repeat setup yields the same pointframe entry"
    Assert-That -Condition (Test-Path "$configFile.pointframe.bak") -Name "vscode backup of the original file kept"

    $entry = $final.servers.pointframe
    return [pscustomobject]@{ Command = $entry.command; Arguments = @(); Source = $configFile }
}

# ---------------------------------------------------------------------------------------------
# Image analysis
# ---------------------------------------------------------------------------------------------

function Initialize-ImageAnalysis
{
    if ("PointframeImageProbe" -as [type])
    {
        return
    }

    Add-Type -AssemblyName PresentationCore, WindowsBase
    $references = @(
        [System.Windows.Media.Imaging.BitmapDecoder].Assembly.Location,
        [System.Windows.DependencyObject].Assembly.Location
    )
    Add-Type -ReferencedAssemblies $references -TypeDefinition @"
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

public static class PointframeImageProbe
{
    public static int[] Analyze(byte[] imageBytes)
    {
        using (var stream = new MemoryStream(imageBytes))
        {
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var frame = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
            int width = frame.PixelWidth, height = frame.PixelHeight, stride = width * 4;
            var buffer = new byte[stride * height];
            frame.CopyPixels(buffer, stride, 0);
            int count = 0, minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int offset = y * stride + x * 4;
                    byte blue = buffer[offset], green = buffer[offset + 1], red = buffer[offset + 2];
                    if (red >= 235 && blue >= 235 && green <= 30)
                    {
                        count++;
                        if (x < minX) { minX = x; }
                        if (y < minY) { minY = y; }
                        if (x > maxX) { maxX = x; }
                        if (y > maxY) { maxY = y; }
                    }
                }
            }

            return new[] { width, height, count, minX, minY, maxX, maxY };
        }
    }
}
"@
}
function Get-MagentaPatchEvidence
{
    param([Parameter(Mandatory = $true)][byte[]]$ImageBytes)

    Initialize-ImageAnalysis
    $values = [PointframeImageProbe]::Analyze($ImageBytes)
    return [pscustomobject]@{
        Width = $values[0]
        Height = $values[1]
        MagentaPixels = $values[2]
        PatchWidth = if ($values[2] -gt 0) { $values[5] - $values[3] + 1 } else { 0 }
        PatchHeight = if ($values[2] -gt 0) { $values[6] - $values[4] + 1 } else { 0 }
    }
}

# ---------------------------------------------------------------------------------------------
# MCP stdio client
# ---------------------------------------------------------------------------------------------

function Start-McpServer
{
    param(
        [Parameter(Mandatory = $true)][string]$Command,
        [string[]]$Arguments = @(),
        [string]$DataDirectory = ""
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $Command
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardInput = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    if ($DataDirectory)
    {
        # Keep the test off the user's real Pointframe database and capture folder.
        $startInfo.Environment["SNIPPINGTOOL_AUTOMATION_DATA_DIRECTORY"] = $DataDirectory
    }
    foreach ($argument in $Arguments)
    {
        [void]$startInfo.ArgumentList.Add($argument)
    }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    if (-not $process.Start())
    {
        throw "Could not start $Command."
    }

    # The server logs heavily to stderr; an unread pipe fills up and blocks the server, so drain it continuously.
    $stderrTask = $process.StandardError.ReadToEndAsync()
    return [pscustomobject]@{ Process = $process; NextId = 1; StderrTask = $stderrTask }
}

function Send-McpRequest
{
    param(
        [Parameter(Mandatory = $true)]$Session,
        [Parameter(Mandatory = $true)][string]$Method,
        $Params = @{},
        [int]$TimeoutSeconds = 60
    )

    $id = $Session.NextId
    $Session.NextId = $id + 1
    $request = @{ jsonrpc = "2.0"; id = $id; method = $Method; params = $Params } | ConvertTo-Json -Compress -Depth 20
    $Session.Process.StandardInput.WriteLine($request)
    $Session.Process.StandardInput.Flush()

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ($true)
    {
        $remaining = [Math]::Max(1, [int]($deadline - [DateTime]::UtcNow).TotalMilliseconds)
        $readTask = $Session.Process.StandardOutput.ReadLineAsync()
        if (-not $readTask.Wait($remaining))
        {
            throw "Timed out waiting for the response to $Method."
        }

        $line = $readTask.Result
        if ($null -eq $line)
        {
            throw "The server closed stdout while waiting for $Method."
        }

        $message = ConvertFrom-McpLine -Line $line
        if ($message.PSObject.Properties.Name -contains "id" -and $message.id -eq $id)
        {
            if ($message.PSObject.Properties.Name -contains "error")
            {
                throw "$Method returned a JSON-RPC error: $($message.error | ConvertTo-Json -Compress)"
            }

            return $message.result
        }
    }
}

function ConvertFrom-McpLine
{
    param([Parameter(Mandatory = $true)][string]$Line)

    try
    {
        return ($Line | ConvertFrom-Json)
    }
    catch
    {
        throw "Server stdout carried a line that is not JSON (this breaks the stdio protocol): $Line"
    }
}

function Send-McpNotification
{
    param(
        [Parameter(Mandatory = $true)]$Session,
        [Parameter(Mandatory = $true)][string]$Method
    )

    $notification = @{ jsonrpc = "2.0"; method = $Method; params = @{} } | ConvertTo-Json -Compress
    $Session.Process.StandardInput.WriteLine($notification)
    $Session.Process.StandardInput.Flush()
}

function Invoke-McpTool
{
    param(
        [Parameter(Mandatory = $true)]$Session,
        [Parameter(Mandatory = $true)][string]$Name,
        [hashtable]$Arguments = @{}
    )

    return Send-McpRequest -Session $Session -Method "tools/call" -Params @{ name = $Name; arguments = $Arguments }
}

function Test-ToolFailed
{
    param([Parameter(Mandatory = $true)]$Result)

    return ($Result.PSObject.Properties.Name -contains "isError") -and [bool]$Result.isError
}

function Get-ToolFailureDetail
{
    param([Parameter(Mandatory = $true)]$Result)

    $json = $Result | ConvertTo-Json -Depth 8 -Compress
    return $json.Substring(0, [Math]::Min(500, $json.Length))
}

function Stop-McpServer
{
    param($Session)

    if ($null -eq $Session)
    {
        return
    }

    if (-not $Session.Process.HasExited)
    {
        $Session.Process.Kill()
    }

    $Session.Process.Dispose()
}

# ---------------------------------------------------------------------------------------------
# Fixture and desktop
# ---------------------------------------------------------------------------------------------

function Resolve-Fixture
{
    param([Parameter(Mandatory = $true)][string]$WorkDirectory)

    if ($FixturePath)
    {
        return (Resolve-Path $FixturePath).Path
    }

    $outputDirectory = Join-Path $WorkDirectory "fixture-build"
    dotnet build (Join-Path $script:RepositoryRoot "Pointframe.DesktopTestFixture\Pointframe.DesktopTestFixture.csproj") -c Release -o $outputDirectory --nologo -v q | Out-Host
    if ($LASTEXITCODE -ne 0)
    {
        throw "Building Pointframe.DesktopTestFixture failed."
    }

    return (Join-Path $outputDirectory "Pointframe.DesktopTestFixture.exe")
}

function Stop-EditorMcpServers
{
    param([Parameter(Mandatory = $true)][string]$WorkDirectory)

    foreach ($process in Get-Process -Name "Pointframe.Mcp" -ErrorAction SilentlyContinue)
    {
        $path = $null
        try { $path = $process.Path } catch { }
        if ($path -and $path.StartsWith($WorkDirectory, [StringComparison]::OrdinalIgnoreCase))
        {
            continue
        }

        Write-Host "Stopping editor-started Pointframe.Mcp.exe (pid $($process.Id)) while holding the desktop lock."
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    }
}

function Start-Fixture
{
    param([Parameter(Mandatory = $true)][string]$FixtureExecutable)

    $process = Start-Process -FilePath $FixtureExecutable -ArgumentList "--scroll-fixture" -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while ([DateTime]::UtcNow -lt $deadline)
    {
        $process.Refresh()
        if ($process.MainWindowHandle -ne 0)
        {
            Start-Sleep -Milliseconds 800
            return $process
        }

        Start-Sleep -Milliseconds 200
    }

    throw "The fixture window did not appear."
}

function Set-ForegroundWindowHandle
{
    param([Parameter(Mandatory = $true)][int64]$Handle)

    if (-not ("PointframeUser32" -as [type]))
    {
        Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class PointframeUser32
{
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
"@
    }

    [void][PointframeUser32]::ShowWindow([IntPtr]$Handle, 9)
    [void][PointframeUser32]::SetForegroundWindow([IntPtr]$Handle)
    Start-Sleep -Milliseconds 500
}

function Invoke-CaptureSequence
{
    param(
        [Parameter(Mandatory = $true)]$Launch,
        [Parameter(Mandatory = $true)]$FixtureProcess
    )

    $session = Start-McpServer -Command $Launch.Command -Arguments $Launch.Arguments -DataDirectory $Launch.DataDirectory
    try
    {
        $initialize = Send-McpRequest -Session $session -Method "initialize" -Params @{
            protocolVersion = "2025-11-25"
            capabilities = @{}
            clientInfo = @{ name = "pointframe-onboarding-test"; version = "1.0.0" }
        }
        Send-McpNotification -Session $session -Method "notifications/initialized"
        Assert-That -Condition (-not [string]::IsNullOrWhiteSpace($initialize.serverInfo.name)) -Name "stdio initialize" -Detail "server=$($initialize.serverInfo.name) $($initialize.serverInfo.version)"

        $tools = Send-McpRequest -Session $session -Method "tools/list"
        $names = @($tools.tools | ForEach-Object { $_.name })
        $required = @("list_displays", "list_windows", "capture_window")
        $missing = @($required | Where-Object { $names -notcontains $_ })
        Assert-That -Condition ($missing.Count -eq 0) -Name "stdio tools/list" -Detail "$($names.Count) tools; missing=[$($missing -join ',')]"

        $displays = Invoke-McpTool -Session $session -Name "list_displays"
        Assert-That -Condition ((-not (Test-ToolFailed $displays)) -and $displays.structuredContent.success -and $displays.structuredContent.displays.Count -ge 1) -Name "stdio list_displays" -Detail "displays=$($displays.structuredContent.displays.Count)"

        Set-ForegroundWindowHandle -Handle $FixtureProcess.MainWindowHandle.ToInt64()
        $windows = Invoke-McpTool -Session $session -Name "list_windows"
        $target = @($windows.structuredContent.windows | Where-Object { $_.title -eq $script:FixtureWindowTitle -and $_.processId -eq $FixtureProcess.Id }) | Select-Object -First 1
        Assert-That -Condition ($null -ne $target) -Name "stdio list_windows finds the fixture" -Detail "hwnd=$($target.hwnd)"

        $capture = Invoke-McpTool -Session $session -Name "capture_window" -Arguments @{ windowId = [int64]$target.hwnd }
        Assert-That -Condition ((-not (Test-ToolFailed $capture)) -and $capture.structuredContent.success) -Name "stdio capture_window" -Detail (Get-ToolFailureDetail $capture)

        $metadata = $capture.structuredContent.artifact.metadata
        $artifactPath = [string]$metadata.path
        try
        {
            Assert-That -Condition (Test-Path $artifactPath) -Name "artifact file exists" -Detail $artifactPath
            $actualHash = Get-Sha256 -Path $artifactPath
            Assert-That -Condition ($actualHash -eq ([string]$metadata.sha256).ToLowerInvariant()) -Name "artifact SHA-256 matches" -Detail $actualHash
            Assert-That -Condition ((Get-Item $artifactPath).Length -eq [int64]$metadata.byteLength) -Name "artifact byte length matches" -Detail "$($metadata.byteLength)"

            $image = @($capture.content | Where-Object { $_.type -eq "image" }) | Select-Object -First 1
            Assert-That -Condition ($null -ne $image -and $image.mimeType -eq "image/png") -Name "inline image block present" -Detail "mime=$($image.mimeType)"
            $imageBytes = [Convert]::FromBase64String([string]$image.data)
            $evidence = Get-MagentaPatchEvidence -ImageBytes $imageBytes
            Assert-That -Condition ($evidence.Width -gt 100 -and $evidence.Height -gt 100) -Name "inline image decodes" -Detail "$($evidence.Width)x$($evidence.Height)"
            Assert-That -Condition ($evidence.MagentaPixels -ge 400 -and $evidence.PatchWidth -ge 20 -and $evidence.PatchHeight -ge 20) -Name "inline image contains the magenta patch" -Detail "pixels=$($evidence.MagentaPixels) bbox=$($evidence.PatchWidth)x$($evidence.PatchHeight)"

            $fileEvidence = Get-MagentaPatchEvidence -ImageBytes ([System.IO.File]::ReadAllBytes($artifactPath))
            Assert-That -Condition ($fileEvidence.MagentaPixels -ge 400) -Name "saved PNG contains the magenta patch" -Detail "pixels=$($fileEvidence.MagentaPixels)"
        }
        finally
        {
            if ($artifactPath -and (Test-Path $artifactPath))
            {
                Remove-Item -LiteralPath $artifactPath -Force -ErrorAction SilentlyContinue
            }
        }
    }
    finally
    {
        Stop-McpServer -Session $session
    }
}

# ---------------------------------------------------------------------------------------------
# Agent smoke
# ---------------------------------------------------------------------------------------------

function Invoke-AgentSmoke
{
    param(
        [Parameter(Mandatory = $true)]$Launch,
        [Parameter(Mandatory = $true)][string]$WorkDirectory
    )

    $mcpConfig = Join-Path $WorkDirectory "agent-mcp.json"
    @{ mcpServers = @{ pointframe = @{ type = "stdio"; command = $Launch.Command; args = @($Launch.Arguments); env = @{ SNIPPINGTOOL_AUTOMATION_DATA_DIRECTORY = $Launch.DataDirectory } } } } |
        ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $mcpConfig -Encoding utf8NoBOM

    $prompt = "Call the Pointframe list_windows tool, find the window titled '$($script:FixtureWindowTitle)', " +
        "call capture_window with its Hwnd as windowId, and look at the returned image. " +
        "Reply with one word: the color of the small square near the top-right of the window content."
    $arguments = @(
        "-p", "--model", $script:AgentModel,
        "--max-budget-usd", "$($script:AgentBudgetUsd)",
        "--mcp-config", $mcpConfig, "--strict-mcp-config",
        "--allowedTools", "mcp__pointframe__list_windows", "mcp__pointframe__capture_window",
        "--no-session-persistence", "--output-format", "json"
    )

    $job = Start-Job -ScriptBlock {
        param($PromptText, $ArgumentList)
        $PromptText | & claude @ArgumentList 2>&1 | Out-String
    } -ArgumentList $prompt, $arguments

    if (-not (Wait-Job $job -Timeout 300))
    {
        Stop-Job $job
        Remove-Job $job -Force
        throw "The agent smoke run did not finish within 300 s."
    }

    $raw = Receive-Job $job
    Remove-Job $job -Force
    $jsonStart = $raw.IndexOf("{")
    Assert-That -Condition ($jsonStart -ge 0) -Name "agent smoke produced JSON output" -Detail ($raw.Substring(0, [Math]::Min(300, $raw.Length)))
    $result = $raw.Substring($jsonStart) | ConvertFrom-Json
    $answer = [string]$result.result
    $cost = [double]$result.total_cost_usd
    Assert-That -Condition ($cost -le $script:AgentBudgetUsd) -Name "agent smoke stayed within budget" -Detail ("cost={0:N4} USD" -f $cost)
    Assert-That -Condition ($answer -match "(?i)magenta|fuchsia|pink|purple|violet") -Name "agent saw the screenshot" -Detail "answer='$($answer.Trim())'"
}

# ---------------------------------------------------------------------------------------------
# Client flow
# ---------------------------------------------------------------------------------------------

function Remove-DirectoryWithRetry
{
    param([Parameter(Mandatory = $true)][string]$Path)

    # Windows keeps an exe locked for a moment after its process exits, so retry briefly.
    for ($attempt = 0; $attempt -lt 15 -and (Test-Path $Path); $attempt++)
    {
        Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction SilentlyContinue
        if (Test-Path $Path)
        {
            Start-Sleep -Seconds 1
        }
    }
}

function Invoke-ClientCheck
{
    $workDirectory = Join-Path ([System.IO.Path]::GetTempPath()) "pointframe-onboarding-$([Guid]::NewGuid().ToString('N').Substring(0, 8))"
    New-Item -ItemType Directory -Force $workDirectory | Out-Null
    $isolatedRoot = Join-Path $workDirectory "isolated"
    $fixtureProcess = $null
    $lockHeld = $false
    try
    {
        $server = Resolve-Server -WorkDirectory $workDirectory
        Assert-That -Condition (Test-Path $server.Executable) -Name "server executable exists" -Detail $server.Executable

        $launch = switch ($Client)
        {
            "claude-code" { Invoke-ClaudeCodeRegistration -Executable $server.Executable -IsolatedRoot $isolatedRoot }
            "codex" { Invoke-CodexRegistration -Executable $server.Executable -IsolatedRoot $isolatedRoot }
            "vscode" { Invoke-VsCodeRegistration -Executable $server.Executable -IsolatedRoot $isolatedRoot }
            "claude-desktop"
            {
                if ($null -eq $server.ManifestLaunch)
                {
                    throw "claude-desktop validates the MCPB bundle; do not combine it with -ServerPath or -UseLocalBuild."
                }

                $server.ManifestLaunch
            }
        }

        Assert-That -Condition ($launch.Command -ieq $server.Executable) -Name "resolved launch config points at the server" -Detail "$($launch.Command) $($launch.Arguments -join ' ') (from $($launch.Source))"

        $launch | Add-Member -NotePropertyName DataDirectory -NotePropertyValue (Join-Path $workDirectory "pointframe-data")
        $fixtureExecutable = Resolve-Fixture -WorkDirectory $workDirectory

        Enter-DesktopLock
        $lockHeld = $true
        Stop-EditorMcpServers -WorkDirectory $workDirectory
        $fixtureProcess = Start-Fixture -FixtureExecutable $fixtureExecutable
        Invoke-CaptureSequence -Launch $launch -FixtureProcess $fixtureProcess
        if ($AgentSmoke)
        {
            Invoke-AgentSmoke -Launch $launch -WorkDirectory $workDirectory
        }
    }
    finally
    {
        if ($null -ne $fixtureProcess -and -not $fixtureProcess.HasExited)
        {
            Stop-Process -Id $fixtureProcess.Id -Force -ErrorAction SilentlyContinue
            [void]$fixtureProcess.WaitForExit(10000)
        }

        if ($lockHeld)
        {
            Exit-DesktopLock
        }

        if (-not $KeepWorkDirectory -and (Test-Path $workDirectory))
        {
            Remove-DirectoryWithRetry -Path $workDirectory
        }
    }

    Assert-That -Condition (-not (Test-Path $script:LockPath)) -Name "cleanup released the desktop lock"
    Assert-That -Condition ($KeepWorkDirectory -or -not (Test-Path $workDirectory)) -Name "cleanup removed the work directory"
}

# ---------------------------------------------------------------------------------------------
# Offline self test
# ---------------------------------------------------------------------------------------------

function New-TestPng
{
    param(
        [int]$Width,
        [int]$Height,
        [bool]$Patch
    )

    $stride = $Width * 4
    $pixels = [byte[]]::new($stride * $Height)
    [Array]::Fill($pixels, [byte]255)
    if ($Patch)
    {
        for ($y = 10; $y -lt 58; $y++)
        {
            for ($x = 100; $x -lt 148; $x++)
            {
                $offset = $y * $stride + $x * 4
                $pixels[$offset] = 255
                $pixels[$offset + 1] = 0
                $pixels[$offset + 2] = 255
            }
        }
    }

    $source = [System.Windows.Media.Imaging.BitmapSource]::Create($Width, $Height, 96, 96, [System.Windows.Media.PixelFormats]::Bgra32, $null, $pixels, $stride)
    $encoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($source))
    $stream = [System.IO.MemoryStream]::new()
    $encoder.Save($stream)
    return , $stream.ToArray()
}
function Invoke-OfflineSelfTest
{
    $work = Join-Path ([System.IO.Path]::GetTempPath()) "pointframe-onboarding-selftest-$([Guid]::NewGuid().ToString('N').Substring(0, 8))"
    New-Item -ItemType Directory -Force $work | Out-Null
    $script:LockPath = Join-Path $work "test.lock"
    try
    {
        # Lock: acquire, contention is detected, release, stale replacement.
        Enter-DesktopLock
        Assert-That -Condition (Test-Path $script:LockPath) -Name "selftest lock acquired"
        $contended = $false
        try
        {
            $stream = [System.IO.File]::Open($script:LockPath, [System.IO.FileMode]::CreateNew)
            $stream.Dispose()
        }
        catch [System.IO.IOException]
        {
            $contended = $true
        }

        Assert-That -Condition $contended -Name "selftest second acquire is refused"
        Exit-DesktopLock
        Assert-That -Condition (-not (Test-Path $script:LockPath)) -Name "selftest lock released"
        Set-Content -LiteralPath $script:LockPath -Value "stale"
        (Get-Item $script:LockPath).LastWriteTimeUtc = [DateTime]::UtcNow.AddMinutes(-($script:LockStaleMinutes + 5))
        Enter-DesktopLock
        Assert-That -Condition ((Get-Content -LiteralPath $script:LockPath -Raw) -like "test-agent-onboarding*") -Name "selftest stale lock replaced"
        Exit-DesktopLock

        # JSON-RPC line parsing.
        $parsed = ConvertFrom-McpLine -Line '{"jsonrpc":"2.0","id":3,"result":{}}'
        Assert-That -Condition ($parsed.id -eq 3) -Name "selftest parses a JSON-RPC line"
        $rejected = $false
        try { ConvertFrom-McpLine -Line "log noise on stdout" | Out-Null } catch { $rejected = $true }
        Assert-That -Condition $rejected -Name "selftest rejects non-JSON stdout"

        # Checksum file parsing.
        $checksumFile = Join-Path $work "x.sha256"
        Set-Content -LiteralPath $checksumFile -Value "ABCDEF  Pointframe.Mcp-win-x64.mcpb"
        Assert-That -Condition ((Read-ExpectedChecksum -ChecksumFile $checksumFile) -eq "abcdef") -Name "selftest reads a checksum file"

        # Manifest validation on a synthetic bundle.
        $bundleDirectory = Join-Path $work "bundle"
        New-Item -ItemType Directory -Force $bundleDirectory | Out-Null
        Set-Content -LiteralPath (Join-Path $bundleDirectory "Pointframe.Mcp.exe") -Value "x"
        @{
            manifest_version = "0.3"
            name = "pointframe-mcp"
            compatibility = @{ platforms = @("win32") }
            server = @{ type = "binary"; entry_point = "Pointframe.Mcp.exe"; mcp_config = @{ command = '${__dirname}/Pointframe.Mcp.exe'; args = @() } }
        } | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $bundleDirectory "manifest.json")
        $launch = Test-BundleManifest -Directory $bundleDirectory
        Assert-That -Condition ($launch.Command -ieq (Join-Path $bundleDirectory "Pointframe.Mcp.exe")) -Name "selftest resolves the manifest launch command"

        # VS Code merge keeps unrelated content and is repeatable.
        $mcpJson = Join-Path $work "vscode\mcp.json"
        New-Item -ItemType Directory -Force (Split-Path $mcpJson -Parent) | Out-Null
        '{"inputs":[1],"servers":{"other":{"type":"stdio","command":"x"}}}' | Set-Content -LiteralPath $mcpJson
        Merge-VsCodeMcpConfig -Path $mcpJson -Executable "C:\a\Pointframe.Mcp.exe"
        Merge-VsCodeMcpConfig -Path $mcpJson -Executable "C:\a\Pointframe.Mcp.exe"
        $merged = Get-Content -LiteralPath $mcpJson -Raw | ConvertFrom-Json
        Assert-That -Condition ($merged.servers.other.command -eq "x" -and $merged.inputs.Count -eq 1 -and $merged.servers.pointframe.command -eq "C:\a\Pointframe.Mcp.exe") -Name "selftest VS Code merge preserves unrelated config"

        # TOML section extraction.
        $toml = "[mcp_servers.a]`ncommand = 'x'`n`n[mcp_servers.b]`ncommand = 'y'`n"
        Assert-That -Condition ((Get-TomlSection -Text $toml -Name "mcp_servers.a") -eq "[mcp_servers.a]`ncommand = 'x'") -Name "selftest extracts a TOML section"

        # Magenta detection on a synthetic image.
        Add-Type -AssemblyName PresentationCore, WindowsBase
        $evidence = Get-MagentaPatchEvidence -ImageBytes (New-TestPng -Width 200 -Height 120 -Patch $true)
        Assert-That -Condition ($evidence.MagentaPixels -eq 2304 -and $evidence.PatchWidth -eq 48) -Name "selftest finds a 48x48 magenta patch" -Detail "pixels=$($evidence.MagentaPixels)"
        $blank = Get-MagentaPatchEvidence -ImageBytes (New-TestPng -Width 50 -Height 50 -Patch $false)
        Assert-That -Condition ($blank.MagentaPixels -eq 0) -Name "selftest finds no patch in a blank image"    }
    finally
    {
        Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# ---------------------------------------------------------------------------------------------
# Entry point
# ---------------------------------------------------------------------------------------------

try
{
    if ($SelfTest)
    {
        Invoke-OfflineSelfTest
    }
    else
    {
        Invoke-ClientCheck
    }

    Write-Host ""
    Write-Host "ONBOARDING CHECK PASSED ($($script:Results.Count) checks)"
    exit 0
}
catch
{
    Write-Host ""
    Write-Host "ONBOARDING CHECK FAILED: $($_.Exception.Message)"
    exit 1
}
