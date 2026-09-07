using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using Pointframe.Engine;
using Windows.Graphics.Imaging;
using Xunit;

namespace Pointframe.Tests.Engine;

public sealed class WindowsOcrEngineServiceTests
{
    [Fact]
    public void ConvertToSoftwareBitmap_CopiesDimensionsAndPixels()
    {
        using var bitmap = new Bitmap(2, 2, PixelFormat.Format32bppArgb);
        bitmap.SetPixel(0, 0, Color.Black);
        bitmap.SetPixel(1, 0, Color.Red);
        bitmap.SetPixel(0, 1, Color.Green);
        bitmap.SetPixel(1, 1, Color.Blue);

        var method = typeof(WindowsOcrEngineService).GetMethod("ConvertToSoftwareBitmap", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        using var softwareBitmap = (SoftwareBitmap)method.Invoke(null, [bitmap])!;

        Assert.Equal(2, softwareBitmap.PixelWidth);
        Assert.Equal(2, softwareBitmap.PixelHeight);
        Assert.Equal(BitmapPixelFormat.Bgra8, softwareBitmap.BitmapPixelFormat);
        Assert.Equal(BitmapAlphaMode.Ignore, softwareBitmap.BitmapAlphaMode);
    }

    [Fact]
    public async Task RecognizeAsync_NullBitmap_Throws()
    {
        var sut = new WindowsOcrEngineService();

        await Assert.ThrowsAsync<ArgumentNullException>(() => sut.RecognizeAsync(null!));
    }
}
