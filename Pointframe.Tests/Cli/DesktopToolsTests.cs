using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using Moq;
using Pointframe.Cli;
using Xunit;

namespace Pointframe.Tests.Cli;

public sealed class DesktopToolsTests : IDisposable
{
    private const string NonStandardGate = """[ { "id": "extra", "run": "cargo test --workspace" } ]""";

    private readonly VerificationFixture _fixture = new();
    private readonly string _outside = Path.Combine(Path.GetTempPath(), "Pointframe.DesktopToolsTests", Guid.NewGuid().ToString("N"));
    private readonly VerificationStore _store;
    private readonly VerificationFixture.Services _services = new();
    private readonly FakeServerHost _host = new();
    private readonly StringWriter _output = new();
    private readonly StringWriter _error = new();

    public DesktopToolsTests()
    {
        Directory.CreateDirectory(_outside);
        _store = new VerificationStore(Path.Combine(_outside, "store"));
    }

    public void Dispose()
    {
        _fixture.Dispose();
        if (Directory.Exists(_outside))
        {
            Directory.Delete(_outside, recursive: true);
        }
    }

    [Fact]
    public void Parser_ServeAcceptsProjectAndMcp()
    {
        Assert.True(CliCommandParser.TryParse(["mcp", "serve", "--project", "C:\\work\\app", "--mcp", "mcp.exe"], out var command, out var error));

        Assert.Null(error);
        Assert.Equal("mcp", command.Name);
        Assert.Equal("serve", command.McpAction);
        Assert.Equal("C:\\work\\app", command.ProjectPath);
        Assert.Equal("mcp.exe", command.McpExecutablePath);
    }

    [Theory]
    [InlineData("--client")]
    [InlineData("--dry-run")]
    [InlineData("--bogus")]
    public void Parser_ServeRejectsOtherOptions(string option)
    {
        Assert.False(CliCommandParser.TryParse(["mcp", "serve", option, "vscode"], out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Parser_SetupDefaultsToClaudeCodeAndAcceptsEveryClient()
    {
        Assert.True(CliCommandParser.TryParse(["verify", "setup"], out var defaults, out _));
        Assert.Equal("setup", defaults.VerifyAction);
        Assert.Equal("claude-code", defaults.McpClient);

        Assert.True(CliCommandParser.TryParse(["verify", "setup", "--client=all", "--spec", "s.json", "--mcp", "m.exe"], out var all, out _));
        Assert.Equal("all", all.McpClient);
        Assert.Equal("s.json", all.SpecPath);
        Assert.Equal("m.exe", all.McpExecutablePath);

        Assert.False(CliCommandParser.TryParse(["verify", "setup", "--client", "cursor"], out _, out var error));
        Assert.Contains("claude-code", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Serve_UntrustedSpec_RefusesOnStderrWithNothingOnStdoutAndStartsNothing()
    {
        _fixture.WriteUntrustedSpec("""{ "id": "fixture", "executable": "C:/Windows/System32/notepad.exe" }""", null, VerificationFixture.ValidScenario);
        _fixture.CreateAppAndMcp();

        var exit = await ServeAsync();

        Assert.Equal(1, exit);
        Assert.Contains("[spec_untrusted]", _error.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, _output.ToString());
        Assert.Empty(_host.Launches);
    }

    [Fact]
    public async Task Serve_AppChangedAfterTrust_Refuses()
    {
        _fixture.WriteSpecWith("""{ "id": "fixture", "executable": "bin/App.exe" }""", null, VerificationFixture.ValidScenario);
        _fixture.CreateAppAndMcp();
        _fixture.WriteUntrustedSpec("""{ "id": "fixture", "executable": "C:/Windows/System32/notepad.exe" }""", null, VerificationFixture.ValidScenario);

        var exit = await ServeAsync();

        Assert.Equal(1, exit);
        Assert.Contains("[spec_untrusted]", _error.ToString(), StringComparison.Ordinal);
        Assert.Empty(_host.Launches);
    }

    [Fact]
    public async Task Serve_RevokedTrust_Refuses()
    {
        _fixture.WriteUntrustedSpec(VerificationFixture.DefaultApp, NonStandardGate);
        _fixture.CreateAppAndMcp();
        TrustByPerson();
        Assert.Equal(0, await ServeAsync());
        _host.Launches.Clear();
        Assert.True(_store.RevokeTrust(_fixture.Root));

        var exit = await ServeAsync();

        Assert.Equal(1, exit);
        Assert.Contains("[approver_unavailable]", _error.ToString(), StringComparison.Ordinal);
        Assert.Empty(_host.Launches);
    }

    [Fact]
    public async Task Serve_SpecWithoutApp_RefusesWithNoApp()
    {
        _fixture.WriteSpecWith(null, """[ { "id": "build", "run": "dotnet build" } ]""");
        _fixture.CreateAppAndMcp();

        var exit = await ServeAsync();

        Assert.Equal(1, exit);
        Assert.Contains("[no_app]", _error.ToString(), StringComparison.Ordinal);
        Assert.Empty(_host.Launches);
    }

    [Fact]
    public async Task Serve_NoSpecAnywhere_RefusesWithSpecInvalid()
    {
        var exit = await ServeAsync(projectPath: Path.Combine(_outside, "empty"));

        Assert.Equal(1, exit);
        Assert.Contains("[spec_invalid]", _error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Serve_NoMcpServer_RefusesWithMcpNotFound()
    {
        _fixture.WriteSpec(VerificationFixture.ValidScenario);
        _fixture.CreateAppAndMcp();

        var exit = await ServeAsync(mcpPath: Path.Combine(_fixture.Root, "missing", "Pointframe.Mcp.exe"));

        Assert.Equal(1, exit);
        Assert.Contains("[mcp_not_found]", _error.ToString(), StringComparison.Ordinal);
        Assert.Empty(_host.Launches);
    }

    [Fact]
    public async Task Serve_AppReachedThroughJunctionOutsideTheProject_IsRefused()
    {
        var target = Path.Combine(_outside, "elsewhere");
        Directory.CreateDirectory(target);
        File.WriteAllBytes(Path.Combine(target, "App.exe"), [0]);
        Directory.CreateDirectory(_fixture.Root);
        CreateJunction(Path.Combine(_fixture.Root, "link"), target);
        _fixture.WriteSpecWith("""{ "id": "fixture", "executable": "link/App.exe" }""", null, VerificationFixture.ValidScenario);
        _fixture.CreateAppAndMcp();
        TrustByPerson();

        var exit = await ServeAsync();

        Assert.Equal(1, exit);
        Assert.Contains("[app_outside_project]", _error.ToString(), StringComparison.Ordinal);
        Assert.Empty(_host.Launches);
        Directory.Delete(Path.Combine(_fixture.Root, "link"));
    }

    [Fact]
    public void PathContainment_FollowsLinksInTheMiddleOfAPathAndHandlesMissingFiles()
    {
        var target = Path.Combine(_outside, "real");
        Directory.CreateDirectory(target);
        var link = Path.Combine(_fixture.Root, "link");
        Directory.CreateDirectory(_fixture.Root);
        CreateJunction(link, target);

        Assert.False(PathContainment.IsInside(Path.Combine(link, "sub", "missing.exe"), _fixture.Root));
        Assert.True(PathContainment.IsInside(Path.Combine(_fixture.Root, "bin", "missing.exe"), _fixture.Root));
        Assert.True(PathContainment.IsInside(_fixture.Root, _fixture.Root));
        Directory.Delete(link);
    }

    [Fact]
    public async Task Serve_TrustedSpec_StartsTheResolvedServerWithAPolicyOutsideTheRepository()
    {
        _fixture.WriteSpec(VerificationFixture.ValidScenario);
        _fixture.CreateAppAndMcp();

        var exit = await ServeAsync();

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, _output.ToString());
        var launch = Assert.Single(_host.Launches);
        Assert.Equal(_fixture.McpPath, launch.Executable);
        Assert.Equal(3, launch.Arguments.Count);
        Assert.Equal("--desktop-testing", launch.Arguments[0]);
        Assert.Equal("--desktop-policy", launch.Arguments[1]);
        var policyPath = launch.Arguments[2];
        var expectedDirectory = Path.Combine(_store.BaseDirectory, "projects", VerificationStore.ProjectKey(_fixture.Root));
        Assert.Equal(Path.Combine(expectedDirectory, "desktop-policy-serve.json"), policyPath);
        Assert.False(policyPath.StartsWith(_fixture.Root, StringComparison.OrdinalIgnoreCase));
        var spec = VerificationSpecLoader.Load(_fixture.SpecPath);
        var reference = VerificationApplication.WritePolicy(spec.App!, expectedDirectory, "reference");
        Assert.Equal(File.ReadAllText(reference), launch.PolicyJson);
    }

    [Fact]
    public async Task Serve_ReturnsTheServersExitCode()
    {
        _fixture.WriteSpec(VerificationFixture.ValidScenario);
        _fixture.CreateAppAndMcp();
        _host.ExitCode = 7;

        Assert.Equal(7, await ServeAsync());
    }

    [Fact]
    public async Task Serve_FindsTheProjectFromASubfolder()
    {
        _fixture.WriteSpec(VerificationFixture.ValidScenario);
        _fixture.CreateAppAndMcp();
        var subfolder = Path.Combine(_fixture.Root, "src", "deep");
        Directory.CreateDirectory(subfolder);

        Assert.Equal(_fixture.Root, DesktopToolsApplication.FindProjectRoot(subfolder));
        Assert.Null(DesktopToolsApplication.FindProjectRoot(_outside));
        Assert.Equal(0, await ServeAsync(projectPath: null, currentDirectory: subfolder));
        Assert.Single(_host.Launches);
    }

    [Fact]
    public async Task Serve_HonoursEnvironmentIsolationWithAFolderDeletedOnExit()
    {
        _fixture.WriteSpecWith("""{ "id": "fixture", "executable": "bin/App.exe", "isolation": { "environmentVariable": "APP_DATA_DIR" } }""", null, VerificationFixture.ValidScenario);
        _fixture.CreateAppAndMcp();

        Assert.Equal(0, await ServeAsync());

        var launch = Assert.Single(_host.Launches);
        var dataDirectory = launch.Environment["APP_DATA_DIR"];
        Assert.True(launch.DataDirectoryExisted);
        Assert.StartsWith(Path.Combine(_store.BaseDirectory, "projects"), dataDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(dataDirectory));
    }

    [Fact]
    public async Task Serve_HonoursArgumentIsolationInThePolicyAndDeletesItsPerSessionFile()
    {
        _fixture.WriteSpecWith("""{ "id": "fixture", "executable": "bin/App.exe", "arguments": ["--quiet"], "isolation": { "argument": "--data-dir" } }""", null, VerificationFixture.ValidScenario);
        _fixture.CreateAppAndMcp();

        Assert.Equal(0, await ServeAsync());

        var launch = Assert.Single(_host.Launches);
        using var policy = JsonDocument.Parse(launch.PolicyJson);
        var arguments = policy.RootElement.GetProperty("profiles")[0].GetProperty("arguments").EnumerateArray().Select(item => item.GetString()).ToArray();
        Assert.Equal(["--quiet", "--data-dir"], arguments.Take(2));
        Assert.False(Directory.Exists(arguments[2]));
        Assert.False(File.Exists(launch.Arguments[2]));
    }

    [Fact]
    public async Task Setup_DefaultWritesClaudeCodeConfigRunsTheSmokeCheckAndReportsNextStep()
    {
        SetupProject();
        var smoke = ArrangeSmoke();

        var exit = await SetupAsync();

        Assert.Equal(0, exit);
        using var json = JsonDocument.Parse(_output.ToString());
        var response = json.RootElement;
        Assert.True(response.GetProperty("success").GetBoolean());
        Assert.Equal("configured", response.GetProperty("status").GetString());
        Assert.Equal(".mcp.json", Assert.Single(response.GetProperty("filesWritten").EnumerateArray()).GetString());
        Assert.Equal("claude-code", Assert.Single(response.GetProperty("clients").EnumerateArray()).GetString());
        Assert.Equal("fixture", response.GetProperty("profileId").GetString());
        Assert.Equal(_fixture.McpPath, response.GetProperty("serverPath").GetString());
        Assert.Contains("Restart your agent session", response.GetProperty("nextStep").GetString(), StringComparison.Ordinal);
        Assert.Contains("desktop_start_test_session", response.GetProperty("tools").EnumerateArray().Select(item => item.GetString()));
        Assert.False(File.Exists(Path.Combine(_fixture.Root, ".codex", "config.toml")));
        using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(_fixture.Root, ".mcp.json")));
        var server = config.RootElement.GetProperty("mcpServers").GetProperty("pointframe");
        Assert.Equal("pointframe", server.GetProperty("command").GetString());
        Assert.Equal(["mcp", "serve"], server.GetProperty("args").EnumerateArray().Select(item => item.GetString()));
        var launch = Assert.Single(smoke);
        Assert.Equal(@"C:\tools\pointframe.exe", launch.Executable);
        Assert.Equal(["mcp", "serve", "--project", _fixture.Root, "--mcp", _fixture.McpPath], launch.Arguments);
    }

    [Fact]
    public async Task Setup_AllWritesEveryClientFile()
    {
        SetupProject();
        ArrangeSmoke();

        Assert.Equal(0, await SetupAsync(client: "all"));

        using var vscode = JsonDocument.Parse(File.ReadAllText(Path.Combine(_fixture.Root, ".vscode", "mcp.json")));
        var server = vscode.RootElement.GetProperty("servers").GetProperty("pointframe");
        Assert.Equal("stdio", server.GetProperty("type").GetString());
        Assert.Equal(["mcp", "serve", "--project", "${workspaceFolder}"], server.GetProperty("args").EnumerateArray().Select(item => item.GetString()));
        var toml = File.ReadAllText(Path.Combine(_fixture.Root, ".codex", "config.toml"));
        Assert.Contains("[mcp_servers.pointframe]", toml, StringComparison.Ordinal);
        Assert.Contains("command = \"pointframe\"", toml, StringComparison.Ordinal);
        Assert.Contains("args = [\"mcp\", \"serve\"]", toml, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(_fixture.Root, ".mcp.json")));
    }

    [Fact]
    public async Task Setup_PreservesUnrelatedServersSettingsAndAcceptsCommentsAndTrailingCommas()
    {
        SetupProject();
        ArrangeSmoke();
        var path = Path.Combine(_fixture.Root, ".mcp.json");
        File.WriteAllText(path, """
            {
              // keep the other server
              "mcpServers": { "other": { "command": "other.exe", "args": ["a"], }, },
              "extra": 5,
            }
            """);

        Assert.Equal(0, await SetupAsync());

        using var config = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal("other.exe", config.RootElement.GetProperty("mcpServers").GetProperty("other").GetProperty("command").GetString());
        Assert.Equal(5, config.RootElement.GetProperty("extra").GetInt32());
        Assert.Equal("pointframe", config.RootElement.GetProperty("mcpServers").GetProperty("pointframe").GetProperty("command").GetString());
        Assert.True(File.Exists($"{path}.pointframe.bak"));
    }

    [Fact]
    public async Task Setup_SecondRunReportsUnchangedAndLeavesTheFilesAlone()
    {
        SetupProject();
        ArrangeSmoke();
        Assert.Equal(0, await SetupAsync(client: "all"));
        var before = new[] { ".mcp.json", ".codex/config.toml", ".vscode/mcp.json" }
            .ToDictionary(file => file, file => File.ReadAllText(Path.Combine(_fixture.Root, file)));
        _output.GetStringBuilder().Clear();

        Assert.Equal(0, await SetupAsync(client: "all"));

        using var json = JsonDocument.Parse(_output.ToString());
        Assert.Equal("unchanged", json.RootElement.GetProperty("status").GetString());
        Assert.Empty(json.RootElement.GetProperty("filesWritten").EnumerateArray());
        Assert.Equal(3, json.RootElement.GetProperty("filesUnchanged").GetArrayLength());
        foreach (var (file, content) in before)
        {
            Assert.Equal(content, File.ReadAllText(Path.Combine(_fixture.Root, file)));
        }
    }

    [Fact]
    public async Task Setup_CodexTomlKeepsOtherTablesCommentsAndKeysAndReplacesOnlyItsOwnTable()
    {
        SetupProject();
        ArrangeSmoke();
        var path = Path.Combine(_fixture.Root, ".codex", "config.toml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var original = string.Join("\r\n",
            "# my settings",
            "model = \"x\"",
            "",
            "[mcp_servers.other]",
            "command = \"other\"",
            "",
            "[mcp_servers.pointframe]  # the old one",
            "command = \"old.exe\"",
            "args = [",
            "  \"stale\",",
            "]",
            "startup_timeout_sec = 30",
            "",
            "[mcp_servers.pointframe.env]",
            "KEY = \"v\"",
            "",
            "[profiles.fast]",
            "model = \"y\"",
            "");
        File.WriteAllText(path, original);

        Assert.Equal(0, await SetupAsync(client: "codex"));

        var expected = string.Join("\r\n",
            "# my settings",
            "model = \"x\"",
            "",
            "[mcp_servers.other]",
            "command = \"other\"",
            "",
            "[mcp_servers.pointframe]  # the old one",
            "command = \"pointframe\"",
            "args = [\"mcp\", \"serve\"]",
            "startup_timeout_sec = 30",
            "",
            "[mcp_servers.pointframe.env]",
            "KEY = \"v\"",
            "",
            "[profiles.fast]",
            "model = \"y\"",
            "");
        Assert.Equal(expected, File.ReadAllText(path));
        Assert.Equal(original, File.ReadAllText($"{path}.pointframe.bak"));
    }

    [Fact]
    public void MergeToml_AppendsATableToAFileWithoutOne()
    {
        var merged = ProjectMcpClientConfig.MergeToml("model = \"x\"\n");

        Assert.Equal("model = \"x\"\n\n[mcp_servers.pointframe]\ncommand = \"pointframe\"\nargs = [\"mcp\", \"serve\"]\n", merged);
        Assert.Equal(merged, ProjectMcpClientConfig.MergeToml(merged));
    }

    [Fact]
    public async Task Setup_InstallsTheServerOnlyWhenNoneResolves()
    {
        SetupProject();
        ArrangeSmoke();
        var installedPath = Path.Combine(_outside, "installed", "Pointframe.Mcp.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(installedPath)!);
        File.WriteAllBytes(installedPath, [0]);
        var installer = new Mock<IMcpPackageInstaller>();
        installer.Setup(item => item.InstallLatestAsync(false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new McpInstallation("9.9.9", installedPath, Path.GetDirectoryName(installedPath)!));

        Assert.Equal(0, await SetupAsync(mcpPath: null, installer: installer.Object));

        installer.Verify(item => item.InstallLatestAsync(false, It.IsAny<CancellationToken>()), Times.Once);
        using var json = JsonDocument.Parse(_output.ToString());
        Assert.True(json.RootElement.GetProperty("serverInstalled").GetBoolean());
        Assert.Equal("9.9.9", json.RootElement.GetProperty("serverVersion").GetString());
        Assert.Equal(installedPath, json.RootElement.GetProperty("serverPath").GetString());
    }

    [Fact]
    public async Task Setup_DoesNotInstallWhenAServerAlreadyResolves()
    {
        SetupProject();
        ArrangeSmoke();
        var installer = new Mock<IMcpPackageInstaller>();

        Assert.Equal(0, await SetupAsync(mcpPath: null, installer: installer.Object, installedMcp: () => _fixture.McpPath));

        installer.Verify(item => item.InstallLatestAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        using var json = JsonDocument.Parse(_output.ToString());
        Assert.False(json.RootElement.GetProperty("serverInstalled").GetBoolean());
    }

    [Fact]
    public async Task Setup_InstallFailureIsReportedWithACodeAndTheFilesAreStillWritten()
    {
        SetupProject();
        var installer = new Mock<IMcpPackageInstaller>();
        installer.Setup(item => item.InstallLatestAsync(false, It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException("offline"));

        var exit = await SetupAsync(mcpPath: null, installer: installer.Object);

        Assert.Equal(1, exit);
        using var json = JsonDocument.Parse(_output.ToString());
        Assert.Equal("mcp_install_failed", json.RootElement.GetProperty("code").GetString());
        Assert.True(File.Exists(Path.Combine(_fixture.Root, ".mcp.json")));
    }

    [Fact]
    public async Task Setup_SmokeFailureIsReportedWithACodeAndTheFilesAreStillWritten()
    {
        SetupProject();
        ArrangeCliOnPath();
        _services.Factory.Setup(item => item.LaunchAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("The MCP server exited."));

        var exit = await SetupAsync();

        Assert.Equal(1, exit);
        using var json = JsonDocument.Parse(_output.ToString());
        Assert.False(json.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("smoke_failed", json.RootElement.GetProperty("code").GetString());
        Assert.Contains(".mcp.json", _error.ToString() + _output, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(_fixture.Root, ".mcp.json")));
    }

    [Fact]
    public async Task Setup_ServerWithoutDesktopToolsOrWithoutTheAppFailsTheSmokeCheck()
    {
        SetupProject();
        ArrangeSmoke(tools: ["capture_monitor"]);
        Assert.Equal(1, await SetupAsync());
        Assert.Contains("smoke_tools_missing", _output.ToString(), StringComparison.Ordinal);

        _output.GetStringBuilder().Clear();
        ArrangeSmoke(appIds: ["another"]);
        Assert.Equal(1, await SetupAsync());
        Assert.Contains("smoke_app_not_listed", _output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Setup_UnbuiltAppStillPassesWithAWarning()
    {
        SetupProject();
        File.Delete(Path.Combine(_fixture.Root, "bin", "App.exe"));
        ArrangeSmoke();

        Assert.Equal(0, await SetupAsync());

        using var json = JsonDocument.Parse(_output.ToString());
        Assert.Contains("app_not_built", Assert.Single(json.RootElement.GetProperty("warnings").EnumerateArray()).GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Setup_UntrustedSpecWritesNothing()
    {
        _fixture.WriteUntrustedSpec("""{ "id": "fixture", "executable": "C:/Windows/System32/notepad.exe" }""", null, VerificationFixture.ValidScenario);
        _fixture.CreateAppAndMcp();

        var exit = await SetupAsync();

        Assert.Equal(1, exit);
        using var json = JsonDocument.Parse(_output.ToString());
        Assert.Equal("spec_untrusted", json.RootElement.GetProperty("code").GetString());
        Assert.False(File.Exists(Path.Combine(_fixture.Root, ".mcp.json")));
    }

    [Fact]
    public async Task Setup_PointframeNotOnPathIsReportedAndTheFilesAreStillWritten()
    {
        SetupProject();

        var exit = await SetupAsync();

        Assert.Equal(1, exit);
        Assert.Contains("pointframe_not_on_path", _output.ToString(), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(_fixture.Root, ".mcp.json")));
    }

    [Fact]
    public async Task Setup_InvalidExistingClientConfigIsReportedWithoutOverwritingIt()
    {
        SetupProject();
        ArrangeSmoke();
        var path = Path.Combine(_fixture.Root, ".mcp.json");
        File.WriteAllText(path, "[1, 2]");

        var exit = await SetupAsync();

        Assert.Equal(1, exit);
        Assert.Contains("client_config_invalid", _output.ToString(), StringComparison.Ordinal);
        Assert.Equal("[1, 2]", File.ReadAllText(path));
    }

    private void SetupProject()
    {
        _fixture.WriteSpec(VerificationFixture.ValidScenario);
        _fixture.CreateAppAndMcp();
    }

    private void TrustByPerson()
    {
        var spec = VerificationSpecLoader.Load(_fixture.SpecPath);
        _store.WriteTrust(new SpecTrust(1, _fixture.Root, SpecDigests.CommandsSha256(spec), SpecDigests.Commands(spec), DateTimeOffset.UtcNow));
    }

    private void ArrangeCliOnPath() => _services.CommandResolver
        .Setup(item => item.Resolve(It.IsAny<string?>()))
        .Returns(new PointframeCommandInfo(@"C:\tools\pointframe.exe", "Pointframe CLI 1.0", true));

    private SmokeLaunches ArrangeSmoke(string[]? tools = null, string[]? appIds = null)
    {
        ArrangeCliOnPath();
        var launches = new SmokeLaunches();
        var client = new Mock<IMcpToolClient>();
        client.Setup(item => item.ListToolsAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(JsonSerializer.SerializeToElement(new { tools = (tools ?? ["capture_monitor", "desktop_list_apps", "desktop_start_test_session"]).Select(name => new { name }) }));
        client.Setup(item => item.CallToolAsync("desktop_list_apps", It.IsAny<object>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(JsonSerializer.SerializeToElement(new
            {
                structuredContent = new { apps = (appIds ?? ["fixture"]).Select(id => new { id, executableName = "App.exe", allowedActionCount = 11 }) },
            }));
        _services.Factory.Setup(item => item.LaunchAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyList<string>, IReadOnlyDictionary<string, string>, CancellationToken>(
                (executable, arguments, _, _) => launches.Add(new SmokeLaunch(executable, [.. arguments])))
            .ReturnsAsync(client.Object);
        return launches;
    }

    private Task<int> ServeAsync(string? projectPath = null, string? mcpPath = null, string? currentDirectory = null)
    {
        var built = _services.Build(_store, serverHost: _host);
        var verification = new VerificationApplication(built, TextWriter.Null, _error);
        var command = new CliCommand("mcp", McpAction: "serve", ProjectPath: projectPath ?? (currentDirectory is null ? _fixture.Root : null), McpExecutablePath: mcpPath ?? _fixture.McpPath);
        return new DesktopToolsApplication(built, verification, _output, _error).ServeAsync(command, currentDirectory ?? _outside, CancellationToken.None);
    }

    private Task<int> SetupAsync(string client = "claude-code", string? mcpPath = "default", IMcpPackageInstaller? installer = null, Func<string?>? installedMcp = null)
    {
        var built = _services.Build(_store, installer: installer, installedMcp: installedMcp);
        var verification = new VerificationApplication(built, TextWriter.Null, _error);
        var command = new CliCommand(
            "verify", VerifyAction: "setup", McpClient: client, SpecPath: _fixture.SpecPath, McpExecutablePath: mcpPath == "default" ? _fixture.McpPath : mcpPath);
        return new DesktopToolsApplication(built, verification, _output, _error).SetupAsync(command, CancellationToken.None);
    }

    private static void CreateJunction(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            ArgumentList = { "/c", "mklink", "/J", link, target },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        })!;
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    private sealed record SmokeLaunch(string Executable, IReadOnlyList<string> Arguments);

    private sealed class SmokeLaunches : List<SmokeLaunch>
    {
    }

    private sealed record ServerLaunch(
        string Executable,
        IReadOnlyList<string> Arguments,
        IReadOnlyDictionary<string, string> Environment,
        string PolicyJson,
        bool DataDirectoryExisted);

    private sealed class FakeServerHost : IMcpServerHost
    {
        internal List<ServerLaunch> Launches { get; } = [];

        internal int ExitCode { get; set; }

        public Task<int> RunAsync(
            string executablePath,
            IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string> environment,
            CancellationToken cancellationToken)
        {
            var dataDirectory = environment.Values.FirstOrDefault();
            Launches.Add(new ServerLaunch(
                executablePath,
                [.. arguments],
                new Dictionary<string, string>(environment),
                File.ReadAllText(arguments[2]),
                dataDirectory is not null && Directory.Exists(dataDirectory)));
            return Task.FromResult(ExitCode);
        }
    }
}
