using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Pointframe.Cli;

internal sealed class McpReleaseAssetMissingException(string version, Uri assetUri)
    : HttpRequestException($"The Pointframe MCP bundle or checksum is missing from CLI release v{version}: {assetUri}", null, System.Net.HttpStatusCode.NotFound)
{
    internal const string Code = "mcp_release_asset_missing";
}

// Release builds download from the release of their own version and never call the API. Candidate and source builds find
// the newest agent release (GitHub Latest is a desktop release from the release trains on) through the public Releases
// API without a token, and fall back to the legacy latest/download aliases only when that call is rate limited or fails
// at the network level.
internal sealed partial class GitHubMcpPackageSource(HttpClient httpClient, string? cliVersion, bool isReleaseBuild, TextWriter? warningWriter = null) : IMcpPackageSource
{
    internal const int ReleasesPageSize = 100;
    internal const int MaxReleasePages = 10;

    private const string Repository = "https://github.com/dimitar-radenkov/Pointframe/releases";
    private const string ReleasesApi = "https://api.github.com/repos/dimitar-radenkov/Pointframe/releases";
    private const string BundleName = "Pointframe.Mcp-win-x64.mcpb";
    private readonly string? _releaseVersion = isReleaseBuild ? ReleaseVersion(cliVersion) : null;

    internal Uri BundleUri => AssetUri(BundleName);
    internal Uri ChecksumUri => AssetUri($"{BundleName}.sha256");

    public async Task<McpPackageDownload> DownloadLatestAsync(CancellationToken cancellationToken)
    {
        if (_releaseVersion is not null)
        {
            return await DownloadAsync(BundleUri, ChecksumUri, _releaseVersion, null, cancellationToken);
        }

        AgentRelease release;
        try
        {
            release = await FindNewestAgentReleaseAsync(cancellationToken);
        }
        catch (ReleaseListingUnavailableException unavailable)
        {
            var writer = warningWriter ?? Console.Error;
            await writer.WriteLineAsync($"Pointframe: warning: could not read the GitHub releases list ({unavailable.Message}); using the legacy latest-release MCP bundle instead.");
            return await DownloadAsync(BundleUri, ChecksumUri, null, null, cancellationToken);
        }

        var bundleName = $"Pointframe.Mcp-{release.Version}-win-x64.mcpb";
        var baseUri = $"{Repository}/download/{release.Tag}";
        return await DownloadAsync(new Uri($"{baseUri}/{bundleName}"), new Uri($"{baseUri}/{bundleName}.sha256"), release.Version, release.Version, cancellationToken);
    }

    private async Task<McpPackageDownload> DownloadAsync(Uri bundleUri, Uri checksumUri, string? missingAssetVersion, string? resolvedVersion, CancellationToken cancellationToken)
    {
        using var bundleResponse = await httpClient.GetAsync(bundleUri, cancellationToken);
        if (bundleResponse.StatusCode == HttpStatusCode.NotFound && missingAssetVersion is not null)
        {
            throw new McpReleaseAssetMissingException(missingAssetVersion, bundleUri);
        }

        bundleResponse.EnsureSuccessStatusCode();
        var bundle = await bundleResponse.Content.ReadAsByteArrayAsync(cancellationToken);

        using var checksumResponse = await httpClient.GetAsync(checksumUri, cancellationToken);
        if (checksumResponse.StatusCode == HttpStatusCode.NotFound && missingAssetVersion is not null)
        {
            throw new McpReleaseAssetMissingException(missingAssetVersion, checksumUri);
        }

        checksumResponse.EnsureSuccessStatusCode();
        var checksum = await checksumResponse.Content.ReadAsStringAsync(cancellationToken);
        return new McpPackageDownload(bundle, checksum, resolvedVersion);
    }

    // Stops at the first page that has a complete agent release or at a short page; the cap is hard.
    private async Task<AgentRelease> FindNewestAgentReleaseAsync(CancellationToken cancellationToken)
    {
        for (var page = 1; page <= MaxReleasePages; page++)
        {
            var json = await ReadReleasesPageAsync(page, cancellationToken);
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(json);
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException($"The GitHub releases list (page {page}) is not valid JSON: {exception.Message}");
            }

            using (document)
            {
                if (document.RootElement.ValueKind != JsonValueKind.Array)
                {
                    throw new InvalidDataException($"The GitHub releases list (page {page}) is not a JSON array.");
                }

                var newest = document.RootElement.EnumerateArray()
                    .Select(ToCompleteAgentRelease)
                    .OfType<AgentRelease>()
                    .OrderByDescending(release => release.SortKey)
                    .FirstOrDefault();
                if (newest is not null)
                {
                    return newest;
                }

                if (document.RootElement.GetArrayLength() < ReleasesPageSize)
                {
                    throw new InvalidOperationException("No published Pointframe release has the CLI zip, its checksum and the MCP bundle; install an explicit version or try again later.");
                }
            }
        }

        throw new InvalidOperationException($"No complete agent release was found in the newest {MaxReleasePages * ReleasesPageSize} releases (page limit {MaxReleasePages}); install an explicit version.");
    }

    private async Task<string> ReadReleasesPageAsync(int page, CancellationToken cancellationToken)
    {
        HttpStatusCode status;
        string? json;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{ReleasesApi}?per_page={ReleasesPageSize}&page={page}");
            request.Headers.UserAgent.ParseAdd("Pointframe-CLI");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await httpClient.SendAsync(request, cancellationToken);
            status = response.StatusCode;
            json = response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(cancellationToken) : null;
        }
        catch (Exception exception) when ((exception is HttpRequestException or IOException or TaskCanceledException) && !cancellationToken.IsCancellationRequested)
        {
            throw new ReleaseListingUnavailableException($"network error: {exception.Message}");
        }

        if (status is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
        {
            throw new ReleaseListingUnavailableException($"rate limited, HTTP {(int)status}");
        }

        return json ?? throw new HttpRequestException($"The GitHub releases API answered HTTP {(int)status}.", null, status);
    }

    // The agent release rule of scripts/release-model.ps1: published, strict numeric v<x.y.z> tag, CLI zip + checksum + MCPB.
    private static AgentRelease? ToCompleteAgentRelease(JsonElement release)
    {
        if (release.ValueKind != JsonValueKind.Object
            || IsTrue(release, "draft")
            || IsTrue(release, "prerelease")
            || string.IsNullOrWhiteSpace(StringProperty(release, "published_at"))
            || StringProperty(release, "tag_name") is not { } tag
            || StrictTagPattern().Match(tag) is not { Success: true } match)
        {
            return null;
        }

        var sortKey = new Version(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value), int.Parse(match.Groups[3].Value));
        var version = sortKey.ToString();
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var names = assets.EnumerateArray().Select(asset => StringProperty(asset, "name")).OfType<string>().ToHashSet(StringComparer.Ordinal);
        string[] required = [$"Pointframe.Cli-{version}-win-x64.zip", $"Pointframe.Cli-{version}-win-x64.zip.sha256", $"Pointframe.Mcp-{version}-win-x64.mcpb"];
        return required.All(names.Contains) ? new AgentRelease(tag, version, sortKey) : null;
    }

    private static bool IsTrue(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static string? StringProperty(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    [GeneratedRegex(@"^v([0-9]{1,9})\.([0-9]{1,9})\.([0-9]{1,9})$", RegexOptions.CultureInvariant)]
    private static partial Regex StrictTagPattern();

    private Uri AssetUri(string assetName) => _releaseVersion is { } version
        ? new Uri($"{Repository}/download/v{version}/{assetName}")
        : new Uri($"{Repository}/latest/download/{assetName}");

    private static string? ReleaseVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        var core = version.Split('+')[0];
        if (core == "0.0.0" || core.Contains('-', StringComparison.Ordinal))
        {
            return null;
        }

        var parts = core.Split('.');
        return parts.Length == 3 && parts.All(part => int.TryParse(part, out _)) ? core : null;
    }

    private sealed record AgentRelease(string Tag, string Version, Version SortKey);

    private sealed class ReleaseListingUnavailableException(string reason) : Exception(reason);
}
