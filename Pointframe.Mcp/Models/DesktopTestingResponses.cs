namespace Pointframe.Mcp;

public sealed record DesktopImageBlock(
    string ImageRef,
    int Width,
    int Height,
    McpPixelBounds DesktopBoundsPixels,
    DateTimeOffset CapturedUtc);

public sealed record DesktopElementBlock(
    string ElementRef,
    string WindowRef,
    string Role,
    string? Name,
    string? AutomationId,
    McpPixelBounds BoundsPixels,
    bool IsEnabled,
    string? ToggleState,
    string? Selection,
    string? Text);

public sealed record DesktopTestingObservationResponse(
    int SchemaVersion,
    string ObservationRef,
    string TargetState,
    string ObservationStatus,
    string UiaStatus,
    string? ProcessRef,
    bool IsTruncated,
    int TopologyGeneration,
    DateTimeOffset PixelCapturedUtc,
    DateTimeOffset? UiaCapturedUtc,
    IReadOnlyList<DesktopImageBlock> Images,
    IReadOnlyList<DesktopElementBlock> Elements,
    McpCaptureError? Error = null,
    DesktopOcrObservationResponse? Ocr = null);

public sealed record DesktopOcrObservationResponse(
    string Status,
    string? Text,
    McpPixelBounds SourceBoundsPixels,
    McpCaptureError? Error = null);

public sealed record DesktopTestingCheckResponse(
    int SchemaVersion,
    string Verification,
    bool StateAvailable,
    bool Matches,
    int MatchCount,
    McpCaptureError? Error = null,
    string? ActualValue = null);

public sealed record DesktopTestingActionResponse(
    int SchemaVersion,
    string OperationStatus,
    string Dispatch,
    string Verification,
    string ObservationStatus,
    McpCaptureError? Error = null,
    string? SessionRef = null,
    string? TargetRef = null,
    IReadOnlyList<DesktopAppSummary>? Apps = null,
    string? Method = null);

public sealed record DesktopAppSummary(string Id, string ExecutableName, int AllowedActionCount);

public sealed record DesktopReplayCheck(
    int Index,
    string Description,
    string Original,
    string? Replayed,
    string? Skipped = null);

public sealed record DesktopReplayCriterion(
    string Id,
    string Original,
    string Replayed);

public sealed record DesktopReplayResponse(
    int SchemaVersion,
    string Status,
    string? OriginalSessionRef,
    string? KeyId,
    IReadOnlyList<DesktopReplayCriterion> Criteria,
    IReadOnlyList<DesktopReplayCheck> Checks,
    McpCaptureError? Error = null);

/// <summary>
/// A desktop_check_ui condition in a shape an MCP client can actually build. The tool previously took
/// the engine's abstract DesktopUiCheckCondition record directly, which has no JSON polymorphism
/// metadata, so no caller could construct one over the wire.
/// </summary>
public sealed record McpUiCheckRequest(
    string Kind,
    string? AutomationId = null,
    string? Role = null,
    string? Name = null,
    string? WindowRef = null,
    string? Expected = null,
    string? ProcessRef = null);

public sealed record DesktopCompactImageBlock(
    string ImageRef,
    int Width,
    int Height,
    McpPixelBounds DesktopBoundsPixels);

// Bounds are [x, y, width, height] in desktop physical pixels. WindowRef is omitted when it equals the
// response's top-level WindowRef; Disabled is present (true) only for a disabled element.
public sealed record DesktopCompactElementBlock(
    string ElementRef,
    string Role,
    string? Name,
    string? AutomationId,
    IReadOnlyList<int> Bounds,
    string? WindowRef = null,
    bool? Disabled = null,
    string? ToggleState = null,
    string? Selection = null,
    string? Text = null);

public sealed record DesktopTestingCompactObservationResponse(
    int SchemaVersion,
    string Detail,
    string ObservationRef,
    string TargetState,
    string ObservationStatus,
    string UiaStatus,
    string? ProcessRef,
    bool IsTruncated,
    int TopologyGeneration,
    DateTimeOffset PixelCapturedUtc,
    IReadOnlyList<DesktopCompactImageBlock> Images,
    string? WindowRef,
    IReadOnlyList<DesktopCompactElementBlock> Elements,
    McpCaptureError? Error = null,
    DesktopOcrObservationResponse? Ocr = null);
