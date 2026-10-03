namespace Pointframe.Cli;

internal interface IMcpHealthChecker
{
    Task<McpHealthResult> CheckAsync(string executablePath, CancellationToken cancellationToken);
}
