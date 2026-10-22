#Requires -Version 7.0
<#
Mechanics of one work package for a Codex or Claude executor, with durable state, the runner's commit and a reviewed,
host-checked acceptance, and `ship`, which takes an accepted head to a merged pull request (or, in a repository without a remote, to a
fast-forward of the base branch). merge-pr.ps1 keeps the merge gate.

  pwsh scripts/package.ps1 help
  pwsh scripts/package.ps1 ship -Id <slug> [-Title <text>] [-AllowDesktop] [-NoWait] [-LocalMerge]
  pwsh scripts/package.ps1 commit -Id <slug> [-Message <text>]
  pwsh scripts/package.ps1 review -Id <slug> -Level light|heavy [-Reviewer codex-sol|codex-luna|claude-opus|claude-sonnet] [-ResetBudget -Reason <text>]
  pwsh scripts/package.ps1 disposition -Id <slug> -Finding <id> -Accept|-Reject -Reason <text>
  pwsh scripts/package.ps1 fix -Id <slug> [-Executor same] [-ResetBudget -Reason <text>] [-Findings <file>]
  pwsh scripts/package.ps1 accept -Id <slug> -Reason <text>
  pwsh scripts/package.ps1 new -Id <slug> -Brief <file> [-Checks <manifest.json>] [-DryRun]
  pwsh scripts/package.ps1 run -Id <slug> [-Executor codex|claude-sonnet|claude-haiku] [-Effort medium|high -EscalationReason <text>] [-MaxMinutes 30]
  pwsh scripts/package.ps1 run -Id <slug> -Executor claude-sonnet -Resume -PromptFile <fix-request.md>
  pwsh scripts/package.ps1 check -Id <slug>
  pwsh scripts/package.ps1 show -Id <slug> [-Json]
  pwsh scripts/package.ps1 recover -Id <slug> -Reason <text>
  pwsh scripts/package.ps1 -SelfTest [-Quick]

help    (and a bare call without a command) prints these lines and exits 0; a command without -Id prints them after the error, exit 2.
new     snapshots the brief and the check manifest (SHA-256, read-only), creates branch pkg/<slug> and its worktree from
        the fetched origin/master (a repository without a remote, "repository": { "remote": null }, branches from the local base branch of
        repository.baseRef and fetches nothing), preflight-builds the check targets, validates the brief with agent-run.ps1. It warns (and never refuses) about an
        acceptance case that names only a command run ("tests pass") or the executor's report ("stated in the report"): a reviewer cannot run
        commands and cannot trust a report, so such a case needs something it can observe (a file, the diff, a gate of the check summary).
run     launches the Codex executor through agent-run.ps1 under a hard wall-clock bound, and fingerprints the main checkout
        before and after. A changed fingerprint stops the run (mainCheckoutMutated); nothing is ever reverted.
        -Executor claude-sonnet|claude-haiku launches `claude -p` through the same suspended job-object launch: the working
        directory is the worktree, stdin is the prompt file (then EOF), stdout is the stream-json log, settings come from a
        file in the package state directory (an allowlist, default permission mode, never a bypass), MCP is empty and strict.
        Permission denials in the stream are recorded and never retried. -MaxMinutes and -MaxInputTokens are enforced from
        outside (job kill) and are cumulative across -Resume runs of the same Claude session. claude-haiku needs a
        `Haiku-scope: helper|mechanical` header line in the brief; helper may change nothing, mechanical only the manifest's
        allowedPaths.
check   runs the manifest's checks in the worktree as the runner. Mutating steps run first (extra commands marked "mutates",
        dotnet format, CRLF normalization), then build, focused tests and the other extra commands. Every command records
        the content hash of the worktree it ran on; a result is fresh only while the worktree still has that hash. A tracked file that git
        sees as unchanged but whose raw bytes differ from what a checkout of it writes (an edit and revert hidden by autocrlf or
        assume-unchanged) fails the check as a named problem with the file list; nothing is rewritten.
show    prints stage, runs, checks (stale or incomplete evidence is reported), the guard and the allowed next steps.
commit  coordinator step after a fresh passing check: commits the package changes on pkg/<slug> (trailer "Package: <slug> run
        <runId>"), fetches and rebases onto the base ref, and re-runs check when the rebase changed the tree. Executors never commit.
review  runs a read-only reviewer of the other provider (Codex through codex-ask.ps1 -Sandbox read-only, Claude through claude -p
        with Read/Glob/Grep only and no shell) on exactly the committed head. The reviewer's final message is saved as
        the verdict file; the host validates it against the kit's review/package-verdict.schema.json and checks it against the observed head,
        tree, clean worktree, the brief's criteria and a fresh check. Anything else is recorded as invalid, never PASS. The reviewer
        also gets the fresh check summary (check-summary.json) and the executor's final report (executor-report.md) as hashed inputs.
        Each review round counts against a persisted budget of 3. A Codex reviewer resumes its session in later rounds; a Claude reviewer
        starts fresh every round, with the previous verdict and findings in the prompt; both must return a full verdict. A Claude CLI older
        than the minimum its model needs (package.minCliVersions of the configuration over the kit table) refuses the round before it is
        spent. A round that is invalid because of a host-detected environment failure (the reviewer could not be started or crashed before
        any verdict, an interrupted wrapper) is refunded; the refund and its reason are recorded and shown. Refunds are capped at two per
        package: a third refundable failure is charged, with the reason recorded (REFUND DENIED), and show prints "n refund(s) of 2 allowed".
disposition  the coordinator's accept or reject (with a reason) of one finding of the latest review.
fix     builds a fix request from the accepted findings, resumes the same executor (Claude -Resume; Codex: a new run with the
        previous report appended), then runs check, commit and review again. A FAIL verdict that carries no finding at all has nothing to
        accept: -Findings <file> (a JSON array of {file, line, summary, scenario[, id, severity]} the coordinator wrote) is recorded,
        hashed and used as the findings of that fix.
recover the audited way out of blocked-main-mutated: -Reason is required. It records the reason, the main checkout as it is now and the
        guard it clears, invalidates every check, review PASS and the acceptance, and returns the package to the stage after its last run
        (check, commit and review start again). It also moves every review record written for the package's commits to
        <git common dir>/<namespace>/reviews/invalidated/ (where merge-pr.ps1 does not look) and records the moves in the recovery entry.
        It never clears the guard silently and reverts nothing.
accept  the coordinator's recorded acceptance: only for a valid PASS bound to the current head and tree; writes the review record
        through record-review.ps1 from values in the state. A later change to the head invalidates it (the plugin pin files excepted).
ship    resumable, one repository-wide ship lock (git common dir <namespace>/ship.lock; a dead ship's leftover ship.json is reported
        and only the same package may resume it). Needs stage accepted with the acceptance bound to the current head, tree and a
        clean worktree, and a main checkout that is at the base ref after ship's fetch (the gate configuration is read from it; a stale one is
        refused and the refusal names merge-pr.ps1 -SyncMain). Ship runs the kit tools of the runner itself (verify, merge-pr, record-review),
        not the ones of the package worktree: a package that upgrades the vendored kit is verified by the new kit at its next run, after that
        kit is installed. Steps, each recorded in state before the next starts (stages shipping, ship-failed, desktop-required,
        auto-merge-enabled, merged, needs-review): base (fetch, rebase onto the base ref; a proven no-op keeps the acceptance, any
        other change invalidates the PASS and the acceptance, re-checks and stops at needs-review: never restamped), pin
        (merge-pr.ps1 -PinOnly; a commit touching only the two pin files keeps the acceptance, anything else returns to review),
        verify (verify.ps1 on the committed head; the receipt's head and tree are recorded; a failure is ship-failed), record (the
        record accept wrote covers the head), qualify (hooks.qualify -Action Impact; a null hook is recorded as not configured, never as passed; when required, a recorded passing qualification is reused
        only for the same head and tree and unchanged candidate artifact hashes, otherwise desktop-required with the exact command,
        and only with -AllowDesktop, after an unlocked-input-desktop check and an announcement, hooks.qualify -Action Run), push (never twice), pr (an existing PR for pkg/<slug> is reused and its description regenerated for the shipped head via gh pr edit; the body is generated from state with a
        "What was not tested" section), merge (merge-pr.ps1 -Auto -NoPluginPin -WaitMerged, only for a head that the verify receipt, the
        qualification decision and the pull request all name; an auto-merge already enabled for that head is waited for, never
        enabled again; a head moved by the pin after the push is re-verified, pushed and gated again; -NoWait always stops at
        auto-merge-enabled; merged only when GitHub reports MERGED), channels (hooks.channels for the release cut for the merge commit, a bounded wait; a null hook is recorded as not configured;
        failed channels and a stale plugin pin are reported, no pin PR is ever opened). merge-pr.ps1 removes the worktree on merge,
        so no step after it uses it.
        Without a remote ("repository": { "remote": null }) there is no fetch, no push, no pull request and no channels: base, pin, verify,
        record and qualify run as above (the rebase is onto the local base branch), then ship stops at the stage no-remote (exit 3, "no remote
        is configured", nothing merged). `ship -LocalMerge` instead runs merge-pr.ps1 -LocalMerge -FromShip -ShipId <this package>: the same receipt and review
        checks as a merge through GitHub, the base branch fast-forwarded to the shipped head in the main checkout, the worktree and branch
        removed; stage merged, channels recorded as not configured.

The check manifest is JSON: { "build": ["<project path>"], "tests": [{ "project": "<test project path>", "filter":
"<filter expression>" }], "format": ["<project path>"], "extra": [{ "name": "x", "exe": "pwsh", "args": ["-File", "x.ps1"],
"mutates": false }], "allowedPaths": ["docs/x.md", "scripts/lib/", "docs/*.md"], "allowedScripts": ["scripts/x.ps1"] }.
allowedPaths globs: * stays inside one path segment, ** crosses segments, ? is one character, a trailing / is a directory.
allowedScripts are the only scripts besides kb.ps1 (init|read|check|changed|selftest) and verify.ps1 a Claude executor may run.
Configuration (agent-workflow.json, read from the main checkout of the repository): state.commonDirNamespace (<namespace> below),
repository.baseRef, pinException.paths (the pin files), hooks.pin (-Action Validate), hooks.qualify (-Action Impact|Run), hooks.channels,
package.commands and package.testFilter (the build, format and test commands), package.minCliVersions (model -> minimum Claude CLI version,
over the kit's own table), verify.outputDir (the receipt folder).
-SelfTest runs offline against marked stubs and temporary repositories; AW_PKG_SELFTEST_CASES=1 prints one CASE line per check and a
KITCASE line per hook-contract check. -SelfTest -Quick runs the fast subset (every command's dispatch, state round-trip, the refusals
and one ship path); the full suite stays the release gate.
State lives in <git common dir>/<namespace>/packages/<slug>.json. Exit codes: 0 ok, 1 failure, 2 bad input, 5 main checkout mutated.
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('new', 'run', 'check', 'show', 'commit', 'review', 'fix', 'disposition', 'accept', 'ship', 'recover', 'help')]
    [string]$Command,
    [string]$Id,
    [string]$Brief,
    [string]$Checks,
    [ValidateSet('medium', 'high')]
    [string]$Effort = 'medium',
    [string]$Model = 'gpt-6-luna',
    [string]$EscalationReason,
    [ValidateRange(0.01, 1440)]
    [double]$MaxMinutes = 30,
    [ValidateRange(1, [long]::MaxValue)]
    [long]$MaxInputTokens = 20000000,
    [string]$CodexVersion,
    [ValidateSet('codex', 'claude-sonnet', 'claude-haiku', 'same')]
    [string]$Executor = 'codex',
    [string]$Message,
    [ValidateSet('light', 'heavy')]
    [string]$Level,
    [ValidateSet('codex-sol', 'codex-luna', 'claude-opus', 'claude-sonnet')]
    [string]$Reviewer,
    [string]$Finding,
    [string]$Findings,
    [switch]$Accept,
    [switch]$Reject,
    [string]$Reason,
    [switch]$ResetBudget,
    [string]$CodexAskScript = (Join-Path $PSScriptRoot 'codex-ask.ps1'),
    [string]$CodexTestScript,
    [string]$RecordReviewScript = (Join-Path $PSScriptRoot 'record-review.ps1'),
    [string]$Title,
    [switch]$AllowDesktop,
    [switch]$NoWait,
    [switch]$LocalMerge,
    [string]$MergePrScript,
    [string]$VerifyScript,
    [string]$GhScript,
    [ValidateRange(0, 240)]
    [int]$ChannelMinutes = 20,
    [ValidateRange(1, 600)]
    [int]$ReleasePollSeconds = 30,
    [ValidateRange(1, 600)]
    [int]$MergeTimeoutMinutes = 40,
    [switch]$Resume,
    [string]$PromptFile,
    [string]$ClaudePath,
    [string]$ClaudeExtraArgs,
    [ValidateRange(0, 1000)]
    [double]$MaxBudgetUsd = 0,
    [ValidateRange(1, 300)]
    [int]$ProbeSeconds = 15,
    [switch]$Json,
    [Alias('WhatIf')]
    [switch]$DryRun,
    [Alias('Repo')]
    [string]$Root,
    [string]$Worktree,
    [string]$BaseRef,
    [string]$AgentRunScript = (Join-Path $PSScriptRoot 'agent-run.ps1'),
    [string]$StatusScript = (Join-Path $PSScriptRoot 'status.ps1'),
    [switch]$SimulateKillFailure,
    [ValidateRange(0, 3600)]
    [int]$LockTimeoutSeconds = 10,
    [ValidateRange(0, 3600)]
    [int]$GraceSeconds = 60,
    [switch]$SelfTest,
    [switch]$Quick
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest
$script:KitDir = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$script:PackageToolsRoot = $PSScriptRoot
$script:PackageScriptPath = $PSCommandPath
. (Join-Path $script:KitDir 'lib' 'root.ps1')
. (Join-Path $script:KitDir 'lib' 'config.ps1')
. (Join-Path $script:KitDir 'lib' 'context.ps1')
. (Join-Path $script:KitDir 'lib' 'selftest.ps1')
. (Join-Path $script:KitDir 'lib' 'process-tree.ps1')
. (Join-Path $script:KitDir 'lib' 'codex.ps1')
. (Join-Path $script:KitDir 'lib' 'tree-hash.ps1')
. (Join-Path $script:KitDir 'lib' 'review-record.ps1')

$SchemaVersion = 1
$script:RepairLimit = 3
# A review round lost to an environment failure is given back (refunded) at most this often per package; the next such failure is charged.
$script:RefundLimit = 2
$script:BriefOverride = $null
$script:ExitCode = 0
$script:JsonDate = if ((Get-Command ConvertFrom-Json).Parameters.ContainsKey('DateKind')) { @{ DateKind = 'String' } } else { @{} }

# Set by Initialize-PackageContext from the configuration of the repository this run is about.
$script:Ctx = $null
$script:ContextScriptRoot = $PSScriptRoot
$script:Repo = $null
$script:Namespace = 'selftest'
$script:PinFiles = @()

function Initialize-PackageContext([string]$Target)
{
    # The repository is -Root, else the repository that holds this script. The configuration (namespace, base, pin files, hooks, commands)
    # is read from its main checkout only, never from a package branch, which could loosen the gates it is judged by.
    # The gate context also checks that the repository named by -Root/-Repo is the repository that holds this tool (the wrapper passes the main
    # checkout of its own), so a standalone clone or another repository is refused, with -Root as with -Worktree. ContextScriptRoot is this
    # folder; only the self-test replaces it, with a copy of the kit outside every configured repository (see Invoke-SelfTest).
    $script:Ctx = if ($Target) { Get-AwGateContext -Root (Resolve-AwRoot -Explicit (ConvertTo-AwFullPath $Target) -ScriptRoot $script:ContextScriptRoot).MainCheckout -Worktree $Target -ScriptRoot $script:ContextScriptRoot } else { Get-AwGateContext -ScriptRoot $script:ContextScriptRoot }
    $script:Repo = [string]$script:Ctx.Worktree
    $script:Namespace = [string]$script:Ctx.Config.state.commonDirNamespace
    $script:PinFiles = @($script:Ctx.PinFiles)
    if (-not $BaseRef) { $script:BaseRef = [string]$script:Ctx.BaseRef }
}

function Get-PackageRemote([string]$Ref)
{
    # The remote a base ref lives on, or $null: a repository without a remote (repository.remote is null) has none, whatever the ref looks
    # like, and so has a ref without a remote prefix. With a remote, the configured one is recognised first, then any "<name>/<branch>" prefix.
    if ($script:Ctx -and -not $script:Ctx.HasRemote) { return $null }
    $configured = if ($script:Ctx) { [string]$script:Ctx.BaseRemote } else { '' }
    if ($configured -and $Ref.StartsWith("$configured/", [StringComparison]::Ordinal)) { return $configured }
    if ($Ref.Contains('/')) { return $Ref.Split('/')[0] }
    $null
}

function Resolve-PackageHook([string]$Name)
{
    # The repository script named by hooks.<Name>, resolved against the main checkout. $null when the hook is not configured: the caller
    # says so in the record and never treats it as passed.
    $hooks = $script:Ctx.Config.hooks
    $relative = if ($hooks.Contains($Name)) { $hooks[$Name] } else { $null }
    if ($null -eq $relative) { return $null }
    $path = [IO.Path]::GetFullPath((Join-Path $script:Ctx.Root ([string]$relative)))
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "the $Name hook does not exist: $path" }
    if ($env:AW_PKG_SELFTEST -and -not (Test-SelfTestStub $path)) { throw "self-test guard: the $Name hook has to be a marked stub" }
    $path
}

function Get-PackageCommand([string]$Kind)
{
    # package.commands.<format|build|test> of the configuration; without it the dotnet commands, which the examples spell out.
    $defaults = @{
        format = @{ exe = 'dotnet'; args = @('format', '{target}', '--no-restore') }
        build = @{ exe = 'dotnet'; args = @('build', '{target}', '--nologo', '-v', 'q') }
        test = @{ exe = 'dotnet'; args = @('test', '{project}', '--nologo', '--filter', '{filter}') }
    }
    $source = $defaults[$Kind]
    if ($script:Ctx)
    {
        $commands = Get-AwSetting (Get-AwSection $script:Ctx 'package') 'commands' @{}
        if ($commands.Contains($Kind) -and $null -ne $commands[$Kind]) { $source = $commands[$Kind] }
    }
    @{ exe = [string]$source.exe; args = @($source.args | ForEach-Object { [string]$_ }) }
}

function Expand-PackageCommand($Command, [hashtable]$Values)
{
    @{ exe = $Command.exe; args = @($Command.args | ForEach-Object { $text = [string]$_; foreach ($key in $Values.Keys) { $text = $text.Replace("{$key}", [string]$Values[$key]) }; $text }) }
}

function Get-PackageTestFilter
{
    $filter = 'Category!=Integration'
    if ($script:Ctx) { $filter = [string](Get-AwSetting (Get-AwSection $script:Ctx 'package') 'testFilter' $filter) }
    $filter
}

function Get-PackageCommandSummary
{
    # "dotnet build|test|format": the executable and the verb of each command, for the permission text.
    $build = Get-PackageCommand 'build'
    "$($build.exe) $(@('build', 'test', 'format' | ForEach-Object { (Get-PackageCommand $_).args[0] }) -join '|')"
}

function Get-PackageVerifyDir
{
    $dir = 'artifacts/verify'
    if ($script:Ctx) { $dir = [string](Get-AwSetting (Get-AwSection $script:Ctx 'verify') 'outputDir' $dir) }
    $dir
}

function Get-Now { [DateTimeOffset]::UtcNow.ToString('o') }

function Get-Sha256([string]$Path)
{
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-OptionalSha256([string]$Path)
{
    if ($Path -and (Test-Path -LiteralPath $Path -PathType Leaf)) { return Get-Sha256 $Path }
    $null
}

function Invoke-Git([string]$Dir, [string[]]$GitArgs)
{
    $output = & git -C $Dir @GitArgs 2>&1
    if ($LASTEXITCODE -ne 0)
    {
        throw "git $($GitArgs -join ' ') failed in ${Dir}: $(($output | ForEach-Object { "$_" }) -join ' ')"
    }
    (($output | ForEach-Object { "$_" }) -join "`n").Trim()
}

function Get-RepoInfo([string]$Path)
{
    $root = Resolve-AwRoot -Explicit (ConvertTo-AwFullPath $Path) -ScriptRoot $PSScriptRoot
    [pscustomobject]@{ Top = $root.WorkTree; CommonDir = $root.CommonDir; MainCheckout = $root.MainCheckout; StateRoot = (Join-Path $root.CommonDir "$($script:Namespace)/packages") }
}
function Get-PackagePaths([string]$Root, [string]$Slug)
{
    [pscustomobject]@{ State = (Join-Path $Root "$Slug.json"); Lock = (Join-Path $Root "$Slug.lock"); Dir = (Join-Path $Root $Slug) }
}

# Set here, not in claude-executor.ps1: $PSBoundParameters belongs to this script and is empty inside a dot-sourced module.
$script:ModelBound = $PSBoundParameters.ContainsKey('Model')
$script:ExecutorBound = $PSBoundParameters.ContainsKey('Executor')

# ---- Modules: the functions of this tool, one file per section, in the order the sections had in the single script -----
# A change to any file under tools/package/ needs the same suites as a change to this file (see CONTRIBUTING.md).

. (Join-Path $script:PackageToolsRoot 'package' 'state.ps1')
. (Join-Path $script:PackageToolsRoot 'package' 'fingerprint.ps1')
. (Join-Path $script:PackageToolsRoot 'package' 'manifest.ps1')
. (Join-Path $script:PackageToolsRoot 'package' 'process.ps1')
. (Join-Path $script:PackageToolsRoot 'package' 'claude-executor.ps1')
. (Join-Path $script:PackageToolsRoot 'package' 'scope.ps1')
. (Join-Path $script:PackageToolsRoot 'package' 'cmd-new.ps1')
. (Join-Path $script:PackageToolsRoot 'package' 'cmd-run.ps1')
. (Join-Path $script:PackageToolsRoot 'package' 'cmd-check.ps1')
. (Join-Path $script:PackageToolsRoot 'package' 'cmd-show.ps1')
. (Join-Path $script:PackageToolsRoot 'package' 'review-helpers.ps1')
. (Join-Path $script:PackageToolsRoot 'package' 'cmd-commit.ps1')
. (Join-Path $script:PackageToolsRoot 'package' 'review-criteria.ps1')
. (Join-Path $script:PackageToolsRoot 'package' 'review-process.ps1')
. (Join-Path $script:PackageToolsRoot 'package' 'cmd-review.ps1')
. (Join-Path $script:PackageToolsRoot 'package' 'cmd-fix-accept.ps1')
. (Join-Path $script:PackageToolsRoot 'package' 'cmd-recover.ps1')
. (Join-Path $script:PackageToolsRoot 'package' 'ship-overview.ps1')
. (Join-Path $script:PackageToolsRoot 'package' 'ship-lock.ps1')
. (Join-Path $script:PackageToolsRoot 'package' 'ship-helpers.ps1')
. (Join-Path $script:PackageToolsRoot 'package' 'ship-return.ps1')
. (Join-Path $script:PackageToolsRoot 'package' 'ship-steps.ps1')

function Show-PackageUsage
{
    # The command lines at the top of this script's header comment, up to the first command description.
    $header = [regex]::Match([IO.File]::ReadAllText($PSCommandPath), '(?s)<#\r?\n(.*?)\r?\n#>').Groups[1].Value
    $cut = [regex]::Match($header, '(?m)^new  ')
    $usage = if ($cut.Success) { $header.Substring(0, $cut.Index) } else { $header }
    Write-Host ($usage.TrimEnd() -replace '\r', '')
    Write-Host ''
    Write-Host "Every command except 'help' and -SelfTest needs -Id <slug>. The header of $PSCommandPath describes each command."
}

# The self-test modules (about 3,000 lines) load only for -SelfTest.
if ($SelfTest)
{
    . (Join-Path $script:PackageToolsRoot 'package' 'selftest-review.ps1')
    . (Join-Path $script:PackageToolsRoot 'package' 'selftest-remoteless.ps1')
    . (Join-Path $script:PackageToolsRoot 'package' 'selftest-ship.ps1')
    . (Join-Path $script:PackageToolsRoot 'package' 'selftest-package-b.ps1')
    . (Join-Path $script:PackageToolsRoot 'package' 'selftest-quick.ps1')
    . (Join-Path $script:PackageToolsRoot 'package' 'selftest-main.ps1')
}
if ($SelfTest) { Invoke-SelfTest }
if (-not $Command -or $Command -eq 'help') { Show-PackageUsage; exit 0 }
if (-not $Id) { Write-Host "ERROR -Id is required for '$Command'"; Show-PackageUsage; exit 2 }
try
{
    Initialize-PackageContext $Root
    switch ($Command)
    {
        'new' { Invoke-New }
        'run' { Invoke-Run }
        'check' { Invoke-Check }
        'show' { Invoke-Show }
        'commit' { Invoke-Commit }
        'review' { Invoke-Review }
        'fix' { Invoke-Fix }
        'disposition' { Invoke-Disposition }
        'accept' { Invoke-Accept }
        'ship' { Invoke-Ship }
        'recover' { Invoke-Recover }
    }
}
catch
{
    Write-Host "ERROR $($_.Exception.Message)"
    exit $(if ($script:ExitCode) { $script:ExitCode } else { 1 })
}
exit $script:ExitCode
