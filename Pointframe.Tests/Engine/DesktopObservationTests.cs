using System.Drawing;
using Pointframe.Engine;
using Pointframe.Engine.Automation.Models;
using Pointframe.Engine.Automation.Services;
using Xunit;

namespace Pointframe.Tests.Engine;

public sealed class DesktopObservationTests
{
    [Fact]
    public async Task ObserveAsync_CapturesWithoutUiAutomation()
    {
        var capture = new FakeCapture();
        var process = new DesktopProcessIdentity("process-1", 1, DateTimeOffset.UtcNow, "target.exe", "hash");
        var store = new DesktopObservationStore();
        var service = new DesktopObservationService(capture, store);

        var result = await service.ObserveAsync(new DesktopObservationRequest(
            process,
            [new PixelBounds(-20, 30, 4, 3)],
            IncludeUiAutomation: true));

        Assert.Equal(DesktopObservationStatus.Available, result.Observation.ObservationStatus);
        Assert.Equal(DesktopUiAutomationStatus.Unavailable, result.Observation.UiaStatus);
        Assert.Equal(new PixelBounds(-20, 30, 4, 3), capture.Bounds);
        Assert.Single(result.Observation.Images);
    }

    private sealed class FakeCapture : IDisplayCaptureEngine
    {
        public PixelBounds Bounds { get; private set; }

        public IReadOnlyList<DisplayDescriptor> GetDisplays() => [];

        public Bitmap Capture(PixelBounds boundsPixels)
        {
            Bounds = boundsPixels;
            return new Bitmap(boundsPixels.Width, boundsPixels.Height);
        }

        public CapturedMonitor CaptureMonitor(string monitorName) => throw new NotSupportedException();
    }
}
