using System.IO;
using System.Text.Json;
using Pointframe.Cli;
using Xunit;

namespace Pointframe.Tests.Cli;

public sealed class AgentRunnerTests : IDisposable
{
    private readonly VerificationFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void Claude_WithDesktopTools_AllowsOnlyThatServer()
    {
        var arguments = ClaudeCodeAgentRunner.Arguments(Request(withMcp: true), "C:/work/examiner-mcp.json").ToList();

        Assert.Equal(string.Empty, Value(arguments, "--tools"));
        Assert.Contains("--strict-mcp-config", arguments);
        Assert.Equal("C:/work/examiner-mcp.json", Value(arguments, "--mcp-config"));
        Assert.Equal("mcp__pointframe", Value(arguments, "--allowedTools"));
        Assert.Equal("dontAsk", Value(arguments, "--permission-mode"));
        Assert.Equal("user", Value(arguments, "--setting-sources"));
        Assert.Equal("Be the examiner.", Value(arguments, "--system-prompt"));
        Assert.Equal("3", Value(arguments, "--max-budget-usd"));
    }

    [Fact]
    public void Claude_WithoutDesktopTools_GetsNoMcpServer()
    {
        var arguments = ClaudeCodeAgentRunner.Arguments(Request(withMcp: false), null).ToList();

        Assert.DoesNotContain("--mcp-config", arguments);
        Assert.DoesNotContain("--allowedTools", arguments);
        Assert.Contains("--strict-mcp-config", arguments);
    }

    [Theory]
    [InlineData("""{ "type": "result", "structured_output": { "a": 1 } }""")]
    [InlineData("""{ "type": "result", "result": "```json\n{ \"a\": 1 }\n```" }""")]
    public void Claude_AnswerIsReadFromItsEnvelope(string output)
    {
        Assert.Equal(1, ClaudeCodeAgentRunner.ParseEnvelope(output, "reviewer").GetProperty("a").GetInt32());
    }

    [Theory]
    [InlineData("""{ "is_error": true, "result": "budget exceeded" }""", "budget exceeded")]
    [InlineData("""{ "type": "result" }""", "no structured output")]
    [InlineData("not json", "not valid JSON")]
    public void Claude_ErrorOrMissingAnswer_IsAnAgentError(string output, string expected)
    {
        var exception = Assert.Throws<AgentException>(() => ClaudeCodeAgentRunner.ParseEnvelope(output, "reviewer"));

        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Codex_RunsReadOnlyWithoutPromptsAndGetsTheServerThroughConfigOverrides()
    {
        var arguments = CodexAgentRunner.Arguments(Request(withMcp: true), "C:/work/schema.json", "C:/work/answer.json").ToList();

        Assert.Equal(["--ask-for-approval", "never", "exec", "-"], arguments.Take(4));
        Assert.Equal("C:/work/schema.json", Value(arguments, "--output-schema"));
        Assert.Equal("C:/work/answer.json", Value(arguments, "-o"));
        Assert.Equal("read-only", Value(arguments, "--sandbox"));
        Assert.Contains("--skip-git-repo-check", arguments);
        Assert.Contains("--ephemeral", arguments);
        Assert.Equal("C:/work", Value(arguments, "-C"));
        Assert.Contains(@"mcp_servers.pointframe.command='C:\tools\Pointframe.Mcp.exe'", arguments);
        Assert.Contains("mcp_servers.pointframe.default_tools_approval_mode='approve'", arguments);
        Assert.Contains(@"mcp_servers.pointframe.args=['--desktop-testing', '--desktop-policy', 'C:\work\policy.json']", arguments);
        Assert.Contains(@"mcp_servers.pointframe.env={ APP_DATA = 'C:\work\data' }", arguments);
    }

    [Fact]
    public void Codex_InstructionsLeadThePrompt()
    {
        var prompt = CodexAgentRunner.Prompt(Request(withMcp: false));

        Assert.True(prompt.IndexOf("Be the examiner.", StringComparison.Ordinal) < prompt.IndexOf("The task: keep the text.", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(@"C:\a\b", @"'C:\a\b'")]
    [InlineData("it's", "\"it's\"")]
    [InlineData("say \"hi\" it's", "\"say \\\"hi\\\" it's\"")]
    public void Codex_TomlStrings_KeepWindowsPathsAndEscapeQuotes(string value, string expected)
    {
        Assert.Equal(expected, CodexAgentRunner.TomlString(value));
    }

    [Fact]
    public async Task Command_AnswerOnStdout_IsRead()
    {
        var runner = new CommandAgentRunner("cmd.exe", ["/d", "/c", "type", "{input_file}"]);
        var request = Request(withMcp: false) with { Input = """{ "approve": true }""", WorkDirectory = Path.Combine(_fixture.Root, "agent") };

        var answer = await runner.RunAsync(request, CancellationToken.None);

        Assert.True(answer.GetProperty("approve").GetBoolean());
    }

    [Fact]
    public async Task Command_AnswerInTheOutputFile_IsPreferredAndPlaceholdersAreFilled()
    {
        var work = Path.Combine(_fixture.Root, "agent");
        var runner = new CommandAgentRunner("cmd.exe", ["/d", "/c", "copy", "/y", "{schema_file}", "{output_file}"]);
        var request = Request(withMcp: true) with { OutputSchema = """{ "type": "object", "marker": 7 }""", WorkDirectory = work };

        var answer = await runner.RunAsync(request, CancellationToken.None);

        Assert.Equal(7, answer.GetProperty("marker").GetInt32());
        using var mcp = JsonDocument.Parse(File.ReadAllText(Path.Combine(work, "examiner-mcp.json")));
        Assert.Equal(@"C:\tools\Pointframe.Mcp.exe", mcp.RootElement.GetProperty("mcpServers").GetProperty("pointframe").GetProperty("command").GetString());
    }

    [Fact]
    public async Task Command_FailingCommand_IsAnAgentError()
    {
        var runner = new CommandAgentRunner("cmd.exe", ["/d", "/c", "exit", "3"]);

        var exception = await Assert.ThrowsAsync<AgentException>(() =>
            runner.RunAsync(Request(withMcp: false) with { WorkDirectory = Path.Combine(_fixture.Root, "agent") }, CancellationToken.None));

        Assert.Contains("exited with 3", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("command", null, "names no command")]
    [InlineData("gemini", null, "Unknown agent 'gemini'")]
    public void Selection_InvalidSettings_SayWhatIsWrong(string agent, string? command, string expected)
    {
        var (runner, description) = AgentSelection.Create(new AgentSettings(agent, command));

        Assert.Null(runner);
        Assert.Contains(expected, description, StringComparison.Ordinal);
    }

    [Fact]
    public void Selection_CustomCommand_IsUsed()
    {
        var (runner, _) = AgentSelection.Create(new AgentSettings("command", @"C:\tools\my-agent.exe", ["--prompt", "{prompt_file}"]));

        Assert.Equal("command:my-agent", runner!.Name);
    }

    [Fact]
    public void Selection_IsStoredInTheProfileNotTheProject()
    {
        AgentSelection.Write(_fixture.Store, new AgentSettings("codex"));

        Assert.Equal("codex", AgentSelection.Read(_fixture.Store)!.Agent);
        Assert.StartsWith(_fixture.Store.BaseDirectory, Path.Combine(_fixture.Store.BaseDirectory, AgentSelection.FileName), StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyAgent_UsePinsTheAgentAndAutoReturnsToDetection()
    {
        var services = new VerificationFixture.Services();
        var output = new StringWriter();

        await services.Application(_fixture.Store, output).RunAsync(new CliCommand("verify", VerifyAction: "agent", UseAgent: "codex"), CancellationToken.None);
        var pinned = AgentSelection.Read(_fixture.Store);
        await services.Application(_fixture.Store, new StringWriter()).RunAsync(new CliCommand("verify", VerifyAction: "agent", UseAgent: "auto"), CancellationToken.None);

        Assert.Equal("codex", pinned!.Agent);
        Assert.Contains("\"selected\": \"codex\"", output.ToString(), StringComparison.Ordinal);
        Assert.Null(AgentSelection.Read(_fixture.Store));
    }

    [Theory]
    [InlineData(new[] { "verify", "agent" }, null)]
    [InlineData(new[] { "verify", "agent", "--use", "Codex" }, "codex")]
    public void TryParse_VerifyAgent_ParsesCommand(string[] args, string? use)
    {
        Assert.True(CliCommandParser.TryParse(args, out var command, out var error), error);
        Assert.Equal("agent", command.VerifyAction);
        Assert.Equal(use, command.UseAgent);
    }

    [Fact]
    public void TryParse_VerifyAgentUnknown_IsRejected()
    {
        Assert.False(CliCommandParser.TryParse(["verify", "agent", "--use", "gemini"], out _, out var error));
        Assert.Contains("claude, codex, or auto", error, StringComparison.Ordinal);
    }

    private static AgentRequest Request(bool withMcp) => new(
        "examiner",
        "Be the examiner.",
        "The task: keep the text.",
        """{ "type": "object" }""",
        "C:/work",
        withMcp
            ? new AgentMcpServer("pointframe", @"C:\tools\Pointframe.Mcp.exe", ["--desktop-testing", "--desktop-policy", @"C:\work\policy.json"], new Dictionary<string, string> { ["APP_DATA"] = @"C:\work\data" })
            : null,
        3m);

    private static string Value(List<string> arguments, string flag) => arguments[arguments.IndexOf(flag) + 1];
}
