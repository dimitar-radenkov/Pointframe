namespace Pointframe.Services;

// Owns the mute state of the active recording microphone for one session.
// The initial mute state is captured at creation so the recording-time toggle
// never turns into a persistent system-wide mute (see lessons.md).
internal sealed class RecordingMicrophoneSession
{
    private readonly IMicrophoneDeviceService _microphoneDeviceService;
    private readonly ILogger _logger;
    private readonly string? _deviceName;
    private readonly bool? _initialMutedState;

    public RecordingMicrophoneSession(
        IMicrophoneDeviceService microphoneDeviceService,
        ILogger logger,
        string? deviceName)
    {
        _microphoneDeviceService = microphoneDeviceService;
        _logger = logger;
        _deviceName = deviceName;
        _initialMutedState = deviceName is null
            ? null
            : microphoneDeviceService.TryGetCaptureDeviceMuted(deviceName);
    }

    public bool IsEnabled => _deviceName is not null;

    public bool CanToggleMute => _initialMutedState.HasValue;

    public bool InitialMutedState => _initialMutedState ?? false;

    public bool TrySetMuted(bool isMuted)
    {
        if (string.IsNullOrWhiteSpace(_deviceName))
        {
            return false;
        }

        if (!_microphoneDeviceService.TrySetCaptureDeviceMuted(_deviceName, isMuted))
        {
            _logger.LogWarning("Failed to set microphone mute state to {IsMuted} for active recording device '{DeviceName}'", isMuted, _deviceName);
            return false;
        }

        return true;
    }

    public void RestoreInitialMuteState()
    {
        if (string.IsNullOrWhiteSpace(_deviceName) || !_initialMutedState.HasValue)
        {
            return;
        }

        if (!_microphoneDeviceService.TrySetCaptureDeviceMuted(_deviceName, _initialMutedState.Value))
        {
            _logger.LogWarning("Failed to restore microphone mute state for recording device '{DeviceName}'", _deviceName);
        }
    }
}
