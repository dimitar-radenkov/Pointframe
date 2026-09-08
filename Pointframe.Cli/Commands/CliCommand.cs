using Pointframe.Engine;

namespace Pointframe.Cli;

internal sealed record CliCommand(
    string Name,
    string? MonitorName = null,
    int? RecordSeconds = null,
    int FramesPerSecond = 20,
    IReadOnlyList<PixelBounds>? RedactionRegions = null);
