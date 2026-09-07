using Pointframe.Engine;
using Xunit;

namespace Pointframe.Tests.Engine;

public sealed class DisplayCaptureEngineTests
{
    [Fact]
    public void Capture_InvalidWidth_ThrowsArgumentOutOfRangeException()
    {
        var sut = new DisplayCaptureEngine();

        Assert.Throws<ArgumentOutOfRangeException>(() => sut.Capture(new PixelBounds(0, 0, 0, 1)));
    }

    [Fact]
    public void GetDisplays_ReturnsDisplayDescriptorsWithPhysicalBoundsAndDpiScales()
    {
        var sut = new DisplayCaptureEngine();

        var displays = sut.GetDisplays();

        Assert.NotEmpty(displays);
        Assert.All(displays, display =>
        {
            Assert.False(string.IsNullOrWhiteSpace(display.MonitorName));
            Assert.True(display.BoundsPixels.Width > 0);
            Assert.True(display.BoundsPixels.Height > 0);
            Assert.True(display.DpiScaleX > 0);
            Assert.True(display.DpiScaleY > 0);
        });
    }
}