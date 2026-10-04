using System.Windows;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Pointframe.Services;
using Xunit;
using static Pointframe.Tests.Services.ScrollingCaptureStitcherTests;

namespace Pointframe.Tests.Services;

public sealed class ScrollingCaptureServiceTests
{
    private static readonly Int32Rect Region = new(200, 300, 50, 100);

    [Fact]
    public async Task CaptureAsync_ScrollsToTheEnd_StitchesTheWholePageAndStopsAtEndOfContent()
    {
        var page = CreatePage(Region.Width, 700, seed: 21);
        var fake = new FakeScrollingPage(page, Region, pixelsPerNotch: 10);

        var result = await fake.CreateService().CaptureAsync(Region);

        Assert.Equal(ScrollingCaptureStopReason.EndOfContent, result.StopReason);
        AssertSamePixels(page, PixelFrame.FromBitmap(result.Image));
        fake.ScrollInput.Verify(input => input.BeginScrolling(225, 350), Times.Once);
        fake.CursorRestore.Verify(restore => restore.Dispose(), Times.Once);
    }

    [Fact]
    public async Task CaptureAsync_FrameLimitReached_StopsWithFrameLimit()
    {
        var page = CreatePage(Region.Width, 2000, seed: 22);
        var fake = new FakeScrollingPage(page, Region, pixelsPerNotch: 10);
        var service = fake.CreateService(maxFrames: 3);

        var result = await service.CaptureAsync(Region);

        Assert.Equal(ScrollingCaptureStopReason.FrameLimit, result.StopReason);
        Assert.Equal(3, result.FrameCount);
        Assert.Equal(3, result.StopLimit);
        AssertSamePixels(Crop(page, 0, result.Image.PixelHeight), PixelFrame.FromBitmap(result.Image));
    }

    [Fact]
    public async Task CaptureAsync_HeightLimitReached_StopsWithHeightLimit()
    {
        var page = CreatePage(Region.Width, 2000, seed: 23);
        var fake = new FakeScrollingPage(page, Region, pixelsPerNotch: 10);
        var service = fake.CreateService(maxHeight: 250);

        var result = await service.CaptureAsync(Region);

        Assert.Equal(ScrollingCaptureStopReason.HeightLimit, result.StopReason);
        Assert.Equal(250, result.StopLimit);
        Assert.True(result.Image.PixelHeight >= 250);
        AssertSamePixels(Crop(page, 0, result.Image.PixelHeight), PixelFrame.FromBitmap(result.Image));
    }

    [Fact]
    public async Task CaptureAsync_ScrollJumpsPastTheViewport_StopsWithNoOverlapAndKeepsTheFirstFrame()
    {
        var page = CreatePage(Region.Width, 2000, seed: 24);
        var fake = new FakeScrollingPage(page, Region, pixelsPerNotch: 60);

        var result = await fake.CreateService().CaptureAsync(Region);

        Assert.Equal(ScrollingCaptureStopReason.NoOverlap, result.StopReason);
        AssertSamePixels(Crop(page, 0, Region.Height), PixelFrame.FromBitmap(result.Image));
        fake.CursorRestore.Verify(restore => restore.Dispose(), Times.Once);
    }

    [Fact]
    public async Task CaptureAsync_ContentThatCannotScroll_ReturnsTheSingleFrame()
    {
        var page = CreatePage(Region.Width, Region.Height, seed: 25);
        var fake = new FakeScrollingPage(page, Region, pixelsPerNotch: 10);

        var result = await fake.CreateService().CaptureAsync(Region);

        Assert.Equal(ScrollingCaptureStopReason.EndOfContent, result.StopReason);
        Assert.Equal(2, result.FrameCount);
        AssertSamePixels(page, PixelFrame.FromBitmap(result.Image));
    }

    [Fact]
    public async Task CaptureAsync_ReportsFrameAndHeightProgress()
    {
        var page = CreatePage(Region.Width, 300, seed: 27);
        var fake = new FakeScrollingPage(page, Region, pixelsPerNotch: 10);
        var observer = new Mock<IScrollingCaptureObserver>();
        var progress = new List<ScrollingCaptureProgress>();
        observer.Setup(item => item.HideForCaptureAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        observer
            .Setup(item => item.ReportProgressAsync(It.IsAny<ScrollingCaptureProgress>(), It.IsAny<CancellationToken>()))
            .Callback<ScrollingCaptureProgress, CancellationToken>((value, _) => progress.Add(value))
            .Returns(Task.CompletedTask);

        var result = await fake.CreateService().CaptureAsync(Region, observer: observer.Object);

        Assert.Equal(result.FrameCount, progress[^1].FrameCount);
        Assert.Equal(result.Image.PixelHeight, progress[^1].HeightPixels);
        Assert.Equal(1, progress[0].FrameCount);
        Assert.Equal(Region.Height, progress[0].HeightPixels);
        observer.Verify(item => item.HideForCaptureAsync(It.IsAny<CancellationToken>()), Times.Exactly(result.FrameCount));
    }

    [Fact]
    public async Task CaptureAsync_StitchesNumberedRowsInSourceOrder()
    {
        const int pageHeight = 700;
        var rowNumbers = Enumerable.Range(0, pageHeight)
            .SelectMany(row => Enumerable.Repeat(unchecked((int)0xFF000000) | row, Region.Width))
            .ToArray();
        var page = new PixelFrame(Region.Width, pageHeight, rowNumbers);
        var fake = new FakeScrollingPage(page, Region, pixelsPerNotch: 10);

        var result = await fake.CreateService().CaptureAsync(Region);
        var stitched = PixelFrame.FromBitmap(result.Image);

        Assert.Equal(ScrollingCaptureStopReason.EndOfContent, result.StopReason);
        Assert.Equal(pageHeight, stitched.Height);
        for (var row = 0; row < pageHeight; row++)
        {
            Assert.All(stitched.Row(row).ToArray(), pixel => Assert.Equal(unchecked((int)0xFF000000) | row, pixel));
        }
    }

    [Fact]
    public async Task CaptureAsync_CancellationAfterFirstFrame_ReturnsPartialImageAndRestoresCursor()
    {
        var page = CreatePage(Region.Width, 2000, seed: 28);
        var fake = new FakeScrollingPage(page, Region, pixelsPerNotch: 10);
        using var cancellation = new CancellationTokenSource();
        fake.ScrollInput
            .Setup(input => input.ScrollDown(It.IsAny<int>()))
            .Callback(() => cancellation.Cancel());

        var result = await fake.CreateService().CaptureAsync(Region, cancellation.Token);

        Assert.Equal(ScrollingCaptureStopReason.Cancelled, result.StopReason);
        Assert.Equal(1, result.FrameCount);
        AssertSamePixels(Crop(page, 0, Region.Height), PixelFrame.FromBitmap(result.Image));
        fake.ScreenCapture.Verify(capture => capture.Capture(Region.X, Region.Y, Region.Width, Region.Height), Times.Once);
        fake.CursorRestore.Verify(restore => restore.Dispose(), Times.Once);
    }

    [Fact]
    public async Task CaptureAsync_CancellationBeforeFirstFrameReturnsNoImageAndDoesNotStartCursorTracking()
    {
        var fake = new FakeScrollingPage(CreatePage(Region.Width, 2000, seed: 30), Region, pixelsPerNotch: 10);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fake.CreateService().CaptureAsync(Region, cancellation.Token));

        fake.ScreenCapture.Verify(capture => capture.Capture(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()), Times.Never);
        fake.ScrollInput.Verify(input => input.BeginScrolling(It.IsAny<int>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task CaptureAsync_EmptyRegion_Throws()
    {
        var fake = new FakeScrollingPage(CreatePage(10, 10, seed: 26), Region, pixelsPerNotch: 10);

        await Assert.ThrowsAsync<ArgumentException>(() => fake.CreateService().CaptureAsync(new Int32Rect(0, 0, 0, 10)));
    }

    [Theory]
    [InlineData(30, 3, 100, 6)]
    [InlineData(30, 3, 1000, 15)]
    [InlineData(90, 3, 100, 2)]
    [InlineData(90, 1, 100, 1)]
    [InlineData(0, 4, 100, 4)]
    public void NextWheelNotches_AimsForSixtyPercentOfTheBody(int shift, int notches, int bodyHeight, int expected)
    {
        Assert.Equal(expected, ScrollingCaptureService.NextWheelNotches(shift, notches, bodyHeight));
    }

    [Theory]
    [InlineData(nameof(ScrollingCaptureStopReason.EndOfContent), "end_of_content")]
    [InlineData(nameof(ScrollingCaptureStopReason.NoOverlap), "no_overlap")]
    [InlineData(nameof(ScrollingCaptureStopReason.FrameLimit), "frame_limit")]
    [InlineData(nameof(ScrollingCaptureStopReason.HeightLimit), "height_limit")]
    public void ToTelemetryValue_MapsEveryStopReason(string stopReason, string expected)
    {
        Assert.Equal(expected, CaptureLaunchService.ToTelemetryValue(Enum.Parse<ScrollingCaptureStopReason>(stopReason)));
    }

    [Fact]
    public void ToTelemetryValue_CoversEveryStopReason()
    {
        Assert.All(
            Enum.GetValues<ScrollingCaptureStopReason>(),
            stopReason => Assert.NotEqual("unknown", CaptureLaunchService.ToTelemetryValue(stopReason)));
    }

    [Theory]
    [InlineData(nameof(ScrollingCaptureStopReason.EndOfContent), null)]
    [InlineData(nameof(ScrollingCaptureStopReason.NoOverlap), "Stopped: no matching overlap. Showing partial capture.")]
    [InlineData(nameof(ScrollingCaptureStopReason.FrameLimit), "Stopped at the 40-frame limit.")]
    [InlineData(nameof(ScrollingCaptureStopReason.HeightLimit), "Stopped at the 20,000 px limit.")]
    [InlineData(nameof(ScrollingCaptureStopReason.Cancelled), "Capture canceled. Showing 3 frames.")]
    public void ToScrollingCaptureNotice_ExplainsEveryStopOutcome(string stopReason, string? expected)
    {
        var reason = Enum.Parse<ScrollingCaptureStopReason>(stopReason);
        var result = new ScrollingCaptureResult(
            CreatePage(Region.Width, 100, seed: 29).ToBitmap(),
            reason == ScrollingCaptureStopReason.Cancelled ? 3 : 40,
            reason,
            reason switch
            {
                ScrollingCaptureStopReason.FrameLimit => 40,
                ScrollingCaptureStopReason.HeightLimit => 20000,
                _ => null,
            });

        Assert.Equal(expected, CaptureLaunchService.ToScrollingCaptureNotice(result));
    }

    private sealed class FakeScrollingPage
    {
        private readonly PixelFrame _page;
        private readonly Int32Rect _region;
        private int _offset;

        public FakeScrollingPage(PixelFrame page, Int32Rect region, int pixelsPerNotch)
        {
            _page = page;
            _region = region;
            ScrollInput
                .Setup(input => input.BeginScrolling(It.IsAny<int>(), It.IsAny<int>()))
                .Returns(CursorRestore.Object);
            ScrollInput
                .Setup(input => input.ScrollDown(It.IsAny<int>()))
                .Callback<int>(notches => _offset = Math.Min(_offset + (notches * pixelsPerNotch), _page.Height - _region.Height));
            ScreenCapture
                .Setup(capture => capture.Capture(region.X, region.Y, region.Width, region.Height))
                .Returns(() => Crop(_page, _offset, _region.Height).ToBitmap());
        }

        public Mock<IScreenCaptureService> ScreenCapture { get; } = new();

        public Mock<IScrollInputService> ScrollInput { get; } = new();

        public Mock<IDisposable> CursorRestore { get; } = new();

        public ScrollingCaptureService CreateService(int maxFrames = 100, int maxHeight = 100000)
        {
            return new ScrollingCaptureService(ScreenCapture.Object, ScrollInput.Object, NullLogger<ScrollingCaptureService>.Instance)
            {
                Options = new ScrollingCaptureOptions(TimeSpan.Zero, 3, maxFrames, maxHeight),
            };
        }

    }
}
