using System.Net;
using System.Security.Cryptography;
using Pointframe.Cli;
using Xunit;

namespace Pointframe.Tests.Cli;

public sealed class McpPackageSourceTests
{
    private const string BundleName = "Pointframe.Mcp-win-x64.mcpb";

    [Fact]
    public void CliApplicationFactory_UsesInjectedReleaseIdentityAndHandler()
    {
        var handler = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
        var source = CliApplication.CreateMcpPackageSource("6.7.123+abc123", true, handler);

        Assert.Equal($"https://github.com/dimitar-radenkov/Pointframe/releases/download/v6.7.123/{BundleName}", source.BundleUri.AbsoluteUri);
        Assert.Equal($"https://github.com/dimitar-radenkov/Pointframe/releases/download/v6.7.123/{BundleName}.sha256", source.ChecksumUri.AbsoluteUri);
    }

    [Theory]
    [InlineData("6.7.124", true, "v6.7.124")]
    [InlineData("6.7.124+abc123", true, "v6.7.124")]
    [InlineData("6.7.124", false, null)]
    public void Constructor_ReleaseVersion_UsesExpectedAssetUris(string version, bool marker, string? release)
    {
        using var client = new HttpClient(new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var source = new GitHubMcpPackageSource(client, version, marker);

        var expected = release is null
            ? $"https://github.com/dimitar-radenkov/Pointframe/releases/latest/download/{BundleName}"
            : $"https://github.com/dimitar-radenkov/Pointframe/releases/download/{release}/{BundleName}";
        Assert.Equal(expected, source.BundleUri.AbsoluteUri);
        Assert.Equal($"{source.BundleUri.AbsoluteUri}.sha256", source.ChecksumUri.AbsoluteUri);
    }

    [Theory]
    [InlineData("6.7.124-preview.1")]
    [InlineData("0.0.0")]
    [InlineData(null)]
    [InlineData("6.7.124.1")]
    [InlineData("v6.7.124")]
    public void Constructor_NonReleaseVersion_UsesLatestAliases(string? version)
    {
        using var client = new HttpClient(new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var source = new GitHubMcpPackageSource(client, version, true);

        Assert.Equal($"https://github.com/dimitar-radenkov/Pointframe/releases/latest/download/{BundleName}", source.BundleUri.AbsoluteUri);
        Assert.Equal($"{source.BundleUri.AbsoluteUri}.sha256", source.ChecksumUri.AbsoluteUri);
    }

    [Fact]
    public async Task DownloadLatestAsync_VersionedBundleMissing_ThrowsStableErrorWithoutInstalling()
    {
        var root = Path.Combine(Path.GetTempPath(), "Pointframe.McpPackageSourceTests", Guid.NewGuid().ToString("N"));
        var handler = new StubHandler((request, _) => new HttpResponseMessage(request.RequestUri!.AbsolutePath.Contains("/latest/", StringComparison.Ordinal) ? HttpStatusCode.OK : HttpStatusCode.NotFound));
        using var client = new HttpClient(handler);
        var installer = new McpPackageInstaller(new GitHubMcpPackageSource(client, "6.7.123", true), root);

        var error = await Assert.ThrowsAsync<McpReleaseAssetMissingException>(() => installer.InstallLatestAsync(false, CancellationToken.None));

        Assert.Equal("mcp_release_asset_missing", McpReleaseAssetMissingException.Code);
        Assert.Contains("v6.7.123", error.Message);
        Assert.False(Directory.Exists(root));
        Assert.Equal(["/dimitar-radenkov/Pointframe/releases/download/v6.7.123/Pointframe.Mcp-win-x64.mcpb"], handler.Requests.Select(uri => uri.AbsolutePath));
    }

    [Fact]
    public async Task DownloadLatestAsync_VersionedChecksumMissing_ThrowsWithoutLatestFallback()
    {
        var handler = new StubHandler((request, _) => new HttpResponseMessage(request.RequestUri!.AbsolutePath.EndsWith(".sha256", StringComparison.Ordinal) ? HttpStatusCode.NotFound : HttpStatusCode.OK) { Content = new ByteArrayContent([1]) });
        using var client = new HttpClient(handler);
        var source = new GitHubMcpPackageSource(client, "6.7.123", true);

        var error = await Assert.ThrowsAsync<McpReleaseAssetMissingException>(() => source.DownloadLatestAsync(CancellationToken.None));

        Assert.Contains("v6.7.123", error.Message);
        Assert.Equal(["/dimitar-radenkov/Pointframe/releases/download/v6.7.123/Pointframe.Mcp-win-x64.mcpb", "/dimitar-radenkov/Pointframe/releases/download/v6.7.123/Pointframe.Mcp-win-x64.mcpb.sha256"], handler.Requests.Select(uri => uri.AbsolutePath));
    }

    [Fact]
    public async Task DownloadLatestAsync_ValidChecksum_IsReturnedForInstallerVerification()
    {
        byte[] bundle = [1, 2, 3, 4];
        var checksum = Convert.ToHexStringLower(SHA256.HashData(bundle));
        var handler = new StubHandler((request, _) =>
        {
            var content = request.RequestUri!.AbsolutePath.EndsWith(".sha256", StringComparison.Ordinal)
                ? checksum
                : string.Empty;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(request.RequestUri.AbsolutePath.EndsWith(".sha256", StringComparison.Ordinal)
                    ? System.Text.Encoding.UTF8.GetBytes(content)
                    : bundle),
            };
        });
        using var client = new HttpClient(handler);
        var source = new GitHubMcpPackageSource(client, "6.7.124", true);

        var downloaded = await source.DownloadLatestAsync(CancellationToken.None);

        Assert.Equal(bundle, downloaded.Bundle);
        Assert.Equal(checksum, downloaded.ChecksumText);
        Assert.Equal([source.BundleUri, source.ChecksumUri], handler.Requests);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> response) : HttpMessageHandler
    {
        internal List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            AddAndRespond(request, cancellationToken);

        private Task<HttpResponseMessage> AddAndRespond(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(response(request, cancellationToken));
        }
    }
}
