using Pointframe.Engine;
using Pointframe.Engine.Automation.Models;
using Pointframe.Mcp.Automation;
using Xunit;

namespace Pointframe.Tests.Mcp;

public sealed class DesktopOcrObservationTests
{
    [Fact]
    public async Task MissingImageReturnsUnavailableWithoutOcrResult()
    {
        var mapper = new DesktopOcrObservationProvider();
        var observation = CreateObservation([], null);

        var result = await mapper.RecognizeAsync(observation, "missing");

        Assert.Equal("unavailable", result.Status);
        Assert.Equal("ImageNotFound", result.ErrorCode);
    }

    [Fact]
    public async Task EmptyOcrResultIsNotActionable()
    {
        var now = DateTimeOffset.UtcNow;
        var bounds = new PixelBounds(1, 2, 2, 2);
        var observation = CreateObservation(
            [new DesktopImageReference("image", 2, 2, bounds, now)],
            new DesktopObservationOcrResult("image", null));

        var result = await new DesktopOcrObservationProvider().RecognizeAsync(observation, "image");

        Assert.Equal("empty", result.Status);
        Assert.Null(result.Text);
        Assert.Equal(bounds, result.SourceBoundsPixels);
    }

    [Fact]
    public async Task MissingOcrEngineReturnsUnavailableWithReason()
    {
        var now = DateTimeOffset.UtcNow;
        var bounds = new PixelBounds(1, 2, 2, 2);
        var observation = CreateObservation(
            [new DesktopImageReference("image", 2, 2, bounds, now)],
            new DesktopObservationOcrResult("image", null, IsAvailable: false));

        var result = await new DesktopOcrObservationProvider().RecognizeAsync(observation, "image");

        Assert.Equal("unavailable", result.Status);
        Assert.Equal("OcrEngineUnavailable", result.ErrorCode);
        Assert.Equal(bounds, result.SourceBoundsPixels);
    }

    [Fact]
    public async Task RecognizedTextReturnsAvailableObservationWithSourceBounds()
    {
        var now = DateTimeOffset.UtcNow;
        var bounds = new PixelBounds(1, 2, 2, 2);
        var observation = CreateObservation(
            [new DesktopImageReference("image", 2, 2, bounds, now)],
            new DesktopObservationOcrResult("image", "Settings"));

        var result = await new DesktopOcrObservationProvider().RecognizeAsync(observation, "image");

        Assert.Equal("available", result.Status);
        Assert.Equal("Settings", result.Text);
        Assert.Equal(bounds, result.SourceBoundsPixels);
        Assert.Null(result.ErrorCode);
    }

    private static DesktopObservationResult CreateObservation(
        IReadOnlyList<DesktopImageReference> images,
        DesktopObservationOcrResult? ocr)
    {
        var now = DateTimeOffset.UtcNow;
        var process = new DesktopProcessIdentity("process", 1, now, "target.exe", "hash");
        return new DesktopObservationResult(
            new DesktopObservation(1, "observation", process, DesktopTargetState.Running,
                DesktopObservationStatus.Available, images, DesktopUiAutomationStatus.Unavailable, now),
            null,
            1,
            ocr);
    }
}
