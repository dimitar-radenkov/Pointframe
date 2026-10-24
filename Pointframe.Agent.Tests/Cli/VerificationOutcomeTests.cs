using System.ComponentModel;
using Moq;
using Pointframe.Cli;
using Pointframe.Telemetry;
using Xunit;

namespace Pointframe.Tests.Cli;

public sealed class VerificationOutcomeTests : IDisposable
{
    private readonly VerificationFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task Run_InvalidSpec_IsAnInvalidRequestWithExitCodeTwo()
    {
        var specPath = _fixture.WriteSpec("""{ "id": "s", "steps": [] }""");
        var application = new VerificationFixture.Services().Application(_fixture.Store, new StringWriter());

        var exitCode = await application.RunAsync(Run(specPath), CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Equal(TelemetryOutcome.InvalidRequest, application.Outcome);
    }

    [Fact]
    public async Task Run_UnknownScenarioId_IsAnInvalidRequestWithExitCodeTwo()
    {
        var specPath = _fixture.WriteSpec(VerificationFixture.ValidScenario);
        var application = new VerificationFixture.Services().Application(_fixture.Store, new StringWriter());

        var exitCode = await application.RunAsync(Run(specPath) with { ScenarioId = "missing" }, CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Equal(TelemetryOutcome.InvalidRequest, application.Outcome);
    }

    [Fact]
    public async Task Run_FailedGate_IsAVerificationFailureWithExitCodeOne()
    {
        var specPath = _fixture.WriteSpecWith(VerificationFixture.DefaultApp, """[ { "id": "build", "run": "dotnet build" } ]""", VerificationFixture.ValidScenario);
        var services = new VerificationFixture.Services();
        services.Commands.Setup(item => item.RunAsync("dotnet build", It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommandResult(1, false, ["A.cs(1,1): error CS1002: ; expected"]));
        var application = services.Application(_fixture.Store, new StringWriter());

        var exitCode = await application.RunAsync(Run(specPath), CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Equal(TelemetryOutcome.VerificationFailed, application.Outcome);
    }

    [Fact]
    public async Task Run_GateThatCannotStart_IsLeftAsAnError()
    {
        var specPath = _fixture.WriteSpecWith(VerificationFixture.DefaultApp, """[ { "id": "build", "run": "dotnet build" } ]""", VerificationFixture.ValidScenario);
        var services = new VerificationFixture.Services();
        services.Commands.Setup(item => item.RunAsync("dotnet build", It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Win32Exception("The system cannot find the file specified."));
        var application = services.Application(_fixture.Store, new StringWriter());

        var exitCode = await application.RunAsync(Run(specPath), CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Null(application.Outcome);
    }

    [Fact]
    public async Task Run_ScenarioThatFailsItsCriterion_IsAVerificationFailureWithExitCodeOne()
    {
        var specPath = _fixture.WriteSpec(VerificationFixture.ValidScenario);
        _fixture.CreateAppAndMcp();
        var services = new VerificationFixture.Services(new FakeMcp(await _fixture.WriteSealedBundleAsync(criterionPassed: false)));
        var application = services.Application(_fixture.Store, new StringWriter());

        var exitCode = await application.RunAsync(Run(specPath), CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Equal("fail", _fixture.ReadVerdict().GetProperty("status").GetString());
        Assert.Equal(TelemetryOutcome.VerificationFailed, application.Outcome);
    }

    [Fact]
    public async Task Run_ProviderExceptionSurfacingAsAFailedScenario_IsLeftAsAnError()
    {
        var specPath = _fixture.WriteSpec(VerificationFixture.ValidScenario);
        _fixture.CreateAppAndMcp();
        var services = new VerificationFixture.Services();
        services.Factory.Setup(item => item.LaunchAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("pipe closed"));
        var application = services.Application(_fixture.Store, new StringWriter());

        var exitCode = await application.RunAsync(Run(specPath), CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Equal("fail", _fixture.ReadVerdict().GetProperty("status").GetString());
        Assert.Null(application.Outcome);
    }

    [Fact]
    public async Task Run_InfrastructureFailureInsideTheRunner_IsLeftAsAnError()
    {
        var specPath = _fixture.WriteSpec(VerificationFixture.ValidScenario);
        _fixture.CreateAppAndMcp();

        foreach (var broken in new[] { "desktop_enter_text", "desktop_check_ui", "desktop_get_test_report", "desktop_end_test_session" })
        {
            var fake = new FakeMcp(await _fixture.WriteSealedBundleAsync(criterionPassed: true));
            fake.Mock
                .Setup(item => item.CallToolAsync(broken, It.IsAny<object>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new IOException("pipe closed"));
            var application = new VerificationFixture.Services(fake).Application(_fixture.Store, new StringWriter());

            var exitCode = await application.RunAsync(Run(specPath), CancellationToken.None);

            Assert.Equal(1, exitCode);
            Assert.Equal("fail", _fixture.ReadVerdict().GetProperty("status").GetString());
            Assert.True(application.Outcome is null, broken);
        }
    }

    [Fact]
    public async Task Run_SessionThatDoesNotStart_IsLeftAsAnError()
    {
        var specPath = _fixture.WriteSpec(VerificationFixture.ValidScenario);
        _fixture.CreateAppAndMcp();
        var fake = new FakeMcp(await _fixture.WriteSealedBundleAsync(criterionPassed: true))
        {
            StartResponse = new { operationStatus = "Completed", dispatch = "NotStarted", error = new { code = "LaunchFailed", message = "x" } },
        };
        var application = new VerificationFixture.Services(fake).Application(_fixture.Store, new StringWriter());

        var exitCode = await application.RunAsync(Run(specPath), CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Equal("fail", _fixture.ReadVerdict().GetProperty("status").GetString());
        Assert.Null(application.Outcome);
    }

    [Fact]
    public async Task Run_CleanupErrorResponse_IsLeftAsAnError()
    {
        var specPath = _fixture.WriteSpec(VerificationFixture.ValidScenario);
        _fixture.CreateAppAndMcp();
        var fake = new FakeMcp(await _fixture.WriteSealedBundleAsync(criterionPassed: true))
        {
            EndResponse = new { operationStatus = "Completed", dispatch = "NotStarted", error = new { code = "SessionNotFound", message = "gone" } },
        };
        var application = new VerificationFixture.Services(fake).Application(_fixture.Store, new StringWriter());

        var exitCode = await application.RunAsync(Run(specPath), CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Null(application.Outcome);
    }

    [Theory]
    [InlineData("ProviderUnavailable")]
    [InlineData("UiaUnavailable")]
    [InlineData("InputDispatchFailed")]
    [InlineData("QueueFull")]
    [InlineData("DispatchTimeout")]
    [InlineData("DispatchFailed")]
    [InlineData("FocusRequired")]
    [InlineData("SomethingNew")]
    public async Task Run_StructuredNonVerificationErrorOnAStep_IsLeftAsAnError(string code)
    {
        var specPath = _fixture.WriteSpec(VerificationFixture.ValidScenario);
        _fixture.CreateAppAndMcp();
        var fake = new FakeMcp(await _fixture.WriteSealedBundleAsync(criterionPassed: false))
        {
            CheckResponse = _ => new { verification = "inconclusive", stateAvailable = false, matches = false, matchCount = 0, error = new { code, message = "x" } },
        };
        var application = new VerificationFixture.Services(fake).Application(_fixture.Store, new StringWriter());

        var exitCode = await application.RunAsync(Run(specPath), CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Equal("fail", _fixture.ReadVerdict().GetProperty("status").GetString());
        Assert.Null(application.Outcome);
    }

    [Fact]
    public async Task Run_StructuredElementNotFound_RemainsAVerificationFailure()
    {
        var specPath = _fixture.WriteSpec(VerificationFixture.ValidScenario);
        _fixture.CreateAppAndMcp();
        var fake = new FakeMcp(await _fixture.WriteSealedBundleAsync(criterionPassed: false))
        {
            CheckResponse = _ => new { verification = "inconclusive", stateAvailable = true, matches = false, matchCount = 0, error = new { code = "ElementNotFound", message = "x" } },
        };
        var application = new VerificationFixture.Services(fake).Application(_fixture.Store, new StringWriter());

        var exitCode = await application.RunAsync(Run(specPath), CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Equal(TelemetryOutcome.VerificationFailed, application.Outcome);
    }

    [Fact]
    public async Task Runner_RepeatedProviderUnavailableObservations_BecomeElementNotFoundButStayInfrastructure()
    {
        var fake = new FakeMcp(await _fixture.WriteSealedBundleAsync(criterionPassed: true))
        {
            ObserveResponse = call => call == 1 ? null! : new { error = new { code = "ProviderUnavailable", message = "uia down" } },
        };

        var result = await FastRunner(fake).RunAsync(Scenario("""[ { "invoke": { "automationId": "saveButton" } } ]"""), CancellationToken.None);

        Assert.Equal("ElementNotFound", result.Steps.Single(step => step.Kind == "invoke").Code);
        Assert.True(result.InfrastructureFailure);
    }

    [Fact]
    public async Task Runner_ObservationsThatSucceedButLackTheElement_AreNotInfrastructure()
    {
        var fake = new FakeMcp(await _fixture.WriteSealedBundleAsync(criterionPassed: true));

        var result = await FastRunner(fake).RunAsync(Scenario("""[ { "invoke": { "automationId": "missing" } } ]"""), CancellationToken.None);

        Assert.Equal("ElementNotFound", result.Steps.Single(step => step.Kind == "invoke").Code);
        Assert.False(result.InfrastructureFailure);
    }

    [Fact]
    public async Task Runner_ProviderFailureDuringRestartReadiness_BecomesNoVisibleWindowButStaysInfrastructure()
    {
        var fake = new FakeMcp(await _fixture.WriteSealedBundleAsync(criterionPassed: true))
        {
            ObserveResponse = call => call <= 2 ? null! : new { error = new { code = "ProviderUnavailable", message = "uia down" } },
        };

        var result = await FastRunner(fake).RunAsync(
            Scenario("""[ { "invoke": { "automationId": "saveButton" } }, { "restart": {} } ]"""), CancellationToken.None);

        Assert.Equal("NoVisibleWindow", result.Steps.Single(step => step.Kind == "restart").Code);
        Assert.True(result.InfrastructureFailure);
    }

    [Fact]
    public async Task Runner_InfrastructureObservationFollowedByASuccessfulOneWithoutTheElement_IsAVerificationResult()
    {
        var fake = new FakeMcp(await _fixture.WriteSealedBundleAsync(criterionPassed: true))
        {
            ObserveResponse = call => call == 2 ? new { error = new { code = "ProviderUnavailable", message = "blip" } } : null!,
        };

        var result = await FastRunner(fake).RunAsync(Scenario("""[ { "invoke": { "automationId": "missing" } } ]"""), CancellationToken.None);

        Assert.Equal("ElementNotFound", result.Steps.Single(step => step.Kind == "invoke").Code);
        Assert.False(result.InfrastructureFailure);
    }

    [Fact]
    public async Task Runner_SuccessfulObservationFollowedByAnInfrastructureOne_IsInfrastructure()
    {
        var fake = new FakeMcp(await _fixture.WriteSealedBundleAsync(criterionPassed: true))
        {
            ObserveResponse = call => call <= 2 ? null! : new { error = new { code = "ProviderUnavailable", message = "down" } },
        };

        var result = await FastRunner(fake).RunAsync(Scenario("""[ { "invoke": { "automationId": "missing" } } ]"""), CancellationToken.None);

        Assert.Equal("ElementNotFound", result.Steps.Single(step => step.Kind == "invoke").Code);
        Assert.True(result.InfrastructureFailure);
    }

    [Fact]
    public async Task Runner_ToolErrorOnAnAction_IsInfrastructure()
    {
        var fake = new FakeMcp(await _fixture.WriteSealedBundleAsync(criterionPassed: true));
        fake.Mock
            .Setup(item => item.CallToolAsync("desktop_invoke", It.IsAny<object>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(System.Text.Json.JsonSerializer.SerializeToElement(new { isError = true, content = new[] { new { type = "text", text = "boom" } } }));

        var result = await FastRunner(fake).RunAsync(Scenario("""[ { "invoke": { "automationId": "saveButton" } } ]"""), CancellationToken.None);

        Assert.Equal("ToolError", result.Steps.Single(step => step.Kind == "invoke").Code);
        Assert.True(result.InfrastructureFailure);
    }

    [Fact]
    public async Task Runner_UnknownStepErrorCode_IsInfrastructure()
    {
        var fake = new FakeMcp(await _fixture.WriteSealedBundleAsync(criterionPassed: true))
        {
            InvokeResponse = new { operationStatus = "Completed", dispatch = "NotStarted", error = new { code = "SomethingNew", message = "x" } },
        };

        var result = await FastRunner(fake).RunAsync(Scenario("""[ { "invoke": { "automationId": "saveButton" } } ]"""), CancellationToken.None);

        Assert.Equal("SomethingNew", result.Steps.Single(step => step.Kind == "invoke").Code);
        Assert.True(result.InfrastructureFailure);
    }

    [Fact]
    public async Task Runner_ErrorResponseOnReportRetrieval_IsInfrastructure()
    {
        var fake = new FakeMcp(await _fixture.WriteSealedBundleAsync(criterionPassed: true));
        fake.Mock
            .Setup(item => item.CallToolAsync("desktop_get_test_report", It.IsAny<object>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(System.Text.Json.JsonSerializer.SerializeToElement(new { content = Array.Empty<object>(), structuredContent = new { error = new { code = "ReportNotFound", message = "gone" } } }));

        var result = await FastRunner(fake).RunAsync(Scenario("""[ { "invoke": { "automationId": "saveButton" } } ]"""), CancellationToken.None);

        Assert.Contains(result.Problems, problem => problem.Contains("ReportNotFound", StringComparison.Ordinal));
        Assert.True(result.InfrastructureFailure);
    }

    [Fact]
    public async Task Runner_TruncatedObservationWithoutTheElement_IsInfrastructureNotElementNotFound()
    {
        var fake = new FakeMcp(await _fixture.WriteSealedBundleAsync(criterionPassed: true))
        {
            ObserveResponse = _ => new
            {
                observationRef = "obs-1",
                isTruncated = true,
                images = new[] { new { imageRef = "img-1" } },
                elements = new[] { new { elementRef = "el-other", windowRef = "win-1", role = "Edit", automationId = "other" } },
            },
        };

        var result = await FastRunner(fake).RunAsync(Scenario("""[ { "invoke": { "automationId": "saveButton" } } ]"""), CancellationToken.None);

        var step = result.Steps.Single(item => item.Kind == "invoke");
        Assert.Equal("InspectionIncomplete", step.Code);
        Assert.Contains("not an app defect", step.Message, StringComparison.Ordinal);
        Assert.Contains("automationId", step.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("windowRef", step.Message, StringComparison.Ordinal);
        Assert.True(result.InfrastructureFailure);
        Assert.NotEqual(VerificationStatus.Pass, result.Status);
    }

    [Fact]
    public async Task Runner_TruncatedObservationWithAUniqueMatch_DoesNotDispatchInput()
    {
        var fake = new FakeMcp(await _fixture.WriteSealedBundleAsync(criterionPassed: true))
        {
            ObserveResponse = _ => new
            {
                observationRef = "obs-1",
                isTruncated = true,
                images = new[] { new { imageRef = "img-1" } },
                elements = new[] { new { elementRef = "el-saveButton", windowRef = "win-1", role = "Button", automationId = "saveButton" } },
            },
        };

        var result = await FastRunner(fake).RunAsync(Scenario("""[ { "invoke": { "automationId": "saveButton" } } ]"""), CancellationToken.None);

        Assert.Equal("InspectionIncomplete", result.Steps.Single(item => item.Kind == "invoke").Code);
        Assert.True(result.InfrastructureFailure);
        // The server refuses every attempt, so no invoke was ever accepted.
        Assert.All(result.Steps.Where(item => item.Kind == "invoke"), item => Assert.NotEqual(VerificationStatus.Pass, item.Status));
    }

    [Fact]
    public async Task Runner_CompleteObservationWithAUniqueMatch_StillDispatches()
    {
        var fake = new FakeMcp(await _fixture.WriteSealedBundleAsync(criterionPassed: true));

        var result = await FastRunner(fake).RunAsync(Scenario("""[ { "invoke": { "automationId": "saveButton" } } ]"""), CancellationToken.None);

        Assert.Equal(VerificationStatus.Pass, result.Steps.Single(item => item.Kind == "invoke").Status);
        Assert.Equal(1, fake.Count("desktop_invoke"));
    }

    [Fact]
    public async Task Runner_SelectorStepDoesNotObserveJustToFindARef()
    {
        var fake = new FakeMcp(await _fixture.WriteSealedBundleAsync(criterionPassed: true));

        var result = await FastRunner(fake).RunAsync(
            Scenario("""[ { "enterText": { "automationId": "textBox", "text": "hi" } }, { "invoke": { "automationId": "saveButton" } } ]"""),
            CancellationToken.None);

        Assert.All(result.Steps.Where(item => item.Kind != "launch"), item => Assert.Equal(VerificationStatus.Pass, item.Status));
        Assert.Equal(1, fake.Count("desktop_observe_app"));
        fake.Verify("desktop_invoke", args => !args.TryGetProperty("elementRef", out _) && !args.TryGetProperty("observationRef", out _));
    }

    [Fact]
    public async Task Runner_SelectorStepWaitsForAnElementThatAppearsLate()
    {
        var fake = new FakeMcp(await _fixture.WriteSealedBundleAsync(criterionPassed: true))
        {
            ObserveResponse = call => call <= 3
                ? new { observationRef = "obs-1", images = new[] { new { imageRef = "img-1" } }, elements = new[] { new { elementRef = "el-x", windowRef = "win-1", role = "Edit", automationId = "other" } } }
                : null!,
        };

        var result = await FastRunner(fake).RunAsync(Scenario("""[ { "invoke": { "automationId": "saveButton" } } ]"""), CancellationToken.None);

        Assert.Equal(VerificationStatus.Pass, result.Steps.Single(item => item.Kind == "invoke").Status);
        Assert.True(fake.Count("desktop_invoke") >= 2);
    }

    [Fact]
    public async Task Runner_InconclusiveInspectionIncompleteCheck_IsInfrastructure()
    {
        var fake = new FakeMcp(await _fixture.WriteSealedBundleAsync(criterionPassed: true))
        {
            CheckResponse = _ => new { verification = "inconclusive", stateAvailable = false, matches = false, matchCount = 0, error = new { code = "InspectionIncomplete", message = "truncated" } },
        };

        var result = await FastRunner(fake).RunAsync(Scenario("""[ { "check": { "kind": "exists", "automationId": "saveButton" } } ]"""), CancellationToken.None);

        var step = result.Steps.Single(item => item.Kind == "check");
        Assert.Equal("InspectionIncomplete", step.Code);
        Assert.True(result.InfrastructureFailure);
        Assert.NotEqual(VerificationStatus.Pass, result.Status);
    }

    [Fact]
    public void NextStep_ForIncompleteInspection_TellsTheAgentToScopeTheCheck()
    {
        var next = VerificationNextSteps.For(VerificationStatus.Fail, "inspection_incomplete");

        Assert.Equal("run_command", next.Kind);
        Assert.Contains("automationId", next.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("windowRef", next.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Fix failing", next.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void NextStep_ForIncompleteInspection_NamesOnlyFieldsTheSpecAccepts()
    {
        var next = VerificationNextSteps.For(VerificationStatus.Fail, "inspection_incomplete");

        Assert.Throws<VerificationSpecException>(() => Scenario("""[ { "check": { "kind": "exists", "automationId": "saveButton", "windowRef": "w" } } ]"""));
        Assert.DoesNotContain("windowRef", next.Text, StringComparison.Ordinal);
        Assert.NotNull(Scenario("""[ { "check": { "kind": "exists", "automationId": "saveButton" } } ]"""));
    }

    private static DesktopScenarioRunner FastRunner(FakeMcp fake) =>
        new(fake.Mock.Object, "fixture", [new CaptureRectangle(0, 0, 1920, 1080)], TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(10));

    private static VerificationScenario Scenario(string steps) => VerificationSpecLoader.ParseScenario($$"""{ "id": "s", "steps": {{steps}} }""", "s");

    [Fact]
    public async Task Run_AppNotBuilt_IsLeftAsAnError()
    {
        var specPath = _fixture.WriteSpec(VerificationFixture.ValidScenario);
        var application = new VerificationFixture.Services().Application(_fixture.Store, new StringWriter());

        var exitCode = await application.RunAsync(Run(specPath), CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Null(application.Outcome);
    }

    [Fact]
    public async Task Run_Pass_HasNoFailureOutcome()
    {
        var specPath = _fixture.WriteSpec(VerificationFixture.ValidScenario);
        _fixture.CreateAppAndMcp();
        var services = new VerificationFixture.Services(new FakeMcp(await _fixture.WriteSealedBundleAsync(criterionPassed: true)));
        var application = services.Application(_fixture.Store, new StringWriter());

        var exitCode = await application.RunAsync(Run(specPath), CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Null(application.Outcome);
    }

    [Fact]
    public async Task Status_FreshVerdictIsNotAFailure_AndAStaleOneIsAVerificationFailure()
    {
        var specPath = _fixture.WriteSpecWith(VerificationFixture.DefaultApp, """[ { "id": "build", "run": "dotnet build" } ]""", VerificationFixture.ValidScenario);
        _fixture.CreateAppAndMcp();
        var services = new VerificationFixture.Services(new FakeMcp(await _fixture.WriteSealedBundleAsync(criterionPassed: true)));
        await services.Application(_fixture.Store, new StringWriter()).RunAsync(Run(specPath), CancellationToken.None);

        var fresh = services.Application(_fixture.Store, new StringWriter());
        var freshExit = await fresh.RunAsync(Status(specPath), CancellationToken.None);
        services.CurrentTree = "TREE2";
        var stale = services.Application(_fixture.Store, new StringWriter());
        var staleExit = await stale.RunAsync(Status(specPath), CancellationToken.None);

        Assert.Equal(0, freshExit);
        Assert.Null(fresh.Outcome);
        Assert.Equal(1, staleExit);
        Assert.Equal(TelemetryOutcome.VerificationFailed, stale.Outcome);
    }

    [Fact]
    public async Task Status_WithoutAnyVerdict_IsAVerificationFailureBecauseTheEvidenceIsNotFresh()
    {
        var specPath = _fixture.WriteSpec(VerificationFixture.ValidScenario);
        var application = new VerificationFixture.Services().Application(_fixture.Store, new StringWriter());

        var exitCode = await application.RunAsync(Status(specPath), CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Equal(TelemetryOutcome.VerificationFailed, application.Outcome);
    }

    [Fact]
    public async Task Trust_InvalidSpec_IsAnInvalidRequestWithExitCodeTwo()
    {
        var specPath = _fixture.WriteSpec("""{ "id": "s", "steps": [] }""");
        var application = new VerificationFixture.Services().Application(_fixture.Store, new StringWriter());

        var exitCode = await application.RunAsync(new CliCommand("verify", SpecPath: specPath, VerifyAction: "trust"), CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Equal(TelemetryOutcome.InvalidRequest, application.Outcome);
    }

    [Fact]
    public async Task TaskStart_MissingTaskFile_IsAnInvalidRequestWithExitCodeTwo()
    {
        var specPath = _fixture.WriteSpec(VerificationFixture.ValidScenario);
        var application = new VerificationFixture.Services().Application(_fixture.Store, new StringWriter());

        var exitCode = await application.RunAsync(
            new CliCommand("verify", SpecPath: specPath, VerifyAction: "task-start", TaskFile: Path.Combine(_fixture.Root, "missing.md")),
            CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Equal(TelemetryOutcome.InvalidRequest, application.Outcome);
    }

    [Fact]
    public async Task ScenarioAdd_MalformedScenarioFile_IsAnInvalidRequest_AndAnIoFailureIsNot()
    {
        var specPath = _fixture.WriteSpec(VerificationFixture.ValidScenario);
        var malformedPath = Path.Combine(_fixture.Root, "bad.json");
        File.WriteAllText(malformedPath, "{ not json");
        var malformed = new VerificationFixture.Services().Application(_fixture.Store, new StringWriter());
        var missing = new VerificationFixture.Services().Application(_fixture.Store, new StringWriter());

        var malformedExit = await malformed.RunAsync(
            new CliCommand("verify", SpecPath: specPath, VerifyAction: "scenario-add", ScenarioFile: malformedPath), CancellationToken.None);
        var missingExit = await missing.RunAsync(
            new CliCommand("verify", SpecPath: specPath, VerifyAction: "scenario-add", ScenarioFile: Path.Combine(_fixture.Root, "absent.json")), CancellationToken.None);

        Assert.Equal(2, malformedExit);
        Assert.Equal(TelemetryOutcome.InvalidRequest, malformed.Outcome);
        Assert.Equal(2, missingExit);
        Assert.Null(missing.Outcome);
    }

    private CliCommand Run(string specPath) => new("verify", SpecPath: specPath, McpExecutablePath: _fixture.McpPath, VerifyAction: "run");

    private static CliCommand Status(string specPath) => new("verify", SpecPath: specPath, VerifyAction: "status");
}
