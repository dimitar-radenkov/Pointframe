using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace Pointframe.Cli;

internal interface IVerificationInitFileSystem
{
    bool FileExists(string path);

    bool DirectoryExists(string path);

    string ReadAllText(string path);

    void WriteAllText(string path, string contents);

    IEnumerable<string> EnumerateFiles(string path, string pattern, SearchOption option);

    void CreateDirectory(string path);
}

internal sealed class PhysicalVerificationInitFileSystem : IVerificationInitFileSystem
{
    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public string ReadAllText(string path) => File.ReadAllText(path);

    public void WriteAllText(string path, string contents) => File.WriteAllText(path, contents);

    public IEnumerable<string> EnumerateFiles(string path, string pattern, SearchOption option) => Directory.EnumerateFiles(path, pattern, option);

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);
}

internal sealed record VerificationInitResponse(
    string Status,
    IReadOnlyList<string> FilesWritten,
    IReadOnlyList<string> FilesLeftAlone,
    IReadOnlyList<VerificationInitGate> Gates,
    string? App,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> NextSteps,
    string? Error = null);

internal sealed record VerificationInitGate(string Id, string Run);

internal sealed class VerificationInit(
    IVerificationInitFileSystem fileSystem,
    TextWriter standardOutput,
    TextWriter standardError,
    IPointframeCommandResolver? commandResolver = null,
    string? runningCliVersion = null)
{
    private const string HookCommand = "pointframe verify hook stop --review";
    private const string AgentsStart = "<!-- pointframe-verify:start -->";
    private const string AgentsEnd = "<!-- pointframe-verify:end -->";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    internal async Task<int> RunAsync(
        CliCommand command,
        string rootDirectory,
        Func<string?, string?> resolveMcp,
        IMcpToolClientFactory clientFactory,
        string desktopLockName,
        CancellationToken cancellationToken)
    {
        rootDirectory = Path.GetFullPath(rootDirectory);
        var filesWritten = new List<string>();
        var filesLeftAlone = new List<string>();
        var warnings = new List<string>();
        var specPath = Path.Combine(rootDirectory, VerificationSpecLoader.DefaultSpecRelativePath);
        var hadSpec = fileSystem.FileExists(specPath);
        var preserveExistingSpec = hadSpec && !command.Force;
        var gates = DetectGates(rootDirectory);
        var app = command.AppPath is not null
            ? Path.GetFullPath(Path.Combine(rootDirectory, command.AppPath))
            : DetectApp(rootDirectory, warnings);
        var appRelative = app is null ? null : Path.GetRelativePath(rootDirectory, app).Replace('\\', '/');
        string? scenario = null;

        if (preserveExistingSpec)
        {
            filesLeftAlone.Add(Relative(rootDirectory, specPath));
        }

        if (!preserveExistingSpec && command.Explore && app is not null && fileSystem.FileExists(app))
        {
            var mcp = resolveMcp(command.McpExecutablePath);
            if (mcp is null)
            {
                warnings.Add("The app could not be explored because no Pointframe MCP server was found; pass --mcp to verify run later, or set POINTFRAME_MCP_EXECUTABLE.");
            }
            else
            {
                var desktopLock = DesktopLock.TryTake(desktopLockName);
                if (desktopLock is null)
                {
                    warnings.Add("The app could not be explored because another verification run is using the desktop.");
                }
                else
                {
                    using (desktopLock)
                    {
                        try
                        {
                            var exploration = await ExploreAsync(rootDirectory, app, mcp, clientFactory, cancellationToken);
                            scenario = exploration.Scenario;
                            if (exploration.AutomationIdCount is > 0 and < 3)
                            {
                                warnings.Add("The app exposed fewer than 3 automation ids; checks will be fragile. Give controls AutomationId or Name values.");
                            }
                        }
                        catch (Exception exception) when (exception is IOException or InvalidOperationException or TimeoutException or JsonException or KeyNotFoundException or System.ComponentModel.Win32Exception)
                        {
                            warnings.Add($"The app could not be explored: {exception.Message}");
                        }
                    }
                }
            }
        }
        else if (!preserveExistingSpec && command.Explore)
        {
            warnings.Add("The app is not built yet, so no scenario was created. Build it, then run 'pointframe verify init --app <path> --explore'.");
        }

        if (!preserveExistingSpec && appRelative is not null && scenario is null)
        {
            warnings.Add("No desktop scenario was created. Add one under spec.scenarios in .pointframe/verify.json, or run 'pointframe verify init --app <path> --explore' after building the app.");
        }

        if (!preserveExistingSpec)
        {
            var spec = BuildSpec(appRelative, gates, scenario);
            var json = JsonSerializer.Serialize(spec, JsonOptions);
            try
            {
                using var document = JsonDocument.Parse(json);
                _ = VerificationSpecLoader.Parse(document.RootElement, specPath, rootDirectory);
            }
            catch (VerificationSpecException exception)
            {
                await WriteAsync(new VerificationInitResponse("unchanged", [], [], gates.Select(DescribeGate).ToArray(), appRelative, warnings, [], exception.Message));
                return 1;
            }

            if (gates.Count == 0 && scenario is null)
            {
                await WriteAsync(new VerificationInitResponse("unchanged", [], [], [], appRelative, warnings,
                    ["Add a build/test gate to package.json or provide a .NET solution, or explore a built app."],
                    "No verification gates or desktop scenarios were detected; no files were written."));
                return 1;
            }

            fileSystem.CreateDirectory(Path.GetDirectoryName(specPath)!);
            fileSystem.WriteAllText(specPath, json + Environment.NewLine);
            filesWritten.Add(Relative(rootDirectory, specPath));
        }

        if (command.Hooks is "claude" or "both")
        {
            MergeHook(rootDirectory, ".claude/settings.json", filesWritten, filesLeftAlone);
        }

        if (command.Hooks is "codex" or "both")
        {
            MergeHook(rootDirectory, ".codex/hooks.json", filesWritten, filesLeftAlone);
        }

        if (command.AgentsMd)
        {
            MergeAgents(rootDirectory, filesWritten, filesLeftAlone);
        }

        MergeGitIgnore(rootDirectory, filesWritten, filesLeftAlone);

        var nextSteps = new List<string> { "pointframe verify run", "pointframe verify trust if a nonstandard command is refused or the approver is unavailable" };
        if (command.Hooks is "claude" or "codex" or "both")
        {
            var hookCommand = (commandResolver ?? new PointframeCommandResolver()).Resolve();
            if (hookCommand.Path is null)
            {
                warnings.Add("pointframe.exe was not found on PATH. Stop hooks will not run, so the agent can stop unverified. Run Pointframe.Cli.exe install or winget install DimitarRadenkov.Pointframe.Cli, then restart the agent.");
                nextSteps.Add("Install the Pointframe CLI and restart the agent so the Stop hook can find pointframe.exe.");
            }
            else if (!hookCommand.Ok)
            {
                warnings.Add($"The command at {hookCommand.Path} is not the Pointframe CLI ({hookCommand.Error}). Remove it or move it later on PATH; a stale scoop shim is one example (scoop is unsupported).");
                nextSteps.Add($"Remove or move {hookCommand.Path} later on PATH, then restart the agent.");
            }
            else if (runningCliVersion is not null && hookCommand.Version != runningCliVersion)
            {
                warnings.Add($"The hook resolves {hookCommand.Path} to {hookCommand.Version}, while this init command is {runningCliVersion}.");
            }
        }

        var status = hadSpec ? (filesWritten.Count == 0 ? "unchanged" : "updated") : "created";

        await WriteAsync(new VerificationInitResponse(status, filesWritten, filesLeftAlone, preserveExistingSpec ? [] : gates.Select(DescribeGate).ToArray(), preserveExistingSpec ? null : appRelative, warnings, nextSteps));
        return 0;
    }

    internal IReadOnlyList<VerificationGate> DetectGates(string rootDirectory)
    {
        var slnx = fileSystem.EnumerateFiles(rootDirectory, "*.slnx", SearchOption.TopDirectoryOnly).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        var solution = slnx ?? fileSystem.EnumerateFiles(rootDirectory, "*.sln", SearchOption.TopDirectoryOnly).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        if (solution is not null)
        {
            var solutionName = Path.GetFileName(solution);
            var gates = new List<VerificationGate> { Gate(rootDirectory, "build", $"dotnet build {Quote(solutionName)} -c Release") };
            if (HasTestProject(rootDirectory, solution))
            {
                gates.Add(Gate(rootDirectory, "tests", $"dotnet test {Quote(solutionName)} -c Release --no-build"));
            }

            return gates;
        }

        var packageJson = Path.Combine(rootDirectory, "package.json");
        if (!fileSystem.FileExists(packageJson))
        {
            return [];
        }

        try
        {
            using var package = JsonDocument.Parse(fileSystem.ReadAllText(packageJson));
            if (!package.RootElement.TryGetProperty("scripts", out var scripts) || scripts.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            var gates = new List<VerificationGate>();
            foreach (var script in new[] { "build", "lint", "test" })
            {
                if (scripts.TryGetProperty(script, out _))
                {
                    var run = script == "test" ? "npm test" : $"npm run {script}";
                    gates.Add(Gate(rootDirectory, script, run));
                }
            }

            return gates;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private bool HasTestProject(string rootDirectory, string solution)
    {
        var solutionText = fileSystem.ReadAllText(solution);
        var projects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (solution.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var xml = XDocument.Parse(solutionText);
                foreach (var project in xml.Descendants().Where(item => item.Name.LocalName == "Project"))
                {
                    var path = project.Attribute("Path")?.Value ?? project.Value;
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        projects.Add(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(solution)!, path)));
                    }
                }
            }
            catch (System.Xml.XmlException)
            {
            }
        }
        else
        {
            foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(solutionText, "Project\\([^\\r\\n]+?\\)\\s*=\\s*\"[^\"]+\",\\s*\"(?<path>[^\"]+\\.csproj)\"", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                projects.Add(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(solution)!, match.Groups["path"].Value.Replace('\\', Path.DirectorySeparatorChar))));
            }
        }

        if (projects.Count == 0)
        {
            foreach (var project in fileSystem.EnumerateFiles(rootDirectory, "*.csproj", SearchOption.AllDirectories))
            {
                projects.Add(project);
            }
        }

        var centralPackages = Path.Combine(rootDirectory, "Directory.Packages.props");
        var packageNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (fileSystem.FileExists(centralPackages))
        {
            AddPackages(fileSystem.ReadAllText(centralPackages), packageNames);
        }

        foreach (var project in projects.Where(fileSystem.FileExists))
        {
            AddPackages(fileSystem.ReadAllText(project), packageNames);
        }

        return packageNames.Any(name => name.Equals("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("xunit", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("NUnit", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("MSTest", StringComparison.OrdinalIgnoreCase));
    }

    internal string? DetectApp(string rootDirectory, List<string> warnings)
    {
        var candidates = new List<string>();
        foreach (var project in fileSystem.EnumerateFiles(rootDirectory, "*.csproj", SearchOption.AllDirectories).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var xml = XDocument.Parse(fileSystem.ReadAllText(project));
                var outputType = xml.Descendants().FirstOrDefault(item => item.Name.LocalName == "OutputType")?.Value.Trim();
                if (!string.Equals(outputType, "WinExe", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (xml.Descendants().Any(item => item.Name.LocalName == "TargetFrameworks"))
                {
                    candidates.Add(project + " (multiple target frameworks)");
                    continue;
                }

                var framework = xml.Descendants().FirstOrDefault(item => item.Name.LocalName == "TargetFramework")?.Value.Trim();
                if (string.IsNullOrWhiteSpace(framework))
                {
                    candidates.Add(project + " (no TargetFramework)");
                    continue;
                }

                var assembly = xml.Descendants().FirstOrDefault(item => item.Name.LocalName == "AssemblyName")?.Value.Trim();
                var name = string.IsNullOrWhiteSpace(assembly) ? Path.GetFileNameWithoutExtension(project) : assembly;
                candidates.Add(Path.Combine(Path.GetDirectoryName(project)!, "bin", "Release", framework, name + ".exe"));
            }
            catch (System.Xml.XmlException)
            {
            }
        }

        if (candidates.Count == 1 && !candidates[0].Contains(" (", StringComparison.Ordinal))
        {
            return candidates[0];
        }

        if (candidates.Count > 0)
        {
            warnings.Add($"Could not select one WinExe project. Candidates: {string.Join(", ", candidates.Select(path => Path.GetRelativePath(rootDirectory, path).Replace('\\', '/')))}. Pass --app <path>.");
        }
        else if (fileSystem.EnumerateFiles(rootDirectory, "*.slnx", SearchOption.TopDirectoryOnly).Any()
            || fileSystem.EnumerateFiles(rootDirectory, "*.sln", SearchOption.TopDirectoryOnly).Any())
        {
            warnings.Add("No .NET WinExe project was detected as an app candidate. Pass --app <path> to configure desktop scenarios.");
        }

        return null;
    }

    private async Task<(string Scenario, int AutomationIdCount)> ExploreAsync(string rootDirectory, string executable, string mcpPath, IMcpToolClientFactory factory, CancellationToken cancellationToken)
    {
        var id = AppIdFromPath(executable);
        var app = new VerificationApp(id, executable, [], Path.GetDirectoryName(executable)!);
        var temp = Path.Combine(Path.GetTempPath(), "Pointframe.Verify.Init", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var policy = VerificationApplication.WritePolicy(app, temp, "init-explore");
            await using var client = await factory.LaunchAsync(mcpPath, ["--desktop-testing", "--desktop-policy", policy], new Dictionary<string, string>(), cancellationToken);
            var displaysResponse = await client.CallToolAsync("list_displays", new { }, TimeSpan.FromSeconds(30), cancellationToken);
            var displays = displaysResponse.TryGetProperty("structuredContent", out var displayContent) ? displayContent : displaysResponse;
            var bounds = displays.GetProperty("displays").EnumerateArray().Select(display => display.GetProperty("boundsPixels")).ToArray();
            var ids = await new DesktopScenarioRunner(client, id, bounds.Select(item => new CaptureRectangle(
                item.GetProperty("x").GetInt32(), item.GetProperty("y").GetInt32(), item.GetProperty("width").GetInt32(), item.GetProperty("height").GetInt32())).ToArray())
                .ObserveAutomationIdsAsync(Guid.NewGuid().ToString("N"), cancellationToken);
            if (ids.Count == 0)
            {
                throw new InvalidOperationException("The app exposed no automation ids to use for a starter scenario.");
            }

            return (Scenario: JsonSerializer.Serialize(new
            {
                id = "app-starts",
                criteria = Array.Empty<string>(),
                steps = new[] { new { check = new { kind = "exists", automationId = ids[0] } } },
            }, JsonOptions), AutomationIdCount: ids.Count);
        }
        finally
        {
            if (Directory.Exists(temp))
            {
                Directory.Delete(temp, true);
            }
        }
    }

    private object BuildSpec(string? appPath, IReadOnlyList<VerificationGate> gates, string? scenario)
    {
        object? app = appPath is null ? null : new { id = AppIdFromPath(appPath), executable = appPath };
        var gateItems = gates.Select(item => new { id = item.Id, run = item.Run }).ToArray();
        var scenarios = scenario is null ? Array.Empty<JsonElement>() : [JsonDocument.Parse(scenario).RootElement.Clone()];
        return new { schemaVersion = 1, app, gates = gateItems, scenarios };
    }

    private void MergeHook(string rootDirectory, string relativePath, List<string> written, List<string> untouched)
    {
        var path = Path.Combine(rootDirectory, relativePath);
        JsonObject root;
        if (fileSystem.FileExists(path))
        {
            try
            {
                root = JsonNode.Parse(fileSystem.ReadAllText(path), new JsonNodeOptions { PropertyNameCaseInsensitive = false }, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject ?? throw new JsonException("Root must be an object.");
            }
            catch (JsonException exception)
            {
                throw new InvalidOperationException($"Cannot merge {relativePath}: {exception.Message}");
            }
        }
        else
        {
            root = new JsonObject();
        }

        var hooks = root["hooks"] as JsonObject ?? new JsonObject();
        root["hooks"] = hooks;
        var stops = hooks["Stop"] as JsonArray ?? new JsonArray();
        hooks["Stop"] = stops;
        if (ContainsVerifyHook(stops))
        {
            untouched.Add(relativePath);
            return;
        }

        stops.Add(new JsonObject
        {
            ["hooks"] = new JsonArray(new JsonObject
            {
                ["type"] = "command",
                ["command"] = HookCommand,
                ["timeout"] = 1800,
            }),
        });
        fileSystem.CreateDirectory(Path.GetDirectoryName(path)!);
        fileSystem.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        written.Add(relativePath);
    }

    private static bool IsVerifyHook(JsonObject entry) => entry["command"]?.GetValue<string>()?.Contains("verify hook stop", StringComparison.OrdinalIgnoreCase) == true;

    private static bool ContainsVerifyHook(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            return IsVerifyHook(obj) || obj.Any(pair => ContainsVerifyHook(pair.Value));
        }

        return node is JsonArray array && array.Any(ContainsVerifyHook);
    }

    private void MergeAgents(string rootDirectory, List<string> written, List<string> untouched)
    {
        const string Section = "Run `pointframe verify run` and check `pointframe verify status` shows pass and fresh before saying the task is done. Read the failure details and fix the cause. Never edit `.pointframe/verify.json` or a frozen task to make a check pass. For desktop work with a task file, `pointframe verify task start <task-file>` freezes its criteria first.";
        var path = Path.Combine(rootDirectory, "AGENTS.md");
        var original = fileSystem.FileExists(path) ? fileSystem.ReadAllText(path) : string.Empty;
        var start = original.IndexOf(AgentsStart, StringComparison.Ordinal);
        var end = original.IndexOf(AgentsEnd, StringComparison.Ordinal);
        var block = $"{AgentsStart}{Environment.NewLine}{Section}{Environment.NewLine}{AgentsEnd}";
        string updated;
        if (start >= 0 && end > start)
        {
            updated = original[..start] + block + original[(end + AgentsEnd.Length)..];
        }
        else
        {
            updated = original.TrimEnd() + (original.Length == 0 ? string.Empty : Environment.NewLine + Environment.NewLine) + block + Environment.NewLine;
        }

        if (string.Equals(updated, original, StringComparison.Ordinal))
        {
            untouched.Add("AGENTS.md");
            return;
        }

        fileSystem.WriteAllText(path, updated);
        written.Add("AGENTS.md");
    }

    private void MergeGitIgnore(string rootDirectory, List<string> written, List<string> untouched)
    {
        var path = Path.Combine(rootDirectory, ".gitignore");
        if (!fileSystem.FileExists(path))
        {
            return;
        }

        var original = fileSystem.ReadAllText(path);
        var rules = original.Split(["\r\n", "\n"], StringSplitOptions.None)
            .Select(line => line.Trim().TrimStart('/').TrimEnd('/'))
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToArray();
        if (rules.Any(rule => rule is "artifacts" or "artifacts/pointframe-verify"))
        {
            untouched.Add(".gitignore");
            return;
        }

        fileSystem.WriteAllText(path, original.TrimEnd() + Environment.NewLine + "artifacts/pointframe-verify/" + Environment.NewLine);
        written.Add(".gitignore");
    }

    private static void AddPackages(string xml, HashSet<string> names)
    {
        try
        {
            foreach (var item in XDocument.Parse(xml).Descendants().Where(item => item.Name.LocalName is "PackageReference" or "PackageVersion"))
            {
                var name = item.Attribute("Include")?.Value ?? item.Attribute("Update")?.Value;
                if (name is not null)
                {
                    names.Add(name);
                }
            }
        }
        catch (System.Xml.XmlException)
        {
        }
    }

    private static VerificationGate Gate(string root, string id, string run) => new(id, run, root, 30);

    private static VerificationInitGate DescribeGate(VerificationGate gate) => new(gate.Id, gate.Run);

    private static string AppIdFromPath(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var id = new string(name.Select(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' ? character : '-').ToArray()).Trim('-');
        return id.Length == 0 ? "app" : id;
    }

    private static string Quote(string value) => value.Contains(' ') ? $"\"{value}\"" : value;

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

    private async Task WriteAsync(VerificationInitResponse response)
    {
        await standardOutput.WriteLineAsync(JsonSerializer.Serialize(response, JsonOptions));
        if (response.Error is not null)
        {
            await standardError.WriteLineAsync(response.Error);
        }
    }
}
