using System.Diagnostics;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using Pointframe.AutomationTests.Fixtures;
using Pointframe.AutomationTests.Support;
using Xunit;

namespace Pointframe.AutomationTests.Smoke;

public sealed class WelcomeWindowSmokeTests : IClassFixture<DesktopAutomationFixture>
{
    private readonly DesktopAutomationFixture _fixture;

    public WelcomeWindowSmokeTests(DesktopAutomationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    [Trait("Category", "DesktopAutomation")]
    public void FreshInstall_WelcomeStartsCaptureAndDismissalPersistsOnce()
    {
        DesktopGateTestSupport.RequireInteractiveGate();
        _fixture.SeedWelcomeSettings(welcomeShown: false);
        var environment = _fixture.CreateEnvironmentVariables();

        using (var app = AutomationApp.LaunchNormally(environment))
        {
            Assert.Equal(AutomationIds.WelcomeWindowRoot, app.MainWindowAutomationId);
            Assert.NotNull(app.FindRequiredElement(AutomationIds.WelcomeWindowCapture));
            app.ClickButton(AutomationIds.WelcomeWindowCapture);
            app.WaitForWindowTitle("SelectionMonitorWindow");
            Assert.False(app.HasWindowAutomationId(AutomationIds.WelcomeWindowRoot));
            Keyboard.Press(VirtualKeyShort.ESC);
            app.WaitForWindowTitleToClose("SelectionMonitorWindow");
        }

        var afterCancellation = _fixture.ReadSettings();
        Assert.True(afterCancellation.WelcomeShown);
        Assert.False(afterCancellation.FirstCaptureCompletedTracked);

        _fixture.SeedWelcomeSettings(welcomeShown: true);
        using var process = AutomationApp.StartExecutableWithoutWaiting(
            AutomationApp.ResolveAutomationExecutablePath(),
            _fixture.CreateEnvironmentVariables());
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline && !process.HasExited)
            {
                Thread.Sleep(200);
            }

            Assert.False(process.HasExited);
            using var desktopAutomation = new FlaUI.UIA3.UIA3Automation();
            var processWindows = desktopAutomation.GetDesktop()
                .FindAllChildren(criteria => criteria.ByProcessId(process.Id));
            Assert.DoesNotContain(processWindows, window => window.AutomationId == AutomationIds.WelcomeWindowRoot);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
            }
        }
    }
}
