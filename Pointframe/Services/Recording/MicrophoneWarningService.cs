namespace Pointframe.Services;

public sealed class MicrophoneWarningService : IMicrophoneWarningService
{
    private const string Message =
        "Microphone recording is enabled, but no compatible microphone device was available. The recording will continue without microphone audio.\n\n" +
        "Turn off microphone recording so this warning stops appearing?\n" +
        "Yes: turn it off (you can turn it on again in Settings).\n" +
        "No: keep it on. This warning will not appear again until Pointframe restarts.";

    private readonly IUserSettingsService _userSettings;
    private readonly IMessageBoxService _messageBox;
    private readonly ITelemetryService _telemetry;
    private readonly object _gate = new();
    private bool _warned;

    public MicrophoneWarningService(
        IUserSettingsService userSettings,
        IMessageBoxService messageBox,
        ITelemetryService telemetry)
    {
        _userSettings = userSettings;
        _messageBox = messageBox;
        _telemetry = telemetry;
    }

    public void ReportIfUnavailable(bool recordingMicrophoneEnabled)
    {
        if (!_userSettings.Current.RecordMicrophone || recordingMicrophoneEnabled)
        {
            return;
        }

        _telemetry.TrackEvent(TelemetryEvents.MicrophoneUnavailable);

        lock (_gate)
        {
            if (_warned)
            {
                return;
            }

            _warned = true;
        }

        if (_messageBox.Confirm(Message, "Microphone unavailable"))
        {
            _userSettings.Update(settings => settings.RecordMicrophone = false);
        }
    }
}
