using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Moq;
using Pointframe.Cli;
using Pointframe.Telemetry;
using Xunit;

namespace Pointframe.Tests.Cli;

// Runs the real CliApplication.RunAsync wiring (parser, package source, installer, verification) with only the
// identity, the network handler, the folders and the external processes replaced, so reverting any call site to
// the latest release makes one of these fail.
public sealed class McpInstallWiringTests : IDisposable
{
    private const string Version = "6.7.123";
    private const string Releases = "https://github.com/dimitar-radenkov/Pointframe/releases";
    private const string BundleName = "Pointframe.Mcp-win-x64.mcpb";
    private const string VersionedPrefix = $"{Releases}/download/v{Version}/";

    private readonly VerificationFixture _fixture = new();
    private readonly string _installRoot = Path.Combine(Path.GetTempPath(), "Pointframe.McpInstallWiringTests", Guid.NewGuid().ToString("N"));
    private readonly StringWriter _output = new();
    private readonly StringWriter _error = new();

    public void Dispose()
    {
        _fixture.Dispose();
        if (Directory.Exists(_installRoot))
        {
            Directory.Delete(_installRoot, recursive: true);
        }
    }

    [Fact]
    public async Task McpInstall_ReleaseBuild_RequestsOnlyTheVersionedAssets()
    {
        var handler = new RecordingHandler(versionedAvailable: true);

        var exit = await RunAsync(handler, ["mcp", "install", "--client", "vscode"], HealthyChecker());

        Assert.Equal(0, exit);
        Assert.Equal(ExpectedVersionedRequests, handler.Requests);
        Assert.Equal(Version, new McpPackageInstaller(new Mock<IMcpPackageSource>().Object, _installRoot).GetCurrent()?.Version);
    }

    [Fact]
    public async Task McpInstall_VersionedAssetsMissing_ReportsTheCodeNeverRequestsLatestAndInstallsNothing()
    {
        var handler = new RecordingHandler(versionedAvailable: false);

        var exit = await RunAsync(handler, ["mcp", "install", "--client", "vscode"], HealthyChecker());

        Assert.Equal(1, exit);
        Assert.Contains(McpReleaseAssetMissingException.Code, _output.ToString(), StringComparison.Ordinal);
        AssertOnlyVersionedBundleRequested(handler);
        Assert.False(Directory.Exists(_installRoot));
    }

    [Fact]
    public async Task VerifyRun_MissingServerFallbackInstall_RequestsOnlyTheVersionedAssets()
    {
        var handler = new RecordingHandler(versionedAvailable: true);

        var exit = await RunVerifyAsync(handler, await ArrangeVerifyAsync());

        Assert.Equal(0, exit);
        Assert.Equal(ExpectedVersionedRequests, handler.Requests);
        Assert.Contains("no MCP server was found, so installed " + Version, _error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyRun_MissingServerFallbackInstallAssetsMissing_ReportsTheCodeNeverRequestsLatestAndInstallsNothing()
    {
        var handler = new RecordingHandler(versionedAvailable: false);

        var exit = await RunVerifyAsync(handler, await ArrangeVerifyAsync());

        Assert.Equal(1, exit);
        Assert.Contains(McpReleaseAssetMissingException.Code, _output.ToString(), StringComparison.Ordinal);
        AssertOnlyVersionedBundleRequested(handler);
        Assert.False(Directory.Exists(_installRoot));
    }

    [Fact]
    public async Task VerifyRun_OlderInstalledServer_UpdatesFromTheVersionedAssetsOnly()
    {
        SeedOlderInstall();
        var handler = new RecordingHandler(versionedAvailable: true);

        var exit = await RunVerifyAsync(handler, await ArrangeVerifyAsync());

        Assert.Equal(0, exit);
        Assert.Equal(ExpectedVersionedRequests, handler.Requests);
        Assert.Contains($"updated the MCP server from 6.7.100 to {Version}", _error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyRun_OlderInstalledServerAssetsMissing_ReportsTheCodeNeverRequestsLatestAndKeepsTheOldServer()
    {
        SeedOlderInstall();
        var handler = new RecordingHandler(versionedAvailable: false);

        var exit = await RunVerifyAsync(handler, await ArrangeVerifyAsync());

        Assert.Equal(1, exit);
        Assert.Contains(McpReleaseAssetMissingException.Code, _output.ToString(), StringComparison.Ordinal);
        AssertOnlyVersionedBundleRequested(handler);
        AssertOnlyOlderServerInstalled();
    }

    [Fact]
    public async Task McpServe_OlderInstalledServer_UpdatesFromTheVersionedAssetsOnly()
    {
        SeedOlderInstall();
        _fixture.WriteSpec(VerificationFixture.ValidScenario);
        _fixture.CreateAppAndMcp();
        var handler = new RecordingHandler(versionedAvailable: true);
        var host = new RecordingServerHost();

        var exit = await RunAsync(handler, ["mcp", "serve", "--project", _fixture.Root], serverHost: host);

        Assert.Equal(0, exit);
        Assert.Equal(ExpectedVersionedRequests, handler.Requests);
        Assert.Equal(string.Empty, _output.ToString());
        Assert.Contains($"updated the MCP server from 6.7.100 to {Version}", _error.ToString(), StringComparison.Ordinal);
        Assert.Equal(Path.Combine(_installRoot, Version, "Pointframe.Mcp.exe"), Assert.Single(host.Launches));
    }

    [Fact]
    public async Task McpServe_OlderInstalledServerAssetsMissing_ReportsTheCodeOnStderrNeverRequestsLatestAndKeepsTheOldServer()
    {
        SeedOlderInstall();
        _fixture.WriteSpec(VerificationFixture.ValidScenario);
        _fixture.CreateAppAndMcp();
        var handler = new RecordingHandler(versionedAvailable: false);
        var host = new RecordingServerHost();

        var exit = await RunAsync(handler, ["mcp", "serve", "--project", _fixture.Root], serverHost: host);

        Assert.Equal(1, exit);
        Assert.Contains(McpReleaseAssetMissingException.Code, _error.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, _output.ToString());
        Assert.Empty(host.Launches);
        AssertOnlyVersionedBundleRequested(handler);
        AssertOnlyOlderServerInstalled();
    }

    private static Uri[] ExpectedVersionedRequests =>
        [new Uri($"{VersionedPrefix}{BundleName}"), new Uri($"{VersionedPrefix}{BundleName}.sha256")];

    private static void AssertOnlyVersionedBundleRequested(RecordingHandler handler)
    {
        Assert.NotEmpty(handler.Requests);
        Assert.All(handler.Requests, uri => Assert.StartsWith(VersionedPrefix, uri.AbsoluteUri, StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Requests, uri => uri.AbsoluteUri.Contains("/latest/", StringComparison.Ordinal));
    }

    private static IMcpHealthChecker HealthyChecker()
    {
        var checker = new Mock<IMcpHealthChecker>();
        checker.Setup(item => item.CheckAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new McpHealthResult(true, "healthy", "Healthy", McpStdioHealthChecker.ExpectedTools));
        return checker.Object;
    }

    private async Task<VerificationFixture.Services> ArrangeVerifyAsync()
    {
        _fixture.WriteSpec(VerificationFixture.ValidScenario);
        _fixture.CreateAppAndMcp();
        return new VerificationFixture.Services(new FakeMcp(await _fixture.WriteSealedBundleAsync(criterionPassed: true)));
    }

    private Task<int> RunVerifyAsync(RecordingHandler handler, VerificationFixture.Services services) =>
        RunAsync(handler, ["verify", "run", "--spec", _fixture.SpecPath], services: services);

    private Task<int> RunAsync(
        RecordingHandler handler,
        string[] args,
        IMcpHealthChecker? healthChecker = null,
        VerificationFixture.Services? services = null,
        IMcpServerHost? serverHost = null)
    {
        services ??= new VerificationFixture.Services();
        var seams = new CliHostSeams
        {
            Version = Version,
            IsReleaseBuild = true,
            Handler = handler,
            InstallRoot = _installRoot,
            ConfigurationPath = Path.Combine(_fixture.Root, "vscode", "mcp.json"),
            Store = _fixture.Store,
            HealthChecker = healthChecker,
            CustomizeVerification = real => services.Build(
                _fixture.Store,
                serverHost: serverHost,
                installer: real.McpInstaller,
                installedMcp: real.InstalledMcpExecutable,
                verifierVersion: real.VerifierVersion),
        };
        return CliApplication.RunAsync(args, _output, _error, CancellationToken.None, NullOperationTelemetry.Instance, seams);
    }

    private void SeedOlderInstall()
    {
        var directory = Path.Combine(_installRoot, "6.7.100");
        Directory.CreateDirectory(directory);
        var executable = Path.Combine(directory, "Pointframe.Mcp.exe");
        File.WriteAllBytes(executable, [0]);
        File.WriteAllText(
            Path.Combine(_installRoot, "install-state.json"),
            JsonSerializer.Serialize(new McpInstallation("6.7.100", executable, directory)));
    }

    private void AssertOnlyOlderServerInstalled()
    {
        var current = new McpPackageInstaller(new Mock<IMcpPackageSource>().Object, _installRoot).GetCurrent();
        Assert.Equal("6.7.100", current?.Version);
        Assert.False(Directory.Exists(Path.Combine(_installRoot, Version)));
    }

    private static byte[] CreateBundle()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "Pointframe.Mcp.exe", "fake executable");
            WriteEntry(archive, "manifest.json", $"{{\"version\":\"{Version}\"}}");
        }

        return stream.ToArray();
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(content);
    }

    // Answers a valid bundle for any URI except the versioned ones when they are made unavailable, so a call site
    // that fell back to latest would succeed and be caught by the recorded URIs.
    private sealed class RecordingHandler(bool versionedAvailable) : HttpMessageHandler
    {
        private static readonly byte[] Bundle = CreateBundle();

        internal List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Requests.Add(uri);
            if (!versionedAvailable && uri.AbsoluteUri.StartsWith(VersionedPrefix, StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            var content = uri.AbsolutePath.EndsWith(".sha256", StringComparison.Ordinal)
                ? Encoding.UTF8.GetBytes($"{Convert.ToHexStringLower(SHA256.HashData(Bundle))}  {BundleName}")
                : Bundle;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) });
        }
    }

    private sealed class RecordingServerHost : IMcpServerHost
    {
        internal List<string> Launches { get; } = [];

        public Task<int> RunAsync(
            string executablePath,
            IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string> environment,
            CancellationToken cancellationToken)
        {
            Launches.Add(executablePath);
            return Task.FromResult(0);
        }
    }
}
