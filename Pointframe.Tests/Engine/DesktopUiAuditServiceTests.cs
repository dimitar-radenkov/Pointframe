using Pointframe.Engine;
using Pointframe.Engine.Automation.Models;
using Pointframe.Engine.Automation.Services;
using Xunit;

namespace Pointframe.Tests.Engine;

public sealed class DesktopUiAuditServiceTests
{
    private static readonly PixelBounds Bounds = new(10, 20, 30, 40);

    [Theory]
    [InlineData("TODO", "TODO")]
    [InlineData("todo", "TODO")]
    [InlineData("  FIXME  ", "FIXME")]
    [InlineData("Lorem Ipsum", "lorem ipsum")]
    [InlineData("lorem   ipsum", "lorem ipsum")]
    [InlineData("{0}", "{0}")]
    [InlineData("{9}", "{9}")]
    [InlineData("null", "null")]
    [InlineData("NULL", "null")]
    [InlineData("undefined", "undefined")]
    [InlineData("NaN", "NaN")]
    [InlineData(" nan ", "NaN")]
    public void PlaceholderText_WholeTextToken_IsMediumSeverityHighConfidence(string name, string token)
    {
        var finding = Assert.Single(Audit(Element("e1", "Text", name)).Findings);

        Assert.Equal(token, finding.MatchedToken);
        Assert.Equal("placeholder-text", finding.RuleId);
        Assert.Equal(1, finding.RuleVersion);
        Assert.Equal(DesktopUiAuditSeverity.Medium, finding.Severity);
        Assert.Equal(DesktopUiAuditConfidence.High, finding.Confidence);
        Assert.Equal(DesktopUiAuditSource.Uia, finding.Source);
    }

    [Theory]
    [InlineData("TODO: wire this up", "TODO")]
    [InlineData("Fix this FIXME before release", "FIXME")]
    [InlineData("Lorem ipsum dolor sit amet", "lorem ipsum")]
    [InlineData("Welcome back, {0}!", "{0}")]
    [InlineData("Page {1} of {2}", "{1}")]
    public void PlaceholderText_TokenInsideLongerText_IsMediumSeverityMediumConfidence(string name, string token)
    {
        var finding = Audit(Element("e1", "Text", name)).Findings.First(item => item.MatchedToken == token);

        Assert.Equal(DesktopUiAuditSeverity.Medium, finding.Severity);
        Assert.Equal(DesktopUiAuditConfidence.Medium, finding.Confidence);
    }

    [Fact]
    public void PlaceholderText_FormatPlaceholders_AreReportedOncePerDistinctToken()
    {
        var findings = Audit(Element("e1", "Text", "Page {1} of {2}, again {1}")).Findings;

        Assert.Equal(new[] { "{1}", "{2}" }, findings.Select(finding => finding.MatchedToken).ToArray());
    }

    [Fact]
    public void PlaceholderText_UiaFinding_CarriesElementEvidence()
    {
        var element = new DesktopUiElementSnapshot(
            "element-7", "window-3", "Text", "TODO", "StatusLabel", new PixelBounds(100, 200, 50, 16), true);

        var finding = Assert.Single(Audit(element).Findings);

        Assert.Equal("element-7", finding.ElementRef);
        Assert.Equal("window-3", finding.WindowRef);
        Assert.Equal("Text", finding.Role);
        Assert.Equal("TODO", finding.Name);
        Assert.Equal("StatusLabel", finding.AutomationId);
        Assert.Equal(new PixelBounds(100, 200, 50, 16), finding.BoundsPixels);
        Assert.Null(finding.ImageRef);
        Assert.Null(finding.OcrLine);
    }

    [Theory]
    [InlineData("{name}")]
    [InlineData("{}")]
    [InlineData("{10}")]
    [InlineData("{0:N2}")]
    [InlineData("Autodoc")]
    [InlineData("Todos")]
    [InlineData("The value is null here")]
    [InlineData("null pointer")]
    [InlineData("undefined behaviour")]
    [InlineData("NaN is not a number")]
    [InlineData("Save")]
    public void PlaceholderText_LegitimateLookalikes_AreNotFlagged(string name)
    {
        Assert.Empty(Audit(Element("e1", "Text", name)).Findings);
    }

    [Fact]
    public void PlaceholderText_TextProperty_IsAudited()
    {
        var finding = Assert.Single(Audit(Element("e1", "Text", "Label", text: "TODO")).Findings);

        Assert.Equal("text", finding.Field);
        Assert.Equal(DesktopUiAuditConfidence.High, finding.Confidence);
    }

    [Fact]
    public void PlaceholderText_TokenInNameAndText_IsReportedOnceWithTheStrongerMatch()
    {
        var finding = Assert.Single(Audit(Element("e1", "Text", "TODO later", text: "TODO")).Findings);

        Assert.Equal("text", finding.Field);
        Assert.Equal(DesktopUiAuditConfidence.High, finding.Confidence);
    }

    [Fact]
    public void PlaceholderText_SensitiveElement_IsNotInspectedAndIsCounted()
    {
        var result = Audit(
            Element("e1", "Text", "TODO", sensitive: true),
            Element("e2", "Text", "Fine"));

        Assert.Empty(result.Findings);
        Assert.Equal(1, result.Coverage.ElementsInspected);
        Assert.Equal(1, result.Coverage.ElementsSkipped[DesktopUiAuditSkipReason.Sensitive]);
    }

    [Theory]
    [InlineData("Edit")]
    [InlineData("Document")]
    [InlineData("edit")]
    public void PlaceholderText_EditableRoles_AreNotInspectedAndAreCounted(string role)
    {
        var result = Audit(
            Element("e1", role, "TODO", text: "FIXME {0}"),
            Element("e2", "Text", "Fine"));

        Assert.Empty(result.Findings);
        Assert.Equal(1, result.Coverage.ElementsInspected);
        Assert.Equal(1, result.Coverage.ElementsSkipped[DesktopUiAuditSkipReason.EditableRole]);
    }

    [Fact]
    public void PlaceholderText_CoverageCountsSkipsByReason()
    {
        var result = Audit(
            Element("e1", "Edit", "a"),
            Element("e2", "Document", "b"),
            Element("e3", "Text", "c", sensitive: true),
            Element("e4", "Button", "OK"));

        Assert.Equal(1, result.Coverage.ElementsInspected);
        Assert.Equal(2, result.Coverage.ElementsSkipped[DesktopUiAuditSkipReason.EditableRole]);
        Assert.Equal(1, result.Coverage.ElementsSkipped[DesktopUiAuditSkipReason.Sensitive]);
        Assert.Equal(new[] { new DesktopUiAuditRuleInfo("placeholder-text", 1) }, result.Coverage.RulesRun.ToArray());
    }

    [Fact]
    public void PlaceholderText_OcrOnlyMatch_IsLowSeverityLowConfidenceWithImageAndLine()
    {
        var ocr = new DesktopObservationOcrResult("image-1", "Welcome\r\nTODO fix the header\r\nSign in");

        var finding = Assert.Single(Audit(ocr, Element("e1", "Button", "Sign in")).Findings);

        Assert.Equal(DesktopUiAuditSource.Ocr, finding.Source);
        Assert.Equal(DesktopUiAuditSeverity.Low, finding.Severity);
        Assert.Equal(DesktopUiAuditConfidence.Low, finding.Confidence);
        Assert.Equal("TODO", finding.MatchedToken);
        Assert.Equal("image-1", finding.ImageRef);
        Assert.Equal("TODO fix the header", finding.OcrLine);
        Assert.Null(finding.ElementRef);
        Assert.Null(finding.BoundsPixels);
    }

    [Fact]
    public void PlaceholderText_TokenSeenByUiaAndOcr_IsReportedOnceAsUia()
    {
        var ocr = new DesktopObservationOcrResult("image-1", "Status\nTODO\nOK");

        var finding = Assert.Single(Audit(ocr, Element("e1", "Text", "TODO")).Findings);

        Assert.Equal(DesktopUiAuditSource.Uia, finding.Source);
        Assert.Equal("e1", finding.ElementRef);
    }

    [Fact]
    public void PlaceholderText_OcrToken_ThatEditableContentExplains_IsNotReported()
    {
        var ocr = new DesktopObservationOcrResult("image-1", "TODO buy milk");

        var result = Audit(ocr, Element("e1", "Edit", null, text: "TODO buy milk"));

        Assert.Empty(result.Findings);
    }

    [Fact]
    public void PlaceholderText_OcrNullAndUndefined_OnlyCountAsWholeLines()
    {
        var ocr = new DesktopObservationOcrResult("image-1", "value is null here\nundefined\nnull pointer");

        var findings = Audit(ocr).Findings;

        var finding = Assert.Single(findings);
        Assert.Equal("undefined", finding.MatchedToken);
        Assert.Equal("undefined", finding.OcrLine);
    }

    [Fact]
    public void PlaceholderText_RepeatedOcrLine_IsReportedOnce()
    {
        var ocr = new DesktopObservationOcrResult("image-1", "TODO\nTODO\n  todo ");

        Assert.Single(Audit(ocr).Findings);
    }

    [Fact]
    public void Coverage_ReportsOcrState()
    {
        Assert.Equal(DesktopUiAuditOcrState.NotRequested, Audit().Coverage.OcrAvailable);
        Assert.Equal(DesktopUiAuditOcrState.Available, Audit(new DesktopObservationOcrResult("i", null)).Coverage.OcrAvailable);
        Assert.Equal(
            DesktopUiAuditOcrState.Unavailable,
            Audit(new DesktopObservationOcrResult("i", null, IsAvailable: false)).Coverage.OcrAvailable);
    }

    [Fact]
    public void Coverage_ReportsTruncationAndMissingUia()
    {
        var truncated = new DesktopObservationResult(
            ObservationHeader(),
            new DesktopUiSnapshot(DesktopUiAutomationStatus.Partial, [], DateTimeOffset.UtcNow, IsTruncated: true),
            1);
        var withoutUia = new DesktopObservationResult(ObservationHeader(), null, 1);

        var truncatedCoverage = new DesktopUiAuditService().Audit(truncated).Coverage;
        var missingCoverage = new DesktopUiAuditService().Audit(withoutUia).Coverage;

        Assert.True(truncatedCoverage.UiaTruncated);
        Assert.Equal("Partial", truncatedCoverage.UiaStatus);
        Assert.False(missingCoverage.UiaTruncated);
        Assert.Equal("NotRequested", missingCoverage.UiaStatus);
        Assert.Equal(0, missingCoverage.ElementsInspected);
    }

    [Fact]
    public void Service_AcceptsASecondRuleWithoutChangingTheResultShape()
    {
        var service = new DesktopUiAuditService([new PlaceholderTextAuditRule(), new StubRule()]);

        var result = service.Audit(Observation(Element("e1", "Text", "TODO")));

        Assert.Equal(new[] { "placeholder-text", "stub-rule" }, result.Findings.Select(finding => finding.RuleId).ToArray());
        Assert.Equal(
            new[] { new DesktopUiAuditRuleInfo("placeholder-text", 1), new DesktopUiAuditRuleInfo("stub-rule", 3) },
            result.Coverage.RulesRun.ToArray());
        Assert.Equal(5, result.Coverage.ElementsInspected);
    }

    private static DesktopUiAuditResult Audit(params DesktopUiElementSnapshot[] elements) =>
        new DesktopUiAuditService().Audit(Observation(elements));

    private static DesktopUiAuditResult Audit(DesktopObservationOcrResult ocr, params DesktopUiElementSnapshot[] elements) =>
        new DesktopUiAuditService().Audit(Observation(ocr, elements));

    private static DesktopObservationResult Observation(params DesktopUiElementSnapshot[] elements) =>
        Observation(null, elements);

    private static DesktopObservationResult Observation(DesktopObservationOcrResult? ocr, params DesktopUiElementSnapshot[] elements) =>
        new(
            ObservationHeader(),
            new DesktopUiSnapshot(DesktopUiAutomationStatus.Available, elements, DateTimeOffset.UtcNow),
            1,
            ocr);

    private static DesktopObservation ObservationHeader()
    {
        var now = DateTimeOffset.UtcNow;
        var process = new DesktopProcessIdentity("process-1", 1, now, "target.exe", "hash");
        return new DesktopObservation(
            1, "observation-1", process, DesktopTargetState.Running, DesktopObservationStatus.Available, [],
            DesktopUiAutomationStatus.Available, now);
    }

    private static DesktopUiElementSnapshot Element(
        string elementRef,
        string role,
        string? name,
        string? text = null,
        bool sensitive = false) =>
        new(elementRef, "window-1", role, name, "auto-" + elementRef, Bounds, true, Text: text, IsSensitive: sensitive);

    private sealed class StubRule : IDesktopUiAuditRule
    {
        public string Id => "stub-rule";

        public int Version => 3;

        public DesktopUiAuditRuleOutput Evaluate(DesktopObservationResult observation) =>
            new(
                [new DesktopUiAuditFinding(Id, Version, DesktopUiAuditSeverity.Low, DesktopUiAuditConfidence.Low, "stub", "x", "stub finding")],
                5,
                new Dictionary<string, int>());
    }
}
