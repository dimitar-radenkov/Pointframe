#Requires -Version 7.0
function Resolve-CodexCommand([string]$TestScript)
{
    if ($TestScript -and (Test-Path -LiteralPath $TestScript -PathType Leaf))
    {
        $pwsh = Get-Command pwsh -CommandType Application -ErrorAction Stop | Select-Object -First 1
        return [pscustomobject]@{ Ok = $true; File = $pwsh.Source; Prefix = @('-NoProfile', '-File', "`"$TestScript`"") }
    }
    $command = Get-Command codex -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $command) { return [pscustomobject]@{ Ok = $false; Reason = 'codex is not on PATH' } }
    $file = $command.Source
    $prefix = @()
    if ([IO.Path]::GetExtension($file) -ne '.exe')
    {
        $script = Join-Path (Split-Path $file -Parent) 'node_modules/@openai/codex/bin/codex.js'
        $node = Get-Command node -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not (Test-Path -LiteralPath $script) -or -not $node)
        { return [pscustomobject]@{ Ok = $false; Reason = "cannot resolve the codex npm shim to node + codex.js ($script)" } }
        $file = $node.Source
        $prefix = @("`"$script`"")
    }
    [pscustomobject]@{ Ok = $true; File = $file; Prefix = $prefix }
}

function New-CodexLimitWindow
{
    param(
        $Window,
        [int]$FallbackMinutes,
        [long]$Now,
        $Timestamp
    )

    if ($Window -isnot [Collections.IDictionary])
    {
        return $null
    }

    if (-not $Window.Contains('used_percent'))
    {
        return $null
    }

    $numberStyles = [Globalization.NumberStyles]::Float
    $culture = [Globalization.CultureInfo]::InvariantCulture
    $percent = 0.0
    $minutes = $FallbackMinutes
    $reset = $null
    if (-not [double]::TryParse([string]$Window['used_percent'], $numberStyles, $culture, [ref]$percent) -or -not [double]::IsFinite($percent))
    {
        return $null
    }

    $parsedMinutes = 0
    if ($Window.Contains('window_minutes') -and
        [int]::TryParse([string]$Window['window_minutes'], [Globalization.NumberStyles]::Integer, $culture, [ref]$parsedMinutes))
    {
        $minutes = $parsedMinutes
    }

    $parsedReset = 0L
    if ($Window.Contains('resets_at') -and
        [long]::TryParse([string]$Window['resets_at'], [Globalization.NumberStyles]::Integer, $culture, [ref]$parsedReset))
    {
        $reset = $parsedReset
    }
    elseif ($Window.Contains('resets_in_seconds') -and $null -ne $Timestamp)
    {
        $seconds = 0.0
        $timestampValue = [DateTimeOffset]::MinValue
        if ([double]::TryParse([string]$Window['resets_in_seconds'], $numberStyles, $culture, [ref]$seconds) -and
            [DateTimeOffset]::TryParse([string]$Timestamp, $culture, [Globalization.DateTimeStyles]::AssumeUniversal, [ref]$timestampValue))
        {
            $reset = $timestampValue.ToUnixTimeSeconds() + [long][Math]::Truncate($seconds)
        }
    }

    return [pscustomobject]@{
        usedPercent = $percent
        windowMinutes = $minutes
        resetsAt = $reset
        stale = ($null -ne $reset -and $reset -le $Now)
    }
}

function Get-CodexRateLimits
{
    try
    {
        $codexHome = if ($env:CODEX_HOME)
        {
            $env:CODEX_HOME
        }
        else
        {
            Join-Path ([Environment]::GetFolderPath('UserProfile')) '.codex'
        }

        $sessions = Join-Path $codexHome 'sessions'
        if (-not (Test-Path -LiteralPath $sessions -PathType Container))
        {
            return [pscustomobject]@{
                known = $false
                reason = 'no sessions directory'
                source = $null
                observedAt = $null
                fiveHour = $null
                weekly = $null
            }
        }

        $file = Get-ChildItem -LiteralPath $sessions -Filter '*.jsonl' -File -Recurse -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTimeUtc -Descending |
            Select-Object -First 1
        if (-not $file)
        {
            return [pscustomobject]@{
                known = $false
                reason = 'no session file'
                source = $null
                observedAt = $null
                fiveHour = $null
                weekly = $null
            }
        }

        try
        {
            $reader = [IO.File]::OpenText($file.FullName)
        }
        catch
        {
            return [pscustomobject]@{
                known = $false
                reason = 'session file unreadable'
                source = $null
                observedAt = $null
                fiveHour = $null
                weekly = $null
            }
        }
        $found = $null
        $observedAt = $null
        $sawRateLimitsLine = $false
        $readFailed = $false
        try
        {
            while (($line = $reader.ReadLine()) -ne $null)
            {
                if (-not $line.Contains('"rate_limits"'))
                {
                    continue
                }

                try
                {
                    $record = $line | ConvertFrom-Json -AsHashtable -ErrorAction Stop
                }
                catch
                {
                    continue
                }

                if ($record -isnot [Collections.IDictionary] -or -not $record.Contains('payload') -or
                    $record['payload'] -isnot [Collections.IDictionary] -or -not $record['payload'].Contains('rate_limits'))
                {
                    continue
                }

                $sawRateLimitsLine = $true
                $candidate = $record['payload']['rate_limits']
                if ($candidate -isnot [Collections.IDictionary])
                {
                    continue
                }

                $candidateTimestamp = $null
                $candidateObservedAt = $null
                if ($record.Contains('timestamp') -and $null -ne $record['timestamp'])
                {
                    $candidateTimestamp = $record['timestamp']
                    if ($candidateTimestamp -is [DateTime])
                    {
                        $candidateObservedAt = $candidateTimestamp.ToUniversalTime().ToString('o')
                    }
                    else
                    {
                        $candidateObservedAt = [string]$candidateTimestamp
                    }
                }

                $now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
                $primaryRaw = if ($candidate.Contains('primary'))
                {
                    $candidate['primary']
                }
                else
                {
                    $null
                }
                $secondaryRaw = if ($candidate.Contains('secondary'))
                {
                    $candidate['secondary']
                }
                else
                {
                    $null
                }
                $primary = New-CodexLimitWindow $primaryRaw 300 $now $candidateTimestamp
                $secondary = New-CodexLimitWindow $secondaryRaw 10080 $now $candidateTimestamp
                $candidateFiveHour = $primary
                $candidateWeekly = $secondary

                if (($null -ne $primary -and $primary.windowMinutes -eq 10080) -or
                    ($null -ne $primary -and $primary.windowMinutes -ne 300 -and $null -ne $secondary -and $secondary.windowMinutes -eq 300))
                {
                    $candidateFiveHour = $secondary
                    $candidateWeekly = $primary
                }

                if ($null -eq $candidateFiveHour -and $null -eq $candidateWeekly)
                {
                    continue
                }

                $found = [pscustomobject]@{
                    fiveHour = $candidateFiveHour
                    weekly = $candidateWeekly
                }
                $observedAt = $candidateObservedAt
            }
        }
        catch
        {
            $readFailed = $true
        }
        finally
        {
            $reader.Dispose()
        }

        if ($readFailed)
        {
            return [pscustomobject]@{
                known = $false
                reason = 'session file unreadable'
                source = $null
                observedAt = $null
                fiveHour = $null
                weekly = $null
            }
        }

        if ($null -eq $found)
        {
            $reason = if ($sawRateLimitsLine) { 'rate_limits unreadable' } else { 'no rate_limits line' }
            return [pscustomobject]@{
                known = $false
                reason = $reason
                source = if ($sawRateLimitsLine) { $file.Name } else { $null }
                observedAt = $null
                fiveHour = $null
                weekly = $null
            }
        }

        return [pscustomobject]@{
            known = $true
            reason = $null
            source = $file.Name
            observedAt = $observedAt
            fiveHour = $found.fiveHour
            weekly = $found.weekly
        }
    }
    catch
    {
        return [pscustomobject]@{
            known = $false
            reason = "rate_limits unreadable: $($_.Exception.Message)"
            source = $null
            observedAt = $null
            fiveHour = $null
            weekly = $null
        }
    }
}

function Format-CodexWindow
{
    param($Window)
    if ($null -eq $Window)
    {
        return 'unknown'
    }

    $reset = if ($null -ne $Window.resetsAt)
    {
        'resets ' + [DateTimeOffset]::FromUnixTimeSeconds([long]$Window.resetsAt).UtcDateTime.ToString('yyyy-MM-dd HH:mmZ')
    }
    else
    {
        'reset unknown'
    }

    $value = [double]$Window.usedPercent
    if (-not [double]::IsFinite($value))
    {
        return 'unknown'
    }

    if ([Math]::Abs($value) -le ([double]::MaxValue / 100))
    {
        $value = [Math]::Truncate([Math]::Round($value * 100, 6)) / 100
    }

    $percentText = $value.ToString('0.##', [Globalization.CultureInfo]::InvariantCulture)
    return "$percentText% ($reset)"
}

function Format-CodexRateLimits
{
    param($Limits)
    try
    {
        if (-not $Limits.known)
        {
            return "Codex limits: unknown ($($Limits.reason))"
        }

        return "Codex limits: 5h $(Format-CodexWindow $Limits.fiveHour), weekly $(Format-CodexWindow $Limits.weekly) [source $($Limits.source)]"
    }
    catch
    {
        return "Codex limits: unknown (format error: $($_.Exception.Message))"
    }
}

function Test-CodexLimitGate
{
    param($Limits, $Config)

    $limitConfig = if ($Config -is [Collections.IDictionary] -and $Config.Contains('codex') -and $Config.codex.Contains('limits'))
    {
        $Config.codex.limits
    }
    else
    {
        @{}
    }

    $weeklyThreshold = [int](Get-AwSetting $limitConfig 'weeklyRefusePercent' 80)
    $fiveHourThreshold = [int](Get-AwSetting $limitConfig 'fiveHourWarnPercent' 80)
    $refuse = [bool]($Limits.weekly -and -not $Limits.weekly.stale -and $Limits.weekly.usedPercent -ge $weeklyThreshold)
    $warn = [bool]($Limits.fiveHour -and -not $Limits.fiveHour.stale -and $Limits.fiveHour.usedPercent -ge $fiveHourThreshold)
    $message = $null

    if ($refuse)
    {
        $message = "Refused: Codex weekly limit at $($Limits.weekly.usedPercent)% (>= $weeklyThreshold%, W-03)"
    }
    elseif ($warn)
    {
        $message = "Codex 5-hour limit at $($Limits.fiveHour.usedPercent)% (>= $fiveHourThreshold%)"
    }

    return [pscustomobject]@{
        refuse = $refuse
        warn = $warn
        message = $message
    }
}

function Get-AwCodexPolicy
{
    # The Codex models a configuration allows: every model of a role whose provider is openai. The model of the
    # heavyReviewer role is the strong one: it needs an escalation reason for workspace-write, like high effort does.
    param($Config)
    $allowed = [Collections.Generic.List[string]]::new()
    $strong = [Collections.Generic.List[string]]::new()
    foreach ($name in $Config.roles.Keys)
    {
        $role = $Config.roles[$name]
        if ($role.provider -ne 'openai') { continue }
        if (-not $allowed.Contains([string]$role.model)) { $allowed.Add([string]$role.model) }
        if ($name -eq 'heavyReviewer' -and -not $strong.Contains([string]$role.model)) { $strong.Add([string]$role.model) }
    }
    [pscustomobject]@{ Allowed = $allowed.ToArray(); Strong = $strong.ToArray() }
}

function Test-CodexRunPolicy
{
    param([string]$Model, [string]$Effort, [string]$EscalationReason, [string]$Sandbox, [bool]$AllowStrongReadOnly = $false, [string[]]$AllowedModels = @(), [string[]]$StrongModels = @())
    $reasons = [Collections.Generic.List[string]]::new()
    if ($Model -notin $AllowedModels)
    { if ($Model -match 'astra|5\.') { $reasons.Add("model '$Model' is refused; the owner must approve it") } else { $reasons.Add("model '$Model' is not allowed") } }
    if (($Effort -in @('high', 'xhigh', 'max') -or $Model -in $StrongModels) -and [string]::IsNullOrWhiteSpace($EscalationReason) -and (-not $AllowStrongReadOnly -or $Sandbox -eq 'workspace-write'))
    { $reasons.Add($(if (@($StrongModels).Count) { "high effort and $(@($StrongModels) -join ', ') require" } else { 'high effort requires' }) + ' -EscalationReason for workspace-write') }
    if ($Sandbox -notin @('workspace-write', 'read-only')) { $reasons.Add("sandbox '$Sandbox' is not accepted") }
    [pscustomobject]@{ Ok = ($reasons.Count -eq 0); Reasons = @($reasons) }
}
