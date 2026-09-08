[CmdletBinding()]
param(
    [string]$Version = "1.0.0",
    [string]$FfmpegPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path $PSScriptRoot -Parent
$projectPath = Join-Path $repositoryRoot "Pointframe.Cli\Pointframe.Cli.csproj"
$publishDirectory = Join-Path $repositoryRoot "Pointframe.Cli\bin\publish\win-x64"
$packageDirectory = Join-Path $repositoryRoot "packaging\output\Pointframe.Cli-$Version-win-x64"
$archivePath = "$packageDirectory.zip"
$checksumPath = "$archivePath.sha256"

dotnet publish $projectPath `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    /p:PublishSingleFile=true `
    /p:PublishReadyToRun=false `
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

if ($FfmpegPath)
{
    if (-not (Test-Path $FfmpegPath -PathType Leaf))
    {
        throw "FfmpegPath '$FfmpegPath' was not found."
    }

    Copy-Item $FfmpegPath (Join-Path $packageDirectory "ffmpeg.exe") -Force
}
else
{
    Write-Warning "No -FfmpegPath supplied: the packaged CLI's 'record' command will only work if ffmpeg.exe is on PATH or POINTFRAME_FFMPEG_PATH is set on the target machine."
}

if (Test-Path $archivePath)
{
    Remove-Item $archivePath -Force
}

Compress-Archive -Path (Join-Path $packageDirectory "*") -DestinationPath $archivePath
$sha256 = (Get-FileHash $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content $checksumPath "$sha256  $(Split-Path $archivePath -Leaf)" -Encoding utf8NoBOM

Write-Host "CLI ZIP ready: $archivePath"
Write-Host "CLI checksum ready: $checksumPath"
