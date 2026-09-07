using System.Diagnostics;

namespace Pointframe.AutomationTests.Support;

public sealed class DesktopDriverFixtureHost : IAsyncDisposable
{
    private readonly Process _process;

    private DesktopDriverFixtureHost(Process process)
    {
        _process = process;
    }

    public int ProcessId => _process.Id;

    public static Task<DesktopDriverFixtureHost> StartAsync(
        string executablePath,
        string? workingDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !Path.IsPathFullyQualified(executablePath))
        {
            throw new ArgumentException("The fixture executable path must be absolute.", nameof(executablePath));
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = Path.GetFullPath(executablePath),
            WorkingDirectory = workingDirectory is null
                ? Path.GetDirectoryName(executablePath)!
                : Path.GetFullPath(workingDirectory),
            UseShellExecute = true,
        };

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The desktop fixture could not be started.");
        return Task.FromResult(new DesktopDriverFixtureHost(process));
    }

    public async ValueTask DisposeAsync()
    {
        if (_process.HasExited)
        {
            _process.Dispose();
            return;
        }

        try
        {
            _process.CloseMainWindow();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch
            {
            }
        }
        finally
        {
            _process.Dispose();
        }
    }
}
