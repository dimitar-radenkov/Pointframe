namespace Pointframe.Telemetry;

// Local hints between Pointframe's own processes. They never become telemetry properties.
public static class TelemetryHints
{
    // Set by the verification runner in the request metadata of each poll that is not the last attempt of a
    // restart wait. The MCP server then leaves a TargetStillRunning refusal of that poll uncounted, so one
    // logical restart emits one terminal operation.
    public const string RestartPollMetaKey = "pointframe/restartPoll";
}
