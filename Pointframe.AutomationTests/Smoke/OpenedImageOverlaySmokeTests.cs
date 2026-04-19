using Pointframe.AutomationTests.Fixtures;
using Pointframe.AutomationTests.Support;
using Xunit;

namespace Pointframe.AutomationTests.Smoke;

public sealed class OpenedImageOverlaySmokeTests : IClassFixture<DesktopAutomationFixture>
{
    private readonly DesktopAutomationFixture _fixture;

    public OpenedImageOverlaySmokeTests(DesktopAutomationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    [Trait("Category", "DesktopAutomation")]
    public void CopyAction_ClosesOpenedImageOverlay()
    {
        _fixture.SeedSettings(autoSaveScreenshots: false);

        using var app = AutomationApp.Launch("--automation-open-sample-overlay", _fixture.CreateEnvironmentVariables());
        Assert.Equal(AutomationIds.OverlayWindowRoot, app.MainWindowAutomationId);

        app.ClickFirstButton(
            AutomationIds.OverlayWindowCopy,
            AutomationIds.OverlayWindowCompactCopy);
        app.WaitForExit();
    }

    [Fact]
    [Trait("Category", "DesktopAutomation")]
    public void CopyAction_WithAutoSaveEnabled_WritesScreenshotToAutomationOutput()
    {
        _fixture.SeedSettings(autoSaveScreenshots: true);

        using (var app = AutomationApp.Launch("--automation-open-sample-overlay", _fixture.CreateEnvironmentVariables()))
        {
            app.ClickFirstButton(
                AutomationIds.OverlayWindowCopy,
                AutomationIds.OverlayWindowCompactCopy);
            app.WaitForExit();
        }

        Assert.Single(Directory.GetFiles(_fixture.ScreenshotOutputPath, "Snip_*.png"));
    }

    [Fact]
    [Trait("Category", "DesktopAutomation")]
    public void PinAction_OpensPinnedScreenshotWindow()
    {
        _fixture.SeedSettings(autoSaveScreenshots: false);

        using var app = AutomationApp.Launch("--automation-open-sample-overlay", _fixture.CreateEnvironmentVariables());
        app.ClickFirstButton(
            AutomationIds.OverlayWindowPin,
            AutomationIds.OverlayWindowCompactPin);

        app.SwitchToTopLevelWindow(AutomationIds.PinnedScreenshotWindowRoot);
        Assert.Equal(AutomationIds.PinnedScreenshotWindowRoot, app.MainWindowAutomationId);
        Assert.NotNull(app.FindRequiredElement(AutomationIds.PinnedScreenshotWindowImage));

        app.CloseMainWindow();
        app.WaitForExit();
    }
}
