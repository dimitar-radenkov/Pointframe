#Requires -Version 7.0
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Helpers for the offline self-tests of the tools: a minimal valid agent-workflow.json for a temporary repository.

function Get-AwSelfTestConfig {
    param(
        [string]$Namespace = 'selftest',
        [string]$BaseRef = 'origin/master',
        [string[]]$PinFiles = @(),
        [string]$PinHook
    )

    $role = { param($provider, $model, $sandbox) [ordered]@{ provider = $provider; model = $model; effort = 'medium'; sandbox = $sandbox } }
    return [ordered]@{
        schemaVersion = 1
        repository = [ordered]@{ id = 'selftest'; baseRef = $BaseRef }
        state = [ordered]@{ commonDirNamespace = $Namespace }
        kb = [ordered]@{
            root = 'docs'; core = 'docs/core.md'; features = 'docs/features'; lessons = 'lessons.md'
            featurePrefix = 'F-'; decisionPrefix = 'D-'; baseline = 'docs/baseline.json'; tasks = 'docs/tasks.json'
        }
        status = [ordered]@{ store = 'plan/status.json'; issue = $null; handoff = 'plan/handoff.md' }
        merge = [ordered]@{ requiredChecks = @('unit-tests') }
        gates = @()
        roles = [ordered]@{
            coordinator = (& $role 'claude' 'opus' 'read-only')
            executor = (& $role 'openai' 'gpt-6-luna' 'workspace-write')
            lightReviewer = (& $role 'openai' 'gpt-6-luna' 'read-only')
            heavyReviewer = [ordered]@{ provider = 'openai'; model = 'gpt-6.1-sol'; effort = 'high'; sandbox = 'read-only' }
        }
        hooks = [ordered]@{
            environment = $null; kbCoverage = $null
            pin = $(if ($PinHook) { $PinHook } else { $null })
            qualify = $null; postMerge = $null
        }
        pinException = $(if ($PinFiles.Count -gt 0) { [ordered]@{ paths = @($PinFiles) } } else { $null })
    }
}

function Write-AwSelfTestConfig {
    param(
        [Parameter(Mandatory)]
        [string]$Directory,
        [Parameter(Mandatory)]
        [Collections.IDictionary]$Config
    )

    $text = ($Config | ConvertTo-Json -Depth 20) + "`n"
    [IO.File]::WriteAllText((Join-Path $Directory 'agent-workflow.json'), $text, [Text.UTF8Encoding]::new($false))
}
