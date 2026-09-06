using System.IO;
using System.Text.Json;
using Pointframe.Cli;
using Xunit;

namespace Pointframe.Tests.Cli;

public sealed class CliApplicationTests
{
    [Fact]
    public async Task RunAsync_Displays_WritesDisplayDescriptorsJson()
    {
        var bridgeClient = new FakeBridgeClient();
        var standardOutput = new StringWriter();
        var standardError = new StringWriter();
        var application = new CliApplication(bridgeClient, standardOutput, standardError);

        var exitCode = await application.RunAsync(["displays"]);

        Assert.Equal(0, exitCode);
        Assert.Equal([("displays.list", (string?)null)], bridgeClient.Calls);
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
        Assert.Equal("displays.list", command.BridgeCommand);
        Assert.Null(command.MonitorName);
    }

    [Fact]
    public void TryParse_Capture_RequiresExactMonitorArgument()
    {
        var parsed = CliCommandParser.TryParse(["capture", "--monitor", @"\\.\DISPLAY1"], out var command, out var error);

        Assert.True(parsed);
        Assert.Null(error);
        Assert.Equal("capture.monitor", command.BridgeCommand);
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
    public async Task RunAsync_Capture_SendsCaptureThenSaveAndWritesArtifactJson()
    {
        var bridgeClient = new FakeBridgeClient();
        var standardOutput = new StringWriter();
        var standardError = new StringWriter();
        var application = new CliApplication(bridgeClient, standardOutput, standardError);

        var exitCode = await application.RunAsync(["capture", "--monitor", @"\\.\DISPLAY1"]);

        Assert.Equal(0, exitCode);
        Assert.Equal(
            [("capture.monitor", @"\\.\DISPLAY1"), ("overlay.save", (string?)null)],
            bridgeClient.Calls);
        Assert.Contains("artifact-1", standardOutput.ToString(), StringComparison.Ordinal);
        Assert.Empty(standardError.ToString());
    }

    private sealed class FakeBridgeClient : IAgentBridgeClient
    {
        public List<(string Command, string? MonitorName)> Calls { get; } = [];

        public Task<BridgeResponse> SendAsync(string command, string? monitorName = null, CancellationToken cancellationToken = default)
        {
            Calls.Add((command, monitorName));
            var response = command switch
            {
                "displays.list" => new BridgeResponse(1, "displays", true, Displays: [new DisplayDescriptor(1, @"\\.\DISPLAY1", 1d, 1d, new PixelBounds(0, 0, 100, 100))]),
                "capture.monitor" => new BridgeResponse(1, "capture", true),
                "overlay.save" => new BridgeResponse(1, "save", true, Artifact: CreateArtifact()),
                _ => throw new InvalidOperationException($"Unexpected command '{command}'."),
            };
            return Task.FromResult(response);
        }

        private static ArtifactDescriptor CreateArtifact()
        {
            return new ArtifactDescriptor(
                1,
                "operation-1",
                new ImageArtifactMetadata(
                    1,
                    "artifact-1",
                    "image/png",
                    "C:\\capture.png",
                    "abc",
                    1,
                    DateTimeOffset.UnixEpoch,
                    "agent",
                    @"\\.\DISPLAY1",
                    1d,
                    1d,
                    new PixelBounds(0, 0, 100, 100)));
        }
    }
}