# ---- Process helpers --------------------------------------------------------------------------------------------

function Invoke-Logged([string]$Exe, [string[]]$ArgList, [string]$Dir, [string]$Log, [switch]$Live)
{
    $previous = $env:MSBUILDDISABLENODEREUSE
    $env:MSBUILDDISABLENODEREUSE = '1'
    Push-Location -LiteralPath $Dir
    try
    {
        New-Item -ItemType Directory -Path (Split-Path $Log -Parent) -Force | Out-Null
        [IO.File]::WriteAllText($Log, '')
        & $Exe @ArgList *>&1 | ForEach-Object { $text = "$_"; if ($Live) { Write-Host $text }; $text } | Set-Content -LiteralPath $Log -Encoding utf8
        return $LASTEXITCODE
    }
    catch
    {
        "failed to start: $($_.Exception.Message)" | Set-Content -LiteralPath $Log -Encoding utf8
        return 127
    }
    finally
    {
        Pop-Location
        if ($null -eq $previous) { Remove-Item Env:MSBUILDDISABLENODEREUSE -ErrorAction SilentlyContinue } else { $env:MSBUILDDISABLENODEREUSE = $previous }
    }
}

function Get-CheckCommands($Manifest)
{
    # Mutating steps first (extra commands marked "mutates", the format command, CRLF normalization), then the validators
    # (build, tests, the other extra commands), so every validator runs on the tree the check ends with.
    $list = [Collections.Generic.List[hashtable]]::new()
    foreach ($extra in $Manifest.extra | Where-Object { $_.mutates }) { $list.Add(@{ name = $extra.name; kind = 'extra'; mutator = $true; exe = $extra.exe; args = @($extra.args) }) }
    foreach ($target in $Manifest.format) { $c = Expand-PackageCommand (Get-PackageCommand 'format') @{ target = $target }; $list.Add(@{ name = "format $target"; kind = 'format'; mutator = $true; exe = $c.exe; args = @($c.args) }) }
    $list.Add(@{ name = 'crlf normalization'; kind = 'crlf'; mutator = $true; exe = ''; args = @() })
    foreach ($target in $Manifest.build) { $c = Expand-PackageCommand (Get-PackageCommand 'build') @{ target = $target }; $list.Add(@{ name = "build $target"; kind = 'build'; mutator = $false; exe = $c.exe; args = @($c.args) }) }
    foreach ($test in $Manifest.tests)
    {
        $baseFilter = Get-PackageTestFilter
        $filter = if ($test.filter) { $(if ($baseFilter) { "($baseFilter)&($($test.filter))" } else { [string]$test.filter }) } else { $baseFilter }
        $c = Expand-PackageCommand (Get-PackageCommand 'test') @{ project = $test.project; filter = $filter }
        $list.Add(@{ name = "test $($test.project)"; kind = 'test'; mutator = $false; exe = $c.exe; args = @($c.args) })
    }
    foreach ($extra in $Manifest.extra | Where-Object { -not $_.mutates }) { $list.Add(@{ name = $extra.name; kind = 'extra'; mutator = $false; exe = $extra.exe; args = @($extra.args) }) }
    @($list)
}

function Get-CheckEvidenceProblems($Check, $Plan)
{
    # A check counts as evidence only if it covers every command of the manifest, in order, with an exit code and an
    # existing log, and each command ran on the tree the previous one left (validators on the check's final tree).
    $problems = [Collections.Generic.List[string]]::new()
    $invalidation = Get-Field $Check 'invalidated'
    if ($invalidation) { $problems.Add("the check was invalidated by recover ($(Get-Field $invalidation 'reason')); run check again") }
    $recorded = @($Check.commands)
    $covered = $recorded.Count -eq $Plan.Count
    for ($i = 0; $covered -and $i -lt $Plan.Count; $i++) { if ($recorded[$i].name -ne $Plan[$i].name) { $covered = $false } }
    if (-not $covered)
    {
        $problems.Add("the check does not cover the manifest's commands ($($recorded.Count) recorded, $($Plan.Count) expected, or the names differ)")
        return @($problems)
    }
    $expectedInput = [string]$Check.treeBefore
    for ($i = 0; $i -lt $Plan.Count; $i++)
    {
        $c = $recorded[$i]
        if ($c.skipped) { if ($Check.ok) { $problems.Add("$($c.name) was skipped in a passing check") } }
        else
        {
            if ($null -eq $c.exitCode) { $problems.Add("$($c.name) has no exit code") }
            if ($Plan[$i].kind -ne 'crlf' -and (-not $c.log -or -not (Test-Path -LiteralPath $c.log -PathType Leaf))) { $problems.Add("$($c.name) has no log") }
        }
        if ($c.inputTree -ne $expectedInput) { $problems.Add("$($c.name) did not run on the tree the previous command left") }
        if (-not $Plan[$i].mutator -and $c.inputTree -ne $Check.treeHash) { $problems.Add("$($c.name) validated a tree other than the one the check ended with") }
        $expectedInput = [string]$c.outputTree
    }
    if ($expectedInput -ne [string]$Check.treeHash) { $problems.Add('the last command did not leave the recorded final tree') }
    # ok must be exactly "every command ran, exited 0, and no validator changed the tree"; anything else contradicts itself.
    $derivedOk = $true
    foreach ($c in $recorded) { if ($c.skipped -or $null -eq $c.exitCode -or $c.exitCode -ne 0 -or ($c.ContainsKey('modifiedWorktree') -and $c.modifiedWorktree)) { $derivedOk = $false } }
    # A check that named raw-byte problems did not pass (older checks have no such field).
    if (@(Get-List (Get-Field $Check 'rawBytesProblems')).Count) { $derivedOk = $false }
    if ([bool]$Check.ok -ne $derivedOk) { $problems.Add("the check says ok=$([bool]$Check.ok) but its recorded exit codes say $derivedOk") }
    @($problems)
}

function Invoke-CrlfNormalize([string]$Wt, [string]$BaseSha)
{
    $changed = @((Invoke-Git $Wt @('-c', 'core.quotepath=false', 'diff', '--no-ext-diff', '--no-textconv', '--name-only', '--diff-filter=d', $BaseSha)) -split "`n") +
    @((Invoke-Git $Wt @('-c', 'core.quotepath=false', 'ls-files', '--others', '--exclude-standard')) -split "`n")
    $fixed = [Collections.Generic.List[string]]::new()
    foreach ($relative in ($changed | Where-Object { $_ } | Select-Object -Unique))
    {
        if ($relative -match '\.(sh|bash)$') { continue }
        $path = Join-Path $Wt $relative
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }
        $bytes = [IO.File]::ReadAllBytes($path)
        if ($bytes.Length -gt 5MB -or [Array]::IndexOf($bytes, [byte]0) -ge 0) { continue }
        $needs = $false
        for ($i = 0; $i -lt $bytes.Length; $i++)
        {
            if ($bytes[$i] -eq 10 -and ($i -eq 0 -or $bytes[$i - 1] -ne 13)) { $needs = $true; break }
        }
        if (-not $needs) { continue }
        $buffer = [IO.MemoryStream]::new()
        for ($i = 0; $i -lt $bytes.Length; $i++)
        {
            if ($bytes[$i] -eq 10 -and ($i -eq 0 -or $bytes[$i - 1] -ne 13)) { $buffer.WriteByte(13) }
            $buffer.WriteByte($bytes[$i])
        }
        [IO.File]::WriteAllBytes($path, $buffer.ToArray())
        $fixed.Add($relative)
    }
    @($fixed)
}

function Invoke-GitWithInput([string]$Dir, [string[]]$GitArgs, [byte[]]$InputBytes)
{
    # git with raw bytes on standard input (NUL separated path lists cannot go through the PowerShell pipeline). Returns the exit code and standard error.
    $psi = [Diagnostics.ProcessStartInfo]::new('git')
    $psi.WorkingDirectory = $Dir
    foreach ($argument in @('-C', $Dir) + $GitArgs) { [void]$psi.ArgumentList.Add($argument) }
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.UseShellExecute = $false
    $process = [Diagnostics.Process]::Start($psi)
    $out = $process.StandardOutput.ReadToEndAsync()
    $err = $process.StandardError.ReadToEndAsync()
    try { $process.StandardInput.BaseStream.Write($InputBytes, 0, $InputBytes.Length) } finally { $process.StandardInput.Close() }
    $process.WaitForExit()
    $out.Wait()
    $err.Wait()
    @{ ExitCode = $process.ExitCode; Error = $err.Result }
}

function Get-RawByteProblems([string]$Wt)
{
    # Tracked files that git sees as unchanged (a clean filter such as autocrlf hides an edit and its revert, assume-unchanged hides
    # everything) but whose raw bytes are not what checking their index entry out writes. The review snapshot is exactly such a checkout, so
    # the checks would have run on bytes the reviewer never reads. Report only: nothing is rewritten, and no object is written (the
    # checkout goes to a temporary folder, the index is not updated). Files git reports as changed are the change set; the CRLF step
    # and the review's own content comparison cover them.
    $staged = (& git -C $Wt ls-files -s -z 2>&1 | ForEach-Object { "$_" }) -join ''
    if ($LASTEXITCODE -ne 0) { throw "git ls-files failed in ${Wt}: $staged" }
    $changed = @{}
    $diffRaw = (& git -C $Wt diff --name-only -z --no-renames --no-ext-diff --no-textconv 2>$null | ForEach-Object { "$_" }) -join ''
    if ($LASTEXITCODE -ne 0) { throw "git diff failed in $Wt" }
    foreach ($name in @($diffRaw -split "`0" | Where-Object { $_ })) { $changed[$name] = $true }
    $candidates = [Collections.Generic.List[string]]::new()
    foreach ($entry in @($staged -split "`0" | Where-Object { $_ }))
    {
        # <mode> SP <oid> SP <stage> TAB <path>; regular files at stage 0 only (not links, not submodules, not conflicted paths).
        $parsed = [regex]::Match($entry, '^(100644|100755) [0-9a-f]+ 0\t(.+)$')
        if (-not $parsed.Success) { continue }
        $path = $parsed.Groups[2].Value
        if ($changed.ContainsKey($path) -or -not (Test-Path -LiteralPath (Join-Path $Wt $path) -PathType Leaf)) { continue }
        $candidates.Add($path)
    }
    if ($candidates.Count -eq 0) { return @() }
    $temp = Join-Path ([IO.Path]::GetTempPath()) "package-rawbytes-$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $temp -Force | Out-Null
    try
    {
        $listing = [Text.UTF8Encoding]::new($false).GetBytes((($candidates | ForEach-Object { "$_`0" }) -join ''))
        $result = Invoke-GitWithInput $Wt @('checkout-index', '-f', '-z', '--stdin', "--prefix=$($temp -replace '\\', '/')/") $listing
        if ($result.ExitCode -ne 0) { throw "git checkout-index failed in ${Wt}: $($result.Error)" }
        $differing = [Collections.Generic.List[string]]::new()
        foreach ($path in $candidates)
        {
            $expected = Join-Path $temp $path
            $actual = Join-Path $Wt $path
            if (-not (Test-Path -LiteralPath $expected -PathType Leaf)) { continue }
            $same = $false
            try
            {
                if ((Get-Item -LiteralPath $expected).Length -eq (Get-Item -LiteralPath $actual).Length) { $same = (Get-Sha256 $expected) -eq (Get-Sha256 $actual) }
            }
            catch { $same = $false }
            if (-not $same) { $differing.Add($path) }
        }
        @($differing | Sort-Object)
    }
    finally
    {
        Get-ChildItem -LiteralPath $temp -Recurse -File -Force -ErrorAction SilentlyContinue | ForEach-Object { $_.IsReadOnly = $false }
        Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Get-SessionIdFromEvents([string]$Path)
{
    if (-not $Path -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    foreach ($line in (Get-Content -LiteralPath $Path))
    {
        if (-not $line) { continue }
        try { $item = $line | ConvertFrom-Json } catch { continue }
        if ($null -eq $item -or $item -isnot [pscustomobject]) { continue }
        foreach ($name in @('thread_id', 'session_id', 'sessionId'))
        {
            if ($item.PSObject.Properties[$name] -and $item.$name) { return [string]$item.$name }
        }
    }
    $null
}

function Get-RunClassification([object]$ExitCode, [string]$Termination, [string]$ReportText, [bool]$ArtifactsPresent, [string]$MissingArtifacts, [string]$ContainmentProblem = '', [string]$ExtraFailure = '')
{
    # Exit status, termination and environment evidence stay separate facts; the outcome is derived from them.
    # env-blocked only applies when the executor reported the blocker with an explicit "ENV-BLOCKED:" line and nothing
    # else failed (exit 0, both artifacts present, containment verified); when unsure the outcome is impl-failed and the
    # evidence is kept. A timeout, a failed kill or an exhausted budget stays impl-failed with that termination; a run is
    # completed only with exit 0, both artifacts present and the process tree verified gone. completed is a statement
    # about the run, not "accepted".
    $line = $null
    foreach ($candidate in ($ReportText -split "`r?`n")) { if ($candidate -match '^\s*\W{0,4}ENV-BLOCKED\W{0,4}\s*:') { $line = $candidate.Trim(); break } }
    $outcome = 'completed'
    $reason = $null
    if ($Termination -ne 'exit') { $outcome = 'impl-failed'; $reason = "termination: $Termination" }
    elseif ($ContainmentProblem) { $outcome = 'impl-failed'; $reason = "containment unverified: $ContainmentProblem" }
    elseif ($line -and $ExitCode -ne 0) { $outcome = 'impl-failed'; $reason = "mixed: implementation failure with environment blocker (exit $ExitCode)" }
    elseif ($line -and -not $ArtifactsPresent) { $outcome = 'impl-failed'; $reason = "mixed: environment blocker reported but artifacts are missing: $MissingArtifacts" }
    elseif ($line) { $outcome = 'env-blocked'; $reason = 'the executor reported ENV-BLOCKED and nothing else failed' }
    elseif ($ExitCode -ne 0) { $outcome = 'impl-failed'; $reason = "exit code $ExitCode" }
    elseif ($ExtraFailure) { $outcome = 'impl-failed'; $reason = $ExtraFailure }
    elseif (-not $ArtifactsPresent) { $outcome = 'impl-failed'; $reason = "missing artifacts: $MissingArtifacts" }
    @{ outcome = $outcome; reason = $reason; envBlocked = [bool]$line; envEvidence = $line }
}

function Get-EventTokens([string]$Path)
{
    # Sums of the turn.completed usage; a field no event reported is null, never 0.
    $sums = @{ input = $null; cached = $null; output = $null }
    $map = @{ input = 'input_tokens'; cached = 'cached_input_tokens'; output = 'output_tokens' }
    if ($Path -and (Test-Path -LiteralPath $Path -PathType Leaf))
    {
        foreach ($line in (Get-Content -LiteralPath $Path))
        {
            if (-not $line) { continue }
            try { $item = $line | ConvertFrom-Json } catch { continue }
            if ($null -eq $item -or $item -isnot [pscustomobject] -or -not $item.PSObject.Properties['type'] -or $item.type -ne 'turn.completed') { continue }
            if (-not $item.PSObject.Properties['usage'] -or -not $item.usage) { continue }
            foreach ($key in $map.Keys)
            {
                $source = $map[$key]
                if ($item.usage.PSObject.Properties[$source] -and $null -ne $item.usage.$source) { $sums[$key] = [long]$sums[$key] + [long]$item.usage.$source }
            }
        }
    }
    $sums
}

function Get-CodexCliVersion
{
    if ($CodexVersion) { return $CodexVersion }
    # The self-test never probes a real binary: a stub path has to pass -CodexVersion.
    if ($env:AW_PKG_SELFTEST) { throw 'self-test guard: Get-CodexCliVersion would run the real codex --version; pass -CodexVersion' }
    try
    {
        $resolved = Resolve-CodexCommand
        if (-not $resolved.Ok) { return $null }
        $version = & $resolved.File @(@($resolved.Prefix) | ForEach-Object { $_.Trim('"') }) --version 2>&1 | Select-Object -First 1
        if ($LASTEXITCODE -eq 0 -and $version) { return ([string]$version).Trim() }
    }
    catch { }
    $null
}

