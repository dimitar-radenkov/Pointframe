namespace Pointframe.Telemetry;

public enum TelemetryHost
{
    Cli,
    Mcp,
}

public enum TelemetryOutcome
{
    Success,
    Error,
    Cancelled,
    Denied,
}

public interface IOperationTelemetry
{
    void Track(string name, TelemetryOutcome outcome, TimeSpan duration, string? mcpClientName = null);

    void Flush();
}

public sealed class NullOperationTelemetry : IOperationTelemetry
{
    public static NullOperationTelemetry Instance { get; } = new();

    public void Track(string name, TelemetryOutcome outcome, TimeSpan duration, string? mcpClientName = null)
    {
    }

    public void Flush()
    {
    }
}
