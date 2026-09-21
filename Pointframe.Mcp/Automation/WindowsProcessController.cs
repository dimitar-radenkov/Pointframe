using System.Diagnostics;
using System.Security.Cryptography;
using Pointframe.Engine.Automation.Models;
using Pointframe.Engine.Automation.Services;

namespace Pointframe.Mcp.Automation;

public sealed class WindowsProcessController : IDesktopProcessController
{
    private readonly Dictionary<string, Process> _processes = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<DesktopProcessIdentity> LaunchAsync(
        DesktopLaunchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var executablePath = Path.GetFullPath(request.ExecutablePath);
        if (!File.Exists(executablePath))
        {
            throw new FileNotFoundException("The executable was not found.", executablePath);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetFullPath(request.WorkingDirectory),
            UseShellExecute = false,

            // The server speaks MCP over its own stdin/stdout. With UseShellExecute false and no
            // redirection the launched application inherits those handles, so anything it writes to
            // standard output lands in the middle of the JSON-RPC stream and anything it reads steals
            // the client's requests. A target that prints nothing (Notepad++) appears to work; a .NET
            // or WPF target hung desktop_start_test_session outright until this was added.
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // Deliberately not disposed here: disposing the Process closes the redirected pipes while the
        // target is still running, and the target then blocks or faults on its next write. The
        // instance is retained below for the lifetime of the session instead.
        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The target process could not be started.");

        // Drain both pipes. An unread pipe fills its buffer and then blocks the target on its next
        // write, which looks exactly like the application hanging for no reason.
        process.OutputDataReceived += static (_, _) => { };
        process.ErrorDataReceived += static (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.StandardInput.Close();
        var processRef = $"process-{Guid.NewGuid():N}";
        var identity = await CaptureIdentityAsync(process, processRef, executablePath, cancellationToken).ConfigureAwait(false);
        // Retain the started instance itself rather than re-opening the pid: it owns the redirected
        // pipes, and those have to outlive this method.
        var retainedProcess = process;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _processes.Add(processRef, retainedProcess);
        }
        finally
        {
            _gate.Release();
        }

        return identity;
    }

    public async Task<DesktopTargetState> GetStateAsync(
        DesktopProcessIdentity process,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(process);
        cancellationToken.ThrowIfCancellationRequested();

        Process? retainedProcess;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _processes.TryGetValue(process.ProcessRef, out retainedProcess);
        }
        finally
        {
            _gate.Release();
        }

        if (retainedProcess is null)
        {
            return DesktopTargetState.Unavailable;
        }

        try
        {
            if (retainedProcess.HasExited)
            {
                return DesktopTargetState.Exited;
            }

            var actualPath = retainedProcess.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(actualPath)
                || !string.Equals(Path.GetFullPath(actualPath), Path.GetFullPath(process.ExecutablePath), StringComparison.OrdinalIgnoreCase))
            {
                return DesktopTargetState.Unavailable;
            }

            var actualStartedUtc = retainedProcess.StartTime.ToUniversalTime();
            if (actualStartedUtc != process.StartedUtc)
            {
                return DesktopTargetState.Unavailable;
            }

            var actualHash = await ComputeSha256Async(actualPath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(actualHash, process.ExecutableSha256, StringComparison.OrdinalIgnoreCase))
            {
                return DesktopTargetState.Unavailable;
            }

            return DesktopTargetState.Running;
        }
        catch (InvalidOperationException)
        {
            return DesktopTargetState.Unavailable;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return DesktopTargetState.Unavailable;
        }
    }

    public async ValueTask ReleaseAsync(
        DesktopProcessIdentity process,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(process);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_processes.Remove(process.ProcessRef, out var retainedProcess))
            {
                retainedProcess.Dispose();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<DesktopProcessIdentity> CaptureIdentityAsync(
        Process process,
        string processRef,
        string executablePath,
        CancellationToken cancellationToken)
    {
        var startedUtc = process.StartTime.ToUniversalTime();
        var hash = await ComputeSha256Async(executablePath, cancellationToken).ConfigureAwait(false);
        return new DesktopProcessIdentity(
            processRef,
            process.Id,
            startedUtc,
            executablePath,
            hash);
    }

    private static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }
}
