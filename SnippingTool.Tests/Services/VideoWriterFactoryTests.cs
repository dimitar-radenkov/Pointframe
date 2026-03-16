using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SnippingTool.Models;
using SnippingTool.Services;
using Xunit;

namespace SnippingTool.Tests.Services;

public sealed class VideoWriterFactoryTests
{
    private static VideoWriterFactory CreateSut() =>
        new(Mock.Of<IUserSettingsService>(s => s.Current == new UserSettings()), NullLogger<FFMpegVideoWriter>.Instance);

    [Fact]
    public void Create_Avi_ReturnsSharpAviVideoWriter()
    {
        var factory = CreateSut();
        var tempPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"test-{Guid.NewGuid()}.avi");

        try
        {
            using var writer = factory.Create(RecordingFormat.Avi, 100, 100, 10, tempPath);
            Assert.IsType<SharpAviVideoWriter>(writer);
        }
        finally
        {
            System.IO.File.Delete(tempPath);
        }
    }

    [Fact]
    public void Create_Mp4_ThrowsFileNotFoundWhenFfmpegMissing()
    {
        var factory = CreateSut();

        Assert.Throws<System.IO.FileNotFoundException>(() =>
            factory.Create(RecordingFormat.Mp4, 100, 100, 10, "test.mp4"));
    }

    [Fact]
    public void Create_InvalidFormat_Throws()
    {
        var factory = CreateSut();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            factory.Create((RecordingFormat)999, 100, 100, 10, "test.bin"));
    }
}
