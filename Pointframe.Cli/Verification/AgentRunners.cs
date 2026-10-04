using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Pointframe.Cli;

internal sealed class AgentException(string message) : Exception(message);

internal sealed record AgentMcpServer(string Name, string Command, IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string> Environment);

// One task for an agent: instructions (its role), the input, the JSON schema its answer must follow, an empty
// folder outside the repository to work in, and at most one MCP server (the examiner's desktop tools).
internal sealed record AgentRequest(
    string Role,
    string Instructions,
    string Input,
    string OutputSchema,
    string WorkDirectory,
    AgentMcpServer? McpServer,
    decimal MaxBudgetUsd);

// The examiner, reviewer, and approver are roles, not products: each runs on whichever agent the person chose
// (Claude Code, Codex, or any command that reads a prompt and writes JSON). The runner hides how that agent
// is started and how its answer is read; the role only sees the answer as JSON.
internal interface IAgentRunner
{
    string Name { get; }

    Task<JsonElement> RunAsync(AgentRequest request, CancellationToken cancellationToken);
}

internal sealed record ResolvedExecutable(string FileName, IReadOnlyList<string> PrefixArguments);

internal static class AgentProcess
{
    // Starts the agent with exact arguments (no shell, so JSON and paths keep their quotes), writes the input
    // to stdin as UTF-8, and reads stdout and stderr concurrently so neither pipe can fill and block it.
    internal static async Task<(int ExitCode, string Output, string Error)> RunAsync(
        ResolvedExecutable executable,
        IReadOnlyList<string> arguments,
        string input,
        string workDirectory,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(executable.FileName)
        {
            WorkingDirectory = workDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in executable.PrefixArguments.Concat(arguments))
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new AgentException($"{executable.FileName} could not be started.");
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.StandardInput.WriteAsync(input.AsMemory(), cancellationToken);
        process.StandardInput.Close();
        var output = await outputTask;
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, output, await errorTask);
    }

    // An npm-installed CLI is a .cmd shim that cmd.exe would re-parse, mangling JSON and quoted arguments.
    // Find the real program behind it instead: a native exe, or the package's JavaScript entry run by node.
    internal static ResolvedExecutable? Resolve(string name, string npmNativePath, string? npmScriptPath)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var exe = Path.Combine(directory, $"{name}.exe");
            if (File.Exists(exe))
            {
                return new ResolvedExecutable(exe, []);
            }

            if (!File.Exists(Path.Combine(directory, $"{name}.cmd")))
            {
                continue;
            }

            var native = Path.Combine(directory, npmNativePath);
            if (File.Exists(native))
            {
                return new ResolvedExecutable(native, []);
            }

            var script = npmScriptPath is null ? null : Path.Combine(directory, npmScriptPath);
            var node = FindOnPath("node.exe");
            if (script is not null && File.Exists(script) && node is not null)
            {
                return new ResolvedExecutable(node, [script]);
            }
        }

        return null;
    }

    internal static string? FindOnPath(string fileName) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory, fileName))
            .FirstOrDefault(File.Exists);

    // Agents sometimes wrap JSON in a ```json fence; the role needs the bare object.
    internal static JsonElement ParseJson(string text, string what)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstLine = trimmed.IndexOf('\n');
            var closing = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            trimmed = firstLine > 0 && closing > firstLine ? trimmed[(firstLine + 1)..closing] : trimmed;
        }

        try
        {
            using var document = JsonDocument.Parse(trimmed);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? document.RootElement.Clone()
                : throw new AgentException($"{what} is not a JSON object.");
        }
        catch (JsonException exception)
        {
            throw new AgentException($"{what} is not valid JSON: {exception.Message}");
        }
    }

    internal static string Last(string text) => text.Length <= 600 ? text.Trim() : text[^600..].Trim();
}

// Claude Code: `claude -p` with the instructions as the system prompt, every built-in tool off, only the given
// MCP server, user settings only (no project hooks), a budget cap, and the answer validated against the
// schema in structured_output.
internal sealed class ClaudeCodeAgentRunner(ResolvedExecutable executable) : IAgentRunner
{
    public string Name => "claude-code";

    internal static ResolvedExecutable? Resolve() =>
        AgentProcess.Resolve("claude", Path.Combine("node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe"), null);

    public async Task<JsonElement> RunAsync(AgentRequest request, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(request.WorkDirectory);
        string? mcpConfigPath = null;
        if (request.McpServer is { } server)
        {
            mcpConfigPath = Path.Combine(request.WorkDirectory, $"{request.Role}-mcp.json");
            await File.WriteAllTextAsync(mcpConfigPath, McpConfigJson(server), cancellationToken);
        }

        var (exitCode, output, error) = await AgentProcess.RunAsync(
            executable, Arguments(request, mcpConfigPath), request.Input, request.WorkDirectory, cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(request.WorkDirectory, $"{request.Role}-output.json"), output, cancellationToken);
        if (exitCode != 0)
        {
            throw new AgentException($"The {request.Role} (Claude Code) exited with {exitCode}: {AgentProcess.Last(error.Length > 0 ? error : output)}");
        }

        return ParseEnvelope(output, request.Role);
    }

    internal static string McpConfigJson(AgentMcpServer server) => JsonSerializer.Serialize(new
    {
        mcpServers = new Dictionary<string, object>
        {
            [server.Name] = new { command = server.Command, args = server.Arguments, env = server.Environment },
        },
    });

    internal static IReadOnlyList<string> Arguments(AgentRequest request, string? mcpConfigPath)
    {
        var arguments = new List<string>
        {
            "-p",
            "--output-format", "json",
            "--json-schema", request.OutputSchema,
            "--system-prompt", request.Instructions,
            "--strict-mcp-config",
            "--tools", string.Empty,
            "--setting-sources", "user",
            "--no-session-persistence",
            "--max-budget-usd", request.MaxBudgetUsd.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        if (mcpConfigPath is not null && request.McpServer is { } server)
        {
            arguments.AddRange(["--mcp-config", mcpConfigPath, "--allowedTools", $"mcp__{server.Name}", "--permission-mode", "dontAsk"]);
        }

        return arguments;
    }

    // Claude Code's json output carries the schema-validated object in structured_output; older builds put it
    // as JSON text in result.
    internal static JsonElement ParseEnvelope(string output, string role)
    {
        var envelope = AgentProcess.ParseJson(output, $"The {role}'s output");
        if (envelope.TryGetProperty("is_error", out var isError) && isError.ValueKind == JsonValueKind.True)
        {
            var message = envelope.TryGetProperty("result", out var result) ? result.ToString() : output;
            throw new AgentException($"The {role} reported an error: {AgentProcess.Last(message)}");
        }

        if (envelope.TryGetProperty("structured_output", out var structured) && structured.ValueKind == JsonValueKind.Object)
        {
            return structured.Clone();
        }

        return envelope.TryGetProperty("result", out var text) && text.ValueKind == JsonValueKind.String
            ? AgentProcess.ParseJson(text.GetString()!, $"The {role}'s answer")
            : throw new AgentException($"The {role} returned no structured output.");
    }
}

// Codex: `codex exec -` reads the prompt from stdin; Codex has no separate system prompt, so the instructions
// lead the prompt. The answer is written by -o, validated against --output-schema. A read-only sandbox, no
// approval prompts, an empty folder outside the repository, and the MCP server given through -c overrides.
// Codex has no budget flag, so MaxBudgetUsd is not enforced here.
internal sealed class CodexAgentRunner(ResolvedExecutable executable) : IAgentRunner
{
    public string Name => "codex";

    internal static ResolvedExecutable? Resolve() =>
        AgentProcess.Resolve(
            "codex",
            Path.Combine("node_modules", "@openai", "codex", "bin", "codex.exe"),
            Path.Combine("node_modules", "@openai", "codex", "bin", "codex.js"));

    public async Task<JsonElement> RunAsync(AgentRequest request, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(request.WorkDirectory);
        var schemaPath = Path.Combine(request.WorkDirectory, $"{request.Role}-schema.json");
        var answerPath = Path.Combine(request.WorkDirectory, $"{request.Role}-answer.json");
        await File.WriteAllTextAsync(schemaPath, request.OutputSchema, cancellationToken);
        var (exitCode, output, error) = await AgentProcess.RunAsync(
            executable, Arguments(request, schemaPath, answerPath), Prompt(request), request.WorkDirectory, cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(request.WorkDirectory, $"{request.Role}-output.txt"), output + error, cancellationToken);
        if (exitCode != 0)
        {
            throw new AgentException($"The {request.Role} (Codex) exited with {exitCode}: {AgentProcess.Last(error.Length > 0 ? error : output)}");
        }

        return File.Exists(answerPath)
            ? AgentProcess.ParseJson(await File.ReadAllTextAsync(answerPath, cancellationToken), $"The {request.Role}'s answer")
            : throw new AgentException($"The {request.Role} (Codex) wrote no answer.");
    }

    internal static string Prompt(AgentRequest request) => $"""
        {request.Instructions}

        ---

        {request.Input}
        """;

    internal static IReadOnlyList<string> Arguments(AgentRequest request, string schemaPath, string answerPath)
    {
        // --ask-for-approval is a top-level codex option, so it goes before `exec` (codex 0.160 rejects it after).
        var arguments = new List<string>
        {
            "--ask-for-approval", "never",
            "exec", "-",
            "--output-schema", schemaPath,
            "-o", answerPath,
            "--sandbox", "read-only",
            "--skip-git-repo-check",
            "--ephemeral",
            "-C", request.WorkDirectory,
        };
        if (request.McpServer is { } server)
        {
            var prefix = $"mcp_servers.{server.Name}";
            arguments.AddRange(["-c", $"{prefix}.command={TomlString(server.Command)}"]);

            // With --ask-for-approval never, Codex rejects every MCP call that would need approval, and the
            // examiner could not open the app at all. Approve this one server's tools; everything else keeps
            // the read-only sandbox and the never-ask policy.
            arguments.AddRange(["-c", $"{prefix}.default_tools_approval_mode='approve'"]);
            arguments.AddRange(["-c", $"{prefix}.args=[{string.Join(", ", server.Arguments.Select(TomlString))}]"]);
            if (server.Environment.Count > 0)
            {
                arguments.AddRange(["-c", $"{prefix}.env={{ {string.Join(", ", server.Environment.Select(pair => $"{pair.Key} = {TomlString(pair.Value)}"))} }}"]);
            }
        }

        return arguments;
    }

    // A TOML literal string ('...') keeps Windows backslashes as they are; a value containing a quote falls
    // back to a basic string with escapes.
    internal static string TomlString(string value) =>
        !value.Contains('\'') && !value.Contains('\n')
            ? $"'{value}'"
            : $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n")}\"";
}

// Any other agent: a command the person configures, with placeholders the CLI fills in. The prompt
// (instructions, then input) is written to {prompt_file} and also sent on stdin; the answer is read from
// {output_file}, or from stdout when the command leaves that file empty. {mcp_config_file} is an MCP config
// in the common "mcpServers" JSON shape when the role needs the desktop tools.
internal sealed class CommandAgentRunner(string command, IReadOnlyList<string> arguments) : IAgentRunner
{
    internal static readonly string[] Placeholders =
        ["{prompt_file}", "{instructions_file}", "{input_file}", "{schema_file}", "{output_file}", "{mcp_config_file}", "{work_dir}"];

    public string Name => $"command:{Path.GetFileNameWithoutExtension(command)}";

    public async Task<JsonElement> RunAsync(AgentRequest request, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(request.WorkDirectory);
        var files = new Dictionary<string, string>
        {
            ["{prompt_file}"] = Path.Combine(request.WorkDirectory, $"{request.Role}-prompt.txt"),
            ["{instructions_file}"] = Path.Combine(request.WorkDirectory, $"{request.Role}-instructions.txt"),
            ["{input_file}"] = Path.Combine(request.WorkDirectory, $"{request.Role}-input.txt"),
            ["{schema_file}"] = Path.Combine(request.WorkDirectory, $"{request.Role}-schema.json"),
            ["{output_file}"] = Path.Combine(request.WorkDirectory, $"{request.Role}-answer.json"),
            ["{mcp_config_file}"] = Path.Combine(request.WorkDirectory, $"{request.Role}-mcp.json"),
            ["{work_dir}"] = request.WorkDirectory,
        };
        var prompt = CodexAgentRunner.Prompt(request);
        await File.WriteAllTextAsync(files["{prompt_file}"], prompt, cancellationToken);
        await File.WriteAllTextAsync(files["{instructions_file}"], request.Instructions, cancellationToken);
        await File.WriteAllTextAsync(files["{input_file}"], request.Input, cancellationToken);
        await File.WriteAllTextAsync(files["{schema_file}"], request.OutputSchema, cancellationToken);
        await File.WriteAllTextAsync(
            files["{mcp_config_file}"],
            request.McpServer is { } server ? ClaudeCodeAgentRunner.McpConfigJson(server) : """{ "mcpServers": {} }""",
            cancellationToken);

        var expanded = arguments.Select(argument => files.Aggregate(argument, (text, file) => text.Replace(file.Key, file.Value, StringComparison.Ordinal))).ToArray();
        var (exitCode, output, error) = await AgentProcess.RunAsync(
            new ResolvedExecutable(command, []), expanded, prompt, request.WorkDirectory, cancellationToken);
        if (exitCode != 0)
        {
            throw new AgentException($"The {request.Role} ({Name}) exited with {exitCode}: {AgentProcess.Last(error.Length > 0 ? error : output)}");
        }

        var answer = File.Exists(files["{output_file}"]) ? await File.ReadAllTextAsync(files["{output_file}"], cancellationToken) : string.Empty;
        return AgentProcess.ParseJson(answer.Trim().Length > 0 ? answer : output, $"The {request.Role}'s answer");
    }
}

internal sealed record AgentSettings(string Agent, string? Command = null, IReadOnlyList<string>? Arguments = null);

// Which agent plays the examiner, reviewer, and approver. The choice lives in the person's profile
// (%LOCALAPPDATA%\Pointframe\verify\agent.json), not in the repository, so a worker editing the project cannot
// pick its own judge. Without a choice, the first installed of Claude Code and Codex is used.
internal static class AgentSelection
{
    internal const string FileName = "agent.json";
    internal static readonly string[] KnownAgents = ["claude", "codex", "command"];

    internal static AgentSettings? Read(VerificationStore store)
    {
        var path = Path.Combine(store.BaseDirectory, FileName);
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<AgentSettings>(File.ReadAllText(path), VerificationStore.Json) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static void Write(VerificationStore store, AgentSettings settings)
    {
        Directory.CreateDirectory(store.BaseDirectory);
        File.WriteAllText(Path.Combine(store.BaseDirectory, FileName), JsonSerializer.Serialize(settings, VerificationStore.Json));
    }

    // Returns the runner, or null with the reason no agent is available.
    internal static (IAgentRunner? Runner, string Description) Create(AgentSettings? settings)
    {
        var agent = settings?.Agent?.ToLowerInvariant();
        switch (agent)
        {
            case "claude":
                return ClaudeCodeAgentRunner.Resolve() is { } claude
                    ? (new ClaudeCodeAgentRunner(claude), $"claude-code ({claude.FileName})")
                    : (null, "Claude Code is selected but not installed.");
            case "codex":
                return CodexAgentRunner.Resolve() is { } codex
                    ? (new CodexAgentRunner(codex), $"codex ({string.Join(" ", codex.PrefixArguments.Prepend(codex.FileName))})")
                    : (null, "Codex is selected but not installed.");
            case "command":
                return string.IsNullOrWhiteSpace(settings!.Command)
                    ? (null, "A command agent is selected but agent.json names no command.")
                    : (new CommandAgentRunner(settings.Command, settings.Arguments ?? []), $"command ({settings.Command})");
            case null:
                if (ClaudeCodeAgentRunner.Resolve() is { } detectedClaude)
                {
                    return (new ClaudeCodeAgentRunner(detectedClaude), $"claude-code, detected ({detectedClaude.FileName})");
                }

                return CodexAgentRunner.Resolve() is { } detectedCodex
                    ? (new CodexAgentRunner(detectedCodex), $"codex, detected ({detectedCodex.FileName})")
                    : (null, "No agent: install Claude Code or Codex, or configure a command in agent.json.");
            default:
                return (null, $"Unknown agent '{settings!.Agent}' in agent.json; use claude, codex, or command.");
        }
    }
}
