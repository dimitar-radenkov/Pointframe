#Requires -Version 7.0
<#
Public entry point of the knowledge base tool: forwards every argument unchanged to the vendored kit tool
(scripts/agent-workflow/tools/kb.ps1) with this repository as -Root, unless the caller gave -Root, and returns its
exit code. The tool, its fixtures, and its workflow and skill templates come from the agent workflow kit.
#>
$tool = Join-Path $PSScriptRoot 'agent-workflow' 'tools' 'kb.ps1'
if (@($args | Where-Object { $_ -is [string] -and $_ -match '^-Root(:|$)' }))
{
    & $tool @args
}
else
{
    $root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
    & $tool @args -Root $root
}
exit $LASTEXITCODE