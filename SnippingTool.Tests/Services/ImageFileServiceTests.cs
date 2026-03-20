using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnippingTool.Services;
using Xunit;

namespace SnippingTool.Tests.Services;

public sealed class ImageFileServiceTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(),
        "SnippingTool.Tests",
        Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(".png")]
    [InlineData(".jpg")]
    [InlineData(".bmp")]
    public void LoadForAnnotation_LoadsSupportedFormats(string extension)
    {
        var path = CreateImageFile(extension);
        var sut = new ImageFileService();

        var result = sut.LoadForAnnotation(path);

        Assert.Equal(2, result.PixelWidth);
        Assert.Equal(2, result.PixelHeight);
        Assert.True(result.IsFrozen);
    }

    [Fact]
    public void LoadForAnnotation_WhenExtensionIsUnsupported_ThrowsNotSupportedException()
    {
        var path = Path.Combine(_tempDirectory, "image.gif");
        Directory.CreateDirectory(_tempDirectory);
        File.WriteAllText(path, "not-an-image");
        var sut = new ImageFileService();

        var exception = Assert.Throws<NotSupportedException>(() => sut.LoadForAnnotation(path));

        Assert.Contains("Unsupported image format", exception.Message);
    }

    [Fact]
    public void LoadForAnnotation_WhenFileIsMissing_ThrowsFileNotFoundException()
    {
        var path = Path.Combine(_tempDirectory, "missing.png");
        var sut = new ImageFileService();

        var exception = Assert.Throws<FileNotFoundException>(() => sut.LoadForAnnotation(path));

        Assert.Equal(path, exception.FileName);
    }

    [Fact]
    public void LoadForAnnotation_WhenFileIsCorrupt_ThrowsInvalidDataException()
    {
        Directory.CreateDirectory(_tempDirectory);
        var path = Path.Combine(_tempDirectory, "corrupt.png");
        File.WriteAllText(path, "not-a-real-png");
        var sut = new ImageFileService();

        var exception = Assert.Throws<InvalidDataException>(() => sut.LoadForAnnotation(path));

        Assert.Contains("could not be opened", exception.Message);
    }

    [Fact]
    public void LoadForAnnotation_LoadsBitmapIntoMemoryAndReleasesSourceFile()
    {
        var path = CreateImageFile(".png");
        var sut = new ImageFileService();

        var result = sut.LoadForAnnotation(path);
        File.Delete(path);

        Assert.False(File.Exists(path));
        Assert.Equal(2, result.PixelWidth);
        Assert.Equal(2, result.PixelHeight);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    private string CreateImageFile(string extension)
    {
        Directory.CreateDirectory(_tempDirectory);
        var path = Path.Combine(_tempDirectory, $"image{extension}");

        var bitmap = BitmapSource.Create(
            2,
            2,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            new byte[]
            {
                0, 0, 255, 255,
                0, 255, 0, 255,
                255, 0, 0, 255,
                255, 255, 255, 255
            },
            8);

        var encoder = CreateEncoder(extension);
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var stream = File.Create(path);
        encoder.Save(stream);

        return path;
    }

    private static BitmapEncoder CreateEncoder(string extension) => extension switch
    {
        ".png" => new PngBitmapEncoder(),
        ".jpg" => new JpegBitmapEncoder(),
        ".bmp" => new BmpBitmapEncoder(),
        _ => throw new ArgumentOutOfRangeException(nameof(extension), extension, null)
    };
}