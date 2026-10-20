#Requires -Version 7.0
[CmdletBinding()]
param(
    [string]$Source,
    [string]$Tag,
    [string]$Commit,
    [string]$Target,
    [switch]$DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\lib\manifest.ps1')

function Invoke-GitChecked {
    param(
        [Parameter(Mandatory)]
        [string]$Directory,
        [Parameter(Mandatory)]
        [string[]]$Arguments
    )

    $output = & git -C $Directory @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "git $($Arguments -join ' ') failed: $($output -join ' ')"
    }
    return @($output)
}

function Get-UpdatedAttributes {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $existing = ''
    if (Test-Path -LiteralPath $Path -PathType Leaf) {
        $existing = [IO.File]::ReadAllText($Path)
    }
    # Keep the consumer file's own line ending; default to CRLF for a new or single-line file.
    $eol = "`r`n"
    if ($existing.Contains("`n") -and -not $existing.Contains("`r`n")) {
        $eol = "`n"
    }
    $begin = (Get-AwAttributesBlock)[0]
    $end = (Get-AwAttributesBlock)[-1]
    if ($existing.Contains($begin) -or $existing.Contains($end)) {
        $pattern = '(?ms)^' + [regex]::Escape($begin) + '\r?\n.*?^' + [regex]::Escape($end) + '(?:\r?\n)?'
        $existing = [regex]::Replace($existing, $pattern, '')
    }
    $existing = $existing.TrimEnd("`r", "`n")
    $block = (Get-AwAttributesBlock) -join $eol
    if ($existing.Length -eq 0) {
        return "$block$eol"
    }
    return "$($existing -replace "`r?`n", $eol)$eol$eol$block$eol"
}
function Invoke-GitRaw {
    # Runs git with UTF-8 output decoding and returns the output as one string (for -z, NUL-separated data).
    param(
        [Parameter(Mandatory)]
        [string]$Directory,
        [Parameter(Mandatory)]
        [string[]]$Arguments
    )

    $previous = [Console]::OutputEncoding
    try {
        [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
        $output = & git -C $Directory @Arguments 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "git $($Arguments -join ' ') failed: $($output -join ' ')"
        }
        return ($output -join "`n")
    }
    finally {
        [Console]::OutputEncoding = $previous
    }
}
# Failure injection exists for the kit's own tests only. It is honored when the script runs from the kit
# repository's kit/tools folder (a sibling tests/run-all.ps1 exists); a vendored copy sits in
# scripts/agent-workflow/tools and never qualifies. Decided once, before the managed tree can move.
$kitFolder = Split-Path -Parent $PSScriptRoot
$kitRepository = Split-Path -Parent $kitFolder
$script:InjectionAllowed = ((Split-Path -Leaf $kitFolder) -ceq 'kit') -and
    (Test-Path -LiteralPath (Join-Path $kitRepository 'tests\run-all.ps1') -PathType Leaf)

function Test-InjectedFailure {
    param(
        [Parameter(Mandatory)]
        [string]$Step
    )

    if ($script:InjectionAllowed -and $env:AW_KIT_TEST_FAILURE_STEP -ceq $Step) {
        throw "Injected test failure at '$Step'."
    }
}

function Test-ReleaseTree {
    param(
        [Parameter(Mandatory)]
        [string]$SourceRoot,
        [Parameter(Mandatory)]
        [string]$ReleaseCommit
    )

    $indexPath = Join-Path ([IO.Path]::GetTempPath()) ('.aw-index-' + [guid]::NewGuid().ToString('N'))
    $previousIndex = $env:GIT_INDEX_FILE
    try {
        $env:GIT_INDEX_FILE = $indexPath
        $null = Invoke-GitChecked -Directory $SourceRoot -Arguments @('read-tree', $ReleaseCommit)
        $tree = Invoke-GitRaw -Directory $SourceRoot -Arguments @('ls-tree', '-r', '-z', $ReleaseCommit, '--', 'kit')
        foreach ($entry in ($tree -split "`0")) {
            if (-not $entry) {
                continue
            }
            if ($entry -match '^120000\s') {
                throw "Release kit contains a symlink: $entry"
            }
            $parts = $entry -split "`t", 2
            if ($parts.Count -ne 2) {
                continue
            }
            $path = $parts[1]
            $raw = Invoke-GitRaw -Directory $SourceRoot -Arguments @(
                'check-attr', '-z', '--cached', 'export-subst', 'export-ignore', '--', $path
            )
            $fields = @($raw -split "`0")
            for ($i = 0; $i + 2 -lt $fields.Count; $i += 3) {
                if ($fields[$i + 2] -cin @('set', 'true')) {
                    throw "Release path '$path' is affected by $($fields[$i + 1])."
                }
            }
        }
    }
    finally {
        if ($null -eq $previousIndex) {
            Remove-Item Env:GIT_INDEX_FILE -ErrorAction SilentlyContinue
        }
        else {
            $env:GIT_INDEX_FILE = $previousIndex
        }
        if (Test-Path -LiteralPath $indexPath) {
            Remove-Item -LiteralPath $indexPath -Force
        }
    }
}

function Get-LockSource {
    param(
        [Parameter(Mandatory)]
        [string]$SourceRoot,
        [Parameter(Mandatory)]
        [string]$ReleaseCommit
    )

    $origin = & git -C $SourceRoot remote get-url origin 2>$null
    if ($LASTEXITCODE -eq 0 -and $origin) {
        return ($origin -join "`n").Trim()
    }
    return ((Invoke-GitChecked -Directory $SourceRoot -Arguments @('rev-list', '--max-parents=0', $ReleaseCommit) |
        Select-Object -First 1) -join '').Trim()
}

function Get-ManagedDifferences {
    param(
        [Parameter(Mandatory)]
        [string]$ManagedRoot,
        [Parameter(Mandatory)]
        [Collections.IDictionary]$Files
    )

    $modified = [Collections.Generic.List[string]]::new()
    $missing = [Collections.Generic.List[string]]::new()
    foreach ($entry in $Files.GetEnumerator()) {
        $path = Join-Path $ManagedRoot ($entry.Key.Replace('/', [IO.Path]::DirectorySeparatorChar))
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            $missing.Add($entry.Key)
        }
        elseif ((Get-AwFileHash -Path $path) -cne [string]$entry.Value) {
            $modified.Add($entry.Key)
        }
    }
    $actual = Get-AwManifest -Root $ManagedRoot
    $extras = @($actual.Keys | Where-Object { -not $Files.Contains($_) })
    return [pscustomobject]@{ Modified = $modified; Missing = $missing; Extra = $extras }
}

function Restore-InstallState {
    param(
        [Parameter(Mandatory)]
        [string]$Managed,
        [Parameter(Mandatory)]
        [string]$Stage,
        [Parameter(Mandatory)]
        [string]$Backup,
        [Parameter(Mandatory)]
        [string]$LockPath,
        [Parameter(Mandatory)]
        [string]$LockBackup,
        [Parameter(Mandatory)]
        [string]$LockTemp,
        [Parameter(Mandatory)]
        [string]$AttributesPath,
        [Parameter(Mandatory)]
        [string]$AttributesBackup,
        [Parameter(Mandatory)]
        [string]$AttributesTemp,
        [Parameter(Mandatory)]
        [bool]$HadManaged,
        [Parameter(Mandatory)]
        [bool]$HadLock,
        [Parameter(Mandatory)]
        [bool]$HadAttributes,
        [Parameter(Mandatory)]
        [bool]$NewManagedInstalled,
        [Parameter(Mandatory)]
        [bool]$NewLockInstalled,
        [Parameter(Mandatory)]
        [bool]$NewAttributesInstalled,
        [Parameter(Mandatory)]
        [bool]$CreatedScripts
    )

    if ($NewManagedInstalled -and (Test-Path -LiteralPath $Managed)) {
        Remove-Item -LiteralPath $Managed -Recurse -Force
    }
    if ($HadManaged -and (Test-Path -LiteralPath $Backup)) {
        [IO.Directory]::Move($Backup, $Managed)
    }
    if (Test-Path -LiteralPath $Stage) {
        Remove-Item -LiteralPath $Stage -Recurse -Force
    }
    if ($NewLockInstalled -and (Test-Path -LiteralPath $LockPath)) {
        Remove-Item -LiteralPath $LockPath -Force
    }
    if ($HadLock -and (Test-Path -LiteralPath $LockBackup)) {
        [IO.File]::Move($LockBackup, $LockPath)
    }
    if ($NewAttributesInstalled -and (Test-Path -LiteralPath $AttributesPath)) {
        Remove-Item -LiteralPath $AttributesPath -Force
    }
    if ($HadAttributes -and (Test-Path -LiteralPath $AttributesBackup)) {
        [IO.File]::Move($AttributesBackup, $AttributesPath)
    }
    foreach ($path in @($LockTemp, $AttributesTemp)) {
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Force
        }
    }
    if ($CreatedScripts) {
        $scriptsRoot = Split-Path -Parent $Managed
        if ((Test-Path -LiteralPath $scriptsRoot -PathType Container) -and
            -not (Get-ChildItem -LiteralPath $scriptsRoot -Force | Select-Object -First 1)) {
            Remove-Item -LiteralPath $scriptsRoot -Force
        }
    }
}

$tempBase = $null
$archive = $null
try {
    if (-not $Source -or -not $Target -or -not $Tag -or -not $Commit -or
        -not [IO.Path]::IsPathFullyQualified($Source) -or
        -not [IO.Path]::IsPathFullyQualified($Target) -or
        $Tag -cnotmatch '^v\d+\.\d+\.\d+$' -or
        $Commit -cnotmatch '^(?:[0-9a-fA-F]{40}|[0-9a-fA-F]{64})$') {
        [Console]::Error.WriteLine('Bad arguments: provide absolute Source and Target, Tag vX.Y.Z, and full Commit SHA.')
        exit 2
    }

    $sourceRoot = [IO.Path]::GetFullPath($Source)
    $targetRoot = [IO.Path]::GetFullPath($Target)
    if (-not (Test-Path -LiteralPath $targetRoot -PathType Container)) {
        throw "Target directory does not exist: $targetRoot"
    }

    $tagRef = "refs/tags/$Tag"
    $tagCommit = (Invoke-GitChecked -Directory $sourceRoot -Arguments @(
        'rev-parse', '--verify', "$tagRef`^{commit}"
    ) | Select-Object -First 1).Trim()
    if ($tagCommit -cne $Commit.ToLowerInvariant()) {
        throw "Tag '$Tag' resolves to '$tagCommit', not expected commit '$Commit'."
    }
    $targetCheck = (Invoke-GitChecked -Directory $targetRoot -Arguments @(
        'rev-parse', '--is-inside-work-tree'
    ) | Select-Object -First 1).Trim()
    if ($targetCheck -cne 'true') {
        throw 'Target must be a Git work tree.'
    }
    Test-ReleaseTree -SourceRoot $sourceRoot -ReleaseCommit $tagCommit

    $managed = Join-Path $targetRoot 'scripts\agent-workflow'
    $lockPath = Join-Path $targetRoot 'agent-workflow.lock.json'
    if (Test-Path -LiteralPath $lockPath -PathType Leaf) {
        try {
            $oldLock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json -AsHashtable
        }
        catch {
            throw "Current lock is invalid: $($_.Exception.Message)"
        }
        if (-not ($oldLock.files -is [Collections.IDictionary])) {
            throw 'Current lock has no files map.'
        }
        $differences = Get-ManagedDifferences -ManagedRoot $managed -Files $oldLock.files
        if ($differences.Modified.Count -or $differences.Missing.Count -or $differences.Extra.Count) {
            $modified = $differences.Modified -join ', '
            $missing = $differences.Missing -join ', '
            $extra = $differences.Extra -join ', '
            throw "Managed files differ from current lock. Modified: $modified; missing: $missing; extra: $extra."
        }
    }
    elseif (Test-Path -LiteralPath $managed) {
        throw 'Managed directory exists without a lock; refusing to replace untracked content.'
    }

    $tempBase = Join-Path ([IO.Path]::GetTempPath()) ('aw-install-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $tempBase | Out-Null
    $archive = Join-Path $tempBase 'kit.zip'
    $null = Invoke-GitChecked -Directory $sourceRoot -Arguments @(
        'archive', '--format=zip', "--output=$archive", $tagCommit, 'kit'
    )
    Expand-Archive -LiteralPath $archive -DestinationPath $tempBase
    $newContent = Join-Path $tempBase 'kit'
    if (-not (Test-Path -LiteralPath $newContent -PathType Container)) {
        throw "Release commit '$tagCommit' does not contain kit/."
    }
    $files = Get-AwManifest -Root $newContent
    if ($DryRun) {
        Write-Output "DRY RUN: $($files.Count) managed files from $Tag ($tagCommit)."
        exit 0
    }

    $lock = [ordered]@{
        schemaVersion = 1
        source = Get-LockSource -SourceRoot $sourceRoot -ReleaseCommit $tagCommit
        tag = $Tag
        commit = $tagCommit.ToLowerInvariant()
        manifestSha256 = Get-AwManifestHash -Files $files
        files = $files
    }
    $scriptsRoot = Join-Path $targetRoot 'scripts'
    $attributesPath = Join-Path $targetRoot '.gitattributes'
    $token = [guid]::NewGuid().ToString('N')
    $stage = Join-Path $targetRoot ".agent-workflow.stage.$token"
    $backup = Join-Path $targetRoot ".agent-workflow.backup.$token"
    $lockTemp = Join-Path $targetRoot ".agent-workflow.lock.$token.tmp"
    $lockBackup = Join-Path $targetRoot ".agent-workflow.lock.backup.$token"
    $attributesTemp = Join-Path $targetRoot ".agent-workflow.attributes.$token.tmp"
    $attributesBackup = Join-Path $targetRoot ".agent-workflow.attributes.backup.$token"
    $hadManaged = Test-Path -LiteralPath $managed
    $hadLock = Test-Path -LiteralPath $lockPath
    $hadAttributes = Test-Path -LiteralPath $attributesPath
    $createdScripts = -not (Test-Path -LiteralPath $scriptsRoot)
    $transactionStarted = $false
    $success = $false
    $newManagedInstalled = $false
    $newLockInstalled = $false
    $newAttributesInstalled = $false

    try {
        $transactionStarted = $true
        if ($createdScripts) {
            New-Item -ItemType Directory -Path $scriptsRoot | Out-Null
        }
        Test-InjectedFailure -Step 'stage'
        Copy-Item -LiteralPath $newContent -Destination $stage -Recurse -Force
        $lock | ConvertTo-Json -Depth 50 | Set-Content -LiteralPath $lockTemp -Encoding utf8NoBOM
        [IO.File]::WriteAllText($attributesTemp, (Get-UpdatedAttributes -Path $attributesPath), [Text.UTF8Encoding]::new($false))

        if ($hadManaged) {
            Test-InjectedFailure -Step 'first-rename'
            [IO.Directory]::Move($managed, $backup)
        }
        Test-InjectedFailure -Step 'second-rename'
        [IO.Directory]::Move($stage, $managed)
        $newManagedInstalled = $true

        if ($hadLock) {
            [IO.File]::Move($lockPath, $lockBackup)
        }
        Test-InjectedFailure -Step 'lock-swap'
        [IO.File]::Move($lockTemp, $lockPath)
        $newLockInstalled = $true

        if ($hadAttributes) {
            [IO.File]::Move($attributesPath, $attributesBackup)
        }
        [IO.File]::Move($attributesTemp, $attributesPath)
        $newAttributesInstalled = $true
        $success = $true
    }
    catch {
        $originalError = $_.Exception.Message
        if ($transactionStarted) {
            try {
                Restore-InstallState -Managed $managed -Stage $stage -Backup $backup -LockPath $lockPath `
                    -LockBackup $lockBackup -LockTemp $lockTemp -AttributesPath $attributesPath `
                    -AttributesBackup $attributesBackup -AttributesTemp $attributesTemp `
                    -HadManaged $hadManaged -HadLock $hadLock -HadAttributes $hadAttributes `
                    -NewManagedInstalled $newManagedInstalled -NewLockInstalled $newLockInstalled `
                    -NewAttributesInstalled $newAttributesInstalled `
                    -CreatedScripts $createdScripts
            }
            catch {
                throw "Install failed: $originalError. Rollback also failed: $($_.Exception.Message)"
            }
        }
        throw $originalError
    }

    if ($success) {
        foreach ($backupPath in @($backup, $lockBackup, $attributesBackup)) {
            if (Test-Path -LiteralPath $backupPath) {
                try {
                    Test-InjectedFailure -Step 'cleanup'
                    Remove-Item -LiteralPath $backupPath -Recurse -Force
                }
                catch {
                    $warning = "installed successfully but could not remove backup '$backupPath': $($_.Exception.Message)"
                    [Console]::Error.WriteLine("install: warning: $warning")
                }
            }
        }
        Write-Output "Installed $Tag ($tagCommit), $($files.Count) files."
    }
}
catch {
    [Console]::Error.WriteLine("install: $($_.Exception.Message)")
    exit 1
}
finally {
    if ($tempBase -and (Test-Path -LiteralPath $tempBase)) {
        Remove-Item -LiteralPath $tempBase -Recurse -Force -ErrorAction SilentlyContinue
    }
}
