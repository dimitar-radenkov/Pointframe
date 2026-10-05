using System.Diagnostics;
using System.IO;
using System.IO.Pipelines;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Moq;
using Pointframe.Engine;
using Pointframe.Mcp;
using Pointframe.Telemetry;
using Xunit;

namespace Pointframe.Tests.Mcp;

public sealed class McpTelemetryTests
{
    [Theory]
    [InlineData(typeof(PointframeMcpTools))]
    [InlineData(typeof(DesktopTestingMcpTools))]
    public void EveryTool_HasATitleAndExplicitReadOnlyAndDestructiveHints(Type toolType)
    {
        var problems = new List<string>();
        foreach (var method in ToolMethods(toolType))
        {
            var tool = McpServerTool.Create(method, _ => null!).ProtocolTool;
            if (string.IsNullOrWhiteSpace(tool.Title))
            {
                problems.Add($"{tool.Name} has no title");
            }

            if (tool.Annotations?.ReadOnlyHint is null)
            {
                problems.Add($"{tool.Name} has no explicit readOnlyHint");
            }

            if (tool.Annotations?.DestructiveHint is null)
            {
                problems.Add($"{tool.Name} has no explicit destructiveHint");
            }

            if (tool.Annotations is { ReadOnlyHint: true, DestructiveHint: true })
            {
                problems.Add($"{tool.Name} claims to be both read-only and destructive");
            }
        }

        Assert.Empty(problems);
    }

    [Fact]
    public void ToolHints_MatchWhatEachToolDoes()
    {
        var hints = ToolMethods(typeof(PointframeMcpTools)).Concat(ToolMethods(typeof(DesktopTestingMcpTools)))
            .Select(method => McpServerTool.Create(method, _ => null!).ProtocolTool)
            .ToDictionary(tool => tool.Name, tool => (ReadOnly: tool.Annotations!.ReadOnlyHint, Destructive: tool.Annotations.DestructiveHint));

        foreach (var name in new[] { "search_captures", "get_capture", "list_displays", "list_windows", "get_recording_status", "desktop_observe_app", "desktop_check_ui" })
        {
            Assert.Equal((true, false), hints[name]);
        }

        foreach (var name in new[] { "start_recording", "stop_recording" })
        {
            Assert.Equal((false, false), hints[name]);
        }

        foreach (var name in new[] { "desktop_click", "desktop_press_keys", "desktop_drag", "desktop_enter_text", "desktop_invoke", "desktop_scroll" })
        {
            Assert.Equal((false, true), hints[name]);
        }
    }

    [Fact]
    public void TelemetryAllowlist_NamesEveryAdvertisedToolAndNothingElse()
    {
        var advertised = ToolMethods(typeof(PointframeMcpTools)).Concat(ToolMethods(typeof(DesktopTestingMcpTools)))
            .Select(method => McpServerTool.Create(method, _ => null!).ProtocolTool.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(advertised, TelemetryAllowlist.McpTools.Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void TelemetryAllowlist_NamesEveryCliCommandTheParserAccepts()
    {
        var commands = new[] { "displays", "windows", "capture", "ocr", "capture-window", "ocr-window", "record", "install", "mcp", "verify" };

        Assert.Equal(commands.Order(StringComparer.Ordinal), TelemetryAllowlist.CliCommands.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Classify_ReportsPolicyDenialsAsDenied()
    {
        var denied = new CallToolResult
        {
            IsError = true,
            StructuredContent = JsonDocument.Parse("{\"error\":{\"code\":\"GlobalHotkeyNotApproved\",\"message\":\"x\"}}").RootElement.Clone(),
        };
        var failed = new CallToolResult
        {
            IsError = true,
            StructuredContent = JsonDocument.Parse("{\"error\":{\"code\":\"SessionNotFound\"}}").RootElement.Clone(),
        };

        Assert.Equal(TelemetryOutcome.Denied, McpTelemetryFilter.Classify(denied));
        Assert.Equal(TelemetryOutcome.Error, McpTelemetryFilter.Classify(failed));
        Assert.Equal(TelemetryOutcome.Error, McpTelemetryFilter.Classify(new CallToolResult { IsError = true }));
        Assert.Equal(TelemetryOutcome.Success, McpTelemetryFilter.Classify(new CallToolResult()));
    }

    [Fact]
    public async Task ToolCall_ReportsOneAllowlistedEventAndKeepsStdoutToJsonRpc()
    {
        var telemetry = new RecordingTelemetry();

        var (result, transcript) = await CallStatusToolAsync(telemetry, "claude-code");

        Assert.NotEqual(true, result.IsError);
        var tracked = Assert.Single(telemetry.Tracked);
        Assert.Equal(("get_recording_status", TelemetryOutcome.Success, "claude-code"), (tracked.Name, tracked.Outcome, tracked.Client));
        AssertEveryLineIsJsonRpc(transcript);
    }

    [Fact]
    public async Task ToolCall_Succeeds_WhenTelemetryThrows()
    {
        var telemetry = new RecordingTelemetry { ThrowOnTrack = true };

        var (result, transcript) = await CallStatusToolAsync(telemetry, "codex");

        Assert.NotEqual(true, result.IsError);
        Assert.Equal(1, telemetry.TrackAttempts);
        AssertEveryLineIsJsonRpc(transcript);
    }

    [Fact]
    public async Task ToolCall_Succeeds_WhenTheRealExporterThrows()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PointframeTelemetryTests", Guid.NewGuid().ToString("N"));
        var telemetry = OperationTelemetryFactory.Create(
            TelemetryHost.Mcp,
            "1.2.3",
            new TelemetryPreferences(directory, _ => null),
            () => throw new InvalidOperationException("exporter is down"),
            _ => { });

        var (result, _) = await CallStatusToolAsync(telemetry, "codex");

        Assert.NotEqual(true, result.IsError);
        telemetry.Flush();
        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public async Task ServerProcess_WithTelemetryOn_KeepsStdoutToJsonRpcAndSendsTheAllowlistedEvent()
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "Pointframe.Mcp.exe");
        Assert.True(File.Exists(executable), $"{executable} was not copied to the test output.");
        var dataDirectory = Path.Combine(Path.GetTempPath(), "PointframeTelemetryTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDirectory);
        using var listener = new System.Net.HttpListener();
        var port = FreePort();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var payloadTask = ReadPayloadAsync(listener);

        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.Environment["SNIPPINGTOOL_AUTOMATION_DATA_DIRECTORY"] = dataDirectory;
        startInfo.Environment["POINTFRAME_TELEMETRY_CONNECTION_STRING"] =
            $"InstrumentationKey=00000000-0000-0000-0000-000000000000;IngestionEndpoint=http://127.0.0.1:{port}/";
        startInfo.Environment.Remove("POINTFRAME_TELEMETRY_OPTOUT");
        startInfo.Environment.Remove("DO_NOT_TRACK");

        using var process = Process.Start(startInfo)!;
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{},\"clientInfo\":{\"name\":\"claude-code\",\"version\":\"1.0\"}}}");
        await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
        await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"get_recording_status\",\"arguments\":{}}}");
        await process.StandardInput.FlushAsync();
        await Task.Delay(TimeSpan.FromSeconds(3));
        process.StandardInput.Close();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));

        var output = await standardOutput;
        var error = await standardError;
        AssertEveryLineIsJsonRpc(output);
        Assert.Contains("\"id\":2", output, StringComparison.Ordinal);
        Assert.Contains("POINTFRAME_TELEMETRY_OPTOUT", error, StringComparison.Ordinal);
        var payload = await payloadTask.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Contains("agent_operation", payload, StringComparison.Ordinal);
        Assert.Contains("get_recording_status", payload, StringComparison.Ordinal);
        Assert.Contains("claude-code", payload, StringComparison.Ordinal);
        Assert.DoesNotContain(Environment.MachineName, payload, StringComparison.OrdinalIgnoreCase);

        try
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task ServerProcess_OptedOutByEnvironment_SendsNothingAndStaysSilent()
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "Pointframe.Mcp.exe");
        var dataDirectory = Path.Combine(Path.GetTempPath(), "PointframeTelemetryTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDirectory);
        using var listener = new System.Net.HttpListener();
        var port = FreePort();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var requestTask = listener.GetContextAsync();

        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.Environment["SNIPPINGTOOL_AUTOMATION_DATA_DIRECTORY"] = dataDirectory;
        startInfo.Environment["POINTFRAME_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["POINTFRAME_TELEMETRY_CONNECTION_STRING"] =
            $"InstrumentationKey=00000000-0000-0000-0000-000000000000;IngestionEndpoint=http://127.0.0.1:{port}/";

        using var process = Process.Start(startInfo)!;
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{},\"clientInfo\":{\"name\":\"claude-code\",\"version\":\"1.0\"}}}");
        await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
        await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"get_recording_status\",\"arguments\":{}}}");
        await process.StandardInput.FlushAsync();
        await Task.Delay(TimeSpan.FromSeconds(3));
        process.StandardInput.Close();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));

        var output = await standardOutput;
        var error = await standardError;
        AssertEveryLineIsJsonRpc(output);
        Assert.Contains("\"id\":2", output, StringComparison.Ordinal);
        Assert.DoesNotContain("POINTFRAME_TELEMETRY_OPTOUT", error, StringComparison.Ordinal);
        var winner = await Task.WhenAny(requestTask, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.NotSame(requestTask, winner);

        try
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static async Task<(CallToolResult Result, string Transcript)> CallStatusToolAsync(IOperationTelemetry telemetry, string clientName)
    {
        var recording = new Mock<IDirectRecordingMcpService>();
        recording.Setup(service => service.GetRecordingStatus()).Returns("{\"SchemaVersion\":1,\"Success\":true,\"IsRecording\":false}");
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        var transcript = new MemoryStream();

        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Services.AddSingleton(new Mock<IDirectCaptureService>().Object);
        builder.Services.AddSingleton(new Mock<ICaptureCatalogService>().Object);
        builder.Services.AddSingleton(recording.Object);
        builder.Services
            .AddMcpServer()
            .WithStreamServerTransport(clientToServer.Reader.AsStream(), new TeeStream(serverToClient.Writer.AsStream(), transcript))
            .WithRequestFilters(filters => filters.AddCallToolFilter(McpTelemetryFilter.Create(telemetry)))
            .WithTools<PointframeMcpTools>();
        using var host = builder.Build();
        await host.StartAsync();

        await using var client = await McpClient.CreateAsync(
            new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream()),
            new McpClientOptions { ClientInfo = new Implementation { Name = clientName, Version = "1.0" } });
        var result = await client.CallToolAsync("get_recording_status", new Dictionary<string, object?>());
        await client.DisposeAsync();
        await host.StopAsync();
        return (result, Encoding.UTF8.GetString(transcript.ToArray()));
    }

    private static void AssertEveryLineIsJsonRpc(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.NotEmpty(lines);
        foreach (var line in lines)
        {
            using var document = JsonDocument.Parse(line);
            Assert.Equal("2.0", document.RootElement.GetProperty("jsonrpc").GetString());
        }
    }

    private static int FreePort()
    {
        using var socket = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        socket.Start();
        return ((System.Net.IPEndPoint)socket.LocalEndpoint).Port;
    }

    private static async Task<string> ReadPayloadAsync(System.Net.HttpListener listener)
    {
        var context = await listener.GetContextAsync();
        await using var body = new MemoryStream();
        await context.Request.InputStream.CopyToAsync(body);
        var bytes = body.ToArray();
        if (context.Request.Headers["Content-Encoding"]?.Contains("gzip", StringComparison.OrdinalIgnoreCase) == true)
        {
            await using var compressed = new MemoryStream(bytes);
            await using var decompressor = new System.IO.Compression.GZipStream(compressed, System.IO.Compression.CompressionMode.Decompress);
            await using var decompressed = new MemoryStream();
            await decompressor.CopyToAsync(decompressed);
            bytes = decompressed.ToArray();
        }

        context.Response.StatusCode = 200;
        context.Response.Close();
        return Encoding.UTF8.GetString(bytes);
    }

    private static IEnumerable<MethodInfo> ToolMethods(Type toolType)
    {
        return toolType
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null);
    }

    private sealed class RecordingTelemetry : IOperationTelemetry
    {
        public List<(string Name, TelemetryOutcome Outcome, string? Client)> Tracked { get; } = [];

        public bool ThrowOnTrack { get; init; }

        public int TrackAttempts { get; private set; }

        public void Track(string name, TelemetryOutcome outcome, TimeSpan duration, string? mcpClientName = null)
        {
            TrackAttempts++;
            if (ThrowOnTrack)
            {
                throw new InvalidOperationException("telemetry failure");
            }

            Tracked.Add((name, outcome, mcpClientName));
        }

        public void Flush()
        {
        }
    }

    private sealed class TeeStream(Stream inner, Stream copy) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            lock (copy)
            {
                copy.Write(buffer, offset, count);
            }

            inner.Write(buffer, offset, count);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            lock (copy)
            {
                copy.Write(buffer.Span);
            }

            await inner.WriteAsync(buffer, cancellationToken);
        }
    }
}
