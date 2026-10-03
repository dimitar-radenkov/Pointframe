namespace Pointframe.Cli;

internal interface IMcpPackageSource
{
    Task<McpPackageDownload> DownloadLatestAsync(CancellationToken cancellationToken);
}
