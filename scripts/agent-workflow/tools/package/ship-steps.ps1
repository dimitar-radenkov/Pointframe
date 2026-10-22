# ---- ship: the steps ---------------------------------------------------------------------------------------------

function Get-PackageSyncMainCommand($Info)
{
    # What a person types to bring the main checkout up to date: the consumer's wrapper of merge-pr.ps1 when it has one, else the kit tool.
    $wrapper = Join-Path $Info.MainCheckout 'scripts\merge-pr.ps1'
    if (Test-Path -LiteralPath $wrapper -PathType Leaf) { return 'pwsh scripts/merge-pr.ps1 -SyncMain' }
    $tool = if ($MergePrScript) { $MergePrScript } else { Join-Path $script:PackageToolsRoot 'merge-pr.ps1' }
    "pwsh -NoProfile -File `"$tool`" -SyncMain -Root `"$($Info.MainCheckout)`""
}

function Get-ShipMainCheckoutProblem($Info, $State, [string]$Wt, [string]$BaseRefName)
{
    # $null when the main checkout is at the base ref, or when this ship has already begun its merge: once the merge-pr call ran, origin/<base>
    # may legitimately contain the merge commit while the main checkout still lags, and that never blocks a resume. Otherwise the step record and the text.
    $mergeStep = Get-ShipStep $State 'merge'
    if ($mergeStep -and [string](Get-Field $mergeStep 'status') -in @('running', 'done')) { return $null }
    $mainHead = ([string](& git -C $Info.MainCheckout rev-parse --verify 'HEAD^{commit}' 2>$null)).Trim()
    $baseTip = ([string](& git -C $Wt rev-parse --verify "$BaseRefName^{commit}" 2>$null)).Trim()
    if ($mainHead -and $baseTip -and $mainHead -eq $baseTip) { return $null }
    $record = @{ status = 'failed'; cause = 'main-checkout-not-at-base'; mainCheckout = [string]$Info.MainCheckout; mainHead = $mainHead; baseRef = $BaseRefName; baseTip = $baseTip }
    $text = "ERROR the main checkout $($Info.MainCheckout) is at $(if ($mainHead) { $mainHead.Substring(0, [Math]::Min(10, $mainHead.Length)) } else { 'an unknown commit' }) but $BaseRefName is at $(if ($baseTip) { $baseTip.Substring(0, [Math]::Min(10, $baseTip.Length)) } else { 'an unknown commit' }): the agent-workflow.json that ship and the merge gate read lives there and may be stale. Bring it up to date first (when no package is running): $(Get-PackageSyncMainCommand $Info)"
    @{ Record = $record; Text = $text }
}

function Step-ShipBase($Info)
{
    $state = Read-State $Info.StateRoot $Id
    $wt = [string]$state.worktree
    $baseRefName = [string]$state.baseRef
    $remote = Get-PackageRemote $baseRefName
    if (-not $remote -and $script:Ctx.HasRemote) { return (Stop-Ship $Info 'ship-failed' 'base' @{ status = 'failed' } "ERROR ship needs a remote base ref such as origin/master, not '$baseRefName'") }
    # Without a remote there is nothing to fetch: the base is the local branch, and the rebase below is onto it as it is.
    if ($remote)
    {
        try { Assert-ShipRemote $wt $remote | Out-Null; Invoke-Git $wt @('fetch', $remote) | Out-Null }
        catch { return (Stop-Ship $Info 'ship-failed' 'base' @{ status = 'failed' } "ERROR cannot fetch ${remote}: $($_.Exception.Message)") }
    }
    # The gate configuration (hooks, required checks, pin files) is read from the main checkout. A main checkout that is not at the base ref
    # may hold a stale one, for example a hook that was null before the last merge: ship would record that and merge-pr would accept it.
    $mainProblem = Get-ShipMainCheckoutProblem $Info $state $wt $baseRefName
    if ($mainProblem)
    {
        return (Stop-Ship $Info 'ship-failed' 'base' $mainProblem.Record $mainProblem.Text)
    }
    # The rebase always runs: git leaves a branch that already contains the base alone, and that is the proven no-op. Anything else that
    # moves the head, the tree, the content or the review inputs returns the package to review.
    $head = Get-GitHead $wt
    $review = Get-ShipReview $state
    $preHead = $head
    $preTree = Get-GitTree $wt
    $preContent = Get-TreeHash $wt
    $rebaseProblem = $null
    $stateLock = Enter-StateLock $Info.StateRoot $Id
    try { Invoke-BaseRebase $Info $Id $wt $baseRefName 'ship-rebase-failed' | Out-Null }
    catch { $rebaseProblem = $_.Exception.Message }
    finally { $stateLock.Dispose() }
    if ($rebaseProblem) { return (Stop-Ship $Info 'ship-failed' 'base' @{ status = 'failed'; head = $preHead } "ERROR $rebaseProblem") }
    $postHead = Get-GitHead $wt
    $postTree = Get-GitTree $wt
    $postContent = Get-TreeHash $wt
    $unchanged = ($postHead -eq $preHead -and $postTree -eq $preTree -and $postContent -eq $preContent)
    $inputProblems = @(if ($unchanged) { Test-ReviewInputsAtAccept $state $review })
    if ($unchanged -and $inputProblems.Count -eq 0)
    {
        Set-ShipStep $Info 'base' @{ status = 'done'; head = $postHead; tree = $postTree; rebased = $true; noOp = $true; baseSha = (Invoke-Git $wt @('rev-parse', '--verify', "$baseRefName^{commit}")) }
        Write-Host "ship: the branch is on the current $baseRefName; the rebase changed nothing (same head, tree, content and review inputs), so the acceptance stands"
        return $true
    }
    $why = if ($unchanged) { "the review inputs changed after the rebase: $($inputProblems -join '; ')" } else { "the rebase onto $baseRefName rewrote the branch ($preHead -> $postHead, tree $preTree -> $postTree)" }
    Move-ShipToReview $Info $why | Out-Null
    $false
}

function Step-ShipPin($Info)
{
    $state = Read-State $Info.StateRoot $Id
    $wt = [string]$state.worktree
    $head = Get-GitHead $wt
    $done = Get-ShipStep $state 'pin'
    if ($done -and $done.status -eq 'done' -and [string]$done.head -eq $head -and (Test-WorktreeClean $wt)) { return $true }
    $mergePr = Resolve-ShipScript 'merge-pr' $MergePrScript (Join-Path $script:PackageToolsRoot 'merge-pr.ps1')
    $paths = Get-ShipPaths $Info
    $log = Join-Path $paths.Dir "pin-$([guid]::NewGuid().ToString('N')).log"
    Write-Host 'ship: merge-pr.ps1 -PinOnly (the plugin pin is moved before verify)'
    $code = Invoke-Logged 'pwsh' @('-NoProfile', '-File', $mergePr, '-PinOnly', '-Worktree', $wt, '-Root', [string]$Info.MainCheckout) $Info.MainCheckout $log -Live
    if ($code -ne 0) { return (Stop-Ship $Info 'ship-failed' 'pin' @{ status = 'failed'; head = $head; exitCode = $code; log = $log } "ERROR merge-pr.ps1 -PinOnly failed (exit $code); see $log") }
    $after = Get-GitHead $wt
    $review = Get-ShipReview $state
    $binding = Get-ReviewBinding $state $review
    if (-not $binding.Current)
    {
        Move-ShipToReview $Info "the plugin pin step changed more than the pin files, or left the worktree dirty: $($binding.Reason)" | Out-Null
        return $false
    }
    Set-ShipStep $Info 'pin' @{ status = 'done'; head = $after; changed = ($after -ne $head); pinOnly = [bool]$binding.PinOnly; log = $log }
    if ($after -ne $head) { Write-Host "ship: the plugin pin moved the head to $after (only the two pin files changed; the acceptance stands)" }
    $true
}

function Get-ShipPaths($Info)
{
    $dir = Join-Path (Get-PackagePaths $Info.StateRoot $Id).Dir 'ship'
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    [pscustomobject]@{ Dir = $dir }
}

function Step-ShipVerify($Info)
{
    $state = Read-State $Info.StateRoot $Id
    $wt = [string]$state.worktree
    if (-not (Test-WorktreeClean $wt)) { return (Stop-Ship $Info 'ship-failed' 'verify' @{ status = 'failed' } 'ERROR the worktree is not clean; verify needs the committed head') }
    $head = Get-GitHead $wt
    $tree = Get-WorkingTreeHash $wt
    $receiptPath = Join-Path $wt (Join-Path (Get-PackageVerifyDir) 'verdict.json')
    $done = Get-ShipStep $state 'verify'
    if ($done -and $done.status -eq 'done' -and [string]$done.head -eq $head -and [string]$done.tree -eq $tree -and (Test-Path -LiteralPath $receiptPath -PathType Leaf) -and (Get-Sha256 $receiptPath) -eq [string]$done.receipt.sha256 -and (Test-VerifyReceiptObject (Read-VerifyReceipt $receiptPath) $head $tree).Ok)
    {
        Write-Host "ship: verify.ps1 already passed for head $head (tree $tree); the receipt is reused"
        return $true
    }
    $script = Resolve-ShipScript 'verify' $VerifyScript (Join-Path $script:PackageToolsRoot 'verify.ps1')
    $paths = Get-ShipPaths $Info
    $log = Join-Path $paths.Dir "verify-$([guid]::NewGuid().ToString('N')).log"
    Write-Host "ship: verify.ps1 on head $head (log $log)"
    $code = Invoke-Logged 'pwsh' @('-NoProfile', '-File', $script, '-Root', $wt) $wt $log -Live
    $headAfter = Get-GitHead $wt
    $cleanAfter = Test-WorktreeClean $wt
    $treeAfter = Get-WorkingTreeHash $wt
    $receipt = Read-VerifyReceipt $receiptPath
    $decision = Test-VerifyReceiptObject $receipt $headAfter $treeAfter
    $problems = @()
    if ($code -ne 0) { $problems += "verify.ps1 exited with $code" }
    if (-not $decision.Ok) { $problems += "the receipt is not a pass for the head and tree: $($decision.Reason)" }
    if ($headAfter -ne $head -or $treeAfter -ne $tree -or -not $cleanAfter) { $problems += 'verify changed the head or the worktree' }
    $summary = @{ path = $receiptPath; sha256 = (Get-OptionalSha256 $receiptPath); status = $(if ($receipt) { [string]$receipt.status } else { $null }); complete = $(if ($receipt) { [bool]$receipt.complete } else { $false }); head = $(if ($receipt) { [string]$receipt.head } else { $null }); treeHash = $(if ($receipt) { [string]$receipt.treeHash } else { $null }) }
    if ($problems.Count)
    {
        return (Stop-Ship $Info 'ship-failed' 'verify' @{ status = 'failed'; head = $head; tree = $tree; exitCode = $code; receipt = $summary; log = $log } "ERROR verify failed: $($problems -join '; '). Receipt: $receiptPath. Log: $log")
    }
    Set-ShipStep $Info 'verify' @{ status = 'done'; head = $head; tree = $tree; exitCode = $code; receipt = $summary; log = $log }
    Write-Host "ship: verify passed (receipt $receiptPath, head $head, tree $tree)"
    $true
}

function Step-ShipRecord($Info)
{
    # The record that accept wrote has to cover the head that is shipped: the head itself, or an ancestor within the three first-parent
    # steps merge-pr.ps1 looks back, with only the plugin pin changed since (the binding proves that).
    $state = Read-State $Info.StateRoot $Id
    $wt = [string]$state.worktree
    $acceptance = $state.acceptance
    $head = Get-GitHead $wt
    $problems = @()
    $recordPath = [string]$acceptance.recordPath
    $expectedPath = Join-Path $Info.CommonDir "$($script:Namespace)/reviews/$($acceptance.commit).json"
    if ([IO.Path]::GetFullPath($recordPath) -ne [IO.Path]::GetFullPath($expectedPath)) { $problems += "the record path $recordPath is not $expectedPath" }
    if (-not (Test-Path -LiteralPath $recordPath -PathType Leaf)) { $problems += "the review record $recordPath is missing" }
    elseif ((Get-Sha256 $recordPath) -ne [string]$acceptance.recordSha256) { $problems += 'the review record changed since accept' }
    else
    {
        $recordDecision = Test-ReviewRecordObject (Get-Content -LiteralPath $recordPath -Raw | ConvertFrom-Json) ([string]$acceptance.commit) ([string]$acceptance.tree)
        if (-not $recordDecision.Ok) { $problems += "the review record does not validate: $($recordDecision.Reason)" }
    }
    $depth = 0
    $walk = $head
    while ($walk -ne [string]$acceptance.commit -and $depth -lt 3)
    {
        $walk = ([string](& git -C $wt rev-parse "$walk^1" 2>$null)).Trim()
        $depth++
        if (-not $walk) { break }
    }
    if ($walk -ne [string]$acceptance.commit) { $problems += "the accepted commit $($acceptance.commit) is not within three first-parent steps of the head $head, where merge-pr.ps1 looks for the record" }
    if ($problems.Count) { return (Stop-Ship $Info 'ship-failed' 'record' @{ status = 'failed'; head = $head } "ERROR the review record does not cover the head: $($problems -join '; ')") }
    Set-ShipStep $Info 'record' @{ status = 'done'; head = $head; recordPath = $recordPath; recordCommit = [string]$acceptance.commit; depth = $depth }
    $true
}

function Step-ShipQualify($Info)
{
    # hooks.qualify is the repository's qualification: -Action Impact decides from the diff whether a candidate check is required, -Action Run
    # takes the machine for it. A repository without the hook has no qualification: the record says so, and it never reads as passed.
    $state = Read-State $Info.StateRoot $Id
    $wt = [string]$state.worktree
    $head = Get-GitHead $wt
    $tree = Get-GitTree $wt
    $hook = Resolve-PackageHook 'qualify'
    if (-not $hook)
    {
        Set-ShipStep $Info 'qualify' @{ status = 'not-configured'; configured = $false; required = $false; head = $head; tree = $tree; reason = 'hooks.qualify is not configured: no qualification decision was made' }
        Write-Host 'ship: desktop qualification is not configured (hooks.qualify is null); no decision was made and nothing was run'
        return $true
    }
    $paths = Get-ShipPaths $Info
    $log = Join-Path $paths.Dir "impact-$([guid]::NewGuid().ToString('N')).log"
    $code = Invoke-Logged 'pwsh' @('-NoProfile', '-File', $hook, '-Action', 'Impact', '-Root', $wt, '-MainRoot', $Info.MainCheckout, '-Base', [string]$state.baseRef, '-Head', $head) $wt $log
    $output = @(Get-Content -LiteralPath $log)
    $verdictLine = @($output | Where-Object { $_ -match '^desktop-qualification:\s*(required|not-required)\s*$' })
    if ($code -ne 0 -or $verdictLine.Count -ne 1) { return (Stop-Ship $Info 'ship-failed' 'qualify' @{ status = 'failed'; head = $head; exitCode = $code; log = $log } "ERROR the qualify hook gave no decision (exit $code); see $log") }
    $required = $verdictLine[0] -match 'required\s*$' -and $verdictLine[0] -notmatch 'not-required'
    $matched = @($output | Where-Object { $_ -match '\[rule: ' })
    if (-not $required)
    {
        Set-ShipStep $Info 'qualify' @{ status = 'done'; required = $false; head = $head; tree = $tree; log = $log }
        Write-Host 'ship: desktop-qualification not-required'
        return $true
    }
    Write-Host "ship: desktop-qualification REQUIRED ($($matched.Count) path(s) matched)"
    $reuse = Find-ReusableQualification $state $head $tree
    if ($reuse.Record)
    {
        $q = $reuse.Record
        Set-ShipStep $Info 'qualify' @{ status = 'done'; required = $true; reused = $true; qualificationId = [string]$q.id; result = [string]$q.result; head = $head; tree = $tree; packages = @(Get-List $q.packages); skippedSteps = @(Get-List $q.skippedSteps); log = $log }
        Write-Host "ship: reusing qualification $($q.id) (same head, same tree, candidate artifacts unchanged)"
        return $true
    }
    $titleArgument = if ($Title) { ' -Title "' + $Title + '"' } else { '' }
    $waitArgument = if ($NoWait) { ' -NoWait' } else { '' }
    $command = "pwsh scripts/package.ps1 ship -Id $Id -AllowDesktop$titleArgument$waitArgument"
    $stopRecord = @{ status = 'stopped'; required = $true; head = $head; tree = $tree; matched = $matched; notReusable = @($reuse.Problems); log = $log }
    if (-not $AllowDesktop)
    {
        $reasons = if ($reuse.Problems.Count) { " Recorded qualifications cannot be reused: $($reuse.Problems -join ' | ')." } else { '' }
        return (Stop-Ship $Info 'desktop-required' 'qualify' $stopRecord "STOP desktop qualification is required for head $head and none is recorded for it.$reasons The desktop is taken over only with the switch. Run: $command" 3)
    }
    if (-not (Test-ShipInputDesktop))
    {
        return (Stop-Ship $Info 'desktop-required' 'qualify' $stopRecord "STOP the Windows session is locked or has no interactive desktop, so the desktop run was not started. Sign in, then run: $command" 3)
    }
    $qualificationId = [guid]::NewGuid().ToString('N')
    $workDirectory = Join-Path $paths.Dir "desktop-$qualificationId"
    $desktopLog = Join-Path $paths.Dir "desktop-$qualificationId.log"
    Write-Host "ANNOUNCE taking over the desktop for the candidate check (hooks.qualify -Action Run; about 10-15 minutes; do not use the keyboard or mouse until it ends). Head $head."
    $startedUtc = Get-Now
    $code = Invoke-Logged 'pwsh' @('-NoProfile', '-File', $hook, '-Action', 'Run', '-Root', $wt, '-MainRoot', $Info.MainCheckout, '-WorkDirectory', $workDirectory) $wt $desktopLog -Live
    $resultFile = Join-Path $workDirectory 'result.json'
    $result = if (Test-Path -LiteralPath $resultFile -PathType Leaf) { try { Get-Content -LiteralPath $resultFile -Raw | ConvertFrom-Json } catch { $null } } else { $null }
    $packages = @()
    if ($result -and $result.PSObject.Properties['packages']) { $packages = @($result.packages | ForEach-Object { @{ name = [string]$_.name; sha256 = [string]$_.sha256 } }) }
    $skipped = @()
    if ($result -and $result.PSObject.Properties['steps']) { $skipped = @($result.steps | Where-Object { $_.status -eq 'skipped' } | ForEach-Object { "$($_.name): $($_.detail)" }) }
    $resultStatus = if ($result -and $result.PSObject.Properties['status']) { [string]$result.status } else { 'missing' }
    $headAfter = Get-GitHead $wt
    $treeAfter = Get-GitTree $wt
    $problems = @()
    if ($code -ne 0) { $problems += "the qualify hook (Run) exited with $code" }
    if ($resultStatus -notin @('passed', 'passed_with_skips')) { $problems += "result.json status is '$resultStatus'" }
    if ($packages.Count -eq 0 -or @($packages | Where-Object { $_.sha256 -notmatch '^[0-9a-f]{64}$' }).Count) { $problems += 'result.json names no candidate artifact hash' }
    if ($headAfter -ne $head -or $treeAfter -ne $tree -or -not (Test-WorktreeClean $wt)) { $problems += 'the desktop run changed the head or the worktree' }
    $qualification = @{ id = $qualificationId; utc = (Get-Now); startedUtc = $startedUtc; head = $head; tree = $tree; result = $resultStatus; exitCode = $code; workDirectory = $workDirectory; log = $desktopLog; packages = $packages; skippedSteps = $skipped
        resultJson = @{ path = $resultFile; sha256 = (Get-OptionalSha256 $resultFile) } }
    Update-State $Info.StateRoot $Id 'ship-qualification' { param($s) $s.qualifications = @(Get-List $s.qualifications) + @($qualification) } | Out-Null
    if ($problems.Count)
    {
        return (Stop-Ship $Info 'ship-failed' 'qualify' @{ status = 'failed'; required = $true; head = $head; tree = $tree; qualificationId = $qualificationId; log = $desktopLog } "ERROR desktop qualification failed: $($problems -join '; '). Log: $desktopLog")
    }
    Set-ShipStep $Info 'qualify' @{ status = 'done'; required = $true; reused = $false; qualificationId = $qualificationId; result = $resultStatus; head = $head; tree = $tree; packages = $packages; skippedSteps = $skipped; log = $desktopLog }
    Write-Host "ship: desktop qualification $resultStatus (candidate: $(($packages | ForEach-Object { "$($_.name) $($_.sha256)" }) -join '; '))"
    $true
}
function Step-ShipPush($Info)
{
    $state = Read-State $Info.StateRoot $Id
    $wt = [string]$state.worktree
    $branch = [string]$state.branch
    $remote = ([string]$state.baseRef).Split('/')[0]
    $head = Get-GitHead $wt
    $done = Get-ShipStep $state 'push'
    if ($done -and $done.status -eq 'done')
    {
        if ([string]$done.head -eq $head) { return $true }
        $pushedReview = Get-ShipReview $state
        $binding = if ($pushedReview) { Get-ReviewBinding $state $pushedReview } else { @{ Current = $false; Reason = 'the acceptance is no longer valid' } }
        if (-not $binding.Current)
        {
            Move-ShipToReview $Info "the branch changed after it was pushed: $($binding.Reason)" | Out-Null
            return $false
        }
        Write-Host "ship: the branch was pushed at $($done.head); the head moved since by the plugin pin only, so the new head is pushed"
    }
    try { Assert-ShipRemote $wt $remote | Out-Null }
    catch { return (Stop-Ship $Info 'ship-failed' 'push' @{ status = 'failed'; head = $head } "ERROR $($_.Exception.Message)") }
    $listing = @(& git -C $wt ls-remote --heads $remote "refs/heads/$branch" 2>$null | ForEach-Object { "$_" })
    if ($LASTEXITCODE -ne 0) { return (Stop-Ship $Info 'ship-failed' 'push' @{ status = 'failed'; head = $head } "ERROR git ls-remote $remote failed") }
    $remoteTip = if ($listing.Count) { ($listing[0] -split '\s+')[0] } else { $null }
    if ($remoteTip -eq $head)
    {
        $knownHead = $head
        Update-State $Info.StateRoot $Id 'ship-push-adopted' { param($s) if (@(Get-List $s.pushes | Where-Object { [string]$_.sha -eq $knownHead }).Count -eq 0) { $s.pushes = @(Get-List $s.pushes) + @(@{ sha = $knownHead; utc = (Get-Now); remote = $remote; adopted = $true }) } } | Out-Null
        Set-ShipStep $Info 'push' @{ status = 'done'; head = $head; remote = $remote; alreadyOnRemote = $true }
        Write-Host "ship: $branch is already on $remote at $head; nothing is pushed again"
        return $true
    }
    $pushArgs = @('push', '-u', $remote, "refs/heads/${branch}:refs/heads/${branch}")
    if ($remoteTip)
    {
        $ours = @(Get-List $state.pushes | ForEach-Object { [string]$_.sha })
        if ($remoteTip -notin $ours) { return (Stop-Ship $Info 'ship-failed' 'push' @{ status = 'failed'; head = $head; remoteTip = $remoteTip } "ERROR $remote/$branch is at $remoteTip, which this runner never pushed; not overwriting it. Look at it by hand.") }
        $pushArgs += "--force-with-lease=refs/heads/${branch}:$remoteTip"
    }
    Add-ShipTrace "push $head"
    $pushOutput = @(& git -C $wt @pushArgs 2>&1 | ForEach-Object { "$_" })
    if ($LASTEXITCODE -ne 0) { return (Stop-Ship $Info 'ship-failed' 'push' @{ status = 'failed'; head = $head } "ERROR git push failed: $(($pushOutput | Select-Object -Last 3) -join ' ')") }
    Test-ShipCrash 'push-before-record'
    $pushedHead = $head
    Update-State $Info.StateRoot $Id 'ship-push' { param($s) $s.pushes = @(Get-List $s.pushes) + @(@{ sha = $pushedHead; utc = (Get-Now); remote = $remote }) } | Out-Null
    Set-ShipStep $Info 'push' @{ status = 'done'; head = $head; remote = $remote; alreadyOnRemote = $false }
    Write-Host "ship: pushed $branch to $remote at $head"
    $true
}

function Write-ShipPrBody($Info, $State, [string]$Head)
{
    $review = Get-ShipReview $State
    $body = New-ShipPrBody $State $review $Head (Get-GitTree ([string]$State.worktree)) (Get-ShipStep $State 'verify') (Get-ShipStep $State 'qualify')
    $bodyPath = Join-Path (Get-ShipPaths $Info).Dir 'pr-body.md'
    [IO.File]::WriteAllText($bodyPath, $body, [Text.UTF8Encoding]::new($false))
    $bodyPath
}

function Sync-ShipPrBody($Info, $State, [string]$Number, [string]$Url, [string]$Head, [bool]$Created)
{
    # The description is generated from the state of the head that is shipped, so a pull request that exists already (reused, or created
    # for a head the plugin pin has moved since) is edited to say what this head's receipt and qualification say.
    $bodyPath = Write-ShipPrBody $Info $State $Head
    $edited = Invoke-Gh @('pr', 'edit', $Number, '--body-file', $bodyPath) $Info.MainCheckout
    if ($edited.Exit -ne 0) { return (Stop-Ship $Info 'ship-failed' 'pr' @{ status = 'failed'; number = $Number; url = $Url; head = $Head } "ERROR gh pr edit failed (exit $($edited.Exit)): $(($edited.Lines | Select-Object -Last 3) -join ' '). The description of #$Number still names an earlier head.") }
    Set-ShipStep $Info 'pr' @{ status = 'done'; number = $Number; url = $Url; head = $Head; created = $Created; body = $bodyPath; bodySha256 = (Get-Sha256 $bodyPath); bodyHead = $Head }
    $true
}

function Step-ShipPr($Info)
{
    $state = Read-State $Info.StateRoot $Id
    $branch = [string]$state.branch
    $done = Get-ShipStep $state 'pr'
    $head = Get-GitHead ([string]$state.worktree)
    if ($done -and $done.status -eq 'done')
    {
        if ([string]$done.head -eq $head -and [string](Get-Field $done 'bodyHead') -eq $head) { return $true }
        Write-Host "ship: the pull request #$($done.number) was opened for head $($done.head); its description is regenerated for $head"
        return (Sync-ShipPrBody $Info $state ([string]$done.number) ([string]$done.url) $head ([bool](Get-Field $done 'created')))
    }
    try { $existing = Find-ShipPr $Info $branch }
    catch { return (Stop-Ship $Info 'ship-failed' 'pr' @{ status = 'failed'; head = $head } "ERROR $($_.Exception.Message)") }
    if ($existing)
    {
        Write-Host "ship: pull request #$($existing.Number) already exists for $branch ($($existing.Url)); no second one is created, its description is regenerated for $head"
        return (Sync-ShipPrBody $Info $state $existing.Number $existing.Url $head $false)
    }
    $prTitle = Get-ShipTitle ([string]$state.brief.path) $Title
    $bodyPath = Write-ShipPrBody $Info $state $head
    $baseBranch = ([string]$state.baseRef).Substring(([string]$state.baseRef).IndexOf('/') + 1)
    $created = Invoke-Gh @('pr', 'create', '--base', $baseBranch, '--head', $branch, '--title', $prTitle, '--body-file', $bodyPath) $Info.MainCheckout
    if ($created.Exit -ne 0) { return (Stop-Ship $Info 'ship-failed' 'pr' @{ status = 'failed'; head = $head } "ERROR gh pr create failed (exit $($created.Exit)): $(($created.Lines | Select-Object -Last 3) -join ' ')") }
    Test-ShipCrash 'pr-before-record'
    $number = $null
    $url = $null
    foreach ($line in $created.Lines) { if ($line -match '(https?://\S+/pull/(\d+))') { $url = $Matches[1]; $number = $Matches[2] } }
    if (-not $number)
    {
        try { $again = Find-ShipPr $Info $branch } catch { $again = $null }
        if ($again) { $number = $again.Number; $url = $again.Url }
    }
    if (-not $number) { return (Stop-Ship $Info 'ship-failed' 'pr' @{ status = 'failed'; head = $head } 'ERROR gh pr create reported no pull request number') }
    Set-ShipStep $Info 'pr' @{ status = 'done'; number = $number; url = $url; head = $head; created = $true; title = $prTitle; body = $bodyPath; bodySha256 = (Get-Sha256 $bodyPath); bodyHead = $head }
    Write-Host "ship: opened pull request #$number ($url)"
    $true
}

function Step-ShipMerge($Info)
{
    # Returns $true only when GitHub reports MERGED. merge-pr.ps1 removes the worktree after a merge, so nothing later may need it.
    $state = Read-State $Info.StateRoot $Id
    $wt = [string]$state.worktree
    $prStep = Get-ShipStep $state 'pr'
    $number = [string]$prStep.number
    $mergeStep = Get-ShipStep $state 'merge'
    $calls = if ($mergeStep) { [int](Get-Field $mergeStep 'calls') } else { 0 }
    # A failure record keeps what the earlier record knew about the auto-merge (the head it was enabled for, that merge-pr.ps1 confirmed it,
    # the call count): a failed read must never make a later run enable the same auto-merge again.
    $keep = @{}
    foreach ($key in @('enabledHead', 'confirmed', 'calls')) { $kept = Get-Field $mergeStep $key; if ($null -ne $kept) { $keep[$key] = $kept } }
    $failRecord = { param([hashtable]$Extra) $merged = $keep.Clone(); foreach ($key in $Extra.Keys) { $merged[$key] = $Extra[$key] }; $merged }
    $view = Get-ShipPrState $Info $number
    if (-not $view) { return (Stop-Ship $Info 'ship-failed' 'merge' (& $failRecord @{ status = 'failed'; number = $number }) "ERROR gh pr view $number failed; the state of the pull request is unknown") }
    if ($view.State -eq 'MERGED') { return (Complete-ShipMerged $Info $wt $number $view $calls $true) }
    if ($view.State -eq 'CLOSED') { return (Stop-Ship $Info 'ship-failed' 'merge' (& $failRecord @{ status = 'failed'; number = $number }) "ERROR pull request #$number is CLOSED without being merged") }

    # Everything the merge is about to rest on has to be bound to the head that is on the pull request and in the worktree.
    $localHead = Get-GitHead $wt
    $evidence = @(Get-ShipEvidenceProblems $state $localHead $wt)
    if ($view.Head -ne $localHead) { $evidence += "the pull request head $($view.Head) is not the shipped head $localHead" }
    if ($evidence.Count) { return (Stop-Ship $Info 'ship-failed' 'merge' (& $failRecord @{ status = 'failed'; number = $number; head = $localHead }) "ERROR the evidence does not cover the head to be merged: $($evidence -join '; ')") }

    # An auto-merge that merge-pr.ps1 already enabled for this very head is waited for, not enabled again.
    $enabledHead = [string](Get-Field $mergeStep 'enabledHead')
    if ($enabledHead -eq $localHead -and ($view.AutoMerge -or [bool](Get-Field $mergeStep 'confirmed')))
    {
        Write-Host "ship: auto-merge is already enabled for #$number at $localHead; merge-pr.ps1 is not called again"
        Update-State $Info.StateRoot $Id 'ship-auto-merge-pending' { param($s) $s.stage = 'auto-merge-enabled' } | Out-Null
        if ($NoWait)
        {
            Write-Host "ship: #$number is $($view.State), not merged yet. Run ship again without -NoWait to wait for the merge and report the channels."
            return $false
        }
        $deadline = (Get-Date).AddMinutes($MergeTimeoutMinutes)
        while ($view -and $view.State -eq 'OPEN' -and (Get-Date) -lt $deadline)
        {
            Write-Host "ship: waiting for GitHub to merge #$number"
            Start-Sleep -Seconds $ReleasePollSeconds
            $view = Get-ShipPrState $Info $number
        }
        if ($view -and $view.State -eq 'MERGED') { return (Complete-ShipMerged $Info $wt $number $view $calls $true) }
        if ($view -and $view.State -eq 'CLOSED') { return (Stop-Ship $Info 'ship-failed' 'merge' (& $failRecord @{ status = 'failed'; number = $number }) "ERROR pull request #$number was CLOSED without being merged") }
        $script:ExitCode = 1
        Write-Host "ERROR #$number is not merged after $MergeTimeoutMinutes minute(s) (GitHub reports $(if ($view) { $view.State } else { 'unknown' })); the package stays at auto-merge-enabled. Run ship again to keep waiting."
        return $false
    }

    $mergePr = Resolve-ShipScript 'merge-pr' $MergePrScript (Join-Path $script:PackageToolsRoot 'merge-pr.ps1')
    $log = Join-Path (Get-ShipPaths $Info).Dir "merge-$([guid]::NewGuid().ToString('N')).log"
    # -NoPluginPin: merge-pr.ps1 must not commit and push a pin on its own, or the merged head would no longer be the verified one.
    $mergeArgs = @('-NoProfile', '-File', $mergePr, '-Pr', $number, '-Worktree', $wt, '-Auto', '-NoPluginPin')
    if (-not $NoWait) { $mergeArgs += @('-WaitMerged', '-TimeoutMinutes', [string]$MergeTimeoutMinutes) }
    $mergeArgs += @('-Root', [string]$Info.MainCheckout)
    Set-ShipStep $Info 'merge' @{ status = 'running'; number = $number; calls = ($calls + 1); noWait = [bool]$NoWait; enabledHead = $localHead }
    Write-Host "ship: merge-pr.ps1 -Pr $number -Auto -NoPluginPin$(if (-not $NoWait) { ' -WaitMerged' })"
    $code = Invoke-Logged 'pwsh' $mergeArgs $Info.MainCheckout $log -Live
    Test-ShipCrash 'merge-before-record'
    if ($code -ne 0) { return (Stop-Ship $Info 'ship-failed' 'merge' @{ status = 'failed'; number = $number; calls = ($calls + 1); exitCode = $code; enabledHead = $localHead; log = $log } "ERROR merge-pr.ps1 exited with $code (receipt, review or check refusal, conflict or timeout); see $log") }
    $view = Get-ShipPrState $Info $number
    $observed = if ($view) { $view.State } else { 'unknown (gh pr view failed)' }
    if ($NoWait)
    {
        # -NoWait always stops here, also when GitHub merged at once; the next ship records the merge.
        Set-ShipStep $Info 'merge' @{ status = 'auto-merge-enabled'; confirmed = $true; number = $number; calls = ($calls + 1); exitCode = $code; observedState = $observed; enabledHead = $localHead; log = $log } 'auto-merge-enabled'
        if ($observed -eq 'MERGED') { Write-Host "ship: auto-merge is enabled for #$number and GitHub already reports it MERGED. Run ship again to record the merge and report the channels." }
        else { Write-Host "ship: auto-merge is enabled for #$number; it is NOT merged yet (GitHub reports $observed). Run ship again to wait for the merge and report the channels." }
        return $false
    }
    if ($view -and $view.State -eq 'MERGED') { return (Complete-ShipMerged $Info $wt $number $view ($calls + 1) $false) }
    Set-ShipStep $Info 'merge' @{ status = 'auto-merge-enabled'; confirmed = $true; number = $number; calls = ($calls + 1); exitCode = $code; observedState = $observed; enabledHead = $localHead; log = $log } 'auto-merge-enabled'
    Write-Host "ERROR merge-pr.ps1 returned but GitHub reports $observed, not MERGED; the package is not merged. Run ship again to keep waiting."
    $script:ExitCode = 1
    $false
}

function Complete-ShipMerged($Info, [string]$Wt, [string]$Number, $View, [int]$Calls, [bool]$Cleanup)
{
    # GitHub reports MERGED. merge-pr.ps1 -WaitMerged has already removed the worktree; in every other case it is removed here.
    $cleanupExit = $null
    if ($Cleanup -and (Test-Path -LiteralPath $Wt -PathType Container))
    {
        $mergePr = Resolve-ShipScript 'merge-pr' $MergePrScript (Join-Path $script:PackageToolsRoot 'merge-pr.ps1')
        $log = Join-Path (Get-ShipPaths $Info).Dir "cleanup-$([guid]::NewGuid().ToString('N')).log"
        $cleanupExit = Invoke-Logged 'pwsh' @('-NoProfile', '-File', $mergePr, '-Pr', $Number, '-Worktree', $Wt, '-Root', [string]$Info.MainCheckout) $Info.MainCheckout $log -Live
    }
    Set-ShipStep $Info 'merge' @{ status = 'done'; number = $Number; mergeCommit = $View.MergeCommit; headRefOid = $View.Head; calls = $Calls; cleanupExit = $cleanupExit } 'merged'
    Write-Host "ship: GitHub reports pull request #$Number MERGED ($($View.MergeCommit))"
    $true
}

function Get-ShipEvidenceProblems($State, [string]$Head, [string]$Wt)
{
    # The verify receipt and the desktop-qualification decision of this ship must be about exactly this head and tree.
    $problems = @()
    if (-not (Test-WorktreeClean $Wt)) { $problems += 'the worktree is not clean' }
    $contentTree = Get-WorkingTreeHash $Wt
    $gitTree = Get-GitTree $Wt
    $verify = Get-ShipStep $State 'verify'
    if (-not $verify -or $verify.status -ne 'done') { $problems += 'verify.ps1 has no passing record' }
    elseif ([string]$verify.head -ne $Head -or [string]$verify.tree -ne $contentTree) { $problems += "the verify.ps1 receipt is bound to head $($verify.head) tree $($verify.tree), not to head $Head tree $contentTree" }
    $qualify = Get-ShipStep $State 'qualify'
    if (-not $qualify -or $qualify.status -notin @('done', 'not-configured')) { $problems += 'the desktop-qualification decision has no passing record' }
    elseif ([string]$qualify.head -ne $Head -or [string]$qualify.tree -ne $gitTree) { $problems += "the desktop-qualification decision is bound to head $($qualify.head) tree $($qualify.tree), not to head $Head tree $gitTree" }
    @($problems)
}

function Step-ShipChannels($Info)
{
    # After the merge: find the release the CD cut for the merge commit (a bounded wait), run hooks.channels for it, and report. A failed
    # channel is reported (the hook prints its own NOTE lines); no pull request is ever opened from here. A repository without the hook
    # has no channel report: the record says so and nothing is waited for.
    $state = Read-State $Info.StateRoot $Id
    $hook = Resolve-PackageHook 'channels'
    if (-not $hook)
    {
        Set-ShipStep $Info 'channels' @{ status = 'not-configured'; configured = $false; reason = 'hooks.channels is not configured: no release channel report is made' }
        Write-Host 'ship: release channels are not configured (hooks.channels is null); nothing was reported'
        return $true
    }
    $mergeStep = Get-ShipStep $state 'merge'
    $mergeCommit = [string](Get-Field $mergeStep 'mergeCommit')
    if (-not $mergeCommit)
    {
        $view = Get-ShipPrState $Info ([string](Get-ShipStep $state 'pr').number)
        if ($view) { $mergeCommit = $view.MergeCommit }
    }
    if (-not $mergeCommit) { Set-ShipStep $Info 'channels' @{ status = 'pending'; reason = 'the merge commit is unknown' }; Write-Host 'NOTE the merge commit is unknown, so the release cannot be found yet. Run ship again.'; return $false }
    $waitMinutes = if ($NoWait) { 0 } else { $ChannelMinutes }
    $deadline = (Get-Date).AddMinutes($waitMinutes)
    $version = $null
    while ($true)
    {
        $list = Invoke-Gh @('release', 'list', '--limit', '10', '--json', 'tagName,isDraft') $Info.MainCheckout -StdoutOnly
        if ($list.Exit -eq 0)
        {
            foreach ($release in @($list.Text | ConvertFrom-Json))
            {
                $tag = [string]$release.tagName
                $tagVersion = if ($tag -match '^v(\d+\.\d+\.\d+)$') { $Matches[1] } else { $null }
                if ($release.isDraft -or -not $tagVersion) { continue }
                $detail = Invoke-Gh @('release', 'view', $tag, '--json', 'targetCommitish') $Info.MainCheckout -StdoutOnly
                if ($detail.Exit -eq 0 -and [string](($detail.Text | ConvertFrom-Json).targetCommitish) -eq $mergeCommit) { $version = $tagVersion; break }
            }
        }
        if ($version -or (Get-Date) -ge $deadline) { break }
        Write-Host "ship: waiting for CD to cut the release for $mergeCommit"
        Start-Sleep -Seconds $ReleasePollSeconds
    }
    if (-not $version)
    {
        Set-ShipStep $Info 'channels' @{ status = 'pending'; mergeCommit = $mergeCommit; reason = "no release for the merge commit appeared within $ChannelMinutes minute(s)" }
        Write-Host "NOTE CHANNELS: not reported. No release for $mergeCommit appeared within $waitMinutes minute(s). Run ship again later."
        return $false
    }
    $outDirectory = Join-Path (Get-ShipPaths $Info).Dir "channels-$version"
    $log = Join-Path (Get-ShipPaths $Info).Dir "channels-$version.log"
    $channelArgs = @('-NoProfile', '-File', $hook, '-Root', $Info.MainCheckout, '-Version', $version, '-MergeCommit', $mergeCommit, '-OutDirectory', $outDirectory)
    if ($waitMinutes -gt 0) { $channelArgs += @('-Watch', '-TimeoutMinutes', [string][Math]::Max(1, [int][Math]::Ceiling(($deadline - (Get-Date)).TotalMinutes)), '-PollSeconds', '60') }
    $code = Invoke-Logged 'pwsh' $channelArgs $Info.MainCheckout $log -Live
    $channels = @()
    foreach ($line in @(Get-Content -LiteralPath $log)) { if ($line -match '^CHANNEL (\S+): (\w+) - (.*)$') { $channels += @(@{ name = $Matches[1]; state = $Matches[2]; detail = $Matches[3] }) } }
    $final = if ($code -eq 0) { 'ok' } elseif ($code -eq 1) { 'failed' } elseif ($code -eq 3) { 'pending' } else { "unknown (exit $code)" }
    $failed = @($channels | Where-Object { $_.state -eq 'failed' })
    Set-ShipStep $Info 'channels' @{ status = $(if ($final -eq 'ok') { 'done' } else { 'reported' }); version = $version; mergeCommit = $mergeCommit; final = $final; exitCode = $code; channels = $channels; log = $log }
    Write-Host "ship: release v$version channels: $final"
    foreach ($channel in $channels) { Write-Host ("  {0}: {1} - {2}" -f $channel.name, $channel.state, $channel.detail) }
    foreach ($channel in $failed) { Write-Host "ERROR channel $($channel.name) failed: $($channel.detail)" }
    if ($failed.Count -or $code -eq 1) { $script:ExitCode = 1 }
    $final -eq 'ok'
}
function Set-ShipLocalMerged($Info, [string]$Head, [hashtable]$Extra)
{
    $record = @{ status = 'done'; local = $true; mergeCommit = $Head; headRefOid = $Head; baseRef = [string]$script:Ctx.BaseRef }
    foreach ($key in $Extra.Keys) { $record[$key] = $Extra[$key] }
    Set-ShipStep $Info 'merge' $record 'merged'
    Set-ShipStep $Info 'channels' @{ status = 'not-configured'; configured = $false; reason = 'no remote is configured: there is no release to find and no channel to report' }
    Write-Host "ship: $($script:Ctx.BaseRef) now contains $Head (fast-forward, no remote)"
}

function Step-ShipLocalMerge($Info)
{
    # The merge of a repository without a remote. The evidence is bound to the head exactly as for a pull request, then merge-pr.ps1 -LocalMerge
    # repeats the receipt and review checks itself and fast-forwards the base branch in the main checkout. It removes the worktree.
    $state = Read-State $Info.StateRoot $Id
    $wt = [string]$state.worktree
    $localHead = Get-GitHead $wt
    $mergeStep = Get-ShipStep $state 'merge'
    $calls = if ($mergeStep) { [int](Get-Field $mergeStep 'calls') } else { 0 }
    $evidence = @(Get-ShipEvidenceProblems $state $localHead $wt)
    if ($evidence.Count) { return (Stop-Ship $Info 'ship-failed' 'merge' @{ status = 'failed'; head = $localHead; calls = $calls } "ERROR the evidence does not cover the head to be merged: $($evidence -join '; ')") }
    $mergePr = Resolve-ShipScript 'merge-pr' $MergePrScript (Join-Path $script:PackageToolsRoot 'merge-pr.ps1')
    $log = Join-Path (Get-ShipPaths $Info).Dir "merge-$([guid]::NewGuid().ToString('N')).log"
    $mergeArgs = @('-NoProfile', '-File', $mergePr, '-LocalMerge', '-FromShip', '-ShipId', $Id, '-Worktree', $wt, '-Root', [string]$Info.MainCheckout)
    Set-ShipStep $Info 'merge' @{ status = 'running'; local = $true; head = $localHead; calls = ($calls + 1) }
    Write-Host "ship: merge-pr.ps1 -LocalMerge (fast-forward $($script:Ctx.BaseRef) to $localHead)"
    $code = Invoke-Logged 'pwsh' $mergeArgs $Info.MainCheckout $log -Live
    Test-ShipCrash 'merge-before-record'
    if ($code -ne 0) { return (Stop-Ship $Info 'ship-failed' 'merge' @{ status = 'failed'; local = $true; head = $localHead; calls = ($calls + 1); exitCode = $code; log = $log } "ERROR merge-pr.ps1 -LocalMerge exited with $code (receipt, review or fast-forward refusal, or a blocked main checkout); see $log") }
    # Trust the repository, not the exit code: the base branch has to contain the head now.
    & git -C $Info.MainCheckout merge-base --is-ancestor $localHead ([string]$script:Ctx.BaseRef) 2>$null
    if ($LASTEXITCODE -ne 0) { return (Stop-Ship $Info 'ship-failed' 'merge' @{ status = 'failed'; local = $true; head = $localHead; calls = ($calls + 1); exitCode = $code; log = $log } "ERROR merge-pr.ps1 -LocalMerge exited 0 but $($script:Ctx.BaseRef) does not contain $localHead") }
    Set-ShipLocalMerged $Info $localHead @{ calls = ($calls + 1); exitCode = $code; log = $log }
    $true
}

function Complete-LocalMergeAfterWorktreeLoss($Info, $State)
{
    # merge-pr.ps1 -LocalMerge removed the worktree and the process died before the merge was recorded: the base branch decides.
    $mergeStep = Get-ShipStep $State 'merge'
    $head = [string](Get-Field $mergeStep 'head')
    if (-not $head) { throw "the worktree $($State.worktree) is missing and no local merge was started for it; nothing can be shipped from here" }
    & git -C $Info.MainCheckout merge-base --is-ancestor $head ([string]$script:Ctx.BaseRef) 2>$null
    if ($LASTEXITCODE -ne 0) { throw "the worktree $($State.worktree) is gone but $($script:Ctx.BaseRef) does not contain the head $head of the local merge; nothing can be shipped from here" }
    Set-ShipLocalMerged $Info $head @{ recoveredAfterWorktreeLoss = $true }
}

function Complete-ShipAfterWorktreeLoss($Info, $State)
{
    # merge-pr.ps1 removed the worktree and the process died before the merge was recorded: GitHub decides.
    $prStep = Get-ShipStep $State 'pr'
    $view = Get-ShipPrState $Info ([string]$prStep.number)
    if (-not $view -or $view.State -ne 'MERGED') { throw "the worktree $($State.worktree) is gone but pull request #$($prStep.number) is $(if ($view) { $view.State } else { 'unreadable' }), not MERGED; nothing can be shipped from here" }
    Set-ShipStep $Info 'merge' @{ status = 'done'; number = [string]$prStep.number; mergeCommit = $view.MergeCommit; headRefOid = $view.Head; recoveredAfterWorktreeLoss = $true } 'merged'
    Write-Host "ship: pull request #$($prStep.number) is MERGED ($($view.MergeCommit)); the worktree was already removed"
    if ($NoWait)
    {
        Write-Host "ship: the merge is recorded. With -NoWait the release channels are not waited for. Report them with: pwsh scripts/package.ps1 ship -Id $Id"
        return
    }
    Step-ShipChannels $Info | Out-Null
}

function Invoke-ShipLocked($Info)
{
    $state = Read-State $Info.StateRoot $Id
    $stage = [string]$state.stage
    # A candidate kit (install.ps1 -Candidate) must never reach the base branch: refuse before anything is rebased, verified, pushed or merged.
    if ([string]$state.worktree -and (Test-Path -LiteralPath ([string]$state.worktree) -PathType Container))
    {
        $candidate = Get-AwCandidateLockRefusal ([string]$state.worktree) -Revision 'HEAD'
        if ($candidate) { throw "ship refused: $candidate" }
    }
    if ($stage -in @('review-passed', 'accepted'))
    {
        $guard = Enter-StateLock $Info.StateRoot $Id
        try { Sync-Invalidation $Info $Id } finally { $guard.Dispose() }
        $state = Read-State $Info.StateRoot $Id
        $stage = [string]$state.stage
    }
    if ($stage -eq 'invalidated' -and $state.acceptance -and (Test-Path -LiteralPath ([string]$state.worktree) -PathType Container))
    {
        # The accepted head is gone (a later commit, or an invalidation recorded by an earlier command): back to review, never shipped.
        $earlier = Get-Field $state.acceptance.invalidated 'reason'
        Move-ShipToReview $Info $(if ($earlier) { [string]$earlier } else { 'the branch no longer matches the reviewed head' }) | Out-Null
        return
    }
    if ($stage -notin $script:ShipStageList)
    {
        $hint = switch ($stage)
        {
            'invalidated' { 'the PASS and the acceptance no longer apply to the branch: run check, commit, review and accept again' }
            'needs-review' { 'the reviewed head changed: run review -Level light|heavy, then accept' }
            'review-passed' { 'run accept -Reason <text> first' }
            default { 'ship needs a valid acceptance (stage accepted)' }
        }
        throw "ship is not allowed in stage '$stage': $hint"
    }
    if ($stage -eq 'merged')
    {
        if (-not $script:Ctx.HasRemote) { Write-Host "ship: $Id is already merged into the base branch; there is no remote and no release channel to report"; return }
        Step-ShipChannels $Info | Out-Null
        return
    }
    $wt = [string]$state.worktree
    if (-not (Test-Path -LiteralPath $wt -PathType Container))
    {
        if (-not $script:Ctx.HasRemote) { Complete-LocalMergeAfterWorktreeLoss $Info $state; return }
        $prStep = Get-ShipStep $state 'pr'
        if ($prStep -and $prStep.status -eq 'done') { Complete-ShipAfterWorktreeLoss $Info $state; return }
        throw "the worktree $wt is missing"
    }
    Assert-StateConsistent $state $Info
    $problems = @(Test-SnapshotsIntact $state)
    if ($problems.Count) { throw ($problems -join '; ') }
    if ([bool]$state.guard.mainCheckoutMutated) { throw 'the main-checkout guard is not clean; nothing is shipped' }
    $null = Get-ShipTitle ([string]$state.brief.path) $Title
    $review = Get-ShipReview $state
    if (-not $review) { throw 'there is no valid PASS review with a recorded, valid acceptance for this package' }
    $binding = Get-ReviewBinding $state $review
    if (-not $binding.Current)
    {
        Move-ShipToReview $Info "the acceptance does not cover the branch as it is now: $($binding.Reason)" | Out-Null
        return
    }
    $fresh = ($stage -eq 'accepted')
    Update-State $Info.StateRoot $Id 'ship-started' {
        param($s)
        if ($fresh -or -not $s.ship) { $s.ship = @{ startedUtc = (Get-Now); attempts = 0; steps = @{} } }
        $s.ship.attempts = [int]$s.ship.attempts + 1
        $s.ship.runner = @{ pid = $PID; startUtc = (Get-ProcessStartIdentity $PID) }
        $s.ship.noWait = [bool]$NoWait
        $s.ship.allowDesktop = [bool]$AllowDesktop
        $s.stage = 'shipping'
    } | Out-Null
    Write-Host "ship ${Id}: head $(Get-GitHead $wt), $(if ($fresh) { 'new ship' } else { "resuming from stage $stage" })"
    # Once the head is pushed the earlier steps are not repeated; a head that moved since (the pin) runs them all again for the new head.
    $pushStep = Get-ShipStep (Read-State $Info.StateRoot $Id) 'push'
    if (-not ($pushStep -and $pushStep.status -eq 'done' -and [string]$pushStep.head -eq (Get-GitHead $wt)))
    {
        if (-not (Test-ShipStepOk (Step-ShipBase $Info))) { return }
        if (-not (Test-ShipStepOk (Step-ShipPin $Info))) { return }
        if (-not (Test-ShipStepOk (Step-ShipVerify $Info))) { return }
        if (-not (Test-ShipStepOk (Step-ShipRecord $Info))) { return }
        if (-not (Test-ShipStepOk (Step-ShipQualify $Info))) { return }
    }
    if (-not $script:Ctx.HasRemote)
    {
        # No remote: nothing is pushed and no pull request exists. The evidence is complete here; the merge is the local fast-forward, and only on request.
        if (-not $LocalMerge)
        {
            $head = Get-GitHead $wt
            Stop-Ship $Info 'no-remote' 'push' @{ status = 'no-remote'; head = $head } "STOP no remote is configured (repository.remote is null), so nothing is pushed and no pull request is opened. The evidence for head $head is complete. To fast-forward $($script:Ctx.BaseRef) to it, run: pwsh scripts/package.ps1 ship -Id $Id -LocalMerge" 3 | Out-Null
            return
        }
        Step-ShipLocalMerge $Info | Out-Null
        return
    }
    if (-not (Test-ShipStepOk (Step-ShipPush $Info))) { return }
    if (-not (Test-ShipStepOk (Step-ShipPr $Info))) { return }
    if (-not (Test-ShipStepOk (Step-ShipMerge $Info))) { return }
    if ($NoWait)
    {
        # GitHub already reported MERGED, which is recorded; -NoWait still means no waiting on the release.
        Write-Host "ship: the merge is recorded. With -NoWait the release channels are not waited for. Report them with: pwsh scripts/package.ps1 ship -Id $Id"
        return
    }
    Step-ShipChannels $Info | Out-Null
}

function Invoke-Ship
{
    $info = Get-RepoInfo $Repo
    if ($env:AW_PKG_SELFTEST -and -not (Test-SelfTestStub $GhScript)) { throw 'self-test guard: ship needs a marked -GhScript stub' }
    # The process must not stand inside the worktree that merge-pr.ps1 removes after the merge.
    Set-Location -LiteralPath $info.MainCheckout
    $shipLock = Enter-ShipLock $info $Id
    try { Invoke-ShipLocked $info }
    finally { Exit-ShipLock $shipLock $info }
}

