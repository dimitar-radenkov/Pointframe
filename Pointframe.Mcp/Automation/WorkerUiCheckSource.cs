using Pointframe.Engine.Automation.Models;
using Pointframe.Engine.Automation.Services;

namespace Pointframe.Mcp.Automation;

/// <summary>
/// Evaluates <c>desktop_check_ui</c> conditions against a fresh UI Automation inspection of the
/// session's target, taken through the supervised worker.
/// </summary>
/// <remarks>
/// This replaces a stub that answered <c>StateAvailable: false</c> unconditionally, which made every
/// check inconclusive. That mattered more than it sounds: with no working verification, every action
/// tool's reported success was unfalsifiable, and an agent had no way to discover that an action had
/// silently done nothing.
/// </remarks>
public sealed class WorkerUiCheckSource(
    IDesktopUiObservationProvider observations,
    IDesktopProcessController processes) : IDesktopUiCheckSource
{
    public async Task<DesktopUiCheckEvaluation> EvaluateAsync(
        DesktopProcessIdentity process,
        DesktopUiCheckCondition condition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(condition);
        cancellationToken.ThrowIfCancellationRequested();

        // Process liveness is answered by the process controller, not by the element tree: a target
        // that has exited has no tree to inspect, so asking the provider first would report
        // ProviderUnavailable for what is actually a definite answer.
        if (condition is DesktopUiCheckCondition.ProcessExited exited)
        {
            var state = await processes.GetStateAsync(process, cancellationToken).ConfigureAwait(false);

            // Unavailable means the retained process identity could not be verified (for example its
            // path or hash no longer matches) -- it is not proof the process actually exited, so it
            // must read as inconclusive rather than as a passing "exited" result.
            if (state == DesktopTargetState.Unavailable)
            {
                return new DesktopUiCheckEvaluation(StateAvailable: false, Matches: false, MatchCount: 0, "ProcessStateUnavailable");
            }

            return new DesktopUiCheckEvaluation(
                StateAvailable: true,
                Matches: state == DesktopTargetState.Exited && string.Equals(exited.ProcessRef, process.ProcessRef, StringComparison.Ordinal),
                MatchCount: 1);
        }

        var snapshot = observations.Inspect(new DesktopObservationRequest(
            process,
            [],
            IncludeUiAutomation: true));
        if (snapshot.Status != DesktopUiAutomationStatus.Available)
        {
            return new DesktopUiCheckEvaluation(false, false, 0, snapshot.ErrorCode ?? "ProviderUnavailable");
        }

        return Evaluate(snapshot.Elements, condition);
    }

    internal static DesktopUiCheckEvaluation Evaluate(
        IReadOnlyList<DesktopUiElementSnapshot> elements,
        DesktopUiCheckCondition condition)
    {
        switch (condition)
        {
            case DesktopUiCheckCondition.WindowExists windowExists:
                {
                    var count = elements.Count(element => Matches(element.WindowRef, windowExists.WindowRef));
                    return new DesktopUiCheckEvaluation(true, count > 0, count);
                }

            case DesktopUiCheckCondition.WindowAbsent windowAbsent:
                {
                    var count = elements.Count(element => Matches(element.WindowRef, windowAbsent.WindowRef));
                    return new DesktopUiCheckEvaluation(true, count == 0, count);
                }
        }

        var matches = Locate(elements, Locator(condition)).ToArray();
        return condition switch
        {
            DesktopUiCheckCondition.Exists => new DesktopUiCheckEvaluation(true, matches.Length > 0, matches.Length),
            DesktopUiCheckCondition.Absent => new DesktopUiCheckEvaluation(true, matches.Length == 0, matches.Length),
            DesktopUiCheckCondition.Enabled enabled => Single(
                matches,
                element => element.IsEnabled == enabled.Expected),
            DesktopUiCheckCondition.ToggleEquals toggle => Single(
                matches,
                element => Matches(element.ToggleState, toggle.Expected)),
            DesktopUiCheckCondition.SelectionEquals selection => Single(
                matches,
                element => Matches(element.Selection, selection.Expected)),
            DesktopUiCheckCondition.TextEquals text => Single(
                matches,
                // A password field reports no text by design, so a text assertion against one can
                // never be satisfied and must not silently read as a mismatch.
                element => !element.IsSensitive && string.Equals(element.Text, text.Expected, StringComparison.Ordinal)),
            _ => new DesktopUiCheckEvaluation(false, false, 0, "UnsupportedCondition"),
        };
    }

    private static DesktopLocator Locator(DesktopUiCheckCondition condition) => condition switch
    {
        DesktopUiCheckCondition.Exists exists => exists.Locator,
        DesktopUiCheckCondition.Absent absent => absent.Locator,
        DesktopUiCheckCondition.Enabled enabled => enabled.Locator,
        DesktopUiCheckCondition.ToggleEquals toggle => toggle.Locator,
        DesktopUiCheckCondition.SelectionEquals selection => selection.Locator,
        DesktopUiCheckCondition.TextEquals text => text.Locator,
        _ => throw new ArgumentOutOfRangeException(nameof(condition), condition, "The condition carries no locator."),
    };

    private static IEnumerable<DesktopUiElementSnapshot> Locate(
        IReadOnlyList<DesktopUiElementSnapshot> elements,
        DesktopLocator locator)
    {
        locator.Validate();
        var candidates = locator.Kind == DesktopLocatorKind.AutomationId
            ? elements.Where(element => string.Equals(element.AutomationId, locator.AutomationId, StringComparison.Ordinal))
            : elements.Where(element =>
                string.Equals(element.Role, locator.Role, StringComparison.OrdinalIgnoreCase)
                && string.Equals(element.Name, locator.Name, StringComparison.Ordinal));

        return locator.WindowRef is { Length: > 0 } windowRef
            ? candidates.Where(element => Matches(element.WindowRef, windowRef))
            : candidates;
    }

    // The caller decides what an ambiguous match means: DesktopUiCheckService rejects a match count
    // other than one for every state predicate, so report the real count rather than collapsing it.
    private static DesktopUiCheckEvaluation Single(
        IReadOnlyList<DesktopUiElementSnapshot> matches,
        Func<DesktopUiElementSnapshot, bool> predicate) =>
        new(true, matches.Count == 1 && predicate(matches[0]), matches.Count);

    private static bool Matches(string? actual, string? expected) =>
        string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
}
