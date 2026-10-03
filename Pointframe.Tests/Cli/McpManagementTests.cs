using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Moq;
using Pointframe.Cli;
using Xunit;

namespace Pointframe.Tests.Cli;

public sealed class McpManagementTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Pointframe.McpManagementTests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("install")]
    [InlineData("status")]
    [InlineData("doctor")]
    public void TryParse_McpActionWithVsCodeClient_ParsesCommand(string action)
    {
        var parsed = CliCommandParser.TryParse(["mcp", action, "--client", "vscode"], out var command, out var error);

        Assert.True(parsed);
        Assert.Null(error);
        Assert.Equal("mcp", command.Name);
        Assert.Equal(action, command.McpAction);
        Assert.Equal("vscode", command.McpClient);
    }

    [Fact]
    public void TryParse_McpInstallDryRun_RecordsDryRun()
    {
        var parsed = CliCommandParser.TryParse(["mcp", "install", "--client=vscode", "--dry-run"], out var command, out var error);

        Assert.True(parsed);
        Assert.Null(error);
        Assert.True(command.DryRun);
    }

    [Theory]
    [InlineData("cursor")]
    [InlineData("claude-code")]
    public void TryParse_McpUnsupportedClient_ReturnsUsageError(string client)
    {
        var parsed = CliCommandParser.TryParse(["mcp", "install", "--client", client], out _, out var error);

        Assert.False(parsed);
        Assert.Equal("The first supported MCP client is vscode; pass --client vscode.", error);
    }

    [Fact]
    public async Task InstallLatestAsync_ValidPackage_InstallsVersionedPackageAndPersistsState()
    {
        var package = CreatePackage("7.0.0");
        var source = new Mock<IMcpPackageSource>();
        source.Setup(item => item.DownloadLatestAsync(It.IsAny<CancellationToken>())).ReturnsAsync(package);
        var installer = new McpPackageInstaller(source.Object, _root);

        var installation = await installer.InstallLatestAsync(dryRun: false, CancellationToken.None);

        Assert.Equal("7.0.0", installation.Version);
        Assert.True(File.Exists(installation.ExecutablePath));
        Assert.Equal(installation, installer.GetCurrent());
    }

    [Fact]
    public async Task InstallLatestAsync_ChecksumMismatch_RejectsPackageWithoutPersistentFiles()
    {
        var package = CreatePackage("7.0.0") with { ChecksumText = new string('0', 64) };
        var source = new Mock<IMcpPackageSource>();
        source.Setup(item => item.DownloadLatestAsync(It.IsAny<CancellationToken>())).ReturnsAsync(package);
        var installer = new McpPackageInstaller(source.Object, _root);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => installer.InstallLatestAsync(dryRun: false, CancellationToken.None));

        Assert.Contains("SHA-256", exception.Message, StringComparison.Ordinal);
        Assert.Null(installer.GetCurrent());
    }

    [Fact]
    public async Task InstallLatestAsync_DryRun_ValidatesWithoutPersistingInstallation()
    {
        var package = CreatePackage("7.0.0");
        var source = new Mock<IMcpPackageSource>();
        source.Setup(item => item.DownloadLatestAsync(It.IsAny<CancellationToken>())).ReturnsAsync(package);
        var installer = new McpPackageInstaller(source.Object, _root);

        var installation = await installer.InstallLatestAsync(dryRun: true, CancellationToken.None);

        Assert.Equal("7.0.0", installation.Version);
        Assert.False(Directory.Exists(_root));
        Assert.Null(installer.GetCurrent());
    }

    [Fact]
    public void Configure_ExistingVsCodeConfiguration_PreservesOtherServersAndCreatesBackup()
    {
        var configurationPath = Path.Combine(_root, "Code", "User", "mcp.json");
        Directory.CreateDirectory(Path.GetDirectoryName(configurationPath)!);
        const string original = "{\"servers\":{\"other\":{\"type\":\"stdio\",\"command\":\"other.exe\"}},\"inputs\":[]}";
        File.WriteAllText(configurationPath, original);
        var configurator = new VsCodeMcpConfigurator(configurationPath);

        configurator.Configure(@"C:\Pointframe\Pointframe.Mcp.exe", dryRun: false);

        using var configuration = JsonDocument.Parse(File.ReadAllText(configurationPath));
        Assert.Equal("other.exe", configuration.RootElement.GetProperty("servers").GetProperty("other").GetProperty("command").GetString());
        Assert.Equal(@"C:\Pointframe\Pointframe.Mcp.exe", configuration.RootElement.GetProperty("servers").GetProperty("pointframe").GetProperty("command").GetString());
        Assert.Equal(original, File.ReadAllText($"{configurationPath}.pointframe.bak"));
    }

    [Fact]
    public void Configure_DryRun_DoesNotCreateConfiguration()
    {
        var configurationPath = Path.Combine(_root, "Code", "User", "mcp.json");
        var configurator = new VsCodeMcpConfigurator(configurationPath);

        configurator.Configure(@"C:\Pointframe\Pointframe.Mcp.exe", dryRun: true);

        Assert.False(File.Exists(configurationPath));
    }

    [Fact]
    public async Task RunAsync_Install_ConfiguresAndVerifiesPackage()
    {
        var installation = new McpInstallation("7.0.0", @"C:\Pointframe\Pointframe.Mcp.exe", @"C:\Pointframe");
        var installer = new Mock<IMcpPackageInstaller>();
        installer.Setup(item => item.InstallLatestAsync(false, It.IsAny<CancellationToken>())).ReturnsAsync(installation);
        var configurator = new Mock<IMcpClientConfigurator>();
        configurator.SetupGet(item => item.ConfigurationPath).Returns(@"C:\Code\mcp.json");
        var healthChecker = new Mock<IMcpHealthChecker>();
        healthChecker.Setup(item => item.CheckAsync(installation.ExecutablePath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new McpHealthResult(true, "healthy", "Healthy", McpStdioHealthChecker.ExpectedTools));
        var output = new StringWriter();
        var error = new StringWriter();
        var application = new McpManagementApplication(installer.Object, configurator.Object, healthChecker.Object, output, error);
        var command = new CliCommand("mcp", McpAction: "install", McpClient: "vscode");

        var exitCode = await application.RunAsync(command, CancellationToken.None);

        Assert.Equal(0, exitCode);
        using var response = JsonDocument.Parse(output.ToString());
        Assert.True(response.RootElement.GetProperty("Success").GetBoolean());
        configurator.Verify(item => item.Configure(installation.ExecutablePath, false), Times.Once);
        healthChecker.Verify(item => item.CheckAsync(installation.ExecutablePath, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task CheckAsync_MissingExecutable_ReturnsPackageNotInstalled()
    {
        var checker = new McpStdioHealthChecker();

        var result = await checker.CheckAsync(Path.Combine(_root, "missing.exe"), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("package_not_installed", result.Code);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static McpPackageDownload CreatePackage(string version)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "Pointframe.Mcp.exe", "fake executable");
            WriteEntry(archive, "manifest.json", $"{{\"version\":\"{version}\"}}");
        }

        var bundle = stream.ToArray();
        var checksum = Convert.ToHexStringLower(SHA256.HashData(bundle));
        return new McpPackageDownload(bundle, $"{checksum}  Pointframe.Mcp-win-x64.mcpb");
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(content);
    }
}
