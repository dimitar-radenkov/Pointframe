using System.ComponentModel;
using ModelContextProtocol.Server;
using Pointframe.Engine;

namespace Pointframe.Mcp;

[McpServerToolType]
internal sealed class PointframeMcpTools(IDirectCaptureService directCaptureService, IDirectRecordingMcpService directRecordingMcpService)
{
    [McpServerTool(
        Title = "List displays",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        UseStructuredContent = true),
     Description("Lists the displays available for a Pointframe monitor capture.")]
    public Task<McpCaptureResponse> ListDisplaysAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(McpResponseMapper.DeserializeCaptureResponse(directCaptureService.ListDisplays()));
    }

    [McpServerTool(
        Title = "Capture monitor",
        Destructive = false,
        UseStructuredContent = true),
     Description("Starts a Pointframe whole-monitor capture for the named display.")]
    public async Task<McpCaptureResponse> CaptureMonitorAsync(
        [Description("The exact Windows display device name, such as \\.\\DISPLAY1.")] string monitorName,
        CancellationToken cancellationToken)
    {
        var json = await directCaptureService.CaptureMonitorAsync(monitorName, cancellationToken).ConfigureAwait(false);
        return McpResponseMapper.DeserializeCaptureResponse(json);
    }

    [McpServerTool(
        Title = "Start recording",
        UseStructuredContent = true),
     Description("Starts direct, no-microphone MP4 recording for a monitor without launching the Pointframe desktop application. Redaction regions are required and use capture-local physical pixels; provide an empty array when none are needed.")]
    public Task<McpRecordingResponse> StartRecordingAsync(
        [Description("The exact Windows display device name, such as \\.\\DISPLAY1.")] string monitorName,
        [Description("Declared pixelation rectangles in capture-local physical pixels. Supply an empty array for no redaction.")] IReadOnlyList<McpPixelBounds> redactionRegionsCaptureLocalPixels,
        [Description("Frames per second, from 1 through 60. Defaults to 20.")] int framesPerSecond = 20,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var engineRegions = redactionRegionsCaptureLocalPixels
            .Select(region => new PixelBounds(region.X, region.Y, region.Width, region.Height))
            .ToArray();
        return Task.FromResult(McpResponseMapper.DeserializeRecordingResponse(
            directRecordingMcpService.StartRecording(monitorName, engineRegions, framesPerSecond)));
    }

    [McpServerTool(
        Title = "Stop recording",
        UseStructuredContent = true),
     Description("Stops the active direct recording and returns its finalized MP4 artifact and sidecar metadata.")]
    public async Task<McpRecordingResponse> StopRecordingAsync(CancellationToken cancellationToken)
    {
        var json = await directRecordingMcpService.StopRecordingAsync(cancellationToken).ConfigureAwait(false);
        return McpResponseMapper.DeserializeRecordingResponse(json);
    }
}
