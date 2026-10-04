using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Pointframe.Cli;

internal sealed record HookState(string? SessionId, int Blocks);

// `verify hook stop`: the Claude Code (and Codex) Stop hook. It lets the agent finish only when the project's
// verdict is a pass for the files as they are now. A failure the agent can fix (a gate, a scenario, the frozen
// task) blocks the stop and tells the agent what failed; a failure only a person can fix (gates not approved,
// no MCP server, the desktop busy) lets it stop and tells the person, because blocking would only loop. After
// maxBlocks blocks in one session the agent may stop and report, as CLAUDE.md asks.
internal sealed class VerificationHook(VerificationServices services, TextReader input, TextWriter output, TextWriter log)
{
    internal const int DefaultMaxBlocks = 5;
    internal const string StateFileName = "hook-state.json";
    internal const string ReviewFileName = "review.json";

    private static readonly HashSet<string> NeedsAPerson = new(StringComparer.Ordinal)
    {
        "spec_untrusted", "spec_invalid", "mcp_not_found", "desktop_busy", "task_not_found", "task_invalid",
    };

    internal async Task<int> StopAsync(CliCommand command, CancellationToken cancellationToken)
    {
        // A hook that crashes is a non-blocking error to Claude Code: the agent would stop unverified and
        // nobody would be told. Report it to the person instead.
        try
        {
            return await DecideAsync(command, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(new
            {
                systemMessage = $"Pointframe verify hook failed, so this work was NOT verified: {exception.GetType().Name}: {exception.Message}",
            }));
            return 0;
        }
    }

    private async Task<int> DecideAsync(CliCommand command, CancellationToken cancellationToken)
    {
        var (sessionId, cwd) = await ReadHookInputAsync();
        var specPath = Path.GetFullPath(command.SpecPath ?? Path.Combine(cwd ?? Environment.CurrentDirectory, VerificationSpecLoader.DefaultSpecRelativePath));
        if (!File.Exists(specPath))
        {
            return 0;
        }

        var root = VerificationSpecLoader.RootDirectoryFor(specPath);
        var outputDirectory = Path.Combine(root, VerificationApplication.OutputRelativePath);
        var state = ReadState(outputDirectory);
        if (state.SessionId != sessionId)
        {
            state = new HookState(sessionId, 0);
        }

        var activeTask = services.Store.ReadActiveTask(root);
        var activeSnapshot = activeTask is null ? null : services.Store.ReadTask(root, activeTask);
        if (activeSnapshot is null)
        {
            activeTask = null;
        }

        var tree = services.WorkingTree.Read(root);
        var specSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(specPath)));
        var verdict = ReusableVerdict(outputDirectory, tree.TreeHash, specSha256, activeTask, activeSnapshot?.Sha256);
        if (verdict is null)
        {
            var captured = new StringWriter();
            var run = new CliCommand(
                "verify", SpecPath: specPath, McpExecutablePath: command.McpExecutablePath, VerifyAction: "run", TaskId: activeTask);
            await new VerificationApplication(services, captured, log).RunAsync(run, cancellationToken);
            verdict = LastJson(captured.ToString());
        }

        if (verdict is null)
        {
            return await AllowAsync(outputDirectory, state with { Blocks = 0 }, "Pointframe verify produced no verdict; the stop was not checked.");
        }

        var status = verdict.Value.GetProperty("status").GetString();
        var errorCode = verdict.Value.TryGetProperty("errorCode", out var code) ? code.GetString() : null;
        if (status == VerificationStatus.Pass)
        {
            var message = $"Pointframe verify: pass on tree {Short(tree.TreeHash)}.";
            if (command.Review && !Reviewed(outputDirectory, tree.TreeHash))
            {
                message += " " + await ReviewAsync(root, outputDirectory, activeTask, tree, verdict.Value, cancellationToken);
            }

            return await AllowAsync(outputDirectory, state with { Blocks = 0 }, message);
        }

        if (errorCode is not null && NeedsAPerson.Contains(errorCode))
        {
            var error = verdict.Value.TryGetProperty("error", out var text) ? text.GetString() : errorCode;
            return await AllowAsync(outputDirectory, state with { Blocks = 0 }, $"Pointframe verify could not check this work and needs you ({errorCode}), so this work was NOT verified: {error}");
        }

        var maxBlocks = command.MaxBlocks ?? DefaultMaxBlocks;
        var blocks = state.Blocks + 1;
        var summary = Summary(verdict.Value);
        if (blocks > maxBlocks)
        {
            return await AllowAsync(
                outputDirectory, state with { Blocks = 0 },
                $"Pointframe verify still fails after {maxBlocks} blocked attempts; the agent was allowed to stop. {summary}");
        }

        WriteState(outputDirectory, state with { Blocks = blocks });
        var reason = new StringBuilder()
            .AppendLine($"Pointframe verify failed (block {blocks} of {maxBlocks}), so this work is not done yet.")
            .AppendLine(summary)
            .AppendLine("Fix the cause in the code, then finish again: this hook re-runs the verification. Do not edit .pointframe/verify.json or the frozen task to make it pass; a person reviews those changes.")
            .ToString();
        await output.WriteLineAsync(JsonSerializer.Serialize(new { decision = "block", reason }));
        return 0;
    }

    // The verdict a run would produce again: the same files, the same spec, the same task, and no filter.
    // Reusing it makes a question-only turn free and keeps an unchanged failure blocking without a re-run.
    private static JsonElement? ReusableVerdict(string outputDirectory, string? treeHash, string specSha256, string? activeTask, string? taskSha256)
    {
        var path = Path.Combine(outputDirectory, "verdict.json");
        if (treeHash is null || !File.Exists(path))
        {
            return null;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var verdict = document.RootElement;
        var sameTree = verdict.TryGetProperty("provenance", out var provenance)
            && provenance.TryGetProperty("treeHash", out var tree) && tree.GetString() == treeHash;
        var sameSpec = verdict.TryGetProperty("specSha256", out var spec) && spec.GetString() == specSha256;
        // The snapshot's hash too: a task replaced under the same id must not reuse a pass on the old criteria.
        var hasTask = verdict.TryGetProperty("task", out var task);
        var sameTask = (hasTask ? task.GetProperty("id").GetString() : null) == activeTask
            && (hasTask ? task.GetProperty("snapshotSha256").GetString() : null) == taskSha256;
        var unfiltered = !verdict.TryGetProperty("only", out _) && !verdict.TryGetProperty("scenarioFilter", out _);
        var notAnError = !verdict.TryGetProperty("errorCode", out _);
        return sameTree && sameSpec && sameTask && unfiltered && notAnError ? verdict.Clone() : null;
    }

    internal static string Summary(JsonElement verdict)
    {
        var lines = new List<string>();
        if (verdict.TryGetProperty("error", out var error))
        {
            lines.Add($"Error ({verdict.GetProperty("errorCode").GetString()}): {error.GetString()}");
        }

        foreach (var gate in verdict.GetProperty("gates").EnumerateArray().Where(gate => gate.GetProperty("status").GetString() != VerificationStatus.Pass))
        {
            lines.Add($"Gate '{gate.GetProperty("id").GetString()}' failed (log: {(gate.TryGetProperty("log", out var log) ? log.GetString() : "none")}):");
            lines.AddRange(gate.GetProperty("details").EnumerateArray().Take(8).Select(detail => $"  {detail.GetString()}"));
        }

        foreach (var scenario in verdict.GetProperty("scenarios").EnumerateArray().Where(scenario => scenario.GetProperty("status").GetString() != VerificationStatus.Pass))
        {
            var id = scenario.GetProperty("id").GetString();
            var step = scenario.GetProperty("steps").EnumerateArray().FirstOrDefault(item => item.GetProperty("status").GetString() == VerificationStatus.Fail);
            if (step.ValueKind == JsonValueKind.Object)
            {
                var found = step.TryGetProperty("actual", out var actual)
                    ? $" Expected '{(step.TryGetProperty("expected", out var expected) ? expected.GetString() : null)}', found '{actual.GetString()}'."
                    : string.Empty;
                var message = step.TryGetProperty("message", out var text) ? $" {text.GetString()}" : string.Empty;
                lines.Add($"Scenario '{id}' failed at step {step.GetProperty("index").GetInt32()} ({step.GetProperty("description").GetString()}): {(step.TryGetProperty("code", out var stepCode) ? stepCode.GetString() : "failed")}.{found}{message}");
            }
            else
            {
                var problems = scenario.GetProperty("problems").EnumerateArray().Select(problem => problem.GetString()).ToArray();
                lines.Add($"Scenario '{id}' {scenario.GetProperty("status").GetString()}: {string.Join(" ", problems)}");
            }
        }

        if (verdict.TryGetProperty("task", out var task) && task.GetProperty("specChanged").GetBoolean())
        {
            lines.Add($"The spec changed since the task started: {string.Join("; ", task.GetProperty("specChanges").EnumerateArray().Select(change => change.GetString()))}.");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private async Task<string> ReviewAsync(string root, string outputDirectory, string? activeTask, WorkingTreeState tree, JsonElement verdict, CancellationToken cancellationToken)
    {
        var snapshot = activeTask is null ? null : services.Store.ReadTask(root, activeTask)?.Snapshot;
        var baseCommit = snapshot?.Head ?? tree.Head;
        if (services.Reviewer is null || baseCommit is null)
        {
            return "Review skipped: no reviewer or no git history.";
        }

        var diff = services.WorkingTree.Diff(root, baseCommit) ?? string.Empty;
        if (diff.Length == 0)
        {
            return "Review skipped: no changes since the task started.";
        }

        try
        {
            var review = await services.Reviewer.ReviewAsync(
                snapshot?.TaskText ?? "No frozen task; review the change on its own.", diff, Summary(verdict) is { Length: > 0 } text ? text : "Every gate and scenario passed.", cancellationToken);
            Directory.CreateDirectory(outputDirectory);
            var path = Path.Combine(outputDirectory, ReviewFileName);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { treeHash = tree.TreeHash, baseCommit, review }, VerificationApplication.VerdictJson), cancellationToken);
            var high = review.Flags.Count(flag => flag.Severity == "high");
            return review.Flags.Count == 0
                ? $"Reviewer: no flags. {review.Summary}"
                : $"Reviewer: {review.Flags.Count} flag(s), {high} high; look before merging ({path}). {review.Summary}";
        }
        catch (Exception exception) when (exception is AgentException or IOException or System.ComponentModel.Win32Exception)
        {
            return $"Review failed: {exception.Message}";
        }
    }

    // A pass on a tree the reviewer has not seen yet, however the pass was produced (by this hook or by the
    // agent running `verify run` itself), is reviewed once.
    private static bool Reviewed(string outputDirectory, string? treeHash)
    {
        var path = Path.Combine(outputDirectory, ReviewFileName);
        if (treeHash is null || !File.Exists(path))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("treeHash", out var reviewed) && reviewed.GetString() == treeHash;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task<int> AllowAsync(string outputDirectory, HookState state, string message)
    {
        WriteState(outputDirectory, state);
        await output.WriteLineAsync(JsonSerializer.Serialize(new { systemMessage = message }));
        return 0;
    }

    private async Task<(string? SessionId, string? Cwd)> ReadHookInputAsync()
    {
        var text = await input.ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(text))
        {
            return (null, null);
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            return (
                root.TryGetProperty("session_id", out var session) ? session.GetString() : null,
                root.TryGetProperty("cwd", out var cwd) ? cwd.GetString() : null);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static JsonElement? LastJson(string text)
    {
        var start = text.IndexOf('{');
        if (start < 0)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(text[start..]);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static HookState ReadState(string outputDirectory)
    {
        var path = Path.Combine(outputDirectory, StateFileName);
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<HookState>(File.ReadAllText(path), VerificationStore.Json) ?? new HookState(null, 0) : new HookState(null, 0);
        }
        catch (JsonException)
        {
            return new HookState(null, 0);
        }
    }

    private static void WriteState(string outputDirectory, HookState state)
    {
        Directory.CreateDirectory(outputDirectory);
        File.WriteAllText(Path.Combine(outputDirectory, StateFileName), JsonSerializer.Serialize(state, VerificationStore.Json));
    }

    private static string Short(string? hash) => hash is null ? "unknown" : hash[..Math.Min(10, hash.Length)];
}
