using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using OpenTelemetry;
using OpenTelemetry.Logs;
using Pointframe.Telemetry;
using Xunit;

namespace Pointframe.Tests.Telemetry;

public sealed class OperationTelemetryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "PointframeTelemetryTests", Guid.NewGuid().ToString("N"));

    public OperationTelemetryTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void McpEvent_ExportsExactlyTheAllowlistedProperties()
    {
        var exporter = new CapturingExporter();
        var sut = Create(TelemetryHost.Mcp, exporter);

        sut.Track("capture_monitor", TelemetryOutcome.Success, TimeSpan.FromSeconds(2), "claude-code");
        sut.Flush();

        var export = Assert.Single(exporter.Exports);
        Assert.Equal(
            new[] { "client", "duration_bucket", "host", "name", "outcome", "version" },
            export.Properties.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal("capture_monitor", export.Properties["name"]);
        Assert.Equal("success", export.Properties["outcome"]);
        Assert.Equal("1_5s", export.Properties["duration_bucket"]);
        Assert.Equal("mcp", export.Properties["host"]);
        Assert.Equal("claude-code", export.Properties["client"]);
        Assert.Equal("1.2.3", export.Properties["version"]);
        Assert.Equal("agent_operation", export.EventName);
    }

    [Fact]
    public void CliEvent_ExportsExactlyTheAllowlistedProperties()
    {
        var exporter = new CapturingExporter();
        var sut = Create(TelemetryHost.Cli, exporter);

        sut.Track("record", TelemetryOutcome.Cancelled, TimeSpan.FromSeconds(45), "ignored-for-cli");
        sut.Flush();

        var export = Assert.Single(exporter.Exports);
        Assert.Equal(
            new[] { "duration_bucket", "host", "name", "outcome", "version" },
            export.Properties.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal("record", export.Properties["name"]);
        Assert.Equal("cancelled", export.Properties["outcome"]);
        Assert.Equal("gt_30s", export.Properties["duration_bucket"]);
        Assert.Equal("cli", export.Properties["host"]);
    }

    [Fact]
    public void UnknownNameAndClient_AreReportedAsOther()
    {
        var exporter = new CapturingExporter();
        var sut = Create(TelemetryHost.Mcp, exporter);

        sut.Track(@"C:\Users\someone\secret.png", TelemetryOutcome.Error, TimeSpan.Zero, "my-private-client 9.9");
        sut.Flush();

        var export = Assert.Single(exporter.Exports);
        Assert.Equal("other", export.Properties["name"]);
        Assert.Equal("other", export.Properties["client"]);
        Assert.Equal("error", export.Properties["outcome"]);
        Assert.Equal("lt_1s", export.Properties["duration_bucket"]);
    }

    [Theory]
    [InlineData("claude-ai", "claude-desktop")]
    [InlineData("claude-code", "claude-code")]
    [InlineData("codex-mcp-client", "codex")]
    [InlineData("Visual Studio Code", "vscode")]
    [InlineData("cursor-vscode", "cursor")]
    [InlineData("something-else", "other")]
    [InlineData(null, "other")]
    public void McpClient_MapsThroughTheAllowlist(string? clientName, string expected)
    {
        Assert.Equal(expected, TelemetryAllowlist.McpClient(clientName));
    }

    [Theory]
    [InlineData(0, "lt_1s")]
    [InlineData(999, "lt_1s")]
    [InlineData(1000, "1_5s")]
    [InlineData(4999, "1_5s")]
    [InlineData(5000, "5_30s")]
    [InlineData(30000, "5_30s")]
    [InlineData(30001, "gt_30s")]
    public void DurationBucket_UsesFixedBuckets(int milliseconds, string expected)
    {
        Assert.Equal(expected, TelemetryAllowlist.DurationBucket(TimeSpan.FromMilliseconds(milliseconds)));
    }

    [Theory]
    [InlineData("POINTFRAME_TELEMETRY_OPTOUT", "1")]
    [InlineData("DO_NOT_TRACK", "1")]
    [InlineData("POINTFRAME_TELEMETRY_OPTOUT", "true")]
    public void OptOutEnvironmentVariable_ExportsNothingAndCreatesNoExporter(string variable, string value)
    {
        var exporterCreated = false;
        var preferences = new TelemetryPreferences(_directory, name => name == variable ? value : null);

        var sut = OperationTelemetryFactory.Create(
            TelemetryHost.Mcp,
            "1.2.3",
            preferences,
            () =>
            {
                exporterCreated = true;
                return new CapturingExporter();
            },
            _ => { });
        sut.Track("capture_monitor", TelemetryOutcome.Success, TimeSpan.Zero, "codex");
        sut.Flush();

        Assert.False(exporterCreated);
        Assert.IsType<NullOperationTelemetry>(sut);
    }

    [Theory]
    [InlineData("{\"optOut\": true}")]
    [InlineData("{\"OptOut\": true}")]
    [InlineData("not json at all")]
    public void OptOutConfigFile_ExportsNothingAndCreatesNoExporter(string contents)
    {
        File.WriteAllText(Path.Combine(_directory, TelemetryPreferences.ConfigFileName), contents);
        var exporterCreated = false;

        var sut = OperationTelemetryFactory.Create(
            TelemetryHost.Cli,
            "1.2.3",
            new TelemetryPreferences(_directory, _ => null),
            () =>
            {
                exporterCreated = true;
                return new CapturingExporter();
            },
            _ => { });
        sut.Track("capture", TelemetryOutcome.Success, TimeSpan.Zero);
        sut.Flush();

        Assert.False(exporterCreated);
        Assert.IsType<NullOperationTelemetry>(sut);
    }

    [Fact]
    public void ConfigFileWithOptOutFalse_KeepsTelemetryOn()
    {
        File.WriteAllText(Path.Combine(_directory, TelemetryPreferences.ConfigFileName), "{\"optOut\": false}");

        var sut = OperationTelemetryFactory.Create(
            TelemetryHost.Cli,
            "1.2.3",
            new TelemetryPreferences(_directory, _ => null),
            () => new CapturingExporter(),
            _ => { });

        Assert.IsNotType<NullOperationTelemetry>(sut);
    }

    [Fact]
    public void Notice_IsShownOncePerDataDirectory_AndStoresNoIdentifier()
    {
        var notices = new List<string>();
        var preferences = new TelemetryPreferences(_directory, _ => null);

        _ = OperationTelemetryFactory.Create(TelemetryHost.Cli, "1.2.3", preferences, () => new CapturingExporter(), notices.Add);
        _ = OperationTelemetryFactory.Create(TelemetryHost.Cli, "1.2.3", preferences, () => new CapturingExporter(), notices.Add);

        var notice = Assert.Single(notices);
        Assert.Contains("POINTFRAME_TELEMETRY_OPTOUT", notice, StringComparison.Ordinal);
        Assert.Contains("DO_NOT_TRACK", notice, StringComparison.Ordinal);
        Assert.Contains(TelemetryPreferences.ConfigFileName, notice, StringComparison.Ordinal);
        Assert.Contains("IP", notice, StringComparison.Ordinal);
        Assert.Equal("1", File.ReadAllText(preferences.NoticePath));
    }

    [Fact]
    public void Notice_IsNotShownWhenOptedOut()
    {
        var notices = new List<string>();

        _ = OperationTelemetryFactory.Create(
            TelemetryHost.Cli,
            "1.2.3",
            new TelemetryPreferences(_directory, name => name == "DO_NOT_TRACK" ? "1" : null),
            () => new CapturingExporter(),
            notices.Add);

        Assert.Empty(notices);
    }

    [Fact]
    public void Track_WhenTheExporterThrows_DoesNotThrow()
    {
        var sut = Create(TelemetryHost.Mcp, new ThrowingExporter());

        var exception = Record.Exception(() =>
        {
            sut.Track("capture_monitor", TelemetryOutcome.Success, TimeSpan.Zero, "codex");
            sut.Flush();
        });

        Assert.Null(exception);
    }

    [Fact]
    public void Track_WhenExporterCreationThrows_DoesNotThrow()
    {
        var sut = OperationTelemetryFactory.Create(
            TelemetryHost.Mcp,
            "1.2.3",
            new TelemetryPreferences(_directory, _ => null),
            () => throw new InvalidOperationException("no exporter"),
            _ => { });

        var exception = Record.Exception(() =>
        {
            sut.Track("capture_monitor", TelemetryOutcome.Success, TimeSpan.Zero, "codex");
            sut.Flush();
        });

        Assert.Null(exception);
    }

    [Fact]
    public void Track_DoesNotWaitForASlowExporter()
    {
        using var release = new ManualResetEventSlim();
        var sut = Create(TelemetryHost.Mcp, new BlockingExporter(release));

        var started = System.Diagnostics.Stopwatch.StartNew();
        sut.Track("capture_monitor", TelemetryOutcome.Success, TimeSpan.Zero, "codex");
        started.Stop();
        release.Set();

        Assert.True(started.ElapsedMilliseconds < 1000, $"Track took {started.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task RealExporter_WhenOptedOut_SendsNoNetworkRequest()
    {
        using var listener = StartListener(out var endpoint);
        var requestTask = listener.GetContextAsync();

        var sut = OperationTelemetryFactory.Create(
            TelemetryHost.Mcp,
            "1.2.3",
            new TelemetryPreferences(_directory, name => name == "POINTFRAME_TELEMETRY_OPTOUT" ? "1" : null),
            () => OperationTelemetry.CreateAzureExporter(ConnectionString(endpoint)),
            _ => { });
        sut.Track("capture_monitor", TelemetryOutcome.Success, TimeSpan.Zero, "codex");
        sut.Flush();

        var winner = await Task.WhenAny(requestTask, Task.Delay(TimeSpan.FromSeconds(3)));
        Assert.NotSame(requestTask, winner);
    }

    [Fact]
    public async Task RealExporter_Payload_CarriesOnlyTheAllowlistAndNoIdentity()
    {
        using var listener = StartListener(out var endpoint);
        var payloadTask = CaptureRequestPayloadAsync(listener);

        var sut = OperationTelemetryFactory.Create(
            TelemetryHost.Mcp,
            "1.2.3",
            new TelemetryPreferences(_directory, _ => null),
            () => OperationTelemetry.CreateAzureExporter(ConnectionString(endpoint)),
            _ => { });
        sut.Track("capture_monitor", TelemetryOutcome.Success, TimeSpan.FromMilliseconds(1500), "claude-code");
        sut.Flush();

        var payload = await payloadTask.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Contains("agent_operation", payload, StringComparison.Ordinal);
        Assert.Contains("capture_monitor", payload, StringComparison.Ordinal);
        Assert.Contains("Pointframe.Mcp", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("ai.user.id", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("ai.session.id", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("ai.location.ip", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("install_id", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("session_id", payload, StringComparison.Ordinal);

        var forbiddenValues = new[]
        {
            Environment.MachineName,
            Environment.UserName,
            Environment.UserDomainName,
            Environment.CurrentDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            AppContext.BaseDirectory,
        };
        foreach (var forbiddenValue in forbiddenValues.Where(value => !string.IsNullOrWhiteSpace(value) && value.Length > 3))
        {
            var serializedValue = JsonSerializer.Serialize(forbiddenValue)[1..^1];
            Assert.DoesNotContain(serializedValue, payload, StringComparison.OrdinalIgnoreCase);
        }

        // The role instance is the constant host kind, never the machine name.
        Assert.Contains("ai.cloud.roleInstance", payload, StringComparison.Ordinal);
    }

    private IOperationTelemetry Create(TelemetryHost host, BaseExporter<LogRecord> exporter)
    {
        return OperationTelemetryFactory.Create(
            host,
            "1.2.3",
            new TelemetryPreferences(_directory, _ => null),
            () => exporter,
            _ => { });
    }

    private static HttpListener StartListener(out string endpoint)
    {
        using var socket = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        var listener = new HttpListener();
        endpoint = $"http://127.0.0.1:{port}/";
        listener.Prefixes.Add(endpoint);
        listener.Start();
        return listener;
    }

    private static string ConnectionString(string endpoint) =>
        $"InstrumentationKey=00000000-0000-0000-0000-000000000000;IngestionEndpoint={endpoint}";

    private static async Task<string> CaptureRequestPayloadAsync(HttpListener listener)
    {
        var context = await listener.GetContextAsync();
        await using var body = new MemoryStream();
        await context.Request.InputStream.CopyToAsync(body);
        var bytes = body.ToArray();
        if (context.Request.Headers["Content-Encoding"]?.Contains("gzip", StringComparison.OrdinalIgnoreCase) == true)
        {
            await using var compressed = new MemoryStream(bytes);
            await using var decompressor = new GZipStream(compressed, CompressionMode.Decompress);
            await using var decompressed = new MemoryStream();
            await decompressor.CopyToAsync(decompressed);
            bytes = decompressed.ToArray();
        }

        context.Response.StatusCode = (int)HttpStatusCode.OK;
        context.Response.Close();
        return Encoding.UTF8.GetString(bytes);
    }

    internal sealed record CapturedExport(string? EventName, Dictionary<string, object?> Properties);

    internal sealed class CapturingExporter : BaseExporter<LogRecord>
    {
        public List<CapturedExport> Exports { get; } = [];

        public override ExportResult Export(in Batch<LogRecord> batch)
        {
            foreach (var record in batch)
            {
                var properties = new Dictionary<string, object?>();
                string? eventName = null;
                record.ForEachScope(
                    static (scope, state) =>
                    {
                        foreach (var item in scope)
                        {
                            state.Add(item.Key, item.Value);
                        }
                    },
                    properties);
                foreach (var attribute in record.Attributes ?? [])
                {
                    if (attribute.Key == "microsoft.custom_event.name")
                    {
                        eventName = attribute.Value?.ToString();
                    }
                    else if (attribute.Key != "{OriginalFormat}")
                    {
                        properties[attribute.Key] = attribute.Value;
                    }
                }

                lock (Exports)
                {
                    Exports.Add(new CapturedExport(eventName, properties));
                }
            }

            return ExportResult.Success;
        }
    }

    private sealed class ThrowingExporter : BaseExporter<LogRecord>
    {
        public override ExportResult Export(in Batch<LogRecord> batch) => throw new InvalidOperationException("exporter failure");
    }

    private sealed class BlockingExporter(ManualResetEventSlim release) : BaseExporter<LogRecord>
    {
        public override ExportResult Export(in Batch<LogRecord> batch)
        {
            release.Wait(TimeSpan.FromSeconds(5));
            return ExportResult.Success;
        }
    }
}
