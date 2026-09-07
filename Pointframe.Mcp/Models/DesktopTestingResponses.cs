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
    McpCaptureError? Error = null);

public sealed record DesktopTestingActionResponse(
    int SchemaVersion,
    string OperationStatus,
    string Dispatch,
    string Verification,
    string ObservationStatus,
    McpCaptureError? Error = null,
    string? SessionRef = null,
    string? TargetRef = null);
