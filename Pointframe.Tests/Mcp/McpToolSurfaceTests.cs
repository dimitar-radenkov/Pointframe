using System.ComponentModel;
using System.Reflection;
using ModelContextProtocol.Server;
using Moq;
using Pointframe.Engine;
using Pointframe.Engine.Automation.Models;
using Pointframe.Engine.Automation.Services;
using Pointframe.Mcp;
using Pointframe.Mcp.Automation;
using Pointframe.Mcp.Configuration;
using Xunit;

namespace Pointframe.Tests.Mcp;

/// <summary>
/// Covers the MCP tool surface as an MCP client sees it: the descriptions a model selects tools from,
/// and the two behaviors that used to make tools awkward or ambiguous to call — a required redaction
/// array on start_recording, and observe_app reporting an unknown session as a bare null.
/// </summary>
public sealed class McpToolSurfaceTests
{
    [Theory]
    [InlineData(typeof(PointframeMcpTools))]
    [InlineData(typeof(DesktopTestingMcpTools))]
    public void EveryToolMethodHasADescription(Type toolType)
    {
        // MCP clients choose tools from their descriptions; an undescribed tool is effectively invisible.
        var undescribed = ToolMethods(toolType)
            .Where(method => string.IsNullOrWhiteSpace(method.GetCustomAttribute<DescriptionAttribute>()?.Description))
            .Select(method => method.Name)
            .ToArray();

        Assert.True(
            undescribed.Length == 0,
            $"{toolType.Name} has tool methods without a [Description]: {string.Join(", ", undescribed)}");
    }

    [Theory]
    [InlineData(typeof(PointframeMcpTools))]
    [InlineData(typeof(DesktopTestingMcpTools))]
    public void EveryToolParameterHasADescription(Type toolType)
    {
        var undescribed = ToolMethods(toolType)
            .SelectMany(method => method.GetParameters().Select(parameter => (method, parameter)))
            // CancellationToken is supplied by the server, never by the caller, so it carries no schema.
            .Where(pair => pair.parameter.ParameterType != typeof(CancellationToken))
            .Where(pair => string.IsNullOrWhiteSpace(pair.parameter.GetCustomAttribute<DescriptionAttribute>()?.Description))
            .Select(pair => $"{pair.method.Name}.{pair.parameter.Name}")
            .ToArray();

        Assert.True(
            undescribed.Length == 0,
            $"{toolType.Name} has tool parameters without a [Description]: {string.Join(", ", undescribed)}");
    }

    [Fact]
    public async Task StartRecordingAsync_WithoutRedactionRegions_StartsWithNoRedaction()
    {
        // The redaction array used to be a required parameter, forcing every caller to pass an empty
        // array for the common "record everything" case.
        var recordingService = new Mock<IDirectRecordingMcpService>();
        recordingService
            .Setup(service => service.StartRecording(It.IsAny<string>(), It.IsAny<IReadOnlyList<PixelBounds>>(), It.IsAny<int>()))
            .Returns("{\"SchemaVersion\":1,\"Success\":true}");
        var tools = new PointframeMcpTools(new Mock<IDirectCaptureService>().Object, recordingService.Object);

        var response = await tools.StartRecordingAsync(@"\\.\DISPLAY1");

        Assert.True(response.Success);
        recordingService.Verify(
            service => service.StartRecording(@"\\.\DISPLAY1", It.Is<IReadOnlyList<PixelBounds>>(regions => regions.Count == 0), 20),
            Times.Once);
    }

    [Fact]
    public async Task ObserveAppAsync_WhenSessionIsUnknown_ReturnsTypedErrorRatherThanNull()
    {
        // Previously this returned null, leaving a caller unable to distinguish a missing session from
        // an observation that simply found nothing. Every sibling tool reports a typed SessionNotFound.
        var sessions = new Mock<IDesktopTestSessionService>();
        sessions
            .Setup(service => service.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DesktopTestSessionSnapshot?)null);
        var tools = CreateDesktopTestingTools(sessions);

        var response = await tools.ObserveAppAsync("session-does-not-exist", []);

        Assert.NotNull(response);
        Assert.Equal("SessionNotFound", response.Error?.Code);
        Assert.Equal(nameof(DesktopTargetState.Unavailable), response.TargetState);
        Assert.Empty(response.Images);
        Assert.Empty(response.Elements);
    }

    private static DesktopTestingMcpTools CreateDesktopTestingTools(Mock<IDesktopTestSessionService> sessions)
    {
        return new DesktopTestingMcpTools(
            new Mock<IDesktopActionCoordinator>().Object,
            sessions.Object,
            new Mock<IDesktopObservationService>().Object,
            new Mock<IWindowsDesktopInputService>().Object,
            new Mock<IWindowsUiAutomationActionProvider>().Object,
            new Mock<IDesktopUiCheckService>().Object,
            new Mock<IDesktopOcrObservationProvider>().Object,
            new Mock<IDesktopTestReportService>().Object,
            new DesktopTestingPolicyLoader(),
            new DesktopTestingHostOptions(Enabled: true, WorkerMode: false, PolicyPath: null, WorkerPipeName: null, ParentProcessId: null));
    }

    private static IEnumerable<MethodInfo> ToolMethods(Type toolType)
    {
        return toolType
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null);
    }
}
