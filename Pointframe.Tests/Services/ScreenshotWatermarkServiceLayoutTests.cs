using Pointframe.Services.Recording;
using Xunit;

namespace Pointframe.Tests.Services;

public sealed class ScreenshotWatermarkServiceLayoutTests
{
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
}
