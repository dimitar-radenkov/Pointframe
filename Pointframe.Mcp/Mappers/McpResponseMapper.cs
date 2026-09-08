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

    internal static McpRecordingStatusResponse DeserializeRecordingStatusResponse(string json)
    {
        return MapRecordingStatusResponse(Deserialize<DirectRecordingStatus>(json));
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
                        ToMcpBounds(response.Artifact.Metadata.CaptureBoundsPixels))),
            response.RecognizedText);
    }

    private static McpRecordingResponse MapRecordingResponse(DirectRecordingResponse response)
    {
        return new McpRecordingResponse(
            response.SchemaVersion,
            response.Success,
            response.Error is null ? null : new McpCaptureError(response.Error.Code, response.Error.Message),
            MapRecordingSession(response.Session),
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

    private static McpRecordingStatusResponse MapRecordingStatusResponse(DirectRecordingStatus status)
    {
        return new McpRecordingStatusResponse(
            status.SchemaVersion,
            status.IsRecording,
            MapRecordingSession(status.Session),
            status.Elapsed);
    }

    private static McpRecordingSession? MapRecordingSession(DirectRecordingSession? session)
    {
        return session is null
            ? null
            : new McpRecordingSession(
                session.SchemaVersion,
                session.OperationId,
                session.ArtifactPath,
                session.MonitorName,
                session.FramesPerSecond,
                ToMcpBounds(session.CaptureBoundsPixels),
                session.RedactionRegionsCaptureLocalPixels.Select(ToMcpBounds).ToArray(),
                session.StartedUtc);
    }

    private static McpPixelBounds ToMcpBounds(PixelBounds bounds)
    {
        return new McpPixelBounds(bounds.X, bounds.Y, bounds.Width, bounds.Height);
    }
}
