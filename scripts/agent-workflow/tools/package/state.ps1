# ---- State: atomic writes, per-package lock, history ------------------------------------------------------------

function Enter-StateLock([string]$Root, [string]$Slug)
{
    New-Item -ItemType Directory -Path $Root -Force | Out-Null
    $path = (Get-PackagePaths $Root $Slug).Lock
    $deadline = [DateTime]::UtcNow.AddSeconds($LockTimeoutSeconds)
    while ($true)
    {
        try { return [IO.File]::Open($path, 'OpenOrCreate', 'ReadWrite', 'None') }
        catch [IO.IOException]
        {
            if ([DateTime]::UtcNow -ge $deadline) { throw "state of package '$Slug' is locked by another process ($path)" }
            Start-Sleep -Milliseconds 100
        }
    }
}

function Read-State([string]$Root, [string]$Slug)
{
    $path = (Get-PackagePaths $Root $Slug).State
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "unknown package '$Slug' (no state at $path)" }
    $state = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -AsHashtable @script:JsonDate
    if ($state.schemaVersion -ne $SchemaVersion) { throw "state schema version $($state.schemaVersion) is not supported (expected $SchemaVersion)" }
    # Packages created before commit, review and accept existed get the new fields empty; the schema version is unchanged.
    if (-not $state.ContainsKey('repair') -or $null -eq $state['repair']) { $state['repair'] = @{ limit = $script:RepairLimit; roundsUsed = 0; resets = @() } }
    foreach ($key in @('reviews', 'commits')) { if (-not $state.ContainsKey($key) -or $null -eq $state[$key]) { $state[$key] = @() } }
    foreach ($key in @('commit', 'acceptance', 'pendingFix', 'ship')) { if (-not $state.ContainsKey($key)) { $state[$key] = $null } }
    foreach ($key in @('qualifications', 'pushes')) { if (-not $state.ContainsKey($key) -or $null -eq $state[$key]) { $state[$key] = @() } }
    # Added after the first release (optional, so a state file written by v0.3.x reads as it did): the audited recoveries, the findings a
    # coordinator wrote for a FAIL verdict that had none, and the review rounds refunded for a host-detected environment failure.
    foreach ($key in @('recoveries', 'coordinatorFindings')) { if (-not $state.ContainsKey($key) -or $null -eq $state[$key]) { $state[$key] = @() } }
    if (-not $state.repair.ContainsKey('refunds') -or $null -eq $state.repair['refunds']) { $state.repair['refunds'] = @() }
    $state
}

function Write-StateAtomic([string]$Root, [string]$Slug, [hashtable]$State, [scriptblock]$BeforeRename = $null)
{
    # A crash leaves the old file or the new one: the content goes to a temp file in the same directory and is
    # renamed over the state file. Stale temp files are removed by the next writer, which holds the lock.
    $path = (Get-PackagePaths $Root $Slug).State
    Get-ChildItem -LiteralPath $Root -Filter "$Slug.json.*.tmp" -File -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
    $temp = "$path.$([guid]::NewGuid().ToString('N')).tmp"
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($State | ConvertTo-Json -Depth 20))
    $stream = [IO.File]::Open($temp, 'CreateNew', 'Write', 'None')
    try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) }
    finally { $stream.Dispose() }
    if ($BeforeRename) { & $BeforeRename }
    [IO.File]::Move($temp, $path, $true)
}

function Update-State([string]$Root, [string]$Slug, [string]$TransitionEvent, [scriptblock]$Mutate, [scriptblock]$BeforeRename = $null, [switch]$Locked)
{
    $stateLock = if ($Locked) { $null } else { Enter-StateLock $Root $Slug }
    try
    {
        $current = Read-State $Root $Slug
        $fromStage = [string]$current.stage
        & $Mutate $current | Out-Null
        $current.history = @($current.history) + @(@{ utc = (Get-Now); event = $TransitionEvent; from = $fromStage; to = [string]$current.stage })
        Write-StateAtomic $Root $Slug $current $BeforeRename
        $current
    }
    finally { if ($stateLock) { $stateLock.Dispose() } }
}

