using System.Drawing;
using Pointframe.Engine;
using Pointframe.Engine.Automation.Models;
using Pointframe.Mcp.Automation;
using Xunit;

namespace Pointframe.Tests.Mcp;

public sealed class DesktopOcrObservationTests
{
    [Fact]
    public async Task MissingImageReturnsUnavailableWithoutCallingOcr()
    {
        var ocr = new DesktopOcrObservationProvider(new FakeCapture(), new FakeOcr());
        var process = new DesktopProcessIdentity("process", 1, DateTimeOffset.UtcNow, "target.exe", "hash");
        var observation = new DesktopObservationResult(
            new DesktopObservation(
                1,
                "observation",
                process,
                DesktopTargetState.Running,
                DesktopObservationStatus.Available,
                [],
                DesktopUiAutomationStatus.Unavailable,
                DateTimeOffset.UtcNow),
            null,
            1);

        var result = await ocr.RecognizeAsync(observation, "missing");

        Assert.Equal("unavailable", result.Status);
        Assert.Equal("ImageNotFound", result.ErrorCode);
    }

    [Fact]
    public async Task EmptyOcrResultIsNotActionable()
    {
        var ocr = new DesktopOcrObservationProvider(new FakeCapture(), new FakeOcr());
        var now = DateTimeOffset.UtcNow;
        var process = new DesktopProcessIdentity("process", 1, now, "target.exe", "hash");
        var observation = new DesktopObservationResult(
            new DesktopObservation(
                1,
                "observation",
                process,
                DesktopTargetState.Running,
                DesktopObservationStatus.Available,
                [new DesktopImageReference("image", 2, 2, new PixelBounds(1, 2, 2, 2), now)],
                DesktopUiAutomationStatus.Unavailable,
                now),
            null,
            1);

        var result = await ocr.RecognizeAsync(observation, "image");

        Assert.Equal("empty", result.Status);
        Assert.Null(result.Text);
        Assert.Equal(new PixelBounds(1, 2, 2, 2), result.SourceBoundsPixels);
    }

    [Fact]
    public async Task RecognizedTextReturnsAvailableObservationWithSourceBounds()
    {
        var ocr = new DesktopOcrObservationProvider(new FakeCapture(), new FakeOcr("Settings"));
        var now = DateTimeOffset.UtcNow;
        var process = new DesktopProcessIdentity("process", 1, now, "target.exe", "hash");
        var observation = new DesktopObservationResult(
            new DesktopObservation(
                1,
                "observation",
                process,
                DesktopTargetState.Running,
                DesktopObservationStatus.Available,
                [new DesktopImageReference("image", 2, 2, new PixelBounds(1, 2, 2, 2), now)],
                DesktopUiAutomationStatus.Unavailable,
                now),
            null,
            1);

        var result = await ocr.RecognizeAsync(observation, "image");

        Assert.Equal("available", result.Status);
        Assert.Equal("Settings", result.Text);
        Assert.Equal(new PixelBounds(1, 2, 2, 2), result.SourceBoundsPixels);
        Assert.Null(result.ErrorCode);
    }

    private sealed class FakeCapture : IDisplayCaptureEngine
    {
        public IReadOnlyList<DisplayDescriptor> GetDisplays() => [];
        public Bitmap Capture(PixelBounds bounds) => new(bounds.Width, bounds.Height);
        public CapturedMonitor CaptureMonitor(string monitorName) => throw new NotSupportedException();
    }

    private sealed class FakeOcr(string? text = null) : IOcrEngineService
    {
        public Task<string?> RecognizeAsync(Bitmap bitmap, CancellationToken cancellationToken = default) => Task.FromResult(text);
    }
}
