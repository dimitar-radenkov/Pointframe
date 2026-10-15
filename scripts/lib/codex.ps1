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

function Test-CodexRunPolicy
{
    param([string]$Model, [string]$Effort, [string]$EscalationReason, [string]$Sandbox, [bool]$AllowStrongReadOnly = $false)
    $reasons = [Collections.Generic.List[string]]::new()
    if ($Model -notin @('gpt-6-luna', 'gpt-6.1-sol'))
    { if ($Model -match 'astra|5\.') { $reasons.Add("model '$Model' is refused; the owner must approve it") } else { $reasons.Add("model '$Model' is not allowed") } }
    if (($Effort -in @('high', 'xhigh', 'max') -or $Model -eq 'gpt-6.1-sol') -and [string]::IsNullOrWhiteSpace($EscalationReason) -and (-not $AllowStrongReadOnly -or $Sandbox -eq 'workspace-write'))
    { $reasons.Add('high effort and gpt-6.1-sol require -EscalationReason for workspace-write') }
    if ($Sandbox -notin @('workspace-write', 'read-only')) { $reasons.Add("sandbox '$Sandbox' is not accepted") }
    [pscustomobject]@{ Ok = ($reasons.Count -eq 0); Reasons = @($reasons) }
}
