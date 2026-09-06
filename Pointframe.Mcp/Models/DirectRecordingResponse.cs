using Pointframe.Engine;

namespace Pointframe.Mcp;

public sealed record DirectRecordingResponse(
    int SchemaVersion,
    bool Success,
    DirectCaptureError? Error = null,
    DirectRecordingSession? Session = null,
    DirectRecordingArtifact? Artifact = null);
