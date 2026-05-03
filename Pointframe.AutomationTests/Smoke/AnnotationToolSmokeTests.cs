using Pointframe.AutomationTests.Fixtures;
using Pointframe.AutomationTests.Support;
using Xunit;

namespace Pointframe.AutomationTests.Smoke;

public sealed class AnnotationToolSmokeTests : IClassFixture<DesktopAutomationFixture>
{
    private readonly DesktopAutomationFixture _fixture;

    public AnnotationToolSmokeTests(DesktopAutomationFixture fixture)
    {
        _fixture = fixture;
    }

    public static TheoryData<string> AllOverlayToolIds => new()
    {
        AutomationIds.OverlayWindowToolArrow,
        AutomationIds.OverlayWindowToolLine,
        AutomationIds.OverlayWindowToolRectangle,
        AutomationIds.OverlayWindowToolCircle,
        AutomationIds.OverlayWindowToolPen,
        AutomationIds.OverlayWindowToolHighlight,
        AutomationIds.OverlayWindowToolText,
        AutomationIds.OverlayWindowToolNumber,
        AutomationIds.OverlayWindowToolBlur,
        AutomationIds.OverlayWindowToolCallout,
        AutomationIds.OverlayWindowToolColorPicker,
        AutomationIds.OverlayWindowToolPixelRuler,
    };

    [Theory]
    [MemberData(nameof(AllOverlayToolIds))]
    [Trait("Category", "DesktopAutomation")]
    public void SelectTool_OverlayRemainsOperational(string toolAutomationId)
    {
        _fixture.SeedSettings(autoSaveScreenshots: false);

        using var app = AutomationApp.Launch("--automation-open-sample-overlay", _fixture.CreateEnvironmentVariables());
        Assert.Equal(AutomationIds.OverlayWindowRoot, app.MainWindowAutomationId);

        app.SelectRadioButton(toolAutomationId);

        Assert.NotNull(app.FindFirstRequiredElement(
            AutomationIds.OverlayWindowCopy,
            AutomationIds.OverlayWindowCompactCopy));

        app.ClickFirstButton(
            AutomationIds.OverlayWindowClose,
            AutomationIds.OverlayWindowCompactClose);
        app.WaitForExit();
    }

    [Theory]
    [MemberData(nameof(AllOverlayToolIds))]
    [Trait("Category", "DesktopAutomation")]
    public void SelectTool_ThenCopy_ClosesOverlay(string toolAutomationId)
    {
        _fixture.SeedSettings(autoSaveScreenshots: false);

        using var app = AutomationApp.Launch("--automation-open-sample-overlay", _fixture.CreateEnvironmentVariables());
        Assert.Equal(AutomationIds.OverlayWindowRoot, app.MainWindowAutomationId);

        app.SelectRadioButton(toolAutomationId);

        app.ClickFirstButton(
            AutomationIds.OverlayWindowCopy,
            AutomationIds.OverlayWindowCompactCopy);
        app.WaitForExit();
    }
}
