namespace Pointframe.Cli;

internal sealed class GitHubMcpPackageSource(HttpClient httpClient) : IMcpPackageSource
{
    private static readonly Uri BundleUri = new("https://github.com/dimitar-radenkov/Pointframe/releases/latest/download/Pointframe.Mcp-win-x64.mcpb");
    private static readonly Uri ChecksumUri = new("https://github.com/dimitar-radenkov/Pointframe/releases/latest/download/Pointframe.Mcp-win-x64.mcpb.sha256");

    public async Task<McpPackageDownload> DownloadLatestAsync(CancellationToken cancellationToken)
    {
        var bundleTask = httpClient.GetByteArrayAsync(BundleUri, cancellationToken);
        var checksumTask = httpClient.GetStringAsync(ChecksumUri, cancellationToken);
        await Task.WhenAll(bundleTask, checksumTask);
        return new McpPackageDownload(await bundleTask, await checksumTask);
    }
}
