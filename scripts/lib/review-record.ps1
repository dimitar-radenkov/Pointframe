function Test-ReviewRecordObject($Record, [string]$ExpectedHead, [string]$ExpectedTree, [bool]$EvidenceExists = $false, [bool]$SmallFixAllowed = $true)
{
    $refuse = { param([string]$why) [pscustomobject]@{ Ok = $false; Reason = $why } }
    if (-not $Record) { return & $refuse 'no record' }
    $schemaType = if ($null -ne $Record.schema) { [Type]::GetTypeCode($Record.schema.GetType()) } else { [TypeCode]::Empty }
    if ($schemaType -notin @([TypeCode]::Byte, [TypeCode]::SByte, [TypeCode]::Int16, [TypeCode]::UInt16, [TypeCode]::Int32, [TypeCode]::UInt32, [TypeCode]::Int64, [TypeCode]::UInt64) -or $Record.schema -ne 1) { return & $refuse 'wrong schema (must be integer 1)' }
    if ($Record.headSha -ne $ExpectedHead) { return & $refuse 'stale head' }
    if ($Record.treeSha -ne $ExpectedTree) { return & $refuse 'stale tree' }
    if ($Record.verdict -ne 'PASS') { return & $refuse 'verdict is not PASS' }
    if ([int]$Record.blockingFindings -gt 0) { return & $refuse 'open blocking findings' }
    $small = $Record.PSObject.Properties['reason'] -and $Record.reason -eq 'small-fix'
    $executor = ([string]$Record.executor).Trim().ToLowerInvariant()
    $reviewer = ([string]$Record.reviewer).Trim().ToLowerInvariant()
    $executorModel = if ($executor.Contains(':')) { $executor.Substring($executor.IndexOf(':') + 1) } else { $executor }
    $reviewerModel = if ($reviewer.Contains(':')) { $reviewer.Substring($reviewer.IndexOf(':') + 1) } else { $reviewer }
    if (-not $small -and ($executor -eq $reviewer -or $executorModel -eq $reviewerModel)) { return & $refuse 'executor and reviewer are the same' }
    if ($small)
    {
        if (-not $SmallFixAllowed) { return & $refuse 'small-fix exception is invalid' }
        if ($Record.executor -ne 'claude:coordinator' -or $Record.reviewer -ne 'self') { return & $refuse 'invalid small-fix identities' }
        if (@($Record.changedFiles).Count -gt 2 -or [int]$Record.changedLines -gt 12) { return & $refuse 'small-fix exceeds 2 files or 12 changed lines' }
    }
    if (-not $Record.evidence -or (-not $EvidenceExists -and -not (Test-Path -LiteralPath ([string]$Record.evidence) -PathType Leaf))) { return & $refuse 'evidence path is missing' }
    [pscustomobject]@{ Ok = $true; Reason = 'review record passed' }
}
function Get-ReviewChangeCount([string]$Repository, [string]$Base, [string]$Head)
{
    $excluded = @('plugin/pointframe/server.lock.json', 'plugin/pointframe/.claude-plugin/plugin.json')
    $files = @(& git -C $Repository diff --name-only $Base $Head 2>$null | Where-Object { $_ -and $_ -notin $excluded })
    $lines = 0
    foreach ($stat in @(& git -C $Repository diff --numstat $Base $Head 2>$null))
    {
        $parts = $stat -split "`t"
        if ($parts.Count -ge 3 -and $parts[2] -notin $excluded)
        {
            if ($parts[0] -match '^\d+$') { $lines += [int]$parts[0] }
            if ($parts[1] -match '^\d+$') { $lines += [int]$parts[1] }
        }
    }
    [pscustomobject]@{ Files = $files; Lines = $lines }
}
