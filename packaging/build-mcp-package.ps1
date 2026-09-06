[CmdletBinding()]
param(
    [string]$Version = "1.0.0",
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

dotnet publish $projectPath /p:PublishProfile=win-x64 /p:Version=$Version --nologo
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

if (Test-Path $archivePath)
{
    Remove-Item $archivePath -Force
}

Compress-Archive -Path (Join-Path $packageDirectory "*") -DestinationPath $archivePath
Write-Host "MCP package ready: $archivePath"