#Requires -Version 7.0
<#
.SYNOPSIS
Measures what the Pointframe MCP server sends to an agent: discovery bytes, per-call text, structured
content, duplicated content, inline images, and outcomes.

.DESCRIPTION
Starts the Release Pointframe.Mcp over stdio with an isolated catalog data directory and a desktop policy
that only allows Pointframe.DesktopTestFixture. Every advertised tool is called through a dependency-ordered
workflow (displays and windows first, then captures, then the catalog, then recording, then a full desktop
test session on the fixture). The run fails when an advertised tool has neither a measurement nor an entry
in $AllowedSkips, when any call reports an error, or when cleanup does not complete.

Token estimate: ceil(UTF-8 bytes / 4). It is a pinned rough estimator, not a model tokenizer; compare runs
with it, do not bill with it. Image bytes are the decoded size of inline image blocks.

Output: artifacts/mcp-payloads/<timestamp>/{payloads.json,payloads.csv,discovery.csv,summary.md}.
Run -SelfTest for the offline checks (no desktop, no MCP process).

This script takes over the desktop (fixture window, screen capture, a short recording). Close other
desktop automation first.
#>
[CmdletBinding()]
param(
    [switch]$SelfTest,
    [string]$Configuration = 'Release',
    [string]$BaselinePayloads
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$script:TokenEstimatorDescription = 'ceil(UTF-8 bytes / 4), a pinned rough estimator (not a model tokenizer)'
$script:FixtureWindowTitle = 'Pointframe Desktop Test Fixture'
$script:FixtureProfileId = 'payload-fixture'

# Tools that cannot be reached from the fixture. Every entry needs a concrete reason; a tool that is
# neither measured nor listed here fails the run.
$script:AllowedSkips = @{}

Add-Type -Namespace PayloadInventory -Name Win32Foreground -MemberDefinition '[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern System.IntPtr GetForegroundWindow();' -ErrorAction Stop

# ---------------------------------------------------------------------------------------------------
# Measurement primitives
# ---------------------------------------------------------------------------------------------------

function Get-Prop
{
    param($Node, [string]$Key)

    if ($Node -isnot [Text.Json.Nodes.JsonObject])
    {
        return $null
    }

    $value = $null
    if ($Node.TryGetPropertyValue($Key, [ref]$value))
    {
        return ,$value
    }

    return $null
}
function Get-Utf8ByteCount
{
    param([string]$Text)

    if ([string]::IsNullOrEmpty($Text))
    {
        return 0
    }

    return [Text.Encoding]::UTF8.GetByteCount($Text)
}

function Get-EstimatedTokens
{
    param([int]$ByteCount)

    return [int][math]::Ceiling($ByteCount / 4.0)
}

function Get-PngSize
{
    param([byte[]]$Bytes)

    $signature = [byte[]](0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A)
    if ($Bytes.Length -lt 24)
    {
        return $null
    }

    for ($index = 0; $index -lt 8; $index++)
    {
        if ($Bytes[$index] -ne $signature[$index])
        {
            return $null
        }
    }

    $width = [int][BitConverter]::ToUInt32([byte[]]($Bytes[19], $Bytes[18], $Bytes[17], $Bytes[16]), 0)
    $height = [int][BitConverter]::ToUInt32([byte[]]($Bytes[23], $Bytes[22], $Bytes[21], $Bytes[20]), 0)
    return "${width}x${height}"
}

function Get-JpegSize
{
    param([byte[]]$Bytes)

    if ($Bytes.Length -lt 4 -or $Bytes[0] -ne 0xFF -or $Bytes[1] -ne 0xD8)
    {
        return $null
    }

    $index = 2
    while ($index + 9 -lt $Bytes.Length)
    {
        if ($Bytes[$index] -ne 0xFF)
        {
            $index++
            continue
        }

        $marker = $Bytes[$index + 1]
        if ($marker -in 0xC0, 0xC1, 0xC2, 0xC3, 0xC5, 0xC6, 0xC7, 0xC9, 0xCA, 0xCB, 0xCD, 0xCE, 0xCF)
        {
            $height = ($Bytes[$index + 5] * 256) + $Bytes[$index + 6]
            $width = ($Bytes[$index + 7] * 256) + $Bytes[$index + 8]
            return "${width}x${height}"
        }

        $index += 2 + ($Bytes[$index + 2] * 256) + $Bytes[$index + 3]
    }

    return $null
}

# Image tokens: width x height / 750 per image, the common vision-model estimate (pixels, not bytes).
function Get-EstimatedImageTokens
{
    param([string[]]$Sizes)

    $total = 0
    foreach ($size in $Sizes)
    {
        if ($size -match '^(\d+)x(\d+)$')
        {
            $total += [int][math]::Ceiling(([double]$Matches[1] * [double]$Matches[2]) / 750.0)
        }
    }

    return $total
}

# Canonical form ignores key order and key casing so a text block that is the same payload as the
# structured content still matches when the two serializers differ in style.
function ConvertTo-CanonicalJson
{
    param($Node)

    if ($null -eq $Node)
    {
        return 'null'
    }

    if ($Node -is [Text.Json.Nodes.JsonObject])
    {
        $parts = foreach ($pair in ($Node | Sort-Object { $_.Key.ToLowerInvariant() }))
        {
            '"' + $pair.Key.ToLowerInvariant() + '":' + (ConvertTo-CanonicalJson $pair.Value)
        }

        return '{' + ($parts -join ',') + '}'
    }

    if ($Node -is [Text.Json.Nodes.JsonArray])
    {
        $items = foreach ($item in $Node)
        {
            ConvertTo-CanonicalJson $item
        }

        return '[' + ($items -join ',') + ']'
    }

    return $Node.ToJsonString()
}

function Get-DuplicatedTextBytes
{
    param(
        [string[]]$TextBlocks,
        $Structured
    )

    if ($null -eq $Structured)
    {
        return 0
    }

    $canonicalStructured = ConvertTo-CanonicalJson $Structured
    $duplicated = 0
    foreach ($text in $TextBlocks)
    {
        try
        {
            $parsed = [Text.Json.Nodes.JsonNode]::Parse($text)
        }
        catch
        {
            continue
        }

        if ((ConvertTo-CanonicalJson $parsed) -eq $canonicalStructured)
        {
            $duplicated += Get-Utf8ByteCount $text
        }
    }

    return $duplicated
}

function Get-ErrorReason
{
    param(
        $Result,
        $Structured
    )

    if ($null -ne (Get-Prop $Result 'isError') -and (Get-Prop $Result 'isError').ToString() -eq 'true')
    {
        return 'MCP result has isError=true'
    }

    if ($Structured -is [Text.Json.Nodes.JsonObject])
    {
        $errorNode = (Get-Prop $Structured 'error')
        if ($null -ne $errorNode)
        {
            return 'structured error: ' + $errorNode.ToJsonString()
        }

        $success = (Get-Prop $Structured 'success')
        if ($null -ne $success -and $success.ToString() -eq 'false')
        {
            return 'structured success=false'
        }
    }

    return $null
}

# $Result is the JSON-RPC "result" of a tools/call as a JsonObject.
function Measure-ToolResult
{
    param(
        [string]$Tool,
        [string]$Variant,
        $Result
    )

    $textBlocks = [Collections.Generic.List[string]]::new()
    $imageBytes = 0
    $imageSizes = [Collections.Generic.List[string]]::new()
    $content = (Get-Prop $Result 'content')
    if ($content -is [Text.Json.Nodes.JsonArray])
    {
        foreach ($block in $content)
        {
            $type = (Get-Prop $block 'type').ToString()
            if ($type -eq 'text')
            {
                $textBlocks.Add((Get-Prop $block 'text').ToString())
            }
            elseif ($type -eq 'image')
            {
                $decoded = [Convert]::FromBase64String((Get-Prop $block 'data').ToString())
                $imageBytes += $decoded.Length
                $size = Get-PngSize $decoded
                if (-not $size)
                {
                    $size = Get-JpegSize $decoded
                }

                $imageSizes.Add($(if ($size) { $size } else { 'unknown' }))
            }
        }
    }

    $textBytes = 0
    foreach ($text in $textBlocks)
    {
        $textBytes += Get-Utf8ByteCount $text
    }

    $structured = (Get-Prop $Result 'structuredContent')
    $structuredBytes = if ($null -ne $structured) { Get-Utf8ByteCount $structured.ToJsonString() } else { 0 }
    $duplicatedBytes = Get-DuplicatedTextBytes $textBlocks.ToArray() $structured
    $reason = Get-ErrorReason $Result $structured

    return [pscustomobject]@{
        tool = $Tool
        variant = $Variant
        outcome = $(if ($reason) { 'unexpected-error' } else { 'measured' })
        reason = $reason
        wireBytes = Get-Utf8ByteCount $Result.ToJsonString()
        textBytes = $textBytes
        estimatedTextTokens = Get-EstimatedTokens $textBytes
        structuredBytes = $structuredBytes
        estimatedStructuredTokens = Get-EstimatedTokens $structuredBytes
        duplicatedText = ($duplicatedBytes -gt 0)
        duplicatedBytes = $duplicatedBytes
        imageBlocks = $imageSizes.Count
        estimatedImageTokens = Get-EstimatedImageTokens $imageSizes.ToArray()
        imageBytes = $imageBytes
        imageDimensions = ($imageSizes -join ';')
    }
}

function New-ProblemRow
{
    param(
        [string]$Tool,
        [string]$Variant,
        [string]$Outcome,
        [string]$Reason
    )

    return [pscustomobject]@{
        tool = $Tool
        variant = $Variant
        outcome = $Outcome
        reason = $Reason
        wireBytes = 0
        textBytes = 0
        estimatedTextTokens = 0
        structuredBytes = 0
        estimatedStructuredTokens = 0
        duplicatedText = $false
        duplicatedBytes = 0
        imageBlocks = 0
        estimatedImageTokens = 0
        imageBytes = 0
        imageDimensions = ''
    }
}

function Get-CoverageProblems
{
    param(
        [string[]]$Advertised,
        $Rows,
        [hashtable]$AllowedSkips
    )

    $problems = [Collections.Generic.List[string]]::new()
    $measured = @($Rows | Where-Object { $_.outcome -eq 'measured' } | ForEach-Object { $_.tool })

    foreach ($tool in $Advertised)
    {
        if ($measured -notcontains $tool -and -not $AllowedSkips.ContainsKey($tool))
        {
            $problems.Add("Advertised tool has no measurement and no allowed skip: $tool")
        }
    }

    foreach ($tool in $AllowedSkips.Keys)
    {
        if ([string]::IsNullOrWhiteSpace($AllowedSkips[$tool]))
        {
            $problems.Add("Allowed skip for $tool has no reason.")
        }
    }

    foreach ($row in @($Rows | Where-Object { $_.outcome -eq 'unexpected-error' }))
    {
        $problems.Add("Unexpected error from $($row.tool) [$($row.variant)]: $($row.reason)")
    }

    return $problems.ToArray()
}

# ---------------------------------------------------------------------------------------------------
# Self test
# ---------------------------------------------------------------------------------------------------

function Assert-Equal
{
    param($Expected, $Actual, [string]$What)

    if ($Expected -ne $Actual)
    {
        throw "SelfTest failed for ${What}: expected '$Expected', got '$Actual'."
    }
}

function New-TestPngBase64
{
    param([int]$Width, [int]$Height)

    $bytes = [byte[]]::new(33)
    ([byte[]](0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52)).CopyTo($bytes, 0)
    $bytes[16] = ($Width -shr 24) -band 0xFF
    $bytes[17] = ($Width -shr 16) -band 0xFF
    $bytes[18] = ($Width -shr 8) -band 0xFF
    $bytes[19] = $Width -band 0xFF
    $bytes[20] = ($Height -shr 24) -band 0xFF
    $bytes[21] = ($Height -shr 16) -band 0xFF
    $bytes[22] = ($Height -shr 8) -band 0xFF
    $bytes[23] = $Height -band 0xFF
    return [Convert]::ToBase64String($bytes)
}

function ConvertTo-ResultNode
{
    param([string]$Json)

    return ,[Text.Json.Nodes.JsonNode]::Parse($Json)
}

function Invoke-SelfTest
{
    Assert-Equal 0 (Get-EstimatedTokens 0) 'tokens for 0 bytes'
    Assert-Equal 3 (Get-EstimatedTokens 11) 'tokens for 11 bytes'
    Assert-Equal 3 (Get-EstimatedTokens 12) 'tokens for 12 bytes'
    Assert-Equal 4 (Get-EstimatedTokens 13) 'tokens for 13 bytes'
    Assert-Equal 2 (Get-Utf8ByteCount ([string][char]0x00E9)) 'multi-byte UTF-8 count'

    $png = New-TestPngBase64 1350 765
    Assert-Equal '1350x765' (Get-PngSize ([Convert]::FromBase64String($png))) 'PNG dimensions'
    Assert-Equal $null (Get-PngSize ([byte[]](1, 2, 3))) 'non-PNG dimensions'

    $duplicate = ConvertTo-ResultNode ('{"content":[{"type":"text","text":"{\"ok\":true,\"n\":1}"},{"type":"image","data":"' + $png + '","mimeType":"image/png"}],"structuredContent":{"ok":true,"n":1}}')
    $row = Measure-ToolResult 'fixture' 'a' $duplicate
    Assert-Equal 'measured' $row.outcome 'duplicate outcome'
    Assert-Equal 17 $row.textBytes 'duplicate text bytes'
    Assert-Equal 5 $row.estimatedTextTokens 'duplicate tokens'
    Assert-Equal 17 $row.structuredBytes 'duplicate structured bytes'
    Assert-Equal $true $row.duplicatedText 'exact duplicate detected'
    Assert-Equal 17 $row.duplicatedBytes 'duplicated bytes'
    Assert-Equal 33 $row.imageBytes 'image bytes'
    Assert-Equal '1350x765' $row.imageDimensions 'image dimensions'

    $reordered = ConvertTo-ResultNode '{"content":[{"type":"text","text":"{\"N\":1,\"Ok\":true}"}],"structuredContent":{"ok":true,"n":1}}'
    Assert-Equal $true (Measure-ToolResult 'fixture' 'b' $reordered).duplicatedText 'reordered and recased duplicate detected'

    $different = ConvertTo-ResultNode '{"content":[{"type":"text","text":"{\"ok\":false}"}],"structuredContent":{"ok":true}}'
    Assert-Equal $false (Measure-ToolResult 'fixture' 'c' $different).duplicatedText 'different payload is not a duplicate'

    $plain = ConvertTo-ResultNode '{"content":[{"type":"text","text":"not json"}],"structuredContent":{"ok":true}}'
    Assert-Equal $false (Measure-ToolResult 'fixture' 'd' $plain).duplicatedText 'plain text is not a duplicate'

    $noStructured = ConvertTo-ResultNode '{"content":[{"type":"text","text":"{\"ok\":true}"}]}'
    $noStructuredRow = Measure-ToolResult 'fixture' 'e' $noStructured
    Assert-Equal $false $noStructuredRow.duplicatedText 'missing structured content'
    Assert-Equal 0 $noStructuredRow.structuredBytes 'missing structured bytes'

    $flagged = ConvertTo-ResultNode '{"content":[{"type":"text","text":"artifact_missing"}],"isError":true}'
    Assert-Equal 'unexpected-error' (Measure-ToolResult 'fixture' 'f' $flagged).outcome 'isError result'

    $structuredError = ConvertTo-ResultNode '{"content":[],"structuredContent":{"success":false,"error":{"code":"x"}}}'
    Assert-Equal 'unexpected-error' (Measure-ToolResult 'fixture' 'g' $structuredError).outcome 'structured error result'

    $measuredRows = @((New-ProblemRow 'a' '' 'measured' ''), (New-ProblemRow 'b' '' 'measured' ''))
    Assert-Equal 0 @(Get-CoverageProblems @('a', 'b') $measuredRows @{}).Count 'full coverage'
    Assert-Equal 1 @(Get-CoverageProblems @('a', 'b', 'c') $measuredRows @{}).Count 'missing tool fails'
    Assert-Equal 0 @(Get-CoverageProblems @('a', 'b', 'c') $measuredRows @{ c = 'needs a third-party app' }).Count 'allowed skip passes'
    Assert-Equal 1 @(Get-CoverageProblems @('a') $measuredRows @{ c = '' }).Count 'allowed skip without reason fails'
    $errorRows = @((New-ProblemRow 'a' 'v' 'unexpected-error' 'boom'))
    Assert-Equal 2 @(Get-CoverageProblems @('a') $errorRows @{}).Count 'unexpected error and missing measurement fail'

    $jpeg = [byte[]](0xFF, 0xD8, 0xFF, 0xC0, 0x00, 0x11, 0x08, 0x00, 0x64, 0x00, 0xC8, 0x03, 0x01, 0x22, 0x00, 0x02)
    Assert-Equal '200x100' (Get-JpegSize $jpeg) 'JPEG dimensions'
    Assert-Equal 1335 (Get-EstimatedImageTokens @('1000x1000', '10x10')) 'image tokens are width x height / 750 rounded up per image'

    $base = @(
        (New-ProblemRow 'desktop_observe_app' 'for-input' 'measured' ''),
        (New-ProblemRow 'desktop_observe_app' 'for-input' 'measured' ''),
        (New-ProblemRow 'desktop_click' '' 'measured' ''))
    $base[0].wireBytes = 6000
    $base[1].wireBytes = 8000
    $base[2].wireBytes = 1000
    $compactSample = @(
        (New-ProblemRow 'desktop_observe_app' 'compact' 'measured' ''),
        (New-ProblemRow 'desktop_click' 'compact-text' 'measured' ''))
    $compactSample[0].wireBytes = 3500
    $compactSample[1].wireBytes = 500
    $comparisonRows = @(Get-ComparisonRows $base $compactSample $null $null)
    Assert-Equal 3 $comparisonRows.Count 'comparison rows (observe, click) plus the combined step'
    Assert-Equal 7000 $comparisonRows[0].baselineBytes 'baseline observe is the average of its calls'
    Assert-Equal 3500 $comparisonRows[0].compactBytes 'compact observe bytes'
    Assert-Equal 50 $comparisonRows[0].savedPercent 'observe saving percent'
    Assert-Equal 8000 $comparisonRows[2].baselineBytes 'combined step baseline'
    Assert-Equal 4000 $comparisonRows[2].compactBytes 'combined step compact'
    $withDiscovery = @(Get-ComparisonRows $base $compactSample ([pscustomobject]@{ toolListBytes = 900 }) ([pscustomobject]@{ toolListBytes = 1000 }))
    Assert-Equal 10 $withDiscovery[-1].savedPercent 'tools/list saving percent'
    Assert-Equal 5 @(Get-ComparisonLines $comparisonRows).Count 'comparison table has a header, a rule and the rows'

    'SelfTest passed (token estimator, UTF-8 counting, PNG sizes, duplicate detection, error rows, coverage rules, comparison math).'
}

# ---------------------------------------------------------------------------------------------------
# MCP stdio client
# ---------------------------------------------------------------------------------------------------

$script:Process = $null
$script:RequestId = 0
$script:Rows = [Collections.Generic.List[object]]::new()
$script:CallLogDirectory = $null
$script:CallNumber = 0

function Send-McpRequest
{
    param(
        [string]$Method,
        $Params,
        [int]$TimeoutSeconds = 120
    )

    $script:RequestId++
    $expectedId = $script:RequestId
    $line = @{ jsonrpc = '2.0'; id = $expectedId; method = $Method; params = $Params } | ConvertTo-Json -Depth 50 -Compress
    $script:Process.StandardInput.WriteLine($line)

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ($true)
    {
        $remaining = $deadline - [DateTime]::UtcNow
        $read = $script:Process.StandardOutput.ReadLineAsync()
        if ($remaining -le [TimeSpan]::Zero -or -not $read.Wait($remaining))
        {
            throw "Timed out after ${TimeoutSeconds}s waiting for $Method."
        }

        if ($null -eq $read.Result)
        {
            throw "The MCP process exited while waiting for $Method."
        }

        $message = [Text.Json.Nodes.JsonNode]::Parse($read.Result)
        $messageId = (Get-Prop $message 'id')
        if ($null -eq $messageId -or $messageId.ToString() -ne $expectedId.ToString())
        {
            continue
        }

        if ($null -ne (Get-Prop $message 'error'))
        {
            throw "JSON-RPC error from ${Method}: $((Get-Prop $message 'error').ToJsonString())"
        }

        return ,(Get-Prop $message 'result')
    }
}

# Replaces inline image data with its length so call logs stay readable.
function Write-CallLog
{
    param([string]$Tool, $Arguments, $Result)

    $script:CallNumber++
    $copy = [Text.Json.Nodes.JsonNode]::Parse($Result.ToJsonString())
    if ((Get-Prop $copy 'content') -is [Text.Json.Nodes.JsonArray])
    {
        foreach ($block in (Get-Prop $copy 'content'))
        {
            if ((Get-Prop $block 'type').ToString() -eq 'image')
            {
                $placeholder = '<' + (Get-Prop $block 'data').ToString().Length + ' base64 chars>'
                $block.AsObject().set_Item('data', [Text.Json.Nodes.JsonValue]::Create($placeholder))
            }
        }
    }

    $entry = @{ tool = $Tool; arguments = $Arguments; result = $copy.ToJsonString() } | ConvertTo-Json -Depth 50
    $name = '{0:D3}-{1}.json' -f $script:CallNumber, $Tool
    Set-Content -LiteralPath (Join-Path $script:CallLogDirectory $name) -Value $entry -Encoding utf8
}

# Calls one tool, records one row, and returns the structured content as a hashtable (or $null).
function Invoke-Tool
{
    param(
        [string]$Name,
        [hashtable]$Arguments = @{},
        [string]$Variant = '',
        [switch]$NoRow,
        [string]$ExpectError = ''
    )

    try
    {
        $result = Send-McpRequest 'tools/call' @{ name = $Name; arguments = $Arguments }
    }
    catch
    {
        $script:Rows.Add((New-ProblemRow $Name $Variant 'unexpected-error' $_.Exception.Message))
        return $null
    }

    Write-CallLog $Name $Arguments $result
    $script:LastText = ''
    foreach ($block in (Get-Prop $result 'content'))
    {
        if ($block -is [Text.Json.Nodes.JsonObject] -and (Get-Prop $block 'type').ToString() -eq 'text' -and $script:LastText -eq '')
        {
            $script:LastText = (Get-Prop $block 'text').ToString()
        }
    }

    $row = Measure-ToolResult $Name $Variant $result
    if ($ExpectError -and $row.outcome -eq 'unexpected-error')
    {
        $row.outcome = 'expected-error'
        $row.reason = "$ExpectError; $($row.reason)"
    }
    if (-not $NoRow)
    {
        $script:Rows.Add($row)
    }

    if ($row.outcome -ne 'measured' -and $NoRow)
    {
        $script:Rows.Add($row)
    }

    $structured = (Get-Prop $result 'structuredContent')
    if ($null -eq $structured)
    {
        return $null
    }

    return $structured.ToJsonString() | ConvertFrom-Json -AsHashtable -Depth 100
}

function Get-FirstItem
{
    param($Value)

    if ($null -eq $Value)
    {
        return $null
    }

    return @($Value)[0]
}

function New-ActionId
{
    return [guid]::NewGuid().ToString()
}

# ---------------------------------------------------------------------------------------------------
# Workflow
# ---------------------------------------------------------------------------------------------------

function Measure-Discovery
{
    param([string]$OutputDirectory)

    $null = Send-McpRequest 'initialize' @{
        protocolVersion = '2025-06-18'
        capabilities = @{}
        clientInfo = @{ name = 'pointframe-payload-inventory'; version = '1' }
    }

    $script:Process.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized","params":{}}')

    $toolList = Send-McpRequest 'tools/list' @{}
    $resourceList = Send-McpRequest 'resources/list' @{}

    $perTool = foreach ($tool in (Get-Prop $toolList 'tools'))
    {
        $schema = (Get-Prop $tool 'inputSchema')
        $outputSchema = (Get-Prop $tool 'outputSchema')
        [pscustomobject]@{
            tool = (Get-Prop $tool 'name').ToString()
            definitionBytes = Get-Utf8ByteCount $tool.ToJsonString()
            descriptionBytes = Get-Utf8ByteCount $(if ($null -ne (Get-Prop $tool 'description')) { (Get-Prop $tool 'description').ToString() } else { '' })
            inputSchemaBytes = Get-Utf8ByteCount $(if ($null -ne $schema) { $schema.ToJsonString() } else { '' })
            outputSchemaBytes = Get-Utf8ByteCount $(if ($null -ne $outputSchema) { $outputSchema.ToJsonString() } else { '' })
            hasOutputSchema = ($null -ne $outputSchema)
        }
    }

    $perTool | Export-Csv -LiteralPath (Join-Path $OutputDirectory 'discovery.csv') -NoTypeInformation

    $resourceBytes = 0
    foreach ($resource in (Get-Prop $resourceList 'resources'))
    {
        $read = Send-McpRequest 'resources/read' @{ uri = (Get-Prop $resource 'uri').ToString() }
        $resourceBytes += Get-Utf8ByteCount $read.ToJsonString()
    }

    $toolListBytes = Get-Utf8ByteCount $toolList.ToJsonString()
    return [pscustomobject]@{
        toolCount = @($perTool).Count
        toolListBytes = $toolListBytes
        estimatedToolListTokens = Get-EstimatedTokens $toolListBytes
        descriptionBytes = ($perTool | Measure-Object descriptionBytes -Sum).Sum
        inputSchemaBytes = ($perTool | Measure-Object inputSchemaBytes -Sum).Sum
        outputSchemaBytes = ($perTool | Measure-Object outputSchemaBytes -Sum).Sum
        toolsWithOutputSchema = @($perTool | Where-Object hasOutputSchema).Count
        resourceCount = @((Get-Prop $resourceList 'resources')).Count
        resourceReadBytes = $resourceBytes
        toolNames = @($perTool | ForEach-Object { $_.tool })
    }
}

function Find-FixtureWindow
{
    param($Windows)

    foreach ($window in @($Windows['windows']))
    {
        if ($window['title'] -like "*$script:FixtureWindowTitle*")
        {
            return $window
        }
    }

    return $null
}

function Start-FixtureSession
{
    param([string[]]$Criteria)

    $arguments = @{ actionId = New-ActionId; profileId = $script:FixtureProfileId }
    if ($Criteria)
    {
        $arguments['criteria'] = $Criteria
    }

    $response = Invoke-Tool 'desktop_start_test_session' $arguments
    if ($null -eq $response -or -not $response['sessionRef'])
    {
        throw "The fixture session did not start: $($response | ConvertTo-Json -Compress -Depth 10)"
    }

    return $response['sessionRef']
}

# Observes the fixture window region and returns what click/drag/scroll/enter_text need.
function Get-FixtureObservation
{
    param(
        [string]$SessionId,
        $Window,
        [string]$Variant,
        [switch]$WithImages
    )

    $bounds = $Window['boundsPixels']
    $arguments = @{
        sessionId = $SessionId
        captureBoundsPixels = @(@{ x = $bounds['x']; y = $bounds['y']; width = $bounds['width']; height = $bounds['height'] })
        includeUiAutomation = $true
        includeImages = [bool]$WithImages
    }

    $structured = Invoke-Tool 'desktop_observe_app' $arguments $Variant
    if ($null -eq $structured)
    {
        throw 'desktop_observe_app returned no structured content.'
    }

    return $structured
}

function Find-Element
{
    param($Observation, [string]$AutomationId)

    foreach ($element in @($Observation['elements']))
    {
        if ($element['automationId'] -eq $AutomationId)
        {
            return $element
        }
    }

    throw "The observation has no element with automation id '$AutomationId'."
}

# Maps the centre of a desktop-pixel rectangle to image-local pixels of the observation's first image.
function Get-ImagePoint
{
    param($Observation, $Element)

    $image = $Observation['images'][0]
    $desktop = $image['desktopBoundsPixels']
    $box = $Element['boundsPixels']
    $scaleX = [double]$image['width'] / [double]$desktop['width']
    $scaleY = [double]$image['height'] / [double]$desktop['height']
    return @{
        x = [int][math]::Floor((($box['x'] + ($box['width'] / 2)) - $desktop['x']) * $scaleX)
        y = [int][math]::Floor((($box['y'] + ($box['height'] / 2)) - $desktop['y']) * $scaleY)
    }
}

function Invoke-CaptureWorkflow
{
    param($Displays, $Window)

    $display = Get-FirstItem $Displays['displays']
    $monitorName = $display['monitorName']

    $null = Invoke-Tool 'search_captures' @{ limit = 5 } 'empty-catalog'
    $capture = Invoke-Tool 'capture_monitor' @{ monitorName = $monitorName; includeImage = $true }
    $null = Invoke-Tool 'capture_monitor' @{ monitorName = $monitorName; includeImage = $false } 'no-image'
    $null = Invoke-Tool 'read_text_from_monitor' @{ monitorName = $monitorName; includeImage = $true }
    $null = Invoke-Tool 'capture_window' @{ windowId = [long]$Window['hwnd']; includeImage = $true }
    $null = Invoke-Tool 'read_text_from_window' @{ windowId = [long]$Window['hwnd']; includeImage = $true }

    $artifactId = $capture['artifact']['metadata']['artifactId']

    # The catalog indexes captures in the background, so poll briefly before measuring the search.
    $found = $null
    for ($attempt = 0; $attempt -lt 20 -and $null -eq $found; $attempt++)
    {
        $probe = Invoke-Tool 'search_captures' @{ limit = 5 } -NoRow
        $found = Get-FirstItem $probe['items']
        if ($null -eq $found)
        {
            Start-Sleep -Milliseconds 500
        }
    }

    if ($null -eq $found)
    {
        $script:Rows.Add((New-ProblemRow 'search_captures' 'after-capture' 'unexpected-error' 'the isolated catalog never indexed the capture'))
        return
    }

    $null = Invoke-Tool 'search_captures' @{ limit = 5 } 'after-capture'
    $null = Invoke-Tool 'get_capture' @{ artifactId = $found['artifactId']; includeImage = $true }
    $null = Invoke-Tool 'get_capture' @{ artifactId = $found['artifactId']; includeImage = $false } 'no-image'
}

function Invoke-RecordingWorkflow
{
    param($Displays)

    $monitorName = (Get-FirstItem $Displays['displays'])['monitorName']
    $null = Invoke-Tool 'get_recording_status' @{} 'idle-before'
    $null = Invoke-Tool 'start_recording' @{ monitorName = $monitorName; framesPerSecond = 10 }
    Start-Sleep -Milliseconds 700
    $null = Invoke-Tool 'get_recording_status' @{} 'active'
    Start-Sleep -Milliseconds 1000
    $null = Invoke-Tool 'stop_recording' @{}
    $null = Invoke-Tool 'get_recording_status' @{} 'idle-after'
}

# Every input action consumes its observation, so each one gets a fresh metadata-only observation.
function Get-InputObservation
{
    param([string]$SessionId, $Window)

    $observation = Get-FixtureObservation $SessionId $Window 'for-input'
    return ,@{
        Observation = $observation
        ObservationRef = $observation['observationRef']
        ImageRef = $observation['images'][0]['imageRef']
    }
}

function Test-FixtureIsForeground
{
    param($Window)

    return [PayloadInventory.Win32Foreground]::GetForegroundWindow().ToInt64() -eq [long]$Window['hwnd']
}

function Invoke-DesktopWorkflow
{
    param($Window, [string]$SessionId)

    $sessionId = $SessionId

    $null = Get-FixtureObservation $sessionId $Window 'with-images' -WithImages
    $focus = Get-InputObservation $sessionId $Window
    $windowRef = (Find-Element $focus.Observation 'textBox')['windowRef']
    $null = Invoke-Tool 'desktop_focus_window' @{ sessionId = $sessionId; actionId = New-ActionId; windowRef = $windowRef }

    $click = Get-InputObservation $sessionId $Window
    $clickPoint = Get-ImagePoint $click.Observation (Find-Element $click.Observation 'clickTarget')
    $clickActionId = New-ActionId
    $null = Invoke-Tool 'desktop_click' @{
        sessionId = $sessionId; actionId = $clickActionId
        observationRef = $click.ObservationRef; imageRef = $click.ImageRef
        x = $clickPoint.x; y = $clickPoint.y
    }

    $drag = Get-InputObservation $sessionId $Window
    $dragFrom = Get-ImagePoint $drag.Observation (Find-Element $drag.Observation 'dragSurface')
    $null = Invoke-Tool 'desktop_drag' @{
        sessionId = $sessionId; actionId = New-ActionId
        observationRef = $drag.ObservationRef; imageRef = $drag.ImageRef
        points = @(
            @{ x = $dragFrom.x - 40; y = $dragFrom.y; width = 0; height = 0 },
            @{ x = $dragFrom.x + 40; y = $dragFrom.y; width = 0; height = 0 }
        )
        durationMilliseconds = 300
    }

    $scroll = Get-InputObservation $sessionId $Window
    $scrollPoint = Get-ImagePoint $scroll.Observation (Find-Element $scroll.Observation 'scrollSurface')
    $null = Invoke-Tool 'desktop_scroll' @{
        sessionId = $sessionId; actionId = New-ActionId
        observationRef = $scroll.ObservationRef; imageRef = $scroll.ImageRef
        x = $scrollPoint.x; y = $scrollPoint.y; detents = -2
    }

    $text = Get-InputObservation $sessionId $Window
    $textPoint = Get-ImagePoint $text.Observation (Find-Element $text.Observation 'textBox')
    $null = Invoke-Tool 'desktop_enter_text' @{
        sessionId = $sessionId; actionId = New-ActionId
        observationRef = $text.ObservationRef; imageRef = $text.ImageRef
        x = $textPoint.x; y = $textPoint.y; text = 'payload'
    }

    # A physical key press names the observed, foreground window and consumes the observation it follows.
    $keys = Get-InputObservation $sessionId $Window
    $keysWindowRef = (Find-Element $keys.Observation 'textBox')['windowRef']
    $null = Invoke-Tool 'desktop_press_keys' @{ sessionId = $sessionId; actionId = New-ActionId; virtualKeys = @(0x10, 0x41); observationRef = $keys.ObservationRef; windowRef = $keysWindowRef } 'physical-keys'
    $null = Invoke-Tool 'desktop_press_keys' @{ sessionId = $sessionId; actionId = New-ActionId; virtualKeys = @(0x10, 0x41); globalHotkeyId = 'type-letter' } 'global-hotkey'

    $invoke = Get-InputObservation $sessionId $Window
    $null = Invoke-Tool 'desktop_invoke' @{ sessionId = $sessionId; actionId = New-ActionId; elementRef = (Find-Element $invoke.Observation 'saveButton')['elementRef'] }

    $null = Invoke-Tool 'desktop_check_ui' @{ sessionId = $sessionId; kind = 'exists'; automationId = 'saveButton'; timeoutSeconds = 5; criterionId = 'C1' }
    $null = Invoke-Tool 'desktop_check_ui' @{ sessionId = $sessionId; kind = 'exists'; automationId = 'textBox'; timeoutSeconds = 5; criterionId = 'C2' } 'second-criterion'
    $null = Invoke-Tool 'desktop_check_ui' @{ sessionId = $sessionId; kind = 'textEquals'; automationId = 'statusLabel'; expected = 'definitely not the status'; timeoutSeconds = 2; expectFailure = $true } 'negative-control'

    $null = Invoke-Tool 'desktop_get_action_result' @{ actionId = $clickActionId }

    $report = Invoke-Tool 'desktop_get_test_report' @{ sessionId = $sessionId }
    $null = Invoke-Tool 'desktop_end_test_session' @{ sessionId = $sessionId; actionId = New-ActionId }

    $reportPath = Join-Path $report['sessionDirectory'] 'report.json'
    $replaySession = Start-FixtureSession $null
    Start-Sleep -Seconds 2
    $null = Invoke-Tool 'desktop_replay_checks' @{ sessionId = $replaySession; reportPath = $reportPath }

    # restart_app refuses to run while the target is alive. Close the window with a physical Alt+F4 sent to the
    # observed window, but only after proving the fixture has the foreground.
    $replayWindow = Find-FixtureWindow (Invoke-Tool 'list_windows' @{} 'replay-session')
    $replayFocus = Get-InputObservation $replaySession $replayWindow
    $replayRef = (Find-Element $replayFocus.Observation 'textBox')['windowRef']
    $null = Invoke-Tool 'desktop_focus_window' @{ sessionId = $replaySession; actionId = New-ActionId; windowRef = $replayRef } 'before-restart'
    Start-Sleep -Milliseconds 500
    if (-not (Test-FixtureIsForeground $replayWindow))
    {
        throw 'The fixture does not have the foreground, so Alt+F4 was not sent.'
    }

    $null = Invoke-Tool 'desktop_press_keys' @{ sessionId = $replaySession; actionId = New-ActionId; virtualKeys = @(0x12, 0x73); observationRef = $replayFocus.ObservationRef; windowRef = $replayRef } 'close-with-alt-f4'
    $null = Invoke-Tool 'desktop_check_ui' @{ sessionId = $replaySession; kind = 'processExited'; timeoutSeconds = 10 } 'process-exited'
    $null = Invoke-Tool 'desktop_restart_app' @{ sessionId = $replaySession; actionId = New-ActionId }
    Start-Sleep -Seconds 2
    $null = Invoke-Tool 'desktop_end_test_session' @{ sessionId = $replaySession; actionId = New-ActionId } 'after-restart'
}

# ---------------------------------------------------------------------------------------------------
# Compact pass: the opt-in compact modes, measured against the default calls above
# ---------------------------------------------------------------------------------------------------

$script:McpPath = $null
$script:McpDataDirectory = $null
$script:McpOutputDirectory = $null
$script:McpPolicyPath = $null
$script:LastText = ''

function Start-McpProcess
{
    param([hashtable]$ExtraEnvironment = @{})

    $startInfo = [Diagnostics.ProcessStartInfo]::new($script:McpPath)
    $startInfo.WorkingDirectory = Split-Path $script:McpPath
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardInput = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.CreateNoWindow = $true
    $startInfo.Environment['SNIPPINGTOOL_AUTOMATION_DATA_DIRECTORY'] = $script:McpDataDirectory
    $startInfo.Environment['POINTFRAME_FIXTURE_STATE_PATH'] = Join-Path $script:McpOutputDirectory 'fixture-state.json'
    $startInfo.Environment['POINTFRAME_FIXTURE_MONITOR'] = '0'
    foreach ($key in $ExtraEnvironment.Keys)
    {
        $startInfo.Environment[$key] = $ExtraEnvironment[$key]
    }

    $startInfo.ArgumentList.Add('--desktop-testing')
    $startInfo.ArgumentList.Add('--desktop-policy')
    $startInfo.ArgumentList.Add($script:McpPolicyPath)
    $script:Process = [Diagnostics.Process]::Start($startInfo)
    $script:ServerLog = $script:Process.StandardError.ReadToEndAsync()
}

function Stop-McpProcess
{
    if ($script:Process -and -not $script:Process.HasExited)
    {
        $script:Process.StandardInput.Close()
        if (-not $script:Process.WaitForExit(10000))
        {
            $script:Process.Kill($true)
        }
    }
}

function Add-CompactCheck
{
    param([string]$Tool, [string]$Variant, [bool]$Condition, [string]$Reason)

    if (-not $Condition)
    {
        $script:Rows.Add((New-ProblemRow $Tool $Variant 'unexpected-error' $Reason))
    }
}

# Maps the centre of an element in a compact observation (bounds = [x, y, width, height]) to image-local pixels.
function Get-CompactImagePoint
{
    param($Observation, [string]$AutomationId)

    foreach ($element in @($Observation['elements']))
    {
        if ($element['automationId'] -eq $AutomationId)
        {
            $image = $Observation['images'][0]
            $desktop = $image['desktopBoundsPixels']
            $box = $element['bounds']
            $scaleX = [double]$image['width'] / [double]$desktop['width']
            $scaleY = [double]$image['height'] / [double]$desktop['height']
            return @{
                Element = $element
                x = [int][math]::Floor((($box[0] + ($box[2] / 2)) - $desktop['x']) * $scaleX)
                y = [int][math]::Floor((($box[1] + ($box[3] / 2)) - $desktop['y']) * $scaleY)
            }
        }
    }

    throw "The compact observation has no element with automation id '$AutomationId'."
}

# Runs against a second server started with POINTFRAME_MCP_COMPACT_TEXT=1. Every compact call must also
# carry what an agent needs to act: the compact observation's refs drive a real click and invoke, and the
# summary text of an action must still state the outcome and the action id.
function Invoke-CompactPass
{
    Stop-McpProcess
    Start-McpProcess @{ POINTFRAME_MCP_COMPACT_TEXT = '1' }
    $null = Send-McpRequest 'initialize' @{
        protocolVersion = '2025-06-18'
        capabilities = @{}
        clientInfo = @{ name = 'pointframe-payload-inventory'; version = '1' }
    }
    $script:Process.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized","params":{}}')

    $displays = Invoke-Tool 'list_displays' @{} 'compact-run' -NoRow
    $monitorName = (Get-FirstItem $displays['displays'])['monitorName']
    $session = Start-FixtureSession @('The fixture Save button is present.')
    Start-Sleep -Seconds 2
    $windows = Invoke-Tool 'list_windows' @{} 'compact-run' -NoRow
    $window = Find-FixtureWindow $windows
    if ($null -eq $window)
    {
        throw 'list_windows did not report the fixture window in the compact pass.'
    }

    $hwnd = [long]$window['hwnd']
    $null = Invoke-Tool 'capture_monitor' @{ monitorName = $monitorName; maxImageEdge = 800 } 'edge-800'
    $null = Invoke-Tool 'capture_monitor' @{ monitorName = $monitorName; imageFormat = 'jpeg' } 'jpeg'
    $null = Invoke-Tool 'capture_monitor' @{ monitorName = $monitorName; maxImageEdge = 800; imageFormat = 'jpeg' } 'edge-800-jpeg'
    $null = Invoke-Tool 'capture_window' @{ windowId = $hwnd; maxImageEdge = 800 } 'edge-800'
    $null = Invoke-Tool 'capture_window' @{ windowId = $hwnd; imageFormat = 'jpeg' } 'jpeg'

    $bounds = $window['boundsPixels']
    $region = @(@{ x = $bounds['x']; y = $bounds['y']; width = $bounds['width']; height = $bounds['height'] })
    $null = Invoke-Tool 'desktop_observe_app' @{ sessionId = $session; captureBoundsPixels = $region; includeUiAutomation = $true; includeImages = $true; detail = 'compact'; maxImageEdge = 800 } 'compact-edge-800'

    # Metadata-only compact observation, the one an agent repeats before every input action.
    $observation = Invoke-Tool 'desktop_observe_app' @{ sessionId = $session; captureBoundsPixels = $region; includeUiAutomation = $true; includeImages = $false; detail = 'compact' } 'compact'
    $textBox = Get-CompactImagePoint $observation 'textBox'
    $windowRef = if ($textBox.Element['windowRef']) { $textBox.Element['windowRef'] } else { $observation['windowRef'] }
    Add-CompactCheck 'desktop_observe_app' 'compact' ([bool]$windowRef) 'a compact observation gave no window ref'
    $null = Invoke-Tool 'desktop_focus_window' @{ sessionId = $session; actionId = New-ActionId; windowRef = $windowRef } 'compact-text'

    $observation = Invoke-Tool 'desktop_observe_app' @{ sessionId = $session; captureBoundsPixels = $region; includeUiAutomation = $true; includeImages = $false; detail = 'compact' } 'compact'
    $point = Get-CompactImagePoint $observation 'clickTarget'
    $clickActionId = New-ActionId
    $click = Invoke-Tool 'desktop_click' @{
        sessionId = $session; actionId = $clickActionId
        observationRef = $observation['observationRef']; imageRef = $observation['images'][0]['imageRef']
        x = $point.x; y = $point.y
    } 'compact-text'
    Add-CompactCheck 'desktop_click' 'compact-text' ($click['dispatch'] -eq 'Complete') 'the click driven by a compact observation was not dispatched'
    Add-CompactCheck 'desktop_click' 'compact-text' ($script:LastText -like '*dispatch=Complete*verification=*') "the summary text lost the dispatch or verification outcome: $script:LastText"

    $observation = Invoke-Tool 'desktop_observe_app' @{ sessionId = $session; captureBoundsPixels = $region; includeUiAutomation = $true; includeImages = $false; detail = 'compact' } 'compact'
    $save = Get-CompactImagePoint $observation 'saveButton'
    $null = Invoke-Tool 'desktop_invoke' @{ sessionId = $session; actionId = New-ActionId; elementRef = $save.Element['elementRef'] } 'compact-text'
    $null = Invoke-Tool 'desktop_check_ui' @{ sessionId = $session; kind = 'exists'; automationId = 'saveButton'; timeoutSeconds = 5; criterionId = 'C1' } 'compact-text'
    $null = Invoke-Tool 'desktop_check_ui' @{ sessionId = $session; kind = 'textEquals'; automationId = 'statusLabel'; expected = 'definitely not the status'; timeoutSeconds = 2; expectFailure = $true } 'compact-text-negative-control'
    $null = Invoke-Tool 'desktop_get_action_result' @{ actionId = $clickActionId } 'compact-text'

    $full = Invoke-Tool 'desktop_get_test_report' @{ sessionId = $session } 'compact-text'
    $compact = Invoke-Tool 'desktop_get_test_report' @{ sessionId = $session; detail = 'compact' } 'compact+compact-text'
    Add-CompactCheck 'desktop_get_test_report' 'compact+compact-text' ($compact['verdict'] -eq $full['verdict']) 'the compact report verdict differs from the full one'
    Add-CompactCheck 'desktop_get_test_report' 'compact+compact-text' ($script:LastText -like '*verdict=*sessionDirectory=*') "the report summary lost the verdict or bundle path: $script:LastText"
    $onDisk = Get-Content -LiteralPath (Join-Path $full['sessionDirectory'] 'report.json') -Raw | ConvertFrom-Json
    Add-CompactCheck 'desktop_get_test_report' 'compact+compact-text' (@($onDisk.proof.entries).Count -gt 0 -and $null -eq $compact['proof']) 'the full proof must stay on disk while the compact response omits it'
    $null = Invoke-Tool 'desktop_end_test_session' @{ sessionId = $session; actionId = New-ActionId } 'compact-text'
}

function Get-AverageOf
{
    param($Rows, [string]$Tool, [string]$Variant, [string]$Property)

    $matching = @($Rows | Where-Object { $_.outcome -eq 'measured' -and $_.tool -eq $Tool -and $_.variant -eq $Variant })
    if ($matching.Count -eq 0)
    {
        return $null
    }

    return [int][math]::Round((($matching | Measure-Object $Property -Average).Average))
}

# Each comparison row: what is measured, the default call, and the compact variant. Rows come from the
# same run, so both sides saw the same fixture and the same monitor.
function Get-ComparisonRows
{
    param($BaselineRows, $CompactRows, $Discovery, $BaselineDiscovery)

    $specs = @(
        @{ Name = 'desktop_observe_app, metadata only (before each input)'; BaseTool = 'desktop_observe_app'; BaseVariant = 'for-input'; Tool = 'desktop_observe_app'; Variant = 'compact' },
        @{ Name = 'desktop_observe_app with image (compact, cap 800 px)'; BaseTool = 'desktop_observe_app'; BaseVariant = 'with-images'; Tool = 'desktop_observe_app'; Variant = 'compact-edge-800' },
        @{ Name = 'capture_monitor, image cap 800 px'; BaseTool = 'capture_monitor'; BaseVariant = ''; Tool = 'capture_monitor'; Variant = 'edge-800' },
        @{ Name = 'capture_monitor, jpeg'; BaseTool = 'capture_monitor'; BaseVariant = ''; Tool = 'capture_monitor'; Variant = 'jpeg' },
        @{ Name = 'capture_monitor, jpeg + cap 800 px'; BaseTool = 'capture_monitor'; BaseVariant = ''; Tool = 'capture_monitor'; Variant = 'edge-800-jpeg' },
        @{ Name = 'capture_window, image cap 800 px'; BaseTool = 'capture_window'; BaseVariant = ''; Tool = 'capture_window'; Variant = 'edge-800' },
        @{ Name = 'capture_window, jpeg'; BaseTool = 'capture_window'; BaseVariant = ''; Tool = 'capture_window'; Variant = 'jpeg' },
        @{ Name = 'desktop_get_test_report, compact text only'; BaseTool = 'desktop_get_test_report'; BaseVariant = ''; Tool = 'desktop_get_test_report'; Variant = 'compact-text' },
        @{ Name = 'desktop_get_test_report, detail compact + compact text'; BaseTool = 'desktop_get_test_report'; BaseVariant = ''; Tool = 'desktop_get_test_report'; Variant = 'compact+compact-text' },
        @{ Name = 'desktop_click, compact text'; BaseTool = 'desktop_click'; BaseVariant = ''; Tool = 'desktop_click'; Variant = 'compact-text' },
        @{ Name = 'desktop_check_ui, compact text'; BaseTool = 'desktop_check_ui'; BaseVariant = ''; Tool = 'desktop_check_ui'; Variant = 'compact-text' },
        @{ Name = 'desktop_get_action_result, compact text'; BaseTool = 'desktop_get_action_result'; BaseVariant = ''; Tool = 'desktop_get_action_result'; Variant = 'compact-text' }
    )

    $rows = [Collections.Generic.List[object]]::new()
    foreach ($spec in $specs)
    {
        $baseWire = Get-AverageOf $BaselineRows $spec.BaseTool $spec.BaseVariant 'wireBytes'
        $compactWire = Get-AverageOf $CompactRows $spec.Tool $spec.Variant 'wireBytes'
        if ($null -eq $baseWire -or $null -eq $compactWire)
        {
            continue
        }

        $rows.Add([pscustomobject]@{
            name = $spec.Name
            baselineBytes = $baseWire
            compactBytes = $compactWire
            baselineTextBytes = Get-AverageOf $BaselineRows $spec.BaseTool $spec.BaseVariant 'textBytes'
            compactTextBytes = Get-AverageOf $CompactRows $spec.Tool $spec.Variant 'textBytes'
            baselineImageTokens = Get-AverageOf $BaselineRows $spec.BaseTool $spec.BaseVariant 'estimatedImageTokens'
            compactImageTokens = Get-AverageOf $CompactRows $spec.Tool $spec.Variant 'estimatedImageTokens'
            savedPercent = [math]::Round(100.0 * ($baseWire - $compactWire) / [math]::Max(1, $baseWire), 1)
        })
    }

    # One input step of the observe-then-act loop: the observation an action consumes plus the action.
    $baseObserve = Get-AverageOf $BaselineRows 'desktop_observe_app' 'for-input' 'wireBytes'
    $baseClick = Get-AverageOf $BaselineRows 'desktop_click' '' 'wireBytes'
    $compactObserve = Get-AverageOf $CompactRows 'desktop_observe_app' 'compact' 'wireBytes'
    $compactClick = Get-AverageOf $CompactRows 'desktop_click' 'compact-text' 'wireBytes'
    if ($null -ne $baseObserve -and $null -ne $baseClick -and $null -ne $compactObserve -and $null -ne $compactClick)
    {
        $baseStep = $baseObserve + $baseClick
        $compactStep = $compactObserve + $compactClick
        $rows.Add([pscustomobject]@{
            name = 'one input step (observe metadata + click)'
            baselineBytes = $baseStep
            compactBytes = $compactStep
            baselineTextBytes = $null
            compactTextBytes = $null
            baselineImageTokens = $null
            compactImageTokens = $null
            savedPercent = [math]::Round(100.0 * ($baseStep - $compactStep) / [math]::Max(1, $baseStep), 1)
        })
    }

    if ($null -ne $BaselineDiscovery)
    {
        $rows.Add([pscustomobject]@{
            name = 'tools/list (baseline file vs this build)'
            baselineBytes = [int]$BaselineDiscovery.toolListBytes
            compactBytes = [int]$Discovery.toolListBytes
            baselineTextBytes = $null
            compactTextBytes = $null
            baselineImageTokens = $null
            compactImageTokens = $null
            savedPercent = [math]::Round(100.0 * ($BaselineDiscovery.toolListBytes - $Discovery.toolListBytes) / [math]::Max(1, $BaselineDiscovery.toolListBytes), 1)
        })
    }

    return $rows.ToArray()
}

function Get-ComparisonLines
{
    param($Comparison)

    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add('| Call | Default wire bytes | Compact wire bytes | Default text bytes | Compact text bytes | Default image tokens | Compact image tokens | Wire saved |')
    $lines.Add('|---|---|---|---|---|---|---|---|')
    foreach ($row in $Comparison)
    {
        $lines.Add("| $($row.name) | $($row.baselineBytes) | $($row.compactBytes) | $($row.baselineTextBytes) | $($row.compactTextBytes) | $($row.baselineImageTokens) | $($row.compactImageTokens) | $($row.savedPercent)% |")
    }

    return $lines.ToArray()
}

# ---------------------------------------------------------------------------------------------------
# Reports
# ---------------------------------------------------------------------------------------------------

function New-MarkdownTable
{
    param($Rows, [string[]]$Headers, [scriptblock]$Cells)

    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add('| ' + ($Headers -join ' | ') + ' |')
    $lines.Add('|' + (($Headers | ForEach-Object { '---' }) -join '|') + '|')
    foreach ($row in $Rows)
    {
        $values = & $Cells $row
        $lines.Add('| ' + ($values -join ' | ') + ' |')
    }

    return $lines
}

function Write-Summary
{
    param([string]$Path, $Discovery, $Rows, [string[]]$Advertised, [string[]]$CleanupProblems, $Comparison = @())

    $measured = @($Rows | Where-Object { $_.outcome -eq 'measured' })
    $topText = @($measured | Sort-Object estimatedTextTokens -Descending | Select-Object -First 10)
    $topImage = @($measured | Where-Object { $_.imageBytes -gt 0 } | Sort-Object imageBytes -Descending | Select-Object -First 10)
    $duplicates = @($measured | Where-Object { $_.duplicatedText } | Sort-Object duplicatedBytes -Descending | Select-Object -First 10)
    $label = { param($row) if ($row.variant) { "$($row.tool) [$($row.variant)]" } else { $row.tool } }

    $md = [Collections.Generic.List[string]]::new()
    $md.Add('# MCP payload inventory')
    $md.Add('')
    $md.Add("Token estimate: $script:TokenEstimatorDescription. Image bytes are decoded inline image blocks.")
    $md.Add('')
    $md.Add("Advertised tools: $($Advertised.Count); tools measured: $(@($measured | ForEach-Object { $_.tool } | Sort-Object -Unique).Count); allowed skips: $($script:AllowedSkips.Count); measured calls: $($measured.Count).")
    $md.Add('')
    $md.Add('## Discovery')
    $md.Add('')
    $md.Add("- tools/list: $($Discovery.toolListBytes) bytes (about $($Discovery.estimatedToolListTokens) tokens) for $($Discovery.toolCount) tools")
    $md.Add("- descriptions: $($Discovery.descriptionBytes) bytes; input schemas: $($Discovery.inputSchemaBytes) bytes; output schemas: $($Discovery.outputSchemaBytes) bytes ($($Discovery.toolsWithOutputSchema) of $($Discovery.toolCount) tools declare one)")
    $md.Add("- resources: $($Discovery.resourceCount) listed, $($Discovery.resourceReadBytes) bytes when all are read")
    $md.Add('')
    $md.Add('## Top 10 calls by estimated text tokens')
    $md.Add('')
    foreach ($line in (New-MarkdownTable $topText @('Call', 'Tokens', 'Text bytes', 'Structured bytes', 'Duplicated text') { param($r) @((& $label $r), $r.estimatedTextTokens, $r.textBytes, $r.structuredBytes, $r.duplicatedText) }))
    {
        $md.Add($line)
    }

    $md.Add('')
    $md.Add('## Top 10 calls by image bytes')
    $md.Add('')
    foreach ($line in (New-MarkdownTable $topImage @('Call', 'Image bytes', 'Image blocks', 'Dimensions') { param($r) @((& $label $r), $r.imageBytes, $r.imageBlocks, $r.imageDimensions) }))
    {
        $md.Add($line)
    }

    $md.Add('')
    $md.Add('## Duplicated text (text block equals structured content)')
    $md.Add('')
    foreach ($line in (New-MarkdownTable $duplicates @('Call', 'Duplicated bytes', 'Text bytes') { param($r) @((& $label $r), $r.duplicatedBytes, $r.textBytes) }))
    {
        $md.Add($line)
    }

    $md.Add('')
    if (@($Comparison).Count -gt 0)
    {
        $md.Add('## Default versus compact modes (same run, bytes on the wire)')
        $md.Add('')
        foreach ($line in (Get-ComparisonLines $Comparison))
        {
            $md.Add($line)
        }

        $md.Add('')
    }

    $md.Add('## Coverage')
    $md.Add('')
    $coverage = foreach ($tool in ($Advertised | Sort-Object))
    {
        $count = @($measured | Where-Object { $_.tool -eq $tool }).Count
        [pscustomobject]@{ tool = $tool; outcome = $(if ($count) { "measured ($count)" } else { 'skipped' }); reason = $script:AllowedSkips[$tool] }
    }

    foreach ($line in (New-MarkdownTable $coverage @('Tool', 'Outcome', 'Reason') { param($r) @($r.tool, $r.outcome, $r.reason) }))
    {
        $md.Add($line)
    }

    $md.Add('')
    $md.Add('## Expected errors (recorded, not counted as coverage)')
    $md.Add('')
    foreach ($row in @($Rows | Where-Object { $_.outcome -eq 'expected-error' }))
    {
        $md.Add("- $(& $label $row): $($row.reason)")
    }
    $md.Add('')
    $md.Add('## Cleanup')
    $md.Add('')
    if ($CleanupProblems.Count)
    {
        foreach ($problem in $CleanupProblems)
        {
            $md.Add("- FAILED: $problem")
        }
    }
    else
    {
        $md.Add('- MCP process exited, fixture processes gone, isolated catalog data and desktop evidence deleted.')
    }

    Set-Content -LiteralPath $Path -Value $md -Encoding utf8
}

if ($SelfTest)
{
    Invoke-SelfTest
    exit 0
}

# ---------------------------------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------------------------------

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$tfm = 'net10.0-windows10.0.18362.0'
$mcpPath = Join-Path $root "Pointframe.Mcp/bin/$Configuration/$tfm/Pointframe.Mcp.exe"
$fixturePath = Join-Path $root "Pointframe.DesktopTestFixture/bin/$Configuration/$tfm/Pointframe.DesktopTestFixture.exe"
if (-not (Test-Path $mcpPath) -or -not (Test-Path $fixturePath))
{
    throw "Build Pointframe.Mcp and Pointframe.DesktopTestFixture ($Configuration) first."
}

$stamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ')
$outputDirectory = Join-Path $root "artifacts/mcp-payloads/$stamp"
$dataDirectory = Join-Path $outputDirectory 'isolated-data'
$evidenceDirectory = Join-Path $outputDirectory 'desktop-evidence'
$script:CallLogDirectory = Join-Path $outputDirectory 'calls'
New-Item -ItemType Directory -Force -Path $outputDirectory, $dataDirectory, $evidenceDirectory, $script:CallLogDirectory | Out-Null

$policyPath = Join-Path $outputDirectory 'fixture-policy.json'
$policy = @{
    schemaVersion = 1
    artifactRoot = $evidenceDirectory
    evidencePolicy = 'All'
    profiles = @(@{
        id = $script:FixtureProfileId
        executablePath = $fixturePath
        arguments = @()
        workingDirectory = (Split-Path $fixturePath)
        allowAttach = $false
        allowedActions = @(
            'ListApps', 'StartTestSession', 'RestartApp', 'ObserveApp', 'FocusWindow', 'Click', 'PressKeys',
            'Drag', 'EnterText', 'Invoke', 'CheckUi', 'Scroll', 'GetActionResult', 'GetTestReport',
            'EndTestSession', 'ReplayChecks')
        allowedGlobalHotkeys = @{ 'type-letter' = @('SHIFT', 'A') }
        allowedShellSurfaces = @()
        allowMonitorObservation = $true
    })
}
$policy | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $policyPath -Encoding utf8

$fixtureBefore = @(Get-Process -Name 'Pointframe.DesktopTestFixture' -ErrorAction SilentlyContinue | ForEach-Object { $_.Id })
$discovery = $null
$advertised = @()
$runFailure = $null
$compactRows = @()
$comparison = @()

try
{
    $script:McpPath = $mcpPath
    $script:McpDataDirectory = $dataDirectory
    $script:McpOutputDirectory = $outputDirectory
    $script:McpPolicyPath = $policyPath
    Start-McpProcess

    $discovery = Measure-Discovery $outputDirectory
    $advertised = $discovery.toolNames

    $displays = Invoke-Tool 'list_displays'

    # The fixture window must exist before list_windows can hand out its handle, so the desktop session
    # starts here and the capture tools run against the live window.
    $null = Invoke-Tool 'desktop_list_apps' @{ actionId = New-ActionId }
    $criteria = @('The fixture Save button is present.', 'The fixture text box is present.')
    $fixtureSession = Start-FixtureSession $criteria
    Start-Sleep -Seconds 2
    $windows = Invoke-Tool 'list_windows'
    $window = Find-FixtureWindow $windows
    if ($null -eq $window)
    {
        throw 'list_windows did not report the fixture window.'
    }

    Invoke-CaptureWorkflow $displays $window
    Invoke-RecordingWorkflow $displays
    Invoke-DesktopWorkflow $window $fixtureSession

    $baselineRows = $script:Rows
    $script:Rows = [Collections.Generic.List[object]]::new()
    try
    {
        Invoke-CompactPass
    }
    finally
    {
        $compactRows = @($script:Rows)
        $script:Rows = $baselineRows
    }
}
catch
{
    $runFailure = $_.Exception.Message
}
finally
{
    Stop-McpProcess
}

# ---------------------------------------------------------------------------------------------------
# Cleanup verification, outputs, verdict
# ---------------------------------------------------------------------------------------------------

$cleanupProblems = [Collections.Generic.List[string]]::new()
if ($script:Process -and -not $script:Process.HasExited)
{
    $cleanupProblems.Add('The MCP process is still running.')
}

foreach ($leftover in @(Get-Process -Name 'Pointframe.DesktopTestFixture' -ErrorAction SilentlyContinue | Where-Object { $fixtureBefore -notcontains $_.Id }))
{
    $cleanupProblems.Add("Fixture process $($leftover.Id) was left running.")
    $leftover.Kill()
}

foreach ($directory in @($dataDirectory, $evidenceDirectory))
{
    try
    {
        Remove-Item -LiteralPath $directory -Recurse -Force -ErrorAction Stop
    }
    catch
    {
        $cleanupProblems.Add("Could not delete ${directory}: $($_.Exception.Message)")
    }
}

$problems = [Collections.Generic.List[string]]::new()
if ($runFailure)
{
    $problems.Add("Workflow stopped early: $runFailure")
}

if ($null -ne $discovery)
{
    foreach ($problem in (Get-CoverageProblems $advertised $script:Rows $script:AllowedSkips))
    {
        $problems.Add($problem)
    }
}

foreach ($row in @($compactRows | Where-Object { $_.outcome -eq 'unexpected-error' }))
{
    $problems.Add("Unexpected error from compact call $($row.tool) [$($row.variant)]: $($row.reason)")
}

$baselineDiscovery = $null
if ($BaselinePayloads)
{
    $baselineDiscovery = (Get-Content -LiteralPath $BaselinePayloads -Raw | ConvertFrom-Json).discovery
}

if ($null -ne $discovery -and $compactRows.Count -gt 0)
{
    $comparison = @(Get-ComparisonRows @($script:Rows) $compactRows $discovery $baselineDiscovery)
}

foreach ($problem in $cleanupProblems)
{
    $problems.Add("Cleanup: $problem")
}

$script:Rows | Export-Csv -LiteralPath (Join-Path $outputDirectory 'payloads.csv') -NoTypeInformation
$document = [ordered]@{
    createdUtc = [DateTime]::UtcNow.ToString('o')
    tokenEstimator = $script:TokenEstimatorDescription
    discovery = $discovery
    allowedSkips = $script:AllowedSkips
    calls = @($script:Rows)
    compactCalls = @($compactRows)
    comparison = @($comparison)
    problems = $problems.ToArray()
}
$document | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $outputDirectory 'payloads.json') -Encoding utf8
if ($null -ne $discovery)
{
    Write-Summary (Join-Path $outputDirectory 'summary.md') $discovery $script:Rows $advertised $cleanupProblems.ToArray() $comparison
}

if ($comparison.Count -gt 0)
{
    Write-Output 'Default versus compact (bytes on the wire, same run):'
    Get-ComparisonLines $comparison | ForEach-Object { Write-Output $_ }
}

Write-Output "Payload inventory written to $outputDirectory"
if ($problems.Count)
{
    $problems | ForEach-Object { Write-Error $_ -ErrorAction Continue }
    exit 1
}
