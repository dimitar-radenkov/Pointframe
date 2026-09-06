using Pointframe.Engine;

namespace Pointframe.Services;

internal sealed record ImageArtifactMetadata(
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

internal sealed record ImageArtifactMetadataRequest(
    string ArtifactPath,
    string Source,
    string MonitorName,
    double DpiScaleX,
    double DpiScaleY,
    PixelBounds CaptureBoundsPixels);

internal sealed record RecordingArtifactMetadata(
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
    string? EventSidecarPath = null,
    long? EventCount = null,
    int? EventTrackSchemaVersion = null);

internal sealed record RecordingArtifactMetadataRequest(
    string ArtifactPath,
    TimeSpan ElapsedDuration,
    bool HadMicrophoneAudio,
    RecordingSessionGeometry Geometry,
    RecordingEventTrackSummary? EventTrackSummary = null);
