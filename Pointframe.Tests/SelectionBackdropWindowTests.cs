using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace Pointframe.Tests;

public sealed class SelectionBackdropWindowTests
{
    [Fact]
    public void CreateDimmedSnapshot_DarkensSourceBitmap()
    {
        var source = CreateSolidBitmap(Colors.White);

        var result = SelectionBackdropWindow.CreateDimmedSnapshot(source);

        Assert.True(result.IsFrozen);

        var pixel = ReadPixel(result, 0, 0);
        Assert.True(pixel.R < 255);
        Assert.True(pixel.G < 255);
        Assert.True(pixel.B < 255);
        Assert.True(pixel.R > 100);
    }

    private static BitmapSource CreateSolidBitmap(Color color)
    {
        var pixels = new byte[] { color.B, color.G, color.R, color.A };
        var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, pixels, 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static Color ReadPixel(BitmapSource bitmap, int x, int y)
    {
        var formattedBitmap = bitmap.Format == PixelFormats.Bgra32
            ? bitmap
            : new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var pixel = new byte[4];
        formattedBitmap.CopyPixels(new System.Windows.Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
    }
}