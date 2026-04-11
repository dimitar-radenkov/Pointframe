using SnippingTool.AutomationTests.Fixtures;
using SnippingTool.AutomationTests.Support;
using Xunit;

namespace SnippingTool.AutomationTests.Smoke;

public sealed class LaunchModeSmokeTests : IClassFixture<DesktopAutomationFixture>
{
    private readonly DesktopAutomationFixture _fixture;

    public LaunchModeSmokeTests(DesktopAutomationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    [Trait("Category", "DesktopAutomation")]
    public void AboutLaunchMode_OpensAboutWindowAndCloses()
    {
        _fixture.SeedSettings(autoSaveScreenshots: false);

        using var app = AutomationApp.Launch("--automation-open-about", _fixture.CreateEnvironmentVariables());
        Assert.Equal(AutomationIds.AboutWindowRoot, app.MainWindowAutomationId);

        app.ClickButton(AutomationIds.AboutWindowClose);
        app.WaitForExit();
    }

    [Fact]
    [Trait("Category", "DesktopAutomation")]
    public void SampleOverlayLaunchMode_OpensOpenedImageOverlayAndCloses()
    {
        _fixture.SeedSettings(autoSaveScreenshots: false);

        using var app = AutomationApp.Launch("--automation-open-sample-overlay", _fixture.CreateEnvironmentVariables());
        Assert.Equal(AutomationIds.OverlayWindowRoot, app.MainWindowAutomationId);
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
    public void SampleRecordingOverlayLaunchMode_OpensRecordingReadyOverlayAndCloses()
    {
        _fixture.SeedSettings(autoSaveScreenshots: false);

        using var app = AutomationApp.Launch("--automation-open-sample-recording-overlay", _fixture.CreateEnvironmentVariables());
        Assert.Equal(AutomationIds.OverlayWindowRoot, app.MainWindowAutomationId);
        Assert.NotNull(app.FindFirstRequiredElement(
            AutomationIds.OverlayWindowRecord,
            AutomationIds.OverlayWindowCompactRecord));

        app.ClickFirstButton(
            AutomationIds.OverlayWindowClose,
            AutomationIds.OverlayWindowCompactClose);
        app.WaitForExit();
    }
}
