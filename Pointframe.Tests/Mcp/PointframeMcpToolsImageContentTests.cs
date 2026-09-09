using System.Drawing;
using System.IO;
using System.Linq;
using ModelContextProtocol.Protocol;
using Moq;
using Pointframe.Engine;
using Pointframe.Mcp;
using Xunit;

namespace Pointframe.Tests.Mcp;

// The direct capture MCP tools used to return only artifact metadata — a file path the calling model
// usually cannot open. They now return a CallToolResult that keeps the structured metadata and adds
// the captured image as an inline image block unless includeImage is false.
public sealed class PointframeMcpToolsImageContentTests : IDisposable
{
    private readonly string _screenshotsDirectory = Path.Combine(Path.GetTempPath(), $"Pointframe.Tests.{Guid.NewGuid():N}");

    [Fact]
    public async Task CaptureMonitorAsync_ByDefault_ReturnsStructuredContentAndInlineImageBlock()
    {
        var tools = CreateTools();

        var result = await tools.CaptureMonitorAsync(@"\\.\DISPLAY1");

        Assert.False(result.IsError);
        Assert.NotNull(result.StructuredContent);
        Assert.True(result.StructuredContent!.Value.GetProperty("success").GetBoolean());
        Assert.Contains(result.Content, block => block is TextContentBlock);
        var image = Assert.Single(result.Content.OfType<ImageContentBlock>());
        Assert.Equal("image/png", image.MimeType);
        Assert.True(image.DecodedData.Length > 0);
    }

    [Fact]
    public async Task CaptureMonitorAsync_WithIncludeImageFalse_OmitsImageBlockButKeepsStructuredContent()
    {
        var tools = CreateTools();

        var result = await tools.CaptureMonitorAsync(@"\\.\DISPLAY1", includeImage: false);

        Assert.Empty(result.Content.OfType<ImageContentBlock>());
        Assert.NotNull(result.StructuredContent);
        Assert.True(result.StructuredContent!.Value.GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task CaptureWindowAsync_ByDefault_ReturnsInlineImageBlock()
    {
        var tools = CreateTools();

        var result = await tools.CaptureWindowAsync(12345);

        var image = Assert.Single(result.Content.OfType<ImageContentBlock>());
        Assert.Equal("image/png", image.MimeType);
    }

    private PointframeMcpTools CreateTools()
    {
        var display = new DisplayDescriptor(
            @"\\.\DISPLAY1",
            1d,
            1d,
            new PixelBounds(0, 0, 400, 300),
            new PixelBounds(0, 0, 400, 300));
        var window = new WindowDescriptor(
            12345,
            "Test Window",
            "testhost",
            100,
            new PixelBounds(10, 20, 120, 90),
            @"\\.\DISPLAY1",
            false);

        var captureEngine = new Mock<IDisplayCaptureEngine>();
        captureEngine.Setup(engine => engine.GetDisplays()).Returns([display]);
        captureEngine
            .Setup(engine => engine.Capture(It.IsAny<PixelBounds>()))
            .Returns((PixelBounds bounds) => new Bitmap(bounds.Width, bounds.Height));
        captureEngine
            .Setup(engine => engine.CaptureMonitor(It.IsAny<string>()))
            .Returns(new CapturedMonitor(display, new Bitmap(display.BoundsPixels.Width, display.BoundsPixels.Height)));

        var windows = new Mock<IWindowDiscoveryService>();
        windows.Setup(service => service.GetWindows()).Returns([window]);
        windows.Setup(service => service.GetWindow(window.Hwnd)).Returns(window);

        var captureService = new DirectCaptureService(
            captureEngine.Object,
            windows.Object,
            new Mock<IOcrEngineService>().Object,
            _screenshotsDirectory);

        return new PointframeMcpTools(captureService, new Mock<IDirectRecordingMcpService>().Object);
    }

    public void Dispose()
    {
        if (Directory.Exists(_screenshotsDirectory))
        {
            Directory.Delete(_screenshotsDirectory, recursive: true);
        }
    }
}
