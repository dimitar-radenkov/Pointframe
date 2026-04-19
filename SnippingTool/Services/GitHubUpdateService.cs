using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Pointframe.Models;

namespace Pointframe.Services;

public sealed class GitHubUpdateService : IUpdateService
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(15),
        DefaultRequestHeaders = { { "User-Agent", "Pointframe" } },
    };

    private const string LatestReleaseUrl =
        "https://api.github.com/repos/dimitar-radenkov/Pointframe/releases/latest";

    private static readonly string[] AllowedDownloadHosts =
    [
        "https://github.com/",
        "https://objects.githubusercontent.com/",
    ];

    private readonly HttpClient _http;
    private readonly ILogger<GitHubUpdateService>? _logger;
    private readonly IAppVersionService _appVersion;

    public GitHubUpdateService(IAppVersionService appVersion, ILogger<GitHubUpdateService> logger) : this(Http, appVersion, logger) { }

    private GitHubUpdateService(
        HttpClient http,
        IAppVersionService appVersion,
        ILogger<GitHubUpdateService>? logger)
    {
        _http = http;
        _appVersion = appVersion;
        _logger = logger;
    }

    public static GitHubUpdateService CreateForTesting(HttpClient http) => new(http, new AppVersionService(), null);

    public async Task<UpdateCheckResult> CheckForUpdates(CancellationToken cancellationToken = default)
    {
        _logger?.LogInformation("Checking for updates at {Url}", LatestReleaseUrl);

        var response = await _http.GetAsync(LatestReleaseUrl, cancellationToken);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            _logger?.LogInformation("No releases found on GitHub (404) — assuming up to date");
            return new UpdateCheckResult(false, _appVersion.Current, string.Empty);
        }

        response.EnsureSuccessStatusCode();

        var release = await response.Content.ReadFromJsonAsync<GitHubRelease>(cancellationToken)
            ?? throw new InvalidOperationException("Empty response from GitHub API.");

        var latestVersion = ParseVersion(release.TagName);
        var currentVersion = _appVersion.Current;

        _logger?.LogDebug("Current version: {Current}, latest: {Latest}", currentVersion, latestVersion);

        if (latestVersion <= currentVersion)
        {
            _logger?.LogInformation("Already up to date (v{Version})", currentVersion);
            return new UpdateCheckResult(false, latestVersion, string.Empty);
        }

        var downloadUrl = release.Assets
            .Where(a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(a => GetInstallerAssetPriority(a.Name))
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .Select(a => a.BrowserDownloadUrl)
            .FirstOrDefault(url => !string.IsNullOrEmpty(url)) ?? string.Empty;

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

    private static int GetInstallerAssetPriority(string assetName)
    {
        if (assetName.StartsWith("Pointframe-", StringComparison.OrdinalIgnoreCase)
            && assetName.EndsWith("-Setup.exe", StringComparison.OrdinalIgnoreCase))
        {
            return 3;
        }

        if (assetName.StartsWith("SnippingTool-", StringComparison.OrdinalIgnoreCase)
            && assetName.EndsWith("-Setup.exe", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        if (assetName.EndsWith("-Setup.exe", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        return 0;
    }

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
