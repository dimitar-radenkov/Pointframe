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
