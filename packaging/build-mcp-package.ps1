[CmdletBinding()]
param(
    [string]$Version = "1.0.0",
    [string]$RepositoryUrl = "https://github.com/dimitar-radenkov/Pointframe",
    [Parameter(Mandatory = $true)]
    [ValidateScript({ Test-Path $_ -PathType Leaf })]
    [string]$FfmpegPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path $PSScriptRoot -Parent
$projectPath = Join-Path $repositoryRoot "Pointframe.Mcp\Pointframe.Mcp.csproj"
$publishDirectory = Join-Path $repositoryRoot "Pointframe.Mcp\bin\publish\win-x64"
$packageDirectory = Join-Path $repositoryRoot "packaging\output\Pointframe.Mcp-$Version-win-x64"
$archivePath = "$packageDirectory.zip"
$mcpbPath = "$packageDirectory.mcpb"
$checksumPath = "$packageDirectory.mcpb.sha256"
$serverJsonPath = "$packageDirectory.server.json"
$serverName = "io.github.dimitar-radenkov/pointframe-mcp"

dotnet publish $projectPath `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    /p:PublishSingleFile=true `
    /p:PublishReadyToRun=true `
    /p:EnableCompressionInSingleFile=true `
    /p:PublishTrimmed=false `
    /p:PublishDir="$publishDirectory\" `
    /p:Version=$Version `
    --nologo
if ($LASTEXITCODE -ne 0)
{
    exit $LASTEXITCODE
}

if (Test-Path $packageDirectory)
{
    Remove-Item $packageDirectory -Recurse -Force
}

New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null
Copy-Item (Join-Path $publishDirectory "*") $packageDirectory -Recurse -Force
Copy-Item $FfmpegPath (Join-Path $packageDirectory "ffmpeg.exe") -Force

@{
    manifest_version = "0.3"
    name = "pointframe-mcp"
    display_name = "Pointframe MCP Server"
    version = $Version
    description = "Local Windows MCP server for Pointframe monitor discovery, screenshots, and recordings."
    long_description = "Runs as a local stdio MCP server in an interactive Windows desktop session. Captures and recordings are written to the user's local Pointframe data directory."
    author = @{
        name = "Dimitar Radenkov"
        url = $RepositoryUrl
    }
    repository = @{
        type = "git"
        url = "$RepositoryUrl.git"
    }
    homepage = $RepositoryUrl
    documentation = "$RepositoryUrl/blob/master/README.md"
    support = "$RepositoryUrl/issues"
    license = "MIT"
    keywords = @("mcp", "screenshots", "screen-recording", "windows", "pointframe")
    compatibility = @{
        platforms = @("win32")
    }
    server = @{
        type = "binary"
        entry_point = "Pointframe.Mcp.exe"
        mcp_config = @{
            command = '${__dirname}/Pointframe.Mcp.exe'
            args = @()
        }
    }
    tools = @(
        @{ name = "list_displays"; description = "List available Windows displays." },
        @{ name = "capture_monitor"; description = "Capture a monitor to a PNG artifact." },
        @{ name = "start_recording"; description = "Start a monitor recording with optional pixelation regions." },
        @{ name = "stop_recording"; description = "Stop the active recording and return its artifacts." }
    )
    tools_generated = $true
} | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $packageDirectory "manifest.json") -Encoding utf8NoBOM

if (Test-Path $archivePath)
{
    Remove-Item $archivePath -Force
}

Compress-Archive -Path (Join-Path $packageDirectory "*") -DestinationPath $archivePath
if (Test-Path $mcpbPath)
{
    Remove-Item $mcpbPath -Force
}

Compress-Archive -Path (Join-Path $packageDirectory "*") -DestinationPath $mcpbPath
$sha256 = (Get-FileHash $mcpbPath -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content $checksumPath "$sha256  $(Split-Path $mcpbPath -Leaf)" -Encoding utf8NoBOM

$releaseUrl = "$RepositoryUrl/releases/download/v$Version/$(Split-Path $mcpbPath -Leaf)"
@{
    '$schema' = "https://static.modelcontextprotocol.io/schemas/2025-12-11/server.schema.json"
    name = $serverName
    title = "Pointframe MCP Server"
    description = "Local Windows MCP server for monitor discovery, screenshots, and recordings."
    websiteUrl = $RepositoryUrl
    repository = @{
        url = "$RepositoryUrl"
        source = "github"
    }
    version = $Version
    packages = @(
        @{
            registryType = "mcpb"
            identifier = $releaseUrl
            fileSha256 = $sha256
            transport = @{
                type = "stdio"
            }
        }
    )
} | ConvertTo-Json -Depth 10 | Set-Content $serverJsonPath -Encoding utf8NoBOM

Write-Host "MCP ZIP ready: $archivePath"
Write-Host "MCPB bundle ready: $mcpbPath"
Write-Host "MCP Registry metadata ready: $serverJsonPath"