using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices.WindowsRuntime;
using Pointframe.Engine;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
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

        var buffer = new Windows.Storage.Streams.Buffer((uint)(softwareBitmap.PixelWidth * softwareBitmap.PixelHeight * 4));
        softwareBitmap.CopyToBuffer(buffer);
        var pixelBytes = buffer.ToArray();

        AssertPixelBgra(pixelBytes, pixelIndex: 0, Color.Black);
        AssertPixelBgra(pixelBytes, pixelIndex: 1, Color.Red);
        AssertPixelBgra(pixelBytes, pixelIndex: 2, Color.Green);
        AssertPixelBgra(pixelBytes, pixelIndex: 3, Color.Blue);
    }

    [Fact]
    public async Task RecognizeAsync_NullBitmap_Throws()
    {
        var sut = new WindowsOcrEngineService();

        await Assert.ThrowsAsync<ArgumentNullException>(() => sut.RecognizeAsync(null!));
    }

    private static void AssertPixelBgra(byte[] pixels, int pixelIndex, Color expected)
    {
        var offset = pixelIndex * 4;
        Assert.Equal(expected.B, pixels[offset]);
        Assert.Equal(expected.G, pixels[offset + 1]);
        Assert.Equal(expected.R, pixels[offset + 2]);
        Assert.Equal(expected.A, pixels[offset + 3]);
    }
}
