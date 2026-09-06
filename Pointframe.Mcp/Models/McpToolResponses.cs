namespace Pointframe.Mcp;

public sealed record McpPixelBounds(int X, int Y, int Width, int Height);

public sealed record McpCaptureResponse(
    int SchemaVersion,
    bool Success,
    McpCaptureError? Error = null,
    IReadOnlyList<McpDisplayDescriptor>? Displays = null,
    McpArtifactDescriptor? Artifact = null);

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
    McpPixelBounds CaptureBoundsPixels);

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
