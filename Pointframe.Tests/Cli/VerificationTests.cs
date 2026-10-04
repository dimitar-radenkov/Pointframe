using System.IO;
using System.Text.Json;
using Moq;
using Pointframe.Cli;
using Pointframe.Engine.Automation.Models;
using Pointframe.Engine.Automation.Services;
using Xunit;

namespace Pointframe.Tests.Cli;

public sealed class VerificationTests : IDisposable
{
    private readonly VerificationFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void Load_SpecInPointframeFolder_ResolvesPathsFromTheProjectRoot()
    {
        var spec = VerificationSpecLoader.Load(_fixture.WriteSpec(VerificationFixture.ValidScenario));

        Assert.Equal(_fixture.Root, spec.RootDirectory);
        Assert.Equal(Path.Combine(_fixture.Root, "bin", "App.exe"), spec.App!.ExecutablePath);
        Assert.Equal(Path.Combine(_fixture.Root, "bin"), spec.App.WorkingDirectory);
        var scenario = Assert.Single(spec.Scenarios);
        Assert.Equal(4, scenario.Steps.Count);
        var check = Assert.IsType<VerificationStep.Check>(scenario.Steps[2]);
        Assert.Equal("textEquals", check.ConditionKind);
        Assert.Equal(DesktopTestingLimits.DefaultUiCheckTimeoutSeconds, check.TimeoutSeconds);
        Assert.Empty(spec.Gates);
    }

    [Theory]
    [InlineData("""{ "id": "s", "criteria": ["A"], "steps": [ { "check": { "kind": "exists", "automationId": "a", "expectFailure": true } } ] }""", "no check covers C1")]
    [InlineData("""{ "id": "s", "criteria": ["A"], "steps": [ { "check": { "kind": "exists", "automationId": "a", "criterion": "C1" } } ] }""", "needs at least one negative control")]
    [InlineData("""{ "id": "s", "steps": [ { "check": { "kind": "exists", "automationId": "a", "criterion": "C2" } } ] }""", "names criterion 'C2'")]
    [InlineData("""{ "id": "s", "steps": [ { "check": { "kind": "exists", "automationId": "a", "criterion": "C1", "expectFailure": true } } ] }""", "cannot also cover a criterion")]
    [InlineData("""{ "id": "s", "steps": [ { "check": { "kind": "textEquals", "automationId": "a" } } ] }""", "needs \"expected\"")]
    [InlineData("""{ "id": "s", "steps": [ { "check": { "kind": "windowExists", "automationId": "a" } } ] }""", "is not supported")]
    [InlineData("""{ "id": "s", "steps": [ { "check": { "kind": "exists", "automationId": "a", "timeoutSeconds": 31 } } ] }""", "timeoutSeconds must be from 1 through 30")]
    [InlineData("""{ "id": "s", "steps": [ { "invoke": { "automationId": "a", "name": "b" } } ] }""", "not both")]
    [InlineData("""{ "id": "s", "steps": [ { "invoke": { "role": "Button" } } ] }""", "both role and name")]
    [InlineData("""{ "id": "s", "steps": [ { "click": { "automationId": "a" } } ] }""", "unknown step 'click'")]
    [InlineData("""{ "id": "s", "steps": [ { "invoke": { "automationId": "a" }, "restart": {} } ] }""", "exactly one property")]
    [InlineData("""{ "id": "s", "steps": [ { "invoke": { "automationId": "a", "typo": 1 } } ] }""", "unknown property 'typo'")]
    [InlineData("""{ "id": "s", "steps": [ { "pressKeys": { "keys": [17, 16, 18, 65, 66] } } ] }""", "1 to 4 virtual-key codes")]
    [InlineData("""{ "id": "s", "steps": [] }""", "steps must be a non-empty array")]
    [InlineData("""{ "id": "bad id", "steps": [ { "restart": {} } ] }""", "may hold only letters")]
    public void Load_InvalidScenario_IsRejectedWithItsPath(string scenario, string expectedMessage)
    {
        var specPath = _fixture.WriteSpec(scenario);

        var exception = Assert.Throws<VerificationSpecException>(() => VerificationSpecLoader.Load(specPath));

        Assert.Contains(expectedMessage, exception.Message, StringComparison.Ordinal);
        Assert.StartsWith("scenarios[0]", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("false", "false")]
    [InlineData("true", "true")]
    [InlineData("3", "3")]
    [InlineData("\"on\"", "on")]
    public void Load_ExpectedAsBooleanOrNumber_IsReadAsText(string expectedJson, string expected)
    {
        var specPath = _fixture.WriteSpec($$"""{ "id": "s", "steps": [ { "check": { "kind": "enabled", "automationId": "saveButton", "expected": {{expectedJson}} } } ] }""");

        var check = Assert.IsType<VerificationStep.Check>(VerificationSpecLoader.Load(specPath).Scenarios[0].Steps[0]);

        Assert.Equal(expected, check.Expected);
    }

    [Fact]
    public void Load_DuplicateScenarioIds_AreRejected()
    {
        var specPath = _fixture.WriteSpec(VerificationFixture.ValidScenario, VerificationFixture.ValidScenario);

        var exception = Assert.Throws<VerificationSpecException>(() => VerificationSpecLoader.Load(specPath));

        Assert.Contains("is used twice", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_UnsupportedSchemaVersion_IsRejected()
    {
        var specPath = _fixture.WriteSpec(VerificationFixture.ValidScenario);
        File.WriteAllText(specPath, File.ReadAllText(specPath).Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2", StringComparison.Ordinal));

        var exception = Assert.Throws<VerificationSpecException>(() => VerificationSpecLoader.Load(specPath));

        Assert.Contains("schemaVersion 2 is not supported", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_GatesOnlySpec_NeedsNoApp()
    {
        var specPath = _fixture.WriteSpecWith(app: null, gates: """[ { "id": "build", "run": "dotnet build", "timeoutMinutes": 5 }, { "id": "tests", "run": "dotnet test", "workingDirectory": "src" } ]""");

        var spec = VerificationSpecLoader.Load(specPath);

        Assert.Null(spec.App);
        Assert.Collection(
            spec.Gates,
            gate =>
            {
                Assert.Equal("build", gate.Id);
                Assert.Equal(_fixture.Root, gate.WorkingDirectory);
                Assert.Equal(5, gate.TimeoutMinutes);
            },
            gate =>
            {
                Assert.Equal(Path.Combine(_fixture.Root, "src"), gate.WorkingDirectory);
                Assert.Equal(30, gate.TimeoutMinutes);
            });
    }

    [Theory]
    [InlineData(null, """[ ]""", "at least one gate or one scenario")]
    [InlineData(null, null, "at least one gate or one scenario")]
    [InlineData(null, """[ { "id": "a", "run": "x" }, { "id": "A", "run": "y" } ]""", "gates[1].id 'A' is used twice")]
    [InlineData(null, """[ { "id": "a", "run": "x", "timeoutMinutes": 0 } ]""", "timeoutMinutes must be from 1 through 120")]
    [InlineData(null, """[ { "id": "a" } ]""", "gates[0].run is required")]
    [InlineData("""{ "id": "a", "executable": "a.exe", "isolation": {} }""", null, "exactly one of environmentVariable or argument")]
    [InlineData("""{ "id": "a", "executable": "a.exe", "isolation": { "environmentVariable": "PATH" } }""", null, "not a system variable")]
    [InlineData("""{ "id": "a", "executable": "a.exe", "isolation": { "environmentVariable": "A B" } }""", null, "plain name")]
    [InlineData("""{ "id": "a", "executable": "a.exe", "isolation": { "argument": "data dir" } }""", null, "must be one option")]
    public void Load_InvalidGatesOrIsolation_IsRejected(string? app, string? gates, string expectedMessage)
    {
        var specPath = _fixture.WriteSpecWith(app, gates);

        var exception = Assert.Throws<VerificationSpecException>(() => VerificationSpecLoader.Load(specPath));

        Assert.Contains(expectedMessage, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_ScenariosWithoutApp_AreRejected()
    {
        var specPath = _fixture.WriteSpecWith(null, null, VerificationFixture.ValidScenario);

        var exception = Assert.Throws<VerificationSpecException>(() => VerificationSpecLoader.Load(specPath));

        Assert.Contains("spec.app is required", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new[] { "verify", "run", "--spec", "a.json", "--mcp=m.exe", "--scenario", "s1", "--task", "t1", "--only", "gates" }, "run")]
    [InlineData(new[] { "verify", "status" }, "status")]
    [InlineData(new[] { "verify", "trust", "--revoke" }, "trust")]
    [InlineData(new[] { "verify", "task", "start", "task.md", "--id", "t1", "--replace" }, "task-start")]
    public void TryParse_VerifyActions_ParseCommand(string[] args, string action)
    {
        var parsed = CliCommandParser.TryParse(args, out var command, out var error);

        Assert.True(parsed, error);
        Assert.Equal("verify", command.Name);
        Assert.Equal(action, command.VerifyAction);
        if (action == "run")
        {
            Assert.Equal("a.json", command.SpecPath);
            Assert.Equal("m.exe", command.McpExecutablePath);
            Assert.Equal("s1", command.ScenarioId);
            Assert.Equal("t1", command.TaskId);
            Assert.Equal("gates", command.Only);
        }

        if (action == "trust")
        {
            Assert.True(command.Revoke);
        }

        if (action == "task-start")
        {
            Assert.Equal("task.md", command.TaskFile);
            Assert.Equal("t1", command.TaskId);
            Assert.True(command.Replace);
        }
    }

    [Theory]
    [InlineData(new[] { "verify" }, "The verify command requires an action: run, status, trust, task start, hook stop, or agent.")]
    [InlineData(new[] { "verify", "start" }, "The verify command requires an action: run, status, trust, task start, hook stop, or agent.")]
    [InlineData(new[] { "verify", "task", "start" }, "The verify task command requires: task start <task-file>.")]
    [InlineData(new[] { "verify", "run", "--fast" }, "Unrecognized verify run option '--fast'.")]
    [InlineData(new[] { "verify", "status", "--mcp", "x" }, "Unrecognized verify status option '--mcp'.")]
    [InlineData(new[] { "verify", "run", "--spec" }, "The verify option --spec requires a value.")]
    [InlineData(new[] { "verify", "run", "--only", "tests" }, "The verify option --only takes gates or scenarios.")]
    public void TryParse_InvalidVerify_ReturnsUsageError(string[] args, string expectedError)
    {
        var parsed = CliCommandParser.TryParse(args, out _, out var error);

        Assert.False(parsed);
        Assert.Equal(expectedError, error);
    }

    [Fact]
    public async Task RunAsync_AllStepsPassAndProofVerifies_PassesScenario()
    {
        var client = new FakeMcp(await _fixture.WriteSealedBundleAsync(criterionPassed: true));

        var result = await Runner(client.Mock.Object).RunAsync(Scenario(), CancellationToken.None);

        Assert.Equal(VerificationStatus.Pass, result.Status);
        Assert.All(result.Steps, step => Assert.Equal(VerificationStatus.Pass, step.Status));
        Assert.True(result.ProofValid);
        Assert.Equal("passed", result.ReportVerdict);
        Assert.Equal("passed", Assert.Single(result.Criteria).Verdict);
        Assert.Empty(result.Problems);
        client.Verify("desktop_enter_text", args => args.GetProperty("semanticValue").GetBoolean()
            && args.GetProperty("elementRef").GetString() == "el-textBox"
            && args.GetProperty("text").GetString() == "hello");
        client.Verify("desktop_invoke", args => args.GetProperty("elementRef").GetString() == "el-saveButton");
        client.Verify("desktop_check_ui", args => args.GetProperty("criterionId").GetString() == "C1");
        client.Verify("desktop_end_test_session", _ => true);
    }

    [Fact]
    public async Task RunAsync_CheckFails_StopsScenarioReportsFoundValueAndStillEndsSession()
    {
        var client = new FakeMcp(await _fixture.WriteSealedBundleAsync(criterionPassed: false)) { CheckResponse = _ => FakeMcp.Failed(string.Empty) };

        var result = await Runner(client.Mock.Object).RunAsync(Scenario(), CancellationToken.None);

        Assert.Equal(VerificationStatus.Fail, result.Status);
        var failed = result.Steps[2];
        Assert.Equal("CheckFailed", failed.Code);
        Assert.Equal("hello", failed.Expected);
        Assert.Equal(string.Empty, failed.Actual);
        Assert.Equal(VerificationStatus.Skipped, result.Steps[3].Status);
        Assert.Contains(result.Problems, problem => problem.Contains("verdict is 'failed'", StringComparison.Ordinal));
        client.Verify("desktop_get_test_report", _ => true);
        client.Verify("desktop_end_test_session", _ => true);
    }

    [Fact]
    public async Task RunAsync_FailBeforeMode_RunsNegativeControlsAfterAFailedCriterion()
    {
        var client = new FakeMcp(await _fixture.WriteSealedBundleAsync(criterionPassed: false))
        {
            CheckResponse = args => args.GetProperty("expectFailure").GetBoolean() ? FakeMcp.Passed(string.Empty) : FakeMcp.Failed(string.Empty),
        };

        var result = await Runner(client.Mock.Object).RunAsync(Scenario(), CancellationToken.None, continueAfterFailedChecks: true);

        Assert.Equal(VerificationStatus.Fail, result.Steps[2].Status);
        Assert.Equal(VerificationStatus.Pass, result.Steps[3].Status);
        Assert.Equal(2, client.Count("desktop_check_ui"));
    }

    [Fact]
    public async Task RunAsync_RestartWhileTheClosedAppIsStillExiting_WaitsForTheExit()
    {
        var stillRunning = new { operationStatus = "Completed", dispatch = "NotStarted", error = new { code = "TargetStillRunning", message = "still exiting" } };
        var client = new FakeMcp(await _fixture.WriteSealedBundleAsync(criterionPassed: true))
        {
            RestartResponse = call => call <= 2 ? stillRunning : new { operationStatus = "Completed", dispatch = "Complete" },
        };
        var scenario = VerificationSpecLoader.ParseScenario(
            """{ "id": "r", "steps": [ { "invoke": { "automationId": "saveButton" } }, { "restart": {} }, { "check": { "kind": "exists", "automationId": "textBox" } } ] }""",
            "r");

        var result = await Runner(client.Mock.Object).RunAsync(scenario, CancellationToken.None);

        Assert.Equal(VerificationStatus.Pass, result.Steps[1].Status);
        Assert.Equal(3, client.Count("desktop_restart_app"));
    }

    [Fact]
    public async Task RunAsync_ElementNeverAppears_FailsWithTheIdsTheAppExposes()
    {
        var client = new FakeMcp(await _fixture.WriteSealedBundleAsync(criterionPassed: false)) { ElementIds = ["otherBox"] };

        var result = await Runner(client.Mock.Object).RunAsync(Scenario(), CancellationToken.None);

        var failed = result.Steps[0];
        Assert.Equal("ElementNotFound", failed.Code);
        Assert.Contains("automationId 'textBox'", failed.Message, StringComparison.Ordinal);
        Assert.Contains("Automation ids seen: otherBox", failed.Message, StringComparison.Ordinal);
        Assert.All(result.Steps.Skip(1), step => Assert.Equal(VerificationStatus.Skipped, step.Status));
    }

    [Fact]
    public async Task RunAsync_CleanupFails_CannotPass()
    {
        var client = new FakeMcp(await _fixture.WriteSealedBundleAsync(criterionPassed: true))
        {
            EndResponse = new { operationStatus = "Completed", dispatch = "NotStarted", error = new { code = "CleanupIncomplete", message = "still running" } },
        };

        var result = await Runner(client.Mock.Object).RunAsync(Scenario(), CancellationToken.None);

        Assert.Equal(VerificationStatus.Fail, result.Status);
        Assert.Contains(result.Problems, problem => problem.Contains("CleanupIncomplete", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_TamperedReport_CannotPass()
    {
        var bundle = await _fixture.WriteSealedBundleAsync(criterionPassed: true);
        var reportPath = Path.Combine(bundle, DesktopProofBundle.ReportFileName);
        File.WriteAllText(reportPath, File.ReadAllText(reportPath).Replace("app.exe", "other.exe", StringComparison.Ordinal));
        var client = new FakeMcp(bundle);

        var result = await Runner(client.Mock.Object).RunAsync(Scenario(), CancellationToken.None);

        Assert.Equal(VerificationStatus.Fail, result.Status);
        Assert.False(result.ProofValid);
        Assert.Contains(result.Problems, problem => problem.StartsWith("Proof:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_SessionDoesNotStart_FailsWithoutRunningSteps()
    {
        var client = new FakeMcp(await _fixture.WriteSealedBundleAsync(criterionPassed: true))
        {
            StartResponse = new { operationStatus = "Completed", dispatch = "NotStarted", error = new { code = "LaunchFailed", message = "no exe" } },
        };

        var result = await Runner(client.Mock.Object).RunAsync(Scenario(), CancellationToken.None);

        Assert.Equal(VerificationStatus.Fail, result.Status);
        Assert.Equal("LaunchFailed", result.Steps[0].Code);
        Assert.All(result.Steps.Skip(1), step => Assert.Equal(VerificationStatus.Skipped, step.Status));
        client.Mock.Verify(
            item => item.CallToolAsync("desktop_end_test_session", It.IsAny<object>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public void FailureDetails_PrefersCompilerErrorsFailedTestsAndErrorLines()
    {
        string[] lines =
        [
            "Restore complete.",
            "C:\\src\\A.cs(10,5): error CS1002: ; expected [C:\\src\\A.csproj]",
            "  Failed Pointframe.Tests.FooTests.Bar [12 ms]",
            "ERROR docs/kb.md: path does not exist",
            "Build FAILED.",
        ];

        var details = GateRunner.FailureDetails(lines);

        Assert.Equal(3, details.Count);
        Assert.Contains("error CS1002", details[0], StringComparison.Ordinal);
        Assert.StartsWith("Failed Pointframe.Tests.FooTests.Bar", details[1], StringComparison.Ordinal);
        Assert.StartsWith("ERROR", details[2], StringComparison.Ordinal);
    }

    [Fact]
    public void FailureDetails_WithoutErrorLines_ShowsTheEndOfTheLog()
    {
        var lines = Enumerable.Range(1, 30).Select(number => $"line {number}").ToArray();

        var details = GateRunner.FailureDetails(lines);

        Assert.Equal(15, details.Count);
        Assert.Equal("line 30", details[^1]);
    }

    [Fact]
    public void ExaminerAnswer_IsReadIntoAProposal()
    {
        using var document = JsonDocument.Parse("""{ "criteria": ["A"], "steps": [ { "restart": {} } ], "requiredAutomationIds": ["saveButton"], "notes": "n" }""");

        var proposal = AgentExaminer.ParseProposal(document.RootElement);

        Assert.Equal(["A"], proposal.Criteria);
        Assert.Equal(["saveButton"], proposal.RequiredAutomationIds);
        Assert.Equal(JsonValueKind.Array, proposal.Steps.ValueKind);
        Assert.Equal("n", proposal.Notes);
    }

    [Fact]
    public void ExaminerAnswer_StepsAsJsonText_AreTurnedIntoObjects()
    {
        using var document = JsonDocument.Parse("""{ "criteria": ["A"], "steps": [ "{\"restart\": {}}", "{\"invoke\": {\"automationId\": \"saveButton\"}}" ], "requiredAutomationIds": [], "notes": "" }""");

        var proposal = AgentExaminer.ParseProposal(document.RootElement);

        Assert.Equal(JsonValueKind.Object, proposal.Steps[0].ValueKind);
        Assert.Equal("saveButton", proposal.Steps[1].GetProperty("invoke").GetProperty("automationId").GetString());
    }

    [Theory]
    [InlineData(typeof(AgentExaminer))]
    [InlineData(typeof(AgentReviewer))]
    [InlineData(typeof(AgentApprover))]
    public void Schemas_AreInTheStrictFormCodexRequires(Type role)
    {
        var schema = (string)role.GetField("OutputSchema", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
        using var document = JsonDocument.Parse(schema);

        AssertStrict(document.RootElement);
    }

    private static void AssertStrict(JsonElement schema)
    {
        if (schema.TryGetProperty("properties", out var properties))
        {
            Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
            var required = schema.GetProperty("required").EnumerateArray().Select(item => item.GetString()).ToHashSet();
            foreach (var property in properties.EnumerateObject())
            {
                Assert.Contains(property.Name, required);
                AssertStrict(property.Value);
            }
        }

        if (schema.TryGetProperty("items", out var items))
        {
            AssertStrict(items);
        }
    }

    [Fact]
    public void ExaminerAnswer_WithoutSteps_IsAnAgentError()
    {
        using var document = JsonDocument.Parse("""{ "criteria": ["A"] }""");

        Assert.Throws<AgentException>(() => AgentExaminer.ParseProposal(document.RootElement));
    }

    [Fact]
    public async Task Examiner_GivesTheAgentTheDesktopToolsAndTheTask()
    {
        var runner = new Mock<IAgentRunner>();
        AgentRequest? sent = null;
        using var answer = JsonDocument.Parse("""{ "criteria": ["A"], "steps": [ { "restart": {} } ], "requiredAutomationIds": [], "notes": "" }""");
        runner.Setup(item => item.RunAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>()))
            .Callback<AgentRequest, CancellationToken>((request, _) => sent = request)
            .ReturnsAsync(answer.RootElement.Clone());

        await new AgentExaminer(runner.Object).ProposeAsync(
            new ExaminerRequest("Keep the text.", "fixture", "C:/mcp/Pointframe.Mcp.exe", "C:/tmp/policy.json", "C:/tmp/work", new Dictionary<string, string>()),
            CancellationToken.None);

        Assert.Equal("examiner", sent!.Role);
        Assert.Contains("profile id is 'fixture'", sent.Instructions, StringComparison.Ordinal);
        Assert.Contains("Keep the text.", sent.Input, StringComparison.Ordinal);
        Assert.Equal(["--desktop-testing", "--desktop-policy", "C:/tmp/policy.json"], sent.McpServer!.Arguments);
        Assert.Equal(3m, sent.MaxBudgetUsd);
    }

    [Fact]
    public async Task Examiner_WithoutAnAgent_SaysHowToGetOne()
    {
        var exception = await Assert.ThrowsAsync<AgentException>(() => new AgentExaminer(null).ProposeAsync(
            new ExaminerRequest("t", "fixture", "m", "p", "w", new Dictionary<string, string>()), CancellationToken.None));

        Assert.Contains("verify agent", exception.Message, StringComparison.Ordinal);
    }

    private static DesktopScenarioRunner Runner(IMcpToolClient client) =>
        new(client, "fixture", [new CaptureRectangle(0, 0, 1920, 1080)], TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(10));

    private VerificationScenario Scenario() => VerificationSpecLoader.Load(_fixture.WriteSpec(VerificationFixture.ValidScenario)).Scenarios[0];
}
