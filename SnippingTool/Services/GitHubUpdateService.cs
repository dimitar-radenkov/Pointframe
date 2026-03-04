using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using SnippingTool.Models;

namespace SnippingTool.Services;

public sealed class GitHubUpdateService : IUpdateService
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(15),
        DefaultRequestHeaders = { { "User-Agent", "SnippingTool" } },
    };

    private const string LatestReleaseUrl =
        "https://api.github.com/repos/dimitar-radenkov/SnippingTool/releases/latest";

    private static readonly string[] AllowedDownloadHosts =
    [
        "https://github.com/",
        "https://objects.githubusercontent.com/",
    ];

    private readonly HttpClient _http;
    private readonly ILogger<GitHubUpdateService>? _logger;

    public GitHubUpdateService(ILogger<GitHubUpdateService> logger) : this(Http, logger) { }

    private GitHubUpdateService(HttpClient http, ILogger<GitHubUpdateService>? logger)
    {
        _http = http;
        _logger = logger;
    }

    public static GitHubUpdateService CreateForTesting(HttpClient http) => new(http, null);

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        _logger?.LogInformation("Checking for updates at {Url}", LatestReleaseUrl);

        var response = await _http.GetAsync(LatestReleaseUrl, cancellationToken);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            _logger?.LogInformation("No releases found on GitHub (404) — assuming up to date");
            var current = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);
            return new UpdateCheckResult(false, current, string.Empty);
        }

        response.EnsureSuccessStatusCode();

        var release = await response.Content.ReadFromJsonAsync<GitHubRelease>(cancellationToken)
            ?? throw new InvalidOperationException("Empty response from GitHub API.");

        var latestVersion = ParseVersion(release.TagName);
        var currentVersion = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);

        _logger?.LogDebug("Current version: {Current}, latest: {Latest}", currentVersion, latestVersion);

        if (latestVersion <= currentVersion)
        {
            _logger?.LogInformation("Already up to date (v{Version})", currentVersion);
            return new UpdateCheckResult(false, latestVersion, string.Empty);
        }

        var downloadUrl = release.Assets
            .FirstOrDefault(a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            ?.BrowserDownloadUrl ?? string.Empty;

        if (!string.IsNullOrEmpty(downloadUrl) && !IsAllowedDownloadUrl(downloadUrl))
        {
            throw new InvalidOperationException($"Unexpected download URL host: {downloadUrl}");
        }

        _logger?.LogInformation("Update available: v{Version}, download URL: {Url}", latestVersion, downloadUrl);
        return new UpdateCheckResult(true, latestVersion, downloadUrl);
    }

    private static Version ParseVersion(string tag)
    {
        var clean = tag.TrimStart('v', 'V');
        return Version.TryParse(clean, out var v) ? v : new Version(0, 0, 0);
    }

    private static bool IsAllowedDownloadUrl(string url) =>
        AllowedDownloadHosts.Any(h => url.StartsWith(h, StringComparison.OrdinalIgnoreCase));

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string TagName { get; init; } = string.Empty;
        [JsonPropertyName("assets")] public List<GitHubAsset> Assets { get; init; } = [];
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
        [JsonPropertyName("browser_download_url")] public string BrowserDownloadUrl { get; init; } = string.Empty;
    }
}
