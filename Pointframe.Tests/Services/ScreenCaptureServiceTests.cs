using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Pointframe.Tests.Services;

public sealed class ScreenCaptureServiceTests
{
    [Fact]
    public void Capture_OnePixel_ReturnsBitmapOfRequestedSize()
    {
        var sut = new ScreenCaptureService(NullLogger<ScreenCaptureService>.Instance);

        var bitmap = sut.Capture(0, 0, 1, 1);

        Assert.Equal(1, bitmap.PixelWidth);
        Assert.Equal(1, bitmap.PixelHeight);
    }

    [Fact]
    public void Capture_ZeroWidth_Throws()
    {
        var sut = new ScreenCaptureService(NullLogger<ScreenCaptureService>.Instance);

        Assert.ThrowsAny<Exception>(() => sut.Capture(0, 0, 0, 1));
    }
}