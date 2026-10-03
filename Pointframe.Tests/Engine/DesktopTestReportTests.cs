using Pointframe.Engine.Automation.Models;
using Pointframe.Engine.Automation.Services;
using Xunit;

namespace Pointframe.Tests.Engine;

public sealed class DesktopTestReportTests
{
    [Fact]
    public async Task FinalizeDistinguishesInconclusiveChecks()
    {
        var service = new DesktopTestReportService();
        service.RecordCheck("session-1", new DesktopTestCheckReport(
            "visual check",
            DesktopVerificationStatus.Inconclusive,
            true,
            "external-image",
            "No reliable oracle",
            DateTimeOffset.UtcNow));

        var report = await service.FinalizeAsync("session-1");

        Assert.Equal("inconclusive", report.Verdict);
        Assert.Single(report.Checks);
        Assert.True(report.Checks[0].External);
    }

    [Fact]
    public async Task FailedCheckWinsOverInconclusive()
    {
        var service = new DesktopTestReportService();
        service.RecordCheck("session-1", Check(DesktopVerificationStatus.Inconclusive));
        service.RecordCheck("session-1", Check(DesktopVerificationStatus.Failed));

        var report = await service.FinalizeAsync("session-1");

        Assert.Equal("failed", report.Verdict);
    }

    [Fact]
    public async Task ReportWithoutChecksIsInconclusive()
    {
        var service = new DesktopTestReportService();
        service.RecordAction("session-1", Action(DesktopDispatchStatus.Complete, DesktopVerificationStatus.NotRequested));

        var report = await service.FinalizeAsync("session-1");

        Assert.Equal("inconclusive", report.Verdict);
    }

    [Fact]
    public async Task PassingChecksWithCompletedActionsPass()
    {
        var service = new DesktopTestReportService();
        service.RecordAction("session-1", Action(DesktopDispatchStatus.Complete, DesktopVerificationStatus.NotRequested));
        service.RecordCheck("session-1", Check(DesktopVerificationStatus.Passed));

        var report = await service.FinalizeAsync("session-1");

        Assert.Equal("passed", report.Verdict);
    }

    [Fact]
    public async Task ActionRejectedBeforeDispatchDoesNotAffectVerdict()
    {
        var service = new DesktopTestReportService();
        service.RecordAction("session-1", Action(DesktopDispatchStatus.NotStarted, DesktopVerificationStatus.Failed));
        service.RecordCheck("session-1", Check(DesktopVerificationStatus.Passed));

        var report = await service.FinalizeAsync("session-1");

        Assert.Equal("passed", report.Verdict);
    }

    [Fact]
    public async Task DispatchedActionWithFailedVerificationFails()
    {
        var service = new DesktopTestReportService();
        service.RecordAction("session-1", Action(DesktopDispatchStatus.Complete, DesktopVerificationStatus.Failed));
        service.RecordCheck("session-1", Check(DesktopVerificationStatus.Passed));

        var report = await service.FinalizeAsync("session-1");

        Assert.Equal("failed", report.Verdict);
    }

    [Theory]
    [InlineData(DesktopDispatchStatus.Partial)]
    [InlineData(DesktopDispatchStatus.Unknown)]
    public async Task UncertainDispatchIsInconclusive(DesktopDispatchStatus dispatch)
    {
        var service = new DesktopTestReportService();
        service.RecordAction("session-1", Action(dispatch, DesktopVerificationStatus.NotRequested));
        service.RecordCheck("session-1", Check(DesktopVerificationStatus.Passed));

        var report = await service.FinalizeAsync("session-1");

        Assert.Equal("inconclusive", report.Verdict);
    }

    [Fact]
    public async Task CriteriaAreNumberedAndHashed()
    {
        var service = new DesktopTestReportService();
        service.Initialize("session-1", "app.exe", "ABC", [" Saved text reopens ", "Checkbox stays checked"]);

        var report = await service.FinalizeAsync("session-1");

        Assert.Equal(["C1", "C2"], report.Criteria.Select(criterion => criterion.Id));
        Assert.Equal("Saved text reopens", report.Criteria[0].Text);
        Assert.Equal(
            DesktopTestReportService.HashCriteria(["Saved text reopens", "Checkbox stays checked"]),
            report.CriteriaSha256);
        Assert.True(service.HasCriterion("session-1", "C2"));
        Assert.False(service.HasCriterion("session-1", "C3"));
    }

    [Fact]
    public void CriteriaCannotBeReplacedOnceFrozen()
    {
        var service = new DesktopTestReportService();
        service.Initialize("session-1", "app.exe", "ABC", ["Original"]);

        Assert.Throws<InvalidOperationException>(
            () => service.Initialize("session-1", "app.exe", "ABC", ["Easier"]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyCriterionIsRejected(string criterion)
    {
        Assert.Throws<ArgumentException>(() => DesktopTestReportService.NormalizeCriteria([criterion]));
    }

    [Fact]
    public void TooManyCriteriaAreRejected()
    {
        var criteria = Enumerable.Range(0, DesktopTestReportService.MaxCriteria + 1).Select(index => $"c{index}").ToArray();

        Assert.Throws<ArgumentException>(() => DesktopTestReportService.NormalizeCriteria(criteria));
    }

    [Fact]
    public async Task UncoveredCriterionKeepsReportInconclusive()
    {
        var service = new DesktopTestReportService();
        service.Initialize("session-1", "app.exe", "ABC", ["First", "Second"]);
        service.RecordCheck("session-1", Check(DesktopVerificationStatus.Passed, "C1"));

        var report = await service.FinalizeAsync("session-1");

        Assert.Equal("inconclusive", report.Verdict);
        Assert.Equal("passed", report.Criteria[0].Verdict);
        Assert.Equal("uncovered", report.Criteria[1].Verdict);
    }

    [Fact]
    public async Task AllCriteriaCoveredWithNegativeControlPasses()
    {
        var service = new DesktopTestReportService();
        service.Initialize("session-1", "app.exe", "ABC", ["First", "Second"]);
        service.RecordCheck("session-1", Check(DesktopVerificationStatus.Passed, "C1"));
        service.RecordCheck("session-1", Check(DesktopVerificationStatus.Passed, "C2"));
        service.RecordCheck("session-1", NegativeControl(DesktopVerificationStatus.Passed));

        var report = await service.FinalizeAsync("session-1");

        Assert.Equal("passed", report.Verdict);
        Assert.All(report.Criteria, criterion => Assert.Equal("passed", criterion.Verdict));
    }

    [Fact]
    public async Task CoveredCriteriaWithoutNegativeControlAreInconclusive()
    {
        var service = new DesktopTestReportService();
        service.Initialize("session-1", "app.exe", "ABC", ["First"]);
        service.RecordCheck("session-1", Check(DesktopVerificationStatus.Passed, "C1"));

        var report = await service.FinalizeAsync("session-1");

        Assert.Equal("passed", report.Criteria[0].Verdict);
        Assert.Equal("inconclusive", report.Verdict);
    }

    [Fact]
    public async Task MatchedNegativeControlFailsTheReport()
    {
        var service = new DesktopTestReportService();
        service.Initialize("session-1", "app.exe", "ABC", ["First"]);
        service.RecordCheck("session-1", Check(DesktopVerificationStatus.Passed, "C1"));
        service.RecordCheck("session-1", NegativeControl(DesktopVerificationStatus.Failed));

        var report = await service.FinalizeAsync("session-1");

        Assert.Equal("failed", report.Verdict);
    }

    [Fact]
    public async Task NegativeControlsAloneAreInconclusive()
    {
        var service = new DesktopTestReportService();
        service.RecordCheck("session-1", NegativeControl(DesktopVerificationStatus.Passed));

        var report = await service.FinalizeAsync("session-1");

        Assert.Equal("inconclusive", report.Verdict);
    }

    [Fact]
    public async Task NegativeControlIsNotRequiredWithoutCriteria()
    {
        var service = new DesktopTestReportService();
        service.RecordCheck("session-1", Check(DesktopVerificationStatus.Passed));

        var report = await service.FinalizeAsync("session-1");

        Assert.Equal("passed", report.Verdict);
    }

    [Fact]
    public async Task FailedCheckFailsItsCriterionEvenAfterAPass()
    {
        var service = new DesktopTestReportService();
        service.Initialize("session-1", "app.exe", "ABC", ["First"]);
        service.RecordCheck("session-1", Check(DesktopVerificationStatus.Passed, "C1"));
        service.RecordCheck("session-1", Check(DesktopVerificationStatus.Failed, "C1"));

        var report = await service.FinalizeAsync("session-1");

        Assert.Equal("failed", report.Verdict);
        Assert.Equal("failed", report.Criteria[0].Verdict);
    }

    [Fact]
    public async Task OnlyInconclusiveChecksLeaveCriterionInconclusive()
    {
        var service = new DesktopTestReportService();
        service.Initialize("session-1", "app.exe", "ABC", ["First"]);
        service.RecordCheck("session-1", Check(DesktopVerificationStatus.Inconclusive, "C1"));

        var report = await service.FinalizeAsync("session-1");

        Assert.Equal("inconclusive", report.Criteria[0].Verdict);
    }

    private static DesktopTestCheckReport Check(DesktopVerificationStatus verdict, string? criterionId = null) =>
        new("check", verdict, false, "server-ui", null, DateTimeOffset.UtcNow, criterionId);

    private static DesktopTestCheckReport NegativeControl(DesktopVerificationStatus verdict) =>
        new("not check", verdict, false, "server-ui", null, DateTimeOffset.UtcNow, NegativeControl: true);

    private static DesktopTestActionReport Action(DesktopDispatchStatus dispatch, DesktopVerificationStatus verification)
    {
        var actionId = Guid.NewGuid().ToString();
        var error = verification == DesktopVerificationStatus.Failed
            ? new DesktopOperationError("InputDispatchFailed", "not accepted")
            : null;
        return new DesktopTestActionReport(
            actionId,
            "desktop action",
            new DesktopActionResult(
                DesktopTestingLimits.SchemaVersion,
                actionId,
                DesktopOperationStatus.Completed,
                dispatch,
                verification,
                DesktopObservationStatus.NotRequested,
                error),
            DateTimeOffset.UtcNow);
    }
}
