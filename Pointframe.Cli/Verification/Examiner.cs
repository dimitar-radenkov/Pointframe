using System.Diagnostics;
using System.Text.Json;

namespace Pointframe.Cli;

internal sealed record ExaminerRequest(
    string TaskText,
    string ProfileId,
    string McpExecutablePath,
    string PolicyPath,
    string WorkDirectory,
    IReadOnlyDictionary<string, string> Environment);

internal sealed record ExaminerProposal(
    IReadOnlyList<string> Criteria,
    JsonElement Steps,
    IReadOnlyList<string> RequiredAutomationIds,
    string? Notes);

internal interface IExaminer
{
    string Name { get; }

    Task<ExaminerProposal> ProposeAsync(ExaminerRequest request, CancellationToken cancellationToken);
}

internal sealed class ExaminerException(string message) : Exception(message);

// The examiner is a separate Claude Code process that sees only the task text and the running app. It runs
// in an empty folder outside the repository with every built-in tool disabled (it cannot read the code)
// and with only the Pointframe MCP server, and it loads no project settings, so the worker's hooks never
// run inside it. Its proposal is checked by the CLI, never trusted: see FailBefore.
internal sealed class ClaudeCodeExaminer(Func<string?> resolveExecutable, decimal maxBudgetUsd = 3m) : IExaminer
{
    internal const string ServerName = "pointframe";

    internal const string OutputSchema = """
        {
          "type": "object",
          "properties": {
            "criteria": { "type": "array", "items": { "type": "string" }, "minItems": 1, "maxItems": 5 },
            "steps": { "type": "array", "items": { "type": "object" }, "minItems": 1 },
            "requiredAutomationIds": { "type": "array", "items": { "type": "string" } },
            "notes": { "type": "string" }
          },
          "required": ["criteria", "steps", "requiredAutomationIds", "notes"]
        }
        """;

    public string Name => "claude-code";

    internal static string SystemPrompt(string profileId) => $$$"""
        You are the examiner for a change to a Windows desktop app. Another agent will implement the change.
        You write the acceptance test it is graded by, before any code exists. You never see the code.

        Your only tools are the Pointframe MCP server's desktop_* tools. The app's policy profile id is '{{{profileId}}}'.
        Start a session with desktop_start_test_session (profileId '{{{profileId}}}', no criteria), explore with
        desktop_observe_app (includeUiAutomation true, includeImages false; call list_displays first for the
        capture rectangles), and end every session with desktop_end_test_session. Never call desktop_check_ui:
        every check is recorded, and you are only exploring.

        1. Restate the task as observable behaviour: what a user does, and what they then see in the app.
        2. Write 1 to 5 criteria. Each is one observable outcome, testable through the UI, free of
           implementation detail. Cover the main path and the one edge case the task's wording implies
           (for example "after a restart" or "when empty"). Write each criterion as plain text with no id
           prefix: they are numbered C1, C2, ... by their order.
        3. Use the automation ids you observed. Where the task needs an element that does not exist yet, name
           the automation id it must have (camelCase, like the existing ones) and list it in
           requiredAutomationIds, so the implementer knows the contract.
        4. Write the steps. Each step is an object with exactly one property:
           {"enterText": {"automationId": "...", "text": "..."}}
           {"invoke": {"automationId": "..."}}          (buttons, menu items; use the app's own Close or Exit to close it)
           {"pressKeys": {"keys": [17, 83]}}            (Windows virtual-key codes, modifiers first)
           {"restart": {}}                              (relaunch; close the app with an invoke first)
           {"check": {"kind": "textEquals", "automationId": "...", "expected": "...", "criterion": "C1"}}
           Instead of automationId an element may be {"role": "Button", "name": "Save"}. Check kinds: exists,
           absent, enabled (expected true/false), toggleEquals (expected on/off), selectionEquals (expected
           selected/notSelected), textEquals. Optional "timeoutSeconds" from 1 to 30.
           Criteria are numbered C1, C2, ... in your order. Every criterion needs a check naming it. Add at
           least one negative control: a check with "expectFailure": true and a deliberately wrong expected
           value, and no "criterion".
        5. Your criteria will be run on the current, unchanged app. Each criterion check must fail there,
           because the behaviour does not exist yet; a criterion that already holds does not test the change
           and makes your proposal rejected. Negative controls must pass there.
        6. Do not write criteria to be easy or hard to pass. Do not guess the implementation.
        7. In notes, say in two or three sentences what each criterion checks and what is out of scope.

        Return only the structured output: criteria, steps, requiredAutomationIds, notes.
        """;

    public async Task<ExaminerProposal> ProposeAsync(ExaminerRequest request, CancellationToken cancellationToken)
    {
        var executable = resolveExecutable()
            ?? throw new ExaminerException("Claude Code was not found. Install it (npm install -g @anthropic-ai/claude-code) or put claude.exe on the PATH.");
        Directory.CreateDirectory(request.WorkDirectory);
        var mcpConfigPath = Path.Combine(request.WorkDirectory, "examiner-mcp.json");
        await File.WriteAllTextAsync(mcpConfigPath, JsonSerializer.Serialize(new
        {
            mcpServers = new Dictionary<string, object>
            {
                [ServerName] = new { command = request.McpExecutablePath, args = new[] { "--desktop-testing", "--desktop-policy", request.PolicyPath }, env = request.Environment },
            },
        }), cancellationToken);

        var (exitCode, output, error) = await RunClaudeAsync(
            executable, Arguments(request.ProfileId, mcpConfigPath, maxBudgetUsd), $"The task:\n\n{request.TaskText}", request.WorkDirectory, cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(request.WorkDirectory, "examiner-output.json"), output, cancellationToken);
        if (exitCode != 0)
        {
            throw new ExaminerException($"The examiner exited with {exitCode}: {Last(error.Length > 0 ? error : output)}");
        }

        return ParseOutput(output);
    }

    // One `claude -p` run: the prompt goes in on stdin, the json result comes back on stdout. Claude Code reads
    // and writes UTF-8; the console code page would garble non-ASCII text in both directions.
    internal static async Task<(int ExitCode, string Output, string Error)> RunClaudeAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string input,
        string workDirectory,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new System.Text.UTF8Encoding(false),
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new ExaminerException("Claude Code could not be started.");
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.StandardInput.WriteAsync(input.AsMemory(), cancellationToken);
        process.StandardInput.Close();
        var output = await outputTask;
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, output, await errorTask);
    }

    internal static IReadOnlyList<string> Arguments(string profileId, string mcpConfigPath, decimal maxBudgetUsd) =>
    [
        "-p",
        "--output-format", "json",
        "--json-schema", OutputSchema,
        "--system-prompt", SystemPrompt(profileId),
        "--mcp-config", mcpConfigPath,
        "--strict-mcp-config",
        "--tools", string.Empty,
        "--allowedTools", $"mcp__{ServerName}",
        "--permission-mode", "dontAsk",
        "--setting-sources", "user",
        "--no-session-persistence",
        "--max-budget-usd", maxBudgetUsd.ToString(System.Globalization.CultureInfo.InvariantCulture),
    ];

    // Claude Code's json output carries the schema-validated object in structured_output; older builds put
    // it as JSON text in result.
    internal static ExaminerProposal ParseOutput(string output)
    {
        try
        {
            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            if (root.TryGetProperty("is_error", out var isError) && isError.ValueKind == JsonValueKind.True)
            {
                throw new ExaminerException($"The examiner reported an error: {Last(root.TryGetProperty("result", out var message) ? message.ToString() : output)}");
            }

            JsonElement proposal;
            if (root.TryGetProperty("structured_output", out var structured) && structured.ValueKind == JsonValueKind.Object)
            {
                proposal = structured;
            }
            else if (root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.String)
            {
                using var inner = JsonDocument.Parse(StripFence(result.GetString()!));
                return FromProposal(inner.RootElement);
            }
            else
            {
                throw new ExaminerException("The examiner returned no structured output.");
            }

            return FromProposal(proposal);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new ExaminerException($"The examiner's output is not the expected JSON: {exception.Message}");
        }
    }

    internal static string? ResolveDefaultExecutable()
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var exe = Path.Combine(directory, "claude.exe");
            if (File.Exists(exe))
            {
                return exe;
            }

            // The npm shim (claude.cmd) only forwards to this exe. Starting the exe directly keeps the JSON
            // schema and prompt arguments intact; cmd.exe would re-parse their quotes.
            var npmExe = Path.Combine(directory, "node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe");
            if (File.Exists(Path.Combine(directory, "claude.cmd")) && File.Exists(npmExe))
            {
                return npmExe;
            }
        }

        return null;
    }

    private static ExaminerProposal FromProposal(JsonElement proposal) => new(
        proposal.GetProperty("criteria").EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToArray(),
        proposal.GetProperty("steps").Clone(),
        proposal.TryGetProperty("requiredAutomationIds", out var ids) ? ids.EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToArray() : [],
        proposal.TryGetProperty("notes", out var notes) ? notes.GetString() : null);

    private static string StripFence(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var firstLine = trimmed.IndexOf('\n');
        var closing = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        return firstLine > 0 && closing > firstLine ? trimmed[(firstLine + 1)..closing] : trimmed;
    }

    private static string Last(string text) => text.Length <= 600 ? text.Trim() : text[^600..].Trim();
}

internal static class FailBefore
{
    internal const string Confirmed = "confirmed";
    internal const string Rejected = "rejected";

    // The examiner's scenario on the unchanged app: every criterion must fail or be unreachable (an element
    // the task adds does not exist yet), no criterion may pass, every negative control that ran must pass,
    // and the app must have launched. A criterion that already holds does not test the change.
    internal static TaskFailBefore Evaluate(VerificationScenario scenario, VerificationScenarioResult result)
    {
        var problems = new List<string>();
        if (result.Steps.Count == 0 || result.Steps.Any(step => step.Kind == "launch" && step.Status == VerificationStatus.Fail))
        {
            problems.Add($"The app did not launch or the run failed ({string.Join(" ", result.Problems)}), so nothing was shown to fail before the change.");
        }

        // Only two failures say something about the app: a check that read a different state, and an element
        // the task is about to add. A timeout, an inconclusive check, or a failed action proves nothing.
        foreach (var step in result.Steps.Where(step => step.Index >= 0 && step.Status == VerificationStatus.Fail))
        {
            var meaningful = scenario.Steps[step.Index] is VerificationStep.Check
                ? step.Code == "CheckFailed"
                : step.Code == "ElementNotFound";
            if (!meaningful)
            {
                problems.Add($"Step {step.Index} ({step.Description}) failed with {step.Code} on the unchanged app, which does not show the behaviour is missing.");
            }
        }

        var criteria = new List<TaskCriterionBefore>();
        for (var number = 1; number <= scenario.Criteria.Count; number++)
        {
            var id = $"C{number}";
            var checks = result.Steps
                .Where(step => step.Index >= 0 && scenario.Steps[step.Index] is VerificationStep.Check { Criterion: var criterion } && criterion == id)
                .ToArray();
            var before = checks.Any(step => step.Status == VerificationStatus.Pass) ? "passed"
                : checks.Any(step => step.Status == VerificationStatus.Fail) ? "failed"
                : "notReached";
            if (before == "passed")
            {
                problems.Add($"{id} already holds on the unchanged app, so it does not test the change: \"{scenario.Criteria[number - 1]}\".");
            }

            criteria.Add(new TaskCriterionBefore(id, scenario.Criteria[number - 1], before));
        }

        foreach (var step in result.Steps.Where(step => step.Index >= 0 && scenario.Steps[step.Index] is VerificationStep.Check { ExpectFailure: true }))
        {
            if (step.Status == VerificationStatus.Fail)
            {
                problems.Add($"The negative control at step {step.Index} failed on the unchanged app ({step.Code}), so the check cannot tell states apart.");
            }
        }

        return new TaskFailBefore(problems.Count == 0 ? Confirmed : Rejected, criteria, problems, result.BundleDirectory);
    }
}
