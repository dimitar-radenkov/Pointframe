using System.IO;
using Moq;
using Pointframe.Models;
using Pointframe.Services;
using Xunit;

namespace Pointframe.Tests.Services;

public sealed class CaptureLibraryServiceTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(),
        "Pointframe.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void GetCaptures_WhenFolderMissing_ReturnsEmpty()
    {
        var sut = NewService(Path.Combine(_tempDirectory, "does-not-exist"));

        Assert.Empty(sut.GetCaptures());
    }

    [Fact]
    public void GetCaptures_ReturnsOnlySupportedImageFiles()
    {
        Directory.CreateDirectory(_tempDirectory);
        CreateFile("a.png");
        CreateFile("b.jpg");
        CreateFile("c.jpeg");
        CreateFile("notes.txt");
        CreateFile("clip.mp4");

        var captures = NewService(_tempDirectory).GetCaptures();

        Assert.Equal(3, captures.Count);
        Assert.DoesNotContain(captures, item => item.FileName == "notes.txt");
        Assert.DoesNotContain(captures, item => item.FileName == "clip.mp4");
    }

    [Fact]
    public void GetCaptures_OrdersByCapturedAtDescending()
    {
        Directory.CreateDirectory(_tempDirectory);
        var older = CreateFile("older.png");
        var newer = CreateFile("newer.png");
        File.SetLastWriteTimeUtc(older, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(newer, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));

        var captures = NewService(_tempDirectory).GetCaptures();

        Assert.Equal("newer.png", captures[0].FileName);
        Assert.Equal("older.png", captures[1].FileName);
    }

    [Fact]
    public void GetCaptures_PopulatesPathAndName()
    {
        Directory.CreateDirectory(_tempDirectory);
        var path = CreateFile("shot.png");

        var item = Assert.Single(NewService(_tempDirectory).GetCaptures());

        Assert.Equal(path, item.FilePath);
        Assert.Equal("shot.png", item.FileName);
    }

    private string CreateFile(string name)
    {
        var path = Path.Combine(_tempDirectory, name);
        File.WriteAllText(path, "stub");
        return path;
    }

    private static CaptureLibraryService NewService(string savePath)
    {
        var settings = new Mock<IUserSettingsService>();
        settings
            .SetupGet(s => s.Current)
            .Returns(new UserSettings { ScreenshotSavePath = savePath });
        return new CaptureLibraryService(settings.Object, Mock.Of<ICaptureTextIndex>());
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }
}
