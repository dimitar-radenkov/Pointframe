using Pointframe.Engine;

namespace Pointframe.Cli;

internal sealed record CliCommand(
    string Name,
    string? MonitorName = null,
    long? WindowId = null,
    int? RecordSeconds = null,
    int FramesPerSecond = 20,
    IReadOnlyList<PixelBounds>? RedactionRegions = null,
    CaptureRegion? Region = null,
    string? OutputPath = null,
    string? McpAction = null,
    string? McpClient = null,
    bool DryRun = false,
    string? SpecPath = null,
    string? McpExecutablePath = null,
    string? ScenarioId = null,
    string? VerifyAction = null,
    string? TaskId = null,
    string? TaskFile = null,
    string? Only = null,
    bool Revoke = false,
    bool Replace = false,
    bool Review = false,
    int? MaxBlocks = null,
    string? UseAgent = null,
    string? AppPath = null,
    string? Hooks = null,
    bool AgentsMd = false,
    bool Explore = false,
    bool Force = false,
    string? ProjectPath = null);
