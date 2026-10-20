#Requires -Version 7.0
<#
Public entry point of the workflow script checker: forwards every argument unchanged to the vendored kit tool
(scripts/agent-workflow/tools/check-workflow-scripts.ps1) with this repository as -Root, unless the caller gave -Root, and
returns its exit code. Standard input passes through. A failure before the tool runs (a parameter that does not bind) is
terminating here and exits 1. The tool and its libraries come from the agent workflow kit.
#>
$ErrorActionPreference = 'Stop'
$tool = Join-Path $PSScriptRoot 'agent-workflow' 'tools' 'check-workflow-scripts.ps1'
$global:LASTEXITCODE = 0
try
{
    if (@($args | Where-Object { $_ -is [string] -and $_ -match '^-Root(:|$)' }))
    {
        & $tool @args
    }
    else
    {
        $root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
        & $tool @args -Root $root
    }
}
catch
{
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
exit $LASTEXITCODE