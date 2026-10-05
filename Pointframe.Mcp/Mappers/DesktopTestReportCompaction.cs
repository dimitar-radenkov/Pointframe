using Pointframe.Engine.Automation.Services;

namespace Pointframe.Mcp;

/// <summary>
/// Builds the inline copy of a finalized report for detail=compact. Verdicts, criteria, every action
/// outcome and every check verdict stay; per-item evidence, recorded check conditions, and the whole proof
/// entry chain are dropped from the response only. The proof bundle on disk is always written from the
/// full report, so the signed record and its evidence are never reduced.
/// </summary>
internal static class DesktopTestReportCompaction
{
    public static DesktopTestReport Compact(DesktopTestReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return report with
        {
            Actions = report.Actions.Select(action => action with { Evidence = null }).ToArray(),
            Checks = report.Checks.Select(check => check with { Evidence = null, Spec = null }).ToArray(),
            Proof = null,
        };
    }
}
