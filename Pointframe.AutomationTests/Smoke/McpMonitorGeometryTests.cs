using Pointframe.AutomationTests.Support;
using Xunit;

namespace Pointframe.AutomationTests.Smoke;

public sealed class McpMonitorGeometryTests
{
    [SkippableFact]
    [Trait("Category", "DesktopAutomation")]
    public void GeometryWorkflowScaffoldRequiresInteractiveGate()
    {
        DesktopGateTestSupport.RequireInteractiveGate();
        Assert.NotEmpty(DesktopGeometryMatrix.DeclaredCells);
    }
}
