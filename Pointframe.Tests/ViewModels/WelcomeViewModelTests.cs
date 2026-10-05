using Moq;
using Pointframe.Services;
using Pointframe.ViewModels;
using Xunit;

namespace Pointframe.Tests.ViewModels;

public sealed class WelcomeViewModelTests
{
    private static (WelcomeViewModel ViewModel, UserSettings Settings, Mock<ITelemetryService> Telemetry) Create(bool hookInstalled = true)
    {
        var settings = new UserSettings { RegionCaptureHotkey = 0x2C };
        var settingsService = new Mock<IUserSettingsService>();
        settingsService.SetupGet(service => service.Current).Returns(settings);
        settingsService.Setup(service => service.Update(It.IsAny<Action<UserSettings>>()))
            .Callback<Action<UserSettings>>(mutate => mutate(settings));
        var hotkey = Mock.Of<IGlobalHotkeyService>(service => service.IsHookInstalled == hookInstalled);
        var telemetry = new Mock<ITelemetryService>();
        return (new WelcomeViewModel(settingsService.Object, hotkey, telemetry.Object), settings, telemetry);
    }

    [Fact]
    public void Constructed_TracksShownAndDisplaysConfiguredShortcut()
    {
        var (viewModel, _, telemetry) = Create();

        Assert.Equal("Your capture shortcut is Print Screen.", viewModel.ShortcutGuidance);
        telemetry.Verify(service => service.TrackEvent(TelemetryEvents.OnboardingShown, null), Times.Once);
    }

    [Fact]
    public void CaptureCommand_PersistsShownTracksActionAndRequestsCapture()
    {
        var (viewModel, settings, telemetry) = Create();
        var requested = false;
        viewModel.CaptureRequested += () => requested = true;

        viewModel.CaptureCommand.Execute(null);

        Assert.True(settings.WelcomeShown);
        Assert.True(requested);
        telemetry.Verify(service => service.TrackEvent(TelemetryEvents.OnboardingAction,
            It.Is<IReadOnlyDictionary<string, string>>(properties => properties[TelemetryPropertyKeys.Action] == "capture")), Times.Once);
    }

    [Fact]
    public void DismissCommand_PersistsShownTracksActionAndRequestsClose()
    {
        var (viewModel, settings, telemetry) = Create();
        var closed = false;
        viewModel.CloseRequested += () => closed = true;

        viewModel.DismissCommand.Execute(null);

        Assert.True(settings.WelcomeShown);
        Assert.True(closed);
        telemetry.Verify(service => service.TrackEvent(TelemetryEvents.OnboardingAction,
            It.Is<IReadOnlyDictionary<string, string>>(properties => properties[TelemetryPropertyKeys.Action] == "dismiss")), Times.Once);
    }

    [Fact]
    public void HookUnavailable_ShowsTrayFallbackInsteadOfShortcut()
    {
        var (viewModel, _, _) = Create(hookInstalled: false);

        Assert.False(viewModel.IsShortcutAvailable);
        Assert.Contains("shortcut is unavailable", viewModel.ShortcutGuidance, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("tray menu", viewModel.ShortcutGuidance, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Print Screen", viewModel.ShortcutGuidance, StringComparison.Ordinal);
    }
}
