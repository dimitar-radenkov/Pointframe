using Pointframe.Engine;
using Pointframe.Engine.Automation.Models;
using Pointframe.Mcp;
using Pointframe.Mcp.Automation;
using Pointframe.Engine.Automation.Services;
using Moq;
using Xunit;

namespace Pointframe.Tests.Mcp;

public class DesktopUiCheckEvaluationTests
{
    private static WorkerUiCheckSource SourceFor(bool truncated, params DesktopUiElementSnapshot[] elements)
    {
        var observations = new Mock<IDesktopUiObservationProvider>();
        observations.Setup(provider => provider.Inspect(It.IsAny<DesktopObservationRequest>()))
            .Returns(new DesktopUiSnapshot(DesktopUiAutomationStatus.Available, elements, DateTimeOffset.UtcNow, IsTruncated: truncated));
        return new WorkerUiCheckSource(observations.Object, new Mock<IDesktopProcessController>().Object);
    }

    private static DesktopProcessIdentity Process() => new("process-1", 1, DateTimeOffset.UtcNow, "app.exe", "hash");

    [Fact]
    public async Task TruncatedTreeWithMissingTargetIsInconclusiveWithRecoveryHint()
    {
        var result = await SourceFor(true).EvaluateAsync(Process(), new DesktopUiCheckCondition.Exists(ById("missing")), CancellationToken.None);

        Assert.False(result.StateAvailable);
        Assert.Equal("InspectionIncomplete", result.ErrorCode);
        var response = DesktopTestingResponseMapper.MapCheck(result);
        Assert.Equal("inconclusive", response.Verification);
        Assert.Equal("InspectionIncomplete", response.Error!.Code);
        Assert.NotNull(response.NextStep);
        Assert.Equal("desktop_check_ui", response.NextStep!.Tool);
    }

    [Fact]
    public async Task TruncatedTreeWithApparentAbsenceIsInconclusive()
    {
        var absent = await SourceFor(true).EvaluateAsync(Process(), new DesktopUiCheckCondition.Absent(ById("missing")), CancellationToken.None);
        var windowAbsent = await SourceFor(true).EvaluateAsync(Process(), new DesktopUiCheckCondition.WindowAbsent("window-z"), CancellationToken.None);

        Assert.Equal("inconclusive", DesktopTestingResponseMapper.MapCheck(absent).Verification);
        Assert.Equal("InspectionIncomplete", absent.ErrorCode);
        Assert.Equal("inconclusive", DesktopTestingResponseMapper.MapCheck(windowAbsent).Verification);
    }

    [Fact]
    public async Task TruncatedTreeWithUnseenTargetOfAStatePredicateIsInconclusive()
    {
        var source = SourceFor(true, Element("el-1-0", automationId: "other"));

        var text = await source.EvaluateAsync(Process(), new DesktopUiCheckCondition.TextEquals(ById("saveButton"), "Save"), CancellationToken.None);
        var enabled = await source.EvaluateAsync(Process(), new DesktopUiCheckCondition.Enabled(ById("saveButton"), true), CancellationToken.None);

        Assert.Equal("InspectionIncomplete", text.ErrorCode);
        Assert.Equal("InspectionIncomplete", enabled.ErrorCode);
        Assert.False(text.StateAvailable);
    }

    [Fact]
    public async Task TruncatedTreeNeverTurnsIntoAPassingNegativeControl()
    {
        var result = await SourceFor(true).EvaluateAsync(Process(), new DesktopUiCheckCondition.Exists(ById("missing")), CancellationToken.None);

        var response = DesktopTestingResponseMapper.MapNegativeControl(result);

        Assert.Equal("inconclusive", response.Verification);
    }

    [Fact]
    public async Task TruncatedTreeStillDecidesWhenTheTargetWasFound()
    {
        var source = SourceFor(true, Element("el-1-0", automationId: "saveButton", text: "Save"));

        var exists = await source.EvaluateAsync(Process(), new DesktopUiCheckCondition.Exists(ById("saveButton")), CancellationToken.None);
        var mismatch = await source.EvaluateAsync(Process(), new DesktopUiCheckCondition.TextEquals(ById("saveButton"), "Other"), CancellationToken.None);
        var absent = await source.EvaluateAsync(Process(), new DesktopUiCheckCondition.Absent(ById("saveButton")), CancellationToken.None);

        Assert.Equal("passed", DesktopTestingResponseMapper.MapCheck(exists).Verification);
        Assert.Equal("failed", DesktopTestingResponseMapper.MapCheck(mismatch).Verification);
        Assert.Equal("failed", DesktopTestingResponseMapper.MapCheck(absent).Verification);
    }

    [Fact]
    public async Task CompleteTreeKeepsItsVerdicts()
    {
        var source = SourceFor(false, Element("el-1-0", automationId: "saveButton", text: "Save"));

        var found = await source.EvaluateAsync(Process(), new DesktopUiCheckCondition.TextEquals(ById("saveButton"), "Save"), CancellationToken.None);
        var mismatch = await source.EvaluateAsync(Process(), new DesktopUiCheckCondition.TextEquals(ById("saveButton"), "Other"), CancellationToken.None);
        var missing = await source.EvaluateAsync(Process(), new DesktopUiCheckCondition.Exists(ById("missing")), CancellationToken.None);
        var absent = await source.EvaluateAsync(Process(), new DesktopUiCheckCondition.Absent(ById("missing")), CancellationToken.None);

        Assert.Equal("passed", DesktopTestingResponseMapper.MapCheck(found).Verification);
        Assert.Equal("failed", DesktopTestingResponseMapper.MapCheck(mismatch).Verification);
        Assert.Equal("failed", DesktopTestingResponseMapper.MapCheck(missing).Verification);
        Assert.Equal("passed", DesktopTestingResponseMapper.MapCheck(absent).Verification);
        Assert.Null(DesktopTestingResponseMapper.MapCheck(missing).NextStep);
    }

    [Theory]
    [InlineData("ProviderUnavailable", "desktop_observe_app")]
    [InlineData("InspectionTimeout", "desktop_observe_app")]
    [InlineData("ProfileNotFound", "desktop_list_apps")]
    [InlineData("UnknownProfile", "desktop_list_apps")]
    [InlineData("StaleObservation", "desktop_observe_app")]
    [InlineData("StaleObservationRef", "desktop_observe_app")]
    [InlineData("ElementNotFound", "desktop_observe_app")]
    [InlineData("TargetStillRunning", "desktop_check_ui")]
    [InlineData("InspectionIncomplete", "desktop_check_ui")]
    public void KnownErrorCodesCarryARecoveryNextStep(string code, string tool)
    {
        var check = DesktopTestingResponseMapper.MapCheck(new DesktopUiCheckEvaluation(false, false, 0, code));
        var action = DesktopTestingResponseMapper.MapAction(new DesktopActionResult(
            DesktopTestingLimits.SchemaVersion,
            Guid.NewGuid().ToString(),
            DesktopOperationStatus.Completed,
            DesktopDispatchStatus.NotStarted,
            DesktopVerificationStatus.Inconclusive,
            DesktopObservationStatus.NotRequested,
            new DesktopOperationError(code, "x")));

        Assert.Equal(tool, check.NextStep!.Tool);
        Assert.Equal(tool, action.NextStep!.Tool);
    }

    [Fact]
    public void UnknownErrorCodesAddNoNextStep()
    {
        var check = DesktopTestingResponseMapper.MapCheck(new DesktopUiCheckEvaluation(false, false, 0, "SomethingNew"));

        Assert.Null(check.NextStep);
    }

    [Fact]
    public void ActionResponsesEchoTheCallersActionId()
    {
        var actionId = Guid.NewGuid().ToString();
        var action = DesktopTestingResponseMapper.MapAction(new DesktopActionResult(
            DesktopTestingLimits.SchemaVersion,
            actionId,
            DesktopOperationStatus.Completed,
            DesktopDispatchStatus.Complete,
            DesktopVerificationStatus.NotRequested,
            DesktopObservationStatus.NotRequested));

        Assert.Equal(actionId, action.ActionId);
        Assert.Null(action.NextStep);
    }

    [Fact]
    public void ExistsMatchesAnElementByAutomationId()
    {
        var result = WorkerUiCheckSource.Evaluate(
            [Element("el-1-0", automationId: "saveButton")],
            new DesktopUiCheckCondition.Exists(ById("saveButton")));

        Assert.True(result.StateAvailable);
        Assert.True(result.Matches);
        Assert.Equal(1, result.MatchCount);
    }

    [Fact]
    public void AbsentMatchesWhenNothingIsFound()
    {
        var result = WorkerUiCheckSource.Evaluate(
            [Element("el-1-0", automationId: "saveButton")],
            new DesktopUiCheckCondition.Absent(ById("deleteButton")));

        Assert.True(result.Matches);
        Assert.Equal(0, result.MatchCount);
    }

    [Fact]
    public void TextEqualsComparesTheElementValue()
    {
        var result = WorkerUiCheckSource.Evaluate(
            [Element("el-1-0", automationId: "search", text: "hello from agent")],
            new DesktopUiCheckCondition.TextEquals(ById("search"), "hello from agent"));

        Assert.True(result.Matches);
    }

    [Fact]
    public void TextEqualsNeverMatchesASensitiveField()
    {
        // A password field reports no text by design. Treating that as a mismatch would be merely
        // wrong; treating it as a match would assert something that was never read.
        var result = WorkerUiCheckSource.Evaluate(
            [Element("el-1-0", automationId: "password", text: null, isSensitive: true)],
            new DesktopUiCheckCondition.TextEquals(ById("password"), string.Empty));

        Assert.False(result.Matches);
    }

    [Fact]
    public void ToggleEqualsIgnoresCase()
    {
        var result = WorkerUiCheckSource.Evaluate(
            [Element("el-1-0", automationId: "check", toggleState: "On")],
            new DesktopUiCheckCondition.ToggleEquals(ById("check"), "on"));

        Assert.True(result.Matches);
    }

    [Theory]
    [InlineData("true", "On")]
    [InlineData("checked", "On")]
    [InlineData("on", "On")]
    [InlineData("false", "Off")]
    [InlineData("unchecked", "Off")]
    [InlineData("indeterminate", "Indeterminate")]
    public void BuildConditionNormalizesToggleValues(string expected, string normalized)
    {
        var condition = DesktopTestingMcpTools.BuildCondition(
            new McpUiCheckRequest("toggleEquals", AutomationId: "check", Expected: expected));

        Assert.Equal(normalized, Assert.IsType<DesktopUiCheckCondition.ToggleEquals>(condition).Expected);
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("yes")]
    [InlineData("")]
    public void BuildConditionRejectsUnknownToggleValues(string expected)
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            DesktopTestingMcpTools.BuildCondition(
                new McpUiCheckRequest("toggleEquals", AutomationId: "check", Expected: expected)));

        Assert.Contains("indeterminate", exception.Message);
    }

    [Theory]
    [InlineData("selected", "Selected")]
    [InlineData("notSelected", "NotSelected")]
    public void BuildConditionNormalizesSelectionValues(string expected, string normalized)
    {
        var condition = DesktopTestingMcpTools.BuildCondition(
            new McpUiCheckRequest("selectionEquals", AutomationId: "item", Expected: expected));

        Assert.Equal(normalized, Assert.IsType<DesktopUiCheckCondition.SelectionEquals>(condition).Expected);
    }

    [Fact]
    public void BuildConditionRejectsUnknownSelectionValue()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            DesktopTestingMcpTools.BuildCondition(
                new McpUiCheckRequest("selectionEquals", AutomationId: "item", Expected: "true")));

        Assert.Contains("selected or notSelected", exception.Message);
    }

    [Fact]
    public void FailedStateCheckReturnsActualValue()
    {
        var result = WorkerUiCheckSource.Evaluate(
            [Element("el-1-0", automationId: "check", toggleState: "On")],
            new DesktopUiCheckCondition.ToggleEquals(ById("check"), "Off"));

        Assert.False(result.Matches);
        Assert.Equal("On", result.ActualValue);
    }

    [Fact]
    public void SensitiveTextIsNeverReturnedAsActualValue()
    {
        var result = WorkerUiCheckSource.Evaluate(
            [Element("el-1-0", automationId: "password", text: "secret", isSensitive: true)],
            new DesktopUiCheckCondition.TextEquals(ById("password"), "expected"));

        Assert.Null(result.ActualValue);
    }

    [Fact]
    public void EnabledReportsTheRealMatchCountWhenAmbiguous()
    {
        // The service rejects a count other than one for state predicates, so the count has to be
        // reported honestly rather than collapsed to the first hit.
        var result = WorkerUiCheckSource.Evaluate(
            [
                Element("el-1-0", automationId: "row"),
                Element("el-1-1", automationId: "row"),
            ],
            new DesktopUiCheckCondition.Enabled(ById("row")));

        Assert.Equal(2, result.MatchCount);
        Assert.False(result.Matches);
    }

    [Fact]
    public void LocatorScopesToAWindowWhenGiven()
    {
        var result = WorkerUiCheckSource.Evaluate(
            [
                Element("el-1-0", automationId: "ok", windowRef: "window-a"),
                Element("el-1-1", automationId: "ok", windowRef: "window-b"),
            ],
            new DesktopUiCheckCondition.Exists(
                new DesktopLocator(DesktopLocatorKind.AutomationId, AutomationId: "ok", WindowRef: "window-b")));

        Assert.Equal(1, result.MatchCount);
    }

    [Theory]
    [InlineData("exists")]
    [InlineData("absent")]
    [InlineData("enabled")]
    public void BuildConditionAcceptsEveryLocatorKind(string kind)
    {
        var condition = DesktopTestingMcpTools.BuildCondition(new McpUiCheckRequest(kind, AutomationId: "save"));

        Assert.NotNull(condition);
    }

    [Fact]
    public void BuildConditionRejectsAnUnknownKind()
    {
        Assert.Throws<ArgumentException>(() =>
            DesktopTestingMcpTools.BuildCondition(new McpUiCheckRequest("teleport", AutomationId: "save")));
    }

    [Fact]
    public void BuildConditionRequiresAnExpectedValueWhereItIsMeaningful()
    {
        Assert.Throws<ArgumentException>(() =>
            DesktopTestingMcpTools.BuildCondition(new McpUiCheckRequest("textEquals", AutomationId: "search")));
    }

    [Fact]
    public void BuildConditionRequiresAWindowReferenceForWindowChecks()
    {
        Assert.Throws<ArgumentException>(() =>
            DesktopTestingMcpTools.BuildCondition(new McpUiCheckRequest("windowExists")));
    }

    [Fact]
    public void BuildConditionTreatsEnabledFalseAsExpectingDisabled()
    {
        var condition = DesktopTestingMcpTools.BuildCondition(
            new McpUiCheckRequest("enabled", AutomationId: "save", Expected: "false"));

        Assert.False(Assert.IsType<DesktopUiCheckCondition.Enabled>(condition).Expected);
    }

    private static DesktopLocator ById(string automationId) =>
        new(DesktopLocatorKind.AutomationId, AutomationId: automationId);

    private static DesktopUiElementSnapshot Element(
        string elementRef,
        string? automationId = null,
        string windowRef = "window-a",
        string? text = null,
        string? toggleState = null,
        bool isSensitive = false) =>
        new(
            elementRef,
            windowRef,
            "Button",
            "Save",
            automationId,
            new PixelBounds(0, 0, 10, 10),
            IsEnabled: true,
            ToggleState: toggleState,
            Selection: null,
            Text: text,
            IsSensitive: isSensitive);

    [Fact]
    public void WindowHandleRoundTripsFromTheWindowRef()
    {
        var handle = DesktopTestingMcpTools.ResolveWindowHandle("window-process-1-1A2B3C", "process-1");

        Assert.Equal(new nint(0x1A2B3C), handle);
    }

    [Fact]
    public void WindowHandleIsRejectedWhenTheRefNamesAnotherProcess()
    {
        // The ref carries the process it belongs to, so a ref captured against one target cannot be
        // replayed to focus a window of a different one.
        var handle = DesktopTestingMcpTools.ResolveWindowHandle("window-process-2-1A2B3C", "process-1");

        Assert.Equal(nint.Zero, handle);
    }

    [Theory]
    [InlineData("")]
    [InlineData("window-process-1")]
    [InlineData("window-process-1-")]
    [InlineData("window-process-1-nothex")]
    [InlineData("window-process-1-0")]
    public void WindowHandleIsRejectedWhenTheRefIsNotUsable(string windowRef)
    {
        Assert.Equal(nint.Zero, DesktopTestingMcpTools.ResolveWindowHandle(windowRef, "process-1"));
    }
}
