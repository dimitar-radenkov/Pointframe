namespace Pointframe.Services;

// Tracks every camera failure but tells the user once per app run, through a tray balloon, so a recording is never
// interrupted. Never closes the recording overlay (unlike the recording failure toast).
internal sealed class CameraNoticeService : ICameraNoticeService
{
    internal const string Title = "Camera unavailable";
    internal const string DefaultMessage = "Pointframe could not show your camera, so this recording has no camera bubble. The video was kept.";
    internal const string AccessDeniedMessage = "Windows is not letting Pointframe use your camera, so this recording has no camera bubble. Turn on Settings > Privacy & security > Camera > Let desktop apps access your camera. The video was kept.";
    internal const string BusyMessage = "Your camera is in use by another app, so this recording has no camera bubble. The video was kept.";
    internal const string DeviceMissingMessage = "The camera chosen in Settings is not connected, so this recording has no camera bubble. The video was kept.";

    private readonly ITrayIconManager _trayIcon;
    private readonly ITelemetryService _telemetry;
    private readonly object _gate = new();
    private bool _notified;

    public CameraNoticeService(ITrayIconManager trayIcon, ITelemetryService telemetry)
    {
        _trayIcon = trayIcon;
        _telemetry = telemetry;
    }

    internal Action<Action> RunOnUiThread { get; set; } = action =>
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        dispatcher.BeginInvoke(action);
    };

    internal static string GetMessage(string reason)
    {
        return reason switch
        {
            CameraFailureReasons.AccessDenied => AccessDeniedMessage,
            CameraFailureReasons.Busy => BusyMessage,
            CameraFailureReasons.DeviceMissing => DeviceMissingMessage,
            _ => DefaultMessage,
        };
    }

    public void ReportUnavailable(string reason, string phase)
    {
        var normalizedReason = CameraFailureReasons.Normalize(reason);
        _telemetry.TrackEvent(TelemetryEvents.CameraUnavailable, new Dictionary<string, string>
        {
            [TelemetryPropertyKeys.Reason] = normalizedReason,
            [TelemetryPropertyKeys.Phase] = phase == CameraFailureReasons.PhaseCapture ? CameraFailureReasons.PhaseCapture : CameraFailureReasons.PhaseStart,
        });

        lock (_gate)
        {
            if (_notified)
            {
                return;
            }

            _notified = true;
        }

        var message = GetMessage(normalizedReason);
        RunOnUiThread(() => _trayIcon.ShowTranscriptBalloon(Title, message, isError: true));
    }
}
