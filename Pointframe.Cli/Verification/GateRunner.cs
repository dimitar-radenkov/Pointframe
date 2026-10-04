using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Pointframe.Cli;

internal sealed record CommandResult(int ExitCode, bool TimedOut, IReadOnlyList<string> Lines);

internal interface ICommandRunner
{
    Task<CommandResult> RunAsync(string command, string workingDirectory, TimeSpan timeout, string logPath, CancellationToken cancellationToken);
}

// Runs a gate's command line through cmd.exe, so a spec can use any tool on the PATH (dotnet, npm, pwsh)
// without the CLI knowing about it. Output goes to one log per gate.
internal sealed class ShellCommandRunner : ICommandRunner
{
    public async Task<CommandResult> RunAsync(string command, string workingDirectory, TimeSpan timeout, string logPath, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe",
            Arguments = $"/d /s /c \"{command}\"",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        var lines = new List<string>();
        using var process = new Process { StartInfo = startInfo };
        process.OutputDataReceived += (_, line) => Remember(lines, line.Data);
        process.ErrorDataReceived += (_, line) => Remember(lines, line.Data);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = !cancellationToken.IsCancellationRequested;
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            if (!timedOut)
            {
                throw;
            }
        }

        string[] snapshot;
        lock (lines)
        {
            snapshot = [.. lines];
        }

        await File.WriteAllLinesAsync(logPath, snapshot, CancellationToken.None);
        return new CommandResult(timedOut ? -1 : process.ExitCode, timedOut, snapshot);
    }

    private static void Remember(List<string> lines, string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (lines)
        {
            lines.Add(line);
        }
    }
}

internal sealed record VerificationGateResult(
    string Id,
    string Run,
    string Status,
    int? ExitCode,
    double Seconds,
    string? Log,
    IReadOnlyList<string> Details);

internal static partial class GateRunner
{
    private const int MaxDetailLines = 20;

    // Every gate runs, in order, even after a failure, so one run reports every problem; scenarios are
    // skipped when any gate failed, the way verify.ps1 skips tests after a failed build.
    internal static async Task<IReadOnlyList<VerificationGateResult>> RunAsync(
        IReadOnlyList<VerificationGate> gates,
        ICommandRunner runner,
        string logDirectory,
        TextWriter progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(logDirectory);
        var results = new List<VerificationGateResult>();
        foreach (var gate in gates)
        {
            await progress.WriteLineAsync($"verify: gate {gate.Id} ...");
            var logPath = Path.Combine(logDirectory, $"{gate.Id}.log");
            var stopwatch = Stopwatch.StartNew();
            VerificationGateResult result;
            try
            {
                var run = await runner.RunAsync(gate.Run, gate.WorkingDirectory, TimeSpan.FromMinutes(gate.TimeoutMinutes), logPath, cancellationToken);
                var seconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 1);
                result = run.TimedOut
                    ? new VerificationGateResult(gate.Id, gate.Run, VerificationStatus.Fail, null, seconds, logPath,
                        [$"Timed out after {gate.TimeoutMinutes} minute(s).", .. Tail(run.Lines)])
                    : run.ExitCode == 0
                        ? new VerificationGateResult(gate.Id, gate.Run, VerificationStatus.Pass, 0, seconds, logPath, [])
                        : new VerificationGateResult(gate.Id, gate.Run, VerificationStatus.Fail, run.ExitCode, seconds, logPath, FailureDetails(run.Lines));
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                result = new VerificationGateResult(
                    gate.Id, gate.Run, VerificationStatus.Fail, null, Math.Round(stopwatch.Elapsed.TotalSeconds, 1), null,
                    [$"The gate could not be started: {exception.Message}"]);
            }

            results.Add(result);
        }

        return results;
    }

    // Compiler errors, failed tests, and ERROR lines when there are any; otherwise the end of the log.
    internal static IReadOnlyList<string> FailureDetails(IReadOnlyList<string> lines)
    {
        var errors = lines.Where(line => ErrorLine().IsMatch(line)).Select(line => line.Trim()).Distinct(StringComparer.Ordinal).Take(MaxDetailLines).ToArray();
        return errors.Length > 0 ? errors : Tail(lines);
    }

    private static IReadOnlyList<string> Tail(IReadOnlyList<string> lines) =>
        lines.Where(line => !string.IsNullOrWhiteSpace(line)).TakeLast(15).ToArray();

    [GeneratedRegex(@"(:\s*error\s+[A-Z]*\d*\s*:)|(^\s*ERROR\b)|(^\s*Failed\s+\S+\s*\[)", RegexOptions.CultureInvariant)]
    private static partial Regex ErrorLine();
}
