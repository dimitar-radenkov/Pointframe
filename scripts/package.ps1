#Requires -Version 7.0
<#
Public entry point of the work package runner: forwards its arguments to the vendored kit tool
(scripts/agent-workflow/tools/package.ps1) with this repository as -Root (for the gate tools, its main checkout), unless the caller already named the repository
(Root). Standard input passes through and the tool's exit code is returned. A failure before the tool runs
(a parameter that does not bind) is terminating here and exits 1.
Generated from the kit template templates/wrapper.ps1: `doctor.ps1` compares it with the template and
`doctor.ps1 -WriteWrappers` rewrites it. Do not edit it by hand.
The arguments are bound against the parameters of the tool, not scanned as text: `-Switch:$false` is a switch set to
false, and a value that happens to read like a parameter (`-Requirement -Root`) stays a value.
#>
$ErrorActionPreference = 'Stop'
$tool = Join-Path $PSScriptRoot 'agent-workflow' 'tools' 'package.ps1'
$selectors = @('Root' -split ',')
$rootKind = 'main'
$global:LASTEXITCODE = 0
try
{
    $given = @($args)
    $parameters = (Get-Command -Name $tool -CommandType ExternalScript).Parameters
    $known = @{}
    foreach ($entry in $parameters.GetEnumerator())
    {
        $known[$entry.Key] = $entry.Key
        foreach ($alias in $entry.Value.Aliases) { $known[$alias] = $entry.Key }
    }
    $named = [ordered]@{}
    $positional = [Collections.Generic.List[object]]::new()
    $understood = $true
    for ($i = 0; $i -lt $given.Count; $i++)
    {
        $token = $given[$i]
        if ($token -isnot [string] -or $token -notmatch '^-([A-Za-z_][A-Za-z0-9_]*)(:(.*))?$')
        {
            $positional.Add($token)
            continue
        }
        $typed = $Matches[1]
        # `-Name:value` arrives as one token or as `-Name:` followed by the value, depending on how PowerShell split it.
        $hasInline = $Matches.ContainsKey(2)
        $inline = if ($hasInline) { [string]$Matches[3] } else { '' }
        if ($hasInline -and $inline -eq '')
        {
            if ($i + 1 -ge $given.Count)
            {
                throw "Missing an argument for parameter '$typed'. Specify a value and try again."
            }
            $i++
            $inline = [string]$given[$i]
        }
        $canonical = $known[$typed]
        if (-not $canonical)
        {
            $matching = @($known.Keys | Where-Object { $_.StartsWith($typed, [StringComparison]::OrdinalIgnoreCase) } |
                    ForEach-Object { $known[$_] } | Select-Object -Unique)
            if ($matching.Count -eq 1) { $canonical = $matching[0] }
        }
        if (-not $canonical)
        {
            # A parameter this tool does not have: the tool reports it, exactly as when it is run directly.
            $understood = $false
            break
        }
        if ($named.Contains($canonical))
        {
            throw "Cannot bind parameter because parameter '$canonical' is specified more than once."
        }
        if ($parameters[$canonical].ParameterType -eq [switch])
        {
            $on = $true
            if (-not $hasInline -and $i + 1 -lt $given.Count -and $given[$i + 1] -is [bool])
            {
                # `pwsh -File wrapper.ps1 -Switch:false` reaches the wrapper as `-Switch` followed by a Boolean.
                $i++
                $on = [bool]$given[$i]
            }
            elseif ($hasInline)
            {
                if ($inline -match '^(?i:\$?true|1)$') { $on = $true }
                elseif ($inline -match '^(?i:\$?false|0)$') { $on = $false }
                else { throw "Cannot convert value '$inline' to type 'System.Management.Automation.SwitchParameter'." }
            }
            $named[$canonical] = [switch]::new($on)
        }
        elseif ($hasInline)
        {
            $named[$canonical] = $inline
        }
        elseif ($i + 1 -lt $given.Count)
        {
            $i++
            $named[$canonical] = $given[$i]
        }
        else
        {
            throw "Missing an argument for parameter '$canonical'. Specify a value and try again."
        }
    }
    if (-not $understood)
    {
        & $tool @args
    }
    else
    {
        if (-not @($selectors | Where-Object { $named.Contains($_) }))
        {
            $here = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
            if ($rootKind -eq 'main')
            {
                # The merge gate reads its configuration from the main checkout only: from a linked worktree name that checkout.
                $first = @(& git -C $here worktree list --porcelain 2>$null | Where-Object { $_ -match '^worktree ' } | Select-Object -First 1)
                if ($LASTEXITCODE -eq 0 -and $first.Count -eq 1) { $here = [IO.Path]::GetFullPath(([string]$first[0]).Substring(9)) }
            }
            $named['Root'] = $here
        }
        $values = $positional.ToArray()
        & $tool @values @named
    }
}
catch
{
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
exit $LASTEXITCODE
