#Requires -Version 7.0
<#
hooks.qualify of agent-workflow.json: the desktop qualification of a package ship, run by the kit's package.ps1 (ship, step qualify).
Moved from Step-ShipQualify of scripts/package.ps1; it calls the release scripts that stay in this repository.

  qualify.ps1 -Action Impact -Root <worktree> -MainRoot <main checkout> -Base <ref> -Head <sha>
      Decides from the diff whether the packaged CLI and MCP server must run on the desktop. Calls scripts/release-impact.ps1 of the MAIN
      checkout (a package cannot change the rules that decide whether it needs qualification) in the worktree. Its lines are passed
      through: exactly one "desktop-qualification: required|not-required" line, "[rule: ...]" lines for the matched paths; exit 0.
  qualify.ps1 -Action Run -Root <worktree> -MainRoot <main checkout> -WorkDirectory <folder>
      Builds the candidate packages of the worktree and runs the desktop check on them: scripts/check-released-desktop.ps1 -BuildCandidate
      of the WORKTREE (the candidate's own check), keeping the work folder. Exit 0 passed. It writes <folder>/result.json (status
      passed|passed_with_skips|failed, packages [{name, sha256}], steps [{name, status, detail}]) and the candidate files in
      <folder>/candidate/, which is what the kit records and re-hashes before a qualification is reused.

The kit runs it in a child process with the worktree as the working folder. A null hook means the repository has no qualification.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Impact', 'Run')]
    [string]$Action,
    [Parameter(Mandatory)]
    [string]$Root,
    [Parameter(Mandatory)]
    [string]$MainRoot,
    [string]$Base,
    [string]$Head,
    [string]$WorkDirectory
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest

try
{
    switch ($Action)
    {
        'Impact'
        {
            if (-not $Base -or -not $Head) { throw '-Action Impact needs -Base and -Head' }
            $script = Join-Path $MainRoot 'scripts' 'release-impact.ps1'
            if (-not (Test-Path -LiteralPath $script -PathType Leaf)) { throw "the release impact script does not exist: $script" }
            & pwsh -NoProfile -File $script -Base $Base -Head $Head
            exit $LASTEXITCODE
        }
        'Run'
        {
            if (-not $WorkDirectory) { throw '-Action Run needs -WorkDirectory' }
            $script = Join-Path $Root 'scripts' 'check-released-desktop.ps1'
            if (-not (Test-Path -LiteralPath $script -PathType Leaf)) { throw "the candidate desktop check does not exist: $script" }
            & pwsh -NoProfile -File $script -BuildCandidate -WorkDirectory $WorkDirectory -KeepWorkDirectory
            exit $LASTEXITCODE
        }
    }
}
catch
{
    Write-Output $_.Exception.Message
    exit 2
}
