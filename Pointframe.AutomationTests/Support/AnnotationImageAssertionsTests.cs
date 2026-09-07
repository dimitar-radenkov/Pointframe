using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace Pointframe.AutomationTests.Support;

public sealed class AnnotationImageAssertionsTests
{
    [Fact]
    public void RestoreOracleRejectsAChangedRoi()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PointframeImageAssertions", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var baseline = Path.Combine(directory, "baseline.png");
        var changed = Path.Combine(directory, "changed.png");
        try
        {
            WriteImage(baseline, Colors.White);
            WriteImage(changed, Colors.Red);
            Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
                AnnotationImageAssertions.AssertRestored(
                    baseline,
                    changed,
                    new Int32Rect(0, 0, 4, 4)));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void DrawOracleRejectsNoDraw()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PointframeImageAssertions", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var baseline = Path.Combine(directory, "baseline.png");
        var same = Path.Combine(directory, "same.png");
        try
        {
            WriteImage(baseline, Colors.White);
            WriteImage(same, Colors.White);
            Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
                AnnotationImageAssertions.AssertDrawnDifference(
                    baseline,
                    same,
                    new Int32Rect(0, 0, 4, 4)));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static void WriteImage(string path, Color color)
    {
        var pixels = Enumerable.Repeat(color, 16).SelectMany(value => new[] { value.B, value.G, value.R, value.A }).ToArray();
        var bitmap = BitmapSource.Create(4, 4, 96, 96, PixelFormats.Bgra32, null, pixels, 16);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
