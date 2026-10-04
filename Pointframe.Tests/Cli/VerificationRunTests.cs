using System.IO;
using System.Text.Json;
using Moq;
using Pointframe.Cli;
using Xunit;

namespace Pointframe.Tests.Cli;

public sealed class VerificationRunTests : IDisposable
{
    private const string Gates = """[ { "id": "build", "run": "dotnet build" }, { "id": "tests", "run": "dotnet test" }, { "id": "extra", "run": "cargo test --workspace" } ]""";

    private const string TaskScenarioSteps = """
        [
          { "enterText": { "automationId": "textBox", "text": "keep me" } },
          { "check": { "kind": "textEquals", "automationId": "textBox", "expected": "keep me", "criterion": "C1" } },
          { "check": { "kind": "textEquals", "automationId": "textBox", "expected": "not this", "expectFailure": true } }
        ]
        """;

    private readonly VerificationFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task Run_InvalidSpec_ExitsTwoWithJsonError()
    {
        var specPath = _fixture.WriteSpec("""{ "id": "s", "steps": [] }""");
        var output = new StringWriter();

        var exitCode = await new VerificationFixture.Services().Application(_fixture.Store, output)
            .RunAsync(Run(specPath), CancellationToken.None);

        Assert.Equal(2, exitCode);
        using var verdict = JsonDocument.Parse(output.ToString());
        Assert.Equal("fail", verdict.RootElement.GetProperty("status").GetString());
        Assert.Equal("spec_invalid", verdict.RootElement.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task Run_UnknownSchemaVersion_RejectsBeforeStartingAnyGate()
    {
        var specPath = _fixture.WriteSpecWith(
            app: null,
            gates: """[ { "id": "build", "run": "dotnet build" } ]""");
        File.WriteAllText(specPath, File.ReadAllText(specPath).Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2", StringComparison.Ordinal));
        var services = new VerificationFixture.Services();
        var output = new StringWriter();

        var exitCode = await services.Application(_fixture.Store, output).RunAsync(Run(specPath), CancellationToken.None);

        Assert.Equal(2, exitCode);
        using var verdict = JsonDocument.Parse(output.ToString());
        Assert.Equal("spec_invalid", verdict.RootElement.GetProperty("errorCode").GetString());
        Assert.Empty(services.Launches);
        services.Commands.Verify(
            item => item.RunAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Run_AppNotBuilt_ExitsOneAndWritesVerdict()
    {
        var specPath = _fixture.WriteSpec(VerificationFixture.ValidScenario);

        var exitCode = await new VerificationFixture.Services().Application(_fixture.Store, new StringWriter())
            .RunAsync(Run(specPath), CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Equal("app_not_found", _fixture.ReadVerdict().GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task Run_FullPass_RecordsProvenanceAndThePolicyNamesTheApp()
    {
        var specPath = _fixture.WriteSpec(VerificationFixture.ValidScenario);
        _fixture.CreateAppAndMcp();
        var services = new VerificationFixture.Services(new FakeMcp(await _fixture.WriteSealedBundleAsync(criterionPassed: true)));

        var exitCode = await services.Application(_fixture.Store, new StringWriter()).RunAsync(Run(specPath), CancellationToken.None);

        Assert.Equal(0, exitCode);
        var verdict = _fixture.ReadVerdict();
        Assert.Equal("pass", verdict.GetProperty("status").GetString());
        var provenance = verdict.GetProperty("provenance");
        Assert.Equal("TREE1", provenance.GetProperty("treeHash").GetString());
        Assert.Equal("HEAD1", provenance.GetProperty("head").GetString());
        Assert.Equal("test-1.0", provenance.GetProperty("verifierVersion").GetString());
        Assert.False(string.IsNullOrEmpty(provenance.GetProperty("mcpSha256").GetString()));
        var (arguments, _) = Assert.Single(services.Launches);
        using var policy = JsonDocument.Parse(File.ReadAllText(arguments[2]));
        var profile = policy.RootElement.GetProperty("profiles")[0];
        Assert.Equal("fixture", profile.GetProperty("id").GetString());
        Assert.Equal(Path.Combine(_fixture.Root, "bin", "App.exe"), profile.GetProperty("executablePath").GetString());
        Assert.False(profile.GetProperty("allowAttach").GetBoolean());
    }

    [Fact]
    public async Task Run_OneScenarioSelected_IsPartialAndExitsZero()
    {
        var specPath = _fixture.WriteSpec(VerificationFixture.ValidScenario);
        _fixture.CreateAppAndMcp();
        var services = new VerificationFixture.Services(new FakeMcp(await _fixture.WriteSealedBundleAsync(criterionPassed: true)));

        var exitCode = await services.Application(_fixture.Store, new StringWriter())
            .RunAsync(Run(specPath) with { ScenarioId = "save-text" }, CancellationToken.None);

        Assert.Equal(0, exitCode);
        var verdict = _fixture.ReadVerdict();
        Assert.Equal("partial", verdict.GetProperty("status").GetString());
        Assert.False(verdict.GetProperty("complete").GetBoolean());
    }

    [Fact]
    public async Task Run_DesktopAlreadyInUse_ExitsOne()
    {
        var specPath = _fixture.WriteSpec(VerificationFixture.ValidScenario);
        _fixture.CreateAppAndMcp();
        var services = new VerificationFixture.Services();
        using var held = new Semaphore(1, 1, services.LockName);
        Assert.True(held.WaitOne(TimeSpan.Zero));

        var exitCode = await services.Application(_fixture.Store, new StringWriter()).RunAsync(Run(specPath), CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Equal("desktop_busy", _fixture.ReadVerdict().GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task Run_UntrustedGates_RunNothingAndListTheCommands()
    {
        var specPath = _fixture.WriteUntrustedSpec(VerificationFixture.DefaultApp, Gates);
        var services = new VerificationFixture.Services();

        var exitCode = await services.Application(_fixture.Store, new StringWriter()).RunAsync(Run(specPath), CancellationToken.None);

        Assert.Equal(1, exitCode);
        var verdict = _fixture.ReadVerdict();
        Assert.Equal("approver_unavailable", verdict.GetProperty("errorCode").GetString());
        Assert.Contains(verdict.GetProperty("details").EnumerateArray(), line => line.GetString()!.StartsWith("gate build: dotnet build", StringComparison.Ordinal));
        services.Commands.Verify(
            item => item.RunAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Trust_WithoutATerminal_IsRefused()
    {
        var specPath = _fixture.WriteUntrustedSpec(VerificationFixture.DefaultApp, Gates);
        var services = new VerificationFixture.Services();
        services.Confirmation.SetupGet(item => item.CanAsk).Returns(false);

        var exitCode = await services.Application(_fixture.Store, new StringWriter()).RunAsync(Trust(specPath), CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Null(_fixture.Store.ReadTrust(_fixture.Root));
        services.Confirmation.Verify(item => item.Confirm(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Trust_ApprovedGatesRunAndAChangedGateNeedsApprovalAgain()
    {
        var specPath = _fixture.WriteUntrustedSpec(VerificationFixture.DefaultApp, Gates);
        var services = new VerificationFixture.Services();
        services.Confirmation.SetupGet(item => item.CanAsk).Returns(true);
        services.Confirmation.Setup(item => item.Confirm(It.IsAny<string>())).Returns(true);
        var application = services.Application(_fixture.Store, new StringWriter());

        Assert.Equal(0, await application.RunAsync(Trust(specPath), CancellationToken.None));
        Assert.Equal(0, await application.RunAsync(Run(specPath), CancellationToken.None));
        Assert.Equal("pass", _fixture.ReadVerdict().GetProperty("status").GetString());
        services.Commands.Verify(item => item.RunAsync("dotnet build", _fixture.Root, TimeSpan.FromMinutes(30), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);

        File.WriteAllText(specPath, File.ReadAllText(specPath).Replace("dotnet test", "dotnet test --no-build", StringComparison.Ordinal));
        Assert.Equal(1, await application.RunAsync(Run(specPath), CancellationToken.None));
        var verdict = _fixture.ReadVerdict();
        Assert.Equal("approver_unavailable", verdict.GetProperty("errorCode").GetString());
        Assert.Contains("outside the standard set", verdict.GetProperty("details")[1].GetString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[ ]")]
    [InlineData("""[ { "id": "build", "run": "dotnet build" } ]""")]
    public async Task Run_GatesRemovedAfterApproval_StandardRemainingCommandsUsePolicy(string fewerGates)
    {
        _fixture.WriteSpecWith(VerificationFixture.DefaultApp, Gates, VerificationFixture.ValidScenario);
        var specPath = _fixture.WriteUntrustedSpec(VerificationFixture.DefaultApp, fewerGates.Replace("\"\"", "\"", StringComparison.Ordinal), VerificationFixture.ValidScenario);

        var exitCode = await new VerificationFixture.Services().Application(_fixture.Store, new StringWriter()).RunAsync(Run(specPath) with { Only = "gates" }, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal("policy", _fixture.ReadVerdict().GetProperty("provenance").GetProperty("commandsApprovedBy").GetString());
        Assert.Equal("policy", _fixture.Store.ReadTrust(_fixture.Root)!.ApprovedBy);
    }

    [Fact]
    public async Task Run_AppPointedAtAnotherProgramAfterApproval_NeedsApprovalAgain()
    {
        _fixture.WriteSpec(VerificationFixture.ValidScenario);
        var specPath = _fixture.WriteUntrustedSpec(
            """{ "id": "fixture", "executable": "C:/Windows/System32/cmd.exe", "arguments": ["/c", "whoami"] }""", null, VerificationFixture.ValidScenario);

        var exitCode = await new VerificationFixture.Services().Application(_fixture.Store, new StringWriter())
            .RunAsync(Run(specPath) with { Only = "scenarios" }, CancellationToken.None);

        Assert.Equal(1, exitCode);
        var verdict = _fixture.ReadVerdict();
        Assert.Equal("spec_untrusted", verdict.GetProperty("errorCode").GetString());
        Assert.Contains(verdict.GetProperty("details").EnumerateArray(), line => line.GetString()!.Contains("cmd.exe /c whoami", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TaskStart_UntrustedSpec_LaunchesNothing()
    {
        var specPath = _fixture.WriteUntrustedSpec(VerificationFixture.DefaultApp, """[ { "id": "extra", "run": "cargo test --workspace" } ]""", VerificationFixture.ValidScenario);
        _fixture.CreateAppAndMcp();
        var services = new VerificationFixture.Services();
        var output = new StringWriter();

        var exitCode = await services.Application(_fixture.Store, output).RunAsync(TaskStart(specPath, WriteTask("Something.")), CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Contains("approver_unavailable", output.ToString(), StringComparison.Ordinal);
        services.Examiner.Verify(item => item.ProposeAsync(It.IsAny<ExaminerRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }
    [Fact]
    public async Task Trust_Revoke_RemovesTheApproval()
    {
        var specPath = _fixture.WriteSpecWith(VerificationFixture.DefaultApp, Gates);
        var spec = VerificationSpecLoader.Load(specPath);
        _fixture.Store.WriteTrust(new SpecTrust(1, _fixture.Root, SpecDigests.CommandsSha256(spec), [], DateTimeOffset.UtcNow));

        var exitCode = await new VerificationFixture.Services().Application(_fixture.Store, new StringWriter())
            .RunAsync(Trust(specPath) with { Revoke = true }, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Null(_fixture.Store.ReadTrust(_fixture.Root));
    }

    [Fact]
    public async Task Run_FailedGate_SkipsScenariosAndFails()
    {
        var specPath = _fixture.WriteSpecWith(VerificationFixture.DefaultApp, Gates, VerificationFixture.ValidScenario);
        TrustGates(specPath);
        var services = new VerificationFixture.Services();
        services.Commands.Setup(item => item.RunAsync("dotnet build", It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommandResult(1, false, ["A.cs(1,1): error CS1002: ; expected"]));

        var exitCode = await services.Application(_fixture.Store, new StringWriter()).RunAsync(Run(specPath), CancellationToken.None);

        Assert.Equal(1, exitCode);
        var verdict = _fixture.ReadVerdict();
        var gates = verdict.GetProperty("gates");
        Assert.Equal("fail", gates[0].GetProperty("status").GetString());
        Assert.Contains("error CS1002", gates[0].GetProperty("details")[0].GetString(), StringComparison.Ordinal);
        Assert.Equal("pass", gates[1].GetProperty("status").GetString());
        Assert.Equal("skipped", verdict.GetProperty("scenarios")[0].GetProperty("status").GetString());
        services.Factory.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Run_OnlyGates_IsPartialAndLaunchesNothing()
    {
        var specPath = _fixture.WriteSpecWith(VerificationFixture.DefaultApp, Gates, VerificationFixture.ValidScenario);
        TrustGates(specPath);
        var services = new VerificationFixture.Services();

        var exitCode = await services.Application(_fixture.Store, new StringWriter())
            .RunAsync(Run(specPath) with { Only = "gates" }, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal("partial", _fixture.ReadVerdict().GetProperty("status").GetString());
        services.Factory.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Run_IsolationByVariable_GivesEachScenarioAFreshFolderAndDeletesIt()
    {
        var app = """{ "id": "fixture", "executable": "bin/App.exe", "isolation": { "environmentVariable": "APP_DATA_DIR" } }""";
        var specPath = _fixture.WriteSpecWith(app, null, VerificationFixture.ValidScenario);
        _fixture.CreateAppAndMcp();
        var services = new VerificationFixture.Services(new FakeMcp(await _fixture.WriteSealedBundleAsync(criterionPassed: true)));

        var exitCode = await services.Application(_fixture.Store, new StringWriter()).RunAsync(Run(specPath), CancellationToken.None);

        Assert.Equal(0, exitCode);
        var (_, environment) = Assert.Single(services.Launches);
        var folder = environment["APP_DATA_DIR"];
        Assert.EndsWith(Path.Combine("data", "save-text"), folder, StringComparison.Ordinal);
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public async Task Run_IsolationByArgument_PassesTheFolderToTheApp()
    {
        var app = """{ "id": "fixture", "executable": "bin/App.exe", "arguments": ["--quiet"], "isolation": { "argument": "--data-dir" } }""";
        var specPath = _fixture.WriteSpecWith(app, null, VerificationFixture.ValidScenario);
        _fixture.CreateAppAndMcp();
        var services = new VerificationFixture.Services(new FakeMcp(await _fixture.WriteSealedBundleAsync(criterionPassed: true)));

        await services.Application(_fixture.Store, new StringWriter()).RunAsync(Run(specPath), CancellationToken.None);

        var (arguments, environment) = Assert.Single(services.Launches);
        Assert.Empty(environment);
        using var policy = JsonDocument.Parse(File.ReadAllText(arguments[2]));
        var appArguments = policy.RootElement.GetProperty("profiles")[0].GetProperty("arguments").EnumerateArray().Select(item => item.GetString()).ToArray();
        Assert.Equal("--quiet", appArguments[0]);
        Assert.Equal("--data-dir", appArguments[1]);
        Assert.EndsWith(Path.Combine("data", "save-text"), appArguments[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Status_IsFreshOnlyForAPassOnTheCurrentTree()
    {
        var specPath = _fixture.WriteSpec(VerificationFixture.ValidScenario);
        _fixture.CreateAppAndMcp();
        var services = new VerificationFixture.Services(new FakeMcp(await _fixture.WriteSealedBundleAsync(criterionPassed: true)));
        var application = services.Application(_fixture.Store, new StringWriter());
        await application.RunAsync(Run(specPath), CancellationToken.None);

        var output = new StringWriter();
        var fresh = await services.Application(_fixture.Store, output).RunAsync(Status(specPath), CancellationToken.None);
        services.CurrentTree = "TREE2";
        var stale = await services.Application(_fixture.Store, new StringWriter()).RunAsync(Status(specPath), CancellationToken.None);

        Assert.Equal(0, fresh);
        Assert.Contains("\"fresh\": true", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, stale);
    }

    [Fact]
    public void FailBefore_CriterionThatAlreadyHolds_IsRejected()
    {
        var scenario = TaskScenario();
        var result = Result(scenario, VerificationStatus.Pass, VerificationStatus.Pass, VerificationStatus.Pass);

        var failBefore = FailBefore.Evaluate(scenario, result);

        Assert.Equal(FailBefore.Rejected, failBefore.Status);
        Assert.Equal("passed", failBefore.Criteria[0].Before);
        Assert.Contains("already holds", failBefore.Problems[0], StringComparison.Ordinal);
    }

    [Fact]
    public void FailBefore_FailedCriterionAndPassingNegativeControl_IsConfirmed()
    {
        var scenario = TaskScenario();

        var failBefore = FailBefore.Evaluate(scenario, Result(scenario, VerificationStatus.Pass, VerificationStatus.Fail, VerificationStatus.Pass));

        Assert.Equal(FailBefore.Confirmed, failBefore.Status);
        Assert.Equal("failed", failBefore.Criteria[0].Before);
    }

    [Fact]
    public void FailBefore_ElementTheTaskAdds_CountsAsNotReached()
    {
        var scenario = TaskScenario();

        var failBefore = FailBefore.Evaluate(scenario, Result(scenario, VerificationStatus.Fail, VerificationStatus.Skipped, VerificationStatus.Skipped));

        Assert.Equal(FailBefore.Confirmed, failBefore.Status);
        Assert.Equal("notReached", failBefore.Criteria[0].Before);
    }

    [Fact]
    public void FailBefore_RunThatNeverStarted_IsRejected()
    {
        var scenario = TaskScenario();
        var crashed = new VerificationScenarioResult(scenario.Id, VerificationStatus.Fail, null, [], [], null, false, null, ["The Pointframe MCP server failed: pipe closed"]);

        var failBefore = FailBefore.Evaluate(scenario, crashed);

        Assert.Equal(FailBefore.Rejected, failBefore.Status);
        Assert.Contains("pipe closed", failBefore.Problems[0], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(VerificationStatus.Fail, "StepError", VerificationStatus.Skipped)]
    [InlineData(VerificationStatus.Pass, "CheckInconclusive", VerificationStatus.Pass)]
    public void FailBefore_FailureThatSaysNothingAboutTheApp_IsRejected(string firstStatus, string failingCode, string lastStatus)
    {
        var scenario = TaskScenario();
        var second = firstStatus == VerificationStatus.Fail ? VerificationStatus.Skipped : VerificationStatus.Fail;
        var result = new VerificationScenarioResult(
            scenario.Id, VerificationStatus.Fail, "failed", [],
            [
                new VerificationStepResult(0, "enterText", firstStatus, "step", firstStatus == VerificationStatus.Fail ? failingCode : null),
                new VerificationStepResult(1, "check", second, "step", second == VerificationStatus.Fail ? failingCode : null),
                new VerificationStepResult(2, "check", lastStatus, "step"),
            ],
            null, true, null, []);

        var failBefore = FailBefore.Evaluate(scenario, result);

        Assert.Equal(FailBefore.Rejected, failBefore.Status);
        Assert.Contains(failingCode, string.Join(" ", failBefore.Problems), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GitWorkingTree_OwnOutputDoesNotChangeTheHashButOtherFilesDo(bool artifactsIgnored)
    {
        var repository = Path.Combine(_fixture.Root, "repo");
        Directory.CreateDirectory(repository);
        Git(repository, "init", "-q");
        File.WriteAllText(Path.Combine(repository, "a.txt"), "one");
        if (artifactsIgnored)
        {
            File.WriteAllText(Path.Combine(repository, ".gitignore"), "artifacts/\n");
        }

        var reader = new GitWorkingTreeReader();
        var before = reader.Read(repository).TreeHash;

        Directory.CreateDirectory(Path.Combine(repository, "artifacts", "pointframe-verify"));
        File.WriteAllText(Path.Combine(repository, "artifacts", "pointframe-verify", "verdict.json"), "{}");
        var withOwnOutput = reader.Read(repository).TreeHash;
        File.WriteAllText(Path.Combine(repository, "b.txt"), "two");
        var withNewFile = reader.Read(repository).TreeHash;

        Assert.NotNull(before);
        Assert.Equal(before, withOwnOutput);
        Assert.NotEqual(before, withNewFile);
    }

    [Fact]
    public void GitWorkingTree_DiffShowsUncommittedAndNewFiles()
    {
        var repository = Path.Combine(_fixture.Root, "repo");
        Directory.CreateDirectory(repository);
        Git(repository, "init", "-q");
        File.WriteAllText(Path.Combine(repository, ".gitignore"), "artifacts/\n");
        File.WriteAllText(Path.Combine(repository, "a.txt"), "one\n");
        Git(repository, "add", "-A");
        Git(repository, "-c", "user.name=t", "-c", "user.email=t@example.invalid", "commit", "-q", "-m", "base");
        File.WriteAllText(Path.Combine(repository, "a.txt"), "changed\n");
        File.WriteAllText(Path.Combine(repository, "new.txt"), "added\n");

        var diff = new GitWorkingTreeReader().Diff(repository, "HEAD");

        Assert.NotNull(diff);
        Assert.Contains("+changed", diff, StringComparison.Ordinal);
        Assert.Contains("new.txt", diff, StringComparison.Ordinal);
    }

    [Fact]
    public void FailBefore_FailingNegativeControl_IsRejected()
    {
        var scenario = TaskScenario();

        var failBefore = FailBefore.Evaluate(scenario, Result(scenario, VerificationStatus.Pass, VerificationStatus.Fail, VerificationStatus.Fail));

        Assert.Equal(FailBefore.Rejected, failBefore.Status);
        Assert.Contains("negative control", failBefore.Problems[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task TaskStart_ConfirmedProposal_FreezesTheSnapshotOutsideTheRepository()
    {
        var specPath = _fixture.WriteSpec(VerificationFixture.ValidScenario);
        _fixture.CreateAppAndMcp();
        var taskFile = WriteTask("Text typed into the box is kept.");
        var services = new VerificationFixture.Services(FailingCriterionMcp(await _fixture.WriteSealedBundleAsync(criterionPassed: false)));
        SetupExaminer(services);
        var output = new StringWriter();

        var exitCode = await services.Application(_fixture.Store, output).RunAsync(TaskStart(specPath, taskFile), CancellationToken.None);

        Assert.Equal(0, exitCode);
        using var response = JsonDocument.Parse(output.ToString());
        Assert.Equal("created", response.RootElement.GetProperty("status").GetString());
        var stored = _fixture.Store.ReadTask(_fixture.Root, "keep-text");
        Assert.NotNull(stored);
        Assert.StartsWith(Path.Combine(_fixture.Root, "store"), _fixture.Store.TaskPath(_fixture.Root, "keep-text"), StringComparison.Ordinal);
        Assert.Equal(FailBefore.Confirmed, stored.Value.Snapshot.FailBefore.Status);
        Assert.Equal("TREE1", stored.Value.Snapshot.TreeHashAtStart);
        Assert.Equal("Text typed into the box is kept.", stored.Value.Snapshot.TaskText);
        services.Examiner.Verify(
            item => item.ProposeAsync(It.Is<ExaminerRequest>(request => !request.WorkDirectory.StartsWith(_fixture.Root, StringComparison.OrdinalIgnoreCase)), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task TaskStart_CriterionAlreadyHolds_IsRejectedAndNothingIsFrozen()
    {
        var specPath = _fixture.WriteSpec(VerificationFixture.ValidScenario);
        _fixture.CreateAppAndMcp();
        var services = new VerificationFixture.Services(new FakeMcp(await _fixture.WriteSealedBundleAsync(criterionPassed: true)));
        SetupExaminer(services);

        var exitCode = await services.Application(_fixture.Store, new StringWriter())
            .RunAsync(TaskStart(specPath, WriteTask("Something.")), CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Null(_fixture.Store.ReadTask(_fixture.Root, "keep-text"));
    }

    [Fact]
    public async Task TaskStart_ProposalBreakingTheRules_IsRejected()
    {
        var specPath = _fixture.WriteSpec(VerificationFixture.ValidScenario);
        _fixture.CreateAppAndMcp();
        var services = new VerificationFixture.Services(new FakeMcp(await _fixture.WriteSealedBundleAsync(criterionPassed: false)));
        SetupExaminer(services, steps: """[ { "check": { "kind": "exists", "automationId": "textBox", "criterion": "C1" } } ]""");
        var output = new StringWriter();

        var exitCode = await services.Application(_fixture.Store, output).RunAsync(TaskStart(specPath, WriteTask("Something.")), CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Contains("examiner_invalid", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("negative control", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TaskStart_ExistingTask_IsNotReplacedWithoutAPerson()
    {
        var specPath = _fixture.WriteSpec(VerificationFixture.ValidScenario);
        _fixture.CreateAppAndMcp();
        var services = new VerificationFixture.Services(FailingCriterionMcp(await _fixture.WriteSealedBundleAsync(criterionPassed: false)));
        SetupExaminer(services);
        var taskFile = WriteTask("Text typed into the box is kept.");
        Assert.Equal(0, await services.Application(_fixture.Store, new StringWriter()).RunAsync(TaskStart(specPath, taskFile), CancellationToken.None));
        services.Confirmation.SetupGet(item => item.CanAsk).Returns(false);

        var again = new StringWriter();
        var exitCode = await services.Application(_fixture.Store, again).RunAsync(TaskStart(specPath, taskFile), CancellationToken.None);
        var replace = new StringWriter();
        var replaceExit = await services.Application(_fixture.Store, replace).RunAsync(TaskStart(specPath, taskFile) with { Replace = true }, CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Contains("task_exists", again.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, replaceExit);
        Assert.Contains("replace_not_approved", replace.ToString(), StringComparison.Ordinal);
        services.Examiner.Verify(item => item.ProposeAsync(It.IsAny<ExaminerRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunWithTask_RunsTheFrozenScenarioAndReportsASpecChangeMadeDuringTheTask()
    {
        var specPath = _fixture.WriteSpec(VerificationFixture.ValidScenario);
        _fixture.CreateAppAndMcp();
        var startServices = new VerificationFixture.Services(FailingCriterionMcp(await _fixture.WriteSealedBundleAsync(criterionPassed: false)));
        SetupExaminer(startServices);
        Assert.Equal(0, await startServices.Application(_fixture.Store, new StringWriter()).RunAsync(TaskStart(specPath, WriteTask("Keep the text.")), CancellationToken.None));

        File.WriteAllText(specPath, File.ReadAllText(specPath).Replace("\"expected\": \"hello\"", "\"expected\": \"hello!\"", StringComparison.Ordinal));
        var runServices = new VerificationFixture.Services(new FakeMcp(await _fixture.WriteSealedBundleAsync(criterionPassed: true)) { CheckResponse = _ => FakeMcp.Passed("x") });
        var exitCode = await runServices.Application(_fixture.Store, new StringWriter())
            .RunAsync(Run(specPath) with { TaskId = "keep-text" }, CancellationToken.None);

        Assert.Equal(0, exitCode);
        var verdict = _fixture.ReadVerdict();
        Assert.Equal(["save-text", "task-keep-text"], verdict.GetProperty("scenarios").EnumerateArray().Select(item => item.GetProperty("id").GetString()));
        var task = verdict.GetProperty("task");
        Assert.True(task.GetProperty("specChanged").GetBoolean());
        Assert.Equal("scenario 'save-text' changed", task.GetProperty("specChanges")[0].GetString());
        Assert.Equal(["textBox"], task.GetProperty("requiredAutomationIds").EnumerateArray().Select(item => item.GetString()));
    }

    [Fact]
    public async Task RunWithTask_UnknownTask_ExitsTwo()
    {
        var specPath = _fixture.WriteSpec(VerificationFixture.ValidScenario);
        var output = new StringWriter();

        var exitCode = await new VerificationFixture.Services().Application(_fixture.Store, output)
            .RunAsync(Run(specPath) with { TaskId = "nope" }, CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Contains("task_not_found", output.ToString(), StringComparison.Ordinal);
    }

    private CliCommand Run(string specPath) => new("verify", SpecPath: specPath, McpExecutablePath: _fixture.McpPath, VerifyAction: "run");

    private static CliCommand Status(string specPath) => new("verify", SpecPath: specPath, VerifyAction: "status");

    private static CliCommand Trust(string specPath) => new("verify", SpecPath: specPath, VerifyAction: "trust");

    private CliCommand TaskStart(string specPath, string taskFile) =>
        new("verify", SpecPath: specPath, McpExecutablePath: _fixture.McpPath, VerifyAction: "task-start", TaskFile: taskFile);

    private static void Git(string directory, params string[] arguments)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = System.Diagnostics.Process.Start(startInfo)!;
        process.WaitForExit();
    }

    private string WriteTask(string text)
    {
        var path = Path.Combine(_fixture.Root, "tasks", "keep-text.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private void TrustGates(string specPath)
    {
        var spec = VerificationSpecLoader.Load(specPath);
        _fixture.Store.WriteTrust(new SpecTrust(1, _fixture.Root, SpecDigests.CommandsSha256(spec), [], DateTimeOffset.UtcNow));
    }

    private static FakeMcp FailingCriterionMcp(string bundle) => new(bundle)
    {
        CheckResponse = args => args.GetProperty("expectFailure").GetBoolean() ? FakeMcp.Passed(string.Empty) : FakeMcp.Failed(string.Empty),
    };

    private static void SetupExaminer(VerificationFixture.Services services, string steps = TaskScenarioSteps)
    {
        using var document = JsonDocument.Parse(steps);
        var proposal = new ExaminerProposal(["The text box keeps the typed text."], document.RootElement.Clone(), ["textBox"], "Checks the text box.");
        services.Examiner.Setup(item => item.ProposeAsync(It.IsAny<ExaminerRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(proposal);
    }

    private static VerificationScenario TaskScenario() =>
        VerificationSpecLoader.ParseScenario($$"""{ "id": "task-t", "criteria": ["Kept."], "steps": {{TaskScenarioSteps}} }""", "task");

    private static VerificationScenarioResult Result(VerificationScenario scenario, params string[] statuses) => new(
        scenario.Id,
        VerificationStatus.Fail,
        "failed",
        [],
        statuses.Select((status, index) => new VerificationStepResult(
            index,
            scenario.Steps[index].Kind,
            status,
            "step",
            status != VerificationStatus.Fail ? null : scenario.Steps[index] is VerificationStep.Check ? "CheckFailed" : "ElementNotFound")).ToArray(),
        null,
        true,
        null,
        []);
}
