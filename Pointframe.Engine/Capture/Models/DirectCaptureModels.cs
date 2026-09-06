namespace Pointframe.Engine;

public sealed record DirectCaptureResponse(
    int SchemaVersion,
    bool Success,
    DirectCaptureError? Error = null,
    IReadOnlyList<DisplayDescriptor>? Displays = null,
    ArtifactDescriptor? Artifact = null);

public sealed record DirectCaptureError(string Code, string Message);

public sealed record ArtifactDescriptor(int SchemaVersion, string OperationId, ImageArtifactMetadata Metadata);

public sealed record ImageArtifactMetadata(
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
    PixelBounds CaptureBoundsPixels);
