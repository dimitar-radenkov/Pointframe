namespace Pointframe.Automation.Bridge;

internal static class AgentBridgeCommands
{
    internal const string DisplaysList = "displays.list";
    internal const string StateGet = "state.get";
    internal const string CaptureMonitor = "capture.monitor";
    internal const string OverlaySave = "overlay.save";
}

internal sealed record BridgeRequest(
    int SchemaVersion,
    string RequestId,
    string Secret,
    string Command,
    string? MonitorName = null);

internal sealed record BridgeResponse(
    int SchemaVersion,
    string RequestId,
    bool Success,
    BridgeError? Error = null,
    IReadOnlyList<DisplayDescriptor>? Displays = null,
    AgentBridgeState? State = null,
    ArtifactDescriptor? Artifact = null);

internal sealed record BridgeError(string Code, string Message);

internal sealed record DisplayDescriptor(
    int SchemaVersion,
    string MonitorName,
    double DpiScaleX,
    double DpiScaleY,
    PixelBounds BoundsPixels);

internal sealed record ArtifactDescriptor(
    int SchemaVersion,
    string OperationId,
    ImageArtifactMetadata Metadata);
