using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pointframe.Cli;

internal sealed record VerificationServices(
    IMcpToolClientFactory ClientFactory,
    Func<string?> InstalledMcpExecutable,
    ICommandRunner Commands,
    IWorkingTreeReader WorkingTree,
    VerificationStore Store,
    IConfirmation Confirmation,
    IExaminer Examiner,
    string DesktopLockName = VerificationApplication.DefaultDesktopLockName,
    string? VerifierVersion = null,
    IReviewer? Reviewer = null,
    TextReader? HookInput = null,
    ICommandApprover? Approver = null,
    IPointframeCommandResolver? CommandResolver = null,
    IMcpServerHost? ServerHost = null,
    IMcpPackageInstaller? McpInstaller = null);

internal sealed record TaskStartResponse(
    int SchemaVersion,
    string Status,
    string TaskId,
    string? SnapshotPath = null,
    string? SnapshotSha256 = null,
    IReadOnlyList<string>? Criteria = null,
    IReadOnlyList<string>? RequiredAutomationIds = null,
    string? Notes = null,
    TaskFailBefore? FailBefore = null,
    string? ErrorCode = null,
    string? Error = null);

// `verify run|status|trust|task start`. A run executes the spec's gates, then (when every gate passed) each
// scenario in its own MCP server and desktop session, and writes artifacts/pointframe-verify/verdict.json.
// Exit 0 for pass (and partial, like scripts/verify.ps1), 1 for fail, 2 for a bad spec or arguments;
// callers that need a final verdict read "status", never only the exit code.
internal sealed class VerificationApplication(VerificationServices services, TextWriter standardOutput, TextWriter standardError)
{
    internal const string DefaultDesktopLockName = @"Global\Pointframe.Verify.Desktop";
    internal const string McpExecutableVariable = "POINTFRAME_MCP_EXECUTABLE";
    internal static readonly string OutputRelativePath = Path.Combine("artifacts", "pointframe-verify");
    private const int SchemaVersion = 1;

    internal static readonly JsonSerializerOptions VerdictJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly string[] AllowedActions =
    [
        "StartTestSession", "RestartApp", "ObserveApp", "FocusWindow", "PressKeys", "EnterText", "Invoke",
        "CheckUi", "GetActionResult", "GetTestReport", "EndTestSession",
    ];

    internal Task<int> RunAsync(CliCommand command, CancellationToken cancellationToken) => command.VerifyAction switch
    {
        "init" => InitAsync(command, cancellationToken),
        "status" => StatusAsync(command),
        "trust" => TrustAsync(command),
        "task-start" => TaskStartAsync(command, cancellationToken),
        "hook-stop" => new VerificationHook(services, services.HookInput ?? TextReader.Null, standardOutput, standardError).StopAsync(command, cancellationToken),
        "agent" => AgentAsync(command),
        "setup" => new DesktopToolsApplication(services, this, standardOutput, standardError).SetupAsync(command, cancellationToken),
        _ => VerifyAsync(command, cancellationToken),
    };

    internal Task<int> ServeAsync(CliCommand command, string currentDirectory, CancellationToken cancellationToken) =>
        new DesktopToolsApplication(services, this, standardOutput, standardError).ServeAsync(command, currentDirectory, cancellationToken);

    private Task<int> InitAsync(CliCommand command, CancellationToken cancellationToken) => new VerificationInit(
        new PhysicalVerificationInitFileSystem(), standardOutput, standardError, services.CommandResolver, services.VerifierVersion)
        .RunAsync(command, Environment.CurrentDirectory, ResolveMcpExecutable, services.ClientFactory, services.DesktopLockName, TrustFailureCodeAsync, cancellationToken);

    private async Task<string?> TrustFailureCodeAsync(VerificationSpec spec, CancellationToken cancellationToken)
    {
        var (_, untrusted, errorCode) = await EnsureTrustedAsync(spec, cancellationToken);
        if (untrusted is null)
        {
            return null;
        }

        foreach (var line in untrusted)
        {
            await standardError.WriteLineAsync($"  {line}");
        }

        return errorCode ?? "spec_untrusted";
    }

    private async Task<int> VerifyAsync(CliCommand command, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var specPath = SpecPathOf(command);
        var context = new RunContext(command, started, stopwatch, specPath);

        VerificationSpec spec;
        try
        {
            spec = VerificationSpecLoader.Load(specPath);
        }
        catch (VerificationSpecException exception)
        {
            return await ErrorAsync(context, 2, "spec_invalid", exception.Message, writeFile: false);
        }

        context.SpecSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(specPath)));
        context.OutputDirectory = Path.Combine(spec.RootDirectory, OutputRelativePath);
        var scenarios = spec.Scenarios;
        if (command.ScenarioId is not null)
        {
            scenarios = spec.Scenarios.Where(item => string.Equals(item.Id, command.ScenarioId, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (scenarios.Count == 0)
            {
                return await ErrorAsync(
                    context, 2, "scenario_not_found",
                    $"The spec has no scenario '{command.ScenarioId}'. Scenarios: {string.Join(", ", spec.Scenarios.Select(item => item.Id))}.",
                    writeFile: false);
            }
        }

        if (command.TaskId is not null)
        {
            var loaded = LoadTask(spec, command.TaskId, context.SpecSha256);
            if (loaded.Error is not null)
            {
                return await ErrorAsync(context, 2, loaded.Error.Value.Code, loaded.Error.Value.Message, writeFile: false);
            }

            context.Task = loaded.Info;
            scenarios = [.. scenarios, loaded.Scenario!];
        }

        var runGates = command.Only != "scenarios" && spec.Gates.Count > 0;
        var runScenarios = command.Only != "gates" && scenarios.Count > 0;
        var tree = services.WorkingTree.Read(spec.RootDirectory);
        context.Provenance = new VerificationProvenance(tree.Head, tree.TreeHash, services.VerifierVersion, null, null);

        // The whole spec's commands, not only the ones this run starts: removing or editing a gate, or pointing
        // the app at another program, must be approved again, or emptying the gates would pass unapproved.
        var (approvedBy, untrusted, trustErrorCode) = await EnsureTrustedAsync(spec, cancellationToken);
        if (untrusted is not null)
        {
            return await ErrorAsync(
                context, 1, trustErrorCode ?? "spec_untrusted",
                trustErrorCode == "approver_unavailable"
                    ? "The spec's commands are outside the standard set, and no approver agent was available."
                    : "The spec's commands (gates and app) run with your permissions and were not approved.",
                writeFile: true, details: untrusted);
        }

        context.Provenance = context.Provenance with { CommandsApprovedBy = approvedBy };

        var runDirectory = Path.Combine(context.OutputDirectory, "runs", started.ToString("yyyyMMdd-HHmmss-fff"));
        Directory.CreateDirectory(runDirectory);
        IReadOnlyList<VerificationGateResult> gates = [];
        if (runGates)
        {
            gates = await GateRunner.RunAsync(spec.Gates, services.Commands, Path.Combine(runDirectory, "gates"), standardError, cancellationToken);
            foreach (var gate in gates)
            {
                await standardError.WriteLineAsync($"{gate.Status.ToUpperInvariant(),-4}  gate {gate.Id}{(gate.Details.Count > 0 ? $" - {gate.Details[0]}" : string.Empty)}");
            }
        }

        var results = new List<VerificationScenarioResult>();
        if (runScenarios)
        {
            var failedGates = gates.Where(gate => gate.Status != VerificationStatus.Pass).Select(gate => gate.Id).ToArray();
            if (failedGates.Length > 0)
            {
                results.AddRange(scenarios.Select(scenario => new VerificationScenarioResult(
                    scenario.Id, VerificationStatus.Skipped, null, [], [], null, false, null,
                    [$"Skipped because gate(s) failed: {string.Join(", ", failedGates)}."])));
            }
            else
            {
                var app = spec.App!;
                if (!File.Exists(app.ExecutablePath))
                {
                    return await ErrorAsync(context, 1, "app_not_found", $"The app executable does not exist: {app.ExecutablePath}. Build the app first.", writeFile: true, gates: gates);
                }

                var mcpExecutable = ResolveMcpExecutable(command.McpExecutablePath);
                if (mcpExecutable is null)
                {
                    return await ErrorAsync(context, 1, "mcp_not_found", McpNotFoundMessage, writeFile: true, gates: gates);
                }

                context.Provenance = context.Provenance with
                {
                    McpExecutablePath = mcpExecutable,
                    McpSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(mcpExecutable))),
                };
                using var desktopLock = DesktopLock.TryTake(services.DesktopLockName);
                if (desktopLock is null)
                {
                    return await ErrorAsync(context, 1, "desktop_busy", DesktopBusyMessage, writeFile: true, gates: gates);
                }

                await standardError.WriteLineAsync(
                    $"Pointframe verify: running {scenarios.Count} scenario(s) on the real desktop. Do not use the mouse or keyboard until it finishes.");
                foreach (var scenario in scenarios)
                {
                    var result = await RunScenarioAsync(app, scenario, runDirectory, mcpExecutable, continueAfterFailedChecks: false, cancellationToken);
                    results.Add(result);
                    await standardError.WriteLineAsync($"{result.Status.ToUpperInvariant(),-4}  {scenario.Id}{FirstFailure(result)}");
                }
            }
        }

        var failed = gates.Any(gate => gate.Status != VerificationStatus.Pass)
            || results.Any(result => result.Status != VerificationStatus.Pass);
        var filtered = command.Only is not null || command.ScenarioId is not null;
        var status = failed ? VerificationStatus.Fail : filtered ? VerificationStatus.Partial : VerificationStatus.Pass;
        var verdict = Verdict(context, status, gates, results);
        await WriteVerdictAsync(verdict, context.OutputDirectory, cancellationToken);
        if (context.Task is { SpecChanged: true })
        {
            await standardError.WriteLineAsync($"Note: the spec changed since task '{context.Task.Id}' started: {string.Join("; ", context.Task.SpecChanges)}.");
        }

        return status == VerificationStatus.Fail ? 1 : 0;
    }

    private async Task<int> StatusAsync(CliCommand command)
    {
        var specPath = SpecPathOf(command);
        var root = VerificationSpecLoader.RootDirectoryFor(specPath);
        var verdictPath = Path.Combine(root, OutputRelativePath, "verdict.json");
        var outputDirectory = Path.Combine(root, OutputRelativePath);
        var current = services.WorkingTree.Read(root);
        var currentSpecSha256 = File.Exists(specPath)
            ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(specPath)))
            : null;
        var status = "none";
        string? verdictTree = null;
        string? verdictSpecSha256 = null;
        string? verdictTaskId = null;
        string? verdictTaskSha256 = null;
        DateTimeOffset? startedUtc = null;
        if (File.Exists(verdictPath))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(verdictPath));
            var verdict = document.RootElement;
            status = verdict.GetProperty("status").GetString() ?? "none";
            verdictTree = verdict.TryGetProperty("provenance", out var provenance) && provenance.TryGetProperty("treeHash", out var tree)
                ? tree.GetString()
                : null;
            verdictSpecSha256 = verdict.TryGetProperty("specSha256", out var specSha256) ? specSha256.GetString() : null;
            startedUtc = verdict.TryGetProperty("startedUtc", out var startedElement) ? startedElement.GetDateTimeOffset() : null;
            if (verdict.TryGetProperty("task", out var task) && task.ValueKind == JsonValueKind.Object)
            {
                verdictTaskId = task.TryGetProperty("id", out var taskId) ? taskId.GetString() : null;
                verdictTaskSha256 = task.TryGetProperty("snapshotSha256", out var taskSha256) ? taskSha256.GetString() : null;
            }
        }

        // The same rule as the Stop hook's reusable verdict: with an active task, only a verdict for that task
        // and its current snapshot is fresh.
        var activeTask = services.Store.ReadActiveTask(root);
        var activeSnapshot = activeTask is null ? null : services.Store.ReadTask(root, activeTask);
        if (activeSnapshot is null)
        {
            activeTask = null;
        }

        var treeMatches = verdictTree is not null && verdictTree == current.TreeHash;
        var specMatches = currentSpecSha256 is not null && verdictSpecSha256 == currentSpecSha256;
        var taskMatches = activeTask is null || (verdictTaskId == activeTask && verdictTaskSha256 == activeSnapshot.Value.Sha256);
        var fresh = status == VerificationStatus.Pass && treeMatches && specMatches && taskMatches;
        var freshnessReason = !specMatches && verdictSpecSha256 is not null
            ? "verdict_for_another_spec"
            : !treeMatches && verdictTree is not null
                ? "tree_changed"
                : taskMatches
                    ? null
                    : verdictTaskId is null ? "task_not_covered" : "verdict_for_another_task";
        var lastStop = ReadLastStop(Path.Combine(outputDirectory, VerificationHook.LastStopFileName));
        var unverifiedStop = lastStop is { } stop
            && stop.TryGetProperty("outcome", out var outcome) && outcome.GetString() == "unverified"
            && stop.TryGetProperty("treeHash", out var stopTree) && stopTree.GetString() == current.TreeHash;
        var hookCommand = (services.CommandResolver ?? new PointframeCommandResolver()).Resolve();
        var review = ReadReviewStatus(Path.Combine(root, OutputRelativePath, VerificationHook.ReviewFileName), current.TreeHash);
        await standardOutput.WriteLineAsync(JsonSerializer.Serialize(new
        {
            schemaVersion = SchemaVersion,
            status,
            fresh,
            verdictTreeHash = verdictTree,
            currentTreeHash = current.TreeHash,
            verdictSpecSha256,
            currentSpecSha256,
            freshnessReason,
            activeTask,
            lastStop,
            unverifiedStop,
            startedUtc,
            verdictPath,
            hookCommand = new { path = hookCommand.Path, version = hookCommand.Version, ok = hookCommand.Ok },
            review,
        }, VerdictJson));
        return fresh ? 0 : 1;
    }

    private static JsonElement? ReadLastStop(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static object ReadReviewStatus(string path, string? treeHash)
    {
        if (treeHash is null || !File.Exists(path))
        {
            return new { status = "none", error = (string?)null };
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var record = document.RootElement;
            if (!record.TryGetProperty("treeHash", out var reviewedTree) || reviewedTree.GetString() != treeHash)
            {
                return new { status = "none", error = (string?)null };
            }

            var failed = record.TryGetProperty("failed", out var failedElement) && failedElement.GetBoolean();
            var error = failed && record.TryGetProperty("error", out var errorElement) ? errorElement.GetString() : null;
            return new { status = failed ? "failed" : "reviewed", error };
        }
        catch (JsonException)
        {
            return new { status = "none", error = (string?)null };
        }
    }

    // `verify agent`: which agent plays the examiner, reviewer, and approver, and whether it is installed.
    // `--use claude|codex` pins one; `--use auto` returns to detection. A custom command is set by editing
    // agent.json, because its arguments need placeholders a flag cannot carry well.
    private async Task<int> AgentAsync(CliCommand command)
    {
        if (command.UseAgent is { } use)
        {
            var path = Path.Combine(services.Store.BaseDirectory, AgentSelection.FileName);
            if (use == "auto")
            {
                File.Delete(path);
            }
            else
            {
                AgentSelection.Write(services.Store, new AgentSettings(use));
            }
        }

        var settings = AgentSelection.Read(services.Store);
        var (runner, description) = AgentSelection.Create(settings);
        await standardOutput.WriteLineAsync(JsonSerializer.Serialize(new
        {
            schemaVersion = SchemaVersion,
            selected = settings?.Agent ?? "auto",
            available = runner is not null,
            agent = description,
            settingsPath = Path.Combine(services.Store.BaseDirectory, AgentSelection.FileName),
        }, VerdictJson));
        return runner is null ? 1 : 0;
    }

    private async Task<int> TrustAsync(CliCommand command)
    {
        VerificationSpec spec;
        try
        {
            spec = VerificationSpecLoader.Load(SpecPathOf(command));
        }
        catch (VerificationSpecException exception)
        {
            await standardError.WriteLineAsync($"Pointframe verify failed: {exception.Message}");
            return 2;
        }

        if (command.Revoke)
        {
            var revoked = services.Store.RevokeTrust(spec.RootDirectory);
            await standardOutput.WriteLineAsync(JsonSerializer.Serialize(new { schemaVersion = SchemaVersion, status = revoked ? "revoked" : "not_trusted" }, VerdictJson));
            return 0;
        }

        var commandsSha256 = SpecDigests.CommandsSha256(spec);
        if (services.Store.ReadTrust(spec.RootDirectory)?.CommandsSha256 == commandsSha256)
        {
            await standardOutput.WriteLineAsync(JsonSerializer.Serialize(new { schemaVersion = SchemaVersion, status = "trusted" }, VerdictJson));
            return 0;
        }

        await standardError.WriteLineAsync($"{spec.SpecPath} runs these commands with your permissions:");
        foreach (var line in SpecDigests.Commands(spec))
        {
            await standardError.WriteLineAsync($"  {line}");
        }

        if (!services.Confirmation.CanAsk)
        {
            await standardError.WriteLineAsync("Approval needs a person at an interactive terminal; this input is redirected.");
            await standardOutput.WriteLineAsync(JsonSerializer.Serialize(new { schemaVersion = SchemaVersion, status = "not_approved", errorCode = "trust_needs_terminal" }, VerdictJson));
            return 1;
        }

        if (!services.Confirmation.Confirm("Allow these commands for this project?"))
        {
            await standardOutput.WriteLineAsync(JsonSerializer.Serialize(new { schemaVersion = SchemaVersion, status = "not_approved" }, VerdictJson));
            return 1;
        }

        services.Store.WriteTrust(new SpecTrust(SchemaVersion, spec.RootDirectory, commandsSha256, SpecDigests.Commands(spec), DateTimeOffset.UtcNow));
        await standardOutput.WriteLineAsync(JsonSerializer.Serialize(new { schemaVersion = SchemaVersion, status = "trusted" }, VerdictJson));
        return 0;
    }

    private async Task<int> TaskStartAsync(CliCommand command, CancellationToken cancellationToken)
    {
        var taskFile = Path.GetFullPath(command.TaskFile!);
        var taskId = command.TaskId ?? Path.GetFileNameWithoutExtension(taskFile);
        if (!File.Exists(taskFile))
        {
            return await TaskErrorAsync(taskId, 2, "task_file_not_found", $"No task file at {taskFile}.");
        }

        if (taskId.Length == 0 || !taskId.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'))
        {
            return await TaskErrorAsync(taskId, 2, "task_id_invalid", $"The task id '{taskId}' may hold only letters, digits, '-', '_', and '.'; pass --id.");
        }

        VerificationSpec spec;
        try
        {
            spec = VerificationSpecLoader.Load(SpecPathOf(command));
        }
        catch (VerificationSpecException exception)
        {
            return await TaskErrorAsync(taskId, 2, "spec_invalid", exception.Message);
        }

        if (spec.App is null)
        {
            return await TaskErrorAsync(taskId, 2, "spec_invalid", "spec.app is required for a task: the examiner explores that app.");
        }

        var trustResult = await EnsureTrustedAsync(spec, cancellationToken);
        if (trustResult.Untrusted is { } untrusted)
        {
            return await TaskErrorAsync(taskId, 1, trustResult.ErrorCode ?? "spec_untrusted", string.Join(" ", untrusted));
        }

        if (services.Store.ReadTask(spec.RootDirectory, taskId) is not null)
        {
            if (!command.Replace)
            {
                return await TaskErrorAsync(taskId, 1, "task_exists", $"Task '{taskId}' already has a frozen snapshot. Replacing it needs --replace and a person's approval.");
            }

            if (!services.Confirmation.CanAsk || !services.Confirmation.Confirm($"Replace the frozen criteria of task '{taskId}'?"))
            {
                return await TaskErrorAsync(taskId, 1, "replace_not_approved", "Replacing a task snapshot needs a person to approve it at an interactive terminal.");
            }
        }

        if (!File.Exists(spec.App.ExecutablePath))
        {
            return await TaskErrorAsync(taskId, 1, "app_not_found", $"The app executable does not exist: {spec.App.ExecutablePath}. Build the unchanged app first.");
        }

        var mcpExecutable = ResolveMcpExecutable(command.McpExecutablePath);
        if (mcpExecutable is null)
        {
            return await TaskErrorAsync(taskId, 1, "mcp_not_found", McpNotFoundMessage);
        }

        using var desktopLock = DesktopLock.TryTake(services.DesktopLockName);
        if (desktopLock is null)
        {
            return await TaskErrorAsync(taskId, 1, "desktop_busy", DesktopBusyMessage);
        }

        var taskText = await File.ReadAllTextAsync(taskFile, cancellationToken);
        var specSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(spec.SpecPath)));
        var tree = services.WorkingTree.Read(spec.RootDirectory);
        var runDirectory = Path.Combine(spec.RootDirectory, OutputRelativePath, "runs", $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fff}-task-{taskId}");
        Directory.CreateDirectory(runDirectory);

        // The examiner works outside the repository, in a folder of its own, so it can neither read the code
        // nor pick up the project's hooks and settings.
        var examinerDirectory = Path.Combine(Path.GetTempPath(), $"pointframe-examiner-{Guid.NewGuid():N}");
        Directory.CreateDirectory(examinerDirectory);
        await standardError.WriteLineAsync($"Pointframe verify: the examiner ({services.Examiner.Name}) is exploring the app to write the criteria for '{taskId}'. Do not use the mouse or keyboard.");
        ExaminerProposal proposal;
        try
        {
            var examinerData = Path.Combine(examinerDirectory, "data");
            Directory.CreateDirectory(examinerData);
            var examinerApp = spec.App with { Arguments = IsolatedArguments(spec.App, examinerData) };
            var examinerEnvironment = spec.App.Isolation?.EnvironmentVariable is { } variable
                ? new Dictionary<string, string> { [variable] = examinerData }
                : new Dictionary<string, string>();
            var policyPath = WritePolicy(examinerApp, examinerDirectory, "examiner");
            proposal = await services.Examiner.ProposeAsync(
                new ExaminerRequest(taskText, spec.App.Id, mcpExecutable, policyPath, examinerDirectory, examinerEnvironment), cancellationToken);
        }
        catch (Exception exception) when (exception is AgentException or IOException or System.ComponentModel.Win32Exception)
        {
            return await TaskErrorAsync(taskId, 1, "examiner_failed", exception.Message);
        }

        var scenarioJson = JsonSerializer.Serialize(new { id = $"task-{taskId}", criteria = proposal.Criteria, steps = proposal.Steps });
        await File.WriteAllTextAsync(Path.Combine(runDirectory, "proposal.json"), JsonSerializer.Serialize(proposal, VerdictJson), cancellationToken);
        VerificationScenario scenario;
        try
        {
            scenario = VerificationSpecLoader.ParseScenario(scenarioJson, "task");
            if (scenario.Criteria.Count == 0)
            {
                throw new VerificationSpecException("task: the examiner proposed no criteria.");
            }
        }
        catch (VerificationSpecException exception)
        {
            return await TaskErrorAsync(taskId, 1, "examiner_invalid", $"The examiner's scenario breaks the spec rules: {exception.Message}");
        }

        await standardError.WriteLineAsync("Pointframe verify: running the examiner's scenario on the unchanged app (every criterion must fail there).");
        var before = await RunScenarioAsync(spec.App, scenario, runDirectory, mcpExecutable, continueAfterFailedChecks: true, cancellationToken);
        var failBefore = FailBefore.Evaluate(scenario, before);
        if (failBefore.Status != FailBefore.Confirmed)
        {
            await standardOutput.WriteLineAsync(JsonSerializer.Serialize(new TaskStartResponse(
                SchemaVersion, "rejected", taskId, Criteria: proposal.Criteria, RequiredAutomationIds: proposal.RequiredAutomationIds,
                Notes: proposal.Notes, FailBefore: failBefore, ErrorCode: "fail_before_rejected",
                Error: string.Join(" ", failBefore.Problems)), VerdictJson));
            return 1;
        }

        using var scenarioDocument = JsonDocument.Parse(scenarioJson);
        var snapshot = new TaskSnapshot(
            SchemaVersion, taskId, spec.RootDirectory, taskText, SpecDigests.Hash(taskText), DateTimeOffset.UtcNow, tree.Head, tree.TreeHash,
            SpecDigests.Of(spec, specSha256), scenarioDocument.RootElement.Clone(), proposal.RequiredAutomationIds, proposal.Notes,
            services.Examiner.Name, failBefore);
        var snapshotSha256 = services.Store.WriteTask(snapshot);
        services.Store.WriteActiveTask(spec.RootDirectory, taskId);
        await standardOutput.WriteLineAsync(JsonSerializer.Serialize(new TaskStartResponse(
            SchemaVersion, "created", taskId, services.Store.TaskPath(spec.RootDirectory, taskId), snapshotSha256,
            proposal.Criteria, proposal.RequiredAutomationIds, proposal.Notes, failBefore), VerdictJson));
        await standardError.WriteLineAsync($"Task '{taskId}' is frozen. Verify the work with: Pointframe.Cli.exe verify run --task {taskId}");
        return 0;
    }

    internal static string WritePolicy(VerificationApp app, string directory, string name)
    {
        var policy = new
        {
            schemaVersion = 1,
            artifactRoot = directory,
            evidencePolicy = "All",
            profiles = new[]
            {
                new
                {
                    id = app.Id,
                    executablePath = app.ExecutablePath,
                    arguments = app.Arguments,
                    workingDirectory = app.WorkingDirectory,
                    allowAttach = false,
                    allowedActions = AllowedActions,
                    allowedGlobalHotkeys = new { },
                    allowedShellSurfaces = Array.Empty<string>(),
                    allowMonitorObservation = true,
                },
            },
        };
        var policyPath = Path.Combine(directory, $"desktop-policy-{name}.json");
        File.WriteAllText(policyPath, JsonSerializer.Serialize(policy, new JsonSerializerOptions { WriteIndented = true }));
        return policyPath;
    }

    // Each scenario gets its own MCP server, desktop session, and (with isolation) its own data folder, so
    // no state leaks between scenarios; a restart inside the scenario keeps the folder, so persistence
    // across a restart can be checked. The folder is deleted afterwards and a failed delete fails the
    // scenario, since leftover state could change the next run.
    private async Task<VerificationScenarioResult> RunScenarioAsync(
        VerificationApp app,
        VerificationScenario scenario,
        string runDirectory,
        string mcpExecutable,
        bool continueAfterFailedChecks,
        CancellationToken cancellationToken)
    {
        string? dataDirectory = null;
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var scenarioApp = app;
        if (app.Isolation is not null)
        {
            dataDirectory = Path.Combine(runDirectory, "data", scenario.Id);
            Directory.CreateDirectory(dataDirectory);
            if (app.Isolation.EnvironmentVariable is not null)
            {
                environment[app.Isolation.EnvironmentVariable] = dataDirectory;
            }

            scenarioApp = app with { Arguments = IsolatedArguments(app, dataDirectory) };
        }

        VerificationScenarioResult result;
        try
        {
            var policyPath = WritePolicy(scenarioApp, runDirectory, scenario.Id);
            await using var client = await services.ClientFactory.LaunchAsync(
                mcpExecutable, ["--desktop-testing", "--desktop-policy", policyPath], environment, cancellationToken);
            var displays = await ListDisplaysAsync(client, cancellationToken);
            result = await new DesktopScenarioRunner(client, app.Id, displays).RunAsync(scenario, cancellationToken, continueAfterFailedChecks);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or TimeoutException or JsonException or KeyNotFoundException or FormatException or IndexOutOfRangeException or System.ComponentModel.Win32Exception)
        {
            result = new VerificationScenarioResult(
                scenario.Id, VerificationStatus.Fail, null, [], [], null, false, null, [$"The Pointframe MCP server failed: {exception.Message}"]);
        }

        if (dataDirectory is not null)
        {
            try
            {
                Directory.Delete(dataDirectory, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                result = result with
                {
                    Status = VerificationStatus.Fail,
                    Problems = [.. result.Problems, $"The isolated data folder could not be deleted: {exception.Message}"],
                };
            }
        }

        return result;
    }

    internal static IReadOnlyList<string> IsolatedArguments(VerificationApp app, string dataDirectory) =>
        app.Isolation?.Argument is { } argument ? [.. app.Arguments, argument, dataDirectory] : app.Arguments;

    private (VerificationScenario? Scenario, VerificationTaskInfo? Info, (string Code, string Message)? Error) LoadTask(VerificationSpec spec, string taskId, string specSha256)
    {
        (TaskSnapshot Snapshot, string Sha256)? loaded;
        try
        {
            loaded = services.Store.ReadTask(spec.RootDirectory, taskId);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or IOException)
        {
            return (null, null, ("task_invalid", $"The snapshot of task '{taskId}' cannot be read: {exception.Message}"));
        }

        if (loaded is null)
        {
            return (null, null, ("task_not_found", $"No frozen snapshot for task '{taskId}'. Start one with 'Pointframe.Cli.exe verify task start <task-file>'."));
        }

        var (snapshot, sha256) = loaded.Value;
        if (snapshot.FailBefore.Status != FailBefore.Confirmed)
        {
            return (null, null, ("task_invalid", $"The snapshot of task '{taskId}' has no confirmed fail-before result."));
        }

        VerificationScenario scenario;
        try
        {
            scenario = VerificationSpecLoader.ParseScenario(snapshot.Scenario.GetRawText(), $"task '{taskId}'");
        }
        catch (VerificationSpecException exception)
        {
            return (null, null, ("task_invalid", exception.Message));
        }

        var changes = SpecDigests.Changes(snapshot.SpecAtStart, SpecDigests.Of(spec, specSha256));
        return (scenario, new VerificationTaskInfo(
            taskId, sha256, snapshot.CreatedUtc, snapshot.TreeHashAtStart, changes.Count > 0, changes, snapshot.RequiredAutomationIds), null);
    }

    // Approval order: exact existing approval; fixed rules; standard command policy; approver agent; person.
    internal async Task<(string? ApprovedBy, IReadOnlyList<string>? Untrusted, string? ErrorCode)> EnsureTrustedAsync(VerificationSpec spec, CancellationToken cancellationToken)
    {
        var commandsSha256 = SpecDigests.CommandsSha256(spec);
        var trust = services.Store.ReadTrust(spec.RootDirectory);
        if (trust?.CommandsSha256 == commandsSha256)
        {
            return (trust.ApprovedBy, null, null);
        }

        var commands = SpecDigests.Commands(spec);
        var violations = CommandPolicy.Violations(spec);
        if (violations.Count > 0)
        {
            return (null, violations
                .Prepend("The spec's commands break the fixed safety rules, so no agent was asked:")
                .Concat(commands.Prepend("The commands:"))
                .Append("Fix the spec, or a person approves it by running 'Pointframe.Cli.exe verify trust' in a terminal.")
                .ToArray(), "spec_untrusted");
        }

        if (CommandPolicy.IsStandard(spec))
        {
            const string Reason = "Standard verification commands approved by policy.";
            services.Store.WriteTrust(new SpecTrust(SchemaVersion, spec.RootDirectory, commandsSha256, commands, DateTimeOffset.UtcNow, "policy", Reason));
            await standardError.WriteLineAsync($"Pointframe verify: the spec's standard commands were approved by policy: {Reason}");
            return ("policy", null, null);
        }

        if (services.Approver is null)
        {
            return (null, UnavailableDetails(commands, "No approver agent is configured."), "approver_unavailable");
        }

        await standardError.WriteLineAsync("Pointframe verify: the spec's commands are new or changed; the approver agent is reviewing them.");
        var decision = await services.Approver.ReviewAsync(spec.RootDirectory, commands, trust?.Commands ?? [], cancellationToken);
        if (!decision.Approve)
        {
            if (decision.Unavailable)
            {
                return (null, UnavailableDetails(commands, decision.Reason), "approver_unavailable");
            }

            return (null, commands
                .Prepend($"The approver agent refused the spec's commands: {decision.Reason}")
                .Concat(decision.Concerns.Select(concern => $"Concern: {concern}"))
                .Append("Fix the spec, or a person approves it by running 'Pointframe.Cli.exe verify trust' in a terminal.")
                .ToArray(), "spec_untrusted");
        }

        var approvedBy = $"agent:{decision.Approver}";
        services.Store.WriteTrust(new SpecTrust(SchemaVersion, spec.RootDirectory, commandsSha256, commands, DateTimeOffset.UtcNow, approvedBy, decision.Reason));
        await standardError.WriteLineAsync($"Pointframe verify: the approver agent approved the commands: {decision.Reason}");
        return (approvedBy, null, null);
    }

    private static IReadOnlyList<string> UnavailableDetails(IReadOnlyList<string> commands, string cause) => commands
        .Prepend("The spec's commands are outside the standard set and could not be approved automatically.")
        .Prepend($"Approver unavailable: {cause}")
        .Append("Use an approver agent with network access, or a person can approve them by running 'Pointframe.Cli.exe verify trust' in a terminal.")
        .ToArray();

    internal string SpecPathOf(CliCommand command) => Path.GetFullPath(command.SpecPath ?? VerificationSpecLoader.DefaultSpecRelativePath);

    internal string? ResolveMcpExecutable(string? explicitPath)
    {
        var candidate = explicitPath
            ?? Environment.GetEnvironmentVariable(McpExecutableVariable)
            ?? services.InstalledMcpExecutable();
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        var fullPath = Path.GetFullPath(candidate);
        return File.Exists(fullPath) ? fullPath : null;
    }

    internal const string McpNotFoundMessage =
        "No Pointframe MCP server found. Pass --mcp <path>, set POINTFRAME_MCP_EXECUTABLE, or run 'Pointframe.Cli.exe mcp install --client vscode'.";

    private const string DesktopBusyMessage = "Another Pointframe verification run is using the desktop. Wait for it to finish.";

    private static async Task<IReadOnlyList<CaptureRectangle>> ListDisplaysAsync(IMcpToolClient client, CancellationToken cancellationToken)
    {
        var result = await client.CallToolAsync("list_displays", new { }, TimeSpan.FromSeconds(30), cancellationToken);
        var structured = result.TryGetProperty("structuredContent", out var content) ? content : result;
        var displays = structured.GetProperty("displays").EnumerateArray()
            .Select(display => display.GetProperty("boundsPixels"))
            .Select(bounds => new CaptureRectangle(
                bounds.GetProperty("x").GetInt32(),
                bounds.GetProperty("y").GetInt32(),
                bounds.GetProperty("width").GetInt32(),
                bounds.GetProperty("height").GetInt32()))
            .ToArray();
        return displays.Length == 0
            ? throw new InvalidOperationException("The MCP server reported no displays.")
            : displays;
    }

    private static string FirstFailure(VerificationScenarioResult result)
    {
        var step = result.Steps.FirstOrDefault(item => item.Status == VerificationStatus.Fail);
        if (step is not null)
        {
            var found = step.Actual is null ? string.Empty : $" (expected '{step.Expected}', found '{step.Actual}')";
            return $" - step {step.Index}: {step.Description}: {step.Code}{found}";
        }

        return result.Problems.Count > 0 ? $" - {result.Problems[0]}" : string.Empty;
    }

    private VerificationVerdict Verdict(
        RunContext context,
        string status,
        IReadOnlyList<VerificationGateResult> gates,
        IReadOnlyList<VerificationScenarioResult> scenarios,
        string? errorCode = null,
        string? error = null,
        IReadOnlyList<string>? details = null) => new(
        SchemaVersion, status, status == VerificationStatus.Pass, context.Started, Math.Round(context.Stopwatch.Elapsed.TotalSeconds, 1),
        context.SpecPath, context.SpecSha256, context.Provenance, context.Command.Only, context.Command.ScenarioId, context.Task,
        gates, scenarios, errorCode, error, details);

    private async Task<int> ErrorAsync(
        RunContext context,
        int exitCode,
        string code,
        string message,
        bool writeFile,
        IReadOnlyList<string>? details = null,
        IReadOnlyList<VerificationGateResult>? gates = null)
    {
        await standardError.WriteLineAsync($"Pointframe verify failed: {message}");
        foreach (var line in details ?? [])
        {
            await standardError.WriteLineAsync($"  {line}");
        }

        var verdict = Verdict(context, VerificationStatus.Fail, gates ?? [], [], code, message, details);
        if (writeFile && context.OutputDirectory is not null)
        {
            await WriteVerdictAsync(verdict, context.OutputDirectory, CancellationToken.None);
        }
        else
        {
            await standardOutput.WriteLineAsync(JsonSerializer.Serialize(verdict, VerdictJson));
        }

        return exitCode;
    }

    private async Task<int> TaskErrorAsync(string taskId, int exitCode, string code, string message)
    {
        await standardError.WriteLineAsync($"Pointframe verify failed: {message}");
        await standardOutput.WriteLineAsync(JsonSerializer.Serialize(new TaskStartResponse(SchemaVersion, "error", taskId, ErrorCode: code, Error: message), VerdictJson));
        return exitCode;
    }

    private async Task WriteVerdictAsync(VerificationVerdict verdict, string outputDirectory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(outputDirectory);
        var json = JsonSerializer.Serialize(verdict, VerdictJson);
        var verdictPath = Path.Combine(outputDirectory, "verdict.json");
        await File.WriteAllTextAsync(verdictPath, json, cancellationToken);
        await standardOutput.WriteLineAsync(json);
        await standardError.WriteLineAsync($"Verdict: {verdict.Status} - {verdictPath}");
    }

    private sealed class RunContext(CliCommand command, DateTimeOffset started, Stopwatch stopwatch, string specPath)
    {
        internal CliCommand Command { get; } = command;

        internal DateTimeOffset Started { get; } = started;

        internal Stopwatch Stopwatch { get; } = stopwatch;

        internal string SpecPath { get; } = specPath;

        internal string? SpecSha256 { get; set; }

        internal string? OutputDirectory { get; set; }

        internal VerificationProvenance Provenance { get; set; } = new(null, null, null, null, null);

        internal VerificationTaskInfo? Task { get; set; }
    }
}

// A desktop run moves the real mouse and keyboard; two at once would drive each other's apps. A named
// semaphore, not a mutex: it is released after awaits on another thread, and Windows destroys it when the
// last process holding it exits, so a crashed run does not leave the desktop locked.
internal sealed class DesktopLock : IDisposable
{
    private readonly Semaphore _semaphore;

    private DesktopLock(Semaphore semaphore)
    {
        _semaphore = semaphore;
    }

    internal static DesktopLock? TryTake(string name)
    {
        var semaphore = new Semaphore(1, 1, name);
        if (semaphore.WaitOne(TimeSpan.Zero))
        {
            return new DesktopLock(semaphore);
        }

        semaphore.Dispose();
        return null;
    }

    public void Dispose()
    {
        _semaphore.Release();
        _semaphore.Dispose();
    }
}
