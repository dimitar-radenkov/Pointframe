#Requires -Version 7.0
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Content-addressed verify receipts. A complete passing verdict is a fact about content: this working tree (including uncommitted changes), checked by
# these gates (gates and verify.* of the configuration), under this vendored kit (the manifestSha256 of the lock), by this verify tool. The same four
# inputs give the same verdict, so a receipt is stored under their SHA-256 and reused instead of running every gate again. Partial, failing and
# filtered runs are never stored.

function ConvertTo-AwCanonicalJson {
    # Deterministic text of a configuration value: dictionary keys in ordinal order, arrays in order, scalars as JSON.
    param($Value)

    if ($null -eq $Value) {
        return 'null'
    }
    if ($Value -is [Collections.IDictionary]) {
        $keys = [string[]]@($Value.Keys | ForEach-Object { [string]$_ })
        [Array]::Sort($keys, [StringComparer]::Ordinal)
        $parts = foreach ($key in $keys) {
            ($key | ConvertTo-Json -Compress) + ':' + (ConvertTo-AwCanonicalJson $Value[$key])
        }
        return '{' + ($parts -join ',') + '}'
    }
    if ($Value -is [string] -or $Value -is [ValueType]) {
        return ConvertTo-Json -InputObject $Value -Compress
    }
    if ($Value -is [Collections.IEnumerable]) {
        return '[' + ((@($Value) | ForEach-Object { ConvertTo-AwCanonicalJson $_ }) -join ',') + ']'
    }
    return ConvertTo-Json -InputObject $Value -Compress
}

function Get-AwTextSha256 {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$Text
    )

    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Text))).ToLowerInvariant()
}

function Get-AwVerifyConfigHash {
    # The normalized gate configuration: the gates and the verify section of the effective configuration.
    param(
        [Parameter(Mandatory)]
        [Collections.IDictionary]$Config
    )

    $gates = if ($Config.Contains('gates')) { $Config['gates'] } else { $null }
    $verify = if ($Config.Contains('verify')) { $Config['verify'] } else { $null }
    return Get-AwTextSha256 (ConvertTo-AwCanonicalJson ([ordered]@{ gates = $gates; verify = $verify }))
}

function Get-AwKitManifestHash {
    # manifestSha256 of the lock of the vendored kit in Root; empty when the repository has no lock (the kit source, a test repository).
    param(
        [Parameter(Mandatory)]
        [string]$Root
    )

    $path = Join-Path $Root 'agent-workflow.lock.json'
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        return ''
    }
    try {
        $lock = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -AsHashtable
    }
    catch {
        return 'unreadable-lock'
    }
    if ($lock -is [Collections.IDictionary] -and $lock.Contains('manifestSha256')) {
        return [string]$lock['manifestSha256']
    }
    return 'no-manifest'
}

function Get-AwVerifyInputs {
    param(
        [Parameter(Mandatory)]
        [Collections.IDictionary]$Config,
        [Parameter(Mandatory)]
        [string]$Root,
        [Parameter(Mandatory)]
        [string]$TreeHash,
        [Parameter(Mandatory)]
        [string]$VerifyToolSha256,
        [AllowEmptyString()]
        [string]$DotnetVersion = '',
        [string]$PwshVersion = $PSVersionTable.PSVersion.ToString()
    )

    return [ordered]@{
        treeHash = $TreeHash
        configSha256 = Get-AwVerifyConfigHash -Config $Config
        kitManifestSha256 = Get-AwKitManifestHash -Root $Root
        verifyToolSha256 = $VerifyToolSha256
        dotnetVersion = $DotnetVersion
        pwshVersion = $PwshVersion
    }
}

$script:AwVerifyInputNames = @('treeHash', 'configSha256', 'kitManifestSha256', 'verifyToolSha256', 'dotnetVersion', 'pwshVersion')

function Get-AwVerifyToolSha256 {
    # The verify tool as the key sees it: verify.ps1 and every library it dot-sources (kit/lib/<name>.ps1), each by SHA-256, so that a change in
    # any code that decides the verdict is a different key.
    param(
        [Parameter(Mandatory)]
        [string]$ToolPath
    )

    $text = [IO.File]::ReadAllText($ToolPath)
    $libDirectory = Join-Path (Split-Path -Parent (Split-Path -Parent $ToolPath)) 'lib'
    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add('tool=' + (Get-FileHash -LiteralPath $ToolPath -Algorithm SHA256).Hash.ToLowerInvariant())
    $names = @([regex]::Matches($text, "\.\s+\(Join-Path \`$PSScriptRoot '\.\.' 'lib' '([A-Za-z0-9._-]+\.ps1)'\)") | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique -CaseSensitive)
    foreach ($name in $names) {
        $path = Join-Path $libDirectory $name
        $hash = if (Test-Path -LiteralPath $path -PathType Leaf) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() } else { 'missing' }
        $lines.Add("lib/$name=$hash")
    }
    return Get-AwTextSha256 ($lines -join "`n")
}

function Get-AwDotnetVersion {
    # The dotnet SDK version as verify.ps1 -ReuseIfFresh reads it; empty when there is no dotnet.
    if (Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue) {
        return ([string](& dotnet --version 2>$null | Select-Object -First 1)).Trim()
    }
    return ''
}

function Get-AwVerifyKey {
    param(
        [Parameter(Mandatory)]
        $Inputs
    )

    return Get-AwTextSha256 (($script:AwVerifyInputNames | ForEach-Object { "$_=$($Inputs.$_)" }) -join "`n")
}

function ConvertFrom-AwJsonText {
    # JSON with dates kept as the text they were written as: a timestamp with an offset must come back with its offset.
    param(
        [Parameter(Mandatory)]
        [string]$Text
    )

    if ((Get-Command ConvertFrom-Json).Parameters.ContainsKey('DateKind')) {
        return $Text | ConvertFrom-Json -DateKind String
    }
    return $Text | ConvertFrom-Json
}

function ConvertTo-AwIsoString {
    param($Value)

    if ($Value -is [DateTimeOffset]) { return $Value.ToString('o') }
    if ($Value -is [DateTime]) { return ([DateTimeOffset]$Value).ToString('o') }
    return [string]$Value
}

function Remove-AwVerifyCache {
    # A run of these inputs that did not pass: a stored pass for the same key is no longer true and is deleted.
    param(
        [Parameter(Mandatory)]
        [string]$Directory,
        [Parameter(Mandatory)]
        $Inputs
    )

    $path = Join-Path $Directory ((Get-AwVerifyKey -Inputs $Inputs) + '.json')
    if (Test-Path -LiteralPath $path -PathType Leaf) {
        Remove-Item -LiteralPath $path -Force
        return $true
    }
    return $false
}

function Get-AwVerifyCacheDirectory {
    param(
        [Parameter(Mandatory)]
        $Context
    )

    return Join-Path $Context.CommonDir (Join-Path ([string]$Context.Config.state.commonDirNamespace) 'verify-cache')
}

function Save-AwVerifyCache {
    # Stores a complete passing receipt under the key of its inputs. The receipt is kept as text with its SHA-256 beside it, so that an edit of the
    # file is found when it is read.
    param(
        [Parameter(Mandatory)]
        [string]$Directory,
        [Parameter(Mandatory)]
        $Inputs,
        [Parameter(Mandatory)]
        [string]$ReceiptJson
    )

    New-Item -ItemType Directory -Path $Directory -Force | Out-Null
    $key = Get-AwVerifyKey -Inputs $Inputs
    $record = [ordered]@{
        schemaVersion = 1
        key = $key
        inputs = $Inputs
        receiptSha256 = Get-AwTextSha256 $ReceiptJson
        receiptJson = $ReceiptJson
    }
    $path = Join-Path $Directory "$key.json"
    $temp = "$path.$([guid]::NewGuid().ToString('N')).tmp"
    [IO.File]::WriteAllText($temp, ($record | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temp -Destination $path -Force
    # The newest hundred entries are kept.
    $old = @(Get-ChildItem -LiteralPath $Directory -Filter '*.json' -File | Sort-Object LastWriteTimeUtc -Descending | Select-Object -Skip 100)
    foreach ($file in $old) {
        Remove-Item -LiteralPath $file.FullName -Force -ErrorAction SilentlyContinue
    }
}

function Read-AwVerifyCache {
    # The cached receipt for these inputs, or $null with the reason. A record is trusted only when its key, its recorded inputs, the SHA-256 of its
    # receipt text and the receipt itself (a complete pass of exactly this tree, with these inputs) all agree.
    param(
        [Parameter(Mandatory)]
        [string]$Directory,
        [Parameter(Mandatory)]
        $Inputs
    )

    $key = Get-AwVerifyKey -Inputs $Inputs
    $path = Join-Path $Directory "$key.json"
    $refuse = { param($why) [pscustomobject]@{ Receipt = $null; Reason = $why } }
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        return & $refuse 'missing'
    }
    try {
        $record = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        if ([string]$record.key -cne $key) { return & $refuse 'the record names another key' }
        foreach ($name in $script:AwVerifyInputNames) {
            if ([string]$record.inputs.$name -cne [string]$Inputs.$name) { return & $refuse "the recorded input $name differs" }
        }
        if ((Get-AwTextSha256 ([string]$record.receiptJson)) -cne [string]$record.receiptSha256) { return & $refuse 'the receipt text does not match its recorded SHA-256 (the file was edited)' }
        $receipt = ConvertFrom-AwJsonText ([string]$record.receiptJson)
        if ([string]$receipt.status -cne 'pass' -or -not $receipt.complete -or $receipt.filter) { return & $refuse 'the receipt is not a complete pass' }
        if ([string]$receipt.treeHash -cne [string]$Inputs.treeHash) { return & $refuse 'the receipt is for another tree' }
        if ((Get-AwVerifyKey -Inputs $receipt.inputs) -cne $key) { return & $refuse 'the inputs inside the receipt do not give this key' }
    }
    catch {
        return & $refuse "unreadable: $($_.Exception.Message)"
    }

    return [pscustomobject]@{ Receipt = $receipt; Reason = $null }
}

function Test-AwReceiptContent {
    # The merge gate's content rule: a complete passing receipt whose treeHash is the committed tree of the head, with the working tree equal to it, and
    # whose recorded inputs are those of the main checkout (configuration, kit) and of this verify tool. The receipt's own head is not compared.
    param(
        $Receipt,
        [Parameter(Mandatory)]
        [string]$HeadTree,
        [Parameter(Mandatory)]
        [string]$CurrentTree,
        [Parameter(Mandatory)]
        [Collections.IDictionary]$Config,
        [Parameter(Mandatory)]
        [string]$MainRoot,
        [Parameter(Mandatory)]
        [string]$VerifyToolPath
    )

    $result = { param($ok, $reason) [pscustomobject]@{ Ok = $ok; Reason = $reason } }
    if (-not $Receipt) { return & $result $false 'missing receipt' }
    if (-not $Receipt.PSObject.Properties['inputs'] -or -not $Receipt.PSObject.Properties['cacheKey']) { return & $result $false 'head mismatch (the receipt records no content inputs)' }
    $status = if ($Receipt.PSObject.Properties['status']) { [string]$Receipt.status } else { '' }
    if ($status -ne 'pass') { return & $result $false "not pass (status '$status')" }
    if (-not $Receipt.PSObject.Properties['complete'] -or -not $Receipt.complete -or ($Receipt.PSObject.Properties['filter'] -and $Receipt.filter)) { return & $result $false 'partial receipt' }
    if ([string]$Receipt.treeHash -cne $HeadTree -or $CurrentTree -cne $HeadTree) { return & $result $false 'tree mismatch (the receipt, the worktree and the committed tree of the head must be one tree)' }
    $expected = Get-AwVerifyInputs -Config $Config -Root $MainRoot -TreeHash $HeadTree -VerifyToolSha256 (Get-AwVerifyToolSha256 -ToolPath $VerifyToolPath) -DotnetVersion (Get-AwDotnetVersion)
    foreach ($name in $script:AwVerifyInputNames) {
        if (-not $Receipt.inputs.PSObject.Properties[$name] -or [string]$Receipt.inputs.$name -cne [string]$expected[$name]) { return & $result $false "head mismatch (the receipt was made under another $name than the main checkout's)" }
    }
    if ((Get-AwVerifyKey -Inputs $Receipt.inputs) -cne [string]$Receipt.cacheKey) { return & $result $false 'head mismatch (the receipt key does not match its inputs)' }
    return & $result $true 'receipt matches the content of the head tree (configuration, kit and verify tool of the main checkout)'
}
