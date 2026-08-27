using Pointframe.AutomationTests.Fixtures;
using Pointframe.AutomationTests.Support;
using Xunit;

namespace Pointframe.AutomationTests.Smoke;

public sealed class SmartRedactionSmokeTests : IClassFixture<DesktopAutomationFixture>
{
    private readonly DesktopAutomationFixture _fixture;

    public SmartRedactionSmokeTests(DesktopAutomationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    [Trait("Category", "DesktopAutomation")]
    public void SmartRedactAction_WhenEnabled_IsAvailableAndOverlayRemainsOperational()
    {
        _fixture.SeedSettings(autoSaveScreenshots: false, smartRedactionEnabled: true);

        using var app = AutomationApp.Launch("--automation-open-sample-overlay", _fixture.CreateEnvironmentVariables());
        Assert.Equal(AutomationIds.OverlayWindowRoot, app.MainWindowAutomationId);
        Assert.True(app.IsFirstButtonEnabled(
            AutomationIds.OverlayWindowSmartRedact,
            AutomationIds.OverlayWindowCompactSmartRedact));

        app.ClickFirstButton(
            AutomationIds.OverlayWindowSmartRedact,
            AutomationIds.OverlayWindowCompactSmartRedact);

        Assert.NotNull(app.FindFirstRequiredElement(
            AutomationIds.OverlayWindowCopy,
            AutomationIds.OverlayWindowCompactCopy));

        app.ClickFirstButton(
            AutomationIds.OverlayWindowClose,
            AutomationIds.OverlayWindowCompactClose);
        app.WaitForExit();
    }

    [Fact]
    [Trait("Category", "DesktopAutomation")]
    public void SmartRedactAction_WhenDisabled_IsNotEnabled()
    {
        _fixture.SeedSettings(autoSaveScreenshots: false, smartRedactionEnabled: false);

        using var app = AutomationApp.Launch("--automation-open-sample-overlay", _fixture.CreateEnvironmentVariables());
        Assert.Equal(AutomationIds.OverlayWindowRoot, app.MainWindowAutomationId);
        Assert.False(app.IsFirstButtonEnabled(
            AutomationIds.OverlayWindowSmartRedact,
            AutomationIds.OverlayWindowCompactSmartRedact));

        app.ClickFirstButton(
            AutomationIds.OverlayWindowClose,
            AutomationIds.OverlayWindowCompactClose);
        app.WaitForExit();
    }
}
