using System.Diagnostics;

namespace Pointframe.Cli;

internal interface IMcpServerHost
{
    // Runs the server with this process's own stdin, stdout, and stderr, so the client that started the CLI
    // talks to the server directly, and returns the server's exit code.
    Task<int> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken);
}

internal sealed class InheritedStdioMcpServerHost : IMcpServerHost
{
    public async Task<int> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetDirectoryName(executablePath)!,
            UseShellExecute = false,
            // Not CreateNoWindow: that gives the child a new hidden console, and with no redirection it would
            // read and write that console instead of the pipes this process was started with.
            CreateNoWindow = false,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in environment)
        {
            startInfo.Environment[name] = value;
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The Pointframe MCP server could not be started.");
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            return 1;
        }
    }
}
