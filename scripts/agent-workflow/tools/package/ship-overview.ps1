# ---- ship: from an accepted head to a merged pull request ---------------------------------------------------------

$script:ShipStageList = @('accepted', 'shipping', 'ship-failed', 'desktop-required', 'no-remote', 'auto-merge-enabled', 'merged')

function Add-ShipTrace([string]$Text)
{
    if ($env:AW_PKG_SELFTEST -and $env:AW_PKG_TEST_SHIPLOG) { Add-Content -LiteralPath $env:AW_PKG_TEST_SHIPLOG -Value $Text }
}

function Test-ShipCrash([string]$Point)
{
    # Fault injection exists only inside the self-test: the process dies without running any finally block.
    if ($env:AW_PKG_SELFTEST -and $env:AW_PKG_TEST_SHIP_CRASH -eq $Point) { [Environment]::Exit(99) }
}

function Test-ShipStepOk($Result)
{
    $items = @($Result)
    $items.Count -gt 0 -and $items[-1] -eq $true
}

function Resolve-ShipScript([string]$Name, [string]$Given, [string]$Default)
{
    # In the self-test only a marked stub is ever accepted, so a test can never fall through to the real script.
    if ($env:AW_PKG_SELFTEST)
    {
        if (-not $Given -or -not (Test-SelfTestStub $Given)) { throw "self-test guard: the $Name script has to be a marked stub" }
        return $Given
    }
    $path = if ($Given) { $Given } else { $Default }
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "the $Name script does not exist: $path" }
    $path
}

function Invoke-Gh([string[]]$GhArgs, [string]$Dir, [switch]$StdoutOnly)
{
    if ($env:AW_PKG_SELFTEST -and -not (Test-SelfTestStub $GhScript)) { throw 'self-test guard: gh has to be a marked stub (-GhScript)' }
    if (-not $GhScript -and -not (Get-Command gh -ErrorAction SilentlyContinue)) { throw 'the gh CLI was not found; install it and run gh auth login' }
    Push-Location -LiteralPath $Dir
    try
    {
        $raw = if ($GhScript) { if ($StdoutOnly) { @(& pwsh -NoProfile -File $GhScript @GhArgs 2>$null) } else { @(& pwsh -NoProfile -File $GhScript @GhArgs 2>&1) } }
        else { if ($StdoutOnly) { @(& gh @GhArgs 2>$null) } else { @(& gh @GhArgs 2>&1) } }
        $exit = $LASTEXITCODE
    }
    finally { Pop-Location }
    $lines = @($raw | ForEach-Object { "$_" })
    [pscustomobject]@{ Exit = $exit; Lines = $lines; Text = ($lines -join "`n") }
}

function Assert-ShipRemote([string]$Wt, [string]$Remote)
{
    $url = (Invoke-Git $Wt @('remote', 'get-url', $Remote))
    if ($env:AW_PKG_SELFTEST)
    {
        $allowed = [IO.Path]::GetFullPath($env:AW_PKG_SELFTEST_ROOT)
        $resolved = try { [IO.Path]::GetFullPath($url) } catch { $url }
        if (-not $resolved.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) { throw "self-test guard: remote '$Remote' is $url, not a repository under the self-test root" }
    }
    $url
}

function Get-ShipStep($State, [string]$Name)
{
    $ship = $State.ship
    if (-not $ship) { return $null }
    $steps = $ship.steps
    if ($steps -and $steps.ContainsKey($Name)) { $steps[$Name] } else { $null }
}

function Get-ShipView($State)
{
    # The ship steps in order with their recorded state, for show.
    if (-not $State.ship) { return $null }
    $steps = @()
    foreach ($name in @('base', 'pin', 'verify', 'record', 'qualify', 'push', 'pr', 'merge', 'channels'))
    {
        $record = Get-ShipStep $State $name
        $detail = ''
        if ($record)
        {
            $detail = switch ($name)
            {
                'base' { "head $(Get-Field $record 'head'), rebased $(Get-Field $record 'rebased')" }
                'pin' { "head $(Get-Field $record 'head'), pin changed $(Get-Field $record 'changed')" }
                'verify' { "head $(Get-Field $record 'head'), tree $(Get-Field $record 'tree'), receipt $(Get-Field (Get-Field $record 'receipt') 'path')" }
                'record' { "record of $(Get-Field $record 'recordCommit')" }
                'qualify' { if ((Get-Field $record 'status') -eq 'not-configured') { 'not configured (hooks.qualify is null)' } else { "desktop required $(Get-Field $record 'required')$(if (Get-Field $record 'qualificationId') { ", qualification $(Get-Field $record 'qualificationId') ($(Get-Field $record 'result'))" })" } }
                'push' { "head $(Get-Field $record 'head')" }
                'pr' { "#$(Get-Field $record 'number') $(Get-Field $record 'url')" }
                'merge' { "#$(Get-Field $record 'number'), merge-pr calls $(Get-Field $record 'calls'), merge commit $(Get-Field $record 'mergeCommit')" }
                'channels' { if ((Get-Field $record 'status') -eq 'not-configured') { 'not configured (hooks.channels is null)' } else { "release $(Get-Field $record 'version'): $(Get-Field $record 'final')" } }
            }
            $why = Get-Field $record 'reason'
            if ($why) { $detail += " - $why" }
        }
        $steps += [ordered]@{ step = $name; status = $(if ($record) { [string]$record.status } else { 'not started' }); utc = $(Get-Field $record 'utc'); detail = [string]$detail }
    }
    [ordered]@{ attempts = [int](Get-Field $State.ship 'attempts'); outcome = (Get-Field $State.ship 'outcome'); steps = $steps; qualifications = @(Get-List $State.qualifications).Count }
}

function Set-ShipStep($Info, [string]$StepName, [hashtable]$StepRecord, [string]$StageName = '')
{
    $recorded = $StepRecord
    $recorded.utc = (Get-Now)
    Update-State $Info.StateRoot $Id "ship-$StepName" {
        param($s)
        if (-not $s.ship) { $s.ship = @{ startedUtc = (Get-Now); attempts = 1; steps = @{} } }
        $s.ship.steps[$StepName] = $recorded
        if ($StageName) { $s.stage = $StageName }
    } | Out-Null
}

function Stop-Ship($Info, [string]$StageName, [string]$StepName, [hashtable]$StepRecord, [string]$Text, [int]$Code = 1)
{
    $StepRecord.reason = $Text
    Set-ShipStep $Info $StepName $StepRecord $StageName
    Write-Host $Text
    $script:ExitCode = $Code
    $false
}

