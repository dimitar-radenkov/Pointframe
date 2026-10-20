#Requires -Version 7.0
<#
Public entry point of the knowledge base tool: forwards every argument unchanged to the vendored kit tool
(scripts/agent-workflow/tools/kb.ps1) with this repository as -Root, unless the caller gave -Root, and returns its
exit code. A failure before the tool runs (a parameter that does not bind) is terminating here and exits 1, as the
tool itself did. The tool, its fixtures, and its workflow and skill templates come from the agent workflow kit.
#>
$ErrorActionPreference = 'Stop'
$tool = Join-Path $PSScriptRoot 'agent-workflow' 'tools' 'kb.ps1'
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