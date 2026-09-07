using System.Text.Json;
using Pointframe.Engine;

namespace Pointframe.Mcp;

internal static class McpResponseMapper
{
    internal static McpCaptureResponse DeserializeCaptureResponse(string json)
    {
        return MapCaptureResponse(Deserialize<DirectCaptureResponse>(json));
    }

    internal static McpRecordingResponse DeserializeRecordingResponse(string json)
    {
        return MapRecordingResponse(Deserialize<DirectRecordingResponse>(json));
    }

    private static T Deserialize<T>(string json)
    {
        return JsonSerializer.Deserialize<T>(json)
            ?? throw new JsonException($"The Pointframe service returned an empty {typeof(T).Name} response.");
    }

    private static McpCaptureResponse MapCaptureResponse(DirectCaptureResponse response)
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

    private static McpRecordingResponse MapRecordingResponse(DirectRecordingResponse response)
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
