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
