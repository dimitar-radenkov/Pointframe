namespace Pointframe.Cli;

internal interface IMcpPackageInstaller
{
    McpInstallation? GetCurrent();

    Task<McpInstallation> InstallLatestAsync(bool dryRun, CancellationToken cancellationToken);
}
