using System.Diagnostics;
using System.Text.Json;

namespace Pointframe.AutomationTests.Support;

public sealed class McpDesktopTestClient : IAsyncDisposable
{
    private const string ProtocolVersion = "2025-06-18";
    private readonly Process _process;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private int _nextRequestId;
    private bool _initialized;

    private McpDesktopTestClient(Process process)
    {
        _process = process;
        _reader = process.StandardOutput;
        _writer = process.StandardInput;
    }

    public int ProcessId => _process.Id;

    public bool HasExited => _process.HasExited;

    public static async Task<McpDesktopTestClient> LaunchAsync(
        string executablePath,
        IReadOnlyList<string>? arguments = null,
        string? workingDirectory = null,
        TimeSpan? startupTimeout = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !Path.IsPathFullyQualified(executablePath))
        {
            throw new ArgumentException("The MCP executable path must be absolute.", nameof(executablePath));
        }

        var fullPath = Path.GetFullPath(executablePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The MCP executable was not found.", fullPath);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = fullPath,
            WorkingDirectory = workingDirectory is null ? Path.GetDirectoryName(fullPath)! : Path.GetFullPath(workingDirectory),
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments ?? Array.Empty<string>())
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("The MCP process could not be started.");
        }

        var client = new McpDesktopTestClient(process);
        try
        {
            await client.InitializeAsync(startupTimeout ?? TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
            return client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<JsonElement> ListToolsAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        return await SendRequestAsync("tools/list", new { }, timeout, cancellationToken).ConfigureAwait(false);
    }

    public async Task<JsonElement> CallToolAsync(
        string name,
        object? arguments = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return await SendRequestAsync(
            "tools/call",
            new
            {
                name,
                arguments = arguments ?? new { },
            },
            timeout,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<JsonElement> ReadResourceAsync(
        string uri,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);
        return await SendRequestAsync("resources/read", new { uri }, timeout, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_initialized && !_process.HasExited)
            {
                await SendNotificationAsync("notifications/cancelled", new { }).ConfigureAwait(false);
            }
        }
        catch
        {
        }

        try
        {
            _writer.Close();
        }
        catch
        {
        }

        if (!_process.HasExited)
        {
            using var exitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try
            {
                await _process.WaitForExitAsync(exitTimeout.Token).ConfigureAwait(false);
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
        }

        _process.Dispose();
        _writeGate.Dispose();
    }

    private async Task InitializeAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        _ = await SendRequestAsync(
            "initialize",
            new
            {
                protocolVersion = ProtocolVersion,
                capabilities = new { },
                clientInfo = new
                {
                    name = "Pointframe.AutomationTests",
                    version = "1.0",
                },
            },
            timeout,
            timeoutSource.Token).ConfigureAwait(false);

        await SendNotificationAsync("notifications/initialized", new { }, timeoutSource.Token).ConfigureAwait(false);
        _initialized = true;
    }

    private async Task<JsonElement> SendRequestAsync(
        string method,
        object parameters,
        TimeSpan? timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout is not null)
        {
            timeoutSource.CancelAfter(timeout.Value);
        }

        var id = Interlocked.Increment(ref _nextRequestId);
        var request = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id,
            method,
            @params = parameters,
        });

        await _writeGate.WaitAsync(timeoutSource.Token).ConfigureAwait(false);
        try
        {
            await _writer.WriteLineAsync(request.AsMemory(), timeoutSource.Token).ConfigureAwait(false);
            await _writer.FlushAsync(timeoutSource.Token).ConfigureAwait(false);

            while (true)
            {
                var line = await _reader.ReadLineAsync(timeoutSource.Token).ConfigureAwait(false);
                if (line is null)
                {
                    var error = await ReadStandardErrorAsync().ConfigureAwait(false);
                    throw new EndOfStreamException(
                        string.IsNullOrWhiteSpace(error)
                            ? "The MCP process closed stdout."
                            : $"The MCP process closed stdout: {error}");
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (!root.TryGetProperty("id", out var responseId)
                    || responseId.ValueKind != JsonValueKind.Number
                    || responseId.GetInt32() != id)
                {
                    continue;
                }

                if (root.TryGetProperty("error", out var errorElement))
                {
                    throw new InvalidOperationException($"MCP request '{method}' failed: {errorElement}");
                }

                return root.TryGetProperty("result", out var result)
                    ? result.Clone()
                    : throw new InvalidDataException($"MCP request '{method}' returned no result.");
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task SendNotificationAsync(
        string method,
        object parameters,
        CancellationToken cancellationToken = default)
    {
        var notification = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            method,
            @params = parameters,
        });

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _writer.WriteLineAsync(notification.AsMemory(), cancellationToken).ConfigureAwait(false);
            await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task<string> ReadStandardErrorAsync()
    {
        if (!_process.StartInfo.RedirectStandardError)
        {
            return string.Empty;
        }

        return await _process.StandardError.ReadToEndAsync().ConfigureAwait(false);
    }
}

public sealed record McpExternalAssertion(
    string Name,
    bool Passed,
    string? Details = null);

public sealed class McpRunManifest
{
    private readonly List<McpExternalAssertion> _externalAssertions = [];

    public DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? EndedUtc { get; private set; }

    public string? TargetExecutablePath { get; set; }

    public string? TargetExecutableSha256Before { get; set; }

    public string? TargetExecutableSha256After { get; set; }

    public IReadOnlyList<string> ActualArguments { get; set; } = [];

    public bool OrdinaryStartup { get; set; }

    public string? InitialObservedState { get; set; }

    public IReadOnlyList<McpExternalAssertion> ExternalAssertions => _externalAssertions;

    public void AddExternalAssertion(string name, bool passed, string? details = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _externalAssertions.Add(new McpExternalAssertion(name, passed, details));
    }

    public void Complete()
    {
        EndedUtc = DateTimeOffset.UtcNow;
    }

    public void CaptureTargetHashBefore(string executablePath)
    {
        TargetExecutablePath = Path.GetFullPath(executablePath);
        TargetExecutableSha256Before = ComputeHash(TargetExecutablePath);
    }

    public void CaptureTargetHashAfter()
    {
        if (TargetExecutablePath is null)
        {
            throw new InvalidOperationException("CaptureTargetHashBefore must be called first.");
        }

        TargetExecutableSha256After = ComputeHash(TargetExecutablePath);
    }

    private static string ComputeHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
    }
}
