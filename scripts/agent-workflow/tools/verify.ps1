#Requires -Version 7.0
<#
One command that decides whether a change is ready: the gates of agent-workflow.json, with failures reported so an
agent can act on them. Run it before saying a task is done; a task is done when it passes.

  verify.ps1                                    # every gate; exit 0 only when all pass
  verify.ps1 -Filter "FullyQualifiedName~Foo"   # narrow the gate that declares filterFlag while iterating
  verify.ps1 -Skip kb                           # leave gates out while iterating (never for the final run)
  verify.ps1 -ReuseIfFresh                      # reuse an identical complete receipt when environment and tree match
  verify.ps1 -Root <repository>                 # the repository to verify (default: the repository that holds this script)

The gates come from the configuration. Records of "gates" that share a "gate" name form one gate of the verdict and run in
order; "dependsOn" orders the gates and skips a gate whose dependency failed. Built in: preflight (verify.preflight.lockDirs:
no running process locks the build output) and doctor (verify.doctor: the vendored kit still equals its lock). Every
other gate always runs, so one pass reports every problem.

Writes <verify.outputDir>/verdict.json (status, gates, failure details, working-tree hash, and environment fingerprint)
and a log per gate next to it. -ReuseIfFresh skips gates only when the complete passing receipt matches head, tree,
dotnet SDK version, pwsh version, and this script's SHA-256. Exit 0 when every gate passed, 1 when any failed, 2 on bad arguments.
#>
[CmdletBinding()]
param(
    [string]$Filter,
    [string[]]$Skip = @(),
    [switch]$ReuseIfFresh,
    [string]$Root
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '..' 'lib' 'root.ps1')
. (Join-Path $PSScriptRoot '..' 'lib' 'config.ps1')
. (Join-Path $PSScriptRoot '..' 'lib' 'context.ps1')
. (Join-Path $PSScriptRoot '..' 'lib' 'tree-hash.ps1')

try
{
    $Context = Get-AwToolContext -Root $Root -ScriptRoot $PSScriptRoot
}
catch
{
    Write-Host "ERROR $($_.Exception.Message)"
    exit 2
}
$RepoRoot = $Context.Root
$Settings = Get-AwSection $Context 'verify'
$OutDir = [IO.Path]::GetFullPath((Join-Path $RepoRoot ([string](Get-AwSetting $Settings 'outputDir' 'artifacts/verify'))))
$VerdictPath = Join-Path $OutDir 'verdict.json'
$MaxDetails = [int](Get-AwSetting $Settings 'maxDetails' 20)

# --------------------------------------------------------------------- gate model

# Records that share a gate name run together as one gate of the verdict, in configuration order.
$groups = [ordered]@{}
foreach ($record in $Context.Config.gates)
{
    $name = if ($record.Contains('gate')) { [string]$record.gate } else { [string]$record.id }
    if (-not $groups.Contains($name))
    {
        $groups[$name] = [pscustomobject]@{ Name = $name; Records = [Collections.Generic.List[object]]::new(); After = [Collections.Generic.List[string]]::new() }
    }
    $groups[$name].Records.Add($record)
}
$gateOf = @{}
foreach ($record in $Context.Config.gates)
{
    $gateOf[[string]$record.id] = if ($record.Contains('gate')) { [string]$record.gate } else { [string]$record.id }
}
foreach ($record in $Context.Config.gates)
{
    $own = $gateOf[[string]$record.id]
    foreach ($dependency in $record.dependsOn)
    {
        $target = $gateOf[[string]$dependency]
        if ($target -ne $own -and -not $groups[$own].After.Contains($target)) { $groups[$own].After.Add($target) }
    }
}

# Dependency order, stable: the first gate in configuration order whose dependencies are all placed goes next.
$ordered = [Collections.Generic.List[object]]::new()
$placed = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$remaining = [Collections.Generic.List[object]]::new()
foreach ($group in $groups.Values) { $remaining.Add($group) }
while ($remaining.Count -gt 0)
{
    $next = $null
    foreach ($candidate in $remaining)
    {
        if (@($candidate.After | Where-Object { -not $placed.Contains($_) }).Count -eq 0) { $next = $candidate; break }
    }
    if (-not $next)
    {
        Write-Host "ERROR the gates depend on each other in a cycle: $(($remaining | ForEach-Object { $_.Name }) -join ', ')."
        exit 2
    }
    $ordered.Add($next)
    [void]$placed.Add($next.Name)
    [void]$remaining.Remove($next)
}

$BuiltIns = [Collections.Generic.List[string]]::new()
if ($null -ne (Get-AwSetting $Settings 'preflight' $null)) { $BuiltIns.Add('preflight') }
if ([bool](Get-AwSetting $Settings 'doctor' $false)) { $BuiltIns.Add('doctor') }
foreach ($builtIn in $BuiltIns)
{
    if ($groups.Contains($builtIn))
    {
        Write-Host "ERROR the configured gate '$builtIn' collides with the built-in gate of that name."
        exit 2
    }
}
$GateNames = @($BuiltIns) + @($ordered | ForEach-Object { $_.Name })

$Skip = @($Skip | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$unknown = @($Skip | Where-Object { $GateNames -notcontains $_ })
if ($unknown.Count -gt 0)
{
    Write-Host "ERROR unknown gate(s): $($unknown -join ', '). Gates: $($GateNames -join ', ')."
    exit 2
}
if ($Filter -and @($Context.Config.gates | Where-Object { $_.Contains('filterFlag') }).Count -eq 0)
{
    Write-Host 'ERROR -Filter needs a gate with filterFlag in agent-workflow.json.'
    exit 2
}

# ------------------------------------------------------------------- environment

$dotnetVersion = ''
if (Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue)
{
    $dotnetVersion = (& dotnet --version).Trim()
}
$started = Get-Date
$environment = [ordered]@{
    dotnet = $dotnetVersion
    pwsh = $PSVersionTable.PSVersion.ToString()
    verifySha256 = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash.ToLowerInvariant()
}
$currentHead = (git -C $RepoRoot rev-parse HEAD).Trim()
$treeHash = Get-WorkingTreeHash $RepoRoot
if ($ReuseIfFresh -and (Test-Path -LiteralPath $VerdictPath))
{
    $existing = Read-VerifyReceipt $VerdictPath
    $fresh = Test-VerifyReceiptObject $existing $currentHead $treeHash ([pscustomobject]$environment)
    if ($fresh.Ok)
    {
        Write-Host "VERIFY PASSED (reused receipt from $($existing.startedAt); tree unchanged)"
        exit 0
    }
}

New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
Get-ChildItem -Path $OutDir -File | Remove-Item -Force
Push-Location $RepoRoot

function ConvertTo-RepoRelative([string]$Text)
{
    $Text.Replace("$RepoRoot\", '').Replace("$RepoRoot/", '')
}

# One command of a gate. Standard output and error are merged in arrival order into the gate's log; a log stem shared by
# several records collects all their lines in one file. The command is stopped when its timeoutMinutes pass.
$script:LogLines = @{}
function Invoke-Logged([string]$Stem, [string]$Exe, [string[]]$Arguments, [string]$Directory, [int]$TimeoutMinutes)
{
    $log = Join-Path $OutDir "$Stem.log"
    $lines = [Collections.Generic.List[string]]::new()
    $exitCode = 0
    $file = $null
    if ($Exe.Contains('\') -or $Exe.Contains('/'))
    {
        $candidate = if ([IO.Path]::IsPathRooted($Exe)) { $Exe } else { [IO.Path]::GetFullPath((Join-Path $Directory $Exe)) }
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { $file = $candidate }
    }
    else
    {
        $command = Get-Command $Exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($command) { $file = $command.Source }
    }
    if (-not $file)
    {
        $lines.Add("cannot find the executable '$Exe'")
        $exitCode = 127
    }
    else
    {
        $psi = [Diagnostics.ProcessStartInfo]::new($file)
        foreach ($argument in $Arguments) { [void]$psi.ArgumentList.Add($argument) }
        $psi.WorkingDirectory = $Directory
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $psi.RedirectStandardInput = $true
        $psi.UseShellExecute = $false
        $psi.StandardOutputEncoding = [Console]::OutputEncoding
        $psi.StandardErrorEncoding = [Console]::OutputEncoding
        $process = [Diagnostics.Process]::Start($psi)
        $process.StandardInput.Close()
        $pendingOut = $process.StandardOutput.ReadLineAsync()
        $pendingErr = $process.StandardError.ReadLineAsync()
        $deadline = [DateTime]::UtcNow.AddMinutes($TimeoutMinutes)
        $timedOut = $false
        $exitedAt = $null
        while ($pendingOut -or $pendingErr)
        {
            $active = [Threading.Tasks.Task[]]@(@($pendingOut, $pendingErr) | Where-Object { $_ })
            [void][Threading.Tasks.Task]::WaitAny($active, 200)
            if ($pendingOut -and $pendingOut.IsCompleted)
            {
                $line = $pendingOut.Result
                if ($null -eq $line) { $pendingOut = $null } else { $lines.Add($line); $pendingOut = $process.StandardOutput.ReadLineAsync() }
            }
            if ($pendingErr -and $pendingErr.IsCompleted)
            {
                $line = $pendingErr.Result
                if ($null -eq $line) { $pendingErr = $null } else { $lines.Add($line); $pendingErr = $process.StandardError.ReadLineAsync() }
            }
            if ([DateTime]::UtcNow -gt $deadline)
            {
                $timedOut = $true
                try { $process.Kill($true) } catch { }
                break
            }
            if ($process.HasExited)
            {
                # A grandchild that keeps a pipe open must not hold the gate for ever.
                if (-not $exitedAt) { $exitedAt = [DateTime]::UtcNow }
                elseif (([DateTime]::UtcNow - $exitedAt).TotalSeconds -gt 5) { break }
            }
        }
        $process.WaitForExit()
        $exitCode = $process.ExitCode
        if ($timedOut)
        {
            $lines.Add("timed out after $TimeoutMinutes minute(s); the command was stopped")
            $exitCode = 124
        }
        $process.Dispose()
    }
    $text = @($lines | ForEach-Object { "$_" })
    if (-not $script:LogLines.ContainsKey($Stem)) { $script:LogLines[$Stem] = [Collections.Generic.List[string]]::new() }
    $script:LogLines[$Stem].AddRange([string[]]$text)
    @($script:LogLines[$Stem]) | Set-Content -Path $log -Encoding utf8
    [pscustomobject]@{ ExitCode = $exitCode; Lines = $text; Log = ConvertTo-RepoRelative $log; AllLines = @($script:LogLines[$Stem]) }
}

function Get-ErrorLines([string[]]$Lines)
{
    @($Lines |
        Where-Object { $_ -match ':\s+error\s' } |
        ForEach-Object { ConvertTo-RepoRelative ($_ -replace '\s+\[[^\]]+\.csproj[^\]]*\]\s*$', '').Trim() } |
        Select-Object -Unique)
}

function New-Gate([string]$Name, [string]$Status, [string]$Summary, [string[]]$Details = @(), [string]$Log = $null)
{
    $all = @($Details)
    $shown = @($all | Select-Object -First $MaxDetails)
    if ($all.Count -gt $MaxDetails)
    {
        $shown += "... $($all.Count - $MaxDetails) more, see $Log"
    }
    [ordered]@{ name = $Name; status = $Status; summary = $Summary; details = $shown; log = $Log; seconds = 0 }
}

function Get-RecordValue($Record, [string]$Key, [string]$Default = '')
{
    # {dollar} stands for a dollar sign: configuration strings may not contain interpolation syntax.
    if ($Record.Contains($Key)) { ([string]$Record[$Key]).Replace('{dollar}', '$') } else { $Default }
}

# The outcome of one command: a gate object for a pass or a fail.
function Get-StepGate([string]$GateName, $Record, $Run)
{
    $parser = Get-RecordValue $Record 'parser' 'exit-code'
    $passSummary = Get-RecordValue $Record 'passSummary' "$($Record.id) passed."
    $fixHint = Get-RecordValue $Record 'fixHint'

    if ($parser -eq 'dotnet-test')
    {
        $trx = Join-Path $RepoRoot (Get-RecordValue $Record 'resultFile')
        if (-not (Test-Path $trx))
        {
            return New-Gate $GateName 'fail' "Test run produced no results (exit $($Run.ExitCode))." @($Run.Lines | Select-Object -Last 10) $Run.Log
        }

        [xml]$doc = Get-Content -Path $trx -Raw
        $ns = @{ t = 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010' }
        $counters = (Select-Xml -Xml $doc -XPath '//t:ResultSummary/t:Counters' -Namespace $ns).Node
        $total = [int]$counters.total
        $failedNodes = @(Select-Xml -Xml $doc -XPath "//t:UnitTestResult[@outcome='Failed']" -Namespace $ns | ForEach-Object { $_.Node })
        $scope = if ($Filter) { " (filter: $Filter)" } else { '' }

        if ($total -eq 0)
        {
            return New-Gate $GateName 'fail' "No tests matched$scope." -Log $Run.Log
        }
        if ($failedNodes.Count -eq 0 -and $Run.ExitCode -eq 0)
        {
            return New-Gate $GateName 'pass' "$($counters.passed) of $total test(s) passed$scope." -Log $Run.Log
        }

        $details = @($failedNodes | ForEach-Object {
                $message = ''
                $messageNode = $_.SelectSingleNode('.//*[local-name()="ErrorInfo"]/*[local-name()="Message"]')
                if ($messageNode)
                {
                    $message = (($messageNode.InnerText -split "`r?`n") | Where-Object { $_.Trim() } | Select-Object -First 3) -join ' | '
                }
                "$($_.testName): $message"
            })
        return New-Gate $GateName 'fail' "$($failedNodes.Count) of $total test(s) failed$scope." $details $Run.Log
    }

    if ($Run.ExitCode -eq 0)
    {
        $summary = $passSummary.Replace('{lastLine}', [string]($Run.AllLines | Select-Object -Last 1))
        return New-Gate $GateName 'pass' $summary -Log $Run.Log
    }

    if ($parser -eq 'dotnet-build')
    {
        $errors = @(Get-ErrorLines $Run.Lines)
        if ($errors.Count -eq 0)
        {
            # No compiler-style error line (a locked file, a restore failure): show the tail of the log.
            return New-Gate $GateName 'fail' "Build failed (exit $($Run.ExitCode))." @($Run.Lines | Select-Object -Last 15) $Run.Log
        }
        return New-Gate $GateName 'fail' "Build failed with $($errors.Count) error(s)." $errors $Run.Log
    }
    if ($parser -eq 'dotnet-format')
    {
        $errors = @(Get-ErrorLines $Run.Lines)
        $details = if ($fixHint) { @($fixHint) + $errors } else { $errors }
        return New-Gate $GateName 'fail' "Formatting differs in $($errors.Count) place(s)." $details $Run.Log
    }

    $pattern = Get-RecordValue $Record 'detailPattern'
    if ($pattern)
    {
        $problems = @($Run.Lines | Where-Object { $_ -match $pattern } | ForEach-Object { $_.Trim() })
    }
    else
    {
        $problems = @($Run.Lines | Select-Object -Last 15)
    }
    $failSummary = Get-RecordValue $Record 'failSummary' "$($Record.id) failed (exit {exit})."
    $summary = $failSummary.Replace('{count}', [string]$problems.Count).Replace('{exit}', [string]$Run.ExitCode)
    $details = if ($fixHint) { @($fixHint) + $problems } else { $problems }
    New-Gate $GateName 'fail' $summary $details $Run.Log
}

function Get-ArgumentsFor($Record)
{
    $arguments = @($Record.args | ForEach-Object { [string]$_ })
    if ($Filter -and $Record.Contains('filterFlag'))
    {
        $index = [Array]::IndexOf($arguments, [string]$Record.filterFlag)
        if ($index -ge 0 -and $index + 1 -lt $arguments.Count)
        {
            $arguments[$index + 1] = "($($arguments[$index + 1]))&($Filter)"
        }
        else
        {
            $arguments += @([string]$Record.filterFlag, $Filter)
        }
    }
    $arguments
}

function Test-Preflight
{
    $preflight = Get-AwSetting $Settings 'preflight' @{}
    $releaseDirs = @($preflight.lockDirs | ForEach-Object { [IO.Path]::GetFullPath((Join-Path $RepoRoot ([string]$_))) })
    $lockers = @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
            $path = $null
            try { $path = $_.Path } catch { }
            $path -and @($releaseDirs | Where-Object { $path.StartsWith($_, [StringComparison]::OrdinalIgnoreCase) }).Count -gt 0
        })
    if ($lockers.Count -eq 0)
    {
        return New-Gate 'preflight' 'pass' 'No running process holds the Release build output.'
    }
    $details = @($lockers | ForEach-Object { "$($_.ProcessName) (PID $($_.Id)): $(ConvertTo-RepoRelative $_.Path)" })
    $details += 'Stop these processes (Stop-Process -Id <PID>) or the build fails with MSB3027 (file locked).'
    New-Gate 'preflight' 'fail' "$($lockers.Count) running process(es) lock the Release build output." $details
}

function Test-Doctor
{
    $doctor = Join-Path $PSScriptRoot 'doctor.ps1'
    $pwshPath = (Get-Process -Id $PID).Path
    $run = Invoke-Logged 'doctor' $pwshPath @('-NoProfile', '-NonInteractive', '-File', $doctor, '-Root', $RepoRoot) $RepoRoot 5
    if ($run.ExitCode -eq 0)
    {
        return New-Gate 'doctor' 'pass' 'The vendored kit files match agent-workflow.lock.json.' -Log $run.Log
    }
    $problems = @($run.Lines | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    $details = @('Fix: restore the managed files under scripts/agent-workflow (git checkout) or install the intended kit release with scripts/agent-workflow/tools/install.ps1; never edit them by hand.') + $problems
    New-Gate 'doctor' 'fail' "The vendored kit differs from its lock ($($problems.Count) line(s))." $details $run.Log
}

function Test-ConfiguredGate($Group)
{
    $result = $null
    $last = $null
    foreach ($record in $Group.Records)
    {
        $stem = Get-RecordValue $record 'log' ([string]$record.id)
        $directory = [IO.Path]::GetFullPath((Join-Path $RepoRoot ([string]$record.cwd)))
        $run = Invoke-Logged $stem ([string]$record.exe) (Get-ArgumentsFor $record) $directory ([int]$record.timeoutMinutes)
        $gate = Get-StepGate $Group.Name $record $run
        if ($gate.status -eq 'fail' -and -not $result)
        {
            $result = $gate
            if (-not ($record.Contains('continueOnFail') -and $record.continueOnFail)) { break }
        }
        elseif ($gate.status -eq 'pass' -and -not $result)
        {
            $last = $gate
        }
    }
    if ($result) { return $result }
    $last
}

# ------------------------------------------------------------------------ run

$started = Get-Date
$gates = [System.Collections.Generic.List[object]]::new()
$statusByGate = @{}

try
{
    foreach ($name in $GateNames)
    {
        if ($Skip -contains $name)
        {
            $gates.Add((New-Gate $name 'skipped' 'Skipped with -Skip.'))
            continue
        }
        $blocked = $null
        if ($groups.Contains($name))
        {
            $blocked = @($groups[$name].After | Where-Object { $statusByGate.ContainsKey($_) -and $statusByGate[$_] -ne 'pass' -and $statusByGate[$_] -ne 'skipped' }) | Select-Object -First 1
        }
        if ($blocked)
        {
            $gates.Add((New-Gate $name 'skipped' "Skipped because the $blocked failed."))
            $statusByGate[$name] = 'blocked'
            continue
        }
        Write-Host "verify: $name ..."
        $timer = [Diagnostics.Stopwatch]::StartNew()
        $gate = switch ($name)
        {
            'preflight' { if ($groups.Contains($name)) { Test-ConfiguredGate $groups[$name] } else { Test-Preflight } }
            'doctor' { if ($groups.Contains($name)) { Test-ConfiguredGate $groups[$name] } else { Test-Doctor } }
            default { Test-ConfiguredGate $groups[$name] }
        }
        $gate.seconds = [math]::Round($timer.Elapsed.TotalSeconds, 1)
        $gates.Add($gate)
        $statusByGate[$name] = $gate.status
    }
}
finally
{
    Pop-Location
}

$failed = @($gates | Where-Object { $_.status -eq 'fail' })
$skipped = @($gates | Where-Object { $_.status -eq 'skipped' })
$status = if ($failed.Count -gt 0) { 'fail' } elseif ($skipped.Count -gt 0 -or $Filter) { 'partial' } else { 'pass' }

$verdict = [ordered]@{
    status = $status
    complete = $status -eq 'pass'
    startedAt = $started.ToString('o')
    seconds = [math]::Round(((Get-Date) - $started).TotalSeconds, 1)
    head = (git -C $RepoRoot rev-parse HEAD).Trim()
    treeHash = $treeHash
    environment = $environment
    filter = $Filter
    gates = $gates
}
$verdict | ConvertTo-Json -Depth 6 | Set-Content -Path $VerdictPath -Encoding utf8

Write-Host ''
foreach ($gate in $gates)
{
    $mark = switch ($gate.status) { 'pass' { 'PASS' } 'fail' { 'FAIL' } default { 'SKIP' } }
    Write-Host ("{0}  {1,-9} {2}" -f $mark, $gate.name, $gate.summary)
    foreach ($line in $gate.details)
    {
        Write-Host "        $line"
    }
}
Write-Host ''
$resultLine = switch ($status)
{
    'pass' { 'VERIFY PASSED: every gate passed.' }
    'partial' { 'VERIFY PARTIAL: no failures, but gates were skipped or tests filtered; run without -Skip and -Filter for a final verdict.' }
    default { "VERIFY FAILED: $($failed.Count) gate(s) failed. Fix the details above and run again." }
}
Write-Host $resultLine
Write-Host "Verdict: $(ConvertTo-RepoRelative $VerdictPath)"

exit ([int]($status -eq 'fail'))
