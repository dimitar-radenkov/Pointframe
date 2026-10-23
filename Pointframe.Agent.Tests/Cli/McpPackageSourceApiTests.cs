using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Moq;
using Pointframe.Cli;
using Pointframe.Telemetry;
using Xunit;

namespace Pointframe.Tests.Cli;

// Candidate and source builds find the newest agent release through the Releases API (no token); release builds stay
// pinned to their own version and never call it. The fake GitHub below answers the API listing and the downloads.
public sealed class McpPackageSourceApiTests : IDisposable
{
    private const string Download = "https://github.com/dimitar-radenkov/Pointframe/releases/download/";
    private const string Api = "https://api.github.com/repos/dimitar-radenkov/Pointframe/releases?per_page=100&page=";
    private const string LatestAlias = "https://github.com/dimitar-radenkov/Pointframe/releases/latest/download/Pointframe.Mcp-win-x64.mcpb";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "Pointframe.McpPackageSourceApiTests", Guid.NewGuid().ToString("N"));
    private readonly StringWriter _warnings = new();

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task NewestCompleteAgentRelease_IsChosenOverANewerDesktopOnlyRelease()
    {
        var github = new FakeGitHub(Page(Desktop("6.9.3"), Agent("6.9.2"), Combined("6.9.1")));

        var download = await NewSource(github).DownloadLatestAsync(CancellationToken.None);

        Assert.Equal("6.9.2", download.ReleaseVersion);
        Assert.Equal([$"{Api}1", $"{Download}v6.9.2/Pointframe.Mcp-6.9.2-win-x64.mcpb", $"{Download}v6.9.2/Pointframe.Mcp-6.9.2-win-x64.mcpb.sha256"], github.RequestUris);
        Assert.Equal(github.Bundle, download.Bundle);
        Assert.Contains(Convert.ToHexStringLower(SHA256.HashData(github.Bundle)), download.ChecksumText, StringComparison.Ordinal);
        Assert.Equal(string.Empty, _warnings.ToString());
    }

    [Fact]
    public async Task HighestVersionWins_EvenWhenAnOlderVersionWasCreatedLater()
    {
        var github = new FakeGitHub(Page(Agent("6.8.9"), Agent("6.9.0"), Agent("6.8.10")));

        var download = await NewSource(github).DownloadLatestAsync(CancellationToken.None);

        Assert.Equal("6.9.0", download.ReleaseVersion);
        Assert.Contains($"{Download}v6.9.0/Pointframe.Mcp-6.9.0-win-x64.mcpb", github.RequestUris);
    }

    [Fact]
    public async Task VersionsAreCompared_Numerically()
    {
        var github = new FakeGitHub(Page(Agent("6.7.99"), Agent("6.7.100"), Agent("6.7.9")));

        var download = await NewSource(github).DownloadLatestAsync(CancellationToken.None);

        Assert.Equal("6.7.100", download.ReleaseVersion);
    }

    [Fact]
    public async Task DraftPrereleaseUnpublishedAndMalformedTags_AreIgnored()
    {
        var github = new FakeGitHub(Page(
            Agent("6.9.9", draft: true),
            Agent("6.9.8", prerelease: true),
            Agent("6.9.7", publishedAt: null),
            Agent("6.9.6", tag: "v6.9.6-rc1"),
            Agent("6.9.5", tag: "6.9.5"),
            Agent("6.9.4", tag: "v6.9"),
            Agent("6.9.3", tag: "V6.9.3"),
            Agent("6.9.2", tag: "agent-v6.9.2"),
            Agent("6.9.1")));

        var download = await NewSource(github).DownloadLatestAsync(CancellationToken.None);

        Assert.Equal("6.9.1", download.ReleaseVersion);
    }

    [Fact]
    public async Task IncompletePairs_AreSkipped()
    {
        var github = new FakeGitHub(Page(
            Release("v6.9.6", "Pointframe.Cli-6.9.6-win-x64.zip", "Pointframe.Mcp-6.9.6-win-x64.mcpb"),
            Release("v6.9.5", "Pointframe.Cli-6.9.5-win-x64.zip", "Pointframe.Cli-6.9.5-win-x64.zip.sha256"),
            Release("v6.9.4", "Pointframe.Cli-6.9.4-win-x64.zip.sha256", "Pointframe.Mcp-6.9.4-win-x64.mcpb"),
            Release("v6.9.3", "Pointframe.Cli-6.9.2-win-x64.zip", "Pointframe.Cli-6.9.2-win-x64.zip.sha256", "Pointframe.Mcp-6.9.2-win-x64.mcpb"),
            Agent("6.9.1")));

        var download = await NewSource(github).DownloadLatestAsync(CancellationToken.None);

        Assert.Equal("6.9.1", download.ReleaseVersion);
    }

    [Fact]
    public async Task Paging_StopsAtTheFirstPageThatHasACompleteAgentRelease()
    {
        var github = new FakeGitHub(FullDesktopPage(1, with: Agent("6.2.0")), FullDesktopPage(2));

        var download = await NewSource(github).DownloadLatestAsync(CancellationToken.None);

        Assert.Equal("6.2.0", download.ReleaseVersion);
        Assert.Equal([$"{Api}1"], github.RequestUris.Where(uri => uri.StartsWith(Api, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Paging_ReadsBeyondTheFirstHundredReleases()
    {
        var github = new FakeGitHub(FullDesktopPage(1), Page(Agent("6.2.0")));

        var download = await NewSource(github).DownloadLatestAsync(CancellationToken.None);

        Assert.Equal("6.2.0", download.ReleaseVersion);
        Assert.Equal([$"{Api}1", $"{Api}2"], github.RequestUris.Where(uri => uri.StartsWith(Api, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Paging_HasAHardCapAndTheErrorNamesIt()
    {
        var github = new FakeGitHub(Enumerable.Range(1, GitHubMcpPackageSource.MaxReleasePages + 5).Select(page => FullDesktopPage(page)).ToArray());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => NewSource(github).DownloadLatestAsync(CancellationToken.None));

        Assert.Contains($"page limit {GitHubMcpPackageSource.MaxReleasePages}", error.Message);
        Assert.Equal(GitHubMcpPackageSource.MaxReleasePages, github.RequestUris.Count);
        Assert.DoesNotContain(github.RequestUris, uri => uri.StartsWith(Download, StringComparison.Ordinal));
    }

    [Fact]
    public async Task NoCompleteAgentRelease_FailsClearlyWithoutAnyDownloadOrFallback()
    {
        var github = new FakeGitHub(Page(Desktop("6.9.3"), Agent("6.9.2", draft: true), Release("v6.9.1", "Pointframe.Cli-6.9.1-win-x64.zip")));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => NewSource(github).DownloadLatestAsync(CancellationToken.None));

        Assert.Contains("No published Pointframe release has the CLI zip, its checksum and the MCP bundle", error.Message);
        Assert.Equal([$"{Api}1"], github.RequestUris);
        Assert.Equal(string.Empty, _warnings.ToString());
    }

    [Fact]
    public async Task ApiRequest_SendsTheDocumentedHeadersAndNoToken()
    {
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", "must-never-be-sent");
        try
        {
            var github = new FakeGitHub(Page(Agent("6.9.2")));

            await NewSource(github).DownloadLatestAsync(CancellationToken.None);

            var api = Assert.Single(github.Requests, request => request.Uri.StartsWith(Api, StringComparison.Ordinal));
            Assert.Equal("Pointframe-CLI", api.UserAgent);
            Assert.Equal("application/vnd.github+json", api.Accept);
            Assert.False(api.HasAuthorization);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", null);
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task RateLimitedApi_FallsBackToTheLatestAliasWithAWarningOnTheWarningWriter(HttpStatusCode status)
    {
        var github = new FakeGitHub(new HttpResponseMessage(status));

        var download = await NewSource(github).DownloadLatestAsync(CancellationToken.None);

        Assert.Null(download.ReleaseVersion);
        Assert.Equal([$"{Api}1", LatestAlias, $"{LatestAlias}.sha256"], github.RequestUris);
        Assert.Contains("warning", _warnings.ToString(), StringComparison.Ordinal);
        Assert.Contains($"HTTP {(int)status}", _warnings.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NetworkErrorOnTheApiCall_FallsBackToTheLatestAliasWithAWarning()
    {
        var github = new FakeGitHub(new HttpRequestException("connection reset"));

        var download = await NewSource(github).DownloadLatestAsync(CancellationToken.None);

        Assert.Null(download.ReleaseVersion);
        Assert.Equal([$"{Api}1", LatestAlias, $"{LatestAlias}.sha256"], github.RequestUris);
        Assert.Contains("connection reset", _warnings.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task OtherApiFailures_AreErrorsNotFallbacks(HttpStatusCode status)
    {
        var github = new FakeGitHub(new HttpResponseMessage(status));

        await Assert.ThrowsAsync<HttpRequestException>(() => NewSource(github).DownloadLatestAsync(CancellationToken.None));

        Assert.Equal([$"{Api}1"], github.RequestUris);
        Assert.Equal(string.Empty, _warnings.ToString());
    }

    [Fact]
    public async Task ACancelledApiCall_IsNotAFallback()
    {
        var github = new FakeGitHub(Page(Agent("6.9.2")));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NewSource(github).DownloadLatestAsync(cancellation.Token));

        Assert.Equal(string.Empty, _warnings.ToString());
    }

    [Fact]
    public async Task MissingVersionedAssetOfTheChosenRelease_IsAnErrorNotAFallback()
    {
        var github = new FakeGitHub(Page(Agent("6.9.2"))) { VersionedAssetsAvailable = false };

        var error = await Assert.ThrowsAsync<McpReleaseAssetMissingException>(() => NewSource(github).DownloadLatestAsync(CancellationToken.None));

        Assert.Contains("v6.9.2", error.Message);
        Assert.DoesNotContain(github.RequestUris, uri => uri.Contains("/latest/", StringComparison.Ordinal));
        Assert.Equal(string.Empty, _warnings.ToString());
    }

    [Fact]
    public async Task HashMismatchOfTheChosenRelease_FailsWithoutInstallingOrFallingBack()
    {
        var github = new FakeGitHub(Page(Agent("6.9.2"))) { ChecksumText = new string('0', 64) };
        var installer = new McpPackageInstaller(NewSource(github), _root);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => installer.InstallLatestAsync(false, CancellationToken.None));

        Assert.Contains("does not match its published SHA-256", error.Message);
        Assert.False(Directory.Exists(_root));
        Assert.DoesNotContain(github.RequestUris, uri => uri.Contains("/latest/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InstallingTheChosenRelease_ActivatesItsVersion()
    {
        var github = new FakeGitHub(Page(Agent("6.9.2")));
        var installer = new McpPackageInstaller(NewSource(github), _root);

        var installation = await installer.InstallLatestAsync(false, CancellationToken.None);

        Assert.Equal("6.9.2", installation.Version);
        Assert.Equal("6.9.2", installer.GetCurrent()?.Version);
    }

    [Theory]
    [InlineData("6.9.2")]
    [InlineData("6.9.3")]
    public async Task AReleaseAtOrBelowTheInstalledVersion_IsNeverInstalled(string installedVersion)
    {
        var current = SeedInstall(installedVersion);
        var github = new FakeGitHub(Page(Agent("6.9.2")));
        var installer = new McpPackageInstaller(NewSource(github), _root);

        var installation = await installer.InstallLatestAsync(false, CancellationToken.None);

        Assert.Equal(current, installation);
        Assert.Equal(installedVersion, installer.GetCurrent()?.Version);
        Assert.Equal([(byte)0], File.ReadAllBytes(current.ExecutablePath));
    }

    [Theory]
    [InlineData("6.9.2", "6.9.1")]
    [InlineData("6.9.2", "6.9.2")]
    public async Task AFallbackAliasBundleAtOrBelowTheInstalledVersion_IsNeverInstalled(string installedVersion, string aliasVersion)
    {
        var current = SeedInstall(installedVersion);
        var github = new FakeGitHub(new HttpResponseMessage(HttpStatusCode.Forbidden)) { BundleVersion = aliasVersion };
        var installer = new McpPackageInstaller(NewSource(github), _root);

        var installation = await installer.InstallLatestAsync(false, CancellationToken.None);

        Assert.Equal([$"{Api}1", LatestAlias, $"{LatestAlias}.sha256"], github.RequestUris);
        Assert.Equal(current, installation);
        Assert.Equal(installedVersion, installer.GetCurrent()?.Version);
        Assert.False(Directory.Exists(Path.Combine(_root, aliasVersion)) && aliasVersion != installedVersion);
    }

    [Fact]
    public async Task AFallbackAliasBundleAboveTheInstalledVersion_UpdatesIt()
    {
        SeedInstall("6.9.1");
        var github = new FakeGitHub(new HttpResponseMessage(HttpStatusCode.Forbidden)) { BundleVersion = "6.9.2" };
        var installer = new McpPackageInstaller(NewSource(github), _root);

        var installation = await installer.InstallLatestAsync(false, CancellationToken.None);

        Assert.Equal("6.9.2", installation.Version);
        Assert.Equal("6.9.2", installer.GetCurrent()?.Version);
    }

    [Fact]
    public async Task AReleaseAboveTheInstalledVersion_UpdatesIt()
    {
        SeedInstall("6.9.1");
        var github = new FakeGitHub(Page(Agent("6.9.2")));
        var installer = new McpPackageInstaller(NewSource(github), _root);

        var installation = await installer.InstallLatestAsync(false, CancellationToken.None);

        Assert.Equal("6.9.2", installation.Version);
        Assert.Equal("6.9.2", installer.GetCurrent()?.Version);
    }

    [Theory]
    [InlineData("6.7.123", true)]
    [InlineData("6.7.123+abc123", true)]
    public async Task ReleaseBuild_RequestsOnlyItsVersionedAssetsAndNeverTheApi(string version, bool marker)
    {
        var github = new FakeGitHub(new HttpResponseMessage(HttpStatusCode.Forbidden));
        using var client = new HttpClient(github);

        await new GitHubMcpPackageSource(client, version, marker, _warnings).DownloadLatestAsync(CancellationToken.None);

        Assert.Equal([$"{Download}v6.7.123/Pointframe.Mcp-win-x64.mcpb", $"{Download}v6.7.123/Pointframe.Mcp-win-x64.mcpb.sha256"], github.RequestUris);
        Assert.DoesNotContain(github.RequestUris, uri => uri.Contains("api.github.com", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("0.0.0", true)]
    [InlineData("6.7.124-preview.1", true)]
    [InlineData("6.7.124", false)]
    [InlineData(null, false)]
    public async Task NonReleaseBuilds_UseTheApi(string? version, bool marker)
    {
        var github = new FakeGitHub(Page(Agent("6.9.2")));
        using var client = new HttpClient(github);

        var download = await new GitHubMcpPackageSource(client, version, marker, _warnings).DownloadLatestAsync(CancellationToken.None);

        Assert.Equal("6.9.2", download.ReleaseVersion);
        Assert.Equal($"{Api}1", github.RequestUris[0]);
    }

    [Fact]
    public async Task McpInstallOnACandidateBuild_RateLimitWarningGoesToStandardErrorOnly()
    {
        var github = new FakeGitHub(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        var checker = new Mock<IMcpHealthChecker>();
        checker.Setup(item => item.CheckAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new McpHealthResult(true, "healthy", "Healthy", McpStdioHealthChecker.ExpectedTools));
        var output = new StringWriter();
        var error = new StringWriter();
        var seams = new CliHostSeams
        {
            Version = "0.0.0",
            IsReleaseBuild = false,
            Handler = github,
            InstallRoot = _root,
            ConfigurationPath = Path.Combine(_root, "vscode", "mcp.json"),
            HealthChecker = checker.Object,
        };

        var exit = await CliApplication.RunAsync(["mcp", "install", "--client", "vscode"], output, error, CancellationToken.None, NullOperationTelemetry.Instance, seams);

        Assert.Equal(0, exit);
        Assert.Equal([$"{Api}1", LatestAlias, $"{LatestAlias}.sha256"], github.RequestUris);
        Assert.Contains("warning: could not read the GitHub releases list", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("warning", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private GitHubMcpPackageSource NewSource(FakeGitHub github) =>
        new(new HttpClient(github, disposeHandler: false), "0.0.0", isReleaseBuild: false, _warnings);

    private McpInstallation SeedInstall(string version)
    {
        var directory = Path.Combine(_root, version);
        Directory.CreateDirectory(directory);
        var executable = Path.Combine(directory, "Pointframe.Mcp.exe");
        File.WriteAllBytes(executable, [0]);
        var installation = new McpInstallation(version, executable, directory);
        File.WriteAllText(Path.Combine(_root, "install-state.json"), JsonSerializer.Serialize(installation));
        return installation;
    }

    private static string[] AgentAssets(string version) =>
        [$"Pointframe.Cli-{version}-win-x64.zip", $"Pointframe.Cli-{version}-win-x64.zip.sha256", $"Pointframe.Mcp-{version}-win-x64.mcpb"];

    private static string Release(string tag, params string[] assets) => Release(tag, false, false, "2026-10-20T10:00:00Z", assets);

    private static string Release(string tag, bool draft, bool prerelease, string? publishedAt, string[] assets) => JsonSerializer.Serialize(new
    {
        tag_name = tag,
        draft,
        prerelease,
        published_at = publishedAt,
        assets = assets.Select(name => new { name }),
    });

    private static string Agent(string version, bool draft = false, bool prerelease = false, string? publishedAt = "2026-10-20T10:00:00Z", string? tag = null) =>
        Release(tag ?? $"v{version}", draft, prerelease, publishedAt, AgentAssets(version));

    private static string Desktop(string version) => Release($"v{version}", $"Pointframe-{version}-x64-Setup.exe");

    private static string Combined(string version) => Release($"v{version}", [.. AgentAssets(version), $"Pointframe-{version}-x64-Setup.exe"]);

    private static HttpResponseMessage Page(params string[] releases) =>
        new(HttpStatusCode.OK) { Content = new StringContent($"[{string.Join(',', releases)}]", Encoding.UTF8, "application/json") };

    private static HttpResponseMessage FullDesktopPage(int page, string? with = null)
    {
        var releases = Enumerable.Range(1, 100).Select(index => Desktop($"6.{page}.{index}")).ToList();
        if (with is not null)
        {
            releases[^1] = with;
        }

        return Page([.. releases]);
    }

    private static byte[] CreateBundle(string version)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "Pointframe.Mcp.exe", "fake executable");
            WriteEntry(archive, "manifest.json", $"{{\"version\":\"{version}\"}}");
        }

        return stream.ToArray();
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(content);
    }

    private sealed record RecordedRequest(string Uri, string? UserAgent, string? Accept, bool HasAuthorization);

    // Answers API pages in order (a response or an exception per page), then bundles and checksums for any download URL.
    private sealed class FakeGitHub : HttpMessageHandler
    {
        private readonly object[] _apiPages;
        private byte[]? _bundle;

        internal FakeGitHub(params object[] apiPages) => _apiPages = apiPages;

        internal List<RecordedRequest> Requests { get; } = [];

        internal List<string> RequestUris => Requests.ConvertAll(request => request.Uri);

        internal bool VersionedAssetsAvailable { get; init; } = true;

        internal string? ChecksumText { get; set; }

        internal byte[] Bundle => _bundle ??= CreateBundle(BundleVersion);

        internal string BundleVersion { get; set; } = "6.9.2";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uri = request.RequestUri!;
            Requests.Add(new RecordedRequest(
                uri.AbsoluteUri,
                request.Headers.UserAgent.ToString(),
                request.Headers.Accept.ToString(),
                request.Headers.Authorization is not null));

            if (uri.Host == "api.github.com")
            {
                var page = int.Parse(Regex.Match(uri.Query, @"[?&]page=(\d+)").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                var item = _apiPages[Math.Min(page, _apiPages.Length) - 1];
                return item is Exception exception ? Task.FromException<HttpResponseMessage>(exception) : Task.FromResult((HttpResponseMessage)item);
            }

            if (!VersionedAssetsAvailable && !uri.AbsolutePath.Contains("/latest/", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            var content = uri.AbsolutePath.EndsWith(".sha256", StringComparison.Ordinal)
                ? Encoding.UTF8.GetBytes(ChecksumText ?? $"{Convert.ToHexStringLower(SHA256.HashData(Bundle))}  Pointframe.Mcp-win-x64.mcpb")
                : Bundle;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) });
        }
    }
}
