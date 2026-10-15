using System.IO;
using System.IO.Pipelines;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Pointframe.Cli;
using Pointframe.Mcp;
using Pointframe.Tests.Cli;
using Pointframe.Telemetry;
using Xunit;

namespace Pointframe.Tests.Mcp;

public sealed class RestartWaitTelemetryTests : IDisposable
{
    private readonly VerificationFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task Runner_RestartThatSucceedsAfterPolls_EmitsOneSuccessForTheWholeWait()
    {
        await using var server = await RestartServer.StartAsync(refusals: 2);
        var client = await ClientAsync(server);

        var result = await Runner(client).RunAsync(RestartScenario(), CancellationToken.None);

        Assert.Equal(VerificationStatus.Pass, result.Steps[1].Status);
        Assert.Equal(3, server.Script.Calls);
        var tracked = Assert.Single(server.Telemetry.Tracked);
        Assert.Equal(("desktop_restart_app", TelemetryOutcome.Success), (tracked.Name, tracked.Outcome));
        Assert.Single(server.Telemetry.Tracked);
    }

    [Fact]
    public async Task Runner_RestartThatTimesOut_EmitsOneErrorForTheWholeWait()
    {
        await using var server = await RestartServer.StartAsync(refusals: int.MaxValue);
        var client = await ClientAsync(server);
        var runner = new DesktopScenarioRunner(
            client.Mock.Object, "fixture", [new CaptureRectangle(0, 0, 1920, 1080)], TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(150));

        var result = await runner.RunAsync(RestartScenario(), CancellationToken.None);

        var restart = result.Steps.Single(step => step.Kind == "restart");
        Assert.Equal(VerificationStatus.Fail, restart.Status);
        Assert.Equal("TargetStillRunning", restart.Code);
        Assert.True(server.Script.Calls > 2);
        var tracked = Assert.Single(server.Telemetry.Tracked);
        Assert.Equal(("desktop_restart_app", TelemetryOutcome.Error), (tracked.Name, tracked.Outcome));
        Assert.Single(server.Telemetry.Tracked);
    }

    [Fact]
    public async Task Runner_RestartCancelledBetweenPolls_EmitsNoRestartEvent()
    {
        using var cancellation = new CancellationTokenSource();
        await using var server = await RestartServer.StartAsync(refusals: int.MaxValue);
        var client = await ClientAsync(server, calls =>
        {
            if (calls == 2)
            {
                cancellation.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Runner(client).RunAsync(RestartScenario(), cancellation.Token));

        Assert.Empty(server.Telemetry.Tracked);
    }

    [Fact]
    public void ClientMetadataRelocation_MovesOnlyTheMetaEntryOutOfTheArguments()
    {
        var wire = JsonSerializer.SerializeToNode(McpStdioToolClient.BuildCallParameters("desktop_restart_app", RestartArguments(poll: true)))!.AsObject();

        Assert.True(wire["_meta"]![TelemetryHints.RestartPollMetaKey]!.GetValue<bool>());
        Assert.Null(wire["arguments"]!["_meta"]);
        Assert.NotNull(wire["arguments"]!["sessionId"]);
        var unmarked = JsonSerializer.SerializeToNode(McpStdioToolClient.BuildCallParameters("desktop_restart_app", RestartArguments(poll: false)))!.AsObject();
        Assert.Null(unmarked["_meta"]);
    }

    [Fact]
    public async Task StandaloneRestartRefusal_IsStillTracked()
    {
        await using var server = await RestartServer.StartAsync(refusals: int.MaxValue);

        await server.CallAsync("desktop_restart_app", RestartArguments(poll: false));

        var tracked = Assert.Single(server.Telemetry.Tracked);
        Assert.Equal(("desktop_restart_app", TelemetryOutcome.Error), (tracked.Name, tracked.Outcome));
    }

    [Fact]
    public async Task DirectRestartCallsByAnAgent_AreCountedIndividually()
    {
        await using var server = await RestartServer.StartAsync(refusals: 1);

        await server.CallAsync("desktop_restart_app", RestartArguments(poll: false));
        await server.CallAsync("desktop_restart_app", RestartArguments(poll: false));
        await server.CallAsync("desktop_restart_app", RestartArguments(poll: false));

        Assert.Equal(
            [TelemetryOutcome.Error, TelemetryOutcome.Success, TelemetryOutcome.Success],
            server.Telemetry.Tracked.Select(item => item.Outcome).ToArray());
    }

    [Fact]
    public async Task PollThatIsNotRefused_IsCountedLikeAnyOtherCall()
    {
        await using var server = await RestartServer.StartAsync(refusals: 0);

        await server.CallAsync("desktop_restart_app", RestartArguments(poll: true));

        var tracked = Assert.Single(server.Telemetry.Tracked);
        Assert.Equal(TelemetryOutcome.Success, tracked.Outcome);
    }

    [Theory]
    [InlineData("InvalidCriteria")]
    [InlineData("InvalidDetail")]
    [InlineData("InvalidImageOptions")]
    [InlineData("InvalidScenarioId")]
    [InlineData("ElementRequired")]
    [InlineData("invalid_query")]
    [InlineData("invalid_cursor")]
    [InlineData("invalid_frames_per_second")]
    public void Classify_TypedValidationErrorCodes_AreInvalidRequests(string code)
    {
        var object_ = new CallToolResult { StructuredContent = JsonDocument.Parse($"{{\"error\":{{\"code\":\"{code}\",\"message\":\"x\"}}}}").RootElement.Clone() };
        var text = new CallToolResult { StructuredContent = JsonDocument.Parse($"{{\"success\":false,\"error\":\"{code}\"}}").RootElement.Clone() };
        var flagged = new CallToolResult
        {
            IsError = true,
            StructuredContent = JsonDocument.Parse($"{{\"error\":{{\"code\":\"{code}\"}}}}").RootElement.Clone(),
        };

        Assert.Equal(TelemetryOutcome.InvalidRequest, McpTelemetryFilter.Classify(object_));
        Assert.Equal(TelemetryOutcome.InvalidRequest, McpTelemetryFilter.Classify(text));
        Assert.Equal(TelemetryOutcome.InvalidRequest, McpTelemetryFilter.Classify(flagged));
    }

    [Fact]
    public void Classify_ErrorTextThatIsExactlyATypedValidationCode_IsAnInvalidRequest()
    {
        var result = new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = "invalid_text_range" }] };
        var other = new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = "Something went wrong: invalid_text_range" }] };

        Assert.Equal(TelemetryOutcome.InvalidRequest, McpTelemetryFilter.Classify(result));
        Assert.Equal(TelemetryOutcome.Error, McpTelemetryFilter.Classify(other));
    }

    [Theory]
    [InlineData("SessionNotFound")]
    [InlineData("TargetStillRunning")]
    [InlineData("FocusRequired")]
    [InlineData("InvalidPayload")]
    [InlineData("CaptureFailed")]
    [InlineData("invalid_capture_bounds")]
    public void Classify_EveryOtherFailureCode_StaysAnError(string code)
    {
        var result = new CallToolResult { StructuredContent = JsonDocument.Parse($"{{\"error\":{{\"code\":\"{code}\",\"message\":\"x\"}}}}").RootElement.Clone() };

        Assert.Equal(TelemetryOutcome.Error, McpTelemetryFilter.Classify(result));
    }

    [Fact]
    public void Classify_ValidationCodeOnASuccessfulResult_IsStillSuccess()
    {
        var result = new CallToolResult { StructuredContent = JsonDocument.Parse("{\"success\":true,\"note\":\"InvalidCriteria\"}").RootElement.Clone() };

        Assert.Equal(TelemetryOutcome.Success, McpTelemetryFilter.Classify(result));
    }

    private static object RestartArguments(bool poll) => new
    {
        sessionId = "session-1",
        actionId = Guid.NewGuid().ToString(),
        _meta = poll ? new Dictionary<string, bool> { [TelemetryHints.RestartPollMetaKey] = true } : null,
    };

    private async Task<FakeMcp> ClientAsync(RestartServer server, Action<int>? afterCall = null)
    {
        var client = new FakeMcp(await _fixture.WriteSealedBundleAsync(criterionPassed: true));
        client.Mock
            .Setup(item => item.CallToolAsync("desktop_restart_app", It.IsAny<object>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Returns<string, object, TimeSpan, CancellationToken>(async (name, arguments, _, cancellationToken) =>
            {
                var response = await server.CallAsync(name, arguments, cancellationToken);
                afterCall?.Invoke(server.Script.Calls);
                return response;
            });
        return client;
    }

    private static DesktopScenarioRunner Runner(FakeMcp client) =>
        new(client.Mock.Object, "fixture", [new CaptureRectangle(0, 0, 1920, 1080)], TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(10));

    private static VerificationScenario RestartScenario() => VerificationSpecLoader.ParseScenario(
        """{ "id": "r", "steps": [ { "invoke": { "automationId": "saveButton" } }, { "restart": {} }, { "check": { "kind": "exists", "automationId": "textBox" } } ] }""",
        "r");

    public sealed class RestartScript(int refusals)
    {
        private int _calls;

        public int Calls => _calls;

        public CallToolResult Next()
        {
            var calls = Interlocked.Increment(ref _calls);
            var json = calls <= refusals
                ? "{\"operationStatus\":\"Completed\",\"dispatch\":\"NotStarted\",\"error\":{\"code\":\"TargetStillRunning\",\"message\":\"still running\"}}"
                : "{\"operationStatus\":\"Completed\",\"dispatch\":\"Complete\"}";
            return new CallToolResult { StructuredContent = JsonDocument.Parse(json).RootElement.Clone() };
        }
    }

    [McpServerToolType]
    public sealed class StubTools(RestartScript script)
    {
        [McpServerTool(Name = "desktop_restart_app", ReadOnly = false, Destructive = false)]
        public CallToolResult Restart(string sessionId, string actionId) => script.Next();

        [McpServerTool(Name = "desktop_end_test_session", ReadOnly = false, Destructive = false)]
        public CallToolResult End(string sessionId, string actionId) =>
            new() { StructuredContent = JsonDocument.Parse("{\"operationStatus\":\"Completed\",\"dispatch\":\"Complete\"}").RootElement.Clone() };
    }


    private sealed class RecordingTelemetry : IOperationTelemetry
    {
        private readonly object _gate = new();
        private readonly List<(string Name, TelemetryOutcome Outcome, string? Client)> _tracked = [];

        public IReadOnlyList<(string Name, TelemetryOutcome Outcome, string? Client)> Tracked
        {
            get
            {
                lock (_gate)
                {
                    return [.. _tracked];
                }
            }
        }

        public void Track(string name, TelemetryOutcome outcome, TimeSpan duration, string? mcpClientName = null)
        {
            lock (_gate)
            {
                _tracked.Add((name, outcome, mcpClientName));
            }
        }

        public void Flush()
        {
        }
    }

    private sealed class RestartServer : IAsyncDisposable
    {
        private readonly IHost _host;
        private readonly McpClient _client;

        private RestartServer(IHost host, McpClient client, RestartScript script, RecordingTelemetry telemetry)
        {
            _host = host;
            _client = client;
            Script = script;
            Telemetry = telemetry;
        }

        internal RestartScript Script { get; }

        internal RecordingTelemetry Telemetry { get; }

        internal static async Task<RestartServer> StartAsync(int refusals)
        {
            var script = new RestartScript(refusals);
            var telemetry = new RecordingTelemetry();
            var clientToServer = new Pipe();
            var serverToClient = new Pipe();
            var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
            builder.Services.AddSingleton(script);
            builder.Services
                .AddMcpServer()
                .WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream())
                .WithRequestFilters(filters => filters.AddCallToolFilter(McpTelemetryFilter.Create(telemetry)))
                .WithTools<StubTools>();
            var host = builder.Build();
            await host.StartAsync();
            var client = await McpClient.CreateAsync(
                new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream()),
                new McpClientOptions { ClientInfo = new Implementation { Name = "Pointframe CLI verify", Version = "1.0" } });
            return new RestartServer(host, client, script, telemetry);
        }

        internal Task<JsonElement> CallAsync(string name, object arguments) => CallAsync(name, arguments, CancellationToken.None);

        internal async Task<JsonElement> CallAsync(string name, object arguments, CancellationToken cancellationToken)
        {
            // The production client decides what is an argument and what is request metadata.
            var wire = JsonSerializer.SerializeToNode(McpStdioToolClient.BuildCallParameters(name, arguments))!.AsObject();
            var node = wire["arguments"]!.AsObject();
            var meta = wire["_meta"] as JsonObject;

            var result = await _client.CallToolAsync(
                new CallToolRequestParams
                {
                    Name = name,
                    Arguments = node.ToDictionary(item => item.Key, item => JsonSerializer.SerializeToElement(item.Value)),
                    Meta = meta,
                },
                cancellationToken);
            return JsonSerializer.SerializeToElement(result, ModelContextProtocol.McpJsonUtilities.DefaultOptions);
        }

        public async ValueTask DisposeAsync()
        {
            await _client.DisposeAsync();
            await _host.StopAsync();
            _host.Dispose();
        }
    }
}
