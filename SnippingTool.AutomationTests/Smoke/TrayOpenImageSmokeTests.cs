using Pointframe.AutomationTests.Fixtures;
using Pointframe.AutomationTests.Support;
using Xunit;

namespace Pointframe.AutomationTests.Smoke;

public sealed class TrayOpenImageSmokeTests : IClassFixture<DesktopAutomationFixture>
{
    private readonly DesktopAutomationFixture _fixture;

    public TrayOpenImageSmokeTests(DesktopAutomationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    [Trait("Category", "DesktopAutomation")]
    public void TrayEquivalentOpenImageLaunch_OpensForegroundOverlay()
    {
        _fixture.SeedSettings(autoSaveScreenshots: false);

        using var app = AutomationApp.Launch(
            "--automation-open-tray-sample-overlay",
            _fixture.CreateEnvironmentVariables(includeOpenImageSamplePath: true));
        Assert.Equal(AutomationIds.OverlayWindowRoot, app.MainWindowAutomationId);
        Assert.NotNull(app.FindFirstRequiredElement(
            AutomationIds.OverlayWindowCopy,
            AutomationIds.OverlayWindowCompactCopy));
        app.WaitForMainWindowToBeForeground();

        app.ClickFirstButton(
            AutomationIds.OverlayWindowClose,
            AutomationIds.OverlayWindowCompactClose);
        app.WaitForExit();
    }
}
