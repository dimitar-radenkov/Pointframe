using Pointframe.AutomationTests.Support;
using Xunit;
using Xunit.Abstractions;

namespace Pointframe.AutomationTests.Smoke;

/// <summary>
/// Verifies that synthesized text actually reaches a real application's edit control. The fixture
/// reports its own text box contents, so this needs no OCR - which matters because the driver's only
/// other read-back path depends on the Windows OCR engine.
/// </summary>
[Trait("Category", "DesktopAutomation")]
public class McpTextEntryReachesTargetTests(ITestOutputHelper output)
{
    private const string TypedText = "hello from agent";

    [SkippableFact]
    public async Task EnterTextArrivesInTheTargetsEditControl()
    {
        DesktopGateTestSupport.RequireInteractiveGate();
        var fixturePath = Environment.GetEnvironmentVariable(DesktopFixtureHarness.FixtureExecutableVariable);
        Skip.If(string.IsNullOrWhiteSpace(fixturePath), "Set POINTFRAME_FIXTURE_EXECUTABLE for the desktop gate.");
        var mcpPath = Environment.GetEnvironmentVariable("POINTFRAME_MCP_EXECUTABLE")!;
        var artifacts = Path.Combine(Path.GetTempPath(), $"pointframe-text-{Guid.NewGuid():N}");

        await using var harness = await DesktopFixtureHarness.StartAsync(fixturePath!, mcpPath, artifacts, 0);
        var displays = await harness.ListDisplaysAsync();
        var display = displays[0];

        var before = harness.ReadState();
        output.WriteLine($"[before] textBoxText='{before.TextBoxText}' box={before.TextBoxScreen}");

        var observation = await harness.ObserveAsync(display);
        var image = observation.Images[0];

        // Aim at the middle of the text box, converted from desktop pixels into the preview's space.
        var centreX = before.TextBoxScreen.X + (before.TextBoxScreen.Width / 2);
        var centreY = before.TextBoxScreen.Y + (before.TextBoxScreen.Height / 2);
        var imageX = (int)Math.Round((centreX - image.DesktopX) * (double)image.Width / image.DesktopWidth);
        var imageY = (int)Math.Round((centreY - image.DesktopY) * (double)image.Height / image.DesktopHeight);
        output.WriteLine($"[target] desktop ({centreX},{centreY}) -> preview ({imageX},{imageY})");

        var result = await harness.EnterTextAsync(observation, imageX, imageY, TypedText);
        output.WriteLine($"[enter_text] {result.GetProperty("structuredContent").GetRawText()}");

        var after = harness.WaitForState(
            state => state.TextBoxText.Contains(TypedText, StringComparison.Ordinal),
            TimeSpan.FromSeconds(5));
        output.WriteLine($"[after] textBoxText='{after.TextBoxText}'");

        Assert.Equal(TypedText, after.TextBoxText);

        // Defect 2: the driver must be able to confirm this itself, rather than the test reading the
        // fixture's private state file. This is the first verification path the agent actually has.
        var check = await harness.CheckUiAsync("textEquals", automationId: "textBox", expected: TypedText);
        var checkJson = check.GetProperty("structuredContent");
        output.WriteLine($"[check_ui textEquals] {checkJson.GetRawText()}");
        Assert.Equal("passed", checkJson.GetProperty("verification").GetString());

        var absent = await harness.CheckUiAsync("textEquals", automationId: "textBox", expected: "something else");
        var absentJson = absent.GetProperty("structuredContent");
        output.WriteLine($"[check_ui negative] {absentJson.GetRawText()}");
        Assert.Equal("failed", absentJson.GetProperty("verification").GetString());
    }
}
