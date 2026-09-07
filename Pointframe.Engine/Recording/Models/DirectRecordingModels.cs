namespace Pointframe.Engine;

public sealed record DirectRecordingRequest(
    string MonitorName,
    IReadOnlyList<PixelBounds> RedactionRegionsCaptureLocalPixels,
    int FramesPerSecond = 20,
    string? OutputDirectory = null);

public sealed record DirectRecordingArtifact(
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
    PixelBounds CaptureBoundsPixels,
    PixelBounds HostBoundsPixels,
    PixelBounds WorkAreaBoundsPixels,
    string EventSidecarPath,
    long EventCount,
    int EventTrackSchemaVersion);

public sealed record DirectRecordingSession(
    int SchemaVersion,
    string OperationId,
    string ArtifactPath,
    string MonitorName,
    int FramesPerSecond,
    PixelBounds CaptureBoundsPixels,
    IReadOnlyList<PixelBounds> RedactionRegionsCaptureLocalPixels,
    DateTimeOffset StartedUtc);

public sealed record DirectRecordingResult(
    bool Success,
    DirectRecordingSession? Session = null,
    DirectRecordingArtifact? Artifact = null,
    string? ErrorCode = null,
    string? ErrorMessage = null);
