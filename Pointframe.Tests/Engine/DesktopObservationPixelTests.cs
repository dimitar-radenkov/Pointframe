using System.Drawing;
using Pointframe.Engine;
using Pointframe.Engine.Automation.Models;
using Pointframe.Engine.Automation.Services;
using Xunit;

namespace Pointframe.Tests.Engine;

public class DesktopObservationPixelTests
{
    [Fact]
    public async Task ObserveReturnsEncodedPixels()
    {
        var service = CreateService(new StubCaptureEngine(800, 600));

        var result = await service.ObserveAsync(CreateRequest(new PixelBounds(0, 0, 800, 600)));

        var image = Assert.Single(result.Observation.Images);
        Assert.NotNull(image.PngBytes);
        Assert.NotEmpty(image.PngBytes!);
    }

    [Fact]
    public async Task ObserveKeepsSmallCapturesAtFullSize()
    {
        var service = CreateService(new StubCaptureEngine(800, 600));

        var result = await service.ObserveAsync(CreateRequest(new PixelBounds(0, 0, 800, 600)));

        var image = Assert.Single(result.Observation.Images);
        Assert.Equal(800, image.Width);
        Assert.Equal(600, image.Height);
    }

    [Fact]
    public async Task ObserveDownscalesLargeCapturesWithinTheImageLimit()
    {
        var service = CreateService(new StubCaptureEngine(3840, 2160));

        var result = await service.ObserveAsync(CreateRequest(new PixelBounds(0, 0, 3840, 2160)));

        var image = Assert.Single(result.Observation.Images);
        Assert.True(
            Math.Max(image.Width, image.Height) <= DesktopTestingLimits.MaxImageLongestEdge,
            $"Preview longest edge was {Math.Max(image.Width, image.Height)}.");
        Assert.Equal(3840, image.DesktopBoundsPixels.Width);
    }

    [Fact]
    public async Task PreviewCoordinatesRescaleBackToTheDesktop()
    {
        // The whole reason coordinates are a hazard: the model sees a 1600-wide image of a 3840-wide
        // monitor, so a click at the middle of the preview has to land at the middle of the monitor.
        var service = CreateService(new StubCaptureEngine(3840, 2160));
        var result = await service.ObserveAsync(CreateRequest(new PixelBounds(0, 0, 3840, 2160)));
        var image = Assert.Single(result.Observation.Images);

        var point = service.ToDesktopPixels(
            result.Observation.ObservationRef,
            image.ImageRef,
            image.Width / 2,
            image.Height / 2);

        Assert.InRange(point.X, 1900, 1940);
        Assert.InRange(point.Y, 1060, 1100);
    }

    [Fact]
    public async Task PreviewCoordinatesRescaleOnAMonitorWithANonZeroOrigin()
    {
        var service = CreateService(new StubCaptureEngine(3840, 2160));
        var result = await service.ObserveAsync(CreateRequest(new PixelBounds(-3840, 0, 3840, 2160)));
        var image = Assert.Single(result.Observation.Images);

        var origin = service.ToDesktopPixels(result.Observation.ObservationRef, image.ImageRef, 0, 0);

        Assert.Equal(-3840, origin.X);
        Assert.Equal(0, origin.Y);
    }

    [Fact]
    public async Task ObserveReturnsOneImagePerRequestedRectangle()
    {
        var service = CreateService(new StubCaptureEngine(400, 400));

        var result = await service.ObserveAsync(CreateRequest(
            new PixelBounds(0, 0, 400, 400),
            new PixelBounds(400, 0, 400, 400)));

        Assert.Equal(2, result.Observation.Images.Count);
        Assert.All(result.Observation.Images, image => Assert.NotNull(image.PngBytes));
    }

    private static DesktopObservationService CreateService(IDisplayCaptureEngine captureEngine) =>
        new(captureEngine, new DesktopObservationStore());

    private static DesktopObservationRequest CreateRequest(params PixelBounds[] bounds) =>
        new(
            new DesktopProcessIdentity(
                "process-1",
                1234,
                DateTimeOffset.UnixEpoch,
                "C:/fixture/fixture.exe",
                new string('a', 64)),
            bounds,
            IncludeUiAutomation: false);

    private sealed class StubCaptureEngine(int width, int height) : IDisplayCaptureEngine
    {
        public Bitmap Capture(PixelBounds boundsPixels) => new(width, height);

        public CapturedMonitor CaptureMonitor(string monitorName) =>
            throw new NotSupportedException();

        public IReadOnlyList<DisplayDescriptor> GetDisplays() => [];
    }
}
