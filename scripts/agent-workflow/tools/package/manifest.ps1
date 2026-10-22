# ---- Manifest and snapshots -------------------------------------------------------------------------------------

function Assert-RelativePath([string]$Value, [string]$Where)
{
    if (-not $Value -or [IO.Path]::IsPathRooted($Value) -or $Value -match '(^|[\\/])\.\.([\\/]|$)') { throw "manifest $Where must be a repository-relative path, got '$Value'" }
}

function Read-Manifest([string]$Path)
{
    $raw = if ($Path) { Get-Content -LiteralPath $Path -Raw } else { '{}' }
    try { $source = $raw | ConvertFrom-Json -AsHashtable @script:JsonDate } catch { throw "check manifest is not valid JSON: $($_.Exception.Message)" }
    if ($source -isnot [hashtable]) { throw 'check manifest must be a JSON object' }
    $unknown = @($source.Keys | Where-Object { $_ -notin @('build', 'tests', 'format', 'extra', 'version', 'allowedPaths', 'allowedScripts') })
    if ($unknown.Count) { throw "check manifest has unknown key(s): $($unknown -join ', ')" }
    $manifest = [ordered]@{ version = 1; build = @(); tests = @(); format = @(); extra = @(); allowedPaths = @(); allowedScripts = @() }
    foreach ($item in @($source['allowedScripts']) | Where-Object { $_ })
    {
        $script = ([string]$item) -replace '\\', '/'
        Assert-RelativePath $script 'allowed script'
        if ($script -notmatch '^scripts/[A-Za-z0-9_./-]+\.ps1$' -or $script -match '(^|/)\.\.?(/|$)') { throw "manifest allowed script must be scripts/<name>.ps1 without '..', got '$item'" }
        $manifest.allowedScripts += $script
    }
    foreach ($item in @($source['allowedPaths']) | Where-Object { $_ }) { Assert-RelativePath ([string]$item) 'allowed path'; $manifest.allowedPaths += ([string]$item -replace '\\', '/') }
    foreach ($item in @($source['build']) | Where-Object { $_ }) { Assert-RelativePath ([string]$item) 'build target'; $manifest.build += [string]$item }
    foreach ($item in @($source['format']) | Where-Object { $_ }) { Assert-RelativePath ([string]$item) 'format target'; $manifest.format += [string]$item }
    foreach ($item in @($source['tests']) | Where-Object { $_ })
    {
        $project = if ($item -is [hashtable]) { [string]$item['project'] } else { [string]$item }
        $filter = if ($item -is [hashtable] -and $item['filter']) { [string]$item['filter'] } else { '' }
        Assert-RelativePath $project 'test project'
        $manifest.tests += @{ project = $project; filter = $filter }
    }
    foreach ($item in @($source['extra']) | Where-Object { $_ })
    {
        if ($item -isnot [hashtable] -or -not $item['name'] -or -not $item['exe']) { throw 'manifest extra entries need name and exe' }
        $manifest.extra += @{ name = [string]$item['name']; exe = [string]$item['exe']; mutates = [bool]$item['mutates']; args = @(@($item['args']) | Where-Object { $null -ne $_ } | ForEach-Object { [string]$_ }) }
    }
    $manifest
}

function Get-ManifestPaths($Manifest)
{
    @($Manifest.build) + @($Manifest.format) + @($Manifest.tests | ForEach-Object { $_.project }) | Select-Object -Unique
}

function Test-SnapshotsIntact($State)
{
    $problems = [Collections.Generic.List[string]]::new()
    foreach ($key in @('brief', 'manifest'))
    {
        $snap = $State[$key]
        if (-not (Test-Path -LiteralPath $snap.path -PathType Leaf)) { $problems.Add("$key snapshot is missing: $($snap.path)") }
        elseif ((Get-Sha256 $snap.path) -ne $snap.sha256) { $problems.Add("$key snapshot changed since 'new' (hash mismatch): $($snap.path)") }
    }
    @($problems)
}

