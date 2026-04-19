using System.Text.Json;
using Pointframe.AutomationTests.Fixtures;
using Pointframe.AutomationTests.Support;
using Pointframe.Models;
using Xunit;

namespace Pointframe.AutomationTests.Smoke;

public sealed class SettingsWindowSmokeTests : IClassFixture<SettingsAutomationFixture>
{
    private readonly SettingsAutomationFixture _fixture;

    public SettingsWindowSmokeTests(SettingsAutomationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    [Trait("Category", "DesktopAutomation")]
    public void Save_Relaunch_PersistsAutoSaveScreenshots()
    {
        // Manual run:
        // dotnet test Pointframe.AutomationTests\Pointframe.AutomationTests.csproj --filter "Category=DesktopAutomation"
        _fixture.SeedSettings(autoSaveScreenshots: false);

        using (var app = AutomationApp.LaunchSettingsWindow(_fixture.SettingsPath))
        {
            Assert.Equal(AutomationIds.SettingsWindowRoot, app.MainWindow.AutomationId);
            Assert.False(app.IsAutoSaveScreenshotsChecked());

            app.ToggleAutoSaveScreenshots();
            app.ClickSave();
            app.WaitForExit();
        }

        Assert.True(File.Exists(_fixture.SettingsPath));
        var persisted = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(_fixture.SettingsPath));
        Assert.NotNull(persisted);
        Assert.True(persisted!.AutoSaveScreenshots);

        using (var app = AutomationApp.LaunchSettingsWindow(_fixture.SettingsPath))
        {
            Assert.True(app.IsAutoSaveScreenshotsChecked());
            app.ClickCancel();
            app.WaitForExit();
        }
    }
}
