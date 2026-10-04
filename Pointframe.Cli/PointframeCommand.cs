using System.Diagnostics;

namespace Pointframe.Cli;

internal sealed record PointframeCommandInfo(string? Path, string? Version, bool Ok, string? Error = null);

internal interface IPointframeCommandResolver
{
    PointframeCommandInfo Resolve(string? path = null);
}

internal sealed record PointframeCommandProbeResult(int? ExitCode, string StandardOutput, string StandardError, bool TimedOut = false, string? Error = null);

internal interface IPointframeCommandProbe
{
    string? Find(string? path);
    PointframeCommandProbeResult RunVersion(string executable, TimeSpan timeout);
}

internal sealed class PhysicalPointframeCommandProbe : IPointframeCommandProbe
{
    public string? Find(string? path) => PointframeCommandResolver.FindOnPath(path);

    public PointframeCommandProbeResult RunVersion(string executable, TimeSpan timeout)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(executable, "--version")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });
            if (process is null)
            {
                return new PointframeCommandProbeResult(null, string.Empty, string.Empty, Error: "The command did not start.");
            }

            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeoutSource = new CancellationTokenSource(timeout);
            try
            {
                process.WaitForExitAsync(timeoutSource.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                return new PointframeCommandProbeResult(null, string.Empty, string.Empty, TimedOut: true);
            }

            var version = stdout.GetAwaiter().GetResult().Trim();
            var error = stderr.GetAwaiter().GetResult().Trim();
            return new PointframeCommandProbeResult(process.ExitCode, version, error);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return new PointframeCommandProbeResult(null, string.Empty, string.Empty, Error: exception.Message);
        }
    }
}

internal sealed class PointframeCommandResolver(IPointframeCommandProbe? probe = null) : IPointframeCommandResolver
{
    public PointframeCommandInfo Resolve(string? path = null)
    {
        var commandProbe = probe ?? new PhysicalPointframeCommandProbe();
        var resolved = commandProbe.Find(path ?? Environment.GetEnvironmentVariable("PATH"));
        if (resolved is null)
        {
            return new PointframeCommandInfo(null, null, false, "pointframe.exe was not found on PATH.");
        }

        var result = commandProbe.RunVersion(resolved, TimeSpan.FromSeconds(10));
        if (result.TimedOut)
        {
            return new PointframeCommandInfo(resolved, null, false, "--version timed out after 10 seconds.");
        }

        if (result.Error is not null)
        {
            return new PointframeCommandInfo(resolved, null, false, result.Error);
        }

        var version = result.StandardOutput.Trim();
        var ok = result.ExitCode == 0 && version.StartsWith("Pointframe CLI", StringComparison.Ordinal);
        return new PointframeCommandInfo(resolved, ok ? version : null, ok,
            ok ? null : result.ExitCode != 0 ? $"--version exited {result.ExitCode}: {result.StandardError.Trim()}" : $"Unexpected --version output: {version}");
    }

    internal static string? FindOnPath(string? path)
    {
        foreach (var directory in (path ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory.Trim().Trim('"'), "pointframe.exe");
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }
}
