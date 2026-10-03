using Moq;
using Pointframe.Engine;
using Pointframe.Engine.Automation.Models;
using Pointframe.Engine.Automation.Services;
using Pointframe.Mcp;
using Pointframe.Mcp.Automation;
using Pointframe.Mcp.Configuration;
using Xunit;

namespace Pointframe.Tests.Mcp;

// desktop_check_ui used to answer the caller without recording anything, so every session report had no
// checks and finalized as passed. These tests pin that the server writes what it evaluated into the report.
public sealed class DesktopCheckReportingTests
{
    private const string SessionId = "session-1";

    private readonly Mock<IDesktopUiCheckService> _checks = new();
    private readonly DesktopTestReportService _reports = new();
    private readonly Mock<IDesktopEvidenceRecorder> _evidence = new();

    [Fact]
    public async Task EvaluatedCheckIsRecordedWithItsCriterion()
    {
        _reports.Initialize(SessionId, "target.exe", "hash", ["Text box shows the typed text"]);
        SetupEvaluation(new DesktopUiCheckEvaluation(true, true, 1));

        var response = await CreateTools().CheckUiAsync(SessionId, "textEquals", automationId: "textBox", expected: "hello", criterionId: "C1");

        Assert.Equal("passed", response.Verification);
        var report = await _reports.FinalizeAsync(SessionId);
        var check = Assert.Single(report.Checks);
        Assert.Equal(DesktopVerificationStatus.Passed, check.Verdict);
        Assert.Equal("C1", check.CriterionId);
        Assert.Equal("server-uia", check.OracleType);
        Assert.Equal("textEquals automationId=textBox expected=hello", check.Description);
        Assert.Equal("passed", report.Criteria[0].Verdict);
    }

    [Fact]
    public async Task CheckEvidenceIsCapturedWhenThePolicyAsksForIt()
    {
        var captured = new DesktopTestEvidence("0001-check.png", "ABC", new PixelBounds(0, 0, 10, 10), DateTimeOffset.UtcNow);
        _evidence.Setup(recorder => recorder.ShouldCapture(SessionId, true)).Returns(true);
        _evidence.Setup(recorder => recorder.Capture(SessionId, It.IsAny<DesktopProcessIdentity>(), "check")).Returns(captured);
        SetupEvaluation(new DesktopUiCheckEvaluation(true, false, 0));

        await CreateTools().CheckUiAsync(SessionId, "exists", automationId: "saveButton");

        Assert.Same(captured, Assert.Single(_reports.Get(SessionId).Checks).Evidence);
    }

    [Fact]
    public async Task CheckEvidenceIsSkippedWhenThePolicyDoesNotAskForIt()
    {
        SetupEvaluation(new DesktopUiCheckEvaluation(true, true, 1));

        await CreateTools().CheckUiAsync(SessionId, "exists", automationId: "saveButton");

        Assert.Null(Assert.Single(_reports.Get(SessionId).Checks).Evidence);
        _evidence.Verify(
            recorder => recorder.Capture(It.IsAny<string>(), It.IsAny<DesktopProcessIdentity>(), It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task NegativeControlPassesWhenTheWrongExpectationIsRejected()
    {
        SetupEvaluation(new DesktopUiCheckEvaluation(true, false, 0));

        var response = await CreateTools().CheckUiAsync(SessionId, "textEquals", automationId: "textBox", expected: "wrong", expectFailure: true);

        Assert.Equal("passed", response.Verification);
        Assert.False(response.Matches);
        var check = Assert.Single(_reports.Get(SessionId).Checks);
        Assert.True(check.NegativeControl);
        Assert.Equal(DesktopVerificationStatus.Passed, check.Verdict);
        Assert.Equal("not textEquals automationId=textBox expected=wrong", check.Description);
    }

    [Fact]
    public async Task NegativeControlFailsWhenTheWrongExpectationHolds()
    {
        SetupEvaluation(new DesktopUiCheckEvaluation(true, true, 1));

        var response = await CreateTools().CheckUiAsync(SessionId, "textEquals", automationId: "textBox", expected: "wrong", expectFailure: true);

        Assert.Equal("failed", response.Verification);
        Assert.Equal("NegativeControlMatched", response.Error?.Code);
        Assert.Equal("failed", (await _reports.FinalizeAsync(SessionId)).Verdict);
    }

    [Fact]
    public async Task NegativeControlOnUnreadableStateIsInconclusive()
    {
        SetupEvaluation(new DesktopUiCheckEvaluation(false, false, 0, "UiaUnavailable", "No tree"));

        var response = await CreateTools().CheckUiAsync(SessionId, "exists", automationId: "ghost", expectFailure: true);

        Assert.Equal("inconclusive", response.Verification);
        Assert.Equal(DesktopVerificationStatus.Inconclusive, Assert.Single(_reports.Get(SessionId).Checks).Verdict);
    }

    [Fact]
    public async Task NegativeControlCannotCoverACriterion()
    {
        _reports.Initialize(SessionId, "target.exe", "hash", ["Only criterion"]);

        var response = await CreateTools().CheckUiAsync(SessionId, "exists", automationId: "ghost", criterionId: "C1", expectFailure: true);

        Assert.Equal("InvalidCondition", response.Error?.Code);
        Assert.Empty(_reports.Get(SessionId).Checks);
    }

    [Fact]
    public async Task FailedCheckFailsTheReport()
    {
        SetupEvaluation(new DesktopUiCheckEvaluation(true, false, 0));

        await CreateTools().CheckUiAsync(SessionId, "exists", automationId: "saveButton");

        var report = await _reports.FinalizeAsync(SessionId);
        Assert.Equal(DesktopVerificationStatus.Failed, Assert.Single(report.Checks).Verdict);
        Assert.Equal("failed", report.Verdict);
    }

    [Fact]
    public async Task FailedCheckReturnsAndRecordsTheActualValue()
    {
        SetupEvaluation(new DesktopUiCheckEvaluation(true, false, 1, ActualValue: "On"));

        var response = await CreateTools().CheckUiAsync(
            SessionId,
            "toggleEquals",
            automationId: "checkBox",
            expected: "false");

        Assert.Equal("On", response.ActualValue);
        Assert.Equal("Expected 'false', but found 'On'.", Assert.Single(_reports.Get(SessionId).Checks).Message);
    }

    [Fact]
    public async Task UnreadableStateIsRecordedAsInconclusive()
    {
        SetupEvaluation(new DesktopUiCheckEvaluation(false, false, 0, "UiaUnavailable", "No tree"));

        await CreateTools().CheckUiAsync(SessionId, "exists", automationId: "saveButton");

        var report = await _reports.FinalizeAsync(SessionId);
        var check = Assert.Single(report.Checks);
        Assert.Equal(DesktopVerificationStatus.Inconclusive, check.Verdict);
        Assert.Equal("No tree", check.Message);
        Assert.Equal("inconclusive", report.Verdict);
    }

    [Fact]
    public async Task UnknownCriterionIsRejectedWithoutEvaluating()
    {
        _reports.Initialize(SessionId, "target.exe", "hash", ["Only criterion"]);

        var response = await CreateTools().CheckUiAsync(SessionId, "exists", automationId: "saveButton", criterionId: "C2");

        Assert.Equal("UnknownCriterion", response.Error?.Code);
        _checks.Verify(
            service => service.CheckAsync(It.IsAny<DesktopProcessIdentity>(), It.IsAny<DesktopUiCheckCondition>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.Never);
        Assert.Empty(_reports.Get(SessionId).Checks);
    }

    [Fact]
    public async Task InvalidConditionIsNotRecorded()
    {
        await CreateTools().CheckUiAsync(SessionId, "textEquals", automationId: "textBox");

        Assert.Empty(_reports.Get(SessionId).Checks);
    }

    [Fact]
    public async Task InvalidCriteriaAreRejectedBeforeLaunch()
    {
        var sessions = new Mock<IDesktopTestSessionService>();

        var response = await CreateTools(sessions).StartTestSessionAsync(Guid.NewGuid().ToString(), "profile", criteria: ["  "]);

        Assert.Equal("InvalidCriteria", response.Error?.Code);
        sessions.Verify(
            service => service.StartAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DesktopLaunchRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private void SetupEvaluation(DesktopUiCheckEvaluation evaluation)
    {
        _checks
            .Setup(service => service.CheckAsync(It.IsAny<DesktopProcessIdentity>(), It.IsAny<DesktopUiCheckCondition>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(evaluation);
    }

    private DesktopTestingMcpTools CreateTools(Mock<IDesktopTestSessionService>? sessions = null)
    {
        if (sessions is null)
        {
            var process = new DesktopProcessIdentity("process-1", 1, DateTimeOffset.UtcNow, "target.exe", "hash");
            sessions = new Mock<IDesktopTestSessionService>();
            sessions
                .Setup(service => service.GetAsync(SessionId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DesktopTestSessionSnapshot(
                    SessionId,
                    "profile",
                    DesktopSessionState.Active,
                    new DesktopTargetReference("target-1", "profile", 1, process, default, LaunchedByDriver: true)));
        }

        return new DesktopTestingMcpTools(
            new Mock<IDesktopActionCoordinator>().Object,
            sessions.Object,
            new Mock<IDesktopObservationService>().Object,
            new Mock<IWindowsDesktopInputService>().Object,
            new Mock<IWindowsUiAutomationActionProvider>().Object,
            _checks.Object,
            new Mock<IDesktopOcrObservationProvider>().Object,
            _reports,
            _evidence.Object,
            new DesktopTestingPolicyLoader(),
            new DesktopTestingHostOptions(Enabled: true, WorkerMode: false, PolicyPath: null, WorkerPipeName: null, ParentProcessId: null));
    }
}
