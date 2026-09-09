using System.Drawing;
using System.IO;
using System.Text.Json;
using Moq;
using Pointframe.Engine;
using Xunit;

namespace Pointframe.Tests.Engine;

public sealed class DirectCaptureServiceWindowTests : IDisposable
{
    private readonly string _screenshotsDirectory = Path.Combine(Path.GetTempPath(), $"Pointframe.Tests.{Guid.NewGuid():N}");

    [Fact]
    public void ListWindows_ReturnsWindowDescriptorsJson()
    {
        var window = new WindowDescriptor(
            12345,
            "Test Window",
            "testhost",
            100,
            new PixelBounds(10, 20, 800, 600),
            @"\\.\DISPLAY1",
            false);
        var windowDiscoveryService = new Mock<IWindowDiscoveryService>();
        windowDiscoveryService.Setup(s => s.GetWindows()).Returns([window]);
        var sut = CreateService(windowDiscoveryService: windowDiscoveryService.Object);

        var json = sut.ListWindows();
        var response = JsonSerializer.Deserialize<DirectCaptureResponse>(json);

        Assert.NotNull(response);
        Assert.True(response.Success);
        Assert.NotNull(response.Windows);
        var returned = Assert.Single(response.Windows);
        Assert.Equal(12345, returned.Hwnd);
        Assert.Equal("Test Window", returned.Title);
        Assert.Equal("testhost", returned.ProcessName);
        Assert.Equal(100, returned.ProcessId);
        Assert.Equal(new PixelBounds(10, 20, 800, 600), returned.BoundsPixels);
        Assert.Equal(@"\\.\DISPLAY1", returned.MonitorName);
        Assert.False(returned.IsMinimized);
    }

    [Fact]
    public async Task CaptureWindowAsync_CapturesWindowBoundsAndReturnsArtifact()
    {
        var window = new WindowDescriptor(
            12345,
            "Test Window",
            "testhost",
            100,
            new PixelBounds(100, 200, 30, 40),
            @"\\.\DISPLAY1",
            false);
        var display = new DisplayDescriptor(
            @"\\.\DISPLAY1",
            1d,
            1d,
            new PixelBounds(0, 0, 1920, 1080));
        var sut = CreateService(window: window, display: display);

        var json = await sut.CaptureWindowAsync(12345);
        var response = JsonSerializer.Deserialize<DirectCaptureResponse>(json);

        Assert.NotNull(response);
        Assert.True(response.Success);
        var artifact = Assert.IsType<ArtifactDescriptor>(response.Artifact);
        Assert.Equal("image/png", artifact.Metadata.Kind);
        Assert.True(File.Exists(artifact.Metadata.Path));
        Assert.Equal(new PixelBounds(100, 200, 30, 40), artifact.Metadata.CaptureBoundsPixels);
        Assert.Equal(display.BoundsPixels, artifact.Metadata.MonitorBoundsPixels);
        Assert.Equal("direct-window-capture", artifact.Metadata.Source);
    }

    [Fact]
    public async Task CaptureWindowTextAsync_CapturesWindowAndReturnsRecognizedText()
    {
        var window = new WindowDescriptor(
            12345,
            "Test Window",
            "testhost",
            100,
            new PixelBounds(100, 200, 30, 40),
            @"\\.\DISPLAY1",
            false);
        var display = new DisplayDescriptor(
            @"\\.\DISPLAY1",
            1d,
            1d,
            new PixelBounds(0, 0, 1920, 1080));
        var ocrEngineService = new Mock<IOcrEngineService>();
        ocrEngineService
            .Setup(s => s.RecognizeAsync(It.IsAny<Bitmap>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("Hello from window");
        var sut = CreateService(window: window, display: display, ocrEngineService: ocrEngineService.Object);

        var json = await sut.CaptureWindowTextAsync(12345);
        var response = JsonSerializer.Deserialize<DirectCaptureResponse>(json);

        Assert.NotNull(response);
        Assert.True(response.Success);
        Assert.Equal("Hello from window", response.RecognizedText);
        Assert.NotNull(response.Artifact);
        ocrEngineService.Verify(s => s.RecognizeAsync(It.IsAny<Bitmap>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CaptureWindowAsync_MinimizedWindow_Throws()
    {
        var window = new WindowDescriptor(
            12345,
            "Minimized Window",
            "testhost",
            100,
            new PixelBounds(100, 200, 800, 600),
            @"\\.\DISPLAY1",
            true);
        var display = new DisplayDescriptor(
            @"\\.\DISPLAY1",
            1d,
            1d,
            new PixelBounds(0, 0, 1920, 1080));
        var sut = CreateService(window: window, display: display);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.CaptureWindowAsync(12345));
        Assert.Contains("minimized", ex.Message);
    }

    [Fact]
    public async Task CaptureWindowAsync_InvalidHandle_Throws()
    {
        var sut = CreateService();

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => sut.CaptureWindowAsync(999_999_999));
        Assert.Contains("999999999", ex.Message);
    }

    [Fact]
    public async Task CaptureWindowAsync_WindowSpanningMultipleMonitors_Throws()
    {
        // Window at (1800, 100, 400, 600) — straddles the boundary of a 1920-wide monitor.
        var window = new WindowDescriptor(
            12345,
            "Spanning Window",
            "testhost",
            100,
            new PixelBounds(1800, 100, 400, 600),
            @"\\.\DISPLAY1",
            false);
        var display = new DisplayDescriptor(
            @"\\.\DISPLAY1",
            1d,
            1d,
            new PixelBounds(0, 0, 1920, 1080));
        var sut = CreateService(window: window, display: display);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.CaptureWindowAsync(12345));
        Assert.Contains("spans multiple monitors", ex.Message);
    }

    [Fact]
    public async Task CaptureWindowAsync_WindowFullyOffScreen_Throws()
    {
        // Window is far outside any monitor bounds.
        var window = new WindowDescriptor(
            12345,
            "Off-screen Window",
            "testhost",
            100,
            new PixelBounds(-5000, -5000, 400, 300),
            null,
            false);
        var display = new DisplayDescriptor(
            @"\\.\DISPLAY1",
            1d,
            1d,
            new PixelBounds(0, 0, 1920, 1080));
        var sut = CreateService(window: window, display: display);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.CaptureWindowAsync(12345));
        Assert.Contains("off-screen", ex.Message);
    }

    public void Dispose()
    {
        if (Directory.Exists(_screenshotsDirectory))
        {
            Directory.Delete(_screenshotsDirectory, recursive: true);
        }
    }

    private DirectCaptureService CreateService(
        WindowDescriptor? window = null,
        DisplayDescriptor? display = null,
        IWindowDiscoveryService? windowDiscoveryService = null,
        IOcrEngineService? ocrEngineService = null)
    {
        var displayCaptureEngine = new Mock<IDisplayCaptureEngine>();
        if (display is not null)
        {
            displayCaptureEngine.Setup(e => e.GetDisplays()).Returns([display]);
            displayCaptureEngine
                .Setup(e => e.Capture(It.IsAny<PixelBounds>()))
                .Returns((PixelBounds bounds) => new Bitmap(bounds.Width, bounds.Height));
            displayCaptureEngine
                .Setup(e => e.CaptureMonitor(It.IsAny<string>()))
                .Returns(new CapturedMonitor(display, new Bitmap(display.BoundsPixels.Width, display.BoundsPixels.Height)));
        }
        else
        {
            displayCaptureEngine.Setup(e => e.GetDisplays()).Returns([]);
        }

        if (windowDiscoveryService is null)
        {
            var windowDiscoveryMock = new Mock<IWindowDiscoveryService>();
            if (window is not null)
            {
                windowDiscoveryMock.Setup(s => s.GetWindows()).Returns([window]);
                windowDiscoveryMock.Setup(s => s.GetWindow(window.Hwnd)).Returns(window);
            }
            else
            {
                windowDiscoveryMock.Setup(s => s.GetWindows()).Returns([]);
            }

            windowDiscoveryService = windowDiscoveryMock.Object;
        }

        return new DirectCaptureService(
            displayCaptureEngine.Object,
            windowDiscoveryService,
            ocrEngineService ?? new Mock<IOcrEngineService>().Object,
            _screenshotsDirectory);
    }
}
