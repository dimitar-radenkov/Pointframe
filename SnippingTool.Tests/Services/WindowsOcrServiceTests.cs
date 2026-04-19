using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Pointframe.Services;
using Windows.Graphics.Imaging;
using Xunit;

namespace Pointframe.Tests.Services;

public sealed class WindowsOcrServiceTests
{
    [Fact]
    public void ConvertToSoftwareBitmap_CopiesDimensionsAndPixels()
    {
        var pixels = new byte[]
        {
            0, 0, 0, 255,
            255, 0, 0, 255,
            0, 255, 0, 255,
            0, 0, 255, 255,
        };
        var bitmap = BitmapSource.Create(
            2,
            2,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            2 * 4);

        var method = typeof(WindowsOcrService).GetMethod("ConvertToSoftwareBitmap", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        using var softwareBitmap = (SoftwareBitmap)method.Invoke(null, [bitmap])!;

        Assert.Equal(2, softwareBitmap.PixelWidth);
        Assert.Equal(2, softwareBitmap.PixelHeight);
        Assert.Equal(BitmapPixelFormat.Bgra8, softwareBitmap.BitmapPixelFormat);
        Assert.Equal(BitmapAlphaMode.Premultiplied, softwareBitmap.BitmapAlphaMode);
    }
}