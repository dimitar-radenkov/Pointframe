using FlaUI.Core.AutomationElements;
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
    private int _generation;

    public WindowsUiAutomationInspection Inspect(DesktopObservationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Refs are scoped to the inspection that produced them. They used to be bare loop indices
        // cleared and reissued on every inspection, so a ref held across two inspections silently
        // addressed whatever element now sat at that position -- an action aimed at one control
        // landing on another. A generation prefix makes a stale ref fail to resolve instead.
        _elements.Clear();
        var generation = ++_generation;
        var captured = DateTimeOffset.UtcNow;
        try
        {
            var desktop = _automation.GetDesktop();

            // Scope the walk to the target's own top-level windows. Asking the desktop root for all
            // descendants of a process makes UI Automation walk every window on the machine, which
            // takes long enough to hang the caller outright on a busy desktop.
            var roots = desktop.FindAllChildren(condition => condition.ByProcessId(request.Process.ProcessId));
            var (snapshots, truncated) = Walk(roots, request.Process.ProcessRef, generation);
            return new WindowsUiAutomationInspection(
                DesktopUiAutomationStatus.Available,
                snapshots,
                captured,
                truncated);
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

    private (IReadOnlyList<DesktopUiElementSnapshot> Elements, bool Truncated) Walk(
        IReadOnlyList<AutomationElement> roots,
        string processRef,
        int generation)
    {
        // Breadth-first so that when the element cap is reached the elements kept are the shallow,
        // interactive ones a caller actually addresses, rather than the first deep branch walked.
        var budget = System.Diagnostics.Stopwatch.StartNew();
        var snapshots = new List<DesktopUiElementSnapshot>();

        // Each root is one top-level window, and every element inherits the ref of the window it was
        // reached through. Elements previously all carried the *process* ref, which made
        // windowExists/windowAbsent meaningless, left locator window-scoping unable to distinguish
        // two windows of the same app, and gave desktop_focus_window nothing it could resolve.
        var queue = new Queue<(AutomationElement Element, int Depth, string WindowRef)>();
        foreach (var root in roots)
        {
            queue.Enqueue((root, 0, WindowRefFor(root, processRef)));
        }

        var truncated = false;
        while (queue.Count > 0)
        {
            if (snapshots.Count >= DesktopTestingLimits.MaxUiAutomationElements
                || budget.Elapsed > InspectionBudget)
            {
                truncated = true;
                break;
            }

            var (element, depth, windowRef) = queue.Dequeue();
            try
            {
                snapshots.Add(AddElement(element, windowRef, $"el-{generation}-{snapshots.Count}"));
                if (depth >= DesktopTestingLimits.MaxUiAutomationDepth)
                {
                    truncated = true;
                    continue;
                }

                foreach (var child in element.FindAllChildren())
                {
                    queue.Enqueue((child, depth + 1, windowRef));
                }
            }
            catch (Exception exception) when (exception is FlaUI.Core.Exceptions.ElementNotAvailableException
                or InvalidOperationException)
            {
                // The element disappeared mid-walk; the rest of the tree is still worth returning.
            }
        }

        return (snapshots, truncated);
    }

    private static readonly TimeSpan InspectionBudget = TimeSpan.FromSeconds(2);

    private static string WindowRefFor(AutomationElement window, string processRef)
    {
        // Keyed by the native handle so the ref identifies one window rather than one inspection, and
        // so a caller can hand it straight back for focusing.
        try
        {
            var handle = new IntPtr(window.Properties.NativeWindowHandle.ValueOrDefault);
            if (handle != nint.Zero)
            {
                return $"window-{processRef}-{handle.ToInt64():X}";
            }
        }
        catch (Exception exception) when (exception is FlaUI.Core.Exceptions.PropertyNotSupportedException
            or FlaUI.Core.Exceptions.ElementNotAvailableException)
        {
            // Fall through to the process-scoped ref below.
        }

        return $"window-{processRef}";
    }

    public DesktopUiElementSnapshot? ResolveLocator(DesktopLocator locator) =>
        ResolveLocatorCandidates(locator).SingleOrDefault();

    public IReadOnlyList<DesktopUiElementSnapshot> ResolveLocatorCandidates(DesktopLocator locator)
    {
        locator.Validate();
        return _elements
            .Select(pair => Describe(pair.Value, locator.WindowRef ?? string.Empty, pair.Key))
            .Where(element => locator.Kind == DesktopLocatorKind.AutomationId
                ? string.Equals(element.AutomationId, locator.AutomationId, StringComparison.Ordinal)
                : string.Equals(element.Role, locator.Role, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(element.Name, locator.Name, StringComparison.Ordinal))
            .ToArray();
    }

    public DesktopUiElementSnapshot? ResolveElement(string elementRef) =>
        _elements.TryGetValue(elementRef, out var element)
            ? Describe(element, string.Empty, elementRef)
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

    private DesktopUiElementSnapshot AddElement(AutomationElement element, string windowRef, string elementRef)
    {
        _elements[elementRef] = element;
        return Describe(element, windowRef, elementRef);
    }

    private static DesktopUiElementSnapshot Describe(AutomationElement element, string windowRef, string elementRef)
    {
        // Every one of these can throw PropertyNotSupportedException: a UI Automation provider is only
        // obliged to expose the properties it actually implements. Reading them directly took down the
        // whole inspection - and with it the worker - the moment one element lacked an AutomationId.
        var bounds = element.Properties.BoundingRectangle.ValueOrDefault;

        // These three were declared on the snapshot but never filled in, so a caller could see a
        // checkbox or a combo box and had no way to read what it was actually set to.
        string? toggleState = null;
        string? selection = null;
        string? text = null;
        var isSensitive = false;
        try
        {
            if (element.Patterns.Toggle.PatternOrDefault is ITogglePattern toggle)
            {
                toggleState = toggle.ToggleState.Value.ToString();
            }

            if (element.Patterns.SelectionItem.PatternOrDefault is ISelectionItemPattern selectionItem)
            {
                selection = selectionItem.IsSelected.Value ? "Selected" : "NotSelected";
            }

            isSensitive = element.Properties.IsPassword.ValueOrDefault;
            if (!isSensitive && element.Patterns.Value.PatternOrDefault is IValuePattern value)
            {
                var raw = value.Value.ValueOrDefault;
                text = raw is { Length: > 256 } ? raw[..256] : raw;
            }
        }
        catch (Exception exception) when (exception is FlaUI.Core.Exceptions.ElementNotAvailableException
            or FlaUI.Core.Exceptions.PropertyNotSupportedException
            or InvalidOperationException)
        {
            // The element went away or does not support the pattern; the rest of the snapshot stands.
        }

        return new DesktopUiElementSnapshot(
            elementRef,
            windowRef,
            element.Properties.ControlType.ValueOrDefault.ToString(),
            element.Properties.Name.ValueOrDefault,
            element.Properties.AutomationId.ValueOrDefault,
            new PixelBounds((int)bounds.X, (int)bounds.Y, (int)bounds.Width, (int)bounds.Height),
            element.Properties.IsEnabled.ValueOrDefault,
            toggleState,
            selection,
            text,
            isSensitive);
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
