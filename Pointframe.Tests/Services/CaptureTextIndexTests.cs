using System.Windows.Media;
using System.Windows.Media.Imaging;
using Moq;
using Pointframe.Models;
using Pointframe.Services;
using Xunit;

namespace Pointframe.Tests.Services;

public sealed class CaptureTextIndexTests
{
    private static readonly DateTime T1 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime T2 = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task GetText_ReturnsRecognizedText()
    {
        var ocr = new Mock<IOcrService>();
        ocr.Setup(o => o.Recognize(It.IsAny<BitmapSource>(), It.IsAny<CancellationToken>())).ReturnsAsync("hello");
        var sut = new CaptureTextIndex(ImageFiles(), ocr.Object);

        var text = await sut.GetText(Item("a.png", T1));

        Assert.Equal("hello", text);
        ocr.Verify(o => o.Recognize(It.IsAny<BitmapSource>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetText_SameItemTwice_UsesCacheAndCallsOcrOnce()
    {
        var ocr = new Mock<IOcrService>();
        ocr.Setup(o => o.Recognize(It.IsAny<BitmapSource>(), It.IsAny<CancellationToken>())).ReturnsAsync("hello");
        var sut = new CaptureTextIndex(ImageFiles(), ocr.Object);
        var item = Item("a.png", T1);

        var first = await sut.GetText(item);
        var second = await sut.GetText(item);

        Assert.Equal("hello", first);
        Assert.Equal("hello", second);
        ocr.Verify(o => o.Recognize(It.IsAny<BitmapSource>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetText_StaleCapturedAt_Reindexes()
    {
        var ocr = new Mock<IOcrService>();
        ocr.SetupSequence(o => o.Recognize(It.IsAny<BitmapSource>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("v1")
            .ReturnsAsync("v2");
        var sut = new CaptureTextIndex(ImageFiles(), ocr.Object);

        var first = await sut.GetText(Item("a.png", T1));
        var second = await sut.GetText(Item("a.png", T2));

        Assert.Equal("v1", first);
        Assert.Equal("v2", second);
        ocr.Verify(o => o.Recognize(It.IsAny<BitmapSource>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task GetText_NullResult_IsCached()
    {
        var ocr = new Mock<IOcrService>();
        ocr.Setup(o => o.Recognize(It.IsAny<BitmapSource>(), It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
        var sut = new CaptureTextIndex(ImageFiles(), ocr.Object);
        var item = Item("a.png", T1);

        var first = await sut.GetText(item);
        var second = await sut.GetText(item);

        Assert.Null(first);
        Assert.Null(second);
        ocr.Verify(o => o.Recognize(It.IsAny<BitmapSource>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetText_PassesCancellationTokenToOcr()
    {
        var ocr = new Mock<IOcrService>();
        var sut = new CaptureTextIndex(ImageFiles(), ocr.Object);
        using var cts = new CancellationTokenSource();

        ocr.Setup(o => o.Recognize(
                It.IsAny<BitmapSource>(),
                It.Is<CancellationToken>(token => token == cts.Token)))
            .ReturnsAsync("hello");

        var text = await sut.GetText(Item("a.png", T1), cts.Token);

        Assert.Equal("hello", text);
    }

    [Fact]
    public async Task GetText_WhenCacheLimitExceeded_EvictsLeastRecentlyUsedItem()
    {
        var ocr = new Mock<IOcrService>();
        ocr.SetupSequence(o => o.Recognize(It.IsAny<BitmapSource>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("first")
            .ReturnsAsync("second")
            .ReturnsAsync("first-again");

        var sut = new CaptureTextIndex(ImageFiles(), ocr.Object, maxCacheEntries: 1);

        await sut.GetText(Item("a.png", T1));
        await sut.GetText(Item("b.png", T1));
        var text = await sut.GetText(Item("a.png", T1));

        Assert.Equal("first-again", text);
        ocr.Verify(o => o.Recognize(It.IsAny<BitmapSource>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    private static CaptureItem Item(string name, DateTime capturedUtc)
        => new(name, name, capturedUtc);

    private static IImageFileService ImageFiles()
    {
        var bitmap = BitmapSource.Create(
            1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 0, 255 }, 4);
        bitmap.Freeze();

        var imageFiles = new Mock<IImageFileService>();
        imageFiles.Setup(f => f.LoadForAnnotation(It.IsAny<string>())).Returns(bitmap);
        return imageFiles.Object;
    }
}
