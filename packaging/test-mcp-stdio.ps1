[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateScript({ Test-Path $_ -PathType Leaf })]
    [string]$ExecutablePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$startInfo = [System.Diagnostics.ProcessStartInfo]::new()
$startInfo.FileName = (Resolve-Path $ExecutablePath).Path
$startInfo.UseShellExecute = $false
$startInfo.CreateNoWindow = $true
$startInfo.RedirectStandardInput = $true
$startInfo.RedirectStandardOutput = $true
$startInfo.RedirectStandardError = $true

$process = [System.Diagnostics.Process]::new()
$process.StartInfo = $startInfo
$started = $false
try
{
    if (-not $process.Start())
    {
        throw "Failed to start MCP executable."
    }

    $started = $true

    $initialize = @{
        jsonrpc = "2.0"
        id = 1
        method = "initialize"
        params = @{
            protocolVersion = "2025-11-25"
            capabilities = @{}
            clientInfo = @{
                name = "Pointframe CI smoke test"
                version = "1.0.0"
            }
        }
    } | ConvertTo-Json -Compress -Depth 10

    $listTools = @{
        jsonrpc = "2.0"
        id = 2
        method = "tools/list"
        params = @{}
    } | ConvertTo-Json -Compress -Depth 10

    $initialized = @{
        jsonrpc = "2.0"
        method = "notifications/initialized"
        params = @{}
    } | ConvertTo-Json -Compress -Depth 10

    $process.StandardInput.WriteLine($initialize)
    $process.StandardInput.WriteLine($initialized)
    $process.StandardInput.WriteLine($listTools)
    $process.StandardInput.Flush()

    $responses = @()
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    while ($responses.Count -lt 2 -and [DateTime]::UtcNow -lt $deadline)
    {
        $remainingMilliseconds = [Math]::Max(1, [int]($deadline - [DateTime]::UtcNow).TotalMilliseconds)
        $readTask = $process.StandardOutput.ReadLineAsync()
        if (-not $readTask.Wait($remainingMilliseconds))
        {
            throw "MCP smoke test timed out waiting for stdout after receiving $($responses.Count) response(s)."
        }

        $line = $readTask.Result
        if ($null -eq $line)
        {
            break
        }

        try
        {
            $responses += $line | ConvertFrom-Json
        }
        catch
        {
            throw "MCP stdout contained invalid JSON: $line"
        }
    }

    if ($responses.Count -lt 2)
    {
        $stderr = $process.StandardError.ReadToEnd()
        throw "MCP smoke test timed out before receiving initialize and tools/list responses. stderr: $stderr"
    }

    $initializeResponse = $responses | Where-Object { $_.id -eq 1 } | Select-Object -First 1
    $toolsResponse = $responses | Where-Object { $_.id -eq 2 } | Select-Object -First 1
    if ($null -eq $initializeResponse -or $null -eq $toolsResponse)
    {
        throw "MCP smoke test did not receive both expected response IDs."
    }

    $toolNames = @($toolsResponse.result.tools | ForEach-Object { $_.name })
    $expectedTools = @("list_displays", "capture_monitor", "start_recording", "stop_recording")
    foreach ($expectedTool in $expectedTools)
    {
        if ($toolNames -notcontains $expectedTool)
        {
            throw "MCP tools/list did not contain expected tool '$expectedTool'."
        }
    }

    Write-Host "MCP stdio smoke test passed: initialize and tools/list returned all expected tools."
}
finally
{
    if ($started -and -not $process.HasExited)
    {
        $process.Kill()
    }

    $process.Dispose()
}
