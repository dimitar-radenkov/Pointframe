using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Pointframe.Mcp;

[McpServerToolType]
internal sealed class PointframeMcpTools(IDirectCaptureService directCaptureService, IDirectRecordingMcpService directRecordingMcpService)
{
    [McpServerTool, Description("Lists the displays available for a Pointframe monitor capture.")]
    public Task<string> ListDisplaysAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(directCaptureService.ListDisplays());
    }

    [McpServerTool, Description("Starts a Pointframe whole-monitor capture for the named display.")]
    public Task<string> CaptureMonitorAsync(
        [Description("The exact Windows display device name, such as \\.\\DISPLAY1.")] string monitorName,
        CancellationToken cancellationToken)
    {
        return directCaptureService.CaptureMonitorAsync(monitorName, cancellationToken);
    }

    [McpServerTool, Description("Starts direct, no-microphone MP4 recording for a monitor without launching the Pointframe desktop application. Redaction regions are required and use capture-local physical pixels; provide an empty array when none are needed.")]
    public Task<string> StartRecordingAsync(
        [Description("The exact Windows display device name, such as \\.\\DISPLAY1.")] string monitorName,
        [Description("Declared pixelation rectangles in capture-local physical pixels. Supply an empty array for no redaction.")] IReadOnlyList<PixelBounds> redactionRegionsCaptureLocalPixels,
        [Description("Frames per second, from 1 through 60. Defaults to 20.")] int framesPerSecond = 20,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(directRecordingMcpService.StartRecording(monitorName, redactionRegionsCaptureLocalPixels, framesPerSecond));
    }

    [McpServerTool, Description("Stops the active direct recording and returns its finalized MP4 artifact and sidecar metadata.")]
    public Task<string> StopRecordingAsync(CancellationToken cancellationToken)
    {
        return directRecordingMcpService.StopRecordingAsync(cancellationToken);
    }
}