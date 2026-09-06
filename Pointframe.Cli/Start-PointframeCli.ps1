param(
    [string]$Configuration = "Debug",
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$CliArguments
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$previousPipeName = $env:POINTFRAME_AGENT_BRIDGE_PIPE
$previousSecret = $env:POINTFRAME_AGENT_BRIDGE_SECRET
$env:POINTFRAME_AGENT_BRIDGE_PIPE = "pointframe-agent-$([guid]::NewGuid().ToString('N'))"
$secretBytes = [byte[]]::new(32)
[System.Security.Cryptography.RandomNumberGenerator]::Fill($secretBytes)
$env:POINTFRAME_AGENT_BRIDGE_SECRET = [Convert]::ToHexString($secretBytes)
$pointframeAssemblyPath = Join-Path $repositoryRoot "Pointframe/bin/$Configuration/net10.0-windows10.0.18362.0/Pointframe.dll"
$pointframe = $null

try
{
    if (-not (Test-Path $pointframeAssemblyPath))
    {
        & dotnet build "$repositoryRoot/Pointframe/Pointframe.csproj" --configuration $Configuration
    }

    $pointframe = Start-Process dotnet -WorkingDirectory $repositoryRoot -ArgumentList @(
        $pointframeAssemblyPath,
        "--agent-bridge"
    ) -PassThru

    & dotnet run --project "$repositoryRoot/Pointframe.Cli/Pointframe.Cli.csproj" --configuration $Configuration -- @CliArguments
    if ($LASTEXITCODE -ne 0)
    {
        exit $LASTEXITCODE
    }
}
finally
{
    if ($null -ne $pointframe -and -not $pointframe.HasExited)
    {
        Stop-Process -Id $pointframe.Id
    }

    if ($null -eq $previousPipeName)
    {
        Remove-Item Env:POINTFRAME_AGENT_BRIDGE_PIPE -ErrorAction SilentlyContinue
    }
    else
    {
        $env:POINTFRAME_AGENT_BRIDGE_PIPE = $previousPipeName
    }

    if ($null -eq $previousSecret)
    {
        Remove-Item Env:POINTFRAME_AGENT_BRIDGE_SECRET -ErrorAction SilentlyContinue
    }
    else
    {
        $env:POINTFRAME_AGENT_BRIDGE_SECRET = $previousSecret
    }
}