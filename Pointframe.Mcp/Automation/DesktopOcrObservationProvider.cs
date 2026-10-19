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

public sealed class DesktopOcrObservationProvider : IDesktopOcrObservationProvider
{
    public Task<DesktopOcrObservation> RecognizeAsync(
        DesktopObservationResult observation,
        string imageRef,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var image = observation.Observation.Images.SingleOrDefault(item => item.ImageRef == imageRef);
        if (image is null)
        {
            return Task.FromResult(new DesktopOcrObservation("unavailable", null, default, "ImageNotFound"));
        }

        if (observation.Ocr?.ImageRef == imageRef && !observation.Ocr.IsAvailable)
        {
            return Task.FromResult(new DesktopOcrObservation("unavailable", null, image.DesktopBoundsPixels, "OcrEngineUnavailable"));
        }

        var text = observation.Ocr?.ImageRef == imageRef ? observation.Ocr.Text : null;
        return Task.FromResult(string.IsNullOrWhiteSpace(text)
            ? new DesktopOcrObservation("empty", null, image.DesktopBoundsPixels)
            : new DesktopOcrObservation("available", text, image.DesktopBoundsPixels));
    }
}
