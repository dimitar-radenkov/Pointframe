using System.IO;
using System.Text.Json;
using Moq;
using Pointframe.Cli;
using Xunit;

namespace Pointframe.Tests.Cli;

public sealed class VerificationHookTests : IDisposable
{
    private const string Gates = """[ { "id": "build", "run": "dotnet build" }, { "id": "extra", "run": "cargo test --workspace" } ]""";

    private readonly VerificationFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task Stop_ProjectWithoutSpec_AllowsSilently()
    {
        var output = new StringWriter();
        var input = HookInput("s1", Path.Combine(_fixture.Root, "elsewhere"));

        var exitCode = await new VerificationFixture.Services().Application(_fixture.Store, output, input).RunAsync(Hook(), CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, output.ToString());
    }

    [Fact]
    public async Task Stop_Pass_AllowsAndReusesTheVerdictWhileTheTreeIsUnchanged()
    {
        _fixture.WriteSpec(VerificationFixture.ValidScenario);
        _fixture.CreateAppAndMcp();
        var services = new VerificationFixture.Services(new FakeMcp(await _fixture.WriteSealedBundleAsync(criterionPassed: true)));

        var first = await StopAsync(services, "s1");
        var second = await StopAsync(services, "s1");

        Assert.Contains("pass on tree TREE1", first.GetProperty("systemMessage").GetString(), StringComparison.Ordinal);
        Assert.False(first.TryGetProperty("decision", out _));
        Assert.Contains("pass", second.GetProperty("systemMessage").GetString(), StringComparison.Ordinal);
        Assert.Single(services.Launches);
        AssertLastStop("verified", null, null);
    }

    [Fact]
    public async Task Stop_FailingScenario_BlocksWithWhatWasFoundAndStopsBlockingAfterTheLimit()
    {
        _fixture.WriteSpec(VerificationFixture.ValidScenario);
        _fixture.CreateAppAndMcp();
        var mcp = new FakeMcp(await _fixture.WriteSealedBundleAsync(criterionPassed: false)) { CheckResponse = _ => FakeMcp.Failed("hullo") };
        var services = new VerificationFixture.Services(mcp);

        var first = await StopAsync(services, "s1", maxBlocks: 2);
        var second = await StopAsync(services, "s1", maxBlocks: 2);
        var third = await StopAsync(services, "s1", maxBlocks: 2);

        Assert.Equal("block", first.GetProperty("decision").GetString());
        var reason = first.GetProperty("reason").GetString()!;
        Assert.Contains("block 1 of 2", reason, StringComparison.Ordinal);
        Assert.Contains("Scenario 'save-text' failed at step 2", reason, StringComparison.Ordinal);
        Assert.Contains("Expected 'hello', found 'hullo'", reason, StringComparison.Ordinal);
        Assert.Equal("block", second.GetProperty("decision").GetString());
        Assert.False(third.TryGetProperty("decision", out _));
        var stoppedMessage = third.GetProperty("systemMessage").GetString()!;
        Assert.Contains("still fails after 2 blocked attempts", stoppedMessage, StringComparison.Ordinal);
        Assert.Contains("this work was NOT verified", stoppedMessage, StringComparison.Ordinal);
        Assert.StartsWith("UNVERIFIED:", stoppedMessage, StringComparison.Ordinal);
        Assert.Contains("pointframe verify status", stoppedMessage, StringComparison.Ordinal);
        Assert.Single(services.Launches);
        AssertLastStop("unverified", "block_limit", null);
    }

    [Fact]
    public async Task Stop_NewSession_StartsCountingAgain()
    {
        _fixture.WriteSpec(VerificationFixture.ValidScenario);
        _fixture.CreateAppAndMcp();
        var mcp = new FakeMcp(await _fixture.WriteSealedBundleAsync(criterionPassed: false)) { CheckResponse = _ => FakeMcp.Failed("x") };
        var services = new VerificationFixture.Services(mcp);
        await StopAsync(services, "s1", maxBlocks: 1);

        var otherSession = await StopAsync(services, "s2", maxBlocks: 1);

        Assert.Equal("block", otherSession.GetProperty("decision").GetString());
    }

    [Fact]
    public async Task Stop_ChangedFiles_RunTheVerificationAgain()
    {
        _fixture.WriteSpec(VerificationFixture.ValidScenario);
        _fixture.CreateAppAndMcp();
        var services = new VerificationFixture.Services(new FakeMcp(await _fixture.WriteSealedBundleAsync(criterionPassed: true)));
        await StopAsync(services, "s1");

        services.CurrentTree = "TREE2";
        var after = await StopAsync(services, "s1");

        Assert.Contains("pass on tree TREE2", after.GetProperty("systemMessage").GetString(), StringComparison.Ordinal);
        Assert.Equal(2, services.Launches.Count);
    }

    [Fact]
    public async Task Stop_GatesNotApproved_LetsTheAgentStopAndTellsThePerson()
    {
        _fixture.WriteUntrustedSpec(VerificationFixture.DefaultApp, Gates);
        var services = new VerificationFixture.Services
        {
            Approver = new Mock<ICommandApprover>(),
        };
        services.Approver.Setup(item => item.ReviewAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApprovalDecision(false, "Unclear command.", [], "fake"));

        var result = await StopAsync(services, "s1");

        Assert.False(result.TryGetProperty("decision", out _));
        Assert.Contains("needs you (spec_untrusted)", result.GetProperty("systemMessage").GetString(), StringComparison.Ordinal);
        Assert.StartsWith("UNVERIFIED:", result.GetProperty("systemMessage").GetString(), StringComparison.Ordinal);
        AssertLastStop("unverified", "needs_person", "spec_untrusted");
    }

    [Fact]
    public async Task Stop_HookCrashStillReturnsMessageWhenLastStopCannotBeWritten()
    {
        _fixture.WriteSpec(VerificationFixture.ValidScenario);
        var services = new VerificationFixture.Services();
        services.WorkingTree.Setup(item => item.Read(It.IsAny<string>())).Throws(new IOException("tree read failed"));
        var output = new StringWriter();
        var code = await services.Application(_fixture.Store, output, HookInput("s1", _fixture.Root))
            .RunAsync(Hook(), CancellationToken.None);
        Assert.Equal(0, code);
        using var document = JsonDocument.Parse(output.ToString());
        Assert.StartsWith("UNVERIFIED:", document.RootElement.GetProperty("systemMessage").GetString(), StringComparison.Ordinal);
        Assert.Contains("hook failed", document.RootElement.GetProperty("systemMessage").GetString(), StringComparison.Ordinal);
    }

    private void AssertLastStop(string outcome, string? reason, string? errorCode)
    {
        using var record = JsonDocument.Parse(File.ReadAllText(Path.Combine(_fixture.Root, "artifacts", "pointframe-verify", VerificationHook.LastStopFileName)));
        Assert.Equal(1, record.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(outcome, record.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(reason, record.RootElement.GetProperty("reason").ValueKind == JsonValueKind.Null ? null : record.RootElement.GetProperty("reason").GetString());
        Assert.Equal(errorCode, record.RootElement.GetProperty("errorCode").ValueKind == JsonValueKind.Null ? null : record.RootElement.GetProperty("errorCode").GetString());
        Assert.True(record.RootElement.TryGetProperty("utc", out _));
    }

    [Fact]
    public async Task Stop_ApproverUnavailable_LetsTheAgentStopAndTellsThePerson()
    {
        _fixture.WriteUntrustedSpec(VerificationFixture.DefaultApp, Gates);

        var result = await StopAsync(new VerificationFixture.Services(), "s1");

        Assert.False(result.TryGetProperty("decision", out _));
        Assert.Contains("needs you (approver_unavailable)", result.GetProperty("systemMessage").GetString(), StringComparison.Ordinal);
        Assert.Contains("outside the standard set", result.GetProperty("systemMessage").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stop_FailedGate_BlocksWithTheGateDetails()
    {
        _fixture.WriteSpecWith(VerificationFixture.DefaultApp, Gates);
        var spec = VerificationSpecLoader.Load(_fixture.SpecPath);
        _fixture.Store.WriteTrust(new SpecTrust(1, _fixture.Root, SpecDigests.CommandsSha256(spec), [], DateTimeOffset.UtcNow));
        var services = new VerificationFixture.Services();
        services.Commands.Setup(item => item.RunAsync("dotnet build", It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommandResult(1, false, ["Form1.cs(3,1): error CS0103: The name 'x' does not exist"]));

        var result = await StopAsync(services, "s1");

        Assert.Equal("block", result.GetProperty("decision").GetString());
        Assert.Contains("Gate 'build' failed", result.GetProperty("reason").GetString(), StringComparison.Ordinal);
        Assert.Contains("error CS0103", result.GetProperty("reason").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stop_ActiveTask_IsVerifiedWithoutTheAgentNamingIt()
    {
        _fixture.WriteSpec(VerificationFixture.ValidScenario);
        _fixture.CreateAppAndMcp();
        var start = new VerificationFixture.Services(new FakeMcp(await _fixture.WriteSealedBundleAsync(criterionPassed: false))
        {
            CheckResponse = args => args.GetProperty("expectFailure").GetBoolean() ? FakeMcp.Passed(string.Empty) : FakeMcp.Failed(string.Empty),
        });
        SetupExaminer(start);
        var taskFile = Path.Combine(_fixture.Root, "keep-text.md");
        File.WriteAllText(taskFile, "Keep the text.");
        Assert.Equal(0, await start.Application(_fixture.Store, new StringWriter()).RunAsync(
            new CliCommand("verify", SpecPath: _fixture.SpecPath, McpExecutablePath: _fixture.McpPath, VerifyAction: "task-start", TaskFile: taskFile), CancellationToken.None));
        var services = new VerificationFixture.Services(new FakeMcp(await _fixture.WriteSealedBundleAsync(criterionPassed: true)));

        await StopAsync(services, "s1");

        var verdict = _fixture.ReadVerdict();
        Assert.Equal("keep-text", verdict.GetProperty("task").GetProperty("id").GetString());
        Assert.Contains(verdict.GetProperty("scenarios").EnumerateArray(), scenario => scenario.GetProperty("id").GetString() == "task-keep-text");
    }

    [Fact]
    public async Task Stop_PassWithReview_RunsTheReviewerOnceAndReportsItsFlags()
    {
        _fixture.WriteSpec(VerificationFixture.ValidScenario);
        _fixture.CreateAppAndMcp();
        var services = new VerificationFixture.Services(new FakeMcp(await _fixture.WriteSealedBundleAsync(criterionPassed: true)));
        services.Reviewer.Setup(item => item.ReviewAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Review("A test was weakened.", [new ReviewFlag("Tests/A.cs", 12, "high", "Assertion removed.")], "fake"));

        var first = await StopAsync(services, "s1", review: true);
        await StopAsync(services, "s1", review: true);

        Assert.Contains("1 flag(s), 1 high", first.GetProperty("systemMessage").GetString(), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(_fixture.Root, "artifacts", "pointframe-verify", VerificationHook.ReviewFileName)));
        services.Reviewer.Verify(
            item => item.ReviewAsync(It.IsAny<string>(), "diff --git a/Form1.cs b/Form1.cs", It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Stop_ReviewerRetriesAgentFailureThenSucceeds()
    {
        _fixture.WriteSpec(VerificationFixture.ValidScenario);
        _fixture.CreateAppAndMcp();
        var services = new VerificationFixture.Services(new FakeMcp(await _fixture.WriteSealedBundleAsync(criterionPassed: true)));
        services.Reviewer.SetupSequence(item => item.ReviewAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AgentException("invalid structured output"))
            .ReturnsAsync(new Review("Reviewed after retry.", [], "fake"));

        var result = await StopAsync(services, "s1", review: true);

        Assert.Contains("Reviewed after retry.", result.GetProperty("systemMessage").GetString(), StringComparison.Ordinal);
        services.Reviewer.Verify(item => item.ReviewAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Stop_TwoReviewerFailuresAreRecordedAndNotRepeatedForTheTree()
    {
        _fixture.WriteSpec(VerificationFixture.ValidScenario);
        _fixture.CreateAppAndMcp();
        var services = new VerificationFixture.Services(new FakeMcp(await _fixture.WriteSealedBundleAsync(criterionPassed: true)));
        services.Reviewer.Setup(item => item.ReviewAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AgentException("invalid structured output"));

        var result = await StopAsync(services, "s1", review: true);
        await StopAsync(services, "s1", review: true);

        Assert.Contains("verification passed, but the work was not reviewed", result.GetProperty("systemMessage").GetString(), StringComparison.Ordinal);
        using var record = JsonDocument.Parse(File.ReadAllText(Path.Combine(_fixture.Root, "artifacts", "pointframe-verify", VerificationHook.ReviewFileName)));
        Assert.True(record.RootElement.GetProperty("failed").GetBoolean());
        Assert.Equal("TREE1", record.RootElement.GetProperty("treeHash").GetString());
        Assert.Equal("HEAD1", record.RootElement.GetProperty("baseCommit").GetString());
        services.Reviewer.Verify(item => item.ReviewAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Stop_BrokenSpec_LetsTheAgentStopAndTellsThePerson()
    {
        _fixture.WriteSpec("""{ "id": "s", "steps": [] }""");

        var result = await StopAsync(new VerificationFixture.Services(), "s1");

        Assert.False(result.TryGetProperty("decision", out _));
        Assert.Contains("(spec_invalid), so this work was NOT verified", result.GetProperty("systemMessage").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stop_PassFromTheAgentsOwnRun_IsStillReviewed()
    {
        _fixture.WriteSpec(VerificationFixture.ValidScenario);
        _fixture.CreateAppAndMcp();
        var services = new VerificationFixture.Services(new FakeMcp(await _fixture.WriteSealedBundleAsync(criterionPassed: true)));
        services.Reviewer.Setup(item => item.ReviewAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Review("Fine.", [], "fake"));
        await services.Application(_fixture.Store, new StringWriter())
            .RunAsync(new CliCommand("verify", SpecPath: _fixture.SpecPath, McpExecutablePath: _fixture.McpPath, VerifyAction: "run"), CancellationToken.None);

        var result = await StopAsync(services, "s1", review: true);

        Assert.Contains("Reviewer: no flags", result.GetProperty("systemMessage").GetString(), StringComparison.Ordinal);
        Assert.Single(services.Launches);
    }

    [Fact]
    public async Task Stop_TaskReplacedUnderTheSameId_DoesNotReuseThePassOnTheOldCriteria()
    {
        _fixture.WriteSpec(VerificationFixture.ValidScenario);
        _fixture.CreateAppAndMcp();
        var taskFile = Path.Combine(_fixture.Root, "keep-text.md");
        File.WriteAllText(taskFile, "Keep the text.");
        var start = new VerificationFixture.Services(new FakeMcp(await _fixture.WriteSealedBundleAsync(criterionPassed: false))
        {
            CheckResponse = args => args.GetProperty("expectFailure").GetBoolean() ? FakeMcp.Passed(string.Empty) : FakeMcp.Failed(string.Empty),
        });
        SetupExaminer(start);
        var taskStart = new CliCommand("verify", SpecPath: _fixture.SpecPath, McpExecutablePath: _fixture.McpPath, VerifyAction: "task-start", TaskFile: taskFile);
        Assert.Equal(0, await start.Application(_fixture.Store, new StringWriter()).RunAsync(taskStart, CancellationToken.None));
        var services = new VerificationFixture.Services(new FakeMcp(await _fixture.WriteSealedBundleAsync(criterionPassed: true)));
        await StopAsync(services, "s1");

        start.Confirmation.SetupGet(item => item.CanAsk).Returns(true);
        start.Confirmation.Setup(item => item.Confirm(It.IsAny<string>())).Returns(true);
        File.WriteAllText(taskFile, "Keep the text, stricter.");
        Assert.Equal(0, await start.Application(_fixture.Store, new StringWriter()).RunAsync(taskStart with { Replace = true }, CancellationToken.None));
        await StopAsync(services, "s1");

        // Two runs of two scenarios each (the spec's and the task's): the second stop did not reuse the first pass.
        Assert.Equal(4, services.Launches.Count);
    }

    [Theory]
    [InlineData(new[] { "verify", "hook", "stop", "--review", "--max-blocks", "3" }, true, 3)]
    [InlineData(new[] { "verify", "hook", "stop" }, false, null)]
    public void TryParse_HookStop_ParsesOptions(string[] args, bool review, int? maxBlocks)
    {
        var parsed = CliCommandParser.TryParse(args, out var command, out var error);

        Assert.True(parsed, error);
        Assert.Equal("hook-stop", command.VerifyAction);
        Assert.Equal(review, command.Review);
        Assert.Equal(maxBlocks, command.MaxBlocks);
    }

    [Theory]
    [InlineData(new[] { "verify", "hook" }, "The verify hook command requires: hook stop.")]
    [InlineData(new[] { "verify", "hook", "stop", "--max-blocks", "0" }, "The verify option --max-blocks takes a whole number from 1 through 50.")]
    [InlineData(new[] { "verify", "hook", "stop", "--only", "gates" }, "Unrecognized verify hook option '--only'.")]
    public void TryParse_InvalidHook_ReturnsUsageError(string[] args, string expectedError)
    {
        var parsed = CliCommandParser.TryParse(args, out _, out var error);

        Assert.False(parsed);
        Assert.Equal(expectedError, error);
    }

    [Fact]
    public void ReviewerAnswer_FlagsAreParsed()
    {
        using var document = JsonDocument.Parse("""{ "summary": "One problem.", "flags": [ { "file": "A.cs", "line": 3, "severity": "high", "message": "Test removed." }, { "file": "B.cs", "line": 0, "severity": "low", "message": "Unused." } ] }""");

        var review = AgentReviewer.ParseReview(document.RootElement, "codex");

        Assert.Equal("One problem.", review.Summary);
        Assert.Equal(3, review.Flags[0].Line);
        Assert.Null(review.Flags[1].Line);
        Assert.Equal("codex", review.Reviewer);
    }

    [Fact]
    public void ReviewerAnswer_LeftoverMarkupIsCutFromTheSummary()
    {
        using var document = JsonDocument.Parse("""{ "summary": "No flags.</parameter>\n</invoke>\n", "flags": [] }""");

        Assert.Equal("No flags.", AgentReviewer.ParseReview(document.RootElement, "claude-code").Summary);
    }

    [Fact]
    public async Task Reviewer_GetsNoMcpServer()
    {
        var runner = new Mock<IAgentRunner>();
        AgentRequest? sent = null;
        using var answer = JsonDocument.Parse("""{ "summary": "Fine.", "flags": [] }""");
        runner.Setup(item => item.RunAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>()))
            .Callback<AgentRequest, CancellationToken>((request, _) => sent = request)
            .ReturnsAsync(answer.RootElement.Clone());

        await new AgentReviewer(runner.Object).ReviewAsync("task", "diff", "pass", CancellationToken.None);

        Assert.Equal("reviewer", sent!.Role);
        Assert.Null(sent.McpServer);
        Assert.Contains("diff", sent.Input, StringComparison.Ordinal);
    }

    private async Task<JsonElement> StopAsync(VerificationFixture.Services services, string sessionId, int? maxBlocks = null, bool review = false)
    {
        var output = new StringWriter();
        var exitCode = await services.Application(_fixture.Store, output, HookInput(sessionId, _fixture.Root))
            .RunAsync(Hook() with { McpExecutablePath = _fixture.McpPath, MaxBlocks = maxBlocks, Review = review }, CancellationToken.None);
        Assert.Equal(0, exitCode);
        using var document = JsonDocument.Parse(output.ToString());
        return document.RootElement.Clone();
    }

    private static CliCommand Hook() => new("verify", VerifyAction: "hook-stop");

    private static string HookInput(string sessionId, string cwd) =>
        JsonSerializer.Serialize(new { session_id = sessionId, cwd, hook_event_name = "Stop", stop_hook_active = false });

    private static void SetupExaminer(VerificationFixture.Services services)
    {
        using var document = JsonDocument.Parse("""
            [
              { "enterText": { "automationId": "textBox", "text": "keep me" } },
              { "check": { "kind": "textEquals", "automationId": "textBox", "expected": "keep me", "criterion": "C1" } },
              { "check": { "kind": "textEquals", "automationId": "textBox", "expected": "not this", "expectFailure": true } }
            ]
            """);
        services.Examiner.Setup(item => item.ProposeAsync(It.IsAny<ExaminerRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExaminerProposal(["The text box keeps the typed text."], document.RootElement.Clone(), [], null));
    }
}
