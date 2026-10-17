namespace Pointframe.Cli;

internal sealed class McpReleaseAssetMissingException(string version, Uri assetUri)
    : HttpRequestException($"The Pointframe MCP bundle or checksum is missing from CLI release v{version}: {assetUri}", null, System.Net.HttpStatusCode.NotFound)
{
    internal const string Code = "mcp_release_asset_missing";
}

internal sealed class GitHubMcpPackageSource(HttpClient httpClient, string? cliVersion, bool isReleaseBuild) : IMcpPackageSource
{
    private const string Repository = "https://github.com/dimitar-radenkov/Pointframe/releases";
    private const string BundleName = "Pointframe.Mcp-win-x64.mcpb";
    private readonly string? _releaseVersion = isReleaseBuild ? ReleaseVersion(cliVersion) : null;

    internal Uri BundleUri => AssetUri(BundleName);
    internal Uri ChecksumUri => AssetUri($"{BundleName}.sha256");

    public async Task<McpPackageDownload> DownloadLatestAsync(CancellationToken cancellationToken)
    {
        using var bundleResponse = await httpClient.GetAsync(BundleUri, cancellationToken);
        if (bundleResponse.StatusCode == System.Net.HttpStatusCode.NotFound && _releaseVersion is not null)
        {
            throw new McpReleaseAssetMissingException(_releaseVersion, BundleUri);
        }

        bundleResponse.EnsureSuccessStatusCode();
        var bundle = await bundleResponse.Content.ReadAsByteArrayAsync(cancellationToken);

        using var checksumResponse = await httpClient.GetAsync(ChecksumUri, cancellationToken);
        if (checksumResponse.StatusCode == System.Net.HttpStatusCode.NotFound && _releaseVersion is not null)
        {
            throw new McpReleaseAssetMissingException(_releaseVersion, ChecksumUri);
        }

        checksumResponse.EnsureSuccessStatusCode();
        var checksum = await checksumResponse.Content.ReadAsStringAsync(cancellationToken);
        return new McpPackageDownload(bundle, checksum);
    }

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
}
