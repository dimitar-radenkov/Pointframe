using Pointframe.Engine.Automation.Models;

namespace Pointframe.Mcp.Automation;

internal sealed record DesktopSelector(string? AutomationId, string? Role, string? Name, string? WindowRef)
{
    public DesktopLocator ToLocator() => string.IsNullOrWhiteSpace(AutomationId)
        ? new DesktopLocator(DesktopLocatorKind.RoleAndName, Role: Role, Name: Name, WindowRef: WindowRef)
        : new DesktopLocator(DesktopLocatorKind.AutomationId, AutomationId: AutomationId, WindowRef: WindowRef);

    public string Describe()
    {
        var target = string.IsNullOrWhiteSpace(AutomationId)
            ? $"role '{Role}' and name '{Name}'"
            : $"automationId '{AutomationId}'";
        return string.IsNullOrWhiteSpace(WindowRef) ? target : $"{target} in window '{WindowRef}'";
    }
}

internal sealed record DesktopTargetSpec(string? ElementRef, DesktopSelector? Selector, string? ErrorCode, string? ErrorMessage)
{
    public bool IsValid => ErrorCode is null;
}

internal sealed record DesktopSelectorResolution(DesktopUiElementSnapshot? Element, string? ErrorCode, string? ErrorMessage)
{
    public bool IsResolved => Element is not null;
}

// A selector names an element by what it is, not by where one earlier inspection happened to put it. It is
// resolved against a fresh inspection at dispatch time, and an uncertain target never resolves: input is only
// dispatched on exactly one match in a complete tree.
internal static class DesktopSelectorResolver
{
    private const int MaxCandidates = 5;

    public static DesktopTargetSpec ParseTarget(
        string? elementRef,
        string? automationId,
        string? role,
        string? name,
        string? windowRef)
    {
        var hasElementRef = !string.IsNullOrWhiteSpace(elementRef);
        var hasAutomationId = !string.IsNullOrWhiteSpace(automationId);
        var hasRole = !string.IsNullOrWhiteSpace(role);
        var hasName = !string.IsNullOrWhiteSpace(name);
        var hasWindow = !string.IsNullOrWhiteSpace(windowRef);
        var hasSelectorField = hasAutomationId || hasRole || hasName || hasWindow;

        if (hasElementRef && hasSelectorField)
        {
            return Invalid("InvalidRequest", "Name the target one way: either elementRef, or a selector (automationId, or role with name, optionally with windowRef). Both were given.");
        }

        if (hasElementRef)
        {
            return new DesktopTargetSpec(elementRef, null, null, null);
        }

        if (hasAutomationId && !hasRole && !hasName)
        {
            return new DesktopTargetSpec(null, new DesktopSelector(automationId, null, null, hasWindow ? windowRef : null), null, null);
        }

        if (!hasAutomationId && hasRole && hasName)
        {
            return new DesktopTargetSpec(null, new DesktopSelector(null, role, name, hasWindow ? windowRef : null), null, null);
        }

        if (hasAutomationId && (hasRole || hasName))
        {
            return Invalid("InvalidRequest", "A selector is either automationId or role with name, not both.");
        }

        if (hasRole != hasName)
        {
            return Invalid("InvalidRequest", "A role and name selector needs both role and name.");
        }

        if (hasWindow)
        {
            return Invalid("InvalidRequest", "windowRef only scopes a selector; add automationId, or role with name.");
        }

        return Invalid("InvalidRequest", "Name the target with elementRef, or with a selector: automationId, or role with name (optionally scoped by windowRef).");
    }

    public static DesktopSelectorResolution Resolve(DesktopUiSnapshot snapshot, DesktopSelector selector)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(selector);
        if (snapshot.Status != DesktopUiAutomationStatus.Available)
        {
            return Failed(snapshot.ErrorCode ?? "ProviderUnavailable", "UI Automation was not available, so the target could not be established. No input was sent.");
        }

        var matches = WorkerUiCheckSource.Locate(snapshot.Elements, selector.ToLocator()).ToArray();
        if (matches.Length > 1)
        {
            var candidates = string.Join("; ", matches.Take(MaxCandidates).Select(Candidate));
            return Failed(
                "AmbiguousTarget",
                $"{matches.Length} elements matched {selector.Describe()}, and an input needs exactly one. No input was sent. Candidates: {candidates}. Add windowRef, or use a more specific automationId or role and name.");
        }

        // A single visible match in a partial tree is not proof of uniqueness: an unseen duplicate could be the
        // real target, so nothing is dispatched on it.
        if (snapshot.IsTruncated)
        {
            return Failed(
                "InspectionIncomplete",
                $"The UI Automation walk stopped early, so it could not establish whether {selector.Describe()} is the only match. No input was sent. This is not an app defect: retry with a new actionId.");
        }

        return matches.Length == 0
            ? Failed("ElementNotFound", $"No element matched {selector.Describe()}. No input was sent.")
            : new DesktopSelectorResolution(matches[0], null, null);
    }

    private static string Candidate(DesktopUiElementSnapshot element) =>
        $"role '{element.Role}', name '{element.Name}', automationId '{element.AutomationId}', window '{element.WindowRef}'";

    private static DesktopTargetSpec Invalid(string code, string message) => new(null, null, code, message);

    private static DesktopSelectorResolution Failed(string code, string message) => new(null, code, message);
}
