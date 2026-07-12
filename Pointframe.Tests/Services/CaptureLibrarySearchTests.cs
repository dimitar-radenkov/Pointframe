using System.IO;
using Moq;
using Pointframe.Models;
using Pointframe.Services;
using Xunit;

namespace Pointframe.Tests.Services;

public sealed class CaptureLibrarySearchTests : IDisposable
{
    private static readonly DateTime Jan = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Mar = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Jun = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(),
        "Pointframe.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void Search_NullQueryAndDates_ReturnsAllCaptures()
    {
        Directory.CreateDirectory(_tempDirectory);
        CreateFile("shot-1.png", Jan);
        CreateFile("shot-2.png", Jun);
        var sut = NewService();

        var results = sut.Search(null, null, null);

        Assert.Equal(sut.GetCaptures().Count, results.Count);
        Assert.Equal(2, results.Count);
    }

    [Fact]
    public void Search_QueryMatchesFileNameCaseInsensitively()
    {
        Directory.CreateDirectory(_tempDirectory);
        CreateFile("shot-1.png", Jan);
        CreateFile("diagram.png", Jun);

        var results = NewService().Search("SHOT", null, null);

        Assert.Equal("shot-1.png", Assert.Single(results).FileName);
    }

    [Fact]
    public void Search_QueryWithNoMatch_ReturnsEmpty()
    {
        Directory.CreateDirectory(_tempDirectory);
        CreateFile("shot-1.png", Jan);

        Assert.Empty(NewService().Search("nope", null, null));
    }

    [Fact]
    public void Search_FromUtc_DropsOlderButIncludesBoundary()
    {
        Directory.CreateDirectory(_tempDirectory);
        CreateFile("older.png", Jan);
        CreateFile("boundary.png", Mar);
        CreateFile("newer.png", Jun);

        var results = NewService().Search(null, Mar, null);

        Assert.Equal(2, results.Count);
        Assert.Contains(results, item => item.FileName == "boundary.png");
        Assert.Contains(results, item => item.FileName == "newer.png");
        Assert.DoesNotContain(results, item => item.FileName == "older.png");
    }

    [Fact]
    public void Search_ToUtc_DropsNewerButIncludesBoundary()
    {
        Directory.CreateDirectory(_tempDirectory);
        CreateFile("older.png", Jan);
        CreateFile("boundary.png", Mar);
        CreateFile("newer.png", Jun);

        var results = NewService().Search(null, null, Mar);

        Assert.Equal(2, results.Count);
        Assert.Contains(results, item => item.FileName == "boundary.png");
        Assert.Contains(results, item => item.FileName == "older.png");
        Assert.DoesNotContain(results, item => item.FileName == "newer.png");
    }

    [Fact]
    public void Search_QueryAndDateRange_AndTogether()
    {
        Directory.CreateDirectory(_tempDirectory);
        CreateFile("shot-old.png", Jan);
        CreateFile("shot-mid.png", Mar);
        CreateFile("diagram-mid.png", Mar);
        CreateFile("shot-new.png", Jun);

        var results = NewService().Search("shot", Mar, Mar);

        Assert.Equal("shot-mid.png", Assert.Single(results).FileName);
    }

    private CaptureLibraryService NewService()
    {
        var settings = new Mock<IUserSettingsService>();
        settings
            .SetupGet(s => s.Current)
            .Returns(new UserSettings { ScreenshotSavePath = _tempDirectory });
        return new CaptureLibraryService(settings.Object, Mock.Of<ICaptureTextIndex>());
    }

    private void CreateFile(string name, DateTime capturedUtc)
    {
        var path = Path.Combine(_tempDirectory, name);
        File.WriteAllText(path, "stub");
        File.SetLastWriteTimeUtc(path, capturedUtc);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }
}
