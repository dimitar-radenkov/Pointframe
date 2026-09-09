namespace Pointframe.Mcp;

public sealed record McpPixelBounds(int X, int Y, int Width, int Height);

/// <summary>
/// A capture sub-region in monitor-local physical pixels, relative to the target monitor's own
/// top-left corner rather than the virtual desktop or another monitor's origin.
/// </summary>
public sealed record McpCaptureRegion(int X, int Y, int Width, int Height);

public sealed record McpCaptureResponse(
    int SchemaVersion,
    bool Success,
    McpCaptureError? Error = null,
    IReadOnlyList<McpDisplayDescriptor>? Displays = null,
    McpArtifactDescriptor? Artifact = null,
    string? RecognizedText = null);

public sealed record McpCaptureError(string Code, string Message);

public sealed record McpDisplayDescriptor(
    string MonitorName,
    double DpiScaleX,
    double DpiScaleY,
    McpPixelBounds BoundsPixels,
    McpPixelBounds WorkAreaBoundsPixels);

public sealed record McpArtifactDescriptor(
    int SchemaVersion,
    string OperationId,
    McpImageArtifactMetadata Metadata);

public sealed record McpImageArtifactMetadata(
    int SchemaVersion,
    string ArtifactId,
    string Kind,
    string Path,
    string Sha256,
    long ByteLength,
    DateTimeOffset CreatedUtc,
    string Source,
    string MonitorName,
    double DpiScaleX,
    double DpiScaleY,
    McpPixelBounds CaptureBoundsPixels,
    McpPixelBounds MonitorBoundsPixels);

public sealed record McpRecordingResponse(
    int SchemaVersion,
    bool Success,
    McpCaptureError? Error = null,
    McpRecordingSession? Session = null,
    McpRecordingArtifact? Artifact = null);

public sealed record McpRecordingSession(
    int SchemaVersion,
    string OperationId,
    string ArtifactPath,
    string MonitorName,
    int FramesPerSecond,
    McpPixelBounds CaptureBoundsPixels,
    IReadOnlyList<McpPixelBounds> RedactionRegionsCaptureLocalPixels,
    DateTimeOffset StartedUtc);

public sealed record McpRecordingArtifact(
    int SchemaVersion,
    string ArtifactId,
    string Kind,
    string Path,
    string Sha256,
    long ByteLength,
    DateTimeOffset CreatedUtc,
    TimeSpan ElapsedDuration,
    bool HadMicrophoneAudio,
    string MonitorName,
    double DpiScaleX,
    double DpiScaleY,
    McpPixelBounds CaptureBoundsPixels,
    McpPixelBounds HostBoundsPixels,
    McpPixelBounds WorkAreaBoundsPixels,
    string EventSidecarPath,
    long EventCount,
    int EventTrackSchemaVersion);

public sealed record McpRecordingStatusResponse(
    int SchemaVersion,
    bool IsRecording,
    McpRecordingSession? Session = null,
    TimeSpan? Elapsed = null);

public sealed record McpServerInfoResponse(
    int SchemaVersion,
    string Version,
    bool DesktopTestingEnabled,
    McpFfmpegAvailability Ffmpeg);

public sealed record McpFfmpegAvailability(bool Found, string Path, string Source);
