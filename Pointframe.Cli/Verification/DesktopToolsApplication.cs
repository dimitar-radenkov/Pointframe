using System.Diagnostics;
using System.Text.Json;

namespace Pointframe.Cli;

internal sealed record DesktopSetupResponse(
    int SchemaVersion,
    bool Success,
    string Status,
    string? Code,
    string Message,
    IReadOnlyList<string> Clients,
    IReadOnlyList<string> FilesWritten,
    IReadOnlyList<string> FilesUnchanged,
    string? ProfileId,
    string? ServerPath,
    string? ServerVersion,
    bool ServerInstalled,
    IReadOnlyList<string>? Tools,
    IReadOnlyList<string> Warnings,
    VerificationNextStep? NextStep);

// `mcp serve` and `verify setup`: interactive desktop tools for the app a project's verification spec
// declares, under the same approval as `verify run` (the spec's commands are trusted, or nothing starts).
// `serve` is the command a committed agent config runs, so standard output belongs to the MCP server alone:
// every refusal goes to standard error with a stable code.
internal sealed class DesktopToolsApplication(
    VerificationServices services,
    VerificationApplication verification,
    TextWriter standardOutput,
    TextWriter standardError)
{
    private const int SchemaVersion = 1;

    private sealed record Refusal(int ExitCode, string Code, string Message);

    internal static string? FindProjectRoot(string startDirectory)
    {
        for (var directory = new DirectoryInfo(Path.GetFullPath(startDirectory)); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, VerificationSpecLoader.DefaultSpecRelativePath)))
            {
                return directory.FullName;
            }
        }

        return null;
    }

    internal async Task<int> ServeAsync(CliCommand command, string currentDirectory, CancellationToken cancellationToken)
    {
        var root = command.ProjectPath is not null ? Path.GetFullPath(command.ProjectPath) : FindProjectRoot(currentDirectory);
        if (root is null)
        {
            return await RefuseServeAsync(new Refusal(1, "spec_invalid", $"No {VerificationSpecLoader.DefaultSpecRelativePath} found in {currentDirectory} or above it. Pass --project <dir>."));
        }

        var (spec, refusal) = await AuthorizeAsync(Path.Combine(root, VerificationSpecLoader.DefaultSpecRelativePath), cancellationToken);
        if (spec is null)
        {
            return await RefuseServeAsync(refusal!);
        }

        var resolution = await verification.ResolveMcpExecutableAsync(command.McpExecutablePath, cancellationToken);
        var mcp = resolution.Path;
        if (mcp is null)
        {
            return await RefuseServeAsync(new Refusal(1, resolution.ErrorCode ?? "mcp_not_found", resolution.Error ?? VerificationApplication.McpNotFoundMessage));
        }

        var app = spec.App!;
        var projectDirectory = Path.Combine(services.Store.BaseDirectory, "projects", VerificationStore.ProjectKey(spec.RootDirectory));
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? dataDirectory = null;
        string? sessionPolicy = null;
        try
        {
            Directory.CreateDirectory(projectDirectory);

            // Isolation is honoured: the app starts on a fresh data folder that is deleted when the server
            // exits, so exploring never touches the app's normal data. Without isolation in the spec it does.
            var policyName = "serve";
            if (app.Isolation is not null)
            {
                dataDirectory = Path.Combine(projectDirectory, "data", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dataDirectory);
                if (app.Isolation.EnvironmentVariable is not null)
                {
                    environment[app.Isolation.EnvironmentVariable] = dataDirectory;
                }

                if (app.Isolation.Argument is not null)
                {
                    app = app with { Arguments = VerificationApplication.IsolatedArguments(app, dataDirectory) };
                    policyName = $"serve-{Guid.NewGuid():N}";
                }
            }

            var policyPath = VerificationApplication.WritePolicy(app, projectDirectory, policyName);
            sessionPolicy = policyName == "serve" ? null : policyPath;
            var host = services.ServerHost ?? new InheritedStdioMcpServerHost();
            return await host.RunAsync(mcp, ["--desktop-testing", "--desktop-policy", policyPath], environment, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return await RefuseServeAsync(new Refusal(1, "mcp_start_failed", exception.Message));
        }
        finally
        {
            await CleanUpAsync(dataDirectory, sessionPolicy);
        }
    }

    internal async Task<int> SetupAsync(CliCommand command, CancellationToken cancellationToken)
    {
        var specPath = verification.SpecPathOf(command);
        var clients = ProjectMcpClientConfig.Expand(command.McpClient ?? ProjectMcpClientConfig.ClaudeCode);
        var (spec, refusal) = await AuthorizeAsync(specPath, cancellationToken);
        if (spec is null)
        {
            await standardError.WriteLineAsync($"Pointframe verify setup failed [{refusal!.Code}]: {refusal.Message}");
            await WriteAsync(Failure(refusal.Code, refusal.Message, clients));
            return refusal.ExitCode;
        }

        var warnings = new List<string>();
        var resolution = await verification.ResolveMcpExecutableAsync(command.McpExecutablePath, cancellationToken);
        var mcp = resolution.Path;
        var installed = false;
        string? version = null;
        string? failureCode = null;
        string? failureMessage = null;
        if (mcp is null && resolution.ErrorCode is not null)
        {
            failureCode = resolution.ErrorCode;
            failureMessage = resolution.Error;
        }
        else if (mcp is null && command.McpExecutablePath is null && services.McpInstaller is { } installer)
        {
            try
            {
                var installation = await installer.InstallLatestAsync(dryRun: false, cancellationToken);
                mcp = installation.ExecutablePath;
                version = installation.Version;
                installed = true;
            }
            catch (Exception exception) when (exception is HttpRequestException or InvalidDataException or IOException or UnauthorizedAccessException)
            {
                failureCode = "mcp_install_failed";
                failureMessage = $"The Pointframe MCP server was not found and could not be installed: {exception.Message}";
            }
        }
        else if (mcp is null)
        {
            failureCode = resolution.ErrorCode ?? "mcp_not_found";
            failureMessage = resolution.Error ?? VerificationApplication.McpNotFoundMessage;
        }

        if (mcp is not null && version is null)
        {
            version = services.McpInstaller?.GetCurrent() is { } current && string.Equals(current.ExecutablePath, mcp, StringComparison.OrdinalIgnoreCase)
                ? current.Version
                : FileVersionOf(mcp);
        }

        var written = new List<string>();
        var unchanged = new List<string>();
        foreach (var client in clients)
        {
            try
            {
                var result = ProjectMcpClientConfig.Write(client, spec.RootDirectory);
                (result.Changed ? written : unchanged).Add(result.RelativePath);
            }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
            {
                failureCode ??= "client_config_invalid";
                failureMessage ??= $"Could not write the {client} configuration: {exception.Message}";
            }
        }

        IReadOnlyList<string>? tools = null;
        if (failureCode is null)
        {
            var cli = (services.CommandResolver ?? new PointframeCommandResolver()).Resolve();
            if (cli.Path is null || !cli.Ok)
            {
                failureCode = "pointframe_not_on_path";
                failureMessage = cli.Path is null
                    ? "The agent config runs 'pointframe mcp serve', but pointframe.exe is not on PATH. Run Pointframe.Cli.exe install or winget install DimitarRadenkov.Pointframe.Cli, then open a new terminal."
                    : $"The agent config runs 'pointframe mcp serve', but {cli.Path} is not a working Pointframe CLI ({cli.Error}).";
            }
            else
            {
                var smoke = await SmokeAsync(cli.Path, spec, command.McpExecutablePath, warnings, cancellationToken);
                tools = smoke.Tools;
                failureCode = smoke.Code;
                failureMessage = smoke.Message;
            }
        }

        var success = failureCode is null;
        await WriteAsync(new DesktopSetupResponse(
            SchemaVersion,
            success,
            !success ? "error" : written.Count == 0 && !installed ? "unchanged" : "configured",
            failureCode,
            success
                ? $"Interactive desktop tools are set up for '{spec.App!.Id}'; they run under the same approval as 'pointframe verify run'."
                : failureMessage!,
            clients,
            written,
            unchanged,
            spec.App!.Id,
            mcp,
            version,
            installed,
            tools,
            warnings,
            success
                ? new VerificationNextStep("none", null, null, "Restart your agent session, then call desktop_list_apps.")
                : VerificationNextSteps.For("error", failureCode, failureMessage)));
        if (!success)
        {
            await standardError.WriteLineAsync($"Pointframe verify setup failed [{failureCode}]: {failureMessage}");
        }

        return success ? 0 : 1;
    }

    private async Task<(IReadOnlyList<string>? Tools, string? Code, string? Message)> SmokeAsync(
        string cliExecutable,
        VerificationSpec spec,
        string? mcpOverride,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        List<string> arguments = ["mcp", "serve", "--project", spec.RootDirectory];
        if (mcpOverride is not null)
        {
            arguments.AddRange(["--mcp", mcpOverride]);
        }

        try
        {
            await using var client = await services.ClientFactory.LaunchAsync(cliExecutable, arguments, new Dictionary<string, string>(), cancellationToken);
            var listed = await client.ListToolsAsync(TimeSpan.FromSeconds(30), cancellationToken);
            var tools = listed.GetProperty("tools").EnumerateArray()
                .Select(tool => tool.GetProperty("name").GetString())
                .OfType<string>()
                .ToArray();
            if (!tools.Contains("desktop_start_test_session", StringComparer.Ordinal))
            {
                return (tools, "smoke_tools_missing", "The server started but does not offer desktop_start_test_session, so desktop testing is not enabled.");
            }

            if (!File.Exists(spec.App!.ExecutablePath))
            {
                warnings.Add($"app_not_built: {spec.App.ExecutablePath} does not exist yet; build the app before an agent starts a desktop session.");
                return (tools, null, null);
            }

            var response = await client.CallToolAsync("desktop_list_apps", new { actionId = Guid.NewGuid().ToString() }, TimeSpan.FromSeconds(30), cancellationToken);
            var structured = response.TryGetProperty("structuredContent", out var content) ? content : response;
            var listsApp = structured.TryGetProperty("apps", out var apps)
                && apps.EnumerateArray().Any(app => app.TryGetProperty("id", out var id) && id.GetString() == spec.App.Id);
            return listsApp
                ? (tools, null, null)
                : (tools, "smoke_app_not_listed", $"desktop_list_apps did not list '{spec.App.Id}'. Response: {structured.GetRawText()}");
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or TimeoutException or JsonException or KeyNotFoundException or System.ComponentModel.Win32Exception)
        {
            return (null, "smoke_failed", $"'pointframe mcp serve' did not start a working server: {exception.Message}");
        }
    }

    private async Task<(VerificationSpec? Spec, Refusal? Refusal)> AuthorizeAsync(string specPath, CancellationToken cancellationToken)
    {
        VerificationSpec spec;
        try
        {
            spec = VerificationSpecLoader.Load(specPath);
        }
        catch (VerificationSpecException exception)
        {
            return (null, new Refusal(2, "spec_invalid", exception.Message));
        }

        if (spec.App is not { } app)
        {
            return (null, new Refusal(1, "no_app", "The spec declares no app, so there is nothing to test interactively. Add spec.app to .pointframe/verify.json."));
        }

        // A link inside the project can lead out of it. A path the spec names outside the project is a
        // different case: that is what a person approves with 'verify trust', and trust is checked below.
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(spec.RootDirectory)) + Path.DirectorySeparatorChar;
        foreach (var path in new[] { app.ExecutablePath, app.WorkingDirectory })
        {
            if (CommandPolicy.LexicallyInside(path, root) && !PathContainment.IsInside(path, root))
            {
                return (null, new Refusal(1, "app_outside_project", $"{path} is inside the project folder but resolves to {PathContainment.Resolve(path)}, outside it."));
            }
        }

        var (_, untrusted, errorCode) = await verification.EnsureTrustedAsync(spec, cancellationToken);
        if (untrusted is not null)
        {
            foreach (var line in untrusted)
            {
                await standardError.WriteLineAsync($"  {line}");
            }

            return (null, new Refusal(1, errorCode ?? "spec_untrusted", errorCode == "approver_unavailable"
                ? "The spec's commands are outside the standard set, and no approver agent was available. Run 'pointframe verify trust' in a terminal."
                : "The spec's commands (gates and app) run with your permissions and were not approved. Run 'pointframe verify trust' in a terminal."));
        }

        return (spec, null);
    }

    private async Task<int> RefuseServeAsync(Refusal refusal)
    {
        var nextStep = VerificationNextSteps.For("error", refusal.Code, refusal.Message);
        var command = nextStep.Command is null ? string.Empty : $" Command: `{nextStep.Command}`.";
        await standardError.WriteLineAsync($"Pointframe mcp serve failed [{refusal.Code}]: {refusal.Message} Next step: {nextStep.Text}{command}");
        return 1;
    }

    private async Task CleanUpAsync(string? dataDirectory, string? sessionPolicy)
    {
        try
        {
            if (sessionPolicy is not null && File.Exists(sessionPolicy))
            {
                File.Delete(sessionPolicy);
            }

            if (dataDirectory is not null && Directory.Exists(dataDirectory))
            {
                Directory.Delete(dataDirectory, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await standardError.WriteLineAsync($"Pointframe mcp serve: could not delete the isolated data folder {dataDirectory}: {exception.Message}");
        }
    }

    private static DesktopSetupResponse Failure(string code, string message, IReadOnlyList<string> clients) =>
        new(SchemaVersion, false, "error", code, message, clients, [], [], null, null, null, false, null, [], VerificationNextSteps.For("error", code, message));

    private static string? FileVersionOf(string path)
    {
        try
        {
            return FileVersionInfo.GetVersionInfo(path).ProductVersion;
        }
        catch (Exception exception) when (exception is IOException or FileNotFoundException)
        {
            return null;
        }
    }

    private Task WriteAsync(DesktopSetupResponse response) =>
        standardOutput.WriteLineAsync(JsonSerializer.Serialize(response, VerificationApplication.VerdictJson));
}
