using System.Security.Cryptography;

namespace Pointframe.Automation.Bridge;

internal sealed record AgentBridgeConnectionOptions(string PipeName, string Secret)
{
    private const string PipeNameEnvironmentVariable = "POINTFRAME_AGENT_BRIDGE_PIPE";
    private const string SecretEnvironmentVariable = "POINTFRAME_AGENT_BRIDGE_SECRET";

    public static AgentBridgeConnectionOptions FromEnvironment()
    {
        var pipeName = Environment.GetEnvironmentVariable(PipeNameEnvironmentVariable);
        var secret = Environment.GetEnvironmentVariable(SecretEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(pipeName) || string.IsNullOrWhiteSpace(secret))
        {
            throw new InvalidOperationException(
                $"{PipeNameEnvironmentVariable} and {SecretEnvironmentVariable} must be set when --agent-bridge is used.");
        }

        return new AgentBridgeConnectionOptions(pipeName, secret);
    }

    internal static AgentBridgeConnectionOptions CreateForTests()
    {
        return new AgentBridgeConnectionOptions(
            $"pointframe-agent-{Guid.NewGuid():N}",
            Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
    }
}
