# ---- review: criteria, prompt, verdict --------------------------------------------------------------------------

function Get-ReviewCriteria([string]$BriefPath)
{
    # The brief's "Acceptance cases" list, one criterion per bullet or number, numbered C1... in order.
    $items = [Collections.Generic.List[string]]::new()
    $inSection = $false
    $current = $null
    foreach ($line in (Get-Content -LiteralPath $BriefPath))
    {
        if ($line -match '^\s{0,3}#{1,6}\s+(.*?)\s*$')
        {
            if ($inSection) { break }
            if ($Matches[1] -match '^(?i)acceptance(\s+(cases|criteria))?\b') { $inSection = $true }
            continue
        }
        if (-not $inSection) { continue }
        if ($line -match '^\s{0,3}(?:[-*+]|\d+[.)])\s+(.*\S)\s*$')
        {
            if ($current) { $items.Add($current) }
            $current = $Matches[1]
        }
        elseif ($current -and $line -match '^\s+\S') { $current += ' ' + $line.Trim() }
    }
    if ($current) { $items.Add($current) }
    if ($items.Count -eq 0) { throw 'the brief has no "Acceptance cases" list: a review needs criteria to be checked against' }
    $criteria = @()
    for ($i = 0; $i -lt $items.Count; $i++) { $criteria += @(@{ id = "C$($i + 1)"; text = $items[$i] }) }
    $criteria
}

function Get-ExecutorIdentity($State)
{
    # The executors of the reviewed content: every run recorded at commit time (any run since the base that started, whatever its
    # outcome). The latest of them names the executor in the review record.
    $recorded = if ($State.commit) { @(Get-List (Get-Field $State.commit 'executors')) } else { @() }
    $set = @(if ($recorded.Count) { $recorded } else { @(Get-List $State.runs | Where-Object { (Get-Field $_ 'termination') -ne 'launch-failed' } | ForEach-Object { @{ runId = [string]$_.runId; provider = [string]$_.provider; model = [string]$_.model; executorKind = [string](Get-Field $_ 'executorKind'); outcome = [string]$_.outcome } }) })
    if ($set.Count -eq 0) { throw 'no executor run is recorded for this package' }
    $latest = $set[-1]
    $run = @(Get-List $State.runs | Where-Object { $_.runId -eq $latest.runId })[0]
    @{ run = $run; provider = [string]$latest.provider; model = [string]$latest.model; kind = [string]$latest.executorKind; session = $(if ($run) { $run.sessionId } else { $null }); set = $set; models = @($set | ForEach-Object { [string]$_.model } | Select-Object -Unique) }
}

function Test-ReviewerConflict($Entry, $ExecutorInfo)
{
    # $null when the reviewer differs from every model of the executor set, Claude family rule included.
    foreach ($executor in $ExecutorInfo.set)
    {
        if ($Entry.model -ieq [string]$executor.model) { return "it is the model of executor run $($executor.runId) ($($executor.model))" }
        if ($Entry.provider -eq 'claude' -and [string]$executor.provider -eq 'claude' -and (Get-ModelFamily $Entry.model) -eq (Get-ModelFamily ([string]$executor.model))) { return "it is the same Claude family as executor run $($executor.runId) ($($executor.model))" }
    }
    $null
}

function Resolve-ReviewerChoice($ExecutorInfo, [string]$LevelName, [string]$Requested)
{
    # Heavy needs a heavy reviewer (gpt-6.1-sol at high effort, or claude-opus), also when one is requested. The default prefers a
    # provider none of the executors used. The reviewer must differ from every executor model.
    $order = if ($LevelName -eq 'heavy') { @('codex-sol', 'claude-opus') } else { @('claude-sonnet', 'codex-luna', 'claude-opus', 'codex-sol') }
    $kind = $Requested
    if ($kind)
    {
        if ($LevelName -eq 'heavy' -and -not $script:ReviewerCatalog[$kind].heavy) { throw "refused: -Level heavy needs a heavy reviewer (codex-sol at high effort, or claude-opus), not $kind" }
        $conflict = Test-ReviewerConflict $script:ReviewerCatalog[$kind] $ExecutorInfo
        if ($conflict) { throw "refused: the reviewer $kind ($($script:ReviewerCatalog[$kind].model)) cannot review this package: $conflict; a reviewer must differ from every executor model" }
    }
    else
    {
        $executorProviders = @($ExecutorInfo.set | ForEach-Object { [string]$_.provider })
        $ranked = @($order | Where-Object { $script:ReviewerCatalog[$_].provider -notin $executorProviders }) + @($order | Where-Object { $script:ReviewerCatalog[$_].provider -in $executorProviders })
        $kind = @($ranked | Where-Object { -not (Test-ReviewerConflict $script:ReviewerCatalog[$_] $ExecutorInfo) })[0]
        if (-not $kind) { throw "refused: no $LevelName reviewer differs from every executor model ($($ExecutorInfo.models -join ', ')); name one with -Reviewer or reassess" }
    }
    $entry = $script:ReviewerCatalog[$kind]
    @{ kind = $kind; provider = $entry.provider; model = $entry.model; effort = $entry.effort }
}
function New-ReviewPrompt($State, $Review, [string]$VerdictPath, $Prior, [bool]$Resumed = $true)
{
    $template = Join-Path $script:KitDir 'review/package-review-prompt.txt'
    $schema = Join-Path $script:KitDir 'review/package-verdict.schema.json'
    if (-not (Test-Path -LiteralPath $template -PathType Leaf) -or -not (Test-Path -LiteralPath $schema -PathType Leaf)) { throw 'the review prompt template or the verdict schema is missing from the kit' }
    $note = ''
    if ($Prior)
    {
        $tail = 'Inspect the commit above afresh and return a COMPLETE, CURRENT verdict: every criterion with its result and evidence, every finding that still applies (and none that no longer does), and your omissions. Do not answer with only what changed since the last round.'
        if ($Resumed)
        {
            $lines = @("This is round $($Review.round) of a review you started earlier in this session. The branch head may have changed since your last verdict. $tail")
        }
        else
        {
            # A Claude reviewer is never resumed (its earlier session ran in a snapshot that has been removed): it starts fresh and is told
            # what the previous round found.
            # Every field of the earlier record is read with Get-Field: a record of an older version, or one that was interrupted, may lack any of them.
            $before = if ((Get-Field $Prior 'status') -eq 'valid') { "the verdict was $(Get-Field $Prior 'verdict') on commit $(Get-Field $Prior 'commit')" } else { "the review was INVALID, so it gave no verdict ($(@(Get-List (Get-Field $Prior 'invalidReasons')) -join '; '))" }
            $lines = @("This is round $($Review.round) of a review. You are a new session with no memory of round $(Get-Field $Prior 'round'); that round's $before. The branch head may have changed since. What it reported is below, as information only: verify everything yourself. $tail")
        }
        foreach ($old in (Get-List (Get-Field $Prior 'findings')))
        {
            $disposition = Get-Field $old 'disposition'
            $lines += "Earlier finding $(Get-Field $old 'id') ($(Get-Field $old 'severity'), $(Get-Field $old 'file'):$(Get-Field $old 'line')): $(Get-Field $old 'summary')$(if ($disposition) { " - the coordinator decided: $(Get-Field $disposition 'decision') ($(Get-Field $disposition 'reason'))" } else { '' })"
        }
        if (-not $Resumed) { foreach ($old in (Get-List (Get-Field $Prior 'omissions'))) { $lines += "Earlier omission: $old" } }
        $note = $lines -join "`n"
    }
    $text = [IO.File]::ReadAllText($template)
    $values = [ordered]@{
        SLUG = [string]$State.slug; LEVEL = [string]$Review.level; ROUND = [string]$Review.round; WORKTREE = [string]$Review.snapshot.path; BRIEF = (Join-Path ([string]$Review.snapshot.inputDir) 'brief.md'); INPUT_DIR = [string]$Review.snapshot.inputDir
        BASE = [string]$Review.baseSha; COMMIT = [string]$Review.commit; TREE = [string]$Review.tree; VERDICT_FILE = $VerdictPath; RESUME_NOTE = $note
        CRITERIA = ((Get-List $Review.criteria | ForEach-Object { "$($_.id): $($_.text)" }) -join "`n"); SCHEMA = ([IO.File]::ReadAllText($schema).Trim())
    }
    foreach ($key in $values.Keys) { $text = $text.Replace("{{$key}}", [string]$values[$key]) }
    $text
}

function Get-VerdictJsonText([string]$FinalText)
{
    # The verdict is the whole final message. One enclosing code fence is tolerated; nothing else is searched for or stripped.
    $text = $FinalText.Trim()
    if ($text -match '(?s)^```(?:json)?[ \t]*\r?\n(.*?)\r?\n```$') { $text = $Matches[1].Trim() }
    $text
}

function Find-DuplicateJsonProperty([string]$Text)
{
    # ConvertFrom-Json keeps only the last of two equal property names, so a contradiction would vanish. A strict tokenizer
    # (Utf8JsonReader: comments and trailing commas are errors) walks the raw text first. It returns "dup:<name>" for a property
    # repeated within one object at any depth, "error:<message>" for anything it cannot read as strict JSON, and $null for a clean
    # document. An error is never "no duplicate".
    if (-not ('AwJsonDuplicates' -as [type]))
    {
        Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
public static class AwJsonDuplicates
{
    public static string Find(string json)
    {
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json), new JsonReaderOptions { CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false });
        var stack = new Stack<HashSet<string>>();
        try
        {
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject: stack.Push(new HashSet<string>(StringComparer.Ordinal)); break;
                    case JsonTokenType.StartArray: stack.Push(null); break;
                    case JsonTokenType.EndObject: case JsonTokenType.EndArray: stack.Pop(); break;
                    case JsonTokenType.PropertyName:
                        var name = reader.GetString();
                        if (!stack.Peek().Add(name)) { return "dup:" + name; }
                        break;
                }
            }
        }
        catch (Exception e) { return "error:" + e.Message; }
        return null;
    }
}
"@
    }
    [AwJsonDuplicates]::Find($Text)
}
function Test-VerdictDocument([string]$Text, $Criteria, [string]$Commit, [string]$Tree, [bool]$CheckFresh)
{
    # Schema (review/package-verdict.schema.json) plus the checks the host never takes from the model: the reviewed commit and
    # tree equal what the host observed, every criterion of the brief is present once, nothing contradicts itself, and PASS
    # means every criterion pass, no blocking finding and a fresh check.
    $problems = [Collections.Generic.List[string]]::new()
    $result = @{ ok = $false; verdict = $null; problems = $problems; criteria = @(); findings = @(); omissions = @(); blocking = 0 }
    if ([string]::IsNullOrWhiteSpace($Text)) { $problems.Add('the verdict is empty'); return $result }
    $scan = Find-DuplicateJsonProperty $Text
    if ($scan -like 'error:*') { $problems.Add("the verdict is not a JSON document (missing, truncated, free text, or not strict JSON: comments and trailing commas are refused): $($scan.Substring(6))"); return $result }
    if ($scan -like 'dup:*') { $problems.Add("the verdict repeats the property '$($scan.Substring(4))' (the last value would silently win)"); return $result }
    try { $doc = $Text | ConvertFrom-Json -AsHashtable @script:JsonDate -ErrorAction Stop }
    catch { $problems.Add("the verdict is not a JSON document (missing, truncated or free text): $($_.Exception.Message)"); return $result }
    if ($doc -isnot [hashtable]) { $problems.Add('the verdict is not a JSON object'); return $result }
    $allowedKeys = @('schemaVersion', 'reviewedCommit', 'reviewedTree', 'verdict', 'criteria', 'findings', 'omissions')
    # One optional $schema, exactly the draft URL of the schema file, is tolerated because reviewers copy it from there.
    foreach ($key in $doc.Keys) { if ($key -notin $allowedKeys -and $key -cne '$schema') { $problems.Add("unknown top-level key '$key'") } }
    if ($doc.ContainsKey('$schema') -and ($doc['$schema'] -isnot [string] -or $doc['$schema'] -cne 'https://json-schema.org/draft/2020-12/schema')) { $problems.Add('the $schema value, when present, must be exactly https://json-schema.org/draft/2020-12/schema') }
    foreach ($key in $allowedKeys) { if (-not $doc.ContainsKey($key)) { $problems.Add("missing top-level key '$key'") } }
    if ($problems.Count) { return $result }
    $isInt = { param($v) $v -is [int] -or $v -is [long] }
    $isText = { param($v) $v -is [string] -and -not [string]::IsNullOrWhiteSpace($v) }
    if (-not (& $isInt $doc.schemaVersion) -or $doc.schemaVersion -ne 1) { $problems.Add('schemaVersion must be the integer 1') }
    foreach ($key in @('reviewedCommit', 'reviewedTree')) { if ($doc[$key] -isnot [string] -or $doc[$key] -notmatch '^[0-9a-fA-F]{40,64}$') { $problems.Add("$key must be a full object id") } }
    if ($doc.verdict -isnot [string] -or $doc.verdict -cnotin @('PASS', 'FAIL')) { $problems.Add('verdict must be PASS or FAIL') }
    foreach ($key in @('criteria', 'findings', 'omissions')) { if ($doc[$key] -isnot [array]) { $problems.Add("$key must be an array") } }
    if ($problems.Count) { return $result }

    $criteriaOut = @()
    $seen = @{}
    $index = 0
    foreach ($item in $doc.criteria)
    {
        $index++
        if ($item -isnot [hashtable]) { $problems.Add("criteria[$index] is not an object"); continue }
        $extraKeys = @($item.Keys | Where-Object { $_ -notin @('id', 'result', 'evidence') })
        $missingKeys = @(@('id', 'result', 'evidence') | Where-Object { -not $item.ContainsKey($_) })
        if ($extraKeys.Count -or $missingKeys.Count) { $problems.Add("criteria[$index] must have exactly id, result and evidence (extra: $($extraKeys -join ','); missing: $($missingKeys -join ','))"); continue }
        if (-not (& $isText $item.id)) { $problems.Add("criteria[$index].id must be a non-empty string"); continue }
        if ($item.result -isnot [string] -or $item.result -cnotin @('pass', 'fail', 'not-checked')) { $problems.Add("criterion $($item.id): result must be pass, fail or not-checked") }
        if (-not (& $isText $item.evidence)) { $problems.Add("criterion $($item.id): evidence must be a non-empty string") }
        if ($seen.ContainsKey($item.id)) { $problems.Add("criterion $($item.id) appears twice") }
        $seen[$item.id] = $true
        $criteriaOut += @(@{ id = $item.id; result = $item.result; evidence = $item.evidence })
    }
    foreach ($expected in $Criteria) { if (-not $seen.ContainsKey($expected.id)) { $problems.Add("criterion $($expected.id) of the brief is missing from the verdict") } }
    foreach ($id in $seen.Keys) { if (-not @($Criteria | Where-Object { $_.id -eq $id })) { $problems.Add("the verdict names criterion $id, which the brief does not have") } }

    $findingsOut = @()
    $seenFindings = @{}
    $index = 0
    foreach ($item in $doc.findings)
    {
        $index++
        if ($item -isnot [hashtable]) { $problems.Add("findings[$index] is not an object"); continue }
        $required = @('id', 'severity', 'file', 'line', 'summary', 'scenario')
        $extraKeys = @($item.Keys | Where-Object { $_ -notin $required })
        $missingKeys = @($required | Where-Object { -not $item.ContainsKey($_) })
        if ($extraKeys.Count -or $missingKeys.Count) { $problems.Add("findings[$index] must have exactly id, severity, file, line, summary and scenario (extra: $($extraKeys -join ','); missing: $($missingKeys -join ','))"); continue }
        foreach ($key in @('id', 'file', 'summary', 'scenario')) { if (-not (& $isText $item[$key])) { $problems.Add("findings[$index].$key must be a non-empty string") } }
        if ($item.severity -isnot [string] -or $item.severity -cnotin @('blocking', 'non-blocking')) { $problems.Add("findings[$index].severity must be blocking or non-blocking") }
        if (-not (& $isInt $item.line) -or $item.line -lt 0) { $problems.Add("findings[$index].line must be an integer >= 0") }
        if ($item.id -is [string] -and $seenFindings.ContainsKey($item.id)) { $problems.Add("finding $($item.id) appears twice") }
        if ($item.id -is [string]) { $seenFindings[$item.id] = $true }
        $findingsOut += @(@{ id = $item.id; severity = $item.severity; file = $item.file; line = $item.line; summary = $item.summary; scenario = $item.scenario; disposition = $null })
    }
    $omissionsOut = @()
    foreach ($item in $doc.omissions)
    {
        if ($item -isnot [string]) { $problems.Add('omissions must be strings'); break }
        $omissionsOut += @($item)
    }
    if ($problems.Count) { return $result }

    if ($doc.reviewedCommit -ine $Commit) { $problems.Add("reviewedCommit $($doc.reviewedCommit) is not the host-observed head $Commit") }
    if ($doc.reviewedTree -ine $Tree) { $problems.Add("reviewedTree $($doc.reviewedTree) is not the host-observed tree $Tree") }
    $blocking = @($findingsOut | Where-Object { $_.severity -eq 'blocking' }).Count
    $notPass = @($criteriaOut | Where-Object { $_.result -ne 'pass' })
    if ($doc.verdict -ceq 'PASS')
    {
        if ($notPass.Count) { $problems.Add("contradiction: verdict PASS but criteria $((($notPass | ForEach-Object { "$($_.id)=$($_.result)" }) -join ', '))") }
        if ($blocking) { $problems.Add("contradiction: verdict PASS with $blocking blocking finding(s)") }
        if (-not $CheckFresh) { $problems.Add('PASS needs a fresh passing check for the reviewed tree and there is none') }
    }
    elseif (@($criteriaOut | Where-Object { $_.result -ne 'pass' }).Count -eq 0 -and $blocking -eq 0) { $problems.Add('contradiction: verdict FAIL but every criterion passes and no finding is blocking') }
    $result.criteria = $criteriaOut; $result.findings = $findingsOut; $result.omissions = $omissionsOut; $result.blocking = $blocking
    if ($problems.Count -eq 0) { $result.ok = $true; $result.verdict = [string]$doc.verdict }
    $result
}

