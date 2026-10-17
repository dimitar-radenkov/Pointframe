#Requires -Version 7.0
<#
The Pointframe knowledge base tool: docs/knowledge-base/knowledge-base.md (the core: cross-cutting rules and recipes),
the other files next to it (file-map.md, references.md, tooling.md, decisions.md, workflow.md) and
docs/knowledge-base/features/*.md (one file per feature area). The /kb-init, /kb-read, /kb-write, and /kb-check skills call it.

  pwsh scripts/kb.ps1 init [-Role coordinator|executor|reviewer|helper] [target]   # session start: core, role rules, environment, optional target
  pwsh scripts/kb.ps1 read <path|area|F-NN|topic> [...] [-All]   # what to read: sections, features, top 5 lessons (-All: every lesson)
  pwsh scripts/kb.ps1 read                                       # the Feature index and the area files
  pwsh scripts/kb.ps1 check                                      # refresh generated blocks and the baseline, then check everything; exit 1 on errors
  pwsh scripts/kb.ps1 check -NoFix                               # check only, never write (CI)
  pwsh scripts/kb.ps1 stats [-Json]                              # bytes and estimated tokens per file, section, and task-suite task
  pwsh scripts/kb.ps1 selftest                                   # synthetic fixtures under scripts/kb-fixtures; exit 1 on a failed case
  pwsh scripts/kb.ps1 changed [-Base <ref>]                      # sections and features to review for a diff
  pwsh scripts/kb.ps1 hook                                       # Claude Code hook: payload JSON on stdin, guidance as additionalContext

A section is a "## " or "### " heading and the lines up to the next heading of any level; a "### " section
also carries its parent "## " preamble (the text between the "## " heading and its first "### "). A "## " heading
that only groups "### " sections is a container, not a section. In an area file the "## Features" table is not a section.

Generated: the core's table of contents, Feature index, and decision index, and the Codex copies of the kb-* skills in
.agents/skills/.

Checks, in every file: backticked repo paths exist and are not gitignored; "- Lesson: <heading>" matches a
"## " heading in lessons.md; links resolve to a heading in the target file; no two headings in a file share
an anchor; a section has at most one "**Files.**" line. Feature rows: seven cells, unique F-NN IDs, an entry
point, a linked section, telemetry events that exist, every TelemetryEvents event and every smoke test
claimed. File map rows (docs/knowledge-base/file-map.md): three cells, short descriptions, no dead patterns, every
tracked file covered, no redundant row, no link to a container heading. Workflow rules in workflow.md: unique
<!-- rule:W-NN roles=... --> IDs, known roles, triggers that point at a heading. lessons.md: every lesson has one
Problem, What fixed it, and Takeaway subsection, and is reachable from a File map row, a feature row, or a task.
Budgets (docs/knowledge-base/kb-baseline.json): a section over 8 KB and a core file over 12 KB are errors unless
the baseline lists them at their current size or more; check without -NoFix lowers the baseline, never raises it.
The task suite docs/knowledge-base/kb-tasks.json must keep recall 1.0.
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('read', 'check', 'changed', 'hook', 'init', 'stats', 'selftest')]
    [string]$Command = 'check',
    [Parameter(Position = 1, ValueFromRemainingArguments = $true)]
    [string[]]$Targets,
    [switch]$All,
    [switch]$NoFix,
    [string]$Base = 'HEAD',
    [ValidateSet('coordinator', 'executor', 'reviewer', 'helper')]
    [string]$Role = 'executor',
    [switch]$Json,
    [string]$Root,
    [switch]$NoProbe
)

$usage = 'Usage: kb.ps1 init [-Role coordinator|executor|reviewer|helper] [target] | read <path|area|F-NN|topic> [...] [-All] | check [-NoFix] | stats [-Json] | selftest | changed [-Base <ref>] | hook'
$Targets = @($Targets | Where-Object { $_ })
$stray = @($Targets | Where-Object { $_.StartsWith('-') })
if ($stray.Count -gt 0 -or ($Targets.Count -gt 0 -and $Command -notin @('read', 'init')))
{
    Write-Host "ERROR unexpected arguments: $($Targets -join ' '). $usage"
    exit 2
}

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest

# ---------------------------------------------------------------- self-test

function Invoke-SelfTest
{
    $fixtureRoot = Join-Path $PSScriptRoot 'kb-fixtures'
    $base = Join-Path $fixtureRoot 'base'
    if (-not (Test-Path (Join-Path $base 'docs' 'knowledge-base' 'knowledge-base.md')))
    {
        Write-Host "ERROR selftest fixtures not found under $base"
        return 1
    }
    $script:stCases = 0
    $script:stFailed = [System.Collections.Generic.List[string]]::new()
    $temp = Join-Path ([System.IO.Path]::GetTempPath()) "kb-selftest-$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Force -Path $temp | Out-Null

    function Assert-Case([bool]$Condition, [string]$Name, [string]$Detail = '')
    {
        $script:stCases++
        if (-not $Condition)
        {
            $script:stFailed.Add($Name)
            Write-Host "FAIL  $Name"
            if ($Detail) { Write-Host "      $($Detail -replace "`r?`n", "`n      ")" }
        }
        else
        {
            Write-Host "ok    $Name"
        }
    }
    function Invoke-Kb([string[]]$KbArgs, [string]$RootDir, [string]$StdIn = '', [hashtable]$EnvVars = @{})
    {
        $psi = [System.Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path)
        foreach ($a in @('-NoProfile', '-NonInteractive', '-File', $PSCommandPath) + $KbArgs + @('-Root', $RootDir))
        {
            [void]$psi.ArgumentList.Add($a)
        }
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $psi.RedirectStandardInput = $true
        $psi.UseShellExecute = $false
        $psi.StandardOutputEncoding = [System.Text.Encoding]::UTF8
        foreach ($k in $EnvVars.Keys) { $psi.Environment[$k] = [string]$EnvVars[$k] }
        $p = [System.Diagnostics.Process]::Start($psi)
        if ($StdIn) { $p.StandardInput.Write($StdIn) }
        $p.StandardInput.Close()
        $stdout = $p.StandardOutput.ReadToEndAsync()
        $stderr = $p.StandardError.ReadToEndAsync()
        $p.WaitForExit()
        return [pscustomobject]@{ Exit = $p.ExitCode; Out = $stdout.Result + $stderr.Result }
    }
    function New-Copy([string]$Name)
    {
        $dest = Join-Path $temp $Name
        Copy-Item -Recurse -Path $base -Destination $dest
        return $dest
    }
    # Edits are written against LF text, whatever line endings the fixture file has.
    function Edit-Text([string]$Dir, [string]$Rel, [scriptblock]$Edit)
    {
        $p = Join-Path $Dir $Rel
        $t = [System.IO.File]::ReadAllText($p) -replace "`r`n", "`n"
        [System.IO.File]::WriteAllText($p, (& $Edit $t), [System.Text.UTF8Encoding]::new($false))
    }
    $kbRel = Join-Path 'docs' 'knowledge-base'

    try
    {
        # 1. The base fixture is clean.
        $r = Invoke-Kb @('check', '-NoFix') $base
        Assert-Case ($r.Exit -eq 0) 'base fixture passes check -NoFix' $r.Out

        # 2. ### routing: the File map row links the narrow ### section; the sibling is not offered.
        $r = Invoke-Kb @('read', 'src/beta/Thing.cs') $base
        Assert-Case ($r.Out -match 'read\s+docs/knowledge-base/knowledge-base\.md:\d+\s+Child B') 'read names the narrow ### section with its line' $r.Out
        Assert-Case ($r.Out -notmatch 'read\s+\S+\s+Child A') 'read does not offer the sibling ### section' $r.Out
        $r = Invoke-Kb @('stats', '-Json') $base
        $stats = $null
        try { $stats = $r.Out | ConvertFrom-Json } catch { }
        $childB = @(if ($stats) { $stats.sections | Where-Object { $_.heading -eq 'Child B' } })
        $group = @(if ($stats) { $stats.sections | Where-Object { $_.heading -eq 'Group' } })
        Assert-Case ($childB.Count -eq 1 -and $group.Count -eq 0) 'a ## heading that only groups ### sections is a container, not a section' $r.Out
        $ownChildB = if ($childB.Count -eq 1) { [int]$childB[0].ownBytes } else { 0 }
        Assert-Case ($childB.Count -eq 1 -and [int]$childB[0].bytes -gt $ownChildB) 'a ### section budget includes its parent ## preamble' $r.Out

        # 3. Hook dedup: sections and lessons are separate per session.
        $session = "selftest-$([guid]::NewGuid().ToString('N'))"
        $payload = { param($File) (@{ tool_input = @{ file_path = (Join-Path $base $File) }; cwd = $base; session_id = $session; hook_event_name = 'PostToolUse' } | ConvertTo-Json -Compress) }
        $h1 = Invoke-Kb @('hook') $base (& $payload 'src/beta/Thing.cs')
        $h2 = Invoke-Kb @('hook') $base (& $payload 'src/beta/Thing.cs')
        $h3 = Invoke-Kb @('hook') $base (& $payload 'src/beta/Other.cs')
        $c1 = if ($h1.Out.Trim()) { ($h1.Out | ConvertFrom-Json).hookSpecificOutput.additionalContext } else { '' }
        $c3 = if ($h3.Out.Trim()) { ($h3.Out | ConvertFrom-Json).hookSpecificOutput.additionalContext } else { '' }
        Assert-Case ($c1 -match 'Child B' -and $c1 -match 'Lesson beta one' -and $c1 -notmatch 'Lesson beta other') 'hook delivers the section and its first lessons' $h1.Out
        Assert-Case (-not $h2.Out.Trim()) 'hook says nothing when nothing is new' $h2.Out
        Assert-Case ($c3 -and $c3 -notmatch 'Child B' -and $c3 -match 'Lesson beta other') 'hook shows a newly relevant lesson for a new path without repeating the section' $h3.Out
        $stateFile = Join-Path (Join-Path ([System.IO.Path]::GetTempPath()) 'pointframe-kb-hook') "$session.txt"
        if (Test-Path $stateFile) { Remove-Item -LiteralPath $stateFile -Force -ErrorAction SilentlyContinue }

        # 4. Baseline: a new violation fails, a listed one may shrink but never grow, a fixed one leaves the baseline.
        $big = New-Copy 'baseline'
        $filler = (1..200 | ForEach-Object { 'Filler line that makes this section large enough to cross the limit for the test.' }) -join "`n"
        Edit-Text $big (Join-Path $kbRel 'knowledge-base.md') { param($t) $t.Replace('Child A body.', "Child A body.`n`n$filler") }
        $r = Invoke-Kb @('check', '-NoFix') $big
        Assert-Case ($r.Exit -eq 1 -and $r.Out -match 'over 8192') 'a new section over 8 KB fails check' $r.Out
        $st = (Invoke-Kb @('stats', '-Json') $big).Out | ConvertFrom-Json
        $size = [int](@($st.sections | Where-Object { $_.heading -eq 'Child A' })[0].bytes)
        $coreSize = [int]$st.core.bytes
        $baselinePath = Join-Path $big (Join-Path $kbRel 'kb-baseline.json')
        $writeBaseline = { param($Section, $Core) ([ordered]@{ coreBytes = $Core; sections = [ordered]@{ 'knowledge-base.md#child-a' = $Section }; workflowRules = [ordered]@{ 'W-01' = @('all'); 'W-02' = @('executor'); 'W-03' = @('coordinator'); 'W-04' = @('reviewer') } } | ConvertTo-Json -Depth 4) | Set-Content -LiteralPath $baselinePath }
        & $writeBaseline $size $coreSize
        $r = Invoke-Kb @('check', '-NoFix') $big
        Assert-Case ($r.Exit -eq 0) 'a listed section at its baseline size passes' $r.Out
        Edit-Text $big (Join-Path $kbRel 'knowledge-base.md') { param($t) $t.Replace('Child A body.', 'Child A body. More.') }
        $r = Invoke-Kb @('check', '-NoFix') $big
        Assert-Case ($r.Exit -eq 1 -and $r.Out -match 'grew') 'growing a listed section fails check' $r.Out
        & $writeBaseline ($size + 5000) ($coreSize + 5000)
        $r = Invoke-Kb @('check', '-NoFix') $big
        Assert-Case ($r.Exit -eq 0) 'shrinking a listed section passes check -NoFix' $r.Out
        $r = Invoke-Kb @('check') $big
        $after = Get-Content -Raw -LiteralPath $baselinePath | ConvertFrom-Json
        Assert-Case ($r.Exit -eq 0 -and [int]$after.sections.'knowledge-base.md#child-a' -le ($size + 20) -and [int]$after.coreBytes -le ($coreSize + 20)) 'check without -NoFix lowers the baseline' "$($r.Out) $($after | ConvertTo-Json -Compress)"
        Edit-Text $big (Join-Path $kbRel 'knowledge-base.md') { param($t) $t.Replace($filler, '') }
        $r = Invoke-Kb @('check', '-NoFix') $big
        Assert-Case ($r.Exit -eq 1 -and $r.Out -match 'baseline') 'a fixed section must leave the baseline' $r.Out
        $r = Invoke-Kb @('check') $big
        $after = Get-Content -Raw -LiteralPath $baselinePath | ConvertFrom-Json
        Assert-Case ($r.Exit -eq 0 -and -not $after.sections.PSObject.Properties['knowledge-base.md#child-a']) 'check without -NoFix drops a fixed section from the baseline' $r.Out

        # 5. Workflow rule IDs.
        $wf = Join-Path $kbRel 'workflow.md'
        $dup = New-Copy 'rules-dup'
        Edit-Text $dup $wf { param($t) $t.Replace('rule:W-02 roles=executor', 'rule:W-01 roles=executor') }
        $r = Invoke-Kb @('check', '-NoFix') $dup
        Assert-Case ($r.Exit -eq 1 -and $r.Out -match 'duplicate rule ID W-01') 'a duplicate rule ID fails check' $r.Out
        $role = New-Copy 'rules-role'
        Edit-Text $role $wf { param($t) $t.Replace('roles=executor -->', 'roles=janitor -->') }
        $r = Invoke-Kb @('check', '-NoFix') $role
        Assert-Case ($r.Exit -eq 1 -and $r.Out -match "unknown role 'janitor'") 'an unknown role fails check' $r.Out
        $trigger = New-Copy 'rules-trigger'
        Edit-Text $trigger $wf { param($t) $t.Replace('<!-- rule-section roles=coordinator -->', "<!-- rule-section roles=coordinator -->`n`nStray paragraph.") }
        $r = Invoke-Kb @('check', '-NoFix') $trigger
        Assert-Case ($r.Exit -eq 1 -and $r.Out -match 'rule-section') 'a trigger that points at no heading fails check' $r.Out
        $noId = New-Copy 'rules-noid'
        Edit-Text $noId $wf { param($t) $t.Replace('<!-- rule:W-03 roles=coordinator -->', '<!-- rule: roles=coordinator -->') }
        $r = Invoke-Kb @('check', '-NoFix') $noId
        Assert-Case ($r.Exit -eq 1 -and $r.Out -match 'malformed rule marker') 'a rule marker without an ID fails check' $r.Out

        # 6. init: order, role filtering, status allowlist, missing status, probe faults.
        $r = Invoke-Kb @('init', '-NoProbe') $base
        $text = $r.Out
        $iCore = $text.IndexOf('Fixture core marker')
        $iRules = $text.IndexOf('Workflow rules for role executor')
        $iEnv = $text.IndexOf('Environment (read-only')
        Assert-Case ($r.Exit -eq 0 -and $iCore -ge 0 -and $iRules -gt $iCore -and $iEnv -gt $iRules) 'init prints core, then rules, then environment' $text
        Assert-Case ($text -match '\[W-01\]' -and $text -match '\[W-02\]' -and $text -notmatch '\[W-03\]' -and $text -notmatch 'Coordinator only rule text') 'init for an executor prints executor and shared rules only' $text
        Assert-Case ($text -notmatch 'Local operational context') 'init for an executor prints no status digest' $text
        Assert-Case ($text -match 'tokens') 'init ends with a size line' $text
        $r = Invoke-Kb @('init', '-Role', 'coordinator', '-NoProbe') $base
        $text = $r.Out
        Assert-Case ($r.Exit -eq 0 -and $text -match 'Local operational context \(plan/status\.json, [^)]+\), not policy' -and $text -match '\[W-03\]') 'init -Role coordinator adds the status digest and coordinator rules' $text
        Assert-Case ($text -match 'Fixture objective alpha' -and $text -match 'stage alpha' -and $text -match 'next alpha' -and $text -match 'blocker alpha' -and $text -match '2026-12-24') 'the status digest prints the allowlisted fields' $text
        Assert-Case ($text -notmatch 'SECRET-NOTE' -and $text -notmatch 'accountable-secret' -and $text -notmatch 'Fixture objective done') 'the status digest never prints a field outside the allowlist or a done record' $text
        $noStatus = New-Copy 'nostatus'
        Remove-Item -LiteralPath (Join-Path $noStatus 'plan' 'status.json') -Force
        $r = Invoke-Kb @('init', '-Role', 'coordinator', '-NoProbe') $noStatus
        Assert-Case ($r.Exit -eq 0 -and $r.Out -notmatch 'Local operational context') 'a missing plan/status.json is no error' $r.Out
        $r = Invoke-Kb @('init') $base '' @{ KB_PROBE_FAULT = '1' }
        Assert-Case ($r.Exit -eq 0 -and ([regex]::Matches($r.Out, 'unknown')).Count -ge 4) 'a probe that throws prints unknown' $r.Out

        # 7. init with a target: bodies for a few sections, a menu for a broad topic.
        $r = Invoke-Kb @('init', '-NoProbe', 'src/beta/Thing.cs') $base
        Assert-Case ($r.Exit -eq 0 -and $r.Out -match 'Child B: already in the core above' -and $r.Out -match 'Takeaway of beta one') 'init with a path prints the section bodies and top lessons' $r.Out
        $r = Invoke-Kb @('init', '-NoProbe', 'widget') $base
        Assert-Case ($r.Exit -eq 0 -and $r.Out -match 'menu: 4 sections' -and $r.Out -notmatch '-----\s+\S+\s+Widget') 'init with a broad topic prints a menu instead of bodies' $r.Out
        $r = Invoke-Kb @('init', '-NoProbe', 'F-90') $base
        Assert-Case ($r.Exit -eq 0 -and $r.Out -match 'Alpha body') 'init with a feature ID of few sections prints bodies' $r.Out
        Assert-Case ($r.Out -match 'Takeaway of alpha one') 'init with a feature ID also prints the full top lessons' $r.Out
        $r = Invoke-Kb @('read', 'Group') $base
        Assert-Case ($r.Exit -eq 0 -and $r.Out -match 'section\s+\S+\s+Child A' -and $r.Out -match 'section\s+\S+\s+Child B') 'a container heading matches by name and resolves to its child sections' $r.Out
        $r = Invoke-Kb @('read', 'src/beta/Thing.cs', '-All') $base
        Assert-Case ($r.Out -match 'lesson\s+Lesson group shared') 'a ### section inherits the lessons linked in its parent ## preamble' $r.Out
        $deleted = New-Copy 'rules-deleted'
        Edit-Text $deleted $wf { param($t) $t.Replace("<!-- rule:W-04 roles=reviewer -->`n", '') }
        $r = Invoke-Kb @('check', '-NoFix') $deleted
        Assert-Case ($r.Exit -eq 1 -and $r.Out -match 'workflow\.md:\d+: this block is covered by no') 'deleting a rule marker is an error naming the block' $r.Out
        # The registry catches a deleted marker even where a role-restricted trigger still covers the block.
        $gone = New-Copy 'rules-registry-deleted'
        Edit-Text $gone $wf { param($t) $t.Replace("<!-- rule:W-02 roles=executor -->`n", '') }
        $r = Invoke-Kb @('check', '-NoFix') $gone
        Assert-Case ($r.Exit -eq 1 -and $r.Out -match 'rule W-02 is registered in kb-baseline\.json') 'deleting a marker inside a role-restricted section is an error' $r.Out
        $changedRoles = New-Copy 'rules-registry-roles'
        Edit-Text $changedRoles $wf { param($t) $t.Replace('rule:W-03 roles=coordinator', 'rule:W-03 roles=coordinator,executor') }
        $r = Invoke-Kb @('check', '-NoFix') $changedRoles
        Assert-Case ($r.Exit -eq 1 -and $r.Out -match 'rule W-03 has roles') 'changing a rule''s roles is an error' $r.Out
        $added = New-Copy 'rules-registry-added'
        Edit-Text $added $wf { param($t) $t.TrimEnd() + "`n`n<!-- rule:W-05 roles=executor -->`nNew rule text.`n" }
        $r = Invoke-Kb @('check', '-NoFix') $added
        Assert-Case ($r.Exit -eq 1 -and $r.Out -match 'rule W-05 is not in the kb-baseline\.json registry') 'an unregistered rule is an error under -NoFix' $r.Out
        $r = Invoke-Kb @('check') $added
        $registered = Get-Content -Raw -LiteralPath (Join-Path $added (Join-Path $kbRel 'kb-baseline.json')) | ConvertFrom-Json
        Assert-Case ($r.Exit -eq 0 -and @($registered.workflowRules.'W-05') -contains 'executor' -and $registered.workflowRules.'W-04') 'check without -NoFix registers a new rule and keeps the others' ($r.Out + ($registered | ConvertTo-Json -Compress))
        $helper = New-Copy 'rules-helper-role'
        Edit-Text $helper $wf { param($t) $t.TrimEnd() + "`n`n<!-- rule:W-05 roles=helper -->`nHelper rule text.`n" }
        $r = Invoke-Kb @('check') $helper
        Assert-Case ($r.Exit -eq 0) 'check registers a helper rule' $r.Out
        $r = Invoke-Kb @('init', '-Role', 'helper', '-NoProbe') $helper
        Assert-Case ($r.Exit -eq 0 -and $r.Out -match 'Workflow rules for role helper' -and $r.Out -match 'Helper rule text' -and $r.Out -match '\[W-01\]' -and $r.Out -notmatch 'Coordinator only rule text' -and $r.Out -notmatch 'Executor rule text' -and $r.Out -notmatch 'Reviewer rule text') 'init -Role helper prints helper and shared rules only' $r.Out
        $emptyRule = New-Copy 'rules-empty-roles'
        Edit-Text $emptyRule $wf { param($t) $t.Replace('rule:W-03 roles=coordinator', 'rule:W-03 roles=,') }
        $r = Invoke-Kb @('check', '-NoFix') $emptyRule
        Assert-Case ($r.Exit -eq 1 -and $r.Out -match 'rule W-03 needs a nonempty list') 'a rule with an empty role list is an error' $r.Out
        $emptyTrigger = New-Copy 'rules-empty-trigger'
        Edit-Text $emptyTrigger $wf { param($t) $t.Replace('<!-- rule-section roles=coordinator -->', '<!-- rule-section roles=, -->') }
        $r = Invoke-Kb @('check', '-NoFix') $emptyTrigger
        Assert-Case ($r.Exit -eq 1 -and $r.Out -match 'trigger needs a nonempty list') 'a trigger with an empty role list is an error' $r.Out
        $noRoles = New-Copy 'rules-no-roles'
        Edit-Text $noRoles $wf { param($t) $t.Replace('rule:W-03 roles=coordinator', 'rule:W-03 roles=') }
        $r = Invoke-Kb @('check', '-NoFix') $noRoles
        Assert-Case ($r.Exit -eq 1 -and $r.Out -match 'malformed rule marker') 'a rule with no role list at all is an error' $r.Out
        # 8. Check failures a reader would hit.
        $noTakeaway = New-Copy 'lessons-takeaway'
        Edit-Text $noTakeaway 'lessons.md' { param($t) $t.Replace("### Takeaway`n`nTakeaway of beta one.`n", '') }
        $r = Invoke-Kb @('check', '-NoFix') $noTakeaway
        Assert-Case ($r.Exit -eq 1 -and $r.Out -match 'Takeaway') 'a lesson without a Takeaway fails check' $r.Out
        $orphan = New-Copy 'lessons-orphan'
        Edit-Text $orphan 'lessons.md' { param($t) $t + "`n## Orphan lesson`n`n### Problem`n`nP.`n`n### What fixed it`n`nF.`n`n### Takeaway`n`nT.`n" }
        $r = Invoke-Kb @('check', '-NoFix') $orphan
        Assert-Case ($r.Exit -eq 1 -and $r.Out -match 'Orphan lesson') 'a lesson no section links fails check' $r.Out
        $container = New-Copy 'container'
        Edit-Text $container (Join-Path $kbRel 'file-map.md') { param($t) $t.Replace('knowledge-base.md#child-b', 'knowledge-base.md#group') }
        $r = Invoke-Kb @('check', '-NoFix') $container
        Assert-Case ($r.Exit -eq 1 -and $r.Out -match 'container') 'a File map link to a container heading fails check' $r.Out
        $recall = New-Copy 'recall'
        Edit-Text $recall (Join-Path $kbRel 'kb-tasks.json') { param($t) $t.Replace('"Lesson beta one"', '"Lesson beta one", "Lesson alpha one"') }
        $r = Invoke-Kb @('check', '-NoFix') $recall
        Assert-Case ($r.Exit -eq 1 -and $r.Out -match 'recall') 'a task whose recall drops below 1.0 fails check' $r.Out
    }
    finally
    {
        Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
    }
    Write-Host "selftest: $($script:stCases) cases, $($script:stFailed.Count) failed"
    return [int]($script:stFailed.Count -gt 0)
}

if ($Command -eq 'selftest')
{
    exit (Invoke-SelfTest)
}

$ReadMode = $Command -eq 'read'
$Changed = $Command -eq 'changed'
$Hook = $Command -eq 'hook'
$InitMode = $Command -eq 'init'
$StatsMode = $Command -eq 'stats'
$Check = $Command -eq 'check' -and $NoFix
$For = @(if ($ReadMode -or $InitMode) { $Targets })

$RepoRoot = if ($Root) { (Resolve-Path -LiteralPath $Root).Path } else { (Resolve-Path (Join-Path $PSScriptRoot '..')).Path }
$KbDir = Join-Path $RepoRoot 'docs' 'knowledge-base'
$MainPath = Join-Path $KbDir 'knowledge-base.md'
$FeatureDir = Join-Path $KbDir 'features'
$LessonsPath = Join-Path $RepoRoot 'lessons.md'
$BaselinePath = Join-Path $KbDir 'kb-baseline.json'
$TasksPath = Join-Path $KbDir 'kb-tasks.json'
$TocMarkers = @('<!-- toc -->', '<!-- /toc -->')
$IndexMarkers = @('<!-- features -->', '<!-- /features -->')
$DecisionMarkers = @('<!-- decisions -->', '<!-- /decisions -->')
$FileMapHeading = 'File map'
$FileMapName = 'file-map.md'
$FeaturesHeading = 'Features'
$MaxDescriptionLength = 160
$SectionLimit = 8192
$CoreLimit = 12288
$ProseLineLimit = 600
$AreaFileLimit = 40960
$KnownRoles = @('all', 'coordinator', 'executor', 'reviewer', 'helper')
$PartBudgets = [ordered]@{ core = 12288; rules = 4096; environment = 1536; status = 2048; target = 12288 }
$InitTargetBudget = 25600
$DefaultBase = if ($Root) { $RepoRoot } else { (Get-Location).Path }

if (-not (Test-Path $MainPath))
{
    if ($Hook) { exit 0 }
    Write-Host "ERROR knowledge base not found: $MainPath"
    exit 1
}

$errors = [System.Collections.Generic.List[string]]::new()
$notes = [System.Collections.Generic.List[string]]::new()
$warns = [System.Collections.Generic.List[string]]::new()

function ConvertTo-Anchor([string]$Heading)
{
    $a = $Heading.Trim().ToLowerInvariant()
    $a = $a -replace '[^\p{L}\p{N}\s-]', ''
    return ($a -replace '\s', '-')
}

function ConvertTo-RepoRelative([string]$FullPath)
{
    return ([System.IO.Path]::GetRelativePath($RepoRoot, $FullPath) -replace '\\', '/')
}

function Get-Bytes([string]$Text)
{
    return [System.Text.Encoding]::UTF8.GetByteCount($Text)
}

function Get-Tokens([int]$Bytes)
{
    return [int][Math]::Ceiling($Bytes / 4.0)
}

# Repo-root-anchored glob: '**' crosses folders, '*' and '?' stay inside one, a trailing '/' means the whole folder.
function ConvertFrom-Glob([string]$Glob)
{
    if ($Glob.EndsWith('/'))
    {
        $Glob += '**'
    }
    $sb = [System.Text.StringBuilder]::new('^')
    for ($i = 0; $i -lt $Glob.Length; $i++)
    {
        $c = $Glob[$i]
        if ($c -eq '*' -and $i + 1 -lt $Glob.Length -and $Glob[$i + 1] -eq '*')
        {
            if ($i + 2 -lt $Glob.Length -and $Glob[$i + 2] -eq '/')
            {
                [void]$sb.Append('(?:.*/)?')
                $i += 2
            }
            else
            {
                [void]$sb.Append('.*')
                $i += 1
            }
        }
        elseif ($c -eq '*')
        {
            [void]$sb.Append('[^/]*')
        }
        elseif ($c -eq '?')
        {
            [void]$sb.Append('[^/]')
        }
        else
        {
            [void]$sb.Append([regex]::Escape([string]$c))
        }
    }
    [void]$sb.Append('$')
    return [regex]::new($sb.ToString(), [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
}

$script:trackedCache = $null
function Get-TrackedCached
{
    if ($null -eq $script:trackedCache)
    {
        $listFile = Join-Path $RepoRoot 'tracked.txt'
        if ($Root -and (Test-Path -LiteralPath $listFile))
        {
            # A fixture root has no git index of its own; it lists its files.
            $script:trackedCache = @([System.IO.File]::ReadAllLines($listFile) | ForEach-Object { $_.Trim() } | Where-Object { $_ })
            return $script:trackedCache
        }
        try
        {
            # New, not-ignored files count too: CI sees them as tracked once they are committed, so a
            # local check that skipped them passed while CI failed (lessons.md, File map check).
            $out = & git -C $RepoRoot ls-files --cached --others --exclude-standard 2>$null
            $script:trackedCache = if ($LASTEXITCODE -eq 0) { @($out | Where-Object { $_ }) } else { @() }
        }
        catch
        {
            $script:trackedCache = @()
        }
    }
    return $script:trackedCache
}

# Splits "| a | b | c |" into cells, ignoring pipes that are escaped (\|) or inside a backtick span.
function Split-TableRow([string]$Row)
{
    $cells = [System.Collections.Generic.List[string]]::new()
    $sb = [System.Text.StringBuilder]::new()
    $inTick = $false
    $text = $Row.Trim().Trim('|')
    for ($k = 0; $k -lt $text.Length; $k++)
    {
        $c = $text[$k]
        if ($c -eq '`')
        {
            $inTick = -not $inTick
        }
        if ($c -eq '\' -and $k + 1 -lt $text.Length -and $text[$k + 1] -eq '|')
        {
            [void]$sb.Append('|')
            $k++
            continue
        }
        if ($c -eq '|' -and -not $inTick)
        {
            $cells.Add($sb.ToString().Trim())
            [void]$sb.Clear()
            continue
        }
        [void]$sb.Append($c)
    }
    $cells.Add($sb.ToString().Trim())
    return , $cells
}

# ---------------------------------------------------------------- documents

# One knowledge base file: its lines, which lines are inside code fences, and its headings. A heading owns the
# lines up to the next heading of any level (OwnEnd); EndLine is the end of its subtree. A section is a "## " or
# "### " heading; a "## " heading that only groups "### " sections is a container. A "### " section also carries
# the preamble of its parent "## " heading.
function Read-Doc([string]$Path, [bool]$IsMain)
{
    $text = [System.IO.File]::ReadAllText($Path)
    $lines = $text -split "\r?\n"
    $doc = [pscustomobject]@{
        Path = $Path
        Rel = ConvertTo-RepoRelative $Path
        Name = ([System.IO.Path]::GetRelativePath($KbDir, $Path) -replace '\\', '/')
        IsMain = $IsMain
        Newline = if ($text.Contains("`r`n")) { "`r`n" } else { "`n" }
        Lines = $lines
        Fenced = [bool[]]::new($lines.Length)
        Headings = [System.Collections.Generic.List[object]]::new()
        Title = ''
        Bytes = Get-Bytes ($lines -join "`n")
    }
    $isFeatureDoc = $doc.Name.StartsWith('features/')
    $inFence = $false
    for ($i = 0; $i -lt $lines.Length; $i++)
    {
        if ($lines[$i] -match '^\s*```')
        {
            $inFence = -not $inFence
            $doc.Fenced[$i] = $true
            continue
        }
        if ($inFence)
        {
            $doc.Fenced[$i] = $true
            continue
        }
        if ($lines[$i] -match '^(#{1,3})\s+(.+?)\s*$')
        {
            $doc.Headings.Add([pscustomobject]@{
                Level = $Matches[1].Length
                Text = $Matches[2]
                Anchor = ConvertTo-Anchor $Matches[2]
                Line = $i
                EndLine = $lines.Length - 1
                OwnEnd = $lines.Length - 1
                Doc = $doc
                Parent = $null
                Children = [System.Collections.Generic.List[object]]::new()
                IsSection = $false
                IsContainer = $false
            })
        }
    }
    for ($k = 0; $k -lt $doc.Headings.Count; $k++)
    {
        $h = $doc.Headings[$k]
        for ($n = $k + 1; $n -lt $doc.Headings.Count; $n++)
        {
            if ($doc.Headings[$n].Level -le $h.Level)
            {
                $h.EndLine = $doc.Headings[$n].Line - 1
                break
            }
        }
        if ($k + 1 -lt $doc.Headings.Count)
        {
            $h.OwnEnd = $doc.Headings[$k + 1].Line - 1
        }
        for ($n = $k - 1; $n -ge 0; $n--)
        {
            if ($doc.Headings[$n].Level -lt $h.Level)
            {
                $h.Parent = $doc.Headings[$n]
                $doc.Headings[$n].Children.Add($h)
                break
            }
        }
        if ($h.Level -eq 1 -and -not $doc.Title)
        {
            $doc.Title = $h.Text
        }
    }
    foreach ($h in $doc.Headings)
    {
        $kids = @($h.Children | Where-Object { $_.Level -eq 3 })
        $own = if ($h.OwnEnd -gt $h.Line) { ($lines[($h.Line + 1)..$h.OwnEnd] -join '').Trim() } else { '' }
        $h.IsContainer = $h.Level -eq 2 -and $kids.Count -gt 0 -and $own.Length -lt 120
        $excluded = ($isFeatureDoc -and $h.Level -eq 2 -and $h.Text -eq $FeaturesHeading) -or
            ($IsMain -and $h.Level -eq 2 -and $h.Text -in @('Contents', 'Feature index'))
        $h.IsSection = ($h.Level -eq 2 -or $h.Level -eq 3) -and -not $h.IsContainer -and -not $excluded
    }
    return $doc
}

$mainDoc = Read-Doc $MainPath $true
$docs = [System.Collections.Generic.List[object]]::new()
$docs.Add($mainDoc)
# Other files next to the main one (decisions, workflow, file map, references, tooling), then one file per feature area.
foreach ($f in Get-ChildItem -Path $KbDir -Filter '*.md' -File | Where-Object { $_.FullName -ne $MainPath } | Sort-Object Name)
{
    $docs.Add((Read-Doc $f.FullName $false))
}
if (Test-Path $FeatureDir)
{
    foreach ($f in Get-ChildItem -Path $FeatureDir -Filter '*.md' -File | Sort-Object Name)
    {
        $docs.Add((Read-Doc $f.FullName $false))
    }
}
$featureDocs = @($docs | Where-Object { $_.Name.StartsWith('features/') })
$mapDoc = $docs | Where-Object { $_.Name -eq $FileMapName } | Select-Object -First 1
$workflowDoc = $docs | Where-Object { $_.Name -eq 'workflow.md' } | Select-Object -First 1

# Decisions ("D-NNN Title" sections) live in decisions.md or in the area file they govern; numbers are global.
$decisions = @(foreach ($doc in $docs)
    {
        foreach ($h in $doc.Headings | Where-Object { $_.IsSection -and $_.Text -match '^D-(\d{3}) ' })
        {
            [pscustomobject]@{ Number = [int]$h.Text.Substring(2, 3); Heading = $h }
        }
    })

# Anchors are per file: "features/recording.md#recording-pipeline".
$anchorIndex = @{}
foreach ($doc in $docs)
{
    foreach ($h in $doc.Headings)
    {
        $key = "$($doc.Name)#$($h.Anchor)"
        if ($anchorIndex.ContainsKey($key))
        {
            $errors.Add("$($doc.Rel):$($h.Line + 1): heading '$($h.Text)' produces the same anchor as an earlier heading in this file; rename one")
        }
        else
        {
            $anchorIndex[$key] = $h
        }
    }
}

# Resolves a markdown link target written in $Doc. Returns @{ Kind = 'heading'|'file'|'external'|'broken'; Heading; Name }.
function Resolve-Link($Doc, [string]$Target)
{
    if ($Target -match '^[a-z]+:' )
    {
        return @{ Kind = 'external' }
    }
    $file, $anchor = if ($Target.Contains('#')) { $Target.Split('#', 2) } else { @($Target, '') }
    if ($file -eq '')
    {
        $name = $Doc.Name
    }
    else
    {
        $full = [System.IO.Path]::GetFullPath((Join-Path (Split-Path $Doc.Path -Parent) $file))
        if (-not $full.StartsWith($KbDir, [System.StringComparison]::OrdinalIgnoreCase))
        {
            return @{ Kind = if (Test-Path -LiteralPath $full) { 'external' } else { 'broken' } }
        }
        $name = [System.IO.Path]::GetRelativePath($KbDir, $full) -replace '\\', '/'
        if (-not ($docs | Where-Object { $_.Name -eq $name }))
        {
            return @{ Kind = 'broken' }
        }
    }
    if ($anchor -eq '')
    {
        return @{ Kind = 'file'; Name = $name }
    }
    $key = "$name#$anchor"
    if ($anchorIndex.ContainsKey($key))
    {
        return @{ Kind = 'heading'; Heading = $anchorIndex[$key]; Name = $name }
    }
    return @{ Kind = 'broken' }
}

function Get-LinkedHeadings($Doc, [string]$Cell)
{
    return @([regex]::Matches($Cell, '\]\(([^)\s]+)\)') | ForEach-Object {
            $r = Resolve-Link $Doc $_.Groups[1].Value
            if ($r.Kind -eq 'heading') { $r.Heading }
        })
}

# ---------------------------------------------------------------- sections: text, bytes, lessons

$script:ownTextCache = @{}
$script:bytesCache = @{}

function Get-SectionKey($Section)
{
    return "$($Section.Doc.Name)#$($Section.Anchor)"
}

# The lines a heading owns itself, up to the next heading of any level.
function Get-OwnText($Section)
{
    $key = Get-SectionKey $Section
    if (-not $script:ownTextCache.ContainsKey($key))
    {
        $script:ownTextCache[$key] = ($Section.Doc.Lines[$Section.Line..$Section.OwnEnd] -join "`n")
    }
    return $script:ownTextCache[$key]
}

# What reading a section costs: its own lines, plus the parent "## " preamble for a "### " section.
function Get-SectionBytes($Section)
{
    $key = Get-SectionKey $Section
    if (-not $script:bytesCache.ContainsKey($key))
    {
        $n = Get-Bytes (Get-OwnText $Section)
        if ($Section.Level -eq 3 -and $null -ne $Section.Parent -and $Section.Parent.Level -eq 2)
        {
            $n += Get-Bytes (Get-OwnText $Section.Parent)
        }
        $script:bytesCache[$key] = $n
    }
    return $script:bytesCache[$key]
}

# The text an agent reads for a section: the parent preamble (for "###"), then the section itself.
function Get-SectionReadText($Section)
{
    $own = Get-OwnText $Section
    if ($Section.Level -eq 3 -and $null -ne $Section.Parent -and $Section.Parent.Level -eq 2)
    {
        return (Get-OwnText $Section.Parent).TrimEnd() + "`n`n" + $own
    }
    return $own
}

function Get-SectionLessons($Section)
{
    $found = [System.Collections.Generic.List[string]]::new()
    # A "###" section also carries its parent "##" preamble, so lessons linked there are its lessons too.
    $owners = @($Section)
    if ($Section.Level -eq 3 -and $null -ne $Section.Parent -and $Section.Parent.Level -eq 2)
    {
        $owners += $Section.Parent
    }
    foreach ($owner in $owners)
    {
        for ($k = $owner.Line; $k -le $owner.OwnEnd; $k++)
        {
            if (-not $owner.Doc.Fenced[$k] -and $owner.Doc.Lines[$k] -match '^\s*-\s*Lesson:\s*(.+?)\s*$' -and -not $found.Contains($Matches[1]))
            {
                $found.Add($Matches[1])
            }
        }
    }
    return $found
}

function Format-Section($Section)
{
    $kb = [Math]::Round((Get-SectionBytes $Section) / 1024.0, 1)
    return "$($Section.Doc.Rel):$($Section.Line + 1)  $($Section.Text)  ($kb KB)"
}

# A heading that only groups "###" sections is replaced by those sections.
function Expand-Heading($Heading)
{
    if ($Heading.IsContainer)
    {
        return @($Heading.Children | Where-Object { $_.IsSection })
    }
    return @($Heading)
}

# lessons.md: heading -> line, bytes, and its "###" subsection names.
$lessonInfo = $null
$lessonDuplicates = [System.Collections.Generic.List[string]]::new()
$lessonLines = @()
if (Test-Path $LessonsPath)
{
    $lessonInfo = [ordered]@{}
    $lessonLines = [System.IO.File]::ReadAllLines($LessonsPath)
    $current = $null
    $inLessonFence = $false
    for ($i = 0; $i -lt $lessonLines.Length; $i++)
    {
        if ($lessonLines[$i] -match '^\s*```')
        {
            $inLessonFence = -not $inLessonFence
        }
        if (-not $inLessonFence -and $lessonLines[$i] -match '^##\s+(.+?)\s*$')
        {
            if ($null -ne $current) { $current.End = $i - 1 }
            if ($lessonInfo.Contains($Matches[1])) { $lessonDuplicates.Add($Matches[1]) }
            $current = [pscustomobject]@{ Heading = $Matches[1]; Line = $i; End = $lessonLines.Length - 1; Subs = [System.Collections.Generic.List[string]]::new(); Bytes = 0 }
            $lessonInfo[$Matches[1]] = $current
        }
        elseif (-not $inLessonFence -and $null -ne $current -and $lessonLines[$i] -match '^###\s+(.+?)\s*$')
        {
            $current.Subs.Add($Matches[1])
        }
    }
    foreach ($l in $lessonInfo.Values)
    {
        $l.Bytes = Get-Bytes ($lessonLines[$l.Line..$l.End] -join "`n")
    }
}

function Get-LessonText([string]$Heading)
{
    $l = $lessonInfo[$Heading]
    return ($lessonLines[$l.Line..$l.End] -join "`n").TrimEnd()
}

# ---------------------------------------------------------------- File map (file-map.md)

$fileMap = [System.Collections.Generic.List[object]]::new()
$mapHeading = if ($mapDoc) { $mapDoc.Headings | Where-Object { $_.Level -eq 2 -and $_.Text -eq $FileMapHeading } | Select-Object -First 1 } else { $null }
if ($mapHeading)
{
    for ($i = $mapHeading.Line + 1; $i -le $mapHeading.EndLine; $i++)
    {
        if ($mapDoc.Fenced[$i] -or -not $mapDoc.Lines[$i].TrimStart().StartsWith('|'))
        {
            continue
        }
        $cells = Split-TableRow $mapDoc.Lines[$i]
        $patterns = @([regex]::Matches($cells[0], '`([^`]+)`') | ForEach-Object { $_.Groups[1].Value })
        if ($patterns.Count -eq 0)
        {
            continue
        }
        if ($cells.Count -ne 3)
        {
            $errors.Add("$($mapDoc.Rel):$($i + 1): File map row needs three cells (Path, What lives here, Read first), found $($cells.Count)")
            continue
        }
        $linked = @(Get-LinkedHeadings $mapDoc $cells[2])
        foreach ($lh in @($linked | Where-Object { $_.IsContainer }))
        {
            $errors.Add("$($mapDoc.Rel):$($i + 1): File map row links the container heading '$($lh.Text)'; link one of its ### sections")
        }
        $fileMap.Add([pscustomobject]@{
            Line = $i
            Patterns = $patterns
            Regexes = @($patterns | ForEach-Object { ConvertFrom-Glob $_ })
            Sections = @($linked | ForEach-Object { Expand-Heading $_ })
            Description = $cells[1]
        })
    }
}

function Test-RowMatch($Row, [string]$Path)
{
    foreach ($r in $Row.Regexes)
    {
        if ($r.IsMatch($Path))
        {
            return $true
        }
    }
    return $false
}

function Get-RowFileCount($Row)
{
    if (-not $Row.PSObject.Properties['FileCount'])
    {
        $count = @(Get-TrackedCached | Where-Object { Test-RowMatch $Row $_ }).Count
        $Row | Add-Member -NotePropertyName FileCount -NotePropertyValue ([Math]::Max(1, $count))
    }
    return $Row.FileCount
}

# ---------------------------------------------------------------- features (area files)

# "| F-NN | Feature | Triggered from | Entry point | Telemetry | Tests | Read first |". IDs are stable: never renumbered or reused.
$features = [System.Collections.Generic.List[object]]::new()
foreach ($doc in $featureDocs)
{
    for ($i = 0; $i -lt $doc.Lines.Length; $i++)
    {
        if ($doc.Fenced[$i] -or -not $doc.Lines[$i].TrimStart().StartsWith('|'))
        {
            continue
        }
        $cells = Split-TableRow $doc.Lines[$i]
        if ($cells[0] -notmatch '^F-\d{2,3}$')
        {
            continue
        }
        if ($cells.Count -ne 7)
        {
            $errors.Add("$($doc.Rel):$($i + 1): feature row needs seven cells (ID, Feature, Triggered from, Entry point, Telemetry, Tests, Read first), found $($cells.Count)")
            continue
        }
        $pathsIn = {
            param($Cell)
            @([regex]::Matches($Cell, '`([^`]+)`') | ForEach-Object { $_.Groups[1].Value } | Where-Object { $_ -match '/' -and $_ -notmatch '\s' })
        }
        $linked = @(Get-LinkedHeadings $doc $cells[6])
        foreach ($lh in @($linked | Where-Object { $_.IsContainer }))
        {
            $errors.Add("$($doc.Rel):$($i + 1): feature $($cells[0]) links the container heading '$($lh.Text)'; link one of its ### sections")
        }
        $features.Add([pscustomobject]@{
            Doc = $doc
            Line = $i
            Id = $cells[0]
            Name = $cells[1]
            EntryPaths = & $pathsIn $cells[3]
            TestPaths = & $pathsIn $cells[5]
            Events = @([regex]::Matches($cells[4], '`([a-z0-9_]+)`') | ForEach-Object { $_.Groups[1].Value })
            Sections = @($linked | ForEach-Object { Expand-Heading $_ })
        })
    }
}

# Features whose entry point or tests name the path (a folder matches every path under it).
function Get-FeaturesForPath([string]$Path)
{
    return @($features | Where-Object {
            @(@($_.EntryPaths) + @($_.TestPaths) | Where-Object {
                    if ($Path.EndsWith('/')) { $_.StartsWith($Path, [System.StringComparison]::OrdinalIgnoreCase) }
                    else { [string]::Equals($_, $Path, [System.StringComparison]::OrdinalIgnoreCase) }
                }).Count -gt 0
        })
}

# ---------------------------------------------------------------- workflow rules (workflow.md)

# "<!-- rule:W-NN roles=a,b -->" on the line before a bullet, paragraph, or table; "<!-- rule-section roles=a -->" on the
# line before a "## " heading. A rule is the block up to the next blank line.
$rules = [System.Collections.Generic.List[object]]::new()
$ruleSections = [System.Collections.Generic.List[object]]::new()
if ($workflowDoc)
{
    $wl = $workflowDoc.Lines
    for ($i = 0; $i -lt $wl.Length; $i++)
    {
        if ($workflowDoc.Fenced[$i] -or $wl[$i] -notmatch '^\s*<!--\s*rule')
        {
            continue
        }
        $where = "$($workflowDoc.Rel):$($i + 1)"
        if ($wl[$i] -match '^\s*<!--\s*rule-section\s+roles=(\S+)\s*-->\s*$')
        {
            $roleList = @($Matches[1] -split ',' | Where-Object { $_ })
            if ($roleList.Count -eq 0)
            {
                $errors.Add("${where}: rule-section trigger needs a nonempty list of valid roles")
            }
            foreach ($r in $roleList | Where-Object { $KnownRoles -notcontains $_ })
            {
                $errors.Add("${where}: rule-section trigger names unknown role '$r'")
            }
            $next = if ($i + 1 -lt $wl.Length) { $wl[$i + 1] } else { '' }
            $headingMatch = [regex]::Match($next, '^##\s+(.+?)\s*$')
            if (-not $headingMatch.Success)
            {
                $errors.Add("${where}: rule-section trigger must be followed by a '## ' heading line")
                continue
            }
            $ruleSections.Add([pscustomobject]@{ Roles = $roleList; Line = $i + 2; Text = $headingMatch.Groups[1].Value; Anchor = ConvertTo-Anchor $headingMatch.Groups[1].Value })
        }
        elseif ($wl[$i] -match '^\s*<!--\s*rule:(W-\d{2,3})\s+roles=(\S+)\s*-->\s*$')
        {
            $id = $Matches[1]
            $roleList = @($Matches[2] -split ',' | Where-Object { $_ })
            if ($roleList.Count -eq 0)
            {
                $errors.Add("${where}: rule $id needs a nonempty list of valid roles")
            }
            foreach ($r in $roleList | Where-Object { $KnownRoles -notcontains $_ })
            {
                $errors.Add("${where}: rule $id names unknown role '$r'")
            }
            if ($rules | Where-Object { $_.Id -eq $id })
            {
                $errors.Add("${where}: duplicate rule ID $id")
            }
            $start = $i + 1
            if ($start -ge $wl.Length -or $wl[$start].Trim() -eq '' -or $wl[$start] -match '^#' -or $wl[$start] -match '^\s*<!--')
            {
                $errors.Add("${where}: rule $id must be followed by the bullet, paragraph, or table it marks")
                continue
            }
            # A bullet is one item plus its continuation lines, a numbered list or table is the whole block, a paragraph runs to the blank line.
            $first = $wl[$start]
            $indent = $first.Length - $first.TrimStart().Length
            $isBullet = $first -match '^\s*[-*]\s'
            $end = $start
            while ($end + 1 -lt $wl.Length -and $wl[$end + 1].Trim() -ne '' -and $wl[$end + 1] -notmatch '^\s*<!--' -and $wl[$end + 1] -notmatch '^#')
            {
                $nextLine = $wl[$end + 1]
                $nextIndent = $nextLine.Length - $nextLine.TrimStart().Length
                if ($isBullet -and $nextIndent -le $indent -and $nextLine -match '^\s*([-*]|\d+\.)\s')
                {
                    break
                }
                $end++
            }
            $rules.Add([pscustomobject]@{ Id = $id; Roles = $roleList; Line = $i + 1; Start = $start; End = $end; Text = ($wl[$start..$end] -join "`n") })
        }
        else
        {
            $errors.Add("${where}: malformed rule marker; use <!-- rule:W-NN roles=all|coordinator|executor|reviewer|helper[,...] --> or <!-- rule-section roles=... -->")
        }
    }
}

function Get-RoleRules([string]$ForRole)
{
    return @($rules | Where-Object { $_.Roles -contains 'all' -or $_.Roles -contains $ForRole })
}

function Get-RoleSections([string]$ForRole)
{
    return @($ruleSections | Where-Object { $_.Roles -contains 'all' -or $_.Roles -contains $ForRole })
}

# ---------------------------------------------------------------- guidance for a path

# Turns a user path (relative to -BaseDir or the current folder, absolute, either slash) into a repo-relative one.
# Returns $null outside the repo. A folder gets exactly one trailing '/'.
function Resolve-RepoPath([string]$Raw, [string]$BaseDir = $DefaultBase)
{
    $full = [System.IO.Path]::GetFullPath($Raw, $BaseDir)
    $rel = ((ConvertTo-RepoRelative $full)).TrimEnd('/')
    if ($rel -eq '.' -or $rel -eq '..' -or $rel.StartsWith('../') -or [System.IO.Path]::IsPathRooted($rel))
    {
        return $null
    }
    if (Test-Path -LiteralPath $full -PathType Container)
    {
        return "$rel/"
    }
    return $rel
}

# File map rows, the sections they link, the features of the path and their sections, and other sections naming
# the path. Weight: a narrow File map row beats a catch-all; a feature's own sections weigh like a narrow row;
# a section that only names the path weighs least.
function Get-Guidance([string]$Path)
{
    if ($Path.EndsWith('/'))
    {
        $under = @(Get-TrackedCached | Where-Object { $_.StartsWith($Path, [System.StringComparison]::OrdinalIgnoreCase) })
        $rows = @($fileMap | Where-Object { $row = $_; $under | Where-Object { Test-RowMatch $row $_ } | Select-Object -First 1 })
        $needle = "``$Path"
    }
    else
    {
        $rows = @($fileMap | Where-Object { Test-RowMatch $_ $Path })
        $needle = "``$Path``"
    }

    $weights = [ordered]@{}
    $byKey = @{}
    $add = {
        param($Section, [double]$Weight)
        $key = Get-SectionKey $Section
        $byKey[$key] = $Section
        if (-not $weights.Contains($key) -or $weights[$key] -lt $Weight)
        {
            $weights[$key] = $Weight
        }
    }
    foreach ($row in $rows)
    {
        $w = 1.0 / (1.0 + [Math]::Log10((Get-RowFileCount $row)))
        foreach ($s in $row.Sections) { & $add $s $w }
    }
    $pathFeatures = @(Get-FeaturesForPath $Path)
    foreach ($f in $pathFeatures)
    {
        foreach ($s in $f.Sections) { & $add $s 0.8 }
    }
    $mentions = @(foreach ($doc in ($docs | Where-Object { $_ -ne $mapDoc }))
        {
            foreach ($h in $doc.Headings | Where-Object { $_.IsSection })
            {
                if (-not $weights.Contains((Get-SectionKey $h)) -and
                    ((Get-OwnText $h).IndexOf($needle, [System.StringComparison]::OrdinalIgnoreCase) -ge 0))
                {
                    $h
                }
            }
        })
    return [pscustomobject]@{
        Path = $Path
        Exists = Test-Path -LiteralPath (Join-Path $RepoRoot $Path)
        Rows = $rows
        Features = @($pathFeatures)
        ReadFirst = @($weights.Keys | ForEach-Object { $byKey[$_] })
        Weights = $weights
        Mentions = $mentions
    }
}

$StopTokens = @('cs', 'xaml', 'json', 'md', 'ps1', 'i', 'service', 'services', 'window', 'windows', 'view', 'views',
    'model', 'models', 'viewmodel', 'viewmodels', 'test', 'tests', 'pointframe', 'helper', 'helpers', 'the', 'and', 'for',
    'handler', 'handlers', 'infrastructure', 'directory', 'package', 'packages', 'props', 'iss', 'csproj', 'docs', 'src')

# Name tokens of every path segment: "Pointframe/Views/OverlayWindow.RecordingHud.cs" -> overlay, recording, hud.
function Get-PathTokens([string[]]$Paths)
{
    $tokens = [System.Collections.Generic.HashSet[string]]::new()
    foreach ($p in $Paths)
    {
        foreach ($part in ($p.TrimEnd('/') -split '/'))
        {
            foreach ($m in [regex]::Matches($part, '[A-Z]{2,}(?=[A-Z][a-z]|\b|[^A-Za-z])|[A-Z]?[a-z]+|[A-Z]+'))
            {
                $t = $m.Value.ToLowerInvariant()
                if ($t.Length -ge 3 -and $StopTokens -notcontains $t)
                {
                    [void]$tokens.Add($t)
                }
            }
        }
    }
    return @($tokens)
}

# Lessons of all guided sections, ranked: name-token relevance (IDF-weighted, so a rare word such as
# "hud" beats a common one such as "recording") first, then the weight of the section they come from.
function Get-RankedLessons($Guidances, [string[]]$Paths)
{
    $sectionWeight = [ordered]@{}
    $sections = [ordered]@{}
    foreach ($g in $Guidances)
    {
        foreach ($s in $g.ReadFirst)
        {
            $key = Get-SectionKey $s
            $sections[$key] = $s
            if (-not $sectionWeight.Contains($key) -or $sectionWeight[$key] -lt $g.Weights[$key])
            {
                $sectionWeight[$key] = $g.Weights[$key]
            }
        }
        foreach ($s in $g.Mentions)
        {
            $key = Get-SectionKey $s
            $sections[$key] = $s
            if (-not $sectionWeight.Contains($key))
            {
                $sectionWeight[$key] = 0.2
            }
        }
    }
    $candidates = [ordered]@{}
    foreach ($key in $sectionWeight.Keys)
    {
        foreach ($l in (Get-SectionLessons $sections[$key]))
        {
            if (-not $candidates.Contains($l) -or $candidates[$l] -lt $sectionWeight[$key])
            {
                $candidates[$l] = $sectionWeight[$key]
            }
        }
    }
    $n = $candidates.Count
    $idf = @{}
    foreach ($t in (Get-PathTokens $Paths))
    {
        $rx = [regex]::new("\b$([regex]::Escape($t))", 'IgnoreCase')
        $df = @($candidates.Keys | Where-Object { $rx.IsMatch($_) }).Count
        if ($df -gt 0)
        {
            $idf[$t] = [pscustomobject]@{ Regex = $rx; Weight = [Math]::Log(($n + 1) / $df) }
        }
    }
    $order = 0
    $ranked = foreach ($l in $candidates.Keys)
    {
        $relevance = 0.0
        foreach ($entry in $idf.Values)
        {
            if ($entry.Regex.IsMatch($l))
            {
                $relevance += $entry.Weight
            }
        }
        [pscustomobject]@{ Heading = $l; Score = $relevance + $candidates[$l]; Order = $order++ }
    }
    return @($ranked | Sort-Object @{ Expression = 'Score'; Descending = $true }, Order | ForEach-Object { $_.Heading })
}

if (($ReadMode -or $InitMode -or $Changed -or $Hook -or $StatsMode) -and -not $mapHeading)
{
    if ($Hook) { exit 0 }
    Write-Host "ERROR no '## $FileMapHeading' section in $FileMapName"
    exit 1
}

# ---------------------------------------------------------------- read

function New-Entry([string]$Text, [string]$Color = '')
{
    return [pscustomobject]@{ Text = $Text; Color = $Color }
}

function Write-Entries($Entries)
{
    foreach ($e in $Entries)
    {
        if ($e.Color) { Write-Host $e.Text -ForegroundColor $e.Color } else { Write-Host $e.Text }
    }
}

function Format-AreaEntries($Doc)
{
    $e = [System.Collections.Generic.List[object]]::new()
    $e.Add((New-Entry "$($Doc.Rel)  $($Doc.Title)" 'Cyan'))
    foreach ($f in $features | Where-Object { $_.Doc -eq $Doc })
    {
        $e.Add((New-Entry "  feature $($f.Id) $($f.Name)  (line $($f.Line + 1))"))
    }
    foreach ($h in $Doc.Headings | Where-Object { $_.IsSection })
    {
        $e.Add((New-Entry "  section $(Format-Section $h)"))
    }
    return $e
}

function Format-FeatureEntries($Feature)
{
    $tests = @($Feature.TestPaths)
    $events = @($Feature.Events)
    $e = [System.Collections.Generic.List[object]]::new()
    $e.Add((New-Entry "$($Feature.Id) $($Feature.Name)  ($($Feature.Doc.Rel):$($Feature.Line + 1))" 'Cyan'))
    $e.Add((New-Entry "  entry   $(@($Feature.EntryPaths) -join ', ')"))
    $e.Add((New-Entry "  tests   $(if ($tests.Count) { $tests -join ', ' } else { 'none (known gap)' })"))
    $e.Add((New-Entry "  events  $(if ($events.Count) { $events -join ', ' } else { 'none' })"))
    foreach ($s in $Feature.Sections)
    {
        $e.Add((New-Entry "  read    $(Format-Section $s)"))
    }
    return $e
}

# Sections whose text mentions the topic often, densest first: used when no heading or feature name matches.
function Find-TopicSections([string]$Topic)
{
    $rx = [regex]::new([regex]::Escape($Topic), 'IgnoreCase')
    $hits = foreach ($doc in $docs)
    {
        foreach ($h in $doc.Headings | Where-Object { $_.IsSection })
        {
            $c = $rx.Matches((Get-OwnText $h)).Count
            if ($c -ge 2 -and (Get-SectionBytes $h) -le $SectionLimit)
            {
                [pscustomobject]@{ Section = $h; Density = $c / [Math]::Max(1.0, (Get-SectionBytes $h) / 1024.0) }
            }
        }
    }
    return @($hits | Sort-Object Density -Descending | Select-Object -First 5 | ForEach-Object { $_.Section })
}

# Resolves targets (feature ID, area, repo path, or topic) to printable entries, the sections to read, and the
# ranked lessons. Nothing is printed here; read prints the entries and init also prints the bodies.
function Invoke-Read([string[]]$Raws, [bool]$ShowAll)
{
    $entries = [System.Collections.Generic.List[object]]::new()
    $required = [ordered]@{}
    $kinds = [System.Collections.Generic.List[string]]::new()
    $problems = 0
    $pathTargets = [System.Collections.Generic.List[string]]::new()
    $areaDocs = [System.Collections.Generic.List[object]]::new()
    foreach ($raw in $Raws)
    {
        $feature = $features | Where-Object { $_.Id -ieq $raw } | Select-Object -First 1
        $areaDoc = $featureDocs | Where-Object { [System.IO.Path]::GetFileNameWithoutExtension($_.Path) -ieq $raw -or $_.Title -ieq $raw } | Select-Object -First 1
        if ($feature)
        {
            foreach ($x in (Format-FeatureEntries $feature)) { $entries.Add($x) }
            foreach ($s in $feature.Sections) { $required[(Get-SectionKey $s)] = $s }
            $kinds.Add('feature')
        }
        elseif ($areaDoc)
        {
            foreach ($x in (Format-AreaEntries $areaDoc)) { $entries.Add($x) }
            $areaDocs.Add($areaDoc)
            $kinds.Add('area')
        }
        elseif ($raw -match '[\\/]' -or $raw -match '\.\w{1,6}$' -or (Test-Path -LiteralPath $raw))
        {
            $pathTargets.Add($raw)
            $kinds.Add('path')
        }
        else
        {
            $featureHits = @($features | Where-Object { $_.Name.IndexOf($raw, [System.StringComparison]::OrdinalIgnoreCase) -ge 0 })
            $headingHits = @(foreach ($doc in $docs)
                {
                    $doc.Headings | Where-Object { ($_.IsSection -or $_.IsContainer) -and $_.Text.IndexOf($raw, [System.StringComparison]::OrdinalIgnoreCase) -ge 0 }
                })
            $textHits = @()
            if ($featureHits.Count + $headingHits.Count -eq 0)
            {
                $textHits = @(Find-TopicSections $raw)
            }
            if ($featureHits.Count + $headingHits.Count + $textHits.Count -eq 0)
            {
                $entries.Add((New-Entry "$raw  -> no area, feature, heading, text, or path matches; try a file path or an area: $(($featureDocs | ForEach-Object { [System.IO.Path]::GetFileNameWithoutExtension($_.Path) }) -join ', ')" 'Yellow'))
                $problems++
                continue
            }
            $entries.Add((New-Entry $raw 'Cyan'))
            foreach ($f in $featureHits)
            {
                $entries.Add((New-Entry "  feature $($f.Id) $($f.Name)  ($($f.Doc.Rel):$($f.Line + 1))"))
                if ($headingHits.Count + $textHits.Count -eq 0)
                {
                    foreach ($s in $f.Sections) { $required[(Get-SectionKey $s)] = $s }
                }
            }
            foreach ($hit in $headingHits)
            {
                # A container heading (only "###" children) resolves to its child sections.
                foreach ($h in (Expand-Heading $hit))
                {
                    if (-not $required.Contains((Get-SectionKey $h)))
                    {
                        $via = if ($hit.IsContainer) { "  (under '$($hit.Text)')" } else { '' }
                        $entries.Add((New-Entry "  section $(Format-Section $h)$via"))
                        $required[(Get-SectionKey $h)] = $h
                    }
                }
            }
            foreach ($h in $textHits)
            {
                $entries.Add((New-Entry "  section $(Format-Section $h)  (text match)"))
                $required[(Get-SectionKey $h)] = $h
            }
            $kinds.Add('topic')
        }
    }

    $lessons = @()
    $allLessons = @()
    $nonPath = @($required.Values)
    $lessonGuidances = @()
    $lessonPaths = @()
    if ($pathTargets.Count -gt 0)
    {
        $resolved = [System.Collections.Generic.List[string]]::new()
        foreach ($raw in $pathTargets)
        {
            $path = Resolve-RepoPath $raw
            if ($null -eq $path)
            {
                $entries.Add((New-Entry "  outside the repository: $raw"))
                $problems++
            }
            elseif (-not $resolved.Contains($path))
            {
                $resolved.Add($path)
            }
        }

        $guidances = @($resolved | ForEach-Object { Get-Guidance $_ })
        foreach ($g in $guidances)
        {
            $suffix = if (-not $g.Exists) { '  (not on disk yet)' } else { '' }
            if ($g.Rows.Count -eq 0)
            {
                $entries.Add((New-Entry "$($g.Path)$suffix  -> no File map row matches; add one" 'Yellow'))
                $problems++
            }
            else
            {
                $entries.Add((New-Entry "$($g.Path)$suffix" 'Cyan'))
            }
        }

        # Merge across paths: each row, feature, and section once, with the paths that reached it.
        $multi = $resolved.Count -gt 1
        $collect = {
            param($Table, [string]$Key, $Item, [string]$Path)
            if (-not $Table.Contains($Key)) { $Table[$Key] = [pscustomobject]@{ Item = $Item; Paths = [System.Collections.Generic.List[string]]::new() } }
            $Table[$Key].Paths.Add($Path)
        }
        $via = { param($Paths) if ($multi) { "  <- $(@($Paths | Sort-Object -Unique) -join ', ')" } else { '' } }
        $rowHits = [ordered]@{}
        $featureHits = [ordered]@{}
        $readHits = [ordered]@{}
        $alsoHits = [ordered]@{}
        foreach ($g in $guidances)
        {
            foreach ($row in $g.Rows) { & $collect $rowHits "$($row.Line)" $row $g.Path }
            foreach ($f in $g.Features) { & $collect $featureHits $f.Id $f $g.Path }
            foreach ($s in $g.ReadFirst) { & $collect $readHits (Get-SectionKey $s) $s $g.Path }
            foreach ($s in $g.Mentions) { & $collect $alsoHits (Get-SectionKey $s) $s $g.Path }
        }
        foreach ($h in $rowHits.Values | Sort-Object { $_.Item.Line })
        {
            $entries.Add((New-Entry "  row     $($mapDoc.Rel):$($h.Item.Line + 1)  $($h.Item.Description)$(& $via $h.Paths)"))
        }
        foreach ($h in $featureHits.Values)
        {
            $entries.Add((New-Entry "  feature $($h.Item.Id) $($h.Item.Name)  ($($h.Item.Doc.Rel):$($h.Item.Line + 1))$(& $via $h.Paths)"))
        }
        foreach ($h in $readHits.Values)
        {
            $entries.Add((New-Entry "  read    $(Format-Section $h.Item)$(& $via $h.Paths)"))
            $required[(Get-SectionKey $h.Item)] = $h.Item
        }
        foreach ($h in $alsoHits.Values | Where-Object { -not $readHits.Contains((Get-SectionKey $_.Item)) })
        {
            $entries.Add((New-Entry "  also    $(Format-Section $h.Item)  (names the path)$(& $via $h.Paths)"))
        }

        $lessonGuidances = @($guidances)
        $lessonPaths = @($resolved)
    }
    # Feature, area, and topic targets rank the lessons of the sections they name, like a path does.
    if ($nonPath.Count -gt 0)
    {
        $nonPathWeights = [ordered]@{}
        foreach ($s in $nonPath) { $nonPathWeights[(Get-SectionKey $s)] = 1.0 }
        $lessonGuidances += [pscustomobject]@{ ReadFirst = $nonPath; Mentions = @(); Weights = $nonPathWeights }
    }
    if ($lessonGuidances.Count -gt 0)
    {
        $allLessons = @(Get-RankedLessons $lessonGuidances $lessonPaths)
        $lessons = @(if ($ShowAll) { $allLessons } else { $allLessons | Select-Object -First 5 })
        foreach ($l in $lessons)
        {
            $entries.Add((New-Entry "  lesson  $l"))
        }
        if ($allLessons.Count -gt $lessons.Count)
        {
            $entries.Add((New-Entry "  ...     $($allLessons.Count - $lessons.Count) more related lessons; add -All to list them"))
        }
    }
    return [pscustomobject]@{
        Entries = $entries
        Required = @($required.Values)
        Lessons = $lessons
        AllLessons = $allLessons
        Problems = $problems
        Kinds = @($kinds)
        AreaDocs = @($areaDocs)
        Paths = @($pathTargets)
    }
}

if ($ReadMode)
{
    # No target: where to start.
    if ($For.Count -eq 0)
    {
        Write-Host "$($mainDoc.Rel)  cross-cutting: composition, decisions, shared rules and recipes; also file-map.md, references.md, tooling.md, workflow.md" -ForegroundColor Cyan
        foreach ($doc in $featureDocs)
        {
            $ids = @($features | Where-Object { $_.Doc -eq $doc } | ForEach-Object { $_.Id })
            Write-Host "  $($doc.Rel)  $($doc.Title)  ($($ids -join ', '))"
        }
        Write-Host 'Next: kb.ps1 read <path|area|F-NN|topic>'
        exit 0
    }
    $result = Invoke-Read $For ([bool]$All)
    Write-Entries $result.Entries
    exit ([int]($result.Problems -gt 0))
}

# ---------------------------------------------------------------- -Changed

if ($Changed)
{
    $diffFiles = @(& git -C $RepoRoot diff --name-only $Base 2>$null)
    if ($LASTEXITCODE -ne 0)
    {
        Write-Host "ERROR git diff against '$Base' failed; is it a valid ref?"
        exit 1
    }
    $untracked = @(& git -C $RepoRoot ls-files --others --exclude-standard 2>$null)
    $changedFiles = @(@($diffFiles) + @($untracked) | Where-Object { $_ } | Sort-Object -Unique)
    $bySection = [ordered]@{}
    $touched = [ordered]@{}
    $uncovered = [System.Collections.Generic.List[string]]::new()
    foreach ($f in $changedFiles)
    {
        $g = Get-Guidance $f
        if ($g.Rows.Count -eq 0)
        {
            $uncovered.Add($f)
        }
        foreach ($feat in $g.Features)
        {
            $touched[$feat.Id] = $feat
        }
        foreach ($s in @($g.ReadFirst) + @($g.Mentions))
        {
            $key = Get-SectionKey $s
            if (-not $bySection.Contains($key))
            {
                $bySection[$key] = [pscustomobject]@{ Section = $s; Files = [System.Collections.Generic.List[string]]::new() }
            }
            $bySection[$key].Files.Add($f)
        }
    }
    $kbChanged = @($changedFiles | Where-Object { $_ -like 'docs/knowledge-base/*' }).Count -gt 0
    Write-Host "$($changedFiles.Count) changed files against $Base; knowledge base changed: $(if ($kbChanged) { 'yes' } else { 'no' })"
    foreach ($entry in $bySection.Values)
    {
        $names = @($entry.Files | Select-Object -First 4)
        $more = if ($entry.Files.Count -gt 4) { " +$($entry.Files.Count - 4) more" } else { '' }
        Write-Host "  review  $(Format-Section $entry.Section)  <- $($names -join ', ')$more"
    }
    foreach ($feat in $touched.Values)
    {
        Write-Host "  feature $($feat.Id) $($feat.Name): re-check its triggers, telemetry, and tests ($($feat.Doc.Rel):$($feat.Line + 1))"
    }
    foreach ($f in $uncovered)
    {
        Write-Host "  no row  $f"
    }
    exit 0
}

# ---------------------------------------------------------------- hook guidance

# What the hook says for a path, given what this session already received. Sections, features, and lessons are
# remembered separately, so a lesson that is newly relevant for another path is shown even when its sections are not.
function Get-HookGuidance([string]$Path, $Delivered)
{
    if ($Delivered.Contains("P|$Path"))
    {
        return $null
    }
    $g = Get-Guidance $Path
    $sections = @(@($g.ReadFirst) + @($g.Mentions))
    if ($sections.Count -eq 0 -and $g.Features.Count -eq 0)
    {
        return $null
    }
    $newSections = @($sections | Where-Object { -not $Delivered.Contains("S|$(Get-SectionKey $_)") })
    $newFeatures = @($g.Features | Where-Object { -not $Delivered.Contains("F|$($_.Id)") })
    $fresh = @(Get-RankedLessons @($g) @($Path) | Where-Object { -not $Delivered.Contains("L|$_") })
    $shown = @($fresh | Select-Object -First 5)
    if ($newSections.Count -eq 0 -and $newFeatures.Count -eq 0 -and $shown.Count -eq 0)
    {
        return $null
    }
    $sb = [System.Text.StringBuilder]::new()
    if ($newSections.Count -gt 0)
    {
        [void]$sb.AppendLine("Knowledge base guidance for $Path. Read these sections before editing if you have not already:")
        foreach ($s in $newSections)
        {
            [void]$sb.AppendLine("- $(Format-Section $s)")
        }
    }
    else
    {
        [void]$sb.AppendLine("Knowledge base guidance for ${Path}: its sections were already given this session.")
    }
    if ($newFeatures.Count -gt 0)
    {
        [void]$sb.AppendLine("User-facing features implemented or tested here: $(($newFeatures | ForEach-Object { "$($_.Id) $($_.Name) ($($_.Doc.Rel))" }) -join '; ')")
    }
    if ($shown.Count -gt 0)
    {
        [void]$sb.AppendLine('Most relevant lessons.md headings (past bugs in this area):')
        foreach ($l in $shown)
        {
            [void]$sb.AppendLine("- $l")
        }
        if ($fresh.Count -gt $shown.Count)
        {
            [void]$sb.AppendLine("- $($fresh.Count - $shown.Count) more: pwsh scripts/kb.ps1 read $Path -All")
        }
    }
    $keys = @("P|$Path") + @(@($newSections | ForEach-Object { "S|$(Get-SectionKey $_)" }) + @($newFeatures | ForEach-Object { "F|$($_.Id)" }) + @($shown | ForEach-Object { "L|$_" }))
    return [pscustomobject]@{ Text = $sb.ToString().TrimEnd(); Keys = $keys }
}

# ---------------------------------------------------------------- -Hook

if ($Hook)
{
    # A hook must never get in the way of a tool call: any failure exits 0 with no output.
    try
    {
        $payload = [Console]::In.ReadToEnd() | ConvertFrom-Json
        $toolInput = $payload.tool_input
        $target = if ($toolInput.PSObject.Properties['file_path']) { $toolInput.file_path } elseif ($toolInput.PSObject.Properties['notebook_path']) { $toolInput.notebook_path } else { $null }
        if (-not $target)
        {
            exit 0
        }
        $baseDir = if ($payload.PSObject.Properties['cwd'] -and $payload.cwd) { $payload.cwd } else { $DefaultBase }
        $path = Resolve-RepoPath $target $baseDir
        if ($null -eq $path -or $path.EndsWith('/'))
        {
            exit 0
        }
        $stateDir = Join-Path ([System.IO.Path]::GetTempPath()) 'pointframe-kb-hook'
        [void](New-Item -ItemType Directory -Force -Path $stateDir)
        $session = if ($payload.PSObject.Properties['session_id']) { $payload.session_id -replace '[^\w-]', '' } else { 'unknown' }
        $stateFile = Join-Path $stateDir "$session.txt"
        $delivered = [System.Collections.Generic.HashSet[string]]::new()
        if (Test-Path $stateFile)
        {
            foreach ($line in Get-Content -LiteralPath $stateFile) { [void]$delivered.Add($line) }
        }
        $guidance = Get-HookGuidance $path $delivered
        if ($null -eq $guidance)
        {
            exit 0
        }
        Add-Content -LiteralPath $stateFile -Value $guidance.Keys
        $eventName = if ($payload.PSObject.Properties['hook_event_name']) { $payload.hook_event_name } else { 'PostToolUse' }
        # EscapeNonAscii keeps the JSON valid whatever the console code page is (em dashes in lesson headings).
        @{
            hookSpecificOutput = @{
                hookEventName = $eventName
                additionalContext = $guidance.Text
            }
        } | ConvertTo-Json -Depth 4 -Compress -EscapeHandling EscapeNonAscii
    }
    catch
    {
        # Silent by design; set KB_HOOK_DEBUG=1 to see why a hook call produced nothing.
        if ($env:KB_HOOK_DEBUG)
        {
            [Console]::Error.WriteLine("knowledge-base hook: $($_.Exception.Message) at $($_.InvocationInfo.PositionMessage)")
        }
    }
    exit 0
}

# ---------------------------------------------------------------- init

# One read-only probe with a 2 second timeout. Returns $null when it fails, times out, or throws: the caller prints "unknown".
function Invoke-Probe([scriptblock]$Block, [object[]]$Arguments = @())
{
    try
    {
        if ($env:KB_PROBE_FAULT)
        {
            throw 'probe fault'
        }
        if (Get-Command Start-ThreadJob -ErrorAction SilentlyContinue)
        {
            $job = Start-ThreadJob -ScriptBlock $Block -ArgumentList $Arguments
            if (-not (Wait-Job -Job $job -Timeout 2))
            {
                Stop-Job -Job $job
                Remove-Job -Job $job -Force
                return $null
            }
            $value = Receive-Job -Job $job -ErrorAction Stop
            Remove-Job -Job $job -Force
            return [pscustomobject]@{ Value = $value }
        }
        # No bounded way to run it here: never run a probe synchronously, report unknown.
        return $null
    }
    catch
    {
        return $null
    }
}

function Get-EnvironmentLines
{
    $lines = [System.Collections.Generic.List[string]]::new()
    if ($NoProbe)
    {
        foreach ($name in @('Pointframe.Mcp.exe', 'desktop lock', 'trial hidden installs', 'git worktrees'))
        {
            $lines.Add("- ${name}: not probed (-NoProbe)")
        }
        return $lines
    }

    $mcp = Invoke-Probe { @(Get-Process -Name 'Pointframe.Mcp' -ErrorAction SilentlyContinue | ForEach-Object { $_.Id }) }
    $lines.Add($(if ($null -eq $mcp) { '- Pointframe.Mcp.exe: unknown' }
            elseif (@($mcp.Value).Count -gt 0) { "- Pointframe.Mcp.exe: running (pid $(@($mcp.Value) -join ', '))" }
            else { '- Pointframe.Mcp.exe: not running' }))

    $lockPath = Join-Path ([System.IO.Path]::GetTempPath()) 'pointframe-desktop.lock'
    $lock = Invoke-Probe {
        param($Path)
        if (-not (Test-Path -LiteralPath $Path)) { return 'absent' }
        $content = ''
        try
        {
            $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
            try { $content = [System.IO.StreamReader]::new($stream).ReadToEnd() } finally { $stream.Dispose() }
        }
        catch
        {
            return 'present; content not readable'
        }
        $pidMatch = [regex]::Match($content, '(?i)(?:"?pid"?\s*[:=]\s*)?\b(\d{2,9})\b')
        if (-not $pidMatch.Success) { return 'present; no pid recorded' }
        $alive = [bool](Get-Process -Id ([int]$pidMatch.Groups[1].Value) -ErrorAction SilentlyContinue)
        return "present; records pid $($pidMatch.Groups[1].Value) ($(if ($alive) { 'alive' } else { 'not alive' }))"
    } @($lockPath)
    $lines.Add($(if ($null -eq $lock) { '- %TEMP%\pointframe-desktop.lock: unknown' } else { "- %TEMP%\pointframe-desktop.lock: $($lock.Value)" }))

    $localAppData = [Environment]::GetFolderPath('LocalApplicationData')
    $hidden = Invoke-Probe {
        param($Local)
        $folders = @(Get-ChildItem -LiteralPath (Join-Path $Local 'Programs') -Directory -Filter '.pointframe-trial-hidden-*' -Force -ErrorAction SilentlyContinue).Count
        $markers = @(Get-ChildItem -Path (Join-Path $Local 'Pointframe\trial-hidden') -Filter '*.json' -File -ErrorAction SilentlyContinue).Count
        return "$folders hidden-install folder(s) in Programs, $markers marker file(s) in Pointframe\trial-hidden"
    } @($localAppData)
    $lines.Add($(if ($null -eq $hidden) { '- trial hidden installs: unknown' } else { "- trial hidden installs: $($hidden.Value)" }))

    $worktrees = Invoke-Probe {
        param($Repo)
        $out = & git -C $Repo worktree list 2>$null
        if ($LASTEXITCODE -ne 0) { throw 'git failed' }
        return @($out | Where-Object { $_ }).Count
    } @($RepoRoot)
    $lines.Add($(if ($null -eq $worktrees) { '- git worktrees: unknown' } else { "- git worktrees: $($worktrees.Value) listed by git worktree list" }))
    return $lines
}

# Active records of plan/status.json, allowlisted fields only. Returns $null when the file is missing or unreadable.
function Get-StatusDigest
{
    $statusPath = Join-Path $RepoRoot 'plan' 'status.json'
    if (-not (Test-Path -LiteralPath $statusPath))
    {
        return $null
    }
    try
    {
        $status = Get-Content -Raw -LiteralPath $statusPath | ConvertFrom-Json
        $stamp = (Get-Item -LiteralPath $statusPath).LastWriteTimeUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")
        $clip = { param($v) $s = [string]$v; if ($s.Length -gt 200) { $s.Substring(0, 197) + '...' } else { $s } }
        $out = [System.Collections.Generic.List[string]]::new()
        $out.Add("Local operational context (plan/status.json, $stamp), not policy")
        $count = 0
        foreach ($job in @($status.jobs))
        {
            if (-not $job.PSObject.Properties['state'] -or $job.state -ne 'active')
            {
                continue
            }
            $count++
            $title = if ($job.PSObject.Properties['title'] -and $job.title) { $job.title } elseif ($job.PSObject.Properties['objective']) { $job.objective } else { '' }
            $out.Add("- $(& $clip $title)")
            foreach ($field in @('stage', 'next', 'blocker', 'due'))
            {
                if ($job.PSObject.Properties[$field] -and "$($job.$field)".Trim())
                {
                    $out.Add("    ${field}: $(& $clip $job.$field)")
                }
            }
        }
        if ($count -eq 0)
        {
            $out.Add('- no active records')
        }
        return $out
    }
    catch
    {
        return $null
    }
}

function Add-Overage($Lines, [string]$Part, [int]$Bytes, [int]$Budget)
{
    if ($Bytes -gt $Budget)
    {
        $Lines.Add("over budget: $Part is $Bytes bytes (~$(Get-Tokens $Bytes) tokens), budget $Budget bytes")
    }
}

# The text of init, part by part: core, rules, environment, status (coordinator), target.
function Get-InitParts([string]$ForRole, [string[]]$Raws)
{
    $parts = [ordered]@{}
    $core = [System.Text.StringBuilder]::new()
    [void]$core.AppendLine("=== Knowledge base core: $($mainDoc.Rel) ===")
    [void]$core.AppendLine(($mainDoc.Lines -join "`n").TrimEnd())
    $parts['core'] = $core.ToString().TrimEnd()

    $rb = [System.Text.StringBuilder]::new()
    [void]$rb.AppendLine("=== Workflow rules for role $ForRole ($($workflowDoc.Rel); verbatim, stable IDs) ===")
    $roleRules = @(Get-RoleRules $ForRole)
    if ($roleRules.Count -eq 0)
    {
        [void]$rb.AppendLine('(no rules are marked for this role)')
    }
    foreach ($r in $roleRules)
    {
        [void]$rb.AppendLine("[$($r.Id)]")
        [void]$rb.AppendLine($r.Text)
    }
    $extra = @(Get-RoleSections $ForRole)
    if ($extra.Count -gt 0)
    {
        [void]$rb.AppendLine('Read these workflow sections too:')
        foreach ($s in $extra)
        {
            $h = $anchorIndex["workflow.md#$($s.Anchor)"]
            [void]$rb.AppendLine("- $(if ($h) { Format-Section $h } else { $s.Text })")
        }
    }
    $parts['rules'] = $rb.ToString().TrimEnd()

    $eb = [System.Text.StringBuilder]::new()
    [void]$eb.AppendLine('=== Environment (read-only probes, 2 s timeout each, facts only) ===')
    foreach ($l in (Get-EnvironmentLines)) { [void]$eb.AppendLine($l) }
    $parts['environment'] = $eb.ToString().TrimEnd()

    if ($ForRole -eq 'coordinator')
    {
        $digest = Get-StatusDigest
        if ($null -ne $digest)
        {
            $sb = [System.Text.StringBuilder]::new()
            [void]$sb.AppendLine("=== $($digest[0]) ===")
            foreach ($l in $digest | Select-Object -Skip 1) { [void]$sb.AppendLine($l) }
            $parts['status'] = $sb.ToString().TrimEnd()
        }
    }

    if ($Raws.Count -gt 0)
    {
        $result = Invoke-Read $Raws $false
        $tb = [System.Text.StringBuilder]::new()
        [void]$tb.AppendLine("=== Target: $($Raws -join ', ') ===")
        foreach ($e in $result.Entries) { [void]$tb.AppendLine($e.Text) }
        $broad = ($result.Kinds -contains 'topic' -or $result.Kinds -contains 'feature') -and $result.Required.Count -gt 3
        if ($result.AreaDocs.Count -gt 0 -and $result.Required.Count -eq 0)
        {
            [void]$tb.AppendLine('menu: an area has too much to print; read one of the sections above (kb.ps1 read <area> lists them).')
        }
        elseif ($broad)
        {
            [void]$tb.AppendLine("menu: $($result.Required.Count) sections match, so no bodies are printed. Open the ones you need at file:line, or ask init for a narrower target.")
            foreach ($s in $result.Required)
            {
                [void]$tb.AppendLine("  $($s.Text)  $($s.Doc.Rel):$($s.Line + 1)  $(Get-SectionBytes $s) B")
            }
        }
        else
        {
            foreach ($s in $result.Required)
            {
                [void]$tb.AppendLine('')
                if ($s.Doc.IsMain)
                {
                    [void]$tb.AppendLine("----- $($s.Doc.Rel):$($s.Line + 1)  $($s.Text): already in the core above -----")
                    continue
                }
                [void]$tb.AppendLine("----- $($s.Doc.Rel):$($s.Line + 1)  $($s.Text) -----")
                [void]$tb.AppendLine((Get-SectionReadText $s).TrimEnd())
            }
            foreach ($l in $result.Lessons)
            {
                [void]$tb.AppendLine('')
                [void]$tb.AppendLine("----- lessons.md: $l -----")
                [void]$tb.AppendLine((Get-LessonText $l))
            }
        }
        $parts['target'] = $tb.ToString().TrimEnd()
    }
    return $parts
}

if ($InitMode)
{
    $parts = Get-InitParts $Role $For
    $overage = [System.Collections.Generic.List[string]]::new()
    $sizes = [System.Collections.Generic.List[string]]::new()
    $total = 0
    foreach ($name in $parts.Keys)
    {
        Write-Host $parts[$name]
        Write-Host ''
        $bytes = Get-Bytes $parts[$name]
        $total += $bytes
        $sizes.Add("$name $bytes B (~$(Get-Tokens $bytes) tokens)")
        if ($PartBudgets.Contains($name))
        {
            $budget = if ($name -eq 'rules' -and $Role -eq 'coordinator') { 16384 } else { $PartBudgets[$name] }
            Add-Overage $overage $name $bytes $budget
        }
    }
    if ($parts.Contains('target'))
    {
        Add-Overage $overage 'init with a target (total)' $total $InitTargetBudget
    }
    foreach ($o in $overage) { Write-Host $o }
    Write-Host "init size: $($sizes -join '; '); total $total B (~$(Get-Tokens $total) tokens)"
    exit 0
}

# ---------------------------------------------------------------- task suite and stats

function Get-TaskResults
{
    if (-not (Test-Path -LiteralPath $TasksPath))
    {
        return $null
    }
    $suite = Get-Content -Raw -LiteralPath $TasksPath | ConvertFrom-Json
    $results = foreach ($task in @($suite.tasks))
    {
        $read = Invoke-Read @($task.target) $false
        $requiredKeys = @($read.Required | ForEach-Object { Get-SectionKey $_ })
        $requiredBytes = 0
        foreach ($s in $read.Required) { $requiredBytes += Get-SectionBytes $s }
        $lessonBytes = 0
        foreach ($l in $read.Lessons) { if ($lessonInfo -and $lessonInfo.Contains($l)) { $lessonBytes += $lessonInfo[$l].Bytes } }
        $hookBytes = 0
        if ($read.Kinds -contains 'path')
        {
            $p = Resolve-RepoPath $task.target
            if ($p)
            {
                $hook = Get-HookGuidance $p ([System.Collections.Generic.HashSet[string]]::new())
                if ($hook) { $hookBytes = Get-Bytes $hook.Text }
            }
        }
        $missing = [System.Collections.Generic.List[string]]::new()
        $expected = 0
        foreach ($e in @($task.sections)) { $expected++; if ($requiredKeys -notcontains $e) { $missing.Add("section $e") } }
        foreach ($e in @($task.lessons)) { $expected++; if (@($read.Lessons) -notcontains $e) { $missing.Add("lesson $e") } }
        [pscustomobject]@{
            Id = $task.id
            Target = $task.target
            Kind = ($read.Kinds -join '+')
            RequiredKeys = $requiredKeys
            RequiredBytes = $requiredBytes
            LessonBytes = $lessonBytes
            HookBytes = $hookBytes
            Expected = $expected
            Missing = @($missing)
            Recall = if ($expected -eq 0) { 1.0 } else { [Math]::Round(($expected - $missing.Count) / $expected, 3) }
        }
    }
    return @($results)
}

function Get-RecentFiles
{
    if ($Root) { return @() }
    try
    {
        $log = @(& git -C $RepoRoot log --since=90.days --name-only --pretty=format: 2>$null | Where-Object { $_ })
        if ($LASTEXITCODE -ne 0) { return @() }
        $tracked = [System.Collections.Generic.HashSet[string]]::new([string[]]@(Get-TrackedCached))
        return @($log | Where-Object { $tracked.Contains($_) } | Group-Object | Sort-Object Count -Descending | Select-Object -First 20 | ForEach-Object {
                $g = Get-Guidance $_.Name
                $bytes = 0
                foreach ($s in $g.ReadFirst) { $bytes += Get-SectionBytes $s }
                [pscustomobject]@{ Path = $_.Name; Commits = $_.Count; RequiredBytes = $bytes; Sections = @($g.ReadFirst | ForEach-Object { Get-SectionKey $_ }) }
            })
    }
    catch
    {
        return @()
    }
}

if ($StatsMode)
{
    $allSections = @(foreach ($doc in $docs) { $doc.Headings | Where-Object { $_.IsSection } })
    $sectionRows = @($allSections | ForEach-Object {
            [pscustomobject]@{
                key = Get-SectionKey $_
                file = $_.Doc.Name
                line = $_.Line + 1
                heading = $_.Text
                bytes = Get-SectionBytes $_
                ownBytes = Get-Bytes (Get-OwnText $_)
                tokens = Get-Tokens (Get-SectionBytes $_)
            }
        })
    $fileRows = @($docs | ForEach-Object { [pscustomobject]@{ name = $_.Name; bytes = $_.Bytes; tokens = Get-Tokens $_.Bytes } })
    $core = [pscustomobject]@{ file = $mainDoc.Name; bytes = $mainDoc.Bytes; tokens = Get-Tokens $mainDoc.Bytes; limit = $CoreLimit }
    $initParts = Get-InitParts 'executor' @()
    $initBytes = [ordered]@{}
    foreach ($name in $initParts.Keys) { $initBytes[$name] = Get-Bytes $initParts[$name] }
    $tasks = @(Get-TaskResults)
    $recent = @(Get-RecentFiles)

    if ($Json)
    {
        [ordered]@{
            core = $core
            files = $fileRows
            sections = $sectionRows
            init = $initBytes
            tasks = @($tasks | ForEach-Object {
                    [ordered]@{
                        id = $_.Id; target = $_.Target; kind = $_.Kind; requiredBytes = $_.RequiredBytes; requiredSections = $_.RequiredKeys
                        lessonBytes = $_.LessonBytes; hookBytes = $_.HookBytes; recall = $_.Recall; missing = $_.Missing
                    }
                })
            recentFiles = $recent
        } | ConvertTo-Json -Depth 6
        exit 0
    }

    Write-Host 'Files (bytes, estimated tokens = bytes / 4)' -ForegroundColor Cyan
    foreach ($f in $fileRows | Sort-Object bytes -Descending)
    {
        Write-Host ('  {0,-34} {1,8} B {2,7} tok' -f $f.name, $f.bytes, $f.tokens)
    }
    Write-Host "Core $($core.file): $($core.bytes) B (~$($core.tokens) tokens), limit $CoreLimit B" -ForegroundColor Cyan
    Write-Host 'Sections over 2 KB (a ### section includes its parent ## preamble)' -ForegroundColor Cyan
    foreach ($s in $sectionRows | Where-Object { $_.bytes -gt 2048 } | Sort-Object bytes -Descending)
    {
        $flag = if ($s.bytes -gt $SectionLimit) { ' OVER 8 KB' } else { '' }
        Write-Host ('  {0,7} B {1,6} tok  {2}:{3}  {4}{5}' -f $s.bytes, $s.tokens, $s.file, $s.line, $s.heading, $flag)
    }
    Write-Host 'init (executor, no target) bytes per part' -ForegroundColor Cyan
    foreach ($name in $initBytes.Keys) { Write-Host ('  {0,-12} {1,8} B (~{2} tokens)' -f $name, $initBytes[$name], (Get-Tokens $initBytes[$name])) }
    $initTotal = ($initBytes.Values | Measure-Object -Sum).Sum
    Write-Host ('  {0,-12} {1,8} B (~{2} tokens)' -f 'total', $initTotal, (Get-Tokens $initTotal))
    Write-Host 'Task suite (required sections / lesson bodies / hook output, bytes; recall of expected items)' -ForegroundColor Cyan
    foreach ($t in $tasks)
    {
        Write-Host ('  {0,-22} {1,-8} sections {2,7} B  lessons {3,6} B  hook {4,5} B  recall {5}' -f $t.Id, $t.Kind, $t.RequiredBytes, $t.LessonBytes, $t.HookBytes, $t.Recall)
        foreach ($m in $t.Missing) { Write-Host "      missing: $m" -ForegroundColor Yellow }
    }
    if ($tasks.Count -gt 0)
    {
        $sumRequired = ($tasks | Measure-Object RequiredBytes -Sum).Sum
        $sumLessons = ($tasks | Measure-Object LessonBytes -Sum).Sum
        $minRecall = ($tasks | Measure-Object Recall -Minimum).Minimum
        Write-Host "  total required sections $sumRequired B, lesson bodies $sumLessons B, lowest recall $minRecall"
    }
    if ($recent.Count -gt 0)
    {
        Write-Host 'Most-changed files in 90 days (commits, required-section bytes)' -ForegroundColor Cyan
        foreach ($r in $recent) { Write-Host ('  {0,4}  {1,7} B  {2}' -f $r.Commits, $r.RequiredBytes, $r.Path) }
    }
    exit 0
}

# ---------------------------------------------------------------- generated blocks (main file)

# Replaces the lines between two marker lines; returns the new line array, or $null when unchanged.
function Update-Block([string[]]$Lines, [string[]]$Markers, [string[]]$Desired, [string]$What)
{
    $start = -1
    $end = -1
    for ($i = 0; $i -lt $Lines.Length; $i++)
    {
        if ($Lines[$i].Trim() -eq $Markers[0]) { $start = $i }
        if ($Lines[$i].Trim() -eq $Markers[1]) { $end = $i }
    }
    if ($start -lt 0 -or $end -lt $start)
    {
        $errors.Add("$($mainDoc.Rel): markers '$($Markers[0])' and '$($Markers[1])' must both exist, in that order")
        return $null
    }
    $current = @()
    if ($end - $start -gt 1)
    {
        $current = @($Lines[($start + 1)..($end - 1)] | Where-Object { $_.Trim() -ne '' })
    }
    if (($current -join "`n") -eq ($Desired -join "`n"))
    {
        return $null
    }
    if ($Check)
    {
        $errors.Add("$($mainDoc.Rel): the $What is out of date; run kb.ps1 check")
        return $null
    }
    $notes.Add("$What refreshed ($($Desired.Count) entries)")
    return @($Lines[0..$start]) + @('') + @($Desired) + @('') + @($Lines[$end..($Lines.Length - 1)])
}

$tocEnd = ($mainDoc.Lines | Select-String -SimpleMatch $TocMarkers[1] | Select-Object -First 1)
$tocEndLine = if ($tocEnd) { $tocEnd.LineNumber - 1 } else { -1 }
$toc = @($mainDoc.Headings | Where-Object { $_.Line -gt $tocEndLine -and $_.Level -gt 1 } | ForEach-Object {
        $indent = if ($_.Level -eq 2) { '' } else { '  ' }
        "$indent- [$($_.Text)](#$($_.Anchor))"
    })
# Feature index: one line per area file, ordered by its lowest feature ID.
$areas = foreach ($doc in $featureDocs)
{
    $own = @($features | Where-Object { $_.Doc -eq $doc })
    $firstId = if ($own.Count) { ($own | ForEach-Object { [int]$_.Id.Substring(2) } | Measure-Object -Minimum).Minimum } else { [int]::MaxValue }
    $title = if ($doc.Title) { $doc.Title } else { $doc.Name }
    $list = if ($own.Count) { ($own | ForEach-Object { "$($_.Id) $($_.Name)" }) -join '; ' } else { 'no features yet' }
    [pscustomobject]@{ Order = $firstId; Line = "- [$title]($($doc.Name)): $list" }
}
$index = @($areas | Sort-Object Order | ForEach-Object { $_.Line })

# Decision index: every D-NNN in number order, linked into the file that holds it.
$decisionIndex = @($decisions | Sort-Object Number | ForEach-Object {
        $h = $_.Heading
        $superseded = if ((Get-OwnText $h) -match '(?m)^Superseded by') { ' (superseded)' } else { '' }
        "- [$($h.Text)]($($h.Doc.Name)#$($h.Anchor))$superseded"
    })

$mainLines = $mainDoc.Lines
$changedMain = $false
$blocks = @(
    @{ Markers = $TocMarkers; Desired = $toc; What = 'table of contents' },
    @{ Markers = $IndexMarkers; Desired = $index; What = 'Feature index' },
    @{ Markers = $DecisionMarkers; Desired = $decisionIndex; What = 'decision index' }
)
foreach ($block in $blocks)
{
    $updated = Update-Block $mainLines $block.Markers $block.Desired $block.What
    if ($null -ne $updated)
    {
        $mainLines = $updated
        $changedMain = $true
    }
}
if ($changedMain)
{
    [System.IO.File]::WriteAllText($MainPath, ($mainLines -join $mainDoc.Newline), [System.Text.UTF8Encoding]::new($false))
}

# ---------------------------------------------------------------- Codex copies of the kb-* skills

# Codex reads skills from .agents/skills. Those copies are generated from .claude/skills/kb-*/SKILL.md so the
# two never drift: check rewrites them, check -NoFix reports them. CLAUDE.md becomes AGENTS.md, and the
# Claude Code hook section is dropped.
$claudeSkills = Join-Path $RepoRoot '.claude' 'skills'
$agentSkills = Join-Path $RepoRoot '.agents' 'skills'
foreach ($skill in @(Get-ChildItem -Path $claudeSkills -Directory -Filter 'kb-*' -ErrorAction SilentlyContinue))
{
    $sourcePath = Join-Path $skill.FullName 'SKILL.md'
    if (-not (Test-Path $sourcePath))
    {
        continue
    }
    $rel = ".agents/skills/$($skill.Name)/SKILL.md"
    $mirrorPath = Join-Path $agentSkills $skill.Name 'SKILL.md'
    $text = [System.IO.File]::ReadAllText($sourcePath)
    $text = $text -replace '`CLAUDE\.md`', '`AGENTS.md`'
    $text = ($text -replace '(?s)\r?\n## The project hook.*$', '').TrimEnd() + "`n"
    $desired = $text -replace '\r?\n', $mainDoc.Newline
    $current = if (Test-Path $mirrorPath) { [System.IO.File]::ReadAllText($mirrorPath) } else { $null }
    if ($null -ne $current -and ($current -replace '\r\n', "`n") -eq ($desired -replace '\r\n', "`n"))
    {
        continue
    }
    if ($Check)
    {
        $errors.Add("$rel is out of date with .claude/skills/$($skill.Name)/SKILL.md; run kb.ps1 check")
    }
    else
    {
        [void](New-Item -ItemType Directory -Force -Path (Split-Path $mirrorPath -Parent))
        [System.IO.File]::WriteAllText($mirrorPath, $desired, [System.Text.UTF8Encoding]::new($false))
        $notes.Add("$rel regenerated from .claude/skills/$($skill.Name)")
    }
}

# ---------------------------------------------------------------- checks across all files

$tracked = @(Get-TrackedCached)

# Repo paths in backticks: must exist and must not be gitignored (an ignored file is visible on this machine
# but absent on every clone). Placeholders (<Name>, *) and URLs are skipped.
$seen = @{}
$existing = [System.Collections.Generic.List[string]]::new()
foreach ($doc in $docs)
{
    foreach ($m in [regex]::Matches(($doc.Lines -join "`n"), '`((?:[\w.-]+/)+[\w.-]+\.[A-Za-z0-9]+)`'))
    {
        $p = $m.Groups[1].Value
        if ($p -match '[<>*]' -or $p.StartsWith('http') -or $seen.ContainsKey($p))
        {
            continue
        }
        $seen[$p] = $true
        if (Test-Path (Join-Path $RepoRoot $p))
        {
            $existing.Add($p)
        }
        else
        {
            $errors.Add("$($doc.Rel): path does not exist: $p")
        }
    }
}
if ($existing.Count -gt 0 -and -not $Root)
{
    try
    {
        # Paths go as arguments in batches; piping to --stdin from PowerShell appends CRLF and nothing matches.
        for ($i = 0; $i -lt $existing.Count; $i += 50)
        {
            $batch = @($existing.GetRange($i, [Math]::Min(50, $existing.Count - $i)))
            $out = & git -C $RepoRoot check-ignore -- @batch 2>$null
            foreach ($p in @($out))
            {
                if ($p)
                {
                    $errors.Add("path is gitignored and will not exist on a clone: $($p.Trim())")
                }
            }
        }
    }
    catch
    {
        $notes.Add('git not available; gitignore check skipped')
    }
}

# Lesson references, links, and one "**Files.**" line per section.
if ($null -eq $lessonInfo)
{
    $notes.Add('lessons.md not present on this clone; lesson references not checked')
}
$linkedLessons = [System.Collections.Generic.HashSet[string]]::new()
foreach ($doc in $docs)
{
    for ($i = 0; $i -lt $doc.Lines.Length; $i++)
    {
        if ($doc.Fenced[$i])
        {
            continue
        }
        $line = $doc.Lines[$i]
        if ($null -ne $lessonInfo -and $line -match '^\s*-\s*Lesson:\s*(.+?)\s*$' -and -not $lessonInfo.Contains($Matches[1]))
        {
            $errors.Add("$($doc.Rel):$($i + 1): lesson heading not found in lessons.md: $($Matches[1])")
        }
        foreach ($m in [regex]::Matches($line, '\]\(([^)\s]+)\)'))
        {
            $target = $m.Groups[1].Value
            if (-not ($target.StartsWith('#') -or $target -match '\.md(#|$)'))
            {
                continue
            }
            if ((Resolve-Link $doc $target).Kind -eq 'broken')
            {
                $errors.Add("$($doc.Rel):$($i + 1): broken link: $target")
            }
        }
    }
    foreach ($h in $doc.Headings | Where-Object { $_.IsSection })
    {
        foreach ($l in (Get-SectionLessons $h))
        {
            [void]$linkedLessons.Add($l)
        }
        $filesLines = @($h.Line..$h.OwnEnd | Where-Object { -not $doc.Fenced[$_] -and $doc.Lines[$_] -match '^\*\*Files\.\*\*' })
        if ($filesLines.Count -gt 1)
        {
            $errors.Add("$($doc.Rel):$($filesLines[1] + 1): section '$($h.Text)' has $($filesLines.Count) **Files.** lines; a block is probably under the wrong heading")
        }
    }
}

# lessons.md: unique headings, one Problem / What fixed it / Takeaway each, at most one Root cause.
if ($null -ne $lessonInfo)
{
    foreach ($d in $lessonDuplicates)
    {
        $errors.Add("lessons.md: duplicate lesson heading: $d")
    }
    foreach ($l in $lessonInfo.Values)
    {
        $where = "lessons.md:$($l.Line + 1)"
        foreach ($need in @('Problem', 'What fixed it', 'Takeaway'))
        {
            $n = @($l.Subs | Where-Object { $_ -eq $need }).Count
            if ($n -ne 1)
            {
                $errors.Add("${where}: lesson '$($l.Heading)' has $n '### $need' subsections; it needs exactly one")
            }
        }
        if (@($l.Subs | Where-Object { $_ -eq 'Root cause' }).Count -gt 1)
        {
            $errors.Add("${where}: lesson '$($l.Heading)' has more than one '### Root cause' subsection")
        }
        foreach ($s in $l.Subs | Where-Object { $_ -notin @('Problem', 'Root cause', 'What fixed it', 'Takeaway') })
        {
            $errors.Add("${where}: lesson '$($l.Heading)' has an unknown subsection '### $s'; use Problem, Root cause, What fixed it, Takeaway")
        }
    }
}

# Decision numbers are global across files and never reused.
foreach ($group in $decisions | Group-Object Number | Where-Object { $_.Count -gt 1 })
{
    $where = ($group.Group | ForEach-Object { "$($_.Heading.Doc.Rel):$($_.Heading.Line + 1)" }) -join ', '
    $errors.Add("decision D-$('{0:D3}' -f [int]$group.Name) is used more than once: $where; numbers are never reused")
}

# Features: unique IDs, an entry point and a section each, telemetry events that exist, and every product
# telemetry event and every smoke test claimed by some feature.
$seenIds = @{}
foreach ($f in $features)
{
    $where = "$($f.Doc.Rel):$($f.Line + 1)"
    if ($seenIds.ContainsKey($f.Id))
    {
        $errors.Add("${where}: feature ID $($f.Id) is also used at $($seenIds[$f.Id]); IDs are never reused")
    }
    else
    {
        $seenIds[$f.Id] = $where
    }
    if (@($f.EntryPaths).Count -eq 0)
    {
        $errors.Add("${where}: feature $($f.Id) names no entry point file")
    }
    if (@($f.Sections).Count -eq 0)
    {
        $errors.Add("${where}: feature $($f.Id) links no section")
    }
}
$catalogPath = Join-Path $RepoRoot 'Pointframe' 'Services' 'Infrastructure' 'TelemetryEventCatalog.cs'
if (Test-Path $catalogPath)
{
    $eventsBlock = [regex]::Match([System.IO.File]::ReadAllText($catalogPath), '(?s)public static class TelemetryEvents\s*\{(.*?)\n\}').Groups[1].Value
    $catalogEvents = @([regex]::Matches($eventsBlock, 'const string \w+ = "([a-z0-9_]+)"') | ForEach-Object { $_.Groups[1].Value })
    $claimed = @{}
    foreach ($f in $features)
    {
        foreach ($ev in $f.Events)
        {
            $claimed[$ev] = $true
            if ($catalogEvents -notcontains $ev)
            {
                $errors.Add("$($f.Doc.Rel):$($f.Line + 1): feature $($f.Id) names telemetry event '$ev', which TelemetryEvents does not define")
            }
        }
    }
    foreach ($ev in $catalogEvents | Where-Object { -not $claimed.ContainsKey($_) })
    {
        $errors.Add("telemetry event '$ev' belongs to no feature; add it to a feature row")
    }
}
$claimedTests = @($features | ForEach-Object { $_.TestPaths })
foreach ($t in $tracked | Where-Object { $_ -like 'Pointframe.AutomationTests/Smoke/*.cs' -and $claimedTests -notcontains $_ })
{
    $errors.Add("smoke test $t belongs to no feature; add it to a feature row")
}

# File map: short descriptions, no dead patterns, full coverage, no redundant rows.
if (-not $mapHeading)
{
    $errors.Add("docs/knowledge-base/${FileMapName}: no '## $FileMapHeading' section")
}
elseif ($tracked.Count -eq 0)
{
    $notes.Add('git not available; File map coverage skipped')
}
else
{
    $covered = [System.Collections.Generic.HashSet[string]]::new()
    foreach ($row in $fileMap)
    {
        $where = "$($mapDoc.Rel):$($row.Line + 1)"
        if ($row.Description.Length -gt $MaxDescriptionLength)
        {
            $errors.Add("${where}: File map description is $($row.Description.Length) characters; keep it under $MaxDescriptionLength and move explanation into a section")
        }
        $rowFiles = [System.Collections.Generic.HashSet[string]]::new()
        for ($p = 0; $p -lt $row.Patterns.Count; $p++)
        {
            $rx = $row.Regexes[$p]
            $hits = @($tracked | Where-Object { $rx.IsMatch($_) })
            if ($hits.Count -eq 0)
            {
                $errors.Add("${where}: File map pattern matches no tracked file: $($row.Patterns[$p])")
            }
            foreach ($f in $hits)
            {
                [void]$rowFiles.Add($f)
                [void]$covered.Add($f)
            }
        }
        $row | Add-Member -NotePropertyName Files -NotePropertyValue $rowFiles -Force
        $row | Add-Member -NotePropertyName Keys -NotePropertyValue @($row.Sections | ForEach-Object { Get-SectionKey $_ }) -Force
    }

    $uncovered = @($tracked | Where-Object { -not $covered.Contains($_) })
    foreach ($f in $uncovered | Select-Object -First 20)
    {
        $errors.Add("tracked file matches no File map row: $f")
    }
    if ($uncovered.Count -gt 20)
    {
        $errors.Add("... and $($uncovered.Count - 20) more files with no File map row")
    }

    # Every matching row applies, so a row whose files and links are both inside another row's adds nothing.
    for ($r = 0; $r -lt $fileMap.Count; $r++)
    {
        $row = $fileMap[$r]
        if ($row.Files.Count -eq 0)
        {
            continue
        }
        for ($o = 0; $o -lt $fileMap.Count; $o++)
        {
            $other = $fileMap[$o]
            if ($o -eq $r -or -not $row.Files.IsSubsetOf($other.Files))
            {
                continue
            }
            $extra = @($row.Keys | Where-Object { $other.Keys -notcontains $_ })
            $identical = $other.Files.IsSubsetOf($row.Files) -and @($other.Keys | Where-Object { $row.Keys -notcontains $_ }).Count -eq 0
            if ($extra.Count -eq 0 -and -not ($identical -and $o -gt $r))
            {
                $errors.Add("$($mapDoc.Rel):$($row.Line + 1): File map row is redundant; line $($other.Line + 1) already covers its files and links")
                break
            }
        }
    }
}

# Workflow coverage: below the first "## " heading every bullet, paragraph, numbered list, and table must be a marked
# rule or sit under a rule-section trigger, so deleting a marker is an error rather than silent loss of guidance.
if ($workflowDoc)
{
    $wcl = $workflowDoc.Lines
    $coveredLines = [System.Collections.Generic.HashSet[int]]::new()
    foreach ($r in $rules) { foreach ($n in $r.Start..$r.End) { [void]$coveredLines.Add($n) } }
    foreach ($t in $ruleSections)
    {
        $th = $anchorIndex["workflow.md#$($t.Anchor)"]
        if ($th) { foreach ($n in $th.Line..$th.EndLine) { [void]$coveredLines.Add($n) } }
    }
    $firstSection = @($workflowDoc.Headings | Where-Object { $_.Level -eq 2 } | Select-Object -First 1)
    if ($firstSection.Count -gt 0)
    {
        $inGap = $false
        for ($n = $firstSection[0].Line; $n -lt $wcl.Length; $n++)
        {
            $line = $wcl[$n]
            $content = $line.Trim() -ne '' -and -not $workflowDoc.Fenced[$n] -and $line -notmatch '^#{1,6}\s' -and $line -notmatch '^\s*<!--' -and $line -notmatch '^\*\*Files\.\*\*'
            if ($content -and -not $coveredLines.Contains($n))
            {
                if (-not $inGap)
                {
                    $errors.Add("$($workflowDoc.Rel):$($n + 1): this block is covered by no <!-- rule:W-NN roles=... --> marker and by no rule-section trigger; mark it so kb init delivers it")
                    $inGap = $true
                }
            }
            elseif ($line.Trim() -eq '' -or $line -match '^#' -or $content)
            {
                $inGap = $false
            }
        }
    }
}

# Workflow rules: every role has rules.
if ($workflowDoc)
{
    foreach ($roleName in @('coordinator', 'executor', 'reviewer', 'helper'))
    {
        if (@(Get-RoleRules $roleName).Count -eq 0)
        {
            $errors.Add("$($workflowDoc.Rel): no <!-- rule:W-NN roles=... --> marks a rule for role '$roleName'")
        }
    }
}

# The task suite: each task's expected sections and lessons must still be what kb read gives.
$taskResults = Get-TaskResults
if ($null -eq $taskResults)
{
    $errors.Add("docs/knowledge-base/kb-tasks.json not found; the task suite measures retrieval recall")
}
else
{
    $suite = Get-Content -Raw -LiteralPath $TasksPath | ConvertFrom-Json
    foreach ($task in @($suite.tasks))
    {
        foreach ($e in @($task.sections) | Where-Object { $_ -and -not $anchorIndex.ContainsKey($_) })
        {
            $errors.Add("kb-tasks.json: task '$($task.id)' expects section '$e', which does not exist")
        }
        foreach ($e in @($task.lessons) | Where-Object { $_ -and $null -ne $lessonInfo -and -not $lessonInfo.Contains($_) })
        {
            $errors.Add("kb-tasks.json: task '$($task.id)' expects lesson '$e', which is not in lessons.md")
        }
    }
    foreach ($t in $taskResults | Where-Object { $_.Recall -lt 1.0 })
    {
        $errors.Add("task suite: task '$($t.Id)' ($($t.Target)) has recall $($t.Recall); missing $($t.Missing -join '; ')")
    }
}

# Every lesson must be reachable: linked from a section that a File map row, a feature row, or a task reaches.
if ($null -ne $lessonInfo)
{
    $reachSections = [System.Collections.Generic.HashSet[string]]::new()
    foreach ($row in $fileMap) { foreach ($s in $row.Sections) { [void]$reachSections.Add((Get-SectionKey $s)) } }
    foreach ($f in $features) { foreach ($s in $f.Sections) { [void]$reachSections.Add((Get-SectionKey $s)) } }
    foreach ($t in @($taskResults)) { foreach ($k in @($t.RequiredKeys)) { [void]$reachSections.Add($k) } }
    $reachLessons = [System.Collections.Generic.HashSet[string]]::new()
    foreach ($doc in $docs)
    {
        foreach ($h in $doc.Headings | Where-Object { $_.IsSection -and $reachSections.Contains((Get-SectionKey $_)) })
        {
            foreach ($l in (Get-SectionLessons $h)) { [void]$reachLessons.Add($l) }
        }
    }
    foreach ($l in $lessonInfo.Values | Where-Object { -not $reachLessons.Contains($_.Heading) })
    {
        if ($linkedLessons.Contains($l.Heading))
        {
            $errors.Add("lessons.md:$($l.Line + 1): lesson '$($l.Heading)' is linked only from sections that no File map row, feature row, or task suite path reaches, so kb.ps1 read never shows it")
        }
        else
        {
            $errors.Add("lessons.md:$($l.Line + 1): lesson '$($l.Heading)' is not linked from any section, so kb.ps1 read never shows it; add a '- Lesson:' line to its owning section")
        }
    }
}

# Budgets: a section over 8 KB and the core over 12 KB fail unless the baseline lists them at that size or more.
# The baseline can only shrink: check without -NoFix lowers or drops entries, never raises one.
$baselineSections = @{}
$baselineCore = $null
$registry = @{}
if (Test-Path -LiteralPath $BaselinePath)
{
    try
    {
        $b = Get-Content -Raw -LiteralPath $BaselinePath | ConvertFrom-Json -AsHashtable
        if ($b.ContainsKey('sections') -and $null -ne $b['sections']) { foreach ($k in $b['sections'].Keys) { $baselineSections[$k] = [int]$b['sections'][$k] } }
        if ($b.ContainsKey('coreBytes') -and $null -ne $b['coreBytes']) { $baselineCore = [int]$b['coreBytes'] }
        if ($b.ContainsKey('workflowRules') -and $null -ne $b['workflowRules']) { foreach ($k in $b['workflowRules'].Keys) { $registry[$k] = @($b['workflowRules'][$k] | ForEach-Object { [string]$_ } | Sort-Object -Unique) } }
    }
    catch
    {
        $errors.Add("docs/knowledge-base/kb-baseline.json is not valid JSON: $($_.Exception.Message)")
    }
}
$newSections = [ordered]@{}
$newCore = $null
$baselineChanged = $false

# Rule registry: the workflow rule IDs and their roles are recorded in kb-baseline.json (workflowRules). A registered ID
# that disappears or changes roles is an error even when a trigger still covers its block; check without -NoFix may
# add new IDs but never removes one or changes its roles (that is a hand edit of the JSON, visible in review).
$newRegistry = [ordered]@{}
foreach ($k in $registry.Keys | Sort-Object) { $newRegistry[$k] = $registry[$k] }
$registryAdded = $false
if ($workflowDoc)
{
    foreach ($k in $registry.Keys | Sort-Object)
    {
        $found = $rules | Where-Object { $_.Id -eq $k } | Select-Object -First 1
        if (-not $found)
        {
            $errors.Add("$($workflowDoc.Rel): rule $k is registered in kb-baseline.json (workflowRules) but its marker is missing; restore it (removing a rule is a hand edit of the registry)")
        }
        elseif ((@($found.Roles | Sort-Object -Unique) -join ',') -ne ($registry[$k] -join ','))
        {
            $errors.Add("$($workflowDoc.Rel):$($found.Line): rule $k has roles $(@($found.Roles | Sort-Object -Unique) -join ',') but the registry has $($registry[$k] -join ','); restore the roles (changing them is a hand edit of the registry)")
        }
    }
    foreach ($r in $rules | Where-Object { -not $registry.ContainsKey($_.Id) })
    {
        if ($Check)
        {
            $errors.Add("$($workflowDoc.Rel):$($r.Line): rule $($r.Id) is not in the kb-baseline.json registry (workflowRules); run kb.ps1 check to register it")
        }
        else
        {
            $newRegistry[$r.Id] = [string[]]@($r.Roles | Sort-Object -Unique)
            $registryAdded = $true
            $notes.Add("rule $($r.Id) registered in kb-baseline.json")
        }
    }
}
$overSections = @($docs | ForEach-Object { $_.Headings } | Where-Object { $_.IsSection -and (Get-SectionBytes $_) -gt $SectionLimit } | Sort-Object { Get-SectionKey $_ })
foreach ($s in $overSections)
{
    $key = Get-SectionKey $s
    $bytes = Get-SectionBytes $s
    $where = "$($s.Doc.Rel):$($s.Line + 1)"
    if (-not $baselineSections.ContainsKey($key))
    {
        $errors.Add("${where}: section '$($s.Text)' is $bytes bytes, over $SectionLimit; split or compress it (the baseline only lists violations that already existed)")
    }
    elseif ($bytes -gt $baselineSections[$key])
    {
        $errors.Add("${where}: section '$($s.Text)' grew from $($baselineSections[$key]) to $bytes bytes in the baseline; a listed section may only shrink")
        $newSections[$key] = $baselineSections[$key]
    }
    else
    {
        $newSections[$key] = $bytes
        if ($bytes -lt $baselineSections[$key]) { $baselineChanged = $true }
    }
}
$overKeys = @($overSections | ForEach-Object { Get-SectionKey $_ })
foreach ($key in $baselineSections.Keys | Where-Object { $overKeys -notcontains $_ } | Sort-Object)
{
    if ($Check)
    {
        $errors.Add("docs/knowledge-base/kb-baseline.json lists $key, which is within $SectionLimit bytes or gone; run kb.ps1 check to drop it from the baseline")
    }
    else
    {
        $baselineChanged = $true
    }
}
if ($mainDoc.Bytes -gt $CoreLimit)
{
    if ($null -eq $baselineCore)
    {
        $errors.Add("$($mainDoc.Rel): the core file is $($mainDoc.Bytes) bytes, over $CoreLimit; move text to references.md, tooling.md, or an area file (the baseline only lists violations that already existed)")
    }
    elseif ($mainDoc.Bytes -gt $baselineCore)
    {
        $errors.Add("$($mainDoc.Rel): the core file grew from $baselineCore to $($mainDoc.Bytes) bytes in the baseline; it may only shrink")
        $newCore = $baselineCore
    }
    else
    {
        $newCore = $mainDoc.Bytes
        if ($mainDoc.Bytes -lt $baselineCore) { $baselineChanged = $true }
    }
}
elseif ($null -ne $baselineCore)
{
    if ($Check)
    {
        $errors.Add("docs/knowledge-base/kb-baseline.json lists the core file, which is within $CoreLimit bytes; run kb.ps1 check to drop it from the baseline")
    }
    else
    {
        $baselineChanged = $true
    }
}
if (($baselineChanged -or $registryAdded) -and -not $Check)
{
    $out = [ordered]@{}
    if ($null -ne $newCore) { $out['coreBytes'] = $newCore }
    $out['sections'] = $newSections
    if ($newRegistry.Count -gt 0)
    {
        $sortedRegistry = [ordered]@{}
        foreach ($k in $newRegistry.Keys | Sort-Object) { $sortedRegistry[$k] = [string[]]@($newRegistry[$k]) }
        $out['workflowRules'] = $sortedRegistry
    }
    $nl = $mainDoc.Newline
    [System.IO.File]::WriteAllText($BaselinePath, ((($out | ConvertTo-Json -Depth 4) -replace "\r?\n", $nl) + $nl), [System.Text.UTF8Encoding]::new($false))
    $notes.Add("kb-baseline.json lowered ($($newSections.Count) sections listed)")
}

# Warnings: long prose lines, big area files, changelog phrasing in area files.
$generatedLines = @{}
$generatedKeys = @{}
foreach ($pair in @($TocMarkers, $IndexMarkers, $DecisionMarkers))
{
    $s0 = [array]::IndexOf(@($mainDoc.Lines | ForEach-Object { $_.Trim() }), $pair[0])
    $e0 = [array]::IndexOf(@($mainDoc.Lines | ForEach-Object { $_.Trim() }), $pair[1])
    if ($s0 -ge 0 -and $e0 -gt $s0) { foreach ($n in $s0..$e0) { $generatedLines[$n] = $true } }
}
$longLines = [System.Collections.Generic.List[string]]::new()
$changelogLines = [System.Collections.Generic.List[string]]::new()
foreach ($doc in $docs)
{
    $isArea = $doc.Name.StartsWith('features/')
    if ($isArea -and $doc.Bytes -gt $AreaFileLimit)
    {
        $warns.Add("$($doc.Rel) is $($doc.Bytes) bytes, over the $AreaFileLimit byte area-file guideline; split it into topics")
    }
    $decisionRanges = @($doc.Headings | Where-Object { $_.Text -match '^D-\d{3} ' } | ForEach-Object { , @($_.Line, $_.OwnEnd) })
    for ($i = 0; $i -lt $doc.Lines.Length; $i++)
    {
        $line = $doc.Lines[$i]
        if ($doc.Fenced[$i] -or ($doc.IsMain -and $generatedLines.ContainsKey($i)))
        {
            continue
        }
        $t = $line.TrimStart()
        $isTable = $t.StartsWith('|')
        if (-not $isTable -and -not $t.StartsWith('**Files.**') -and -not $t.StartsWith('#') -and $line.Length -gt $ProseLineLimit)
        {
            $longLines.Add("$($doc.Rel):$($i + 1) ($($line.Length) chars)")
        }
        if ($isArea -and $line -match '(?i)\b(?:since|added)\s+20\d{2}-\d{2}-\d{2}\b|\bAs of 20\d{2}\b')
        {
            $inDecision = $false
            foreach ($rg in $decisionRanges) { if ($i -ge $rg[0] -and $i -le $rg[1]) { $inDecision = $true } }
            if (-not $inDecision)
            {
                $changelogLines.Add("$($doc.Rel):$($i + 1)")
            }
        }
    }
}
$listWarn = {
    param([string]$Text, $Items)
    if ($Items.Count -gt 0)
    {
        $shown = if ($All) { $Items } else { @($Items | Select-Object -First 3) }
        $more = if (-not $All -and $Items.Count -gt 3) { "; +$($Items.Count - 3) more (check -All lists them)" } else { '' }
        $warns.Add("$($Items.Count) $Text ($($shown -join ', ')$more)")
    }
}
& $listWarn "prose line(s) over $ProseLineLimit characters (tables and **Files.** lines are exempt)" $longLines
& $listWarn 'changelog phrase(s) in area files (since/added YYYY-MM-DD, As of YYYY): state the current fact, not its history' $changelogLines

foreach ($n in $notes)
{
    Write-Host "note  $n"
}
foreach ($w in $warns)
{
    Write-Host "warn  $w" -ForegroundColor Yellow
}
foreach ($e in $errors)
{
    Write-Host "ERROR $e" -ForegroundColor Red
}
Write-Host ('{0} files, {1} features, {2} paths checked, {3} warnings, {4} errors' -f $docs.Count, $features.Count, $seen.Count, $warns.Count, $errors.Count)
if ($errors.Count -gt 0)
{
    exit 1
}
# Explicit: GitHub's pwsh step ends with "exit $LASTEXITCODE", and the last git call (check-ignore exits 1
# when nothing is ignored) would otherwise fail a clean check.
exit 0
