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

function Get-AwTextAttributeViolations {
    # The paths (relative to Root, slash separated) whose `text` attribute is not unset for Git. The managed block marks the
    # vendored tree and the lock `-text`; a rule the consumer adds after the block (for example `*.ps1 text eol=lf`) wins and
    # silently defeats it, so the block being present is not enough. Reads stdout only, because NUL-separated output must
    # not be mixed with warnings from stderr.
    param(
        [Parameter(Mandatory)]
        [string]$Root,
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [string[]]$Paths
    )

    $violations = [Collections.Generic.List[string]]::new()
    $previous = [Console]::OutputEncoding
    try {
        [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
        for ($offset = 0; $offset -lt $Paths.Count; $offset += 40) {
            $last = [Math]::Min($offset + 39, $Paths.Count - 1)
            $batch = @($Paths[$offset..$last])
            $raw = & git -C $Root check-attr -z text -- @batch 2>$null
            if ($LASTEXITCODE -ne 0) {
                throw "git check-attr failed in $Root."
            }
            $fields = @(([string]($raw -join "`n")) -split "`0")
            for ($i = 0; $i + 2 -lt $fields.Count; $i += 3) {
                if ($fields[$i + 2] -cne 'unset') {
                    $violations.Add($fields[$i])
                }
            }
        }
    }
    finally {
        [Console]::OutputEncoding = $previous
    }

    return @($violations)
}

function Get-AwInstallLeftovers {
    # Install working paths a failed or half-cleaned install can leave in the consumer root. None of them may be committed.
    param(
        [Parameter(Mandatory)]
        [string]$Root
    )

    $found = [Collections.Generic.List[string]]::new()
    foreach ($item in (Get-ChildItem -LiteralPath $Root -Force -ErrorAction SilentlyContinue)) {
        if ($item.Name -clike '.agent-workflow.*') {
            $found.Add($item.Name)
        }
    }

    return @($found)
}

function Test-AwFileHasBom {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $false
    }
    $head = [byte[]]::new(3)
    $stream = [IO.File]::OpenRead($Path)
    try {
        $count = $stream.Read($head, 0, 3)
    }
    finally {
        $stream.Dispose()
    }

    return ($count -eq 3 -and $head[0] -eq 0xEF -and $head[1] -eq 0xBB -and $head[2] -eq 0xBF)
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