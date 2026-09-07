using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Pointframe.AutomationTests.Support;

public sealed record AnnotationImageComparison(
    int ComparedPixels,
    int ChangedPixels,
    byte MaximumChannelDelta)
{
    public double ChangedPixelFraction =>
        ComparedPixels == 0 ? 0 : (double)ChangedPixels / ComparedPixels;
}

public static class AnnotationImageAssertions
{
    public static AnnotationImageComparison CompareRoi(
        string expectedPath,
        string actualPath,
        Int32Rect roi,
        byte allowedChannelDelta = 2)
    {
        var expected = ReadPixels(expectedPath);
        var actual = ReadPixels(actualPath);
        if (expected.Width != actual.Width || expected.Height != actual.Height)
        {
            throw new Xunit.Sdk.XunitException("The expected and actual images have different dimensions.");
        }

        var bounds = new Int32Rect(
            Math.Max(0, roi.X),
            Math.Max(0, roi.Y),
            Math.Min(roi.Width, expected.Width - Math.Max(0, roi.X)),
            Math.Min(roi.Height, expected.Height - Math.Max(0, roi.Y)));
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            throw new Xunit.Sdk.XunitException("The annotation ROI is empty or outside the image.");
        }

        var changed = 0;
        var maximumDelta = 0;
        var expectedPixels = expected.Pixels;
        var actualPixels = actual.Pixels;
        for (var y = bounds.Y; y < bounds.Y + bounds.Height; y++)
        {
            for (var x = bounds.X; x < bounds.X + bounds.Width; x++)
            {
                var offset = ((y * expected.Width) + x) * 4;
                var pixelDelta = (byte)Math.Max(
                    Math.Max(Math.Abs(expectedPixels[offset] - actualPixels[offset]), Math.Abs(expectedPixels[offset + 1] - actualPixels[offset + 1])),
                    Math.Max(Math.Abs(expectedPixels[offset + 2] - actualPixels[offset + 2]), Math.Abs(expectedPixels[offset + 3] - actualPixels[offset + 3])));
                maximumDelta = Math.Max(maximumDelta, pixelDelta);
                if (pixelDelta > allowedChannelDelta)
                {
                    changed++;
                }
            }
        }

        return new AnnotationImageComparison(bounds.Width * bounds.Height, changed, (byte)maximumDelta);
    }

    public static void AssertRestored(
        string expectedPath,
        string actualPath,
        Int32Rect roi,
        double maximumChangedPixelFraction = 0.001)
    {
        var comparison = CompareRoi(expectedPath, actualPath, roi);
        Xunit.Assert.InRange(comparison.ChangedPixelFraction, 0, maximumChangedPixelFraction);
    }

    public static void AssertDrawnDifference(
        string baselinePath,
        string drawnPath,
        Int32Rect roi,
        double minimumChangedPixelFraction = 0.001)
    {
        var comparison = CompareRoi(baselinePath, drawnPath, roi, allowedChannelDelta: 2);
        Xunit.Assert.True(
            comparison.ChangedPixelFraction >= minimumChangedPixelFraction,
            $"The annotation ROI changed by {comparison.ChangedPixelFraction:P3}, below the required {minimumChangedPixelFraction:P3}.");
    }

    private static (int Width, int Height, byte[] Pixels) ReadPixels(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var source = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
        var pixels = new byte[source.PixelWidth * source.PixelHeight * 4];
        source.CopyPixels(pixels, source.PixelWidth * 4, 0);
        return (source.PixelWidth, source.PixelHeight, pixels);
    }
}
