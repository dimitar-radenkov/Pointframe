using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;

namespace Pointframe.AutomationTests.Support;

internal sealed class AgentBridgeApp : IDisposable
{
    private const int MaximumMessageLength = 64 * 1024;
    private readonly Process _process;
    private readonly string _pipeName;
    private readonly string _secret;

    private AgentBridgeApp(Process process, string pipeName, string secret)
    {
        _process = process;
        _pipeName = pipeName;
        _secret = secret;
    }

    public static AgentBridgeApp Launch(IReadOnlyDictionary<string, string> environmentVariables)
    {
        ArgumentNullException.ThrowIfNull(environmentVariables);
        var pipeName = environmentVariables["POINTFRAME_AGENT_BRIDGE_PIPE"];
        var secret = environmentVariables["POINTFRAME_AGENT_BRIDGE_SECRET"];
        var executablePath = AutomationApp.ResolveAutomationExecutablePath();
        var startInfo = new ProcessStartInfo(executablePath, "--agent-bridge")
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory,
        };

        foreach (var environmentVariable in environmentVariables)
        {
            startInfo.Environment[environmentVariable.Key] = environmentVariable.Value;
        }

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Pointframe agent bridge process did not start.");
        return new AgentBridgeApp(process, pipeName, secret);
    }

    public async Task<JsonDocument> SendAsync(string command, string? monitorName = null)
    {
        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var request = new
        {
            schemaVersion = 1,
            requestId = Guid.NewGuid().ToString("N"),
            secret = _secret,
            command,
            monitorName,
        };
        await using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(cancellationTokenSource.Token);

        var requestPayload = JsonSerializer.SerializeToUtf8Bytes(request);
        await client.WriteAsync(BitConverter.GetBytes(requestPayload.Length), cancellationTokenSource.Token);
        await client.WriteAsync(requestPayload, cancellationTokenSource.Token);
        await client.FlushAsync(cancellationTokenSource.Token);

        var lengthBuffer = new byte[sizeof(int)];
        await client.ReadExactlyAsync(lengthBuffer, cancellationTokenSource.Token);
        var responseLength = BitConverter.ToInt32(lengthBuffer);
        if (responseLength is <= 0 or > MaximumMessageLength)
        {
            throw new InvalidDataException("The agent bridge response length is invalid.");
        }

        var responsePayload = new byte[responseLength];
        await client.ReadExactlyAsync(responsePayload, cancellationTokenSource.Token);
        return JsonDocument.Parse(responsePayload);
    }

    public void Dispose()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            _process.WaitForExit(5000);
        }

        _process.Dispose();
    }
}
