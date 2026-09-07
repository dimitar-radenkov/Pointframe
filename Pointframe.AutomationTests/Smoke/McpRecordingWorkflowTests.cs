using Pointframe.AutomationTests.Support;
using Xunit;

namespace Pointframe.AutomationTests.Smoke;

public sealed class McpRecordingWorkflowTests
{
    [SkippableFact]
    [Trait("Category", "DesktopAutomation")]
    public void RecordingWorkflowScaffoldRequiresInteractiveGate()
    {
        DesktopGateTestSupport.RequireInteractiveGate();
        Assert.True(File.Exists(Environment.GetEnvironmentVariable("POINTFRAME_EXECUTABLE")));
    }
}
