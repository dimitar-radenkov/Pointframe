using System.Drawing;
using Microsoft.Extensions.Logging.Abstractions;
using Pointframe.Engine;
using Xunit;

namespace Pointframe.Tests.Services;

public sealed class ScreenCaptureServiceTests
{
    [Fact]
    public void Capture_OnePixel_ReturnsBitmapOfRequestedSize()
    {
        var captureEngine = new FakeDisplayCaptureEngine();
        var sut = new ScreenCaptureService(NullLogger<ScreenCaptureService>.Instance, captureEngine);

        var bitmap = sut.Capture(0, 0, 1, 1);

        Assert.Equal(1, bitmap.PixelWidth);
        Assert.Equal(1, bitmap.PixelHeight);
        Assert.Equal(new PixelBounds(0, 0, 1, 1), captureEngine.CapturedBounds);
    }

    [Fact]
    public void Capture_ZeroWidth_Throws()
    {
        var sut = new ScreenCaptureService(NullLogger<ScreenCaptureService>.Instance, new DisplayCaptureEngine());

        Assert.Throws<ArgumentOutOfRangeException>(() => sut.Capture(0, 0, 0, 1));
    }

    private sealed class FakeDisplayCaptureEngine : IDisplayCaptureEngine
    {
        public PixelBounds? CapturedBounds { get; private set; }

        public IReadOnlyList<DisplayDescriptor> GetDisplays()
        {
            return [];
        }

        public Bitmap Capture(PixelBounds boundsPixels)
        {
            CapturedBounds = boundsPixels;
            return new Bitmap(boundsPixels.Width, boundsPixels.Height);
        }

        public CapturedMonitor CaptureMonitor(string monitorName)
        {
            throw new NotSupportedException();
        }
    }
}
