using Pointframe.AutomationTests.Support;
using Xunit;

namespace Pointframe.AutomationTests.Smoke;

public sealed class McpPinWorkflowTests
{
    [SkippableFact]
    [Trait("Category", "DesktopAutomation")]
    public void PinWorkflowScaffoldRequiresInteractiveGate()
    {
        DesktopGateTestSupport.RequireInteractiveGate();
        Assert.True(File.Exists(Environment.GetEnvironmentVariable("POINTFRAME_MCP_EXECUTABLE")));
    }
}
