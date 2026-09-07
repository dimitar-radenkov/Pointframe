namespace Pointframe.Engine.Automation.Models;

public enum DesktopLocatorKind
{
    AutomationId,
    RoleAndName,
}

public sealed record DesktopLocator(
    DesktopLocatorKind Kind,
    string? AutomationId = null,
    string? Role = null,
    string? Name = null,
    string? AncestorRef = null,
    string? WindowRef = null)
{
    public void Validate()
    {
        if (Kind == DesktopLocatorKind.AutomationId && string.IsNullOrWhiteSpace(AutomationId))
        {
            throw new ArgumentException("An automation ID is required.", nameof(AutomationId));
        }

        if (Kind == DesktopLocatorKind.RoleAndName &&
            (string.IsNullOrWhiteSpace(Role) || string.IsNullOrWhiteSpace(Name)))
        {
            throw new ArgumentException("A role and name are required for a semantic locator.");
        }
    }
}

public sealed record DesktopUiElementSnapshot(
    string ElementRef,
    string WindowRef,
    string Role,
    string? Name,
    string? AutomationId,
    PixelBounds BoundsPixels,
    bool IsEnabled,
    string? ToggleState = null,
    string? Selection = null,
    string? Text = null,
    bool IsSensitive = false);

public sealed record DesktopUiSnapshot(
    DesktopUiAutomationStatus Status,
    IReadOnlyList<DesktopUiElementSnapshot> Elements,
    DateTimeOffset CapturedUtc,
    bool IsTruncated = false,
    string? ErrorCode = null);

public sealed record DesktopObservationRequest(
    DesktopProcessIdentity Process,
    IReadOnlyList<PixelBounds> CaptureBoundsPixels,
    bool IncludeUiAutomation = true,
    string? WindowRef = null,
    DesktopSurfaceIdentity? Surface = null,
    int? TopologyGeneration = null);

public sealed record DesktopObservationResult(
    DesktopObservation Observation,
    DesktopUiSnapshot? UiAutomation,
    int TopologyGeneration);

public sealed record DesktopCoordinateTransform(
    string ImageRef,
    PixelBounds DesktopBoundsPixels,
    int ImageWidth,
    int ImageHeight,
    int TopologyGeneration,
    DateTimeOffset CapturedUtc);

public sealed record DesktopUiCheckEvaluation(
    bool StateAvailable,
    bool Matches,
    int MatchCount,
    string? ErrorCode = null,
    string? Message = null);

public abstract record DesktopUiCheckCondition
{
    public sealed record Exists(DesktopLocator Locator) : DesktopUiCheckCondition;
    public sealed record Absent(DesktopLocator Locator) : DesktopUiCheckCondition;
    public sealed record Enabled(DesktopLocator Locator, bool Expected = true) : DesktopUiCheckCondition;
    public sealed record ToggleEquals(DesktopLocator Locator, string Expected) : DesktopUiCheckCondition;
    public sealed record SelectionEquals(DesktopLocator Locator, string Expected) : DesktopUiCheckCondition;
    public sealed record TextEquals(DesktopLocator Locator, string Expected) : DesktopUiCheckCondition;
    public sealed record WindowExists(string WindowRef) : DesktopUiCheckCondition;
    public sealed record WindowAbsent(string WindowRef) : DesktopUiCheckCondition;
    public sealed record ProcessExited(string ProcessRef) : DesktopUiCheckCondition;
}
