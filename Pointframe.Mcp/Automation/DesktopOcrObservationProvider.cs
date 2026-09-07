using Pointframe.Engine;
using Pointframe.Engine.Automation.Models;

namespace Pointframe.Mcp.Automation;

public sealed record DesktopOcrObservation(
    string Status,
    string? Text,
    PixelBounds SourceBoundsPixels,
    string? ErrorCode = null);

public interface IDesktopOcrObservationProvider
{
    Task<DesktopOcrObservation> RecognizeAsync(
        DesktopObservationResult observation,
        string imageRef,
        CancellationToken cancellationToken = default);
}

public sealed class DesktopOcrObservationProvider(
    IDisplayCaptureEngine captureEngine,
    IOcrEngineService ocrEngine) : IDesktopOcrObservationProvider
{
    public async Task<DesktopOcrObservation> RecognizeAsync(
        DesktopObservationResult observation,
        string imageRef,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var image = observation.Observation.Images.SingleOrDefault(item => item.ImageRef == imageRef);
        if (image is null)
        {
            return new DesktopOcrObservation("unavailable", null, default, "ImageNotFound");
        }

        using var bitmap = captureEngine.Capture(image.DesktopBoundsPixels);
        var text = await ocrEngine.RecognizeAsync(bitmap, cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(text)
            ? new DesktopOcrObservation("empty", null, image.DesktopBoundsPixels)
            : new DesktopOcrObservation("available", text, image.DesktopBoundsPixels);
    }
}
