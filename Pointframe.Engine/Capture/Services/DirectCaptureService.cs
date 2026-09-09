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
    private readonly IWindowDiscoveryService _windowDiscoveryService;
    private readonly IOcrEngineService _ocrEngineService;
    private readonly string _screenshotsDirectory;
    private readonly TimeProvider _timeProvider;

    public DirectCaptureService(
        IDisplayCaptureEngine displayCaptureEngine,
        IOcrEngineService ocrEngineService,
        string? screenshotsDirectory = null,
        TimeProvider? timeProvider = null)
        : this(displayCaptureEngine, new WindowDiscoveryService(), ocrEngineService, screenshotsDirectory, timeProvider)
    {
    }

    public DirectCaptureService(
        IDisplayCaptureEngine displayCaptureEngine,
        IWindowDiscoveryService windowDiscoveryService,
        IOcrEngineService ocrEngineService,
        string? screenshotsDirectory = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(displayCaptureEngine);
        ArgumentNullException.ThrowIfNull(ocrEngineService);
        ArgumentNullException.ThrowIfNull(windowDiscoveryService);
        _displayCaptureEngine = displayCaptureEngine;
        _windowDiscoveryService = windowDiscoveryService;
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

    public string ListWindows()
    {
        var windows = _windowDiscoveryService.GetWindows();
        return JsonSerializer.Serialize(new DirectCaptureResponse(SchemaVersion, true, Windows: windows));
    }

    public async Task<string> CaptureWindowAsync(long windowId, CancellationToken cancellationToken = default)
    {
        var (artifact, _) = await CaptureWindowInternalAsync(windowId, recognizeText: false, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new DirectCaptureResponse(SchemaVersion, true, Artifact: artifact));
    }

    public async Task<string> CaptureWindowTextAsync(long windowId, CancellationToken cancellationToken = default)
    {
        var (artifact, recognizedText) = await CaptureWindowInternalAsync(windowId, recognizeText: true, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new DirectCaptureResponse(SchemaVersion, true, Artifact: artifact, RecognizedText: recognizedText));
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

    private async Task<(ArtifactDescriptor Artifact, string? RecognizedText)> CaptureWindowInternalAsync(
        long windowId,
        bool recognizeText,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var window = _windowDiscoveryService.GetWindow(windowId)
            ?? throw new ArgumentException($"The window with id '{windowId}' was not found or is no longer valid.", nameof(windowId));

        if (window.IsMinimized)
        {
            throw new InvalidOperationException($"The window '{window.Title}' (id {windowId}) is minimized and cannot be captured. Restore it first.");
        }

        var bounds = window.BoundsPixels;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            throw new InvalidOperationException($"The window '{window.Title}' (id {windowId}) has zero or negative size ({bounds.Width}x{bounds.Height}).");
        }

        // Reject windows that span multiple monitors: each edge must be inside the same monitor.
        var displays = _displayCaptureEngine.GetDisplays();
        var containingDisplay = FindContainingDisplay(bounds, displays);
        if (containingDisplay is null)
        {
            throw new InvalidOperationException(
                $"The window '{window.Title}' (id {windowId}) at ({bounds.X},{bounds.Y} {bounds.Width}x{bounds.Height}) " +
                "is off-screen or spans multiple monitors and cannot be captured in this version.");
        }

        Directory.CreateDirectory(_screenshotsDirectory);
        using var bitmap = _displayCaptureEngine.Capture(bounds);
        var createdUtc = _timeProvider.GetUtcNow();
        var artifactId = Guid.NewGuid().ToString("N");
        var path = Path.Combine(_screenshotsDirectory, $"{createdUtc:yyyyMMdd-HHmmss}-{artifactId}.png");
        bitmap.Save(path, ImageFormat.Png);

        var recognizedText = recognizeText
            ? await _ocrEngineService.RecognizeAsync(bitmap, cancellationToken).ConfigureAwait(false)
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
            "direct-window-capture",
            containingDisplay.MonitorName,
            containingDisplay.DpiScaleX,
            containingDisplay.DpiScaleY,
            bounds,
            containingDisplay.BoundsPixels);
        await WriteMetadataSidecarAsync(metadata, cancellationToken);
        var artifact = new ArtifactDescriptor(SchemaVersion, artifactId, metadata);
        return (artifact, recognizedText);
    }

    // A window is "contained" if all four edges are inside one monitor's bounds.
    private static DisplayDescriptor? FindContainingDisplay(PixelBounds windowBounds, IReadOnlyList<DisplayDescriptor> displays)
    {
        var windowRight = windowBounds.X + windowBounds.Width;
        var windowBottom = windowBounds.Y + windowBounds.Height;

        foreach (var display in displays)
        {
            var monitorRight = display.BoundsPixels.X + display.BoundsPixels.Width;
            var monitorBottom = display.BoundsPixels.Y + display.BoundsPixels.Height;

            if (windowBounds.X >= display.BoundsPixels.X &&
                windowBounds.Y >= display.BoundsPixels.Y &&
                windowRight <= monitorRight &&
                windowBottom <= monitorBottom)
            {
                return display;
            }
        }

        return null;
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
