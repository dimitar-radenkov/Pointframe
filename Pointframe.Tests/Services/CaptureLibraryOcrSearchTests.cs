using System.IO;
using Moq;
using Pointframe.Models;
using Pointframe.Services;
using Xunit;

namespace Pointframe.Tests.Services;

public sealed class CaptureLibraryOcrSearchTests : IDisposable
{
    private static readonly DateTime Jan = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Jun = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(),
        "Pointframe.Tests",
        Guid.NewGuid().ToString("N"));

    private readonly Mock<ICaptureTextLookupService> _textIndex = new();

    [Fact]
    public async Task SearchAsync_MatchesTextInsideImage_EvenWhenFileNameDoesNot()
    {
        Directory.CreateDirectory(_tempDirectory);
        CreateFile("a.png", Jan);
        CreateFile("b.png", Jun);
        TextFor("a.png", "invoice total 42");
        TextFor("b.png", "unrelated content");

        var results = await NewService().SearchAsync("invoice", null, null);

        Assert.Equal("a.png", Assert.Single(results).FileName);
    }

    [Fact]
    public async Task SearchAsync_FileNameMatch_DoesNotRunOcr()
    {
        Directory.CreateDirectory(_tempDirectory);
        CreateFile("invoice.png", Jan);

        var results = await NewService().SearchAsync("invoice", null, null);

        Assert.Single(results);
        _textIndex.Verify(t => t.GetText(It.IsAny<CaptureItem>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SearchAsync_EmptyQuery_ReturnsAllAndNeverRunsOcr()
    {
        Directory.CreateDirectory(_tempDirectory);
        CreateFile("a.png", Jan);
        CreateFile("b.png", Jun);

        var results = await NewService().SearchAsync(null, null, null);

        Assert.Equal(2, results.Count);
        _textIndex.Verify(t => t.GetText(It.IsAny<CaptureItem>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SearchAsync_DateExcludedItems_AreNeverOcrd()
    {
        Directory.CreateDirectory(_tempDirectory);
        CreateFile("old.png", Jan);
        CreateFile("new.png", Jun);
        TextFor("new.png", "invoice");

        var results = await NewService().SearchAsync("invoice", Jun, null);

        Assert.Equal("new.png", Assert.Single(results).FileName);
        _textIndex.Verify(
            t => t.GetText(It.Is<CaptureItem>(c => c.FileName == "old.png"), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SearchAsync_OcrFailureForOneItem_ContinuesAndReturnsOtherMatches()
    {
        Directory.CreateDirectory(_tempDirectory);
        CreateFile("broken.png", Jan);
        CreateFile("ok.png", Jun);

        _textIndex
            .Setup(t => t.GetText(It.Is<CaptureItem>(c => c.FileName == "broken.png"), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("broken"));
        _textIndex
            .Setup(t => t.GetText(It.Is<CaptureItem>(c => c.FileName == "ok.png"), It.IsAny<CancellationToken>()))
            .ReturnsAsync("invoice");

        var results = await NewService().SearchAsync("invoice", null, null);

        Assert.Equal("ok.png", Assert.Single(results).FileName);
    }

    [Fact]
    public async Task SearchAsync_PassesCancellationTokenToTextIndex()
    {
        Directory.CreateDirectory(_tempDirectory);
        CreateFile("a.png", Jan);
        using var cts = new CancellationTokenSource();

        _textIndex
            .Setup(t => t.GetText(
                It.Is<CaptureItem>(c => c.FileName == "a.png"),
                It.Is<CancellationToken>(token => token == cts.Token)))
            .ReturnsAsync("invoice");

        var results = await NewService().SearchAsync("invoice", null, null, cancellationToken: cts.Token);

        Assert.Equal("a.png", Assert.Single(results).FileName);
    }

    [Fact]
    public async Task SearchAsync_NoMatchInNameOrText_ReturnsEmpty()
    {
        Directory.CreateDirectory(_tempDirectory);
        CreateFile("a.png", Jan);
        TextFor("a.png", "something else");

        var results = await NewService().SearchAsync("invoice", null, null);

        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchAsync_TextMatchIsCaseInsensitive()
    {
        Directory.CreateDirectory(_tempDirectory);
        CreateFile("a.png", Jan);
        TextFor("a.png", "Total Due");

        var results = await NewService().SearchAsync("total due", null, null);

        Assert.Single(results);
    }

    [Fact]
    public async Task SearchAsync_LongQuery_ReturnsMatchesSortedNewestFirst()
    {
        Directory.CreateDirectory(_tempDirectory);
        CreateFile("older.png", Jan);
        CreateFile("newer.png", Jun);
        TextFor("older.png", "invoice");
        TextFor("newer.png", "invoice");

        var results = await NewService().SearchAsync("invoice", null, null);

        Assert.Equal(["newer.png", "older.png"], results.Select(result => result.FileName).ToArray());
    }

    [Fact]
    public async Task SearchAsync_ReportsProgressAcrossScannedCandidates()
    {
        Directory.CreateDirectory(_tempDirectory);
        CreateFile("a.png", Jan);
        CreateFile("b.png", Jun);
        TextFor("a.png", "invoice");
        TextFor("b.png", "other");

        var reports = new List<CaptureSearchProgress>();
        var progress = new Progress<CaptureSearchProgress>(reports.Add);

        await NewService().SearchAsync("invoice", null, null, progress);

        // Progress<T> marshals asynchronously; give the callbacks a moment to drain.
        await Task.Delay(200);

        Assert.NotEmpty(reports);
        Assert.All(reports, report => Assert.Equal(2, report.Total));
        Assert.Equal(2, reports[^1].Scanned);
    }

    private CaptureLibraryService NewService()
    {
        var settings = new Mock<IUserSettingsService>();
        settings
            .SetupGet(s => s.Current)
            .Returns(new UserSettings { ScreenshotSavePath = _tempDirectory });
        return new CaptureLibraryService(settings.Object, _textIndex.Object);
    }

    private void TextFor(string fileName, string? text)
        => _textIndex
            .Setup(t => t.GetText(It.Is<CaptureItem>(c => c.FileName == fileName), It.IsAny<CancellationToken>()))
            .ReturnsAsync(text);

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
