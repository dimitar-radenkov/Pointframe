using System.Windows.Media;

namespace Pointframe.Services;

internal sealed class PixelFrame
{
    private const int OpaqueAlpha = unchecked((int)0xFF000000);

    public PixelFrame(int width, int height, int[] pixels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentNullException.ThrowIfNull(pixels);
        if (pixels.Length != width * height)
        {
            throw new ArgumentException($"Expected {width * height} pixels for {width}x{height}, got {pixels.Length}.", nameof(pixels));
        }

        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }

    public int Height { get; }

    public int[] Pixels { get; }

    public ReadOnlySpan<int> Row(int y)
    {
        return Pixels.AsSpan(y * Width, Width);
    }

    public static PixelFrame FromBitmap(BitmapSource bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        var source = bitmap.Format == PixelFormats.Bgra32
            ? bitmap
            : new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var width = source.PixelWidth;
        var height = source.PixelHeight;
        var pixels = new int[width * height];
        source.CopyPixels(pixels, width * 4, 0);

        // Screen captures are Bgr32, whose unused alpha byte can be zero; captures are always opaque.
        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i] |= OpaqueAlpha;
        }

        return new PixelFrame(width, height, pixels);
    }

    public BitmapSource ToBitmap()
    {
        var bitmap = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgra32, null, Pixels, Width * 4);
        bitmap.Freeze();
        return bitmap;
    }
}
