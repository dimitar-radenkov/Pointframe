using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Patterns;
using FlaUI.UIA3;
using Pointframe.Engine;
using Pointframe.Engine.Automation.Models;

namespace Pointframe.Mcp.Automation;

internal sealed class FlaUiWindowsUiAutomationBackend :
    IWindowsUiAutomationBackend,
    IWindowsUiAutomationCandidateBackend,
    IWindowsUiAutomationActionBackend,
    IDisposable
{
    private readonly UIA3Automation _automation = new();
    private readonly Dictionary<string, AutomationElement> _elements = new(StringComparer.Ordinal);

    public WindowsUiAutomationInspection Inspect(DesktopObservationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _elements.Clear();
        var captured = DateTimeOffset.UtcNow;
        try
        {
            var desktop = _automation.GetDesktop();
            var matches = desktop.FindAll(TreeScope.Descendants, desktop.ConditionFactory.ByProcessId(request.Process.ProcessId));
            var snapshots = matches
                .Take(DesktopTestingLimits.MaxUiAutomationElements)
                .Select((element, index) => AddElement(element, request.Process.ProcessRef, index))
                .ToArray();
            return new WindowsUiAutomationInspection(
                DesktopUiAutomationStatus.Available,
                snapshots,
                captured,
                matches.Length > snapshots.Length);
        }
        catch (Exception exception) when (exception is FlaUI.Core.Exceptions.ElementNotAvailableException or InvalidOperationException)
        {
            return new WindowsUiAutomationInspection(
                DesktopUiAutomationStatus.Unavailable,
                [],
                captured,
                ErrorCode: "ProviderUnavailable");
        }
    }

    public DesktopUiElementSnapshot? ResolveLocator(DesktopLocator locator) =>
        ResolveLocatorCandidates(locator).SingleOrDefault();

    public IReadOnlyList<DesktopUiElementSnapshot> ResolveLocatorCandidates(DesktopLocator locator)
    {
        locator.Validate();
        return _elements
            .Select(pair => AddElement(pair.Value, locator.WindowRef ?? string.Empty, pair.Key))
            .Where(element => locator.Kind == DesktopLocatorKind.AutomationId
                ? string.Equals(element.AutomationId, locator.AutomationId, StringComparison.Ordinal)
                : string.Equals(element.Role, locator.Role, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(element.Name, locator.Name, StringComparison.Ordinal))
            .ToArray();
    }

    public DesktopUiElementSnapshot? ResolveElement(string elementRef) =>
        _elements.TryGetValue(elementRef, out var element)
            ? AddElement(element, string.Empty, elementRef)
            : null;

    public bool TryInvoke(string elementRef)
    {
        return _elements.TryGetValue(elementRef, out var element)
            && element.Patterns.Invoke.Pattern is IInvokePattern invoke
            && Try(invoke.Invoke);
    }

    public bool TrySetValue(string elementRef, string value)
    {
        return _elements.TryGetValue(elementRef, out var element)
            && element.Patterns.Value.Pattern is IValuePattern pattern
            && !pattern.IsReadOnly
            && Try(() => pattern.SetValue(value));
    }

    public void Dispose() => _automation.Dispose();

    private DesktopUiElementSnapshot AddElement(AutomationElement element, string windowRef, object key)
    {
        var elementRef = key.ToString() ?? Guid.NewGuid().ToString("N");
        _elements[elementRef] = element;
        var bounds = element.BoundingRectangle;
        return new DesktopUiElementSnapshot(
            elementRef,
            windowRef,
            element.ControlType.ToString(),
            element.Name,
            element.AutomationId,
            new PixelBounds(bounds.X, bounds.Y, bounds.Width, bounds.Height),
            element.IsEnabled);
    }

    private static bool Try(Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
