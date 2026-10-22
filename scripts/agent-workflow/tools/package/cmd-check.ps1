# ---- check ------------------------------------------------------------------------------------------------------

function Invoke-Check
{
    $info = Get-RepoInfo $Repo
    $stateLock = Enter-StateLock $info.StateRoot $Id
    try { Invoke-CheckCore $info | Out-Null }
    finally { $stateLock.Dispose() }
}

function Invoke-CheckCore($info)
{
    # The package lock is held by the caller. Returns whether the check passed.
    try
    {
        Sync-Invalidation $info $Id
        $state = Read-State $info.StateRoot $Id
        Assert-StateConsistent $state $info
        if ([string]$state.stage -in @('creating', 'creation-failed', 'preflight-failed', 'running', 'executor-kill-failed', 'blocked-main-mutated', 'reviewing', 'shipping', 'auto-merge-enabled', 'merged')) { throw "check is not allowed in stage '$($state.stage)'" }
        $problems = @(Test-SnapshotsIntact $state)
        if ($problems.Count) { throw ($problems -join '; ') }
        $wt = [string]$state.worktree
        $manifest = Read-Manifest $state.manifest.path
        $paths = Get-PackagePaths $info.StateRoot $Id
        $checkId = [guid]::NewGuid().ToString('N')
        $dir = Join-Path $paths.Dir "checks/$checkId"
        $treeBefore = Get-TreeHash $wt
        $tree = $treeBefore
        $records = @()
        $buildFailed = $false
        $modifiedByValidator = $false
        $number = 0
        foreach ($command in (Get-CheckCommands $manifest))
        {
            $number++
            $log = Join-Path $dir "$number-$($command.kind).log"
            $commandLine = if ($command.kind -eq 'crlf') { 'normalize changed text files to CRLF' } else { "$($command.exe) $($command.args -join ' ')" }
            $record = @{ name = $command.name; kind = $command.kind; mutator = [bool]$command.mutator; command = $commandLine; exitCode = $null; skipped = $null; log = $null; inputTree = $tree; outputTree = $tree }
            if ($command.kind -eq 'test' -and $buildFailed) { $record.skipped = 'a build failed'; $records += @($record); continue }
            if ($command.kind -eq 'crlf')
            {
                $record.files = @(Invoke-CrlfNormalize $wt ([string]$state.baseSha))
                $record.exitCode = 0
            }
            else
            {
                $record.exitCode = Invoke-Logged $command.exe @($command.args) $wt $log
                $record.log = $log
                if ($command.kind -eq 'build' -and $record.exitCode -ne 0) { $buildFailed = $true }
            }
            $tree = Get-TreeHash $wt
            $record.outputTree = $tree
            if (-not $command.mutator -and $tree -ne $record.inputTree) { $record.modifiedWorktree = $true; $modifiedByValidator = $true }
            $records += @($record)
        }
        # The tracked files git sees as unchanged have to hold the bytes a checkout of them writes (the review snapshot is one); otherwise the
        # commands above ran on content the reviewer will never read. Named here, with the file list, instead of costing a review round.
        $rawProblems = @()
        try { $rawProblems = @(Get-RawByteProblems $wt) }
        catch { $rawProblems = @("(the raw-byte comparison could not run: $($_.Exception.Message))") }
        $ok = @($records | Where-Object { $null -ne $_.exitCode -and $_.exitCode -ne 0 }).Count -eq 0 -and -not $buildFailed -and -not $modifiedByValidator -and $rawProblems.Count -eq 0
        $record = @{ checkId = $checkId; utc = (Get-Now); ok = $ok; treeBefore = $treeBefore; treeHash = $tree; commands = $records; rawBytesProblems = @($rawProblems) }
        $stage = if ($ok) { 'checks-passed' } else { 'checks-failed' }
        Update-State $info.StateRoot $Id 'checked' {
            param($s)
            $s.checks = @($s.checks) + @($record)
            $s.stage = $stage
        } -Locked | Out-Null
        foreach ($item in $records) { Write-Host ("  {0,-6} {1}{2}" -f $(if ($item.skipped) { 'SKIP' } elseif ($item.exitCode -eq 0) { 'ok' } else { "exit $($item.exitCode)" }), $item.name, $(if (Get-Field $item 'modifiedWorktree') { ' (changed the worktree, so the result cannot be trusted)' } else { '' })) }
        if ($rawProblems.Count)
        {
            Write-Host "  RAW BYTES $($rawProblems.Count) tracked file(s) that git sees as unchanged differ from what a checkout of them writes (autocrlf or assume-unchanged hides the edit): $(($rawProblems | Select-Object -First 20) -join ', ')$(if ($rawProblems.Count -gt 20) { ", and $($rawProblems.Count - 20) more" })"
            Write-Host '  RAW BYTES the review snapshot is such a checkout, so these bytes would never be read by the reviewer. Restore the files (clear assume-unchanged, then git checkout -- <file>); nothing was rewritten.'
        }
        Write-Host "check ${checkId}: $(if ($ok) { 'passed' } else { 'FAILED' }) on tree $tree"
        if (-not $ok) { $script:ExitCode = 1 }
        return $ok
    }
    finally { }
}

