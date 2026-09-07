using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;

namespace Pointframe.Mcp.Automation;

public interface IDesktopAutomationWorkerProvider
{
    Task<DesktopAutomationWorkerResponse> HandleAsync(
        DesktopAutomationWorkerRequest request,
        CancellationToken cancellationToken);
}

public sealed class DesktopAutomationWorkerHost : IAsyncDisposable
{
    private readonly string _executablePath;
    private readonly string _assemblyPath;
    private NamedPipeServerStream? _pipe;
    private Process? _worker;
    private bool _started;

    public DesktopAutomationWorkerHost(string executablePath, string assemblyPath)
    {
        _executablePath = Path.GetFullPath(executablePath);
        _assemblyPath = Path.GetFullPath(assemblyPath);
    }

    public int? WorkerProcessId => _worker?.Id;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_started)
        {
            throw new InvalidOperationException("The worker is already running.");
        }

        var pipeName = $"pointframe-desktop-{Guid.NewGuid():N}";
        _pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        _worker = Process.Start(CreateStartInfo(pipeName))
            ?? throw new InvalidOperationException("The worker process could not be started.");
        await _pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
        VerifyWorkerIdentity(_worker);
        var hello = await ReadAsync<DesktopAutomationWorkerHello>(cancellationToken).ConfigureAwait(false);
        if (hello.ProtocolVersion != DesktopAutomationWorkerProtocol.Version
            || hello.WorkerProcessId != _worker.Id
            || hello.WindowsSessionId != Process.GetCurrentProcess().SessionId)
        {
            throw new InvalidDataException("The worker identity could not be verified.");
        }

        _started = true;
    }

    public async Task<DesktopAutomationWorkerResponse> DispatchAsync(
        DesktopAutomationWorkerRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_started || _pipe is null)
        {
            throw new InvalidOperationException("The worker is not running.");
        }

        await WriteAsync(request, cancellationToken).ConfigureAwait(false);
        var response = await ReadAsync<DesktopAutomationWorkerResponse>(cancellationToken).ConfigureAwait(false);
        if (response.ProtocolVersion != DesktopAutomationWorkerProtocol.Version
            || !string.Equals(response.RequestId, request.RequestId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The worker returned an unexpected response.");
        }

        return response;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_worker is null)
        {
            return;
        }

        if (!_worker.HasExited && _pipe is not null)
        {
            var request = new DesktopAutomationWorkerRequest(
                DesktopAutomationWorkerProtocol.Version,
                Guid.NewGuid().ToString("N"),
                "stop");
            await WriteAsync(request, cancellationToken).ConfigureAwait(false);
            await ReadAsync<DesktopAutomationWorkerResponse>(cancellationToken).ConfigureAwait(false);
        }

        await WaitForExitAsync(_worker, cancellationToken).ConfigureAwait(false);
        if (!_worker.HasExited)
        {
            throw new InvalidOperationException("The owned worker did not stop normally.");
        }

        _worker.Dispose();
        _worker = null;
        _started = false;
    }

    public async ValueTask DisposeAsync()
    {
        if (_worker is not null)
        {
            try
            {
                await StopAsync().ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // Keep ownership when a worker cannot be confirmed stopped.
            }
        }

        _pipe?.Dispose();
        _pipe = null;
    }

    public static async Task<int> RunWorkerAsync(
        string pipeName,
        IDesktopAutomationWorkerProvider provider,
        int? expectedParentProcessId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentNullException.ThrowIfNull(provider);
        if (expectedParentProcessId is not null)
        {
            using var parent = Process.GetProcessById(expectedParentProcessId.Value);
            if (parent.SessionId != Process.GetCurrentProcess().SessionId)
            {
                throw new InvalidOperationException("The worker parent is in a different Windows session.");
            }
        }

        await using var client = new DesktopAutomationWorkerClient(pipeName);
        await client.ConnectAsync(TimeSpan.FromSeconds(10), expectHello: false, cancellationToken: cancellationToken).ConfigureAwait(false);
        await client.WriteMessageAsync(
            new DesktopAutomationWorkerHello(
                DesktopAutomationWorkerProtocol.Version,
                Environment.ProcessId,
                Process.GetCurrentProcess().SessionId,
                WindowsIdentity.GetCurrent().User?.Value ?? string.Empty),
            cancellationToken).ConfigureAwait(false);

        var requestIds = new HashSet<string>(StringComparer.Ordinal);
        using var executor = new MtaWorkerExecutor(provider);
        Task<DesktopAutomationWorkerResponse>? active = null;
        CancellationTokenSource? activeCancellation = null;
        Task<DesktopAutomationWorkerRequest>? pendingRead = null;
        while (true)
        {
            pendingRead ??= client.ReadMessageAsync<DesktopAutomationWorkerRequest>(cancellationToken);
            if (active is null)
            {
                var request = await pendingRead.ConfigureAwait(false);
                pendingRead = null;
                ValidateRequest(request);
                if (!requestIds.Add(request.RequestId))
                {
                    throw new InvalidDataException("The worker received a replayed request.");
                }

                if (string.Equals(request.Operation, "stop", StringComparison.Ordinal))
                {
                    await client.WriteMessageAsync(
                        new DesktopAutomationWorkerResponse(
                            DesktopAutomationWorkerProtocol.Version,
                            request.RequestId,
                            true,
                            "Stopped"),
                        cancellationToken).ConfigureAwait(false);
                    return 0;
                }

                activeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                active = executor.ExecuteAsync(request, activeCancellation.Token);
                continue;
            }

            var completed = await Task.WhenAny(active, pendingRead).ConfigureAwait(false);
            if (completed == active)
            {
                await client.WriteMessageAsync(await active.ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
                active = null;
                activeCancellation?.Dispose();
                activeCancellation = null;
                continue;
            }

            var controlRequest = await pendingRead.ConfigureAwait(false);
            pendingRead = null;
            ValidateRequest(controlRequest);
            if (!requestIds.Add(controlRequest.RequestId))
            {
                throw new InvalidDataException("The worker received a replayed request.");
            }

            if (string.Equals(controlRequest.Operation, "stop", StringComparison.Ordinal))
            {
                activeCancellation!.Cancel();
                try
                {
                    await active.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }

                await client.WriteMessageAsync(
                    new DesktopAutomationWorkerResponse(
                        DesktopAutomationWorkerProtocol.Version,
                        controlRequest.RequestId,
                        true,
                        "Stopped"),
                    cancellationToken).ConfigureAwait(false);
                return 0;
            }

            await client.WriteMessageAsync(
                new DesktopAutomationWorkerResponse(
                    DesktopAutomationWorkerProtocol.Version,
                    controlRequest.RequestId,
                    false,
                    "WorkerBusy"),
                cancellationToken).ConfigureAwait(false);
        }

    }

    private static void ValidateRequest(DesktopAutomationWorkerRequest request)
    {
        if (request.ProtocolVersion != DesktopAutomationWorkerProtocol.Version
            || string.IsNullOrWhiteSpace(request.RequestId))
        {
            throw new InvalidDataException("The worker request used an unsupported protocol version or request ID.");
        }
    }

    private ProcessStartInfo CreateStartInfo(string pipeName)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(_executablePath), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.ArgumentList.Add(_assemblyPath);
        }

        startInfo.ArgumentList.Add("--desktop-worker");
        startInfo.ArgumentList.Add("--desktop-pipe");
        startInfo.ArgumentList.Add(pipeName);
        startInfo.ArgumentList.Add("--desktop-parent-pid");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
        return startInfo;
    }

    private static void VerifyWorkerIdentity(Process worker)
    {
        if (worker.SessionId != Process.GetCurrentProcess().SessionId)
        {
            throw new InvalidOperationException("The worker is in a different Windows session.");
        }
    }

    private async Task WriteAsync<T>(T message, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(DesktopAutomationWorkerProtocol.Serialize(message) + "\n");
        await _pipe!.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await _pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> ReadAsync<T>(CancellationToken cancellationToken)
    {
        using var memory = new MemoryStream();
        var buffer = new byte[1024];
        while (true)
        {
            var read = await _pipe!.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("The worker closed the pipe.");
            }

            var newline = Array.IndexOf(buffer, (byte)'\n', 0, read);
            if (newline >= 0)
            {
                memory.Write(buffer, 0, newline);
                break;
            }

            memory.Write(buffer, 0, read);
            if (memory.Length > DesktopAutomationWorkerProtocol.MaxMessageBytes)
            {
                throw new InvalidDataException("The worker message exceeds the protocol limit.");
            }
        }

        return DesktopAutomationWorkerProtocol.Deserialize<T>(Encoding.UTF8.GetString(memory.ToArray()));
    }

    private static async Task WaitForExitAsync(Process process, CancellationToken cancellationToken)
    {
        while (!process.HasExited)
        {
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
    }

    internal sealed class MtaWorkerExecutor : IDisposable
    {
        private readonly BlockingCollection<WorkItem> _queue = new();
        private readonly Thread _thread;

        public MtaWorkerExecutor(IDesktopAutomationWorkerProvider provider)
        {
            ArgumentNullException.ThrowIfNull(provider);
            _thread = new Thread(() =>
            {
                foreach (var item in _queue.GetConsumingEnumerable())
                {
                    try
                    {
                        item.Completion.SetResult(provider.HandleAsync(item.Request, item.CancellationToken).GetAwaiter().GetResult());
                    }
                    catch (Exception exception)
                    {
                        item.Completion.SetException(exception);
                    }
                }
            })
            {
                IsBackground = true,
                Name = "Pointframe desktop automation worker",
            };
            _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Start();
        }

        public Task<DesktopAutomationWorkerResponse> ExecuteAsync(
            DesktopAutomationWorkerRequest request,
            CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<DesktopAutomationWorkerResponse>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Add(new WorkItem(request, cancellationToken, completion), cancellationToken);
            return completion.Task;
        }

        public void Dispose()
        {
            _queue.CompleteAdding();
            if (_thread.IsAlive)
            {
                _thread.Join(TimeSpan.FromSeconds(2));
            }

            _queue.Dispose();
        }

        private sealed record WorkItem(
            DesktopAutomationWorkerRequest Request,
            CancellationToken CancellationToken,
            TaskCompletionSource<DesktopAutomationWorkerResponse> Completion);
    }
}
