using System.IO;
using System.Text.Json;
using Moq;
using Pointframe.Cli;
using Xunit;

namespace Pointframe.Tests.Cli;

public sealed class VerificationInitTests
{
    [Fact]
    public void Parser_InitAcceptsEveryFlag()
    {
        var parsed = CliCommandParser.TryParse(
            ["verify", "init", "--app", "bin/App.exe", "--mcp", "mcp.exe", "--hooks", "codex", "--agents-md", "--explore", "--force"],
            out var command,
            out var error);

        Assert.True(parsed);
        Assert.Null(error);
        Assert.Equal("init", command.VerifyAction);
        Assert.Equal("bin/App.exe", command.AppPath);
        Assert.Equal("mcp.exe", command.McpExecutablePath);
        Assert.Equal("codex", command.Hooks);
        Assert.True(command.AgentsMd);
        Assert.True(command.Explore);
        Assert.True(command.Force);
    }

    [Fact]
    public void Parser_HooksDefaultOnlyForInit()
    {
        Assert.True(CliCommandParser.TryParse(["verify", "init"], out var init, out _));
        Assert.Equal("both", init.Hooks);

        Assert.True(CliCommandParser.TryParse(["verify", "run"], out var run, out _));
        Assert.Null(run.Hooks);
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    [InlineData("both")]
    [InlineData("none")]
    public void Parser_InitAcceptsHookChoices(string hooks)
    {
        var parsed = CliCommandParser.TryParse(["verify", "init", "--hooks", hooks], out var command, out _);

        Assert.True(parsed);
        Assert.Equal(hooks, command.Hooks);
    }

    [Theory]
    [InlineData("bad")]
    [InlineData("--unknown")]
    public void Parser_InitRejectsBadFlagsAndValues(string argument)
    {
        var args = argument == "bad" ? new[] { "verify", "init", "--hooks", argument } : ["verify", "init", argument];

        Assert.False(CliCommandParser.TryParse(args, out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void DetectGates_PrefersSlnxAndDetectsTestPackages()
    {
        using var fixture = new InitFixture();
        fixture.Write("App.sln", "Project(\"{GUID}\") = \"A\", \"a.csproj\", \"{GUID}\"\nEndProject");
        fixture.Write("z.slnx", "<Solution><Project Path=\"src/App.csproj\" /></Solution>");
        fixture.Write("src/App.csproj", "<Project><ItemGroup><PackageReference Include=\"Microsoft.NET.Test.Sdk\" /></ItemGroup></Project>");

        var gates = fixture.Init.DetectGates(fixture.Root);

        Assert.Equal(["build", "tests"], gates.Select(item => item.Id));
        Assert.Contains("z.slnx", gates[0].Run);
        Assert.Contains("--no-build", gates[1].Run);
        Assert.All(gates, gate => Assert.True(IsStandardGate(fixture.Root, gate)));
    }

    [Fact]
    public void DetectGates_UsesCentralTestPackageAndSupportsSolutionWithoutTests()
    {
        using var fixture = new InitFixture();
        fixture.Write("Pointframe.sln", "Project(\"{GUID}\") = \"A\", \"a.csproj\", \"{GUID}\"\nEndProject");
        fixture.Write("a.csproj", "<Project />");
        fixture.Write("Directory.Packages.props", "<Project><ItemGroup><PackageVersion Include=\"xunit\" Version=\"2.0\" /></ItemGroup></Project>");

        var gates = fixture.Init.DetectGates(fixture.Root);

        Assert.Equal(["build", "tests"], gates.Select(item => item.Id));
    }

    [Fact]
    public void DetectGates_SolutionWithoutTestFrameworkGetsOnlyBuildGate()
    {
        using var fixture = new InitFixture();
        fixture.Write("z.sln", "Project(\"{GUID}\") = \"A\", \"a.csproj\", \"{GUID}\"\nEndProject");
        fixture.Write("a.csproj", "<Project><ItemGroup><PackageReference Include=\"Newtonsoft.Json\" Version=\"13.0.0\" /></ItemGroup></Project>");

        var gates = fixture.Init.DetectGates(fixture.Root);

        Assert.Equal(["build"], gates.Select(item => item.Id));
        Assert.Contains("z.sln", gates[0].Run);
    }

    [Fact]
    public void DetectGates_UsesNodeScriptsAndDoesNothingWithoutProject()
    {
        using var fixture = new InitFixture();
        Assert.Empty(fixture.Init.DetectGates(fixture.Root));
        fixture.Write("package.json", "{ \"scripts\": { \"build\": \"vite build\", \"test\": \"vitest\", \"other\": \"true\" } }");

        var gates = fixture.Init.DetectGates(fixture.Root);

        Assert.Equal(["build", "test"], gates.Select(item => item.Id));
        Assert.Equal("npm test", gates[1].Run);
        Assert.All(gates, gate => Assert.True(IsStandardGate(fixture.Root, gate)));
    }

    [Theory]
    [InlineData("pnpm", "pnpm-lock.yaml")]
    [InlineData("yarn", "yarn.lock")]
    public async Task Init_UsesNodeLockfilePackageManagerAndWritesStandardCommands(string manager, string lockfile)
    {
        using var fixture = new InitFixture();
        fixture.Write("package.json", "{ \"scripts\": { \"build\": \"build-tool\", \"test\": \"test-tool\" } }");
        fixture.Write(lockfile, "");

        Assert.Equal(0, await fixture.RunAsync(Command()));

        using var output = JsonDocument.Parse(fixture.Output.ToString());
        var gates = output.RootElement.GetProperty("gates").EnumerateArray()
            .Select(gate => new VerificationGate(gate.GetProperty("id").GetString()!, gate.GetProperty("run").GetString()!, fixture.Root, 30))
            .ToArray();
        Assert.Equal($"{manager} run build", gates[0].Run);
        Assert.Equal($"{manager} test", gates[1].Run);
        Assert.All(gates, gate => Assert.True(IsStandardGate(fixture.Root, gate)));
    }

    [Fact]
    public async Task Init_PackageManagerFieldResolvesMultipleLockfiles()
    {
        using var fixture = new InitFixture();
        fixture.Write("package.json", "{ \"packageManager\": \"yarn@4.1.0\", \"scripts\": { \"test\": \"test-tool\" } }");
        fixture.Write("pnpm-lock.yaml", "");
        fixture.Write("yarn.lock", "");
        fixture.Write("package-lock.json", "{}");

        Assert.Equal(0, await fixture.RunAsync(Command()));

        using var output = JsonDocument.Parse(fixture.Output.ToString());
        Assert.Equal("yarn test", output.RootElement.GetProperty("gates")[0].GetProperty("run").GetString());
        Assert.Empty(output.RootElement.GetProperty("warnings").EnumerateArray());
    }

    [Fact]
    public async Task Init_WarnsAndUsesPriorityWhenMultipleLockfilesLackRecognizedPackageManager()
    {
        using var fixture = new InitFixture();
        fixture.Write("package.json", "{ \"packageManager\": \"bun@1.0.0\", \"scripts\": { \"test\": \"test-tool\" } }");
        fixture.Write("pnpm-lock.yaml", "");
        fixture.Write("yarn.lock", "");

        Assert.Equal(0, await fixture.RunAsync(Command()));

        using var output = JsonDocument.Parse(fixture.Output.ToString());
        Assert.Equal("pnpm test", output.RootElement.GetProperty("gates")[0].GetProperty("run").GetString());
        Assert.Contains("pnpm-lock.yaml", output.RootElement.GetProperty("warnings")[0].GetString(), StringComparison.Ordinal);
        Assert.Contains("yarn.lock", output.RootElement.GetProperty("warnings")[0].GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void DetectApp_UsesWinExeOutputAndExplicitOverrideWins()
    {
        using var fixture = new InitFixture();
        fixture.Write("src/App.csproj", "<Project><PropertyGroup><OutputType>WinExe</OutputType><TargetFramework>net10.0-windows</TargetFramework><AssemblyName>Custom</AssemblyName></PropertyGroup></Project>");
        fixture.Write("src/Other.csproj", "<Project><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");

        var warnings = new List<string>();
        var detected = fixture.Init.DetectApp(fixture.Root, warnings);

        Assert.Equal(Path.Combine(fixture.Root, "src", "bin", "Release", "net10.0-windows", "Custom.exe"), detected);
        Assert.Empty(warnings);
    }

    [Fact]
    public void DetectApp_ReportsNoAmbiguousAndMultiTargetCandidates()
    {
        using var fixture = new InitFixture();
        fixture.Write("Fixture.slnx", "<Solution />");
        var warnings = new List<string>();
        Assert.Null(fixture.Init.DetectApp(fixture.Root, warnings));
        Assert.Contains(warnings, warning => warning.Contains("No .NET WinExe", StringComparison.Ordinal));

        fixture.Write("one/App.csproj", "<Project><PropertyGroup><OutputType>WinExe</OutputType><TargetFrameworks>net9.0-windows;net10.0-windows</TargetFrameworks></PropertyGroup></Project>");
        fixture.Write("two/Other.csproj", "<Project><PropertyGroup><OutputType>WinExe</OutputType><TargetFramework>net10.0-windows</TargetFramework></PropertyGroup></Project>");
        warnings.Clear();

        Assert.Null(fixture.Init.DetectApp(fixture.Root, warnings));
        Assert.Contains(warnings, warning => warning.Contains("one/App.csproj (multiple target frameworks)", StringComparison.Ordinal));
        Assert.Contains(warnings, warning => warning.Contains("two/bin/Release", StringComparison.Ordinal));
        Assert.Contains(warnings, warning => warning.Contains("Pass --app", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_WritesValidatedSpecHooksAgentsAndIgnoreOnce()
    {
        using var fixture = new InitFixture();
        fixture.Write("Pointframe.sln", "Project(\"{GUID}\") = \"A\", \"a.csproj\", \"{GUID}\"\nEndProject");
        fixture.Write(".gitignore", "bin/\n");
        fixture.Write(".claude/settings.json", "{\n  \"permissions\": { \"allow\": [\"dotnet build\"] },\n  \"hooks\": { \"PostToolUse\": [{ \"matcher\": \"Read\", \"hooks\": [{\"type\":\"command\",\"command\":\"keep\"}] }] }\n}");
        fixture.Write(".codex/hooks.json", "{ \"hooks\": { \"PostToolUse\": [{ \"hooks\": [{ \"type\": \"command\", \"command\": \"keep-codex\" }] }] } }");
        var command = Command(hooks: "both", agentsMd: true);

        Assert.Equal(0, await fixture.RunAsync(command));
        Assert.Equal(0, await fixture.RunAsync(Command(hooks: "both", agentsMd: true, force: true)));

        _ = VerificationSpecLoader.Load(Path.Combine(fixture.Root, ".pointframe", "verify.json"));
        var hook = JsonDocument.Parse(fixture.Read(".claude/settings.json")).RootElement;
        Assert.True(hook.GetProperty("permissions").GetProperty("allow").GetArrayLength() == 1);
        Assert.Contains("keep", hook.GetRawText(), StringComparison.Ordinal);
        Assert.Contains("verify hook stop", hook.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(1, Count(fixture.Read(".claude/settings.json"), "verify hook stop"));
        Assert.Contains("PostToolUse", hook.GetRawText(), StringComparison.Ordinal);
        var codexHook = JsonDocument.Parse(fixture.Read(".codex/hooks.json")).RootElement;
        Assert.Contains("keep-codex", codexHook.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(1, Count(codexHook.GetRawText(), "verify hook stop"));
        Assert.Equal(1, Count(fixture.Read("AGENTS.md"), "pointframe-verify:start"));
        Assert.Equal(1, Count(fixture.Read(".gitignore"), "artifacts/pointframe-verify/"));
    }

    [Fact]
    public async Task RunAsync_ExistingSpecIsUntouchedUnlessForced()
    {
        using var fixture = new InitFixture();
        fixture.Write("Pointframe.sln", "Project(\"{GUID}\") = \"A\", \"a.csproj\", \"{GUID}\"\nEndProject");
        fixture.Write(".pointframe/verify.json", "original");
        fixture.Write(".gitignore", "bin/" + Environment.NewLine);

        Assert.Equal(0, await fixture.RunAsync(Command(hooks: "both", agentsMd: true)));
        var specBytesAfterFirstRun = File.ReadAllBytes(Path.Combine(fixture.Root, ".pointframe", "verify.json"));
        Assert.Equal("original", System.Text.Encoding.UTF8.GetString(specBytesAfterFirstRun));
        Assert.Contains("verify hook stop", fixture.Read(".claude/settings.json"), StringComparison.Ordinal);
        Assert.Contains("verify hook stop", fixture.Read(".codex/hooks.json"), StringComparison.Ordinal);
        Assert.Contains("pointframe-verify:start", fixture.Read("AGENTS.md"), StringComparison.Ordinal);
        Assert.Contains("artifacts/pointframe-verify/", fixture.Read(".gitignore"), StringComparison.Ordinal);
        Assert.Contains("\"status\": \"updated\"", fixture.Output.ToString(), StringComparison.Ordinal);

        Assert.Equal(0, await fixture.RunAsync(Command(hooks: "both", agentsMd: true)));
        Assert.Equal(specBytesAfterFirstRun, File.ReadAllBytes(Path.Combine(fixture.Root, ".pointframe", "verify.json")));
        Assert.Contains("\"status\": \"unchanged\"", fixture.Output.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, await fixture.RunAsync(Command(force: true)));
        _ = VerificationSpecLoader.Load(Path.Combine(fixture.Root, ".pointframe", "verify.json"));
    }

    [Fact]
    public async Task RunAsync_ExistingVerifyHookCommandIsPreservedWithoutDuplication()
    {
        using var fixture = new InitFixture();
        fixture.Write("package.json", "{ \"scripts\": { \"test\": \"vitest\" } }");
        const string existingHook = "pointframe verify hook stop --spec .pointframe/custom.json";
        var settings = $$"""{ "hooks": { "Stop": [{ "hooks": [{ "type": "command", "command": "{{existingHook}}" }] }] } }""";
        fixture.Write(".claude/settings.json", settings);

        Assert.Equal(0, await fixture.RunAsync(Command(hooks: "claude")));

        Assert.Equal(settings, fixture.Read(".claude/settings.json"));
        Assert.Equal(1, Count(fixture.Read(".claude/settings.json"), "verify hook stop"));
    }

    [Fact]
    public async Task RunAsync_ExploreWithMissingExplicitMcpWritesSpecAndWarns()
    {
        using var fixture = new InitFixture();
        fixture.Write("Pointframe.sln", "Project(\"{GUID}\") = \"A\", \"a.csproj\", \"{GUID}\"\nEndProject");
        fixture.Write("bin/App.exe", "exe");

        var result = await fixture.RunAsync(Command(appPath: "bin/App.exe", explore: true, mcpPath: "missing-mcp.exe"));

        Assert.Equal(0, result);
        Assert.True(File.Exists(Path.Combine(fixture.Root, ".pointframe", "verify.json")));
        Assert.Contains("no Pointframe MCP server was found", fixture.Output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_ExploreWithoutTrustDoesNotLaunchTheAppAndWarns()
    {
        using var fixture = new InitFixture();
        fixture.Write("Pointframe.sln", "Project(\"{GUID}\") = \"A\", \"a.csproj\", \"{GUID}\"\nEndProject");
        fixture.Write("bin/App.exe", "exe");
        var mcp = new FakeMcp(Path.Combine(fixture.Root, "proof"));
        var services = new VerificationFixture.Services(mcp);
        var checkedApps = new List<string?>();

        var result = await fixture.RunAsync(
            Command(appPath: "bin/App.exe", explore: true, hooks: "none"),
            services,
            fixture.McpPath,
            (spec, _) =>
            {
                checkedApps.Add(spec.App?.Id);
                return Task.FromResult<string?>("spec_untrusted");
            });

        Assert.Equal(0, result);
        Assert.Equal(["App"], checkedApps);
        services.Factory.Verify(
            item => item.LaunchAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()),
            Times.Never);
        Assert.Contains("spec_untrusted", fixture.Output.ToString(), StringComparison.Ordinal);
        Assert.Contains("pointframe verify trust", fixture.Output.ToString(), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(fixture.Root, ".pointframe", "verify.json")));
    }

    [Fact]
    public async Task RunAsync_NoGatesOrScenarioReturnsOneWithoutWriting()
    {
        using var fixture = new InitFixture();

        var result = await fixture.RunAsync(Command(hooks: "none"));

        Assert.Equal(1, result);
        Assert.False(File.Exists(Path.Combine(fixture.Root, ".pointframe", "verify.json")));
    }

    [Fact]
    public async Task RunAsync_CreatesHooksAndSkipsAlreadyIgnoredArtifacts()
    {
        using var fixture = new InitFixture();
        fixture.Write("package.json", "{ \"scripts\": { \"lint\": \"eslint .\" } }");
        fixture.Write(".gitignore", "artifacts/\n");

        Assert.Equal(0, await fixture.RunAsync(Command(hooks: "both")));

        Assert.Contains("verify hook stop", fixture.Read(".claude/settings.json"), StringComparison.Ordinal);
        Assert.Contains("verify hook stop", fixture.Read(".codex/hooks.json"), StringComparison.Ordinal);
        Assert.Equal("artifacts/\n", fixture.Read(".gitignore"));
    }

    [Fact]
    public async Task RunAsync_NoneHooksDoesNotCreateHookFiles()
    {
        using var fixture = new InitFixture();
        fixture.Write("package.json", "{ \"scripts\": { \"build\": \"vite build\" } }");

        Assert.Equal(0, await fixture.RunAsync(Command(hooks: "none")));

        Assert.False(File.Exists(Path.Combine(fixture.Root, ".claude", "settings.json")));
        Assert.False(File.Exists(Path.Combine(fixture.Root, ".codex", "hooks.json")));
    }

    [Fact]
    public async Task RunAsync_HooksWarnWhenPointframeCommandIsMissing()
    {
        using var fixture = new InitFixture();
        fixture.Write("package.json", "{ \"scripts\": { \"build\": \"dotnet build\" } }");
        var resolver = new Mock<IPointframeCommandResolver>();
        resolver.Setup(item => item.Resolve(null)).Returns(new PointframeCommandInfo(null, null, false));

        Assert.Equal(0, await fixture.RunAsync(Command(hooks: "both"), resolver.Object));

        using var output = JsonDocument.Parse(fixture.Output.ToString());
        Assert.Contains(output.RootElement.GetProperty("warnings").EnumerateArray(), warning => warning.GetString()!.Contains("Stop hooks will not run", StringComparison.Ordinal));
        Assert.Contains(output.RootElement.GetProperty("nextSteps").EnumerateArray(), step => step.GetString()!.Contains("restart the agent", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RunAsync_HooksHaveNoWarningWhenTheInstalledCommandMatchesThisCli()
    {
        using var fixture = new InitFixture();
        fixture.Write("package.json", "{ \"scripts\": { \"build\": \"dotnet build\" } }");
        var resolver = new Mock<IPointframeCommandResolver>();
        resolver.Setup(item => item.Resolve(null)).Returns(new PointframeCommandInfo("C:\\Programs\\pointframe.exe", "Pointframe CLI 1.0", true));

        Assert.Equal(0, await fixture.RunAsync(Command(hooks: "both"), resolver.Object));

        using var output = JsonDocument.Parse(fixture.Output.ToString());
        Assert.Empty(output.RootElement.GetProperty("warnings").EnumerateArray());
    }

    [Fact]
    public async Task RunAsync_HooksReportVersionMismatch()
    {
        using var fixture = new InitFixture();
        fixture.Write("package.json", "{ \"scripts\": { \"build\": \"dotnet build\" } }");
        var resolver = new Mock<IPointframeCommandResolver>();
        resolver.Setup(item => item.Resolve(null)).Returns(new PointframeCommandInfo("C:\\Programs\\pointframe.exe", "Pointframe CLI 2.0", true));

        Assert.Equal(0, await fixture.RunAsync(Command(hooks: "both"), resolver.Object));

        using var output = JsonDocument.Parse(fixture.Output.ToString());
        var warning = Assert.Single(output.RootElement.GetProperty("warnings").EnumerateArray());
        Assert.Contains("Pointframe CLI 1.0", warning.GetString(), StringComparison.Ordinal);
        Assert.Contains("Pointframe CLI 2.0", warning.GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ReplacesAgentsSectionBetweenMarkers()
    {
        using var fixture = new InitFixture();
        fixture.Write("package.json", "{ \"scripts\": { \"build\": \"vite build\" } }");
        fixture.Write("AGENTS.md", $"Keep before.{Environment.NewLine}<!-- pointframe-verify:start -->{Environment.NewLine}old{Environment.NewLine}<!-- pointframe-verify:end -->{Environment.NewLine}Keep after.{Environment.NewLine}");

        Assert.Equal(0, await fixture.RunAsync(Command(hooks: "none", agentsMd: true)));

        var instructions = fixture.Read("AGENTS.md");
        Assert.Contains("Keep before.", instructions, StringComparison.Ordinal);
        Assert.Contains("Keep after.", instructions, StringComparison.Ordinal);
        Assert.DoesNotContain("old", instructions, StringComparison.Ordinal);
        Assert.Contains("Run `pointframe verify run` and check `pointframe verify status` shows pass and fresh before saying the task is done.", instructions, StringComparison.Ordinal);
        Assert.Contains("Read the failure details and fix the cause.", instructions, StringComparison.Ordinal);
        Assert.Contains("Never edit `.pointframe/verify.json` or a frozen task to make a check pass.", instructions, StringComparison.Ordinal);
        Assert.Contains("For desktop work with a task file, `pointframe verify task start <task-file>` freezes its criteria first.", instructions, StringComparison.Ordinal);
        Assert.DoesNotContain("Run `pointframe verify task start <task-file>` to freeze criteria for a new task.", instructions, StringComparison.Ordinal);
        Assert.Equal(1, Count(instructions, "pointframe-verify:start"));
        Assert.Equal(1, Count(instructions, "pointframe-verify:end"));
    }

    [Fact]
    public async Task RunAsync_AppOverrideBecomesTheSpecApp()
    {
        using var fixture = new InitFixture();
        fixture.Write("package.json", "{ \"scripts\": { \"build\": \"vite build\" } }");

        Assert.Equal(0, await fixture.RunAsync(Command(appPath: "output/My App.exe", hooks: "none")));

        var spec = JsonDocument.Parse(fixture.Read(".pointframe/verify.json")).RootElement;
        Assert.Equal("output/My App.exe", spec.GetProperty("app").GetProperty("executable").GetString());
    }

    [Fact]
    public async Task RunAsync_ExploreUsesFirstObservedIdAndWarnsOnFewIds()
    {
        using var fixture = new InitFixture();
        fixture.Write("Pointframe.sln", "Project(\"{GUID}\") = \"A\", \"a.csproj\", \"{GUID}\"\nEndProject");
        fixture.Write("bin/App.exe", "exe");
        var mcp = new FakeMcp(Path.Combine(fixture.Root, "proof")) { ElementIds = ["first", "second"] };
        var services = new VerificationFixture.Services(mcp);
        var command = Command(appPath: "bin/App.exe", explore: true, hooks: "none");

        Assert.Equal(0, await fixture.RunAsync(command, services, fixture.McpPath));

        var spec = JsonDocument.Parse(fixture.Read(".pointframe/verify.json")).RootElement;
        Assert.Equal("first", spec.GetProperty("scenarios")[0].GetProperty("steps")[0].GetProperty("check").GetProperty("automationId").GetString());
        Assert.Contains("fewer than 3", fixture.Output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, mcp.Count("desktop_observe_app"));
        Assert.Equal(1, mcp.Count("desktop_end_test_session"));
    }

    private static CliCommand Command(string? appPath = null, string hooks = "none", bool agentsMd = false, bool explore = false, bool force = false, string? mcpPath = null) =>
        new("verify", VerifyAction: "init", AppPath: appPath, McpExecutablePath: mcpPath, Hooks: hooks, AgentsMd: agentsMd, Explore: explore, Force: force);

    private static int Count(string value, string pattern) => value.Split(pattern).Length - 1;

    private static bool IsStandardGate(string root, VerificationGate gate) => CommandPolicy.IsStandard(
        new VerificationSpec(1, Path.Combine(root, ".pointframe", "verify.json"), root, null, [gate], []));

    private sealed class InitFixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "Pointframe.VerifyInitTests", Guid.NewGuid().ToString("N"));

        internal string McpPath { get; }

        internal StringWriter Output { get; } = new();

        internal VerificationInit Init { get; }

        internal InitFixture()
        {
            McpPath = Path.Combine(Root, "mcp.exe");
            Directory.CreateDirectory(Root);
            File.WriteAllText(McpPath, "mcp");
            Init = new VerificationInit(new PhysicalVerificationInitFileSystem(), Output, TextWriter.Null);
        }

        internal void Write(string relativePath, string value)
        {
            var path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, value);
        }

        internal string Read(string relativePath) => File.ReadAllText(Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar)));

        internal Task<int> RunAsync(CliCommand command) => RunAsync(command, new VerificationFixture.Services(), null);

        internal Task<int> RunAsync(CliCommand command, IPointframeCommandResolver resolver) => new VerificationInit(
            new PhysicalVerificationInitFileSystem(), Output, TextWriter.Null, resolver, "Pointframe CLI 1.0").RunAsync(
                command, Root, path => path is null ? null : File.Exists(path) ? path : null,
                new VerificationFixture.Services().Factory.Object, "test-init-lock", Trusted, CancellationToken.None);

        internal Task<int> RunAsync(CliCommand command, VerificationFixture.Services services, string? mcpPath) =>
            RunAsync(command, services, mcpPath, Trusted);

        internal Task<int> RunAsync(
            CliCommand command,
            VerificationFixture.Services services,
            string? mcpPath,
            Func<VerificationSpec, CancellationToken, Task<string?>> trustFailureCode) => Init.RunAsync(
            command,
            Root,
            path => path is null ? mcpPath : File.Exists(path) ? path : null,
            services.Factory.Object,
            services.LockName,
            trustFailureCode,
            CancellationToken.None);

        private static Task<string?> Trusted(VerificationSpec spec, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, true);
            }

            Output.Dispose();
        }
    }
}
