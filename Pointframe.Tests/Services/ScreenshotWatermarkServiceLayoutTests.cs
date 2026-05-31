using System.Windows.Media;
using System.Windows.Media.Imaging;
using Pointframe.Services.Recording;
using Pointframe.Tests.Services.Handlers;
using Xunit;

namespace Pointframe.Tests.Services;

public sealed class ScreenshotWatermarkServiceLayoutTests
{
    [Fact]
    public void Apply_WhenSourceIsNull_ThrowsArgumentNullException()
    {
        var sut = new ScreenshotWatermarkService();
        var settings = new ScreenshotWatermarkSettings();

        Assert.Throws<ArgumentNullException>(() => sut.Apply(null!, settings));
    }

    [Fact]
    public void Apply_WhenSettingsIsNull_ThrowsArgumentNullException()
    {
        var sut = new ScreenshotWatermarkService();
        var source = CreateBitmap();

        Assert.Throws<ArgumentNullException>(() => sut.Apply(source, null!));
    }

    [Fact]
    public void Apply_WhenTemplateSelectionIsTimezone_RendersWatermark()
    {
        StaTestHelper.Run(() =>
        {
            var sut = new ScreenshotWatermarkService();
            var source = CreateBitmap();
            var settings = new ScreenshotWatermarkSettings
            {
                TextTemplate = WatermarkTextTemplate.TimezoneOnly,
            };

            var result = sut.Apply(source, settings);

            Assert.NotSame(source, result);
        });
    }

    [Fact]
    public void Apply_WhenTemplateHasText_RendersWatermarkAndReturnsNewBitmap()
    {
        StaTestHelper.Run(() =>
        {
            var sut = new ScreenshotWatermarkService();
            var source = CreateBitmap(width: 240, height: 140);
            var settings = new ScreenshotWatermarkSettings
            {
                TextTemplate = WatermarkTextTemplate.DateTime,
                Position = WatermarkPosition.TopLeft,
                Margin = 8,
                FontSize = 20,
                BackgroundEnabled = true,
                Opacity = 1
            };

            var result = sut.Apply(source, settings);

            Assert.NotSame(source, result);
            Assert.Equal(source.PixelWidth, result.PixelWidth);
            Assert.Equal(source.PixelHeight, result.PixelHeight);
            Assert.True(ContainsNonZeroPixel(result));
        });
    }

    [Fact]
    public void Apply_WhenColorHexIsInvalid_FallsBackWithoutThrowing()
    {
        StaTestHelper.Run(() =>
        {
            var sut = new ScreenshotWatermarkService();
            var source = CreateBitmap(width: 200, height: 120);
            var settings = new ScreenshotWatermarkSettings
            {
                TextTemplate = WatermarkTextTemplate.DateTime,
                ColorHex = "not-a-color",
                BackgroundEnabled = false
            };

            var result = sut.Apply(source, settings);

            Assert.Equal(source.PixelWidth, result.PixelWidth);
            Assert.Equal(source.PixelHeight, result.PixelHeight);
        });
    }

    [Fact]
    public void Apply_WhenFontSizeIsNonPositive_UsesFallbackWithoutThrowing()
    {
        StaTestHelper.Run(() =>
        {
            var sut = new ScreenshotWatermarkService();
            var source = CreateBitmap(width: 200, height: 120);
            var settings = new ScreenshotWatermarkSettings
            {
                TextTemplate = WatermarkTextTemplate.DateTime,
                FontSize = 0d,
                BackgroundEnabled = false
            };

            var result = sut.Apply(source, settings);

            Assert.Equal(source.PixelWidth, result.PixelWidth);
            Assert.Equal(source.PixelHeight, result.PixelHeight);
        });
    }

    [Theory]
    [InlineData(WatermarkPosition.TopLeft, 16, 16)]
    [InlineData(WatermarkPosition.TopRight, 784, 16)]
    [InlineData(WatermarkPosition.BottomLeft, 16, 584)]
    [InlineData(WatermarkPosition.BottomRight, 784, 584)]
    public void ComputePosition_CornerPlacements_RespectMargin(WatermarkPosition position, double expectedX, double expectedY)
    {
        var (x, y) = ScreenshotWatermarkService.ComputePosition(position, 1000, 700, 200, 100, 16);

        Assert.Equal(expectedX, x);
        Assert.Equal(expectedY, y);
    }

    [Fact]
    public void ComputePosition_Center_CentersBox()
    {
        var (x, y) = ScreenshotWatermarkService.ComputePosition(WatermarkPosition.Center, 1000, 700, 200, 100, 16);

        Assert.Equal(400, x);
        Assert.Equal(300, y);
    }

    [Fact]
    public void ComputePosition_WhenPositionIsUnknown_UsesBottomRightFallback()
    {
        var (x, y) = ScreenshotWatermarkService.ComputePosition((WatermarkPosition)999, 1000, 700, 200, 100, 16);

        Assert.Equal(784, x);
        Assert.Equal(584, y);
    }

    private static BitmapSource CreateBitmap(int width = 120, int height = 80)
    {
        var pixels = new byte[width * height * 4];
        var bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static bool ContainsNonZeroPixel(BitmapSource bitmap)
    {
        var bytes = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(bytes, bitmap.PixelWidth * 4, 0);
        return bytes.Any(value => value != 0);
    }
}
