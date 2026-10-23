[CmdletBinding()]
param(
    [ValidateRange(1, 20)][int]$Repeat = 3,
    [string]$StateDirectory,
    [switch]$DryRun,
    [switch]$NoIssue,
    [switch]$SelfTest,
    [string]$Analyze,
    [string]$PromoteBaseline
)

# Weekly cold-agent canary: does a fresh coding agent still verify the WPF sample from the public README?
# Run it locally at the Monday retro. It resolves the newest agent release and the origin/master SHA once, runs
# scripts/trial.ps1 (wpf-sample, readme entry, FreshMachine) Repeat times, classifies every run, compares the batch
# with the retained known-good baseline, and on a clean-pass loss opens or updates one GitHub issue.
#
#   pwsh scripts/weekly-trial.ps1 [-Repeat 3] [-StateDirectory <dir>] [-NoIssue]
#   pwsh scripts/weekly-trial.ps1 -DryRun                     # resolve and print the plan, run nothing, write nothing
#   pwsh scripts/weekly-trial.ps1 -Analyze <trial output>     # score an existing folder against the baseline
#   pwsh scripts/weekly-trial.ps1 -PromoteBaseline <folder>   # explicit: write baseline.json from an all pass-clean run
#   pwsh scripts/weekly-trial.ps1 -SelfTest                   # offline, fixtures in scripts/tests/weekly-trial
#
# Status: ok, warning (median toolCalls or totalInputTokens more than 30% above the baseline), triage (fewer clean
# passes than the baseline), environment (invalid or missing runs explain the shortfall; harness trouble, not a product
# regression), noBaseline, incomparable. Exit codes: 0 ok/warning/noBaseline/incomparable, 1 triage, 2 environment.
# The baseline is never written except by -PromoteBaseline. Real runs hide installs and take over the desktop for
# 10-15 minutes; the coordinator runs them, not an executor.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

. (Join-Path $PSScriptRoot 'agent-release.ps1')

$script:RepositoryRoot = Split-Path $PSScriptRoot -Parent
$script:FixtureDirectory = Join-Path $PSScriptRoot 'tests/weekly-trial'
$script:ReleaseRepository = 'dimitar-radenkov/Pointframe'
$script:ReleasesFile = $null # a releases fixture for the self-test; $null reads GitHub through gh
$script:IssueLabel = 'weekly-trial'
$script:OverdueDays = 8
$script:RegressionThreshold = 0.30
$script:OutcomeClasses = @('pass-clean', 'pass-workaround', 'fail', 'timeout', 'invalid')
$script:TrialApp = 'wpf-sample'

# Injectable seams: the self-test replaces these so nothing touches gh, git, the agent CLI or a real trial.
$script:Gh = {
    param([string[]]$Arguments)
    $output = @(& gh @Arguments 2>&1 | ForEach-Object { "$_" })
    [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $output }
}
$script:Resolvers = @{
    Version = {
        # The newest complete agent release; GitHub Latest is a desktop release from the release trains on.
        try { Resolve-NewestAgentVersion -Repository $script:ReleaseRepository -ReleasesFile $script:ReleasesFile }
        catch { throw "Could not resolve the newest agent release: $($_.Exception.Message)" }
    }
    DocsRef = {
        param([bool]$Fetch)
        if ($Fetch)
        {
            & git -C $script:RepositoryRoot fetch origin master --quiet 2>&1 | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'git fetch origin master failed.' }
            $sha = (& git -C $script:RepositoryRoot rev-parse --verify 'origin/master^{commit}').Trim()
        }
        else
        {
            $line = (& git -C $script:RepositoryRoot ls-remote origin refs/heads/master) | Select-Object -First 1
            if ($LASTEXITCODE -ne 0 -or -not $line) { throw 'git ls-remote origin master failed.' }
            $sha = ("$line" -split '\s+')[0]
        }
        if ($sha -notmatch '^[0-9a-f]{40}$') { throw "Unexpected docs ref '$sha'." }
        $sha
    }
    AgentVersion = {
        $text = (& claude --version 2>&1 | Out-String).Trim()
        if ($LASTEXITCODE -ne 0) { return 'unknown' }
        $text
    }
    HarnessSha = {
        $sha = (& git -C $script:RepositoryRoot rev-parse HEAD 2>$null)
        if ($LASTEXITCODE -ne 0) { 'unknown' } else { "$sha".Trim() }
    }
}
$script:TrialRunner = {
    param([string[]]$Arguments)
    Push-Location $script:RepositoryRoot
    try
    {
        $log = Join-Path ($Arguments[[array]::IndexOf($Arguments, '-OutDirectory') + 1]) 'trial.log'
        & pwsh -NoProfile -NonInteractive -File (Join-Path $PSScriptRoot 'trial.ps1') @Arguments *> $log
        [int]$LASTEXITCODE
    }
    finally
    {
        Pop-Location
    }
}

# ---------------------------------------------------------------------------------------------------------------
# Reading and classifying runs

function Get-Prop
{
    param($Object, [Parameter(Mandatory)][string]$Name, $Default = $null)
    if ($null -ne $Object -and $Object.PSObject.Properties[$Name]) { return $Object.$Name }
    $Default
}

function Get-OutcomeClass
{
    param([string]$Raw, [bool]$TimedOut = $false)
    if ($Raw -in $script:OutcomeClasses) { return $Raw }
    if ($TimedOut) { return 'timeout' }
    'invalid'
}

function Get-TranscriptModel
{
    param([string]$Path)
    if (-not $Path -or -not (Test-Path -LiteralPath $Path)) { return $null }
    $first = Get-Content -LiteralPath $Path -TotalCount 1
    if ($first -match '"model"\s*:\s*"([^"]+)"') { return $Matches[1] }
    $null
}

function Read-RunRecord
{
    param([Parameter(Mandatory)][string]$Folder)
    $path = Join-Path $Folder 'result.json'
    $record = [ordered]@{
        Folder = $Folder; Outcome = 'invalid'; RawOutcome = $null; App = $null; Task = $null; Entry = $null; Agent = $null; Model = $null
        Version = $null; DocsRef = $null; ToolCalls = $null; TotalInputTokens = $null; Minutes = $null; DocsWebFetchViolation = $false; Note = $null
    }
    if (-not (Test-Path -LiteralPath $path))
    {
        $record.Note = 'no result.json (the harness stopped before scoring)'
        return [pscustomobject]$record
    }
    try
    {
        $result = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    }
    catch
    {
        $record.Note = "result.json is not valid JSON: $($_.Exception.Message)"
        return [pscustomobject]$record
    }

    $metrics = Get-Prop $result 'metrics'
    $agentRun = Get-Prop $result 'agentRun'
    $record.RawOutcome = Get-Prop $result 'outcome'
    $record.Outcome = Get-OutcomeClass ([string]$record.RawOutcome) ([bool](Get-Prop $agentRun 'timedOut' $false))
    $record.App = Get-Prop $result 'app'
    $record.Task = Get-Prop $result 'task'
    $record.Entry = Get-Prop $result 'entry'
    $record.Agent = Get-Prop $result 'agent'
    $model = Get-Prop $result 'agentModel'
    if (-not $model) { $model = Get-TranscriptModel (Get-Prop $result 'transcript') }
    $record.Model = $model
    $record.Version = Get-Prop $result 'expectedVersion'
    $record.DocsRef = Get-Prop $result 'docsCommit'
    $record.ToolCalls = Get-Prop $metrics 'toolCalls'
    $record.TotalInputTokens = Get-Prop $metrics 'totalInputTokens'
    $record.Minutes = Get-Prop $metrics 'minutes'
    $record.DocsWebFetchViolation = [bool](Get-Prop $result 'docsWebFetchViolation' $false)
    if ($record.RawOutcome -and $record.Outcome -eq 'invalid' -and $record.RawOutcome -ne 'invalid')
    {
        $record.Note = "unknown outcome '$($record.RawOutcome)'"
    }
    [pscustomobject]$record
}

function Get-RunRecords
{
    param([Parameter(Mandatory)][string]$Folder)
    if (-not (Test-Path -LiteralPath $Folder -PathType Container)) { throw "Trial folder not found: $Folder" }
    $records = [System.Collections.Generic.List[object]]::new()
    if (Test-Path -LiteralPath (Join-Path $Folder 'result.json'))
    {
        $records.Add((Read-RunRecord $Folder))
    }
    foreach ($child in Get-ChildItem -LiteralPath $Folder -Directory | Where-Object { $_.Name -match '^\d+$' } | Sort-Object Name)
    {
        $records.Add((Read-RunRecord $child.FullName))
    }
    , $records.ToArray()
}

# ---------------------------------------------------------------------------------------------------------------
# Comparison with the baseline

function Get-Median
{
    param([double[]]$Values)
    $sorted = @($Values | Sort-Object)
    if ($sorted.Count -eq 0) { return $null }
    $mid = [int][math]::Floor($sorted.Count / 2)
    if ($sorted.Count % 2 -eq 1) { return [double]$sorted[$mid] }
    ([double]$sorted[$mid - 1] + [double]$sorted[$mid]) / 2
}

function Test-Comparable
{
    param($Record, $Baseline)
    foreach ($field in 'App', 'Task', 'Entry', 'Model')
    {
        $left = "$(Get-Prop $Record $field)"
        $right = "$(Get-Prop $Baseline $field.ToLowerInvariant())"
        if ($left -ne $right) { return $false }
    }
    $true
}

function New-BaselineRecord
{
    param([Parameter(Mandatory)][object[]]$Records, [string]$SourceFolder, [datetime]$Now = [datetime]::UtcNow)
    if ($Records.Count -eq 0) { throw 'No runs to promote.' }
    $bad = @($Records | Where-Object { $_.Outcome -ne 'pass-clean' })
    if ($bad.Count -gt 0)
    {
        throw "Only an all-pass-clean run can become the baseline; $($bad.Count) of $($Records.Count) run(s) are not ($((@($bad | ForEach-Object { $_.Outcome }) -join ', ')))."
    }
    $first = $Records[0]
    foreach ($field in 'App', 'Task', 'Entry', 'Model', 'Version', 'DocsRef')
    {
        if (@($Records | Where-Object { -not (Get-Prop $_ $field) }).Count -gt 0) { throw "A baseline needs a non-empty $field on every run." }
        $distinct = @($Records | ForEach-Object { "$(Get-Prop $_ $field)" } | Select-Object -Unique)
        if ($distinct.Count -gt 1) { throw "The runs disagree on $field ($($distinct -join ', ')); a baseline needs comparable runs." }
    }
    [ordered]@{
        schemaVersion = 1
        promotedUtc = $Now.ToString('o')
        sourceFolder = $SourceFolder
        app = $first.App
        task = $first.Task
        entry = $first.Entry
        agent = $first.Agent
        model = $first.Model
        version = $first.Version
        docsRef = $first.DocsRef
        runCount = $Records.Count
        cleanCount = $Records.Count
        medianToolCalls = Get-Median ([double[]]@($Records | ForEach-Object { $_.ToolCalls } | Where-Object { $null -ne $_ }))
        medianTotalInputTokens = Get-Median ([double[]]@($Records | ForEach-Object { $_.TotalInputTokens } | Where-Object { $null -ne $_ }))
        medianMinutes = Get-Median ([double[]]@($Records | ForEach-Object { $_.Minutes } | Where-Object { $null -ne $_ }))
    }
}

function Compare-ToBaseline
{
    param([Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Records, $Baseline, [double]$Threshold = $script:RegressionThreshold, [int]$Expected = 0)

    $counts = [ordered]@{}
    foreach ($class in $script:OutcomeClasses) { $counts[$class] = @($Records | Where-Object { $_.Outcome -eq $class }).Count }
    $result = [ordered]@{
        status = 'ok'; reasons = @(); counts = $counts; cleanCount = $counts['pass-clean']; baselineCleanCount = $null
        medianToolCalls = $null; medianTotalInputTokens = $null; baselineMedianToolCalls = $null; baselineMedianTotalInputTokens = $null
        toolCallsRatio = $null; totalInputTokensRatio = $null; docsWebFetchViolations = @($Records | Where-Object { $_.DocsWebFetchViolation }).Count
    }
    $reasons = [System.Collections.Generic.List[string]]::new()

    if ($Records.Count -eq 0)
    {
        $result.status = 'environment'
        $reasons.Add('No runs were scored: harness or environment trouble, not a product regression.')
        $result.reasons = $reasons.ToArray()
        return [pscustomobject]$result
    }

    if ($null -eq $Baseline)
    {
        $result.status = 'noBaseline'
        $reasons.Add('No baseline.json: nothing to compare with. Promote a known-good all-pass-clean run with -PromoteBaseline.')
        if ($counts['invalid'] -gt 0) { $reasons.Add("$($counts['invalid']) invalid run(s): harness or environment trouble.") }
        $result.reasons = $reasons.ToArray()
        return [pscustomobject]$result
    }

    $comparable = @($Records | Where-Object { Test-Comparable $_ $Baseline })
    $baselineClean = [int](Get-Prop $Baseline 'cleanCount' 0)
    $result.baselineCleanCount = $baselineClean
    $clean = $counts['pass-clean']
    $scoredFailures = $counts['fail'] + $counts['timeout'] + $counts['pass-workaround']
    $missing = [math]::Max(0, $Expected - $Records.Count)
    $noIdentity = @($Records | Where-Object { $_.Outcome -ne 'invalid' -and (-not $_.App -or -not $_.Task -or -not $_.Entry -or -not $_.Model) }).Count
    $unscored = $counts['invalid'] + $missing + $noIdentity
    $status = 'ok'

    if ($clean -lt $baselineClean)
    {
        if ($scoredFailures -gt 0)
        {
            $status = 'triage'
            $reasons.Add("Clean passes $clean < baseline $baselineClean (fail $($counts['fail']), timeout $($counts['timeout']), pass-workaround $($counts['pass-workaround']), invalid $($counts['invalid']), missing $missing).")
        }
        else
        {
            $status = 'environment'
            if ($clean + $unscored -ge $baselineClean)
            {
                $reasons.Add("Clean passes $clean < baseline $baselineClean, and the whole shortfall comes from $unscored invalid, missing or unidentified run(s): harness or environment trouble, not a product regression.")
            }
            else
            {
                $reasons.Add("Clean passes $clean < baseline $baselineClean with no scored failure and only $($Records.Count) run(s) (expected $(if ($Expected -gt 0) { $Expected } else { 'at least ' + $baselineClean })): fewer repetitions than the baseline needs, a configuration or harness problem.")
            }
        }
    }
    if ($unscored -gt 0)
    {
        $reasons.Add("$unscored run(s) are invalid, missing or lack identity fields (app, task, entry, model): reported as harness or environment trouble, not a product regression.")
    }

    $cleanComparable = @($comparable | Where-Object { $_.Outcome -eq 'pass-clean' })
    if ($cleanComparable.Count -gt 0)
    {
        $result.medianToolCalls = Get-Median ([double[]]@($cleanComparable | ForEach-Object { $_.ToolCalls } | Where-Object { $null -ne $_ }))
        $result.medianTotalInputTokens = Get-Median ([double[]]@($cleanComparable | ForEach-Object { $_.TotalInputTokens } | Where-Object { $null -ne $_ }))
        $result.baselineMedianToolCalls = Get-Prop $Baseline 'medianToolCalls'
        $result.baselineMedianTotalInputTokens = Get-Prop $Baseline 'medianTotalInputTokens'
        $warned = $false
        foreach ($pair in @(@('toolCalls', 'medianToolCalls', 'baselineMedianToolCalls', 'toolCallsRatio'), @('totalInputTokens', 'medianTotalInputTokens', 'baselineMedianTotalInputTokens', 'totalInputTokensRatio')))
        {
            $current = $result[$pair[1]]
            $base = $result[$pair[2]]
            if ($null -eq $current -or $null -eq $base -or [double]$base -le 0) { continue }
            $ratio = [double]$current / [double]$base
            $result[$pair[3]] = [math]::Round($ratio, 3)
            if ([math]::Round($ratio - 1, 6) -gt $Threshold)
            {
                $warned = $true
                $reasons.Add(("Median {0} {1} is {2:P0} above the baseline {3} (limit {4:P0})." -f $pair[0], $current, ($ratio - 1), $base, $Threshold))
            }
        }
        if ($warned -and $status -eq 'ok') { $status = 'warning' }
    }
    if ($comparable.Count -eq 0 -and $status -eq 'ok')
    {
        $status = 'incomparable'
        $reasons.Add("No run matches the baseline's app, task, entry and model ($($Baseline.app), $($Baseline.task), $($Baseline.entry), $($Baseline.model)); clean-pass count compared, cost not compared.")
    }
    elseif ($comparable.Count -lt $Records.Count)
    {
        $reasons.Add("$($Records.Count - $comparable.Count) run(s) are not comparable with the baseline (different app, task, entry or model) and were left out of the cost comparison.")
    }

    $result.status = $status
    $result.reasons = $reasons.ToArray()
    [pscustomobject]$result
}

# ---------------------------------------------------------------------------------------------------------------
# State, overdue, summary

function Get-Overdue
{
    param($LastRun, [datetime]$Now = [datetime]::UtcNow, [int]$Days = $script:OverdueDays)
    $date = $null
    if ($LastRun -and (Get-Prop $LastRun 'date'))
    {
        $parsed = [datetimeoffset]::MinValue
        if ([datetimeoffset]::TryParse([string]$LastRun.date, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::AssumeUniversal, [ref]$parsed)) { $date = $parsed.UtcDateTime }
    }
    if ($null -eq $date)
    {
        return [pscustomobject]@{ Overdue = $true; DaysSince = $null; Message = 'OVERDUE: no completed weekly trial is recorded.' }
    }
    $elapsed = ($Now.ToUniversalTime() - $date).TotalDays
    $since = [math]::Floor($elapsed)
    if ($elapsed -gt $Days)
    {
        return [pscustomobject]@{ Overdue = $true; DaysSince = [int]$since; Message = "OVERDUE: the last completed weekly trial was $since days ago ($($date.ToString('yyyy-MM-dd'))); the limit is $Days." }
    }
    [pscustomobject]@{ Overdue = $false; DaysSince = [int]$since; Message = "On schedule: the last completed weekly trial was $since day(s) ago ($($date.ToString('yyyy-MM-dd')))." }
}

function Read-JsonFile
{
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
}

function Write-JsonFile
{
    param([string]$Path, $Value)
    $directory = Split-Path $Path -Parent
    if ($directory -and -not (Test-Path -LiteralPath $directory)) { New-Item -ItemType Directory -Path $directory -Force | Out-Null }
    $temp = "$Path.tmp"
    ($Value | ConvertTo-Json -Depth 8) | Set-Content -LiteralPath $temp -Encoding utf8
    Move-Item -LiteralPath $temp -Destination $Path -Force
}

function Format-RunTable
{
    param([object[]]$Records)
    $lines = @('| run | outcome | toolCalls | totalInputTokens | minutes | note |', '|---|---|---|---|---|---|')
    foreach ($record in $Records)
    {
        $name = Split-Path $record.Folder -Leaf
        $note = if ($record.Note) { $record.Note } elseif ($record.DocsWebFetchViolation) { 'docs web fetch violation' } else { '' }
        $lines += "| $name | $($record.Outcome) | $($record.ToolCalls) | $($record.TotalInputTokens) | $($record.Minutes) | $note |"
    }
    $lines
}

function Format-Summary
{
    param([Parameter(Mandatory)]$Weekly)
    $c = $Weekly.comparison
    $lines = @(
        "# Weekly trial $($Weekly.date): $($c.status)",
        '',
        "- Release: $($Weekly.inputs.version), docs ref: $($Weekly.inputs.docsRef)",
        "- Agent: $($Weekly.environment.agentVersion), model $($Weekly.environment.model), harness $($Weekly.environment.harnessSha)",
        "- Runs: pass-clean $($c.counts.'pass-clean'), pass-workaround $($c.counts.'pass-workaround'), fail $($c.counts.'fail'), timeout $($c.counts.'timeout'), invalid $($c.counts.'invalid')",
        "- Baseline clean passes: $(if ($null -ne $c.baselineCleanCount) { $c.baselineCleanCount } else { 'none' })",
        ''
    )
    $lines += @($c.reasons | ForEach-Object { "- $_" })
    $lines += ''
    $lines += Format-RunTable @($Weekly.runs | ForEach-Object { [pscustomobject]@{ Folder = $_.folder; Outcome = $_.outcome; ToolCalls = $_.toolCalls; TotalInputTokens = $_.totalInputTokens; Minutes = $_.minutes; Note = $_.note; DocsWebFetchViolation = $_.docsWebFetchViolation } })
    $lines += ''
    $lines += "Output: $($Weekly.outDirectory)"
    $lines += "Environment note: $($Weekly.environment.freshMachineNote)"
    $lines -join "`n"
}

# ---------------------------------------------------------------------------------------------------------------
# GitHub issue (one open issue per label, never a duplicate)

function Get-IssueDecision
{
    param([object[]]$OpenIssues = @())
    $open = @($OpenIssues | Where-Object { $null -ne $_ } | Sort-Object { [int]$_.number })
    if ($open.Count -gt 0) { return [pscustomobject]@{ Action = 'comment'; Number = [int]$open[0].number } }
    [pscustomobject]@{ Action = 'create'; Number = $null }
}

function Update-TriageIssue
{
    param([Parameter(Mandatory)][string]$Title, [Parameter(Mandatory)][string]$Body)
    $list = & $script:Gh @('issue', 'list', '--repo', $script:ReleaseRepository, '--label', $script:IssueLabel, '--state', 'open', '--json', 'number,title', '--limit', '20')
    if ($list.ExitCode -ne 0) { throw "Could not list open $($script:IssueLabel) issues, so none was created or updated: $($list.Output -join ' ')" }
    $open = @((($list.Output -join "`n") | ConvertFrom-Json))
    $decision = Get-IssueDecision $open
    $bodyFile = Join-Path ([IO.Path]::GetTempPath()) "weekly-trial-$([guid]::NewGuid().ToString('N')).md"
    try
    {
        Set-Content -LiteralPath $bodyFile -Value $Body -Encoding utf8
        if ($decision.Action -eq 'comment')
        {
            $run = & $script:Gh @('issue', 'comment', "$($decision.Number)", '--repo', $script:ReleaseRepository, '--body-file', $bodyFile)
            if ($run.ExitCode -ne 0) { throw "gh issue comment failed: $($run.Output -join ' ')" }
            return "commented on issue #$($decision.Number)"
        }
        $null = & $script:Gh @('label', 'create', $script:IssueLabel, '--repo', $script:ReleaseRepository, '--force', '--description', 'Weekly cold-agent trial regressions', '--color', 'b60205')
        $run = & $script:Gh @('issue', 'create', '--repo', $script:ReleaseRepository, '--title', $Title, '--body-file', $bodyFile, '--label', $script:IssueLabel)
        if ($run.ExitCode -ne 0) { throw "gh issue create failed: $($run.Output -join ' ')" }
        "created issue: $($run.Output -join ' ')"
    }
    finally
    {
        Remove-Item -LiteralPath $bodyFile -Force -ErrorAction SilentlyContinue
    }
}

# ---------------------------------------------------------------------------------------------------------------
# The run

function Get-ExitCodeForStatus
{
    param([string]$Status)
    switch ($Status)
    {
        'triage' { 1 }
        'environment' { 2 }
        default { 0 }
    }
}

function Invoke-WeeklyRun
{
    param(
        [Parameter(Mandatory)][string]$State,
        [int]$Count = 3,
        [switch]$Plan,
        [switch]$SkipIssue,
        [datetime]$Now = [datetime]::UtcNow
    )

    $lastRun = Read-JsonFile (Join-Path $State 'last-run.json')
    $overdue = Get-Overdue $lastRun $Now
    $version = & $script:Resolvers.Version
    $docsRef = & $script:Resolvers.DocsRef (-not $Plan)
    $date = $Now.ToString('yyyy-MM-dd')
    $outDirectory = Join-Path $State $date
    $suffix = 2
    while ((-not $Plan) -and (Test-Path -LiteralPath $outDirectory)) { $outDirectory = Join-Path $State "$date-$suffix"; $suffix++ }
    $arguments = @('-App', $script:TrialApp, '-Entry', 'readme', '-DocsRef', $docsRef, '-Version', $version, '-FreshMachine', '-Repeat', "$Count", '-OutDirectory', $outDirectory)

    if ($Plan)
    {
        Write-Host 'DRY RUN: nothing is run, written or fetched.'
        Write-Host "Release version : $version"
        Write-Host "Docs ref        : $docsRef (origin/master on the remote)"
        Write-Host "State directory : $State"
        Write-Host "Output directory: $outDirectory"
        Write-Host "Command         : pwsh -NoProfile -NonInteractive -File scripts/trial.ps1 $($arguments -join ' ')"
        Write-Host "Baseline        : $(if (Test-Path -LiteralPath (Join-Path $State 'baseline.json')) { 'present' } else { 'missing' })"
        Write-Host $overdue.Message
        return [pscustomobject]@{ Plan = $true; Overdue = $overdue; Arguments = $arguments; Version = $version; DocsRef = $docsRef; Status = $null }
    }

    if ($overdue.Overdue) { Write-Host $overdue.Message }
    New-Item -ItemType Directory -Path $outDirectory -Force | Out-Null
    $agentVersion = & $script:Resolvers.AgentVersion
    $harnessSha = & $script:Resolvers.HarnessSha
    Write-Host "weekly-trial: v$version, docs $docsRef, $Count run(s) -> $outDirectory"
    $trialExit = [int](@(& $script:TrialRunner $arguments) | Select-Object -Last 1)
    $records = Get-RunRecords $outDirectory
    $baseline = Read-JsonFile (Join-Path $State 'baseline.json')
    $comparison = Compare-ToBaseline $records $baseline -Expected $Count
    $model = @($records | ForEach-Object { $_.Model } | Where-Object { $_ } | Select-Object -First 1)

    $weekly = [ordered]@{
        schemaVersion = 1
        date = $Now.ToString('o')
        outDirectory = $outDirectory
        inputs = [ordered]@{ app = $script:TrialApp; entry = 'readme'; task = 'change'; version = $version; docsRef = $docsRef; repeat = $Count; freshMachine = $true; trialArguments = $arguments }
        environment = [ordered]@{
            agentVersion = $agentVersion
            model = if ($model.Count -gt 0) { $model[0] } else { 'unknown' }
            harnessSha = $harnessSha
            configuration = 'none (change task: no Pointframe skill or plugin)'
            freshMachineNote = '-FreshMachine renames the real Programs\Pointframe.Cli and Pointframe.Mcp folders for the run and redirects LOCALAPPDATA; agent credentials come from USERPROFILE.'
            trialExitCode = $trialExit
        }
        runs = @($records | ForEach-Object { [ordered]@{ folder = $_.Folder; outcome = $_.Outcome; rawOutcome = $_.RawOutcome; toolCalls = $_.ToolCalls; totalInputTokens = $_.TotalInputTokens; minutes = $_.Minutes; model = $_.Model; docsWebFetchViolation = $_.DocsWebFetchViolation; note = $_.Note } })
        comparison = $comparison
        overdueAtStart = $overdue.Overdue
    }
    $weeklyObject = ($weekly | ConvertTo-Json -Depth 8 | ConvertFrom-Json)
    Write-JsonFile (Join-Path $outDirectory 'weekly.json') $weekly
    $summary = Format-Summary $weeklyObject
    Set-Content -LiteralPath (Join-Path $outDirectory 'weekly.md') -Value $summary -Encoding utf8
    Write-JsonFile (Join-Path $State 'last-run.json') ([ordered]@{ date = $Now.ToString('o'); status = $comparison.status; outDirectory = $outDirectory })

    $issueResult = $null
    if ($comparison.status -eq 'triage' -and -not $SkipIssue)
    {
        $issueResult = Update-TriageIssue "Weekly trial: clean-pass loss ($date)" $summary
        Write-Host "weekly-trial: $issueResult"
    }
    Write-Host $summary
    [pscustomobject]@{ Plan = $false; Overdue = $overdue; Status = $comparison.status; Summary = $summary; OutDirectory = $outDirectory; Issue = $issueResult; Arguments = $arguments }
}

function Invoke-Analyze
{
    param([Parameter(Mandatory)][string]$Folder, [Parameter(Mandatory)][string]$State)
    $records = Get-RunRecords $Folder
    $baseline = Read-JsonFile (Join-Path $State 'baseline.json')
    $comparison = Compare-ToBaseline $records $baseline
    Write-Host "Analyze: $Folder"
    foreach ($line in Format-RunTable $records) { Write-Host $line }
    $counts = $comparison.counts
    Write-Host ("Classes: pass-clean {0}/{5}, pass-workaround {1}, fail {2}, timeout {3}, invalid {4}" -f $counts['pass-clean'], $counts['pass-workaround'], $counts['fail'], $counts['timeout'], $counts['invalid'], $records.Count)
    Write-Host "Status : $($comparison.status)"
    foreach ($reason in $comparison.reasons) { Write-Host "  - $reason" }
    [pscustomobject]@{ Records = $records; Comparison = $comparison }
}

function Invoke-Promote
{
    param([Parameter(Mandatory)][string]$Folder, [Parameter(Mandatory)][string]$State, [datetime]$Now = [datetime]::UtcNow)
    $records = Get-RunRecords $Folder
    $baseline = New-BaselineRecord $records $Folder $Now
    $path = Join-Path $State 'baseline.json'
    if (Test-Path -LiteralPath $path) { Copy-Item -LiteralPath $path -Destination (Join-Path $State 'baseline.previous.json') -Force }
    Write-JsonFile $path $baseline
    Write-Host "Baseline written: $path ($($baseline.cleanCount)/$($baseline.runCount) pass-clean, median toolCalls $($baseline.medianToolCalls), median totalInputTokens $($baseline.medianTotalInputTokens))"
    $baseline
}

# ---------------------------------------------------------------------------------------------------------------
# Self-test

function New-FixtureRuns
{
    param([Parameter(Mandatory)][string]$Directory, [Parameter(Mandatory)][object[]]$Runs, $Defaults)
    $index = 1
    foreach ($run in $Runs)
    {
        $folder = Join-Path $Directory ('{0:00}' -f $index)
        New-Item -ItemType Directory -Path $folder -Force | Out-Null
        $result = [ordered]@{
            app = Get-Prop $Defaults 'app'; task = Get-Prop $Defaults 'task'; entry = Get-Prop $Defaults 'entry'; agent = 'claude'
            agentModel = Get-Prop $Defaults 'model'; expectedVersion = '1.0.0'; docsCommit = ('a' * 40)
            docsWebFetchViolation = [bool](Get-Prop $run 'docsWebFetchViolation' $false)
            agentRun = [ordered]@{ timedOut = [bool](Get-Prop $run 'timedOut' $false) }
        }
        if ($null -ne (Get-Prop $run 'outcome')) { $result.outcome = $run.outcome }
        if ($null -ne (Get-Prop $run 'toolCalls')) { $result.metrics = [ordered]@{ toolCalls = $run.toolCalls; totalInputTokens = $run.totalInputTokens; minutes = $run.minutes } }
        if (-not (Get-Prop $run 'noResult' $false)) { ($result | ConvertTo-Json -Depth 6) | Set-Content -LiteralPath (Join-Path $folder 'result.json') -Encoding utf8 }
        $index++
    }
}

function Invoke-SelfTest
{
    $failures = [System.Collections.Generic.List[string]]::new()
    function Assert-That
    {
        param([bool]$Condition, [string]$Message)
        if ($Condition) { $script:selfPassed++ } else { $script:selfFailures.Add($Message) }
    }
    $script:selfPassed = 0
    $script:selfFailures = $failures

    $fixtures = Read-JsonFile (Join-Path $script:FixtureDirectory 'scenarios.json')
    $baselineFixture = Read-JsonFile (Join-Path $script:FixtureDirectory 'baseline.json')
    if (-not $fixtures -or -not $baselineFixture) { throw "Fixtures missing under $script:FixtureDirectory" }
    $root = Join-Path ([IO.Path]::GetTempPath()) "weekly-trial-selftest-$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $root | Out-Null

    $savedGh = $script:Gh
    $savedResolvers = $script:Resolvers
    $savedRunner = $script:TrialRunner
    try
    {
        $scenarioRecords = @{}
        foreach ($scenario in $fixtures.scenarios)
        {
            $directory = Join-Path $root "scenario-$($scenario.name)"
            $defaults = [pscustomobject]@{ app = $fixtures.defaults.app; task = $fixtures.defaults.task; entry = $fixtures.defaults.entry; model = if (Get-Prop $scenario 'model') { $scenario.model } else { $fixtures.defaults.model } }
            New-FixtureRuns $directory @($scenario.runs) $defaults
            $scenarioRecords[$scenario.name] = Get-RunRecords $directory
        }
        function Get-Status { param([string]$Name) (Compare-ToBaseline $scenarioRecords[$Name] $baselineFixture).status }

        # Classification of every outcome class, from raw outcomes and from missing or odd results
        $classes = @($scenarioRecords['classes'] | ForEach-Object { $_.Outcome })
        Assert-That (($classes -join ',') -eq 'pass-clean,pass-workaround,fail,timeout,invalid,invalid,timeout,invalid') "classes were: $($classes -join ',')"
        Assert-That ($scenarioRecords['classes'][5].Note -like 'unknown outcome*') 'an unknown raw outcome carries a note'
        Assert-That ($scenarioRecords['classes'][7].Note -like 'no result.json*') 'a missing result.json is invalid with a note'

        # Triage and its neighbours
        Assert-That ((Get-Status 'all-clean') -eq 'ok') 'three clean runs at baseline cost are ok'
        Assert-That ((Get-Status 'one-fail') -eq 'triage') 'a clean-pass loss to fail is triage'
        Assert-That ((Get-Status 'one-timeout') -eq 'triage') 'a clean-pass loss to timeout is triage'
        Assert-That ((Get-Status 'one-workaround') -eq 'triage') 'a clean-pass loss to pass-workaround is triage'
        Assert-That ((Get-Status 'two-fail-one-invalid') -eq 'triage') 'losses beyond the invalid runs are triage'

        # Invalid is environment trouble, never a product regression
        $invalidComparison = Compare-ToBaseline $scenarioRecords['one-invalid'] $baselineFixture
        Assert-That ($invalidComparison.status -eq 'environment') 'a shortfall explained by an invalid run is environment'
        Assert-That ((($invalidComparison.reasons -join ' ') -match 'not a product regression')) 'the environment reason says it is not a product regression'
        Assert-That ((Get-ExitCodeForStatus 'environment') -eq 2 -and (Get-ExitCodeForStatus 'triage') -eq 1) 'exit codes follow status'
        Assert-That ((Get-Status 'four-two-clean-fail-invalid') -eq 'triage') 'a scored failure is triage even when an invalid run is also present (-Repeat 4)'
        Assert-That ((Compare-ToBaseline $scenarioRecords['four-two-clean-fail-invalid'] $baselineFixture -Expected 4).status -eq 'triage') 'the Repeat 4 case stays triage with the expected count'
        Assert-That ((Get-Status 'other-model-fail') -eq 'triage') 'failures on another model are still triage, not incomparable'
        $partial = Compare-ToBaseline $scenarioRecords['partial'] $baselineFixture -Expected 3
        Assert-That ($partial.status -eq 'environment' -and (($partial.reasons -join ' ') -match 'missing')) 'a missing repetition is environment trouble, not triage'
        Assert-That ((Compare-ToBaseline $scenarioRecords['partial'] $baselineFixture).status -eq 'environment') 'fewer runs than the baseline needs, with no failure, is environment'
        Assert-That ((Get-Status 'all-invalid') -eq 'environment') 'all-invalid runs are environment'
        Assert-That ((Compare-ToBaseline @() $baselineFixture).status -eq 'environment') 'no scored runs is environment'

        # Cost warning: strictly more than 30%
        Assert-That ((Get-Status 'plus-35') -eq 'warning') '+35% median cost warns'
        Assert-That ((Get-Status 'plus-31') -eq 'warning') '+31% median cost warns'
        Assert-That ((Get-Status 'plus-30') -eq 'ok') 'exactly +30% does not warn (the limit is more than 30%)'
        Assert-That ((Get-Status 'plus-20') -eq 'ok') '+20% median cost does not warn'
        Assert-That ((Get-Status 'tokens-only-plus-40') -eq 'warning') 'tokens alone above the limit warn'
        Assert-That ((Get-Status 'cheaper') -eq 'ok') 'a cheaper run is ok'
        $mixed = Compare-ToBaseline $scenarioRecords['warning-with-failure'] $baselineFixture
        Assert-That ($mixed.status -eq 'triage') 'triage outranks a cost warning'
        $other = Compare-ToBaseline $scenarioRecords['other-model'] $baselineFixture
        Assert-That ($other.status -eq 'incomparable') 'runs on another model are incomparable, never compared on cost'
        $none = Compare-ToBaseline $scenarioRecords['all-clean'] $null
        Assert-That ($none.status -eq 'noBaseline') 'no baseline gives noBaseline'
        Assert-That ($scenarioRecords['violation'][0].DocsWebFetchViolation -and (Compare-ToBaseline $scenarioRecords['violation'] $baselineFixture).docsWebFetchViolations -eq 1) 'a docs web fetch violation is counted'

        # Median of an even count
        Assert-That ((Get-Median @(10, 20, 30, 50)) -eq 25 -and (Get-Median @(5, 1, 3)) -eq 3 -and $null -eq (Get-Median @())) 'median handles odd, even and empty'

        # Overdue computation
        $now = [datetime]::Parse('2026-10-26T09:00:00Z').ToUniversalTime()
        Assert-That ((Get-Overdue ([pscustomobject]@{ date = '2026-10-17T10:00:00Z' }) $now).Overdue) '8 days 23 hours is overdue'
        Assert-That (-not (Get-Overdue ([pscustomobject]@{ date = '2026-10-18T10:00:00Z' }) $now).Overdue) '7 days 23 hours is on schedule'
        Assert-That ((Get-Overdue ([pscustomobject]@{ date = '2026-10-19T09:00:00Z' }) $now).Overdue -eq $false) '7 days is on schedule'
        Assert-That ((Get-Overdue ([pscustomobject]@{ date = '2026-10-18T09:00:00Z' }) $now).Overdue -eq $false) 'exactly 8 days is still on schedule'
        $late = Get-Overdue ([pscustomobject]@{ date = '2026-10-17T08:00:00Z' }) $now
        Assert-That ($late.Overdue -and $late.Message -like 'OVERDUE*' -and $late.DaysSince -eq 9) 'more than 8 days is overdue'
        Assert-That ((Get-Overdue $null $now).Overdue) 'no record is overdue'
        Assert-That ((Get-Overdue ([pscustomobject]@{ date = 'garbage' }) $now).Overdue) 'an unreadable date is overdue'

        # Issue dedup decisions
        Assert-That ((Get-IssueDecision @()).Action -eq 'create') 'no open issue creates one'
        $decision = Get-IssueDecision @([pscustomobject]@{ number = 42 }, [pscustomobject]@{ number = 17 })
        Assert-That ($decision.Action -eq 'comment' -and $decision.Number -eq 17) 'open issues are commented on, oldest first'

        $ghCalls = [System.Collections.Generic.List[string]]::new()
        $ghState = @{ Open = '[]' }
        $script:Gh = {
            param([string[]]$Arguments)
            $ghCalls.Add($Arguments -join ' ')
            if ($Arguments[0] -eq 'issue' -and $Arguments[1] -eq 'list') { return [pscustomobject]@{ ExitCode = 0; Output = @($ghState.Open) } }
            if ($Arguments[0] -eq 'issue' -and $Arguments[1] -eq 'create') { return [pscustomobject]@{ ExitCode = 0; Output = @('https://github.com/x/y/issues/99') } }
            [pscustomobject]@{ ExitCode = 0; Output = @() }
        }.GetNewClosure()
        $created = Update-TriageIssue 'title' 'body'
        Assert-That ($created -like 'created issue*' -and @($ghCalls | Where-Object { $_ -like 'issue create*' }).Count -eq 1) 'with no open issue, one is created'
        $ghCalls.Clear()
        $ghState.Open = '[{"number":7,"title":"Weekly trial"}]'
        $commented = Update-TriageIssue 'title' 'body'
        Assert-That ($commented -eq 'commented on issue #7' -and @($ghCalls | Where-Object { $_ -like 'issue create*' }).Count -eq 0 -and @($ghCalls | Where-Object { $_ -like 'issue comment 7*' }).Count -eq 1) 'with an open issue, it is commented on and none is created'
        $ghCalls.Clear()
        $script:Gh = {
            param([string[]]$Arguments)
            $ghCalls.Add($Arguments -join ' ')
            [pscustomobject]@{ ExitCode = 1; Output = @('network down') }
        }.GetNewClosure()
        $threw = $false
        try { Update-TriageIssue 'title' 'body' | Out-Null } catch { $threw = $true }
        Assert-That ($threw -and @($ghCalls | Where-Object { $_ -like 'issue create*' }).Count -eq 0) 'a failed issue listing creates nothing (no duplicate risk)'

        # Promote refuses anything but an all pass-clean run
        $promoteFailed = $false
        try { New-BaselineRecord $scenarioRecords['one-fail'] 'x' | Out-Null } catch { $promoteFailed = $true }
        Assert-That $promoteFailed 'promoting a run with a failure is refused'
        $promoted = New-BaselineRecord $scenarioRecords['all-clean'] 'x' $now
        Assert-That ($promoted.cleanCount -eq 3 -and $promoted.medianToolCalls -eq 100 -and $promoted.medianTotalInputTokens -eq 1000000) 'promotion records clean count and medians'

        # The real version resolver reads the newest agent release (never Latest), from fixtures here
        $releaseFixtures = Join-Path $PSScriptRoot 'tests/agent-release'
        try
        {
            foreach ($releaseFixture in Get-ChildItem -LiteralPath $releaseFixtures -Filter '*.json')
            {
                $releaseCase = Read-JsonFile $releaseFixture.FullName
                $script:ReleasesFile = $releaseFixture.FullName
                if (Get-Prop $releaseCase 'expectedError')
                {
                    $failedClearly = $false
                    try { & $savedResolvers.Version | Out-Null } catch { $failedClearly = $_.Exception.Message -like 'Could not resolve the newest agent release:*' }
                    Assert-That $failedClearly "$($releaseFixture.Name): no complete agent release fails clearly"
                }
                else
                {
                    Assert-That ((& $savedResolvers.Version) -eq $releaseCase.expected) "$($releaseFixture.Name): the version is the newest agent release $($releaseCase.expected)"
                }
            }
        }
        finally { $script:ReleasesFile = $null }

        # Full run with fakes: baseline untouched, issue only on triage, DryRun runs nothing
        $trialCalls = [System.Collections.Generic.List[string]]::new()
        $script:Resolvers = @{
            Version = { '1.0.0' }
            DocsRef = { param([bool]$Fetch) ('b' * 40) }
            AgentVersion = { '2.1.160 (fake)' }
            HarnessSha = { 'c0ffee' }
        }
        $script:FakeTrial = @{ Calls = $trialCalls; Scenario = 'all-clean'; Fixtures = $fixtures }
        $script:TrialRunner = {
            param([string[]]$Arguments)
            $fake = $script:FakeTrial
            $fake.Calls.Add($Arguments -join ' ')
            $outIndex = [array]::IndexOf($Arguments, '-OutDirectory') + 1
            $scenario = $fake.Fixtures.scenarios | Where-Object { $_.name -eq $fake.Scenario }
            New-FixtureRuns $Arguments[$outIndex] @($scenario.runs) $fake.Fixtures.defaults
            0
        }
        $state = Join-Path $root 'state'
        New-Item -ItemType Directory -Path $state | Out-Null
        Write-JsonFile (Join-Path $state 'baseline.json') $baselineFixture
        $baselineHash = (Get-FileHash -LiteralPath (Join-Path $state 'baseline.json')).Hash

        $plan = Invoke-WeeklyRun -State $state -Count 3 -Plan -Now $now 6>$null
        Assert-That ($trialCalls.Count -eq 0 -and $plan.Plan -and $plan.Overdue.Overdue) '-DryRun runs no trial and reports overdue'
        Assert-That (-not (Test-Path (Join-Path $state 'last-run.json')) -and @(Get-ChildItem $state).Count -eq 1) '-DryRun writes nothing'
        Assert-That (($plan.Arguments -join ' ') -match '-FreshMachine' -and ($plan.Arguments -join ' ') -match '-Entry readme' -and ($plan.Arguments -join ' ') -match ('-DocsRef ' + ('b' * 40)) -and ($plan.Arguments -join ' ') -match '-Version 1.0.0') 'the plan pins version, docs ref, readme entry and FreshMachine'

        $ghCalls.Clear()
        $script:FakeTrial.Scenario = 'all-clean'
        $ok = Invoke-WeeklyRun -State $state -Count 3 -Now $now 6>$null
        Assert-That ($ok.Status -eq 'ok' -and $trialCalls.Count -eq 1) 'a clean week is ok and runs the trial once'
        Assert-That ((Get-FileHash -LiteralPath (Join-Path $state 'baseline.json')).Hash -eq $baselineHash) 'the baseline is unchanged after a run'
        $lastRun = Read-JsonFile (Join-Path $state 'last-run.json')
        Assert-That ($lastRun.status -eq 'ok' -and -not (Get-Overdue $lastRun $now).Overdue) 'last-run.json records the completed run'
        $weeklyJson = Read-JsonFile (Join-Path $ok.OutDirectory 'weekly.json')
        Assert-That ($weeklyJson.inputs.version -eq '1.0.0' -and $weeklyJson.environment.harnessSha -eq 'c0ffee' -and $weeklyJson.runs.Count -eq 3 -and (Test-Path (Join-Path $ok.OutDirectory 'weekly.md'))) 'weekly.json and weekly.md are written with inputs and runs'
        Assert-That ($ghCalls.Count -eq 0) 'an ok week touches no GitHub issue'

        $script:FakeTrial.Scenario = 'one-fail'
        $ghCalls.Clear()
        $script:Gh = {
            param([string[]]$Arguments)
            $ghCalls.Add($Arguments -join ' ')
            if ($Arguments[0] -eq 'issue' -and $Arguments[1] -eq 'list') { return [pscustomobject]@{ ExitCode = 0; Output = @('[]') } }
            [pscustomobject]@{ ExitCode = 0; Output = @('https://github.com/x/y/issues/1') }
        }.GetNewClosure()
        $triage = Invoke-WeeklyRun -State $state -Count 3 -Now $now 6>$null
        Assert-That ($triage.Status -eq 'triage' -and @($ghCalls | Where-Object { $_ -like 'issue create*' }).Count -eq 1) 'triage creates one issue'
        Assert-That ($triage.OutDirectory -ne $ok.OutDirectory -and (Split-Path $triage.OutDirectory -Leaf) -eq '2026-10-26-2' -and (Test-Path $ok.OutDirectory)) 'a second run the same day gets the -2 folder and keeps the first'
        Assert-That ((Read-JsonFile (Join-Path $triage.OutDirectory 'weekly.json')).environment.trialExitCode -is [long]) 'trialExitCode holds a single integer'
        $ghCalls.Clear()
        $silent = Invoke-WeeklyRun -State $state -Count 3 -SkipIssue -Now $now 6>$null
        Assert-That ($silent.Status -eq 'triage' -and $ghCalls.Count -eq 0) '-NoIssue triages without touching GitHub'
        Assert-That ((Get-FileHash -LiteralPath (Join-Path $state 'baseline.json')).Hash -eq $baselineHash) 'the baseline is unchanged after triage runs'

        $script:FakeTrial.Scenario = 'one-invalid'
        $ghCalls.Clear()
        $env2 = Invoke-WeeklyRun -State $state -Count 3 -Now $now.AddDays(2) 6>$null
        Assert-That ($env2.Status -eq 'environment' -and $ghCalls.Count -eq 0) 'an invalid-run shortfall opens no issue'

        # -DryRun with -PromoteBaseline is rejected before anything is written
        $modeState = Join-Path $root 'mode-state'
        New-Item -ItemType Directory -Path $modeState | Out-Null
        $null = & pwsh -NoProfile -NonInteractive -File $PSCommandPath -DryRun -PromoteBaseline (Join-Path $root 'scenario-all-clean') -StateDirectory $modeState 2>&1
        Assert-That ($LASTEXITCODE -ne 0 -and @(Get-ChildItem $modeState).Count -eq 0) '-DryRun with -PromoteBaseline fails and writes nothing'

        # Promotion needs identity fields and one version and docs commit
        $mixed = @($scenarioRecords['all-clean'] | ForEach-Object { $_.PSObject.Copy() })
        $mixed[1].Version = '2.0.0'
        $refusedMixed = $false
        try { New-BaselineRecord $mixed 'x' | Out-Null } catch { $refusedMixed = $true }
        Assert-That $refusedMixed 'promotion refuses mixed versions'
        $blank = @($scenarioRecords['all-clean'] | ForEach-Object { $_.PSObject.Copy() })
        $blank[0].DocsRef = $null
        $refusedBlank = $false
        try { New-BaselineRecord $blank 'x' | Out-Null } catch { $refusedBlank = $true }
        Assert-That $refusedBlank 'promotion refuses an empty docs commit'

        # -Analyze never writes; -PromoteBaseline is the only writer
        $analyzeState = Join-Path $root 'analyze-state'
        New-Item -ItemType Directory -Path $analyzeState | Out-Null
        $null = Invoke-Analyze (Join-Path $root 'scenario-all-clean') $analyzeState 6>$null
        Assert-That (@(Get-ChildItem $analyzeState).Count -eq 0) '-Analyze writes nothing'
        $null = Invoke-Promote (Join-Path $root 'scenario-all-clean') $analyzeState $now 6>$null
        $written = Read-JsonFile (Join-Path $analyzeState 'baseline.json')
        Assert-That ($written.cleanCount -eq 3 -and $written.entry -eq 'readme') '-PromoteBaseline writes baseline.json'
        $null = Invoke-Promote (Join-Path $root 'scenario-all-clean') $analyzeState $now 6>$null
        Assert-That (Test-Path (Join-Path $analyzeState 'baseline.previous.json')) 'promoting over a baseline keeps the previous one'
        $refused = $false
        try { Invoke-Promote (Join-Path $root 'scenario-one-fail') $analyzeState $now 6>$null | Out-Null } catch { $refused = $true }
        Assert-That ($refused -and (Read-JsonFile (Join-Path $analyzeState 'baseline.json')).cleanCount -eq 3) 'a refused promotion leaves the baseline alone'
    }
    finally
    {
        $script:Gh = $savedGh
        $script:Resolvers = $savedResolvers
        $script:TrialRunner = $savedRunner
        Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
    }

    if ($failures.Count -gt 0)
    {
        foreach ($failure in $failures) { Write-Host "FAIL  $failure" }
        Write-Host "weekly-trial self-test: $($failures.Count) failure(s), $($script:selfPassed) passed."
        return 1
    }
    Write-Host "weekly-trial self-test: $($script:selfPassed) checks passed."
    0
}

# ---------------------------------------------------------------------------------------------------------------
# Entry

if ($SelfTest)
{
    exit (Invoke-SelfTest)
}

if (-not $StateDirectory) { $StateDirectory = Join-Path $script:RepositoryRoot 'plan/trials/weekly' }
$StateDirectory = [IO.Path]::GetFullPath($StateDirectory)

if ($PromoteBaseline -and $DryRun)
{
    throw '-DryRun cannot be combined with -PromoteBaseline: promotion writes baseline.json.'
}

if ($PromoteBaseline)
{
    Invoke-Promote ([IO.Path]::GetFullPath($PromoteBaseline)) $StateDirectory | Out-Null
    exit 0
}

if ($Analyze)
{
    $analysis = Invoke-Analyze ([IO.Path]::GetFullPath($Analyze)) $StateDirectory
    exit (Get-ExitCodeForStatus $analysis.Comparison.status)
}

$outcome = Invoke-WeeklyRun -State $StateDirectory -Count $Repeat -Plan:$DryRun -SkipIssue:$NoIssue
if ($DryRun) { exit 0 }
exit (Get-ExitCodeForStatus $outcome.Status)
