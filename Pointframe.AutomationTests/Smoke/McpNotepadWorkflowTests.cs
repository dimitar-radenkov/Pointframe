using Pointframe.AutomationTests.Support;
using Xunit;

namespace Pointframe.AutomationTests.Smoke;

public sealed class McpNotepadWorkflowTests
{
    [SkippableFact]
    [Trait("Category", "DesktopAutomation")]
    public void NotepadWorkflowScaffoldRequiresInteractiveGate()
    {
        DesktopGateTestSupport.RequireInteractiveGate();
        var notepad = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");
        Skip.If(!File.Exists(notepad), "The provisioned Notepad executable is unavailable.");
        Assert.True(File.Exists(notepad));
    }
}
