using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text.Json;

namespace Pointframe.Engine;

public sealed class DirectCaptureService : IDirectCaptureService
{
    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions MetadataSerializerOptions = new()
    {
        WriteIndented = true,
    };
    private readonly IDisplayCaptureEngine _displayCaptureEngine;
    private readonly IOcrEngineService _ocrEngineService;
    private readonly string _screenshotsDirectory;
    private readonly TimeProvider _timeProvider;

    public DirectCaptureService(
        IDisplayCaptureEngine displayCaptureEngine,
        IOcrEngineService ocrEngineService,
        string? screenshotsDirectory = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(ocrEngineService);
        _displayCaptureEngine = displayCaptureEngine;
        _screenshotsDirectory = screenshotsDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Pointframe",
            "Screenshots");
        _timeProvider = timeProvider ?? TimeProvider.System;
        _ocrEngineService = ocrEngineService;
    }

    public string ListDisplays()
    {
        var displays = _displayCaptureEngine.GetDisplays();
        return JsonSerializer.Serialize(new DirectCaptureResponse(SchemaVersion, true, Displays: displays));
    }

    public async Task<string> CaptureMonitorAsync(string monitorName, CaptureRegion? region = null, CancellationToken cancellationToken = default)
    {
        var (artifact, _) = await CaptureMonitorInternalAsync(monitorName, region, recognizeText: false, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new DirectCaptureResponse(SchemaVersion, true, Artifact: artifact));
    }

    public async Task<string> CaptureMonitorTextAsync(string monitorName, CaptureRegion? region = null, CancellationToken cancellationToken = default)
    {
        var (artifact, recognizedText) = await CaptureMonitorInternalAsync(monitorName, region, recognizeText: true, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new DirectCaptureResponse(SchemaVersion, true, Artifact: artifact, RecognizedText: recognizedText));
    }

    private async Task<(ArtifactDescriptor Artifact, string? RecognizedText)> CaptureMonitorInternalAsync(
        string monitorName,
        CaptureRegion? region,
        bool recognizeText,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(monitorName);
        cancellationToken.ThrowIfCancellationRequested();

        Directory.CreateDirectory(_screenshotsDirectory);
        using var capturedMonitor = ResolveCapturedMonitor(monitorName, region, out var captureBoundsPixels);
        var createdUtc = _timeProvider.GetUtcNow();
        var artifactId = Guid.NewGuid().ToString("N");
        var path = Path.Combine(_screenshotsDirectory, $"{createdUtc:yyyyMMdd-HHmmss}-{artifactId}.png");
        capturedMonitor.Bitmap.Save(path, ImageFormat.Png);

        var recognizedText = recognizeText
            ? await _ocrEngineService.RecognizeAsync(capturedMonitor.Bitmap, cancellationToken).ConfigureAwait(false)
            : null;

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
            captureBoundsPixels,
            capturedMonitor.Display.BoundsPixels);
        await WriteMetadataSidecarAsync(metadata, cancellationToken);
        var artifact = new ArtifactDescriptor(SchemaVersion, artifactId, metadata);
        return (artifact, recognizedText);
    }

    /// <summary>
    /// Resolves the monitor and captures either the whole monitor (<paramref name="region"/> is
    /// <see langword="null"/>) or a validated sub-region of it, reporting the actual physical-pixel
    /// bounds that were captured via <paramref name="captureBoundsPixels"/>.
    /// </summary>
    private CapturedMonitor ResolveCapturedMonitor(string monitorName, CaptureRegion? region, out PixelBounds captureBoundsPixels)
    {
        if (region is null)
        {
            var capturedMonitor = _displayCaptureEngine.CaptureMonitor(monitorName);
            captureBoundsPixels = capturedMonitor.Display.BoundsPixels;
            return capturedMonitor;
        }

        var display = _displayCaptureEngine.GetDisplays().SingleOrDefault(candidate =>
            string.Equals(candidate.MonitorName, monitorName, StringComparison.OrdinalIgnoreCase));
        if (display is null)
        {
            throw new ArgumentException($"The monitor '{monitorName}' was not found.", nameof(monitorName));
        }

        captureBoundsPixels = region.Value.ResolveWithin(display.BoundsPixels);
        return new CapturedMonitor(display, _displayCaptureEngine.Capture(captureBoundsPixels));
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
