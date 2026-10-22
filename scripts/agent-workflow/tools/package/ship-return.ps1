# ---- ship: return to review ---------------------------------------------------------------------------------------

function Move-ShipToReview($Info, [string]$Why)
{
    # The reviewed head is gone (the rebase rewrote it, or the pin step changed more than the pin). The PASS and the acceptance are
    # invalidated and never restamped; the new head is checked, recorded as the commit under review, and the package waits for a review.
    $stateLock = Enter-StateLock $Info.StateRoot $Id
    try
    {
        $invalidatedReason = $Why
        Update-State $Info.StateRoot $Id 'ship-invalidated' {
            param($s)
            if ($s.acceptance) { $s.acceptance.invalidated = @{ utc = (Get-Now); reason = $invalidatedReason } }
            $s.stage = 'invalidated'
        } -Locked | Out-Null
        Write-Host "NOTE the earlier PASS and the acceptance no longer apply: $Why"
        $state = Read-State $Info.StateRoot $Id
        $wt = [string]$state.worktree
        $checked = Invoke-CheckCore $Info
        if (-not $checked)
        {
            Write-Host 'ship stopped: the check on the new head failed (stage checks-failed). Fix it, then check, commit and review.'
            return $false
        }
        $script:ExitCode = 3
        if (-not (Test-WorktreeClean $wt))
        {
            Write-Host 'ship stopped: the check changed files (stage checks-passed). Run commit, then review.'
            return $false
        }
        $state = Read-State $Info.StateRoot $Id
        $previous = $state.commit
        $baseRefName = [string]$state.baseRef
        $mergeBase = ([string](& git -C $wt merge-base HEAD $baseRefName 2>$null)).Trim()
        if (-not $mergeBase) { $mergeBase = [string]$state.baseSha }
        $entry = @{ sha = (Get-GitHead $wt); tree = (Get-GitTree $wt); contentTree = (Get-TreeHash $wt); utc = (Get-Now); message = [string](Get-Field $previous 'message'); runIds = @(Get-List (Get-Field $previous 'runIds')); executors = @(Get-List (Get-Field $previous 'executors'))
            baseSha = $mergeBase; rebaseChangedTree = $true; checkedAfterRebase = $true; author = [string](Get-Field $previous 'author'); refreshedByShip = $Why }
        Update-State $Info.StateRoot $Id 'ship-needs-review' {
            param($s)
            $s.commit = $entry
            $s.commits = @(Get-List $s.commits) + @($entry)
            $s.baseSha = $entry.baseSha
            $s.stage = 'needs-review'
            if ($s.ship) { $s.ship.outcome = 'needs-review'; $s.ship.endedUtc = (Get-Now) }
        } -Locked | Out-Null
        Write-Host "ship stopped: the head is no longer the reviewed one. Next: pwsh scripts/package.ps1 review -Id $Id -Level light|heavy"
        return $false
    }
    finally { $stateLock.Dispose() }
}

