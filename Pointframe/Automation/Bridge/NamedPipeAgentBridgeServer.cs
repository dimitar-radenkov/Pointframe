using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pointframe.Automation.Bridge;

internal interface IAgentBridgeServer : IDisposable
{
    string PipeName { get; }

    void Start();
}

internal sealed class NamedPipeAgentBridgeServer : IAgentBridgeServer
{
    private const int MaximumMessageLength = 64 * 1024;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };
    private readonly IAgentBridgeCommandService _commandService;
    private readonly ILogger<NamedPipeAgentBridgeServer> _logger;
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly AgentBridgeConnectionOptions _connectionOptions;
    private Task? _listener;
    private bool _disposed;

    public NamedPipeAgentBridgeServer(
        IAgentBridgeCommandService commandService,
        ILogger<NamedPipeAgentBridgeServer> logger,
        AgentBridgeConnectionOptions connectionOptions)
    {
        _commandService = commandService;
        _logger = logger;
        _connectionOptions = connectionOptions;
        PipeName = connectionOptions.PipeName;
    }

    public string PipeName { get; }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_listener is not null)
        {
            return;
        }

        _listener = ListenAsync(_cancellationTokenSource.Token);
        _logger.LogInformation("Agent bridge listening on named pipe {PipeName}", PipeName);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cancellationTokenSource.Cancel();
        try
        {
            _listener?.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _cancellationTokenSource.Dispose();
        }
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("Agent bridge creating pipe listener {PipeName}", PipeName);
            await using var server = new NamedPipeServerStream(
                PipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

            try
            {
                _logger.LogDebug("Agent bridge waiting for a client connection on {PipeName}", PipeName);
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogDebug("Agent bridge accepted a client connection on {PipeName}", PipeName);
                await ProcessConnectionAsync(server, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Agent bridge connection failed");
            }
        }
    }

    private async Task ProcessConnectionAsync(Stream stream, CancellationToken cancellationToken)
    {
        BridgeRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<BridgeRequest>(await ReadFrameAsync(stream, cancellationToken), SerializerOptions);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            await WriteResponseAsync(stream, Error(string.Empty, "invalid_request", "The request framing or JSON is invalid."), cancellationToken);
            return;
        }

        _logger.LogDebug(
            "Agent bridge received request {RequestId} for command {Command}",
            request?.RequestId,
            request?.Command);

        if (request is null || string.IsNullOrWhiteSpace(request.RequestId))
        {
            await WriteResponseAsync(stream, Error(string.Empty, "invalid_request", "A request ID is required."), cancellationToken);
            return;
        }

        if (request.SchemaVersion != 1)
        {
            await WriteResponseAsync(stream, Error(request.RequestId, "unsupported_version", "Schema version 1 is required."), cancellationToken);
            return;
        }

        if (!CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(request.Secret ?? string.Empty),
                System.Text.Encoding.UTF8.GetBytes(_connectionOptions.Secret)))
        {
            _logger.LogWarning(
                "Agent bridge rejected unauthenticated request {RequestId} for command {Command}",
                request.RequestId,
                request.Command);
            await WriteResponseAsync(stream, Error(request.RequestId, "unauthenticated", "The bridge secret is invalid."), cancellationToken);
            return;
        }

        var response = await DispatchAsync(request, cancellationToken);
        _logger.LogDebug(
            "Agent bridge completed request {RequestId} for command {Command} with success {Success}",
            request.RequestId,
            request.Command,
            response.Success);
        await WriteResponseAsync(stream, response, cancellationToken);
    }

    private async Task<BridgeResponse> DispatchAsync(BridgeRequest request, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogDebug("Agent bridge dispatching request {RequestId} for command {Command}", request.RequestId, request.Command);
            return request.Command switch
            {
                AgentBridgeCommands.DisplaysList => new BridgeResponse(1, request.RequestId, true, Displays: await _commandService.ListDisplaysAsync(cancellationToken)),
                AgentBridgeCommands.StateGet => new BridgeResponse(1, request.RequestId, true, State: _commandService.GetState()),
                AgentBridgeCommands.CaptureMonitor when !string.IsNullOrWhiteSpace(request.MonitorName) =>
                    new BridgeResponse(1, request.RequestId, true, State: await _commandService.CaptureMonitorAsync(request.MonitorName, cancellationToken)),
                AgentBridgeCommands.CaptureMonitor => Error(request.RequestId, "invalid_request", "A monitor name is required."),
                AgentBridgeCommands.OverlaySave => new BridgeResponse(1, request.RequestId, true, Artifact: await _commandService.SaveOverlayAsync(cancellationToken)),
                _ => Error(request.RequestId, "unknown_command", $"Unsupported command '{request.Command}'."),
            };
        }
        catch (ArgumentException exception)
        {
            return Error(request.RequestId, "invalid_request", exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            return Error(request.RequestId, "invalid_state", exception.Message);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Agent bridge command {Command} failed", request.Command);
            return Error(request.RequestId, "command_failed", "The command could not be completed.");
        }
    }

    private static async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        var lengthBuffer = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(lengthBuffer, cancellationToken);
        var length = BitConverter.ToInt32(lengthBuffer);
        if (length is <= 0 or > MaximumMessageLength)
        {
            throw new InvalidDataException("The bridge request length is invalid.");
        }

        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        return payload;
    }

    private static async Task WriteResponseAsync(Stream stream, BridgeResponse response, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(response, SerializerOptions);
        await stream.WriteAsync(BitConverter.GetBytes(payload.Length), cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static BridgeResponse Error(string requestId, string code, string message)
    {
        return new BridgeResponse(1, requestId, false, new BridgeError(code, message));
    }
}
