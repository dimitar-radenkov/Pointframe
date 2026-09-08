[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateScript({ Test-Path $_ -PathType Leaf })]
    [string]$ExecutablePath,
    [switch]$SkipEnabledDiscovery
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Invoke-McpDiscovery {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ResolvedExecutablePath,
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [string[]]$Arguments,
        [Parameter(Mandatory = $true)]
        [string[]]$ExpectedTools,
        [Parameter(Mandatory = $true)]
        [string]$DiscoveryName
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $ResolvedExecutablePath
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardInput = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in $Arguments)
    {
        [void]$startInfo.ArgumentList.Add($argument)
    }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $started = $false
    try
    {
        if (-not $process.Start())
        {
            throw "Failed to start MCP executable for $DiscoveryName discovery."
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
                throw "MCP $DiscoveryName discovery timed out after receiving $($responses.Count) response(s)."
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
                throw "MCP stdout contained invalid JSON during $DiscoveryName discovery: $line"
            }
        }

        if ($responses.Count -lt 2)
        {
            $stderr = $process.StandardError.ReadToEnd()
            throw "MCP $DiscoveryName discovery did not return initialize and tools/list. stderr: $stderr"
        }

        $initializeResponse = $responses | Where-Object { $_.id -eq 1 } | Select-Object -First 1
        $toolsResponse = $responses | Where-Object { $_.id -eq 2 } | Select-Object -First 1
        if ($null -eq $initializeResponse -or $null -eq $toolsResponse)
        {
            throw "MCP $DiscoveryName discovery did not receive both expected response IDs."
        }

        $actualTools = @($toolsResponse.result.tools | ForEach-Object { $_.name })
        $unexpectedTools = @($actualTools | Where-Object { $ExpectedTools -notcontains $_ })
        $missingTools = @($ExpectedTools | Where-Object { $actualTools -notcontains $_ })
        if ($actualTools.Count -ne $ExpectedTools.Count -or $unexpectedTools.Count -gt 0 -or $missingTools.Count -gt 0)
        {
            throw "MCP $DiscoveryName discovery returned an unexpected exact tool set: $($actualTools -join ', ')."
        }

        Write-Host "MCP $DiscoveryName discovery passed: exact delivered tool set returned."
    }
    finally
    {
        if ($started -and -not $process.HasExited)
        {
            $process.Kill()
        }

        $process.Dispose()
    }
}

$resolvedExecutablePath = (Resolve-Path $ExecutablePath).Path
$expectedTools = @("list_displays", "capture_monitor", "read_text_from_monitor", "start_recording", "stop_recording", "get_recording_status")
Invoke-McpDiscovery -ResolvedExecutablePath $resolvedExecutablePath -Arguments @() -ExpectedTools $expectedTools -DiscoveryName "disabled"

if (-not $SkipEnabledDiscovery)
{
    $enabledExpectedTools = $expectedTools + @(
        "list_apps", "start_test_session", "restart_app", "observe_app", "focus_window",
        "click", "press_keys", "drag", "enter_text", "invoke", "check_ui", "scroll",
        "get_action_result", "get_test_report", "end_test_session"
    )
    $policyPath = Join-Path (Split-Path $resolvedExecutablePath -Parent) "desktop-testing-policy.test.json"
    $policy = @{
        schemaVersion = 1
        artifactRoot = (Split-Path $resolvedExecutablePath -Parent)
        evidencePolicy = "Failures"
        profiles = @(
            @{
                id = "mcp-smoke"
                executablePath = $resolvedExecutablePath
                arguments = @()
                workingDirectory = (Split-Path $resolvedExecutablePath -Parent)
                allowAttach = $false
                allowedActions = @("ListApps", "StartTestSession", "ObserveApp", "FocusWindow", "Click", "PressKeys", "CheckUi", "GetActionResult", "GetTestReport", "EndTestSession")
                allowedGlobalHotkeys = @{}
                allowedShellSurfaces = @("NotificationArea", "NotificationOverflow")
                allowMonitorObservation = $true
            }
        )
    }

    try
    {
        $policy | ConvertTo-Json -Depth 10 | Set-Content $policyPath -Encoding utf8NoBOM
        Invoke-McpDiscovery -ResolvedExecutablePath $resolvedExecutablePath `
            -Arguments @("--desktop-testing", "--desktop-policy", $policyPath) `
            -ExpectedTools $enabledExpectedTools `
            -DiscoveryName "enabled"
    }
    finally
    {
        if (Test-Path $policyPath)
        {
            Remove-Item $policyPath -Force
        }
    }
}
