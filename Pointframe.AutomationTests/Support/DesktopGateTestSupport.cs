using Xunit;

namespace Pointframe.AutomationTests.Support;

internal static class DesktopGateTestSupport
{
    public static void RequireInteractiveGate()
    {
        Skip.If(
            !string.Equals(
                Environment.GetEnvironmentVariable("POINTFRAME_DESKTOP_TEST_ACK"),
                "true",
                StringComparison.OrdinalIgnoreCase),
            "Interactive desktop gate is not enabled; explicitly confirm that this account or machine is safe for automation.");
        Skip.If(
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("POINTFRAME_MCP_EXECUTABLE")),
            "Set POINTFRAME_MCP_EXECUTABLE for the interactive desktop gate.");
        Skip.If(
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("POINTFRAME_EXECUTABLE")),
            "Set POINTFRAME_EXECUTABLE for the interactive desktop gate.");
    }
}
