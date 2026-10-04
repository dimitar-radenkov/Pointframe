using System.Diagnostics;
using System.Text.Json;

namespace Pointframe.Cli;

internal interface IMcpToolClient : IAsyncDisposable
{
    Task<JsonElement> CallToolAsync(string name, object arguments, TimeSpan timeout, CancellationToken cancellationToken);
}

internal interface IMcpToolClientFactory
{
    // environment is added to the server's environment; the app it launches inherits it, which is how
    // isolation hands the app its fresh data folder without the desktop policy allowing environment keys.
    Task<IMcpToolClient> LaunchAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken);
}

internal sealed class McpStdioToolClientFactory : IMcpToolClientFactory
{
    public Task<IMcpToolClient> LaunchAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken) =>
        McpStdioToolClient.LaunchAsync(executablePath, arguments, environment, cancellationToken);
}

// A minimal MCP client over stdio: one request at a time, newline-delimited JSON-RPC. The server logs at
// Debug level to stderr, so stderr is drained continuously; an undrained pipe fills and blocks the server.
internal sealed class McpStdioToolClient : IMcpToolClient
{
    private const string ProtocolVersion = "2025-06-18";
    private const int StandardErrorTailLines = 40;

    private readonly Process _process;
    private readonly Queue<string> _standardErrorTail = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _nextRequestId;

    private McpStdioToolClient(Process process)
    {
        _process = process;
    }

    internal static async Task<IMcpToolClient> LaunchAsync(
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
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in environment)
        {
            startInfo.Environment[name] = value;
        }

        var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("The Pointframe MCP server could not be started.");
        }

        var client = new McpStdioToolClient(process);
        process.ErrorDataReceived += (_, line) => client.RememberStandardError(line.Data);
        process.BeginErrorReadLine();
        try
        {
            _ = await client.SendRequestAsync(
                "initialize",
                new
                {
                    protocolVersion = ProtocolVersion,
                    capabilities = new { },
                    clientInfo = new { name = "Pointframe CLI verify", version = "1.0" },
                },
                TimeSpan.FromSeconds(30),
                cancellationToken).ConfigureAwait(false);
            await client.WriteLineAsync(
                JsonSerializer.Serialize(new { jsonrpc = "2.0", method = "notifications/initialized", @params = new { } }),
                cancellationToken).ConfigureAwait(false);
            return client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public Task<JsonElement> CallToolAsync(string name, object arguments, TimeSpan timeout, CancellationToken cancellationToken) =>
        SendRequestAsync("tools/call", new { name, arguments }, timeout, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        try
        {
            _process.StandardInput.Close();
            using var exitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _process.WaitForExitAsync(exitTimeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or InvalidOperationException)
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }

        _process.Dispose();
        _gate.Dispose();
    }

    private async Task<JsonElement> SendRequestAsync(string method, object parameters, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        await _gate.WaitAsync(timeoutSource.Token).ConfigureAwait(false);
        try
        {
            var id = ++_nextRequestId;
            await WriteLineAsync(
                JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters }),
                timeoutSource.Token).ConfigureAwait(false);
            while (true)
            {
                string? line;
                try
                {
                    line = await _process.StandardOutput.ReadLineAsync(timeoutSource.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException($"The MCP server did not answer '{method}' within {timeout.TotalSeconds:0} seconds.");
                }

                if (line is null)
                {
                    throw new IOException($"The MCP server exited during '{method}'. Last log lines: {StandardErrorTail()}");
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (!root.TryGetProperty("id", out var responseId) || !responseId.TryGetInt32(out var responseNumber) || responseNumber != id)
                {
                    continue;
                }

                if (root.TryGetProperty("error", out var error))
                {
                    throw new InvalidOperationException($"MCP request '{method}' failed: {error.GetRawText()}");
                }

                return root.GetProperty("result").Clone();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task WriteLineAsync(string line, CancellationToken cancellationToken)
    {
        await _process.StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
        await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private void RememberStandardError(string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (_standardErrorTail)
        {
            _standardErrorTail.Enqueue(line);
            while (_standardErrorTail.Count > StandardErrorTailLines)
            {
                _standardErrorTail.Dequeue();
            }
        }
    }

    private string StandardErrorTail()
    {
        lock (_standardErrorTail)
        {
            return string.Join(" | ", _standardErrorTail);
        }
    }
}
