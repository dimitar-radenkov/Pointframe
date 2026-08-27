using Pointframe.AutomationTests.Fixtures;
using Pointframe.AutomationTests.Support;
using Xunit;

namespace Pointframe.AutomationTests.Smoke;

public sealed class SettingsSectionNavigationTests : IClassFixture<SettingsAutomationFixture>
{
    private readonly SettingsAutomationFixture _fixture;

    public SettingsSectionNavigationTests(SettingsAutomationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    [Trait("Category", "DesktopAutomation")]
    public void NavigateToSmartRedactionSection_ShowsSmartRedactionContent()
    {
        _fixture.SeedSettings(autoSaveScreenshots: false);

        using var app = AutomationApp.LaunchSettingsWindow(_fixture.SettingsPath);
        Assert.Equal(AutomationIds.SettingsWindowRoot, app.MainWindow.AutomationId);

        app.SelectListItem(AutomationIds.SettingsWindowSectionSmartRedaction);

        Assert.NotNull(app.FindRequiredElement(AutomationIds.SettingsWindowSmartRedactionContent));

        app.ClickCancel();
        app.WaitForExit();
    }
    [Fact]
    [Trait("Category", "DesktopAutomation")]
    public void NavigateToRecordingSection_ShowsRecordingContent()
    {
        _fixture.SeedSettings(autoSaveScreenshots: false);

        using var app = AutomationApp.LaunchSettingsWindow(_fixture.SettingsPath);
        Assert.Equal(AutomationIds.SettingsWindowRoot, app.MainWindow.AutomationId);

        app.SelectListItem(AutomationIds.SettingsWindowSectionRecording);

        Assert.NotNull(app.FindRequiredElement(AutomationIds.SettingsWindowRecordingContent));

        app.ClickCancel();
        app.WaitForExit();
    }

    [Fact]
    [Trait("Category", "DesktopAutomation")]
    public void NavigateToAnnotationSection_ShowsAnnotationContent()
    {
        _fixture.SeedSettings(autoSaveScreenshots: false);

        using var app = AutomationApp.LaunchSettingsWindow(_fixture.SettingsPath);
        Assert.Equal(AutomationIds.SettingsWindowRoot, app.MainWindow.AutomationId);

        app.SelectListItem(AutomationIds.SettingsWindowSectionAnnotation);

        Assert.NotNull(app.FindRequiredElement(AutomationIds.SettingsWindowAnnotationContent));

        app.ClickCancel();
        app.WaitForExit();
    }

    [Fact]
    [Trait("Category", "DesktopAutomation")]
    public void NavigateToAppSection_ShowsAppContent()
    {
        _fixture.SeedSettings(autoSaveScreenshots: false);

        using var app = AutomationApp.LaunchSettingsWindow(_fixture.SettingsPath);
        Assert.Equal(AutomationIds.SettingsWindowRoot, app.MainWindow.AutomationId);

        app.SelectListItem(AutomationIds.SettingsWindowSectionApp);

        Assert.NotNull(app.FindRequiredElement(AutomationIds.SettingsWindowAppContent));

        app.ClickCancel();
        app.WaitForExit();
    }

    [Fact]
    [Trait("Category", "DesktopAutomation")]
    public void NavigateBackToCaptureSection_ShowsCaptureContent()
    {
        _fixture.SeedSettings(autoSaveScreenshots: false);

        using var app = AutomationApp.LaunchSettingsWindow(_fixture.SettingsPath);
        Assert.Equal(AutomationIds.SettingsWindowRoot, app.MainWindow.AutomationId);

        app.SelectListItem(AutomationIds.SettingsWindowSectionRecording);
        app.SelectListItem(AutomationIds.SettingsWindowSectionCapture);

        Assert.NotNull(app.FindRequiredElement(AutomationIds.SettingsWindowCaptureContent));

        app.ClickCancel();
        app.WaitForExit();
    }

    [Fact]
    [Trait("Category", "DesktopAutomation")]
    public void ResetCurrentSection_DoesNotCrash()
    {
        _fixture.SeedSettings(autoSaveScreenshots: false);

        using var app = AutomationApp.LaunchSettingsWindow(_fixture.SettingsPath);
        Assert.Equal(AutomationIds.SettingsWindowRoot, app.MainWindow.AutomationId);

        app.ClickButton(AutomationIds.SettingsWindowResetCurrentSection);

        Assert.NotNull(app.FindRequiredElement(AutomationIds.SettingsWindowSave));

        app.ClickCancel();
        app.WaitForExit();
    }

    [Fact]
    [Trait("Category", "DesktopAutomation")]
    public void RestoreDefaults_DoesNotCrash()
    {
        _fixture.SeedSettings(autoSaveScreenshots: false);

        using var app = AutomationApp.LaunchSettingsWindow(_fixture.SettingsPath);
        Assert.Equal(AutomationIds.SettingsWindowRoot, app.MainWindow.AutomationId);

        app.ClickButton(AutomationIds.SettingsWindowRestoreDefaults);

        Assert.NotNull(app.FindRequiredElement(AutomationIds.SettingsWindowSave));

        app.ClickCancel();
        app.WaitForExit();
    }
}
