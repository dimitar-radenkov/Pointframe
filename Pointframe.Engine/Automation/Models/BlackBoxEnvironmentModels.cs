namespace Pointframe.Engine.Automation.Models;

public sealed record BlackBoxDesktopEnvironmentOptions(
    string PointframeExecutablePath,
    string McpExecutablePath,
    string ArtifactRoot,
    bool DisposableEnvironmentAcknowledged);

public sealed record BlackBoxDisplaySnapshot(
    string MonitorName,
    double DpiScaleX,
    double DpiScaleY,
    PixelBounds BoundsPixels,
    PixelBounds WorkAreaBoundsPixels);

public sealed record BlackBoxDesktopEnvironmentSnapshot(
    string UserName,
    int SessionId,
    bool IsInteractive,
    bool IsUnlocked,
    IReadOnlyList<BlackBoxDisplaySnapshot> Displays,
    string PointframeExecutablePath,
    string PointframeExecutableSha256,
    string McpExecutablePath,
    string McpExecutableSha256,
    DateTimeOffset CapturedUtc);

public sealed record BlackBoxEnvironmentValidationResult(
    bool IsValid,
    IReadOnlyList<string> Errors,
    BlackBoxDesktopEnvironmentSnapshot? Snapshot = null);

public sealed record BlackBoxAppProfile(
    string Id,
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    bool AllowAttach,
    string? ApprovedAttachExecutablePath,
    IReadOnlySet<DesktopTestingAction> AllowedActions,
    IReadOnlyDictionary<string, IReadOnlyList<string>> AllowedGlobalHotkeys,
    IReadOnlySet<DesktopSurfaceKind> AllowedShellSurfaces,
    bool AllowMonitorObservation);
