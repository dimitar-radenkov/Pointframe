using System.IO;
using Microsoft.Extensions.DependencyInjection;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Moq;
using Pointframe.Data.Abstractions;
using Pointframe.Data.Entities;
using Pointframe.Models;
using Pointframe.Services;
using Xunit;

namespace Pointframe.Tests.Services;

public sealed class CaptureTextLookupServiceTests
{
    private static readonly DateTime T1 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task GetText_WhenRepositoryHit_ReturnsCachedText_WithoutOcr()
    {
        var repository = new Mock<ICaptureTextCacheRepository>();
        var unitOfWork = UnitOfWork(repository.Object);
        repository
            .Setup(r => r.GetByFilePath("a.png", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CaptureTextCacheEntry
            {
                FilePath = "a.png",
                CapturedAt = T1,
                Text = "cached",
                LastAccessedAt = DateTime.UtcNow,
            });

        var ocr = new Mock<IOcrService>();
        var sut = new CaptureTextLookupService(ImageFiles(), ocr.Object, ScopeFactory(unitOfWork.Object));

        var text = await sut.GetText(Item("a.png", T1));

        Assert.Equal("cached", text);
        ocr.Verify(o => o.Recognize(It.IsAny<BitmapSource>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetText_WhenRepositoryMiss_RunsOcr_AndPersistsResult()
    {
        var repository = new Mock<ICaptureTextCacheRepository>();
        var unitOfWork = UnitOfWork(repository.Object);
        repository
            .Setup(r => r.GetByFilePath(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CaptureTextCacheEntry?)null);

        var ocr = new Mock<IOcrService>();
        ocr.Setup(o => o.Recognize(It.IsAny<BitmapSource>(), It.IsAny<CancellationToken>())).ReturnsAsync("hello");
        var sut = new CaptureTextLookupService(ImageFiles(), ocr.Object, ScopeFactory(unitOfWork.Object));
        var item = Item("a.png", T1);

        var text = await sut.GetText(item);

        Assert.Equal("hello", text);
        ocr.Verify(o => o.Recognize(It.IsAny<BitmapSource>(), It.IsAny<CancellationToken>()), Times.Once);
        repository.Verify(
            r => r.Add(It.Is<CaptureTextCacheEntry>(entry =>
                entry.FilePath == "a.png"
                && entry.CapturedAt == T1
                && entry.Text == "hello"), It.IsAny<CancellationToken>()),
            Times.Once);
        repository.Verify(r => r.TrimToLimit(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
        unitOfWork.Verify(u => u.SaveChanges(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetText_PassesCancellationTokenToRepositoryAndOcr()
    {
        using var cts = new CancellationTokenSource();

        var repository = new Mock<ICaptureTextCacheRepository>();
        var unitOfWork = UnitOfWork(repository.Object);
        repository
            .Setup(r => r.GetByFilePath(
                It.IsAny<string>(),
                It.Is<CancellationToken>(token => token == cts.Token)))
            .ReturnsAsync((CaptureTextCacheEntry?)null);

        var ocr = new Mock<IOcrService>();
        ocr.Setup(o => o.Recognize(
                It.IsAny<BitmapSource>(),
                It.Is<CancellationToken>(token => token == cts.Token)))
            .ReturnsAsync("hello");

        var sut = new CaptureTextLookupService(ImageFiles(), ocr.Object, ScopeFactory(unitOfWork.Object));

        var text = await sut.GetText(Item("a.png", T1), cts.Token);

        Assert.Equal("hello", text);
        repository.Verify(r => r.Add(
            It.IsAny<CaptureTextCacheEntry>(),
            It.Is<CancellationToken>(token => token == cts.Token)), Times.Once);
        unitOfWork.Verify(
            r => r.SaveChanges(It.Is<CancellationToken>(token => token == cts.Token)),
            Times.Once);
    }

    [Fact]
    public async Task GetText_WhenRepositoryUnavailable_FallsBackToOcr()
    {
        var repository = new Mock<ICaptureTextCacheRepository>();
        var unitOfWork = UnitOfWork(repository.Object);
        repository
            .Setup(r => r.GetByFilePath(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("db unavailable"));

        var ocr = new Mock<IOcrService>();
        ocr.Setup(o => o.Recognize(It.IsAny<BitmapSource>(), It.IsAny<CancellationToken>())).ReturnsAsync("fallback");
        var sut = new CaptureTextLookupService(ImageFiles(), ocr.Object, ScopeFactory(unitOfWork.Object));
        var item = Item("a.png", T1);

        var text = await sut.GetText(item);

        Assert.Equal("fallback", text);
        ocr.Verify(o => o.Recognize(It.IsAny<BitmapSource>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetText_WhenStoreUpsertFails_StillReturnsOcrText()
    {
        var repository = new Mock<ICaptureTextCacheRepository>();
        var unitOfWork = UnitOfWork(repository.Object);
        repository
            .Setup(r => r.GetByFilePath(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CaptureTextCacheEntry?)null);
        repository
            .Setup(r => r.Add(It.IsAny<CaptureTextCacheEntry>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("write failed"));

        var ocr = new Mock<IOcrService>();
        ocr.Setup(o => o.Recognize(It.IsAny<BitmapSource>(), It.IsAny<CancellationToken>())).ReturnsAsync("hello");

        var sut = new CaptureTextLookupService(ImageFiles(), ocr.Object, ScopeFactory(unitOfWork.Object));

        var text = await sut.GetText(Item("a.png", T1));

        Assert.Equal("hello", text);
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

    private static Mock<IPointframeDataUnitOfWork> UnitOfWork(ICaptureTextCacheRepository repository)
    {
        var unitOfWork = new Mock<IPointframeDataUnitOfWork>();
        unitOfWork.SetupGet(u => u.CaptureTextCache).Returns(repository);
        unitOfWork.Setup(u => u.SaveChanges(It.IsAny<CancellationToken>())).ReturnsAsync(1);
        return unitOfWork;
    }

    private static IServiceScopeFactory ScopeFactory(IPointframeDataUnitOfWork unitOfWork)
    {
        var provider = new Mock<IServiceProvider>();
        provider
            .Setup(p => p.GetService(typeof(IPointframeDataUnitOfWork)))
            .Returns(unitOfWork);

        var scope = new Mock<IServiceScope>();
        scope.SetupGet(s => s.ServiceProvider).Returns(provider.Object);

        var scopeFactory = new Mock<IServiceScopeFactory>();
        scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);
        return scopeFactory.Object;
    }
}
