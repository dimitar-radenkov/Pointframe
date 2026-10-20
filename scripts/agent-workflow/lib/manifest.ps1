#Requires -Version 7.0
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-AwFileHash {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-AwManifest {
    param(
        [Parameter(Mandatory)]
        [string]$Root
    )

    $files = [Collections.Specialized.OrderedDictionary]::new([StringComparer]::Ordinal)
    if (-not (Test-Path -LiteralPath $Root -PathType Container)) {
        return $files
    }

    $paths = [Collections.Generic.List[string]]::new()
    foreach ($file in (Get-ChildItem -LiteralPath $Root -File -Recurse -Force)) {
        if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Managed tree contains a reparse point: $($file.FullName)"
        }
        $relative = $file.FullName.Substring($Root.Length).TrimStart([char[]]@([char]92, [char]47))
        $paths.Add($relative.Replace([string][char]92, '/'))
    }

    $pathArray = $paths.ToArray()
    [Array]::Sort($pathArray, [StringComparer]::Ordinal)
    foreach ($relative in $pathArray) {
        $fullPath = Join-Path $Root ($relative.Replace('/', [IO.Path]::DirectorySeparatorChar))
        $files.Add($relative, (Get-AwFileHash -Path $fullPath))
    }

    return $files
}

function Get-AwManifestHash {
    param(
        [Parameter(Mandatory)]
        [Collections.IDictionary]$Files
    )

    $paths = [string[]]@($Files.Keys)
    [Array]::Sort($paths, [StringComparer]::Ordinal)
    $lines = foreach ($path in $paths) {
        "$path`0$($Files[$path])"
    }
    $text = $lines -join "`n"
    $bytes = [Text.Encoding]::UTF8.GetBytes($text)
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}

function ConvertTo-AwRelativePath {
    param(
        [Parameter(Mandatory)]
        [string]$Root,
        [Parameter(Mandatory)]
        [string]$Path
    )

    return $Path.Substring($Root.Length).TrimStart([char[]]@([char]92, [char]47)).Replace([string][char]92, '/')
}

function Get-AwAttributesBlock {
    return @(
        '# BEGIN agent-workflow managed attributes',
        '/scripts/agent-workflow/** -text',
        '/agent-workflow.lock.json -text',
        '# END agent-workflow managed attributes'
    )
}

function Test-AwAttributesBlock {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $false
    }

    $lines = @([IO.File]::ReadAllText($Path) -split "`r?`n")
    $expected = @(Get-AwAttributesBlock)
    for ($start = 0; $start + $expected.Count -le $lines.Count; $start++) {
        if ($lines[$start] -ceq $expected[0]) {
            $actual = @($lines[$start..($start + $expected.Count - 1)])
            return (($actual -join "`n") -ceq ($expected -join "`n"))
        }
    }
    return $false
}