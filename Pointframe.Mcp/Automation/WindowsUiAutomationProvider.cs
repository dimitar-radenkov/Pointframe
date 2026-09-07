using Pointframe.Engine.Automation.Models;
using Pointframe.Engine.Automation.Services;

namespace Pointframe.Mcp.Automation;

public sealed record WindowsUiAutomationInspection(
    DesktopUiAutomationStatus Status,
    IReadOnlyList<DesktopUiElementSnapshot> Elements,
    DateTimeOffset CapturedUtc,
    bool IsTruncated = false,
    string? ErrorCode = null);

public interface IWindowsUiAutomationBackend
{
    WindowsUiAutomationInspection Inspect(DesktopObservationRequest request);

    DesktopUiElementSnapshot? ResolveLocator(DesktopLocator locator);

    DesktopUiElementSnapshot? ResolveElement(string elementRef);
}

public interface IWindowsUiAutomationCandidateBackend
{
    IReadOnlyList<DesktopUiElementSnapshot> ResolveLocatorCandidates(DesktopLocator locator);
}

public interface IWindowsUiAutomationActionBackend
{
    bool TryInvoke(string elementRef);

    bool TrySetValue(string elementRef, string value);
}

public interface IWindowsUiAutomationActionProvider
{
    bool TryInvoke(string elementRef);

    bool TrySetValue(string elementRef, string value);
}

public sealed class WindowsUiAutomationProvider :
    IDesktopUiObservationProvider,
    IWindowsUiAutomationActionProvider
{
    private readonly IWindowsUiAutomationBackend? _backend;

    public WindowsUiAutomationProvider(IWindowsUiAutomationBackend? backend = null)
    {
        _backend = backend;
    }

    public WindowsUiAutomationInspection Inspect(DesktopObservationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_backend is null)
        {
            return Unavailable();
        }

        var result = _backend.Inspect(request);
        return result with
        {
            Elements = result.Elements.Take(DesktopTestingLimits.MaxUiAutomationElements).ToArray(),
            IsTruncated = result.IsTruncated || result.Elements.Count > DesktopTestingLimits.MaxUiAutomationElements,
        };
    }

    public DesktopUiElementSnapshot? ResolveLocator(DesktopLocator locator)
    {
        ArgumentNullException.ThrowIfNull(locator);
        locator.Validate();
        if (_backend is IWindowsUiAutomationCandidateBackend candidates)
        {
            var matches = candidates.ResolveLocatorCandidates(locator);
            if (matches.Count > 1)
            {
                throw new DesktopOperationException("AmbiguousTarget", "The locator matched more than one element.");
            }

            return matches.SingleOrDefault();
        }

        return _backend?.ResolveLocator(locator);
    }

    public DesktopUiElementSnapshot? ResolveElement(string elementRef)
    {
        if (string.IsNullOrWhiteSpace(elementRef))
        {
            throw new ArgumentException("An element reference is required.", nameof(elementRef));
        }

        return _backend?.ResolveElement(elementRef);
    }

    public bool TryInvoke(string elementRef)
    {
        return _backend is IWindowsUiAutomationActionBackend actionBackend
            && actionBackend.TryInvoke(elementRef);
    }

    public bool TrySetValue(string elementRef, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return _backend is IWindowsUiAutomationActionBackend actionBackend
            && actionBackend.TrySetValue(elementRef, value);
    }

    DesktopUiSnapshot IDesktopUiObservationProvider.Inspect(DesktopObservationRequest request)
    {
        var inspection = Inspect(request);
        return new DesktopUiSnapshot(
            inspection.Status,
            inspection.Elements,
            inspection.CapturedUtc,
            inspection.IsTruncated,
            inspection.ErrorCode);
    }

    private static WindowsUiAutomationInspection Unavailable()
    {
        return new WindowsUiAutomationInspection(
            DesktopUiAutomationStatus.Unavailable,
            [],
            DateTimeOffset.UtcNow,
            ErrorCode: "ProviderUnavailable");
    }
}
