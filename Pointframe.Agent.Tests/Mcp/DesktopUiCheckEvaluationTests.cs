using Moq;
using Pointframe.Engine;
using Pointframe.Engine.Automation.Models;
using Pointframe.Engine.Automation.Services;
using Pointframe.Mcp;
using Pointframe.Mcp.Automation;
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
        var response = DesktopTestingResponseMapper.MapCheck(result, "session-1", CheckSpec);
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
    public async Task TruncatedTreeStillDecidesWitnessedChecks()
    {
        var source = SourceFor(true, Element("el-1-0", automationId: "saveButton", text: "Save"));

        var exists = await source.EvaluateAsync(Process(), new DesktopUiCheckCondition.Exists(ById("saveButton")), CancellationToken.None);
        var absent = await source.EvaluateAsync(Process(), new DesktopUiCheckCondition.Absent(ById("saveButton")), CancellationToken.None);

        Assert.Equal("passed", DesktopTestingResponseMapper.MapCheck(exists).Verification);
        Assert.Equal("failed", DesktopTestingResponseMapper.MapCheck(absent).Verification);
    }

    [Fact]
    public async Task TruncatedTreeWithAHiddenDuplicateMakesEveryUniquenessPredicateInconclusive()
    {
        var source = SourceFor(true, Element("el-1-0", automationId: "saveButton", text: "Save", toggleState: "On"));
        DesktopUiCheckCondition[] conditions =
        [
            new DesktopUiCheckCondition.TextEquals(ById("saveButton"), "Save"),
            new DesktopUiCheckCondition.TextEquals(ById("saveButton"), "Other"),
            new DesktopUiCheckCondition.Enabled(ById("saveButton"), true),
            new DesktopUiCheckCondition.Enabled(ById("saveButton"), false),
            new DesktopUiCheckCondition.ToggleEquals(ById("saveButton"), "On"),
            new DesktopUiCheckCondition.ToggleEquals(ById("saveButton"), "Off"),
            new DesktopUiCheckCondition.SelectionEquals(ById("saveButton"), "Selected"),
        ];

        foreach (var condition in conditions)
        {
            var result = await source.EvaluateAsync(Process(), condition, CancellationToken.None);

            Assert.Equal("InspectionIncomplete", result.ErrorCode);
            Assert.Equal("inconclusive", DesktopTestingResponseMapper.MapCheck(result).Verification);
        }
    }

    [Fact]
    public async Task TruncatedTreeWithAVisibleDuplicateStaysAmbiguous()
    {
        var source = SourceFor(true, Element("el-1-0", automationId: "dup", text: "a"), Element("el-1-1", automationId: "dup", text: "a"));

        var result = await source.EvaluateAsync(Process(), new DesktopUiCheckCondition.TextEquals(ById("dup"), "a"), CancellationToken.None);

        Assert.Equal(2, result.MatchCount);
        Assert.NotEqual("InspectionIncomplete", result.ErrorCode);
    }

    [Fact]
    public async Task TruncatedFoundTargetNeverPassesOrInvertsAsANegativeControl()
    {
        var source = SourceFor(true, Element("el-1-0", automationId: "saveButton", text: "Save"));
        var wrong = await source.EvaluateAsync(Process(), new DesktopUiCheckCondition.TextEquals(ById("saveButton"), "Wrong"), CancellationToken.None);
        var exists = await source.EvaluateAsync(Process(), new DesktopUiCheckCondition.Exists(ById("saveButton")), CancellationToken.None);

        var fromPredicate = DesktopTestingResponseMapper.MapNegativeControl(wrong);
        var fromExists = DesktopTestingResponseMapper.MapNegativeControl(exists);
        var direct = DesktopTestingResponseMapper.MapNegativeControl(new DesktopUiCheckEvaluation(true, false, 1, IsTruncated: true));

        Assert.Equal("inconclusive", fromPredicate.Verification);
        Assert.Equal("inconclusive", fromExists.Verification);
        Assert.Equal("inconclusive", direct.Verification);
        Assert.Equal("InspectionIncomplete", direct.Error!.Code);
    }

    [Fact]
    public void CompleteTreeNegativeControlStillInverts()
    {
        var passed = DesktopTestingResponseMapper.MapNegativeControl(new DesktopUiCheckEvaluation(true, false, 1));
        var failed = DesktopTestingResponseMapper.MapNegativeControl(new DesktopUiCheckEvaluation(true, true, 1));

        Assert.Equal("passed", passed.Verification);
        Assert.Equal("failed", failed.Verification);
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

    private static readonly DesktopCheckSpec CheckSpec = new("textEquals", "saveButton", null, null, "window-a", "Save", 5);

    [Theory]
    [InlineData("InspectionIncomplete")]
    [InlineData("ProviderUnavailable")]
    [InlineData("UiaUnavailable")]
    [InlineData("InspectionTimeout")]
    public void CheckRecoveryRetriesTheSameCheckWithItsOwnArguments(string code)
    {
        var response = DesktopTestingResponseMapper.MapCheck(new DesktopUiCheckEvaluation(false, false, 0, code), "session-1", CheckSpec);

        var next = response.NextStep!;
        Assert.Equal("diagnostic_retry", next.Kind);
        Assert.Contains("cannot change that verdict", next.Reason, StringComparison.Ordinal);
        Assert.Equal("desktop_check_ui", next.Tool);
        Assert.Equal("session-1", next.Arguments!["sessionId"]);
        Assert.Equal("textEquals", next.Arguments["kind"]);
        Assert.Equal("saveButton", next.Arguments["automationId"]);
        Assert.Equal("Save", next.Arguments["expected"]);
        Assert.False(next.Arguments.ContainsKey("expectFailure"));
        AssertCallable(next);
    }

    [Fact]
    public void NegativeControlRecoveryKeepsExpectFailure()
    {
        var response = DesktopTestingResponseMapper.MapNegativeControl(
            new DesktopUiCheckEvaluation(true, false, 1, IsTruncated: true), "session-1", CheckSpec);

        Assert.Equal(true, response.NextStep!.Arguments!["expectFailure"]);
        AssertCallable(response.NextStep);
    }

    [Theory]
    [InlineData("ProfileNotFound")]
    [InlineData("UnknownProfile")]
    public void UnknownProfileRecoveryListsTheApprovedApps(string code)
    {
        var action = DesktopTestingResponseMapper.MapAction(new DesktopActionResult(
            DesktopTestingLimits.SchemaVersion,
            Guid.NewGuid().ToString(),
            DesktopOperationStatus.Completed,
            DesktopDispatchStatus.NotStarted,
            DesktopVerificationStatus.Inconclusive,
            DesktopObservationStatus.NotRequested,
            new DesktopOperationError(code, "x")));

        Assert.Equal("desktop_list_apps", action.NextStep!.Tool);
        Assert.True(Guid.TryParse((string?)action.NextStep.Arguments!["actionId"], out _));
        AssertCallable(action.NextStep);
    }

    [Fact]
    public void DiagnosticRetryKeepsTheCriterionId()
    {
        var response = DesktopTestingResponseMapper.MapCheck(
            new DesktopUiCheckEvaluation(false, false, 0, "InspectionIncomplete"), "session-1", CheckSpec, criterionId: "C1");

        Assert.Equal("diagnostic_retry", response.NextStep!.Kind);
        Assert.Equal("C1", response.NextStep.Arguments!["criterionId"]);
        AssertCallable(response.NextStep);
    }

    [Theory]
    [InlineData("InspectionIncomplete")]
    [InlineData("ProviderUnavailable")]
    public void ReportBearingSessionRecoveryEndsTheSessionAndStartsAFreshOne(string code)
    {
        var response = DesktopTestingResponseMapper.MapCheck(
            new DesktopUiCheckEvaluation(false, false, 0, code), "session-1", CheckSpec, criterionId: "C1", reportBearing: true);

        var next = response.NextStep!;
        Assert.Equal("fresh_session", next.Kind);
        Assert.Equal("desktop_get_test_report", next.Tool);
        Assert.Equal("session-1", next.Arguments!["sessionId"]);
        Assert.Contains("desktop_end_test_session", next.Reason, StringComparison.Ordinal);
        Assert.Contains("save", next.Reason, StringComparison.Ordinal);
        Assert.Contains("desktop_start_test_session", next.Reason, StringComparison.Ordinal);
        Assert.Contains("same acceptance criteria", next.Reason, StringComparison.Ordinal);
        Assert.Contains("C1", next.Reason, StringComparison.Ordinal);
        AssertCallable(next);
    }

    [Theory]
    [InlineData("InspectionIncomplete", false)]
    [InlineData("InspectionIncomplete", true)]
    [InlineData("ProviderUnavailable", false)]
    [InlineData("ProviderUnavailable", true)]
    [InlineData("UiaUnavailable", true)]
    [InlineData("InspectionTimeout", true)]
    public void NoRecoveryHintForAnInconclusiveCheckMakesADestructiveToolTheExecutableStep(string code, bool reportBearing)
    {
        var tools = typeof(DesktopTestingResponseMapper).Assembly.GetType("Pointframe.Mcp.DesktopTestingMcpTools")!;
        var destructive = tools.GetMethods()
            .Select(method => method.GetCustomAttributes(typeof(ModelContextProtocol.Server.McpServerToolAttribute), false)
                .Cast<ModelContextProtocol.Server.McpServerToolAttribute>().SingleOrDefault())
            .Where(attribute => attribute is { Destructive: true })
            .Select(attribute => attribute!.Name)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("desktop_end_test_session", destructive);

        foreach (var negativeControl in new[] { false, true })
        {
            var next = DesktopTestingResponseMapper.RecoveryFor(code, "session-1", CheckSpec, negativeControl, "C1", reportBearing)!;

            Assert.True(next.Arguments is null || !destructive.Contains(next.Tool), $"{next.Tool} is destructive.");
        }
    }

    [Fact]
    public void ReportBearingNegativeControlRecoveryAlsoNeedsAFreshSession()
    {
        var response = DesktopTestingResponseMapper.MapNegativeControl(
            new DesktopUiCheckEvaluation(true, false, 1, IsTruncated: true), "session-1", CheckSpec, reportBearing: true);

        Assert.Equal("fresh_session", response.NextStep!.Kind);
    }

    [Theory]
    [InlineData("TargetStillRunning", "desktop_press_keys", "processExited")]
    [InlineData("StaleObservation", "desktop_observe_app", "captureBoundsPixels")]
    [InlineData("StaleObservationRef", "desktop_observe_app", "captureBoundsPixels")]
    [InlineData("ElementNotFound", "desktop_observe_app", "includeUiAutomation")]
    public void HintsThatNeedAgentInputAreGuidanceOnly(string code, string tool, string mention)
    {
        var action = DesktopTestingResponseMapper.MapAction(new DesktopActionResult(
            DesktopTestingLimits.SchemaVersion,
            Guid.NewGuid().ToString(),
            DesktopOperationStatus.Completed,
            DesktopDispatchStatus.NotStarted,
            DesktopVerificationStatus.Inconclusive,
            DesktopObservationStatus.NotRequested,
            new DesktopOperationError(code, "x")), "session-1");

        var next = action.NextStep!;
        Assert.Equal(tool, next.Tool);
        Assert.Null(next.Arguments);
        Assert.False(string.IsNullOrWhiteSpace(next.Reason));
        Assert.Contains(code == "TargetStillRunning" ? mention : mention, next.Reason, StringComparison.Ordinal);
        if (code == "TargetStillRunning")
        {
            Assert.Contains("Alt+F4", next.Reason, StringComparison.Ordinal);
            Assert.Contains("desktop_restart_app", next.Reason, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("session-1", next.Reason, StringComparison.Ordinal);
        }

        AssertCallable(next);
    }

    [Fact]
    public async Task TruncatedTreeWindowChecksNeedExactlyOneMatchSoOneVisibleRootIsInconclusive()
    {
        var service = new DesktopUiCheckService(SourceFor(true, Element("el-1-0", automationId: "root")));

        var exists = await service.CheckAsync(Process(), new DesktopUiCheckCondition.WindowExists("window-a"), TimeSpan.FromSeconds(1));
        var absent = await service.CheckAsync(Process(), new DesktopUiCheckCondition.WindowAbsent("window-a"), TimeSpan.FromSeconds(1));

        Assert.Equal("InspectionIncomplete", exists.ErrorCode);
        Assert.False(exists.Matches);
        Assert.Equal("inconclusive", DesktopTestingResponseMapper.MapCheck(exists).Verification);
        Assert.Equal("InspectionIncomplete", absent.ErrorCode);
    }

    [Fact]
    public async Task TruncatedTreeWindowChecksWithTwoVisibleMatchesAreAmbiguousAsOnACompleteTree()
    {
        var elements = new[] { Element("el-1-0", automationId: "a"), Element("el-1-1", automationId: "b") };
        var truncated = new DesktopUiCheckService(SourceFor(true, elements));
        var complete = new DesktopUiCheckService(SourceFor(false, elements));

        var fromTruncated = await truncated.CheckAsync(Process(), new DesktopUiCheckCondition.WindowExists("window-a"), TimeSpan.FromSeconds(1));
        var fromComplete = await complete.CheckAsync(Process(), new DesktopUiCheckCondition.WindowExists("window-a"), TimeSpan.FromSeconds(1));

        Assert.Equal("AmbiguousTarget", fromTruncated.ErrorCode);
        Assert.Equal("AmbiguousTarget", fromComplete.ErrorCode);
    }

    [Fact]
    public async Task CompleteTreeWindowExistsStillPassesWithOneMatch()
    {
        var service = new DesktopUiCheckService(SourceFor(false, Element("el-1-0", automationId: "root")));

        var result = await service.CheckAsync(Process(), new DesktopUiCheckCondition.WindowExists("window-a"), TimeSpan.FromSeconds(1));

        Assert.True(result.Matches);
    }

    [Fact]
    public void CheckRecoveryWithoutASessionIsOmitted()
    {
        Assert.Null(DesktopTestingResponseMapper.MapCheck(new DesktopUiCheckEvaluation(false, false, 0, "InspectionIncomplete")).NextStep);
    }

    private static void AssertCallable(DesktopNextStep step)
    {
        if (step.Arguments is null)
        {
            Assert.False(string.IsNullOrWhiteSpace(step.Reason));
            return;
        }

        Assert.NotNull(step.Tool);
        var tools = typeof(DesktopTestingResponseMapper).Assembly.GetType("Pointframe.Mcp.DesktopTestingMcpTools")!;
        var method = tools.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Single(candidate => candidate.GetCustomAttributes(typeof(ModelContextProtocol.Server.McpServerToolAttribute), false)
                .Cast<ModelContextProtocol.Server.McpServerToolAttribute>().Any(attribute => attribute.Name == step.Tool));
        var parameters = method.GetParameters().Where(parameter => parameter.ParameterType != typeof(CancellationToken)).ToArray();
        var names = parameters.Select(parameter => parameter.Name!).ToHashSet(StringComparer.Ordinal);
        foreach (var required in parameters.Where(parameter => !parameter.HasDefaultValue))
        {
            Assert.True(step.Arguments.ContainsKey(required.Name!), $"{step.Tool} requires '{required.Name}'.");
        }

        foreach (var key in step.Arguments.Keys)
        {
            Assert.Contains(key, names);
        }
    }

    [Fact]
    public void ALostActionResponseCanBeReadBackWithTheEchoedActionId()
    {
        var actionId = Guid.NewGuid().ToString();
        var stored = new DesktopActionResult(
            DesktopTestingLimits.SchemaVersion,
            actionId,
            DesktopOperationStatus.Completed,
            DesktopDispatchStatus.Complete,
            DesktopVerificationStatus.NotRequested,
            DesktopObservationStatus.NotRequested);
        var coordinator = new Mock<IDesktopActionCoordinator>();
        coordinator.Setup(item => item.GetResult(actionId)).Returns(stored);

        var response = DesktopTestingResponseMapper.MapAction(coordinator.Object.GetResult(actionId));

        Assert.Equal(actionId, response.ActionId);
        Assert.Equal("Complete", response.Dispatch);
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
