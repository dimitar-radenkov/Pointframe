using Pointframe.Engine.Automation.Models;

namespace Pointframe.Engine.Automation.Services;

public interface IDesktopUiAuditRule
{
    string Id { get; }

    int Version { get; }

    DesktopUiAuditRuleOutput Evaluate(DesktopObservationResult observation);
}

public interface IDesktopUiAuditService
{
    DesktopUiAuditResult Audit(DesktopObservationResult observation);
}

public sealed class DesktopUiAuditService : IDesktopUiAuditService
{
    private readonly IReadOnlyList<IDesktopUiAuditRule> _rules;

    public DesktopUiAuditService()
        : this([new PlaceholderTextAuditRule()])
    {
    }

    public DesktopUiAuditService(IReadOnlyList<IDesktopUiAuditRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        _rules = rules;
    }

    public DesktopUiAuditResult Audit(DesktopObservationResult observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var findings = new List<DesktopUiAuditFinding>();
        var inspected = 0;
        var skipped = new Dictionary<string, int>(StringComparer.Ordinal);
        var ocrLinesSuppressed = 0;
        foreach (var rule in _rules)
        {
            var output = rule.Evaluate(observation);
            ocrLinesSuppressed += output.OcrLinesSuppressedAsUserContent;
            findings.AddRange(output.Findings);
            // Rules look at overlapping elements, so the coverage of the whole audit is the widest any one rule had.
            inspected = Math.Max(inspected, output.ElementsInspected);
            foreach (var (reason, count) in output.ElementsSkipped)
            {
                skipped[reason] = Math.Max(skipped.GetValueOrDefault(reason), count);
            }
        }

        var ocrState = observation.Ocr is null
            ? DesktopUiAuditOcrState.NotRequested
            : observation.Ocr.IsAvailable ? DesktopUiAuditOcrState.Available : DesktopUiAuditOcrState.Unavailable;
        var coverage = new DesktopUiAuditCoverage(
            _rules.Select(rule => new DesktopUiAuditRuleInfo(rule.Id, rule.Version)).ToArray(),
            inspected,
            skipped,
            ocrState,
            observation.UiAutomation?.IsTruncated ?? false,
            observation.UiAutomation?.Status.ToString() ?? "NotRequested",
            ocrLinesSuppressed);
        return new DesktopUiAuditResult(observation.Observation.ObservationRef, findings, coverage);
    }
}
