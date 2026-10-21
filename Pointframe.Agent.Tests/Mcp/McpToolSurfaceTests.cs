using System.ComponentModel;
using System.IO;
using System.IO.Pipelines;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Moq;
using Pointframe.Engine;
using Pointframe.Engine.Automation.Models;
using Pointframe.Engine.Automation.Services;
using Pointframe.Mcp;
using Pointframe.Mcp.Automation;
using Pointframe.Mcp.Configuration;
using Xunit;

namespace Pointframe.Tests.Mcp;

// Covers the MCP tool surface as an MCP client sees it: the descriptions a model selects tools from,
// and the two behaviors that used to make tools awkward or ambiguous to call — a required redaction
// array on start_recording, and observe_app reporting an unknown session as a bare null.
public sealed class McpToolSurfaceTests
{
    [Theory]
    [InlineData(typeof(PointframeMcpTools))]
    [InlineData(typeof(DesktopTestingMcpTools))]
    public void EveryToolMethodHasADescription(Type toolType)
    {
        // MCP clients choose tools from their descriptions; an undescribed tool is effectively invisible.
        var undescribed = ToolMethods(toolType)
            .Where(method => string.IsNullOrWhiteSpace(method.GetCustomAttribute<DescriptionAttribute>()?.Description))
            .Select(method => method.Name)
            .ToArray();

        Assert.True(
            undescribed.Length == 0,
            $"{toolType.Name} has tool methods without a [Description]: {string.Join(", ", undescribed)}");
    }

    [Theory]
    [InlineData(typeof(PointframeMcpTools))]
    [InlineData(typeof(DesktopTestingMcpTools))]
    public void EveryToolParameterHasADescription(Type toolType)
    {
        var undescribed = ToolMethods(toolType)
            .SelectMany(method => method.GetParameters().Select(parameter => (method, parameter)))
            // CancellationToken is supplied by the server, never by the caller, so it carries no schema.
            .Where(pair => pair.parameter.ParameterType != typeof(CancellationToken))
            .Where(pair => string.IsNullOrWhiteSpace(pair.parameter.GetCustomAttribute<DescriptionAttribute>()?.Description))
            .Select(pair => $"{pair.method.Name}.{pair.parameter.Name}")
            .ToArray();

        Assert.True(
            undescribed.Length == 0,
            $"{toolType.Name} has tool parameters without a [Description]: {string.Join(", ", undescribed)}");
    }

    [Fact]
    public async Task StartRecordingAsync_WithoutRedactionRegions_StartsWithNoRedaction()
    {
        // The redaction array used to be a required parameter, forcing every caller to pass an empty
        // array for the common "record everything" case.
        var recordingService = new Mock<IDirectRecordingMcpService>();
        recordingService
            .Setup(service => service.StartRecording(It.IsAny<string>(), It.IsAny<IReadOnlyList<PixelBounds>>(), It.IsAny<int>()))
            .Returns("{\"SchemaVersion\":1,\"Success\":true}");
        var tools = new PointframeMcpTools(new Mock<IDirectCaptureService>().Object, recordingService.Object, new Mock<ICaptureCatalogService>().Object);

        var response = await tools.StartRecordingAsync(@"\\.\DISPLAY1");

        Assert.True(response.Success);
        recordingService.Verify(
            service => service.StartRecording(@"\\.\DISPLAY1", It.Is<IReadOnlyList<PixelBounds>>(regions => regions.Count == 0), 20),
            Times.Once);
    }

    [Fact]
    public async Task ObserveAppAsync_WhenSessionIsUnknown_ReturnsTypedErrorRatherThanNull()
    {
        // Previously this returned null, leaving a caller unable to distinguish a missing session from
        // an observation that simply found nothing. Every sibling tool reports a typed SessionNotFound.
        var sessions = new Mock<IDesktopTestSessionService>();
        sessions
            .Setup(service => service.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DesktopTestSessionSnapshot?)null);
        var tools = CreateDesktopTestingTools(sessions);

        var result = await tools.ObserveAppAsync("session-does-not-exist", []);

        Assert.NotNull(result);
        Assert.True(result.IsError);
        Assert.NotNull(result.StructuredContent);
        var response = result.StructuredContent!.Value.Deserialize<DesktopTestingObservationResponse>(
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(response);
        Assert.Equal("SessionNotFound", response!.Error?.Code);
        Assert.Equal(nameof(DesktopTargetState.Unavailable), response.TargetState);
        Assert.Empty(response.Images);
        Assert.Empty(response.Elements);
        // An unknown session has no pixels to show, so the result must not claim an image block.
        Assert.DoesNotContain(result.Content, block => block is ImageContentBlock);
    }

    private static DesktopTestingMcpTools CreateDesktopTestingTools(Mock<IDesktopTestSessionService> sessions)
    {
        return new DesktopTestingMcpTools(
            new Mock<IDesktopActionCoordinator>().Object,
            sessions.Object,
            new Mock<IDesktopObservationService>().Object,
            new Mock<IWindowsDesktopInputService>().Object,
            new Mock<IWindowsUiAutomationActionProvider>().Object,
            new Mock<IDesktopUiCheckService>().Object,
            new Mock<IDesktopOcrObservationProvider>().Object,
            new Mock<IDesktopTestReportService>().Object,
            new Mock<IDesktopEvidenceRecorder>().Object,
            new DesktopTestingPolicyLoader(),
            new DesktopTestingHostOptions(Enabled: true, WorkerMode: false, PolicyPath: null, WorkerPipeName: null, ParentProcessId: null));
    }

    [Theory]
    [InlineData(typeof(PointframeMcpTools))]
    [InlineData(typeof(DesktopTestingMcpTools))]
    public void NoPublishedSchemaUsesBooleanSubschemas(Type toolType)
    {
        // Claude Code validates the whole tools/list and drops every tool if one schema has a boolean
        // subschema such as "structuredContent": true. That once hid all desktop tools from Claude Code
        // agents, because desktop_observe_app returned CallToolResult with UseStructuredContent set.
        var fullDiscovery = ToolMethods(toolType)
            .Select(method => McpServerTool.Create(method, _ => null!).ProtocolTool)
            .ToArray();
        var defaultDiscovery = McpDiscoveryFilter.Apply(new ListToolsResult { Tools = [.. fullDiscovery] });
        var offenders = defaultDiscovery.Tools!
            .SelectMany(tool => new[] { ("input", tool.Name, (JsonElement?)tool.InputSchema) })
            .Where(schema => schema.Item3 is { } element && ContainsBooleanSubschema(element))
            .Select(schema => $"{schema.Name} {schema.Item1}")
            .ToArray();

        Assert.Empty(offenders);
        Assert.All(defaultDiscovery.Tools, tool => Assert.Null(tool.OutputSchema));
        Assert.DoesNotContain(fullDiscovery, tool => tool.OutputSchema is { } schema && ContainsBooleanSubschema(schema));
    }

    [Theory]
    [InlineData(typeof(PointframeMcpTools))]
    [InlineData(typeof(DesktopTestingMcpTools))]
    public void DefaultDiscoveryOmitsOnlyOutputSchema(Type toolType)
    {
        var fullTools = ToolMethods(toolType)
            .Select(method => McpServerTool.Create(method, _ => null!).ProtocolTool)
            .ToArray();
        var defaultTools = McpDiscoveryFilter.Apply(new ListToolsResult { Tools = [.. fullTools] }).Tools!;

        Assert.Equal(fullTools.Length, defaultTools.Count);
        for (var index = 0; index < fullTools.Length; index++)
        {
            var full = fullTools[index];
            var advertised = defaultTools[index];
            Assert.Equal(full.Name, advertised.Name);
            Assert.Equal(full.Title, advertised.Title);
            Assert.Equal(full.Description, advertised.Description);
            Assert.Equal(full.InputSchema.GetRawText(), advertised.InputSchema.GetRawText());
            Assert.Equal(full.Annotations, advertised.Annotations);
            Assert.Equal(full.Icons, advertised.Icons);
            Assert.Equal(full.Meta, advertised.Meta);
            Assert.Null(advertised.OutputSchema);
        }
    }

    [Theory]
    [InlineData(typeof(PointframeMcpTools))]
    [InlineData(typeof(DesktopTestingMcpTools))]
    public void FullDiscoveryRetainsOutputSchemasAndSchemaValidity(Type toolType)
    {
        var tools = ToolMethods(toolType)
            .Select(method => McpServerTool.Create(method, _ => null!).ProtocolTool)
            .ToArray();
        var outputSchemas = tools.Select(tool => tool.OutputSchema).ToArray();
        var result = new ListToolsResult { Tools = [.. tools] };

        Assert.Equal(
            outputSchemas.Select(schema => schema?.GetRawText()),
            result.Tools!.Select(tool => tool.OutputSchema?.GetRawText()));
        Assert.All(result.Tools, tool => Assert.NotEqual(JsonValueKind.Undefined, tool.InputSchema.ValueKind));
        Assert.All(outputSchemas.Where(schema => schema.HasValue), schema => Assert.NotEqual(JsonValueKind.Undefined, schema!.Value.ValueKind));
        Assert.DoesNotContain(result.Tools, tool => tool.OutputSchema is { } schema && ContainsBooleanSubschema(schema));
    }

    [Fact]
    public async Task DefaultAndFullDiscoveryServers_AdvertiseTheSameToolsExceptOutputSchemas_AndReturnIdenticalResults()
    {
        await using var defaultServer = await DiscoveryServer.StartAsync(omitOutputSchemas: true);
        await using var fullServer = await DiscoveryServer.StartAsync(omitOutputSchemas: false);

        var defaultTools = await defaultServer.Client.ListToolsAsync();
        var fullTools = await fullServer.Client.ListToolsAsync();

        Assert.Equal(fullTools.Count, defaultTools.Count);
        Assert.NotEmpty(fullTools);
        Assert.Contains(fullTools, tool => tool.ProtocolTool.OutputSchema.HasValue);
        for (var index = 0; index < fullTools.Count; index++)
        {
            var full = fullTools[index].ProtocolTool;
            var advertised = defaultTools[index].ProtocolTool;
            Assert.Equal(full.Name, advertised.Name);
            Assert.Equal(full.Title, advertised.Title);
            Assert.Equal(full.Description, advertised.Description);
            Assert.Equal(full.InputSchema.GetRawText(), advertised.InputSchema.GetRawText());
            Assert.Equal(
                JsonSerializer.Serialize(full.Annotations, ModelContextProtocol.McpJsonUtilities.DefaultOptions),
                JsonSerializer.Serialize(advertised.Annotations, ModelContextProtocol.McpJsonUtilities.DefaultOptions));
            Assert.Null(advertised.OutputSchema);
        }

        var arguments = new Dictionary<string, object?> { ["text"] = "hello" };
        var defaultResult = await defaultServer.Client.CallToolAsync("echo_structured", arguments);
        var fullResult = await fullServer.Client.CallToolAsync("echo_structured", arguments);

        Assert.NotNull(defaultResult.StructuredContent);
        Assert.Equal(
            JsonSerializer.Serialize(fullResult.StructuredContent, ModelContextProtocol.McpJsonUtilities.DefaultOptions),
            JsonSerializer.Serialize(defaultResult.StructuredContent, ModelContextProtocol.McpJsonUtilities.DefaultOptions));
        Assert.Equal(
            fullResult.Content.OfType<TextContentBlock>().Select(block => block.Text),
            defaultResult.Content.OfType<TextContentBlock>().Select(block => block.Text));
    }

    public sealed record EchoResult(string Text, int Length);

    [McpServerToolType]
    public sealed class DiscoveryStubTools
    {
        [McpServerTool(Name = "echo_structured", ReadOnly = true, UseStructuredContent = true)]
        [Description("Echoes the text with its length.")]
        public EchoResult Echo([Description("Text to echo.")] string text) => new(text, text.Length);

        [McpServerTool(Name = "ping_plain", ReadOnly = true)]
        [Description("Returns a fixed string.")]
        public string Ping() => "pong";
    }

    private sealed class DiscoveryServer : IAsyncDisposable
    {
        private readonly IHost _host;

        private DiscoveryServer(IHost host, McpClient client)
        {
            _host = host;
            Client = client;
        }

        internal McpClient Client { get; }

        internal static async Task<DiscoveryServer> StartAsync(bool omitOutputSchemas)
        {
            var clientToServer = new Pipe();
            var serverToClient = new Pipe();
            var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
            var server = builder.Services
                .AddMcpServer()
                .WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream());
            if (omitOutputSchemas)
            {
                server.WithRequestFilters(filters => filters.AddListToolsFilter(McpDiscoveryFilter.Create()));
            }

            server.WithTools<DiscoveryStubTools>();
            var host = builder.Build();
            await host.StartAsync();
            var client = await McpClient.CreateAsync(
                new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream()));
            return new DiscoveryServer(host, client);
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await _host.StopAsync();
            _host.Dispose();
        }
    }

    private static bool ContainsBooleanSubschema(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var property in schema.EnumerateObject())
        {
            if (property.Name is "properties" && property.Value.ValueKind == JsonValueKind.Object
                && property.Value.EnumerateObject().Any(child => child.Value.ValueKind is JsonValueKind.True or JsonValueKind.False))
            {
                return true;
            }

            if (property.Name is "items" or "additionalProperties" && property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return property.Name == "items";
            }

            if (property.Value.ValueKind == JsonValueKind.Object && ContainsBooleanSubschema(property.Value))
            {
                return true;
            }

            if (property.Value.ValueKind == JsonValueKind.Array && property.Value.EnumerateArray().Any(ContainsBooleanSubschema))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<MethodInfo> ToolMethods(Type toolType)
    {
        return toolType
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null);
    }
}
