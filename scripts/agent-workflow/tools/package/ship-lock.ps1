# ---- ship: the repository-wide lock -----------------------------------------------------------------------------

function Read-ShipLockInfo([string]$Path)
{
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    try { return (Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -AsHashtable @script:JsonDate) }
    catch { return @{ slug = '?'; pid = 0; startUtc = ''; utc = ''; unreadable = $true } }
}

function Enter-ShipLock($Info, [string]$Slug)
{
    # One ship in flight per repository (W-30). The lock itself is an exclusive open that the OS releases when the holder dies; ship.json
    # says who held it. A leftover ship.json with a free lock is a ship that died: it is reported, and only the same package may take
    # it over (resume). Another package's dead ship may have left a pull request or an auto-merge in flight, so that one is refused.
    $directory = Join-Path $Info.CommonDir $script:Namespace
    $lockPath = Join-Path $directory 'ship.lock'
    $metaPath = Join-Path $directory 'ship.json'
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    try { $stream = [IO.File]::Open($lockPath, 'OpenOrCreate', 'ReadWrite', 'None') }
    catch [IO.IOException]
    {
        $holder = Read-ShipLockInfo $metaPath
        $who = if ($holder) { "package '$($holder.slug)' (pid $($holder.pid), since $($holder.utc))" } else { 'another process' }
        throw "another ship is in flight: $who. Only one ship runs at a time (one release in flight); wait for it, or resume it with its own ship command."
    }
    $previous = Read-ShipLockInfo $metaPath
    if ($previous)
    {
        $alive = (-not $previous.ContainsKey('unreadable')) -and (Test-ProcessIdentityAlive ([int]$previous.pid) ([string]$previous.startUtc))
        if ($alive) { $stream.Dispose(); throw "the ship lock file names the live process $($previous.pid) (package '$($previous.slug)') although the lock is free; not taking it over ($metaPath)" }
        if ([string]$previous.slug -ne $Slug)
        {
            $stream.Dispose()
            throw "stale ship lock: the ship of package '$($previous.slug)' (pid $($previous.pid), started $($previous.utc)) died without finishing and may have left a pull request or an auto-merge in flight. Resume it with: pwsh scripts/package.ps1 ship -Id $($previous.slug) (or, after checking by hand that nothing is in flight, delete $metaPath)."
        }
        Write-Host "NOTE stale ship lock: an earlier ship of this package (pid $($previous.pid), started $($previous.utc)) died without finishing; its lock was free, so this ship resumes it."
    }
    $meta = @{ pid = $PID; startUtc = (Get-ProcessStartIdentity $PID); slug = $Slug; utc = (Get-Now) }
    [IO.File]::WriteAllText($metaPath, ($meta | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    $stream
}

function Exit-ShipLock($Stream, $Info)
{
    $directory = Join-Path $Info.CommonDir $script:Namespace
    Remove-Item -LiteralPath (Join-Path $directory 'ship.json') -Force -ErrorAction SilentlyContinue
    if ($Stream) { $Stream.Dispose() }
    Remove-Item -LiteralPath (Join-Path $directory 'ship.lock') -Force -ErrorAction SilentlyContinue
}

