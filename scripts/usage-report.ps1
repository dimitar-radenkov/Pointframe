#Requires -Version 7.0
<#
Repeatable Pointframe usage report from Azure Application Insights (resource pointframe-monitoring).

  pwsh scripts/usage-report.ps1                         # text report: last 8 complete UTC weeks of cohorts
  pwsh scripts/usage-report.ps1 -Json                   # one machine-readable object instead of text
  pwsh scripts/usage-report.ps1 -OutFile report.txt     # also write the output to a file
  pwsh scripts/usage-report.ps1 -From 2026-08-10 -To 2026-10-05
  pwsh scripts/usage-report.ps1 -SandboxView            # add percentages with sandbox-like installs removed
  pwsh scripts/usage-report.ps1 -SelfTest               # offline: result shaping on scripts/tests/usage-report fixtures

Needs `az` logged in (exit 2 otherwise). Read-only: every query goes to the data API
(az rest --resource https://api.applicationinsights.io ... /v1/apps/<appId>/query); the ARM /api/query path does
not work for this resource. Exit 1 when any section fails (the rest is still printed), 2 on bad arguments or login.

An install is identified by customDimensions.install_id. App events are customEvents whose name does not start
with "website"; website events start with "website_". Activated = the install ever sent capture_completed or
recording_completed. Sandbox-like (heuristic only) = lifetime under 2 minutes and only startup events; raw numbers
are always shown. Excluded installs (-ExcludeInstallId, or any install that ever reported -ExcludeRoleInstance) are
removed from every app query.
#>
[CmdletBinding()]
param(
    [string]$AppId = '6733b212-1b3a-482e-97f0-6e21d94207fe',
    [string]$From,
    [string]$To,
    [string]$ExcludeRoleInstance = 'MITKO-ROG-STRIX',
    [string[]]$ExcludeInstallId = @(),
    [switch]$SandboxView,
    [switch]$Json,
    [string]$OutFile,
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest

$LoginHint = 'az login --use-device-code --tenant 19b3ded3-8748-441b-bc1b-08a986f52ae0'

# ---------------------------------------------------------------- KQL builders

function ConvertTo-KqlString([string]$Value)
{
    if ($Value -notmatch '^[\w.\- ]*$')
    {
        throw "Unsupported characters in '$Value'; only letters, digits, space, '.', '-' and '_' are allowed."
    }
    '"' + $Value + '"'
}

function ConvertTo-KqlDatetime([datetime]$Value)
{
    'datetime(' + $Value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", [cultureinfo]::InvariantCulture) + ')'
}

function Expand-Kql([string]$Template, [hashtable]$Ctx)
{
    $ids = @($Ctx.ExcludeInstallId | Where-Object { $_ } | ForEach-Object { ConvertTo-KqlString $_ })
    $idList = if ($ids.Count -gt 0) { $ids -join ', ' } else { '""' }
    $text = $Template.Replace('{{EXCLUDE_IDS}}', $idList)
    $text = $text.Replace('{{EXCLUDE_ROLE}}', (ConvertTo-KqlString ([string]$Ctx.ExcludeRoleInstance)))
    $text = $text.Replace('{{FROM}}', (ConvertTo-KqlDatetime $Ctx.From))
    $text = $text.Replace('{{TO}}', (ConvertTo-KqlDatetime $Ctx.To))
    $text.Trim()
}

$AppEventsKql = @'
let excludedIds = dynamic([{{EXCLUDE_IDS}}]);
let excludedRole = {{EXCLUDE_ROLE}};
let roleInstalls = customEvents
| where isnotempty(excludedRole) and cloud_RoleInstance == excludedRole
| extend install = tostring(customDimensions.install_id)
| where isnotempty(install)
| distinct install;
let AppEvents = customEvents
| where name !startswith "website"
| extend install = tostring(customDimensions.install_id)
| where isnotempty(install) and install !in (excludedIds)
| join kind=leftanti roleInstalls on install;
'@

$InstallsKql = @'
let startupEvents = dynamic(["app_started", "startup_completed", "app_heartbeat", "app_closed", "update_available"]);
let activationEvents = dynamic(["capture_completed", "recording_completed"]);
let Installs = AppEvents
| summarize firstSeen = min(timestamp), lastSeen = max(timestamp),
    starts = countif(name == "app_started"),
    snips = countif(name == "snip_started"),
    cancels = countif(name == "snip_cancelled"),
    nonStartup = countif(name !in (startupEvents)),
    activatedAt = minif(timestamp, name in (activationEvents)),
    activeDays = dcount(startofday(timestamp)),
    firstStart = minif(strcat(format_datetime(timestamp, "yyyyMMddHHmmss"), "|", tostring(customDimensions.version), "|", tostring(customDimensions.os_build)), name == "app_started")
    by install
| extend parts = split(firstStart, "|")
| extend version = tostring(parts[1]), osBuild = tostring(parts[2])
| extend versionParts = split(version, ".")
| extend minor = iff(array_length(versionParts) >= 2, strcat(versionParts[0], ".", versionParts[1]), "unknown")
| extend buildParts = split(osBuild, ".")
| extend buildNumber = toint(iff(array_length(buildParts) >= 3, tostring(buildParts[2]), tostring(buildParts[array_length(buildParts) - 1])))
| extend os = case(isnull(buildNumber), "unknown", buildNumber >= 22000, "Win11", "Win10")
| extend activated = isnotnull(activatedAt)
| extend sandboxLike = (lastSeen - firstSeen) < 2m and nonStartup == 0;
'@

function Get-Prelude
{
    $AppEventsKql + "`n" + $InstallsKql
}

$QueryBuilders = [ordered]@{
    totals = {
        param($Ctx)
        Expand-Kql ((Get-Prelude) + @'

Installs
| summarize installs = count(), activated = countif(activated), sandboxLike = countif(sandboxLike)
'@) $Ctx
    }
    active = {
        param($Ctx)
        Expand-Kql ((Get-Prelude) + @'

Installs
| where activated
| summarize active1d = countif(lastSeen > ago(1d)), active7d = countif(lastSeen > ago(7d)), active30d = countif(lastSeen > ago(30d))
'@) $Ctx
    }
    cohorts = {
        param($Ctx)
        Expand-Kql ((Get-Prelude) + @'

Installs
| where firstSeen >= {{FROM}} and firstSeen < {{TO}}
| extend weekIndex = toint((firstSeen - {{FROM}}) / 7d)
| summarize size = count(), sandboxLike = countif(sandboxLike),
    sandboxActivated24h = countif(sandboxLike and activated and activatedAt - firstSeen <= 1d),
    activated24h = countif(activated and activatedAt - firstSeen <= 1d),
    activated7d = countif(activated and activatedAt - firstSeen <= 7d)
    by weekIndex
| order by weekIndex asc
'@) $Ctx
    }
    splitOs = {
        param($Ctx)
        Expand-Kql ((Get-Prelude) + @'

Installs
| where firstSeen >= {{FROM}} and firstSeen < {{TO}} and firstSeen <= now() - 7d
| summarize size = count(), sandboxLike = countif(sandboxLike), activated7d = countif(activated and activatedAt - firstSeen <= 7d) by key = os
| order by key asc
'@) $Ctx
    }
    splitMinor = {
        param($Ctx)
        Expand-Kql ((Get-Prelude) + @'

Installs
| where firstSeen >= {{FROM}} and firstSeen < {{TO}} and firstSeen <= now() - 7d
| summarize size = count(), sandboxLike = countif(sandboxLike), activated7d = countif(activated and activatedAt - firstSeen <= 7d) by key = minor
| order by key asc
'@) $Ctx
    }
    funnel = {
        param($Ctx)
        Expand-Kql ((Get-Prelude) + @'

Installs
| where not(activated)
| summarize never = count(),
    startedOnce = countif(starts >= 1),
    startedTwicePlus = countif(starts >= 2),
    ranOver10Min = countif(lastSeen - firstSeen > 10m),
    startedSnip = countif(snips > 0),
    allSnipsCancelled = countif(snips > 0 and cancels >= snips),
    sandboxLike = countif(sandboxLike)
'@) $Ctx
    }
    retention = {
        param($Ctx)
        Expand-Kql ((Get-Prelude) + @'

Installs
| where activated
| summarize activated = count(), days2Plus = countif(activeDays >= 2), days5Plus = countif(activeDays >= 5), span30Plus = countif(lastSeen - firstSeen >= 30d)
'@) $Ctx
    }
    onboardingShown = {
        param($Ctx)
        Expand-Kql ($AppEventsKql + @'

AppEvents
| where name == "onboarding_shown"
| summarize events = count(), installs = dcount(install)
'@) $Ctx
    }
    onboardingActions = {
        param($Ctx)
        Expand-Kql ($AppEventsKql + @'

AppEvents
| where name == "onboarding_action"
| summarize events = count(), installs = dcount(install) by action = tostring(customDimensions.action)
| order by events desc
'@) $Ctx
    }
    hotkeyStatus = {
        param($Ctx)
        Expand-Kql ($AppEventsKql + @'

AppEvents
| where name == "hotkey_status"
| summarize events = count(), installs = dcount(install) by status = tostring(customDimensions.status)
| order by events desc
'@) $Ctx
    }
    onboardingFirstCaptures = {
        param($Ctx)
        Expand-Kql ($AppEventsKql + @'

AppEvents
| where name == "first_capture_completed" and tostring(customDimensions.source) == "onboarding"
| summarize events = count(), installs = dcount(install)
'@) $Ctx
    }
    exceptions = {
        param($Ctx)
        Expand-Kql ($AppEventsKql + @'

AppEvents
| where timestamp > ago(30d) and name == "unhandled_exception"
| summarize events = count(), installs = dcount(install) by version = tostring(customDimensions.version), exceptionType = tostring(customDimensions.exception_type)
| order by events desc
| take 15
'@) $Ctx
    }
    recordingFailed = {
        param($Ctx)
        Expand-Kql ($AppEventsKql + @'

AppEvents
| where timestamp > ago(30d) and name == "recording_failed"
| summarize events = count(), installs = dcount(install) by version = tostring(customDimensions.version), phase = tostring(customDimensions.phase), reason = tostring(customDimensions.reason)
| order by events desc
'@) $Ctx
    }
    website = {
        param($Ctx)
        Expand-Kql @'
customEvents
| where timestamp > ago(90d) and name startswith "website_"
| summarize sessions = dcount(tostring(customDimensions.session_id)),
    ctaClicks = countif(name == "website_cta_clicked"),
    ctaSessions = dcountif(tostring(customDimensions.session_id), name == "website_cta_clicked")
'@ $Ctx
    }
}

# ------------------------------------------------------------ transport and parsing

function Invoke-AppInsightsQuery([string]$AppId, [string]$Kql)
{
    $bodyFile = Join-Path ([IO.Path]::GetTempPath()) ("usage-report-" + [guid]::NewGuid().ToString('N') + '.json')
    try
    {
        [IO.File]::WriteAllText($bodyFile, (@{ query = $Kql } | ConvertTo-Json -Compress), [Text.UTF8Encoding]::new($false))
        $url = "https://api.applicationinsights.io/v1/apps/$AppId/query"
        $output = & az rest --method post --resource https://api.applicationinsights.io --url $url --body "@$bodyFile" 2>&1
        $text = ($output | ForEach-Object { "$_" }) -join "`n"
        if ($LASTEXITCODE -ne 0)
        {
            throw "az rest failed (exit $LASTEXITCODE): $($text.Trim())"
        }
        $text | ConvertFrom-Json
    }
    finally
    {
        Remove-Item -LiteralPath $bodyFile -Force -ErrorAction SilentlyContinue
    }
}

function ConvertTo-Rows($Response)
{
    $errorProp = $Response.PSObject.Properties['error']
    if ($null -ne $errorProp -and $null -ne $errorProp.Value)
    {
        $e = $errorProp.Value
        $message = if ($e.PSObject.Properties['message']) { $e.message } else { "$e" }
        $code = if ($e.PSObject.Properties['code']) { $e.code } else { 'error' }
        throw "API error ${code}: $message"
    }
    $tablesProp = $Response.PSObject.Properties['tables']
    if ($null -eq $tablesProp -or @($tablesProp.Value).Count -eq 0)
    {
        return @()
    }
    $table = @($tablesProp.Value)[0]
    $names = @($table.columns | ForEach-Object { $_.name })
    $rows = foreach ($row in @($table.rows))
    {
        $obj = [ordered]@{}
        for ($i = 0; $i -lt $names.Count; $i++)
        {
            $obj[$names[$i]] = $row[$i]
        }
        [pscustomobject]$obj
    }
    $rows
}

function Get-Value($Row, [string]$Name)
{
    if ($null -eq $Row)
    {
        return 0
    }
    $p = $Row.PSObject.Properties[$Name]
    if ($null -eq $p -or $null -eq $p.Value)
    {
        return 0
    }
    $p.Value
}

# ------------------------------------------------------------------ window and shaping

function Resolve-Window([string]$From, [string]$To, [datetime]$Now)
{
    $style = [Globalization.DateTimeStyles]::AssumeUniversal -bor [Globalization.DateTimeStyles]::AdjustToUniversal
    $parse = {
        param($s, $label)
        $parsed = [datetime]::MinValue
        if (-not [datetime]::TryParse($s, [cultureinfo]::InvariantCulture, $style, [ref]$parsed))
        {
            throw "Cannot parse -$label '$s' as a date."
        }
        $parsed
    }
    $nowUtc = $Now.ToUniversalTime()
    $daysSinceMonday = (([int]$nowUtc.DayOfWeek) + 6) % 7
    $thisMonday = $nowUtc.Date.AddDays(-$daysSinceMonday)
    $toValue = if ($To) { & $parse $To 'To' } else { $thisMonday }
    $fromValue = if ($From) { & $parse $From 'From' } else { $toValue.AddDays(-56) }
    if ($fromValue -ge $toValue)
    {
        throw "-From ($fromValue) must be earlier than -To ($toValue)."
    }
    @{ From = $fromValue; To = $toValue }
}

function Get-Percent($Part, $Whole)
{
    if ($null -eq $Whole -or [double]$Whole -le 0)
    {
        return $null
    }
    [math]::Round(100.0 * [double]$Part / [double]$Whole, 1)
}

function Format-Percent($Value)
{
    if ($null -eq $Value) { return '-' }
    ('{0:0.0}%' -f $Value)
}

function Get-CohortRows($Rows, [datetime]$From, [datetime]$To, [datetime]$Now)
{
    $nowUtc = $Now.ToUniversalTime()
    $out = foreach ($r in $Rows)
    {
        $index = [int](Get-Value $r 'weekIndex')
        $start = $From.AddDays(7 * $index)
        $end = $start.AddDays(7)
        if ($end -gt $To) { $end = $To }
        $size = [int](Get-Value $r 'size')
        $sandbox = [int](Get-Value $r 'sandboxLike')
        $mature24 = $end.AddDays(1) -le $nowUtc
        $mature7 = $end.AddDays(7) -le $nowUtc
        $a24 = [int](Get-Value $r 'activated24h')
        $a7 = [int](Get-Value $r 'activated7d')
        $sandboxActivated = [int](Get-Value $r 'sandboxActivated24h')
        [pscustomobject][ordered]@{
            weekStart = $start.ToString('yyyy-MM-dd')
            size = $size
            sandboxLike = $sandbox
            activated24h = if ($mature24) { $a24 } else { $null }
            activated7d = if ($mature7) { $a7 } else { $null }
            activation7dPct = if ($mature7) { Get-Percent $a7 $size } else { $null }
            activation7dPctExSandbox = if ($mature7) { Get-Percent ($a7 - $sandboxActivated) ($size - $sandbox) } else { $null }
            immature = (-not $mature7)
        }
    }
    , @($out)
}

function Get-SplitRows($Rows)
{
    $out = foreach ($r in $Rows)
    {
        $size = [int](Get-Value $r 'size')
        $sandbox = [int](Get-Value $r 'sandboxLike')
        $a7 = [int](Get-Value $r 'activated7d')
        [pscustomobject][ordered]@{
            key = [string](Get-Value $r 'key')
            size = $size
            sandboxLike = $sandbox
            activated7d = $a7
            activation7dPct = Get-Percent $a7 $size
            activation7dPctExSandbox = Get-Percent $a7 ($size - $sandbox)
        }
    }
    , @($out)
}

function Get-Single($Rows)
{
    $list = @($Rows)
    if ($list.Count -eq 0) { return $null }
    $list[0]
}

function Get-Section([scriptblock]$Body)
{
    try
    {
        $result = & $Body
        @{ ok = $true; data = $result; error = $null }
    }
    catch
    {
        @{ ok = $false; data = $null; error = $_.Exception.Message }
    }
}

function New-Report([scriptblock]$Runner, [hashtable]$Ctx, [datetime]$Now)
{
    $q = {
        param($name)
        $kql = & $QueryBuilders[$name] $Ctx
        ConvertTo-Rows (& $Runner $name $kql)
    }
    $report = [ordered]@{}
    $report.generatedAt = $Now.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")
    $report.window = [ordered]@{ from = $Ctx.From.ToString('yyyy-MM-dd'); to = $Ctx.To.ToString('yyyy-MM-dd') }
    $report.exclusions = [ordered]@{ roleInstance = $Ctx.ExcludeRoleInstance; installIds = @($Ctx.ExcludeInstallId).Count }

    $report.totals = Get-Section {
        $r = Get-Single (& $q 'totals')
        $installs = [int](Get-Value $r 'installs')
        $activated = [int](Get-Value $r 'activated')
        $sandbox = [int](Get-Value $r 'sandboxLike')
        [ordered]@{
            installs = $installs
            activated = $activated
            activationPct = Get-Percent $activated $installs
            sandboxLike = $sandbox
            installsExSandbox = $installs - $sandbox
            activationPctExSandbox = Get-Percent $activated ($installs - $sandbox)
        }
    }
    $report.active = Get-Section {
        $r = Get-Single (& $q 'active')
        [ordered]@{
            days1 = [int](Get-Value $r 'active1d')
            days7 = [int](Get-Value $r 'active7d')
            days30 = [int](Get-Value $r 'active30d')
        }
    }
    $report.cohorts = Get-Section { Get-CohortRows (& $q 'cohorts') $Ctx.From $Ctx.To $Now }
    $report.splitOs = Get-Section { Get-SplitRows (& $q 'splitOs') }
    $report.splitMinor = Get-Section { Get-SplitRows (& $q 'splitMinor') }
    $report.funnel = Get-Section {
        $r = Get-Single (& $q 'funnel')
        [ordered]@{
            neverActivated = [int](Get-Value $r 'never')
            startedOnce = [int](Get-Value $r 'startedOnce')
            startedTwicePlus = [int](Get-Value $r 'startedTwicePlus')
            ranOver10Min = [int](Get-Value $r 'ranOver10Min')
            startedSnip = [int](Get-Value $r 'startedSnip')
            allSnipsCancelled = [int](Get-Value $r 'allSnipsCancelled')
            sandboxLike = [int](Get-Value $r 'sandboxLike')
        }
    }
    $report.retention = Get-Section {
        $r = Get-Single (& $q 'retention')
        $activated = [int](Get-Value $r 'activated')
        [ordered]@{
            activated = $activated
            days2Plus = [int](Get-Value $r 'days2Plus')
            days2PlusPct = Get-Percent (Get-Value $r 'days2Plus') $activated
            days5Plus = [int](Get-Value $r 'days5Plus')
            days5PlusPct = Get-Percent (Get-Value $r 'days5Plus') $activated
            span30Plus = [int](Get-Value $r 'span30Plus')
            span30PlusPct = Get-Percent (Get-Value $r 'span30Plus') $activated
        }
    }
    $report.onboarding = Get-Section {
        $shown = Get-Single (& $q 'onboardingShown')
        $actions = @(& $q 'onboardingActions')
        $hotkeys = @(& $q 'hotkeyStatus')
        $first = Get-Single (& $q 'onboardingFirstCaptures')
        $shownEvents = [int](Get-Value $shown 'events')
        $firstEvents = [int](Get-Value $first 'events')
        [ordered]@{
            hasData = ($shownEvents -gt 0 -or $actions.Count -gt 0 -or $hotkeys.Count -gt 0 -or $firstEvents -gt 0)
            shownEvents = $shownEvents
            shownInstalls = [int](Get-Value $shown 'installs')
            actions = @($actions | ForEach-Object { [ordered]@{ action = [string](Get-Value $_ 'action'); events = [int](Get-Value $_ 'events'); installs = [int](Get-Value $_ 'installs') } })
            hotkeyStatus = @($hotkeys | ForEach-Object { [ordered]@{ status = [string](Get-Value $_ 'status'); events = [int](Get-Value $_ 'events'); installs = [int](Get-Value $_ 'installs') } })
            firstCapturesFromOnboarding = $firstEvents
        }
    }
    $report.exceptions = Get-Section {
        $rows = @(& $q 'exceptions')
        $failed = @(& $q 'recordingFailed')
        [ordered]@{
            unhandled = @($rows | ForEach-Object { [ordered]@{ version = [string](Get-Value $_ 'version'); exceptionType = [string](Get-Value $_ 'exceptionType'); events = [int](Get-Value $_ 'events'); installs = [int](Get-Value $_ 'installs') } })
            recordingFailed = @($failed | ForEach-Object { [ordered]@{ version = [string](Get-Value $_ 'version'); phase = [string](Get-Value $_ 'phase'); reason = [string](Get-Value $_ 'reason'); events = [int](Get-Value $_ 'events'); installs = [int](Get-Value $_ 'installs') } })
        }
    }
    $report.website = Get-Section {
        $r = Get-Single (& $q 'website')
        [ordered]@{
            sessions = [int](Get-Value $r 'sessions')
            ctaClicks = [int](Get-Value $r 'ctaClicks')
            ctaSessions = [int](Get-Value $r 'ctaSessions')
            ctaSessionPct = Get-Percent (Get-Value $r 'ctaSessions') (Get-Value $r 'sessions')
        }
    }
    $report
}

# ------------------------------------------------------------------------ text output

function Format-TextTable($Rows, [string[]]$Headers, [scriptblock]$Cells)
{
    $matrix = @(, $Headers)
    foreach ($row in @($Rows))
    {
        $matrix += , @(& $Cells $row | ForEach-Object { "$_" })
    }
    $widths = for ($c = 0; $c -lt $Headers.Count; $c++)
    {
        ($matrix | ForEach-Object { $_[$c].Length } | Measure-Object -Maximum).Maximum
    }
    $lines = foreach ($m in $matrix)
    {
        $padded = for ($c = 0; $c -lt $Headers.Count; $c++)
        {
            if ($c -eq 0) { $m[$c].PadRight($widths[$c]) } else { $m[$c].PadLeft($widths[$c]) }
        }
        '  ' + ($padded -join '  ').TrimEnd()
    }
    $lines -join "`n"
}

function Format-Count($Value)
{
    if ($null -eq $Value) { 'n/a' } else { "$Value" }
}

function Format-ReportText($Report, [bool]$SandboxView)
{
    $sb = [Text.StringBuilder]::new()
    $add = { param($t) [void]$sb.AppendLine($t) }
    $section = {
        param($title, $s, [scriptblock]$render)
        & $add ''
        & $add $title
        if (-not $s.ok)
        {
            & $add "  ERROR: $($s.error)"
        }
        else
        {
            & $add (& $render $s.data)
        }
    }
    & $add "Pointframe usage report, generated $($Report.generatedAt) UTC"
    & $add "Cohort window $($Report.window.from) to $($Report.window.to) (UTC, to exclusive). Excluded: role instance '$($Report.exclusions.roleInstance)', $($Report.exclusions.installIds) install id(s)."

    & $section '1. Totals (lifetime, raw)' $Report.totals {
        param($d)
        $text = "  installs $($d.installs), activated $($d.activated), activation $(Format-Percent $d.activationPct)"
        $text += "`n  sandbox-like (heuristic: lifetime < 2 min, startup events only): $($d.sandboxLike)"
        if ($SandboxView)
        {
            $text += "`n  excluding sandbox-like: installs $($d.installsExSandbox), activation $(Format-Percent $d.activationPctExSandbox)"
        }
        $text
    }
    & $section '2. Active activated installs (any event in the last N days)' $Report.active {
        param($d)
        "  1 day $($d.days1), 7 days $($d.days7), 30 days $($d.days30)"
    }
    & $section '3. Weekly cohorts by first event' $Report.cohorts {
        param($d)
        if (@($d).Count -eq 0) { return '  no installs first seen in the window' }
        $headers = @('week of', 'size', 'sandbox', 'act 24h', 'act 7d', 'act 7d %')
        if ($SandboxView) { $headers += 'act 7d % ex-sandbox' }
        $table = Format-TextTable $d $headers {
            param($c)
            $cells = @($c.weekStart, $c.size, $c.sandboxLike, (Format-Count $c.activated24h), (Format-Count $c.activated7d), $(if ($c.immature) { 'immature' } else { Format-Percent $c.activation7dPct }))
            if ($SandboxView) { $cells += $(if ($c.immature) { 'immature' } else { Format-Percent $c.activation7dPctExSandbox }) }
            $cells
        }
        $table + "`n  immature = fewer than 7 days since the cohort's last day; 7-day activation is not final."
    }
    foreach ($pair in @(@('4a. 7-day activation by OS (mature installs; Win11 = build >= 22000)', $Report.splitOs), @('4b. 7-day activation by minor version (first seen version)', $Report.splitMinor)))
    {
        & $section $pair[0] $pair[1] {
            param($d)
            if (@($d).Count -eq 0) { return '  no data' }
            $headers = @('key', 'installs', 'sandbox', 'act 7d', 'act 7d %')
            if ($SandboxView) { $headers += 'ex-sandbox %' }
            Format-TextTable $d $headers {
                param($r)
                $cells = @($r.key, $r.size, $r.sandboxLike, $r.activated7d, (Format-Percent $r.activation7dPct))
                if ($SandboxView) { $cells += (Format-Percent $r.activation7dPctExSandbox) }
                $cells
            }
        }
    }
    & $section '5. Funnel for never-activated installs' $Report.funnel {
        param($d)
        $rows = @(
            @('never activated', $d.neverActivated), @('started once or more', $d.startedOnce), @('started 2+ times', $d.startedTwicePlus),
            @('ran longer than 10 min', $d.ranOver10Min), @('started a snip', $d.startedSnip), @('all snips cancelled', $d.allSnipsCancelled),
            @('(sandbox-like, heuristic)', $d.sandboxLike))
        Format-TextTable $rows @('step', 'installs') { param($r) $r }
    }
    & $section '6. Retention of activated installs' $Report.retention {
        param($d)
        $rows = @(
            @('activated', $d.activated, ''), @('used on 2+ days', $d.days2Plus, (Format-Percent $d.days2PlusPct)),
            @('used on 5+ days', $d.days5Plus, (Format-Percent $d.days5PlusPct)), @('active span >= 30 days', $d.span30Plus, (Format-Percent $d.span30PlusPct)))
        Format-TextTable $rows @('measure', 'installs', '%') { param($r) $r }
    }
    & $section '7. Onboarding' $Report.onboarding {
        param($d)
        if (-not $d.hasData) { return '  no data yet' }
        $text = "  onboarding_shown: $($d.shownEvents) events, $($d.shownInstalls) installs"
        $text += "`n  first captures with source=onboarding: $($d.firstCapturesFromOnboarding)"
        $text += "`n  onboarding_action by action:`n" + $(if (@($d.actions).Count -eq 0) { '  no data yet' } else { Format-TextTable $d.actions @('action', 'events', 'installs') { param($r) @($r.action, $r.events, $r.installs) } })
        $text += "`n  hotkey_status by status:`n" + $(if (@($d.hotkeyStatus).Count -eq 0) { '  no data yet' } else { Format-TextTable $d.hotkeyStatus @('status', 'events', 'installs') { param($r) @($r.status, $r.events, $r.installs) } })
        $text
    }
    & $section '8. Failures (last 30 days)' $Report.exceptions {
        param($d)
        $text = '  unhandled_exception by version and type:'
        $text += "`n" + $(if (@($d.unhandled).Count -eq 0) { '  none' } else { Format-TextTable $d.unhandled @('version', 'type', 'events', 'installs') { param($r) @($r.version, $r.exceptionType, $r.events, $r.installs) } })
        $text += "`n  recording_failed by version, phase, and reason:"
        $text += "`n" + $(if (@($d.recordingFailed).Count -eq 0) { '  no data yet' } else { Format-TextTable $d.recordingFailed @('version', 'phase', 'reason', 'events', 'installs') { param($r) @($r.version, $r.phase, $r.reason, $r.events, $r.installs) } })
        $text
    }
    & $section '9. Website (last 90 days)' $Report.website {
        param($d)
        "  sessions $($d.sessions), CTA clicks $($d.ctaClicks), sessions with a CTA click $($d.ctaSessions) ($(Format-Percent $d.ctaSessionPct))"
    }
    $sb.ToString().TrimEnd()
}

# ---------------------------------------------------------------------------- self-test

function Invoke-SelfTest
{
    $fixtureDir = Join-Path $PSScriptRoot 'tests/usage-report'
    $failures = [Collections.Generic.List[string]]::new()
    $counter = @{ n = 0 }
    $check = {
        param([bool]$Condition, [string]$Message)
        $counter.n++
        if (-not $Condition) { $failures.Add($Message) }
    }
    $utc = { param($y, $m, $d, $h, $mi, $s) [datetime]::new($y, $m, $d, $h, $mi, $s, [DateTimeKind]::Utc) }

    $now = & $utc 2026 10 5 12 0 0
    $window = Resolve-Window '' '' $now
    $ctx = @{ From = $window.From; To = $window.To; ExcludeRoleInstance = 'HOST-1'; ExcludeInstallId = @('abc123', 'def456') }

    # UTC boundaries
    & $check ($window.To -eq (& $utc 2026 10 5 0 0 0)) 'default To is Monday 2026-10-05 UTC'
    & $check ($window.From -eq (& $utc 2026 8 10 0 0 0)) 'default From is 8 weeks earlier'
    $sundayLate = Resolve-Window '' '' (& $utc 2026 10 4 23 59 59)
    & $check ($sundayLate.To -eq (& $utc 2026 9 28 0 0 0)) 'Sunday 23:59:59 UTC still belongs to the week that began 2026-09-28'
    $explicit = Resolve-Window '2026-08-10' '2026-09-07T12:00:00+02:00' $now
    & $check ($explicit.To -eq (& $utc 2026 9 7 10 0 0)) 'explicit To with an offset converts to UTC'
    $threw = $false
    try { [void](Resolve-Window '2026-10-05' '2026-10-05' $now) } catch { $threw = $true }
    & $check $threw 'From must be earlier than To'
    & $check ((ConvertTo-KqlDatetime $window.From) -eq 'datetime(2026-08-10T00:00:00Z)') 'KQL datetime literal is UTC'

    # KQL builders
    foreach ($name in $QueryBuilders.Keys)
    {
        $kql = & $QueryBuilders[$name] $ctx
        $open = ($kql.ToCharArray() | Where-Object { $_ -eq '(' }).Count
        $close = ($kql.ToCharArray() | Where-Object { $_ -eq ')' }).Count
        $quotes = ($kql.ToCharArray() | Where-Object { $_ -eq '"' }).Count
        & $check ($kql -notmatch '\{\{|\}\}') "$name leaves no unexpanded placeholder"
        & $check ($open -eq $close) "$name has balanced parentheses"
        & $check (($quotes % 2) -eq 0) "$name has balanced quotes"
        & $check ($kql -notmatch '\b(first|real)\s*=') "$name does not name a column with a KQL reserved word"
        if ($name -ne 'website')
        {
            & $check ($kql.Contains('"abc123", "def456"') -and $kql.Contains('"HOST-1"')) "$name embeds both exclusions"
            & $check ($kql.Contains('name !startswith "website"')) "$name keeps website events out of app queries"
        }
    }
    & $check ((& $QueryBuilders['cohorts'] $ctx).Contains('datetime(2026-08-10T00:00:00Z)')) 'cohorts query uses the window start'
    $bad = $false
    try { [void](Expand-Kql '{{EXCLUDE_IDS}}' @{ ExcludeInstallId = @('x" | drop'); ExcludeRoleInstance = 'a'; From = $window.From; To = $window.To }) } catch { $bad = $true }
    & $check $bad 'an install id with quotes is rejected'
    $noRole = Expand-Kql '{{EXCLUDE_IDS}}|{{EXCLUDE_ROLE}}' @{ ExcludeInstallId = @(); ExcludeRoleInstance = ''; From = $window.From; To = $window.To }
    & $check ($noRole -eq '""|""') 'no exclusions expand to empty literals'

    # Shaping from fixtures
    $load = {
        param($name)
        $path = Join-Path $fixtureDir "$name.json"
        if (-not (Test-Path -LiteralPath $path)) { return [pscustomobject]@{ tables = @() } }
        Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    }
    $runner = { param($name, $kql) & $load $name }
    $report = New-Report $runner $ctx $now
    & $check ($report.totals.ok -and $report.totals.data.installs -eq 706 -and $report.totals.data.activated -eq 116) 'totals read from fixture'
    & $check ($report.totals.data.activationPct -eq 16.4) 'activation rate rounds to 16.4'
    & $check ($report.totals.data.installsExSandbox -eq 656 -and $report.totals.data.activationPctExSandbox -eq 17.7) 'sandbox view removes the sandbox-like count'
    & $check ($report.active.data.days1 -eq 13 -and $report.active.data.days7 -eq 33 -and $report.active.data.days30 -eq 54) 'active 1/7/30 read from fixture'

    $cohorts = @($report.cohorts.data)
    & $check ($cohorts.Count -eq 3) 'three cohort rows'
    & $check ($cohorts[0].weekStart -eq '2026-08-10' -and $cohorts[1].weekStart -eq '2026-08-17') 'week starts are From + 7 days per index'
    & $check ($cohorts[0].activation7dPct -eq 20.0 -and -not $cohorts[0].immature) 'old cohort shows 7-day activation'
    & $check ($cohorts[2].weekStart -eq '2026-09-28' -and $cohorts[2].immature -and $null -eq $cohorts[2].activated7d -and $null -eq $cohorts[2].activation7dPct) 'week of 2026-09-28 is immature on 2026-10-05 and withholds 7-day numbers'
    & $check ($null -eq $cohorts[2].activated24h -and $cohorts[1].activated24h -eq 10) '24 h activation is withheld until 24 h after the cohort ends'
    $row = @([pscustomobject]@{ weekIndex = 0; size = 10; sandboxLike = 0; activated24h = 1; activated7d = 2; sandboxActivated24h = 0 })
    $weekEnd = $window.From.AddDays(7)
    $edge = Get-CohortRows $row $window.From $weekEnd (& $utc 2026 8 24 0 0 0)
    & $check (-not $edge[0].immature) 'a cohort whose last day ended exactly 7 days ago is mature'
    $edge2 = Get-CohortRows $row $window.From $weekEnd (& $utc 2026 8 23 23 59 59)
    & $check ($edge2[0].immature) 'one second earlier it is still immature'

    & $check (@($report.splitOs.data).Count -eq 2 -and $report.splitOs.data[0].key -eq 'Win10' -and $report.splitOs.data[1].activation7dPct -eq 25.0) 'OS split'
    & $check ($report.splitMinor.data[0].key -eq '6.7') 'minor split'
    & $check ($report.funnel.data.neverActivated -eq 590 -and $report.funnel.data.allSnipsCancelled -eq 12) 'funnel'
    & $check ($report.retention.data.days2PlusPct -eq 50.0 -and $report.retention.data.span30Plus -eq 20) 'retention'
    & $check ($report.onboarding.ok -and -not $report.onboarding.data.hasData) 'absent onboarding events mean no data yet'
    & $check ($report.exceptions.data.unhandled[0].exceptionType -eq 'AggregateException' -and @($report.exceptions.data.recordingFailed).Count -eq 0) 'exceptions, and no recording_failed'
    & $check ($report.website.data.sessions -eq 145 -and $report.website.data.ctaSessions -eq 26) 'website'

    $text = Format-ReportText $report $true
    & $check ($text -match 'no data yet') 'text says "no data yet" for onboarding'
    & $check ($text -match 'immature') 'text labels the immature cohort'
    & $check ($text -match 'excluding sandbox-like') 'text shows the sandbox view when asked'
    & $check ((Format-ReportText $report $false) -notmatch 'excluding sandbox-like') 'sandbox view is off by default'
    $roundTrip = $report | ConvertTo-Json -Depth 8 | ConvertFrom-Json
    & $check ($roundTrip.totals.data.installs -eq 706) 'report serializes to JSON'

    # Empty results: every section degrades to zeros or "no data", never an error
    $emptyRunner = { param($n, $k) [pscustomobject]@{ tables = @([pscustomobject]@{ name = 'PrimaryResult'; columns = @(); rows = @() }) } }
    $empty = New-Report $emptyRunner $ctx $now
    $broken = @($empty.Keys | Where-Object { $empty[$_] -is [hashtable] -and -not $empty[$_].ok })
    & $check ($broken.Count -eq 0) 'empty tables produce no section errors'
    & $check ($empty.totals.data.installs -eq 0 -and $null -eq $empty.totals.data.activationPct) 'empty totals have no percentage'
    & $check (@($empty.cohorts.data).Count -eq 0) 'empty cohorts'
    & $check ((Format-ReportText $empty $false) -match 'no installs first seen in the window') 'text explains an empty cohort window'

    # API error: one failing query is reported in its section and the rest still run
    $errorRunner = {
        param($name, $kql)
        if ($name -eq 'funnel') { return (& $load 'error') }
        & $load $name
    }
    $withError = New-Report $errorRunner $ctx $now
    & $check (-not $withError.funnel.ok -and $withError.funnel.error -match 'BadArgumentError' -and $withError.funnel.error -match 'Failed to resolve') 'API error becomes a section error with the API message'
    & $check ($withError.totals.ok -and $withError.retention.ok) 'other sections still succeed'
    & $check ((Format-ReportText $withError $false) -match 'ERROR: API error BadArgumentError') 'text shows the section error'

    foreach ($f in $failures) { Write-Host "FAIL: $f" }
    if ($failures.Count -gt 0)
    {
        Write-Host "usage-report self-test FAILED: $($failures.Count) of $($counter.n) checks."
        exit 1
    }
    Write-Host "usage-report self-test passed: $($counter.n) checks."
    exit 0
}

# ------------------------------------------------------------------------------- main

if ($SelfTest)
{
    Invoke-SelfTest
}

if (-not (Get-Command az -ErrorAction SilentlyContinue))
{
    Write-Host "az CLI not found. Install it, then run: $LoginHint"
    exit 2
}
$null = & az account show 2>&1
if ($LASTEXITCODE -ne 0)
{
    Write-Host "az is not logged in. Run: $LoginHint"
    exit 2
}

try
{
    $now = [datetime]::UtcNow
    $window = Resolve-Window $From $To $now
    $ctx = @{ From = $window.From; To = $window.To; ExcludeRoleInstance = $ExcludeRoleInstance; ExcludeInstallId = @($ExcludeInstallId) }
    foreach ($name in $QueryBuilders.Keys) { [void](& $QueryBuilders[$name] $ctx) }
}
catch
{
    Write-Host "Bad arguments: $($_.Exception.Message)"
    exit 2
}

$appIdValue = $AppId
$runner = { param($name, $kql) Invoke-AppInsightsQuery $appIdValue $kql }
$report = New-Report $runner $ctx $now
$output = if ($Json) { $report | ConvertTo-Json -Depth 8 } else { Format-ReportText $report $SandboxView.IsPresent }
if ($OutFile)
{
    [IO.File]::WriteAllText([IO.Path]::GetFullPath($OutFile, (Get-Location).Path), $output + "`n", [Text.UTF8Encoding]::new($false))
}
Write-Output $output
$failed = @($report.Keys | Where-Object { $report[$_] -is [hashtable] -and -not $report[$_].ok })
exit $(if ($failed.Count -gt 0) { 1 } else { 0 })
