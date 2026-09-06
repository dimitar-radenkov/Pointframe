using System.IO;
using System.Text.Json;
using Pointframe.Cli;
using Pointframe.Engine;
using Xunit;

namespace Pointframe.Tests.Cli;

public sealed class CliApplicationTests
{
    [Fact]
    public async Task RunAsync_Displays_WritesDisplayDescriptorsJson()
    {
        var directCaptureService = new FakeDirectCaptureService();
        var standardOutput = new StringWriter();
        var standardError = new StringWriter();
        var application = new CliApplication(directCaptureService, standardOutput, standardError);

        var exitCode = await application.RunAsync(["displays"]);

        Assert.Equal(0, exitCode);
        Assert.Equal(1, directCaptureService.ListDisplaysCallCount);
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
    public async Task RunAsync_Capture_WritesDirectArtifactJson()
    {
        var directCaptureService = new FakeDirectCaptureService();
        var standardOutput = new StringWriter();
        var standardError = new StringWriter();
        var application = new CliApplication(directCaptureService, standardOutput, standardError);

        var exitCode = await application.RunAsync(["capture", "--monitor", @"\\.\DISPLAY1"]);

        Assert.Equal(0, exitCode);
        Assert.Equal(@"\\.\DISPLAY1", directCaptureService.CapturedMonitorName);
        Assert.Contains("artifact-1", standardOutput.ToString(), StringComparison.Ordinal);
        Assert.Empty(standardError.ToString());
    }

    private sealed class FakeDirectCaptureService : IDirectCaptureService
    {
        public string? CapturedMonitorName { get; private set; }

        public int ListDisplaysCallCount { get; private set; }

        public string ListDisplays()
        {
            ListDisplaysCallCount++;
            return "[{\"monitorName\":\"\\\\\\\\.\\\\DISPLAY1\"}]";
        }

        public Task<string> CaptureMonitorAsync(string monitorName, CancellationToken cancellationToken = default)
        {
            CapturedMonitorName = monitorName;
            return Task.FromResult("{\"artifact\":{\"metadata\":{\"artifactId\":\"artifact-1\"}}}");
        }
    }
}