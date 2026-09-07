using System.IO.Pipes;
using System.Text;

namespace Pointframe.Mcp.Automation;

public sealed class DesktopAutomationWorkerClient : IAsyncDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _completedRequests = new(StringComparer.Ordinal);
    private bool _connected;

    public DesktopAutomationWorkerClient(string pipeName)
    {
        if (string.IsNullOrWhiteSpace(pipeName))
        {
            throw new ArgumentException("A pipe name is required.", nameof(pipeName));
        }

        _pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
    }

    public async Task ConnectAsync(
        TimeSpan timeout,
        int expectedParentProcessId = 0,
        bool expectHello = true,
        CancellationToken cancellationToken = default)
    {
        await _pipe.ConnectAsync((int)timeout.TotalMilliseconds, cancellationToken).ConfigureAwait(false);
        _connected = true;
        if (expectedParentProcessId > 0)
        {
            using var parent = System.Diagnostics.Process.GetProcessById(expectedParentProcessId);
            if (parent.SessionId != System.Diagnostics.Process.GetCurrentProcess().SessionId)
            {
                throw new InvalidOperationException("The worker parent is in a different Windows session.");
            }
        }

        if (!expectHello)
        {
            return;
        }

        var hello = await ReadAsync<DesktopAutomationWorkerHello>(cancellationToken).ConfigureAwait(false);
        if (hello.ProtocolVersion != DesktopAutomationWorkerProtocol.Version
            || hello.WorkerProcessId <= 0
            || hello.WindowsSessionId != System.Diagnostics.Process.GetCurrentProcess().SessionId)
        {
            throw new InvalidDataException("The worker identity could not be verified.");
        }
    }

    public async Task<DesktopAutomationWorkerResponse> SendAsync(
        string operation,
        string? payload = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        if (!_connected)
        {
            throw new InvalidOperationException("The worker is not connected.");
        }

        var requestId = Guid.NewGuid().ToString("N");
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout is not null)
        {
            timeoutSource.CancelAfter(timeout.Value);
        }

        await _gate.WaitAsync(timeoutSource.Token).ConfigureAwait(false);
        try
        {
            await WriteAsync(new DesktopAutomationWorkerRequest(
                DesktopAutomationWorkerProtocol.Version,
                requestId,
                operation,
                payload), timeoutSource.Token).ConfigureAwait(false);

            while (true)
            {
                var response = await ReadAsync<DesktopAutomationWorkerResponse>(timeoutSource.Token).ConfigureAwait(false);
                if (response.ProtocolVersion != DesktopAutomationWorkerProtocol.Version
                    || !string.Equals(response.RequestId, requestId, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("The worker returned an unexpected response.");
                }

                if (!_completedRequests.Add(response.RequestId))
                {
                    throw new InvalidDataException("The worker replayed a response.");
                }

                return response;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _connected = false;
        await _pipe.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    internal Task WriteMessageAsync<T>(T message, CancellationToken cancellationToken) =>
        WriteAsync(message, cancellationToken);

    internal Task<T> ReadMessageAsync<T>(CancellationToken cancellationToken) =>
        ReadAsync<T>(cancellationToken);

    private async Task WriteAsync<T>(T message, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(DesktopAutomationWorkerProtocol.Serialize(message) + "\n");
        await _pipe.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await _pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> ReadAsync<T>(CancellationToken cancellationToken)
    {
        using var memory = new MemoryStream();
        var buffer = new byte[1024];
        while (true)
        {
            var read = await _pipe.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
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
}
