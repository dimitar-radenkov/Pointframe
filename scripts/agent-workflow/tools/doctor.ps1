#Requires -Version 7.0
[CmdletBinding()]
param(
    [string]$Root,
    [string]$Source,
    [switch]$WriteWrappers,
    [switch]$RequireRelease,
    [switch]$Json
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# Offline integrity check of the vendored kit: the files against the lock, that the managed paths are really -text for Git,
# leftovers of an install, and the configuration. -Source <absolute path of a kit clone> (optional, offline) also proves
# that the lock's commit exists in that clone under the lock's tag and that manifestSha256 and every file hash equal the
# kit/ folder of that commit. That catches a tamper that rewrites both the vendored files and the lock, which the lock
# alone cannot show.
. (Join-Path $PSScriptRoot '..\lib\root.ps1')
. (Join-Path $PSScriptRoot '..\lib\config.ps1')
. (Join-Path $PSScriptRoot '..\lib\manifest.ps1')
. (Join-Path $PSScriptRoot '..\lib\wrapper.ps1')

function Add-AwIssue {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [Collections.Generic.List[object]]$Issues,
        [Parameter(Mandatory)]
        [string]$Status,
        [Parameter(Mandatory)]
        [string]$Path,
        [string]$Message
    )

    $issue = [ordered]@{ status = $Status; path = $Path }
    if ($Message) {
        $issue.message = $Message
    }
    $Issues.Add([pscustomobject]$issue)
}

function Test-AwLockAgainstSource {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [Collections.Generic.List[object]]$Issues,
        [Parameter(Mandatory)]
        [Collections.IDictionary]$Lock,
        [Parameter(Mandatory)]
        [string]$SourceRoot
    )

    $commit = [string]$Lock.commit
    $tag = [string]$Lock.tag
    $lockPath = 'agent-workflow.lock.json'
    $null = & git -C $SourceRoot rev-parse --is-inside-work-tree 2>$null
    if ($LASTEXITCODE -ne 0) {
        throw "Source is not a Git work tree: $SourceRoot"
    }

    $null = & git -C $SourceRoot cat-file -e "$commit^{commit}" 2>$null
    if ($LASTEXITCODE -ne 0) {
        Add-AwIssue -Issues $Issues -Status 'source-mismatch' -Path $lockPath `
            -Message "lock commit $commit does not exist in the source"
        return
    }
    # A candidate install has no tag; the commit itself is what the source must hold.
    $isCandidate = $Lock.Contains('candidate') -and $Lock['candidate'] -eq $true
    if (-not $isCandidate) {
        $tagCommit = & git -C $SourceRoot rev-parse --verify "refs/tags/$tag^{commit}" 2>$null
        if ($LASTEXITCODE -ne 0 -or -not $tagCommit) {
            Add-AwIssue -Issues $Issues -Status 'source-mismatch' -Path $lockPath `
                -Message "the source has no tag '$tag'"
            return
        }
        if (([string]($tagCommit | Select-Object -First 1)).Trim() -cne $commit.ToLowerInvariant()) {
            Add-AwIssue -Issues $Issues -Status 'source-mismatch' -Path $lockPath `
                -Message "tag '$tag' resolves to a different commit than the lock's $commit"
            return
        }
    }

    $temp = Join-Path ([IO.Path]::GetTempPath()) ('aw-doctor-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $temp | Out-Null
    try {
        $archive = Join-Path $temp 'kit.zip'
        $null = & git -C $SourceRoot archive --format=zip "--output=$archive" $commit kit 2>$null
        if ($LASTEXITCODE -ne 0) {
            throw "Could not archive kit/ of commit $commit from the source."
        }
        Expand-Archive -LiteralPath $archive -DestinationPath $temp
        $release = Get-AwManifest -Root (Join-Path $temp 'kit')
        if ((Get-AwManifestHash -Files $release) -cne [string]$Lock.manifestSha256) {
            $differs = [Collections.Generic.List[string]]::new()
            foreach ($entry in $release.GetEnumerator()) {
                if (-not $Lock.files.Contains($entry.Key) -or [string]$Lock.files[$entry.Key] -cne [string]$entry.Value) {
                    $differs.Add([string]$entry.Key)
                }
            }
            foreach ($key in $Lock.files.Keys) {
                if (-not $release.Contains($key)) {
                    $differs.Add([string]$key)
                }
            }
            Add-AwIssue -Issues $Issues -Status 'source-mismatch' -Path $lockPath `
                -Message ("manifestSha256 differs from kit/ at $commit; differing: " +
                    (($differs | Select-Object -Unique | Select-Object -First 10) -join ', '))
        }
    }
    finally {
        Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
    }
}

try {
    if ($PSBoundParameters.ContainsKey('Root') -and $Root -and
        -not [IO.Path]::IsPathFullyQualified($Root)) {
        [Console]::Error.WriteLine('doctor: Root must be an absolute path.')
        exit 2
    }
    if ($Source -and -not [IO.Path]::IsPathFullyQualified($Source)) {
        [Console]::Error.WriteLine('doctor: Source must be an absolute path.')
        exit 2
    }

    $resolved = Resolve-AwRoot -Explicit $Root -ScriptRoot $PSScriptRoot
    $issues = [Collections.Generic.List[object]]::new()
    $validLock = $null
    $lockPath = Join-Path $resolved.WorkTree 'agent-workflow.lock.json'
    $managed = Join-Path $resolved.WorkTree 'scripts\agent-workflow'
    if (-not (Test-Path -LiteralPath $lockPath -PathType Leaf)) {
        Add-AwIssue -Issues $issues -Status 'missing' -Path 'agent-workflow.lock.json'
    }
    else {
        try {
            $lock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json -AsHashtable
            $lockIsCandidate = $lock.ContainsKey('candidate') -and $lock['candidate'] -eq $true
            if ($lockIsCandidate) {
                # Reported, not an error: doctor still exits 0 unless -RequireRelease is given.
                Add-AwIssue -Issues $issues -Status 'candidate' -Path 'agent-workflow.lock.json' `
                    -Message "the vendored kit is a candidate install of commit $($lock['commit']) (no release tag); merge-pr and package ship refuse to merge it. Install a tagged release before merging"
            }
            if ($lock.schemaVersion -ne 1 -or -not $lock.source -or (-not $lock.tag -and -not $lockIsCandidate) -or
                -not $lock.commit -or -not $lock.manifestSha256 -or
                -not ($lock.files -is [Collections.IDictionary])) {
                throw 'Lock schema is invalid.'
            }
            if ((Get-AwManifestHash -Files $lock.files) -cne $lock.manifestSha256) {
                throw 'Lock manifestSha256 does not match its files map.'
            }

            foreach ($entry in $lock.files.GetEnumerator()) {
                $relative = [string]$entry.Key
                $path = Join-Path $managed ($relative.Replace('/', [IO.Path]::DirectorySeparatorChar))
                $reportPath = "scripts/agent-workflow/$relative"
                if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
                    Add-AwIssue -Issues $issues -Status 'missing' -Path $reportPath
                }
                elseif ((Get-AwFileHash -Path $path) -cne [string]$entry.Value) {
                    Add-AwIssue -Issues $issues -Status 'modified' -Path $reportPath
                }
            }

            $expected = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
            foreach ($path in $lock.files.Keys) {
                $null = $expected.Add([string]$path)
            }
            foreach ($actual in (Get-AwManifest -Root $managed).Keys) {
                if (-not $expected.Contains([string]$actual)) {
                    Add-AwIssue -Issues $issues -Status 'extra' -Path "scripts/agent-workflow/$actual"
                }
            }

            # The managed paths must really be -text for Git: a consumer rule after the block can defeat it.
            $textPaths = @('agent-workflow.lock.json') +
                @($lock.files.Keys | ForEach-Object { "scripts/agent-workflow/$_" })
            $violations = @(Get-AwTextAttributeViolations -Root $resolved.WorkTree -Paths $textPaths)
            if ($violations.Count -gt 0) {
                Add-AwIssue -Issues $issues -Status 'text-attribute' -Path $violations[0] `
                    -Message ("Git does not treat $($violations.Count) managed path(s) as -text (first shown); " +
                        'a later .gitattributes rule overrides the kit block or the block is missing')
            }

            $validLock = $lock
        }
        catch {
            Add-AwIssue -Issues $issues -Status 'invalid-lock' `
                -Path 'agent-workflow.lock.json' -Message $_.Exception.Message
        }
        if ($Source -and $validLock) {
            # A source that cannot be read is a usage error (exit 2), not a finding about the lock.
            Test-AwLockAgainstSource -Issues $issues -Lock $validLock -SourceRoot ([IO.Path]::GetFullPath($Source))
        }
    }

    if (-not (Test-AwAttributesBlock -Path (Join-Path $resolved.WorkTree '.gitattributes'))) {
        Add-AwIssue -Issues $issues -Status 'missing-attributes' -Path '.gitattributes'
    }

    foreach ($leftover in (Get-AwInstallLeftovers -Root $resolved.WorkTree)) {
        Add-AwIssue -Issues $issues -Status 'leftover' -Path $leftover `
            -Message 'left by an install; delete it, it must not be committed'
    }

    # The consumer's scripts/<tool>.ps1 wrappers are the render of templates/wrapper.ps1. -WriteWrappers rewrites the ones that
    # differ (keeping each file's line ending) instead of reporting them.
    $written = [Collections.Generic.List[string]]::new()
    foreach ($difference in (Get-AwWrapperDifferences -KitDir $resolved.KitDir -Root $resolved.WorkTree)) {
        if ($WriteWrappers) {
            $eol = if ($difference.Actual.Contains("`r`n") -or -not $difference.Actual.Contains("`n")) { "`r`n" } else { "`n" }
            [IO.File]::WriteAllText($difference.Path, $difference.Expected.Replace("`n", $eol), [Text.UTF8Encoding]::new($false))
            $written.Add($difference.Relative)
        }
        else {
            Add-AwIssue -Issues $issues -Status 'wrapper-differs' -Path $difference.Relative `
                -Message 'is not the render of templates/wrapper.ps1; run doctor.ps1 -WriteWrappers'
        }
    }

    try {
        $null = Read-AwConfig -Root $resolved.WorkTree
    }
    catch {
        Add-AwIssue -Issues $issues -Status 'invalid-config' `
            -Path 'agent-workflow.json' -Message $_.Exception.Message
    }

    # A candidate install is a finding, not a failure, unless a release is required.
    $blocking = @($issues | Where-Object { $_.status -ne 'candidate' -or $RequireRelease })
    $result = [pscustomobject][ordered]@{
        root = $resolved.WorkTree
        clean = ($blocking.Count -eq 0)
        issues = @($issues)
        written = @($written)
    }
    if ($Json) {
        $result | ConvertTo-Json -Depth 20 -Compress
    }
    elseif ($result.clean) {
        foreach ($path in $written) { "doctor: wrote $path" }
        foreach ($issue in $issues) { "doctor: $($issue.status): $($issue.path) ($($issue.message))" }
        'doctor: clean'
    }
    else {
        foreach ($issue in $issues) {
            $line = "doctor: $($issue.status): $($issue.path)"
            if ($issue.PSObject.Properties.Name -contains 'message') { $line += " ($($issue.message))" }
            $line
        }
    }

    if ($result.clean) {
        exit 0
    }
    exit 1
}
catch {
    if ($Json) {
        [pscustomobject][ordered]@{ clean = $false; error = $_.Exception.Message; issues = @() } |
            ConvertTo-Json -Compress
    }
    else {
        [Console]::Error.WriteLine("doctor: $($_.Exception.Message)")
    }
    exit 2
}
