using System.Drawing;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Moq;
using Pointframe.Engine;
using Xunit;

namespace Pointframe.Tests.Engine;

public sealed class DirectCaptureServiceTests : IDisposable
{
    private readonly string _screenshotsDirectory = Path.Combine(Path.GetTempPath(), $"Pointframe.Tests.{Guid.NewGuid():N}");

    [Fact]
    public async Task CaptureMonitorAsync_WritesPngAndReturnsVerifiedArtifactMetadata()
    {
        var display = new Pointframe.Engine.DisplayDescriptor(
            @"\\.\DISPLAY1",
            1.5,
            1.25,
            new Pointframe.Engine.PixelBounds(-20, 10, 2, 3));
        var sut = new DirectCaptureService(CreateDisplayCaptureEngine(display), new Mock<IOcrEngineService>().Object, _screenshotsDirectory);

        var json = await sut.CaptureMonitorAsync(display.MonitorName);
        var response = JsonSerializer.Deserialize<DirectCaptureResponse>(json);

        Assert.NotNull(response);
        var artifact = Assert.IsType<ArtifactDescriptor>(response.Artifact);
        var metadata = artifact.Metadata;
        Assert.True(response.Success);
        Assert.Equal("image/png", metadata.Kind);
        Assert.True(File.Exists(metadata.Path));
        var metadataSidecarPath = $"{metadata.Path}.metadata.json";
        Assert.True(File.Exists(metadataSidecarPath));
        Assert.Equal(new FileInfo(metadata.Path).Length, metadata.ByteLength);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(metadata.Path))), metadata.Sha256);
        var persistedMetadata = JsonSerializer.Deserialize<ImageArtifactMetadata>(File.ReadAllText(metadataSidecarPath));
        Assert.Equal(metadata, persistedMetadata);
        Assert.Equal(display.MonitorName, metadata.MonitorName);
        Assert.Equal(display.DpiScaleX, metadata.DpiScaleX);
        Assert.Equal(display.DpiScaleY, metadata.DpiScaleY);
        Assert.Equal(new Pointframe.Engine.PixelBounds(-20, 10, 2, 3), metadata.CaptureBoundsPixels);
        Assert.Equal(new Pointframe.Engine.PixelBounds(-20, 10, 2, 3), metadata.MonitorBoundsPixels);
    }

    [Fact]
    public void ListDisplays_ReturnsCurrentCompatibleDisplayMetadata()
    {
        var display = new Pointframe.Engine.DisplayDescriptor(
            @"\\.\DISPLAY1",
            1d,
            1d,
            new Pointframe.Engine.PixelBounds(0, 0, 100, 200));
        var sut = new DirectCaptureService(CreateDisplayCaptureEngine(display), new Mock<IOcrEngineService>().Object, _screenshotsDirectory);

        var response = JsonSerializer.Deserialize<DirectCaptureResponse>(sut.ListDisplays());

        Assert.NotNull(response);
        Assert.True(response.Success);
        var returnedDisplay = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<Pointframe.Engine.DisplayDescriptor>>(response.Displays));
        Assert.Equal(display.MonitorName, returnedDisplay.MonitorName);
        Assert.Equal(new Pointframe.Engine.PixelBounds(0, 0, 100, 200), returnedDisplay.BoundsPixels);
    }

    [Fact]
    public async Task CaptureMonitorTextAsync_WritesArtifactAndReturnsRecognizedText()
    {
        var display = new Pointframe.Engine.DisplayDescriptor(
            @"\\.\DISPLAY1",
            1.5,
            1.25,
            new Pointframe.Engine.PixelBounds(-20, 10, 2, 3));
        var ocrEngineService = new Mock<IOcrEngineService>();
        ocrEngineService
            .Setup(service => service.RecognizeAsync(It.IsAny<Bitmap>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("Hello world");
        var sut = new DirectCaptureService(CreateDisplayCaptureEngine(display), ocrEngineService.Object, _screenshotsDirectory);

        var json = await sut.CaptureMonitorTextAsync(display.MonitorName);
        var response = JsonSerializer.Deserialize<DirectCaptureResponse>(json);

        Assert.NotNull(response);
        Assert.True(response.Success);
        Assert.Equal("Hello world", response.RecognizedText);
        var artifact = Assert.IsType<ArtifactDescriptor>(response.Artifact);
        Assert.True(File.Exists(artifact.Metadata.Path));
        ocrEngineService.Verify(service => service.RecognizeAsync(It.IsAny<Bitmap>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CaptureMonitorTextAsync_WhenOcrFindsNoText_ReturnsNullRecognizedText()
    {
        var display = new Pointframe.Engine.DisplayDescriptor(
            @"\\.\DISPLAY1",
            1d,
            1d,
            new Pointframe.Engine.PixelBounds(0, 0, 100, 200));
        var ocrEngineService = new Mock<IOcrEngineService>();
        ocrEngineService
            .Setup(service => service.RecognizeAsync(It.IsAny<Bitmap>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        var sut = new DirectCaptureService(CreateDisplayCaptureEngine(display), ocrEngineService.Object, _screenshotsDirectory);

        var json = await sut.CaptureMonitorTextAsync(display.MonitorName);
        var response = JsonSerializer.Deserialize<DirectCaptureResponse>(json);

        Assert.NotNull(response);
        Assert.True(response.Success);
        Assert.Null(response.RecognizedText);
    }

    [Fact]
    public async Task CaptureMonitorAsync_DoesNotInvokeOcr()
    {
        var display = new Pointframe.Engine.DisplayDescriptor(
            @"\\.\DISPLAY1",
            1d,
            1d,
            new Pointframe.Engine.PixelBounds(0, 0, 100, 200));
        var ocrEngineService = new Mock<IOcrEngineService>();
        ocrEngineService
            .Setup(service => service.RecognizeAsync(It.IsAny<Bitmap>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("unused");
        var sut = new DirectCaptureService(CreateDisplayCaptureEngine(display), ocrEngineService.Object, _screenshotsDirectory);

        var json = await sut.CaptureMonitorAsync(display.MonitorName);
        var response = JsonSerializer.Deserialize<DirectCaptureResponse>(json);

        Assert.NotNull(response);
        Assert.Null(response.RecognizedText);
        ocrEngineService.Verify(service => service.RecognizeAsync(It.IsAny<Bitmap>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CaptureMonitorAsync_WithRegion_CapturesResolvedSubRegionAndReportsActualBounds()
    {
        var display = new Pointframe.Engine.DisplayDescriptor(
            @"\\.\DISPLAY1",
            1d,
            1d,
            new Pointframe.Engine.PixelBounds(100, 200, 800, 600));
        var sut = new DirectCaptureService(CreateDisplayCaptureEngine(display), new Mock<IOcrEngineService>().Object, _screenshotsDirectory);

        var json = await sut.CaptureMonitorAsync(display.MonitorName, new CaptureRegion(10, 20, 30, 40));
        var response = JsonSerializer.Deserialize<DirectCaptureResponse>(json);

        Assert.NotNull(response);
        Assert.True(response.Success);
        var metadata = Assert.IsType<ArtifactDescriptor>(response.Artifact).Metadata;
        Assert.Equal(new Pointframe.Engine.PixelBounds(110, 220, 30, 40), metadata.CaptureBoundsPixels);
        Assert.Equal(display.BoundsPixels, metadata.MonitorBoundsPixels);
        using var savedBitmap = new Bitmap(metadata.Path);
        Assert.Equal(30, savedBitmap.Width);
        Assert.Equal(40, savedBitmap.Height);
    }

    [Fact]
    public async Task CaptureMonitorAsync_WithRegionOutsideMonitorBounds_Throws()
    {
        var display = new Pointframe.Engine.DisplayDescriptor(
            @"\\.\DISPLAY1",
            1d,
            1d,
            new Pointframe.Engine.PixelBounds(0, 0, 100, 200));
        var sut = new DirectCaptureService(CreateDisplayCaptureEngine(display), new Mock<IOcrEngineService>().Object, _screenshotsDirectory);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => sut.CaptureMonitorAsync(display.MonitorName, new CaptureRegion(90, 0, 50, 50)));
    }

    [Fact]
    public async Task CaptureMonitorAsync_WithRegionForUnknownMonitor_Throws()
    {
        var display = new Pointframe.Engine.DisplayDescriptor(
            @"\\.\DISPLAY1",
            1d,
            1d,
            new Pointframe.Engine.PixelBounds(0, 0, 100, 200));
        var sut = new DirectCaptureService(CreateDisplayCaptureEngine(display), new Mock<IOcrEngineService>().Object, _screenshotsDirectory);

        await Assert.ThrowsAsync<ArgumentException>(
            () => sut.CaptureMonitorAsync(@"\\.\DISPLAY9", new CaptureRegion(0, 0, 10, 10)));
    }

    public void Dispose()
    {
        if (Directory.Exists(_screenshotsDirectory))
        {
            Directory.Delete(_screenshotsDirectory, recursive: true);
        }
    }

    private static IDisplayCaptureEngine CreateDisplayCaptureEngine(Pointframe.Engine.DisplayDescriptor display)
    {
        var displayCaptureEngine = new Mock<IDisplayCaptureEngine>();
        displayCaptureEngine
            .Setup(engine => engine.GetDisplays())
            .Returns([display]);
        displayCaptureEngine
            .Setup(engine => engine.Capture(It.IsAny<Pointframe.Engine.PixelBounds>()))
            .Returns((Pointframe.Engine.PixelBounds bounds) => new Bitmap(bounds.Width, bounds.Height));
        displayCaptureEngine
            .Setup(engine => engine.CaptureMonitor(It.Is<string>(monitorName => string.Equals(monitorName, display.MonitorName, StringComparison.OrdinalIgnoreCase))))
            .Returns(new CapturedMonitor(display, new Bitmap(display.BoundsPixels.Width, display.BoundsPixels.Height)));
        return displayCaptureEngine.Object;
    }
}
