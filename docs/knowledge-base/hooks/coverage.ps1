#Requires -Version 7.0
<#
hooks.kbCoverage of agent-workflow.json: the Pointframe-specific coverage checks of `kb.ps1 check`. Every product
telemetry event (the TelemetryEvents catalog) and every smoke test must be claimed by a feature row, and a feature
row may only name events the catalog defines. The kit passes the repository as -Root and a JSON document on standard
input ({root, tracked, features: [{id, name, where, events, entryPaths, testPaths}]}); each finding is one line,
"error: <text>". Moved from scripts/kb.ps1.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Root
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$context = [Console]::In.ReadToEnd() | ConvertFrom-Json
$features = @($context.features)
$tracked = @($context.tracked)

$catalogPath = Join-Path $Root 'Pointframe' 'Services' 'Infrastructure' 'TelemetryEventCatalog.cs'
if (Test-Path $catalogPath)
{
    $eventsBlock = [regex]::Match([System.IO.File]::ReadAllText($catalogPath), '(?s)public static class TelemetryEvents\s*\{(.*?)\n\}').Groups[1].Value
    $catalogEvents = @([regex]::Matches($eventsBlock, 'const string \w+ = "([a-z0-9_]+)"') | ForEach-Object { $_.Groups[1].Value })
    $claimed = @{}
    foreach ($f in $features)
    {
        foreach ($ev in @($f.events))
        {
            $claimed[$ev] = $true
            if ($catalogEvents -notcontains $ev)
            {
                "error: $($f.where): feature $($f.id) names telemetry event '$ev', which TelemetryEvents does not define"
            }
        }
    }
    foreach ($ev in $catalogEvents | Where-Object { -not $claimed.ContainsKey($_) })
    {
        "error: telemetry event '$ev' belongs to no feature; add it to a feature row"
    }
}
$claimedTests = @($features | ForEach-Object { $_.testPaths })
foreach ($t in $tracked | Where-Object { $_ -like 'Pointframe.AutomationTests/Smoke/*.cs' -and $claimedTests -notcontains $_ })
{
    "error: smoke test $t belongs to no feature; add it to a feature row"
}
