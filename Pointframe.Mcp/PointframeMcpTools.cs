using System.ComponentModel;
using System.Text.Json;
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
        return Task.FromResult(ToMcpResponse(Deserialize<DirectCaptureResponse>(directCaptureService.ListDisplays())));
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
        return ToMcpResponse(Deserialize<DirectCaptureResponse>(json));
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
        return Task.FromResult(ToMcpResponse(Deserialize<DirectRecordingResponse>(
            directRecordingMcpService.StartRecording(monitorName, engineRegions, framesPerSecond))));
    }

    [McpServerTool(
        Title = "Stop recording",
        UseStructuredContent = true),
     Description("Stops the active direct recording and returns its finalized MP4 artifact and sidecar metadata.")]
    public async Task<McpRecordingResponse> StopRecordingAsync(CancellationToken cancellationToken)
    {
        var json = await directRecordingMcpService.StopRecordingAsync(cancellationToken).ConfigureAwait(false);
        return ToMcpResponse(Deserialize<DirectRecordingResponse>(json));
    }

    private static T Deserialize<T>(string json)
    {
        return JsonSerializer.Deserialize<T>(json)
            ?? throw new JsonException($"The Pointframe service returned an empty {typeof(T).Name} response.");
    }

    private static McpCaptureResponse ToMcpResponse(DirectCaptureResponse response)
    {
        return new McpCaptureResponse(
            response.SchemaVersion,
            response.Success,
            response.Error is null ? null : new McpCaptureError(response.Error.Code, response.Error.Message),
            response.Displays?.Select(display => new McpDisplayDescriptor(
                display.MonitorName,
                display.DpiScaleX,
                display.DpiScaleY,
                ToMcpBounds(display.BoundsPixels),
                ToMcpBounds(display.WorkAreaBoundsPixels))).ToArray(),
            response.Artifact is null
                ? null
                : new McpArtifactDescriptor(
                    response.Artifact.SchemaVersion,
                    response.Artifact.OperationId,
                    new McpImageArtifactMetadata(
                        response.Artifact.Metadata.SchemaVersion,
                        response.Artifact.Metadata.ArtifactId,
                        response.Artifact.Metadata.Kind,
                        response.Artifact.Metadata.Path,
                        response.Artifact.Metadata.Sha256,
                        response.Artifact.Metadata.ByteLength,
                        response.Artifact.Metadata.CreatedUtc,
                        response.Artifact.Metadata.Source,
                        response.Artifact.Metadata.MonitorName,
                        response.Artifact.Metadata.DpiScaleX,
                        response.Artifact.Metadata.DpiScaleY,
                        ToMcpBounds(response.Artifact.Metadata.CaptureBoundsPixels))));
    }

    private static McpRecordingResponse ToMcpResponse(DirectRecordingResponse response)
    {
        return new McpRecordingResponse(
            response.SchemaVersion,
            response.Success,
            response.Error is null ? null : new McpCaptureError(response.Error.Code, response.Error.Message),
            response.Session is null
                ? null
                : new McpRecordingSession(
                    response.Session.SchemaVersion,
                    response.Session.OperationId,
                    response.Session.ArtifactPath,
                    response.Session.MonitorName,
                    response.Session.FramesPerSecond,
                    ToMcpBounds(response.Session.CaptureBoundsPixels),
                    response.Session.RedactionRegionsCaptureLocalPixels.Select(ToMcpBounds).ToArray(),
                    response.Session.StartedUtc),
            response.Artifact is null
                ? null
                : new McpRecordingArtifact(
                    response.Artifact.SchemaVersion,
                    response.Artifact.ArtifactId,
                    response.Artifact.Kind,
                    response.Artifact.Path,
                    response.Artifact.Sha256,
                    response.Artifact.ByteLength,
                    response.Artifact.CreatedUtc,
                    response.Artifact.ElapsedDuration,
                    response.Artifact.HadMicrophoneAudio,
                    response.Artifact.MonitorName,
                    response.Artifact.DpiScaleX,
                    response.Artifact.DpiScaleY,
                    ToMcpBounds(response.Artifact.CaptureBoundsPixels),
                    ToMcpBounds(response.Artifact.HostBoundsPixels),
                    ToMcpBounds(response.Artifact.WorkAreaBoundsPixels),
                    response.Artifact.EventSidecarPath,
                    response.Artifact.EventCount,
                    response.Artifact.EventTrackSchemaVersion));
    }

    private static McpPixelBounds ToMcpBounds(PixelBounds bounds)
    {
        return new McpPixelBounds(bounds.X, bounds.Y, bounds.Width, bounds.Height);
    }
}

public sealed record McpPixelBounds(int X, int Y, int Width, int Height);

public sealed record McpCaptureResponse(
    int SchemaVersion,
    bool Success,
    McpCaptureError? Error = null,
    IReadOnlyList<McpDisplayDescriptor>? Displays = null,
    McpArtifactDescriptor? Artifact = null);

public sealed record McpCaptureError(string Code, string Message);

public sealed record McpDisplayDescriptor(
    string MonitorName,
    double DpiScaleX,
    double DpiScaleY,
    McpPixelBounds BoundsPixels,
    McpPixelBounds WorkAreaBoundsPixels);

public sealed record McpArtifactDescriptor(
    int SchemaVersion,
    string OperationId,
    McpImageArtifactMetadata Metadata);

public sealed record McpImageArtifactMetadata(
    int SchemaVersion,
    string ArtifactId,
    string Kind,
    string Path,
    string Sha256,
    long ByteLength,
    DateTimeOffset CreatedUtc,
    string Source,
    string MonitorName,
    double DpiScaleX,
    double DpiScaleY,
    McpPixelBounds CaptureBoundsPixels);

public sealed record McpRecordingResponse(
    int SchemaVersion,
    bool Success,
    McpCaptureError? Error = null,
    McpRecordingSession? Session = null,
    McpRecordingArtifact? Artifact = null);

public sealed record McpRecordingSession(
    int SchemaVersion,
    string OperationId,
    string ArtifactPath,
    string MonitorName,
    int FramesPerSecond,
    McpPixelBounds CaptureBoundsPixels,
    IReadOnlyList<McpPixelBounds> RedactionRegionsCaptureLocalPixels,
    DateTimeOffset StartedUtc);

public sealed record McpRecordingArtifact(
    int SchemaVersion,
    string ArtifactId,
    string Kind,
    string Path,
    string Sha256,
    long ByteLength,
    DateTimeOffset CreatedUtc,
    TimeSpan ElapsedDuration,
    bool HadMicrophoneAudio,
    string MonitorName,
    double DpiScaleX,
    double DpiScaleY,
    McpPixelBounds CaptureBoundsPixels,
    McpPixelBounds HostBoundsPixels,
    McpPixelBounds WorkAreaBoundsPixels,
    string EventSidecarPath,
    long EventCount,
    int EventTrackSchemaVersion);