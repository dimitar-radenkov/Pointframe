using Pointframe.Engine.Automation.Models;

namespace Pointframe.Engine.Automation.Services;

public interface IDesktopObservationService
{
    Task<DesktopObservationResult> ObserveAsync(DesktopObservationRequest request, CancellationToken cancellationToken = default);

    DesktopObservationResult Resolve(string observationRef);

    PixelBounds ToDesktopPixels(string observationRef, string imageRef, int x, int y);
}

public interface IDesktopObservationCapture
{
    Bitmap Capture(PixelBounds boundsPixels);
}

public interface IDesktopUiObservationProvider
{
    DesktopUiSnapshot Inspect(DesktopObservationRequest request);
}

public sealed class DesktopObservationService : IDesktopObservationService
{
    private readonly IDisplayCaptureEngine _captureEngine;
    private readonly IDesktopObservationStore _store;
    private readonly IDesktopUiObservationProvider? _uiProvider;
    private readonly TimeProvider _timeProvider;
    private int _topologyGeneration;

    public DesktopObservationService(
        IDisplayCaptureEngine captureEngine,
        IDesktopObservationStore store,
        IDesktopUiObservationProvider? uiProvider = null,
        TimeProvider? timeProvider = null)
    {
        _captureEngine = captureEngine ?? throw new ArgumentNullException(nameof(captureEngine));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _uiProvider = uiProvider;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<DesktopObservationResult> ObserveAsync(DesktopObservationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.CaptureBoundsPixels.Count == 0 || request.CaptureBoundsPixels.Count > DesktopTestingLimits.MaxObservationCount)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "An observation must contain a bounded set of capture regions.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var capturedUtc = _timeProvider.GetUtcNow();
        var images = new List<DesktopImageReference>();
        foreach (var bounds in request.CaptureBoundsPixels)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(request), "Capture bounds must have positive dimensions.");
            }

            using var bitmap = _captureEngine.Capture(bounds);
            var imageRef = $"image-{Guid.NewGuid():N}";
            images.Add(new DesktopImageReference(imageRef, bitmap.Width, bitmap.Height, bounds, capturedUtc));
        }

        var topologyGeneration = request.TopologyGeneration ?? Interlocked.Increment(ref _topologyGeneration);
        var ui = request.IncludeUiAutomation
            ? _uiProvider?.Inspect(request) ?? new DesktopUiSnapshot(
                DesktopUiAutomationStatus.Unavailable,
                [],
                capturedUtc,
                ErrorCode: "ProviderUnavailable")
            : null;

        var observation = new DesktopObservation(
            DesktopTestingLimits.SchemaVersion,
            $"observation-{Guid.NewGuid():N}",
            request.Process,
            DesktopTargetState.Running,
            DesktopObservationStatus.Available,
            images,
            ui?.Status ?? DesktopUiAutomationStatus.Unavailable,
            capturedUtc,
            ui?.CapturedUtc,
            ui?.IsTruncated ?? false);
        var result = new DesktopObservationResult(observation, ui, topologyGeneration);
        _store.Save(result);
        return Task.FromResult(result);
    }

    public DesktopObservationResult Resolve(string observationRef)
    {
        return _store.Resolve(observationRef);
    }

    public PixelBounds ToDesktopPixels(string observationRef, string imageRef, int x, int y)
    {
        var observation = Resolve(observationRef);
        var image = observation.Observation.Images.SingleOrDefault(item => item.ImageRef == imageRef)
            ?? throw new KeyNotFoundException($"Image '{imageRef}' was not found.");
        return new DesktopCoordinateMapper(_timeProvider).ToDesktopPixels(
            image,
            x,
            y,
            observation.TopologyGeneration);
    }
}
