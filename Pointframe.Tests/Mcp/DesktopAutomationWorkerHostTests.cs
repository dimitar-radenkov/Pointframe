using Pointframe.Mcp.Automation;
using Xunit;

namespace Pointframe.Tests.Mcp;

public sealed class DesktopAutomationWorkerHostTests
{
    [Fact]
    public void Constructor_AcceptsTheEmptyAssemblyPathOfASingleFilePublish()
    {
        // Assembly.Location is empty in the released single-file server; constructing the host used to throw
        // there, which failed every desktop tool call before the tool ran.
        var exception = Record.Exception(() => new DesktopAutomationWorkerHost(@"C:\server\Pointframe.Mcp.exe", string.Empty));

        Assert.Null(exception);
    }
}
