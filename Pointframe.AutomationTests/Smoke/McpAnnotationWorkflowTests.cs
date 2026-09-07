using Pointframe.AutomationTests.Support;
using Xunit;

namespace Pointframe.AutomationTests.Smoke;

public sealed class McpAnnotationWorkflowTests
{
    [SkippableFact]
    [Trait("Category", "DesktopAutomation")]
    public void AnnotationWorkflowScaffoldRequiresInteractiveGate()
    {
        DesktopGateTestSupport.RequireInteractiveGate();
        Assert.True(
            File.Exists(Environment.GetEnvironmentVariable("POINTFRAME_MCP_EXECUTABLE")),
            "The configured MCP executable must exist before a real run.");
    }
}
