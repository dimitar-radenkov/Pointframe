function Get-WorkingTreeHash
{
    param([Parameter(Mandatory = $true)][string]$RepositoryRoot)

    $gitDir = (& git -C $RepositoryRoot rev-parse --absolute-git-dir).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Could not locate the Git directory.' }
    $index = Join-Path $gitDir 'index'
    $tempIndex = Join-Path ([IO.Path]::GetTempPath()) "pointframe-tree-$([guid]::NewGuid().ToString('N')).index"
    if (Test-Path -LiteralPath $index) { Copy-Item -LiteralPath $index -Destination $tempIndex }
    $previousIndex = $env:GIT_INDEX_FILE
    $env:GIT_INDEX_FILE = $tempIndex
    try
    {
        & git -C $RepositoryRoot add -A 2>$null | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Could not stage the working tree in the temporary index.' }
        $hash = (& git -C $RepositoryRoot write-tree).Trim()
        if ($LASTEXITCODE -ne 0) { throw 'Could not calculate the working-tree hash.' }
        return $hash
    }
    finally
    {
        if ($null -eq $previousIndex) { Remove-Item Env:GIT_INDEX_FILE -ErrorAction SilentlyContinue }
        else { $env:GIT_INDEX_FILE = $previousIndex }
        Remove-Item -LiteralPath $tempIndex -Force -ErrorAction SilentlyContinue
    }
}

function Test-VerifyReceiptObject
{
    # Pure decision: is this verify receipt a complete pass for exactly this head and working tree (and, when given,
    # this environment)? merge-pr.ps1 calls it without an environment; verify.ps1 -ReuseIfFresh with one.
    param($Receipt, [string]$Head, [string]$TreeHash, $Environment = $null)

    $result = { param($ok, $reason) [pscustomobject]@{ Ok = $ok; Reason = $reason } }
    if (-not $Receipt)
    {
        return & $result $false 'missing receipt'
    }
    $status = if ($Receipt.PSObject.Properties['status']) { [string]$Receipt.status } else { '' }
    if ($status -ne 'pass')
    {
        return & $result $false "not pass (status '$status')"
    }
    if (-not $Receipt.PSObject.Properties['complete'] -or -not $Receipt.complete -or ($Receipt.PSObject.Properties['filter'] -and $Receipt.filter))
    {
        return & $result $false 'partial receipt'
    }
    if (-not $Receipt.PSObject.Properties['head'] -or [string]$Receipt.head -ne $Head)
    {
        return & $result $false 'head mismatch'
    }
    if (-not $Receipt.PSObject.Properties['treeHash'] -or [string]$Receipt.treeHash -ne $TreeHash)
    {
        return & $result $false 'tree mismatch'
    }
    if ($Environment)
    {
        foreach ($key in @('dotnet', 'pwsh', 'verifySha256'))
        {
            if (-not $Receipt.PSObject.Properties['environment'] -or -not $Receipt.environment.PSObject.Properties[$key] -or
                [string]$Receipt.environment.$key -ne [string]$Environment.$key)
            {
                return & $result $false "environment mismatch ($key)"
            }
        }
    }
    & $result $true 'receipt matches head and working tree'
}

function Read-VerifyReceipt([string]$Path)
{
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf))
    {
        return $null
    }
    try
    {
        Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    }
    catch
    {
        $null
    }
}

# The two files the Claude plugin pin rewrites. After every release the next PR must move the pin, and verify.ps1 does
# not need to run again for that change alone (check-agent-discovery.ps1 validates those files offline).
$script:PluginPinFiles = @('plugin/pointframe/server.lock.json', 'plugin/pointframe/.claude-plugin/plugin.json')

function Test-PinOnlyChange
{
    # Pure decision: does a receipt for the verified commit still cover the PR head? Only when verify.ps1 hashed exactly
    # that commit's tree (no uncommitted changes), the commit is an ancestor of the head, the worktree is the head's
    # tree, and every path changed since then is a plugin pin file.
    param(
        $Receipt,
        [string]$VerifiedCommitTree,
        [bool]$IsAncestor,
        [string]$HeadTree,
        [string]$CurrentTree,
        [string[]]$ChangedPaths
    )

    $result = { param($ok, $reason) [pscustomobject]@{ Ok = $ok; Reason = $reason } }
    $base = Test-VerifyReceiptObject $Receipt ([string]$Receipt.head) ([string]$Receipt.treeHash)
    if (-not $base.Ok)
    {
        return & $result $false $base.Reason
    }
    if ([string]$Receipt.treeHash -ne $VerifiedCommitTree)
    {
        return & $result $false 'tree mismatch (the verified tree had uncommitted changes)'
    }
    if (-not $IsAncestor)
    {
        return & $result $false 'head mismatch (the verified commit is not an ancestor of the PR head)'
    }
    if ($CurrentTree -ne $HeadTree)
    {
        return & $result $false 'tree mismatch (the worktree differs from the PR head)'
    }
    $changed = @($ChangedPaths | Where-Object { $_ })
    if ($changed.Count -eq 0)
    {
        return & $result $false 'head mismatch (no change since the verified commit, yet a different head)'
    }
    $other = @($changed | Where-Object { $script:PluginPinFiles -notcontains $_ })
    if ($other.Count -gt 0)
    {
        return & $result $false "head mismatch (changed since verify: $($other -join ', '))"
    }
    & $result $true 'only the plugin pin changed since the verified commit'
}
