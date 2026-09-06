using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text.Json;
using Pointframe.Engine;

namespace Pointframe.Mcp;

public interface IDirectCaptureService
{
    string ListDisplays();

    Task<string> CaptureMonitorAsync(string monitorName, CancellationToken cancellationToken = default);
}

public sealed class DirectCaptureService : IDirectCaptureService
{
    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions MetadataSerializerOptions = new()
    {
        WriteIndented = true,
    };
    private readonly IDisplayCaptureEngine _displayCaptureEngine;
    private readonly string _screenshotsDirectory;
    private readonly TimeProvider _timeProvider;

    public DirectCaptureService(
        IDisplayCaptureEngine displayCaptureEngine,
        string? screenshotsDirectory = null,
        TimeProvider? timeProvider = null)
    {
        _displayCaptureEngine = displayCaptureEngine;
        _screenshotsDirectory = screenshotsDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Pointframe",
            "Screenshots");
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public string ListDisplays()
    {
        var displays = _displayCaptureEngine.GetDisplays()
            .Select(ToDisplayDescriptor)
            .ToArray();
        return JsonSerializer.Serialize(new DirectCaptureResponse(SchemaVersion, true, Displays: displays));
    }

    public async Task<string> CaptureMonitorAsync(string monitorName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(monitorName);
        cancellationToken.ThrowIfCancellationRequested();

        Directory.CreateDirectory(_screenshotsDirectory);
        using var capturedMonitor = _displayCaptureEngine.CaptureMonitor(monitorName);
        var createdUtc = _timeProvider.GetUtcNow();
        var artifactId = Guid.NewGuid().ToString("N");
        var path = Path.Combine(_screenshotsDirectory, $"{createdUtc:yyyyMMdd-HHmmss}-{artifactId}.png");
        capturedMonitor.Bitmap.Save(path, ImageFormat.Png);

        string sha256;
        long byteLength;
        await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous))
        {
            sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
            byteLength = stream.Length;
        }

        var metadata = new ImageArtifactMetadata(
            SchemaVersion,
            artifactId,
            "image/png",
            path,
            sha256,
            byteLength,
            createdUtc,
            "direct-monitor-capture",
            capturedMonitor.Display.MonitorName,
            capturedMonitor.Display.DpiScaleX,
            capturedMonitor.Display.DpiScaleY,
            ToPixelBounds(capturedMonitor.Display.BoundsPixels));
        await WriteMetadataSidecarAsync(metadata, cancellationToken);
        var artifact = new ArtifactDescriptor(SchemaVersion, artifactId, metadata);
        return JsonSerializer.Serialize(new DirectCaptureResponse(SchemaVersion, true, Artifact: artifact));
    }

    private static DisplayDescriptor ToDisplayDescriptor(Pointframe.Engine.DisplayDescriptor display)
    {
        return new DisplayDescriptor(
            SchemaVersion,
            display.MonitorName,
            display.DpiScaleX,
            display.DpiScaleY,
            ToPixelBounds(display.BoundsPixels));
    }

    private static PixelBounds ToPixelBounds(Pointframe.Engine.PixelBounds bounds)
    {
        return new PixelBounds(bounds.X, bounds.Y, bounds.Width, bounds.Height);
    }

    private static async Task WriteMetadataSidecarAsync(ImageArtifactMetadata metadata, CancellationToken cancellationToken)
    {
        var metadataPath = $"{metadata.Path}.metadata.json";
        var temporaryPath = $"{metadataPath}.{Guid.NewGuid():N}.tmp";

        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                JsonSerializer.Serialize(metadata, MetadataSerializerOptions),
                cancellationToken);
            File.Move(temporaryPath, metadataPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}

public sealed record DirectCaptureResponse(
    int SchemaVersion,
    bool Success,
    DirectCaptureError? Error = null,
    IReadOnlyList<DisplayDescriptor>? Displays = null,
    ArtifactDescriptor? Artifact = null);

public sealed record DirectCaptureError(string Code, string Message);

public sealed record DisplayDescriptor(int SchemaVersion, string MonitorName, double DpiScaleX, double DpiScaleY, PixelBounds BoundsPixels);

public sealed record PixelBounds(int X, int Y, int Width, int Height);

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

public interface IDirectRecordingMcpService
{
    string StartRecording(string monitorName, IReadOnlyList<PixelBounds> redactionRegionsCaptureLocalPixels, int framesPerSecond);

    Task<string> StopRecordingAsync(CancellationToken cancellationToken = default);
}

public sealed class DirectRecordingMcpService : IDirectRecordingMcpService
{
    private const int SchemaVersion = 1;
    private readonly IDirectRecordingService _directRecordingService;

    public DirectRecordingMcpService(IDirectRecordingService directRecordingService)
    {
        _directRecordingService = directRecordingService;
    }

    public string StartRecording(string monitorName, IReadOnlyList<PixelBounds> redactionRegionsCaptureLocalPixels, int framesPerSecond)
    {
        var result = _directRecordingService.Start(new DirectRecordingRequest(
            monitorName,
            redactionRegionsCaptureLocalPixels.Select(region => new Pointframe.Engine.PixelBounds(region.X, region.Y, region.Width, region.Height)).ToArray(),
            framesPerSecond));
        return JsonSerializer.Serialize(ToResponse(result));
    }

    public async Task<string> StopRecordingAsync(CancellationToken cancellationToken = default)
    {
        var result = await _directRecordingService.StopAsync(cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(ToResponse(result));
    }

    private static DirectRecordingResponse ToResponse(DirectRecordingResult result)
    {
        return new DirectRecordingResponse(
            SchemaVersion,
            result.Success,
            result.ErrorCode is null ? null : new DirectCaptureError(result.ErrorCode, result.ErrorMessage ?? "Unknown error."),
            result.Session,
            result.Artifact);
    }
}

public sealed record DirectRecordingResponse(
    int SchemaVersion,
    bool Success,
    DirectCaptureError? Error = null,
    DirectRecordingSession? Session = null,
    DirectRecordingArtifact? Artifact = null);