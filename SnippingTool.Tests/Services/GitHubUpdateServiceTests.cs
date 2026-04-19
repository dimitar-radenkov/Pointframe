using System.Net;
using System.Net.Http;
using Pointframe.Services;
using Xunit;

namespace Pointframe.Tests.Services;

public sealed class GitHubUpdateServiceTests
{
    private static GitHubUpdateService CreateService(string responseJson, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        var handler = new FakeHttpMessageHandler(responseJson, statusCode);
        var http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(5),
            DefaultRequestHeaders = { { "User-Agent", "SnippingTool-Test" } },
        };
        return GitHubUpdateService.CreateForTesting(http);
    }

    [Fact]
    public async Task CurrentIsLatest_ReturnsNoUpdate()
    {
        var appVersion = new AppVersionService();
        var current = appVersion.Current;
        var json = $$"""
            {
              "tag_name": "v{{current.Major}}.{{current.Minor}}.{{current.Build}}",
              "assets": []
            }
            """;

        var result = await CreateService(json).CheckForUpdates();

        Assert.False(result.IsUpdateAvailable);
    }

    [Fact]
    public async Task NewerVersionAvailable_ReturnsUpdateWithDownloadUrl()
    {
        const string Json = """
            {
              "tag_name": "v999.0.0",
              "assets": [
                {
                  "name": "Pointframe-999.0.0-Setup.exe",
                  "browser_download_url": "https://github.com/dimitar-radenkov/Pointframe/releases/download/v999.0.0/Pointframe-999.0.0-Setup.exe"
                }
              ]
            }
            """;

        var result = await CreateService(Json).CheckForUpdates();

        Assert.True(result.IsUpdateAvailable);
        Assert.Equal(new Version(999, 0, 0), result.LatestVersion);
        Assert.Contains("Pointframe-999.0.0-Setup.exe", result.DownloadUrl);
    }

    [Fact]
    public async Task NewerVersionAvailable_PrefersSetupInstallerAsset()
    {
        const string Json = """
            {
              "tag_name": "v999.0.0",
              "assets": [
                {
                  "name": "Pointframe.exe",
                  "browser_download_url": "https://github.com/dimitar-radenkov/Pointframe/releases/download/v999.0.0/Pointframe.exe"
                },
                {
                  "name": "Pointframe-999.0.0-Setup.exe",
                  "browser_download_url": "https://github.com/dimitar-radenkov/Pointframe/releases/download/v999.0.0/Pointframe-999.0.0-Setup.exe"
                }
              ]
            }
            """;

        var result = await CreateService(Json).CheckForUpdates();

        Assert.Contains("Pointframe-999.0.0-Setup.exe", result.DownloadUrl);
    }

    [Fact]
    public async Task NewerVersionAvailable_PrefersPointframeInstallerOverLegacyInstaller()
    {
        const string Json = """
            {
              "tag_name": "v999.0.0",
              "assets": [
                {
                  "name": "SnippingTool-999.0.0-Setup.exe",
                  "browser_download_url": "https://github.com/dimitar-radenkov/Pointframe/releases/download/v999.0.0/SnippingTool-999.0.0-Setup.exe"
                },
                {
                  "name": "Pointframe-999.0.0-Setup.exe",
                  "browser_download_url": "https://github.com/dimitar-radenkov/Pointframe/releases/download/v999.0.0/Pointframe-999.0.0-Setup.exe"
                }
              ]
            }
            """;

        var result = await CreateService(Json).CheckForUpdates();

        Assert.Equal(
            "https://github.com/dimitar-radenkov/Pointframe/releases/download/v999.0.0/Pointframe-999.0.0-Setup.exe",
            result.DownloadUrl);
    }

    [Fact]
    public async Task InvalidTagFormat_ReturnsNoUpdate()
    {
        const string Json = """
            {
              "tag_name": "not-a-version",
              "assets": []
            }
            """;

        var result = await CreateService(Json).CheckForUpdates();

        Assert.False(result.IsUpdateAvailable);
    }

    [Fact]
    public async Task NoReleasesYet_404_ReturnsNoUpdate()
    {
        var result = await CreateService("{}", HttpStatusCode.NotFound).CheckForUpdates();

        Assert.False(result.IsUpdateAvailable);
        Assert.Empty(result.DownloadUrl);
    }

    [Fact]
    public async Task ServerError_500_Throws()
    {
        var service = CreateService("{}", HttpStatusCode.InternalServerError);

        await Assert.ThrowsAsync<HttpRequestException>(() => service.CheckForUpdates());
    }

    [Fact]
    public async Task UpdateAvailable_NoExeAsset_ReturnsEmptyDownloadUrl()
    {
        const string Json = """
            {
              "tag_name": "v999.0.0",
              "assets": [
                {
                  "name": "SnippingTool-999.0.0.zip",
                  "browser_download_url": "https://github.com/dimitar-radenkov/Pointframe/releases/download/v999.0.0/SnippingTool-999.0.0.zip"
                }
              ]
            }
            """;

        var result = await CreateService(Json).CheckForUpdates();

        Assert.True(result.IsUpdateAvailable);
        Assert.Empty(result.DownloadUrl);
    }

    [Fact]
    public async Task DisallowedDownloadUrl_Throws()
    {
        const string Json = """
            {
              "tag_name": "v999.0.0",
              "assets": [
                {
                  "name": "malicious.exe",
                  "browser_download_url": "https://evil.example.com/malicious.exe"
                }
              ]
            }
            """;

        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateService(Json).CheckForUpdates());
    }

    [Fact(DisplayName = "Tag without 'v' prefix is parsed correctly")]
    public async Task TagWithoutVPrefix_ParsedCorrectly()
    {
        const string Json = """
            {
              "tag_name": "999.0.0",
              "assets": []
            }
            """;

        var result = await CreateService(Json).CheckForUpdates();

        Assert.Equal(new Version(999, 0, 0), result.LatestVersion);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Integration_GitHubApi_NoReleasesYet_ReturnsNoUpdate()
    {
        // Requires network access and the public repo to exist.
        // Will return 404 (no releases) or a real release — either is valid.
        var service = new GitHubUpdateService(
            new AppVersionService(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<GitHubUpdateService>.Instance);

        var result = await service.CheckForUpdates();

        // Either state is acceptable; we just verify no exception is thrown
        // and the result is well-formed.
        Assert.NotNull(result);
        Assert.True(result.LatestVersion >= new Version(0, 0, 0));
    }

    private sealed class FakeHttpMessageHandler(string responseJson, HttpStatusCode statusCode = HttpStatusCode.OK) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseJson, System.Text.Encoding.UTF8, "application/json"),
            });
    }
}

