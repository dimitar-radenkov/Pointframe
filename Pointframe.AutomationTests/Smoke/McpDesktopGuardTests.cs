using Pointframe.AutomationTests.Support;
using Xunit;

namespace Pointframe.AutomationTests.Smoke;

public sealed class McpDesktopGuardTests
{
    [SkippableFact]
    [Trait("Category", "DesktopAutomation")]
    public void GuardWorkflowScaffoldRequiresInteractiveGate()
    {
        DesktopGateTestSupport.RequireInteractiveGate();
        Assert.True(File.Exists(Environment.GetEnvironmentVariable("POINTFRAME_MCP_EXECUTABLE")));
    }
}

public sealed class McpCopyWorkflowTests
{
    [SkippableFact]
    [Trait("Category", "DesktopAutomation")]
    public void CopyWorkflowScaffoldRequiresInteractiveGate()
    {
        DesktopGateTestSupport.RequireInteractiveGate();
        Assert.True(File.Exists(Environment.GetEnvironmentVariable("POINTFRAME_EXECUTABLE")));
    }
}
