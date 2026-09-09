using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using Pointframe.Engine;
using Xunit;

namespace Pointframe.Tests.Engine;

public sealed class CapturePreviewImageTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"Pointframe.Tests.{Guid.NewGuid():N}");

    public CapturePreviewImageTests()
    {
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public void CreateDownscaledPng_WhenSourceFitsWithinCap_ReturnsOriginalBytesUnchanged()
    {
        var path = SavePng(800, 600);
        var original = File.ReadAllBytes(path);

        var result = CapturePreviewImage.CreateDownscaledPng(path, maxLongestEdgePixels: 1600);

        Assert.Equal(original, result);
    }

    [Fact]
    public void CreateDownscaledPng_WhenSourceExceedsCap_DownscalesPreservingAspectRatio()
    {
        var path = SavePng(3200, 1800);

        var result = CapturePreviewImage.CreateDownscaledPng(path, maxLongestEdgePixels: 1600);

        using var stream = new MemoryStream(result);
        using var image = new Bitmap(stream);
        Assert.Equal(1600, image.Width);
        Assert.Equal(900, image.Height);
    }

    [Fact]
    public void CreateDownscaledPng_MissingFile_Throws()
    {
        Assert.ThrowsAny<IOException>(
            () => CapturePreviewImage.CreateDownscaledPng(Path.Combine(_directory, "does-not-exist.png")));
    }

    private string SavePng(int width, int height)
    {
        var path = Path.Combine(_directory, $"{width}x{height}.png");
        using var bitmap = new Bitmap(width, height);
        bitmap.Save(path, ImageFormat.Png);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
