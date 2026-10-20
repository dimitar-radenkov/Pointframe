#Requires -Version 7.0
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Rendering of the files the kit owns in a consumer: the workflow rules file and the kb skills.
#
# A kit template is Markdown with tokens:
#   {{slot:NAME}}    text the consumer supplies in its slots file; NAME? makes it optional
#   {{local:W-NN}}   the whole text of a rule the consumer owns; the template keeps the rule marker line
#   {{kb.root}}      a value from agent-workflow.json (see Get-AwConfigTokens)
# A token that is alone on its line may expand to several lines; an optional token alone on its line with
# no value removes the line. Slot text is never expanded again.
#
# The slots file holds blocks:
#   <!-- slot:NAME -->
#   text
#   <!-- /slot:NAME -->
# A slot named W-NN supplies a {{local:W-NN}} token. Rules a consumer adds itself (inside any slot) use IDs
# W-800..W-999; W-01..W-799 belong to the kit.

$script:AwTokenPattern = '\{\{([^{}\r\n]+)\}\}'
$script:AwLocalRuleFirst = 800
$script:AwLocalRuleLast = 999

function Read-AwSlots {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $slots = [ordered]@{}
    $errors = [Collections.Generic.List[string]]::new()
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return [pscustomobject]@{ Slots = $slots; Errors = @("slots file not found: $Path") }
    }

    $lines = [IO.File]::ReadAllText($Path) -split "`r?`n"
    $open = $null
    $body = [Collections.Generic.List[string]]::new()
    for ($i = 0; $i -lt $lines.Length; $i++) {
        $line = $lines[$i]
        if ($null -eq $open) {
            if ($line -match '^<!--\s*slot:([A-Za-z0-9][A-Za-z0-9._-]*)\s*-->\s*$') {
                $open = $Matches[1]
                $body.Clear()
                if ($slots.Contains($open)) {
                    $errors.Add("slots line $($i + 1): slot '$open' is defined twice")
                }
            }
            elseif ($line -match '^<!--\s*/slot:') {
                $errors.Add("slots line $($i + 1): closing marker without an opening marker")
            }
            elseif ($line.Trim() -ne '' -and $line -notmatch '^<!--.*-->\s*$') {
                $errors.Add("slots line $($i + 1): text outside a slot block")
            }
            continue
        }
        if ($line -match '^<!--\s*/slot:([A-Za-z0-9][A-Za-z0-9._-]*)\s*-->\s*$') {
            if ($Matches[1] -ne $open) {
                $errors.Add("slots line $($i + 1): slot '$open' is closed by '$($Matches[1])'")
            }
            if (-not $slots.Contains($open)) {
                $slots[$open] = $body -join "`n"
            }
            $open = $null
            continue
        }
        if ($line -match '^<!--\s*slot:') {
            $errors.Add("slots line $($i + 1): slot '$open' is not closed before the next slot")
            $open = $null
            $i--
            continue
        }
        $body.Add($line)
    }
    if ($null -ne $open) {
        $errors.Add("slots file ends inside slot '$open'")
    }
    return [pscustomobject]@{ Slots = $slots; Errors = @($errors) }
}

function Get-AwConfigTokens {
    param(
        [Parameter(Mandatory)]
        [Collections.IDictionary]$Config
    )

    $baseRef = [string]$Config.repository.baseRef
    $slash = $baseRef.IndexOf('/')
    $tokens = [ordered]@{
        'kb.root' = ([string]$Config.kb.root).Replace('\', '/')
        'kb.core' = ([string]$Config.kb.core).Replace('\', '/')
        'kb.features' = ([string]$Config.kb.features).Replace('\', '/')
        'kb.lessons' = ([string]$Config.kb.lessons).Replace('\', '/')
        'kb.baseline' = ([string]$Config.kb.baseline).Replace('\', '/')
        'kb.tasks' = ([string]$Config.kb.tasks).Replace('\', '/')
        'repository.id' = [string]$Config.repository.id
        'repository.baseRef' = $baseRef
        'repository.baseBranch' = $(if ($slash -ge 0) { $baseRef.Substring($slash + 1) } else { $baseRef })
        'state.commonDirNamespace' = [string]$Config.state.commonDirNamespace
        'status.store' = ([string]$Config.status.store).Replace('\', '/')
    }
    $workflow = "$($tokens['kb.root'])/workflow.md"
    if ($Config.kb.Contains('render') -and $null -ne $Config.kb.render) {
        $workflow = ([string]$Config.kb.render.workflow).Replace('\', '/')
    }
    $tokens['kb.workflow'] = $workflow
    return $tokens
}

function Expand-AwTemplate {
    param(
        [Parameter(Mandatory)]
        [string]$Template,
        [Parameter(Mandatory)]
        [string]$Name,
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [Collections.IDictionary]$Slots,
        [Parameter(Mandatory)]
        [Collections.IDictionary]$Tokens,
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [Collections.Generic.HashSet[string]]$Used,
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [Collections.Generic.List[string]]$Errors
    )

    $resolve = {
        param([string]$Token)
        $optional = $false
        $body = $Token
        if ($body.EndsWith('?')) {
            $optional = $true
            $body = $body.Substring(0, $body.Length - 1)
        }
        if ($body.StartsWith('slot:')) {
            $key = $body.Substring(5)
            [void]$Used.Add($key)
            if ($Slots.Contains($key)) { return [pscustomobject]@{ Ok = $true; Value = [string]$Slots[$key]; Optional = $optional } }
            if (-not $optional) { $Errors.Add("${Name}: slot '$key' is not defined in the slots file") }
            return [pscustomobject]@{ Ok = $optional; Value = ''; Optional = $optional }
        }
        if ($body.StartsWith('local:')) {
            $key = $body.Substring(6)
            [void]$Used.Add($key)
            if ($Slots.Contains($key)) { return [pscustomobject]@{ Ok = $true; Value = [string]$Slots[$key]; Optional = $false } }
            $Errors.Add("${Name}: rule $key is local to the consumer and has no slot '$key' in the slots file")
            return [pscustomobject]@{ Ok = $false; Value = ''; Optional = $false }
        }
        if ($Tokens.Contains($body)) { return [pscustomobject]@{ Ok = $true; Value = [string]$Tokens[$body]; Optional = $false } }
        $Errors.Add("${Name}: unknown token {{$Token}}")
        return [pscustomobject]@{ Ok = $false; Value = ''; Optional = $false }
    }

    $out = [Collections.Generic.List[string]]::new()
    $lines = $Template.Split("`n")
    foreach ($line in $lines) {
        $alone = [regex]::Match($line, "^$($script:AwTokenPattern)$")
        if ($alone.Success) {
            $r = & $resolve $alone.Groups[1].Value
            if ($r.Optional -and $r.Value -eq '') {
                continue
            }
            $out.Add($r.Value)
            continue
        }
        $out.Add([regex]::Replace($line, $script:AwTokenPattern, {
                    param($m)
                    $r = & $resolve $m.Groups[1].Value
                    return $r.Value
                }))
    }
    return $out -join "`n"
}

function Test-AwSlotRules {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [Collections.IDictionary]$Slots,
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [Collections.Generic.List[string]]$Errors
    )

    foreach ($name in $Slots.Keys) {
        # The knowledge base parser reads these markers case-insensitively, so this check does too.
        foreach ($m in [regex]::Matches([string]$Slots[$name], '<!--\s*rule-section\b[^\r\n]*', 'IgnoreCase')) {
            $Errors.Add("slot '$name' has a rule-section marker, which only the kit templates may create: $($m.Value.Trim())")
        }
        foreach ($m in [regex]::Matches([string]$Slots[$name], '<!--\s*rule:[^\r\n]*', 'IgnoreCase')) {
            $marker = [regex]::Match($m.Value, '^<!--\s*rule:W-(\d{2,3})\s+roles=\S+\s*-->\s*$', 'IgnoreCase')
            if (-not $marker.Success) {
                $Errors.Add("slot '$name' has a malformed rule marker: $($m.Value.Trim())")
                continue
            }
            $number = [int]$marker.Groups[1].Value
            if ($number -lt $script:AwLocalRuleFirst -or $number -gt $script:AwLocalRuleLast) {
                $Errors.Add("slot '$name' defines rule W-$($marker.Groups[1].Value); consumer-defined rules use W-$($script:AwLocalRuleFirst) to W-$($script:AwLocalRuleLast), the kit owns W-01 to W-799")
            }
        }
    }
}

function ConvertTo-AwAgentsSkill {
    # Codex reads skills from .agents/skills: the Claude skill with CLAUDE.md named AGENTS.md and without the
    # Claude Code hook section. LF text in, LF text out.
    param(
        [Parameter(Mandatory)]
        [string]$ClaudeText
    )

    $text = $ClaudeText -replace '`CLAUDE\.md`', '`AGENTS.md`'
    return ($text -replace '(?s)\r?\n## The project hook.*$', '').TrimEnd() + "`n"
}

function Get-AwRenderPlan {
    # What the kit owns in the consumer, as LF text: the workflow file and both copies of every kb skill.
    # Errors lists template, slot, and rule-range problems; Files is empty when there are any.
    param(
        [Parameter(Mandatory)]
        [string]$Root,
        [Parameter(Mandatory)]
        [Collections.IDictionary]$Config,
        [Parameter(Mandatory)]
        [string]$KitDir
    )

    $errors = [Collections.Generic.List[string]]::new()
    $files = [Collections.Generic.List[object]]::new()
    $render = $null
    if ($Config.kb.Contains('render')) {
        $render = $Config.kb.render
    }
    if ($null -eq $render) {
        return [pscustomobject]@{ Configured = $false; Errors = @(); Files = @() }
    }

    $slotsPath = Join-Path $Root ([string]$render.slots)
    $read = Read-AwSlots -Path $slotsPath
    foreach ($e in $read.Errors) { $errors.Add($e) }
    $slots = $read.Slots
    Test-AwSlotRules -Slots $slots -Errors $errors
    $tokens = Get-AwConfigTokens -Config $Config
    $used = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)

    $workflowTemplate = Join-Path $KitDir 'workflow\workflow.template.md'
    $workflowText = [IO.File]::ReadAllText($workflowTemplate) -replace "`r`n", "`n"
    $expanded = Expand-AwTemplate -Template $workflowText -Name 'workflow template' -Slots $slots -Tokens $tokens `
        -Used $used -Errors $errors
    $files.Add([pscustomobject]@{ Kind = 'workflow'; Rel = ([string]$render.workflow).Replace('\', '/'); Text = $expanded })

    $skillsDir = Join-Path $KitDir 'skills'
    foreach ($dir in @(Get-ChildItem -LiteralPath $skillsDir -Directory -Filter 'kb-*' | Sort-Object Name)) {
        $source = Join-Path $dir.FullName 'SKILL.md'
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { continue }
        $template = [IO.File]::ReadAllText($source) -replace "`r`n", "`n"
        $claude = Expand-AwTemplate -Template $template -Name "skill template $($dir.Name)" -Slots $slots -Tokens $tokens `
            -Used $used -Errors $errors
        $claudeRel = ([string]$render.skills.claude).Replace('\', '/').TrimEnd('/') + "/$($dir.Name)/SKILL.md"
        $agentsRel = ([string]$render.skills.agents).Replace('\', '/').TrimEnd('/') + "/$($dir.Name)/SKILL.md"
        $files.Add([pscustomobject]@{ Kind = 'skill'; Rel = $claudeRel; Text = $claude })
        $files.Add([pscustomobject]@{ Kind = 'skill'; Rel = $agentsRel; Text = (ConvertTo-AwAgentsSkill -ClaudeText $claude) })
    }

    foreach ($name in $slots.Keys) {
        if (-not $used.Contains($name)) {
            $errors.Add("slot '$name' is not used by any kit template")
        }
    }
    if ($errors.Count -gt 0) {
        return [pscustomobject]@{ Configured = $true; Errors = @($errors); Files = @() }
    }
    return [pscustomobject]@{ Configured = $true; Errors = @(); Files = @($files) }
}
