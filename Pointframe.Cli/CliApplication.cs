using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pointframe.Cli;

internal interface IAgentBridgeClient
{
    Task<BridgeResponse> SendAsync(string command, string? monitorName = null, CancellationToken cancellationToken = default);
}

internal sealed class CliApplication
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };
    private readonly IAgentBridgeClient _bridgeClient;
    private readonly TextWriter _standardOutput;
    private readonly TextWriter _standardError;

    internal CliApplication(IAgentBridgeClient bridgeClient, TextWriter standardOutput, TextWriter standardError)
    {
        _bridgeClient = bridgeClient;
        _standardOutput = standardOutput;
        _standardError = standardError;
    }

    internal static async Task<int> RunAsync(string[] args, TextWriter standardOutput, TextWriter standardError)
    {
        try
        {
            var client = new NamedPipeAgentBridgeClient();
            return await new CliApplication(client, standardOutput, standardError).RunAsync(args);
        }
        catch (Exception exception)
        {
            await standardError.WriteLineAsync($"Pointframe CLI failed: {exception.Message}");
            return 1;
        }
    }

    internal async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        if (!CliCommandParser.TryParse(args, out var command, out var error))
        {
            await _standardError.WriteLineAsync(error);
            await _standardError.WriteLineAsync(CliCommandParser.Usage);
            return 2;
        }

        var response = await _bridgeClient.SendAsync(command.BridgeCommand, command.MonitorName, cancellationToken);
        if (!response.Success)
        {
            var bridgeError = response.Error?.Message ?? "The Pointframe agent bridge rejected the command.";
            await _standardError.WriteLineAsync($"Pointframe CLI failed: {bridgeError}");
            return 1;
        }

        if (command.BridgeCommand == "capture.monitor")
        {
            response = await _bridgeClient.SendAsync("overlay.save", cancellationToken: cancellationToken);
            if (!response.Success)
            {
                var bridgeError = response.Error?.Message ?? "The Pointframe agent bridge rejected the command.";
                await _standardError.WriteLineAsync($"Pointframe CLI failed: {bridgeError}");
                return 1;
            }
        }

        var payload = command.BridgeCommand switch
        {
            "displays.list" when response.Displays is not null => JsonSerializer.Serialize(response.Displays, SerializerOptions),
            "capture.monitor" when response.Artifact is not null => JsonSerializer.Serialize(response.Artifact, SerializerOptions),
            _ => null,
        };
        if (payload is null)
        {
            await _standardError.WriteLineAsync("Pointframe CLI failed: The bridge returned an incomplete response.");
            return 1;
        }

        await _standardOutput.WriteLineAsync(payload);
        return 0;
    }
}

internal static class CliCommandParser
{
    internal const string Usage = "Usage: pointframe-cli displays | capture --monitor <exact Windows device name>";

    internal static bool TryParse(string[] args, out CliCommand command, out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Length == 1 && string.Equals(args[0], "displays", StringComparison.OrdinalIgnoreCase))
        {
            command = new CliCommand("displays.list");
            error = null;
            return true;
        }

        if (args.Length == 3
            && string.Equals(args[0], "capture", StringComparison.OrdinalIgnoreCase)
            && string.Equals(args[1], "--monitor", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(args[2]))
        {
            command = new CliCommand("capture.monitor", args[2]);
            error = null;
            return true;
        }

        command = default!;
        error = args.FirstOrDefault()?.Equals("capture", StringComparison.OrdinalIgnoreCase) == true
            ? "The capture command requires --monitor followed by an exact Windows device name."
            : "Unknown or incomplete command.";
        return false;
    }
}

internal sealed record CliCommand(string BridgeCommand, string? MonitorName = null);

internal sealed class NamedPipeAgentBridgeClient : IAgentBridgeClient
{
    private const int MaximumMessageLength = 64 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };
    private readonly string _pipeName;
    private readonly string _secret;

    internal NamedPipeAgentBridgeClient()
    {
        _pipeName = Environment.GetEnvironmentVariable("POINTFRAME_AGENT_BRIDGE_PIPE")
            ?? throw new InvalidOperationException("POINTFRAME_AGENT_BRIDGE_PIPE is required.");
        _secret = Environment.GetEnvironmentVariable("POINTFRAME_AGENT_BRIDGE_SECRET")
            ?? throw new InvalidOperationException("POINTFRAME_AGENT_BRIDGE_SECRET is required.");
    }

    public async Task<BridgeResponse> SendAsync(string command, string? monitorName = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        var request = new BridgeRequest(1, Guid.NewGuid().ToString("N"), _secret, command, monitorName);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(RequestTimeout);
        var requestCancellationToken = timeoutSource.Token;
        await using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(requestCancellationToken);

        var payload = JsonSerializer.SerializeToUtf8Bytes(request, SerializerOptions);
        await client.WriteAsync(BitConverter.GetBytes(payload.Length), requestCancellationToken);
        await client.WriteAsync(payload, requestCancellationToken);
        await client.FlushAsync(requestCancellationToken);

        var lengthBuffer = new byte[sizeof(int)];
        await client.ReadExactlyAsync(lengthBuffer, requestCancellationToken);
        var length = BitConverter.ToInt32(lengthBuffer);
        if (length is <= 0 or > MaximumMessageLength)
        {
            throw new InvalidDataException("The bridge response length is invalid.");
        }

        var responsePayload = new byte[length];
        await client.ReadExactlyAsync(responsePayload, requestCancellationToken);
        return JsonSerializer.Deserialize<BridgeResponse>(responsePayload, SerializerOptions)
            ?? throw new InvalidDataException("The bridge returned an empty response.");
    }
}

internal sealed record BridgeRequest(int SchemaVersion, string RequestId, string Secret, string Command, string? MonitorName = null);

internal sealed record BridgeResponse(
    int SchemaVersion,
    string RequestId,
    bool Success,
    BridgeError? Error = null,
    IReadOnlyList<DisplayDescriptor>? Displays = null,
    ArtifactDescriptor? Artifact = null);

internal sealed record BridgeError(string Code, string Message);

internal sealed record DisplayDescriptor(int SchemaVersion, string MonitorName, double DpiScaleX, double DpiScaleY, PixelBounds BoundsPixels);

internal sealed record PixelBounds(int X, int Y, int Width, int Height);

internal sealed record ArtifactDescriptor(int SchemaVersion, string OperationId, ImageArtifactMetadata Metadata);

internal sealed record ImageArtifactMetadata(
    int SchemaVersion,
    string ArtifactId,
    string Kind,
    string Path,
    string Sha256,
    long ByteLength,
    DateTimeOffset CreatedUtc,
    string Source,
    string MonitorName,
    double DpiScaleX,
    double DpiScaleY,
    PixelBounds CaptureBoundsPixels);
