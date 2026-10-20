#Requires -Version 7.0
[CmdletBinding()]
param(
    [string]$Root,
    [switch]$Json
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\lib\root.ps1')
. (Join-Path $PSScriptRoot '..\lib\config.ps1')
. (Join-Path $PSScriptRoot '..\lib\manifest.ps1')

function Add-AwIssue {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [Collections.Generic.List[object]]$Issues,
        [Parameter(Mandatory)]
        [string]$Status,
        [Parameter(Mandatory)]
        [string]$Path,
        [string]$Message
    )

    $issue = [ordered]@{ status = $Status; path = $Path }
    if ($Message) {
        $issue.message = $Message
    }
    $Issues.Add([pscustomobject]$issue)
}

try {
    if ($PSBoundParameters.ContainsKey('Root') -and $Root -and
        -not [IO.Path]::IsPathFullyQualified($Root)) {
        [Console]::Error.WriteLine('doctor: Root must be an absolute path.')
        exit 2
    }

    $resolved = Resolve-AwRoot -Explicit $Root -ScriptRoot $PSScriptRoot
    $issues = [Collections.Generic.List[object]]::new()
    $lockPath = Join-Path $resolved.WorkTree 'agent-workflow.lock.json'
    $managed = Join-Path $resolved.WorkTree 'scripts\agent-workflow'
    if (-not (Test-Path -LiteralPath $lockPath -PathType Leaf)) {
        Add-AwIssue -Issues $issues -Status 'missing' -Path 'agent-workflow.lock.json'
    }
    else {
        try {
            $lock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json -AsHashtable
            if ($lock.schemaVersion -ne 1 -or -not $lock.source -or -not $lock.tag -or
                -not $lock.commit -or -not $lock.manifestSha256 -or
                -not ($lock.files -is [Collections.IDictionary])) {
                throw 'Lock schema is invalid.'
            }
            if ((Get-AwManifestHash -Files $lock.files) -cne $lock.manifestSha256) {
                throw 'Lock manifestSha256 does not match its files map.'
            }

            foreach ($entry in $lock.files.GetEnumerator()) {
                $relative = [string]$entry.Key
                $path = Join-Path $managed ($relative.Replace('/', [IO.Path]::DirectorySeparatorChar))
                $reportPath = "scripts/agent-workflow/$relative"
                if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
                    Add-AwIssue -Issues $issues -Status 'missing' -Path $reportPath
                }
                elseif ((Get-AwFileHash -Path $path) -cne [string]$entry.Value) {
                    Add-AwIssue -Issues $issues -Status 'modified' -Path $reportPath
                }
            }

            $expected = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
            foreach ($path in $lock.files.Keys) {
                $null = $expected.Add([string]$path)
            }
            foreach ($actual in (Get-AwManifest -Root $managed).Keys) {
                if (-not $expected.Contains([string]$actual)) {
                    Add-AwIssue -Issues $issues -Status 'extra' -Path "scripts/agent-workflow/$actual"
                }
            }
        }
        catch {
            Add-AwIssue -Issues $issues -Status 'invalid-lock' `
                -Path 'agent-workflow.lock.json' -Message $_.Exception.Message
        }
    }

    if (-not (Test-AwAttributesBlock -Path (Join-Path $resolved.WorkTree '.gitattributes'))) {
        Add-AwIssue -Issues $issues -Status 'missing-attributes' -Path '.gitattributes'
    }

    try {
        $null = Read-AwConfig -Root $resolved.WorkTree
    }
    catch {
        Add-AwIssue -Issues $issues -Status 'invalid-config' `
            -Path 'agent-workflow.json' -Message $_.Exception.Message
    }

    $result = [pscustomobject][ordered]@{
        root = $resolved.WorkTree
        clean = ($issues.Count -eq 0)
        issues = @($issues)
    }
    if ($Json) {
        $result | ConvertTo-Json -Depth 20 -Compress
    }
    elseif ($result.clean) {
        'doctor: clean'
    }
    else {
        foreach ($issue in $issues) {
            $line = "doctor: $($issue.status): $($issue.path)"
            if ($issue.PSObject.Properties.Name -contains 'message') { $line += " ($($issue.message))" }
            $line
        }
    }

    if ($result.clean) {
        exit 0
    }
    exit 1
}
catch {
    if ($Json) {
        [pscustomobject][ordered]@{ clean = $false; error = $_.Exception.Message; issues = @() } |
            ConvertTo-Json -Compress
    }
    else {
        [Console]::Error.WriteLine("doctor: $($_.Exception.Message)")
    }
    exit 2
}
