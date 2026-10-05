using Pointframe.Services;

namespace Pointframe.ViewModels;

internal sealed partial class WelcomeViewModel : ObservableObject
{
    private readonly IUserSettingsService _settings;
    private readonly IGlobalHotkeyService _hotkey;
    private readonly ITelemetryService _telemetry;

    public WelcomeViewModel(
        IUserSettingsService settings,
        IGlobalHotkeyService hotkey,
        ITelemetryService telemetry)
    {
        _settings = settings;
        _hotkey = hotkey;
        _telemetry = telemetry;
        _telemetry.TrackEvent(TelemetryEvents.OnboardingShown);
    }

    public event Action? CloseRequested;
    public event Action? CaptureRequested;

    public bool IsShortcutAvailable => _hotkey.IsHookInstalled;

    public string ShortcutText
    {
        get
        {
            var settings = _settings.Current;
            return new HotkeyBinding(settings.RegionCaptureHotkey, settings.RegionCaptureHotkeyModifiers).DisplayName;
        }
    }

    public string ShortcutGuidance => IsShortcutAvailable
        ? $"Your capture shortcut is {ShortcutText}."
        : "The capture shortcut is unavailable. Start a capture from the Pointframe tray menu instead.";

    [RelayCommand]
    private void Capture()
    {
        MarkShown();
        _telemetry.TrackEvent(TelemetryEvents.OnboardingAction, new Dictionary<string, string>
        {
            [TelemetryPropertyKeys.Action] = "capture",
        });
        CaptureRequested?.Invoke();
    }

    [RelayCommand]
    private void Dismiss()
    {
        MarkShown();
        _telemetry.TrackEvent(TelemetryEvents.OnboardingAction, new Dictionary<string, string>
        {
            [TelemetryPropertyKeys.Action] = "dismiss",
        });
        CloseRequested?.Invoke();
    }

    private void MarkShown() => _settings.Update(settings => settings.WelcomeShown = true);
}
