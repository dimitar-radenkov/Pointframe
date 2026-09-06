using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Moq;
using Pointframe.Automation.Bridge;
using Xunit;

namespace Pointframe.Tests.Automation;

public sealed class NamedPipeAgentBridgeServerTests
{
    [Fact]
    public async Task Server_ValidSecretDispatchesAndInvalidSecretIsRejected()
    {
        var commandService = new Mock<IAgentBridgeCommandService>();
        commandService.Setup(service => service.GetState()).Returns(new AgentBridgeState(
            1, AgentBridgeOperationStatus.Idle, null, null, null, null, null, false));
        var connectionOptions = AgentBridgeConnectionOptions.CreateForTests();
        using var server = new NamedPipeAgentBridgeServer(
            commandService.Object,
            Mock.Of<ILogger<NamedPipeAgentBridgeServer>>(),
            connectionOptions);
        server.Start();

        var validResponse = await SendAsync(server.PipeName, new BridgeRequest(1, "valid", connectionOptions.Secret, AgentBridgeCommands.StateGet));
        var invalidResponse = await SendAsync(server.PipeName, new BridgeRequest(1, "invalid", "wrong", AgentBridgeCommands.StateGet));

        Assert.True(validResponse.Success);
        Assert.Equal(AgentBridgeOperationStatus.Idle, validResponse.State!.Status);
        Assert.False(invalidResponse.Success);
        Assert.Equal("unauthenticated", invalidResponse.Error!.Code);
    }

    private static async Task<BridgeResponse> SendAsync(string pipeName, BridgeRequest request)
    {
        await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync();
        var serializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter() },
        };
        var requestPayload = JsonSerializer.SerializeToUtf8Bytes(request, serializerOptions);
        await client.WriteAsync(BitConverter.GetBytes(requestPayload.Length));
        await client.WriteAsync(requestPayload);
        await client.FlushAsync();

        var lengthBuffer = new byte[sizeof(int)];
        await client.ReadExactlyAsync(lengthBuffer);
        var responsePayload = new byte[BitConverter.ToInt32(lengthBuffer)];
        await client.ReadExactlyAsync(responsePayload);
        return JsonSerializer.Deserialize<BridgeResponse>(responsePayload, serializerOptions)!;
    }
}