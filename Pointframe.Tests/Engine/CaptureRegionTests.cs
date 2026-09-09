using Pointframe.Engine;
using Xunit;

namespace Pointframe.Tests.Engine;

public sealed class CaptureRegionTests
{
    private static readonly PixelBounds MonitorBounds = new(100, 200, 800, 600);

    [Fact]
    public void ResolveWithin_RegionInsideMonitor_OffsetsByMonitorOrigin()
    {
        var region = new CaptureRegion(10, 20, 300, 400);

        var resolved = region.ResolveWithin(MonitorBounds);

        Assert.Equal(new PixelBounds(110, 220, 300, 400), resolved);
    }

    [Fact]
    public void ResolveWithin_RegionFillsEntireMonitor_ResolvesToMonitorBounds()
    {
        var region = new CaptureRegion(0, 0, MonitorBounds.Width, MonitorBounds.Height);

        var resolved = region.ResolveWithin(MonitorBounds);

        Assert.Equal(MonitorBounds, resolved);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1, 10)]
    [InlineData(10, 0)]
    [InlineData(10, -1)]
    public void ResolveWithin_NonPositiveDimensions_Throws(int width, int height)
    {
        var region = new CaptureRegion(0, 0, width, height);

        Assert.Throws<ArgumentOutOfRangeException>(() => region.ResolveWithin(MonitorBounds));
    }

    [Theory]
    [InlineData(-1, 0, 10, 10)]
    [InlineData(0, -1, 10, 10)]
    [InlineData(795, 0, 10, 10)]
    [InlineData(0, 595, 10, 10)]
    public void ResolveWithin_RegionOutsideMonitorBounds_Throws(int x, int y, int width, int height)
    {
        var region = new CaptureRegion(x, y, width, height);

        Assert.Throws<ArgumentOutOfRangeException>(() => region.ResolveWithin(MonitorBounds));
    }

    [Fact]
    public void ResolveWithin_ExtremeRegionValues_DoNotOverflowBoundaryValidation()
    {
        var region = new CaptureRegion(int.MaxValue, 0, int.MaxValue, 1);

        Assert.Throws<ArgumentOutOfRangeException>(() => region.ResolveWithin(MonitorBounds));
    }
}
