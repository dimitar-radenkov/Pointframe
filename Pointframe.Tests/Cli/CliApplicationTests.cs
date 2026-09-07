using System.IO;
using System.Text.Json;
using Moq;
using Pointframe.Cli;
using Pointframe.Engine;
using Xunit;

namespace Pointframe.Tests.Cli;

public sealed class CliApplicationTests
{
    [Fact]
    public async Task RunAsync_Displays_WritesDisplayDescriptorsJson()
    {
        var directCaptureService = new Mock<IDirectCaptureService>();
        directCaptureService
            .Setup(service => service.ListDisplays())
            .Returns("[{\"monitorName\":\"\\\\\\\\.\\\\DISPLAY1\"}]");
        var standardOutput = new StringWriter();
        var standardError = new StringWriter();
        var application = new CliApplication(directCaptureService.Object, standardOutput, standardError);

        var exitCode = await application.RunAsync(["displays"]);

        Assert.Equal(0, exitCode);
        directCaptureService.Verify(service => service.ListDisplays(), Times.Once);
        using var output = JsonDocument.Parse(standardOutput.ToString());
        Assert.Equal(@"\\.\DISPLAY1", output.RootElement[0].GetProperty("monitorName").GetString());
        Assert.Empty(standardError.ToString());
    }

    [Fact]
    public void TryParse_Displays_UsesDisplaysListCommand()
    {
        var parsed = CliCommandParser.TryParse(["displays"], out var command, out var error);

        Assert.True(parsed);
        Assert.Null(error);
        Assert.Equal("displays", command.Name);
        Assert.Null(command.MonitorName);
    }

    [Fact]
    public void TryParse_Capture_RequiresExactMonitorArgument()
    {
        var parsed = CliCommandParser.TryParse(["capture", "--monitor", @"\\.\DISPLAY1"], out var command, out var error);

        Assert.True(parsed);
        Assert.Null(error);
        Assert.Equal("capture", command.Name);
        Assert.Equal(@"\\.\DISPLAY1", command.MonitorName);
    }

    [Fact]
    public void TryParse_CaptureWithoutMonitor_ReturnsUsageError()
    {
        var parsed = CliCommandParser.TryParse(["capture"], out _, out var error);

        Assert.False(parsed);
        Assert.Equal("The capture command requires --monitor followed by an exact Windows device name.", error);
    }

    [Fact]
    public void TryParse_Ocr_RequiresExactMonitorArgument()
    {
        var parsed = CliCommandParser.TryParse(["ocr", "--monitor", @"\\.\DISPLAY1"], out var command, out var error);

        Assert.True(parsed);
        Assert.Null(error);
        Assert.Equal("ocr", command.Name);
        Assert.Equal(@"\\.\DISPLAY1", command.MonitorName);
    }

    [Fact]
    public void TryParse_OcrWithoutMonitor_ReturnsUsageError()
    {
        var parsed = CliCommandParser.TryParse(["ocr"], out _, out var error);

        Assert.False(parsed);
        Assert.Equal("The ocr command requires --monitor followed by an exact Windows device name.", error);
    }

    [Fact]
    public async Task RunAsync_Capture_WritesDirectArtifactJson()
    {
        var directCaptureService = new Mock<IDirectCaptureService>();
        directCaptureService
            .Setup(service => service.CaptureMonitorAsync(@"\\.\DISPLAY1", It.IsAny<CancellationToken>()))
            .ReturnsAsync("{\"artifact\":{\"metadata\":{\"artifactId\":\"artifact-1\"}}}");
        var standardOutput = new StringWriter();
        var standardError = new StringWriter();
        var application = new CliApplication(directCaptureService.Object, standardOutput, standardError);

        var exitCode = await application.RunAsync(["capture", "--monitor", @"\\.\DISPLAY1"]);

        Assert.Equal(0, exitCode);
        directCaptureService.Verify(service => service.CaptureMonitorAsync(@"\\.\DISPLAY1", It.IsAny<CancellationToken>()), Times.Once);
        Assert.Contains("artifact-1", standardOutput.ToString(), StringComparison.Ordinal);
        Assert.Empty(standardError.ToString());
    }

    [Fact]
    public async Task RunAsync_Ocr_WritesRecognizedTextJson()
    {
        var directCaptureService = new Mock<IDirectCaptureService>();
        directCaptureService
            .Setup(service => service.CaptureMonitorTextAsync(@"\\.\DISPLAY1", It.IsAny<CancellationToken>()))
            .ReturnsAsync("{\"artifact\":{\"metadata\":{\"artifactId\":\"artifact-1\"}},\"recognizedText\":\"Hello world\"}");
        var standardOutput = new StringWriter();
        var standardError = new StringWriter();
        var application = new CliApplication(directCaptureService.Object, standardOutput, standardError);

        var exitCode = await application.RunAsync(["ocr", "--monitor", @"\\.\DISPLAY1"]);

        Assert.Equal(0, exitCode);
        directCaptureService.Verify(service => service.CaptureMonitorTextAsync(@"\\.\DISPLAY1", It.IsAny<CancellationToken>()), Times.Once);
        Assert.Contains("Hello world", standardOutput.ToString(), StringComparison.Ordinal);
        Assert.Empty(standardError.ToString());
    }
}
