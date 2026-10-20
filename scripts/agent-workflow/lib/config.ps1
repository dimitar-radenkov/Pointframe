#Requires -Version 7.0
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Read-AwConfig {
    param(
        [Parameter(Mandatory)]
        [string]$Root
    )

    if (-not [IO.Path]::IsPathFullyQualified($Root)) {
        throw 'Root must be an absolute path.'
    }

    $configPath = Join-Path $Root 'agent-workflow.json'
    $schemaPath = Join-Path $PSScriptRoot '..\schema\agent-workflow.schema.json'
    if (-not (Test-Path -LiteralPath $configPath -PathType Leaf)) {
        throw "Configuration not found: $configPath"
    }

    try {
        $json = Get-Content -LiteralPath $configPath -Raw
        $config = ConvertFrom-Json -InputObject $json -AsHashtable -Depth 100
    }
    catch {
        throw "Invalid JSON in ${configPath}: $($_.Exception.Message)"
    }

    $schema = Get-Content -LiteralPath $schemaPath -Raw | ConvertFrom-Json -AsHashtable -Depth 100
    $messages = @(Get-AwSchemaErrors -Value $config -Schema $schema -Definitions $schema.definitions -Path '')
    if ($messages.Count -gt 0) {
        throw "Invalid agent-workflow.json:`n - $($messages -join "`n - ")"
    }

    $ids = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($gate in $config.gates) {
        if (-not $ids.Add([string]$gate.id)) {
            throw "Duplicate gate id '$($gate.id)' in agent-workflow.json."
        }
    }

    foreach ($gate in $config.gates) {
        foreach ($dependency in $gate.dependsOn) {
            if (-not $ids.Contains([string]$dependency)) {
                throw "Gate '$($gate.id)' depends on unknown gate '$dependency'."
            }
        }
    }

    foreach ($value in (Get-StringValues -Value $config)) {
        if ($value -match '\$\(|\$\{') {
            throw 'Configuration strings must not contain expression interpolation ($(...), ${...}).'
        }
    }

    return $config
}

function Get-StringValues {
    param(
        [Parameter(Mandatory)]
        [AllowNull()]
        [object]$Value
    )

    if ($null -eq $Value) {
        return @()
    }

    if ($Value -is [string]) {
        return ,$Value
    }

    if ($Value -is [Collections.IDictionary]) {
        $strings = [Collections.Generic.List[string]]::new()
        foreach ($item in $Value.Values) {
            foreach ($string in (Get-StringValues -Value $item)) {
                $strings.Add($string)
            }
        }
        return $strings.ToArray()
    }

    if ($Value -is [Collections.IEnumerable]) {
        $strings = [Collections.Generic.List[string]]::new()
        foreach ($item in $Value) {
            foreach ($string in (Get-StringValues -Value $item)) {
                $strings.Add($string)
            }
        }
        return $strings.ToArray()
    }

    return @()
}

function Get-AwSchemaErrors {
    <#
    Test-Json (PowerShell 7.6) reports only the top-level failure of a nested document, so this function
    asks Test-Json whether a node is valid and, when it is not, descends into the children to name every
    failing leaf with its JSON path.
    #>
    param(
        [Parameter(Mandatory)]
        [AllowNull()]
        [object]$Value,
        [Parameter(Mandatory)]
        [Collections.IDictionary]$Schema,
        [Parameter(Mandatory)]
        [Collections.IDictionary]$Definitions,
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$Path
    )

    $resolved = $Schema
    if ($Schema.Contains('$ref')) {
        $resolved = $Definitions[([string]$Schema['$ref']).Substring('#/definitions/'.Length)]
    }

    $standalone = [ordered]@{} + $resolved
    $standalone['definitions'] = $Definitions
    $nodeJson = ConvertTo-Json -InputObject $Value -Depth 100 -Compress
    $nodeErrors = @()
    $valid = Test-Json -Json $nodeJson -Schema (ConvertTo-Json -InputObject $standalone -Depth 100) `
        -ErrorAction SilentlyContinue -ErrorVariable nodeErrors
    if ($valid) {
        return @()
    }

    $where = if ($Path) { $Path } else { '/' }
    $found = [Collections.Generic.List[string]]::new()
    if ($Value -is [Collections.IDictionary]) {
        foreach ($required in @($resolved['required'])) {
            if ($required -and -not $Value.Contains($required)) {
                $found.Add("${where}: missing required property '$required'")
            }
        }
        foreach ($key in $Value.Keys) {
            if ($resolved.Contains('properties') -and $resolved['properties'].Contains($key)) {
                foreach ($message in (Get-AwSchemaErrors -Value $Value[$key] -Schema $resolved['properties'][$key] `
                        -Definitions $Definitions -Path "$Path/$key")) {
                    $found.Add($message)
                }
            }
            elseif ($resolved['additionalProperties'] -eq $false) {
                $found.Add("${where}: unknown property '$key'")
            }
        }
    }
    elseif (($Value -is [Collections.IEnumerable]) -and ($Value -isnot [string]) -and $resolved.Contains('items')) {
        $index = 0
        foreach ($item in $Value) {
            foreach ($message in (Get-AwSchemaErrors -Value $item -Schema $resolved['items'] `
                    -Definitions $Definitions -Path "$Path/$index")) {
                $found.Add($message)
            }
            $index++
        }
    }

    if ($found.Count -eq 0) {
        $detail = ($nodeErrors | ForEach-Object { $_.Exception.Message }) -join '; '
        if (-not $detail) {
            $detail = 'value does not satisfy the schema'
        }
        $found.Add("${where}: $detail")
    }
    return $found.ToArray()
}