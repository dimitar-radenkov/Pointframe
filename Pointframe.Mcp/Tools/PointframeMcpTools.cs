using System.ComponentModel;
using ModelContextProtocol.Protocol;
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
        Title = "List windows",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        UseStructuredContent = true),
     Description("Lists visible top-level windows that are reasonable capture candidates. Returns window handles (Hwnd), titles, process names, bounds in physical screen pixels, and the containing monitor name. Window handles are session-local and temporary; always call list_windows to get current handles before capture_window or read_text_from_window.")]
    public Task<McpCaptureResponse> ListWindowsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(McpResponseMapper.DeserializeCaptureResponse(directCaptureService.ListWindows()));
    }

    [McpServerTool(
        Title = "Capture window",
        Destructive = false),
     Description("Captures the visible screen rectangle of a window by its handle (Hwnd from list_windows). This is a screen-rectangle capture: if the window is partially covered by another window, the occluding content will appear in the capture. Minimized, zero-size, off-screen, and multi-monitor-spanning windows are rejected. Returns the saved PNG artifact metadata as structured content plus, unless includeImage is false, the captured image itself (downscaled to at most 1600 px on its longest edge) as an inline image block.")]
    public async Task<CallToolResult> CaptureWindowAsync(
        [Description("The window handle (Hwnd) returned by list_windows. Must be a positive integer.")] long windowId,
        [Description("Whether to also return the captured image inline as an image block, downscaled to at most 1600 px on its longest edge. The full-resolution PNG is always saved to disk regardless. Defaults to true.")] bool includeImage = true,
        CancellationToken cancellationToken = default)
    {
        var json = await directCaptureService.CaptureWindowAsync(windowId, cancellationToken: cancellationToken).ConfigureAwait(false);
        return McpCaptureResultBuilder.Build(McpResponseMapper.DeserializeCaptureResponse(json), includeImage);
    }

    [McpServerTool(
        Title = "Read text from window",
        Destructive = false),
     Description("Captures the visible screen rectangle of a window by its handle (Hwnd from list_windows) and recognizes on-screen text using Windows OCR. Returns the saved PNG artifact and the recognized text as structured content plus, unless includeImage is false, the captured image itself (downscaled to at most 1600 px on its longest edge) as an inline image block. RecognizedText is null when no text was found or no OCR language pack is installed. Same screen-rectangle capture semantics and rejection rules as capture_window.")]
    public async Task<CallToolResult> ReadTextFromWindowAsync(
        [Description("The window handle (Hwnd) returned by list_windows. Must be a positive integer.")] long windowId,
        [Description("Whether to also return the captured image inline as an image block, downscaled to at most 1600 px on its longest edge. The full-resolution PNG is always saved to disk regardless. Defaults to true.")] bool includeImage = true,
        CancellationToken cancellationToken = default)
    {
        var json = await directCaptureService.CaptureWindowTextAsync(windowId, cancellationToken: cancellationToken).ConfigureAwait(false);
        return McpCaptureResultBuilder.Build(McpResponseMapper.DeserializeCaptureResponse(json), includeImage);
    }

    [McpServerTool(
        Title = "Capture monitor",
        Destructive = false),
     Description("Starts a Pointframe monitor capture for the named display, optionally limited to a sub-region. Returns the saved PNG artifact metadata as structured content plus, unless includeImage is false, the captured image itself (downscaled to at most 1600 px on its longest edge) as an inline image block.")]
    public async Task<CallToolResult> CaptureMonitorAsync(
        [Description("The exact Windows display device name, such as \\.\\DISPLAY1.")] string monitorName,
        [Description("Optional sub-region to capture, in monitor-local physical pixels relative to the monitor's own top-left corner. Captures the whole monitor when omitted. Width and height must be positive; a region outside the monitor bounds is rejected.")] McpCaptureRegion? region = null,
        [Description("Whether to also return the captured image inline as an image block, downscaled to at most 1600 px on its longest edge. The full-resolution PNG is always saved to disk regardless. Defaults to true.")] bool includeImage = true,
        CancellationToken cancellationToken = default)
    {
        var json = await directCaptureService.CaptureMonitorAsync(monitorName, ToEngineRegion(region), cancellationToken: cancellationToken).ConfigureAwait(false);
        return McpCaptureResultBuilder.Build(McpResponseMapper.DeserializeCaptureResponse(json), includeImage);
    }

    [McpServerTool(
        Title = "Read text from monitor",
        Destructive = false),
     Description("Captures a Pointframe monitor screenshot, optionally limited to a sub-region, and recognizes on-screen text using Windows OCR. Returns the saved PNG artifact and the recognized text as structured content plus, unless includeImage is false, the captured image itself (downscaled to at most 1600 px on its longest edge) as an inline image block. RecognizedText is null when no text was found or no OCR language pack is installed.")]
    public async Task<CallToolResult> ReadTextFromMonitorAsync(
        [Description("The exact Windows display device name, such as \\.\\DISPLAY1.")] string monitorName,
        [Description("Optional sub-region to capture and run OCR on, in monitor-local physical pixels relative to the monitor's own top-left corner. Captures the whole monitor when omitted. Width and height must be positive; a region outside the monitor bounds is rejected.")] McpCaptureRegion? region = null,
        [Description("Whether to also return the captured image inline as an image block, downscaled to at most 1600 px on its longest edge. The full-resolution PNG is always saved to disk regardless. Defaults to true.")] bool includeImage = true,
        CancellationToken cancellationToken = default)
    {
        var json = await directCaptureService.CaptureMonitorTextAsync(monitorName, ToEngineRegion(region), cancellationToken: cancellationToken).ConfigureAwait(false);
        return McpCaptureResultBuilder.Build(McpResponseMapper.DeserializeCaptureResponse(json), includeImage);
    }

    [McpServerTool(
        Title = "Start recording",
        UseStructuredContent = true),
     Description("Starts direct, no-microphone MP4 recording for a monitor without launching the Pointframe desktop application. Only one recording can be active at a time; call stop_recording to finalize it and obtain the MP4 artifact.")]
    public Task<McpRecordingResponse> StartRecordingAsync(
        [Description("The exact Windows display device name, such as \\.\\DISPLAY1.")] string monitorName,
        [Description("Optional pixelation rectangles in capture-local physical pixels, applied to every frame before it is encoded. Omit for no redaction.")] IReadOnlyList<McpPixelBounds>? redactionRegionsCaptureLocalPixels = null,
        [Description("Frames per second, from 1 through 60. Defaults to 20.")] int framesPerSecond = 20,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var engineRegions = (redactionRegionsCaptureLocalPixels ?? [])
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

    [McpServerTool(
        Title = "Get recording status",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        UseStructuredContent = true),
     Description("Reports whether a direct recording session is currently active. When active, includes the session started by start_recording and the elapsed duration since it started; when not, session and elapsed are omitted.")]
    public Task<McpRecordingStatusResponse> GetRecordingStatusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(McpResponseMapper.DeserializeRecordingStatusResponse(directRecordingMcpService.GetRecordingStatus()));
    }

    private static CaptureRegion? ToEngineRegion(McpCaptureRegion? region)
    {
        return region is null ? null : new CaptureRegion(region.X, region.Y, region.Width, region.Height);
    }
}
