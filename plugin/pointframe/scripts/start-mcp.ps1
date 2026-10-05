# Starts the pinned Pointframe MCP server for the Pointframe Claude plugin (Windows PowerShell 5.1 or later).
# stdout belongs to the MCP JSON-RPC stream, so this script never writes to it; every message goes to stderr.
# The server is downloaded once from the pinned GitHub release, verified against the SHA-256 in
# server.lock.json, extracted under %LOCALAPPDATA%\Pointframe\plugin-mcp\<version>\, and then run with
# stdin, stdout, and stderr inherited.
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$ServerArguments = @()
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Write-Log([string]$Message)
{
    [Console]::Error.WriteLine("pointframe-plugin: $Message")
}

function Get-FileSha256([string]$Path)
{
    $sha256 = [Security.Cryptography.SHA256]::Create()
    $stream = [IO.File]::OpenRead($Path)
    try
    {
        return ([BitConverter]::ToString($sha256.ComputeHash($stream)) -replace '-', '').ToLowerInvariant()
    }
    finally
    {
        $stream.Dispose()
        $sha256.Dispose()
    }
}

function Read-Lock([string]$Path)
{
    $lock = [IO.File]::ReadAllText($Path) | ConvertFrom-Json
    if ($lock.schemaVersion -ne 1)
    {
        throw "Unsupported server.lock.json schemaVersion '$($lock.schemaVersion)'."
    }
    if ([string]$lock.version -notmatch '^[0-9]+(\.[0-9]+){2,3}$')
    {
        throw "server.lock.json has an invalid version '$($lock.version)'."
    }
    if ([string]$lock.sha256 -notmatch '^[0-9a-fA-F]{64}$')
    {
        throw 'server.lock.json has an invalid sha256.'
    }
    if ([string]$lock.url -notmatch '^https://')
    {
        throw 'server.lock.json url must be an https:// URL.'
    }
    $lock
}

function Test-CachedServer([string]$ServerDirectory, [string]$ExpectedSha256)
{
    $markerPath = Join-Path $ServerDirectory '.verified.json'
    $exePath = Join-Path $ServerDirectory 'Pointframe.Mcp.exe'
    if (-not ((Test-Path -LiteralPath $markerPath) -and (Test-Path -LiteralPath $exePath)))
    {
        return $false
    }
    try
    {
        $marker = [IO.File]::ReadAllText($markerPath) | ConvertFrom-Json
        return ($marker.archiveSha256 -eq $ExpectedSha256) -and ($marker.exeSha256 -eq (Get-FileSha256 $exePath))
    }
    catch
    {
        return $false
    }
}

function Install-Server($Lock, [string]$VersionDirectory, [string]$ServerDirectory)
{
    $suffix = [Guid]::NewGuid().ToString('N').Substring(0, 8)
    $archive = Join-Path $VersionDirectory "download-$suffix.mcpb"
    $staging = Join-Path $VersionDirectory "staging-$suffix"
    try
    {
        Write-Log "Downloading Pointframe MCP server $($Lock.version) from $($Lock.url)"
        Invoke-WebRequest -Uri $Lock.url -OutFile $archive -UseBasicParsing

        $expected = ([string]$Lock.sha256).ToLowerInvariant()
        $actual = Get-FileSha256 $archive
        if ($actual -ne $expected)
        {
            throw "SHA-256 mismatch for the downloaded server. Expected $expected but got $actual. The download was discarded and nothing was started."
        }

        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [IO.Compression.ZipFile]::ExtractToDirectory($archive, $staging)
        $exe = Join-Path $staging 'Pointframe.Mcp.exe'
        if (-not (Test-Path -LiteralPath $exe))
        {
            throw 'The verified archive does not contain Pointframe.Mcp.exe.'
        }

        $marker = @{ archiveSha256 = $expected; exeSha256 = (Get-FileSha256 $exe); version = [string]$Lock.version }
        [IO.File]::WriteAllText((Join-Path $staging '.verified.json'), ($marker | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))

        if (Test-Path -LiteralPath $ServerDirectory)
        {
            Remove-Item -LiteralPath $ServerDirectory -Recurse -Force
        }
        try
        {
            Move-Item -LiteralPath $staging -Destination $ServerDirectory
        }
        catch
        {
            # Another start installed the same verified version first; use its copy if it is valid.
            if (-not (Test-CachedServer $ServerDirectory $expected))
            {
                throw
            }
        }
        Write-Log "Verified and cached the server in $ServerDirectory"
    }
    finally
    {
        Remove-Item -LiteralPath $archive -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Remove-OldVersions([string]$CacheRoot, [string]$CurrentVersion)
{
    foreach ($directory in Get-ChildItem -LiteralPath $CacheRoot -Directory -ErrorAction SilentlyContinue)
    {
        if ($directory.Name -ne $CurrentVersion)
        {
            Remove-Item -LiteralPath $directory.FullName -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

function ConvertTo-QuotedArgument([string]$Value)
{
    if ($Value.Length -gt 0 -and $Value -notmatch '[\s"]')
    {
        return $Value
    }
    '"' + (($Value -replace '(\\*)"', '$1$1\"') -replace '(\\+)$', '$1$1') + '"'
}

try
{
    $protocols = [Net.SecurityProtocolType]::Tls12
    try { $protocols = $protocols -bor [Net.SecurityProtocolType]12288 } catch { }
    [Net.ServicePointManager]::SecurityProtocol = $protocols

    $lock = Read-Lock (Join-Path $PSScriptRoot '..\server.lock.json')
    if ([string]::IsNullOrWhiteSpace($env:LOCALAPPDATA))
    {
        throw 'LOCALAPPDATA is not set; this plugin runs on Windows only.'
    }

    $cacheRoot = Join-Path $env:LOCALAPPDATA 'Pointframe\plugin-mcp'
    $versionDirectory = Join-Path $cacheRoot ([string]$lock.version)
    $serverDirectory = Join-Path $versionDirectory 'server'
    New-Item -ItemType Directory -Force -Path $versionDirectory | Out-Null

    $expectedSha256 = ([string]$lock.sha256).ToLowerInvariant()
    if (-not (Test-CachedServer $serverDirectory $expectedSha256))
    {
        Install-Server $lock $versionDirectory $serverDirectory
    }
    Remove-OldVersions $cacheRoot ([string]$lock.version)

    $startInfo = New-Object Diagnostics.ProcessStartInfo
    $startInfo.FileName = Join-Path $serverDirectory 'Pointframe.Mcp.exe'
    $startInfo.Arguments = (@($ServerArguments | ForEach-Object { ConvertTo-QuotedArgument $_ }) -join ' ')
    $startInfo.UseShellExecute = $false
    $process = [Diagnostics.Process]::Start($startInfo)
    $process.WaitForExit()
    exit $process.ExitCode
}
catch
{
    Write-Log "ERROR: $($_.Exception.Message)"
    exit 1
}
