using System.Security.Cryptography;
using System.Text.Json;
using Pointframe.Engine;

namespace Pointframe.Services;

internal sealed class ArtifactMetadataService : IArtifactMetadataService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    private readonly TimeProvider _timeProvider;

    public ArtifactMetadataService(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public async Task<ImageArtifactMetadata> WriteImageMetadataAsync(
        ImageArtifactMetadataRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ArtifactPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Source);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.MonitorName);

        var artifactFile = new FileInfo(request.ArtifactPath);
        if (!artifactFile.Exists)
        {
            throw new FileNotFoundException("The image artifact does not exist.", request.ArtifactPath);
        }

        await using var artifactStream = new FileStream(
            artifactFile.FullName,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            useAsync: true);
        var hash = await SHA256.HashDataAsync(artifactStream, cancellationToken);
        var metadata = new ImageArtifactMetadata(
            SchemaVersion: 1,
            ArtifactId: $"img_{Guid.NewGuid():N}",
            Kind: "image/png",
            Path: artifactFile.FullName,
            Sha256: Convert.ToHexStringLower(hash),
            ByteLength: artifactFile.Length,
            CreatedUtc: _timeProvider.GetUtcNow(),
            Source: request.Source,
            MonitorName: request.MonitorName,
            DpiScaleX: request.DpiScaleX,
            DpiScaleY: request.DpiScaleY,
            CaptureBoundsPixels: request.CaptureBoundsPixels);

        var metadataPath = $"{artifactFile.FullName}.metadata.json";
        var temporaryPath = $"{metadataPath}.{Guid.NewGuid():N}.tmp";
        var json = JsonSerializer.Serialize(metadata, SerializerOptions);

        try
        {
            await File.WriteAllTextAsync(temporaryPath, json, cancellationToken);
            File.Move(temporaryPath, metadataPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }

        return metadata;
    }

    public async Task<RecordingArtifactMetadata> WriteRecordingMetadataAsync(
        RecordingArtifactMetadataRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ArtifactPath);
        ArgumentNullException.ThrowIfNull(request.Geometry);

        var artifactFile = new FileInfo(request.ArtifactPath);
        if (!artifactFile.Exists)
        {
            throw new FileNotFoundException("The recording artifact does not exist.", request.ArtifactPath);
        }

        await using var artifactStream = new FileStream(
            artifactFile.FullName,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            useAsync: true);
        var hash = await SHA256.HashDataAsync(artifactStream, cancellationToken);
        var geometry = request.Geometry;
        var metadata = new RecordingArtifactMetadata(
            SchemaVersion: 1,
            ArtifactId: $"rec_{Guid.NewGuid():N}",
            Kind: "video/mp4",
            Path: artifactFile.FullName,
            Sha256: Convert.ToHexStringLower(hash),
            ByteLength: artifactFile.Length,
            CreatedUtc: _timeProvider.GetUtcNow(),
            ElapsedDuration: request.ElapsedDuration,
            HadMicrophoneAudio: request.HadMicrophoneAudio,
            MonitorName: geometry.MonitorName,
            DpiScaleX: geometry.MonitorScaleX,
            DpiScaleY: geometry.MonitorScaleY,
            CaptureBoundsPixels: ToPixelBounds(geometry.CaptureBoundsPixels),
            HostBoundsPixels: ToPixelBounds(geometry.HostBoundsPixels),
            WorkAreaBoundsPixels: ToPixelBounds(geometry.WorkAreaBoundsPixels),
            EventSidecarPath: request.EventTrackSummary?.SidecarPath,
            EventCount: request.EventTrackSummary?.EventCount,
            EventTrackSchemaVersion: request.EventTrackSummary?.SchemaVersion);

        var metadataPath = $"{artifactFile.FullName}.metadata.json";
        var temporaryPath = $"{metadataPath}.{Guid.NewGuid():N}.tmp";
        var json = JsonSerializer.Serialize(metadata, SerializerOptions);

        try
        {
            await File.WriteAllTextAsync(temporaryPath, json, cancellationToken);
            File.Move(temporaryPath, metadataPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }

        return metadata;
    }

    private static PixelBounds ToPixelBounds(System.Windows.Int32Rect bounds) => new(
        bounds.X,
        bounds.Y,
        bounds.Width,
        bounds.Height);
}
