namespace Pointframe.Engine.Automation.Models;

public static class DesktopUiAuditSeverity
{
    public const string Low = "low";
    public const string Medium = "medium";
}

public static class DesktopUiAuditConfidence
{
    public const string Low = "low";
    public const string Medium = "medium";
    public const string High = "high";
}

public static class DesktopUiAuditSource
{
    public const string Uia = "uia";
    public const string Ocr = "ocr";
}

public static class DesktopUiAuditSkipReason
{
    public const string Sensitive = "sensitive";
    public const string EditableRole = "editable-role";
}

public static class DesktopUiAuditOcrState
{
    public const string Available = "true";
    public const string Unavailable = "false";
    public const string NotRequested = "not-requested";
}

public sealed record DesktopUiAuditFinding(
    string RuleId,
    int RuleVersion,
    string Severity,
    string Confidence,
    string Source,
    string MatchedToken,
    string Message,
    string? ElementRef = null,
    string? WindowRef = null,
    string? Role = null,
    string? Name = null,
    string? AutomationId = null,
    PixelBounds? BoundsPixels = null,
    string? Field = null,
    string? Text = null,
    string? ImageRef = null,
    string? OcrLine = null);

public sealed record DesktopUiAuditRuleInfo(string Id, int Version);

public sealed record DesktopUiAuditRuleOutput(
    IReadOnlyList<DesktopUiAuditFinding> Findings,
    int ElementsInspected,
    IReadOnlyDictionary<string, int> ElementsSkipped);

public sealed record DesktopUiAuditCoverage(
    IReadOnlyList<DesktopUiAuditRuleInfo> RulesRun,
    int ElementsInspected,
    IReadOnlyDictionary<string, int> ElementsSkipped,
    string OcrAvailable,
    bool UiaTruncated,
    string UiaStatus);

public sealed record DesktopUiAuditResult(
    string ObservationRef,
    IReadOnlyList<DesktopUiAuditFinding> Findings,
    DesktopUiAuditCoverage Coverage);
